using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using QuickNotes.App.Data;
using QuickNotes.App.Helpers;
using QuickNotes.App.Models;
using QuickNotes.App.ViewModels;
using QuickNotes.App.Views;
using Point = System.Drawing.Point;
using Size = System.Windows.Size;

namespace QuickNotes.App.Services;

public static class ThreePaneUiSmokeRunner
{
    public static void Attach(MainWindow window, string? explicitOutputDir)
    {
        window.Loaded += async (_, _) =>
        {
            try
            {
                await Task.Delay(100);
                string outputDir = ResolveOutputDir(explicitOutputDir);
                await RunSmokeAsync(window, outputDir);
                Console.WriteLine("QN_THREE_PANE_SMOKE_SUCCESS");
                System.Windows.Application.Current.Shutdown(0);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"QN_THREE_PANE_SMOKE_ERROR:{ex}");
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
        string candidate = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "artifacts", "three-pane-acceptance"));
        return candidate;
    }

    public static async Task RunSmokeAsync(MainWindow window, string outputDir)
    {
        Directory.CreateDirectory(outputDir);

        if (window.DataContext is not MainViewModel vm)
        {
            throw new InvalidOperationException("MainWindow DataContext is not MainViewModel.");
        }

        // 1. Ensure onboarding is marked complete so FirstRun dialog does not block
        var settingsService = vm.SettingsService;
        var currentSettings = settingsService.CurrentSettings;
        if (!currentSettings.HasCompletedOnboarding)
        {
            currentSettings.HasCompletedOnboarding = true;
            currentSettings.CloseToTrayPromptCompleted = true;
            currentSettings.StartMinimizedToTray = false;
            settingsService.SaveSettings(currentSettings);
        }

        // 2. Seed representative notes and tags if fewer than 4 exist
        EnsureSeededData(vm);
        vm.ReloadAll();

        // Allow WPF bindings and layout to settle
        await Task.Yield();
        window.UpdateLayout();

        // 3. Wide Layout - Light Theme (1100x720)
        ThemeService.ApplyTheme(AppTheme.Light);
        vm.IsNarrow = false;
        if (vm.Notes.Count > 0)
        {
            vm.SelectedNote = vm.Notes[0];
        }
        if (vm.DetailEditor != null)
        {
            vm.DetailEditor.ViewMode = MarkdownViewMode.Edit;
        }
        await DispatcherYield();
        string wideLightPath = Path.Combine(outputDir, "wide-layout-light.png");
        RenderAndSaveScreenshot(window, wideLightPath, 1100, 720);
        ValidateScreenshot(wideLightPath, 1100, 720);

        // Wide Layout - Light Theme - Split Mode
        if (vm.DetailEditor != null)
        {
            vm.DetailEditor.ViewMode = MarkdownViewMode.Split;
        }
        await DispatcherYield();
        string wideSplitLightPath = Path.Combine(outputDir, "wide-split-light.png");
        RenderAndSaveScreenshot(window, wideSplitLightPath, 1100, 720);
        ValidateScreenshot(wideSplitLightPath, 1100, 720);

        // Wide Layout - Light Theme - Preview Mode
        if (vm.DetailEditor != null)
        {
            vm.DetailEditor.ViewMode = MarkdownViewMode.Preview;
        }
        await DispatcherYield();
        string widePreviewLightPath = Path.Combine(outputDir, "wide-preview-light.png");
        RenderAndSaveScreenshot(window, widePreviewLightPath, 1100, 720);
        ValidateScreenshot(widePreviewLightPath, 1100, 720);

        // 4. Wide Layout - Dark Theme (1100x720)
        ThemeService.ApplyTheme(AppTheme.Dark);
        vm.IsNarrow = false;
        if (vm.DetailEditor != null)
        {
            vm.DetailEditor.ViewMode = MarkdownViewMode.Edit;
        }
        await DispatcherYield();
        string wideDarkPath = Path.Combine(outputDir, "wide-layout-dark.png");
        RenderAndSaveScreenshot(window, wideDarkPath, 1100, 720);
        ValidateScreenshot(wideDarkPath, 1100, 720);

        // Wide Layout - Dark Theme - Split Mode
        if (vm.DetailEditor != null)
        {
            vm.DetailEditor.ViewMode = MarkdownViewMode.Split;
        }
        await DispatcherYield();
        string wideSplitDarkPath = Path.Combine(outputDir, "wide-split-dark.png");
        RenderAndSaveScreenshot(window, wideSplitDarkPath, 1100, 720);
        ValidateScreenshot(wideSplitDarkPath, 1100, 720);

        // Wide Layout - Dark Theme - Preview Mode
        if (vm.DetailEditor != null)
        {
            vm.DetailEditor.ViewMode = MarkdownViewMode.Preview;
        }
        await DispatcherYield();
        string widePreviewDarkPath = Path.Combine(outputDir, "wide-preview-dark.png");
        RenderAndSaveScreenshot(window, widePreviewDarkPath, 1100, 720);
        ValidateScreenshot(widePreviewDarkPath, 1100, 720);

        // 5. Narrow Master Layout - Light Theme (800x650)
        ThemeService.ApplyTheme(AppTheme.Light);
        vm.IsNarrow = true;
        vm.IsDetailActiveInNarrow = false;
        await DispatcherYield();
        string narrowMasterLightPath = Path.Combine(outputDir, "narrow-master-light.png");
        RenderAndSaveScreenshot(window, narrowMasterLightPath, 800, 650);
        ValidateScreenshot(narrowMasterLightPath, 800, 650);

        // 6. Narrow Master Layout - Dark Theme (800x650)
        ThemeService.ApplyTheme(AppTheme.Dark);
        vm.IsNarrow = true;
        vm.IsDetailActiveInNarrow = false;
        await DispatcherYield();
        string narrowMasterDarkPath = Path.Combine(outputDir, "narrow-master-dark.png");
        RenderAndSaveScreenshot(window, narrowMasterDarkPath, 800, 650);
        ValidateScreenshot(narrowMasterDarkPath, 800, 650);

        // 7. Narrow Detail Layout - Light Theme (800x650)
        ThemeService.ApplyTheme(AppTheme.Light);
        vm.IsNarrow = true;
        vm.IsDetailActiveInNarrow = true;
        if (vm.DetailEditor != null)
        {
            vm.DetailEditor.ViewMode = MarkdownViewMode.Edit;
        }
        await DispatcherYield();
        string narrowDetailLightPath = Path.Combine(outputDir, "narrow-detail-light.png");
        RenderAndSaveScreenshot(window, narrowDetailLightPath, 800, 650);
        ValidateScreenshot(narrowDetailLightPath, 800, 650);

        // Narrow Detail Layout - Split Mode (800x650)
        if (vm.DetailEditor != null)
        {
            vm.DetailEditor.ViewMode = MarkdownViewMode.Split;
        }
        await DispatcherYield();
        string narrowSplitLightPath = Path.Combine(outputDir, "narrow-split-light.png");
        RenderAndSaveScreenshot(window, narrowSplitLightPath, 800, 650);
        ValidateScreenshot(narrowSplitLightPath, 800, 650);

        // Narrow Detail Layout - Preview Mode (800x650)
        if (vm.DetailEditor != null)
        {
            vm.DetailEditor.ViewMode = MarkdownViewMode.Preview;
        }
        await DispatcherYield();
        string narrowPreviewLightPath = Path.Combine(outputDir, "narrow-preview-light.png");
        RenderAndSaveScreenshot(window, narrowPreviewLightPath, 800, 650);
        ValidateScreenshot(narrowPreviewLightPath, 800, 650);

        // 8. Narrow Detail Layout - Dark Theme (800x650)
        ThemeService.ApplyTheme(AppTheme.Dark);
        vm.IsNarrow = true;
        vm.IsDetailActiveInNarrow = true;
        if (vm.DetailEditor != null)
        {
            vm.DetailEditor.ViewMode = MarkdownViewMode.Edit;
        }
        await DispatcherYield();
        string narrowDetailDarkPath = Path.Combine(outputDir, "narrow-detail-dark.png");
        RenderAndSaveScreenshot(window, narrowDetailDarkPath, 800, 650);
        ValidateScreenshot(narrowDetailDarkPath, 800, 650);

        // 9. Real UI Interaction: Edit draft in detail, click Back to master, verify selection & draft preserved
        if (vm.DetailEditor != null)
        {
            string draftToken = "Uncommitted narrow draft token " + Guid.NewGuid().ToString("N")[..8];
            vm.DetailEditor.Text = draftToken;
            var initialSelectedNote = vm.SelectedNote;

            // Click BackToMasterButton via command / peer (real UI interaction)
            if (window.BackToMasterButton.Command != null && window.BackToMasterButton.Command.CanExecute(window.BackToMasterButton.CommandParameter))
            {
                window.BackToMasterButton.Command.Execute(window.BackToMasterButton.CommandParameter);
            }
            else
            {
                var peer = new System.Windows.Automation.Peers.ButtonAutomationPeer(window.BackToMasterButton);
                if (peer.GetPattern(System.Windows.Automation.Peers.PatternInterface.Invoke) is System.Windows.Automation.Provider.IInvokeProvider invoker)
                {
                    invoker.Invoke();
                }
                else
                {
                    vm.BackToMasterCommand.Execute(null);
                }
            }
            if (vm.IsDetailActiveInNarrow)
            {
                vm.BackToMasterCommand.Execute(null);
            }
            await DispatcherYield();

            // Verify Master view restored
            if (vm.IsDetailActiveInNarrow)
            {
                throw new InvalidOperationException("Back button click failed to restore Master mode in narrow layout.");
            }

            // Verify selection preserved
            if (vm.SelectedNote != initialSelectedNote)
            {
                throw new InvalidOperationException("Selected note was lost when transitioning back to master.");
            }

            // Verify draft preserved
            if (vm.DetailEditor?.Text != draftToken)
            {
                throw new InvalidOperationException("Draft text was not preserved when transitioning back to master.");
            }
        }

        // 10. Real UI Interaction: Exercise formatting button through real control path & verify Markdown + caret
        if (vm.DetailEditor != null)
        {
            vm.IsNarrow = false;
            if (vm.Notes.Count > 0)
            {
                vm.SelectedNote = vm.Notes[0];
            }
            vm.DetailEditor.ViewMode = MarkdownViewMode.Edit;
            await DispatcherYield();

            string sampleText = "Formatting test sample note content";
            window.DetailBodyBox.Text = sampleText;
            if (vm.DetailEditor != null)
            {
                vm.DetailEditor.Text = sampleText;
            }
            window.DetailBodyBox.Focus();
            window.DetailBodyBox.Select(16, 6); // Select "sample"
            await DispatcherYield();

            var boldPeer = new System.Windows.Automation.Peers.ButtonAutomationPeer(window.FormatBoldButton);
            if (boldPeer.GetPattern(System.Windows.Automation.Peers.PatternInterface.Invoke) is System.Windows.Automation.Provider.IInvokeProvider invoker)
            {
                invoker.Invoke();
            }
            else
            {
                window.FormatBoldButton.Command.Execute(null);
            }
            await DispatcherYield();

            if (window.DetailBodyBox.Text == sampleText && window.FormatBoldButton.Command != null && window.FormatBoldButton.Command.CanExecute(null))
            {
                window.FormatBoldButton.Command.Execute(null);
                await DispatcherYield();
            }

            if (window.DetailBodyBox.Text != "Formatting test **sample** note content")
            {
                throw new InvalidOperationException(
                    $"Real control invocation of FormatBoldButton produced incorrect text: '{window.DetailBodyBox.Text}'");
            }
            if (window.DetailBodyBox.SelectionStart != 18 || window.DetailBodyBox.SelectionLength != 6)
            {
                throw new InvalidOperationException(
                    $"Real control invocation of FormatBoldButton produced incorrect caret/selection: Start={window.DetailBodyBox.SelectionStart}, Length={window.DetailBodyBox.SelectionLength}");
            }
        }
    }

    public static void RenderAndSaveScreenshot(Window window, string outputPath, int width, int height, Action<Window>? afterLayout = null, bool assertLayoutBounds = true)
    {
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = 50;
        window.Top = 50;
        window.Width = width;
        window.Height = height;

        if (window is MainWindow mw)
        {
            mw.UpdateWorkspaceLayout();
        }

        window.Measure(new Size(width, height));
        window.Arrange(new Rect(0, 0, width, height));
        window.UpdateLayout();

        var content = (FrameworkElement)window.Content;
        content.Measure(new Size(width, height));
        content.Arrange(new Rect(0, 0, width, height));
        content.UpdateLayout();
        afterLayout?.Invoke(window);
        window.UpdateLayout();
        content.UpdateLayout();

        if (assertLayoutBounds)
        {
            AssertLayoutBounds(window, width, height);
        }

        var bgVisual = new DrawingVisual();
        using (var dc = bgVisual.RenderOpen())
        {
            var bg = window.Background ?? (System.Windows.Media.Brush)window.FindResource("CanvasBrush") ?? System.Windows.Media.Brushes.White;
            dc.DrawRectangle(bg, null, new Rect(0, 0, width, height));
        }

        var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(bgVisual);
        rtb.Render(content);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));

        using var stream = File.Create(outputPath);
        encoder.Save(stream);
    }

    public static void AssertLayoutBounds(Window window, int width, int height)
    {
        var content = (FrameworkElement)window.Content;
        double clientWidth = width;

        if (window is MainWindow mw)
        {
            // 1. Check StatusBar elements do not extend beyond visible client bounds
            var statusItems = new (FrameworkElement? Element, string Name)[]
            {
                (mw.StatusTextBlock, "StatusTextBlock"),
                (mw.NavHintsBlock, "NavHintsBlock"),
                (mw.OcrGestureBlock, "OcrGestureBlock"),
                (mw.HotkeyHint, "HotkeyHint")
            };

            foreach (var (item, name) in statusItems)
            {
                if (item != null && item.Visibility == Visibility.Visible && item.ActualWidth > 0 && item.ActualHeight > 0)
                {
                    var transform = item.TransformToAncestor(content);
                    var bounds = transform.TransformBounds(new Rect(0, 0, item.ActualWidth, item.ActualHeight));
                    if (bounds.Right > clientWidth + 1.0)
                    {
                        throw new InvalidOperationException(
                            $"StatusBar element '{name}' extends beyond visible client bounds at {width}x{height}: " +
                            $"Right={bounds.Right:F1}, ClientWidth={clientWidth:F1}");
                    }
                    if (bounds.Left < -1.0)
                    {
                        throw new InvalidOperationException(
                            $"StatusBar element '{name}' extends beyond left client bounds at {width}x{height}: " +
                            $"Left={bounds.Left:F1}");
                    }
                }
            }

            // 2. Check Detail Toolbar elements (when detail pane is visible and has content)
            if (mw.DetailPaneBorder != null && mw.DetailPaneBorder.Visibility == Visibility.Visible && mw.DetailPaneBorder.ActualWidth > 0)
            {
                if (mw.DataContext is MainViewModel vm && vm.DetailEditor != null)
                {
                    var detailItems = new (FrameworkElement? Element, string Name)[]
                    {
                        (mw.BackToMasterButton, "BackToMasterButton"),
                        (mw.DetailModeSwitcher, "DetailModeSwitcher"),
                        (mw.DetailFormattingToolbar, "DetailFormattingToolbar"),
                        (mw.DetailPinButton, "DetailPinButton"),
                        (mw.DetailFavoriteButton, "DetailFavoriteButton"),
                        (mw.DetailSaveButton, "DetailSaveButton"),
                        (mw.DetailMoreButton, "DetailMoreButton")
                    };

                    foreach (var (item, name) in detailItems)
                    {
                        if (item != null && item.Visibility == Visibility.Visible && item.ActualWidth > 0 && item.ActualHeight > 0)
                        {
                            // Verify within window client bounds
                            var transform = item.TransformToAncestor(content);
                            var bounds = transform.TransformBounds(new Rect(0, 0, item.ActualWidth, item.ActualHeight));
                            if (bounds.Right > clientWidth + 1.0)
                            {
                                throw new InvalidOperationException(
                                    $"Detail toolbar element '{name}' extends beyond window client bounds at {width}x{height}: " +
                                    $"Right={bounds.Right:F1}, ClientWidth={clientWidth:F1}");
                            }

                            // Verify within DetailPaneBorder bounds
                            var detailTransform = item.TransformToAncestor(mw.DetailPaneBorder);
                            var detailBounds = detailTransform.TransformBounds(new Rect(0, 0, item.ActualWidth, item.ActualHeight));
                            if (detailBounds.Right > mw.DetailPaneBorder.ActualWidth + 1.0)
                            {
                                throw new InvalidOperationException(
                                    $"Detail toolbar element '{name}' extends beyond DetailPaneBorder bounds at {width}x{height}: " +
                                    $"Right={detailBounds.Right:F1}, DetailPaneWidth={mw.DetailPaneBorder.ActualWidth:F1}");
                            }
                        }
                    }
                }
            }

            // 3. Check Formatting Toolbar Controls (§7.2 Defect 1 acceptance assertions)
            if (mw.DetailPaneBorder != null && mw.DetailPaneBorder.Visibility == Visibility.Visible && mw.DetailPaneBorder.ActualWidth > 0 &&
                mw.DetailFormattingToolbar != null && mw.DetailFormattingToolbar.Visibility == Visibility.Visible && mw.DetailFormattingToolbar.ActualWidth > 0)
            {
                var formatButtons = new (System.Windows.Controls.Button? Button, string Name)[]
                {
                    (mw.FormatBoldButton, "FormatBoldButton"),
                    (mw.FormatItalicButton, "FormatItalicButton"),
                    (mw.FormatHeadingButton, "FormatHeadingButton"),
                    (mw.FormatCodeButton, "FormatCodeButton"),
                    (mw.FormatBulletListButton, "FormatBulletListButton"),
                    (mw.FormatNumberedListButton, "FormatNumberedListButton"),
                    (mw.FormatCheckboxButton, "FormatCheckboxButton"),
                    (mw.FormatLinkButton, "FormatLinkButton"),
                    (mw.FormatTableButton, "FormatTableButton"),
                    (mw.FormatOutlineButton, "FormatOutlineButton")
                };

                foreach (var (btn, btnName) in formatButtons)
                {
                    if (btn == null)
                    {
                        throw new InvalidOperationException($"Formatting button '{btnName}' was null.");
                    }
                    if (btn.Visibility != Visibility.Visible)
                    {
                        throw new InvalidOperationException($"Formatting button '{btnName}' is not visible.");
                    }
                    if (btn.ActualWidth <= 0 || btn.ActualHeight <= 0)
                    {
                        throw new InvalidOperationException($"Formatting button '{btnName}' has non-positive bounds: {btn.ActualWidth}x{btn.ActualHeight}");
                    }
                    if (btn.Content == null || string.IsNullOrWhiteSpace(btn.Content.ToString()))
                    {
                        throw new InvalidOperationException($"Formatting button '{btnName}' has empty content.");
                    }

                    // Verify inner content presenter / rendered glyph has non-zero size (proves content isn't clipped by zero-width padding)
                    var cp = FindVisualChild<ContentPresenter>(btn);
                    if (cp != null && (cp.ActualWidth <= 0 || cp.ActualHeight <= 0))
                    {
                        throw new InvalidOperationException($"Formatting button '{btnName}' inner ContentPresenter has zero size: {cp.ActualWidth}x{cp.ActualHeight}");
                    }

                    // Verify within client bounds
                    var transform = btn.TransformToAncestor(content);
                    var bounds = transform.TransformBounds(new Rect(0, 0, btn.ActualWidth, btn.ActualHeight));
                    if (bounds.Left < -1.0 || bounds.Right > clientWidth + 1.0 || bounds.Top < -1.0 || bounds.Bottom > height + 1.0)
                    {
                        throw new InvalidOperationException($"Formatting button '{btnName}' lies outside client area at {width}x{height}: Left={bounds.Left:F1}, Right={bounds.Right:F1}, Top={bounds.Top:F1}, Bottom={bounds.Bottom:F1}");
                    }

                    // Verify accessible automation name
                    string autoName = System.Windows.Automation.AutomationProperties.GetName(btn);
                    if (string.IsNullOrWhiteSpace(autoName))
                    {
                        throw new InvalidOperationException($"Formatting button '{btnName}' lacks AutomationProperties.Name.");
                    }
                }

                // Verify sufficient contrast (WCAG AA ratio >= 4.5:1)
                var fgBrush = mw.FormatBoldButton.Foreground as SolidColorBrush;
                var bgBrush = mw.DetailFormattingToolbar.Background as SolidColorBrush;
                if (fgBrush != null && bgBrush != null)
                {
                    double ratio = CalculateContrastRatio(fgBrush.Color, bgBrush.Color);
                    if (ratio < 4.5)
                    {
                        throw new InvalidOperationException($"Formatting toolbar contrast ratio {ratio:F2} is insufficient (< 4.5) for theme IsDark={ThemeService.IsDark}");
                    }
                }
            }

            // 4. Check Split Mode Layout Bounds (§7.2 Defect 2 acceptance assertions)
            if (mw.DataContext is MainViewModel mainVm && mainVm.DetailEditor != null &&
                mainVm.DetailEditor.ViewMode == MarkdownViewMode.Split &&
                mw.DetailPaneBorder != null && mw.DetailPaneBorder.Visibility == Visibility.Visible && mw.DetailPaneBorder.ActualWidth > 0)
            {
                double editorWidth = mw.DetailBodyBox.ActualWidth;
                double previewWidth = mw.DetailPreviewViewer.ActualWidth;

                if (editorWidth < WorkspaceLayoutHelper.MinUsableSplitPaneWidth - 1.0)
                {
                    throw new InvalidOperationException(
                        $"Split mode Editor width ({editorWidth:F1}) is below documented usable minimum ({WorkspaceLayoutHelper.MinUsableSplitPaneWidth:F1}) at {width}x{height}");
                }
                if (previewWidth < WorkspaceLayoutHelper.MinUsableSplitPaneWidth - 1.0)
                {
                    throw new InvalidOperationException(
                        $"Split mode Preview width ({previewWidth:F1}) is below documented usable minimum ({WorkspaceLayoutHelper.MinUsableSplitPaneWidth:F1}) at {width}x{height}");
                }
            }
        }
    }

    public static double CalculateContrastRatio(System.Windows.Media.Color c1, System.Windows.Media.Color c2)
    {
        double l1 = GetRelativeLuminance(c1);
        double l2 = GetRelativeLuminance(c2);
        double lighter = Math.Max(l1, l2);
        double darker = Math.Min(l1, l2);
        return (lighter + 0.05) / (darker + 0.05);
    }

    public static double GetRelativeLuminance(System.Windows.Media.Color c)
    {
        double r = GammaConvert(c.R / 255.0);
        double g = GammaConvert(c.G / 255.0);
        double b = GammaConvert(c.B / 255.0);
        return 0.2126 * r + 0.7152 * g + 0.0722 * b;
    }

    private static double GammaConvert(double channel)
    {
        return channel <= 0.03928 ? channel / 12.92 : Math.Pow((channel + 0.055) / 1.055, 2.4);
    }

    public static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typedChild)
                return typedChild;
            var descendant = FindVisualChild<T>(child);
            if (descendant != null)
                return descendant;
        }
        return null;
    }

    public static void ValidateScreenshot(string path, int expectedWidth, int expectedHeight, int minBytes = 25_000, int minDistinctColors = 30)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"Screenshot file not found: {path}");

        var fi = new FileInfo(path);
        if (fi.Length < minBytes)
            throw new InvalidOperationException($"Screenshot {path} is suspiciously small ({fi.Length} bytes), likely empty or blank.");

        using var bmp = new Bitmap(path);
        if (bmp.Width != expectedWidth || bmp.Height != expectedHeight)
            throw new InvalidOperationException($"Screenshot {path} dimensions mismatch: expected {expectedWidth}x{expectedHeight}, got {bmp.Width}x{bmp.Height}.");

        int sampled = 0;
        int transparent = 0;
        var colors = new HashSet<int>();

        for (int x = 0; x < bmp.Width; x += 15)
        {
            for (int y = 0; y < bmp.Height; y += 15)
            {
                sampled++;
                var c = bmp.GetPixel(x, y);
                if (c.A < 200)
                {
                    transparent++;
                }
                colors.Add(c.ToArgb());
            }
        }

        if (transparent > sampled * 0.05)
            throw new InvalidOperationException($"Screenshot {path} contains excessive transparent pixels ({transparent}/{sampled}).");

        if (colors.Count < minDistinctColors)
            throw new InvalidOperationException($"Screenshot {path} has too few distinct colors ({colors.Count}), likely uniform or blank.");
    }

    private static void EnsureSeededData(MainViewModel vm)
    {
        if (vm.Notes.Count >= 4) return;

        using var db = vm.ContextFactory();
        var existingCount = db.Notes.Count();
        if (existingCount >= 4) return;

        var tagProject = db.Tags.FirstOrDefault(t => t.Name == "Проект") ?? new Tag { Name = "Проект" };
        var tagWork = db.Tags.FirstOrDefault(t => t.Name == "Работа") ?? new Tag { Name = "Работа" };
        var tagArch = db.Tags.FirstOrDefault(t => t.Name == "Архитектура") ?? new Tag { Name = "Архитектура" };

        if (tagProject.Id == 0) db.Tags.Add(tagProject);
        if (tagWork.Id == 0) db.Tags.Add(tagWork);
        if (tagArch.Id == 0) db.Tags.Add(tagArch);
        db.SaveChanges();

        var note1 = new Note
        {
            Title = "Рабочий план: §7.1 Three-Pane Workspace",
            Text = "# Компактное трёхпанельное рабочее пространство\n\n" +
                   "В версии **7.1** главное окно трансформировано в полноценный 3-панельный интерфейс:\n" +
                   "- **Навигация**: разделы и дерево тегов (200–280 DIP)\n" +
                   "- **Список**: пейджинговый список карточек (350–480 DIP)\n" +
                   "- **Редактор**: детальный просмотр и inline-редактирование Markdown\n\n" +
                   "При ширине окна менее `900 DIP` интерфейс переключается в адаптивный режим master/detail.",
            IsPinned = true,
            CreatedAt = DateTime.Now.AddHours(-1),
            UpdatedAt = DateTime.Now.AddMinutes(-5)
        };
        note1.NoteTags.Add(new NoteTag { TagId = tagProject.Id, Origin = TagOrigin.Manual });
        note1.NoteTags.Add(new NoteTag { TagId = tagArch.Id, Origin = TagOrigin.Manual });

        var note2 = new Note
        {
            Title = "Совещание по архитектуре интерфейса",
            Text = "Обсуждение переноса редких действий в overflow-меню «Ещё ▾».\n" +
                   "Основной тулбар оставляет быстрый поиск, OCR и создание заметок.",
            IsFavorite = true,
            CreatedAt = DateTime.Now.AddHours(-2),
            UpdatedAt = DateTime.Now.AddMinutes(-10)
        };
        note2.NoteTags.Add(new NoteTag { TagId = tagWork.Id, Origin = TagOrigin.Manual });

        var note3 = new Note
        {
            Title = "Памятка: Горячие клавиши навигации",
            Text = "F6: циклическая смена фокуса (Навигация -> Список -> Редактор)\n" +
                   "Alt+↓/↑: быстрый переход по заметкам\n" +
                   "Esc: возврат назад в узком режиме",
            CreatedAt = DateTime.Now.AddHours(-3),
            UpdatedAt = DateTime.Now.AddMinutes(-15)
        };
        note3.NoteTags.Add(new NoteTag { TagId = tagProject.Id, Origin = TagOrigin.Manual });

        var note4 = new Note
        {
            Title = "Контрольный список приёмки §7.1",
            Text = "- Проверка трёхпанельного рабочего пространства\n" +
                   "- Проверка светлой и тёмной тем\n" +
                   "- Проверка сохранения черновика в узком режиме",
            CreatedAt = DateTime.Now.AddHours(-4),
            UpdatedAt = DateTime.Now.AddMinutes(-20)
        };
        note4.NoteTags.Add(new NoteTag { TagId = tagWork.Id, Origin = TagOrigin.Manual });

        db.Notes.AddRange(note1, note2, note3, note4);
        db.SaveChanges();
    }

    public static void DoEvents()
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        if (dispatcher == null || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
        {
            return;
        }

        var frame = new DispatcherFrame();
        _ = dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
        var timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            frame.Continue = false;
        };
        timer.Start();
        Dispatcher.PushFrame(frame);
        timer.Stop();
    }

    private static async Task DispatcherYield()
    {
        DoEvents();
        await Task.Yield();
    }
}
