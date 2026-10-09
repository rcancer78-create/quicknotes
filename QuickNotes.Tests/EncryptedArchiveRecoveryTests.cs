using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using QuickNotes.App.Data;
using QuickNotes.App.Models;
using QuickNotes.App.Services;
using QuickNotes.App.Services.EncryptedArchive;
using QuickNotes.App.Services.NoteProtection;
using QuickNotes.App.Services.Sync;
using QuickNotes.App.ViewModels;
using Xunit;

namespace QuickNotes.Tests;

[Collection(LiveProfileSnapshotCollection.Name)]
[TestCategory(TestCategories.Integration)]
public sealed class EncryptedArchiveRecoveryTests
{
    private const int TestKdfN = 10;
    private const int OtherKdfN = 50;

    [Fact]
    public void ArS1_QnarFile_ContainsNoPlaintextNoteDekPasswordsOrAttachmentNames()
    {
        using var fx = Fixture.Create();
        const string token = "PLAINTEXT_NOTE_TOKEN_AR_S1_UNIQUE_7f3c91e2d4ab";
        const string attachmentName = "UNIQUE_ATTACHMENT_NAME_AR_S1_photo_secret.png";
        fx.AddOpenNote(token);
        fx.AddAttachmentToLastNote(Encoding.UTF8.GetBytes("open-bytes"), attachmentName);
        var created = fx.CreateArchive();

        byte[] file = File.ReadAllBytes(created.ArchivePath);
        AssertNoUtf8(file, token);
        AssertNoUtf8(file, fx.ArchivePassword);
        AssertNoUtf8(file, attachmentName);
        AssertNoUtf8(file, created.RecoveryKeyFormatted);
    }

    [Fact]
    public void ArS2_Create_DoesNotDecryptProtectedContent_AndCopiesCiphertextAsOnDisk()
    {
        using var fx = Fixture.Create();
        fx.AddProtectedNoteWithAttachment("note-password-ars2", "SECRET_PROTECTED_TEXT_ARS2", Encoding.UTF8.GetBytes("prot-bytes"), "hidden.bin");
        int decryptBefore = fx.CryptoSpy.DecryptCalls + fx.CryptoSpy.DecryptWithKeyCalls;
        var created = fx.CreateArchive();
        Assert.Equal(decryptBefore, fx.CryptoSpy.DecryptCalls + fx.CryptoSpy.DecryptWithKeyCalls);

        byte[] onDisk = File.ReadAllBytes(fx.ProtectedAttachmentFullPath!);
        var dry = fx.DryRunPassword();
        QnapEntry packed = dry.Entries.Single(e => e.Path.StartsWith("attachments/", StringComparison.Ordinal));
        Assert.Equal(onDisk, packed.Content);
        Assert.StartsWith("QNAT", Encoding.ASCII.GetString(packed.Content.AsSpan(0, 4)));
        _ = created;
    }

