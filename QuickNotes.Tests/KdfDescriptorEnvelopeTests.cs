using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using QuickNotes.App.Data;
using QuickNotes.App.Models;
using QuickNotes.App.Models.Sync;
using QuickNotes.App.Services;
using QuickNotes.App.Services.Crypto;
using QuickNotes.App.Services.NoteProtection;
using QuickNotes.App.Services.Sync;
using Xunit;

namespace QuickNotes.Tests;

/// <summary>
/// ROADMAP 10.2 KDF envelope versioning: legacy read, new round trip, malformed/unknown
/// descriptor rejection, hostile work-factor rejection before derivation, wrong password,
/// protected sync/archive paths and migration interruption/rollback.
///
/// Deterministic: fixed passwords, in-process crypto, temp profiles only.
/// No test touches the live %LOCALAPPDATA%\QuickNotes profile.
/// </summary>
[TestCategory(TestCategories.Integration)]
public sealed class KdfDescriptorEnvelopeTests : IDisposable
{
    private const string Password = "kdf-envelope-password-1";
    private const string WrongPassword = "kdf-envelope-password-2";
    private const int TestNoteIterations = 1000;
    private const int TestSyncIterations = 1000;

    private readonly List<string> _tempPaths = new();

    public void Dispose()
    {
        foreach (var p in _tempPaths)
        {
            if (File.Exists(p)) { try { File.Delete(p); } catch { } }
            if (Directory.Exists(p)) { try { Directory.Delete(p, recursive: true); } catch { } }
        }
    }

