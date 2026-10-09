using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using QuickNotes.App.Data;
using QuickNotes.App.Models;
using QuickNotes.App.Models.Sync;
using QuickNotes.App.Services;
using QuickNotes.App.Services.Sync;
using Xunit;

namespace QuickNotes.Tests;

[TestCategory(TestCategories.Integration)]
public class CloudPasswordRotationTests : IDisposable
{
    private const string OldPassword = "RotationOldPass-21H!";
    private const string NewPassword = "RotationNewPass-21H!";
    private readonly List<string> _temp = new();

    public void Dispose()
    {
        foreach (var p in _temp)
        {
            try { if (File.Exists(p)) File.Delete(p); } catch { }
            try { if (Directory.Exists(p)) Directory.Delete(p, true); } catch { }
        }
    }

    private sealed class MemoryPassword : ISyncPasswordStorage
    {
        public string? Password { get; set; }
        public string? Pending { get; set; }
        public bool ThrowOnPromote { get; set; }
        public Task<string?> LoadPasswordAsync(CancellationToken ct = default) => Task.FromResult(Password);
        public Task SavePasswordAsync(string password, CancellationToken ct = default)
        {
            Password = password;
            return Task.CompletedTask;
        }
        public Task DeletePasswordAsync(CancellationToken ct = default)
        {
            Password = null;
            Pending = null;
            return Task.CompletedTask;
        }
        public bool HasPassword() => !string.IsNullOrEmpty(Password);
        public Task SavePendingPasswordAsync(string password, CancellationToken ct = default)
        {
            Pending = password;
            return Task.CompletedTask;
        }
        public Task<string?> LoadPendingPasswordAsync(CancellationToken ct = default) => Task.FromResult(Pending);
        public Task PromotePendingPasswordAsync(CancellationToken ct = default)
        {
            if (ThrowOnPromote) throw new IOException("simulated promote failure");
            Password = Pending;
            Pending = null;
            return Task.CompletedTask;
        }
        public Task DeletePendingPasswordAsync(CancellationToken ct = default)
        {
            Pending = null;
            return Task.CompletedTask;
        }
        public bool HasPendingPassword() => !string.IsNullOrEmpty(Pending);
    }

    private sealed class FakeStore : ICloudObjectStoreTransport
    {
        public readonly ConcurrentDictionary<string, Stored> Store = new();
        public bool TruncateWithoutToken { get; set; }
        public int? FailGenerationPutIndex { get; set; }
        public int GenerationConditionalPuts { get; private set; }
        public Action<string, byte[]>? AfterPutConditional { get; set; }

        public sealed class Stored
        {
            public byte[] Content { get; set; } = Array.Empty<byte>();
            public string ETag { get; set; } = Guid.NewGuid().ToString("N");
        }

        public Task<bool> TestConnectionAsync(CancellationToken ct = default) => Task.FromResult(true);

        public Task<StorageObjectMetadata?> HeadObjectAsync(string key, CancellationToken ct = default)
        {
            return Store.TryGetValue(key, out var o)
                ? Task.FromResult<StorageObjectMetadata?>(Meta(key, o))
                : Task.FromResult<StorageObjectMetadata?>(null);
        }

        public Task<StorageObjectResult?> GetObjectAsync(string key, CancellationToken ct = default)
        {
            return Store.TryGetValue(key, out var o)
                ? Task.FromResult<StorageObjectResult?>(new StorageObjectResult { Metadata = Meta(key, o), Content = (byte[])o.Content.Clone() })
                : Task.FromResult<StorageObjectResult?>(null);
        }

        public Task<StorageObjectMetadata> PutImmutableObjectAsync(string key, byte[] content, string contentType = "application/json", CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (Store.TryGetValue(key, out var existing))
            {
                throw new CloudConflictException("exists", objectKey: key, actualETag: existing.ETag, statusCode: 409);
            }
            var stored = new Stored { Content = (byte[])content.Clone(), ETag = Guid.NewGuid().ToString("N") };
            if (!Store.TryAdd(key, stored))
                throw new CloudConflictException("race", objectKey: key, statusCode: 412);
            return Task.FromResult(Meta(key, stored));
        }