    [Fact]
    public void ArS3_Create_LeavesNoStagingSnapshotBesideArchiveOrInTemp()
    {
        using var fx = Fixture.Create();
        fx.AddOpenNote("ars3-note");
        string archiveDir = Path.GetDirectoryName(fx.ArchivePath)!;
        string[] tempBefore = Directory.GetFileSystemEntries(Path.GetTempPath(), "*qnar*", SearchOption.TopDirectoryOnly);
        var created = fx.CreateArchive();
        Assert.True(File.Exists(created.ArchivePath));
        Assert.Empty(Directory.GetDirectories(archiveDir, ".qnar-staging-*"));
        Assert.Empty(Directory.GetFiles(archiveDir, "quicknotes_backup_*.db"));
        Assert.Equal(new[] { created.ArchivePath }, Directory.GetFiles(archiveDir));
        string[] tempAfter = Directory.GetFileSystemEntries(Path.GetTempPath(), "*qnar*", SearchOption.TopDirectoryOnly);
        Assert.Equal(tempBefore.OrderBy(x => x, StringComparer.OrdinalIgnoreCase), tempAfter.OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public void ArS4_ArchivePasswordOpensPayload_NotePasswordDoesNot()
    {
        using var fx = Fixture.Create();
        const string notePassword = "note-password-NOT-archive";
        fx.AddProtectedNote("SECRET_NOTE_ARS4", notePassword);
        var created = fx.CreateArchive();
        EncryptedArchiveDryRunResult opened = fx.Service.DryRun(new EncryptedArchiveDryRunRequest
        {
            ArchivePath = created.ArchivePath,
            ArchivePassword = fx.ArchivePassword
        });
        Assert.Equal(created.ArchiveId, opened.ArchiveId);

        Assert.Throws<EncryptedArchiveSecurityException>(() => fx.Service.DryRun(new EncryptedArchiveDryRunRequest
        {
            ArchivePath = created.ArchivePath,
            ArchivePassword = notePassword
        }));

        using var db = SqliteTestUtil.CreateContext(fx.DbPath);
        var protectedNote = db.Notes.Single(n => n.IsProtected);
        var crypto = new NoteCryptoService(iterations: 1000);
        Assert.Throws<NoteProtectionSecurityException>(() => crypto.Decrypt(
            fx.ArchivePassword,
            Convert.FromBase64String(protectedNote.ProtectedCiphertextBase64!),
            Convert.FromBase64String(protectedNote.ProtectedSaltBase64!),
            Convert.FromBase64String(protectedNote.ProtectedNonceBase64!),
            Convert.FromBase64String(protectedNote.ProtectedTagBase64!),
            protectedNote.SyncId,
            NoteProtectedObjectType.NoteEnvelope,
            protectedNote.ProtectedKdfIterations));
    }

    [Fact]
    public void ArS5_RecoveryKeyOpensSamePayload_AndDoesNotDecryptNoteEnvelope()
    {
        using var fx = Fixture.Create();
        const string notePassword = "note-pw-ars5";
        fx.AddProtectedNote("SECRET_NOTE_ARS5", notePassword);
        var created = fx.CreateArchive();
        Assert.NotEqual(fx.ArchivePassword, created.RecoveryKeyFormatted);

        EncryptedArchiveDryRunResult viaPassword = fx.DryRunPassword();
        EncryptedArchiveDryRunResult viaRecovery = fx.Service.DryRun(new EncryptedArchiveDryRunRequest
        {
            ArchivePath = created.ArchivePath,
            RecoveryKeyFormatted = created.RecoveryKeyFormatted
        });
        Assert.Equal(viaPassword.ArchiveId, viaRecovery.ArchiveId);
        Assert.Equal(viaPassword.SnapshotSqliteBytes, viaRecovery.SnapshotSqliteBytes);

        using var db = SqliteTestUtil.CreateContext(fx.DbPath);
        var protectedNote = db.Notes.Single(n => n.IsProtected);
        var crypto = new NoteCryptoService(iterations: 1000);
        byte[] rkMaterial = RecoveryKeyEncoding.Parse(created.RecoveryKeyFormatted);
        string rkAsPassword = Convert.ToHexString(rkMaterial);
        Assert.Throws<NoteProtectionSecurityException>(() => crypto.Decrypt(
            rkAsPassword,
            Convert.FromBase64String(protectedNote.ProtectedCiphertextBase64!),
            Convert.FromBase64String(protectedNote.ProtectedSaltBase64!),
            Convert.FromBase64String(protectedNote.ProtectedNonceBase64!),
            Convert.FromBase64String(protectedNote.ProtectedTagBase64!),
            protectedNote.SyncId,
            NoteProtectedObjectType.NoteEnvelope,
            protectedNote.ProtectedKdfIterations));
        Assert.Throws<NoteProtectionSecurityException>(() => crypto.Decrypt(
            created.RecoveryKeyFormatted,
            Convert.FromBase64String(protectedNote.ProtectedCiphertextBase64!),
            Convert.FromBase64String(protectedNote.ProtectedSaltBase64!),
            Convert.FromBase64String(protectedNote.ProtectedNonceBase64!),
            Convert.FromBase64String(protectedNote.ProtectedTagBase64!),
            protectedNote.SyncId,
            NoteProtectedObjectType.NoteEnvelope,
            protectedNote.ProtectedKdfIterations));
    }

    [Fact]
    public void ArS6_WrongPasswordAndWrongRecoveryKey_SameFailureClass_DestinationUntouched()
    {
        using var fx = Fixture.Create();
        fx.AddOpenNote("ars6");
        var created = fx.CreateArchive();
        string dest = Path.Combine(fx.Root, "restore-target");
        Directory.CreateDirectory(dest);
        File.WriteAllText(Path.Combine(dest, "keep.txt"), "keep");
        string[] before = Directory.GetFileSystemEntries(dest, "*", SearchOption.AllDirectories);

        var wrongPassword = Assert.Throws<EncryptedArchiveSecurityException>(() => fx.Service.DryRun(new EncryptedArchiveDryRunRequest
        {
            ArchivePath = created.ArchivePath,
            ArchivePassword = "wrong-archive-password",
            RestoreDestinationDirectory = dest
        }));
        var wrongRk = Assert.Throws<EncryptedArchiveSecurityException>(() => fx.Service.DryRun(new EncryptedArchiveDryRunRequest
        {
            ArchivePath = created.ArchivePath,
            RecoveryKeyFormatted = RecoveryKeyEncoding.Format(RandomNumberGenerator.GetBytes(32)),
            RestoreDestinationDirectory = dest
        }));
        Assert.Equal(wrongPassword.GetType(), wrongRk.GetType());
        Assert.Equal(EncryptedArchiveSecurityException.GenericUserMessage, wrongPassword.Message);
        Assert.Equal(wrongPassword.Message, wrongRk.Message);
        Assert.Equal(before, Directory.GetFileSystemEntries(dest, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public void ArS7_MutationsOfMagicHeaderWrapCiphertextTagAndTail_FailClosed()
    {
        using var fx = Fixture.Create();
        fx.AddOpenNote("ars7");
        var created = fx.CreateArchive();
        byte[] original = File.ReadAllBytes(created.ArchivePath);

        void AssertRejects(byte[] mutated)
        {
            string path = Path.Combine(fx.Root, "mut", Guid.NewGuid().ToString("N") + ".qnar");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, mutated);
            Assert.ThrowsAny<EncryptedArchiveException>(() => fx.Service.DryRun(new EncryptedArchiveDryRunRequest
            {
                ArchivePath = path,
                ArchivePassword = fx.ArchivePassword
            }));
        }

        byte[] magic = (byte[])original.Clone();
        magic[0] ^= 0xFF;
        AssertRejects(magic);

        byte[] header = (byte[])original.Clone();
        header[8] ^= 0x01;
        AssertRejects(header);

        byte[] wrap = (byte[])original.Clone();
        ushort headerLen = BinaryPrimitives.ReadUInt16LittleEndian(original.AsSpan(4));
        wrap[6 + headerLen] ^= 0x01;
        AssertRejects(wrap);

        byte[] ciphertext = (byte[])original.Clone();
        ciphertext[^20] ^= 0x01;
        AssertRejects(ciphertext);

        byte[] tag = (byte[])original.Clone();
        tag[^1] ^= 0x01;
        AssertRejects(tag);

        byte[] trailing = new byte[original.Length + 1];
        Buffer.BlockCopy(original, 0, trailing, 0, original.Length);
        trailing[^1] = 0x07;
        AssertRejects(trailing);

        byte[] truncated = original.AsSpan(0, original.Length - 1).ToArray();
        AssertRejects(truncated);
    }

    [Fact]
    public void ArS8_KdfIterationsAboveMax_RejectedBeforePbkdf2()
    {
        using var fx = Fixture.Create();
        fx.AddOpenNote("ars8");
        var created = fx.CreateArchive();
        byte[] original = File.ReadAllBytes(created.ArchivePath);
        EncryptedArchiveFramedFile framed = EncryptedArchiveFraming.ReadBoundedAllowingKdfForTests(original);
        string[] parts = framed.CanonicalHeaderText.Split('|');
        parts[6] = (EncryptedArchiveConstants.MaxKdfIterations + 1).ToString();
        byte[] mutated = EncryptedArchiveFraming.ReplaceCanonicalHeader(original, string.Join('|', parts));
        string path = Path.Combine(fx.Root, "high-kdf.qnar");
        File.WriteAllBytes(path, mutated);

        EncryptedArchiveCrypto.ResetPbkdf2Invocations();
        var sw = Stopwatch.StartNew();
        Assert.Throws<EncryptedArchiveFormatException>(() => fx.Service.DryRun(new EncryptedArchiveDryRunRequest
        {
            ArchivePath = path,
            ArchivePassword = fx.ArchivePassword
        }));
        sw.Stop();
        Assert.Equal(0, EncryptedArchiveCrypto.Pbkdf2Invocations);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), $"KDF limit check took {sw.Elapsed}");
    }

    [Fact]
    public void ArS9_HeaderIterationsN_AreReadByClientWithDifferentPreferredM()
    {
        using var fx = Fixture.Create(createIterations: TestKdfN);
        fx.AddOpenNote("ars9");
        var created = fx.CreateArchive();
        Assert.Equal(TestKdfN, created.Pbkdf2Iterations);

        var reader = new EncryptedArchiveService(EncryptedArchiveKdfLimits.ForTests());
        EncryptedArchiveDryRunResult result = reader.DryRun(new EncryptedArchiveDryRunRequest
        {
            ArchivePath = created.ArchivePath,
            ArchivePassword = fx.ArchivePassword
        });
        Assert.Equal(created.ArchiveId, result.ArchiveId);

        using var fx2 = Fixture.Create(createIterations: OtherKdfN);
        fx2.AddOpenNote("ars9-other");
        var created2 = fx2.CreateArchive();
        EncryptedArchiveDryRunResult result2 = reader.DryRun(new EncryptedArchiveDryRunRequest
        {
            ArchivePath = created2.ArchivePath,
            ArchivePassword = fx2.ArchivePassword
        });
        Assert.Equal(created2.ArchiveId, result2.ArchiveId);
        Assert.NotEqual(created.Pbkdf2Iterations, created2.Pbkdf2Iterations);
    }

    [Fact]
    public void ArS10_UnknownKdfAlgorithmAndFormatVersion_FailClosedWithoutPbkdf2()
    {
        using var fx = Fixture.Create();
        fx.AddOpenNote("ars10");
        var created = fx.CreateArchive();
        byte[] original = File.ReadAllBytes(created.ArchivePath);
        EncryptedArchiveFramedFile framed = EncryptedArchiveFraming.ReadBoundedAllowingKdfForTests(original);
        string[] parts = framed.CanonicalHeaderText.Split('|');

        parts[4] = "Argon2id";
        File.WriteAllBytes(Path.Combine(fx.Root, "argon.qnar"), EncryptedArchiveFraming.ReplaceCanonicalHeader(original, string.Join('|', parts)));
        EncryptedArchiveCrypto.ResetPbkdf2Invocations();
        Assert.Throws<EncryptedArchiveFormatException>(() => fx.Service.DryRun(new EncryptedArchiveDryRunRequest
        {
            ArchivePath = Path.Combine(fx.Root, "argon.qnar"),
            ArchivePassword = fx.ArchivePassword
        }));
        Assert.Equal(0, EncryptedArchiveCrypto.Pbkdf2Invocations);

        parts = framed.CanonicalHeaderText.Split('|');
        parts[1] = "2";
        File.WriteAllBytes(Path.Combine(fx.Root, "v2.qnar"), EncryptedArchiveFraming.ReplaceCanonicalHeader(original, string.Join('|', parts)));
        EncryptedArchiveCrypto.ResetPbkdf2Invocations();
        Assert.Throws<EncryptedArchiveFormatException>(() => fx.Service.DryRun(new EncryptedArchiveDryRunRequest
        {
            ArchivePath = Path.Combine(fx.Root, "v2.qnar"),
            ArchivePassword = fx.ArchivePassword
        }));
        Assert.Equal(0, EncryptedArchiveCrypto.Pbkdf2Invocations);
    }

    [Fact]
    public void ArS11_HeaderFieldPermutationWithoutNewTag_Fails()
    {
        using var fx = Fixture.Create();
        fx.AddOpenNote("ars11");
        var created = fx.CreateArchive();
        byte[] original = File.ReadAllBytes(created.ArchivePath);
        EncryptedArchiveFramedFile framed = EncryptedArchiveFraming.ReadBoundedAllowingKdfForTests(original);
        string[] parts = framed.CanonicalHeaderText.Split('|');
        parts[3] = (long.Parse(parts[3]) + 1).ToString();
        byte[] mutated = EncryptedArchiveFraming.ReplaceCanonicalHeader(original, string.Join('|', parts));
        File.WriteAllBytes(Path.Combine(fx.Root, "aad.qnar"), mutated);
        Assert.Throws<EncryptedArchiveSecurityException>(() => fx.Service.DryRun(new EncryptedArchiveDryRunRequest
        {
            ArchivePath = Path.Combine(fx.Root, "aad.qnar"),
            ArchivePassword = fx.ArchivePassword
        }));
    }

    [Fact]
    public void ArS12_TraversalAbsoluteUncAndAdsPaths_AreRejectedWithoutWrites()
    {
        using var fx = Fixture.Create();
        string dest = Path.Combine(fx.Root, "extract-root");
        Directory.CreateDirectory(dest);
        File.WriteAllText(Path.Combine(dest, "sentinel.txt"), "sentinel");
        string[] before = Directory.GetFileSystemEntries(dest, "*", SearchOption.AllDirectories);

        string[] hostilePaths =
        {
            "../secret.bin",
            "attachments/../outside.bin",
            "attachments/..\\x.bin",
            "C:/Windows/notepad.exe",
            "//server/share/x.bin",
            "attachments/foo:ads.bin"
        };

        foreach (string hostile in hostilePaths)
        {
            byte[] archive = fx.SealHostileQnap(BuildMinimalHostileQnap(hostile, Encoding.UTF8.GetBytes("x")));
            string path = Path.Combine(fx.Root, Guid.NewGuid().ToString("N") + ".qnar");
            File.WriteAllBytes(path, archive);
            Assert.Throws<EncryptedArchiveValidationException>(() => fx.Service.DryRun(new EncryptedArchiveDryRunRequest
            {
                ArchivePath = path,
                ArchivePassword = fx.ArchivePassword,
                RestoreDestinationDirectory = dest
            }));
            Assert.Equal(before, Directory.GetFileSystemEntries(dest, "*", SearchOption.AllDirectories));
        }
    }

    [Fact]
    public void ArS13_ReparsePointOnRequiredAttachment_FailsCreate_AndDryRunDoesNotCreateReparse()
    {
        using var fx = Fixture.Create();
        fx.AddOpenNote("ars13");
        fx.AddAttachmentToLastNote(Encoding.UTF8.GetBytes("att"), "ok.bin");

        string outside = Path.Combine(fx.Root, "outside-target");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "leaked.txt"), "leaked");
        string junction = Path.Combine(fx.Storage.AttachmentsDirectory, "junction-out");
        bool createdJunction = TryCreateJunction(junction, outside);
        if (createdJunction)
        {
            Assert.Throws<EncryptedArchiveValidationException>(() => fx.CreateArchive());
        }
        else
        {
            string required = Directory.GetFiles(fx.Storage.AttachmentsDirectory).First();
            string backup = required + ".bak";
            File.Move(required, backup);
            try
            {
                File.CreateSymbolicLink(required, backup);
                if ((File.GetAttributes(required) & FileAttributes.ReparsePoint) != 0)
                {
                    Assert.Throws<EncryptedArchiveValidationException>(() => fx.CreateArchive());
                }
            }
            catch (UnauthorizedAccessException)
            {
                File.Move(backup, required);
                Assert.True(true, "Reparse creation is not permitted in this environment; directory scan still refuses reparse when present.");
            }
        }

        using var fx2 = Fixture.Create();
        fx2.AddOpenNote("ars13-dry");
        var created = fx2.CreateArchive();
        string dest = Path.Combine(fx2.Root, "empty-dest");
        fx2.Service.DryRun(new EncryptedArchiveDryRunRequest
        {
            ArchivePath = created.ArchivePath,
            ArchivePassword = fx2.ArchivePassword,
            RestoreDestinationDirectory = dest
        });
        Assert.False(Directory.Exists(dest));
    }

