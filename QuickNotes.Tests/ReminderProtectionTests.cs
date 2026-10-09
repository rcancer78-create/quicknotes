using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using QuickNotes.App.Data;
using QuickNotes.App.Models;
using QuickNotes.App.Services;
using QuickNotes.App.Services.Reminders;
using QuickNotes.App.Services.Sync;
using Xunit;

namespace QuickNotes.Tests;

[TestCategory(TestCategories.Integration)]
public sealed class ReminderProtectionTests : IDisposable
{
    private readonly string _dir;
    private readonly string _dbPath;
    private readonly string _logDir;

    public ReminderProtectionTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "qn_rem_sec_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _dbPath = Path.Combine(_dir, "quicknotes.db");
        _logDir = Path.Combine(_dir, "Logs");
        Directory.CreateDirectory(_logDir);
        using var db = SqliteTestUtil.CreateContext(_dbPath);
        DbInitializer.Initialize(db);
    }

    public void Dispose()
    {
        SqliteTestUtil.TryDeleteDirectory(_dir);
    }

    [Fact]
    public async Task ProtectedNote_NeverReachesDelivery_AndLeavesNoPlaintextInLedgerToastOrLogs()
    {
        const string secret = "СЕКРЕТНЫЙ_ТОКЕН_ЗАЩИЩЁННОЙ_ЗАДАЧИ";
        using (var db = SqliteTestUtil.CreateContext(_dbPath))
        {
            db.Notes.Add(new Note
            {
                Title = string.Empty,
                Text = string.Empty,
                IsProtected = true,
                ProtectedCiphertextBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("- [ ] " + secret + " @2026-09-12"))
            });
            db.Notes.Add(new Note { Title = "Open", Text = "- [ ] public-task @2026-09-12" });
            db.SaveChanges();
        }

        var index = new TaskIndexService(() => SqliteTestUtil.CreateContext(_dbPath));
        var dated = index.QueryDatedOpenTasks();
        Assert.DoesNotContain(dated, t => t.TaskText.Contains(secret, StringComparison.Ordinal));
        Assert.Contains(dated, t => t.TaskText.Contains("public-task", StringComparison.Ordinal));

        var toast = new RecordingToastAdapter();
        var ledger = new ReminderLedgerStore(Path.Combine(_dir, ReminderLedgerStore.FileName));
        var clock = new FakeSyncClock
        {
            UtcNow = new DateTime(2026, 9, 12, 10, 0, 0, DateTimeKind.Local).ToUniversalTime()
        };

        using (ErrorLogService.UseScopedDirectory(_logDir))
        using (var scheduler = new TaskReminderScheduler(index, ledger, toast, clock, () => true))
        {
            scheduler.Start();
            await scheduler.RefreshAsync();
        }

        Assert.DoesNotContain(toast.Shown, t => (t.Body + t.Title + t.LaunchArgs).Contains(secret, StringComparison.Ordinal));
        string raw = ledger.ReadRawForTests();
        Assert.DoesNotContain(secret, raw, StringComparison.Ordinal);
        if (File.Exists(ErrorLogService.LogFilePath))
        {
            string log = File.ReadAllText(ErrorLogService.LogFilePath);
            Assert.DoesNotContain(secret, log, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void CorruptActivation_DoesNotLogPayload()
    {
        using (ErrorLogService.UseScopedDirectory(_logDir))
        {
            Assert.False(ReminderActivationArgs.TryParse("qn1." + new string('x', 32) + "- [ ] secret", out _));
            ErrorLogService.Write("ReminderActivation", "ignored");
        }

        string logPath = Path.Combine(_logDir, "errors.log");
        string log = File.Exists(logPath) ? File.ReadAllText(logPath) : string.Empty;
        Assert.DoesNotContain("secret", log, StringComparison.OrdinalIgnoreCase);
    }
}
