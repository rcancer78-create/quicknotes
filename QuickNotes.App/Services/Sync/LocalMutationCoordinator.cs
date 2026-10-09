using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace QuickNotes.App.Services.Sync;

/// <summary>
/// Thread-safe and async-safe implementation of ILocalMutationCoordinator.
/// Adheres strictly to ADR-006:
/// - Coordinated local locks serialize incompatible mutations without blocking unrelated reads.
/// - Distinct notes execute concurrently; identical noteId mutations serialize.
/// - Bulk mutations (Sync apply, import, restore) run exclusively.
/// - Nested lock acquisitions throw InvalidOperationException.
/// - Never holds locks across network I/O.
/// </summary>
public class LocalMutationCoordinator : ILocalMutationCoordinator
{
    private readonly object _stateLock = new();
    private int _activeNoteMutations;
    private bool _isBulkRunning;
    private TaskCompletionSource<bool>? _bulkCompletedTcs;
    private TaskCompletionSource<bool>? _notesDrainedTcs;

    private readonly ConcurrentDictionary<int, SemaphoreSlim> _noteLocks = new();
    private static readonly AsyncLocal<bool> _isLockHeld = new();

    public int ActiveNoteMutationsCount
    {
        get
        {
            lock (_stateLock) return _activeNoteMutations;
        }
    }

    public bool IsBulkRunning
    {
        get
        {
            lock (_stateLock) return _isBulkRunning;
        }
    }

    public async Task<T> ExecuteNoteMutationAsync<T>(int? noteId, Func<Task<T>> mutation, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        EnsureNoNestedLock();

        _isLockHeld.Value = true;
        SemaphoreSlim? noteLock = null;
        try
        {
            await EnterNoteMutationAsync(ct).ConfigureAwait(false);
            try
            {
                if (noteId.HasValue)
                {
                    noteLock = GetNoteLock(noteId.Value);
                    await noteLock.WaitAsync(ct).ConfigureAwait(false);
                }

                return await mutation().ConfigureAwait(false);
            }
            finally
            {
                noteLock?.Release();
                ExitNoteMutation();
            }
        }
        finally
        {
            _isLockHeld.Value = false;
        }
    }

    public async Task ExecuteNoteMutationAsync(int? noteId, Func<Task> mutation, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        await ExecuteNoteMutationAsync(noteId, async () =>
        {
            await mutation().ConfigureAwait(false);
            return true;
        }, ct).ConfigureAwait(false);
    }

    public T ExecuteNoteMutation<T>(int? noteId, Func<T> mutation)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        EnsureNoNestedLock();

