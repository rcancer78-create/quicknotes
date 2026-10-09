using System;

namespace QuickNotes.App.Models.Sync;

/// <summary>
/// Tracks the latest processed package and pointer ETag for a remote device.
/// Stored in SQLite table SyncDeviceStates. Contains no secrets.
/// </summary>
public class SyncDeviceState
{
    public Guid DeviceId { get; set; }
    public Guid? LatestProcessedPackageId { get; set; }
    public string? LatestProcessedETag { get; set; }
    public DateTime LastSyncedAtUtc { get; set; } = DateTime.UtcNow;
    public int PackageCount { get; set; }
}
