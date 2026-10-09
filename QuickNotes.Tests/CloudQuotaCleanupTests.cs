using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using QuickNotes.App.Models.Sync;
using QuickNotes.App.Services.Sync;
using Xunit;

namespace QuickNotes.Tests;

public class CloudQuotaCleanupTests
{
    private const string Password = "cleanup-password-21g";

    private sealed class FakeCleanupTransport : ICloudObjectStoreTransport
    {
        public readonly ConcurrentDictionary<string, (byte[] Content, string ETag, long Size)> Store = new();
        public int DeleteCalls;
        public readonly List<string> DeletedKeys = new();
        public bool ThrowOnDelete;
        public int ThrowOnDeleteAfter;
        public Exception? ListError;
        public bool Offline;

        public void Put(string key, byte[] content, string? etag = null)
        {
            Store[key] = ((byte[])content.Clone(), etag ?? Guid.NewGuid().ToString("N"), content.Length);
        }

        public Task<bool> TestConnectionAsync(CancellationToken ct = default) => Task.FromResult(true);

        public Task<StorageObjectMetadata?> HeadObjectAsync(string key, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (Offline) throw new CloudOfflineException("offline");
            return Store.TryGetValue(key, out var obj)
                ? Task.FromResult<StorageObjectMetadata?>(new StorageObjectMetadata
                {
                    Key = key,
                    ETag = obj.ETag,
                    ContentLength = obj.Size
                })
                : Task.FromResult<StorageObjectMetadata?>(null);
        }

        public Task<StorageObjectResult?> GetObjectAsync(string key, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (Offline) throw new CloudOfflineException("offline");
            return Store.TryGetValue(key, out var obj)
                ? Task.FromResult<StorageObjectResult?>(new StorageObjectResult
                {
                    Metadata = new StorageObjectMetadata { Key = key, ETag = obj.ETag, ContentLength = obj.Size },
                    Content = (byte[])obj.Content.Clone()
                })
                : Task.FromResult<StorageObjectResult?>(null);
        }

        public Task<StorageObjectMetadata> PutImmutableObjectAsync(string key, byte[] content, string contentType = "application/json", CancellationToken ct = default)
        {
            Put(key, content);
            return Task.FromResult(new StorageObjectMetadata { Key = key, ETag = Store[key].ETag, ContentLength = content.Length });
        }

        public Task<StorageObjectMetadata> PutConditionalPointerAsync(string key, byte[] content, string? expectedETag, string contentType = "application/json", CancellationToken ct = default)
            => PutImmutableObjectAsync(key, content, contentType, ct);

        public Task<IReadOnlyList<StorageObjectSummary>> ListObjectsAsync(string? prefix = null, int maxKeys = 1000, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<StorageObjectSummary>>(Array.Empty<StorageObjectSummary>());

        public Task<StorageListResult> ListObjectsV2Async(StorageListRequest request, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (Offline) throw new CloudOfflineException("offline");
            if (ListError != null) throw ListError;

            string prefix = request.Prefix ?? string.Empty;
            string? delimiter = string.IsNullOrEmpty(request.Delimiter) ? null : request.Delimiter;
            int maxKeys = request.MaxKeys > 0 ? request.MaxKeys : 1000;
            var sorted = Store.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal))
                .OrderBy(k => k, StringComparer.Ordinal).ToList();

            int start = 0;
            if (!string.IsNullOrEmpty(request.ContinuationToken) && int.TryParse(request.ContinuationToken, out int idx))
                start = idx;

            var objects = new List<StorageObjectSummary>();
            var commonPrefixes = new HashSet<string>(StringComparer.Ordinal);
            int current = start;
            bool truncated = false;
            string? next = null;

            while (current < sorted.Count)
            {
                string key = sorted[current];
                if (delimiter != null)
                {
                    string rest = key.Substring(prefix.Length);
                    int d = rest.IndexOf(delimiter, StringComparison.Ordinal);
                    if (d >= 0)
                    {
                        string cp = prefix + rest.Substring(0, d + delimiter.Length);
                        if (commonPrefixes.Add(cp) && objects.Count + commonPrefixes.Count >= maxKeys)
                        {
                            commonPrefixes.Remove(cp);
                            truncated = true;
                            next = current.ToString();
                            break;
                        }
                        current++;
                        continue;
                    }
                }

                if (objects.Count + commonPrefixes.Count >= maxKeys)
                {
                    truncated = true;
                    next = current.ToString();
                    break;
                }

                var stored = Store[key];
                objects.Add(new StorageObjectSummary { Key = key, ETag = stored.ETag, Size = stored.Size });
                current++;
            }

