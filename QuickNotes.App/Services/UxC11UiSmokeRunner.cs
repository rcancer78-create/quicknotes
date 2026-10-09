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
using QuickNotes.App.Models.Sync;
using QuickNotes.App.ViewModels;
using QuickNotes.App.Views;
using TextBlock = System.Windows.Controls.TextBlock;

namespace QuickNotes.App.Services;

public static class UxC11UiSmokeRunner
{
    public const string LocalTitle = "Черновик для C11";
    public const string PendingTitle = "Очередь для C11";
    public const string CloudTitle = "Успешная копия C11";
    public const string ConflictTitle = "Расхождение версий C11";
    public const string ErrorTitle = "Сбой передачи C11";
    public const string SuccessMarker = "QN_UX_C11_SMOKE_SUCCESS";

    public static readonly string[] ExpectedFileNames =
    {
        "status-local-light-wide.png", "status-local-dark-wide.png",
        "status-pending-light-wide.png", "status-pending-dark-wide.png",
        "status-cloud-light-wide.png", "status-cloud-dark-wide.png",
        "status-conflict-light-wide.png", "status-conflict-dark-wide.png",
        "status-error-light-wide.png", "status-error-dark-wide.png"
    };

    private static readonly (string Prefix, string Title, LocalCommitSyncStatus Status, string Label, bool OnCard)[] Scenes =
    {
        ("status-local", LocalTitle, LocalCommitSyncStatus.SavedLocally, PublicationStatusCopy.SavedOnThisPc, false),
        ("status-pending", PendingTitle, LocalCommitSyncStatus.PendingUpload, PublicationStatusCopy.WaitingToSend, true),
        ("status-cloud", CloudTitle, LocalCommitSyncStatus.Synchronized, PublicationStatusCopy.InCloud, false),
        ("status-conflict", ConflictTitle, LocalCommitSyncStatus.Conflict, PublicationStatusCopy.Conflict, true),
        ("status-error", ErrorTitle, LocalCommitSyncStatus.Error, PublicationStatusCopy.Error, true)
    };

