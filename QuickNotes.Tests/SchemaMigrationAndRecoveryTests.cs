using System;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using QuickNotes.App.Data;
using QuickNotes.App.Models;
using QuickNotes.App.Services;
using Xunit;

namespace QuickNotes.Tests;

/// <summary>
/// M1 migration/recovery coverage against the upgrade path actually implemented in
/// <see cref="DbInitializer.ApplySchemaUpgrades"/>. Supported starting
/// <c>PRAGMA user_version</c> values are 0 through <see cref="DbInitializer.CurrentSchemaVersion"/>.
/// Historical DDL is taken from that migrator and existing fixtures, not invented schemas.
/// </summary>
[TestCategory(TestCategories.Integration)]
public sealed class SchemaMigrationAndRecoveryTests
{
    public const int MinSupportedUserVersion = 0;

    private const string NoteSyncId = "11111111-1111-1111-1111-111111111111";
    private const string TagSyncId = "22222222-2222-2222-2222-222222222222";
    private const string ChildTagSyncId = "22222222-2222-2222-2222-222222222223";
    private const string TemplateSyncId = "33333333-3333-3333-3333-333333333333";
    private const string AttachmentSyncId = "44444444-4444-4444-4444-444444444444";
    private const string DeviceId = "55555555-5555-5555-5555-555555555555";
    private const string Ts = "2026-03-01 12:00:00";
    private const string AttachmentSha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string ProtectedCipher = "Y2lwaGVydGV4dA==";
    private const string UnprotectedText = "legacy filled note";
    private const string LinkedPrefix = "see ";

    [Fact]
    public void SupportedSchemaRange_IsUserVersionZeroThroughCurrent()
    {
        Assert.Equal(14, DbInitializer.CurrentSchemaVersion);
        Assert.Equal(0, MinSupportedUserVersion);
        Assert.True(MinSupportedUserVersion <= DbInitializer.CurrentSchemaVersion);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(11)]
    [InlineData(12)]
    [InlineData(13)]
    [InlineData(14)]
    public void FilledDatabase_FromEachSupportedSchema_UpgradesToCurrent_PreservesData_AndIsIdempotent(int fromVersion)
    {
        Assert.InRange(fromVersion, MinSupportedUserVersion, DbInitializer.CurrentSchemaVersion);

        string dbPath = NewDbPath($"schema_v{fromVersion}");
        try
        {
            CreateFilledDatabaseAtVersion(dbPath, fromVersion);

            int backupCount = 0;
            using (var context = new QuickNotesDbContext(dbPath))
            {
                DbInitializer.Initialize(context, () => backupCount++);
            }

            if (fromVersion < DbInitializer.CurrentSchemaVersion)
            {
                Assert.True(backupCount >= 1, "Filled pre-current database must request a pre-upgrade backup.");
            }

            AssertUpgradedFilledDatabase(dbPath, fromVersion);

            int backupCountAfterFirst = backupCount;
            using (var context = new QuickNotesDbContext(dbPath))
            {
                DbInitializer.Initialize(context, () => backupCount++);
            }

            Assert.Equal(backupCountAfterFirst, backupCount);
            AssertUpgradedFilledDatabase(dbPath, fromVersion);
        }
        finally
        {
            CleanupDb(dbPath);
        }
    }

