using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using QuickNotes.App.Models.Sync;

namespace QuickNotes.App.Services.Sync;

public interface ISyncConflictService
{
    Task<List<SyncConflictRecord>> GetUnresolvedConflictsAsync(CancellationToken ct = default);
    Task<int> GetUnresolvedConflictsCountAsync(CancellationToken ct = default);
    Task<SyncConflictDetail?> GetConflictDetailAsync(int conflictId, CancellationToken ct = default);
    Task<SyncConflictResolutionResult> ResolveKeepBothAsync(int conflictId, CancellationToken ct = default);
    Task<SyncConflictResolutionResult> ResolveKeepLocalAsync(int conflictId, CancellationToken ct = default);
    Task<SyncConflictResolutionResult> ResolveAcceptRemoteAsync(int conflictId, CancellationToken ct = default);
    Task<SyncConflictResolutionResult> ResolveMergeNoteAsync(int conflictId, string mergedText, CancellationToken ct = default);

    Task<SyncConflictResolutionResult> ResolveMergeNoteAsync(
        int conflictId,
        string mergedText,
        SyncConflictMergeChoices choices,
        CancellationToken ct = default)
        => ResolveMergeNoteAsync(conflictId, mergedText, ct);
}
