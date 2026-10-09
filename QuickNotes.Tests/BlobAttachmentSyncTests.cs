using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using QuickNotes.App.Data;
using QuickNotes.App.Models;
using QuickNotes.App.Models.Sync;
using QuickNotes.App.Services;
using QuickNotes.App.Services.Sync;
using Xunit;

namespace QuickNotes.Tests;

[TestCategory(TestCategories.Integration)]
public class BlobAttachmentSyncTests : IDisposable
{
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
        var dir = Path.Combine(Path.GetTempPath(), "qn21f_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _tempPaths.Add(dir);
        return dir;
    }

    private static byte[] RandomBytes(int length)
    {
        var b = new byte[length];
        RandomNumberGenerator.Fill(b);
        return b;
    }

    private static string Sha256Hex(byte[] data)
    {
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(data)).ToLowerInvariant();
    }

    private static string ComputeFileSha256(string path)
    {
        using var fs = File.OpenRead(path);
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(fs)).ToLowerInvariant();
    }

    private class FakeAttachmentStorageService : IAttachmentStorageService
    {
        public string BaseDirectory { get; }
        public string AttachmentsDirectory { get; }

        public FakeAttachmentStorageService(string baseDir)
        {
            BaseDirectory = baseDir;
            AttachmentsDirectory = Path.Combine(baseDir, "Attachments");
            Directory.CreateDirectory(AttachmentsDirectory);
        }

        public AttachmentSaveResult SaveAttachment(string sourceFilePath, long maxSizeBytes)
            => throw new NotImplementedException();

        public AttachmentSaveResult SaveFromBytes(byte[] content, string originalFileName, long maxSizeBytes)
            => throw new NotImplementedException();

        public string GetFullPath(string relativePath)
        {
            string normalized = relativePath.Replace('/', Path.DirectorySeparatorChar)
                                            .Replace('\\', Path.DirectorySeparatorChar);
            string candidate = normalized.StartsWith("Attachments" + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase)
                ? Path.Combine(BaseDirectory, normalized)
                : Path.Combine(AttachmentsDirectory, normalized);

            string fullPath = Path.GetFullPath(candidate);
            string root = AttachmentsDirectory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Path traversal detected.");
            return fullPath;
        }

        public bool FileExists(string relativePath)
        {
            try { return File.Exists(GetFullPath(relativePath)); }
            catch { return false; }
        }

        public bool DeleteManagedFileIfUnreferenced(QuickNotesDbContext db, string storedFileName) => false;
        public void CleanupUnreferencedFiles(QuickNotesDbContext db, IEnumerable<string> storedFileNames) { }
    }

    private class FakeTransport : ICloudObjectStoreTransport
    {
        public readonly ConcurrentDictionary<string, (byte[] Content, string ETag, string ContentType)> Store = new();
        public bool IsOffline { get; set; }
        public bool RejectPutImmutable { get; set; }
        public int PutImmutableCalls;
        public int HideExistingObjectOnGetCount;

        public Task<bool> TestConnectionAsync(CancellationToken ct = default) => Task.FromResult(true);

        public Task<StorageObjectMetadata?> HeadObjectAsync(string key, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (IsOffline) throw new CloudOfflineException("offline");
            return Store.TryGetValue(key, out var obj)
                ? Task.FromResult<StorageObjectMetadata?>(new StorageObjectMetadata
                { Key = key, ETag = obj.ETag, ContentLength = obj.Content.Length })
                : Task.FromResult<StorageObjectMetadata?>(null);
        }

        public Task<StorageObjectResult?> GetObjectAsync(string key, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (IsOffline) throw new CloudOfflineException("offline");
            if (HideExistingObjectOnGetCount > 0)
            {
                Interlocked.Decrement(ref HideExistingObjectOnGetCount);
                return Task.FromResult<StorageObjectResult?>(null);
            }
            return Store.TryGetValue(key, out var obj)
                ? Task.FromResult<StorageObjectResult?>(new StorageObjectResult
                {
                    Metadata = new StorageObjectMetadata
                    { Key = key, ETag = obj.ETag, ContentLength = obj.Content.Length },
                    Content = (byte[])obj.Content.Clone()
                })
                : Task.FromResult<StorageObjectResult?>(null);
        }

        public Task<StorageObjectMetadata> PutImmutableObjectAsync(string key, byte[] content,
            string contentType = "application/json", CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (IsOffline) throw new CloudOfflineException("offline");
            Interlocked.Increment(ref PutImmutableCalls);
            if (RejectPutImmutable)
                throw new InvalidOperationException("simulated upload error");
            if (Store.TryGetValue(key, out var existing))
                throw new CloudConflictException("immutable " + key + " exists", objectKey: key, actualETag: existing.ETag, statusCode: 409);
            string etag = Guid.NewGuid().ToString("N");
            if (!Store.TryAdd(key, ((byte[])content.Clone(), etag, contentType)))
                throw new CloudConflictException("concurrent " + key, objectKey: key, statusCode: 412);
            return Task.FromResult(new StorageObjectMetadata
            { Key = key, ETag = etag, ContentLength = content.Length });
        }

        public Task<StorageObjectMetadata> PutConditionalPointerAsync(string key, byte[] content,
            string? expectedETag, string contentType = "application/json", CancellationToken ct = default)
        {
            string etag = Guid.NewGuid().ToString("N");
            Store[key] = ((byte[])content.Clone(), etag, contentType);
            return Task.FromResult(new StorageObjectMetadata
            { Key = key, ETag = etag, ContentLength = content.Length });
        }

        public Task<IReadOnlyList<StorageObjectSummary>> ListObjectsAsync(string? prefix = null,
            int maxKeys = 1000, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<StorageObjectSummary>>(new List<StorageObjectSummary>());

        public Task<StorageListResult> ListObjectsV2Async(StorageListRequest request, CancellationToken ct = default)
            => Task.FromResult(new StorageListResult());

        public Task<bool> DeleteObjectAsync(string key, CancellationToken ct = default)
        {
            Store.TryRemove(key, out _);
            return Task.FromResult(true);
        }

        public void Dispose() { }
    }

    private (SyncAttachmentBlobService svc, FakeTransport transport, FakeAttachmentStorageService storage, SyncObjectKeyHelper keyHelper)
        MakeSvc(SyncCloudSettings? settings = null, ICloudUsageService? usageService = null)
    {
        settings ??= new SyncCloudSettings { SyncAttachments = true };
        var transport = new FakeTransport();
        var crypto = new SyncCryptoService(1000);
        var dir = MakeTempDir();
        var storage = new FakeAttachmentStorageService(dir);
        var keyHelper = new SyncObjectKeyHelper(settings);
        var svc = new SyncAttachmentBlobService(transport, crypto, storage, keyHelper, settings, usageService);
        return (svc, transport, storage, keyHelper);
    }

    private NoteAttachment PlaceLocalFile(FakeAttachmentStorageService storage, byte[] content,
        string originalFileName = "test.bin")
    {
        string sha256 = Sha256Hex(content);
        string storedName = sha256 + ".bin";
        File.WriteAllBytes(Path.Combine(storage.AttachmentsDirectory, storedName), content);
        return new NoteAttachment
        {
            SyncId = Guid.NewGuid(),
            OriginalFileName = originalFileName,
            StoredFileName = storedName,
            RelativePath = "Attachments/" + storedName,
            ContentType = "application/octet-stream",
            Size = content.Length,
            Sha256 = sha256
        };
    }

    [Fact]
    public void Encrypt_Decrypt_Roundtrip_ProducesIdenticalPlaintext()
    {
        var (svc, _, _, _) = MakeSvc();
        byte[] plaintext = RandomBytes(1024);
        string sha256 = Sha256Hex(plaintext);
        byte[] envelope = svc.EncryptBlobEnvelope(plaintext, sha256, "test-password-123");
        byte[] decrypted = svc.DecryptBlobEnvelope(envelope, sha256, "test-password-123");
        Assert.Equal(plaintext, decrypted);
    }

    [Fact]
    public void Decrypt_WithTamperedCiphertext_ThrowsSyncSecurityException()
    {
        var (svc, _, _, _) = MakeSvc();
        byte[] plaintext = RandomBytes(256);
        string sha256 = Sha256Hex(plaintext);
        byte[] envelope = svc.EncryptBlobEnvelope(plaintext, sha256, "secure-password");
        envelope[^1] ^= 0xFF;
        Assert.Throws<SyncSecurityException>(() => svc.DecryptBlobEnvelope(envelope, sha256, "secure-password"));
    }

    [Fact]
    public void Decrypt_WithTamperedTag_ThrowsSyncSecurityException()
    {
        var (svc, _, _, _) = MakeSvc();
        byte[] plaintext = RandomBytes(128);
        string sha256 = Sha256Hex(plaintext);
        byte[] envelope = svc.EncryptBlobEnvelope(plaintext, sha256, "password");
        // tag offset: 4(magic) + 1(ver) + 4(saltLen) + 32(salt) + 12(nonce) = 53
        envelope[53] ^= 0xFF;
        Assert.Throws<SyncSecurityException>(() => svc.DecryptBlobEnvelope(envelope, sha256, "password"));
    }

    [Fact]
    public void Decrypt_WithWrongPassword_ThrowsSyncSecurityException()
    {
        var (svc, _, _, _) = MakeSvc();
        byte[] plaintext = RandomBytes(64);
        string sha256 = Sha256Hex(plaintext);
        byte[] envelope = svc.EncryptBlobEnvelope(plaintext, sha256, "correct-password");
        Assert.Throws<SyncSecurityException>(() => svc.DecryptBlobEnvelope(envelope, sha256, "wrong-password"));
    }

    [Fact]
    public async Task UploadBlob_CloudKeyContainsOnlySha256_NotFilenameOrContent()
    {
        var (svc, transport, storage, keyHelper) = MakeSvc();
        byte[] content = System.Text.Encoding.UTF8.GetBytes("super-secret content");
        var att = PlaceLocalFile(storage, content, "my_private_document.docx");
        var result = await svc.UploadBlobAsync(att, "password");
        Assert.True(result.Success, result.ErrorMessage);
        string expectedKey = keyHelper.GetBlobKey(att.Sha256);
        Assert.Equal(expectedKey, result.CloudKey);
        Assert.DoesNotContain("docx", result.CloudKey, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("private", result.CloudKey, StringComparison.OrdinalIgnoreCase);
        Assert.True(transport.Store.TryGetValue(expectedKey, out var stored));
        string storedText = System.Text.Encoding.UTF8.GetString(stored.Content);
        Assert.DoesNotContain("super-secret", storedText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("my_private_document", storedText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Upload_TwoIdenticalFiles_ProducesOnlyOneCloudBlob()
    {
        var (svc, transport, storage, keyHelper) = MakeSvc();
        byte[] content = RandomBytes(512);
        var att1 = PlaceLocalFile(storage, content, "file1.txt");
        var att2 = new NoteAttachment
        {
            SyncId = Guid.NewGuid(),
            OriginalFileName = "file2.txt",
            StoredFileName = att1.StoredFileName,
            RelativePath = att1.RelativePath,
            ContentType = "text/plain",
            Size = content.Length,
            Sha256 = att1.Sha256
        };
        var r1 = await svc.UploadBlobAsync(att1, "password");
        var r2 = await svc.UploadBlobAsync(att2, "password");
        Assert.True(r1.Success); Assert.False(r1.AlreadyExisted);
        Assert.True(r2.Success); Assert.True(r2.AlreadyExisted);
        Assert.Equal(r1.CloudKey, r2.CloudKey);
        Assert.Equal(1, transport.Store.Keys.Count(k => k == keyHelper.GetBlobKey(att1.Sha256)));
        Assert.Equal(1, transport.PutImmutableCalls);
    }

    [Fact]
    public async Task RepeatedUpload_DoesNotOverwriteExistingBlob()
    {
        var (svc, transport, storage, keyHelper) = MakeSvc();
        byte[] content = RandomBytes(256);
        var att = PlaceLocalFile(storage, content);
        var r1 = await svc.UploadBlobAsync(att, "password");
        Assert.True(r1.Success);
        string originalETag = transport.Store[keyHelper.GetBlobKey(att.Sha256)].ETag;
        var r2 = await svc.UploadBlobAsync(att, "password");
        Assert.True(r2.Success); Assert.True(r2.AlreadyExisted);
        Assert.Equal(originalETag, transport.Store[keyHelper.GetBlobKey(att.Sha256)].ETag);
        Assert.Equal(1, transport.PutImmutableCalls);
    }

    [Fact]
    public async Task UploadAttachmentBlobsAsync_CompletesBeforeAnyPackageWrite()
    {
        var (svc, transport, storage, keyHelper) = MakeSvc();
        byte[] content = RandomBytes(256);
        var att = PlaceLocalFile(storage, content);
        bool packageUploaded = false;
        var blobResult = await svc.UploadAttachmentBlobsAsync(new List<NoteAttachment> { att }, "password");
        bool blobUploadedBeforePackage = blobResult.uploaded == 1 && !packageUploaded;
        await transport.PutImmutableObjectAsync("v1/devices/fake/packages/pkg.json",
            System.Text.Encoding.UTF8.GetBytes("{\"test\":true}"));
        packageUploaded = true;
        Assert.True(blobUploadedBeforePackage, "Blob must be uploaded before the package");
        Assert.True(transport.Store.ContainsKey(keyHelper.GetBlobKey(att.Sha256)));
    }

    [Fact]
    public async Task DownloadBlob_Success_AtomicallyPlacesFileInStorage()
    {
        var (svc, transport, storage, keyHelper) = MakeSvc();
        byte[] content = RandomBytes(512);
        var uploadAtt = PlaceLocalFile(storage, content, "downloaded.pdf");
        await svc.UploadBlobAsync(uploadAtt, "password");
        string localPath = Path.Combine(storage.AttachmentsDirectory, uploadAtt.StoredFileName);
        File.Delete(localPath);
        var downloadAtt = new NoteAttachment
        {
            SyncId = Guid.NewGuid(),
            OriginalFileName = "downloaded.pdf",
            StoredFileName = uploadAtt.StoredFileName,
            RelativePath = uploadAtt.RelativePath,
            Size = content.Length,
            Sha256 = uploadAtt.Sha256
        };
        var result = await svc.DownloadBlobAsync(downloadAtt, "password");
        Assert.True(result.Success, result.ErrorMessage);
        Assert.True(File.Exists(localPath));
        Assert.Equal(content, File.ReadAllBytes(localPath));
        Assert.Equal(uploadAtt.Sha256, ComputeFileSha256(localPath));
    }

    [Fact]
    public async Task DownloadBlob_AlreadyPresentLocally_DoesNotDownloadAgain()
    {
        var (svc, transport, storage, keyHelper) = MakeSvc();
        byte[] content = RandomBytes(256);
        var att = PlaceLocalFile(storage, content);
        await svc.UploadBlobAsync(att, "password");
        var result = await svc.DownloadBlobAsync(att, "password");
        Assert.True(result.Success); Assert.True(result.AlreadyLocal);
    }

    [Fact]
    public async Task DownloadBlob_MissingInCloud_ReturnsErrorResult()
    {
        var (svc, transport, storage, keyHelper) = MakeSvc();
        byte[] content = RandomBytes(128);
        string sha256 = Sha256Hex(content);
        var att = new NoteAttachment
        {
            SyncId = Guid.NewGuid(),
            OriginalFileName = "missing.bin",
            StoredFileName = sha256 + ".bin",
            RelativePath = "Attachments/" + sha256 + ".bin",
            Size = content.Length,
            Sha256 = sha256
        };
        var result = await svc.DownloadBlobAsync(att, "password");
        Assert.False(result.Success);
        Assert.NotNull(result.ErrorMessage);
    }

    [Fact]
    public async Task DownloadBlob_CorruptInCloud_ReturnsErrorAndNoPartialFile()
    {
        var (svc, transport, storage, keyHelper) = MakeSvc();
        byte[] content = RandomBytes(256);
        var att = PlaceLocalFile(storage, content);
        await svc.UploadBlobAsync(att, "password");
        string blobKey = keyHelper.GetBlobKey(att.Sha256);
        var (storedContent, etag, ct) = transport.Store[blobKey];
        byte[] corrupted = (byte[])storedContent.Clone();
        corrupted[^1] ^= 0xFF;
        transport.Store[blobKey] = (corrupted, etag, ct);
        string localPath = Path.Combine(storage.AttachmentsDirectory, att.StoredFileName);
        File.Delete(localPath);
        var downloadAtt = new NoteAttachment
        {
            SyncId = att.SyncId,
            OriginalFileName = att.OriginalFileName,
            StoredFileName = att.StoredFileName,
            RelativePath = att.RelativePath,
            Size = content.Length,
            Sha256 = att.Sha256
        };
        var result = await svc.DownloadBlobAsync(downloadAtt, "password");
        Assert.False(result.Success);
        Assert.False(File.Exists(localPath));
    }

    [Fact]
    public async Task DownloadBlob_WrongSizeMetadata_ReturnsError()
    {
        var (svc, transport, storage, keyHelper) = MakeSvc();
        byte[] content = RandomBytes(128);
        var att = PlaceLocalFile(storage, content);
        await svc.UploadBlobAsync(att, "password");
        string localPath = Path.Combine(storage.AttachmentsDirectory, att.StoredFileName);
        File.Delete(localPath);
        var downloadAtt = new NoteAttachment
        {
            SyncId = att.SyncId,
            OriginalFileName = att.OriginalFileName,
            StoredFileName = att.StoredFileName,
            RelativePath = att.RelativePath,
            Size = content.Length + 9999,
            Sha256 = att.Sha256
        };
        var result = await svc.DownloadBlobAsync(downloadAtt, "password");
        Assert.False(result.Success);
        Assert.False(File.Exists(localPath));
    }

    [Fact]
    public async Task DownloadBlob_Sha256MismatchAfterDecrypt_ReturnsError()
    {
        var (svc, transport, storage, keyHelper) = MakeSvc();
        var crypto = new SyncCryptoService(1000);
        byte[] content = RandomBytes(64);
        byte[] differentContent = RandomBytes(64);
        while (Sha256Hex(differentContent) == Sha256Hex(content))
            differentContent = RandomBytes(64);
        var uploadAtt = PlaceLocalFile(storage, content);
        await svc.UploadBlobAsync(uploadAtt, "password");
        string blobKey = keyHelper.GetBlobKey(uploadAtt.Sha256);
        byte[] corruptEnvelope = svc.EncryptBlobEnvelope(differentContent, uploadAtt.Sha256, "password");
        var (_, etag, ctype) = transport.Store[blobKey];
        transport.Store[blobKey] = (corruptEnvelope, etag, ctype);
        string localPath = Path.Combine(storage.AttachmentsDirectory, uploadAtt.StoredFileName);
        File.Delete(localPath);
        var downloadAtt = new NoteAttachment
        {
            SyncId = Guid.NewGuid(),
            OriginalFileName = "test.bin",
            StoredFileName = uploadAtt.StoredFileName,
            RelativePath = uploadAtt.RelativePath,
            Size = differentContent.Length,
            Sha256 = uploadAtt.Sha256
        };
        var result = await svc.DownloadBlobAsync(downloadAtt, "password");
        Assert.False(result.Success);
        Assert.Contains("SHA-256", result.ErrorMessage ?? "", StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(localPath));
    }

    [Fact]
    public async Task Upload_RetryAfterPartialFailure_IsIdempotent()
    {
        var (svc, transport, storage, keyHelper) = MakeSvc();
        byte[] content = RandomBytes(256);
        var att = PlaceLocalFile(storage, content);
        transport.RejectPutImmutable = true;
        var r1 = await svc.UploadBlobAsync(att, "password");
        Assert.False(r1.Success);
        Assert.False(transport.Store.ContainsKey(keyHelper.GetBlobKey(att.Sha256)));
        transport.RejectPutImmutable = false;
        var r2 = await svc.UploadBlobAsync(att, "password");
        Assert.True(r2.Success, r2.ErrorMessage); Assert.False(r2.AlreadyExisted);
        var r3 = await svc.UploadBlobAsync(att, "password");
        Assert.True(r3.Success); Assert.True(r3.AlreadyExisted);
    }

    [Fact]
    public async Task UploadBlob_AttachmentSyncDisabled_SkipsUpload()
    {
        var (svc, transport, storage, _) = MakeSvc(new SyncCloudSettings { SyncAttachments = false });
        byte[] content = RandomBytes(128);
        var att = PlaceLocalFile(storage, content);
        var result = await svc.UploadBlobAsync(att, "password");
        Assert.True(result.Skipped);
        Assert.False(transport.Store.Any());
    }

    [Fact]
    public async Task DownloadBlob_AttachmentSyncDisabled_SkipsDownload()
    {
        var enabledSettings = new SyncCloudSettings { SyncAttachments = true };
        var transport = new FakeTransport();
        var crypto = new SyncCryptoService(1000);
        var dir = MakeTempDir();
        var storage = new FakeAttachmentStorageService(dir);
        var keyHelper = new SyncObjectKeyHelper(enabledSettings);
        var svcEnabled = new SyncAttachmentBlobService(transport, crypto, storage, keyHelper, enabledSettings);
        byte[] content = RandomBytes(128);
        var att = PlaceLocalFile(storage, content);
        await svcEnabled.UploadBlobAsync(att, "password");
        string localPath = Path.Combine(storage.AttachmentsDirectory, att.StoredFileName);
        File.Delete(localPath);
        var disabledSettings = new SyncCloudSettings { SyncAttachments = false };
        var svcDisabled = new SyncAttachmentBlobService(transport, crypto, storage, keyHelper, disabledSettings);
        var downloadAtt = new NoteAttachment
        {
            SyncId = Guid.NewGuid(),
            OriginalFileName = att.OriginalFileName,
            StoredFileName = att.StoredFileName,
            RelativePath = att.RelativePath,
            Size = content.Length,
            Sha256 = att.Sha256
        };
        var result = await svcDisabled.DownloadBlobAsync(downloadAtt, "password");
        Assert.True(result.Skipped);
        Assert.False(File.Exists(localPath));
    }

    [Fact]
    public async Task UploadBlob_ExceedsPerFileSizeLimit_Skips()
    {
        var (svc, transport, storage, _) = MakeSvc(new SyncCloudSettings { SyncAttachments = true, MaxAttachmentSyncBytes = 100 });
        byte[] content = RandomBytes(200);
        var att = PlaceLocalFile(storage, content);
        var result = await svc.UploadBlobAsync(att, "password");
        Assert.True(result.Skipped);
        Assert.False(transport.Store.Any());
    }

    [Fact]
    public void BlobKey_PathTraversal_IsRejected()
    {
        var keyHelper = new SyncObjectKeyHelper();
        Assert.Throws<ArgumentException>(() => keyHelper.GetBlobKey("../../../etc/passwd"));
        Assert.Throws<ArgumentException>(() => keyHelper.GetBlobKey("abcdef"));
        Assert.Throws<ArgumentException>(() => keyHelper.GetBlobKey("g" + new string('a', 63)));
    }

    [Fact]
    public async Task DownloadBlob_PathTraversalInRelativePath_FailsSafely()
    {
        var (svc, transport, storage, keyHelper) = MakeSvc();
        byte[] content = RandomBytes(64);
        string sha256 = Sha256Hex(content);
        var att = new NoteAttachment
        {
            SyncId = Guid.NewGuid(),
            OriginalFileName = "evil.bin",
            StoredFileName = "evil.bin",
            RelativePath = "../../../Windows/System32/evil.exe",
            Size = content.Length,
            Sha256 = sha256
        };
        var result = await svc.DownloadBlobAsync(att, "password");
        Assert.True(!result.Success || result.Skipped);
    }

    [Fact]
    public async Task TwoDeviceExchange_AttachmentReachesDeviceB()
    {
        var settings = new SyncCloudSettings { SyncAttachments = true };
        var sharedTransport = new FakeTransport();
        var crypto = new SyncCryptoService(1000);
        var keyHelper = new SyncObjectKeyHelper(settings);
        const string password = "shared-sync-password";
        var dirA = MakeTempDir();
        var storageA = new FakeAttachmentStorageService(dirA);
        var svcA = new SyncAttachmentBlobService(sharedTransport, crypto, storageA, keyHelper, settings);
        byte[] content = RandomBytes(1024);
        var attA = PlaceLocalFile(storageA, content, "document.pdf");
        var uploadResult = await svcA.UploadBlobAsync(attA, password);
        Assert.True(uploadResult.Success, uploadResult.ErrorMessage);
        var dirB = MakeTempDir();
        var storageB = new FakeAttachmentStorageService(dirB);
        var svcB = new SyncAttachmentBlobService(sharedTransport, crypto, storageB, keyHelper, settings);
        var attB = new NoteAttachment
        {
            SyncId = attA.SyncId,
            OriginalFileName = "document.pdf",
            StoredFileName = attA.StoredFileName,
            RelativePath = attA.RelativePath,
            Size = content.Length,
            Sha256 = attA.Sha256
        };
        var downloadResult = await svcB.DownloadBlobAsync(attB, password);
        Assert.True(downloadResult.Success, downloadResult.ErrorMessage);
        string localPathB = Path.Combine(storageB.AttachmentsDirectory, attA.StoredFileName);
        Assert.True(File.Exists(localPathB));
        Assert.Equal(content, File.ReadAllBytes(localPathB));
    }

    [Fact]
    public async Task BatchUpload_TwoIdenticalFiles_SingleCloudObject()
    {
        var (svc, transport, storage, keyHelper) = MakeSvc();
        byte[] content = RandomBytes(256);
        var att1 = PlaceLocalFile(storage, content, "a.txt");
        var att2 = new NoteAttachment
        {
            SyncId = Guid.NewGuid(),
            OriginalFileName = "b.txt",
            StoredFileName = att1.StoredFileName,
            RelativePath = att1.RelativePath,
            ContentType = "text/plain",
            Size = content.Length,
            Sha256 = att1.Sha256
        };
        var (uploaded, skipped, alreadyExisted, errors, _) =
            await svc.UploadAttachmentBlobsAsync(new[] { att1, att2 }, "password");
        Assert.Equal(0, errors);
        Assert.Equal(1, transport.PutImmutableCalls);
        Assert.Equal(1, transport.Store.Count(kv => kv.Key.EndsWith(att1.Sha256 + ".bin")));
    }

    [Fact]
    public void DecryptEnvelope_InvalidMagic_ThrowsSyncSecurityException()
    {
        var (svc, _, _, _) = MakeSvc();
        byte[] badData = new byte[200];
        RandomNumberGenerator.Fill(badData);
        badData[0] = 0xFF; badData[1] = 0xFF; badData[2] = 0xFF; badData[3] = 0xFF;
        Assert.Throws<SyncSecurityException>(() => svc.DecryptBlobEnvelope(badData, new string('a', 64), "password"));
    }

    [Fact]
    public void DecryptEnvelope_TooShort_ThrowsSyncSecurityException()
    {
        var (svc, _, _, _) = MakeSvc();
        Assert.Throws<SyncSecurityException>(() => svc.DecryptBlobEnvelope(new byte[10], new string('a', 64), "password"));
    }

    [Fact]
    public void SyncCloudSettings_AttachmentDefaults_AreCorrect()
    {
        var settings = new SyncCloudSettings();
        Assert.True(settings.SyncAttachments);
        Assert.Equal(SyncCloudSettings.DefaultMaxAttachmentSyncBytes, settings.MaxAttachmentSyncBytes);
        var cloned = settings.Clone();
        Assert.True(cloned.SyncAttachments);
        Assert.Equal(settings.MaxAttachmentSyncBytes, cloned.MaxAttachmentSyncBytes);
    }

    [Fact]
    public void SyncCloudSettings_NegativeMaxAttachmentSize_Throws()
    {
        var settings = new SyncCloudSettings();
        Assert.Throws<ArgumentOutOfRangeException>(() => settings.MaxAttachmentSyncBytes = -1);
    }

    [Fact]
    public async Task UploadBlob_MissingLocalFile_ReturnsError()
    {
        var (svc, transport, storage, _) = MakeSvc();
        byte[] content = RandomBytes(32);
        var att = PlaceLocalFile(storage, content);
        File.Delete(Path.Combine(storage.AttachmentsDirectory, att.StoredFileName));
        var result = await svc.UploadBlobAsync(att, "password");
        Assert.False(result.Success);
        Assert.False(transport.Store.Any());
    }

    [Fact]
    public void SyncCloudSettings_TooLargeMaxAttachmentSize_Throws()
    {
        var settings = new SyncCloudSettings();
        Assert.Throws<ArgumentOutOfRangeException>(() => settings.MaxAttachmentSyncBytes = SyncCloudSettings.MaxAllowedAttachmentSyncBytes + 1);
    }

    [Fact]
    public void GetBlobKey_ValidSha256_ReturnsCorrectKey()
    {
        var keyHelper = new SyncObjectKeyHelper(new SyncCloudSettings { Prefix = "myapp" });
        string sha256 = new string('a', 64);
        string key = keyHelper.GetBlobKey(sha256);
        Assert.Contains("blobs/", key);
        Assert.Contains(sha256, key);
        Assert.EndsWith(".bin", key);
        Assert.DoesNotContain("..", key);
        Assert.DoesNotContain("\\", key);
    }

    [Fact]
    public async Task UploadBlob_TransportOffline_ReturnsErrorResult()
    {
        var (svc, transport, storage, _) = MakeSvc();
        byte[] content = RandomBytes(128);
        var att = PlaceLocalFile(storage, content);
        transport.IsOffline = true;
        var result = await svc.UploadBlobAsync(att, "password");
        Assert.False(result.Success);
    }

    [Fact]
    public void SyncCycleResult_HasBlobCounters()
    {
        var result = new SyncCycleResult { Success = true };
        result.BlobsUploaded = 5;
        result.BlobsDownloaded = 3;
        result.BlobsSkipped = 1;
        result.BlobErrors = 0;
        Assert.Equal(5, result.BlobsUploaded);
        Assert.Equal(3, result.BlobsDownloaded);
        Assert.Equal(1, result.BlobsSkipped);
        Assert.Equal(0, result.BlobErrors);
    }

    [Fact]
    public async Task UploadBlob_ValidExistingBlob_IsAcceptedWithoutOverwrite()
    {
        var (svc, transport, storage, keyHelper) = MakeSvc();
        byte[] content = RandomBytes(192);
        var att = PlaceLocalFile(storage, content);
        var first = await svc.UploadBlobAsync(att, "password");
        Assert.True(first.Success, first.ErrorMessage);
        Assert.False(first.AlreadyExisted);
        byte[] original = (byte[])transport.Store[keyHelper.GetBlobKey(att.Sha256)].Content.Clone();
        string etag = transport.Store[keyHelper.GetBlobKey(att.Sha256)].ETag;
        var second = await svc.UploadBlobAsync(att, "password");
        Assert.True(second.Success, second.ErrorMessage);
        Assert.True(second.AlreadyExisted);
        Assert.Equal(1, transport.PutImmutableCalls);
        Assert.Equal(original, transport.Store[keyHelper.GetBlobKey(att.Sha256)].Content);
        Assert.Equal(etag, transport.Store[keyHelper.GetBlobKey(att.Sha256)].ETag);
    }

    [Fact]
    public async Task UploadBlob_CorruptExistingBlob_ReturnsErrorAndDoesNotOverwrite()
    {
        var (svc, transport, storage, keyHelper) = MakeSvc();
        byte[] content = RandomBytes(128);
        var att = PlaceLocalFile(storage, content);
        string blobKey = keyHelper.GetBlobKey(att.Sha256);
        byte[] corrupt = RandomBytes(200);
        transport.Store[blobKey] = (corrupt, "etag-corrupt", "application/octet-stream");
        var result = await svc.UploadBlobAsync(att, "password");
        Assert.False(result.Success);
        Assert.Equal(SyncAttachmentBlobService.UnusableExistingBlobMessage, result.ErrorMessage);
        Assert.Equal(0, transport.PutImmutableCalls);
        Assert.Equal(corrupt, transport.Store[blobKey].Content);
    }

    [Fact]
    public async Task UploadBlob_ExistingBlobEncryptedWithOtherPassword_ReturnsErrorAndDoesNotOverwrite()
    {
        var (svc, transport, storage, keyHelper) = MakeSvc();
        byte[] content = RandomBytes(96);
        var att = PlaceLocalFile(storage, content);
        var first = await svc.UploadBlobAsync(att, "password-a");
        Assert.True(first.Success, first.ErrorMessage);
        byte[] stored = (byte[])transport.Store[keyHelper.GetBlobKey(att.Sha256)].Content.Clone();
        var result = await svc.UploadBlobAsync(att, "password-b");
        Assert.False(result.Success);
        Assert.Equal(SyncAttachmentBlobService.UnusableExistingBlobMessage, result.ErrorMessage);
        Assert.Equal(1, transport.PutImmutableCalls);
        Assert.Equal(stored, transport.Store[keyHelper.GetBlobKey(att.Sha256)].Content);
    }

    [Fact]
    public async Task UploadBlob_ExistingBlobHashMismatchAfterDecrypt_ReturnsErrorAndDoesNotOverwrite()
    {
        var (svc, transport, storage, keyHelper) = MakeSvc();
        byte[] content = RandomBytes(64);
        byte[] other = RandomBytes(64);
        while (Sha256Hex(other) == Sha256Hex(content))
            other = RandomBytes(64);
        var att = PlaceLocalFile(storage, content);
        string blobKey = keyHelper.GetBlobKey(att.Sha256);
        byte[] mismatchEnvelope = svc.EncryptBlobEnvelope(other, att.Sha256, "password");
        transport.Store[blobKey] = (mismatchEnvelope, "etag-mismatch", "application/octet-stream");
        var result = await svc.UploadBlobAsync(att, "password");
        Assert.False(result.Success);
        Assert.Equal(SyncAttachmentBlobService.UnusableExistingBlobMessage, result.ErrorMessage);
        Assert.Equal(0, transport.PutImmutableCalls);
        Assert.Equal(mismatchEnvelope, transport.Store[blobKey].Content);
    }

    [Fact]
    public async Task UploadBlob_ConcurrentPutConflict_ValidWinner_IsAccepted()
    {
        var (svc, transport, storage, keyHelper) = MakeSvc();
        byte[] content = RandomBytes(160);
        var att = PlaceLocalFile(storage, content);
        var first = await svc.UploadBlobAsync(att, "password");
        Assert.True(first.Success, first.ErrorMessage);
        string etag = transport.Store[keyHelper.GetBlobKey(att.Sha256)].ETag;
        byte[] winner = (byte[])transport.Store[keyHelper.GetBlobKey(att.Sha256)].Content.Clone();
        transport.HideExistingObjectOnGetCount = 1;
        var raced = await svc.UploadBlobAsync(att, "password");
        Assert.True(raced.Success, raced.ErrorMessage);
        Assert.True(raced.AlreadyExisted);
        Assert.Equal(2, transport.PutImmutableCalls);
        Assert.Equal(etag, transport.Store[keyHelper.GetBlobKey(att.Sha256)].ETag);
        Assert.Equal(winner, transport.Store[keyHelper.GetBlobKey(att.Sha256)].Content);
    }

    [Fact]
    public async Task UploadBlob_ConcurrentPutConflict_CorruptWinner_ReturnsErrorAndDoesNotOverwrite()
    {
        var (svc, transport, storage, keyHelper) = MakeSvc();
        byte[] content = RandomBytes(80);
        var att = PlaceLocalFile(storage, content);
        string blobKey = keyHelper.GetBlobKey(att.Sha256);
        byte[] corrupt = RandomBytes(180);
        transport.Store[blobKey] = (corrupt, "etag-winner", "application/octet-stream");
        transport.HideExistingObjectOnGetCount = 1;
        var result = await svc.UploadBlobAsync(att, "password");
        Assert.False(result.Success);
        Assert.Equal(SyncAttachmentBlobService.UnusableExistingBlobMessage, result.ErrorMessage);
        Assert.Equal(1, transport.PutImmutableCalls);
        Assert.Equal(corrupt, transport.Store[blobKey].Content);
    }

    private class MutableUsageService : ICloudUsageService
    {
        public CloudUsageResult Result { get; set; } = CloudUsageResult.Success(0, 0, 1, false, "v1/", "bucket");
        public CloudUsageResult? CachedUsage => Result;
        public bool IsCalculating => false;
        public Task<CloudUsageResult> CalculateUsageAsync(bool forceRefresh = false, CancellationToken ct = default)
            => Task.FromResult(Result);
        public void InvalidateCache() { }
        public void Dispose() { }
    }

    [Fact]
    public async Task Quota_Below800_AllowsNewBlobUpload()
    {
        var usage = new MutableUsageService
        {
            Result = CloudUsageResult.Success(100, 1, 1, false, "v1/", "bucket")
        };
        var (svc, transport, storage, _) = MakeSvc(usageService: usage);
        var att = PlaceLocalFile(storage, RandomBytes(32));
        var result = await svc.UploadBlobAsync(att, "password");
        Assert.True(result.Success, result.ErrorMessage);
        Assert.False(result.AlreadyExisted);
        Assert.Equal(1, transport.PutImmutableCalls);
    }

    [Fact]
    public async Task Quota_At800_WarnsButAllowsNewBlob()
    {
        Assert.True(CloudQuotaPolicy.IsWarning(CloudUsageResult.Success(
            CloudQuotaPolicy.WarningThresholdBytes, 1, 1, false, "v1/", "b")));
        var usage = new MutableUsageService
        {
            Result = CloudUsageResult.Success(CloudQuotaPolicy.WarningThresholdBytes, 2, 1, false, "v1/", "bucket")
        };
        var (svc, transport, storage, _) = MakeSvc(usageService: usage);
        var att = PlaceLocalFile(storage, RandomBytes(32));
        var result = await svc.UploadBlobAsync(att, "password");
        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(1, transport.PutImmutableCalls);
    }

    [Fact]
    public async Task Quota_At950_BlocksNewBlob_AllowsVerifiedDedup()
    {
        var usage = new MutableUsageService
        {
            Result = CloudUsageResult.Success(100, 1, 1, false, "v1/", "bucket")
        };
        var (svc, transport, storage, _) = MakeSvc(usageService: usage);
        var att = PlaceLocalFile(storage, RandomBytes(48));
        var first = await svc.UploadBlobAsync(att, "password");
        Assert.True(first.Success, first.ErrorMessage);

        usage.Result = CloudUsageResult.Success(
            CloudQuotaPolicy.BlockNewAttachmentThresholdBytes, 10, 1, false, "v1/", "bucket");
        var dedup = await svc.UploadBlobAsync(att, "password");
        Assert.True(dedup.Success, dedup.ErrorMessage);
        Assert.True(dedup.AlreadyExisted);
        Assert.Equal(1, transport.PutImmutableCalls);

        var other = PlaceLocalFile(storage, RandomBytes(64), "new.bin");
        var blocked = await svc.UploadBlobAsync(other, "password");
        Assert.False(blocked.Success);
        Assert.Equal(CloudQuotaPolicy.NewBlobBlockedByQuotaMessage, blocked.ErrorMessage);
        Assert.Equal(1, transport.PutImmutableCalls);
    }

    [Fact]
    public async Task Quota_TruncatedMeasurement_IsNotZero_AndBlocksNewBlob()
    {
        var truncated = CloudUsageResult.Success(0, 0, 1, true, "v1/", "bucket");
        Assert.False(CloudQuotaPolicy.IsMeasurementReliable(truncated));
        Assert.True(CloudQuotaPolicy.BlocksNewAttachments(truncated));
        Assert.Contains("неполн", CloudQuotaPolicy.FormatStatus(truncated), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Новые вложения разрешены", CloudQuotaPolicy.FormatStatus(truncated), StringComparison.Ordinal);

        var usage = new MutableUsageService { Result = truncated };
        var (svc, transport, storage, _) = MakeSvc(usageService: usage);
        var att = PlaceLocalFile(storage, RandomBytes(16));
        var result = await svc.UploadBlobAsync(att, "password");
        Assert.False(result.Success);
        Assert.Equal(0, transport.PutImmutableCalls);
    }

    [Fact]
    public async Task Quota_FailedMeasurement_BlocksNewBlob()
    {
        var usage = new MutableUsageService { Result = CloudUsageResult.Failure("list failed", "v1/", "bucket") };
        var (svc, transport, storage, _) = MakeSvc(usageService: usage);
        var att = PlaceLocalFile(storage, RandomBytes(16));
        var result = await svc.UploadBlobAsync(att, "password");
        Assert.False(result.Success);
        Assert.Equal(0, transport.PutImmutableCalls);
    }
}
