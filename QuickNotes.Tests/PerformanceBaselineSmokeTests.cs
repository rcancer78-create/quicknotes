using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using QuickNotes.App.Data;
using QuickNotes.Tools.Performance;
using Xunit;

namespace QuickNotes.Tests;

[TestCategory(TestCategories.UiSensitive)]
public sealed class PerformanceBaselineSmokeTests
{
    [Fact]
    public void Isolation_RejectsLiveProfileAndAncestorsAndDescendants()
    {
        string liveProfile = QuickNotesDbContext.LiveProfileDirectory;

        // Must reject null or whitespace
        Assert.Throws<ArgumentException>(() => QuickNotesDbContext.ValidateIsolatedProfilePath(null!));
        Assert.Throws<ArgumentException>(() => QuickNotesDbContext.ValidateIsolatedProfilePath("   "));

        // Must reject exact live profile
        Assert.Throws<InvalidOperationException>(() => QuickNotesDbContext.ValidateIsolatedProfilePath(liveProfile));

        // Must reject live profile with trailing separator
        Assert.Throws<InvalidOperationException>(() => QuickNotesDbContext.ValidateIsolatedProfilePath(liveProfile + Path.DirectorySeparatorChar));

        // Must reject ancestor (e.g. %LOCALAPPDATA%)
        string? parent = Directory.GetParent(liveProfile)?.FullName;
        if (parent != null)
        {
            Assert.Throws<InvalidOperationException>(() => QuickNotesDbContext.ValidateIsolatedProfilePath(parent));
        }

        // Must reject descendant (e.g. %LOCALAPPDATA%\QuickNotes\Attachments)
        string descendant = Path.Combine(liveProfile, "Attachments");
        Assert.Throws<InvalidOperationException>(() => QuickNotesDbContext.ValidateIsolatedProfilePath(descendant));

        // Must allow an isolated temp directory
        string tempIsolated = Path.Combine(Path.GetTempPath(), "qn_perf_smoke_test_" + Guid.NewGuid().ToString("N"));
        QuickNotesDbContext.ValidateIsolatedProfilePath(tempIsolated);
    }

    [Fact]
    public void MetricsCalculator_CorrectlyComputesMedianP95MinMax()
    {
        // Edge case: single element
        var singleSummary = PerformanceMetricsCalculator.ComputeSummary(new double[] { 42.0 });
        Assert.Equal(42.0, singleSummary.Median);
        Assert.Equal(42.0, singleSummary.P95);
        Assert.Equal(42.0, singleSummary.Min);
        Assert.Equal(42.0, singleSummary.Max);
        Assert.Equal(42.0, singleSummary.Mean);

        // Edge case: empty
        var emptySummary = PerformanceMetricsCalculator.ComputeSummary(Array.Empty<double>());
        Assert.Equal(0.0, emptySummary.Median);
        Assert.Equal(0.0, emptySummary.P95);

        // Odd count: [10, 20, 30, 40, 50]
        var oddSummary = PerformanceMetricsCalculator.ComputeSummary(new double[] { 50, 10, 30, 20, 40 });
        Assert.Equal(30.0, oddSummary.Median);
        Assert.Equal(10.0, oddSummary.Min);
        Assert.Equal(50.0, oddSummary.Max);
        Assert.True(oddSummary.P95 >= 40.0 && oddSummary.P95 <= 50.0);

        // Even count: [10, 20, 30, 40] -> median = 25.0
        var evenSummary = PerformanceMetricsCalculator.ComputeSummary(new double[] { 40, 20, 10, 30 });
        Assert.Equal(25.0, evenSummary.Median);

        // 100 uniform values from 1 to 100
        var uniform = Enumerable.Range(1, 100).Select(x => (double)x).ToList();
        var uniformSummary = PerformanceMetricsCalculator.ComputeSummary(uniform);
        Assert.Equal(50.5, uniformSummary.Median);
        Assert.True(uniformSummary.P95 >= 95.0 && uniformSummary.P95 <= 96.0);
    }

