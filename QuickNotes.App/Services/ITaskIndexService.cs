using System.Threading;
using System.Threading.Tasks;
using QuickNotes.App.Models;

namespace QuickNotes.App.Services;

public interface ITaskIndexService
{
    Task<TaskIndexPage> QueryAsync(
        string? searchQuery,
        int skip,
        int take,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Open unprotected tasks that have a valid due date. Used by the reminder scheduler.
    /// </summary>
    IReadOnlyList<MarkdownTaskLocator> QueryDatedOpenTasks(CancellationToken cancellationToken = default);
}
