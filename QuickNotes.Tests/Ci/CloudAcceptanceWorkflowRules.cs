using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace QuickNotes.Tests.Ci;

internal static class CloudAcceptanceWorkflowRules
{
    private static readonly Regex DotnetTest = new(@"\bdotnet\s+test\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex FilterArg = new(@"--filter\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex SecretExpression = new(@"\$\{\{\s*secrets\.", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex SecretAsProcessArg = new(
        @"(?im)^[^\n]*(?:dotnet\s+|&\s+|\.exe\b)[^\n]*\$env:S3_(?:ACCESS_KEY_ID|SECRET_ACCESS_KEY)",
        RegexOptions.CultureInvariant);
    private static readonly Regex PasswordArg = new(@"--password\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex RecursiveGlob = new(@"\*\*", RegexOptions.CultureInvariant);

    public static IReadOnlyList<string> Evaluate(GitHubWorkflowYaml.WorkflowDocument doc)
    {
        var errors = new List<string>();

        if (!doc.TriggerKeys.Contains("workflow_dispatch", StringComparer.Ordinal))
            errors.Add("Cloud workflow must declare workflow_dispatch as an on: key.");
        foreach (string forbidden in new[] { "push", "pull_request", "schedule", "workflow_call" })
        {
            if (doc.TriggerKeys.Contains(forbidden, StringComparer.Ordinal))
                errors.Add($"Cloud workflow must not trigger on {forbidden}.");
        }

        if (!doc.HasPermissionsBlock)
            errors.Add("Cloud workflow must declare permissions.");
        else if (!string.IsNullOrEmpty(doc.PermissionsScalar)
                 || doc.Permissions.Count != 1
                 || !doc.Permissions.TryGetValue("contents", out string? contents)
                 || !contents.Equals("read", StringComparison.Ordinal))
        {
            errors.Add("Cloud workflow permissions must be exactly contents: read.");
        }

        if (doc.Concurrency.Count == 0)
            errors.Add("Cloud workflow must declare concurrency.");
        else if (!doc.Concurrency.ContainsKey("cancel-in-progress"))
            errors.Add("Cloud workflow must set cancel-in-progress.");

        if (doc.Jobs.Count == 0)
            errors.Add("Cloud workflow must declare a job.");

        foreach (GitHubWorkflowYaml.WorkflowJob job in doc.Jobs)
            EvaluateJob(job, errors);

        return errors;
    }

    private static void EvaluateJob(GitHubWorkflowYaml.WorkflowJob job, List<string> errors)
    {
        if (GitHubWorkflowYaml.ParsePositiveInt(job.TimeoutMinutes) is null)
            errors.Add("Cloud workflow job must set timeout-minutes.");
        if (!string.Equals(job.Environment, "cloud-acceptance", StringComparison.Ordinal))
            errors.Add("Cloud workflow job must use environment: cloud-acceptance.");

        IReadOnlyList<GitHubWorkflowYaml.WorkflowStep> steps = job.Steps;
        if (steps.Count == 0)
        {
            errors.Add("Cloud workflow job must declare steps.");
            return;
        }

        int restore = IndexOf(steps, static s => HasRunToken(s.Run, "dotnet restore"));
        int build = IndexOf(steps, static s => HasRunToken(s.Run, "dotnet build"));
        int encrypt = IndexOf(steps, static s => HasRunToken(s.Run, "encrypt-credentials"));
        int smoke = IndexOf(steps, static s => s.Run.Contains("Run-YandexCloudLiveSmoke.ps1", StringComparison.OrdinalIgnoreCase));
        int acceptance = IndexOf(steps, static s => s.Run.Contains("Run-YandexCloudLiveAcceptance.ps1", StringComparison.OrdinalIgnoreCase));
        int summary = IndexOf(steps, static s => s.Run.Contains("Write-TestCategorySummary.ps1", StringComparison.OrdinalIgnoreCase));
        int upload = IndexOf(steps, static s => s.IsUploadArtifact);
        int cleanup = IndexOf(steps, static s => s.Name.Equals("Cleanup credentials", StringComparison.OrdinalIgnoreCase)
                                                 || (s.Name.Contains("Cleanup", StringComparison.OrdinalIgnoreCase)
                                                     && s.Run.Contains("qn-cloud-creds", StringComparison.OrdinalIgnoreCase)));

        if (restore < 0)
            errors.Add("Cloud workflow must have a restore step before --no-restore build/test.");
        if (build < 0)
            errors.Add("Cloud workflow must have a build step.");
        if (restore >= 0 && build >= 0 && restore > build)
            errors.Add("Restore must run before build.");
        if (build >= 0 && !HasRunToken(steps[build].Run, "--no-restore"))
            errors.Add("Build must use --no-restore after a clean restore.");

        if (encrypt < 0)
            errors.Add("Cloud workflow must invoke encrypt-credentials.");

        int smokeCount = steps.Count(static s => s.Run.Contains("Run-YandexCloudLiveSmoke.ps1", StringComparison.OrdinalIgnoreCase));
        int acceptanceCount = steps.Count(static s => s.Run.Contains("Run-YandexCloudLiveAcceptance.ps1", StringComparison.OrdinalIgnoreCase));
        if (smokeCount != 1)
            errors.Add("Cloud workflow must invoke Run-YandexCloudLiveSmoke.ps1 exactly once (Smoke must not run twice).");
        if (acceptanceCount != 1)
            errors.Add("Cloud workflow must invoke Run-YandexCloudLiveAcceptance.ps1 exactly once.");

        if (smoke >= 0)
        {
            GitHubWorkflowYaml.WorkflowStep step = steps[smoke];
            if (!CoversScenario(step.IfCondition, "Smoke") || !CoversScenario(step.IfCondition, "All") || CoversOnlyAcceptance(step.IfCondition))
                errors.Add("Smoke runner step must be gated to Smoke or All.");
            if (!HasRunToken(step.Run, "-NoRestore") || !HasRunToken(step.Run, "-NoBuild"))
                errors.Add("Smoke runner invocation must pass -NoRestore and -NoBuild.");
            if (!HasRunToken(step.Run, "cloud-smoke.trx"))
                errors.Add("Smoke runner must use deterministic TRX name cloud-smoke.trx.");
            if (!HasRunToken(step.Run, "-SanitizedReportFileName") || !HasRunToken(step.Run, "cloud-smoke.md"))
                errors.Add("Smoke runner must write deterministic sanitized report cloud-smoke.md.");
            if (!HasRunToken(step.Run, "-BlameHangTimeout"))
                errors.Add("Smoke runner must pass -BlameHangTimeout.");
            if (HasRunToken(step.Run, "ProtocolSelfTest"))
                errors.Add("Cloud workflow must not pass ProtocolSelfTest.");
        }

        if (acceptance >= 0)
        {
            GitHubWorkflowYaml.WorkflowStep step = steps[acceptance];
            if (!CoversScenario(step.IfCondition, "Acceptance") || !CoversScenario(step.IfCondition, "All") || CoversOnlySmoke(step.IfCondition))
                errors.Add("Acceptance runner step must be gated to Acceptance or All.");
            if (!HasRunToken(step.Run, "-NoRestore") || !HasRunToken(step.Run, "-NoBuild"))
                errors.Add("Acceptance runner invocation must pass -NoRestore and -NoBuild.");
            if (!HasRunToken(step.Run, "cloud-acceptance.trx"))
                errors.Add("Acceptance runner must use deterministic TRX name cloud-acceptance.trx.");
            if (!HasRunToken(step.Run, "-SanitizedReportFileName") || !HasRunToken(step.Run, "cloud-acceptance.md"))
                errors.Add("Acceptance runner must write deterministic sanitized report cloud-acceptance.md.");
            if (HasRunToken(step.Run, "ProtocolSelfTest"))
                errors.Add("Cloud workflow must not pass ProtocolSelfTest.");
        }

        if (smoke >= 0 && acceptance >= 0 && smoke > acceptance)
            errors.Add("Smoke step must precede Acceptance so All does not reverse order.");

        foreach (GitHubWorkflowYaml.WorkflowStep step in steps)
        {
            if (string.Equals(step.ContinueOnError, "true", StringComparison.OrdinalIgnoreCase))
                errors.Add($"Step '{step.Name}' must not continue-on-error.");

            if (DotnetTest.IsMatch(step.Run) || FilterArg.IsMatch(step.Run))
                errors.Add($"Step '{step.Name}' must not run scenario dotnet test or --filter in YAML.");

            if (SecretExpression.IsMatch(step.Run) || PasswordArg.IsMatch(step.Run) || SecretAsProcessArg.IsMatch(step.Run))
                errors.Add($"Step '{step.Name}' must not pass secrets as CLI arguments.");

            if (HasRunToken(step.Run, "echo") && (step.Run.Contains("S3_ACCESS_KEY_ID", StringComparison.Ordinal) || step.Run.Contains("S3_SECRET_ACCESS_KEY", StringComparison.Ordinal)))
                errors.Add($"Step '{step.Name}' must not echo secrets.");
        }

        bool requiredEnv = steps.Any(static s =>
            s.Env.ContainsKey("S3_ACCESS_KEY_ID")
            && s.Env.ContainsKey("S3_SECRET_ACCESS_KEY"));
        bool requiredVars = steps.Any(static s =>
            s.Env.ContainsKey("S3_ENDPOINT")
            && s.Env.ContainsKey("S3_BUCKET"));
        if (!requiredEnv)
            errors.Add("Cloud workflow must map S3_ACCESS_KEY_ID and S3_SECRET_ACCESS_KEY through step env.");
        if (!requiredVars)
            errors.Add("Cloud workflow must map S3_ENDPOINT and S3_BUCKET through step env.");

        if (upload < 0)
            errors.Add("Cloud workflow must upload artifacts.");
        else
        {
            GitHubWorkflowYaml.WorkflowStep step = steps[upload];
            if (!GitHubWorkflowYaml.IsAlwaysIf(step.IfCondition))
                errors.Add("Artifact upload must use if: always().");
            if (!step.With.TryGetValue("retention-days", out string? days)
                || GitHubWorkflowYaml.ParsePositiveInt(days) is not int retention
                || retention < 1
                || retention > 14)
            {
                errors.Add("Artifact retention-days must be a short window (1-14).");
            }

            if (!step.With.TryGetValue("path", out string? path))
                errors.Add("Artifact upload must declare path.");
            else
            {
                IReadOnlyList<string> paths = GitHubWorkflowYaml.SplitMultilinePaths(path);
                if (paths.Count == 0)
                    errors.Add("Artifact upload path allowlist is empty.");
                var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "artifacts/ci/cloud-acceptance/cloud-smoke.trx",
                    "artifacts/ci/cloud-acceptance/cloud-acceptance.trx",
                    "artifacts/ci/cloud-acceptance/cloud-smoke.md",
                    "artifacts/ci/cloud-acceptance/cloud-acceptance.md",
                    "artifacts/ci/cloud-acceptance/category-summary.md"
                };
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string item in paths)
                {
                    string normalized = item.Replace('\\', '/');
                    if (RecursiveGlob.IsMatch(normalized) || normalized.Contains('*', StringComparison.Ordinal))
                        errors.Add("Artifact upload must not use a recursive or wildcard directory glob.");
                    if (!allowed.Contains(normalized))
                        errors.Add("Artifact upload path is outside the TRX/sanitized-report/category-summary allowlist: " + item);
                    seen.Add(normalized);
                    if (normalized.Contains("RUNNER_TEMP", StringComparison.OrdinalIgnoreCase)
                        || normalized.Contains("qn-cloud-creds", StringComparison.OrdinalIgnoreCase))
                    {
                        errors.Add("Credentials path must not be uploaded.");
                    }
                }

                foreach (string required in allowed)
                {
                    if (!seen.Contains(required))
                        errors.Add("Artifact upload allowlist must include " + required);
                }
            }
        }

        if (cleanup < 0)
            errors.Add("Cloud workflow must have a credentials cleanup step.");
        else if (!GitHubWorkflowYaml.IsAlwaysIf(steps[cleanup].IfCondition))
            errors.Add("Cleanup must use if: always().");

        if (summary >= 0 && !GitHubWorkflowYaml.IsAlwaysIf(steps[summary].IfCondition))
            errors.Add("Category summary must use if: always().");

        if (upload >= 0 && cleanup >= 0 && cleanup < upload)
            errors.Add("Cleanup must run after artifact upload, not before.");
        if (encrypt >= 0 && upload >= 0 && encrypt > upload)
            errors.Add("Credentials encryption must happen before upload.");
        if (restore >= 0 && smoke >= 0 && restore > smoke)
            errors.Add("Restore must run before smoke.");
        if (build >= 0 && smoke >= 0 && build > smoke)
            errors.Add("Build must run before smoke.");
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

    private static bool HasRunToken(string run, string token) =>
        run.Contains(token, StringComparison.OrdinalIgnoreCase);

    private static bool CoversScenario(string? condition, string scenario)
    {
        if (string.IsNullOrWhiteSpace(condition))
            return false;
        return condition.Contains("'" + scenario + "'", StringComparison.Ordinal)
               || condition.Contains("\"" + scenario + "\"", StringComparison.Ordinal);
    }

    private static bool CoversOnlySmoke(string? condition) =>
        CoversScenario(condition, "Smoke") && !CoversScenario(condition, "All") && !CoversScenario(condition, "Acceptance");

    private static bool CoversOnlyAcceptance(string? condition) =>
        CoversScenario(condition, "Acceptance") && !CoversScenario(condition, "All") && !CoversScenario(condition, "Smoke");
}
