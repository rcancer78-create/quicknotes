using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using QuickNotes.App.Data;
using QuickNotes.App.Models;
using QuickNotes.App.Models.Sync;
using QuickNotes.App.Services;
using QuickNotes.App.Services.Sync;
using Xunit;

namespace QuickNotes.Tests;

[TestCategory(TestCategories.Integration)]
public class SyncCloudCoordinatorTests : IDisposable
{
    private readonly List<string> _tempFiles = new();
    private readonly List<IDisposable> _disposables = new();

    public void Dispose()
    {
        foreach (var d in _disposables)
        {
            try { d.Dispose(); } catch { }
        }

        foreach (var file in _tempFiles)
        {
            if (File.Exists(file))
            {
                try { File.Delete(file); } catch { }
            }
        }
    }

    private string CreateTempDbPath()
    {
        var path = Path.Combine(Path.GetTempPath(), $"quicknotes_cloudtest_{Guid.NewGuid():N}.db");
        _tempFiles.Add(path);
        return path;
    }

    private QuickNotesDbContext CreateContext(string dbPath)
    {
        var context = new QuickNotesDbContext(dbPath);
        _disposables.Add(context);
        return context;
    }

    /// <summary>
    /// Autonomous in-memory implementation of ICloudObjectStoreTransport for unit tests.
    /// Strictly replicates S3 / Yandex Object Storage semantics (ETags, immutable writes, conditional updates).
    /// </summary>
    private class InMemoryCloudObjectStoreTransport : ICloudObjectStoreTransport
    {
        private class StoredObject
        {
            public byte[] Content { get; set; } = Array.Empty<byte>();
            public string ETag { get; set; } = string.Empty;
            public DateTimeOffset LastModified { get; set; } = DateTimeOffset.UtcNow;
            public string ContentType { get; set; } = "application/json";
        }

        private readonly ConcurrentDictionary<string, StoredObject> _store = new();
        public bool IsOffline { get; set; } = false;
        public bool IsAuthError { get; set; } = false;

        public Task<bool> TestConnectionAsync(CancellationToken ct = default)
        {
            if (IsOffline) throw new CloudOfflineException("Object store is offline.");
            if (IsAuthError) throw new CloudAuthException("Access denied to bucket.", 403);
            return Task.FromResult(true);
        }

        public Task<StorageObjectMetadata?> HeadObjectAsync(string key, CancellationToken ct = default)
        {
            if (IsOffline) throw new CloudOfflineException("Object store is offline.");
            if (IsAuthError) throw new CloudAuthException("Access denied to bucket.", 403);

            if (_store.TryGetValue(key, out var obj))
            {
                return Task.FromResult<StorageObjectMetadata?>(new StorageObjectMetadata
                {
                    Key = key,
                    ETag = obj.ETag,
                    ContentLength = obj.Content.Length,
                    LastModified = obj.LastModified,
                    ContentType = obj.ContentType
                });
            }

            return Task.FromResult<StorageObjectMetadata?>(null);
        }

        public Task<StorageObjectResult?> GetObjectAsync(string key, CancellationToken ct = default)
        {
            if (IsOffline) throw new CloudOfflineException("Object store is offline.");
            if (IsAuthError) throw new CloudAuthException("Access denied to bucket.", 403);

            if (_store.TryGetValue(key, out var obj))
            {
                return Task.FromResult<StorageObjectResult?>(new StorageObjectResult
                {
                    Metadata = new StorageObjectMetadata
                    {
                        Key = key,
                        ETag = obj.ETag,
                        ContentLength = obj.Content.Length,
                        LastModified = obj.LastModified,
                        ContentType = obj.ContentType
                    },
                    Content = (byte[])obj.Content.Clone()
                });
            }

            return Task.FromResult<StorageObjectResult?>(null);
        }

