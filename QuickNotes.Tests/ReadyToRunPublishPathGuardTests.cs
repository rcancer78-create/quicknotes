using System;
using System.Globalization;
using System.IO;
using System.Linq;
using QuickNotes.App.Data;
using QuickNotes.Tools.Performance;
using Xunit;

namespace QuickNotes.Tests;

[TestCategory(TestCategories.Unit)]
public sealed class ReadyToRunPublishPathGuardTests
{
    private const string FakeRepo = @"C:\qn_guard_repo_root";

    [Fact]
    public void Normalize_AllowsArtifactsColdStartBaselineAndR2r()
    {
        string baseline = Path.Combine(FakeRepo, "artifacts", "publish", "cold-start", "20260913T000000Z_abcd", "baseline");
        string r2r = Path.Combine(FakeRepo, "artifacts", "publish", "cold-start", "20260913T000000Z_abcd", "r2r");
        Assert.Equal(Path.GetFullPath(baseline), ReadyToRunPublishPathGuard.NormalizeAndValidatePublishDirectory(baseline, FakeRepo));
        Assert.Equal(Path.GetFullPath(r2r), ReadyToRunPublishPathGuard.NormalizeAndValidatePublishDirectory(r2r, FakeRepo));
    }

    [Fact]
    public void Normalize_RejectsAppBinAndObjAndDefaultPublish()
    {
        string bin = Path.Combine(FakeRepo, "QuickNotes.App", "bin", "Release", "net8.0-windows10.0.19041.0");
        string obj = Path.Combine(FakeRepo, "QuickNotes.App", "obj", "Release");
        string nestedPublish = Path.Combine(bin, "win-x64", "publish");
        Assert.Throws<InvalidOperationException>(() => ReadyToRunPublishPathGuard.NormalizeAndValidatePublishDirectory(bin, FakeRepo));
        Assert.Throws<InvalidOperationException>(() => ReadyToRunPublishPathGuard.NormalizeAndValidatePublishDirectory(obj, FakeRepo));
        Assert.Throws<InvalidOperationException>(() => ReadyToRunPublishPathGuard.NormalizeAndValidatePublishDirectory(nestedPublish, FakeRepo));
    }

    [Fact]
    public void Normalize_RejectsMalformedColdStartLeaf()
    {
        string mixed = Path.Combine(FakeRepo, "artifacts", "publish", "cold-start", "run1", "mixed");
        Assert.Throws<InvalidOperationException>(() => ReadyToRunPublishPathGuard.NormalizeAndValidatePublishDirectory(mixed, FakeRepo));
        string tooDeep = Path.Combine(FakeRepo, "artifacts", "publish", "cold-start", "run1", "baseline", "extra");
        Assert.Throws<InvalidOperationException>(() => ReadyToRunPublishPathGuard.NormalizeAndValidatePublishDirectory(tooDeep, FakeRepo));
        string runBin = Path.Combine(FakeRepo, "artifacts", "publish", "cold-start", "bin", "baseline");
        Assert.Throws<InvalidOperationException>(() => ReadyToRunPublishPathGuard.NormalizeAndValidatePublishDirectory(runBin, FakeRepo));
    }

    [Fact]
    public void Normalize_AllowsUniqueTempDirectory()
    {
        string? temp = Environment.GetEnvironmentVariable("TEMP") ?? Environment.GetEnvironmentVariable("TMP");
        Assert.False(string.IsNullOrWhiteSpace(temp));
        string dir = Path.GetFullPath(Path.Combine(temp!, "qn_r2r_unit_" + Guid.NewGuid().ToString("N")));
        Assert.Equal(dir, ReadyToRunPublishPathGuard.NormalizeAndValidatePublishDirectory(dir, FakeRepo));
    }

