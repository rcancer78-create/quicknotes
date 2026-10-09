using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
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
/// W5 saved workspace views acceptance: local named filters over the existing note
/// set. Views never copy notes, never create revisions, and never enqueue Sync.
/// </summary>
[Collection(LiveProfileSnapshotCollection.Name)]
[TestCategory(TestCategories.UiSensitive)]
public sealed class WorkspaceViewsTests
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

    private static int CountRevisions(MainViewModel vm)
    {
        using var db = vm.ContextFactory();
        return db.NoteRevisions.Count();
    }

    [Fact]
    public void SavedWorkspaceView_DefaultRoundTrip_AndUnknownFieldsAreIgnored()
    {
        var view = new SavedWorkspaceView
        {
            Name = "Работа",
            Section = NavigationSection.Favorites,
            IncludedTagIds = new List<int> { 3 },
            ExcludedTagIds = new List<int> { 7 },
            SearchQuery = "отчёт",
            SortMode = NoteSortMode.UpdatedAt,
            ViewMode = WorkspaceViewMode.Board,
            CompactCards = true
        };

        var settings = new AppSettings { SavedWorkspaceViews = new List<SavedWorkspaceView> { view } };
        string json = JsonSerializer.Serialize(settings);

        // A future/unknown top-level and per-view field must not break deserialization.
        string withUnknown = json.TrimEnd();
        withUnknown = withUnknown.Substring(0, withUnknown.Length - 1) + ",\"FutureField\":42}";
        withUnknown = withUnknown.Replace("\"Name\": \"Работа\"", "\"Name\": \"Работа\", \"FutureViewField\": \"x\"");

        var loaded = JsonSerializer.Deserialize<AppSettings>(withUnknown);
        Assert.NotNull(loaded);
        Assert.Single(loaded!.SavedWorkspaceViews);

        var roundTripped = loaded.SavedWorkspaceViews[0];
        Assert.Equal("Работа", roundTripped.Name);
        Assert.Equal(NavigationSection.Favorites, roundTripped.Section);
        Assert.Equal(new[] { 3 }, roundTripped.IncludedTagIds);
        Assert.Equal(new[] { 7 }, roundTripped.ExcludedTagIds);
        Assert.Equal("отчёт", roundTripped.SearchQuery);
        Assert.Equal(NoteSortMode.UpdatedAt, roundTripped.SortMode);
        Assert.Equal(WorkspaceViewMode.Board, roundTripped.ViewMode);
        Assert.True(roundTripped.CompactCards);
    }

    [Fact]
    public void SavedWorkspaceView_CorruptEntries_AreSkippedOrFailSafe_WithoutThrowing()
    {
        const string json = """
        {
            "SavedWorkspaceViews": [
                { "Name": "Хороший вид", "Section": "Favorites", "SortMode": "UpdatedAt", "ViewMode": "Board" },
                { "Name": "", "Section": "All" },
                { "Name": "Плохой раздел", "Section": "Несуществующий", "SortMode": "НетТакого", "ViewMode": "Focus" },
                { "Name": "Плохие теги", "IncludedTagIds": "not-an-array", "ExcludedTagIds": [1, "2", "x"] },
                "не объект",
                null
            ]
        }
        """;

        var loaded = JsonSerializer.Deserialize<AppSettings>(json);
        Assert.NotNull(loaded);
        var views = loaded!.SavedWorkspaceViews;

        // The empty-name and non-object entries are skipped; the rest are safe.
        Assert.Equal(3, views.Count);

        var good = views.Single(v => v.Name == "Хороший вид");
        Assert.Equal(NavigationSection.Favorites, good.Section);
        Assert.Equal(NoteSortMode.UpdatedAt, good.SortMode);
        Assert.Equal(WorkspaceViewMode.Board, good.ViewMode);

        var badSection = views.Single(v => v.Name == "Плохой раздел");
        Assert.Equal(NavigationSection.All, badSection.Section);
        Assert.Equal(NoteSortMode.Pinned, badSection.SortMode);
        Assert.Equal(WorkspaceViewMode.List, badSection.ViewMode);

        var badTags = views.Single(v => v.Name == "Плохие теги");
        Assert.Empty(badTags.IncludedTagIds);
        Assert.Equal(new[] { 1, 2 }, badTags.ExcludedTagIds);
    }

    [Fact]
    public void SavedWorkspaceView_NonArrayOrMissing_FailsSafe()
    {
        var fromNonArray = JsonSerializer.Deserialize<AppSettings>("""{ "SavedWorkspaceViews": "мусор" }""");
        Assert.NotNull(fromNonArray);
        Assert.NotNull(fromNonArray!.SavedWorkspaceViews);
        Assert.Empty(fromNonArray.SavedWorkspaceViews);

        var fromNull = JsonSerializer.Deserialize<AppSettings>("""{ "SavedWorkspaceViews": null }""");
        Assert.NotNull(fromNull);
        Assert.Empty(fromNull!.SavedWorkspaceViews);

        Assert.Empty(new AppSettings().SavedWorkspaceViews);
    }

    [Fact]
    public void WorkspaceViews_CreateFromCurrentFilters_ApplyRestoresSameIdsAndOrder()
    {
        string liveRoot = QuickNotesDbContext.LiveProfileDirectory;
        string[] liveBefore = SnapshotLiveProfile(liveRoot);

        string tempProfile = Path.Combine(Path.GetTempPath(), "qn_views_apply_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempProfile);
        try
        {
            RunInSta(() =>
            {
                ThemeService.ApplyThemeColors(isDark: false);
                using var fixture = new TestProfileComposition(tempProfile, seedNotes: 6, ownsDirectory: false);
                var (window, vm) = fixture;

                window.Measure(new Size(1100, 720));
                window.Arrange(new Rect(0, 0, 1100, 720));
                window.UpdateLayout();

                // Build a specific filter: search + tag + sort + Board + compact density.
                var workTag = vm.TagTreeRoots.FirstOrDefault(t => t.Name == "Работа")
                    ?? vm.TagTreeRoots.SelectMany(t => t.Children).FirstOrDefault(t => t.Name == "Работа");
                Assert.NotNull(workTag);

                vm.SelectedTag = workTag;
                vm.SearchQuery = "тестовой";
                vm.RefreshNotes();
                vm.SortMode = NoteSortMode.CreatedAt;
                vm.SetBoardViewModeCommand.Execute(null);
                vm.CompactCards = true;
                vm.RefreshNotes();

                var expectedIds = vm.Notes.Select(n => n.Id).ToList();
                Assert.NotEmpty(expectedIds);

                // Create a view from the current filter (dialog intercepted).
                string createdName = "Работа + поиск";
                vm.RequestOpenSavedViewEditor = editor =>
                {
                    editor.ViewName = createdName;
                    return true;
                };
                vm.SaveCurrentViewCommand.Execute(null);

                Assert.Single(vm.SavedViews);
                Assert.Equal(createdName, vm.SavedViews[0].Name);

                var saved = vm.SavedViews[0].View;
                Assert.Equal(NoteSortMode.CreatedAt, saved.SortMode);
                Assert.Equal(WorkspaceViewMode.Board, saved.ViewMode);
                Assert.True(saved.CompactCards);
                Assert.Equal(workTag.Id, saved.IncludedTagIds.Single());
                Assert.Contains("тестовой", saved.SearchQuery);

                // Reset everything, then apply the view.
                vm.ResetFilter();
                vm.SortMode = NoteSortMode.Pinned;
                vm.SetListViewModeCommand.Execute(null);
                vm.CompactCards = false;
                vm.RefreshNotes();

                Assert.True(vm.ApplySavedView(vm.SavedViews[0]));

                Assert.Equal(expectedIds, vm.Notes.Select(n => n.Id).ToList());
                Assert.Equal(workTag.Id, vm.SelectedTag?.Id);
                Assert.Equal(NoteSortMode.CreatedAt, vm.SortMode);
                Assert.Equal(WorkspaceViewMode.Board, vm.WorkspaceViewMode);
                Assert.True(vm.CompactCards);
            });

            Assert.Equal(liveBefore, SnapshotLiveProfile(liveRoot));
        }
        finally
        {
            SqliteTestUtil.TryDeleteDirectory(tempProfile);
        }
    }

    [Fact]
    public void WorkspaceViews_ApplySectionOnlyView_RestoresSectionFilter()
    {
        string liveRoot = QuickNotesDbContext.LiveProfileDirectory;
        string[] liveBefore = SnapshotLiveProfile(liveRoot);

        string tempProfile = Path.Combine(Path.GetTempPath(), "qn_views_section_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempProfile);
        try
        {
            RunInSta(() =>
            {
                ThemeService.ApplyThemeColors(isDark: false);
                using var fixture = new TestProfileComposition(tempProfile, seedNotes: 6, ownsDirectory: false);
                var (_, vm) = fixture;

                var favorites = vm.VirtualSections.Single(s => s.Section == NavigationSection.Favorites);
                vm.SelectedSection = favorites;
                vm.RefreshNotes();
                var expected = vm.Notes.Select(n => n.Id).ToList();
                Assert.NotEmpty(expected);

                vm.RequestOpenSavedViewEditor = editor =>
                {
                    editor.ViewName = "Избранное";
                    return true;
                };
                vm.SaveCurrentViewCommand.Execute(null);

                vm.ResetFilter();
                vm.RefreshNotes();

                Assert.True(vm.ApplySavedView(vm.SavedViews[0]));
                Assert.Equal(NavigationSection.Favorites, vm.SelectedSection?.Section);
                Assert.Equal(expected, vm.Notes.Select(n => n.Id).ToList());
            });

            Assert.Equal(liveBefore, SnapshotLiveProfile(liveRoot));
        }
        finally
        {
            SqliteTestUtil.TryDeleteDirectory(tempProfile);
        }
    }

    [Fact]
    public void WorkspaceViews_ApplyUsesAuthoritativeTagLists_EvenWhenSearchQueryOmitsThem()
    {
        string liveRoot = QuickNotesDbContext.LiveProfileDirectory;
        string[] liveBefore = SnapshotLiveProfile(liveRoot);

        string tempProfile = Path.Combine(Path.GetTempPath(), "qn_views_lists_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempProfile);
        try
        {
            RunInSta(() =>
            {
                ThemeService.ApplyThemeColors(isDark: false);
                using var fixture = new TestProfileComposition(tempProfile, seedNotes: 6, ownsDirectory: false);

                // Give one "Работа" note the "Проект" tag as well, so the exclusion is observable.
                int workTagId;
                int projectTagId;
                int overlapNoteId;
                using (var db = fixture.CreateDbContext())
                {
                    var work = db.Tags.Single(t => t.Name == "Работа");
                    var project = db.Tags.Single(t => t.Name == "Проект");
                    workTagId = work.Id;
                    projectTagId = project.Id;

                    var workNote = db.Notes
                        .Include(n => n.NoteTags)
                        .First(n => n.NoteTags.Any(nt => nt.TagId == work.Id));
                    workNote.NoteTags.Add(new NoteTag { TagId = project.Id, Origin = TagOrigin.Manual });
                    overlapNoteId = workNote.Id;
                    db.SaveChanges();
                }

                var (window, vm) = fixture;
                window.Measure(new Size(1100, 720));
                window.Arrange(new Rect(0, 0, 1100, 720));
                window.UpdateLayout();

                var workNode = FindTagNode(vm, workTagId);
                Assert.NotNull(workNode);

                // Manual reference: selected tag + explicit exclusion in the free-text query.
                vm.SelectedTag = workNode;
                vm.SearchQuery = "-tag:Проект";
                vm.RefreshNotes();
                var manualIds = vm.Notes.Select(n => n.Id).ToList();
                Assert.NotEmpty(manualIds);
                Assert.DoesNotContain(overlapNoteId, manualIds);

                vm.ResetFilter();
                vm.RefreshNotes();

                // Authoritative lists: the persisted tag ids carry the whole tag filter, while
                // SearchQuery is empty. Apply must not depend on query-based duplication.
                var view = new SavedWorkspaceView
                {
                    Id = Guid.NewGuid(),
                    Name = "Только списки",
                    Section = NavigationSection.All,
                    IncludedTagIds = new List<int> { workTagId },
                    ExcludedTagIds = new List<int> { projectTagId },
                    SearchQuery = string.Empty,
                    SortMode = NoteSortMode.Pinned,
                    ViewMode = WorkspaceViewMode.List,
                    CompactCards = false
                };

                Assert.True(vm.ApplySavedView(new SavedViewItemViewModel(view)));

                Assert.Equal(workTagId, vm.SelectedTag?.Id);
                Assert.Equal(manualIds, vm.Notes.Select(n => n.Id).ToList());
                Assert.DoesNotContain(overlapNoteId, vm.Notes.Select(n => n.Id));
                Assert.Contains("Проект", vm.SearchQuery);
            });

            Assert.Equal(liveBefore, SnapshotLiveProfile(liveRoot));
        }
        finally
        {
            SqliteTestUtil.TryDeleteDirectory(tempProfile);
        }
    }

    [Fact]
    public void WorkspaceViews_ApplyAdditionalIncludedTag_MatchesExistingSearchCombination()
    {
        string liveRoot = QuickNotesDbContext.LiveProfileDirectory;
        string[] liveBefore = SnapshotLiveProfile(liveRoot);

        string tempProfile = Path.Combine(Path.GetTempPath(), "qn_views_multi_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempProfile);
        try
        {
            RunInSta(() =>
            {
                ThemeService.ApplyThemeColors(isDark: false);
                using var fixture = new TestProfileComposition(tempProfile, seedNotes: 6, ownsDirectory: false);

                int workTagId;
                int projectTagId;
                int overlapNoteId;
                using (var db = fixture.CreateDbContext())
                {
                    var work = db.Tags.Single(t => t.Name == "Работа");
                    var project = db.Tags.Single(t => t.Name == "Проект");
                    workTagId = work.Id;
                    projectTagId = project.Id;

                    var workNote = db.Notes
                        .Include(n => n.NoteTags)
                        .First(n => n.NoteTags.Any(nt => nt.TagId == work.Id));
                    workNote.NoteTags.Add(new NoteTag { TagId = project.Id, Origin = TagOrigin.Manual });
                    overlapNoteId = workNote.Id;
                    db.SaveChanges();
                }

                var (window, vm) = fixture;
                window.Measure(new Size(1100, 720));
                window.Arrange(new Rect(0, 0, 1100, 720));
                window.UpdateLayout();

                var workNode = FindTagNode(vm, workTagId);
                Assert.NotNull(workNode);

                // Documented mapping: first included id -> SelectedTag, remaining ids -> tag:Name.
                vm.SelectedTag = workNode;
                vm.SearchQuery = "tag:Проект";
                vm.RefreshNotes();
                var expectedIds = vm.Notes.Select(n => n.Id).ToList();
                Assert.Equal(new[] { overlapNoteId }, expectedIds);

                vm.ResetFilter();
                vm.RefreshNotes();

                var view = new SavedWorkspaceView
                {
                    Id = Guid.NewGuid(),
                    Name = "Два включённых тега",
                    Section = NavigationSection.All,
                    IncludedTagIds = new List<int> { workTagId, projectTagId },
                    ExcludedTagIds = new List<int>(),
                    SearchQuery = string.Empty,
                    SortMode = NoteSortMode.Pinned,
                    ViewMode = WorkspaceViewMode.List,
                    CompactCards = false
                };

                Assert.True(vm.ApplySavedView(new SavedViewItemViewModel(view)));

                Assert.Equal(workTagId, vm.SelectedTag?.Id);
                Assert.Equal(expectedIds, vm.Notes.Select(n => n.Id).ToList());

                // Applying a view never copies notes.
                using var dbAfter = fixture.CreateDbContext();
                Assert.Equal(6, dbAfter.Notes.Count());
            });

            Assert.Equal(liveBefore, SnapshotLiveProfile(liveRoot));
        }
        finally
        {
            SqliteTestUtil.TryDeleteDirectory(tempProfile);
        }
    }

    [Fact]
    public void WorkspaceViews_RenameAndDelete_KeepNotesAndRevisions_NoSyncEnqueue()
    {
        string liveRoot = QuickNotesDbContext.LiveProfileDirectory;
        string[] liveBefore = SnapshotLiveProfile(liveRoot);

        string tempProfile = Path.Combine(Path.GetTempPath(), "qn_views_delete_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempProfile);
        try
        {
            RunInSta(() =>
            {
                ThemeService.ApplyThemeColors(isDark: false);
                var scheduler = new FakeTrackingSyncScheduler();
                using var fixture = new TestProfileComposition(tempProfile, seedNotes: 4, ownsDirectory: false);
                var vm = fixture.CreateMainViewModel(syncScheduler: scheduler);

                int notesBefore;
                int revisionsBefore;
                using (var db = vm.ContextFactory())
                {
                    notesBefore = db.Notes.Count();
                    revisionsBefore = db.NoteRevisions.Count();
                }
                scheduler.EnqueueLocalChangeCallCount = 0;

                // Create is a local-settings-only mutation: the snapshot is taken before
                // create and must stay unchanged after create, rename, and delete.
                vm.RequestOpenSavedViewEditor = editor =>
                {
                    editor.ViewName = "Мой вид";
                    return true;
                };
                vm.SaveCurrentViewCommand.Execute(null);
                Assert.Single(vm.SavedViews);
                Assert.Equal(revisionsBefore, CountRevisions(vm));
                Assert.Equal(0, scheduler.EnqueueLocalChangeCallCount);

                // Rename.
                vm.RequestOpenSavedViewEditor = editor =>
                {
                    editor.ViewName = "Переименованный";
                    return true;
                };
                vm.RenameSavedViewCommand.Execute(vm.SavedViews[0]);
                Assert.Equal("Переименованный", vm.SavedViews[0].Name);
                Assert.Equal(revisionsBefore, CountRevisions(vm));
                Assert.Equal(0, scheduler.EnqueueLocalChangeCallCount);

                // Delete (confirm dialog intercepted as OK).
                vm.ConfirmHandler = (_, _, _, _) => MessageBoxResult.OK;
                vm.DeleteSavedViewCommand.Execute(vm.SavedViews[0]);
                Assert.Empty(vm.SavedViews);
                Assert.False(vm.HasSavedViews);

                using (var db = vm.ContextFactory())
                {
                    Assert.Equal(notesBefore, db.Notes.Count());
                    Assert.Equal(revisionsBefore, db.NoteRevisions.Count());
                }
                Assert.Equal(0, scheduler.EnqueueLocalChangeCallCount);
            });

            Assert.Equal(liveBefore, SnapshotLiveProfile(liveRoot));
        }
        finally
        {
            SqliteTestUtil.TryDeleteDirectory(tempProfile);
        }
    }

    [Fact]
    public void WorkspaceViews_DeleteConfirmCancel_KeepsView()
    {
        string liveRoot = QuickNotesDbContext.LiveProfileDirectory;
        string[] liveBefore = SnapshotLiveProfile(liveRoot);

        string tempProfile = Path.Combine(Path.GetTempPath(), "qn_views_cancel_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempProfile);
        try
        {
            RunInSta(() =>
            {
                ThemeService.ApplyThemeColors(isDark: false);
                using var fixture = new TestProfileComposition(tempProfile, seedNotes: 2, ownsDirectory: false);
                var (_, vm) = fixture;

                vm.RequestOpenSavedViewEditor = editor =>
                {
                    editor.ViewName = "Остаётся";
                    return true;
                };
                vm.SaveCurrentViewCommand.Execute(null);

                string? confirmMessage = null;
                vm.ConfirmHandler = (message, _, _, _) =>
                {
                    confirmMessage = message;
                    return MessageBoxResult.Cancel;
                };
                vm.DeleteSavedViewCommand.Execute(vm.SavedViews[0]);

                Assert.Single(vm.SavedViews);
                Assert.NotNull(confirmMessage);
                // Destructive copy must reference the view, not the notes.
                Assert.Contains("вид", confirmMessage, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("не будут удалены", confirmMessage);
            });

            Assert.Equal(liveBefore, SnapshotLiveProfile(liveRoot));
        }
        finally
        {
            SqliteTestUtil.TryDeleteDirectory(tempProfile);
        }
    }

    [Fact]
    public void WorkspaceViews_TwoViewsShareSameNote_WithoutCopying()
    {
        string liveRoot = QuickNotesDbContext.LiveProfileDirectory;
        string[] liveBefore = SnapshotLiveProfile(liveRoot);

        string tempProfile = Path.Combine(Path.GetTempPath(), "qn_views_overlap_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempProfile);
        try
        {
            RunInSta(() =>
            {
                ThemeService.ApplyThemeColors(isDark: false);
                using var fixture = new TestProfileComposition(tempProfile, seedNotes: 6, ownsDirectory: false);
                var (_, vm) = fixture;

                // View A: search that matches multiple notes.
                vm.SearchQuery = "Заметка";
                vm.RefreshNotes();
                var idsA = vm.Notes.Select(n => n.Id).ToHashSet();
                Assert.True(idsA.Count >= 2);

                vm.RequestOpenSavedViewEditor = editor =>
                {
                    editor.ViewName = "Вид A";
                    return true;
                };
                vm.SaveCurrentViewCommand.Execute(null);

                // View B: All section with no search -> everything (superset).
                vm.ResetFilter();
                vm.RefreshNotes();
                var idsB = vm.Notes.Select(n => n.Id).ToHashSet();

                vm.RequestOpenSavedViewEditor = editor =>
                {
                    editor.ViewName = "Вид B";
                    return true;
                };
                vm.SaveCurrentViewCommand.Execute(null);

                Assert.Equal(2, vm.SavedViews.Count);

                // A note visible in both views (overlap), still a single DB row.
                var overlap = idsA.Intersect(idsB).ToList();
                Assert.NotEmpty(overlap);

                int notesBefore;
                using (var db = vm.ContextFactory())
                {
                    notesBefore = db.Notes.Count();
                }

                vm.ApplySavedView(vm.SavedViews[0]);
                var appliedA = vm.Notes.Select(n => n.Id).ToHashSet();
                Assert.True(appliedA.IsSubsetOf(idsA));
                Assert.NotEmpty(appliedA.Intersect(overlap));

                vm.ApplySavedView(vm.SavedViews[1]);
                var appliedB = vm.Notes.Select(n => n.Id).ToHashSet();
                Assert.Contains(overlap[0], appliedB);

                using (var db = vm.ContextFactory())
                {
                    Assert.Equal(notesBefore, db.Notes.Count());
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
    public void WorkspaceViews_ApplyWhileFocusDirty_UsesLeaveContract_NoSilentLoss()
    {
        string liveRoot = QuickNotesDbContext.LiveProfileDirectory;
        string[] liveBefore = SnapshotLiveProfile(liveRoot);

        string tempProfile = Path.Combine(Path.GetTempPath(), "qn_views_dirty_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempProfile);
        try
        {
            RunInSta(() =>
            {
                ThemeService.ApplyThemeColors(isDark: false);
                var scheduler = new FakeTrackingSyncScheduler();
                using var fixture = new TestProfileComposition(tempProfile, seedNotes: 3, ownsDirectory: false);
                var vm = fixture.CreateMainViewModel(syncScheduler: scheduler);

                vm.RequestOpenSavedViewEditor = editor =>
                {
                    editor.ViewName = "Вид";
                    return true;
                };
                vm.SaveCurrentViewCommand.Execute(null);
                var view = vm.SavedViews[0];

                vm.RequestUnsavedEditorDecision = () => UnsavedEditorDecision.Stay;
                vm.RequestDraftDiscardDecision = () => true;

                var card = vm.Notes[0];
                vm.SelectedNote = card;
                Assert.True(vm.EnterFocus(card));
                var editor = vm.DetailEditor!;
                editor.Text = "Черновик " + Guid.NewGuid().ToString("N");
                Assert.True(editor.HasUnsavedChanges);

                int revisionsBefore = CountRevisions(vm);
                scheduler.EnqueueLocalChangeCallCount = 0;

                // User chooses Stay: applying the view must not lose the draft.
                Assert.False(vm.ApplySavedView(view));
                Assert.True(vm.IsFocusMode);
                Assert.Same(editor, vm.DetailEditor);
                Assert.True(editor.HasUnsavedChanges);
                Assert.Equal(revisionsBefore, CountRevisions(vm));
                Assert.Equal(0, scheduler.EnqueueLocalChangeCallCount);
            });

            Assert.Equal(liveBefore, SnapshotLiveProfile(liveRoot));
        }
        finally
        {
            SqliteTestUtil.TryDeleteDirectory(tempProfile);
        }
    }

    [Fact]
    public async Task WorkspaceViews_SettingsSave_DoesNotDropSavedViewsOrNavState()
    {
        string tempProfile = Path.Combine(Path.GetTempPath(), "qn_views_settings_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempProfile);
        try
        {
            string settingsPath = Path.Combine(tempProfile, "settings.json");
            var settingsService = new SettingsService(settingsPath, _ => { });

            settingsService.CurrentSettings.SavedWorkspaceViews = new List<SavedWorkspaceView>
            {
                new() { Name = "Вид 1", Section = NavigationSection.Inbox, SortMode = NoteSortMode.CreatedAt },
                new() { Name = "Вид 2", ViewMode = WorkspaceViewMode.Board, CompactCards = true }
            };
            settingsService.CurrentSettings.WorkspaceViewMode = WorkspaceViewMode.Board;
            settingsService.CurrentSettings.NavigationPanelState = NavigationPanelState.Compact;
            settingsService.SaveSettings(settingsService.CurrentSettings);

            var vm = SettingsViewModelTestComposition.Create(
                settingsService,
                new GlobalHotkeyService((_, _, _, _) => true, (_, _) => true, IntPtr.Zero),
                showMessage: (_, _, _) => { });

            await vm.SaveAsync();

            var reloaded = new SettingsService(settingsPath, _ => { }).CurrentSettings;
            Assert.Equal(2, reloaded.SavedWorkspaceViews.Count);
            Assert.Contains(reloaded.SavedWorkspaceViews, v => v.Name == "Вид 1");
            Assert.Contains(reloaded.SavedWorkspaceViews, v => v.Name == "Вид 2" && v.ViewMode == WorkspaceViewMode.Board && v.CompactCards);
            Assert.Equal(WorkspaceViewMode.Board, reloaded.WorkspaceViewMode);
            Assert.Equal(NavigationPanelState.Compact, reloaded.NavigationPanelState);
        }
        finally
        {
            SqliteTestUtil.TryDeleteDirectory(tempProfile);
        }
    }

    [Fact]
    public void WorkspaceViews_PersistAndReloadAcrossRestart()
    {
        string liveRoot = QuickNotesDbContext.LiveProfileDirectory;
        string[] liveBefore = SnapshotLiveProfile(liveRoot);

        string tempProfile = Path.Combine(Path.GetTempPath(), "qn_views_restart_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempProfile);
        try
        {
            RunInSta(() =>
            {
                ThemeService.ApplyThemeColors(isDark: false);

                using (var first = new TestProfileComposition(tempProfile, seedNotes: 3, ownsDirectory: false))
                {
                    var (_, vm) = first;
                    vm.RequestOpenSavedViewEditor = editor =>
                    {
                        editor.ViewName = "Сохранённый";
                        return true;
                    };
                    vm.SaveCurrentViewCommand.Execute(null);
                    Assert.Single(vm.SavedViews);
                }

                using var restarted = new TestProfileComposition(tempProfile, seedNotes: 3, ownsDirectory: false);
                var (_, restartedVm) = restarted;
                Assert.Single(restartedVm.SavedViews);
                Assert.Equal("Сохранённый", restartedVm.SavedViews[0].Name);
            });

            Assert.Equal(liveBefore, SnapshotLiveProfile(liveRoot));
        }
        finally
        {
            SqliteTestUtil.TryDeleteDirectory(tempProfile);
        }
    }

    [Fact]
    public void WorkspaceViews_Ui_HasAccessibleNamesAndCreateAction()
    {
        string liveRoot = QuickNotesDbContext.LiveProfileDirectory;
        string[] liveBefore = SnapshotLiveProfile(liveRoot);

        string tempProfile = Path.Combine(Path.GetTempPath(), "qn_views_ui_" + Guid.NewGuid().ToString("N"));
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
                vm.IsNarrow = false;
                window.UpdateWorkspaceLayout();

                var content = (FrameworkElement)window.Content;
                content.Measure(new Size(1100, 720));
                content.Arrange(new Rect(0, 0, 1100, 720));
                content.UpdateLayout();
                ThreePaneUiSmokeRunner.DoEvents();

                Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(window.SavedViewsList)));
                Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(window.SaveCurrentViewButton)));
                Assert.NotNull(window.SaveCurrentViewButton.ToolTip);
                Assert.True(window.SaveCurrentViewButton.IsEnabled);

                // Create one view so the per-view delete control is realized in the template.
                vm.RequestOpenSavedViewEditor = editor =>
                {
                    editor.ViewName = "Пробный вид";
                    return true;
                };
                vm.SaveCurrentViewCommand.Execute(null);
                Assert.Single(vm.SavedViews);

                content.UpdateLayout();
                ThreePaneUiSmokeRunner.DoEvents();

                // The delete control deletes the view, not the notes; it is not the create button.
                var deleteButton = FindVisualChildren<Button>(window.SavedViewsList)
                    .FirstOrDefault(b => string.Equals(b.Name, "DeleteSavedViewButton", StringComparison.Ordinal));
                Assert.NotNull(deleteButton);
                Assert.NotSame(window.SaveCurrentViewButton, deleteButton);
                Assert.Equal("Удалить вид", deleteButton!.Content?.ToString());
                Assert.Equal("Удалить сохранённый вид", AutomationProperties.GetName(deleteButton));
                Assert.False(string.IsNullOrWhiteSpace(deleteButton.ToolTip?.ToString()));

                // Delete confirmation copy must state that the notes are not removed.
                string? confirmMessage = null;
                vm.ConfirmHandler = (message, _, _, _) =>
                {
                    confirmMessage = message;
                    return MessageBoxResult.Cancel;
                };
                vm.DeleteSavedViewCommand.Execute(vm.SavedViews[0]);
                Assert.NotNull(confirmMessage);
                Assert.Contains("не будут удалены", confirmMessage);
                Assert.Single(vm.SavedViews);
            });

            Assert.Equal(liveBefore, SnapshotLiveProfile(liveRoot));
        }
        finally
        {
            SqliteTestUtil.TryDeleteDirectory(tempProfile);
        }
    }

    [Fact]
    public void WorkspaceViews_ListModeWorksWithNoViews()
    {
        string liveRoot = QuickNotesDbContext.LiveProfileDirectory;
        string[] liveBefore = SnapshotLiveProfile(liveRoot);

        string tempProfile = Path.Combine(Path.GetTempPath(), "qn_views_empty_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempProfile);
        try
        {
            RunInSta(() =>
            {
                ThemeService.ApplyThemeColors(isDark: false);
                using var fixture = new TestProfileComposition(tempProfile, seedNotes: 3, ownsDirectory: false);
                var (window, vm) = fixture;

                window.Measure(new Size(1100, 720));
                window.Arrange(new Rect(0, 0, 1100, 720));
                window.UpdateLayout();

                Assert.Empty(vm.SavedViews);
                Assert.False(vm.HasSavedViews);
                Assert.Equal(WorkspaceViewMode.List, vm.WorkspaceViewMode);
                Assert.Equal(Visibility.Visible, window.NotesListGrid.Visibility);
                Assert.Equal(Visibility.Visible, window.DetailPaneBorder.Visibility);
                Assert.NotEmpty(vm.Notes);

                // Applying a null view is a safe no-op.
                Assert.False(vm.ApplySavedView(null));
            });

            Assert.Equal(liveBefore, SnapshotLiveProfile(liveRoot));
        }
        finally
        {
            SqliteTestUtil.TryDeleteDirectory(tempProfile);
        }
    }

    private static TagTreeItemViewModel? FindTagNode(MainViewModel vm, int tagId)
    {
        static TagTreeItemViewModel? Find(IEnumerable<TagTreeItemViewModel> nodes, int id)
        {
            foreach (var node in nodes)
            {
                if (node.Id == id)
                {
                    return node;
                }

                var nested = Find(node.Children, id);
                if (nested != null)
                {
                    return nested;
                }
            }

            return null;
        }

        return Find(vm.TagTreeRoots, tagId);
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent)
        where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
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
}
