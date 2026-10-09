using System;
using System.Threading;
using System.Threading.Tasks;

namespace QuickNotes.App.Helpers;

/// <summary>
/// Coalesces rapid successive requests into a single execution after a delay.
/// Guarantees that within the delay window, multiple requests result in at most one execution.
/// Supports safe cancellation and disposal without leaking unobserved tasks.
/// </summary>
public class ActionCoalescer : IDisposable
{
    private readonly int _delayMs;
    private readonly Action<Action> _dispatcher;
    private readonly object _lock = new();
    private CancellationTokenSource? _cts;
    private Action? _pendingAction;
    private bool _hasPending;
    private bool _disposed;

    public int DelayMs => _delayMs;

    public bool HasPending
    {
        get { lock (_lock) return _hasPending; }
    }

    public ActionCoalescer(int delayMs = 50, Action<Action>? dispatcher = null)
    {
        _delayMs = delayMs;
        _dispatcher = dispatcher ?? (action => action());
    }

    /// <summary>
    /// Requests that the specified action be executed after the coalescing delay.
    /// If an execution is already scheduled, the request is coalesced into the pending run.
    /// </summary>
    public void Request(Action action)
    {
        lock (_lock)
        {
            if (_disposed) return;

            _pendingAction = action;

            if (_delayMs <= 0)
            {
                _pendingAction = null;
                _dispatcher(action);
                return;
            }

            if (_hasPending)
            {
                return;
            }

            _hasPending = true;
            _cts?.Cancel();
            _cts?.Dispose();
            var cts = new CancellationTokenSource();
            _cts = cts;
            var token = cts.Token;

            _ = Task.Delay(_delayMs, token).ContinueWith(t =>
            {
                if (!t.IsCompletedSuccessfully || token.IsCancellationRequested)
                {
                    return;
                }

                Action? actToRun;
                lock (_lock)
                {
                    if (_disposed || token.IsCancellationRequested)
                    {
                        return;
                    }
                    _hasPending = false;
                    actToRun = _pendingAction;
                    _pendingAction = null;
                }

                if (actToRun != null)
                {
                    _dispatcher(actToRun);
                }
            }, TaskScheduler.Default);
        }
    }

    public void Cancel()
    {
        lock (_lock)
        {
            _hasPending = false;
            _pendingAction = null;
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            _hasPending = false;
            _pendingAction = null;
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
        }
    }
}
