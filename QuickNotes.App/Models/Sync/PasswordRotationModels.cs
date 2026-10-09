using System;

namespace QuickNotes.App.Models.Sync;

public class PasswordRotationPreview
{
    public string Token { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
    public bool IsComplete { get; set; }
    public string? BlockReason { get; set; }
    public Guid ActiveGenerationId { get; set; }
    public int PackageCount { get; set; }
    public int PointerCount { get; set; }
    public int BlobCount { get; set; }
    public long BytesToCopy { get; set; }
    public string Summary { get; set; } = string.Empty;
}

public class PasswordRotationProgress
{
    public string Message { get; set; } = string.Empty;
    public int Current { get; set; }
    public int Total { get; set; }
}

public class PasswordRotationExecuteResult
{
    public bool TokenAccepted { get; set; }
    public bool Success { get; set; }
    public bool Canceled { get; set; }
    public bool CloudSwitched { get; set; }
    public bool LocalPasswordSaved { get; set; }
    public bool AlreadyOnNewGeneration { get; set; }
    public Guid? ActiveGenerationId { get; set; }
    public Guid? PreviousGenerationId { get; set; }
    public string Summary { get; set; } = string.Empty;
}
