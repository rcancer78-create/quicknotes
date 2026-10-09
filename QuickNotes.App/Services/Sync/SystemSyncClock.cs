using System;
using System.Threading;

namespace QuickNotes.App.Services.Sync;

/// <summary>
/// Production clock and timer factory using standard System.Threading.Timer.
/// </summary>
public class SystemSyncClock : ISyncClock
{
    public DateTime UtcNow => DateTime.UtcNow;
    public DateTime Now => DateTime.Now;

    public ISyncTimer CreateTimer(Action callback)
    {
        return new SystemSyncTimer(callback);
    }

    private sealed class SystemSyncTimer : ISyncTimer
    {
        private readonly Action _callback;
        private System.Threading.Timer? _timer;
        private bool _isActive;
        private readonly object _lock = new();

        public bool IsActive
        {
            get
            {
                lock (_lock)
                {
                    return _isActive;
                }
            }
        }

        public SystemSyncTimer(Action callback)
        {
            _callback = callback ?? throw new ArgumentNullException(nameof(callback));
        }

        public void Change(TimeSpan dueTime, TimeSpan period = default)
        {
            lock (_lock)
            {
                _isActive = dueTime != Timeout.InfiniteTimeSpan;
                var safePeriod = period == default ? Timeout.InfiniteTimeSpan : period;

                if (_timer == null)
                {
                    _timer = new System.Threading.Timer(_ =>
                    {
                        lock (_lock)
                        {
                            if (safePeriod == Timeout.InfiniteTimeSpan)
                            {
                                _isActive = false;
                            }
                        }
                        _callback();
                    }, null, dueTime, safePeriod);
                }
                else
                {
                    _timer.Change(dueTime, safePeriod);
                }
            }
        }

        public void Stop()
        {
            lock (_lock)
            {
                _isActive = false;
                _timer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                _isActive = false;
                _timer?.Dispose();
                _timer = null;
            }
        }
    }
}
