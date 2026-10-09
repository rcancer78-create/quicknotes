using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using QuickNotes.App.Data;
using QuickNotes.App.Models.Sync;

namespace QuickNotes.App.Services.Sync;

/// <summary>
/// Autonomous sync engine for multi-device encrypted synchronization over object storage.
/// Coordinates safe pull-before-push sync cycles with:
/// - Single-flight gate (prevents concurrent sync cycles)
/// - Deterministic remote device pointer listing and processing
/// - Pull-before-push ordering
/// - ETag-based skipping of already-processed packages
/// - Strict pointer validation and namespace isolation (prohibits cross-device or escaping references)
/// - Envelope and content integrity validation via ISyncPackageImporter
/// - Durable conflict records without silent data overwrites
/// - Non-resurrection of tombstones across devices
/// - Change-detection: only creates/uploads new immutable packages when local content actually changed
/// - Crash/retry safe indeterminate upload recovery without package duplication
/// - Zero network/cloud disruption to local editing.
/// </summary>
public class SyncEngine : ISyncEngine, IDisposable
{
    private readonly ICloudObjectStoreTransport _transport;
    private readonly ISyncPackageExporter _exporter;
    private readonly ISyncPackageImporter _importer;
    private readonly IDeviceIdProvider _deviceIdProvider;
    private readonly SyncCloudSettings _settings;
    private readonly IS3CredentialsStorage _credentialsStorage;
    private readonly SyncObjectKeyHelper _keyHelper;
    private readonly IDateTimeProvider _dateTimeProvider;
    private readonly Func<QuickNotesDbContext>? _dbFactory;
    private readonly ISyncAttachmentBlobService? _blobService;
    private readonly SyncCloudExclusiveLock? _exclusiveLock;
    private readonly ISyncPasswordStorage? _passwordStorage;
    private readonly ILocalMutationCoordinator? _mutationCoordinator;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTime? _lastSyncTimeUtc;
    private bool? _lastSyncSuccess;
    private string? _lastError;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public SyncEngine(
        ICloudObjectStoreTransport transport,
        ISyncPackageExporter exporter,
        ISyncPackageImporter importer,
        IDeviceIdProvider deviceIdProvider,
        SyncCloudSettings settings,
        IS3CredentialsStorage credentialsStorage,
        SyncObjectKeyHelper? keyHelper = null,
        IDateTimeProvider? dateTimeProvider = null,
        Func<QuickNotesDbContext>? dbFactory = null,
        ISyncAttachmentBlobService? blobService = null,
        SyncCloudExclusiveLock? exclusiveLock = null,
        ISyncPasswordStorage? passwordStorage = null,
        ILocalMutationCoordinator? mutationCoordinator = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _exporter = exporter ?? throw new ArgumentNullException(nameof(exporter));
        _importer = importer ?? throw new ArgumentNullException(nameof(importer));
        _deviceIdProvider = deviceIdProvider ?? throw new ArgumentNullException(nameof(deviceIdProvider));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _credentialsStorage = credentialsStorage ?? throw new ArgumentNullException(nameof(credentialsStorage));
        _keyHelper = keyHelper ?? new SyncObjectKeyHelper(_settings);
        _dateTimeProvider = dateTimeProvider ?? new SystemDateTimeProvider();
        _dbFactory = dbFactory;
        _blobService = blobService;
        _exclusiveLock = exclusiveLock;
        _passwordStorage = passwordStorage;
        _mutationCoordinator = mutationCoordinator;
    }

    internal ILocalMutationCoordinator? MutationCoordinator => _mutationCoordinator;

    private bool _disposed;

    public async Task<SyncCycleResult> RunSyncCycleAsync(
        string encryptionPassword,
        SyncCycleOptions? options = null,
        IProgress<SyncProgressReport>? progress = null,
        CancellationToken ct = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(SyncEngine));
        if (_dbFactory == null)
        {
            throw new InvalidOperationException("DbFactory was not configured for this SyncEngine instance. Use the overload accepting QuickNotesDbContext.");
        }

