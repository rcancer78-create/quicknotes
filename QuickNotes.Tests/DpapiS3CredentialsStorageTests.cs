using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using QuickNotes.App.Models.Sync;
using QuickNotes.App.Services.Sync;
using Xunit;

namespace QuickNotes.Tests;

[TestCategory(TestCategories.Integration)]
public class DpapiS3CredentialsStorageTests : IDisposable
{
    private readonly string _tempCredentialsFile;

    public DpapiS3CredentialsStorageTests()
    {
        _tempCredentialsFile = Path.Combine(Path.GetTempPath(), $"s3_creds_test_{Guid.NewGuid():N}.dat");
    }

    public void Dispose()
    {
        if (File.Exists(_tempCredentialsFile))
        {
            try { File.Delete(_tempCredentialsFile); } catch { }
        }
    }

    [Fact]
    public async Task SaveAndLoad_Roundtrip_Succeeds()
    {
        var storage = new DpapiS3CredentialsStorage(_tempCredentialsFile);
        var creds = new S3Credentials("YCAJEtestAccessKey123", "YCPtestSecretAccessKey456789xyz");

        Assert.False(storage.HasCredentials());
        await storage.SaveCredentialsAsync(creds);
        Assert.True(storage.HasCredentials());

        var loaded = await storage.LoadCredentialsAsync();
        Assert.NotNull(loaded);
        Assert.Equal(creds.AccessKeyId, loaded.AccessKeyId);
        Assert.Equal(creds.SecretAccessKey, loaded.SecretAccessKey);
        Assert.Equal(creds, loaded);
    }

    [Fact]
    public async Task Save_Rotation_OverwritesOldCredentialsAtomically()
    {
        var storage = new DpapiS3CredentialsStorage(_tempCredentialsFile);
        var creds1 = new S3Credentials("KeyInitial", "SecretInitial");
        var creds2 = new S3Credentials("KeyRotated", "SecretRotated");

        await storage.SaveCredentialsAsync(creds1);
        var loaded1 = await storage.LoadCredentialsAsync();
        Assert.Equal("KeyInitial", loaded1?.AccessKeyId);

        // Rotate
        await storage.SaveCredentialsAsync(creds2);
        var loaded2 = await storage.LoadCredentialsAsync();
        Assert.Equal("KeyRotated", loaded2?.AccessKeyId);
        Assert.Equal("SecretRotated", loaded2?.SecretAccessKey);

        // Ensure no stray .tmp files left in directory
        string dir = Path.GetDirectoryName(_tempCredentialsFile)!;
        var tempFiles = Directory.GetFiles(dir, Path.GetFileName(_tempCredentialsFile) + ".*.tmp");
        Assert.Empty(tempFiles);
    }

    [Fact]
    public async Task Delete_RemovesCredentialsFile_HasCredentialsReturnsFalse()
    {
        var storage = new DpapiS3CredentialsStorage(_tempCredentialsFile);
        var creds = new S3Credentials("KeyToDelete", "SecretToDelete");

        await storage.SaveCredentialsAsync(creds);
        Assert.True(storage.HasCredentials());

        await storage.DeleteCredentialsAsync();

        Assert.False(storage.HasCredentials());
        Assert.False(File.Exists(_tempCredentialsFile));
        var loaded = await storage.LoadCredentialsAsync();
        Assert.Null(loaded);
    }

    [Fact]
    public async Task Load_CorruptedFile_ThrowsSyncSecurityException()
    {
        var storage = new DpapiS3CredentialsStorage(_tempCredentialsFile);

        // Write random garbage bytes
        await File.WriteAllBytesAsync(_tempCredentialsFile, new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0x01, 0x02 });

        await Assert.ThrowsAsync<SyncSecurityException>(() => storage.LoadCredentialsAsync());
    }

    [Fact]
    public async Task OnDiskPayload_IsEncryptedWithDPAPI_NeverContainsPlaintextSecrets()
    {
        var storage = new DpapiS3CredentialsStorage(_tempCredentialsFile);
        string accessKey = "YCA_PLAINTEXT_ACCESS_KEY_999";
        string secretKey = "YCP_VERY_SECRET_KEY_NEVER_LEAK_ME_ABC123";
        var creds = new S3Credentials(accessKey, secretKey);

        await storage.SaveCredentialsAsync(creds);

        byte[] rawBytes = await File.ReadAllBytesAsync(_tempCredentialsFile);
        string rawString = Encoding.UTF8.GetString(rawBytes);

        // Verify that raw bytes on disk DO NOT contain plaintext keys
        Assert.DoesNotContain(accessKey, rawString);
        Assert.DoesNotContain(secretKey, rawString);
    }

    [Fact]
    public void S3Credentials_ToString_RedactsSecrets()
    {
        var creds = new S3Credentials("MyAccessKey", "MySecretKey");
        string str = creds.ToString();

        Assert.Equal("S3Credentials [PROTECTED]", str);
        Assert.DoesNotContain("MyAccessKey", str);
        Assert.DoesNotContain("MySecretKey", str);
    }

    [Fact]
    public void S3Credentials_Validation_RejectsEmptyOrWhitespace()
    {
        Assert.Throws<ArgumentException>(() => new S3Credentials("", "secret"));
        Assert.Throws<ArgumentException>(() => new S3Credentials("   ", "secret"));
        Assert.Throws<ArgumentException>(() => new S3Credentials("key", ""));
        Assert.Throws<ArgumentException>(() => new S3Credentials("key", "   "));
    }
}
