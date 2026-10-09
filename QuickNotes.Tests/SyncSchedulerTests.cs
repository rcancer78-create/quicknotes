using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using QuickNotes.App.Data;
using QuickNotes.App.Models.Sync;
using QuickNotes.App.Services;
using QuickNotes.App.Services.Sync;
using Xunit;

namespace QuickNotes.Tests;

public class FakeSyncTimer : ISyncTimer
{
    private readonly Action _callback;
    private readonly FakeSyncClock _clock;
    public TimeSpan DueTime { get; private set; } = Timeout.InfiniteTimeSpan;
    public TimeSpan Period { get; private set; } = Timeout.InfiniteTimeSpan;
    public bool IsActive { get; private set; }
    public DateTime? NextTriggerUtc { get; private set; }

    public FakeSyncTimer(Action callback, FakeSyncClock clock)
    {
        _callback = callback;
        _clock = clock;
    }

    public void Change(TimeSpan dueTime, TimeSpan period = default)
    {
        DueTime = dueTime;
        Period = period == default ? Timeout.InfiniteTimeSpan : period;
        IsActive = dueTime != Timeout.InfiniteTimeSpan;
        if (IsActive)
        {
            NextTriggerUtc = _clock.UtcNow + dueTime;
        }
        else
        {
            NextTriggerUtc = null;
        }
    }

    public void Stop()
    {
        IsActive = false;
        NextTriggerUtc = null;
    }

    public void Fire()
    {
        if (Period != Timeout.InfiniteTimeSpan)
        {
            NextTriggerUtc = _clock.UtcNow + Period;
        }
        else
        {
            IsActive = false;
            NextTriggerUtc = null;
        }
        _callback();
    }

    public void Dispose()
    {
        Stop();
    }
}

public class FakeSyncClock : ISyncClock
{
    public DateTime UtcNow { get; set; } = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
    public DateTime Now => UtcNow.ToLocalTime();
    public List<FakeSyncTimer> Timers { get; } = new();

    public ISyncTimer CreateTimer(Action callback)
    {
        var timer = new FakeSyncTimer(callback, this);
        Timers.Add(timer);
        return timer;
    }

    public void Advance(TimeSpan delta)
    {
        UtcNow += delta;
        var toFire = Timers
            .Where(t => t.IsActive && t.NextTriggerUtc.HasValue && t.NextTriggerUtc.Value <= UtcNow)
            .OrderBy(t => t.NextTriggerUtc)
            .ToList();

        foreach (var timer in toFire)
        {
            timer.Fire();
        }
    }

    public void TriggerAllActive()
    {
        var active = Timers.Where(t => t.IsActive).ToList();
        foreach (var t in active)
        {
            t.Fire();
        }
    }
}

public class FakeSyncEngineStub : ISyncEngine
{
    public int RunCallCount { get; private set; }
    public List<CancellationToken> ObservedTokens { get; } = new();
    public Func<CancellationToken, Task<SyncCycleResult>>? Handler { get; set; }

    public Task<SyncCycleResult> RunSyncCycleAsync(
        QuickNotesDbContext db,
        string encryptionPassword,
        SyncCycleOptions? options = null,
        IProgress<SyncProgressReport>? progress = null,
        CancellationToken ct = default)
    {
        RunCallCount++;
        ObservedTokens.Add(ct);
        if (Handler != null)
        {
            return Handler(ct);
        }
        return Task.FromResult(SyncCycleResult.Succeeded(noOp: true));
    }

    public Task<SyncCycleResult> RunSyncCycleAsync(
        string encryptionPassword,
        SyncCycleOptions? options = null,
        IProgress<SyncProgressReport>? progress = null,
        CancellationToken ct = default)
    {
        return RunSyncCycleAsync(null!, encryptionPassword, options, progress, ct);
    }

    public Task<SyncEngineStatus> GetStatusAsync(QuickNotesDbContext? db = null, CancellationToken ct = default)
    {
        return Task.FromResult(new SyncEngineStatus { IsConfigured = true });
    }
}

