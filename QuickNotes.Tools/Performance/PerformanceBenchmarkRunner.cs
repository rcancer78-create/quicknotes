using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Microsoft.EntityFrameworkCore;
using QuickNotes.App.Data;
using QuickNotes.App.Models;
using QuickNotes.App.Services;
using QuickNotes.App.Services.DraftJournal;
using QuickNotes.App.ViewModels;
using QuickNotes.App.Views;

namespace QuickNotes.Tools.Performance;

public sealed class BenchmarkRunOptions
{
    public SyntheticDatasetSummary Dataset { get; init; } = null!;
    public int WarmupCount { get; init; } = 1;
    public int SampleCount { get; init; } = 5;
    public string AppExePath { get; init; } = string.Empty;
}

public enum BenchmarkMetricStatus
{
    Unverified,
    Pass,
    Fail
}

public sealed class BenchmarkMetricResult
{
    private readonly BenchmarkMetricStatus? _status;
    private readonly bool _meetsTarget;

    public string Name { get; init; } = string.Empty;
    public string TargetDescription { get; init; } = string.Empty;
    public MetricSampleSummary Summary { get; init; } = new();
    public string Unit { get; init; } = "ms";
    public bool IsAvailable { get; init; } = true;

    public BenchmarkMetricStatus Status
    {
        get
        {
            if (!IsAvailable || Summary.Count == 0)
            {
                return BenchmarkMetricStatus.Unverified;
            }
            if (_status.HasValue)
            {
                return _status.Value;
            }
            return _meetsTarget ? BenchmarkMetricStatus.Pass : BenchmarkMetricStatus.Fail;
        }
        init => _status = value;
    }

    public bool MeetsTarget
    {
        get => IsAvailable && Summary.Count > 0 && Status == BenchmarkMetricStatus.Pass;
        init => _meetsTarget = value;
    }

    public string Note { get; init; } = string.Empty;
}

public sealed class BenchmarkSuiteResult
{
    public BenchmarkEnvironmentInfo Environment { get; init; } = null!;
    public SyntheticDatasetSummary Dataset { get; init; } = null!;
    public DateTimeOffset ExecutedAtUtc { get; init; }
    public long TotalDurationMs { get; init; }

    public BenchmarkMetricResult ColdStartMetric { get; init; } = null!;
    public BenchmarkMetricResult SearchTitleMetric { get; init; } = null!;
    public BenchmarkMetricResult SearchBodyMetric { get; init; } = null!;
    public BenchmarkMetricResult SearchTagMetric { get; init; } = null!;
    public BenchmarkMetricResult SearchNoHitMetric { get; init; } = null!;
    public BenchmarkMetricResult SearchOverallMetric { get; init; } = null!;
    public BenchmarkMetricResult EditorOpenMetric { get; init; } = null!;
    public BenchmarkMetricResult WorkingSetMetric { get; init; } = null!;
    public BenchmarkMetricResult ScrollFpsMetric { get; init; } = null!;
}

public static class PerformanceBenchmarkRunner
{
    public static BenchmarkSuiteResult Run(BenchmarkRunOptions options, Action<string>? log = null)
    {
        var totalSw = Stopwatch.StartNew();
        var env = BenchmarkEnvironmentCollector.Collect(options.Dataset.ProfileDirectory);

        log?.Invoke($"Starting performance benchmark suite on {env.CpuName}, {env.LogicalCores} cores, {env.TotalPhysicalMemoryGb} GB RAM...");
        log?.Invoke($"Dataset: {options.Dataset.NoteCount} notes, {options.Dataset.TagCount} tags, {options.Dataset.AttachmentCount} attachments ({options.Dataset.AttachmentTotalBytes / (1024 * 1024)} MB)");

        // 1. Cold Process Start Benchmark
        log?.Invoke("Measuring cold process start to input-ready signal...");
        var (coldStartMetric, workingSetMetric) = MeasureColdStartupAndWorkingSet(options, log);

        // 2. Search-as-you-type Query Completion
        log?.Invoke("Measuring search-as-you-type query completion...");
        var (searchTitle, searchBody, searchTag, searchNoHit, searchOverall) = MeasureSearchQueries(options, log);

        // 3. Editor Open to Ready
        log?.Invoke("Measuring editor open to ready...");
        var editorMetric = MeasureEditorOpen(options, log);

        // 4. UI Scroll Smoothness
        log?.Invoke("Evaluating UI scroll smoothness automation...");
        var scrollMetric = MeasureScrollSmoothness(options, log);

        totalSw.Stop();

        return new BenchmarkSuiteResult
        {
            Environment = env,
            Dataset = options.Dataset,
            ExecutedAtUtc = DateTimeOffset.UtcNow,
            TotalDurationMs = totalSw.ElapsedMilliseconds,
            ColdStartMetric = coldStartMetric,
            SearchTitleMetric = searchTitle,
            SearchBodyMetric = searchBody,
            SearchTagMetric = searchTag,
            SearchNoHitMetric = searchNoHit,
            SearchOverallMetric = searchOverall,
            EditorOpenMetric = editorMetric,
            WorkingSetMetric = workingSetMetric,
            ScrollFpsMetric = scrollMetric
        };
    }

