using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using QuickNotes.App.Models;
using QuickNotes.App.Services;
using Xunit;

namespace QuickNotes.Tests;

[TestCategory(TestCategories.UiSensitive)]
public sealed class ThemeAndPaletteGuardTests
{
    private static string GetSolutionRoot()
    {
        var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ROADMAP.md")) &&
                File.Exists(Path.Combine(dir.FullName, "QuickNotes.sln")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate QuickNotes solution root containing ROADMAP.md and QuickNotes.sln");
    }

    private static void OnUi(Action<Application> assert)
    {
        StaTestHarness.Run(() =>
        {
            assert(Application.Current
                ?? throw new InvalidOperationException("WPF Application was not created on the pumping dispatcher."));
        });
    }

    [Fact]
    public void SemanticBrushes_DarkTheme_AllRequiredKeysArePopulatedWithExpectedColors()
    {
        OnUi(app =>
        {
            ThemeService.ApplyTheme(AppTheme.Dark);
            Assert.True(ThemeService.IsDark);
            Assert.Equal(AppTheme.Dark, ThemeService.CurrentTheme);

            string[] requiredKeys =
            {
                "InkBrush", "MutedBrush", "SurfaceBrush", "SurfaceAltBrush", "CanvasBrush",
                "LineBrush", "LineMutedBrush", "AccentBrush", "AccentHoverBrush",
                "ButtonBgBrush", "ButtonFgBrush", "BadgeBgBrush", "BadgeFgBrush",
                "InputBgBrush", "InputFgBrush", "InputBorderBrush",
                "MenuBgBrush", "MenuFgBrush",
                "CardBgBrush", "CardBorderBrush", "CardSelectedBgBrush", "CardSelectedBorderBrush",
                "InfoBannerBgBrush", "InfoBannerBorderBrush", "InfoBannerFgBrush",
                "WarningBannerBgBrush", "WarningBannerBorderBrush", "WarningBannerFgBrush",
                "ErrorBannerBgBrush", "ErrorBannerBorderBrush", "ErrorBannerFgBrush",
                "SuccessBannerBgBrush", "SuccessBannerBorderBrush", "SuccessBannerFgBrush"
            };

            foreach (var key in requiredKeys)
            {
                Assert.True(app.Resources.Contains(key), $"Resource '{key}' was not found in Application.Current.Resources");
                var brush = app.Resources[key] as SolidColorBrush;
                Assert.NotNull(brush);
                Assert.True(brush.IsFrozen, $"Brush '{key}' should be frozen");
            }

            var ink = (SolidColorBrush)app.Resources["InkBrush"];
            Assert.Equal(Color.FromRgb(0xF8, 0xFA, 0xFC), ink.Color);

            var canvas = (SolidColorBrush)app.Resources["CanvasBrush"];
            Assert.Equal(Color.FromRgb(0x0F, 0x17, 0x2A), canvas.Color);

            var surface = (SolidColorBrush)app.Resources["SurfaceBrush"];
            Assert.Equal(Color.FromRgb(0x1E, 0x29, 0x3B), surface.Color);

            var cardSelected = (SolidColorBrush)app.Resources["CardSelectedBgBrush"];
            Assert.Equal(Color.FromRgb(0x1E, 0x1B, 0x4B), cardSelected.Color);

            var inputBg = (SolidColorBrush)app.Resources["InputBgBrush"];
            Assert.Equal(Color.FromRgb(0x1E, 0x29, 0x3B), inputBg.Color);

            var muted = (SolidColorBrush)app.Resources["MutedBrush"];
            Assert.Equal(Color.FromRgb(0x94, 0xA3, 0xB8), muted.Color);
        });
    }

    [Fact]
    public void SemanticBrushes_LightTheme_AllRequiredKeysArePopulatedWithExpectedColors()
    {
        OnUi(app =>
        {
            ThemeService.ApplyTheme(AppTheme.Light);
            Assert.False(ThemeService.IsDark);
            Assert.Equal(AppTheme.Light, ThemeService.CurrentTheme);

            string[] requiredKeys =
            {
                "InkBrush", "MutedBrush", "SurfaceBrush", "SurfaceAltBrush", "CanvasBrush",
                "LineBrush", "LineMutedBrush", "AccentBrush", "AccentHoverBrush",
                "ButtonBgBrush", "ButtonFgBrush", "BadgeBgBrush", "BadgeFgBrush",
                "InputBgBrush", "InputFgBrush", "InputBorderBrush",
                "MenuBgBrush", "MenuFgBrush",
                "CardBgBrush", "CardBorderBrush", "CardSelectedBgBrush", "CardSelectedBorderBrush",
                "InfoBannerBgBrush", "InfoBannerBorderBrush", "InfoBannerFgBrush",
                "WarningBannerBgBrush", "WarningBannerBorderBrush", "WarningBannerFgBrush",
                "ErrorBannerBgBrush", "ErrorBannerBorderBrush", "ErrorBannerFgBrush",
                "SuccessBannerBgBrush", "SuccessBannerBorderBrush", "SuccessBannerFgBrush"
            };

            foreach (var key in requiredKeys)
            {
                Assert.True(app.Resources.Contains(key), $"Resource '{key}' was not found in Application.Current.Resources");
                var brush = app.Resources[key] as SolidColorBrush;
                Assert.NotNull(brush);
                Assert.True(brush.IsFrozen, $"Brush '{key}' should be frozen");
            }

            var ink = (SolidColorBrush)app.Resources["InkBrush"];
            Assert.Equal(Color.FromRgb(0x17, 0x20, 0x33), ink.Color);

            var canvas = (SolidColorBrush)app.Resources["CanvasBrush"];
            Assert.Equal(Color.FromRgb(0xF5, 0xF7, 0xFB), canvas.Color);

            var surface = (SolidColorBrush)app.Resources["SurfaceBrush"];
            Assert.Equal(Color.FromRgb(0xFF, 0xFF, 0xFF), surface.Color);

            var cardSelected = (SolidColorBrush)app.Resources["CardSelectedBgBrush"];
            Assert.Equal(Color.FromRgb(0xEE, 0xF2, 0xFF), cardSelected.Color);

            var inputBg = (SolidColorBrush)app.Resources["InputBgBrush"];
            Assert.Equal(Color.FromRgb(0xFF, 0xFF, 0xFF), inputBg.Color);

            var muted = (SolidColorBrush)app.Resources["MutedBrush"];
            Assert.Equal(Color.FromRgb(0x5B, 0x6B, 0x7F), muted.Color);
        });
    }

    [Fact]
    public void SemanticBrushes_GreenTheme_AllRequiredKeysArePopulatedWithExpectedColors()
    {
        OnUi(app =>
        {
            bool? themeChangedArg = null;
            Action<bool> handler = isDark => themeChangedArg = isDark;
            ThemeService.ThemeChanged += handler;
            try
            {
                ThemeService.ApplyTheme(AppTheme.Green);
            }
            finally
            {
                ThemeService.ThemeChanged -= handler;
            }

            Assert.True(ThemeService.IsDark);
            Assert.True(themeChangedArg);
            Assert.Equal(AppTheme.Green, ThemeService.CurrentTheme);

            var expectedColors = new Dictionary<string, Color>
            {
                ["InkBrush"] = Color.FromRgb(0xEC, 0xFD, 0xF5),
                ["MutedBrush"] = Color.FromRgb(0x86, 0xA7, 0x89),
                ["SurfaceBrush"] = Color.FromRgb(0x13, 0x2B, 0x20),
                ["SurfaceAltBrush"] = Color.FromRgb(0x0F, 0x24, 0x1B),
                ["CanvasBrush"] = Color.FromRgb(0x0B, 0x1A, 0x13),
                ["LineBrush"] = Color.FromRgb(0x1E, 0x42, 0x32),
                ["LineMutedBrush"] = Color.FromRgb(0x2A, 0x5A, 0x44),
                ["AccentBrush"] = Color.FromRgb(0x10, 0xB9, 0x81),
                ["AccentHoverBrush"] = Color.FromRgb(0x34, 0xD3, 0x99),
                ["ButtonBgBrush"] = Color.FromRgb(0x1C, 0x44, 0x32),
                ["ButtonFgBrush"] = Color.FromRgb(0xEC, 0xFD, 0xF5),
                ["BadgeBgBrush"] = Color.FromRgb(0x1E, 0x47, 0x35),
                ["BadgeFgBrush"] = Color.FromRgb(0xA7, 0xF3, 0xD0),
                ["InputBgBrush"] = Color.FromRgb(0x13, 0x2B, 0x20),
                ["InputFgBrush"] = Color.FromRgb(0xEC, 0xFD, 0xF5),
                ["InputBorderBrush"] = Color.FromRgb(0x2D, 0x63, 0x4B),
                ["MenuBgBrush"] = Color.FromRgb(0x13, 0x2B, 0x20),
                ["MenuFgBrush"] = Color.FromRgb(0xEC, 0xFD, 0xF5),
                ["CardBgBrush"] = Color.FromRgb(0x13, 0x2B, 0x20),
                ["CardBorderBrush"] = Color.FromRgb(0x1E, 0x42, 0x32),
                ["CardSelectedBgBrush"] = Color.FromRgb(0x1B, 0x4D, 0x36),
                ["CardSelectedBorderBrush"] = Color.FromRgb(0x34, 0xD3, 0x99),
                ["InfoBannerBgBrush"] = Color.FromRgb(0x11, 0x34, 0x38),
                ["InfoBannerBorderBrush"] = Color.FromRgb(0x0D, 0x94, 0x88),
                ["InfoBannerFgBrush"] = Color.FromRgb(0x99, 0xF6, 0xE4),
                ["WarningBannerBgBrush"] = Color.FromRgb(0x3D, 0x26, 0x05),
                ["WarningBannerBorderBrush"] = Color.FromRgb(0xB4, 0x53, 0x09),
                ["WarningBannerFgBrush"] = Color.FromRgb(0xFD, 0xE6, 0x8A),
                ["ErrorBannerBgBrush"] = Color.FromRgb(0x40, 0x10, 0x14),
                ["ErrorBannerBorderBrush"] = Color.FromRgb(0xDC, 0x26, 0x26),
                ["ErrorBannerFgBrush"] = Color.FromRgb(0xFE, 0xCA, 0xCA),
                ["SuccessBannerBgBrush"] = Color.FromRgb(0x06, 0x4E, 0x3B),
                ["SuccessBannerBorderBrush"] = Color.FromRgb(0x05, 0x96, 0x69),
                ["SuccessBannerFgBrush"] = Color.FromRgb(0x6E, 0xE7, 0xB7)
            };

            Assert.Equal(34, expectedColors.Count);

            foreach (var pair in expectedColors)
            {
                Assert.True(app.Resources.Contains(pair.Key), $"Resource '{pair.Key}' was not found in Application.Current.Resources");
                var brush = app.Resources[pair.Key] as SolidColorBrush;
                Assert.NotNull(brush);
                Assert.True(brush.IsFrozen, $"Brush '{pair.Key}' should be frozen");
                Assert.Equal(pair.Value, brush.Color);
            }
        });
    }

    [Fact]
    public void ThemeService_SystemTheme_AppliesWindowsThemeAndFiresEvent()
    {
        OnUi(_ => { });

        bool fired = false;
        bool notifiedIsDark = false;
        Action<bool> handler = isDark =>
        {
            fired = true;
            notifiedIsDark = isDark;
        };

        ThemeService.ThemeChanged += handler;
        try
        {
            OnUi(_ => ThemeService.ApplyTheme(AppTheme.System));
            Assert.Equal(AppTheme.System, ThemeService.CurrentTheme);
            Assert.True(fired);
            Assert.Equal(ThemeService.ResolveIsDark(AppTheme.System), notifiedIsDark);
            Assert.Equal(ThemeService.ResolveIsDark(AppTheme.System), ThemeService.IsDark);
        }
        finally
        {
            ThemeService.ThemeChanged -= handler;
        }
    }

    [Theory]
    [InlineData("MainWindow.xaml")]
    [InlineData("NoteEditorWindow.xaml")]
    [InlineData("ImportExportWindow.xaml")]
    [InlineData("TagRuleDialog.xaml")]
    [InlineData("TagSynonymsDialog.xaml")]
    [InlineData("TagEditDialog.xaml")]
    [InlineData("ChangeParentDialog.xaml")]
    [InlineData("SettingsWindow.xaml")]
    [InlineData("TagMergeDialog.xaml")]
    [InlineData("TagRescanPreviewDialog.xaml")]
    [InlineData("TagSuggestionsDialog.xaml")]
    [InlineData("TemplateEditDialog.xaml")]
    [InlineData("TemplateManagementDialog.xaml")]
    public void ViewsXaml_DoNotContainForbiddenHardcodedLightPalette(string xamlFileName)
    {
        string root = GetSolutionRoot();
        string filePath = Path.Combine(root, "QuickNotes.App", "Views", xamlFileName);
        Assert.True(File.Exists(filePath), $"File '{filePath}' does not exist");

        string content = File.ReadAllText(filePath);

        // Forbidden hardcoded light surface/text/border hex colors that broke dark theme
        string[] forbiddenHexColors =
        {
            "#FFFFFF",
            "#F8FAFC",
            "#1E293B",
            "#334155",
            "#64748B",
            "#0284C7",
            "#E0E7FF",
            "#E9EDFF",
            "#EEF2F6",
            "#F1F5F9"
        };

        foreach (var hex in forbiddenHexColors)
        {
            bool containsForbidden = content.IndexOf(hex, StringComparison.OrdinalIgnoreCase) >= 0;
            Assert.False(containsForbidden,
                $"File '{xamlFileName}' contains forbidden hardcoded hex '{hex}'. Semantic DynamicResource brush must be used instead.");
        }
    }

    [Fact]
    public void InteractiveViews_ControlsHaveAccessibleToolTips()
    {
        string root = GetSolutionRoot();
        string[] viewFiles =
        {
            "ImportExportWindow.xaml",
            "NoteEditorWindow.xaml",
            "TagRuleDialog.xaml",
            "TagSynonymsDialog.xaml",
            "TagEditDialog.xaml",
            "ChangeParentDialog.xaml",
            "TagMergeDialog.xaml",
            "TagRescanPreviewDialog.xaml",
            "TagSuggestionsDialog.xaml",
            "TemplateEditDialog.xaml",
            "TemplateManagementDialog.xaml"
        };

        foreach (var file in viewFiles)
        {
            string fullPath = Path.Combine(root, "QuickNotes.App", "Views", file);
            string content = File.ReadAllText(fullPath);

            // Each interactive view must define ToolTip attributes on interactive elements
            Assert.True(content.Contains("ToolTip=\""), $"View '{file}' should contain ToolTip attributes for accessible elements.");
        }
    }

    [Fact]
    public void NoteEditorWindow_UsesDynamicBrushesForEditingAndPreview()
    {
        string root = GetSolutionRoot();
        string editorXamlPath = Path.Combine(root, "QuickNotes.App", "Views", "NoteEditorWindow.xaml");
        string content = File.ReadAllText(editorXamlPath);

        // NoteTextBox must inherit from global TextBox style and use dynamic foreground/caret
        Assert.Contains("BasedOn=\"{StaticResource {x:Type TextBox}}\"", content);
        Assert.Contains("Foreground=\"{DynamicResource InkBrush}\"", content);
        Assert.Contains("CaretBrush=\"{DynamicResource InkBrush}\"", content);

        // Preview viewer must use FlowDocumentScrollViewer with dynamic foreground
        Assert.Contains("<FlowDocumentScrollViewer", content);
        Assert.Contains("Document=\"{Binding PreviewDocument}\"", content);
        Assert.Contains("Foreground=\"{DynamicResource InkBrush}\"", content);
        Assert.Contains("PreviewViewer", content);
    }

    [Fact]
    public void ContextMenuAndMenuItem_UseSemanticDynamicResourcesForBackgroundForegroundBorderAndHighlight()
    {
        string root = GetSolutionRoot();
        string appXamlPath = Path.Combine(root, "QuickNotes.App", "App.xaml");
        string content = File.ReadAllText(appXamlPath);

        // ContextMenu style
        Assert.Contains("<Style TargetType=\"ContextMenu\">", content);
        Assert.Contains("Property=\"Background\" Value=\"{DynamicResource MenuBgBrush}\"", content);
        Assert.Contains("Property=\"Foreground\" Value=\"{DynamicResource MenuFgBrush}\"", content);
        Assert.Contains("Property=\"BorderBrush\" Value=\"{DynamicResource LineBrush}\"", content);

        // MenuItem style
        Assert.Contains("<Style TargetType=\"MenuItem\">", content);
        Assert.Contains("Property=\"Foreground\" Value=\"{DynamicResource MenuFgBrush}\"", content);
        Assert.Contains("Property=\"Background\" Value=\"{DynamicResource CardSelectedBgBrush}\"", content);
        Assert.Contains("Property=\"BorderBrush\" Value=\"{DynamicResource CardSelectedBorderBrush}\"", content);

        // Separator style
        Assert.Contains("<Style TargetType=\"Separator\">", content);
        Assert.Contains("Property=\"Background\" Value=\"{DynamicResource LineBrush}\"", content);

        // Verify that ThemeService updates these brushes on theme switch
        OnUi(app =>
        {
            ThemeService.ApplyTheme(AppTheme.Dark);
            var darkMenuBg = (SolidColorBrush)app.Resources["MenuBgBrush"];
            var darkMenuFg = (SolidColorBrush)app.Resources["MenuFgBrush"];
            var darkLine = (SolidColorBrush)app.Resources["LineBrush"];
            var darkCardSel = (SolidColorBrush)app.Resources["CardSelectedBgBrush"];
            Assert.Equal(Color.FromRgb(0x1E, 0x29, 0x3B), darkMenuBg.Color);
            Assert.Equal(Color.FromRgb(0xF8, 0xFA, 0xFC), darkMenuFg.Color);
            Assert.Equal(Color.FromRgb(0x33, 0x41, 0x55), darkLine.Color);
            Assert.Equal(Color.FromRgb(0x1E, 0x1B, 0x4B), darkCardSel.Color);

            ThemeService.ApplyTheme(AppTheme.Light);
            var lightMenuBg = (SolidColorBrush)app.Resources["MenuBgBrush"];
            var lightMenuFg = (SolidColorBrush)app.Resources["MenuFgBrush"];
            var lightLine = (SolidColorBrush)app.Resources["LineBrush"];
            var lightCardSel = (SolidColorBrush)app.Resources["CardSelectedBgBrush"];
            Assert.Equal(Color.FromRgb(0xFF, 0xFF, 0xFF), lightMenuBg.Color);
            Assert.Equal(Color.FromRgb(0x17, 0x20, 0x33), lightMenuFg.Color);
            Assert.Equal(Color.FromRgb(0xE5, 0xEA, 0xF2), lightLine.Color);
            Assert.Equal(Color.FromRgb(0xEE, 0xF2, 0xFF), lightCardSel.Color);
        });
    }

    [Fact]
    public void ComboBoxAndRadioButtonStyles_UseSemanticThemeResources()
    {
        string root = GetSolutionRoot();
        string appXaml = File.ReadAllText(Path.Combine(root, "QuickNotes.App", "App.xaml"));
        string mainWindowXaml = File.ReadAllText(Path.Combine(root, "QuickNotes.App", "Views", "MainWindow.xaml"));

        Assert.Contains("<Style TargetType=\"ComboBox\">", appXaml);
        Assert.Contains("Background=\"{DynamicResource InputBgBrush}\"", appXaml);
        Assert.Contains("Foreground=\"{DynamicResource InputFgBrush}\"", appXaml);
        Assert.Contains("<Style TargetType=\"RadioButton\">", appXaml);
        Assert.Contains("Fill=\"{TemplateBinding Background}\"", appXaml);
        Assert.Contains("Stroke=\"{TemplateBinding BorderBrush}\"", appXaml);
        Assert.Contains("Fill=\"{DynamicResource AccentBrush}\"", appXaml);
        Assert.Contains("BasedOn=\"{StaticResource {x:Type ComboBox}}\"", mainWindowXaml);
    }

    [Fact]
    public void ScrollBars_UseThemeAwareTrackAndThumbResources()
    {
        string root = GetSolutionRoot();
        string appXaml = File.ReadAllText(Path.Combine(root, "QuickNotes.App", "App.xaml"));

        Assert.Contains("<Style TargetType=\"ScrollBar\">", appXaml);
        Assert.Contains("x:Key=\"QuickNotesScrollBarThumbStyle\"", appXaml);
        Assert.Contains("Property=\"Background\" Value=\"{DynamicResource SurfaceAltBrush}\"", appXaml);
        Assert.Contains("Value=\"{DynamicResource LineMutedBrush}\"", appXaml);
        Assert.Contains("Value=\"{DynamicResource AccentHoverBrush}\"", appXaml);
    }

    [Fact]
    public void NarrowEditorToolbar_WrapsWithoutClippingModeLabels()
    {
        string root = GetSolutionRoot();
        string editorXaml = File.ReadAllText(Path.Combine(root, "QuickNotes.App", "Views", "NoteEditorWindow.xaml"));

        Assert.Contains("<WrapPanel x:Name=\"EditorToolbarWrapPanel\"", editorXaml);
        Assert.Contains("Content=\"Раздельно\"", editorXaml);
        Assert.Contains("Content=\"Предпросмотр\"", editorXaml);
        Assert.Contains("<WrapPanel Grid.Row=\"1\"", editorXaml);
    }

    [Fact]
    public void MainWindowToolbar_DoesNotOverloadToolbarWithSeparateTemplatesButton_UsesSplitButton()
    {
        string root = GetSolutionRoot();
        string mainWindowXaml = Path.Combine(root, "QuickNotes.App", "Views", "MainWindow.xaml");
        string mainContent = File.ReadAllText(mainWindowXaml);

        // Separate button removed from main toolbar
        Assert.DoesNotContain("<Button Content=\"Шаблоны…\"", mainContent);

        // Split button present for New Note
        Assert.Contains("x:Name=\"NewNoteSplitRoot\"", mainContent);
        Assert.Contains("x:Name=\"NewNoteButton\"", mainContent);
        Assert.Contains("x:Name=\"NewNoteDropdownButton\"", mainContent);
        Assert.Contains("MouseRightButtonUp=\"NewNoteButton_MouseRightButtonUp\"", mainContent);

        // Settings window still has the management entry point
        string settingsXaml = Path.Combine(root, "QuickNotes.App", "Views", "SettingsWindow.xaml");
        string settingsContent = File.ReadAllText(settingsXaml);
        Assert.Contains("Command=\"{Binding OpenTemplateManagementCommand}\"", settingsContent);
    }
}
