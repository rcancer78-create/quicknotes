using System;
using System.Threading;
using System.Threading.Tasks;

namespace QuickNotes.App.Helpers;

public class SearchDebouncer : IDisposable
{
    private readonly int _delayMs;
    private readonly Action<Action> _dispatcher;
    private CancellationTokenSource? _cts;
    private readonly object _lock = new();

    public int DelayMs => _delayMs;
    public bool HasPending { get; private set; }

    public SearchDebouncer(int delayMs = 300, Action<Action>? dispatcher = null)
    {
        _delayMs = delayMs;
        _dispatcher = dispatcher ?? (action => action());
    }

    public void Debounce(Action action)
    {
        if (_delayMs <= 0)
        {
            Cancel();
            action();
            return;
        }

        lock (_lock)
        {
            _cts?.Cancel();
            _cts?.Dispose();
            var cts = new CancellationTokenSource();
            _cts = cts;
            HasPending = true;
            var token = cts.Token;

            _ = Task.Delay(_delayMs, token).ContinueWith(t =>
            {
                if (t.IsCompletedSuccessfully && !token.IsCancellationRequested)
                {
                    lock (_lock)
                    {
                        if (!token.IsCancellationRequested)
                        {
                            HasPending = false;
                            _dispatcher(action);
                        }
                    }
                }
            }, TaskScheduler.Default);
        }
    }

    public void Cancel()
    {
        lock (_lock)
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
            HasPending = false;
        }
    }

    public void Dispose()
    {
        Cancel();
    }
}