    [Fact]
    public void BuildPerfStartupArguments_RequiresIsolatedTempAndNeverLiveProfile()
    {
        Assert.Throws<InvalidOperationException>(() =>
            ReadyToRunPublishPathGuard.BuildPerfStartupArguments(QuickNotesDbContext.LiveProfileDirectory));

        string? temp = Environment.GetEnvironmentVariable("TEMP") ?? Environment.GetEnvironmentVariable("TMP");
        string profile = Path.GetFullPath(Path.Combine(temp!, "qn_r2r_args_" + Guid.NewGuid().ToString("N")));
        string[] args = ReadyToRunPublishPathGuard.BuildPerfStartupArguments(profile);
        Assert.Equal(new[] { "--isolated-profile", profile, "--perf-startup" }, args);
        Assert.DoesNotContain(args, a => a.Contains("LocalAppData", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AbbaSchedule_WarmupTwoMeasuredTen_HasEqualCountsAndAbbaPattern()
    {
        var trials = ColdStartTrialSchedule.BuildAbba(2, 10);
        Assert.Equal(24, trials.Count);
        Assert.Equal(2, trials.Count(t => t.IsWarmup && t.Variant == ColdStartPublishVariant.Baseline));
        Assert.Equal(2, trials.Count(t => t.IsWarmup && t.Variant == ColdStartPublishVariant.ReadyToRun));
        Assert.Equal(10, trials.Count(t => !t.IsWarmup && t.Variant == ColdStartPublishVariant.Baseline));
        Assert.Equal(10, trials.Count(t => !t.IsWarmup && t.Variant == ColdStartPublishVariant.ReadyToRun));
        Assert.All(trials.Take(4), t => Assert.True(t.IsWarmup));
        Assert.All(trials.Skip(4), t => Assert.False(t.IsWarmup));
        Assert.Equal(
            new[]
            {
                ColdStartPublishVariant.Baseline,
                ColdStartPublishVariant.ReadyToRun,
                ColdStartPublishVariant.ReadyToRun,
                ColdStartPublishVariant.Baseline
            },
            trials.Take(4).Select(t => t.Variant).ToArray());
    }

    [Fact]
    public void CliParse_RequiresRepoAndBothExes_AndRejectsUnknown()
    {
        Assert.True(PerformanceColdStartR2rTool.Parse(Array.Empty<string>()).HasParseError);
        Assert.True(PerformanceColdStartR2rTool.Parse(new[] { "--repo-root", FakeRepo }).HasParseError);
        Assert.True(PerformanceColdStartR2rTool.Parse(new[] { "--repo-root", FakeRepo, "--baseline-exe", "a", "--r2r-exe", "b", "--nope" }).HasParseError);

        var ok = PerformanceColdStartR2rTool.Parse(new[]
        {
            "--repo-root", FakeRepo,
            "--baseline-exe", @"C:\pub\baseline\app.exe",
            "--r2r-exe", @"C:\pub\r2r\app.exe",
            "--warmup", "2",
            "--samples", "10",
            "--timeout-ms", "15000"
        });
        Assert.False(ok.HasParseError);
        Assert.Equal(2, ok.WarmupEach);
        Assert.Equal(10, ok.MeasuredEach);
        Assert.Equal(15000, ok.TimeoutMs);
        Assert.True(PerformanceColdStartR2rTool.Parse(new[]
        {
            "--repo-root", FakeRepo,
            "--baseline-exe", "a",
            "--r2r-exe", "b",
            "--timeout-ms", "0"
        }).HasParseError);
    }

    [Fact]
    public void ReportStatistics_UseMeasuredSamplesOnly_AndDoNotCloseGateBelowTen()
    {
        double[] measured = { 1400, 1410, 1420, 1430, 1440, 1450, 1460, 1470, 1480, 2000 };
        var summary = PerformanceMetricsCalculator.ComputeSummary(measured);
        Assert.Equal(10, summary.Count);
        Assert.Equal(1400, summary.Min);
        Assert.Equal(2000, summary.Max);
        Assert.Equal(1445, summary.Median);
        Assert.True(summary.P95 > 1480 && summary.P95 <= 2000);

        var env = new BenchmarkEnvironmentInfo { OsDescription = "Windows", FrameworkDescription = ".NET", Architecture = "X64", CpuName = "CPU", LogicalCores = 12, TotalPhysicalMemoryGb = 32, BuildConfiguration = "Release", StorageType = "SSD", StorageFileSystem = "NTFS" };
        var dataset = new SyntheticDatasetSummary { Seed = 1337, NoteCount = 10, TagCount = 1, RevisionCount = 1, DatabaseSizeBytes = 1024 };
        var result = new ColdStartR2rBenchmarkResult
        {
            Environment = env,
            Dataset = dataset,
            ExecutedAtUtc = new DateTimeOffset(2026, 9, 13, 8, 0, 0, TimeSpan.Zero),
            WarmupEach = 2,
            MeasuredEach = 10,
            TimeoutMs = 60000,
            BaselinePublishCommand = "dotnet publish baseline",
            R2rPublishCommand = "dotnet publish r2r",
            LiveProfileHashBefore = "AAA",
            LiveProfileHashAfter = "AAA",
            LiveProfileUnchanged = true,
            Baseline = new ColdStartVariantResult
            {
                Name = "baseline",
                ExePath = "b.exe",
                PublishBytes = 10,
                PublishFileCount = 2,
                Summary = summary,
                WarmupSamples = new[] { 1.0, 2.0 }
            },
            ReadyToRun = new ColdStartVariantResult
            {
                Name = "r2r",
                ExePath = "r.exe",
                PublishBytes = 11,
                PublishFileCount = 2,
                Summary = PerformanceMetricsCalculator.ComputeSummary(new[] { 900.0, 910.0 }),
                WarmupSamples = Array.Empty<double>()
            }
        };

        Assert.False(result.ReadyToRunMeetsTarget);
        string markdown = ColdStartR2rReportGenerator.GenerateMarkdown(result);
        Assert.Contains("1400.0, 1410.0", markdown, StringComparison.Ordinal);
        Assert.Contains("not** ≤ 1.5 s", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("CompositionTarget.Rendering as proof", markdown, StringComparison.Ordinal);

        var passing = new ColdStartR2rBenchmarkResult
        {
            Environment = env,
            Dataset = dataset,
            ExecutedAtUtc = result.ExecutedAtUtc,
            WarmupEach = 2,
            MeasuredEach = 10,
            TimeoutMs = 60000,
            LiveProfileHashBefore = "AAA",
            LiveProfileHashAfter = "AAA",
            LiveProfileUnchanged = true,
            Baseline = result.Baseline,
            ReadyToRun = new ColdStartVariantResult
            {
                Name = "r2r",
                Summary = PerformanceMetricsCalculator.ComputeSummary(Enumerable.Repeat(1000.0, 10)),
                WarmupSamples = Array.Empty<double>()
            }
        };
        Assert.True(passing.ReadyToRunMeetsTarget);
        Assert.Contains("may be closed", ColdStartR2rReportGenerator.GenerateMarkdown(passing), StringComparison.Ordinal);
    }
}
