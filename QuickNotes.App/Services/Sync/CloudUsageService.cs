using System;
using System.Threading;
using System.Threading.Tasks;
using QuickNotes.App.Models.Sync;

namespace QuickNotes.App.Services.Sync;

/// <summary>
/// Safe and isolated cloud usage calculator for the QuickNotes object space.
/// Never queries the root bucket without prefix and never exceeds page/object caps.
/// </summary>
public class CloudUsageService : ICloudUsageService
{
    public const int DefaultMaxPages = 50;
    public const int DefaultMaxObjects = 50_000;
    public const int DefaultPageSize = 1000;
    public static readonly TimeSpan DefaultCacheDuration = TimeSpan.FromMinutes(5);

    private readonly ICloudObjectStoreTransport _transport;
    private readonly SyncCloudSettings _settings;
    private readonly IS3CredentialsStorage _credentialsStorage;
    private readonly IDateTimeProvider _dateTimeProvider;
    private readonly int _maxPages;
    private readonly int _maxObjects;
    private readonly int _pageSize;
    private readonly TimeSpan _cacheDuration;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private CloudUsageResult? _cachedResult;
    private bool _isCalculating;
    private bool _disposed;

    public CloudUsageResult? CachedUsage => _cachedResult;
    public bool IsCalculating => _isCalculating;

    public CloudUsageService(
        ICloudObjectStoreTransport transport,
        SyncCloudSettings settings,
        IS3CredentialsStorage credentialsStorage,
        IDateTimeProvider? dateTimeProvider = null,
        int maxPages = DefaultMaxPages,
        int maxObjects = DefaultMaxObjects,
        int pageSize = DefaultPageSize,
        TimeSpan? cacheDuration = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _credentialsStorage = credentialsStorage ?? throw new ArgumentNullException(nameof(credentialsStorage));
        _dateTimeProvider = dateTimeProvider ?? new SystemDateTimeProvider();
        _maxPages = Math.Max(1, maxPages);
        _maxObjects = Math.Max(1, maxObjects);
        _pageSize = Math.Clamp(pageSize, 1, 1000);
        _cacheDuration = cacheDuration ?? DefaultCacheDuration;
    }

    public async Task<CloudUsageResult> CalculateUsageAsync(
        bool forceRefresh = false,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Check cache first if forceRefresh is false
        if (!forceRefresh && _cachedResult != null && _cachedResult.IsSuccess)
        {
            var age = _dateTimeProvider.UtcNow - _cachedResult.CalculatedAtUtc;
            if (age < _cacheDuration)
            {
                return _cachedResult;
            }
        }

        string bucket = _settings.Bucket ?? string.Empty;
        string prefix = GetEffectivePrefix();

        if (string.IsNullOrWhiteSpace(bucket) || !_credentialsStorage.HasCredentials())
        {
            var unconfigured = CloudUsageResult.Failure(
                "Облако не настроено: укажите имя бакета и сохраните ключи S3.",
                prefix,
                bucket);
            _cachedResult = unconfigured;
            return unconfigured;
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Re-check cache under lock
            if (!forceRefresh && _cachedResult != null && _cachedResult.IsSuccess)
            {
                var age = _dateTimeProvider.UtcNow - _cachedResult.CalculatedAtUtc;
                if (age < _cacheDuration)
                {
                    return _cachedResult;
                }
            }

            _isCalculating = true;

            long totalBytes = 0;
            int totalObjects = 0;
            int totalPages = 0;
            bool isTruncated = false;
            string? continuationToken = null;

            while (totalPages < _maxPages && totalObjects < _maxObjects)
            {
                ct.ThrowIfCancellationRequested();

                var request = new StorageListRequest
                {
                    Prefix = prefix,
                    Delimiter = null,
                    MaxKeys = _pageSize,
                    ContinuationToken = continuationToken
                };

                StorageListResult page = await _transport.ListObjectsV2Async(request, ct).ConfigureAwait(false);
                totalPages++;

                foreach (var obj in page.Objects)
                {
                    // Prefix isolation guard: verify key begins with the designated prefix
                    if (!obj.Key.StartsWith(prefix, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    totalObjects++;

                    // Overflow protection
                    if (obj.Size > 0)
                    {
                        if (long.MaxValue - totalBytes < obj.Size)
                        {
                            totalBytes = long.MaxValue;
                            isTruncated = true;
                            break;
                        }
                        totalBytes += obj.Size;
                    }

                    if (totalObjects >= _maxObjects)
                    {
                        isTruncated = true;
                        break;
                    }
                }

                if (isTruncated)
                {
                    break;
                }

                if (!page.IsTruncated || string.IsNullOrEmpty(page.NextContinuationToken))
                {
                    break;
                }

                continuationToken = page.NextContinuationToken;
            }

            if (totalPages >= _maxPages && !string.IsNullOrEmpty(continuationToken))
            {
                isTruncated = true;
            }

            var successResult = CloudUsageResult.Success(
                totalBytes,
                totalObjects,
                totalPages,
                isTruncated,
                prefix,
                bucket);

            _cachedResult = successResult;
            return successResult;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (CloudOfflineException ex)
        {
            var offline = CloudUsageResult.Offline(ex.Message, prefix, bucket);
            _cachedResult = offline;
            return offline;
        }
        catch (CloudAuthException ex)
        {
            var auth = CloudUsageResult.AuthError(ex.Message, prefix, bucket);
            _cachedResult = auth;
            return auth;
        }
        catch (Exception ex)
        {
            ErrorLogService.Write("CloudUsageService.CalculateUsage", ex);
            var failure = CloudUsageResult.Failure(ex.Message, prefix, bucket);
            _cachedResult = failure;
            return failure;
        }
        finally
        {
            _isCalculating = false;
            try
            {
                _gate.Release();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    public void InvalidateCache()
    {
        _cachedResult = null;
    }

    public string GetEffectivePrefix()
    {
        if (!string.IsNullOrWhiteSpace(_settings.Prefix))
        {
            return SyncCloudSettingsValidator.NormalizePrefix(_settings.Prefix);
        }

        // Default namespace prefix if no custom prefix was given
        return $"{SyncObjectKeyHelper.DefaultApiVersion}/";
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _gate.Dispose();
    }
}