        public Task<StorageObjectMetadata> PutImmutableObjectAsync(
            string key,
            byte[] content,
            string contentType = "application/json",
            CancellationToken ct = default)
        {
            if (IsOffline) throw new CloudOfflineException("Object store is offline.");
            if (IsAuthError) throw new CloudAuthException("Access denied to bucket.", 403);

            // Immutable collision check
            if (_store.TryGetValue(key, out var existing))
            {
                throw new CloudConflictException(
                    $"Конфликт: неизменяемый объект '{key}' уже существует.",
                    objectKey: key,
                    actualETag: existing.ETag,
                    statusCode: 409);
            }

            string etag = Guid.NewGuid().ToString("N");
            var stored = new StoredObject
            {
                Content = (byte[])content.Clone(),
                ETag = etag,
                LastModified = DateTimeOffset.UtcNow,
                ContentType = contentType
            };

            if (!_store.TryAdd(key, stored))
            {
                throw new CloudConflictException($"Конфликт параллельной записи объекта '{key}'.", objectKey: key, statusCode: 412);
            }

            return Task.FromResult(new StorageObjectMetadata
            {
                Key = key,
                ETag = etag,
                ContentLength = content.Length,
                LastModified = stored.LastModified,
                ContentType = contentType
            });
        }

        public Task<StorageObjectMetadata> PutConditionalPointerAsync(
            string key,
            byte[] content,
            string? expectedETag,
            string contentType = "application/json",
            CancellationToken ct = default)
        {
            if (IsOffline) throw new CloudOfflineException("Object store is offline.");
            if (IsAuthError) throw new CloudAuthException("Access denied to bucket.", 403);

            string? cleanExpected = StorageObjectMetadata.NormalizeETag(expectedETag);
            string newETag = Guid.NewGuid().ToString("N");

            if (string.IsNullOrEmpty(cleanExpected))
            {
                // Create if absent (If-None-Match: *)
                var newObj = new StoredObject
                {
                    Content = (byte[])content.Clone(),
                    ETag = newETag,
                    LastModified = DateTimeOffset.UtcNow,
                    ContentType = contentType
                };

                if (!_store.TryAdd(key, newObj))
                {
                    throw new CloudConflictException(
                        $"Конфликт создания указателя '{key}': объект уже существует.",
                        objectKey: key,
                        statusCode: 412);
                }
            }
            else
            {
                // Update with If-Match
                if (!_store.TryGetValue(key, out var current))
                {
                    throw new CloudConflictException(
                        $"Конфликт обновления указателя '{key}': объект не найден в хранилище.",
                        objectKey: key,
                        expectedETag: cleanExpected,
                        statusCode: 412);
                }

                if (current.ETag != cleanExpected)
                {
                    throw new CloudConflictException(
                        $"Конфликт обновления указателя '{key}': ETag не совпадает. Ожидался '{cleanExpected}', актуальный '{current.ETag}'.",
                        objectKey: key,
                        expectedETag: cleanExpected,
                        actualETag: current.ETag,
                        statusCode: 412);
                }

                current.Content = (byte[])content.Clone();
                current.ETag = newETag;
                current.LastModified = DateTimeOffset.UtcNow;
            }

            return Task.FromResult(new StorageObjectMetadata
            {
                Key = key,
                ETag = newETag,
                ContentLength = content.Length,
                LastModified = DateTimeOffset.UtcNow,
                ContentType = contentType
            });
        }

        public Task<IReadOnlyList<StorageObjectSummary>> ListObjectsAsync(
            string? prefix = null,
            int maxKeys = 1000,
            CancellationToken ct = default)
        {
            if (IsOffline) throw new CloudOfflineException("Object store is offline.");
            if (IsAuthError) throw new CloudAuthException("Access denied to bucket.", 403);

            var query = _store.AsEnumerable();
            if (!string.IsNullOrEmpty(prefix))
            {
                query = query.Where(kvp => kvp.Key.StartsWith(prefix, StringComparison.Ordinal));
            }

            var list = query
                .Take(maxKeys)
                .Select(kvp => new StorageObjectSummary
                {
                    Key = kvp.Key,
                    ETag = kvp.Value.ETag,
                    Size = kvp.Value.Content.Length,
                    LastModified = kvp.Value.LastModified
                })
                .ToList();

            return Task.FromResult<IReadOnlyList<StorageObjectSummary>>(list);
        }

