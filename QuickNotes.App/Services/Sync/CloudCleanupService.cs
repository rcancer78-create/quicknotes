using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using QuickNotes.App.Models.Sync;
using QuickNotes.App.Services.Crypto;
using QuickNotes.App.Services;

namespace QuickNotes.App.Services.Sync;

/// <summary>
/// Manual, fail-closed cleanup of unreferenced content-addressed attachment blobs.
/// Never deletes packages, pointers, unknown keys, or objects outside the blobs prefix.
/// Preview does not mutate storage. Execute requires an immutable preview token.
/// Reachability is conservative: a blob mentioned in any fully verified historical package
/// of a device is treated as live (incremental packages may omit unchanged attachments).
/// Delete uses back-to-back HEAD (ETag+size) then unconditional Delete; If-Match delete
/// is not available on the current Yandex-compatible AWS SDK 3.7.415 surface.
/// </summary>
public class CloudCleanupService : ICloudCleanupService
{
    public const int DefaultMaxPages = CloudUsageService.DefaultMaxPages;
    public const int DefaultMaxObjects = CloudUsageService.DefaultMaxObjects;
    public const int DefaultPageSize = CloudUsageService.DefaultPageSize;
    public const long MaxReadablePackageBytes = 25L * 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly ICloudObjectStoreTransport _transport;
    private readonly ISyncCryptoService _crypto;
    private readonly SyncObjectKeyHelper _keyHelper;
    private readonly ICloudUsageService? _usageService;
    private readonly SyncCloudExclusiveLock? _exclusiveLock;
    private readonly int _maxPages;
    private readonly int _maxObjects;
    private readonly int _pageSize;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private CloudCleanupPreview? _lastPreview;
    private Dictionary<string, CloudCleanupCandidate>? _lastSnapshot;
    private HashSet<string>? _lastReachableKeys;

