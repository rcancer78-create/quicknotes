using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using QuickNotes.App.Models;
using QuickNotes.App.Services;
using QuickNotes.App.Services.Reminders;
using QuickNotes.App.Services.Sync;
using Xunit;

namespace QuickNotes.Tests;

public sealed class ReminderDueSemanticsTests
{
    [Fact]
    public void DueOrOverdue_UsesNineAmLocalBoundary()
    {
        var due = new DateOnly(2026, 9, 12);
        var tz = TimeZoneInfo.CreateCustomTimeZone("qn-east", TimeSpan.FromHours(10), "qn-east", "qn-east");
        DateTime dueLocal = ReminderDueSemantics.GetDueLocal(due, tz);
        Assert.Equal(9, dueLocal.Hour);
        Assert.Equal(0, dueLocal.Minute);
        Assert.False(ReminderDueSemantics.IsDueOrOverdue(due, new DateTime(2026, 9, 12, 8, 59, 59), tz));
        Assert.True(ReminderDueSemantics.IsDueOrOverdue(due, new DateTime(2026, 9, 12, 9, 0, 0), tz));
        Assert.True(ReminderDueSemantics.IsDueOrOverdue(due, new DateTime(2026, 9, 12, 18, 0, 0), tz));
        Assert.False(ReminderDueSemantics.IsDueOrOverdue(due, new DateTime(2026, 9, 11, 23, 59, 0), tz));
    }
}

public sealed class ReminderIdentityAndActivationTests
{
    [Fact]
    public void Identity_IsStable_AndDoesNotEmbedPlaintext()
    {
        var sync = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        string a = ReminderIdentity.Compute(sync, "-[ ] buy milk", new DateOnly(2026, 10, 1));
        string b = ReminderIdentity.Compute(sync, "-[ ] buy milk", new DateOnly(2026, 10, 1));
        Assert.Equal(a, b);
        Assert.Equal(64, a.Length);
        Assert.DoesNotContain("milk", a, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual(a, ReminderIdentity.Compute(sync, "-[ ] buy milk", new DateOnly(2026, 10, 2)));
        Assert.NotEqual(a, ReminderIdentity.Compute(Guid.NewGuid(), "-[ ] buy milk", new DateOnly(2026, 10, 1)));
    }

    [Fact]
    public void ActivationArgs_Roundtrip_AndRejectCorrupt()
    {
        var id = Guid.NewGuid();
        string args = ReminderActivationArgs.Create(id);
        Assert.True(ReminderActivationArgs.TryParse(args, out Guid parsed));
        Assert.Equal(id, parsed);
        Assert.False(ReminderActivationArgs.TryParse("qn1.not-a-guid-value-here-zzzzzz", out _));
        Assert.False(ReminderActivationArgs.TryParse("secret task text", out _));
        Assert.False(ReminderActivationArgs.TryParse(null, out _));
    }

    [Fact]
    public void ToastXml_EscapesBody_AndKeepsOpaqueLaunch()
    {
        var sync = Guid.NewGuid();
        string xml = WindowsToastAdapter.BuildToastXml("Напоминание QuickNotes", "Buy <milk> & \"eggs\"", ReminderActivationArgs.Create(sync));
        Assert.Contains("&lt;milk&gt;", xml, StringComparison.Ordinal);
        Assert.Contains("&amp;", xml, StringComparison.Ordinal);
        Assert.Contains(ReminderActivationArgs.Create(sync), xml, StringComparison.Ordinal);
        Assert.DoesNotContain("Buy <milk>", xml, StringComparison.Ordinal);
    }
}

[TestCategory(TestCategories.Integration)]
public sealed class ReminderLedgerStoreTests : IDisposable
{
    private readonly string _dir;
    private readonly string _path;

    public ReminderLedgerStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "qn_ledger_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, ReminderLedgerStore.FileName);
    }

    public void Dispose() => SqliteTestUtil.TryDeleteDirectory(_dir);