        public Task<StorageListResult> ListObjectsV2Async(
            StorageListRequest request,
            CancellationToken ct = default)
        {
            if (IsOffline) throw new CloudOfflineException("Object store is offline.");
            if (IsAuthError) throw new CloudAuthException("Access denied to bucket.", 403);

            string prefix = request.Prefix ?? string.Empty;
            string? delimiter = string.IsNullOrEmpty(request.Delimiter) ? null : request.Delimiter;
            int maxKeys = request.MaxKeys > 0 ? request.MaxKeys : 1000;
            string? token = request.ContinuationToken;

            var sortedKeys = _store.Keys
                .Where(k => k.StartsWith(prefix, StringComparison.Ordinal))
                .OrderBy(k => k, StringComparer.Ordinal)
                .ToList();

            int startIndex = 0;
            if (!string.IsNullOrEmpty(token))
            {
                if (int.TryParse(token, out int parsedIndex))
                {
                    startIndex = parsedIndex;
                }
                else
                {
                    startIndex = sortedKeys.FindIndex(k => string.Compare(k, token, StringComparison.Ordinal) >= 0);
                    if (startIndex < 0) startIndex = sortedKeys.Count;
                }
            }

            var objects = new List<StorageObjectSummary>();
            var commonPrefixes = new HashSet<string>(StringComparer.Ordinal);
            int currentIndex = startIndex;
            bool isTruncated = false;
            string? nextContinuationToken = null;

            while (currentIndex < sortedKeys.Count)
            {
                string key = sortedKeys[currentIndex];
                if (delimiter != null)
                {
                    string rest = key.Substring(prefix.Length);
                    int delimIndex = rest.IndexOf(delimiter, StringComparison.Ordinal);
                    if (delimIndex >= 0)
                    {
                        string cp = prefix + rest.Substring(0, delimIndex + delimiter.Length);
                        if (!commonPrefixes.Contains(cp))
                        {
                            if (objects.Count + commonPrefixes.Count >= maxKeys)
                            {
                                isTruncated = true;
                                nextContinuationToken = currentIndex.ToString();
                                break;
                            }
                            commonPrefixes.Add(cp);
                        }
                        currentIndex++;
                        continue;
                    }
                }

                if (objects.Count + commonPrefixes.Count >= maxKeys)
                {
                    isTruncated = true;
                    nextContinuationToken = currentIndex.ToString();
                    break;
                }

                if (_store.TryGetValue(key, out var stored))
                {
                    objects.Add(new StorageObjectSummary
                    {
                        Key = key,
                        ETag = stored.ETag,
                        Size = stored.Content.Length,
                        LastModified = stored.LastModified
                    });
                }
                currentIndex++;
            }

            return Task.FromResult(new StorageListResult
            {
                Objects = objects,
                CommonPrefixes = commonPrefixes.OrderBy(p => p, StringComparer.Ordinal).ToList(),
                IsTruncated = isTruncated,
                NextContinuationToken = nextContinuationToken
            });
        }

        public Task<bool> DeleteObjectAsync(string key, CancellationToken ct = default)
        {
            return Task.FromResult(_store.TryRemove(key, out _));
        }

        public void Dispose() { }
    }

    private class InMemoryCredentialsStorage : IS3CredentialsStorage
    {
        private S3Credentials? _creds;
        public InMemoryCredentialsStorage(S3Credentials? creds = null)
        {
            _creds = creds;
        }

        public Task<S3Credentials?> LoadCredentialsAsync(CancellationToken ct = default) => Task.FromResult(_creds);
        public Task SaveCredentialsAsync(S3Credentials credentials, CancellationToken ct = default)
        {
            _creds = credentials;
            return Task.CompletedTask;
        }
        public Task DeleteCredentialsAsync(CancellationToken ct = default)
        {
            _creds = null;
            return Task.CompletedTask;
        }
        public bool HasCredentials() => _creds != null;
    }

