using System;
using System.Net;
using System.Text.RegularExpressions;
using QuickNotes.App.Models.Sync;

namespace QuickNotes.App.Services.Sync;

public static class SyncCloudSettingsValidator
{
    private static readonly Regex BucketRegex = new("^[a-z0-9][a-z0-9.-]{1,61}[a-z0-9]$", RegexOptions.Compiled);
    private static readonly Regex InvalidPrefixCharsRegex = new(@"[\\:*?""<>|\x00-\x1F]", RegexOptions.Compiled);

    public static void Validate(SyncCloudSettings settings, bool requireBucket = true)
    {
        if (settings == null)
            throw new ArgumentNullException(nameof(settings));

        ValidateEndpoint(settings.Endpoint);

        if (string.IsNullOrWhiteSpace(settings.Region))
            throw new SyncValidationException("Регион S3/Yandex Object Storage не может быть пустым.");

        if (requireBucket || settings.Enabled)
        {
            ValidateBucket(settings.Bucket);
        }

        if (!string.IsNullOrEmpty(settings.Prefix))
        {
            ValidatePrefix(settings.Prefix);
        }

        if (settings.RequestTimeoutSeconds <= 0 || settings.RequestTimeoutSeconds > 300)
            throw new SyncValidationException("Таймаут запроса должен быть в пределах от 1 до 300 секунд.");

        if (settings.MaxRetryAttempts < 0 || settings.MaxRetryAttempts > 10)
            throw new SyncValidationException("Количество повторных попыток должно быть от 0 до 10.");

        if (settings.AutoSyncPeriodic && settings.PeriodicIntervalMinutes < SyncCloudSettings.MinPeriodicIntervalMinutes)
            throw new SyncValidationException($"Интервал фонового обмена должен составлять не менее {SyncCloudSettings.MinPeriodicIntervalMinutes} минут.");

        if (settings.DebounceSeconds < 1 || settings.DebounceSeconds > 300)
            throw new SyncValidationException("Задержка синхронизации (debounce) должна быть от 1 до 300 секунд.");

        if (settings.MaxAttachmentSyncBytes <= 0 || settings.MaxAttachmentSyncBytes > SyncCloudSettings.MaxAllowedAttachmentSyncBytes)
            throw new SyncValidationException($"Лимит размера облачного вложения должен быть от 1 байта до {SyncCloudSettings.MaxAllowedAttachmentSyncBytes} байт.");
    }

    public static void ValidateEndpoint(string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
            throw new SyncValidationException("Эндпоинт S3 не может быть пустым.");

        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new SyncValidationException($"Недопустимый эндпоинт: '{endpoint}'. Должен быть абсолютный HTTP или HTTPS URL.");
        }
    }

    public static void ValidateBucket(string? bucket)
    {
        if (string.IsNullOrWhiteSpace(bucket))
            throw new SyncValidationException("Имя бакета не может быть пустым.");

        bucket = bucket.Trim();

        if (bucket.Length < 3 || bucket.Length > 63)
            throw new SyncValidationException($"Длина имени бакета '{bucket}' должна составлять от 3 до 63 символов.");

        if (!BucketRegex.IsMatch(bucket))
            throw new SyncValidationException($"Имя бакета '{bucket}' содержит недопустимые символы. Разрешены строчные буквы (a-z), цифры и дефисы; имя должно начинаться и оканчиваться на букву или цифру.");

        if (bucket.Contains(".."))
            throw new SyncValidationException($"Имя бакета '{bucket}' не должно содержать две точки подряд.");

        if (IPAddress.TryParse(bucket, out _))
            throw new SyncValidationException($"Имя бакета '{bucket}' не должно быть в формате IP-адреса.");
    }

    public static void ValidatePrefix(string prefix)
    {
        if (string.IsNullOrWhiteSpace(prefix))
            return;

        if (prefix.Contains(".."))
            throw new SyncValidationException("Префикс не должен содержать '..' (защита от path traversal).");

        if (InvalidPrefixCharsRegex.IsMatch(prefix))
            throw new SyncValidationException("Префикс содержит недопустимые символы (обратные слеши, двоеточия или спецсимволы).");
    }

    public static string NormalizePrefix(string? prefix)
    {
        if (string.IsNullOrWhiteSpace(prefix))
            return string.Empty;

        string normalized = prefix.Trim().Replace('\\', '/');
        ValidatePrefix(normalized);

        while (normalized.Contains("//"))
        {
            normalized = normalized.Replace("//", "/");
        }

        normalized = normalized.Trim('/');

        return string.IsNullOrEmpty(normalized) ? string.Empty : normalized + "/";
    }
}
