using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using QuickNotes.App.Models.DraftJournal;

namespace QuickNotes.App.Services.DraftJournal;

/// <summary>
/// Deterministic no-op implementation of IDraftJournalService.
/// Performs no disk I/O and never touches the filesystem or user profile.
/// Used for deterministic testing and as a safe default when journal persistence is not needed.
/// </summary>
public sealed class NoOpDraftJournalService : IDraftJournalService
{
    public static readonly NoOpDraftJournalService Instance = new();

    public string JournalsDirectory => string.Empty;

    public string GenerateNewDraftId() => $"new-{Guid.NewGuid():N}";

    public string GetDraftIdForNote(int noteId) => $"note-{noteId}";

    public Task<DraftSaveResult> SaveJournalAsync(DraftJournalSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(DraftSaveResult.Ok(snapshot.SequenceNumber, DateTime.UtcNow));
    }

    public DraftJournalReadResult ReadJournal(string draftId)
    {
        return DraftJournalReadResult.NotFound();
    }

    public DraftJournalReadResult ReadJournalForNote(int noteId)
    {
        return DraftJournalReadResult.NotFound();
    }

    public IReadOnlyList<DraftJournalReadResult> GetAllJournals()
    {
        return Array.Empty<DraftJournalReadResult>();
    }

    public bool DeleteJournal(string draftId) => true;

    public bool DeleteJournalIfContentMatches(string draftId, string? committedTitle, string? committedText) => true;

    public bool HasJournal(string draftId) => false;

    public bool HasJournalForNote(int noteId) => false;
}
