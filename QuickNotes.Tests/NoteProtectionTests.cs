using System;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using QuickNotes.App.Data;
using QuickNotes.App.Models;
using QuickNotes.App.Services;
using QuickNotes.App.Services.NoteProtection;
using Xunit;

namespace QuickNotes.Tests;

[TestCategory(TestCategories.Integration)]
public class NoteProtectionTests
{
    private static string CreateTempDb()
    {
        return Path.Combine(Path.GetTempPath(), $"quicknotes_protect_{Guid.NewGuid():N}.db");
    }

    private static void CleanupDb(string dbPath)
    {
        SqliteConnection.ClearAllPools();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        if (File.Exists(dbPath))
        {
            try { File.Delete(dbPath); } catch { /* best effort */ }
        }
    }

    private static QuickNotesDbContext CreateContext(string dbPath)
    {
        var options = new DbContextOptionsBuilder<QuickNotesDbContext>()
            .UseSqlite($"Data Source={dbPath}")
            .Options;
        return new QuickNotesDbContext(options);
    }

    [Fact]
    public void CryptoService_RoundTrip_EncryptsAndDecrypts()
    {
        var crypto = new NoteCryptoService();
        var syncId = Guid.NewGuid();
        byte[] plaintext = System.Text.Encoding.UTF8.GetBytes("Секретный текст заметки");

        var result = crypto.Encrypt("пароль123", plaintext, syncId, NoteProtectedObjectType.NoteEnvelope);

        Assert.NotEmpty(result.Ciphertext);
        Assert.Equal(NoteCryptoService.SaltByteSize, result.Salt.Length);
        Assert.Equal(NoteCryptoService.NonceByteSize, result.Nonce.Length);
        Assert.Equal(NoteCryptoService.TagByteSize, result.Tag.Length);
        Assert.Equal(1, result.FormatVersion);

        byte[] decrypted = crypto.Decrypt(
            "пароль123",
            result.Ciphertext,
            result.Salt,
            result.Nonce,
            result.Tag,
            syncId,
            NoteProtectedObjectType.NoteEnvelope,
            result.KdfIterations);

        Assert.Equal(plaintext, decrypted);
    }

    [Fact]
    public void CryptoService_WrongPassword_ThrowsSecurityException()
    {
        var crypto = new NoteCryptoService();
        var syncId = Guid.NewGuid();
        var result = crypto.Encrypt("правильный", new byte[] { 1, 2, 3 }, syncId, NoteProtectedObjectType.NoteEnvelope);

        Assert.Throws<NoteProtectionSecurityException>(() =>
            crypto.Decrypt(
                "неправильный",
                result.Ciphertext,
                result.Salt,
                result.Nonce,
                result.Tag,
                syncId,
                NoteProtectedObjectType.NoteEnvelope,
                result.KdfIterations));
    }

    [Fact]
    public void CryptoService_TamperedCiphertext_ThrowsSecurityException()
    {
        var crypto = new NoteCryptoService();
        var syncId = Guid.NewGuid();
        var result = crypto.Encrypt("пароль", new byte[] { 1, 2, 3, 4 }, syncId, NoteProtectedObjectType.NoteEnvelope);

        var tampered = (byte[])result.Ciphertext.Clone();
        tampered[0] ^= 0xFF;

        Assert.Throws<NoteProtectionSecurityException>(() =>
            crypto.Decrypt(
                "пароль",
                tampered,
                result.Salt,
                result.Nonce,
                result.Tag,
                syncId,
                NoteProtectedObjectType.NoteEnvelope,
                result.KdfIterations));
    }

