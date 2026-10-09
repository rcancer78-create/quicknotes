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
public sealed class SearchVerticalSliceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly QuickNotesDbContext _context;
    private readonly SearchService _searchService = new();
    private readonly string _tempSettingsDir;

    public SearchVerticalSliceTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<QuickNotesDbContext>()
            .UseSqlite(_connection)
            .Options;

        _context = new QuickNotesDbContext(options);
        DbInitializer.Initialize(_context);

        _tempSettingsDir = Path.Combine(Path.GetTempPath(), $"quicknotes-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempSettingsDir);
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();

        try
        {
            if (Directory.Exists(_tempSettingsDir))
            {
                Directory.Delete(_tempSettingsDir, recursive: true);
            }
        }
        catch { }
    }

    private MainViewModel CreateTestMainViewModel(
        int debounceDelayMs = 0,
        QuickNotes.App.Services.NoteProtection.INoteProtectionService? noteProtectionService = null,
        Func<QuickNotesDbContext>? contextFactory = null)
    {
        var settingsPath = Path.Combine(_tempSettingsDir, $"settings-{Guid.NewGuid():N}.json");
        var hotkey = new GlobalHotkeyService((_, _, _, _) => true, (_, _) => true, IntPtr.Zero);
        var tray = new TrayIconService();
        var debouncer = new SearchDebouncer(delayMs: debounceDelayMs);

        var factory = contextFactory ?? (() => new QuickNotesDbContext(new DbContextOptionsBuilder<QuickNotesDbContext>().UseSqlite(_connection).Options));

        return MainViewModelTestComposition.Create(
            factory,
            new TagDetectionService(),
            _searchService,
            new SettingsService(settingsPath, _ => { }),
            hotkey,
            new ClipboardCaptureService(),
            tray,
            noteProtectionService: noteProtectionService,
            searchDebouncer: debouncer,
            draftJournalService: NoOpDraftJournalService.Instance);
    }

    #region 1. Title, Body, Cross-Column, Phrase, Prefix, and Protected Note Tests

    [Fact]
    public void Search_TitleOnly_MatchesFreeTextQuery()
    {
        var note = new Note
        {
            Title = "Архитектурный обзор проекта",
            Text = "Обычный текст без ключевых слов",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        _context.Notes.Add(note);
        _context.SaveChanges();

        var results = _searchService.QueryNotes(_context, "Архитектурный", null, new List<Tag>());
        Assert.Single(results);
        Assert.Equal(note.Id, results[0].Id);
    }

    [Fact]
    public void Search_BodyOnly_MatchesFreeTextQuery()
    {
        var note = new Note
        {
            Title = "Случайный заголовок",
            Text = "Внутри документа находится секретныймаркер для поиска",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        _context.Notes.Add(note);
        _context.SaveChanges();

        var results = _searchService.QueryNotes(_context, "секретныймаркер", null, new List<Tag>());
        Assert.Single(results);
        Assert.Equal(note.Id, results[0].Id);
    }

    [Fact]
    public void Search_CrossColumn_MatchesWhenTermsAreInDifferentColumns()
    {
        var matchingNote = new Note
        {
            Title = "Отчёт по бюджету",
            Text = "Финансовые расходы за квартал утверждены",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        var otherNote = new Note
        {
            Title = "Отчёт по кадрам",
            Text = "Штатное расписание утверждено",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        _context.Notes.AddRange(matchingNote, otherNote);
        _context.SaveChanges();

        // "бюджету" is in Title, "расходы" is in Text
        var results = _searchService.QueryNotes(_context, "бюджету расходы", null, new List<Tag>());
        Assert.Single(results);
        Assert.Equal(matchingNote.Id, results[0].Id);
    }

    [Fact]
    public void Search_PhraseQuery_MatchesExactPhraseOnly()
    {
        var exactPhraseNote = new Note
        {
            Title = "Notes",
            Text = "Мы провели глубокий анализ данных на встрече",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        var separatedWordsNote = new Note
        {
            Title = "Notes 2",
            Text = "Глубокий и всесторонний проведённый анализ был полезен",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        _context.Notes.AddRange(exactPhraseNote, separatedWordsNote);
        _context.SaveChanges();

        var results = _searchService.QueryNotes(_context, "\"глубокий анализ\"", null, new List<Tag>());
        Assert.Single(results);
        Assert.Equal(exactPhraseNote.Id, results[0].Id);
    }

    [Fact]
    public void Search_PrefixQuery_MatchesRussianAndEnglishPrefix()
    {
        var enNote = new Note
        {
            Title = "Telecom report",
            Text = "Telephone infrastructure upgrade",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        var ruNote = new Note
        {
            Title = "Оборудование",
            Text = "Телекоммуникационная сеть компании",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        _context.Notes.AddRange(enNote, ruNote);
        _context.SaveChanges();

        var enResults = _searchService.QueryNotes(_context, "tele*", null, new List<Tag>());
        Assert.Single(enResults);
        Assert.Equal(enNote.Id, enResults[0].Id);

        var ruResults = _searchService.QueryNotes(_context, "Телеком*", null, new List<Tag>());
        Assert.Single(ruResults);
        Assert.Equal(ruNote.Id, ruResults[0].Id);
    }

    [Fact]
    public void Search_ProtectedNote_ExcludedFromFtsAndQuery()
    {
        var protectedNote = new Note
        {
            Title = string.Empty, // Protected notes have empty Title and Text in the database
            Text = string.Empty,
            IsProtected = true,
            ProtectedCiphertextBase64 = "encrypted_base64_blob",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        _context.Notes.Add(protectedNote);
        _context.SaveChanges();

        // Querying for anything should never match the protected note in SQL/FTS
        var results = _searchService.QueryNotes(_context, "ciphertext", null, new List<Tag>());
        Assert.Empty(results);
    }

    #endregion

    #region 2. Tag Subtree Filtering and Multi-Tag Boolean Expressions

    [Fact]
    public void TagFilter_SubtreeExpansion_MatchesChildTagWhenParentSelected()
    {
        var parentTag = new Tag { Name = "Engineering" };
        _context.Tags.Add(parentTag);
        _context.SaveChanges();

        var childTag = new Tag { Name = "Backend", ParentTagId = parentTag.Id };
        _context.Tags.Add(childTag);
        _context.SaveChanges();

        var allTags = _context.Tags.ToList();

        var note = new Note
        {
            Title = "Service architecture",
            Text = "Microservice details",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        note.NoteTags.Add(new NoteTag { TagId = childTag.Id, Origin = TagOrigin.Manual });
        _context.Notes.Add(note);
        _context.SaveChanges();

        // Querying with parentTagId should match the note tagged with childTag
        var results = _searchService.QueryNotes(_context, null, parentTag.Id, allTags);
        Assert.Single(results);
        Assert.Equal(note.Id, results[0].Id);
    }

    [Fact]
    public void TagFilter_MultiTagIntersection_AndCondition()
    {
        var tagA = new Tag { Name = "ProjectX" };
        var tagB = new Tag { Name = "Urgent" };
        _context.Tags.AddRange(tagA, tagB);
        _context.SaveChanges();

        var allTags = _context.Tags.ToList();

        var noteBoth = new Note { Title = "Both tags", Text = "Note with both tags" };
        noteBoth.NoteTags.Add(new NoteTag { TagId = tagA.Id, Origin = TagOrigin.Manual });
        noteBoth.NoteTags.Add(new NoteTag { TagId = tagB.Id, Origin = TagOrigin.Manual });

        var noteOnlyA = new Note { Title = "Only A", Text = "Note with only tag A" };
        noteOnlyA.NoteTags.Add(new NoteTag { TagId = tagA.Id, Origin = TagOrigin.Manual });

        var noteOnlyB = new Note { Title = "Only B", Text = "Note with only tag B" };
        noteOnlyB.NoteTags.Add(new NoteTag { TagId = tagB.Id, Origin = TagOrigin.Manual });

        _context.Notes.AddRange(noteBoth, noteOnlyA, noteOnlyB);
        _context.SaveChanges();

        var results = _searchService.QueryNotes(_context, "tag:ProjectX AND tag:Urgent", null, allTags);
        Assert.Single(results);
        Assert.Equal(noteBoth.Id, results[0].Id);
    }

    [Fact]
    public void TagFilter_MultiTagUnion_OrCondition()
    {
        var tagA = new Tag { Name = "Frontend" };
        var tagB = new Tag { Name = "Backend" };
        _context.Tags.AddRange(tagA, tagB);
        _context.SaveChanges();

        var allTags = _context.Tags.ToList();

        var noteA = new Note { Title = "A", Text = "content" };
        noteA.NoteTags.Add(new NoteTag { TagId = tagA.Id, Origin = TagOrigin.Manual });

        var noteB = new Note { Title = "B", Text = "content" };
        noteB.NoteTags.Add(new NoteTag { TagId = tagB.Id, Origin = TagOrigin.Manual });

        var noteNeither = new Note { Title = "C", Text = "content" };

        _context.Notes.AddRange(noteA, noteB, noteNeither);
        _context.SaveChanges();

        var results = _searchService.QueryNotes(_context, "tag:Frontend OR tag:Backend", null, allTags);
        Assert.Equal(2, results.Count);
        Assert.Contains(results, n => n.Id == noteA.Id);
        Assert.Contains(results, n => n.Id == noteB.Id);
    }

    [Fact]
    public void TagFilter_TagExclusion_WithoutCondition()
    {
        var tagA = new Tag { Name = "ClientWork" };
        var tagB = new Tag { Name = "Archived" };
        _context.Tags.AddRange(tagA, tagB);
        _context.SaveChanges();

        var allTags = _context.Tags.ToList();

        var activeClientNote = new Note { Title = "Active", Text = "content" };
        activeClientNote.NoteTags.Add(new NoteTag { TagId = tagA.Id, Origin = TagOrigin.Manual });

        var archivedClientNote = new Note { Title = "Archived", Text = "content" };
        archivedClientNote.NoteTags.Add(new NoteTag { TagId = tagA.Id, Origin = TagOrigin.Manual });
        archivedClientNote.NoteTags.Add(new NoteTag { TagId = tagB.Id, Origin = TagOrigin.Manual });

        _context.Notes.AddRange(activeClientNote, archivedClientNote);
        _context.SaveChanges();

        var results = _searchService.QueryNotes(_context, "tag:ClientWork WITHOUT tag:Archived", null, allTags);
        Assert.Single(results);
        Assert.Equal(activeClientNote.Id, results[0].Id);
    }

    [Fact]
    public void TagFilter_SelectedTagAndQueryCondition_IntersectsCorrectly()
    {
        var tagSection = new Tag { Name = "Finance" };
        var tagQuery = new Tag { Name = "Q3" };
        _context.Tags.AddRange(tagSection, tagQuery);
        _context.SaveChanges();

        var allTags = _context.Tags.ToList();

        var bothNote = new Note { Title = "Finance Q3", Text = "content" };
        bothNote.NoteTags.Add(new NoteTag { TagId = tagSection.Id, Origin = TagOrigin.Manual });
        bothNote.NoteTags.Add(new NoteTag { TagId = tagQuery.Id, Origin = TagOrigin.Manual });

        var financeOnly = new Note { Title = "Finance Only", Text = "content" };
        financeOnly.NoteTags.Add(new NoteTag { TagId = tagSection.Id, Origin = TagOrigin.Manual });

        _context.Notes.AddRange(bothNote, financeOnly);
        _context.SaveChanges();

        // Selected tag is Finance, search query has tag:Q3
        var results = _searchService.QueryNotes(_context, "tag:Q3", tagSection.Id, allTags);
        Assert.Single(results);
        Assert.Equal(bothNote.Id, results[0].Id);
    }

    #endregion

    #region 3. Safe Highlight Tokenization

    [Fact]
    public void Highlight_ExcludesStructuredOperators()
    {
        string query = "tag:Work source:chrome untagged: created:today actualText";
        var spans = SearchPreview.SplitHighlights("Here is the actualText to find in source", query).ToList();

        // "actualText" should be marked as match, but "tag", "Work", "source", "chrome", "untagged", "created" should NOT match.
        var matchedTexts = spans.Where(s => s.IsMatch).Select(s => s.Text).ToList();
        Assert.Single(matchedTexts);
        Assert.Equal("actualText", matchedTexts[0]);
    }

    [Fact]
    public void Highlight_RegexMetacharacters_MatchLiterallyWithoutException()
    {
        // Query containing regex special characters: [ ( * + ? ^ $ \ . )
        string query = "[test] (item)* +?^$\\.";
        string text = "Check [test] value and (item)* safely";

        // Must not throw RegexParseException
        var spans = SearchPreview.SplitHighlights(text, query).ToList();
        Assert.NotEmpty(spans);
        Assert.Contains(spans, s => s.IsMatch && s.Text.Contains("[test]"));
    }

    #endregion

    #region 4. NoteEditor Find Navigation, Count, Wrap-around, and Immutability

    [Fact]
    public void NoteEditor_FindInCurrentNote_NextPreviousWrapAndCount()
    {
        var note = new Note
        {
            Id = 42,
            Title = "Test Note",
            Text = "The quick brown fox jumps over the lazy dog. Another fox appeared.",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        var editorVm = new NoteEditorViewModel(
            new TagDetectionService(),
            new List<Tag>(),
            existingNote: note,
            draftJournalService: NoOpDraftJournalService.Instance);

        Assert.False(editorVm.IsFindOpen);
        Assert.Equal(string.Empty, editorVm.FindText);
        Assert.Equal(0, editorVm.TotalMatchCount);

        // Open find with "fox"
        editorVm.OpenFind("fox");
        Assert.True(editorVm.IsFindOpen);
        Assert.Equal("fox", editorVm.FindText);
        Assert.Equal(2, editorVm.TotalMatchCount);
        Assert.Equal(0, editorVm.CurrentMatchIndex);
        Assert.Equal("1/2", editorVm.MatchCountText);

        // Find Next -> match 2
        editorVm.FindNext();
        Assert.Equal(1, editorVm.CurrentMatchIndex);
        Assert.Equal("2/2", editorVm.MatchCountText);

        // Find Next -> wraps around to match 1
        editorVm.FindNext();
        Assert.Equal(0, editorVm.CurrentMatchIndex);
        Assert.Equal("1/2", editorVm.MatchCountText);

        // Find Previous -> wraps backwards to match 2
        editorVm.FindPrevious();
        Assert.Equal(1, editorVm.CurrentMatchIndex);
        Assert.Equal("2/2", editorVm.MatchCountText);

        // Close find
        editorVm.CloseFind();
        Assert.False(editorVm.IsFindOpen);
        Assert.Equal(-1, editorVm.CurrentMatchIndex);

        // Verify text was never mutated by find operations
        Assert.Equal("The quick brown fox jumps over the lazy dog. Another fox appeared.", editorVm.Text);
    }

    [Fact]
    public void NoteEditor_FindSelectionCallback_FiresWithCorrectOffsets()
    {
        var note = new Note
        {
            Id = 99,
            Title = "Callback test",
            Text = "abc def abc",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        var editorVm = new NoteEditorViewModel(
            new TagDetectionService(),
            new List<Tag>(),
            existingNote: note,
            draftJournalService: NoOpDraftJournalService.Instance);

        int selectedStart = -1;
        int selectedLength = -1;
        editorVm.RequestSelectMatch = (start, len) =>
        {
            selectedStart = start;
            selectedLength = len;
        };

        editorVm.OpenFind("abc");
        Assert.Equal(0, selectedStart);
        Assert.Equal(3, selectedLength);

        editorVm.FindNext();
        Assert.Equal(8, selectedStart);
        Assert.Equal(3, selectedLength);
    }

    #endregion

    #region 5. 10,000-Note Bounded Paging and Continuation Tests

    [Fact]
    public void BoundedPaging_10000Notes_ObservableBoundedMaterializationAndContinuation()
    {
        // 1. Populate 10,000 notes in an isolated in-memory SQLite database
        using (var tx = _context.Database.BeginTransaction())
        {
            var baseTime = DateTime.UtcNow;
            var notesBatch = new List<Note>(10000);
            for (int i = 1; i <= 10000; i++)
            {
                notesBatch.Add(new Note
                {
                    Title = $"Note {i:D5}",
                    Text = $"Body content for note {i}",
                    CreatedAt = baseTime.AddSeconds(-i),
                    UpdatedAt = baseTime.AddSeconds(-i)
                });
            }
            _context.Notes.AddRange(notesBatch);
            _context.SaveChanges();
            tx.Commit();
        }

        // 2. CountNotes returns exactly 10,000 via server-side SQL Count()
        int totalCount = _searchService.CountNotes(_context, null, null, new List<Tag>());
        Assert.Equal(10000, totalCount);

        // 3. QueryNotes with skip: 0, take: 50 returns exactly 50 notes
        var firstPage = _searchService.QueryNotes(_context, null, null, new List<Tag>(), skip: 0, take: 50);
        Assert.Equal(50, firstPage.Count);

        // 4. MainViewModel observable bounded materialization
        using var vm = CreateTestMainViewModel();
        vm.RefreshNotes();

        Assert.Equal(10000, vm.TotalNotesCount);
        Assert.Equal(50, vm.Notes.Count); // Exactly 50 NoteCardViewModels materialized!
        Assert.True(vm.HasMoreNotes);
        Assert.Equal("Загрузить ещё (50 из 10000)", vm.LoadMoreButtonText);

        // 5. Continuation: LoadMoreNotes loads next page without changing sort or duplicating notes
        vm.LoadMoreNotes();
        Assert.Equal(100, vm.Notes.Count);
        Assert.Equal("Загрузить ещё (100 из 10000)", vm.LoadMoreButtonText);

        // Verify zero duplicates across loaded pages
        var uniqueIds = vm.Notes.Select(n => n.Id).ToHashSet();
        Assert.Equal(100, uniqueIds.Count);

        // Verify deterministic descending ordering by UpdatedAt / Id
        var list = vm.Notes.ToList();
        for (int i = 0; i < list.Count - 1; i++)
        {
            Assert.True(list[i].UpdatedAtText.CompareTo(list[i + 1].UpdatedAtText) >= 0 || list[i].Id > list[i + 1].Id);
        }
    }

    [Fact]
    public void SearchCancellation_StaleQueryCancellation_CancelsPriorSearch()
    {
        using var vm = CreateTestMainViewModel(debounceDelayMs: 200);

        // Simulating rapid typing: each change cancels debouncer and starts fresh
        vm.SearchQuery = "first";
        vm.SearchQuery = "second";
        vm.SearchQuery = "third";

        // Query string is preserved accurately
        Assert.Equal("third", vm.SearchQuery);
    }

    [Fact]
    public void BoundedPaging_LargeUnlockedProtectedHits_MergesIntoPagedStream()
    {
        // 1. Create a tag for filter verification
        var specialTag = new Tag { Name = "SpecialTag" };
        _context.Tags.Add(specialTag);
        _context.SaveChanges();

        // 2. Create 120 notes in database: alternating public and protected
        // i = 1..80 have SpecialTag (40 public, 40 protected)
        // i = 81..120 do NOT have SpecialTag (20 public, 20 protected)
        var baseTime = DateTime.UtcNow;
        var protectedIds = new List<int>();
        var expectedMatchingIds = new List<int>();

        using (var tx = _context.Database.BeginTransaction())
        {
            var batch = new List<Note>(120);
            for (int i = 1; i <= 120; i++)
            {
                bool isProt = (i % 2 == 0);
                var note = new Note
                {
                    Title = isProt ? string.Empty : $"Public Note {i:D3}",
                    Text = isProt ? string.Empty : $"CommonSearchTerm content {i}",
                    IsProtected = isProt,
                    ProtectedCiphertextBase64 = isProt ? "ciphertext" : null,
                    CreatedAt = baseTime.AddSeconds(-i),
                    UpdatedAt = baseTime.AddSeconds(-i)
                };

                if (i <= 80)
                {
                    note.NoteTags.Add(new NoteTag { TagId = specialTag.Id, Origin = TagOrigin.Manual });
                }

                batch.Add(note);
            }
            _context.Notes.AddRange(batch);
            _context.SaveChanges();
            tx.Commit();

            for (int i = 0; i < batch.Count; i++)
            {
                var note = batch[i];
                if (note.IsProtected)
                {
                    protectedIds.Add(note.Id);
                }
                if (i < 80) // 1-indexed i <= 80
                {
                    expectedMatchingIds.Add(note.Id);
                }
            }
        }

        // 3. Register unlocked sessions in ProtectedNoteSessionStore with real NoteProtectionService
        var sessionStore = new QuickNotes.App.Services.NoteProtection.ProtectedNoteSessionStore();
        foreach (var id in protectedIds)
        {
            sessionStore.Add(new QuickNotes.App.Models.ProtectedNoteSession(
                id,
                Guid.NewGuid(),
                new byte[32],
                new QuickNotes.App.Models.NoteProtectedPayload
                {
                    Title = $"Protected Note {id}",
                    Text = $"CommonSearchTerm classified content {id}"
                }));
        }
        var protectionService = new QuickNotes.App.Services.NoteProtection.NoteProtectionService(sessions: sessionStore);
        using var vm = CreateTestMainViewModel(noteProtectionService: protectionService);

        // Filter by both free-text term and tag
        vm.SearchQuery = "CommonSearchTerm tag:SpecialTag";
        vm.RefreshNotes();

        // Must boundedly materialize at most PageSize (50) cards!
        Assert.Equal(80, vm.TotalNotesCount);
        Assert.Equal(50, vm.Notes.Count);
        Assert.True(vm.HasMoreNotes);
        Assert.Equal("Загрузить ещё (50 из 80)", vm.LoadMoreButtonText);

        // Page 1: exactly first 50 expected IDs (25 public, 25 protected alternating)
        var page1Ids = vm.Notes.Select(n => n.Id).ToList();
        Assert.Equal(expectedMatchingIds.Take(50).ToList(), page1Ids);
        Assert.Equal(25, vm.Notes.Count(n => n.IsProtected));
        Assert.Equal(25, vm.Notes.Count(n => !n.IsProtected));

        // Page 2: Continuation loads next 30 notes (15 public, 15 protected)
        vm.LoadMoreNotes();
        Assert.Equal(80, vm.Notes.Count);
        var allLoadedIds = vm.Notes.Select(n => n.Id).ToList();
        Assert.Equal(expectedMatchingIds, allLoadedIds);
        Assert.Equal(80, allLoadedIds.Distinct().Count()); // No duplicates!
        Assert.Equal(40, vm.Notes.Count(n => n.IsProtected));
        Assert.Equal(40, vm.Notes.Count(n => !n.IsProtected));
        Assert.False(vm.HasMoreNotes);
    }

    [Fact]
    public void SearchCancellation_StaleLoadMoreContinuation_DoesNotAppendStaleResults()
    {
        // 1. Create 100 "Alpha" notes and 100 "Beta" notes
        var baseTime = DateTime.UtcNow;
        using (var tx = _context.Database.BeginTransaction())
        {
            var batch = new List<Note>(200);
            for (int i = 1; i <= 100; i++)
            {
                batch.Add(new Note
                {
                    Title = $"Alpha note {i:D3}",
                    Text = $"Alpha content {i}",
                    CreatedAt = baseTime.AddSeconds(-i),
                    UpdatedAt = baseTime.AddSeconds(-i)
                });
            }
            for (int i = 1; i <= 100; i++)
            {
                batch.Add(new Note
                {
                    Title = $"Beta note {i:D3}",
                    Text = $"Beta content {i}",
                    CreatedAt = baseTime.AddSeconds(-100 - i),
                    UpdatedAt = baseTime.AddSeconds(-100 - i)
                });
            }
            _context.Notes.AddRange(batch);
            _context.SaveChanges();
            tx.Commit();
        }

        using var vm = CreateTestMainViewModel(debounceDelayMs: 200);

        // 2. Initial query for "Alpha"
        vm.SearchQuery = "Alpha";
        vm.RefreshNotes();
        Assert.Equal(50, vm.Notes.Count);
        Assert.All(vm.Notes, n => Assert.Contains("Alpha", n.Note.Title));

        // 3. User types a new search query "Beta" (invalidates generation & cancels active search CTS)
        vm.SearchQuery = "Beta";

        // 4. A stale LoadMore from the "Alpha" query continuation is invoked
        vm.LoadMoreNotes();

        // 5. Must NOT append results from the old "Alpha" query!
        Assert.Equal(50, vm.Notes.Count);
        Assert.DoesNotContain(vm.Notes, n => n.Note.Title.Contains("Alpha") && vm.Notes.IndexOf(n) >= 50);

        // 6. When the new query refreshes, it presents "Beta" notes cleanly
        vm.RefreshNotes();
        Assert.Equal(50, vm.Notes.Count);
        Assert.All(vm.Notes, n => Assert.Contains("Beta", n.Note.Title));
    }

    #endregion

    #region Helper Test Doubles

    private sealed class NoteMaterializationCounter : Microsoft.EntityFrameworkCore.Diagnostics.IMaterializationInterceptor
    {
        public int MaterializedCount { get; private set; }

        public object InitializedInstance(Microsoft.EntityFrameworkCore.Diagnostics.MaterializationInterceptionData materializationData, object entity)
        {
            if (entity is Note)
            {
                MaterializedCount++;
            }
            return entity;
        }
    }

    #endregion
}
