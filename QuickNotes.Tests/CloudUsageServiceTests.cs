using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using QuickNotes.App.Models.Sync;
using QuickNotes.App.Services;
using QuickNotes.App.Services.Sync;
using Xunit;

namespace QuickNotes.Tests;

public class FakeUsageDateTimeProvider : IDateTimeProvider
{
    public DateTime UtcNow { get; set; } = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
    public DateTime Now => UtcNow.ToLocalTime();
}

public class FakeUsageCloudTransport : ICloudObjectStoreTransport
{
    public List<StorageObjectSummary> StoredObjects { get; set; } = new();
    public bool IsOffline { get; set; }
    public bool IsAuthError { get; set; }
    public int ListCallsCount { get; private set; }

    public Task<StorageListResult> ListObjectsV2Async(StorageListRequest request, CancellationToken ct = default)
    {
        ListCallsCount++;
        if (IsOffline) throw new CloudOfflineException("Storage unreachable");
        if (IsAuthError) throw new CloudAuthException("Invalid credentials", 403);

        string prefix = request.Prefix ?? string.Empty;
        var matching = StoredObjects
            .Where(o => o.Key.StartsWith(prefix, StringComparison.Ordinal))
            .OrderBy(o => o.Key, StringComparer.Ordinal)
            .ToList();

        int startIndex = 0;
        if (!string.IsNullOrEmpty(request.ContinuationToken) && int.TryParse(request.ContinuationToken, out int idx))
        {
            startIndex = idx;
        }

        int maxKeys = request.MaxKeys > 0 ? request.MaxKeys : 1000;
        var page = matching.Skip(startIndex).Take(maxKeys).ToList();
        int nextIndex = startIndex + page.Count;
        bool isTruncated = nextIndex < matching.Count;

        return Task.FromResult(new StorageListResult
        {
            Objects = page,
            IsTruncated = isTruncated,
            NextContinuationToken = isTruncated ? nextIndex.ToString() : null
        });
    }

    public Task<IReadOnlyList<StorageObjectSummary>> ListObjectsAsync(string? prefix = null, int maxKeys = 1000, CancellationToken ct = default) =>
        throw new NotImplementedException();
    public Task<StorageObjectResult?> GetObjectAsync(string key, CancellationToken ct = default) =>
        throw new NotImplementedException();
    public Task<StorageObjectMetadata> PutImmutableObjectAsync(string key, byte[] content, string contentType = "application/json", CancellationToken ct = default) =>
        throw new NotImplementedException();
    public Task<StorageObjectMetadata> PutConditionalPointerAsync(string key, byte[] content, string? expectedETag, string contentType = "application/json", CancellationToken ct = default) =>
        throw new NotImplementedException();
    public Task<bool> TestConnectionAsync(CancellationToken ct = default) => Task.FromResult(true);
    public Task<StorageObjectMetadata?> HeadObjectAsync(string key, CancellationToken ct = default) => Task.FromResult<StorageObjectMetadata?>(null);
    public Task<bool> DeleteObjectAsync(string key, CancellationToken ct = default) =>
        throw new NotImplementedException();
    public void Dispose() { }
}

public class CloudUsageServiceTests : IDisposable
{
    private readonly FakeUsageCloudTransport _transport;
    private readonly SyncCloudSettings _settings;
    private readonly FakeSchedulerCredentials _creds;
    private readonly FakeUsageDateTimeProvider _timeProvider;
    private readonly List<IDisposable> _disposables = new();

    public CloudUsageServiceTests()
    {
        _transport = new FakeUsageCloudTransport();
        _settings = new SyncCloudSettings
        {
            Enabled = true,
            Bucket = "my-bucket",
            Prefix = "quicknotes/"
        };
        _creds = new FakeSchedulerCredentials();
        _timeProvider = new FakeUsageDateTimeProvider();
    }

    public void Dispose()
    {
        foreach (var d in _disposables)
        {
            try { d.Dispose(); } catch { }
        }
    }

    private CloudUsageService CreateService(
        int maxPages = CloudUsageService.DefaultMaxPages,
        int maxObjects = CloudUsageService.DefaultMaxObjects,
        int pageSize = CloudUsageService.DefaultPageSize,
        TimeSpan? cacheDuration = null)
    {
        var svc = new CloudUsageService(
            _transport,
            _settings,
            _creds,
            _timeProvider,
            maxPages,
            maxObjects,
            pageSize,
            cacheDuration);
        _disposables.Add(svc);
        return svc;
    }

