using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using QuickNotes.App.Models.Sync;

namespace QuickNotes.App.Services.Sync;

/// <summary>
/// Production transport for Yandex Object Storage using official AWS SDK for .NET (AWSSDK.S3).
/// Implements <see cref="ICloudObjectStoreTransport"/> with:
/// - Official AWS SDK SigV4 signing without home-grown cryptography
/// - Path-style and Yandex Object Storage compatible settings (https://s3.yandexcloud.net, ru-central1)
/// - Strictly in-memory credentials from DPAPI provider (never reads AWS credential files)
/// - Logical object key handling (spaces, Cyrillic Unicode, % signs) without manual double URL-encoding
/// - Immutable writes with explicit anti-overwrite pre-flight check and IfNoneMatch = "*"
/// - Conditional updates for technical pointers via ETag / IfMatch / IfNoneMatch
/// - Safe error classification (offline, auth, quota, conflict, corruption)
/// - Idempotent retries with exponential backoff and jitter; non-idempotent writes fail immediately
/// - Guaranteed zero leakage of Authorization headers or secrets in logs/exceptions.
/// </summary>
/// <remarks>
/// Инструкция минимальных bucket-scoped прав для сервисного аккаунта Yandex Cloud:
/// 1. В консоли Yandex Cloud создать отдельный сервисный аккаунт в каталоге (например, quicknotes-sync-sa).
/// 2. Не назначать сервисному аккаунту широких административных ролей на каталог или облако.
/// 3. В приватном бакете Yandex Object Storage (на вкладке «Права доступа» / ACL или Bucket Policy)
///    предоставить сервисному аккаунту минимальные bucket-scoped разрешения:
///    - READ (чтение объектов и просмотр списка)
///    - WRITE (запись неизменяемых пакетов, создание/обновление указателей и удаление при ротации)
/// 4. Сгенерировать статический ключ доступа (Access Key ID и Secret Access Key) и передать в QuickNotes.
/// Приложение QuickNotes не создаёт аккаунты или IAM-политики в облаке автоматически,
/// а работает строго по статическим ключам в рамках назначенных прав на бакет.
/// </remarks>
public class S3ObjectStoreTransport : ICloudObjectStoreTransport
{
    private readonly SyncCloudSettings _settings;
    private readonly IS3CredentialsProvider _credentialsProvider;
    private readonly IAmazonS3? _injectedClient;
    private IAmazonS3? _managedClient;
    private S3Credentials? _cachedCredentials;
    private readonly object _clientLock = new();
    private readonly Random _jitter = new();
    private bool _disposed;

    public S3ObjectStoreTransport(
        SyncCloudSettings settings,
        IS3CredentialsProvider credentialsProvider,
        IAmazonS3? s3Client = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _credentialsProvider = credentialsProvider ?? throw new ArgumentNullException(nameof(credentialsProvider));
        _injectedClient = s3Client;

        SyncCloudSettingsValidator.Validate(_settings, requireBucket: false);
    }

    public async Task<bool> TestConnectionAsync(CancellationToken ct = default)
    {
        EnsureBucketConfigured();
        return await ExecuteWithRetryAsync(async () =>
        {
            var client = await GetOrCreateClientAsync(ct).ConfigureAwait(false);
            var request = new ListObjectsV2Request
            {
                BucketName = _settings.Bucket,
                MaxKeys = 1
            };

            try
            {
                await client.ListObjectsV2Async(request, ct).ConfigureAwait(false);
                return true;
            }
            catch (Exception ex)
            {
                throw MapS3Exception(ex, null, ct);
            }
        }, isIdempotent: true, ct).ConfigureAwait(false);
    }

    public async Task<StorageObjectMetadata?> HeadObjectAsync(string key, CancellationToken ct = default)
    {
        EnsureBucketConfigured();
        SyncObjectKeyHelper.ValidateObjectKey(key);

        return await ExecuteWithRetryAsync(async () =>
        {
            var client = await GetOrCreateClientAsync(ct).ConfigureAwait(false);
            var request = new GetObjectMetadataRequest
            {
                BucketName = _settings.Bucket,
                Key = key
            };

            try
            {
                var response = await client.GetObjectMetadataAsync(request, ct).ConfigureAwait(false);
                return new StorageObjectMetadata
                {
                    Key = key,
                    ETag = StorageObjectMetadata.NormalizeETag(response.ETag),
                    ContentLength = response.ContentLength,
                    LastModified = ToUtcOffset(response.LastModified),
                    ContentType = response.Headers?.ContentType
                };
            }
            catch (AmazonS3Exception ex) when (IsNotFound(ex))
            {
                return null;
            }
            catch (Exception ex)
            {
                throw MapS3Exception(ex, key, ct);
            }
        }, isIdempotent: true, ct).ConfigureAwait(false);
    }

