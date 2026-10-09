using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using QuickNotes.App.Data;
using QuickNotes.App.Models;
using QuickNotes.App.Models.Sync;
using QuickNotes.App.Services;
using QuickNotes.App.Services.Sync;
using Xunit;

namespace QuickNotes.Tests;

[TestCategory(TestCategories.Integration)]
public class SyncTests : IDisposable
{
    private readonly List<string> _tempFiles = new();
    private readonly List<IDisposable> _disposables = new();

    public void Dispose()
    {
        foreach (var d in _disposables)
        {
            try { d.Dispose(); } catch { }
        }

        foreach (var file in _tempFiles)
        {
            if (File.Exists(file))
            {
                try { File.Delete(file); } catch { }
            }
        }
    }

    private string CreateTempDbPath()
    {
        var path = Path.Combine(Path.GetTempPath(), $"quicknotes_synctest_{Guid.NewGuid():N}.db");
        _tempFiles.Add(path);
        return path;
    }

    private QuickNotesDbContext CreateContext(string dbPath)
    {
        var context = new QuickNotesDbContext(dbPath);
        _disposables.Add(context);
        return context;
    }

    private class FixedDateTimeProvider : IDateTimeProvider
    {
        public DateTime Now { get; }
        public DateTime UtcNow { get; }

        public FixedDateTimeProvider(DateTime fixedTimeUtc)
        {
            UtcNow = fixedTimeUtc;
            Now = fixedTimeUtc.ToLocalTime();
        }
    }

    // ----------------------------------------------------------------------------------
    // 1. Migration of existing database & SyncId uniqueness
    // ----------------------------------------------------------------------------------
    [Fact]
    public void Migration_ExistingV4Database_PopulatesSyncId_GuaranteesUniqueness_AndCreatesUniqueIndexes()
    {
        string dbPath = CreateTempDbPath();
        bool backupCalled = false;

        // 1. Create older schema version 4 database without SyncId columns
        using (var conn = new SqliteConnection($"Data Source={dbPath}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                CREATE TABLE Notes (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    Text TEXT NOT NULL,
                    CreatedAt TEXT NOT NULL,
                    UpdatedAt TEXT NOT NULL,
                    IsPinned INTEGER NOT NULL DEFAULT 0,
                    IsFavorite INTEGER NOT NULL DEFAULT 0,
                    IsInbox INTEGER NOT NULL DEFAULT 0,
                    DeletedAt TEXT NULL,
                    CapturedAt TEXT NULL,
                    SourceProcessName TEXT NULL,
                    SourceWindowTitle TEXT NULL,
                    SourceUrl TEXT NULL
                );
                CREATE TABLE Tags (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    Name TEXT NOT NULL,
                    ParentTagId INTEGER NULL
                );
                CREATE TABLE NoteTags (
                    NoteId INTEGER NOT NULL,
                    TagId INTEGER NOT NULL,
                    Origin INTEGER NOT NULL,
                    IsSuppressed INTEGER NOT NULL DEFAULT 0,
                    PRIMARY KEY (NoteId, TagId)
                );
                CREATE TABLE NoteRevisions (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    NoteId INTEGER NOT NULL,
                    CreatedAt TEXT NOT NULL,
                    Text TEXT NOT NULL,
                    TagsJson TEXT NOT NULL
                );
                CREATE TABLE NoteAttachments (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    NoteId INTEGER NOT NULL,
                    OriginalFileName TEXT NOT NULL,
                    StoredFileName TEXT NOT NULL,
                    RelativePath TEXT NOT NULL,
                    ContentType TEXT NOT NULL,
                    Size INTEGER NOT NULL,
                    Sha256 TEXT NOT NULL,
                    CreatedAt TEXT NOT NULL
                );
                CREATE TABLE NoteTemplates (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    Title TEXT NOT NULL COLLATE NOCASE,
                    Text TEXT NOT NULL,
                    CreatedAt TEXT NOT NULL,
                    UpdatedAt TEXT NOT NULL
                );
                CREATE TABLE NoteTemplateTags (
                    TemplateId INTEGER NOT NULL,
                    TagId INTEGER NOT NULL,
                    PRIMARY KEY (TemplateId, TagId)
                );

                PRAGMA user_version = 4;

                INSERT INTO Tags (Id, Name) VALUES (1, 'Backend');
                INSERT INTO Tags (Id, Name) VALUES (2, 'Frontend');

                INSERT INTO Notes (Id, Text, CreatedAt, UpdatedAt) VALUES (1, 'Legacy Note 1', '2026-09-01T10:00:00', '2026-09-01T10:00:00');
                INSERT INTO Notes (Id, Text, CreatedAt, UpdatedAt) VALUES (2, 'Legacy Note 2', '2026-09-02T11:00:00', '2026-09-02T11:00:00');

                INSERT INTO NoteTemplates (Id, Title, Text, CreatedAt, UpdatedAt) VALUES (1, 'Template 1', 'Body 1', '2026-09-01T10:00:00', '2026-09-01T10:00:00');
                INSERT INTO NoteAttachments (Id, NoteId, OriginalFileName, StoredFileName, RelativePath, ContentType, Size, Sha256, CreatedAt)
                VALUES (1, 1, 'file.txt', 'f_1.dat', 'files/f_1.dat', 'text/plain', 123, 'sha256hash', '2026-09-01T10:00:00');
            ";
            cmd.ExecuteNonQuery();
        }

        // 2. Initialize and migrate
        using (var context = CreateContext(dbPath))
        {
            DbInitializer.Initialize(context, () => { backupCalled = true; });
        }

        Assert.True(backupCalled, "Backup must be created before schema upgrade when user data exists.");

        // 3. Verify schema version and SyncId population
        using (var conn = new SqliteConnection($"Data Source={dbPath}"))
        {
            conn.Open();

            // Verify user_version is 6
            using (var verCmd = conn.CreateCommand())
            {
                verCmd.CommandText = "PRAGMA user_version;";
                long version = Convert.ToInt64(verCmd.ExecuteScalar());
                Assert.Equal(DbInitializer.CurrentSchemaVersion, version);
            }

            // Verify unique indexes exist on SQLite level
            Assert.True(DbInitializer.HasUniqueIndex(conn, "Notes", "IX_Notes_SyncId"));
            Assert.True(DbInitializer.HasUniqueIndex(conn, "Tags", "IX_Tags_SyncId"));
            Assert.True(DbInitializer.HasUniqueIndex(conn, "NoteTemplates", "IX_NoteTemplates_SyncId"));
            Assert.True(DbInitializer.HasUniqueIndex(conn, "NoteAttachments", "IX_NoteAttachments_SyncId"));

            // Verify Notes SyncIds
            var noteSyncIds = new List<Guid>();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT SyncId FROM Notes;";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    Assert.False(reader.IsDBNull(0));
                    Assert.True(Guid.TryParse(reader.GetString(0), out var guid));
                    Assert.NotEqual(Guid.Empty, guid);
                    noteSyncIds.Add(guid);
                }
            }
            Assert.Equal(2, noteSyncIds.Count);
            Assert.Equal(2, noteSyncIds.Distinct().Count());