    [Fact]
    public void ProtectNote_EncryptsTextAndRemovesFromFts()
    {
        string dbPath = CreateTempDb();
        try
        {
            using (var init = CreateContext(dbPath))
            {
                DbInitializer.Initialize(init);
            }

            int noteId;
            using (var db = CreateContext(dbPath))
            {
                var note = new Note
                {
                    Text = "Секретное содержимое заметки",
                    SourceProcessName = "chrome.exe",
                    SourceWindowTitle = "Конфиденциальный документ",
                    SourceUrl = "https://example.com/secret"
                };
                db.Notes.Add(note);
                db.SaveChanges();
                noteId = note.Id;

                // FTS should index it
                var ftsCount = db.Database.SqlQueryRaw<int>(
                    "SELECT COUNT(*) FROM NotesFts WHERE NoteId = {0}", noteId).ToList();
                Assert.True(ftsCount.Count > 0);
            }

            var protection = new NoteProtectionService();
            using (var db = CreateContext(dbPath))
            {
                var result = protection.ProtectNote(db, noteId, "мой-пароль");
                Assert.True(result.Success);
            }

            using (var db = CreateContext(dbPath))
            {
                var note = db.Notes.Find(noteId);
                Assert.NotNull(note);
                Assert.True(note!.IsProtected);
                Assert.Equal(string.Empty, note.Text);
                Assert.Null(note.SourceProcessName);
                Assert.Null(note.SourceWindowTitle);
                Assert.Null(note.SourceUrl);
                Assert.False(string.IsNullOrEmpty(note.ProtectedCiphertextBase64));

                // FTS must NOT contain the note
                var ftsCount = db.Database.SqlQueryRaw<int>(
                    "SELECT COUNT(*) FROM NotesFts WHERE NoteId = {0}", noteId).ToList();
                Assert.Equal(0, ftsCount.FirstOrDefault());
            }

            // Unlock with correct password
            using (var db = CreateContext(dbPath))
            {
                var payload = protection.UnlockNote(db, noteId, "мой-пароль");
                Assert.Equal("Секретное содержимое заметки", payload.Text);
                Assert.Equal("chrome.exe", payload.SourceProcessName);
                Assert.Equal("Конфиденциальный документ", payload.SourceWindowTitle);
                Assert.Equal("https://example.com/secret", payload.SourceUrl);
            }

            // Wrong password must not modify data
            using (var db = CreateContext(dbPath))
            {
                Assert.Throws<NoteProtectionSecurityException>(() => protection.UnlockNote(db, noteId, "неверный"));
            }
        }
        finally
        {
            CleanupDb(dbPath);
        }
    }

    [Fact]
    public void ChangePassword_ReencryptsAndOldPasswordFails()
    {
        string dbPath = CreateTempDb();
        try
        {
            using (var init = CreateContext(dbPath))
            {
                DbInitializer.Initialize(init);
            }

            int noteId;
            using (var db = CreateContext(dbPath))
            {
                var note = new Note { Text = "Текст для смены пароля" };
                db.Notes.Add(note);
                db.SaveChanges();
                noteId = note.Id;
            }

            var protection = new NoteProtectionService();
            using (var db = CreateContext(dbPath))
            {
                Assert.True(protection.ProtectNote(db, noteId, "старый").Success);
            }

            using (var db = CreateContext(dbPath))
            {
                var result = protection.ChangePassword(db, noteId, "старый", "новый");
                Assert.True(result.Success);
            }

            using (var db = CreateContext(dbPath))
            {
                // Old password must fail
                Assert.Throws<NoteProtectionSecurityException>(() => protection.UnlockNote(db, noteId, "старый"));
                // New password works
                var payload = protection.UnlockNote(db, noteId, "новый");
                Assert.Equal("Текст для смены пароля", payload.Text);
            }
        }
        finally
        {
            CleanupDb(dbPath);
        }
    }

    [Fact]
    public void RemoveProtection_RestoresPlaintextAndReindexesFts()
    {
        string dbPath = CreateTempDb();
        try
        {
            using (var init = CreateContext(dbPath))
            {
                DbInitializer.Initialize(init);
            }

            int noteId;
            using (var db = CreateContext(dbPath))
            {
                var note = new Note { Text = "Восстановленный текст" };
                db.Notes.Add(note);
                db.SaveChanges();
                noteId = note.Id;
            }

            var protection = new NoteProtectionService();
            using (var db = CreateContext(dbPath))
            {
                Assert.True(protection.ProtectNote(db, noteId, "пароль").Success);
            }

            using (var db = CreateContext(dbPath))
            {
                var result = protection.RemoveProtection(db, noteId, "пароль");
                Assert.True(result.Success);
            }

            using (var db = CreateContext(dbPath))
            {
                var note = db.Notes.Find(noteId);
                Assert.NotNull(note);
                Assert.False(note!.IsProtected);
                Assert.Equal("Восстановленный текст", note.Text);

                // FTS re-indexed
                var ftsCount = db.Database.SqlQueryRaw<int>(
                    "SELECT COUNT(*) FROM NotesFts WHERE NoteId = {0}", noteId).ToList();
                Assert.True(ftsCount.FirstOrDefault() > 0);
            }
        }
        finally
        {
            CleanupDb(dbPath);
        }
    }