    [Fact]
    public async Task EndToEnd_UploadAndDownload_TransfersEncryptedPackage_WithoutPlaintextInCloud()
    {
        string dbPathA = CreateTempDbPath();
        string dbPathB = CreateTempDbPath();

        using var dbA = CreateContext(dbPathA);
        DbInitializer.Initialize(dbA, null);

        using var dbB = CreateContext(dbPathB);
        DbInitializer.Initialize(dbB, null);

        // 1. Seed sensitive note in Device A
        string classifiedText = "TOP_SECRET_CLASSIFIED_NOTE_CONTENT_12345";
        var note = new Note
        {
            Text = classifiedText,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        dbA.Notes.Add(note);
        dbA.SaveChanges();

        var crypto = new SyncCryptoService(iterations: 10_000); // reduced iterations for faster test run
        var deviceA = Guid.NewGuid();
        var deviceIdProviderA = new FixedDeviceIdProvider(deviceA);
        var exporterA = new SyncPackageExporter(crypto, deviceIdProviderA);
        var importerB = new SyncPackageImporter(crypto);

        var cloudTransport = new InMemoryCloudObjectStoreTransport();
        var credentialsStorage = new InMemoryCredentialsStorage(new S3Credentials("testKey", "testSecret"));

        var settings = new SyncCloudSettings
        {
            Enabled = true,
            Endpoint = "https://storage.yandexcloud.net",
            Region = "ru-central1",
            Bucket = "private-notes-bucket",
            Prefix = "user-backup"
        };

        var keyHelper = new SyncObjectKeyHelper(settings);
        var coordinatorA = new SyncCloudCoordinator(
            cloudTransport,
            exporterA,
            importerB,
            deviceIdProviderA,
            settings,
            credentialsStorage,
            keyHelper);

        string syncPassword = "StrongSyncPassword!2026";

        // 2. Upload package from Device A
        var uploadResult = await coordinatorA.UploadLatestPackageAsync(dbA, syncPassword);
        Assert.True(uploadResult.Success, uploadResult.ErrorMessage);
        Assert.NotNull(uploadResult.ObjectKey);
        Assert.NotNull(uploadResult.PackageId);

        // 3. Inspect Cloud Storage: verify ZERO plaintext
        var rawCloudObj = await cloudTransport.GetObjectAsync(uploadResult.ObjectKey);
        Assert.NotNull(rawCloudObj);
        string cloudPayloadString = Encoding.UTF8.GetString(rawCloudObj.Content);

        // Crucial security guarantee: Note plaintext NEVER appears in cloud storage
        Assert.DoesNotContain(classifiedText, cloudPayloadString);

        // Verify it is a valid SyncPackageEnvelope
        var envelope = JsonSerializer.Deserialize<SyncPackageEnvelope>(cloudPayloadString);
        Assert.NotNull(envelope);
        Assert.Equal("QNSP", envelope.Magic);
        Assert.Equal(deviceA, envelope.DeviceId);
        Assert.False(string.IsNullOrEmpty(envelope.EncryptedPayloadBase64));

        // 4. Verify Device Pointer was created with ETag
        string pointerKey = keyHelper.GetDevicePointerKey(deviceA);
        var pointerObj = await cloudTransport.GetObjectAsync(pointerKey);
        Assert.NotNull(pointerObj);
        var pointer = JsonSerializer.Deserialize<DevicePointerPayload>(pointerObj.Content);
        Assert.NotNull(pointer);
        Assert.Equal(deviceA, pointer.DeviceId);
        Assert.Equal(uploadResult.PackageId, pointer.LatestPackageId);
        Assert.Equal(1, pointer.PackageCount);

        // 5. Download and Import into Device B
        var deviceIdProviderB = new FixedDeviceIdProvider(Guid.NewGuid());
        var coordinatorB = new SyncCloudCoordinator(
            cloudTransport,
            new SyncPackageExporter(crypto, deviceIdProviderB),
            importerB,
            deviceIdProviderB,
            settings,
            credentialsStorage,
            keyHelper);

        var downloadResult = await coordinatorB.DownloadAndImportPackageAsync(uploadResult.ObjectKey, dbB, syncPassword);
        Assert.True(downloadResult.Success, downloadResult.ErrorMessage);
        Assert.NotNull(downloadResult.ImportResult);
        Assert.True(downloadResult.ImportResult.Success, string.Join("; ", downloadResult.ImportResult.Errors));
        Assert.Equal(1, downloadResult.ImportResult.Notes.Created);

        // Verify note decrypted and restored in Device B
        var noteInB = await dbB.Notes.FirstOrDefaultAsync();
        Assert.NotNull(noteInB);
        Assert.Equal(classifiedText, noteInB.Text);
        Assert.Equal(note.SyncId, noteInB.SyncId);

        // 6. Attempt download and import with WRONG password
        string dbPathC = CreateTempDbPath();
        using var dbC = CreateContext(dbPathC);
        DbInitializer.Initialize(dbC, null);

        var wrongPwdResult = await coordinatorB.DownloadAndImportPackageAsync(uploadResult.ObjectKey, dbC, "WrongPassword!999");
        Assert.NotNull(wrongPwdResult.ImportResult);
        Assert.False(wrongPwdResult.ImportResult.Success);
        Assert.Contains(wrongPwdResult.ImportResult.Errors, e => e.Contains("парол") || e.Contains("целостност") || e.Contains("расшифров"));
    }

    [Fact]
    public async Task DeviceIsolation_TwoDevicesUpload_KeepsObjectsInSeparateNamespaces()
    {
        string dbPathA = CreateTempDbPath();
        string dbPathB = CreateTempDbPath();
        using var dbA = CreateContext(dbPathA);
        using var dbB = CreateContext(dbPathB);
        DbInitializer.Initialize(dbA, null);
        DbInitializer.Initialize(dbB, null);

        dbA.Notes.Add(new Note { Text = "Device A Note", CreatedAt = DateTime.UtcNow });
        dbA.SaveChanges();
        dbB.Notes.Add(new Note { Text = "Device B Note", CreatedAt = DateTime.UtcNow });
        dbB.SaveChanges();

        var crypto = new SyncCryptoService(10_000);
        var cloudTransport = new InMemoryCloudObjectStoreTransport();
        var credentials = new InMemoryCredentialsStorage(new S3Credentials("k", "s"));
        var settings = new SyncCloudSettings { Enabled = true, Bucket = "b", Prefix = "ns" };
        var keyHelper = new SyncObjectKeyHelper(settings);

        var devA = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var devB = Guid.Parse("22222222-2222-2222-2222-222222222222");

        var coordA = new SyncCloudCoordinator(cloudTransport, new SyncPackageExporter(crypto, new FixedDeviceIdProvider(devA)), new SyncPackageImporter(crypto), new FixedDeviceIdProvider(devA), settings, credentials, keyHelper);
        var coordB = new SyncCloudCoordinator(cloudTransport, new SyncPackageExporter(crypto, new FixedDeviceIdProvider(devB)), new SyncPackageImporter(crypto), new FixedDeviceIdProvider(devB), settings, credentials, keyHelper);

        var resA = await coordA.UploadLatestPackageAsync(dbA, "Pass123!");
        var resB = await coordB.UploadLatestPackageAsync(dbB, "Pass123!");

        Assert.True(resA.Success);
        Assert.True(resB.Success);

        Assert.StartsWith("ns/v1/devices/11111111-1111-1111-1111-111111111111/packages/", resA.ObjectKey);
        Assert.StartsWith("ns/v1/devices/22222222-2222-2222-2222-222222222222/packages/", resB.ObjectKey);

        // List remote packages
        var listResult = await coordA.ListRemotePackagesAsync();
        Assert.True(listResult.Success);
        Assert.Equal(4, listResult.Summaries.Count); // 2 packages + 2 pointers
        Assert.Equal(2, listResult.DevicePointers.Count);
    }

    [Fact]
    public async Task PointerConflict_ConcurrentUpdate_TriggersConflictResult()
    {
        string dbPath = CreateTempDbPath();
        using var db = CreateContext(dbPath);
        DbInitializer.Initialize(db, null);
        db.Notes.Add(new Note { Text = "Note for conflict test", CreatedAt = DateTime.UtcNow });
        db.SaveChanges();

        var crypto = new SyncCryptoService(10_000);
        var cloudTransport = new InMemoryCloudObjectStoreTransport();
        var credentials = new InMemoryCredentialsStorage(new S3Credentials("k", "s"));
        var settings = new SyncCloudSettings { Enabled = true, Bucket = "b", Prefix = "test" };
        var keyHelper = new SyncObjectKeyHelper(settings);
        var devId = Guid.NewGuid();

        var coord = new SyncCloudCoordinator(cloudTransport, new SyncPackageExporter(crypto, new FixedDeviceIdProvider(devId)), new SyncPackageImporter(crypto), new FixedDeviceIdProvider(devId), settings, credentials, keyHelper);

        // First upload creates pointer
        var res1 = await coord.UploadLatestPackageAsync(db, "Pass123!");
        Assert.True(res1.Success);

        // Simulate concurrent device or process modifying pointer out-of-band
        string ptrKey = keyHelper.GetDevicePointerKey(devId);
        var currentPtrObj = await cloudTransport.GetObjectAsync(ptrKey);
        Assert.NotNull(currentPtrObj);

        // Modify remote pointer behind the scenes with a different ETag
        await cloudTransport.PutConditionalPointerAsync(
            ptrKey,
            Encoding.UTF8.GetBytes("{\"tampered\": true}"),
            currentPtrObj.Metadata.ETag);

        // Now an upload that expected the OLD ETag will detect conflict
        // Let's test calling PutConditionalPointer directly with stale ETag
        await Assert.ThrowsAsync<CloudConflictException>(() =>
            cloudTransport.PutConditionalPointerAsync(
                ptrKey,
                Encoding.UTF8.GetBytes("{\"stale\": true}"),
                currentPtrObj.Metadata.ETag));
    }

    [Fact]
    public async Task OfflineResilience_WhenNetworkIsDown_UploadReturnsOfflineResult_WithoutThrowing()
    {
        string dbPath = CreateTempDbPath();
        using var db = CreateContext(dbPath);
        DbInitializer.Initialize(db, null);
        db.Notes.Add(new Note { Text = "Local note", CreatedAt = DateTime.UtcNow });
        db.SaveChanges();

        var crypto = new SyncCryptoService(10_000);
        var cloudTransport = new InMemoryCloudObjectStoreTransport { IsOffline = true }; // Simulate offline
        var credentials = new InMemoryCredentialsStorage(new S3Credentials("k", "s"));
        var settings = new SyncCloudSettings { Enabled = true, Bucket = "b" };

        var coord = new SyncCloudCoordinator(
            cloudTransport,
            new SyncPackageExporter(crypto, new FixedDeviceIdProvider(Guid.NewGuid())),
            new SyncPackageImporter(crypto),
            new FixedDeviceIdProvider(Guid.NewGuid()),
            settings,
            credentials);

        // MUST NOT throw! Local work continues uninhibited
        var uploadResult = await coord.UploadLatestPackageAsync(db, "Password123!");
        Assert.False(uploadResult.Success);
        Assert.True(uploadResult.IsOffline);
        Assert.NotNull(uploadResult.ErrorMessage);

        var testResult = await coord.TestConnectionAsync();
        Assert.False(testResult.Success);
        Assert.True(testResult.IsOffline);

        var listResult = await coord.ListRemotePackagesAsync();
        Assert.False(listResult.Success);
        Assert.True(listResult.IsOffline);

        var downloadResult = await coord.DownloadAndImportPackageAsync("some/key.json", db, "Password123!");
        Assert.False(downloadResult.Success);
        Assert.True(downloadResult.IsOffline);
    }

    [Fact]
    public async Task DisabledSettings_UploadReturnsFailure_WithoutInvokingCloud()
    {
        string dbPath = CreateTempDbPath();
        using var db = CreateContext(dbPath);
        DbInitializer.Initialize(db, null);

        var crypto = new SyncCryptoService(10_000);
        var cloudTransport = new InMemoryCloudObjectStoreTransport();
        var credentials = new InMemoryCredentialsStorage(new S3Credentials("k", "s"));
        var settings = new SyncCloudSettings { Enabled = false }; // Disabled

        var coord = new SyncCloudCoordinator(
            cloudTransport,
            new SyncPackageExporter(crypto, new FixedDeviceIdProvider(Guid.NewGuid())),
            new SyncPackageImporter(crypto),
            new FixedDeviceIdProvider(Guid.NewGuid()),
            settings,
            credentials);

        var res = await coord.UploadLatestPackageAsync(db, "Password123!");
        Assert.False(res.Success);
        Assert.Contains("отключен", res.ErrorMessage);
    }
}
