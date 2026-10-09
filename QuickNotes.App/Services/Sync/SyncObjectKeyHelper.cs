using System;
using System.Text.RegularExpressions;
using QuickNotes.App.Models.Sync;

namespace QuickNotes.App.Services.Sync;

/// <summary>
/// Deterministic and safe object key resolver for cloud packages and device pointers.
/// Enforces device-level namespace isolation, strict path traversal prevention,
/// and safe S3 key conventions.
/// </summary>
public class SyncObjectKeyHelper
{
    public const string DefaultApiVersion = "v1";
    public const string GenerationSegmentName = "g";

    private readonly string _basePrefix;
    private readonly string _apiVersion;
    private readonly string _rootVersionedPrefix;
    private Guid _generationId;
    private string _versionedPrefix = string.Empty;

    public string VersionedPrefix => _versionedPrefix;
    public string RootVersionedPrefix => _rootVersionedPrefix;
    public Guid GenerationId => _generationId;
    public bool IsLegacyGeneration => _generationId == Guid.Empty;

    public SyncObjectKeyHelper(SyncCloudSettings? settings = null, string apiVersion = DefaultApiVersion, Guid generationId = default)
        : this(
            SyncCloudSettingsValidator.NormalizePrefix(settings?.Prefix),
            string.IsNullOrWhiteSpace(apiVersion) ? DefaultApiVersion : apiVersion.Trim('/'),
            generationId)
    {
    }

    private SyncObjectKeyHelper(string basePrefix, string apiVersion, Guid generationId)
    {
        _basePrefix = basePrefix ?? string.Empty;
        _apiVersion = string.IsNullOrWhiteSpace(apiVersion) ? DefaultApiVersion : apiVersion.Trim('/');
        _rootVersionedPrefix = $"{_basePrefix}{_apiVersion}/";
        ApplyGeneration(generationId);
    }

    /// <summary>
    /// Empty generation keeps the legacy layout ({prefix}v1/devices|blobs).
    /// Non-empty generation isolates ciphertexts under {prefix}v1/g/{id}/.
    /// </summary>
    public void ApplyGeneration(Guid generationId)
    {
        _generationId = generationId;
        _versionedPrefix = generationId == Guid.Empty
            ? _rootVersionedPrefix
            : $"{_rootVersionedPrefix}{GenerationSegmentName}/{generationId:D}/";
    }

    public SyncObjectKeyHelper ForGeneration(Guid generationId)
        => new SyncObjectKeyHelper(_basePrefix, _apiVersion, generationId);

    /// <summary>
    /// Small generation pointer, always at the version root (never inside /g/{id}/).
    /// </summary>
    public string GetGenerationPointerKey()
    {
        string key = $"{_rootVersionedPrefix}generation.json";
        ValidateObjectKey(key);
        return key;
    }

    public string GetPackageKey(Guid deviceId, Guid packageId)
    {
        if (deviceId == Guid.Empty)
            throw new ArgumentException("DeviceId не может быть пустым GUID.", nameof(deviceId));
        if (packageId == Guid.Empty)
            throw new ArgumentException("PackageId не может быть пустым GUID.", nameof(packageId));

        string key = $"{_versionedPrefix}devices/{deviceId:D}/packages/{packageId:D}.json";
        ValidateObjectKey(key);
        return key;
    }

    public string GetDevicePointerKey(Guid deviceId)
    {
        if (deviceId == Guid.Empty)
            throw new ArgumentException("DeviceId не может быть пустым GUID.", nameof(deviceId));

        string key = $"{_versionedPrefix}devices/{deviceId:D}/pointer.json";
        ValidateObjectKey(key);
        return key;
    }

    public string GetDevicesPrefix()
    {
        return $"{_versionedPrefix}devices/";
    }

    public string GetDevicePackagesPrefix(Guid deviceId)
    {
        if (deviceId == Guid.Empty)
            throw new ArgumentException("DeviceId не может быть пустым GUID.", nameof(deviceId));

        return $"{_versionedPrefix}devices/{deviceId:D}/packages/";
    }

