using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using QuickNotes.App.Data;
using QuickNotes.App.Models;
using QuickNotes.App.Services;
using QuickNotes.App.Services.EncryptedArchive;
using QuickNotes.App.Services.NoteProtection;
using QuickNotes.App.Services.Sync;
using QuickNotes.App.ViewModels;
using Xunit;

namespace QuickNotes.Tests;

[TestCategory(TestCategories.Integration)]
public sealed class EncryptedArchiveSetupViewModelTests : IDisposable
{
    private const int TestKdfN = 10;
    private readonly string _root;

    public EncryptedArchiveSetupViewModelTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "qn_qnar_ui_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose() => SqliteTestUtil.TryDeleteDirectory(_root);

    [Fact]
    public async Task Create_OmitsPbkdf2Iterations_WhenNoTestOverride()
    {
        using var harness = Harness.Create(_root);
        var spy = new RecordingArchiveService();
        var vm = harness.CreateVm(spy);
        vm.EncryptedArchiveDestinationPath = Path.Combine(harness.OutDir, "prod-default.qnar");
        vm.SetEncryptedArchivePasswordInput("archive-pw");
        vm.SetEncryptedArchivePasswordConfirmInput("archive-pw");

        await vm.ExecuteCreateEncryptedArchiveAsync();

        Assert.NotNull(spy.LastCreate);
        Assert.Null(spy.LastCreate!.Pbkdf2Iterations);
        Assert.True(vm.HasPendingRecoveryVerification);
        Assert.False(vm.HasLastSuccessfulEncryptedArchive);
    }

    [Fact]
    public async Task WrongRecoveryKey_DoesNotComplete_AndDoesNotChangeLastSuccess()
    {
        using var harness = Harness.Create(_root);
        var vm = harness.CreateVm(harness.Service);
        string archivePath = Path.Combine(harness.OutDir, "wrong-key.qnar");
        vm.EncryptedArchiveDestinationPath = archivePath;
        vm.SetEncryptedArchivePasswordInput(harness.ArchivePassword);
        vm.SetEncryptedArchivePasswordConfirmInput(harness.ArchivePassword);
        await vm.ExecuteCreateEncryptedArchiveAsync();
        string shown = vm.ShownRecoveryKey;
        Assert.False(string.IsNullOrEmpty(shown));

        string otherKey = RecoveryKeyEncoding.Format(RandomNumberGenerator.GetBytes(32));
        vm.SetRecoveryKeyConfirmationInput(otherKey);
        await vm.ExecuteVerifyEncryptedArchiveRecoveryAsync();

        Assert.True(vm.HasPendingRecoveryVerification);
        Assert.False(vm.HasLastSuccessfulEncryptedArchive);
        Assert.Null(harness.Settings.CurrentSettings.LastEncryptedArchiveUtc);
        Assert.Equal(shown, vm.ShownRecoveryKey);
        Assert.Equal(EncryptedArchiveSecurityException.GenericUserMessage, vm.EncryptedArchiveStatusMessage);
        Assert.True(File.Exists(archivePath));
        AssertNoTemp(harness.OutDir);
    }

    [Fact]
    public async Task CancelAfterCreate_ClearsSecrets_DoesNotChangeLastSuccess_LeavesNoTemp()
    {
        using var harness = Harness.Create(_root);
        var vm = harness.CreateVm(harness.Service);
        string previousPath = Path.Combine(harness.OutDir, "previous-success.qnar");
        harness.Settings.CurrentSettings.LastEncryptedArchivePath = previousPath;
        harness.Settings.CurrentSettings.LastEncryptedArchiveUtc = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        harness.Settings.SaveSettings(harness.Settings.CurrentSettings);

        var reloaded = harness.CreateVm(harness.Service);
        Assert.True(reloaded.HasLastSuccessfulEncryptedArchive);
        string priorBanner = reloaded.LastSuccessfulEncryptedArchiveBanner;

        string newPath = Path.Combine(harness.OutDir, "cancelled.qnar");
        reloaded.EncryptedArchiveDestinationPath = newPath;
        reloaded.SetEncryptedArchivePasswordInput(harness.ArchivePassword);
        reloaded.SetEncryptedArchivePasswordConfirmInput(harness.ArchivePassword);
        await reloaded.ExecuteCreateEncryptedArchiveAsync();
        Assert.True(File.Exists(newPath));
        Assert.NotEqual(newPath, reloaded.LastEncryptedArchivePath);

        reloaded.CancelEncryptedArchiveSetup();
        Assert.False(reloaded.HasPendingRecoveryVerification);
        Assert.True(string.IsNullOrEmpty(reloaded.ShownRecoveryKey));
        Assert.Equal(previousPath, reloaded.LastEncryptedArchivePath);
        Assert.Equal(priorBanner, reloaded.LastSuccessfulEncryptedArchiveBanner);
        Assert.Equal(previousPath, harness.Settings.CurrentSettings.LastEncryptedArchivePath);
        Assert.True(File.Exists(newPath));
        AssertNoTemp(harness.OutDir);
    }

