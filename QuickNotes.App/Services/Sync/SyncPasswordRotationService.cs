using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using QuickNotes.App.Models.Sync;
using QuickNotes.App.Services;
using QuickNotes.App.Services.Crypto;

namespace QuickNotes.App.Services.Sync;

/// <summary>
/// Explicit master-password rotation: re-encrypt current-generation packages and blobs
/// into a new namespace, then ETag-switch generation.json. Old generation is never deleted here.
/// New password is stored in a DPAPI pending slot before the generation switch, then promoted after success.
/// </summary>
public class SyncPasswordRotationService : ISyncPasswordRotationService
{
    public const int DefaultMaxPages = CloudUsageService.DefaultMaxPages;
    public const int DefaultMaxObjects = CloudUsageService.DefaultMaxObjects;
    public const int DefaultPageSize = CloudUsageService.DefaultPageSize;
    public const long MaxReadablePackageBytes = 25L * 1024 * 1024;

    private static readonly byte[] BlobMagic = { 0x51, 0x4E, 0x42, 0x41 };
    private const byte BlobEnvelopeVersionV1 = 0x01;
    private const byte BlobEnvelopeVersion = 0x02;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly ICloudObjectStoreTransport _transport;
    private readonly ISyncCryptoService _crypto;
    private readonly SyncObjectKeyHelper _keyHelper;
    private readonly ISyncPasswordStorage _passwordStorage;
    private readonly SyncCloudSettings _settings;
    private readonly ICloudUsageService? _usageService;
    private readonly SyncCloudExclusiveLock? _exclusiveLock;
    private readonly int _maxPages;
    private readonly int _maxObjects;
    private readonly int _pageSize;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private PasswordRotationPreview? _lastPreview;
    private Inventory? _lastInventory;