        public Task<StorageObjectMetadata> PutConditionalPointerAsync(string key, byte[] content, string? expectedETag, string contentType = "application/json", CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (key.EndsWith("generation.json", StringComparison.Ordinal))
            {
                GenerationConditionalPuts++;
                if (FailGenerationPutIndex.HasValue && GenerationConditionalPuts == FailGenerationPutIndex.Value)
                    throw new CloudConflictException("etag race", objectKey: key, statusCode: 412);
            }

            string? clean = StorageObjectMetadata.NormalizeETag(expectedETag);
            if (string.IsNullOrEmpty(clean))
            {
                var created = new Stored { Content = (byte[])content.Clone() };
                if (!Store.TryAdd(key, created))
                    throw new CloudConflictException("exists", objectKey: key, statusCode: 412);
                AfterPutConditional?.Invoke(key, content);
                return Task.FromResult(Meta(key, created));
            }

            if (!Store.TryGetValue(key, out var current) || current.ETag != clean)
                throw new CloudConflictException("etag mismatch", objectKey: key, expectedETag: clean, statusCode: 412);

            current.Content = (byte[])content.Clone();
            current.ETag = Guid.NewGuid().ToString("N");
            AfterPutConditional?.Invoke(key, content);
            return Task.FromResult(Meta(key, current));
        }

        public Task<IReadOnlyList<StorageObjectSummary>> ListObjectsAsync(string? prefix = null, int maxKeys = 1000, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<StorageObjectSummary>>(Array.Empty<StorageObjectSummary>());

        public Task<StorageListResult> ListObjectsV2Async(StorageListRequest request, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (TruncateWithoutToken)
            {
                return Task.FromResult(new StorageListResult
                {
                    Objects = Array.Empty<StorageObjectSummary>(),
                    IsTruncated = true,
                    NextContinuationToken = null
                });
            }

            string prefix = request.Prefix ?? string.Empty;
            string? delimiter = string.IsNullOrEmpty(request.Delimiter) ? null : request.Delimiter;
            int maxKeys = request.MaxKeys > 0 ? request.MaxKeys : 1000;
            var sorted = Store.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal))
                .OrderBy(k => k, StringComparer.Ordinal).ToList();
            var objects = new List<StorageObjectSummary>();
            var commonPrefixes = new HashSet<string>(StringComparer.Ordinal);

            foreach (var key in sorted)
            {
                if (delimiter != null)
                {
                    string rest = key.Substring(prefix.Length);
                    int d = rest.IndexOf(delimiter, StringComparison.Ordinal);
                    if (d >= 0)
                    {
                        commonPrefixes.Add(prefix + rest.Substring(0, d + delimiter.Length));
                        if (objects.Count + commonPrefixes.Count >= maxKeys)
                            break;
                        continue;
                    }
                }

                objects.Add(new StorageObjectSummary
                {
                    Key = key,
                    ETag = Store[key].ETag,
                    Size = Store[key].Content.Length
                });
                if (objects.Count + commonPrefixes.Count >= maxKeys)
                    break;
            }

            return Task.FromResult(new StorageListResult
            {
                Objects = objects,
                CommonPrefixes = commonPrefixes.OrderBy(p => p, StringComparer.Ordinal).ToList(),
                IsTruncated = false
            });
        }

        public Task<bool> DeleteObjectAsync(string key, CancellationToken ct = default)
            => Task.FromResult(Store.TryRemove(key, out _));

        public void Dispose() { }

