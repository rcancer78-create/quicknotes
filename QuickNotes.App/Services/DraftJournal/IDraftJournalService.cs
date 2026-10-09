using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using QuickNotes.App.Models.DraftJournal;

namespace QuickNotes.App.Services.DraftJournal;

/// <summary>
/// Service for managing local asynchronous, atomic draft journals.
/// Draft journals are kept separate from SQLite DB, revisions, and cloud synchronization.
/// </summary>
public interface IDraftJournalService
{
    /// <summary>
    /// Directory where journal files are stored.
    /// </summary>
    string JournalsDirectory { get; }

    /// <summary>
    /// Generates a unique collision-free identifier for a new note draft.
    /// </summary>
    string GenerateNewDraftId();

    /// <summary>
    /// Gets the standard safe identifier for an existing note draft.
    /// </summary>
    string GetDraftIdForNote(int noteId);

    /// <summary>
    /// Persists a draft snapshot asynchronously and atomically.
    /// Guarantees that protected notes are encrypted with current session key,
    /// and that obsolete sequence numbers cannot overwrite newer drafts.
    /// </summary>
    Task<DraftSaveResult> SaveJournalAsync(DraftJournalSnapshot snapshot, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads and validates a draft journal by draftId.
    /// </summary>
    DraftJournalReadResult ReadJournal(string draftId);

    /// <summary>
    /// Reads and validates a draft journal for an existing noteId.
    /// </summary>
    DraftJournalReadResult ReadJournalForNote(int noteId);

    /// <summary>
    /// Returns all journals currently found in the journals directory.
    /// </summary>
    IReadOnlyList<DraftJournalReadResult> GetAllJournals();

    /// <summary>
    /// Deletes the draft journal file if present.
    /// </summary>
    bool DeleteJournal(string draftId);

    /// <summary>
    /// Deletes the journal only if its content matches the committed content,
    /// preventing stale callbacks from deleting newer drafts.
    /// </summary>
    bool DeleteJournalIfContentMatches(string draftId, string? committedTitle, string? committedText);

    /// <summary>
    /// Checks whether a journal exists on disk for the given draftId.
    /// </summary>
    bool HasJournal(string draftId);

    /// <summary>
    /// Checks whether a journal exists on disk for the given noteId.
    /// </summary>
    bool HasJournalForNote(int noteId);
}
