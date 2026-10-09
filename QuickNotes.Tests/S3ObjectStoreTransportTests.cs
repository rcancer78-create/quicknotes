using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using QuickNotes.App.Models.Sync;
using QuickNotes.App.Services.Sync;
using Xunit;

namespace QuickNotes.Tests;

public class S3ObjectStoreTransportTests
{
    private class FakeAmazonS3Client : AmazonS3Client
    {
        public FakeAmazonS3Client() : base(
            new AnonymousAWSCredentials(),
            new AmazonS3Config { ServiceURL = "http://localhost", ForcePathStyle = true })
        {
        }

        public Func<ListObjectsV2Request, CancellationToken, Task<ListObjectsV2Response>>? ListObjectsV2Handler { get; set; }
        public Func<GetObjectMetadataRequest, CancellationToken, Task<GetObjectMetadataResponse>>? GetObjectMetadataHandler { get; set; }
        public Func<GetObjectRequest, CancellationToken, Task<GetObjectResponse>>? GetObjectHandler { get; set; }
        public Func<PutObjectRequest, CancellationToken, Task<PutObjectResponse>>? PutObjectHandler { get; set; }
        public Func<DeleteObjectRequest, CancellationToken, Task<DeleteObjectResponse>>? DeleteObjectHandler { get; set; }

        public List<ListObjectsV2Request> CapturedListRequests { get; } = new();
        public List<GetObjectMetadataRequest> CapturedHeadRequests { get; } = new();
        public List<GetObjectRequest> CapturedGetRequests { get; } = new();
        public List<PutObjectRequest> CapturedPutRequests { get; } = new();
        public List<DeleteObjectRequest> CapturedDeleteRequests { get; } = new();

        public override Task<ListObjectsV2Response> ListObjectsV2Async(ListObjectsV2Request request, CancellationToken cancellationToken = default)
        {
            CapturedListRequests.Add(request);
            if (ListObjectsV2Handler != null) return ListObjectsV2Handler(request, cancellationToken);
            return Task.FromResult(new ListObjectsV2Response { HttpStatusCode = HttpStatusCode.OK });
        }

        public override Task<GetObjectMetadataResponse> GetObjectMetadataAsync(GetObjectMetadataRequest request, CancellationToken cancellationToken = default)
        {
            CapturedHeadRequests.Add(request);
            if (GetObjectMetadataHandler != null) return GetObjectMetadataHandler(request, cancellationToken);
            return Task.FromResult(new GetObjectMetadataResponse { HttpStatusCode = HttpStatusCode.OK, ETag = "\"default-etag\"" });
        }

        public override Task<GetObjectResponse> GetObjectAsync(GetObjectRequest request, CancellationToken cancellationToken = default)
        {
            CapturedGetRequests.Add(request);
            if (GetObjectHandler != null) return GetObjectHandler(request, cancellationToken);
            return Task.FromResult(new GetObjectResponse
            {
                HttpStatusCode = HttpStatusCode.OK,
                ETag = "\"default-etag\"",
                ResponseStream = new MemoryStream(Encoding.UTF8.GetBytes("sample data"))
            });
        }

        public override Task<PutObjectResponse> PutObjectAsync(PutObjectRequest request, CancellationToken cancellationToken = default)
        {
            CapturedPutRequests.Add(request);
            if (PutObjectHandler != null) return PutObjectHandler(request, cancellationToken);
            return Task.FromResult(new PutObjectResponse { HttpStatusCode = HttpStatusCode.OK, ETag = "\"default-etag\"" });
        }

        public override Task<DeleteObjectResponse> DeleteObjectAsync(DeleteObjectRequest request, CancellationToken cancellationToken = default)
        {
            CapturedDeleteRequests.Add(request);
            if (DeleteObjectHandler != null) return DeleteObjectHandler(request, cancellationToken);
            return Task.FromResult(new DeleteObjectResponse { HttpStatusCode = HttpStatusCode.OK });
        }
    }

    private class FixedCredentialsProvider : IS3CredentialsProvider
    {
        private readonly S3Credentials _creds;
        public FixedCredentialsProvider(string key = "testKey", string secret = "testSecret")
        {
            _creds = new S3Credentials(key, secret);
        }
        public Task<S3Credentials?> GetCredentialsAsync(CancellationToken ct = default) => Task.FromResult<S3Credentials?>(_creds);
    }

    private readonly SyncCloudSettings _settings = new()
    {
        Enabled = true,
        Endpoint = "https://s3.yandexcloud.net",
        Region = "ru-central1",
        Bucket = "my-test-bucket",
        Prefix = "v1",
        RequestTimeoutSeconds = 5,
        MaxRetryAttempts = 2
    };

    [Fact]
    public async Task TestConnection_ReturnsTrue_OnSuccess()
    {
        var fake = new FakeAmazonS3Client
        {
            ListObjectsV2Handler = (req, ct) => Task.FromResult(new ListObjectsV2Response { HttpStatusCode = HttpStatusCode.OK })
        };

        using var transport = new S3ObjectStoreTransport(_settings, new FixedCredentialsProvider(), fake);

        bool result = await transport.TestConnectionAsync();
        Assert.True(result);
        Assert.Single(fake.CapturedListRequests);
        Assert.Equal("my-test-bucket", fake.CapturedListRequests[0].BucketName);
        Assert.Equal(1, fake.CapturedListRequests[0].MaxKeys);
    }

