using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using Microsoft.EntityFrameworkCore;
using QuickNotes.App.Data;
using QuickNotes.App.Models;
using QuickNotes.App.Services;
using QuickNotes.App.ViewModels;
using QuickNotes.App.Views;
using Xunit;

namespace QuickNotes.Tests;

/// <summary>
/// W1 workspace shell acceptance: reversible Board / List switch, fail-safe
/// persistence, and regression guard for the existing three-pane List layout.
/// </summary>
[Collection(LiveProfileSnapshotCollection.Name)]
[TestCategory(TestCategories.UiSensitive)]
public sealed class WorkspaceShellTests
{
    private static void RunInSta(Action action) => StaTestHarness.Run(action, TimeSpan.FromSeconds(30));

    [Fact]
    public void AppSettings_DefaultAndMissingWorkspaceViewMode_ResolveToList()
    {
        Assert.Equal(WorkspaceViewMode.List, new AppSettings().WorkspaceViewMode);

        // Settings payload from an earlier version without the field.
        const string oldJson = """
        {
            "HotkeyKey": "Space",
            "CompactCards": false,
            "TagPanelWidth": 260
        }
        """;

        var deserialized = JsonSerializer.Deserialize<AppSettings>(oldJson);
        Assert.NotNull(deserialized);
        Assert.Equal(WorkspaceViewMode.List, deserialized.WorkspaceViewMode);
    }

    [Fact]
    public void WorkspaceViewModeJson_UnknownOrInvalidValue_ResolvesToListWithoutThrowing()
    {
        const string unknownString = """{ "WorkspaceViewMode": "Карточки" }""";
        var fromUnknownString = JsonSerializer.Deserialize<AppSettings>(unknownString);
        Assert.NotNull(fromUnknownString);
        Assert.Equal(WorkspaceViewMode.List, fromUnknownString.WorkspaceViewMode);

        const string invalidNumber = """{ "WorkspaceViewMode": 99 }""";
        var fromInvalidNumber = JsonSerializer.Deserialize<AppSettings>(invalidNumber);
        Assert.NotNull(fromInvalidNumber);
        Assert.Equal(WorkspaceViewMode.List, fromInvalidNumber.WorkspaceViewMode);

        const string boardString = """{ "WorkspaceViewMode": "Board" }""";
        var fromBoard = JsonSerializer.Deserialize<AppSettings>(boardString);
        Assert.NotNull(fromBoard);
        Assert.Equal(WorkspaceViewMode.Board, fromBoard.WorkspaceViewMode);
    }

