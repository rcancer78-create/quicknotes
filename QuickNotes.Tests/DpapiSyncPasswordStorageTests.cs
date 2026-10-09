using System;
using System.IO;
using System.Threading.Tasks;
using QuickNotes.App.Services.Sync;
using Xunit;

namespace QuickNotes.Tests;

[TestCategory(TestCategories.Integration)]
public class DpapiSyncPasswordStorageTests : IDisposable
{
    private readonly string _testFilePath;

    public DpapiSyncPasswordStorageTests()
    {
        _testFilePath = Path.Combine(Path.GetTempPath(), $"quicknotes_pwd_test_{Guid.NewGuid():N}.dat");
    }

    public void Dispose()
    {
        if (File.Exists(_testFilePath))
        {
            try { File.Delete(_testFilePath); } catch { }
        }
        string pending = _testFilePath + ".pending";
        if (File.Exists(pending))
        {
            try { File.Delete(pending); } catch { }
        }
    }

    [Fact]
    public async Task SaveAndLoad_RoundTrip_ReturnsMatchingPassword()
    {
        var storage = new DpapiSyncPasswordStorage(_testFilePath);
        Assert.False(storage.HasPassword());

        string expectedPassword = "SuperSecureSyncPassword123!@#тест";
        await storage.SavePasswordAsync(expectedPassword);

        Assert.True(storage.HasPassword());
        string? loaded = await storage.LoadPasswordAsync();
        Assert.Equal(expectedPassword, loaded);
    }

    [Fact]
    public async Task DeletePassword_RemovesFileAndReturnsNull()
    {
        var storage = new DpapiSyncPasswordStorage(_testFilePath);
        await storage.SavePasswordAsync("secret_pass");
        Assert.True(storage.HasPassword());

        await storage.DeletePasswordAsync();
        Assert.False(storage.HasPassword());
        Assert.False(File.Exists(_testFilePath));

        string? loaded = await storage.LoadPasswordAsync();
        Assert.Null(loaded);
    }

    [Fact]
    public async Task DeletePassword_WhenNoFile_DoesNotThrow()
    {
        var storage = new DpapiSyncPasswordStorage(_testFilePath);
        Assert.False(storage.HasPassword());

        await storage.DeletePasswordAsync();
        Assert.False(storage.HasPassword());
    }

    [Fact]
    public async Task SavePassword_EmptyOrWhitespace_ThrowsArgumentException()
    {
        var storage = new DpapiSyncPasswordStorage(_testFilePath);

        await Assert.ThrowsAsync<ArgumentException>(() => storage.SavePasswordAsync(""));
        await Assert.ThrowsAsync<ArgumentException>(() => storage.SavePasswordAsync("   "));
        await Assert.ThrowsAsync<ArgumentException>(() => storage.SavePasswordAsync(null!));
    }

    [Fact]
    public async Task SavePassword_OverwritesExistingPasswordSafely()
    {
        var storage = new DpapiSyncPasswordStorage(_testFilePath);

        await storage.SavePasswordAsync("first_password");
        Assert.Equal("first_password", await storage.LoadPasswordAsync());

        await storage.SavePasswordAsync("second_password");
        Assert.Equal("second_password", await storage.LoadPasswordAsync());
    }

    [Fact]
    public async Task PendingSlot_DoesNotReplaceActive_UntilPromote()
    {
        var storage = new DpapiSyncPasswordStorage(_testFilePath);
        await storage.SavePasswordAsync("active-old");
        await storage.SavePendingPasswordAsync("pending-new");

        Assert.True(storage.HasPassword());
        Assert.True(storage.HasPendingPassword());
        Assert.Equal("active-old", await storage.LoadPasswordAsync());
        Assert.Equal("pending-new", await storage.LoadPendingPasswordAsync());
        Assert.True(File.Exists(_testFilePath));
        Assert.True(File.Exists(_testFilePath + ".pending"));

        await storage.PromotePendingPasswordAsync();
        Assert.Equal("pending-new", await storage.LoadPasswordAsync());
        Assert.False(storage.HasPendingPassword());
        Assert.False(File.Exists(_testFilePath + ".pending"));
        Assert.Null(await storage.LoadPendingPasswordAsync());
    }

    [Fact]
    public async Task DeletePassword_ClearsPendingSlot()
    {
        var storage = new DpapiSyncPasswordStorage(_testFilePath);
        await storage.SavePasswordAsync("active-old");
        await storage.SavePendingPasswordAsync("pending-new");
        await storage.DeletePasswordAsync();
        Assert.False(storage.HasPassword());
        Assert.False(storage.HasPendingPassword());
        Assert.False(File.Exists(_testFilePath));
        Assert.False(File.Exists(_testFilePath + ".pending"));
    }
}