    [Fact]
    public async Task LastSuccess_IsActualVerifiedPathAndTime_NotCurrentSelection()
    {
        using var harness = Harness.Create(_root);
        var vm = harness.CreateVm(harness.Service);
        string archivePath = Path.Combine(harness.OutDir, "verified.qnar");
        vm.EncryptedArchiveDestinationPath = archivePath;
        vm.SetEncryptedArchivePasswordInput(harness.ArchivePassword);
        vm.SetEncryptedArchivePasswordConfirmInput(harness.ArchivePassword);
        await vm.ExecuteCreateEncryptedArchiveAsync();
        string key = vm.ShownRecoveryKey;
        vm.EncryptedArchiveDestinationPath = Path.Combine(harness.OutDir, "not-the-success.qnar");
        vm.SetRecoveryKeyConfirmationInput(key);
        DateTime before = DateTime.UtcNow.AddMinutes(-1);
        await vm.ExecuteVerifyEncryptedArchiveRecoveryAsync();
        DateTime after = DateTime.UtcNow.AddMinutes(1);

        Assert.True(vm.HasLastSuccessfulEncryptedArchive);
        Assert.Equal(Path.GetFullPath(archivePath), Path.GetFullPath(vm.LastEncryptedArchivePath));
        Assert.NotEqual(vm.EncryptedArchiveDestinationPath, vm.LastEncryptedArchivePath);
        Assert.InRange(vm.LastEncryptedArchiveUtc!.Value, before, after);
        Assert.Contains(vm.LastEncryptedArchivePath, vm.LastSuccessfulEncryptedArchiveBanner, StringComparison.Ordinal);
        Assert.DoesNotContain("not-the-success.qnar", vm.LastSuccessfulEncryptedArchiveBanner, StringComparison.Ordinal);
        Assert.True(string.IsNullOrEmpty(vm.ShownRecoveryKey));
    }

    [Fact]
    public async Task ProtectedNoteRoundtrip_UsesVerifiedArchive_AndNotePasswordStillRequired()
    {
        using var harness = Harness.Create(_root);
        const string notePassword = "note-password-roundtrip";
        const string secret = "PROTECTED_QNAR_UI_ROUNDTRIP_TOKEN";
        harness.AddProtectedNote(secret, notePassword);

        var vm = harness.CreateVm(harness.Service);
        string archivePath = Path.Combine(harness.OutDir, "protected.qnar");
        vm.EncryptedArchiveDestinationPath = archivePath;
        vm.SetEncryptedArchivePasswordInput(harness.ArchivePassword);
        vm.SetEncryptedArchivePasswordConfirmInput(harness.ArchivePassword);
        await vm.ExecuteCreateEncryptedArchiveAsync();
        string recoveryKey = vm.ShownRecoveryKey;
        vm.SetRecoveryKeyConfirmationInput(recoveryKey);
        await vm.ExecuteVerifyEncryptedArchiveRecoveryAsync();
        Assert.True(vm.HasLastSuccessfulEncryptedArchive);

        string restoreDir = Path.Combine(harness.Root, "restore");
        EncryptedArchiveRestoreResult restored = harness.Service.Restore(new EncryptedArchiveRestoreRequest
        {
            ArchivePath = vm.LastEncryptedArchivePath,
            RecoveryKeyFormatted = recoveryKey,
            DestinationDirectory = restoreDir
        });

        using var restoredDb = SqliteTestUtil.CreateContext(restored.DatabasePath);
        var protectedNote = restoredDb.Notes.Single(n => n.IsProtected);
        var crypto = new NoteCryptoService(iterations: 1000);
        byte[] plain = crypto.Decrypt(
            notePassword,
            Convert.FromBase64String(protectedNote.ProtectedCiphertextBase64!),
            Convert.FromBase64String(protectedNote.ProtectedSaltBase64!),
            Convert.FromBase64String(protectedNote.ProtectedNonceBase64!),
            Convert.FromBase64String(protectedNote.ProtectedTagBase64!),
            protectedNote.SyncId,
            NoteProtectedObjectType.NoteEnvelope,
            protectedNote.ProtectedKdfIterations);
        Assert.Contains(secret, Encoding.UTF8.GetString(plain), StringComparison.Ordinal);
        Assert.Throws<NoteProtectionSecurityException>(() => crypto.Decrypt(
            harness.ArchivePassword,
            Convert.FromBase64String(protectedNote.ProtectedCiphertextBase64!),
            Convert.FromBase64String(protectedNote.ProtectedSaltBase64!),
            Convert.FromBase64String(protectedNote.ProtectedNonceBase64!),
            Convert.FromBase64String(protectedNote.ProtectedTagBase64!),
            protectedNote.SyncId,
            NoteProtectedObjectType.NoteEnvelope,
            protectedNote.ProtectedKdfIterations));
        Assert.Throws<NoteProtectionSecurityException>(() => crypto.Decrypt(
            recoveryKey,
            Convert.FromBase64String(protectedNote.ProtectedCiphertextBase64!),
            Convert.FromBase64String(protectedNote.ProtectedSaltBase64!),
            Convert.FromBase64String(protectedNote.ProtectedNonceBase64!),
            Convert.FromBase64String(protectedNote.ProtectedTagBase64!),
            protectedNote.SyncId,
            NoteProtectedObjectType.NoteEnvelope,
            protectedNote.ProtectedKdfIterations));
    }