    private string MakeTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "qn_kdf_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _tempPaths.Add(dir);
        return dir;
    }

    private string CreateTempDb() => Path.Combine(MakeTempDir(), "quicknotes.db");

    private static QuickNotesDbContext CreateContext(string dbPath)
        => SqliteTestUtil.CreateContext(dbPath);

    // ------------------------------------------------------------------
    // 1. Descriptor contract (pure, no crypto)
    // ------------------------------------------------------------------

    [Fact]
    public void Descriptor_CanonicalText_IsStableAndNonSecret()
    {
        var descriptor = KdfDescriptor.Pbkdf2(120_000);

        Assert.Equal("PBKDF2-HMAC-SHA256", descriptor.AlgorithmId);
        Assert.Equal(KdfDescriptorConstants.CurrentDescriptorVersion, descriptor.DescriptorVersion);
        Assert.Equal(120_000, descriptor.Iterations);
        Assert.Equal(KdfDescriptorConstants.SaltByteSize, descriptor.SaltByteSize);
        Assert.Equal("PBKDF2-HMAC-SHA256|1|120000|32", descriptor.ToCanonicalText());

        // Log-safe rendering is fixed, human readable and carries no salt, key or plaintext.
        Assert.Equal("PBKDF2-HMAC-SHA256/v1/n=120000/salt=32", descriptor.ToString());
    }

    [Theory]
    [InlineData("PBKDF2-HMAC-SHA256|1|1|32")]
    [InlineData("PBKDF2-HMAC-SHA256|1|120000|32")]
    [InlineData("PBKDF2-HMAC-SHA256|1|2490000|32")]
    [InlineData("PBKDF2-HMAC-SHA256|1|5000000|32")]
    public void Descriptor_TryParse_AcceptsCanonicalInBounds(string text)
    {
        Assert.True(KdfDescriptor.TryParse(text, KdfDescriptorLimits.LocalEnvelope, out var descriptor, out var error), error);
        Assert.Equal(text, descriptor.ToCanonicalText());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("PBKDF2-HMAC-SHA256|1|120000")]
    [InlineData("PBKDF2-HMAC-SHA256|1|120000|32|extra")]
    [InlineData("argon2id|1|120000|32")]
    [InlineData("ARGON2ID|1|120000|32")]
    [InlineData("ARGON2ID|2|19456|32")]
    [InlineData("PBKDF2-HMAC-SHA256|2|120000|32")]
    [InlineData("PBKDF2-HMAC-SHA256|0|120000|32")]
    [InlineData("PBKDF2-HMAC-SHA256|1|0120|32")]
    [InlineData("PBKDF2-HMAC-SHA256|1|+120|32")]
    [InlineData("PBKDF2-HMAC-SHA256|1| 120|32")]
    [InlineData("PBKDF2-HMAC-SHA256|1|120 |32")]
    [InlineData("PBKDF2-HMAC-SHA256|1|0|32")]
    [InlineData("PBKDF2-HMAC-SHA256|1|-1|32")]
    [InlineData("PBKDF2-HMAC-SHA256|1|5000001|32")]
    [InlineData("PBKDF2-HMAC-SHA256|1|120000|16")]
    [InlineData("PBKDF2-HMAC-SHA256|1|120000|0")]
    public void Descriptor_TryParse_RejectsMalformedUnknownAndOutOfBounds(string text)
    {
        bool parsed = KdfDescriptor.TryParse(text, KdfDescriptorLimits.LocalEnvelope, out var descriptor, out var error);

        Assert.False(parsed);
        Assert.NotEmpty(error);
        Assert.Equal(default, descriptor);
    }

    [Fact]
    public void Descriptor_Validate_RejectsUnknownAlgorithmAndVersionWithoutDerivation()
    {
        KdfDiagnostics.ResetPbkdf2Invocations();

        var wrongAlgorithm = new KdfDescriptor("ARGON2ID", 1, 120_000, 32);
        Assert.Throws<KdfDescriptorValidationException>(() => wrongAlgorithm.Validate(KdfDescriptorLimits.LocalEnvelope));

        var wrongVersion = new KdfDescriptor(KdfAlgorithmIds.Pbkdf2HmacSha256, 2, 120_000, 32);
        Assert.Throws<KdfDescriptorValidationException>(() => wrongVersion.Validate(KdfDescriptorLimits.LocalEnvelope));

        Assert.Equal(0, KdfDiagnostics.Pbkdf2Invocations);
    }

    // ------------------------------------------------------------------
    // 2. Note envelope (JSON columns): legacy defaults and strict rejection
    // ------------------------------------------------------------------

    [Fact]
    public void NoteCrypto_LegacyEnvelope_UsesExactHistoricalIterationsAndSaltLength()
    {
        var crypto = new NoteCryptoService(TestNoteIterations);

        var reading = crypto.ResolveDescriptor(null, TestNoteIterations, NoteCryptoService.SaltByteSize);

        Assert.True(reading.NeedsMigration);
        Assert.False(reading.IsCurrent);
        Assert.Equal(KdfEnvelopeState.LegacyMissingDescriptor, reading.State);
        Assert.Equal(KdfAlgorithmIds.Pbkdf2HmacSha256, reading.Descriptor.AlgorithmId);
        Assert.Equal(TestNoteIterations, reading.Descriptor.Iterations);
        Assert.Equal(NoteCryptoService.SaltByteSize, reading.Descriptor.SaltByteSize);
    }

    [Fact]
    public void NoteCrypto_LegacyEnvelope_HostileWorkFactorIsRejectedBeforeDerivation()
    {
        var crypto = new NoteCryptoService(TestNoteIterations);
        KdfDiagnostics.ResetPbkdf2Invocations();

        Assert.Throws<NoteProtectionSecurityException>(() =>
            crypto.ResolveDescriptor(null, KdfDescriptorConstants.MaxPbkdf2Iterations + 1, NoteCryptoService.SaltByteSize));
        Assert.Throws<NoteProtectionSecurityException>(() =>
            crypto.ResolveDescriptor(null, 0, NoteCryptoService.SaltByteSize));
        Assert.Throws<NoteProtectionSecurityException>(() =>
            crypto.ResolveDescriptor(null, TestNoteIterations, 16));

        Assert.Equal(0, KdfDiagnostics.Pbkdf2Invocations);
    }

    [Fact]
    public void NoteCrypto_UnknownDescriptorText_IsRejectedBeforeDerivation()
    {
        var crypto = new NoteCryptoService(TestNoteIterations);
        KdfDiagnostics.ResetPbkdf2Invocations();

        Assert.Throws<NoteProtectionSecurityException>(() =>
            crypto.ResolveDescriptor("ARGON2ID|1|120000|32", TestNoteIterations, NoteCryptoService.SaltByteSize));
        Assert.Throws<NoteProtectionSecurityException>(() =>
            crypto.ResolveDescriptor("PBKDF2-HMAC-SHA256|1|9000000|32", TestNoteIterations, NoteCryptoService.SaltByteSize));

        Assert.Equal(0, KdfDiagnostics.Pbkdf2Invocations);
    }

    [Fact]
    public void NoteCrypto_NewWrite_EmitsCurrentDescriptor_AndRoundTrips()
    {
        var crypto = new NoteCryptoService(TestNoteIterations);
        var syncId = Guid.NewGuid();
        byte[] plaintext = Encoding.UTF8.GetBytes("envelope payload");

        var envelope = crypto.Encrypt(Password, plaintext, syncId, NoteProtectedObjectType.NoteEnvelope);

        Assert.Equal("PBKDF2-HMAC-SHA256|1|1000|32", envelope.KdfDescriptorText);
        Assert.True(crypto.ResolveDescriptor(envelope.KdfDescriptorText, TestNoteIterations, NoteCryptoService.SaltByteSize).IsCurrent);

        byte[] decrypted = crypto.Decrypt(
            Password,
            envelope.Ciphertext,
            envelope.Salt,
            envelope.Nonce,
            envelope.Tag,
            syncId,
            NoteProtectedObjectType.NoteEnvelope,
            envelope.KdfIterations);

        Assert.Equal(plaintext, decrypted);
    }

    [Fact]
    public void NoteCrypto_DeriveKey_HostileWorkFactorIsRejectedBeforeDerivation()
    {
        var crypto = new NoteCryptoService(TestNoteIterations);
        KdfDiagnostics.ResetPbkdf2Invocations();

        Assert.Throws<NoteProtectionSecurityException>(() =>
            crypto.DeriveKey(Password, new byte[NoteCryptoService.SaltByteSize], KdfDescriptorConstants.MaxPbkdf2Iterations + 1));

        Assert.Equal(0, KdfDiagnostics.Pbkdf2Invocations);
    }

    // ------------------------------------------------------------------
    // 3. QNAT protected attachment file
    // ------------------------------------------------------------------

    private static byte[] BuildLegacyV1Qnat(byte[] key, byte[] plaintext, int formatVersion, Guid syncId)
    {
        var crypto = new NoteCryptoService(TestNoteIterations);
        var obj = crypto.EncryptWithKey(key, plaintext, formatVersion, syncId, NoteProtectedObjectType.AttachmentContent);

        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        bw.Write(new byte[] { 0x51, 0x4E, 0x41, 0x54 }); // QNAT
        bw.Write((byte)0x01);
        bw.Write(NoteCryptoService.SaltByteSize);
        bw.Write(new byte[NoteCryptoService.SaltByteSize]);
        bw.Write(obj.Nonce);
        bw.Write(obj.Tag);
        bw.Write((long)plaintext.Length);
        bw.Write(obj.Ciphertext);
        bw.Flush();
        return ms.ToArray();
    }

    [Fact]
    public void Qnat_LegacyV1Container_IsReadWithOwningNoteIterations()
    {
        var crypto = new NoteCryptoService(TestNoteIterations);
        var syncId = Guid.NewGuid();
        byte[] plaintext = Encoding.UTF8.GetBytes("legacy attachment content");
        byte[] salt = Enumerable.Range(0, NoteCryptoService.SaltByteSize).Select(i => (byte)i).ToArray();
        byte[] key = crypto.DeriveKey(Password, salt, TestNoteIterations);

        byte[] v1 = BuildLegacyV1Qnat(key, plaintext, crypto.CurrentFormatVersion, syncId);

        Assert.True(ProtectedAttachmentFile.TryReadDescriptor(v1, TestNoteIterations, out var reading));
        Assert.True(reading.NeedsMigration);
        Assert.Equal(TestNoteIterations, reading.Descriptor.Iterations);

        byte[] decrypted = ProtectedAttachmentFile.DecryptContainer(key, v1, crypto.CurrentFormatVersion, syncId);
        Assert.Equal(plaintext, decrypted);
    }

    [Fact]
    public void Qnat_NewWrite_CarriesDescriptor_AndRoundTrips()
    {
        var crypto = new NoteCryptoService(TestNoteIterations);
        var syncId = Guid.NewGuid();
        byte[] plaintext = Encoding.UTF8.GetBytes("current attachment content");
        byte[] salt = Enumerable.Range(0, NoteCryptoService.SaltByteSize).Select(i => (byte)(i * 3)).ToArray();
        byte[] key = crypto.DeriveKey(Password, salt, TestNoteIterations);

        byte[] v2 = ProtectedAttachmentFile.CreateContainer(
            key, plaintext, crypto.CurrentFormatVersion, syncId, crypto.CurrentDescriptor);

        Assert.Equal(0x02, v2[4]);
        Assert.True(ProtectedAttachmentFile.TryReadDescriptor(v2, TestNoteIterations, out var reading));
        Assert.True(reading.IsCurrent);
        Assert.Equal(TestNoteIterations, reading.Descriptor.Iterations);

        byte[] decrypted = ProtectedAttachmentFile.DecryptContainer(key, v2, crypto.CurrentFormatVersion, syncId);
        Assert.Equal(plaintext, decrypted);
    }

    [Fact]
    public void Qnat_TamperedDescriptor_IsRejectedBeforeDerivation()
    {
        var crypto = new NoteCryptoService(TestNoteIterations);
        var syncId = Guid.NewGuid();
        byte[] key = crypto.DeriveKey(Password, new byte[NoteCryptoService.SaltByteSize], TestNoteIterations);

        // Layout after magic(4) + version(1) + algId(1): uint16 descriptor version, int32 work factor.
        byte[] hostileWorkFactor = ProtectedAttachmentFile.CreateContainer(
            key, new byte[] { 1, 2, 3 }, crypto.CurrentFormatVersion, syncId, crypto.CurrentDescriptor);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(
            hostileWorkFactor.AsSpan(4 + 1 + 1 + 2), KdfDescriptorConstants.MaxPbkdf2Iterations + 1);

        KdfDiagnostics.ResetPbkdf2Invocations();
        Assert.False(ProtectedAttachmentFile.TryReadDescriptor(hostileWorkFactor, TestNoteIterations, out _));
        Assert.Throws<NoteProtectionSecurityException>(() =>
            ProtectedAttachmentFile.DecryptContainer(key, hostileWorkFactor, crypto.CurrentFormatVersion, syncId));
        Assert.Equal(0, KdfDiagnostics.Pbkdf2Invocations);

        // Unknown descriptor version fails closed too.
        byte[] unknownDescriptorVersion = ProtectedAttachmentFile.CreateContainer(
            key, new byte[] { 1, 2, 3 }, crypto.CurrentFormatVersion, syncId, crypto.CurrentDescriptor);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(
            unknownDescriptorVersion.AsSpan(4 + 1 + 1), (ushort)(KdfDescriptorConstants.CurrentDescriptorVersion + 1));
        Assert.False(ProtectedAttachmentFile.TryReadDescriptor(unknownDescriptorVersion, TestNoteIterations, out _));
        Assert.Throws<NoteProtectionSecurityException>(() =>
            ProtectedAttachmentFile.DecryptContainer(key, unknownDescriptorVersion, crypto.CurrentFormatVersion, syncId));
    }

    [Fact]
    public void Qnat_UnknownKdfAlgorithm_IsRejected()
    {
        var crypto = new NoteCryptoService(TestNoteIterations);
        var syncId = Guid.NewGuid();
        byte[] key = crypto.DeriveKey(Password, new byte[NoteCryptoService.SaltByteSize], TestNoteIterations);
        byte[] container = ProtectedAttachmentFile.CreateContainer(
            key, new byte[] { 9 }, crypto.CurrentFormatVersion, syncId, crypto.CurrentDescriptor);

        container[5] = 0x7F; // unknown algorithm id

        Assert.False(ProtectedAttachmentFile.TryReadDescriptor(container, TestNoteIterations, out _));
        Assert.Throws<NoteProtectionSecurityException>(() =>
            ProtectedAttachmentFile.DecryptContainer(key, container, crypto.CurrentFormatVersion, syncId));
    }

    // ------------------------------------------------------------------
    // 4. QNSP sync package
    // ------------------------------------------------------------------

    private static string BuildSyncPackageJson(
        SyncCryptoService crypto,
        Guid deviceId,
        Guid packageId,
        string password,
        string? declaredAlgorithm = null,
        int? declaredKdfVersion = null,
        int? declaredIterations = null,
        string? descriptor = null)
    {
        var payload = new SyncPackagePayload
        {
            PackageId = packageId,
            SourceDeviceId = deviceId,
            CreatedAtUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        };
        byte[] plaintext = JsonSerializer.SerializeToUtf8Bytes(payload, SyncPackageExporter.DeterministicJsonOptions);

        var envelope = new SyncPackageEnvelope
        {
            Magic = "QNSP",
            FormatVersion = 1,
            PackageId = packageId,
            DeviceId = deviceId,
            CreatedAtUtc = payload.CreatedAtUtc,
            Crypto = new SyncCryptoHeader
            {
                Algorithm = SyncCryptoService.AlgorithmName,
                KdfAlgorithm = declaredAlgorithm ?? SyncCryptoService.KdfName,
                KdfVersion = declaredKdfVersion ?? SyncCryptoService.KdfVersion,
                KdfIterations = declaredIterations ?? crypto.Iterations,
                KdfDescriptor = descriptor
            }
        };

        var enc = crypto.EncryptPayload(plaintext, password, envelope.GetAssociatedData());
        envelope.Crypto.SaltBase64 = Convert.ToBase64String(enc.Salt);
        envelope.Crypto.NonceBase64 = Convert.ToBase64String(enc.Nonce);
        envelope.Crypto.TagBase64 = Convert.ToBase64String(enc.Tag);
        envelope.EncryptedPayloadBase64 = Convert.ToBase64String(enc.Ciphertext);
        return JsonSerializer.Serialize(envelope);
    }

    [Fact]
    public async Task SyncPackage_LegacyEnvelopeWithoutDescriptor_Imports()
    {
        string dbPath = CreateTempDb();
        using (var db = CreateContext(dbPath)) { DbInitializer.Initialize(db); }

        var crypto = new SyncCryptoService(TestSyncIterations);
        string packageJson = BuildSyncPackageJson(crypto, Guid.NewGuid(), Guid.NewGuid(), Password);

        // A legacy package has no descriptor field at all: the header KDF fields are authoritative.
        Assert.Null(JsonSerializer.Deserialize<SyncPackageEnvelope>(packageJson)!.Crypto.KdfDescriptor);

        using (var db = CreateContext(dbPath))
        {
            var result = await new SyncPackageImporter(crypto).ImportPackageAsync(db, packageJson, Password);
            Assert.True(result.Success, string.Join("; ", result.Errors));
        }
    }

    [Fact]
    public void SyncPackage_DescriptorIsMetadataOnly_AndNotPartOfAad()
    {
        var crypto = new SyncCryptoService(TestSyncIterations);
        var envelope = new SyncPackageEnvelope
        {
            Magic = "QNSP",
            FormatVersion = 1,
            PackageId = Guid.NewGuid(),
            DeviceId = Guid.NewGuid(),
            Crypto = new SyncCryptoHeader
            {
                Algorithm = SyncCryptoService.AlgorithmName,
                KdfAlgorithm = SyncCryptoService.KdfName,
                KdfVersion = SyncCryptoService.KdfVersion,
                KdfIterations = crypto.Iterations
            }
        };

        byte[] aadWithoutDescriptor = envelope.GetAssociatedData();
        envelope.Crypto.KdfDescriptor = crypto.CurrentDescriptor.ToCanonicalText();
        byte[] aadWithDescriptor = envelope.GetAssociatedData();

        Assert.Equal("PBKDF2-HMAC-SHA256|1|1000|32", envelope.Crypto.KdfDescriptor);
        // Byte-for-byte legacy compatibility depends on this: adding the descriptor must not move the AAD.
        Assert.Equal(aadWithoutDescriptor, aadWithDescriptor);

        var reading = crypto.ResolveDescriptor(
            envelope.Crypto.KdfDescriptor,
            envelope.Crypto.KdfAlgorithm,
            envelope.Crypto.KdfVersion,
            envelope.Crypto.KdfIterations,
            SyncCryptoService.SaltByteSize);

        Assert.True(reading.IsCurrent);
        Assert.Equal(TestSyncIterations, reading.Descriptor.Iterations);
    }

    [Fact]
    public async Task SyncPackage_HostileOrUnknownKdfHeader_IsRejectedBeforeDerivation()
    {
        string dbPath = CreateTempDb();
        using (var db = CreateContext(dbPath)) { DbInitializer.Initialize(db); }

        var crypto = new SyncCryptoService(TestSyncIterations);
        string absurd = BuildSyncPackageJson(crypto, Guid.NewGuid(), Guid.NewGuid(), Password,
            declaredIterations: KdfDescriptorConstants.MaxPbkdf2Iterations + 1);
        string unknownAlgorithm = BuildSyncPackageJson(crypto, Guid.NewGuid(), Guid.NewGuid(), Password,
            declaredAlgorithm: "ARGON2ID");

        KdfDiagnostics.ResetPbkdf2Invocations();

        using (var db = CreateContext(dbPath))
        {
            var importer = new SyncPackageImporter(crypto);
            var absurdResult = await importer.ImportPackageAsync(db, absurd, Password);
            var unknownResult = await importer.ImportPackageAsync(db, unknownAlgorithm, Password);

            Assert.False(absurdResult.Success);
            Assert.False(unknownResult.Success);
            Assert.Empty(db.Notes);
        }

        Assert.Equal(0, KdfDiagnostics.Pbkdf2Invocations);
    }

    [Fact]
    public async Task SyncPackage_WrongPassword_FailsWithoutPartialImport()
    {
        string dbPath = CreateTempDb();
        using (var db = CreateContext(dbPath)) { DbInitializer.Initialize(db); }

        var crypto = new SyncCryptoService(TestSyncIterations);
        string packageJson = BuildSyncPackageJson(crypto, Guid.NewGuid(), Guid.NewGuid(), Password);

        using (var db = CreateContext(dbPath))
        {
            var result = await new SyncPackageImporter(crypto).ImportPackageAsync(db, packageJson, WrongPassword);
            Assert.False(result.Success);
            Assert.Empty(db.Notes);
        }
    }

    [Fact]
    public async System.Threading.Tasks.Task Sync_ExportedProtectedPackage_CarriesDescriptorAndNoPlaintextSecret()
    {
        string dbPath = CreateTempDb();
        var storage = new AttachmentStorageService(MakeTempDir());
        using (var db = CreateContext(dbPath)) { DbInitializer.Initialize(db); }

        const string secret = "KDF_ENVELOPE_SECRET_TEXT";
        var protection = new NoteProtectionService(crypto: new NoteCryptoService(TestNoteIterations), attachmentStorage: storage);
        int noteId;
        using (var db = CreateContext(dbPath))
        {
            var note = new Note { Text = secret };
            db.Notes.Add(note);
            db.SaveChanges();
            noteId = note.Id;
            Assert.True(protection.ProtectNote(db, noteId, Password).Success);
        }

        var syncCrypto = new SyncCryptoService(TestSyncIterations);
        var exporter = new SyncPackageExporter(syncCrypto, new FixedDeviceId(Guid.NewGuid()));

        using (var db = CreateContext(dbPath))
        {
            var export = await exporter.ExportPackageAsync(db, Password);
            Assert.True(export.Success, string.Join("; ", export.Errors));

            var envelope = JsonSerializer.Deserialize<SyncPackageEnvelope>(export.PackageJson!)!;
            Assert.Equal("PBKDF2-HMAC-SHA256|1|1000|32", envelope.Crypto.KdfDescriptor);
            Assert.DoesNotContain(secret, export.PackageJson!, StringComparison.Ordinal);
            Assert.DoesNotContain(Password, export.PackageJson!, StringComparison.Ordinal);
        }

        using (var db = CreateContext(dbPath))
        {
            var note = db.Notes.AsNoTracking().Single(n => n.Id == noteId);
            Assert.Equal("PBKDF2-HMAC-SHA256|1|1000|32", note.ProtectedKdfDescriptor);
            Assert.Equal(TestNoteIterations, note.ProtectedKdfIterations);
            Assert.Equal(string.Empty, note.Text);
            Assert.Equal(string.Empty, note.Title);
        }
    }

    // ------------------------------------------------------------------
    // 5. QNBA sync attachment blob
    // ------------------------------------------------------------------

    private static SyncAttachmentBlobService CreateBlobService(int iterations = TestSyncIterations)
    {
        var settings = new SyncCloudSettings { SyncAttachments = true };
        var keyHelper = new SyncObjectKeyHelper(settings);
        var storage = new AttachmentStorageService(MakeStaticTempDir());
        return new SyncAttachmentBlobService(
            new NullTransport(),
            new SyncCryptoService(iterations),
            storage,
            keyHelper,
            settings);
    }

    [Fact]
    public void Blob_NewWrite_CarriesDescriptor_AndRoundTrips()
    {
        var service = CreateBlobService();
        byte[] plaintext = RandomBytes(256);
        string sha = Sha256Hex(plaintext);

        byte[] envelope = service.EncryptBlobEnvelope(plaintext, sha, Password);

        Assert.Equal(0x02, envelope[4]);
        byte[] decrypted = service.DecryptBlobEnvelope(envelope, sha, Password);
        Assert.Equal(plaintext, decrypted);
    }

    [Fact]
    public void Blob_LegacyV1EnvelopeWithoutDescriptor_IsReadWithServiceIterations()
    {
        const int serviceIterations = 5000;
        var service = CreateBlobService(serviceIterations);
        var crypto = new SyncCryptoService(serviceIterations);
        byte[] plaintext = RandomBytes(128);
        string sha = Sha256Hex(plaintext);

        byte[] aad = Encoding.UTF8.GetBytes($"QNBA|1|{sha}");
        var enc = crypto.EncryptPayload(plaintext, Password, aad);

        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        bw.Write(new byte[] { 0x51, 0x4E, 0x42, 0x41 }); // QNBA
        bw.Write((byte)0x01);
        bw.Write(enc.Salt.Length);
        bw.Write(enc.Salt);
        bw.Write(enc.Nonce);
        bw.Write(enc.Tag);
        bw.Write((long)plaintext.Length);
        bw.Write(enc.Ciphertext);
        bw.Flush();

        byte[] decrypted = service.DecryptBlobEnvelope(ms.ToArray(), sha, Password);
        Assert.Equal(plaintext, decrypted);
    }

    [Fact]
    public void Blob_HostileDescriptorAndUnknownVersion_AreRejectedBeforeDerivation()
    {
        var service = CreateBlobService();
        byte[] plaintext = RandomBytes(64);
        string sha = Sha256Hex(plaintext);
        byte[] envelope = service.EncryptBlobEnvelope(plaintext, sha, Password);

        // v2 layout: magic(4) + version(1) + algId(1) + uint16 descriptor version + int32 work factor.
        byte[] hostile = (byte[])envelope.Clone();
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(
            hostile.AsSpan(4 + 1 + 1 + 2), KdfDescriptorConstants.MaxPbkdf2Iterations + 1);

        byte[] unknownVersion = (byte[])envelope.Clone();
        unknownVersion[4] = 0x03;

        KdfDiagnostics.ResetPbkdf2Invocations();
        Assert.Throws<SyncSecurityException>(() => service.DecryptBlobEnvelope(hostile, sha, Password));
        Assert.Throws<SyncSecurityException>(() => service.DecryptBlobEnvelope(unknownVersion, sha, Password));
        Assert.Equal(0, KdfDiagnostics.Pbkdf2Invocations);
    }

    [Fact]
    public void Blob_V2DescriptorWorkFactorIsHonored_NotTheReaderPreference()
    {
        byte[] plaintext = RandomBytes(48);
        string sha = Sha256Hex(plaintext);

        var writer = CreateBlobService(TestSyncIterations);
        byte[] envelope = writer.EncryptBlobEnvelope(plaintext, sha, Password);

        // A reader configured with a different default must still use the stored descriptor.
        var readerWithDifferentPreference = CreateBlobService(TestSyncIterations + 7);
        Assert.Equal(plaintext, readerWithDifferentPreference.DecryptBlobEnvelope(envelope, sha, Password));
    }

    [Fact]
    public void Blob_V1UsesReaderPreference_AbsentDescriptorHasNoOtherSource()
    {
        var crypto = new SyncCryptoService(5000);
        byte[] plaintext = RandomBytes(48);
        string sha = Sha256Hex(plaintext);
        byte[] aad = Encoding.UTF8.GetBytes($"QNBA|1|{sha}");
        var enc = crypto.EncryptPayload(plaintext, Password, aad);

        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        bw.Write(new byte[] { 0x51, 0x4E, 0x42, 0x41 });
        bw.Write((byte)0x01);
        bw.Write(enc.Salt.Length);
        bw.Write(enc.Salt);
        bw.Write(enc.Nonce);
        bw.Write(enc.Tag);
        bw.Write((long)plaintext.Length);
        bw.Write(enc.Ciphertext);
        bw.Flush();
        byte[] v1 = ms.ToArray();

        Assert.Equal(plaintext, CreateBlobService(5000).DecryptBlobEnvelope(v1, sha, Password));
        // No descriptor exists in a v1 blob, so a mismatched reader work factor cannot decrypt it.
        Assert.Throws<SyncSecurityException>(() => CreateBlobService(5001).DecryptBlobEnvelope(v1, sha, Password));
    }

    [Fact]
    public void Blob_WrongPassword_FailsClosed()
    {
        var service = CreateBlobService();
        byte[] plaintext = RandomBytes(32);
        string sha = Sha256Hex(plaintext);
        byte[] envelope = service.EncryptBlobEnvelope(plaintext, sha, Password);

        Assert.Throws<SyncSecurityException>(() => service.DecryptBlobEnvelope(envelope, sha, WrongPassword));
    }

    // ------------------------------------------------------------------
    // 6. Archive envelope: canonical header and bounds unchanged
    // ------------------------------------------------------------------

    [Fact]
    public void Archive_DescriptorConstants_AreUnchangedAndShareTheKdfIdentifier()
    {
        var constants = typeof(QuickNotes.App.Services.EncryptedArchive.EncryptedArchiveConstants);

        Assert.Equal(KdfAlgorithmIds.Pbkdf2HmacSha256,
            QuickNotes.App.Services.EncryptedArchive.EncryptedArchiveConstants.KdfAlgorithmV1);
        Assert.Equal(KdfDescriptorConstants.CurrentDescriptorVersion,
            QuickNotes.App.Services.EncryptedArchive.EncryptedArchiveConstants.KdfVersionV1);
        Assert.Equal(KdfDescriptorConstants.MaxPbkdf2Iterations,
            QuickNotes.App.Services.EncryptedArchive.EncryptedArchiveConstants.MaxKdfIterations);
        Assert.Equal(2_490_000,
            QuickNotes.App.Services.EncryptedArchive.EncryptedArchiveConstants.DefaultPbkdf2IterationsForNewArchives);
        Assert.Equal(10_000,
            QuickNotes.App.Services.EncryptedArchive.EncryptedArchiveConstants.MinKdfIterationsUntrusted);
        Assert.NotNull(constants);
    }

    [Fact]
    public void ArchiveUntrustedLimits_KeepTheStricterTenThousandFloor()
    {
        var limits = KdfDescriptorLimits.ArchiveUntrusted;

        Assert.Equal(10_000, limits.MinIterations);
        Assert.Equal(KdfDescriptorConstants.MaxPbkdf2Iterations, limits.MaxIterations);

        Assert.False(KdfDescriptor.TryParse("PBKDF2-HMAC-SHA256|1|9999|32", limits, out _, out _));
        Assert.True(KdfDescriptor.TryParse("PBKDF2-HMAC-SHA256|1|10000|32", limits, out _, out _));

        // Note/sync families stay permissive on purpose: their historical and test work factors
        // are below the archive floor and must keep reading.
        Assert.True(KdfDescriptor.TryParse("PBKDF2-HMAC-SHA256|1|9999|32", KdfDescriptorLimits.LocalEnvelope, out _, out _));
        Assert.True(KdfDescriptor.TryParse("PBKDF2-HMAC-SHA256|1|9999|32", KdfDescriptorLimits.CloudEnvelope, out _, out _));
    }

    // ------------------------------------------------------------------
    // 7. Note protection: legacy detection, opportunistic migration, rollback
    // ------------------------------------------------------------------

    private static byte[] RandomBytes(int length)
    {
        var bytes = new byte[length];
        System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);
        return bytes;
    }

    private static string Sha256Hex(byte[] data)
        => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data)).ToLowerInvariant();

    private static string MakeStaticTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "qn_kdf_blob_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static int ExecuteNonQuery(string dbPath, string sql, int noteId)
    {
        using var conn = new SqliteConnection(SqliteTestUtil.ConnectionString(dbPath));
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$id", noteId);
        return cmd.ExecuteNonQuery();
    }

    private static int ExecuteNonQuery(string dbPath, string sql, int noteId, Guid syncId)
    {
        using var conn = new SqliteConnection(SqliteTestUtil.ConnectionString(dbPath));
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$id", noteId);
        cmd.Parameters.AddWithValue("$syncId", syncId.ToString("D"));
        return cmd.ExecuteNonQuery();
    }

    private static string? ReadNoteDescriptor(string dbPath, int noteId)
    {
        using var conn = new SqliteConnection(SqliteTestUtil.ConnectionString(dbPath));
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT ProtectedKdfDescriptor FROM Notes WHERE Id = $id;";
        cmd.Parameters.AddWithValue("$id", noteId);
        var value = cmd.ExecuteScalar();
        return value == null || value == DBNull.Value ? null : (string)value;
    }

    [Fact]
    public void NoteProtection_LegacyRow_ReportsMigration_AndHeaderIterationsWinOverServicePreference()
    {
        string dbPath = CreateTempDb();
        var storage = new AttachmentStorageService(MakeStaticTempDir());
        using (var db = CreateContext(dbPath)) { DbInitializer.Initialize(db); }

        var protection = new NoteProtectionService(crypto: new NoteCryptoService(TestNoteIterations), attachmentStorage: storage);
        int noteId;
        using (var db = CreateContext(dbPath))
        {
            var note = new Note { Text = "legacy protected note" };
            db.Notes.Add(note);
            db.SaveChanges();
            noteId = note.Id;
            Assert.True(protection.ProtectNote(db, noteId, Password).Success);
        }

        Assert.Equal("PBKDF2-HMAC-SHA256|1|1000|32", ReadNoteDescriptor(dbPath, noteId));
        protection.LockNote(noteId);

        // Simulate a pre-versioning row: identical ciphertext, no descriptor column value.
        Assert.Equal(1, ExecuteNonQuery(dbPath, "UPDATE Notes SET ProtectedKdfDescriptor = NULL WHERE Id = $id;", noteId));

        using (var db = CreateContext(dbPath))
        {
            Assert.True(protection.NeedsKdfMigration(db, noteId));
            var row = db.Notes.AsNoTracking().Single(n => n.Id == noteId);
            Assert.True(protection.InspectKdfEnvelope(row).NeedsMigration);
        }

        // A differently configured service must still derive with the row's stored work factor.
        var otherProtection = new NoteProtectionService(crypto: new NoteCryptoService(TestNoteIterations + 7), attachmentStorage: storage);
        using (var db = CreateContext(dbPath))
        {
            Assert.Equal("legacy protected note", otherProtection.UnlockNote(db, noteId, Password).Text);
            Assert.True(otherProtection.NeedsKdfMigration(db, noteId));
        }
        otherProtection.LockNote(noteId);
    }

    [Fact]
    public void NoteProtection_AuthenticatedSave_MigratesDescriptorWithoutChangingCost()
    {
        string dbPath = CreateTempDb();
        var storage = new AttachmentStorageService(MakeStaticTempDir());
        using (var db = CreateContext(dbPath)) { DbInitializer.Initialize(db); }

        var protection = new NoteProtectionService(crypto: new NoteCryptoService(TestNoteIterations), attachmentStorage: storage);
        int noteId;
        using (var db = CreateContext(dbPath))
        {
            var note = new Note { Text = "before migration" };
            db.Notes.Add(note);
            db.SaveChanges();
            noteId = note.Id;
            Assert.True(protection.ProtectNote(db, noteId, Password).Success);
        }
        Assert.Equal(1, ExecuteNonQuery(dbPath, "UPDATE Notes SET ProtectedKdfDescriptor = NULL WHERE Id = $id;", noteId));
        protection.LockNote(noteId);

        using (var db = CreateContext(dbPath))
        {
            protection.UnlockNote(db, noteId, Password);
            var note = db.Notes.Single(n => n.Id == noteId);
            protection.ApplyUnlockedEdits(db, note, "after migration", null, null, null);
            db.SaveChanges();
        }

        using (var db = CreateContext(dbPath))
        {
            Assert.False(protection.NeedsKdfMigration(db, noteId));
            var note = db.Notes.AsNoTracking().Single(n => n.Id == noteId);
            // Descriptor is present, effective work factor is unchanged, plaintext stays out of SQLite.
            Assert.Equal("PBKDF2-HMAC-SHA256|1|1000|32", note.ProtectedKdfDescriptor);
            Assert.Equal(TestNoteIterations, note.ProtectedKdfIterations);
            Assert.Equal(string.Empty, note.Text);
        }

        protection.LockNote(noteId);
        using (var db = CreateContext(dbPath))
        {
            Assert.Equal("after migration", protection.UnlockNote(db, noteId, Password).Text);
        }
    }

    [Fact]
    public void NoteProtection_InterruptedMigration_RollbackLeavesLegacyEnvelopeByteForByte()
    {
        string dbPath = CreateTempDb();
        var storage = new AttachmentStorageService(MakeStaticTempDir());
        using (var db = CreateContext(dbPath)) { DbInitializer.Initialize(db); }

        var protection = new NoteProtectionService(crypto: new NoteCryptoService(TestNoteIterations), attachmentStorage: storage);
        int noteId;
        string ciphertextBefore;
        using (var db = CreateContext(dbPath))
        {
            var note = new Note { Text = "rollback source" };
            db.Notes.Add(note);
            db.SaveChanges();
            noteId = note.Id;
            Assert.True(protection.ProtectNote(db, noteId, Password).Success);
            ciphertextBefore = db.Notes.AsNoTracking().Single(n => n.Id == noteId).ProtectedCiphertextBase64 ?? string.Empty;
        }
        Assert.Equal(1, ExecuteNonQuery(dbPath, "UPDATE Notes SET ProtectedKdfDescriptor = NULL WHERE Id = $id;", noteId));
        protection.LockNote(noteId);

        using (var db = CreateContext(dbPath))
        {
            protection.UnlockNote(db, noteId, Password);
            var note = db.Notes.Single(n => n.Id == noteId);

            using var tx = db.Database.BeginTransaction();
            protection.ApplyUnlockedEdits(db, note, "should be rolled back", null, null, null);
            db.SaveChanges();
            tx.Rollback();
            db.ChangeTracker.Clear();
        }

        using (var db = CreateContext(dbPath))
        {
            var note = db.Notes.AsNoTracking().Single(n => n.Id == noteId);
            Assert.Null(note.ProtectedKdfDescriptor);
            Assert.True(protection.NeedsKdfMigration(db, noteId));
            Assert.Equal(ciphertextBefore, note.ProtectedCiphertextBase64);
        }

        protection.LockNote(noteId);
        using (var db = CreateContext(dbPath))
        {
            Assert.Equal("rollback source", protection.UnlockNote(db, noteId, Password).Text);
        }
    }

    [Fact]
    public void NoteProtection_DescriptorTamperOrUnknownAlgorithm_FailsClosed()
    {
        string dbPath = CreateTempDb();
        var storage = new AttachmentStorageService(MakeStaticTempDir());
        using (var db = CreateContext(dbPath)) { DbInitializer.Initialize(db); }

        var protection = new NoteProtectionService(crypto: new NoteCryptoService(TestNoteIterations), attachmentStorage: storage);
        int noteId;
        using (var db = CreateContext(dbPath))
        {
            var note = new Note { Text = "tamper target" };
            db.Notes.Add(note);
            db.SaveChanges();
            noteId = note.Id;
            Assert.True(protection.ProtectNote(db, noteId, Password).Success);
        }
        protection.LockNote(noteId);

        // A well-formed but tampered work factor derives a different key: AEAD fails closed.
        Assert.Equal(1, ExecuteNonQuery(dbPath,
            "UPDATE Notes SET ProtectedKdfDescriptor = 'PBKDF2-HMAC-SHA256|1|1001|32' WHERE Id = $id;", noteId));

        using (var db = CreateContext(dbPath))
        {
            Assert.Throws<NoteProtectionSecurityException>(() => protection.UnlockNote(db, noteId, Password));
            var row = db.Notes.AsNoTracking().Single(n => n.Id == noteId);
            Assert.True(protection.InspectKdfEnvelope(row).IsCurrent);
        }

        // An unknown descriptor is rejected outright, before any derivation.
        Assert.Equal(1, ExecuteNonQuery(dbPath,
            "UPDATE Notes SET ProtectedKdfDescriptor = 'ARGON2ID|1|1000|32' WHERE Id = $id;", noteId));

        KdfDiagnostics.ResetPbkdf2Invocations();
        using (var db = CreateContext(dbPath))
        {
            var row = db.Notes.AsNoTracking().Single(n => n.Id == noteId);
            Assert.Throws<NoteProtectionSecurityException>(() => protection.InspectKdfEnvelope(row));
            Assert.Throws<NoteProtectionSecurityException>(() => protection.UnlockNote(db, noteId, Password));
        }
        Assert.Equal(0, KdfDiagnostics.Pbkdf2Invocations);
    }

    [Fact]
    public void NoteProtection_LegacyNote_RevisionAndAttachmentMirrorNoteKdf_NotServiceDefault()
    {
        string dbPath = CreateTempDb();
        var storage = new AttachmentStorageService(MakeStaticTempDir());
        using (var db = CreateContext(dbPath)) { DbInitializer.Initialize(db); }

        var writer = new NoteProtectionService(crypto: new NoteCryptoService(TestNoteIterations), attachmentStorage: storage);
        int noteId;
        using (var db = CreateContext(dbPath))
        {
            var note = new Note { Text = "mirror source" };
            db.Notes.Add(note);
            db.SaveChanges();
            noteId = note.Id;
            Assert.True(writer.ProtectNote(db, noteId, Password).Success);
        }
        Assert.Equal(1, ExecuteNonQuery(dbPath, "UPDATE Notes SET ProtectedKdfDescriptor = NULL WHERE Id = $id;", noteId));
        writer.LockNote(noteId);

        // A reader configured with a different preferred work factor still unlocks the legacy row.
        var reader = new NoteProtectionService(crypto: new NoteCryptoService(TestNoteIterations + 7), attachmentStorage: storage);
        using (var db = CreateContext(dbPath))
        {
            reader.UnlockNote(db, noteId, Password);

            // Mirroring must copy the note's stored 1000, never the reader's preference of 1007.
            var revision = reader.SaveProtectedRevision(db, db.Notes.Single(n => n.Id == noteId), "history text");
            Assert.NotNull(revision);
            Assert.Equal("PBKDF2-HMAC-SHA256|1|1000|32", revision!.ProtectedKdfDescriptor);

            var attachment = AddAttachment(db, storage, noteId, RandomBytes(64), "mirror.bin");
            reader.EncryptAttachment(db, noteId, attachment, "mirror.bin", storage.GetFullPath(attachment.RelativePath));
            Assert.Equal("PBKDF2-HMAC-SHA256|1|1000|32", attachment.ProtectedKdfDescriptor);

            db.SaveChanges();
            Assert.Equal("history text", reader.DecryptRevisionText(db, noteId, revision));

            string tempFile = reader.DecryptAttachmentToTempFile(db, noteId, attachment);
            try
            {
                Assert.Equal(64, new FileInfo(tempFile).Length);
            }
            finally
            {
                ProtectedAttachmentFile.TryDeleteTempFile(tempFile);
            }
        }
        reader.LockNote(noteId);
    }

    [Fact]
    public void NoteProtection_ChangePasswordAndRemoveProtection_SetDescriptorCorrectly()
    {
        string dbPath = CreateTempDb();
        var storage = new AttachmentStorageService(MakeStaticTempDir());
        using (var db = CreateContext(dbPath)) { DbInitializer.Initialize(db); }

        var protection = new NoteProtectionService(crypto: new NoteCryptoService(TestNoteIterations), attachmentStorage: storage);
        int noteId;
        using (var db = CreateContext(dbPath))
        {
            var note = new Note { Text = "rotation source" };
            db.Notes.Add(note);
            db.SaveChanges();
            noteId = note.Id;
            Assert.True(protection.ProtectNote(db, noteId, Password).Success);
            Assert.Equal(1, ExecuteNonQuery(dbPath, "UPDATE Notes SET ProtectedKdfDescriptor = NULL WHERE Id = $id;", noteId));
        }
        protection.LockNote(noteId);

        using (var db = CreateContext(dbPath))
        {
            var rotated = protection.ChangePassword(db, noteId, Password, WrongPassword);
            Assert.True(rotated.Success, rotated.ErrorMessage);
        }

        using (var db = CreateContext(dbPath))
        {
            var note = db.Notes.AsNoTracking().Single(n => n.Id == noteId);
            Assert.Equal("PBKDF2-HMAC-SHA256|1|1000|32", note.ProtectedKdfDescriptor);
            Assert.False(protection.NeedsKdfMigration(db, noteId));
        }
        protection.LockNote(noteId);

        using (var db = CreateContext(dbPath))
        {
            Assert.Equal("rotation source", protection.UnlockNote(db, noteId, WrongPassword).Text);
            var removed = protection.RemoveProtection(db, noteId, WrongPassword);
            Assert.True(removed.Success, removed.ErrorMessage);
        }

        using (var db = CreateContext(dbPath))
        {
            var note = db.Notes.AsNoTracking().Single(n => n.Id == noteId);
            Assert.Null(note.ProtectedKdfDescriptor);
            Assert.Equal(0, note.ProtectedKdfIterations);
            Assert.False(note.IsProtected);
        }
    }

    private static NoteAttachment AddAttachment(
        QuickNotesDbContext db, IAttachmentStorageService storage, int noteId, byte[] content, string originalName)
    {
        var saved = storage.SaveFromBytes(content, originalName, 50 * 1024 * 1024);
        var attachment = new NoteAttachment
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
        db.NoteAttachments.Add(attachment);
        db.SaveChanges();
        return attachment;
    }

    // ------------------------------------------------------------------
    // 8. Schema v14: descriptor columns are additive, ciphertext untouched
    // ------------------------------------------------------------------

    [Fact]
    public void SchemaV14_AddsDescriptorColumns_WithoutTouchingExistingEnvelopes()
    {
        Assert.Equal(14, DbInitializer.CurrentSchemaVersion);

        string dbPath = CreateTempDb();
        var storage = new AttachmentStorageService(MakeStaticTempDir());
        using (var db = CreateContext(dbPath)) { DbInitializer.Initialize(db); }

        var protection = new NoteProtectionService(crypto: new NoteCryptoService(TestNoteIterations), attachmentStorage: storage);
        int noteId;
        string ciphertext;
        using (var db = CreateContext(dbPath))
        {
            var note = new Note { Text = "v14 note" };
            db.Notes.Add(note);
            db.SaveChanges();
            noteId = note.Id;
            Assert.True(protection.ProtectNote(db, noteId, Password).Success);
            ciphertext = db.Notes.AsNoTracking().Single(n => n.Id == noteId).ProtectedCiphertextBase64 ?? string.Empty;
        }

        using (var conn = new SqliteConnection(SqliteTestUtil.ConnectionString(dbPath)))
        {
            conn.Open();
            Assert.True(DbInitializer.ColumnExists(conn, "Notes", "ProtectedKdfDescriptor"));
            Assert.True(DbInitializer.ColumnExists(conn, "NoteRevisions", "ProtectedKdfDescriptor"));
            Assert.True(DbInitializer.ColumnExists(conn, "NoteAttachments", "ProtectedKdfDescriptor"));
            using var ver = conn.CreateCommand();
            ver.CommandText = "PRAGMA user_version;";
            Assert.Equal(14L, Convert.ToInt64(ver.ExecuteScalar()));
        }

        // Re-running the initializer (idempotent path) must not rewrite the envelope.
        using (var db = CreateContext(dbPath)) { DbInitializer.Initialize(db); }
        using (var db = CreateContext(dbPath))
        {
            Assert.Equal(ciphertext, db.Notes.AsNoTracking().Single(n => n.Id == noteId).ProtectedCiphertextBase64);
        }
    }

    // ------------------------------------------------------------------
    // 9. Sync fingerprints: descriptor participates, legacy shape preserved
    // ------------------------------------------------------------------

    /// <summary>
    /// Reproduces the pre-descriptor canonical note text literally. If the helper ever moves the
    /// descriptor line, this fixture fails: a null descriptor must keep the historical hash.
    /// </summary>
    private static string LegacyNoteFingerprintSource(
        string title, string text, bool pin, bool fav, bool inbox,
        Guid tagSyncId, int formatVersion, int iterations,
        string salt, string nonce, string tag, string ciphertext, Guid? originalSyncId)
    {
        var sb = new StringBuilder();
        sb.Append("NOTE\n");
        sb.Append(title).Append('\n');
        sb.Append(text).Append('\n');
        sb.Append(pin ? "1" : "0").Append('\n');
        sb.Append(fav ? "1" : "0").Append('\n');
        sb.Append(inbox ? "1" : "0").Append('\n');
        sb.Append('\n'); // sourceProcessName
        sb.Append('\n'); // sourceWindowTitle
        sb.Append('\n'); // sourceUrl
        sb.Append('\n'); // capturedAtUtc (none)
        sb.Append(tagSyncId.ToString("D")).Append("|1|0\n");
        sb.Append("1\n");
        sb.Append(formatVersion.ToString()).Append('\n');
        sb.Append(iterations.ToString()).Append('\n');
        sb.Append(salt).Append('\n');
        sb.Append(nonce).Append('\n');
        sb.Append(tag).Append('\n');
        sb.Append(ciphertext).Append('\n');
        sb.Append(originalSyncId?.ToString("D") ?? string.Empty).Append('\n');
        return sb.ToString();
    }

    private static string LegacyAttachmentFingerprintSource(
        Guid noteSyncId, string fileName, string contentType, long size, string sha256,
        int formatVersion, int iterations, string salt, string nonce, string tag, string ciphertext)
    {
        var sb = new StringBuilder();
        sb.Append("ATTACHMENT\n");
        sb.Append(noteSyncId.ToString("D")).Append('\n');
        sb.Append(fileName).Append('\n');
        sb.Append(contentType).Append('\n');
        sb.Append(size.ToString()).Append('\n');
        sb.Append(sha256).Append('\n');
        sb.Append("1\n");
        sb.Append(formatVersion.ToString()).Append('\n');
        sb.Append(iterations.ToString()).Append('\n');
        sb.Append(salt).Append('\n');
        sb.Append(nonce).Append('\n');
        sb.Append(tag).Append('\n');
        sb.Append(ciphertext).Append('\n');
        return sb.ToString();
    }

    [Fact]
    public void SyncFingerprint_LegacyNullDescriptor_KeepsHistoricalHash()
    {
        var tagSyncId = Guid.NewGuid();
        Guid originalSyncId = Guid.NewGuid();

        string legacyNote = SyncFingerprintHelper.ComputeNoteFingerprint(
            string.Empty, string.Empty, true, false, true, null, null, null, null, false,
            new[] { (tagSyncId, TagOrigin.Manual, false) },
            isProtected: true,
            protectedFormatVersion: 1,
            protectedKdfIterations: 1000,
            protectedSaltBase64: "salt",
            protectedNonceBase64: "nonce",
            protectedTagBase64: "tag",
            protectedCiphertextBase64: "cipher",
            protectedKdfDescriptor: null,
            protectedOriginalSyncId: originalSyncId);

        Assert.Equal(
            SyncFingerprintHelper.ComputeSha256(LegacyNoteFingerprintSource(
                string.Empty, string.Empty, true, false, true, tagSyncId,
                1, 1000, "salt", "nonce", "tag", "cipher", originalSyncId)),
            legacyNote);

        Guid noteSyncId = Guid.NewGuid();
        string legacyAttachment = SyncFingerprintHelper.ComputeAttachmentFingerprint(
            noteSyncId, "file.bin", "application/octet-stream", 42, "sha",
            isProtected: true,
            protectedFormatVersion: 1,
            protectedKdfIterations: 1000,
            protectedSaltBase64: "salt",
            protectedNonceBase64: "nonce",
            protectedTagBase64: "tag",
            protectedCiphertextBase64: "cipher",
            protectedKdfDescriptor: null);

        Assert.Equal(
            SyncFingerprintHelper.ComputeSha256(LegacyAttachmentFingerprintSource(
                noteSyncId, "file.bin", "application/octet-stream", 42, "sha",
                1, 1000, "salt", "nonce", "tag", "cipher")),
            legacyAttachment);
    }

    [Fact]
    public void SyncFingerprint_DescriptorChange_IsRepresented()
    {
        var tagSyncId = Guid.NewGuid();
        var tags = new[] { (tagSyncId, TagOrigin.Manual, false) };

        string withoutDescriptor = SyncFingerprintHelper.ComputeNoteFingerprint(
            string.Empty, string.Empty, false, false, false, null, null, null, null, false, tags,
            isProtected: true, protectedFormatVersion: 1, protectedKdfIterations: 1000,
            protectedSaltBase64: "salt", protectedNonceBase64: "nonce",
            protectedTagBase64: "tag", protectedCiphertextBase64: "cipher",
            protectedKdfDescriptor: null);

        // Same ciphertext and same work factor, descriptor only: must still be a distinct state.
        string withDescriptor = SyncFingerprintHelper.ComputeNoteFingerprint(
            string.Empty, string.Empty, false, false, false, null, null, null, null, false, tags,
            isProtected: true, protectedFormatVersion: 1, protectedKdfIterations: 1000,
            protectedSaltBase64: "salt", protectedNonceBase64: "nonce",
            protectedTagBase64: "tag", protectedCiphertextBase64: "cipher",
            protectedKdfDescriptor: "PBKDF2-HMAC-SHA256|1|1000|32");

        string otherDescriptor = SyncFingerprintHelper.ComputeNoteFingerprint(
            string.Empty, string.Empty, false, false, false, null, null, null, null, false, tags,
            isProtected: true, protectedFormatVersion: 1, protectedKdfIterations: 1000,
            protectedSaltBase64: "salt", protectedNonceBase64: "nonce",
            protectedTagBase64: "tag", protectedCiphertextBase64: "cipher",
            protectedKdfDescriptor: "PBKDF2-HMAC-SHA256|1|2000|32");

        Assert.NotEqual(withoutDescriptor, withDescriptor);
        Assert.NotEqual(withDescriptor, otherDescriptor);

        Guid noteSyncId = Guid.NewGuid();
        string attWithoutDescriptor = SyncFingerprintHelper.ComputeAttachmentFingerprint(
            noteSyncId, "file.bin", "application/octet-stream", 42, "sha",
            isProtected: true, protectedFormatVersion: 1, protectedKdfIterations: 1000,
            protectedSaltBase64: "salt", protectedNonceBase64: "nonce",
            protectedTagBase64: "tag", protectedCiphertextBase64: "cipher");

        string attWithDescriptor = SyncFingerprintHelper.ComputeAttachmentFingerprint(
            noteSyncId, "file.bin", "application/octet-stream", 42, "sha",
            isProtected: true, protectedFormatVersion: 1, protectedKdfIterations: 1000,
            protectedSaltBase64: "salt", protectedNonceBase64: "nonce",
            protectedTagBase64: "tag", protectedCiphertextBase64: "cipher",
            protectedKdfDescriptor: "PBKDF2-HMAC-SHA256|1|1000|32");

        Assert.NotEqual(attWithoutDescriptor, attWithDescriptor);
    }

    [Fact]
    public void SyncSnapshot_DescriptorOnlyMigration_IsADetectedLocalModification()
    {
        string dbPath = CreateTempDb();
        using (var db = CreateContext(dbPath)) { DbInitializer.Initialize(db); }

        var deviceId = Guid.NewGuid();
        var protection = new NoteProtectionService(
            crypto: new NoteCryptoService(TestNoteIterations),
            attachmentStorage: new AttachmentStorageService(MakeStaticTempDir()));

        Guid noteSyncId;
        using (var db = CreateContext(dbPath))
        {
            var note = new Note { Text = "fingerprint source" };
            db.Notes.Add(note);
            db.SaveChanges();
            Assert.True(protection.ProtectNote(db, note.Id, Password).Success);
            noteSyncId = note.SyncId;
        }

        // Legacy row: exactly as it looked before descriptor versioning.
        Assert.Equal(1, ExecuteNonQuery(dbPath, "UPDATE Notes SET ProtectedKdfDescriptor = NULL WHERE SyncId = $syncId;", 0, noteSyncId));

        using (var db = CreateContext(dbPath))
        {
            Assert.True(SyncSnapshotHelper.RegisterLocalModifications(db, deviceId));
            db.SaveChanges();
            Assert.False(SyncSnapshotHelper.HasPendingLocalModifications(db, deviceId));
        }

        // Publishing the descriptor without touching ciphertext is still a real state change.
        using (var db = CreateContext(dbPath))
        {
            var note = db.Notes.Single(n => n.SyncId == noteSyncId);
            note.ProtectedKdfDescriptor = "PBKDF2-HMAC-SHA256|1|1000|32";
            db.SaveChanges();
            Assert.True(SyncSnapshotHelper.HasPendingLocalModifications(db, deviceId));
        }

        protection.LockNote(1);
    }

    [Fact]
    public void SyncPackage_NullDescriptor_IsOmittedFromJson_AndNewWritesEmitIt()
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        var legacyEnvelope = new SyncPackageEnvelope
        {
            Magic = "QNSP",
            FormatVersion = 1,
            PackageId = Guid.NewGuid(),
            DeviceId = Guid.NewGuid(),
            Crypto = new SyncCryptoHeader
            {
                Algorithm = SyncCryptoService.AlgorithmName,
                KdfAlgorithm = SyncCryptoService.KdfName,
                KdfVersion = SyncCryptoService.KdfVersion,
                KdfIterations = 1000,
                KdfDescriptor = null
            }
        };

        Assert.DoesNotContain("kdfDescriptor", JsonSerializer.Serialize(legacyEnvelope, options), StringComparison.OrdinalIgnoreCase);

        var payload = new SyncPackagePayload
        {
            Notes = { new SyncNoteDto { SyncId = Guid.NewGuid() } },
            Attachments = { new SyncAttachmentDto { SyncId = Guid.NewGuid(), ProtectedKdfDescriptor = null } }
        };
        string payloadJson = JsonSerializer.Serialize(payload, options);
        Assert.DoesNotContain("protectedKdfDescriptor", payloadJson, StringComparison.OrdinalIgnoreCase);

        // A current writer still emits the descriptor it will actually be validated against.
        var currentEnvelope = new SyncPackageEnvelope
        {
            Magic = "QNSP",
            FormatVersion = 1,
            PackageId = Guid.NewGuid(),
            DeviceId = Guid.NewGuid(),
            Crypto = new SyncCryptoHeader
            {
                Algorithm = SyncCryptoService.AlgorithmName,
                KdfAlgorithm = SyncCryptoService.KdfName,
                KdfVersion = SyncCryptoService.KdfVersion,
                KdfIterations = 1000,
                KdfDescriptor = "PBKDF2-HMAC-SHA256|1|1000|32"
            }
        };
        Assert.Contains("kdfDescriptor", JsonSerializer.Serialize(currentEnvelope, options), StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------
    // 10. QNAT descriptor must agree with the owning note/session
    // ------------------------------------------------------------------

    [Fact]
    public void QnatV2_ExpectedDescriptorMismatch_IsRejectedBeforeDecrypt()
    {
        var crypto = new NoteCryptoService(TestNoteIterations);
        var syncId = Guid.NewGuid();
        byte[] plaintext = Encoding.UTF8.GetBytes("owner-bound attachment");
        byte[] key = crypto.DeriveKey(Password, new byte[NoteCryptoService.SaltByteSize], TestNoteIterations);
        var owner = KdfDescriptor.Pbkdf2(TestNoteIterations);

        byte[] v2 = ProtectedAttachmentFile.CreateContainer(
            key, plaintext, crypto.CurrentFormatVersion, syncId, owner);

        // Matching owner descriptor: readable.
        Assert.Equal(plaintext, ProtectedAttachmentFile.DecryptContainer(
            key, v2, crypto.CurrentFormatVersion, syncId, owner));

        // Different work factor recorded on the owning note: rejected before AES-GCM.
        Assert.Throws<NoteProtectionSecurityException>(() => ProtectedAttachmentFile.DecryptContainer(
            key, v2, crypto.CurrentFormatVersion, syncId, KdfDescriptor.Pbkdf2(TestNoteIterations + 1)));

        // Different descriptor version / algorithm: rejected too.
        Assert.Throws<NoteProtectionSecurityException>(() => ProtectedAttachmentFile.DecryptContainer(
            key, v2, crypto.CurrentFormatVersion, syncId, new KdfDescriptor(KdfAlgorithmIds.Pbkdf2HmacSha256, 2, TestNoteIterations, 32)));
        Assert.Throws<NoteProtectionSecurityException>(() => ProtectedAttachmentFile.DecryptContainer(
            key, v2, crypto.CurrentFormatVersion, syncId, new KdfDescriptor("ARGON2ID", 1, TestNoteIterations, 32)));

        // Tampering with the stored work factor still fails closed against the owner descriptor.
        byte[] tampered = (byte[])v2.Clone();
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(
            tampered.AsSpan(4 + 1 + 1 + 2), TestNoteIterations + 1);
        Assert.Throws<NoteProtectionSecurityException>(() => ProtectedAttachmentFile.DecryptContainer(
            key, tampered, crypto.CurrentFormatVersion, syncId, owner));
    }

    [Fact]
    public void QnatV1_IgnoresOwnerDescriptorExpectation()
    {
        var crypto = new NoteCryptoService(TestNoteIterations);
        var syncId = Guid.NewGuid();
        byte[] plaintext = Encoding.UTF8.GetBytes("legacy attachment");
        byte[] key = crypto.DeriveKey(Password, new byte[NoteCryptoService.SaltByteSize], TestNoteIterations);
        byte[] v1 = BuildLegacyV1Qnat(key, plaintext, crypto.CurrentFormatVersion, syncId);

        // v1 carries no descriptor, so it stays readable; the caller still supplies the owner contract.
        Assert.Equal(plaintext, ProtectedAttachmentFile.DecryptContainer(
            key, v1, crypto.CurrentFormatVersion, syncId, KdfDescriptor.Pbkdf2(TestNoteIterations)));
        Assert.Equal(plaintext, ProtectedAttachmentFile.DecryptContainer(
            key, v1, crypto.CurrentFormatVersion, syncId, KdfDescriptor.Pbkdf2(TestNoteIterations + 5)));
    }

    [Fact]
    public void NoteProtection_AttachmentDecrypt_BindsContainerToOwningNoteDescriptor()
    {
        string dbPath = CreateTempDb();
        var storage = new AttachmentStorageService(MakeStaticTempDir());
        using (var db = CreateContext(dbPath)) { DbInitializer.Initialize(db); }

        var protection = new NoteProtectionService(crypto: new NoteCryptoService(TestNoteIterations), attachmentStorage: storage);
        int noteId;
        using (var db = CreateContext(dbPath))
        {
            var note = new Note { Text = "owner binding" };
            db.Notes.Add(note);
            db.SaveChanges();
            noteId = note.Id;
            Assert.True(protection.ProtectNote(db, noteId, Password).Success);

            var attachment = AddAttachment(db, storage, noteId, RandomBytes(64), "bound.bin");
            protection.EncryptAttachment(db, noteId, attachment, "bound.bin", storage.GetFullPath(attachment.RelativePath));
            db.SaveChanges();

            Assert.Equal("PBKDF2-HMAC-SHA256|1|1000|32", attachment.ProtectedKdfDescriptor);
            string fullPath = storage.GetFullPath(attachment.RelativePath);
            byte[] container = File.ReadAllBytes(fullPath);
            Assert.Equal(0x02, container[4]);

            // Rewrite the stored work factor: the container no longer agrees with the row.
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(
                container.AsSpan(4 + 1 + 1 + 2), TestNoteIterations + 1);
            File.WriteAllBytes(fullPath, container);
        }

        using (var db = CreateContext(dbPath))
        {
            protection.UnlockNote(db, noteId, Password);
            var attachment = db.NoteAttachments.Single(a => a.NoteId == noteId);
            Assert.Throws<NoteProtectionSecurityException>(
                () => protection.DecryptAttachmentToTempFile(db, noteId, attachment));
        }

        protection.LockNote(noteId);
    }

    private sealed class FixedDeviceId : IDeviceIdProvider
    {
        private readonly Guid _id;
        public FixedDeviceId(Guid id) { _id = id; }
        public Guid GetDeviceId() => _id;
    }

    /// <summary>Transport is never used by the envelope-only blob tests.</summary>
    private sealed class NullTransport : ICloudObjectStoreTransport
    {
        public System.Threading.Tasks.Task<bool> TestConnectionAsync(System.Threading.CancellationToken ct = default)
            => System.Threading.Tasks.Task.FromResult(true);
        public System.Threading.Tasks.Task<StorageObjectMetadata?> HeadObjectAsync(string key, System.Threading.CancellationToken ct = default)
            => throw new NotSupportedException();
        public System.Threading.Tasks.Task<StorageObjectResult?> GetObjectAsync(string key, System.Threading.CancellationToken ct = default)
            => throw new NotSupportedException();
        public System.Threading.Tasks.Task<StorageObjectMetadata> PutImmutableObjectAsync(string key, byte[] content, string contentType = "application/json", System.Threading.CancellationToken ct = default)
            => throw new NotSupportedException();
        public System.Threading.Tasks.Task<StorageObjectMetadata> PutConditionalPointerAsync(string key, byte[] content, string? expectedETag, string contentType = "application/json", System.Threading.CancellationToken ct = default)
            => throw new NotSupportedException();
        public System.Threading.Tasks.Task<IReadOnlyList<StorageObjectSummary>> ListObjectsAsync(string? prefix = null, int maxKeys = 1000, System.Threading.CancellationToken ct = default)
            => throw new NotSupportedException();
        public System.Threading.Tasks.Task<StorageListResult> ListObjectsV2Async(StorageListRequest request, System.Threading.CancellationToken ct = default)
            => throw new NotSupportedException();
        public System.Threading.Tasks.Task<bool> DeleteObjectAsync(string key, System.Threading.CancellationToken ct = default)
            => throw new NotSupportedException();
        public void Dispose() { }
    }
}