    [Fact]
    public async Task CalculateUsageAsync_PrefixIsolation_OnlyCountsPrefixObjects()
    {
        _settings.Prefix = "quicknotes/";
        _transport.StoredObjects = new List<StorageObjectSummary>
        {
            new() { Key = "quicknotes/v1/packages/pkg1.bin", Size = 1000 },
            new() { Key = "quicknotes/v1/pointers/device1.json", Size = 200 },
            new() { Key = "other_app/data.bin", Size = 999999 },
            new() { Key = "root_file.txt", Size = 5000 }
        };

        var service = CreateService();
        var result = await service.CalculateUsageAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal(1200, result.TotalBytes);
        Assert.Equal(2, result.TotalObjects);
        Assert.False(result.IsTruncated);
        Assert.Equal("quicknotes/", result.Prefix);
        Assert.Equal("my-bucket", result.Bucket);
    }

    [Fact]
    public async Task CalculateUsageAsync_MultiplePages_PaginatesUntilExhausted()
    {
        _settings.Prefix = "v1/";
        _transport.StoredObjects = Enumerable.Range(1, 25)
            .Select(i => new StorageObjectSummary { Key = $"v1/pkg_{i:D3}.json", Size = 100 })
            .ToList();

        // Page size = 10 => 3 pages total (10, 10, 5)
        var service = CreateService(pageSize: 10);
        var result = await service.CalculateUsageAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal(2500, result.TotalBytes);
        Assert.Equal(25, result.TotalObjects);
        Assert.Equal(3, result.TotalPages);
        Assert.False(result.IsTruncated);
        Assert.Equal(3, _transport.ListCallsCount);
    }

    [Fact]
    public async Task CalculateUsageAsync_MaxPagesLimit_StopsAndMarksTruncated()
    {
        _settings.Prefix = "v1/";
        _transport.StoredObjects = Enumerable.Range(1, 50)
            .Select(i => new StorageObjectSummary { Key = $"v1/pkg_{i:D3}.json", Size = 10 })
            .ToList();

        // maxPages = 2, pageSize = 10 => stops after 20 objects
        var service = CreateService(maxPages: 2, pageSize: 10);
        var result = await service.CalculateUsageAsync();

        Assert.True(result.IsSuccess);
        Assert.True(result.IsTruncated);
        Assert.True(result.IsPartial);
        Assert.Equal(2, result.TotalPages);
        Assert.Equal(20, result.TotalObjects);
        Assert.Equal(200, result.TotalBytes);
        Assert.Equal(2, _transport.ListCallsCount);
    }

    [Fact]
    public async Task CalculateUsageAsync_MaxObjectsLimit_StopsAndMarksTruncated()
    {
        _settings.Prefix = "v1/";
        _transport.StoredObjects = Enumerable.Range(1, 30)
            .Select(i => new StorageObjectSummary { Key = $"v1/pkg_{i:D3}.json", Size = 10 })
            .ToList();

        // maxObjects = 15, pageSize = 10
        var service = CreateService(maxObjects: 15, pageSize: 10);
        var result = await service.CalculateUsageAsync();

        Assert.True(result.IsSuccess);
        Assert.True(result.IsTruncated);
        Assert.True(result.IsPartial);
        Assert.Equal(15, result.TotalObjects);
        Assert.Equal(150, result.TotalBytes);
    }

    [Fact]
    public async Task CalculateUsageAsync_OverflowProtection_ClampsToLongMaxValue()
    {
        _settings.Prefix = "v1/";
        _transport.StoredObjects = new List<StorageObjectSummary>
        {
            new() { Key = "v1/huge1.bin", Size = long.MaxValue - 50 },
            new() { Key = "v1/huge2.bin", Size = 100 }
        };

        var service = CreateService();
        var result = await service.CalculateUsageAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal(long.MaxValue, result.TotalBytes);
        Assert.True(result.IsTruncated);
    }

