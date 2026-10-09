using System;
using System.Globalization;
using System.IO;

namespace QuickNotes.Tools.Performance;

public sealed class ColdStartR2rCliOptions
{
    public string RepoRoot { get; init; } = string.Empty;
    public string BaselineExe { get; init; } = string.Empty;
    public string ReadyToRunExe { get; init; } = string.Empty;
    public int WarmupEach { get; init; } = 2;
    public int MeasuredEach { get; init; } = 10;
    public int TimeoutMs { get; init; } = 60_000;
    public bool Smoke { get; init; }
    public int Seed { get; init; } = 1337;
    public string? DatasetDirectory { get; init; }
    public bool KeepDataset { get; init; }
    public string? ReportPath { get; init; }
    public string BaselinePublishCommand { get; init; } = string.Empty;
    public string R2rPublishCommand { get; init; } = string.Empty;
    public string? ParseError { get; init; }

    public bool HasParseError => !string.IsNullOrEmpty(ParseError);
}

public static class PerformanceColdStartR2rTool
{
    public static readonly string UsageText =
        "Usage: QuickNotes.Tools.exe cold-start-r2r --repo-root <dir> --baseline-exe <exe> --r2r-exe <exe> [options]"
        + Environment.NewLine
        + "Orchestrates isolated --perf-startup launches of two already-published trees. Does not copy publish output into bin/obj."
        + Environment.NewLine
        + "Options:" + Environment.NewLine
        + "  --warmup <int>       Warmup launches per variant (default 2)" + Environment.NewLine
        + "  --samples <int>      Measured launches per variant (default 10)" + Environment.NewLine
        + "  --timeout-ms <int>   Per-process timeout (default 60000)" + Environment.NewLine
        + "  --smoke              Small dataset for tests" + Environment.NewLine
        + "  --seed <int>         Dataset seed (default 1337)" + Environment.NewLine
        + "  --dataset <dir>      Existing isolated temp dataset directory" + Environment.NewLine
        + "  --keep-dataset       Do not delete the generated dataset" + Environment.NewLine
        + "  --report <path>      Markdown report path" + Environment.NewLine
        + "  --baseline-publish-command <text>" + Environment.NewLine
        + "  --r2r-publish-command <text>";