    [Fact]
    public void Restore_ReparseInExistingAncestor_IsRejectedWithoutWrites()
    {
        using var fx = Fixture.Create();
        fx.AddOpenNote("ancestor-reparse");
        fx.CreateArchive();

        string ordinary = Path.Combine(fx.Root, "ordinary-dest");
        EncryptedArchivePathRules.ThrowIfExistingAncestorChainHasReparse(ordinary);

        string realParent = Path.Combine(fx.Root, "real-parent");
        Directory.CreateDirectory(realParent);
        string linkParent = Path.Combine(fx.Root, "link-parent");
        string dest = Path.Combine(linkParent, "restore-dest");
        bool createdReparse = TryCreateDirectoryReparse(linkParent, realParent);
        if (createdReparse)
        {
            EncryptedArchiveValidationException ex = Assert.Throws<EncryptedArchiveValidationException>(
                () => fx.RestorePassword(dest));
            Assert.Contains("reparse", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.False(Directory.Exists(dest));
            Assert.False(Directory.Exists(Path.Combine(realParent, "restore-dest")));
            Assert.Empty(RestoreLeftovers(realParent));
            Assert.Empty(RestoreLeftovers(fx.Root));
            EncryptedArchiveValidationException helper = Assert.Throws<EncryptedArchiveValidationException>(
                () => EncryptedArchivePathRules.ThrowIfExistingAncestorChainHasReparse(dest));
            Assert.Contains("reparse", helper.Message, StringComparison.OrdinalIgnoreCase);
            return;
        }

        string? existingReparse = FindExistingWindowsReparseDirectory();
        if (existingReparse == null)
        {
            return;
        }

        string underReparse = Path.Combine(existingReparse, "qn-restore-child-" + Guid.NewGuid().ToString("N"));
        EncryptedArchiveValidationException existing = Assert.Throws<EncryptedArchiveValidationException>(
            () => EncryptedArchivePathRules.ThrowIfExistingAncestorChainHasReparse(underReparse));
        Assert.Contains("reparse", existing.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(underReparse));
    }

    [Fact]
    public void ArS14_OversizedDeclarations_RejectedWithoutAllocatingDestination()
    {
        Assert.True(EncryptedArchiveConstants.InMemoryBudgetBytes < int.MaxValue);
        Assert.Equal(EncryptedArchiveConstants.InMemoryBudgetBytes, EncryptedArchiveConstants.MaxArchiveFileBytes);
        Assert.Equal(EncryptedArchiveConstants.InMemoryBudgetBytes, EncryptedArchiveConstants.MaxPayloadBytes);
        Assert.Equal(EncryptedArchiveConstants.InMemoryBudgetBytes, EncryptedArchiveConstants.MaxSingleFileBytes);

        using var fx = Fixture.Create();
        fx.AddOpenNote("ars14");
        var created = fx.CreateArchive();
        string dest = Path.Combine(fx.Root, "oversized-dest");
        Directory.CreateDirectory(dest);

        byte[] original = File.ReadAllBytes(created.ArchivePath);
        ushort headerLen = BinaryPrimitives.ReadUInt16LittleEndian(original.AsSpan(4));
        int payloadLenOff = 6 + headerLen + (EncryptedArchiveConstants.WrapSlotByteSize * 2);

        void RejectDeclaredPayloadLength(ulong declared, string fileName)
        {
            byte[] mutated = (byte[])original.Clone();
            BinaryPrimitives.WriteUInt64LittleEndian(mutated.AsSpan(payloadLenOff), declared);
            string path = Path.Combine(fx.Root, fileName);
            File.WriteAllBytes(path, mutated);
            Assert.Throws<EncryptedArchiveFormatException>(() => fx.Service.DryRun(new EncryptedArchiveDryRunRequest
            {
                ArchivePath = path,
                ArchivePassword = fx.ArchivePassword,
                RestoreDestinationDirectory = dest
            }));
        }

        RejectDeclaredPayloadLength((ulong)EncryptedArchiveConstants.MaxPayloadBytes + 1, "huge-payload.qnar");
        RejectDeclaredPayloadLength((ulong)int.MaxValue, "int-max-payload.qnar");
        RejectDeclaredPayloadLength(8UL * 1024 * 1024 * 1024, "eight-gib-payload.qnar");

        byte[] hugeCount = fx.SealHostileQnap(EncryptedArchivePayload.WriteHostile(
            EncryptedArchiveConstants.MaxEntryCount + 1u,
            Array.Empty<(string, byte[], ulong, byte[])>()));
        File.WriteAllBytes(Path.Combine(fx.Root, "huge-count.qnar"), hugeCount);
        Assert.Throws<EncryptedArchiveValidationException>(() => fx.Service.DryRun(new EncryptedArchiveDryRunRequest
        {
            ArchivePath = Path.Combine(fx.Root, "huge-count.qnar"),
            ArchivePassword = fx.ArchivePassword,
            RestoreDestinationDirectory = dest
        }));

        byte[] content = Encoding.UTF8.GetBytes("x");
        byte[] sha = SHA256.HashData(content);
        byte[] hugeFile = fx.SealHostileQnap(EncryptedArchivePayload.WriteHostile(
            1,
            new[] { (EncryptedArchiveConstants.SnapshotEntryPath, content, (ulong)EncryptedArchiveConstants.MaxSingleFileBytes + 1, sha) }));
        File.WriteAllBytes(Path.Combine(fx.Root, "huge-file.qnar"), hugeFile);
        Assert.Throws<EncryptedArchiveValidationException>(() => fx.Service.DryRun(new EncryptedArchiveDryRunRequest
        {
            ArchivePath = Path.Combine(fx.Root, "huge-file.qnar"),
            ArchivePassword = fx.ArchivePassword,
            RestoreDestinationDirectory = dest
        }));

        byte[] intMaxFile = fx.SealHostileQnap(EncryptedArchivePayload.WriteHostile(
            1,
            new[] { (EncryptedArchiveConstants.SnapshotEntryPath, content, (ulong)int.MaxValue, sha) }));
        File.WriteAllBytes(Path.Combine(fx.Root, "int-max-file.qnar"), intMaxFile);
        Assert.Throws<EncryptedArchiveValidationException>(() => fx.Service.DryRun(new EncryptedArchiveDryRunRequest
        {
            ArchivePath = Path.Combine(fx.Root, "int-max-file.qnar"),
            ArchivePassword = fx.ArchivePassword,
            RestoreDestinationDirectory = dest
        }));

        string oversizedOnDisk = Path.Combine(fx.Root, "length-override.qnar");
        File.WriteAllBytes(oversizedOnDisk, original);
        EncryptedArchiveInMemoryLimits.GetLengthOverrideForTests = path =>
            string.Equals(path, oversizedOnDisk, StringComparison.OrdinalIgnoreCase)
                ? EncryptedArchiveConstants.MaxArchiveFileBytes + 1L
                : new FileInfo(path).Length;
        try
        {
            Assert.Throws<EncryptedArchiveFormatException>(() => fx.Service.DryRun(new EncryptedArchiveDryRunRequest
            {
                ArchivePath = oversizedOnDisk,
                ArchivePassword = fx.ArchivePassword,
                RestoreDestinationDirectory = dest
            }));
        }
        finally
        {
            EncryptedArchiveInMemoryLimits.GetLengthOverrideForTests = null;
        }

        fx.AddAttachmentToLastNote(Encoding.UTF8.GetBytes("att"), "ars14.bin");
        string attachmentPath = Directory.GetFiles(fx.Storage.AttachmentsDirectory).Single();
        EncryptedArchiveInMemoryLimits.GetLengthOverrideForTests = path =>
            string.Equals(path, attachmentPath, StringComparison.OrdinalIgnoreCase)
                ? EncryptedArchiveConstants.MaxSingleFileBytes + 1L
                : new FileInfo(path).Length;
        try
        {
            Assert.Throws<EncryptedArchiveValidationException>(() => fx.Service.Create(new EncryptedArchiveCreateRequest
            {
                DatabasePath = fx.DbPath,
                AttachmentsDirectory = fx.Storage.AttachmentsDirectory,
                DestinationArchivePath = Path.Combine(fx.Root, "out-oversize", "too-big.qnar"),
                ArchivePassword = fx.ArchivePassword,
                Pbkdf2Iterations = fx.CreateIterations
            }));
            Assert.False(File.Exists(Path.Combine(fx.Root, "out-oversize", "too-big.qnar")));
        }
        finally
        {
            EncryptedArchiveInMemoryLimits.GetLengthOverrideForTests = null;
        }

        Assert.Empty(Directory.GetFiles(dest));
    }

    [Fact]
    public void ArS17_Create_DoesNotOverwriteExistingArchive()
    {
        using var fx = Fixture.Create();
        fx.AddOpenNote("ars17-existing");
        var created = fx.CreateArchive();
        byte[] original = File.ReadAllBytes(created.ArchivePath);
        var again = Assert.Throws<EncryptedArchiveValidationException>(() => fx.CreateArchive());
        Assert.Contains("уже существует", again.Message, StringComparison.Ordinal);
        Assert.Equal(original, File.ReadAllBytes(created.ArchivePath));
    }

    [Fact]
    public void ArS17_InjectedWriteAndBeforePublishFailures_LeaveFinalMissing_AndDeleteTemp()
    {
        using var fx = Fixture.Create();
        fx.AddOpenNote("ars17-write");
        string archiveDir = Path.GetDirectoryName(fx.ArchivePath)!;

        fx.Service.TestBeforeTempWrite = _ => throw new IOException("injected write failure");
        Assert.Throws<IOException>(() => fx.CreateArchive());
        Assert.False(File.Exists(fx.ArchivePath));
        Assert.Empty(Directory.GetFiles(archiveDir).Where(p => p.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)));
        fx.Service.TestBeforeTempWrite = null;

        fx.Service.TestBeforePublishMove = tempPath =>
        {
            Assert.True(File.Exists(tempPath));
            throw new IOException("injected before-publish failure");
        };
        Assert.Throws<IOException>(() => fx.CreateArchive());
        Assert.False(File.Exists(fx.ArchivePath));
        Assert.Empty(Directory.GetFiles(archiveDir).Where(p => p.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)));
        fx.Service.TestBeforePublishMove = null;

