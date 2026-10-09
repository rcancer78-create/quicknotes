using System;

namespace QuickNotes.App.Models.Sync;

/// <summary>
/// Tracks the local device's upload checkpoint and pending/crash recovery state.
/// Stored in SQLite table SyncLocalStates. Contains no secrets.
/// </summary>
public class SyncLocalState
{
    public Guid DeviceId { get; set; }
    public Guid? LastUploadedPackageId { get; set; }
    public string? LatestUploadedPackageKey { get; set; }
    public string? LastUploadedETag { get; set; }
    public string? LastUploadedSnapshotHash { get; set; }
    public DateTime? LastUploadCompletedAtUtc { get; set; }

    // Crash / Retry recovery fields for indeterminate PUT operations
    public Guid? PendingPackageId { get; set; }
    public string? PendingPackageKey { get; set; }
    public string? PendingPackageDigest { get; set; }
    public string? PendingContentHash { get; set; }
    public DateTime? PendingCreatedAtUtc { get; set; }
    public byte[]? PendingPayloadBytes { get; set; }
}
