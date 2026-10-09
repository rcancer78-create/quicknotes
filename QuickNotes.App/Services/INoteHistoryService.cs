using System.Collections.Generic;
using QuickNotes.App.Data;
using QuickNotes.App.Models;

namespace QuickNotes.App.Services;

public interface INoteHistoryService
{
    NoteRevision? SaveSnapshot(QuickNotesDbContext db, Note note, int maxRevisions = 20);

    List<NoteHistoryItemDto> GetHistory(QuickNotesDbContext db, int noteId);

    Note RestoreRevision(QuickNotesDbContext db, int noteId, int revisionId, int maxRevisions = 20);

    string ComputeDiffSummary(
        string? oldText,
        IReadOnlyCollection<NoteRevisionTagSnapshot>? oldTags,
        string newText,
        IReadOnlyCollection<NoteRevisionTagSnapshot> newTags);
}
