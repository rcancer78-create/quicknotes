using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using QuickNotes.App.Data;
using QuickNotes.App.Models;
using QuickNotes.App.Services;
using QuickNotes.App.Services.DraftJournal;
using QuickNotes.App.ViewModels;
using Xunit;

namespace QuickNotes.Tests;

[TestCategory(TestCategories.Integration)]
public class NoteLinkTests
{
    private static (QuickNotesDbContext Context, SqliteConnection Connection) CreateInMemoryDb()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<QuickNotesDbContext>()
            .UseSqlite(connection)
            .Options;

        var context = new QuickNotesDbContext(options);
        DbInitializer.Initialize(context);

        return (context, connection);
    }

    [Fact]
    public void ExtractLinkedNoteIds_ValidMarkers_ReturnsExtractedPositiveIds()
    {
        var service = new NoteLinkService();
        var text = "Смотри заметку [[123]] и ещё [[456]], а также [[7]].";

        var ids = service.ExtractLinkedNoteIds(text);

        Assert.Equal(new[] { 123, 456, 7 }, ids);
    }

    [Fact]
    public void ExtractLinkedNoteIds_RepeatedMarkers_ReturnsSingleOccurrencePreservingOrder()
    {
        var service = new NoteLinkService();
        var text = "Повтор [[42]] и снова [[42]] и далее [[10]] и опять [[42]] и [[20]].";

        var ids = service.ExtractLinkedNoteIds(text);

        Assert.Equal(new[] { 42, 10, 20 }, ids);
    }

    [Theory]
    [InlineData("[[0]]")]
    [InlineData("[[-1]]")]
    [InlineData("[[-100]]")]
    [InlineData("[[abc]]")]
    [InlineData("[[123abc]]")]
    [InlineData("[[abc123]]")]
    [InlineData("[[99999999999999999999999999999999999999999999999]]")] // int overflow
    [InlineData("[[]]")]
    [InlineData("[[   ]]")]
    [InlineData("[123]")]
    [InlineData("123")]
    [InlineData("[[123")]
    [InlineData("123]]")]
    [InlineData("[[[123]]]")] // inner [123] inside [[...]] is invalid int
    [InlineData("")]
    [InlineData(null)]
    public void ExtractLinkedNoteIds_InvalidOrMalformedMarkers_AreIgnored(string? input)
    {
        var service = new NoteLinkService();

        var ids = service.ExtractLinkedNoteIds(input);

        Assert.Empty(ids);
    }

    [Fact]
    public void ExtractLinkedNoteIds_SurroundedByWhitespaceInsideBrackets_ExtractsCleanId()
    {
        var service = new NoteLinkService();
        var text = "Текст [[ 99 ]] пробелы.";

        var ids = service.ExtractLinkedNoteIds(text);

        Assert.Equal(new[] { 99 }, ids);
    }

    [Fact]
    public void FormatLink_FormatsStandardWikiLink()
    {
        var service = new NoteLinkService();
        Assert.Equal("[[154]]", service.FormatLink(154));
    }

    [Fact]
    public void OutgoingAndIncomingLinks_ReturnsCorrectResultsForActiveNotes()
    {
        var (db, conn) = CreateInMemoryDb();
        using (conn)
        using (db)
        {
            var noteA = new Note { Text = "Заметка A ссылается на [[2]] и [[3]]" };
            var noteB = new Note { Text = "Заметка B (целевая)\nВторая строка с деталями" };
            var noteC = new Note { Text = "Заметка C (ещё одна)\nПодробности" };
            db.Notes.AddRange(noteA, noteB, noteC);
            db.SaveChanges();

            var service = new NoteLinkService();

            // 1. Outgoing from noteA (id=1)
            var outgoing = service.GetOutgoingLinks(db, noteA.Text);
            Assert.Equal(2, outgoing.Count);

            Assert.Equal(noteB.Id, outgoing[0].TargetNoteId);
            Assert.Equal("Заметка B (целевая)", outgoing[0].Title);
            Assert.True(outgoing[0].IsAvailable);

            Assert.Equal(noteC.Id, outgoing[1].TargetNoteId);
            Assert.Equal("Заметка C (ещё одна)", outgoing[1].Title);
            Assert.True(outgoing[1].IsAvailable);

            // 2. Incoming to noteB (id=2)
            var incomingB = service.GetIncomingLinks(db, noteB.Id);
            Assert.Single(incomingB);
            Assert.Equal(noteA.Id, incomingB[0].TargetNoteId);
            Assert.Equal("Заметка A ссылается на [[2]] и [[3]]", incomingB[0].Title);
            Assert.True(incomingB[0].IsAvailable);

            // 3. Incoming to noteA (id=1) - no one links to A
            var incomingA = service.GetIncomingLinks(db, noteA.Id);
            Assert.Empty(incomingA);
        }
    }

    [Fact]
    public void OutgoingLinks_NonExistentOrUnavailableId_ShowsUnavailableTitleAndDisabledState()
    {
        var (db, conn) = CreateInMemoryDb();
        using (conn)
        using (db)
        {
            var noteA = new Note { Text = "Ссылка на несуществующую заметку [[9999]]" };
            db.Notes.Add(noteA);
            db.SaveChanges();

            var service = new NoteLinkService();
            var outgoing = service.GetOutgoingLinks(db, noteA.Text);

            Assert.Single(outgoing);
            Assert.Equal(9999, outgoing[0].TargetNoteId);
            Assert.Equal(NoteLinkService.UnavailableNoteTitle, outgoing[0].Title);
            Assert.False(outgoing[0].IsAvailable);
        }
    }

    [Fact]
    public void TargetMovedToTrash_OutgoingLinkShowsUnavailable_AndSourceTextNotModified()
    {
        var (db, conn) = CreateInMemoryDb();
        using (conn)
        using (db)
        {
            var noteSource = new Note { Text = "Источник со ссылкой на [[2]]" };
            var noteTarget = new Note { Text = "Цель для ссылки" };
            db.Notes.AddRange(noteSource, noteTarget);
            db.SaveChanges();

            var service = new NoteLinkService();

            // Initially available
            var initialOutgoing = service.GetOutgoingLinks(db, noteSource.Text);
            Assert.True(initialOutgoing[0].IsAvailable);
            Assert.Equal("Цель для ссылки", initialOutgoing[0].Title);

            // Move target to trash
            noteTarget.DeletedAt = DateTime.Now;
            db.SaveChanges();

            // Refresh source from DB: Source note text must NOT be modified
            var refreshedSource = db.Notes.Find(noteSource.Id);
            Assert.NotNull(refreshedSource);
            Assert.Equal("Источник со ссылкой на [[2]]", refreshedSource.Text);

            // Outgoing link should now report "Заметка недоступна"
            var outgoingAfterTrash = service.GetOutgoingLinks(db, refreshedSource.Text);
            Assert.Single(outgoingAfterTrash);
            Assert.Equal(noteTarget.Id, outgoingAfterTrash[0].TargetNoteId);
            Assert.Equal(NoteLinkService.UnavailableNoteTitle, outgoingAfterTrash[0].Title);
            Assert.False(outgoingAfterTrash[0].IsAvailable);

            // When target is restored from trash
            noteTarget.DeletedAt = null;
            db.SaveChanges();

            var outgoingAfterRestore = service.GetOutgoingLinks(db, refreshedSource.Text);
            Assert.Single(outgoingAfterRestore);
            Assert.True(outgoingAfterRestore[0].IsAvailable);
            Assert.Equal("Цель для ссылки", outgoingAfterRestore[0].Title);
        }
    }

    [Fact]
    public void IncomingLinks_TargetInTrash_ExcludesDeletedNotesFromIncomingLinks()
    {
        var (db, conn) = CreateInMemoryDb();
        using (conn)
        using (db)
        {
            var noteA = new Note { Text = "Активная ссылка на [[3]]" };
            var noteB = new Note { Text = "Удалённая ссылка на [[3]]", DeletedAt = DateTime.Now };
            var noteTarget = new Note { Text = "Целевая заметка 3" };
            db.Notes.AddRange(noteA, noteB, noteTarget);
            db.SaveChanges();

            var service = new NoteLinkService();
            var incoming = service.GetIncomingLinks(db, noteTarget.Id);

            Assert.Single(incoming);
            Assert.Equal(noteA.Id, incoming[0].TargetNoteId);
        }
    }

    [Fact]
    public void NoteLinkPicker_ExcludesCurrentNoteAndDeletedNotes()
    {
        var notes = new List<Note>
        {
            new() { Id = 1, Text = "Заметка 1", UpdatedAt = DateTime.Now.AddMinutes(-30) },
            new() { Id = 2, Text = "Заметка 2 (текущая)", UpdatedAt = DateTime.Now.AddMinutes(-20) },
            new() { Id = 3, Text = "Заметка 3", UpdatedAt = DateTime.Now.AddMinutes(-10) },
            new() { Id = 4, Text = "Заметка 4 в корзине", DeletedAt = DateTime.Now, UpdatedAt = DateTime.Now }
        };

        var vm = new NoteLinkPickerViewModel(notes, currentNoteId: 2);

        Assert.Equal(2, vm.FilteredNotes.Count);
        Assert.DoesNotContain(vm.FilteredNotes, n => n.Id == 2);
        Assert.DoesNotContain(vm.FilteredNotes, n => n.Id == 4);
        Assert.Contains(vm.FilteredNotes, n => n.Id == 1);
        Assert.Contains(vm.FilteredNotes, n => n.Id == 3);
    }

    [Fact]
    public void NoteLinkPicker_SearchByIdAndText_FiltersCorrectly()
    {
        var notes = new List<Note>
        {
            new() { Id = 10, Text = "Список покупок: хлеб, сыр" },
            new() { Id = 42, Text = "Отчёт по проекту Альфа" },
            new() { Id = 105, Text = "Заметки про отпуск" }
        };

        var vm = new NoteLinkPickerViewModel(notes, currentNoteId: null);

        // Search by ID
        vm.SearchText = "42";
        Assert.Single(vm.FilteredNotes);
        Assert.Equal(42, vm.FilteredNotes[0].Id);
        Assert.Equal(42, vm.SelectedNote?.Id);
        Assert.StartsWith("[[qn:", vm.FormattedLink);
        Assert.Contains(vm.SelectedNote!.SyncId.ToString("D"), vm.FormattedLink);

        // Search by text fragment
        vm.SearchText = "покупок";
        Assert.Single(vm.FilteredNotes);
        Assert.Equal(10, vm.FilteredNotes[0].Id);

        // Clear search
        vm.SearchText = "";
        Assert.Equal(3, vm.FilteredNotes.Count);
    }

    [Fact]
    public void NoteLinkPicker_InsertAndCancelCommands_TriggerRequestCloseWithResult()
    {
        var notes = new List<Note>
        {
            new() { Id = 5, Text = "Единственная заметка" }
        };

        var vm = new NoteLinkPickerViewModel(notes);
        bool? closedResult = null;
        vm.RequestClose += res => closedResult = res;

        Assert.True(vm.CanInsert);
        vm.InsertCommand.Execute(null);
        Assert.True(closedResult);

        closedResult = null;
        vm.CancelCommand.Execute(null);
        Assert.False(closedResult);
    }

    [Fact]
    public void NoteEditorViewModel_InsertLinkCommand_InsertsMarkerAndRefreshesOutgoingLinks()
    {
        var (db, conn) = CreateInMemoryDb();
        using (conn)
        using (db)
        {
            var target = new Note { Text = "Целевая заметка для ссылки" };
            db.Notes.Add(target);
            db.SaveChanges();

            using var vm = new NoteEditorViewModel(
                new TagDetectionService(),
                new List<Tag>(),
                existingNote: null,
                initialText: "Привет",
                contextFactory: () => new QuickNotesDbContext(
                    new DbContextOptionsBuilder<QuickNotesDbContext>().UseSqlite(conn).Options),
                draftJournalService: NoOpDraftJournalService.Instance);

            vm.PickLinkNoteId = () => target.Id;

            // Execute insert link
            vm.InsertLinkCommand.Execute(null);

            Assert.Contains($"[[{target.Id}]]", vm.Text);
            Assert.True(vm.HasOutgoingLinks);
            Assert.Single(vm.OutgoingLinks);
            Assert.Equal(target.Id, vm.OutgoingLinks[0].TargetNoteId);
            Assert.Equal("Целевая заметка для ссылки", vm.OutgoingLinks[0].Title);
            Assert.True(vm.OutgoingLinks[0].IsAvailable);
        }
    }

    [Fact]
    public void NoteEditorViewModel_EditingText_UpdatesOutgoingLinksReactively()
    {
        var (db, conn) = CreateInMemoryDb();
        using (conn)
        using (db)
        {
            var target = new Note { Text = "Вторая заметка" };
            db.Notes.Add(target);
            db.SaveChanges();

            using var vm = new NoteEditorViewModel(
                new TagDetectionService(),
                new List<Tag>(),
                existingNote: null,
                initialText: "Обычный текст",
                contextFactory: () => new QuickNotesDbContext(
                    new DbContextOptionsBuilder<QuickNotesDbContext>().UseSqlite(conn).Options),
                draftJournalService: NoOpDraftJournalService.Instance);

            Assert.False(vm.HasOutgoingLinks);
            Assert.Empty(vm.OutgoingLinks);

            // Type a marker into Text
            vm.Text = $"Текст с маркером [[{target.Id}]]";

            Assert.True(vm.HasOutgoingLinks);
            Assert.Single(vm.OutgoingLinks);
            Assert.Equal(target.Id, vm.OutgoingLinks[0].TargetNoteId);

            // Remove the marker
            vm.Text = "Снова обычный текст";
            Assert.False(vm.HasOutgoingLinks);
            Assert.Empty(vm.OutgoingLinks);
        }
    }

    [Fact]
    public void NoteEditorViewModel_OpenCommandOnLinkItem_InvokesRequestOpenLinkedNote()
    {
        var (db, conn) = CreateInMemoryDb();
        using (conn)
        using (db)
        {
            var target = new Note { Text = "Связанная заметка" };
            db.Notes.Add(target);
            db.SaveChanges();

            using var vm = new NoteEditorViewModel(
                new TagDetectionService(),
                new List<Tag>(),
                existingNote: null,
                initialText: $"Ссылка [[{target.Id}]]",
                contextFactory: () => new QuickNotesDbContext(
                    new DbContextOptionsBuilder<QuickNotesDbContext>().UseSqlite(conn).Options),
                draftJournalService: NoOpDraftJournalService.Instance);

            int? requestedId = null;
            vm.RequestOpenLinkedNote += id => requestedId = id;

            var linkItem = vm.OutgoingLinks[0];
            Assert.True(linkItem.OpenCommand.CanExecute(null));
            linkItem.OpenCommand.Execute(null);

            Assert.Equal(target.Id, requestedId);
        }
    }

    [Fact]
    public void NoteLinkItemViewModel_UnavailableNote_OpenCommandCannotExecute()
    {
        bool wasCalled = false;
        var item = new NoteLinkItemViewModel(999, NoteLinkService.UnavailableNoteTitle, isAvailable: false, id => wasCalled = true);

        Assert.False(item.IsAvailable);
        Assert.False(item.OpenCommand.CanExecute(null));
        Assert.False(wasCalled);
    }

    [Fact]
    public void NoteHistory_TextWithMarkers_IsSavedAndRestoredProperly()
    {
        var (db, conn) = CreateInMemoryDb();
        using (conn)
        using (db)
        {
            var historyService = new NoteHistoryService();
            var note = new Note { Text = "Версия 1 без ссылок" };
            db.Notes.Add(note);
            db.SaveChanges();
            historyService.SaveSnapshot(db, note);

            note.Text = "Версия 2 со ссылкой [[42]]";
            db.SaveChanges();
            historyService.SaveSnapshot(db, note);

            var history = historyService.GetHistory(db, note.Id);
            Assert.Equal(2, history.Count);

            // Restore version 1
            var restored = historyService.RestoreRevision(db, note.Id, history[1].Id);
            Assert.Equal("Версия 1 без ссылок", restored.Text);
        }
    }

    [Fact]
    public void TargetPermanentlyDeleted_OutgoingLinkShowsUnavailable_AndTextNotModified()
    {
        var (db, conn) = CreateInMemoryDb();
        using (conn)
        using (db)
        {
            var noteSource = new Note { Text = "Текст со ссылкой [[2]]" };
            var noteTarget = new Note { Text = "Удаляемая навсегда цель" };
            db.Notes.AddRange(noteSource, noteTarget);
            db.SaveChanges();

            var service = new NoteLinkService();

            // Permanently delete target note
            db.Notes.Remove(noteTarget);
            db.SaveChanges();

            // Text of source note must NOT be modified
            var refreshedSource = db.Notes.Find(noteSource.Id);
            Assert.NotNull(refreshedSource);
            Assert.Equal("Текст со ссылкой [[2]]", refreshedSource.Text);

            var outgoing = service.GetOutgoingLinks(db, refreshedSource.Text);
            Assert.Single(outgoing);
            Assert.Equal(2, outgoing[0].TargetNoteId);
            Assert.Equal(NoteLinkService.UnavailableNoteTitle, outgoing[0].Title);
            Assert.False(outgoing[0].IsAvailable);
        }
    }

    [Fact]
    public void IncomingLinks_MultipleMarkersInSameSourceNote_ReturnsSingleIncomingEntry()
    {
        var (db, conn) = CreateInMemoryDb();
        using (conn)
        using (db)
        {
            var noteTarget = new Note { Text = "Цель" };
            var noteSource = new Note { Text = "Многократная ссылка [[1]] и [[1]] и ещё [[1]]" };
            db.Notes.AddRange(noteTarget, noteSource);
            db.SaveChanges();

            var service = new NoteLinkService();
            var incoming = service.GetIncomingLinks(db, noteTarget.Id);

            Assert.Single(incoming);
            Assert.Equal(noteSource.Id, incoming[0].TargetNoteId);
        }
    }

    [Fact]
    public void NoteCardViewModel_DoesNotExposeOrLoadLinks()
    {
        var note = new Note
        {
            Id = 1,
            Text = "Заметка со ссылкой [[2]]",
            CreatedAt = DateTime.Now,
            UpdatedAt = DateTime.Now
        };

        var card = new NoteCardViewModel(note);

        // Verify NoteCardViewModel has no links collections or properties
        Assert.Null(card.GetType().GetProperty("OutgoingLinks"));
        Assert.Null(card.GetType().GetProperty("IncomingLinks"));
    }

    [Theory]
    [InlineData("[[123]]", 123)]
    [InlineData("[[ 123]]", 123)]
    [InlineData("[[123 ]]", 123)]
    [InlineData("[[ 123 ]]", 123)]
    public void ExtractLinkedNoteIds_AllWhitespaceVariants_ExtractsExpectedId(string marker, int expectedId)
    {
        var service = new NoteLinkService();
        var ids = service.ExtractLinkedNoteIds($"Заметка с маркером {marker} в тексте");

        Assert.Single(ids);
        Assert.Equal(expectedId, ids[0]);
    }

    [Fact]
    public void ExtractLinkedNoteIds_DifferentTargetId_DoesNotMatchBaseId()
    {
        var service = new NoteLinkService();
        var ids = service.ExtractLinkedNoteIds("Текст со ссылкой на [[1234]]");

        Assert.Single(ids);
        Assert.Equal(1234, ids[0]);
        Assert.DoesNotContain(123, ids);
    }

    [Fact]
    public void GetIncomingLinks_AllFourWhitespaceVariants_AreFoundForTargetId()
    {
        var (db, conn) = CreateInMemoryDb();
        using (conn)
        using (db)
        {
            var targetNote = new Note { Id = 123, Text = "Целевая заметка #123" };
            var noteNoSpace = new Note { Text = "Ссылка без пробелов: [[123]]" };
            var noteLeadingSpace = new Note { Text = "Ссылка с ведущим пробелом: [[ 123]]" };
            var noteTrailingSpace = new Note { Text = "Ссылка с замыкающим пробелом: [[123 ]]" };
            var noteBothSpaces = new Note { Text = "Ссылка с пробелами с обеих сторон: [[ 123 ]]" };

            db.Notes.AddRange(targetNote, noteNoSpace, noteLeadingSpace, noteTrailingSpace, noteBothSpaces);
            db.SaveChanges();

            var service = new NoteLinkService();
            var incoming = service.GetIncomingLinks(db, 123);

            Assert.Equal(4, incoming.Count);
            var incomingSourceIds = incoming.Select(i => i.TargetNoteId).ToHashSet();
            Assert.Contains(noteNoSpace.Id, incomingSourceIds);
            Assert.Contains(noteLeadingSpace.Id, incomingSourceIds);
            Assert.Contains(noteTrailingSpace.Id, incomingSourceIds);
            Assert.Contains(noteBothSpaces.Id, incomingSourceIds);
            Assert.All(incoming, item => Assert.True(item.IsAvailable));
        }
    }

    [Fact]
    public void GetIncomingLinks_MarkerWithExtraDigits_DoesNotProduceFalsePositiveForBaseId()
    {
        var (db, conn) = CreateInMemoryDb();
        using (conn)
        using (db)
        {
            var targetNote = new Note { Id = 123, Text = "Целевая заметка #123" };
            var noteWithSimilarId = new Note { Text = "Ссылка на заметку с похожим ID: [[1234]]" };
            db.Notes.AddRange(targetNote, noteWithSimilarId);
            db.SaveChanges();

            var service = new NoteLinkService();

            // When searching for incoming links to note 123, [[1234]] must NOT match
            var incoming123 = service.GetIncomingLinks(db, 123);
            Assert.Empty(incoming123);

            // But searching for 1234 should find it
            var incoming1234 = service.GetIncomingLinks(db, 1234);
            Assert.Single(incoming1234);
            Assert.Equal(noteWithSimilarId.Id, incoming1234[0].TargetNoteId);
        }
    }

    [Fact]
    public void GetIncomingLinks_InitialUnconfirmedCandidates_DoNotHideConfirmedLinksWhenLimitApplies()
    {
        var (db, conn) = CreateInMemoryDb();
        using (conn)
        using (db)
        {
            var targetNote = new Note { Id = 123, Text = "Целевая заметка #123" };
            db.Notes.Add(targetNote);

            // Create 105 notes that match the SQL LIKE '%[[%123%]]%' filter but contain [[1234]], not [[123]]
            var falseCandidates = new List<Note>();
            for (int i = 0; i < 105; i++)
            {
                falseCandidates.Add(new Note { Text = $"Кандидат #{i} со ссылкой на [[1234]]" });
            }
            db.Notes.AddRange(falseCandidates);

            // Create real links with various whitespaces that come after the 105 false candidates
            var realLink1 = new Note { Text = "Настоящая ссылка [[ 123 ]]" };
            var realLink2 = new Note { Text = "Ещё одна настоящая ссылка [[123]]" };
            db.Notes.AddRange(realLink1, realLink2);
            db.SaveChanges();

            var service = new NoteLinkService();
            var incoming = service.GetIncomingLinks(db, 123);

            // Confirm that the false candidates did not consume the limit and hide real links
            Assert.Equal(2, incoming.Count);
            Assert.Equal(realLink1.Id, incoming[0].TargetNoteId);
            Assert.Equal(realLink2.Id, incoming[1].TargetNoteId);
        }
    }

    [Fact]
    public void GetIncomingLinks_CandidateLimiter_EnforcesMaximumCandidateLimitBeforeToList()
    {
        // 1. Verify existence and boundary requirements of the candidate limiter constant
        Assert.True(NoteLinkService.MaxIncomingCandidates >= 500, "Candidate limit must be at least 500.");
        Assert.True(NoteLinkService.MaxIncomingCandidates > NoteLinkService.MaxIncomingLinks, "Candidate limit must be noticeably higher than MaxIncomingLinks.");

        var (db, conn) = CreateInMemoryDb();
        using (conn)
        using (db)
        {
            var targetNote = new Note { Text = "Целевая заметка" };
            db.Notes.Add(targetNote);
            db.SaveChanges();

            int targetId = targetNote.Id;

            // Valid link within the candidate limit
            var realLinkWithinLimit = new Note { Text = $"Ссылка в пределах лимита [[{targetId}]]" };
            db.Notes.Add(realLinkWithinLimit);

            // Fill candidate budget with false candidates matching SQL LIKE '%[[%{targetId}%]]%'
            var falseCandidates = new List<Note>();
            for (int i = 0; i < NoteLinkService.MaxIncomingCandidates; i++)
            {
                falseCandidates.Add(new Note { Text = $"Кандидат #{i} со ссылкой на [[{targetId}99]]" });
            }
            db.Notes.AddRange(falseCandidates);

            // Valid link placed beyond the candidate limit
            var realLinkBeyondLimit = new Note { Text = $"Ссылка за пределами лимита [[ {targetId} ]]" };
            db.Notes.Add(realLinkBeyondLimit);
            db.SaveChanges();

            var service = new NoteLinkService();
            var incoming = service.GetIncomingLinks(db, targetId);

            // The candidate limiter must cap SQL evaluation to MaxIncomingCandidates before ToList,
            // so realLinkWithinLimit is included, but realLinkBeyondLimit is excluded.
            Assert.Single(incoming);
            Assert.Equal(realLinkWithinLimit.Id, incoming[0].TargetNoteId);
            Assert.DoesNotContain(incoming, item => item.TargetNoteId == realLinkBeyondLimit.Id);
        }
    }

    [Fact]
    public void GetIncomingLinks_BelowLimit_IsTruncatedIsFalse()
    {
        var (db, conn) = CreateInMemoryDb();
        using (conn)
        using (db)
        {
            var targetNote = new Note { Text = "Целевая заметка для проверки лимита" };
            db.Notes.Add(targetNote);
            db.SaveChanges();

            int targetId = targetNote.Id;

            // Add several valid links well below candidate limit
            var note1 = new Note { Text = $"Ссылка 1 на [[{targetId}]]" };
            var note2 = new Note { Text = $"Ссылка 2 на [[ {targetId} ]]" };
            var note3 = new Note { Text = $"Ссылка 3 на [[{targetId} ]]" };
            db.Notes.AddRange(note1, note2, note3);
            db.SaveChanges();

            var service = new NoteLinkService();

            // 1. Check via GetIncomingLinksResult
            var result = service.GetIncomingLinksResult(db, targetId);
            Assert.False(result.IsTruncated);
            Assert.False(result.IsLimited);
            Assert.Equal(3, result.Count);
            Assert.Equal(3, result.Items.Count);
            Assert.Equal(3, result.Links.Count);

            // 2. Check via GetIncomingLinks (backward compatible method returning IncomingLinksResult)
            var incoming = service.GetIncomingLinks(db, targetId);
            Assert.False(incoming.IsTruncated);
            Assert.Equal(3, incoming.Count);
        }
    }

    [Fact]
    public void GetIncomingLinks_ExactlyAtCandidateLimit_IsTruncatedIsFalse()
    {
        var (db, conn) = CreateInMemoryDb();
        using (conn)
        using (db)
        {
            var targetNote = new Note { Text = "Целевая заметка ровно на лимите" };
            db.Notes.Add(targetNote);
            db.SaveChanges();

            int targetId = targetNote.Id;

            // 1 valid link
            var validLink = new Note { Text = $"Ссылка на [[{targetId}]]" };
            db.Notes.Add(validLink);

            // MaxIncomingCandidates - 1 false candidates, making total candidate count exactly MaxIncomingCandidates (500)
            var falseCandidates = new List<Note>();
            for (int i = 0; i < NoteLinkService.MaxIncomingCandidates - 1; i++)
            {
                falseCandidates.Add(new Note { Text = $"Кандидат #{i} со ссылкой на [[{targetId}99]]" });
            }
            db.Notes.AddRange(falseCandidates);
            db.SaveChanges();

            var service = new NoteLinkService();
            var result = service.GetIncomingLinksResult(db, targetId);

            // Exactly at candidate limit: no candidate beyond limit, so IsTruncated must be false
            Assert.False(result.IsTruncated);
            Assert.Single(result);
            Assert.Equal(validLink.Id, result[0].TargetNoteId);
        }
    }

    [Fact]
    public void GetIncomingLinks_CandidateBeyondLimit_IsTruncatedIsTrue()
    {
        var (db, conn) = CreateInMemoryDb();
        using (conn)
        using (db)
        {
            var targetNote = new Note { Text = "Целевая заметка с кандидатом за пределом" };
            db.Notes.Add(targetNote);
            db.SaveChanges();

            int targetId = targetNote.Id;

            // 1 valid link within candidate limit
            var validLinkWithinLimit = new Note { Text = $"Действительная ссылка в пределах лимита [[{targetId}]]" };
            db.Notes.Add(validLinkWithinLimit);

            // Exactly MaxIncomingCandidates false candidates matching SQL LIKE
            var falseCandidates = new List<Note>();
            for (int i = 0; i < NoteLinkService.MaxIncomingCandidates; i++)
            {
                falseCandidates.Add(new Note { Text = $"Кандидат #{i} со ссылкой на [[{targetId}99]]" });
            }
            db.Notes.AddRange(falseCandidates);

            // 1 candidate beyond limit (501st candidate overall)
            var candidateBeyondLimit = new Note { Text = $"Кандидат за пределом [[ {targetId} ]]" };
            db.Notes.Add(candidateBeyondLimit);
            db.SaveChanges();

            var service = new NoteLinkService();

            // 1. GetIncomingLinksResult must report IsTruncated = true
            var result = service.GetIncomingLinksResult(db, targetId);
            Assert.True(result.IsTruncated);
            Assert.True(result.IsLimited);
            // Only candidates within candidate limit are evaluated, so candidateBeyondLimit is excluded
            Assert.Single(result);
            Assert.Equal(validLinkWithinLimit.Id, result[0].TargetNoteId);

            // 2. GetIncomingLinks also reflects IsTruncated = true
            var incoming = service.GetIncomingLinks(db, targetId);
            Assert.True(incoming.IsTruncated);
            Assert.Single(incoming);
        }
    }

    [Fact]
    public void NoteEditorViewModel_IncomingLinksTruncation_VisibleStringConditionBelowLimit()
    {
        var (db, conn) = CreateInMemoryDb();
        using (conn)
        using (db)
        {
            var targetNote = new Note { Text = "Целевая заметка для редактора (ниже лимита)" };
            db.Notes.Add(targetNote);
            db.SaveChanges();

            int targetId = targetNote.Id;

            var linkNote1 = new Note { Text = $"Ссылка на [[{targetId}]]" };
            var linkNote2 = new Note { Text = $"Вторая ссылка на [[ {targetId} ]]" };
            db.Notes.AddRange(linkNote1, linkNote2);
            db.SaveChanges();

            using var vm = new NoteEditorViewModel(
                new TagDetectionService(),
                new List<Tag>(),
                existingNote: targetNote,
                contextFactory: () => new QuickNotesDbContext(
                    new DbContextOptionsBuilder<QuickNotesDbContext>().UseSqlite(conn).Options),
                draftJournalService: NoOpDraftJournalService.Instance);

            // Below limit: truncation flag is false
            Assert.False(vm.IsIncomingLinksTruncated);
            Assert.False(vm.HasIncomingLinksTruncated);
            Assert.False(vm.IsIncomingLinksTruncatedVisible);
            Assert.False(vm.ShowIncomingLinksTruncatedNotice);

            // Visible string condition: string is empty when not truncated
            Assert.Equal(string.Empty, vm.IncomingLinksTruncatedText);
            Assert.True(string.IsNullOrEmpty(vm.IncomingLinksTruncatedText));

            // All links shown
            Assert.Equal(2, vm.IncomingLinks.Count);
            Assert.True(vm.HasIncomingLinks);
        }
    }

    [Fact]
    public void NoteEditorViewModel_IncomingLinksTruncation_VisibleStringConditionCandidateBeyondLimit()
    {
        var (db, conn) = CreateInMemoryDb();
        using (conn)
        using (db)
        {
            var targetNote = new Note { Text = "Целевая заметка для редактора (кандидат за пределом)" };
            db.Notes.Add(targetNote);
            db.SaveChanges();

            int targetId = targetNote.Id;

            // 1 valid link within candidate limit
            var validLink = new Note { Text = $"Ссылка в пределах лимита [[{targetId}]]" };
            db.Notes.Add(validLink);

            // 500 false candidates matching LIKE pattern
            var falseCandidates = new List<Note>();
            for (int i = 0; i < NoteLinkService.MaxIncomingCandidates; i++)
            {
                falseCandidates.Add(new Note { Text = $"Кандидат #{i} со ссылкой на [[{targetId}99]]" });
            }
            db.Notes.AddRange(falseCandidates);

            // 1 candidate beyond limit (501st candidate)
            var beyondLimit = new Note { Text = $"Ссылка за пределом [[{targetId}]]" };
            db.Notes.Add(beyondLimit);
            db.SaveChanges();

            using var vm = new NoteEditorViewModel(
                new TagDetectionService(),
                new List<Tag>(),
                existingNote: targetNote,
                contextFactory: () => new QuickNotesDbContext(
                    new DbContextOptionsBuilder<QuickNotesDbContext>().UseSqlite(conn).Options),
                draftJournalService: NoOpDraftJournalService.Instance);

            // Candidate beyond limit: truncation flag is true
            Assert.True(vm.IsIncomingLinksTruncated);
            Assert.True(vm.HasIncomingLinksTruncated);
            Assert.True(vm.IsIncomingLinksTruncatedVisible);
            Assert.True(vm.ShowIncomingLinksTruncatedNotice);

            // Visible string condition: exact required message is present
            Assert.Equal("Показана часть ссылок; уточните текст или сократите число заметок", vm.IncomingLinksTruncatedText);
            Assert.Equal(NoteEditorViewModel.IncomingLinksTruncatedMessage, vm.IncomingLinksTruncatedText);

            // Links evaluated within candidate budget are present
            Assert.Single(vm.IncomingLinks);
            Assert.Equal(validLink.Id, vm.IncomingLinks[0].TargetNoteId);
        }
    }

    [Fact]
    public void NoteEditorViewModel_RefreshIncomingLinks_RaisesPropertyChangedEventsForTruncation()
    {
        var (db, conn) = CreateInMemoryDb();
        using (conn)
        using (db)
        {
            var targetNote = new Note { Text = "Заметка для проверки PropertyChanged" };
            db.Notes.Add(targetNote);
            db.SaveChanges();

            using var vm = new NoteEditorViewModel(
                new TagDetectionService(),
                new List<Tag>(),
                existingNote: targetNote,
                contextFactory: () => new QuickNotesDbContext(
                    new DbContextOptionsBuilder<QuickNotesDbContext>().UseSqlite(conn).Options),
                draftJournalService: NoOpDraftJournalService.Instance);

            var changedProperties = new List<string>();
            vm.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName != null)
                {
                    changedProperties.Add(e.PropertyName);
                }
            };

            vm.RefreshIncomingLinks();

            Assert.Contains(nameof(vm.IsIncomingLinksTruncated), changedProperties);
            Assert.Contains(nameof(vm.IncomingLinksTruncatedText), changedProperties);
            Assert.Contains(nameof(vm.HasIncomingLinksTruncated), changedProperties);
        }
    }
}

