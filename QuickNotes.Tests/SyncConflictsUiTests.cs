using System;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using QuickNotes.App.Models.Sync;
using QuickNotes.App.Services;
using QuickNotes.App.ViewModels;
using QuickNotes.App.Views;
using Xunit;

namespace QuickNotes.Tests;

[Collection(LiveProfileSnapshotCollection.Name)]
[TestCategory(TestCategories.UiSensitive)]
public sealed class SyncConflictsUiTests
{
    [Fact]
    public void KeyboardAccessibleNames_ArePresentInXaml()
    {
        var root = FindSolutionRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "QuickNotes.App", "Views", "SyncConflictsWindow.xaml"));
        Assert.Contains("ConflictListBox", xaml);
        Assert.Contains("LocalBodyBox", xaml);
        Assert.Contains("RemoteBodyBox", xaml);
        Assert.Contains("MergeEditorBox", xaml);
        Assert.Contains("KeepLocalButton", xaml);
        Assert.Contains("KeepRemoteButton", xaml);
        Assert.Contains("KeepBothButton", xaml);
        Assert.Contains("MergeButton", xaml);
        Assert.Contains("MergeTitleLocalRadio", xaml);
        Assert.Contains("AutomationProperties.Name=\"Оставить локальную версию\"", xaml);
        Assert.Contains("AutomationProperties.Name=\"Оставить облачную версию\"", xaml);
        Assert.Contains("AutomationProperties.Name=\"Редактор объединённого Markdown\"", xaml);
        Assert.Contains("PreviewKeyDown=\"Window_PreviewKeyDown\"", xaml);
        Assert.DoesNotContain("Key=\"D1\"", xaml);
        Assert.DoesNotContain("Key=\"D2\"", xaml);
        Assert.DoesNotContain("Key=\"D3\"", xaml);
        Assert.DoesNotContain("Key=\"D4\"", xaml);
        Assert.DoesNotContain("WebView2", xaml);
    }

    [Fact]
    public async Task DigitKeysInMergeEditor_DoNotResolveConflict()
    {
        var fake = new SyncConflictsViewModelTestsFake();
        fake.Conflicts.Add(new SyncConflictRecord { Id = 3, EntityType = "Note", Reason = "r" });
        fake.Details[3] = new SyncConflictDetail
        {
            ConflictId = 3,
            EntityType = "Note",
            LocalText = "L",
            RemoteText = "R",
            InitialMergedText = "draft 1 then 2"
        };
        var vm = new SyncConflictsViewModel(fake, loadOnStart: false);
        await vm.LoadConflictsAsync();
        vm.UseLocalTitle = true;
        vm.UseLocalTags = true;

        StaTestHarness.Run(() =>
        {
            var window = new SyncConflictsWindow(vm)
            {
                Width = 780,
                Height = 720,
                ShowInTaskbar = false,
                Left = -20000,
                Top = -20000,
                WindowStartupLocation = WindowStartupLocation.Manual
            };
            window.Show();
            window.UpdateLayout();
            var merge = (TextBox)window.FindName("MergeEditorBox")!;
            merge.Focus();
            merge.Text = "line 1";
            merge.CaretIndex = merge.Text.Length;

            var args = new System.Windows.Input.KeyEventArgs(
                System.Windows.Input.Keyboard.PrimaryDevice,
                PresentationSource.FromVisual(window) ?? throw new InvalidOperationException("No PresentationSource"),
                0,
                System.Windows.Input.Key.D1)
            {
                RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent
            };
            window.RaiseEvent(args);
            merge.RaiseEvent(args);

            var numPad = new System.Windows.Input.KeyEventArgs(
                System.Windows.Input.Keyboard.PrimaryDevice,
                PresentationSource.FromVisual(window) ?? throw new InvalidOperationException("No PresentationSource"),
                0,
                System.Windows.Input.Key.NumPad1)
            {
                RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent
            };
            window.RaiseEvent(numPad);
            merge.RaiseEvent(numPad);

            Assert.Equal(0, fake.KeepLocalCalls);
            Assert.Single(vm.Conflicts);
            window.Close();
        }, TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task MergeCommit_DisabledUntilExplicitTitleAndTagChoices()
    {
        var fake = new SyncConflictsViewModelTestsFake();
        fake.Conflicts.Add(new SyncConflictRecord { Id = 1, EntityType = "Note", Reason = "r" });
        fake.Details[1] = new SyncConflictDetail
        {
            ConflictId = 1,
            EntityType = "Note",
            LocalText = "L",
            RemoteText = "R",
            InitialMergedText = "L\nR"
        };
        var vm = new SyncConflictsViewModel(fake, loadOnStart: false);
        await vm.LoadConflictsAsync();
        Assert.False(vm.CanCommitMerge);
        Assert.False(vm.MergeCommand.CanExecute(null));
        Assert.True(vm.KeepLocalCommand.CanExecute(null));
        Assert.True(vm.AcceptRemoteCommand.CanExecute(null));

        vm.UseLocalTitle = true;
        Assert.False(vm.CanCommitMerge);
        vm.UseLocalTags = true;
        Assert.True(vm.CanCommitMerge);
        Assert.True(vm.MergeCommand.CanExecute(null));
    }

    [Fact]
    public async Task NarrowWindow_CompareAndMerge_AreReachableWithinClientBounds()
    {
        var fake = new SyncConflictsViewModelTestsFake();
        fake.Conflicts.Add(new SyncConflictRecord { Id = 7, EntityType = "Note", Reason = "Одновременные правки Markdown" });
        fake.Details[7] = new SyncConflictDetail
        {
            ConflictId = 7,
            EntityType = "Note",
            LocalTitle = "Локальный заголовок",
            LocalText = SyncConflictUiSmokeRunner.LocalBody,
            RemoteTitle = "Облачный заголовок",
            RemoteText = SyncConflictUiSmokeRunner.RemoteBody,
            InitialMergedText = "draft",
            LocalDeviceDisplay = "Это устройство (aaaaaaaa)",
            RemoteDeviceDisplay = "Устройство 11111111",
            LocalUpdatedDisplay = "12.09.2026 10:00",
            RemoteUpdatedDisplay = "12.09.2026 11:00",
            ReasonDisplay = "Одновременные правки Markdown"
        };

        var vm = new SyncConflictsViewModel(fake, loadOnStart: false);
        await vm.LoadConflictsAsync();
        vm.MergedText = SyncConflictUiSmokeRunner.MergeBody;
        vm.UseLocalTitle = true;
        vm.UseRemoteTags = true;

        StaTestHarness.Run(() =>
        {
            var window = new SyncConflictsWindow(vm)
            {
                Width = 640,
                Height = 560,
                MinWidth = 640,
                MinHeight = 520
            };
            window.Show();
            window.UpdateLayout();
            Assert.True(vm.CanCommitMerge);

            var local = (TextBox)window.FindName("LocalBodyBox")!;
            var remote = (TextBox)window.FindName("RemoteBodyBox")!;
            var merge = (TextBox)window.FindName("MergeEditorBox")!;
            var commit = (Button)window.FindName("MergeButton")!;
            merge.BringIntoView();
            window.UpdateLayout();
            Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(local)));
            Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(commit)));
            NoteAssemblyUiSmokeRunner.AssertVisibleInClient(window, commit, "MergeButton");
            NoteAssemblyUiSmokeRunner.AssertUnclipped(window, commit, "MergeButton", 16);
            Assert.True(local.IsReadOnly);
            Assert.True(remote.IsReadOnly);
            Assert.False(merge.IsReadOnly);
            window.Close();
        }, TimeSpan.FromSeconds(30));
    }

    private static string FindSolutionRoot()
    {
        var current = AppDomain.CurrentDomain.BaseDirectory;
        while (!string.IsNullOrEmpty(current))
        {
            if (File.Exists(Path.Combine(current, "QuickNotes.sln")))
            {
                return current;
            }

            var parent = Directory.GetParent(current);
            if (parent == null)
            {
                break;
            }

            current = parent.FullName;
        }

        return @"D:\work\QuickNotes";
    }
}

