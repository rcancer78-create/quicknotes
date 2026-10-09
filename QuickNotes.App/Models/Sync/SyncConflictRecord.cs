using System;

namespace QuickNotes.App.Models.Sync;

/// <summary>
/// Durable conflict record stored in SQLite table SyncConflicts.
/// Preserves both local and remote entity snapshots for future UI resolution (both/local/remote/merge).
/// Strictly contains no secrets (no passwords, keys, DPAPI data).
/// </summary>
public class SyncConflictRecord
{
    public int Id { get; set; }
    public Guid SyncId { get; set; }
    public string EntityType { get; set; } = string.Empty;
    public Guid? LocalRevisionId { get; set; }
    public Guid RemoteRevisionId { get; set; }
    public Guid? ParentRevisionId { get; set; }
    public Guid SourceDeviceId { get; set; }
    public Guid? SourcePackageId { get; set; }
    public DateTime DetectedAtUtc { get; set; } = DateTime.UtcNow;
    public string Reason { get; set; } = string.Empty;

    /// <summary>
    /// Serialized snapshot of the local entity at detection time (plaintext note/tag/template data, no secrets).
    /// </summary>
    public string? LocalDataJson { get; set; }

    /// <summary>
    /// Serialized snapshot of the incoming remote entity from the package (no secrets).
    /// </summary>
    public string? RemoteDataJson { get; set; }

    public bool IsResolved { get; set; }
    public DateTime? ResolvedAtUtc { get; set; }
    public string? ResolutionAction { get; set; }
}
