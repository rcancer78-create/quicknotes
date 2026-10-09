using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using QuickNotes.App.Data;
using QuickNotes.App.Helpers;
using QuickNotes.App.Models;
using QuickNotes.App.Models.Sync;
using QuickNotes.App.Services;
using QuickNotes.App.Services.Sync;
using QuickNotes.App.ViewModels;
using Xunit;

namespace QuickNotes.Tests;

[TestCategory(TestCategories.Integration)]
public class MainViewModelSyncTests : IDisposable
{
    private readonly string _dbPath;
    private readonly string _settingsFilePath;
    private readonly QuickNotesDbContext _context;

    public MainViewModelSyncTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"quicknotes_main_sync_{Guid.NewGuid():N}.db");
        _settingsFilePath = Path.Combine(Path.GetTempPath(), $"quicknotes_main_settings_{Guid.NewGuid():N}.json");
        _context = new QuickNotesDbContext(_dbPath);
        _context.Database.EnsureCreated();
    }

    private readonly List<IDisposable> _disposables = new();

    public void Dispose()
    {
        foreach (var d in _disposables)
        {
            try { d.Dispose(); } catch { }
        }
        _context.Dispose();
        if (File.Exists(_dbPath)) try { File.Delete(_dbPath); } catch { }
        if (File.Exists(_settingsFilePath)) try { File.Delete(_settingsFilePath); } catch { }
    }

    private class FakeCredentialsStorage : IS3CredentialsStorage, IS3CredentialsProvider
    {
        public bool Configured { get; set; } = true;
        public Task<S3Credentials?> LoadCredentialsAsync(CancellationToken ct = default) =>
            Task.FromResult(Configured ? new S3Credentials("KEY", "SECRET") : null);
        public Task<S3Credentials?> GetCredentialsAsync(CancellationToken ct = default) => LoadCredentialsAsync(ct);
        public Task SaveCredentialsAsync(S3Credentials credentials, CancellationToken ct = default) { Configured = true; return Task.CompletedTask; }
        public Task DeleteCredentialsAsync(CancellationToken ct = default) { Configured = false; return Task.CompletedTask; }
        public bool HasCredentials() => Configured;
    }

    private class FakePasswordStorage : ISyncPasswordStorage
    {
        public bool Configured { get; set; } = true;
        public string? StoredPassword { get; set; } = "Pass123";
        public Task<string?> LoadPasswordAsync(CancellationToken ct = default) =>
            Task.FromResult(Configured ? StoredPassword : null);
        public Task SavePasswordAsync(string password, CancellationToken ct = default) { Configured = true; StoredPassword = password; return Task.CompletedTask; }
        public Task DeletePasswordAsync(CancellationToken ct = default) { Configured = false; StoredPassword = null; return Task.CompletedTask; }
        public bool HasPassword() => Configured && !string.IsNullOrEmpty(StoredPassword);
        public string? Pending { get; set; }
        public Task SavePendingPasswordAsync(string password, CancellationToken ct = default) { Pending = password; return Task.CompletedTask; }
        public Task<string?> LoadPendingPasswordAsync(CancellationToken ct = default) => Task.FromResult(Pending);
        public Task PromotePendingPasswordAsync(CancellationToken ct = default) { StoredPassword = Pending; Pending = null; Configured = StoredPassword != null; return Task.CompletedTask; }
        public Task DeletePendingPasswordAsync(CancellationToken ct = default) { Pending = null; return Task.CompletedTask; }
        public bool HasPendingPassword() => !string.IsNullOrEmpty(Pending);
    }

    private class FakeConflictService : ISyncConflictService
    {
        public int ConflictsCount { get; set; } = 0;
        public int GetCountCallCount { get; set; } = 0;
        public Exception? ExceptionToThrow { get; set; }

        public Task<List<SyncConflictRecord>> GetUnresolvedConflictsAsync(CancellationToken ct = default) =>
            Task.FromResult(ConflictsCount > 0
                ? new List<SyncConflictRecord> { new SyncConflictRecord { Id = 1, EntityType = "Note", SyncId = Guid.NewGuid() } }
                : new List<SyncConflictRecord>());

        public Task<int> GetUnresolvedConflictsCountAsync(CancellationToken ct = default)
        {
            GetCountCallCount++;
            if (ExceptionToThrow != null) throw ExceptionToThrow;
            return Task.FromResult(ConflictsCount);
        }
        public Task<SyncConflictDetail?> GetConflictDetailAsync(int conflictId, CancellationToken ct = default) =>
            Task.FromResult<SyncConflictDetail?>(new SyncConflictDetail { ConflictId = conflictId, EntityType = "Note" });
        public Task<SyncConflictResolutionResult> ResolveKeepBothAsync(int conflictId, CancellationToken ct = default) =>
            Task.FromResult(SyncConflictResolutionResult.Succeeded(conflictId, "KeepBoth"));
        public Task<SyncConflictResolutionResult> ResolveKeepLocalAsync(int conflictId, CancellationToken ct = default) =>
            Task.FromResult(SyncConflictResolutionResult.Succeeded(conflictId, "KeepLocal"));
        public Task<SyncConflictResolutionResult> ResolveAcceptRemoteAsync(int conflictId, CancellationToken ct = default) =>
            Task.FromResult(SyncConflictResolutionResult.Succeeded(conflictId, "AcceptRemote"));
        public Task<SyncConflictResolutionResult> ResolveMergeNoteAsync(int conflictId, string mergedText, CancellationToken ct = default) =>
            Task.FromResult(SyncConflictResolutionResult.Succeeded(conflictId, "Merge"));
    }

    private class FakeSyncEngine : ISyncEngine
    {
        public SyncCycleResult ResultToReturn { get; set; } = SyncCycleResult.Succeeded(
            noOp: false,
            remoteDevicesExamined: 1,
            remotePackagesPulled: 2,
            applied: new SyncEntityCounts { Created = 3, Updated = 1 },
            skipped: 0,
            conflicts: 0,
            packageUploaded: true,
            uploadedPackageId: Guid.NewGuid());

        public Exception? ExceptionToThrow { get; set; }
        public int CallCount { get; private set; }

        public Task<SyncCycleResult> RunSyncCycleAsync(QuickNotesDbContext db, string encryptionPassword, SyncCycleOptions? options = null, IProgress<SyncProgressReport>? progress = null, CancellationToken ct = default)
        {
            CallCount++;
            ct.ThrowIfCancellationRequested();
            if (ExceptionToThrow != null) throw ExceptionToThrow;
            return Task.FromResult(ResultToReturn);
        }

        public Task<SyncCycleResult> RunSyncCycleAsync(string encryptionPassword, SyncCycleOptions? options = null, IProgress<SyncProgressReport>? progress = null, CancellationToken ct = default)
        {
            return RunSyncCycleAsync(null!, encryptionPassword, options, progress, ct);
        }

        public Task<SyncEngineStatus> GetStatusAsync(QuickNotesDbContext? db = null, CancellationToken ct = default)
        {
            return Task.FromResult(new SyncEngineStatus { IsConfigured = true });
        }
    }

    private MainViewModel CreateViewModel(
        SettingsService settingsService,
        FakeCredentialsStorage credsStorage,
        FakePasswordStorage pwdStorage,
        FakeConflictService conflictService,
        ISyncEngine? syncEngine = null)
    {
        var profileDir = Path.Combine(Path.GetTempPath(), "qn_main_sync_profile_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profileDir);
        var tagRuleService = new TagRuleService(Path.Combine(profileDir, "tag_rules.json"));
        var tagDetectionService = new TagDetectionService(tagRuleService);
        var searchService = new SearchService();
        var hotkeyService = new GlobalHotkeyService((_, _, _, _) => true, (_, _) => true, IntPtr.Zero);
        _disposables.Add(hotkeyService);
        var clipboardCapture = new ClipboardCaptureService();
        var trayIcon = new TrayIconService(visible: false);
        _disposables.Add(trayIcon);
        var backupService = new BackupService(_dbPath, Path.Combine(profileDir, "Backups"));
        var attachments = new AttachmentStorageService(profileDir);
        var logScope = ErrorLogService.UseScopedDirectory(Path.Combine(profileDir, "Logs"));
        _disposables.Add(logScope);

        var vm = MainViewModelTestComposition.Create(
            () => new QuickNotesDbContext(_dbPath),
            tagDetectionService,
            searchService,
            settingsService,
            hotkeyService,
            clipboardCapture,
            trayIcon,
            backupService,
            tagRuleService: tagRuleService,
            attachmentStorageService: attachments,
            syncEngine: syncEngine,
            credentialsStorage: credsStorage,
            passwordStorage: pwdStorage,
            conflictService: conflictService,
            draftJournalService: NoOpDraftJournalService.Instance);
        _disposables.Add(vm);
        return vm;
    }

    [Fact]
    public async Task Status_Disabled_WhenSyncNotEnabled()
    {
        var settingsService = new SettingsService(_settingsFilePath, _ => { });
        var settings = settingsService.CurrentSettings;
        settings.CloudSync.Enabled = false;
        settingsService.SaveSettings(settings);

        var creds = new FakeCredentialsStorage();
        var pwd = new FakePasswordStorage();
        var conflict = new FakeConflictService();
        var engine = new FakeSyncEngine();

        var vm = CreateViewModel(settingsService, creds, pwd, conflict, engine);
        await vm.RefreshSyncStateAsync();

        Assert.Equal(MainSyncStatus.Disabled, vm.SyncStatus);
        Assert.Equal("☁", vm.SyncStatusIcon);
        Assert.Equal("Отключена", vm.SyncStatusShortText);
        Assert.Equal(PublicationStatusCopy.SavedOnThisPc, vm.SyncStatusSummaryText);
        Assert.Contains("отключена", vm.SyncStatusTooltip);
        Assert.False(vm.HasUnresolvedConflicts);
    }

    [Fact]
    public void StatusBar_FollowsSelectedNoteFirstLevel_NotGlobalJargon()
    {
        using (var db = new QuickNotesDbContext(_dbPath))
        {
            db.Notes.Add(new Note { Title = "Alpha", Text = "a", CreatedAt = DateTime.Now, UpdatedAt = DateTime.Now });
            db.Notes.Add(new Note { Title = "Beta", Text = "b", CreatedAt = DateTime.Now, UpdatedAt = DateTime.Now.AddMinutes(1) });
            db.SaveChanges();
        }

        var settingsService = new SettingsService(_settingsFilePath, _ => { });
        var settings = settingsService.CurrentSettings;
        settings.CloudSync.Enabled = false;
        settingsService.SaveSettings(settings);

        var vm = CreateViewModel(settingsService, new FakeCredentialsStorage(), new FakePasswordStorage(), new FakeConflictService(), new FakeSyncEngine());
        vm.ReloadAll();
        Assert.True(vm.Notes.Count >= 2);

        var local = vm.Notes.First(n => n.DisplayTitle == "Alpha");
        var pending = vm.Notes.First(n => n.DisplayTitle == "Beta");
        local.PublicationStatus = LocalCommitSyncStatus.SavedLocally;
        pending.PublicationStatus = LocalCommitSyncStatus.PendingUpload;

        vm.SelectedNote = local;
        Assert.Equal(PublicationStatusCopy.SavedOnThisPc, vm.SyncStatusSummaryText);

        vm.SelectedNote = pending;
        Assert.Equal(PublicationStatusCopy.WaitingToSend, vm.SyncStatusSummaryText);
        Assert.StartsWith(PublicationStatusCopy.WaitingToSend, vm.SyncStatusTooltip, StringComparison.Ordinal);
        Assert.DoesNotContain("Облако:", vm.SyncStatusSummaryText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SyncStatusActionTone_MatchesSelectedNoteOrGlobalStatus()
    {
        using (var db = new QuickNotesDbContext(_dbPath))
        {
            db.Notes.Add(new Note { Title = "Alpha", Text = "a", CreatedAt = DateTime.Now, UpdatedAt = DateTime.Now });
            db.SaveChanges();
        }

        var settingsService = new SettingsService(_settingsFilePath, _ => { });
        var settings = settingsService.CurrentSettings;
        settings.CloudSync.Enabled = true;
        settings.CloudSync.Bucket = "valid-bucket";
        settingsService.SaveSettings(settings);

        var conflictService = new FakeConflictService();
        var engine = new FakeSyncEngine();
        var vm = CreateViewModel(settingsService, new FakeCredentialsStorage(), new FakePasswordStorage(), conflictService, engine);
        vm.ReloadAll();
        await vm.RefreshSyncStateAsync();

        var card = vm.Notes.First();

        // 4. без выбранной заметки, глобальный Ready:
        vm.SelectedNote = null;
        Assert.Equal(SyncStatusActionTone.Success, vm.SyncStatusActionTone);

        vm.SelectedNote = card;

        // 3. выбранная SavedLocally, глобальный Ready:
        card.PublicationStatus = LocalCommitSyncStatus.SavedLocally;
        Assert.Equal(SyncStatusActionTone.Neutral, vm.SyncStatusActionTone);

        // 2. выбранная заметка Conflict, глобальный Ready:
        card.PublicationStatus = LocalCommitSyncStatus.Conflict;
        Assert.Equal(SyncStatusActionTone.Warning, vm.SyncStatusActionTone);

        // 1. выбранная заметка Error, глобальный Ready:
        card.PublicationStatus = LocalCommitSyncStatus.Error;
        Assert.Equal(SyncStatusActionTone.Error, vm.SyncStatusActionTone);
    }

    [Fact]
    public void SelectedNote_PublicationStatusChange_UpdatesStatusBar()
    {
        using (var db = new QuickNotesDbContext(_dbPath))
        {
            db.Notes.Add(new Note { Title = "Alpha", Text = "a", CreatedAt = DateTime.Now, UpdatedAt = DateTime.Now });
            db.SaveChanges();
        }

        var settingsService = new SettingsService(_settingsFilePath, _ => { });
        var settings = settingsService.CurrentSettings;
        settings.CloudSync.Enabled = false;
        settingsService.SaveSettings(settings);

        var conflictService = new FakeConflictService();
        var engine = new FakeSyncEngine();
        var vm = CreateViewModel(settingsService, new FakeCredentialsStorage(), new FakePasswordStorage(), conflictService, engine);
        vm.ReloadAll();

        var selectedCard = vm.Notes.First();
        vm.SelectedNote = selectedCard;

        var notifiedProperties = new HashSet<string>();
        vm.PropertyChanged += (sender, e) =>
        {
            if (e.PropertyName != null)
            {
                notifiedProperties.Add(e.PropertyName);
            }
        };

        SyncConflictsViewModel? openedDialogVm = null;
        vm.RequestOpenSyncConflicts += dialogVm =>
        {
            openedDialogVm = dialogVm;
            return true;
        };

        // Act
        selectedCard.PublicationStatus = LocalCommitSyncStatus.Conflict;

        // Assert Notifications
        Assert.Contains(nameof(vm.SyncStatusSummaryText), notifiedProperties);
        Assert.Contains(nameof(vm.SyncStatusTooltip), notifiedProperties);
        Assert.Contains(nameof(vm.SyncStatusActionAutomationName), notifiedProperties);
        Assert.Contains(nameof(vm.SyncStatusActionTone), notifiedProperties);

        // Assert Values
        Assert.Equal(PublicationStatusCopy.Conflict, vm.SyncStatusSummaryText);
        Assert.Equal("Открыть разрешение конфликтов синхронизации", vm.SyncStatusActionAutomationName);
        Assert.Equal(SyncStatusActionTone.Warning, vm.SyncStatusActionTone);

        // Execute and check conflict flow
        Assert.True(vm.SyncStatusActionCommand.CanExecute(null));
        vm.SyncStatusActionCommand.Execute(null);

        Assert.NotNull(openedDialogVm);
        Assert.Equal(0, engine.CallCount);

        openedDialogVm!.Dispose();
    }

    [Fact]
    public async Task Status_NeedsConfig_WhenCredentialsOrPasswordMissing()
    {
        var settingsService = new SettingsService(_settingsFilePath, _ => { });
        var settings = settingsService.CurrentSettings;
        settings.CloudSync.Enabled = true;
        settings.CloudSync.Bucket = "valid-bucket";
        settingsService.SaveSettings(settings);

        var creds = new FakeCredentialsStorage { Configured = false };
        var pwd = new FakePasswordStorage { Configured = false };
        var conflict = new FakeConflictService();
        var engine = new FakeSyncEngine();

        var vm = CreateViewModel(settingsService, creds, pwd, conflict, engine);
        await vm.RefreshSyncStateAsync();

        Assert.Equal(MainSyncStatus.NeedsConfig, vm.SyncStatus);
        Assert.Equal("⚙", vm.SyncStatusIcon);
        Assert.Equal("Настройка", vm.SyncStatusShortText);
    }

    [Fact]
    public async Task Status_Conflicts_WhenUnresolvedConflictsExist()
    {
        var settingsService = new SettingsService(_settingsFilePath, _ => { });
        var settings = settingsService.CurrentSettings;
        settings.CloudSync.Enabled = true;
        settings.CloudSync.Bucket = "valid-bucket";
        settingsService.SaveSettings(settings);

        var creds = new FakeCredentialsStorage();
        var pwd = new FakePasswordStorage();
        var conflict = new FakeConflictService { ConflictsCount = 3 };
        var engine = new FakeSyncEngine();

        var vm = CreateViewModel(settingsService, creds, pwd, conflict, engine);
        await vm.RefreshSyncStateAsync();

        Assert.Equal(MainSyncStatus.Conflicts, vm.SyncStatus);
        Assert.True(vm.HasUnresolvedConflicts);
        Assert.Equal(3, vm.UnresolvedConflictsCount);
        Assert.Equal("Конфликты: 3", vm.ConflictBadgeText);
        Assert.Equal(PublicationStatusCopy.Conflict, vm.SyncStatusSummaryText);
    }

    [Fact]
    public async Task SyncNowCommand_ExecutesEngine_UpdatesMetricsAndTooltip()
    {
        var settingsService = new SettingsService(_settingsFilePath, _ => { });
        var settings = settingsService.CurrentSettings;
        settings.CloudSync.Enabled = true;
        settings.CloudSync.Bucket = "my-bucket";
        settingsService.SaveSettings(settings);

        var creds = new FakeCredentialsStorage();
        var pwd = new FakePasswordStorage();
        var conflict = new FakeConflictService { ConflictsCount = 0 };
        var engine = new FakeSyncEngine();

        var vm = CreateViewModel(settingsService, creds, pwd, conflict, engine);
        await vm.SyncNowAsync();

        Assert.Equal(1, engine.CallCount);
        Assert.Equal(MainSyncStatus.Ready, vm.SyncStatus);
        Assert.NotNull(vm.LastSyncResult);
        Assert.NotNull(vm.LastSyncTime);
        Assert.Contains("Последний обмен", vm.SyncStatusTooltip);
        Assert.Contains("Получено пакетов: 2", vm.SyncStatusTooltip);
        Assert.Contains("Применено изменений: 4", vm.SyncStatusTooltip);
        Assert.Contains("Отправлено пакетов: 1", vm.SyncStatusTooltip);
        Assert.Equal(PublicationStatusCopy.InCloud, vm.SyncStatusSummaryText);
    }

    [Fact]
    public async Task SyncNowCommand_OfflineMapping_SetsOfflineState()
    {
        var settingsService = new SettingsService(_settingsFilePath, _ => { });
        var settings = settingsService.CurrentSettings;
        settings.CloudSync.Enabled = true;
        settings.CloudSync.Bucket = "my-bucket";
        settingsService.SaveSettings(settings);

        var creds = new FakeCredentialsStorage();
        var pwd = new FakePasswordStorage();
        var conflict = new FakeConflictService();
        var engine = new FakeSyncEngine
        {
            ExceptionToThrow = new CloudOfflineException("DNS resolution failed")
        };

        var vm = CreateViewModel(settingsService, creds, pwd, conflict, engine);
        await vm.SyncNowAsync();

        Assert.Equal(MainSyncStatus.Offline, vm.SyncStatus);
        Assert.Equal("⚠", vm.SyncStatusIcon);
        Assert.Contains("Офлайн", vm.SyncStatusShortText);
        Assert.Equal(PublicationStatusCopy.WaitingToSend, vm.SyncStatusSummaryText);
        Assert.Contains("DNS resolution failed", vm.SyncStatusTooltip);
    }

    [Fact]
    public async Task SyncStatusActionCommand_WhenSyncDisabled_OpensCloudWizardWithoutSync()
    {
        var settingsService = new SettingsService(_settingsFilePath, _ => { });
        var settings = settingsService.CurrentSettings;
        settings.CloudSync.Enabled = false;
        settingsService.SaveSettings(settings);

        var engine = new FakeSyncEngine();
        var vm = CreateViewModel(
            settingsService,
            new FakeCredentialsStorage(),
            new FakePasswordStorage(),
            new FakeConflictService(),
            engine);
        await vm.RefreshSyncStateAsync();
        Assert.Equal(MainSyncStatus.Disabled, vm.SyncStatus);

        int wizardRequests = 0;
        vm.RequestOpenCloudSetupWizard += () => wizardRequests++;
        Assert.Equal("Настроить облачные копии", vm.SyncStatusActionAutomationName);

        Assert.True(vm.SyncStatusActionCommand.CanExecute(null));
        vm.SyncStatusActionCommand.Execute(null);

        Assert.Equal(1, wizardRequests);
        Assert.Equal(0, engine.CallCount);
    }

    [Fact]
    public async Task SyncStatusActionCommand_WhenConflictsExist_OpensConflictResolutionWithoutSync()
    {
        var settingsService = new SettingsService(_settingsFilePath, _ => { });
        var settings = settingsService.CurrentSettings;
        settings.CloudSync.Enabled = true;
        settings.CloudSync.Bucket = "my-bucket";
        settingsService.SaveSettings(settings);

        var engine = new FakeSyncEngine();
        var vm = CreateViewModel(
            settingsService,
            new FakeCredentialsStorage(),
            new FakePasswordStorage(),
            new FakeConflictService { ConflictsCount = 2 },
            engine);
        await vm.RefreshSyncStateAsync();
        Assert.Equal(MainSyncStatus.Conflicts, vm.SyncStatus);

        SyncConflictsViewModel? openedDialogVm = null;
        vm.RequestOpenSyncConflicts += dialogVm =>
        {
            openedDialogVm = dialogVm;
            return true;
        };

        Assert.Equal("Открыть разрешение конфликтов синхронизации", vm.SyncStatusActionAutomationName);

        Assert.True(vm.SyncStatusActionCommand.CanExecute(null));
        vm.SyncStatusActionCommand.Execute(null);

        Assert.NotNull(openedDialogVm);
        var dialog = openedDialogVm!;
        try
        {
            Assert.Equal(0, engine.CallCount);
        }
        finally
        {
            dialog.Dispose();
        }
    }

    [Fact]
    public async Task OpenSyncConflicts_ResolvingConflicts_RefreshesCount_WithoutAutoSync()
    {
        var settingsService = new SettingsService(_settingsFilePath, _ => { });
        var settings = settingsService.CurrentSettings;
        settings.CloudSync.Enabled = true;
        settings.CloudSync.Bucket = "my-bucket";
        settingsService.SaveSettings(settings);

        var creds = new FakeCredentialsStorage();
        var pwd = new FakePasswordStorage();
        var conflict = new FakeConflictService { ConflictsCount = 2 };
        var engine = new FakeSyncEngine();

        var vm = CreateViewModel(settingsService, creds, pwd, conflict, engine);
        await vm.RefreshSyncStateAsync();
        Assert.Equal(MainSyncStatus.Conflicts, vm.SyncStatus);

        SyncConflictsViewModel? openedDialogVm = null;
        vm.RequestOpenSyncConflicts += dialogVm =>
        {
            openedDialogVm = dialogVm;
            return true;
        };

        vm.OpenSyncConflicts();
        Assert.NotNull(openedDialogVm);

        await openedDialogVm.LoadConflictsAsync();
        openedDialogVm.SelectedConflict = openedDialogVm.Conflicts.FirstOrDefault();
        Assert.NotNull(openedDialogVm.SelectedConflict);

        // Simulate conflict resolution
        conflict.ConflictsCount = 0;
        await openedDialogVm.ResolveKeepLocalAsync();

        // Check that MainViewModel updated its state
        Assert.Equal(0, vm.UnresolvedConflictsCount);
        Assert.False(vm.HasUnresolvedConflicts);
        Assert.Equal(MainSyncStatus.Ready, vm.SyncStatus);

        // Crucial requirement: Sync engine should NOT have been auto-triggered!
        Assert.Equal(0, engine.CallCount);
    }

    [Fact]
    public async Task OpenSyncConflicts_SingleSubscription_CallsRefreshCountExactlyOnce()
    {
        var settingsService = new SettingsService(_settingsFilePath, _ => { });
        var creds = new FakeCredentialsStorage();
        var pwd = new FakePasswordStorage();
        var conflict = new FakeConflictService { ConflictsCount = 1 };
        var engine = new FakeSyncEngine();

        var vm = CreateViewModel(settingsService, creds, pwd, conflict, engine);

        SyncConflictsViewModel? openedDialogVm = null;
        vm.RequestOpenSyncConflicts += dialogVm =>
        {
            openedDialogVm = dialogVm;
            return true;
        };

        vm.OpenSyncConflicts();
        Assert.NotNull(openedDialogVm);

        await openedDialogVm.LoadConflictsAsync();
        openedDialogVm.SelectedConflict = openedDialogVm.Conflicts.FirstOrDefault();
        Assert.NotNull(openedDialogVm.SelectedConflict);

        conflict.GetCountCallCount = 0;
        conflict.ConflictsCount = 0;

        await openedDialogVm.ResolveKeepLocalAsync();

        // Must be called exactly once, proving no duplicate sync/async event double-subscription
        Assert.Equal(1, conflict.GetCountCallCount);
    }

    [Fact]
    public async Task RefreshConflictsStateAsync_WhenExceptionOccurs_RethrowsAndDoesNotSwallow()
    {
        var settingsService = new SettingsService(_settingsFilePath, _ => { });
        var creds = new FakeCredentialsStorage();
        var pwd = new FakePasswordStorage();
        var conflict = new FakeConflictService
        {
            ExceptionToThrow = new InvalidOperationException("Simulated database failure during conflict refresh")
        };
        var engine = new FakeSyncEngine();

        var vm = CreateViewModel(settingsService, creds, pwd, conflict, engine);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => vm.RefreshConflictsStateAsync());
        Assert.Contains("Simulated database failure", ex.Message);
    }

    [Fact]
    public void ProductionSyncEngine_Lifetime_ReusedAcrossClicks_DisposedWithViewModel()
    {
        var settingsService = new SettingsService(_settingsFilePath, _ => { });
        var creds = new FakeCredentialsStorage();
        var pwd = new FakePasswordStorage();
        var conflict = new FakeConflictService();

        // null engine to test production instance management
        var vm = CreateViewModel(settingsService, creds, pwd, conflict, syncEngine: null);

        var engine1 = vm.GetOrCreateSyncEngineForTesting();
        var engine2 = vm.GetOrCreateSyncEngineForTesting();

        Assert.NotNull(engine1);
        Assert.Same(engine1, engine2);
        Assert.NotNull(vm.ProductionTransport);

        // Verify it implements IDisposable
        Assert.IsAssignableFrom<IDisposable>(engine1);

        // Dispose ViewModel
        vm.Dispose();

        Assert.Null(vm.ProductionSyncEngine);
        Assert.Null(vm.ProductionTransport);

        // Disposed engine must throw ObjectDisposedException
        using var db = new QuickNotesDbContext(_dbPath);
        Assert.ThrowsAsync<ObjectDisposedException>(() => engine1.RunSyncCycleAsync(db, "password"));
    }
}
