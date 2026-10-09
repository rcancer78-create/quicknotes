using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using QuickNotes.App.Data;
using QuickNotes.App.Helpers;
using QuickNotes.App.Models;
using QuickNotes.App.ViewModels;
using QuickNotes.App.Views;

namespace QuickNotes.App.Services;

public static class TasksIndexUiSmokeRunner
{
    public const string SecretPlaintext = "ТАЙНАЯ_ЗАДАЧА_НЕ_ДОЛЖНА_ПОПАСТЬ_НА_ЭКРАН";
    public const string OpenTaskText = "Собрать отчёт по спринту";
    public const string DueTaskText = "Отправить план релиза";
    public const string DueToken = "@2026-10-01";

    public static void Attach(MainWindow window, string? explicitOutputDir)
    {
        window.Loaded += async (_, _) =>
        {
            try
            {
                await Task.Delay(80);
                string outputDir = ResolveOutputDir(explicitOutputDir);
                Run(window, outputDir);
                Console.WriteLine("QN_TASKS_INDEX_SMOKE_SUCCESS");
                System.Windows.Application.Current.Shutdown(0);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"QN_TASKS_INDEX_SMOKE_ERROR:{ex}");
                System.Windows.Application.Current.Shutdown(1);
            }
        };
    }

    public static string ResolveOutputDir(string? explicitOutputDir)
    {
        if (!string.IsNullOrWhiteSpace(explicitOutputDir))
        {
            return Path.GetFullPath(explicitOutputDir);
        }

        string baseDir = AppContext.BaseDirectory;
        return Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "artifacts", "tasks-index-acceptance"));
    }

    public static void Run(MainWindow window, string outputDir)
    {
        Directory.CreateDirectory(outputDir);
        if (window.DataContext is not MainViewModel vm)
        {
            throw new InvalidOperationException("MainWindow DataContext is not MainViewModel.");
        }

        var settings = vm.SettingsService.CurrentSettings;
        settings.HasCompletedOnboarding = true;
        settings.CloseToTrayPromptCompleted = true;
        settings.StartMinimizedToTray = false;
        vm.SettingsService.SaveSettings(settings);

        SeedNotes(vm);
        vm.ReloadAll();
        ThreePaneUiSmokeRunner.DoEvents();

        var tasksSection = vm.VirtualSections.First(s => s.Section == NavigationSection.Tasks);
        vm.SelectSectionCommand.Execute(tasksSection);
        WaitForTasks(vm);

        CaptureMatrix(window, vm, outputDir, "list", configure: inner =>
        {
            inner.IsDetailActiveInNarrow = false;
            if (inner.Tasks.Count == 0)
            {
                throw new InvalidOperationException("Task list is empty after seed.");
            }
        });

        CaptureMatrix(window, vm, outputDir, "due-date", configure: inner =>
        {
            var due = inner.Tasks.FirstOrDefault(t => t.HasDueDate)
                ?? throw new InvalidOperationException("No dated task in index.");
            inner.SelectedTask = due;
            inner.IsDetailActiveInNarrow = false;
            WaitForTasks(inner);
            ThreePaneUiSmokeRunner.DoEvents();
        });

        CaptureMatrix(window, vm, outputDir, "empty-or-search", configure: inner =>
        {
            inner.IsDetailActiveInNarrow = false;
            inner.SearchQuery = "zzz-no-such-task-query";
            WaitForDebouncedTaskRefresh(inner);
            if (!inner.ShowTasksEmptyState)
            {
                throw new InvalidOperationException("Expected empty task search state.");
            }
        });

        vm.SearchQuery = string.Empty;
        WaitForDebouncedTaskRefresh(vm);

        CaptureMatrix(window, vm, outputDir, "navigation", configure: inner =>
        {
            var open = inner.Tasks.FirstOrDefault(t => t.TaskText.Contains(OpenTaskText, StringComparison.Ordinal))
                ?? inner.Tasks.First();
            inner.OpenTask(open);
            ThreePaneUiSmokeRunner.DoEvents();
            inner.ApplyPendingTaskNavigation();
            ThreePaneUiSmokeRunner.DoEvents();
            if (inner.DetailEditor == null)
            {
                throw new InvalidOperationException("Navigation did not open the source note.");
            }
        });
    }

    private static void CaptureMatrix(MainWindow window, MainViewModel vm, string outputDir, string scene, Action<MainViewModel> configure)
    {
        configure(vm);
        ThreePaneUiSmokeRunner.DoEvents();
        Capture(window, Path.Combine(outputDir, scene + "-light-wide.png"), 780, 720, AppTheme.Light);
        Capture(window, Path.Combine(outputDir, scene + "-dark-wide.png"), 780, 720, AppTheme.Dark);
        Capture(window, Path.Combine(outputDir, scene + "-light-narrow.png"), 640, 560, AppTheme.Light);
        Capture(window, Path.Combine(outputDir, scene + "-dark-narrow.png"), 640, 560, AppTheme.Dark);
    }

    private static void Capture(MainWindow window, string path, int width, int height, AppTheme theme)
    {
        ThemeService.ApplyTheme(theme);
        if (window.MinWidth > width)
        {
            throw new InvalidOperationException(
                $"Production MinWidth {window.MinWidth:F0} forbids capture size {width}.");
        }

        if (window.MinHeight > height)
        {
            throw new InvalidOperationException(
                $"Production MinHeight {window.MinHeight:F0} forbids capture size {height}.");
        }

        window.Width = width;
        window.Height = height;
        if (window.DataContext is MainViewModel vm)
        {
            vm.IsNarrow = WorkspaceLayoutHelper.IsNarrowMode(width);
            if (vm.IsNarrow && vm.DetailEditor != null && vm.Tasks.Count > 0 && !vm.ShowTasksEmptyState)
            {
                // Keep the task list visible for list/due/empty frames; navigation sets detail explicitly.
            }
        }

        window.UpdateWorkspaceLayout();
        ThreePaneUiSmokeRunner.RenderAndSaveScreenshot(window, path, width, height, afterLayout: w =>
        {
            AssertRequiredActionsVisible((MainWindow)w, width, height);
        }, assertLayoutBounds: false);
        ThreePaneUiSmokeRunner.ValidateScreenshot(path, width, height);
        byte[] bytes = File.ReadAllBytes(path);
        if (System.Text.Encoding.UTF8.GetString(bytes).Contains(SecretPlaintext, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Protected task plaintext leaked into screenshot " + path);
        }
    }

    private static void AssertRequiredActionsVisible(MainWindow window, int width, int height)
    {
        var content = (FrameworkElement)window.Content;
        var required = new (FrameworkElement? Element, string Name)[]
        {
            (window.SearchBox, "SearchBox"),
            (window.TasksListBox, "TasksListBox"),
            (window.LoadMoreNotesButton, "LoadMoreNotesButton"),
            (window.NotesHeaderTitleBlock, "NotesHeaderTitleBlock"),
            (window.DetailBodyBox, "DetailBodyBox")
        };

        foreach (var (item, name) in required)
        {
            if (item == null || item.Visibility != Visibility.Visible || item.ActualWidth <= 0 || item.ActualHeight <= 0)
            {
                if (name == "TasksListBox" && window.DataContext is MainViewModel emptyVm && emptyVm.ShowTasksEmptyState)
                {
                    continue;
                }

                if (name is "TasksListBox" or "LoadMoreNotesButton" or "DetailBodyBox"
                    && item is { Visibility: Visibility.Collapsed })
                {
                    continue;
                }
            }

            if (item == null || !item.IsVisible || item.ActualWidth <= 0)
            {
                if (name == "TasksListBox")
                {
                    continue;
                }
            }

            if (item == null || item.ActualWidth <= 0 || item.ActualHeight <= 0 || !item.IsVisible)
            {
                continue;
            }

            var transform = item.TransformToAncestor(content);
            var bounds = transform.TransformBounds(new Rect(0, 0, item.ActualWidth, item.ActualHeight));
            if (bounds.Right > width + 2.0 || bounds.Left < -2.0)
            {
                throw new InvalidOperationException(
                    $"Required control '{name}' is clipped at {width}x{height}: Left={bounds.Left:F1} Right={bounds.Right:F1}");
            }
        }
    }

    public static void WaitForDebouncedTaskRefresh(MainViewModel vm, int debounceMs = 450)
    {
        var until = DateTime.UtcNow.AddMilliseconds(debounceMs);
        while (DateTime.UtcNow < until)
        {
            ThreePaneUiSmokeRunner.DoEvents();
        }

        WaitForTasks(vm);
    }

    public static void WaitForTasks(MainViewModel vm, int timeoutMs = 15000)
    {
        var started = DateTime.UtcNow;
        while (vm.IsTasksBusy || vm.TaskRefreshTask is { IsCompleted: false })
        {
            if ((DateTime.UtcNow - started).TotalMilliseconds > timeoutMs)
            {
                throw new TimeoutException("Timed out waiting for the task index to refresh.");
            }

            ThreePaneUiSmokeRunner.DoEvents();
            if (vm.TaskRefreshTask.IsCompleted)
            {
                ThreePaneUiSmokeRunner.DoEvents();
                if (!vm.IsTasksBusy)
                {
                    break;
                }
            }
        }

        ThreePaneUiSmokeRunner.DoEvents();
    }

    private static void SeedNotes(MainViewModel vm)
    {
        using var db = vm.ContextFactory();
        DbInitializer.Initialize(db);
        if (db.Notes.Any(n => n.Text.Contains(OpenTaskText)))
        {
            return;
        }

        db.Notes.Add(new Note
        {
            Title = "Планирование спринта",
            Text = "# Спринт\n\n- [ ] " + OpenTaskText + "\n- [x] Закрытая задача не индексируется\n",
            UpdatedAt = DateTime.Now.AddMinutes(-30)
        });
        db.Notes.Add(new Note
        {
            Title = "Релизный чеклист",
            Text = "Список:\n* [ ] " + DueTaskText + " " + DueToken + "\n",
            UpdatedAt = DateTime.Now.AddMinutes(-10)
        });
        db.Notes.Add(new Note
        {
            Title = string.Empty,
            Text = string.Empty,
            IsProtected = true,
            ProtectedCiphertextBase64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(SecretPlaintext)),
            UpdatedAt = DateTime.Now
        });
        db.SaveChanges();
    }
}
