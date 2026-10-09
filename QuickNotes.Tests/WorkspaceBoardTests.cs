using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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

[Collection(LiveProfileSnapshotCollection.Name)]
[TestCategory(TestCategories.UiSensitive)]
public sealed class WorkspaceBoardTests
{
    private static readonly byte[] PngSignature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

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

    [Fact]
    public void WorkspaceBoard_Parse_RecognizesWorkspaceBoardSmokeSwitch()
    {
        string profile = Path.Combine(Path.GetTempPath(), "qn_board_cli_" + Guid.NewGuid().ToString("N"));
        var parsed = IsolatedProfileAppCli.Parse(new[]
        {
            "--isolated-profile", profile,
            "--workspace-board-smoke",
            "--output-dir", Path.Combine(Path.GetTempPath(), "qn_board_out")
        });
        Assert.True(parsed.IsIsolated);
        Assert.True(parsed.WorkspaceBoardSmoke);
        Assert.False(parsed.UxC08Smoke);
        Assert.False(parsed.ThreePaneSmoke);
    }

    [Fact]
    public void WorkspaceBoard_LayoutHelper_CalculatesAdaptiveColumnsAndCardWidths()
    {
        // Narrow threshold gives 1 column regardless of width
        Assert.Equal(1, WorkspaceLayoutHelper.CalculateBoardColumnCount(1200, isNarrow: true));

        // Wide available width: 854 DIP (typical wide window 1100 - 246 nav)
        int cols854 = WorkspaceLayoutHelper.CalculateBoardColumnCount(854, isNarrow: false);
        Assert.Equal(3, cols854);
        double width854 = WorkspaceLayoutHelper.CalculateBoardCardWidth(854, cols854);
        Assert.True(width854 >= WorkspaceLayoutHelper.MinBoardCardWidth);
        Assert.True(width854 <= WorkspaceLayoutHelper.MaxBoardCardWidth);

        // Large display: available width 1600 DIP -> at least 4 columns
        int cols1600 = WorkspaceLayoutHelper.CalculateBoardColumnCount(1600, isNarrow: false);
        Assert.True(cols1600 >= 4);

        // Very small available width (e.g. 300) -> 1 column
        int cols300 = WorkspaceLayoutHelper.CalculateBoardColumnCount(300, isNarrow: false);
        Assert.Equal(1, cols300);

        // Clamp checks
        Assert.Equal(WorkspaceLayoutHelper.MinBoardCardWidth, WorkspaceLayoutHelper.ClampBoardCardWidth(200));
        Assert.Equal(WorkspaceLayoutHelper.MaxBoardCardWidth, WorkspaceLayoutHelper.ClampBoardCardWidth(500));
        Assert.Equal(300, WorkspaceLayoutHelper.ClampBoardCardWidth(300));
    }