    private static (BenchmarkMetricResult Startup, BenchmarkMetricResult WorkingSet) MeasureColdStartupAndWorkingSet(
        BenchmarkRunOptions options,
        Action<string>? log)
    {
        string exe = ResolveAppExe(options.AppExePath);
        var startupSamples = new List<double>();
        var workingSetSamples = new List<double>();

        int totalRuns = options.WarmupCount + options.SampleCount;

        for (int i = 0; i < totalRuns; i++)
        {
            bool isWarmup = (i < options.WarmupCount);
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            psi.ArgumentList.Add("--isolated-profile");
            psi.ArgumentList.Add(options.Dataset.ProfileDirectory);
            psi.ArgumentList.Add("--perf-startup");

            var sw = Stopwatch.StartNew();
            using var proc = Process.Start(psi);
            if (proc == null)
            {
                throw new InvalidOperationException($"Failed to launch {exe}");
            }

            string stdout = proc.StandardOutput.ReadToEnd();
            string stderr = proc.StandardError.ReadToEnd();
            bool exited = proc.WaitForExit(30_000);
            sw.Stop();

            if (!exited)
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                throw new TimeoutException("Process timed out waiting for input-ready signal.");
            }

            if (proc.ExitCode != 0)
            {
                throw new InvalidOperationException($"App exited with non-zero code {proc.ExitCode}: {stderr}");
            }

            double startupMs = sw.Elapsed.TotalMilliseconds;
            double wsMb = 0.0;

            foreach (string line in stdout.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (line.StartsWith("QN_PERF_STARTUP_READY_MS:", StringComparison.Ordinal))
                {
                    if (double.TryParse(line.Substring("QN_PERF_STARTUP_READY_MS:".Length), out double parsedMs))
                    {
                        startupMs = parsedMs;
                    }
                }
                else if (line.StartsWith("QN_PERF_WORKING_SET_BYTES:", StringComparison.Ordinal))
                {
                    if (long.TryParse(line.Substring("QN_PERF_WORKING_SET_BYTES:".Length), out long parsedBytes))
                    {
                        wsMb = Math.Round((double)parsedBytes / (1024.0 * 1024.0), 2);
                    }
                }
                else if (line.StartsWith("QN_PERF_PHASE:", StringComparison.Ordinal))
                {
                    log?.Invoke($"    {line}");
                }
            }

            if (!isWarmup)
            {
                startupSamples.Add(startupMs);
                if (wsMb > 0)
                {
                    workingSetSamples.Add(wsMb);
                }
                log?.Invoke($"  Sample #{i - options.WarmupCount + 1}: startup={startupMs:F1}ms, workingSet={wsMb:F1}MB");
            }
            else
            {
                log?.Invoke($"  Warmup: startup={startupMs:F1}ms, workingSet={wsMb:F1}MB");
            }
        }

        var startupSummary = PerformanceMetricsCalculator.ComputeSummary(startupSamples);
        var wsSummary = PerformanceMetricsCalculator.ComputeSummary(workingSetSamples);

        var startupMetric = new BenchmarkMetricResult
        {
            Name = "Cold process start to input-ready",
            TargetDescription = "p95 <= 1500 ms (1.5 s)",
            Summary = startupSummary,
            Unit = "ms",
            MeetsTarget = startupSummary.P95 <= 1500.0,
            Note = "Measured from process startup to MainWindow rendered and SearchBox focused."
        };

