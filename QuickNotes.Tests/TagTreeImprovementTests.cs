using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using QuickNotes.App.Data;
using QuickNotes.App.Helpers;
using QuickNotes.App.Models;
using QuickNotes.App.Services;
using QuickNotes.App.ViewModels;
using Xunit;

namespace QuickNotes.Tests;

[TestCategory(TestCategories.Integration)]
public class TagTreeImprovementTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<QuickNotesDbContext> _options;
    private readonly string _tempSettingsPath;
    private readonly List<IDisposable> _disposables = new();

    public TagTreeImprovementTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        _options = new DbContextOptionsBuilder<QuickNotesDbContext>()
            .UseSqlite(_connection)
            .Options;

        using var db = new QuickNotesDbContext(_options);
        DbInitializer.Initialize(db);

        _tempSettingsPath = Path.Combine(Path.GetTempPath(), $"quicknotes-test-{Guid.NewGuid():N}.json");
    }

    public void Dispose()
    {
        foreach (var d in _disposables)
        {
            try { d.Dispose(); } catch { }
        }
        _connection.Dispose();
        if (File.Exists(_tempSettingsPath))
        {
            try { File.Delete(_tempSettingsPath); } catch { }
        }
    }

    private MainViewModel CreateViewModel()
    {
        var settingsService = new SettingsService(_tempSettingsPath, _ => { });
        var hotkey = new GlobalHotkeyService((_, _, _, _) => true, (_, _) => true, IntPtr.Zero);
        var tray = new TrayIconService();
        _disposables.Add(hotkey);
        _disposables.Add(tray);
        var debouncer = new SearchDebouncer(0, a => a());
        _disposables.Add(debouncer);

        var vm = MainViewModelTestComposition.Create(
            () => new QuickNotesDbContext(_options),
            new TagDetectionService(),
            new SearchService(),
            settingsService,
            hotkey,
            new ClipboardCaptureService(),
            tray,
            new BackupService(),
            debouncer,
            draftJournalService: NoOpDraftJournalService.Instance);
        _disposables.Add(vm);
        return vm;
    }

    [Fact]
    public void CalculateTagNoteCounts_WithDescendants_AggregatesAndDeduplicates()
    {
        // Hierarchy: Dev (1) -> Backend (2) -> Database (3)
        //            Dev (1) -> Frontend (4)
        var dev = new Tag { Id = 1, Name = "Dev", ParentTagId = null };
        var backend = new Tag { Id = 2, Name = "Backend", ParentTagId = 1 };
        var database = new Tag { Id = 3, Name = "Database", ParentTagId = 2 };
        var frontend = new Tag { Id = 4, Name = "Frontend", ParentTagId = 1 };
        var allTags = new List<Tag> { dev, backend, database, frontend };

        // Note 10: tagged Database
        // Note 20: tagged Backend
        // Note 30: tagged Dev
        // Note 40: tagged Dev AND Database (multi-level tag on same note)
        // Note 50: tagged Frontend
        var activePairs = new List<(int TagId, int NoteId)>
        {
            (3, 10),
            (2, 20),
            (1, 30),
            (1, 40),
            (3, 40),
            (4, 50)
        };

        var counts = TagHierarchyService.CalculateTagNoteCounts(allTags, activePairs);

        // Database has Notes 10, 40 -> count 2
        Assert.Equal(2, counts[3]);

        // Backend has Notes 10 (via DB), 20, 40 (via DB) -> count 3
        Assert.Equal(3, counts[2]);

        // Frontend has Note 50 -> count 1
        Assert.Equal(1, counts[4]);

        // Dev has Notes 10 (via DB), 20 (via Backend), 30, 40 (counted once even with 2 tags), 50 (via Frontend) -> count 5
        Assert.Equal(5, counts[1]);
    }

    [Fact]
    public void CalculateTagNoteCounts_ExcludesDeletedNotes()
    {
        var tag = new Tag { Id = 1, Name = "Work" };
        var allTags = new List<Tag> { tag };

        var activeNote = new Note { Id = 1, Text = "Active note", DeletedAt = null };
        var deletedNote = new Note { Id = 2, Text = "Deleted note", DeletedAt = DateTime.Now };

        var noteTags = new List<NoteTag>
        {
            new() { TagId = 1, NoteId = 1, Note = activeNote, IsSuppressed = false },
            new() { TagId = 1, NoteId = 2, Note = deletedNote, IsSuppressed = false }
        };

        var counts = TagHierarchyService.CalculateTagNoteCounts(allTags, noteTags);

        Assert.Equal(1, counts[1]);
    }

    [Fact]
    public void CalculateTagNoteCounts_ExcludesSuppressedAutoTags()
    {
        var parent = new Tag { Id = 1, Name = "Parent" };
        var child = new Tag { Id = 2, Name = "Child", ParentTagId = 1 };
        var allTags = new List<Tag> { parent, child };

        var note1 = new Note { Id = 1, Text = "Note 1", DeletedAt = null };
        var note2 = new Note { Id = 2, Text = "Note 2", DeletedAt = null };

        var noteTags = new List<NoteTag>
        {
            // Note 1 has child tag suppressed -> should NOT count for child or parent
            new() { TagId = 2, NoteId = 1, Note = note1, IsSuppressed = true },
            // Note 2 has child tag active -> counts for both child and parent
            new() { TagId = 2, NoteId = 2, Note = note2, IsSuppressed = false }
        };

        var counts = TagHierarchyService.CalculateTagNoteCounts(allTags, noteTags);

        Assert.Equal(1, counts[2]);
        Assert.Equal(1, counts[1]);
    }

    [Fact]
    public void BreadcrumbAndFullPath_FormatsHierarchiesCorrectly()
    {
        var it = new Tag { Id = 1, Name = "IT", ParentTagId = null };
        var db = new Tag { Id = 2, Name = "Database", ParentTagId = 1 };
        var pg = new Tag { Id = 3, Name = "PostgreSQL", ParentTagId = 2 };
        var allTags = new List<Tag> { it, db, pg };

        // Root
        var rootBreadcrumb = TagHierarchyService.GetBreadcrumbPath(1, allTags);
        Assert.Equal(new[] { "IT" }, rootBreadcrumb);
        Assert.Equal("IT", TagHierarchyService.GetFullPath(1, allTags));

        // Mid-level
        var midBreadcrumb = TagHierarchyService.GetBreadcrumbPath(2, allTags);
        Assert.Equal(new[] { "IT", "Database" }, midBreadcrumb);
        Assert.Equal("IT / Database", TagHierarchyService.GetFullPath(2, allTags));

        // Leaf
        var leafBreadcrumb = TagHierarchyService.GetBreadcrumbPath(3, allTags);
        Assert.Equal(new[] { "IT", "Database", "PostgreSQL" }, leafBreadcrumb);
        Assert.Equal("IT / Database / PostgreSQL", TagHierarchyService.GetFullPath(3, allTags));
    }

    [Fact]
    public void Breadcrumb_CorruptedTreeWithCycles_TerminatesSafely()
    {
        // Direct cycle: 1 -> 2 -> 1
        var tag1 = new Tag { Id = 1, Name = "Loop1", ParentTagId = 2 };
        var tag2 = new Tag { Id = 2, Name = "Loop2", ParentTagId = 1 };

        // Self loop: 3 -> 3
        var tag3 = new Tag { Id = 3, Name = "SelfLoop", ParentTagId = 3 };

        // Broken reference: 4 -> 999 (non-existent)
        var tag4 = new Tag { Id = 4, Name = "Dangling", ParentTagId = 999 };

        var allTags = new List<Tag> { tag1, tag2, tag3, tag4 };

        // Must terminate without hanging or StackOverflowException
        var path1 = TagHierarchyService.GetBreadcrumbPath(1, allTags);
        Assert.NotEmpty(path1);
        Assert.True(path1.Count <= 2);

        var path3 = TagHierarchyService.GetBreadcrumbPath(3, allTags);
        Assert.Single(path3);
        Assert.Equal("SelfLoop", path3[0]);

        var path4 = TagHierarchyService.GetBreadcrumbPath(4, allTags);
        Assert.Single(path4);
        Assert.Equal("Dangling", path4[0]);
    }

    [Fact]
    public void CanReparent_EnforcesReparentingRulesAndCycleGuard()
    {
        // Hierarchy: Root (1) -> Child (2) -> Grandchild (3), Standalone (4)
        var root = new Tag { Id = 1, Name = "Root", ParentTagId = null };
        var child = new Tag { Id = 2, Name = "Child", ParentTagId = 1 };
        var grandchild = new Tag { Id = 3, Name = "Grandchild", ParentTagId = 2 };
        var standalone = new Tag { Id = 4, Name = "Standalone", ParentTagId = null };
        var allTags = new List<Tag> { root, child, grandchild, standalone };

        // 1. Cannot drop tag onto itself
        Assert.False(TagHierarchyService.CanReparent(1, 1, allTags));
        Assert.False(TagHierarchyService.CanReparent(2, 2, allTags));

        // 2. Cannot drop tag onto its own child or descendant (cycle)
        Assert.False(TagHierarchyService.CanReparent(1, 2, allTags));
        Assert.False(TagHierarchyService.CanReparent(1, 3, allTags));
        Assert.False(TagHierarchyService.CanReparent(2, 3, allTags));

        // 3. Cannot drop tag onto its current parent (no-op)
        Assert.False(TagHierarchyService.CanReparent(2, 1, allTags));
        Assert.False(TagHierarchyService.CanReparent(3, 2, allTags));
        Assert.False(TagHierarchyService.CanReparent(1, null, allTags)); // already root

        // 4. Can drop child tag onto root (null)
        Assert.True(TagHierarchyService.CanReparent(2, null, allTags));
        Assert.True(TagHierarchyService.CanReparent(3, null, allTags));

        // 5. Can drop child tag onto another valid parent
        Assert.True(TagHierarchyService.CanReparent(2, 4, allTags));
        Assert.True(TagHierarchyService.CanReparent(3, 1, allTags));

        // 6. Non-existent source or target returns false
        Assert.False(TagHierarchyService.CanReparent(999, 1, allTags));
        Assert.False(TagHierarchyService.CanReparent(1, 999, allTags));
    }

    [Fact]
    public void MainViewModel_MoveTag_ExecutesReparent_UpdatesDbAndModel()
    {
        using (var db = new QuickNotesDbContext(_options))
        {
            var p1 = new Tag { Id = 1, Name = "Parent1" };
            var p2 = new Tag { Id = 2, Name = "Parent2" };
            var c = new Tag { Id = 3, Name = "Child", ParentTagId = 1 };
            db.Tags.AddRange(p1, p2, c);
            db.SaveChanges();
        }

        var vm = CreateViewModel();

        var p1Node = vm.TagTreeRoots.First(r => r.Id == 1);
        Assert.Single(p1Node.Children);
        Assert.Equal("Parent1 / Child", p1Node.Children[0].FullPath);

        // Move Child (3) under Parent2 (2)
        bool moved = vm.MoveTag(3, 2);
        Assert.True(moved);

        // Verify Database
        using (var db = new QuickNotesDbContext(_options))
        {
            var childDb = db.Tags.Find(3);
            Assert.NotNull(childDb);
            Assert.Equal(2, childDb.ParentTagId);
        }

        // Verify ViewModel tree
        p1Node = vm.TagTreeRoots.First(r => r.Id == 1);
        var p2Node = vm.TagTreeRoots.First(r => r.Id == 2);
        Assert.Empty(p1Node.Children);
        Assert.Single(p2Node.Children);
        Assert.Equal("Parent2 / Child", p2Node.Children[0].FullPath);
    }

    [Fact]
    public void MainViewModel_MoveTag_CycleRejection_DoesNotModifyDatabaseOrModel()
    {
        using (var db = new QuickNotesDbContext(_options))
        {
            var p = new Tag { Id = 1, Name = "Parent" };
            var c = new Tag { Id = 2, Name = "Child", ParentTagId = 1 };
            db.Tags.AddRange(p, c);
            db.SaveChanges();
        }

        var vm = CreateViewModel();

        // Attempt invalid move: Parent (1) under Child (2)
        bool moved = vm.MoveTag(1, 2);
        Assert.False(moved);

        // Verify Database was not modified
        using (var db = new QuickNotesDbContext(_options))
        {
            var parentDb = db.Tags.Find(1);
            Assert.NotNull(parentDb);
            Assert.Null(parentDb.ParentTagId);
        }

        // Verify ViewModel tree was not corrupted
        var pNode = vm.TagTreeRoots.First(r => r.Id == 1);
        Assert.Null(pNode.ParentId);
        Assert.Single(pNode.Children);
        Assert.Equal(2, pNode.Children[0].Id);
    }

    [Fact]
    public void MainViewModel_TagOperations_UpdateCountersAndPaths()
    {
        using (var db = new QuickNotesDbContext(_options))
        {
            var category = new Tag { Id = 1, Name = "Category" };
            var subcat = new Tag { Id = 2, Name = "Subcategory", ParentTagId = 1 };
            db.Tags.AddRange(category, subcat);

            // Note in Subcategory
            var note = new Note { Text = "Test Note" };
            note.NoteTags.Add(new NoteTag { TagId = 2, IsSuppressed = false });
            db.Notes.Add(note);
            db.SaveChanges();
        }

        var vm = CreateViewModel();

        var catNode = vm.TagTreeRoots.First(r => r.Id == 1);
        var subNode = catNode.Children.First(r => r.Id == 2);

        // Counters before
        Assert.Equal(1, subNode.NoteCount);
        Assert.Equal(1, catNode.NoteCount);
        Assert.Equal("Category / Subcategory", subNode.FullPath);

        // Rename Category to "Section"
        using (var db = new QuickNotesDbContext(_options))
        {
            var catDb = db.Tags.Find(1);
            catDb!.Name = "Section";
            db.SaveChanges();
        }
        vm.ReloadTags();

        catNode = vm.TagTreeRoots.First(r => r.Id == 1);
        subNode = catNode.Children.First(r => r.Id == 2);
        Assert.Equal("Section / Subcategory", subNode.FullPath);

        // Trashing note updates counters to 0
        int noteId;
        using (var db = new QuickNotesDbContext(_options))
        {
            var noteToTrash = db.Notes.First();
            noteId = noteToTrash.Id;
            noteToTrash.DeletedAt = DateTime.Now;
            db.SaveChanges();
        }
        vm.RefreshNotes();

        Assert.Equal(0, subNode.NoteCount);
        Assert.Equal(0, catNode.NoteCount);

        // Restoring note updates counters back to 1
        vm.RestoreNote(noteId);

        Assert.Equal(1, subNode.NoteCount);
        Assert.Equal(1, catNode.NoteCount);
    }
}