    [Fact]
    public async Task Secrets_AreNotPersisted_InSettingsLogsOrCommandLine()
    {
        using var harness = Harness.Create(_root);
        var vm = harness.CreateVm(harness.Service);
        string archivePath = Path.Combine(harness.OutDir, "nopersist.qnar");
        const string archivePassword = "archive-pw-MUST-NOT-PERSIST-9f3a";
        vm.EncryptedArchiveDestinationPath = archivePath;
        vm.SetEncryptedArchivePasswordInput(archivePassword);
        vm.SetEncryptedArchivePasswordConfirmInput(archivePassword);
        await vm.ExecuteCreateEncryptedArchiveAsync();
        string recoveryKey = vm.ShownRecoveryKey;
        vm.SetRecoveryKeyConfirmationInput(recoveryKey);
        await vm.ExecuteVerifyEncryptedArchiveRecoveryAsync();

        string settingsJson = File.ReadAllText(harness.SettingsPath);
        Assert.DoesNotContain(archivePassword, settingsJson, StringComparison.Ordinal);
        Assert.DoesNotContain(recoveryKey, settingsJson, StringComparison.Ordinal);
        Assert.DoesNotContain("RecoveryKey", settingsJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ArchivePassword", settingsJson, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("LastEncryptedArchivePath", settingsJson, StringComparison.Ordinal);
        Assert.Contains("nopersist.qnar", settingsJson, StringComparison.Ordinal);

        if (File.Exists(ErrorLogService.LogFilePath))
        {
            string log = File.ReadAllText(ErrorLogService.LogFilePath);
            Assert.DoesNotContain(archivePassword, log, StringComparison.Ordinal);
            Assert.DoesNotContain(recoveryKey, log, StringComparison.Ordinal);
        }

        string vmSource = File.ReadAllText(Path.Combine(FindSolutionRoot(), "QuickNotes.App", "ViewModels", "ImportExportViewModel.cs"));
        Assert.DoesNotContain("Clipboard", vmSource, StringComparison.Ordinal);
        Assert.DoesNotContain("ProcessStartInfo", vmSource, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateError_DoesNotChangeLastSuccess_AndLeavesNoTemp()
    {
        using var harness = Harness.Create(_root);
        var vm = harness.CreateVm(harness.Service);
        string existing = Path.Combine(harness.OutDir, "exists.qnar");
        File.WriteAllBytes(existing, new byte[] { 1, 2, 3, 4 });
        string decoyStaging = Path.Combine(harness.OutDir, ".qnar-staging-unrelated");
        Directory.CreateDirectory(decoyStaging);
        File.WriteAllText(Path.Combine(decoyStaging, "keep.txt"), "keep");
        string decoyTmp = Path.Combine(harness.OutDir, ".unrelated.tmp");
        File.WriteAllBytes(decoyTmp, new byte[] { 9 });
        vm.EncryptedArchiveDestinationPath = existing;
        vm.SetEncryptedArchivePasswordInput(harness.ArchivePassword);
        vm.SetEncryptedArchivePasswordConfirmInput(harness.ArchivePassword);
        await vm.ExecuteCreateEncryptedArchiveAsync();

        Assert.False(vm.HasPendingRecoveryVerification);
        Assert.False(vm.HasLastSuccessfulEncryptedArchive);
        Assert.True(string.IsNullOrEmpty(vm.ShownRecoveryKey));
        Assert.True(Directory.Exists(decoyStaging));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(decoyStaging, "keep.txt")));
        Assert.True(File.Exists(decoyTmp));
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, File.ReadAllBytes(existing));
    }

    [Fact]
    public async Task PasswordMismatch_DoesNotCreateArchive()
    {
        using var harness = Harness.Create(_root);
        var spy = new RecordingArchiveService();
        var vm = harness.CreateVm(spy);
        vm.EncryptedArchiveDestinationPath = Path.Combine(harness.OutDir, "mismatch.qnar");
        vm.SetEncryptedArchivePasswordInput("one");
        vm.SetEncryptedArchivePasswordConfirmInput("two");
        await vm.ExecuteCreateEncryptedArchiveAsync();
        Assert.Null(spy.LastCreate);
        Assert.False(File.Exists(vm.EncryptedArchiveDestinationPath));
        Assert.False(vm.HasLastSuccessfulEncryptedArchive);
    }

