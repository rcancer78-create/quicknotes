using System;
using System.Threading;
using System.Threading.Tasks;

namespace QuickNotes.App.Services.Sync;

/// <summary>
/// Profile-scoped coordinator for operations that mutate confirmed local state.
/// Enforces ADR-006 lock ordering:
/// 1. Coordinated local locks serialize incompatible mutations without blocking unrelated reads.
/// 2. Mutations to distinct notes execute concurrently; mutations to the same note are serialized.
/// 3. Sync apply, bulk import, and restore acquire exclusive bulk scope.
/// 4. Nested lock acquisitions are strictly prohibited to prevent deadlocks.
/// 5. Never hold a local mutation lock while awaiting network I/O.
/// 6. Enqueue of Sync occurs strictly after local database transaction commit.
/// </summary>
public interface ILocalMutationCoordinator
{
    /// <summary>
    /// Executes a note-specific mutation (create, edit, delete, protect, attachment change) within a scoped lock.
    /// Serializes mutations for the same noteId; allows concurrent mutations across distinct notes.
    /// </summary>
    Task<T> ExecuteNoteMutationAsync<T>(int? noteId, Func<Task<T>> mutation, CancellationToken ct = default);

    /// <summary>
    /// Executes a note-specific mutation asynchronously without return value.
    /// </summary>
    Task ExecuteNoteMutationAsync(int? noteId, Func<Task> mutation, CancellationToken ct = default);

    /// <summary>
    /// Executes a note-specific mutation synchronously within a scoped lock.
    /// </summary>
    T ExecuteNoteMutation<T>(int? noteId, Func<T> mutation);

    /// <summary>
    /// Executes a note-specific mutation synchronously without return value.
    /// </summary>
    void ExecuteNoteMutation(int? noteId, Action mutation);

    /// <summary>
    /// Executes Sync apply (importing remote changes into local SQLite).
    /// Exclusive with respect to note mutations.
    /// Network I/O must remain outside this call.
    /// </summary>
    Task<T> ExecuteSyncApplyAsync<T>(Func<Task<T>> action, CancellationToken ct = default);

    /// <summary>
    /// Executes Sync apply asynchronously without return value.
    /// </summary>
    Task ExecuteSyncApplyAsync(Func<Task> action, CancellationToken ct = default);

    /// <summary>
    /// Executes a bulk mutation (bulk import, database restore, maintenance).
    /// Exclusive across all note mutations and Sync apply.
    /// </summary>
    Task<T> ExecuteBulkMutationAsync<T>(Func<Task<T>> action, CancellationToken ct = default);

    /// <summary>
    /// Executes a bulk mutation asynchronously without return value.
    /// </summary>
    Task ExecuteBulkMutationAsync(Func<Task> action, CancellationToken ct = default);

    /// <summary>
    /// Executes a bulk mutation synchronously without return value.
    /// </summary>
    void ExecuteBulkMutation(Action action);

    /// <summary>
    /// Executes a bulk mutation synchronously and returns its result.
    /// </summary>
    T ExecuteBulkMutation<T>(Func<T> action);

    /// <summary>
    /// Number of active note mutations currently executing. (Useful for testing and diagnostics)
    /// </summary>
    int ActiveNoteMutationsCount { get; }

    /// <summary>
    /// Indicates whether an exclusive bulk operation or Sync apply is currently executing.
    /// </summary>
    bool IsBulkRunning { get; }
}
