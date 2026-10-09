using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using QuickNotes.App.Data;
using QuickNotes.App.Helpers;
using QuickNotes.App.Models;
using QuickNotes.App.Models.Sync;
using QuickNotes.App.Services;
using QuickNotes.App.Services.EncryptedArchive;
using QuickNotes.App.Services.Sync;
using QuickNotes.App.ViewModels;
using Xunit;

namespace QuickNotes.Tests;

[TestCategory(TestCategories.Integration)]
public sealed class AsyncBoundaryTests : IDisposable
{
    private readonly string _root;
    private readonly IDisposable _logScope;

    public AsyncBoundaryTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "qn_async_10_4_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _logScope = ErrorLogService.UseScopedDirectory(Path.Combine(_root, "Logs"));
    }

    public void Dispose()
    {
        NoteImportService.TestInjectFailure = null;
        NoteArchiveService.TestBeforeCommit = null;
        BoundedOperation.OwnedTerminalGrace = TimeSpan.FromSeconds(2);
        Win32Helper.ResetBrowserUrlProbeStateForTests();
        _logScope.Dispose();
        SqliteTestUtil.TryDeleteDirectory(_root);
    }

    [Fact]
    public async Task BoundedOperation_Success_ReturnsValue()
    {
        var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = BoundedOperation.RunAsync(async ct =>
        {
            using (ct.Register(() => tcs.TrySetCanceled(ct)))
            {
                tcs.TrySetResult(7);
                return await tcs.Task;
            }
        }, TimeSpan.FromSeconds(5), CancellationToken.None, "test.success");

        var result = await run;
        Assert.Equal(BoundedOperationOutcome.Success, result.Outcome);
        Assert.Equal(7, result.Value);
    }

    [Fact]
    public async Task BoundedOperation_CallerCancel_IsCancelled()
    {
        using var cts = new CancellationTokenSource();
        var never = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = BoundedOperation.RunAsync(
            ct => never.Task.WaitAsync(ct),
            TimeSpan.FromSeconds(30),
            cts.Token,
            "test.cancel");
        cts.Cancel();
        var result = await run;
        Assert.Equal(BoundedOperationOutcome.Cancelled, result.Outcome);
        Assert.Equal(UserFacingOperationError.Cancelled, result.UserMessage);
    }

    [Fact]
    public async Task BoundedOperation_Timeout_DoesNotUseSleepAsPrimarySync()
    {
        var never = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var result = await BoundedOperation.RunAsync(
            ct => never.Task.WaitAsync(ct),
            TimeSpan.FromMilliseconds(40),
            CancellationToken.None,
            "test.timeout");
        Assert.Equal(BoundedOperationOutcome.Timeout, result.Outcome);
        Assert.Equal(UserFacingOperationError.Timeout, result.UserMessage);
    }

    [Fact]
    public async Task BoundedOperation_Exception_IsContainedAndDoesNotLeakMessage()
    {
        var result = await BoundedOperation.RunAsync<int>(
            _ => throw new InvalidOperationException("internal-token-do-not-leak"),
            TimeSpan.FromSeconds(5),
            CancellationToken.None,
            "test.fail");
        Assert.Equal(BoundedOperationOutcome.Failed, result.Outcome);
        Assert.Equal(UserFacingOperationError.GenericFailure, result.UserMessage);
        Assert.DoesNotContain("internal-token", result.UserMessage);
    }

    [Fact]
    public async Task AsyncEventBridge_RestoresSafeState_OnFault()
    {
        bool restored = false;
        var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        AsyncEventBridge.Fire(
            () => throw new InvalidOperationException("bridge-fault"),
            "test.bridge",
            () =>
            {
                restored = true;
                observed.TrySetResult();
            });
        await observed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(restored);
    }

    [Fact]
    public async Task RelayCommand_FuncTask_ContainsException()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cmd = new RelayCommand((Func<Task>)(() =>
        {
            try
            {
                throw new InvalidOperationException("command-fault");
            }
            finally
            {
                done.TrySetResult();
            }
        }));
        cmd.Execute(null);
        await done.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(cmd.CanExecute(null));
    }

    [Fact]
    public async Task RelayCommand_FuncTask_RejectsReentrancyUntilFirstCompletes()
    {
        int started = 0;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cmd = new RelayCommand((Func<Task>)(async () =>
        {
            Interlocked.Increment(ref started);
            entered.TrySetResult();
            await release.Task;
        }));

        cmd.Execute(null);
        cmd.Execute(null);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, started);
        Assert.False(cmd.CanExecute(null));
        release.TrySetResult();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!cmd.CanExecute(null) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        Assert.True(cmd.CanExecute(null));
        Assert.Equal(1, started);
    }

    [Fact]
    public async Task BoundedOperation_WaitForOwnedWork_TimeoutWaitsUntilCancelIsObserved()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool published = false;
        var run = BoundedOperation.RunAsync(async ct =>
        {
            entered.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
            }

            published = !ct.IsCancellationRequested;
            finished.TrySetResult();
            ct.ThrowIfCancellationRequested();
            return 1;
        }, TimeSpan.FromMilliseconds(40), CancellationToken.None, "test.owned-timeout");

        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var result = await run.WaitAsync(TimeSpan.FromSeconds(5));
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(BoundedOperationOutcome.Timeout, result.Outcome);
        Assert.False(published);
    }

    [Fact]
    public async Task BoundedOperation_AbandonAndObserve_ReturnsTimeoutWhileWorkStillRuns()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = BoundedOperation.RunAsync(
            async ct =>
            {
                entered.TrySetResult();
                return await release.Task;
            },
            TimeSpan.FromMilliseconds(40),
            CancellationToken.None,
            "test.abandon",
            BoundedTimeoutBehavior.AbandonAndObserve);

        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var result = await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(BoundedOperationOutcome.Timeout, result.Outcome);
        Assert.False(release.Task.IsCompleted);
        release.TrySetResult(9);
    }

    [Fact]
    public async Task LifecycleShutdown_WaitOwned_ReachesTerminalBeforeCallerMayDispose()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int inTeardown = 1;
        using var cts = new CancellationTokenSource();
        var shutdown = Task.Run(async () =>
        {
            started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, cts.Token);
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                Volatile.Write(ref inTeardown, 0);
            }
        });

        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        bool completed = LifecycleShutdown.WaitOwned(shutdown, TimeSpan.FromMilliseconds(40), cts);
        Assert.True(completed);
        Assert.Equal(0, Volatile.Read(ref inTeardown));
    }

    [Fact]
    public async Task ImportExport_OpenExport_Timeout_DoesNotPublishLater_AndRejectsOverlap()
    {
        string dbPath = Path.Combine(_root, "export.db");
        using var db = SqliteTestUtil.CreateContext(dbPath);
        DbInitializer.Initialize(db);
        db.Notes.Add(new Note { Text = "export me" });
        db.SaveChanges();

        var hanging = new HangingExportService();
        var vm = ImportExportViewModelTestComposition.Create(
            () => SqliteTestUtil.CreateContext(dbPath),
            hanging);
        vm.ExportTimeout = TimeSpan.FromMilliseconds(50);
        vm.FolderBrowserDialogProvider = () => Path.Combine(_root, "out");
        vm.ConfirmProvider = (_, _) => true;
        vm.MessageBoxProvider = (_, _, _) => { };

        var export = vm.ExecuteExportMarkdownAsync();
        await hanging.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(vm.IsBusy);
        await vm.ExecuteExportMarkdownAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, hanging.Calls);
        await export.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(vm.IsBusy);
        Assert.False(vm.IsExportSuccess);
        Assert.False(hanging.Published);
        Assert.Equal(UserFacingOperationError.Timeout, vm.ExportStatusMessage);
        hanging.Release.TrySetResult(ExportResultSuccess());
        Assert.False(hanging.Published);
    }

    [Fact]
    public async Task ImportExport_ConfirmImport_Timeout_DoesNotPublishLater_AndRejectsOverlap()
    {
        string dbPath = Path.Combine(_root, "import.db");
        using (var init = SqliteTestUtil.CreateContext(dbPath))
        {
            DbInitializer.Initialize(init);
        }

        string notePath = Path.Combine(_root, "ok.md");
        File.WriteAllText(notePath, "hello import");
        var hanging = new HangingImportService();
        var vm = ImportExportViewModelTestComposition.Create(
            () => SqliteTestUtil.CreateContext(dbPath),
            importService: hanging);
        vm.ImportTimeout = TimeSpan.FromMilliseconds(50);
        vm.MessageBoxProvider = (_, _, _) => { };
        vm.LoadPreview(() =>
        {
            using var db = SqliteTestUtil.CreateContext(dbPath);
            return new NoteImportService().BuildPreviewFromFiles(db, new[] { notePath });
        });
        Assert.True(vm.CanConfirmImport);

        var import = vm.ExecuteConfirmImportAsync();
        await hanging.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(vm.IsBusy);
        await vm.ExecuteConfirmImportAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, hanging.Calls);
        await import.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(vm.IsBusy);
        Assert.False(vm.IsImportSuccess);
        Assert.False(hanging.Published);
        Assert.Equal(UserFacingOperationError.Timeout, vm.ImportStatusMessage);
        hanging.Release.TrySetResult(new ImportExecutionResult { Success = true, ImportedNotesCount = 1 });
        Assert.False(hanging.Published);
        using var verify = SqliteTestUtil.CreateContext(dbPath);
        Assert.Equal(0, verify.Notes.Count());
    }

    [Fact]
    public async Task ImportExport_CreateEncryptedArchive_Timeout_DoesNotPublishLater_AndRejectsOverlap()
    {
        using (var init = SqliteTestUtil.CreateContext(Path.Combine(_root, "arch.db")))
        {
            DbInitializer.Initialize(init);
        }

        var hanging = new HangingArchiveService();
        var vm = ImportExportViewModelTestComposition.Create(
            () => SqliteTestUtil.CreateContext(Path.Combine(_root, "arch.db")),
            encryptedArchiveService: hanging);
        vm.EncryptedArchiveTimeout = TimeSpan.FromMilliseconds(50);
        vm.EncryptedArchiveDestinationPath = Path.Combine(_root, "late.qnar");
        vm.SetEncryptedArchivePasswordInput("pw");
        vm.SetEncryptedArchivePasswordConfirmInput("pw");
        vm.MessageBoxProvider = (_, _, _) => { };

        var create = vm.ExecuteCreateEncryptedArchiveAsync();
        await hanging.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(vm.IsEncryptedArchiveBusy);
        await vm.ExecuteCreateEncryptedArchiveAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, hanging.Calls);
        await create.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(vm.IsEncryptedArchiveBusy);
        Assert.False(hanging.Published);
        Assert.False(vm.HasPendingRecoveryVerification);
        Assert.Equal(UserFacingOperationError.Timeout, vm.EncryptedArchiveStatusMessage);
        hanging.Release.TrySetResult();
        Assert.False(hanging.Published);
        Assert.False(File.Exists(vm.EncryptedArchiveDestinationPath));
    }

    [Fact]
    public async Task ImportExport_CreateEncryptedArchive_CommandTimeoutGrace_KeepsBusyUntilOrphanTerminal()
    {
        TimeSpan previous = BoundedOperation.OwnedTerminalGrace;
        BoundedOperation.OwnedTerminalGrace = TimeSpan.FromMilliseconds(40);
        using (var init = SqliteTestUtil.CreateContext(Path.Combine(_root, "arch_lease.db")))
        {
            DbInitializer.Initialize(init);
        }

        var hanging = new NonCooperativeArchiveService();
        var vm = ImportExportViewModelTestComposition.Create(
            () => SqliteTestUtil.CreateContext(Path.Combine(_root, "arch_lease.db")),
            encryptedArchiveService: hanging);
        vm.EncryptedArchiveTimeout = TimeSpan.FromMilliseconds(40);
        vm.EncryptedArchiveDestinationPath = Path.Combine(_root, "lease.qnar");
        vm.SetEncryptedArchivePasswordInput("pw");
        vm.SetEncryptedArchivePasswordConfirmInput("pw");
        vm.MessageBoxProvider = (_, _, _) => { };

        try
        {
            var timeoutShown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(vm.EncryptedArchiveStatusMessage) &&
                    vm.EncryptedArchiveStatusMessage == UserFacingOperationError.Timeout)
                {
                    timeoutShown.TrySetResult();
                }
            };

            Assert.True(vm.CreateEncryptedArchiveCommand.CanExecute(null));
            vm.CreateEncryptedArchiveCommand.Execute(null);
            await hanging.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await timeoutShown.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.True(vm.IsEncryptedArchiveBusy);
            Assert.False(vm.CreateEncryptedArchiveCommand.CanExecute(null));
            Assert.False(vm.CanEditEncryptedArchiveCreateInputs);
            await vm.ExecuteCreateEncryptedArchiveAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, hanging.Calls);
            vm.CreateEncryptedArchiveCommand.Execute(null);
            Assert.Equal(1, hanging.Calls);
            Assert.Equal(UserFacingOperationError.Timeout, vm.EncryptedArchiveStatusMessage);

            var idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(vm.IsEncryptedArchiveBusy) && !vm.IsEncryptedArchiveBusy)
                {
                    idle.TrySetResult();
                }
            };
            hanging.Release.TrySetResult();
            await idle.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(vm.IsEncryptedArchiveBusy);
            Assert.False(vm.HasPendingRecoveryVerification);
            Assert.Equal(UserFacingOperationError.Timeout, vm.EncryptedArchiveStatusMessage);
        }
        finally
        {
            BoundedOperation.OwnedTerminalGrace = previous;
            hanging.Release.TrySetResult();
        }
    }

    [Fact]
    public async Task ScreenOcr_NonCooperativeRecognize_TimesOut_RestoresUi_AndIgnoresLateResult()
    {
        var hangingOcr = new NonCooperativeOcrService();
        var capture = new ImmediateCaptureService();
        var visibility = new RecordingOcrVisibility();
        var coordinator = new ScreenOcrCoordinator(capture, hangingOcr, visibility);

        var resultTask = coordinator.ExecuteOcrWorkflowAsync(
            cancellationToken: CancellationToken.None,
            recognizeTimeout: TimeSpan.FromMilliseconds(40));
        await hangingOcr.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var result = await resultTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(result.IsTimeout);
        Assert.False(result.IsCancelled);
        Assert.False(result.Success);
        Assert.Equal(UserFacingOperationError.Timeout, result.ErrorMessage);
        Assert.True(visibility.RestoreCount >= 1);

        hangingOcr.Release.TrySetResult(OcrResult.Succeeded("late-must-ignore"));
        await Task.Yield();
        Assert.True(result.IsTimeout);
        Assert.Null(result.Text);
    }

    [Fact]
    public async Task MainViewModel_SyncNow_Timeout_SetsErrorState()
    {
        string dbPath = Path.Combine(_root, "sync.db");
        using (var init = new QuickNotesDbContext(dbPath))
        {
            init.Database.EnsureCreated();
        }

        string settingsPath = Path.Combine(_root, "settings.json");
        var settingsService = new SettingsService(settingsPath, _ => { });
        var settings = settingsService.CurrentSettings;
        settings.CloudSync.Enabled = true;
        settings.CloudSync.Bucket = "bucket";
        settingsService.SaveSettings(settings);

        var hanging = new HangingSyncScheduler();
        var creds = new EphemeralS3CredentialsStorage();
        await creds.SaveCredentialsAsync(new S3Credentials("K", "S"));
        var pwd = new EphemeralSyncPasswordStorage();
        await pwd.SavePasswordAsync("pw");

        var hotkey = new GlobalHotkeyService((_, _, _, _) => true, (_, _) => true, IntPtr.Zero);
        var tray = new TrayIconService();
        var vm = MainViewModelTestComposition.Create(
            () => new QuickNotesDbContext(dbPath),
            new TagDetectionService(),
            new SearchService(),
            settingsService,
            hotkey,
            new ClipboardCaptureService(),
            tray,
            backupService: new BackupService(dbPath, Path.Combine(_root, "bak")),
            credentialsStorage: creds,
            passwordStorage: pwd,
            syncScheduler: hanging);
        vm.ManualSyncTimeout = TimeSpan.FromMilliseconds(50);

        await vm.SyncNowAsync();
        Assert.False(vm.IsSyncBusy);
        Assert.Equal(MainSyncStatus.Error, vm.SyncStatus);
        Assert.Equal(UserFacingOperationError.Timeout, vm.StatusText);
        hanging.Release.TrySetResult(SyncCycleResult.Succeeded());
        vm.Dispose();
        hotkey.Dispose();
        tray.Dispose();
    }

    [Fact]
    public async Task MainViewModel_SyncNow_Cancel_SetsCancelledStatus()
    {
        string dbPath = Path.Combine(_root, "sync_cancel.db");
        using (var init = new QuickNotesDbContext(dbPath))
        {
            init.Database.EnsureCreated();
        }

        string settingsPath = Path.Combine(_root, "settings_cancel.json");
        var settingsService = new SettingsService(settingsPath, _ => { });
        var settings = settingsService.CurrentSettings;
        settings.CloudSync.Enabled = true;
        settings.CloudSync.Bucket = "bucket";
        settingsService.SaveSettings(settings);

        var hanging = new HangingSyncScheduler();
        var creds = new EphemeralS3CredentialsStorage();
        await creds.SaveCredentialsAsync(new S3Credentials("K", "S"));
        var pwd = new EphemeralSyncPasswordStorage();
        await pwd.SavePasswordAsync("pw");

        var hotkey = new GlobalHotkeyService((_, _, _, _) => true, (_, _) => true, IntPtr.Zero);
        var tray = new TrayIconService();
        var vm = MainViewModelTestComposition.Create(
            () => new QuickNotesDbContext(dbPath),
            new TagDetectionService(),
            new SearchService(),
            settingsService,
            hotkey,
            new ClipboardCaptureService(),
            tray,
            backupService: new BackupService(dbPath, Path.Combine(_root, "bak_cancel")),
            credentialsStorage: creds,
            passwordStorage: pwd,
            syncScheduler: hanging);
        vm.ManualSyncTimeout = TimeSpan.FromMinutes(5);

        Task sync = vm.SyncNowAsync();
        await hanging.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        vm.CancelSync();
        await sync.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(vm.IsSyncBusy);
        Assert.Equal(UserFacingOperationError.Cancelled, vm.StatusText);
        vm.Dispose();
        hotkey.Dispose();
        tray.Dispose();
    }

    [Fact]
    public async Task MainViewModel_SyncNow_NonCooperative_TimeoutKeepsBusy_AndIgnoresLateApply()
    {
        TimeSpan previous = BoundedOperation.OwnedTerminalGrace;
        BoundedOperation.OwnedTerminalGrace = TimeSpan.FromMilliseconds(40);
        string dbPath = Path.Combine(_root, "sync_noncoop.db");
        using (var init = new QuickNotesDbContext(dbPath))
        {
            init.Database.EnsureCreated();
        }

        string settingsPath = Path.Combine(_root, "settings_noncoop.json");
        var settingsService = new SettingsService(settingsPath, _ => { });
        var settings = settingsService.CurrentSettings;
        settings.CloudSync.Enabled = true;
        settings.CloudSync.Bucket = "bucket";
        settingsService.SaveSettings(settings);

        var hanging = new NonCooperativeSyncScheduler();
        var creds = new EphemeralS3CredentialsStorage();
        await creds.SaveCredentialsAsync(new S3Credentials("K", "S"));
        var pwd = new EphemeralSyncPasswordStorage();
        await pwd.SavePasswordAsync("pw");

        var hotkey = new GlobalHotkeyService((_, _, _, _) => true, (_, _) => true, IntPtr.Zero);
        var tray = new TrayIconService();
        var vm = MainViewModelTestComposition.Create(
            () => new QuickNotesDbContext(dbPath),
            new TagDetectionService(),
            new SearchService(),
            settingsService,
            hotkey,
            new ClipboardCaptureService(),
            tray,
            backupService: new BackupService(dbPath, Path.Combine(_root, "bak_noncoop")),
            credentialsStorage: creds,
            passwordStorage: pwd,
            syncScheduler: hanging);
        vm.ManualSyncTimeout = TimeSpan.FromMilliseconds(40);

        try
        {
            Task sync = vm.SyncNowAsync();
            await hanging.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await sync.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.True(vm.IsSyncBusy);
            Assert.Equal(MainSyncStatus.Error, vm.SyncStatus);
            Assert.Equal(UserFacingOperationError.Timeout, vm.StatusText);
            Assert.Null(vm.LastSyncResult);
            await vm.SyncNowAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, hanging.ManualSyncCallCount);
            Assert.Equal(0, hanging.StartCallCount);

            var late = SyncCycleResult.Succeeded(remotePackagesPulled: 3);
            hanging.Release.TrySetResult(late);
            var idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(vm.IsSyncBusy) && !vm.IsSyncBusy)
                {
                    idle.TrySetResult();
                }
            };
            await idle.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(vm.IsSyncBusy);
            Assert.Equal(MainSyncStatus.Error, vm.SyncStatus);
            Assert.Equal(UserFacingOperationError.Timeout, vm.StatusText);
            Assert.Null(vm.LastSyncResult);
            Assert.Equal(UserFacingOperationError.Timeout, vm.LastSyncError);
        }
        finally
        {
            BoundedOperation.OwnedTerminalGrace = previous;
            hanging.Release.TrySetResult(SyncCycleResult.Succeeded());
            vm.Dispose();
            hotkey.Dispose();
            tray.Dispose();
        }
    }

    [Fact]
    public async Task MainViewModel_SyncNow_LateSchedulerEvent_DoesNotUpdateConflictsWhileLeaseHeld()
    {
        TimeSpan previous = BoundedOperation.OwnedTerminalGrace;
        BoundedOperation.OwnedTerminalGrace = TimeSpan.FromMilliseconds(40);
        string dbPath = Path.Combine(_root, "sync_late_conflicts.db");
        using (var init = new QuickNotesDbContext(dbPath))
        {
            init.Database.EnsureCreated();
        }

        string settingsPath = Path.Combine(_root, "settings_late_conflicts.json");
        var settingsService = new SettingsService(settingsPath, _ => { });
        var settings = settingsService.CurrentSettings;
        settings.CloudSync.Enabled = true;
        settings.CloudSync.Bucket = "bucket";
        settingsService.SaveSettings(settings);

        var hanging = new NonCooperativeSyncScheduler();
        var creds = new EphemeralS3CredentialsStorage();
        await creds.SaveCredentialsAsync(new S3Credentials("K", "S"));
        var pwd = new EphemeralSyncPasswordStorage();
        await pwd.SavePasswordAsync("pw");

        var hotkey = new GlobalHotkeyService((_, _, _, _) => true, (_, _) => true, IntPtr.Zero);
        var tray = new TrayIconService();
        var vm = MainViewModelTestComposition.Create(
            () => new QuickNotesDbContext(dbPath),
            new TagDetectionService(),
            new SearchService(),
            settingsService,
            hotkey,
            new ClipboardCaptureService(),
            tray,
            backupService: new BackupService(dbPath, Path.Combine(_root, "bak_late_conflicts")),
            credentialsStorage: creds,
            passwordStorage: pwd,
            syncScheduler: hanging);
        vm.ManualSyncTimeout = TimeSpan.FromMilliseconds(40);

        try
        {
            Task sync = vm.SyncNowAsync();
            await hanging.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await sync.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.True(vm.IsSyncBusy);
            Assert.Equal(0, vm.UnresolvedConflictsCount);

            hanging.UnresolvedConflictsCount = 7;
            hanging.RaiseStatusChanged(new SyncSchedulerStatusChangedEventArgs(
                SyncSchedulerQueueState.Idle,
                "late-conflicts",
                Array.Empty<SyncTriggerReason>(),
                DateTime.UtcNow,
                DateTime.UtcNow,
                7,
                SyncCycleResult.Succeeded(remotePackagesPulled: 2)));

            Assert.Equal(0, vm.UnresolvedConflictsCount);
            Assert.Null(vm.LastSyncResult);
            Assert.Equal(MainSyncStatus.Error, vm.SyncStatus);
            Assert.Equal(UserFacingOperationError.Timeout, vm.StatusText);

            var idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(vm.IsSyncBusy) && !vm.IsSyncBusy)
                {
                    idle.TrySetResult();
                }
            };
            hanging.Release.TrySetResult(SyncCycleResult.Succeeded(remotePackagesPulled: 2));
            await idle.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(vm.IsSyncBusy);
            Assert.Equal(7, vm.UnresolvedConflictsCount);
            Assert.Null(vm.LastSyncResult);
            Assert.Equal(MainSyncStatus.Error, vm.SyncStatus);
            Assert.Equal(UserFacingOperationError.Timeout, vm.StatusText);
        }
        finally
        {
            BoundedOperation.OwnedTerminalGrace = previous;
            hanging.Release.TrySetResult(SyncCycleResult.Succeeded());
            vm.Dispose();
            hotkey.Dispose();
            tray.Dispose();
        }
    }

    [Fact]
    public async Task Settings_CreateBackup_Timeout_DoesNotPublishLater_AndRejectsOverlap()
    {
        string settingsPath = Path.Combine(_root, "settings_backup.json");
        var shown = new TaskCompletionSource<(string msg, string title)>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var hotkey = new GlobalHotkeyService((_, _, _, _) => true, (_, _) => true, IntPtr.Zero);
        var hanging = new HangingBackupService(Path.Combine(_root, "missing.db"), Path.Combine(_root, "bak_ui"));
        var vm = SettingsViewModelTestComposition.Create(
            new SettingsService(settingsPath, _ => { }),
            hotkey,
            backupService: hanging,
            showMessage: (msg, title, _) => shown.TrySetResult((msg, title)));
        vm.BackupTimeout = TimeSpan.FromMilliseconds(50);

        var first = vm.CreateBackupAsync();
        await hanging.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(vm.IsBackupBusy);
        await vm.CreateBackupAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, hanging.Calls);
        await first.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(vm.IsBackupBusy);
        Assert.False(hanging.Published);
        Assert.Equal(UserFacingOperationError.Timeout, vm.BackupStatusText);
        var dialog = await shown.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(UserFacingOperationError.Timeout, dialog.msg);
        hanging.Release.TrySetResult();
        Assert.False(hanging.Published);
    }

    [Fact]
    public async Task Settings_CreateBackup_CloseDuringNonCooperative_SuppressesLateUi()
    {
        TimeSpan previous = BoundedOperation.OwnedTerminalGrace;
        BoundedOperation.OwnedTerminalGrace = TimeSpan.FromMilliseconds(40);
        var messages = new List<(string msg, string title)>();
        using var hotkey = new GlobalHotkeyService((_, _, _, _) => true, (_, _) => true, IntPtr.Zero);
        var hanging = new NonCooperativeBackupService(Path.Combine(_root, "missing_close.db"), Path.Combine(_root, "bak_close"));
        var vm = SettingsViewModelTestComposition.Create(
            new SettingsService(Path.Combine(_root, "settings_backup_close.json"), _ => { }),
            hotkey,
            backupService: hanging,
            showMessage: (msg, title, _) => messages.Add((msg, title)));
        vm.BackupTimeout = TimeSpan.FromMilliseconds(50);

        bool closed = false;
        int mutationsAfterClose = 0;
        vm.PropertyChanged += (_, e) =>
        {
            if (!closed)
            {
                return;
            }

            if (e.PropertyName is nameof(vm.IsBackupBusy) or nameof(vm.BackupStatusText))
            {
                Interlocked.Increment(ref mutationsAfterClose);
            }
        };

        Exception? unobserved = null;
        EventHandler<UnobservedTaskExceptionEventArgs> onUnobserved = (_, e) =>
        {
            unobserved = e.Exception;
            e.SetObserved();
        };
        TaskScheduler.UnobservedTaskException += onUnobserved;
        try
        {
            Task backup = vm.CreateBackupAsync();
            await hanging.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(vm.IsBackupBusy);
            string statusAtClose = vm.BackupStatusText;
            closed = true;
            vm.NotifyClosed();
            await backup.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Empty(messages);
            Assert.Equal(statusAtClose, vm.BackupStatusText);
            Assert.True(vm.IsBackupBusy);

            hanging.Release.TrySetResult();
            await hanging.Completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            GC.Collect();
            GC.WaitForPendingFinalizers();
            Assert.Empty(messages);
            Assert.Equal(0, mutationsAfterClose);
            Assert.Equal(statusAtClose, vm.BackupStatusText);
            Assert.True(vm.IsBackupBusy);
            Assert.Null(unobserved);
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= onUnobserved;
            BoundedOperation.OwnedTerminalGrace = previous;
            hanging.Release.TrySetResult();
        }
    }

    [Fact]
    public async Task MainViewModel_Shutdown_IsIdempotent_AndDoesNotUseGraphAfterCancel()
    {
        string dbPath = Path.Combine(_root, "shutdown.db");
        using (var init = new QuickNotesDbContext(dbPath))
        {
            init.Database.EnsureCreated();
        }

        var hanging = new HangDrainScheduler();
        var settingsService = new SettingsService(Path.Combine(_root, "shutdown.json"), _ => { });
        var hotkey = new GlobalHotkeyService((_, _, _, _) => true, (_, _) => true, IntPtr.Zero);
        var tray = new TrayIconService();
        var vm = MainViewModelTestComposition.Create(
            () => new QuickNotesDbContext(dbPath),
            new TagDetectionService(),
            new SearchService(),
            settingsService,
            hotkey,
            new ClipboardCaptureService(),
            tray,
            backupService: new BackupService(dbPath, Path.Combine(_root, "bak_sd")),
            syncScheduler: hanging);

        using var cts = new CancellationTokenSource();
        Task first = vm.ShutdownAsync(TimeSpan.FromMilliseconds(40), cts.Token);
        await hanging.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task second = vm.ShutdownAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
        Assert.Same(first, second);
        cts.Cancel();
        await first.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, hanging.Calls);
        vm.Dispose();
        hotkey.Dispose();
        tray.Dispose();
    }

    [Fact]
    public async Task TryGetBrowserUrlAsync_DoesNotBlockCallerWhenProbeHangs()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new ManualResetEventSlim(false);
        Win32Helper.BrowserUrlCoreOverrideForTests = _ =>
        {
            started.TrySetResult();
            release.Wait();
            return "https://example.invalid/";
        };

        try
        {
            var urlTask = Win32Helper.TryGetBrowserUrlAsync(new IntPtr(1));
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            string? url = await urlTask.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Null(url);
            Assert.False(release.IsSet);
        }
        finally
        {
            Win32Helper.BrowserUrlCoreOverrideForTests = null;
            Win32Helper.ResetBrowserUrlProbeStateForTests();
            release.Set();
        }
    }

    [Fact]
    public async Task TryGetBrowserUrlAsync_SingleFlight_CoalescesAndIgnoresLateResult()
    {
        int probes = 0;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new ManualResetEventSlim(false);
        Win32Helper.ResetBrowserUrlProbeStateForTests();
        Win32Helper.BrowserUrlCoreOverrideForTests = _ =>
        {
            Interlocked.Increment(ref probes);
            started.TrySetResult();
            release.Wait();
            return "https://late.example.invalid/";
        };

        try
        {
            var first = Win32Helper.TryGetBrowserUrlAsync(new IntPtr(1));
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var second = Win32Helper.TryGetBrowserUrlAsync(new IntPtr(2));
            string? firstUrl = await first.WaitAsync(TimeSpan.FromSeconds(5));
            string? secondUrl = await second.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Null(firstUrl);
            Assert.Null(secondUrl);
            Assert.Equal(1, probes);
            release.Set();
            await Task.Yield();
            Assert.Equal(1, probes);
        }
        finally
        {
            Win32Helper.BrowserUrlCoreOverrideForTests = null;
            if (!release.IsSet)
            {
                release.Set();
            }

            Win32Helper.ResetBrowserUrlProbeStateForTests();
        }
    }

    [Fact]
    public async Task BoundedOperation_NonCooperative_TimeoutUsesGrace_DoesNotSucceed_AndRejectsOverlap()
    {
        TimeSpan previous = BoundedOperation.OwnedTerminalGrace;
        BoundedOperation.OwnedTerminalGrace = TimeSpan.FromMilliseconds(50);
        var ownership = new BoundedOwnership();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        int started = 0;
        var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ownership.DrainCompleted += () => drained.TrySetResult();

        try
        {
            var run = BoundedOperation.RunAsync(
                async _ =>
                {
                    Interlocked.Increment(ref started);
                    entered.TrySetResult();
                    return await release.Task;
                },
                TimeSpan.FromMilliseconds(30),
                CancellationToken.None,
                "test.owned-noncoop",
                BoundedTimeoutBehavior.WaitForOwnedWork,
                ownership);

            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var result = await run.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(BoundedOperationOutcome.Timeout, result.Outcome);
            Assert.True(ownership.IsCancelling);

            var overlap = await BoundedOperation.RunAsync(
                _ =>
                {
                    Interlocked.Increment(ref started);
                    return Task.FromResult(99);
                },
                TimeSpan.FromSeconds(2),
                CancellationToken.None,
                "test.owned-noncoop-overlap",
                BoundedTimeoutBehavior.WaitForOwnedWork,
                ownership);
            Assert.Equal(BoundedOperationOutcome.Failed, overlap.Outcome);
            Assert.Equal(UserFacingOperationError.Overlap, overlap.UserMessage);
            Assert.Equal(1, started);

            release.TrySetResult(7);
            await drained.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(ownership.IsHeld);
            Assert.True(ownership.TryAcquire());
            ownership.Release();
        }
        finally
        {
            BoundedOperation.OwnedTerminalGrace = previous;
            release.TrySetResult(0);
        }
    }

    [Fact]
    public async Task BoundedOwnership_DrainCompleted_MarshalsToCapturedContext_AndNoopsAfterSuppress()
    {
        TimeSpan previous = BoundedOperation.OwnedTerminalGrace;
        BoundedOperation.OwnedTerminalGrace = TimeSpan.FromMilliseconds(40);
        var ctx = new InstallingSynchronizationContext();
        SynchronizationContext? previousContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(ctx);
        BoundedOwnership ownership;
        try
        {
            ownership = new BoundedOwnership();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }

        int callbackHits = 0;
        bool sawCapturedContext = false;
        var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ownership.DrainCompleted += () =>
        {
            Interlocked.Increment(ref callbackHits);
            sawCapturedContext = ReferenceEquals(SynchronizationContext.Current, ctx);
            drained.TrySetResult();
        };

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var run = BoundedOperation.RunAsync(
                async _ =>
                {
                    entered.TrySetResult();
                    return await release.Task;
                },
                TimeSpan.FromMilliseconds(30),
                CancellationToken.None,
                "test.drain-marshal",
                BoundedTimeoutBehavior.WaitForOwnedWork,
                ownership);

            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var result = await run.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(BoundedOperationOutcome.Timeout, result.Outcome);
            Assert.True(ownership.IsCancelling);

            release.TrySetResult(1);
            await drained.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(sawCapturedContext);
            Assert.True(ctx.Posts >= 1);
            Assert.Equal(1, Volatile.Read(ref callbackHits));

            ownership.SuppressCallbacks();
            Assert.False(ownership.IsHeld);

            var suppressed = new BoundedOwnership(ctx);
            int suppressedHits = 0;
            suppressed.DrainCompleted += () => Interlocked.Increment(ref suppressedHits);
            var entered2 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release2 = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            var run2 = BoundedOperation.RunAsync(
                async _ =>
                {
                    entered2.TrySetResult();
                    return await release2.Task;
                },
                TimeSpan.FromMilliseconds(30),
                CancellationToken.None,
                "test.drain-suppress",
                BoundedTimeoutBehavior.WaitForOwnedWork,
                suppressed);
            await entered2.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await run2.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(suppressed.IsCancelling);
            suppressed.SuppressCallbacks();
            release2.TrySetResult(2);
            await Task.Yield();
            await Task.Yield();
            Assert.Equal(0, Volatile.Read(ref suppressedHits));
        }
        finally
        {
            BoundedOperation.OwnedTerminalGrace = previous;
            release.TrySetResult(0);
        }
    }

    [Fact]
    public void StandardImport_CancelImmediatelyBeforeCommit_DoesNotBecomeSuccess()
    {
        string dbPath = Path.Combine(_root, "import_precommit.db");
        using var db = SqliteTestUtil.CreateContext(dbPath);
        DbInitializer.Initialize(db);
        string notePath = Path.Combine(_root, "precommit.md");
        File.WriteAllText(notePath, "pre-commit cancel");
        var svc = new NoteImportService();
        var preview = svc.BuildPreviewFromFiles(db, new[] { notePath });
        preview.LossesAcknowledged = true;
        using var cts = new CancellationTokenSource();
        NoteImportService.TestInjectFailure = phase =>
        {
            if (phase == ImportTransactionPhase.Publish)
            {
                cts.Cancel();
            }
        };

        try
        {
            var exec = svc.ExecuteImport(db, preview, new LocalMutationCoordinator(), cancellationToken: cts.Token);
            Assert.False(exec.Success);
            Assert.Contains("отмен", exec.ErrorMessage, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0, db.Notes.Count());
        }
        finally
        {
            NoteImportService.TestInjectFailure = null;
        }
    }

    [Fact]
    public void PortableImport_CancelImmediatelyBeforeCommit_DoesNotBecomeSuccess()
    {
        string srcDb = Path.Combine(_root, "arch_src.db");
        using (var db = SqliteTestUtil.CreateContext(srcDb))
        {
            DbInitializer.Initialize(db);
            db.Notes.Add(new Note { Text = "archive note", SyncId = Guid.NewGuid() });
            db.SaveChanges();
        }

        var storage = new AttachmentStorageService(Path.Combine(_root, "arch_att"));
        string zipPath = Path.Combine(_root, "precommit.qnarchive.zip");
        var archive = new NoteArchiveService();
        using (var db = SqliteTestUtil.CreateContext(srcDb))
        {
            Assert.True(archive.ExportArchive(db, zipPath, storage).Success);
        }

        string destDb = Path.Combine(_root, "arch_dest.db");
        using var dest = SqliteTestUtil.CreateContext(destDb);
        DbInitializer.Initialize(dest);
        var preview = archive.PreviewArchive(dest, zipPath);
        using var cts = new CancellationTokenSource();
        NoteArchiveService.TestBeforeCommit = () => cts.Cancel();
        try
        {
            var exec = archive.ImportArchive(dest, preview, storage, new LocalMutationCoordinator(), cancellationToken: cts.Token);
            Assert.False(exec.Success);
            Assert.Contains("отмен", exec.ErrorMessage, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0, dest.Notes.Count());
        }
        finally
        {
            NoteArchiveService.TestBeforeCommit = null;
        }
    }

    [Fact]
    public void PortableZipExport_CancelAfterTempZip_DeletesPlaintextTemp()
    {
        string dbPath = Path.Combine(_root, "zip_src.db");
        using (var db = SqliteTestUtil.CreateContext(dbPath))
        {
            DbInitializer.Initialize(db);
            db.Notes.Add(new Note { Text = "zip me", SyncId = Guid.NewGuid() });
            db.SaveChanges();
        }

        string target = Path.Combine(_root, "out.qnarchive.zip");
        string? tempZip = null;
        using var cts = new CancellationTokenSource();
        var archive = new NoteArchiveService
        {
            AfterTempZipCreatedForTests = path =>
            {
                tempZip = path;
                Assert.True(File.Exists(path));
                cts.Cancel();
            }
        };

        using (var db = SqliteTestUtil.CreateContext(dbPath))
        {
            Assert.Throws<OperationCanceledException>(() =>
                archive.ExportArchive(db, target, new AttachmentStorageService(Path.Combine(_root, "zip_att")), cts.Token));
        }

        Assert.False(File.Exists(target));
        Assert.False(string.IsNullOrWhiteSpace(tempZip));
        Assert.False(File.Exists(tempZip));
    }

    [Fact]
    public void DirectoryExport_CancelBeforeCommitPoint_DoesNotPublish()
    {
        string dbPath = Path.Combine(_root, "dir_export.db");
        using (var db = SqliteTestUtil.CreateContext(dbPath))
        {
            DbInitializer.Initialize(db);
            db.Notes.Add(new Note { Text = "PREVIOUS_DIR" });
            db.SaveChanges();
        }

        string exportDir = Path.Combine(_root, "md_snapshot");
        var exporter = new NoteExportService();
        using (var db = SqliteTestUtil.CreateContext(dbPath))
        {
            Assert.True(exporter.ExportToMarkdown(db, exportDir).Success);
        }

        using (var db = SqliteTestUtil.CreateContext(dbPath))
        {
            db.Notes.Add(new Note { Text = "MUST_NOT_PUBLISH" });
            db.SaveChanges();
        }

        using var cts = new CancellationTokenSource();
        exporter.BeforePublishForTests = _ => cts.Cancel();
        using (var db = SqliteTestUtil.CreateContext(dbPath))
        {
            Assert.Throws<OperationCanceledException>(() => exporter.ExportToMarkdown(db, exportDir, cancellationToken: cts.Token));
        }

        string snapshot = string.Join('\n', Directory.GetFiles(exportDir, "*", SearchOption.AllDirectories).Select(File.ReadAllText));
        Assert.Contains("PREVIOUS_DIR", snapshot);
        Assert.DoesNotContain("MUST_NOT_PUBLISH", snapshot);
        Assert.Empty(Directory.GetDirectories(_root, ".tmp_export_*"));
        Assert.Empty(Directory.GetDirectories(_root, ".bak_export_*"));
    }

    [Fact]
    public void DirectoryExport_CancelAfterCommitPoint_CompletesAtomicPublish()
    {
        string dbPath = Path.Combine(_root, "dir_export_after.db");
        using (var db = SqliteTestUtil.CreateContext(dbPath))
        {
            DbInitializer.Initialize(db);
            db.Notes.Add(new Note { Text = "OLD_DIR" });
            db.SaveChanges();
        }

        string exportDir = Path.Combine(_root, "md_snapshot_after");
        var exporter = new NoteExportService();
        using (var db = SqliteTestUtil.CreateContext(dbPath))
        {
            Assert.True(exporter.ExportToMarkdown(db, exportDir).Success);
        }

        using (var db = SqliteTestUtil.CreateContext(dbPath))
        {
            db.Notes.Add(new Note { Text = "NEW_DIR" });
            db.SaveChanges();
        }

        using var cts = new CancellationTokenSource();
        exporter.AfterTargetMovedAsideForTests = _ => cts.Cancel();
        using (var db = SqliteTestUtil.CreateContext(dbPath))
        {
            ExportResult published = exporter.ExportToMarkdown(db, exportDir, cancellationToken: cts.Token);
            Assert.True(published.Success, published.ErrorMessage);
        }

        string snapshot = string.Join('\n', Directory.GetFiles(exportDir, "*", SearchOption.AllDirectories).Select(File.ReadAllText));
        Assert.Contains("NEW_DIR", snapshot);
        Assert.Contains("OLD_DIR", snapshot);
        Assert.Empty(Directory.GetDirectories(_root, ".bak_export_*"));
    }

    [Fact]
    public void ProductionSources_DoNotUseAsyncVoid_ExceptAsyncEventBridge()
    {
        string appRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "QuickNotes.App"));
        Assert.True(Directory.Exists(appRoot), appRoot);
        var offenders = new System.Collections.Generic.List<string>();
        foreach (string file in Directory.GetFiles(appRoot, "*.cs", SearchOption.AllDirectories))
        {
            if (Path.GetFileName(file).Equals("AsyncEventBridge.cs", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (string line in File.ReadAllLines(file))
            {
                if (line.Contains("async void", StringComparison.Ordinal))
                {
                    offenders.Add(file + ": " + line.Trim());
                }
            }
        }

        Assert.True(offenders.Count == 0, string.Join(Environment.NewLine, offenders));
    }

    [Fact]
    public void ProductionSources_InteractiveGetResult_OnlyIsolatedCliHosts()
    {
        string appRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "QuickNotes.App"));
        Assert.True(Directory.Exists(appRoot), appRoot);
        var offenders = new System.Collections.Generic.List<string>();
        foreach (string file in Directory.GetFiles(appRoot, "*.cs", SearchOption.AllDirectories))
        {
            string name = Path.GetFileName(file);
            bool isolatedHost =
                name.Contains("Smoke", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Cli", StringComparison.OrdinalIgnoreCase);
            if (isolatedHost)
            {
                continue;
            }

            string[] lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                if (line.Contains("GetAwaiter().GetResult()", StringComparison.Ordinal))
                {
                    offenders.Add($"{file}({i + 1}): blocking GetResult on interactive/production path");
                }
            }
        }

        Assert.True(offenders.Count == 0, string.Join(Environment.NewLine, offenders));
    }

    private static ExportResult ExportResultSuccess() => new() { Success = true, ExportedNotesCount = 1 };

    private sealed class HangingExportService : INoteExportService
    {
        public TaskCompletionSource<ExportResult> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls;
        public bool Published;

        public ExportResult ExportToJson(QuickNotesDbContext db, string targetFilePath, CancellationToken cancellationToken = default)
            => Hang(cancellationToken);

        public ExportResult ExportToCsv(QuickNotesDbContext db, string targetFilePath, CancellationToken cancellationToken = default)
            => Hang(cancellationToken);

        public ExportResult ExportToMarkdown(
            QuickNotesDbContext db,
            string targetDirectoryPath,
            IAttachmentStorageService? attachments = null,
            CancellationToken cancellationToken = default)
            => Hang(cancellationToken);

        private ExportResult Hang(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            Entered.TrySetResult();
            ExportResult result = Release.Task.WaitAsync(cancellationToken).GetAwaiter().GetResult();
            Published = true;
            return result;
        }

        public string BuildJson(System.Collections.Generic.IEnumerable<Note> notes, System.Collections.Generic.IEnumerable<Tag> tags) => "{}";
        public string BuildCsv(System.Collections.Generic.IEnumerable<Note> notes) => "";
        public string GenerateMarkdownContent(Note note) => note.Text ?? "";
        public string BuildSafeFileName(int noteId, string? text, int collisionIndex = 0) => noteId.ToString();
    }

    private sealed class HangingImportService : INoteImportService
    {
        public TaskCompletionSource<ImportExecutionResult> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls;
        public bool Published;

        public ImportPreviewResult BuildPreviewFromFiles(QuickNotesDbContext db, IEnumerable<string> filePaths, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ImportPreviewResult BuildPreviewFromDirectory(QuickNotesDbContext db, string directoryPath, bool recursive = true, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ImportExecutionResult ExecuteImport(
            QuickNotesDbContext db,
            ImportPreviewResult preview,
            ILocalMutationCoordinator mutationCoordinator,
            INoteHistoryService? historyService = null,
            TagDetectionService? tagDetectionService = null,
            IAttachmentStorageService? attachmentStorage = null,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Calls);
            Entered.TrySetResult();
            ImportExecutionResult result = Release.Task.WaitAsync(cancellationToken).GetAwaiter().GetResult();
            Published = true;
            return result;
        }

        public bool TryParseMarkdownFile(string filePath, out ImportItemPreview? item, out string? errorMessage)
        {
            item = null;
            errorMessage = "not supported";
            return false;
        }

        public bool TryParseMarkdownContent(string content, string fileName, DateTime creationTime, DateTime lastWriteTime, out ImportItemPreview? item, out string? errorMessage)
        {
            item = null;
            errorMessage = "not supported";
            return false;
        }

        public bool TryParsePlainTextFile(string filePath, out ImportItemPreview? item, out string? errorMessage)
        {
            item = null;
            errorMessage = "not supported";
            return false;
        }

        public bool TryParsePlainTextContent(string content, string fileName, DateTime creationTime, DateTime lastWriteTime, out ImportItemPreview? item, out string? errorMessage)
        {
            item = null;
            errorMessage = "not supported";
            return false;
        }

        public bool TryParseHtmlFile(string filePath, string importRoot, out ImportItemPreview? item, out string? errorMessage)
        {
            item = null;
            errorMessage = "not supported";
            return false;
        }

        public bool TryParseHtmlContent(string html, string fileName, DateTime creationTime, DateTime lastWriteTime, out ImportItemPreview? item, out HtmlConversionResult conversion, out string? errorMessage)
        {
            item = null;
            conversion = new HtmlConversionResult();
            errorMessage = "not supported";
            return false;
        }

        public bool TryParseJsonFile(string filePath, out List<ImportItemPreview>? items, out List<ExportTagDto>? packageTags, out string? errorMessage)
        {
            items = null;
            packageTags = null;
            errorMessage = "not supported";
            return false;
        }

        public bool TryParseJsonContent(string jsonContent, string fileName, out List<ImportItemPreview>? items, out List<ExportTagDto>? packageTags, out string? errorMessage)
        {
            items = null;
            packageTags = null;
            errorMessage = "not supported";
            return false;
        }

        public string NormalizeContent(string? text) => text ?? "";
        public void RecalculatePreviewTotals(QuickNotesDbContext db, ImportPreviewResult preview) { }
    }

    private sealed class HangingArchiveService : IEncryptedArchiveService
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls;
        public bool Published;

        public EncryptedArchiveCreateResult Create(EncryptedArchiveCreateRequest request, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Calls);
            Entered.TrySetResult();
            Release.Task.WaitAsync(cancellationToken).GetAwaiter().GetResult();
            Published = true;
            Directory.CreateDirectory(Path.GetDirectoryName(request.DestinationArchivePath)!);
            File.WriteAllBytes(request.DestinationArchivePath, new byte[] { 1 });
            return new EncryptedArchiveCreateResult
            {
                ArchivePath = Path.GetFullPath(request.DestinationArchivePath),
                ArchiveId = Guid.NewGuid(),
                RecoveryKeyFormatted = "AAAA-AAAA-AAAA-AAAA-AAAA-AAAA-AAAA-AAAA-AAAA-AAAA-AAAA-AAAA-AAAA-AAAA-AAAA-AAAA-AAAA-AA",
                Pbkdf2Iterations = EncryptedArchiveConstants.DefaultPbkdf2IterationsForNewArchives,
                SnapshotUserVersion = 1,
                NoteCount = 0,
                ProtectedNoteCount = 0,
                AttachmentFileCount = 0
            };
        }

        public EncryptedArchiveDryRunResult DryRun(EncryptedArchiveDryRunRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public EncryptedArchiveRestoreResult Restore(EncryptedArchiveRestoreRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public EncryptedArchiveRotateRecoveryResult RotateRecovery(EncryptedArchiveRotateRecoveryRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class NonCooperativeBackupService : BackupService
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls;
        public bool Published;

        public NonCooperativeBackupService(string dbPath, string backupDirectory)
            : base(dbPath, backupDirectory)
        {
        }

        public override string CreateBackup(int maxCopies = DefaultMaxCopies, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Calls);
            Entered.TrySetResult();
            try
            {
                Release.Task.GetAwaiter().GetResult();
                Published = true;
                return Path.Combine(BackupDirectory, "late.db");
            }
            finally
            {
                Completed.TrySetResult();
            }
        }
    }

    private sealed class HangingBackupService : BackupService
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls;
        public bool Published;

        public HangingBackupService(string dbPath, string backupDirectory)
            : base(dbPath, backupDirectory)
        {
        }

        public override string CreateBackup(int maxCopies = DefaultMaxCopies, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Calls);
            Entered.TrySetResult();
            Release.Task.WaitAsync(cancellationToken).GetAwaiter().GetResult();
            Published = true;
            return Path.Combine(BackupDirectory, "late.db");
        }
    }

    private sealed class NonCooperativeOcrService : IOcrService
    {
        public TaskCompletionSource<OcrResult> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool IsSupported => true;

        public Task<OcrResult> RecognizeTextAsync(byte[] imageBytes, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            return Release.Task;
        }
    }

    private sealed class RecordingOcrVisibility : IOcrWindowVisibilityCoordinator
    {
        public int RestoreCount;

        public IOcrWindowScope HideQuickNotesWindows() => new Scope(this);

        private sealed class Scope : IOcrWindowScope
        {
            private readonly RecordingOcrVisibility _owner;
            public Scope(RecordingOcrVisibility owner) => _owner = owner;
            public bool WasMainWindowVisible => true;
            public IReadOnlyList<System.Windows.Window> HiddenWindows => Array.Empty<System.Windows.Window>();
            public Task WaitForDesktopCompositionAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
            public void RestoreWindows() => Interlocked.Increment(ref _owner.RestoreCount);
            public void Dispose() => RestoreWindows();
        }
    }

    private sealed class ImmediateCaptureService : IScreenCaptureService
    {
        public Task<ScreenCaptureResult> CaptureAreaAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(ScreenCaptureResult.Succeeded(new byte[] { 1, 2, 3 }, 1, 1));
    }

    private sealed class HangDrainScheduler : FakeTrackingSyncScheduler
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls;

        public override async Task<bool> DrainAndStopAsync(TimeSpan timeout, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Calls);
            Entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return true;
        }
    }

    [Fact]
    public async Task MainViewModel_SyncStatusAction_WhenBusyAndHasConflicts_DoesNotOpenConflicts()
    {
        string dbPath = Path.Combine(_root, "sync_busy_conflicts.db");
        using (var init = new QuickNotesDbContext(dbPath))
        {
            init.Database.EnsureCreated();
        }

        string settingsPath = Path.Combine(_root, "settings_busy_conflicts.json");
        var settingsService = new SettingsService(settingsPath, _ => { });
        var settings = settingsService.CurrentSettings;
        settings.CloudSync.Enabled = true;
        settings.CloudSync.Bucket = "bucket";
        settingsService.SaveSettings(settings);

        var hanging = new HangingSyncScheduler();
        hanging.UnresolvedConflictsCount = 1;
        var creds = new EphemeralS3CredentialsStorage();
        await creds.SaveCredentialsAsync(new S3Credentials("K", "S"));
        var pwd = new EphemeralSyncPasswordStorage();
        await pwd.SavePasswordAsync("pw");

        using var hotkey = new GlobalHotkeyService((_, _, _, _) => true, (_, _) => true, IntPtr.Zero);
        using var tray = new TrayIconService();
        var vm = MainViewModelTestComposition.Create(
            () => new QuickNotesDbContext(dbPath),
            new TagDetectionService(),
            new SearchService(),
            settingsService,
            hotkey,
            new ClipboardCaptureService(),
            tray,
            backupService: new BackupService(dbPath, Path.Combine(_root, "bak_busy_conflicts")),
            credentialsStorage: creds,
            passwordStorage: pwd,
            syncScheduler: hanging);
        vm.ManualSyncTimeout = TimeSpan.FromMinutes(5);

        try
        {
            bool conflictsRequested = false;
            vm.RequestOpenSyncConflicts += _ => { conflictsRequested = true; return true; };

            Task sync = vm.SyncNowAsync();
            await hanging.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.True(vm.IsSyncBusy);
            Assert.False(vm.SyncStatusActionCommand.CanExecute(null));
            Assert.Equal("Синхронизация выполняется", vm.SyncStatusActionAutomationName);

            vm.SyncStatusActionCommand.Execute(null);

            Assert.False(conflictsRequested);
            Assert.Equal(1, hanging.ManualSyncCallCount);
        }
        finally
        {
            hanging.Release.TrySetResult(SyncCycleResult.Succeeded());
            vm.Dispose();
        }
    }

    private sealed class HangingSyncScheduler : FakeTrackingSyncScheduler
    {
        public TaskCompletionSource<SyncCycleResult?> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async Task<SyncCycleResult?> TriggerManualSyncAsync(CancellationToken ct = default)
        {
            ManualSyncCallCount++;
            Entered.TrySetResult();
            return await Release.Task.WaitAsync(ct);
        }
    }

    private sealed class NonCooperativeSyncScheduler : FakeTrackingSyncScheduler
    {
        public TaskCompletionSource<SyncCycleResult?> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async Task<SyncCycleResult?> TriggerManualSyncAsync(CancellationToken ct = default)
        {
            ManualSyncCallCount++;
            Entered.TrySetResult();
            SyncCycleResult? result = await Release.Task;
            if (result != null)
            {
                LastResult = result;
                if (result.Success)
                {
                    LastSuccessTimeUtc = DateTime.UtcNow;
                }

                RaiseSyncCompleted(result);
                RaiseStatusChanged(new SyncSchedulerStatusChangedEventArgs(
                    SyncSchedulerQueueState.Idle,
                    "Готов",
                    Array.Empty<SyncTriggerReason>(),
                    LastSuccessTimeUtc,
                    DateTime.UtcNow,
                    UnresolvedConflictsCount,
                    result));
            }

            return result;
        }
    }

    private sealed class NonCooperativeArchiveService : IEncryptedArchiveService
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls;

        public EncryptedArchiveCreateResult Create(EncryptedArchiveCreateRequest request, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Calls);
            Entered.TrySetResult();
            Release.Task.GetAwaiter().GetResult();
            Directory.CreateDirectory(Path.GetDirectoryName(request.DestinationArchivePath)!);
            File.WriteAllBytes(request.DestinationArchivePath, new byte[] { 1 });
            return new EncryptedArchiveCreateResult
            {
                ArchivePath = Path.GetFullPath(request.DestinationArchivePath),
                ArchiveId = Guid.NewGuid(),
                RecoveryKeyFormatted = "AAAA-AAAA-AAAA-AAAA-AAAA-AAAA-AAAA-AAAA-AAAA-AAAA-AAAA-AAAA-AAAA-AAAA-AAAA-AAAA-AAAA-AA",
                Pbkdf2Iterations = EncryptedArchiveConstants.DefaultPbkdf2IterationsForNewArchives,
                SnapshotUserVersion = 1,
                NoteCount = 0,
                ProtectedNoteCount = 0,
                AttachmentFileCount = 0
            };
        }

        public EncryptedArchiveDryRunResult DryRun(EncryptedArchiveDryRunRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public EncryptedArchiveRestoreResult Restore(EncryptedArchiveRestoreRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public EncryptedArchiveRotateRecoveryResult RotateRecovery(EncryptedArchiveRotateRecoveryRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class InstallingSynchronizationContext : SynchronizationContext
    {
        public int Posts;

        public override void Post(SendOrPostCallback d, object? state)
        {
            Interlocked.Increment(ref Posts);
            SynchronizationContext? previous = Current;
            SetSynchronizationContext(this);
            try
            {
                d(state);
            }
            finally
            {
                SetSynchronizationContext(previous);
            }
        }

        public override void Send(SendOrPostCallback d, object? state) => Post(d, state);
    }
}
