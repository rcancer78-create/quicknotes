using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using QuickNotes.App.Data;
using QuickNotes.App.Models.Sync;

namespace QuickNotes.App.Services.Sync;

/// <summary>
/// High-level single-operation coordinator for cloud synchronization.
/// Coordinates encrypted package export/upload, download/import, and listing.
/// Strictly non-blocking: network outages and cloud errors never disrupt local work.
/// </summary>
public class SyncCloudCoordinator : ISyncCloudCoordinator
{
    private readonly ICloudObjectStoreTransport _transport;
    private readonly ISyncPackageExporter _exporter;
    private readonly ISyncPackageImporter _importer;
    private readonly IDeviceIdProvider _deviceIdProvider;
    private readonly SyncCloudSettings _settings;
    private readonly IS3CredentialsStorage _credentialsStorage;
    private readonly SyncObjectKeyHelper _keyHelper;

    public SyncCloudCoordinator(
        ICloudObjectStoreTransport transport,
        ISyncPackageExporter exporter,
        ISyncPackageImporter importer,
        IDeviceIdProvider deviceIdProvider,
        SyncCloudSettings settings,
        IS3CredentialsStorage credentialsStorage,
        SyncObjectKeyHelper? keyHelper = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _exporter = exporter ?? throw new ArgumentNullException(nameof(exporter));
        _importer = importer ?? throw new ArgumentNullException(nameof(importer));
        _deviceIdProvider = deviceIdProvider ?? throw new ArgumentNullException(nameof(deviceIdProvider));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _credentialsStorage = credentialsStorage ?? throw new ArgumentNullException(nameof(credentialsStorage));
        _keyHelper = keyHelper ?? new SyncObjectKeyHelper(_settings);
    }