    [Fact]
    public void DeterministicGeneration_SameSeedYieldsIdenticalData()
    {
        string dir1 = Path.Combine(Path.GetTempPath(), "qn_perf_det_1_" + Guid.NewGuid().ToString("N"));
        string dir2 = Path.Combine(Path.GetTempPath(), "qn_perf_det_2_" + Guid.NewGuid().ToString("N"));

        try
        {
            var options1 = new SyntheticDatasetOptions
            {
                OutputDirectory = dir1,
                Seed = 9999,
                NoteCount = 20,
                AttachmentTotalBytes = 50 * 1024 // 50 KB
            };

            var options2 = new SyntheticDatasetOptions
            {
                OutputDirectory = dir2,
                Seed = 9999,
                NoteCount = 20,
                AttachmentTotalBytes = 50 * 1024 // 50 KB
            };

            var summary1 = SyntheticDatasetGenerator.Generate(options1);
            var summary2 = SyntheticDatasetGenerator.Generate(options2);

            Assert.Equal(summary1.NoteCount, summary2.NoteCount);
            Assert.Equal(summary1.TagCount, summary2.TagCount);
            Assert.Equal(summary1.RevisionCount, summary2.RevisionCount);
            Assert.Equal(summary1.AttachmentCount, summary2.AttachmentCount);
            Assert.Equal(summary1.AttachmentTotalBytes, summary2.AttachmentTotalBytes);

            // Compare generated attachment files
            string attachDir1 = Path.Combine(dir1, "Attachments");
            string attachDir2 = Path.Combine(dir2, "Attachments");
            string[] files1 = Directory.GetFiles(attachDir1).OrderBy(f => Path.GetFileName(f)).ToArray();
            string[] files2 = Directory.GetFiles(attachDir2).OrderBy(f => Path.GetFileName(f)).ToArray();

            Assert.Equal(files1.Length, files2.Length);
            for (int i = 0; i < files1.Length; i++)
            {
                byte[] hash1 = SHA256.HashData(File.ReadAllBytes(files1[i]));
                byte[] hash2 = SHA256.HashData(File.ReadAllBytes(files2[i]));
                Assert.Equal(hash1, hash2);
            }

            // Compare notes in database
            using (var db1 = new QuickNotesDbContext(Path.Combine(dir1, "quicknotes.db")))
            using (var db2 = new QuickNotesDbContext(Path.Combine(dir2, "quicknotes.db")))
            {
                var notes1 = db1.Notes.OrderBy(n => n.Id).ToList();
                var notes2 = db2.Notes.OrderBy(n => n.Id).ToList();

                Assert.Equal(notes1.Count, notes2.Count);
                for (int i = 0; i < notes1.Count; i++)
                {
                    Assert.Equal(notes1[i].Title, notes2[i].Title);
                    Assert.Equal(notes1[i].Text, notes2[i].Text);
                    Assert.Equal(notes1[i].IsPinned, notes2[i].IsPinned);
                }
            }
        }
        finally
        {
            SafeDeleteDirectory(dir1);
            SafeDeleteDirectory(dir2);
        }
    }

    [Fact]
    public void DeterministicGeneration_DifferentSeedYieldsDifferentData()
    {
        string dir1 = Path.Combine(Path.GetTempPath(), "qn_perf_diff_1_" + Guid.NewGuid().ToString("N"));
        string dir2 = Path.Combine(Path.GetTempPath(), "qn_perf_diff_2_" + Guid.NewGuid().ToString("N"));

        try
        {
            var summary1 = SyntheticDatasetGenerator.Generate(new SyntheticDatasetOptions
            {
                OutputDirectory = dir1,
                Seed = 1111,
                NoteCount = 20,
                AttachmentTotalBytes = 20 * 1024
            });

            var summary2 = SyntheticDatasetGenerator.Generate(new SyntheticDatasetOptions
            {
                OutputDirectory = dir2,
                Seed = 2222,
                NoteCount = 20,
                AttachmentTotalBytes = 20 * 1024
            });

            using var db1 = new QuickNotesDbContext(Path.Combine(dir1, "quicknotes.db"));
            using var db2 = new QuickNotesDbContext(Path.Combine(dir2, "quicknotes.db"));

            var notes1 = db1.Notes.OrderBy(n => n.Id).ToList();
            var notes2 = db2.Notes.OrderBy(n => n.Id).ToList();

            // Titles or texts must differ between different seeds
            bool anyDifference = false;
            for (int i = 0; i < Math.Min(notes1.Count, notes2.Count); i++)
            {
                if (notes1[i].Title != notes2[i].Title || notes1[i].Text != notes2[i].Text)
                {
                    anyDifference = true;
                    break;
                }
            }
            Assert.True(anyDifference, "Expected datasets generated with different seeds to differ.");
        }
        finally
        {
            SafeDeleteDirectory(dir1);
            SafeDeleteDirectory(dir2);
        }
    }

