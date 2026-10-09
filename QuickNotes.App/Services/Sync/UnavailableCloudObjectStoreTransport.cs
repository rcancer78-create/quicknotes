using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using QuickNotes.App.Models.Sync;

namespace QuickNotes.App.Services.Sync;

/// <summary>
/// Isolated-profile transport: never opens a network connection.
/// </summary>
public sealed class UnavailableCloudObjectStoreTransport : ICloudObjectStoreTransport
{
    public const string DefaultReason = "Облачный транспорт недоступен в изолированном профиле.";

    private readonly string _reason;
    private bool _disposed;

    public UnavailableCloudObjectStoreTransport(string? reason = null)
    {
        _reason = string.IsNullOrWhiteSpace(reason) ? DefaultReason : reason;
    }

    public Task<bool> TestConnectionAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        throw new CloudOfflineException(_reason);
    }

    public Task<StorageObjectMetadata?> HeadObjectAsync(string key, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        throw new CloudOfflineException(_reason);
    }

    public Task<StorageObjectResult?> GetObjectAsync(string key, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        throw new CloudOfflineException(_reason);
    }

    public Task<StorageObjectMetadata> PutImmutableObjectAsync(
        string key,
        byte[] content,
        string contentType = "application/json",
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        throw new CloudOfflineException(_reason);
    }

    public Task<StorageObjectMetadata> PutConditionalPointerAsync(
        string key,
        byte[] content,
        string? expectedETag,
        string contentType = "application/json",
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        throw new CloudOfflineException(_reason);
    }

    public Task<IReadOnlyList<StorageObjectSummary>> ListObjectsAsync(
        string? prefix = null,
        int maxKeys = 1000,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        throw new CloudOfflineException(_reason);
    }

    public Task<StorageListResult> ListObjectsV2Async(
        StorageListRequest request,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        throw new CloudOfflineException(_reason);
    }

    public Task<bool> DeleteObjectAsync(string key, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        throw new CloudOfflineException(_reason);
    }

    public void Dispose()
    {
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    internal bool IsDisposed => _disposed;
}