        using var db = _dbFactory();
        return await RunSyncCycleAsync(db, encryptionPassword, options, progress, ct).ConfigureAwait(false);
    }

    public async Task<SyncCycleResult> RunSyncCycleAsync(
        QuickNotesDbContext db,
        string encryptionPassword,
        SyncCycleOptions? options = null,
        IProgress<SyncProgressReport>? progress = null,
        CancellationToken ct = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(SyncEngine));
        if (db == null) throw new ArgumentNullException(nameof(db));
        options ??= new SyncCycleOptions();

        var stopwatch = Stopwatch.StartNew();

        bool exclusiveHeld = false;
        if (_exclusiveLock != null)
        {
            exclusiveHeld = await _exclusiveLock.TryAcquireAsync(options.WaitIfBusy, ct).ConfigureAwait(false);
            if (!exclusiveHeld)
                return SyncCycleResult.Busy();
        }

        try
        {
            // 1. Single-flight Gate
            bool acquired;
            if (options.WaitIfBusy)
            {
                await _gate.WaitAsync(ct).ConfigureAwait(false);
                acquired = true;
            }
            else
            {
                acquired = await _gate.WaitAsync(0, ct).ConfigureAwait(false);
            }

            if (!acquired)
            {
                return SyncCycleResult.Busy();
            }

            try
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report(new SyncProgressReport(SyncProgressStage.Starting, "Запуск цикла синхронизации..."));

                // 2. Pre-flight Validation
                if (!_settings.Enabled)
                {
                    return Fail("Облачная синхронизация отключена в настройках.");
                }

                if (string.IsNullOrWhiteSpace(_settings.Bucket))
                {
                    return Fail("Имя бакета Yandex Object Storage не настроено.");
                }

                if (!_credentialsStorage.HasCredentials())
                {
                    var authErr = SyncCycleResult.AuthError("Ключи доступа S3 отсутствуют в защищённом хранилище.");
                    RecordOutcome(authErr);
                    return authErr;
                }

                if (string.IsNullOrEmpty(encryptionPassword))
                {
                    return Fail("Пароль сквозного шифрования не указан.");
                }

                Guid localDeviceId = _deviceIdProvider.GetDeviceId();
                if (localDeviceId == Guid.Empty)
                {
                    return Fail("Идентификатор локального устройства (DeviceId) не инициализирован.");
                }

                var generation = await SyncGenerationResolver.ResolveAndApplyAsync(_transport, _keyHelper, ct).ConfigureAwait(false);
                if (!generation.Success)
                {
                    return Fail(generation.Error ?? "Указатель поколения синхронизации недоступен.");
                }

                if (_passwordStorage != null)
                {
                    encryptionPassword = await SyncPendingPasswordPromoter.TryPromoteAsync(
                        _passwordStorage, _transport, _keyHelper, new SyncCryptoService(), encryptionPassword, ct)
                        .ConfigureAwait(false);
                }

                var cycleResult = new SyncCycleResult
                {
                    Success = true
                };

                // -------------------------------------------------------------------------------------
                // Phase 1: PULL (unless PushOnly)
                // -------------------------------------------------------------------------------------
                if (!options.PushOnly)
                {
                    ct.ThrowIfCancellationRequested();

                    long kdfBudgetLimit = options.UntrustedInboundKdfCycleBudgetIterations > 0
                        ? options.UntrustedInboundKdfCycleBudgetIterations
                        : UntrustedInboundKdfWorkBudget.DefaultCycleBudgetIterations;
                    var inboundKdfBudget = new UntrustedInboundKdfWorkBudget(kdfBudgetLimit);

                    // Register any uncommitted local edits before pulling to prevent concurrent edits
                    // from being silently overwritten
                    SyncSnapshotHelper.RegisterLocalModifications(db, localDeviceId, _dateTimeProvider);

                    progress?.Report(new SyncProgressReport(SyncProgressStage.ListingRemotePointers, "Получение указателей удалённых устройств..."));

                    string devicesPrefix = _keyHelper.GetDevicesPrefix();
                    var discoveredDeviceIds = new List<Guid>();
                    string? continuationToken = null;
                    int pageCount = 0;
                    bool isTruncated = false;
                    int maxDevices = options.MaxRemoteDevices > 0 ? options.MaxRemoteDevices : 100;
                    int maxPages = options.MaxListPages > 0 ? options.MaxListPages : 20;

                    do
                    {
                        pageCount++;
                        StorageListResult listResult;
                        try
                        {
                            listResult = await _transport.ListObjectsV2Async(new StorageListRequest
                            {
                                Prefix = devicesPrefix,
                                Delimiter = "/",
                                ContinuationToken = continuationToken,
                                MaxKeys = Math.Clamp(maxDevices, 1, 1000)
                            }, ct).ConfigureAwait(false);
                        }
                        catch (CloudOfflineException ex)
                        {
                            var off = SyncCycleResult.Offline(ex.Message);
                            RecordOutcome(off);
                            return off;
                        }
                        catch (CloudAuthException ex)
                        {
                            var auth = SyncCycleResult.AuthError(ex.Message);
                            RecordOutcome(auth);
                            return auth;
                        }
                        catch (CloudQuotaException ex)
                        {
                            var quota = SyncCycleResult.QuotaExceeded(ex.Message);
                            RecordOutcome(quota);
                            return quota;
                        }

                        if (listResult.CommonPrefixes != null)
                        {
                            foreach (var prefix in listResult.CommonPrefixes)
                            {
                                if (_keyHelper.TryParseDevicePrefix(prefix, out var devId))
                                {
                                    if (devId != localDeviceId && !discoveredDeviceIds.Contains(devId))
                                    {
                                        if (discoveredDeviceIds.Count < maxDevices)
                                        {
                                            discoveredDeviceIds.Add(devId);
                                        }
                                        else
                                        {
                                            isTruncated = true;
                                            break;
                                        }
                                    }
                                }
                            }
                        }

                        if (discoveredDeviceIds.Count >= maxDevices)
                        {
                            if (listResult.IsTruncated || (listResult.CommonPrefixes != null && listResult.CommonPrefixes.Count > maxDevices))
                            {
                                isTruncated = true;
                            }
                            break;
                        }

                        if (listResult.IsTruncated && !string.IsNullOrEmpty(listResult.NextContinuationToken))
                        {
                            if (pageCount >= maxPages)
                            {
                                isTruncated = true;
                                break;
                            }
                            continuationToken = listResult.NextContinuationToken;
                        }
                        else
                        {
                            continuationToken = null;
                        }
                    } while (!string.IsNullOrEmpty(continuationToken));

                    cycleResult.IsTruncated = isTruncated;
                    if (isTruncated)
                    {
                        cycleResult.Diagnostics.Add($"Обнаружение устройств усечено: достигнут лимит ({discoveredDeviceIds.Count} устройств или {pageCount} страниц).");
                    }

                    // Deterministic order by Guid string
                    discoveredDeviceIds.Sort((a, b) => string.CompareOrdinal(a.ToString("D"), b.ToString("D")));

                    cycleResult.RemoteDevicesExamined = discoveredDeviceIds.Count;

                    var localDeviceStates = await db.SyncDeviceStates
                        .ToDictionaryAsync(s => s.DeviceId, ct)
                        .ConfigureAwait(false);

                    for (int i = 0; i < discoveredDeviceIds.Count; i++)
                    {
                        ct.ThrowIfCancellationRequested();
                        var remoteDeviceId = discoveredDeviceIds[i];

                        progress?.Report(new SyncProgressReport(
                            SyncProgressStage.PullingRemotePackages,
                            $"Обработка указателя устройства {i + 1} из {discoveredDeviceIds.Count}...",
                            i + 1,
                            discoveredDeviceIds.Count));

                        string pointerKey = _keyHelper.GetDevicePointerKey(remoteDeviceId);
                        localDeviceStates.TryGetValue(remoteDeviceId, out var deviceState);

                        // Check pointer metadata via HeadObjectAsync
                        StorageObjectMetadata? pointerMeta = null;
                        try
                        {
                            pointerMeta = await _transport.HeadObjectAsync(pointerKey, ct).ConfigureAwait(false);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            cycleResult.Diagnostics.Add($"Не удалось проверить метаданные указателя '{pointerKey}': {CloudErrorSanitizer.SanitizeDiagnosticDetail(ex.Message)}");
                        }

                        if (pointerMeta == null)
                        {
                            cycleResult.Diagnostics.Add($"Устройство {remoteDeviceId}: указатель '{pointerKey}' отсутствует, пропуск.");
                            continue;
                        }

                        string normalizedPointerETag = StorageObjectMetadata.NormalizeETag(pointerMeta.ETag);

                        // Skip if pointer ETag hasn't changed since last successful processing
                        if (deviceState != null &&
                            !string.IsNullOrEmpty(deviceState.LatestProcessedETag) &&
                            string.Equals(deviceState.LatestProcessedETag, normalizedPointerETag, StringComparison.Ordinal))
                        {
                            cycleResult.Diagnostics.Add($"Устройство {remoteDeviceId}: указатель не изменился (ETag: {normalizedPointerETag}), пропуск.");
                            continue;
                        }

                        // Download and validate pointer manifest
                        StorageObjectResult? pointerObj;
                        try
                        {
                            pointerObj = await _transport.GetObjectAsync(pointerKey, ct).ConfigureAwait(false);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            cycleResult.Diagnostics.Add($"Не удалось прочитать указатель '{pointerKey}': {CloudErrorSanitizer.SanitizeDiagnosticDetail(ex.Message)}");
                            continue;
                        }

                        if (pointerObj == null || pointerObj.Content.Length == 0)
                        {
                            cycleResult.Diagnostics.Add($"Указатель '{pointerKey}' пуст или отсутствует.");
                            continue;
                        }

                        DevicePointerPayload pointer;
                        try
                        {
                            pointer = SyncPointerValidator.ValidateAndParse(pointerObj.Content, pointerKey, _keyHelper);
                        }
                        catch (SyncValidationException valEx)
                        {
                            cycleResult.Errors.Add($"Ошибка валидации указателя '{pointerKey}': {CloudErrorSanitizer.SanitizeDiagnosticDetail(valEx.Message)}");
                            cycleResult.Diagnostics.Add($"Указатель '{pointerKey}' отклонён: {CloudErrorSanitizer.SanitizeDiagnosticDetail(valEx.Message)}");
                            continue;
                        }

                        // Check if package already processed
                        if (deviceState != null &&
                            deviceState.LatestProcessedPackageId == pointer.LatestPackageId)
                        {
                            deviceState.LatestProcessedETag = normalizedPointerETag;
                            deviceState.LastSyncedAtUtc = _dateTimeProvider.UtcNow;
                            await db.SaveChangesAsync(ct).ConfigureAwait(false);
                            cycleResult.Diagnostics.Add($"Устройство {remoteDeviceId}: пакет {pointer.LatestPackageId} уже обработан ранее, обновлён чекпоинт.");
                            continue;
                        }

                        // Resolve package key
                        string packageKey = pointer.LatestPackageKey ?? _keyHelper.GetPackageKey(pointer.DeviceId, pointer.LatestPackageId);

                        // Check size bounds
                        try
                        {
                            var headMeta = await _transport.HeadObjectAsync(packageKey, ct).ConfigureAwait(false);
                            if (headMeta != null && headMeta.ContentLength > options.MaxPackageSizeBytes)
                            {
                                cycleResult.Diagnostics.Add($"Пакет '{packageKey}' ({headMeta.ContentLength} байт) превышает максимальный размер {options.MaxPackageSizeBytes} байт. Пропуск.");
                                continue;
                            }
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            cycleResult.Diagnostics.Add($"Не удалось проверить метаданные пакета '{packageKey}': {CloudErrorSanitizer.SanitizeDiagnosticDetail(ex.Message)}");
                        }

                        // Download package payload
                        StorageObjectResult? packageObj;
                        try
                        {
                            packageObj = await _transport.GetObjectAsync(packageKey, ct).ConfigureAwait(false);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            cycleResult.Errors.Add($"Не удалось загрузить пакет '{packageKey}': {CloudErrorSanitizer.SanitizeDiagnosticDetail(ex.Message)}");
                            continue;
                        }

                        if (packageObj == null || packageObj.Content == null || packageObj.Content.Length == 0)
                        {
                            cycleResult.Errors.Add($"Пакет '{packageKey}' пуст или не найден в хранилище.");
                            continue;
                        }

                        // 1. Check package digest directly on raw bytes WITHOUT UTF-8 roundtrip
                        string actualDigest = SyncFingerprintHelper.ComputeSha256(packageObj.Content);
                        if (!string.IsNullOrWhiteSpace(pointer.PackageDigest))
                        {
                            if (!string.Equals(pointer.PackageDigest, actualDigest, StringComparison.OrdinalIgnoreCase))
                            {
                                cycleResult.Errors.Add($"Нарушена целостность пакета '{packageKey}': дайджест ({actualDigest}) не совпадает с указателем ({pointer.PackageDigest}).");
                                continue;
                            }
                        }

                        // 2. Parse and validate outer envelope before decryption/import
                        SyncPackageEnvelope? envelope;
                        try
                        {
                            envelope = JsonSerializer.Deserialize<SyncPackageEnvelope>(packageObj.Content, JsonOptions);
                        }
                        catch (Exception ex)
                        {
                            cycleResult.Errors.Add($"Повреждение пакета '{packageKey}': невалидный JSON envelope ({CloudErrorSanitizer.SanitizeDiagnosticDetail(ex.Message)}).");
                            continue;
                        }

                        if (envelope == null)
                        {
                            cycleResult.Errors.Add($"Повреждение пакета '{packageKey}': пустой envelope.");
                            continue;
                        }

                        if (envelope.PackageId != pointer.LatestPackageId)
                        {
                            cycleResult.Errors.Add($"Несоответствие пакета '{packageKey}': PackageId в envelope ({envelope.PackageId}) не совпадает с LatestPackageId указателя ({pointer.LatestPackageId}).");
                            continue;
                        }

                        if (envelope.DeviceId != pointer.DeviceId || envelope.DeviceId != remoteDeviceId)
                        {
                            cycleResult.Errors.Add($"Несоответствие пакета '{packageKey}': DeviceId в envelope ({envelope.DeviceId}) не совпадает с пространством устройства ({pointer.DeviceId}).");
                            continue;
                        }

                        // 3. Decrypt and apply package atomically
                        progress?.Report(new SyncProgressReport(
                            SyncProgressStage.ApplyingRemoteChanges,
                            $"Применение изменений пакета {pointer.LatestPackageId}..."));

                        string packageJson = Encoding.UTF8.GetString(packageObj.Content);
                        int prefetchedBlobs = 0;
                        Func<SyncPackagePayload, Task>? beforeApply = null;
                        if (_blobService != null && _settings.SyncAttachments)
                        {
                            ISyncAttachmentBlobService blobService = _blobService;
                            string inboundPassword = encryptionPassword;
                            beforeApply = async payload =>
                            {
                                var (downloaded, _) = await blobService.PrefetchUntrustedInboundBlobsBeforeApplyAsync(
                                    payload.Attachments,
                                    inboundPassword,
                                    inboundKdfBudget,
                                    ct).ConfigureAwait(false);
                                prefetchedBlobs = downloaded;
                            };
                        }

                        SyncImportResult importResult;
                        if (_mutationCoordinator != null)
                        {
                            importResult = await _mutationCoordinator.ExecuteSyncApplyAsync(
                                () => _importer.ImportPackageAsync(db, packageJson, encryptionPassword, beforeApply, inboundKdfBudget),
                                ct).ConfigureAwait(false);
                        }
                        else
                        {
                            importResult = await _importer.ImportPackageAsync(db, packageJson, encryptionPassword, beforeApply, inboundKdfBudget).ConfigureAwait(false);
                        }

                        if (!importResult.Success)
                        {
                            string err = importResult.Errors.Count > 0 ? importResult.Errors[0] : "Неизвестная ошибка импорта пакета.";
                            cycleResult.Errors.Add($"Сбой импорта пакета '{packageKey}': {err}");
                            // Do NOT update checkpoint for failed device; other devices continue safely
                            continue;
                        }

                        cycleResult.BlobsDownloaded += prefetchedBlobs;

                        // Download blobs before advancing the remote checkpoint. Metadata may already
                        // be applied (importer saves locally); a blob failure must not skip retries.
                        bool blobsReady = true;
                        if (_blobService != null && _settings.SyncAttachments)
                        {
                            try
                            {
                                var attachmentsToDownload = await db.NoteAttachments
                                    .Where(a => a.Sha256 != null && a.Sha256 != string.Empty)
                                    .ToListAsync(ct)
                                    .ConfigureAwait(false);

                                var (dlDownloaded, dlSkipped, dlAlready, dlErrors, dlDiag) =
                                    await _blobService.DownloadMissingBlobsAsync(attachmentsToDownload, encryptionPassword, ct)
                                        .ConfigureAwait(false);

                                cycleResult.BlobsDownloaded += dlDownloaded;
                                cycleResult.BlobsSkipped += dlSkipped + dlAlready;
                                cycleResult.BlobErrors += dlErrors;
                                foreach (var d in dlDiag) cycleResult.Diagnostics.Add(d);

                                await db.SaveChangesAsync(ct).ConfigureAwait(false);
                                if (dlErrors > 0)
                                    blobsReady = false;
                            }
                            catch (OperationCanceledException) { throw; }
                            catch (SyncKdfWorkBudgetExceededException) { throw; }
                            catch (Exception blobEx)
                            {
                                cycleResult.Diagnostics.Add($"Ошибка скачивания blob при обработке пакета '{packageKey}': {CloudErrorSanitizer.SanitizeDiagnosticDetail(blobEx.Message)}");
                                cycleResult.BlobErrors++;
                                blobsReady = false;
                            }
                        }

                        if (!blobsReady)
                        {
                            cycleResult.Diagnostics.Add($"Пакет '{packageKey}' применён локально, но checkpoint не продвинут из-за ошибки вложений.");
                            continue;
                        }

                        // Update device state checkpoint only after blobs succeeded (or attachments disabled)
                        if (deviceState == null)
                        {
                            deviceState = new SyncDeviceState
                            {
                                DeviceId = pointer.DeviceId,
                                LatestProcessedPackageId = pointer.LatestPackageId,
                                LatestProcessedETag = normalizedPointerETag,
                                LastSyncedAtUtc = _dateTimeProvider.UtcNow,
                                PackageCount = pointer.PackageCount
                            };
                            db.SyncDeviceStates.Add(deviceState);
                            localDeviceStates[pointer.DeviceId] = deviceState;
                        }
                        else
                        {
                            deviceState.LatestProcessedPackageId = pointer.LatestPackageId;
                            deviceState.LatestProcessedETag = normalizedPointerETag;
                            deviceState.LastSyncedAtUtc = _dateTimeProvider.UtcNow;
                            deviceState.PackageCount = pointer.PackageCount;
                        }

                        await db.SaveChangesAsync(ct).ConfigureAwait(false);

                        // Accumulate counts
                        cycleResult.RemotePackagesPulled++;
                        cycleResult.RemoteEntitiesApplied.Created += importResult.TotalCreated;
                        cycleResult.RemoteEntitiesApplied.Updated += importResult.TotalUpdated;
                        cycleResult.RemoteEntitiesApplied.Deleted += importResult.TotalDeleted;
                        cycleResult.RemoteEntitiesSkipped += importResult.TotalSkipped;
                        cycleResult.ConflictsDetected += importResult.Conflicts.Count;
                    }
                }

                // -------------------------------------------------------------------------------------
                // Phase 2: PUSH (unless PullOnly)
                // -------------------------------------------------------------------------------------
                if (!options.PullOnly)
                {
                    ct.ThrowIfCancellationRequested();
                    progress?.Report(new SyncProgressReport(SyncProgressStage.CheckingLocalChanges, "Проверка локальных изменений..."));

                    var localState = await db.SyncLocalStates
                        .FirstOrDefaultAsync(s => s.DeviceId == localDeviceId, ct)
                        .ConfigureAwait(false);

                    if (localState == null)
                    {
                        localState = new SyncLocalState
                        {
                            DeviceId = localDeviceId
                        };
                        db.SyncLocalStates.Add(localState);
                        await db.SaveChangesAsync(ct).ConfigureAwait(false);
                    }

                    // 2.1 Indeterminate upload recovery (crash safety)
                    if (localState.PendingPackageId.HasValue && localState.PendingPackageId.Value != Guid.Empty)
                    {
                        Guid pendingPkgId = localState.PendingPackageId.Value;
                        string pendingPkgKey = localState.PendingPackageKey ?? _keyHelper.GetPackageKey(localDeviceId, pendingPkgId);

                        cycleResult.Diagnostics.Add($"Обнаружена незавершённая отправка пакета {pendingPkgId}. Выполняется восстановление...");

                        if (!await TryUploadLocalAttachmentBlobsAsync(db, encryptionPassword, cycleResult, ct).ConfigureAwait(false))
                        {
                            return FailWithBlobStats(cycleResult, "Не удалось выгрузить вложения до публикации пакета. Повторите синхронизацию.");
                        }

                        // Check if package object already exists in the object store
                        StorageObjectResult? existingPkgObj = null;
                        try
                        {
                            existingPkgObj = await _transport.GetObjectAsync(pendingPkgKey, ct).ConfigureAwait(false);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            cycleResult.Diagnostics.Add($"GetObjectAsync для ожидающего пакета: {CloudErrorSanitizer.SanitizeDiagnosticDetail(ex.Message)}");
                        }

                        byte[] payloadBytesToCommit;

                        if (existingPkgObj == null)
                        {
                            // Package was not uploaded before crash; MUST re-send exact saved bytes without re-exporting DB!
                            if (localState.PendingPayloadBytes == null || localState.PendingPayloadBytes.Length == 0)
                            {
                                return Fail($"Сбой восстановления: сохранённый payload ожидающего пакета {pendingPkgId} отсутствует в локальном состоянии. Повторный экспорт запрещён во избежание рассинхронизации.");
                            }

                            if (localState.PendingPayloadBytes.Length > options.MaxPackageSizeBytes)
                            {
                                return Fail($"Сбой восстановления: размер сохранённого payload ({localState.PendingPayloadBytes.Length} байт) превышает лимит {options.MaxPackageSizeBytes} байт.");
                            }

                            // Verify digest of saved bytes
                            string savedDigest = SyncFingerprintHelper.ComputeSha256(localState.PendingPayloadBytes);
                            if (!string.IsNullOrEmpty(localState.PendingPackageDigest) &&
                                !string.Equals(localState.PendingPackageDigest, savedDigest, StringComparison.OrdinalIgnoreCase))
                            {
                                return Fail($"Сбой восстановления: контрольная сумма сохранённого payload не совпадает с PendingPackageDigest ({savedDigest} != {localState.PendingPackageDigest}). Обнаружено повреждение данных.");
                            }

                            // Validate envelope
                            SyncPackageEnvelope? savedEnv;
                            try
                            {
                                savedEnv = JsonSerializer.Deserialize<SyncPackageEnvelope>(localState.PendingPayloadBytes, JsonOptions);
                            }
                            catch (Exception ex)
                            {
                                return Fail($"Сбой восстановления: сохранённый payload содержит невалидный JSON envelope ({ex.Message}).");
                            }

                            if (savedEnv == null || savedEnv.PackageId != pendingPkgId || savedEnv.DeviceId != localDeviceId)
                            {
                                return Fail($"Сбой восстановления: сохранённый envelope содержит PackageId ({savedEnv?.PackageId}) или DeviceId ({savedEnv?.DeviceId}), не совпадающие с ожидаемым состоянием ({pendingPkgId}, {localDeviceId}).");
                            }

                            payloadBytesToCommit = localState.PendingPayloadBytes;

                            // Upload exact saved bytes
                            await _transport.PutImmutableObjectAsync(pendingPkgKey, payloadBytesToCommit, "application/json", ct).ConfigureAwait(false);
                        }
                        else
                        {
                            // Remote object already exists in storage!
                            // Verify size, SHA-256 digest on raw bytes, and envelope PackageId/DeviceId
                            if (existingPkgObj.Content == null || existingPkgObj.Content.Length == 0)
                            {
                                return Fail($"Сбой восстановления: удалённый объект '{pendingPkgKey}' пуст.");
                            }

                            if (existingPkgObj.Content.Length > options.MaxPackageSizeBytes)
                            {
                                return Fail($"Сбой восстановления: размер удалённого объекта '{pendingPkgKey}' ({existingPkgObj.Content.Length} байт) превышает лимит {options.MaxPackageSizeBytes} байт.");
                            }

                            if (localState.PendingPayloadBytes != null &&
                                existingPkgObj.Content.Length != localState.PendingPayloadBytes.Length)
                            {
                                return Fail($"Конфликт/повреждение: размер удалённого объекта '{pendingPkgKey}' ({existingPkgObj.Content.Length} байт) не совпадает с локальным сохранённым payload ({localState.PendingPayloadBytes.Length} байт).");
                            }

                            string remoteDigest = SyncFingerprintHelper.ComputeSha256(existingPkgObj.Content);
                            if (!string.IsNullOrEmpty(localState.PendingPackageDigest) &&
                                !string.Equals(localState.PendingPackageDigest, remoteDigest, StringComparison.OrdinalIgnoreCase))
                            {
                                return Fail($"Конфликт/повреждение: дайджест удалённого объекта '{pendingPkgKey}' ({remoteDigest}) не совпадает с PendingPackageDigest ({localState.PendingPackageDigest}).");
                            }

                            if (localState.PendingPayloadBytes != null &&
                                !existingPkgObj.Content.AsSpan().SequenceEqual(localState.PendingPayloadBytes))
                            {
                                return Fail($"Конфликт/повреждение: байты удалённого объекта '{pendingPkgKey}' не совпадают с локальным сохранённым payload.");
                            }

                            // Deserialize and validate envelope
                            SyncPackageEnvelope? remoteEnv;
                            try
                            {
                                remoteEnv = JsonSerializer.Deserialize<SyncPackageEnvelope>(existingPkgObj.Content, JsonOptions);
                            }
                            catch (Exception ex)
                            {
                                return Fail($"Сбой восстановления: удалённый объект '{pendingPkgKey}' содержит невалидный JSON envelope ({ex.Message}).");
                            }

                            if (remoteEnv == null || remoteEnv.PackageId != pendingPkgId || remoteEnv.DeviceId != localDeviceId)
                            {
                                return Fail($"Конфликт/повреждение: удалённый объект '{pendingPkgKey}' содержит PackageId ({remoteEnv?.PackageId}) или DeviceId ({remoteEnv?.DeviceId}), не совпадающие с ожидаемым состоянием ({pendingPkgId}, {localDeviceId}).");
                            }

                            payloadBytesToCommit = existingPkgObj.Content;
                        }

                        // Conditionally update pointer for recovered package
                        string pointerKey = _keyHelper.GetDevicePointerKey(localDeviceId);
                        var currentPointerObj = await _transport.GetObjectAsync(pointerKey, ct).ConfigureAwait(false);

                        string? expectedETag = currentPointerObj?.Metadata.ETag;
                        string pointerETag;
                        int prevCount = 0;

                        if (currentPointerObj != null && currentPointerObj.Content.Length > 0)
                        {
                            try
                            {
                                var prevPtr = JsonSerializer.Deserialize<DevicePointerPayload>(currentPointerObj.Content, JsonOptions);
                                if (prevPtr != null)
                                {
                                    prevCount = prevPtr.PackageCount;
                                    if (prevPtr.LatestPackageId == pendingPkgId)
                                    {
                                        // Pointer was already updated in the cloud before the crash!
                                        pointerETag = StorageObjectMetadata.NormalizeETag(currentPointerObj.Metadata.ETag);
                                        goto CommitRecoveryCheckpoint;
                                    }
                                }
                            }
                            catch { }
                        }

                        var newPointer = new DevicePointerPayload
                        {
                            FormatVersion = 1,
                            DeviceId = localDeviceId,
                            LatestPackageId = pendingPkgId,
                            LatestPackageKey = pendingPkgKey,
                            PackageDigest = localState.PendingPackageDigest ?? SyncFingerprintHelper.ComputeSha256(payloadBytesToCommit),
                            CreatedAtUtc = localState.PendingCreatedAtUtc ?? _dateTimeProvider.UtcNow,
                            UpdatedAtUtc = _dateTimeProvider.UtcNow,
                            PackageCount = prevCount + 1
                        };

                        byte[] pointerBytes = JsonSerializer.SerializeToUtf8Bytes(newPointer);
                        var pointerMeta = await _transport.PutConditionalPointerAsync(pointerKey, pointerBytes, expectedETag, "application/json", ct).ConfigureAwait(false);
                        pointerETag = StorageObjectMetadata.NormalizeETag(pointerMeta.ETag);

                    CommitRecoveryCheckpoint:
                        localState.LastUploadedPackageId = pendingPkgId;
                        localState.LatestUploadedPackageKey = pendingPkgKey;
                        localState.LastUploadedETag = pointerETag;
                        localState.LastUploadedSnapshotHash = localState.PendingContentHash;
                        localState.LastUploadCompletedAtUtc = _dateTimeProvider.UtcNow;
                        localState.PendingPackageId = null;
                        localState.PendingPackageKey = null;
                        localState.PendingPackageDigest = null;
                        localState.PendingContentHash = null;
                        localState.PendingCreatedAtUtc = null;
                        localState.PendingPayloadBytes = null;
                        await db.SaveChangesAsync(ct).ConfigureAwait(false);

                        cycleResult.PackageUploaded = true;
                        cycleResult.UploadedPackageId = pendingPkgId;
                    }
                    else
                    {
                        // 2.2 Check if local changes exist
                        bool hasPendingMods = SyncSnapshotHelper.HasPendingLocalModifications(db, localDeviceId);
                        string currentVector = SyncSnapshotHelper.ComputeLocalRevisionVector(db, localDeviceId);
                        string lastHash = localState.LastUploadedSnapshotHash ?? string.Empty;
                        bool vectorChanged = !string.Equals(currentVector, lastHash, StringComparison.Ordinal);

                        if (!hasPendingMods && (!vectorChanged || string.IsNullOrEmpty(currentVector)))
                        {
                            // No local modifications -> Push is a clean no-op!
                            cycleResult.NoOp = true;
                            cycleResult.Diagnostics.Add("Локальные данные не изменялись с момента последней выгрузки. Повторный цикл не создаёт новые объекты.");
                        }
                        else
                        {
                            // 2.3 Local changes present: export and upload exactly one new immutable package
                            Guid newPackageId = Guid.NewGuid();
                            string packageKey = _keyHelper.GetPackageKey(localDeviceId, newPackageId);

                            progress?.Report(new SyncProgressReport(SyncProgressStage.UploadingLocalPackage, "Экспорт и отправка нового пакета изменений..."));

                            var exportResult = await _exporter.ExportPackageAsync(db, encryptionPassword, localDeviceId, newPackageId).ConfigureAwait(false);
                            if (!exportResult.Success || string.IsNullOrEmpty(exportResult.PackageJson))
                            {
                                string err = exportResult.Errors.Count > 0 ? exportResult.Errors[0] : "Ошибка экспорта пакета синхронизации.";
                                return Fail(err);
                            }

                            byte[] packageBytes = Encoding.UTF8.GetBytes(exportResult.PackageJson);
                            if (packageBytes.Length > options.MaxPackageSizeBytes)
                            {
                                return Fail($"Размер сформированного пакета ({packageBytes.Length} байт) превышает максимальный лимит {options.MaxPackageSizeBytes} байт.");
                            }

                            string packageDigest = SyncFingerprintHelper.ComputeSha256(packageBytes);
                            string newVector = SyncSnapshotHelper.ComputeLocalRevisionVector(db, localDeviceId);

                            // Save pending upload state to DB before network write
                            localState.PendingPackageId = newPackageId;
                            localState.PendingPackageKey = packageKey;
                            localState.PendingPackageDigest = packageDigest;
                            localState.PendingContentHash = newVector;
                            localState.PendingCreatedAtUtc = _dateTimeProvider.UtcNow;
                            localState.PendingPayloadBytes = packageBytes;
                            await db.SaveChangesAsync(ct).ConfigureAwait(false);

                            if (!await TryUploadLocalAttachmentBlobsAsync(db, encryptionPassword, cycleResult, ct).ConfigureAwait(false))
                            {
                                return FailWithBlobStats(cycleResult, "Не удалось выгрузить вложения до публикации пакета. Метаданные не опубликованы. Повторите синхронизацию.");
                            }

                            // Put immutable package object
                            await _transport.PutImmutableObjectAsync(packageKey, packageBytes, "application/json", ct).ConfigureAwait(false);

                            // Update device pointer conditionally
                            progress?.Report(new SyncProgressReport(SyncProgressStage.UpdatingDevicePointer, "Обновление указателя устройства..."));

                            string pointerKey = _keyHelper.GetDevicePointerKey(localDeviceId);
                            var currentPointerObj = await _transport.GetObjectAsync(pointerKey, ct).ConfigureAwait(false);

                            string? expectedETag = currentPointerObj?.Metadata.ETag;
                            int prevCount = 0;
                            if (currentPointerObj != null && currentPointerObj.Content.Length > 0)
                            {
                                try
                                {
                                    var prevPtr = JsonSerializer.Deserialize<DevicePointerPayload>(currentPointerObj.Content, JsonOptions);
                                    if (prevPtr != null) prevCount = prevPtr.PackageCount;
                                }
                                catch { }
                            }

                            var newPointer = new DevicePointerPayload
                            {
                                FormatVersion = 1,
                                DeviceId = localDeviceId,
                                LatestPackageId = newPackageId,
                                LatestPackageKey = packageKey,
                                PackageDigest = packageDigest,
                                CreatedAtUtc = _dateTimeProvider.UtcNow,
                                UpdatedAtUtc = _dateTimeProvider.UtcNow,
                                PackageCount = prevCount + 1
                            };

                            byte[] pointerBytes = JsonSerializer.SerializeToUtf8Bytes(newPointer);
                            var pointerMeta = await _transport.PutConditionalPointerAsync(pointerKey, pointerBytes, expectedETag, "application/json", ct).ConfigureAwait(false);
                            string pointerETag = StorageObjectMetadata.NormalizeETag(pointerMeta.ETag);

                            // Commit checkpoint
                            localState.LastUploadedPackageId = newPackageId;
                            localState.LatestUploadedPackageKey = packageKey;
                            localState.LastUploadedETag = pointerETag;
                            localState.LastUploadedSnapshotHash = newVector;
                            localState.LastUploadCompletedAtUtc = _dateTimeProvider.UtcNow;
                            localState.PendingPackageId = null;
                            localState.PendingPackageKey = null;
                            localState.PendingPackageDigest = null;
                            localState.PendingContentHash = null;
                            localState.PendingCreatedAtUtc = null;
                            localState.PendingPayloadBytes = null;
                            await db.SaveChangesAsync(ct).ConfigureAwait(false);

                            cycleResult.PackageUploaded = true;
                            cycleResult.UploadedPackageId = newPackageId;
                        }
                    }
                }

                stopwatch.Stop();
                cycleResult.Duration = stopwatch.Elapsed;
                progress?.Report(new SyncProgressReport(SyncProgressStage.Completed, "Синхронизация успешно завершена."));

                RecordOutcome(cycleResult);
                return cycleResult;
            }
            catch (CloudOfflineException ex)
            {
                var res = SyncCycleResult.Offline(ex.Message);
                res.Duration = stopwatch.Elapsed;
                RecordOutcome(res);
                return res;
            }
            catch (CloudAuthException ex)
            {
                var res = SyncCycleResult.AuthError(ex.Message);
                res.Duration = stopwatch.Elapsed;
                RecordOutcome(res);
                return res;
            }
            catch (CloudQuotaException ex)
            {
                var res = SyncCycleResult.QuotaExceeded(ex.Message);
                res.Duration = stopwatch.Elapsed;
                RecordOutcome(res);
                return res;
            }
            catch (CloudConflictException ex)
            {
                var res = SyncCycleResult.EtagConflict($"Конфликт ETag указателя: {ex.Message}");
                res.Duration = stopwatch.Elapsed;
                RecordOutcome(res);
                return res;
            }
            catch (SyncKdfWorkBudgetExceededException ex)
            {
                var res = SyncCycleResult.KdfWorkBudgetExceeded(ex.Message);
                res.Duration = stopwatch.Elapsed;
                RecordOutcome(res);
                return res;
            }
            catch (OperationCanceledException)
            {
                progress?.Report(new SyncProgressReport(SyncProgressStage.Failed, "Синхронизация отменена."));
                throw;
            }
            catch (Exception ex)
            {
                ErrorLogService.Write("SyncEngine.RunSyncCycle", ex);
                var res = SyncCycleResult.Failure($"Сбой цикла синхронизации: {CloudErrorSanitizer.SanitizeDiagnosticDetail(ex.Message)}");
                res.Duration = stopwatch.Elapsed;
                RecordOutcome(res);
                return res;
            }
            finally
            {
                _gate.Release();
            }
        }
        finally
        {
            if (exclusiveHeld)
                _exclusiveLock!.Release();
        }
    }

    public async Task<SyncEngineStatus> GetStatusAsync(QuickNotesDbContext? db = null, CancellationToken ct = default)
    {
        bool disposeDb = false;
        if (db == null)
        {
            if (_dbFactory != null)
            {
                db = _dbFactory();
                disposeDb = true;
            }
        }

        try
        {
            int pendingConflicts = 0;
            Guid? lastUploadedPkg = null;

            if (db != null)
            {
                pendingConflicts = await db.SyncConflicts
                    .CountAsync(c => !c.IsResolved, ct)
                    .ConfigureAwait(false);

                Guid localDeviceId = _deviceIdProvider.GetDeviceId();
                var localState = await db.SyncLocalStates
                    .AsNoTracking()
                    .FirstOrDefaultAsync(s => s.DeviceId == localDeviceId, ct)
                    .ConfigureAwait(false);

                lastUploadedPkg = localState?.LastUploadedPackageId;
            }

            return new SyncEngineStatus
            {
                IsConfigured = _settings.Enabled && !string.IsNullOrWhiteSpace(_settings.Bucket) && _credentialsStorage.HasCredentials(),
                IsRunning = _gate.CurrentCount == 0,
                LastSyncTimeUtc = _lastSyncTimeUtc,
                LastSyncSuccess = _lastSyncSuccess,
                LastError = _lastError,
                PendingConflictsCount = pendingConflicts,
                LastUploadedPackageId = lastUploadedPkg
            };
        }
        finally
        {
            if (disposeDb && db != null)
            {
                db.Dispose();
            }
        }
    }

    private async Task<bool> TryUploadLocalAttachmentBlobsAsync(
        QuickNotesDbContext db,
        string encryptionPassword,
        SyncCycleResult cycleResult,
        CancellationToken ct)
    {
        if (_blobService == null || !_settings.SyncAttachments)
            return true;

        try
        {
            var allAttachments = await db.NoteAttachments
                .Where(a => a.Sha256 != null && a.Sha256 != string.Empty)
                .ToListAsync(ct)
                .ConfigureAwait(false);

            var (ulUploaded, ulSkipped, ulExisted, ulErrors, ulDiag) =
                await _blobService.UploadAttachmentBlobsAsync(allAttachments, encryptionPassword, ct)
                    .ConfigureAwait(false);

            cycleResult.BlobsUploaded += ulUploaded + ulExisted;
            cycleResult.BlobsSkipped += ulSkipped;
            cycleResult.BlobErrors += ulErrors;
            foreach (var d in ulDiag) cycleResult.Diagnostics.Add(d);
            return ulErrors == 0;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception blobEx)
        {
            cycleResult.Diagnostics.Add($"Ошибка выгрузки blob перед отправкой пакета: {CloudErrorSanitizer.SanitizeDiagnosticDetail(blobEx.Message)}");
            cycleResult.BlobErrors++;
            return false;
        }
    }

    private SyncCycleResult FailWithBlobStats(SyncCycleResult cycleResult, string message)
    {
        var res = SyncCycleResult.Failure(message);
        res.BlobsUploaded = cycleResult.BlobsUploaded;
        res.BlobsDownloaded = cycleResult.BlobsDownloaded;
        res.BlobsSkipped = cycleResult.BlobsSkipped;
        res.BlobErrors = cycleResult.BlobErrors;
        res.Diagnostics.AddRange(cycleResult.Diagnostics);
        RecordOutcome(res);
        return res;
    }

    private SyncCycleResult Fail(string message)
    {
        var res = SyncCycleResult.Failure(message);
        RecordOutcome(res);
        return res;
    }

    private void RecordOutcome(SyncCycleResult result)
    {
        _lastSyncTimeUtc = _dateTimeProvider.UtcNow;
        _lastSyncSuccess = result.Success;
        _lastError = result.Success ? null : (result.Errors.Count > 0 ? result.Errors[0] : "Ошибка синхронизации");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _gate.Dispose();
        _transport.Dispose();
    }
}
