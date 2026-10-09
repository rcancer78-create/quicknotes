using System;
using System.Threading;
using System.Threading.Tasks;
using QuickNotes.App.Services;
using QuickNotes.App.Services.EncryptedArchive;
using QuickNotes.App.Services.Sync;

namespace QuickNotes.App.Helpers;

public enum BoundedOperationOutcome
{
    Success,
    Cancelled,
    Timeout,
    Failed
}

/// <summary>
/// How <see cref="BoundedOperation.RunAsync{T}"/> treats work that outlives the wall-clock bound.
/// </summary>
public enum BoundedTimeoutBehavior
{
    /// <summary>
    /// Cancel the work token and wait up to <see cref="BoundedOperation.OwnedTerminalGrace"/> for a
    /// terminal state. A post-deadline completion is never mapped to Success. If work is still
    /// running after grace, it is isolated (observed orphan); ownership stays
    /// <see cref="BoundedOwnershipState.Cancelling"/> until that task terminals so a repeated
    /// mutation cannot overlap. Late local publish must observe the cancelled token before commit.
    /// </summary>
    WaitForOwnedWork = 0,

    /// <summary>
    /// Return timeout immediately, observe the orphan, and ignore a late result (OCR/compute).
    /// Allowed for remote/read-only work only when a late local apply is structurally impossible.
    /// </summary>
    AbandonAndObserve = 1
}

public enum BoundedOwnershipState
{
    Idle = 0,
    Running = 1,
    Cancelling = 2
}

/// <summary>
/// Lease for owned mutations. Held through Running and Cancelling until the owned task is
/// terminal or isolated after grace (late result ignored, no Success).
/// </summary>
public sealed class BoundedOwnership
{
    private readonly SynchronizationContext? _callbackContext;
    private int _state;
    private int _callbacksSuppressed;

    public BoundedOwnership(SynchronizationContext? callbackContext = null)
    {
        _callbackContext = callbackContext ?? SynchronizationContext.Current;
    }

    public event Action? StateChanged;

    /// <summary>Raised when a Cancelling lease returns to Idle after isolated work terminals.</summary>
    public event Action? DrainCompleted;

    public BoundedOwnershipState State => (BoundedOwnershipState)Volatile.Read(ref _state);

    public bool IsHeld => State != BoundedOwnershipState.Idle;

    public bool IsCancelling => State == BoundedOwnershipState.Cancelling;

    /// <summary>Stop marshaled callbacks after the owner VM is closed or disposed.</summary>
    public void SuppressCallbacks() => Volatile.Write(ref _callbacksSuppressed, 1);

    public bool TryAcquire()
    {
        if (Interlocked.CompareExchange(ref _state, (int)BoundedOwnershipState.Running, (int)BoundedOwnershipState.Idle)
            != (int)BoundedOwnershipState.Idle)
        {
            return false;
        }

        Raise(StateChanged);
        return true;
    }

    internal void MarkCancelling()
    {
        Interlocked.CompareExchange(ref _state, (int)BoundedOwnershipState.Cancelling, (int)BoundedOwnershipState.Running);
        Raise(StateChanged);
    }

    internal void Release()
    {
        var previous = (BoundedOwnershipState)Interlocked.Exchange(ref _state, (int)BoundedOwnershipState.Idle);
        Raise(StateChanged);
        if (previous == BoundedOwnershipState.Cancelling)
        {
            Raise(DrainCompleted);
        }
    }

    private void Raise(Action? handler)
    {
        if (handler == null || Volatile.Read(ref _callbacksSuppressed) != 0)
        {
            return;
        }

        SynchronizationContext? ctx = _callbackContext;
        if (ctx == null || ReferenceEquals(SynchronizationContext.Current, ctx))
        {
            InvokeSafe(handler);
            return;
        }

        ctx.Post(_ =>
        {
            if (Volatile.Read(ref _callbacksSuppressed) != 0)
            {
                return;
            }

            InvokeSafe(handler);
        }, null);
    }

    private static void InvokeSafe(Action handler)
    {
        try
        {
            handler();
        }
        catch (Exception ex)
        {
            ErrorLogService.Write("BoundedOwnership.Callback", ex);
        }
    }
}

public readonly struct BoundedOperationResult<T>
{
    public BoundedOperationOutcome Outcome { get; }
    public T? Value { get; }
    public string UserMessage { get; }

    private BoundedOperationResult(BoundedOperationOutcome outcome, T? value, string userMessage)
    {
        Outcome = outcome;
        Value = value;
        UserMessage = userMessage;
    }

    public bool IsSuccess => Outcome == BoundedOperationOutcome.Success;

    public static BoundedOperationResult<T> Success(T value) =>
        new(BoundedOperationOutcome.Success, value, string.Empty);

    public static BoundedOperationResult<T> Cancelled() =>
        new(BoundedOperationOutcome.Cancelled, default, UserFacingOperationError.Cancelled);

    public static BoundedOperationResult<T> Timeout() =>
        new(BoundedOperationOutcome.Timeout, default, UserFacingOperationError.Timeout);

    public static BoundedOperationResult<T> Failed(string userMessage) =>
        new(BoundedOperationOutcome.Failed, default, userMessage);
}

/// <summary>
/// Wall-clock bounds and outcome mapping for user-visible async work (ADR-014).
/// </summary>
public static class BoundedOperation
{
    public static readonly TimeSpan ManualSyncTimeout = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan BackupTimeout = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan OpenExportTimeout = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan ImportTimeout = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan EncryptedArchiveTimeout = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan OcrRecognizeTimeout = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan CloudTestTimeout = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan ShutdownDrainTimeout = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan ShutdownWaitTimeout = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan EditorPendingSaveTimeout = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan BrowserUrlCaptureTimeout = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Finite wait after the wall-clock deadline before isolating still-running owned work.
    /// Tests may shorten this; production default is two seconds.
    /// </summary>
    internal static TimeSpan OwnedTerminalGrace { get; set; } = TimeSpan.FromSeconds(2);

