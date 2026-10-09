using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Xml.Linq;
using QuickNotes.App.Data;
using QuickNotes.App.Services;

namespace QuickNotes.Tests;

[TestCategory(TestCategories.UiSensitive)]
public sealed class TestHostIsolationTests
{
    private const string ChildVariable = "QUICKNOTES_TEST_BOOTSTRAP_CHILD";

    [Fact]
    public void ModuleInitialization_IsolatesProfileWithoutStartingWpf()
    {
        if (IsChild(nameof(ModuleInitialization_IsolatesProfileWithoutStartingWpf)))
        {
            // A fresh testhost makes this independent of preceding UI tests.
            // Loading the assembly must not start WPF, even for non-UI tests.
            Assert.Null(Application.Current);
            string profile = Assert.IsType<string>(QuickNotesDbContext.ProfileDirectoryOverride);
            Assert.True(Directory.Exists(profile));
            Assert.Equal(Path.Combine(profile, "Logs"), ErrorLogService.LogDirectoryOverride);
            Assert.NotEqual(QuickNotesDbContext.LiveProfileDirectory, profile);
            return;
        }

        RunInFreshTestHost(nameof(ModuleInitialization_IsolatesProfileWithoutStartingWpf));
    }

    [Fact]
    public void StaDispatcher_LoadsResourcesWithoutRunningProductionStartup()
    {
        if (IsChild(nameof(StaDispatcher_LoadsResourcesWithoutRunningProductionStartup)))
        {
            Assert.Null(Application.Current);
            StaTestHarness.Run(() =>
            {
                Application app = Assert.IsType<QuickNotes.App.App>(Application.Current);
                Assert.True(app.Dispatcher.CheckAccess());
                Assert.Equal(ApartmentState.STA, Thread.CurrentThread.GetApartmentState());
                Assert.True(app.Resources.Contains("InkBrush"));
                Assert.Null(app.MainWindow);
                Assert.Empty(app.Windows.Cast<Window>());
            });
            StaTestHarness.Run(() => Assert.Null(Assert.IsType<QuickNotes.App.App>(Application.Current).MainWindow));
            return;
        }

        RunInFreshTestHost(nameof(StaDispatcher_LoadsResourcesWithoutRunningProductionStartup));
    }

    [Fact]
    public void BenchmarkRunner_RunBeforeOtherUiTests_KeepsTheApplicationDispatcherPumping()
        => AssertBenchmarkKeepsDispatcherPumping(
            nameof(BenchmarkRunner_RunBeforeOtherUiTests_KeepsTheApplicationDispatcherPumping),
            () => new PerformanceBaselineSmokeTests().SmokeBenchmarkSuite_CompletesQuicklyWithValidMetrics());

    [Fact]
    public void BenchmarkTool_RunBeforeOtherUiTests_KeepsTheApplicationDispatcherPumping()
        => AssertBenchmarkKeepsDispatcherPumping(
            nameof(BenchmarkTool_RunBeforeOtherUiTests_KeepsTheApplicationDispatcherPumping),
            () => new PerformanceBaselineSmokeTests().PerformanceBenchmarkTool_WhenOptionalUiMetricUnavailable_ExitsSuccessfully());

    private static void AssertBenchmarkKeepsDispatcherPumping(string testName, Action benchmark)
    {
        if (IsChild(testName))
        {
            Assert.Null(Application.Current);
            benchmark();

            Application app = StaTestHarness.EnsureApplication();
            StaTestHarness.Run(() =>
            {
                Assert.Same(app, Application.Current);
                Assert.True(app.Dispatcher.CheckAccess());
                Assert.False(app.Dispatcher.HasShutdownStarted);
                Assert.True(app.Resources.Contains("InkBrush"));
            });
            return;
        }

        // Each entry point runs first in its own testhost, including its isolated
        // GUI processes, before checking subsequent STA work.
        RunInFreshTestHost(testName, iterations: 1, timeout: TimeSpan.FromSeconds(120));
    }

    private static bool IsChild(string testName)
        => Environment.GetEnvironmentVariable(ChildVariable) == testName;

    private static void RunInFreshTestHost(string testName, int iterations = 3, TimeSpan? timeout = null)
    {
        for (int iteration = 0; iteration < iterations; iteration++)
        {
            string results = Path.Combine(Path.GetTempPath(), "qn-bootstrap-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(results);
            try
            {
                var start = new ProcessStartInfo("dotnet")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    ArgumentList =
                    {
                        "vstest",
                        typeof(TestHostIsolationTests).Assembly.Location,
                        "--TestCaseFilter:FullyQualifiedName=" + typeof(TestHostIsolationTests).FullName + "." +
                            testName,
                        "--Logger:trx;LogFileName=bootstrap.trx",
                        "--ResultsDirectory:" + results
                    }
                };
                start.Environment[ChildVariable] = testName;
                var result = ProcessTestHarness.Run(start, timeout ?? TimeSpan.FromSeconds(60));
                Assert.True(result.ExitCode == 0,
                    $"Bootstrap iteration {iteration}: {result.StandardOutput}\n{result.StandardError}");

                XDocument report = XDocument.Load(Path.Combine(results, "bootstrap.trx"));
                XNamespace ns = report.Root!.Name.Namespace;
                XElement summary = Assert.Single(report.Root.Elements(ns + "ResultSummary"));
                XElement counters = Assert.Single(summary.Elements(ns + "Counters"));
                Assert.Equal("Completed", (string?)summary.Attribute("outcome"));
                Assert.Equal("1", (string?)counters.Attribute("total"));
                Assert.Equal("1", (string?)counters.Attribute("executed"));
                Assert.Equal("1", (string?)counters.Attribute("passed"));
            }
            finally
            {
                Directory.Delete(results, recursive: true);
            }
        }
    }
}
