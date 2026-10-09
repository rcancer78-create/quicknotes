using System;

namespace QuickNotes.App.Models.Sync;

public class StorageObjectMetadata
{
    public string Key { get; set; } = string.Empty;
    public string ETag { get; set; } = string.Empty;
    public long ContentLength { get; set; }
    public DateTimeOffset? LastModified { get; set; }
    public string? ContentType { get; set; }

    public static string NormalizeETag(string? etag)
    {
        if (string.IsNullOrWhiteSpace(etag))
            return string.Empty;

        return etag.Trim().Trim('\"');
    }
}

public class StorageObjectResult
{
    public StorageObjectMetadata Metadata { get; set; } = new();
    public byte[] Content { get; set; } = Array.Empty<byte>();
}

public class StorageObjectSummary
{
    public string Key { get; set; } = string.Empty;
    public string ETag { get; set; } = string.Empty;
    public long Size { get; set; }
    public DateTimeOffset? LastModified { get; set; }
}

public class StorageListRequest
{
    public string? Prefix { get; set; }
    public string? Delimiter { get; set; }
    public string? ContinuationToken { get; set; }
    public int MaxKeys { get; set; } = 1000;
}

public class StorageListResult
{
    public IReadOnlyList<StorageObjectSummary> Objects { get; set; } = Array.Empty<StorageObjectSummary>();
    public IReadOnlyList<string> CommonPrefixes { get; set; } = Array.Empty<string>();
    public bool IsTruncated { get; set; }
    public string? NextContinuationToken { get; set; }
}

/// <summary>
/// Technical device pointer metadata stored at {prefix}devices/{deviceId}/pointer.json.
/// Used to track the latest package uploaded by a particular device and updated conditionally using ETag.
/// Contains NO user notes or plaintext secrets.
/// </summary>
public class DevicePointerPayload
{
    public int FormatVersion { get; set; } = 1;
    public Guid DeviceId { get; set; }
    public Guid LatestPackageId { get; set; }
    public string? LatestPackageKey { get; set; }
    public string? PackageDigest { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public int PackageCount { get; set; }
}
