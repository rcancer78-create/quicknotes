using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using QuickNotes.App.Models.Sync;
using QuickNotes.App.Services.Sync;

namespace QuickNotes.Tests;

/// <summary>
/// Deterministic in-memory object store for production <see cref="SyncEngine"/> tests.
/// Faults are explicit flags; no live S3.
/// </summary>
public class FaultInjectingCloudObjectStoreTransport : ICloudObjectStoreTransport
{
    public class StoredObject
    {
        public byte[] Content { get; set; } = Array.Empty<byte>();
        public string ETag { get; set; } = string.Empty;
        public DateTimeOffset LastModified { get; set; } = DateTimeOffset.UtcNow;
        public string ContentType { get; set; } = "application/json";
    }

    public readonly ConcurrentDictionary<string, StoredObject> Store = new();

    public bool IsOffline { get; set; }
    public bool IsAuthError { get; set; }
    public bool ThrowQuotaOnPut { get; set; }
    public bool ThrowQuotaOnList { get; set; }
    public bool ThrowCorruptionOnGet { get; set; }
    public long? MaxStoreBytes { get; set; }
    public bool ThrowOnPutConditionalPointer { get; set; }
    public string? ThrowOnPutImmutableKeyContains { get; set; }
    public bool SimulatePointerConflictOnPut { get; set; }
    public int? ForceMaxKeys { get; set; }
    public Func<Task>? BeforeListObjectsAsync { get; set; }

    public int PutImmutableCallsCount { get; private set; }
    public int PutConditionalCallsCount { get; private set; }
    public int GetObjectCallsCount { get; private set; }
    public int HeadObjectCallsCount { get; private set; }
    public int ListObjectsV2CallsCount { get; private set; }
    public List<string> PutImmutableKeyOrder { get; } = new();

    public Task<bool> TestConnectionAsync(CancellationToken ct = default)
    {
        ThrowIfUnavailable();
        return Task.FromResult(true);
    }

    public Task<StorageObjectMetadata?> HeadObjectAsync(string key, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ThrowIfUnavailable();

        HeadObjectCallsCount++;
        if (Store.TryGetValue(key, out var obj))
        {
            return Task.FromResult<StorageObjectMetadata?>(new StorageObjectMetadata
            {
                Key = key,
                ETag = obj.ETag,
                ContentLength = obj.Content.Length,
                LastModified = obj.LastModified,
                ContentType = obj.ContentType
            });
        }

        return Task.FromResult<StorageObjectMetadata?>(null);
    }

    public Task<StorageObjectResult?> GetObjectAsync(string key, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ThrowIfUnavailable();
        if (ThrowCorruptionOnGet)
        {
            throw new CloudCorruptionException("Simulated corrupted remote object.", key);
        }

        GetObjectCallsCount++;
        if (Store.TryGetValue(key, out var obj))
        {
            return Task.FromResult<StorageObjectResult?>(new StorageObjectResult
            {
                Metadata = new StorageObjectMetadata
                {
                    Key = key,
                    ETag = obj.ETag,
                    ContentLength = obj.Content.Length,
                    LastModified = obj.LastModified,
                    ContentType = obj.ContentType
                },
                Content = (byte[])obj.Content.Clone()
            });
        }

        return Task.FromResult<StorageObjectResult?>(null);
    }

