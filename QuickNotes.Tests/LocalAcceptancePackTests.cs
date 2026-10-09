using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using QuickNotes.App.Data;
using Xunit;

namespace QuickNotes.Tests;

[TestCategory(TestCategories.Integration)]
public sealed class LocalAcceptancePackTests
{
    [Fact]
    public void Script_DocumentsIsolationHangProtectionAndManualChecklist()
    {
        string script = File.ReadAllText(Path.Combine(CiReportingConfigTests.FindRepoRoot(), "scripts", "Run-LocalAcceptancePack.ps1"));
        Assert.DoesNotContain("never reads or mutates", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Never uses", script, StringComparison.Ordinal);
        Assert.Contains("never writes it", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("metadata-only", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not a cryptographic content hash", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("%LOCALAPPDATA%\\QuickNotes", script, StringComparison.Ordinal);
        Assert.Contains("--no-restore", script, StringComparison.Ordinal);
        Assert.Contains("--no-build", script, StringComparison.Ordinal);
        Assert.Contains("--blame-hang", script, StringComparison.Ordinal);
        Assert.Contains("--blame-hang-timeout 2m", script, StringComparison.Ordinal);
        Assert.Contains("Category!=LiveCloud", script, StringComparison.Ordinal);
        Assert.Contains("QUICKNOTES_LIVE_S3_REQUIRED", script, StringComparison.Ordinal);
        Assert.Contains("AWS_PROFILE", script, StringComparison.Ordinal);
        Assert.Contains("AWS_REGION", script, StringComparison.Ordinal);
        Assert.Contains("AWS_DEFAULT_REGION", script, StringComparison.Ordinal);
        Assert.Contains("AWS_SHARED_CREDENTIALS_FILE", script, StringComparison.Ordinal);
        Assert.Contains("in this process only", script, StringComparison.Ordinal);
        Assert.Contains("zero executed tests", script, StringComparison.Ordinal);
        Assert.Contains("SelfTestZeroExecuted", script, StringComparison.Ordinal);
        Assert.Contains("Assert-AcceptanceTrxHasExecutedTests", script, StringComparison.Ordinal);
        Assert.Contains("Refusing recursive delete", script, StringComparison.Ordinal);
        Assert.Contains("Manual-only checklist", script, StringComparison.Ordinal);
        Assert.Contains("These items are **not passed**", script, StringComparison.Ordinal);
        Assert.Contains("refusing to restore or hit package feeds", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("QUICKNOTES_LIVE_S3_REQUIRED = '1'", script, StringComparison.Ordinal);
        Assert.DoesNotContain("never uses cloud credentials or network", script, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DocumentedCommand_WithoutRepoRoot_BindsParameters_AndCompletesGuardOnly()
    {
        string repo = CiReportingConfigTests.FindRepoRoot();
        string live = QuickNotesDbContext.LiveProfileDirectory;
        string[] before = Snapshot(live);
        string runId = UniqueRunId();
        string artifacts = Path.Combine(repo, "artifacts", "local-acceptance", runId);
        try
        {
            var result = RunPackWindowsPowerShell($"-Mode GuardOnly -RunId {runId}", repo);
            Assert.DoesNotContain("Cannot bind argument to parameter 'Path'", result.Output, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("running scripts is disabled", result.Output, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0, result.ExitCode);
            string manifest = Path.Combine(artifacts, "local-acceptance-manifest.md");
            Assert.True(File.Exists(manifest), result.Output);
            Assert.Contains("Mode | GuardOnly", File.ReadAllText(manifest), StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteOwnedArtifacts(artifacts, runId);
            Assert.Equal(before, Snapshot(live));
        }
    }

    [Fact]
    public void GuardOnly_WritesManifestUnderUniqueArtifacts_AndLeavesLiveProfileUntouched()
    {
        string repo = CiReportingConfigTests.FindRepoRoot();
        string live = QuickNotesDbContext.LiveProfileDirectory;
        string[] before = Snapshot(live);
        string runId = UniqueRunId();
        string artifacts = Path.Combine(repo, "artifacts", "local-acceptance", runId);
        var previousEnv = CaptureCloudEnv();
        try
        {
            Environment.SetEnvironmentVariable("AWS_PROFILE", "child-only-profile");
            Environment.SetEnvironmentVariable("AWS_REGION", "us-east-1");
            Environment.SetEnvironmentVariable("AWS_DEFAULT_REGION", "us-west-2");
            Environment.SetEnvironmentVariable("AWS_SHARED_CREDENTIALS_FILE", @"C:\unused\credentials");

            var result = RunPack($"-Mode GuardOnly -RunId {runId} -RepoRoot \"{repo}\"", repo);
            Assert.Equal(0, result.ExitCode);
            string manifest = Path.Combine(artifacts, "local-acceptance-manifest.md");
            Assert.True(File.Exists(manifest), result.Output);
            string text = File.ReadAllText(manifest);
            Assert.Contains("Mode | GuardOnly", text, StringComparison.Ordinal);
            Assert.Contains("Live metadata unchanged | True", text, StringComparison.Ordinal);
            Assert.Contains("path/length/LastWriteTimeUtc ticks; not a content hash", text, StringComparison.Ordinal);
            Assert.Contains("never used as app profile; never written", text, StringComparison.Ordinal);
            Assert.Contains("Manual-only checklist (open)", text, StringComparison.Ordinal);
            Assert.DoesNotContain(QuickNotesDbContext.LiveProfileDirectory, GetIsolatedProfileLine(text), StringComparison.OrdinalIgnoreCase);
            Assert.Contains("qn-local-acceptance-" + runId, text, StringComparison.Ordinal);
            Assert.DoesNotContain("QUICKNOTES_LIVE_S3_REQUIRED = '1'", text, StringComparison.Ordinal);
            string clearedLine = GetManifestField(text, "Cleared cloud/env vars");
            Assert.Contains("AWS_PROFILE", clearedLine, StringComparison.Ordinal);
            Assert.Contains("AWS_DEFAULT_REGION", clearedLine, StringComparison.Ordinal);
            Assert.Contains("AWS_SHARED_CREDENTIALS_FILE", clearedLine, StringComparison.Ordinal);
            var clearedNames = clearedLine.Split(',').Select(s => s.Trim()).ToArray();
            Assert.Contains("AWS_REGION", clearedNames);
            Assert.Equal("child-only-profile", Environment.GetEnvironmentVariable("AWS_PROFILE"));
            Assert.Equal("us-east-1", Environment.GetEnvironmentVariable("AWS_REGION"));
            Assert.Equal("us-west-2", Environment.GetEnvironmentVariable("AWS_DEFAULT_REGION"));
            Assert.Equal(@"C:\unused\credentials", Environment.GetEnvironmentVariable("AWS_SHARED_CREDENTIALS_FILE"));
        }
        finally
        {
            RestoreCloudEnv(previousEnv);
            TryDeleteOwnedArtifacts(artifacts, runId);
            Assert.Equal(before, Snapshot(live));
        }
    }

    [Fact]
    public void SelfTestZeroExecuted_ValidTrxWithZeroTests_ExitsNonZero()
    {
        string repo = CiReportingConfigTests.FindRepoRoot();
        string runId = UniqueRunId();
        string artifacts = Path.Combine(repo, "artifacts", "local-acceptance", runId);
        try
        {
            var result = RunPack($"-Mode SelfTestZeroExecuted -RunId {runId} -RepoRoot \"{repo}\"", repo);
            Assert.Equal(1, result.ExitCode);
            string manifest = Path.Combine(artifacts, "local-acceptance-manifest.md");
            Assert.True(File.Exists(manifest), result.Output);
            string text = File.ReadAllText(manifest);
            Assert.Contains("FAIL (exit 1)", text, StringComparison.Ordinal);
            Assert.Contains("Valid TRX reported zero executed tests", text, StringComparison.Ordinal);
            Assert.Contains("| Tests total | 0 |", text, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteOwnedArtifacts(artifacts, runId);
        }
    }

    [Fact]
    public void IsolatedProfile_LivePathAndAncestor_AreRejected()
    {
        string repo = CiReportingConfigTests.FindRepoRoot();
        string live = QuickNotesDbContext.LiveProfileDirectory;
        string runId = UniqueRunId();
        var liveResult = RunPack(
            $"-Mode GuardOnly -RunId {runId} -RepoRoot \"{repo}\" -IsolatedProfile \"{live}\"",
            repo);
        Assert.NotEqual(0, liveResult.ExitCode);
        Assert.Contains("live profile", liveResult.Output, StringComparison.OrdinalIgnoreCase);

        string ancestor = Directory.GetParent(live)!.FullName;
        string runId2 = UniqueRunId();
        var ancestorResult = RunPack(
            $"-Mode GuardOnly -RunId {runId2} -RepoRoot \"{repo}\" -IsolatedProfile \"{ancestor}\"",
            repo);
        Assert.NotEqual(0, ancestorResult.ExitCode);
        Assert.Contains("ancestor", ancestorResult.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProbeUnsafeDelete_RefusesLiveProfileRecursiveDelete()
    {
        string repo = CiReportingConfigTests.FindRepoRoot();
        string live = QuickNotesDbContext.LiveProfileDirectory;
        string[] before = Snapshot(live);
        string runId = UniqueRunId();
        var result = RunPack($"-Mode ProbeUnsafeDelete -RunId {runId} -RepoRoot \"{repo}\"", repo);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("refused unsafe recursive delete", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before, Snapshot(live));
    }

    [Fact]
    public void SelfTestFailure_WritesManifestAndExitsNonZero()
    {
        string repo = CiReportingConfigTests.FindRepoRoot();
        string runId = UniqueRunId();
        string artifacts = Path.Combine(repo, "artifacts", "local-acceptance", runId);
        try
        {
            var result = RunPack($"-Mode SelfTestFailure -RunId {runId} -RepoRoot \"{repo}\"", repo);
            Assert.Equal(1, result.ExitCode);
            string manifest = Path.Combine(artifacts, "local-acceptance-manifest.md");
            Assert.True(File.Exists(manifest), result.Output);
            string text = File.ReadAllText(manifest);
            Assert.Contains("FAIL (exit 1)", text, StringComparison.Ordinal);
            Assert.Contains("| Failed | 1 |", text, StringComparison.Ordinal);
            Assert.Contains("self-test injected failure", text, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteOwnedArtifacts(artifacts, runId);
        }
    }

    [Fact]
    public void FilteredTesthost_CloudUsageFilter_LoadsWithoutDispatcherStartup_Repeatedly()
    {
        string repo = CiReportingConfigTests.FindRepoRoot();
        string csproj = Path.Combine(repo, "QuickNotes.Tests", "QuickNotes.Tests.csproj");
        const string filter =
            "FullyQualifiedName~CloudUsageServiceTests&FullyQualifiedName!~LocalAcceptancePackTests&Category!=LiveCloud";
        for (int i = 0; i < 3; i++)
        {
            string trxDir = Path.Combine(Path.GetTempPath(), "qn-focused-dispatcher-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(trxDir);
            try
            {
                var result = RunProcess(
                    "dotnet",
                    "test \"" + csproj + "\" -c Release --no-restore --no-build --nologo " +
                    "--blame-hang --blame-hang-timeout 2m --filter \"" + filter + "\" " +
                    "--results-directory \"" + trxDir + "\" --logger \"trx;LogFileName=dispatcher-ready.trx\"",
                    repo,
                    "filtered testhost dispatcher");
                Assert.True(result.ExitCode == 0, "iteration " + i + ": " + result.Output);
                Assert.DoesNotContain("dispatcher is not pumping", result.Output, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("Result reported for unknown test case", result.Output, StringComparison.OrdinalIgnoreCase);
                string trx = Path.Combine(trxDir, "dispatcher-ready.trx");
                Assert.True(File.Exists(trx), "iteration " + i + " missing TRX. " + result.Output);
                string xml = File.ReadAllText(trx);
                Assert.Contains("CloudUsageServiceTests", xml, StringComparison.Ordinal);
                Assert.DoesNotContain("WPF application dispatcher is not pumping", xml, StringComparison.Ordinal);
            }
            finally
            {
                try
                {
                    Directory.Delete(trxDir, recursive: true);
                }
                catch
                {
                    // Temp testhost files can linger briefly on Windows.
                }
            }
        }
    }

    [Fact]
    public void Lightweight_MissingBuildOutputs_FailsWithoutRestore()
    {
        string repo = CiReportingConfigTests.FindRepoRoot();
        string runId = UniqueRunId();
        string artifacts = Path.Combine(repo, "artifacts", "local-acceptance", runId);
        try
        {
            var result = RunPack(
                $"-Mode Lightweight -Configuration MissingLocalAcceptanceBuild -RunId {runId} -RepoRoot \"{repo}\"",
                repo);
            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains("refusing to restore or hit package feeds", result.Output, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("dotnet build", result.Output, StringComparison.Ordinal);
            Assert.DoesNotContain("Restored", result.Output, StringComparison.Ordinal);
            string manifest = Path.Combine(artifacts, "local-acceptance-manifest.md");
            Assert.True(File.Exists(manifest), result.Output);
            string text = File.ReadAllText(manifest);
            Assert.Contains("FAIL (exit 1)", text, StringComparison.Ordinal);
            Assert.Contains("Category!=LiveCloud", File.ReadAllText(Path.Combine(repo, "scripts", "Run-LocalAcceptancePack.ps1")), StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteOwnedArtifacts(artifacts, runId);
        }
    }

    private static readonly string[] CloudEnvNames =
    {
        "AWS_PROFILE",
        "AWS_REGION",
        "AWS_DEFAULT_REGION",
        "AWS_SHARED_CREDENTIALS_FILE"
    };

    private static string?[] CaptureCloudEnv()
    {
        var values = new string?[CloudEnvNames.Length];
        for (int i = 0; i < CloudEnvNames.Length; i++)
        {
            values[i] = Environment.GetEnvironmentVariable(CloudEnvNames[i]);
        }

        return values;
    }

    private static void RestoreCloudEnv(string?[] previous)
    {
        for (int i = 0; i < CloudEnvNames.Length; i++)
        {
            Environment.SetEnvironmentVariable(CloudEnvNames[i], previous[i]);
        }
    }

    private static string UniqueRunId()
    {
        return DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ") + "_" + Guid.NewGuid().ToString("N")[..12];
    }

    private static string GetIsolatedProfileLine(string manifest)
    {
        return GetManifestField(manifest, "Isolated profile");
    }

    private static string GetManifestField(string manifest, string field)
    {
        string prefix = "| " + field + " |";
        foreach (string line in manifest.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
        {
            if (line.StartsWith(prefix, StringComparison.Ordinal))
            {
                string rest = line.Substring(prefix.Length).Trim();
                if (rest.EndsWith('|'))
                {
                    rest = rest[..^1].Trim();
                }

                return rest;
            }
        }

        return string.Empty;
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

        if (!full.Contains(Path.Combine("artifacts", "local-acceptance"), StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (Directory.Exists(full))
        {
            Directory.Delete(full, recursive: true);
        }
    }

    private static (int ExitCode, string Output) RunPack(string args, string repo)
    {
        string script = Path.Combine(repo, "scripts", "Run-LocalAcceptancePack.ps1");
        return RunProcess(
            PowerShellHost.FileName,
            $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\" {args}",
            repo,
            "local acceptance pack (" + PowerShellHost.FileName + ")");
    }

    private static (int ExitCode, string Output) RunPackWindowsPowerShell(string args, string repo)
    {
        return RunProcess(
            "powershell",
            "-NoProfile -ExecutionPolicy Bypass -File scripts\\Run-LocalAcceptancePack.ps1 " + args,
            repo,
            "local acceptance pack (Windows PowerShell)");
    }

    private static (int ExitCode, string Output) RunProcess(string fileName, string arguments, string repo, string timeoutLabel)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            WorkingDirectory = repo,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        using var process = Process.Start(psi) ?? throw new InvalidOperationException(fileName + " failed to start");
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
}