        private static StorageObjectMetadata Meta(string key, Stored o) => new()
        {
            Key = key,
            ETag = o.ETag,
            ContentLength = o.Content.Length
        };
    }

    private static SyncCloudSettings Settings() => new()
    {
        Enabled = true,
        Bucket = "notes-sync-bucket",
        Prefix = "sync-root",
        Endpoint = "https://storage.yandexcloud.net",
        Region = "ru-central1"
    };

    private async Task<(FakeStore store, SyncObjectKeyHelper keys, Guid deviceId, Guid packageId, string packageKey, string pointerKey)> SeedLegacyAsync()
    {
        var settings = Settings();
        var keys = new SyncObjectKeyHelper(settings);
        var store = new FakeStore();
        var deviceId = Guid.NewGuid();
        string dbPath = Path.Combine(Path.GetTempPath(), $"qn_rot_{Guid.NewGuid():N}.db");
        _temp.Add(dbPath);

        var crypto = new SyncCryptoService(iterations: 5_000);
        using (var db = new QuickNotesDbContext(dbPath))
        {
            DbInitializer.Initialize(db);
            db.Notes.Add(new Note { Text = "rotation-note", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
            var exporter = new SyncPackageExporter(crypto, new FixedDeviceIdProvider(deviceId));
            var exported = await exporter.ExportPackageAsync(db, OldPassword, deviceId);
            Assert.True(exported.Success);
            var packageId = exported.PackageId;
            string packageKey = keys.GetPackageKey(deviceId, packageId);
            await store.PutImmutableObjectAsync(packageKey, Encoding.UTF8.GetBytes(exported.PackageJson!));
            var pointer = new DevicePointerPayload
            {
                FormatVersion = 1,
                DeviceId = deviceId,
                LatestPackageId = packageId,
                LatestPackageKey = packageKey,
                PackageDigest = SyncFingerprintHelper.ComputeSha256(Encoding.UTF8.GetBytes(exported.PackageJson!)),
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow,
                PackageCount = 1
            };
            string pointerKey = keys.GetDevicePointerKey(deviceId);
            await store.PutConditionalPointerAsync(pointerKey, JsonSerializer.SerializeToUtf8Bytes(pointer), null);
            return (store, keys, deviceId, packageId, packageKey, pointerKey);
        }
    }

    private static SyncPasswordRotationService CreateService(
        FakeStore store,
        SyncObjectKeyHelper keys,
        MemoryPassword pwd,
        int maxPages = 20,
        SyncCloudExclusiveLock? exclusiveLock = null)
        => new(store, new SyncCryptoService(iterations: 5_000), keys, pwd, Settings(), exclusiveLock: exclusiveLock, maxPages: maxPages);

    private static void AssertOldPackageRejectsNewPassword(byte[] packageBytes)
    {
        var env = JsonSerializer.Deserialize<SyncPackageEnvelope>(packageBytes);
        Assert.NotNull(env);
        Assert.Throws<SyncSecurityException>(() =>
            new SyncCryptoService(5_000).DecryptPayload(
                Convert.FromBase64String(env!.EncryptedPayloadBase64),
                Convert.FromBase64String(env.Crypto.SaltBase64),
                Convert.FromBase64String(env.Crypto.NonceBase64),
                Convert.FromBase64String(env.Crypto.TagBase64),
                NewPassword,
                env.GetAssociatedData(),
                env.Crypto.KdfIterations));
    }

    private static byte[] EncryptPackage(Guid deviceId, Guid packageId, byte[] plaintext, string password)
    {
        var crypto = new SyncCryptoService(5_000);
        var envelope = new SyncPackageEnvelope
        {
            Magic = "QNSP",
            FormatVersion = 1,
            PackageId = packageId,
            DeviceId = deviceId,
            CreatedAtUtc = DateTime.UtcNow,
            Crypto = new SyncCryptoHeader
            {
                Algorithm = SyncCryptoService.AlgorithmName,
                KdfAlgorithm = SyncCryptoService.KdfName,
                KdfVersion = SyncCryptoService.KdfVersion,
                KdfIterations = crypto.Iterations
            }
        };
        var enc = crypto.EncryptPayload(plaintext, password, envelope.GetAssociatedData());
        envelope.Crypto.SaltBase64 = Convert.ToBase64String(enc.Salt);
        envelope.Crypto.NonceBase64 = Convert.ToBase64String(enc.Nonce);
        envelope.Crypto.TagBase64 = Convert.ToBase64String(enc.Tag);
        envelope.EncryptedPayloadBase64 = Convert.ToBase64String(enc.Ciphertext);
        return Encoding.UTF8.GetBytes(JsonSerializer.Serialize(envelope));
    }

    private static byte[] EncryptBlob(byte[] plaintext, string shaForAad, string password, long? declaredLength = null)
    {
        var crypto = new SyncCryptoService(5_000);
        byte[] aad = Encoding.UTF8.GetBytes($"QNBA|1|{shaForAad.ToLowerInvariant()}");
        var enc = crypto.EncryptPayload(plaintext, password, aad);
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        bw.Write(new byte[] { 0x51, 0x4E, 0x42, 0x41 });
        bw.Write((byte)0x01);
        bw.Write(enc.Salt.Length);
        bw.Write(enc.Salt);
        bw.Write(enc.Nonce);
        bw.Write(enc.Tag);
        bw.Write(declaredLength ?? plaintext.Length);
        bw.Write(enc.Ciphertext);
        bw.Flush();
        return ms.ToArray();
    }

    private static string Sha256Hex(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    [Fact]
    public async Task SuccessfulRotation_NewPasswordReadsAfterSwitch_NotBefore_OldGenerationKept()
    {
        var (store, keys, _, _, packageKey, pointerKey) = await SeedLegacyAsync();
        var pwd = new MemoryPassword { Password = OldPassword };
        var svc = CreateService(store, keys, pwd);

        AssertOldPackageRejectsNewPassword(store.Store[packageKey].Content);

        var preview = await svc.PreviewAsync(OldPassword);
        Assert.True(preview.IsComplete, preview.BlockReason);
        AssertOldPackageRejectsNewPassword(store.Store[packageKey].Content);
        Assert.Equal(OldPassword, pwd.Password);
        Assert.Null(pwd.Pending);

        var exec = await svc.ExecuteAsync(preview.Token, OldPassword, NewPassword);
        Assert.True(exec.CloudSwitched, exec.Summary);
        Assert.True(exec.LocalPasswordSaved);
        Assert.Equal(NewPassword, pwd.Password);
        Assert.Null(pwd.Pending);
        Assert.True(store.Store.ContainsKey(packageKey), "old generation package must remain");
        Assert.True(store.Store.ContainsKey(pointerKey), "old generation pointer must remain");
        Assert.Contains(store.Store.Keys, k => k.Contains("/g/") && k.Contains("/packages/"));
        AssertOldPackageRejectsNewPassword(store.Store[packageKey].Content);

        var helper = new SyncObjectKeyHelper(Settings());
        var resolved = await SyncGenerationResolver.ResolveAndApplyAsync(store, helper);
        Assert.True(resolved.Success);
        Assert.NotEqual(Guid.Empty, helper.GenerationId);

        string dbPath = Path.Combine(Path.GetTempPath(), $"qn_rot_pull_{Guid.NewGuid():N}.db");
        _temp.Add(dbPath);
        var creds = new EngineCreds();
        var crypto = new SyncCryptoService(5_000);
        var deviceId = Guid.NewGuid();
        using var db = new QuickNotesDbContext(dbPath);
        DbInitializer.Initialize(db);
        var engine = new SyncEngine(store, new SyncPackageExporter(crypto, new FixedDeviceIdProvider(deviceId)),
            new SyncPackageImporter(crypto), new FixedDeviceIdProvider(deviceId), Settings(), creds, helper,
            dbFactory: () => new QuickNotesDbContext(dbPath));
        var pull = await engine.RunSyncCycleAsync(db, NewPassword, new SyncCycleOptions { PullOnly = true });
        Assert.True(pull.Success, string.Join("; ", pull.Errors));
        Assert.Equal("rotation-note", (await db.Notes.FirstAsync()).Text);
    }

    private sealed class EngineCreds : IS3CredentialsStorage
    {
        public Task<S3Credentials?> LoadCredentialsAsync(CancellationToken ct = default)
            => Task.FromResult<S3Credentials?>(new S3Credentials("k", "s"));
        public Task SaveCredentialsAsync(S3Credentials credentials, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteCredentialsAsync(CancellationToken ct = default) => Task.CompletedTask;
        public bool HasCredentials() => true;
    }

    [Fact]
    public async Task WrongOldPassword_LeavesLegacyAndDpapiUnchanged()
    {
        var (store, keys, _, _, packageKey, _) = await SeedLegacyAsync();
        var pwd = new MemoryPassword { Password = OldPassword };
        var svc = CreateService(store, keys, pwd);
        var preview = await svc.PreviewAsync("not-the-old-password");
        Assert.False(preview.IsComplete);
        Assert.Equal(OldPassword, pwd.Password);
        Assert.Null(pwd.Pending);
        Assert.DoesNotContain(store.Store.Keys, k => k.Contains("/g/"));
        Assert.True(store.Store.ContainsKey(packageKey));
    }

    [Fact]
    public async Task CorruptPackage_FailsClosed_NoSwitch()
    {
        var (store, keys, _, _, packageKey, _) = await SeedLegacyAsync();
        store.Store[packageKey].Content = Encoding.UTF8.GetBytes("{not-valid-envelope}");
        var pwd = new MemoryPassword { Password = OldPassword };
        var svc = CreateService(store, keys, pwd);
        var preview = await svc.PreviewAsync(OldPassword);
        Assert.False(preview.IsComplete);
        Assert.Equal(OldPassword, pwd.Password);
        Assert.DoesNotContain(store.Store.Keys, k => k.Contains("/g/"));
    }

    [Fact]
    public async Task IncompletePagination_FailsClosed_NoSwitch()
    {
        var (store, keys, _, _, _, _) = await SeedLegacyAsync();
        store.TruncateWithoutToken = true;
        var pwd = new MemoryPassword { Password = OldPassword };
        var svc = CreateService(store, keys, pwd);
        var preview = await svc.PreviewAsync(OldPassword);
        Assert.False(preview.IsComplete);
        Assert.Equal(OldPassword, pwd.Password);
    }

    [Fact]
    public async Task CancelBeforeSwitch_LeavesOldGenerationAndPassword()
    {
        var (store, keys, _, _, packageKey, _) = await SeedLegacyAsync();
        var pwd = new MemoryPassword { Password = OldPassword };
        var svc = CreateService(store, keys, pwd);
        var preview = await svc.PreviewAsync(OldPassword);
        Assert.True(preview.IsComplete, preview.BlockReason);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var exec = await svc.ExecuteAsync(preview.Token, OldPassword, NewPassword, ct: cts.Token);
        Assert.True(exec.Canceled);
        Assert.False(exec.CloudSwitched);
        Assert.Equal(OldPassword, pwd.Password);
        Assert.Null(pwd.Pending);
        Assert.True(store.Store.ContainsKey(packageKey));
        var genObj = await store.GetObjectAsync(keys.GetGenerationPointerKey());
        if (genObj?.Content != null)
        {
            var ptr = JsonSerializer.Deserialize<SyncGenerationPointer>(genObj.Content);
            Assert.Equal(Guid.Empty, ptr!.ActiveGenerationId);
        }
    }

    [Fact]
    public async Task ETagRaceOnSwitch_LeavesOldActiveAndOldDpapi()
    {
        var (store, keys, _, _, packageKey, _) = await SeedLegacyAsync();
        store.FailGenerationPutIndex = 2;
        var pwd = new MemoryPassword { Password = OldPassword };
        var svc = CreateService(store, keys, pwd);
        var preview = await svc.PreviewAsync(OldPassword);
        Assert.True(preview.IsComplete, preview.BlockReason);
        var exec = await svc.ExecuteAsync(preview.Token, OldPassword, NewPassword);
        Assert.False(exec.CloudSwitched);
        Assert.Equal(OldPassword, pwd.Password);
        Assert.True(store.Store.ContainsKey(packageKey));
        var genObj = await store.GetObjectAsync(keys.GetGenerationPointerKey());
        Assert.NotNull(genObj);
        var ptr = JsonSerializer.Deserialize<SyncGenerationPointer>(genObj!.Content);
        Assert.Equal(Guid.Empty, ptr!.ActiveGenerationId);
        Assert.NotNull(ptr.PendingGenerationId);
    }

    [Fact]
    public async Task CrashAfterNewGenUploadBeforeSwitch_RetryCompletesIdempotently()
    {
        var (store, keys, _, _, packageKey, _) = await SeedLegacyAsync();
        store.FailGenerationPutIndex = 2;
        var pwd = new MemoryPassword { Password = OldPassword };
        var svc = CreateService(store, keys, pwd);
        var preview = await svc.PreviewAsync(OldPassword);
        var first = await svc.ExecuteAsync(preview.Token, OldPassword, NewPassword);
        Assert.False(first.CloudSwitched);
        Assert.Equal(OldPassword, pwd.Password);

        store.FailGenerationPutIndex = null;
        var second = await svc.ExecuteAsync(preview.Token, OldPassword, NewPassword);
        Assert.True(second.CloudSwitched, second.Summary);
        Assert.Equal(NewPassword, pwd.Password);
        Assert.True(store.Store.ContainsKey(packageKey));
        Assert.Equal(1, store.Store.Keys.Count(k => k.Contains("/g/") && k.Contains("/packages/")));
    }

    [Fact]
    public async Task CrashAfterSwitchBeforePromote_RetryAndEngineRecoverFromPending()
    {
        var (store, keys, _, _, packageKey, _) = await SeedLegacyAsync();
        var pwd = new MemoryPassword { Password = OldPassword, ThrowOnPromote = true };
        var svc = CreateService(store, keys, pwd);
        var preview = await svc.PreviewAsync(OldPassword);
        var first = await svc.ExecuteAsync(preview.Token, OldPassword, NewPassword);
        Assert.True(first.CloudSwitched, first.Summary);
        Assert.False(first.LocalPasswordSaved);
        Assert.Equal(OldPassword, pwd.Password);
        Assert.Equal(NewPassword, pwd.Pending);
        Assert.True(store.Store.ContainsKey(packageKey));

        pwd.ThrowOnPromote = false;
        var second = await svc.ExecuteAsync(preview.Token, OldPassword, NewPassword);
        Assert.True(second.AlreadyOnNewGeneration);
        Assert.True(second.LocalPasswordSaved);
        Assert.Equal(NewPassword, pwd.Password);
        Assert.Null(pwd.Pending);
        var gen = JsonSerializer.Deserialize<SyncGenerationPointer>((await store.GetObjectAsync(keys.GetGenerationPointerKey()))!.Content);
        Assert.NotEqual(Guid.Empty, gen!.ActiveGenerationId);
        Assert.Equal(1, store.Store.Keys.Count(k => k.Contains("/g/") && k.Contains("/packages/")));
    }

    [Fact]
    public async Task CrashAfterSwitch_EnginePromotesPendingWithoutReswitch()
    {
        var (store, keys, _, _, packageKey, _) = await SeedLegacyAsync();
        var pwd = new MemoryPassword { Password = OldPassword, ThrowOnPromote = true };
        var svc = CreateService(store, keys, pwd);
        var preview = await svc.PreviewAsync(OldPassword);
        var first = await svc.ExecuteAsync(preview.Token, OldPassword, NewPassword);
        Assert.True(first.CloudSwitched);
        Assert.Equal(OldPassword, pwd.Password);
        Assert.Equal(NewPassword, pwd.Pending);

        pwd.ThrowOnPromote = false;
        string dbPath = Path.Combine(Path.GetTempPath(), $"qn_rot_prom_{Guid.NewGuid():N}.db");
        _temp.Add(dbPath);
        using var db = new QuickNotesDbContext(dbPath);
        DbInitializer.Initialize(db);
        var helper = new SyncObjectKeyHelper(Settings());
        var crypto = new SyncCryptoService(5_000);
        var localDevice = Guid.NewGuid();
        var engine = new SyncEngine(store, new SyncPackageExporter(crypto, new FixedDeviceIdProvider(localDevice)),
            new SyncPackageImporter(crypto), new FixedDeviceIdProvider(localDevice), Settings(), new EngineCreds(), helper,
            dbFactory: () => new QuickNotesDbContext(dbPath),
            passwordStorage: pwd);
        var pull = await engine.RunSyncCycleAsync(db, OldPassword, new SyncCycleOptions { PullOnly = true });
        Assert.True(pull.Success, string.Join("; ", pull.Errors));
        Assert.Equal(NewPassword, pwd.Password);
        Assert.Null(pwd.Pending);
        Assert.True(store.Store.ContainsKey(packageKey));
        var gen = JsonSerializer.Deserialize<SyncGenerationPointer>((await store.GetObjectAsync(keys.GetGenerationPointerKey()))!.Content);
        Assert.NotEqual(Guid.Empty, gen!.ActiveGenerationId);
    }

    [Fact]
    public async Task UnknownDeviceObject_FailsClosed()
    {
        var (store, keys, _, _, _, _) = await SeedLegacyAsync();
        store.Store[keys.GetDevicesPrefix() + "unexpected.bin"] = new FakeStore.Stored { Content = new byte[] { 1 } };
        var pwd = new MemoryPassword { Password = OldPassword };
        var preview = await CreateService(store, keys, pwd).PreviewAsync(OldPassword);
        Assert.False(preview.IsComplete);
        Assert.Equal(OldPassword, pwd.Password);
    }

    [Fact]
    public async Task BlobValidAeadWrongDeclaredLength_FailsClosed()
    {
        var (store, keys, _, _, _, _) = await SeedLegacyAsync();
        byte[] plain = Encoding.UTF8.GetBytes("blob-plain-21h");
        string sha = Sha256Hex(plain);
        byte[] envelope = EncryptBlob(plain, sha, OldPassword, declaredLength: plain.Length + 7);
        await store.PutImmutableObjectAsync(keys.GetBlobKey(sha), envelope, "application/octet-stream");
        var pwd = new MemoryPassword { Password = OldPassword };
        var preview = await CreateService(store, keys, pwd).PreviewAsync(OldPassword);
        Assert.False(preview.IsComplete);
        Assert.DoesNotContain(store.Store.Keys, k => k.Contains("/g/"));
        Assert.Equal(OldPassword, pwd.Password);
    }

    [Fact]
    public async Task BlobValidAeadWrongShaInKey_FailsClosed()
    {
        var (store, keys, _, _, _, _) = await SeedLegacyAsync();
        byte[] plain = Encoding.UTF8.GetBytes("blob-plain-sha");
        string actualSha = Sha256Hex(plain);
        string wrongSha = Sha256Hex(Encoding.UTF8.GetBytes("other-bytes"));
        byte[] envelope = EncryptBlob(plain, wrongSha, OldPassword);
        await store.PutImmutableObjectAsync(keys.GetBlobKey(wrongSha), envelope, "application/octet-stream");
        var pwd = new MemoryPassword { Password = OldPassword };
        var preview = await CreateService(store, keys, pwd).PreviewAsync(OldPassword);
        Assert.False(preview.IsComplete, "AEAD may succeed under the key AAD, but SHA of plaintext must not match the key");
        Assert.DoesNotContain(store.Store.Keys, k => k.Contains("/g/"));
        Assert.NotEqual(actualSha, wrongSha);
        Assert.Equal(OldPassword, pwd.Password);
    }

    [Fact]
    public async Task PackageEnvelopeIdentityMismatch_FailsClosed()
    {
        var (store, keys, deviceId, packageId, packageKey, _) = await SeedLegacyAsync();
        byte[] foreign = EncryptPackage(Guid.NewGuid(), packageId, Encoding.UTF8.GetBytes("foreign"), OldPassword);
        store.Store[packageKey].Content = foreign;
        var pwd = new MemoryPassword { Password = OldPassword };
        var preview = await CreateService(store, keys, pwd).PreviewAsync(OldPassword);
        Assert.False(preview.IsComplete);
        Assert.Equal(OldPassword, pwd.Password);
        Assert.NotEqual(Guid.Empty, deviceId);
    }

    [Fact]
    public async Task ConflictDestinationDifferentValidPackage_FailsClosed_NoSwitch()
    {
        var (store, keys, deviceId, packageId, packageKey, _) = await SeedLegacyAsync();
        store.AfterPutConditional = (key, content) =>
        {
            if (!key.EndsWith("generation.json", StringComparison.Ordinal))
                return;
            var ptr = JsonSerializer.Deserialize<SyncGenerationPointer>(content);
            if (ptr?.PendingGenerationId == null || ptr.PendingGenerationId == Guid.Empty || ptr.ActiveGenerationId != Guid.Empty)
                return;
            var dest = keys.ForGeneration(ptr.PendingGenerationId.Value);
            string destKey = dest.GetPackageKey(deviceId, packageId);
            if (store.Store.ContainsKey(destKey))
                return;
            store.Store[destKey] = new FakeStore.Stored
            {
                Content = EncryptPackage(deviceId, packageId, Encoding.UTF8.GetBytes("different-plaintext"), NewPassword)
            };
        };

        var pwd = new MemoryPassword { Password = OldPassword };
        var svc = CreateService(store, keys, pwd);
        var preview = await svc.PreviewAsync(OldPassword);
        Assert.True(preview.IsComplete, preview.BlockReason);
        var exec = await svc.ExecuteAsync(preview.Token, OldPassword, NewPassword);
        Assert.False(exec.CloudSwitched, exec.Summary);
        Assert.Equal(OldPassword, pwd.Password);
        Assert.True(store.Store.ContainsKey(packageKey));
        var genObj = await store.GetObjectAsync(keys.GetGenerationPointerKey());
        var ptrAfter = JsonSerializer.Deserialize<SyncGenerationPointer>(genObj!.Content);
        Assert.Equal(Guid.Empty, ptrAfter!.ActiveGenerationId);
    }

    [Fact]
    public async Task ConflictDestinationDifferentValidBlob_FailsClosed_NoSwitch()
    {
        var (store, keys, _, _, _, _) = await SeedLegacyAsync();
        byte[] plain = Encoding.UTF8.GetBytes("live-blob");
        string sha = Sha256Hex(plain);
        await store.PutImmutableObjectAsync(keys.GetBlobKey(sha), EncryptBlob(plain, sha, OldPassword), "application/octet-stream");
        store.AfterPutConditional = (key, content) =>
        {
            if (!key.EndsWith("generation.json", StringComparison.Ordinal))
                return;
            var ptr = JsonSerializer.Deserialize<SyncGenerationPointer>(content);
            if (ptr?.PendingGenerationId == null || ptr.PendingGenerationId == Guid.Empty || ptr.ActiveGenerationId != Guid.Empty)
                return;
            var dest = keys.ForGeneration(ptr.PendingGenerationId.Value);
            string destKey = dest.GetBlobKey(sha);
            if (store.Store.ContainsKey(destKey))
                return;
            byte[] other = Encoding.UTF8.GetBytes("other-blob-plain");
            store.Store[destKey] = new FakeStore.Stored
            {
                Content = EncryptBlob(other, sha, NewPassword)
            };
        };

        var pwd = new MemoryPassword { Password = OldPassword };
        var svc = CreateService(store, keys, pwd);
        var preview = await svc.PreviewAsync(OldPassword);
        Assert.True(preview.IsComplete, preview.BlockReason);
        var exec = await svc.ExecuteAsync(preview.Token, OldPassword, NewPassword);
        Assert.False(exec.CloudSwitched, exec.Summary);
        Assert.Equal(OldPassword, pwd.Password);
        var genObj = await store.GetObjectAsync(keys.GetGenerationPointerKey());
        var ptrAfter = JsonSerializer.Deserialize<SyncGenerationPointer>(genObj!.Content);
        Assert.Equal(Guid.Empty, ptrAfter!.ActiveGenerationId);
    }

    [Fact]
    public async Task SourceInventoryChangeBetweenPreviewAndExecute_FailsClosed()
    {
        var (store, keys, _, _, packageKey, _) = await SeedLegacyAsync();
        var pwd = new MemoryPassword { Password = OldPassword };
        var svc = CreateService(store, keys, pwd);
        var preview = await svc.PreviewAsync(OldPassword);
        Assert.True(preview.IsComplete, preview.BlockReason);
        store.Store[packageKey].ETag = "mutated-etag-after-preview";
        var exec = await svc.ExecuteAsync(preview.Token, OldPassword, NewPassword);
        Assert.False(exec.CloudSwitched, exec.Summary);
        Assert.Equal(OldPassword, pwd.Password);
        var genObj = await store.GetObjectAsync(keys.GetGenerationPointerKey());
        if (genObj?.Content != null)
        {
            var ptr = JsonSerializer.Deserialize<SyncGenerationPointer>(genObj.Content);
            Assert.Equal(Guid.Empty, ptr!.ActiveGenerationId);
        }
    }

    [Fact]
    public async Task ExclusiveLockHeld_EngineWaitIfBusyFalse_ReturnsBusy()
    {
        var (store, keys, _, _, _, _) = await SeedLegacyAsync();
        var cloudLock = new SyncCloudExclusiveLock();
        await cloudLock.AcquireAsync(CancellationToken.None);
        try
        {
            string dbPath = Path.Combine(Path.GetTempPath(), $"qn_rot_busy_{Guid.NewGuid():N}.db");
            _temp.Add(dbPath);
            using var db = new QuickNotesDbContext(dbPath);
            DbInitializer.Initialize(db);
            var crypto = new SyncCryptoService(5_000);
            var localDevice = Guid.NewGuid();
            var engine = new SyncEngine(store, new SyncPackageExporter(crypto, new FixedDeviceIdProvider(localDevice)),
                new SyncPackageImporter(crypto), new FixedDeviceIdProvider(localDevice), Settings(), new EngineCreds(), keys,
                dbFactory: () => new QuickNotesDbContext(dbPath),
                exclusiveLock: cloudLock);
            var result = await engine.RunSyncCycleAsync(db, OldPassword, new SyncCycleOptions { WaitIfBusy = false });
            Assert.True(result.IsBusy);
        }
        finally
        {
            cloudLock.Release();
        }
    }

    [Fact]
    public async Task ExclusiveLock_RotationHoldsEngineBusy()
    {
        var (store, keys, _, _, _, _) = await SeedLegacyAsync();
        var cloudLock = new SyncCloudExclusiveLock();
        var pwd = new MemoryPassword { Password = OldPassword };
        var svc = CreateService(store, keys, pwd, exclusiveLock: cloudLock);
        using var hold = new SemaphoreSlim(0, 1);
        var previewStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var previewTask = Task.Run(async () =>
        {
            await cloudLock.AcquireAsync(CancellationToken.None);
            previewStarted.SetResult();
            await hold.WaitAsync();
            cloudLock.Release();
        });
        await previewStarted.Task;

        string dbPath = Path.Combine(Path.GetTempPath(), $"qn_rot_hold_{Guid.NewGuid():N}.db");
        _temp.Add(dbPath);
        using var db = new QuickNotesDbContext(dbPath);
        DbInitializer.Initialize(db);
        var crypto = new SyncCryptoService(5_000);
        var localDevice = Guid.NewGuid();
        var engine = new SyncEngine(store, new SyncPackageExporter(crypto, new FixedDeviceIdProvider(localDevice)),
            new SyncPackageImporter(crypto), new FixedDeviceIdProvider(localDevice), Settings(), new EngineCreds(), keys,
            dbFactory: () => new QuickNotesDbContext(dbPath),
            exclusiveLock: cloudLock);
        var busy = await engine.RunSyncCycleAsync(db, OldPassword, new SyncCycleOptions { WaitIfBusy = false });
        Assert.True(busy.IsBusy);

        hold.Release();
        await previewTask;
        var preview = await svc.PreviewAsync(OldPassword);
        Assert.True(preview.IsComplete, preview.BlockReason);
    }
}