    public Task<StorageObjectMetadata> PutImmutableObjectAsync(
        string key,
        byte[] content,
        string contentType = "application/json",
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ThrowIfUnavailable();

        PutImmutableCallsCount++;
        PutImmutableKeyOrder.Add(key);
        if (!string.IsNullOrEmpty(ThrowOnPutImmutableKeyContains) &&
            key.Contains(ThrowOnPutImmutableKeyContains, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Simulated crash during immutable put.");
        }

        ThrowIfPutQuotaOrFull(content);

        if (Store.TryGetValue(key, out var existing))
        {
            throw new CloudConflictException(
                $"Конфликт: неизменяемый объект '{key}' уже существует.",
                objectKey: key,
                actualETag: existing.ETag,
                statusCode: 409);
        }

        string etag = Guid.NewGuid().ToString("N");
        var stored = new StoredObject
        {
            Content = (byte[])content.Clone(),
            ETag = etag,
            LastModified = DateTimeOffset.UtcNow,
            ContentType = contentType
        };

        if (!Store.TryAdd(key, stored))
        {
            throw new CloudConflictException($"Конфликт параллельной записи объекта '{key}'.", objectKey: key, statusCode: 412);
        }

        return Task.FromResult(new StorageObjectMetadata
        {
            Key = key,
            ETag = etag,
            ContentLength = content.Length,
            LastModified = stored.LastModified,
            ContentType = contentType
        });
    }

    public Task<StorageObjectMetadata> PutConditionalPointerAsync(
        string key,
        byte[] content,
        string? expectedETag,
        string contentType = "application/json",
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ThrowIfUnavailable();

        PutConditionalCallsCount++;
        if (ThrowOnPutConditionalPointer)
        {
            throw new InvalidOperationException("Simulated crash during conditional pointer update.");
        }

        if (SimulatePointerConflictOnPut)
        {
            throw new CloudConflictException("Simulated ETag conflict.", objectKey: key, expectedETag: expectedETag, actualETag: "competing-etag-999", statusCode: 412);
        }

        ThrowIfPutQuotaOrFull(content, replacingKey: string.IsNullOrEmpty(StorageObjectMetadata.NormalizeETag(expectedETag)) ? null : key);

        string? cleanExpected = StorageObjectMetadata.NormalizeETag(expectedETag);
        string newETag = Guid.NewGuid().ToString("N");

        if (string.IsNullOrEmpty(cleanExpected))
        {
            var newObj = new StoredObject
            {
                Content = (byte[])content.Clone(),
                ETag = newETag,
                LastModified = DateTimeOffset.UtcNow,
                ContentType = contentType
            };

            if (!Store.TryAdd(key, newObj))
            {
                throw new CloudConflictException(
                    $"Конфликт создания указателя '{key}': объект уже существует.",
                    objectKey: key,
                    statusCode: 412);
            }
        }
        else
        {
            if (!Store.TryGetValue(key, out var current))
            {
                throw new CloudConflictException(
                    $"Конфликт обновления указателя '{key}': объект не найден в хранилище.",
                    objectKey: key,
                    expectedETag: cleanExpected,
                    statusCode: 412);
            }

            if (current.ETag != cleanExpected)
            {
                throw new CloudConflictException(
                    $"Конфликт обновления указателя '{key}': ETag не совпадает. Ожидался '{cleanExpected}', актуальный '{current.ETag}'.",
                    objectKey: key,
                    expectedETag: cleanExpected,
                    actualETag: current.ETag,
                    statusCode: 412);
            }

            current.Content = (byte[])content.Clone();
            current.ETag = newETag;
            current.LastModified = DateTimeOffset.UtcNow;
        }

        return Task.FromResult(new StorageObjectMetadata
        {
            Key = key,
            ETag = newETag,
            ContentLength = content.Length,
            LastModified = DateTimeOffset.UtcNow,
            ContentType = contentType
        });
    }

    public async Task<IReadOnlyList<StorageObjectSummary>> ListObjectsAsync(
        string? prefix = null,
        int maxKeys = 1000,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (BeforeListObjectsAsync != null)
        {
            await BeforeListObjectsAsync().ConfigureAwait(false);
        }

        ThrowIfUnavailable();
        ThrowIfListQuota();

        var list = Store
            .OrderBy(kvp => kvp.Key, StringComparer.Ordinal)
            .Where(kvp => string.IsNullOrEmpty(prefix) || kvp.Key.StartsWith(prefix, StringComparison.Ordinal))
            .Take(maxKeys)
            .Select(kvp => new StorageObjectSummary
            {
                Key = kvp.Key,
                ETag = kvp.Value.ETag,
                Size = kvp.Value.Content.Length,
                LastModified = kvp.Value.LastModified
            })
            .ToList();

        return list;
    }

    public async Task<StorageListResult> ListObjectsV2Async(
        StorageListRequest request,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (BeforeListObjectsAsync != null)
        {
            await BeforeListObjectsAsync().ConfigureAwait(false);
        }

        ThrowIfUnavailable();
        ThrowIfListQuota();

        ListObjectsV2CallsCount++;
        string prefix = request.Prefix ?? string.Empty;
        string? delimiter = string.IsNullOrEmpty(request.Delimiter) ? null : request.Delimiter;
        int maxKeys = ForceMaxKeys ?? (request.MaxKeys > 0 ? request.MaxKeys : 1000);
        string? token = request.ContinuationToken;

        var sortedKeys = Store.Keys
            .Where(k => k.StartsWith(prefix, StringComparison.Ordinal))
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();

        int startIndex = 0;
        if (!string.IsNullOrEmpty(token))
        {
            startIndex = sortedKeys.FindIndex(k => string.Compare(k, token, StringComparison.Ordinal) >= 0);
            if (startIndex < 0)
            {
                startIndex = sortedKeys.Count;
            }
        }

        var objects = new List<StorageObjectSummary>();
        var commonPrefixes = new HashSet<string>(StringComparer.Ordinal);
        int currentIndex = startIndex;
        bool isTruncated = false;
        string? nextContinuationToken = null;

        while (currentIndex < sortedKeys.Count)
        {
            string key = sortedKeys[currentIndex];
            if (delimiter != null)
            {
                string rest = key.Substring(prefix.Length);
                int delimIndex = rest.IndexOf(delimiter, StringComparison.Ordinal);
                if (delimIndex >= 0)
                {
                    string cp = prefix + rest.Substring(0, delimIndex + delimiter.Length);
                    if (!commonPrefixes.Contains(cp))
                    {
                        if (objects.Count + commonPrefixes.Count >= maxKeys)
                        {
                            isTruncated = true;
                            nextContinuationToken = key;
                            break;
                        }
                        commonPrefixes.Add(cp);
                    }
                    currentIndex++;
                    continue;
                }
            }

            if (objects.Count + commonPrefixes.Count >= maxKeys)
            {
                isTruncated = true;
                nextContinuationToken = key;
                break;
            }

            if (Store.TryGetValue(key, out var stored))
            {
                objects.Add(new StorageObjectSummary
                {
                    Key = key,
                    ETag = stored.ETag,
                    Size = stored.Content.Length,
                    LastModified = stored.LastModified
                });
            }
            currentIndex++;
        }

        return new StorageListResult
        {
            Objects = objects,
            CommonPrefixes = commonPrefixes.OrderBy(p => p, StringComparer.Ordinal).ToList(),
            IsTruncated = isTruncated,
            NextContinuationToken = nextContinuationToken
        };
    }

    public Task<bool> DeleteObjectAsync(string key, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(Store.TryRemove(key, out _));
    }

    public void CorruptObject(string key, byte[] newPayload)
    {
        if (Store.TryGetValue(key, out var obj))
        {
            obj.Content = newPayload;
        }
    }

    public bool ContainsUtf8(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        foreach (var kvp in Store)
        {
            if (kvp.Key.Contains(value, StringComparison.Ordinal))
            {
                return true;
            }

            if (Encoding.UTF8.GetString(kvp.Value.Content).Contains(value, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    public void Dispose() { }

    private void ThrowIfUnavailable()
    {
        if (IsOffline) throw new CloudOfflineException("Object store is offline.");
        if (IsAuthError) throw new CloudAuthException("Access denied to bucket.", 403);
    }

    private void ThrowIfListQuota()
    {
        if (ThrowQuotaOnList)
        {
            throw new CloudQuotaException("Simulated object-store quota exceeded.", 507);
        }
    }

    private void ThrowIfPutQuotaOrFull(byte[] content, string? replacingKey = null)
    {
        if (ThrowQuotaOnPut)
        {
            throw new CloudQuotaException("Simulated object-store quota exceeded.", 507);
        }

        if (!MaxStoreBytes.HasValue)
        {
            return;
        }

        long used = 0;
        foreach (var kvp in Store)
        {
            if (replacingKey != null && string.Equals(kvp.Key, replacingKey, StringComparison.Ordinal))
            {
                continue;
            }

            used += kvp.Value.Content.Length;
        }

        if (used + content.LongLength > MaxStoreBytes.Value)
        {
            throw new CloudQuotaException("Simulated object store is full.", 507);
        }
    }
}