    public SyncPasswordRotationService(
        ICloudObjectStoreTransport transport,
        ISyncCryptoService crypto,
        SyncObjectKeyHelper keyHelper,
        ISyncPasswordStorage passwordStorage,
        SyncCloudSettings settings,
        ICloudUsageService? usageService = null,
        SyncCloudExclusiveLock? exclusiveLock = null,
        int maxPages = DefaultMaxPages,
        int maxObjects = DefaultMaxObjects,
        int pageSize = DefaultPageSize)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _crypto = crypto ?? throw new ArgumentNullException(nameof(crypto));
        _keyHelper = keyHelper ?? throw new ArgumentNullException(nameof(keyHelper));
        _passwordStorage = passwordStorage ?? throw new ArgumentNullException(nameof(passwordStorage));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _usageService = usageService;
        _exclusiveLock = exclusiveLock;
        _maxPages = Math.Max(1, maxPages);
        _maxObjects = Math.Max(1, maxObjects);
        _pageSize = Math.Clamp(pageSize, 1, 1000);
    }

    public async Task<PasswordRotationPreview> PreviewAsync(string oldPassword, CancellationToken ct = default)
    {
        bool exclusive = false;
        if (_exclusiveLock != null)
        {
            await _exclusiveLock.AcquireAsync(ct).ConfigureAwait(false);
            exclusive = true;
        }

        try
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                return await TryBuildPreviewAsync(oldPassword, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _lastPreview = null;
                _lastInventory = null;
                return Incomplete("Ротация отменена. Облако не изменялось.");
            }
            finally
            {
                _gate.Release();
            }
        }
        finally
        {
            if (exclusive)
                _exclusiveLock!.Release();
        }
    }

    public async Task<PasswordRotationExecuteResult> ExecuteAsync(
        string previewToken,
        string oldPassword,
        string newPassword,
        IProgress<PasswordRotationProgress>? progress = null,
        CancellationToken ct = default)
    {
        bool exclusive = false;
        try
        {
            if (_exclusiveLock != null)
            {
                await _exclusiveLock.AcquireAsync(ct).ConfigureAwait(false);
                exclusive = true;
            }

            await _gate.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (exclusive)
                _exclusiveLock!.Release();
            return CanceledResult("Ротация отменена до начала. Облако и локальный пароль не изменялись.");
        }

        try
        {
            if (string.IsNullOrEmpty(newPassword) || string.Equals(oldPassword, newPassword, StringComparison.Ordinal))
            {
                return new PasswordRotationExecuteResult
                {
                    TokenAccepted = false,
                    Summary = "Новый пароль пуст или совпадает со старым. Переключение не выполнялось."
                };
            }

            if (_lastPreview == null ||
                !_lastPreview.IsComplete ||
                _lastInventory == null ||
                !string.Equals(_lastPreview.Token, previewToken, StringComparison.Ordinal))
            {
                return new PasswordRotationExecuteResult
                {
                    TokenAccepted = false,
                    Summary = "Нет полного предварительного просмотра или токен не совпадает. Переключение не выполнялось."
                };
            }

            var resolved = await SyncGenerationResolver.ResolveAndApplyAsync(_transport, _keyHelper, ct).ConfigureAwait(false);
            if (!resolved.Success)
                return FailExecute("Указатель поколения недоступен: " + resolved.Error);

            progress?.Report(new PasswordRotationProgress { Message = "Проверка текущего поколения…" });

            string effectivePassword = await SyncPendingPasswordPromoter.TryPromoteAsync(
                _passwordStorage, _transport, _keyHelper, _crypto, oldPassword, ct).ConfigureAwait(false);

            var live = await InventoryCurrentAsync(ct).ConfigureAwait(false);
            if (live == null)
                return FailExecute("Неполное перечисление текущего поколения — ротация закрыта.");

            string? verifyError = await VerifyInventoryIntegrityAsync(live, null, ct).ConfigureAwait(false);
            if (verifyError != null)
                return FailExecute(verifyError);

            bool liveAcceptsNew = await TryVerifyInventoryPasswordAsync(live, newPassword, ct).ConfigureAwait(false);
            bool liveAcceptsOld = await TryVerifyInventoryPasswordAsync(live, oldPassword, ct).ConfigureAwait(false);
            if (!liveAcceptsOld &&
                !liveAcceptsNew &&
                !string.Equals(effectivePassword, oldPassword, StringComparison.Ordinal))
            {
                liveAcceptsOld = await TryVerifyInventoryPasswordAsync(live, effectivePassword, ct).ConfigureAwait(false);
            }

            if (liveAcceptsNew && !liveAcceptsOld)
            {
                bool saved = await TryPromoteOrSaveNewAsync(newPassword, ct).ConfigureAwait(false);
                return new PasswordRotationExecuteResult
                {
                    TokenAccepted = true,
                    Success = saved,
                    CloudSwitched = true,
                    LocalPasswordSaved = saved,
                    AlreadyOnNewGeneration = true,
                    ActiveGenerationId = resolved.Pointer.ActiveGenerationId,
                    PreviousGenerationId = resolved.Pointer.PreviousGenerationId,
                    Summary = saved
                        ? "Облако уже на новом поколении. Локальный пароль сохранён. Старое поколение не удалялось."
                        : "Облако уже на новом поколении. Локальный пароль не сохранён — повторите ротацию с новым паролем."
                };
            }

            if (!liveAcceptsOld)
                return FailExecute("Старый пароль не расшифровывает текущее поколение. Переключение не выполнялось.");

            if (!InventoriesMatch(_lastInventory, live))
                return FailExecute("Набор объектов изменился после предварительного просмотра. Переключение не выполнялось.");

            Guid oldActive = resolved.Pointer.ActiveGenerationId;
            Guid pendingId = resolved.Pointer.PendingGenerationId ?? Guid.Empty;
            string? etag = resolved.ETag;

            if (pendingId == Guid.Empty)
            {
                pendingId = Guid.NewGuid();
                var pendingPointer = ClonePointer(resolved.Pointer);
                pendingPointer.PendingGenerationId = pendingId;
                byte[] pendingBytes = JsonSerializer.SerializeToUtf8Bytes(pendingPointer);
                progress?.Report(new PasswordRotationProgress { Message = "Фиксация pending-поколения…" });
                var meta = await _transport.PutConditionalPointerAsync(
                    _keyHelper.GetGenerationPointerKey(),
                    pendingBytes,
                    string.IsNullOrEmpty(etag) ? null : etag,
                    "application/json",
                    ct).ConfigureAwait(false);
                etag = StorageObjectMetadata.NormalizeETag(meta.ETag);
            }

            var destHelper = _keyHelper.ForGeneration(pendingId);
            int total = live.Packages.Count + live.Pointers.Count + live.Blobs.Count;
            int done = 0;

            foreach (var pkg in live.Packages)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report(new PasswordRotationProgress { Message = "Перешифрование пакетов…", Current = ++done, Total = total });
                await CopyPackageAsync(pkg, destHelper, oldPassword, newPassword, ct).ConfigureAwait(false);
            }

            foreach (var ptr in live.Pointers)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report(new PasswordRotationProgress { Message = "Копирование указателей устройств…", Current = ++done, Total = total });
                await CopyPointerAsync(ptr, destHelper, ct).ConfigureAwait(false);
            }

            foreach (var blob in live.Blobs)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report(new PasswordRotationProgress { Message = "Перешифрование вложений…", Current = ++done, Total = total });
                await CopyBlobAsync(blob, destHelper, oldPassword, newPassword, ct).ConfigureAwait(false);
            }

            var beforeSwitch = await InventoryCurrentAsync(ct).ConfigureAwait(false);
            if (beforeSwitch == null || !InventoriesMatch(_lastInventory, beforeSwitch))
                return FailExecute("Исходное поколение изменилось перед переключением. Указатель не переключался.");

            await _passwordStorage.SavePendingPasswordAsync(newPassword, ct).ConfigureAwait(false);

            var switched = new SyncGenerationPointer
            {
                FormatVersion = SyncGenerationResolver.FormatVersion,
                ActiveGenerationId = pendingId,
                PendingGenerationId = null,
                PreviousGenerationId = oldActive == Guid.Empty ? null : oldActive,
                SwitchedAtUtc = DateTime.UtcNow
            };
            byte[] switchBytes = JsonSerializer.SerializeToUtf8Bytes(switched);
            await _transport.PutConditionalPointerAsync(
                _keyHelper.GetGenerationPointerKey(),
                switchBytes,
                string.IsNullOrEmpty(etag) ? null : etag,
                "application/json",
                ct).ConfigureAwait(false);

            _keyHelper.ApplyGeneration(pendingId);

            bool localSaved = await TryPromoteOrSaveNewAsync(newPassword, ct).ConfigureAwait(false);

            return new PasswordRotationExecuteResult
            {
                TokenAccepted = true,
                Success = localSaved,
                CloudSwitched = true,
                LocalPasswordSaved = localSaved,
                ActiveGenerationId = pendingId,
                PreviousGenerationId = switched.PreviousGenerationId,
                Summary = localSaved
                    ? "Пароль сменён. Облако переключено на новое поколение. Старое поколение не удалялось."
                    : "Облако переключено на новое поколение. Отложенный пароль сохранён для автоматического завершения."
            };
        }
        catch (OperationCanceledException)
        {
            return CanceledResult("Ротация отменена. Указатель активного поколения не переключался. Активный локальный пароль не изменялся.");
        }
        catch (CloudConflictException ex)
        {
            return FailExecute("Конфликт ETag указателя поколения: " + ErrorLogService.Sanitize(ex.Message));
        }
        catch (Exception ex)
        {
            ErrorLogService.Write("PasswordRotation.Execute", ex.GetType().Name);
            return FailExecute("Сбой ротации: " + ErrorLogService.Sanitize(ex.Message));
        }
        finally
        {
            _gate.Release();
            if (exclusive)
                _exclusiveLock!.Release();
        }
    }

    private async Task<PasswordRotationPreview> TryBuildPreviewAsync(string oldPassword, CancellationToken ct)
    {
        _lastPreview = null;
        _lastInventory = null;

        if (string.IsNullOrEmpty(oldPassword))
            return Incomplete("Старый пароль не задан.");

        string? stored = await _passwordStorage.LoadPasswordAsync(ct).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(stored) && !string.Equals(stored, oldPassword, StringComparison.Ordinal))
            return Incomplete("Старый пароль не совпадает с сохранённым. Облако не изменялось.");

        var resolved = await SyncGenerationResolver.ResolveAndApplyAsync(_transport, _keyHelper, ct).ConfigureAwait(false);
        if (!resolved.Success)
            return Incomplete(resolved.Error ?? "Указатель поколения недоступен.");

        var inventory = await InventoryCurrentAsync(ct).ConfigureAwait(false);
        if (inventory == null)
            return Incomplete("Неполное перечисление объектов текущего поколения — ротация закрыта.");

        string? integrity = await VerifyInventoryIntegrityAsync(inventory, oldPassword, ct).ConfigureAwait(false);
        if (integrity != null)
            return Incomplete(integrity);

        if (_usageService != null)
        {
            CloudUsageResult usage;
            try
            {
                usage = await _usageService.CalculateUsageAsync(forceRefresh: true, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                return Incomplete("Не удалось измерить квоту — ротация закрыта.");
            }

            if (!CloudQuotaPolicy.IsMeasurementReliable(usage))
                return Incomplete("Измерение квоты неполное — ротация закрыта.");

            try
            {
                long projected = checked(usage.TotalBytes + inventory.TotalBytes);
                if (projected > CloudQuotaPolicy.FreeQuotaBytes)
                    return Incomplete("Недостаточно места в квоте 1 ГиБ для копии нового поколения.");
            }
            catch (OverflowException)
            {
                return Incomplete("Недостаточно места в квоте 1 ГиБ для копии нового поколения.");
            }
        }

        var preview = new PasswordRotationPreview
        {
            Token = Guid.NewGuid().ToString("N"),
            CreatedAtUtc = DateTime.UtcNow,
            IsComplete = true,
            ActiveGenerationId = _keyHelper.GenerationId,
            PackageCount = inventory.Packages.Count,
            PointerCount = inventory.Pointers.Count,
            BlobCount = inventory.Blobs.Count,
            BytesToCopy = inventory.TotalBytes,
            Summary = $"К перешифрованию: пакетов {inventory.Packages.Count}, указателей {inventory.Pointers.Count}, вложений {inventory.Blobs.Count}. Старое поколение после переключения не удаляется."
        };
        _lastPreview = preview;
        _lastInventory = inventory;
        return preview;
    }

    private async Task<Inventory?> InventoryCurrentAsync(CancellationToken ct)
    {
        var inventory = new Inventory();
        string devicesPrefix = _keyHelper.GetDevicesPrefix();
        var deviceObjects = await ListAllAsync(devicesPrefix, ct).ConfigureAwait(false);
        if (deviceObjects == null)
            return null;

        foreach (var obj in deviceObjects)
        {
            if (!obj.Key.StartsWith(devicesPrefix, StringComparison.Ordinal))
                continue;
            if (_keyHelper.TryParsePackageKey(obj.Key, out Guid deviceId, out Guid packageId))
            {
                inventory.Packages.Add(new KeyedObject
                {
                    Key = obj.Key,
                    Size = obj.Size,
                    ETag = StorageObjectMetadata.NormalizeETag(obj.ETag),
                    DeviceId = deviceId,
                    PackageId = packageId
                });
                inventory.TotalBytes += obj.Size;
            }
            else if (_keyHelper.TryParsePointerKey(obj.Key, out Guid pointerDevice))
            {
                inventory.Pointers.Add(new KeyedObject
                {
                    Key = obj.Key,
                    Size = obj.Size,
                    ETag = StorageObjectMetadata.NormalizeETag(obj.ETag),
                    DeviceId = pointerDevice
                });
                inventory.TotalBytes += obj.Size;
            }
            else
            {
                return null;
            }

            if (inventory.Packages.Count + inventory.Pointers.Count > _maxObjects)
                return null;
        }

        string blobsPrefix = _keyHelper.GetBlobsPrefix();
        var blobObjects = await ListAllAsync(blobsPrefix, ct).ConfigureAwait(false);
        if (blobObjects == null)
            return null;

        foreach (var obj in blobObjects)
        {
            if (!obj.Key.StartsWith(blobsPrefix, StringComparison.Ordinal))
                continue;
            if (!_keyHelper.TryParseBlobKey(obj.Key, out string sha))
                return null;

            inventory.Blobs.Add(new KeyedObject
            {
                Key = obj.Key,
                Size = obj.Size,
                ETag = StorageObjectMetadata.NormalizeETag(obj.ETag),
                Sha256 = sha
            });
            inventory.TotalBytes += obj.Size;
            if (inventory.Blobs.Count > _maxObjects)
                return null;
        }

        return inventory;
    }

    private async Task<string?> VerifyInventoryIntegrityAsync(Inventory inventory, string? password, CancellationToken ct)
    {
        var packagesByDevice = inventory.Packages
            .GroupBy(p => p.DeviceId)
            .ToDictionary(g => g.Key, g => g.ToList());

        foreach (var pkg in inventory.Packages)
        {
            ct.ThrowIfCancellationRequested();
            var obj = await _transport.GetObjectAsync(pkg.Key, ct).ConfigureAwait(false);
            if (obj?.Content == null || obj.Content.Length == 0 || obj.Content.Length > MaxReadablePackageBytes)
                return "Пакет отсутствует или превышает допустимый размер — ротация закрыта.";
            if (obj.Content.Length != pkg.Size && pkg.Size > 0)
                return "Размер пакета не совпадает с перечислением — ротация закрыта.";

            string etag = StorageObjectMetadata.NormalizeETag(obj.Metadata?.ETag);
            if (string.IsNullOrEmpty(pkg.ETag))
                pkg.ETag = etag;
            else if (!string.Equals(pkg.ETag, etag, StringComparison.Ordinal))
                return "ETag пакета изменился — ротация закрыта.";

            SyncPackageEnvelope envelope;
            try
            {
                envelope = ParseAndValidateEnvelope(obj.Content, pkg.DeviceId, pkg.PackageId);
            }
            catch (Exception ex)
            {
                return "Пакет не согласован с ключом объекта: " + ErrorLogService.Sanitize(ex.Message);
            }

            if (!string.IsNullOrEmpty(password))
            {
                try
                {
                    byte[] plain = DecryptPackagePlaintext(obj.Content, password, envelope);
                    CryptographicOperations.ZeroMemory(plain);
                }
                catch
                {
                    return "Старый пароль не расшифровывает пакеты или вложения текущего поколения.";
                }
            }

            pkg.Digest = SyncFingerprintHelper.ComputeSha256(obj.Content);
        }

        foreach (var ptr in inventory.Pointers)
        {
            ct.ThrowIfCancellationRequested();
            var obj = await _transport.GetObjectAsync(ptr.Key, ct).ConfigureAwait(false);
            if (obj?.Content == null || obj.Content.Length == 0)
                return "Указатель устройства отсутствует — ротация закрыта.";

            string etag = StorageObjectMetadata.NormalizeETag(obj.Metadata?.ETag);
            if (string.IsNullOrEmpty(ptr.ETag))
                ptr.ETag = etag;
            else if (!string.Equals(ptr.ETag, etag, StringComparison.Ordinal))
                return "ETag указателя изменился — ротация закрыта.";

            DevicePointerPayload? parsed;
            try
            {
                parsed = JsonSerializer.Deserialize<DevicePointerPayload>(obj.Content, JsonOptions);
            }
            catch
            {
                return "Указатель устройства повреждён — ротация закрыта.";
            }

            if (parsed == null || parsed.DeviceId != ptr.DeviceId || parsed.FormatVersion != 1)
                return "Указатель устройства не согласован с ключом — ротация закрыта.";

            packagesByDevice.TryGetValue(ptr.DeviceId, out var devicePackages);
            int packageCount = devicePackages?.Count ?? 0;
            if (parsed.PackageCount != packageCount)
                return "PackageCount указателя не совпадает с числом пакетов устройства — ротация закрыта.";

            if (parsed.LatestPackageId == Guid.Empty ||
                devicePackages == null ||
                devicePackages.All(p => p.PackageId != parsed.LatestPackageId))
            {
                return "LatestPackageId отсутствует в полном наборе пакетов устройства — ротация закрыта.";
            }

            string expectedKey = _keyHelper.GetPackageKey(parsed.DeviceId, parsed.LatestPackageId);
            if (!string.IsNullOrEmpty(parsed.LatestPackageKey) &&
                !string.Equals(parsed.LatestPackageKey, expectedKey, StringComparison.Ordinal))
            {
                if (!_keyHelper.TryParsePackageKey(parsed.LatestPackageKey, out Guid keyDev, out Guid keyPkg) ||
                    keyDev != parsed.DeviceId || keyPkg != parsed.LatestPackageId)
                {
                    return "LatestPackageKey не согласован с поколением и идентификаторами — ротация закрыта.";
                }
            }

            var latest = devicePackages.First(p => p.PackageId == parsed.LatestPackageId);
            if (!string.IsNullOrEmpty(parsed.PackageDigest) &&
                !string.Equals(parsed.PackageDigest, latest.Digest, StringComparison.OrdinalIgnoreCase))
            {
                return "PackageDigest указателя не совпадает с содержимым пакета — ротация закрыта.";
            }
        }

        foreach (var blob in inventory.Blobs)
        {
            ct.ThrowIfCancellationRequested();
            var obj = await _transport.GetObjectAsync(blob.Key, ct).ConfigureAwait(false);
            if (obj?.Content == null || obj.Content.Length == 0)
                return "Blob отсутствует — ротация закрыта.";

            string etag = StorageObjectMetadata.NormalizeETag(obj.Metadata?.ETag);
            if (string.IsNullOrEmpty(blob.ETag))
                blob.ETag = etag;
            else if (!string.Equals(blob.ETag, etag, StringComparison.Ordinal))
                return "ETag blob изменился — ротация закрыта.";

            if (!string.IsNullOrEmpty(password))
            {
                try
                {
                    byte[] plain = DecryptBlobEnvelope(obj.Content, blob.Sha256, password);
                    CryptographicOperations.ZeroMemory(plain);
                }
                catch
                {
                    return "Старый пароль не расшифровывает пакеты или вложения текущего поколения.";
                }
            }
        }

        if (inventory.Packages.Count == 0 && inventory.Blobs.Count == 0 && !string.IsNullOrEmpty(password))
        {
            string? stored = await _passwordStorage.LoadPasswordAsync(ct).ConfigureAwait(false);
            if (string.IsNullOrEmpty(stored) || !string.Equals(stored, password, StringComparison.Ordinal))
                return "Старый пароль не совпадает с сохранённым. Облако не изменялось.";
        }

        return null;
    }

    private static bool InventoriesMatch(Inventory expected, Inventory actual)
    {
        return SameSet(expected.Packages, actual.Packages) &&
               SameSet(expected.Pointers, actual.Pointers) &&
               SameSet(expected.Blobs, actual.Blobs);
    }

    private static bool SameSet(List<KeyedObject> a, List<KeyedObject> b)
    {
        if (a.Count != b.Count)
            return false;
        var map = b.ToDictionary(x => x.Key, StringComparer.Ordinal);
        foreach (var item in a)
        {
            if (!map.TryGetValue(item.Key, out var other))
                return false;
            if (item.Size != other.Size)
                return false;
            if (!string.IsNullOrEmpty(item.ETag) && !string.IsNullOrEmpty(other.ETag) &&
                !string.Equals(item.ETag, other.ETag, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private async Task<bool> TryVerifyInventoryPasswordAsync(Inventory inventory, string password, CancellationToken ct)
    {
        if (inventory.Packages.Count == 0 && inventory.Blobs.Count == 0)
        {
            string? stored = await _passwordStorage.LoadPasswordAsync(ct).ConfigureAwait(false);
            return !string.IsNullOrEmpty(stored) && string.Equals(stored, password, StringComparison.Ordinal);
        }

        foreach (var pkg in inventory.Packages)
        {
            ct.ThrowIfCancellationRequested();
            var obj = await _transport.GetObjectAsync(pkg.Key, ct).ConfigureAwait(false);
            if (obj?.Content == null || obj.Content.Length == 0 || obj.Content.Length > MaxReadablePackageBytes)
                return false;
            try
            {
                var envelope = ParseAndValidateEnvelope(obj.Content, pkg.DeviceId, pkg.PackageId);
                byte[] plain = DecryptPackagePlaintext(obj.Content, password, envelope);
                CryptographicOperations.ZeroMemory(plain);
            }
            catch
            {
                return false;
            }
        }

        foreach (var blob in inventory.Blobs)
        {
            ct.ThrowIfCancellationRequested();
            var obj = await _transport.GetObjectAsync(blob.Key, ct).ConfigureAwait(false);
            if (obj?.Content == null || obj.Content.Length == 0)
                return false;
            try
            {
                byte[] plain = DecryptBlobEnvelope(obj.Content, blob.Sha256, password);
                CryptographicOperations.ZeroMemory(plain);
            }
            catch
            {
                return false;
            }
        }

        return true;
    }

    private async Task CopyPackageAsync(KeyedObject pkg, SyncObjectKeyHelper dest, string oldPassword, string newPassword, CancellationToken ct)
    {
        var obj = await _transport.GetObjectAsync(pkg.Key, ct).ConfigureAwait(false);
        if (obj?.Content == null)
            throw new InvalidOperationException("Пакет исчез во время ротации.");

        var envelope = ParseAndValidateEnvelope(obj.Content, pkg.DeviceId, pkg.PackageId);
        byte[] plaintext = DecryptPackagePlaintext(obj.Content, oldPassword, envelope);
        byte[] reencrypted = ReencryptPackage(envelope, plaintext, newPassword);
        string destKey = dest.GetPackageKey(pkg.DeviceId, pkg.PackageId);
        try
        {
            await PutImmutableOrVerifyPackageAsync(destKey, reencrypted, plaintext, pkg.DeviceId, pkg.PackageId, newPassword, ct)
                .ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private async Task CopyPointerAsync(KeyedObject ptr, SyncObjectKeyHelper dest, CancellationToken ct)
    {
        var obj = await _transport.GetObjectAsync(ptr.Key, ct).ConfigureAwait(false);
        if (obj?.Content == null)
            throw new InvalidOperationException("Указатель устройства исчез во время ротации.");

        DevicePointerPayload? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<DevicePointerPayload>(obj.Content, JsonOptions);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Указатель устройства повреждён: " + ErrorLogService.Sanitize(ex.Message));
        }

        if (parsed == null || parsed.DeviceId != ptr.DeviceId || parsed.LatestPackageId == Guid.Empty)
            throw new InvalidOperationException("Указатель устройства не соответствует ключу объекта.");

        string destPackageKey = dest.GetPackageKey(parsed.DeviceId, parsed.LatestPackageId);
        var destPkg = await _transport.GetObjectAsync(destPackageKey, ct).ConfigureAwait(false);
        if (destPkg?.Content == null || destPkg.Content.Length == 0)
            throw new InvalidOperationException("Пакет нового поколения отсутствует при копировании указателя.");

        parsed.LatestPackageKey = destPackageKey;
        parsed.PackageDigest = SyncFingerprintHelper.ComputeSha256(destPkg.Content);
        byte[] rewritten = JsonSerializer.SerializeToUtf8Bytes(parsed);
        string destKey = dest.GetDevicePointerKey(ptr.DeviceId);
        try
        {
            await _transport.PutImmutableObjectAsync(destKey, rewritten, "application/json", ct).ConfigureAwait(false);
        }
        catch (CloudConflictException)
        {
            var existing = await _transport.GetObjectAsync(destKey, ct).ConfigureAwait(false);
            if (existing?.Content == null)
                throw new InvalidOperationException("Конфликт указателя в новом поколении: объект отсутствует при проверке.");
            var existingParsed = JsonSerializer.Deserialize<DevicePointerPayload>(existing.Content, JsonOptions);
            if (existingParsed == null ||
                existingParsed.LatestPackageId != parsed.LatestPackageId ||
                !string.Equals(existingParsed.LatestPackageKey, destPackageKey, StringComparison.Ordinal) ||
                !string.Equals(existingParsed.PackageDigest, parsed.PackageDigest, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Конфликт указателя в новом поколении: содержимое не совпадает.");
            }
        }
    }

    private async Task CopyBlobAsync(KeyedObject blob, SyncObjectKeyHelper dest, string oldPassword, string newPassword, CancellationToken ct)
    {
        var obj = await _transport.GetObjectAsync(blob.Key, ct).ConfigureAwait(false);
        if (obj?.Content == null)
            throw new InvalidOperationException("Blob исчез во время ротации.");

        byte[] plain = DecryptBlobEnvelope(obj.Content, blob.Sha256, oldPassword);
        byte[] envelope;
        try
        {
            envelope = EncryptBlobEnvelope(plain, blob.Sha256, newPassword);
        }
        catch
        {
            CryptographicOperations.ZeroMemory(plain);
            throw;
        }

        string destKey = dest.GetBlobKey(blob.Sha256);
        try
        {
            await PutImmutableOrVerifyBlobAsync(destKey, envelope, plain, blob.Sha256, newPassword, ct).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    private async Task PutImmutableOrVerifyPackageAsync(
        string destKey,
        byte[] content,
        byte[] expectedPlaintext,
        Guid deviceId,
        Guid packageId,
        string newPassword,
        CancellationToken ct)
    {
        try
        {
            await _transport.PutImmutableObjectAsync(destKey, content, "application/json", ct).ConfigureAwait(false);
        }
        catch (CloudConflictException)
        {
            var existing = await _transport.GetObjectAsync(destKey, ct).ConfigureAwait(false);
            if (existing?.Content == null)
                throw new InvalidOperationException("Конфликт в новом поколении: объект отсутствует при проверке.");

            var envelope = ParseAndValidateEnvelope(existing.Content, deviceId, packageId);
            byte[] actual = DecryptPackagePlaintext(existing.Content, newPassword, envelope);
            try
            {
                if (!actual.AsSpan().SequenceEqual(expectedPlaintext))
                    throw new InvalidOperationException("Конфликт в новом поколении: пакет расшифровывается, но содержимое не совпадает.");
            }
            finally
            {
                CryptographicOperations.ZeroMemory(actual);
            }
        }
    }

    private async Task PutImmutableOrVerifyBlobAsync(
        string destKey,
        byte[] content,
        byte[] expectedPlaintext,
        string sha256,
        string newPassword,
        CancellationToken ct)
    {
        try
        {
            await _transport.PutImmutableObjectAsync(destKey, content, "application/octet-stream", ct).ConfigureAwait(false);
        }
        catch (CloudConflictException)
        {
            var existing = await _transport.GetObjectAsync(destKey, ct).ConfigureAwait(false);
            if (existing?.Content == null)
                throw new InvalidOperationException("Конфликт в новом поколении: blob отсутствует при проверке.");

            byte[] actual = DecryptBlobEnvelope(existing.Content, sha256, newPassword);
            try
            {
                if (!actual.AsSpan().SequenceEqual(expectedPlaintext))
                    throw new InvalidOperationException("Конфликт в новом поколении: blob расшифровывается, но содержимое не совпадает.");
            }
            finally
            {
                CryptographicOperations.ZeroMemory(actual);
            }
        }
    }

    private byte[] ReencryptPackage(SyncPackageEnvelope envelope, byte[] plaintext, string newPassword)
    {
        var newEnvelope = new SyncPackageEnvelope
        {
            Magic = envelope.Magic,
            FormatVersion = envelope.FormatVersion,
            PackageId = envelope.PackageId,
            DeviceId = envelope.DeviceId,
            CreatedAtUtc = envelope.CreatedAtUtc,
            Crypto = new SyncCryptoHeader
            {
                Algorithm = SyncCryptoService.AlgorithmName,
                KdfAlgorithm = SyncCryptoService.KdfName,
                KdfVersion = SyncCryptoService.KdfVersion,
                KdfIterations = _crypto.Iterations,
                KdfDescriptor = _crypto.CurrentDescriptor.ToCanonicalText()
            }
        };

        byte[] aad = newEnvelope.GetAssociatedData();
        var enc = _crypto.EncryptPayload(plaintext, newPassword, aad);

        newEnvelope.Crypto.Algorithm = enc.Algorithm;
        newEnvelope.Crypto.KdfAlgorithm = enc.KdfAlgorithm;
        newEnvelope.Crypto.KdfVersion = enc.KdfVersion;
        newEnvelope.Crypto.KdfIterations = enc.KdfIterations;
        newEnvelope.Crypto.KdfDescriptor = enc.KdfDescriptorText;
        newEnvelope.Crypto.SaltBase64 = Convert.ToBase64String(enc.Salt);
        newEnvelope.Crypto.NonceBase64 = Convert.ToBase64String(enc.Nonce);
        newEnvelope.Crypto.TagBase64 = Convert.ToBase64String(enc.Tag);
        newEnvelope.EncryptedPayloadBase64 = Convert.ToBase64String(enc.Ciphertext);

        return Encoding.UTF8.GetBytes(JsonSerializer.Serialize(newEnvelope, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        }));
    }

    private SyncPackageEnvelope ParseAndValidateEnvelope(byte[] packageBytes, Guid expectedDeviceId, Guid expectedPackageId)
    {
        var envelope = JsonSerializer.Deserialize<SyncPackageEnvelope>(packageBytes, JsonOptions)
            ?? throw new SyncSecurityException("Пустой envelope пакета.");
        if (!string.Equals(envelope.Magic, "QNSP", StringComparison.Ordinal))
            throw new SyncSecurityException("Неверная сигнатура пакета.");
        if (envelope.FormatVersion != 1)
            throw new SyncSecurityException("Неподдерживаемая версия пакета.");
        if (envelope.Crypto == null ||
            !string.Equals(envelope.Crypto.Algorithm, SyncCryptoService.AlgorithmName, StringComparison.OrdinalIgnoreCase))
        {
            throw new SyncSecurityException("Неподдерживаемые параметры криптографического конверта.");
        }

        // Reject unknown KDF descriptors before any derivation (legacy envelopes keep working).
        _crypto.ResolveDescriptor(
            envelope.Crypto.KdfDescriptor,
            envelope.Crypto.KdfAlgorithm,
            envelope.Crypto.KdfVersion,
            envelope.Crypto.KdfIterations,
            SyncCryptoService.SaltByteSize);

        if (envelope.DeviceId != expectedDeviceId || envelope.PackageId != expectedPackageId)
            throw new SyncSecurityException("DeviceId/PackageId envelope не совпадают с ключом объекта.");

        return envelope;
    }

    private byte[] DecryptPackagePlaintext(byte[] packageBytes, string password, SyncPackageEnvelope envelope)
    {
        byte[] ciphertext = Convert.FromBase64String(envelope.EncryptedPayloadBase64);
        byte[] salt = Convert.FromBase64String(envelope.Crypto.SaltBase64);
        byte[] nonce = Convert.FromBase64String(envelope.Crypto.NonceBase64);
        byte[] tag = Convert.FromBase64String(envelope.Crypto.TagBase64);
        KdfEnvelopeReading reading = _crypto.ResolveDescriptor(
            envelope.Crypto.KdfDescriptor,
            envelope.Crypto.KdfAlgorithm,
            envelope.Crypto.KdfVersion,
            envelope.Crypto.KdfIterations,
            SyncCryptoService.SaltByteSize);
        return _crypto.DecryptPayload(
            ciphertext, salt, nonce, tag, password, envelope.GetAssociatedData(), reading.Descriptor.Iterations);
    }

    private byte[] EncryptBlobEnvelope(byte[] plaintext, string sha256Hex, string password)
    {
        byte[] aad = Encoding.UTF8.GetBytes($"QNBA|1|{sha256Hex.ToLowerInvariant()}");
        var enc = _crypto.EncryptPayload(plaintext, password, aad);
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        bw.Write(BlobMagic);
        bw.Write(BlobEnvelopeVersion);
        bw.Write(KdfAlgorithmIds.Pbkdf2HmacSha256Id);
        bw.Write((ushort)KdfDescriptorConstants.CurrentDescriptorVersion);
        bw.Write(enc.KdfIterations);
        bw.Write(enc.Salt.Length);
        bw.Write(enc.Salt);
        bw.Write(enc.Nonce);
        bw.Write(enc.Tag);
        bw.Write((long)plaintext.Length);
        bw.Write(enc.Ciphertext);
        bw.Flush();
        return ms.ToArray();
    }

    private byte[] DecryptBlobEnvelope(byte[] envelope, string sha256Hex, string password)
    {
        int minOverhead = 4 + 1 + 4 + SyncCryptoService.SaltByteSize + SyncCryptoService.NonceByteSize
            + SyncCryptoService.TagByteSize + 8;
        if (envelope == null || envelope.Length < minOverhead)
            throw new SyncSecurityException("Blob envelope повреждён.");

        using var ms = new MemoryStream(envelope);
        using var br = new BinaryReader(ms, Encoding.UTF8, leaveOpen: true);
        byte[] magic = br.ReadBytes(4);
        if (magic.Length != 4 || magic[0] != BlobMagic[0] || magic[1] != BlobMagic[1]
            || magic[2] != BlobMagic[2] || magic[3] != BlobMagic[3])
            throw new SyncSecurityException("Неверная сигнатура blob envelope.");

        byte version = br.ReadByte();
        int blobIterations = _crypto.Iterations;
        if (version == BlobEnvelopeVersion)
        {
            byte algorithmId = br.ReadByte();
            if (algorithmId != KdfAlgorithmIds.Pbkdf2HmacSha256Id)
                throw new SyncSecurityException("Неподдерживаемый алгоритм KDF blob envelope.");

            int descriptorVersion = br.ReadUInt16();
            blobIterations = br.ReadInt32();
            var descriptor = new KdfDescriptor(
                KdfAlgorithmIds.Pbkdf2HmacSha256,
                descriptorVersion,
                blobIterations,
                SyncCryptoService.SaltByteSize);
            try
            {
                descriptor.Validate(KdfDescriptorLimits.CloudEnvelope);
            }
            catch (KdfDescriptorValidationException ex)
            {
                throw new SyncSecurityException("Недопустимые параметры KDF blob envelope.", ex);
            }
        }
        else if (version != BlobEnvelopeVersionV1)
        {
            throw new SyncSecurityException("Неподдерживаемая версия blob envelope.");
        }

        int saltLen = br.ReadInt32();
        byte[] salt = br.ReadBytes(saltLen);
        byte[] nonce = br.ReadBytes(SyncCryptoService.NonceByteSize);
        byte[] tag = br.ReadBytes(SyncCryptoService.TagByteSize);
        long plaintextSize = br.ReadInt64();
        if (plaintextSize < 0 || plaintextSize > _settings.MaxAttachmentSyncBytes)
            throw new SyncSecurityException("Недопустимый размер plaintext blob.");

        byte[] ciphertext = br.ReadBytes((int)(envelope.Length - ms.Position));
        byte[] aad = Encoding.UTF8.GetBytes($"QNBA|1|{sha256Hex.ToLowerInvariant()}");
        // v1 blobs carry no iteration count: they were written with this service's configured
        // work factor, so the historical default is preserved exactly. v2 blobs carry the
        // explicit descriptor and use the stored work factor.
        int effectiveIterations = version == BlobEnvelopeVersion ? blobIterations : _crypto.Iterations;
        byte[] plaintext = _crypto.DecryptPayload(ciphertext, salt, nonce, tag, password, aad, effectiveIterations);
        if (plaintext.Length != plaintextSize)
        {
            CryptographicOperations.ZeroMemory(plaintext);
            throw new SyncSecurityException("Длина plaintext blob не совпадает с полем envelope.");
        }

        string actualHash = Convert.ToHexString(SHA256.HashData(plaintext)).ToLowerInvariant();
        if (!string.Equals(actualHash, sha256Hex, StringComparison.OrdinalIgnoreCase))
        {
            CryptographicOperations.ZeroMemory(plaintext);
            throw new SyncSecurityException("SHA-256 plaintext blob не совпадает с ключом объекта.");
        }

        return plaintext;
    }

    private async Task<List<StorageObjectSummary>?> ListAllAsync(string prefix, CancellationToken ct)
    {
        var all = new List<StorageObjectSummary>();
        string? token = null;
        int pages = 0;

        do
        {
            ct.ThrowIfCancellationRequested();
            if (pages >= _maxPages || all.Count >= _maxObjects)
                return null;

            StorageListResult page;
            try
            {
                page = await _transport.ListObjectsV2Async(new StorageListRequest
                {
                    Prefix = prefix,
                    MaxKeys = _pageSize,
                    ContinuationToken = token
                }, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                return null;
            }

            pages++;
            if (page.Objects != null)
            {
                foreach (var obj in page.Objects)
                {
                    if (!obj.Key.StartsWith(prefix, StringComparison.Ordinal))
                        continue;
                    all.Add(obj);
                    if (all.Count >= _maxObjects)
                        return null;
                }
            }

            if (page.IsTruncated && !string.IsNullOrEmpty(page.NextContinuationToken))
            {
                if (pages >= _maxPages)
                    return null;
                token = page.NextContinuationToken;
            }
            else if (page.IsTruncated)
            {
                return null;
            }
            else
            {
                token = null;
            }
        } while (!string.IsNullOrEmpty(token));

        return all;
    }

    private async Task<bool> TryPromoteOrSaveNewAsync(string newPassword, CancellationToken ct)
    {
        try
        {
            if (_passwordStorage.HasPendingPassword())
            {
                string? pending = await _passwordStorage.LoadPendingPasswordAsync(ct).ConfigureAwait(false);
                if (string.Equals(pending, newPassword, StringComparison.Ordinal))
                {
                    await _passwordStorage.PromotePendingPasswordAsync(ct).ConfigureAwait(false);
                    return true;
                }
            }

            await _passwordStorage.SavePasswordAsync(newPassword, ct).ConfigureAwait(false);
            await _passwordStorage.DeletePendingPasswordAsync(ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            ErrorLogService.Write("PasswordRotation.Promote", ex.GetType().Name);
            return false;
        }
    }

    private async Task<bool> TrySaveLocalPasswordAsync(string newPassword, CancellationToken ct)
    {
        try
        {
            await _passwordStorage.SavePasswordAsync(newPassword, ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            ErrorLogService.Write("PasswordRotation.SavePassword", ex.GetType().Name);
            return false;
        }
    }

    private static SyncGenerationPointer ClonePointer(SyncGenerationPointer source) => new()
    {
        FormatVersion = source.FormatVersion,
        ActiveGenerationId = source.ActiveGenerationId,
        PendingGenerationId = source.PendingGenerationId,
        PreviousGenerationId = source.PreviousGenerationId,
        SwitchedAtUtc = source.SwitchedAtUtc
    };

    private static PasswordRotationPreview Incomplete(string reason) => new()
    {
        IsComplete = false,
        BlockReason = reason,
        Summary = reason
    };

    private static PasswordRotationExecuteResult FailExecute(string summary) => new()
    {
        TokenAccepted = true,
        Success = false,
        CloudSwitched = false,
        LocalPasswordSaved = false,
        Summary = summary
    };

    private static PasswordRotationExecuteResult CanceledResult(string summary) => new()
    {
        TokenAccepted = true,
        Success = false,
        Canceled = true,
        CloudSwitched = false,
        LocalPasswordSaved = false,
        Summary = summary
    };

    private sealed class Inventory
    {
        public List<KeyedObject> Packages { get; } = new();
        public List<KeyedObject> Pointers { get; } = new();
        public List<KeyedObject> Blobs { get; } = new();
        public long TotalBytes { get; set; }
    }

    private sealed class KeyedObject
    {
        public string Key { get; set; } = string.Empty;
        public long Size { get; set; }
        public string ETag { get; set; } = string.Empty;
        public Guid DeviceId { get; set; }
        public Guid PackageId { get; set; }
        public string Sha256 { get; set; } = string.Empty;
        public string Digest { get; set; } = string.Empty;
    }
}