    [Fact]
    public async Task TestConnection_ThrowsCloudAuthException_On403Forbidden()
    {
        var fake = new FakeAmazonS3Client
        {
            ListObjectsV2Handler = (req, ct) => throw new AmazonS3Exception("Access Denied", ErrorType.Sender, "AccessDenied", "req1", HttpStatusCode.Forbidden)
        };

        using var transport = new S3ObjectStoreTransport(_settings, new FixedCredentialsProvider(), fake);

        var ex = await Assert.ThrowsAsync<CloudAuthException>(() => transport.TestConnectionAsync());
        Assert.Equal(403, ex.StatusCode);
        Assert.Equal(CloudErrorCode.Authentication, ex.ErrorCode);
    }

    [Fact]
    public async Task TestConnection_ThrowsCloudAuthException_On401Unauthorized()
    {
        var fake = new FakeAmazonS3Client
        {
            ListObjectsV2Handler = (req, ct) => throw new AmazonS3Exception("Invalid Access Key", ErrorType.Sender, "InvalidAccessKeyId", "req1", HttpStatusCode.Unauthorized)
        };

        using var transport = new S3ObjectStoreTransport(_settings, new FixedCredentialsProvider(), fake);

        var ex = await Assert.ThrowsAsync<CloudAuthException>(() => transport.TestConnectionAsync());
        Assert.Equal(401, ex.StatusCode);
        Assert.Equal(CloudErrorCode.Authentication, ex.ErrorCode);
    }

