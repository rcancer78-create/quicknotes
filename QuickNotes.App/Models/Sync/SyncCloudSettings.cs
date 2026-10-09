using System;

namespace QuickNotes.App.Models.Sync;

/// <summary>
/// Configuration model for Yandex Object Storage / S3-compatible cloud synchronization.
/// Contains ONLY public non-sensitive configuration properties (endpoint, region, bucket, prefix).
/// NEVER contains access keys, secret keys, or encryption passwords.
/// </summary>
public class SyncCloudSettings
{
    public const string DefaultYandexEndpoint = "https://s3.yandexcloud.net";
    public const string DefaultYandexRegion = "ru-central1";

    /// <summary>
    /// Whether cloud synchronization is enabled by the user.
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// S3-compatible REST API endpoint. Defaults to Yandex Object Storage.
    /// Can be overridden for testing or alternate private S3 installations.
    /// </summary>
    public string Endpoint { get; set; } = DefaultYandexEndpoint;

    /// <summary>
    /// S3 region identifier. Defaults to Yandex Cloud region 'ru-central1'.
    /// </summary>
    public string Region { get; set; } = DefaultYandexRegion;

    /// <summary>
    /// Target private S3 bucket name.
    /// </summary>
    public string Bucket { get; set; } = string.Empty;

    /// <summary>
    /// Optional object key prefix to isolate application objects in the bucket (e.g. 'quicknotes' or 'user1').
    /// </summary>
    public string? Prefix { get; set; } = null;

    /// <summary>
    /// HTTP request timeout in seconds.
    /// </summary>
    public int RequestTimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Maximum retry attempts for transient errors on idempotent operations.
    /// </summary>
    public int MaxRetryAttempts { get; set; } = 3;

    /// <summary>
    /// Minimum allowed periodic background sync interval in minutes.
    /// </summary>
    public const int MinPeriodicIntervalMinutes = 15;
    public const int DefaultPeriodicIntervalMinutes = 30;
    public const int DefaultDebounceSeconds = 3;

    /// <summary>
    /// Automatically enqueue sync after local note, tag, or template changes (with debounce).
    /// </summary>
    public bool AutoSyncOnChanges { get; set; } = true;

    /// <summary>
    /// Debounce delay in seconds after local changes before executing sync.
    /// </summary>
    public int DebounceSeconds { get; set; } = DefaultDebounceSeconds;

    /// <summary>
    /// Automatically run sync once upon application startup (if sync is enabled and configured).
    /// </summary>
    public bool AutoSyncOnStartup { get; set; } = true;

    /// <summary>
    /// Automatically run periodic sync in the background.
    /// </summary>
    public bool AutoSyncPeriodic { get; set; } = false;

    private int _periodicIntervalMinutes = DefaultPeriodicIntervalMinutes;

    /// <summary>
    /// Periodic background sync interval in minutes. Minimum is 15 minutes; values below 15 are forbidden.
    /// </summary>
    public int PeriodicIntervalMinutes
    {
        get => Math.Max(MinPeriodicIntervalMinutes, _periodicIntervalMinutes);
        set
        {
            if (value < MinPeriodicIntervalMinutes)
            {
                throw new ArgumentOutOfRangeException(nameof(value), $"Интервал периодической синхронизации не может быть меньше {MinPeriodicIntervalMinutes} минут.");
            }
            _periodicIntervalMinutes = value;
        }
    }

    /// <summary>
    /// Whether binary attachment blobs should be synced to cloud in addition to metadata.
    /// When false, metadata continues to sync but binary content is not transferred.
    /// </summary>
    public bool SyncAttachments { get; set; } = true;

    /// <summary>
    /// Maximum size in bytes of a single attachment blob allowed for cloud sync.
    /// Attachments exceeding this limit are skipped (metadata still syncs).
    /// Default: 100 MiB. Must be > 0 if set; 0 means use default.
    /// </summary>
    public const long DefaultMaxAttachmentSyncBytes = 100L * 1024 * 1024; // 100 MiB
    public const long MaxAllowedAttachmentSyncBytes = 1024L * 1024 * 1024; // 1 GiB per file (quota/cleanup are out of 21F)

    private long _maxAttachmentSyncBytes = DefaultMaxAttachmentSyncBytes;

    public long MaxAttachmentSyncBytes
    {
        get => _maxAttachmentSyncBytes > 0 ? _maxAttachmentSyncBytes : DefaultMaxAttachmentSyncBytes;
        set
        {
            if (value < 0)
                throw new ArgumentOutOfRangeException(nameof(value), "Лимит размера вложения не может быть отрицательным.");
            if (value > MaxAllowedAttachmentSyncBytes)
                throw new ArgumentOutOfRangeException(nameof(value), $"Лимит размера вложения не может превышать {MaxAllowedAttachmentSyncBytes} байт.");
            _maxAttachmentSyncBytes = value > 0 ? value : DefaultMaxAttachmentSyncBytes;
        }
    }

    public SyncCloudSettings Clone()
    {
        return new SyncCloudSettings
        {
            Enabled = Enabled,
            Endpoint = Endpoint,
            Region = Region,
            Bucket = Bucket,
            Prefix = Prefix,
            RequestTimeoutSeconds = RequestTimeoutSeconds,
            MaxRetryAttempts = MaxRetryAttempts,
            AutoSyncOnChanges = AutoSyncOnChanges,
            DebounceSeconds = DebounceSeconds,
            AutoSyncOnStartup = AutoSyncOnStartup,
            AutoSyncPeriodic = AutoSyncPeriodic,
            PeriodicIntervalMinutes = PeriodicIntervalMinutes,
            SyncAttachments = SyncAttachments,
            MaxAttachmentSyncBytes = MaxAttachmentSyncBytes
        };
    }
}