    [Fact]
    public void ProtectNote_ConvertsRevisionsToEncrypted()
    {
        string dbPath = CreateTempDb();
        try
        {
            using (var init = CreateContext(dbPath))
            {
                DbInitializer.Initialize(init);
            }

            int noteId;
            using (var db = CreateContext(dbPath))
            {
                var note = new Note { Text = "Версия 1" };
                db.Notes.Add(note);
                db.SaveChanges();
                noteId = note.Id;

                var history = new NoteHistoryService();
                history.SaveSnapshot(db, note);
                note.Text = "Версия 2";
                db.SaveChanges();
                history.SaveSnapshot(db, note);
            }

            var protection = new NoteProtectionService();
            using (var db = CreateContext(dbPath))
            {
                Assert.True(protection.ProtectNote(db, noteId, "пароль").Success);
            }

            using (var db = CreateContext(dbPath))
            {
                var revisions = db.NoteRevisions.Where(r => r.NoteId == noteId).ToList();
                Assert.NotEmpty(revisions);
                foreach (var rev in revisions)
                {
                    Assert.True(rev.IsProtected);
                    Assert.Equal(string.Empty, rev.Text);
                    Assert.False(string.IsNullOrEmpty(rev.ProtectedCiphertextBase64));
                }
            }

            // Unlock and decrypt revisions
            using (var db = CreateContext(dbPath))
            {
                protection.UnlockNote(db, noteId, "пароль");
                var revisions = db.NoteRevisions.Where(r => r.NoteId == noteId).ToList();
                foreach (var rev in revisions)
                {
                    string? text = protection.DecryptRevisionText(db, noteId, rev);
                    Assert.False(string.IsNullOrEmpty(text));
                }
            }
        }
        finally
        {
            CleanupDb(dbPath);
        }
    }

    [Fact]
    public void SearchService_ExcludesProtectedNotesFromFts()
    {
        string dbPath = CreateTempDb();
        try
        {
            using (var init = CreateContext(dbPath))
            {
                DbInitializer.Initialize(init);
            }

            int protectedId;
            int normalId;
            using (var db = CreateContext(dbPath))
            {
                var normal = new Note { Text = "Обычная заметка про котиков" };
                db.Notes.Add(normal);
                db.SaveChanges();
                normalId = normal.Id;

                var protectedNote = new Note { Text = "Секретная заметка про котиков" };
                db.Notes.Add(protectedNote);
                db.SaveChanges();
                protectedId = protectedNote.Id;
            }

            var protection = new NoteProtectionService();
            using (var db = CreateContext(dbPath))
            {
                Assert.True(protection.ProtectNote(db, protectedId, "пароль").Success);
            }

            using (var db = CreateContext(dbPath))
            {
                var search = new SearchService();
                var ids = search.SearchNoteIds(db, "котиков");
                Assert.Contains(normalId, ids);
                Assert.DoesNotContain(protectedId, ids);
            }
        }
        finally
        {
            CleanupDb(dbPath);
        }
    }

    [Fact]
    public void InMemorySearch_FindsUnlockedProtectedNotes()
    {
        string dbPath = CreateTempDb();
        try
        {
            using (var init = CreateContext(dbPath))
            {
                DbInitializer.Initialize(init);
            }

            int noteId;
            using (var db = CreateContext(dbPath))
            {
                var note = new Note { Text = "Уникальный секретный пароль для теста" };
                db.Notes.Add(note);
                db.SaveChanges();
                noteId = note.Id;
            }

            var protection = new NoteProtectionService();
            using (var db = CreateContext(dbPath))
            {
                Assert.True(protection.ProtectNote(db, noteId, "пароль").Success);
            }

            // ProtectNote leaves the note unlocked (per spec it may stay unlocked in process).
            // Explicitly lock it to test the locked behavior.
            protection.LockNote(noteId);

            // Locked: no in-memory hits
            Assert.Empty(protection.SearchUnlockedInMemory("секретный"));

            // Unlock: in-memory search finds it
            using (var db = CreateContext(dbPath))
            {
                protection.UnlockNote(db, noteId, "пароль");
            }
            var hits = protection.SearchUnlockedInMemory("секретный");
            Assert.Contains(hits, h => h.NoteId == noteId);

            // Lock again: no hits
            protection.LockNote(noteId);
            Assert.Empty(protection.SearchUnlockedInMemory("секретный"));
        }
        finally
        {
            CleanupDb(dbPath);
        }
    }