    [Fact]
    public async Task SettingsSave_PreservesLastEncryptedArchive_WithoutSecrets()
    {
        using var harness = Harness.Create(_root);
        var vm = harness.CreateVm(harness.Service);
        string archivePath = Path.Combine(harness.OutDir, "keep.qnar");
        vm.EncryptedArchiveDestinationPath = archivePath;
        vm.SetEncryptedArchivePasswordInput(harness.ArchivePassword);
        vm.SetEncryptedArchivePasswordConfirmInput(harness.ArchivePassword);
        await vm.ExecuteCreateEncryptedArchiveAsync();
        string key = vm.ShownRecoveryKey;
        vm.SetRecoveryKeyConfirmationInput(key);
        await vm.ExecuteVerifyEncryptedArchiveRecoveryAsync();

        using var hotkey = new GlobalHotkeyService((_, _, _, _) => true, (_, _) => true, IntPtr.Zero);
        var settingsVm = SettingsViewModelTestComposition.Create(harness.Settings, hotkey);
        Assert.Contains("keep.qnar", settingsVm.EncryptedArchiveLastSuccessBanner, StringComparison.OrdinalIgnoreCase);
        settingsVm.ShowMessageAction = (_, _, _) => { };
        await settingsVm.SaveAsync();

        string json = File.ReadAllText(harness.SettingsPath);
        Assert.DoesNotContain(key, json, StringComparison.Ordinal);
        Assert.DoesNotContain(harness.ArchivePassword, json, StringComparison.Ordinal);
        var reloaded = JsonSerializer.Deserialize<AppSettings>(json);
        Assert.NotNull(reloaded);
        Assert.Equal(Path.GetFullPath(archivePath), Path.GetFullPath(reloaded!.LastEncryptedArchivePath!));
    }

    [Fact]
    public async Task CloseCommand_ClearsPendingRecoveryKey()
    {
        using var harness = Harness.Create(_root);
        var spy = new RecordingArchiveService { CreateWritesFile = true };
        var vm = harness.CreateVm(spy);
        vm.EncryptedArchiveDestinationPath = Path.Combine(harness.OutDir, "close.qnar");
        vm.SetEncryptedArchivePasswordInput("pw");
        vm.SetEncryptedArchivePasswordConfirmInput("pw");
        await vm.ExecuteCreateEncryptedArchiveAsync();
        Assert.False(string.IsNullOrEmpty(vm.ShownRecoveryKey));
        vm.CloseCommand.Execute(null);
        Assert.True(string.IsNullOrEmpty(vm.ShownRecoveryKey));
        Assert.False(vm.HasPendingRecoveryVerification);
        Assert.False(vm.HasLastSuccessfulEncryptedArchive);
    }

