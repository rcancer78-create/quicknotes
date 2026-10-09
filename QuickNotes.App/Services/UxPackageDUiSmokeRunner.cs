using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using QuickNotes.App.Helpers;
using Color = System.Windows.Media.Color;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;
using QuickNotes.App.Models;
using QuickNotes.App.ViewModels;
using QuickNotes.App.Views;
using Button = System.Windows.Controls.Button;

namespace QuickNotes.App.Services;

public static class UxPackageDUiSmokeRunner
{
    public const string SuccessMarker = "QN_UX_PACKAGE_D_SMOKE_SUCCESS";
    public const string ContrastReportFileName = "contrast-report.json";
    public const string NamesReportFileName = "automation-names.json";

    public static readonly string[] ExpectedPngFileNames =
    {
        "help-light-wide.png", "help-dark-wide.png",
        "names-main-light-wide.png"
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
                Console.Error.WriteLine($"QN_UX_PACKAGE_D_SMOKE_ERROR:{ex}");
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
        return Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "artifacts", "ux-package-d-acceptance"));
    }

    public static void ValidateOutput(string path, string fileName)
    {
        var (width, height) = fileName.StartsWith("names-main", StringComparison.Ordinal)
            ? (1100, 720)
            : (640, 560);
        ThreePaneUiSmokeRunner.ValidateScreenshot(path, width, height, minBytes: 15_000, minDistinctColors: 20);
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
        UxC14UiSmokeRunner.EnsureSeeded(vm);
        WriteContrastReport(outputDir);
        CaptureHelp(outputDir);
        WriteNamesReport(window, vm, outputDir);
        ThemeService.ApplyTheme(AppTheme.Light);
    }

    private static void WriteContrastReport(string outputDir)
    {
        var app = System.Windows.Application.Current
            ?? throw new InvalidOperationException("WPF Application is required for contrast measurement.");

        var themes = new List<ThemeContrastReport>();
        foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark })
        {
            ThemeService.ApplyTheme(theme);
            ThreePaneUiSmokeRunner.DoEvents();
            var pairs = MeasurePairs(app);
            foreach (var pair in pairs.Where(p => p.Role == "required"))
            {
                if (pair.Ratio < ColorContrast.AaNormalText)
                {
                    throw new InvalidOperationException(
                        $"Required contrast failed for {theme} {pair.Foreground}/{pair.Background}: {pair.Ratio:0.00}");
                }
            }

            themes.Add(new ThemeContrastReport(theme.ToString(), ThemeService.IsDark, pairs));
        }

        var report = new ContrastReport(
            DateTime.UtcNow.ToString("o"),
            "WCAG 2 relative luminance of theme SolidColorBrush values. Not a physical pixel meter, DPI capture, or High Contrast probe.",
            ColorContrast.AaNormalText,
            ColorContrast.AaLargeOrUi,
            themes);

        string json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(outputDir, ContrastReportFileName), json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static List<ContrastPairReport> MeasurePairs(System.Windows.Application app)
    {
        Color Ink() => BrushColor(app, "InkBrush");
        Color Canvas() => BrushColor(app, "CanvasBrush");
        Color Surface() => BrushColor(app, "SurfaceBrush");
        Color SurfaceAlt() => BrushColor(app, "SurfaceAltBrush");
        Color Card() => BrushColor(app, "CardBgBrush");
        Color Muted() => BrushColor(app, "MutedBrush");

        return new List<ContrastPairReport>
        {
            Pair("InkBrush", "CanvasBrush", Ink(), Canvas(), "required"),
            Pair("InkBrush", "SurfaceBrush", Ink(), Surface(), "required"),
            Pair("InkBrush", "CardBgBrush", Ink(), Card(), "required"),
            Pair("ButtonFgBrush", "ButtonBgBrush", BrushColor(app, "ButtonFgBrush"), BrushColor(app, "ButtonBgBrush"), "required"),
            Pair("InfoBannerFgBrush", "InfoBannerBgBrush", BrushColor(app, "InfoBannerFgBrush"), BrushColor(app, "InfoBannerBgBrush"), "required"),
            Pair("WarningBannerFgBrush", "WarningBannerBgBrush", BrushColor(app, "WarningBannerFgBrush"), BrushColor(app, "WarningBannerBgBrush"), "required"),
            Pair("ErrorBannerFgBrush", "ErrorBannerBgBrush", BrushColor(app, "ErrorBannerFgBrush"), BrushColor(app, "ErrorBannerBgBrush"), "required"),
            Pair("SuccessBannerFgBrush", "SuccessBannerBgBrush", BrushColor(app, "SuccessBannerFgBrush"), BrushColor(app, "SuccessBannerBgBrush"), "required"),
            Pair("MutedBrush", "CanvasBrush", Muted(), Canvas(), "required"),
            Pair("MutedBrush", "SurfaceBrush", Muted(), Surface(), "required"),
            Pair("MutedBrush", "SurfaceAltBrush", Muted(), SurfaceAlt(), "required"),
            Pair("MutedBrush", "CardBgBrush", Muted(), Card(), "required")
        };
    }

    private static ContrastPairReport Pair(string fgName, string bgName, Color fg, Color bg, string role)
    {
        double ratio = ColorContrast.Ratio(fg, bg);
        return new ContrastPairReport(
            fgName,
            bgName,
            Math.Round(ratio, 3),
            ratio >= ColorContrast.AaNormalText,
            ratio >= ColorContrast.AaLargeOrUi,
            role);
    }

    private static Color BrushColor(System.Windows.Application app, string key)
    {
        if (app.Resources[key] is not SolidColorBrush brush)
        {
            throw new InvalidOperationException("Missing theme brush: " + key);
        }

        return brush.Color;
    }

    private static void CaptureHelp(string outputDir)
    {
        var help = new HelpWindow(ShortcutCatalog.DefaultOcrGesture)
        {
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual
        };
        try
        {
            help.Show();
            ThreePaneUiSmokeRunner.DoEvents();
            foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark })
            {
                ThemeService.ApplyTheme(theme);
                ThreePaneUiSmokeRunner.DoEvents();
                string fileName = $"help-{(theme == AppTheme.Dark ? "dark" : "light")}-wide.png";
                string path = Path.Combine(outputDir, fileName);
                ThreePaneUiSmokeRunner.RenderAndSaveScreenshot(help, path, 640, 560, w =>
                {
                    w.UpdateLayout();
                    string visible = UxPackageAbUiSmokeRunner.CollectVisibleUiText(w);
                    if (!visible.Contains(CardGestureCopy.Expand, StringComparison.Ordinal)
                        || !visible.Contains("Enter", StringComparison.Ordinal)
                        || !visible.Contains("Ctrl+E", StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException("Help smoke must describe card open/expand gestures.");
                    }

                    File.WriteAllText(VisibleTextDumpPath(path), visible, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                }, assertLayoutBounds: false);
                ValidateOutput(path, fileName);
            }
        }
        finally
        {
            help.Close();
            ThreePaneUiSmokeRunner.DoEvents();
        }
    }

    private static void WriteNamesReport(MainWindow window, MainViewModel vm, string outputDir)
    {
        window.UpdateLayout();
        ThreePaneUiSmokeRunner.DoEvents();
        ThemeService.ApplyTheme(AppTheme.Light);
        string mainPath = Path.Combine(outputDir, "names-main-light-wide.png");
        ThreePaneUiSmokeRunner.RenderAndSaveScreenshot(window, mainPath, 1100, 720, w =>
        {
            w.UpdateLayout();
        }, assertLayoutBounds: false);
        ValidateOutput(mainPath, "names-main-light-wide.png");

        var expandNames = FindVisualChildren<Button>(window)
            .Select(AutomationProperties.GetName)
            .Where(n => n == CardGestureCopy.ExpandAutomationName || n == CardGestureCopy.CollapseAutomationName)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (expandNames.Count == 0)
        {
            throw new InvalidOperationException("Package D names smoke must find the card expand AutomationProperties.Name.");
        }

        var settingsVm = vm.CreateSettingsViewModel();
        settingsVm.SettingsSectionIndex = 1;
        var settings = new SettingsWindow(settingsVm)
        {
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual
        };
        string wizardName;
        try
        {
            settings.Show();
            ThreePaneUiSmokeRunner.DoEvents();
            wizardName = AutomationProperties.GetName(settings.CloudWizardButton);
            if (wizardName != UserTaskCopy.CloudWizardAutomationName)
            {
                throw new InvalidOperationException("Cloud wizard button is missing its AutomationProperties.Name.");
            }
        }
        finally
        {
            settings.Close();
            ThreePaneUiSmokeRunner.DoEvents();
        }

        var payload = new NamesReport(
            DateTime.UtcNow.ToString("o"),
            "WPF AutomationProperties.Name on laid-out controls. Not Narrator, NVDA, or system High Contrast.",
            expandNames,
            wizardName,
            TagOriginCopy.RestoreAutomationName);
        string json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(outputDir, NamesReportFileName), json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
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

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent)
        where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var nested in FindVisualChildren<T>(child))
            {
                yield return nested;
            }
        }

        if (parent is ContentControl cc && cc.Content is DependencyObject content)
        {
            foreach (var nested in FindVisualChildren<T>(content))
            {
                yield return nested;
            }
        }
    }

    public sealed record ContrastReport(
        string GeneratedUtc,
        string Method,
        double AaNormalText,
        double AaLargeOrUi,
        List<ThemeContrastReport> Themes);

    public sealed record ThemeContrastReport(string Theme, bool IsDark, List<ContrastPairReport> Pairs);

    public sealed record ContrastPairReport(
        string Foreground,
        string Background,
        double Ratio,
        bool MeetsAaNormalText,
        bool MeetsAaLargeOrUi,
        string Role);

    public sealed record NamesReport(
        string GeneratedUtc,
        string Method,
        List<string> CardExpandNames,
        string CloudWizardName,
        string RestoreAutomationName);
}
