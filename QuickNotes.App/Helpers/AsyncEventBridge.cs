using System;
using System.Threading.Tasks;
using QuickNotes.App.Services;

namespace QuickNotes.App.Helpers;

/// <summary>
/// Owned observation for WPF/event/command bridges that must be <c>async void</c>
/// or <see cref="Action"/> at the framework boundary (ADR-014).
/// </summary>
public static class AsyncEventBridge
{
    public static void Fire(Func<Task> work, string scope, Action? restoreSafeState = null)
    {
        Run(work, scope, restoreSafeState);
    }

    public static async void Run(Func<Task> work, string scope, Action? restoreSafeState = null)
    {
        ArgumentNullException.ThrowIfNull(work);
        try
        {
            await work().ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ErrorLogService.Write(scope, ex);
        }
        finally
        {
            try
            {
                restoreSafeState?.Invoke();
            }
            catch (Exception restoreEx)
            {
                ErrorLogService.Write(scope + ".Restore", restoreEx);
            }
        }
    }
}
