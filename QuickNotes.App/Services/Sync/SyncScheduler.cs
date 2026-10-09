using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using QuickNotes.App.Data;
using QuickNotes.App.Models.Sync;

namespace QuickNotes.App.Services.Sync;

/// <summary>
/// Long-lived coordinator and scheduler for cloud synchronization.
/// Manages single-flight sync cycles, coalesces trigger reasons, enforces safe debouncing,
/// bounded exponential retry backoff, and limits periodic syncs to >= 15 minutes.
/// </summary>
public class SyncScheduler : ISyncScheduler
{
    private readonly Func<ISyncEngine> _syncEngineFactory;
    private readonly Func<QuickNotesDbContext> _dbFactory;
    private SyncCloudSettings _settings;
    private readonly Func<SyncCloudSettings>? _settingsProvider;
    private readonly IS3CredentialsStorage _credentialsStorage;
    private readonly ISyncPasswordStorage _passwordStorage;
    private readonly ISyncConflictService _conflictService;
    private readonly ISyncClock _clock;
    private readonly IDeviceIdProvider? _deviceIdProvider;

    private readonly object _stateLock = new();
    private readonly SemaphoreSlim _cycleGate = new(1, 1);

    private readonly HashSet<SyncTriggerReason> _pendingReasons = new();
    private readonly HashSet<SyncTriggerReason> _followUpReasons = new();
    private readonly List<TaskCompletionSource<SyncCycleResult?>> _followUpManualTcs = new();

    private SyncSchedulerQueueState _queueState = SyncSchedulerQueueState.Idle;
    private bool _isSyncRunning;
    private bool _isStarted;
    private bool _disposed;

    private DateTime? _lastSuccessTimeUtc;
    private DateTime? _lastAttemptTimeUtc;
    private SyncCycleResult? _lastResult;
    private int _unresolvedConflictsCount;
    private int _consecutiveFailures;
    private TimeSpan? _nextRetryDelay;

    private readonly ISyncTimer _debounceTimer;
    private readonly ISyncTimer _periodicTimer;
    private readonly ISyncTimer _retryTimer;
    private CancellationTokenSource? _activeCycleCts;
    private TaskCompletionSource<bool>? _activeCycleWaitTcs;
    private Task? _ownedCycleTask;

    public LocalCommitSyncStatus PublicationStatus
    {
        get
        {
            lock (_stateLock)
            {
                if (!_settings.Enabled || !ArePrerequisitesMet())
                {
                    return LocalCommitSyncStatus.SavedLocally;
                }

                if (_unresolvedConflictsCount > 0)
                {
                    return LocalCommitSyncStatus.Conflict;
                }

                if (_isSyncRunning)
                {
                    return LocalCommitSyncStatus.Syncing;
                }

                if (_queueState == SyncSchedulerQueueState.ErrorBackoff || (_lastResult != null && !_lastResult.Success))
                {
                    return LocalCommitSyncStatus.Error;
                }

                if (_queueState == SyncSchedulerQueueState.Pending || _pendingReasons.Count > 0 || _followUpReasons.Count > 0)
                {
                    return LocalCommitSyncStatus.PendingUpload;
                }

                return LocalCommitSyncStatus.Synchronized;
            }
        }
    }

    public SyncSchedulerQueueState QueueState
    {
        get { lock (_stateLock) return _queueState; }
        private set { lock (_stateLock) _queueState = value; }
    }

    public string QueueStatusDescription
    {
        get
        {
            lock (_stateLock)
            {
                return _queueState switch
                {
                    SyncSchedulerQueueState.Idle => "Нет изменений",
                    SyncSchedulerQueueState.Pending => _pendingReasons.Count switch
                    {
                        0 => "Ожидание запуска…",
                        1 => $"Ожидает запуск ({FormatReason(_pendingReasons.First())})",
                        _ => $"Ожидает запуск ({_pendingReasons.Count} причин)"
                    },
                    SyncSchedulerQueueState.Syncing => "Идёт обмен…",
                    SyncSchedulerQueueState.ErrorBackoff => _nextRetryDelay.HasValue
                        ? $"Повтор после ошибки (через {(int)_nextRetryDelay.Value.TotalSeconds} с)"
                        : "Повтор после ошибки",
                    _ => "Нет изменений"
                };
            }
        }
    }

