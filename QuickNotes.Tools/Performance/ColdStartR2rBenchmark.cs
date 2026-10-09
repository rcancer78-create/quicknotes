using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;
using QuickNotes.App.Data;

namespace QuickNotes.Tools.Performance;

public sealed class ColdStartVariantResult
{
    public string Name { get; init; } = string.Empty;
    public string ExePath { get; init; } = string.Empty;
    public long PublishBytes { get; init; }
    public int PublishFileCount { get; init; }
    public MetricSampleSummary Summary { get; init; } = new();
    public IReadOnlyList<double> WarmupSamples { get; init; } = Array.Empty<double>();
}

public sealed class ColdStartR2rBenchmarkResult
{
    public BenchmarkEnvironmentInfo Environment { get; init; } = null!;
    public SyntheticDatasetSummary Dataset { get; init; } = null!;
    public DateTimeOffset ExecutedAtUtc { get; init; }
    public int WarmupEach { get; init; }
    public int MeasuredEach { get; init; }
    public int TimeoutMs { get; init; }
    public string Schedule { get; init; } = "ABBA";
    public string BaselinePublishCommand { get; init; } = string.Empty;
    public string R2rPublishCommand { get; init; } = string.Empty;
    public ColdStartVariantResult Baseline { get; init; } = null!;
    public ColdStartVariantResult ReadyToRun { get; init; } = null!;
    public string LiveProfileHashBefore { get; init; } = string.Empty;
    public string LiveProfileHashAfter { get; init; } = string.Empty;
    public bool LiveProfileUnchanged { get; init; }
    public bool ReadyToRunMeetsTarget => ReadyToRun.Summary.Count >= 10 && ReadyToRun.Summary.P95 <= 1500.0;
}

public sealed class ColdStartR2rBenchmarkOptions
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
    public string BaselinePublishCommand { get; init; } = string.Empty;
    public string R2rPublishCommand { get; init; } = string.Empty;
    public Action<string>? Log { get; init; }
}