public class FakeSchedulerCredentials : IS3CredentialsStorage
{
    public bool Configured { get; set; } = true;
    public Task<S3Credentials?> LoadCredentialsAsync(CancellationToken ct = default) =>
        Task.FromResult(Configured ? new S3Credentials("AKIA_TEST", "SECRET_KEY") : null);
    public Task SaveCredentialsAsync(S3Credentials credentials, CancellationToken ct = default) { Configured = true; return Task.CompletedTask; }
    public Task DeleteCredentialsAsync(CancellationToken ct = default) { Configured = false; return Task.CompletedTask; }
    public bool HasCredentials() => Configured;
}

public class FakeSchedulerPassword : ISyncPasswordStorage
{
    public bool Configured { get; set; } = true;
    public string StoredPassword { get; set; } = "Pass123!";
    public Task<string?> LoadPasswordAsync(CancellationToken ct = default) =>
        Task.FromResult(Configured ? StoredPassword : null);
    public Task SavePasswordAsync(string password, CancellationToken ct = default) { Configured = true; StoredPassword = password; return Task.CompletedTask; }
    public Task DeletePasswordAsync(CancellationToken ct = default) { Configured = false; return Task.CompletedTask; }
    public bool HasPassword() => Configured && !string.IsNullOrEmpty(StoredPassword);
    public string? Pending { get; set; }
    public Task SavePendingPasswordAsync(string password, CancellationToken ct = default) { Pending = password; return Task.CompletedTask; }
    public Task<string?> LoadPendingPasswordAsync(CancellationToken ct = default) => Task.FromResult(Pending);
    public Task PromotePendingPasswordAsync(CancellationToken ct = default) { if (Pending != null) { Configured = true; StoredPassword = Pending; } Pending = null; return Task.CompletedTask; }
    public Task DeletePendingPasswordAsync(CancellationToken ct = default) { Pending = null; return Task.CompletedTask; }
    public bool HasPendingPassword() => !string.IsNullOrEmpty(Pending);
}

public class FakeSchedulerConflictService : ISyncConflictService
{
    public int ConflictsCount { get; set; } = 0;
    public Task<List<SyncConflictRecord>> GetUnresolvedConflictsAsync(CancellationToken ct = default) =>
        Task.FromResult(new List<SyncConflictRecord>());
    public Task<int> GetUnresolvedConflictsCountAsync(CancellationToken ct = default) =>
        Task.FromResult(ConflictsCount);
    public Task<SyncConflictDetail?> GetConflictDetailAsync(int conflictId, CancellationToken ct = default) =>
        Task.FromResult<SyncConflictDetail?>(null);
    public Task<SyncConflictResolutionResult> ResolveKeepBothAsync(int conflictId, CancellationToken ct = default) =>
        Task.FromResult(SyncConflictResolutionResult.Succeeded(conflictId, "KeepBoth"));
    public Task<SyncConflictResolutionResult> ResolveKeepLocalAsync(int conflictId, CancellationToken ct = default) =>
        Task.FromResult(SyncConflictResolutionResult.Succeeded(conflictId, "KeepLocal"));
    public Task<SyncConflictResolutionResult> ResolveAcceptRemoteAsync(int conflictId, CancellationToken ct = default) =>
        Task.FromResult(SyncConflictResolutionResult.Succeeded(conflictId, "AcceptRemote"));
    public Task<SyncConflictResolutionResult> ResolveMergeNoteAsync(int conflictId, string mergedText, CancellationToken ct = default) =>
        Task.FromResult(SyncConflictResolutionResult.Succeeded(conflictId, "Merge"));
}

[TestCategory(TestCategories.Integration)]
public class SyncSchedulerTests : IDisposable
{
    private readonly string _dbPath;
    private readonly FakeSyncClock _clock;
    private readonly FakeSyncEngineStub _engine;
    private readonly FakeSchedulerCredentials _creds;
    private readonly FakeSchedulerPassword _pass;
    private readonly FakeSchedulerConflictService _conflicts;
    private readonly SyncCloudSettings _settings;
    private readonly List<IDisposable> _disposables = new();