    [Fact]
    public void SettingsService_RoundTripsBoardThenList()
    {
        string tempProfile = Path.Combine(Path.GetTempPath(), "qn_shell_settings_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempProfile);
        try
        {
            string settingsPath = Path.Combine(tempProfile, "settings.json");

            var boardService = new SettingsService(settingsPath, _ => { });
            boardService.CurrentSettings.WorkspaceViewMode = WorkspaceViewMode.Board;
            boardService.SaveSettings(boardService.CurrentSettings);

            Assert.Equal(WorkspaceViewMode.Board, new SettingsService(settingsPath, _ => { }).CurrentSettings.WorkspaceViewMode);

            var listService = new SettingsService(settingsPath, _ => { });
            listService.CurrentSettings.WorkspaceViewMode = WorkspaceViewMode.List;
            listService.SaveSettings(listService.CurrentSettings);

            Assert.Equal(WorkspaceViewMode.List, new SettingsService(settingsPath, _ => { }).CurrentSettings.WorkspaceViewMode);
        }
        finally
        {
            SqliteTestUtil.TryDeleteDirectory(tempProfile);
        }
    }

    [Fact]
    public void SettingsService_UnknownWorkspaceViewModeOnDisk_LoadsAsListWithoutThrow()
    {
        string tempProfile = Path.Combine(Path.GetTempPath(), "qn_shell_corrupt_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempProfile);
        try
        {
            string settingsPath = Path.Combine(tempProfile, "settings.json");
            File.WriteAllText(settingsPath, """{ "WorkspaceViewMode": "Что-то", "CompactCards": true }""");

            var service = new SettingsService(settingsPath, _ => { });

            Assert.Equal(WorkspaceViewMode.List, service.CurrentSettings.WorkspaceViewMode);
            Assert.True(service.CurrentSettings.CompactCards);
        }
        finally
        {
            SqliteTestUtil.TryDeleteDirectory(tempProfile);
        }
    }

    [Fact]
    public async Task SettingsViewModel_Save_PreservesWorkspaceViewMode()
    {
        string tempProfile = Path.Combine(Path.GetTempPath(), "qn_shell_settingsvm_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempProfile);
        try
        {
            string settingsPath = Path.Combine(tempProfile, "settings.json");
            var settingsService = new SettingsService(settingsPath, _ => { });
            settingsService.CurrentSettings.WorkspaceViewMode = WorkspaceViewMode.Board;
            settingsService.SaveSettings(settingsService.CurrentSettings);

            var vm = SettingsViewModelTestComposition.Create(
                settingsService,
                new GlobalHotkeyService((_, _, _, _) => true, (_, _) => true, IntPtr.Zero),
                showMessage: (_, _, _) => { });

            await vm.SaveAsync();

            Assert.Equal(WorkspaceViewMode.Board, settingsService.CurrentSettings.WorkspaceViewMode);
            Assert.Equal(WorkspaceViewMode.Board, new SettingsService(settingsPath, _ => { }).CurrentSettings.WorkspaceViewMode);
        }
        finally
        {
            SqliteTestUtil.TryDeleteDirectory(tempProfile);
        }
    }

    [Fact]
    public void WorkspaceShell_DefaultSettings_StartInListMode_WithAllPanesPresent()
    {
        string liveRoot = QuickNotesDbContext.LiveProfileDirectory;
        string[] liveBefore = SnapshotLiveProfile(liveRoot);

        string tempProfile = Path.Combine(Path.GetTempPath(), "qn_shell_list_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempProfile);
        try
        {
            RunInSta(() =>
            {
                ThemeService.ApplyThemeColors(isDark: false);
                using var fixture = new TestProfileComposition(tempProfile, seedNotes: 3, ownsDirectory: false);
                var (window, vm) = fixture;

                window.Width = 1100;
                window.Height = 720;
                window.Measure(new Size(1100, 720));
                window.Arrange(new Rect(0, 0, 1100, 720));
                window.UpdateLayout();

                Assert.Equal(WorkspaceViewMode.List, vm.WorkspaceViewMode);
                Assert.True(vm.IsListViewMode);
                Assert.False(vm.IsBoardMode);
                Assert.False(vm.IsNarrow);

                Assert.Equal(Visibility.Visible, window.NavBorder.Visibility);
                Assert.Equal(Visibility.Visible, window.NotesListGrid.Visibility);
                Assert.Equal(Visibility.Visible, window.DetailPaneBorder.Visibility);
                Assert.Equal(Visibility.Collapsed, window.BoardHost.Visibility);
                Assert.Equal(Visibility.Visible, window.NavSplitter.Visibility);
                Assert.Equal(Visibility.Visible, window.DetailSplitter.Visibility);
            });

            Assert.Equal(liveBefore, SnapshotLiveProfile(liveRoot));
        }
        finally
        {
            SqliteTestUtil.TryDeleteDirectory(tempProfile);
        }
    }

    [Fact]
    public void WorkspaceShell_BoardMode_ShowsBoardHost_HidesListAndDetail_WithDistinctCopy()
    {
        string liveRoot = QuickNotesDbContext.LiveProfileDirectory;
        string[] liveBefore = SnapshotLiveProfile(liveRoot);

        string tempProfile = Path.Combine(Path.GetTempPath(), "qn_shell_board_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempProfile);
        try
        {
            RunInSta(() =>
            {
                ThemeService.ApplyThemeColors(isDark: false);
                using var fixture = new TestProfileComposition(tempProfile, seedNotes: 3, ownsDirectory: false);
                var (window, vm) = fixture;

                window.Width = 1100;
                window.Height = 720;
                window.Measure(new Size(1100, 720));
                window.Arrange(new Rect(0, 0, 1100, 720));
                window.UpdateLayout();

                vm.SetBoardViewModeCommand.Execute(null);
                window.UpdateWorkspaceLayout();
                window.Measure(new Size(1100, 720));
                window.Arrange(new Rect(0, 0, 1100, 720));
                window.UpdateLayout();

                Assert.Equal(WorkspaceViewMode.Board, vm.WorkspaceViewMode);
                Assert.True(vm.IsBoardMode);
                Assert.Equal(Visibility.Visible, window.BoardHost.Visibility);
                Assert.Equal(Visibility.Collapsed, window.NotesListGrid.Visibility);
                Assert.Equal(Visibility.Collapsed, window.DetailPaneBorder.Visibility);
                Assert.Equal(0, window.ListCol.Width.Value);
                Assert.Equal(0, window.DetailCol.Width.Value);

                // Navigation may stay visible on the Board surface.
                Assert.Equal(Visibility.Visible, window.NavBorder.Visibility);

                // The top-bar toggle reflects the active mode.
                ThreePaneUiSmokeRunner.DoEvents();
                Assert.True(window.WorkspaceModeToggle.IsChecked);

                // Board empty copy must not reuse the library/section/search empty state copy.
                Assert.NotEqual(vm.EmptyStateTitle, window.BoardEmptyTitle.Text);
                Assert.NotEqual(vm.EmptyStateDescription, window.BoardEmptyDescription.Text);
                Assert.False(string.IsNullOrWhiteSpace(window.BoardEmptyDescription.Text));

                // Reversible: back to List restores the three-pane surface.
                vm.SetListViewModeCommand.Execute(null);
                window.UpdateWorkspaceLayout();
                window.Measure(new Size(1100, 720));
                window.Arrange(new Rect(0, 0, 1100, 720));
                window.UpdateLayout();

                Assert.Equal(WorkspaceViewMode.List, vm.WorkspaceViewMode);
                Assert.Equal(Visibility.Collapsed, window.BoardHost.Visibility);
                Assert.Equal(Visibility.Visible, window.NotesListGrid.Visibility);
                Assert.Equal(Visibility.Visible, window.DetailPaneBorder.Visibility);
            });

            Assert.Equal(liveBefore, SnapshotLiveProfile(liveRoot));
        }
        finally
        {
            SqliteTestUtil.TryDeleteDirectory(tempProfile);
        }
    }

    [Fact]
    public void WorkspaceShell_Toggle_DoesNotCreateNotesRevisionsOrSyncEnqueue()
    {
        string liveRoot = QuickNotesDbContext.LiveProfileDirectory;
        string[] liveBefore = SnapshotLiveProfile(liveRoot);

        string tempProfile = Path.Combine(Path.GetTempPath(), "qn_shell_nosave_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempProfile);
        try
        {
            RunInSta(() =>
            {
                ThemeService.ApplyThemeColors(isDark: false);
                using var fixture = new TestProfileComposition(tempProfile, seedNotes: 2, ownsDirectory: false);
                var scheduler = new FakeTrackingSyncScheduler();
                var vm = fixture.CreateMainViewModel(syncScheduler: scheduler);
                var window = fixture.CreateMainWindow(vm);

                window.Measure(new Size(1100, 720));
                window.Arrange(new Rect(0, 0, 1100, 720));
                window.UpdateLayout();

                int notesBefore;
                int revisionsBefore;
                using (var db = fixture.CreateDbContext())
                {
                    notesBefore = db.Notes.Count();
                    revisionsBefore = db.NoteRevisions.Count();
                }

                scheduler.EnqueueLocalChangeCallCount = 0;

                vm.SetBoardViewModeCommand.Execute(null);
                window.UpdateWorkspaceLayout();
                vm.SetListViewModeCommand.Execute(null);
                window.UpdateWorkspaceLayout();
                vm.IsBoardMode = true;
                vm.IsBoardMode = false;

                using (var db = fixture.CreateDbContext())
                {
                    Assert.Equal(notesBefore, db.Notes.Count());
                    Assert.Equal(revisionsBefore, db.NoteRevisions.Count());
                }

                Assert.Equal(0, scheduler.EnqueueLocalChangeCallCount);
                Assert.Equal(WorkspaceViewMode.List, vm.WorkspaceViewMode);
            });

            Assert.Equal(liveBefore, SnapshotLiveProfile(liveRoot));
        }
        finally
        {
            SqliteTestUtil.TryDeleteDirectory(tempProfile);
        }
    }

    [Fact]
    public void WorkspaceShell_Restart_RestoresSavedMode()
    {
        string liveRoot = QuickNotesDbContext.LiveProfileDirectory;
        string[] liveBefore = SnapshotLiveProfile(liveRoot);

        string tempProfile = Path.Combine(Path.GetTempPath(), "qn_shell_restart_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempProfile);
        try
        {
            RunInSta(() =>
            {
                ThemeService.ApplyThemeColors(isDark: false);

                using (var first = new TestProfileComposition(tempProfile, seedNotes: 1, ownsDirectory: false))
                {
                    var (_, vm) = first;
                    Assert.Equal(WorkspaceViewMode.List, vm.WorkspaceViewMode);
                    vm.WorkspaceViewMode = WorkspaceViewMode.Board;
                    Assert.Equal(WorkspaceViewMode.Board, vm.WorkspaceViewMode);
                    Assert.Equal(WorkspaceViewMode.Board, first.SettingsService.CurrentSettings.WorkspaceViewMode);
                }

                // Simulated restart: a fresh composition over the same isolated profile.
                using var restarted = new TestProfileComposition(tempProfile, seedNotes: 1, ownsDirectory: false);
                var (_, restartedVm) = restarted;
                Assert.Equal(WorkspaceViewMode.Board, restartedVm.WorkspaceViewMode);
            });

            Assert.Equal(liveBefore, SnapshotLiveProfile(liveRoot));
        }
        finally
        {
            SqliteTestUtil.TryDeleteDirectory(tempProfile);
        }
    }

    [Fact]
    public void WorkspaceShell_Toggle_HasAccessibleNameVisibleFocusRingAndIsTabReachable()
    {
        string liveRoot = QuickNotesDbContext.LiveProfileDirectory;
        string[] liveBefore = SnapshotLiveProfile(liveRoot);

        string tempProfile = Path.Combine(Path.GetTempPath(), "qn_shell_a11y_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempProfile);
        try
        {
            RunInSta(() =>
            {
                ThemeService.ApplyThemeColors(isDark: false);
                using var fixture = new TestProfileComposition(tempProfile, seedNotes: 1, ownsDirectory: false);
                var (window, vm) = fixture;

                Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(window.WorkspaceModeToggle)));
                Assert.True(window.WorkspaceModeToggle.IsTabStop);
                Assert.NotNull(window.WorkspaceModeToggle.ToolTip);
                Assert.True(window.WorkspaceModeToggle.Focusable);

                // Initial state reflects the default List mode.
                Assert.False(window.WorkspaceModeToggle.IsChecked);

                // ViewModel -> UI: switching mode updates the bound toggle.
                vm.SetBoardViewModeCommand.Execute(null);
                ThreePaneUiSmokeRunner.DoEvents();
                Assert.True(window.WorkspaceModeToggle.IsChecked);
                Assert.True(vm.IsBoardMode);

                // UI -> ViewModel: toggling the control switches mode.
                window.WorkspaceModeToggle.IsChecked = false;
                ThreePaneUiSmokeRunner.DoEvents();
                Assert.False(window.WorkspaceModeToggle.IsChecked);
                Assert.True(vm.IsListViewMode);
                Assert.Equal(WorkspaceViewMode.List, vm.WorkspaceViewMode);
            });

            Assert.Equal(liveBefore, SnapshotLiveProfile(liveRoot));
        }
        finally
        {
            SqliteTestUtil.TryDeleteDirectory(tempProfile);
        }
    }

    private static string[] SnapshotLiveProfile(string? liveRoot = null)
    {
        liveRoot ??= QuickNotesDbContext.LiveProfileDirectory;
        if (!Directory.Exists(liveRoot))
        {
            return Array.Empty<string>();
        }

        return Directory.GetFileSystemEntries(liveRoot, "*", SearchOption.AllDirectories)
            .Select(p => p + "|" + (File.Exists(p) ? new FileInfo(p).Length + "|" + File.GetLastWriteTimeUtc(p).Ticks : "dir"))
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