        var wsMetric = new BenchmarkMetricResult
        {
            Name = "Idle working set",
            TargetDescription = "limit <= 400 MB",
            Summary = wsSummary,
            Unit = "MB",
            MeetsTarget = wsSummary.P95 <= 400.0,
            Note = "Physical working set after initial note card list materialization."
        };

        return (startupMetric, wsMetric);
    }

    private static (BenchmarkMetricResult Title, BenchmarkMetricResult Body, BenchmarkMetricResult Tag, BenchmarkMetricResult NoHit, BenchmarkMetricResult Overall)
        MeasureSearchQueries(BenchmarkRunOptions options, Action<string>? log)
    {
        string dbPath = Path.Combine(options.Dataset.ProfileDirectory, "quicknotes.db");
        using var db = new QuickNotesDbContext(dbPath);
        var searchService = new SearchService();
        var allTags = db.Tags.AsNoTracking().ToList();

        var queryCases = new[]
        {
            ("Title search", SyntheticDatasetSummary.TitleQueryTerm),
            ("Body search", SyntheticDatasetSummary.BodyQueryTerm),
            ("Tag intersection", SyntheticDatasetSummary.TagQueryTerm),
            ("No-hit search", SyntheticDatasetSummary.NoHitQueryTerm)
        };

        var results = new Dictionary<string, MetricSampleSummary>();
        var allSamples = new List<double>();

        foreach (var (name, query) in queryCases)
        {
            var samples = new List<double>();
            int totalRuns = options.WarmupCount + options.SampleCount;

            for (int i = 0; i < totalRuns; i++)
            {
                bool isWarmup = (i < options.WarmupCount);

                var sw = Stopwatch.StartNew();

                // 1. Count query
                int totalCount = searchService.CountNotes(
                    db,
                    query,
                    selectedTagId: null,
                    allTags: allTags,
                    section: NavigationSection.All,
                    protectedNoteIds: null,
                    cancellationToken: CancellationToken.None);

                // 2. Paged query (page 1: skip 0, take 50)
                var notes = searchService.QueryNotes(
                    db,
                    query,
                    selectedTagId: null,
                    allTags: allTags,
                    section: NavigationSection.All,
                    sortMode: NoteSortMode.Pinned,
                    protectedNoteIds: null,
                    skip: 0,
                    take: 50,
                    cancellationToken: CancellationToken.None);

                // 3. Materialize NoteCardViewModels for visible page (bounded to <= 50)
                var cards = new List<NoteCardViewModel>(notes.Count);
                foreach (var note in notes)
                {
                    cards.Add(new NoteCardViewModel(note));
                }

                sw.Stop();
                double elapsedMs = sw.Elapsed.TotalMilliseconds;

                if (cards.Count > 50)
                {
                    throw new InvalidOperationException($"First page materialization violated page bound: {cards.Count} cards materialized.");
                }

                if (!isWarmup)
                {
                    samples.Add(elapsedMs);
                    allSamples.Add(elapsedMs);
                }
            }

            var summary = PerformanceMetricsCalculator.ComputeSummary(samples);
            results[name] = summary;
            log?.Invoke($"  {name} ('{query}'): median={summary.Median:F2}ms, p95={summary.P95:F2}ms");
        }

        var overallSummary = PerformanceMetricsCalculator.ComputeSummary(allSamples);

        var titleMetric = new BenchmarkMetricResult
        {
            Name = "Search: Title query",
            TargetDescription = "p95 <= 150 ms",
            Summary = results["Title search"],
            Unit = "ms",
            MeetsTarget = results["Title search"].P95 <= 150.0
        };

        var bodyMetric = new BenchmarkMetricResult
        {
            Name = "Search: Body query",
            TargetDescription = "p95 <= 150 ms",
            Summary = results["Body search"],
            Unit = "ms",
            MeetsTarget = results["Body search"].P95 <= 150.0
        };

        var tagMetric = new BenchmarkMetricResult
        {
            Name = "Search: Tag intersection",
            TargetDescription = "p95 <= 150 ms",
            Summary = results["Tag intersection"],
            Unit = "ms",
            MeetsTarget = results["Tag intersection"].P95 <= 150.0
        };

        var noHitMetric = new BenchmarkMetricResult
        {
            Name = "Search: No-hit query",
            TargetDescription = "p95 <= 150 ms",
            Summary = results["No-hit search"],
            Unit = "ms",
            MeetsTarget = results["No-hit search"].P95 <= 150.0
        };

        var overallMetric = new BenchmarkMetricResult
        {
            Name = "Search-as-you-type query completion (overall)",
            TargetDescription = "p95 <= 150 ms",
            Summary = overallSummary,
            Unit = "ms",
            MeetsTarget = overallSummary.P95 <= 150.0,
            Note = "Includes query parsing, FTS/tag search, Count, first-page fetch (50), and NoteCardViewModel instantiation."
        };

        return (titleMetric, bodyMetric, tagMetric, noHitMetric, overallMetric);
    }

    private static BenchmarkMetricResult MeasureEditorOpen(BenchmarkRunOptions options, Action<string>? log)
    {
        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
        {
            BenchmarkMetricResult? staResult = null;
            Exception? staEx = null;
            var thread = new Thread(() =>
            {
                try
                {
                    staResult = MeasureEditorOpenInternal(options, log);
                }
                catch (Exception ex)
                {
                    staEx = ex;
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();

            if (staEx != null)
            {
                throw new InvalidOperationException($"STA execution failed: {staEx.Message}", staEx);
            }
            return staResult!;
        }

        return MeasureEditorOpenInternal(options, log);
    }

    private static BenchmarkMetricResult MeasureEditorOpenInternal(BenchmarkRunOptions options, Action<string>? log)
    {
        if (Application.Current == null)
        {
            try
            {
                _ = new QuickNotes.App.App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            }
            catch (InvalidOperationException)
            {
            }
        }

        string dbPath = Path.Combine(options.Dataset.ProfileDirectory, "quicknotes.db");
        using var db = new QuickNotesDbContext(dbPath);
        var targetNote = db.Notes
            .Include(n => n.NoteTags)
            .ThenInclude(nt => nt.Tag)
            .First(n => n.DeletedAt == null);

        var allTags = db.Tags.AsNoTracking().ToList();
        var tagRuleService = new TagRuleService(Path.Combine(options.Dataset.ProfileDirectory, "tag_rules.json"));
        var tagDetectionService = new TagDetectionService(tagRuleService);
        var noteHistoryService = new NoteHistoryService();
        var noteLinkService = new NoteLinkService();
        var attachmentStorageService = new AttachmentStorageService(options.Dataset.ProfileDirectory);
        var settingsService = new SettingsService(
            Path.Combine(options.Dataset.ProfileDirectory, "settings.json"),
            _ => { });
        var draftJournalService = new DraftJournalService(baseDirectory: options.Dataset.ProfileDirectory);

        var samples = new List<double>();
        int totalRuns = options.WarmupCount + options.SampleCount;

        for (int i = 0; i < totalRuns; i++)
        {
            bool isWarmup = (i < options.WarmupCount);

            var sw = Stopwatch.StartNew();

            var vm = new NoteEditorViewModel(
                tagDetectionService,
                allTags,
                existingNote: targetNote,
                rules: tagRuleService.GetAllRules(),
                noteHistoryService: noteHistoryService,
                contextFactory: () => new QuickNotesDbContext(dbPath),
                noteLinkService: noteLinkService,
                attachmentStorageService: attachmentStorageService,
                settingsService: settingsService,
                draftJournalService: draftJournalService);

            // Simulate UI preparation
            var window = new NoteEditorWindow(vm);
            window.BeginInit();
            window.EndInit();

            sw.Stop();
            double elapsedMs = sw.Elapsed.TotalMilliseconds;

            vm.Dispose();

            if (!isWarmup)
            {
                samples.Add(elapsedMs);
            }
        }

        var summary = PerformanceMetricsCalculator.ComputeSummary(samples);
        log?.Invoke($"  Editor open: median={summary.Median:F2}ms, p95={summary.P95:F2}ms");

        return new BenchmarkMetricResult
        {
            Name = "Editor open to ready",
            TargetDescription = "p95 <= 300 ms",
            Summary = summary,
            Unit = "ms",
            MeetsTarget = summary.P95 <= 300.0,
            Note = "Instantiates NoteEditorViewModel, loads history/attachments/tags, and initializes NoteEditorWindow."
        };
    }

    private static BenchmarkMetricResult MeasureScrollSmoothness(BenchmarkRunOptions options, Action<string>? log)
    {
        string? exe;
        try
        {
            exe = ResolveAppExe(options.AppExePath);
        }
        catch (Exception ex)
        {
            log?.Invoke($"  Scroll automation skipped (App executable not found): {ex.Message}");
            return CreateUnverifiedScrollMetric("Headless / background process execution does not provide a physical 60Hz display VSYNC loop. Left unverified per ROADMAP rules rather than inventing FPS.");
        }

        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        psi.ArgumentList.Add("--isolated-profile");
        psi.ArgumentList.Add(options.Dataset.ProfileDirectory);
        psi.ArgumentList.Add("--perf-scroll");

        try
        {
            using var proc = Process.Start(psi);
            if (proc != null)
            {
                string stdout = proc.StandardOutput.ReadToEnd();
                bool exited = proc.WaitForExit(15_000);
                if (!exited)
                {
                    try { proc.Kill(entireProcessTree: true); } catch { }
                }

                if (stdout.Contains("QN_PERF_SCROLL_MEDIAN_FPS:", StringComparison.Ordinal))
                {
                    double fps = 0.0;
                    foreach (string line in stdout.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (line.StartsWith("QN_PERF_SCROLL_MEDIAN_FPS:", StringComparison.Ordinal))
                        {
                            double.TryParse(line.Substring("QN_PERF_SCROLL_MEDIAN_FPS:".Length), out fps);
                        }
                    }

                    if (fps > 0)
                    {
                        var summary = new MetricSampleSummary
                        {
                            Count = 1,
                            Median = fps,
                            P95 = fps,
                            Min = fps,
                            Max = fps,
                            Mean = fps,
                            RawSamples = new[] { fps }
                        };

                        log?.Invoke($"  Scroll diagnostic measured: {fps:F1} fps (non-acceptance; unverified against display refresh)");
                        return new BenchmarkMetricResult
                        {
                            Name = "List scroll smoothness",
                            TargetDescription = ">= 50 fps on reference dataset",
                            Summary = summary,
                            Unit = "fps",
                            IsAvailable = false,
                            Status = BenchmarkMetricStatus.Unverified,
                            MeetsTarget = false,
                            Note = $"UNVERIFIED: CompositionTarget.Rendering callback delta registered {fps:F1} FPS diagnostic, which measures WPF dispatch ticks rather than delivered monitor presentation frames/jank. Non-acceptance diagnostic only; not compared to 50-FPS target."
                        };
                    }
                }
            }
        }
        catch (Exception ex)
        {
            log?.Invoke($"  Scroll automation check note: {ex.Message}");
        }

        return CreateUnverifiedScrollMetric("Headless / background process execution does not provide a physical 60Hz display VSYNC loop. Left unverified per ROADMAP rules rather than inventing FPS.");
    }

    private static BenchmarkMetricResult CreateUnverifiedScrollMetric(string note)
    {
        return new BenchmarkMetricResult
        {
            Name = "List scroll smoothness",
            TargetDescription = ">= 50 fps on reference dataset",
            Summary = new MetricSampleSummary(),
            Unit = "fps",
            IsAvailable = false,
            Status = BenchmarkMetricStatus.Unverified,
            MeetsTarget = false,
            Note = note
        };
    }

    private static string ResolveAppExe(string? explicitPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath) && File.Exists(explicitPath))
        {
            return explicitPath;
        }

        string baseDir = AppContext.BaseDirectory;
        string candidate1 = Path.Combine(baseDir, "QuickNotes.App.exe");
        if (File.Exists(candidate1)) return candidate1;

        string candidate2 = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "QuickNotes.App", "bin", "Release", "net8.0-windows10.0.19041.0", "QuickNotes.App.exe"));
        if (File.Exists(candidate2)) return candidate2;

        string candidate3 = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "QuickNotes.App", "bin", "Debug", "net8.0-windows10.0.19041.0", "QuickNotes.App.exe"));
        if (File.Exists(candidate3)) return candidate3;

        throw new FileNotFoundException("QuickNotes.App.exe not found for benchmark execution.", candidate1);
    }
}
