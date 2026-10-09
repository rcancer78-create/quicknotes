using System;
using System.IO;

namespace QuickNotes.Tools.Performance;

public static class PerformanceBenchmarkTool
{
    public static readonly string UsageText =
        "Usage: QuickNotes.Tools.exe benchmark [options]" + Environment.NewLine +
        "Options:" + Environment.NewLine +
        "  --smoke              Run fast CI smoke mode (50 notes, 1 MB attachments)" + Environment.NewLine +
        "  --seed <int>         Random seed for deterministic generation (default 1337)" + Environment.NewLine +
        "  --samples <int>      Number of measurement samples (default 5, smoke: 2)" + Environment.NewLine +
        "  --output <dir>       Custom isolated profile directory" + Environment.NewLine +
        "  --keep-dataset       Keep generated profile on disk (do not delete)" + Environment.NewLine +
        "  --report <path>      Save report to markdown file";

    public static int Run(string[] args)
    {
        bool isSmoke = false;
        int seed = 1337;
        int? sampleCount = null;
        string? customOutput = null;
        bool keepDataset = false;
        string? customReportPath = null;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (string.Equals(arg, "--smoke", StringComparison.OrdinalIgnoreCase))
            {
                isSmoke = true;
            }
            else if (string.Equals(arg, "--seed", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                if (int.TryParse(args[++i], out int parsedSeed)) seed = parsedSeed;
            }
            else if (string.Equals(arg, "--samples", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                if (int.TryParse(args[++i], out int parsedSamples)) sampleCount = parsedSamples;
            }
            else if (string.Equals(arg, "--output", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                customOutput = args[++i];
            }
            else if (string.Equals(arg, "--keep-dataset", StringComparison.OrdinalIgnoreCase))
            {
                keepDataset = true;
            }
            else if (string.Equals(arg, "--report", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                customReportPath = args[++i];
            }
        }

        string tempProfile = customOutput ?? Path.Combine(Path.GetTempPath(), "qn_perf_isolated_" + Guid.NewGuid().ToString("N"));

        try
        {
            Console.WriteLine("=========================================================");
            Console.WriteLine("QuickNotes ROADMAP §6.5 Performance Baseline Benchmark");
            Console.WriteLine("=========================================================");
            Console.WriteLine($"Mode: {(isSmoke ? "CI Smoke (50 notes)" : "Full Baseline (10,000 notes + ~500 MB attachments)")}");
            Console.WriteLine($"Seed: {seed}");
            Console.WriteLine($"Profile Directory: {BenchmarkEnvironmentCollector.SanitizePath(tempProfile)}");
            Console.WriteLine();

            Console.WriteLine("1. Generating deterministic synthetic dataset...");
            var dsOptions = isSmoke
                ? SyntheticDatasetOptions.CreateSmoke(tempProfile, seed)
                : new SyntheticDatasetOptions
                {
                    Seed = seed,
                    NoteCount = 10_000,
                    AttachmentTotalBytes = 500L * 1024 * 1024,
                    OutputDirectory = tempProfile
                };

            var dataset = SyntheticDatasetGenerator.Generate(dsOptions);
            Console.WriteLine($"Dataset generated in {dataset.GenerationElapsedMs / 1000.0:F2}s:");
            Console.WriteLine($"  Notes: {dataset.NoteCount:N0}");
            Console.WriteLine($"  Tags: {dataset.TagCount:N0}");
            Console.WriteLine($"  Revisions: {dataset.RevisionCount:N0}");
            Console.WriteLine($"  Attachments: {dataset.AttachmentCount:N0} ({dataset.AttachmentTotalBytes / (1024.0 * 1024.0):F1} MB)");
            Console.WriteLine($"  SQLite Size: {dataset.DatabaseSizeBytes / (1024.0 * 1024.0):F2} MB");
            Console.WriteLine();

            Console.WriteLine("2. Executing benchmark measurements...");
            int effectiveSamples = sampleCount ?? (isSmoke ? 2 : 5);
            var runOptions = new BenchmarkRunOptions
            {
                Dataset = dataset,
                WarmupCount = 1,
                SampleCount = effectiveSamples
            };

            var results = PerformanceBenchmarkRunner.Run(runOptions, msg => Console.WriteLine(msg));

            Console.WriteLine();
            Console.WriteLine("=========================================================");
            Console.WriteLine("RESULTS SUMMARY vs ROADMAP TARGETS:");
            Console.WriteLine("=========================================================");
            PrintMetricSummary(results.ColdStartMetric);
            PrintMetricSummary(results.SearchOverallMetric);
            PrintMetricSummary(results.EditorOpenMetric);
            PrintMetricSummary(results.WorkingSetMetric);
            PrintMetricSummary(results.ScrollFpsMetric);

            // Generate and save report
            string reportPath = customReportPath ?? ResolveDefaultReportPath();
            PerformanceReportGenerator.SaveReport(results, reportPath);
            Console.WriteLine();
            Console.WriteLine($"Report written to: {reportPath}");

            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Benchmark execution failed: {ex.GetType().Name}: {ex.Message}");
            Console.Error.WriteLine(ex.StackTrace);
            return 1;
        }
        finally
        {
            if (!keepDataset && Directory.Exists(tempProfile))
            {
                try
                {
                    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                    Directory.Delete(tempProfile, recursive: true);
                }
                catch
                {
                    // Ignore cleanup failure in temp
                }
            }
        }
    }

    private static void PrintMetricSummary(BenchmarkMetricResult m)
    {
        string status = (!m.IsAvailable || m.Status == BenchmarkMetricStatus.Unverified || m.Summary.Count == 0)
            ? "[UNVERIFIED]"
            : (m.Status == BenchmarkMetricStatus.Pass ? "[PASS]" : "[FAIL]");
        string val = (m.IsAvailable && m.Summary.Count > 0 && m.Status != BenchmarkMetricStatus.Unverified)
            ? $"median={m.Summary.Median:F1}{m.Unit}, p95={m.Summary.P95:F1}{m.Unit}"
            : "N/A";
        Console.WriteLine($"{status,-12} {m.Name,-45} {val,-25} (Target: {m.TargetDescription})");
    }

    private static string ResolveDefaultReportPath()
    {
        string cwd = Environment.CurrentDirectory;
        string candidate = Path.Combine(cwd, "docs", "performance", "baseline-report-2026-09-11.md");
        if (Directory.Exists(Path.Combine(cwd, "docs")))
        {
            return candidate;
        }

        string baseDir = AppContext.BaseDirectory;
        string rootCandidate = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "docs", "performance", "baseline-report-2026-09-11.md"));
        return rootCandidate;
    }
}
