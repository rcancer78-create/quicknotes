using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Application = System.Windows.Application;
using QuickNotes.App.Helpers;
using QuickNotes.App.Views;

namespace QuickNotes.App.Services;

public class OcrWindowVisibilityCoordinator : IOcrWindowVisibilityCoordinator
{
    private const int CompositionWaitTimeoutMs = 250;

    private readonly Func<IEnumerable<Window>> _windowProvider;
    private readonly Func<CancellationToken, Task>? _dwmWaitFunc;

    public OcrWindowVisibilityCoordinator(
        Func<IEnumerable<Window>>? windowProvider = null,
        Func<CancellationToken, Task>? dwmWaitFunc = null)
    {
        _windowProvider = windowProvider ?? GetDefaultWindows;
        _dwmWaitFunc = dwmWaitFunc;
    }

    private static IEnumerable<Window> GetDefaultWindows()
    {
        var app = Application.Current;
        if (app == null || app.Dispatcher == null ||
            app.Dispatcher.HasShutdownStarted || app.Dispatcher.HasShutdownFinished)
        {
            return Enumerable.Empty<Window>();
        }

        if (!app.Dispatcher.CheckAccess())
        {
            try
            {
                return app.Dispatcher.Invoke(
                    () => app.Windows.OfType<Window>().ToList(),
                    DispatcherPriority.Normal,
                    CancellationToken.None,
                    TimeSpan.FromMilliseconds(250));
            }
            catch
            {
                return Enumerable.Empty<Window>();
            }
        }

        return app.Windows.OfType<Window>().ToList();
    }

    public IOcrWindowScope HideQuickNotesWindows()
    {
        List<Window> windows;
        try
        {
            windows = _windowProvider().ToList();
        }
        catch
        {
            windows = new List<Window>();
        }

        bool wasMainWindowVisible = CaptureWasMainWindowVisible(windows);
        var snapshots = new List<WindowStateSnapshot>();

        foreach (var window in windows)
        {
            if (window is ScreenCropOverlayWindow)
            {
                continue;
            }

            var snapshot = TryHideVisibleWindow(window);
            if (snapshot != null)
            {
                snapshots.Add(snapshot);
            }
        }

        return new OcrWindowScope(snapshots, wasMainWindowVisible, _dwmWaitFunc);
    }

    /// <summary>
    /// Visibility for bring-to-front after OCR is taken only from loaded
    /// <see cref="MainWindow"/> instances in this operation's window list.
    /// Leftover Import/Settings dialogs (including when WPF assigns one to
    /// <see cref="Application.MainWindow"/>) must not count. No loaded main
    /// window means there is no tray-hidden QuickNotes UI, so restore is allowed.
    /// Dead or foreign-thread fixtures that cannot be read are ignored.
    /// </summary>
    private static bool CaptureWasMainWindowVisible(IReadOnlyList<Window> windows)
    {
        var liveMains = windows
            .OfType<MainWindow>()
            .Where(w => ReadOnWindowThread(w, static x => x.IsLoaded, false))
            .ToList();

        if (liveMains.Count == 0)
        {
            return true;
        }

        return liveMains.Any(w => ReadOnWindowThread(w, static x => x.IsVisible, false));
    }

    private static WindowStateSnapshot? TryHideVisibleWindow(Window window)
    {
        return InvokeOnWindowThread(window, w =>
        {
            if (!w.IsVisible)
            {
                return null;
            }

            var snapshot = new WindowStateSnapshot(
                w,
                w.WindowState,
                w.IsActive,
                w.Topmost,
                w.Visibility);

            w.Visibility = Visibility.Hidden;
            return snapshot;
        }, fallback: null);
    }

    private static T ReadOnWindowThread<T>(Window window, Func<Window, T> read, T fallback)
        => InvokeOnWindowThread(window, read, fallback);