    public async Task<StorageObjectResult?> GetObjectAsync(string key, CancellationToken ct = default)
    {
        EnsureBucketConfigured();
        SyncObjectKeyHelper.ValidateObjectKey(key);

        return await ExecuteWithRetryAsync(async () =>
        {
            var client = await GetOrCreateClientAsync(ct).ConfigureAwait(false);
            var request = new GetObjectRequest
            {
                BucketName = _settings.Bucket,
                Key = key
            };

            try
            {
                using var response = await client.GetObjectAsync(request, ct).ConfigureAwait(false);
                using var ms = new MemoryStream();
                if (response.ResponseStream != null)
                {
                    await response.ResponseStream.CopyToAsync(ms, ct).ConfigureAwait(false);
                }

                return new StorageObjectResult
                {
                    Metadata = new StorageObjectMetadata
                    {
                        Key = key,
                        ETag = StorageObjectMetadata.NormalizeETag(response.ETag),
                        ContentLength = response.ContentLength,
                        LastModified = ToUtcOffset(response.LastModified),
                        ContentType = response.Headers?.ContentType
                    },
                    Content = ms.ToArray()
                };
            }
            catch (AmazonS3Exception ex) when (IsNotFound(ex))
            {
                return null;
            }
            catch (Exception ex)
            {
                throw MapS3Exception(ex, key, ct);
            }
        }, isIdempotent: true, ct).ConfigureAwait(false);
    }

    public async Task<StorageObjectMetadata> PutImmutableObjectAsync(
        string key,
        byte[] content,
        string contentType = "application/json",
        CancellationToken ct = default)
    {
        EnsureBucketConfigured();
        SyncObjectKeyHelper.ValidateObjectKey(key);
        if (content == null) throw new ArgumentNullException(nameof(content));

        // 1. Guard against silent overwrite: check if object already exists
        var existing = await HeadObjectAsync(key, ct).ConfigureAwait(false);
        if (existing != null)
        {
            throw new CloudConflictException(
                $"Конфликт: неизменяемый объект '{key}' уже существует в бакете и защищён от перезаписи.",
                objectKey: key,
                actualETag: existing.ETag,
                statusCode: 409);
        }

        // 2. Put with If-None-Match: *
        var client = await GetOrCreateClientAsync(ct).ConfigureAwait(false);
        using var ms = new MemoryStream(content, writable: false);
        var request = new PutObjectRequest
        {
            BucketName = _settings.Bucket,
            Key = key,
            InputStream = ms,
            ContentType = contentType,
            IfNoneMatch = "*"
        };

        try
        {
            var response = await client.PutObjectAsync(request, ct).ConfigureAwait(false);
            return new StorageObjectMetadata
            {
                Key = key,
                ETag = StorageObjectMetadata.NormalizeETag(response.ETag),
                ContentLength = content.Length,
                LastModified = DateTimeOffset.UtcNow,
                ContentType = contentType
            };
        }
        catch (AmazonS3Exception ex) when (IsPreconditionFailedOrConflict(ex))
        {
            throw new CloudConflictException(
                $"Конфликт: объект '{key}' был параллельно создан другим клиентом (PreconditionFailed).",
                objectKey: key,
                statusCode: (int)ex.StatusCode);
        }
        catch (Exception ex)
        {
            throw MapS3Exception(ex, key, ct);
        }
    }

