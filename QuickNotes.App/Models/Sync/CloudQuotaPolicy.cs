using System;

namespace QuickNotes.App.Models.Sync;

/// <summary>
/// Free-tier Yandex Object Storage quota for the QuickNotes prefix (1 GiB).
/// Thresholds are MiB (1024-based). Incomplete measurements are never treated as zero usage.
/// </summary>
public static class CloudQuotaPolicy
{
    public const long FreeQuotaBytes = 1024L * 1024 * 1024;
    public const long WarningThresholdBytes = 800L * 1024 * 1024;
    public const long BlockNewAttachmentThresholdBytes = 950L * 1024 * 1024;
    public const int NewBlobSizeSafetyMarginBytes = 4096;

    public const string NewBlobBlockedByQuotaMessage =
        "Выгрузка нового вложения остановлена: занятый объём достиг 950 МиБ или измерение квоты неполное/недоступно. Метаданные, скачивание и локальная работа продолжаются. Освободите место ручной очисткой или дождитесь полного расчёта объёма.";

    public static bool IsMeasurementReliable(CloudUsageResult? usage)
        => usage != null && usage.IsSuccess && !usage.IsTruncated && !usage.IsPartial;

    public static bool IsWarning(CloudUsageResult? usage)
        => usage != null && usage.IsSuccess && usage.TotalBytes >= WarningThresholdBytes;

    public static bool BlocksNewAttachments(CloudUsageResult? usage)
        => !IsMeasurementReliable(usage) || usage!.TotalBytes >= BlockNewAttachmentThresholdBytes;

    public static bool CanUploadNewBlob(CloudUsageResult? usage, long newEncryptedObjectBytes)
    {
        if (!IsMeasurementReliable(usage) || newEncryptedObjectBytes < 0)
            return false;

        if (usage!.TotalBytes >= BlockNewAttachmentThresholdBytes)
            return false;

        try
        {
            long projected = checked(usage.TotalBytes + newEncryptedObjectBytes + NewBlobSizeSafetyMarginBytes);
            return projected <= BlockNewAttachmentThresholdBytes;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    public static string FormatStatus(CloudUsageResult? usage)
    {
        if (usage == null)
            return "Квота: объём ещё не вычислен. Выгрузка новых вложений приостановлена до полного расчёта.";

        if (!usage.IsSuccess)
        {
            if (usage.IsOffline)
                return "Квота: измерение недоступно (офлайн). Новые облачные вложения заблокированы.";
            if (usage.IsAuthError)
                return "Квота: измерение недоступно (ошибка доступа). Новые облачные вложения заблокированы.";
            return "Квота: измерение не удалось. Новые облачные вложения заблокированы до успешного полного расчёта.";
        }

        string used = CloudUsageResult.FormatBytes(usage.TotalBytes);
        string when = usage.CalculatedAtUtc.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss");
        string completeness = usage.IsTruncated || usage.IsPartial
            ? "результат неполный (нижняя оценка, не ноль)"
            : "результат полный";

        if (!IsMeasurementReliable(usage))
        {
            return $"Квота: ≥ {used} из 1 ГиБ ({completeness}, {usage.TotalObjects} объектов, {when}). Новые вложения заблокированы до полного измерения. Порог предупреждения 800 МиБ, блокировка 950 МиБ.";
        }

        if (usage.TotalBytes >= BlockNewAttachmentThresholdBytes)
        {
            return $"Квота: {used} из 1 ГиБ ({completeness}, {usage.TotalObjects} объектов, {when}). Новые облачные вложения заблокированы (порог 950 МиБ).";
        }

        if (IsWarning(usage))
        {
            return $"Квота: {used} из 1 ГиБ ({completeness}, {usage.TotalObjects} объектов, {when}). Предупреждение: занято не менее 800 МиБ.";
        }

        return $"Квота: {used} из 1 ГиБ ({completeness}, {usage.TotalObjects} объектов, {when}). Новые вложения разрешены.";
    }
}