            return Task.FromResult(new StorageListResult
            {
                Objects = objects,
                CommonPrefixes = commonPrefixes.OrderBy(p => p, StringComparer.Ordinal).ToList(),
                IsTruncated = truncated,
                NextContinuationToken = truncated ? next : null
            });
        }

        public Task<bool> DeleteObjectAsync(string key, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            DeleteCalls++;
            if (ThrowOnDelete && DeleteCalls > ThrowOnDeleteAfter)
                throw new CloudOfflineException("delete failed");
            DeletedKeys.Add(key);
            return Task.FromResult(Store.TryRemove(key, out _));
        }

        public void Dispose() { }
    }

    private static SyncCloudSettings Settings() => new()
    {
        Enabled = true,
        Bucket = "qn-bucket",
        Prefix = "qn"
    };

    private static (CloudCleanupService svc, FakeCleanupTransport transport, SyncObjectKeyHelper keys, SyncCryptoService crypto)
        MakeCleanup(int maxPages = 50, int maxObjects = 50000, int pageSize = 1000)
    {
        var settings = Settings();
        var transport = new FakeCleanupTransport();
        var keys = new SyncObjectKeyHelper(settings);
        var crypto = new SyncCryptoService(1000);
        var svc = new CloudCleanupService(transport, crypto, keys, maxPages: maxPages, maxObjects: maxObjects, pageSize: pageSize);
        return (svc, transport, keys, crypto);
    }

    private static byte[] MakePackageBytes(SyncCryptoService crypto, Guid deviceId, Guid packageId, params string[] sha256s)
    {
        var payload = new SyncPackagePayload
        {
            PackageId = packageId,
            SourceDeviceId = deviceId,
            CreatedAtUtc = DateTime.UtcNow,
            Attachments = sha256s.Select(s => new SyncAttachmentDto
            {
                SyncId = Guid.NewGuid(),
                Sha256 = s,
                Operation = SyncOperationType.Upsert
            }).ToList()
        };
        byte[] plaintext = JsonSerializer.SerializeToUtf8Bytes(payload);
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
        var enc = crypto.EncryptPayload(plaintext, Password, envelope.GetAssociatedData());
        envelope.Crypto.SaltBase64 = Convert.ToBase64String(enc.Salt);
        envelope.Crypto.NonceBase64 = Convert.ToBase64String(enc.Nonce);
        envelope.Crypto.TagBase64 = Convert.ToBase64String(enc.Tag);
        envelope.Crypto.KdfIterations = enc.KdfIterations;
        envelope.EncryptedPayloadBase64 = Convert.ToBase64String(enc.Ciphertext);
        return JsonSerializer.SerializeToUtf8Bytes(envelope);
    }

    private static void SeedDevice(FakeCleanupTransport transport, SyncObjectKeyHelper keys, SyncCryptoService crypto,
        Guid deviceId, string[] reachableSha, IEnumerable<(string sha, byte[] body, string etag)> blobs)
    {
        Guid packageId = Guid.NewGuid();
        string packageKey = keys.GetPackageKey(deviceId, packageId);
        transport.Put(packageKey, MakePackageBytes(crypto, deviceId, packageId, reachableSha));
        var pointer = new DevicePointerPayload
        {
            FormatVersion = 1,
            DeviceId = deviceId,
            LatestPackageId = packageId,
            LatestPackageKey = packageKey,
            PackageCount = 1
        };
        transport.Put(keys.GetDevicePointerKey(deviceId), JsonSerializer.SerializeToUtf8Bytes(pointer));
        foreach (var blob in blobs)
            transport.Put(keys.GetBlobKey(blob.sha), blob.body, blob.etag);
    }

    [Fact]
    public void QuotaPolicy_Below800_WarningOff_UploadAllowed()
    {
        var usage = CloudUsageResult.Success(100 * 1024 * 1024, 3, 1, false, "qn/", "b");
        Assert.False(CloudQuotaPolicy.IsWarning(usage));
        Assert.False(CloudQuotaPolicy.BlocksNewAttachments(usage));
        Assert.True(CloudQuotaPolicy.CanUploadNewBlob(usage, 1024));
        Assert.Contains("разрешены", CloudQuotaPolicy.FormatStatus(usage), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void QuotaPolicy_At800_Warning_UploadStillAllowedUntil950()
    {
        var usage = CloudUsageResult.Success(CloudQuotaPolicy.WarningThresholdBytes, 4, 1, false, "qn/", "b");
        Assert.True(CloudQuotaPolicy.IsWarning(usage));
        Assert.True(CloudQuotaPolicy.CanUploadNewBlob(usage, 1024));
        Assert.Contains("Предупреждение", CloudQuotaPolicy.FormatStatus(usage), StringComparison.Ordinal);
    }

    [Fact]
    public void QuotaPolicy_At950_BlocksNewUploads()
    {
        var usage = CloudUsageResult.Success(CloudQuotaPolicy.BlockNewAttachmentThresholdBytes, 9, 1, false, "qn/", "b");
        Assert.True(CloudQuotaPolicy.BlocksNewAttachments(usage));
        Assert.False(CloudQuotaPolicy.CanUploadNewBlob(usage, 1));
        Assert.Contains("заблокированы", CloudQuotaPolicy.FormatStatus(usage), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void QuotaPolicy_ErrorAndTruncated_AreNotZeroAllowance()
    {
        var failed = CloudUsageResult.Failure("boom", "qn/", "b");
        Assert.Equal(0, failed.TotalBytes);
        Assert.False(CloudQuotaPolicy.IsMeasurementReliable(failed));
        Assert.True(CloudQuotaPolicy.BlocksNewAttachments(failed));
        Assert.DoesNotContain("разрешены", CloudQuotaPolicy.FormatStatus(failed), StringComparison.Ordinal);

        var truncated = CloudUsageResult.Success(0, 0, 50, true, "qn/", "b");
        Assert.True(truncated.IsTruncated);
        Assert.True(CloudQuotaPolicy.BlocksNewAttachments(truncated));
        Assert.Contains("неполн", CloudQuotaPolicy.FormatStatus(truncated), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Preview_DoesNotMutateStore_AndIgnoresNonBlobPrefix()
    {
        var (svc, transport, keys, crypto) = MakeCleanup();
        Guid device = Guid.NewGuid();
        string keep = new string('a', 64);
        string orphan = new string('b', 64);
        SeedDevice(transport, keys, crypto, device, new[] { keep }, new[]
        {
            (keep, Encoding.UTF8.GetBytes("keep-blob"), "etag-keep"),
            (orphan, Encoding.UTF8.GetBytes("orphan-blob"), "etag-orphan")
        });
        transport.Put("other-app/blobs/" + new string('c', 64) + ".bin", Encoding.UTF8.GetBytes("outside"));
        var before = transport.Store.Keys.OrderBy(k => k).ToArray();

        var preview = await svc.PreviewAsync(Password);
        Assert.True(preview.IsComplete, preview.BlockReason);
        Assert.Equal(before, transport.Store.Keys.OrderBy(k => k).ToArray());
        Assert.Single(preview.Candidates);
        Assert.Equal(keys.GetBlobKey(orphan), preview.Candidates[0].Key);
        Assert.DoesNotContain(preview.Candidates, c => c.Key.Contains("other-app", StringComparison.Ordinal));
        Assert.DoesNotContain(preview.Candidates, c => c.Key.Contains("/packages/", StringComparison.Ordinal));
        Assert.DoesNotContain(preview.Candidates, c => c.Key.Contains("pointer.json", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Preview_ReachableBlobIsNotCandidate_OrphanIsCandidate()
    {
        var (svc, transport, keys, crypto) = MakeCleanup();
        Guid device = Guid.NewGuid();
        string keep = new string('1', 64);
        string orphan = new string('2', 64);
        SeedDevice(transport, keys, crypto, device, new[] { keep }, new[]
        {
            (keep, new byte[] { 1, 2, 3 }, "e1"),
            (orphan, new byte[] { 4, 5, 6 }, "e2")
        });

        var preview = await svc.PreviewAsync(Password);
        Assert.True(preview.IsComplete, preview.BlockReason);
        Assert.Equal(keys.GetBlobKey(keep), keys.GetBlobKey(keep));
        Assert.DoesNotContain(preview.Candidates, c => c.Key == keys.GetBlobKey(keep));
        Assert.Contains(preview.Candidates, c => c.Key == keys.GetBlobKey(orphan));
    }

    [Fact]
    public async Task Preview_CorruptPackage_IsFailClosed()
    {
        var (svc, transport, keys, crypto) = MakeCleanup();
        Guid device = Guid.NewGuid();
        string orphan = new string('3', 64);
        SeedDevice(transport, keys, crypto, device, Array.Empty<string>(), new[]
        {
            (orphan, new byte[] { 9 }, "e3")
        });
        var pointer = JsonSerializer.Deserialize<DevicePointerPayload>(
            transport.Store[keys.GetDevicePointerKey(device)].Content)!;
        transport.Put(pointer.LatestPackageKey!, Encoding.UTF8.GetBytes("{not-json"));

        var preview = await svc.PreviewAsync(Password);
        Assert.False(preview.IsComplete);
        Assert.Empty(preview.Candidates);
        Assert.Contains("закрыта", preview.BlockReason ?? "", StringComparison.OrdinalIgnoreCase);
        Assert.True(transport.Store.ContainsKey(keys.GetBlobKey(orphan)));
    }

    [Fact]
    public async Task Preview_IncompletePagination_IsFailClosed()
    {
        var (svc, transport, keys, _) = MakeCleanup(maxPages: 1, pageSize: 1);
        for (int i = 0; i < 3; i++)
        {
            string sha = i.ToString("x").PadLeft(64, '0');
            transport.Put(keys.GetBlobKey(sha), new byte[] { (byte)i }, "e" + i);
        }

        var preview = await svc.PreviewAsync(Password);
        Assert.False(preview.IsComplete);
        Assert.Empty(preview.Candidates);
    }

    [Fact]
    public async Task Execute_ChangedEtag_AndNewReference_AndRepeatDelete_AreSkipped()
    {
        var (svc, transport, keys, crypto) = MakeCleanup();
        Guid device = Guid.NewGuid();
        string keep = new string('a', 64);
        string orphan = new string('b', 64);
        SeedDevice(transport, keys, crypto, device, new[] { keep }, new[]
        {
            (keep, new byte[] { 1 }, "keep-et"),
            (orphan, new byte[] { 2 }, "orphan-et")
        });

        var preview = await svc.PreviewAsync(Password);
        Assert.True(preview.IsComplete, preview.BlockReason);
        Assert.Single(preview.Candidates);

        string orphanKey = keys.GetBlobKey(orphan);
        var current = transport.Store[orphanKey];
        transport.Store[orphanKey] = (current.Content, "changed-etag", current.Size);

        var execEtag = await svc.ExecuteAsync(preview.Token, Password);
        Assert.True(execEtag.TokenAccepted);
        Assert.Equal(0, execEtag.Deleted);
        Assert.True(execEtag.Skipped >= 1);
        Assert.True(transport.Store.ContainsKey(orphanKey));

        transport.Store[orphanKey] = (current.Content, "orphan-et", current.Size);
        var pointer = JsonSerializer.Deserialize<DevicePointerPayload>(
            transport.Store[keys.GetDevicePointerKey(device)].Content)!;
        transport.Put(pointer.LatestPackageKey!, MakePackageBytes(crypto, device, pointer.LatestPackageId, keep, orphan));

        var execRef = await svc.ExecuteAsync(preview.Token, Password);
        Assert.Equal(0, execRef.Deleted);
        Assert.True(transport.Store.ContainsKey(orphanKey));

        transport.Put(pointer.LatestPackageKey!, MakePackageBytes(crypto, device, pointer.LatestPackageId, keep));
        var previewDelete = await svc.PreviewAsync(Password);
        Assert.Contains(previewDelete.Candidates, c => c.Key == orphanKey);
        var firstDelete = await svc.ExecuteAsync(previewDelete.Token, Password);
        Assert.Equal(1, firstDelete.Deleted);
        var secondDelete = await svc.ExecuteAsync(previewDelete.Token, Password);
        Assert.Equal(0, secondDelete.Deleted);
        Assert.True(secondDelete.Skipped >= 1);
        Assert.DoesNotContain(secondDelete.Items, i => i.Outcome == "deleted");
    }

    [Fact]
    public async Task Execute_NeverDeletesPackagesPointersOrForeignKeys()
    {
        var (svc, transport, keys, crypto) = MakeCleanup();
        Guid device = Guid.NewGuid();
        string orphan = new string('d', 64);
        SeedDevice(transport, keys, crypto, device, Array.Empty<string>(), new[]
        {
            (orphan, new byte[] { 7 }, "or")
        });
        string packageKey = transport.Store.Keys.First(k => k.Contains("/packages/"));
        string pointerKey = keys.GetDevicePointerKey(device);
        transport.Put("qn/secret.bin", Encoding.UTF8.GetBytes("nope"));

        var preview = await svc.PreviewAsync(Password);
        Assert.True(preview.IsComplete, preview.BlockReason);
        var exec = await svc.ExecuteAsync(preview.Token, Password);
        Assert.Equal(1, exec.Deleted);
        Assert.True(transport.Store.ContainsKey(packageKey));
        Assert.True(transport.Store.ContainsKey(pointerKey));
        Assert.True(transport.Store.ContainsKey("qn/secret.bin"));
        Assert.False(transport.Store.ContainsKey(keys.GetBlobKey(orphan)));
    }

    [Fact]
    public async Task Execute_PartialNetworkFailure_ReportsWithoutRepeatingSuccess()
    {
        var (svc, transport, keys, crypto) = MakeCleanup();
        Guid device = Guid.NewGuid();
        string o1 = new string('e', 64);
        string o2 = new string('f', 64);
        SeedDevice(transport, keys, crypto, device, Array.Empty<string>(), new[]
        {
            (o1, new byte[] { 1 }, "x1"),
            (o2, new byte[] { 2 }, "x2")
        });

        var preview = await svc.PreviewAsync(Password);
        Assert.Equal(2, preview.Candidates.Count);
        transport.ThrowOnDelete = true;
        transport.ThrowOnDeleteAfter = 1;
        var exec = await svc.ExecuteAsync(preview.Token, Password);
        Assert.True(exec.Deleted + exec.Failed + exec.Skipped == 2);
        Assert.Equal(1, exec.Deleted);
        Assert.Equal(1, exec.Failed);
        Assert.Equal(1, transport.Store.Count(kv => kv.Key.Contains("/blobs/")));
    }

    [Fact]
    public async Task Execute_Cancel_StopsWithoutDeletingRemaining()
    {
        var (svc, transport, keys, crypto) = MakeCleanup();
        Guid device = Guid.NewGuid();
        string o1 = new string('7', 64);
        string o2 = new string('8', 64);
        SeedDevice(transport, keys, crypto, device, Array.Empty<string>(), new[]
        {
            (o1, new byte[] { 1 }, "c1"),
            (o2, new byte[] { 2 }, "c2")
        });
        var preview = await svc.PreviewAsync(Password);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var exec = await svc.ExecuteAsync(preview.Token, Password, cts.Token);
        Assert.True(exec.Canceled);
        Assert.Equal(0, exec.Deleted);
        Assert.Equal(2, transport.Store.Count(kv => kv.Key.Contains("/blobs/")));
    }

    [Fact]
    public async Task Preview_ListError_IsFailClosed()
    {
        var (svc, transport, keys, crypto) = MakeCleanup();
        Guid device = Guid.NewGuid();
        SeedDevice(transport, keys, crypto, device, Array.Empty<string>(), Array.Empty<(string, byte[], string)>());
        transport.ListError = new CloudOfflineException("list down");
        var preview = await svc.PreviewAsync(Password);
        Assert.False(preview.IsComplete);
        Assert.Empty(preview.Candidates);
    }

    [Fact]
    public async Task Preview_IncrementalHistory_KeepsBlobFromOlderPackage()
    {
        var (svc, transport, keys, crypto) = MakeCleanup();
        Guid device = Guid.NewGuid();
        Guid pkg1 = Guid.NewGuid();
        Guid pkg2 = Guid.NewGuid();
        string historical = new string('a', 64);
        string orphan = new string('b', 64);
        transport.Put(keys.GetPackageKey(device, pkg1), MakePackageBytes(crypto, device, pkg1, historical));
        transport.Put(keys.GetPackageKey(device, pkg2), MakePackageBytes(crypto, device, pkg2));
        transport.Put(keys.GetDevicePointerKey(device), JsonSerializer.SerializeToUtf8Bytes(new DevicePointerPayload
        {
            FormatVersion = 1,
            DeviceId = device,
            LatestPackageId = pkg2,
            LatestPackageKey = keys.GetPackageKey(device, pkg2),
            PackageCount = 2
        }));
        transport.Put(keys.GetBlobKey(historical), new byte[] { 1 }, "hist");
        transport.Put(keys.GetBlobKey(orphan), new byte[] { 2 }, "orph");

        var preview = await svc.PreviewAsync(Password);
        Assert.True(preview.IsComplete, preview.BlockReason);
        Assert.DoesNotContain(preview.Candidates, c => c.Key == keys.GetBlobKey(historical));
        Assert.Contains(preview.Candidates, c => c.Key == keys.GetBlobKey(orphan));

        var exec = await svc.ExecuteAsync(preview.Token, Password);
        Assert.Equal(1, exec.Deleted);
        Assert.True(transport.Store.ContainsKey(keys.GetBlobKey(historical)));
        Assert.False(transport.Store.ContainsKey(keys.GetBlobKey(orphan)));
    }

    [Fact]
    public async Task Preview_IncompletePackageHistoryPagination_IsFailClosed()
    {
        var (svc, transport, keys, crypto) = MakeCleanup(maxPages: 1, pageSize: 1);
        Guid device = Guid.NewGuid();
        var pkgIds = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
        foreach (var id in pkgIds)
            transport.Put(keys.GetPackageKey(device, id), MakePackageBytes(crypto, device, id));
        transport.Put(keys.GetDevicePointerKey(device), JsonSerializer.SerializeToUtf8Bytes(new DevicePointerPayload
        {
            FormatVersion = 1,
            DeviceId = device,
            LatestPackageId = pkgIds[2],
            LatestPackageKey = keys.GetPackageKey(device, pkgIds[2]),
            PackageCount = 3
        }));
        transport.Put(keys.GetBlobKey(new string('c', 64)), new byte[] { 9 }, "only");

        var preview = await svc.PreviewAsync(Password);
        Assert.False(preview.IsComplete);
        Assert.Empty(preview.Candidates);
    }

    [Fact]
    public async Task Preview_MissingPackageRelativeToPackageCount_IsFailClosed()
    {
        var (svc, transport, keys, crypto) = MakeCleanup();
        Guid device = Guid.NewGuid();
        Guid pkg1 = Guid.NewGuid();
        transport.Put(keys.GetPackageKey(device, pkg1), MakePackageBytes(crypto, device, pkg1));
        transport.Put(keys.GetDevicePointerKey(device), JsonSerializer.SerializeToUtf8Bytes(new DevicePointerPayload
        {
            FormatVersion = 1,
            DeviceId = device,
            LatestPackageId = pkg1,
            LatestPackageKey = keys.GetPackageKey(device, pkg1),
            PackageCount = 2
        }));
        transport.Put(keys.GetBlobKey(new string('d', 64)), new byte[] { 3 }, "x");

        var preview = await svc.PreviewAsync(Password);
        Assert.False(preview.IsComplete);
        Assert.Empty(preview.Candidates);
        Assert.Contains("PackageCount", preview.BlockReason ?? "", StringComparison.Ordinal);
    }

    [Fact]
    public async Task Preview_ForeignDeviceIdInEnvelope_IsFailClosed()
    {
        var (svc, transport, keys, crypto) = MakeCleanup();
        Guid device = Guid.NewGuid();
        Guid foreign = Guid.NewGuid();
        Guid pkg = Guid.NewGuid();
        transport.Put(keys.GetPackageKey(device, pkg), MakePackageBytes(crypto, foreign, pkg));
        transport.Put(keys.GetDevicePointerKey(device), JsonSerializer.SerializeToUtf8Bytes(new DevicePointerPayload
        {
            FormatVersion = 1,
            DeviceId = device,
            LatestPackageId = pkg,
            LatestPackageKey = keys.GetPackageKey(device, pkg),
            PackageCount = 1
        }));
        transport.Put(keys.GetBlobKey(new string('e', 64)), new byte[] { 4 }, "x");

        var preview = await svc.PreviewAsync(Password);
        Assert.False(preview.IsComplete);
        Assert.Empty(preview.Candidates);
    }

    [Fact]
    public async Task Preview_ForeignDeviceIdInPointerPackageKey_IsFailClosed()
    {
        var (svc, transport, keys, crypto) = MakeCleanup();
        Guid device = Guid.NewGuid();
        Guid foreign = Guid.NewGuid();
        Guid pkg = Guid.NewGuid();
        transport.Put(keys.GetPackageKey(device, pkg), MakePackageBytes(crypto, device, pkg));
        transport.Put(keys.GetDevicePointerKey(device), JsonSerializer.SerializeToUtf8Bytes(new DevicePointerPayload
        {
            FormatVersion = 1,
            DeviceId = device,
            LatestPackageId = pkg,
            LatestPackageKey = keys.GetPackageKey(foreign, pkg),
            PackageCount = 1
        }));

        var preview = await svc.PreviewAsync(Password);
        Assert.False(preview.IsComplete);
        Assert.Empty(preview.Candidates);
    }

    [Fact]
    public async Task Preview_CorruptOlderPackage_IsFailClosed()
    {
        var (svc, transport, keys, crypto) = MakeCleanup();
        Guid device = Guid.NewGuid();
        Guid pkg1 = Guid.NewGuid();
        Guid pkg2 = Guid.NewGuid();
        string keep = new string('f', 64);
        transport.Put(keys.GetPackageKey(device, pkg1), Encoding.UTF8.GetBytes("{broken-old-package"));
        transport.Put(keys.GetPackageKey(device, pkg2), MakePackageBytes(crypto, device, pkg2));
        transport.Put(keys.GetDevicePointerKey(device), JsonSerializer.SerializeToUtf8Bytes(new DevicePointerPayload
        {
            FormatVersion = 1,
            DeviceId = device,
            LatestPackageId = pkg2,
            LatestPackageKey = keys.GetPackageKey(device, pkg2),
            PackageCount = 2
        }));
        transport.Put(keys.GetBlobKey(keep), new byte[] { 8 }, "k");

        var preview = await svc.PreviewAsync(Password);
        Assert.False(preview.IsComplete);
        Assert.Empty(preview.Candidates);
        Assert.True(transport.Store.ContainsKey(keys.GetBlobKey(keep)));
    }

    [Fact]
    public async Task Preview_UnknownKeyInBlobsPrefix_FailClosed()
    {
        var (svc, transport, keys, crypto) = MakeCleanup();
        Guid device = Guid.NewGuid();
        SeedDevice(transport, keys, crypto, device, Array.Empty<string>(), Array.Empty<(string, byte[], string)>());
        transport.Put(keys.GetBlobsPrefix() + "not-a-hash.bin", new byte[] { 1 }, "z");
        var preview = await svc.PreviewAsync(Password);
        Assert.False(preview.IsComplete);
        Assert.Empty(preview.Candidates);
    }

    [Fact]
    public async Task Preview_Offline_IsFailClosed()
    {
        var (svc, transport, keys, crypto) = MakeCleanup();
        Guid device = Guid.NewGuid();
        SeedDevice(transport, keys, crypto, device, Array.Empty<string>(), Array.Empty<(string, byte[], string)>());
        transport.Offline = true;
        var preview = await svc.PreviewAsync(Password);
        Assert.False(preview.IsComplete);
        Assert.Empty(preview.Candidates);
    }
}
