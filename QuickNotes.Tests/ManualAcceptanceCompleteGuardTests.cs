using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using QuickNotes.App.Data;
using Xunit;

namespace QuickNotes.Tests;

[Collection(LiveProfileSnapshotCollection.Name)]
[TestCategory(TestCategories.Integration)]
public sealed class ManualAcceptanceCompleteGuardTests
{
    [Fact]
    public void DocumentedCompleteCommand_WithoutRepoRoot_BindsParameters_AndDoesNotTouchLiveProfile()
    {
        string repo = CiReportingConfigTests.FindRepoRoot();
        string live = QuickNotesDbContext.LiveProfileDirectory;
        string[] liveBefore = Snapshot(live);
        string runId = UniqueRunId();
        string runDir = Path.Combine(repo, "artifacts", "manual-acceptance", runId);
        var result = RunDocumentedComplete(repo, runId);
        Assert.NotEqual(0, result.ExitCode);
        Assert.DoesNotContain("running scripts is disabled", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SecurityError", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Cannot bind argument to parameter 'Path'", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("does not exist", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(runDir));
        Assert.Equal(liveBefore, Snapshot(live));
    }

    [Fact]
    public void Complete_RejectsArbitraryDirectory_AndDoesNotWriteReportThere()
    {
        string repo = CiReportingConfigTests.FindRepoRoot();
        string live = QuickNotesDbContext.LiveProfileDirectory;
        string[] liveBefore = Snapshot(live);
        string outsider = Path.Combine(Path.GetTempPath(), "qn_manual_complete_out_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outsider);
        try
        {
            var result = RunComplete(repo, outsider);
            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains("strict descendant", result.Output, StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(Path.Combine(outsider, "completion-report.json")));
            Assert.Equal(liveBefore, Snapshot(live));
        }
        finally
        {
            SqliteTestUtil.TryDeleteDirectory(outsider);
        }
    }

    [Fact]
    public void Complete_RejectsArtifactsRootItself()
    {
        string repo = CiReportingConfigTests.FindRepoRoot();
        string live = QuickNotesDbContext.LiveProfileDirectory;
        string[] liveBefore = Snapshot(live);
        string artifactsRoot = Path.Combine(repo, "artifacts", "manual-acceptance");
        Directory.CreateDirectory(artifactsRoot);
        string reportAtRoot = Path.Combine(artifactsRoot, "completion-report.json");
        bool reportExisted = File.Exists(reportAtRoot);
        var result = RunComplete(repo, artifactsRoot);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("strict descendant", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(reportExisted, File.Exists(reportAtRoot));
        Assert.Equal(liveBefore, Snapshot(live));
    }

    [Fact]
    public void Complete_RejectsLiveProfileRelatives()
    {
        string repo = CiReportingConfigTests.FindRepoRoot();
        string live = QuickNotesDbContext.LiveProfileDirectory;
        string[] liveBefore = Snapshot(live);

        if (Directory.Exists(live))
        {
            var liveResult = RunComplete(repo, live);
            Assert.NotEqual(0, liveResult.ExitCode);
            Assert.Contains("live profile", liveResult.Output, StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(Path.Combine(live, "completion-report.json")));
        }

        string ancestor = Directory.GetParent(live)!.FullName;
        string ancestorReport = Path.Combine(ancestor, "completion-report.json");
        bool ancestorReportExisted = File.Exists(ancestorReport);
        var ancestorResult = RunComplete(repo, ancestor);
        Assert.NotEqual(0, ancestorResult.ExitCode);
        Assert.Contains("live profile", ancestorResult.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(ancestorReportExisted, File.Exists(ancestorReport));
        Assert.Equal(liveBefore, Snapshot(live));
    }

    [Fact]
    public void Complete_RejectsJunctionAliases()
    {
        string repo = CiReportingConfigTests.FindRepoRoot();
        string live = QuickNotesDbContext.LiveProfileDirectory;
        string[] liveBefore = Snapshot(live);
        string runId = UniqueRunId();
        string realRun = Path.Combine(repo, "artifacts", "manual-acceptance", runId);
        string aliasRoot = Path.Combine(Path.GetTempPath(), "qn_manual_complete_junc_" + Guid.NewGuid().ToString("N"));
        string outsideTarget = Path.Combine(Path.GetTempPath(), "qn_manual_complete_junc_tgt_" + Guid.NewGuid().ToString("N"));
        string inwardAlias = Path.Combine(aliasRoot, "as-run");
        string outwardAlias = Path.Combine(repo, "artifacts", "manual-acceptance", "junc_" + runId);
        Directory.CreateDirectory(realRun);
        Directory.CreateDirectory(aliasRoot);
        Directory.CreateDirectory(outsideTarget);
        try
        {
            if (!TryCreateJunction(inwardAlias, realRun))
            {
                return;
            }

            var inward = RunComplete(repo, inwardAlias);
            Assert.NotEqual(0, inward.ExitCode);
            Assert.Contains("strict descendant", inward.Output, StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(Path.Combine(realRun, "completion-report.json")));
            Assert.False(File.Exists(Path.Combine(inwardAlias, "completion-report.json")));

            if (!TryCreateJunction(outwardAlias, outsideTarget))
            {
                return;
            }

            var outward = RunComplete(repo, outwardAlias);
            Assert.NotEqual(0, outward.ExitCode);
            Assert.Contains("strict descendant", outward.Output, StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(Path.Combine(outsideTarget, "completion-report.json")));
            Assert.False(File.Exists(Path.Combine(outwardAlias, "completion-report.json")));
            Assert.Equal(liveBefore, Snapshot(live));
        }
        finally
        {
            TryDeleteJunction(inwardAlias);
            TryDeleteJunction(outwardAlias);
            TryDeleteOwnedRun(realRun, runId);
            SqliteTestUtil.TryDeleteDirectory(aliasRoot);
            SqliteTestUtil.TryDeleteDirectory(outsideTarget);
        }
    }

    [Fact]
    public void Complete_AcceptsLegitimateRunDirectory()
    {
        string repo = CiReportingConfigTests.FindRepoRoot();
        string live = QuickNotesDbContext.LiveProfileDirectory;
        string[] liveBefore = Snapshot(live);
        string runId = UniqueRunId();
        string runDir = Path.Combine(repo, "artifacts", "manual-acceptance", runId);
        Directory.CreateDirectory(runDir);
        try
        {
            var result = RunComplete(repo, runDir);
            Assert.Equal(0, result.ExitCode);
            Assert.True(File.Exists(Path.Combine(runDir, "completion-report.json")), result.Output);
            Assert.Equal(liveBefore, Snapshot(live));
        }
        finally
        {
            TryDeleteOwnedRun(runDir, runId);
        }
    }

    private static string UniqueRunId()
    {
        return "complete_guard_" + DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ") + "_" + Guid.NewGuid().ToString("N")[..12];
    }

    private static string[] Snapshot(string liveRoot)
    {
        if (!Directory.Exists(liveRoot))
        {
            return Array.Empty<string>();
        }

        return Directory.GetFileSystemEntries(liveRoot, "*", SearchOption.AllDirectories)
            .Select(p => p + "|" + (File.Exists(p) ? new FileInfo(p).Length + "|" + File.GetLastWriteTimeUtc(p).Ticks : "dir"))
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static (int ExitCode, string Output) RunDocumentedComplete(string repo, string runId)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "powershell",
            Arguments = "-NoProfile -ExecutionPolicy Bypass -File scripts\\Start-ManualAcceptance.ps1 -Mode Complete -RunDirectory artifacts\\manual-acceptance\\" + runId,
            WorkingDirectory = repo,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        return RunProcess(psi, "documented manual acceptance complete");
    }

    private static (int ExitCode, string Output) RunComplete(string repo, string runDirectory)
    {
        string script = Path.Combine(repo, "scripts", "Start-ManualAcceptance.ps1");
        var psi = new ProcessStartInfo
        {
            FileName = PowerShellHost.FileName,
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\" -Mode Complete -RepoRoot \"{repo}\" -RunDirectory \"{runDirectory}\"",
            WorkingDirectory = repo,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        return RunProcess(psi, "manual acceptance complete");
    }

    private static (int ExitCode, string Output) RunProcess(ProcessStartInfo psi, string timeoutLabel)
    {
        using var process = Process.Start(psi) ?? throw new InvalidOperationException(psi.FileName + " failed to start");
        var output = new StringBuilder();
        output.Append(process.StandardOutput.ReadToEnd());
        output.Append(process.StandardError.ReadToEnd());
        if (!process.WaitForExit(60_000))
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException(timeoutLabel + " timed out." + output);
        }

        return (process.ExitCode, output.ToString());
    }

    private static void TryDeleteOwnedRun(string runDir, string runId)
    {
        string full = Path.GetFullPath(runDir);
        if (!full.EndsWith(runId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!full.Contains(Path.Combine("artifacts", "manual-acceptance"), StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        SqliteTestUtil.TryDeleteDirectory(full);
    }

    private static bool TryCreateJunction(string linkPath, string targetPath)
    {
        try
        {
            var psi = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{linkPath}\" \"{targetPath}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using Process? process = Process.Start(psi);
            if (process == null)
            {
                return false;
            }

            process.WaitForExit(5000);
            return Directory.Exists(linkPath)
                   && (File.GetAttributes(linkPath) & FileAttributes.ReparsePoint) != 0;
        }
        catch
        {
            return false;
        }
    }

    private static void TryDeleteJunction(string linkPath)
    {
        try
        {
            if (Directory.Exists(linkPath)
                && (File.GetAttributes(linkPath) & FileAttributes.ReparsePoint) != 0)
            {
                Directory.Delete(linkPath);
            }
        }
        catch
        {
            // Best-effort: never recurse through a junction.
        }
    }
}
