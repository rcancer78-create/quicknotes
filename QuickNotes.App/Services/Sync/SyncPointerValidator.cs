using System;
using System.Text.Json;
using QuickNotes.App.Models.Sync;

namespace QuickNotes.App.Services.Sync;

/// <summary>
/// Strict validator for device pointer manifests stored at {prefix}devices/{deviceId}/pointer.json.
/// Enforces:
/// - Maximum byte size (prevents oversized payload attacks)
/// - Valid JSON structure and format version
/// - Non-empty valid GUIDs for DeviceId and LatestPackageId
/// - Exact match between DeviceId and the object key's device namespace segment
/// - Prohibition of references outside permitted prefix/device namespace (no path traversal, no cross-device pointers)
/// - Zero user plaintext secrets.
/// </summary>
public static class SyncPointerValidator
{
    public const int MaxPointerSizeBytes = 32 * 1024; // 32 KB limit

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static DevicePointerPayload ValidateAndParse(
        byte[] pointerBytes,
        string pointerObjectKey,
        SyncObjectKeyHelper keyHelper)
    {
        if (pointerBytes == null || pointerBytes.Length == 0)
        {
            throw new SyncValidationException("Содержимое указателя устройства пусто.");
        }

        if (pointerBytes.Length > MaxPointerSizeBytes)
        {
            throw new SyncValidationException($"Размер указателя устройства ({pointerBytes.Length} байт) превышает допустимый максимум {MaxPointerSizeBytes} байт.");
        }

        if (string.IsNullOrWhiteSpace(pointerObjectKey))
        {
            throw new SyncValidationException("Ключ объекта указателя не может быть пустым.");
        }

        SyncObjectKeyHelper.ValidateObjectKey(pointerObjectKey);

        if (!keyHelper.TryParsePointerKey(pointerObjectKey, out var keyDeviceId))
        {
            throw new SyncValidationException($"Ключ объекта указателя '{pointerObjectKey}' не соответствует пути 'devices/{{deviceId}}/pointer.json'.");
        }

        DevicePointerPayload? pointer;
        try
        {
            pointer = JsonSerializer.Deserialize<DevicePointerPayload>(pointerBytes, JsonOptions);
        }
        catch (Exception ex)
        {
            throw new SyncValidationException($"Ошибка десериализации JSON указателя устройства: {ex.Message}", ex);
        }

        if (pointer == null)
        {
            throw new SyncValidationException("Десериализованный указатель устройства равен null.");
        }

        if (pointer.FormatVersion != 1)
        {
            throw new SyncValidationException($"Неподдерживаемая версия формата указателя ({pointer.FormatVersion}).");
        }

        if (pointer.DeviceId == Guid.Empty)
        {
            throw new SyncValidationException("Указатель устройства содержит пустой DeviceId (Guid.Empty).");
        }

        if (pointer.DeviceId != keyDeviceId)
        {
            throw new SyncValidationException($"Несоответствие пространства имён: DeviceId в указателе ({pointer.DeviceId}) не совпадает с DeviceId в ключе объекта ({keyDeviceId}).");
        }

        if (pointer.LatestPackageId == Guid.Empty)
        {
            throw new SyncValidationException("Указатель устройства содержит пустой LatestPackageId (Guid.Empty).");
        }

        if (pointer.PackageCount < 0)
        {
            throw new SyncValidationException($"Указатель устройства содержит недопустимое отрицательное количество пакетов ({pointer.PackageCount}).");
        }

        if (!string.IsNullOrWhiteSpace(pointer.LatestPackageKey))
        {
            SyncObjectKeyHelper.ValidateObjectKey(pointer.LatestPackageKey);

            if (!keyHelper.TryParsePackageKey(pointer.LatestPackageKey, out var pkgDeviceId, out var pkgPackageId))
            {
                throw new SyncValidationException($"Ключ пакета '{pointer.LatestPackageKey}' в указателе ссылается за пределы разрешённого префикса/пространства пакетов.");
            }

            if (pkgDeviceId != pointer.DeviceId)
            {
                throw new SyncValidationException($"Ключ пакета '{pointer.LatestPackageKey}' ссылается на пространство другого устройства ({pkgDeviceId} != {pointer.DeviceId}).");
            }

            if (pkgPackageId != pointer.LatestPackageId)
            {
                throw new SyncValidationException($"Ключ пакета '{pointer.LatestPackageKey}' ссылается на PackageId {pkgPackageId}, отличный от LatestPackageId {pointer.LatestPackageId}.");
            }
        }

        return pointer;
    }
}
