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
public class TagMergeTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<QuickNotesDbContext> _options;
    private readonly string _tempSettingsPath;
    private readonly List<IDisposable> _disposables = new();
    private readonly TagMergeService _mergeService = new();

    public TagMergeTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        _options = new DbContextOptionsBuilder<QuickNotesDbContext>()
            .UseSqlite(_connection)
            .Options;

        using var db = new QuickNotesDbContext(_options);
        DbInitializer.Initialize(db);

        _tempSettingsPath = Path.Combine(Path.GetTempPath(), $"quicknotes-merge-test-{Guid.NewGuid():N}.json");
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
            _mergeService,
            draftJournalService: NoOpDraftJournalService.Instance);
        _disposables.Add(vm);
        return vm;
    }

    [Fact]
    public void CalculatePreview_CalculatesCorrectMetrics_ExcludingDeletedAndSuppressedFromActiveNotes()
    {
        using (var db = new QuickNotesDbContext(_options))
        {
            var source = new Tag { Id = 10, Name = "SourceTag" };
            var target = new Tag { Id = 20, Name = "TargetTag" };
            var child1 = new Tag { Id = 30, Name = "Child1", ParentTagId = 10 };
            var child2 = new Tag { Id = 31, Name = "Child2", ParentTagId = 10 };
            db.Tags.AddRange(source, target, child1, child2);

            // Synonyms on source: 3 total ("react", "redux", "js")
            db.TagSynonyms.AddRange(
                new TagSynonym { TagId = 10, Value = "react" },
                new TagSynonym { TagId = 10, Value = "redux" },
                new TagSynonym { TagId = 10, Value = "js" }
            );

            // Synonyms on target: "JS" (case-insensitive duplicate of "js")
            db.TagSynonyms.Add(new TagSynonym { TagId = 20, Value = "JS" });

            // Notes:
            // Note 1: Active note, tagged with Source only -> active affected
            var n1 = new Note { Id = 1, Text = "N1", DeletedAt = null };
            n1.NoteTags.Add(new NoteTag { TagId = 10, IsSuppressed = false });

            // Note 2: Active note, tagged with BOTH Source and Target -> active affected AND duplicate
            var n2 = new Note { Id = 2, Text = "N2", DeletedAt = null };
            n2.NoteTags.Add(new NoteTag { TagId = 10, IsSuppressed = false });
            n2.NoteTags.Add(new NoteTag { TagId = 20, IsSuppressed = false });

            // Note 3: Deleted note (in Trash), tagged with Source -> NOT active note!
            var n3 = new Note { Id = 3, Text = "N3", DeletedAt = DateTime.Now };
            n3.NoteTags.Add(new NoteTag { TagId = 10, IsSuppressed = false });

            // Note 4: Active note, but Source tag link is suppressed -> NOT active tag occurrence!
            var n4 = new Note { Id = 4, Text = "N4", DeletedAt = null };
            n4.NoteTags.Add(new NoteTag { TagId = 10, IsSuppressed = true });

            // Note 5: Active note, tagged with Target only -> not affected
            var n5 = new Note { Id = 5, Text = "N5", DeletedAt = null };
            n5.NoteTags.Add(new NoteTag { TagId = 20, IsSuppressed = false });

            db.Notes.AddRange(n1, n2, n3, n4, n5);
            db.SaveChanges();
        }

        using (var db = new QuickNotesDbContext(_options))
        {
            var allTags = db.Tags.Include(t => t.Synonyms).ToList();
            var preview = _mergeService.CalculatePreview(db, 10, 20, allTags);

            Assert.True(preview.CanMerge);
            // Notes 1 and 2 are active affected notes (Note 3 is in Trash, Note 4 is suppressed)
            Assert.Equal(2, preview.AffectedActiveNotesCount);
            Assert.Equal(2, preview.ActiveNotesCount);

            // Note 2 is tagged with both Source and Target
            Assert.Equal(1, preview.DuplicateNoteTagsCount);
            Assert.Equal(1, preview.DuplicateLinksCount);

            // Total synonyms on source = 3 ("react", "redux", "js")
            Assert.Equal(3, preview.SourceSynonymsCount);
            Assert.Equal(3, preview.SynonymsCount);

            // Unique synonyms to transfer = 2 ("react", "redux"; "js" is duplicate of "JS")
            Assert.Equal(2, preview.UniqueSynonymsCount);
            Assert.Equal(2, preview.SynonymsToTransferCount);

            // 2 child tags (Child1, Child2)
            Assert.Equal(2, preview.ChildTagsCount);
            Assert.Contains("2", preview.StatusExplanation);
        }
    }

    [Fact]
    public void DescendantTarget_IsExcludedFromAvailableTargets_AndBlockedFromMerging()
    {
        // Hierarchy:
        // Root (1) -> Child (2) -> Grandchild (3)
        // Standalone (4)
        var root = new Tag { Id = 1, Name = "Root" };
        var child = new Tag { Id = 2, Name = "Child", ParentTagId = 1 };
        var grandchild = new Tag { Id = 3, Name = "Grandchild", ParentTagId = 2 };
        var standalone = new Tag { Id = 4, Name = "Standalone" };
        var allTags = new List<Tag> { root, child, grandchild, standalone };

        // 1. Available targets for Root (1) must NOT include 1, 2, or 3
        var targetsForRoot = _mergeService.GetAvailableTargets(1, allTags);
        Assert.DoesNotContain(targetsForRoot, t => t.Id == 1);
        Assert.DoesNotContain(targetsForRoot, t => t.Id == 2);
        Assert.DoesNotContain(targetsForRoot, t => t.Id == 3);
        Assert.Contains(targetsForRoot, t => t.Id == 4);

        // 2. Available targets for Child (2) must NOT include 2 or 3, but CAN include Root (1) or Standalone (4)
        var targetsForChild = _mergeService.GetAvailableTargets(2, allTags);
        Assert.DoesNotContain(targetsForChild, t => t.Id == 2);
        Assert.DoesNotContain(targetsForChild, t => t.Id == 3);
        Assert.Contains(targetsForChild, t => t.Id == 1);
        Assert.Contains(targetsForChild, t => t.Id == 4);

        // 3. CanMerge checks
        Assert.False(_mergeService.CanMerge(1, 1, allTags, out var reasonSelf));
        Assert.Contains("самим собой", reasonSelf);

        Assert.False(_mergeService.CanMerge(1, 2, allTags, out var reasonChild));
        Assert.Contains("потомком", reasonChild);

        Assert.False(_mergeService.CanMerge(1, 3, allTags, out var reasonGrandchild));
        Assert.Contains("потомком", reasonGrandchild);

        Assert.True(_mergeService.CanMerge(2, 1, allTags, out _));
        Assert.True(_mergeService.CanMerge(1, 4, allTags, out _));

        // 4. MergeTags throws on descendant target
        using (var db = new QuickNotesDbContext(_options))
        {
            db.Tags.AddRange(root, child, grandchild, standalone);
            db.SaveChanges();

            var ex = Assert.Throws<InvalidOperationException>(() =>
                _mergeService.MergeTags(db, 1, 2, allTags));
            Assert.Contains("потомком", ex.Message);
        }
    }

    [Fact]
    public void MergeTags_DeduplicatesNoteTags_WithActiveAndManualPriority()
    {
        // Conflict resolution requirements:
        // "сохраняя активную/ручную связь при конфликте и не оставляя подавленную связь поверх активной"
        using (var db = new QuickNotesDbContext(_options))
        {
            var source = new Tag { Id = 1, Name = "Source" };
            var target = new Tag { Id = 2, Name = "Target" };
            db.Tags.AddRange(source, target);

            // Note 1: Source Active Auto, Target Suppressed Auto -> Result: Active Auto
            var n1 = new Note { Id = 1, Text = "N1" };
            n1.NoteTags.Add(new NoteTag { TagId = 1, Origin = TagOrigin.Auto, IsSuppressed = false });
            n1.NoteTags.Add(new NoteTag { TagId = 2, Origin = TagOrigin.Auto, IsSuppressed = true });

            // Note 2: Source Suppressed Auto, Target Active Auto -> Result: Active Auto
            var n2 = new Note { Id = 2, Text = "N2" };
            n2.NoteTags.Add(new NoteTag { TagId = 1, Origin = TagOrigin.Auto, IsSuppressed = true });
            n2.NoteTags.Add(new NoteTag { TagId = 2, Origin = TagOrigin.Auto, IsSuppressed = false });

            // Note 3: Source Active Auto, Target Active Manual -> Result: Active Manual
            var n3 = new Note { Id = 3, Text = "N3" };
            n3.NoteTags.Add(new NoteTag { TagId = 1, Origin = TagOrigin.Auto, IsSuppressed = false });
            n3.NoteTags.Add(new NoteTag { TagId = 2, Origin = TagOrigin.Manual, IsSuppressed = false });

            // Note 4: Source Active Manual, Target Active Auto -> Result: Active Manual
            var n4 = new Note { Id = 4, Text = "N4" };
            n4.NoteTags.Add(new NoteTag { TagId = 1, Origin = TagOrigin.Manual, IsSuppressed = false });
            n4.NoteTags.Add(new NoteTag { TagId = 2, Origin = TagOrigin.Auto, IsSuppressed = false });

            // Note 5: Source Suppressed Manual, Target Active Auto -> Result: Active Manual
            var n5 = new Note { Id = 5, Text = "N5" };
            n5.NoteTags.Add(new NoteTag { TagId = 1, Origin = TagOrigin.Manual, IsSuppressed = true });
            n5.NoteTags.Add(new NoteTag { TagId = 2, Origin = TagOrigin.Auto, IsSuppressed = false });

            // Note 6: Source Suppressed Auto, Target Suppressed Auto -> Result: Suppressed Auto
            var n6 = new Note { Id = 6, Text = "N6" };
            n6.NoteTags.Add(new NoteTag { TagId = 1, Origin = TagOrigin.Auto, IsSuppressed = true });
            n6.NoteTags.Add(new NoteTag { TagId = 2, Origin = TagOrigin.Auto, IsSuppressed = true });

            // Note 7: Only on Source (Active Manual) -> Result: Active Manual transferred to Target
            var n7 = new Note { Id = 7, Text = "N7" };
            n7.NoteTags.Add(new NoteTag { TagId = 1, Origin = TagOrigin.Manual, IsSuppressed = false });

            // Note 8: Deleted note in Trash with duplicate (Source Suppressed Auto, Target Active Manual)
            // Result: Active Manual on Target, note remains in Trash
            var n8 = new Note { Id = 8, Text = "N8", DeletedAt = DateTime.Now.AddDays(-1) };
            n8.NoteTags.Add(new NoteTag { TagId = 1, Origin = TagOrigin.Auto, IsSuppressed = true });
            n8.NoteTags.Add(new NoteTag { TagId = 2, Origin = TagOrigin.Manual, IsSuppressed = false });

            db.Notes.AddRange(n1, n2, n3, n4, n5, n6, n7, n8);
            db.SaveChanges();
        }

        using (var db = new QuickNotesDbContext(_options))
        {
            var result = _mergeService.MergeTags(db, 1, 2);
            Assert.True(result.Success);
            Assert.Equal(1, result.NotesMigratedCount); // Note 7
            Assert.Equal(7, result.DuplicatesResolvedCount); // Notes 1, 2, 3, 4, 5, 6, 8
        }

        // Verify in fresh context
        using (var db = new QuickNotesDbContext(_options))
        {
            // Source tag was deleted
            Assert.Null(db.Tags.Find(1));
            // Target tag still exists
            Assert.NotNull(db.Tags.Find(2));

            // No links to source tag remain
            Assert.Empty(db.NoteTags.Where(nt => nt.TagId == 1));

            // Check each note's link on Target
            var links = db.NoteTags.Where(nt => nt.TagId == 2).ToDictionary(nt => nt.NoteId);

            // N1: Active Auto
            Assert.False(links[1].IsSuppressed);
            Assert.Equal(TagOrigin.Auto, links[1].Origin);

            // N2: Active Auto
            Assert.False(links[2].IsSuppressed);
            Assert.Equal(TagOrigin.Auto, links[2].Origin);

            // N3: Active Manual
            Assert.False(links[3].IsSuppressed);
            Assert.Equal(TagOrigin.Manual, links[3].Origin);

            // N4: Active Manual
            Assert.False(links[4].IsSuppressed);
            Assert.Equal(TagOrigin.Manual, links[4].Origin);

            // N5: Active Manual
            Assert.False(links[5].IsSuppressed);
            Assert.Equal(TagOrigin.Manual, links[5].Origin);

            // N6: Suppressed Auto
            Assert.True(links[6].IsSuppressed);
            Assert.Equal(TagOrigin.Auto, links[6].Origin);

            // N7: Active Manual
            Assert.False(links[7].IsSuppressed);
            Assert.Equal(TagOrigin.Manual, links[7].Origin);

            // N8: Active Manual, DeletedAt still set
            Assert.False(links[8].IsSuppressed);
            Assert.Equal(TagOrigin.Manual, links[8].Origin);
            var note8 = db.Notes.Find(8);
            Assert.NotNull(note8?.DeletedAt);
        }
    }

    [Fact]
    public void MergeTags_TransfersUniqueSynonyms_CaseInsensitiveDeduplication()
    {
        using (var db = new QuickNotesDbContext(_options))
        {
            var source = new Tag { Id = 1, Name = "Frontend" };
            var target = new Tag { Id = 2, Name = "UI" };
            db.Tags.AddRange(source, target);

            // Target has: "web", "UI" (matches target name)
            db.TagSynonyms.Add(new TagSynonym { TagId = 2, Value = "web" });

            // Source has:
            // "WEB" (case-insensitive duplicate of target's "web" -> skip)
            // "ui" (case-insensitive duplicate of target's name "UI" -> skip)
            // "CSS" (unique -> transfer)
            // "css" (duplicate of "CSS" in source -> transfer only once)
            // "JavaScript" (unique -> transfer)
            db.TagSynonyms.AddRange(
                new TagSynonym { TagId = 1, Value = "WEB" },
                new TagSynonym { TagId = 1, Value = "ui" },
                new TagSynonym { TagId = 1, Value = "CSS" },
                new TagSynonym { TagId = 1, Value = "css" },
                new TagSynonym { TagId = 1, Value = "JavaScript" }
            );

            db.SaveChanges();
        }

        using (var db = new QuickNotesDbContext(_options))
        {
            var result = _mergeService.MergeTags(db, 1, 2);
            Assert.True(result.Success);
            Assert.Equal(2, result.SynonymsTransferredCount); // "CSS" and "JavaScript"
        }

        using (var db = new QuickNotesDbContext(_options))
        {
            var targetSynonyms = db.TagSynonyms
                .Where(s => s.TagId == 2)
                .Select(s => s.Value)
                .ToList();

            // Must contain "web", "CSS", "JavaScript"
            Assert.Contains("web", targetSynonyms);
            Assert.Contains("CSS", targetSynonyms);
            Assert.Contains("JavaScript", targetSynonyms);
            Assert.Equal(3, targetSynonyms.Count);

            // Source synonyms deleted
            Assert.Empty(db.TagSynonyms.Where(s => s.TagId == 1));
        }
    }

    [Fact]
    public void MergeTags_WithChildTags_ReparentsChildrenSafely()
    {
        using (var db = new QuickNotesDbContext(_options))
        {
            var source = new Tag { Id = 1, Name = "Source" };
            var target = new Tag { Id = 2, Name = "Target" };
            var child1 = new Tag { Id = 3, Name = "Child1", ParentTagId = 1 };
            var child2 = new Tag { Id = 4, Name = "Child2", ParentTagId = 1 };
            var grandchild = new Tag { Id = 5, Name = "Grandchild", ParentTagId = 3 };

            db.Tags.AddRange(source, target, child1, child2, grandchild);
            db.SaveChanges();
        }

        using (var db = new QuickNotesDbContext(_options))
        {
            var result = _mergeService.MergeTags(db, 1, 2);
            Assert.True(result.Success);
            Assert.Equal(2, result.ChildTagsReparentedCount);
        }

        using (var db = new QuickNotesDbContext(_options))
        {
            Assert.Null(db.Tags.Find(1)); // Source deleted

            var c1 = db.Tags.Find(3);
            var c2 = db.Tags.Find(4);
            var gc = db.Tags.Find(5);

            Assert.NotNull(c1);
            Assert.NotNull(c2);
            Assert.NotNull(gc);

            // Direct children reparented under Target (2)
            Assert.Equal(2, c1.ParentTagId);
            Assert.Equal(2, c2.ParentTagId);

            // Grandchild remains under Child1 (3)
            Assert.Equal(3, gc.ParentTagId);
        }
    }

    [Fact]
    public void MergeTags_MissingSourceTag_ThrowsAndLeavesStateUnmodified()
    {
        using (var db = new QuickNotesDbContext(_options))
        {
            var target = new Tag { Id = 2, Name = "Target" };
            db.Tags.Add(target);
            db.SaveChanges();

            var ex = Assert.Throws<InvalidOperationException>(() =>
                _mergeService.MergeTags(db, 99999, 2));
            Assert.Contains("99999", ex.Message);

            // Target still exists and is untouched
            Assert.NotNull(db.Tags.Find(2));
        }
    }

    [Fact]
    public void MergeTags_TransactionRollback_PreservesOriginalDataOnError()
    {
        using (var db = new QuickNotesDbContext(_options))
        {
            var source = new Tag { Id = 1, Name = "Source" };
            var target = new Tag { Id = 2, Name = "Target" };
            db.Tags.AddRange(source, target);

            db.TagSynonyms.Add(new TagSynonym { TagId = 1, Value = "test-syn" });

            var note = new Note { Id = 10, Text = "Test Note" };
            note.NoteTags.Add(new NoteTag { TagId = 1, Origin = TagOrigin.Manual, IsSuppressed = false });
            db.Notes.Add(note);

            db.SaveChanges();
        }

        // Test simulated failure: try merging to non-existent target ID in CanMerge
        using (var db = new QuickNotesDbContext(_options))
        {
            Assert.Throws<InvalidOperationException>(() =>
                _mergeService.MergeTags(db, 1, 99999));
        }

        // Verify that database state was not changed at all
        using (var db = new QuickNotesDbContext(_options))
        {
            var sourceTag = db.Tags.Include(t => t.Synonyms).Include(t => t.NoteTags).FirstOrDefault(t => t.Id == 1);
            Assert.NotNull(sourceTag);
            Assert.Equal("Source", sourceTag.Name);
            Assert.Single(sourceTag.Synonyms);
            Assert.Single(sourceTag.NoteTags);
        }
    }

    [Fact]
    public void MainViewModel_ExecuteMergeTag_UpdatesTreeCountersSelectedTagAndSearch()
    {
        using (var db = new QuickNotesDbContext(_options))
        {
            var source = new Tag { Id = 1, Name = "LegacyTag" };
            var target = new Tag { Id = 2, Name = "ModernTag" };
            var child = new Tag { Id = 3, Name = "SubItem", ParentTagId = 1 };
            db.Tags.AddRange(source, target, child);

            var note1 = new Note { Id = 10, Text = "Note 10" };
            note1.NoteTags.Add(new NoteTag { TagId = 1, IsSuppressed = false });

            var note2 = new Note { Id = 20, Text = "Note 20" };
            note2.NoteTags.Add(new NoteTag { TagId = 2, IsSuppressed = false });

            db.Notes.AddRange(note1, note2);
            db.SaveChanges();
        }

        var vm = CreateViewModel();

        // Check initial state
        var sourceNode = vm.TagTreeRoots.First(r => r.Id == 1);
        var targetNode = vm.TagTreeRoots.First(r => r.Id == 2);
        Assert.Equal(1, sourceNode.NoteCount);
        Assert.Equal(1, targetNode.NoteCount);

        // Select the source tag
        vm.SelectedTag = sourceNode;
        Assert.Equal(1, vm.SelectedTag.Id);

        // Set search query containing tag:LegacyTag
        vm.SearchQuery = "tag:LegacyTag";

        // Execute merge: LegacyTag (1) into ModernTag (2)
        bool merged = vm.ExecuteMergeTag(1, 2);
        Assert.True(merged);

        // 1. Tag tree updated: SourceTag is gone, Child is now under ModernTag
        Assert.DoesNotContain(vm.TagTreeRoots, r => r.Id == 1);
        targetNode = vm.TagTreeRoots.First(r => r.Id == 2);
        Assert.Single(targetNode.Children);
        Assert.Equal(3, targetNode.Children[0].Id);
        Assert.Equal("ModernTag / SubItem", targetNode.Children[0].FullPath);

        // 2. Counters updated: ModernTag now has both Note 10 and Note 20 -> count = 2
        Assert.Equal(2, targetNode.NoteCount);

        // 3. SelectedTag switched to ModernTag
        Assert.NotNull(vm.SelectedTag);
        Assert.Equal(2, vm.SelectedTag.Id);
        Assert.Equal("ModernTag", vm.SelectedTag.Name);

        // 4. Search query updated from tag:LegacyTag to tag:ModernTag
        Assert.Equal("tag:ModernTag", vm.SearchQuery);
    }

    [Fact]
    public void TagMergeViewModel_WithAvailableTargets_CalculatesPreviewAndClosesOnConfirm()
    {
        using (var db = new QuickNotesDbContext(_options))
        {
            var source = new Tag { Id = 1, Name = "Source" };
            var target = new Tag { Id = 2, Name = "Target" };
            db.Tags.AddRange(source, target);

            var note = new Note { Id = 10, Text = "Hello" };
            note.NoteTags.Add(new NoteTag { TagId = 1, IsSuppressed = false });
            db.Notes.Add(note);
            db.SaveChanges();
        }

        using var dbContext = new QuickNotesDbContext(_options);
        var allTags = dbContext.Tags.Include(t => t.Synonyms).ToList();
        var sourceTag = allTags.First(t => t.Id == 1);

        var vm = new TagMergeViewModel(
            sourceTag,
            allTags,
            () => new QuickNotesDbContext(_options),
            _mergeService);

        Assert.Single(vm.AvailableTargets);
        Assert.Equal(2, vm.AvailableTargets[0].Id);
        Assert.Equal(2, vm.SelectedTargetTag?.Id);
        Assert.True(vm.CanConfirm);
        Assert.Equal(1, vm.Preview?.AffectedActiveNotesCount);
        Assert.Equal("1", vm.AffectedNotesText);

        bool? dialogResult = null;
        vm.RequestClose += res => dialogResult = res;

        vm.ConfirmCommand.Execute(null);
        Assert.True(dialogResult);
    }

    [Fact]
    public void TagMergeViewModel_WithNoAvailableTargets_DisablesConfirm()
    {
        using (var db = new QuickNotesDbContext(_options))
        {
            // Only one tag in DB (no other candidates exist)
            var soleTag = new Tag { Id = 1, Name = "Solo" };
            db.Tags.Add(soleTag);
            db.SaveChanges();
        }

        using var dbContext = new QuickNotesDbContext(_options);
        var allTags = dbContext.Tags.Include(t => t.Synonyms).ToList();
        var sourceTag = allTags.First(t => t.Id == 1);

        var vm = new TagMergeViewModel(
            sourceTag,
            allTags,
            () => new QuickNotesDbContext(_options),
            _mergeService);

        Assert.Empty(vm.AvailableTargets);
        Assert.Null(vm.SelectedTargetTag);
        Assert.False(vm.CanConfirm);
        Assert.Contains("Нет доступных тегов", vm.StatusExplanation);
        Assert.False(vm.ConfirmCommand.CanExecute(null));
    }
}