    public IReadOnlyCollection<SyncTriggerReason> PendingReasons
    {
        get
        {
            lock (_stateLock)
            {
                return _pendingReasons.Concat(_followUpReasons).Distinct().ToList();
            }
        }
    }

    public int PendingReasonsCount
    {
        get
        {
            lock (_stateLock)
            {
                return _pendingReasons.Concat(_followUpReasons).Distinct().Count();
            }
        }
    }

    public bool IsSyncRunning
    {
        get
        {
            lock (_stateLock)
            {
                return _isSyncRunning;
            }
        }
    }

    public DateTime? LastSuccessTimeUtc => _lastSuccessTimeUtc;
    public DateTime? LastAttemptTimeUtc => _lastAttemptTimeUtc;
    public SyncCycleResult? LastResult => _lastResult;
    public int UnresolvedConflictsCount => _unresolvedConflictsCount;
    public TimeSpan? NextRetryDelay => _nextRetryDelay;

    public event EventHandler<SyncSchedulerStatusChangedEventArgs>? StatusChanged;
    public event EventHandler<SyncCycleResult>? SyncCompleted;

    public SyncScheduler(
        Func<ISyncEngine> syncEngineFactory,
        Func<QuickNotesDbContext> dbFactory,
        SyncCloudSettings settings,
        IS3CredentialsStorage credentialsStorage,
        ISyncPasswordStorage passwordStorage,
        ISyncConflictService conflictService,
        ISyncClock? clock = null,
        Func<SyncCloudSettings>? settingsProvider = null,
        IDeviceIdProvider? deviceIdProvider = null)
    {
        _syncEngineFactory = syncEngineFactory ?? throw new ArgumentNullException(nameof(syncEngineFactory));
        _dbFactory = dbFactory ?? throw new ArgumentNullException(nameof(dbFactory));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _settingsProvider = settingsProvider;
        _credentialsStorage = credentialsStorage ?? throw new ArgumentNullException(nameof(credentialsStorage));
        _passwordStorage = passwordStorage ?? throw new ArgumentNullException(nameof(passwordStorage));
        _conflictService = conflictService ?? throw new ArgumentNullException(nameof(conflictService));
        _clock = clock ?? new SystemSyncClock();
        _deviceIdProvider = deviceIdProvider;

        _debounceTimer = _clock.CreateTimer(OnDebounceTimerFired);
        _periodicTimer = _clock.CreateTimer(OnPeriodicTimerFired);
        _retryTimer = _clock.CreateTimer(OnRetryTimerFired);
    }

    public void Start()
    {
        bool triggerStartup = false;
        lock (_stateLock)
        {
            if (_disposed || _isStarted) return;
            _isStarted = true;

            if (!_settings.Enabled)
            {
                return;
            }

            // 1. Check for durable committed changes from local SQLite (retained across crash/restart)
            bool hasDurablePending = false;
            if (_deviceIdProvider != null)
            {
                try
                {
                    using var db = _dbFactory();
                    Guid devId = _deviceIdProvider.GetDeviceId();
                    if (devId != Guid.Empty)
                    {
                        hasDurablePending = SyncSnapshotHelper.HasDurablePendingWork(db, devId);
                    }
                }
                catch (Exception ex)
                {
                    ErrorLogService.Write("SyncScheduler.StartCheckDurable", ex);
                }
            }

            if (hasDurablePending)
            {
                _pendingReasons.Add(SyncTriggerReason.LocalChange);
                _queueState = SyncSchedulerQueueState.Pending;
                if ((_settings.AutoSyncOnChanges || _settings.AutoSyncOnStartup) && ArePrerequisitesMet())
                {
                    triggerStartup = true;
                }
            }
            else if (_settings.AutoSyncOnStartup && ArePrerequisitesMet())
            {
                _pendingReasons.Add(SyncTriggerReason.Startup);
                _queueState = SyncSchedulerQueueState.Pending;
                triggerStartup = true;
            }

            // 2. Periodic sync timer (enforcing >= 15 min interval)
            SchedulePeriodicTimer();
        }

        if (triggerStartup)
        {
            BeginOwnedCycle();
        }
    }

