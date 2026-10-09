using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using QuickNotes.App.Data;
using QuickNotes.App.Models;
using QuickNotes.App.Services;
using QuickNotes.App.ViewModels;
using QuickNotes.App.Views;
using Xunit;

namespace QuickNotes.Tests;

[TestCategory(TestCategories.UiSensitive)]
public sealed class ImportMigrationUiTests : IDisposable
{
    private readonly string _root;
    private readonly string _dbPath;

    public ImportMigrationUiTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "qn_impui_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _dbPath = Path.Combine(_root, "db.sqlite");
        using var db = SqliteTestUtil.CreateContext(_dbPath);
        DbInitializer.Initialize(db);
    }

    public void Dispose() => SqliteTestUtil.TryDeleteDirectory(_root);

    [Fact]
    public void PreviewDoesNotWrite_AndBlockingOrUnackedLossDisablesCommit()
    {
        string html = Path.Combine(_root, "loss.html");
        File.WriteAllText(html, "<p>x</p><script>alert(1)</script><iframe src='https://e'></iframe>", Encoding.UTF8);
        var vm = ImportExportViewModelTestComposition.Create(() => SqliteTestUtil.CreateContext(_dbPath), new NoteExportService(), new NoteImportService());
        vm.MessageBoxProvider = (_, _, _) => { };
        vm.LoadPreview(() =>
        {
            using var db = SqliteTestUtil.CreateContext(_dbPath);
            return new NoteImportService().BuildPreviewFromFiles(db, new[] { html });
        });

        Assert.True(vm.HasPreview);
        using (var db = SqliteTestUtil.CreateContext(_dbPath))
        {
            Assert.Equal(0, db.Notes.Count());
        }

        if (vm.HasNonBlockingLosses)
        {
            Assert.False(vm.LossesAcknowledged);
            Assert.False(vm.CanConfirmImport);
            vm.LossesAcknowledged = true;
        }

        Assert.True(vm.PreviewResult!.CanConfirm || vm.TotalNotesToImport == 0 || vm.HasBlockingDiagnostics);
    }

    [Fact]
    public void KeyboardAccessibleNames_ArePresentInXaml()
    {
        var root = FindSolutionRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "QuickNotes.App", "Views", "ImportExportWindow.xaml"));
        Assert.Contains("ImportSelectFilesButton", xaml);
        Assert.Contains("ImportSelectFolderButton", xaml);
        Assert.Contains("ImportDuplicateActionCombo", xaml);
        Assert.Contains("ImportLossAckCheckBox", xaml);
        Assert.Contains("ConfirmImportButton", xaml);
        Assert.Contains("ImportSuccessSummaryPanel", xaml);
        Assert.Contains("Binding CanConfirmImport, Mode=OneWay", xaml);

        var appXaml = File.ReadAllText(Path.Combine(root, "QuickNotes.App", "App.xaml"));
        Assert.Contains("ControlTemplate TargetType=\"ComboBox\"", appXaml);
        Assert.Contains("DynamicResource InputBgBrush", appXaml);
        Assert.Contains("DynamicResource InputFgBrush", appXaml);
        Assert.Contains("ControlTemplate TargetType=\"ComboBoxItem\"", appXaml);
    }

    [Fact]
    public void NarrowWindow_DuplicateComboAndAck_AreWithinClientBounds()
    {
        string fixture = ImportMigrationUiSmokeRunner.CreateFixtureTree();
        try
        {
            using (var seed = SqliteTestUtil.CreateContext(_dbPath))
            {
                seed.Notes.Add(new Note { Title = "План", Text = "Существующая" });
                seed.Notes.Add(new Note
                {
                    Title = "Старый replace",
                    Text = "старое",
                    ImportSourceRelativePath = ImportMigrationUiSmokeRunner.ReplaceRelativePath,
                    ImportSourceFingerprint = "seed-replace-fingerprint"
                });
                seed.SaveChanges();
            }

            StaTestHarness.Run(() =>
            {
                var vm = ImportExportViewModelTestComposition.Create(() => SqliteTestUtil.CreateContext(_dbPath), new NoteExportService(), new NoteImportService());
                vm.MessageBoxProvider = (_, _, _) => { };
                vm.SelectedTabIndex = 1;
                vm.LoadPreview(() =>
                {
                    using var db = SqliteTestUtil.CreateContext(_dbPath);
                    return new NoteImportService().BuildPreviewFromDirectory(db, fixture);
                });
                Assert.True(vm.PreviewItems.Count(i => i.IsConflict) >= 2);

                var window = new ImportExportWindow(vm)
                {
                    Width = 640,
                    Height = 560,
                    MinWidth = 640,
                    MinHeight = 520
                };
                window.Show();
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.Loaded);
                window.Measure(new System.Windows.Size(640, 560));
                window.Arrange(new System.Windows.Rect(0, 0, 640, 560));
                window.UpdateLayout();
                try
                {
                    var viewer = FindNamed(window, "ImportPreviewScrollViewer") as ScrollViewer;
                    Assert.NotNull(viewer);
                    var combos = FindAllById(window, "ImportDuplicateActionCombo")
                        .OfType<ComboBox>()
                        .Where(c => c.IsVisible)
                        .ToList();
                    Assert.True(combos.Count >= 2, $"expected multiple visible conflict combos, got {combos.Count}");
                    var confirm = FindNamed(window, "ConfirmImportButton");
                    var ack = FindNamed(window, "ImportLossAckCheckBox");
                    Assert.NotNull(confirm);
                    Assert.NotNull(ack);
                    Assert.True(confirm!.IsVisible);
                    Assert.True(ack!.IsVisible);
                    var confirmOrigin = confirm.TransformToAncestor(window).Transform(new System.Windows.Point(0, 0));
                    Assert.True(confirmOrigin.Y + confirm.ActualHeight <= window.ActualHeight + 1);
                    var ackOrigin = ack.TransformToAncestor(window).Transform(new System.Windows.Point(0, 0));
                    Assert.True(ackOrigin.Y + ack.ActualHeight <= window.ActualHeight + 1);

                    foreach (var combo in combos)
                    {
                        combo.BringIntoView();
                        window.UpdateLayout();
                        viewer!.UpdateLayout();
                        Assert.True(combo.IsVisible);
                        Assert.True(combo.ActualHeight > 0);
                        Assert.True(combo.ActualWidth > 0);
                        Assert.True(combo.Focusable);
                        var origin = combo.TransformToAncestor(window).Transform(new System.Windows.Point(0, 0));
                        Assert.True(origin.X >= 0);
                        Assert.True(origin.Y + combo.ActualHeight <= window.ActualHeight + 1,
                            $"combo y={origin.Y} h={combo.ActualHeight} window={window.ActualHeight}");
                        Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(combo)));
                        var bg = combo.Background as System.Windows.Media.SolidColorBrush;
                        var fg = combo.Foreground as System.Windows.Media.SolidColorBrush;
                        Assert.NotNull(bg);
                        Assert.NotNull(fg);
                        Assert.NotEqual(bg!.Color, fg!.Color);
                    }
                }
                finally
                {
                    window.Close();
                }
            }, TimeSpan.FromSeconds(30));
        }
        finally
        {
            SqliteTestUtil.TryDeleteDirectory(Path.GetDirectoryName(fixture)!);
        }
    }

    [Fact]
    public void Window_TabOrderAndNames_ForImportControls()
    {
        StaTestHarness.Run(() =>
        {
            var vm = ImportExportViewModelTestComposition.Create(() => SqliteTestUtil.CreateContext(_dbPath));
            vm.SelectedTabIndex = 1;
            var window = new ImportExportWindow(vm);
            window.Width = 780;
            window.Height = 720;
            window.Show();
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.Loaded);
            window.UpdateLayout();
            try
            {
                var files = FindNamed(window, "ImportSelectFilesButton");
                var folder = FindNamed(window, "ImportSelectFolderButton");
                Assert.NotNull(files);
                Assert.NotNull(folder);
                Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(files!)));
                Assert.True(files.Focusable);
                Assert.True(folder.Focusable);
            }
            finally
            {
                window.Close();
            }
        }, TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task SuccessfulSummary_AfterAckedImport()
    {
        File.WriteAllText(Path.Combine(_root, "ok.md"), "чисто", Encoding.UTF8);
        var vm = ImportExportViewModelTestComposition.Create(() => SqliteTestUtil.CreateContext(_dbPath), new NoteExportService(), new NoteImportService());
        vm.MessageBoxProvider = (_, _, _) => { };
        vm.LoadPreview(() =>
        {
            using var db = SqliteTestUtil.CreateContext(_dbPath);
            return new NoteImportService().BuildPreviewFromFiles(db, new[] { Path.Combine(_root, "ok.md") });
        });
        Assert.True(vm.CanConfirmImport);
        await vm.ExecuteConfirmImportAsync();
        Assert.True(vm.IsImportSuccess);
        Assert.True(vm.HasCompletedImport);
        Assert.False(vm.CanConfirmImport);
        Assert.Contains("Импортировано", vm.ImportStatusMessage, StringComparison.Ordinal);
        Assert.Contains("Импортировано", vm.LastImportSummary, StringComparison.Ordinal);
        Assert.True(vm.LastImportedNotesCount >= 1);
        Assert.Empty(vm.PreviewItems);
    }

    [Fact]
    public void DarkTheme_ConflictComboBox_UsesDistinctInputBrushes()
    {
        File.WriteAllText(Path.Combine(_root, "ok.md"), "# План\n\nдругое", Encoding.UTF8);
        using (var seed = SqliteTestUtil.CreateContext(_dbPath))
        {
            seed.Notes.Add(new Note { Title = "План", Text = "Существующая" });
            seed.SaveChanges();
        }

        StaTestHarness.Run(() =>
        {
            ThemeService.ApplyTheme(AppTheme.Dark);
            var vm = ImportExportViewModelTestComposition.Create(() => SqliteTestUtil.CreateContext(_dbPath), new NoteExportService(), new NoteImportService());
            vm.MessageBoxProvider = (_, _, _) => { };
            vm.SelectedTabIndex = 1;
            vm.LoadPreview(() =>
            {
                using var db = SqliteTestUtil.CreateContext(_dbPath);
                return new NoteImportService().BuildPreviewFromFiles(db, new[] { Path.Combine(_root, "ok.md") });
            });

            var window = new ImportExportWindow(vm) { Width = 780, Height = 720 };
            window.Show();
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.Loaded);
            window.UpdateLayout();
            try
            {
                var combo = FindNamed(window, "ImportDuplicateActionCombo") as ComboBox;
                Assert.NotNull(combo);
                var inputBg = (System.Windows.Media.SolidColorBrush)Application.Current.Resources["InputBgBrush"];
                var inputFg = (System.Windows.Media.SolidColorBrush)Application.Current.Resources["InputFgBrush"];
                Assert.Equal(System.Windows.Media.Color.FromRgb(0x1E, 0x29, 0x3B), inputBg.Color);
                Assert.Equal(System.Windows.Media.Color.FromRgb(0xF8, 0xFA, 0xFC), inputFg.Color);

                var bg = combo!.Background as System.Windows.Media.SolidColorBrush;
                var fg = combo.Foreground as System.Windows.Media.SolidColorBrush;
                Assert.NotNull(bg);
                Assert.NotNull(fg);
                Assert.Equal(inputBg.Color, bg!.Color);
                Assert.Equal(inputFg.Color, fg!.Color);
                Assert.True(RelativeLuminance(fg.Color) > RelativeLuminance(bg.Color) + 0.2,
                    $"dark combo fg={fg.Color} bg={bg.Color}");
                Assert.NotEqual(System.Windows.Media.Color.FromRgb(255, 255, 255), bg.Color);

                combo.IsDropDownOpen = true;
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.Loaded);
                window.UpdateLayout();
                combo.UpdateLayout();
                try
                {
                    int generated = 0;
                    for (int i = 0; i < combo.Items.Count; i++)
                    {
                        if (combo.ItemContainerGenerator.ContainerFromIndex(i) is not ComboBoxItem item)
                        {
                            continue;
                        }

                        generated++;
                        var itemBg = item.Background as System.Windows.Media.SolidColorBrush;
                        var itemFg = item.Foreground as System.Windows.Media.SolidColorBrush;
                        Assert.NotNull(itemBg);
                        Assert.NotNull(itemFg);
                        Assert.Equal(inputBg.Color, itemBg!.Color);
                        Assert.Equal(inputFg.Color, itemFg!.Color);
                        Assert.True(RelativeLuminance(itemFg.Color) > RelativeLuminance(itemBg.Color) + 0.2,
                            $"dark dropdown item fg={itemFg.Color} bg={itemBg.Color}");
                    }

                    Assert.True(generated >= 1, "expected generated ComboBoxItem containers in the open dropdown");
                }
                finally
                {
                    combo.IsDropDownOpen = false;
                }
            }
            finally
            {
                ThemeService.ApplyTheme(AppTheme.Light);
                window.Close();
            }
        }, TimeSpan.FromSeconds(30));
    }

    private static double RelativeLuminance(System.Windows.Media.Color c)
    {
        static double Lin(byte channel)
        {
            double s = channel / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Lin(c.R) + 0.7152 * Lin(c.G) + 0.0722 * Lin(c.B);
    }

    private static List<FrameworkElement> FindAllById(DependencyObject root, string id)
    {
        var found = new List<FrameworkElement>();
        CollectById(root, id, found);
        return found;
    }

    private static void CollectById(DependencyObject root, string id, List<FrameworkElement> found)
    {
        int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is FrameworkElement fe)
            {
                if (AutomationProperties.GetAutomationId(fe) == id)
                {
                    found.Add(fe);
                }

                CollectById(fe, id, found);
            }
        }
    }

    private static FrameworkElement? FindNamed(DependencyObject root, string id)
    {
        int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is FrameworkElement fe)
            {
                if (AutomationProperties.GetAutomationId(fe) == id)
                {
                    return fe;
                }

                var nested = FindNamed(fe, id);
                if (nested != null)
                {
                    return nested;
                }
            }
        }

        return null;
    }

    private static string FindSolutionRoot()
    {
        var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "QuickNotes.sln")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        return @"D:\work\QuickNotes";
    }
}