internal sealed class SyncConflictsViewModelTestsFake : QuickNotes.App.Services.Sync.ISyncConflictService
{
    public System.Collections.Generic.List<SyncConflictRecord> Conflicts { get; } = new();
    public System.Collections.Generic.Dictionary<int, SyncConflictDetail> Details { get; } = new();

    public System.Threading.Tasks.Task<System.Collections.Generic.List<SyncConflictRecord>> GetUnresolvedConflictsAsync(System.Threading.CancellationToken ct = default)
        => System.Threading.Tasks.Task.FromResult(Conflicts.FindAll(c => !c.IsResolved));

    public System.Threading.Tasks.Task<int> GetUnresolvedConflictsCountAsync(System.Threading.CancellationToken ct = default)
        => System.Threading.Tasks.Task.FromResult(Conflicts.FindAll(c => !c.IsResolved).Count);

    public System.Threading.Tasks.Task<SyncConflictDetail?> GetConflictDetailAsync(int conflictId, System.Threading.CancellationToken ct = default)
    {
        Details.TryGetValue(conflictId, out var detail);
        return System.Threading.Tasks.Task.FromResult(detail);
    }

    public System.Threading.Tasks.Task<SyncConflictResolutionResult> ResolveKeepBothAsync(int conflictId, System.Threading.CancellationToken ct = default)
        => System.Threading.Tasks.Task.FromResult(SyncConflictResolutionResult.Succeeded(conflictId, "KeepBoth"));

    public int KeepLocalCalls { get; private set; }

    public System.Threading.Tasks.Task<SyncConflictResolutionResult> ResolveKeepLocalAsync(int conflictId, System.Threading.CancellationToken ct = default)
    {
        KeepLocalCalls++;
        return System.Threading.Tasks.Task.FromResult(SyncConflictResolutionResult.Succeeded(conflictId, "KeepLocal"));
    }

    public System.Threading.Tasks.Task<SyncConflictResolutionResult> ResolveAcceptRemoteAsync(int conflictId, System.Threading.CancellationToken ct = default)
        => System.Threading.Tasks.Task.FromResult(SyncConflictResolutionResult.Succeeded(conflictId, "AcceptRemote"));

    public System.Threading.Tasks.Task<SyncConflictResolutionResult> ResolveMergeNoteAsync(int conflictId, string mergedText, System.Threading.CancellationToken ct = default)
        => System.Threading.Tasks.Task.FromResult(SyncConflictResolutionResult.Succeeded(conflictId, "Merge"));
}
