using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace QuickNotes.App.Services;

public interface IOcrWindowVisibilityCoordinator
{
    IOcrWindowScope HideQuickNotesWindows();
}

public interface IOcrWindowScope : IDisposable
{
    bool WasMainWindowVisible { get; }
    IReadOnlyList<Window> HiddenWindows { get; }
    Task WaitForDesktopCompositionAsync(CancellationToken cancellationToken = default);
    void RestoreWindows();
}
