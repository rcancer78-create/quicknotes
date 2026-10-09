using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using QuickNotes.App.Models;
using QuickNotes.App.Models.Sync;
using QuickNotes.App.Services;
using QuickNotes.App.Services.Sync;
using QuickNotes.App.ViewModels;
using Xunit;

namespace QuickNotes.Tests;

[TestCategory(TestCategories.Integration)]
public class SyncSettingsViewModelTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _settingsFilePath;

    public SyncSettingsViewModelTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"quicknotes_settings_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _settingsFilePath = Path.Combine(_tempDir, "settings.json");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, true); } catch { }
        }
    }

    private class InMemoryCredentialsStorage : IS3CredentialsStorage, IS3CredentialsProvider
    {
        public S3Credentials? Credentials { get; set; }

        public InMemoryCredentialsStorage(S3Credentials? credentials = null)
        {
            Credentials = credentials;
        }

        public Task<S3Credentials?> LoadCredentialsAsync(CancellationToken ct = default) => Task.FromResult(Credentials);
        public Task<S3Credentials?> GetCredentialsAsync(CancellationToken ct = default) => Task.FromResult(Credentials);
        public Task SaveCredentialsAsync(S3Credentials credentials, CancellationToken ct = default)
        {
            Credentials = credentials;
            return Task.CompletedTask;
        }
        public Task DeleteCredentialsAsync(CancellationToken ct = default)
        {
            Credentials = null;
            return Task.CompletedTask;
        }
        public bool HasCredentials() => Credentials != null && !string.IsNullOrWhiteSpace(Credentials.AccessKeyId);
    }

    private class InMemoryPasswordStorage : ISyncPasswordStorage
    {
        public string? Password { get; set; }

        public InMemoryPasswordStorage(string? password = null)
        {
            Password = password;
        }

        public Task<string?> LoadPasswordAsync(CancellationToken ct = default) => Task.FromResult(Password);
        public Task SavePasswordAsync(string password, CancellationToken ct = default)
        {
            Password = password;
            return Task.CompletedTask;
        }
        public Task DeletePasswordAsync(CancellationToken ct = default)
        {
            Password = null;
            Pending = null;
            return Task.CompletedTask;
        }
        public bool HasPassword() => !string.IsNullOrEmpty(Password);
        public string? Pending { get; set; }
        public Task SavePendingPasswordAsync(string password, CancellationToken ct = default) { Pending = password; return Task.CompletedTask; }
        public Task<string?> LoadPendingPasswordAsync(CancellationToken ct = default) => Task.FromResult(Pending);
        public Task PromotePendingPasswordAsync(CancellationToken ct = default) { Password = Pending; Pending = null; return Task.CompletedTask; }
        public Task DeletePendingPasswordAsync(CancellationToken ct = default) { Pending = null; return Task.CompletedTask; }
        public bool HasPendingPassword() => !string.IsNullOrEmpty(Pending);
    }

    private class FakeSyncCloudCoordinator : ISyncCloudCoordinator
    {
        public CloudSyncConnectionTestResult TestResult { get; set; } = CloudSyncConnectionTestResult.Succeeded("https://storage.yandexcloud.net", "test-bucket");
        public Exception? ExceptionToThrow { get; set; }

        public Task<CloudSyncConnectionTestResult> TestConnectionAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (ExceptionToThrow != null) throw ExceptionToThrow;
            return Task.FromResult(TestResult);
        }

        public Task<CloudSyncUploadResult> UploadLatestPackageAsync(QuickNotes.App.Data.QuickNotesDbContext db, string encryptionPassword, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<CloudSyncDownloadResult> DownloadAndImportPackageAsync(string packageKey, QuickNotes.App.Data.QuickNotesDbContext db, string encryptionPassword, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<CloudSyncListResult> ListRemotePackagesAsync(CancellationToken ct = default) => throw new NotImplementedException();
    }

    [Fact]
    public void Init_WhenCredentialsExist_MasksKeyAndShowsSavedStatus()
    {
        var settingsService = new SettingsService(_settingsFilePath, _ => { });
        var credsStorage = new InMemoryCredentialsStorage(new S3Credentials("AKIA_TEST_KEY", "SECRET_KEY_123"));
        var pwdStorage = new InMemoryPasswordStorage("MasterPass123");

        var vm = new SettingsViewModel(
            settingsService,
            new GlobalHotkeyService((_, _, _, _) => true, (_, _) => true, IntPtr.Zero),
            new BackupService(),
            credentialsStorage: credsStorage,
            passwordStorage: pwdStorage);

        Assert.True(vm.HasSavedCredentials);
        Assert.True(vm.HasSavedPassword);
        Assert.StartsWith("••••", vm.SyncAccessKeyId);
        Assert.Contains("сохранены в DPAPI", vm.SyncCredentialsStatusText);
        Assert.Contains("сохранён в DPAPI", vm.SyncPasswordStatusText);
    }

    [Fact]
    public async Task SaveSyncSettings_SavesToDPAPI_AndDoesNotLeakSecretsToAppSettings()
    {
        var settingsService = new SettingsService(_settingsFilePath, _ => { });
        var credsStorage = new InMemoryCredentialsStorage();
        var pwdStorage = new InMemoryPasswordStorage();

        var vm = new SettingsViewModel(
            settingsService,
            new GlobalHotkeyService((_, _, _, _) => true, (_, _) => true, IntPtr.Zero),
            new BackupService(),
            credentialsStorage: credsStorage,
            passwordStorage: pwdStorage);

        bool clearRequested = false;
        vm.ClearPasswordInputsRequested += () => clearRequested = true;

        vm.SyncEnabled = true;
        vm.SyncBucket = "my-secure-bucket";
        vm.SyncPrefix = "quicknotes";
        vm.SyncAccessKeyId = "YC_KEY_IDENTIFIER";
        vm.SetSecretAccessKeyInput("SUPER_SECRET_S3_KEY");
        vm.SetEncryptionPasswordInput("MyE2EEPassword99");
        vm.SetEncryptionPasswordConfirmInput("MyE2EEPassword99");
        vm.SyncAttachments = true;
        vm.MaxAttachmentSyncMb = 50;

        bool saved = await vm.SaveSyncSettingsAsync();
        Assert.True(saved);
        Assert.True(clearRequested);

        // Verify DPAPI storages have the keys
        Assert.NotNull(credsStorage.Credentials);
        Assert.Equal("YC_KEY_IDENTIFIER", credsStorage.Credentials.AccessKeyId);
        Assert.Equal("SUPER_SECRET_S3_KEY", credsStorage.Credentials.SecretAccessKey);
        Assert.Equal("MyE2EEPassword99", pwdStorage.Password);

        // Verify settings.json on disk does NOT contain S3 secret key or encryption password
        var loadedSettings = settingsService.CurrentSettings;
        Assert.True(loadedSettings.CloudSync.Enabled);
        Assert.Equal("my-secure-bucket", loadedSettings.CloudSync.Bucket);
        Assert.Equal("quicknotes/", loadedSettings.CloudSync.Prefix);
        Assert.True(loadedSettings.CloudSync.SyncAttachments);
        Assert.Equal(50L * 1024 * 1024, loadedSettings.CloudSync.MaxAttachmentSyncBytes);

        string jsonContent = File.ReadAllText(_settingsFilePath);
        Assert.DoesNotContain("SUPER_SECRET_S3_KEY", jsonContent);
        Assert.DoesNotContain("MyE2EEPassword99", jsonContent);
        Assert.DoesNotContain("YC_KEY_IDENTIFIER", jsonContent);
    }

    [Fact]
    public async Task SaveSyncSettings_PasswordMismatch_RejectsAndShowsMessage()
    {
        var settingsService = new SettingsService(_settingsFilePath, _ => { });
        var credsStorage = new InMemoryCredentialsStorage();
        var pwdStorage = new InMemoryPasswordStorage();

        var vm = new SettingsViewModel(
            settingsService,
            new GlobalHotkeyService((_, _, _, _) => true, (_, _) => true, IntPtr.Zero),
            new BackupService(),
            credentialsStorage: credsStorage,
            passwordStorage: pwdStorage);

        string? shownTitle = null;
        vm.ShowMessageAction = (msg, title, icon) => shownTitle = title;

        vm.SyncEnabled = true;
        vm.SyncBucket = "my-bucket";
        vm.SyncAccessKeyId = "KEY_ID";
        vm.SetSecretAccessKeyInput("SECRET_KEY");
        vm.SetEncryptionPasswordInput("Password123");
        vm.SetEncryptionPasswordConfirmInput("MismatchPassword456");

        bool saved = await vm.SaveSyncSettingsAsync();
        Assert.False(saved);
        Assert.Equal("Несовпадение паролей", shownTitle);
        Assert.False(pwdStorage.HasPassword());
    }

    [Fact]
    public async Task DeleteSyncCredentials_ClearsStoragesAndDisablesSync()
    {
        var settingsService = new SettingsService(_settingsFilePath, _ => { });
        var credsStorage = new InMemoryCredentialsStorage(new S3Credentials("KEY", "SECRET"));
        var pwdStorage = new InMemoryPasswordStorage("PWD");

        var vm = new SettingsViewModel(
            settingsService,
            new GlobalHotkeyService((_, _, _, _) => true, (_, _) => true, IntPtr.Zero),
            new BackupService(),
            credentialsStorage: credsStorage,
            passwordStorage: pwdStorage);

        bool clearRequested = false;
        vm.ClearPasswordInputsRequested += () => clearRequested = true;

        await vm.DeleteSyncCredentialsAsync();

        Assert.False(credsStorage.HasCredentials());
        Assert.False(pwdStorage.HasPassword());
        Assert.False(vm.HasSavedCredentials);
        Assert.False(vm.HasSavedPassword);
        Assert.False(vm.SyncEnabled);
        Assert.True(clearRequested);
        Assert.Contains("Ключи и пароль удалены", vm.SyncConnectionStatusText);
        Assert.True(vm.IsConnectionWarning);
    }

    [Fact]
    public async Task TestConnection_SuccessMapping()
    {
        var settingsService = new SettingsService(_settingsFilePath, _ => { });
        var credsStorage = new InMemoryCredentialsStorage(new S3Credentials("KEY", "SECRET"));
        var pwdStorage = new InMemoryPasswordStorage("PWD");
        var fakeCoordinator = new FakeSyncCloudCoordinator
        {
            TestResult = CloudSyncConnectionTestResult.Succeeded("https://storage.yandexcloud.net", "bucket")
        };

        var vm = new SettingsViewModel(
            settingsService,
            new GlobalHotkeyService((_, _, _, _) => true, (_, _) => true, IntPtr.Zero),
            new BackupService(),
            credentialsStorage: credsStorage,
            passwordStorage: pwdStorage,
            coordinator: fakeCoordinator);

        vm.SyncBucket = "valid-bucket";
        await vm.TestConnectionAsync();

        Assert.True(vm.IsConnectionSuccess);
        Assert.False(vm.IsConnectionError);
        Assert.False(vm.IsConnectionWarning);
        Assert.Contains("✓ Подключение успешно", vm.SyncConnectionStatusText);
    }

    [Fact]
    public async Task TestConnection_OfflineMapping()
    {
        var settingsService = new SettingsService(_settingsFilePath, _ => { });
        var credsStorage = new InMemoryCredentialsStorage(new S3Credentials("KEY", "SECRET"));
        var pwdStorage = new InMemoryPasswordStorage("PWD");
        var fakeCoordinator = new FakeSyncCloudCoordinator
        {
            TestResult = CloudSyncConnectionTestResult.Offline("https://storage.yandexcloud.net", "bucket", "No internet")
        };

        var vm = new SettingsViewModel(
            settingsService,
            new GlobalHotkeyService((_, _, _, _) => true, (_, _) => true, IntPtr.Zero),
            new BackupService(),
            credentialsStorage: credsStorage,
            passwordStorage: pwdStorage,
            coordinator: fakeCoordinator);

        vm.SyncBucket = "valid-bucket";
        await vm.TestConnectionAsync();

        Assert.True(vm.IsConnectionWarning);
        Assert.False(vm.IsConnectionSuccess);
        Assert.Contains("⚠ Нет сети", vm.SyncConnectionStatusText);
    }

    [Fact]
    public async Task TestConnection_AuthErrorMapping()
    {
        var settingsService = new SettingsService(_settingsFilePath, _ => { });
        var credsStorage = new InMemoryCredentialsStorage(new S3Credentials("KEY", "SECRET"));
        var pwdStorage = new InMemoryPasswordStorage("PWD");
        var fakeCoordinator = new FakeSyncCloudCoordinator
        {
            TestResult = CloudSyncConnectionTestResult.AuthFailure("https://storage.yandexcloud.net", "bucket", "403 Forbidden")
        };

        var vm = new SettingsViewModel(
            settingsService,
            new GlobalHotkeyService((_, _, _, _) => true, (_, _) => true, IntPtr.Zero),
            new BackupService(),
            credentialsStorage: credsStorage,
            passwordStorage: pwdStorage,
            coordinator: fakeCoordinator);

        vm.SyncBucket = "valid-bucket";
        await vm.TestConnectionAsync();

        Assert.True(vm.IsConnectionError);
        Assert.Contains("✕ Неверные ключи или права", vm.SyncConnectionStatusText);
    }

    [Fact]
    public async Task TestConnection_BucketNotFoundMapping()
    {
        var settingsService = new SettingsService(_settingsFilePath, _ => { });
        var credsStorage = new InMemoryCredentialsStorage(new S3Credentials("KEY", "SECRET"));
        var pwdStorage = new InMemoryPasswordStorage("PWD");
        var fakeCoordinator = new FakeSyncCloudCoordinator
        {
            TestResult = CloudSyncConnectionTestResult.Failure("https://storage.yandexcloud.net", "bucket", "NoSuchBucket: The specified bucket does not exist")
        };

        var vm = new SettingsViewModel(
            settingsService,
            new GlobalHotkeyService((_, _, _, _) => true, (_, _) => true, IntPtr.Zero),
            new BackupService(),
            credentialsStorage: credsStorage,
            passwordStorage: pwdStorage,
            coordinator: fakeCoordinator);

        vm.SyncBucket = "missing-bucket";
        await vm.TestConnectionAsync();

        Assert.True(vm.IsConnectionError);
        Assert.Contains("✕ Бакет не найден", vm.SyncConnectionStatusText);
    }

    [Fact]
    public async Task TestConnection_QuotaMapping()
    {
        var settingsService = new SettingsService(_settingsFilePath, _ => { });
        var credsStorage = new InMemoryCredentialsStorage(new S3Credentials("KEY", "SECRET"));
        var pwdStorage = new InMemoryPasswordStorage("PWD");
        var fakeCoordinator = new FakeSyncCloudCoordinator
        {
            ExceptionToThrow = new CloudQuotaException("Too Many Requests (Rate limit exceeded)", 429)
        };

        var vm = new SettingsViewModel(
            settingsService,
            new GlobalHotkeyService((_, _, _, _) => true, (_, _) => true, IntPtr.Zero),
            new BackupService(),
            credentialsStorage: credsStorage,
            passwordStorage: pwdStorage,
            coordinator: fakeCoordinator);

        vm.SyncBucket = "bucket";
        await vm.TestConnectionAsync();

        Assert.True(vm.IsConnectionWarning);
        Assert.Contains("⚠ Квота или временный сбой", vm.SyncConnectionStatusText);
    }

    private sealed class FakeRotationService : ISyncPasswordRotationService
    {
        public PasswordRotationPreview Preview { get; set; } = new()
        {
            Token = "tok",
            IsComplete = true,
            Summary = "preview-ok"
        };
        public PasswordRotationExecuteResult ExecuteResult { get; set; } = new()
        {
            TokenAccepted = true,
            Success = true,
            CloudSwitched = true,
            LocalPasswordSaved = true,
            Summary = "rotated-ok"
        };
        public int ExecuteCalls { get; private set; }

        public Task<PasswordRotationPreview> PreviewAsync(string oldPassword, CancellationToken ct = default)
            => Task.FromResult(Preview);

        public Task<PasswordRotationExecuteResult> ExecuteAsync(
            string previewToken, string oldPassword, string newPassword,
            IProgress<PasswordRotationProgress>? progress = null, CancellationToken ct = default)
        {
            ExecuteCalls++;
            return Task.FromResult(ExecuteResult);
        }
    }

    [Fact]
    public async Task PasswordRotation_UsesConfirmCallback_AndDoesNotSwitchWhenCancelled()
    {
        var settingsService = new SettingsService(_settingsFilePath, _ => { });
        var fake = new FakeRotationService();
        int confirms = 0;
        var vm = new SettingsViewModel(
            settingsService,
            new GlobalHotkeyService((_, _, _, _) => true, (_, _) => true, IntPtr.Zero),
            new BackupService(),
            showMessage: (_, _, _) => { },
            credentialsStorage: new InMemoryCredentialsStorage(new S3Credentials("KEY", "SECRET")),
            passwordStorage: new InMemoryPasswordStorage("old-pass"),
            passwordRotationService: fake);

        vm.ConfirmAction = (_, _) =>
        {
            confirms++;
            return false;
        };
        vm.SetRotationOldPasswordInput("old-pass");
        vm.SetRotationNewPasswordInput("new-pass");
        vm.SetRotationNewPasswordConfirmInput("new-pass");

        await vm.PreviewPasswordRotationAsync();
        await vm.ExecutePasswordRotationAsync();

        Assert.Equal(1, confirms);
        Assert.Equal(0, fake.ExecuteCalls);
    }
}