    [Fact]
    public void SmokeBenchmarkSuite_CompletesQuicklyWithValidMetrics()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "qn_perf_suite_smoke_" + Guid.NewGuid().ToString("N"));
        try
        {
            var dataset = SyntheticDatasetGenerator.Generate(new SyntheticDatasetOptions
            {
                OutputDirectory = tempDir,
                Seed = 42,
                NoteCount = 25,
                AttachmentTotalBytes = 50 * 1024
            });

            BenchmarkSuiteResult? result = null;
            // The editor measurement must use the Application-owning STA. Calling
            // it from MTA creates an Application on a temporary thread that exits.
            StaTestHarness.Run(() => result = PerformanceBenchmarkRunner.Run(new BenchmarkRunOptions
            {
                Dataset = dataset,
                WarmupCount = 0,
                SampleCount = 1
            }), TimeSpan.FromSeconds(60));

            Assert.NotNull(result);
            Assert.True(result.SearchOverallMetric.Summary.Median > 0);
            Assert.True(result.EditorOpenMetric.Summary.Median > 0);
            Assert.True(result.ColdStartMetric.Summary.Median > 0);
            Assert.True(result.WorkingSetMetric.Summary.Median > 0);
            Assert.False(result.ScrollFpsMetric.MeetsTarget);
            Assert.Equal(BenchmarkMetricStatus.Unverified, result.ScrollFpsMetric.Status);
            Assert.False(result.ScrollFpsMetric.IsAvailable);
        }
        finally
        {
            SafeDeleteDirectory(tempDir);
        }
    }

    [Fact]
    public void ReportGenerator_RendersUnavailableMetricAsUnverifiedRatherThanPass()
    {
        var unavailableMetricWithMeetsTargetTrue = new BenchmarkMetricResult
        {
            Name = "List scroll smoothness",
            TargetDescription = ">= 50 fps on reference dataset",
            Summary = new MetricSampleSummary
            {
                Count = 1,
                Median = 154.5,
                P95 = 154.5,
                RawSamples = new[] { 154.5 }
            },
            Unit = "fps",
            IsAvailable = false,
            MeetsTarget = true,
            Note = "Diagnostic data only; unverified"
        };

        // Contract verification: IsAvailable=false forces Status=Unverified and MeetsTarget=false
        Assert.Equal(BenchmarkMetricStatus.Unverified, unavailableMetricWithMeetsTargetTrue.Status);
        Assert.False(unavailableMetricWithMeetsTargetTrue.MeetsTarget);

        var suiteResult = new BenchmarkSuiteResult
        {
            Environment = new BenchmarkEnvironmentInfo
            {
                CpuName = "Test CPU",
                LogicalCores = 8,
                TotalPhysicalMemoryGb = 16.0,
                OsDescription = "Microsoft Windows Test",
                FrameworkDescription = ".NET 8.0.23",
                Architecture = "X64",
                BuildConfiguration = "Release",
                StorageType = "SSD",
                StorageFileSystem = "NTFS",
                SystemDpi = 96,
                SystemTheme = "Light"
            },
            Dataset = new SyntheticDatasetSummary
            {
                Seed = 1337,
                NoteCount = 10,
                TagCount = 5,
                RevisionCount = 5,
                AttachmentCount = 2,
                AttachmentTotalBytes = 1024,
                DatabaseSizeBytes = 2048,
                GenerationElapsedMs = 100
            },
            ExecutedAtUtc = DateTimeOffset.UtcNow,
            TotalDurationMs = 500,
            ColdStartMetric = new BenchmarkMetricResult { Name = "Cold process start to input-ready", TargetDescription = "<= 1500 ms", Summary = new MetricSampleSummary { Count = 1, Median = 1000, P95 = 1000 }, MeetsTarget = true },
            SearchOverallMetric = new BenchmarkMetricResult { Name = "Search-as-you-type query completion (overall)", TargetDescription = "<= 150 ms", Summary = new MetricSampleSummary { Count = 1, Median = 20, P95 = 20 }, MeetsTarget = true },
            SearchTitleMetric = new BenchmarkMetricResult { Name = "Search: Title query", Summary = new MetricSampleSummary { Count = 1, Median = 15, P95 = 15 } },
            SearchBodyMetric = new BenchmarkMetricResult { Name = "Search: Body query", Summary = new MetricSampleSummary { Count = 1, Median = 15, P95 = 15 } },
            SearchTagMetric = new BenchmarkMetricResult { Name = "Search: Tag intersection", Summary = new MetricSampleSummary { Count = 1, Median = 15, P95 = 15 } },
            SearchNoHitMetric = new BenchmarkMetricResult { Name = "Search: No-hit query", Summary = new MetricSampleSummary { Count = 1, Median = 15, P95 = 15 } },
            EditorOpenMetric = new BenchmarkMetricResult { Name = "Editor open to ready", TargetDescription = "<= 300 ms", Summary = new MetricSampleSummary { Count = 1, Median = 30, P95 = 30 }, MeetsTarget = true },
            WorkingSetMetric = new BenchmarkMetricResult { Name = "Idle working set", TargetDescription = "<= 400 MB", Summary = new MetricSampleSummary { Count = 1, Median = 150, P95 = 150 }, MeetsTarget = true },
            ScrollFpsMetric = unavailableMetricWithMeetsTargetTrue
        };

        string markdown = PerformanceReportGenerator.GenerateMarkdown(suiteResult);

        var lines = markdown.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        var scrollRow = lines.FirstOrDefault(l => l.Contains("List scroll smoothness"));
        Assert.NotNull(scrollRow);
        Assert.Contains("**UNVERIFIED**", scrollRow);
        Assert.DoesNotContain("**PASS**", scrollRow);
        Assert.Contains("N/A", scrollRow);
    }

    [Fact]
    public void PerformanceBenchmarkTool_WhenOptionalUiMetricUnavailable_ExitsSuccessfully()
    {
        string tempReport = Path.Combine(Path.GetTempPath(), "qn_perf_smoke_report_" + Guid.NewGuid().ToString("N") + ".md");
        try
        {
            int exitCode = -1;
            // This entry point also measures the editor in-process.
            StaTestHarness.Run(() => exitCode = PerformanceBenchmarkTool.Run(new[]
            {
                "--smoke",
                "--seed", "42",
                "--samples", "1",
                "--report", tempReport
            }), TimeSpan.FromSeconds(60));

            Assert.Equal(0, exitCode);
            Assert.True(File.Exists(tempReport));
            string reportText = File.ReadAllText(tempReport);
            Assert.Contains("**UNVERIFIED**", reportText);
            Assert.DoesNotContain("| List scroll smoothness | >= 50 fps on reference dataset | 154,50 fps | 154,50 fps | **PASS** |", reportText);
        }
        finally
        {
            if (File.Exists(tempReport))
            {
                try { File.Delete(tempReport); } catch { }
            }
        }
    }

    private static void SafeDeleteDirectory(string path)
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        if (Directory.Exists(path))
        {
            try
            {
                Directory.Delete(path, recursive: true);
            }
            catch (IOException)
            {
                System.Threading.Thread.Sleep(50);
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                try { Directory.Delete(path, recursive: true); } catch { }
            }
        }
    }
}
