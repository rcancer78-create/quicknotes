using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data.Common;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using QuickNotes.App.Data;
using QuickNotes.App.Models;
using QuickNotes.App.Models.Sync;
using QuickNotes.App.Services;
using QuickNotes.App.Services.NoteProtection;
using QuickNotes.App.Services.Sync;
using QuickNotes.App.ViewModels;
using Xunit;

namespace QuickNotes.Tests;

/// <summary>
/// Reproducing tests for the independent-acceptance defects of note protection:
/// password rotation corrupting attachments, non-atomic file/DB operations,
/// missing sync transport of protected attachments, lost metadata on save,
/// sync fingerprint blindness and incomplete semantic-index integration.
/// </summary>
[TestCategory(TestCategories.Integration)]
public class NoteProtectionRegressionTests : IDisposable
{
    private readonly List<string> _tempPaths = new();

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var p in _tempPaths)
        {
            if (File.Exists(p)) { try { File.Delete(p); } catch { } }
            if (Directory.Exists(p)) { try { Directory.Delete(p, recursive: true); } catch { } }
        }
    }

    private string MakeTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "qn_reg_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _tempPaths.Add(dir);
        return dir;
    }

    private string CreateTempDb()
    {
        string path = Path.Combine(Path.GetTempPath(), $"quicknotes_reg_{Guid.NewGuid():N}.db");
        _tempPaths.Add(path);
        return path;
    }

    private static QuickNotesDbContext CreateContext(string dbPath, params IInterceptor[] interceptors)
    {
        var builder = new DbContextOptionsBuilder<QuickNotesDbContext>()
            .UseSqlite($"Data Source={dbPath}");
        if (interceptors != null && interceptors.Length > 0)
        {
            builder.AddInterceptors(interceptors);
        }
        return new QuickNotesDbContext(builder.Options);
    }

    private static void InitDb(string dbPath)
    {
        using var db = CreateContext(dbPath);
        DbInitializer.Initialize(db);
    }

    /// <summary>Adds a real attachment (managed plaintext file) to a note.</summary>
    private static NoteAttachment AddAttachment(QuickNotesDbContext db, IAttachmentStorageService storage, int noteId, byte[] content, string originalName)
    {
        var saved = storage.SaveFromBytes(content, originalName, 50 * 1024 * 1024);
        var att = new NoteAttachment
        {
            NoteId = noteId,
            OriginalFileName = originalName,
            StoredFileName = saved.StoredFileName,
            RelativePath = saved.RelativePath,
            ContentType = saved.ContentType,
            Size = saved.Size,
            Sha256 = saved.Sha256,
            CreatedAt = DateTime.Now
        };
        db.NoteAttachments.Add(att);
        db.SaveChanges();
        return att;
    }

    private sealed class FixedDeviceId : IDeviceIdProvider
    {
        private readonly Guid _id;
        public FixedDeviceId(Guid id) { _id = id; }
        public Guid GetDeviceId() => _id;
    }

    // ------------------------------------------------------------------
    // Defect 1: ChangePassword corrupts protected attachments
    // ------------------------------------------------------------------

    [Fact]
    public void ChangePassword_WithAttachment_PreservesContentByteForByte()
    {
        string dbPath = CreateTempDb();
        var storage = new AttachmentStorageService(MakeTempDir());
        InitDb(dbPath);

        int noteId;
        byte[] originalContent = Enumerable.Range(0, 4096).Select(i => (byte)(i % 251)).ToArray();
        int attId;
        using (var db = CreateContext(dbPath))
        {
            var note = new Note { Text = "Заметка с вложением для ротации пароля" };
            db.Notes.Add(note);
            db.SaveChanges();
            noteId = note.Id;
            var att = AddAttachment(db, storage, noteId, originalContent, "документ-секрет.bin");
            attId = att.Id;
        }

        var protection = new NoteProtectionService(attachmentStorage: storage);
        using (var db = CreateContext(dbPath))
        {
            Assert.True(protection.ProtectNote(db, noteId, "старый-пароль").Success);
        }
        protection.LockNote(noteId);

        using (var db = CreateContext(dbPath))
        {
            var result = protection.ChangePassword(db, noteId, "старый-пароль", "новый-пароль");
            Assert.True(result.Success, result.ErrorMessage);
        }

        // New password must decrypt the attachment byte-for-byte.
        using (var db = CreateContext(dbPath))
        {
            protection.UnlockNote(db, noteId, "новый-пароль");
            var att = db.NoteAttachments.Find(attId);
            Assert.NotNull(att);
            string tempFile = protection.DecryptAttachmentToTempFile(db, noteId, att!);
            try
            {
                byte[] roundTripped = File.ReadAllBytes(tempFile);
                Assert.Equal(originalContent, roundTripped);
            }
            finally
            {
                ProtectedAttachmentFile.TryDeleteTempFile(tempFile);
            }

            string name = protection.DecryptAttachmentName(db, noteId, att!);
            Assert.Equal("документ-секрет.bin", name);
        }
    }

    [Fact]
    public void ChangePassword_OldPasswordCannotDecryptNoteHistoryOrAttachment()
    {
        string dbPath = CreateTempDb();
        var storage = new AttachmentStorageService(MakeTempDir());
        InitDb(dbPath);

        int noteId;
        byte[] originalContent = System.Text.Encoding.UTF8.GetBytes("сверхсекретные-байты-вложения");
        using (var db = CreateContext(dbPath))
        {
            var note = new Note { Text = "Версия 1" };
            db.Notes.Add(note);
            db.SaveChanges();
            noteId = note.Id;

            var history = new NoteHistoryService();
            history.SaveSnapshot(db, note);
            note.Text = "Версия 2 секретная";
            db.SaveChanges();
            history.SaveSnapshot(db, note);

            AddAttachment(db, storage, noteId, originalContent, "таеное.txt");
        }

        var protection = new NoteProtectionService(attachmentStorage: storage);
        using (var db = CreateContext(dbPath))
        {
            Assert.True(protection.ProtectNote(db, noteId, "pw-old").Success);
        }
        protection.LockNote(noteId);
        using (var db = CreateContext(dbPath))
        {
            Assert.True(protection.ChangePassword(db, noteId, "pw-old", "pw-new").Success);
        }

        // Old password must not unlock the note anymore.
        using (var db = CreateContext(dbPath))
        {
            Assert.Throws<NoteProtectionSecurityException>(() => protection.UnlockNote(db, noteId, "pw-old"));
        }

        // New password opens note, history and attachment with the original content.
        using (var db = CreateContext(dbPath))
        {
            var payload = protection.UnlockNote(db, noteId, "pw-new");
            Assert.Equal("Версия 2 секретная", payload.Text);

            var revisions = db.NoteRevisions.Where(r => r.NoteId == noteId).OrderBy(r => r.Id).ToList();
            Assert.NotEmpty(revisions);
            foreach (var rev in revisions)
            {
                string? text = protection.DecryptRevisionText(db, noteId, rev);
                Assert.False(string.IsNullOrEmpty(text));
            }

            var att = db.NoteAttachments.First(a => a.NoteId == noteId);
            string tempFile = protection.DecryptAttachmentToTempFile(db, noteId, att);
            try
            {
                Assert.Equal(originalContent, File.ReadAllBytes(tempFile));
            }
            finally
            {
                ProtectedAttachmentFile.TryDeleteTempFile(tempFile);
            }
        }
    }


    // ------------------------------------------------------------------
    // Defect 5: sync fingerprint must track the protected envelope
    // ------------------------------------------------------------------

    [Fact]
    public void SyncRevision_PasswordChange_CreatesNewNoteRevision()
    {
        string dbPath = CreateTempDb();
        var storage = new AttachmentStorageService(MakeTempDir());
        InitDb(dbPath);

        int noteId;
        Guid syncId;
        using (var db = CreateContext(dbPath))
        {
            var note = new Note { Text = "Конфиденциально" };
            db.Notes.Add(note);
            db.SaveChanges();
            noteId = note.Id;
            syncId = note.SyncId;
        }

        var protection = new NoteProtectionService(attachmentStorage: storage);
        using (var db = CreateContext(dbPath))
        {
            Assert.True(protection.ProtectNote(db, noteId, "pw-1").Success);
        }

        var crypto = new SyncCryptoService(iterations: 5_000);
        var deviceId = Guid.NewGuid();
        var exporter = new SyncPackageExporter(crypto, new FixedDeviceId(deviceId));

        Guid revAfterProtect;
        using (var db = CreateContext(dbPath))
        {
            var payload = exporter.BuildDeterministicPayload(db, deviceId);
            var dto = payload.Notes.Single(n => n.SyncId == syncId);
            Assert.True(dto.IsProtected);
            revAfterProtect = dto.RevisionId;
        }

        using (var db = CreateContext(dbPath))
        {
            Assert.True(protection.ChangePassword(db, noteId, "pw-1", "pw-2").Success);
        }

        using (var db = CreateContext(dbPath))
        {
            var payload = exporter.BuildDeterministicPayload(db, deviceId);
            var dto = payload.Notes.Single(n => n.SyncId == syncId);
            Assert.NotEqual(revAfterProtect, dto.RevisionId);
        }
    }

    [Fact]
    public void SyncRevision_UnchangedProtectedNote_ExportIsNoOp()
    {
        string dbPath = CreateTempDb();
        var storage = new AttachmentStorageService(MakeTempDir());
        InitDb(dbPath);

        int noteId;
        Guid syncId;
        using (var db = CreateContext(dbPath))
        {
            var note = new Note { Text = "Стабильный секрет" };
            db.Notes.Add(note);
            db.SaveChanges();
            noteId = note.Id;
            syncId = note.SyncId;
        }

        var protection = new NoteProtectionService(attachmentStorage: storage);
        using (var db = CreateContext(dbPath))
        {
            Assert.True(protection.ProtectNote(db, noteId, "pw-stable").Success);
        }

        var crypto = new SyncCryptoService(iterations: 5_000);
        var deviceId = Guid.NewGuid();
        var exporter = new SyncPackageExporter(crypto, new FixedDeviceId(deviceId));

        Guid rev1;
        using (var db = CreateContext(dbPath))
        {
            rev1 = exporter.BuildDeterministicPayload(db, deviceId).Notes.Single(n => n.SyncId == syncId).RevisionId;
        }

        // Simulate an editor save with UNCHANGED content (envelope must not be re-encrypted).
        using (var db = CreateContext(dbPath))
        {
            protection.UnlockNote(db, noteId, "pw-stable");
            var note = db.Notes.Find(noteId)!;
            protection.ApplyUnlockedEdits(db, note, "Стабильный секрет", null, null, null);
            db.SaveChanges();
        }

        Guid rev2;
        using (var db = CreateContext(dbPath))
        {
            rev2 = exporter.BuildDeterministicPayload(db, deviceId).Notes.Single(n => n.SyncId == syncId).RevisionId;
        }

        Assert.Equal(rev1, rev2);
    }


    [Fact]
    public void SyncRevision_ProtectedTextEdit_CreatesNewRevision()
    {
        string dbPath = CreateTempDb();
        var storage = new AttachmentStorageService(MakeTempDir());
        InitDb(dbPath);

        int noteId;
        Guid syncId;
        using (var db = CreateContext(dbPath))
        {
            var note = new Note { Text = "Текст до правки" };
            db.Notes.Add(note);
            db.SaveChanges();
            noteId = note.Id;
            syncId = note.SyncId;
        }

        var protection = new NoteProtectionService(attachmentStorage: storage);
        using (var db = CreateContext(dbPath))
        {
            Assert.True(protection.ProtectNote(db, noteId, "pw-edit").Success);
        }

        var crypto = new SyncCryptoService(iterations: 5_000);
        var deviceId = Guid.NewGuid();
        var exporter = new SyncPackageExporter(crypto, new FixedDeviceId(deviceId));

        Guid rev1;
        using (var db = CreateContext(dbPath))
        {
            rev1 = exporter.BuildDeterministicPayload(db, deviceId).Notes.Single(n => n.SyncId == syncId).RevisionId;
        }

        using (var db = CreateContext(dbPath))
        {
            protection.UnlockNote(db, noteId, "pw-edit");
            var note = db.Notes.Find(noteId)!;
            protection.ApplyUnlockedEdits(db, note, "Текст ПОСЛЕ правки", null, null, null);
            db.SaveChanges();
        }

        Guid rev2;
        using (var db = CreateContext(dbPath))
        {
            rev2 = exporter.BuildDeterministicPayload(db, deviceId).Notes.Single(n => n.SyncId == syncId).RevisionId;
        }

        Assert.NotEqual(rev1, rev2);
    }

    // ------------------------------------------------------------------
    // Defect 6: session store must wipe the replaced key
    // ------------------------------------------------------------------

    [Fact]
    public void SessionStore_Add_WipesReplacedSessionKey()
    {
        var store = new ProtectedNoteSessionStore();
        byte[] oldKey = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
        byte[] oldKeyCopy = (byte[])oldKey.Clone();
        var oldSession = new ProtectedNoteSession(7, Guid.NewGuid(), oldKey, new NoteProtectedPayload { Text = "old" });
        store.Add(oldSession);

        byte[] newKey = Enumerable.Range(101, 32).Select(i => (byte)i).ToArray();
        store.Add(new ProtectedNoteSession(7, oldSession.SyncId, newKey, new NoteProtectedPayload { Text = "new" }));

        // The replaced session key material must be wiped BEFORE the reference is dropped.
        Assert.Equal(new byte[32], oldKey);
        Assert.NotEqual(oldKeyCopy, oldKey);

        var current = store.Get(7);
        Assert.NotNull(current);
        Assert.Equal("new", current!.Payload.Text);
        Assert.Equal(newKey, current.Key);
    }

    // ------------------------------------------------------------------
    // Multi-attachment protection, tamper detection, and restoration
    // ------------------------------------------------------------------

    [Fact]
    public void ProtectNote_WithMultipleAttachments_EncryptsAll_AndRemovesPlaintextFromDbAndDisk()
    {
        string dbPath = CreateTempDb();
        var storage = new AttachmentStorageService(MakeTempDir());
        InitDb(dbPath);

        int noteId;
        byte[] content1 = Encoding.UTF8.GetBytes("Содержимое первого вложения");
        byte[] content2 = Encoding.UTF8.GetBytes("Содержимое второго секретного документа 12345");
        int attId1, attId2;

        using (var db = CreateContext(dbPath))
        {
            var note = new Note { Text = "Заметка с двумя секретными файлами" };
            db.Notes.Add(note);
            db.SaveChanges();
            noteId = note.Id;

            attId1 = AddAttachment(db, storage, noteId, content1, "первый.txt").Id;
            attId2 = AddAttachment(db, storage, noteId, content2, "второй.bin").Id;
        }

        var protection = new NoteProtectionService(attachmentStorage: storage);
        using (var db = CreateContext(dbPath))
        {
            var result = protection.ProtectNote(db, noteId, "strong-password");
            Assert.True(result.Success, result.ErrorMessage);
        }

        // Verify SQLite database does not contain plaintext note or original filenames
        using (var db = CreateContext(dbPath))
        {
            var note = db.Notes.Find(noteId)!;
            Assert.True(note.IsProtected);
            Assert.Equal(string.Empty, note.Text);

            var att1 = db.NoteAttachments.Find(attId1)!;
            var att2 = db.NoteAttachments.Find(attId2)!;

            Assert.True(att1.IsProtected);
            Assert.True(att2.IsProtected);
            Assert.NotEqual("первый.txt", att1.OriginalFileName);
            Assert.NotEqual("второй.bin", att2.OriginalFileName);
            Assert.Equal("attachment.qnat", att1.OriginalFileName);
            Assert.Equal("attachment.qnat", att2.OriginalFileName);

            // Files on disk must be .qnat containers and not contain plaintext content
            string path1 = storage.GetFullPath(att1.RelativePath);
            string path2 = storage.GetFullPath(att2.RelativePath);
            Assert.True(File.Exists(path1));
            Assert.True(File.Exists(path2));

            byte[] disk1 = File.ReadAllBytes(path1);
            byte[] disk2 = File.ReadAllBytes(path2);

            Assert.False(disk1.SequenceEqual(content1));
            Assert.False(disk2.SequenceEqual(content2));
            Assert.DoesNotContain("первый.txt", Encoding.UTF8.GetString(disk1));
            Assert.DoesNotContain("второй.bin", Encoding.UTF8.GetString(disk2));
        }

        // Unlock note and verify original names and contents byte-for-byte
        using (var db = CreateContext(dbPath))
        {
            var payload = protection.UnlockNote(db, noteId, "strong-password");
            Assert.Equal("Заметка с двумя секретными файлами", payload.Text);

            var att1 = db.NoteAttachments.Find(attId1)!;
            var att2 = db.NoteAttachments.Find(attId2)!;

            Assert.Equal("первый.txt", protection.DecryptAttachmentName(db, noteId, att1));
            Assert.Equal("второй.bin", protection.DecryptAttachmentName(db, noteId, att2));

            string temp1 = protection.DecryptAttachmentToTempFile(db, noteId, att1);
            string temp2 = protection.DecryptAttachmentToTempFile(db, noteId, att2);
            try
            {
                Assert.Equal(content1, File.ReadAllBytes(temp1));
                Assert.Equal(content2, File.ReadAllBytes(temp2));
            }
            finally
            {
                ProtectedAttachmentFile.TryDeleteTempFile(temp1);
                ProtectedAttachmentFile.TryDeleteTempFile(temp2);
            }
        }
    }

    [Fact]
    public void ChangePassword_WithMultipleAttachments_ReEncryptsAll_ByteForByteNewPassword_OldPasswordFails()
    {
        string dbPath = CreateTempDb();
        var storage = new AttachmentStorageService(MakeTempDir());
        InitDb(dbPath);

        int noteId;
        byte[] content1 = Encoding.UTF8.GetBytes("Файл 1");
        byte[] content2 = Encoding.UTF8.GetBytes("Файл 2 данные");
        int attId1, attId2;

        using (var db = CreateContext(dbPath))
        {
            var note = new Note { Text = "Ротация двух файлов" };
            db.Notes.Add(note);
            db.SaveChanges();
            noteId = note.Id;

            attId1 = AddAttachment(db, storage, noteId, content1, "f1.txt").Id;
            attId2 = AddAttachment(db, storage, noteId, content2, "f2.bin").Id;
        }

        var protection = new NoteProtectionService(attachmentStorage: storage);
        using (var db = CreateContext(dbPath))
        {
            Assert.True(protection.ProtectNote(db, noteId, "pw1").Success);
        }

        protection.LockNote(noteId);

        using (var db = CreateContext(dbPath))
        {
            var result = protection.ChangePassword(db, noteId, "pw1", "pw2");
            Assert.True(result.Success, result.ErrorMessage);
        }

        // Old password fails
        using (var db = CreateContext(dbPath))
        {
            Assert.Throws<NoteProtectionSecurityException>(() => protection.UnlockNote(db, noteId, "pw1"));
        }

        // New password works byte-for-byte
        using (var db = CreateContext(dbPath))
        {
            var payload = protection.UnlockNote(db, noteId, "pw2");
            Assert.Equal("Ротация двух файлов", payload.Text);

            var att1 = db.NoteAttachments.Find(attId1)!;
            var att2 = db.NoteAttachments.Find(attId2)!;

            Assert.Equal("f1.txt", protection.DecryptAttachmentName(db, noteId, att1));
            Assert.Equal("f2.bin", protection.DecryptAttachmentName(db, noteId, att2));

            string temp1 = protection.DecryptAttachmentToTempFile(db, noteId, att1);
            string temp2 = protection.DecryptAttachmentToTempFile(db, noteId, att2);
            try
            {
                Assert.Equal(content1, File.ReadAllBytes(temp1));
                Assert.Equal(content2, File.ReadAllBytes(temp2));
            }
            finally
            {
                ProtectedAttachmentFile.TryDeleteTempFile(temp1);
                ProtectedAttachmentFile.TryDeleteTempFile(temp2);
            }
        }
    }

    [Fact]
    public void RemoveProtection_WithMultipleAttachments_RestoresPlaintextFilesAndOriginalNames()
    {
        string dbPath = CreateTempDb();
        var storage = new AttachmentStorageService(MakeTempDir());
        InitDb(dbPath);

        int noteId;
        byte[] content1 = Encoding.UTF8.GetBytes("Восстановление 1");
        byte[] content2 = Encoding.UTF8.GetBytes("Восстановление 2");
        int attId1, attId2;

        using (var db = CreateContext(dbPath))
        {
            var note = new Note { Text = "Снятие защиты" };
            db.Notes.Add(note);
            db.SaveChanges();
            noteId = note.Id;

            attId1 = AddAttachment(db, storage, noteId, content1, "restore1.txt").Id;
            attId2 = AddAttachment(db, storage, noteId, content2, "restore2.bin").Id;
        }

        var protection = new NoteProtectionService(attachmentStorage: storage);
        using (var db = CreateContext(dbPath))
        {
            Assert.True(protection.ProtectNote(db, noteId, "pw-unprotect").Success);
        }

        protection.LockNote(noteId);

        using (var db = CreateContext(dbPath))
        {
            var result = protection.RemoveProtection(db, noteId, "pw-unprotect");
            Assert.True(result.Success, result.ErrorMessage);
        }

        using (var db = CreateContext(dbPath))
        {
            var note = db.Notes.Find(noteId)!;
            Assert.False(note.IsProtected);
            Assert.Equal("Снятие защиты", note.Text);

            var att1 = db.NoteAttachments.Find(attId1)!;
            var att2 = db.NoteAttachments.Find(attId2)!;

            Assert.False(att1.IsProtected);
            Assert.False(att2.IsProtected);
            Assert.Equal("restore1.txt", att1.OriginalFileName);
            Assert.Equal("restore2.bin", att2.OriginalFileName);

            string path1 = storage.GetFullPath(att1.RelativePath);
            string path2 = storage.GetFullPath(att2.RelativePath);

            Assert.Equal(content1, File.ReadAllBytes(path1));
            Assert.Equal(content2, File.ReadAllBytes(path2));
        }
    }

    [Fact]
    public void TamperAttachment_DiskCiphertextOrTag_ThrowsNoteProtectionSecurityException()
    {
        string dbPath = CreateTempDb();
        var storage = new AttachmentStorageService(MakeTempDir());
        InitDb(dbPath);

        int noteId;
        int attId;
        using (var db = CreateContext(dbPath))
        {
            var note = new Note { Text = "Заметка для проверки tamper" };
            db.Notes.Add(note);
            db.SaveChanges();
            noteId = note.Id;

            attId = AddAttachment(db, storage, noteId, Encoding.UTF8.GetBytes("Секрет для tamper"), "tamper.bin").Id;
        }

        var protection = new NoteProtectionService(attachmentStorage: storage);
        using (var db = CreateContext(dbPath))
        {
            Assert.True(protection.ProtectNote(db, noteId, "pw-tamper").Success);
        }

        using (var db = CreateContext(dbPath))
        {
            protection.UnlockNote(db, noteId, "pw-tamper");
            var att = db.NoteAttachments.Find(attId)!;
            string filePath = storage.GetFullPath(att.RelativePath);

            // Tamper with the container file on disk (flip bytes in ciphertext)
            byte[] bytes = File.ReadAllBytes(filePath);
            bytes[^1] ^= 0xFF; // tamper tag or ciphertext
            File.WriteAllBytes(filePath, bytes);

            Assert.Throws<NoteProtectionSecurityException>(() => protection.DecryptAttachmentToTempFile(db, noteId, att));
        }
    }

    [Fact]
    public void TamperAttachment_DbEnvelope_ThrowsNoteProtectionSecurityException()
    {
        string dbPath = CreateTempDb();
        var storage = new AttachmentStorageService(MakeTempDir());
        InitDb(dbPath);

        int noteId;
        int attId;
        using (var db = CreateContext(dbPath))
        {
            var note = new Note { Text = "Заметка для проверки tamper envelope" };
            db.Notes.Add(note);
            db.SaveChanges();
            noteId = note.Id;

            attId = AddAttachment(db, storage, noteId, Encoding.UTF8.GetBytes("Данные"), "file.txt").Id;
        }

        var protection = new NoteProtectionService(attachmentStorage: storage);
        using (var db = CreateContext(dbPath))
        {
            Assert.True(protection.ProtectNote(db, noteId, "pw-env-tamper").Success);
        }

        using (var db = CreateContext(dbPath))
        {
            protection.UnlockNote(db, noteId, "pw-env-tamper");
            var att = db.NoteAttachments.Find(attId)!;

            // Tamper tag in DB
            byte[] tag = Convert.FromBase64String(att.ProtectedTagBase64!);
            tag[0] ^= 0xAA;
            att.ProtectedTagBase64 = Convert.ToBase64String(tag);
            db.SaveChanges();

            Assert.Throws<NoteProtectionSecurityException>(() => protection.DecryptAttachmentName(db, noteId, att));
        }
    }

    // ------------------------------------------------------------------
    // Failure injection and two-phase atomic file rollback tests
    // ------------------------------------------------------------------

    [Fact]
    public void TwoPhase_Protect_FailureOnNewFileCreation_RollsBack_NoDataLoss()
    {
        string dbPath = CreateTempDb();
        var storage = new AttachmentStorageService(MakeTempDir());
        InitDb(dbPath);

        int noteId;
        byte[] originalContent = Encoding.UTF8.GetBytes("Важный исходный документ");
        int attId;
        string originalFilePath;

        using (var db = CreateContext(dbPath))
        {
            var note = new Note { Text = "Заметка до сбоя" };
            db.Notes.Add(note);
            db.SaveChanges();
            noteId = note.Id;

            var att = AddAttachment(db, storage, noteId, originalContent, "исходник.bin");
            attId = att.Id;
            originalFilePath = storage.GetFullPath(att.RelativePath);
        }

        var adapter = new FaultInjectingProtectionFileAdapter();
        adapter.ShouldFailWrite = (path, content) => true; // fail immediately on creating new encrypted file

        var protection = new NoteProtectionService(attachmentStorage: storage, fileAdapter: adapter);

        using (var db = CreateContext(dbPath))
        {
            var result = protection.ProtectNote(db, noteId, "pw");
            Assert.False(result.Success);
        }

        // Verify original file is intact and note remains unprotected
        using (var db = CreateContext(dbPath))
        {
            var note = db.Notes.Find(noteId)!;
            Assert.False(note.IsProtected);
            Assert.Equal("Заметка до сбоя", note.Text);

            var att = db.NoteAttachments.Find(attId)!;
            Assert.False(att.IsProtected);
            Assert.Equal("исходник.bin", att.OriginalFileName);
            Assert.True(File.Exists(originalFilePath));
            Assert.Equal(originalContent, File.ReadAllBytes(originalFilePath));
        }
    }

    [Fact]
    public void TwoPhase_Protect_FailureOnSecondAttachment_RollsBack_CleansFirstContainer_NoDataLoss()
    {
        string dbPath = CreateTempDb();
        var storage = new AttachmentStorageService(MakeTempDir());
        InitDb(dbPath);

        int noteId;
        byte[] content1 = Encoding.UTF8.GetBytes("Файл 1");
        byte[] content2 = Encoding.UTF8.GetBytes("Файл 2");
        string originalPath1, originalPath2;

        using (var db = CreateContext(dbPath))
        {
            var note = new Note { Text = "Два файла, второй упадет" };
            db.Notes.Add(note);
            db.SaveChanges();
            noteId = note.Id;

            var att1 = AddAttachment(db, storage, noteId, content1, "f1.bin");
            var att2 = AddAttachment(db, storage, noteId, content2, "f2.bin");
            originalPath1 = storage.GetFullPath(att1.RelativePath);
            originalPath2 = storage.GetFullPath(att2.RelativePath);
        }

        var adapter = new FaultInjectingProtectionFileAdapter();
        // Fail on the second created file write
        adapter.ShouldFailWrite = (path, content) => adapter.CreatedFiles.Count >= 1;

        var protection = new NoteProtectionService(attachmentStorage: storage, fileAdapter: adapter);

        using (var db = CreateContext(dbPath))
        {
            var result = protection.ProtectNote(db, noteId, "pw");
            Assert.False(result.Success);
        }

        // Verify:
        // 1. The first created container was cleaned up
        Assert.NotEmpty(adapter.CreatedFiles);
        foreach (var created in adapter.CreatedFiles)
        {
            Assert.False(File.Exists(created), $"Временный файл {created} должен быть удалён при откате.");
        }

        // 2. Both original plaintext files are untouched
        Assert.True(File.Exists(originalPath1));
        Assert.True(File.Exists(originalPath2));
        Assert.Equal(content1, File.ReadAllBytes(originalPath1));
        Assert.Equal(content2, File.ReadAllBytes(originalPath2));

        // 3. Note and attachments in DB are untouched
        using (var db = CreateContext(dbPath))
        {
            var note = db.Notes.Find(noteId)!;
            Assert.False(note.IsProtected);
            Assert.Equal("Два файла, второй упадет", note.Text);
            var atts = db.NoteAttachments.Where(a => a.NoteId == noteId).ToList();
            Assert.All(atts, a => Assert.False(a.IsProtected));
        }
    }

    [Fact]
    public void TwoPhase_Protect_FailureOnSaveChanges_RollsBack_CleansAllContainers_NoDataLoss()
    {
        string dbPath = CreateTempDb();
        var storage = new AttachmentStorageService(MakeTempDir());
        InitDb(dbPath);

        int noteId;
        byte[] content = Encoding.UTF8.GetBytes("Данные до ошибки сохранения БД");
        string originalPath;

        using (var db = CreateContext(dbPath))
        {
            var note = new Note { Text = "Заметка до ошибки БД" };
            db.Notes.Add(note);
            db.SaveChanges();
            noteId = note.Id;

            var att = AddAttachment(db, storage, noteId, content, "doc.bin");
            originalPath = storage.GetFullPath(att.RelativePath);
        }

        var interceptor = new FaultInjectionInterceptor { FailOnSaveChanges = true };
        var adapter = new FaultInjectingProtectionFileAdapter();
        var protection = new NoteProtectionService(attachmentStorage: storage, fileAdapter: adapter);

        using (var db = CreateContext(dbPath, interceptor))
        {
            var result = protection.ProtectNote(db, noteId, "pw");
            Assert.False(result.Success);
        }

        // All created containers must be deleted
        foreach (var created in adapter.CreatedFiles)
        {
            Assert.False(File.Exists(created));
        }

        // Original plaintext file must remain intact
        Assert.True(File.Exists(originalPath));
        Assert.Equal(content, File.ReadAllBytes(originalPath));

        // DB state unchanged
        using (var db = CreateContext(dbPath))
        {
            var note = db.Notes.Find(noteId)!;
            Assert.False(note.IsProtected);
            Assert.Equal("Заметка до ошибки БД", note.Text);
        }
    }

    [Fact]
    public void TwoPhase_Protect_FailureOnCommit_RollsBack_CleansAllContainers_NoDataLoss()
    {
        string dbPath = CreateTempDb();
        var storage = new AttachmentStorageService(MakeTempDir());
        InitDb(dbPath);

        int noteId;
        byte[] content = Encoding.UTF8.GetBytes("Данные до ошибки commit");
        string originalPath;

        using (var db = CreateContext(dbPath))
        {
            var note = new Note { Text = "Заметка до сбоя фиксации" };
            db.Notes.Add(note);
            db.SaveChanges();
            noteId = note.Id;

            var att = AddAttachment(db, storage, noteId, content, "doc_commit.bin");
            originalPath = storage.GetFullPath(att.RelativePath);
        }

        var interceptor = new FaultInjectionInterceptor { FailOnCommit = true };
        var adapter = new FaultInjectingProtectionFileAdapter();
        var protection = new NoteProtectionService(attachmentStorage: storage, fileAdapter: adapter);

        using (var db = CreateContext(dbPath, interceptor))
        {
            var result = protection.ProtectNote(db, noteId, "pw");
            Assert.False(result.Success);
        }

        // All created containers must be deleted
        foreach (var created in adapter.CreatedFiles)
        {
            Assert.False(File.Exists(created));
        }

        // Original file intact
        Assert.True(File.Exists(originalPath));
        Assert.Equal(content, File.ReadAllBytes(originalPath));

        // DB unchanged
        using (var db = CreateContext(dbPath))
        {
            var note = db.Notes.Find(noteId)!;
            Assert.False(note.IsProtected);
        }
    }

    [Fact]
    public void TwoPhase_Protect_FailureOnDeleteOldFilePostCommit_Succeeds_RecordValid()
    {
        string dbPath = CreateTempDb();
        var storage = new AttachmentStorageService(MakeTempDir());
        InitDb(dbPath);

        int noteId;
        byte[] content = Encoding.UTF8.GetBytes("Файл с ошибкой удаления оригинала");
        int attId;

        using (var db = CreateContext(dbPath))
        {
            var note = new Note { Text = "Заметка для post-commit delete error" };
            db.Notes.Add(note);
            db.SaveChanges();
            noteId = note.Id;

            var att = AddAttachment(db, storage, noteId, content, "fail_delete.bin");
            attId = att.Id;
        }

        var adapter = new FaultInjectingProtectionFileAdapter();
        adapter.ShouldFailDelete = (path) => path.EndsWith(".bin", StringComparison.OrdinalIgnoreCase);

        var protection = new NoteProtectionService(attachmentStorage: storage, fileAdapter: adapter);

        using (var db = CreateContext(dbPath))
        {
            // Protect must succeed even if deleting the old file post-commit fails
            var result = protection.ProtectNote(db, noteId, "pw-post-delete");
            Assert.True(result.Success, result.ErrorMessage);
        }

        // Working record must be valid and unlockable byte-for-byte
        using (var db = CreateContext(dbPath))
        {
            var note = db.Notes.Find(noteId)!;
            Assert.True(note.IsProtected);

            var att = db.NoteAttachments.Find(attId)!;
            Assert.True(att.IsProtected);

            protection.UnlockNote(db, noteId, "pw-post-delete");
            string temp = protection.DecryptAttachmentToTempFile(db, noteId, att);
            try
            {
                Assert.Equal(content, File.ReadAllBytes(temp));
            }
            finally
            {
                ProtectedAttachmentFile.TryDeleteTempFile(temp);
            }
        }
    }

    // ------------------------------------------------------------------
    // Adding and removing attachments from a protected note
    // ------------------------------------------------------------------

    [Fact]
    public void ProtectedNote_AddAttachment_EncryptsBeforeCommit_RestoresOnUnlock()
    {
        string dbPath = CreateTempDb();
        var storage = new AttachmentStorageService(MakeTempDir());
        InitDb(dbPath);

        int noteId;
        using (var db = CreateContext(dbPath))
        {
            var note = new Note { Text = "Заметка изначально без файлов" };
            db.Notes.Add(note);
            db.SaveChanges();
            noteId = note.Id;
        }

        var protection = new NoteProtectionService(attachmentStorage: storage);
        using (var db = CreateContext(dbPath))
        {
            Assert.True(protection.ProtectNote(db, noteId, "pw-add-att").Success);
        }

        byte[] newContent = Encoding.UTF8.GetBytes("Новый секретный файл, добавленный в защищённую заметку");
        int newAttId;
        using (var db = CreateContext(dbPath))
        {
            protection.UnlockNote(db, noteId, "pw-add-att");

            // Save plaintext attachment to storage (as editor does on file pick)
            var saved = storage.SaveFromBytes(newContent, "новый_документ.docx", 50 * 1024 * 1024);
            string plaintextFullPath = storage.GetFullPath(saved.RelativePath);

            var att = new NoteAttachment
            {
                NoteId = noteId,
                OriginalFileName = "новый_документ.docx",
                StoredFileName = saved.StoredFileName,
                RelativePath = saved.RelativePath,
                ContentType = saved.ContentType,
                Size = saved.Size,
                Sha256 = saved.Sha256,
                CreatedAt = DateTime.Now
            };
            db.NoteAttachments.Add(att);

            var createdFiles = new List<string>();
            var oldFilesToDelete = new List<string>();

            // Encrypt before commit
            protection.EncryptAttachment(db, noteId, att, "новый_документ.docx", plaintextFullPath, createdFiles, oldFilesToDelete);

            db.SaveChanges();

            // Delete old plaintext file post-commit
            foreach (var f in oldFilesToDelete)
            {
                protection.FileAdapter.Delete(f);
            }

            newAttId = att.Id;
        }

        // Verify on disk and in DB
        using (var db = CreateContext(dbPath))
        {
            var att = db.NoteAttachments.Find(newAttId)!;
            Assert.True(att.IsProtected);
            Assert.Equal("attachment.qnat", att.OriginalFileName);

            string path = storage.GetFullPath(att.RelativePath);
            Assert.True(File.Exists(path));
            Assert.False(File.ReadAllBytes(path).SequenceEqual(newContent));

            protection.UnlockNote(db, noteId, "pw-add-att");
            Assert.Equal("новый_документ.docx", protection.DecryptAttachmentName(db, noteId, att));

            string temp = protection.DecryptAttachmentToTempFile(db, noteId, att);
            try
            {
                Assert.Equal(newContent, File.ReadAllBytes(temp));
            }
            finally
            {
                ProtectedAttachmentFile.TryDeleteTempFile(temp);
            }
        }
    }

    [Fact]
    public void ProtectedNote_RemoveAttachment_DeletesPostCommit_RollbackPreservesFile()
    {
        string dbPath = CreateTempDb();
        var storage = new AttachmentStorageService(MakeTempDir());
        InitDb(dbPath);

        int noteId;
        int attId;
        string containerPath;
        byte[] content = Encoding.UTF8.GetBytes("Файл для удаления");

        using (var db = CreateContext(dbPath))
        {
            var note = new Note { Text = "Заметка с файлом для удаления" };
            db.Notes.Add(note);
            db.SaveChanges();
            noteId = note.Id;

            attId = AddAttachment(db, storage, noteId, content, "del.bin").Id;
        }

        var protection = new NoteProtectionService(attachmentStorage: storage);
        using (var db = CreateContext(dbPath))
        {
            Assert.True(protection.ProtectNote(db, noteId, "pw-del-att").Success);
        }

        using (var db = CreateContext(dbPath))
        {
            var att = db.NoteAttachments.Find(attId)!;
            containerPath = storage.GetFullPath(att.RelativePath);
            Assert.True(File.Exists(containerPath));
        }

        // 1. Simulate failure during removal (rollback preserves file)
        var interceptor = new FaultInjectionInterceptor { FailOnCommit = true };
        using (var db = CreateContext(dbPath, interceptor))
        {
            var filesToDelete = new List<string>();
            using var tx = db.Database.BeginTransaction();
            try
            {
                var att = db.NoteAttachments.Find(attId)!;
                filesToDelete.Add(storage.GetFullPath(att.RelativePath));
                db.NoteAttachments.Remove(att);
                db.SaveChanges();
                tx.Commit(); // throws
                foreach (var f in filesToDelete) File.Delete(f);
            }
            catch
            {
                tx.Rollback();
            }
        }

        // File must still exist
        Assert.True(File.Exists(containerPath), "Файл не должен удаляться при откате транзакции удаления.");

        // 2. Successful removal (file deleted post-commit)
        using (var db = CreateContext(dbPath))
        {
            var filesToDelete = new List<string>();
            using var tx = db.Database.BeginTransaction();
            var att = db.NoteAttachments.Find(attId)!;
            filesToDelete.Add(storage.GetFullPath(att.RelativePath));
            db.NoteAttachments.Remove(att);
            db.SaveChanges();
            tx.Commit();

            foreach (var f in filesToDelete) File.Delete(f);
        }

        Assert.False(File.Exists(containerPath), "Файл должен быть удалён после успешного commit.");
    }

    [Fact]
    public void ProtectedNote_SaveEditor_PreservesPinFavoriteInboxTrashTags_AndKeepsTextEmpty()
    {
        string dbPath = CreateTempDb();
        var storage = new AttachmentStorageService(MakeTempDir());
        InitDb(dbPath);

        int noteId;
        using (var db = CreateContext(dbPath))
        {
            var note = new Note { Text = "Секретный текст" };
            db.Notes.Add(note);
            db.SaveChanges();
            noteId = note.Id;
        }

        var protection = new NoteProtectionService(attachmentStorage: storage);
        using (var db = CreateContext(dbPath))
        {
            Assert.True(protection.ProtectNote(db, noteId, "pw-meta").Success);
        }

        using (var db = CreateContext(dbPath))
        {
            var note = db.Notes.Include(n => n.NoteTags).Single(n => n.Id == noteId);
            var tag = new Tag { Name = "СекретныйТег", SyncId = Guid.NewGuid() };
            db.Tags.Add(tag);
            db.SaveChanges();

            // Simulate editor metadata updates
            using var editorVm = new NoteEditorViewModel(new TagDetectionService(), new List<Tag> { tag }, existingNote: note, draftJournalService: NoOpDraftJournalService.Instance);
            editorVm.IsPinned = true;
            editorVm.IsFavorite = true;
            editorVm.IsInbox = false;
            editorVm.ActiveTags.Add(new NoteEditorTagItem { TagId = tag.Id, TagName = tag.Name, Origin = TagOrigin.Manual });

            // Apply metadata and links
            editorVm.ApplyMetadataAndLinks(note);

            // Plaintext fields must be cleared for protected note
            note.Text = string.Empty;
            note.SourceProcessName = null;
            note.SourceWindowTitle = null;
            note.SourceUrl = null;

            // Apply unlocked edits (encrypted into payload)
            protection.UnlockNote(db, noteId, "pw-meta");
            protection.ApplyUnlockedEdits(db, note, "Измененный секретный текст", null, null, null);

            db.SaveChanges();
        }

        // Verify in fresh context
        using (var db = CreateContext(dbPath))
        {
            var note = db.Notes.Include(n => n.NoteTags).ThenInclude(nt => nt.Tag).Single(n => n.Id == noteId);
            Assert.True(note.IsProtected);
            Assert.Equal(string.Empty, note.Text); // never leaks plaintext!
            Assert.True(note.IsPinned);
            Assert.True(note.IsFavorite);
            Assert.False(note.IsInbox);
            Assert.Contains(note.NoteTags, nt => nt.Tag?.Name == "СекретныйТег");

            // Unlock must yield the updated text
            var payload = protection.UnlockNote(db, noteId, "pw-meta");
            Assert.Equal("Измененный секретный текст", payload.Text);
        }
    }

    // ------------------------------------------------------------------
    // Cloud synchronization and two-device exchange
    // ------------------------------------------------------------------

    [Fact]
    public async Task Sync_TwoDeviceExchange_ProtectedNoteWithAttachment_NeutralBeforeUnlock_ByteForByteAfterUnlock()
    {
        string dbPathA = CreateTempDb();
        string dbPathB = CreateTempDb();
        InitDb(dbPathA);
        InitDb(dbPathB);

        var dirA = MakeTempDir();
        var dirB = MakeTempDir();
        var storageA = new AttachmentStorageService(dirA);
        var storageB = new AttachmentStorageService(dirB);

        int noteIdA;
        Guid noteSyncId;
        byte[] originalContent = Encoding.UTF8.GetBytes("Секретные данные для синхронизации между двумя устройствами");
        int attIdA;

        using (var dbA = CreateContext(dbPathA))
        {
            var noteA = new Note { Text = "Секретная заметка Device A" };
            dbA.Notes.Add(noteA);
            dbA.SaveChanges();
            noteIdA = noteA.Id;
            noteSyncId = noteA.SyncId;

            var attA = AddAttachment(dbA, storageA, noteIdA, originalContent, "deviceA_secret.pdf");
            attIdA = attA.Id;
        }

        var protectionA = new NoteProtectionService(attachmentStorage: storageA);
        using (var dbA = CreateContext(dbPathA))
        {
            Assert.True(protectionA.ProtectNote(dbA, noteIdA, "shared-note-password").Success);
        }

        // Sync setup
        var transport = new SyncEngineTests.TestCloudObjectStoreTransport();
        var settings = new SyncCloudSettings
        {
            Enabled = true,
            Endpoint = "https://storage.yandexcloud.net",
            Region = "ru-central1",
            Bucket = "notes-sync-bucket",
            Prefix = "sync-root",
            SyncAttachments = true
        };
        var credsA = new InMemoryCredentialsStorage();
        var credsB = new InMemoryCredentialsStorage();
        var crypto = new SyncCryptoService(iterations: 5_000);
        var keyHelper = new SyncObjectKeyHelper(settings);

        var devIdA = Guid.NewGuid();
        var devIdB = Guid.NewGuid();

        var exporterA = new SyncPackageExporter(crypto, new FixedDeviceId(devIdA));
        var importerA = new SyncPackageImporter(crypto);
        var blobServiceA = new SyncAttachmentBlobService(transport, crypto, storageA, keyHelper, settings);
        var engineA = new SyncEngine(transport, exporterA, importerA, new FixedDeviceId(devIdA), settings, credsA, keyHelper, dbFactory: () => CreateContext(dbPathA), blobService: blobServiceA);

        var exporterB = new SyncPackageExporter(crypto, new FixedDeviceId(devIdB));
        var importerB = new SyncPackageImporter(crypto);
        var blobServiceB = new SyncAttachmentBlobService(transport, crypto, storageB, keyHelper, settings);
        var engineB = new SyncEngine(transport, exporterB, importerB, new FixedDeviceId(devIdB), settings, credsB, keyHelper, dbFactory: () => CreateContext(dbPathB), blobService: blobServiceB);

        // Device A pushes
        var pushResult = await engineA.RunSyncCycleAsync("cloud-master-pw");
        Assert.True(pushResult.Success, string.Join("; ", pushResult.Errors));

        // Device B pulls
        var pullResult = await engineB.RunSyncCycleAsync("cloud-master-pw");
        Assert.True(pullResult.Success, string.Join("; ", pullResult.Errors));

        // On Device B before unlock:
        var protectionB = new NoteProtectionService(attachmentStorage: storageB);
        using (var dbB = CreateContext(dbPathB))
        {
            var noteB = dbB.Notes.SingleOrDefault(n => n.SyncId == noteSyncId);
            Assert.NotNull(noteB);
            Assert.True(noteB!.IsProtected);
            Assert.Equal(string.Empty, noteB.Text);

            var attB = dbB.NoteAttachments.SingleOrDefault(a => a.NoteId == noteB.Id);
            Assert.NotNull(attB);
            Assert.True(attB!.IsProtected);
            Assert.Equal("attachment.qnat", attB.OriginalFileName); // Neutral name before unlock

            // Unlock on Device B with the note password
            var payload = protectionB.UnlockNote(dbB, noteB.Id, "shared-note-password");
            Assert.Equal("Секретная заметка Device A", payload.Text);

            // Decrypt attachment name
            string originalName = protectionB.DecryptAttachmentName(dbB, noteB.Id, attB);
            Assert.Equal("deviceA_secret.pdf", originalName);

            // Decrypt attachment content byte-for-byte
            string tempFile = protectionB.DecryptAttachmentToTempFile(dbB, noteB.Id, attB);
            try
            {
                byte[] roundTripped = File.ReadAllBytes(tempFile);
                Assert.Equal(originalContent, roundTripped);
            }
            finally
            {
                ProtectedAttachmentFile.TryDeleteTempFile(tempFile);
            }
        }
    }

    [Fact]
    public void Sync_CloudPayloadPrivacy_NoPlaintextLeaked()
    {
        string dbPath = CreateTempDb();
        var storage = new AttachmentStorageService(MakeTempDir());
        InitDb(dbPath);

        int noteId;
        byte[] content = Encoding.UTF8.GetBytes("Совершенно секретные байты");

        using (var db = CreateContext(dbPath))
        {
            var note = new Note
            {
                Text = "PlaintextSecretTitle\nВторая строка текста",
                SourceProcessName = "SecretProcess.exe",
                SourceWindowTitle = "Secret Window Title",
                SourceUrl = "https://secret.example.com/vault"
            };
            db.Notes.Add(note);
            db.SaveChanges();
            noteId = note.Id;

            AddAttachment(db, storage, noteId, content, "secret_attachment.docx");
        }

        var protection = new NoteProtectionService(attachmentStorage: storage);
        using (var db = CreateContext(dbPath))
        {
            Assert.True(protection.ProtectNote(db, noteId, "pw-privacy").Success);
        }

        var crypto = new SyncCryptoService(iterations: 5_000);
        var deviceId = Guid.NewGuid();
        var exporter = new SyncPackageExporter(crypto, new FixedDeviceId(deviceId));

        SyncPackagePayload payload;
        using (var db = CreateContext(dbPath))
        {
            payload = exporter.BuildDeterministicPayload(db, deviceId);
        }

        string json = JsonSerializer.Serialize(payload);

        // Plaintext text, source context and original filename must NEVER appear in the payload JSON
        Assert.DoesNotContain("PlaintextSecretTitle", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SecretProcess.exe", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Secret Window Title", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret.example.com", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret_attachment.docx", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Совершенно секретные байты", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SyncConflict_ProtectedNote_KeepBoth_PreservesCiphertextAndNeverCreatesFakePlaintextNote()
    {
        string dbPath = CreateTempDb();
        var storage = new AttachmentStorageService(MakeTempDir());
        InitDb(dbPath);

        int localNoteId;
        Guid syncId;
        var protection = new NoteProtectionService(attachmentStorage: storage);

        using (var db = CreateContext(dbPath))
        {
            var note = new Note { Text = "Локальная версия заметки" };
            db.Notes.Add(note);
            db.SaveChanges();
            localNoteId = note.Id;
            syncId = note.SyncId;
            Assert.True(protection.ProtectNote(db, localNoteId, "pw-conflict").Success);
        }

        // Create remote protected note with different ciphertext for the same password
        string remoteCiphertext;
        string remoteNonce;
        string remoteTag;
        string remoteSalt;
        int formatVersion;
        int kdfIterations;

        using (var tempDb = CreateContext(CreateTempDb()))
        {
            InitDb(tempDb.Database.GetDbConnection().ConnectionString.Replace("Data Source=", ""));
            var rNote = new Note { SyncId = syncId, Text = "Удалённая версия заметки" };
            tempDb.Notes.Add(rNote);
            tempDb.SaveChanges();
            Assert.True(protection.ProtectNote(tempDb, rNote.Id, "pw-conflict").Success);
            remoteCiphertext = rNote.ProtectedCiphertextBase64 ?? string.Empty;
            remoteNonce = rNote.ProtectedNonceBase64 ?? string.Empty;
            remoteTag = rNote.ProtectedTagBase64 ?? string.Empty;
            remoteSalt = rNote.ProtectedSaltBase64 ?? string.Empty;
            formatVersion = rNote.ProtectedFormatVersion;
            kdfIterations = rNote.ProtectedKdfIterations;
        }

        var remoteDto = new SyncNoteDto
        {
            SyncId = syncId,
            RevisionId = Guid.NewGuid(),
            ParentRevisionId = Guid.NewGuid(),
            DeviceId = Guid.NewGuid(),
            UpdatedAtUtc = DateTime.UtcNow,
            IsProtected = true,
            ProtectedFormatVersion = formatVersion,
            ProtectedKdfIterations = kdfIterations,
            ProtectedSaltBase64 = remoteSalt,
            ProtectedNonceBase64 = remoteNonce,
            ProtectedTagBase64 = remoteTag,
            ProtectedCiphertextBase64 = remoteCiphertext,
            Text = string.Empty
        };

        int conflictId;
        using (var db = CreateContext(dbPath))
        {
            var conflictRecord = new SyncConflictRecord
            {
                SyncId = syncId,
                EntityType = "Note",
                LocalRevisionId = Guid.NewGuid(),
                RemoteRevisionId = remoteDto.RevisionId,
                ParentRevisionId = remoteDto.ParentRevisionId,
                SourceDeviceId = remoteDto.DeviceId,
                SourcePackageId = Guid.NewGuid(),
                DetectedAtUtc = DateTime.UtcNow,
                Reason = "Branch conflict",
                RemoteDataJson = JsonSerializer.Serialize(remoteDto),
                IsResolved = false
            };
            db.SyncConflicts.Add(conflictRecord);
            db.SaveChanges();
            conflictId = conflictRecord.Id;
        }

        var conflictService = new SyncConflictService(
            () => CreateContext(dbPath),
            new FixedDeviceId(Guid.NewGuid()));

        var resolution = await conflictService.ResolveKeepBothAsync(conflictId);
        Assert.True(resolution.Success, resolution.ErrorMessage);

        // Verify:
        // 1. Neither note has fake plaintext "🔒 Защищённая заметка..."
        // 2. Both notes are protected with valid ciphertext
        // 3. Both notes can be unlocked with "pw-conflict" and return their respective contents!
        using (var db = CreateContext(dbPath))
        {
            var notes = db.Notes.ToList();
            Assert.Equal(2, notes.Count);

            foreach (var n in notes)
            {
                Assert.True(n.IsProtected);
                Assert.Equal(string.Empty, n.Text);
                Assert.DoesNotContain("🔒 Защищённая", n.Text);
            }

            var localNote = notes.Single(n => n.Id == localNoteId);
            var remoteCopy = notes.Single(n => n.Id != localNoteId);

            var localPayload = protection.UnlockNote(db, localNote.Id, "pw-conflict");
            Assert.Equal("Локальная версия заметки", localPayload.Text);

            var remotePayload = protection.UnlockNote(db, remoteCopy.Id, "pw-conflict");
            Assert.Equal("Удалённая версия заметки", remotePayload.Text);
        }
    }

    [Fact]
    public void ManualSmoke_FullEndToEndLifecycle_PassesAllChecks()
    {
        string dbPath = CreateTempDb();
        string storageDir = MakeTempDir();
        var storage = new AttachmentStorageService(storageDir);
        InitDb(dbPath);

        int noteId;
        byte[] docBytes = Encoding.UTF8.GetBytes("Конфиденциальный договор №12345");
        string docOriginalName = "contract.pdf";

        // 1. Создать заметку + история + вложение
        using (var db = CreateContext(dbPath))
        {
            var note = new Note
            {
                Text = "Исходный текст заметки для smoke-теста",
                SourceProcessName = "chrome.exe",
                SourceWindowTitle = "Важные документы",
                IsPinned = false,
                IsFavorite = false
            };
            db.Notes.Add(note);
            db.SaveChanges();
            noteId = note.Id;

            // Добавить ревизию истории
            var rev = new NoteRevision
            {
                NoteId = noteId,
                Text = "Первая версия заметки",
                CreatedAt = DateTime.UtcNow.AddMinutes(-10)
            };
            db.NoteRevisions.Add(rev);

            // Добавить вложение
            AddAttachment(db, storage, noteId, docBytes, docOriginalName);
            db.SaveChanges();
        }

        // 2. Установить пароль
        var protection1 = new NoteProtectionService(attachmentStorage: storage);
        using (var db = CreateContext(dbPath))
        {
            var result = protection1.ProtectNote(db, noteId, "InitialSecretPass123!");
            Assert.True(result.Success, result.ErrorMessage);
        }

        // Проверить состояние после установки защиты
        using (var db = CreateContext(dbPath))
        {
            var note = db.Notes.Include(n => n.Attachments).Include(n => n.Revisions).Single(n => n.Id == noteId);
            Assert.True(note.IsProtected);
            Assert.Equal(string.Empty, note.Text);
            Assert.Null(note.SourceProcessName);
            Assert.False(string.IsNullOrEmpty(note.ProtectedCiphertextBase64));

            var att = note.Attachments.Single();
            Assert.True(att.IsProtected);
            Assert.Equal("attachment.qnat", att.OriginalFileName);
            Assert.EndsWith(".qnat", att.StoredFileName);
            Assert.False(File.Exists(Path.Combine(storageDir, docOriginalName)));
        }

        // 3. Перезапустить приложение (новый независимый экземпляр сервиса и сессий)
        var protection2 = new NoteProtectionService(attachmentStorage: storage);

        // 4. Проверить неверный пароль -> ошибка; правильный -> успех
        using (var db = CreateContext(dbPath))
        {
            Assert.Throws<NoteProtectionSecurityException>(() =>
                protection2.UnlockNote(db, noteId, "WrongPassword123!"));

            var payload = protection2.UnlockNote(db, noteId, "InitialSecretPass123!");
            Assert.Equal("Исходный текст заметки для smoke-теста", payload.Text);
            Assert.Equal("chrome.exe", payload.SourceProcessName);
        }

        // 5. Изменить текст, теги, favorite/pin и вложения
        byte[] imgBytes = Encoding.UTF8.GetBytes("PNG_IMAGE_DATA_BYTES_67890");
        string imgOriginalName = "appendix.png";

        using (var db = CreateContext(dbPath))
        {
            var note = db.Notes.Include(n => n.Attachments).Include(n => n.NoteTags).Single(n => n.Id == noteId);

            note.IsPinned = true;
            note.IsFavorite = true;
            note.UpdatedAt = DateTime.Now;

            var tag = new Tag { Name = "confidential" };
            db.Tags.Add(tag);
            note.NoteTags.Add(new NoteTag { Note = note, Tag = tag });

            protection2.ApplyUnlockedEdits(db, note, "Обновлённый секретный текст", "code.exe", "VS Code", null);
            protection2.SaveProtectedRevision(db, note, "Обновлённый секретный текст");

            var savedNew = storage.SaveFromBytes(imgBytes, imgOriginalName, 50 * 1024 * 1024);
            var newAtt = new NoteAttachment
            {
                NoteId = noteId,
                OriginalFileName = imgOriginalName,
                StoredFileName = savedNew.StoredFileName,
                RelativePath = savedNew.RelativePath,
                ContentType = savedNew.ContentType,
                Size = savedNew.Size,
                Sha256 = savedNew.Sha256
            };
            var createdFiles = new List<string>();
            var oldFilesToDelete = new List<string>();
            protection2.EncryptAttachment(db, noteId, newAtt, imgOriginalName, storage.GetFullPath(newAtt.RelativePath), createdFiles, oldFilesToDelete);
            note.Attachments.Add(newAtt);

            db.SaveChanges();
            foreach (var oldF in oldFilesToDelete)
            {
                try { File.Delete(oldF); } catch { }
            }
        }

        // 6. Сменить пароль
        using (var db = CreateContext(dbPath))
        {
            var changeRes = protection2.ChangePassword(db, noteId, "InitialSecretPass123!", "NewStrongPassword456!");
            Assert.True(changeRes.Success, changeRes.ErrorMessage);
        }

        // 7. Проверить, что старый пароль больше не работает, а новый работает
        var protection3 = new NoteProtectionService(attachmentStorage: storage);
        using (var db = CreateContext(dbPath))
        {
            Assert.Throws<NoteProtectionSecurityException>(() =>
                protection3.UnlockNote(db, noteId, "InitialSecretPass123!"));

            var payload = protection3.UnlockNote(db, noteId, "NewStrongPassword456!");
            Assert.Equal("Обновлённый секретный текст", payload.Text);

            var atts = db.NoteAttachments.Where(a => a.NoteId == noteId).OrderBy(a => a.Id).ToList();
            Assert.Equal(2, atts.Count);

            string name1 = protection3.DecryptAttachmentName(db, noteId, atts[0]);
            Assert.Equal(docOriginalName, name1);
            string temp1 = protection3.DecryptAttachmentToTempFile(db, noteId, atts[0]);
            Assert.Equal(docBytes, File.ReadAllBytes(temp1));
            File.Delete(temp1);

            string name2 = protection3.DecryptAttachmentName(db, noteId, atts[1]);
            Assert.Equal(imgOriginalName, name2);
            string temp2 = protection3.DecryptAttachmentToTempFile(db, noteId, atts[1]);
            Assert.Equal(imgBytes, File.ReadAllBytes(temp2));
            File.Delete(temp2);
        }

        // 8. Снять защиту
        using (var db = CreateContext(dbPath))
        {
            var removeRes = protection3.RemoveProtection(db, noteId, "NewStrongPassword456!");
            Assert.True(removeRes.Success, removeRes.ErrorMessage);
        }

        // 9. Проверить обычный поиск и экспорт
        using (var db = CreateContext(dbPath))
        {
            var note = db.Notes.Include(n => n.Attachments).Include(n => n.NoteTags).ThenInclude(nt => nt.Tag).Single(n => n.Id == noteId);
            Assert.False(note.IsProtected);
            Assert.Equal("Обновлённый секретный текст", note.Text);
            Assert.True(note.IsPinned);
            Assert.True(note.IsFavorite);
            Assert.Contains(note.NoteTags, nt => nt.Tag.Name == "confidential");

            var found = db.Notes.Where(n => n.Text.Contains("Обновлённый секретный")).ToList();
            Assert.Single(found);

            var att1 = note.Attachments.First(a => a.OriginalFileName == docOriginalName);
            Assert.False(att1.IsProtected);
            string fullPath1 = storage.GetFullPath(att1.RelativePath);
            Assert.True(File.Exists(fullPath1));
            Assert.Equal(docBytes, File.ReadAllBytes(fullPath1));

            var exporter = new NoteExportService();
            string exportFile = Path.Combine(storageDir, "export.json");
            var exportRes = exporter.ExportToJson(db, exportFile);
            Assert.True(exportRes.Success, exportRes.ErrorMessage);
            string exportedJson = File.ReadAllText(exportFile);
            Assert.Contains("Обновлённый секретный текст", exportedJson);
            Assert.Contains("confidential", exportedJson);
        }
    }

    [Fact]
    public async Task Sync_KeepBoth_TransfersProtectedOriginalSyncId_CrossDatabase_UnlocksAndIsNoOp()
    {
        string dbPathA = CreateTempDb();
        string dbPathB = CreateTempDb();
        var storageA = new AttachmentStorageService(MakeTempDir());
        var storageB = new AttachmentStorageService(MakeTempDir());
        InitDb(dbPathA);
        InitDb(dbPathB);

        var devIdA = Guid.NewGuid();
        var devIdB = Guid.NewGuid();
        var crypto = new SyncCryptoService(iterations: 5_000);
        string syncPassword = "cloud-master-pw";
        string notePassword = "pw-keepboth-test-123";

        var protectionA = new NoteProtectionService(attachmentStorage: storageA);
        var protectionB = new NoteProtectionService(attachmentStorage: storageB);

        Guid originalSyncId;
        Guid plainNoteSyncId;
        int localNoteId;

        // 1. На Device A создаём защищённую заметку и обычную незащищённую заметку
        using (var dbA = CreateContext(dbPathA))
        {
            var protNote = new Note { Text = "Секретный текст заметки на Device A" };
            var plainNote = new Note { Text = "Обычный незащищённый текст" };
            dbA.Notes.AddRange(protNote, plainNote);
            dbA.SaveChanges();

            localNoteId = protNote.Id;
            originalSyncId = protNote.SyncId;
            plainNoteSyncId = plainNote.SyncId;

            Assert.True(protectionA.ProtectNote(dbA, localNoteId, notePassword).Success);
        }

        // 2. Формируем удалённую защищённую версию с тем же SyncId = originalSyncId для моделирования конфликта
        string remoteCiphertext;
        string remoteNonce;
        string remoteTag;
        string remoteSalt;
        int formatVersion;
        int kdfIterations;

        using (var tempDb = CreateContext(CreateTempDb()))
        {
            InitDb(tempDb.Database.GetDbConnection().ConnectionString.Replace("Data Source=", ""));
            var rNote = new Note { SyncId = originalSyncId, Text = "Удалённая конфликтующая версия" };
            tempDb.Notes.Add(rNote);
            tempDb.SaveChanges();
            Assert.True(protectionA.ProtectNote(tempDb, rNote.Id, notePassword).Success);
            remoteCiphertext = rNote.ProtectedCiphertextBase64 ?? string.Empty;
            remoteNonce = rNote.ProtectedNonceBase64 ?? string.Empty;
            remoteTag = rNote.ProtectedTagBase64 ?? string.Empty;
            remoteSalt = rNote.ProtectedSaltBase64 ?? string.Empty;
            formatVersion = rNote.ProtectedFormatVersion;
            kdfIterations = rNote.ProtectedKdfIterations;
        }

        var remoteDto = new SyncNoteDto
        {
            SyncId = originalSyncId,
            RevisionId = Guid.NewGuid(),
            ParentRevisionId = Guid.NewGuid(),
            DeviceId = Guid.NewGuid(),
            UpdatedAtUtc = DateTime.UtcNow,
            IsProtected = true,
            ProtectedFormatVersion = formatVersion,
            ProtectedKdfIterations = kdfIterations,
            ProtectedSaltBase64 = remoteSalt,
            ProtectedNonceBase64 = remoteNonce,
            ProtectedTagBase64 = remoteTag,
            ProtectedCiphertextBase64 = remoteCiphertext,
            Text = string.Empty
        };

        // Создаем запись конфликта в dbA
        int conflictId;
        using (var dbA = CreateContext(dbPathA))
        {
            var conflictRecord = new SyncConflictRecord
            {
                SyncId = originalSyncId,
                EntityType = "Note",
                LocalRevisionId = Guid.NewGuid(),
                RemoteRevisionId = remoteDto.RevisionId,
                ParentRevisionId = remoteDto.ParentRevisionId,
                SourceDeviceId = remoteDto.DeviceId,
                SourcePackageId = Guid.NewGuid(),
                DetectedAtUtc = DateTime.UtcNow,
                Reason = "Branch conflict",
                RemoteDataJson = JsonSerializer.Serialize(remoteDto),
                IsResolved = false
            };
            dbA.SyncConflicts.Add(conflictRecord);
            dbA.SaveChanges();
            conflictId = conflictRecord.Id;
        }

        // 3. Разрешаем конфликт через KeepBoth
        var conflictService = new SyncConflictService(
            () => CreateContext(dbPathA),
            new FixedDeviceId(devIdA));

        var res = await conflictService.ResolveKeepBothAsync(conflictId);
        Assert.True(res.Success, res.ErrorMessage);

        Guid keepBothCopySyncId;
        using (var dbA = CreateContext(dbPathA))
        {
            var notes = dbA.Notes.ToList();
            Assert.Equal(3, notes.Count); // 2 protected notes (local + keepboth copy) + 1 plain

            var keepBothCopy = notes.Single(n => n.IsProtected && n.SyncId != originalSyncId);
            keepBothCopySyncId = keepBothCopy.SyncId;
            Assert.Equal(originalSyncId, keepBothCopy.ProtectedOriginalSyncId);

            var plain = notes.Single(n => !n.IsProtected);
            Assert.Null(plain.ProtectedOriginalSyncId);
        }

        // 4. Экспортируем пакет из DB A
        var exporterA = new SyncPackageExporter(crypto, new FixedDeviceId(devIdA));
        SyncExportResult exportResultA;
        using (var dbA = CreateContext(dbPathA))
        {
            exportResultA = await exporterA.ExportPackageAsync(dbA, syncPassword, devIdA);
        }
        Assert.True(exportResultA.Success, string.Join("; ", exportResultA.Errors));
        Assert.NotNull(exportResultA.PackageJson);

        // Проверяем DTO и сырой JSON:
        // - У KeepBoth-копии ProtectedOriginalSyncId передан и равен originalSyncId
        // - У незащищённой заметки ProtectedOriginalSyncId == null и отсутствует в JSON
        var payloadA = exporterA.BuildDeterministicPayload(CreateContext(dbPathA), devIdA);
        var copyDto = payloadA.Notes.Single(n => n.SyncId == keepBothCopySyncId);
        Assert.Equal(originalSyncId, copyDto.ProtectedOriginalSyncId);

        var plainDto = payloadA.Notes.Single(n => n.SyncId == plainNoteSyncId);
        Assert.Null(plainDto.ProtectedOriginalSyncId);

        // Проверяем сериализованный JSON полезной нагрузки (payload)
        string payloadJson = JsonSerializer.Serialize(payloadA, SyncPackageExporter.DeterministicJsonOptions);
        using (var doc = JsonDocument.Parse(payloadJson))
        {
            var notesElem = doc.RootElement.GetProperty("notes");
            bool foundPlainWithoutProp = false;
            bool foundCopy = false;

            foreach (var elem in notesElem.EnumerateArray())
            {
                var sid = elem.GetProperty("syncId").GetString();
                if (sid == plainNoteSyncId.ToString())
                {
                    Assert.False(elem.TryGetProperty("protectedOriginalSyncId", out _));
                    foundPlainWithoutProp = true;
                }
                else if (sid == keepBothCopySyncId.ToString())
                {
                    Assert.True(elem.TryGetProperty("protectedOriginalSyncId", out var prop));
                    Assert.Equal(originalSyncId.ToString(), prop.GetString());
                    foundCopy = true;
                }
            }
            Assert.True(foundPlainWithoutProp);
            Assert.True(foundCopy);
        }

        // 5. Импортируем пакет в новую базу Device B
        var importerB = new SyncPackageImporter(crypto);
        SyncImportResult importResultB;
        using (var dbB = CreateContext(dbPathB))
        {
            importResultB = await importerB.ImportPackageAsync(dbB, exportResultA.PackageJson!, syncPassword);
        }
        Assert.True(importResultB.Success, string.Join("; ", importResultB.Errors));
        Assert.Equal(3, importResultB.Notes.Created);

        // 6. Проверяем состояние в DB B и расшифровываем KeepBoth копию
        int importedCopyId;
        using (var dbB = CreateContext(dbPathB))
        {
            var importedCopy = dbB.Notes.Single(n => n.SyncId == keepBothCopySyncId);
            importedCopyId = importedCopy.Id;
            Assert.True(importedCopy.IsProtected);
            Assert.Equal(originalSyncId, importedCopy.ProtectedOriginalSyncId);
            Assert.Equal(string.Empty, importedCopy.Text);

            // Разблокировка паролем и проверка исходного текста!
            var payload = protectionB.UnlockNote(dbB, importedCopy.Id, notePassword);
            Assert.Equal("Удалённая конфликтующая версия", payload.Text);
        }

        // 7. Проверяем no-op экспорт после синхронизации на Device B
        using (var dbB = CreateContext(dbPathB))
        {
            bool hasPendingMods = SyncSnapshotHelper.HasPendingLocalModifications(dbB, devIdB);
            Assert.False(hasPendingMods);

            var exporterB = new SyncPackageExporter(crypto, new FixedDeviceId(devIdB));
            var payloadB = exporterB.BuildDeterministicPayload(dbB, devIdB);

            // Все ContentHash совпадают с SyncEntityState - ни одна ревизия не изменилась!
            var statesB = dbB.SyncEntityStates.ToDictionary(s => s.SyncId);
            foreach (var note in payloadB.Notes)
            {
                Assert.True(statesB.TryGetValue(note.SyncId, out var st));
                Assert.Equal(st.RevisionId, note.RevisionId);
            }
        }

        // 8. Перешифрование содержимого под текущий SyncId (после редактирования) должно очистить ProtectedOriginalSyncId
        using (var dbB = CreateContext(dbPathB))
        {
            var importedCopy = dbB.Notes.Single(n => n.Id == importedCopyId);
            // Заметка уже разблокирована в protectionB
            protectionB.ApplyUnlockedEdits(dbB, importedCopy, "Отредактированный после KeepBoth текст", null, null, null);
            dbB.SaveChanges();

            // ProtectedOriginalSyncId должно быть очищено (null)
            Assert.Null(importedCopy.ProtectedOriginalSyncId);
        }

        // Проверяем, что теперь заметка разблокируется уже без ProtectedOriginalSyncId,
        // а AAD привязан к текущему SyncId
        var freshProtectionB = new NoteProtectionService(attachmentStorage: storageB);
        using (var dbB = CreateContext(dbPathB))
        {
            var unlockedEdited = freshProtectionB.UnlockNote(dbB, importedCopyId, notePassword);
            Assert.Equal("Отредактированный после KeepBoth текст", unlockedEdited.Text);

            var noteInDb = dbB.Notes.Find(importedCopyId);
            Assert.Null(noteInDb!.ProtectedOriginalSyncId);
        }
    }

    [Fact]
    public void MainViewModel_SaveProtectedNote_AttachmentCleanupOnFailure_AndByteForByteSuccess()
    {
        string dbPath = CreateTempDb();
        string storageDir = MakeTempDir();
        string settingsPath = Path.Combine(MakeTempDir(), "settings.json");
        var baseStorage = new AttachmentStorageService(storageDir);
        var delegatingStorage = new DelegatingAttachmentStorage(baseStorage);
        var interceptor = new FaultInjectionInterceptor();

        InitDb(dbPath);

        var protection = new NoteProtectionService(attachmentStorage: delegatingStorage);
        int noteId;
        int oldAttId;
        byte[] oldBytes = Encoding.UTF8.GetBytes("Старый документ до попыток сохранения");
        string oldFileName = "old_doc.txt";
        string oldFullPath;

        using (var db = CreateContext(dbPath))
        {
            var note = new Note { Text = "Исходная защищённая заметка" };
            db.Notes.Add(note);
            db.SaveChanges();
            noteId = note.Id;

            Assert.True(protection.ProtectNote(db, noteId, "pass123").Success);
            var att = AddAttachment(db, delegatingStorage, noteId, oldBytes, oldFileName);
            oldAttId = att.Id;
            var created = new List<string>();
            var oldToDel = new List<string>();
            protection.EncryptAttachment(db, noteId, att, oldFileName, delegatingStorage.GetFullPath(att.RelativePath), created, oldToDel);
            db.SaveChanges();
            oldFullPath = delegatingStorage.GetFullPath(att.RelativePath);
        }

        Assert.True(File.Exists(oldFullPath));
        Assert.Single(Directory.GetFiles(storageDir, "*.qnat", SearchOption.AllDirectories));

        using var mainVm = CreateMainViewModel(
            () => CreateContext(dbPath, interceptor),
            delegatingStorage,
            protection,
            settingsPath);

        mainVm.RequestProtectedNotePassword = _ => "pass123";
        mainVm.AlertHandler = (_, _, _) => { };
        mainVm.NotificationHandler = (_, _) => { };

        NoteEditorViewModel? capturedEditor = null;
        mainVm.RequestOpenNoteEditor += editor =>
        {
            capturedEditor = editor;
            return true;
        };

        // -------------------------------------------------------------
        // Сценарий 1: Сбой writeDb.SaveChanges()
        // -------------------------------------------------------------
        mainVm.RefreshNotes();
        var card = mainVm.Notes.Single(n => n.Id == noteId);
        mainVm.EditNote(card);
        Assert.NotNull(capturedEditor);

        byte[] failedBytes1 = Encoding.UTF8.GetBytes("Файл для сбоя SaveChanges");
        Assert.True(capturedEditor.AddAttachmentFromBytes(failedBytes1, "failed1.txt", out _));

        interceptor.FailOnSaveChanges = true;
        interceptor.FailOnCommit = false;

        Assert.Throws<DbUpdateException>(() => capturedEditor.Commit!.Invoke());

        // Проверяем:
        // 1. Никаких новых .qnat не осталось на диске
        var qnatFilesAfterSaveFailure = Directory.GetFiles(storageDir, "*.qnat", SearchOption.AllDirectories);
        Assert.Single(qnatFilesAfterSaveFailure);
        Assert.Equal(Path.GetFullPath(oldFullPath), Path.GetFullPath(qnatFilesAfterSaveFailure[0]));

        // 2. Старый файл существует и его зашифрованные байты сохранны
        Assert.True(File.Exists(oldFullPath));

        // 3. Состояние БД откатилось: в базе ровно 1 исходное вложение
        using (var dbCheck = CreateContext(dbPath))
        {
            var noteFromDb = dbCheck.Notes.Include(n => n.Attachments).Single(n => n.Id == noteId);
            Assert.Single(noteFromDb.Attachments);
            Assert.Equal(oldAttId, noteFromDb.Attachments.First().Id);
        }

        // -------------------------------------------------------------
        // Сценарий 2: Сбой tx.Commit()
        // -------------------------------------------------------------
        interceptor.FailOnSaveChanges = false;
        interceptor.FailOnCommit = false;

        capturedEditor = null;
        mainVm.EditNote(card);
        Assert.NotNull(capturedEditor);

        byte[] failedBytes2 = Encoding.UTF8.GetBytes("Файл для сбоя Commit");
        Assert.True(capturedEditor.AddAttachmentFromBytes(failedBytes2, "failed2.txt", out _));

        interceptor.FailOnCommit = true;

        Assert.Throws<InvalidOperationException>(() => capturedEditor.Commit!.Invoke());

        // Проверяем:
        // 1. Никаких новых .qnat не осталось на диске
        var qnatFilesAfterCommitFailure = Directory.GetFiles(storageDir, "*.qnat", SearchOption.AllDirectories);
        Assert.Single(qnatFilesAfterCommitFailure);
        Assert.Equal(Path.GetFullPath(oldFullPath), Path.GetFullPath(qnatFilesAfterCommitFailure[0]));

        // 2. Старый файл сохранен
        Assert.True(File.Exists(oldFullPath));

        // 3. Состояние БД откатилось: в базе ровно 1 исходное вложение
        using (var dbCheck = CreateContext(dbPath))
        {
            var noteFromDb = dbCheck.Notes.Include(n => n.Attachments).Single(n => n.Id == noteId);
            Assert.Single(noteFromDb.Attachments);
            Assert.Equal(oldAttId, noteFromDb.Attachments.First().Id);
        }

        // -------------------------------------------------------------
        // Сценарий 3: Успешное сохранение и открытие нового вложения байт-в-байт
        // -------------------------------------------------------------
        interceptor.FailOnSaveChanges = false;
        interceptor.FailOnCommit = false;

        capturedEditor = null;
        mainVm.EditNote(card);
        Assert.NotNull(capturedEditor);

        byte[] successBytes = Encoding.UTF8.GetBytes("Успешно прикреплённый секретный файл 12345");
        string successName = "success_payload.pdf";
        Assert.True(capturedEditor.AddAttachmentFromBytes(successBytes, successName, out _));

        capturedEditor.Commit!.Invoke();

        // Проверяем:
        // 1. На диске теперь 2 .qnat файла
        var qnatFilesSuccess = Directory.GetFiles(storageDir, "*.qnat", SearchOption.AllDirectories);
        Assert.Equal(2, qnatFilesSuccess.Length);

        // 2. В БД теперь 2 вложения
        using (var dbCheck = CreateContext(dbPath))
        {
            var noteFromDb = dbCheck.Notes.Include(n => n.Attachments).Single(n => n.Id == noteId);
            Assert.Equal(2, noteFromDb.Attachments.Count);

            var newAtt = noteFromDb.Attachments.First(a => a.Id != oldAttId);
            Assert.True(newAtt.IsProtected);

            // 3. Расшифровываем имя и содержимое нового вложения байт-в-байт через NoteProtectionService
            string decryptedName = protection.DecryptAttachmentName(dbCheck, noteId, newAtt);
            Assert.Equal(successName, decryptedName);

            string tempFile = protection.DecryptAttachmentToTempFile(dbCheck, noteId, newAtt);
            try
            {
                byte[] decryptedBytes = File.ReadAllBytes(tempFile);
                Assert.Equal(successBytes, decryptedBytes);
            }
            finally
            {
                ProtectedAttachmentFile.TryDeleteTempFile(tempFile);
            }

            // Старый файл также открывается байт-в-байт
            var oldAtt = noteFromDb.Attachments.First(a => a.Id == oldAttId);
            string oldTemp = protection.DecryptAttachmentToTempFile(dbCheck, noteId, oldAtt);
            try
            {
                Assert.Equal(oldBytes, File.ReadAllBytes(oldTemp));
            }
            finally
            {
                ProtectedAttachmentFile.TryDeleteTempFile(oldTemp);
            }
        }
    }

    [Fact]
    public void MainViewModel_SaveProtectedNote_PostCommitCleanupFailure_DoesNotFailSave()
    {
        string dbPath = CreateTempDb();
        string storageDir = MakeTempDir();
        string settingsPath = Path.Combine(MakeTempDir(), "settings.json");
        var baseStorage = new AttachmentStorageService(storageDir);
        var delegatingStorage = new DelegatingAttachmentStorage(baseStorage);

        InitDb(dbPath);

        var protection = new NoteProtectionService(attachmentStorage: delegatingStorage);
        int noteId;
        byte[] oldBytes = Encoding.UTF8.GetBytes("Документ, который будет удалён в редакторе");
        string oldFileName = "delete_me.txt";

        using (var db = CreateContext(dbPath))
        {
            var note = new Note { Text = "Заметка для проверки post-commit cleanup" };
            db.Notes.Add(note);
            db.SaveChanges();
            noteId = note.Id;

            Assert.True(protection.ProtectNote(db, noteId, "pass123").Success);
            var att = AddAttachment(db, delegatingStorage, noteId, oldBytes, oldFileName);
            var created = new List<string>();
            var oldToDel = new List<string>();
            protection.EncryptAttachment(db, noteId, att, oldFileName, delegatingStorage.GetFullPath(att.RelativePath), created, oldToDel);
            db.SaveChanges();
        }

        using var mainVm = CreateMainViewModel(
            () => CreateContext(dbPath),
            delegatingStorage,
            protection,
            settingsPath);

        mainVm.RequestProtectedNotePassword = _ => "pass123";
        mainVm.AlertHandler = (_, _, _) => { };
        mainVm.NotificationHandler = (_, _) => { };

        NoteEditorViewModel? capturedEditor = null;
        mainVm.RequestOpenNoteEditor += editor =>
        {
            capturedEditor = editor;
            return true;
        };

        mainVm.RefreshNotes();
        var card = mainVm.Notes.Single(n => n.Id == noteId);
        mainVm.EditNote(card);
        Assert.NotNull(capturedEditor);

        // Помечаем вложение на удаление в редакторе
        Assert.Single(capturedEditor.Attachments);
        capturedEditor.Attachments[0].DeleteCommand.Execute(null);
        Assert.Empty(capturedEditor.Attachments);
        Assert.Single(capturedEditor.DeletedAttachments);

        // Имитируем сбой post-commit cleanup
        delegatingStorage.FailCleanup = true;

        // Сохранение НЕ должно выбросить исключение, так как post-commit ошибка перехватывается и логируется
        var ex = Record.Exception(() => capturedEditor.Commit!.Invoke());
        Assert.Null(ex);

        // В БД вложение должно быть удалено
        using (var dbCheck = CreateContext(dbPath))
        {
            var noteFromDb = dbCheck.Notes.Include(n => n.Attachments).Single(n => n.Id == noteId);
            Assert.Empty(noteFromDb.Attachments);
        }
    }

    // ------------------------------------------------------------------
    // Supporting test fakes
    // ------------------------------------------------------------------

    private sealed class FaultInjectionInterceptor : SaveChangesInterceptor, IDbTransactionInterceptor
    {
        public bool FailOnCommit { get; set; }
        public bool FailOnSaveChanges { get; set; }

        public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
        {
            if (FailOnSaveChanges)
            {
                throw new DbUpdateException("[Injected] Simulated SaveChanges failure", (Exception?)null);
            }
            return base.SavingChanges(eventData, result);
        }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (FailOnSaveChanges)
            {
                throw new DbUpdateException("[Injected] Simulated SaveChanges failure", (Exception?)null);
            }
            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }

        public InterceptionResult TransactionCommitting(
            DbTransaction transaction,
            TransactionEventData eventData,
            InterceptionResult result)
        {
            if (FailOnCommit)
            {
                throw new InvalidOperationException("[Injected] Simulated transaction commit failure");
            }
            return result;
        }

        public void TransactionCommitted(DbTransaction transaction, TransactionEndEventData eventData) { }
        public InterceptionResult TransactionRollingBack(DbTransaction transaction, TransactionEventData eventData, InterceptionResult result) => result;
        public void TransactionRolledBack(DbTransaction transaction, TransactionEndEventData eventData) { }

        public ValueTask<InterceptionResult> TransactionCommittingAsync(
            DbTransaction transaction,
            TransactionEventData eventData,
            InterceptionResult result,
            CancellationToken cancellationToken = default)
        {
            if (FailOnCommit)
            {
                throw new InvalidOperationException("[Injected] Simulated transaction commit failure");
            }
            return ValueTask.FromResult(result);
        }

        public ValueTask TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask<InterceptionResult> TransactionRollingBackAsync(DbTransaction transaction, TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default) => ValueTask.FromResult(result);
        public ValueTask TransactionRolledBackAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }

    private sealed class InMemoryCredentialsStorage : IS3CredentialsStorage
    {
        private S3Credentials? _creds = new S3Credentials("key", "secret");
        public Task<S3Credentials?> LoadCredentialsAsync(CancellationToken ct = default) => Task.FromResult(_creds);
        public Task SaveCredentialsAsync(S3Credentials credentials, CancellationToken ct = default) { _creds = credentials; return Task.CompletedTask; }
        public Task DeleteCredentialsAsync(CancellationToken ct = default) { _creds = null; return Task.CompletedTask; }
        public bool HasCredentials() => _creds != null;
    }

    private sealed class DelegatingAttachmentStorage : IAttachmentStorageService
    {
        private readonly AttachmentStorageService _inner;
        public bool FailCleanup { get; set; }

        public DelegatingAttachmentStorage(AttachmentStorageService inner) => _inner = inner;

        public string BaseDirectory => _inner.BaseDirectory;
        public string AttachmentsDirectory => _inner.AttachmentsDirectory;
        public AttachmentSaveResult SaveAttachment(string sourceFilePath, long maxSizeBytes) => _inner.SaveAttachment(sourceFilePath, maxSizeBytes);
        public AttachmentSaveResult SaveFromBytes(byte[] content, string originalFileName, long maxSizeBytes) => _inner.SaveFromBytes(content, originalFileName, maxSizeBytes);
        public string GetFullPath(string relativePath) => _inner.GetFullPath(relativePath);
        public bool FileExists(string relativePath) => _inner.FileExists(relativePath);
        public bool DeleteManagedFileIfUnreferenced(QuickNotesDbContext db, string storedFileName) => _inner.DeleteManagedFileIfUnreferenced(db, storedFileName);

        public void CleanupUnreferencedFiles(QuickNotesDbContext db, IEnumerable<string> storedFileNames)
        {
            if (FailCleanup)
            {
                throw new IOException("[Injected] Simulated post-commit cleanup failure");
            }
            _inner.CleanupUnreferencedFiles(db, storedFileNames);
        }
    }

    private static MainViewModel CreateMainViewModel(
        Func<QuickNotesDbContext> contextFactory,
        IAttachmentStorageService storageService,
        INoteProtectionService protectionService,
        string settingsPath)
    {
        var tagRuleService = new TagRuleService();
        var tagDetectionService = new TagDetectionService(tagRuleService);
        var searchService = new SearchService();
        var hotkeyService = new GlobalHotkeyService((_, _, _, _) => true, (_, _) => true, IntPtr.Zero);
        var clipboardCapture = new ClipboardCaptureService();
        var trayIcon = new TrayIconService();
        var backupService = new BackupService();
        var settingsService = new SettingsService(settingsPath, _ => { });

        return MainViewModelTestComposition.Create(
            contextFactory,
            tagDetectionService,
            searchService,
            settingsService,
            hotkeyService,
            clipboardCapture,
            trayIcon,
            backupService,
            attachmentStorageService: storageService,
            noteProtectionService: protectionService,
            draftJournalService: NoOpDraftJournalService.Instance);
    }
}

