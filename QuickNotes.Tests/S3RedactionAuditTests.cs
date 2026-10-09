using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.EntityFrameworkCore;
using QuickNotes.App.Data;
using QuickNotes.App.Models;
using QuickNotes.App.Models.Sync;
using QuickNotes.App.Services;
using QuickNotes.App.Services.Sync;
using Xunit;

namespace QuickNotes.Tests;

[TestCategory(TestCategories.Integration)]
public class S3RedactionAuditTests : IDisposable
{
    private const string NotePlaintext = "UNIQUE-USER-NOTE-PLAINTEXT-S3-AUDIT";
    private const string SyncPassword = "unique-sync-password-S3-AUDIT";
    private const string AccessKey = "AKIAIOSFODNN7EXAMPLE";
    private const string SecretKey = "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY";
    private const string RecoveryMaterial = "RK-recovery-wrap-material-S3-AUDIT";

    private readonly List<string> _tempFiles = new();
    private readonly List<string> _tempDirs = new();

    public void Dispose()
    {
        SqliteTestUtil.ReleasePools();
        foreach (string file in _tempFiles)
        {
            SqliteTestUtil.TryDeleteFileAndSiblings(file);
        }

        foreach (string dir in _tempDirs)
        {
            try
            {
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, recursive: true);
                }
            }
            catch
            {
            }
        }
    }

    [Fact]
    public void Source_S3TransportDoesNotConstructUserMetadataHeadersOrSecretLogs()
    {
        string root = CiReportingConfigTests.FindRepoRoot();
        string transport = File.ReadAllText(Path.Combine(root, "QuickNotes.App", "Services", "Sync", "S3ObjectStoreTransport.cs"));
        string models = File.ReadAllText(Path.Combine(root, "QuickNotes.App", "Models", "Sync", "StorageObjectModels.cs"));
        string contract = File.ReadAllText(Path.Combine(root, "QuickNotes.App", "Services", "Sync", "ICloudObjectStoreTransport.cs"));
        string keys = File.ReadAllText(Path.Combine(root, "QuickNotes.App", "Services", "Sync", "SyncObjectKeyHelper.cs"));

        Assert.DoesNotContain("x-amz-meta", transport, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Metadata.Add", transport, StringComparison.Ordinal);
        Assert.DoesNotContain("Metadata[", transport, StringComparison.Ordinal);
        Assert.DoesNotContain("Headers.Add", transport, StringComparison.Ordinal);
        Assert.Contains("SanitizeError", transport, StringComparison.Ordinal);
        Assert.Contains("CloudErrorSanitizer.SanitizeProviderError", transport, StringComparison.Ordinal);
        string errorLog = File.ReadAllText(Path.Combine(root, "QuickNotes.App", "Services", "ErrorLogService.cs"));
        Assert.Contains("CloudErrorSanitizer.FormatExceptionForLog", errorLog, StringComparison.Ordinal);
        Assert.Contains("CloudErrorSanitizer.RedactSecrets", errorLog, StringComparison.Ordinal);
        Assert.DoesNotContain("using QuickNotes.App.Services.Sync", errorLog, StringComparison.Ordinal);

        Assert.DoesNotContain("Dictionary<string, string>", models, StringComparison.Ordinal);
        Assert.Contains("public string Key", models, StringComparison.Ordinal);
        Assert.Contains("public string ETag", models, StringComparison.Ordinal);
        Assert.Contains("public string? ContentType", models, StringComparison.Ordinal);
        Assert.DoesNotContain("x-amz-meta", contract, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("devices/{deviceId:D}/packages/{packageId:D}.json", keys, StringComparison.Ordinal);
        Assert.Contains("blobs/{sha256Hex.ToLowerInvariant()}.bin", keys, StringComparison.Ordinal);
        Assert.DoesNotContain("note.Text", keys, StringComparison.Ordinal);
        Assert.DoesNotContain("Title", keys, StringComparison.Ordinal);
    }

    [Fact]
    public void Source_ProductionSyncPutsOnlyJsonOrOctetStreamContentTypes()
    {
        string root = CiReportingConfigTests.FindRepoRoot();
        string syncDir = Path.Combine(root, "QuickNotes.App", "Services", "Sync");
        var hits = new List<string>();
        foreach (string file in Directory.GetFiles(syncDir, "*.cs"))
        {
            string text = File.ReadAllText(file);
            foreach (string token in new[] { "PutImmutableObjectAsync(", "PutConditionalPointerAsync(" })
            {
                int idx = 0;
                while ((idx = text.IndexOf(token, idx, StringComparison.Ordinal)) >= 0)
                {
                    int end = text.IndexOf(';', idx);
                    string call = end > idx ? text[idx..end] : text[idx..Math.Min(text.Length, idx + 280)];
                    if (call.Contains("contentType", StringComparison.OrdinalIgnoreCase)
                        && !call.Contains("\"application/json\"", StringComparison.Ordinal)
                        && !call.Contains("\"application/octet-stream\"", StringComparison.Ordinal))
                    {
                        hits.Add(Path.GetFileName(file) + ": " + call.ReplaceLineEndings(" "));
                    }

                    idx += token.Length;
                }
            }
        }

        Assert.True(hits.Count == 0, string.Join(Environment.NewLine, hits));
    }

    [Theory]
    [InlineData("GET https://s3.yandexcloud.net/b/k?AWSAccessKeyId=AKIAIOSFODNN7EXAMPLE&Signature=deadbeef", "AKIAIOSFODNN7EXAMPLE")]
    [InlineData("Authorization: AWS4-HMAC-SHA256 Credential=AKIAIOSFODNN7EXAMPLE/20260101/ru-central1/s3/aws4_request", "AKIAIOSFODNN7EXAMPLE")]
    [InlineData("SecretAccessKey=wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY leaked", "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY")]
    [InlineData("https://user:sync-password-S3-AUDIT@s3.yandexcloud.net/bucket", "sync-password-S3-AUDIT")]
    public void CloudErrorSanitizer_RedactsCredentialsAndUrlUserInfo(string raw, string secret)
    {
        string redacted = CloudErrorSanitizer.RedactSecrets(raw);
        Assert.DoesNotContain(secret, redacted);
        string provider = CloudErrorSanitizer.SanitizeProviderError(raw, "AccessDenied");
        Assert.DoesNotContain(secret, provider);
        Assert.DoesNotContain(secret, CloudErrorSanitizer.SanitizeDiagnosticDetail(raw));
    }

    [Fact]
    public void CloudErrorSanitizer_PreservesSafeQueryParameters()
    {
        string raw =
            "No such key at https://s3.yandexcloud.net/b/k.json?versionId=abc123&X-Amz-Date=20260101T000000Z&X-Amz-Expires=86400&AWSAccessKeyId=" +
            AccessKey + "&Signature=deadbeef";
        string redacted = CloudErrorSanitizer.RedactSecrets(raw);
        Assert.Contains("versionId=abc123", redacted, StringComparison.Ordinal);
        Assert.Contains("X-Amz-Date=20260101T000000Z", redacted, StringComparison.Ordinal);
        Assert.Contains("X-Amz-Expires=86400", redacted, StringComparison.Ordinal);
        Assert.Contains("AWSAccessKeyId=[REDACTED]", redacted, StringComparison.Ordinal);
        Assert.Contains("Signature=[REDACTED]", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain(AccessKey, redacted);
        Assert.DoesNotContain("deadbeef", redacted);
        Assert.DoesNotContain("?[redacted]", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void CloudErrorSanitizer_ProviderError_KeepsSafeContextWhenSigningMaterialPresent()
    {
        string raw =
            "Access Denied for object v1/pkg.json Authorization: AWS4-HMAC-SHA256 Credential=" + AccessKey +
            "/20260101/ru-central1/s3/aws4_request RequestId=abc-safe-id";
        string provider = CloudErrorSanitizer.SanitizeProviderError(raw, "AccessDenied");
        Assert.Contains("AccessDenied", provider, StringComparison.Ordinal);
        Assert.Contains("Access Denied for object v1/pkg.json", provider, StringComparison.Ordinal);
        Assert.Contains("RequestId=abc-safe-id", provider, StringComparison.Ordinal);
        Assert.DoesNotContain(AccessKey, provider);
        Assert.NotEqual("[AccessDenied]", provider);
    }

    [Fact]
    public void CloudErrorSanitizer_AuthorizationMatch_DoesNotConsumeFollowingJsonFields()
    {
        string raw = "{\"Authorization\":\"AWS4-HMAC-SHA256 Credential=" + AccessKey +
                     "/20260101/ru-central1/s3/aws4_request\",\"RequestId\":\"req-123\",\"Note\":\"" + NotePlaintext + "\"}";
        string redacted = CloudErrorSanitizer.RedactSecrets(raw);
        Assert.Contains("\"RequestId\":\"req-123\"", redacted, StringComparison.Ordinal);
        Assert.Contains("\"Authorization\":\"[REDACTED]\"", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain(AccessKey, redacted);
        Assert.Contains(NotePlaintext, redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void CloudErrorSanitizer_LongAdversarialInput_CompletesAndRedacts()
    {
        var builder = new StringBuilder(capacity: 120_000);
        builder.Append("start ");
        builder.Append('A', 80_000);
        builder.Append(" https://s3.yandexcloud.net/b/k?versionId=keep-me&AWSAccessKeyId=");
        builder.Append(AccessKey);
        builder.Append("&Signature=");
        builder.Append('d', 2_000);
        builder.Append(' ');
        for (int i = 0; i < 200; i++)
        {
            builder.Append("Authorization: AWS4-HMAC-SHA256 Credential=");
            builder.Append(AccessKey);
            builder.Append("/x RequestId=keep-");
            builder.Append(i);
            builder.Append(' ');
        }

        builder.Append(" SecretAccessKey=");
        builder.Append(SecretKey);
        string raw = builder.ToString();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        string redacted = CloudErrorSanitizer.RedactSecrets(raw);
        sw.Stop();
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), $"redaction took {sw.Elapsed}");
        Assert.DoesNotContain(AccessKey, redacted);
        Assert.DoesNotContain(SecretKey, redacted);
        Assert.Contains("versionId=keep-me", redacted, StringComparison.Ordinal);
        Assert.Contains("RequestId=keep-0", redacted, StringComparison.Ordinal);
        Assert.Equal(string.Empty, CloudErrorSanitizer.RedactSecrets(null));
    }

    [Fact]
    public void ErrorLogService_WriteException_RedactsInnerChainSecrets_ForUnmappedCloudFailures()
    {
        string leakyUrl = $"https://s3.yandexcloud.net/b/k.json?AWSAccessKeyId={AccessKey}&Signature=deadbeef";
        var inner = new InvalidOperationException(
            $"provider dump Authorization: AWS4-HMAC-SHA256 Credential={AccessKey}/20260101/ru-central1/s3/aws4_request note={NotePlaintext} password={SyncPassword} recovery={RecoveryMaterial} url={leakyUrl}");
        var outer = new InvalidOperationException("unmapped sync transport failure", inner);

        string logDir = Path.Combine(Path.GetTempPath(), $"qn-s3-redact-log-{Guid.NewGuid():N}");
        Directory.CreateDirectory(logDir);
        _tempDirs.Add(logDir);

        using (ErrorLogService.UseScopedDirectory(logDir))
        {
            ErrorLogService.Write("SyncEngine.RunSyncCycle", outer);
            string log = File.ReadAllText(ErrorLogService.LogFilePath);
            Assert.Contains("InvalidOperationException", log, StringComparison.Ordinal);
            Assert.Contains("unmapped sync transport failure", log, StringComparison.Ordinal);
            Assert.Contains(" --> ", log, StringComparison.Ordinal);
            AssertForbidden(log);
        }
    }

    [Fact]
    public void CloudStorageException_Sanitize_RedactsSignedQueryWithoutChangingPublicShape()
    {
        var ex = new CloudAuthException(
            "Denied https://s3.yandexcloud.net/b/k.json?AWSAccessKeyId=" + AccessKey + "&Signature=abc",
            statusCode: 403);
        Assert.DoesNotContain(AccessKey, ex.Message);
        Assert.Contains("AWSAccessKeyId=[REDACTED]", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Signature=[REDACTED]", ex.Message, StringComparison.Ordinal);
        Assert.Contains("https://s3.yandexcloud.net/b/k.json", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("?[redacted]", ex.Message, StringComparison.Ordinal);
        Assert.Equal(CloudErrorCode.Authentication, ex.ErrorCode);
        Assert.Equal(403, ex.StatusCode);
    }

    [Fact]
    public void ObjectKeys_AreGuidAndHashNamespaces_NotUserPlaintext()
    {
        var helper = new SyncObjectKeyHelper(new SyncCloudSettings { Prefix = "v1-root" });
        Guid device = Guid.Parse("11111111-1111-1111-1111-111111111111");
        Guid package = Guid.Parse("22222222-2222-2222-2222-222222222222");
        string packageKey = helper.GetPackageKey(device, package);
        string pointerKey = helper.GetDevicePointerKey(device);
        string blobKey = helper.GetBlobKey(new string('a', 64));

        Assert.Equal("v1-root/v1/devices/11111111-1111-1111-1111-111111111111/packages/22222222-2222-2222-2222-222222222222.json", packageKey);
        Assert.EndsWith("/pointer.json", pointerKey, StringComparison.Ordinal);
        Assert.EndsWith("/blobs/" + new string('a', 64) + ".bin", blobKey, StringComparison.Ordinal);

        foreach (string key in new[] { packageKey, pointerKey, blobKey })
        {
            Assert.DoesNotContain(NotePlaintext, key);
            Assert.DoesNotContain(SyncPassword, key);
            Assert.DoesNotContain(AccessKey, key);
            Assert.DoesNotContain(RecoveryMaterial, key);
        }
    }

    [Fact]
    public async Task SyncEngine_AuthFailure_DiagnosticsLogsAndCycleResult_OmitSecretsUrlsAndNotePlaintext()
    {
        string leaky =
            $"AccessDenied for https://s3.yandexcloud.net/notes-sync-bucket/v1/devices/?AWSAccessKeyId={AccessKey}&Signature=deadbeef " +
            $"Authorization: AWS4-HMAC-SHA256 Credential={AccessKey}/20260101/ru-central1/s3/aws4_request " +
            $"note={NotePlaintext} password={SyncPassword} recovery={RecoveryMaterial}";

        var fakeS3 = new LeakyAmazonS3Client
        {
            ListObjectsV2Handler = (_, _) =>
                throw new AmazonS3Exception(leaky, ErrorType.Sender, "AccessDenied", "req", HttpStatusCode.Forbidden),
            GetObjectHandler = (_, _) =>
                throw new AmazonS3Exception(leaky, ErrorType.Sender, "AccessDenied", "req", HttpStatusCode.Forbidden)
        };

        var settings = new SyncCloudSettings
        {
            Enabled = true,
            Endpoint = "https://s3.yandexcloud.net",
            Region = "ru-central1",
            Bucket = "notes-sync-bucket",
            Prefix = "sync-root",
            MaxRetryAttempts = 0
        };

        string dbPath = Path.Combine(Path.GetTempPath(), $"qn-s3-redact-{Guid.NewGuid():N}.db");
        _tempFiles.Add(dbPath);
        string logDir = Path.Combine(Path.GetTempPath(), $"qn-s3-redact-log-{Guid.NewGuid():N}");
        Directory.CreateDirectory(logDir);
        _tempDirs.Add(logDir);

        using var db = SqliteTestUtil.CreateContext(dbPath);
        DbInitializer.Initialize(db);
        db.Notes.Add(new Note
        {
            Text = NotePlaintext,
            Title = "audit",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        using var s3Transport = new S3ObjectStoreTransport(settings, new FixedCreds(AccessKey, SecretKey), fakeS3);
        var crypto = new SyncCryptoService(iterations: 5_000);
        var deviceId = Guid.NewGuid();
        var deviceIds = new FixedDeviceIdProvider(deviceId);
        var exporter = new SyncPackageExporter(crypto, deviceIds);
        var importer = new SyncPackageImporter(crypto);
        var keyHelper = new SyncObjectKeyHelper(settings);
        var creds = new MemoryCreds(AccessKey, SecretKey);

        using var engine = new SyncEngine(
            s3Transport,
            exporter,
            importer,
            deviceIds,
            settings,
            creds,
            keyHelper,
            dbFactory: () => SqliteTestUtil.CreateContext(dbPath));

        using (ErrorLogService.UseScopedDirectory(logDir))
        {
            var result = await engine.RunSyncCycleAsync(db, SyncPassword);
            Assert.True(result.IsAuthError);
            AssertForbidden(result.ErrorMessage);
            foreach (string line in result.Diagnostics.Concat(result.Errors))
            {
                AssertForbidden(line);
            }

            string logPath = ErrorLogService.LogFilePath;
            string log = File.Exists(logPath) ? File.ReadAllText(logPath) : string.Empty;
            AssertForbidden(log);
        }
    }

    [Fact]
    public async Task SyncEngine_UnmappedException_LogsRedactedInnerChain_WithoutLeakingSecrets()
    {
        string leaky =
            $"unmapped dump https://s3.yandexcloud.net/notes-sync-bucket/v1/devices/?AWSAccessKeyId={AccessKey}&Signature=deadbeef " +
            $"Authorization: AWS4-HMAC-SHA256 Credential={AccessKey}/20260101/ru-central1/s3/aws4_request " +
            $"note={NotePlaintext} password={SyncPassword} recovery={RecoveryMaterial}";

        var fakeS3 = new LeakyAmazonS3Client
        {
            ListObjectsV2Handler = (_, _) =>
                throw new InvalidOperationException("unmapped S3 SDK failure", new InvalidOperationException(leaky)),
            GetObjectHandler = (_, _) =>
                throw new AmazonS3Exception("Not found", ErrorType.Sender, "NoSuchKey", "req", HttpStatusCode.NotFound)
        };

        var settings = new SyncCloudSettings
        {
            Enabled = true,
            Endpoint = "https://s3.yandexcloud.net",
            Region = "ru-central1",
            Bucket = "notes-sync-bucket",
            Prefix = "sync-root",
            MaxRetryAttempts = 0
        };

        string dbPath = Path.Combine(Path.GetTempPath(), $"qn-s3-redact-{Guid.NewGuid():N}.db");
        _tempFiles.Add(dbPath);
        string logDir = Path.Combine(Path.GetTempPath(), $"qn-s3-redact-log-{Guid.NewGuid():N}");
        Directory.CreateDirectory(logDir);
        _tempDirs.Add(logDir);

        using var db = SqliteTestUtil.CreateContext(dbPath);
        DbInitializer.Initialize(db);

        using var s3Transport = new S3ObjectStoreTransport(settings, new FixedCreds(AccessKey, SecretKey), fakeS3);
        var crypto = new SyncCryptoService(iterations: 5_000);
        var deviceId = Guid.NewGuid();
        var deviceIds = new FixedDeviceIdProvider(deviceId);
        var exporter = new SyncPackageExporter(crypto, deviceIds);
        var importer = new SyncPackageImporter(crypto);
        var keyHelper = new SyncObjectKeyHelper(settings);
        var creds = new MemoryCreds(AccessKey, SecretKey);

        using var engine = new SyncEngine(
            s3Transport,
            exporter,
            importer,
            deviceIds,
            settings,
            creds,
            keyHelper,
            dbFactory: () => SqliteTestUtil.CreateContext(dbPath));

        using (ErrorLogService.UseScopedDirectory(logDir))
        {
            var result = await engine.RunSyncCycleAsync(db, SyncPassword);
            Assert.False(result.Success);
            Assert.False(result.IsAuthError);
            AssertForbidden(result.ErrorMessage);
            Assert.Contains("unmapped S3 SDK failure", result.ErrorMessage, StringComparison.Ordinal);

            Assert.True(File.Exists(ErrorLogService.LogFilePath), "expected ErrorLogService entry for unmapped catch-all");
            string log = File.ReadAllText(ErrorLogService.LogFilePath);
            Assert.Contains("SyncEngine.RunSyncCycle", log, StringComparison.Ordinal);
            Assert.Contains("InvalidOperationException", log, StringComparison.Ordinal);
            Assert.Contains("unmapped S3 SDK failure", log, StringComparison.Ordinal);
            Assert.Contains(" --> ", log, StringComparison.Ordinal);
            AssertForbidden(log);
        }
    }

    private static void AssertForbidden(string? text)
    {
        string value = text ?? string.Empty;
        Assert.DoesNotContain(NotePlaintext, value);
        Assert.DoesNotContain(SyncPassword, value);
        Assert.DoesNotContain(AccessKey, value);
        Assert.DoesNotContain(SecretKey, value);
        Assert.DoesNotContain(RecoveryMaterial, value);
        Assert.DoesNotContain("deadbeef", value);
    }

    private sealed class FixedCreds : IS3CredentialsProvider
    {
        private readonly S3Credentials _creds;
        public FixedCreds(string key, string secret) => _creds = new S3Credentials(key, secret);
        public Task<S3Credentials?> GetCredentialsAsync(CancellationToken ct = default) => Task.FromResult<S3Credentials?>(_creds);
    }

    private sealed class MemoryCreds : IS3CredentialsStorage
    {
        private S3Credentials? _creds;
        public MemoryCreds(string key, string secret) => _creds = new S3Credentials(key, secret);
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

    private sealed class LeakyAmazonS3Client : AmazonS3Client
    {
        public LeakyAmazonS3Client()
            : base(new AnonymousAWSCredentials(), new AmazonS3Config { ServiceURL = "http://localhost", ForcePathStyle = true })
        {
        }

        public Func<ListObjectsV2Request, CancellationToken, Task<ListObjectsV2Response>>? ListObjectsV2Handler { get; set; }

        public Func<GetObjectRequest, CancellationToken, Task<GetObjectResponse>>? GetObjectHandler { get; set; }

        public override Task<ListObjectsV2Response> ListObjectsV2Async(ListObjectsV2Request request, CancellationToken cancellationToken = default)
        {
            if (ListObjectsV2Handler != null)
            {
                return ListObjectsV2Handler(request, cancellationToken);
            }

            return Task.FromResult(new ListObjectsV2Response { HttpStatusCode = HttpStatusCode.OK });
        }

        public override Task<GetObjectResponse> GetObjectAsync(GetObjectRequest request, CancellationToken cancellationToken = default)
        {
            if (GetObjectHandler != null)
            {
                return GetObjectHandler(request, cancellationToken);
            }

            throw new AmazonS3Exception("Access Denied", ErrorType.Sender, "AccessDenied", "req", HttpStatusCode.Forbidden);
        }
    }
}
