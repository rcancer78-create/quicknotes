using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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
public sealed class SmartSearchTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly QuickNotesDbContext _context;
    private readonly SearchService _searchService = new();

    public SmartSearchTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<QuickNotesDbContext>()
            .UseSqlite(_connection)
            .Options;

        _context = new QuickNotesDbContext(options);
        DbInitializer.Initialize(_context);
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    #region Parser Unit Tests

    [Fact]
    public void Parser_PlainFtsTextOnly_ReturnsNullConditionAndFullFreeText()
    {
        var tags = new List<Tag> { new() { Id = 1, Name = "Work" } };
        var result = SearchQueryParser.Parse("buy milk and cheese", tags);

        Assert.Null(result.Condition);
        Assert.Equal("buy milk and cheese", result.FreeText);
        Assert.Empty(result.Chips);
    }

    [Fact]
    public void Parser_SingleTagCondition_ExtractsTagAndEmptyFreeText()
    {
        var tags = new List<Tag> { new() { Id = 1, Name = "Work" } };
        var result = SearchQueryParser.Parse("tag:Work", tags);

        Assert.NotNull(result.Condition);
        Assert.IsType<TagCondition>(result.Condition);
        Assert.Equal(string.Empty, result.FreeText);
        Assert.Single(result.Chips);
        Assert.Equal("tag:Work", result.Chips[0].DisplayText);
    }

    [Fact]
    public void Parser_QuotedTagCondition_ExtractsTag()
    {
        var tags = new List<Tag> { new() { Id = 1, Name = "Work Projects" } };
        var result = SearchQueryParser.Parse("tag:\"Work Projects\"", tags);

        Assert.NotNull(result.Condition);
        var tagCond = Assert.IsType<TagCondition>(result.Condition);
        Assert.Equal("Work Projects", tagCond.TagName);
        Assert.Single(result.Chips);
        Assert.Equal("tag:Work Projects", result.Chips[0].DisplayText);
    }

    [Fact]
    public void Parser_UnknownTag_TreatedAsFreeText()
    {
        var tags = new List<Tag> { new() { Id = 1, Name = "Work" } };
        var result = SearchQueryParser.Parse("tag:NonExistent urgent", tags);

        Assert.Null(result.Condition);
        Assert.Equal("tag:NonExistent urgent", result.FreeText);
        Assert.Empty(result.Chips);
    }

    [Fact]
    public void Parser_CreatedTodayAndWeek_ExtractsConditions()
    {
        var tags = new List<Tag>();
        var todayResult = SearchQueryParser.Parse("created:today", tags);
        Assert.NotNull(todayResult.Condition);
        var todayCond = Assert.IsType<CreatedCondition>(todayResult.Condition);
        Assert.Equal(DateFilterType.Today, todayCond.Type);
        Assert.Single(todayResult.Chips);
        Assert.Equal("created:today", todayResult.Chips[0].DisplayText);

        var weekResult = SearchQueryParser.Parse("created:week", tags);
        Assert.NotNull(weekResult.Condition);
        var weekCond = Assert.IsType<CreatedCondition>(weekResult.Condition);
        Assert.Equal(DateFilterType.Week, weekCond.Type);
        Assert.Single(weekResult.Chips);
        Assert.Equal("created:week", weekResult.Chips[0].DisplayText);
    }

    [Fact]
    public void Parser_InvalidDateCondition_TreatedAsFreeText()
    {
        var tags = new List<Tag>();
        var result = SearchQueryParser.Parse("created:yesterday meeting", tags);

        Assert.Null(result.Condition);
        Assert.Equal("created:yesterday meeting", result.FreeText);
        Assert.Empty(result.Chips);
    }

    [Fact]
    public void Parser_UpdatedWeekAndToday_ExtractsConditions()
    {
        var tags = new List<Tag>();
        var result = SearchQueryParser.Parse("updated:week", tags);
        Assert.NotNull(result.Condition);
        var cond = Assert.IsType<UpdatedCondition>(result.Condition);
        Assert.Equal(DateFilterType.Week, cond.Type);
        Assert.Single(result.Chips);
        Assert.Equal("updated:week", result.Chips[0].DisplayText);
    }

    [Fact]
    public void Parser_UntaggedTrueAndFalse_ExtractsConditions()
    {
        var tags = new List<Tag>();
        var trueResult = SearchQueryParser.Parse("untagged:true", tags);
        Assert.NotNull(trueResult.Condition);
        var trueCond = Assert.IsType<UntaggedCondition>(trueResult.Condition);
        Assert.True(trueCond.Expected);
        Assert.Single(trueResult.Chips);

        var falseResult = SearchQueryParser.Parse("untagged:false", tags);
        Assert.NotNull(falseResult.Condition);
        var falseCond = Assert.IsType<UntaggedCondition>(falseResult.Condition);
        Assert.False(falseCond.Expected);
        Assert.Single(falseResult.Chips);
    }

    [Fact]
    public void Parser_UnknownPrefix_TreatedAsFreeText()
    {
        var tags = new List<Tag>();
        var result = SearchQueryParser.Parse("status:open priority:high hello", tags);
        Assert.Null(result.Condition);
        Assert.Equal("status:open priority:high hello", result.FreeText);
    }

    #endregion

    #region Operator & Precedence Tests

    [Fact]
    public void Parser_AndOperator_BetweenTags()
    {
        var tags = new List<Tag>
        {
            new() { Id = 1, Name = "Work" },
            new() { Id = 2, Name = "Urgent" }
        };

        var result = SearchQueryParser.Parse("tag:Work AND tag:Urgent", tags);
        Assert.NotNull(result.Condition);
        var andCond = Assert.IsType<AndCondition>(result.Condition);
        Assert.IsType<TagCondition>(andCond.Left);
        Assert.IsType<TagCondition>(andCond.Right);
        Assert.Equal(2, result.Chips.Count);
    }

    [Fact]
    public void Parser_OrOperator_BetweenTags()
    {
        var tags = new List<Tag>
        {
            new() { Id = 1, Name = "Work" },
            new() { Id = 2, Name = "Personal" }
        };

        var result = SearchQueryParser.Parse("tag:Work OR tag:Personal", tags);
        Assert.NotNull(result.Condition);
        var orCond = Assert.IsType<OrCondition>(result.Condition);
        Assert.IsType<TagCondition>(orCond.Left);
        Assert.IsType<TagCondition>(orCond.Right);
        Assert.Equal(2, result.Chips.Count);
    }

    [Fact]
    public void Parser_WithoutOperator_Binary()
    {
        var tags = new List<Tag>
        {
            new() { Id = 1, Name = "Work" },
            new() { Id = 2, Name = "Archive" }
        };

        var result = SearchQueryParser.Parse("tag:Work WITHOUT tag:Archive", tags);
        Assert.NotNull(result.Condition);
        var andCond = Assert.IsType<AndCondition>(result.Condition);
        Assert.IsType<TagCondition>(andCond.Left);
        var notCond = Assert.IsType<NotCondition>(andCond.Right);
        Assert.IsType<TagCondition>(notCond.Inner);

        Assert.Equal(2, result.Chips.Count);
        Assert.Equal("tag:Work", result.Chips[0].DisplayText);
        Assert.Equal("WITHOUT tag:Archive", result.Chips[1].DisplayText);
        Assert.True(result.Chips[1].IsNegated);
    }

    [Fact]
    public void Parser_WithoutOperator_PrefixUnary()
    {
        var tags = new List<Tag> { new() { Id = 1, Name = "Archive" } };

        var result = SearchQueryParser.Parse("WITHOUT tag:Archive urgent", tags);
        Assert.NotNull(result.Condition);
        var notCond = Assert.IsType<NotCondition>(result.Condition);
        Assert.IsType<TagCondition>(notCond.Inner);
        Assert.Equal("urgent", result.FreeText);
        Assert.Single(result.Chips);
        Assert.Equal("WITHOUT tag:Archive", result.Chips[0].DisplayText);
    }

    [Fact]
    public void Parser_ImplicitAnd_BetweenConsecutiveConditions()
    {
        var tags = new List<Tag> { new() { Id = 1, Name = "Work" } };

        var result = SearchQueryParser.Parse("tag:Work created:today report", tags);
        Assert.NotNull(result.Condition);
        var andCond = Assert.IsType<AndCondition>(result.Condition);
        Assert.IsType<TagCondition>(andCond.Left);
        Assert.IsType<CreatedCondition>(andCond.Right);
        Assert.Equal("report", result.FreeText);
        Assert.Equal(2, result.Chips.Count);
    }

    [Fact]
    public void Parser_OperatorPrecedence_AndTighterThanOr()
    {
        var tags = new List<Tag>
        {
            new() { Id = 1, Name = "Work" },
            new() { Id = 2, Name = "Urgent" }
        };

        // tag:Work OR tag:Urgent AND created:today => tag:Work OR (tag:Urgent AND created:today)
        var result = SearchQueryParser.Parse("tag:Work OR tag:Urgent AND created:today", tags);
        Assert.NotNull(result.Condition);
        var orCond = Assert.IsType<OrCondition>(result.Condition);
        Assert.IsType<TagCondition>(orCond.Left);
        var andCond = Assert.IsType<AndCondition>(orCond.Right);
        Assert.IsType<TagCondition>(andCond.Left);
        Assert.IsType<CreatedCondition>(andCond.Right);
    }

    [Fact]
    public void Parser_Parentheses_OverridePrecedence()
    {
        var tags = new List<Tag>
        {
            new() { Id = 1, Name = "Work" },
            new() { Id = 2, Name = "Personal" }
        };

        // (tag:Work OR tag:Personal) AND created:today
        var result = SearchQueryParser.Parse("(tag:Work OR tag:Personal) AND created:today", tags);
        Assert.NotNull(result.Condition);
        var andCond = Assert.IsType<AndCondition>(result.Condition);
        var orCond = Assert.IsType<OrCondition>(andCond.Left);
        Assert.IsType<TagCondition>(orCond.Left);
        Assert.IsType<TagCondition>(orCond.Right);
        Assert.IsType<CreatedCondition>(andCond.Right);
    }

    [Fact]
    public void Parser_TrailingOperators_DoNotCrash()
    {
        var tags = new List<Tag> { new() { Id = 1, Name = "Work" } };
        var r1 = SearchQueryParser.Parse("tag:Work AND", tags);
        Assert.NotNull(r1.Condition);
        Assert.Equal(string.Empty, r1.FreeText);

        var r2 = SearchQueryParser.Parse("tag:Work OR", tags);
        Assert.NotNull(r2.Condition);

        var r3 = SearchQueryParser.Parse("tag:Work WITHOUT", tags);
        Assert.NotNull(r3.Condition);
    }

    #endregion

    #region Chip Removal Tests

    [Fact]
    public void RemoveChip_SingleCondition_ReturnsEmpty()
    {
        var tags = new List<Tag> { new() { Id = 1, Name = "Work" } };
        var parsed = SearchQueryParser.Parse("tag:Work", tags);
        Assert.Single(parsed.Chips);

        var remaining = SearchQueryParser.RemoveChip("tag:Work", parsed.Chips[0]);
        Assert.Equal(string.Empty, remaining);
    }

    [Fact]
    public void RemoveChip_FirstConditionWithTrailingAnd_CleansOperator()
    {
        var tags = new List<Tag>
        {
            new() { Id = 1, Name = "Work" },
            new() { Id = 2, Name = "Urgent" }
        };
        var parsed = SearchQueryParser.Parse("tag:Work AND tag:Urgent fix bug", tags);
        Assert.Equal(2, parsed.Chips.Count);

        var remaining = SearchQueryParser.RemoveChip("tag:Work AND tag:Urgent fix bug", parsed.Chips[0]);
        Assert.Equal("tag:Urgent fix bug", remaining);
    }

    [Fact]
    public void RemoveChip_SecondConditionWithPrecedingAnd_CleansOperator()
    {
        var tags = new List<Tag>
        {
            new() { Id = 1, Name = "Work" },
            new() { Id = 2, Name = "Urgent" }
        };
        var parsed = SearchQueryParser.Parse("tag:Work AND tag:Urgent fix bug", tags);

        var remaining = SearchQueryParser.RemoveChip("tag:Work AND tag:Urgent fix bug", parsed.Chips[1]);
        Assert.Equal("tag:Work fix bug", remaining);
    }

    [Fact]
    public void RemoveChip_WithoutCondition_CleansWithoutPrefix()
    {
        var tags = new List<Tag>
        {
            new() { Id = 1, Name = "Work" },
            new() { Id = 2, Name = "Archive" }
        };
        var parsed = SearchQueryParser.Parse("tag:Work WITHOUT tag:Archive", tags);
        Assert.Equal(2, parsed.Chips.Count);

        var remaining = SearchQueryParser.RemoveChip("tag:Work WITHOUT tag:Archive", parsed.Chips[1]);
        Assert.Equal("tag:Work", remaining);
    }

    [Fact]
    public void RemoveChip_LeavesFreeTextIntactAndEditable()
    {
        var tags = new List<Tag> { new() { Id = 1, Name = "Work" } };
        var parsed = SearchQueryParser.Parse("tag:Work my urgent notes", tags);

        var remaining = SearchQueryParser.RemoveChip("tag:Work my urgent notes", parsed.Chips[0]);
        Assert.Equal("my urgent notes", remaining);
    }

    #endregion

    #region Debounce Tests

    [Fact]
    public async Task Debouncer_ExecutesAfterDelayAndCancelsPending()
    {
        int executionCount = 0;
        using var debouncer = new SearchDebouncer(delayMs: 150);

        debouncer.Debounce(() => Interlocked.Increment(ref executionCount));
        debouncer.Debounce(() => Interlocked.Increment(ref executionCount));
        debouncer.Debounce(() => Interlocked.Increment(ref executionCount));

        // Immediately before delay: 0 executions
        Assert.Equal(0, executionCount);

        // Wait for debounce delay
        await Task.Delay(300);

        // Only 1 execution occurred despite 3 rapid calls
        Assert.Equal(1, executionCount);
    }

    [Fact]
    public void Debouncer_ImmediateMode_RunsSynchronously()
    {
        int executionCount = 0;
        using var debouncer = new SearchDebouncer(delayMs: 0);

        debouncer.Debounce(() => executionCount++);
        Assert.Equal(1, executionCount);
    }

    [Fact]
    public async Task Debouncer_Cancel_PreventsExecution()
    {
        int executionCount = 0;
        using var debouncer = new SearchDebouncer(delayMs: 150);

        debouncer.Debounce(() => executionCount++);
        debouncer.Cancel();

        await Task.Delay(250);
        Assert.Equal(0, executionCount);
    }

    #endregion

    #region SQLite Database & QueryNotes Integration Tests

    [Fact]
    public void QueryNotes_TagWithDescendants_MatchesNotesWithChildTags()
    {
        var parentTag = new Tag { Name = "Backend" };
        _context.Tags.Add(parentTag);
        _context.SaveChanges();

        var childTag = new Tag { Name = "CSharp", ParentTagId = parentTag.Id };
        _context.Tags.Add(childTag);
        _context.SaveChanges();

        var note = new Note
        {
            Text = "Learning LINQ and EF Core in .NET",
            CreatedAt = DateTime.Now,
            UpdatedAt = DateTime.Now
        };
        note.NoteTags.Add(new NoteTag { TagId = childTag.Id, Origin = TagOrigin.Manual });
        _context.Notes.Add(note);
        _context.SaveChanges();

        var allTags = _context.Tags.ToList();

        // Searching for parent tag should find note tagged with child tag
        var results = _searchService.QueryNotes(_context, "tag:Backend", null, allTags);
        Assert.Single(results);
        Assert.Equal(note.Id, results[0].Id);
    }

    [Fact]
    public void QueryNotes_TagAndOperator_MatchesOnlyWhenBothPresent()
    {
        var tagWork = new Tag { Name = "Work" };
        var tagUrgent = new Tag { Name = "Urgent" };
        _context.Tags.AddRange(tagWork, tagUrgent);
        _context.SaveChanges();

        var noteBoth = new Note { Text = "Both tags", CreatedAt = DateTime.Now, UpdatedAt = DateTime.Now };
        noteBoth.NoteTags.Add(new NoteTag { TagId = tagWork.Id });
        noteBoth.NoteTags.Add(new NoteTag { TagId = tagUrgent.Id });

        var noteWorkOnly = new Note { Text = "Work only", CreatedAt = DateTime.Now, UpdatedAt = DateTime.Now };
        noteWorkOnly.NoteTags.Add(new NoteTag { TagId = tagWork.Id });

        _context.Notes.AddRange(noteBoth, noteWorkOnly);
        _context.SaveChanges();

        var allTags = _context.Tags.ToList();

        var results = _searchService.QueryNotes(_context, "tag:Work AND tag:Urgent", null, allTags);
        Assert.Single(results);
        Assert.Equal(noteBoth.Id, results[0].Id);
    }

    [Fact]
    public void QueryNotes_TagOrOperator_MatchesEither()
    {
        var tagWork = new Tag { Name = "Work" };
        var tagHome = new Tag { Name = "Home" };
        _context.Tags.AddRange(tagWork, tagHome);
        _context.SaveChanges();

        var note1 = new Note { Text = "Office task", CreatedAt = DateTime.Now, UpdatedAt = DateTime.Now };
        note1.NoteTags.Add(new NoteTag { TagId = tagWork.Id });

        var note2 = new Note { Text = "Home chores", CreatedAt = DateTime.Now, UpdatedAt = DateTime.Now };
        note2.NoteTags.Add(new NoteTag { TagId = tagHome.Id });

        var note3 = new Note { Text = "Unrelated", CreatedAt = DateTime.Now, UpdatedAt = DateTime.Now };

        _context.Notes.AddRange(note1, note2, note3);
        _context.SaveChanges();

        var allTags = _context.Tags.ToList();

        var results = _searchService.QueryNotes(_context, "tag:Work OR tag:Home", null, allTags);
        Assert.Equal(2, results.Count);
        Assert.Contains(results, n => n.Id == note1.Id);
        Assert.Contains(results, n => n.Id == note2.Id);
        Assert.DoesNotContain(results, n => n.Id == note3.Id);
    }

    [Fact]
    public void QueryNotes_TagWithoutOperator_ExcludesTag()
    {
        var tagWork = new Tag { Name = "Work" };
        var tagArchived = new Tag { Name = "Archived" };
        _context.Tags.AddRange(tagWork, tagArchived);
        _context.SaveChanges();

        var noteActive = new Note { Text = "Active task", CreatedAt = DateTime.Now, UpdatedAt = DateTime.Now };
        noteActive.NoteTags.Add(new NoteTag { TagId = tagWork.Id });

        var noteArchived = new Note { Text = "Old task", CreatedAt = DateTime.Now, UpdatedAt = DateTime.Now };
        noteArchived.NoteTags.Add(new NoteTag { TagId = tagWork.Id });
        noteArchived.NoteTags.Add(new NoteTag { TagId = tagArchived.Id });

        _context.Notes.AddRange(noteActive, noteArchived);
        _context.SaveChanges();

        var allTags = _context.Tags.ToList();

        var results = _searchService.QueryNotes(_context, "tag:Work WITHOUT tag:Archived", null, allTags);
        Assert.Single(results);
        Assert.Equal(noteActive.Id, results[0].Id);
    }

    [Fact]
    public void QueryNotes_CreatedToday_FiltersByDate()
    {
        var noteToday = new Note { Text = "Created today", CreatedAt = DateTime.Now, UpdatedAt = DateTime.Now };
        var noteOld = new Note { Text = "Created last month", CreatedAt = DateTime.Now.AddDays(-30), UpdatedAt = DateTime.Now.AddDays(-30) };

        _context.Notes.AddRange(noteToday, noteOld);
        _context.SaveChanges();

        var allTags = _context.Tags.ToList();

        var results = _searchService.QueryNotes(_context, "created:today", null, allTags);
        Assert.Single(results);
        Assert.Equal(noteToday.Id, results[0].Id);
    }

    [Fact]
    public void QueryNotes_UpdatedWeek_FiltersByUpdatedDate()
    {
        var noteRecentUpdate = new Note
        {
            Text = "Updated 2 days ago",
            CreatedAt = DateTime.Now.AddDays(-60),
            UpdatedAt = DateTime.Now.AddDays(-2)
        };
        var noteOldUpdate = new Note
        {
            Text = "Updated 20 days ago",
            CreatedAt = DateTime.Now.AddDays(-60),
            UpdatedAt = DateTime.Now.AddDays(-20)
        };

        _context.Notes.AddRange(noteRecentUpdate, noteOldUpdate);
        _context.SaveChanges();

        var allTags = _context.Tags.ToList();

        var results = _searchService.QueryNotes(_context, "updated:week", null, allTags);
        Assert.Single(results);
        Assert.Equal(noteRecentUpdate.Id, results[0].Id);
    }

    [Fact]
    public void QueryNotes_UntaggedTrue_FiltersNotesWithoutTags()
    {
        var tag = new Tag { Name = "Tagged" };
        _context.Tags.Add(tag);
        _context.SaveChanges();

        var noteWithTag = new Note { Text = "Has tag", CreatedAt = DateTime.Now, UpdatedAt = DateTime.Now };
        noteWithTag.NoteTags.Add(new NoteTag { TagId = tag.Id });

        var noteUntagged = new Note { Text = "No tag", CreatedAt = DateTime.Now, UpdatedAt = DateTime.Now };

        _context.Notes.AddRange(noteWithTag, noteUntagged);
        _context.SaveChanges();

        var allTags = _context.Tags.ToList();

        var results = _searchService.QueryNotes(_context, "untagged:true", null, allTags);
        Assert.Single(results);
        Assert.Equal(noteUntagged.Id, results[0].Id);
    }

    [Fact]
    public void QueryNotes_SeparatesFtsTextAndStructuredFilter()
    {
        var tagWork = new Tag { Name = "Work" };
        var tagPersonal = new Tag { Name = "Personal" };
        _context.Tags.AddRange(tagWork, tagPersonal);
        _context.SaveChanges();

        var note1 = new Note { Text = "Work meeting about database migration", CreatedAt = DateTime.Now, UpdatedAt = DateTime.Now };
        note1.NoteTags.Add(new NoteTag { TagId = tagWork.Id });

        var note2 = new Note { Text = "Personal shopping list with database book", CreatedAt = DateTime.Now, UpdatedAt = DateTime.Now };
        note2.NoteTags.Add(new NoteTag { TagId = tagPersonal.Id });

        _context.Notes.AddRange(note1, note2);
        _context.SaveChanges();

        var allTags = _context.Tags.ToList();

        // Query: tag:Work AND FTS text "migration"
        var results = _searchService.QueryNotes(_context, "tag:Work migration", null, allTags);
        Assert.Single(results);
        Assert.Equal(note1.Id, results[0].Id);
    }

    [Fact]
    public void QueryNotes_SoftDeletedNotes_ExcludedFromNormalSearch()
    {
        var tag = new Tag { Name = "Work" };
        _context.Tags.Add(tag);
        _context.SaveChanges();

        var activeNote = new Note { Text = "Active note", CreatedAt = DateTime.Now, UpdatedAt = DateTime.Now };
        activeNote.NoteTags.Add(new NoteTag { TagId = tag.Id });

        var deletedNote = new Note
        {
            Text = "Deleted note",
            CreatedAt = DateTime.Now,
            UpdatedAt = DateTime.Now,
            DeletedAt = DateTime.Now
        };
        deletedNote.NoteTags.Add(new NoteTag { TagId = tag.Id });

        _context.Notes.AddRange(activeNote, deletedNote);
        _context.SaveChanges();

        var allTags = _context.Tags.ToList();

        // Normal search (NavigationSection.All) must exclude deleted note
        var normalResults = _searchService.QueryNotes(_context, "tag:Work", null, allTags, NavigationSection.All);
        Assert.Single(normalResults);
        Assert.Equal(activeNote.Id, normalResults[0].Id);

        // Trash section must include only deleted note
        var trashResults = _searchService.QueryNotes(_context, "tag:Work", null, allTags, NavigationSection.Trash);
        Assert.Single(trashResults);
        Assert.Equal(deletedNote.Id, trashResults[0].Id);
    }

    [Fact]
    public void MainViewModel_ChipRemoval_UpdatesSearchQueryAndResultsImmediately()
    {
        var tagWork = new Tag { Name = "Work" };
        var tagUrgent = new Tag { Name = "Urgent" };
        _context.Tags.AddRange(tagWork, tagUrgent);
        _context.SaveChanges();

        var note1 = new Note { Text = "Note with Work only", CreatedAt = DateTime.Now, UpdatedAt = DateTime.Now };
        note1.NoteTags.Add(new NoteTag { TagId = tagWork.Id });

        var note2 = new Note { Text = "Note with Work and Urgent", CreatedAt = DateTime.Now, UpdatedAt = DateTime.Now };
        note2.NoteTags.Add(new NoteTag { TagId = tagWork.Id });
        note2.NoteTags.Add(new NoteTag { TagId = tagUrgent.Id });

        _context.Notes.AddRange(note1, note2);
        _context.SaveChanges();

        string settingsPath = Path.Combine(Path.GetTempPath(), $"quicknotes-test-{Guid.NewGuid():N}.json");
        try
        {
            using var hotkey = new GlobalHotkeyService((_, _, _, _) => true, (_, _) => true, IntPtr.Zero);
            using var tray = new TrayIconService();
            // Immediate debouncer for synchronous test execution
            var debouncer = new SearchDebouncer(delayMs: 0);

            using var vm = MainViewModelTestComposition.Create(
                () => new QuickNotesDbContext(new DbContextOptionsBuilder<QuickNotesDbContext>().UseSqlite(_connection).Options),
                new TagDetectionService(),
                _searchService,
                new SettingsService(settingsPath, _ => { }),
                hotkey,
                new ClipboardCaptureService(),
                tray,
                searchDebouncer: debouncer,
                draftJournalService: NoOpDraftJournalService.Instance);

            // Initially set query with two tag filters
            vm.SearchQuery = "tag:Work AND tag:Urgent";
            Assert.Equal(2, vm.ActiveFilterChips.Count);
            Assert.Single(vm.Notes); // only note2

            // Remove the second chip (tag:Urgent)
            var urgentChip = vm.ActiveFilterChips[1];
            urgentChip.RemoveCommand.Execute(null);

            // Search query should now be "tag:Work"
            Assert.Equal("tag:Work", vm.SearchQuery);
            Assert.Single(vm.ActiveFilterChips);
            // Notes should now contain both note1 and note2
            Assert.Equal(2, vm.Notes.Count);
        }
        finally
        {
            if (File.Exists(settingsPath)) File.Delete(settingsPath);
        }
    }

    [Fact]
    public void QueryNotes_ComplexExpression_ParenthesesAndWithout()
    {
        var tagWork = new Tag { Name = "Work" };
        var tagHome = new Tag { Name = "Home" };
        var tagArchive = new Tag { Name = "Archive" };
        _context.Tags.AddRange(tagWork, tagHome, tagArchive);
        _context.SaveChanges();

        var noteWorkActive = new Note { Text = "Work active", CreatedAt = DateTime.Now, UpdatedAt = DateTime.Now };
        noteWorkActive.NoteTags.Add(new NoteTag { TagId = tagWork.Id });

        var noteHomeActive = new Note { Text = "Home active", CreatedAt = DateTime.Now, UpdatedAt = DateTime.Now };
        noteHomeActive.NoteTags.Add(new NoteTag { TagId = tagHome.Id });

        var noteWorkArchived = new Note { Text = "Work archived", CreatedAt = DateTime.Now, UpdatedAt = DateTime.Now };
        noteWorkArchived.NoteTags.Add(new NoteTag { TagId = tagWork.Id });
        noteWorkArchived.NoteTags.Add(new NoteTag { TagId = tagArchive.Id });

        _context.Notes.AddRange(noteWorkActive, noteHomeActive, noteWorkArchived);
        _context.SaveChanges();

        var allTags = _context.Tags.ToList();

        // (tag:Work OR tag:Home) WITHOUT tag:Archive
        var results = _searchService.QueryNotes(_context, "(tag:Work OR tag:Home) WITHOUT tag:Archive", null, allTags);
        Assert.Equal(2, results.Count);
        Assert.Contains(results, n => n.Id == noteWorkActive.Id);
        Assert.Contains(results, n => n.Id == noteHomeActive.Id);
        Assert.DoesNotContain(results, n => n.Id == noteWorkArchived.Id);
    }

    [Fact]
    public async Task MainViewModel_Debounce_DelaysRefreshAndFiresOnce()
    {
        var tag = new Tag { Name = "Work" };
        _context.Tags.Add(tag);
        _context.SaveChanges();

        var note = new Note { Text = "Some task", CreatedAt = DateTime.Now, UpdatedAt = DateTime.Now };
        note.NoteTags.Add(new NoteTag { TagId = tag.Id });
        _context.Notes.Add(note);
        _context.SaveChanges();

        string settingsPath = Path.Combine(Path.GetTempPath(), $"quicknotes-test-{Guid.NewGuid():N}.json");
        try
        {
            using var hotkey = new GlobalHotkeyService((_, _, _, _) => true, (_, _) => true, IntPtr.Zero);
            using var tray = new TrayIconService();
            var debouncer = new SearchDebouncer(delayMs: 120);

            using var vm = MainViewModelTestComposition.Create(
                () => new QuickNotesDbContext(new DbContextOptionsBuilder<QuickNotesDbContext>().UseSqlite(_connection).Options),
                new TagDetectionService(),
                _searchService,
                new SettingsService(settingsPath, _ => { }),
                hotkey,
                new ClipboardCaptureService(),
                tray,
                searchDebouncer: debouncer,
                draftJournalService: NoOpDraftJournalService.Instance);

            // Initially all notes are loaded
            Assert.Single(vm.Notes);

            // Type non-matching search query
            vm.SearchQuery = "unmatched";

            // Immediately: notes not yet updated because of debounce
            Assert.Single(vm.Notes);

            // Wait for debounce to fire
            await Task.Delay(250);

            // Now notes collection is refreshed and empty
            Assert.Empty(vm.Notes);
        }
        finally
        {
            if (File.Exists(settingsPath)) File.Delete(settingsPath);
        }
    }

    [Fact]
    public void MainViewModel_ManualRefreshNotes_BypassesDebounceImmediately()
    {
        var tag = new Tag { Name = "Work" };
        _context.Tags.Add(tag);
        _context.SaveChanges();

        var note = new Note { Text = "First note", CreatedAt = DateTime.Now, UpdatedAt = DateTime.Now };
        _context.Notes.Add(note);
        _context.SaveChanges();

        string settingsPath = Path.Combine(Path.GetTempPath(), $"quicknotes-test-{Guid.NewGuid():N}.json");
        try
        {
            using var hotkey = new GlobalHotkeyService((_, _, _, _) => true, (_, _) => true, IntPtr.Zero);
            using var tray = new TrayIconService();
            // A long debounce
            var debouncer = new SearchDebouncer(delayMs: 5000);

            using var vm = MainViewModelTestComposition.Create(
                () => new QuickNotesDbContext(new DbContextOptionsBuilder<QuickNotesDbContext>().UseSqlite(_connection).Options),
                new TagDetectionService(),
                _searchService,
                new SettingsService(settingsPath, _ => { }),
                hotkey,
                new ClipboardCaptureService(),
                tray,
                searchDebouncer: debouncer,
                draftJournalService: NoOpDraftJournalService.Instance);

            Assert.Single(vm.Notes);

            // Add note to db
            var note2 = new Note { Text = "Second note", CreatedAt = DateTime.Now, UpdatedAt = DateTime.Now };
            _context.Notes.Add(note2);
            _context.SaveChanges();

            // Calling vm.RefreshNotes() directly updates immediately without waiting 5 seconds
            vm.RefreshNotes();
            Assert.Equal(2, vm.Notes.Count);
        }
        finally
        {
            if (File.Exists(settingsPath)) File.Delete(settingsPath);
        }
    }

    #endregion
}
