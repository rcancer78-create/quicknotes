using System;

namespace QuickNotes.App.Services.Sync;

/// <summary>
/// Abstraction for timer scheduling to support safe time virtualization in tests
/// without real Task.Delay or minutes-long thread blocking.
/// </summary>
public interface ISyncTimer : IDisposable
{
    /// <summary>
    /// Changes the start time and the interval between method invocations.
    /// </summary>
    void Change(TimeSpan dueTime, TimeSpan period = default);

    /// <summary>
    /// Stops the timer.
    /// </summary>
    void Stop();

    /// <summary>
    /// Whether the timer is currently active and scheduled.
    /// </summary>
    bool IsActive { get; }
}

/// <summary>
/// Clock and timer factory abstraction for the sync coordinator.
/// </summary>
public interface ISyncClock
{
    DateTime UtcNow { get; }
    DateTime Now { get; }

    /// <summary>
    /// Creates a timer that invokes the specified callback when due.
    /// </summary>
    ISyncTimer CreateTimer(Action callback);
}