    public static void Attach(MainWindow window, string? explicitOutputDir)
    {
        window.Loaded += async (_, _) =>
        {
            try
            {
                Console.WriteLine("QN_UX_C11_SMOKE_STAGE:loaded");
                SuppressBlockingFirstRun(window);
                await Task.Delay(80);
                string outputDir = ResolveOutputDir(explicitOutputDir);
                Run(window, outputDir);
                Console.WriteLine(SuccessMarker);
                Console.WriteLine("QN_UX_C11_SMOKE_STAGE:shutdown");
                System.Windows.Application.Current.Shutdown(0);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"QN_UX_C11_SMOKE_ERROR:{ex}");
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
        return Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "artifacts", "ux-c11-acceptance"));
    }

    public static void ValidateOutput(string path, string fileName)
    {
        var (width, height) = ExpectedSize(fileName);
        ThreePaneUiSmokeRunner.ValidateScreenshot(path, width, height, minBytes: 25_000, minDistinctColors: 30);
    }

    public static (int Width, int Height) ExpectedSize(string fileName)
    {
        _ = fileName;
        return (1100, 720);
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
        Console.WriteLine("QN_UX_C11_SMOKE_STAGE:seed");
        SeedCards(vm);
        vm.ReloadAll();
        ApplyStatuses(vm);
        ThreePaneUiSmokeRunner.DoEvents();

        foreach (var scene in Scenes)
        {
            foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark })
            {
                Console.WriteLine($"QN_UX_C11_SMOKE_STAGE:{scene.Prefix}:{theme}");
                ThemeService.ApplyTheme(theme);
                vm.IsNarrow = false;
                vm.IsDetailActiveInNarrow = false;
                ApplyStatuses(vm);
                SelectNote(vm, scene.Title);
                if (window is MainWindow mwSelect && vm.SelectedNote != null)
                {
                    mwSelect.NotesListBox.ScrollIntoView(vm.SelectedNote);
                }
                ThreePaneUiSmokeRunner.DoEvents();
                window.UpdateLayout();

                string fileName = $"{scene.Prefix}-{(theme == AppTheme.Dark ? "dark" : "light")}-wide.png";
                var (width, height) = ExpectedSize(fileName);
                string path = Path.Combine(outputDir, fileName);
                ThreePaneUiSmokeRunner.RenderAndSaveScreenshot(window, path, width, height, w =>
                {
                    ApplyStatuses(vm);
                    SelectNote(vm, scene.Title);
                    if (w is MainWindow mwShot && vm.SelectedNote != null)
                    {
                        mwShot.NotesListBox.ScrollIntoView(vm.SelectedNote);
                    }
                    w.UpdateLayout();
                    AssertScene(w, vm, scene.Label, scene.OnCard, scene.Title);
                    string visible = UxPackageAbUiSmokeRunner.CollectVisibleUiText(w);
                    if (string.IsNullOrWhiteSpace(visible)
                        || !visible.Contains(scene.Title, StringComparison.Ordinal)
                        || !visible.Contains(scene.Label, StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException("Visible UI text must contain the scene title and first-level status.");
                    }

                    File.WriteAllText(VisibleTextDumpPath(path), visible, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                });
                ValidateOutput(path, fileName);
            }
        }
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

    private static void SeedCards(MainViewModel vm)
    {
        using var db = vm.ContextFactory();
        string[] titles = { LocalTitle, PendingTitle, CloudTitle, ConflictTitle, ErrorTitle };
        if (db.Notes.Any(n => titles.Contains(n.Title)))
        {
            return;
        }

        var now = DateTime.Now;
        db.Notes.AddRange(
            new Note { Title = LocalTitle, Text = "Локальный черновик без облака.", IsInbox = false, CreatedAt = now.AddHours(-5), UpdatedAt = now.AddMinutes(-9) },
            new Note { Title = PendingTitle, Text = "Локально сохранено, очередь на отправку.", IsInbox = false, CreatedAt = now.AddHours(-4), UpdatedAt = now.AddMinutes(-7) },
            new Note { Title = CloudTitle, Text = "Уже есть облачная копия.", IsInbox = false, CreatedAt = now.AddHours(-3), UpdatedAt = now.AddMinutes(-5) },
            new Note { Title = ConflictTitle, Text = "Нужно выбрать версию.", IsInbox = false, CreatedAt = now.AddHours(-2), UpdatedAt = now.AddMinutes(-3) },
            new Note { Title = ErrorTitle, Text = "Отправка не удалась.", IsInbox = false, CreatedAt = now.AddHours(-1), UpdatedAt = now.AddMinutes(-1) });
        db.SaveChanges();
    }

    private static void ApplyStatuses(MainViewModel vm)
    {
        SetStatus(vm, LocalTitle, LocalCommitSyncStatus.SavedLocally);
        SetStatus(vm, PendingTitle, LocalCommitSyncStatus.PendingUpload);
        SetStatus(vm, CloudTitle, LocalCommitSyncStatus.Synchronized);
        SetStatus(vm, ConflictTitle, LocalCommitSyncStatus.Conflict);
        SetStatus(vm, ErrorTitle, LocalCommitSyncStatus.Error);
    }

    private static void SetStatus(MainViewModel vm, string title, LocalCommitSyncStatus status)
    {
        var card = vm.Notes.FirstOrDefault(n => n.DisplayTitle == title)
            ?? throw new InvalidOperationException("Smoke note was not loaded: " + title);
        card.PublicationStatus = status;
    }

    private static void SelectNote(MainViewModel vm, string title)
    {
        var card = vm.Notes.FirstOrDefault(n => n.DisplayTitle == title)
            ?? throw new InvalidOperationException("Cannot select missing note: " + title);
        if (ReferenceEquals(vm.SelectedNote, card))
        {
            var other = vm.Notes.FirstOrDefault(n => !ReferenceEquals(n, card));
            if (other != null)
            {
                vm.SelectedNote = other;
            }
        }

        vm.SelectedNote = card;
        if (vm.SelectedNote?.DisplayTitle != title)
        {
            throw new InvalidOperationException("Failed to select note: " + title);
        }
    }

    private static void AssertScene(Window window, MainViewModel vm, string expectedLabel, bool expectOnCard, string title)
    {
        if (window is not MainWindow mw)
        {
            throw new InvalidOperationException("Expected MainWindow.");
        }

        if (vm.SelectedNote?.DisplayTitle != title)
        {
            throw new InvalidOperationException("Selected note is not the scene note: " + title);
        }

        if (vm.SyncStatusSummaryText != expectedLabel)
        {
            throw new InvalidOperationException($"Status bar first-level was '{vm.SyncStatusSummaryText}', expected '{expectedLabel}'.");
        }

        var bar = mw.PublicationStatusBarText;
        if (bar.Visibility != Visibility.Visible || bar.Text != expectedLabel)
        {
            throw new InvalidOperationException("PublicationStatusBarText must show the first-level status.");
        }

        var barButton = mw.PublicationStatusBarButton;
        if (barButton.Visibility != Visibility.Visible || !barButton.Focusable)
        {
            throw new InvalidOperationException("PublicationStatusBarButton must be visible and focusable.");
        }

        if (!System.Windows.Input.KeyboardNavigation.GetIsTabStop(barButton))
        {
            throw new InvalidOperationException("PublicationStatusBarButton must be reachable by Tab.");
        }

        if (string.IsNullOrWhiteSpace(
                System.Windows.Automation.AutomationProperties.GetName(barButton)))
        {
            throw new InvalidOperationException("PublicationStatusBarButton must expose an action automation name.");
        }

        bool sawOnCard = CardSurfaceHasExactLabel(mw.NotesListBox, expectedLabel);
        if (expectOnCard && !sawOnCard)
        {
            throw new InvalidOperationException("Actionable first-level status must be visible on the card: " + expectedLabel);
        }

        if (!expectOnCard && sawOnCard)
        {
            throw new InvalidOperationException("Quiet first-level status must stay off the card surface: " + expectedLabel);
        }

        if (CardSurfaceHasExactLabel(mw.NotesListBox, PublicationStatusCopy.SavedOnThisPc) && expectedLabel != PublicationStatusCopy.SavedOnThisPc)
        {
            throw new InvalidOperationException("Quiet local first-level leaked onto a card surface.");
        }

        if (CardSurfaceHasExactLabel(mw.NotesListBox, PublicationStatusCopy.InCloud) && expectedLabel != PublicationStatusCopy.InCloud)
        {
            throw new InvalidOperationException("Quiet in-cloud first-level leaked onto a card surface.");
        }
    }

    private static bool CardSurfaceHasExactLabel(DependencyObject root, string label)
    {
        foreach (var block in FindVisualChildren<TextBlock>(root))
        {
            if (block.Visibility != Visibility.Visible)
            {
                continue;
            }

            if (block.Text == label)
            {
                return true;
            }
        }

        return false;
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
}
