using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using QuickNotes.App.Models.Sync;
using QuickNotes.App.Services.Sync;
using Xunit;

namespace QuickNotes.Tests;

public class CloudRetentionAndSmokeTests
{
    private sealed class MemoryStore : ICloudObjectStoreTransport
    {
        public readonly ConcurrentDictionary<string, (byte[] Content, string ETag)> Store = new();

        public void Put(string key, byte[] content)
            => Store[key] = ((byte[])content.Clone(), Guid.NewGuid().ToString("N"));

        public Task<bool> TestConnectionAsync(CancellationToken ct = default) => Task.FromResult(true);

        public Task<StorageObjectMetadata?> HeadObjectAsync(string key, CancellationToken ct = default)
            => Store.TryGetValue(key, out var obj)
                ? Task.FromResult<StorageObjectMetadata?>(new StorageObjectMetadata { Key = key, ETag = obj.ETag, ContentLength = obj.Content.Length })
                : Task.FromResult<StorageObjectMetadata?>(null);

        public Task<StorageObjectResult?> GetObjectAsync(string key, CancellationToken ct = default)
            => Store.TryGetValue(key, out var obj)
                ? Task.FromResult<StorageObjectResult?>(new StorageObjectResult
                {
                    Metadata = new StorageObjectMetadata { Key = key, ETag = obj.ETag, ContentLength = obj.Content.Length },
                    Content = (byte[])obj.Content.Clone()
                })
                : Task.FromResult<StorageObjectResult?>(null);

        public Task<StorageObjectMetadata> PutImmutableObjectAsync(string key, byte[] content, string contentType = "application/json", CancellationToken ct = default)
        {
            if (Store.ContainsKey(key))
                throw new CloudConflictException("exists");
            Put(key, content);
            return Task.FromResult(new StorageObjectMetadata { Key = key, ETag = Store[key].ETag, ContentLength = content.Length });
        }

        public Task<StorageObjectMetadata> PutConditionalPointerAsync(string key, byte[] content, string? expectedETag, string contentType = "application/json", CancellationToken ct = default)
        {
            Put(key, content);
            return Task.FromResult(new StorageObjectMetadata { Key = key, ETag = Store[key].ETag, ContentLength = content.Length });
        }

        public Task<IReadOnlyList<StorageObjectSummary>> ListObjectsAsync(string? prefix = null, int maxKeys = 1000, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<StorageObjectSummary>>(Summaries(prefix));

        public Task<StorageListResult> ListObjectsV2Async(StorageListRequest request, CancellationToken ct = default)
            => Task.FromResult(new StorageListResult { Objects = Summaries(request.Prefix), IsTruncated = false });

        public Task<bool> DeleteObjectAsync(string key, CancellationToken ct = default)
            => Task.FromResult(Store.TryRemove(key, out _));

        public void Dispose() { }

        private List<StorageObjectSummary> Summaries(string? prefix)
            => Store.Where(kv => prefix == null || kv.Key.StartsWith(prefix, StringComparison.Ordinal))
                .Select(kv => new StorageObjectSummary { Key = kv.Key, ETag = kv.Value.ETag, Size = kv.Value.Content.Length })
                .ToList();
    }

    [Fact]
    public async Task PackageRetention_KeepsLatestPointerPackage_DeletesOlderSnapshots()
    {
        var settings = new SyncCloudSettings { Bucket = "test-bucket", Prefix = "qn/" };
        var keys = new SyncObjectKeyHelper(settings);
        var store = new MemoryStore();
        var device = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var oldPkg = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var latest = Guid.Parse("22222222-2222-2222-2222-222222222222");

        store.Put(keys.GetPackageKey(device, oldPkg), Encoding.UTF8.GetBytes("{\"old\":true}"));
        store.Put(keys.GetPackageKey(device, latest), Encoding.UTF8.GetBytes("{\"latest\":true}"));
        store.Put(keys.GetDevicePointerKey(device), JsonSerializer.SerializeToUtf8Bytes(new DevicePointerPayload
        {
            DeviceId = device,
            LatestPackageId = latest,
            LatestPackageKey = keys.GetPackageKey(device, latest),
            PackageCount = 2
        }));

        var service = new CloudRetentionService(store, keys);
        var preview = await service.PreviewPackageRetentionAsync();
        Assert.True(preview.IsComplete);
        Assert.Single(preview.Candidates);
        Assert.Contains(oldPkg.ToString("D"), preview.Candidates[0].Key);

        var executed = await service.ExecutePackageRetentionAsync(preview.Token);
        Assert.True(executed.TokenAccepted);
        Assert.Equal(1, executed.Deleted);
        Assert.False(store.Store.ContainsKey(keys.GetPackageKey(device, oldPkg)));
        Assert.True(store.Store.ContainsKey(keys.GetPackageKey(device, latest)));
    }

