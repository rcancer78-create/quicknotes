using System;
using System.Collections.Generic;
using System.IO;
using QuickNotes.Tests.Ci;
using Xunit;

namespace QuickNotes.Tests;

[TestCategory(TestCategories.Integration)]
public sealed class WindowsCiWorkflowTests
{
    private static string RepoRoot => CiReportingConfigTests.FindRepoRoot();

    private static GitHubWorkflowYaml.WorkflowDocument ParseRepoWorkflow()
    {
        string path = Path.Combine(RepoRoot, ".github", "workflows", "ci.yml");
        Assert.True(File.Exists(path), $"Windows CI workflow missing: {path}");
        return GitHubWorkflowYaml.Parse(File.ReadAllText(path));
    }

    [Fact]
    public void RealWorkflow_SatisfiesWhitespaceFormatGate()
    {
        IReadOnlyList<string> errors = WindowsCiWorkflowRules.Evaluate(ParseRepoWorkflow());
        Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors));
    }

    [Fact]
    public void Packaging_CannotRunWithoutSuccessfulAuthoritativeTests()
    {
        AssertContainsError(MutateReal(static text => WorkflowTextMutation.Replace(
            text, "    needs: windows-release", "    needs: missing-job")), "depend on successful");
        AssertContainsError(MutateReal(static text => WorkflowTextMutation.Replace(
            text, "    needs: windows-release", "    needs: windows-release\n    if: always()")), "depend on successful");
    }

    [Fact]
    public void Parser_IgnoresCommentedFormatGate()
    {
        const string yaml = """
            name: CI
            on:
              push:
            jobs:
              windows-release:
                timeout-minutes: 40
                steps:
                  - name: Restore
                    run: dotnet restore QuickNotes.sln --force
                  - name: Commented format
                    run: |
                      # dotnet format whitespace QuickNotes.sln --verify-no-changes --no-restore
                      echo skip
                  - name: Release build
                    run: dotnet build QuickNotes.sln -c Release --no-restore -warnaserror
                  - name: Full test suite
                    run: dotnet test QuickNotes.sln
            """;

        GitHubWorkflowYaml.WorkflowDocument doc = GitHubWorkflowYaml.Parse(yaml);
        IReadOnlyList<string> errors = WindowsCiWorkflowRules.Evaluate(doc);
        Assert.Contains(errors, static e => e.Contains("dotnet format whitespace exactly once", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Rules_FailMissingFormatStep()
    {
        AssertContainsError(MutateReal(static text => WorkflowTextMutation.Replace(
            text,
            FormatGateStepBlock,
            "")), "exactly once");
    }

    [Fact]
    public void Rules_FailBareDotnetFormatWithoutWhitespaceSubcommand()
    {
        AssertContainsError(MutateReal(static text => WorkflowTextMutation.Replace(
            text,
            "dotnet format whitespace",
            "dotnet format")), "whitespace-only");
    }

    [Fact]
    public void Rules_FailStyleFormatter()
    {
        AssertContainsError(MutateReal(static text => WorkflowTextMutation.Replace(
            text,
            "dotnet format whitespace",
            "dotnet format style")), "style or analyzers");
    }

    [Fact]
    public void Rules_FailMissingVerifyNoChanges()
    {
        AssertContainsError(MutateReal(static text => WorkflowTextMutation.Replace(
            text,
            " --verify-no-changes",
            "")), "--verify-no-changes");
    }

    [Fact]
    public void Rules_FailFormatBeforeRestore()
    {
        string yaml = File.ReadAllText(Path.Combine(RepoRoot, ".github", "workflows", "ci.yml"));
        AssertContainsError(
            GitHubWorkflowYaml.Parse(WorkflowTextMutation.ApplyAndAssertChanged(
                yaml,
                static text => SwapNamedSteps(text, "Clean restore", "Verify C# whitespace formatting"))),
            "after restore");
    }

    [Fact]
    public void Rules_FailFormatAfterBuild()
    {
        string yaml = File.ReadAllText(Path.Combine(RepoRoot, ".github", "workflows", "ci.yml"));
        AssertContainsError(
            GitHubWorkflowYaml.Parse(WorkflowTextMutation.ApplyAndAssertChanged(
                yaml,
                static text => SwapNamedSteps(text, "Verify C# whitespace formatting", "Release build"))),
            "before build");
    }

    [Fact]
    public void LocalValidator_ProvesFormatGateWithoutGitHub()
    {
        CiReportingConfigTests.PwshResult result = CiReportingConfigTests.RunPwshDetailed(
            Path.Combine(RepoRoot, "scripts", "Assert-CiReportingConfig.ps1"),
            $"-RepoRoot \"{RepoRoot}\"",
            RepoRoot);
        Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
    }

    [Fact]
    public void LocalValidator_FailsWhenFormatGateRemoved()
    {
        string workDir = Path.Combine(Path.GetTempPath(), "qn-ci-fmt-" + Guid.NewGuid().ToString("N"));
        try
        {
            CopyValidatorInputs(RepoRoot, workDir);
            string workflowPath = Path.Combine(workDir, ".github", "workflows", "ci.yml");
            string yml = WorkflowTextMutation.Replace(File.ReadAllText(workflowPath), FormatGateStepBlock, "");
            File.WriteAllText(workflowPath, yml);
            CiReportingConfigTests.PwshResult result = CiReportingConfigTests.RunPwshDetailed(
                Path.Combine(RepoRoot, "scripts", "Assert-CiReportingConfig.ps1"),
                $"-RepoRoot \"{workDir}\"",
                workDir);
            Assert.NotEqual(0, result.ExitCode);
            string output = result.Stdout + result.Stderr;
            Assert.Contains("dotnet format whitespace", output, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { Directory.Delete(workDir, recursive: true); } catch { }
        }
    }

    private const string FormatGateStepBlock =
        "      - name: Verify C# whitespace formatting\n        run: dotnet format whitespace \"$env:GITHUB_WORKSPACE\\QuickNotes.sln\" --verify-no-changes --no-restore\n\n";

    private static GitHubWorkflowYaml.WorkflowDocument MutateReal(Func<string, string> mutate)
    {
        string original = File.ReadAllText(Path.Combine(RepoRoot, ".github", "workflows", "ci.yml"));
        return GitHubWorkflowYaml.Parse(WorkflowTextMutation.ApplyAndAssertChanged(original, mutate));
    }

    private static void AssertContainsError(GitHubWorkflowYaml.WorkflowDocument doc, string expected)
    {
        IReadOnlyList<string> errors = WindowsCiWorkflowRules.Evaluate(doc);
        Assert.True(errors.Count > 0, "expected structural failure");
        Assert.Contains(errors, e => e.Contains(expected, StringComparison.OrdinalIgnoreCase));
    }

    private static string SwapNamedSteps(string yaml, string firstName, string secondName)
    {
        string a = $"      - name: {firstName}";
        string b = $"      - name: {secondName}";
        int aIndex = yaml.IndexOf(a, StringComparison.Ordinal);
        int bIndex = yaml.IndexOf(b, StringComparison.Ordinal);
        Assert.True(aIndex > 0 && bIndex > 0);
        int aEnd = yaml.IndexOf("\n      - name:", aIndex + a.Length, StringComparison.Ordinal);
        if (aEnd < 0)
            aEnd = yaml.Length;
        int bEnd = yaml.IndexOf("\n      - name:", bIndex + b.Length, StringComparison.Ordinal);
        if (bEnd < 0)
            bEnd = yaml.Length;
        if (aIndex < bIndex)
        {
            string blockA = yaml.Substring(aIndex, aEnd - aIndex);
            string blockB = yaml.Substring(bIndex, bEnd - bIndex);
            return yaml.Substring(0, aIndex) + blockB + yaml.Substring(aEnd, bIndex - aEnd) + blockA + yaml.Substring(bEnd);
        }

        string first = yaml.Substring(bIndex, bEnd - bIndex);
        string second = yaml.Substring(aIndex, aEnd - aIndex);
        return yaml.Substring(0, bIndex) + second + yaml.Substring(bEnd, aIndex - bEnd) + first + yaml.Substring(aEnd);
    }

    private static void CopyValidatorInputs(string repo, string dest)
    {
        string[] files =
        {
            Path.Combine(".github", "workflows", "ci.yml"),
            ".gitignore",
            Path.Combine("scripts", "Write-TestCategorySummary.ps1"),
            Path.Combine("QuickNotes.Tests", "TestCategories.cs")
        };
        foreach (string relative in files)
        {
            string target = Path.Combine(dest, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(Path.Combine(repo, relative), target);
        }
    }
}
