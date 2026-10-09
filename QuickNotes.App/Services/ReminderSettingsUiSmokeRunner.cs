using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using QuickNotes.App.Helpers;
using QuickNotes.App.Models;
using QuickNotes.App.ViewModels;
using QuickNotes.App.Views;

namespace QuickNotes.App.Services;

public static class ReminderSettingsUiSmokeRunner
{
    public static void Attach(MainWindow window, string? explicitOutputDir)
    {
        window.Loaded += async (_, _) =>
        {
            try
            {
                await Task.Delay(80);
                string outputDir = ResolveOutputDir(explicitOutputDir);
                Run(window, outputDir);
                Console.WriteLine("QN_REMINDERS_SMOKE_SUCCESS");
                System.Windows.Application.Current.Shutdown(0);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"QN_REMINDERS_SMOKE_ERROR:{ex}");
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
        return Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "artifacts", "reminders-acceptance"));
    }

    public static void Run(MainWindow window, string outputDir)
    {
        Directory.CreateDirectory(outputDir);
        if (window.DataContext is not MainViewModel mainVm)
        {
            throw new InvalidOperationException("MainWindow DataContext is not MainViewModel.");
        }

        var settings = mainVm.SettingsService.CurrentSettings;
        settings.HasCompletedOnboarding = true;
        settings.CloseToTrayPromptCompleted = true;
        settings.StartMinimizedToTray = false;
        settings.LocalRemindersEnabled = true;
        mainVm.SettingsService.SaveSettings(settings);

        var tasksSection = mainVm.VirtualSections[1];
        mainVm.SelectSectionCommand.Execute(tasksSection);
        ThreePaneUiSmokeRunner.DoEvents();

        using (var settingsVm = new SettingsViewModelDisposable(mainVm.CreateSettingsViewModel()))
        {
            CaptureSettingsMatrix(settingsVm.ViewModel, outputDir, "enabled", enabled: true);
            CaptureSettingsMatrix(settingsVm.ViewModel, outputDir, "disabled", enabled: false);
        }
    }

    public static void CaptureSettingsMatrix(SettingsViewModel vm, string outputDir, string scenario, bool enabled)
    {
        vm.SettingsSectionIndex = 0;
        vm.LocalRemindersEnabled = enabled;
        foreach (var (theme, themeName) in new[] { (AppTheme.Light, "light"), (AppTheme.Dark, "dark") })
        {
            ThemeService.ApplyTheme(theme);
            foreach (var (width, height, sizeName) in new[] { (820, 780, "wide"), (640, 560, "narrow") })
            {
                var window = new SettingsWindow(vm)
                {
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = 40,
                    Top = 40
                };
                try
                {
                    window.Show();
                    ThreePaneUiSmokeRunner.DoEvents();
                    string path = Path.Combine(outputDir, $"reminders-{scenario}-{themeName}-{sizeName}.png");
                    ThreePaneUiSmokeRunner.RenderAndSaveScreenshot(window, path, width, height, afterLayout: w =>
                    {
                        if (w is not SettingsWindow sw)
                        {
                            return;
                        }

                        sw.LocalRemindersEnabledCheckBox.BringIntoView();
                        sw.LocalRemindersStatusText.BringIntoView();
                        ThreePaneUiSmokeRunner.DoEvents();
                        if (sw.LocalRemindersEnabledCheckBox.ActualHeight <= 0)
                        {
                            throw new InvalidOperationException("Reminder toggle is not laid out.");
                        }

                        var content = (FrameworkElement)sw.Content;
                        var bounds = sw.LocalRemindersEnabledCheckBox.TransformToAncestor(content)
                            .TransformBounds(new Rect(0, 0, sw.LocalRemindersEnabledCheckBox.ActualWidth, sw.LocalRemindersEnabledCheckBox.ActualHeight));
                        if (bounds.Bottom < 0 || bounds.Top > height)
                        {
                            throw new InvalidOperationException("Reminder toggle is outside the captured window.");
                        }
                    }, assertLayoutBounds: false);
                    ThreePaneUiSmokeRunner.ValidateScreenshot(path, width, height);
                }
                finally
                {
                    window.Close();
                    ThreePaneUiSmokeRunner.DoEvents();
                }
            }
        }

        ThemeService.ApplyTheme(AppTheme.Light);
    }

    private sealed class SettingsViewModelDisposable : IDisposable
    {
        public SettingsViewModel ViewModel { get; }
        public SettingsViewModelDisposable(SettingsViewModel vm) => ViewModel = vm;
        public void Dispose() { }
    }
}