    [Fact]
    public async Task CalculateUsageAsync_Caching_ReturnsCachedResultWithinCacheDuration()
    {
        _settings.Prefix = "v1/";
        _transport.StoredObjects = new List<StorageObjectSummary>
        {
            new() { Key = "v1/pkg1.json", Size = 500 }
        };

        var service = CreateService(cacheDuration: TimeSpan.FromMinutes(5));
        var result1 = await service.CalculateUsageAsync();
        Assert.Equal(1, _transport.ListCallsCount);

        // Advance 2 minutes (less than 5 min)
        _timeProvider.UtcNow += TimeSpan.FromMinutes(2);
        var result2 = await service.CalculateUsageAsync();

        Assert.Same(result1, result2);
        Assert.Equal(1, _transport.ListCallsCount); // No new transport call
    }

    [Fact]
    public async Task CalculateUsageAsync_ForceRefresh_BypassesCache()
    {
        _settings.Prefix = "v1/";
        _transport.StoredObjects = new List<StorageObjectSummary>
        {
            new() { Key = "v1/pkg1.json", Size = 500 }
        };

        var service = CreateService(cacheDuration: TimeSpan.FromMinutes(5));
        var result1 = await service.CalculateUsageAsync();
        Assert.Equal(1, _transport.ListCallsCount);

        // Force refresh immediately
        var result2 = await service.CalculateUsageAsync(forceRefresh: true);

        Assert.NotSame(result1, result2);
        Assert.Equal(2, _transport.ListCallsCount);
    }

    [Fact]
    public async Task CalculateUsageAsync_WhenOffline_ReturnsOfflineResult()
    {
        _transport.IsOffline = true;
        var service = CreateService();

        var result = await service.CalculateUsageAsync();

        Assert.False(result.IsSuccess);
        Assert.True(result.IsOffline);
        Assert.Contains("Storage unreachable", result.ErrorMessage);
    }

    [Fact]
    public async Task CalculateUsageAsync_WhenAuthError_ReturnsAuthResult()
    {
        _transport.IsAuthError = true;
        var service = CreateService();

        var result = await service.CalculateUsageAsync();

        Assert.False(result.IsSuccess);
        Assert.True(result.IsAuthError);
        Assert.Contains("credentials", result.ErrorMessage);
    }

    [Fact]
    public async Task CalculateUsageAsync_WhenUnconfigured_ReturnsFailureWithoutCallingTransport()
    {
        _settings.Bucket = "";
        var service = CreateService();

        var result = await service.CalculateUsageAsync();

        Assert.False(result.IsSuccess);
        Assert.Contains("не настроено", result.ErrorMessage);
        Assert.Equal(0, _transport.ListCallsCount);
    }

    [Fact]
    public void Formatting_FormatBytes_FormatsUnitsCorrectly()
    {
        Assert.Equal("0 Б", CloudUsageResult.FormatBytes(0));
        Assert.Equal("512 Б", CloudUsageResult.FormatBytes(512));
        Assert.Equal($"{(2048.0 / 1024):F1} КБ", CloudUsageResult.FormatBytes(2048));
        Assert.Equal($"{5.0:F1} МБ", CloudUsageResult.FormatBytes(5 * 1024 * 1024));
        Assert.Equal($"{3.0:F2} ГБ", CloudUsageResult.FormatBytes(3L * 1024 * 1024 * 1024));
    }

    [Fact]
    public void Formatting_FormatUsageText_HandlesTruncatedAndErrors()
    {
        var truncated = CloudUsageResult.Success(
            totalBytes: 50 * 1024 * 1024,
            totalObjects: 50000,
            totalPages: 50,
            isTruncated: true,
            prefix: "v1/",
            bucket: "my-bucket");

        string truncText = truncated.FormatUsageText();
        Assert.StartsWith("≥ 50", truncText);
        Assert.Contains("МБ", truncText);
        Assert.Contains("50000 объектов", truncText);

        var normal = CloudUsageResult.Success(
            totalBytes: 2 * 1024 * 1024,
            totalObjects: 12,
            totalPages: 1,
            isTruncated: false,
            prefix: "v1/",
            bucket: "my-bucket");

        string normalText = normal.FormatUsageText();
        Assert.StartsWith("2", normalText);
        Assert.Contains("МБ (12 объектов)", normalText);

        var offline = CloudUsageResult.Offline("No network");
        Assert.Equal("Офлайн (нет связи с хранилищем)", offline.FormatUsageText());
    }
}
