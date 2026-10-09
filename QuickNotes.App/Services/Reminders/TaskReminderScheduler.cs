using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using QuickNotes.App.Helpers;
using QuickNotes.App.Models;
using QuickNotes.App.Services.Sync;

namespace QuickNotes.App.Services.Reminders;

/// <summary>
/// In-process reminder scheduler. Lives with the app: Start / Refresh / Stop.
/// Does not claim delivery after QuickNotes exits.
/// </summary>
public sealed class TaskReminderScheduler : ITaskReminderScheduler
{
    public static readonly TimeSpan MaxDelay = TimeSpan.FromMinutes(15);
    public const string ToastTitle = "Напоминание QuickNotes";
    public const int MaxBodyChars = 80;

    private readonly ITaskIndexService _taskIndex;
    private readonly ReminderLedgerStore _ledger;
    private readonly ILocalToastAdapter _toast;
    private readonly ISyncClock _clock;
    private readonly Func<bool> _isEnabled;
    private readonly TimeZoneInfo _timeZone;
    private readonly ISyncTimer _timer;
    private readonly object _gate = new();

    private CancellationTokenSource? _scanCts;
    private Task? _ownedRefresh;
    private int _scanGeneration;
    private bool _started;
    private bool _disposed;
    private ReminderSchedulerStatus _status = new()
    {
        CompactText = "Локальные напоминания: выключены"
    };

    public TaskReminderScheduler(
        ITaskIndexService taskIndex,
        ReminderLedgerStore ledger,
        ILocalToastAdapter toast,
        ISyncClock clock,
        Func<bool> isEnabled,
        TimeZoneInfo? timeZone = null)
    {
        _taskIndex = taskIndex;
        _ledger = ledger;
        _toast = toast;
        _clock = clock;
        _isEnabled = isEnabled;
        _timeZone = timeZone ?? TimeZoneInfo.Local;
        _timer = clock.CreateTimer(OnTimer);
        _toast.Activated += OnToastActivated;
        PublishStatus(BuildStatus(enabled: isEnabled(), running: false, next: null, error: toast.IsAvailable ? null : toast.UnavailableReason));
    }

    public ReminderSchedulerStatus Status
    {
        get
        {
            lock (_gate)
            {
                return _status;
            }
        }
    }

    public event Action? StatusChanged;
    public event Action<Guid>? NoteActivationRequested;

