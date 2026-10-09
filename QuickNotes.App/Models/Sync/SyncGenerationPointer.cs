using System;

namespace QuickNotes.App.Models.Sync;

/// <summary>
/// Small cloud pointer that selects the active encryption-generation namespace.
/// Missing object means legacy keys ({prefix}v1/devices|blobs) with ActiveGenerationId empty.
/// PendingGenerationId is a resume token; it does not change which generation the engine reads.
/// </summary>
public class SyncGenerationPointer
{
    public int FormatVersion { get; set; } = 1;
    public Guid ActiveGenerationId { get; set; }
    public Guid? PendingGenerationId { get; set; }
    public Guid? PreviousGenerationId { get; set; }
    public DateTime SwitchedAtUtc { get; set; }
}