    [Fact]
    public async Task TestConnection_ThrowsCloudOfflineException_OnNetworkFailure()
    {
        var fake = new FakeAmazonS3Client
        {
            ListObjectsV2Handler = (req, ct) => throw new AmazonClientException("Failed to resolve host", new HttpRequestException("DNS resolution failed"))
        };

        using var transport = new S3ObjectStoreTransport(_settings, new FixedCredentialsProvider(), fake);

        var ex = await Assert.ThrowsAsync<CloudOfflineException>(() => transport.TestConnectionAsync());
        Assert.Equal(CloudErrorCode.Offline, ex.ErrorCode);
        Assert.IsType<AmazonClientException>(ex.InnerException);
        Assert.IsType<HttpRequestException>(ex.InnerException!.InnerException);
        Assert.Contains("DNS resolution failed", ex.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task HeadObject_ReturnsMetadata_On200_AndNull_On404()
    {
        var fake = new FakeAmazonS3Client
        {
            GetObjectMetadataHandler = (req, ct) =>
            {
                if (req.Key.Contains("exists.json"))
                {
                    var resp = new GetObjectMetadataResponse
                    {
                        HttpStatusCode = HttpStatusCode.OK,
                        ETag = "\"etag123\"",
                        ContentLength = 42,
                        LastModified = DateTime.UtcNow
                    };
                    resp.Headers.ContentType = "application/json";
                    return Task.FromResult(resp);
                }

                throw new AmazonS3Exception("The specified key does not exist.", ErrorType.Sender, "NoSuchKey", "req2", HttpStatusCode.NotFound);
            }
        };

        using var transport = new S3ObjectStoreTransport(_settings, new FixedCredentialsProvider(), fake);

        var meta = await transport.HeadObjectAsync("v1/exists.json");
        Assert.NotNull(meta);
        Assert.Equal("etag123", meta.ETag);
        Assert.Equal(42, meta.ContentLength);
        Assert.Equal("application/json", meta.ContentType);

        var notFound = await transport.HeadObjectAsync("v1/notfound.json");
        Assert.Null(notFound);
    }

    [Fact]
    public async Task GetObject_ReturnsResult_On200_AndNull_On404()
    {
        byte[] expectedPayload = Encoding.UTF8.GetBytes("hello s3 payload");

        var fake = new FakeAmazonS3Client
        {
            GetObjectHandler = (req, ct) =>
            {
                if (req.Key.Contains("test.txt"))
                {
                    var resp = new GetObjectResponse
                    {
                        HttpStatusCode = HttpStatusCode.OK,
                        ETag = "\"etag-txt\"",
                        ContentLength = expectedPayload.Length,
                        LastModified = DateTime.UtcNow,
                        ResponseStream = new MemoryStream(expectedPayload)
                    };
                    resp.Headers.ContentType = "text/plain";
                    return Task.FromResult(resp);
                }

                throw new AmazonS3Exception("Object not found", ErrorType.Sender, "NotFound", "req3", HttpStatusCode.NotFound);
            }
        };

        using var transport = new S3ObjectStoreTransport(_settings, new FixedCredentialsProvider(), fake);

        var res = await transport.GetObjectAsync("v1/test.txt");
        Assert.NotNull(res);
        Assert.Equal("etag-txt", res.Metadata.ETag);
        Assert.Equal(expectedPayload, res.Content);
        Assert.Equal("text/plain", res.Metadata.ContentType);

        var notFound = await transport.GetObjectAsync("v1/missing.txt");
        Assert.Null(notFound);
    }

    [Fact]
    public async Task PutImmutableObject_Succeeds_AndSendsBucketKeyAndIfNoneMatchAsterisk()
    {
        var fake = new FakeAmazonS3Client
        {
            GetObjectMetadataHandler = (req, ct) =>
                throw new AmazonS3Exception("Not found", ErrorType.Sender, "NoSuchKey", "req4", HttpStatusCode.NotFound),
            PutObjectHandler = (req, ct) =>
            {
                Assert.Equal("my-test-bucket", req.BucketName);
                Assert.Equal("v1/package.json", req.Key);
                Assert.Equal("*", req.IfNoneMatch);
                Assert.Null(req.IfMatch);
                Assert.Equal("application/json", req.ContentType);

                return Task.FromResult(new PutObjectResponse
                {
                    HttpStatusCode = HttpStatusCode.OK,
                    ETag = "\"etag-immutable\""
                });
            }
        };

        using var transport = new S3ObjectStoreTransport(_settings, new FixedCredentialsProvider(), fake);

        var meta = await transport.PutImmutableObjectAsync("v1/package.json", Encoding.UTF8.GetBytes("package content"));
        Assert.Equal("etag-immutable", meta.ETag);
        Assert.Single(fake.CapturedPutRequests);
    }

    [Fact]
    public async Task PutImmutableObject_ThrowsConflict_WhenObjectAlreadyExists_PreventingSilentOverwrite()
    {
        var fake = new FakeAmazonS3Client
        {
            GetObjectMetadataHandler = (req, ct) => Task.FromResult(new GetObjectMetadataResponse
            {
                HttpStatusCode = HttpStatusCode.OK,
                ETag = "\"existing-etag\"",
                ContentLength = 100
            })
        };

        using var transport = new S3ObjectStoreTransport(_settings, new FixedCredentialsProvider(), fake);

        var ex = await Assert.ThrowsAsync<CloudConflictException>(() =>
            transport.PutImmutableObjectAsync("v1/existing.json", Encoding.UTF8.GetBytes("new content")));

        Assert.Equal(CloudErrorCode.Conflict, ex.ErrorCode);
        Assert.Equal("existing-etag", ex.ActualETag);
        // Ensure PUT was never called because pre-flight detected collision
        Assert.Empty(fake.CapturedPutRequests);
    }

    [Fact]
    public async Task PutImmutableObject_ThrowsConflict_WhenParallelWriterCreatedObject_PreconditionFailed()
    {
        var fake = new FakeAmazonS3Client
        {
            GetObjectMetadataHandler = (req, ct) =>
                throw new AmazonS3Exception("Not found", ErrorType.Sender, "NoSuchKey", "req", HttpStatusCode.NotFound),
            PutObjectHandler = (req, ct) =>
                throw new AmazonS3Exception("At least one precondition failed", ErrorType.Sender, "PreconditionFailed", "req", HttpStatusCode.PreconditionFailed)
        };

        using var transport = new S3ObjectStoreTransport(_settings, new FixedCredentialsProvider(), fake);

        var ex = await Assert.ThrowsAsync<CloudConflictException>(() =>
            transport.PutImmutableObjectAsync("v1/parallel.json", Encoding.UTF8.GetBytes("body")));

        Assert.Equal(CloudErrorCode.Conflict, ex.ErrorCode);
        Assert.Equal(412, ex.StatusCode);
    }

    [Fact]
    public async Task PutConditionalPointer_WithNullExpectedETag_SetsIfNoneMatch_AndThrowsConflictOnPreconditionFailed()
    {
        var fake = new FakeAmazonS3Client
        {
            PutObjectHandler = (req, ct) =>
            {
                Assert.Equal("my-test-bucket", req.BucketName);
                Assert.Equal("v1/pointer.json", req.Key);
                Assert.Equal("*", req.IfNoneMatch);
                Assert.Null(req.IfMatch);

                throw new AmazonS3Exception("Precondition failed", ErrorType.Sender, "PreconditionFailed", "req", HttpStatusCode.PreconditionFailed);
            }
        };

        using var transport = new S3ObjectStoreTransport(_settings, new FixedCredentialsProvider(), fake);

        var ex = await Assert.ThrowsAsync<CloudConflictException>(() =>
            transport.PutConditionalPointerAsync("v1/pointer.json", Encoding.UTF8.GetBytes("ptr"), expectedETag: null));

        Assert.Equal(CloudErrorCode.Conflict, ex.ErrorCode);
        Assert.Equal(412, ex.StatusCode);
    }

    [Fact]
    public async Task PutConditionalPointer_WithExpectedETag_SetsIfMatch_AndSucceedsOn200()
    {
        var fake = new FakeAmazonS3Client
        {
            PutObjectHandler = (req, ct) =>
            {
                Assert.Equal("my-test-bucket", req.BucketName);
                Assert.Equal("v1/pointer.json", req.Key);
                Assert.Equal("\"etag-v1\"", req.IfMatch);
                Assert.Null(req.IfNoneMatch);

                return Task.FromResult(new PutObjectResponse
                {
                    HttpStatusCode = HttpStatusCode.OK,
                    ETag = "\"etag-v2\""
                });
            }
        };

        using var transport = new S3ObjectStoreTransport(_settings, new FixedCredentialsProvider(), fake);

        var meta = await transport.PutConditionalPointerAsync("v1/pointer.json", Encoding.UTF8.GetBytes("ptr"), expectedETag: "etag-v1");
        Assert.Equal("etag-v2", meta.ETag);
        Assert.Single(fake.CapturedPutRequests);
    }

    [Fact]
    public async Task PutConditionalPointer_WithExpectedETag_ThrowsConflict_OnPreconditionFailed()
    {
        var fake = new FakeAmazonS3Client
        {
            PutObjectHandler = (req, ct) =>
            {
                Assert.Equal("\"etag-v1\"", req.IfMatch);
                throw new AmazonS3Exception("Precondition failed", ErrorType.Sender, "PreconditionFailed", "req", HttpStatusCode.PreconditionFailed);
            }
        };

        using var transport = new S3ObjectStoreTransport(_settings, new FixedCredentialsProvider(), fake);

        var ex = await Assert.ThrowsAsync<CloudConflictException>(() =>
            transport.PutConditionalPointerAsync("v1/pointer.json", Encoding.UTF8.GetBytes("ptr"), expectedETag: "etag-v1"));

        Assert.Equal(CloudErrorCode.Conflict, ex.ErrorCode);
        Assert.Equal("etag-v1", ex.ExpectedETag);
        Assert.Equal(412, ex.StatusCode);
    }

    [Fact]
    public async Task PutObjectRequest_PassesLogicalKeyWithoutManualDoubleUrlEncoding_ForUnicodeSpacesAndPercents()
    {
        string complexKey = "v1/devices/dev-1/заметки пользователя/отчёт 100% готов.json";

        var fake = new FakeAmazonS3Client
        {
            GetObjectMetadataHandler = (req, ct) =>
            {
                // Head should also receive the exact logical key
                Assert.Equal(complexKey, req.Key);
                throw new AmazonS3Exception("Not found", ErrorType.Sender, "NoSuchKey", "req", HttpStatusCode.NotFound);
            },
            PutObjectHandler = (req, ct) =>
            {
                // Verify that the logical key is passed directly to the SDK without double URL-encoding
                Assert.Equal(complexKey, req.Key);
                Assert.DoesNotContain("%20", req.Key);
                Assert.DoesNotContain("%25", req.Key);
                Assert.DoesNotContain("%D0", req.Key);

                return Task.FromResult(new PutObjectResponse
                {
                    HttpStatusCode = HttpStatusCode.OK,
                    ETag = "\"etag-complex\""
                });
            }
        };

        using var transport = new S3ObjectStoreTransport(_settings, new FixedCredentialsProvider(), fake);

        var meta = await transport.PutImmutableObjectAsync(complexKey, Encoding.UTF8.GetBytes("test content"));
        Assert.Equal("etag-complex", meta.ETag);

        Assert.Single(fake.CapturedHeadRequests);
        Assert.Equal(complexKey, fake.CapturedHeadRequests[0].Key);

        Assert.Single(fake.CapturedPutRequests);
        Assert.Equal(complexKey, fake.CapturedPutRequests[0].Key);
    }

    [Fact]
    public async Task ListObjects_MapsS3ObjectsCorrectly_AndPassesPrefixAndMaxKeys()
    {
        var fake = new FakeAmazonS3Client
        {
            ListObjectsV2Handler = (req, ct) =>
            {
                Assert.Equal("my-test-bucket", req.BucketName);
                Assert.Equal("v1/devices/", req.Prefix);
                Assert.Equal(500, req.MaxKeys);

                var resp = new ListObjectsV2Response
                {
                    HttpStatusCode = HttpStatusCode.OK,
                    S3Objects = new List<S3Object>
                    {
                        new()
                        {
                            Key = "v1/devices/dev1/pointer.json",
                            ETag = "\"etag1\"",
                            Size = 128,
                            LastModified = new DateTime(2026, 9, 7, 5, 0, 0, DateTimeKind.Utc)
                        },
                        new()
                        {
                            Key = "v1/devices/dev1/packages/pkg1.json",
                            ETag = "\"etag2\"",
                            Size = 1024,
                            LastModified = new DateTime(2026, 9, 7, 5, 1, 0, DateTimeKind.Utc)
                        }
                    }
                };
                return Task.FromResult(resp);
            }
        };

        using var transport = new S3ObjectStoreTransport(_settings, new FixedCredentialsProvider(), fake);

        var items = await transport.ListObjectsAsync("v1/devices/", maxKeys: 500);
        Assert.Equal(2, items.Count);

        Assert.Equal("v1/devices/dev1/pointer.json", items[0].Key);
        Assert.Equal("etag1", items[0].ETag);
        Assert.Equal(128, items[0].Size);

        Assert.Equal("v1/devices/dev1/packages/pkg1.json", items[1].Key);
        Assert.Equal("etag2", items[1].ETag);
        Assert.Equal(1024, items[1].Size);
    }

    [Fact]
    public async Task DeleteObject_ReturnsTrue_OnSuccess_AndFalse_OnNotFound()
    {
        var fake = new FakeAmazonS3Client
        {
            DeleteObjectHandler = (req, ct) =>
            {
                if (req.Key.Contains("missing.json"))
                {
                    throw new AmazonS3Exception("Not found", ErrorType.Sender, "NoSuchKey", "req", HttpStatusCode.NotFound);
                }

                return Task.FromResult(new DeleteObjectResponse { HttpStatusCode = HttpStatusCode.OK });
            }
        };

        using var transport = new S3ObjectStoreTransport(_settings, new FixedCredentialsProvider(), fake);

        bool deleted = await transport.DeleteObjectAsync("v1/item.json");
        Assert.True(deleted);

        bool missingDeleted = await transport.DeleteObjectAsync("v1/missing.json");
        Assert.False(missingDeleted);
    }

    [Fact]
    public async Task ErrorMapping_Maps429AndSlowDown_ToCloudQuotaException()
    {
        var fake = new FakeAmazonS3Client
        {
            GetObjectHandler = (req, ct) =>
                throw new AmazonS3Exception("Reduce request rate", ErrorType.Sender, "SlowDown", "req", (HttpStatusCode)429)
        };

        using var transport = new S3ObjectStoreTransport(_settings, new FixedCredentialsProvider(), fake);

        var ex = await Assert.ThrowsAsync<CloudQuotaException>(() => transport.GetObjectAsync("v1/rate.json"));
        Assert.Equal(CloudErrorCode.QuotaExceeded, ex.ErrorCode);
        Assert.Equal(429, ex.StatusCode);
    }

    [Fact]
    public async Task ErrorMapping_Maps503SlowDown_ToCloudQuotaException()
    {
        var fake = new FakeAmazonS3Client
        {
            GetObjectHandler = (req, ct) =>
                throw new AmazonS3Exception("Please slow down", ErrorType.Receiver, "SlowDown", "req", HttpStatusCode.ServiceUnavailable)
        };

        using var transport = new S3ObjectStoreTransport(_settings, new FixedCredentialsProvider(), fake);

        var ex = await Assert.ThrowsAsync<CloudQuotaException>(() => transport.GetObjectAsync("v1/rate503.json"));
        Assert.Equal(CloudErrorCode.QuotaExceeded, ex.ErrorCode);
    }

    [Fact]
    public async Task ErrorMapping_Maps409Conflict_ToCloudConflictException()
    {
        var fake = new FakeAmazonS3Client
        {
            GetObjectHandler = (req, ct) =>
                throw new AmazonS3Exception("Bucket already exists or concurrent conflict", ErrorType.Sender, "Conflict", "req", HttpStatusCode.Conflict)
        };

        using var transport = new S3ObjectStoreTransport(_settings, new FixedCredentialsProvider(), fake);

        var ex = await Assert.ThrowsAsync<CloudConflictException>(() => transport.GetObjectAsync("v1/conflict.json"));
        Assert.Equal(CloudErrorCode.Conflict, ex.ErrorCode);
        Assert.Equal(409, ex.StatusCode);
    }

    [Fact]
    public async Task RetryPolicy_RetriesIdempotentReadOnTransportReset_ThenSucceeds()
    {
        int attempt = 0;
        var fake = new FakeAmazonS3Client
        {
            GetObjectHandler = (req, ct) =>
            {
                attempt++;
                if (attempt < 3)
                {
                    throw new IOException("Unable to read data from the transport connection: connection reset.");
                }

                return Task.FromResult(new GetObjectResponse
                {
                    HttpStatusCode = HttpStatusCode.OK,
                    ETag = "\"etag-reset-retry\"",
                    ResponseStream = new MemoryStream(Encoding.UTF8.GetBytes("reset-retry-ok"))
                });
            }
        };

        using var transport = new S3ObjectStoreTransport(_settings, new FixedCredentialsProvider(), fake);

        var res = await transport.GetObjectAsync("v1/reset-retry.json");
        Assert.NotNull(res);
        Assert.Equal(3, attempt);
        Assert.Equal("reset-retry-ok", Encoding.UTF8.GetString(res.Content));
    }

    [Fact]
    public async Task RetryPolicy_RetriesIdempotentReadOnRequestTimeout_ThenSucceeds()
    {
        int attempt = 0;
        var fake = new FakeAmazonS3Client
        {
            GetObjectHandler = (req, ct) =>
            {
                attempt++;
                if (attempt < 3)
                {
                    throw new TaskCanceledException("A task was canceled.");
                }

                return Task.FromResult(new GetObjectResponse
                {
                    HttpStatusCode = HttpStatusCode.OK,
                    ETag = "\"etag-timeout-retry\"",
                    ResponseStream = new MemoryStream(Encoding.UTF8.GetBytes("timeout-retry-ok"))
                });
            }
        };

        using var transport = new S3ObjectStoreTransport(_settings, new FixedCredentialsProvider(), fake);

        var res = await transport.GetObjectAsync("v1/timeout-retry.json");
        Assert.NotNull(res);
        Assert.Equal(3, attempt);
        Assert.Equal("timeout-retry-ok", Encoding.UTF8.GetString(res.Content));
    }

    [Fact]
    public async Task RetryPolicy_MapsSdkTimeoutToCloudTimeout_AfterIdempotentRetriesExhausted()
    {
        int attempt = 0;
        var fake = new FakeAmazonS3Client
        {
            GetObjectHandler = (req, ct) =>
            {
                attempt++;
                throw new TaskCanceledException("A task was canceled.");
            }
        };

        using var transport = new S3ObjectStoreTransport(_settings, new FixedCredentialsProvider(), fake);

        var ex = await Assert.ThrowsAsync<CloudStorageException>(() => transport.GetObjectAsync("v1/timeout-exhausted.json"));
        Assert.Equal(CloudErrorCode.Timeout, ex.ErrorCode);
        Assert.Equal(3, attempt);
        Assert.IsType<TaskCanceledException>(ex.InnerException);
        Assert.DoesNotContain("v1/timeout-exhausted.json", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancellation_DoesNotTreatCallerCancelAsRetryableTimeout()
    {
        int attempt = 0;
        using var cts = new CancellationTokenSource();
        var fake = new FakeAmazonS3Client
        {
            GetObjectHandler = (req, ct) =>
            {
                attempt++;
                cts.Cancel();
                throw new TaskCanceledException("A task was canceled.");
            }
        };

        using var transport = new S3ObjectStoreTransport(_settings, new FixedCredentialsProvider(), fake);

        await Assert.ThrowsAsync<OperationCanceledException>(() => transport.GetObjectAsync("v1/cancel.json", cts.Token));
        Assert.Equal(1, attempt);
    }

    [Fact]
    public async Task NonIdempotentWrite_DoesNotRetryPutOnTimeout()
    {
        int putAttempts = 0;
        var fake = new FakeAmazonS3Client
        {
            GetObjectMetadataHandler = (req, ct) =>
                throw new AmazonS3Exception("Not found", ErrorType.Sender, "NoSuchKey", "req", HttpStatusCode.NotFound),
            PutObjectHandler = (req, ct) =>
            {
                putAttempts++;
                throw new TaskCanceledException("A task was canceled.");
            }
        };

        using var transport = new S3ObjectStoreTransport(_settings, new FixedCredentialsProvider(), fake);

        var ex = await Assert.ThrowsAsync<CloudStorageException>(() =>
            transport.PutImmutableObjectAsync("v1/put-timeout.json", new byte[] { 1, 2, 3 }));
        Assert.Equal(CloudErrorCode.Timeout, ex.ErrorCode);
        Assert.Equal(1, putAttempts);
    }

    [Fact]
    public async Task RetryPolicy_RetriesIdempotentReadOnTransient503_AndEventuallySucceeds()
    {
        int attempt = 0;
        var fake = new FakeAmazonS3Client
        {
            GetObjectHandler = (req, ct) =>
            {
                attempt++;
                if (attempt < 3)
                {
                    throw new AmazonS3Exception("Service Unavailable", ErrorType.Receiver, "ServiceUnavailable", "req", HttpStatusCode.ServiceUnavailable);
                }

                return Task.FromResult(new GetObjectResponse
                {
                    HttpStatusCode = HttpStatusCode.OK,
                    ETag = "\"etag-retry\"",
                    ResponseStream = new MemoryStream(Encoding.UTF8.GetBytes("retry success"))
                });
            }
        };

        using var transport = new S3ObjectStoreTransport(_settings, new FixedCredentialsProvider(), fake);

        var res = await transport.GetObjectAsync("v1/retry.json");
        Assert.NotNull(res);
        Assert.Equal(3, attempt);
        Assert.Equal("retry success", Encoding.UTF8.GetString(res.Content));
    }

    [Fact]
    public async Task NonIdempotentWrite_DoesNotRetryOn500_FailsImmediately()
    {
        int putAttempts = 0;
        var fake = new FakeAmazonS3Client
        {
            GetObjectMetadataHandler = (req, ct) =>
                throw new AmazonS3Exception("Not found", ErrorType.Sender, "NoSuchKey", "req", HttpStatusCode.NotFound),
            PutObjectHandler = (req, ct) =>
            {
                putAttempts++;
                throw new AmazonS3Exception("Internal Server Error", ErrorType.Receiver, "InternalError", "req", HttpStatusCode.InternalServerError);
            }
        };

        using var transport = new S3ObjectStoreTransport(_settings, new FixedCredentialsProvider(), fake);

        await Assert.ThrowsAsync<CloudStorageException>(() =>
            transport.PutImmutableObjectAsync("v1/no-retry.json", new byte[] { 1, 2, 3 }));

        // Only 1 PUT attempt must be made (non-idempotent operation is NOT retried blindly)
        Assert.Equal(1, putAttempts);
    }

    [Fact]
    public async Task Cancellation_HonorsCancellationToken()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel(); // already canceled

        var fake = new FakeAmazonS3Client();
        using var transport = new S3ObjectStoreTransport(_settings, new FixedCredentialsProvider(), fake);

        await Assert.ThrowsAsync<OperationCanceledException>(() => transport.TestConnectionAsync(cts.Token));
    }

    [Fact]
    public async Task Security_AuthorizationHeaderAndSecretKey_AreNeverPresentInExceptionMessage()
    {
        string secretKey = "SuperSecretAWSKeyDoNotLeak";
        var fake = new FakeAmazonS3Client
        {
            ListObjectsV2Handler = (req, ct) =>
                throw new AmazonS3Exception($"Signature validation failed for AWS4-HMAC-SHA256 SecretAccessKey={secretKey}",
                    ErrorType.Sender, "SignatureDoesNotMatch", "req", HttpStatusCode.Forbidden)
        };

        using var transport = new S3ObjectStoreTransport(_settings, new FixedCredentialsProvider("access123", secretKey), fake);

        var ex = await Assert.ThrowsAsync<CloudAuthException>(() => transport.TestConnectionAsync());
        Assert.DoesNotContain(secretKey, ex.Message);
        Assert.Contains("AWS4-HMAC-SHA256", ex.Message, StringComparison.Ordinal);
        Assert.Contains("SecretAccessKey=[REDACTED]", ex.Message, StringComparison.Ordinal);
        Assert.Null(ex.InnerException);
        Assert.DoesNotContain(secretKey, ex.ToString());
    }

    [Fact]
    public async Task ErrorMapping_RedactsSignedUrlsAccessKeysAndDoesNotAttachSdkInnerExceptions()
    {
        const string accessKey = "AKIAIOSFODNN7EXAMPLE";
        const string secret = "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY";
        string leaky = $"GET https://s3.yandexcloud.net/my-test-bucket/v1/pkg.json?AWSAccessKeyId={accessKey}&Signature=deadbeef&X-Amz-Security-Token={secret} Authorization: AWS4-HMAC-SHA256 Credential={accessKey}/20260101/ru-central1/s3/aws4_request";

        var fake = new FakeAmazonS3Client
        {
            GetObjectHandler = (req, ct) =>
                throw new AmazonS3Exception(leaky, ErrorType.Sender, "InvalidAccessKeyId", "req", HttpStatusCode.Forbidden)
        };

        using var transport = new S3ObjectStoreTransport(_settings, new FixedCredentialsProvider(accessKey, secret), fake);

        var ex = await Assert.ThrowsAsync<CloudAuthException>(() => transport.GetObjectAsync("v1/pkg.json"));
        Assert.Null(ex.InnerException);
        Assert.DoesNotContain(accessKey, ex.Message);
        Assert.DoesNotContain(secret, ex.Message);
        Assert.DoesNotContain("deadbeef", ex.Message);
        Assert.Contains("AWSAccessKeyId=[REDACTED]", ex.Message, StringComparison.Ordinal);
        Assert.Contains("https://s3.yandexcloud.net/my-test-bucket/v1/pkg.json", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(accessKey, ex.ToString());
        Assert.DoesNotContain(secret, ex.ToString());
    }

    [Fact]
    public async Task ErrorMapping_RedactsSecretQueryParameters_PreservesSafeQueryAndErrorCode()
    {
        var fake = new FakeAmazonS3Client
        {
            GetObjectHandler = (req, ct) =>
                throw new AmazonS3Exception(
                    "No such key at https://s3.yandexcloud.net/my-test-bucket/v1/pkg.json?versionId=abc123&X-Amz-Date=20260101T000000Z&session=tok-PLAIN-NOTE-BODY",
                    ErrorType.Sender,
                    "NoSuchUpload",
                    "req",
                    HttpStatusCode.BadRequest)
        };

        using var transport = new S3ObjectStoreTransport(_settings, new FixedCredentialsProvider(), fake);

        var ex = await Assert.ThrowsAsync<CloudStorageException>(() => transport.GetObjectAsync("v1/pkg.json"));
        Assert.Equal(400, ex.StatusCode);
        Assert.Contains("NoSuchUpload", ex.Message, StringComparison.Ordinal);
        Assert.Contains("versionId=abc123", ex.Message, StringComparison.Ordinal);
        Assert.Contains("X-Amz-Date=20260101T000000Z", ex.Message, StringComparison.Ordinal);
        Assert.Contains("session=[REDACTED]", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("tok-PLAIN-NOTE-BODY", ex.Message);
        Assert.DoesNotContain("?[redacted]", ex.Message, StringComparison.Ordinal);
        Assert.Null(ex.InnerException);
    }

    [Fact]
    public async Task ErrorMapping_Timeout_PreservesInnerCause_AndRedactsInnerSecretsInToString()
    {
        const string accessKey = "AKIAIOSFODNN7EXAMPLE";
        var fake = new FakeAmazonS3Client
        {
            GetObjectHandler = (req, ct) =>
                throw new AmazonClientException(
                    $"timeout GET https://s3.yandexcloud.net/b/k.json?AWSAccessKeyId={accessKey}&Signature=deadbeef",
                    new TimeoutException($"signed Authorization: AWS4-HMAC-SHA256 Credential={accessKey}/20260101/ru-central1/s3/aws4_request"))
        };

        using var transport = new S3ObjectStoreTransport(_settings, new FixedCredentialsProvider(accessKey, "secret"), fake);

        var ex = await Assert.ThrowsAsync<CloudStorageException>(() => transport.GetObjectAsync("v1/timeout-inner.json"));
        Assert.Equal(CloudErrorCode.Timeout, ex.ErrorCode);
        Assert.IsType<AmazonClientException>(ex.InnerException);
        Assert.IsType<TimeoutException>(ex.InnerException!.InnerException);
        Assert.Equal("Превышено время ожидания ответа от S3 хранилища.", ex.Message);
        Assert.DoesNotContain(accessKey, ex.ToString());
        Assert.DoesNotContain("deadbeef", ex.ToString());
        Assert.Contains("TimeoutException", ex.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task PutRequests_DoNotSetUserMetadataHeaders_AndKeepCallerContentTypeOnly()
    {
        const string notePlaintext = "UNIQUE-NOTE-PLAINTEXT-LEAK-MARKER";
        const string syncPassword = "sync-password-should-not-be-a-header";
        const string recovery = "RK-recovery-material-must-not-be-metadata";

        var fake = new FakeAmazonS3Client
        {
            GetObjectMetadataHandler = (req, ct) =>
                throw new AmazonS3Exception("Not found", ErrorType.Sender, "NoSuchKey", "req", HttpStatusCode.NotFound),
            PutObjectHandler = (req, ct) =>
            {
                AssertNoCustomAmzMeta(req);
                Assert.Equal("application/json", req.ContentType);
                Assert.DoesNotContain(notePlaintext, req.Key);
                Assert.DoesNotContain(notePlaintext, req.ContentType ?? string.Empty);
                Assert.DoesNotContain(syncPassword, req.Key);
                Assert.DoesNotContain(recovery, req.Key);
                Assert.Equal("v1/devices/aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa/packages/bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb.json", req.Key);

                return Task.FromResult(new PutObjectResponse
                {
                    HttpStatusCode = HttpStatusCode.OK,
                    ETag = "\"etag-no-meta\""
                });
            }
        };

        using var transport = new S3ObjectStoreTransport(_settings, new FixedCredentialsProvider(), fake);

        var meta = await transport.PutImmutableObjectAsync(
            "v1/devices/aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa/packages/bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb.json",
            Encoding.UTF8.GetBytes("ciphertext-not-asserted-here"),
            "application/json");

        Assert.Equal("etag-no-meta", meta.ETag);
        Assert.Equal("application/json", meta.ContentType);
        Assert.DoesNotContain(notePlaintext, meta.Key);
        Assert.DoesNotContain(notePlaintext, meta.ETag);
        Assert.DoesNotContain(notePlaintext, meta.ContentType ?? string.Empty);

        fake.PutObjectHandler = (req, ct) =>
        {
            AssertNoCustomAmzMeta(req);
            Assert.Equal("application/octet-stream", req.ContentType);
            return Task.FromResult(new PutObjectResponse { HttpStatusCode = HttpStatusCode.OK, ETag = "\"etag-blob\"" });
        };

        var blobMeta = await transport.PutConditionalPointerAsync(
            "v1/blobs/" + new string('a', 64) + ".bin",
            new byte[] { 1, 2, 3 },
            expectedETag: null,
            "application/octet-stream");
        Assert.Equal("application/octet-stream", blobMeta.ContentType);
        Assert.Equal(2, fake.CapturedPutRequests.Count);
    }

    private static void AssertNoCustomAmzMeta(PutObjectRequest req)
    {
        Assert.NotNull(req.Metadata);
        Assert.Equal(0, req.Metadata.Count);
        if (req.Headers != null)
        {
            foreach (string name in req.Headers.Keys)
            {
                Assert.False(
                    name.StartsWith("x-amz-meta", StringComparison.OrdinalIgnoreCase),
                    "PutObjectRequest must not send user metadata headers.");
                Assert.DoesNotContain("UNIQUE-NOTE-PLAINTEXT-LEAK-MARKER", req.Headers[name] ?? string.Empty);
            }
        }
    }

    [Fact]
    public void CreateS3Config_ConfiguresYandexObjectStorageSettingsCorrectly()
    {
        var defaultSettings = new SyncCloudSettings();
        var config = S3ObjectStoreTransport.CreateS3Config(defaultSettings);

        Assert.Equal("https://s3.yandexcloud.net", config.ServiceURL.TrimEnd('/'));
        Assert.Equal("ru-central1", config.AuthenticationRegion);
        Assert.True(config.ForcePathStyle);
        Assert.Equal(0, config.MaxErrorRetry);

        // Can override for local testing / mock servers
        var customSettings = new SyncCloudSettings
        {
            Endpoint = "http://127.0.0.1:9000",
            Region = "custom-region",
            RequestTimeoutSeconds = 10
        };
        var customConfig = S3ObjectStoreTransport.CreateS3Config(customSettings);

        Assert.Equal("http://127.0.0.1:9000", customConfig.ServiceURL.TrimEnd('/'));
        Assert.Equal("custom-region", customConfig.AuthenticationRegion);
        Assert.True(customConfig.ForcePathStyle);
        Assert.Equal(TimeSpan.FromSeconds(10), customConfig.Timeout);
    }

    [Fact]
    public async Task ListObjectsV2Async_ConfiguresDelimiterAndPagination_ReturnsCommonPrefixesAndTruncated()
    {
        var fake = new FakeAmazonS3Client
        {
            ListObjectsV2Handler = (req, ct) =>
            {
                Assert.Equal("my-test-bucket", req.BucketName);
                Assert.Equal("v1/devices/", req.Prefix);
                Assert.Equal("/", req.Delimiter);
                Assert.Equal("token-123", req.ContinuationToken);
                Assert.Equal(50, req.MaxKeys);

                return Task.FromResult(new ListObjectsV2Response
                {
                    HttpStatusCode = HttpStatusCode.OK,
                    IsTruncated = true,
                    NextContinuationToken = "token-456",
                    CommonPrefixes = new List<string>
                    {
                        "v1/devices/11111111-1111-1111-1111-111111111111/",
                        "v1/devices/22222222-2222-2222-2222-222222222222/"
                    },
                    S3Objects = new List<S3Object>
                    {
                        new S3Object
                        {
                            Key = "v1/devices/root.json",
                            ETag = "\"etag-root\"",
                            Size = 42,
                            LastModified = DateTime.UtcNow
                        }
                    }
                });
            }
        };

        using var transport = new S3ObjectStoreTransport(_settings, new FixedCredentialsProvider(), fake);

        var result = await transport.ListObjectsV2Async(new StorageListRequest
        {
            Prefix = "v1/devices/",
            Delimiter = "/",
            ContinuationToken = "token-123",
            MaxKeys = 50
        });

        Assert.True(result.IsTruncated);
        Assert.Equal("token-456", result.NextContinuationToken);
        Assert.Equal(2, result.CommonPrefixes.Count);
        Assert.Equal("v1/devices/11111111-1111-1111-1111-111111111111/", result.CommonPrefixes[0]);
        Assert.Equal("v1/devices/22222222-2222-2222-2222-222222222222/", result.CommonPrefixes[1]);
        Assert.Single(result.Objects);
        Assert.Equal("v1/devices/root.json", result.Objects[0].Key);
    }
}

