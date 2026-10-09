using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.EntityFrameworkCore;
using QuickNotes.App.Services;

namespace QuickNotes.App.Data;

/// <summary>
/// Per-call startup diagnostics from <see cref="DbInitializer.Initialize"/>. Not process-global state.
/// </summary>
public readonly struct DbInitializeOutcome
{
    public bool SkippedIntegrityCheck { get; init; }
    public bool SkippedEnsureCreated { get; init; }
    public bool SkippedSchemaUpgradePass { get; init; }
    public bool SkippedFtsRebuild { get; init; }
}

public static class DbInitializer
{
    public const int CurrentSchemaVersion = 14;
    public static readonly List<string> LastMigrationDiagnostics = new();

    public static bool TableExists(System.Data.Common.DbConnection connection, string tableName)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='{tableName}';";
        var res = cmd.ExecuteScalar();
        return res != null && Convert.ToInt64(res) > 0;
    }

    public static bool ColumnExists(System.Data.Common.DbConnection connection, string tableName, string columnName)
    {
        if (!TableExists(connection, tableName))
            return false;

        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info('{tableName}');";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    public static bool HasUniqueIndex(System.Data.Common.DbConnection connection, string tableName, string indexName)
    {
        if (!TableExists(connection, tableName))
            return false;

        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT [unique] FROM pragma_index_list('{tableName}') WHERE name='{indexName}';";
        var res = cmd.ExecuteScalar();
        return res != null && res != DBNull.Value && Convert.ToInt64(res) == 1;
    }

    public static bool HasUniqueTitleIndex(System.Data.Common.DbConnection connection)
    {
        return HasUniqueIndex(connection, "NoteTemplates", "IX_NoteTemplates_Title");
    }

    /// <summary>
    /// Reuse a just-completed recovery <c>PRAGMA integrity_check</c> only when that probe ran on the
    /// same existing non-empty database file and reported it healthy. Missing files, empty files,
    /// and any corrupt/recovery path must run <see cref="Initialize"/>'s own integrity check.
    /// </summary>
    public static bool CanReusePriorIntegrityCheck(bool databaseFileExistedAndNonEmpty, bool probeReportedCorrupt)
        => databaseFileExistedAndNonEmpty && !probeReportedCorrupt;

    public static DbInitializeOutcome Initialize(
        QuickNotesDbContext context,
        Action? backupBeforeUpgrade = null,
        bool integrityAlreadyVerified = false)
    {
        bool skippedIntegrityCheck = false;
        bool skippedEnsureCreated = false;
        bool skippedSchemaUpgradePass = false;
        bool skippedFtsRebuild = false;

        var connection = context.Database.GetDbConnection();
        bool fileMissingOrEmpty = !File.Exists(context.DbPath) || new FileInfo(context.DbPath).Length == 0;
        if (fileMissingOrEmpty)
        {
            context.Database.EnsureCreated();
        }

        bool wasOpen = connection.State == System.Data.ConnectionState.Open;
        if (!wasOpen)
        {
            connection.Open();
        }

        try
        {
            if (!fileMissingOrEmpty && !NeedsSchemaUpgrade(connection) && TableExists(connection, "Notes"))
            {
                skippedEnsureCreated = true;
            }
            else if (!fileMissingOrEmpty)
            {
                if (!wasOpen)
                {
                    connection.Close();
                }

                context.Database.EnsureCreated();
                wasOpen = connection.State == System.Data.ConnectionState.Open;
                if (!wasOpen)
                {
                    connection.Open();
                }
            }

            if (integrityAlreadyVerified)
            {
                skippedIntegrityCheck = true;
            }
            else
            {
                EnsureHealthyOrThrow(connection);
            }

            MaybeBackupBeforeUpgrade(connection, backupBeforeUpgrade);

            // 2. Versioned schema upgrades only when the probe says the file is not current.
            if (!NeedsSchemaUpgrade(connection))
            {
                skippedSchemaUpgradePass = true;
            }
            else
            {
                ApplySchemaUpgrades(context);
            }

            // 3. FTS5: skip rebuild only when every unprotected note matches NotesFts exactly.
            skippedFtsRebuild = EnsureFtsIndex(context, connection);
        }
        finally
        {
            if (!wasOpen)
            {
                connection.Close();
            }
        }

        return new DbInitializeOutcome
        {
            SkippedIntegrityCheck = skippedIntegrityCheck,
            SkippedEnsureCreated = skippedEnsureCreated,
            SkippedSchemaUpgradePass = skippedSchemaUpgradePass,
            SkippedFtsRebuild = skippedFtsRebuild
        };
    }

    public static string CheckIntegrity(QuickNotesDbContext context)
    {
        var connection = context.Database.GetDbConnection();
        bool wasOpen = connection.State == System.Data.ConnectionState.Open;
        if (!wasOpen) connection.Open();
        try
        {
            return ReadIntegrity(connection);
        }
        finally
        {
            if (!wasOpen) connection.Close();
        }
    }

    private static void EnsureHealthyOrThrow(System.Data.Common.DbConnection connection)
    {
        var integrity = ReadIntegrity(connection);
        if (!string.Equals(integrity, "ok", StringComparison.OrdinalIgnoreCase))
        {
            ErrorLogService.Write("SqliteIntegrity", integrity);
            throw new InvalidOperationException(
                "Файл базы данных повреждён. Восстановите резервную копию из папки Backups в каталоге QuickNotes.\n\n" +
                ErrorLogService.Sanitize(integrity));
        }
    }

    private static void MaybeBackupBeforeUpgrade(System.Data.Common.DbConnection connection, Action? backupBeforeUpgrade)
    {
        if (backupBeforeUpgrade == null)
            return;

        if (!NeedsSchemaUpgrade(connection))
            return;
        if (!HasAnyUserData(connection))
            return;

        backupBeforeUpgrade();
    }

    /// <returns><c>true</c> when FTS already matched exactly and rebuild was skipped.</returns>
    private static bool EnsureFtsIndex(QuickNotesDbContext context, System.Data.Common.DbConnection connection)
    {
        if (FtsIndexIsCurrent(connection))
        {
            return true;
        }

        using var transaction = context.Database.BeginTransaction();
        if (TableExists(connection, "NotesFts") && !ColumnExists(connection, "NotesFts", "Title"))
        {
            context.Database.ExecuteSqlRaw("DROP TABLE IF EXISTS NotesFts;");
        }

        context.Database.ExecuteSqlRaw(@"
                CREATE VIRTUAL TABLE IF NOT EXISTS NotesFts USING fts5(NoteId UNINDEXED, Title, Text);

                DROP TRIGGER IF EXISTS Notes_ai;
                CREATE TRIGGER Notes_ai AFTER INSERT ON Notes BEGIN
                    INSERT INTO NotesFts(NoteId, Title, Text) SELECT new.Id, coalesce(new.Title, ''), new.Text WHERE new.IsProtected = 0;
                END;

                DROP TRIGGER IF EXISTS Notes_ad;
                CREATE TRIGGER Notes_ad AFTER DELETE ON Notes BEGIN
                    DELETE FROM NotesFts WHERE NoteId = old.Id;
                END;

                DROP TRIGGER IF EXISTS Notes_au;
                CREATE TRIGGER Notes_au AFTER UPDATE ON Notes BEGIN
                    DELETE FROM NotesFts WHERE NoteId = old.Id;
                    INSERT INTO NotesFts(NoteId, Title, Text) SELECT new.Id, coalesce(new.Title, ''), new.Text WHERE new.IsProtected = 0;
                END;
            ");

        context.Database.ExecuteSqlRaw("DELETE FROM NotesFts;");
        context.Database.ExecuteSqlRaw(@"
                INSERT INTO NotesFts(NoteId, Title, Text)
                SELECT Id, coalesce(Title, ''), Text FROM Notes
                WHERE IsProtected = 0;
            ");
        transaction.Commit();
        return false;
    }

    private static bool FtsIndexIsCurrent(System.Data.Common.DbConnection connection)
    {
        if (!TableExists(connection, "Notes") || !ColumnExists(connection, "Notes", "IsProtected"))
        {
            return false;
        }

        if (!TableExists(connection, "NotesFts") || !ColumnExists(connection, "NotesFts", "Title"))
        {
            return false;
        }

        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
SELECT
  CASE WHEN (
    SELECT COUNT(*) FROM sqlite_master WHERE type='trigger' AND name IN ('Notes_ai','Notes_ad','Notes_au')
  ) = 3 THEN 1 ELSE 0 END,
  CASE WHEN EXISTS (SELECT 1 FROM NotesFts GROUP BY NoteId HAVING COUNT(*) > 1) THEN 1 ELSE 0 END,
  CASE WHEN EXISTS (
    SELECT 1 FROM (
      SELECT NoteId, Title, Text FROM NotesFts
      EXCEPT
      SELECT Id, coalesce(Title, ''), Text FROM Notes WHERE IsProtected = 0
    )
  ) THEN 1 ELSE 0 END,
  CASE WHEN EXISTS (
    SELECT 1 FROM (
      SELECT Id, coalesce(Title, ''), Text FROM Notes WHERE IsProtected = 0
      EXCEPT
      SELECT NoteId, Title, Text FROM NotesFts
    )
  ) THEN 1 ELSE 0 END;";
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
        {
            return false;
        }

        bool triggersOk = reader.GetInt64(0) == 1;
        bool hasDuplicateNoteIds = reader.GetInt64(1) != 0;
        bool ftsHasExtraOrStale = reader.GetInt64(2) != 0;
        bool notesMissingFromFts = reader.GetInt64(3) != 0;
        return triggersOk && !hasDuplicateNoteIds && !ftsHasExtraOrStale && !notesMissingFromFts;
    }

    private static bool NeedsSchemaUpgrade(System.Data.Common.DbConnection connection)
    {
        long version = ReadUserVersion(connection);
        if (version < CurrentSchemaVersion)
            return true;

        if (TableExists(connection, "NoteTemplates") && !HasUniqueTitleIndex(connection))
            return true;

        if (!TableExists(connection, "SyncEntityStates"))
            return true;

        if (TableExists(connection, "SyncEntityStates") && !ColumnExists(connection, "SyncEntityStates", "ContentHash"))
            return true;

        if (TableExists(connection, "Notes") && (!ColumnExists(connection, "Notes", "SyncId") || !HasUniqueIndex(connection, "Notes", "IX_Notes_SyncId")))
            return true;

        if (TableExists(connection, "Tags") && (!ColumnExists(connection, "Tags", "SyncId") || !HasUniqueIndex(connection, "Tags", "IX_Tags_SyncId")))
            return true;

        if (TableExists(connection, "NoteTemplates") && (!ColumnExists(connection, "NoteTemplates", "SyncId") || !HasUniqueIndex(connection, "NoteTemplates", "IX_NoteTemplates_SyncId")))
            return true;

        if (TableExists(connection, "NoteAttachments") && (!ColumnExists(connection, "NoteAttachments", "SyncId") || !HasUniqueIndex(connection, "NoteAttachments", "IX_NoteAttachments_SyncId")))
            return true;

        if (!TableExists(connection, "SyncDeviceStates"))
            return true;

        if (!TableExists(connection, "SyncLocalStates"))
            return true;

        if (TableExists(connection, "SyncLocalStates") && !ColumnExists(connection, "SyncLocalStates", "PendingPackageDigest"))
            return true;

        if (TableExists(connection, "SyncLocalStates") && !ColumnExists(connection, "SyncLocalStates", "PendingPayloadBytes"))
            return true;

        if (!TableExists(connection, "SyncConflicts"))
            return true;

        if (TableExists(connection, "SyncConflicts") && (!ColumnExists(connection, "SyncConflicts", "LocalDataJson") || !HasUniqueIndex(connection, "SyncConflicts", "IX_SyncConflicts_SyncId_RemoteRevisionId")))
            return true;

        // Version 10: per-note password protection columns on Notes, NoteRevisions, NoteAttachments
        if (TableExists(connection, "Notes") && (!ColumnExists(connection, "Notes", "IsProtected") || HasNullProtectionValues(connection, "Notes")))
            return true;

        if (TableExists(connection, "NoteRevisions") && (!ColumnExists(connection, "NoteRevisions", "IsProtected") || HasNullProtectionValues(connection, "NoteRevisions")))
            return true;

        if (TableExists(connection, "NoteAttachments") && (!ColumnExists(connection, "NoteAttachments", "IsProtected") || HasNullProtectionValues(connection, "NoteAttachments")))
            return true;

        // Version 11: ProtectedOriginalSyncId on Notes
        if (TableExists(connection, "Notes") && !ColumnExists(connection, "Notes", "ProtectedOriginalSyncId"))
            return true;

        // Version 12: Title on Notes and NoteRevisions, and NotesFts with Title
        if (TableExists(connection, "Notes") && !ColumnExists(connection, "Notes", "Title"))
            return true;

        if (TableExists(connection, "NoteRevisions") && !ColumnExists(connection, "NoteRevisions", "Title"))
            return true;

        if (TableExists(connection, "NotesFts") && !ColumnExists(connection, "NotesFts", "Title"))
            return true;

        if (TableExists(connection, "Notes") && !ColumnExists(connection, "Notes", "ImportSourceFingerprint"))
            return true;

        // Version 14: explicit versioned KDF descriptor columns (nullable, no ciphertext rewrite)
        if (TableExists(connection, "Notes") && !ColumnExists(connection, "Notes", "ProtectedKdfDescriptor"))
            return true;

        if (TableExists(connection, "NoteRevisions") && !ColumnExists(connection, "NoteRevisions", "ProtectedKdfDescriptor"))
            return true;

        if (TableExists(connection, "NoteAttachments") && !ColumnExists(connection, "NoteAttachments", "ProtectedKdfDescriptor"))
            return true;

        if (!TableExists(connection, "NoteAttachments") || !TableExists(connection, "NoteRevisions"))
            return true;

        return false;
    }

    private static bool HasNullProtectionValues(System.Data.Common.DbConnection connection, string tableName)
    {
        if (!ColumnExists(connection, tableName, "ProtectedSaltBase64"))
            return false;
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT 1 FROM [{tableName}] WHERE ProtectedSaltBase64 IS NULL OR ProtectedCiphertextBase64 IS NULL LIMIT 1;";
        var res = cmd.ExecuteScalar();
        return res != null && res != DBNull.Value;
    }

    private static string ReadIntegrity(System.Data.Common.DbConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA integrity_check;";
        var result = cmd.ExecuteScalar();
        return result?.ToString() ?? "unknown";
    }

    private static long ReadUserVersion(System.Data.Common.DbConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA user_version;";
        var res = cmd.ExecuteScalar();
        if (res == null || res == DBNull.Value)
            return 0;
        return Convert.ToInt64(res);
    }

    public static bool HasAnyUserData(System.Data.Common.DbConnection connection)
    {
        string[] userTables = { "Notes", "Tags", "TagSynonyms", "NoteTags", "NoteRevisions", "NoteAttachments", "NoteTemplates", "NoteTemplateTags" };
        foreach (var table in userTables)
        {
            using var existsCmd = connection.CreateCommand();
            existsCmd.CommandText = $"SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='{table}';";
            var exists = Convert.ToInt64(existsCmd.ExecuteScalar() ?? 0);
            if (exists == 0)
                continue;

            using var countCmd = connection.CreateCommand();
            countCmd.CommandText = $"SELECT EXISTS(SELECT 1 FROM [{table}] LIMIT 1);";
            var hasRows = Convert.ToInt64(countCmd.ExecuteScalar() ?? 0);
            if (hasRows > 0)
                return true;
        }

        return false;
    }

    private static void ApplySchemaUpgrades(QuickNotesDbContext context)
    {
        var connection = context.Database.GetDbConnection();
        bool wasOpen = connection.State == System.Data.ConnectionState.Open;
        if (!wasOpen) connection.Open();

        try
        {
            var existingColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "PRAGMA table_info(Notes);";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    existingColumns.Add(reader.GetString(1)); // Column name
                }
            }

            long currentVersion = 0;
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "PRAGMA user_version;";
                var res = cmd.ExecuteScalar();
                if (res != null && res != DBNull.Value)
                {
                    currentVersion = Convert.ToInt64(res);
                }
            }

            // Version 1 upgrade: Add IsPinned, IsFavorite, IsInbox, DeletedAt to Notes table
            var versionOneColumns = new[] { "IsPinned", "IsFavorite", "IsInbox", "DeletedAt" };
            if (currentVersion < 1 || versionOneColumns.Any(column => !existingColumns.Contains(column)))
            {
                using var transaction = connection.BeginTransaction();
                try
                {
                    if (!existingColumns.Contains("IsPinned"))
                    {
                        using var cmd = connection.CreateCommand();
                        cmd.Transaction = transaction;
                        cmd.CommandText = "ALTER TABLE Notes ADD COLUMN IsPinned INTEGER NOT NULL DEFAULT 0;";
                        cmd.ExecuteNonQuery();
                        existingColumns.Add("IsPinned");
                    }

                    if (!existingColumns.Contains("IsFavorite"))
                    {
                        using var cmd = connection.CreateCommand();
                        cmd.Transaction = transaction;
                        cmd.CommandText = "ALTER TABLE Notes ADD COLUMN IsFavorite INTEGER NOT NULL DEFAULT 0;";
                        cmd.ExecuteNonQuery();
                        existingColumns.Add("IsFavorite");
                    }

                    if (!existingColumns.Contains("IsInbox"))
                    {
                        using var cmd = connection.CreateCommand();
                        cmd.Transaction = transaction;
                        cmd.CommandText = "ALTER TABLE Notes ADD COLUMN IsInbox INTEGER NOT NULL DEFAULT 0;";
                        cmd.ExecuteNonQuery();
                        existingColumns.Add("IsInbox");
                    }

                    if (!existingColumns.Contains("DeletedAt"))
                    {
                        using var cmd = connection.CreateCommand();
                        cmd.Transaction = transaction;
                        cmd.CommandText = "ALTER TABLE Notes ADD COLUMN DeletedAt TEXT NULL;";
                        cmd.ExecuteNonQuery();
                        existingColumns.Add("DeletedAt");
                    }

                    using (var cmd = connection.CreateCommand())
                    {
                        cmd.Transaction = transaction;
                        cmd.CommandText = @"
                            CREATE INDEX IF NOT EXISTS IX_Notes_IsPinned ON Notes(IsPinned);
                            CREATE INDEX IF NOT EXISTS IX_Notes_IsFavorite ON Notes(IsFavorite);
                            CREATE INDEX IF NOT EXISTS IX_Notes_IsInbox ON Notes(IsInbox);
                            CREATE INDEX IF NOT EXISTS IX_Notes_DeletedAt ON Notes(DeletedAt);
                        ";
                        cmd.ExecuteNonQuery();
                    }

                    using (var cmd = connection.CreateCommand())
                    {
                        cmd.Transaction = transaction;
                        cmd.CommandText = "PRAGMA user_version = 1;";
                        cmd.ExecuteNonQuery();
                    }

                    transaction.Commit();
                }
                catch (Exception ex)
                {
                    transaction.Rollback();
                    ErrorLogService.Write("SchemaUpgrade", ex);
                    throw;
                }
            }

            // Version 2 upgrade: Add SourceProcessName, SourceWindowTitle, SourceUrl, CapturedAt to Notes table
            var versionTwoColumns = new[] { "SourceProcessName", "SourceWindowTitle", "SourceUrl", "CapturedAt" };
            if (currentVersion < 2 || versionTwoColumns.Any(column => !existingColumns.Contains(column)))
            {
                using var transaction = connection.BeginTransaction();
                try
                {
                    if (!existingColumns.Contains("SourceProcessName"))
                    {
                        using var cmd = connection.CreateCommand();
                        cmd.Transaction = transaction;
                        cmd.CommandText = "ALTER TABLE Notes ADD COLUMN SourceProcessName TEXT NULL;";
                        cmd.ExecuteNonQuery();
                        existingColumns.Add("SourceProcessName");
                    }

                    if (!existingColumns.Contains("SourceWindowTitle"))
                    {
                        using var cmd = connection.CreateCommand();
                        cmd.Transaction = transaction;
                        cmd.CommandText = "ALTER TABLE Notes ADD COLUMN SourceWindowTitle TEXT NULL;";
                        cmd.ExecuteNonQuery();
                        existingColumns.Add("SourceWindowTitle");
                    }

                    if (!existingColumns.Contains("SourceUrl"))
                    {
                        using var cmd = connection.CreateCommand();
                        cmd.Transaction = transaction;
                        cmd.CommandText = "ALTER TABLE Notes ADD COLUMN SourceUrl TEXT NULL;";
                        cmd.ExecuteNonQuery();
                        existingColumns.Add("SourceUrl");
                    }

                    if (!existingColumns.Contains("CapturedAt"))
                    {
                        using var cmd = connection.CreateCommand();
                        cmd.Transaction = transaction;
                        cmd.CommandText = "ALTER TABLE Notes ADD COLUMN CapturedAt TEXT NULL;";
                        cmd.ExecuteNonQuery();
                        existingColumns.Add("CapturedAt");
                    }

                    using (var cmd = connection.CreateCommand())
                    {
                        cmd.Transaction = transaction;
                        cmd.CommandText = @"
                            CREATE INDEX IF NOT EXISTS IX_Notes_CapturedAt ON Notes(CapturedAt);
                            CREATE INDEX IF NOT EXISTS IX_Notes_SourceProcessName ON Notes(SourceProcessName);
                        ";
                        cmd.ExecuteNonQuery();
                    }

                    using (var cmd = connection.CreateCommand())
                    {
                        cmd.Transaction = transaction;
                        cmd.CommandText = "PRAGMA user_version = 2;";
                        cmd.ExecuteNonQuery();
                    }

                    transaction.Commit();
                }
                catch (Exception ex)
                {
                    transaction.Rollback();
                    ErrorLogService.Write("SchemaUpgrade", ex);
                    throw;
                }
            }

            // Version 3 upgrade: Add NoteRevisions table
            bool noteRevisionsExists = false;
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='NoteRevisions';";
                var res = cmd.ExecuteScalar();
                if (res != null && Convert.ToInt64(res) > 0)
                {
                    noteRevisionsExists = true;
                }
            }

            if (currentVersion < 3 || !noteRevisionsExists)
            {
                using var transaction = connection.BeginTransaction();
                try
                {
                    using (var cmd = connection.CreateCommand())
                    {
                        cmd.Transaction = transaction;
                        cmd.CommandText = @"
                            CREATE TABLE IF NOT EXISTS NoteRevisions (
                                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                                NoteId INTEGER NOT NULL,
                                CreatedAt TEXT NOT NULL,
                                Text TEXT NOT NULL,
                                TagsJson TEXT NOT NULL,
                                CONSTRAINT FK_NoteRevisions_Notes_NoteId FOREIGN KEY (NoteId) REFERENCES Notes (Id) ON DELETE CASCADE
                            );
                            CREATE INDEX IF NOT EXISTS IX_NoteRevisions_NoteId ON NoteRevisions(NoteId);
                            CREATE INDEX IF NOT EXISTS IX_NoteRevisions_CreatedAt ON NoteRevisions(CreatedAt);
                        ";
                        cmd.ExecuteNonQuery();
                    }

                    using (var cmd = connection.CreateCommand())
                    {
                        cmd.Transaction = transaction;
                        cmd.CommandText = "PRAGMA user_version = 3;";
                        cmd.ExecuteNonQuery();
                    }

                    transaction.Commit();
                }
                catch (Exception ex)
                {
                    transaction.Rollback();
                    ErrorLogService.Write("SchemaUpgrade", ex);
                    throw;
                }
            }

            // Ensure NoteAttachments table and indexes exist
            bool noteAttachmentsExists = false;
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='NoteAttachments';";
                var res = cmd.ExecuteScalar();
                if (res != null && Convert.ToInt64(res) > 0)
                {
                    noteAttachmentsExists = true;
                }
            }

            if (!noteAttachmentsExists)
            {
                using var transaction = connection.BeginTransaction();
                try
                {
                    using (var cmd = connection.CreateCommand())
                    {
                        cmd.Transaction = transaction;
                        cmd.CommandText = @"
                            CREATE TABLE IF NOT EXISTS NoteAttachments (
                                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                                NoteId INTEGER NOT NULL,
                                OriginalFileName TEXT NOT NULL,
                                StoredFileName TEXT NOT NULL,
                                RelativePath TEXT NOT NULL,
                                ContentType TEXT NOT NULL,
                                Size INTEGER NOT NULL,
                                Sha256 TEXT NOT NULL,
                                CreatedAt TEXT NOT NULL,
                                CONSTRAINT FK_NoteAttachments_Notes_NoteId FOREIGN KEY (NoteId) REFERENCES Notes (Id) ON DELETE CASCADE
                            );
                            CREATE INDEX IF NOT EXISTS IX_NoteAttachments_NoteId ON NoteAttachments(NoteId);
                            CREATE INDEX IF NOT EXISTS IX_NoteAttachments_Sha256 ON NoteAttachments(Sha256);
                            CREATE INDEX IF NOT EXISTS IX_NoteAttachments_StoredFileName ON NoteAttachments(StoredFileName);
                        ";
                        cmd.ExecuteNonQuery();
                    }

                    transaction.Commit();
                }
                catch (Exception ex)
                {
                    transaction.Rollback();
                    ErrorLogService.Write("SchemaUpgrade", ex);
                    throw;
                }
            }

            // Version 4 upgrade: NoteTemplates, NoteTemplateTags, and unique Title index with NOCASE
            bool templatesTableExists = TableExists(connection, "NoteTemplates");
            bool templateTagsTableExists = TableExists(connection, "NoteTemplateTags");
            bool hasUniqueTitleIndex = HasUniqueTitleIndex(connection);

            if (currentVersion < 4 || !templatesTableExists || !templateTagsTableExists || !hasUniqueTitleIndex)
            {
                using var transaction = connection.BeginTransaction();
                try
                {
                    if (!templatesTableExists)
                    {
                        using var cmd = connection.CreateCommand();
                        cmd.Transaction = transaction;
                        cmd.CommandText = @"
                            CREATE TABLE IF NOT EXISTS NoteTemplates (
                                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                                Title TEXT NOT NULL COLLATE NOCASE,
                                Text TEXT NOT NULL,
                                CreatedAt TEXT NOT NULL,
                                UpdatedAt TEXT NOT NULL
                            );
                        ";
                        cmd.ExecuteNonQuery();
                    }

                    if (!templateTagsTableExists)
                    {
                        using var cmd = connection.CreateCommand();
                        cmd.Transaction = transaction;
                        cmd.CommandText = @"
                            CREATE TABLE IF NOT EXISTS NoteTemplateTags (
                                TemplateId INTEGER NOT NULL,
                                TagId INTEGER NOT NULL,
                                PRIMARY KEY (TemplateId, TagId),
                                CONSTRAINT FK_NoteTemplateTags_NoteTemplates_TemplateId FOREIGN KEY (TemplateId) REFERENCES NoteTemplates (Id) ON DELETE CASCADE,
                                CONSTRAINT FK_NoteTemplateTags_Tags_TagId FOREIGN KEY (TagId) REFERENCES Tags (Id) ON DELETE CASCADE
                            );
                            CREATE INDEX IF NOT EXISTS IX_NoteTemplateTags_TagId ON NoteTemplateTags(TagId);
                        ";
                        cmd.ExecuteNonQuery();
                    }

                    // Check and resolve any duplicate/colliding titles among existing templates before unique index
                    var templatesToUpdate = new List<(int Id, string OldTitle, string NewTitle, bool IsConflict)>();
                    using (var readCmd = connection.CreateCommand())
                    {
                        readCmd.Transaction = transaction;
                        readCmd.CommandText = "SELECT Id, Title FROM NoteTemplates ORDER BY Id ASC;";
                        using var reader = readCmd.ExecuteReader();
                        var seenNormalized = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                        while (reader.Read())
                        {
                            int id = reader.GetInt32(0);
                            string rawTitle = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
                            string normalized = NoteTemplateService.NormalizeTitle(rawTitle);

                            if (string.IsNullOrWhiteSpace(normalized))
                            {
                                normalized = $"Шаблон {id}";
                            }

                            if (!seenNormalized.Contains(normalized))
                            {
                                seenNormalized.Add(normalized);
                                if (!string.Equals(rawTitle, normalized, StringComparison.Ordinal))
                                {
                                    templatesToUpdate.Add((id, rawTitle, normalized, false));
                                }
                            }
                            else
                            {
                                string candidate = $"{normalized} (дубликат {id})";
                                int suffix = 2;
                                while (seenNormalized.Contains(candidate))
                                {
                                    candidate = $"{normalized} (дубликат {id}_{suffix++})";
                                }
                                seenNormalized.Add(candidate);
                                templatesToUpdate.Add((id, rawTitle, candidate, true));
                            }
                        }
                    }

                    foreach (var (id, oldTitle, newTitle, isConflict) in templatesToUpdate)
                    {
                        using var updateCmd = connection.CreateCommand();
                        updateCmd.Transaction = transaction;
                        updateCmd.CommandText = "UPDATE NoteTemplates SET Title = @newTitle WHERE Id = @id;";
                        var pNew = updateCmd.CreateParameter();
                        pNew.ParameterName = "@newTitle";
                        pNew.Value = newTitle;
                        updateCmd.Parameters.Add(pNew);
                        var pId = updateCmd.CreateParameter();
                        pId.ParameterName = "@id";
                        pId.Value = id;
                        updateCmd.Parameters.Add(pId);
                        updateCmd.ExecuteNonQuery();

                        if (isConflict)
                        {
                            string diag = $"Обнаружен конфликт названий шаблонов: шаблон #{id} ('{oldTitle}') переименован в '{newTitle}' для обеспечения уникальности без потери данных. Вы можете переименовать или объединить этот шаблон в настройках.";
                            LastMigrationDiagnostics.Add(diag);
                            ErrorLogService.Write("NoteTemplateMigration", diag);
                        }
                    }

                    using (var indexCmd = connection.CreateCommand())
                    {
                        indexCmd.Transaction = transaction;
                        indexCmd.CommandText = @"
                            DROP INDEX IF EXISTS IX_NoteTemplates_Title;
                            CREATE UNIQUE INDEX IF NOT EXISTS IX_NoteTemplates_Title ON NoteTemplates(Title COLLATE NOCASE);
                            CREATE INDEX IF NOT EXISTS IX_NoteTemplates_CreatedAt ON NoteTemplates(CreatedAt);
                            CREATE INDEX IF NOT EXISTS IX_NoteTemplates_UpdatedAt ON NoteTemplates(UpdatedAt);
                            CREATE INDEX IF NOT EXISTS IX_NoteTemplateTags_TagId ON NoteTemplateTags(TagId);
                        ";
                        indexCmd.ExecuteNonQuery();
                    }

                    using (var verCmd = connection.CreateCommand())
                    {
                        verCmd.Transaction = transaction;
                        verCmd.CommandText = "PRAGMA user_version = 4;";
                        verCmd.ExecuteNonQuery();
                    }

                    transaction.Commit();
                }
                catch (Exception ex)
                {
                    transaction.Rollback();
                    ErrorLogService.Write("SchemaUpgrade", ex);
                    throw;
                }
            }

            // Version 5 upgrade: Persistent SyncId GUIDs and SyncEntityStates table
            bool needsV5 = currentVersion < 5 ||
                !TableExists(connection, "SyncEntityStates") ||
                (TableExists(connection, "Notes") && !ColumnExists(connection, "Notes", "SyncId")) ||
                (TableExists(connection, "Tags") && !ColumnExists(connection, "Tags", "SyncId")) ||
                (TableExists(connection, "NoteTemplates") && !ColumnExists(connection, "NoteTemplates", "SyncId")) ||
                (TableExists(connection, "NoteAttachments") && !ColumnExists(connection, "NoteAttachments", "SyncId")) ||
                (TableExists(connection, "Notes") && !HasUniqueIndex(connection, "Notes", "IX_Notes_SyncId")) ||
                (TableExists(connection, "Tags") && !HasUniqueIndex(connection, "Tags", "IX_Tags_SyncId")) ||
                (TableExists(connection, "NoteTemplates") && !HasUniqueIndex(connection, "NoteTemplates", "IX_NoteTemplates_SyncId")) ||
                (TableExists(connection, "NoteAttachments") && !HasUniqueIndex(connection, "NoteAttachments", "IX_NoteAttachments_SyncId"));

            if (needsV5)
            {
                using var transaction = connection.BeginTransaction();
                try
                {
                    if (TableExists(connection, "Notes"))
                    {
                        EnsureSyncIdColumnAndPopulate(connection, transaction, "Notes");
                    }
                    if (TableExists(connection, "Tags"))
                    {
                        EnsureSyncIdColumnAndPopulate(connection, transaction, "Tags");
                    }
                    if (TableExists(connection, "NoteTemplates"))
                    {
                        EnsureSyncIdColumnAndPopulate(connection, transaction, "NoteTemplates");
                    }
                    if (TableExists(connection, "NoteAttachments"))
                    {
                        EnsureSyncIdColumnAndPopulate(connection, transaction, "NoteAttachments");
                    }

                    using (var cmd = connection.CreateCommand())
                    {
                        cmd.Transaction = transaction;
                        cmd.CommandText = @"
                            CREATE TABLE IF NOT EXISTS SyncEntityStates (
                                SyncId TEXT PRIMARY KEY COLLATE NOCASE,
                                EntityType TEXT NOT NULL,
                                RevisionId TEXT NOT NULL COLLATE NOCASE,
                                ParentRevisionId TEXT NULL COLLATE NOCASE,
                                DeviceId TEXT NOT NULL COLLATE NOCASE,
                                UpdatedAtUtc TEXT NOT NULL,
                                IsDeleted INTEGER NOT NULL DEFAULT 0,
                                DeletedAtUtc TEXT NULL,
                                ContentHash TEXT NULL
                            );
                            CREATE INDEX IF NOT EXISTS IX_SyncEntityStates_EntityType ON SyncEntityStates(EntityType);
                            CREATE INDEX IF NOT EXISTS IX_SyncEntityStates_RevisionId ON SyncEntityStates(RevisionId);
                        ";
                        cmd.ExecuteNonQuery();
                    }

                    PopulateInitialSyncEntityStates(connection, transaction);

                    using (var cmd = connection.CreateCommand())
                    {
                        cmd.Transaction = transaction;
                        var sql = new System.Text.StringBuilder();
                        if (TableExists(connection, "Notes") && ColumnExists(connection, "Notes", "SyncId"))
                        {
                            sql.AppendLine("CREATE UNIQUE INDEX IF NOT EXISTS IX_Notes_SyncId ON Notes(SyncId);");
                        }
                        if (TableExists(connection, "Tags") && ColumnExists(connection, "Tags", "SyncId"))
                        {
                            sql.AppendLine("CREATE UNIQUE INDEX IF NOT EXISTS IX_Tags_SyncId ON Tags(SyncId);");
                        }
                        if (TableExists(connection, "NoteTemplates") && ColumnExists(connection, "NoteTemplates", "SyncId"))
                        {
                            sql.AppendLine("CREATE UNIQUE INDEX IF NOT EXISTS IX_NoteTemplates_SyncId ON NoteTemplates(SyncId);");
                        }
                        if (TableExists(connection, "NoteAttachments") && ColumnExists(connection, "NoteAttachments", "SyncId"))
                        {
                            sql.AppendLine("CREATE UNIQUE INDEX IF NOT EXISTS IX_NoteAttachments_SyncId ON NoteAttachments(SyncId);");
                        }
                        cmd.CommandText = sql.ToString();
                        if (!string.IsNullOrWhiteSpace(cmd.CommandText))
                        {
                            cmd.ExecuteNonQuery();
                        }
                    }

                    using (var verCmd = connection.CreateCommand())
                    {
                        verCmd.Transaction = transaction;
                        verCmd.CommandText = "PRAGMA user_version = 5;";
                        verCmd.ExecuteNonQuery();
                    }

                    transaction.Commit();
                    currentVersion = 5;
                }
                catch (Exception ex)
                {
                    transaction.Rollback();
                    ErrorLogService.Write("SchemaUpgradeV5", ex);
                    throw;
                }
            }

            // Version 6 upgrade: ContentHash in SyncEntityStates
            bool syncEntityStatesExists = TableExists(connection, "SyncEntityStates");
            bool needsV6 = currentVersion < 6 ||
                !syncEntityStatesExists ||
                (syncEntityStatesExists && !ColumnExists(connection, "SyncEntityStates", "ContentHash"));

            if (needsV6)
            {
                using var transaction = connection.BeginTransaction();
                try
                {
                    if (!syncEntityStatesExists)
                    {
                        using var cmd = connection.CreateCommand();
                        cmd.Transaction = transaction;
                        cmd.CommandText = @"
                            CREATE TABLE IF NOT EXISTS SyncEntityStates (
                                SyncId TEXT PRIMARY KEY COLLATE NOCASE,
                                EntityType TEXT NOT NULL,
                                RevisionId TEXT NOT NULL COLLATE NOCASE,
                                ParentRevisionId TEXT NULL COLLATE NOCASE,
                                DeviceId TEXT NOT NULL COLLATE NOCASE,
                                UpdatedAtUtc TEXT NOT NULL,
                                IsDeleted INTEGER NOT NULL DEFAULT 0,
                                DeletedAtUtc TEXT NULL,
                                ContentHash TEXT NULL
                            );
                            CREATE INDEX IF NOT EXISTS IX_SyncEntityStates_EntityType ON SyncEntityStates(EntityType);
                            CREATE INDEX IF NOT EXISTS IX_SyncEntityStates_RevisionId ON SyncEntityStates(RevisionId);
                        ";
                        cmd.ExecuteNonQuery();
                    }
                    else
                    {
                        if (!ColumnExists(connection, "SyncEntityStates", "ContentHash"))
                        {
                            using var alterCmd = connection.CreateCommand();
                            alterCmd.Transaction = transaction;
                            alterCmd.CommandText = "ALTER TABLE SyncEntityStates ADD COLUMN ContentHash TEXT NULL;";
                            alterCmd.ExecuteNonQuery();
                        }

                        using var normCmd = connection.CreateCommand();
                        normCmd.Transaction = transaction;
                        normCmd.CommandText = @"
                            UPDATE SyncEntityStates 
                            SET SyncId = upper(SyncId), RevisionId = upper(RevisionId), DeviceId = upper(DeviceId)
                            WHERE SyncId <> upper(SyncId);";
                        normCmd.ExecuteNonQuery();
                    }

                    PopulateInitialSyncEntityStates(connection, transaction);

                    using (var verCmd = connection.CreateCommand())
                    {
                        verCmd.Transaction = transaction;
                        verCmd.CommandText = "PRAGMA user_version = 6;";
                        verCmd.ExecuteNonQuery();
                    }

                    transaction.Commit();
                    currentVersion = 6;
                }
                catch (Exception ex)
                {
                    transaction.Rollback();
                    ErrorLogService.Write("SchemaUpgradeV6", ex);
                    throw;
                }
            }

            // Version 7 upgrade: SyncDeviceStates, SyncLocalStates, SyncConflicts
            bool syncDeviceStatesExists = TableExists(connection, "SyncDeviceStates");
            bool syncLocalStatesExists = TableExists(connection, "SyncLocalStates");
            bool syncConflictsExists = TableExists(connection, "SyncConflicts");
            bool hasConflictUniqueIndex = syncConflictsExists && HasUniqueIndex(connection, "SyncConflicts", "IX_SyncConflicts_SyncId_RemoteRevisionId");

            bool syncLocalStatesComplete = syncLocalStatesExists &&
                ColumnExists(connection, "SyncLocalStates", "PendingPackageDigest") &&
                ColumnExists(connection, "SyncLocalStates", "PendingPackageId") &&
                ColumnExists(connection, "SyncLocalStates", "LatestUploadedPackageKey");

            bool syncConflictsComplete = syncConflictsExists &&
                ColumnExists(connection, "SyncConflicts", "LocalDataJson") &&
                ColumnExists(connection, "SyncConflicts", "RemoteDataJson");

            bool needsV7 = currentVersion < 7 ||
                !syncDeviceStatesExists ||
                !syncLocalStatesExists ||
                !syncLocalStatesComplete ||
                !syncConflictsExists ||
                !syncConflictsComplete ||
                !hasConflictUniqueIndex;

            if (needsV7)
            {
                using var transaction = connection.BeginTransaction();
                try
                {
                    if (!syncDeviceStatesExists)
                    {
                        using var cmd = connection.CreateCommand();
                        cmd.Transaction = transaction;
                        cmd.CommandText = @"
                            CREATE TABLE IF NOT EXISTS SyncDeviceStates (
                                DeviceId TEXT PRIMARY KEY COLLATE NOCASE,
                                LatestProcessedPackageId TEXT NULL COLLATE NOCASE,
                                LatestProcessedETag TEXT NULL,
                                LastSyncedAtUtc TEXT NOT NULL,
                                PackageCount INTEGER NOT NULL DEFAULT 0
                            );
                        ";
                        cmd.ExecuteNonQuery();
                    }

                    if (!syncLocalStatesExists)
                    {
                        using var cmd = connection.CreateCommand();
                        cmd.Transaction = transaction;
                        cmd.CommandText = @"
                            CREATE TABLE IF NOT EXISTS SyncLocalStates (
                                DeviceId TEXT PRIMARY KEY COLLATE NOCASE,
                                LastUploadedPackageId TEXT NULL COLLATE NOCASE,
                                LatestUploadedPackageKey TEXT NULL,
                                LastUploadedETag TEXT NULL,
                                LastUploadedSnapshotHash TEXT NULL,
                                LastUploadCompletedAtUtc TEXT NULL,
                                PendingPackageId TEXT NULL COLLATE NOCASE,
                                PendingPackageKey TEXT NULL,
                                PendingPackageDigest TEXT NULL,
                                PendingContentHash TEXT NULL,
                                PendingCreatedAtUtc TEXT NULL,
                                PendingPayloadBytes BLOB NULL
                            );
                        ";
                        cmd.ExecuteNonQuery();
                    }

                    if (!syncConflictsExists)
                    {
                        using var cmd = connection.CreateCommand();
                        cmd.Transaction = transaction;
                        cmd.CommandText = @"
                            CREATE TABLE IF NOT EXISTS SyncConflicts (
                                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                                SyncId TEXT NOT NULL COLLATE NOCASE,
                                EntityType TEXT NOT NULL,
                                LocalRevisionId TEXT NULL COLLATE NOCASE,
                                RemoteRevisionId TEXT NOT NULL COLLATE NOCASE,
                                ParentRevisionId TEXT NULL COLLATE NOCASE,
                                SourceDeviceId TEXT NOT NULL COLLATE NOCASE,
                                SourcePackageId TEXT NULL COLLATE NOCASE,
                                DetectedAtUtc TEXT NOT NULL,
                                Reason TEXT NOT NULL,
                                LocalDataJson TEXT NULL,
                                RemoteDataJson TEXT NULL,
                                IsResolved INTEGER NOT NULL DEFAULT 0,
                                ResolvedAtUtc TEXT NULL,
                                ResolutionAction TEXT NULL
                            );
                            CREATE INDEX IF NOT EXISTS IX_SyncConflicts_SyncId ON SyncConflicts(SyncId);
                            CREATE INDEX IF NOT EXISTS IX_SyncConflicts_IsResolved ON SyncConflicts(IsResolved);
                            CREATE UNIQUE INDEX IF NOT EXISTS IX_SyncConflicts_SyncId_RemoteRevisionId ON SyncConflicts(SyncId, RemoteRevisionId);
                        ";
                        cmd.ExecuteNonQuery();
                    }
                    else
                    {
                        EnsureColumnExists(connection, transaction, "SyncConflicts", "LocalDataJson", "TEXT NULL");
                        EnsureColumnExists(connection, transaction, "SyncConflicts", "RemoteDataJson", "TEXT NULL");
                        EnsureColumnExists(connection, transaction, "SyncConflicts", "IsResolved", "INTEGER NOT NULL DEFAULT 0");
                        EnsureColumnExists(connection, transaction, "SyncConflicts", "ResolvedAtUtc", "TEXT NULL");
                        EnsureColumnExists(connection, transaction, "SyncConflicts", "ResolutionAction", "TEXT NULL");

                        using var idxCmd = connection.CreateCommand();
                        idxCmd.Transaction = transaction;
                        idxCmd.CommandText = @"
                            CREATE INDEX IF NOT EXISTS IX_SyncConflicts_SyncId ON SyncConflicts(SyncId);
                            CREATE INDEX IF NOT EXISTS IX_SyncConflicts_IsResolved ON SyncConflicts(IsResolved);
                            CREATE UNIQUE INDEX IF NOT EXISTS IX_SyncConflicts_SyncId_RemoteRevisionId ON SyncConflicts(SyncId, RemoteRevisionId);
                        ";
                        idxCmd.ExecuteNonQuery();
                    }

                    if (TableExists(connection, "SyncLocalStates"))
                    {
                        EnsureColumnExists(connection, transaction, "SyncLocalStates", "LatestUploadedPackageKey", "TEXT NULL");
                        EnsureColumnExists(connection, transaction, "SyncLocalStates", "PendingPackageId", "TEXT NULL COLLATE NOCASE");
                        EnsureColumnExists(connection, transaction, "SyncLocalStates", "PendingPackageKey", "TEXT NULL");
                        EnsureColumnExists(connection, transaction, "SyncLocalStates", "PendingPackageDigest", "TEXT NULL");
                        EnsureColumnExists(connection, transaction, "SyncLocalStates", "PendingContentHash", "TEXT NULL");
                        EnsureColumnExists(connection, transaction, "SyncLocalStates", "PendingCreatedAtUtc", "TEXT NULL");
                        EnsureColumnExists(connection, transaction, "SyncLocalStates", "PendingPayloadBytes", "BLOB NULL");
                    }

                    if (TableExists(connection, "SyncDeviceStates"))
                    {
                        EnsureColumnExists(connection, transaction, "SyncDeviceStates", "PackageCount", "INTEGER NOT NULL DEFAULT 0");
                    }

                    using (var verCmd = connection.CreateCommand())
                    {
                        verCmd.Transaction = transaction;
                        verCmd.CommandText = "PRAGMA user_version = 7;";
                        verCmd.ExecuteNonQuery();
                    }

                    transaction.Commit();
                    currentVersion = 7;
                }
                catch (Exception ex)
                {
                    transaction.Rollback();
                    ErrorLogService.Write("SchemaUpgradeV7", ex);
                    throw;
                }
            }

            // Version 8 upgrade: PendingPayloadBytes in SyncLocalStates
            bool syncLocalStatesExistsV8 = TableExists(connection, "SyncLocalStates");
            bool hasPendingPayloadBytes = syncLocalStatesExistsV8 && ColumnExists(connection, "SyncLocalStates", "PendingPayloadBytes");

            bool needsV8 = currentVersion < 8 || !hasPendingPayloadBytes;
            if (needsV8)
            {
                using var transaction = connection.BeginTransaction();
                try
                {
                    if (TableExists(connection, "SyncLocalStates"))
                    {
                        EnsureColumnExists(connection, transaction, "SyncLocalStates", "PendingPayloadBytes", "BLOB NULL");
                    }

                    using (var verCmd = connection.CreateCommand())
                    {
                        verCmd.Transaction = transaction;
                        verCmd.CommandText = "PRAGMA user_version = 8;";
                        verCmd.ExecuteNonQuery();
                    }

                    transaction.Commit();
                    currentVersion = 8;
                }
                catch (Exception ex)
                {
                    transaction.Rollback();
                    ErrorLogService.Write("SchemaUpgradeV8", ex);
                    throw;
                }
            }

            // Version 9: rewrite legacy [[localId]] wiki links to portable [[qn:syncId]] anchors.
            if (currentVersion < 9)
            {
                using var transaction = connection.BeginTransaction();
                try
                {
                    MigrateLegacyWikiLinks(connection, transaction);

                    using (var verCmd = connection.CreateCommand())
                    {
                        verCmd.Transaction = transaction;
                        verCmd.CommandText = "PRAGMA user_version = 9;";
                        verCmd.ExecuteNonQuery();
                    }

                    transaction.Commit();
                    currentVersion = 9;
                }
                catch (Exception ex)
                {
                    transaction.Rollback();
                    ErrorLogService.Write("SchemaUpgradeV9", ex);
                    throw;
                }
            }

            // Version 10: per-note password protection columns on Notes, NoteRevisions, NoteAttachments.
            // Idempotent: adds the columns only when missing; never touches user data.
            // Existing unprotected notes remain fully readable (columns default to unprotected).
            bool notesTableExistsV10 = TableExists(connection, "Notes");
            bool revisionsTableExistsV10 = TableExists(connection, "NoteRevisions");
            bool attachmentsTableExistsV10 = TableExists(connection, "NoteAttachments");

            bool notesNeedV10 = notesTableExistsV10 && (!ColumnExists(connection, "Notes", "IsProtected") || HasNullProtectionValues(connection, "Notes"));
            bool revisionsNeedV10 = revisionsTableExistsV10 && (!ColumnExists(connection, "NoteRevisions", "IsProtected") || HasNullProtectionValues(connection, "NoteRevisions"));
            bool attachmentsNeedV10 = attachmentsTableExistsV10 && (!ColumnExists(connection, "NoteAttachments", "IsProtected") || HasNullProtectionValues(connection, "NoteAttachments"));

            if (currentVersion < 10 || notesNeedV10 || revisionsNeedV10 || attachmentsNeedV10)
            {
                using var transaction = connection.BeginTransaction();
                try
                {
                    if (notesTableExistsV10)
                    {
                        EnsureColumnExists(connection, transaction, "Notes", "IsProtected", "INTEGER NOT NULL DEFAULT 0");
                        EnsureColumnExists(connection, transaction, "Notes", "ProtectedFormatVersion", "INTEGER NOT NULL DEFAULT 0");
                        EnsureColumnExists(connection, transaction, "Notes", "ProtectedKdfIterations", "INTEGER NOT NULL DEFAULT 0");
                        EnsureColumnExists(connection, transaction, "Notes", "ProtectedSaltBase64", "TEXT NULL DEFAULT ''");
                        EnsureColumnExists(connection, transaction, "Notes", "ProtectedNonceBase64", "TEXT NULL DEFAULT ''");
                        EnsureColumnExists(connection, transaction, "Notes", "ProtectedTagBase64", "TEXT NULL DEFAULT ''");
                        EnsureColumnExists(connection, transaction, "Notes", "ProtectedCiphertextBase64", "TEXT NULL DEFAULT ''");

                        using (var updateCmd = connection.CreateCommand())
                        {
                            updateCmd.Transaction = transaction;
                            updateCmd.CommandText = @"
                                UPDATE Notes SET
                                    ProtectedSaltBase64 = COALESCE(ProtectedSaltBase64, ''),
                                    ProtectedNonceBase64 = COALESCE(ProtectedNonceBase64, ''),
                                    ProtectedTagBase64 = COALESCE(ProtectedTagBase64, ''),
                                    ProtectedCiphertextBase64 = COALESCE(ProtectedCiphertextBase64, '')
                                WHERE ProtectedSaltBase64 IS NULL
                                   OR ProtectedNonceBase64 IS NULL
                                   OR ProtectedTagBase64 IS NULL
                                   OR ProtectedCiphertextBase64 IS NULL;
                            ";
                            updateCmd.ExecuteNonQuery();
                        }

                        using (var idxCmd = connection.CreateCommand())
                        {
                            idxCmd.Transaction = transaction;
                            idxCmd.CommandText = "CREATE INDEX IF NOT EXISTS IX_Notes_IsProtected ON Notes(IsProtected);";
                            idxCmd.ExecuteNonQuery();
                        }
                    }

                    if (revisionsTableExistsV10)
                    {
                        EnsureColumnExists(connection, transaction, "NoteRevisions", "IsProtected", "INTEGER NOT NULL DEFAULT 0");
                        EnsureColumnExists(connection, transaction, "NoteRevisions", "ProtectedFormatVersion", "INTEGER NOT NULL DEFAULT 0");
                        EnsureColumnExists(connection, transaction, "NoteRevisions", "ProtectedKdfIterations", "INTEGER NOT NULL DEFAULT 0");
                        EnsureColumnExists(connection, transaction, "NoteRevisions", "ProtectedSaltBase64", "TEXT NULL DEFAULT ''");
                        EnsureColumnExists(connection, transaction, "NoteRevisions", "ProtectedNonceBase64", "TEXT NULL DEFAULT ''");
                        EnsureColumnExists(connection, transaction, "NoteRevisions", "ProtectedTagBase64", "TEXT NULL DEFAULT ''");
                        EnsureColumnExists(connection, transaction, "NoteRevisions", "ProtectedCiphertextBase64", "TEXT NULL DEFAULT ''");

                        using (var updateCmd = connection.CreateCommand())
                        {
                            updateCmd.Transaction = transaction;
                            updateCmd.CommandText = @"
                                UPDATE NoteRevisions SET
                                    ProtectedSaltBase64 = COALESCE(ProtectedSaltBase64, ''),
                                    ProtectedNonceBase64 = COALESCE(ProtectedNonceBase64, ''),
                                    ProtectedTagBase64 = COALESCE(ProtectedTagBase64, ''),
                                    ProtectedCiphertextBase64 = COALESCE(ProtectedCiphertextBase64, '')
                                WHERE ProtectedSaltBase64 IS NULL
                                   OR ProtectedNonceBase64 IS NULL
                                   OR ProtectedTagBase64 IS NULL
                                   OR ProtectedCiphertextBase64 IS NULL;
                            ";
                            updateCmd.ExecuteNonQuery();
                        }
                    }

                    if (attachmentsTableExistsV10)
                    {
                        EnsureColumnExists(connection, transaction, "NoteAttachments", "IsProtected", "INTEGER NOT NULL DEFAULT 0");
                        EnsureColumnExists(connection, transaction, "NoteAttachments", "ProtectedFormatVersion", "INTEGER NOT NULL DEFAULT 0");
                        EnsureColumnExists(connection, transaction, "NoteAttachments", "ProtectedKdfIterations", "INTEGER NOT NULL DEFAULT 0");
                        EnsureColumnExists(connection, transaction, "NoteAttachments", "ProtectedSaltBase64", "TEXT NULL DEFAULT ''");
                        EnsureColumnExists(connection, transaction, "NoteAttachments", "ProtectedNonceBase64", "TEXT NULL DEFAULT ''");
                        EnsureColumnExists(connection, transaction, "NoteAttachments", "ProtectedTagBase64", "TEXT NULL DEFAULT ''");
                        EnsureColumnExists(connection, transaction, "NoteAttachments", "ProtectedCiphertextBase64", "TEXT NULL DEFAULT ''");

                        using (var updateCmd = connection.CreateCommand())
                        {
                            updateCmd.Transaction = transaction;
                            updateCmd.CommandText = @"
                                UPDATE NoteAttachments SET
                                    ProtectedSaltBase64 = COALESCE(ProtectedSaltBase64, ''),
                                    ProtectedNonceBase64 = COALESCE(ProtectedNonceBase64, ''),
                                    ProtectedTagBase64 = COALESCE(ProtectedTagBase64, ''),
                                    ProtectedCiphertextBase64 = COALESCE(ProtectedCiphertextBase64, '')
                                WHERE ProtectedSaltBase64 IS NULL
                                   OR ProtectedNonceBase64 IS NULL
                                   OR ProtectedTagBase64 IS NULL
                                   OR ProtectedCiphertextBase64 IS NULL;
                            ";
                            updateCmd.ExecuteNonQuery();
                        }
                    }

                    using (var verCmd = connection.CreateCommand())
                    {
                        verCmd.Transaction = transaction;
                        verCmd.CommandText = "PRAGMA user_version = 10;";
                        verCmd.ExecuteNonQuery();
                    }

                    transaction.Commit();
                    currentVersion = 10;
                }
                catch (Exception ex)
                {
                    transaction.Rollback();
                    ErrorLogService.Write("SchemaUpgradeV10", ex);
                    throw;
                }
            }

            // Version 11: ProtectedOriginalSyncId on Notes for conflict resolution (KeepBoth) cipher preserving sync ID.
            // Safe and idempotent: adds column only if missing; never touches user data or crypto envelopes.
            bool notesTableExistsV11 = TableExists(connection, "Notes");
            bool notesNeedV11 = notesTableExistsV11 && !ColumnExists(connection, "Notes", "ProtectedOriginalSyncId");

            if (currentVersion < 11 || notesNeedV11)
            {
                using var transaction = connection.BeginTransaction();
                try
                {
                    if (notesTableExistsV11)
                    {
                        EnsureColumnExists(connection, transaction, "Notes", "ProtectedOriginalSyncId", "TEXT NULL");
                    }

                    using (var verCmd = connection.CreateCommand())
                    {
                        verCmd.Transaction = transaction;
                        verCmd.CommandText = "PRAGMA user_version = 11;";
                        verCmd.ExecuteNonQuery();
                    }

                    transaction.Commit();
                    currentVersion = 11;
                }
                catch (Exception ex)
                {
                    transaction.Rollback();
                    ErrorLogService.Write("SchemaUpgradeV11", ex);
                    throw;
                }
            }

            // Version 12: Title on Notes and NoteRevisions, with backfill for unprotected notes from first non-empty line of Text.
            // Text is strictly NEVER modified. Protected notes remain with empty Title in DB.
            // NotesFts is updated to include Title: NotesFts USING fts5(NoteId UNINDEXED, Title, Text).
            bool notesTableExistsV12 = TableExists(connection, "Notes");
            bool revisionsTableExistsV12 = TableExists(connection, "NoteRevisions");
            bool ftsTableExistsV12 = TableExists(connection, "NotesFts");

            bool notesNeedV12 = notesTableExistsV12 && !ColumnExists(connection, "Notes", "Title");
            bool revisionsNeedV12 = revisionsTableExistsV12 && !ColumnExists(connection, "NoteRevisions", "Title");
            bool ftsNeedsV12 = ftsTableExistsV12 && !ColumnExists(connection, "NotesFts", "Title");

            if (currentVersion < 12 || notesNeedV12 || revisionsNeedV12 || ftsNeedsV12)
            {
                using var transaction = connection.BeginTransaction();
                try
                {
                    if (notesTableExistsV12)
                    {
                        EnsureColumnExists(connection, transaction, "Notes", "Title", "TEXT NOT NULL DEFAULT ''");

                        // Backfill title for unprotected notes where Title is empty or null, without touching Text
                        var notesToBackfill = new List<(int Id, string Text)>();
                        using (var readCmd = connection.CreateCommand())
                        {
                            readCmd.Transaction = transaction;
                            readCmd.CommandText = "SELECT Id, Text FROM Notes WHERE IsProtected = 0 AND (Title IS NULL OR Title = '') AND Text IS NOT NULL AND Text <> '';";
                            using var reader = readCmd.ExecuteReader();
                            while (reader.Read())
                            {
                                notesToBackfill.Add((reader.GetInt32(0), reader.GetString(1)));
                            }
                        }

                        foreach (var item in notesToBackfill)
                        {
                            var derivedTitle = QuickNotes.App.Helpers.NoteTitleHelper.DeriveTitleFromText(item.Text);
                            if (!string.IsNullOrEmpty(derivedTitle))
                            {
                                using var updateCmd = connection.CreateCommand();
                                updateCmd.Transaction = transaction;
                                updateCmd.CommandText = "UPDATE Notes SET Title = @title WHERE Id = @id;";
                                var pTitle = updateCmd.CreateParameter();
                                pTitle.ParameterName = "@title";
                                pTitle.Value = derivedTitle;
                                updateCmd.Parameters.Add(pTitle);

                                var pId = updateCmd.CreateParameter();
                                pId.ParameterName = "@id";
                                pId.Value = item.Id;
                                updateCmd.Parameters.Add(pId);

                                updateCmd.ExecuteNonQuery();
                            }
                        }
                    }

                    if (revisionsTableExistsV12)
                    {
                        EnsureColumnExists(connection, transaction, "NoteRevisions", "Title", "TEXT NOT NULL DEFAULT ''");

                        var revsToBackfill = new List<(int Id, string Text)>();
                        using (var readCmd = connection.CreateCommand())
                        {
                            readCmd.Transaction = transaction;
                            readCmd.CommandText = "SELECT Id, Text FROM NoteRevisions WHERE IsProtected = 0 AND (Title IS NULL OR Title = '') AND Text IS NOT NULL AND Text <> '';";
                            using var reader = readCmd.ExecuteReader();
                            while (reader.Read())
                            {
                                revsToBackfill.Add((reader.GetInt32(0), reader.GetString(1)));
                            }
                        }

                        foreach (var item in revsToBackfill)
                        {
                            var derivedTitle = QuickNotes.App.Helpers.NoteTitleHelper.DeriveTitleFromText(item.Text);
                            if (!string.IsNullOrEmpty(derivedTitle))
                            {
                                using var updateCmd = connection.CreateCommand();
                                updateCmd.Transaction = transaction;
                                updateCmd.CommandText = "UPDATE NoteRevisions SET Title = @title WHERE Id = @id;";
                                var pTitle = updateCmd.CreateParameter();
                                pTitle.ParameterName = "@title";
                                pTitle.Value = derivedTitle;
                                updateCmd.Parameters.Add(pTitle);

                                var pId = updateCmd.CreateParameter();
                                pId.ParameterName = "@id";
                                pId.Value = item.Id;
                                updateCmd.Parameters.Add(pId);

                                updateCmd.ExecuteNonQuery();
                            }
                        }
                    }

                    // Rebuild NotesFts table if it exists without Title column
                    if (ftsTableExistsV12 && !ColumnExists(connection, "NotesFts", "Title"))
                    {
                        using var dropCmd = connection.CreateCommand();
                        dropCmd.Transaction = transaction;
                        dropCmd.CommandText = @"
                            DROP TABLE IF EXISTS NotesFts;
                            CREATE VIRTUAL TABLE IF NOT EXISTS NotesFts USING fts5(NoteId UNINDEXED, Title, Text);

                            DROP TRIGGER IF EXISTS Notes_ai;
                            CREATE TRIGGER Notes_ai AFTER INSERT ON Notes BEGIN
                                INSERT INTO NotesFts(NoteId, Title, Text) SELECT new.Id, coalesce(new.Title, ''), new.Text WHERE new.IsProtected = 0;
                            END;

                            DROP TRIGGER IF EXISTS Notes_ad;
                            CREATE TRIGGER Notes_ad AFTER DELETE ON Notes BEGIN
                                DELETE FROM NotesFts WHERE NoteId = old.Id;
                            END;

                            DROP TRIGGER IF EXISTS Notes_au;
                            CREATE TRIGGER Notes_au AFTER UPDATE ON Notes BEGIN
                                DELETE FROM NotesFts WHERE NoteId = old.Id;
                                INSERT INTO NotesFts(NoteId, Title, Text) SELECT new.Id, coalesce(new.Title, ''), new.Text WHERE new.IsProtected = 0;
                            END;

                            DELETE FROM NotesFts WHERE NoteId IN (SELECT Id FROM Notes WHERE IsProtected = 1);

                            INSERT INTO NotesFts(NoteId, Title, Text)
                            SELECT Id, coalesce(Title, ''), Text FROM Notes
                            WHERE IsProtected = 0
                              AND Id NOT IN (SELECT NoteId FROM NotesFts);
                        ";
                        dropCmd.ExecuteNonQuery();
                    }

                    using (var verCmd = connection.CreateCommand())
                    {
                        verCmd.Transaction = transaction;
                        verCmd.CommandText = "PRAGMA user_version = 12;";
                        verCmd.ExecuteNonQuery();
                    }

                    transaction.Commit();
                    currentVersion = 12;
                }
                catch (Exception ex)
                {
                    transaction.Rollback();
                    ErrorLogService.Write("SchemaUpgradeV12", ex);
                    throw;
                }
            }

            // Version 13: stable import source fingerprint for idempotent HTML/Markdown migration.
            bool notesNeedV13 = TableExists(connection, "Notes")
                && !ColumnExists(connection, "Notes", "ImportSourceFingerprint");
            if (currentVersion < 13 || notesNeedV13)
            {
                using var transaction = connection.BeginTransaction();
                try
                {
                    EnsureColumnExists(connection, transaction, "Notes", "ImportSourceFingerprint", "TEXT NULL");
                    EnsureColumnExists(connection, transaction, "Notes", "ImportSourceRelativePath", "TEXT NULL");
                    using (var idx = connection.CreateCommand())
                    {
                        idx.Transaction = transaction;
                        idx.CommandText = @"
                            CREATE UNIQUE INDEX IF NOT EXISTS IX_Notes_ImportSourceFingerprint
                            ON Notes(ImportSourceFingerprint)
                            WHERE ImportSourceFingerprint IS NOT NULL AND DeletedAt IS NULL;
                            CREATE INDEX IF NOT EXISTS IX_Notes_ImportSourceRelativePath
                            ON Notes(ImportSourceRelativePath);
                        ";
                        idx.ExecuteNonQuery();
                    }

                    using (var verCmd = connection.CreateCommand())
                    {
                        verCmd.Transaction = transaction;
                        verCmd.CommandText = "PRAGMA user_version = 13;";
                        verCmd.ExecuteNonQuery();
                    }

                    transaction.Commit();
                    currentVersion = 13;
                }
                catch (Exception ex)
                {
                    transaction.Rollback();
                    ErrorLogService.Write("SchemaUpgradeV13", ex);
                    throw;
                }
            }

            // Version 14: explicit versioned KDF descriptor column per encrypted envelope row.
            // Nullable TEXT: NULL/'' means a legacy envelope that still uses ProtectedKdfIterations
            // exactly. Adding the column never rewrites envelopes, so legacy ciphertext stays
            // byte-for-byte readable; the descriptor appears on the next authenticated write.
            bool notesNeedV14 = TableExists(connection, "Notes")
                && !ColumnExists(connection, "Notes", "ProtectedKdfDescriptor");
            bool revisionsNeedV14 = TableExists(connection, "NoteRevisions")
                && !ColumnExists(connection, "NoteRevisions", "ProtectedKdfDescriptor");
            bool attachmentsNeedV14 = TableExists(connection, "NoteAttachments")
                && !ColumnExists(connection, "NoteAttachments", "ProtectedKdfDescriptor");

            if (currentVersion < 14 || notesNeedV14 || revisionsNeedV14 || attachmentsNeedV14)
            {
                using var transaction = connection.BeginTransaction();
                try
                {
                    EnsureColumnExists(connection, transaction, "Notes", "ProtectedKdfDescriptor", "TEXT NULL");
                    EnsureColumnExists(connection, transaction, "NoteRevisions", "ProtectedKdfDescriptor", "TEXT NULL");
                    EnsureColumnExists(connection, transaction, "NoteAttachments", "ProtectedKdfDescriptor", "TEXT NULL");

                    using (var verCmd = connection.CreateCommand())
                    {
                        verCmd.Transaction = transaction;
                        verCmd.CommandText = "PRAGMA user_version = 14;";
                        verCmd.ExecuteNonQuery();
                    }

                    transaction.Commit();
                    currentVersion = 14;
                }
                catch (Exception ex)
                {
                    transaction.Rollback();
                    ErrorLogService.Write("SchemaUpgradeV14", ex);
                    throw;
                }
            }
        }
        finally
        {
            if (!wasOpen) connection.Close();
        }
    }

    private static void MigrateLegacyWikiLinks(System.Data.Common.DbConnection connection, System.Data.Common.DbTransaction transaction)
    {
        if (!TableExists(connection, "Notes") || !ColumnExists(connection, "Notes", "SyncId"))
        {
            return;
        }

        var idToSyncId = new Dictionary<int, Guid>();
        using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = "SELECT Id, SyncId FROM Notes;";
            using var reader = select.ExecuteReader();
            while (reader.Read())
            {
                int id = reader.GetInt32(0);
                if (reader.IsDBNull(1))
                {
                    continue;
                }

                if (Guid.TryParse(reader.GetString(1), out var syncId) && syncId != Guid.Empty)
                {
                    idToSyncId[id] = syncId;
                }
            }
        }

        if (idToSyncId.Count == 0)
        {
            return;
        }

        var linker = new NoteLinkService();
        var updates = new List<(int Id, string Text)>();
        using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = "SELECT Id, Text FROM Notes WHERE Text IS NOT NULL AND Text LIKE '%[[%]]%';";
            using var reader = select.ExecuteReader();
            while (reader.Read())
            {
                int id = reader.GetInt32(0);
                string text = reader.GetString(1);
                string rewritten = linker.RewriteLegacyLinks(text, idToSyncId);
                if (!string.Equals(text, rewritten, StringComparison.Ordinal))
                {
                    updates.Add((id, rewritten));
                }
            }
        }

        foreach (var (id, text) in updates)
        {
            using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = "UPDATE Notes SET Text = @text WHERE Id = @id;";
            var pText = update.CreateParameter();
            pText.ParameterName = "@text";
            pText.Value = text;
            update.Parameters.Add(pText);
            var pId = update.CreateParameter();
            pId.ParameterName = "@id";
            pId.Value = id;
            update.Parameters.Add(pId);
            update.ExecuteNonQuery();
        }
    }

    private static void EnsureColumnExists(System.Data.Common.DbConnection connection, System.Data.Common.DbTransaction transaction, string tableName, string columnName, string columnDefinition)
    {
        if (!ColumnExists(connection, tableName, columnName))
        {
            using var cmd = connection.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = $"ALTER TABLE [{tableName}] ADD COLUMN [{columnName}] {columnDefinition};";
            cmd.ExecuteNonQuery();
        }
    }

    private static void EnsureSyncIdColumnAndPopulate(System.Data.Common.DbConnection connection, System.Data.Common.DbTransaction transaction, string tableName)
    {
        if (!ColumnExists(connection, tableName, "SyncId"))
        {
            using var alterCmd = connection.CreateCommand();
            alterCmd.Transaction = transaction;
            alterCmd.CommandText = $"ALTER TABLE [{tableName}] ADD COLUMN SyncId TEXT NULL;";
            alterCmd.ExecuteNonQuery();
        }

        var idsToUpdate = new System.Collections.Generic.List<int>();
        using (var selectCmd = connection.CreateCommand())
        {
            selectCmd.Transaction = transaction;
            selectCmd.CommandText = $"SELECT Id FROM [{tableName}] WHERE SyncId IS NULL OR SyncId = '';";
            using var reader = selectCmd.ExecuteReader();
            while (reader.Read())
            {
                idsToUpdate.Add(reader.GetInt32(0));
            }
        }

        foreach (var id in idsToUpdate)
        {
            using var updateCmd = connection.CreateCommand();
            updateCmd.Transaction = transaction;
            updateCmd.CommandText = $"UPDATE [{tableName}] SET SyncId = @syncId WHERE Id = @id;";
            var pSyncId = updateCmd.CreateParameter();
            pSyncId.ParameterName = "@syncId";
            pSyncId.Value = Guid.NewGuid().ToString().ToUpperInvariant();
            updateCmd.Parameters.Add(pSyncId);

            var pId = updateCmd.CreateParameter();
            pId.ParameterName = "@id";
            pId.Value = id;
            updateCmd.Parameters.Add(pId);

            updateCmd.ExecuteNonQuery();
        }
    }

    private static void PopulateInitialSyncEntityStates(System.Data.Common.DbConnection connection, System.Data.Common.DbTransaction transaction)
    {
        if (TableExists(connection, "Notes") && ColumnExists(connection, "Notes", "SyncId"))
        {
            var notesToSync = new System.Collections.Generic.List<(string SyncId, string UpdatedAt, bool IsDeleted, string? DeletedAt)>();
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = @"
                    SELECT SyncId, UpdatedAt, DeletedAt 
                    FROM Notes 
                    WHERE SyncId IS NOT NULL AND SyncId <> ''
                      AND upper(SyncId) NOT IN (SELECT upper(SyncId) FROM SyncEntityStates);";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    string syncId = reader.GetString(0);
                    string updatedAt = reader.IsDBNull(1) ? DateTime.UtcNow.ToString("O") : reader.GetString(1);
                    string? deletedAt = reader.IsDBNull(2) ? null : reader.GetString(2);
                    notesToSync.Add((syncId, updatedAt, !string.IsNullOrEmpty(deletedAt), deletedAt));
                }
            }

            foreach (var note in notesToSync)
            {
                InsertSyncEntityState(connection, transaction, note.SyncId, "Note", note.UpdatedAt, note.IsDeleted, note.DeletedAt);
            }
        }

        if (TableExists(connection, "Tags") && ColumnExists(connection, "Tags", "SyncId"))
        {
            var tagsToSync = new System.Collections.Generic.List<string>();
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = @"
                    SELECT SyncId 
                    FROM Tags 
                    WHERE SyncId IS NOT NULL AND SyncId <> ''
                      AND upper(SyncId) NOT IN (SELECT upper(SyncId) FROM SyncEntityStates);";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    tagsToSync.Add(reader.GetString(0));
                }
            }

            foreach (var syncId in tagsToSync)
            {
                InsertSyncEntityState(connection, transaction, syncId, "Tag", DateTime.UtcNow.ToString("O"), false, null);
            }
        }

        if (TableExists(connection, "NoteTemplates") && ColumnExists(connection, "NoteTemplates", "SyncId"))
        {
            var templatesToSync = new System.Collections.Generic.List<(string SyncId, string UpdatedAt)>();
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = @"
                    SELECT SyncId, UpdatedAt 
                    FROM NoteTemplates 
                    WHERE SyncId IS NOT NULL AND SyncId <> ''
                      AND upper(SyncId) NOT IN (SELECT upper(SyncId) FROM SyncEntityStates);";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    string syncId = reader.GetString(0);
                    string updatedAt = reader.IsDBNull(1) ? DateTime.UtcNow.ToString("O") : reader.GetString(1);
                    templatesToSync.Add((syncId, updatedAt));
                }
            }

            foreach (var tmpl in templatesToSync)
            {
                InsertSyncEntityState(connection, transaction, tmpl.SyncId, "NoteTemplate", tmpl.UpdatedAt, false, null);
            }
        }

        if (TableExists(connection, "NoteAttachments") && ColumnExists(connection, "NoteAttachments", "SyncId"))
        {
            var attachmentsToSync = new System.Collections.Generic.List<(string SyncId, string CreatedAt)>();
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = @"
                    SELECT SyncId, CreatedAt 
                    FROM NoteAttachments 
                    WHERE SyncId IS NOT NULL AND SyncId <> ''
                      AND upper(SyncId) NOT IN (SELECT upper(SyncId) FROM SyncEntityStates);";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    string syncId = reader.GetString(0);
                    string createdAt = reader.IsDBNull(1) ? DateTime.UtcNow.ToString("O") : reader.GetString(1);
                    attachmentsToSync.Add((syncId, createdAt));
                }
            }

            foreach (var att in attachmentsToSync)
            {
                InsertSyncEntityState(connection, transaction, att.SyncId, "NoteAttachment", att.CreatedAt, false, null);
            }
        }
    }

    private static void InsertSyncEntityState(
        System.Data.Common.DbConnection connection,
        System.Data.Common.DbTransaction transaction,
        string syncId,
        string entityType,
        string updatedAtUtc,
        bool isDeleted,
        string? deletedAtUtc)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        bool hasContentHash = ColumnExists(connection, "SyncEntityStates", "ContentHash");
        if (hasContentHash)
        {
            cmd.CommandText = @"
                INSERT OR IGNORE INTO SyncEntityStates 
                (SyncId, EntityType, RevisionId, ParentRevisionId, DeviceId, UpdatedAtUtc, IsDeleted, DeletedAtUtc, ContentHash)
                VALUES (@syncId, @entityType, @revisionId, NULL, @deviceId, @updatedAtUtc, @isDeleted, @deletedAtUtc, NULL);";
        }
        else
        {
            cmd.CommandText = @"
                INSERT OR IGNORE INTO SyncEntityStates 
                (SyncId, EntityType, RevisionId, ParentRevisionId, DeviceId, UpdatedAtUtc, IsDeleted, DeletedAtUtc)
                VALUES (@syncId, @entityType, @revisionId, NULL, @deviceId, @updatedAtUtc, @isDeleted, @deletedAtUtc);";
        }

        string normalizedSyncId = Guid.TryParse(syncId, out var parsedGuid)
            ? parsedGuid.ToString().ToUpperInvariant()
            : syncId;

        var pSyncId = cmd.CreateParameter();
        pSyncId.ParameterName = "@syncId";
        pSyncId.Value = normalizedSyncId;
        cmd.Parameters.Add(pSyncId);

        var pType = cmd.CreateParameter();
        pType.ParameterName = "@entityType";
        pType.Value = entityType;
        cmd.Parameters.Add(pType);

        var pRev = cmd.CreateParameter();
        pRev.ParameterName = "@revisionId";
        pRev.Value = Guid.NewGuid().ToString().ToUpperInvariant();
        cmd.Parameters.Add(pRev);

        var pDev = cmd.CreateParameter();
        pDev.ParameterName = "@deviceId";
        pDev.Value = Guid.Empty.ToString().ToUpperInvariant();
        cmd.Parameters.Add(pDev);

        var pUpd = cmd.CreateParameter();
        pUpd.ParameterName = "@updatedAtUtc";
        pUpd.Value = updatedAtUtc;
        cmd.Parameters.Add(pUpd);

        var pDel = cmd.CreateParameter();
        pDel.ParameterName = "@isDeleted";
        pDel.Value = isDeleted ? 1 : 0;
        cmd.Parameters.Add(pDel);

        var pDelAt = cmd.CreateParameter();
        pDelAt.ParameterName = "@deletedAtUtc";
        pDelAt.Value = (object?)deletedAtUtc ?? DBNull.Value;
        cmd.Parameters.Add(pDelAt);

        cmd.ExecuteNonQuery();
    }
}
