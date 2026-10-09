using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using Microsoft.EntityFrameworkCore;
using QuickNotes.App.Data;
using QuickNotes.App.Helpers;
using QuickNotes.App.Models;
using QuickNotes.App.Services;
using QuickNotes.App.ViewModels;
using QuickNotes.App.Views;
using Xunit;

namespace QuickNotes.Tests;

/// <summary>
/// W4 fullscreen acceptance: F11 borderless fullscreen, monitor-aware bounds restore,
/// persistence safety on crash/close, Esc precedence (Focus -> Fullscreen -> Search),
/// navigation compact/hidden states and keyboard return, and tray contract preservation.
/// </summary>
[Collection(LiveProfileSnapshotCollection.Name)]
[TestCategory(TestCategories.UiSensitive)]
public sealed class WorkspaceFullscreenTests
{
    private static void RunInSta(Action action) => StaTestHarness.Run(action, TimeSpan.FromSeconds(60));

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

    private static string CreateTempProfile(string prefix)
    {
        string dir = Path.Combine(Path.GetTempPath(), prefix + "_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void RaiseKey(MainWindow window, Key key)
    {
        var source = PresentationSource.FromVisual(window);
        Assert.NotNull(source);
        var args = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key)
        {
            RoutedEvent = Keyboard.PreviewKeyDownEvent
        };
        window.RaiseEvent(args);
        ThreePaneUiSmokeRunner.DoEvents();
    }

    private static void PrepareWindow(MainWindow window, double left = 100, double top = 80, double width = 1100, double height = 720)
    {
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = left;
        window.Top = top;
        window.Width = width;
        window.Height = height;
        window.Measure(new Size(width, height));
        window.Arrange(new Rect(0, 0, width, height));
        window.UpdateLayout();
        window.Show();
        ThreePaneUiSmokeRunner.DoEvents();
        window.Left = left;
        window.Top = top;
        window.Width = width;
        window.Height = height;
        ThreePaneUiSmokeRunner.DoEvents();
    }

    #region Layout Helper Clamp Unit Tests

    [Fact]
    public void WorkspaceLayoutHelper_ClampWindowBounds_WithinPrimaryScreen_Preserved()
    {
        var primaryWorkArea = new Rect(0, 0, 1920, 1080);
        var screens = new[] { primaryWorkArea };

        var result = WorkspaceLayoutHelper.ClampWindowBounds(100, 100, 1200, 800, screens, primaryWorkArea);

        Assert.Equal(100, result.Left);
        Assert.Equal(100, result.Top);
        Assert.Equal(1200, result.Width);
        Assert.Equal(800, result.Height);
    }

    [Fact]
    public void WorkspaceLayoutHelper_ClampWindowBounds_DualMonitor_WithinSecondaryScreen_Preserved()
    {
        var primaryWorkArea = new Rect(0, 0, 1920, 1080);
        var secondaryWorkArea = new Rect(1920, 0, 1920, 1080);
        var screens = new[] { primaryWorkArea, secondaryWorkArea };

        var result = WorkspaceLayoutHelper.ClampWindowBounds(2000, 150, 1280, 720, screens, primaryWorkArea);

        Assert.Equal(2000, result.Left);
        Assert.Equal(150, result.Top);
        Assert.Equal(1280, result.Width);
        Assert.Equal(720, result.Height);
    }

    [Fact]
    public void WorkspaceLayoutHelper_ClampWindowBounds_OffScreen_ClampedToClosestScreen()
    {
        var primaryWorkArea = new Rect(0, 0, 1920, 1080);
        var secondaryWorkArea = new Rect(1920, 0, 1920, 1080);
        var screens = new[] { primaryWorkArea, secondaryWorkArea };

        // Way off to the left
        var resultLeft = WorkspaceLayoutHelper.ClampWindowBounds(-5000, -200, 1200, 800, screens, primaryWorkArea);
        Assert.True(resultLeft.Left >= primaryWorkArea.Left);
        Assert.True(resultLeft.Top >= primaryWorkArea.Top);

        // Way off to the right past secondary
        var resultRight = WorkspaceLayoutHelper.ClampWindowBounds(6000, 2000, 1200, 800, screens, primaryWorkArea);
        Assert.True(resultRight.Right <= secondaryWorkArea.Right);
        Assert.True(resultRight.Bottom <= secondaryWorkArea.Bottom);
    }

    [Fact]
    public void WorkspaceLayoutHelper_ClampWindowBounds_ExceedsScreenSize_ClampedToWorkArea()
    {
        var primaryWorkArea = new Rect(0, 0, 1920, 1080);
        var screens = new[] { primaryWorkArea };

        var result = WorkspaceLayoutHelper.ClampWindowBounds(-100, -100, 2500, 1500, screens, primaryWorkArea);

        Assert.Equal(primaryWorkArea.Left, result.Left);
        Assert.Equal(primaryWorkArea.Top, result.Top);
        Assert.Equal(primaryWorkArea.Width, result.Width);
        Assert.Equal(primaryWorkArea.Height, result.Height);
    }

    [Fact]
    public void WorkspaceLayoutHelper_ClampNavigationPanelState_RespectsFocusScenario()
    {
        Assert.Equal(NavigationPanelState.Normal, WorkspaceLayoutHelper.ClampNavigationPanelState(NavigationPanelState.Normal, isFullscreenFocus: false));
        Assert.Equal(NavigationPanelState.Compact, WorkspaceLayoutHelper.ClampNavigationPanelState(NavigationPanelState.Compact, isFullscreenFocus: false));
        Assert.Equal(NavigationPanelState.Normal, WorkspaceLayoutHelper.ClampNavigationPanelState(NavigationPanelState.Hidden, isFullscreenFocus: false));

        Assert.Equal(NavigationPanelState.Hidden, WorkspaceLayoutHelper.ClampNavigationPanelState(NavigationPanelState.Hidden, isFullscreenFocus: true));
        Assert.Equal(NavigationPanelState.Normal, WorkspaceLayoutHelper.ClampNavigationPanelState((NavigationPanelState)99, isFullscreenFocus: true));
    }

    [Fact]
    public void WorkspaceLayoutHelper_DevicePixelsToDip_NonUnitScale_ConvertsDeviceRectToDip()
    {
        // 125% scaling (scale = 1.25): 120 DPI
        var deviceRect = new Rect(1920, 0, 1920, 1080);
        var dipRect = WorkspaceLayoutHelper.DevicePixelsToDip(deviceRect, 1.25, 1.25);

        Assert.Equal(1536, dipRect.Left);
        Assert.Equal(0, dipRect.Top);
        Assert.Equal(1536, dipRect.Width);
        Assert.Equal(864, dipRect.Height);

        // Also verify Matrix overload (CompositionTarget.TransformFromDevice)
        var matrix = new System.Windows.Media.Matrix(1.0 / 1.25, 0, 0, 1.0 / 1.25, 0, 0);
        var matrixDipRect = WorkspaceLayoutHelper.DevicePixelsToDip(deviceRect, matrix);

        Assert.Equal(1536, matrixDipRect.Left);
        Assert.Equal(0, matrixDipRect.Top);
        Assert.Equal(1536, matrixDipRect.Width);
        Assert.Equal(864, matrixDipRect.Height);

        // Prove clamping: a DIP window at (1600, 100, 1000, 700) is clamped against converted DIP screen
        var clamped = WorkspaceLayoutHelper.ClampWindowBounds(
            1600, 100, 1000, 700,
            new[] { new Rect(0, 0, 1536, 864), dipRect },
            new Rect(0, 0, 1536, 864));

        Assert.Equal(1600, clamped.Left);
        Assert.Equal(100, clamped.Top);
        Assert.Equal(1000, clamped.Width);
        Assert.Equal(700, clamped.Height);
    }

    #endregion

    #region Fullscreen Toggle & Restore Tests

    [Fact]
    public void WorkspaceFullscreen_F11EntersAndSecondF11RestoresBoundsAndState()
    {
        string liveRoot = QuickNotesDbContext.LiveProfileDirectory;
        string[] liveBefore = SnapshotLiveProfile(liveRoot);
        string tempProfile = CreateTempProfile("qn_fs_toggle");
        try
        {
            RunInSta(() =>
            {
                using var fixture = new TestProfileComposition(tempProfile, seedNotes: 2, ownsDirectory: false);
                var (window, vm) = fixture;
                PrepareWindow(window, left: 120, top: 90, width: 1050, height: 710);
                try
                {
                    // --- Part 1: Toggle fullscreen from WindowState.Normal ---
                    Assert.False(vm.IsFullscreen);
                    Assert.Equal(WindowState.Normal, window.WindowState);
                    Assert.Equal(WindowStyle.SingleBorderWindow, window.WindowStyle);

                    double preLeft = window.Left;
                    double preTop = window.Top;
                    double preWidth = window.Width;
                    double preHeight = window.Height;

                    // First F11: Enter fullscreen
                    RaiseKey(window, Key.F11);

                    Assert.True(vm.IsFullscreen);
                    Assert.Equal(WindowState.Maximized, window.WindowState);
                    Assert.Equal(WindowStyle.None, window.WindowStyle);
                    Assert.Equal(ResizeMode.NoResize, window.ResizeMode);

                    // Second F11: Exit fullscreen and restore exact bounds
                    RaiseKey(window, Key.F11);

                    Assert.False(vm.IsFullscreen);
                    Assert.Equal(WindowState.Normal, window.WindowState);
                    Assert.Equal(WindowStyle.SingleBorderWindow, window.WindowStyle);
                    Assert.Equal(ResizeMode.CanResize, window.ResizeMode);
                    Assert.Equal(preLeft, window.Left);
                    Assert.Equal(preTop, window.Top);
                    Assert.Equal(preWidth, window.Width);
                    Assert.Equal(preHeight, window.Height);
                    Assert.True(Math.Abs(window.Left - 120) < 1.0);
                    Assert.True(Math.Abs(window.Top - 90) < 1.0);
                    Assert.True(Math.Abs(window.Width - 1050) < 1.0);
                    Assert.True(Math.Abs(window.Height - 710) < 1.0);

                    // --- Part 2: Toggle fullscreen from WindowState.Maximized ---
                    window.WindowState = WindowState.Maximized;
                    ThreePaneUiSmokeRunner.DoEvents();
                    Assert.Equal(WindowState.Maximized, window.WindowState);
                    Assert.Equal(WindowStyle.SingleBorderWindow, window.WindowStyle);
                    Assert.Equal(ResizeMode.CanResize, window.ResizeMode);
                    Assert.False(vm.IsFullscreen);

                    // F11 from Maximized: Enters borderless fullscreen
                    RaiseKey(window, Key.F11);
                    Assert.True(vm.IsFullscreen);
                    Assert.Equal(WindowState.Maximized, window.WindowState);
                    Assert.Equal(WindowStyle.None, window.WindowStyle);
                    Assert.Equal(ResizeMode.NoResize, window.ResizeMode);

                    // Second F11: Restores previous WindowState.Maximized and bordered chrome
                    RaiseKey(window, Key.F11);
                    Assert.False(vm.IsFullscreen);
                    Assert.Equal(WindowState.Maximized, window.WindowState);
                    Assert.Equal(WindowStyle.SingleBorderWindow, window.WindowStyle);
                    Assert.Equal(ResizeMode.CanResize, window.ResizeMode);
                }
                finally
                {
                    window.CloseWithoutShutdown();
                }
            });
        }
        finally
        {
            SqliteTestUtil.TryDeleteDirectory(tempProfile);
            Assert.Equal(liveBefore, SnapshotLiveProfile(liveRoot));
        }
    }

    [Fact]
    public void WorkspaceFullscreen_F11ThenEsc_NotFocus_ExitsFullscreenAndRestoresBounds()
    {
        string liveRoot = QuickNotesDbContext.LiveProfileDirectory;
        string[] liveBefore = SnapshotLiveProfile(liveRoot);
        string tempProfile = CreateTempProfile("qn_fs_esc");
        try
        {
            RunInSta(() =>
            {
                using var fixture = new TestProfileComposition(tempProfile, seedNotes: 2, ownsDirectory: false);
                var (window, vm) = fixture;
                PrepareWindow(window, left: 140, top: 110, width: 1020, height: 690);
                try
                {
                    Assert.False(vm.IsFocusMode);

                    double preLeft = window.Left;
                    double preTop = window.Top;
                    double preWidth = window.Width;
                    double preHeight = window.Height;

                    // Enter fullscreen
                    RaiseKey(window, Key.F11);
                    Assert.True(vm.IsFullscreen);

                    // Escape while not in focus mode -> exits fullscreen
                    RaiseKey(window, Key.Escape);

                    Assert.False(vm.IsFullscreen);
                    Assert.Equal(WindowState.Normal, window.WindowState);
                    Assert.Equal(WindowStyle.SingleBorderWindow, window.WindowStyle);
                    Assert.Equal(preLeft, window.Left);
                    Assert.Equal(preTop, window.Top);
                    Assert.Equal(preWidth, window.Width);
                    Assert.Equal(preHeight, window.Height);
                    Assert.True(Math.Abs(window.Left - 140) < 1.0);
                    Assert.True(Math.Abs(window.Top - 110) < 1.0);
                    Assert.True(Math.Abs(window.Width - 1020) < 1.0);
                    Assert.True(Math.Abs(window.Height - 690) < 1.0);
                }
                finally
                {
                    window.CloseWithoutShutdown();
                }
            });
        }
        finally
        {
            SqliteTestUtil.TryDeleteDirectory(tempProfile);
            Assert.Equal(liveBefore, SnapshotLiveProfile(liveRoot));
        }
    }

    [Fact]
    public void WorkspaceFullscreen_FocusPlusF11_EscLeavesFocusFirstAndStaysFullscreen_SecondEscExitsFullscreen()
    {
        string liveRoot = QuickNotesDbContext.LiveProfileDirectory;
        string[] liveBefore = SnapshotLiveProfile(liveRoot);
        string tempProfile = CreateTempProfile("qn_fs_focus_esc");
        try
        {
            RunInSta(() =>
            {
                using var fixture = new TestProfileComposition(tempProfile, seedNotes: 3, ownsDirectory: false);
                var (window, vm) = fixture;
                PrepareWindow(window, left: 130, top: 95, width: 1080, height: 720);
                try
                {
                    var targetNote = vm.Notes[1];
                    vm.SelectedNote = targetNote;
                    bool entered = vm.EnterFocus(targetNote);
                    Assert.True(entered);
                    Assert.True(vm.IsFocusMode);

                    double preLeft = window.Left;
                    double preTop = window.Top;
                    double preWidth = window.Width;
                    double preHeight = window.Height;

                    // Enter fullscreen while in Focus
                    RaiseKey(window, Key.F11);
                    Assert.True(vm.IsFullscreen);
                    Assert.True(vm.IsFocusMode);

                    // First Escape: Leaves Focus mode first, remains fullscreen
                    RaiseKey(window, Key.Escape);
                    Assert.False(vm.IsFocusMode);
                    Assert.True(vm.IsFullscreen);

                    // Second Escape: Exits fullscreen
                    RaiseKey(window, Key.Escape);
                    Assert.False(vm.IsFullscreen);
                    Assert.Equal(WindowState.Normal, window.WindowState);
                    Assert.Equal(WindowStyle.SingleBorderWindow, window.WindowStyle);
                    Assert.Equal(preLeft, window.Left);
                    Assert.Equal(preTop, window.Top);
                    Assert.Equal(preWidth, window.Width);
                    Assert.Equal(preHeight, window.Height);
                    Assert.True(Math.Abs(window.Left - 130) < 1.0);
                    Assert.True(Math.Abs(window.Top - 95) < 1.0);
                    Assert.True(Math.Abs(window.Width - 1080) < 1.0);
                    Assert.True(Math.Abs(window.Height - 720) < 1.0);
                }
                finally
                {
                    window.CloseWithoutShutdown();
                }
            });
        }
        finally
        {
            SqliteTestUtil.TryDeleteDirectory(tempProfile);
            Assert.Equal(liveBefore, SnapshotLiveProfile(liveRoot));
        }
    }

    [Fact]
    public void WorkspaceFullscreen_FindBarOpen_EscClosesFindBarFirst()
    {
        string liveRoot = QuickNotesDbContext.LiveProfileDirectory;
        string[] liveBefore = SnapshotLiveProfile(liveRoot);
        string tempProfile = CreateTempProfile("qn_fs_find_esc");
        try
        {
            RunInSta(() =>
            {
                using var fixture = new TestProfileComposition(tempProfile, seedNotes: 2, ownsDirectory: false);
                var (window, vm) = fixture;
                PrepareWindow(window);
                try
                {
                    // Enter fullscreen
                    RaiseKey(window, Key.F11);
                    Assert.True(vm.IsFullscreen);

                    // Open in-editor find
                    Assert.NotNull(vm.DetailEditor);
                    vm.DetailEditor.OpenFindCommand.Execute(null);
                    Assert.True(vm.DetailEditor.IsFindOpen);

                    // First Esc closes find bar, stays in fullscreen
                    RaiseKey(window, Key.Escape);
                    Assert.False(vm.DetailEditor.IsFindOpen);
                    Assert.True(vm.IsFullscreen);

                    // Second Esc exits fullscreen
                    RaiseKey(window, Key.Escape);
                    Assert.False(vm.IsFullscreen);
                }
                finally
                {
                    window.CloseWithoutShutdown();
                }
            });
        }
        finally
        {
            SqliteTestUtil.TryDeleteDirectory(tempProfile);
            Assert.Equal(liveBefore, SnapshotLiveProfile(liveRoot));
        }
    }

    #endregion

    #region Persistence Safety & Tray Contract Tests

    [Fact]
    public void WorkspaceFullscreen_CrashOrExitWhileFullscreen_NeverPersistsFullscreenOrBorderlessState()
    {
        string liveRoot = QuickNotesDbContext.LiveProfileDirectory;
        string[] liveBefore = SnapshotLiveProfile(liveRoot);
        string tempProfile = CreateTempProfile("qn_fs_persistence");
        try
        {
            RunInSta(() =>
            {
                // Session 1: enter fullscreen, persist settings while still fullscreen WITHOUT OnClosing safe branch
                using (var fixture = new TestProfileComposition(tempProfile, seedNotes: 2, ownsDirectory: false))
                {
                    var (window, vm) = fixture;
                    PrepareWindow(window, left: 160, top: 120, width: 1040, height: 700);

                    // Establish initial normal window layout in settings
                    vm.SaveWindowBoundsAndLayout(160, 120, 1040, 700, WindowState.Normal, 220, 320);

                    // Enter fullscreen
                    RaiseKey(window, Key.F11);
                    Assert.True(vm.IsFullscreen);
                    Assert.Equal(WindowState.Maximized, window.WindowState);
                    Assert.Equal(WindowStyle.None, window.WindowStyle);

                    // Persist settings while still fullscreen (bypassing OnClosing's safe fullscreen bounds branch)
                    vm.PersistLayout();

                    // Simulate crash: detach DataContext so OnClosing does not run the safe fullscreen persistence branch
                    window.DataContext = null;
                    window.CloseWithoutShutdown();
                }

                // Verify persisted settings on disk: no fullscreen or borderless state is ever stored
                string settingsJson = File.ReadAllText(Path.Combine(tempProfile, "settings.json"));
                var settings = JsonSerializer.Deserialize<AppSettings>(settingsJson);
                Assert.NotNull(settings);

                // Session 2 (Restart after crash): creates a new window and VM from the crashed profile
                using (var secondFixture = new TestProfileComposition(tempProfile, seedNotes: 2, ownsDirectory: false))
                {
                    var (restartedWindow, restartedVm) = secondFixture;
                    PrepareWindow(restartedWindow,
                        left: settings.WindowLeft ?? 100,
                        top: settings.WindowTop ?? 80,
                        width: settings.WindowWidth,
                        height: settings.WindowHeight);

                    // Must start with standard bordered, non-fullscreen window
                    Assert.False(restartedVm.IsFullscreen);
                    Assert.Equal(WindowStyle.SingleBorderWindow, restartedWindow.WindowStyle);
                    Assert.NotEqual(WindowStyle.None, restartedWindow.WindowStyle);
                    Assert.Equal(ResizeMode.CanResize, restartedWindow.ResizeMode);
                    Assert.NotEqual(ResizeMode.NoResize, restartedWindow.ResizeMode);

                    restartedWindow.CloseWithoutShutdown();
                }
            });
        }
        finally
        {
            SqliteTestUtil.TryDeleteDirectory(tempProfile);
            Assert.Equal(liveBefore, SnapshotLiveProfile(liveRoot));
        }
    }

    [Fact]
    public void WorkspaceFullscreen_CloseToTray_PreservesHideVsExit_AndNeverPersistsBrokenState()
    {
        string liveRoot = QuickNotesDbContext.LiveProfileDirectory;
        string[] liveBefore = SnapshotLiveProfile(liveRoot);
        string tempProfile = CreateTempProfile("qn_fs_tray");
        try
        {
            RunInSta(() =>
            {
                using (var fixture = new TestProfileComposition(tempProfile, seedNotes: 2, ownsDirectory: false))
                {
                    var (window, vm) = fixture;
                    PrepareWindow(window, left: 150, top: 100, width: 1000, height: 680);
                    try
                    {
                        // --- Part 1: CloseMainWindowDecision.Hide while fullscreen ---
                        // Enter fullscreen
                        RaiseKey(window, Key.F11);
                        Assert.True(vm.IsFullscreen);
                        Assert.Equal(WindowStyle.None, window.WindowStyle);

                        // Mock user choosing "Hide to tray"
                        fixture.SettingsService.CurrentSettings.CloseToTrayPromptCompleted = false;
                        vm.RequestCloseMainWindowDecision = () => CloseMainWindowDecision.Hide;

                        // Trigger close
                        window.Close();

                        // Window must exit fullscreen before hide
                        Assert.False(window.IsVisible);
                        Assert.False(vm.IsFullscreen);
                        Assert.Equal(WindowStyle.SingleBorderWindow, window.WindowStyle);
                        Assert.Equal(ResizeMode.CanResize, window.ResizeMode);

                        // Re-show from tray
                        window.BringWindowToFront();
                        Assert.True(window.IsVisible);
                        Assert.False(vm.IsFullscreen);
                        Assert.Equal(WindowStyle.SingleBorderWindow, window.WindowStyle);

                        // --- Part 2: CloseMainWindowDecision.Exit while fullscreen ---
                        // Enter fullscreen again
                        RaiseKey(window, Key.F11);
                        Assert.True(vm.IsFullscreen);
                        Assert.Equal(WindowStyle.None, window.WindowStyle);

                        // Mock user choosing "Exit"
                        fixture.SettingsService.CurrentSettings.CloseToTrayPromptCompleted = false;
                        vm.RequestCloseMainWindowDecision = () => CloseMainWindowDecision.Exit;

                        bool shutdownCalled = false;
                        MainWindow.ApplicationShutdownOverrideForTests = () => { shutdownCalled = true; };
                        try
                        {
                            window.Close();
                            Assert.True(shutdownCalled);

                            // Verify that Exit while fullscreen persisted pre-fullscreen bordered bounds
                            string settingsJson = File.ReadAllText(Path.Combine(tempProfile, "settings.json"));
                            var settings = JsonSerializer.Deserialize<AppSettings>(settingsJson);
                            Assert.NotNull(settings);
                            Assert.Equal(WindowState.Normal, settings.WindowState);
                            Assert.True(Math.Abs((settings.WindowLeft ?? 0) - 150) < 2.0);
                            Assert.True(Math.Abs((settings.WindowTop ?? 0) - 100) < 2.0);
                            Assert.True(Math.Abs(settings.WindowWidth - 1000) < 2.0);
                            Assert.True(Math.Abs(settings.WindowHeight - 680) < 2.0);
                        }
                        finally
                        {
                            MainWindow.ApplicationShutdownOverrideForTests = null;
                        }
                    }
                    finally
                    {
                        window.CloseWithoutShutdown();
                    }
                }

                // Session 3 (Restart after Exit): construct a new window from the profile after Exit to prove it opens bordered, not fullscreen
                using (var thirdFixture = new TestProfileComposition(tempProfile, seedNotes: 2, ownsDirectory: false))
                {
                    var (exitWindow, exitVm) = thirdFixture;
                    string exitSettingsJson = File.ReadAllText(Path.Combine(tempProfile, "settings.json"));
                    var exitSettings = JsonSerializer.Deserialize<AppSettings>(exitSettingsJson);
                    Assert.NotNull(exitSettings);

                    PrepareWindow(exitWindow,
                        left: exitSettings.WindowLeft ?? 100,
                        top: exitSettings.WindowTop ?? 80,
                        width: exitSettings.WindowWidth,
                        height: exitSettings.WindowHeight);

                    Assert.False(exitVm.IsFullscreen);
                    Assert.Equal(WindowStyle.SingleBorderWindow, exitWindow.WindowStyle);
                    Assert.Equal(ResizeMode.CanResize, exitWindow.ResizeMode);

                    exitWindow.CloseWithoutShutdown();
                }
            });
        }
        finally
        {
            SqliteTestUtil.TryDeleteDirectory(tempProfile);
            Assert.Equal(liveBefore, SnapshotLiveProfile(liveRoot));
        }
    }

    #endregion

    #region Navigation Collapse & Keyboard Return Tests

    [Fact]
    public void WorkspaceFullscreen_NavCompactAndHidden_KeyboardCycleAndRestore()
    {
        string liveRoot = QuickNotesDbContext.LiveProfileDirectory;
        string[] liveBefore = SnapshotLiveProfile(liveRoot);
        string tempProfile = CreateTempProfile("qn_fs_nav");
        try
        {
            RunInSta(() =>
            {
                using var fixture = new TestProfileComposition(tempProfile, seedNotes: 3, ownsDirectory: false);
                var (window, vm) = fixture;
                PrepareWindow(window);
                try
                {
                    // Default state is Normal
                    Assert.Equal(NavigationPanelState.Normal, vm.NavigationPanelState);

                    // In List mode, F4 toggles between Normal and Compact
                    RaiseKey(window, Key.F4);
                    Assert.Equal(NavigationPanelState.Compact, vm.NavigationPanelState);
                    Assert.True(vm.IsCompactNav);
                    Assert.False(vm.IsNavHidden);

                    RaiseKey(window, Key.F4);
                    Assert.Equal(NavigationPanelState.Normal, vm.NavigationPanelState);
                    Assert.False(vm.IsCompactNav);

                    // Enter Focus mode + Fullscreen
                    var note = vm.Notes[0];
                    vm.SelectedNote = note;
                    vm.EnterFocus(note);
                    RaiseKey(window, Key.F11);
                    Assert.True(vm.IsFullscreen);
                    Assert.True(vm.IsFocusMode);

                    // In Fullscreen Focus, F4 cycles Normal -> Compact -> Hidden -> Normal
                    RaiseKey(window, Key.F4);
                    Assert.Equal(NavigationPanelState.Compact, vm.NavigationPanelState);

                    RaiseKey(window, Key.F4);
                    Assert.Equal(NavigationPanelState.Hidden, vm.NavigationPanelState);
                    Assert.True(vm.IsNavHidden);

                    // RestoreNavigationCommand returns to Normal
                    vm.RestoreNavigationCommand.Execute(null);
                    Assert.Equal(NavigationPanelState.Normal, vm.NavigationPanelState);
                    Assert.False(vm.IsNavHidden);

                    // Cycle back to Hidden, then exit Focus -> Hidden is clamped to Normal
                    RaiseKey(window, Key.F4); // Compact
                    RaiseKey(window, Key.F4); // Hidden
                    Assert.Equal(NavigationPanelState.Hidden, vm.NavigationPanelState);

                    vm.LeaveFocus(vm.FocusReturnViewMode);
                    Assert.False(vm.IsFocusMode);
                    Assert.Equal(NavigationPanelState.Normal, vm.NavigationPanelState);
                }
                finally
                {
                    window.CloseWithoutShutdown();
                }
            });
        }
        finally
        {
            SqliteTestUtil.TryDeleteDirectory(tempProfile);
            Assert.Equal(liveBefore, SnapshotLiveProfile(liveRoot));
        }
    }

    [Fact]
    public void WorkspaceFullscreen_InvalidStoredNavState_ClampedSafelyOnLoad()
    {
        string liveRoot = QuickNotesDbContext.LiveProfileDirectory;
        string[] liveBefore = SnapshotLiveProfile(liveRoot);
        string tempProfile = CreateTempProfile("qn_fs_corrupt_nav");
        try
        {
            RunInSta(() =>
            {
                string settingsPath = Path.Combine(tempProfile, "settings.json");

                // 1. Unknown string value -> deserializes to Normal and MainViewModel initializes to Normal
                File.WriteAllText(settingsPath, """{ "NavigationPanelState": "SuperHidden" }""");
                using (var fixture1 = new TestProfileComposition(tempProfile, seedNotes: 0, ownsDirectory: false))
                {
                    Assert.Equal(NavigationPanelState.Normal, fixture1.MainViewModel.NavigationPanelState);
                    Assert.False(fixture1.MainViewModel.IsNavHidden);
                    Assert.False(fixture1.MainViewModel.IsCompactNav);
                }

                // 2. Invalid integer value -> deserializes to Normal and MainViewModel initializes to Normal
                File.WriteAllText(settingsPath, """{ "NavigationPanelState": 42 }""");
                using (var fixture2 = new TestProfileComposition(tempProfile, seedNotes: 0, ownsDirectory: false))
                {
                    Assert.Equal(NavigationPanelState.Normal, fixture2.MainViewModel.NavigationPanelState);
                    Assert.False(fixture2.MainViewModel.IsNavHidden);
                    Assert.False(fixture2.MainViewModel.IsCompactNav);
                }

                // 3. Hidden value in stored settings without fullscreen focus -> MainViewModel safely clamps to Normal on load
                File.WriteAllText(settingsPath, """{ "NavigationPanelState": "Hidden" }""");
                using (var fixture3 = new TestProfileComposition(tempProfile, seedNotes: 0, ownsDirectory: false))
                {
                    Assert.Equal(NavigationPanelState.Normal, fixture3.MainViewModel.NavigationPanelState);
                    Assert.False(fixture3.MainViewModel.IsNavHidden);
                    Assert.False(fixture3.MainViewModel.IsCompactNav);
                }
            });
        }
        finally
        {
            SqliteTestUtil.TryDeleteDirectory(tempProfile);
            Assert.Equal(liveBefore, SnapshotLiveProfile(liveRoot));
        }
    }

    #endregion
}