    public async Task<StorageObjectMetadata> PutConditionalPointerAsync(
        string key,
        byte[] content,
        string? expectedETag,
        string contentType = "application/json",
        CancellationToken ct = default)
    {
        EnsureBucketConfigured();
        SyncObjectKeyHelper.ValidateObjectKey(key);
        if (content == null) throw new ArgumentNullException(nameof(content));

        var client = await GetOrCreateClientAsync(ct).ConfigureAwait(false);
        string? cleanExpected = StorageObjectMetadata.NormalizeETag(expectedETag);

        using var ms = new MemoryStream(content, writable: false);
        var request = new PutObjectRequest
        {
            BucketName = _settings.Bucket,
            Key = key,
            InputStream = ms,
            ContentType = contentType
        };

        if (string.IsNullOrEmpty(cleanExpected))
        {
            // Initial creation: pointer must not already exist
            request.IfNoneMatch = "*";
        }
        else
        {
            // Update: pointer ETag must match expected
            request.IfMatch = $"\"{cleanExpected}\"";
        }

        try
        {
            var response = await client.PutObjectAsync(request, ct).ConfigureAwait(false);
            return new StorageObjectMetadata
            {
                Key = key,
                ETag = StorageObjectMetadata.NormalizeETag(response.ETag),
                ContentLength = content.Length,
                LastModified = DateTimeOffset.UtcNow,
                ContentType = contentType
            };
        }
        catch (AmazonS3Exception ex) when (IsPreconditionFailedOrConflict(ex))
        {
            throw new CloudConflictException(
                $"Конфликт обновления указателя '{key}': ожидаемый ETag '{cleanExpected ?? "*"}' не совпадает с актуальным в облаке.",
                objectKey: key,
                expectedETag: cleanExpected,
                statusCode: (int)ex.StatusCode);
        }
        catch (Exception ex)
        {
            throw MapS3Exception(ex, key, ct);
        }
    }

    public async Task<IReadOnlyList<StorageObjectSummary>> ListObjectsAsync(
        string? prefix = null,
        int maxKeys = 1000,
        CancellationToken ct = default)
    {
        var result = await ListObjectsV2Async(new StorageListRequest
        {
            Prefix = prefix,
            MaxKeys = maxKeys
        }, ct).ConfigureAwait(false);

        return result.Objects;
    }

    public async Task<StorageListResult> ListObjectsV2Async(
        StorageListRequest request,
        CancellationToken ct = default)
    {
        if (request == null) throw new ArgumentNullException(nameof(request));
        EnsureBucketConfigured();

        return await ExecuteWithRetryAsync(async () =>
        {
            var client = await GetOrCreateClientAsync(ct).ConfigureAwait(false);
            var s3Request = new ListObjectsV2Request
            {
                BucketName = _settings.Bucket,
                Prefix = string.IsNullOrEmpty(request.Prefix) ? null : request.Prefix,
                Delimiter = string.IsNullOrEmpty(request.Delimiter) ? null : request.Delimiter,
                ContinuationToken = string.IsNullOrEmpty(request.ContinuationToken) ? null : request.ContinuationToken,
                MaxKeys = Math.Clamp(request.MaxKeys, 1, 1000)
            };

            try
            {
                var response = await client.ListObjectsV2Async(s3Request, ct).ConfigureAwait(false);
                var resultObjects = new List<StorageObjectSummary>();
                if (response.S3Objects != null)
                {
                    foreach (var item in response.S3Objects)
                    {
                        resultObjects.Add(new StorageObjectSummary
                        {
                            Key = item.Key,
                            ETag = StorageObjectMetadata.NormalizeETag(item.ETag),
                            Size = item.Size,
                            LastModified = ToUtcOffset(item.LastModified)
                        });
                    }
                }

                var commonPrefixes = new List<string>();
                if (response.CommonPrefixes != null)
                {
                    foreach (var cp in response.CommonPrefixes)
                    {
                        if (!string.IsNullOrEmpty(cp))
                        {
                            commonPrefixes.Add(cp);
                        }
                    }
                }

                return new StorageListResult
                {
                    Objects = resultObjects,
                    CommonPrefixes = commonPrefixes,
                    IsTruncated = response.IsTruncated,
                    NextContinuationToken = response.NextContinuationToken
                };
            }
            catch (Exception ex)
            {
                throw MapS3Exception(ex, request.Prefix, ct);
            }
        }, isIdempotent: true, ct).ConfigureAwait(false);
    }

    public async Task<bool> DeleteObjectAsync(string key, CancellationToken ct = default)
    {
        EnsureBucketConfigured();
        SyncObjectKeyHelper.ValidateObjectKey(key);

        var client = await GetOrCreateClientAsync(ct).ConfigureAwait(false);
        var request = new DeleteObjectRequest
        {
            BucketName = _settings.Bucket,
            Key = key
        };

        try
        {
            await client.DeleteObjectAsync(request, ct).ConfigureAwait(false);
            return true;
        }
        catch (AmazonS3Exception ex) when (IsNotFound(ex))
        {
            return false;
        }
        catch (Exception ex)
        {
            throw MapS3Exception(ex, key, ct);
        }
    }

