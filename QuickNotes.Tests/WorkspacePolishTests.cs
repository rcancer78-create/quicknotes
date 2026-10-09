using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
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
/// W6 workspace polish acceptance tests:
/// 1. Default stays List: fresh profile, missing or corrupt JSON, and first-run UI load List not Board.
/// 2. Performance contract: first Board and List page is PageSize (50); Board binds to existing NoteCardViewModels
///    without extra VM allocation and without full-corpus materialization. Documented 1000-note cap for automated
///    in-process measurements while physical 10k + DPI 100/125/150/200% remain manual leftovers.
/// 3. Shortcuts and help: F11, Enter/Esc Focus, and saved views never promise Board as default.
/// 4. Reversible rollback: List remains the baseline and rollback path without schema or Sync mutations.
/// </summary>
[Collection(LiveProfileSnapshotCollection.Name)]
[TestCategory(TestCategories.UiSensitive)]
public sealed class WorkspacePolishTests
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

    [Fact]
    public void WorkspacePolish_DefaultStaysList_OnFreshProfileAndMissingSettingsJson()
    {
        string liveRoot = QuickNotesDbContext.LiveProfileDirectory;
        string[] liveBefore = SnapshotLiveProfile(liveRoot);
        string tempProfile = CreateTempProfile("qn_polish_fresh");

        try
        {
            // 1. AppSettings model default is List.
            var appSettings = new AppSettings();
            Assert.Equal(WorkspaceViewMode.List, appSettings.WorkspaceViewMode);

            // 2. SettingsService with non-existent settings.json resolves to List.
            string settingsPath = Path.Combine(tempProfile, "settings.json");
            Assert.False(File.Exists(settingsPath));
            var settingsService = new SettingsService(settingsPath, _ => { });
            Assert.Equal(WorkspaceViewMode.List, settingsService.CurrentSettings.WorkspaceViewMode);

            // 3. UI and ViewModel from fresh profile start in List mode, not Board.
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

                // Three-pane list layout is visible; Board surface is collapsed.
                Assert.Equal(Visibility.Visible, window.NotesListGrid.Visibility);
                Assert.Equal(Visibility.Visible, window.DetailPaneBorder.Visibility);
                Assert.Equal(Visibility.Visible, window.NavBorder.Visibility);
                Assert.Equal(Visibility.Collapsed, window.BoardHost.Visibility);

                // WorkspaceModeToggle is unchecked (false corresponds to List).
                Assert.False(window.WorkspaceModeToggle.IsChecked);
            });

            Assert.Equal(liveBefore, SnapshotLiveProfile(liveRoot));
        }
        finally
        {
            SqliteTestUtil.TryDeleteDirectory(tempProfile);
        }
    }

    [Fact]
    public void WorkspacePolish_DefaultStaysList_OnCorruptOrUnrecognizedSettings()
    {
        string liveRoot = QuickNotesDbContext.LiveProfileDirectory;
        string[] liveBefore = SnapshotLiveProfile(liveRoot);
        string tempProfile = CreateTempProfile("qn_polish_corrupt");

        try
        {
            string settingsPath = Path.Combine(tempProfile, "settings.json");

            // Cases that must all safely fall back to WorkspaceViewMode.List:
            string[] corruptPayloads =
            {
                "",                                                  // empty file
                "{}",                                                // missing key
                """{ "WorkspaceViewMode": null }""",                 // null value
                """{ "WorkspaceViewMode": "" }""",                   // empty string
                """{ "WorkspaceViewMode": "BoardIsDefaultNow" }""",  // unknown string
                """{ "WorkspaceViewMode": 999 }""",                  // out-of-range integer
                """{ "WorkspaceViewMode": -1 }""",                   // negative integer
                """{ "WorkspaceViewMode": "Focus" }""",              // Focus is transient, must resolve to List
                """{ "WorkspaceViewMode": 2 }""",                    // Focus as int (2), must resolve to List
                """{ not valid json }"""                             // completely broken json
            };

            foreach (string payload in corruptPayloads)
            {
                File.WriteAllText(settingsPath, payload);
                var service = new SettingsService(settingsPath, _ => { });
                Assert.Equal(WorkspaceViewMode.List, service.CurrentSettings.WorkspaceViewMode);
            }

            Assert.Equal(liveBefore, SnapshotLiveProfile(liveRoot));
        }
        finally
        {
            SqliteTestUtil.TryDeleteDirectory(tempProfile);
        }
    }

    [Fact]
    public void WorkspacePolish_FirstRunOnboarding_MaintainsListAsDefault()
    {
        string liveRoot = QuickNotesDbContext.LiveProfileDirectory;
        string[] liveBefore = SnapshotLiveProfile(liveRoot);
        string tempProfile = CreateTempProfile("qn_polish_onboarding");

        try
        {
            RunInSta(() =>
            {
                ThemeService.ApplyThemeColors(isDark: false);
                using var fixture = new TestProfileComposition(tempProfile, seedNotes: 1, ownsDirectory: false);
                var (window, vm) = fixture;

                // Fresh profile starts with onboarding incomplete.
                Assert.Equal(WorkspaceViewMode.List, vm.WorkspaceViewMode);

                // Completing onboarding keeps mode as List.
                vm.CompleteOnboarding(createStarterTags: false);

                Assert.Equal(WorkspaceViewMode.List, vm.WorkspaceViewMode);
                Assert.True(vm.IsListViewMode);
                Assert.False(vm.IsBoardMode);
                Assert.Equal(Visibility.Collapsed, window.BoardHost.Visibility);
                Assert.Equal(Visibility.Visible, window.NotesListGrid.Visibility);
            });

            Assert.Equal(liveBefore, SnapshotLiveProfile(liveRoot));
        }
        finally
        {
            SqliteTestUtil.TryDeleteDirectory(tempProfile);
        }
    }

    [Fact]
    public void WorkspacePolish_PagingPerformanceContract_FirstPageBoundedToPageSize_NoFullCorpusMaterialization()
    {
        string liveRoot = QuickNotesDbContext.LiveProfileDirectory;
        string[] liveBefore = SnapshotLiveProfile(liveRoot);
        string tempProfile = CreateTempProfile("qn_polish_paging");

        const int totalNotesCount = WorkspaceLayoutHelper.AutomatedBenchmarkSampleCap; // 1000 isolated notes cap

        try
        {
            // Seed 1000 notes in isolated database using a single fast transaction.
            using (var db = new QuickNotesDbContext(new DbContextOptionsBuilder<QuickNotesDbContext>()
                       .UseSqlite($"Data Source={Path.Combine(tempProfile, "quicknotes.db")}").Options))
            {
                DbInitializer.Initialize(db);
                using var tx = db.Database.BeginTransaction();
                var now = DateTime.UtcNow;
                var batch = new List<Note>(totalNotesCount);
                for (int i = 1; i <= totalNotesCount; i++)
                {
                    batch.Add(new Note
                    {
                        Title = $"Заметка полировки {i:D4}",
                        Text = $"Текст заметки {i} для проверки производительности пагинации W6.",
                        IsInbox = true,
                        CreatedAt = now.AddSeconds(-i),
                        UpdatedAt = now.AddSeconds(-i)
                    });
                }
                db.Notes.AddRange(batch);
                db.SaveChanges();
                tx.Commit();
            }

            RunInSta(() =>
            {
                ThemeService.ApplyThemeColors(isDark: false);
                using var fixture = new TestProfileComposition(tempProfile, seedNotes: 0, ownsDirectory: false);
                var (window, vm) = fixture;

                window.Width = 1100;
                window.Height = 720;
                window.Measure(new Size(1100, 720));
                window.Arrange(new Rect(0, 0, 1100, 720));
                // Unshown windows still need the dispatcher to apply queued WPF bindings.
                ThreePaneUiSmokeRunner.DoEvents();
                window.UpdateLayout();

                // 1. Initial List mode verifies PageSize limit (50).
                Assert.Equal(totalNotesCount, vm.TotalNotesCount);
                Assert.Equal(WorkspaceLayoutHelper.DefaultPageSize, vm.Notes.Count);
                Assert.Equal(50, vm.Notes.Count);
                Assert.True(vm.HasMoreNotes);
                Assert.Equal(50, window.NotesListBox.Items.Count);

                // Invariant: no full-corpus materialization.
                Assert.Equal(
                    50,
                    WorkspaceLayoutHelper.CalculateMaterializedCardLimit(totalNotesCount, pageNumber: 1, pageSize: 50));
                Assert.True(vm.Notes.Count <= WorkspaceLayoutHelper.DefaultPageSize);

                // 2. Switch to Board mode: Board binds directly to existing Notes, NO extra card VMs constructed.
                var sw = Stopwatch.StartNew();
                vm.SetBoardViewModeCommand.Execute(null);
                window.UpdateWorkspaceLayout();
                window.UpdateBoardLayout(1100);
                window.UpdateLayout();
                ThreePaneUiSmokeRunner.DoEvents();
                sw.Stop();

                // Bounded layout execution time on 1000-note database.
                Assert.True(sw.ElapsedMilliseconds < 5000,
                    $"Board layout switch took {sw.ElapsedMilliseconds}ms, exceeding budget.");

                Assert.Equal(WorkspaceViewMode.Board, vm.WorkspaceViewMode);
                Assert.Equal(50, window.BoardListBox.Items.Count);

                // Check that BoardListBox.ItemsSource is referentially bound to vm.Notes
                // and does not allocate a separate card VM copy for any item.
                var boardItems = window.BoardListBox.ItemsSource.OfType<NoteCardViewModel>().ToList();
                Assert.Equal(50, boardItems.Count);
                for (int i = 0; i < 50; i++)
                {
                    Assert.Same(vm.Notes[i], boardItems[i]);
                }

                // 3. Continuation: LoadMoreNotes appends next page (Page 2 = 100 notes total).
                vm.LoadMoreNotesCommand.Execute(null);
                window.UpdateLayout();
                ThreePaneUiSmokeRunner.DoEvents();

                Assert.Equal(100, vm.Notes.Count);
                Assert.Equal(100, window.BoardListBox.Items.Count);
                Assert.True(vm.HasMoreNotes);

                // Verify 0 duplicates across the 100 loaded notes.
                var uniqueIds = vm.Notes.Select(n => n.Id).ToHashSet();
                Assert.Equal(100, uniqueIds.Count);

                // Invariant: all 100 items are referentially identical between ViewModel and Board view.
                var page2BoardItems = window.BoardListBox.ItemsSource.OfType<NoteCardViewModel>().ToList();
                Assert.Equal(100, page2BoardItems.Count);
                for (int i = 0; i < 100; i++)
                {
                    Assert.Same(vm.Notes[i], page2BoardItems[i]);
                }

                // Materialization cap on page 2 matches helper baseline.
                Assert.Equal(
                    100,
                    WorkspaceLayoutHelper.CalculateMaterializedCardLimit(totalNotesCount, pageNumber: 2, pageSize: 50));
            });

            Assert.Equal(liveBefore, SnapshotLiveProfile(liveRoot));
        }
        finally
        {
            SqliteTestUtil.TryDeleteDirectory(tempProfile);
        }
    }

    [Fact]
    public void WorkspacePolish_LayoutHelperBaseline_ContractualPageSizeAndRowMetrics()
    {
        // 1. Contractual constants.
        Assert.Equal(50, WorkspaceLayoutHelper.DefaultPageSize);
        Assert.Equal(1000, WorkspaceLayoutHelper.AutomatedBenchmarkSampleCap);

        // 2. Materialization cap boundary calculations.
        Assert.Equal(0, WorkspaceLayoutHelper.CalculateMaterializedCardLimit(0, 1, 50));
        Assert.Equal(0, WorkspaceLayoutHelper.CalculateMaterializedCardLimit(-10, 1, 50));
        Assert.Equal(0, WorkspaceLayoutHelper.CalculateMaterializedCardLimit(100, 0, 50));
        Assert.Equal(0, WorkspaceLayoutHelper.CalculateMaterializedCardLimit(100, -1, 50));
        Assert.Equal(0, WorkspaceLayoutHelper.CalculateMaterializedCardLimit(100, 1, 0));

        Assert.Equal(1, WorkspaceLayoutHelper.CalculateMaterializedCardLimit(1, 1, 50));
        Assert.Equal(49, WorkspaceLayoutHelper.CalculateMaterializedCardLimit(49, 1, 50));
        Assert.Equal(50, WorkspaceLayoutHelper.CalculateMaterializedCardLimit(50, 1, 50));
        Assert.Equal(50, WorkspaceLayoutHelper.CalculateMaterializedCardLimit(51, 1, 50));
        Assert.Equal(51, WorkspaceLayoutHelper.CalculateMaterializedCardLimit(51, 2, 50));

        Assert.Equal(50, WorkspaceLayoutHelper.CalculateMaterializedCardLimit(1000, 1, 50));
        Assert.Equal(100, WorkspaceLayoutHelper.CalculateMaterializedCardLimit(1000, 2, 50));
        Assert.Equal(1000, WorkspaceLayoutHelper.CalculateMaterializedCardLimit(1000, 20, 50));
        Assert.Equal(1000, WorkspaceLayoutHelper.CalculateMaterializedCardLimit(1000, 25, 50));

        // 3. Row metrics boundary calculations for 50-card page.
        // Narrow mode (1 column): 50 rows.
        var (c1, t1) = WorkspaceLayoutHelper.CalculateBoardRowMetrics(50, 1);
        Assert.Equal(50, c1);
        Assert.Equal(50, t1);

        // 2 columns: 25 complete, 25 total.
        var (c2, t2) = WorkspaceLayoutHelper.CalculateBoardRowMetrics(50, 2);
        Assert.Equal(25, c2);
        Assert.Equal(25, t2);

        // 3 columns: 16 complete, 17 total (50 = 16*3 + 2).
        var (c3, t3) = WorkspaceLayoutHelper.CalculateBoardRowMetrics(50, 3);
        Assert.Equal(16, c3);
        Assert.Equal(17, t3);

        // 4 columns: 12 complete, 13 total (50 = 12*4 + 2).
        var (c4, t4) = WorkspaceLayoutHelper.CalculateBoardRowMetrics(50, 4);
        Assert.Equal(12, c4);
        Assert.Equal(13, t4);

        // 5 columns: 10 complete, 10 total.
        var (c5, t5) = WorkspaceLayoutHelper.CalculateBoardRowMetrics(50, 5);
        Assert.Equal(10, c5);
        Assert.Equal(10, t5);

        // Degenerate inputs safely return (0, 0).
        Assert.Equal((0, 0), WorkspaceLayoutHelper.CalculateBoardRowMetrics(0, 3));
        Assert.Equal((0, 0), WorkspaceLayoutHelper.CalculateBoardRowMetrics(-5, 3));
        Assert.Equal((0, 0), WorkspaceLayoutHelper.CalculateBoardRowMetrics(50, 0));
        Assert.Equal((0, 0), WorkspaceLayoutHelper.CalculateBoardRowMetrics(50, -1));
    }

    [Fact]
    public void WorkspacePolish_ShortcutsAndHelp_DoNotPromiseBoardAsDefault()
    {
        // 1. ShortcutCatalog help paragraph:
        // Mentions F11, Enter/Esc Focus, and Enter in list without promising Board as default.
        string helpParagraph = ShortcutCatalog.BuildHelpHotkeysParagraph(ShortcutCatalog.DefaultOcrGesture);
        Assert.Contains("Enter в списке открывает выбранную заметку", helpParagraph);
        Assert.Contains("Enter на доске открывает заметку в режиме «Фокус»", helpParagraph);
        Assert.Contains("Esc возвращает назад", helpParagraph);
        Assert.Contains("F11 — полноэкранный режим", helpParagraph);
        Assert.Contains("F4 — переключение панели навигации", helpParagraph);

        // Must NOT promise Board as default anywhere in help text.
        Assert.DoesNotContain("доска по умолчанию", helpParagraph, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("доска — основной режим", helpParagraph, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("основной режим — доска", helpParagraph, StringComparison.OrdinalIgnoreCase);

        // 2. Toolbar tips.
        Assert.Equal("Полноэкранный режим (F11)", ShortcutCatalog.FullscreenToolbarTip);
        Assert.Equal("Выйти из полноэкранного режима (F11, Esc)", ShortcutCatalog.FullscreenExitToolbarTip);
        Assert.Equal("Переключить панель навигации (F4)", ShortcutCatalog.NavigationToggleToolbarTip);

        // 3. Saved views model default is List, not Board.
        var newView = new SavedWorkspaceView();
        Assert.Equal(WorkspaceViewMode.List, newView.ViewMode);

        // 4. Deserializing a view with missing, unknown, or Focus view mode falls back to List.
        const string legacyViewJson = """[{ "Name": "Без режима", "SearchQuery": "тест" }]""";
        var legacyViews = JsonSerializer.Deserialize<List<SavedWorkspaceView>>(
            legacyViewJson,
            new JsonSerializerOptions { Converters = { new SavedWorkspaceViewsJsonConverter() } });
        Assert.NotNull(legacyViews);
        Assert.Single(legacyViews);
        Assert.Equal(WorkspaceViewMode.List, legacyViews[0].ViewMode);

        const string focusViewJson = """[{ "Name": "Фокусный вид", "ViewMode": "Focus" }]""";
        var focusViews = JsonSerializer.Deserialize<List<SavedWorkspaceView>>(
            focusViewJson,
            new JsonSerializerOptions { Converters = { new SavedWorkspaceViewsJsonConverter() } });
        Assert.NotNull(focusViews);
        Assert.Single(focusViews);
        Assert.Equal(WorkspaceViewMode.List, focusViews[0].ViewMode);

        // 5. SavedViewItemViewModel tooltip clearly identifies view as a filter, not default board.
        var itemVm = new SavedViewItemViewModel(new SavedWorkspaceView { Name = "Мой вид" });
        Assert.Contains("фильтр, а не копия заметок", itemVm.Tooltip);
        Assert.DoesNotContain("по умолчанию", itemVm.Tooltip, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WorkspacePolish_WorkspaceModeToggle_HasNeutralAccessibleCopy_AndPreservesRollback()
    {
        string liveRoot = QuickNotesDbContext.LiveProfileDirectory;
        string[] liveBefore = SnapshotLiveProfile(liveRoot);
        string tempProfile = CreateTempProfile("qn_polish_toggle");

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

                // Check AutomationProperties and ToolTip on the toggle.
                string autoName = AutomationProperties.GetName(window.WorkspaceModeToggle);
                string tooltip = window.WorkspaceModeToggle.ToolTip?.ToString() ?? string.Empty;

                Assert.Equal("Представление рабочего пространства: Доска или Список", autoName);
                Assert.Equal("Переключить представление рабочего пространства: Доска / Список", tooltip);

                // Initial state: false = List (the default).
                Assert.False(window.WorkspaceModeToggle.IsChecked);
                Assert.Equal(WorkspaceViewMode.List, vm.WorkspaceViewMode);

                // Toggle to Board and back to List (rollback path).
                vm.SetBoardViewModeCommand.Execute(null);
                window.UpdateWorkspaceLayout();
                ThreePaneUiSmokeRunner.DoEvents();
                Assert.True(window.WorkspaceModeToggle.IsChecked);
                Assert.Equal(WorkspaceViewMode.Board, vm.WorkspaceViewMode);

                vm.SetListViewModeCommand.Execute(null);
                window.UpdateWorkspaceLayout();
                ThreePaneUiSmokeRunner.DoEvents();
                Assert.False(window.WorkspaceModeToggle.IsChecked);
                Assert.Equal(WorkspaceViewMode.List, vm.WorkspaceViewMode);
                Assert.Equal(Visibility.Visible, window.NotesListGrid.Visibility);
                Assert.Equal(Visibility.Visible, window.DetailPaneBorder.Visibility);
                Assert.Equal(Visibility.Collapsed, window.BoardHost.Visibility);
            });

            Assert.Equal(liveBefore, SnapshotLiveProfile(liveRoot));
        }
        finally
        {
            SqliteTestUtil.TryDeleteDirectory(tempProfile);
        }
    }
}