    public void Stop()
    {
        lock (_stateLock)
        {
            _isStarted = false;
            _debounceTimer.Stop();
            _periodicTimer.Stop();
            _retryTimer.Stop();
            _pendingReasons.Clear();
            _followUpReasons.Clear();
            _nextRetryDelay = null;
            _consecutiveFailures = 0;
            _queueState = SyncSchedulerQueueState.Idle;

            try
            {
                _activeCycleCts?.Cancel();
            }
            catch (ObjectDisposedException) { }

            CancelPendingManualFollowUps();
        }
        RaiseStatusChanged();
    }

    public void EnqueueLocalChange()
    {
        lock (_stateLock)
        {
            if (_disposed || !_settings.Enabled || !_settings.AutoSyncOnChanges)
            {
                return;
            }

            if (!ArePrerequisitesMet())
            {
                return;
            }

            if (_isSyncRunning)
            {
                // Active cycle running: coalesce into follow-up reasons (at most 1 follow-up cycle)
                _followUpReasons.Add(SyncTriggerReason.LocalChange);
                RaiseStatusChanged();
                return;
            }

            _pendingReasons.Add(SyncTriggerReason.LocalChange);
            _queueState = SyncSchedulerQueueState.Pending;
            _retryTimer.Stop();
            _nextRetryDelay = null;

            int debounceSec = Math.Clamp(_settings.DebounceSeconds, 1, 300);
            _debounceTimer.Change(TimeSpan.FromSeconds(debounceSec));
        }

        RaiseStatusChanged();
    }

    public async Task<SyncCycleResult?> TriggerManualSyncAsync(CancellationToken ct = default)
    {
        TaskCompletionSource<SyncCycleResult?>? tcs = null;

        lock (_stateLock)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(SyncScheduler));

