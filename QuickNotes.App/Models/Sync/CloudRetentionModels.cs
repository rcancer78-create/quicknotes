using System;
using System.Collections.Generic;

namespace QuickNotes.App.Models.Sync;

public class CloudRetentionPreview
{
    public string Token { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
    public bool IsComplete { get; set; }
    public string? BlockReason { get; set; }
    public IReadOnlyList<CloudCleanupCandidate> Candidates { get; set; } = Array.Empty<CloudCleanupCandidate>();
    public long ReclaimableBytes { get; set; }
    public string Summary { get; set; } = string.Empty;
    public bool IsGenerationCleanup { get; set; }
}