    [Fact]
    public void CorruptLedger_FailsClosed_WithoutPersistingGarbageAsDelivered()
    {
        File.WriteAllText(_path, "{ not json, task: секретная-задача }");
        var store = new ReminderLedgerStore(_path);
        string id = ReminderIdentity.Compute(Guid.NewGuid(), "fp", new DateOnly(2026, 1, 1));
        Assert.False(store.IsDelivered(id));
        Assert.Empty(store.Snapshot());
    }

    [Fact]
    public void MarkDelivered_Roundtrips_WithoutTaskText()
    {
        var store = new ReminderLedgerStore(_path);
        string id = ReminderIdentity.Compute(Guid.NewGuid(), "-[ ] secret-token", new DateOnly(2026, 5, 1));
        store.MarkDelivered(id, DateTime.UtcNow);
        var reloaded = new ReminderLedgerStore(_path);
        Assert.True(reloaded.IsDelivered(id));
        string raw = reloaded.ReadRawForTests();
        Assert.DoesNotContain("secret-token", raw, StringComparison.Ordinal);
        Assert.Contains(id, raw, StringComparison.Ordinal);
    }
}

[TestCategory(TestCategories.Integration)]
public sealed class TaskReminderSchedulerTests : IDisposable
{
    private readonly string _dir;

    public TaskReminderSchedulerTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "qn_rem_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose() => SqliteTestUtil.TryDeleteDirectory(_dir);

    [Fact]
    public async Task Scan_BeforeDue_DoesNotDeliver_AtNineAm_DeliversOnce_RestartIsIdempotent()
    {
        var sync = Guid.NewGuid();
        var locator = DatedTask(sync, "pay rent @2026-09-12", new DateOnly(2026, 9, 12));
        var index = new FakeDatedTaskIndex(locator);
        var clock = new FakeSyncClock
        {
            UtcNow = new DateTime(2026, 9, 12, 8, 59, 0, DateTimeKind.Local).ToUniversalTime()
        };
        var toast = new RecordingToastAdapter();
        var ledger = new ReminderLedgerStore(Path.Combine(_dir, ReminderLedgerStore.FileName));
        using var scheduler = Create(index, ledger, toast, clock, () => true);

        scheduler.Start();
        await scheduler.RefreshAsync();
        Assert.Empty(toast.Shown);

        clock.UtcNow = new DateTime(2026, 9, 12, 9, 0, 0, DateTimeKind.Local).ToUniversalTime();
        await scheduler.RefreshAsync();
        Assert.Single(toast.Shown);
        Assert.Equal("pay rent @2026-09-12", toast.Shown[0].Body);

        await scheduler.RefreshAsync();
        Assert.Single(toast.Shown);

        using var restarted = Create(index, new ReminderLedgerStore(Path.Combine(_dir, ReminderLedgerStore.FileName)), toast, clock, () => true);
        restarted.Start();
        await restarted.RefreshAsync();
        Assert.Single(toast.Shown);
    }

    [Fact]
    public async Task Overdue_DeliversOnNextScan_ChangedDueDate_GetsNewIdentity()
    {
        var sync = Guid.NewGuid();
        var first = DatedTask(sync, "ship @2026-01-01", new DateOnly(2026, 1, 1));
        var index = new FakeDatedTaskIndex(first);
        var clock = new FakeSyncClock
        {
            UtcNow = new DateTime(2026, 1, 2, 10, 0, 0, DateTimeKind.Local).ToUniversalTime()
        };
        var toast = new RecordingToastAdapter();
        var ledger = new ReminderLedgerStore(Path.Combine(_dir, ReminderLedgerStore.FileName));
        using var scheduler = Create(index, ledger, toast, clock, () => true);
        scheduler.Start();
        await scheduler.RefreshAsync();
        Assert.Single(toast.Shown);

        var moved = DatedTask(sync, "ship @2026-01-03", new DateOnly(2026, 1, 3));
        index.Items = new[] { moved };
        clock.UtcNow = new DateTime(2026, 1, 3, 9, 0, 0, DateTimeKind.Local).ToUniversalTime();
        await scheduler.RefreshAsync();
        Assert.Equal(2, toast.Shown.Count);
    }