    [Fact]
    public void PartiallyAppliedV10Columns_WithUserVersionStill9_CompletesOnRerunWithoutDataLoss()
    {
        string dbPath = NewDbPath("partial_v10");
        try
        {
            CreateFilledDatabaseAtVersion(dbPath, 9);
            using (var conn = new SqliteConnection($"Data Source={dbPath}"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "ALTER TABLE Notes ADD COLUMN IsProtected INTEGER NOT NULL DEFAULT 0;";
                cmd.ExecuteNonQuery();
            }

            using (var context = new QuickNotesDbContext(dbPath))
            {
                DbInitializer.Initialize(context);
            }

            AssertUpgradedFilledDatabase(dbPath, fromVersion: 9);

            using (var context = new QuickNotesDbContext(dbPath))
            {
                DbInitializer.Initialize(context);
            }

            AssertUpgradedFilledDatabase(dbPath, fromVersion: 9);
        }
        finally
        {
            CleanupDb(dbPath);
        }
    }

    [Fact]
    public void PartiallyAppliedV12Columns_WithUserVersionStill11_CompletesOnRerunWithoutDataLoss()
    {
        string dbPath = NewDbPath("partial_v12");
        try
        {
            CreateFilledDatabaseAtVersion(dbPath, 11);
            using (var conn = new SqliteConnection($"Data Source={dbPath}"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "ALTER TABLE Notes ADD COLUMN Title TEXT NOT NULL DEFAULT '';";
                cmd.ExecuteNonQuery();
            }

            using (var context = new QuickNotesDbContext(dbPath))
            {
                DbInitializer.Initialize(context);
            }

            AssertUpgradedFilledDatabase(dbPath, fromVersion: 11);

            using (var context = new QuickNotesDbContext(dbPath))
            {
                DbInitializer.Initialize(context);
            }

            AssertUpgradedFilledDatabase(dbPath, fromVersion: 11);
        }
        finally
        {
            CleanupDb(dbPath);
        }
    }

    [Fact]
    public void PartiallyAppliedV6AndV8_MissingContentHashAndPendingPayload_CompletesOnRerun()
    {
        string dbPath = NewDbPath("partial_v6_v8");
        try
        {
            CreateFilledDatabaseAtVersion(dbPath, 5);
            using (var conn = new SqliteConnection($"Data Source={dbPath}"))
            {
                conn.Open();
                Assert.True(DbInitializer.TableExists(conn, "SyncEntityStates"));
                Assert.False(DbInitializer.ColumnExists(conn, "SyncEntityStates", "ContentHash"));
            }

            using (var context = new QuickNotesDbContext(dbPath))
            {
                DbInitializer.Initialize(context);
            }

            using (var conn = new SqliteConnection($"Data Source={dbPath}"))
            {
                conn.Open();
                Assert.Equal(DbInitializer.CurrentSchemaVersion, ReadUserVersion(conn));
                Assert.True(DbInitializer.ColumnExists(conn, "SyncEntityStates", "ContentHash"));
                Assert.True(DbInitializer.ColumnExists(conn, "SyncLocalStates", "PendingPayloadBytes"));
            }

            using (var context = new QuickNotesDbContext(dbPath))
            {
                DbInitializer.Initialize(context);
            }

            AssertUpgradedFilledDatabase(dbPath, fromVersion: 5);
        }
        finally
        {
            CleanupDb(dbPath);
        }
    }

    [Fact]
    public void BackupService_RestoreAfterSchemaUpgrade_ReturnsPreUpgradeCopy_ThenInitializerReappliesUpgrade()
    {
        string root = Path.Combine(Path.GetTempPath(), $"qn_schema_recovery_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        string dbPath = Path.Combine(root, "quicknotes.db");
        string backupsDir = Path.Combine(root, "Backups");
        const int sourceVersion = 4;

        try
        {
            CreateFilledDatabaseAtVersion(dbPath, sourceVersion);

            var backupService = new BackupService(dbPath, backupsDir);
            string backupPath = backupService.CreateBackup();
            Assert.True(File.Exists(backupPath));

            var recovery = new DatabaseRecoveryService(dbPath, backupService);
            Assert.True(recovery.CheckBackupIntegrity(backupPath, out var backupIntegrity), backupIntegrity);

            using (var context = new QuickNotesDbContext(dbPath))
            {
                DbInitializer.Initialize(context);
            }

            using (var upgraded = new SqliteConnection($"Data Source={dbPath}"))
            {
                upgraded.Open();
                Assert.Equal(DbInitializer.CurrentSchemaVersion, ReadUserVersion(upgraded));
                Assert.Equal(UnprotectedText, ReadNoteText(conn: upgraded, noteId: 1));
            }

            SqliteConnection.ClearAllPools();

            var result = recovery.PerformRecovery(backupPath);
            Assert.True(result.Success, result.ErrorMessage);
            Assert.True(File.Exists(result.PreservedCorruptPath));

            SqliteConnection.ClearAllPools();

            using (var restored = new SqliteConnection($"Data Source={dbPath}"))
            {
                restored.Open();
                Assert.Equal(sourceVersion, ReadUserVersion(restored));
                Assert.Equal(UnprotectedText, ReadNoteText(restored, 1));
                Assert.False(DbInitializer.ColumnExists(restored, "Notes", "SyncId"));
                Assert.False(DbInitializer.ColumnExists(restored, "Notes", "IsProtected"));
            }

            using (var context = new QuickNotesDbContext(dbPath))
            {
                DbInitializer.Initialize(context);
            }

            AssertUpgradedFilledDatabase(dbPath, sourceVersion);
        }
        finally
        {
            CleanupDb(dbPath);
            try
            {
                if (Directory.Exists(root))
                    Directory.Delete(root, recursive: true);
            }
            catch
            {
            }
        }
    }

    private static void AssertUpgradedFilledDatabase(string dbPath, int fromVersion)
    {
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();

        Assert.Equal("ok", ReadIntegrity(conn));
        Assert.Equal(DbInitializer.CurrentSchemaVersion, ReadUserVersion(conn));

        Assert.True(DbInitializer.ColumnExists(conn, "Notes", "IsPinned"));
        Assert.True(DbInitializer.ColumnExists(conn, "Notes", "SourceProcessName"));
        Assert.True(DbInitializer.ColumnExists(conn, "Notes", "SyncId"));
        Assert.True(DbInitializer.ColumnExists(conn, "Notes", "IsProtected"));
        Assert.True(DbInitializer.ColumnExists(conn, "Notes", "ProtectedOriginalSyncId"));
        Assert.True(DbInitializer.ColumnExists(conn, "Notes", "Title"));
        Assert.True(DbInitializer.TableExists(conn, "NoteRevisions"));
        Assert.True(DbInitializer.ColumnExists(conn, "NoteRevisions", "Title"));
        Assert.True(DbInitializer.TableExists(conn, "NoteAttachments"));
        Assert.True(DbInitializer.TableExists(conn, "NoteTemplates"));
        Assert.True(DbInitializer.TableExists(conn, "SyncEntityStates"));
        Assert.True(DbInitializer.ColumnExists(conn, "SyncEntityStates", "ContentHash"));
        Assert.True(DbInitializer.TableExists(conn, "SyncDeviceStates"));
        Assert.True(DbInitializer.TableExists(conn, "SyncLocalStates"));
        Assert.True(DbInitializer.ColumnExists(conn, "SyncLocalStates", "PendingPayloadBytes"));
        Assert.True(DbInitializer.TableExists(conn, "SyncConflicts"));

        Assert.Equal(UnprotectedText, ReadNoteText(conn, 1));
        Assert.Equal(UnprotectedText, ScalarString(conn, "SELECT Title FROM Notes WHERE Id = 1;"));
        Assert.Equal(fromVersion >= 1 ? 1L : 0L, ScalarLong(conn, "SELECT IsPinned FROM Notes WHERE Id = 1;"));
        Assert.Equal("Work", ScalarString(conn, "SELECT Name FROM Tags WHERE Id = 1;"));
        Assert.Equal("Child", ScalarString(conn, "SELECT Name FROM Tags WHERE Id = 2;"));
        Assert.Equal(1L, ScalarLong(conn, "SELECT ParentTagId FROM Tags WHERE Id = 2;"));
        Assert.Equal("wrk", ScalarString(conn, "SELECT Value FROM TagSynonyms WHERE Id = 1;"));
        Assert.Equal(1L, ScalarLong(conn, "SELECT COUNT(*) FROM NoteTags WHERE NoteId = 1 AND TagId = 1;"));

        if (fromVersion >= 2)
        {
            Assert.Equal("notepad", ScalarString(conn, "SELECT SourceProcessName FROM Notes WHERE Id = 1;"));
        }

        if (fromVersion >= 3)
        {
            Assert.Equal(UnprotectedText, ScalarString(conn, "SELECT Text FROM NoteRevisions WHERE Id = 1;"));
            Assert.Equal(UnprotectedText, ScalarString(conn, "SELECT Title FROM NoteRevisions WHERE Id = 1;"));
            Assert.Equal(AttachmentSha, ScalarString(conn, "SELECT Sha256 FROM NoteAttachments WHERE Id = 1;"));
            Assert.Equal("clip.txt", ScalarString(conn, "SELECT OriginalFileName FROM NoteAttachments WHERE Id = 1;"));
        }

        if (fromVersion >= 4)
        {
            Assert.Equal("Daily Plan", ScalarString(conn, "SELECT Title FROM NoteTemplates WHERE Id = 1;"));
            Assert.Equal(1L, ScalarLong(conn, "SELECT COUNT(*) FROM NoteTemplateTags WHERE TemplateId = 1 AND TagId = 1;"));
        }

        if (fromVersion >= 5)
        {
            Assert.Equal(NoteSyncId, ScalarString(conn, "SELECT SyncId FROM Notes WHERE Id = 1;"));
        }

        string linked = ReadNoteText(conn, 2);
        Assert.StartsWith(LinkedPrefix, linked, StringComparison.Ordinal);
        Assert.Contains("[[qn:", linked, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("[[1]]", linked, StringComparison.Ordinal);

        if (fromVersion >= 10)
        {
            Assert.Equal(3L, ScalarLong(conn, "SELECT COUNT(*) FROM Notes;"));
            Assert.Equal(1L, ScalarLong(conn, "SELECT IsProtected FROM Notes WHERE Id = 3;"));
            Assert.Equal(string.Empty, ScalarString(conn, "SELECT Title FROM Notes WHERE Id = 3;"));
            Assert.Equal(string.Empty, ScalarString(conn, "SELECT Title FROM NoteRevisions WHERE NoteId = 3;"));
            Assert.Equal(ProtectedCipher, ScalarString(conn, "SELECT ProtectedCiphertextBase64 FROM Notes WHERE Id = 3;"));
            Assert.Equal(ProtectedCipher, ScalarString(conn, "SELECT ProtectedCiphertextBase64 FROM NoteRevisions WHERE NoteId = 3;"));
            Assert.Equal(ProtectedCipher, ScalarString(conn, "SELECT ProtectedCiphertextBase64 FROM NoteAttachments WHERE NoteId = 3;"));
            Assert.Equal(0L, ScalarLong(conn, "SELECT COUNT(*) FROM NotesFts WHERE NoteId = 3;"));
        }
        else
        {
            Assert.Equal(2L, ScalarLong(conn, "SELECT COUNT(*) FROM Notes;"));
        }

        Assert.Equal(1L, ScalarLong(conn, "SELECT COUNT(*) FROM NotesFts WHERE NoteId = 1 AND Title = 'legacy filled note';"));
        Assert.True(DbInitializer.ColumnExists(conn, "Notes", "ImportSourceFingerprint"));
        Assert.True(DbInitializer.ColumnExists(conn, "Notes", "ImportSourceRelativePath"));
        Assert.True(DbInitializer.ColumnExists(conn, "Notes", "ProtectedKdfDescriptor"));
        Assert.True(DbInitializer.ColumnExists(conn, "NoteRevisions", "ProtectedKdfDescriptor"));
        Assert.True(DbInitializer.ColumnExists(conn, "NoteAttachments", "ProtectedKdfDescriptor"));

        using var ef = new QuickNotesDbContext(dbPath);
        var note = ef.Notes.AsNoTracking().Single(n => n.Id == 1);
        Assert.Equal(UnprotectedText, note.Text);
        Assert.False(note.IsProtected);
        var tags = ef.Tags.AsNoTracking().OrderBy(t => t.Id).ToList();
        Assert.Equal(2, tags.Count);
        Assert.Equal("Work", tags[0].Name);
        Assert.Equal(1, tags[1].ParentTagId);
    }

    private static void CreateFilledDatabaseAtVersion(string dbPath, int version)
    {
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = BuildSchemaSql(version);
        cmd.ExecuteNonQuery();
    }

    private static string BuildSchemaSql(int version)
    {
        var sql = new StringBuilder();

        sql.AppendLine("CREATE TABLE Notes (");
        sql.AppendLine("    Id INTEGER PRIMARY KEY AUTOINCREMENT,");
        if (version >= 5)
            sql.AppendLine("    SyncId TEXT NOT NULL,");
        sql.AppendLine("    Text TEXT NOT NULL,");
        sql.AppendLine("    CreatedAt TEXT NOT NULL,");
        sql.AppendLine("    UpdatedAt TEXT NOT NULL");
        if (version >= 1)
        {
            sql.AppendLine("    , IsPinned INTEGER NOT NULL DEFAULT 0");
            sql.AppendLine("    , IsFavorite INTEGER NOT NULL DEFAULT 0");
            sql.AppendLine("    , IsInbox INTEGER NOT NULL DEFAULT 0");
            sql.AppendLine("    , DeletedAt TEXT NULL");
        }
        if (version >= 2)
        {
            sql.AppendLine("    , SourceProcessName TEXT NULL");
            sql.AppendLine("    , SourceWindowTitle TEXT NULL");
            sql.AppendLine("    , SourceUrl TEXT NULL");
            sql.AppendLine("    , CapturedAt TEXT NULL");
        }
        if (version >= 10)
        {
            sql.AppendLine("    , IsProtected INTEGER NOT NULL DEFAULT 0");
            sql.AppendLine("    , ProtectedFormatVersion INTEGER NOT NULL DEFAULT 0");
            sql.AppendLine("    , ProtectedKdfIterations INTEGER NOT NULL DEFAULT 0");
            sql.AppendLine("    , ProtectedSaltBase64 TEXT NULL DEFAULT ''");
            sql.AppendLine("    , ProtectedNonceBase64 TEXT NULL DEFAULT ''");
            sql.AppendLine("    , ProtectedTagBase64 TEXT NULL DEFAULT ''");
            sql.AppendLine("    , ProtectedCiphertextBase64 TEXT NULL DEFAULT ''");
        }
        if (version >= 11)
            sql.AppendLine("    , ProtectedOriginalSyncId TEXT NULL");
        if (version >= 12)
            sql.AppendLine("    , Title TEXT NOT NULL DEFAULT ''");
        if (version >= 13)
        {
            sql.AppendLine("    , ImportSourceFingerprint TEXT NULL");
            sql.AppendLine("    , ImportSourceRelativePath TEXT NULL");
        }
        if (version >= 14)
            sql.AppendLine("    , ProtectedKdfDescriptor TEXT NULL");
        sql.AppendLine(");");

        sql.AppendLine("CREATE TABLE Tags (");
        sql.AppendLine("    Id INTEGER PRIMARY KEY AUTOINCREMENT,");
        if (version >= 5)
            sql.AppendLine("    SyncId TEXT NOT NULL,");
        sql.AppendLine("    Name TEXT NOT NULL,");
        sql.AppendLine("    ParentTagId INTEGER NULL");
        sql.AppendLine(");");

        sql.AppendLine(@"
            CREATE TABLE TagSynonyms (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                TagId INTEGER NOT NULL,
                Value TEXT
            );
            CREATE TABLE NoteTags (
                NoteId INTEGER NOT NULL,
                TagId INTEGER NOT NULL,
                Origin INTEGER NOT NULL DEFAULT 0,
                IsSuppressed INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (NoteId, TagId)
            );
        ");

        if (version >= 3)
        {
            sql.AppendLine("CREATE TABLE NoteRevisions (");
            sql.AppendLine("    Id INTEGER PRIMARY KEY AUTOINCREMENT,");
            sql.AppendLine("    NoteId INTEGER NOT NULL,");
            sql.AppendLine("    CreatedAt TEXT NOT NULL,");
            sql.AppendLine("    Text TEXT NOT NULL,");
            sql.AppendLine("    TagsJson TEXT NOT NULL");
            if (version >= 10)
            {
                sql.AppendLine("    , IsProtected INTEGER NOT NULL DEFAULT 0");
                sql.AppendLine("    , ProtectedFormatVersion INTEGER NOT NULL DEFAULT 0");
                sql.AppendLine("    , ProtectedKdfIterations INTEGER NOT NULL DEFAULT 0");
                sql.AppendLine("    , ProtectedSaltBase64 TEXT NULL DEFAULT ''");
                sql.AppendLine("    , ProtectedNonceBase64 TEXT NULL DEFAULT ''");
                sql.AppendLine("    , ProtectedTagBase64 TEXT NULL DEFAULT ''");
                sql.AppendLine("    , ProtectedCiphertextBase64 TEXT NULL DEFAULT ''");
            }
            if (version >= 12)
            {
                sql.AppendLine("    , Title TEXT NOT NULL DEFAULT ''");
            }
            sql.AppendLine(");");

            sql.AppendLine("CREATE TABLE NoteAttachments (");
            sql.AppendLine("    Id INTEGER PRIMARY KEY AUTOINCREMENT,");
            sql.AppendLine("    NoteId INTEGER NOT NULL,");
            if (version >= 5)
                sql.AppendLine("    SyncId TEXT NOT NULL,");
            sql.AppendLine("    OriginalFileName TEXT NOT NULL,");
            sql.AppendLine("    StoredFileName TEXT NOT NULL,");
            sql.AppendLine("    RelativePath TEXT NOT NULL,");
            sql.AppendLine("    ContentType TEXT NOT NULL,");
            sql.AppendLine("    Size INTEGER NOT NULL,");
            sql.AppendLine("    Sha256 TEXT NOT NULL,");
            sql.AppendLine("    CreatedAt TEXT NOT NULL");
            if (version >= 10)
            {
                sql.AppendLine("    , IsProtected INTEGER NOT NULL DEFAULT 0");
                sql.AppendLine("    , ProtectedFormatVersion INTEGER NOT NULL DEFAULT 0");
                sql.AppendLine("    , ProtectedKdfIterations INTEGER NOT NULL DEFAULT 0");
                sql.AppendLine("    , ProtectedSaltBase64 TEXT NULL DEFAULT ''");
                sql.AppendLine("    , ProtectedNonceBase64 TEXT NULL DEFAULT ''");
                sql.AppendLine("    , ProtectedTagBase64 TEXT NULL DEFAULT ''");
                sql.AppendLine("    , ProtectedCiphertextBase64 TEXT NULL DEFAULT ''");
            }
            sql.AppendLine(");");
        }

        if (version >= 4)
        {
            sql.AppendLine("CREATE TABLE NoteTemplates (");
            sql.AppendLine("    Id INTEGER PRIMARY KEY AUTOINCREMENT,");
            if (version >= 5)
                sql.AppendLine("    SyncId TEXT NOT NULL,");
            sql.AppendLine("    Title TEXT NOT NULL COLLATE NOCASE,");
            sql.AppendLine("    Text TEXT NOT NULL,");
            sql.AppendLine("    CreatedAt TEXT NOT NULL,");
            sql.AppendLine("    UpdatedAt TEXT NOT NULL");
            sql.AppendLine(");");
            sql.AppendLine(@"
                CREATE TABLE NoteTemplateTags (
                    TemplateId INTEGER NOT NULL,
                    TagId INTEGER NOT NULL,
                    PRIMARY KEY (TemplateId, TagId)
                );
            ");
            if (version >= 4)
            {
                sql.AppendLine("CREATE UNIQUE INDEX IX_NoteTemplates_Title ON NoteTemplates(Title COLLATE NOCASE);");
            }
        }

        if (version >= 5)
        {
            sql.AppendLine(@"
                CREATE UNIQUE INDEX IX_Notes_SyncId ON Notes(SyncId);
                CREATE UNIQUE INDEX IX_Tags_SyncId ON Tags(SyncId);
            ");
            if (version >= 4)
                sql.AppendLine("CREATE UNIQUE INDEX IX_NoteTemplates_SyncId ON NoteTemplates(SyncId);");
            if (version >= 3)
                sql.AppendLine("CREATE UNIQUE INDEX IX_NoteAttachments_SyncId ON NoteAttachments(SyncId);");

            sql.AppendLine("CREATE TABLE SyncEntityStates (");
            sql.AppendLine("    SyncId TEXT PRIMARY KEY COLLATE NOCASE,");
            sql.AppendLine("    EntityType TEXT NOT NULL,");
            sql.AppendLine("    RevisionId TEXT NOT NULL COLLATE NOCASE,");
            sql.AppendLine("    ParentRevisionId TEXT NULL COLLATE NOCASE,");
            sql.AppendLine("    DeviceId TEXT NOT NULL COLLATE NOCASE,");
            sql.AppendLine("    UpdatedAtUtc TEXT NOT NULL,");
            sql.AppendLine("    IsDeleted INTEGER NOT NULL DEFAULT 0,");
            sql.AppendLine("    DeletedAtUtc TEXT NULL");
            if (version >= 6)
                sql.AppendLine("    , ContentHash TEXT NULL");
            sql.AppendLine(");");
        }

        if (version >= 7)
        {
            sql.AppendLine(@"
                CREATE TABLE SyncDeviceStates (
                    DeviceId TEXT PRIMARY KEY COLLATE NOCASE,
                    LatestProcessedPackageId TEXT NULL COLLATE NOCASE,
                    LatestProcessedETag TEXT NULL,
                    LastSyncedAtUtc TEXT NOT NULL,
                    PackageCount INTEGER NOT NULL DEFAULT 0
                );
                CREATE TABLE SyncLocalStates (
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
                    PendingCreatedAtUtc TEXT NULL
            ");
            if (version >= 8)
                sql.AppendLine("    , PendingPayloadBytes BLOB NULL");
            sql.AppendLine(@"
                );
                CREATE TABLE SyncConflicts (
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
                CREATE UNIQUE INDEX IX_SyncConflicts_SyncId_RemoteRevisionId ON SyncConflicts(SyncId, RemoteRevisionId);
            ");
        }

        string note2Text = version >= 9
            ? LinkedPrefix + "[[qn:" + NoteSyncId + "]]"
            : LinkedPrefix + "[[1]]";

        if (version >= 5)
        {
            sql.AppendLine($@"
                INSERT INTO Notes (Id, SyncId, Text, CreatedAt, UpdatedAt{(version >= 1 ? ", IsPinned, IsFavorite, IsInbox, DeletedAt" : "")}{(version >= 2 ? ", SourceProcessName, SourceWindowTitle, SourceUrl, CapturedAt" : "")}{(version >= 10 ? ", IsProtected, ProtectedFormatVersion, ProtectedKdfIterations, ProtectedSaltBase64, ProtectedNonceBase64, ProtectedTagBase64, ProtectedCiphertextBase64" : "")}{(version >= 11 ? ", ProtectedOriginalSyncId" : "")}{(version >= 12 ? ", Title" : "")})
                VALUES (1, '{NoteSyncId}', '{UnprotectedText}', '{Ts}', '{Ts}'{(version >= 1 ? ", 1, 0, 0, NULL" : "")}{(version >= 2 ? $", 'notepad', 'legacy window', NULL, '{Ts}'" : "")}{(version >= 10 ? ", 0, 0, 0, '', '', '', ''" : "")}{(version >= 11 ? ", NULL" : "")}{(version >= 12 ? $", '{UnprotectedText}'" : "")});
                INSERT INTO Notes (Id, SyncId, Text, CreatedAt, UpdatedAt{(version >= 1 ? ", IsPinned, IsFavorite, IsInbox, DeletedAt" : "")}{(version >= 2 ? ", SourceProcessName, SourceWindowTitle, SourceUrl, CapturedAt" : "")}{(version >= 10 ? ", IsProtected, ProtectedFormatVersion, ProtectedKdfIterations, ProtectedSaltBase64, ProtectedNonceBase64, ProtectedTagBase64, ProtectedCiphertextBase64" : "")}{(version >= 11 ? ", ProtectedOriginalSyncId" : "")}{(version >= 12 ? ", Title" : "")})
                VALUES (2, '66666666-6666-6666-6666-666666666666', '{note2Text}', '{Ts}', '{Ts}'{(version >= 1 ? ", 0, 0, 0, NULL" : "")}{(version >= 2 ? ", NULL, NULL, NULL, NULL" : "")}{(version >= 10 ? ", 0, 0, 0, '', '', '', ''" : "")}{(version >= 11 ? ", NULL" : "")}{(version >= 12 ? $", '{note2Text}'" : "")});
            ");
            sql.AppendLine($@"
                INSERT INTO Tags (Id, SyncId, Name, ParentTagId) VALUES (1, '{TagSyncId}', 'Work', NULL);
                INSERT INTO Tags (Id, SyncId, Name, ParentTagId) VALUES (2, '{ChildTagSyncId}', 'Child', 1);
            ");
        }
        else
        {
            sql.AppendLine($@"
                INSERT INTO Notes (Id, Text, CreatedAt, UpdatedAt{(version >= 1 ? ", IsPinned, IsFavorite, IsInbox, DeletedAt" : "")}{(version >= 2 ? ", SourceProcessName, SourceWindowTitle, SourceUrl, CapturedAt" : "")})
                VALUES (1, '{UnprotectedText}', '{Ts}', '{Ts}'{(version >= 1 ? ", 1, 0, 0, NULL" : "")}{(version >= 2 ? $", 'notepad', 'legacy window', NULL, '{Ts}'" : "")});
                INSERT INTO Notes (Id, Text, CreatedAt, UpdatedAt{(version >= 1 ? ", IsPinned, IsFavorite, IsInbox, DeletedAt" : "")}{(version >= 2 ? ", SourceProcessName, SourceWindowTitle, SourceUrl, CapturedAt" : "")})
                VALUES (2, '{note2Text}', '{Ts}', '{Ts}'{(version >= 1 ? ", 0, 0, 0, NULL" : "")}{(version >= 2 ? ", NULL, NULL, NULL, NULL" : "")});
                INSERT INTO Tags (Id, Name, ParentTagId) VALUES (1, 'Work', NULL);
                INSERT INTO Tags (Id, Name, ParentTagId) VALUES (2, 'Child', 1);
            ");
        }

        sql.AppendLine(@"
            INSERT INTO TagSynonyms (Id, TagId, Value) VALUES (1, 1, 'wrk');
            INSERT INTO NoteTags (NoteId, TagId, Origin, IsSuppressed) VALUES (1, 1, 1, 0);
        ");

        if (version >= 3)
        {
            if (version >= 10)
            {
                sql.AppendLine($@"
                    INSERT INTO NoteRevisions (Id, NoteId, CreatedAt, Text, TagsJson, IsProtected, ProtectedFormatVersion, ProtectedKdfIterations, ProtectedSaltBase64, ProtectedNonceBase64, ProtectedTagBase64, ProtectedCiphertextBase64{(version >= 12 ? ", Title" : "")})
                    VALUES (1, 1, '{Ts}', '{UnprotectedText}', '[]', 0, 0, 0, '', '', '', ''{(version >= 12 ? $", '{UnprotectedText}'" : "")});
                    INSERT INTO NoteAttachments (Id, NoteId, {(version >= 5 ? "SyncId, " : "")}OriginalFileName, StoredFileName, RelativePath, ContentType, Size, Sha256, CreatedAt, IsProtected, ProtectedFormatVersion, ProtectedKdfIterations, ProtectedSaltBase64, ProtectedNonceBase64, ProtectedTagBase64, ProtectedCiphertextBase64)
                    VALUES (1, 1, {(version >= 5 ? $"'{AttachmentSyncId}', " : "")}'clip.txt', 'stored-clip.txt', 'attachments/stored-clip.txt', 'text/plain', 12, '{AttachmentSha}', '{Ts}', 0, 0, 0, '', '', '', '');
                ");
            }
            else
            {
                sql.AppendLine($@"
                    INSERT INTO NoteRevisions (Id, NoteId, CreatedAt, Text, TagsJson)
                    VALUES (1, 1, '{Ts}', '{UnprotectedText}', '[]');
                    INSERT INTO NoteAttachments (Id, NoteId, {(version >= 5 ? "SyncId, " : "")}OriginalFileName, StoredFileName, RelativePath, ContentType, Size, Sha256, CreatedAt)
                    VALUES (1, 1, {(version >= 5 ? $"'{AttachmentSyncId}', " : "")}'clip.txt', 'stored-clip.txt', 'attachments/stored-clip.txt', 'text/plain', 12, '{AttachmentSha}', '{Ts}');
                ");
            }
        }

        if (version >= 4)
        {
            if (version >= 5)
            {
                sql.AppendLine($@"
                    INSERT INTO NoteTemplates (Id, SyncId, Title, Text, CreatedAt, UpdatedAt)
                    VALUES (1, '{TemplateSyncId}', 'Daily Plan', 'template body', '{Ts}', '{Ts}');
                ");
            }
            else
            {
                sql.AppendLine($@"
                    INSERT INTO NoteTemplates (Id, Title, Text, CreatedAt, UpdatedAt)
                    VALUES (1, 'Daily Plan', 'template body', '{Ts}', '{Ts}');
                ");
            }
            sql.AppendLine("INSERT INTO NoteTemplateTags (TemplateId, TagId) VALUES (1, 1);");
        }

        if (version >= 5)
        {
            string hashColumn = version >= 6 ? ", ContentHash" : "";
            string hashValue = version >= 6 ? ", 'hash-note'" : "";
            sql.AppendLine($@"
                INSERT INTO SyncEntityStates (SyncId, EntityType, RevisionId, ParentRevisionId, DeviceId, UpdatedAtUtc, IsDeleted, DeletedAtUtc{hashColumn})
                VALUES ('{NoteSyncId}', 'Note', 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa', NULL, '{DeviceId}', '{Ts}', 0, NULL{hashValue});
                INSERT INTO SyncEntityStates (SyncId, EntityType, RevisionId, ParentRevisionId, DeviceId, UpdatedAtUtc, IsDeleted, DeletedAtUtc{hashColumn})
                VALUES ('{TagSyncId}', 'Tag', 'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb', NULL, '{DeviceId}', '{Ts}', 0, NULL{(version >= 6 ? ", 'hash-tag'" : "")});
            ");
        }

        if (version >= 7)
        {
            sql.AppendLine($@"
                INSERT INTO SyncDeviceStates (DeviceId, LatestProcessedPackageId, LatestProcessedETag, LastSyncedAtUtc, PackageCount)
                VALUES ('{DeviceId}', NULL, NULL, '{Ts}', 1);
                INSERT INTO SyncLocalStates (DeviceId, LastUploadedPackageId, LatestUploadedPackageKey, LastUploadedETag, LastUploadedSnapshotHash, LastUploadCompletedAtUtc, PendingPackageId, PendingPackageKey, PendingPackageDigest, PendingContentHash, PendingCreatedAtUtc{(version >= 8 ? ", PendingPayloadBytes" : "")})
                VALUES ('{DeviceId}', NULL, 'packages/pending', NULL, NULL, NULL, NULL, NULL, 'digest-not-a-secret', NULL, '{Ts}'{(version >= 8 ? ", X'0102'" : "")});
                INSERT INTO SyncConflicts (SyncId, EntityType, LocalRevisionId, RemoteRevisionId, ParentRevisionId, SourceDeviceId, SourcePackageId, DetectedAtUtc, Reason, LocalDataJson, RemoteDataJson, IsResolved)
                VALUES ('{NoteSyncId}', 'Note', NULL, 'cccccccc-cccc-cccc-cccc-cccccccccccc', NULL, '{DeviceId}', NULL, '{Ts}', 'test-conflict', '{{""text"":""local""}}', '{{""text"":""remote""}}', 0);
            ");
        }

        if (version >= 10)
        {
            sql.AppendLine($@"
                INSERT INTO Notes (Id, SyncId, Text, CreatedAt, UpdatedAt, IsPinned, IsFavorite, IsInbox, DeletedAt{(version >= 2 ? ", SourceProcessName, SourceWindowTitle, SourceUrl, CapturedAt" : "")}, IsProtected, ProtectedFormatVersion, ProtectedKdfIterations, ProtectedSaltBase64, ProtectedNonceBase64, ProtectedTagBase64, ProtectedCiphertextBase64{(version >= 11 ? ", ProtectedOriginalSyncId" : "")}{(version >= 12 ? ", Title" : "")})
                VALUES (3, '77777777-7777-7777-7777-777777777777', '', '{Ts}', '{Ts}', 0, 0, 0, NULL{(version >= 2 ? ", NULL, NULL, NULL, NULL" : "")}, 1, 1, 100000, 'c2FsdA==', 'bm9uY2U=', 'dGFn', '{ProtectedCipher}'{(version >= 11 ? ", NULL" : "")}{(version >= 12 ? ", ''" : "")});
                INSERT INTO NoteRevisions (NoteId, CreatedAt, Text, TagsJson, IsProtected, ProtectedFormatVersion, ProtectedKdfIterations, ProtectedSaltBase64, ProtectedNonceBase64, ProtectedTagBase64, ProtectedCiphertextBase64{(version >= 12 ? ", Title" : "")})
                VALUES (3, '{Ts}', '', '[]', 1, 1, 100000, 'c2FsdA==', 'bm9uY2U=', 'dGFn', '{ProtectedCipher}'{(version >= 12 ? ", ''" : "")});
                INSERT INTO NoteAttachments (NoteId, SyncId, OriginalFileName, StoredFileName, RelativePath, ContentType, Size, Sha256, CreatedAt, IsProtected, ProtectedFormatVersion, ProtectedKdfIterations, ProtectedSaltBase64, ProtectedNonceBase64, ProtectedTagBase64, ProtectedCiphertextBase64)
                VALUES (3, '88888888-8888-8888-8888-888888888888', 'secret.bin', 'stored-secret.bin', 'attachments/stored-secret.bin', 'application/octet-stream', 4, 'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb', '{Ts}', 1, 1, 100000, 'c2FsdA==', 'bm9uY2U=', 'dGFn', '{ProtectedCipher}');
            ");
        }

        sql.AppendLine($"PRAGMA user_version = {version};");
        return sql.ToString();
    }

    private static string NewDbPath(string prefix)
    {
        return Path.Combine(Path.GetTempPath(), $"qn_{prefix}_{Guid.NewGuid():N}.db");
    }

    private static void CleanupDb(string dbPath)
    {
        SqliteConnection.ClearAllPools();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        if (File.Exists(dbPath))
        {
            try { File.Delete(dbPath); } catch { }
        }
        foreach (var extra in new[] { dbPath + "-wal", dbPath + "-shm" })
        {
            if (File.Exists(extra))
            {
                try { File.Delete(extra); } catch { }
            }
        }
    }

    private static long ReadUserVersion(SqliteConnection conn)
    {
        return ScalarLong(conn, "PRAGMA user_version;");
    }

    private static string ReadIntegrity(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA integrity_check;";
        return cmd.ExecuteScalar()?.ToString() ?? "unknown";
    }

    private static string ReadNoteText(SqliteConnection conn, int noteId)
    {
        return ScalarString(conn, $"SELECT Text FROM Notes WHERE Id = {noteId};");
    }

    private static long ScalarLong(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        var value = cmd.ExecuteScalar();
        return value == null || value == DBNull.Value ? 0 : Convert.ToInt64(value);
    }

    private static string ScalarString(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        var value = cmd.ExecuteScalar();
        return value == null || value == DBNull.Value ? string.Empty : Convert.ToString(value) ?? string.Empty;
    }
}
