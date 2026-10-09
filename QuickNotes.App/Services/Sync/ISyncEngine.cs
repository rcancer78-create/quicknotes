using System;
using System.Threading;
using System.Threading.Tasks;
using QuickNotes.App.Data;
using QuickNotes.App.Models.Sync;

namespace QuickNotes.App.Services.Sync;

/// <summary>
/// Autonomous engine for multi-device encrypted synchronization over object storage.
/// Coordinates pull-before-push cycles, single-flight gating, crash/retry safe uploads,
/// durable conflict tracking, and deterministic tombstone propagation.
/// Network failures never block or disrupt local note operations.
/// </summary>
public interface ISyncEngine
{
    /// <summary>
    /// Executes a single safe sync cycle using the provided DbContext:
    /// 1. Pulls and imports remote changes, recording durable conflicts without silent overwrites.
    /// 2. If local changes exist, creates and uploads exactly one immutable package and updates technical device pointer.
    /// 3. If no local changes exist, push is a no-op and creates zero cloud objects.
    /// </summary>
    Task<SyncCycleResult> RunSyncCycleAsync(
        QuickNotesDbContext db,
        string encryptionPassword,
        SyncCycleOptions? options = null,
        IProgress<SyncProgressReport>? progress = null,
        CancellationToken ct = default);

    /// <summary>
    /// Executes a single safe sync cycle using the default or configured DbContext factory.
    /// </summary>
    Task<SyncCycleResult> RunSyncCycleAsync(
        string encryptionPassword,
        SyncCycleOptions? options = null,
        IProgress<SyncProgressReport>? progress = null,
        CancellationToken ct = default);

    /// <summary>
    /// Retrieves current configuration, sync status, pending conflicts count, and last upload info.
    /// </summary>
    Task<SyncEngineStatus> GetStatusAsync(QuickNotesDbContext? db = null, CancellationToken ct = default);
}
