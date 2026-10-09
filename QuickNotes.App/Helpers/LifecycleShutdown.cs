using System;
using System.Threading;
using System.Threading.Tasks;
using QuickNotes.App.Services;

namespace QuickNotes.App.Helpers;

/// <summary>
/// Bounded wait used only during process/window teardown. Interactive UI work
/// must use <see cref="BoundedOperation.RunAsync{T}"/> instead (ADR-014).
/// </summary>
public static class LifecycleShutdown
{
    public static bool Wait(Task? task, TimeSpan timeout)
        => WaitOwned(task, timeout, cancelOnTimeout: null, disposeWhenStillRunning: true);

    /// <summary>
    /// Waits for owned teardown. On timeout, cancels <paramref name="cancelOnTimeout"/> and
    /// waits again so callers can dispose dependencies only after the shutdown task has
    /// reached a terminal state. Returns <c>false</c> if the task is still running.
    /// </summary>
    public static bool WaitOwned(
        Task? task,
        TimeSpan timeout,
        CancellationTokenSource? cancelOnTimeout,
        bool disposeWhenStillRunning = false)
    {
        if (task == null)
        {
            return true;
        }

        if (task.IsCompleted)
        {
            Observe(task);
            return true;
        }

        try
        {
            if (task.Wait(timeout))
            {
                Observe(task);
                return true;
            }

            try
            {
                cancelOnTimeout?.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            ErrorLogService.Write("LifecycleShutdown", "bounded wait timed out; cancelling owned teardown");

            if (task.Wait(timeout))
            {
                Observe(task);
                return true;
            }

            ErrorLogService.Write("LifecycleShutdown", "owned teardown still running");
            if (disposeWhenStillRunning)
            {
                ObserveOrphan(task);
            }

            return false;
        }
        catch (AggregateException ae)
        {
            ErrorLogService.Write("LifecycleShutdown", ae.GetBaseException());
            return true;
        }
        catch (Exception ex)
        {
            ErrorLogService.Write("LifecycleShutdown", ex);
            return true;
        }
    }

    private static void Observe(Task task)
    {
        if (task.IsFaulted)
        {
            ErrorLogService.Write("LifecycleShutdown", task.Exception!.GetBaseException());
        }
    }

    private static void ObserveOrphan(Task task)
        => BoundedOperation.ObserveOrphan(task, "LifecycleShutdown.Orphan");
}
