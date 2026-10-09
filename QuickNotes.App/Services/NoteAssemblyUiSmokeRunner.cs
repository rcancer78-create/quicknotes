using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CheckBox = System.Windows.Controls.CheckBox;
using TextBox = System.Windows.Controls.TextBox;
using Border = System.Windows.Controls.Border;
using Button = System.Windows.Controls.Button;
using RadioButton = System.Windows.Controls.RadioButton;
using QuickNotes.App.Data;
using QuickNotes.App.Models;
using QuickNotes.App.ViewModels;
using QuickNotes.App.Views;

namespace QuickNotes.App.Services;

public static class NoteAssemblyUiSmokeRunner
{
    public const string FirstSourceBody = "Первый исходник: план встречи.\n- пункт A";
    public const string SecondSourceBody = "Второй исходник: **идеи** и Unicode «ёлка».";
    public const string ThirdSourceBody = "Третий исходник: пустая строка ниже.\n\nхвост";

    public static void Attach(MainWindow window, string? explicitOutputDir)
    {
        window.Loaded += async (_, _) =>
        {
            try
            {
                await Task.Delay(80);
                string outputDir = ResolveOutputDir(explicitOutputDir);
                Run(outputDir);
                Console.WriteLine("QN_NOTE_ASSEMBLY_SMOKE_SUCCESS");
                System.Windows.Application.Current.Shutdown(0);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"QN_NOTE_ASSEMBLY_SMOKE_ERROR:{ex}");
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
        return Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "artifacts", "quick-note-assembly-acceptance"));
    }

    public static void Run(string outputDir)
    {
        Directory.CreateDirectory(outputDir);
        int firstId;
        int secondId;
        int thirdId;
        using (var db = new QuickNotesDbContext())
        {
            DbInitializer.Initialize(db);
            var alpha = new Note { Title = "Встреча", Text = FirstSourceBody };
            var beta = new Note { Title = "Идеи", Text = SecondSourceBody };
            var gamma = new Note { Title = "Черновик", Text = ThirdSourceBody };
            db.Notes.AddRange(alpha, beta, gamma);
            db.SaveChanges();
            firstId = alpha.Id;
            secondId = beta.Id;
            thirdId = gamma.Id;
        }

        var vm = new NoteAssemblyViewModel(() => new QuickNotesDbContext(), new NoteAssemblyService(), new NoteHistoryService());
        vm.TryAddSource(firstId);
        vm.TryAddSource(secondId);
        vm.Title = "Настройка сборки";
        vm.SelectedSeparator = vm.SeparatorOptions.First(o => o.Kind == NoteAssemblySeparatorKind.ThematicBreakDash);

        var window = new NoteAssemblyWindow(vm);
        window.Show();
        window.UpdateLayout();

        CaptureScene(window, vm, outputDir, "setup", 780, 720, AppTheme.Light);
        CaptureScene(window, vm, outputDir, "setup", 780, 720, AppTheme.Dark);
        CaptureScene(window, vm, outputDir, "setup", 640, 560, AppTheme.Light);
        CaptureScene(window, vm, outputDir, "setup", 640, 560, AppTheme.Dark);

        vm.TryAddSource(thirdId);
        vm.Title = "Полный предпросмотр сборки";
        vm.SelectedSeparator = vm.SeparatorOptions.First(o => o.Kind == NoteAssemblySeparatorKind.ThematicBreakStar);
        vm.SelectedSource = vm.Sources[0];
        CaptureScene(window, vm, outputDir, "preview", 780, 720, AppTheme.Light);
        CaptureScene(window, vm, outputDir, "preview", 780, 720, AppTheme.Dark);
        CaptureScene(window, vm, outputDir, "preview", 640, 560, AppTheme.Light);
        CaptureScene(window, vm, outputDir, "preview", 640, 560, AppTheme.Dark);

        vm.MoveSourcesToTrash = true;
        vm.TrashAcknowledged = false;
        vm.Title = "Подтверждение корзины исходников";
        CaptureScene(window, vm, outputDir, "destructive", 780, 720, AppTheme.Light);
        CaptureScene(window, vm, outputDir, "destructive", 780, 720, AppTheme.Dark);
        CaptureScene(window, vm, outputDir, "destructive", 640, 560, AppTheme.Light);
        CaptureScene(window, vm, outputDir, "destructive", 640, 560, AppTheme.Dark);

        vm.TrashAcknowledged = true;
        if (!vm.CanCommit)
        {
            throw new InvalidOperationException(vm.BlockingReason + " / " + vm.StatusMessage);
        }

        vm.Execute();
        if (!vm.HasCompletedAssembly)
        {
            throw new InvalidOperationException(vm.StatusMessage);
        }

        CaptureScene(window, vm, outputDir, "summary", 780, 720, AppTheme.Light);
        CaptureScene(window, vm, outputDir, "summary", 780, 720, AppTheme.Dark);
        CaptureScene(window, vm, outputDir, "summary", 640, 560, AppTheme.Light);
        CaptureScene(window, vm, outputDir, "summary", 640, 560, AppTheme.Dark);

        window.Close();
    }

    private static void CaptureScene(NoteAssemblyWindow window, NoteAssemblyViewModel vm, string outputDir, string scene, int width, int height, AppTheme theme)
    {
        ThemeService.ApplyTheme(theme);
        vm.StatusMessage = scene switch
        {
            "setup" => "Настройка: два исходника и ---.",
            "preview" => "Предпросмотр трёх исходников, ***.",
            "destructive" => "Корзина: нужен отдельный checkbox.",
            _ => vm.SuccessSummary
        };

        string size = width >= 720 ? "wide" : "narrow";
        string themeName = theme == AppTheme.Dark ? "dark" : "light";
        string path = Path.Combine(outputDir, $"{scene}-{themeName}-{size}.png");
        ThreePaneUiSmokeRunner.RenderAndSaveScreenshot(window, path, width, height, w => PrepareSceneLayout(w, vm, scene));
        ThreePaneUiSmokeRunner.ValidateScreenshot(path, width, height);
    }

    private static void PrepareSceneLayout(Window window, NoteAssemblyViewModel vm, string scene)
    {
        window.UpdateLayout();
        if (scene == "summary")
        {
            var summary = Named<Border>(window, "AssemblySuccessSummaryPanel");
            summary.BringIntoView();
            window.UpdateLayout();
            AssertVisibleInClient(window, summary, "AssemblySuccessSummaryPanel");
            if (vm.ShowSetup)
            {
                throw new InvalidOperationException("Summary scene still shows setup controls.");
            }

            if (string.IsNullOrWhiteSpace(vm.SuccessSummary) || !vm.HasCompletedAssembly)
            {
                throw new InvalidOperationException("Summary scene is not a real post-commit state.");
            }

            AssertVisibleInClient(window, Named<Button>(window, "AssemblyCancelButton"), "AssemblyCancelButton");
            return;
        }

        var config = Named<TextBlock>(window, "AssemblyConfigSummaryText");
        var preview = Named<TextBox>(window, "AssemblyPreviewBox");
        var commit = Named<Button>(window, "AssemblyCommitButton");
        config.BringIntoView();
        preview.BringIntoView();
        ScrollTextBoxToHome(preview);
        window.UpdateLayout();

        AssertVisibleInClient(window, commit, "AssemblyCommitButton");
        AssertVisibleInClient(window, config, "AssemblyConfigSummaryText");
        AssertVisibleInClient(window, preview, "AssemblyPreviewBox");
        AssertSourceActionsUnclipped(window);

        if (scene == "preview")
        {
            string md = vm.PreviewMarkdown;
            if (!md.Contains("Полный предпросмотр сборки", StringComparison.Ordinal)
                || !md.Contains("Первый исходник", StringComparison.Ordinal)
                || !md.Contains("Второй исходник", StringComparison.Ordinal)
                || !md.Contains("Третий исходник", StringComparison.Ordinal)
                || !md.Contains("***", StringComparison.Ordinal)
                || !preview.Text.Contains("Первый исходник", StringComparison.Ordinal)
                || !preview.Text.Contains("хвост", StringComparison.Ordinal)
                || !config.Text.Contains("Полный предпросмотр сборки", StringComparison.Ordinal)
                || !config.Text.Contains("Встреча", StringComparison.Ordinal)
                || preview.ActualHeight < 100)
            {
                throw new InvalidOperationException(
                    "Preview scene does not simultaneously expose configuration and full Markdown bodies. " +
                    $"config='{config.Text}', previewH={preview.ActualHeight}, mdLen={md.Length}");
            }

            AssertUnclipped(window, preview, "AssemblyPreviewBox", minVisibleHeight: 100);
            var inner = FindVisualChild<ScrollViewer>(preview);
            if (inner != null)
            {
                inner.UpdateLayout();
                if (inner.ExtentHeight > inner.ViewportHeight + 2)
                {
                    throw new InvalidOperationException(
                        $"Preview Markdown is still clipped (extent={inner.ExtentHeight:F0} viewport={inner.ViewportHeight:F0}). Third source body would not appear on the acceptance frame.");
                }
            }
        }

        if (scene == "destructive")
        {
            var trash = Named<RadioButton>(window, "AssemblyTrashSourcesRadio");
            var ack = Named<CheckBox>(window, "AssemblyTrashAckCheckBox");
            var banner = Named<Border>(window, "AssemblyTrashWarningBanner");
            ack.BringIntoView();
            window.UpdateLayout();
            if (trash.IsChecked != true || ack.IsChecked == true || vm.TrashAcknowledged || vm.CanCommit || commit.IsEnabled)
            {
                throw new InvalidOperationException(
                    "Destructive scene must show selected trash mode, unchecked acknowledgement, and disabled Commit.");
            }

            if (ack.Content is not string ackText
                || ackText.IndexOf("корзин", StringComparison.OrdinalIgnoreCase) < 0
                || ackText.IndexOf("подтвержд", StringComparison.OrdinalIgnoreCase) < 0)
            {
                throw new InvalidOperationException("Acknowledgement checkbox text is missing.");
            }

            AssertVisibleInClient(window, trash, "AssemblyTrashSourcesRadio");
            AssertVisibleInClient(window, banner, "AssemblyTrashWarningBanner");
            AssertVisibleInClient(window, ack, "AssemblyTrashAckCheckBox");
            AssertUnclipped(window, ack, "AssemblyTrashAckCheckBox", minVisibleHeight: 16);
        }
    }

    private static void ScrollTextBoxToHome(TextBox box)
    {
        box.CaretIndex = 0;
        box.ScrollToHome();
        if (VisualTreeHelper.GetChildrenCount(box) == 0)
        {
            box.ApplyTemplate();
        }

        var inner = FindVisualChild<ScrollViewer>(box);
        inner?.ScrollToHome();
        box.UpdateLayout();
    }

    private static T Named<T>(DependencyObject root, string name) where T : FrameworkElement
    {
        if (root is FrameworkElement namedRoot)
        {
            if (namedRoot.FindName(name) is T direct)
            {
                return direct;
            }
        }

        var found = FindNamed<T>(root, name);
        if (found == null)
        {
            throw new InvalidOperationException($"Named element '{name}' was not found.");
        }

        return found;
    }

    public static void AssertVisibleInClient(Window window, FrameworkElement element, string name)
    {
        var content = (FrameworkElement)window.Content;
        if (!element.IsVisible || element.ActualWidth < 4 || element.ActualHeight < 4)
        {
            throw new InvalidOperationException($"{name} is not visible (w={element.ActualWidth}, h={element.ActualHeight}, vis={element.IsVisible}).");
        }

        var bounds = element.TransformToAncestor(content).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
        if (bounds.Left < -2 || bounds.Top < -2 || bounds.Right > content.ActualWidth + 2 || bounds.Bottom > content.ActualHeight + 2)
        {
            throw new InvalidOperationException(
                $"{name} is outside client bounds: {bounds} in {content.ActualWidth}x{content.ActualHeight}.");
        }
    }

    public static void AssertUnclipped(Window window, FrameworkElement element, string name, double minVisibleHeight)
    {
        var content = (FrameworkElement)window.Content;
        var bounds = element.TransformToAncestor(content).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
        double visible = Math.Min(bounds.Bottom, content.ActualHeight) - Math.Max(bounds.Top, 0);
        if (visible < minVisibleHeight)
        {
            throw new InvalidOperationException($"{name} visible height {visible:F1} < {minVisibleHeight} (bounds={bounds}).");
        }

        if (element.ActualHeight >= minVisibleHeight && visible + 1 < element.ActualHeight)
        {
            throw new InvalidOperationException($"{name} is clipped: visible={visible:F1} actual={element.ActualHeight:F1} (bounds={bounds}).");
        }
    }

    public static void AssertSourceActionsUnclipped(Window window)
    {
        var up = Named<Button>(window, "AssemblyMoveUpButton");
        var down = Named<Button>(window, "AssemblyMoveDownButton");
        var remove = Named<Button>(window, "AssemblyRemoveSourceButton");
        var candidates = Named<FrameworkElement>(window, "AssemblyCandidateRow");
        foreach (var button in new[] { up, down, remove })
        {
            if (!button.Focusable)
            {
                throw new InvalidOperationException($"{button.Name} must remain keyboard-focusable.");
            }

            string automationName = System.Windows.Automation.AutomationProperties.GetName(button);
            if (string.IsNullOrWhiteSpace(automationName))
            {
                throw new InvalidOperationException($"{button.Name} is missing AutomationProperties.Name.");
            }

            AssertVisibleInClient(window, button, button.Name);
            AssertUnclipped(window, button, button.Name, minVisibleHeight: 24);
            AssertDoesNotOverlap(window, button, candidates, button.Name, "AssemblyCandidateRow");
        }

        AssertVisibleInClient(window, candidates, "AssemblyCandidateRow");
        AssertUnclipped(window, candidates, "AssemblyCandidateRow", minVisibleHeight: 24);
    }

    public static void AssertDoesNotOverlap(Window window, FrameworkElement a, FrameworkElement b, string nameA, string nameB)
    {
        var content = (FrameworkElement)window.Content;
        var boundsA = a.TransformToAncestor(content).TransformBounds(new Rect(0, 0, a.ActualWidth, a.ActualHeight));
        var boundsB = b.TransformToAncestor(content).TransformBounds(new Rect(0, 0, b.ActualWidth, b.ActualHeight));
        var overlap = Rect.Intersect(boundsA, boundsB);
        if (!overlap.IsEmpty && overlap.Width > 1 && overlap.Height > 1)
        {
            throw new InvalidOperationException($"{nameA} overlaps {nameB}: {overlap} (a={boundsA}, b={boundsB}).");
        }
    }

    private static T? FindNamed<T>(DependencyObject root, string name) where T : FrameworkElement
    {
        if (root is FrameworkElement fe && fe.Name == name && root is T match)
        {
            return match;
        }

        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            var found = FindNamed<T>(child, name);
            if (found != null)
            {
                return found;
            }
        }

        if (root is ContentControl cc && cc.Content is DependencyObject content)
        {
            return FindNamed<T>(content, name);
        }

        return null;
    }

    private static T? FindVisualChild<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T match)
        {
            return match;
        }

        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var found = FindVisualChild<T>(VisualTreeHelper.GetChild(root, i));
            if (found != null)
            {
                return found;
            }
        }

        return null;
    }
}
