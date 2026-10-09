using System;
using System.Collections.Generic;

namespace QuickNotes.App.Models.Sync;

public class CloudCleanupCandidate
{
    public string Key { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
    public string ETag { get; set; } = string.Empty;
    public long Size { get; set; }
    public string Reason { get; set; } = string.Empty;
}

public class CloudCleanupPreview
{
    public string Token { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
    public bool IsComplete { get; set; }
    public string? BlockReason { get; set; }
    public IReadOnlyList<CloudCleanupCandidate> Candidates { get; set; } = Array.Empty<CloudCleanupCandidate>();
    public long ReclaimableBytes { get; set; }
    public int BlobObjectsExamined { get; set; }
    public int ReachableBlobCount { get; set; }
}

public class CloudCleanupItemResult
{
    public string Key { get; set; } = string.Empty;
    public string Outcome { get; set; } = string.Empty;
    public string? Detail { get; set; }
}

public class CloudCleanupExecuteResult
{
    public bool TokenAccepted { get; set; }
    public bool Canceled { get; set; }
    public int Deleted { get; set; }
    public int Skipped { get; set; }
    public int Failed { get; set; }
    public long DeletedBytes { get; set; }
    public IReadOnlyList<CloudCleanupItemResult> Items { get; set; } = Array.Empty<CloudCleanupItemResult>();
    public string Summary { get; set; } = string.Empty;
}