    public async Task<CloudSyncConnectionTestResult> TestConnectionAsync(CancellationToken ct = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_settings.Bucket))
            {
                return CloudSyncConnectionTestResult.Failure(_settings.Endpoint, _settings.Bucket, "Имя S3-бакета не задано в настройках.");
            }

            if (!_credentialsStorage.HasCredentials())
            {
                return CloudSyncConnectionTestResult.AuthFailure(_settings.Endpoint, _settings.Bucket, "Ключи доступа S3 (Access Key ID / Secret Key) не сохранены.");
            }

            bool ok = await _transport.TestConnectionAsync(ct).ConfigureAwait(false);
            return ok
                ? CloudSyncConnectionTestResult.Succeeded(_settings.Endpoint, _settings.Bucket)
                : CloudSyncConnectionTestResult.Failure(_settings.Endpoint, _settings.Bucket, "Проверка подключения не подтверждена.");
        }
        catch (CloudOfflineException ex)
        {
            return CloudSyncConnectionTestResult.Offline(_settings.Endpoint, _settings.Bucket, ex.Message);
        }
        catch (CloudAuthException ex)
        {
            return CloudSyncConnectionTestResult.AuthFailure(_settings.Endpoint, _settings.Bucket, ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ErrorLogService.Write("SyncCloudCoordinator.TestConnection", ex);
            return CloudSyncConnectionTestResult.Failure(_settings.Endpoint, _settings.Bucket, ex.Message);
        }
    }

    public async Task<CloudSyncUploadResult> UploadLatestPackageAsync(
        QuickNotesDbContext db,
        string encryptionPassword,
        CancellationToken ct = default)
    {
        if (db == null) throw new ArgumentNullException(nameof(db));
        if (string.IsNullOrEmpty(encryptionPassword))
            return CloudSyncUploadResult.Failure("Пароль сквозного шифрования синхронизации не задан.");

        if (!_settings.Enabled)
        {
            return CloudSyncUploadResult.Failure("Облачная синхронизация отключена в настройках.");
        }

        if (string.IsNullOrWhiteSpace(_settings.Bucket))
        {
            return CloudSyncUploadResult.Failure("Имя бакета Yandex Object Storage не настроено.");
        }

        if (!_credentialsStorage.HasCredentials())
        {
            return CloudSyncUploadResult.AuthError("Ключи доступа S3 отсутствуют в защищённом хранилище.");
        }

        Guid deviceId = _deviceIdProvider.GetDeviceId();

        try
        {
            // 1. Export deterministic encrypted package (AES-256-GCM envelope)
            var exportResult = await _exporter.ExportPackageAsync(db, encryptionPassword, deviceId).ConfigureAwait(false);
            if (!exportResult.Success || string.IsNullOrEmpty(exportResult.PackageJson))
            {
                string err = exportResult.Errors.Count > 0 ? exportResult.Errors[0] : "Ошибка формирования пакета синхронизации.";
                return CloudSyncUploadResult.Failure(err);
            }

            // 2. Put immutable package object into isolated device space
            string packageKey = _keyHelper.GetPackageKey(deviceId, exportResult.PackageId);
            byte[] packageBytes = Encoding.UTF8.GetBytes(exportResult.PackageJson);

            var meta = await _transport.PutImmutableObjectAsync(packageKey, packageBytes, "application/json", ct).ConfigureAwait(false);

            // 3. Conditionally update technical device pointer
            string pointerKey = _keyHelper.GetDevicePointerKey(deviceId);
            var currentPointerObj = await _transport.GetObjectAsync(pointerKey, ct).ConfigureAwait(false);

            string? currentETag = currentPointerObj?.Metadata.ETag;
            int prevCount = 0;
            if (currentPointerObj != null && currentPointerObj.Content.Length > 0)
            {
                try
                {
                    var prevPointer = JsonSerializer.Deserialize<DevicePointerPayload>(currentPointerObj.Content);
                    if (prevPointer != null)
                    {
                        prevCount = prevPointer.PackageCount;
                    }
                }
                catch
                {
                    // Ignore parse errors on previous pointer payload
                }
            }

            var newPointer = new DevicePointerPayload
            {
                FormatVersion = 1,
                DeviceId = deviceId,
                LatestPackageId = exportResult.PackageId,
                UpdatedAtUtc = DateTime.UtcNow,
                PackageCount = prevCount + 1
            };

            byte[] pointerBytes = JsonSerializer.SerializeToUtf8Bytes(newPointer);
            var pointerMeta = await _transport.PutConditionalPointerAsync(pointerKey, pointerBytes, currentETag, "application/json", ct).ConfigureAwait(false);

            return CloudSyncUploadResult.Succeeded(
                exportResult.PackageId,
                packageKey,
                pointerMeta.ETag,
                packageBytes.Length);
        }
        catch (CloudOfflineException ex)
        {
            return CloudSyncUploadResult.Offline(ex.Message);
        }
        catch (CloudAuthException ex)
        {
            return CloudSyncUploadResult.AuthError(ex.Message);
        }
        catch (CloudConflictException ex)
        {
            return CloudSyncUploadResult.Conflict(ex.Message, ex.ObjectKey);
        }
        catch (CloudQuotaException ex)
        {
            return CloudSyncUploadResult.QuotaError(ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ErrorLogService.Write("SyncCloudCoordinator.Upload", ex);
            return CloudSyncUploadResult.Failure(ex.Message);
        }
    }

    public async Task<CloudSyncDownloadResult> DownloadAndImportPackageAsync(
        string packageKey,
        QuickNotesDbContext db,
        string encryptionPassword,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(packageKey))
            return CloudSyncDownloadResult.Failure("Ключ объекта пакета не указан.");
        if (db == null) throw new ArgumentNullException(nameof(db));
        if (string.IsNullOrEmpty(encryptionPassword))
            return CloudSyncDownloadResult.Failure("Пароль сквозного шифрования не указан.");

        try
        {
            var obj = await _transport.GetObjectAsync(packageKey, ct).ConfigureAwait(false);
            if (obj == null || obj.Content.Length == 0)
            {
                return CloudSyncDownloadResult.Failure($"Объект '{packageKey}' не найден в облачном хранилище.");
            }

            string packageJson = Encoding.UTF8.GetString(obj.Content);
            var importResult = await _importer.ImportPackageAsync(db, packageJson, encryptionPassword).ConfigureAwait(false);

            return CloudSyncDownloadResult.Succeeded(packageKey, importResult);
        }
        catch (CloudOfflineException ex)
        {
            return CloudSyncDownloadResult.Offline(ex.Message);
        }
        catch (CloudAuthException ex)
        {
            return CloudSyncDownloadResult.Failure(ex.Message, isAuth: true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ErrorLogService.Write("SyncCloudCoordinator.Download", ex);
            return CloudSyncDownloadResult.Failure(ex.Message);
        }
    }

    public async Task<CloudSyncListResult> ListRemotePackagesAsync(CancellationToken ct = default)
    {
        try
        {
            string prefix = _keyHelper.GetDevicesPrefix();
            var summaries = await _transport.ListObjectsAsync(prefix, maxKeys: 1000, ct).ConfigureAwait(false);

            var list = new List<StorageObjectSummary>(summaries);
            var pointers = new List<DevicePointerPayload>();

            foreach (var item in list)
            {
                if (_keyHelper.TryParsePointerKey(item.Key, out _))
                {
                    try
                    {
                        var obj = await _transport.GetObjectAsync(item.Key, ct).ConfigureAwait(false);
                        if (obj != null && obj.Content.Length > 0)
                        {
                            var ptr = JsonSerializer.Deserialize<DevicePointerPayload>(obj.Content);
                            if (ptr != null) pointers.Add(ptr);
                        }
                    }
                    catch
                    {
                        // Pointer read is best-effort during listing
                    }
                }
            }

            return CloudSyncListResult.Succeeded(list, pointers);
        }
        catch (CloudOfflineException ex)
        {
            return CloudSyncListResult.Offline(ex.Message);
        }
        catch (CloudAuthException ex)
        {
            return CloudSyncListResult.Failure(ex.Message, isAuth: true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ErrorLogService.Write("SyncCloudCoordinator.List", ex);
            return CloudSyncListResult.Failure(ex.Message);
        }
    }
}