            if (_isSyncRunning)
            {
                // Active cycle running: coalesce into follow-up reasons (at most 1 follow-up cycle)
                _followUpReasons.Add(SyncTriggerReason.Manual);
                tcs = new TaskCompletionSource<SyncCycleResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
                _followUpManualTcs.Add(tcs);
                RaiseStatusChanged();
            }
            else
            {
                _debounceTimer.Stop();
                _retryTimer.Stop();
                _nextRetryDelay = null;
                _pendingReasons.Add(SyncTriggerReason.Manual);
            }
        }

        if (tcs != null)
        {
            using var reg = ct.CanBeCanceled ? ct.Register(() => tcs.TrySetCanceled(ct)) : default;
            try
            {
                return await tcs.Task.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return null;
            }
        }

        return await ExecuteCycleAsync(ct).ConfigureAwait(false);
    }

    public void CancelCurrentCycle()
    {
        lock (_stateLock)
        {
            try
            {
                _activeCycleCts?.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    public void NotifySettingsChanged(SyncCloudSettings? newSettings = null)
    {
        lock (_stateLock)
        {
            if (_disposed) return;

            if (newSettings != null)
            {
                _settings = newSettings;
            }
            else if (_settingsProvider != null)
            {
                _settings = _settingsProvider() ?? _settings;
            }

            if (!_settings.Enabled)
            {
                _debounceTimer.Stop();
                _retryTimer.Stop();
                _periodicTimer.Stop();
                _pendingReasons.Clear();
                _followUpReasons.Clear();
                _queueState = SyncSchedulerQueueState.Idle;
                _nextRetryDelay = null;
                _consecutiveFailures = 0;

                try
                {
                    _activeCycleCts?.Cancel();
                }
                catch (ObjectDisposedException) { }

                CancelPendingManualFollowUps();
            }
            else
            {
                SchedulePeriodicTimer();
            }
        }
        RaiseStatusChanged();
    }

    private void CancelPendingManualFollowUps()
    {
        List<TaskCompletionSource<SyncCycleResult?>> toCancel;
        lock (_stateLock)
        {
            toCancel = _followUpManualTcs.ToList();
            _followUpManualTcs.Clear();
        }
        foreach (var tcs in toCancel)
        {
            tcs.TrySetResult(null);
        }
    }

    public async Task RefreshStateAsync(CancellationToken ct = default)
    {
        try
        {
            _unresolvedConflictsCount = await _conflictService.GetUnresolvedConflictsCountAsync(ct).ConfigureAwait(false);
            if (_deviceIdProvider != null && _settings.Enabled)
            {
                using var db = _dbFactory();
                Guid devId = _deviceIdProvider.GetDeviceId();
                if (devId != Guid.Empty && SyncSnapshotHelper.HasDurablePendingWork(db, devId))
                {
                    lock (_stateLock)
                    {
                        if (_queueState == SyncSchedulerQueueState.Idle)
                        {
                            _pendingReasons.Add(SyncTriggerReason.LocalChange);
                            _queueState = SyncSchedulerQueueState.Pending;
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            ErrorLogService.Write("SyncScheduler.RefreshState", ex);
        }
        RaiseStatusChanged();
    }

    public async Task<bool> DrainAndStopAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        Task? waitTask = null;
        lock (_stateLock)
        {
            _isStarted = false;
            _debounceTimer.Stop();
            _periodicTimer.Stop();
            _retryTimer.Stop();

            if (_isSyncRunning)
            {
                waitTask = _activeCycleWaitTcs?.Task;
            }
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(timeout);
        CancellationToken bound = linked.Token;

        bool completed = true;
        if (waitTask != null && !waitTask.IsCompleted)
        {
            try
            {
                await waitTask.WaitAsync(bound).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                completed = false;
            }
        }

        Task? owned = _ownedCycleTask;
        if (completed && owned != null && !owned.IsCompleted)
        {
            try
            {
                await owned.WaitAsync(bound).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                completed = false;
            }
        }

        Stop();
        return completed;
    }

    private void OnDebounceTimerFired()
    {
        lock (_stateLock)
        {
            if (_disposed || !_settings.Enabled || _isSyncRunning)
            {
                return;
            }
        }

        BeginOwnedCycle();
    }

    private void OnPeriodicTimerFired()
    {
        lock (_stateLock)
        {
            if (_disposed || !_settings.Enabled || !_settings.AutoSyncPeriodic)
            {
                return;
            }

            // Rate limit: periodic run cannot occur more than once per 15 minutes
            if (_lastAttemptTimeUtc.HasValue && (_clock.UtcNow - _lastAttemptTimeUtc.Value) < TimeSpan.FromMinutes(15))
            {
                return;
            }

            if (!ArePrerequisitesMet())
            {
                return;
            }

            if (_isSyncRunning)
            {
                _followUpReasons.Add(SyncTriggerReason.Periodic);
                RaiseStatusChanged();
                return;
            }

            _pendingReasons.Add(SyncTriggerReason.Periodic);
            _queueState = SyncSchedulerQueueState.Pending;
        }

        BeginOwnedCycle();
    }

    private void OnRetryTimerFired()
    {
        lock (_stateLock)
        {
            if (_disposed || !_settings.Enabled || _isSyncRunning)
            {
                return;
            }

            if (!ArePrerequisitesMet())
            {
                return;
            }

            _pendingReasons.Add(SyncTriggerReason.Retry);
            _queueState = SyncSchedulerQueueState.Pending;
        }

        BeginOwnedCycle();
    }

    private void BeginOwnedCycle()
    {
        _ownedCycleTask = ObserveCycleAsync();
    }

    private async Task ObserveCycleAsync()
    {
        try
        {
            await ExecuteCycleAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ErrorLogService.Write("SyncScheduler.ObserveCycle", ex);
        }
    }

    private async Task<SyncCycleResult?> ExecuteCycleAsync(CancellationToken ct = default)
    {
        CancellationTokenSource? cycleCts = null;
        List<TaskCompletionSource<SyncCycleResult?>> currentManualWaiters;

        lock (_stateLock)
        {
            if (_disposed || _isSyncRunning)
            {
                return null;
            }

            _isSyncRunning = true;
            _activeCycleWaitTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _queueState = SyncSchedulerQueueState.Syncing;
            _lastAttemptTimeUtc = _clock.UtcNow;

            currentManualWaiters = _followUpManualTcs.ToList();
            _followUpManualTcs.Clear();

            cycleCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _activeCycleCts = cycleCts;
        }

        RaiseStatusChanged();

        SyncCycleResult? cycleResult = null;
        try
        {
            var token = cycleCts.Token;

            string? password = await _passwordStorage.LoadPasswordAsync(token).ConfigureAwait(false);
            if (string.IsNullOrEmpty(password))
            {
                var noPwdResult = SyncCycleResult.Failure("Пароль сквозного шифрования не настроен.");
                cycleResult = noPwdResult;
                _lastResult = noPwdResult;
                return noPwdResult;
            }

            var engine = _syncEngineFactory();
            using var db = _dbFactory();

            cycleResult = await engine.RunSyncCycleAsync(db, password, ct: token).ConfigureAwait(false);
            _lastResult = cycleResult;

            try
            {
                _unresolvedConflictsCount = await _conflictService.GetUnresolvedConflictsCountAsync(token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                ErrorLogService.Write("SyncScheduler.UpdateConflictsCount", ex);
            }

            if (cycleResult.Success)
            {
                _consecutiveFailures = 0;
                _nextRetryDelay = null;
                _lastSuccessTimeUtc = _clock.UtcNow;
            }
            else if (cycleResult.IsOffline)
            {
                _consecutiveFailures++;
                // Bounded exponential backoff: 15s, 30s, 60s, 120s, 240s, 480s, capped at 900s (15 min)
                int delaySec = (int)Math.Min(15 * Math.Pow(2, Math.Min(_consecutiveFailures - 1, 6)), 900);
                _nextRetryDelay = TimeSpan.FromSeconds(delaySec);
            }
            else if (cycleResult.IsAuthError || cycleResult.IsKdfWorkBudgetExceeded || cycleResult.IsQuotaExceeded)
            {
                // Auth, inbound KDF budget exhaustion, and store quota/full require a user-visible stop; not a network retry.
                _consecutiveFailures = 0;
                _nextRetryDelay = null;
            }
            else
            {
                _consecutiveFailures++;
                int delaySec = (int)Math.Min(15 * Math.Pow(2, Math.Min(_consecutiveFailures - 1, 6)), 900);
                _nextRetryDelay = TimeSpan.FromSeconds(delaySec);
            }

            return cycleResult;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (ObjectDisposedException) when (_disposed || (cycleCts != null && cycleCts.IsCancellationRequested))
        {
            return null;
        }
        catch (CloudOfflineException ex)
        {
            _consecutiveFailures++;
            int delaySec = (int)Math.Min(15 * Math.Pow(2, Math.Min(_consecutiveFailures - 1, 6)), 900);
            _nextRetryDelay = TimeSpan.FromSeconds(delaySec);
            var offlineResult = SyncCycleResult.Offline(ex.Message);
            _lastResult = offlineResult;
            cycleResult = offlineResult;
            return offlineResult;
        }
        catch (CloudAuthException ex)
        {
            _consecutiveFailures = 0;
            _nextRetryDelay = null;
            var authResult = SyncCycleResult.AuthError(ex.Message);
            _lastResult = authResult;
            cycleResult = authResult;
            return authResult;
        }
        catch (Exception ex)
        {
            ErrorLogService.Write("SyncScheduler.ExecuteCycle", ex);
            _consecutiveFailures++;
            int delaySec = (int)Math.Min(15 * Math.Pow(2, Math.Min(_consecutiveFailures - 1, 6)), 900);
            _nextRetryDelay = TimeSpan.FromSeconds(delaySec);
            var errResult = SyncCycleResult.Failure(ex.Message);
            _lastResult = errResult;
            cycleResult = errResult;
            return errResult;
        }
        finally
        {
            _activeCycleWaitTcs?.TrySetResult(true);
            bool hasFollowUp = false;

            lock (_stateLock)
            {
                _isSyncRunning = false;
                _activeCycleCts?.Dispose();
                _activeCycleCts = null;

                _pendingReasons.Clear();

                foreach (var waiter in currentManualWaiters)
                {
                    waiter.TrySetResult(cycleResult);
                }

                // Check for follow-up triggers arrived during the active cycle
                if (_followUpReasons.Count > 0)
                {
                    foreach (var r in _followUpReasons)
                    {
                        _pendingReasons.Add(r);
                    }
                    _followUpReasons.Clear();
                    hasFollowUp = true;
                    _queueState = SyncSchedulerQueueState.Pending;
                }
                else if (_nextRetryDelay.HasValue && _settings.Enabled)
                {
                    _queueState = SyncSchedulerQueueState.ErrorBackoff;
                    _retryTimer.Change(_nextRetryDelay.Value);
                }
                else
                {
                    _queueState = SyncSchedulerQueueState.Idle;
                }
            }

            if (cycleResult != null)
            {
                SyncCompleted?.Invoke(this, cycleResult);
            }

            RaiseStatusChanged();

            // Run exactly one coalesced follow-up cycle if changes queued during cycle
            if (hasFollowUp && !_disposed && _settings.Enabled)
            {
                BeginOwnedCycle();
            }
            else if (!hasFollowUp)
            {
                CancelPendingManualFollowUps();
            }
        }
    }

    private void SchedulePeriodicTimer()
    {
        if (_settings.Enabled && _settings.AutoSyncPeriodic)
        {
            int intervalMinutes = Math.Max(SyncCloudSettings.MinPeriodicIntervalMinutes, _settings.PeriodicIntervalMinutes);
            var interval = TimeSpan.FromMinutes(intervalMinutes);
            _periodicTimer.Change(interval, interval);
        }
        else
        {
            _periodicTimer.Stop();
        }
    }

    private bool ArePrerequisitesMet()
    {
        return _settings.Enabled &&
               !string.IsNullOrWhiteSpace(_settings.Bucket) &&
               _credentialsStorage.HasCredentials() &&
               _passwordStorage.HasPassword();
    }

    private void RaiseStatusChanged()
    {
        SyncSchedulerStatusChangedEventArgs args;
        lock (_stateLock)
        {
            args = new SyncSchedulerStatusChangedEventArgs(
                _queueState,
                QueueStatusDescription,
                _pendingReasons.Concat(_followUpReasons).Distinct().ToList(),
                _lastSuccessTimeUtc,
                _lastAttemptTimeUtc,
                _unresolvedConflictsCount,
                _lastResult,
                PublicationStatus);
        }

        StatusChanged?.Invoke(this, args);
    }

    private static string FormatReason(SyncTriggerReason reason)
    {
        return reason switch
        {
            SyncTriggerReason.Startup => "при запуске",
            SyncTriggerReason.LocalChange => "локальные изменения",
            SyncTriggerReason.Periodic => "периодический обмен",
            SyncTriggerReason.Manual => "ручной запуск",
            SyncTriggerReason.Retry => "повтор после ошибки",
            _ => "изменения"
        };
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Stop();

        _debounceTimer.Dispose();
        _periodicTimer.Dispose();
        _retryTimer.Dispose();
        _cycleGate.Dispose();
    }
}
