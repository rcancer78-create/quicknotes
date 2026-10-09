using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using QuickNotes.App.Helpers;
using QuickNotes.App.Models;
using QuickNotes.App.ViewModels;
using QuickNotes.App.Views;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using Expander = System.Windows.Controls.Expander;
using TextBlock = System.Windows.Controls.TextBlock;
using TextBox = System.Windows.Controls.TextBox;

namespace QuickNotes.App.Services;

public static class UxC13UiSmokeRunner
{
    public const string SuccessMarker = "QN_UX_C13_SMOKE_SUCCESS";

    public static readonly string[] ExpectedFileNames =
    {
        "cloud-first-light-wide.png", "cloud-first-dark-wide.png",
        "cloud-advanced-light-wide.png", "cloud-advanced-dark-wide.png",
        "transfer-export-light-wide.png", "transfer-export-dark-wide.png",
        "transfer-archive-light-wide.png", "transfer-archive-dark-wide.png"
    };

    public static void Attach(MainWindow window, string? explicitOutputDir)
    {
        window.Loaded += async (_, _) =>
        {
            try
            {
                SuppressBlockingFirstRun(window);
                await Task.Delay(80);
                string outputDir = ResolveOutputDir(explicitOutputDir);
                Run(window, outputDir);
                Console.WriteLine(SuccessMarker);
                System.Windows.Application.Current.Shutdown(0);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"QN_UX_C13_SMOKE_ERROR:{ex}");
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
        return Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "artifacts", "ux-c13-acceptance"));
    }

    public static void ValidateOutput(string path, string fileName)
    {
        var (width, height) = ExpectedSize(fileName);
        ThreePaneUiSmokeRunner.ValidateScreenshot(path, width, height, minBytes: 20_000, minDistinctColors: 25);
    }

    public static (int Width, int Height) ExpectedSize(string fileName)
    {
        return fileName.StartsWith("transfer-", StringComparison.Ordinal)
            ? (780, 720)
            : (820, 780);
    }

    public static string VisibleTextDumpPath(string pngPath) => Path.ChangeExtension(pngPath, ".visible.txt");

    public static void Run(MainWindow window, string outputDir)
    {
        Directory.CreateDirectory(outputDir);
        if (window.DataContext is not MainViewModel vm)
        {
            throw new InvalidOperationException("MainWindow DataContext is not MainViewModel.");
        }

        SuppressBlockingFirstRun(window);
        CaptureCloud(vm, outputDir);
        CaptureTransfer(vm, outputDir);
        ThemeService.ApplyTheme(AppTheme.Light);
    }

    private static void SuppressBlockingFirstRun(MainWindow window)
    {
        if (window.DataContext is not MainViewModel vm)
        {
            return;
        }

        var settings = vm.SettingsService.CurrentSettings;
        settings.HasCompletedOnboarding = true;
        settings.CloseToTrayPromptCompleted = true;
        settings.StartMinimizedToTray = false;
        vm.SettingsService.SaveSettings(settings);
    }

    private static void CaptureCloud(MainViewModel mainVm, string outputDir)
    {
        var settingsVm = mainVm.CreateSettingsViewModel();
        settingsVm.SettingsSectionIndex = 1;

        CaptureSettings(settingsVm, outputDir, "cloud-first-light-wide.png", AppTheme.Light, expanded: false);
        CaptureSettings(settingsVm, outputDir, "cloud-first-dark-wide.png", AppTheme.Dark, expanded: false);
        CaptureSettings(settingsVm, outputDir, "cloud-advanced-light-wide.png", AppTheme.Light, expanded: true);
        CaptureSettings(settingsVm, outputDir, "cloud-advanced-dark-wide.png", AppTheme.Dark, expanded: true);
    }

    private static void CaptureSettings(SettingsViewModel vm, string outputDir, string fileName, AppTheme theme, bool expanded)
    {
        ThemeService.ApplyTheme(theme);
        vm.SettingsSectionIndex = 1;
        var window = new SettingsWindow(vm)
        {
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual
        };
        try
        {
            window.Show();
            ThreePaneUiSmokeRunner.DoEvents();
            window.CloudAdvancedExpander.IsExpanded = expanded;
            ThreePaneUiSmokeRunner.DoEvents();

            var (width, height) = ExpectedSize(fileName);
            string path = Path.Combine(outputDir, fileName);
            ThreePaneUiSmokeRunner.RenderAndSaveScreenshot(window, path, width, height, w =>
            {
                if (w is not SettingsWindow sw)
                {
                    return;
                }

                sw.CloudAdvancedExpander.IsExpanded = expanded;
                if (expanded)
                {
                    sw.SecretAccessKeyBox.BringIntoView();
                }
                else
                {
                    sw.CloudWizardButton.BringIntoView();
                }

                ThreePaneUiSmokeRunner.DoEvents();
                w.UpdateLayout();
                string visible = expanded
                    ? UxPackageAbUiSmokeRunner.CollectVisibleUiText(w)
                    : CollectLaidOutCopy(w, includeCollapsedExpander: false);
                if (expanded)
                {
                    AssertAdvancedVisible(visible);
                }
                else
                {
                    AssertFirstLevelVisible(visible);
                    if (sw.CloudAdvancedExpander.IsExpanded)
                    {
                        throw new InvalidOperationException("Cloud first-level smoke must keep Дополнительно collapsed.");
                    }
                }

                File.WriteAllText(VisibleTextDumpPath(path), visible, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            }, assertLayoutBounds: false);
            ValidateOutput(path, fileName);
        }
        finally
        {
            window.Close();
            ThreePaneUiSmokeRunner.DoEvents();
        }
    }

    private static void CaptureTransfer(MainViewModel mainVm, string outputDir)
    {
        var vm = new ImportExportViewModel(
            mainVm.ContextFactory,
            mainVm.MutationCoordinator,
            archiveService: null,
            attachmentStorage: mainVm.AttachmentStorageService,
            settingsService: mainVm.SettingsService);

        var window = new ImportExportWindow(vm)
        {
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual
        };
        try
        {
            window.Show();
            ThreePaneUiSmokeRunner.DoEvents();
            foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark })
            {
                string suffix = theme == AppTheme.Dark ? "dark" : "light";
                CaptureTransferTab(window, vm, outputDir, $"transfer-export-{suffix}-wide.png", tabIndex: 0, UserTaskCopy.ExportTab, UserTaskCopy.ExportLead, theme);
                CaptureTransferTab(window, vm, outputDir, $"transfer-archive-{suffix}-wide.png", tabIndex: 2, UserTaskCopy.ArchiveTab, "Recovery key", theme);
            }
        }
        finally
        {
            window.Close();
            ThreePaneUiSmokeRunner.DoEvents();
        }
    }

    private static void CaptureTransferTab(
        ImportExportWindow window,
        ImportExportViewModel vm,
        string outputDir,
        string fileName,
        int tabIndex,
        string requiredTab,
        string requiredBody,
        AppTheme theme)
    {
        ThemeService.ApplyTheme(theme);
        vm.SelectedTabIndex = tabIndex;
        ThreePaneUiSmokeRunner.DoEvents();
        var (width, height) = ExpectedSize(fileName);
        string path = Path.Combine(outputDir, fileName);
        ThreePaneUiSmokeRunner.RenderAndSaveScreenshot(window, path, width, height, w =>
        {
            vm.SelectedTabIndex = tabIndex;
            w.UpdateLayout();
            string visible = UxPackageAbUiSmokeRunner.CollectVisibleUiText(w);
            if (string.IsNullOrWhiteSpace(visible)
                || !visible.Contains(UserTaskCopy.TransferWindowTitle, StringComparison.Ordinal)
                || !visible.Contains(requiredTab, StringComparison.Ordinal)
                || !visible.Contains(requiredBody, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Transfer smoke is missing first-level copy: " + fileName);
            }

            File.WriteAllText(VisibleTextDumpPath(path), visible, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }, assertLayoutBounds: false);
        ValidateOutput(path, fileName);
    }

    private static string CollectLaidOutCopy(DependencyObject root, bool includeCollapsedExpander)
    {
        var parts = new List<string>();
        CollectLaidOutCopyCore(root, includeCollapsedExpander, parts);
        return string.Join("\n", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
    }

    private static void CollectLaidOutCopyCore(DependencyObject parent, bool includeCollapsedExpander, List<string> parts)
    {
        if (parent is UIElement element && element.Visibility != Visibility.Visible)
        {
            return;
        }

        if (parent is Expander expander && !expander.IsExpanded && !includeCollapsedExpander)
        {
            if (expander.Header is string header)
            {
                parts.Add(header);
            }

            return;
        }

        switch (parent)
        {
            case TextBlock textBlock:
                parts.Add(textBlock.Text ?? string.Empty);
                break;
            case TextBox textBox:
                parts.Add(textBox.Text ?? string.Empty);
                break;
            case AccessText accessText:
                parts.Add(accessText.Text ?? string.Empty);
                break;
            case CheckBox checkBox when checkBox.Content is string checkText:
                parts.Add(checkText);
                break;
            case Button button when button.Content is string buttonText:
                parts.Add(buttonText);
                break;
            case HeaderedContentControl headered when headered.Header is string headerText:
                parts.Add(headerText);
                break;
        }

        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            CollectLaidOutCopyCore(VisualTreeHelper.GetChild(parent, i), includeCollapsedExpander, parts);
        }
    }

    private static void AssertFirstLevelVisible(string visible)
    {
        if (string.IsNullOrWhiteSpace(visible)
            || !visible.Contains(UserTaskCopy.CloudSectionTitle, StringComparison.Ordinal)
            || !visible.Contains(UserTaskCopy.CloudWizardButton, StringComparison.Ordinal)
            || !visible.Contains(UserTaskCopy.CloudEnableCheckbox, StringComparison.Ordinal)
            || !visible.Contains(UserTaskCopy.CloudCryptoWarning, StringComparison.Ordinal)
            || !visible.Contains(UserTaskCopy.CloudAdvancedHeader, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Cloud first-level smoke must show user-task copy, wizard and crypto warning.");
        }

        if (visible.Contains("Access Key ID", StringComparison.Ordinal)
            || visible.Contains("Endpoint", StringComparison.Ordinal)
            || visible.Contains("Yandex Object Storage", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Implementation jargon leaked onto the cloud first level.");
        }
    }

    private static void AssertAdvancedVisible(string visible)
    {
        if (!visible.Contains("Access Key ID", StringComparison.Ordinal)
            || !visible.Contains("Endpoint", StringComparison.Ordinal)
            || !visible.Contains(UserTaskCopy.CloudCryptoWarning, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Advanced cloud smoke must keep implementation params and crypto warning.");
        }
    }
}