    [Fact]
    public async Task OfflineCopy_RequiresConfirm_WritesNewFileApartFromArchive_AndDoesNotUseClipboard()
    {
        using var harness = Harness.Create(_root);
        var vm = harness.CreateVm(harness.Service);
        string archivePath = Path.Combine(harness.OutDir, "offline.qnar");
        vm.EncryptedArchiveDestinationPath = archivePath;
        vm.SetEncryptedArchivePasswordInput(harness.ArchivePassword);
        vm.SetEncryptedArchivePasswordConfirmInput(harness.ArchivePassword);
        await vm.ExecuteCreateEncryptedArchiveAsync();
        string key = vm.ShownRecoveryKey;
        string copyPath = Path.Combine(harness.Root, "docs", "rk.txt");
        bool confirmed = false;
        vm.EncryptedArchiveConfirmProvider = (msg, _) =>
        {
            confirmed = true;
            Assert.Contains("буфер", msg, StringComparison.OrdinalIgnoreCase);
            return true;
        };
        vm.RecoveryKeyOfflineSavePathProvider = () => copyPath;
        await vm.ExecuteSaveRecoveryKeyOfflineCopyAsync();

        Assert.True(confirmed);
        Assert.True(File.Exists(copyPath));
        string text = File.ReadAllText(copyPath);
        Assert.Contains(key, text, StringComparison.Ordinal);
        Assert.Contains(RecoveryKeyEncoding.ChecksumGroups(key), text, StringComparison.Ordinal);
        Assert.Equal(key, vm.ShownRecoveryKey);
        if (File.Exists(harness.SettingsPath))
        {
            Assert.DoesNotContain(key, File.ReadAllText(harness.SettingsPath), StringComparison.Ordinal);
        }
        Assert.DoesNotContain("Clipboard", File.ReadAllText(Path.Combine(FindSolutionRoot(), "QuickNotes.App", "ViewModels", "ImportExportViewModel.cs")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task OfflineCopy_CancelledConfirm_DoesNotWrite()
    {
        using var harness = Harness.Create(_root);
        var vm = harness.CreateVm(harness.Service);
        string archivePath = Path.Combine(harness.OutDir, "offline-cancel.qnar");
        vm.EncryptedArchiveDestinationPath = archivePath;
        vm.SetEncryptedArchivePasswordInput(harness.ArchivePassword);
        vm.SetEncryptedArchivePasswordConfirmInput(harness.ArchivePassword);
        await vm.ExecuteCreateEncryptedArchiveAsync();
        string copyPath = Path.Combine(harness.Root, "docs", "nope.txt");
        vm.EncryptedArchiveConfirmProvider = (_, _) => false;
        vm.RecoveryKeyOfflineSavePathProvider = () => copyPath;
        await vm.ExecuteSaveRecoveryKeyOfflineCopyAsync();
        Assert.False(File.Exists(copyPath));
        Assert.True(vm.HasPendingRecoveryVerification);
    }

    [Fact]
    public async Task PrintRecoveryKey_RequiresConfirm_AndDoesNotCopyClipboard()
    {
        using var harness = Harness.Create(_root);
        var vm = harness.CreateVm(harness.Service);
        string archivePath = Path.Combine(harness.OutDir, "print.qnar");
        vm.EncryptedArchiveDestinationPath = archivePath;
        vm.SetEncryptedArchivePasswordInput(harness.ArchivePassword);
        vm.SetEncryptedArchivePasswordConfirmInput(harness.ArchivePassword);
        await vm.ExecuteCreateEncryptedArchiveAsync();
        string? printed = null;
        vm.EncryptedArchiveConfirmProvider = (_, _) => true;
        vm.PrintRecoveryKeyDocumentProvider = doc =>
        {
            printed = doc;
            return true;
        };
        vm.PrintShownRecoveryKey();
        Assert.NotNull(printed);
        Assert.Contains(vm.ShownRecoveryKey, printed, StringComparison.Ordinal);
        Assert.Contains(RecoveryKeyOfflineCopy.FileTitle, printed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rotate_ShowsNewKeyOnce_OldKeyRejected_AndIsAsync()
    {
        using var harness = Harness.Create(_root);
        var vm = harness.CreateVm(harness.Service);
        string archivePath = Path.Combine(harness.OutDir, "rotate.qnar");
        vm.EncryptedArchiveDestinationPath = archivePath;
        vm.SetEncryptedArchivePasswordInput(harness.ArchivePassword);
        vm.SetEncryptedArchivePasswordConfirmInput(harness.ArchivePassword);
        await vm.ExecuteCreateEncryptedArchiveAsync();
        string oldKey = vm.ShownRecoveryKey;
        vm.SetRecoveryKeyConfirmationInput(oldKey);
        await vm.ExecuteVerifyEncryptedArchiveRecoveryAsync();
        Assert.True(string.IsNullOrEmpty(vm.ShownRecoveryKey));

        vm.EncryptedArchiveRotatePath = archivePath;
        vm.SetEncryptedArchiveRotateRecoveryInput(oldKey);
        vm.EncryptedArchiveConfirmProvider = (_, _) => true;
        await vm.ExecuteRotateEncryptedArchiveRecoveryAsync();
        string newKey = vm.ShownRecoveryKey;
        Assert.False(string.IsNullOrEmpty(newKey));
        Assert.NotEqual(oldKey, newKey);
        Assert.True(vm.HasPendingRecoveryVerification);
        Assert.False(vm.CanEditEncryptedArchiveRotateInputs);

        vm.SetRecoveryKeyConfirmationInput(newKey);
        await vm.ExecuteVerifyEncryptedArchiveRecoveryAsync();
        Assert.True(string.IsNullOrEmpty(vm.ShownRecoveryKey));

        Assert.Throws<EncryptedArchiveSecurityException>(() => harness.Service.DryRun(new EncryptedArchiveDryRunRequest
        {
            ArchivePath = archivePath,
            RecoveryKeyFormatted = oldKey
        }));
    }

    [Fact]
    public async Task Rotate_CancelledConfirm_DoesNotCallService()
    {
        using var harness = Harness.Create(_root);
        var spy = new RecordingArchiveService();
        var vm = harness.CreateVm(spy);
        vm.EncryptedArchiveRotatePath = Path.Combine(harness.OutDir, "no-rotate.qnar");
        vm.SetEncryptedArchiveRotatePasswordInput("pw");
        vm.EncryptedArchiveConfirmProvider = (_, _) => false;
        await vm.ExecuteRotateEncryptedArchiveRecoveryAsync();
        Assert.Null(spy.LastRotate);
        Assert.False(vm.HasPendingRecoveryVerification);
    }

    [Fact]
    public async Task RotateAsync_DoesNotBlockCallingThread()
    {
        using var harness = Harness.Create(_root);
        using var blocking = new BlockingArchiveService();
        var vm = harness.CreateVm(blocking);
        vm.EncryptedArchiveRotatePath = Path.Combine(harness.OutDir, "blocking-rotate.qnar");
        vm.SetEncryptedArchiveRotatePasswordInput("pw");
        vm.EncryptedArchiveConfirmProvider = (_, _) => true;

        var callerReturned = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
        var uiThread = new Thread(() =>
        {
            try
            {
                callerReturned.TrySetResult(vm.ExecuteRotateEncryptedArchiveRecoveryAsync());
            }
            catch (Exception ex)
            {
                callerReturned.TrySetException(ex);
            }
        })
        {
            IsBackground = true
        };
        uiThread.Start();

        Task rotateTask = await callerReturned.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(blocking.RotateEntered.Wait(TimeSpan.FromSeconds(5)));
        Assert.False(rotateTask.IsCompleted);
        Assert.True(vm.IsEncryptedArchiveBusy);
        Assert.False(vm.CanEditEncryptedArchiveRotateInputs);
        blocking.Release.Set();
        await rotateTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(vm.IsEncryptedArchiveBusy);
        Assert.True(vm.HasPendingRecoveryVerification);
        Assert.NotEqual(uiThread.ManagedThreadId, blocking.RotateThreadId);
    }

    [Fact]
    public async Task CreateAsync_DoesNotBlockCallingThread_AndDoubleExecuteCallsServiceOnce()
    {
        using var harness = Harness.Create(_root);
        using var blocking = new BlockingArchiveService();
        var vm = harness.CreateVm(blocking);
        vm.EncryptedArchiveDestinationPath = Path.Combine(harness.OutDir, "async.qnar");
        vm.SetEncryptedArchivePasswordInput("pw");
        vm.SetEncryptedArchivePasswordConfirmInput("pw");

        var callerReturned = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
        var uiThread = new Thread(() =>
        {
            try
            {
                callerReturned.TrySetResult(vm.ExecuteCreateEncryptedArchiveAsync());
            }
            catch (Exception ex)
            {
                callerReturned.TrySetException(ex);
            }
        })
        {
            IsBackground = true
        };
        uiThread.Start();

        Task createTask = await callerReturned.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(blocking.Entered.Wait(TimeSpan.FromSeconds(5)));
        Assert.False(createTask.IsCompleted);
        Assert.True(vm.IsEncryptedArchiveBusy);
        Assert.False(vm.CanCloseImportExportWindow);
        Assert.False(vm.CloseCommand.CanExecute(null));
        Assert.False(vm.CancelEncryptedArchiveSetupCommand.CanExecute(null));
        Assert.False(vm.CreateEncryptedArchiveCommand.CanExecute(null));
        vm.CloseCommand.Execute(null);
        vm.CancelEncryptedArchiveSetup();
        Assert.True(string.IsNullOrEmpty(vm.ShownRecoveryKey));
        Assert.False(vm.HasPendingRecoveryVerification);

        Task second = vm.ExecuteCreateEncryptedArchiveAsync();
        await second.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, blocking.CreateCalls);

        blocking.Release.Set();
        await createTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(vm.IsEncryptedArchiveBusy);
        Assert.True(vm.HasPendingRecoveryVerification);
        Assert.False(string.IsNullOrEmpty(vm.ShownRecoveryKey));
        Assert.True(vm.CanCloseImportExportWindow);
        Assert.NotEqual(uiThread.ManagedThreadId, blocking.CreateThreadId);
        Assert.True(vm.CloseCommand.CanExecute(null));
    }

    [Fact]
    public async Task VerifyAsync_DoesNotBlockCallingThread_AndClearsSecretsWhenFinished()
    {
        using var harness = Harness.Create(_root);
        using var blocking = new BlockingArchiveService { CreateWritesFile = true };
        var vm = harness.CreateVm(blocking);
        vm.EncryptedArchiveDestinationPath = Path.Combine(harness.OutDir, "verify-async.qnar");
        vm.SetEncryptedArchivePasswordInput("pw");
        vm.SetEncryptedArchivePasswordConfirmInput("pw");
        blocking.Release.Set();
        await vm.ExecuteCreateEncryptedArchiveAsync();
        blocking.Release.Reset();
        blocking.Entered.Reset();
        string key = vm.ShownRecoveryKey;
        vm.SetRecoveryKeyConfirmationInput(key);

        var callerReturned = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
        var uiThread = new Thread(() =>
        {
            try
            {
                callerReturned.TrySetResult(vm.ExecuteVerifyEncryptedArchiveRecoveryAsync());
            }
            catch (Exception ex)
            {
                callerReturned.TrySetException(ex);
            }
        })
        {
            IsBackground = true
        };
        uiThread.Start();

        Task verifyTask = await callerReturned.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(blocking.DryRunEntered.Wait(TimeSpan.FromSeconds(5)));
        Assert.False(verifyTask.IsCompleted);
        Assert.True(vm.IsEncryptedArchiveBusy);
        Assert.False(vm.CanCloseImportExportWindow);
        Assert.False(vm.CancelEncryptedArchiveSetupCommand.CanExecute(null));
        Task second = vm.ExecuteVerifyEncryptedArchiveRecoveryAsync();
        await second.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, blocking.DryRunCalls);

        blocking.Release.Set();
        await verifyTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(vm.IsEncryptedArchiveBusy);
        Assert.True(string.IsNullOrEmpty(vm.ShownRecoveryKey));
        Assert.False(vm.HasPendingRecoveryVerification);
        Assert.True(vm.HasLastSuccessfulEncryptedArchive);
    }

    [Fact]
    public void UiSources_DoNotSweepTempByMask()
    {
        string root = FindSolutionRoot();
        string[] files =
        {
            Path.Combine(root, "QuickNotes.App", "ViewModels", "ImportExportViewModel.cs"),
            Path.Combine(root, "QuickNotes.App", "ViewModels", "EncryptedArchiveSetupUi.cs"),
            Path.Combine(root, "QuickNotes.App", "Views", "ImportExportWindow.xaml.cs"),
            Path.Combine(root, "QuickNotes.App", "Views", "ImportExportWindow.xaml")
        };

        foreach (string file in files)
        {
            string text = File.ReadAllText(file);
            Assert.DoesNotContain("AssertNoArchiveTempLeft", text, StringComparison.Ordinal);
            Assert.DoesNotContain(".qnar-staging", text, StringComparison.Ordinal);
            Assert.DoesNotContain("EnumerateFileSystemEntries", text, StringComparison.Ordinal);
            Assert.DoesNotContain("Directory.Delete", text, StringComparison.Ordinal);
            Assert.DoesNotContain("File.Delete", text, StringComparison.Ordinal);
            Assert.DoesNotContain("EnumerateFiles", text, StringComparison.Ordinal);
        }
    }

    private static void AssertNoTemp(string directory)
    {
        Assert.Empty(Directory.GetDirectories(directory, ".qnar-staging-*"));
        Assert.Empty(Directory.GetFiles(directory).Where(f => f.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)));
    }

    private static string FindSolutionRoot()
    {
        var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "QuickNotes.sln")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("QuickNotes.sln");
    }

    private sealed class RecordingArchiveService : IEncryptedArchiveService
    {
        public EncryptedArchiveCreateRequest? LastCreate { get; private set; }
        public EncryptedArchiveDryRunRequest? LastDryRun { get; private set; }
        public bool CreateWritesFile { get; init; }
        public string RecoveryKey { get; } = RecoveryKeyEncoding.Format(RandomNumberGenerator.GetBytes(32));
        public int CreateCalls { get; private set; }

        public EncryptedArchiveCreateResult Create(EncryptedArchiveCreateRequest request, CancellationToken cancellationToken = default)
        {
            CreateCalls++;
            LastCreate = request;
            if (CreateWritesFile)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(request.DestinationArchivePath)!);
                File.WriteAllBytes(request.DestinationArchivePath, new byte[] { 0x51, 0x4E, 0x41, 0x52 });
            }

            return new EncryptedArchiveCreateResult
            {
                ArchivePath = Path.GetFullPath(request.DestinationArchivePath),
                ArchiveId = Guid.NewGuid(),
                RecoveryKeyFormatted = RecoveryKey,
                Pbkdf2Iterations = request.Pbkdf2Iterations ?? EncryptedArchiveConstants.DefaultPbkdf2IterationsForNewArchives,
                SnapshotUserVersion = 1,
                NoteCount = 0,
                ProtectedNoteCount = 0,
                AttachmentFileCount = 0
            };
        }