    public CloudCleanupService(
        ICloudObjectStoreTransport transport,
        ISyncCryptoService crypto,
        SyncObjectKeyHelper keyHelper,
        ICloudUsageService? usageService = null,
        SyncCloudExclusiveLock? exclusiveLock = null,
        int maxPages = DefaultMaxPages,
        int maxObjects = DefaultMaxObjects,
        int pageSize = DefaultPageSize)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _crypto = crypto ?? throw new ArgumentNullException(nameof(crypto));
        _keyHelper = keyHelper ?? throw new ArgumentNullException(nameof(keyHelper));
        _usageService = usageService;
        _exclusiveLock = exclusiveLock;
        _maxPages = Math.Max(1, maxPages);
        _maxObjects = Math.Max(1, maxObjects);
        _pageSize = Math.Clamp(pageSize, 1, 1000);
    }

    public async Task<CloudCleanupPreview> PreviewAsync(string encryptionPassword, CancellationToken ct = default)
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
                var discovery = await DiscoverAsync(encryptionPassword, ct).ConfigureAwait(false);
                if (!discovery.Complete)
                {
                    var blocked = new CloudCleanupPreview
                    {
                        Token = Guid.NewGuid().ToString("N"),
                        CreatedAtUtc = DateTime.UtcNow,
                        IsComplete = false,
                        BlockReason = discovery.BlockReason,
                        Candidates = Array.Empty<CloudCleanupCandidate>(),
                        ReclaimableBytes = 0,
                        BlobObjectsExamined = discovery.Blobs.Count,
                        ReachableBlobCount = discovery.ReachableKeys.Count
                    };
                    _lastPreview = blocked;
                    _lastSnapshot = null;
                    _lastReachableKeys = null;
                    return blocked;
                }

                var candidates = new List<CloudCleanupCandidate>();
                long reclaimable = 0;
                foreach (var blob in discovery.Blobs.OrderBy(b => b.Key, StringComparer.Ordinal))
                {
                    if (discovery.ReachableKeys.Contains(blob.Key))
                        continue;

                    if (string.IsNullOrEmpty(blob.ETag))
                        continue;

                    candidates.Add(new CloudCleanupCandidate
                    {
                        Key = blob.Key,
                        Sha256 = blob.Sha256,
                        ETag = blob.ETag,
                        Size = blob.Size,
                        Reason = "Не упоминается ни в одном актуальном пакете/указателе пространства QuickNotes."
                    });

                    if (blob.Size > 0)
                    {
                        if (long.MaxValue - reclaimable < blob.Size)
                            reclaimable = long.MaxValue;
                        else
                            reclaimable += blob.Size;
                    }
                }

                var preview = new CloudCleanupPreview
                {
                    Token = Guid.NewGuid().ToString("N"),
                    CreatedAtUtc = DateTime.UtcNow,
                    IsComplete = true,
                    Candidates = candidates,
                    ReclaimableBytes = reclaimable,
                    BlobObjectsExamined = discovery.Blobs.Count,
                    ReachableBlobCount = discovery.ReachableKeys.Count
                };

                _lastPreview = preview;
                _lastSnapshot = candidates.ToDictionary(c => c.Key, c => c, StringComparer.Ordinal);
                _lastReachableKeys = new HashSet<string>(discovery.ReachableKeys, StringComparer.Ordinal);
                return preview;
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

    public async Task<CloudCleanupExecuteResult> ExecuteAsync(
        string previewToken,
        string encryptionPassword,
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
            return new CloudCleanupExecuteResult
            {
                TokenAccepted = true,
                Canceled = true,
                Summary = "Очистка отменена до начала удаления. Объекты не удалялись."
            };
        }

        try
        {
            if (_lastPreview == null ||
                !_lastPreview.IsComplete ||
                _lastSnapshot == null ||
                !string.Equals(_lastPreview.Token, previewToken, StringComparison.Ordinal))
            {
                return new CloudCleanupExecuteResult
                {
                    TokenAccepted = false,
                    Summary = "Снимок предварительного просмотра отсутствует, неполный или не совпадает. Удаление не выполнялось."
                };
            }

            var snapshot = _lastSnapshot.Values.ToList();
            var items = new List<CloudCleanupItemResult>();
            int deleted = 0, skipped = 0, failed = 0;
            long deletedBytes = 0;

            DiscoveryResult live;
            try
            {
                live = await DiscoverAsync(encryptionPassword, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return new CloudCleanupExecuteResult
                {
                    TokenAccepted = true,
                    Canceled = true,
                    Skipped = snapshot.Count,
                    Summary = "Очистка отменена до удаления. Объекты не удалялись.",
                    Items = snapshot.Select(c => new CloudCleanupItemResult
                    {
                        Key = c.Key,
                        Outcome = "skipped",
                        Detail = "Отмена"
                    }).ToList()
                };
            }

            if (!live.Complete)
            {
                return new CloudCleanupExecuteResult
                {
                    TokenAccepted = true,
                    Skipped = snapshot.Count,
                    Summary = "Повторная проверка достижимости неполная — удаление отменено (fail-closed).",
                    Items = snapshot.Select(c => new CloudCleanupItemResult
                    {
                        Key = c.Key,
                        Outcome = "skipped",
                        Detail = live.BlockReason
                    }).ToList()
                };
            }

            foreach (var candidate in snapshot)
            {
                ct.ThrowIfCancellationRequested();

                if (!_keyHelper.TryParseBlobKey(candidate.Key, out _) ||
                    !candidate.Key.StartsWith(_keyHelper.GetBlobsPrefix(), StringComparison.Ordinal))
                {
                    skipped++;
                    items.Add(Skip(candidate.Key, "Ключ не является content-addressed blob в пространстве QuickNotes."));
                    continue;
                }

                if (live.ReachableKeys.Contains(candidate.Key))
                {
                    skipped++;
                    items.Add(Skip(candidate.Key, "Объект снова упоминается актуальным пакетом — пропущен."));
                    continue;
                }

                StorageObjectMetadata? head;
                try
                {
                    head = await _transport.HeadObjectAsync(candidate.Key, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    ErrorLogService.Write("CloudCleanup.Head", ex.GetType().Name);
                    failed++;
                    items.Add(Fail(candidate.Key, "Ошибка повторной проверки объекта — пропущен."));
                    continue;
                }

                if (head == null)
                {
                    skipped++;
                    items.Add(Skip(candidate.Key, "Объект уже отсутствует — повторное удаление пропущено."));
                    continue;
                }

                string currentEtag = StorageObjectMetadata.NormalizeETag(head.ETag);
                if (!string.Equals(currentEtag, candidate.ETag, StringComparison.Ordinal) ||
                    head.ContentLength != candidate.Size)
                {
                    skipped++;
                    items.Add(Skip(candidate.Key, "ETag или размер изменились после просмотра — объект пропущен."));
                    continue;
                }

                // AWSSDK.S3 3.7.415 / Yandex-compatible DeleteObject не предоставляет доказуемого If-Match.
                // Повторный HEAD сразу перед Delete сужает окно, но не устраняет его полностью.
                StorageObjectMetadata? confirm;
                try
                {
                    confirm = await _transport.HeadObjectAsync(candidate.Key, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    ErrorLogService.Write("CloudCleanup.HeadConfirm", ex.GetType().Name);
                    failed++;
                    items.Add(Fail(candidate.Key, "Ошибка подтверждения объекта перед удалением — пропущен."));
                    continue;
                }

                if (confirm == null)
                {
                    skipped++;
                    items.Add(Skip(candidate.Key, "Объект исчез до удаления — пропущен."));
                    continue;
                }

                string confirmEtag = StorageObjectMetadata.NormalizeETag(confirm.ETag);
                if (!string.Equals(confirmEtag, candidate.ETag, StringComparison.Ordinal) ||
                    confirm.ContentLength != candidate.Size)
                {
                    skipped++;
                    items.Add(Skip(candidate.Key, "Идентичность объекта изменилась непосредственно перед удалением — пропущен."));
                    continue;
                }

                try
                {
                    bool removed = await _transport.DeleteObjectAsync(candidate.Key, ct).ConfigureAwait(false);
                    if (!removed)
                    {
                        skipped++;
                        items.Add(Skip(candidate.Key, "Удаление не подтверждено — объект пропущен."));
                        continue;
                    }

                    deleted++;
                    if (candidate.Size > 0 && long.MaxValue - deletedBytes >= candidate.Size)
                        deletedBytes += candidate.Size;
                    else if (candidate.Size > 0)
                        deletedBytes = long.MaxValue;

                    items.Add(new CloudCleanupItemResult
                    {
                        Key = candidate.Key,
                        Outcome = "deleted",
                        Detail = "Удалён после повторной проверки недостижимости, ETag и размера. Условный Delete по If-Match недоступен — остаётся узкое окно гонки."
                    });
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    ErrorLogService.Write("CloudCleanup.Delete", ex.GetType().Name);
                    failed++;
                    items.Add(Fail(candidate.Key, "Сетевой сбой при удалении — объект не удалялся повторно в этом шаге."));
                }
            }

            _usageService?.InvalidateCache();

            return new CloudCleanupExecuteResult
            {
                TokenAccepted = true,
                Deleted = deleted,
                Skipped = skipped,
                Failed = failed,
                DeletedBytes = deletedBytes,
                Items = items,
                Summary = $"Удалено: {deleted}, пропущено: {skipped}, ошибок: {failed}, освобождено примерно {CloudUsageResult.FormatBytes(deletedBytes)}."
            };
        }
        catch (OperationCanceledException)
        {
            return new CloudCleanupExecuteResult
            {
                TokenAccepted = true,
                Canceled = true,
                Summary = "Очистка отменена. Уже обработанные объекты не удаляются повторно."
            };
        }
        finally
        {
            _gate.Release();
            if (exclusive)
                _exclusiveLock!.Release();
        }
    }

    private sealed class ListedBlob
    {
        public string Key { get; set; } = string.Empty;
        public string Sha256 { get; set; } = string.Empty;
        public string ETag { get; set; } = string.Empty;
        public long Size { get; set; }
    }

    private sealed class DiscoveryResult
    {
        public bool Complete { get; set; }
        public string? BlockReason { get; set; }
        public List<ListedBlob> Blobs { get; set; } = new();
        public HashSet<string> ReachableKeys { get; set; } = new(StringComparer.Ordinal);
    }

    private async Task<DiscoveryResult> DiscoverAsync(string encryptionPassword, CancellationToken ct)
    {
        var result = new DiscoveryResult();
        if (string.IsNullOrEmpty(encryptionPassword))
        {
            result.BlockReason = "Пароль шифрования не задан — очистка закрыта (fail-closed).";
            return result;
        }

        GenerationResolveResult generation;
        try
        {
            generation = await SyncGenerationResolver.ResolveAndApplyAsync(_transport, _keyHelper, ct).ConfigureAwait(false);
        }
        catch (CloudOfflineException)
        {
            result.BlockReason = "Хранилище недоступно — очистка закрыта.";
            return result;
        }
        catch (CloudAuthException)
        {
            result.BlockReason = "Нет доступа к хранилищу — очистка закрыта.";
            return result;
        }

        if (!generation.Success)
        {
            result.BlockReason = generation.Error ?? "Указатель поколения недоступен — очистка закрыта.";
            return result;
        }

        string blobsPrefix = _keyHelper.GetBlobsPrefix();
        var blobPage = await ListAllAsync(blobsPrefix, delimiter: null, ct).ConfigureAwait(false);
        if (blobPage == null)
        {
            result.BlockReason = "Неполное перечисление blob-объектов — очистка закрыта.";
            return result;
        }

        foreach (var obj in blobPage)
        {
            if (!obj.Key.StartsWith(blobsPrefix, StringComparison.Ordinal))
                continue;

            if (!_keyHelper.TryParseBlobKey(obj.Key, out string sha))
            {
                result.BlockReason = "В префиксе blobs обнаружен неизвестный ключ — очистка закрыта.";
                return result;
            }

            string etag = StorageObjectMetadata.NormalizeETag(obj.ETag);
            if (string.IsNullOrEmpty(etag))
            {
                try
                {
                    var head = await _transport.HeadObjectAsync(obj.Key, ct).ConfigureAwait(false);
                    etag = StorageObjectMetadata.NormalizeETag(head?.ETag);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    ErrorLogService.Write("CloudCleanup.BlobHead", ex.GetType().Name);
                    result.BlockReason = "Не удалось подтвердить ETag blob — очистка закрыта.";
                    return result;
                }
            }

            result.Blobs.Add(new ListedBlob
            {
                Key = obj.Key,
                Sha256 = sha,
                ETag = etag,
                Size = obj.Size
            });
        }

        var devices = await DiscoverDeviceIdsFromPagesAsync(ct).ConfigureAwait(false);
        if (devices == null)
        {
            result.BlockReason = "Неполное обнаружение устройств/указателей — очистка закрыта.";
            return result;
        }

        var reachableHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var deviceId in devices)
        {
            ct.ThrowIfCancellationRequested();
            string pointerKey = _keyHelper.GetDevicePointerKey(deviceId);
            StorageObjectResult? pointerObj;
            try
            {
                pointerObj = await _transport.GetObjectAsync(pointerKey, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                ErrorLogService.Write("CloudCleanup.Pointer", ex.GetType().Name);
                result.BlockReason = "Указатель устройства недоступен — очистка закрыта.";
                return result;
            }

            if (pointerObj == null || pointerObj.Content == null || pointerObj.Content.Length == 0)
            {
                result.BlockReason = "Указатель устройства отсутствует или пуст — очистка закрыта.";
                return result;
            }

            DevicePointerPayload? pointer;
            try
            {
                pointer = JsonSerializer.Deserialize<DevicePointerPayload>(pointerObj.Content, JsonOptions);
            }
            catch
            {
                result.BlockReason = "Указатель устройства повреждён — очистка закрыта.";
                return result;
            }

            if (pointer == null || pointer.DeviceId != deviceId || pointer.LatestPackageId == Guid.Empty)
            {
                result.BlockReason = "Указатель устройства невалиден — очистка закрыта.";
                return result;
            }

            string latestKey = pointer.LatestPackageKey ?? _keyHelper.GetPackageKey(deviceId, pointer.LatestPackageId);
            if (!_keyHelper.TryParsePackageKey(latestKey, out Guid latestDevice, out Guid latestPkgId) ||
                latestDevice != deviceId ||
                latestPkgId != pointer.LatestPackageId)
            {
                result.BlockReason = "Указатель ссылается на недопустимый пакет — очистка закрыта.";
                return result;
            }

            string expectedLatestKey = _keyHelper.GetPackageKey(deviceId, pointer.LatestPackageId);
            if (!string.Equals(latestKey, expectedLatestKey, StringComparison.Ordinal))
            {
                result.BlockReason = "Ключ актуального пакета не совпадает с каноническим ключом устройства — очистка закрыта.";
                return result;
            }

            var history = await ListDevicePackageKeysAsync(deviceId, ct).ConfigureAwait(false);
            if (history == null)
            {
                result.BlockReason = "Неполное перечисление истории пакетов устройства — очистка закрыта.";
                return result;
            }

            if (pointer.PackageCount <= 0 || history.Count != pointer.PackageCount)
            {
                result.BlockReason = "Число пакетов не совпадает с PackageCount указателя — очистка закрыта.";
                return result;
            }

            if (!history.Contains(expectedLatestKey))
            {
                result.BlockReason = "Актуальный пакет указателя отсутствует в полном наборе пакетов — очистка закрыта.";
                return result;
            }

            foreach (var packageKey in history)
            {
                ct.ThrowIfCancellationRequested();
                if (!_keyHelper.TryParsePackageKey(packageKey, out Guid pkgDevice, out Guid pkgId) ||
                    pkgDevice != deviceId)
                {
                    result.BlockReason = "Ключ пакета принадлежит другому устройству или невалиден — очистка закрыта.";
                    return result;
                }

                bool ok = await TryCollectHashesFromPackageAsync(
                        packageKey, deviceId, pkgId, encryptionPassword, reachableHashes, ct)
                    .ConfigureAwait(false);
                if (!ok)
                {
                    result.BlockReason = "Исторический или актуальный пакет повреждён, нечитаем, слишком большой или принадлежит другому устройству — очистка закрыта.";
                    return result;
                }
            }
        }

        foreach (var hash in reachableHashes)
        {
            try
            {
                result.ReachableKeys.Add(_keyHelper.GetBlobKey(hash));
            }
            catch
            {
                result.BlockReason = "Недопустимый SHA-256 во вложении пакета — очистка закрыта.";
                return result;
            }
        }

        result.Complete = true;
        return result;
    }

    private async Task<List<string>?> ListDevicePackageKeysAsync(Guid deviceId, CancellationToken ct)
    {
        string prefix = _keyHelper.GetDevicePackagesPrefix(deviceId);
        var listed = await ListAllAsync(prefix, delimiter: null, ct).ConfigureAwait(false);
        if (listed == null)
            return null;

        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var obj in listed)
        {
            if (!obj.Key.StartsWith(prefix, StringComparison.Ordinal))
                return null;

            if (!_keyHelper.TryParsePackageKey(obj.Key, out Guid pkgDevice, out _) || pkgDevice != deviceId)
                return null;

            if (!keys.Add(obj.Key))
                return null;
        }

        return keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
    }

    private async Task<bool> TryCollectHashesFromPackageAsync(
        string packageKey,
        Guid expectedDeviceId,
        Guid expectedPackageId,
        string encryptionPassword,
        HashSet<string> reachableHashes,
        CancellationToken ct)
    {
        try
        {
            var head = await _transport.HeadObjectAsync(packageKey, ct).ConfigureAwait(false);
            if (head == null)
                return false;
            if (head.ContentLength <= 0 || head.ContentLength > MaxReadablePackageBytes)
                return false;

            var packageObj = await _transport.GetObjectAsync(packageKey, ct).ConfigureAwait(false);
            if (packageObj?.Content == null || packageObj.Content.Length == 0)
                return false;
            if (packageObj.Content.Length > MaxReadablePackageBytes)
                return false;
            if (packageObj.Content.Length != head.ContentLength)
                return false;

            SyncPackageEnvelope? envelope;
            try
            {
                envelope = JsonSerializer.Deserialize<SyncPackageEnvelope>(packageObj.Content, JsonOptions);
            }
            catch
            {
                return false;
            }

            if (envelope == null || !string.Equals(envelope.Magic, "QNSP", StringComparison.Ordinal))
                return false;
            if (envelope.DeviceId != expectedDeviceId || envelope.PackageId != expectedPackageId)
                return false;

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
            byte[] plaintext = _crypto.DecryptPayload(
                ciphertext,
                salt,
                nonce,
                tag,
                encryptionPassword,
                envelope.GetAssociatedData(),
                reading.Descriptor.Iterations);

            SyncPackagePayload? payload;
            try
            {
                payload = JsonSerializer.Deserialize<SyncPackagePayload>(plaintext, JsonOptions);
            }
            finally
            {
                CryptographicOperationsZero(plaintext);
            }

            if (payload == null)
                return false;
            if (payload.PackageId != expectedPackageId || payload.SourceDeviceId != expectedDeviceId)
                return false;

            if (payload.Attachments == null)
                return true;

            foreach (var att in payload.Attachments)
            {
                if (att.Operation == SyncOperationType.Delete)
                    continue;
                if (string.IsNullOrWhiteSpace(att.Sha256) || att.Sha256.Length != 64)
                    continue;
                reachableHashes.Add(att.Sha256.Trim().ToLowerInvariant());
            }

            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    private static void CryptographicOperationsZero(byte[] data)
    {
        System.Security.Cryptography.CryptographicOperations.ZeroMemory(data);
    }

    private async Task<List<Guid>?> DiscoverDeviceIdsFromPagesAsync(CancellationToken ct)
    {
        var ids = new HashSet<Guid>();
        string devicesPrefix = _keyHelper.GetDevicesPrefix();
        string? token = null;
        int pages = 0;
        int objects = 0;

        do
        {
            ct.ThrowIfCancellationRequested();
            if (pages >= _maxPages || objects >= _maxObjects)
                return null;

            StorageListResult page;
            try
            {
                page = await _transport.ListObjectsV2Async(new StorageListRequest
                {
                    Prefix = devicesPrefix,
                    Delimiter = "/",
                    MaxKeys = _pageSize,
                    ContinuationToken = token
                }, ct).ConfigureAwait(false);
            }
            catch
            {
                return null;
            }

            pages++;
            if (page.CommonPrefixes != null)
            {
                foreach (var prefix in page.CommonPrefixes)
                {
                    objects++;
                    if (!_keyHelper.TryParseDevicePrefix(prefix, out Guid deviceId))
                        return null;
                    ids.Add(deviceId);
                    if (objects >= _maxObjects)
                        return null;
                }
            }

            if (page.Objects != null)
            {
                foreach (var obj in page.Objects)
                {
                    if (!obj.Key.StartsWith(devicesPrefix, StringComparison.Ordinal))
                        continue;
                    objects++;
                    if (_keyHelper.TryParsePointerKey(obj.Key, out Guid pointerDevice))
                    {
                        ids.Add(pointerDevice);
                    }
                    else if (_keyHelper.TryParsePackageKey(obj.Key, out Guid pkgDevice, out _))
                    {
                        ids.Add(pkgDevice);
                    }
                    else
                    {
                        return null;
                    }

                    if (objects >= _maxObjects)
                        return null;
                }
            }

            if (page.IsTruncated && !string.IsNullOrEmpty(page.NextContinuationToken))
            {
                if (pages >= _maxPages)
                    return null;
                token = page.NextContinuationToken;
            }
            else
            {
                token = null;
            }
        } while (!string.IsNullOrEmpty(token));

        return ids.ToList();
    }

    private async Task<List<StorageObjectSummary>?> ListAllAsync(string prefix, string? delimiter, CancellationToken ct)
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
                    Delimiter = delimiter,
                    MaxKeys = _pageSize,
                    ContinuationToken = token
                }, ct).ConfigureAwait(false);
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

    private static CloudCleanupItemResult Skip(string key, string detail)
        => new() { Key = key, Outcome = "skipped", Detail = detail };

    private static CloudCleanupItemResult Fail(string key, string detail)
        => new() { Key = key, Outcome = "failed", Detail = detail };
}