    [Fact]
    public void NoteCardViewModel_ShowsNeutralTitleForProtectedNote()
    {
        var note = new Note
        {
            Id = 42,
            Text = "Секретный заголовок",
            IsProtected = true,
            SourceProcessName = "chrome.exe",
            SourceUrl = "https://secret.example.com"
        };

        var card = new QuickNotes.App.ViewModels.NoteCardViewModel(note);
        Assert.Equal("🔒 Защищённая заметка", card.DisplayTitle);
        Assert.Equal(string.Empty, card.Text);
        Assert.Equal("Содержимое защищено паролем.", card.PreviewText);
        Assert.False(card.HasSourceText);
        Assert.Empty(card.Tags);
    }

    [Fact]
    public void DbInitializer_V10Migration_AddsProtectionColumns()
    {
        string dbPath = CreateTempDb();
        try
        {
            // Create a v9-style database without protection columns
            using (var connection = new SqliteConnection($"Data Source={dbPath}"))
            {
                connection.Open();
                using var cmd = connection.CreateCommand();
                cmd.CommandText = @"
                    CREATE TABLE Notes (
                        Id INTEGER PRIMARY KEY AUTOINCREMENT,
                        SyncId TEXT NULL,
                        Text TEXT NOT NULL,
                        CreatedAt TEXT NOT NULL,
                        UpdatedAt TEXT NOT NULL,
                        IsPinned INTEGER NOT NULL DEFAULT 0,
                        IsFavorite INTEGER NOT NULL DEFAULT 0,
                        IsInbox INTEGER NOT NULL DEFAULT 0,
                        DeletedAt TEXT NULL,
                        SourceProcessName TEXT NULL,
                        SourceWindowTitle TEXT NULL,
                        SourceUrl TEXT NULL,
                        CapturedAt TEXT NULL
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
                    INSERT INTO Notes (Id, SyncId, Text, CreatedAt, UpdatedAt)
                    VALUES (1, '11111111-1111-1111-1111-111111111111', 'Existing note', '2026-01-01', '2026-01-01');
                    PRAGMA user_version = 9;
                ";
                cmd.ExecuteNonQuery();
            }

            using (var context = CreateContext(dbPath))
            {
                DbInitializer.Initialize(context);
            }

            using (var verify = new SqliteConnection($"Data Source={dbPath}"))
            {
                verify.Open();
                long version = 0;
                using (var vCmd = verify.CreateCommand())
                {
                    vCmd.CommandText = "PRAGMA user_version;";
                    version = Convert.ToInt64(vCmd.ExecuteScalar());
                }
                Assert.Equal(DbInitializer.CurrentSchemaVersion, version);

                // Verify protection columns exist
                using (var colCmd = verify.CreateCommand())
                {
                    colCmd.CommandText = "PRAGMA table_info(Notes);";
                    using var reader = colCmd.ExecuteReader();
                    var cols = new System.Collections.Generic.List<string>();
                    while (reader.Read()) cols.Add(reader.GetString(1));
                    Assert.Contains("IsProtected", cols);
                    Assert.Contains("ProtectedCiphertextBase64", cols);
                }

                // Existing note remains readable
                using (var selCmd = verify.CreateCommand())
                {
                    selCmd.CommandText = "SELECT Text FROM Notes WHERE Id = 1;";
                    Assert.Equal("Existing note", selCmd.ExecuteScalar());
                }
            }

            // Verify reading through EF Core (ensures nullable columns don't crash EF entity materialization)
            using (var efContext = CreateContext(dbPath))
            {
                var migratedNote = efContext.Notes.FirstOrDefault(n => n.Id == 1);
                Assert.NotNull(migratedNote);
                Assert.Equal("Existing note", migratedNote.Text);
                Assert.False(migratedNote.IsProtected);
                Assert.True(string.IsNullOrEmpty(migratedNote.ProtectedSaltBase64));
                Assert.True(string.IsNullOrEmpty(migratedNote.ProtectedCiphertextBase64));
            }
        }
        finally
        {
            CleanupDb(dbPath);
        }
    }

    [Fact]
    public void MigrationV10ToV11_SafelyUpgradesOldFormatV10_BacksUpUserData_AndIsIdempotent()
    {
        string dbPath = CreateTempDb();
        try
        {
            // 1. Build an existing v10 database: user_version = 10, all v10 protection columns, but WITHOUT ProtectedOriginalSyncId
            using (var conn = new SqliteConnection($"Data Source={dbPath}"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    CREATE TABLE Notes (
                        Id INTEGER PRIMARY KEY AUTOINCREMENT,
                        SyncId TEXT NOT NULL,
                        Text TEXT NOT NULL,
                        CreatedAt TEXT NOT NULL,
                        UpdatedAt TEXT NOT NULL,
                        DeletedAt TEXT NULL,
                        CapturedAt TEXT NULL,
                        SourceProcessName TEXT NULL,
                        SourceWindowTitle TEXT NULL,
                        SourceUrl TEXT NULL,
                        IsPinned INTEGER NOT NULL DEFAULT 0,
                        IsFavorite INTEGER NOT NULL DEFAULT 0,
                        IsInbox INTEGER NOT NULL DEFAULT 0,
                        IsProtected INTEGER NOT NULL DEFAULT 0,
                        ProtectedFormatVersion INTEGER NOT NULL DEFAULT 0,
                        ProtectedKdfIterations INTEGER NOT NULL DEFAULT 0,
                        ProtectedSaltBase64 TEXT NULL DEFAULT '',
                        ProtectedNonceBase64 TEXT NULL DEFAULT '',
                        ProtectedTagBase64 TEXT NULL DEFAULT '',
                        ProtectedCiphertextBase64 TEXT NULL DEFAULT ''
                    );
                    CREATE TABLE NoteRevisions (
                        Id INTEGER PRIMARY KEY AUTOINCREMENT,
                        NoteId INTEGER NOT NULL,
                        CreatedAt TEXT NOT NULL,
                        TagsJson TEXT NOT NULL,
                        Text TEXT NOT NULL,
                        IsProtected INTEGER NOT NULL DEFAULT 0,
                        ProtectedFormatVersion INTEGER NOT NULL DEFAULT 0,
                        ProtectedKdfIterations INTEGER NOT NULL DEFAULT 0,
                        ProtectedSaltBase64 TEXT NULL DEFAULT '',
                        ProtectedNonceBase64 TEXT NULL DEFAULT '',
                        ProtectedTagBase64 TEXT NULL DEFAULT '',
                        ProtectedCiphertextBase64 TEXT NULL DEFAULT ''
                    );
                    CREATE TABLE NoteAttachments (
                        Id INTEGER PRIMARY KEY AUTOINCREMENT,
                        NoteId INTEGER NOT NULL,
                        SyncId TEXT NOT NULL,
                        OriginalFileName TEXT NOT NULL,
                        StoredFileName TEXT NOT NULL,
                        RelativePath TEXT NOT NULL,
                        ContentType TEXT NOT NULL,
                        Size INTEGER NOT NULL,
                        Sha256 TEXT NOT NULL,
                        CreatedAt TEXT NOT NULL,
                        IsProtected INTEGER NOT NULL DEFAULT 0,
                        ProtectedFormatVersion INTEGER NOT NULL DEFAULT 0,
                        ProtectedKdfIterations INTEGER NOT NULL DEFAULT 0,
                        ProtectedSaltBase64 TEXT NULL DEFAULT '',
                        ProtectedNonceBase64 TEXT NULL DEFAULT '',
                        ProtectedTagBase64 TEXT NULL DEFAULT '',
                        ProtectedCiphertextBase64 TEXT NULL DEFAULT ''
                    );

                    INSERT INTO Notes (Id, SyncId, Text, CreatedAt, UpdatedAt, IsProtected, ProtectedFormatVersion, ProtectedKdfIterations, ProtectedSaltBase64, ProtectedNonceBase64, ProtectedTagBase64, ProtectedCiphertextBase64)
                    VALUES 
                    (1, '11111111-1111-1111-1111-111111111111', 'Unprotected note in v10', '2026-01-01', '2026-01-01', 0, 0, 0, '', '', '', ''),
                    (2, '22222222-2222-2222-2222-222222222222', '', '2026-01-01', '2026-01-01', 1, 1, 100000, 'c2FsdA==', 'bm9uY2U=', 'dGFn', 'Y2lwaGVydGV4dA==');

                    PRAGMA user_version = 10;
                ";
                cmd.ExecuteNonQuery();
            }

            // Verify that ProtectedOriginalSyncId is missing before upgrade
            using (var checkConn = new SqliteConnection($"Data Source={dbPath}"))
            {
                checkConn.Open();
                Assert.False(DbInitializer.ColumnExists(checkConn, "Notes", "ProtectedOriginalSyncId"));
            }

            // 2. Run initialization with backup spy
            int backupCount = 0;
            using (var context = CreateContext(dbPath))
            {
                DbInitializer.Initialize(context, () => { backupCount++; });
            }

            // 3. Verify backup was created once before the real upgrade with user data
            Assert.Equal(1, backupCount);

            // 4. Verify DB was upgraded to current schema version and columns were safely added
            using (var verifyConn = new SqliteConnection($"Data Source={dbPath}"))
            {
                verifyConn.Open();
                long version = 0;
                using (var vCmd = verifyConn.CreateCommand())
                {
                    vCmd.CommandText = "PRAGMA user_version;";
                    version = Convert.ToInt64(vCmd.ExecuteScalar());
                }
                Assert.Equal(DbInitializer.CurrentSchemaVersion, version);
                Assert.True(DbInitializer.ColumnExists(verifyConn, "Notes", "ProtectedOriginalSyncId"));
                Assert.True(DbInitializer.ColumnExists(verifyConn, "Notes", "Title"));
                Assert.True(DbInitializer.ColumnExists(verifyConn, "NoteRevisions", "Title"));

                // Verify user data and crypto envelopes are intact and untouched
                using (var selCmd = verifyConn.CreateCommand())
                {
                    selCmd.CommandText = "SELECT Text FROM Notes WHERE Id = 1;";
                    Assert.Equal("Unprotected note in v10", selCmd.ExecuteScalar());

                    selCmd.CommandText = "SELECT Title FROM Notes WHERE Id = 1;";
                    Assert.Equal("Unprotected note in v10", selCmd.ExecuteScalar());

                    selCmd.CommandText = "SELECT Title FROM Notes WHERE Id = 2;";
                    Assert.Equal(string.Empty, selCmd.ExecuteScalar());

                    selCmd.CommandText = "SELECT ProtectedCiphertextBase64 FROM Notes WHERE Id = 2;";
                    Assert.Equal("Y2lwaGVydGV4dA==", selCmd.ExecuteScalar());

                    selCmd.CommandText = "SELECT ProtectedOriginalSyncId FROM Notes WHERE Id = 2;";
                    var origSyncId = selCmd.ExecuteScalar();
                    Assert.True(origSyncId == null || origSyncId == DBNull.Value);
                }
            }

            // 5. Test repeat run of migration: must be idempotent, version remains current, NO repeat backup!
            using (var context2 = CreateContext(dbPath))
            {
                DbInitializer.Initialize(context2, () => { backupCount++; });
            }

            Assert.Equal(1, backupCount); // Still 1! No second backup created!

            using (var verifyConn2 = new SqliteConnection($"Data Source={dbPath}"))
            {
                verifyConn2.Open();
                long version = 0;
                using (var vCmd = verifyConn2.CreateCommand())
                {
                    vCmd.CommandText = "PRAGMA user_version;";
                    version = Convert.ToInt64(vCmd.ExecuteScalar());
                }
                Assert.Equal(DbInitializer.CurrentSchemaVersion, version);
            }
        }
        finally
        {
            CleanupDb(dbPath);
        }
    }
}