    public static Task<BoundedOperationResult<T>> RunAsync<T>(
        Func<CancellationToken, Task<T>> work,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        string logScope)
        => RunAsync(work, timeout, cancellationToken, logScope, BoundedTimeoutBehavior.WaitForOwnedWork);

    public static Task<BoundedOperationResult<T>> RunAsync<T>(
        Func<CancellationToken, Task<T>> work,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        string logScope,
        BoundedTimeoutBehavior timeoutBehavior)
        => RunAsync(work, timeout, cancellationToken, logScope, timeoutBehavior, ownership: null);

    public static async Task<BoundedOperationResult<T>> RunAsync<T>(
        Func<CancellationToken, Task<T>> work,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        string logScope,
        BoundedTimeoutBehavior timeoutBehavior,
        BoundedOwnership? ownership)
    {
        ArgumentNullException.ThrowIfNull(work);

        if (ownership != null && !ownership.TryAcquire())
        {
            return BoundedOperationResult<T>.Failed(UserFacingOperationError.Overlap);
        }

        bool isolateOwnership = false;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(timeout);
        Task<T>? running = null;
        try
        {
            running = work(linked.Token);
            T value = await running.WaitAsync(linked.Token).ConfigureAwait(false);
            return BoundedOperationResult<T>.Success(value);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            var result = await FinishAfterAbortAsync(running, timeoutBehavior, callerCancelled: true, logScope, ownership)
                .ConfigureAwait(false);
            isolateOwnership = ownership != null && ownership.IsCancelling;
            return result;
        }
        catch (OperationCanceledException)
        {
            var result = await FinishAfterAbortAsync(running, timeoutBehavior, callerCancelled: false, logScope, ownership)
                .ConfigureAwait(false);
            isolateOwnership = ownership != null && ownership.IsCancelling;
            return result;
        }
        catch (TimeoutException)
        {
            var result = await FinishAfterAbortAsync(running, timeoutBehavior, callerCancelled: false, logScope, ownership)
                .ConfigureAwait(false);
            isolateOwnership = ownership != null && ownership.IsCancelling;
            return result;
        }
        catch (Exception ex)
        {
            ErrorLogService.Write(logScope, ex);
            return BoundedOperationResult<T>.Failed(UserFacingOperationError.For(ex));
        }
        finally
        {
            if (!isolateOwnership)
            {
                ownership?.Release();
            }
        }
    }

    public static void ObserveOrphan(Task? running, string logScope, Action? onCompleted = null)
    {
        if (running == null)
        {
            InvokeCompleted(onCompleted);
            return;
        }

        if (running.IsCompleted)
        {
            if (running.IsFaulted)
            {
                ErrorLogService.Write(logScope, running.Exception!.GetBaseException());
            }

            InvokeCompleted(onCompleted);
            return;
        }

        _ = ObserveAsync(running, logScope, onCompleted);
    }

    private static async Task<BoundedOperationResult<T>> FinishAfterAbortAsync<T>(
        Task<T>? running,
        BoundedTimeoutBehavior timeoutBehavior,
        bool callerCancelled,
        string logScope,
        BoundedOwnership? ownership)
    {
        BoundedOperationResult<T> aborted = callerCancelled
            ? BoundedOperationResult<T>.Cancelled()
            : BoundedOperationResult<T>.Timeout();

        if (running == null)
        {
            return aborted;
        }

        if (timeoutBehavior == BoundedTimeoutBehavior.AbandonAndObserve)
        {
            ObserveOrphan(running, logScope);
            return aborted;
        }

        try
        {
            await running.WaitAsync(OwnedTerminalGrace).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            ownership?.MarkCancelling();
            ObserveOrphan(running, logScope, () => ownership?.Release());
            return aborted;
        }
        catch (OperationCanceledException)
        {
            return aborted;
        }
        catch (Exception ex)
        {
            ErrorLogService.Write(logScope, ex);
            return BoundedOperationResult<T>.Failed(UserFacingOperationError.For(ex));
        }

        if (running.IsFaulted)
        {
            Exception ex = running.Exception!.GetBaseException();
            ErrorLogService.Write(logScope, ex);
            return BoundedOperationResult<T>.Failed(UserFacingOperationError.For(ex));
        }

        // Cooperative cancel or a late result: never Success after the deadline.
        return aborted;
    }

    private static async Task ObserveAsync(Task running, string logScope, Action? onCompleted)
    {
        try
        {
            await running.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ErrorLogService.Write(logScope + ".Orphan", ex);
        }
        finally
        {
            InvokeCompleted(onCompleted);
        }
    }

    private static void InvokeCompleted(Action? onCompleted)
    {
        if (onCompleted == null)
        {
            return;
        }

        try
        {
            onCompleted();
        }
        catch (Exception ex)
        {
            ErrorLogService.Write("BoundedOperation.OwnershipRelease", ex);
        }
    }
}

public static class UserFacingOperationError
{
    public const string Cancelled = "Операция отменена.";
    public const string Timeout = "Операция прервана по тайм-ауту.";
    public const string Overlap = "Предыдущая операция ещё выполняется.";
    public const string GenericFailure = "Операция не выполнена. Подробности записаны в журнал.";

    public static string For(Exception ex)
    {
        if (ex is EncryptedArchiveException)
        {
            return ex.Message;
        }

        if (ex is SyncValidationException)
        {
            return ex.Message;
        }

        return GenericFailure;
    }
}
