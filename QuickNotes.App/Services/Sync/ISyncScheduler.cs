using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using QuickNotes.App.Models.Sync;

namespace QuickNotes.App.Services.Sync;

public enum SyncTriggerReason
{
    Startup,
    LocalChange,
    Periodic,
    Manual,
    Retry
}

public enum SyncSchedulerQueueState
{
    Idle,
    Pending,
    Syncing,
    ErrorBackoff
}

public class SyncSchedulerStatusChangedEventArgs : EventArgs
{
    public SyncSchedulerQueueState QueueState { get; }
    public bool IsSyncRunning => QueueState == SyncSchedulerQueueState.Syncing;
    public string QueueStatusText { get; }
    public IReadOnlyCollection<SyncTriggerReason> PendingReasons { get; }
    public DateTime? LastSuccessTimeUtc { get; }
    public DateTime? LastAttemptTimeUtc { get; }
    public int ConflictsCount { get; }
    public SyncCycleResult? LastResult { get; }
    public LocalCommitSyncStatus PublicationStatus { get; }

    public SyncSchedulerStatusChangedEventArgs(
        SyncSchedulerQueueState queueState,
        string queueStatusText,
        IReadOnlyCollection<SyncTriggerReason> pendingReasons,
        DateTime? lastSuccessTimeUtc,
        DateTime? lastAttemptTimeUtc,
        int conflictsCount,
        SyncCycleResult? lastResult,
        LocalCommitSyncStatus publicationStatus = LocalCommitSyncStatus.SavedLocally)
    {
        QueueState = queueState;
        QueueStatusText = queueStatusText;
        PendingReasons = pendingReasons;
        LastSuccessTimeUtc = lastSuccessTimeUtc;
        LastAttemptTimeUtc = lastAttemptTimeUtc;
        ConflictsCount = conflictsCount;
        LastResult = lastResult;
        PublicationStatus = publicationStatus;
    }
}

public interface ISyncScheduler : IDisposable
{
    SyncSchedulerQueueState QueueState { get; }
    string QueueStatusDescription { get; }
    IReadOnlyCollection<SyncTriggerReason> PendingReasons { get; }
    int PendingReasonsCount { get; }
    bool IsSyncRunning { get; }
    LocalCommitSyncStatus PublicationStatus { get; }

    DateTime? LastSuccessTimeUtc { get; }
    DateTime? LastAttemptTimeUtc { get; }
    SyncCycleResult? LastResult { get; }
    int UnresolvedConflictsCount { get; }
    TimeSpan? NextRetryDelay { get; }

    event EventHandler<SyncSchedulerStatusChangedEventArgs>? StatusChanged;
    event EventHandler<SyncCycleResult>? SyncCompleted;

    void Start();
    void Stop();
    Task<bool> DrainAndStopAsync(TimeSpan timeout, CancellationToken ct = default);
    void EnqueueLocalChange();
    Task<SyncCycleResult?> TriggerManualSyncAsync(CancellationToken ct = default);
    void CancelCurrentCycle();
    void NotifySettingsChanged(SyncCloudSettings? newSettings = null);
    Task RefreshStateAsync(CancellationToken ct = default);
}
