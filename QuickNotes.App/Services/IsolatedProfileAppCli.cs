using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using QuickNotes.App.Data;
using QuickNotes.App.Views;

namespace QuickNotes.App.Services;

public sealed class IsolatedProfileAppOptions
{
    public bool IsIsolated { get; init; }
    public string? ProfileDirectory { get; init; }
    public bool PerfStartup { get; init; }
    public bool PerfScroll { get; init; }
    public bool ThreePaneSmoke { get; init; }
    public bool ImportMigrationSmoke { get; init; }
    public bool NoteAssemblySmoke { get; init; }
    public bool TasksIndexSmoke { get; init; }
    public bool RemindersSmoke { get; init; }
    public bool SyncConflictSmoke { get; init; }
    public bool UxPackageAbSmoke { get; init; }
    public bool UxC08Smoke { get; init; }
    public bool UxC11Smoke { get; init; }
    public bool UxC12Smoke { get; init; }
    public bool UxC13Smoke { get; init; }
    public bool UxC14Smoke { get; init; }
    public bool UxPackageDLayoutSmoke { get; init; }
    public bool WorkspaceBoardSmoke { get; init; }
    public bool ManualAcceptancePrep { get; init; }
    public string? ArtifactsOutputDir { get; init; }
}

public sealed class StartupPhaseRecorder
{
    private readonly Stopwatch _stopwatch;
    private readonly List<(string Name, long ElapsedMs)> _marks = new();

    public StartupPhaseRecorder(Stopwatch stopwatch)
    {
        _stopwatch = stopwatch;
    }

    public void Mark(string name)
    {
        _marks.Add((name, _stopwatch.ElapsedMilliseconds));
    }

    public IReadOnlyList<(string Name, long ElapsedMs)> Marks => _marks;
}

public static class IsolatedProfileAppCli
{
    public static StartupPhaseRecorder BeginStartupPhases(Stopwatch startupStopwatch)
    {
        return new StartupPhaseRecorder(startupStopwatch);
    }

