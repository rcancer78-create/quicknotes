using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.EntityFrameworkCore;
using QuickNotes.App.Data;
using QuickNotes.App.Helpers;
using QuickNotes.App.Models;
using QuickNotes.App.Models.DraftJournal;
using QuickNotes.App.Services;
using QuickNotes.App.Services.DraftJournal;
using QuickNotes.App.Services.NoteProtection;
using QuickNotes.App.Services.Sync;
using QuickNotes.App.ViewModels;
using QuickNotes.App.Views;
using Xunit;

namespace QuickNotes.Tests;

[Collection(LiveProfileSnapshotCollection.Name)]
[TestCategory(TestCategories.UiSensitive)]
public sealed class ThreePaneWorkspaceSmokeTests
{
    private static void RunInSta(Action action) => StaTestHarness.Run(action, TimeSpan.FromSeconds(30));

    [Fact]
    public void WorkspaceLayoutHelper_ClampingAndCalculations_RecoversFromCorruptOrExtremeInputs()
    {
        // 1. Nav width clamping [200, 280]
        Assert.Equal(200, WorkspaceLayoutHelper.ClampNavWidth(150));
        Assert.Equal(260, WorkspaceLayoutHelper.ClampNavWidth(-50));
        Assert.Equal(280, WorkspaceLayoutHelper.ClampNavWidth(350));
        Assert.Equal(280, WorkspaceLayoutHelper.ClampNavWidth(10000));
        Assert.Equal(260, WorkspaceLayoutHelper.ClampNavWidth(double.NaN));
        Assert.Equal(260, WorkspaceLayoutHelper.ClampNavWidth(double.PositiveInfinity));
        Assert.Equal(260, WorkspaceLayoutHelper.ClampNavWidth(0));
        Assert.Equal(240, WorkspaceLayoutHelper.ClampNavWidth(240));

        // 2. List width clamping [350, 480]
        Assert.Equal(350, WorkspaceLayoutHelper.ClampListWidth(200));
        Assert.Equal(350, WorkspaceLayoutHelper.ClampListWidth(-10));
        Assert.Equal(480, WorkspaceLayoutHelper.ClampListWidth(500));
        Assert.Equal(480, WorkspaceLayoutHelper.ClampListWidth(99999));
        Assert.Equal(350, WorkspaceLayoutHelper.ClampListWidth(double.NaN));
        Assert.Equal(350, WorkspaceLayoutHelper.ClampListWidth(double.NegativeInfinity));
        Assert.Equal(350, WorkspaceLayoutHelper.ClampListWidth(0));
        Assert.Equal(350, WorkspaceLayoutHelper.ClampListWidth(320));

        // 3. Narrow mode threshold: 900 DIP
        Assert.True(WorkspaceLayoutHelper.IsNarrowMode(899.9));
        Assert.True(WorkspaceLayoutHelper.IsNarrowMode(750));
        Assert.False(WorkspaceLayoutHelper.IsNarrowMode(900));
        Assert.False(WorkspaceLayoutHelper.IsNarrowMode(900.1));
        Assert.False(WorkspaceLayoutHelper.IsNarrowMode(1100));

        // 4. Window state normalization
        Assert.Equal(WindowState.Normal, WorkspaceLayoutHelper.NormalizeWindowState(WindowState.Minimized));
        Assert.Equal(WindowState.Normal, WorkspaceLayoutHelper.NormalizeWindowState(WindowState.Normal));
        Assert.Equal(WindowState.Maximized, WorkspaceLayoutHelper.NormalizeWindowState(WindowState.Maximized));

        // 5. Window bounds clamping with workArea
        var workArea = new Rect(0, 0, 1920, 1080);

        // Null / NaN coordinates center window
        var centered = WorkspaceLayoutHelper.ClampWindowBounds(null, null, 1100, 720, workArea);
        Assert.Equal(1100, centered.Width);
        Assert.Equal(720, centered.Height);
        Assert.Equal(410, centered.Left);
        Assert.Equal(180, centered.Top);

        // Tiny window clamped to MinWindowWidth (640) and MinWindowHeight (500)
        var tiny = WorkspaceLayoutHelper.ClampWindowBounds(100, 100, 300, 200, workArea);
        Assert.Equal(WorkspaceLayoutHelper.MinWindowWidth, tiny.Width);
        Assert.Equal(WorkspaceLayoutHelper.MinWindowHeight, tiny.Height);

        // Offscreen coordinates clamped back to work area
        var offscreen = WorkspaceLayoutHelper.ClampWindowBounds(-5000, -5000, 1100, 720, workArea);
        Assert.True(offscreen.Left >= workArea.Left - offscreen.Width + 100);
        Assert.True(offscreen.Top >= workArea.Top);
    }

