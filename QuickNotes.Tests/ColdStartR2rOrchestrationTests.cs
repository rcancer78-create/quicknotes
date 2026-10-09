using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using Microsoft.Data.Sqlite;
using QuickNotes.App.Data;
using QuickNotes.Tools.Performance;
using Xunit;

namespace QuickNotes.Tests;

[TestCategory(TestCategories.UiSensitive)]
public sealed class ColdStartR2rOrchestrationTests
{
    [Fact]
    public void PublishedExe_MustBeQuickNotesAppAndOutsideBin()
    {
        const string fakeRepo = @"C:\qn_guard_repo_root";
        string ok = Path.Combine(fakeRepo, "artifacts", "publish", "cold-start", "runX", "baseline", "QuickNotes.App.exe");
        Assert.Equal(Path.GetFullPath(ok), ReadyToRunPublishPathGuard.NormalizeAndValidatePublishedExe(ok, fakeRepo));

        string wrongName = Path.Combine(fakeRepo, "artifacts", "publish", "cold-start", "runX", "baseline", "other.exe");
        Assert.Throws<InvalidOperationException>(() => ReadyToRunPublishPathGuard.NormalizeAndValidatePublishedExe(wrongName, fakeRepo));

        string inBin = Path.Combine(fakeRepo, "QuickNotes.App", "bin", "Release", "QuickNotes.App.exe");
        Assert.Throws<InvalidOperationException>(() => ReadyToRunPublishPathGuard.NormalizeAndValidatePublishedExe(inBin, fakeRepo));
    }

    [Fact]
    public void IntegrityProbe_OkOnHealthySyntheticDb_StopsWithoutRepairOnGarbage()
    {
        string dir = Path.Combine(Path.GetTempPath(), "qn_r2r_integrity_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            QuickNotesDbContext.ValidateIsolatedProfilePath(dir);
            string dbPath = Path.Combine(dir, "quicknotes.db");
            using (var db = new QuickNotesDbContext(dbPath))
            {
                DbInitializer.Initialize(db);
            }

            SqliteConnection.ClearAllPools();
            Assert.Equal("ok", SqliteIntegrityProbe.Check(dbPath), StringComparer.OrdinalIgnoreCase);
            SqliteIntegrityProbe.RequireOk(dbPath);

            string garbage = Path.Combine(dir, "not-a-db.db");
            File.WriteAllBytes(garbage, Encoding.ASCII.GetBytes("not sqlite"));
            var ex = Assert.ThrowsAny<Exception>(() => SqliteIntegrityProbe.RequireOk(garbage));
            Assert.DoesNotContain("user database is corrupted", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("восстанов", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            SqliteTestUtil.TryDeleteDirectory(dir);
        }
    }

    [Fact]
    public void IsolatedProcess_TimeoutKillsOnlyCreatedProcess()
    {
        string comspec = Environment.GetEnvironmentVariable("ComSpec") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
        var timeout = TimeSpan.FromMilliseconds(1500);
        var ex = Assert.Throws<TimeoutException>(() =>
            IsolatedColdStartLauncher.Run(comspec, new[] { "/c", "ping", "-n", "30", "127.0.0.1" }, timeout));
        Assert.Contains("exceeded timeout", ex.Message, StringComparison.Ordinal);
        Assert.Contains("terminated", ex.Message, StringComparison.Ordinal);

        string pidToken = "Created process ";
        int start = ex.Message.IndexOf(pidToken, StringComparison.Ordinal);
        Assert.True(start >= 0);
        string rest = ex.Message.Substring(start + pidToken.Length);
        string pidText = rest.Split(' ')[0];
        int pid = int.Parse(pidText, System.Globalization.CultureInfo.InvariantCulture);
        Process? leftover = null;
        try
        {
            leftover = Process.GetProcessById(pid);
        }
        catch (ArgumentException)
        {
            leftover = null;
        }

        if (leftover != null)
        {
            using (leftover)
            {
                Assert.True(leftover.HasExited);
            }
        }
    }

    [Fact]
    public void ParseStartupReadyMs_ReadsInvariantValue()
    {
        double ms = IsolatedColdStartLauncher.ParseStartupReadyMs("QN_PERF_PHASE:x:1\r\nQN_PERF_STARTUP_READY_MS:1730\nQN_PERF_WORKING_SET_BYTES:1\n");
        Assert.Equal(1730, ms);
        Assert.Throws<InvalidOperationException>(() => IsolatedColdStartLauncher.ParseStartupReadyMs("nope"));
        Assert.Equal(
            new[] { "QN_PERF_PHASE:db_initialize:50176" },
            IsolatedColdStartLauncher.ParsePhases("QN_PERF_PHASE:db_initialize:50176\nQN_PERF_STARTUP_READY_MS:1730"));
    }

    [Fact]
    public void LiveProfileFingerprint_CaptureIsStableWhenUnchanged()
    {
        var a = LiveProfileFingerprint.Capture();
        var b = LiveProfileFingerprint.Capture();
        LiveProfileFingerprint.AssertUnchanged(a, b);
        Assert.Equal(a.CombinedSha256Hex, b.CombinedSha256Hex);
    }
}