    [Fact]
    public void WorkspaceBoard_BoardVsList_ExposesSameNoteIdSequence()
    {
        string liveRoot = QuickNotesDbContext.LiveProfileDirectory;
        string[] liveBefore = SnapshotLiveProfile(liveRoot);

        string tempProfile = Path.Combine(Path.GetTempPath(), "qn_board_parity_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempProfile);
        try
        {
            RunInSta(() =>
            {
                ThemeService.ApplyThemeColors(isDark: false);
                using var fixture = new TestProfileComposition(tempProfile, seedNotes: 10, ownsDirectory: false);
                var (window, vm) = fixture;

                window.Width = 1100;
                window.Height = 720;
                window.Measure(new Size(1100, 720));
                window.Arrange(new Rect(0, 0, 1100, 720));
                window.UpdateLayout();

                // 1. Initial order parity
                var listIds = vm.Notes.Select(n => n.Id).ToList();
                Assert.Equal(10, listIds.Count);

                vm.SetBoardViewModeCommand.Execute(null);
                window.UpdateWorkspaceLayout();
                window.UpdateLayout();

                var boardIds = window.BoardListBox.ItemsSource.OfType<NoteCardViewModel>().Select(n => n.Id).ToList();
                Assert.Equal(listIds, boardIds);

                // 2. Sort change parity
                vm.SortMode = NoteSortMode.CreatedAt;
                ThreePaneUiSmokeRunner.DoEvents();
                window.UpdateLayout();

                var sortedListIds = vm.Notes.Select(n => n.Id).ToList();
                var sortedBoardIds = window.BoardListBox.ItemsSource.OfType<NoteCardViewModel>().Select(n => n.Id).ToList();
                Assert.Equal(sortedListIds, sortedBoardIds);

                // 3. Search query parity
                vm.SearchQuery = "Заметка 1";
                ThreePaneUiSmokeRunner.DoEvents();
                window.UpdateLayout();

                var searchListIds = vm.Notes.Select(n => n.Id).ToList();
                var searchBoardIds = window.BoardListBox.ItemsSource.OfType<NoteCardViewModel>().Select(n => n.Id).ToList();
                Assert.Equal(searchListIds, searchBoardIds);

                // Clear search
                vm.SearchQuery = string.Empty;
                ThreePaneUiSmokeRunner.DoEvents();
                window.UpdateLayout();

                // 4. Tag filter parity
                var workTagNode = vm.TagTreeRoots.FirstOrDefault(t => t.Name == "Работа")
                    ?? vm.TagTreeRoots.SelectMany(t => t.Children).FirstOrDefault(t => t.Name == "Работа");
                Assert.NotNull(workTagNode);
                vm.SelectedTag = workTagNode;
                ThreePaneUiSmokeRunner.DoEvents();
                window.UpdateLayout();

                var tagListIds = vm.Notes.Select(n => n.Id).ToList();
                var tagBoardIds = window.BoardListBox.ItemsSource.OfType<NoteCardViewModel>().Select(n => n.Id).ToList();
                Assert.NotEmpty(tagListIds);
                Assert.Equal(tagListIds, tagBoardIds);

                // Clear tag
                vm.SelectedTag = null;
                ThreePaneUiSmokeRunner.DoEvents();
                window.UpdateLayout();

                // 5. Section parity (Favorites)
                var favSection = vm.VirtualSections.FirstOrDefault(s => s.Section == NavigationSection.Favorites);
                Assert.NotNull(favSection);
                vm.SelectedSection = favSection;
                ThreePaneUiSmokeRunner.DoEvents();
                window.UpdateLayout();

                var favListIds = vm.Notes.Select(n => n.Id).ToList();
                var favBoardIds = window.BoardListBox.ItemsSource.OfType<NoteCardViewModel>().Select(n => n.Id).ToList();
                Assert.NotEmpty(favListIds);
                Assert.Equal(favListIds, favBoardIds);

                // Reset section back to All Notes
                var allSection = vm.VirtualSections.FirstOrDefault(s => s.Section == NavigationSection.All);
                if (allSection != null)
                {
                    vm.SelectedSection = allSection;
                    ThreePaneUiSmokeRunner.DoEvents();
                    window.UpdateLayout();
                }

                // 6. No extra ViewModels constructed: Board binds directly to existing NoteCardViewModel instances
                var listCards = vm.Notes.ToList();
                var boardCards = window.BoardListBox.ItemsSource.OfType<NoteCardViewModel>().ToList();
                Assert.Equal(listCards.Count, boardCards.Count);
                for (int i = 0; i < listCards.Count; i++)
                {
                    Assert.Same(listCards[i], boardCards[i]);
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
    public void WorkspaceBoard_Paging_FirstPageLimitedToPageSize_AndLoadMoreAppendsWithoutDuplicates()
    {
        string liveRoot = QuickNotesDbContext.LiveProfileDirectory;
        string[] liveBefore = SnapshotLiveProfile(liveRoot);

        string tempProfile = Path.Combine(Path.GetTempPath(), "qn_board_paging_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempProfile);
        try
        {
            // Seed 65 notes so first page (50) has continuation
            using (var db = new QuickNotesDbContext(new DbContextOptionsBuilder<QuickNotesDbContext>()
                       .UseSqlite($"Data Source={Path.Combine(tempProfile, "quicknotes.db")}").Options))
            {
                DbInitializer.Initialize(db);
                var now = DateTime.Now;
                for (int i = 1; i <= 65; i++)
                {
                    db.Notes.Add(new Note
                    {
                        Title = $"Пейджинг заметка {i:D3}",
                        Text = $"Текст для проверки постраничной загрузки на доске {i}.",
                        IsInbox = true,
                        CreatedAt = now.AddMinutes(-i),
                        UpdatedAt = now.AddMinutes(-i)
                    });
                }
                db.SaveChanges();
            }

            RunInSta(() =>
            {
                ThemeService.ApplyThemeColors(isDark: false);
                using var fixture = new TestProfileComposition(tempProfile, seedNotes: 0, ownsDirectory: false);
                var (window, vm) = fixture;

                window.Width = 1100;
                window.Height = 720;
                vm.SetBoardViewModeCommand.Execute(null);
                window.UpdateWorkspaceLayout();
                window.Measure(new Size(1100, 720));
                window.Arrange(new Rect(0, 0, 1100, 720));
                window.UpdateLayout();

                // First page must be <= PageSize (50)
                Assert.True(vm.Notes.Count <= vm.PageSize, $"Expected <= {vm.PageSize}, got {vm.Notes.Count}");
                Assert.Equal(50, vm.Notes.Count);
                Assert.True(vm.HasMoreNotes);
                Assert.Equal(Visibility.Visible, window.BoardLoadMoreNotesButton.Visibility);

                // Load more appends the remaining 15 notes
                vm.LoadMoreNotesCommand.Execute(null);
                ThreePaneUiSmokeRunner.DoEvents();
                window.UpdateLayout();

                Assert.Equal(65, vm.Notes.Count);
                Assert.False(vm.HasMoreNotes);
                Assert.Equal(Visibility.Collapsed, window.BoardLoadMoreNotesButton.Visibility);

                // Verify no duplicates
                var uniqueIds = vm.Notes.Select(n => n.Id).Distinct().ToList();
                Assert.Equal(65, uniqueIds.Count);

                // Board items source reflects the exact same 65 notes
                Assert.Equal(65, window.BoardListBox.Items.Count);
            });

            Assert.Equal(liveBefore, SnapshotLiveProfile(liveRoot));
        }
        finally
        {
            SqliteTestUtil.TryDeleteDirectory(tempProfile);
        }
    }

    [Fact]
    public void WorkspaceBoard_ProtectedCard_DoesNotLeakPlaintextBodyOrTitle()
    {
        string liveRoot = QuickNotesDbContext.LiveProfileDirectory;
        string[] liveBefore = SnapshotLiveProfile(liveRoot);

        string tempProfile = Path.Combine(Path.GetTempPath(), "qn_board_protected_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempProfile);
        try
        {
            const string secretMarker = "LEAK_SECRET_PROTECTED_BODY_MARKER";
            using (var db = new QuickNotesDbContext(new DbContextOptionsBuilder<QuickNotesDbContext>()
                       .UseSqlite($"Data Source={Path.Combine(tempProfile, "quicknotes.db")}").Options))
            {
                DbInitializer.Initialize(db);
                var now = DateTime.Now;
                db.Notes.Add(new Note
                {
                    Title = "Защищённая заметка",
                    Text = secretMarker,
                    IsProtected = true,
                    IsInbox = true,
                    CreatedAt = now,
                    UpdatedAt = now
                });
                db.Notes.Add(new Note
                {
                    Title = "Обычная заметка",
                    Text = "Текст обычной заметки.",
                    IsInbox = true,
                    CreatedAt = now.AddMinutes(-5),
                    UpdatedAt = now.AddMinutes(-5)
                });
                db.SaveChanges();
            }

            RunInSta(() =>
            {
                ThemeService.ApplyThemeColors(isDark: false);
                using var fixture = new TestProfileComposition(tempProfile, seedNotes: 0, ownsDirectory: false);
                var (window, vm) = fixture;

                window.Width = 1100;
                window.Height = 720;
                vm.SetBoardViewModeCommand.Execute(null);
                window.UpdateWorkspaceLayout();
                window.Measure(new Size(1100, 720));
                window.Arrange(new Rect(0, 0, 1100, 720));
                window.UpdateLayout();

                var protectedCard = vm.Notes.FirstOrDefault(n => n.IsProtected);
                Assert.NotNull(protectedCard);
                Assert.Contains("Защищённая заметка", protectedCard.DisplayTitle);
                Assert.Equal("Содержимое защищено паролем.", protectedCard.PreviewText);
                Assert.DoesNotContain(secretMarker, protectedCard.PreviewText);
                Assert.DoesNotContain(secretMarker, protectedCard.DisplayTitle);

                // Inspect visual tree of BoardListBox to ensure no element contains secretMarker
                string visible = UxPackageAbUiSmokeRunner.CollectVisibleUiText(window.BoardListBox);
                Assert.DoesNotContain(secretMarker, visible);

                foreach (var tb in FindVisualChildren<TextBlock>(window.BoardListBox))
                {
                    Assert.DoesNotContain(secretMarker, tb.Text ?? string.Empty);
                    string autoName = AutomationProperties.GetName(tb);
                    Assert.DoesNotContain(secretMarker, autoName ?? string.Empty);
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
    public void WorkspaceBoard_ToggleListBoardList_PreservesSelection_WithoutSyncOrRevisionMutation()
    {
        string liveRoot = QuickNotesDbContext.LiveProfileDirectory;
        string[] liveBefore = SnapshotLiveProfile(liveRoot);

        string tempProfile = Path.Combine(Path.GetTempPath(), "qn_board_toggle_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempProfile);
        try
        {
            RunInSta(() =>
            {
                ThemeService.ApplyThemeColors(isDark: false);
                using var fixture = new TestProfileComposition(tempProfile, seedNotes: 5, ownsDirectory: false);
                var (window, vm) = fixture;

                window.Width = 1100;
                window.Height = 720;
                window.Measure(new Size(1100, 720));
                window.Arrange(new Rect(0, 0, 1100, 720));
                window.UpdateLayout();

                // Select note index 2
                var targetNote = vm.Notes[2];
                vm.SelectedNote = targetNote;
                int targetId = targetNote.Id;

                int revisionsBefore;
                int syncStatesBefore;
                using (var db = vm.ContextFactory())
                {
                    revisionsBefore = db.NoteRevisions.Count();
                    syncStatesBefore = db.SyncEntityStates.Count();
                }
                int syncEnqueueBefore = vm.SyncScheduler?.PendingReasonsCount ?? 0;

                // Toggle to Board
                vm.SetBoardViewModeCommand.Execute(null);
                window.UpdateWorkspaceLayout();
                window.UpdateLayout();

                Assert.Equal(WorkspaceViewMode.Board, vm.WorkspaceViewMode);
                Assert.Equal(targetId, vm.SelectedNote?.Id);
                Assert.Equal(targetNote, window.BoardListBox.SelectedItem);

                // Toggle back to List
                vm.SetListViewModeCommand.Execute(null);
                window.UpdateWorkspaceLayout();
                window.UpdateLayout();

                Assert.Equal(WorkspaceViewMode.List, vm.WorkspaceViewMode);
                Assert.Equal(targetId, vm.SelectedNote?.Id);
                Assert.Equal(targetNote, window.NotesListBox.SelectedItem);

                // Verify revisions and sync states in database did not change
                using (var db = vm.ContextFactory())
                {
                    int revisionsAfter = db.NoteRevisions.Count();
                    int syncStatesAfter = db.SyncEntityStates.Count();
                    Assert.Equal(revisionsBefore, revisionsAfter);
                    Assert.Equal(syncStatesBefore, syncStatesAfter);
                }

                int syncEnqueueAfter = vm.SyncScheduler?.PendingReasonsCount ?? 0;
                Assert.Equal(syncEnqueueBefore, syncEnqueueAfter);
            });

            Assert.Equal(liveBefore, SnapshotLiveProfile(liveRoot));
        }
        finally
        {
            SqliteTestUtil.TryDeleteDirectory(tempProfile);
        }
    }

    [Fact]
    public void WorkspaceBoard_TasksSection_ShowsTaskList_HidesBoardCards()
    {
        string liveRoot = QuickNotesDbContext.LiveProfileDirectory;
        string[] liveBefore = SnapshotLiveProfile(liveRoot);

        string tempProfile = Path.Combine(Path.GetTempPath(), "qn_board_tasks_" + Guid.NewGuid().ToString("N"));
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
                vm.SetBoardViewModeCommand.Execute(null);
                window.UpdateWorkspaceLayout();
                window.Measure(new Size(1100, 720));
                window.Arrange(new Rect(0, 0, 1100, 720));
                window.UpdateLayout();

                // In Notes section: BoardScrollViewer is visible, BoardTasksListBox is collapsed
                Assert.Equal(Visibility.Visible, window.BoardScrollViewer.Visibility);
                Assert.Equal(Visibility.Collapsed, window.BoardTasksListBox.Visibility);

                // Select Tasks section
                var tasksSection = vm.VirtualSections.FirstOrDefault(s => s.IsTaskIndex);
                if (tasksSection != null)
                {
                    vm.SelectedSection = tasksSection;
                    ThreePaneUiSmokeRunner.DoEvents();
                    window.UpdateLayout();

                    Assert.True(vm.IsTasksSelected);
                    Assert.Equal(Visibility.Collapsed, window.BoardScrollViewer.Visibility);
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
    public void WorkspaceBoard_SmokeScreenshots_GeneratesValidPngs()
    {
        string liveRoot = QuickNotesDbContext.LiveProfileDirectory;
        string[] liveBefore = SnapshotLiveProfile(liveRoot);

        string tempProfile = Path.Combine(Path.GetTempPath(), "qn_board_screens_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempProfile);
        string outputDir = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "artifacts", "workspace-board-acceptance"));
        Directory.CreateDirectory(outputDir);

        try
        {
            StaTestHarness.Run(() =>
            {
                var previous = QuickNotesDbContext.ProfileDirectoryOverride;
                QuickNotesDbContext.ProfileDirectoryOverride = tempProfile;
                try
                {
                    using var fixture = new TestProfileComposition(tempProfile, seedNotes: 0, ownsDirectory: false);
                    var (window, _) = fixture;
                    window.Show();
                    WorkspaceBoardUiSmokeRunner.Run(window, outputDir);
                    window.CloseWithoutShutdown();
                }
                finally
                {
                    QuickNotesDbContext.ProfileDirectoryOverride = previous;
                }
            }, TimeSpan.FromSeconds(90));

            Assert.Equal(WorkspaceBoardUiSmokeRunner.ExpectedFileNames.Length,
                WorkspaceBoardUiSmokeRunner.ExpectedFileNames.Distinct(StringComparer.Ordinal).Count());

            byte[]? previousBytes = null;
            foreach (var name in WorkspaceBoardUiSmokeRunner.ExpectedFileNames)
            {
                string path = Path.Combine(outputDir, name);
                Assert.True(File.Exists(path), path);
                byte[] bytes = File.ReadAllBytes(path);
                Assert.True(bytes.Length >= PngSignature.Length, path);
                Assert.True(bytes.Take(PngSignature.Length).SequenceEqual(PngSignature), path);
                WorkspaceBoardUiSmokeRunner.ValidateOutput(path, name);

                string dump = File.ReadAllText(WorkspaceBoardUiSmokeRunner.VisibleTextDumpPath(path));
                Assert.Contains(WorkspaceBoardUiSmokeRunner.LongTitle, dump, StringComparison.Ordinal);
                Assert.Contains(WorkspaceBoardUiSmokeRunner.ConflictTitle, dump, StringComparison.Ordinal);
                Assert.DoesNotContain(WorkspaceBoardUiSmokeRunner.LeakSecretProtectedBodyMarker, dump, StringComparison.Ordinal);

                if (previousBytes != null)
                {
                    Assert.False(previousBytes.SequenceEqual(bytes), "adjacent board screenshots must differ: " + name);
                }

                previousBytes = bytes;
            }

            Assert.Equal(liveBefore, SnapshotLiveProfile(liveRoot));
        }
        finally
        {
            SqliteTestUtil.TryDeleteDirectory(tempProfile);
        }
    }

    [Fact]
    public void WorkspaceBoard_RenderedLayout_MatchesCalculatedColumnCount_AndWrapsCorrectly()
    {
        string liveRoot = QuickNotesDbContext.LiveProfileDirectory;
        string[] liveBefore = SnapshotLiveProfile(liveRoot);

        string tempProfile = Path.Combine(Path.GetTempPath(), "qn_board_layout_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempProfile);
        try
        {
            RunInSta(() =>
            {
                ThemeService.ApplyThemeColors(isDark: false);
                using var fixture = new TestProfileComposition(tempProfile, seedNotes: 4, ownsDirectory: false);
                var (window, vm) = fixture;

                // 1. Wide mode (1100x720) -> 3 columns
                window.Width = 1100;
                window.Height = 720;
                vm.IsNarrow = false;
                vm.SetBoardViewModeCommand.Execute(null);
                window.UpdateWorkspaceLayout();
                window.UpdateBoardLayout(1100);

                var content = (FrameworkElement)window.Content;
                content.Measure(new Size(1100, 720));
                content.Arrange(new Rect(0, 0, 1100, 720));
                content.UpdateLayout();
                ThreePaneUiSmokeRunner.DoEvents();

                int expectedCols = WorkspaceLayoutHelper.CalculateBoardColumnCount(854, isNarrow: false);
                Assert.Equal(3, expectedCols);

                var items = FindVisualChildren<ListBoxItem>(window.BoardListBox).Take(4).ToList();
                Assert.Equal(4, items.Count);

                Point p0 = items[0].TranslatePoint(new Point(0, 0), window.BoardListBox);
                Point p1 = items[1].TranslatePoint(new Point(0, 0), window.BoardListBox);
                Point p2 = items[2].TranslatePoint(new Point(0, 0), window.BoardListBox);
                Point p3 = items[3].TranslatePoint(new Point(0, 0), window.BoardListBox);

                // Row 1: items 0, 1, 2 must have the exact same Y position (3 columns in row 1)
                Assert.Equal(p0.Y, p1.Y, precision: 1);
                Assert.Equal(p0.Y, p2.Y, precision: 1);

                // Row 1: items 0, 1, 2 must be arranged horizontally
                Assert.True(p0.X < p1.X, $"Expected p0.X ({p0.X}) < p1.X ({p1.X})");
                Assert.True(p1.X < p2.X, $"Expected p1.X ({p1.X}) < p2.X ({p2.X})");

                // Row 2: item 3 must wrap to next row (p3.Y > p0.Y) and start at the left
                Assert.True(p3.Y > p0.Y, $"Expected p3.Y ({p3.Y}) > p0.Y ({p0.Y})");
                Assert.Equal(p0.X, p3.X, precision: 1);

                // 2. Narrow mode (800x650) -> 1 column
                window.Width = 800;
                window.Height = 650;
                vm.IsNarrow = true;
                window.UpdateWorkspaceLayout();
                window.UpdateBoardLayout(800);
                content.Measure(new Size(800, 650));
                content.Arrange(new Rect(0, 0, 800, 650));
                content.UpdateLayout();
                ThreePaneUiSmokeRunner.DoEvents();

                int narrowCols = WorkspaceLayoutHelper.CalculateBoardColumnCount(800 - 246, isNarrow: true);
                Assert.Equal(1, narrowCols);

                var narrowItems = FindVisualChildren<ListBoxItem>(window.BoardListBox).Take(4).ToList();
                Assert.Equal(4, narrowItems.Count);

                Point np0 = narrowItems[0].TranslatePoint(new Point(0, 0), window.BoardListBox);
                Point np1 = narrowItems[1].TranslatePoint(new Point(0, 0), window.BoardListBox);
                Point np2 = narrowItems[2].TranslatePoint(new Point(0, 0), window.BoardListBox);
                Point np3 = narrowItems[3].TranslatePoint(new Point(0, 0), window.BoardListBox);

                // In 1 column mode, every item wraps to its own row
                Assert.True(np1.Y > np0.Y, $"Expected np1.Y ({np1.Y}) > np0.Y ({np0.Y})");
                Assert.True(np2.Y > np1.Y, $"Expected np2.Y ({np2.Y}) > np1.Y ({np1.Y})");
                Assert.True(np3.Y > np2.Y, $"Expected np3.Y ({np3.Y}) > np2.Y ({np1.Y})");
                Assert.Equal(np0.X, np1.X, precision: 1);
                Assert.Equal(np0.X, np2.X, precision: 1);
                Assert.Equal(np0.X, np3.X, precision: 1);
            });

            Assert.Equal(liveBefore, SnapshotLiveProfile(liveRoot));
        }
        finally
        {
            SqliteTestUtil.TryDeleteDirectory(tempProfile);
        }
    }

    [Fact]
    public void WorkspaceBoard_Interactions_OneClickSelects_CtrlERoutesToEdit_EnterOpensFocus_PinFavoriteWork()
    {
        string liveRoot = QuickNotesDbContext.LiveProfileDirectory;
        string[] liveBefore = SnapshotLiveProfile(liveRoot);

        string tempProfile = Path.Combine(Path.GetTempPath(), "qn_board_interact_" + Guid.NewGuid().ToString("N"));
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
                vm.SetBoardViewModeCommand.Execute(null);
                window.UpdateWorkspaceLayout();
                window.Measure(new Size(1100, 720));
                window.Arrange(new Rect(0, 0, 1100, 720));
                window.UpdateLayout();
                window.Show();
                ThreePaneUiSmokeRunner.DoEvents();

                try
                {
                    // 1. One click selects via rendered Board card (CardSurface_MouseLeftButtonDown)
                    var cardContainers = FindVisualChildren<ListBoxItem>(window.BoardListBox).ToList();
                    Assert.True(cardContainers.Count >= 3, "Rendered card containers must be present on the Board.");

                    var targetNote = vm.Notes[1];
                    var cardBorder1 = FindVisualChildren<Border>(cardContainers[1]).FirstOrDefault(b => b.Name == "CardBorder");
                    Assert.NotNull(cardBorder1);

                    // Ensure initial selection differs
                    vm.SelectedNote = vm.Notes[0];
                    window.UpdateLayout();

                    var clickArgs = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                    {
                        RoutedEvent = UIElement.MouseLeftButtonDownEvent,
                        Source = cardBorder1
                    };
                    cardBorder1.RaiseEvent(clickArgs);

                    Assert.Equal(targetNote, vm.SelectedNote);
                    Assert.Equal(targetNote.Id, vm.SelectedNote?.Id);
                    Assert.Equal(targetNote, window.BoardListBox.SelectedItem);

                    // One click on the Board only selects; it does not open Focus.
                    Assert.Equal(WorkspaceViewMode.Board, vm.WorkspaceViewMode);

                    // Narrow mode: clicking rendered card selects and does not open detail
                    vm.IsNarrow = true;
                    vm.IsDetailActiveInNarrow = false;

                    var cardBorder2 = FindVisualChildren<Border>(cardContainers[2]).FirstOrDefault(b => b.Name == "CardBorder");
                    Assert.NotNull(cardBorder2);

                    var narrowClickArgs = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                    {
                        RoutedEvent = UIElement.MouseLeftButtonDownEvent,
                        Source = cardBorder2
                    };
                    cardBorder2.RaiseEvent(narrowClickArgs);

                    Assert.Equal(vm.Notes[2], vm.SelectedNote);
                    Assert.Equal(vm.Notes[2], window.BoardListBox.SelectedItem);
                    Assert.False(vm.IsDetailActiveInNarrow);
                    vm.IsNarrow = false;

                    // 2. Ctrl+E routes to existing EditNoteCommand via MainWindow_PreviewKeyDown
                    int editorOpenCount = 0;
                    NoteEditorViewModel? capturedEditorVm = null;
                    var eventField = typeof(MainViewModel).GetField(
                        nameof(MainViewModel.RequestOpenNoteEditor),
                        BindingFlags.Instance | BindingFlags.NonPublic);
                    Assert.NotNull(eventField);

                    // Intercept RequestOpenNoteEditor callback so command execution is proven without hanging STA in ShowDialog
                    eventField.SetValue(vm, (Func<NoteEditorViewModel, bool?>)(editorVm =>
                    {
                        editorOpenCount++;
                        capturedEditorVm = editorVm;
                        return false;
                    }));

                    SetCtrlKeyState(true);
                    try
                    {
                        var ctrlEArgs = new KeyEventArgs(
                            Keyboard.PrimaryDevice,
                            PresentationSource.FromVisual(window) ?? throw new InvalidOperationException("No PresentationSource"),
                            0,
                            Key.E)
                        {
                            RoutedEvent = Keyboard.PreviewKeyDownEvent
                        };
                        window.RaiseEvent(ctrlEArgs);

                        Assert.True(ctrlEArgs.Handled, "Ctrl+E should be handled by MainWindow_PreviewKeyDown.");
                        Assert.Equal(1, editorOpenCount);
                        Assert.NotNull(capturedEditorVm);
                        Assert.Equal(vm.SelectedNote?.Id, capturedEditorVm?.ExistingNote?.Id);
                    }
                    finally
                    {
                        SetCtrlKeyState(false);
                    }

                    // 3. Enter on BoardListBox opens Focus for the selected note (W3)
                    window.BoardListBox.Focus();
                    var enterArgs = new KeyEventArgs(
                        Keyboard.PrimaryDevice,
                        PresentationSource.FromVisual(window) ?? throw new InvalidOperationException("No PresentationSource"),
                        0,
                        Key.Enter)
                    {
                        RoutedEvent = Keyboard.PreviewKeyDownEvent
                    };
                    window.RaiseEvent(enterArgs);
                    Assert.True(enterArgs.Handled, "Enter on the Board should be handled by MainWindow_PreviewKeyDown.");
                    Assert.Equal(WorkspaceViewMode.Focus, vm.WorkspaceViewMode);
                    Assert.Equal(vm.SelectedNote?.Id, vm.DetailEditor?.NoteId);
                    window.UpdateWorkspaceLayout();
                    Assert.Equal(Visibility.Visible, window.DetailPaneBorder.Visibility);
                    Assert.Equal(Visibility.Collapsed, window.BoardHost.Visibility);

                    // Esc leaves Focus back to the Board without a commit or revision.
                    var escapeArgs = new KeyEventArgs(
                        Keyboard.PrimaryDevice,
                        PresentationSource.FromVisual(window) ?? throw new InvalidOperationException("No PresentationSource"),
                        0,
                        Key.Escape)
                    {
                        RoutedEvent = Keyboard.PreviewKeyDownEvent
                    };
                    window.RaiseEvent(escapeArgs);
                    Assert.Equal(WorkspaceViewMode.Board, vm.WorkspaceViewMode);
                    window.UpdateWorkspaceLayout();
                    Assert.False(vm.IsDetailActiveInNarrow);
                    Assert.Equal(Visibility.Collapsed, window.DetailPaneBorder.Visibility);

                    // 4. Pin and favorite controls inside realized NoteCardItemTemplate work
                    var firstCard = vm.Notes[0];
                    var firstContainer = cardContainers[0];

                    var buttons = FindVisualChildren<Button>(firstContainer).ToList();

                    // Pin button in NoteCardItemTemplate has Command bound to TogglePinCommand
                    var pinButton = buttons.FirstOrDefault(b =>
                        ReferenceEquals(b.Command, firstCard.TogglePinCommand) ||
                        b.ToolTip as string == firstCard.PinTooltip);
                    Assert.NotNull(pinButton);
                    Assert.Same(firstCard.TogglePinCommand, pinButton.Command);

                    bool pinnedBefore = firstCard.IsPinned;
                    InvokeButtonClick(pinButton);

                    using (var db = vm.ContextFactory())
                    {
                        var note = db.Notes.Find(firstCard.Id);
                        Assert.NotNull(note);
                        Assert.NotEqual(pinnedBefore, note.IsPinned);
                    }

                    // Favorite button in NoteCardItemTemplate has Command bound to ToggleFavoriteCommand
                    var favButton = buttons.FirstOrDefault(b =>
                        ReferenceEquals(b.Command, firstCard.ToggleFavoriteCommand) ||
                        b.ToolTip as string == firstCard.FavoriteTooltip);
                    Assert.NotNull(favButton);
                    Assert.Same(firstCard.ToggleFavoriteCommand, favButton.Command);

                    bool favBefore = firstCard.IsFavorite;
                    InvokeButtonClick(favButton);

                    using (var db = vm.ContextFactory())
                    {
                        var note = db.Notes.Find(firstCard.Id);
                        Assert.NotNull(note);
                        Assert.NotEqual(favBefore, note.IsFavorite);
                    }
                }
                finally
                {
                    window.CloseWithoutShutdown();
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
    public void WorkspaceBoard_StatusAndHelpCopy_DoesNotPromiseEnterInBoardMode()
    {
        string liveRoot = QuickNotesDbContext.LiveProfileDirectory;
        string[] liveBefore = SnapshotLiveProfile(liveRoot);

        string tempProfile = Path.Combine(Path.GetTempPath(), "qn_board_copy_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempProfile);
        try
        {
            RunInSta(() =>
            {
                ThemeService.ApplyThemeColors(isDark: false);
                using var fixture = new TestProfileComposition(tempProfile, seedNotes: 2, ownsDirectory: false);
                var (window, vm) = fixture;

                window.Width = 1100;
                window.Height = 720;
                window.Measure(new Size(1100, 720));
                window.Arrange(new Rect(0, 0, 1100, 720));
                window.UpdateLayout();

                // 1. In List mode, status bar mentions Enter
                Assert.Equal(WorkspaceViewMode.List, vm.WorkspaceViewMode);
                Assert.Contains("Enter", window.NavHintsBlock.Text);

                // 2. In Board mode, status bar mentions Ctrl+E and does NOT mention Enter
                vm.SetBoardViewModeCommand.Execute(null);
                window.UpdateWorkspaceLayout();
                window.UpdateLayout();

                Assert.Equal(WorkspaceViewMode.Board, vm.WorkspaceViewMode);
                Assert.DoesNotContain("Enter", window.NavHintsBlock.Text);
                Assert.Contains("Ctrl+E", window.NavHintsBlock.Text);
            });

            Assert.Equal(liveBefore, SnapshotLiveProfile(liveRoot));
        }
        finally
        {
            SqliteTestUtil.TryDeleteDirectory(tempProfile);
        }
    }

    private static System.Collections.Generic.IEnumerable<T> FindVisualChildren<T>(DependencyObject parent)
        where T : DependencyObject
    {
        int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var nested in FindVisualChildren<T>(child))
            {
                yield return nested;
            }
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetKeyboardState(byte[] lpKeyState);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetKeyboardState(byte[] lpKeyState);

    private static void SetCtrlKeyState(bool isDown)
    {
        byte[] keys = new byte[256];
        GetKeyboardState(keys);
        keys[0x11] = isDown ? (byte)0x80 : (byte)0; // VK_CONTROL
        keys[0xA2] = isDown ? (byte)0x80 : (byte)0; // VK_LCONTROL
        SetKeyboardState(keys);
    }

    private static void InvokeButtonClick(Button button)
    {
        var onClick = typeof(ButtonBase).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic);
        if (onClick != null)
        {
            onClick.Invoke(button, null);
        }
        else
        {
            button.Command?.Execute(button.CommandParameter);
        }
    }
}