public static class ColdStartR2rBenchmark
{
    public static ColdStartR2rBenchmarkResult Run(ColdStartR2rBenchmarkOptions options)
    {
        string repoRoot = Path.GetFullPath(options.RepoRoot);
        string baselineExe = ReadyToRunPublishPathGuard.NormalizeAndValidatePublishedExe(options.BaselineExe, repoRoot);
        string r2rExe = ReadyToRunPublishPathGuard.NormalizeAndValidatePublishedExe(options.ReadyToRunExe, repoRoot);

        var liveBefore = LiveProfileFingerprint.Capture();
        string datasetDir = options.DatasetDirectory
            ?? Path.Combine(Path.GetTempPath(), "qn_r2r_dataset_" + Guid.NewGuid().ToString("N"));
        QuickNotesDbContext.ValidateIsolatedProfilePath(datasetDir);
        ReadyToRunPublishPathGuard.ValidateIsolatedLaunchProfile(datasetDir);

        try
        {
            options.Log?.Invoke("Generating isolated synthetic dataset...");
            SyntheticDatasetOptions dsOptions = options.Smoke
                ? SyntheticDatasetOptions.CreateSmoke(datasetDir, options.Seed)
                : new SyntheticDatasetOptions
                {
                    Seed = options.Seed,
                    NoteCount = 10_000,
                    AttachmentTotalBytes = 500L * 1024 * 1024,
                    OutputDirectory = datasetDir
                };

            SyntheticDatasetSummary dataset = SyntheticDatasetGenerator.Generate(dsOptions);
            string dbPath = Path.Combine(dataset.ProfileDirectory, "quicknotes.db");
            SqliteConnection.ClearAllPools();
            SqliteIntegrityProbe.CheckpointWal(dbPath);
            SqliteIntegrityProbe.RequireOk(dbPath);

            var baselineSamples = new List<double>();
            var r2rSamples = new List<double>();
            var baselineWarmup = new List<double>();
            var r2rWarmup = new List<double>();
            TimeSpan timeout = TimeSpan.FromMilliseconds(options.TimeoutMs);
            IReadOnlyList<ColdStartTrial> trials = ColdStartTrialSchedule.BuildAbba(options.WarmupEach, options.MeasuredEach);
            bool loggedPhases = false;

            foreach (ColdStartTrial trial in trials)
            {
                string exe = trial.Variant == ColdStartPublishVariant.Baseline ? baselineExe : r2rExe;
                IsolatedProcessRunResult run = RunOneLaunch(exe, dataset.ProfileDirectory, timeout, repoRoot);
                if (!loggedPhases)
                {
                    foreach (string phase in IsolatedColdStartLauncher.ParsePhases(run.StandardOutput))
                    {
                        options.Log?.Invoke("    " + phase);
                    }

                    loggedPhases = true;
                }

                double ms = IsolatedColdStartLauncher.ParseStartupReadyMs(run.StandardOutput);
                if (trial.IsWarmup)
                {
                    if (trial.Variant == ColdStartPublishVariant.Baseline) baselineWarmup.Add(ms);
                    else r2rWarmup.Add(ms);
                    options.Log?.Invoke("  warmup " + trial.Variant + " = " + ms.ToString("F1", CultureInfo.InvariantCulture) + " ms");
                }
                else
                {
                    if (trial.Variant == ColdStartPublishVariant.Baseline) baselineSamples.Add(ms);
                    else r2rSamples.Add(ms);
                    options.Log?.Invoke("  measured " + trial.Variant + " #" + (trial.Variant == ColdStartPublishVariant.Baseline ? baselineSamples.Count : r2rSamples.Count).ToString(CultureInfo.InvariantCulture)
                        + " = " + ms.ToString("F1", CultureInfo.InvariantCulture) + " ms");
                }
            }

            var liveAfter = LiveProfileFingerprint.Capture();
            LiveProfileFingerprint.AssertUnchanged(liveBefore, liveAfter);

            var env = BenchmarkEnvironmentCollector.Collect(dataset.ProfileDirectory);
            return new ColdStartR2rBenchmarkResult
            {
                Environment = env,
                Dataset = dataset,
                ExecutedAtUtc = DateTimeOffset.UtcNow,
                WarmupEach = options.WarmupEach,
                MeasuredEach = options.MeasuredEach,
                TimeoutMs = options.TimeoutMs,
                BaselinePublishCommand = options.BaselinePublishCommand,
                R2rPublishCommand = options.R2rPublishCommand,
                Baseline = DescribeVariant("framework-dependent win-x64 (no ReadyToRun)", baselineExe, baselineSamples, baselineWarmup),
                ReadyToRun = DescribeVariant("framework-dependent win-x64 PublishReadyToRun=true", r2rExe, r2rSamples, r2rWarmup),
                LiveProfileHashBefore = liveBefore.CombinedSha256Hex,
                LiveProfileHashAfter = liveAfter.CombinedSha256Hex,
                LiveProfileUnchanged = true
            };
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (!options.KeepDataset && Directory.Exists(datasetDir))
            {
                TryDeleteDirectory(datasetDir);
            }
        }
    }

    private static ColdStartVariantResult DescribeVariant(
        string name,
        string exe,
        List<double> measured,
        List<double> warmup)
    {
        string dir = Path.GetDirectoryName(exe)!;
        return new ColdStartVariantResult
        {
            Name = name,
            ExePath = exe,
            PublishBytes = SumDirectoryBytes(dir),
            PublishFileCount = Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Length,
            Summary = PerformanceMetricsCalculator.ComputeSummary(measured),
            WarmupSamples = warmup.ToArray()
        };
    }

    private static IsolatedProcessRunResult RunOneLaunch(
        string exe,
        string isolatedProfile,
        TimeSpan timeout,
        string repoRoot)
    {
        ReadyToRunPublishPathGuard.ValidateIsolatedLaunchProfile(isolatedProfile);
        string dbPath = Path.Combine(isolatedProfile, "quicknotes.db");
        SqliteConnection.ClearAllPools();
        SqliteIntegrityProbe.RequireOk(dbPath);
        IsolatedProcessRunResult run = IsolatedColdStartLauncher.RunPerfStartup(exe, isolatedProfile, timeout, repoRoot);
        if (run.ExitCode != 0)
        {
            throw new InvalidOperationException(
                "Published app exited " + run.ExitCode.ToString(CultureInfo.InvariantCulture) + ": " + run.StandardError);
        }

        return run;
    }

    private static long SumDirectoryBytes(string dir)
    {
        return Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Sum(path => new FileInfo(path).Length);
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
            try
            {
                Directory.Delete(path, recursive: true);
            }
            catch
            {
                // Unique temp profile only; leftover temp is not the live profile.
            }
        }
    }
}