            // Verify Tags SyncIds
            var tagSyncIds = new List<Guid>();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT SyncId FROM Tags;";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    Assert.False(reader.IsDBNull(0));
                    Assert.True(Guid.TryParse(reader.GetString(0), out var guid));
                    Assert.NotEqual(Guid.Empty, guid);
                    tagSyncIds.Add(guid);
                }
            }
            Assert.Equal(2, tagSyncIds.Count);
            Assert.Equal(2, tagSyncIds.Distinct().Count());

            // Verify NoteTemplates SyncIds
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT SyncId FROM NoteTemplates WHERE Id = 1;";
                var raw = cmd.ExecuteScalar()?.ToString();
                Assert.True(Guid.TryParse(raw, out var tmplGuid));
                Assert.NotEqual(Guid.Empty, tmplGuid);
            }

            // Verify NoteAttachments SyncIds
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT SyncId FROM NoteAttachments WHERE Id = 1;";
                var raw = cmd.ExecuteScalar()?.ToString();
                Assert.True(Guid.TryParse(raw, out var attGuid));
                Assert.NotEqual(Guid.Empty, attGuid);
            }

            // Verify SyncEntityStates table is populated
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM SyncEntityStates;";
                long count = Convert.ToInt64(cmd.ExecuteScalar());
                Assert.Equal(6, count); // 2 notes + 2 tags + 1 template + 1 attachment
            }
        }
    }

    // ----------------------------------------------------------------------------------
    // 2. Idempotency of migration
    // ----------------------------------------------------------------------------------
    [Fact]
    public void Migration_Idempotency_PreservesExistingSyncIds_OnRepeatedRuns()
    {
        string dbPath = CreateTempDbPath();

        using (var context = CreateContext(dbPath))
        {
            DbInitializer.Initialize(context);

            context.Tags.Add(new Tag { Name = "Tag 1" });
            context.Notes.Add(new Note { Text = "Note 1" });
            context.NoteTemplates.Add(new NoteTemplate { Title = "Template 1", Text = "Content" });
            context.SaveChanges();
        }

        Dictionary<string, Guid> originalSyncIds = new();

        using (var conn = new SqliteConnection($"Data Source={dbPath}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT 'Note', SyncId FROM Notes UNION ALL SELECT 'Tag', SyncId FROM Tags UNION ALL SELECT 'Template', SyncId FROM NoteTemplates;";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                originalSyncIds[reader.GetString(0)] = Guid.Parse(reader.GetString(1));
            }
        }

        // Run migration a second time
        using (var context2 = CreateContext(dbPath))
        {
            DbInitializer.Initialize(context2);
        }

        // Verify SyncIds have NOT changed
        using (var conn = new SqliteConnection($"Data Source={dbPath}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT 'Note', SyncId FROM Notes UNION ALL SELECT 'Tag', SyncId FROM Tags UNION ALL SELECT 'Template', SyncId FROM NoteTemplates;";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                string key = reader.GetString(0);
                Guid current = Guid.Parse(reader.GetString(1));
                Assert.Equal(originalSyncIds[key], current);
            }
        }
    }

    // ----------------------------------------------------------------------------------
    // 3. Roundtrip of all entities
    // ----------------------------------------------------------------------------------
    [Fact]
    public async Task SyncPackage_RoundtripAllEntities_RestoresCleanlyAndPreservesRelationships()
    {
        string srcDbPath = CreateTempDbPath();
        string dstDbPath = CreateTempDbPath();
        string password = "StrongMasterPassword987!";
        var deviceA = Guid.NewGuid();

        // 1. Populate Source Database
        Guid noteSyncId, rootTagSyncId, childTagSyncId, tmplSyncId, attSyncId;
        DateTime fixedCapturedAt = new DateTime(2026, 9, 7, 12, 0, 0, DateTimeKind.Utc);

        using (var srcDb = CreateContext(srcDbPath))
        {
            DbInitializer.Initialize(srcDb);

            var rootTag = new Tag { Name = "IT" };
            var childTag = new Tag { Name = "C#", ParentTag = rootTag };
            childTag.Synonyms.Add(new TagSynonym { Value = "csharp", Tag = childTag });
            childTag.Synonyms.Add(new TagSynonym { Value = "dotnet", Tag = childTag });

            srcDb.Tags.AddRange(rootTag, childTag);

            var note = new Note
            {
                Text = "Developing QuickNotes Stage 21A",
                IsPinned = true,
                IsFavorite = true,
                IsInbox = false,
                SourceProcessName = "devenv.exe",
                SourceWindowTitle = "QuickNotes - Visual Studio",
                SourceUrl = "https://github.com/project",
                CapturedAt = fixedCapturedAt
            };
            note.NoteTags.Add(new NoteTag
            {
                Note = note,
                Tag = childTag,
                Origin = TagOrigin.Manual,
                IsSuppressed = false
            });
            srcDb.Notes.Add(note);

            var tmpl = new NoteTemplate
            {
                Title = "Sprint Plan",
                Text = "Goals for sprint:\n1. Synchronization foundation"
            };
            tmpl.TemplateTags.Add(new NoteTemplateTag { Template = tmpl, Tag = childTag });
            srcDb.NoteTemplates.Add(tmpl);

            var att = new NoteAttachment
            {
                Note = note,
                OriginalFileName = "architecture_v21.pdf",
                ContentType = "application/pdf",
                Size = 1048576,
                Sha256 = "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08",
                CreatedAt = fixedCapturedAt
            };
            srcDb.NoteAttachments.Add(att);

            await srcDb.SaveChangesAsync();

            noteSyncId = note.SyncId;
            rootTagSyncId = rootTag.SyncId;
            childTagSyncId = childTag.SyncId;
            tmplSyncId = tmpl.SyncId;
            attSyncId = att.SyncId;
        }

        // 2. Export package from Source
        var cryptoService = new SyncCryptoService();
        var exporter = new SyncPackageExporter(cryptoService, new FixedDeviceIdProvider(deviceA));
        SyncExportResult exportResult;
        using (var srcDb = CreateContext(srcDbPath))
        {
            exportResult = await exporter.ExportPackageAsync(srcDb, password, deviceA);
        }

        Assert.True(exportResult.Success);
        Assert.NotNull(exportResult.PackageJson);
        Assert.Equal(1, exportResult.ExportedNotesCount);
        Assert.Equal(2, exportResult.ExportedTagsCount);
        Assert.Equal(1, exportResult.ExportedTemplatesCount);
        Assert.Equal(1, exportResult.ExportedAttachmentsCount);

        // 3. Import package into empty Target Database
        var importer = new SyncPackageImporter(cryptoService);
        SyncImportResult importResult;
        using (var dstDb = CreateContext(dstDbPath))
        {
            DbInitializer.Initialize(dstDb);
            importResult = await importer.ImportPackageAsync(dstDb, exportResult.PackageJson, password);
        }

        Assert.True(importResult.Success);
        Assert.Equal(1, importResult.Notes.Created);
        Assert.Equal(2, importResult.Tags.Created);
        Assert.Equal(1, importResult.Templates.Created);
        Assert.Equal(1, importResult.Attachments.Created);
        Assert.Empty(importResult.Conflicts);
        Assert.Empty(importResult.Errors);

        // 4. Verify entities in Target Database
        using (var dstDb = CreateContext(dstDbPath))
        {
            // Tags & hierarchy
            var importedRoot = await dstDb.Tags.FirstOrDefaultAsync(t => t.SyncId == rootTagSyncId);
            Assert.NotNull(importedRoot);
            Assert.Equal("IT", importedRoot.Name);
            Assert.Null(importedRoot.ParentTagId);

            var importedChild = await dstDb.Tags
                .Include(t => t.ParentTag)
                .Include(t => t.Synonyms)
                .FirstOrDefaultAsync(t => t.SyncId == childTagSyncId);
            Assert.NotNull(importedChild);
            Assert.Equal("C#", importedChild.Name);
            Assert.NotNull(importedChild.ParentTag);
            Assert.Equal(importedRoot.Id, importedChild.ParentTag.Id);
            Assert.Equal(2, importedChild.Synonyms.Count);
            Assert.Contains(importedChild.Synonyms, s => s.Value == "csharp");
            Assert.Contains(importedChild.Synonyms, s => s.Value == "dotnet");

            // Note & NoteTags
            var importedNote = await dstDb.Notes
                .Include(n => n.NoteTags)
                .ThenInclude(nt => nt.Tag)
                .FirstOrDefaultAsync(n => n.SyncId == noteSyncId);
            Assert.NotNull(importedNote);
            Assert.Equal("Developing QuickNotes Stage 21A", importedNote.Text);
            Assert.True(importedNote.IsPinned);
            Assert.True(importedNote.IsFavorite);
            Assert.False(importedNote.IsInbox);
            Assert.Equal("devenv.exe", importedNote.SourceProcessName);
            Assert.Equal("QuickNotes - Visual Studio", importedNote.SourceWindowTitle);
            Assert.Equal("https://github.com/project", importedNote.SourceUrl);

            var noteTag = Assert.Single(importedNote.NoteTags);
            Assert.Equal(importedChild.Id, noteTag.TagId);
            Assert.Equal(TagOrigin.Manual, noteTag.Origin);
            Assert.False(noteTag.IsSuppressed);

            // Template
            var importedTmpl = await dstDb.NoteTemplates
                .Include(t => t.TemplateTags)
                .ThenInclude(tt => tt.Tag)
                .FirstOrDefaultAsync(t => t.SyncId == tmplSyncId);
            Assert.NotNull(importedTmpl);
            Assert.Equal("Sprint Plan", importedTmpl.Title);
            Assert.Contains("Goals for sprint", importedTmpl.Text);
            var tmplTag = Assert.Single(importedTmpl.TemplateTags);
            Assert.Equal(importedChild.Id, tmplTag.TagId);

            // Attachment metadata
            var importedAtt = await dstDb.NoteAttachments
                .Include(a => a.Note)
                .FirstOrDefaultAsync(a => a.SyncId == attSyncId);
            Assert.NotNull(importedAtt);
            Assert.Equal("architecture_v21.pdf", importedAtt.OriginalFileName);
            Assert.Equal("application/pdf", importedAtt.ContentType);
            Assert.Equal(1048576, importedAtt.Size);
            Assert.Equal("9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08", importedAtt.Sha256);
            Assert.Equal(importedNote.Id, importedAtt.NoteId);
        }
    }

    // ----------------------------------------------------------------------------------
    // 4. Wrong password
    // ----------------------------------------------------------------------------------
    [Fact]
    public async Task SyncPackage_WrongPassword_FailsDecryption_AndLeavesDbUnmodified()
    {
        string srcDbPath = CreateTempDbPath();
        string dstDbPath = CreateTempDbPath();

        using (var srcDb = CreateContext(srcDbPath))
        {
            DbInitializer.Initialize(srcDb);
            srcDb.Notes.Add(new Note { Text = "Confidential Note" });
            await srcDb.SaveChangesAsync();
        }

        var crypto = new SyncCryptoService();
        var exporter = new SyncPackageExporter(crypto, new FixedDeviceIdProvider(Guid.NewGuid()));
        var exportResult = await exporter.ExportPackageAsync(CreateContext(srcDbPath), "CorrectPassword123!");

        // Target DB initially empty
        using (var dstDb = CreateContext(dstDbPath))
        {
            DbInitializer.Initialize(dstDb);
        }

        var importer = new SyncPackageImporter(crypto);
        SyncImportResult importResult;
        using (var dstDb = CreateContext(dstDbPath))
        {
            importResult = await importer.ImportPackageAsync(dstDb, exportResult.PackageJson!, "IncorrectPassword999!");
        }

        Assert.False(importResult.Success);
        Assert.NotEmpty(importResult.Errors);

        // Verify DB was NOT modified
        using (var dstDb = CreateContext(dstDbPath))
        {
            Assert.Equal(0, await dstDb.Notes.CountAsync());
            Assert.Equal(0, await dstDb.SyncEntityStates.CountAsync());
        }
    }

    // ----------------------------------------------------------------------------------
    // 5. Tamper detection
    // ----------------------------------------------------------------------------------
    [Fact]
    public async Task SyncPackage_TamperDetection_RejectsCorruptedCiphertextOrHeader_LeavesDbUnmodified()
    {
        string srcDbPath = CreateTempDbPath();
        string dstDbPath = CreateTempDbPath();
        string password = "SafePassword123!";

        using (var srcDb = CreateContext(srcDbPath))
        {
            DbInitializer.Initialize(srcDb);
            srcDb.Notes.Add(new Note { Text = "Untampered note" });
            await srcDb.SaveChangesAsync();
        }

        var crypto = new SyncCryptoService();
        var exporter = new SyncPackageExporter(crypto, new FixedDeviceIdProvider(Guid.NewGuid()));
        var exportResult = await exporter.ExportPackageAsync(CreateContext(srcDbPath), password);

        var envelope = JsonSerializer.Deserialize<SyncPackageEnvelope>(exportResult.PackageJson!)!;

        // 1. Tamper with ciphertext
        byte[] rawCipher = Convert.FromBase64String(envelope.EncryptedPayloadBase64);
        rawCipher[0] ^= 0xFF; // flip bits
        envelope.EncryptedPayloadBase64 = Convert.ToBase64String(rawCipher);

        string tamperedPayloadJson = JsonSerializer.Serialize(envelope);

        var importer = new SyncPackageImporter(crypto);
        using (var dstDb = CreateContext(dstDbPath))
        {
            DbInitializer.Initialize(dstDb);
            var result = await importer.ImportPackageAsync(dstDb, tamperedPayloadJson, password);
            Assert.False(result.Success);
            Assert.Equal(0, await dstDb.Notes.CountAsync());
        }

        // 2. Tamper with header (DeviceId in associated data)
        envelope = JsonSerializer.Deserialize<SyncPackageEnvelope>(exportResult.PackageJson!)!;
        envelope.DeviceId = Guid.NewGuid(); // header tampered
        string tamperedHeaderJson = JsonSerializer.Serialize(envelope);

        using (var dstDb = CreateContext(dstDbPath))
        {
            var result = await importer.ImportPackageAsync(dstDb, tamperedHeaderJson, password);
            Assert.False(result.Success);
            Assert.Equal(0, await dstDb.Notes.CountAsync());
        }
    }

    // ----------------------------------------------------------------------------------
    // 6. Absence of plaintext secrets/text/filenames in exported package
    // ----------------------------------------------------------------------------------
    [Fact]
    public async Task SyncPackage_PlaintextPrivacy_NoUserMetadataOrSecretsInEnvelope()
    {
        string srcDbPath = CreateTempDbPath();
        string secretText = "TOP_SECRET_NOTE_CONTENT_123456789";
        string secretTag = "SUPER_CLASSIFIED_TAG_XYZ";
        string secretFile = "bank_statement_2026.pdf";
        string secretTemplate = "PRIVATE_CONFIDENTIAL_TEMPLATE";
        string password = "MySuperSecretKey999!";

        using (var srcDb = CreateContext(srcDbPath))
        {
            DbInitializer.Initialize(srcDb);

            var tag = new Tag { Name = secretTag };
            srcDb.Tags.Add(tag);

            var note = new Note { Text = secretText };
            srcDb.Notes.Add(note);

            var tmpl = new NoteTemplate { Title = secretTemplate, Text = "Template text" };
            srcDb.NoteTemplates.Add(tmpl);

            var att = new NoteAttachment
            {
                Note = note,
                OriginalFileName = secretFile,
                ContentType = "application/pdf",
                Sha256 = "abcd",
                Size = 512
            };
            srcDb.NoteAttachments.Add(att);

            await srcDb.SaveChangesAsync();
        }

        var crypto = new SyncCryptoService();
        var exporter = new SyncPackageExporter(crypto, new FixedDeviceIdProvider(Guid.NewGuid()));
        var exportResult = await exporter.ExportPackageAsync(CreateContext(srcDbPath), password);

        Assert.True(exportResult.Success);
        string packageJson = exportResult.PackageJson!;

        // Plaintext inspection: None of user metadata or passwords must appear anywhere in the envelope
        Assert.DoesNotContain(secretText, packageJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(secretTag, packageJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(secretFile, packageJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(secretTemplate, packageJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(password, packageJson, StringComparison.OrdinalIgnoreCase);

        // Technical header only
        var envelope = JsonSerializer.Deserialize<SyncPackageEnvelope>(packageJson);
        Assert.NotNull(envelope);
        Assert.Equal("QNSP", envelope.Magic);
        Assert.Equal(1, envelope.FormatVersion);
        Assert.Equal("AES-256-GCM", envelope.Crypto.Algorithm);
        Assert.Equal("PBKDF2-HMAC-SHA256", envelope.Crypto.KdfAlgorithm);
    }

    // ----------------------------------------------------------------------------------
    // 7. Unknown version
    // ----------------------------------------------------------------------------------
    [Fact]
    public async Task SyncPackage_UnknownVersion_SafelyRejectedWithoutDbMutation()
    {
        string srcDbPath = CreateTempDbPath();
        string dstDbPath = CreateTempDbPath();
        string password = "Password123!";

        using (var srcDb = CreateContext(srcDbPath))
        {
            DbInitializer.Initialize(srcDb);
            srcDb.Notes.Add(new Note { Text = "Test" });
            await srcDb.SaveChangesAsync();
        }

        var crypto = new SyncCryptoService();
        var exporter = new SyncPackageExporter(crypto, new FixedDeviceIdProvider(Guid.NewGuid()));
        var exportResult = await exporter.ExportPackageAsync(CreateContext(srcDbPath), password);

        var envelope = JsonSerializer.Deserialize<SyncPackageEnvelope>(exportResult.PackageJson!)!;
        envelope.FormatVersion = 999; // Unknown future version
        string unknownVersionJson = JsonSerializer.Serialize(envelope);

        var importer = new SyncPackageImporter(crypto);
        using (var dstDb = CreateContext(dstDbPath))
        {
            DbInitializer.Initialize(dstDb);
            var result = await importer.ImportPackageAsync(dstDb, unknownVersionJson, password);
            Assert.False(result.Success);
            Assert.Contains("Неподдерживаемая версия формата", result.Errors[0]);
            Assert.Equal(0, await dstDb.Notes.CountAsync());
        }
    }

    // ----------------------------------------------------------------------------------
    // 8. Atomic rollback on failure
    // ----------------------------------------------------------------------------------
    [Fact]
    public async Task SyncPackage_AtomicRollback_OnFailure_LeavesDbUnmodified()
    {
        string srcDbPath = CreateTempDbPath();
        string dstDbPath = CreateTempDbPath();
        string password = "Password123!";

        using (var srcDb = CreateContext(srcDbPath))
        {
            DbInitializer.Initialize(srcDb);
            for (int i = 1; i <= 5; i++)
            {
                srcDb.Notes.Add(new Note { Text = $"Note {i}" });
                srcDb.Tags.Add(new Tag { Name = $"Tag {i}" });
            }
            await srcDb.SaveChangesAsync();
        }

        var crypto = new SyncCryptoService();
        var exporter = new SyncPackageExporter(crypto, new FixedDeviceIdProvider(Guid.NewGuid()));
        var exportResult = await exporter.ExportPackageAsync(CreateContext(srcDbPath), password);

        // In Target DB, inject a conflicting constraint before import:
        // We will insert an incompatible manual trigger on Notes that aborts on INSERT of 'Note 3'
        using (var dstDb = CreateContext(dstDbPath))
        {
            DbInitializer.Initialize(dstDb);

            var conn = dstDb.Database.GetDbConnection();
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                CREATE TRIGGER AbortOnNote3 BEFORE INSERT ON Notes
                FOR EACH ROW WHEN new.Text = 'Note 3'
                BEGIN
                    SELECT RAISE(ABORT, 'Simulated failure injection on Note 3');
                END;
            ";
            cmd.ExecuteNonQuery();
            conn.Close();
        }

        var importer = new SyncPackageImporter(crypto);
        using (var dstDb = CreateContext(dstDbPath))
        {
            var result = await importer.ImportPackageAsync(dstDb, exportResult.PackageJson!, password);
            Assert.False(result.Success);
            Assert.NotEmpty(result.Errors);
            Assert.Contains("Ошибка транзакции", result.Errors[0]);
        }

        // Verify Target DB rolled back completely: 0 notes, 0 tags, 0 sync entity states!
        using (var dstDb = CreateContext(dstDbPath))
        {
            Assert.Equal(0, await dstDb.Notes.CountAsync());
            Assert.Equal(0, await dstDb.Tags.CountAsync());
            Assert.Equal(0, await dstDb.SyncEntityStates.CountAsync());
        }
    }

    // ----------------------------------------------------------------------------------
    // 9. Linear update
    // ----------------------------------------------------------------------------------
    [Fact]
    public async Task SyncPackage_LinearUpdate_AdvancesEntityOnSecondDevice()
    {
        string deviceADbPath = CreateTempDbPath();
        string deviceBDbPath = CreateTempDbPath();
        string password = "LinearPassword123!";
        var deviceAId = Guid.NewGuid();
        var crypto = new SyncCryptoService();

        // Step 1: Device A creates note R1
        Guid noteSyncId;
        using (var dbA = CreateContext(deviceADbPath))
        {
            DbInitializer.Initialize(dbA);
            var note = new Note { Text = "Revision 1 text" };
            dbA.Notes.Add(note);
            await dbA.SaveChangesAsync();
            noteSyncId = note.SyncId;
        }

        // Export from Device A and import into Device B
        var exporterA = new SyncPackageExporter(crypto, new FixedDeviceIdProvider(deviceAId));
        var importer = new SyncPackageImporter(crypto);

        var export1 = await exporterA.ExportPackageAsync(CreateContext(deviceADbPath), password, deviceAId);
        using (var dbB = CreateContext(deviceBDbPath))
        {
            DbInitializer.Initialize(dbB);
            var impResult1 = await importer.ImportPackageAsync(dbB, export1.PackageJson!, password);
            Assert.True(impResult1.Success);
            Assert.Equal(1, impResult1.Notes.Created);
        }

        // Step 2: Device A updates note to R2
        using (var dbA = CreateContext(deviceADbPath))
        {
            var note = await dbA.Notes.FirstAsync(n => n.SyncId == noteSyncId);
            note.Text = "Revision 2 updated text";
            note.UpdatedAt = DateTime.Now.AddMinutes(5);
            await dbA.SaveChangesAsync();
        }

        var export2 = await exporterA.ExportPackageAsync(CreateContext(deviceADbPath), password, deviceAId);

        // Step 3: Device B imports package 2
        using (var dbB = CreateContext(deviceBDbPath))
        {
            var impResult2 = await importer.ImportPackageAsync(dbB, export2.PackageJson!, password);
            Assert.True(impResult2.Success);
            Assert.Equal(1, impResult2.Notes.Updated);
            Assert.Equal(0, impResult2.Notes.Created);
            Assert.Empty(impResult2.Conflicts);

            var noteOnB = await dbB.Notes.FirstAsync(n => n.SyncId == noteSyncId);
            Assert.Equal("Revision 2 updated text", noteOnB.Text);

            var stateOnB = await dbB.SyncEntityStates.FirstAsync(s => s.SyncId == noteSyncId);
            Assert.NotNull(stateOnB.ParentRevisionId);
        }
    }

    // ----------------------------------------------------------------------------------
    // 10. Tombstone
    // ----------------------------------------------------------------------------------
    [Fact]
    public async Task SyncPackage_Tombstone_MarksEntityDeletedOnSecondDevice()
    {
        string deviceADbPath = CreateTempDbPath();
        string deviceBDbPath = CreateTempDbPath();
        string password = "TombstonePassword123!";
        var deviceAId = Guid.NewGuid();
        var crypto = new SyncCryptoService();

        // 1. Device A creates note
        Guid noteSyncId;
        using (var dbA = CreateContext(deviceADbPath))
        {
            DbInitializer.Initialize(dbA);
            var note = new Note { Text = "Note to be deleted" };
            dbA.Notes.Add(note);
            await dbA.SaveChangesAsync();
            noteSyncId = note.SyncId;
        }

        var exporterA = new SyncPackageExporter(crypto, new FixedDeviceIdProvider(deviceAId));
        var importer = new SyncPackageImporter(crypto);

        // Sync initial state to Device B
        var export1 = await exporterA.ExportPackageAsync(CreateContext(deviceADbPath), password, deviceAId);
        using (var dbB = CreateContext(deviceBDbPath))
        {
            DbInitializer.Initialize(dbB);
            await importer.ImportPackageAsync(dbB, export1.PackageJson!, password);
            var noteOnB = await dbB.Notes.FirstAsync(n => n.SyncId == noteSyncId);
            Assert.Null(noteOnB.DeletedAt);
        }

        // 2. Device A soft-deletes note
        using (var dbA = CreateContext(deviceADbPath))
        {
            var note = await dbA.Notes.FirstAsync(n => n.SyncId == noteSyncId);
            note.DeletedAt = DateTime.UtcNow;
            await dbA.SaveChangesAsync();
        }

        // Device A exports tombstone
        var export2 = await exporterA.ExportPackageAsync(CreateContext(deviceADbPath), password, deviceAId);

        // 3. Device B imports tombstone
        using (var dbB = CreateContext(deviceBDbPath))
        {
            var impResult2 = await importer.ImportPackageAsync(dbB, export2.PackageJson!, password);
            Assert.True(impResult2.Success);
            Assert.Equal(1, impResult2.Notes.Deleted);

            var noteOnB = await dbB.Notes.FirstAsync(n => n.SyncId == noteSyncId);
            Assert.NotNull(noteOnB.DeletedAt);

            var stateOnB = await dbB.SyncEntityStates.FirstAsync(s => s.SyncId == noteSyncId);
            Assert.True(stateOnB.IsDeleted);
        }
    }

    // ----------------------------------------------------------------------------------
    // 11. Branch conflict without overwrite
    // ----------------------------------------------------------------------------------
    [Fact]
    public async Task SyncPackage_BranchConflict_DoesNotOverwriteLocalData_AndReportsConflict()
    {
        string deviceADbPath = CreateTempDbPath();
        string deviceBDbPath = CreateTempDbPath();
        string password = "ConflictPassword123!";
        var deviceAId = Guid.NewGuid();
        var deviceBId = Guid.NewGuid();
        var crypto = new SyncCryptoService();

        // 1. Initial shared state
        Guid noteSyncId;
        using (var dbA = CreateContext(deviceADbPath))
        {
            DbInitializer.Initialize(dbA);
            var note = new Note { Text = "Base shared content" };
            dbA.Notes.Add(note);
            await dbA.SaveChangesAsync();
            noteSyncId = note.SyncId;
        }

        var exporterA = new SyncPackageExporter(crypto, new FixedDeviceIdProvider(deviceAId));
        var exporterB = new SyncPackageExporter(crypto, new FixedDeviceIdProvider(deviceBId));
        var importer = new SyncPackageImporter(crypto);

        var exportBase = await exporterA.ExportPackageAsync(CreateContext(deviceADbPath), password, deviceAId);
        using (var dbB = CreateContext(deviceBDbPath))
        {
            DbInitializer.Initialize(dbB);
            await importer.ImportPackageAsync(dbB, exportBase.PackageJson!, password);
        }

        // 2. Both devices edit concurrently!
        // Device A edits to "Edit by Device A"
        using (var dbA = CreateContext(deviceADbPath))
        {
            var noteA = await dbA.Notes.FirstAsync(n => n.SyncId == noteSyncId);
            noteA.Text = "Edit by Device A";
            noteA.UpdatedAt = DateTime.Now.AddMinutes(5);
            await dbA.SaveChangesAsync();
        }

        // Device B edits to "Edit by Device B"
        using (var dbB = CreateContext(deviceBDbPath))
        {
            var noteB = await dbB.Notes.FirstAsync(n => n.SyncId == noteSyncId);
            noteB.Text = "Edit by Device B";
            noteB.UpdatedAt = DateTime.Now.AddMinutes(10);
            await dbB.SaveChangesAsync();
        }

        // Device B also triggers an export to record its new local revision in SyncEntityStates
        await exporterB.ExportPackageAsync(CreateContext(deviceBDbPath), password, deviceBId);

        // 3. Device A exports its revision
        var exportFromA = await exporterA.ExportPackageAsync(CreateContext(deviceADbPath), password, deviceAId);

        // 4. Device B imports Device A's package -> Conflict!
        using (var dbB = CreateContext(deviceBDbPath))
        {
            var conflictImport = await importer.ImportPackageAsync(dbB, exportFromA.PackageJson!, password);
            Assert.True(conflictImport.Success);
            Assert.Equal(0, conflictImport.Notes.Updated);
            Assert.Equal(1, conflictImport.Notes.Skipped);

            var conflict = Assert.Single(conflictImport.Conflicts);
            Assert.Equal(noteSyncId, conflict.SyncId);
            Assert.Equal("Note", conflict.EntityType);
            Assert.Contains("Конфликт ветвления", conflict.Reason);

            // Verify Device B's local note was NOT overwritten!
            var noteOnB = await dbB.Notes.FirstAsync(n => n.SyncId == noteSyncId);
            Assert.Equal("Edit by Device B", noteOnB.Text);
        }
    }

    // ----------------------------------------------------------------------------------
    // 12. Broken references & repeated import
    // ----------------------------------------------------------------------------------
    [Fact]
    public async Task SyncPackage_BrokenReferences_AndRepeatedImport_AreSafeAndIdempotent()
    {
        string dstDbPath = CreateTempDbPath();
        string password = "BrokenRefPassword123!";
        var crypto = new SyncCryptoService();

        // Construct a payload manually with broken references:
        // - Tag referencing non-existent parent TagSyncId
        // - Note referencing non-existent TagSyncId
        // - Attachment referencing non-existent NoteSyncId
        var payload = new SyncPackagePayload
        {
            PayloadVersion = 1,
            PackageId = Guid.NewGuid(),
            SourceDeviceId = Guid.NewGuid(),
            CreatedAtUtc = DateTime.UtcNow
        };

        var tagWithMissingParent = new SyncTagDto
        {
            SyncId = Guid.NewGuid(),
            RevisionId = Guid.NewGuid(),
            Name = "Orphan Tag",
            ParentTagSyncId = Guid.NewGuid(), // does not exist
            Operation = SyncOperationType.Upsert
        };
        payload.Tags.Add(tagWithMissingParent);

        var noteWithMissingTag = new SyncNoteDto
        {
            SyncId = Guid.NewGuid(),
            RevisionId = Guid.NewGuid(),
            Text = "Note with broken tag reference",
            CreatedAtUtc = DateTime.UtcNow,
            Operation = SyncOperationType.Upsert,
            Tags = new List<SyncNoteTagDto>
            {
                new SyncNoteTagDto { TagSyncId = Guid.NewGuid(), Origin = TagOrigin.Auto, IsSuppressed = false }
            }
        };
        payload.Notes.Add(noteWithMissingTag);

        var attWithMissingNote = new SyncAttachmentDto
        {
            SyncId = Guid.NewGuid(),
            NoteSyncId = Guid.NewGuid(), // does not exist
            RevisionId = Guid.NewGuid(),
            OriginalFileName = "orphan_att.txt",
            ContentType = "text/plain",
            Sha256 = "1234",
            Size = 10,
            CreatedAtUtc = DateTime.UtcNow,
            Operation = SyncOperationType.Upsert
        };
        payload.Attachments.Add(attWithMissingNote);

        // Encrypt envelope
        byte[] plaintext = JsonSerializer.SerializeToUtf8Bytes(payload, SyncPackageExporter.DeterministicJsonOptions);
        var envelope = new SyncPackageEnvelope
        {
            Magic = "QNSP",
            FormatVersion = 1,
            PackageId = payload.PackageId,
            DeviceId = payload.SourceDeviceId,
            CreatedAtUtc = payload.CreatedAtUtc,
            Crypto = new SyncCryptoHeader()
        };

        var encResult = crypto.EncryptPayload(plaintext, password, envelope.GetAssociatedData());
        envelope.Crypto.SaltBase64 = Convert.ToBase64String(encResult.Salt);
        envelope.Crypto.NonceBase64 = Convert.ToBase64String(encResult.Nonce);
        envelope.Crypto.TagBase64 = Convert.ToBase64String(encResult.Tag);
        envelope.EncryptedPayloadBase64 = Convert.ToBase64String(encResult.Ciphertext);

        string packageJson = JsonSerializer.Serialize(envelope);

        var importer = new SyncPackageImporter(crypto);

        // Run 1: Import with broken references
        using (var dstDb = CreateContext(dstDbPath))
        {
            DbInitializer.Initialize(dstDb);
            var result1 = await importer.ImportPackageAsync(dstDb, packageJson, password);
            Assert.True(result1.Success);
            Assert.Equal(1, result1.Tags.Created);
            Assert.Equal(1, result1.Notes.Created);
            Assert.Equal(0, result1.Attachments.Created); // skipped because parent note missing
            Assert.Equal(1, result1.Attachments.Skipped);
            Assert.NotEmpty(result1.Diagnostics);

            // Orphan tag exists in root
            var importedTag = await dstDb.Tags.FirstAsync(t => t.SyncId == tagWithMissingParent.SyncId);
            Assert.Null(importedTag.ParentTagId);

            // Note exists with 0 tags (broken ref was safely skipped)
            var importedNote = await dstDb.Notes.Include(n => n.NoteTags).FirstAsync(n => n.SyncId == noteWithMissingTag.SyncId);
            Assert.Empty(importedNote.NoteTags);
        }

        // Run 2: Repeat import of exact same package -> 100% idempotent no-op!
        using (var dstDb = CreateContext(dstDbPath))
        {
            var result2 = await importer.ImportPackageAsync(dstDb, packageJson, password);
            Assert.True(result2.Success);
            Assert.Equal(0, result2.TotalCreated);
            Assert.Equal(0, result2.TotalUpdated);
            Assert.Equal(0, result2.TotalDeleted);
            Assert.Equal(3, result2.TotalSkipped); // note, tag, attachment all skipped
            Assert.Empty(result2.Conflicts);

            Assert.Equal(1, await dstDb.Tags.CountAsync());
            Assert.Equal(1, await dstDb.Notes.CountAsync());
        }
    }

    // ----------------------------------------------------------------------------------
    // 13. Deterministic order
    // ----------------------------------------------------------------------------------
    [Fact]
    public void SyncPackage_DeterministicOrdering_ProducesIdenticalPayloadBytes()
    {
        string dbPath = CreateTempDbPath();
        var fixedDeviceId = new Guid("11111111-2222-3333-4444-555555555555");
        var fixedTime = new DateTime(2026, 9, 7, 0, 0, 0, DateTimeKind.Utc);
        var timeProvider = new FixedDateTimeProvider(fixedTime);

        using (var db = CreateContext(dbPath))
        {
            DbInitializer.Initialize(db);

            // Add tags, notes, templates with deliberate reverse ordering
            var tagZ = new Tag { Name = "Z Tag", SyncId = new Guid("ffffffff-0000-0000-0000-000000000000") };
            tagZ.Synonyms.Add(new TagSynonym { Value = "syn_z" });
            tagZ.Synonyms.Add(new TagSynonym { Value = "syn_a" });

            var tagA = new Tag { Name = "A Tag", SyncId = new Guid("aaaaaaaa-0000-0000-0000-000000000000") };
            tagA.Synonyms.Add(new TagSynonym { Value = "syn_2" });
            tagA.Synonyms.Add(new TagSynonym { Value = "syn_1" });

            db.Tags.AddRange(tagZ, tagA);

            var noteZ = new Note { Text = "Note Z", SyncId = new Guid("eeeeeeee-0000-0000-0000-000000000000") };
            noteZ.NoteTags.Add(new NoteTag { Note = noteZ, Tag = tagZ });
            noteZ.NoteTags.Add(new NoteTag { Note = noteZ, Tag = tagA });

            var noteA = new Note { Text = "Note A", SyncId = new Guid("11111111-0000-0000-0000-000000000000") };
            noteA.NoteTags.Add(new NoteTag { Note = noteA, Tag = tagZ });

            db.Notes.AddRange(noteZ, noteA);

            var tmplZ = new NoteTemplate { Title = "Template Z", SyncId = new Guid("dddddddd-0000-0000-0000-000000000000") };
            tmplZ.TemplateTags.Add(new NoteTemplateTag { Template = tmplZ, Tag = tagZ });
            tmplZ.TemplateTags.Add(new NoteTemplateTag { Template = tmplZ, Tag = tagA });

            var tmplA = new NoteTemplate { Title = "Template A", SyncId = new Guid("22222222-0000-0000-0000-000000000000") };
            tmplA.TemplateTags.Add(new NoteTemplateTag { Template = tmplA, Tag = tagA });

            db.NoteTemplates.AddRange(tmplZ, tmplA);

            var attZ = new NoteAttachment { Note = noteZ, OriginalFileName = "z.txt", SyncId = new Guid("cccccccc-0000-0000-0000-000000000000"), Sha256 = "hashZ" };
            var attA = new NoteAttachment { Note = noteA, OriginalFileName = "a.txt", SyncId = new Guid("33333333-0000-0000-0000-000000000000"), Sha256 = "hashA" };
            db.NoteAttachments.AddRange(attZ, attA);

            db.SaveChanges();
        }

        var crypto = new SyncCryptoService();
        var exporter = new SyncPackageExporter(crypto, new FixedDeviceIdProvider(fixedDeviceId), timeProvider);

        SyncPackagePayload payload1, payload2;
        using (var db = CreateContext(dbPath))
        {
            payload1 = exporter.BuildDeterministicPayload(db, fixedDeviceId);
        }
        using (var db = CreateContext(dbPath))
        {
            payload2 = exporter.BuildDeterministicPayload(db, fixedDeviceId);
        }

        // Assert list orders
        Assert.True(payload1.Tags[0].SyncId < payload1.Tags[1].SyncId);
        Assert.True(payload1.Notes[0].SyncId < payload1.Notes[1].SyncId);
        Assert.True(payload1.Templates[0].SyncId < payload1.Templates[1].SyncId);
        Assert.True(payload1.Attachments[0].SyncId < payload1.Attachments[1].SyncId);

        // Assert synonyms sorted
        Assert.Equal("syn_a", payload1.Tags[1].Synonyms[0]);
        Assert.Equal("syn_z", payload1.Tags[1].Synonyms[1]);

        // Fix PackageId for comparison of payload bytes
        payload2.PackageId = payload1.PackageId;
        payload2.CreatedAtUtc = payload1.CreatedAtUtc;

        byte[] bytes1 = JsonSerializer.SerializeToUtf8Bytes(payload1, SyncPackageExporter.DeterministicJsonOptions);
        byte[] bytes2 = JsonSerializer.SerializeToUtf8Bytes(payload2, SyncPackageExporter.DeterministicJsonOptions);

        Assert.Equal(bytes1, bytes2);
    }

    private static SyncPackagePayload DecryptPayload(string packageJson, string password, ISyncCryptoService? crypto = null)
    {
        crypto ??= new SyncCryptoService();
        var envelope = JsonSerializer.Deserialize<SyncPackageEnvelope>(packageJson, SyncPackageImporter.JsonOptions)!;
        byte[] ciphertext = Convert.FromBase64String(envelope.EncryptedPayloadBase64);
        byte[] salt = Convert.FromBase64String(envelope.Crypto.SaltBase64);
        byte[] nonce = Convert.FromBase64String(envelope.Crypto.NonceBase64);
        byte[] tag = Convert.FromBase64String(envelope.Crypto.TagBase64);
        byte[] associatedData = envelope.GetAssociatedData();

        byte[] plaintextBytes = crypto.DecryptPayload(
            ciphertext,
            salt,
            nonce,
            tag,
            password,
            associatedData,
            envelope.Crypto.KdfIterations);

        return JsonSerializer.Deserialize<SyncPackagePayload>(plaintextBytes, SyncPackageImporter.JsonOptions)!;
    }

    [Fact]
    public async Task SyncPackage_FieldChange_Tag_CreatesNewRevision_ExactlyOnce()
    {
        string dbPath = CreateTempDbPath();
        var deviceId = Guid.NewGuid();
        var crypto = new SyncCryptoService();
        var exporter = new SyncPackageExporter(crypto, new FixedDeviceIdProvider(deviceId));
        string password = "TestPassword123!";

        Guid rootTagSyncId;
        Guid childTagSyncId;

        using (var db = CreateContext(dbPath))
        {
            DbInitializer.Initialize(db);

            var rootTag = new Tag { Name = "Root" };
            var childTag = new Tag { Name = "Child", ParentTag = rootTag };
            childTag.Synonyms.Add(new TagSynonym { Value = "syn1", Tag = childTag });

            db.Tags.AddRange(rootTag, childTag);
            await db.SaveChangesAsync();

            rootTagSyncId = rootTag.SyncId;
            childTagSyncId = childTag.SyncId;
        }

        // Export 1: Initial state
        SyncExportResult res1;
        using (var db = CreateContext(dbPath))
        {
            res1 = await exporter.ExportPackageAsync(db, password, deviceId);
        }
        Assert.True(res1.Success);
        var p1 = DecryptPayload(res1.PackageJson!, password, crypto);
        var tagDto1 = Assert.Single(p1.Tags.Where(t => t.SyncId == childTagSyncId));
        Assert.Equal("Child", tagDto1.Name);
        Assert.Equal(rootTagSyncId, tagDto1.ParentTagSyncId);
        Assert.Single(tagDto1.Synonyms, "syn1");
        Assert.Equal(deviceId, tagDto1.DeviceId);
        var rev1 = tagDto1.RevisionId;
        var parentRev1 = tagDto1.ParentRevisionId;

        // Repeat export 1b: No changes
        SyncExportResult res1b;
        using (var db = CreateContext(dbPath))
        {
            res1b = await exporter.ExportPackageAsync(db, password, deviceId);
        }
        var p1b = DecryptPayload(res1b.PackageJson!, password, crypto);
        var tagDto1b = Assert.Single(p1b.Tags.Where(t => t.SyncId == childTagSyncId));
        Assert.Equal(rev1, tagDto1b.RevisionId);
        Assert.Equal(parentRev1, tagDto1b.ParentRevisionId);

        // Change 1: Name
        using (var db = CreateContext(dbPath))
        {
            var t = await db.Tags.FirstAsync(x => x.SyncId == childTagSyncId);
            t.Name = "ChildRenamed";
            await db.SaveChangesAsync();
        }
        SyncExportResult res2;
        using (var db = CreateContext(dbPath))
        {
            res2 = await exporter.ExportPackageAsync(db, password, deviceId);
        }
        var p2 = DecryptPayload(res2.PackageJson!, password, crypto);
        var tagDto2 = Assert.Single(p2.Tags.Where(t => t.SyncId == childTagSyncId));
        Assert.NotEqual(rev1, tagDto2.RevisionId);
        Assert.Equal(rev1, tagDto2.ParentRevisionId);
        Assert.Equal("ChildRenamed", tagDto2.Name);
        var rev2 = tagDto2.RevisionId;

        // Repeat export 2b
        using (var db = CreateContext(dbPath))
        {
            var res2b = await exporter.ExportPackageAsync(db, password, deviceId);
            var p2b = DecryptPayload(res2b.PackageJson!, password, crypto);
            var tagDto2b = Assert.Single(p2b.Tags.Where(t => t.SyncId == childTagSyncId));
            Assert.Equal(rev2, tagDto2b.RevisionId);
            Assert.Equal(rev1, tagDto2b.ParentRevisionId);
        }

        // Change 2: ParentTag (remove parent)
        using (var db = CreateContext(dbPath))
        {
            var t = await db.Tags.FirstAsync(x => x.SyncId == childTagSyncId);
            t.ParentTagId = null;
            await db.SaveChangesAsync();
        }
        SyncExportResult res3;
        using (var db = CreateContext(dbPath))
        {
            res3 = await exporter.ExportPackageAsync(db, password, deviceId);
        }
        var p3 = DecryptPayload(res3.PackageJson!, password, crypto);
        var tagDto3 = Assert.Single(p3.Tags.Where(t => t.SyncId == childTagSyncId));
        Assert.NotEqual(rev2, tagDto3.RevisionId);
        Assert.Equal(rev2, tagDto3.ParentRevisionId);
        Assert.Null(tagDto3.ParentTagSyncId);
        var rev3 = tagDto3.RevisionId;

        // Repeat export 3b
        using (var db = CreateContext(dbPath))
        {
            var res3b = await exporter.ExportPackageAsync(db, password, deviceId);
            var p3b = DecryptPayload(res3b.PackageJson!, password, crypto);
            var tagDto3b = Assert.Single(p3b.Tags.Where(t => t.SyncId == childTagSyncId));
            Assert.Equal(rev3, tagDto3b.RevisionId);
            Assert.Equal(rev2, tagDto3b.ParentRevisionId);
        }

        // Change 3: Add Synonym
        using (var db = CreateContext(dbPath))
        {
            var t = await db.Tags.Include(x => x.Synonyms).FirstAsync(x => x.SyncId == childTagSyncId);
            t.Synonyms.Add(new TagSynonym { Value = "syn2", Tag = t });
            await db.SaveChangesAsync();
        }
        SyncExportResult res4;
        using (var db = CreateContext(dbPath))
        {
            res4 = await exporter.ExportPackageAsync(db, password, deviceId);
        }
        var p4 = DecryptPayload(res4.PackageJson!, password, crypto);
        var tagDto4 = Assert.Single(p4.Tags.Where(t => t.SyncId == childTagSyncId));
        Assert.NotEqual(rev3, tagDto4.RevisionId);
        Assert.Equal(rev3, tagDto4.ParentRevisionId);
        Assert.Contains("syn1", tagDto4.Synonyms);
        Assert.Contains("syn2", tagDto4.Synonyms);
        var rev4 = tagDto4.RevisionId;

        // Repeat export 4b
        using (var db = CreateContext(dbPath))
        {
            var res4b = await exporter.ExportPackageAsync(db, password, deviceId);
            var p4b = DecryptPayload(res4b.PackageJson!, password, crypto);
            var tagDto4b = Assert.Single(p4b.Tags.Where(t => t.SyncId == childTagSyncId));
            Assert.Equal(rev4, tagDto4b.RevisionId);
            Assert.Equal(rev3, tagDto4b.ParentRevisionId);
        }

        // Change 4: Remove Synonym
        using (var db = CreateContext(dbPath))
        {
            var t = await db.Tags.Include(x => x.Synonyms).FirstAsync(x => x.SyncId == childTagSyncId);
            var sToRemove = t.Synonyms.First(s => s.Value == "syn1");
            db.TagSynonyms.Remove(sToRemove);
            t.Synonyms.Remove(sToRemove);
            await db.SaveChangesAsync();
        }
        SyncExportResult res5;
        using (var db = CreateContext(dbPath))
        {
            res5 = await exporter.ExportPackageAsync(db, password, deviceId);
        }
        var p5 = DecryptPayload(res5.PackageJson!, password, crypto);
        var tagDto5 = Assert.Single(p5.Tags.Where(t => t.SyncId == childTagSyncId));
        Assert.NotEqual(rev4, tagDto5.RevisionId);
        Assert.Equal(rev4, tagDto5.ParentRevisionId);
        Assert.Single(tagDto5.Synonyms, "syn2");
        var rev5 = tagDto5.RevisionId;

        // Repeat export 5b
        using (var db = CreateContext(dbPath))
        {
            var res5b = await exporter.ExportPackageAsync(db, password, deviceId);
            var p5b = DecryptPayload(res5b.PackageJson!, password, crypto);
            var tagDto5b = Assert.Single(p5b.Tags.Where(t => t.SyncId == childTagSyncId));
            Assert.Equal(rev5, tagDto5b.RevisionId);
            Assert.Equal(rev4, tagDto5b.ParentRevisionId);
        }
    }

    [Fact]
    public async Task SyncPackage_FieldChange_Note_CreatesNewRevision_ExactlyOnce()
    {
        string dbPath = CreateTempDbPath();
        var deviceId = Guid.NewGuid();
        var crypto = new SyncCryptoService();
        var exporter = new SyncPackageExporter(crypto, new FixedDeviceIdProvider(deviceId));
        string password = "TestPasswordNote!";

        Guid noteSyncId;

        using (var db = CreateContext(dbPath))
        {
            DbInitializer.Initialize(db);
            var note = new Note
            {
                Text = "Original Note Text",
                IsPinned = false,
                IsFavorite = false,
                IsInbox = false,
                SourceProcessName = null,
                SourceWindowTitle = null,
                SourceUrl = null,
                CapturedAt = null
            };
            db.Notes.Add(note);
            await db.SaveChangesAsync();
            noteSyncId = note.SyncId;
        }

        // Export 1
        var res1 = await exporter.ExportPackageAsync(CreateContext(dbPath), password, deviceId);
        var p1 = DecryptPayload(res1.PackageJson!, password, crypto);
        var n1 = Assert.Single(p1.Notes.Where(n => n.SyncId == noteSyncId));
        Assert.Equal("Original Note Text", n1.Text);
        Assert.False(n1.IsPinned);
        Assert.False(n1.IsFavorite);
        Assert.False(n1.IsInbox);
        Assert.Null(n1.SourceProcessName);
        Assert.Null(n1.SourceWindowTitle);
        Assert.Null(n1.SourceUrl);
        Assert.Null(n1.CapturedAtUtc);
        var rev = n1.RevisionId;

        // Repeat export: no change
        var res1b = await exporter.ExportPackageAsync(CreateContext(dbPath), password, deviceId);
        var n1b = Assert.Single(DecryptPayload(res1b.PackageJson!, password, crypto).Notes.Where(n => n.SyncId == noteSyncId));
        Assert.Equal(rev, n1b.RevisionId);

        // Change 1: Text
        using (var db = CreateContext(dbPath))
        {
            var note = await db.Notes.FirstAsync(n => n.SyncId == noteSyncId);
            note.Text = "Updated Note Text";
            await db.SaveChangesAsync();
        }
        var res2 = await exporter.ExportPackageAsync(CreateContext(dbPath), password, deviceId);
        var n2 = Assert.Single(DecryptPayload(res2.PackageJson!, password, crypto).Notes.Where(n => n.SyncId == noteSyncId));
        Assert.NotEqual(rev, n2.RevisionId);
        Assert.Equal(rev, n2.ParentRevisionId);
        Assert.Equal("Updated Note Text", n2.Text);
        rev = n2.RevisionId;

        // Repeat export
        var res2b = await exporter.ExportPackageAsync(CreateContext(dbPath), password, deviceId);
        Assert.Equal(rev, Assert.Single(DecryptPayload(res2b.PackageJson!, password, crypto).Notes.Where(n => n.SyncId == noteSyncId)).RevisionId);

        // Change 2: IsPinned
        using (var db = CreateContext(dbPath))
        {
            var note = await db.Notes.FirstAsync(n => n.SyncId == noteSyncId);
            note.IsPinned = true;
            await db.SaveChangesAsync();
        }
        var res3 = await exporter.ExportPackageAsync(CreateContext(dbPath), password, deviceId);
        var n3 = Assert.Single(DecryptPayload(res3.PackageJson!, password, crypto).Notes.Where(n => n.SyncId == noteSyncId));
        Assert.NotEqual(rev, n3.RevisionId);
        Assert.Equal(rev, n3.ParentRevisionId);
        Assert.True(n3.IsPinned);
        rev = n3.RevisionId;

        // Change 3: IsFavorite
        using (var db = CreateContext(dbPath))
        {
            var note = await db.Notes.FirstAsync(n => n.SyncId == noteSyncId);
            note.IsFavorite = true;
            await db.SaveChangesAsync();
        }
        var res4 = await exporter.ExportPackageAsync(CreateContext(dbPath), password, deviceId);
        var n4 = Assert.Single(DecryptPayload(res4.PackageJson!, password, crypto).Notes.Where(n => n.SyncId == noteSyncId));
        Assert.NotEqual(rev, n4.RevisionId);
        Assert.Equal(rev, n4.ParentRevisionId);
        Assert.True(n4.IsFavorite);
        rev = n4.RevisionId;

        // Change 4: IsInbox
        using (var db = CreateContext(dbPath))
        {
            var note = await db.Notes.FirstAsync(n => n.SyncId == noteSyncId);
            note.IsInbox = true;
            await db.SaveChangesAsync();
        }
        var res5 = await exporter.ExportPackageAsync(CreateContext(dbPath), password, deviceId);
        var n5 = Assert.Single(DecryptPayload(res5.PackageJson!, password, crypto).Notes.Where(n => n.SyncId == noteSyncId));
        Assert.NotEqual(rev, n5.RevisionId);
        Assert.Equal(rev, n5.ParentRevisionId);
        Assert.True(n5.IsInbox);
        rev = n5.RevisionId;

        // Change 5: SourceProcessName
        using (var db = CreateContext(dbPath))
        {
            var note = await db.Notes.FirstAsync(n => n.SyncId == noteSyncId);
            note.SourceProcessName = "code.exe";
            await db.SaveChangesAsync();
        }
        var res6 = await exporter.ExportPackageAsync(CreateContext(dbPath), password, deviceId);
        var n6 = Assert.Single(DecryptPayload(res6.PackageJson!, password, crypto).Notes.Where(n => n.SyncId == noteSyncId));
        Assert.NotEqual(rev, n6.RevisionId);
        Assert.Equal(rev, n6.ParentRevisionId);
        Assert.Equal("code.exe", n6.SourceProcessName);
        rev = n6.RevisionId;

        // Change 6: SourceWindowTitle
        using (var db = CreateContext(dbPath))
        {
            var note = await db.Notes.FirstAsync(n => n.SyncId == noteSyncId);
            note.SourceWindowTitle = "Program.cs - QuickNotes";
            await db.SaveChangesAsync();
        }
        var res7 = await exporter.ExportPackageAsync(CreateContext(dbPath), password, deviceId);
        var n7 = Assert.Single(DecryptPayload(res7.PackageJson!, password, crypto).Notes.Where(n => n.SyncId == noteSyncId));
        Assert.NotEqual(rev, n7.RevisionId);
        Assert.Equal(rev, n7.ParentRevisionId);
        Assert.Equal("Program.cs - QuickNotes", n7.SourceWindowTitle);
        rev = n7.RevisionId;

        // Change 7: SourceUrl
        using (var db = CreateContext(dbPath))
        {
            var note = await db.Notes.FirstAsync(n => n.SyncId == noteSyncId);
            note.SourceUrl = "https://docs.microsoft.com/dotnet";
            await db.SaveChangesAsync();
        }
        var res8 = await exporter.ExportPackageAsync(CreateContext(dbPath), password, deviceId);
        var n8 = Assert.Single(DecryptPayload(res8.PackageJson!, password, crypto).Notes.Where(n => n.SyncId == noteSyncId));
        Assert.NotEqual(rev, n8.RevisionId);
        Assert.Equal(rev, n8.ParentRevisionId);
        Assert.Equal("https://docs.microsoft.com/dotnet", n8.SourceUrl);
        rev = n8.RevisionId;

        // Change 8: CapturedAt
        var capturedTime = new DateTime(2026, 9, 7, 10, 30, 0, DateTimeKind.Local);
        using (var db = CreateContext(dbPath))
        {
            var note = await db.Notes.FirstAsync(n => n.SyncId == noteSyncId);
            note.CapturedAt = capturedTime;
            await db.SaveChangesAsync();
        }
        var res9 = await exporter.ExportPackageAsync(CreateContext(dbPath), password, deviceId);
        var n9 = Assert.Single(DecryptPayload(res9.PackageJson!, password, crypto).Notes.Where(n => n.SyncId == noteSyncId));
        Assert.NotEqual(rev, n9.RevisionId);
        Assert.Equal(rev, n9.ParentRevisionId);
        Assert.NotNull(n9.CapturedAtUtc);
        Assert.Equal(capturedTime.ToUniversalTime().ToString("O"), n9.CapturedAtUtc.Value.ToString("O"));
        rev = n9.RevisionId;

        // Repeat export 9b: no change
        var res9b = await exporter.ExportPackageAsync(CreateContext(dbPath), password, deviceId);
        Assert.Equal(rev, Assert.Single(DecryptPayload(res9b.PackageJson!, password, crypto).Notes.Where(n => n.SyncId == noteSyncId)).RevisionId);
    }

    [Fact]
    public async Task SyncPackage_FieldChange_NoteTags_OriginAndSuppressed_ChangeRevision_ExactlyOnce()
    {
        string dbPath = CreateTempDbPath();
        var deviceId = Guid.NewGuid();
        var crypto = new SyncCryptoService();
        var exporter = new SyncPackageExporter(crypto, new FixedDeviceIdProvider(deviceId));
        string password = "TestPasswordNoteTags!";

        Guid noteSyncId, tag1SyncId, tag2SyncId;

        using (var db = CreateContext(dbPath))
        {
            DbInitializer.Initialize(db);
            var tag1 = new Tag { Name = "TagAlpha" };
            var tag2 = new Tag { Name = "TagBeta" };
            db.Tags.AddRange(tag1, tag2);

            var note = new Note { Text = "Note for Tag linking test" };
            note.NoteTags.Add(new NoteTag
            {
                Note = note,
                Tag = tag1,
                Origin = TagOrigin.Auto,
                IsSuppressed = false
            });
            db.Notes.Add(note);

            await db.SaveChangesAsync();
            noteSyncId = note.SyncId;
            tag1SyncId = tag1.SyncId;
            tag2SyncId = tag2.SyncId;
        }

        // Export 1
        var res1 = await exporter.ExportPackageAsync(CreateContext(dbPath), password, deviceId);
        var p1 = DecryptPayload(res1.PackageJson!, password, crypto);
        var n1 = Assert.Single(p1.Notes.Where(n => n.SyncId == noteSyncId));
        var nt1 = Assert.Single(n1.Tags);
        Assert.Equal(tag1SyncId, nt1.TagSyncId);
        Assert.Equal(TagOrigin.Auto, nt1.Origin);
        Assert.False(nt1.IsSuppressed);
        var rev = n1.RevisionId;

        // Re-export: no change
        var res1b = await exporter.ExportPackageAsync(CreateContext(dbPath), password, deviceId);
        Assert.Equal(rev, Assert.Single(DecryptPayload(res1b.PackageJson!, password, crypto).Notes.Where(n => n.SyncId == noteSyncId)).RevisionId);

        // Change 1: Origin Auto -> Manual
        using (var db = CreateContext(dbPath))
        {
            var note = await db.Notes.Include(n => n.NoteTags).FirstAsync(n => n.SyncId == noteSyncId);
            var link = note.NoteTags.First();
            link.Origin = TagOrigin.Manual;
            await db.SaveChangesAsync();
        }
        var res2 = await exporter.ExportPackageAsync(CreateContext(dbPath), password, deviceId);
        var n2 = Assert.Single(DecryptPayload(res2.PackageJson!, password, crypto).Notes.Where(n => n.SyncId == noteSyncId));
        Assert.NotEqual(rev, n2.RevisionId);
        Assert.Equal(rev, n2.ParentRevisionId);
        Assert.Equal(TagOrigin.Manual, Assert.Single(n2.Tags).Origin);
        rev = n2.RevisionId;

        // Re-export
        var res2b = await exporter.ExportPackageAsync(CreateContext(dbPath), password, deviceId);
        Assert.Equal(rev, Assert.Single(DecryptPayload(res2b.PackageJson!, password, crypto).Notes.Where(n => n.SyncId == noteSyncId)).RevisionId);

        // Change 2: IsSuppressed false -> true
        using (var db = CreateContext(dbPath))
        {
            var note = await db.Notes.Include(n => n.NoteTags).FirstAsync(n => n.SyncId == noteSyncId);
            var link = note.NoteTags.First();
            link.IsSuppressed = true;
            await db.SaveChangesAsync();
        }
        var res3 = await exporter.ExportPackageAsync(CreateContext(dbPath), password, deviceId);
        var n3 = Assert.Single(DecryptPayload(res3.PackageJson!, password, crypto).Notes.Where(n => n.SyncId == noteSyncId));
        Assert.NotEqual(rev, n3.RevisionId);
        Assert.Equal(rev, n3.ParentRevisionId);
        Assert.True(Assert.Single(n3.Tags).IsSuppressed);
        rev = n3.RevisionId;

        // Change 3: Add second Tag
        using (var db = CreateContext(dbPath))
        {
            var note = await db.Notes.Include(n => n.NoteTags).FirstAsync(n => n.SyncId == noteSyncId);
            var tag2 = await db.Tags.FirstAsync(t => t.SyncId == tag2SyncId);
            note.NoteTags.Add(new NoteTag
            {
                Note = note,
                Tag = tag2,
                Origin = TagOrigin.Manual,
                IsSuppressed = false
            });
            await db.SaveChangesAsync();
        }
        var res4 = await exporter.ExportPackageAsync(CreateContext(dbPath), password, deviceId);
        var n4 = Assert.Single(DecryptPayload(res4.PackageJson!, password, crypto).Notes.Where(n => n.SyncId == noteSyncId));
        Assert.NotEqual(rev, n4.RevisionId);
        Assert.Equal(rev, n4.ParentRevisionId);
        Assert.Equal(2, n4.Tags.Count);
        rev = n4.RevisionId;

        // Change 4: Remove first Tag
        using (var db = CreateContext(dbPath))
        {
            var note = await db.Notes.Include(n => n.NoteTags).ThenInclude(nt => nt.Tag).FirstAsync(n => n.SyncId == noteSyncId);
            var toRemove = note.NoteTags.First(nt => nt.Tag.SyncId == tag1SyncId);
            db.NoteTags.Remove(toRemove);
            note.NoteTags.Remove(toRemove);
            await db.SaveChangesAsync();
        }
        var res5 = await exporter.ExportPackageAsync(CreateContext(dbPath), password, deviceId);
        var n5 = Assert.Single(DecryptPayload(res5.PackageJson!, password, crypto).Notes.Where(n => n.SyncId == noteSyncId));
        Assert.NotEqual(rev, n5.RevisionId);
        Assert.Equal(rev, n5.ParentRevisionId);
        var remainingTag = Assert.Single(n5.Tags);
        Assert.Equal(tag2SyncId, remainingTag.TagSyncId);
        rev = n5.RevisionId;

        // Re-export
        var res5b = await exporter.ExportPackageAsync(CreateContext(dbPath), password, deviceId);
        Assert.Equal(rev, Assert.Single(DecryptPayload(res5b.PackageJson!, password, crypto).Notes.Where(n => n.SyncId == noteSyncId)).RevisionId);
    }

    [Fact]
    public async Task SyncPackage_FieldChange_NoteTemplate_CreatesNewRevision_ExactlyOnce()
    {
        string dbPath = CreateTempDbPath();
        var deviceId = Guid.NewGuid();
        var crypto = new SyncCryptoService();
        var exporter = new SyncPackageExporter(crypto, new FixedDeviceIdProvider(deviceId));
        string password = "TestPasswordTemplate!";

        Guid tmplSyncId, tag1SyncId, tag2SyncId;

        using (var db = CreateContext(dbPath))
        {
            DbInitializer.Initialize(db);
            var tag1 = new Tag { Name = "TmplTag1" };
            var tag2 = new Tag { Name = "TmplTag2" };
            db.Tags.AddRange(tag1, tag2);

            var tmpl = new NoteTemplate
            {
                Title = "Meeting Notes",
                Text = "Agenda:\n1. Update\n2. Next steps"
            };
            tmpl.TemplateTags.Add(new NoteTemplateTag { Template = tmpl, Tag = tag1 });
            db.NoteTemplates.Add(tmpl);

            await db.SaveChangesAsync();
            tmplSyncId = tmpl.SyncId;
            tag1SyncId = tag1.SyncId;
            tag2SyncId = tag2.SyncId;
        }

        // Export 1
        var res1 = await exporter.ExportPackageAsync(CreateContext(dbPath), password, deviceId);
        var p1 = DecryptPayload(res1.PackageJson!, password, crypto);
        var t1 = Assert.Single(p1.Templates.Where(t => t.SyncId == tmplSyncId));
        Assert.Equal("Meeting Notes", t1.Title);
        Assert.Contains("Agenda", t1.Text);
        Assert.Single(t1.TagSyncIds, tag1SyncId);
        var rev = t1.RevisionId;

        // Re-export
        var res1b = await exporter.ExportPackageAsync(CreateContext(dbPath), password, deviceId);
        Assert.Equal(rev, Assert.Single(DecryptPayload(res1b.PackageJson!, password, crypto).Templates.Where(t => t.SyncId == tmplSyncId)).RevisionId);

        // Change 1: Title
        using (var db = CreateContext(dbPath))
        {
            var tmpl = await db.NoteTemplates.FirstAsync(t => t.SyncId == tmplSyncId);
            tmpl.Title = "1-on-1 Meeting Notes";
            await db.SaveChangesAsync();
        }
        var res2 = await exporter.ExportPackageAsync(CreateContext(dbPath), password, deviceId);
        var t2 = Assert.Single(DecryptPayload(res2.PackageJson!, password, crypto).Templates.Where(t => t.SyncId == tmplSyncId));
        Assert.NotEqual(rev, t2.RevisionId);
        Assert.Equal(rev, t2.ParentRevisionId);
        Assert.Equal("1-on-1 Meeting Notes", t2.Title);
        rev = t2.RevisionId;

        // Change 2: Text
        using (var db = CreateContext(dbPath))
        {
            var tmpl = await db.NoteTemplates.FirstAsync(t => t.SyncId == tmplSyncId);
            tmpl.Text = "New Template Body Text";
            await db.SaveChangesAsync();
        }
        var res3 = await exporter.ExportPackageAsync(CreateContext(dbPath), password, deviceId);
        var t3 = Assert.Single(DecryptPayload(res3.PackageJson!, password, crypto).Templates.Where(t => t.SyncId == tmplSyncId));
        Assert.NotEqual(rev, t3.RevisionId);
        Assert.Equal(rev, t3.ParentRevisionId);
        Assert.Equal("New Template Body Text", t3.Text);
        rev = t3.RevisionId;

        // Change 3: Add Tag
        using (var db = CreateContext(dbPath))
        {
            var tmpl = await db.NoteTemplates.Include(t => t.TemplateTags).FirstAsync(t => t.SyncId == tmplSyncId);
            var tag2 = await db.Tags.FirstAsync(t => t.SyncId == tag2SyncId);
            tmpl.TemplateTags.Add(new NoteTemplateTag { Template = tmpl, Tag = tag2 });
            await db.SaveChangesAsync();
        }
        var res4 = await exporter.ExportPackageAsync(CreateContext(dbPath), password, deviceId);
        var t4 = Assert.Single(DecryptPayload(res4.PackageJson!, password, crypto).Templates.Where(t => t.SyncId == tmplSyncId));
        Assert.NotEqual(rev, t4.RevisionId);
        Assert.Equal(rev, t4.ParentRevisionId);
        Assert.Equal(2, t4.TagSyncIds.Count);
        Assert.Contains(tag1SyncId, t4.TagSyncIds);
        Assert.Contains(tag2SyncId, t4.TagSyncIds);
        rev = t4.RevisionId;

        // Change 4: Remove Tag
        using (var db = CreateContext(dbPath))
        {
            var tmpl = await db.NoteTemplates.Include(t => t.TemplateTags).ThenInclude(tt => tt.Tag).FirstAsync(t => t.SyncId == tmplSyncId);
            var toRemove = tmpl.TemplateTags.First(tt => tt.Tag.SyncId == tag1SyncId);
            db.NoteTemplateTags.Remove(toRemove);
            tmpl.TemplateTags.Remove(toRemove);
            await db.SaveChangesAsync();
        }
        var res5 = await exporter.ExportPackageAsync(CreateContext(dbPath), password, deviceId);
        var t5 = Assert.Single(DecryptPayload(res5.PackageJson!, password, crypto).Templates.Where(t => t.SyncId == tmplSyncId));
        Assert.NotEqual(rev, t5.RevisionId);
        Assert.Equal(rev, t5.ParentRevisionId);
        Assert.Single(t5.TagSyncIds, tag2SyncId);
        rev = t5.RevisionId;

        // Re-export
        var res5b = await exporter.ExportPackageAsync(CreateContext(dbPath), password, deviceId);
        Assert.Equal(rev, Assert.Single(DecryptPayload(res5b.PackageJson!, password, crypto).Templates.Where(t => t.SyncId == tmplSyncId)).RevisionId);
    }

    [Fact]
    public async Task SyncPackage_FieldChange_NoteAttachment_CreatesNewRevision_ExactlyOnce()
    {
        string dbPath = CreateTempDbPath();
        var deviceId = Guid.NewGuid();
        var crypto = new SyncCryptoService();
        var exporter = new SyncPackageExporter(crypto, new FixedDeviceIdProvider(deviceId));
        string password = "TestPasswordAttachment!";

        Guid note1SyncId, note2SyncId, attSyncId;

        using (var db = CreateContext(dbPath))
        {
            DbInitializer.Initialize(db);
            var note1 = new Note { Text = "Parent Note 1" };
            var note2 = new Note { Text = "Parent Note 2" };
            db.Notes.AddRange(note1, note2);

            var att = new NoteAttachment
            {
                Note = note1,
                OriginalFileName = "document_v1.pdf",
                ContentType = "application/pdf",
                Size = 1024,
                Sha256 = "hash_initial_12345"
            };
            db.NoteAttachments.Add(att);

            await db.SaveChangesAsync();
            note1SyncId = note1.SyncId;
            note2SyncId = note2.SyncId;
            attSyncId = att.SyncId;
        }

        // Export 1
        var res1 = await exporter.ExportPackageAsync(CreateContext(dbPath), password, deviceId);
        var p1 = DecryptPayload(res1.PackageJson!, password, crypto);
        var a1 = Assert.Single(p1.Attachments.Where(a => a.SyncId == attSyncId));
        Assert.Equal(note1SyncId, a1.NoteSyncId);
        Assert.Equal("document_v1.pdf", a1.OriginalFileName);
        Assert.Equal("application/pdf", a1.ContentType);
        Assert.Equal(1024, a1.Size);
        Assert.Equal("hash_initial_12345", a1.Sha256);
        var rev = a1.RevisionId;

        // Re-export
        var res1b = await exporter.ExportPackageAsync(CreateContext(dbPath), password, deviceId);
        Assert.Equal(rev, Assert.Single(DecryptPayload(res1b.PackageJson!, password, crypto).Attachments.Where(a => a.SyncId == attSyncId)).RevisionId);

        // Change 1: OriginalFileName
        using (var db = CreateContext(dbPath))
        {
            var att = await db.NoteAttachments.FirstAsync(a => a.SyncId == attSyncId);
            att.OriginalFileName = "document_v2.pdf";
            await db.SaveChangesAsync();
        }
        var res2 = await exporter.ExportPackageAsync(CreateContext(dbPath), password, deviceId);
        var a2 = Assert.Single(DecryptPayload(res2.PackageJson!, password, crypto).Attachments.Where(a => a.SyncId == attSyncId));
        Assert.NotEqual(rev, a2.RevisionId);
        Assert.Equal(rev, a2.ParentRevisionId);
        Assert.Equal("document_v2.pdf", a2.OriginalFileName);
        rev = a2.RevisionId;

        // Change 2: ContentType
        using (var db = CreateContext(dbPath))
        {
            var att = await db.NoteAttachments.FirstAsync(a => a.SyncId == attSyncId);
            att.ContentType = "application/octet-stream";
            await db.SaveChangesAsync();
        }
        var res3 = await exporter.ExportPackageAsync(CreateContext(dbPath), password, deviceId);
        var a3 = Assert.Single(DecryptPayload(res3.PackageJson!, password, crypto).Attachments.Where(a => a.SyncId == attSyncId));
        Assert.NotEqual(rev, a3.RevisionId);
        Assert.Equal(rev, a3.ParentRevisionId);
        Assert.Equal("application/octet-stream", a3.ContentType);
        rev = a3.RevisionId;

        // Change 3: Size
        using (var db = CreateContext(dbPath))
        {
            var att = await db.NoteAttachments.FirstAsync(a => a.SyncId == attSyncId);
            att.Size = 2048;
            await db.SaveChangesAsync();
        }
        var res4 = await exporter.ExportPackageAsync(CreateContext(dbPath), password, deviceId);
        var a4 = Assert.Single(DecryptPayload(res4.PackageJson!, password, crypto).Attachments.Where(a => a.SyncId == attSyncId));
        Assert.NotEqual(rev, a4.RevisionId);
        Assert.Equal(rev, a4.ParentRevisionId);
        Assert.Equal(2048, a4.Size);
        rev = a4.RevisionId;

        // Change 4: Sha256
        using (var db = CreateContext(dbPath))
        {
            var att = await db.NoteAttachments.FirstAsync(a => a.SyncId == attSyncId);
            att.Sha256 = "hash_modified_99999";
            await db.SaveChangesAsync();
        }
        var res5 = await exporter.ExportPackageAsync(CreateContext(dbPath), password, deviceId);
        var a5 = Assert.Single(DecryptPayload(res5.PackageJson!, password, crypto).Attachments.Where(a => a.SyncId == attSyncId));
        Assert.NotEqual(rev, a5.RevisionId);
        Assert.Equal(rev, a5.ParentRevisionId);
        Assert.Equal("hash_modified_99999", a5.Sha256);
        rev = a5.RevisionId;

        // Change 5: NoteSyncId (parent Note change)
        using (var db = CreateContext(dbPath))
        {
            var att = await db.NoteAttachments.FirstAsync(a => a.SyncId == attSyncId);
            var note2 = await db.Notes.FirstAsync(n => n.SyncId == note2SyncId);
            att.Note = note2;
            att.NoteId = note2.Id;
            await db.SaveChangesAsync();
        }
        var res6 = await exporter.ExportPackageAsync(CreateContext(dbPath), password, deviceId);
        var a6 = Assert.Single(DecryptPayload(res6.PackageJson!, password, crypto).Attachments.Where(a => a.SyncId == attSyncId));
        Assert.NotEqual(rev, a6.RevisionId);
        Assert.Equal(rev, a6.ParentRevisionId);
        Assert.Equal(note2SyncId, a6.NoteSyncId);
        rev = a6.RevisionId;

        // Re-export
        var res6b = await exporter.ExportPackageAsync(CreateContext(dbPath), password, deviceId);
        Assert.Equal(rev, Assert.Single(DecryptPayload(res6b.PackageJson!, password, crypto).Attachments.Where(a => a.SyncId == attSyncId)).RevisionId);
    }

    [Fact]
    public async Task SyncPackage_MigratedStateWithEmptyDeviceId_GetsRealDeviceId_PackageNeverContainsGuidEmpty()
    {
        string dbPath = CreateTempDbPath();
        var designatedDeviceId = Guid.NewGuid();
        var crypto = new SyncCryptoService();
        var exporter = new SyncPackageExporter(crypto, new FixedDeviceIdProvider(designatedDeviceId));
        string password = "TestMigrationDeviceId!";

        using (var conn = new SqliteConnection($"Data Source={dbPath}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                CREATE TABLE Notes (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    Text TEXT NOT NULL,
                    CreatedAt TEXT NOT NULL,
                    UpdatedAt TEXT NOT NULL,
                    IsPinned INTEGER NOT NULL DEFAULT 0,
                    IsFavorite INTEGER NOT NULL DEFAULT 0,
                    IsInbox INTEGER NOT NULL DEFAULT 0,
                    DeletedAt TEXT NULL,
                    CapturedAt TEXT NULL,
                    SourceProcessName TEXT NULL,
                    SourceWindowTitle TEXT NULL,
                    SourceUrl TEXT NULL,
                    SyncId TEXT NOT NULL
                );
                CREATE TABLE Tags (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    Name TEXT NOT NULL,
                    ParentTagId INTEGER NULL,
                    SyncId TEXT NOT NULL
                );
                CREATE TABLE NoteTemplates (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    Title TEXT NOT NULL COLLATE NOCASE,
                    Text TEXT NOT NULL,
                    CreatedAt TEXT NOT NULL,
                    UpdatedAt TEXT NOT NULL,
                    SyncId TEXT NOT NULL
                );
                CREATE TABLE NoteAttachments (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    NoteId INTEGER NOT NULL,
                    StoredFileName TEXT NOT NULL,
                    OriginalFileName TEXT NOT NULL,
                    ContentType TEXT NOT NULL,
                    Size INTEGER NOT NULL,
                    Sha256 TEXT NOT NULL,
                    CreatedAt TEXT NOT NULL,
                    RelativePath TEXT NOT NULL,
                    SyncId TEXT NOT NULL
                );
                CREATE TABLE NoteTags (NoteId INTEGER, TagId INTEGER, Origin INTEGER, IsSuppressed INTEGER, PRIMARY KEY(NoteId, TagId));
                CREATE TABLE NoteTemplateTags (TemplateId INTEGER, TagId INTEGER, PRIMARY KEY(TemplateId, TagId));
                CREATE TABLE TagSynonyms (Id INTEGER PRIMARY KEY AUTOINCREMENT, TagId INTEGER, Value TEXT);
                CREATE TABLE NoteRevisions (Id INTEGER PRIMARY KEY AUTOINCREMENT, NoteId INTEGER, CreatedAt TEXT, Text TEXT, TagsJson TEXT);
                PRAGMA user_version = 4;
            ";
            cmd.ExecuteNonQuery();

            cmd.CommandText = $@"
                INSERT INTO Notes (Text, CreatedAt, UpdatedAt, SyncId) VALUES ('Migrated note', '{DateTime.UtcNow:O}', '{DateTime.UtcNow:O}', '{Guid.NewGuid()}');
                INSERT INTO Tags (Name, SyncId) VALUES ('Migrated tag', '{Guid.NewGuid()}');
                INSERT INTO NoteTemplates (Title, Text, CreatedAt, UpdatedAt, SyncId) VALUES ('Migrated template', 'text', '{DateTime.UtcNow:O}', '{DateTime.UtcNow:O}', '{Guid.NewGuid()}');
                INSERT INTO NoteAttachments (NoteId, StoredFileName, OriginalFileName, ContentType, Size, Sha256, CreatedAt, RelativePath, SyncId)
                VALUES (1, 'att.bin', 'att.txt', 'text/plain', 123, 'sha', '{DateTime.UtcNow:O}', 'att.bin', '{Guid.NewGuid()}');
            ";
            cmd.ExecuteNonQuery();
        }

        // Run upgrade
        using (var db = CreateContext(dbPath))
        {
            DbInitializer.Initialize(db);
        }

        // Verify that initial migrated states have DeviceId == Guid.Empty and ContentHash == null
        using (var db = CreateContext(dbPath))
        {
            var states = await db.SyncEntityStates.ToListAsync();
            Assert.Equal(4, states.Count);
            foreach (var state in states)
            {
                Assert.Equal(Guid.Empty, state.DeviceId);
                Assert.Null(state.ContentHash);
            }
        }

        // First Export
        SyncExportResult exportResult;
        using (var db = CreateContext(dbPath))
        {
            exportResult = await exporter.ExportPackageAsync(db, password, designatedDeviceId);
        }
        Assert.True(exportResult.Success, string.Join("; ", exportResult.Errors));

        // Verify envelope
        var envelope = JsonSerializer.Deserialize<SyncPackageEnvelope>(exportResult.PackageJson!, SyncPackageImporter.JsonOptions)!;
        Assert.NotEqual(Guid.Empty, envelope.DeviceId);
        Assert.Equal(designatedDeviceId, envelope.DeviceId);

        // Verify decrypted payload
        var payload = DecryptPayload(exportResult.PackageJson!, password, crypto);
        Assert.Equal(designatedDeviceId, payload.SourceDeviceId);
        Assert.NotEqual(Guid.Empty, payload.SourceDeviceId);

        Assert.All(payload.Tags, t => { Assert.NotEqual(Guid.Empty, t.DeviceId); Assert.Equal(designatedDeviceId, t.DeviceId); });
        Assert.All(payload.Notes, n => { Assert.NotEqual(Guid.Empty, n.DeviceId); Assert.Equal(designatedDeviceId, n.DeviceId); });
        Assert.All(payload.Templates, t => { Assert.NotEqual(Guid.Empty, t.DeviceId); Assert.Equal(designatedDeviceId, t.DeviceId); });
        Assert.All(payload.Attachments, a => { Assert.NotEqual(Guid.Empty, a.DeviceId); Assert.Equal(designatedDeviceId, a.DeviceId); });

        // Verify that in the database, states are updated with designatedDeviceId and non-null ContentHash
        using (var db = CreateContext(dbPath))
        {
            var states = await db.SyncEntityStates.ToListAsync();
            Assert.Equal(4, states.Count);
            foreach (var state in states)
            {
                Assert.Equal(designatedDeviceId, state.DeviceId);
                Assert.False(string.IsNullOrEmpty(state.ContentHash));
            }
        }

        // Second Export: RevisionId and ParentRevisionId are preserved
        SyncExportResult exportResult2;
        using (var db = CreateContext(dbPath))
        {
            exportResult2 = await exporter.ExportPackageAsync(db, password, designatedDeviceId);
        }
        var payload2 = DecryptPayload(exportResult2.PackageJson!, password, crypto);
        for (int i = 0; i < payload.Tags.Count; i++)
        {
            Assert.Equal(payload.Tags[i].RevisionId, payload2.Tags[i].RevisionId);
            Assert.Equal(payload.Tags[i].ParentRevisionId, payload2.Tags[i].ParentRevisionId);
        }
        for (int i = 0; i < payload.Notes.Count; i++)
        {
            Assert.Equal(payload.Notes[i].RevisionId, payload2.Notes[i].RevisionId);
            Assert.Equal(payload.Notes[i].ParentRevisionId, payload2.Notes[i].ParentRevisionId);
        }
        for (int i = 0; i < payload.Templates.Count; i++)
        {
            Assert.Equal(payload.Templates[i].RevisionId, payload2.Templates[i].RevisionId);
            Assert.Equal(payload.Templates[i].ParentRevisionId, payload2.Templates[i].ParentRevisionId);
        }
        for (int i = 0; i < payload.Attachments.Count; i++)
        {
            Assert.Equal(payload.Attachments[i].RevisionId, payload2.Attachments[i].RevisionId);
            Assert.Equal(payload.Attachments[i].ParentRevisionId, payload2.Attachments[i].ParentRevisionId);
        }
    }

    [Fact]
    public async Task SyncPackage_ImportThenImmediateExport_CreatesNoFalseRevisions_ForAllEntityTypes()
    {
        string srcDbPath = CreateTempDbPath();
        string dstDbPath = CreateTempDbPath();
        var deviceA = Guid.NewGuid();
        var deviceB = Guid.NewGuid();
        var crypto = new SyncCryptoService();
        var exporterA = new SyncPackageExporter(crypto, new FixedDeviceIdProvider(deviceA));
        var exporterB = new SyncPackageExporter(crypto, new FixedDeviceIdProvider(deviceB));
        var importerB = new SyncPackageImporter(crypto);
        string password = "StrongMasterPass123!";

        // Device A prepares entities
        using (var srcDb = CreateContext(srcDbPath))
        {
            DbInitializer.Initialize(srcDb);
            var rootTag = new Tag { Name = "Category" };
            var childTag = new Tag { Name = "SubCategory", ParentTag = rootTag };
            childTag.Synonyms.Add(new TagSynonym { Value = "subcat", Tag = childTag });
            srcDb.Tags.AddRange(rootTag, childTag);

            var note = new Note
            {
                Text = "Comprehensive test note",
                IsPinned = true,
                IsFavorite = true,
                IsInbox = false,
                SourceProcessName = "chrome.exe",
                SourceWindowTitle = "Article - Web",
                SourceUrl = "https://example.org/article",
                CapturedAt = new DateTime(2026, 9, 7, 8, 30, 0, DateTimeKind.Utc)
            };
            note.NoteTags.Add(new NoteTag { Note = note, Tag = childTag, Origin = TagOrigin.Manual, IsSuppressed = false });
            srcDb.Notes.Add(note);

            var tmpl = new NoteTemplate { Title = "Weekly Sync", Text = "Template contents" };
            tmpl.TemplateTags.Add(new NoteTemplateTag { Template = tmpl, Tag = childTag });
            srcDb.NoteTemplates.Add(tmpl);

            var att = new NoteAttachment
            {
                Note = note,
                OriginalFileName = "spec.pdf",
                ContentType = "application/pdf",
                Size = 4096,
                Sha256 = "deadbeef12345678"
            };
            srcDb.NoteAttachments.Add(att);

            await srcDb.SaveChangesAsync();
        }

        // Device A exports package
        SyncExportResult exportA;
        using (var srcDb = CreateContext(srcDbPath))
        {
            exportA = await exporterA.ExportPackageAsync(srcDb, password, deviceA);
        }
        Assert.True(exportA.Success);

        var payloadA = DecryptPayload(exportA.PackageJson!, password, crypto);

        // Device B imports package
        SyncImportResult importB;
        using (var dstDb = CreateContext(dstDbPath))
        {
            DbInitializer.Initialize(dstDb);
            importB = await importerB.ImportPackageAsync(dstDb, exportA.PackageJson!, password);
        }
        Assert.True(importB.Success);
        Assert.Empty(importB.Conflicts);
        Assert.Empty(importB.Errors);

        // Device B immediately exports package without any local modifications
        SyncExportResult exportB;
        using (var dstDb = CreateContext(dstDbPath))
        {
            exportB = await exporterB.ExportPackageAsync(dstDb, password, deviceB);
        }
        Assert.True(exportB.Success);

        var payloadB = DecryptPayload(exportB.PackageJson!, password, crypto);

        // Assert: NO false revisions created on Device B!
        // Revisions and ParentRevisions MUST match payloadA exactly!
        Assert.Equal(payloadA.Tags.Count, payloadB.Tags.Count);
        for (int i = 0; i < payloadA.Tags.Count; i++)
        {
            var tagA = payloadA.Tags[i];
            var tagB = Assert.Single(payloadB.Tags.Where(t => t.SyncId == tagA.SyncId));
            Assert.Equal(tagA.RevisionId, tagB.RevisionId);
            Assert.Equal(tagA.ParentRevisionId, tagB.ParentRevisionId);
            Assert.Equal(tagA.DeviceId, tagB.DeviceId);
            Assert.Equal(tagA.Name, tagB.Name);
            Assert.Equal(tagA.ParentTagSyncId, tagB.ParentTagSyncId);
        }

        Assert.Equal(payloadA.Notes.Count, payloadB.Notes.Count);
        for (int i = 0; i < payloadA.Notes.Count; i++)
        {
            var noteA = payloadA.Notes[i];
            var noteB = Assert.Single(payloadB.Notes.Where(n => n.SyncId == noteA.SyncId));
            Assert.Equal(noteA.RevisionId, noteB.RevisionId);
            Assert.Equal(noteA.ParentRevisionId, noteB.ParentRevisionId);
            Assert.Equal(noteA.DeviceId, noteB.DeviceId);
            Assert.Equal(noteA.Text, noteB.Text);
            Assert.Equal(noteA.Tags.Count, noteB.Tags.Count);
        }

        Assert.Equal(payloadA.Templates.Count, payloadB.Templates.Count);
        for (int i = 0; i < payloadA.Templates.Count; i++)
        {
            var tmplA = payloadA.Templates[i];
            var tmplB = Assert.Single(payloadB.Templates.Where(t => t.SyncId == tmplA.SyncId));
            Assert.Equal(tmplA.RevisionId, tmplB.RevisionId);
            Assert.Equal(tmplA.ParentRevisionId, tmplB.ParentRevisionId);
            Assert.Equal(tmplA.DeviceId, tmplB.DeviceId);
            Assert.Equal(tmplA.Title, tmplB.Title);
        }

        Assert.Equal(payloadA.Attachments.Count, payloadB.Attachments.Count);
        for (int i = 0; i < payloadA.Attachments.Count; i++)
        {
            var attA = payloadA.Attachments[i];
            var attB = Assert.Single(payloadB.Attachments.Where(a => a.SyncId == attA.SyncId));
            Assert.Equal(attA.RevisionId, attB.RevisionId);
            Assert.Equal(attA.ParentRevisionId, attB.ParentRevisionId);
            Assert.Equal(attA.DeviceId, attB.DeviceId);
            Assert.Equal(attA.OriginalFileName, attB.OriginalFileName);
            Assert.Equal(attA.Sha256, attB.Sha256);
        }
    }

    [Fact]
    public async Task SyncPackage_DeleteAndRestore_UpdatesRevisionAndFingerprintCorrectly()
    {
        string dbPath = CreateTempDbPath();
        var deviceId = Guid.NewGuid();
        var crypto = new SyncCryptoService();
        var exporter = new SyncPackageExporter(crypto, new FixedDeviceIdProvider(deviceId));
        string password = "TestPasswordDelRestore!";

        Guid noteSyncId;

        using (var db = CreateContext(dbPath))
        {
            DbInitializer.Initialize(db);
            var note = new Note { Text = "Note to delete and restore" };
            db.Notes.Add(note);
            await db.SaveChangesAsync();
            noteSyncId = note.SyncId;
        }

        // Export 1: Active
        var res1 = await exporter.ExportPackageAsync(CreateContext(dbPath), password, deviceId);
        var p1 = DecryptPayload(res1.PackageJson!, password, crypto);
        var n1 = Assert.Single(p1.Notes.Where(n => n.SyncId == noteSyncId));
        Assert.Equal(SyncOperationType.Upsert, n1.Operation);
        Assert.Equal("Note to delete and restore", n1.Text);
        var rev1 = n1.RevisionId;

        // Re-export 1b: unchanged
        var res1b = await exporter.ExportPackageAsync(CreateContext(dbPath), password, deviceId);
        Assert.Equal(rev1, Assert.Single(DecryptPayload(res1b.PackageJson!, password, crypto).Notes.Where(n => n.SyncId == noteSyncId)).RevisionId);

        // Soft delete note
        using (var db = CreateContext(dbPath))
        {
            var note = await db.Notes.FirstAsync(n => n.SyncId == noteSyncId);
            note.DeletedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }

        // Export 2: Deleted (Tombstone)
        var res2 = await exporter.ExportPackageAsync(CreateContext(dbPath), password, deviceId);
        var p2 = DecryptPayload(res2.PackageJson!, password, crypto);
        var n2 = Assert.Single(p2.Notes.Where(n => n.SyncId == noteSyncId));
        Assert.Equal(SyncOperationType.Delete, n2.Operation);
        Assert.NotEqual(rev1, n2.RevisionId);
        Assert.Equal(rev1, n2.ParentRevisionId);
        var rev2 = n2.RevisionId;

        // Check DB state has tombstone hash
        using (var db = CreateContext(dbPath))
        {
            var state = await db.SyncEntityStates.FirstAsync(s => s.SyncId == noteSyncId);
            Assert.True(state.IsDeleted);
            Assert.NotNull(state.DeletedAtUtc);
            Assert.Equal(SyncFingerprintHelper.ComputeTombstoneFingerprint("Note"), state.ContentHash);
        }

        // Re-export 2b while deleted: no change
        var res2b = await exporter.ExportPackageAsync(CreateContext(dbPath), password, deviceId);
        var n2b = Assert.Single(DecryptPayload(res2b.PackageJson!, password, crypto).Notes.Where(n => n.SyncId == noteSyncId));
        Assert.Equal(rev2, n2b.RevisionId);
        Assert.Equal(rev1, n2b.ParentRevisionId);
        Assert.Equal(SyncOperationType.Delete, n2b.Operation);

        // Restore note
        using (var db = CreateContext(dbPath))
        {
            var note = await db.Notes.FirstAsync(n => n.SyncId == noteSyncId);
            note.DeletedAt = null;
            await db.SaveChangesAsync();
        }

        // Export 3: Restored
        var res3 = await exporter.ExportPackageAsync(CreateContext(dbPath), password, deviceId);
        var p3 = DecryptPayload(res3.PackageJson!, password, crypto);
        var n3 = Assert.Single(p3.Notes.Where(n => n.SyncId == noteSyncId));
        Assert.Equal(SyncOperationType.Upsert, n3.Operation);
        Assert.NotEqual(rev2, n3.RevisionId);
        Assert.Equal(rev2, n3.ParentRevisionId);
        Assert.Equal("Note to delete and restore", n3.Text);
        var rev3 = n3.RevisionId;

        // Check DB state is restored
        using (var db = CreateContext(dbPath))
        {
            var state = await db.SyncEntityStates.FirstAsync(s => s.SyncId == noteSyncId);
            Assert.False(state.IsDeleted);
            Assert.Null(state.DeletedAtUtc);
            Assert.NotEqual(SyncFingerprintHelper.ComputeTombstoneFingerprint("Note"), state.ContentHash);
        }

        // Re-export 3b: no change
        var res3b = await exporter.ExportPackageAsync(CreateContext(dbPath), password, deviceId);
        var n3b = Assert.Single(DecryptPayload(res3b.PackageJson!, password, crypto).Notes.Where(n => n.SyncId == noteSyncId));
        Assert.Equal(rev3, n3b.RevisionId);
        Assert.Equal(rev2, n3b.ParentRevisionId);
        Assert.Equal(SyncOperationType.Upsert, n3b.Operation);
    }

    [Fact]
    public void SyncPackage_MigrationV6_FromV5_AndRepeatedRun_AndPartiallyUpdatedSchema()
    {
        string dbPath = CreateTempDbPath();

        // 1. Create a Schema Version 5 database (has SyncEntityStates table without ContentHash column)
        using (var conn = new SqliteConnection($"Data Source={dbPath}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                CREATE TABLE Notes (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    Text TEXT NOT NULL,
                    CreatedAt TEXT NOT NULL,
                    UpdatedAt TEXT NOT NULL,
                    IsPinned INTEGER NOT NULL DEFAULT 0,
                    IsFavorite INTEGER NOT NULL DEFAULT 0,
                    IsInbox INTEGER NOT NULL DEFAULT 0,
                    DeletedAt TEXT NULL,
                    CapturedAt TEXT NULL,
                    SourceProcessName TEXT NULL,
                    SourceWindowTitle TEXT NULL,
                    SourceUrl TEXT NULL,
                    SyncId TEXT NOT NULL
                );
                CREATE UNIQUE INDEX IX_Notes_SyncId ON Notes(SyncId);

                CREATE TABLE Tags (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    Name TEXT NOT NULL,
                    ParentTagId INTEGER NULL,
                    SyncId TEXT NOT NULL
                );
                CREATE UNIQUE INDEX IX_Tags_SyncId ON Tags(SyncId);

                CREATE TABLE NoteTemplates (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    Title TEXT NOT NULL COLLATE NOCASE,
                    Text TEXT NOT NULL,
                    CreatedAt TEXT NOT NULL,
                    UpdatedAt TEXT NOT NULL,
                    SyncId TEXT NOT NULL
                );
                CREATE UNIQUE INDEX IX_NoteTemplates_SyncId ON NoteTemplates(SyncId);
                CREATE UNIQUE INDEX IX_NoteTemplates_Title ON NoteTemplates(Title);

                CREATE TABLE NoteAttachments (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    NoteId INTEGER NOT NULL,
                    StoredFileName TEXT NOT NULL,
                    OriginalFileName TEXT NOT NULL,
                    ContentType TEXT NOT NULL,
                    Size INTEGER NOT NULL,
                    Sha256 TEXT NOT NULL,
                    CreatedAt TEXT NOT NULL,
                    RelativePath TEXT NOT NULL,
                    SyncId TEXT NOT NULL
                );
                CREATE UNIQUE INDEX IX_NoteAttachments_SyncId ON NoteAttachments(SyncId);

                CREATE TABLE NoteTags (NoteId INTEGER, TagId INTEGER, Origin INTEGER, IsSuppressed INTEGER, PRIMARY KEY(NoteId, TagId));
                CREATE TABLE NoteTemplateTags (TemplateId INTEGER, TagId INTEGER, PRIMARY KEY(TemplateId, TagId));
                CREATE TABLE TagSynonyms (Id INTEGER PRIMARY KEY AUTOINCREMENT, TagId INTEGER, Value TEXT);
                CREATE TABLE NoteRevisions (Id INTEGER PRIMARY KEY AUTOINCREMENT, NoteId INTEGER, CreatedAt TEXT, Text TEXT, TagsJson TEXT);

                CREATE TABLE SyncEntityStates (
                    SyncId TEXT PRIMARY KEY,
                    EntityType TEXT NOT NULL,
                    RevisionId TEXT NOT NULL,
                    ParentRevisionId TEXT NULL,
                    DeviceId TEXT NOT NULL,
                    UpdatedAtUtc TEXT NOT NULL,
                    IsDeleted INTEGER NOT NULL DEFAULT 0,
                    DeletedAtUtc TEXT NULL
                );
                CREATE INDEX IX_SyncEntityStates_EntityType ON SyncEntityStates(EntityType);
                CREATE INDEX IX_SyncEntityStates_RevisionId ON SyncEntityStates(RevisionId);

                PRAGMA user_version = 5;
            ";
            cmd.ExecuteNonQuery();

            // Insert a note and tag in v5
            cmd.CommandText = $@"
                INSERT INTO Notes (Text, CreatedAt, UpdatedAt, SyncId) VALUES ('V5 Note Text', '{DateTime.UtcNow:O}', '{DateTime.UtcNow:O}', '{Guid.NewGuid()}');
                INSERT INTO Tags (Name, SyncId) VALUES ('V5 Tag', '{Guid.NewGuid()}');
            ";
            cmd.ExecuteNonQuery();
        }

        // Case A: Migration v5 -> v6
        using (var db = CreateContext(dbPath))
        {
            DbInitializer.Initialize(db);
        }

        using (var conn = new SqliteConnection($"Data Source={dbPath}"))
        {
            conn.Open();
            Assert.True(DbInitializer.ColumnExists(conn, "SyncEntityStates", "ContentHash"));
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA user_version;";
            long ver = Convert.ToInt64(cmd.ExecuteScalar());
            Assert.Equal(DbInitializer.CurrentSchemaVersion, ver);

            cmd.CommandText = "SELECT COUNT(*) FROM SyncEntityStates;";
            long stateCount = Convert.ToInt64(cmd.ExecuteScalar());
            Assert.Equal(2, stateCount);
        }

        // Case B: Repeated execution on current schema (idempotency)
        using (var db = CreateContext(dbPath))
        {
            DbInitializer.Initialize(db);
        }

        using (var conn = new SqliteConnection($"Data Source={dbPath}"))
        {
            conn.Open();
            Assert.True(DbInitializer.ColumnExists(conn, "SyncEntityStates", "ContentHash"));
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA user_version;";
            long ver = Convert.ToInt64(cmd.ExecuteScalar());
            Assert.Equal(DbInitializer.CurrentSchemaVersion, ver);

            cmd.CommandText = "SELECT COUNT(*) FROM SyncEntityStates;";
            long stateCount = Convert.ToInt64(cmd.ExecuteScalar());
            Assert.Equal(2, stateCount);
        }

        // Case C: Partially updated schema (e.g. ContentHash column dropped, but user_version = current)
        using (var conn = new SqliteConnection($"Data Source={dbPath}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "ALTER TABLE SyncEntityStates DROP COLUMN ContentHash;";
            cmd.ExecuteNonQuery();
            Assert.False(DbInitializer.ColumnExists(conn, "SyncEntityStates", "ContentHash"));
        }

        // Run Initialize on partially updated schema
        using (var db = CreateContext(dbPath))
        {
            DbInitializer.Initialize(db);
        }

        using (var conn = new SqliteConnection($"Data Source={dbPath}"))
        {
            conn.Open();
            Assert.True(DbInitializer.ColumnExists(conn, "SyncEntityStates", "ContentHash"));
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA user_version;";
            long ver = Convert.ToInt64(cmd.ExecuteScalar());
            Assert.Equal(DbInitializer.CurrentSchemaVersion, ver);
        }
    }

    [Fact]
    public async Task SyncPackage_AtomicImport_RollbackLeavesDatabaseUnmodified_WhenErrorOccurs()
    {
        string dbPath = CreateTempDbPath();
        string password = "TestPasswordRollback!";
        var crypto = new SyncCryptoService();
        var importer = new SyncPackageImporter(crypto);

        Guid initialNoteSyncId, initialTagSyncId;
        using (var db = CreateContext(dbPath))
        {
            DbInitializer.Initialize(db);
            var tag = new Tag { Name = "Existing Tag" };
            db.Tags.Add(tag);
            var note = new Note { Text = "Existing Note" };
            db.Notes.Add(note);
            await db.SaveChangesAsync();

            initialTagSyncId = tag.SyncId;
            initialNoteSyncId = note.SyncId;
        }

        // Create a payload with a valid tag, and an invalid note with Text = null that violates SQLite NOT NULL constraint
        var payload = new SyncPackagePayload
        {
            PayloadVersion = 1,
            PackageId = Guid.NewGuid(),
            SourceDeviceId = Guid.NewGuid(),
            CreatedAtUtc = DateTime.UtcNow
        };

        var validTag = new SyncTagDto
        {
            SyncId = Guid.NewGuid(),
            RevisionId = Guid.NewGuid(),
            DeviceId = Guid.NewGuid(),
            UpdatedAtUtc = DateTime.UtcNow,
            Operation = SyncOperationType.Upsert,
            Name = "New Valid Tag"
        };
        payload.Tags.Add(validTag);

        var invalidNote = new SyncNoteDto
        {
            SyncId = Guid.NewGuid(),
            RevisionId = Guid.NewGuid(),
            DeviceId = Guid.NewGuid(),
            UpdatedAtUtc = DateTime.UtcNow,
            Operation = SyncOperationType.Upsert,
            Text = null! // Violates NOT NULL in SQLite Notes table
        };
        payload.Notes.Add(invalidNote);

        // Encrypt envelope
        byte[] plaintextBytes = JsonSerializer.SerializeToUtf8Bytes(payload, SyncPackageExporter.DeterministicJsonOptions);
        var envelope = new SyncPackageEnvelope
        {
            Magic = "QNSP",
            FormatVersion = 1,
            PackageId = payload.PackageId,
            DeviceId = payload.SourceDeviceId,
            CreatedAtUtc = payload.CreatedAtUtc,
            Crypto = new SyncCryptoHeader
            {
                Algorithm = SyncCryptoService.AlgorithmName,
                KdfAlgorithm = SyncCryptoService.KdfName,
                KdfVersion = SyncCryptoService.KdfVersion,
                KdfIterations = SyncCryptoService.DefaultIterations
            }
        };
        var encResult = crypto.EncryptPayload(plaintextBytes, password, envelope.GetAssociatedData());
        envelope.Crypto.SaltBase64 = Convert.ToBase64String(encResult.Salt);
        envelope.Crypto.NonceBase64 = Convert.ToBase64String(encResult.Nonce);
        envelope.Crypto.TagBase64 = Convert.ToBase64String(encResult.Tag);
        envelope.EncryptedPayloadBase64 = Convert.ToBase64String(encResult.Ciphertext);

        string packageJson = JsonSerializer.Serialize(envelope);

        // Attempt import
        SyncImportResult importResult;
        using (var db = CreateContext(dbPath))
        {
            importResult = await importer.ImportPackageAsync(db, packageJson, password);
        }

        // Assert failure
        Assert.False(importResult.Success);
        Assert.NotEmpty(importResult.Errors);

        // Assert database is completely UNTOUCHED
        using (var db = CreateContext(dbPath))
        {
            var tags = await db.Tags.ToListAsync();
            Assert.Single(tags);
            Assert.Equal(initialTagSyncId, tags[0].SyncId);
            Assert.Equal("Existing Tag", tags[0].Name);

            var notes = await db.Notes.ToListAsync();
            Assert.Single(notes);
            Assert.Equal(initialNoteSyncId, notes[0].SyncId);
            Assert.Equal("Existing Note", notes[0].Text);

            Assert.DoesNotContain(tags, t => t.SyncId == validTag.SyncId);
            Assert.DoesNotContain(notes, n => n.SyncId == invalidNote.SyncId);
        }
    }
}