    /// <summary>
    /// Returns the cloud object key for a content-addressed attachment blob.
    /// Key = {versionedPrefix}blobs/{sha256hex}.bin
    /// The SHA-256 hex must be 64 lowercase hex characters.
    /// No original filename, no path, no device ID in the key.
    /// </summary>
    public string GetBlobKey(string sha256Hex)
    {
        if (string.IsNullOrWhiteSpace(sha256Hex) || sha256Hex.Length != 64)
            throw new ArgumentException("Хеш SHA-256 должен быть строкой из 64 шестнадцатеричных символов.", nameof(sha256Hex));

        // Validate hex characters only
        foreach (char c in sha256Hex)
        {
            if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')))
                throw new ArgumentException("Хеш SHA-256 содержит недопустимые символы.", nameof(sha256Hex));
        }

        string key = $"{_versionedPrefix}blobs/{sha256Hex.ToLowerInvariant()}.bin";
        ValidateObjectKey(key);
        return key;
    }

    /// <summary>Returns the prefix for all blob objects.</summary>
    public string GetBlobsPrefix() => $"{_versionedPrefix}blobs/";

    public bool TryParseBlobKey(string key, out string sha256Hex)
    {
        sha256Hex = string.Empty;
        if (string.IsNullOrWhiteSpace(key) || !key.StartsWith(_versionedPrefix, StringComparison.Ordinal))
            return false;

        string relative = key.Substring(_versionedPrefix.Length);
        var match = Regex.Match(
            relative,
            @"^blobs/([0-9a-fA-F]{64})\.bin$",
            RegexOptions.IgnoreCase);

        if (!match.Success)
            return false;

        sha256Hex = match.Groups[1].Value.ToLowerInvariant();
        return true;
    }

    public static void ValidateObjectKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new SyncValidationException("Ключ объекта S3 не может быть пустым.");

        if (key.StartsWith('/') || key.StartsWith('\\'))
            throw new SyncValidationException($"Ключ объекта '{key}' не должен начинаться со слеша.");

        if (key.Contains(".."))
            throw new SyncValidationException($"Ключ объекта '{key}' содержит '..' (попытка path traversal).");

        if (key.Contains('\\'))
            throw new SyncValidationException($"Ключ объекта '{key}' содержит недопустимый символ '\\'.");

        if (System.Text.Encoding.UTF8.GetByteCount(key) > 1024)
            throw new SyncValidationException($"Длина ключа объекта '{key}' превышает допустимый размер S3 (1024 байта).");
    }

    public bool TryParsePackageKey(string key, out Guid deviceId, out Guid packageId)
    {
        deviceId = Guid.Empty;
        packageId = Guid.Empty;

        if (string.IsNullOrWhiteSpace(key) || !key.StartsWith(_versionedPrefix, StringComparison.Ordinal))
            return false;

        string relative = key.Substring(_versionedPrefix.Length);
        // Expecting: devices/{deviceId}/packages/{packageId}.json
        var match = Regex.Match(
            relative,
            @"^devices/([0-9a-fA-F-]{36})/packages/([0-9a-fA-F-]{36})\.json$",
            RegexOptions.IgnoreCase);

        if (match.Success &&
            Guid.TryParse(match.Groups[1].Value, out deviceId) &&
            Guid.TryParse(match.Groups[2].Value, out packageId))
        {
            return true;
        }

        return false;
    }

    public bool TryParsePointerKey(string key, out Guid deviceId)
    {
        deviceId = Guid.Empty;

        if (string.IsNullOrWhiteSpace(key) || !key.StartsWith(_versionedPrefix, StringComparison.Ordinal))
            return false;

        string relative = key.Substring(_versionedPrefix.Length);
        // Expecting: devices/{deviceId}/pointer.json
        var match = Regex.Match(
            relative,
            @"^devices/([0-9a-fA-F-]{36})/pointer\.json$",
            RegexOptions.IgnoreCase);

        if (match.Success && Guid.TryParse(match.Groups[1].Value, out deviceId))
        {
            return true;
        }

        return false;
    }

    public bool TryParseDevicePrefix(string prefix, out Guid deviceId)
    {
        deviceId = Guid.Empty;

        if (string.IsNullOrWhiteSpace(prefix) || !prefix.StartsWith(_versionedPrefix, StringComparison.Ordinal))
            return false;

        string relative = prefix.Substring(_versionedPrefix.Length);
        // Expecting: devices/{deviceId}/ (optional trailing slash)
        var match = Regex.Match(
            relative,
            @"^devices/([0-9a-fA-F-]{36})/?$",
            RegexOptions.IgnoreCase);

        if (match.Success && Guid.TryParse(match.Groups[1].Value, out deviceId) && deviceId != Guid.Empty)
        {
            return true;
        }

        return false;
    }
}