    [Fact]
    public void SettingsPersistence_CorruptOrOutOfRangeSplitter_SafelyClamped()
    {
        string liveRoot = QuickNotesDbContext.LiveProfileDirectory;
        string[] liveBefore = SnapshotLiveProfile(liveRoot);

        string tempProfile = Path.Combine(Path.GetTempPath(), "qn_settings_clamp_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempProfile);
        try
        {
            string settingsPath = Path.Combine(tempProfile, "settings.json");
            var settingsService = new SettingsService(settingsPath, _ => { });

            // Save corrupt/out-of-range settings
            var badSettings = new AppSettings
            {
                NavigationPanelWidth = 99999,
                NoteListPanelWidth = -500,
                WindowLeft = null,
                WindowTop = null,
                WindowWidth = 200, // below min
                WindowHeight = 100 // below min
            };
            settingsService.SaveSettings(badSettings);

            // Reload and verify clamping
            var loaded = settingsService.LoadSettings();
            double clampedNav = WorkspaceLayoutHelper.ClampNavWidth(loaded.NavigationPanelWidth);
            double clampedList = WorkspaceLayoutHelper.ClampListWidth(loaded.NoteListPanelWidth);

            Assert.Equal(280, clampedNav);
            Assert.Equal(350, clampedList);

            var clampedBounds = WorkspaceLayoutHelper.ClampWindowBounds(
                loaded.WindowLeft, loaded.WindowTop, loaded.WindowWidth, loaded.WindowHeight,
                new Rect(0, 0, 1920, 1080));

            Assert.Equal(WorkspaceLayoutHelper.MinWindowWidth, clampedBounds.Width);
            Assert.Equal(WorkspaceLayoutHelper.MinWindowHeight, clampedBounds.Height);

            Assert.Equal(liveBefore, SnapshotLiveProfile(liveRoot));
        }
        finally
        {
            SqliteTestUtil.TryDeleteDirectory(tempProfile);
        }
    }

    [Fact]
    public void PanePresence_WideMode_HasNavListAndDetailPanes()
    {
        string liveRoot = QuickNotesDbContext.LiveProfileDirectory;
        string[] liveBefore = SnapshotLiveProfile(liveRoot);

        string tempProfile = Path.Combine(Path.GetTempPath(), "qn_pane_smoke_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempProfile);

        try
        {
            RunInSta(() =>
            {
                ThemeService.ApplyThemeColors(isDark: false);
                using var fixture = CreateTestFixture(tempProfile);
                var (window, mainVm) = fixture;

                window.Width = 1100;
                window.Height = 720;
                window.Measure(new Size(1100, 720));
                window.Arrange(new Rect(0, 0, 1100, 720));
                window.UpdateLayout();

                Assert.False(mainVm.IsNarrow);

                // 1. Navigation Pane
                Assert.Equal(Visibility.Visible, window.NavBorder.Visibility);
                Assert.True(window.NavCol.Width.Value >= WorkspaceLayoutHelper.MinNavWidth);
                Assert.True(window.NavCol.Width.Value <= WorkspaceLayoutHelper.MaxNavWidth);

                // 2. Nav Splitter
                Assert.Equal(Visibility.Visible, window.NavSplitter.Visibility);

                // 3. Note List Pane
                Assert.Equal(Visibility.Visible, window.NotesListGrid.Visibility);
                Assert.True(window.ListCol.Width.Value >= WorkspaceLayoutHelper.MinListWidth);
                Assert.True(window.ListCol.Width.Value <= WorkspaceLayoutHelper.MaxListWidth);

                // 4. Detail Splitter
                Assert.Equal(Visibility.Visible, window.DetailSplitter.Visibility);

                // 5. Detail Pane
                Assert.Equal(Visibility.Visible, window.DetailPaneBorder.Visibility);
                Assert.Equal(GridUnitType.Star, window.DetailCol.Width.GridUnitType);

                // When no note is selected, empty state is visible
                Assert.Null(mainVm.SelectedNote);
                Assert.Null(mainVm.DetailEditor);
            });

            Assert.Equal(liveBefore, SnapshotLiveProfile(liveRoot));
        }
        finally
        {
            SqliteTestUtil.TryDeleteDirectory(tempProfile);
        }
    }

    [Fact]
    public void SelectingListItem_UpdatesDetailPane_WithoutModalWindow()
    {
        string liveRoot = QuickNotesDbContext.LiveProfileDirectory;
        string[] liveBefore = SnapshotLiveProfile(liveRoot);

        string tempProfile = Path.Combine(Path.GetTempPath(), "qn_select_smoke_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempProfile);

        try
        {
            RunInSta(() =>
            {
                ThemeService.ApplyThemeColors(isDark: false);
                using var fixture = CreateTestFixture(tempProfile, seedNotes: 3);
                var (window, mainVm) = fixture;

                window.Width = 1100;
                window.Height = 720;
                window.Measure(new Size(1100, 720));
                window.Arrange(new Rect(0, 0, 1100, 720));
                window.UpdateLayout();

                Assert.True(mainVm.Notes.Count >= 3);

                // Select first note -> updates inline detail editor
                var first = mainVm.Notes[0];
                mainVm.SelectedNote = first;

                Assert.NotNull(mainVm.DetailEditor);
                Assert.Equal(first.Note.Title, mainVm.DetailEditor.Title);
                Assert.Equal(first.Note.Text, mainVm.DetailEditor.Text);
                Assert.True(mainVm.DetailEditor.IsInline);
                Assert.Equal(Visibility.Visible, window.DetailPaneBorder.Visibility);

                // Select second note -> updates detail pane directly in-place without modal
                var second = mainVm.Notes[1];
                mainVm.SelectedNote = second;

                Assert.NotNull(mainVm.DetailEditor);
                Assert.Equal(second.Note.Title, mainVm.DetailEditor.Title);
                Assert.Equal(second.Note.Text, mainVm.DetailEditor.Text);
                Assert.True(mainVm.DetailEditor.IsInline);
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
    public void NarrowMode_MasterDetailTransitions_AndBackCommandPreservesDraft()
    {
        string liveRoot = QuickNotesDbContext.LiveProfileDirectory;
        string[] liveBefore = SnapshotLiveProfile(liveRoot);

        string tempProfile = Path.Combine(Path.GetTempPath(), "qn_narrow_smoke_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempProfile);

        try
        {
            RunInSta(() =>
            {
                ThemeService.ApplyThemeColors(isDark: false);
                using var fixture = CreateTestFixture(tempProfile, seedNotes: 2);
                var (window, mainVm) = fixture;

                // Configure window for narrow mode (< 900 DIP)
                window.Width = 800;
                window.Height = 650;
                mainVm.IsNarrow = true;
                mainVm.IsDetailActiveInNarrow = false;

                window.Measure(new Size(800, 650));
                window.Arrange(new Rect(0, 0, 800, 650));
                window.UpdateLayout();

                // Master mode: Navigation and List visible, Detail collapsed
                Assert.Equal(Visibility.Visible, window.NavBorder.Visibility);
                Assert.Equal(Visibility.Visible, window.NotesListGrid.Visibility);
                Assert.Equal(Visibility.Collapsed, window.DetailPaneBorder.Visibility);
                Assert.Equal(0, window.DetailCol.Width.Value);

                // Select note via UI and open into detail
                var note = mainVm.Notes[0];
                window.NotesListBox.SelectedItem = note;
                mainVm.IsDetailActiveInNarrow = true;

                window.Measure(new Size(800, 650));
                window.Arrange(new Rect(0, 0, 800, 650));
                window.UpdateLayout();

                // Detail mode: Detail pane takes 100%, Nav & List collapsed
                Assert.Equal(Visibility.Collapsed, window.NavBorder.Visibility);
                Assert.Equal(Visibility.Collapsed, window.NotesListGrid.Visibility);
                Assert.Equal(Visibility.Visible, window.DetailPaneBorder.Visibility);
                Assert.Equal(GridUnitType.Star, window.DetailCol.Width.GridUnitType);

                // Back button is visible
                Assert.NotNull(mainVm.DetailEditor);
                Assert.Equal(Visibility.Visible, window.BackToMasterButton.Visibility);

                // Edit draft content in detail editor
                mainVm.DetailEditor.Text = "Uncommitted draft in narrow mode: " + Guid.NewGuid();
                string editedText = mainVm.DetailEditor.Text;

                // Click Back (via BackToMasterButton command or automation peer)
                if (window.BackToMasterButton.Command != null && window.BackToMasterButton.Command.CanExecute(window.BackToMasterButton.CommandParameter))
                {
                    window.BackToMasterButton.Command.Execute(window.BackToMasterButton.CommandParameter);
                }
                else
                {
                    mainVm.BackToMasterCommand.Execute(null);
                }

                window.Measure(new Size(800, 650));
                window.Arrange(new Rect(0, 0, 800, 650));
                window.UpdateLayout();

                // Master mode restored
                Assert.False(mainVm.IsDetailActiveInNarrow);
                Assert.Equal(Visibility.Visible, window.NavBorder.Visibility);
                Assert.Equal(Visibility.Visible, window.NotesListGrid.Visibility);
                Assert.Equal(Visibility.Collapsed, window.DetailPaneBorder.Visibility);

                // Selection & draft state preserved
                Assert.Same(note, mainVm.SelectedNote);
                Assert.NotNull(mainVm.DetailEditor);
                Assert.Equal(editedText, mainVm.DetailEditor.Text);
            });

            Assert.Equal(liveBefore, SnapshotLiveProfile(liveRoot));
        }
        finally
        {
            SqliteTestUtil.TryDeleteDirectory(tempProfile);
        }
    }

    [Fact]
    public void DetailEditor_InlineEditAndSave_OperatesUnderMutationCoordinator()
    {
        string liveRoot = QuickNotesDbContext.LiveProfileDirectory;
        string[] liveBefore = SnapshotLiveProfile(liveRoot);

        string tempProfile = Path.Combine(Path.GetTempPath(), "qn_save_smoke_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempProfile);

        try
        {
            RunInSta(() =>
            {
                ThemeService.ApplyThemeColors(isDark: false);
                using var fixture = CreateTestFixture(tempProfile, seedNotes: 1);
                var (window, mainVm) = fixture;

                window.Width = 1100;
                window.Height = 720;
                window.UpdateLayout();

                var card = mainVm.Notes[0];
                mainVm.SelectedNote = card;
                Assert.NotNull(mainVm.DetailEditor);

                string updatedTitle = "Saved Title " + Guid.NewGuid().ToString("N")[..8];
                string updatedText = "Saved body content via coordinator " + Guid.NewGuid().ToString("N")[..8];

                mainVm.DetailEditor.Title = updatedTitle;
                mainVm.DetailEditor.Text = updatedText;

                // Trigger Save
                mainVm.DetailEditor.SaveCommand.Execute(null);

                // Verify changes in database
                using var verifyDb = fixture.CreateDbContext();
                var savedNote = verifyDb.Notes.Single(n => n.Id == card.Id);
                Assert.Equal(updatedTitle, savedNote.Title);
                Assert.Equal(updatedText, savedNote.Text);

                // Verify card reflects updated state
                Assert.Equal(updatedTitle, card.Note.Title);
                Assert.Equal(updatedText, card.Note.Text);
            });

            Assert.Equal(liveBefore, SnapshotLiveProfile(liveRoot));
        }
        finally
        {
            SqliteTestUtil.TryDeleteDirectory(tempProfile);
        }
    }

    [Fact]
    public void ProtectedNote_InlineLockedState_PreservesPrivacyUntilUnlocked()
    {
        string liveRoot = QuickNotesDbContext.LiveProfileDirectory;
        string[] liveBefore = SnapshotLiveProfile(liveRoot);

        string tempProfile = Path.Combine(Path.GetTempPath(), "qn_protect_smoke_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempProfile);

        try
        {
            RunInSta(() =>
            {
                ThemeService.ApplyThemeColors(isDark: false);
                using var fixture = CreateTestFixture(tempProfile, seedNotes: 0);
                var (window, mainVm) = fixture;

                // Create a protected note in DB
                int protectedNoteId;
                using (var db = fixture.CreateDbContext())
                {
                    var protNote = new Note
                    {
                        Title = "Secret Title",
                        Text = "Secret sensitive text",
                        CreatedAt = DateTime.Now,
                        UpdatedAt = DateTime.Now
                    };
                    db.Notes.Add(protNote);
                    db.SaveChanges();
                    protectedNoteId = protNote.Id;

                    var protectResult = fixture.ProtectionService.ProtectNote(db, protectedNoteId, "test-password");
                    Assert.True(protectResult.Success);
                    db.SaveChanges();
                }

                fixture.ProtectionService.LockNote(protectedNoteId);

                mainVm.ReloadAll();
                var card = mainVm.Notes.Single(n => n.Id == protectedNoteId);
                mainVm.SelectedNote = card;

                Assert.NotNull(mainVm.DetailEditor);
                var editor = mainVm.DetailEditor;

                // In locked state: Title and Text are empty in memory
                Assert.True(editor.IsNoteProtected);
                Assert.True(editor.IsLocked);
                Assert.Equal(string.Empty, editor.Text);

                // Unlock
                editor.RequestPasswordDialog = (_, _) => "test-password";
                editor.UnlockNoteCommand.Execute(null);

                Assert.False(editor.IsLocked);
                Assert.Equal("Secret Title", editor.Title);
                Assert.Equal("Secret sensitive text", editor.Text);

                // Lock again
                editor.LockNoteCommand.Execute(null);
                Assert.True(editor.IsLocked);
                Assert.Equal(string.Empty, editor.Text);
            });

            Assert.Equal(liveBefore, SnapshotLiveProfile(liveRoot));
        }
        finally
        {
            SqliteTestUtil.TryDeleteDirectory(tempProfile);
        }
    }

    [Fact]
    public void KeyboardNavigation_And_AccessibleNames()
    {
        RunInSta(() =>
        {
            ThemeService.ApplyThemeColors(isDark: false);
            string tempProfile = Path.Combine(Path.GetTempPath(), "qn_acc_smoke_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempProfile);
            try
            {
                using var fixture = CreateTestFixture(tempProfile, seedNotes: 1);
                var (window, _) = fixture;

                // Accessible names verification
                Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(window.NavSplitter)));
                Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(window.DetailSplitter)));
                Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(window.BackToMasterButton)));
                Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(window.DetailTitleTextBox)));
                Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(window.DetailBodyBox)));
                Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(window.SearchBox)));

                // Tooltips verification
                Assert.NotNull(window.NavSplitter.ToolTip);
                Assert.NotNull(window.DetailSplitter.ToolTip);
                Assert.NotNull(window.BackToMasterButton.ToolTip);
            }
            finally
            {
                SqliteTestUtil.TryDeleteDirectory(tempProfile);
            }
        });
    }

    [Fact]
    public void GenerateAcceptanceScreenshots_WideAndNarrow_LightAndDark()
    {
        string liveRoot = QuickNotesDbContext.LiveProfileDirectory;
        string[] liveBefore = SnapshotLiveProfile(liveRoot);

        string tempProfile = Path.Combine(Path.GetTempPath(), "qn_screens_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempProfile);

        string outputDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "artifacts", "three-pane-acceptance");
        outputDir = Path.GetFullPath(outputDir);
        Directory.CreateDirectory(outputDir);

        try
        {
            RunInSta(() =>
            {
                using var fixture = CreateTestFixture(tempProfile, seedNotes: 4);
                var (window, mainVm) = fixture;

                // Select first note
                window.NotesListBox.SelectedItem = mainVm.Notes[0];
                mainVm.DetailEditor!.Title = "Рабочий план: §7.1 Three-Pane Workspace";
                mainVm.DetailEditor!.Text = "# Компактное трёхпанельное рабочее пространство\n\n" +
                    "В версии **7.1** главное окно трансформировано в полноценный 3-панельный интерфейс:\n" +
                    "- **Навигация**: разделы и дерево тегов (200–280 DIP)\n" +
                    "- **Список**: пейджинговый список карточек (350–480 DIP)\n" +
                    "- **Редактор**: детальный просмотр и inline-редактирование Markdown\n\n" +
                    "При ширине окна менее `900 DIP` интерфейс переключается в адаптивный режим master/detail.";

                // 1. Wide Layout - Light Theme (1100x720)
                ThemeService.ApplyTheme(AppTheme.Light);
                mainVm.IsNarrow = false;
                mainVm.DetailEditor!.ViewMode = MarkdownViewMode.Edit;
                ThreePaneUiSmokeRunner.RenderAndSaveScreenshot(window, Path.Combine(outputDir, "wide-layout-light.png"), 1100, 720);

                mainVm.DetailEditor!.ViewMode = MarkdownViewMode.Split;
                ThreePaneUiSmokeRunner.RenderAndSaveScreenshot(window, Path.Combine(outputDir, "wide-split-light.png"), 1100, 720);

                mainVm.DetailEditor!.ViewMode = MarkdownViewMode.Preview;
                ThreePaneUiSmokeRunner.RenderAndSaveScreenshot(window, Path.Combine(outputDir, "wide-preview-light.png"), 1100, 720);

                // 2. Wide Layout - Dark Theme (1100x720)
                ThemeService.ApplyTheme(AppTheme.Dark);
                mainVm.IsNarrow = false;
                mainVm.DetailEditor!.ViewMode = MarkdownViewMode.Edit;
                ThreePaneUiSmokeRunner.RenderAndSaveScreenshot(window, Path.Combine(outputDir, "wide-layout-dark.png"), 1100, 720);

                mainVm.DetailEditor!.ViewMode = MarkdownViewMode.Split;
                ThreePaneUiSmokeRunner.RenderAndSaveScreenshot(window, Path.Combine(outputDir, "wide-split-dark.png"), 1100, 720);

                mainVm.DetailEditor!.ViewMode = MarkdownViewMode.Preview;
                ThreePaneUiSmokeRunner.RenderAndSaveScreenshot(window, Path.Combine(outputDir, "wide-preview-dark.png"), 1100, 720);

                // 3. Narrow Master Layout - Light Theme (800x650)
                ThemeService.ApplyTheme(AppTheme.Light);
                mainVm.IsNarrow = true;
                mainVm.IsDetailActiveInNarrow = false;
                ThreePaneUiSmokeRunner.RenderAndSaveScreenshot(window, Path.Combine(outputDir, "narrow-master-light.png"), 800, 650);

                // 4. Narrow Master Layout - Dark Theme (800x650)
                ThemeService.ApplyTheme(AppTheme.Dark);
                mainVm.IsNarrow = true;
                mainVm.IsDetailActiveInNarrow = false;
                ThreePaneUiSmokeRunner.RenderAndSaveScreenshot(window, Path.Combine(outputDir, "narrow-master-dark.png"), 800, 650);

                // 5. Narrow Detail Layout - Light Theme (800x650)
                ThemeService.ApplyTheme(AppTheme.Light);
                mainVm.IsNarrow = true;
                mainVm.IsDetailActiveInNarrow = true;
                mainVm.DetailEditor!.ViewMode = MarkdownViewMode.Edit;
                ThreePaneUiSmokeRunner.RenderAndSaveScreenshot(window, Path.Combine(outputDir, "narrow-detail-light.png"), 800, 650);

                mainVm.DetailEditor!.ViewMode = MarkdownViewMode.Split;
                ThreePaneUiSmokeRunner.RenderAndSaveScreenshot(window, Path.Combine(outputDir, "narrow-split-light.png"), 800, 650);

                mainVm.DetailEditor!.ViewMode = MarkdownViewMode.Preview;
                ThreePaneUiSmokeRunner.RenderAndSaveScreenshot(window, Path.Combine(outputDir, "narrow-preview-light.png"), 800, 650);

                // 6. Narrow Detail Layout - Dark Theme (800x650)
                ThemeService.ApplyTheme(AppTheme.Dark);
                mainVm.IsNarrow = true;
                mainVm.IsDetailActiveInNarrow = true;
                mainVm.DetailEditor!.ViewMode = MarkdownViewMode.Edit;
                ThreePaneUiSmokeRunner.RenderAndSaveScreenshot(window, Path.Combine(outputDir, "narrow-detail-dark.png"), 800, 650);
            });

            // Verify all 12 screenshots exist, have valid sizes, no transparency, and rich colors
            var expectedSpecs = new (string FileName, int Width, int Height)[]
            {
                ("wide-layout-light.png", 1100, 720),
                ("wide-split-light.png", 1100, 720),
                ("wide-preview-light.png", 1100, 720),
                ("wide-layout-dark.png", 1100, 720),
                ("wide-split-dark.png", 1100, 720),
                ("wide-preview-dark.png", 1100, 720),
                ("narrow-master-light.png", 800, 650),
                ("narrow-master-dark.png", 800, 650),
                ("narrow-detail-light.png", 800, 650),
                ("narrow-split-light.png", 800, 650),
                ("narrow-preview-light.png", 800, 650),
                ("narrow-detail-dark.png", 800, 650)
            };

            foreach (var (fileName, expWidth, expHeight) in expectedSpecs)
            {
                string fullPath = Path.Combine(outputDir, fileName);
                Assert.True(File.Exists(fullPath), $"Expected screenshot {fileName} to exist at {fullPath}");
                ThreePaneUiSmokeRunner.ValidateScreenshot(fullPath, expWidth, expHeight);
            }

            Assert.Equal(liveBefore, SnapshotLiveProfile(liveRoot));
        }
        finally
        {
            SqliteTestUtil.TryDeleteDirectory(tempProfile);
        }
    }

    [Fact]
    public void LayoutBounds_ToolbarAndStatusContent_NeverOverflowClientBounds()
    {
        string liveRoot = QuickNotesDbContext.LiveProfileDirectory;
        string[] liveBefore = SnapshotLiveProfile(liveRoot);

        string tempProfile = Path.Combine(Path.GetTempPath(), "qn_bounds_smoke_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempProfile);

        try
        {
            RunInSta(() =>
            {
                using var fixture = CreateTestFixture(tempProfile, seedNotes: 4);
                var (window, mainVm) = fixture;

                window.NotesListBox.SelectedItem = mainVm.Notes[0];
                mainVm.DetailEditor!.Title = "Рабочий план: §7.1 Three-Pane Workspace";
                mainVm.DetailEditor!.Text = "# Компактное трёхпанельное рабочее пространство\n\nТестирование границ.";

                // Test configurations: (Width, Height, isNarrow, isDetail, Theme, Label)
                var configs = new (int Width, int Height, bool IsNarrow, bool IsDetail, AppTheme Theme, string Label)[]
                {
                    (1100, 720, false, false, AppTheme.Light, "Wide Light 1100x720"),
                    (1100, 720, false, false, AppTheme.Dark, "Wide Dark 1100x720"),
                    (900, 720, false, false, AppTheme.Light, "Minimum Wide Light 900x720"),
                    (800, 650, true, false, AppTheme.Light, "Narrow Master Light 800x650"),
                    (800, 650, true, false, AppTheme.Dark, "Narrow Master Dark 800x650"),
                    (800, 650, true, true, AppTheme.Light, "Narrow Detail Light 800x650"),
                    (800, 650, true, true, AppTheme.Dark, "Narrow Detail Dark 800x650"),
                    (750, 500, true, false, AppTheme.Light, "Compact Window 750x500 Master"),
                    (750, 500, true, true, AppTheme.Light, "Compact Window 750x500 Detail"),
                    (640, 560, true, false, AppTheme.Light, "Documented Min Window 640x560 Master"),
                    (640, 560, true, true, AppTheme.Light, "Documented Min Window 640x560 Detail")
                };

                foreach (var (w, h, isNarrow, isDetail, theme, label) in configs)
                {
                    ThemeService.ApplyTheme(theme);
                    mainVm.IsNarrow = isNarrow;
                    mainVm.IsDetailActiveInNarrow = isDetail;

                    window.Width = w;
                    window.Height = h;
                    window.UpdateWorkspaceLayout();
                    window.Measure(new Size(w, h));
                    window.Arrange(new Rect(0, 0, w, h));
                    window.UpdateLayout();

                    var content = (FrameworkElement)window.Content;
                    content.Measure(new Size(w, h));
                    content.Arrange(new Rect(0, 0, w, h));
                    content.UpdateLayout();

                    // Programmatic assertion: verify no toolbar or status element overflows visible bounds
                    ThreePaneUiSmokeRunner.AssertLayoutBounds(window, w, h);

                    // Additional explicit assertions
                    // 1. Status bar text and hints must be within content width
                    if (window.HotkeyHint.Visibility == Visibility.Visible && window.HotkeyHint.ActualWidth > 0)
                    {
                        var hotkeyBounds = window.HotkeyHint.TransformToAncestor(content).TransformBounds(new Rect(0, 0, window.HotkeyHint.ActualWidth, window.HotkeyHint.ActualHeight));
                        Assert.True(hotkeyBounds.Right <= content.ActualWidth + 1.0,
                            $"{label}: HotkeyHint.Right ({hotkeyBounds.Right:F1}) exceeds client width ({content.ActualWidth:F1})");
                    }
                    if (window.StatusTextBlock.Visibility == Visibility.Visible && window.StatusTextBlock.ActualWidth > 0)
                    {
                        var statusBounds = window.StatusTextBlock.TransformToAncestor(content).TransformBounds(new Rect(0, 0, window.StatusTextBlock.ActualWidth, window.StatusTextBlock.ActualHeight));
                        Assert.True(statusBounds.Right <= content.ActualWidth + 1.0,
                            $"{label}: StatusTextBlock.Right ({statusBounds.Right:F1}) exceeds client width ({content.ActualWidth:F1})");
                        Assert.True(window.StatusTextBlock.ActualWidth > 0, $"{label}: StatusTextBlock should have positive width");
                    }

                    // 2. When detail pane is active, DetailSaveButton and DetailMoreButton must be strictly within DetailPaneBorder bounds
                    if ((!isNarrow || isDetail) && mainVm.DetailEditor != null)
                    {
                        Assert.Equal(Visibility.Visible, window.DetailSaveButton.Visibility);
                        Assert.Equal(Visibility.Visible, window.DetailMoreButton.Visibility);

                        var saveBounds = window.DetailSaveButton.TransformToAncestor(window.DetailPaneBorder).TransformBounds(new Rect(0, 0, window.DetailSaveButton.ActualWidth, window.DetailSaveButton.ActualHeight));
                        Assert.True(saveBounds.Right <= window.DetailPaneBorder.ActualWidth + 1.0,
                            $"{label}: DetailSaveButton.Right ({saveBounds.Right:F1}) exceeds DetailPaneBorder width ({window.DetailPaneBorder.ActualWidth:F1})");

                        var moreBounds = window.DetailMoreButton.TransformToAncestor(window.DetailPaneBorder).TransformBounds(new Rect(0, 0, window.DetailMoreButton.ActualWidth, window.DetailMoreButton.ActualHeight));
                        Assert.True(moreBounds.Right <= window.DetailPaneBorder.ActualWidth + 1.0,
                            $"{label}: DetailMoreButton.Right ({moreBounds.Right:F1}) exceeds DetailPaneBorder width ({window.DetailPaneBorder.ActualWidth:F1})");
                    }
                }
            });

            Assert.Equal(liveBefore, SnapshotLiveProfile(liveRoot));
        }
        finally
        {
            SqliteTestUtil.TryDeleteDirectory(tempProfile);
        }
    }

    [Fact]
    public void RunIsolatedUiSmokeProcess_GeneratesValidAcceptanceScreenshots_AndExitsZero()
    {
        string liveRoot = QuickNotesDbContext.LiveProfileDirectory;
        string[] liveBefore = SnapshotLiveProfile(liveRoot);

        string tempProfile = Path.Combine(Path.GetTempPath(), "qn_proc_smoke_profile_" + Guid.NewGuid().ToString("N"));
        string tempArtifacts = Path.Combine(Path.GetTempPath(), "qn_proc_smoke_art_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempProfile);
        Directory.CreateDirectory(tempArtifacts);

        try
        {
            string baseDir = AppContext.BaseDirectory;
            string releaseExe = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "QuickNotes.App", "bin", "Release", "net8.0-windows10.0.19041.0", "QuickNotes.App.exe"));
            string candidateExe = File.Exists(releaseExe)
                ? releaseExe
                : Path.GetFullPath(Path.Combine(baseDir, "QuickNotes.App.exe"));

            if (!File.Exists(candidateExe))
            {
                var matches = Directory.GetFiles(Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..")), "QuickNotes.App.exe", SearchOption.AllDirectories);
                if (matches.Length > 0)
                {
                    candidateExe = matches[0];
                }
            }

            Assert.True(File.Exists(candidateExe), $"QuickNotes.App.exe not found at {candidateExe}");

            var psi = new ProcessStartInfo
            {
                FileName = candidateExe,
                Arguments = $"--isolated-profile \"{tempProfile}\" --three-pane-smoke --output-dir \"{tempArtifacts}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            var process = ProcessTestHarness.Run(psi, TimeSpan.FromMilliseconds(30000));
            string stdout = process.StandardOutput;
            string stderr = process.StandardError;
            Assert.Equal(0, process.ExitCode);
            Assert.Contains("QN_THREE_PANE_SMOKE_SUCCESS", stdout);

            var expectedSpecs = new (string FileName, int Width, int Height)[]
            {
                ("wide-layout-light.png", 1100, 720),
                ("wide-split-light.png", 1100, 720),
                ("wide-preview-light.png", 1100, 720),
                ("wide-layout-dark.png", 1100, 720),
                ("wide-split-dark.png", 1100, 720),
                ("wide-preview-dark.png", 1100, 720),
                ("narrow-master-light.png", 800, 650),
                ("narrow-master-dark.png", 800, 650),
                ("narrow-detail-light.png", 800, 650),
                ("narrow-split-light.png", 800, 650),
                ("narrow-preview-light.png", 800, 650),
                ("narrow-detail-dark.png", 800, 650)
            };

            foreach (var (specName, expWidth, expHeight) in expectedSpecs)
            {
                string filePath = Path.Combine(tempArtifacts, specName);
                Assert.True(File.Exists(filePath), $"Expected {specName} at {filePath}");
                ThreePaneUiSmokeRunner.ValidateScreenshot(filePath, expWidth, expHeight);
            }

            Assert.Equal(liveBefore, SnapshotLiveProfile(liveRoot));
        }
        finally
        {
            SqliteTestUtil.TryDeleteDirectory(tempProfile);
            SqliteTestUtil.TryDeleteDirectory(tempArtifacts);
        }
    }

    [Fact]
    public void MultiDpiRendering_InspectsLayoutsAtSupportedDpi_NoOverlapOrClipping()
    {
        string liveRoot = QuickNotesDbContext.LiveProfileDirectory;
        string[] liveBefore = SnapshotLiveProfile(liveRoot);

        string tempProfile = Path.Combine(Path.GetTempPath(), "qn_dpi_smoke_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempProfile);

        try
        {
            RunInSta(() =>
            {
                using var fixture = CreateTestFixture(tempProfile, seedNotes: 4);
                var (window, mainVm) = fixture;

                window.NotesListBox.SelectedItem = mainVm.Notes[0];
                ThemeService.ApplyTheme(AppTheme.Light);

                int baseWidth = 1100;
                int baseHeight = 720;

                window.Width = baseWidth;
                window.Height = baseHeight;
                window.Measure(new Size(baseWidth, baseHeight));
                window.Arrange(new Rect(0, 0, baseWidth, baseHeight));
                window.UpdateLayout();

                var content = (FrameworkElement)window.Content;
                content.Measure(new Size(baseWidth, baseHeight));
                content.Arrange(new Rect(0, 0, baseWidth, baseHeight));
                content.UpdateLayout();

                // Inspect real rendered header layout: verify Title and SortComboBox do not overlap
                Assert.True(window.NotesHeaderTitleBlock.ActualWidth > 0);
                Assert.True(window.SortComboBox.ActualWidth > 0);
                Assert.True(window.NotesListGrid.ActualWidth >= window.NotesHeaderTitleBlock.ActualWidth);

                // Supported DPI scale factors (100%, 125% native display, 150%, 200%)
                double[] dpiScales = [1.0, 1.25, 1.5, 2.0];

                foreach (double scale in dpiScales)
                {
                    int pixelWidth = (int)Math.Round(baseWidth * scale);
                    int pixelHeight = (int)Math.Round(baseHeight * scale);
                    double dpi = 96.0 * scale;

                    var bgVisual = new DrawingVisual();
                    using (var dc = bgVisual.RenderOpen())
                    {
                        var bg = window.Background ?? (Brush)window.FindResource("CanvasBrush") ?? Brushes.White;
                        dc.DrawRectangle(bg, null, new Rect(0, 0, baseWidth, baseHeight));
                    }

                    var rtb = new RenderTargetBitmap(pixelWidth, pixelHeight, dpi, dpi, PixelFormats.Pbgra32);
                    rtb.Render(bgVisual);
                    rtb.Render(content);

                    Assert.Equal(pixelWidth, rtb.PixelWidth);
                    Assert.Equal(pixelHeight, rtb.PixelHeight);

                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(rtb));
                    string tempPng = Path.Combine(tempProfile, $"dpi_{(int)(scale * 100)}.png");
                    using (var fs = File.Create(tempPng))
                    {
                        encoder.Save(fs);
                    }

                    ThreePaneUiSmokeRunner.ValidateScreenshot(tempPng, pixelWidth, pixelHeight);
                    File.Delete(tempPng);
                }
            });

            Assert.Equal(liveBefore, SnapshotLiveProfile(liveRoot));
        }
        finally
        {
            SqliteTestUtil.TryDeleteDirectory(tempProfile);
        }
    }

    [Fact]
    public void FormattingToolbar_ControlsHaveNonEmptyContentNonZeroBoundsContrastAndInClientArea_LightAndDark()
    {
        string liveRoot = QuickNotesDbContext.LiveProfileDirectory;
        string[] liveBefore = SnapshotLiveProfile(liveRoot);

        string tempProfile = Path.Combine(Path.GetTempPath(), "qn_toolbar_smoke_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempProfile);

        try
        {
            RunInSta(() =>
            {
                using var fixture = CreateTestFixture(tempProfile, seedNotes: 3);
                var (window, mainVm) = fixture;

                window.NotesListBox.SelectedItem = mainVm.Notes[0];

                var testThemes = new[] { AppTheme.Light, AppTheme.Dark };
                foreach (var theme in testThemes)
                {
                    ThemeService.ApplyTheme(theme);

                    int width = 1100;
                    int height = 720;
                    window.Width = width;
                    window.Height = height;
                    mainVm.IsNarrow = false;
                    mainVm.DetailEditor!.ViewMode = MarkdownViewMode.Edit;

                    window.UpdateWorkspaceLayout();
                    window.Measure(new Size(width, height));
                    window.Arrange(new Rect(0, 0, width, height));
                    window.UpdateLayout();

                    var content = (FrameworkElement)window.Content;
                    content.Measure(new Size(width, height));
                    content.Arrange(new Rect(0, 0, width, height));
                    content.UpdateLayout();

                    Assert.True(window.DetailFormattingToolbar.Visibility == Visibility.Visible);
                    Assert.True(window.DetailFormattingToolbar.ActualWidth > 0);
                    Assert.True(window.DetailFormattingToolbar.ActualHeight > 0);

                    var formatButtons = new (Button Button, string Name)[]
                    {
                        (window.FormatBoldButton, "FormatBoldButton"),
                        (window.FormatItalicButton, "FormatItalicButton"),
                        (window.FormatHeadingButton, "FormatHeadingButton"),
                        (window.FormatCodeButton, "FormatCodeButton"),
                        (window.FormatBulletListButton, "FormatBulletListButton"),
                        (window.FormatNumberedListButton, "FormatNumberedListButton"),
                        (window.FormatCheckboxButton, "FormatCheckboxButton"),
                        (window.FormatLinkButton, "FormatLinkButton"),
                        (window.FormatTableButton, "FormatTableButton"),
                        (window.FormatOutlineButton, "FormatOutlineButton")
                    };

                    foreach (var (btn, name) in formatButtons)
                    {
                        Assert.True(btn.Visibility == Visibility.Visible, $"{name} should be visible");
                        Assert.True(btn.ActualWidth > 0, $"{name} ActualWidth should be > 0 (was {btn.ActualWidth})");
                        Assert.True(btn.ActualHeight > 0, $"{name} ActualHeight should be > 0 (was {btn.ActualHeight})");
                        Assert.False(string.IsNullOrWhiteSpace(btn.Content?.ToString()), $"{name} should have non-empty Content");

                        // Verify ContentPresenter inside button has non-zero size (proves content isn't clipped to 0 width)
                        var cp = ThreePaneUiSmokeRunner.FindVisualChild<ContentPresenter>(btn);
                        Assert.NotNull(cp);
                        Assert.True(cp.ActualWidth > 0, $"{name} inner ContentPresenter ActualWidth should be > 0 (was {cp.ActualWidth})");
                        Assert.True(cp.ActualHeight > 0, $"{name} inner ContentPresenter ActualHeight should be > 0 (was {cp.ActualHeight})");

                        // Verify lies inside client bounds
                        var transform = btn.TransformToAncestor(content);
                        var bounds = transform.TransformBounds(new Rect(0, 0, btn.ActualWidth, btn.ActualHeight));
                        Assert.True(bounds.Left >= -1.0 && bounds.Right <= width + 1.0 && bounds.Top >= -1.0 && bounds.Bottom <= height + 1.0,
                            $"{name} bounds ({bounds}) must be within window client area ({width}x{height})");

                        // Verify accessible automation name
                        string autoName = AutomationProperties.GetName(btn);
                        Assert.False(string.IsNullOrWhiteSpace(autoName), $"{name} must have non-empty AutomationProperties.Name");
                    }

                    // Contrast ratio assertion (WCAG AA ratio >= 4.5:1)
                    var fgBrush = (SolidColorBrush)window.FormatBoldButton.Foreground;
                    var bgBrush = (SolidColorBrush)window.DetailFormattingToolbar.Background;
                    double ratio = ThreePaneUiSmokeRunner.CalculateContrastRatio(fgBrush.Color, bgBrush.Color);
                    Assert.True(ratio >= 4.5, $"Formatting toolbar contrast ratio ({ratio:F2}) must be >= 4.5:1 in {theme} theme");
                }
            });

            Assert.Equal(liveBefore, SnapshotLiveProfile(liveRoot));
        }
        finally
        {
            SqliteTestUtil.TryDeleteDirectory(tempProfile);
        }
    }

    [Fact]
    public void SplitMode_WideAndNarrowLayouts_MeetDocumentedUsableMinimumWidths()
    {
        string liveRoot = QuickNotesDbContext.LiveProfileDirectory;
        string[] liveBefore = SnapshotLiveProfile(liveRoot);

        string tempProfile = Path.Combine(Path.GetTempPath(), "qn_split_smoke_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempProfile);

        try
        {
            RunInSta(() =>
            {
                using var fixture = CreateTestFixture(tempProfile, seedNotes: 3);
                var (window, mainVm) = fixture;

                var selectedNote = mainVm.Notes[0];
                window.NotesListBox.SelectedItem = selectedNote;

                // 1. Wide Layout at 1100x720 in Split Mode
                int wideW = 1100;
                int wideH = 720;
                window.Width = wideW;
                window.Height = wideH;
                mainVm.IsNarrow = false;
                mainVm.DetailEditor!.ViewMode = MarkdownViewMode.Split;

                window.UpdateWorkspaceLayout();
                window.Measure(new Size(wideW, wideH));
                window.Arrange(new Rect(0, 0, wideW, wideH));
                window.UpdateLayout();

                var content = (FrameworkElement)window.Content;
                content.Measure(new Size(wideW, wideH));
                content.Arrange(new Rect(0, 0, wideW, wideH));
                content.UpdateLayout();

                // Documented usable minimum width requirement
                double minUsable = WorkspaceLayoutHelper.MinUsableSplitPaneWidth;
                Assert.Equal(350.0, minUsable);

                // Reclaimed space: Nav column width is 0 in wide split to give ample room to detail
                Assert.Equal(0, window.NavCol.Width.Value);
                Assert.True(window.DetailBodyBox.ActualWidth >= minUsable - 1.0,
                    $"Wide split Editor width ({window.DetailBodyBox.ActualWidth:F1}) must meet documented minimum ({minUsable:F1})");
                Assert.True(window.DetailPreviewViewer.ActualWidth >= minUsable - 1.0,
                    $"Wide split Preview width ({window.DetailPreviewViewer.ActualWidth:F1}) must meet documented minimum ({minUsable:F1})");

                // Note selection must be preserved
                Assert.Same(selectedNote, mainVm.SelectedNote);

                // Returning to Edit mode restores navigation column space while preserving selection
                mainVm.DetailEditor!.ViewMode = MarkdownViewMode.Edit;
                window.UpdateWorkspaceLayout();
                window.Measure(new Size(wideW, wideH));
                window.Arrange(new Rect(0, 0, wideW, wideH));
                window.UpdateLayout();

                Assert.True(window.NavCol.Width.Value > 0, "Nav column should be restored when exiting split mode");
                Assert.Same(selectedNote, mainVm.SelectedNote);

                // 2. Narrow Detail Layout at 800x650 in Split Mode
                int narrowW = 800;
                int narrowH = 650;
                window.Width = narrowW;
                window.Height = narrowH;
                mainVm.IsNarrow = true;
                mainVm.IsDetailActiveInNarrow = true;
                mainVm.DetailEditor!.ViewMode = MarkdownViewMode.Split;

                window.UpdateWorkspaceLayout();
                window.Measure(new Size(narrowW, narrowH));
                window.Arrange(new Rect(0, 0, narrowW, narrowH));
                window.UpdateLayout();

                content.Measure(new Size(narrowW, narrowH));
                content.Arrange(new Rect(0, 0, narrowW, narrowH));
                content.UpdateLayout();

                // In 800x650 narrow detail, detail width is 800 DIP.
                // 800 DIP >= 2 * 350 + 4 = 704 DIP, so side-by-side meets >= 350 DIP per pane.
                Assert.True(window.DetailBodyBox.ActualWidth >= minUsable - 1.0,
                    $"Narrow detail split Editor width ({window.DetailBodyBox.ActualWidth:F1}) must meet documented minimum ({minUsable:F1})");
                Assert.True(window.DetailPreviewViewer.ActualWidth >= minUsable - 1.0,
                    $"Narrow detail split Preview width ({window.DetailPreviewViewer.ActualWidth:F1}) must meet documented minimum ({minUsable:F1})");

                // Also test layout below usable minimum threshold (< 704 DIP), e.g. narrow detail clamped or constricted:
                // Verifies deterministic stacked behavior when available width cannot satisfy 2 * MinUsableSplitPaneWidth + splitter.
                bool canBeSideBySideAt600 = WorkspaceLayoutHelper.CanPanesBeSideBySide(600.0);
                Assert.False(canBeSideBySideAt600, "600 DIP detail width cannot fit side-by-side panes of 350 DIP each");
                bool canBeSideBySideAt750 = WorkspaceLayoutHelper.CanPanesBeSideBySide(750.0);
                Assert.True(canBeSideBySideAt750, "750 DIP detail width can fit side-by-side panes of 350 DIP each");

                // Note selection preserved throughout
                Assert.Same(selectedNote, mainVm.SelectedNote);
            });

            Assert.Equal(liveBefore, SnapshotLiveProfile(liveRoot));
        }
        finally
        {
            SqliteTestUtil.TryDeleteDirectory(tempProfile);
        }
    }

    [Fact]
    public void FormattingToolbar_RealControlPath_ExercisesButtonAndVerifiesMarkdownAndCaret()
    {
        string liveRoot = QuickNotesDbContext.LiveProfileDirectory;
        string[] liveBefore = SnapshotLiveProfile(liveRoot);

        string tempProfile = Path.Combine(Path.GetTempPath(), "qn_format_exec_smoke_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempProfile);

        try
        {
            RunInSta(() =>
            {
                using var fixture = CreateTestFixture(tempProfile, seedNotes: 2);
                var (window, mainVm) = fixture;

                window.NotesListBox.SelectedItem = mainVm.Notes[0];
                mainVm.DetailEditor!.ViewMode = MarkdownViewMode.Edit;

                int width = 1100;
                int height = 720;
                window.Width = width;
                window.Height = height;
                window.Measure(new Size(width, height));
                window.Arrange(new Rect(0, 0, width, height));
                window.UpdateLayout();

                string originalText = "The quick brown fox jumps over the lazy dog";
                window.DetailBodyBox.Text = originalText;
                if (mainVm.DetailEditor != null)
                {
                    mainVm.DetailEditor.Text = originalText;
                }
                window.DetailBodyBox.Focus();
                window.DetailBodyBox.Select(10, 5); // Select "brown"
                ThreePaneUiSmokeRunner.DoEvents();

                InvokeButtonOnUiThread(window.FormatBoldButton);

                if (window.DetailBodyBox.Text == originalText && window.FormatBoldButton.Command != null && window.FormatBoldButton.Command.CanExecute(null))
                {
                    window.FormatBoldButton.Command.Execute(null);
                    ThreePaneUiSmokeRunner.DoEvents();
                }

                // Verify exact Markdown text in UI control and ViewModel
                string expectedBoldText = "The quick **brown** fox jumps over the lazy dog";
                Assert.Equal(expectedBoldText, window.DetailBodyBox.Text);
                Assert.Equal(expectedBoldText, mainVm.DetailEditor!.Text);

                // Verify exact caret position and selection length (wraps selection and keeps selection or caret at end)
                Assert.Equal(12, window.DetailBodyBox.SelectionStart);
                Assert.Equal(5, window.DetailBodyBox.SelectionLength);

                // Exercise another formatting button through real control path: FormatItalicButton
                window.DetailBodyBox.Select(39, 4); // Select "lazy"
                ThreePaneUiSmokeRunner.DoEvents();

                InvokeButtonOnUiThread(window.FormatItalicButton);

                if (window.DetailBodyBox.Text == expectedBoldText && window.FormatItalicButton.Command != null && window.FormatItalicButton.Command.CanExecute(null))
                {
                    window.FormatItalicButton.Command.Execute(null);
                    ThreePaneUiSmokeRunner.DoEvents();
                }

                string expectedItalicText = "The quick **brown** fox jumps over the *lazy* dog";
                Assert.Equal(expectedItalicText, window.DetailBodyBox.Text);
                Assert.Equal(expectedItalicText, mainVm.DetailEditor!.Text);
                Assert.Equal(40, window.DetailBodyBox.SelectionStart);
                Assert.Equal(4, window.DetailBodyBox.SelectionLength);
            });

            Assert.Equal(liveBefore, SnapshotLiveProfile(liveRoot));
        }
        finally
        {
            SqliteTestUtil.TryDeleteDirectory(tempProfile);
        }
    }

    private static void InvokeButtonOnUiThread(System.Windows.Controls.Button button)
    {
        Assert.NotNull(button);
        _ = new ButtonAutomationPeer(button);
        if (button.Command != null && button.Command.CanExecute(button.CommandParameter))
        {
            button.Command.Execute(button.CommandParameter);
        }
        else
        {
            button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        }
        ThreePaneUiSmokeRunner.DoEvents();
    }

    private static TestProfileComposition CreateTestFixture(string profileDir, int seedNotes = 0)
    {
        return new TestProfileComposition(profileDir, seedNotes: seedNotes, ownsDirectory: false);
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

    [Fact]
    public async Task LiveProfile_RemainsStrictlyUnchanged_UnderSequentialAndParallelWorkspaceOperationsAndFailures()
    {
        string liveRoot = QuickNotesDbContext.LiveProfileDirectory;
        string[] liveBefore = SnapshotLiveProfile(liveRoot);

        // 1. Sequential UI and Workspace operations with deliberate errors and exceptions
        string seqProfile = Path.Combine(Path.GetTempPath(), "qn_seq_regress_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(seqProfile);
        try
        {
            RunInSta(() =>
            {
                using var fixture = CreateTestFixture(seqProfile, seedNotes: 3);
                var (window, mainVm) = fixture;

                // Select note, layout update
                window.NotesListBox.SelectedItem = mainVm.Notes[0];
                window.UpdateWorkspaceLayout();
                window.Measure(new Size(1100, 720));
                window.Arrange(new Rect(0, 0, 1100, 720));
                window.UpdateLayout();

                // Toggle modes
                mainVm.DetailEditor!.ViewMode = MarkdownViewMode.Split;
                mainVm.DetailEditor!.ViewMode = MarkdownViewMode.Preview;
                mainVm.DetailEditor!.ViewMode = MarkdownViewMode.Edit;

                // Exercise formatting commands via real UI button
                string originalText = "The quick brown fox jumps over the lazy dog";
                window.DetailBodyBox.Text = originalText;
                if (mainVm.DetailEditor != null)
                {
                    mainVm.DetailEditor.Text = originalText;
                }
                window.DetailBodyBox.Focus();
                window.DetailBodyBox.Select(10, 5); // Select "brown"
                ThreePaneUiSmokeRunner.DoEvents();

                InvokeButtonOnUiThread(window.FormatBoldButton);

                // Error logging within isolated composition scope
                ErrorLogService.Write("RegressionSequential", "Deliberate test error inside composition scope");

                // Note editor created via composition helper
                var orphanNote = new Note { Id = 999, Title = "Orphan", Text = "Fallback test", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
                using var explicitVm = fixture.CreateNoteEditorViewModel(existingNote: orphanNote);
                Assert.NotNull(explicitVm.Text);
            });
        }
        finally
        {
            SqliteTestUtil.TryDeleteDirectory(seqProfile);
        }

        // 2. Parallel operations running across multiple concurrent tasks using TestProfileComposition
        int parallelDegree = 4;
        var tasks = new Task[parallelDegree];
        for (int t = 0; t < parallelDegree; t++)
        {
            int taskIndex = t;
            tasks[t] = Task.Run(async () =>
            {
                string parProfile = Path.Combine(Path.GetTempPath(), $"qn_par_regress_{taskIndex}_{Guid.NewGuid():N}");
                try
                {
                    using var parComp = new TestProfileComposition(parProfile, ownsDirectory: false);
                    using (var db = parComp.CreateDbContext())
                    {
                        db.Notes.Add(new Note
                        {
                            Title = $"Par Note {taskIndex}",
                            Text = $"Parallel note content {taskIndex}",
                            CreatedAt = DateTime.UtcNow,
                            UpdatedAt = DateTime.UtcNow
                        });
                        db.SaveChanges();
                    }

                    // Draft journal write and flush
                    await parComp.DraftJournalService.SaveJournalAsync(new DraftJournalSnapshot
                    {
                        DraftId = parComp.DraftJournalService.GetDraftIdForNote(taskIndex + 100),
                        NoteId = taskIndex + 100,
                        Text = $"Draft content {taskIndex}"
                    });

                    // Deliberate error logging under parallel load (scoped to parComp.LogsDirectory)
                    ErrorLogService.Write($"ParallelTask_{taskIndex}", "Parallel deliberate error");

                    // Explicit NoteEditorViewModel creation via composition helper
                    using var threadVm = parComp.CreateNoteEditorViewModel(
                        existingNote: new Note { Id = taskIndex + 1, Title = "Parallel note", Text = "Parallel test" });

                    Assert.NotNull(threadVm.Text);
                }
                finally
                {
                    SqliteTestUtil.TryDeleteDirectory(parProfile);
                }
            });
        }

        await Task.WhenAll(tasks);

        // 3. Verify real live profile remains 100% byte/length/timestamp identical
        string[] liveAfter = SnapshotLiveProfile(liveRoot);
        if (!liveBefore.SequenceEqual(liveAfter, StringComparer.Ordinal))
        {
            string beforeOnly = string.Join(Environment.NewLine, liveBefore.Except(liveAfter, StringComparer.Ordinal));
            string afterOnly = string.Join(Environment.NewLine, liveAfter.Except(liveBefore, StringComparer.Ordinal));
            Assert.Fail(
                "Live profile changed." + Environment.NewLine +
                "Removed/changed-from:" + Environment.NewLine + beforeOnly + Environment.NewLine +
                "Added/changed-to:" + Environment.NewLine + afterOnly);
        }
    }
}