    public static AmazonS3Config CreateS3Config(SyncCloudSettings settings)
    {
        string endpoint = string.IsNullOrWhiteSpace(settings.Endpoint)
            ? SyncCloudSettings.DefaultYandexEndpoint
            : settings.Endpoint;

        string region = string.IsNullOrWhiteSpace(settings.Region)
            ? SyncCloudSettings.DefaultYandexRegion
            : settings.Region;

        return new AmazonS3Config
        {
            ServiceURL = endpoint,
            AuthenticationRegion = region,
            ForcePathStyle = true,
            Timeout = TimeSpan.FromSeconds(Math.Max(1, settings.RequestTimeoutSeconds)),
            MaxErrorRetry = 0
        };
    }

    private async Task<IAmazonS3> GetOrCreateClientAsync(CancellationToken ct)
    {
        if (_injectedClient != null)
        {
            return _injectedClient;
        }

        var creds = await _credentialsProvider.GetCredentialsAsync(ct).ConfigureAwait(false);
        if (creds == null || string.IsNullOrWhiteSpace(creds.AccessKeyId) || string.IsNullOrWhiteSpace(creds.SecretAccessKey))
        {
            throw new CloudAuthException("Учетные данные S3 (Access Key ID / Secret Key) не настроены или не найдены.");
        }

        lock (_clientLock)
        {
            if (_managedClient != null &&
                _cachedCredentials != null &&
                _cachedCredentials.AccessKeyId == creds.AccessKeyId &&
                _cachedCredentials.SecretAccessKey == creds.SecretAccessKey)
            {
                return _managedClient;
            }

            _managedClient?.Dispose();
            _cachedCredentials = new S3Credentials(creds.AccessKeyId, creds.SecretAccessKey);

            var awsCredentials = new BasicAWSCredentials(creds.AccessKeyId, creds.SecretAccessKey);
            var config = CreateS3Config(_settings);
            _managedClient = new AmazonS3Client(awsCredentials, config);
            return _managedClient;
        }
    }

