using System;

namespace QuickNotes.App.Models.Sync;

/// <summary>
/// Result of safe Object Storage prefix usage calculation.
/// Represents ONLY the storage consumed within the QuickNotes prefix, NOT the entire bucket.
/// </summary>
public class CloudUsageResult
{
    public bool IsSuccess { get; set; }
    public long TotalBytes { get; set; }
    public int TotalObjects { get; set; }
    public int TotalPages { get; set; }
    public bool IsTruncated { get; set; }
    public bool IsPartial { get; set; }
    public string Prefix { get; set; } = string.Empty;
    public string Bucket { get; set; } = string.Empty;
    public DateTime CalculatedAtUtc { get; set; } = DateTime.UtcNow;
    public bool IsOffline { get; set; }
    public bool IsAuthError { get; set; }
    public string? ErrorMessage { get; set; }

    public static CloudUsageResult Success(
        long totalBytes,
        int totalObjects,
        int totalPages,
        bool isTruncated,
        string prefix,
        string bucket)
    {
        return new CloudUsageResult
        {
            IsSuccess = true,
            TotalBytes = totalBytes,
            TotalObjects = totalObjects,
            TotalPages = totalPages,
            IsTruncated = isTruncated,
            IsPartial = isTruncated,
            Prefix = prefix,
            Bucket = bucket,
            CalculatedAtUtc = DateTime.UtcNow
        };
    }

    public static CloudUsageResult Offline(string message, string prefix = "", string bucket = "")
    {
        return new CloudUsageResult
        {
            IsSuccess = false,
            IsOffline = true,
            ErrorMessage = message,
            Prefix = prefix,
            Bucket = bucket,
            CalculatedAtUtc = DateTime.UtcNow
        };
    }

    public static CloudUsageResult AuthError(string message, string prefix = "", string bucket = "")
    {
        return new CloudUsageResult
        {
            IsSuccess = false,
            IsAuthError = true,
            ErrorMessage = message,
            Prefix = prefix,
            Bucket = bucket,
            CalculatedAtUtc = DateTime.UtcNow
        };
    }

    public static CloudUsageResult Failure(string message, string prefix = "", string bucket = "")
    {
        return new CloudUsageResult
        {
            IsSuccess = false,
            ErrorMessage = message,
            Prefix = prefix,
            Bucket = bucket,
            CalculatedAtUtc = DateTime.UtcNow
        };
    }

    public bool IsMeasurementReliable => CloudQuotaPolicy.IsMeasurementReliable(this);

    public bool IsQuotaWarning => CloudQuotaPolicy.IsWarning(this);

    public bool BlocksNewAttachmentUploads => CloudQuotaPolicy.BlocksNewAttachments(this);

    public string FormatQuotaStatusText() => CloudQuotaPolicy.FormatStatus(this);

    public string FormatUsageText()
    {
        if (!IsSuccess)
        {
            if (IsOffline) return "Офлайн (нет связи с хранилищем)";
            if (IsAuthError) return "Ошибка доступа к S3";
            return $"Ошибка: {ErrorMessage ?? "не удалось получить объём"}";
        }

        string sizeStr = FormatBytes(TotalBytes);
        if (IsTruncated || IsPartial)
        {
            return $"≥ {sizeStr} (данные неполные, лимит {TotalObjects} объектов)";
        }

        return $"{sizeStr} ({TotalObjects} объектов)";
    }

    public static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return "0 Б";
        if (bytes < 1024) return $"{bytes} Б";
        if (bytes < 1024 * 1024) return $"{(double)bytes / 1024:F1} КБ";
        if (bytes < 1024 * 1024 * 1024) return $"{(double)bytes / (1024 * 1024):F1} МБ";
        return $"{(double)bytes / (1024 * 1024 * 1024):F2} ГБ";
    }
}
