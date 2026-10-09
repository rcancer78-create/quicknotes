using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using QuickNotes.App.Models.Sync;

namespace QuickNotes.App.Services.Sync;

/// <summary>
/// Abstraction for private cloud object-storage transport (compatible with Yandex Object Storage S3 API).
/// Enforces immutable object writes, conditional pointer updates via ETag/If-Match,
/// and safe error classification.
/// </summary>
public interface ICloudObjectStoreTransport : IDisposable
{
    /// <summary>
    /// Tests connectivity and credentials against the configured bucket.
    /// </summary>
    Task<bool> TestConnectionAsync(CancellationToken ct = default);

    /// <summary>
    /// Retrieves object metadata (ETag, size, last-modified) without downloading the body.
    /// Returns null if the object does not exist (HTTP 404).
    /// </summary>
    Task<StorageObjectMetadata?> HeadObjectAsync(string key, CancellationToken ct = default);

    /// <summary>
    /// Downloads an object's payload and metadata.
    /// Returns null if the object does not exist (HTTP 404).
    /// </summary>
    Task<StorageObjectResult?> GetObjectAsync(string key, CancellationToken ct = default);

    /// <summary>
    /// Puts an immutable object. Prohibits silent overwriting.
    /// If an object with the same key already exists, throws <see cref="CloudConflictException"/>.
    /// </summary>
    Task<StorageObjectMetadata> PutImmutableObjectAsync(
        string key,
        byte[] content,
        string contentType = "application/json",
        CancellationToken ct = default);

    /// <summary>
    /// Conditionally puts/updates a technical pointer or manifest object using ETag.
    /// If expectedETag is null or empty, creates the object only if it does not already exist (If-None-Match: *).
    /// If expectedETag is provided, updates only if the remote ETag matches (If-Match: expectedETag).
    /// Throws <see cref="CloudConflictException"/> on concurrency conflict or lost update.
    /// </summary>
    Task<StorageObjectMetadata> PutConditionalPointerAsync(
        string key,
        byte[] content,
        string? expectedETag,
        string contentType = "application/json",
        CancellationToken ct = default);

    /// <summary>
    /// Lists objects matching an optional key prefix up to maxKeys.
    /// </summary>
    Task<IReadOnlyList<StorageObjectSummary>> ListObjectsAsync(
        string? prefix = null,
        int maxKeys = 1000,
        CancellationToken ct = default);

    /// <summary>
    /// Paginated ListObjectsV2 API with Delimiter, CommonPrefixes, and continuation token support.
    /// </summary>
    Task<StorageListResult> ListObjectsV2Async(
        StorageListRequest request,
        CancellationToken ct = default);

    /// <summary>
    /// Safely deletes an object if needed.
    /// </summary>
    Task<bool> DeleteObjectAsync(string key, CancellationToken ct = default);
}
