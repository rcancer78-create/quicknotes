using System.Threading;
using QuickNotes.App.Data;
using QuickNotes.App.Models;
using QuickNotes.App.Services.Sync;

namespace QuickNotes.App.Services;

public interface INoteAssemblyService
{
    NoteAssemblyPreviewResult BuildPreview(QuickNotesDbContext db, NoteAssemblyPreviewRequest request);

    NoteAssemblyExecuteResult Execute(
        QuickNotesDbContext db,
        NoteAssemblyExecuteRequest request,
        ILocalMutationCoordinator mutationCoordinator,
        INoteHistoryService? historyService = null,
        CancellationToken cancellationToken = default);
}
