using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using QuickNotes.Tests.Ci;
using Xunit;

namespace QuickNotes.Tests;

[TestCategory(TestCategories.Integration)]
public sealed class CloudAcceptanceWorkflowTests
{
    private static string RepoRoot => CiReportingConfigTests.FindRepoRoot();

    private static GitHubWorkflowYaml.WorkflowDocument ParseRepoWorkflow()
    {
        string path = Path.Combine(RepoRoot, ".github", "workflows", "cloud-acceptance.yml");
        Assert.True(File.Exists(path), $"Cloud acceptance workflow missing: {path}");
        return GitHubWorkflowYaml.Parse(File.ReadAllText(path));
    }

    [Fact]
    public void Parser_IgnoresCommentedTriggersAndDotnetTest()
    {
        const string yaml = """
            name: x
            on:
              # push:
              # pull_request:
              # schedule:
              workflow_dispatch:
            permissions:
              contents: read
            concurrency:
              group: g
              cancel-in-progress: false
            jobs:
              j:
                timeout-minutes: 5
                environment: cloud-acceptance
                steps:
                  - name: Restore
                    run: dotnet restore
                  - name: Build
                    run: dotnet build --no-restore
                  - name: Encrypt
                    run: encrypt-credentials in.json out.dat
                    env:
                      S3_ACCESS_KEY_ID: x
                      S3_SECRET_ACCESS_KEY: y
                      S3_ENDPOINT: e
                      S3_BUCKET: b
                  - name: Run smoke
                    if: ${{ inputs.scenario == 'Smoke' || inputs.scenario == 'All' }}
                    run: |
                      # dotnet test --filter FullyQualifiedName~YandexObjectStorageLiveSmokeTests
                      pwsh -File scripts/Run-YandexCloudLiveSmoke.ps1 -NoRestore -NoBuild -TrxLogFileName cloud-smoke.trx -SanitizedReportFileName cloud-smoke.md -BlameHangTimeout 2m
                  - name: Run acceptance
                    if: ${{ inputs.scenario == 'Acceptance' || inputs.scenario == 'All' }}
                    run: pwsh -File scripts/Run-YandexCloudLiveAcceptance.ps1 -NoRestore -NoBuild -TrxLogFileName cloud-acceptance.trx -SanitizedReportFileName cloud-acceptance.md -BlameHangTimeout 2m
                  - name: Category summary
                    if: always()
                    run: pwsh -File scripts/Write-TestCategorySummary.ps1
                  - name: Upload
                    if: always()
                    uses: actions/upload-artifact@v4
                    with:
                      path: |
                        artifacts/ci/cloud-acceptance/cloud-smoke.trx
                        artifacts/ci/cloud-acceptance/cloud-acceptance.trx
                        artifacts/ci/cloud-acceptance/cloud-smoke.md
                        artifacts/ci/cloud-acceptance/cloud-acceptance.md
                        artifacts/ci/cloud-acceptance/category-summary.md
                      retention-days: 7
                  - name: Cleanup credentials
                    if: always()
                    run: Remove-Item $env:RUNNER_TEMP/qn-cloud-creds
            """;

        GitHubWorkflowYaml.WorkflowDocument doc = GitHubWorkflowYaml.Parse(yaml);
        Assert.Equal(new[] { "workflow_dispatch" }, doc.TriggerKeys.ToArray());
        IReadOnlyList<string> errors = CloudAcceptanceWorkflowRules.Evaluate(doc);
        Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors));
    }

    [Fact]
    public void Rules_FailDirectScenarioDotnetTestInYaml()
    {
        AssertContainsError(MutateReal(static text => WorkflowTextMutation.Replace(
            text,
            "pwsh -NoProfile -File \"$env:GITHUB_WORKSPACE\\scripts\\Run-YandexCloudLiveSmoke.ps1\" `",
            "dotnet test --filter FullyQualifiedName~YandexObjectStorageLiveSmokeTests `")), "dotnet test");
    }

    [Fact]
    public void Rules_FailMissingRestore()
    {
        AssertContainsError(MutateReal(static text => WorkflowTextMutation.Replace(text, "dotnet restore", "echo skip-restore")), "restore");
    }

    [Fact]
    public void Rules_FailDuplicateSmoke()
    {
        const string extra =
            "\n      - name: Run smoke again\n        if: ${{ inputs.scenario == 'Smoke' || inputs.scenario == 'All' }}\n        run: pwsh -File scripts/Run-YandexCloudLiveSmoke.ps1 -NoRestore -NoBuild -TrxLogFileName cloud-smoke.trx -BlameHangTimeout 2m\n";
        AssertContainsError(MutateReal(text => WorkflowTextMutation.Replace(
            text,
            "      - name: Run acceptance",
            extra + "\n      - name: Run acceptance")), "exactly once");
    }

    [Fact]
    public void Rules_FailBroadArtifactUpload()
    {
        AssertContainsError(MutateReal(static text => WorkflowTextMutation.Replace(
            text,
            ExactArtifactAllowlistBlock,
            "          path: artifacts/ci/cloud-acceptance/**")), "glob");
    }

    [Fact]
    public void Rules_FailCleanupBeforeUpload()
    {
        GitHubWorkflowYaml.WorkflowDocument doc = ParseRepoWorkflow();
        IReadOnlyList<GitHubWorkflowYaml.WorkflowStep> steps = doc.Jobs[0].Steps;
        int upload = IndexOf(steps, static s => s.IsUploadArtifact);
        int cleanup = IndexOf(steps, static s => s.Name.Equals("Cleanup credentials", StringComparison.Ordinal));
        Assert.True(upload >= 0 && cleanup > upload);

        string yaml = WorkflowTextMutation.NormalizeNewlines(File.ReadAllText(Path.Combine(RepoRoot, ".github", "workflows", "cloud-acceptance.yml")));
        AssertContainsError(
            GitHubWorkflowYaml.Parse(WorkflowTextMutation.ApplyAndAssertChanged(
                yaml,
                static text => SwapSteps(text, "Cleanup credentials", "Upload cloud acceptance artifacts"))),
            "after artifact upload");
    }

    [Fact]
    public void Rules_FailSecretsPassedAsCliArguments()
    {
        AssertContainsError(MutateReal(static text => WorkflowTextMutation.Replace(
            text,
            "encrypt-credentials $credJson $credDat",
            "encrypt-credentials $env:S3_ACCESS_KEY_ID $env:S3_SECRET_ACCESS_KEY")), "CLI arguments");
    }

    [Fact]
    public void Rules_FailPushPullRequestAndScheduleTriggers()
    {
        AssertContainsError(MutateReal(static text => WorkflowTextMutation.Replace(
            text,
            "on:\n  workflow_dispatch:",
            "on:\n  push:\n  pull_request:\n  schedule:\n    - cron: '0 3 * * *'\n  workflow_dispatch:")), "must not trigger on push");
    }

    [Fact]
    public void Rules_FailBroadPermissions()
    {
        AssertContainsError(MutateReal(static text => WorkflowTextMutation.Replace(
            text,
            "permissions:\n  contents: read",
            "permissions:\n  contents: write")), "contents: read");
    }

    [Fact]
    public void Rules_FailMissingTimeoutConcurrencyEnvironment()
    {
        AssertContainsError(MutateReal(static text => WorkflowTextMutation.Replace(text, "timeout-minutes: 30\n", "")), "timeout-minutes");
        AssertContainsError(MutateReal(static text => WorkflowTextMutation.Replace(
            text,
            "concurrency:\n  group: cloud-acceptance\n  cancel-in-progress: false\n\n",
            "")), "concurrency");
        AssertContainsError(MutateReal(static text => WorkflowTextMutation.Replace(
            text,
            "    environment: cloud-acceptance\n",
            "")), "environment: cloud-acceptance");
    }

    [Fact]
    public void RealWorkflow_SatisfiesAllStructuralRules()
    {
        IReadOnlyList<string> errors = CloudAcceptanceWorkflowRules.Evaluate(ParseRepoWorkflow());
        Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors));
    }

    [Fact]
    public void RealWorkflow_ExactArtifactAllowlist_IsFiveExplicitFiles()
    {
        GitHubWorkflowYaml.WorkflowDocument doc = ParseRepoWorkflow();
        GitHubWorkflowYaml.WorkflowStep upload = doc.Jobs[0].Steps.Single(static s => s.IsUploadArtifact);
        IReadOnlyList<string> paths = GitHubWorkflowYaml.SplitMultilinePaths(upload.With["path"]);
        string[] expected =
        {
            "artifacts/ci/cloud-acceptance/cloud-smoke.trx",
            "artifacts/ci/cloud-acceptance/cloud-acceptance.trx",
            "artifacts/ci/cloud-acceptance/cloud-smoke.md",
            "artifacts/ci/cloud-acceptance/cloud-acceptance.md",
            "artifacts/ci/cloud-acceptance/category-summary.md"
        };
        Assert.Equal(expected, paths.ToArray());
        Assert.DoesNotContain(paths, static p => p.Contains('*', StringComparison.Ordinal));
        Assert.DoesNotContain(paths, static p => p.Contains("**", StringComparison.Ordinal));
    }

    [Fact]
    public void Rules_FailOmittedSanitizedReport()
    {
        AssertContainsError(MutateReal(static text => WorkflowTextMutation.Replace(
            text,
            "            -SanitizedReportFileName cloud-smoke.md `\n",
            "")), "cloud-smoke.md");
    }

    [Fact]
    public void RealWorkflow_SmokeAndAcceptanceAreMutuallyGated()
    {
        GitHubWorkflowYaml.WorkflowDocument doc = ParseRepoWorkflow();
        GitHubWorkflowYaml.WorkflowStep smoke = doc.Jobs[0].Steps.Single(static s => s.Run.Contains("Run-YandexCloudLiveSmoke.ps1", StringComparison.Ordinal));
        GitHubWorkflowYaml.WorkflowStep acceptance = doc.Jobs[0].Steps.Single(static s => s.Run.Contains("Run-YandexCloudLiveAcceptance.ps1", StringComparison.Ordinal));
        Assert.Contains("'Smoke'", smoke.IfCondition, StringComparison.Ordinal);
        Assert.Contains("'All'", smoke.IfCondition, StringComparison.Ordinal);
        Assert.DoesNotContain("'Acceptance'", smoke.IfCondition, StringComparison.Ordinal);
        Assert.Contains("'Acceptance'", acceptance.IfCondition, StringComparison.Ordinal);
        Assert.Contains("'All'", acceptance.IfCondition, StringComparison.Ordinal);
        Assert.DoesNotContain("'Smoke'", acceptance.IfCondition, StringComparison.Ordinal);
        Assert.DoesNotContain("dotnet test", smoke.Run, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("dotnet test", acceptance.Run, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OrdinaryCiWorkflow_DoesNotReferenceCloudAcceptance()
    {
        GitHubWorkflowYaml.WorkflowDocument ci = GitHubWorkflowYaml.Parse(
            File.ReadAllText(Path.Combine(RepoRoot, ".github", "workflows", "ci.yml")));
        Assert.DoesNotContain("cloud-acceptance", string.Join('|', ci.TriggerKeys), StringComparison.OrdinalIgnoreCase);
        foreach (GitHubWorkflowYaml.WorkflowJob job in ci.Jobs)
        {
            Assert.NotEqual("cloud-acceptance", job.Environment);
            foreach (GitHubWorkflowYaml.WorkflowStep step in job.Steps)
            {
                Assert.DoesNotContain("cloud-acceptance", step.Name, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("cloud-acceptance", step.Run, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public void LocalValidator_ProvesCloudAcceptanceConfigWithoutGitHub()
    {
        CiReportingConfigTests.PwshResult result = CiReportingConfigTests.RunPwshDetailed(
            Path.Combine(RepoRoot, "scripts", "Assert-CloudAcceptanceConfig.ps1"),
            $"-RepoRoot \"{RepoRoot}\"",
            RepoRoot);
        Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
    }

    [Theory]
    [InlineData("on:\n  push:\n  workflow_dispatch:", "must not trigger on push")]
    [InlineData("dotnet test --filter FullyQualifiedName~YandexObjectStorageLiveSmokeTests", "dotnet test")]
    [InlineData("path: artifacts/ci/cloud-acceptance/**", "glob")]
    public void LocalValidator_FailsUnsafeWorkflowCopy(string needle, string expected)
    {
        string workDir = Path.Combine(Path.GetTempPath(), "qn-cloud-val-" + Guid.NewGuid().ToString("N"));
        try
        {
            CopyValidatorInputs(RepoRoot, workDir);
            string workflowPath = Path.Combine(workDir, ".github", "workflows", "cloud-acceptance.yml");
            string yml = File.ReadAllText(workflowPath);
            if (needle.StartsWith("on:", StringComparison.Ordinal))
            {
                yml = WorkflowTextMutation.Replace(yml, "on:\n  workflow_dispatch:", needle);
            }
            else if (needle.StartsWith("path:", StringComparison.Ordinal))
            {
                yml = WorkflowTextMutation.Replace(yml, ExactArtifactAllowlistBlock, "          " + needle);
            }
            else
            {
                yml = WorkflowTextMutation.Replace(
                    yml,
                    "pwsh -NoProfile -File \"$env:GITHUB_WORKSPACE\\scripts\\Run-YandexCloudLiveSmoke.ps1\" `",
                    needle + " `");
            }

            File.WriteAllText(workflowPath, yml);
            CiReportingConfigTests.PwshResult result = CiReportingConfigTests.RunPwshDetailed(
                Path.Combine(RepoRoot, "scripts", "Assert-CloudAcceptanceConfig.ps1"),
                $"-RepoRoot \"{workDir}\"",
                workDir);
            Assert.NotEqual(0, result.ExitCode);
            string output = result.Stdout + result.Stderr;
            Assert.Contains(expected, output, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { Directory.Delete(workDir, recursive: true); } catch { }
        }
    }

    private const string ExactArtifactAllowlistBlock = """
                      path: |
                        artifacts/ci/cloud-acceptance/cloud-smoke.trx
                        artifacts/ci/cloud-acceptance/cloud-acceptance.trx
                        artifacts/ci/cloud-acceptance/cloud-smoke.md
                        artifacts/ci/cloud-acceptance/cloud-acceptance.md
                        artifacts/ci/cloud-acceptance/category-summary.md
            """;

    private static GitHubWorkflowYaml.WorkflowDocument MutateReal(Func<string, string> mutate)
    {
        string original = File.ReadAllText(Path.Combine(RepoRoot, ".github", "workflows", "cloud-acceptance.yml"));
        return GitHubWorkflowYaml.Parse(WorkflowTextMutation.ApplyAndAssertChanged(original, mutate));
    }

    private static void AssertContainsError(GitHubWorkflowYaml.WorkflowDocument doc, string expected)
    {
        IReadOnlyList<string> errors = CloudAcceptanceWorkflowRules.Evaluate(doc);
        Assert.True(errors.Count > 0, "expected structural failure");
        Assert.Contains(errors, e => e.Contains(expected, StringComparison.OrdinalIgnoreCase));
    }

    private static int IndexOf(IReadOnlyList<GitHubWorkflowYaml.WorkflowStep> steps, Func<GitHubWorkflowYaml.WorkflowStep, bool> predicate)
    {
        for (int i = 0; i < steps.Count; i++)
        {
            if (predicate(steps[i]))
                return i;
        }

        return -1;
    }

    private static string SwapSteps(string yaml, string firstName, string secondName)
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
            Path.Combine(".github", "workflows", "cloud-acceptance.yml"),
            Path.Combine(".github", "workflows", "ci.yml"),
            ".gitignore",
            Path.Combine("QuickNotes.Tools", "EncryptCredentialsTool.cs"),
            Path.Combine("scripts", "CloudRunProtocol.ps1"),
            Path.Combine("scripts", "Run-YandexCloudLiveSmoke.ps1"),
            Path.Combine("scripts", "Run-YandexCloudLiveAcceptance.ps1")
        };
        foreach (string relative in files)
        {
            string target = Path.Combine(dest, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(Path.Combine(repo, relative), target);
        }
    }
}
