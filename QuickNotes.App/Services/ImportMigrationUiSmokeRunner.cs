using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using QuickNotes.App.Data;
using QuickNotes.App.Helpers;
using QuickNotes.App.Models;
using QuickNotes.App.Services.Sync;
using QuickNotes.App.ViewModels;
using QuickNotes.App.Views;

namespace QuickNotes.App.Services;

public static class ImportMigrationUiSmokeRunner
{
    public const string ReplaceRelativePath = "Работа/replace-me.md";

    public static void Attach(MainWindow window, string? explicitOutputDir)
    {
        window.Loaded += async (_, _) =>
        {
            try
            {
                await Task.Delay(80);
                string outputDir = ResolveOutputDir(explicitOutputDir);
                Run(outputDir);
                Console.WriteLine("QN_IMPORT_MIGRATION_SMOKE_SUCCESS");
                System.Windows.Application.Current.Shutdown(0);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"QN_IMPORT_MIGRATION_SMOKE_ERROR:{ex}");
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
        return Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "artifacts", "import-migration-acceptance"));
    }

    public static void Run(string outputDir)
    {
        Directory.CreateDirectory(outputDir);
        string fixtureRoot = CreateFixtureTree();
        try
        {
            var vm = CreateViewModel();
            vm.MessageBoxProvider = (_, _, _) => { };
            vm.SelectedTabIndex = 1;
            var window = new ImportExportWindow(vm);
            window.Show();
            window.UpdateLayout();

            using (var db = new QuickNotesDbContext())
            {
                db.Notes.Add(new Note
                {
                    Title = "План",
                    Text = "Существующая заметка с тем же заголовком."
                });
                db.Notes.Add(new Note
                {
                    Title = "Старый replace",
                    Text = "Предыдущая версия файла источника.",
                    ImportSourceRelativePath = ReplaceRelativePath,
                    ImportSourceFingerprint = "seed-replace-fingerprint"
                });
                db.SaveChanges();
                vm.LoadPreview(() => new NoteImportService().BuildPreviewFromDirectory(db, fixtureRoot));
            }

            CaptureScene(window, vm, outputDir, "mapping", 780, 720, AppTheme.Light, ack: false);
            CaptureScene(window, vm, outputDir, "mapping", 780, 720, AppTheme.Dark, ack: false);
            CaptureScene(window, vm, outputDir, "mapping", 640, 560, AppTheme.Light, ack: false);
            CaptureScene(window, vm, outputDir, "mapping", 640, 560, AppTheme.Dark, ack: false);

            CaptureScene(window, vm, outputDir, "diagnostics", 780, 720, AppTheme.Light, ack: false);
            CaptureScene(window, vm, outputDir, "diagnostics", 780, 720, AppTheme.Dark, ack: false);
            CaptureScene(window, vm, outputDir, "diagnostics", 640, 560, AppTheme.Light, ack: false);
            CaptureScene(window, vm, outputDir, "diagnostics", 640, 560, AppTheme.Dark, ack: false);

            ApplyDuplicateResolutions(vm);
            CaptureScene(window, vm, outputDir, "duplicates", 780, 720, AppTheme.Light, ack: true);
            CaptureScene(window, vm, outputDir, "duplicates", 780, 720, AppTheme.Dark, ack: true);
            CaptureScene(window, vm, outputDir, "duplicates", 640, 560, AppTheme.Light, ack: true);
            CaptureScene(window, vm, outputDir, "duplicates", 640, 560, AppTheme.Dark, ack: true);

            vm.LossesAcknowledged = true;
            if (vm.ConfirmImportCommand is RelayCommand confirm)
            {
                confirm.RaiseCanExecuteChanged();
            }

            if (!vm.CanConfirmImport)
            {
                throw new InvalidOperationException("Smoke import could not commit after acknowledgement.");
            }

            vm.ExecuteConfirmImport();
            if (!vm.HasCompletedImport)
            {
                throw new InvalidOperationException(vm.ImportStatusMessage);
            }

            window.UpdateLayout();
            window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Render);

            CaptureScene(window, vm, outputDir, "summary", 780, 720, AppTheme.Light, ack: true);
            CaptureScene(window, vm, outputDir, "summary", 780, 720, AppTheme.Dark, ack: true);
            CaptureScene(window, vm, outputDir, "summary", 640, 560, AppTheme.Light, ack: true);
            CaptureScene(window, vm, outputDir, "summary", 640, 560, AppTheme.Dark, ack: true);

            window.Close();
        }
        finally
        {
            try { Directory.Delete(fixtureRoot, true); } catch { /* ignore */ }
        }
    }

    private static void ApplyDuplicateResolutions(ImportExportViewModel vm)
    {
        var conflicts = vm.PreviewItems.Where(i => i.IsConflict).ToList();
        var replace = conflicts.FirstOrDefault(i => i.CanReplace);
        if (replace != null)
        {
            vm.ChangeDuplicateAction(replace, ImportDuplicateAction.Replace);
        }

        var separate = conflicts.FirstOrDefault(i => !i.CanReplace && i.DuplicateKind == ImportDuplicateKind.SameTitle);
        if (separate != null)
        {
            vm.ChangeDuplicateAction(separate, ImportDuplicateAction.ImportSeparate);
        }

        var skip = conflicts.FirstOrDefault(i =>
            i != replace && i != separate);
        if (skip != null)
        {
            vm.ChangeDuplicateAction(skip, ImportDuplicateAction.Skip);
        }

        var ordered = vm.PreviewItems.OrderByDescending(i => i.IsConflict).ToList();
        vm.PreviewItems.Clear();
        foreach (var item in ordered)
        {
            vm.PreviewItems.Add(item);
        }
    }

    private static ImportExportViewModel CreateViewModel()
    {
        return new ImportExportViewModel(
            () => new QuickNotesDbContext(),
            new LocalMutationCoordinator(),
            attachmentStorage: new AttachmentStorageService());
    }

    private static void CaptureScene(ImportExportWindow window, ImportExportViewModel vm, string outputDir, string scene, int width, int height, AppTheme theme, bool ack)
    {
        ThemeService.ApplyTheme(theme);
        vm.SelectedTabIndex = 1;
        if (!vm.HasCompletedImport)
        {
            vm.LossesAcknowledged = ack;
        }

        if (vm.ConfirmImportCommand is RelayCommand confirm)
        {
            confirm.RaiseCanExecuteChanged();
        }

        vm.ImportStatusMessage = scene switch
        {
            "mapping" => "Сопоставление: блокнот → корневой тег, раздел → дочерний. Вложенность глубже раздела не сглаживается молча.",
            "diagnostics" => "Диагностика потерь: script/iframe отброшены, Word/OneNote не разбираются. Commit недоступен без подтверждения.",
            "duplicates" => "Конфликты: «Импортировать отдельно», «Пропустить» и «Заменить существующую» где источник совпал.",
            _ => vm.LastImportSummary
        };

        string size = width >= 720 ? "wide" : "narrow";
        string themeName = theme == AppTheme.Dark ? "dark" : "light";
        string path = Path.Combine(outputDir, $"{scene}-{themeName}-{size}.png");
        ThreePaneUiSmokeRunner.RenderAndSaveScreenshot(window, path, width, height, w => PrepareSceneLayout(w, scene));
        ThreePaneUiSmokeRunner.ValidateScreenshot(path, width, height);
    }

    private static void PrepareSceneLayout(Window window, string scene)
    {
        var viewer = FindImportScrollViewer(window);
        if (viewer == null)
        {
            return;
        }

        window.UpdateLayout();
        viewer.UpdateLayout();

        if (scene == "mapping")
        {
            var mapping = FindByAutomationId(window, "ImportFolderMappingPanel");
            mapping?.BringIntoView();
            viewer.ScrollToVerticalOffset(0);
            return;
        }

        if (scene == "diagnostics")
        {
            FrameworkElement? diagnostics = null;
            if (window is ImportExportWindow importWindow)
            {
                diagnostics = importWindow.ImportDiagnosticsBanner;
            }

            diagnostics ??= FindByAutomationId(window, "ImportDiagnosticsBanner")
                ?? FindByAutomationId(window, "ImportDiagnosticsList")
                ?? FindByName(window, "Диагностика импорта");
            if (diagnostics == null)
            {
                throw new InvalidOperationException("Diagnostics banner was not found.");
            }

            try
            {
                var pos = diagnostics.TransformToAncestor(viewer).Transform(new System.Windows.Point(0, 0));
                viewer.ScrollToVerticalOffset(Math.Max(0, viewer.VerticalOffset + pos.Y));
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("Could not scroll diagnostics banner into view.", ex);
            }

            return;
        }

        if (scene == "duplicates")
        {
            var combo = FindByAutomationId(window, "ImportDuplicateActionCombo");
            if (combo != null)
            {
                combo.BringIntoView();
                try
                {
                    var pos = combo.TransformToAncestor(viewer).Transform(new System.Windows.Point(0, 0));
                    viewer.ScrollToVerticalOffset(Math.Max(0, viewer.VerticalOffset + pos.Y - 8));
                    return;
                }
                catch
                {
                    // fall through
                }
            }

            viewer.ScrollToBottom();
            return;
        }

        if (scene == "summary")
        {
            var summary = FindByAutomationId(window, "ImportSuccessSummaryPanel");
            if (summary == null)
            {
                throw new InvalidOperationException("Import success summary panel was not in the visual tree.");
            }

            summary.BringIntoView();
            viewer.ScrollToVerticalOffset(0);
            window.UpdateLayout();
            if (summary.ActualHeight < 80 || summary.Visibility != Visibility.Visible)
            {
                throw new InvalidOperationException($"Import success summary not visible (h={summary.ActualHeight}, vis={summary.Visibility}).");
            }
        }
    }

    private static ScrollViewer? FindImportScrollViewer(DependencyObject root)
    {
        int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer sv
                && (string.Equals(System.Windows.Automation.AutomationProperties.GetAutomationId(sv), "ImportPreviewScrollViewer", StringComparison.Ordinal)
                    || string.Equals(System.Windows.Automation.AutomationProperties.GetName(sv), "Прокрутка предпросмотра импорта", StringComparison.Ordinal)))
            {
                return sv;
            }

            var nested = FindImportScrollViewer(child);
            if (nested != null)
            {
                return nested;
            }
        }

        return null;
    }

    private static FrameworkElement? FindByAutomationId(DependencyObject root, string id)
    {
        int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is FrameworkElement fe)
            {
                if (System.Windows.Automation.AutomationProperties.GetAutomationId(fe) == id)
                {
                    return fe;
                }

                var nested = FindByAutomationId(fe, id);
                if (nested != null)
                {
                    return nested;
                }
            }
        }

        return null;
    }

    private static FrameworkElement? FindByName(DependencyObject root, string name)
    {
        int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is FrameworkElement fe)
            {
                if (System.Windows.Automation.AutomationProperties.GetName(fe) == name)
                {
                    return fe;
                }

                var nested = FindByName(fe, name);
                if (nested != null)
                {
                    return nested;
                }
            }
        }

        return null;
    }

    public static string CreateFixtureTree()
    {
        string root = Path.Combine(Path.GetTempPath(), "qn_import_smoke_" + Guid.NewGuid().ToString("N"), "Архив блокнот");
        Directory.CreateDirectory(Path.Combine(root, "Работа"));
        Directory.CreateDirectory(Path.Combine(root, "Работа", "Вложенный"));
        File.WriteAllText(Path.Combine(root, "Работа", "план.md"), "# План\n\nТекст **важный**.", Encoding.UTF8);
        File.WriteAllText(Path.Combine(root, "Работа", "Вложенный", "extra.md"), "# Extra\n\nГлубже раздела.", Encoding.UTF8);
        File.WriteAllText(Path.Combine(root, "Работа", "letter.html"), """
            <html><body>
            <h1>Письмо</h1>
            <p>Кириллица <strong>жирный</strong> и <em>курсив</em>.</p>
            <ul><li>один</li><li><input type="checkbox"> задача</li></ul>
            <script>alert(1)</script>
            <iframe src="https://example.com"></iframe>
            <img src="https://evil.example/x.png">
            <table><tr><th>A</th><th>B</th></tr><tr><td>1</td><td>2</td></tr></table>
            </body></html>
            """, Encoding.UTF8);
        File.WriteAllText(Path.Combine(root, "Работа", "same-title.md"), "# План\n\nДругое содержимое с тем же заголовком.", Encoding.UTF8);
        File.WriteAllText(Path.Combine(root, "Работа", "replace-me.md"), "# Replace me\n\nНовая версия источника для замены.", Encoding.UTF8);
        File.WriteAllBytes(Path.Combine(root, "Работа", "legacy.docx"), new byte[] { 0, 1, 2, 0 });
        File.WriteAllBytes(Path.Combine(root, "Работа", "Вложенный", "page.one"), new byte[] { 0, 9, 8 });
        return root;
    }
}