        var created = fx.CreateArchive();
        Assert.True(File.Exists(created.ArchivePath));
        Assert.Empty(Directory.GetFiles(archiveDir).Where(p => p.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)));
        Assert.Equal(new[] { created.ArchivePath }, Directory.GetFiles(archiveDir));
    }

    [Fact]
    public void ArS15_DryRunSuccessAndFailure_WriteNoNewFiles()
    {
        using var fx = Fixture.Create();
        fx.AddOpenNote("ars15");
        var created = fx.CreateArchive();
        string probe = Path.Combine(fx.Root, "dry-probe");
        Directory.CreateDirectory(probe);
        string[] before = SnapshotTree(probe);

        fx.Service.DryRun(new EncryptedArchiveDryRunRequest
        {
            ArchivePath = created.ArchivePath,
            ArchivePassword = fx.ArchivePassword,
            RestoreDestinationDirectory = probe
        });
        Assert.Equal(before, SnapshotTree(probe));

        Assert.Throws<EncryptedArchiveSecurityException>(() => fx.Service.DryRun(new EncryptedArchiveDryRunRequest
        {
            ArchivePath = created.ArchivePath,
            ArchivePassword = "nope",
            RestoreDestinationDirectory = probe
        }));
        Assert.Equal(before, SnapshotTree(probe));
    }

    [Fact]
    public void ArS16_NonEmptyRestoreDestination_IsRejectedWithoutChanges()
    {
        using var fx = Fixture.Create();
        fx.AddOpenNote("ars16");
        var created = fx.CreateArchive();
        string dest = Path.Combine(fx.Root, "existing");
        Directory.CreateDirectory(dest);
        string marker = Path.Combine(dest, "already.txt");
        File.WriteAllText(marker, "already");

        Assert.Throws<EncryptedArchiveValidationException>(() => fx.Service.DryRun(new EncryptedArchiveDryRunRequest
        {
            ArchivePath = created.ArchivePath,
            ArchivePassword = fx.ArchivePassword,
            RestoreDestinationDirectory = dest
        }));
        Assert.True(File.Exists(marker));
        Assert.Equal("already", File.ReadAllText(marker));
        Assert.Equal(new[] { marker }, Directory.GetFiles(dest));
    }

    [Fact]
    public void ArS22_PlaintextZipAndQnsp_AreRejectedAsQnar()
    {
        using var fx = Fixture.Create();
        fx.AddOpenNote("open-for-zip");
        using (var db = SqliteTestUtil.CreateContext(fx.DbPath))
        {
            string zipPath = Path.Combine(fx.Root, "open.zip");
            var zip = new NoteArchiveService().ExportArchive(db, zipPath, fx.Storage);
            Assert.True(zip.Success);
            Assert.Throws<EncryptedArchiveFormatException>(() => fx.Service.DryRun(new EncryptedArchiveDryRunRequest
            {
                ArchivePath = zipPath,
                ArchivePassword = fx.ArchivePassword
            }));
        }

        string qnspPath = Path.Combine(fx.Root, "packet.qnsp");
        File.WriteAllBytes(qnspPath, Encoding.ASCII.GetBytes("QNSP" + new string('A', 64)));
        EncryptedArchiveCrypto.ResetPbkdf2Invocations();
        Assert.Throws<EncryptedArchiveFormatException>(() => fx.Service.DryRun(new EncryptedArchiveDryRunRequest
        {
            ArchivePath = qnspPath,
            ArchivePassword = fx.ArchivePassword
        }));
        Assert.Equal(0, EncryptedArchiveCrypto.Pbkdf2Invocations);
    }

    [Fact]
    public async Task ArS20_SetupIsNotCompleteUntilRecoveryDryRunSucceeds()
    {
        using var fx = Fixture.Create();
        fx.AddOpenNote("ars20-setup");
        var settingsPath = Path.Combine(fx.Root, "settings.json");
        var settings = new SettingsService(settingsPath, _ => { });
        var storage = fx.Storage;
        var vm = new ImportExportViewModel(
            () => SqliteTestUtil.CreateContext(fx.DbPath),
            new LocalMutationCoordinator(),
            attachmentStorage: storage,
            settingsService: settings,
            encryptedArchiveService: fx.Service)
        {
            TestOverridePbkdf2Iterations = TestKdfN,
            EncryptedArchiveDestinationPath = Path.Combine(fx.Root, "ui-ars20.qnar")
        };
        vm.SetEncryptedArchivePasswordInput(fx.ArchivePassword);
        vm.SetEncryptedArchivePasswordConfirmInput(fx.ArchivePassword);

        await vm.ExecuteCreateEncryptedArchiveAsync();
        Assert.True(vm.HasPendingRecoveryVerification);
        Assert.False(vm.HasLastSuccessfulEncryptedArchive);
        Assert.Null(settings.CurrentSettings.LastEncryptedArchivePath);
        Assert.False(string.IsNullOrWhiteSpace(vm.ShownRecoveryKey));

        vm.SetRecoveryKeyConfirmationInput("not-the-recovery-key");
        await vm.ExecuteVerifyEncryptedArchiveRecoveryAsync();
        Assert.True(vm.HasPendingRecoveryVerification);
        Assert.False(vm.HasLastSuccessfulEncryptedArchive);
        Assert.Contains(EncryptedArchiveSecurityException.GenericUserMessage, vm.EncryptedArchiveStatusMessage, StringComparison.Ordinal);

        vm.SetRecoveryKeyConfirmationInput(vm.ShownRecoveryKey);
        await vm.ExecuteVerifyEncryptedArchiveRecoveryAsync();
        Assert.False(vm.HasPendingRecoveryVerification);
        Assert.True(vm.HasLastSuccessfulEncryptedArchive);
        Assert.Null(vm.PendingVerificationArchivePath);
        Assert.Equal(settings.CurrentSettings.LastEncryptedArchivePath, vm.LastEncryptedArchivePath);
        Assert.True(File.Exists(vm.LastEncryptedArchivePath));
        Assert.DoesNotContain(fx.ArchivePassword, File.ReadAllText(settingsPath), StringComparison.Ordinal);
        Assert.True(string.IsNullOrEmpty(vm.ShownRecoveryKey));
    }

    [Fact]
    public void ArS23_ExceptionMessagesAndLogs_DoNotContainSecretsOrProtectedText()
    {
        using var fx = Fixture.Create();
        const string protectedText = "PROTECTED_BODY_AR_S23_TOKEN";
        fx.AddProtectedNote(protectedText, "note-pw-s23");
        var created = fx.CreateArchive();
        var ex = Assert.Throws<EncryptedArchiveSecurityException>(() => fx.Service.DryRun(new EncryptedArchiveDryRunRequest
        {
            ArchivePath = created.ArchivePath,
            ArchivePassword = fx.ArchivePassword + "-wrong"
        }));
        Assert.DoesNotContain(fx.ArchivePassword, ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(protectedText, ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(created.RecoveryKeyFormatted, ex.Message, StringComparison.Ordinal);
        if (File.Exists(ErrorLogService.LogFilePath))
        {
            string log = File.ReadAllText(ErrorLogService.LogFilePath);
            Assert.DoesNotContain(fx.ArchivePassword, log, StringComparison.Ordinal);
            Assert.DoesNotContain(protectedText, log, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ArS24_OldUserVersionSnapshot_SurvivesDbInitializerAfterValidatedDryRun()
    {
        using var fx = Fixture.Create();
        CreateLegacyV4Database(fx.DbPath);
        var created = fx.CreateArchive();
        EncryptedArchiveDryRunResult dry = fx.DryRunPassword();
        Assert.Equal(4, dry.SnapshotUserVersion);

        string restoredDb = Path.Combine(fx.Root, "restored.db");
        File.WriteAllBytes(restoredDb, dry.SnapshotSqliteBytes);
        using (var context = SqliteTestUtil.CreateContext(restoredDb))
        {
            DbInitializer.Initialize(context);
        }

        using (var context = SqliteTestUtil.CreateContext(restoredDb))
        {
            Assert.Contains(context.Notes, n => n.Text.Contains("legacy-ars24", StringComparison.Ordinal));
            Assert.Equal(DbInitializer.CurrentSchemaVersion, ReadUserVersion(restoredDb));
        }

        _ = created;
    }

    [Fact]
    public void ArS18_InjectedFailuresBeforeAndAfterStaging_LeaveDestinationAndLiveDbUntouched()
    {
        using var fx = Fixture.Create();
        fx.AddOpenNote("ars18-live");
        byte[] liveDb = File.ReadAllBytes(fx.DbPath);
        var created = fx.CreateArchive();
        string dest = Path.Combine(fx.Root, "restore-ars18");
        string destParent = Path.GetDirectoryName(dest)!;

        fx.Service.TestBeforeRestoreStaging = _ => throw new IOException("injected before staging");
        Assert.Throws<IOException>(() => fx.RestorePassword(dest));
        Assert.False(Directory.Exists(dest));
        Assert.Empty(RestoreLeftovers(destParent));
        Assert.Equal(liveDb, File.ReadAllBytes(fx.DbPath));
        fx.Service.TestBeforeRestoreStaging = null;

        fx.Service.TestAfterRestoreStagingBeforePublish = staging =>
        {
            Assert.True(Directory.Exists(staging));
            Assert.True(File.Exists(Path.Combine(staging, EncryptedArchiveConstants.RestoredDatabaseFileName)));
            throw new IOException("injected after staging");
        };
        Assert.Throws<IOException>(() => fx.RestorePassword(dest));
        Assert.False(Directory.Exists(dest));
        Assert.Empty(RestoreLeftovers(destParent));
        Assert.Equal(liveDb, File.ReadAllBytes(fx.DbPath));
        fx.Service.TestAfterRestoreStagingBeforePublish = null;

        string emptyDest = Path.Combine(fx.Root, "restore-ars18-empty");
        Directory.CreateDirectory(emptyDest);
        fx.Service.TestAfterRestoreStagingBeforePublish = _ => throw new IOException("injected after staging empty dest");
        Assert.Throws<IOException>(() => fx.RestorePassword(emptyDest));
        Assert.True(Directory.Exists(emptyDest));
        Assert.Empty(Directory.GetFileSystemEntries(emptyDest));
        Assert.Empty(RestoreLeftovers(Path.GetDirectoryName(emptyDest)!));
        Assert.Equal(liveDb, File.ReadAllBytes(fx.DbPath));
    }

    [Fact]
    public void ArS25_InProcessRestore_DoesNotUseWpfOrDpapi()
    {
        using var fx = Fixture.Create();
        fx.AddOpenNote("ars25-body");
        fx.AddAttachmentToLastNote(Encoding.UTF8.GetBytes("att-25"), "ars25.bin");
        var created = fx.CreateArchive();
        string dest = Path.Combine(fx.Root, "restore-ars25");
        string liveRoot = QuickNotesDbContext.LiveProfileDirectory;
        string s3 = Path.Combine(liveRoot, "s3_credentials.dat");
        string syncPw = Path.Combine(liveRoot, "sync_password.dat");
        DateTime s3Write = File.Exists(s3) ? File.GetLastWriteTimeUtc(s3) : DateTime.MinValue;
        DateTime syncWrite = File.Exists(syncPw) ? File.GetLastWriteTimeUtc(syncPw) : DateTime.MinValue;
        bool s3Existed = File.Exists(s3);
        bool syncExisted = File.Exists(syncPw);

        var restored = fx.RestorePassword(dest);
        Assert.True(File.Exists(restored.DatabasePath));
        using (var db = SqliteTestUtil.CreateContext(restored.DatabasePath))
        {
            Assert.Contains(db.Notes, n => n.Text == "ars25-body");
        }

        Assert.Equal(s3Existed, File.Exists(s3));
        Assert.Equal(syncExisted, File.Exists(syncPw));
        if (s3Existed)
        {
            Assert.Equal(s3Write, File.GetLastWriteTimeUtc(s3));
        }

        if (syncExisted)
        {
            Assert.Equal(syncWrite, File.GetLastWriteTimeUtc(syncPw));
        }

        _ = created;
    }

    [Fact]
    public void ArS19_Rotation_RevokesOldRecoveryKey_PreservesPasswordWrapAndPayload()
    {
        using var fx = Fixture.Create();
        fx.AddOpenNote("ars19-note");
        var created = fx.CreateArchive();
        byte[] before = File.ReadAllBytes(created.ArchivePath);
        var originalFramed = EncryptedArchiveFraming.ReadBoundedAllowingKdfForTests(before);

        EncryptedArchiveRotateRecoveryResult rotated = fx.Service.RotateRecovery(new EncryptedArchiveRotateRecoveryRequest
        {
            ArchivePath = created.ArchivePath,
            CurrentRecoveryKeyFormatted = created.RecoveryKeyFormatted
        });

        byte[] after = File.ReadAllBytes(created.ArchivePath);
        Assert.NotEqual(before, after);
        var rotatedFramed = EncryptedArchiveFraming.ReadBoundedAllowingKdfForTests(after);
        Assert.Equal(originalFramed.PasswordWrap, rotatedFramed.PasswordWrap);
        Assert.Equal(originalFramed.PayloadNonce, rotatedFramed.PayloadNonce);
        Assert.Equal(originalFramed.PayloadCiphertext, rotatedFramed.PayloadCiphertext);
        Assert.Equal(originalFramed.PayloadTag, rotatedFramed.PayloadTag);
        Assert.Equal(originalFramed.CanonicalHeaderText, rotatedFramed.CanonicalHeaderText);
        Assert.NotEqual(originalFramed.RecoveryWrap, rotatedFramed.RecoveryWrap);
        Assert.NotEqual(created.RecoveryKeyFormatted, rotated.RecoveryKeyFormatted);

        EncryptedArchiveDryRunResult viaNew = fx.Service.DryRun(new EncryptedArchiveDryRunRequest
        {
            ArchivePath = created.ArchivePath,
            RecoveryKeyFormatted = rotated.RecoveryKeyFormatted
        });
        EncryptedArchiveDryRunResult viaPassword = fx.DryRunPassword();
        Assert.Equal(created.ArchiveId, viaNew.ArchiveId);
        Assert.Equal(created.ArchiveId, viaPassword.ArchiveId);

        Assert.Throws<EncryptedArchiveSecurityException>(() => fx.Service.DryRun(new EncryptedArchiveDryRunRequest
        {
            ArchivePath = created.ArchivePath,
            RecoveryKeyFormatted = created.RecoveryKeyFormatted
        }));

        EncryptedArchiveDryRunResult oldFileStillOpens = fx.Service.DryRunBytes(
            before,
            archivePassword: null,
            created.RecoveryKeyFormatted,
            restoreDestinationDirectory: null);
        Assert.Equal(created.ArchiveId, oldFileStillOpens.ArchiveId);
        Assert.Throws<EncryptedArchiveSecurityException>(() => fx.Service.DryRunBytes(
            before,
            archivePassword: null,
            rotated.RecoveryKeyFormatted,
            restoreDestinationDirectory: null));

        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(created.ArchivePath)!, "*.tmp"));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(created.ArchivePath)!, "*.bak"));
    }

    [Fact]
    public void ArS19_Rotation_WithArchivePassword_AndInjectedPublishFailure_LeavesOriginalBytes()
    {
        using var fx = Fixture.Create();
        fx.AddOpenNote("ars19-fail");
        var created = fx.CreateArchive();
        byte[] before = File.ReadAllBytes(created.ArchivePath);
        fx.Service.TestBeforePublishMove = _ => throw new IOException("injected rotate publish failure");

        Assert.Throws<IOException>(() => fx.Service.RotateRecovery(new EncryptedArchiveRotateRecoveryRequest
        {
            ArchivePath = created.ArchivePath,
            ArchivePassword = fx.ArchivePassword
        }));

        Assert.Equal(before, File.ReadAllBytes(created.ArchivePath));
        fx.Service.DryRun(new EncryptedArchiveDryRunRequest
        {
            ArchivePath = created.ArchivePath,
            RecoveryKeyFormatted = created.RecoveryKeyFormatted
        });
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(created.ArchivePath)!, "*.tmp"));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(created.ArchivePath)!, "*.bak"));
    }

    [Fact]
    public void Restore_Password_WritesQuicknotesDbAndAttachmentsAsOnDisk()
    {
        using var fx = Fixture.Create();
        fx.AddOpenNote("restore-password-body");
        byte[] attachment = Encoding.UTF8.GetBytes("attachment-bytes-password");
        fx.AddAttachmentToLastNote(attachment, "photo.bin");
        string sourceAtt = Directory.GetFiles(fx.Storage.AttachmentsDirectory).Single();
        byte[] onDisk = File.ReadAllBytes(sourceAtt);
        var created = fx.CreateArchive();
        string dest = Path.Combine(fx.Root, "restore-password");
        var restored = fx.RestorePassword(dest);

        Assert.True(File.Exists(Path.Combine(dest, EncryptedArchiveConstants.RestoredDatabaseFileName)));
        Assert.False(File.Exists(Path.Combine(dest, "snapshot", "quicknotes.db")));
        string restoredAtt = Directory.GetFiles(restored.AttachmentsDirectory, "*", SearchOption.AllDirectories).Single();
        Assert.Equal(onDisk, File.ReadAllBytes(restoredAtt));
        using (var db = SqliteTestUtil.CreateContext(restored.DatabasePath))
        {
            Assert.Contains(db.Notes, n => n.Text == "restore-password-body");
        }

        _ = created;
    }

    [Fact]
    public void Restore_RecoveryKey_WritesSameTree()
    {
        using var fx = Fixture.Create();
        fx.AddOpenNote("restore-rk-body");
        var created = fx.CreateArchive();
        string dest = Path.Combine(fx.Root, "restore-rk");
        var restored = fx.Service.Restore(new EncryptedArchiveRestoreRequest
        {
            ArchivePath = created.ArchivePath,
            RecoveryKeyFormatted = created.RecoveryKeyFormatted,
            DestinationDirectory = dest
        });
        using var db = SqliteTestUtil.CreateContext(restored.DatabasePath);
        Assert.Contains(db.Notes, n => n.Text == "restore-rk-body");
    }

    [Fact]
    public void Restore_OldUserVersion_IsMigratedByDbInitializer()
    {
        using var fx = Fixture.Create();
        CreateLegacyV4Database(fx.DbPath);
        var created = fx.CreateArchive();
        string dest = Path.Combine(fx.Root, "restore-legacy");
        var restored = fx.RestorePassword(dest);
        Assert.Equal(4, restored.SnapshotUserVersion);
        Assert.Equal(DbInitializer.CurrentSchemaVersion, ReadUserVersion(restored.DatabasePath));
        using var db = SqliteTestUtil.CreateContext(restored.DatabasePath);
        Assert.Contains(db.Notes, n => n.Text.Contains("legacy-ars24", StringComparison.Ordinal));
        _ = created;
    }

    [Fact]
    public void Restore_NonEmptyDestination_IsRejectedWithoutChanges()
    {
        using var fx = Fixture.Create();
        fx.AddOpenNote("restore-nonempty");
        var created = fx.CreateArchive();
        string dest = Path.Combine(fx.Root, "existing-restore");
        Directory.CreateDirectory(dest);
        string marker = Path.Combine(dest, "keep.txt");
        File.WriteAllText(marker, "keep-me");
        Assert.Throws<EncryptedArchiveValidationException>(() => fx.RestorePassword(dest));
        Assert.Equal("keep-me", File.ReadAllText(marker));
        Assert.Equal(new[] { marker }, Directory.GetFiles(dest));
        _ = created;
    }

    [Fact]
    public void Restore_CorruptArchive_DoesNotCreateDestination()
    {
        using var fx = Fixture.Create();
        fx.AddOpenNote("restore-corrupt");
        var created = fx.CreateArchive();
        byte[] bytes = File.ReadAllBytes(created.ArchivePath);
        bytes[^1] ^= 0x5A;
        string corrupt = Path.Combine(fx.Root, "corrupt.qnar");
        File.WriteAllBytes(corrupt, bytes);
        string dest = Path.Combine(fx.Root, "restore-corrupt-dest");
        Assert.Throws<EncryptedArchiveSecurityException>(() => fx.Service.Restore(new EncryptedArchiveRestoreRequest
        {
            ArchivePath = corrupt,
            ArchivePassword = fx.ArchivePassword,
            DestinationDirectory = dest
        }));
        Assert.False(Directory.Exists(dest));
        Assert.Empty(RestoreLeftovers(fx.Root));
    }

    [Fact]
    public void Restore_LiveProfileDestination_IsRejected()
    {
        using var fx = Fixture.Create();
        fx.AddOpenNote("restore-live");
        fx.CreateArchive();
        string live = QuickNotesDbContext.LiveProfileDirectory;
        string[] before = Directory.Exists(live)
            ? Directory.GetFileSystemEntries(live, "*", SearchOption.TopDirectoryOnly)
            : Array.Empty<string>();
        // Fail before invoking a writer if the path guard regresses on a fresh machine.
        Assert.Throws<EncryptedArchiveValidationException>(() =>
            EncryptedArchivePathRules.EnsureRestoreDestinationAllowed(live));
        Assert.Throws<EncryptedArchiveValidationException>(() => fx.RestorePassword(live));
        string[] after = Directory.Exists(live)
            ? Directory.GetFileSystemEntries(live, "*", SearchOption.TopDirectoryOnly)
            : Array.Empty<string>();
        Assert.Equal(before, after);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RestoreDestination_ProtectsLiveAndIsolatedProfiles_WithoutDependingOnTheirContents(bool nested)
    {
        string isolated = Path.Combine(Path.GetTempPath(), "qn_restore_guard_" + Guid.NewGuid().ToString("N"));
        string? previous = QuickNotesDbContext.ProfileDirectoryOverride;
        try
        {
            QuickNotesDbContext.ProfileDirectoryOverride = isolated;
            Assert.False(Directory.Exists(isolated));
            foreach (string root in new[] { QuickNotesDbContext.LiveProfileDirectory, isolated })
            {
                string destination = nested ? Path.Combine(root, "restore-child") : root;
                // This guard is purely lexical: do not create, clear, or restore into a real profile.
                Assert.Throws<EncryptedArchiveValidationException>(() =>
                    EncryptedArchivePathRules.EnsureNotLiveProfile(destination));
            }
            Assert.False(Directory.Exists(isolated));
        }
        finally
        {
            QuickNotesDbContext.ProfileDirectoryOverride = previous;
        }
    }

    [Fact]
    public void Restore_DryRunCliPath_DoesNotWriteDestination()
    {
        using var fx = Fixture.Create();
        fx.AddOpenNote("cli-dry");
        var created = fx.CreateArchive();
        string dest = Path.Combine(fx.Root, "cli-dry-dest");
        var parsed = EncryptedArchiveRestoreCli.Parse(new[]
        {
            "--restore-archive", created.ArchivePath,
            "--destination", dest,
            "--password-stdin",
            "--dry-run"
        });
        using var stdin = new MemoryStream(Encoding.UTF8.GetBytes(fx.ArchivePassword + "\n"));
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        int code = EncryptedArchiveRestoreCli.Execute(parsed, stdin, stdout, stderr, fx.Service);
        Assert.Equal(EncryptedArchiveRestoreExitCodes.Success, code);
        Assert.False(Directory.Exists(dest));
        Assert.Contains("dry-run", stdout.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void DuplicatePayloadPaths_AreRejected()
    {
        using var fx = Fixture.Create();
        byte[] snap = Encoding.UTF8.GetBytes("db");
        byte[] sha = SHA256.HashData(snap);
        byte[] qnap = EncryptedArchivePayload.WriteHostile(
            2,
            new[]
            {
                (EncryptedArchiveConstants.SnapshotEntryPath, snap, (ulong)snap.Length, sha),
                (EncryptedArchiveConstants.SnapshotEntryPath, snap, (ulong)snap.Length, sha)
            });
        byte[] archive = fx.SealHostileQnap(qnap);
        File.WriteAllBytes(Path.Combine(fx.Root, "dup.qnar"), archive);
        Assert.Throws<EncryptedArchiveValidationException>(() => fx.Service.DryRunBytes(archive, fx.ArchivePassword, null, null));
    }

    private static byte[] BuildMinimalHostileQnap(string hostilePath, byte[] content)
    {
        byte[] sha = SHA256.HashData(content);
        return EncryptedArchivePayload.WriteHostile(
            1,
            new[] { (hostilePath, content, (ulong)content.Length, sha) });
    }

    private static void AssertNoUtf8(byte[] haystack, string token)
    {
        byte[] needle = Encoding.UTF8.GetBytes(token);
        Assert.True(needle.Length >= 8);
        for (int i = 0; i <= haystack.Length - needle.Length; i++)
        {
            if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle))
            {
                Assert.Fail($"Token leaked at offset {i}: {token}");
            }
        }
    }

    private static string[] SnapshotTree(string root)
        => Directory.GetFileSystemEntries(root, "*", SearchOption.AllDirectories)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static string[] RestoreLeftovers(string parent)
        => Directory.Exists(parent)
            ? Directory.GetFileSystemEntries(parent)
                .Where(p =>
                    p.Contains(".restore-staging", StringComparison.OrdinalIgnoreCase)
                    || p.Contains(".empty-aside", StringComparison.OrdinalIgnoreCase))
                .ToArray()
            : Array.Empty<string>();

    private static bool TryCreateDirectoryReparse(string linkPath, string targetPath)
        => TryCreateJunction(linkPath, targetPath) || TryCreateDirectorySymbolicLink(linkPath, targetPath);

    private static bool TryCreateJunction(string linkPath, string targetPath)
    {
        try
        {
            var psi = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{linkPath}\" \"{targetPath}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using Process? process = Process.Start(psi);
            if (process == null)
            {
                return false;
            }

            process.WaitForExit(5000);
            return Directory.Exists(linkPath)
                   && (File.GetAttributes(linkPath) & FileAttributes.ReparsePoint) != 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryCreateDirectorySymbolicLink(string linkPath, string targetPath)
    {
        try
        {
            Directory.CreateSymbolicLink(linkPath, targetPath);
            return Directory.Exists(linkPath)
                   && (File.GetAttributes(linkPath) & FileAttributes.ReparsePoint) != 0;
        }
        catch
        {
            return false;
        }
    }

    private static string? FindExistingWindowsReparseDirectory()
    {
        string[] candidates =
        {
            @"C:\Users\All Users",
            @"C:\Documents and Settings",
            @"C:\Users\Default User"
        };
        foreach (string candidate in candidates)
        {
            try
            {
                if (Directory.Exists(candidate)
                    && (File.GetAttributes(candidate) & FileAttributes.ReparsePoint) != 0)
                {
                    return candidate;
                }
            }
            catch
            {
                // Probe only.
            }
        }

        return null;
    }

    private static void CreateLegacyV4Database(string dbPath)
    {
        SqliteTestUtil.TryDeleteFileAndSiblings(dbPath);
        using var conn = new SqliteConnection(SqliteTestUtil.ConnectionString(dbPath));
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE Notes (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
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
            CREATE TABLE Tags (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Name TEXT NOT NULL,
                ParentTagId INTEGER NULL
            );
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
            CREATE UNIQUE INDEX IX_NoteTemplates_Title ON NoteTemplates(Title COLLATE NOCASE);
            CREATE TABLE NoteTemplateTags (
                TemplateId INTEGER NOT NULL,
                TagId INTEGER NOT NULL,
                PRIMARY KEY (TemplateId, TagId)
            );
            INSERT INTO Notes (Id, Text, CreatedAt, UpdatedAt, IsPinned, IsFavorite, IsInbox)
            VALUES (1, 'legacy-ars24 unique body', '2026-03-01 12:00:00', '2026-03-01 12:00:00', 1, 0, 0);
            INSERT INTO Tags (Id, Name, ParentTagId) VALUES (1, 'Work', NULL);
            INSERT INTO NoteTags (NoteId, TagId, Origin, IsSuppressed) VALUES (1, 1, 1, 0);
            PRAGMA user_version = 4;
            """;
        cmd.ExecuteNonQuery();
    }

    private static int ReadUserVersion(string dbPath)
    {
        using var conn = new SqliteConnection(SqliteTestUtil.ConnectionString(dbPath));
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private sealed class SpyNoteCryptoService : INoteCryptoService
    {
        private readonly NoteCryptoService _inner = new(iterations: 1000);
        public int DecryptCalls;
        public int DecryptWithKeyCalls;
        public int CurrentFormatVersion => _inner.CurrentFormatVersion;
        public int DefaultIterations => _inner.DefaultIterations;
        public QuickNotes.App.Services.Crypto.KdfDescriptor CurrentDescriptor => _inner.CurrentDescriptor;
        public QuickNotes.App.Services.Crypto.KdfEnvelopeReading ResolveDescriptor(string? descriptorText, int legacyIterations, int legacySaltByteSize)
            => _inner.ResolveDescriptor(descriptorText, legacyIterations, legacySaltByteSize);
        public byte[] DeriveKey(string password, byte[] salt, int iterations) => _inner.DeriveKey(password, salt, iterations);
        public NoteCryptoObject EncryptWithKey(byte[] key, byte[] plaintext, int formatVersion, Guid syncId, string objectType)
            => _inner.EncryptWithKey(key, plaintext, formatVersion, syncId, objectType);
        public byte[] DecryptWithKey(byte[] key, byte[] ciphertext, byte[] nonce, byte[] tag, int formatVersion, Guid syncId, string objectType)
        {
            DecryptWithKeyCalls++;
            return _inner.DecryptWithKey(key, ciphertext, nonce, tag, formatVersion, syncId, objectType);
        }

        public NoteEncryptionResult Encrypt(string password, byte[] plaintext, Guid syncId, string objectType, int? iterations = null)
            => _inner.Encrypt(password, plaintext, syncId, objectType, iterations);

        public byte[] Decrypt(string password, byte[] ciphertext, byte[] salt, byte[] nonce, byte[] tag, Guid syncId, string objectType, int iterations)
        {
            DecryptCalls++;
            return _inner.Decrypt(password, ciphertext, salt, nonce, tag, syncId, objectType, iterations);
        }
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; }
        public string DbPath { get; }
        public string ArchivePath { get; }
        public string ArchivePassword { get; } = "archive-password-AR-S-" + Guid.NewGuid().ToString("N");
        public AttachmentStorageService Storage { get; }
        public SpyNoteCryptoService CryptoSpy { get; } = new();
        public EncryptedArchiveService Service { get; }
        public int CreateIterations { get; }
        public int LastNoteId { get; private set; }
        public string? ProtectedAttachmentFullPath { get; private set; }
        private NoteProtectionService? _protection;

        private Fixture(int createIterations)
        {
            CreateIterations = createIterations;
            Root = Path.Combine(Path.GetTempPath(), "qn_enc_archive_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            DbPath = Path.Combine(Root, "data", "quicknotes.db");
            Directory.CreateDirectory(Path.GetDirectoryName(DbPath)!);
            Storage = new AttachmentStorageService(Path.Combine(Root, "data"));
            ArchivePath = Path.Combine(Root, "out", "backup.qnar");
            Directory.CreateDirectory(Path.GetDirectoryName(ArchivePath)!);
            Service = new EncryptedArchiveService(EncryptedArchiveKdfLimits.ForTests(), CryptoSpy);
            using var db = SqliteTestUtil.CreateContext(DbPath);
            DbInitializer.Initialize(db);
        }

        public static Fixture Create(int createIterations = TestKdfN) => new(createIterations);

        public EncryptedArchiveCreateResult CreateArchive()
            => Service.Create(new EncryptedArchiveCreateRequest
            {
                DatabasePath = DbPath,
                AttachmentsDirectory = Storage.AttachmentsDirectory,
                DestinationArchivePath = ArchivePath,
                ArchivePassword = ArchivePassword,
                Pbkdf2Iterations = CreateIterations
            });

        public EncryptedArchiveDryRunResult DryRunPassword()
            => Service.DryRun(new EncryptedArchiveDryRunRequest
            {
                ArchivePath = ArchivePath,
                ArchivePassword = ArchivePassword
            });

        public EncryptedArchiveRestoreResult RestorePassword(string destinationDirectory)
            => Service.Restore(new EncryptedArchiveRestoreRequest
            {
                ArchivePath = ArchivePath,
                ArchivePassword = ArchivePassword,
                DestinationDirectory = destinationDirectory
            });

        public byte[] SealHostileQnap(byte[] qnap)
        {
            byte[] rk = RandomNumberGenerator.GetBytes(32);
            try
            {
                return Service.SealPayload(
                    qnap,
                    ArchivePassword,
                    rk,
                    CreateIterations,
                    Guid.NewGuid(),
                    DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            }
            finally
            {
                CryptographicOperations.ZeroMemory(rk);
            }
        }

        public void AddOpenNote(string text)
        {
            using var db = SqliteTestUtil.CreateContext(DbPath);
            var note = new Note { Text = text, CreatedAt = DateTime.Now, UpdatedAt = DateTime.Now };
            db.Notes.Add(note);
            db.SaveChanges();
            LastNoteId = note.Id;
        }

        public void AddAttachmentToLastNote(byte[] content, string originalName)
        {
            using var db = SqliteTestUtil.CreateContext(DbPath);
            var saved = Storage.SaveFromBytes(content, originalName, 50 * 1024 * 1024);
            db.NoteAttachments.Add(new NoteAttachment
            {
                NoteId = LastNoteId,
                OriginalFileName = originalName,
                StoredFileName = saved.StoredFileName,
                RelativePath = saved.RelativePath,
                ContentType = saved.ContentType,
                Size = saved.Size,
                Sha256 = saved.Sha256,
                CreatedAt = DateTime.Now
            });
            db.SaveChanges();
        }

        public void AddProtectedNote(string text, string notePassword)
        {
            AddOpenNote(text);
            _protection ??= new NoteProtectionService(CryptoSpy, attachmentStorage: Storage);
            using var db = SqliteTestUtil.CreateContext(DbPath);
            Assert.True(_protection.ProtectNote(db, LastNoteId, notePassword).Success);
        }

        public void AddProtectedNoteWithAttachment(string notePassword, string text, byte[] attachmentBytes, string originalName)
        {
            AddOpenNote(text);
            AddAttachmentToLastNote(attachmentBytes, originalName);
            using (var db = SqliteTestUtil.CreateContext(DbPath))
            {
                var att = db.NoteAttachments.Single(a => a.NoteId == LastNoteId);
                ProtectedAttachmentFullPath = Storage.GetFullPath(att.RelativePath);
            }

            _protection ??= new NoteProtectionService(CryptoSpy, attachmentStorage: Storage);
            using var pdb = SqliteTestUtil.CreateContext(DbPath);
            Assert.True(_protection.ProtectNote(pdb, LastNoteId, notePassword).Success);
            using var verify = SqliteTestUtil.CreateContext(DbPath);
            var protectedAtt = verify.NoteAttachments.Single(a => a.NoteId == LastNoteId);
            ProtectedAttachmentFullPath = Storage.GetFullPath(protectedAtt.RelativePath);
        }

        public void Dispose()
        {
            SqliteTestUtil.TryDeleteDirectory(Root);
        }
    }
}
