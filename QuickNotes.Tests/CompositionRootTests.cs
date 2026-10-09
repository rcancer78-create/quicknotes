using System;
using System.IO;
using System.Linq;
using System.Reflection;
using QuickNotes.App.Composition;
using QuickNotes.App.Data;
using QuickNotes.App.Models;
using QuickNotes.App.Services;
using QuickNotes.App.Services.Reminders;
using QuickNotes.App.Services.Sync;
using QuickNotes.App.ViewModels;
using Xunit;

namespace QuickNotes.Tests;

[TestCategory(TestCategories.Integration)]
public class CompositionRootTests
{
    private static string LiveProfile => Path.GetFullPath(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QuickNotes"));

    [Fact]
    public void MainViewModel_PublicConstructor_IsBoundedToTypedBundles()
    {
        var ctors = typeof(MainViewModel).GetConstructors(BindingFlags.Public | BindingFlags.Instance);
        Assert.Single(ctors);
        Assert.Equal(ApplicationCompositionRoot.MainViewModelPublicConstructorParameterBound, ctors[0].GetParameters().Length);
        var types = ctors[0].GetParameters().Select(p => p.ParameterType).ToArray();
        Assert.Equal(typeof(CoreNotesServices), types[0]);
        Assert.Equal(typeof(CaptureServices), types[1]);
        Assert.Equal(typeof(SyncCloudServices), types[2]);
        Assert.Equal(typeof(SecurityServices), types[3]);
        Assert.Equal(typeof(TasksRemindersServices), types[4]);
        Assert.Equal(typeof(UiHostServices), types[5]);
    }

    [Fact]
    public void SourceGuards_MainViewModelAndSettings_DoNotConstructProductionInfrastructure()
    {
        string root = FindRepoRoot();
        string main = File.ReadAllText(Path.Combine(root, "QuickNotes.App", "ViewModels", "MainViewModel.cs"));
        string settings = File.ReadAllText(Path.Combine(root, "QuickNotes.App", "ViewModels", "SettingsViewModel.cs"));
        string app = File.ReadAllText(Path.Combine(root, "QuickNotes.App", "App.xaml.cs"));

        string[] forbiddenInViewModels =
        {
            "new SyncEngine",
            "new SyncCloudCoordinator",
            "new S3ObjectStoreTransport",
            "new DpapiS3CredentialsStorage",
            "new DpapiSyncPasswordStorage",
            "new ReminderLedgerStore",
            "new WindowsToastAdapter",
            "new RecordingToastAdapter",
            "new DeviceIdProvider",
            "IServiceProvider",
            "GetService("
        };

        foreach (string token in forbiddenInViewModels)
        {
            Assert.False(main.Contains(token, StringComparison.Ordinal), "MainViewModel contains " + token);
            Assert.False(settings.Contains(token, StringComparison.Ordinal), "SettingsViewModel contains " + token);
        }

        Assert.DoesNotContain("new BackupService", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("new EphemeralS3CredentialsStorage", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("new EphemeralSyncPasswordStorage", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("new DpapiS3CredentialsStorage", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("new DpapiSyncPasswordStorage", settings, StringComparison.Ordinal);

        string importVmSource = File.ReadAllText(Path.Combine(root, "QuickNotes.App", "ViewModels", "ImportExportViewModel.cs"));
        Assert.DoesNotContain("new LocalMutationCoordinator", importVmSource, StringComparison.Ordinal);

        string[] forbiddenInApp =
        {
            "new SyncEngine",
            "new S3ObjectStoreTransport",
            "new DpapiS3CredentialsStorage",
            "new TaskReminderScheduler",
            "IServiceProvider"
        };
        foreach (string token in forbiddenInApp)
        {
            Assert.False(app.Contains(token, StringComparison.Ordinal), "App.xaml.cs contains " + token);
        }
    }

    [Fact]
    public void IsolatedGraph_UsesProfileScopedAdapters_AndDoesNotTouchLiveProfileOrNetwork()
    {
        string profile = CreateTempProfile();
        try
        {
            using var root = ApplicationCompositionRoot.Create(new ApplicationCompositionOptions
            {
                ProfileDirectory = profile,
                HostKind = CompositionHostKind.IsolatedProfile,
                RegisterGlobalHotkeys = false,
                TrayVisible = false
            });

            Assert.Equal(Path.GetFullPath(profile), Path.GetFullPath(root.ProfileDirectory));
            Assert.StartsWith(Path.GetFullPath(profile), Path.GetFullPath(root.S3CredentialsPath), StringComparison.OrdinalIgnoreCase);
            Assert.StartsWith(Path.GetFullPath(profile), Path.GetFullPath(root.ReminderLedgerPath), StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(LiveProfile, root.ProfileDirectory, StringComparison.OrdinalIgnoreCase);
            Assert.IsType<UnavailableCloudTransportFactory>(root.SyncCloud.TransportFactory);
            Assert.IsType<RecordingToastAdapter>(root.Tasks.Toast);
            Assert.False(root.Options.RegisterGlobalHotkeys);

            using var vm = root.CreateMainViewModel();
            var engine = vm.GetOrCreateSyncEngineForTesting();
            Assert.Same(engine, vm.GetOrCreateSyncEngineForTesting());
            Assert.IsType<UnavailableCloudObjectStoreTransport>(vm.ProductionTransport);
            Assert.Same(root.Core.MutationCoordinator, vm.MutationCoordinator);
            Assert.Same(root.Core.MutationCoordinator, ((SyncConflictService)root.SyncCloud.Conflicts).MutationCoordinator);
            Assert.Same(root.Core.MutationCoordinator, ((SyncEngine)engine).MutationCoordinator);
            Assert.Same(root.SyncCloud.Scheduler, vm.SyncScheduler);
            Assert.Equal(root.DbPath, root.Ui.Backup.DbPath);
            Assert.Equal(Path.Combine(Path.GetFullPath(profile), "Backups"), root.Ui.Backup.BackupDirectory);

            var importVm = new ImportExportViewModel(
                root.Core.ContextFactory,
                root.Core.MutationCoordinator,
                root.Core.Export,
                root.Core.Import,
                root.Core.History,
                root.Core.TagDetection,
                attachmentStorage: root.Core.Attachments,
                settingsService: root.Core.Settings);
            Assert.Same(root.Core.MutationCoordinator, importVm.MutationCoordinatorForTests);

            using var editor = new NoteEditorViewModel(
                root.Core.TagDetection,
                new System.Collections.Generic.List<Tag>(),
                contextFactory: root.Core.ContextFactory,
                settingsService: root.Core.Settings,
                attachmentStorageService: root.Core.Attachments,
                noteProtectionService: root.Security.Protection,
                draftJournalService: root.Core.DraftJournal,
                mutationCoordinator: root.Core.MutationCoordinator);
            Assert.Same(root.Core.MutationCoordinator, editor.MutationCoordinatorForTests);
        }
        finally
        {
            SqliteTestUtil.TryDeleteDirectory(profile);
        }
    }

    [Fact]
    public async Task ProductionLikeGraph_SharesCoordinatorAndCreatesS3TransportWithoutLiveProfile()
    {
        string profile = CreateTempProfile();
        try
        {
            using var root = ApplicationCompositionRoot.Create(new ApplicationCompositionOptions
            {
                ProfileDirectory = profile,
                HostKind = CompositionHostKind.ProductionDesktop,
                RegisterGlobalHotkeys = false,
                TrayVisible = false,
                ToastAdapter = new RecordingToastAdapter(),
                HotkeyService = new GlobalHotkeyService((_, _, _, _) => true, (_, _) => true, IntPtr.Zero)
            });

            Assert.IsType<S3CloudTransportFactory>(root.SyncCloud.TransportFactory);
            Assert.IsType<RecordingToastAdapter>(root.Tasks.Toast);
            Assert.StartsWith(Path.GetFullPath(profile), Path.GetFullPath(root.DbPath), StringComparison.OrdinalIgnoreCase);

            using var vm = root.CreateMainViewModel();
            var engine1 = vm.GetOrCreateSyncEngineForTesting();
            var engine2 = vm.GetOrCreateSyncEngineForTesting();
            Assert.Same(engine1, engine2);
            Assert.IsType<S3ObjectStoreTransport>(vm.ProductionTransport);
            Assert.Same(root.Core.MutationCoordinator, ((SyncEngine)engine1).MutationCoordinator);

            var settingsVm = vm.CreateSettingsViewModel();
            Assert.Same(root.SyncCloud.Scheduler, settingsVm.SyncSchedulerForTests);
            Assert.Same(vm.GetOrCreateCloudUsageService(), settingsVm.CloudUsageServiceForTests);
            Assert.Same(root.Tasks.Reminders, settingsVm.TaskReminderSchedulerForTests);

            var engine = engine1;
            vm.Dispose();
            Assert.Equal(0, root.SyncCloud.DisposeCount);
            Assert.Same(engine, root.SyncCloud.CurrentEngine);
            Assert.NotNull(vm.ProductionSyncEngine);

            root.Dispose();
            Assert.Equal(1, root.SyncCloud.DisposeCount);
            using var db = new QuickNotesDbContext(root.DbPath);
            await Assert.ThrowsAsync<ObjectDisposedException>(() => engine.RunSyncCycleAsync(db, "password"));
            root.Dispose();
            Assert.Equal(1, root.SyncCloud.DisposeCount);
        }
        finally
        {
            SqliteTestUtil.TryDeleteDirectory(profile);
        }
    }

    [Fact]
    public void RootOwnsInfrastructure_ViewModelUnsubscribeStopsCallbacks_WithoutDisposingBundles()
    {
        string profile = CreateTempProfile();
        var scheduler = new FakeTrackingSyncScheduler();
        try
        {
            using var root = ApplicationCompositionRoot.Create(new ApplicationCompositionOptions
            {
                ProfileDirectory = profile,
                HostKind = CompositionHostKind.IsolatedProfile,
                TrayVisible = false,
                SyncScheduler = scheduler
            });

            var vm = root.CreateMainViewModel();
            var statusBefore = vm.SyncStatus;
            vm.Dispose();
            Assert.Equal(0, root.SyncCloud.DisposeCount);

            scheduler.RaiseStatusChanged(new SyncSchedulerStatusChangedEventArgs(
                SyncSchedulerQueueState.Syncing,
                "syncing-after-dispose",
                Array.Empty<SyncTriggerReason>(),
                DateTime.UtcNow,
                DateTime.UtcNow,
                4,
                null));

            Assert.Equal(statusBefore, vm.SyncStatus);
            Assert.False(vm.IsSyncBusy);
            Assert.Equal(0, vm.UnresolvedConflictsCount);
        }
        finally
        {
            SqliteTestUtil.TryDeleteDirectory(profile);
        }
    }

    [Fact]
    public void StartupBackupService_IsProfileScoped_AndSharedWithRecoveryAndRoot()
    {
        string profile = CreateTempProfile();
        try
        {
            var isolated = new IsolatedProfileAppOptions
            {
                IsIsolated = true,
                ProfileDirectory = profile
            };
            Assert.Equal(Path.GetFullPath(profile), ApplicationCompositionRoot.ResolveProfileDirectory(isolated));
            var backup = ApplicationCompositionRoot.CreateProfileBackupService(profile);
            Assert.Equal(Path.Combine(Path.GetFullPath(profile), "quicknotes.db"), backup.DbPath);
            Assert.Equal(Path.Combine(Path.GetFullPath(profile), "Backups"), backup.BackupDirectory);

            var recovery = new DatabaseRecoveryService(backup.DbPath, backup);
            using var root = ApplicationCompositionRoot.CreateForStartup(isolated, backup);
            Assert.Same(backup, root.Ui.Backup);
            Assert.Equal(backup.DbPath, root.DbPath);

            string backupPath = backup.CreateBackup();
            Assert.True(File.Exists(backupPath));
            Assert.StartsWith(Path.GetFullPath(backup.BackupDirectory), Path.GetFullPath(backupPath), StringComparison.OrdinalIgnoreCase);
            var found = recovery.FindLatestHealthyBackup(out _);
            Assert.NotNull(found);
            Assert.Equal(Path.GetFullPath(backupPath), Path.GetFullPath(found!.FullName));
        }
        finally
        {
            SqliteTestUtil.TryDeleteDirectory(profile);
        }
    }

    [Fact]
    public void DoubleDispose_OfRootAndViewModel_IsSafe()
    {
        string profile = CreateTempProfile();
        var root = ApplicationCompositionRoot.Create(new ApplicationCompositionOptions
        {
            ProfileDirectory = profile,
            HostKind = CompositionHostKind.IsolatedProfile,
            TrayVisible = false
        });
        var vm = root.CreateMainViewModel();
        vm.Dispose();
        vm.Dispose();
        root.Dispose();
        root.Dispose();
        SqliteTestUtil.TryDeleteDirectory(profile);
    }

    private static string CreateTempProfile()
    {
        string dir = Path.Combine(Path.GetTempPath(), "qn_compgraph_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        using var db = new QuickNotesDbContext(Path.Combine(dir, "quicknotes.db"));
        DbInitializer.Initialize(db);
        return dir;
    }

    private static string FindRepoRoot()
    {
        string dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8; i++)
        {
            if (File.Exists(Path.Combine(dir, "QuickNotes.sln")))
            {
                return dir;
            }

            dir = Directory.GetParent(dir)!.FullName;
        }

        throw new InvalidOperationException("Repository root not found.");
    }
}
