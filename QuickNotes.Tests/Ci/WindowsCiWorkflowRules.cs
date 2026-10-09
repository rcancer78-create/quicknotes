using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace QuickNotes.Tests.Ci;

internal static class WindowsCiWorkflowRules
{
    private static readonly Regex FormatWhitespace = new(
        @"\bdotnet\s+format\s+whitespace\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex FormatAny = new(
        @"\bdotnet\s+format\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex FormatStyleOrAnalyzers = new(
        @"\bdotnet\s+format\s+(style|analyzers)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex VerifyNoChanges = new(
        @"--verify-no-changes\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex NoRestore = new(
        @"--no-restore\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex LiveCloudEnv = new(
        @"QUICKNOTES_LIVE_S3",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Secrets = new(
        @"\$\{\{\s*secrets\.",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex TestFilter = new(
        @"--filter\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static IReadOnlyList<string> Evaluate(GitHubWorkflowYaml.WorkflowDocument doc)
    {
        var errors = new List<string>();

        if (doc.Jobs.Count == 0)
            errors.Add("Windows CI workflow must declare a job.");

        foreach (GitHubWorkflowYaml.WorkflowJob job in doc.Jobs)
            EvaluateJob(job, errors);

        return errors;
    }

    private static void EvaluateJob(GitHubWorkflowYaml.WorkflowJob job, List<string> errors)
    {
        if (GitHubWorkflowYaml.ParsePositiveInt(job.TimeoutMinutes) is null)
            errors.Add("Windows CI job must set timeout-minutes.");

        IReadOnlyList<GitHubWorkflowYaml.WorkflowStep> steps = job.Steps;
        if (steps.Count == 0)
        {
            errors.Add("Windows CI job must declare steps.");
            return;
        }

        int restore = IndexOf(steps, static s => HasRunToken(s.Run, "dotnet restore"));
        int format = IndexOf(steps, IsWhitespaceFormatStep);
        int build = IndexOf(steps, static s => HasRunToken(s.Run, "dotnet build"));
        int test = IndexOf(steps, static s => HasRunToken(s.Run, "dotnet test"));

        int formatAnyCount = steps.Count(static s => FormatAny.IsMatch(s.Run));
        int whitespaceCount = steps.Count(IsWhitespaceFormatStep);
        int styleOrAnalyzerCount = steps.Count(static s => FormatStyleOrAnalyzers.IsMatch(s.Run));

        if (restore < 0)
            errors.Add("Windows CI must restore before --no-restore format/build/test.");
        if (whitespaceCount != 1)
            errors.Add("Windows CI must run dotnet format whitespace exactly once.");
        if (formatAnyCount != whitespaceCount)
            errors.Add("Windows CI format gate must be whitespace-only (no bare dotnet format, style, or analyzers).");
        if (styleOrAnalyzerCount > 0)
            errors.Add("Windows CI must not run dotnet format style or analyzers.");
        if (build < 0)
            errors.Add("Windows CI must have a build step.");
        if (test < 0)
            errors.Add("Windows CI must run dotnet test.");

        if (format >= 0)
        {
            GitHubWorkflowYaml.WorkflowStep step = steps[format];
            if (!VerifyNoChanges.IsMatch(step.Run))
                errors.Add("Whitespace format gate must pass --verify-no-changes.");
            if (!NoRestore.IsMatch(step.Run))
                errors.Add("Whitespace format gate must pass --no-restore.");
            if (!step.Run.Contains("QuickNotes.sln", StringComparison.OrdinalIgnoreCase))
                errors.Add("Whitespace format gate must target QuickNotes.sln.");
            if (string.Equals(step.ContinueOnError, "true", StringComparison.OrdinalIgnoreCase))
                errors.Add("Format gate must not continue-on-error.");
        }

        if (restore >= 0 && format >= 0 && restore > format)
            errors.Add("Format verify must run after restore.");
        if (format >= 0 && build >= 0 && format > build)
            errors.Add("Format verify must run before build.");
        if (build >= 0 && test >= 0 && build > test)
            errors.Add("Build must run before test.");

        if (build >= 0 && !HasRunToken(steps[build].Run, "--no-restore"))
            errors.Add("Build must use --no-restore after restore.");
        if (test >= 0 && TestFilter.IsMatch(steps[test].Run))
            errors.Add("Authoritative test run must not use --filter.");

        foreach (GitHubWorkflowYaml.WorkflowStep step in steps)
        {
            if (LiveCloudEnv.IsMatch(step.Run) || step.Env.Keys.Any(static k => LiveCloudEnv.IsMatch(k)))
                errors.Add("Ordinary CI must not set LiveCloud environment variables.");
            if (Secrets.IsMatch(step.Run) || step.Env.Values.Any(static v => Secrets.IsMatch(v)))
                errors.Add("Ordinary CI must not reference GitHub secrets.");
        }
    }

    internal static bool IsWhitespaceFormatStep(GitHubWorkflowYaml.WorkflowStep step) =>
        FormatWhitespace.IsMatch(step.Run);

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
}
