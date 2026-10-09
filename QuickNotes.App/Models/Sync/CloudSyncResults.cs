using System;
using System.Collections.Generic;

namespace QuickNotes.App.Models.Sync;

public class CloudSyncUploadResult
{
    public bool Success { get; set; }
    public Guid? PackageId { get; set; }
    public string? ObjectKey { get; set; }
    public string? ETag { get; set; }
    public long BytesUploaded { get; set; }
    public bool IsOffline { get; set; }
    public bool IsConflict { get; set; }
    public bool IsAuthError { get; set; }
    public bool IsQuotaError { get; set; }
    public string? ErrorMessage { get; set; }
    public List<string> Diagnostics { get; set; } = new();

    public static CloudSyncUploadResult Succeeded(Guid packageId, string objectKey, string etag, long bytes)
    {
        return new CloudSyncUploadResult
        {
            Success = true,
            PackageId = packageId,
            ObjectKey = objectKey,
            ETag = etag,
            BytesUploaded = bytes
        };
    }

    public static CloudSyncUploadResult Offline(string message)
    {
        return new CloudSyncUploadResult
        {
            Success = false,
            IsOffline = true,
            ErrorMessage = message
        };
    }

    public static CloudSyncUploadResult AuthError(string message)
    {
        return new CloudSyncUploadResult
        {
            Success = false,
            IsAuthError = true,
            ErrorMessage = message
        };
    }

    public static CloudSyncUploadResult Conflict(string message, string? key = null)
    {
        return new CloudSyncUploadResult
        {
            Success = false,
            IsConflict = true,
            ObjectKey = key,
            ErrorMessage = message
        };
    }

    public static CloudSyncUploadResult QuotaError(string message)
    {
        return new CloudSyncUploadResult
        {
            Success = false,
            IsQuotaError = true,
            ErrorMessage = message
        };
    }

    public static CloudSyncUploadResult Failure(string message)
    {
        return new CloudSyncUploadResult
        {
            Success = false,
            ErrorMessage = message
        };
    }
}

public class CloudSyncDownloadResult
{
    public bool Success { get; set; }
    public string? PackageKey { get; set; }
    public SyncImportResult? ImportResult { get; set; }
    public bool IsOffline { get; set; }
    public bool IsAuthError { get; set; }
    public string? ErrorMessage { get; set; }

    public static CloudSyncDownloadResult Succeeded(string packageKey, SyncImportResult importResult)
    {
        return new CloudSyncDownloadResult
        {
            Success = importResult.Success,
            PackageKey = packageKey,
            ImportResult = importResult,
            ErrorMessage = importResult.Success ? null : string.Join("; ", importResult.Errors)
        };
    }

    public static CloudSyncDownloadResult Offline(string message)
    {
        return new CloudSyncDownloadResult
        {
            Success = false,
            IsOffline = true,
            ErrorMessage = message
        };
    }

    public static CloudSyncDownloadResult Failure(string message, bool isAuth = false)
    {
        return new CloudSyncDownloadResult
        {
            Success = false,
            IsAuthError = isAuth,
            ErrorMessage = message
        };
    }
}

public class CloudSyncListResult
{
    public bool Success { get; set; }
    public List<StorageObjectSummary> Summaries { get; set; } = new();
    public List<DevicePointerPayload> DevicePointers { get; set; } = new();
    public bool IsOffline { get; set; }
    public bool IsAuthError { get; set; }
    public string? ErrorMessage { get; set; }

    public static CloudSyncListResult Succeeded(List<StorageObjectSummary> summaries, List<DevicePointerPayload>? pointers = null)
    {
        return new CloudSyncListResult
        {
            Success = true,
            Summaries = summaries,
            DevicePointers = pointers ?? new List<DevicePointerPayload>()
        };
    }

    public static CloudSyncListResult Offline(string message)
    {
        return new CloudSyncListResult
        {
            Success = false,
            IsOffline = true,
            ErrorMessage = message
        };
    }

    public static CloudSyncListResult Failure(string message, bool isAuth = false)
    {
        return new CloudSyncListResult
        {
            Success = false,
            IsAuthError = isAuth,
            ErrorMessage = message
        };
    }
}

public class CloudSyncConnectionTestResult
{
    public bool Success { get; set; }
    public string Endpoint { get; set; } = string.Empty;
    public string Bucket { get; set; } = string.Empty;
    public bool IsOffline { get; set; }
    public bool IsAuthError { get; set; }
    public string? ErrorMessage { get; set; }
    public List<string> Diagnostics { get; set; } = new();

    public static CloudSyncConnectionTestResult Succeeded(string endpoint, string bucket)
    {
        return new CloudSyncConnectionTestResult
        {
            Success = true,
            Endpoint = endpoint,
            Bucket = bucket
        };
    }

    public static CloudSyncConnectionTestResult Offline(string endpoint, string bucket, string message)
    {
        return new CloudSyncConnectionTestResult
        {
            Success = false,
            Endpoint = endpoint,
            Bucket = bucket,
            IsOffline = true,
            ErrorMessage = message
        };
    }

    public static CloudSyncConnectionTestResult AuthFailure(string endpoint, string bucket, string message)
    {
        return new CloudSyncConnectionTestResult
        {
            Success = false,
            Endpoint = endpoint,
            Bucket = bucket,
            IsAuthError = true,
            ErrorMessage = message
        };
    }

    public static CloudSyncConnectionTestResult Failure(string endpoint, string bucket, string message)
    {
        return new CloudSyncConnectionTestResult
        {
            Success = false,
            Endpoint = endpoint,
            Bucket = bucket,
            ErrorMessage = message
        };
    }
}