        public EncryptedArchiveDryRunResult DryRun(EncryptedArchiveDryRunRequest request, CancellationToken cancellationToken = default)
        {
            LastDryRun = request;
            return new EncryptedArchiveDryRunResult
            {
                ArchiveId = Guid.Empty,
                FormatVersion = 1,
                CreatedAtUtc = DateTime.UtcNow,
                SnapshotUserVersion = 1,
                NoteCount = 0,
                ProtectedNoteCount = 0,
                AttachmentFileCount = 0,
                PayloadEntryCount = 0,
                EntryPaths = Array.Empty<string>()
            };
        }

        public EncryptedArchiveRestoreResult Restore(EncryptedArchiveRestoreRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public EncryptedArchiveRotateRecoveryResult RotateRecovery(EncryptedArchiveRotateRecoveryRequest request, CancellationToken cancellationToken = default)
        {
            LastRotate = request;
            return new EncryptedArchiveRotateRecoveryResult
            {
                ArchivePath = Path.GetFullPath(request.ArchivePath),
                ArchiveId = Guid.NewGuid(),
                RecoveryKeyFormatted = RecoveryKey
            };
        }

        public EncryptedArchiveRotateRecoveryRequest? LastRotate { get; private set; }
    }

    private sealed class BlockingArchiveService : IEncryptedArchiveService, IDisposable
    {
        public ManualResetEventSlim Entered { get; } = new(false);
        public ManualResetEventSlim DryRunEntered { get; } = new(false);
        public ManualResetEventSlim RotateEntered { get; } = new(false);
        public ManualResetEventSlim Release { get; } = new(false);
        public int CreateCalls;
        public int DryRunCalls;
        public int RotateCalls;
        public int CreateThreadId;
        public int RotateThreadId;
        public bool CreateWritesFile { get; init; }
        public string RecoveryKey { get; } = RecoveryKeyEncoding.Format(RandomNumberGenerator.GetBytes(32));

