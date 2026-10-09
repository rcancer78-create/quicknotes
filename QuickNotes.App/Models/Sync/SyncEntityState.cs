using System;

namespace QuickNotes.App.Models.Sync;

/// <summary>
/// Tracks persistent sync state and revision history for synchronized entities.
/// </summary>
public class SyncEntityState
{
    public Guid SyncId { get; set; }
    public string EntityType { get; set; } = string.Empty;
    public Guid RevisionId { get; set; }
    public Guid? ParentRevisionId { get; set; }
    public Guid DeviceId { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public bool IsDeleted { get; set; }
    public DateTime? DeletedAtUtc { get; set; }
    public string? ContentHash { get; set; }

    [System.ComponentModel.DataAnnotations.Schema.NotMapped]
    public string? ContentFingerprint
    {
        get => ContentHash;
        set => ContentHash = value;
    }
}
