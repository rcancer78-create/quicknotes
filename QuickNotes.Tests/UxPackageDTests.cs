using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using QuickNotes.App.Helpers;
using QuickNotes.App.Models;
using QuickNotes.App.Services;
using Xunit;

namespace QuickNotes.Tests;

[Collection(LiveProfileSnapshotCollection.Name)]
[TestCategory(TestCategories.UiSensitive)]
public sealed class UxPackageDTests
{
    [Fact]
    public void ColorContrast_BlackOnWhite_ExceedsAa()
    {
        double ratio = ColorContrast.Ratio(Colors.Black, Colors.White);
        Assert.True(ratio >= 20, ratio.ToString("0.00"));
        Assert.Equal(4.5, ColorContrast.AaNormalText);
        Assert.Equal(3.0, ColorContrast.AaLargeOrUi);
    }

    [Fact]
    public void ThemeBrushes_InkAndBanners_MeetAaAgainstTheirBackgrounds()
    {
        StaTestHarness.Run(() =>
        {
            var app = Application.Current
                ?? throw new InvalidOperationException("WPF Application was not created.");
            foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark })
            {
                ThemeService.ApplyTheme(theme);
                AssertPair(app, "InkBrush", "CanvasBrush");
                AssertPair(app, "InkBrush", "SurfaceBrush");
                AssertPair(app, "InkBrush", "CardBgBrush");
                AssertPair(app, "ButtonFgBrush", "ButtonBgBrush");
                AssertPair(app, "InfoBannerFgBrush", "InfoBannerBgBrush");
                AssertPair(app, "WarningBannerFgBrush", "WarningBannerBgBrush");
                AssertPair(app, "ErrorBannerFgBrush", "ErrorBannerBgBrush");
                AssertPair(app, "SuccessBannerFgBrush", "SuccessBannerBgBrush");
                AssertPair(app, "MutedBrush", "CanvasBrush");
                AssertPair(app, "MutedBrush", "SurfaceBrush");
                AssertPair(app, "MutedBrush", "SurfaceAltBrush");
                AssertPair(app, "MutedBrush", "CardBgBrush");
            }
        });
    }

    [Fact]
    public void NewUxControls_DeclareAutomationPropertiesNames()
    {
        string root = FindSolutionRoot();
        string main = File.ReadAllText(Path.Combine(root, "QuickNotes.App", "Views", "MainWindow.xaml"));
        string editor = File.ReadAllText(Path.Combine(root, "QuickNotes.App", "Views", "NoteEditorWindow.xaml"));
        string settings = File.ReadAllText(Path.Combine(root, "QuickNotes.App", "Views", "SettingsWindow.xaml"));

        Assert.Contains("AutomationProperties.Name=\"{Binding ExpandButtonAutomationName}\"", main, StringComparison.Ordinal);
        Assert.Contains("x:Static h:TagOriginCopy.RestoreAutomationName", main, StringComparison.Ordinal);
        Assert.Contains("x:Static h:TagOriginCopy.RestoreAutomationName", editor, StringComparison.Ordinal);
        Assert.Contains("x:Static h:UserTaskCopy.CloudWizardAutomationName", settings, StringComparison.Ordinal);
    }

    private static void AssertPair(Application app, string fgKey, string bgKey)
    {
        var fg = (SolidColorBrush)app.Resources[fgKey];
        var bg = (SolidColorBrush)app.Resources[bgKey];
        double ratio = ColorContrast.Ratio(fg.Color, bg.Color);
        Assert.True(ratio >= ColorContrast.AaNormalText,
            $"{fgKey} vs {bgKey} = {ratio:0.00}, need {ColorContrast.AaNormalText}");
    }

    private static string FindSolutionRoot()
    {
        var current = AppDomain.CurrentDomain.BaseDirectory;
        while (!string.IsNullOrEmpty(current))
        {
            if (File.Exists(Path.Combine(current, "QuickNotes.sln")))
            {
                return current;
            }

            var parent = Directory.GetParent(current);
            if (parent == null)
            {
                break;
            }

            current = parent.FullName;
        }

        return @"D:\work\QuickNotes";
    }
}