    private async Task<T> ExecuteWithRetryAsync<T>(Func<Task<T>> action, bool isIdempotent, CancellationToken ct)
    {
        int maxAttempts = isIdempotent ? Math.Max(1, _settings.MaxRetryAttempts + 1) : 1;
        int attempt = 0;

        while (true)
        {
            attempt++;
            ct.ThrowIfCancellationRequested();

            try
            {
                return await action().ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (CloudOfflineException) when (isIdempotent && attempt < maxAttempts)
            {
                await ApplyBackoffDelayAsync(attempt, ct).ConfigureAwait(false);
            }
            catch (CloudStorageException ex) when (isIdempotent && attempt < maxAttempts && IsRetriable(ex))
            {
                await ApplyBackoffDelayAsync(attempt, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task ApplyBackoffDelayAsync(int attempt, CancellationToken ct)
    {
        int baseMs = 100 * (1 << (attempt - 1));
        int jitterMs;
        lock (_jitter)
        {
            jitterMs = _jitter.Next(10, 50);
        }
        int totalDelay = Math.Min(3000, baseMs + jitterMs);
        await Task.Delay(totalDelay, ct).ConfigureAwait(false);
    }

    private static bool IsRetriable(CloudStorageException ex)
    {
        if (ex.ErrorCode is CloudErrorCode.Timeout or CloudErrorCode.Offline)
        {
            return true;
        }

        return IsTransientStatus(ex.StatusCode);
    }

    private static bool IsTransientStatus(int? statusCode)
    {
        return statusCode switch
        {
            500 or 502 or 503 or 504 => true,
            _ => false
        };
    }

    private void EnsureBucketConfigured()
    {
        SyncCloudSettingsValidator.ValidateBucket(_settings.Bucket);
    }

    private static bool IsNotFound(AmazonS3Exception ex)
    {
        return ex.StatusCode == HttpStatusCode.NotFound ||
               string.Equals(ex.ErrorCode, "NotFound", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(ex.ErrorCode, "NoSuchKey", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPreconditionFailedOrConflict(AmazonS3Exception ex)
    {
        return ex.StatusCode == HttpStatusCode.PreconditionFailed ||
               ex.StatusCode == HttpStatusCode.Conflict ||
               string.Equals(ex.ErrorCode, "PreconditionFailed", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(ex.ErrorCode, "ConditionalCheckFailed", StringComparison.OrdinalIgnoreCase);
    }

    private static Exception MapS3Exception(Exception ex, string? key, CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
        {
            return new OperationCanceledException(ct);
        }

        if (ex is CloudStorageException)
        {
            return ex;
        }

        // AWS SDK surfaces request timeouts as TaskCanceledException without canceling the caller token.
        // Treat those as retryable timeouts; honor true caller cancellation separately above.
        if (ex is TimeoutException || ex is TaskCanceledException)
        {
            return new CloudStorageException(CloudErrorCode.Timeout, "Превышено время ожидания ответа от S3 хранилища.", innerException: ex);
        }

        if (ex is OperationCanceledException oce)
        {
            return oce;
        }

        if (ex is AmazonS3Exception s3Ex)
        {
            int code = (int)s3Ex.StatusCode;
            string sanitizedMsg = SanitizeError(s3Ex.Message, s3Ex.ErrorCode);

            if (code == 401 || code == 403 ||
                string.Equals(s3Ex.ErrorCode, "AccessDenied", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(s3Ex.ErrorCode, "InvalidAccessKeyId", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(s3Ex.ErrorCode, "SignatureDoesNotMatch", StringComparison.OrdinalIgnoreCase))
            {
                return new CloudAuthException($"Ошибка авторизации S3 ({code}): доступ запрещён или ключи недействительны. {sanitizedMsg}", code);
            }

            if (code == 409 || code == 412 ||
                string.Equals(s3Ex.ErrorCode, "PreconditionFailed", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(s3Ex.ErrorCode, "ConditionalCheckFailed", StringComparison.OrdinalIgnoreCase))
            {
                return new CloudConflictException($"Конфликт S3 ({code}): {sanitizedMsg}", objectKey: key, statusCode: code);
            }

            if (code == 429 ||
                (code == 503 && (string.Equals(s3Ex.ErrorCode, "SlowDown", StringComparison.OrdinalIgnoreCase) || sanitizedMsg.Contains("SlowDown", StringComparison.OrdinalIgnoreCase))) ||
                string.Equals(s3Ex.ErrorCode, "SlowDown", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(s3Ex.ErrorCode, "QuotaExceeded", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(s3Ex.ErrorCode, "AccountProblem", StringComparison.OrdinalIgnoreCase) ||
                sanitizedMsg.Contains("SlowDown", StringComparison.OrdinalIgnoreCase) ||
                sanitizedMsg.Contains("QuotaExceeded", StringComparison.OrdinalIgnoreCase) ||
                sanitizedMsg.Contains("AccountProblem", StringComparison.OrdinalIgnoreCase))
            {
                return new CloudQuotaException($"Превышена квота или лимит запросов S3 ({code}): {sanitizedMsg}", code);
            }

            if (code >= 500)
            {
                return new CloudStorageException(CloudErrorCode.General, $"Сбой сервиса S3 ({code}): {sanitizedMsg}", code, key);
            }

            return new CloudStorageException(CloudErrorCode.General, $"Ошибка S3 ({code}): {sanitizedMsg}", code, key);
        }

        if (ex is AmazonClientException ace)
        {
            if (ace.InnerException is TaskCanceledException || ace.InnerException is TimeoutException)
            {
                return new CloudStorageException(CloudErrorCode.Timeout, "Превышено время ожидания ответа от S3 хранилища.", innerException: ace);
            }

            return new CloudOfflineException("Не удалось подключиться к S3 хранилищу (проверьте подключение к сети или DNS).", ace);
        }

        if (ex is HttpRequestException or IOException or System.Net.Sockets.SocketException)
        {
            return new CloudOfflineException("Не удалось подключиться к S3 хранилищу (проверьте подключение к сети или DNS).", ex);
        }

        return ex;
    }

    private static string SanitizeError(string? message, string? errorCode)
        => CloudErrorSanitizer.SanitizeProviderError(message, errorCode);

    private static DateTimeOffset? ToUtcOffset(DateTime dt)
    {
        if (dt == DateTime.MinValue || dt == default)
        {
            return null;
        }

        return dt.Kind switch
        {
            DateTimeKind.Utc => dt,
            DateTimeKind.Local => dt.ToUniversalTime(),
            _ => DateTime.SpecifyKind(dt, DateTimeKind.Utc)
        };
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        lock (_clientLock)
        {
            _managedClient?.Dispose();
            _managedClient = null;
        }
    }
}