    public void Start()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _started = true;
        }

        _ownedRefresh = ObserveRefreshAsync();
    }

    public void Refresh()
    {
        _ownedRefresh = ObserveRefreshAsync();
    }

    private async Task ObserveRefreshAsync()
    {
        try
        {
            await RefreshAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ErrorLogService.Write("TaskReminderScheduler.ObserveRefresh", ex);
        }
    }

    public void NotifySettingsChanged()
    {
        Refresh();
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        bool started;
        lock (_gate)
        {
            started = _started && !_disposed;
        }

        if (!started)
        {
            return;
        }

        int generation = Interlocked.Increment(ref _scanGeneration);
        CancellationTokenSource cts;
        lock (_gate)
        {
            try
            {
                _scanCts?.Cancel();
                _scanCts?.Dispose();
            }
            catch (ObjectDisposedException)
            {
            }

            _scanCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts = _scanCts;
        }

        var token = cts.Token;
        try
        {
            await Task.Run(() => Scan(generation, token), token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ErrorLogService.Write("TaskReminderScheduler.Refresh", ex.GetType().Name);
            ScheduleNext(MaxDelay);
            PublishStatus(BuildStatus(_isEnabled(), true, _clock.Now + MaxDelay, "Не удалось проверить задачи."));
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            _started = false;
            try
            {
                _scanCts?.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            _timer.Stop();
        }

        PublishStatus(BuildStatus(_isEnabled(), false, null, Status.LastError));
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _started = false;
        }

        _toast.Activated -= OnToastActivated;
        try
        {
            _scanCts?.Cancel();
            _scanCts?.Dispose();
        }
        catch
        {
        }

        _timer.Dispose();
        LifecycleShutdown.Wait(_ownedRefresh, BoundedOperation.ShutdownDrainTimeout);
    }

    private void OnTimer()
    {
        Refresh();
    }

    private void OnToastActivated(LocalToastActivation activation)
    {
        if (activation.NoteSyncId is Guid id && id != Guid.Empty)
        {
            NoteActivationRequested?.Invoke(id);
        }
    }

    private void Scan(int generation, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (generation != Volatile.Read(ref _scanGeneration))
        {
            return;
        }

        bool enabled = _isEnabled();
        if (!enabled)
        {
            _timer.Stop();
            PublishStatus(BuildStatus(false, true, null, null));
            return;
        }

        if (!_toast.IsAvailable)
        {
            ScheduleNext(MaxDelay);
            PublishStatus(BuildStatus(true, true, _clock.Now + MaxDelay, _toast.UnavailableReason));
            return;
        }

        var page = _taskIndex.QueryDatedOpenTasks(token);
        token.ThrowIfCancellationRequested();
        DateTime nowLocal = _clock.Now;
        var liveIds = new List<string>(page.Count);
        DateTime? nextDue = null;
        string? error = null;

        foreach (var locator in page)
        {
            token.ThrowIfCancellationRequested();
            if (locator.DueDate is not DateOnly due)
            {
                continue;
            }

            string id = ReminderIdentity.Compute(locator);
            liveIds.Add(id);
            if (_ledger.IsDelivered(id))
            {
                continue;
            }

            if (ReminderDueSemantics.IsDueOrOverdue(due, nowLocal, _timeZone))
            {
                var result = _toast.Show(CreateRequest(id, locator));
                if (result.Kind == LocalToastDeliveryKind.Shown)
                {
                    _ledger.MarkDelivered(id, _clock.UtcNow);
                }
                else
                {
                    error = result.UserMessage ?? "Не удалось показать уведомление.";
                    DateTime retry = nowLocal + MaxDelay;
                    nextDue = nextDue == null || retry < nextDue ? retry : nextDue;
                }
            }
            else
            {
                DateTime dueLocal = ReminderDueSemantics.GetDueLocal(due, _timeZone);
                if (nextDue == null || dueLocal < nextDue)
                {
                    nextDue = dueLocal;
                }
            }
        }

        _ledger.PruneTo(liveIds);

        TimeSpan delay = MaxDelay;
        if (nextDue.HasValue)
        {
            TimeSpan until = nextDue.Value - nowLocal;
            if (until < TimeSpan.Zero)
            {
                until = TimeSpan.Zero;
            }

            delay = until < MaxDelay ? until : MaxDelay;
        }

        DateTime nextScan = nowLocal + delay;
        ScheduleNext(delay);
        PublishStatus(BuildStatus(true, true, nextScan, error));
    }

    internal static LocalToastRequest CreateRequest(string reminderId, MarkdownTaskLocator locator)
    {
        string body = locator.TaskText ?? string.Empty;
        if (body.Length > MaxBodyChars)
        {
            body = body[..MaxBodyChars] + "…";
        }

        if (string.IsNullOrWhiteSpace(body))
        {
            body = "Открытая задача";
        }

        return new LocalToastRequest
        {
            ReminderId = reminderId,
            NoteSyncId = locator.NoteSyncId,
            Title = ToastTitle,
            Body = body,
            LaunchArgs = ReminderActivationArgs.Create(locator.NoteSyncId)
        };
    }

    private void ScheduleNext(TimeSpan delay)
    {
        if (delay < TimeSpan.Zero)
        {
            delay = TimeSpan.Zero;
        }

        if (delay > MaxDelay)
        {
            delay = MaxDelay;
        }

        _timer.Change(delay);
    }

    private ReminderSchedulerStatus BuildStatus(bool enabled, bool running, DateTime? next, string? error)
    {
        string compact;
        if (!enabled)
        {
            compact = "Локальные напоминания: выключены";
        }
        else if (!_toast.IsAvailable)
        {
            compact = "Локальные напоминания: недоступны";
        }
        else if (!string.IsNullOrWhiteSpace(error))
        {
            compact = "Локальные напоминания: ошибка";
        }
        else if (next.HasValue)
        {
            compact = "Локальные напоминания: включены · проверка "
                + next.Value.ToString("HH:mm", CultureInfo.InvariantCulture);
        }
        else
        {
            compact = "Локальные напоминания: включены";
        }

        return new ReminderSchedulerStatus
        {
            UserEnabled = enabled,
            AdapterAvailable = _toast.IsAvailable,
            IsRunning = running,
            NextScanLocal = next,
            CompactText = compact,
            LastError = string.IsNullOrWhiteSpace(error) ? null : error
        };
    }

    private void PublishStatus(ReminderSchedulerStatus status)
    {
        lock (_gate)
        {
            _status = status;
        }

        StatusChanged?.Invoke();
    }
}