    public SyncSchedulerTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"sched_test_{Guid.NewGuid():N}.db");
        _clock = new FakeSyncClock();
        _engine = new FakeSyncEngineStub();
        _creds = new FakeSchedulerCredentials();
        _pass = new FakeSchedulerPassword();
        _conflicts = new FakeSchedulerConflictService();
        _settings = new SyncCloudSettings
        {
            Enabled = true,
            Bucket = "test-bucket",
            Prefix = "qn-prefix/",
            AutoSyncOnStartup = true,
            AutoSyncOnChanges = true,
            DebounceSeconds = 3,
            AutoSyncPeriodic = true,
            PeriodicIntervalMinutes = 30
        };

        // Initialize SQLite DB
        using var db = new QuickNotesDbContext(_dbPath);
        db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        foreach (var d in _disposables)
        {
            try { d.Dispose(); } catch { }
        }
        if (File.Exists(_dbPath))
        {
            try { File.Delete(_dbPath); } catch { }
        }
    }

    private SyncScheduler CreateScheduler()
    {
        var sched = new SyncScheduler(
            () => _engine,
            () => new QuickNotesDbContext(_dbPath),
            _settings,
            _creds,
            _pass,
            _conflicts,
            _clock);
        _disposables.Add(sched);
        return sched;
    }

    [Fact]
    public void Start_WhenAutoSyncOnStartupEnabled_TriggersStartupSyncCycle()
    {
        var sched = CreateScheduler();
        sched.Start();

        Assert.Equal(1, _engine.RunCallCount);
        Assert.Equal(SyncSchedulerQueueState.Idle, sched.QueueState);
    }

    [Fact]
    public void Start_WhenAutoSyncOnStartupDisabled_DoesNotTriggerStartupSync()
    {
        _settings.AutoSyncOnStartup = false;
        var sched = CreateScheduler();
        sched.Start();

        Assert.Equal(0, _engine.RunCallCount);
    }

    [Fact]
    public void EnqueueLocalChange_DebouncesAndCoalescesMultipleCalls()
    {
        var sched = CreateScheduler();
        _settings.AutoSyncOnStartup = false;
        sched.Start();

        // 5 consecutive mutations in quick succession
        sched.EnqueueLocalChange();
        sched.EnqueueLocalChange();
        sched.EnqueueLocalChange();
        sched.EnqueueLocalChange();
        sched.EnqueueLocalChange();

        Assert.Equal(SyncSchedulerQueueState.Pending, sched.QueueState);
        Assert.Equal(0, _engine.RunCallCount);

        // Advance 2 seconds (less than 3s debounce)
        _clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(0, _engine.RunCallCount);

        // Advance 1 more second (debounce fires at 3s total)
        _clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(1, _engine.RunCallCount);
        Assert.Equal(SyncSchedulerQueueState.Idle, sched.QueueState);
    }

    [Fact]
    public void EnqueueLocalChange_WhenCloudSyncDisabled_RemainsIdle()
    {
        _settings.Enabled = false;
        var sched = CreateScheduler();
        sched.Start();

        sched.EnqueueLocalChange();
        _clock.Advance(TimeSpan.FromSeconds(10));

        Assert.Equal(0, _engine.RunCallCount);
        Assert.Equal(SyncSchedulerQueueState.Idle, sched.QueueState);
    }

    [Fact]
    public async Task SingleFlight_ConcurrentTriggers_CoalesceIntoAtMostOneFollowUp()
    {
        var cycle1Tcs = new TaskCompletionSource<SyncCycleResult>();
        int cycleIndex = 0;
        _engine.Handler = ct =>
        {
            cycleIndex++;
            if (cycleIndex == 1)
            {
                return cycle1Tcs.Task;
            }
            return Task.FromResult(SyncCycleResult.Succeeded(noOp: true));
        };

        _settings.AutoSyncOnStartup = false;
        var sched = CreateScheduler();
        sched.Start();

        // Start cycle 1
        var manualTask1 = sched.TriggerManualSyncAsync();
        Assert.Equal(SyncSchedulerQueueState.Syncing, sched.QueueState);
        Assert.Equal(1, _engine.RunCallCount);

        // While cycle 1 is running, queue multiple triggers
        sched.EnqueueLocalChange();
        sched.EnqueueLocalChange();
        var manualTask2 = sched.TriggerManualSyncAsync();

        // Still single-flight running
        Assert.Equal(1, _engine.RunCallCount);
        Assert.Contains(SyncTriggerReason.Manual, sched.PendingReasons);

        // Complete cycle 1
        cycle1Tcs.SetResult(SyncCycleResult.Succeeded(noOp: false, packageUploaded: true));
        var res1 = await manualTask1;
        Assert.True(res1?.PackageUploaded);

        // Follow-up should execute automatically
        var res2 = await manualTask2;
        Assert.True(res2?.Success);

        // Exactly 2 cycles were run: cycle 1 and coalesced follow-up
        Assert.Equal(2, _engine.RunCallCount);
        Assert.Equal(SyncSchedulerQueueState.Idle, sched.QueueState);
    }

    [Fact]
    public async Task ManualSync_BypassesDebounceAndExecutesImmediatelyWhenIdle()
    {
        _settings.AutoSyncOnStartup = false;
        var sched = CreateScheduler();
        sched.Start();

        var result = await sched.TriggerManualSyncAsync();

        Assert.NotNull(result);
        Assert.True(result.Success);
        Assert.Equal(1, _engine.RunCallCount);
    }

    [Fact]
    public void PeriodicSync_EnforcesMinimum15MinutesInterval()
    {
        _settings.AutoSyncOnStartup = false;
        _settings.AutoSyncPeriodic = true;
        _settings.PeriodicIntervalMinutes = 30;

        var sched = CreateScheduler();
        sched.Start();

        // Advance 30 minutes to trigger periodic sync
        _clock.Advance(TimeSpan.FromMinutes(30));
        Assert.Equal(1, _engine.RunCallCount);

        // Attempting periodic sync again before 15 minutes have elapsed should be ignored
        var periodicTimer = _clock.Timers.FirstOrDefault(t => t.Period >= TimeSpan.FromMinutes(15));
        Assert.NotNull(periodicTimer);

        // Fire timer at 5 minutes
        _clock.Advance(TimeSpan.FromMinutes(5));
        periodicTimer.Fire();
        // Still only 1 execution because 5 min < 15 min rate limit
        Assert.Equal(1, _engine.RunCallCount);

        // Advance past 15 min (e.g. 15 min more = 20 min elapsed since last attempt)
        _clock.Advance(TimeSpan.FromMinutes(15));
        periodicTimer.Fire();
        Assert.Equal(2, _engine.RunCallCount);
    }

    [Fact]
    public void OfflineFailure_TriggersBoundedExponentialBackoff()
    {
        _settings.AutoSyncOnStartup = false;
        _engine.Handler = ct => Task.FromResult(SyncCycleResult.Offline("No network"));

        var sched = CreateScheduler();
        sched.Start();

        // Trigger local change
        sched.EnqueueLocalChange();
        _clock.Advance(TimeSpan.FromSeconds(3)); // debounce fires

        Assert.Equal(1, _engine.RunCallCount);
        Assert.Equal(SyncSchedulerQueueState.ErrorBackoff, sched.QueueState);
        Assert.Contains("15 с", sched.QueueStatusDescription);

        // Retry after 15s
        _clock.Advance(TimeSpan.FromSeconds(15));
        Assert.Equal(2, _engine.RunCallCount);
        Assert.Equal(SyncSchedulerQueueState.ErrorBackoff, sched.QueueState);
        // Second backoff doubles to 30s
        Assert.Contains("30 с", sched.QueueStatusDescription);

        // Succeed on third attempt
        _engine.Handler = ct => Task.FromResult(SyncCycleResult.Succeeded(noOp: true));
        _clock.Advance(TimeSpan.FromSeconds(30));

        Assert.Equal(3, _engine.RunCallCount);
        Assert.Equal(SyncSchedulerQueueState.Idle, sched.QueueState);
    }

    [Fact]
    public void AuthFailure_EntersErrorStateWithoutAutoRetryLoop()
    {
        _settings.AutoSyncOnStartup = false;
        _engine.Handler = ct => Task.FromResult(SyncCycleResult.AuthError("Invalid credentials 403"));

        var sched = CreateScheduler();
        sched.Start();

        sched.EnqueueLocalChange();
        _clock.Advance(TimeSpan.FromSeconds(3));

        Assert.Equal(1, _engine.RunCallCount);
        // Should NOT schedule auto-retry for auth failure
        _clock.Advance(TimeSpan.FromMinutes(10));
        Assert.Equal(1, _engine.RunCallCount);
    }

    [Fact]
    public void KdfWorkBudgetExceeded_DoesNotRetryAsNetworkBackoff()
    {
        _settings.AutoSyncOnStartup = false;
        _engine.Handler = ct => Task.FromResult(SyncCycleResult.KdfWorkBudgetExceeded());

        var sched = CreateScheduler();
        sched.Start();

        sched.EnqueueLocalChange();
        _clock.Advance(TimeSpan.FromSeconds(3));

        Assert.Equal(1, _engine.RunCallCount);
        _clock.Advance(TimeSpan.FromMinutes(10));
        Assert.Equal(1, _engine.RunCallCount);
        Assert.NotEqual(SyncSchedulerQueueState.ErrorBackoff, sched.QueueState);
    }

    [Fact]
    public void QuotaExceeded_DoesNotRetryAsNetworkBackoff()
    {
        _settings.AutoSyncOnStartup = false;
        _engine.Handler = ct => Task.FromResult(SyncCycleResult.QuotaExceeded("Simulated object store is full."));

        var sched = CreateScheduler();
        sched.Start();

        sched.EnqueueLocalChange();
        _clock.Advance(TimeSpan.FromSeconds(3));

        Assert.Equal(1, _engine.RunCallCount);
        _clock.Advance(TimeSpan.FromMinutes(10));
        Assert.Equal(1, _engine.RunCallCount);
        Assert.NotEqual(SyncSchedulerQueueState.ErrorBackoff, sched.QueueState);
        Assert.False(sched.LastResult?.IsOffline);
        Assert.True(sched.LastResult?.IsQuotaExceeded);
    }

    [Fact]
    public async Task CancelCurrentCycle_CancelsActiveToken()
    {
        var cycleStartedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelObservedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        _engine.Handler = async ct =>
        {
            cycleStartedTcs.TrySetResult(true);
            try
            {
                await Task.Delay(5000, ct);
                return SyncCycleResult.Succeeded(noOp: true);
            }
            catch (OperationCanceledException)
            {
                cancelObservedTcs.TrySetResult(true);
                return SyncCycleResult.Failure("Cancelled");
            }
        };

        _settings.AutoSyncOnStartup = false;
        var sched = CreateScheduler();
        sched.Start();

        var runTask = sched.TriggerManualSyncAsync();
        await cycleStartedTcs.Task;

        sched.CancelCurrentCycle();
        await cancelObservedTcs.Task;

        Assert.True(cancelObservedTcs.Task.IsCompletedSuccessfully);
    }

    [Fact]
    public void NotifySettingsChanged_WhenDisabled_CancelsTimersAndResetsState()
    {
        var sched = CreateScheduler();
        sched.Start();

        _settings.Enabled = false;
        sched.NotifySettingsChanged();

        Assert.Equal(SyncSchedulerQueueState.Idle, sched.QueueState);
        Assert.Equal(0, sched.PendingReasonsCount);
    }
}