        public EncryptedArchiveCreateResult Create(EncryptedArchiveCreateRequest request, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref CreateCalls);
            CreateThreadId = Environment.CurrentManagedThreadId;
            Entered.Set();
            Release.Wait(cancellationToken);

            if (CreateWritesFile)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(request.DestinationArchivePath)!);
                File.WriteAllBytes(request.DestinationArchivePath, new byte[] { 0x51, 0x4E, 0x41, 0x52 });
            }

            return new EncryptedArchiveCreateResult
            {
                ArchivePath = Path.GetFullPath(request.DestinationArchivePath),
                ArchiveId = Guid.NewGuid(),
                RecoveryKeyFormatted = RecoveryKey,
                Pbkdf2Iterations = request.Pbkdf2Iterations ?? EncryptedArchiveConstants.DefaultPbkdf2IterationsForNewArchives,
                SnapshotUserVersion = 1,
                NoteCount = 0,
                ProtectedNoteCount = 0,
                AttachmentFileCount = 0
            };
        }

        public EncryptedArchiveDryRunResult DryRun(EncryptedArchiveDryRunRequest request, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref DryRunCalls);
            DryRunEntered.Set();
            Release.Wait(cancellationToken);

            return new EncryptedArchiveDryRunResult
            {
                ArchiveId = Guid.Empty,
                FormatVersion = 1,
                CreatedAtUtc = DateTime.UtcNow,
                SnapshotUserVersion = 1,
                NoteCount = 0,
                ProtectedNoteCount = 0,
                AttachmentFileCount = 0,
                PayloadEntryCount = 0,
                EntryPaths = Array.Empty<string>()
            };
        }

        public EncryptedArchiveRestoreResult Restore(EncryptedArchiveRestoreRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public EncryptedArchiveRotateRecoveryResult RotateRecovery(EncryptedArchiveRotateRecoveryRequest request, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref RotateCalls);
            RotateThreadId = Environment.CurrentManagedThreadId;
            RotateEntered.Set();
            Release.Wait(cancellationToken);

            return new EncryptedArchiveRotateRecoveryResult
            {
                ArchivePath = Path.GetFullPath(request.ArchivePath),
                ArchiveId = Guid.NewGuid(),
                RecoveryKeyFormatted = RecoveryKey
            };
        }

        public void Dispose()
        {
            Release.Set();
            Entered.Dispose();
            DryRunEntered.Dispose();
            RotateEntered.Dispose();
            Release.Dispose();
        }
    }

    private sealed class Harness : IDisposable
    {
        public string Root { get; }
        public string DbPath { get; }
        public string OutDir { get; }
        public string SettingsPath { get; }
        public string ArchivePassword { get; } = "archive-password-ui-" + Guid.NewGuid().ToString("N");
        public AttachmentStorageService Storage { get; }
        public EncryptedArchiveService Service { get; }
        public SettingsService Settings { get; }
        private int _lastNoteId;

        private Harness(string parent)
        {
            Root = Path.Combine(parent, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            DbPath = Path.Combine(Root, "data", "quicknotes.db");
            Directory.CreateDirectory(Path.GetDirectoryName(DbPath)!);
            OutDir = Path.Combine(Root, "out");
            Directory.CreateDirectory(OutDir);
            Storage = new AttachmentStorageService(Path.Combine(Root, "data"));
            Service = new EncryptedArchiveService(EncryptedArchiveKdfLimits.ForTests());
            SettingsPath = Path.Combine(Root, "settings.json");
            Settings = new SettingsService(SettingsPath, _ => { });
            using var db = SqliteTestUtil.CreateContext(DbPath);
            DbInitializer.Initialize(db);
        }

        public static Harness Create(string parent) => new(parent);

        public ImportExportViewModel CreateVm(IEncryptedArchiveService archiveService)
        {
            string dbPath = DbPath;
            var storage = Storage;
            return new ImportExportViewModel(
                () => SqliteTestUtil.CreateContext(dbPath),
                new LocalMutationCoordinator(),
                attachmentStorage: storage,
                settingsService: Settings,
                encryptedArchiveService: archiveService)
            {
                TestOverridePbkdf2Iterations = archiveService is EncryptedArchiveService ? TestKdfN : null,
                EncryptedArchiveSavePathProvider = () => null,
                EncryptedArchiveOpenPathProvider = () => null,
                RecoveryKeyOfflineSavePathProvider = () => null,
                PrintRecoveryKeyDocumentProvider = _ => false,
                MessageBoxProvider = (_, _, _) => { },
                ConfirmProvider = (_, _) => true
            };
        }

        public void AddProtectedNote(string text, string notePassword)
        {
            using (var db = SqliteTestUtil.CreateContext(DbPath))
            {
                var note = new Note { Text = text, CreatedAt = DateTime.Now, UpdatedAt = DateTime.Now };
                db.Notes.Add(note);
                db.SaveChanges();
                _lastNoteId = note.Id;
            }

            var protection = new NoteProtectionService(attachmentStorage: Storage);
            using var pdb = SqliteTestUtil.CreateContext(DbPath);
            Assert.True(protection.ProtectNote(pdb, _lastNoteId, notePassword).Success);
        }

        public void Dispose() => SqliteTestUtil.TryDeleteDirectory(Root);
    }
}
