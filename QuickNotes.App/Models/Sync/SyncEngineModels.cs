using System;
using System.Collections.Generic;

namespace QuickNotes.App.Models.Sync;

public class SyncCycleOptions
{
    public bool PullOnly { get; set; } = false;
    public bool PushOnly { get; set; } = false;
    public int MaxRemoteDevices { get; set; } = 100;
    public int MaxListPages { get; set; } = 20;
    public long MaxPackageSizeBytes { get; set; } = 25 * 1024 * 1024; // 25 MB default limit
    public bool WaitIfBusy { get; set; } = false;

    /// <summary>
    /// Cumulative claimed PBKDF2 iterations allowed for untrusted inbound QNSP/QNBA
    /// in this cycle. 0 means the production default (20_000_000 claimed iterations).
    /// Does not change per-envelope max or production KDF defaults.
    /// Zero selects the production default (20_000_000 claimed iterations).
    /// </summary>
    public long UntrustedInboundKdfCycleBudgetIterations { get; set; }
}

public enum SyncProgressStage
{
    Starting,
    CheckingConnectivity,
    ListingRemotePointers,
    PullingRemotePackages,
    ApplyingRemoteChanges,
    CheckingLocalChanges,
    UploadingLocalPackage,
    UpdatingDevicePointer,
    Completed,
    Failed
}

public class SyncProgressReport
{
    public SyncProgressStage Stage { get; set; }
    public string Message { get; set; } = string.Empty;
    public int CurrentStep { get; set; }
    public int TotalSteps { get; set; }

    public SyncProgressReport() { }

    public SyncProgressReport(SyncProgressStage stage, string message, int currentStep = 0, int totalSteps = 0)
    {
        Stage = stage;
        Message = message;
        CurrentStep = currentStep;
        TotalSteps = totalSteps;
    }
}

public class SyncEngineStatus
{
    public bool IsConfigured { get; set; }
    public bool IsRunning { get; set; }
    public DateTime? LastSyncTimeUtc { get; set; }
    public bool? LastSyncSuccess { get; set; }
    public string? LastError { get; set; }
    public int PendingConflictsCount { get; set; }
    public Guid? LastUploadedPackageId { get; set; }
}

public class SyncCycleResult
{
    public bool Success { get; set; }
    public bool NoOp { get; set; }
    public int RemoteDevicesExamined { get; set; }
    public int RemotePackagesPulled { get; set; }
    public SyncEntityCounts RemoteEntitiesApplied { get; set; } = new();
    public int RemoteEntitiesSkipped { get; set; }
    public int ConflictsDetected { get; set; }
    public bool PackageUploaded { get; set; }
    public Guid? UploadedPackageId { get; set; }
    public bool IsOffline { get; set; }
    public bool IsAuthError { get; set; }
    public bool IsEtagConflict { get; set; }
    public bool IsBusy { get; set; }
    public bool IsCancelled { get; set; }
    public bool IsTimeout { get; set; }
    public bool IsKdfWorkBudgetExceeded { get; set; }
    public bool IsQuotaExceeded { get; set; }
    public bool IsTruncated { get; set; }
    public int BlobsUploaded { get; set; }
    public int BlobsDownloaded { get; set; }
    public int BlobsSkipped { get; set; }
    public int BlobErrors { get; set; }
    public List<string> Errors { get; set; } = new();
    public List<string> Diagnostics { get; set; } = new();
    public TimeSpan Duration { get; set; }
    public string? ErrorMessage => Errors.Count > 0 ? string.Join("; ", Errors) : null;

    public static SyncCycleResult Succeeded(
        bool noOp = false,
        int remoteDevicesExamined = 0,
        int remotePackagesPulled = 0,
        SyncEntityCounts? applied = null,
        int skipped = 0,
        int conflicts = 0,
        bool packageUploaded = false,
        Guid? uploadedPackageId = null)
    {
        return new SyncCycleResult
        {
            Success = true,
            NoOp = noOp,
            RemoteDevicesExamined = remoteDevicesExamined,
            RemotePackagesPulled = remotePackagesPulled,
            RemoteEntitiesApplied = applied ?? new SyncEntityCounts(),
            RemoteEntitiesSkipped = skipped,
            ConflictsDetected = conflicts,
            PackageUploaded = packageUploaded,
            UploadedPackageId = uploadedPackageId
        };
    }

    public static SyncCycleResult Busy()
    {
        var res = new SyncCycleResult
        {
            Success = false,
            IsBusy = true
        };
        res.Errors.Add("Синхронизация уже выполняется другим процессом или задачей.");
        return res;
    }

    public static SyncCycleResult Offline(string message)
    {
        var res = new SyncCycleResult
        {
            Success = false,
            IsOffline = true
        };
        res.Errors.Add(message);
        return res;
    }

    public static SyncCycleResult AuthError(string message)
    {
        var res = new SyncCycleResult
        {
            Success = false,
            IsAuthError = true
        };
        res.Errors.Add(message);
        return res;
    }

    public static SyncCycleResult EtagConflict(string message)
    {
        var res = new SyncCycleResult
        {
            Success = false,
            IsEtagConflict = true
        };
        res.Errors.Add(message);
        return res;
    }

    public static SyncCycleResult Failure(string message)
    {
        var res = new SyncCycleResult
        {
            Success = false
        };
        res.Errors.Add(message);
        return res;
    }

    public static SyncCycleResult Cancelled()
    {
        var res = new SyncCycleResult
        {
            Success = false,
            IsCancelled = true
        };
        res.Errors.Add(Helpers.UserFacingOperationError.Cancelled);
        return res;
    }

    public static SyncCycleResult Timeout()
    {
        var res = new SyncCycleResult
        {
            Success = false,
            IsTimeout = true
        };
        res.Errors.Add(Helpers.UserFacingOperationError.Timeout);
        return res;
    }

    public static SyncCycleResult QuotaExceeded(string message)
    {
        var res = new SyncCycleResult
        {
            Success = false,
            IsQuotaExceeded = true
        };
        res.Errors.Add(message);
        return res;
    }

    public static SyncCycleResult KdfWorkBudgetExceeded(string? message = null)
    {
        var res = new SyncCycleResult
        {
            Success = false,
            IsKdfWorkBudgetExceeded = true
        };
        res.Errors.Add(string.IsNullOrWhiteSpace(message)
            ? QuickNotes.App.Services.Sync.UntrustedInboundKdfWorkBudget.UserFacingMessage
            : message);
        return res;
    }
}
