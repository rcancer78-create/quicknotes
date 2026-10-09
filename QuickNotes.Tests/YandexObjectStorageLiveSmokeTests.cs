using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using QuickNotes.App.Models.Sync;
using QuickNotes.App.Services.Sync;
using Xunit;

namespace QuickNotes.Tests;

/// <summary>
/// Explicit opt-in live smoke for a disposable object in Yandex Object Storage.
/// It is skipped by returning early unless all QUICKNOTES_LIVE_S3_* variables are set.
/// Credentials remain in a caller-supplied DPAPI file and are never embedded in the test.
/// </summary>
[TestCategory(TestCategories.LiveCloud)]
public sealed class YandexObjectStorageLiveSmokeTests
{
    [Fact]
    public async Task OptIn_PutGetDelete_RoundTrip()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("QUICKNOTES_LIVE_S3_REQUIRED"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        string? credentialsPath = Environment.GetEnvironmentVariable("QUICKNOTES_LIVE_S3_CREDENTIALS_PATH");
        string? endpoint = Environment.GetEnvironmentVariable("QUICKNOTES_LIVE_S3_ENDPOINT");
        string? bucket = Environment.GetEnvironmentVariable("QUICKNOTES_LIVE_S3_BUCKET");
        Assert.False(string.IsNullOrWhiteSpace(credentialsPath));
        Assert.False(string.IsNullOrWhiteSpace(endpoint));
        Assert.False(string.IsNullOrWhiteSpace(bucket));
        Assert.True(System.IO.File.Exists(credentialsPath));

        var settings = new SyncCloudSettings
        {
            Enabled = true,
            Endpoint = endpoint,
            Region = SyncCloudSettings.DefaultYandexRegion,
            Bucket = bucket,
            RequestTimeoutSeconds = 20,
            MaxRetryAttempts = 1
        };
        var credentials = new DpapiS3CredentialsStorage(credentialsPath);
        using var transport = new S3ObjectStoreTransport(settings, credentials);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));

        string key = $"quicknotes-live-smoke/{DateTime.UtcNow:yyyyMMdd}/{Guid.NewGuid():N}.bin";
        byte[] payload = RandomNumberGenerator.GetBytes(64);
        bool created = false;
        try
        {
            await transport.PutImmutableObjectAsync(key, payload, "application/octet-stream", cts.Token);
            created = true;

            StorageObjectResult? downloaded = await transport.GetObjectAsync(key, cts.Token);
            Assert.NotNull(downloaded);
            Assert.True(CryptographicOperations.FixedTimeEquals(payload, downloaded!.Content));

            Assert.True(await transport.DeleteObjectAsync(key, cts.Token));
            created = false;
            Assert.Null(await transport.HeadObjectAsync(key, cts.Token));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
            if (created)
            {
                try { await transport.DeleteObjectAsync(key, CancellationToken.None); }
                catch { /* Best-effort cleanup; preserve the original failure. */ }
            }
        }
    }
}