    public static ColdStartR2rCliOptions Parse(string[] args)
    {
        string repoRoot = string.Empty;
        string baselineExe = string.Empty;
        string r2rExe = string.Empty;
        int warmup = 2;
        int samples = 10;
        int timeoutMs = 60_000;
        bool smoke = false;
        int seed = 1337;
        string? dataset = null;
        bool keep = false;
        string? report = null;
        string baselineCmd = string.Empty;
        string r2rCmd = string.Empty;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (string.Equals(arg, "--repo-root", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                repoRoot = args[++i];
            }
            else if (string.Equals(arg, "--baseline-exe", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                baselineExe = args[++i];
            }
            else if (string.Equals(arg, "--r2r-exe", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                r2rExe = args[++i];
            }
            else if (string.Equals(arg, "--warmup", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                if (!int.TryParse(args[++i], NumberStyles.Integer, CultureInfo.InvariantCulture, out warmup) || warmup < 0)
                {
                    return Error("Invalid --warmup.");
                }
            }
            else if (string.Equals(arg, "--samples", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                if (!int.TryParse(args[++i], NumberStyles.Integer, CultureInfo.InvariantCulture, out samples) || samples < 0)
                {
                    return Error("Invalid --samples.");
                }
            }
            else if (string.Equals(arg, "--timeout-ms", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                if (!int.TryParse(args[++i], NumberStyles.Integer, CultureInfo.InvariantCulture, out timeoutMs) || timeoutMs <= 0)
                {
                    return Error("Invalid --timeout-ms.");
                }
            }
            else if (string.Equals(arg, "--smoke", StringComparison.OrdinalIgnoreCase))
            {
                smoke = true;
            }
            else if (string.Equals(arg, "--seed", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                if (!int.TryParse(args[++i], NumberStyles.Integer, CultureInfo.InvariantCulture, out seed))
                {
                    return Error("Invalid --seed.");
                }
            }
            else if (string.Equals(arg, "--dataset", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                dataset = args[++i];
            }
            else if (string.Equals(arg, "--keep-dataset", StringComparison.OrdinalIgnoreCase))
            {
                keep = true;
            }
            else if (string.Equals(arg, "--report", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                report = args[++i];
            }
            else if (string.Equals(arg, "--baseline-publish-command", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                baselineCmd = args[++i];
            }
            else if (string.Equals(arg, "--r2r-publish-command", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                r2rCmd = args[++i];
            }
            else if (string.Equals(arg, "--help", StringComparison.OrdinalIgnoreCase) || arg == "-h")
            {
                return Error("help");
            }
            else
            {
                return Error("Unknown argument: " + arg);
            }
        }

        if (string.IsNullOrWhiteSpace(repoRoot) || string.IsNullOrWhiteSpace(baselineExe) || string.IsNullOrWhiteSpace(r2rExe))
        {
            return Error("Required: --repo-root, --baseline-exe, --r2r-exe.");
        }

        return new ColdStartR2rCliOptions
        {
            RepoRoot = repoRoot,
            BaselineExe = baselineExe,
            ReadyToRunExe = r2rExe,
            WarmupEach = warmup,
            MeasuredEach = samples,
            TimeoutMs = timeoutMs,
            Smoke = smoke,
            Seed = seed,
            DatasetDirectory = dataset,
            KeepDataset = keep,
            ReportPath = report,
            BaselinePublishCommand = baselineCmd,
            R2rPublishCommand = r2rCmd
        };
    }

    public static int Run(string[] args)
    {
        ColdStartR2rCliOptions parsed = Parse(args);
        if (parsed.HasParseError)
        {
            Console.Error.WriteLine(parsed.ParseError == "help" ? UsageText : parsed.ParseError);
            Console.Error.WriteLine(UsageText);
            return 1;
        }

        try
        {
            var result = ColdStartR2rBenchmark.Run(new ColdStartR2rBenchmarkOptions
            {
                RepoRoot = parsed.RepoRoot,
                BaselineExe = parsed.BaselineExe,
                ReadyToRunExe = parsed.ReadyToRunExe,
                WarmupEach = parsed.WarmupEach,
                MeasuredEach = parsed.MeasuredEach,
                TimeoutMs = parsed.TimeoutMs,
                Smoke = parsed.Smoke,
                Seed = parsed.Seed,
                DatasetDirectory = parsed.DatasetDirectory,
                KeepDataset = parsed.KeepDataset,
                BaselinePublishCommand = parsed.BaselinePublishCommand,
                R2rPublishCommand = parsed.R2rPublishCommand,
                Log = msg => Console.WriteLine(msg)
            });

            string reportPath = parsed.ReportPath
                ?? Path.Combine(parsed.RepoRoot, "docs", "performance", "cold-start-r2r-report-" + DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".md");
            ColdStartR2rReportGenerator.SaveReport(result, reportPath);
            Console.WriteLine("Report: " + reportPath);
            Console.WriteLine("Baseline p95=" + result.Baseline.Summary.P95.ToString("F1", CultureInfo.InvariantCulture)
                + " ms; R2R p95=" + result.ReadyToRun.Summary.P95.ToString("F1", CultureInfo.InvariantCulture)
                + " ms; gate=" + (result.ReadyToRunMeetsTarget ? "PASS" : "OPEN"));
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.GetType().Name + ": " + ex.Message);
            return 1;
        }
    }

    private static ColdStartR2rCliOptions Error(string message)
    {
        return new ColdStartR2rCliOptions { ParseError = message };
    }
}