    [Fact]
    public async Task ClosedOrDeletedTask_IsNotDelivered_AndPrunesLedger()
    {
        var locator = DatedTask(Guid.NewGuid(), "gone @2026-09-12", new DateOnly(2026, 9, 12));
        var index = new FakeDatedTaskIndex(locator);
        var clock = new FakeSyncClock
        {
            UtcNow = new DateTime(2026, 9, 12, 10, 0, 0, DateTimeKind.Local).ToUniversalTime()
        };
        var toast = new RecordingToastAdapter();
        var ledger = new ReminderLedgerStore(Path.Combine(_dir, ReminderLedgerStore.FileName));
        using var scheduler = Create(index, ledger, toast, clock, () => true);
        scheduler.Start();
        await scheduler.RefreshAsync();
        Assert.Single(toast.Shown);
        Assert.Single(ledger.Snapshot());

        index.Items = Array.Empty<MarkdownTaskLocator>();
        await scheduler.RefreshAsync();
        Assert.Empty(ledger.Snapshot());
        Assert.Single(toast.Shown);
    }

    [Fact]
    public async Task Cancellation_StopsInFlightScan()
    {
        var index = new BlockingDatedTaskIndex();
        var clock = new FakeSyncClock();
        var toast = new RecordingToastAdapter();
        var ledger = new ReminderLedgerStore(Path.Combine(_dir, ReminderLedgerStore.FileName));
        using var scheduler = Create(index, ledger, toast, clock, () => true);
        scheduler.Start();
        using var cts = new CancellationTokenSource();
        var refresh = scheduler.RefreshAsync(cts.Token);
        index.Entered.Wait(TimeSpan.FromSeconds(5));
        cts.Cancel();
        index.Release.Set();
        await refresh;
        Assert.Empty(toast.Shown);
    }

