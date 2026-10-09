using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using QuickNotes.App.Data;
using Xunit;

namespace QuickNotes.Tests;

[TestCategory(TestCategories.Integration)]
public sealed class ScrollFpsPresentMonHarnessTests
{
    [Fact]
    public void Script_DocumentsManualScrollIsolationAndNoCompositionTargetFallback()
    {
        string script = File.ReadAllText(Path.Combine(CiReportingConfigTests.FindRepoRoot(), "scripts", "Measure-ScrollFpsPresentMon.ps1"));
        Assert.Contains("Never uses", script, StringComparison.Ordinal);
        Assert.Contains("%LOCALAPPDATA%\\QuickNotes", script, StringComparison.Ordinal);
        Assert.Contains("scroll the note list", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("will not substitute CompositionTarget.Rendering", script, StringComparison.Ordinal);
        Assert.Contains("will not download tools", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("CaptureSeconds must be between 5 and 120", script, StringComparison.Ordinal);
        Assert.Contains("Refusing recursive delete", script, StringComparison.Ordinal);
        Assert.Contains("QUICKNOTES_LIVE_S3_REQUIRED", script, StringComparison.Ordinal);
        Assert.DoesNotContain("Invoke-WebRequest", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("curl.exe", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("--perf-scroll", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Capture_MissingPresentMon_ExitsWithActionableMessage()
    {
        string repo = CiReportingConfigTests.FindRepoRoot();
        string runId = UniqueRunId();
        string artifacts = Path.Combine(repo, "artifacts", "scroll-fps", runId);
        string missing = Path.Combine(Path.GetTempPath(), "qn-missing-presentmon-" + Guid.NewGuid().ToString("N"), "PresentMon.exe");
        try
        {
            var result = RunScript($"-Mode Capture -RunId {runId} -RepoRoot \"{repo}\" -PresentMonPath \"{missing}\"", repo);
            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains("PresentMon is not installed", result.Output, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("CompositionTarget.Rendering", result.Output, StringComparison.Ordinal);
            Assert.DoesNotContain("QN_PERF_SCROLL_MEDIAN_FPS", result.Output, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteOwnedArtifacts(artifacts, runId);
        }
    }

    [Fact]
    public void IsolatedProfile_LivePath_IsRejected()
    {
        string repo = CiReportingConfigTests.FindRepoRoot();
        string live = QuickNotesDbContext.LiveProfileDirectory;
        string runId = UniqueRunId();
        var result = RunScript(
            $"-Mode GuardOnly -RunId {runId} -RepoRoot \"{repo}\" -IsolatedProfile \"{live}\"",
            repo);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("live profile", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GuardOnly_WritesUnverifiedReport_AndCleansOwnedTemp()
    {
        string repo = CiReportingConfigTests.FindRepoRoot();
        string live = QuickNotesDbContext.LiveProfileDirectory;
        string[] before = Snapshot(live);
        string runId = UniqueRunId();
        string artifacts = Path.Combine(repo, "artifacts", "scroll-fps", runId);
        try
        {
            var result = RunScript($"-Mode GuardOnly -RunId {runId} -RepoRoot \"{repo}\"", repo);
            Assert.Equal(0, result.ExitCode);
            string report = Path.Combine(artifacts, "scroll-fps-presentmon.md");
            Assert.True(File.Exists(report), result.Output);
            string text = File.ReadAllText(report);
            Assert.Contains("**UNVERIFIED**", text, StringComparison.Ordinal);
            Assert.Contains("scroll the note list", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("| **Result** | **PASS** |", text, StringComparison.Ordinal);
            Assert.DoesNotContain(QuickNotesDbContext.LiveProfileDirectory, text, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TryDeleteOwnedArtifacts(artifacts, runId);
            Assert.Equal(before, Snapshot(live));
        }
    }

    [Fact]
    public void ProbeUnsafeDelete_RefusesLiveProfileRecursiveDelete()
    {
        string repo = CiReportingConfigTests.FindRepoRoot();
        string live = QuickNotesDbContext.LiveProfileDirectory;
        string[] before = Snapshot(live);
        string runId = UniqueRunId();
        var result = RunScript($"-Mode ProbeUnsafeDelete -RunId {runId} -RepoRoot \"{repo}\"", repo);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("refused unsafe recursive delete", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before, Snapshot(live));
    }

    [Fact]
    public void ParseOnly_FixtureCsv_WritesReportWithoutLaunchingUi()
    {
        string repo = CiReportingConfigTests.FindRepoRoot();
        string runId = UniqueRunId();
        string artifacts = Path.Combine(repo, "artifacts", "scroll-fps", runId);
        string csv = Path.Combine(repo, "QuickNotes.Tests", "Fixtures", "presentmon", "healthy-60hz.csv");
        string tools = Path.Combine(repo, "QuickNotes.Tools", "bin", "Release", "net8.0-windows10.0.19041.0", "QuickNotes.Tools.exe");
        Assert.True(File.Exists(tools), "Release QuickNotes.Tools.exe is required for ParseOnly.");

        try
        {
            var result = RunScript(
                $"-Mode ParseOnly -RunId {runId} -RepoRoot \"{repo}\" -CsvPath \"{csv}\" -ToolsExe \"{tools}\"",
                repo);
            Assert.Equal(2, result.ExitCode);
            string report = Path.Combine(artifacts, "scroll-fps-presentmon.md");
            Assert.True(File.Exists(report), result.Output);
            string text = File.ReadAllText(report);
            Assert.Contains("**UNVERIFIED**", text, StringComparison.Ordinal);
            Assert.Contains("Operator confirmed manual scroll | False", text, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteOwnedArtifacts(artifacts, runId);
        }
    }

    [Fact]
    public void ToolsParse_ConfirmedHealthyFixture_ExitsZeroPass()
    {
        string repo = CiReportingConfigTests.FindRepoRoot();
        string tools = Path.Combine(repo, "QuickNotes.Tools", "bin", "Release", "net8.0-windows10.0.19041.0", "QuickNotes.Tools.exe");
        Assert.True(File.Exists(tools), "Release QuickNotes.Tools.exe is required for scroll-fps-parse.");

        string dir = Path.Combine(Path.GetTempPath(), "qn-scroll-parse-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            QuickNotesDbContext.ValidateIsolatedProfilePath(dir);
            string report = Path.Combine(dir, "report.md");
            string csv = Path.Combine(repo, "QuickNotes.Tests", "Fixtures", "presentmon", "vrr.csv");
            var psi = new ProcessStartInfo
            {
                FileName = tools,
                Arguments = $"scroll-fps-parse --csv \"{csv}\" --report \"{report}\" --operator-confirms-scroll --os test-os --display vrr-display --tool-version fixture",
                WorkingDirectory = repo,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            using var process = Process.Start(psi) ?? throw new InvalidOperationException("tools failed to start");
            string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            Assert.True(process.WaitForExit(30_000));
            Assert.Equal(2, process.ExitCode);
            Assert.Contains("QN_SCROLL_FPS_VERDICT:UNVERIFIED", output, StringComparison.Ordinal);
            Assert.Contains("Variable refresh", File.ReadAllText(report), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    private static string UniqueRunId()
    {
        return DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ") + "_" + Guid.NewGuid().ToString("N")[..12];
    }

    private static string[] Snapshot(string liveRoot)
    {
        if (!Directory.Exists(liveRoot))
        {
            return Array.Empty<string>();
        }

        return Directory.GetFileSystemEntries(liveRoot, "*", SearchOption.AllDirectories);
    }

    private static void TryDeleteOwnedArtifacts(string artifacts, string runId)
    {
        string full = Path.GetFullPath(artifacts);
        if (!full.EndsWith(runId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!full.Contains(Path.Combine("artifacts", "scroll-fps"), StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (Directory.Exists(full))
        {
            Directory.Delete(full, recursive: true);
        }
    }

    private static (int ExitCode, string Output) RunScript(string args, string repo)
    {
        string script = Path.Combine(repo, "scripts", "Measure-ScrollFpsPresentMon.ps1");
        var psi = new ProcessStartInfo
        {
            FileName = PowerShellHost.FileName,
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\" {args}",
            WorkingDirectory = repo,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        using var process = Process.Start(psi) ?? throw new InvalidOperationException(PowerShellHost.FileName + " failed to start");
        var output = new StringBuilder();
        output.Append(process.StandardOutput.ReadToEnd());
        output.Append(process.StandardError.ReadToEnd());
        if (!process.WaitForExit(60_000))
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException("scroll fps harness timed out." + output);
        }

        return (process.ExitCode, output.ToString());
    }
}
