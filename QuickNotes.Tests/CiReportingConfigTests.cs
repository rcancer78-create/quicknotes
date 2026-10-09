using System;
using System.Diagnostics;
using System.IO;
using Xunit;

namespace QuickNotes.Tests;

[TestCategory(TestCategories.Integration)]
public sealed class CiReportingConfigTests
{
    [Fact]
    public void LocalValidator_ProvesWorkflowAndGitIgnoreWithoutGitHub()
    {
        string repo = FindRepoRoot();
        int exit = RunPwsh(Path.Combine(repo, "scripts", "Assert-CiReportingConfig.ps1"), $"-RepoRoot \"{repo}\"", repo);
        Assert.Equal(0, exit);
    }

    [Fact]
    public void CategorySummary_DisjointSumEqualsTotal_AndLiveCloudIsVisible()
    {
        string repo = FindRepoRoot();
        string trxDir = Path.Combine(repo, "QuickNotes.Tests", "Ci", "Fixtures");
        string passing = Path.Combine(Path.GetTempPath(), "qn-ci-summary-pass-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(passing);
        try
        {
            File.Copy(
                Path.Combine(trxDir, "category-summary-passing.trx.xml"),
                Path.Combine(passing, "test-results.trx"));
            string output = Path.Combine(passing, "summary.md");
            int exit = RunPwsh(
                Path.Combine(repo, "scripts", "Write-TestCategorySummary.ps1"),
                $"-TrxDirectory \"{passing}\" -OutputPath \"{output}\"",
                repo);
            Assert.Equal(0, exit);
            string text = File.ReadAllText(output);
            Assert.Contains("| LiveCloud | 1 |", text, StringComparison.Ordinal);
            Assert.Contains("Total: 4", text, StringComparison.Ordinal);
            Assert.Contains("Disjoint category sum: 4", text, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(passing, recursive: true);
        }
    }

    [Fact]
    public void CategorySummary_DoesNotHideOverallFailure()
    {
        string repo = FindRepoRoot();
        string failing = Path.Combine(Path.GetTempPath(), "qn-ci-summary-fail-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(failing);
        try
        {
            File.Copy(
                Path.Combine(repo, "QuickNotes.Tests", "Ci", "Fixtures", "category-summary-failing.trx.xml"),
                Path.Combine(failing, "test-results.trx"));
            string output = Path.Combine(failing, "summary.md");
            int exit = RunPwsh(
                Path.Combine(repo, "scripts", "Write-TestCategorySummary.ps1"),
                $"-TrxDirectory \"{failing}\" -OutputPath \"{output}\"",
                repo);
            Assert.Equal(1, exit);
            string text = File.ReadAllText(output);
            Assert.Contains("| LiveCloud | 1 |", text, StringComparison.Ordinal);
            Assert.Contains("Failed: 1", text, StringComparison.Ordinal);
            Assert.Contains("Disjoint category sum: 4", text, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(failing, recursive: true);
        }
    }

    internal readonly record struct PwshResult(int ExitCode, string Stdout, string Stderr);

    internal static int RunPwsh(string script, string args, string workDir)
    {
        return RunPwshDetailed(script, args, workDir).ExitCode;
    }

    internal static PwshResult RunPwshDetailed(string script, string args, string workDir)
    {
        PowerShellHost.PowerShellResult result = PowerShellHost.RunFile(script, args, workDir);
        return new PwshResult(result.ExitCode, result.Stdout, result.Stderr);
    }

    internal static string FindRepoRoot()
    {
        string? dir = Path.GetDirectoryName(typeof(CiReportingConfigTests).Assembly.Location);
        while (!string.IsNullOrEmpty(dir))
        {
            if (File.Exists(Path.Combine(dir, "QuickNotes.sln")) && Directory.Exists(Path.Combine(dir, ".github")))
            {
                return dir;
            }

            dir = Path.GetDirectoryName(dir);
        }

        throw new InvalidOperationException("Could not find repo root.");
    }
}