    [Fact]
    public async Task AdapterUnavailable_SetsStatus_AndDoesNotMarkDelivered()
    {
        var locator = DatedTask(Guid.NewGuid(), "task @2026-09-12", new DateOnly(2026, 9, 12));
        var index = new FakeDatedTaskIndex(locator);
        var clock = new FakeSyncClock
        {
            UtcNow = new DateTime(2026, 9, 12, 10, 0, 0, DateTimeKind.Local).ToUniversalTime()
        };
        var toast = new UnavailableToastAdapter("Уведомления Windows недоступны.");
        var ledger = new ReminderLedgerStore(Path.Combine(_dir, ReminderLedgerStore.FileName));
        using var scheduler = Create(index, ledger, toast, clock, () => true);
        scheduler.Start();
        await scheduler.RefreshAsync();
        Assert.Empty(ledger.Snapshot());
        Assert.False(scheduler.Status.AdapterAvailable);
        Assert.Contains("недоступны", scheduler.Status.CompactText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AdapterError_DoesNotMarkDelivered()
    {
        var locator = DatedTask(Guid.NewGuid(), "task @2026-09-12", new DateOnly(2026, 9, 12));
        var index = new FakeDatedTaskIndex(locator);
        var clock = new FakeSyncClock
        {
            UtcNow = new DateTime(2026, 9, 12, 10, 0, 0, DateTimeKind.Local).ToUniversalTime()
        };
        var toast = new RecordingToastAdapter { ForcedResult = LocalToastDeliveryKind.Failed, ForcedMessage = "Не удалось показать уведомление." };
        var ledger = new ReminderLedgerStore(Path.Combine(_dir, ReminderLedgerStore.FileName));
        using var scheduler = Create(index, ledger, toast, clock, () => true);
        scheduler.Start();
        await scheduler.RefreshAsync();
        Assert.Empty(ledger.Snapshot());
        Assert.Equal("Не удалось показать уведомление.", scheduler.Status.LastError);
    }

    [Fact]
    public async Task DisabledSetting_DoesNotShowToast()
    {
        var locator = DatedTask(Guid.NewGuid(), "task @2026-09-12", new DateOnly(2026, 9, 12));
        var index = new FakeDatedTaskIndex(locator);
        var clock = new FakeSyncClock
        {
            UtcNow = new DateTime(2026, 9, 12, 10, 0, 0, DateTimeKind.Local).ToUniversalTime()
        };
        var toast = new RecordingToastAdapter();
        bool enabled = false;
        using var scheduler = Create(index, new ReminderLedgerStore(Path.Combine(_dir, ReminderLedgerStore.FileName)), toast, clock, () => enabled);
        scheduler.Start();
        await scheduler.RefreshAsync();
        Assert.Empty(toast.Shown);
        Assert.Contains("выключены", scheduler.Status.CompactText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TimerAdvance_WithoutRealSleep_DeliversAtDue()
    {
        var locator = DatedTask(Guid.NewGuid(), "later @2026-09-12", new DateOnly(2026, 9, 12));
        var index = new FakeDatedTaskIndex(locator);
        var clock = new FakeSyncClock
        {
            UtcNow = new DateTime(2026, 9, 12, 8, 50, 0, DateTimeKind.Local).ToUniversalTime()
        };
        var toast = new RecordingToastAdapter();
        using var scheduler = Create(index, new ReminderLedgerStore(Path.Combine(_dir, ReminderLedgerStore.FileName)), toast, clock, () => true);
        scheduler.Start();
        await scheduler.RefreshAsync();
        Assert.Empty(toast.Shown);
        Assert.Contains(clock.Timers, t => t.IsActive);
        clock.UtcNow = new DateTime(2026, 9, 12, 9, 0, 0, DateTimeKind.Local).ToUniversalTime();
        clock.TriggerAllActive();
        await WaitUntil(() => toast.Shown.Count > 0, TimeSpan.FromSeconds(5));
        Assert.Single(toast.Shown);
    }

    private static TaskReminderScheduler Create(
        ITaskIndexService index,
        ReminderLedgerStore ledger,
        ILocalToastAdapter toast,
        ISyncClock clock,
        Func<bool> enabled)
        => new(index, ledger, toast, clock, enabled, TimeZoneInfo.Local);

    private static MarkdownTaskLocator DatedTask(Guid sync, string text, DateOnly due)
        => new()
        {
            NoteSyncId = sync,
            TaskText = text,
            DueDate = due,
            Fingerprint = "-\u001f" + text
        };

    private static async Task WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var start = DateTime.UtcNow;
        while (!condition())
        {
            if (DateTime.UtcNow - start > timeout)
            {
                throw new TimeoutException("condition");
            }

            await Task.Delay(20);
        }
    }

    private sealed class FakeDatedTaskIndex : ITaskIndexService
    {
        public IReadOnlyList<MarkdownTaskLocator> Items { get; set; }

        public FakeDatedTaskIndex(params MarkdownTaskLocator[] items)
        {
            Items = items;
        }

        public Task<TaskIndexPage> QueryAsync(string? searchQuery, int skip, int take, CancellationToken cancellationToken = default)
            => Task.FromResult(new TaskIndexPage { Items = Items, TotalCount = Items.Count });

        public IReadOnlyList<MarkdownTaskLocator> QueryDatedOpenTasks(CancellationToken cancellationToken = default)
            => Items;
    }

    private sealed class BlockingDatedTaskIndex : ITaskIndexService
    {
        public ManualResetEventSlim Entered { get; } = new(false);
        public ManualResetEventSlim Release { get; } = new(false);

        public Task<TaskIndexPage> QueryAsync(string? searchQuery, int skip, int take, CancellationToken cancellationToken = default)
            => Task.FromResult(new TaskIndexPage());

        public IReadOnlyList<MarkdownTaskLocator> QueryDatedOpenTasks(CancellationToken cancellationToken = default)
        {
            Entered.Set();
            Release.Wait(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return Array.Empty<MarkdownTaskLocator>();
        }
    }
}