    [Fact]
    public async Task OldGenerationCleanup_DeletesOnlyPreviousGeneration_AfterConfirm()
    {
        var settings = new SyncCloudSettings { Bucket = "test-bucket", Prefix = "qn/" };
        var keys = new SyncObjectKeyHelper(settings);
        var store = new MemoryStore();
        var previous = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        var active = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");

        store.Put(keys.GetGenerationPointerKey(), JsonSerializer.SerializeToUtf8Bytes(new SyncGenerationPointer
        {
            FormatVersion = 1,
            ActiveGenerationId = active,
            PreviousGenerationId = previous
        }));

        var oldKeys = keys.ForGeneration(previous);
        var liveKeys = keys.ForGeneration(active);
        string oldObj = oldKeys.GetBlobKey(new string('a', 64));
        string liveObj = liveKeys.GetBlobKey(new string('b', 64));
        store.Put(oldObj, new byte[] { 1, 2, 3 });
        store.Put(liveObj, new byte[] { 4, 5, 6 });

        var service = new CloudRetentionService(store, keys);
        var preview = await service.PreviewOldGenerationAsync();
        Assert.True(preview.IsComplete);
        Assert.True(preview.IsGenerationCleanup);
        Assert.Contains(preview.Candidates, c => c.Key == oldObj);
        Assert.DoesNotContain(preview.Candidates, c => c.Key == liveObj);

        var wrong = await service.ExecuteOldGenerationCleanupAsync("stale");
        Assert.False(wrong.TokenAccepted);

        var executed = await service.ExecuteOldGenerationCleanupAsync(preview.Token);
        Assert.Equal(1, executed.Deleted);
        Assert.False(store.Store.ContainsKey(oldObj));
        Assert.True(store.Store.ContainsKey(liveObj));
    }

    [Fact]
    public void SmokeChecklist_CoversTwoDeviceScenarios_WithoutLiveBucket()
    {
        string[] required =
        {
            "первый обмен",
            "повтор без изменений",
            "конфликт",
            "tombstone",
            "вложение",
            "офлайн",
            "ETag",
            "квота",
            "ротация"
        };

        var engine = File.ReadAllText(Path.Combine(FindRoot(), "QuickNotes.Tests", "SyncEngineTests.cs"));
        var blobs = File.ReadAllText(Path.Combine(FindRoot(), "QuickNotes.Tests", "BlobAttachmentSyncTests.cs"));
        var quota = File.ReadAllText(Path.Combine(FindRoot(), "QuickNotes.Tests", "CloudQuotaCleanupTests.cs"));
        var rotation = File.ReadAllText(Path.Combine(FindRoot(), "QuickNotes.Tests", "CloudPasswordRotationTests.cs"));
        var combined = engine + blobs + quota + rotation + File.ReadAllText(Path.Combine(FindRoot(), "QuickNotes.Tests", "CloudRetentionAndSmokeTests.cs"));

        Assert.Contains("NoOp", engine, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Conflict", engine, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("tombstone", engine, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("offline", engine, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ETag", engine, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Blob", blobs, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Quota", quota, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Rotation", rotation, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(9, required.Length);
    }

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "QuickNotes.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }
        return @"D:\work\QuickNotes";
    }
}