    public static IsolatedProfileAppOptions Parse(string[]? args)
    {
        if (args == null || args.Length == 0)
        {
            return new IsolatedProfileAppOptions();
        }

        string? dir = null;
        bool perfStartup = false;
        bool perfScroll = false;
        bool threePaneSmoke = false;
        bool importMigrationSmoke = false;
        bool noteAssemblySmoke = false;
        bool tasksIndexSmoke = false;
        bool remindersSmoke = false;
        bool syncConflictsSmoke = false;
        bool uxPackageAbSmoke = false;
        bool uxC08Smoke = false;
        bool uxC11Smoke = false;
        bool uxC12Smoke = false;
        bool uxC13Smoke = false;
        bool uxC14Smoke = false;
        bool uxPackageDLayoutSmoke = false;
        bool workspaceBoardSmoke = false;
        bool manualAcceptancePrep = false;
        string? outputDir = null;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (string.Equals(arg, "--isolated-profile", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                dir = args[++i];
            }
            else if (string.Equals(arg, "--perf-startup", StringComparison.OrdinalIgnoreCase))
            {
                perfStartup = true;
            }
            else if (string.Equals(arg, "--perf-scroll", StringComparison.OrdinalIgnoreCase))
            {
                perfScroll = true;
            }
            else if (string.Equals(arg, "--three-pane-smoke", StringComparison.OrdinalIgnoreCase))
            {
                threePaneSmoke = true;
            }
            else if (string.Equals(arg, "--import-migration-smoke", StringComparison.OrdinalIgnoreCase))
            {
                importMigrationSmoke = true;
            }
            else if (string.Equals(arg, "--quick-note-assembly-smoke", StringComparison.OrdinalIgnoreCase))
            {
                noteAssemblySmoke = true;
            }
            else if (string.Equals(arg, "--tasks-index-smoke", StringComparison.OrdinalIgnoreCase))
            {
                tasksIndexSmoke = true;
            }
            else if (string.Equals(arg, "--reminders-smoke", StringComparison.OrdinalIgnoreCase))
            {
                remindersSmoke = true;
            }
            else if (string.Equals(arg, "--sync-conflict-smoke", StringComparison.OrdinalIgnoreCase)
                     || string.Equals(arg, "--sync-conflicts-smoke", StringComparison.OrdinalIgnoreCase))
            {
                syncConflictsSmoke = true;
            }
            else if (string.Equals(arg, "--ux-package-ab-smoke", StringComparison.OrdinalIgnoreCase))
            {
                uxPackageAbSmoke = true;
            }
            else if (string.Equals(arg, "--ux-c08-smoke", StringComparison.OrdinalIgnoreCase))
            {
                uxC08Smoke = true;
            }
            else if (string.Equals(arg, "--ux-c11-smoke", StringComparison.OrdinalIgnoreCase))
            {
                uxC11Smoke = true;
            }
            else if (string.Equals(arg, "--ux-c12-smoke", StringComparison.OrdinalIgnoreCase))
            {
                uxC12Smoke = true;
            }
            else if (string.Equals(arg, "--ux-c13-smoke", StringComparison.OrdinalIgnoreCase))
            {
                uxC13Smoke = true;
            }
            else if (string.Equals(arg, "--ux-c14-smoke", StringComparison.OrdinalIgnoreCase))
            {
                uxC14Smoke = true;
            }
            else if (string.Equals(arg, "--ux-package-d-layout-smoke", StringComparison.OrdinalIgnoreCase))
            {
                uxPackageDLayoutSmoke = true;
            }
            else if (string.Equals(arg, "--workspace-board-smoke", StringComparison.OrdinalIgnoreCase))
            {
                workspaceBoardSmoke = true;
            }
            else if (string.Equals(arg, "--manual-acceptance-prep", StringComparison.OrdinalIgnoreCase))
            {
                manualAcceptancePrep = true;
            }
            else if (string.Equals(arg, "--output-dir", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                outputDir = args[++i];
            }
        }

        if (string.IsNullOrWhiteSpace(dir))
        {
            return new IsolatedProfileAppOptions();
        }

        return new IsolatedProfileAppOptions
        {
            IsIsolated = true,
            ProfileDirectory = Path.GetFullPath(dir),
            PerfStartup = perfStartup,
            PerfScroll = perfScroll,
            ThreePaneSmoke = threePaneSmoke,
            ImportMigrationSmoke = importMigrationSmoke,
            NoteAssemblySmoke = noteAssemblySmoke,
            TasksIndexSmoke = tasksIndexSmoke,
            RemindersSmoke = remindersSmoke,
            SyncConflictSmoke = syncConflictsSmoke,
            UxPackageAbSmoke = uxPackageAbSmoke,
            UxC08Smoke = uxC08Smoke,
            UxC11Smoke = uxC11Smoke,
            UxC12Smoke = uxC12Smoke,
            UxC13Smoke = uxC13Smoke,
            UxC14Smoke = uxC14Smoke,
            UxPackageDLayoutSmoke = uxPackageDLayoutSmoke,
            WorkspaceBoardSmoke = workspaceBoardSmoke,
            ManualAcceptancePrep = manualAcceptancePrep,
            ArtifactsOutputDir = outputDir
        };
    }

    public static void AttachThreePaneSmoke(MainWindow window, string? outputDir)
    {
        ThreePaneUiSmokeRunner.Attach(window, outputDir);
    }

    public static void AttachImportMigrationSmoke(MainWindow window, string? outputDir)
    {
        ImportMigrationUiSmokeRunner.Attach(window, outputDir);
    }

    public static void AttachNoteAssemblySmoke(MainWindow window, string? outputDir)
    {
        NoteAssemblyUiSmokeRunner.Attach(window, outputDir);
    }

    public static void AttachTasksIndexSmoke(MainWindow window, string? outputDir)
    {
        TasksIndexUiSmokeRunner.Attach(window, outputDir);
    }

    public static void AttachRemindersSmoke(MainWindow window, string? outputDir)
    {
        ReminderSettingsUiSmokeRunner.Attach(window, outputDir);
    }

    public static void AttachSyncConflictSmoke(MainWindow window, string? outputDir)
    {
        SyncConflictUiSmokeRunner.Attach(window, outputDir);
    }

    public static void AttachUxPackageAbSmoke(MainWindow window, string? outputDir)
    {
        UxPackageAbUiSmokeRunner.Attach(window, outputDir);
    }

    public static void AttachUxC08Smoke(MainWindow window, string? outputDir)
    {
        UxC08UiSmokeRunner.Attach(window, outputDir);
    }

    public static void AttachUxC11Smoke(MainWindow window, string? outputDir)
    {
        UxC11UiSmokeRunner.Attach(window, outputDir);
    }

    public static void AttachUxC12Smoke(MainWindow window, string? outputDir)
    {
        UxC12UiSmokeRunner.Attach(window, outputDir);
    }

    public static void AttachUxC13Smoke(MainWindow window, string? outputDir)
    {
        UxC13UiSmokeRunner.Attach(window, outputDir);
    }

    public static void AttachUxC14Smoke(MainWindow window, string? outputDir)
    {
        UxC14UiSmokeRunner.Attach(window, outputDir);
    }

    public static void AttachUxPackageDLayoutSmoke(MainWindow window, string? outputDir)
    {
        UxPackageDUiSmokeRunner.Attach(window, outputDir);
    }

    public static void AttachWorkspaceBoardSmoke(MainWindow window, string? outputDir)
    {
        WorkspaceBoardUiSmokeRunner.Attach(window, outputDir);
    }

    public static void AttachManualAcceptancePrep(MainWindow window, string? outputDir)
    {
        ManualAcceptancePrepRunner.Attach(window, outputDir);
    }

    public static void AttachStartupBenchmark(
        MainWindow window,
        Stopwatch startupStopwatch,
        StartupPhaseRecorder? phases = null)
    {
        window.Loaded += (_, _) =>
        {
            window.Dispatcher.InvokeAsync(() =>
            {
                window.SearchBox.Focus();
                startupStopwatch.Stop();
                phases?.Mark("searchbox_focused");

                var proc = Process.GetCurrentProcess();
                proc.Refresh();
                long workingSet = proc.WorkingSet64;

                if (phases != null)
                {
                    foreach (var (name, elapsedMs) in phases.Marks)
                    {
                        Console.WriteLine($"QN_PERF_PHASE:{name}:{elapsedMs}");
                    }
                }

                Console.WriteLine($"QN_PERF_STARTUP_READY_MS:{startupStopwatch.ElapsedMilliseconds}");
                Console.WriteLine($"QN_PERF_WORKING_SET_BYTES:{workingSet}");

                System.Windows.Application.Current.Shutdown(0);
            }, DispatcherPriority.Input);
        };
    }

    public static void AttachScrollBenchmark(MainWindow window)
    {
        window.Loaded += async (_, _) =>
        {
            await Task.Delay(200);

            var listBox = window.NotesListBox;
            var scrollViewer = FindVisualChild<ScrollViewer>(listBox);
            if (scrollViewer == null)
            {
                Console.WriteLine("QN_PERF_SCROLL_UNAVAILABLE:ScrollViewerNotFound");
                System.Windows.Application.Current.Shutdown(0);
                return;
            }

            var frameTimes = new List<long>();
            var sw = Stopwatch.StartNew();
            long lastTicks = sw.ElapsedTicks;

            EventHandler renderingHandler = (_, _) =>
            {
                long currentTicks = sw.ElapsedTicks;
                frameTimes.Add(currentTicks - lastTicks);
                lastTicks = currentTicks;
            };

            CompositionTarget.Rendering += renderingHandler;
            try
            {
                double maxScroll = scrollViewer.ScrollableHeight;
                if (maxScroll <= 0)
                {
                    Console.WriteLine("QN_PERF_SCROLL_UNAVAILABLE:NoScrollableContent");
                    System.Windows.Application.Current.Shutdown(0);
                    return;
                }

                int steps = 40;
                double stepDelta = maxScroll / steps;
                for (int i = 0; i <= steps; i++)
                {
                    scrollViewer.ScrollToVerticalOffset(i * stepDelta);
                    await Task.Delay(16);
                }
                for (int i = steps; i >= 0; i--)
                {
                    scrollViewer.ScrollToVerticalOffset(i * stepDelta);
                    await Task.Delay(16);
                }
            }
            finally
            {
                CompositionTarget.Rendering -= renderingHandler;
            }

            if (frameTimes.Count > 10)
            {
                var deltasMs = frameTimes.Skip(2)
                    .Select(t => (double)t / Stopwatch.Frequency * 1000.0)
                    .OrderBy(x => x)
                    .ToList();

                double medianDelta = deltasMs[deltasMs.Count / 2];
                double fps = medianDelta > 0 ? 1000.0 / medianDelta : 0;
                int droppedFrames = deltasMs.Count(d => d > 33.3);

                Console.WriteLine($"QN_PERF_SCROLL_SAMPLES:{deltasMs.Count}");
                Console.WriteLine($"QN_PERF_SCROLL_MEDIAN_DELTA_MS:{medianDelta:F2}");
                Console.WriteLine($"QN_PERF_SCROLL_MEDIAN_FPS:{fps:F1}");
                Console.WriteLine($"QN_PERF_SCROLL_DROPPED_FRAMES:{droppedFrames}");
            }
            else
            {
                Console.WriteLine("QN_PERF_SCROLL_UNAVAILABLE:InsufficientFrames");
            }

            System.Windows.Application.Current.Shutdown(0);
        };
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typedChild)
            {
                return typedChild;
            }

            var result = FindVisualChild<T>(child);
            if (result != null)
            {
                return result;
            }
        }
        return null;
    }
}