        _isLockHeld.Value = true;
        SemaphoreSlim? noteLock = null;
        try
        {
            EnterNoteMutationSync();
            try
            {
                if (noteId.HasValue)
                {
                    noteLock = GetNoteLock(noteId.Value);
                    noteLock.Wait();
                }

                return mutation();
            }
            finally
            {
                noteLock?.Release();
                ExitNoteMutation();
            }
        }
        finally
        {
            _isLockHeld.Value = false;
        }
    }

    public void ExecuteNoteMutation(int? noteId, Action mutation)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        ExecuteNoteMutation(noteId, () =>
        {
            mutation();
            return true;
        });
    }

    public async Task<T> ExecuteSyncApplyAsync<T>(Func<Task<T>> action, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        EnsureNoNestedLock();

        _isLockHeld.Value = true;
        try
        {
            await EnterBulkMutationAsync(ct).ConfigureAwait(false);
            try
            {
                return await action().ConfigureAwait(false);
            }
            finally
            {
                ExitBulkMutation();
            }
        }
        finally
        {
            _isLockHeld.Value = false;
        }
    }

    public async Task ExecuteSyncApplyAsync(Func<Task> action, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        await ExecuteSyncApplyAsync(async () =>
        {
            await action().ConfigureAwait(false);
            return true;
        }, ct).ConfigureAwait(false);
    }

    public async Task<T> ExecuteBulkMutationAsync<T>(Func<Task<T>> action, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        EnsureNoNestedLock();

        _isLockHeld.Value = true;
        try
        {
            await EnterBulkMutationAsync(ct).ConfigureAwait(false);
            try
            {
                return await action().ConfigureAwait(false);
            }
            finally
            {
                ExitBulkMutation();
            }
        }
        finally
        {
            _isLockHeld.Value = false;
        }
    }

    public async Task ExecuteBulkMutationAsync(Func<Task> action, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        await ExecuteBulkMutationAsync(async () =>
        {
            await action().ConfigureAwait(false);
            return true;
        }, ct).ConfigureAwait(false);
    }

    public void ExecuteBulkMutation(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        ExecuteBulkMutation(() =>
        {
            action();
            return true;
        });
    }

    public T ExecuteBulkMutation<T>(Func<T> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        EnsureNoNestedLock();

        _isLockHeld.Value = true;
        try
        {
            EnterBulkMutationSync();
            try
            {
                return action();
            }
            finally
            {
                ExitBulkMutation();
            }
        }
        finally
        {
            _isLockHeld.Value = false;
        }
    }

    private static void EnsureNoNestedLock()
    {
        if (_isLockHeld.Value)
        {
            throw new InvalidOperationException("Nested mutation lock acquisition is prohibited by ADR-006 to prevent deadlocks.");
        }
    }

    private SemaphoreSlim GetNoteLock(int noteId)
    {
        return _noteLocks.GetOrAdd(noteId, _ => new SemaphoreSlim(1, 1));
    }

    private async Task EnterNoteMutationAsync(CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            Task waitBulkTask;
            lock (_stateLock)
            {
                if (!_isBulkRunning)
                {
                    _activeNoteMutations++;
                    return;
                }
                waitBulkTask = _bulkCompletedTcs?.Task ?? Task.CompletedTask;
            }
            await waitBulkTask.WaitAsync(ct).ConfigureAwait(false);
        }
    }

    private void EnterNoteMutationSync()
    {
        lock (_stateLock)
        {
            while (_isBulkRunning)
            {
                Monitor.Wait(_stateLock);
            }
            _activeNoteMutations++;
        }
    }

    private void ExitNoteMutation()
    {
        lock (_stateLock)
        {
            _activeNoteMutations--;
            if (_activeNoteMutations == 0)
            {
                _notesDrainedTcs?.TrySetResult(true);
                Monitor.PulseAll(_stateLock);
            }
        }
    }

    private async Task EnterBulkMutationAsync(CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            Task waitBulkTask;
            lock (_stateLock)
            {
                if (!_isBulkRunning)
                {
                    _isBulkRunning = true;
                    _bulkCompletedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    break;
                }
                waitBulkTask = _bulkCompletedTcs?.Task ?? Task.CompletedTask;
            }
            await waitBulkTask.WaitAsync(ct).ConfigureAwait(false);
        }

        Task waitNotesTask;
        lock (_stateLock)
        {
            if (_activeNoteMutations == 0)
            {
                return;
            }
            _notesDrainedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            waitNotesTask = _notesDrainedTcs.Task;
        }

        try
        {
            await waitNotesTask.WaitAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            ExitBulkMutation();
            throw;
        }
    }

    private void EnterBulkMutationSync()
    {
        lock (_stateLock)
        {
            while (_isBulkRunning)
            {
                Monitor.Wait(_stateLock);
            }
            _isBulkRunning = true;
            _bulkCompletedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            while (_activeNoteMutations > 0)
            {
                Monitor.Wait(_stateLock);
            }
        }
    }

    private void ExitBulkMutation()
    {
        lock (_stateLock)
        {
            _isBulkRunning = false;
            _notesDrainedTcs = null;
            _bulkCompletedTcs?.TrySetResult(true);
            Monitor.PulseAll(_stateLock);
        }
    }
}
