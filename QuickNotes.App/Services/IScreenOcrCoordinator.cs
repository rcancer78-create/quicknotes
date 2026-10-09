using System;
using System.Threading;
using System.Threading.Tasks;
using QuickNotes.App.Models;

namespace QuickNotes.App.Services;

public interface IScreenOcrCoordinator
{
    bool LastWasMainWindowVisible { get; }

    Task<ScreenOcrResult> ExecuteOcrWorkflowAsync(
        Action<string>? onProgress = null,
        CancellationToken cancellationToken = default,
        TimeSpan? recognizeTimeout = null);
}