    private static T InvokeOnWindowThread<T>(Window window, Func<Window, T> action, T fallback)
    {
        try
        {
            var dispatcher = window.Dispatcher;
            if (dispatcher == null || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
            {
                return fallback;
            }

            if (dispatcher.CheckAccess())
            {
                return action(window);
            }

            var operation = dispatcher.Invoke(
                () => action(window),
                DispatcherPriority.Normal,
                CancellationToken.None,
                TimeSpan.FromMilliseconds(CompositionWaitTimeoutMs));
            return operation;
        }
        catch
        {
            return fallback;
        }
    }

    private sealed record WindowStateSnapshot(
        Window Window,
        WindowState State,
        bool IsActive,
        bool Topmost,
        Visibility PreviousVisibility);

    private sealed class OcrWindowScope : IOcrWindowScope
    {
        private readonly List<WindowStateSnapshot> _snapshots;
        private readonly Func<CancellationToken, Task>? _dwmWaitFunc;
        private int _restored;

        public bool WasMainWindowVisible { get; }

        public IReadOnlyList<Window> HiddenWindows => _snapshots.Select(s => s.Window).ToList();

        public OcrWindowScope(
            List<WindowStateSnapshot> snapshots,
            bool wasMainWindowVisible,
            Func<CancellationToken, Task>? dwmWaitFunc)
        {
            _snapshots = snapshots;
            WasMainWindowVisible = wasMainWindowVisible;
            _dwmWaitFunc = dwmWaitFunc;
        }

        public async Task WaitForDesktopCompositionAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (_dwmWaitFunc != null)
            {
                await _dwmWaitFunc(cancellationToken).ConfigureAwait(false);
                return;
            }

            // DwmFlush and a synchronous Dispatcher.Invoke can block forever when
            // Application.Current exists but its dispatcher is not pumping (typical in
            // testhost after theme tests construct App). Bound both waits and keep going
            // so capture/OCR can still complete; RestoreWindows remains responsible for cleanup.
            await FlushDwmBoundedAsync(cancellationToken).ConfigureAwait(false);
            await PumpDispatcherBackgroundBoundedAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
        }

        public void RestoreWindows()
        {
            if (Interlocked.Exchange(ref _restored, 1) != 0)
            {
                return;
            }

            RestoreWindowsCore();
        }

        private void RestoreWindowsCore()
        {
            foreach (var snapshot in _snapshots)
            {
                InvokeOnWindowThread(snapshot.Window, window =>
                {
                    try
                    {
                        window.WindowState = snapshot.State;
                        window.Visibility = snapshot.PreviousVisibility;
                        window.Topmost = snapshot.Topmost;
                        if (snapshot.IsActive)
                        {
                            window.Activate();
                        }
                    }
                    catch (Exception ex)
                    {
                        ErrorLogService.Write("OcrWindowScope.RestoreWindows", ex);
                    }

                    return true;
                }, fallback: false);
            }
        }

        public void Dispose()
        {
            RestoreWindows();
        }
    }

    private static Task FlushDwmBoundedAsync(CancellationToken cancellationToken)
    {
        // testhost has no desktop composition loop; DwmFlush has aborted the process
        // (native AV) after other tests constructed a WPF Application without Run().
        var processName = System.Diagnostics.Process.GetCurrentProcess().ProcessName;
        if (processName.StartsWith("testhost", StringComparison.OrdinalIgnoreCase))
        {
            return Task.CompletedTask;
        }
        var flushCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var flushThread = new Thread(() =>
        {
            try
            {
                Win32Helper.DwmFlush();
            }
            catch
            {
                // DwmFlush may fail on headless/non-DWM environments; ignore safely.
            }
            finally
            {
                flushCompleted.TrySetResult();
            }
        })
        {
            IsBackground = true,
            Name = "QuickNotes.DwmFlush"
        };

        flushThread.Start();
        return WaitBoundedAsync(flushCompleted.Task, cancellationToken);
    }

    private static async Task PumpDispatcherBackgroundBoundedAsync(CancellationToken cancellationToken)
    {
        var app = Application.Current;
        var dispatcher = app?.Dispatcher;
        if (dispatcher == null || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
        {
            return;
        }

        try
        {
            var operation = dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
            await WaitBoundedAsync(operation.Task, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
        }
    }

    private static async Task WaitBoundedAsync(Task waitTask, CancellationToken cancellationToken)
    {
        try
        {
            await waitTask.WaitAsync(TimeSpan.FromMilliseconds(CompositionWaitTimeoutMs), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
        }
    }
}
