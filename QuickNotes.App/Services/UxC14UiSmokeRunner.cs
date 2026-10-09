using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using QuickNotes.App.Helpers;
using QuickNotes.App.Models;
using QuickNotes.App.ViewModels;
using QuickNotes.App.Views;
using Button = System.Windows.Controls.Button;

namespace QuickNotes.App.Services;

public static class UxC14UiSmokeRunner
{
    public const string NoteTitle = "Карточка для разворота UX-C14";
    public const string LongBody =
        "Первый абзац длинного тела карточки, чтобы кнопка «Развернуть» имела смысл. " +
        "Второй абзац продолжает текст без открытия редактора двойным кликом. " +
        "Третий абзац проверяет, что полный текст появляется только после явной кнопки. " +
        "Четвёртый абзац нужен, чтобы свёрнутое превью обрезалось. " +
        "Пятый абзац остаётся скрытым, пока карточка не развёрнута.";
    public const string SuccessMarker = "QN_UX_C14_SMOKE_SUCCESS";

    public static readonly string[] ExpectedFileNames =
    {
        "cards-collapsed-light-wide.png", "cards-collapsed-dark-wide.png",
        "cards-collapsed-light-narrow.png", "cards-collapsed-dark-narrow.png",
        "cards-expanded-light-wide.png", "cards-expanded-dark-wide.png",
        "cards-expanded-light-narrow.png", "cards-expanded-dark-narrow.png"
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
                Console.Error.WriteLine($"QN_UX_C14_SMOKE_ERROR:{ex}");
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
        return Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "artifacts", "ux-c14-acceptance"));
    }

    public static void ValidateOutput(string path, string fileName)
    {
        var (width, height) = ExpectedSize(fileName);
        ThreePaneUiSmokeRunner.ValidateScreenshot(path, width, height, minBytes: 20_000, minDistinctColors: 25);
    }

    public static (int Width, int Height) ExpectedSize(string fileName)
    {
        return fileName.Contains("narrow", StringComparison.Ordinal) ? (800, 650) : (1100, 720);
    }

    public static string VisibleTextDumpPath(string pngPath) => Path.ChangeExtension(pngPath, ".visible.txt");

    public static void EnsureSeeded(MainViewModel vm)
    {
        Seed(vm);
        vm.ReloadAll();
        ThreePaneUiSmokeRunner.DoEvents();
    }

    public static void Run(MainWindow window, string outputDir)
    {
        Directory.CreateDirectory(outputDir);
        if (window.DataContext is not MainViewModel vm)
        {
            throw new InvalidOperationException("MainWindow DataContext is not MainViewModel.");
        }

        SuppressBlockingFirstRun(window);
        EnsureSeeded(vm);

        foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark })
        {
            foreach (var size in new[] { "wide", "narrow" })
            {
                ThemeService.ApplyTheme(theme);
                bool narrow = size == "narrow";
                vm.IsNarrow = narrow;
                vm.IsDetailActiveInNarrow = false;
                SetExpanded(vm, expanded: false);
                ThreePaneUiSmokeRunner.DoEvents();
                Capture(window, outputDir, $"cards-collapsed-{(theme == AppTheme.Dark ? "dark" : "light")}-{size}.png", expectExpanded: false);

                SetExpanded(vm, expanded: true);
                ThreePaneUiSmokeRunner.DoEvents();
                Capture(window, outputDir, $"cards-expanded-{(theme == AppTheme.Dark ? "dark" : "light")}-{size}.png", expectExpanded: true);
            }
        }

        ThemeService.ApplyTheme(AppTheme.Light);
    }

    private static void Capture(MainWindow window, string outputDir, string fileName, bool expectExpanded)
    {
        var (width, height) = ExpectedSize(fileName);
        string path = Path.Combine(outputDir, fileName);
        ThreePaneUiSmokeRunner.RenderAndSaveScreenshot(window, path, width, height, w =>
        {
            w.UpdateLayout();
            AssertExpandButton(w, expectExpanded);
            string visible = UxPackageAbUiSmokeRunner.CollectVisibleUiText(w);
            if (string.IsNullOrWhiteSpace(visible) || !visible.Contains(NoteTitle, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("C14 smoke must show the long card title.");
            }

            string expectedButton = expectExpanded ? CardGestureCopy.Collapse : CardGestureCopy.Expand;
            if (!visible.Contains(expectedButton, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("C14 smoke must show explicit expand/collapse button copy: " + expectedButton);
            }

            if (visible.Contains("Двойной клик", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("C14 smoke must not advertise double-click as a card command.");
            }

            if (expectExpanded && !visible.Contains("Пятый абзац", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Expanded card must reveal the full body text.");
            }

            File.WriteAllText(VisibleTextDumpPath(path), visible, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }, assertLayoutBounds: false);
        ValidateOutput(path, fileName);
    }

    private static void AssertExpandButton(Window window, bool expectExpanded)
    {
        if (window is not MainWindow mw)
        {
            throw new InvalidOperationException("Expected MainWindow.");
        }

        mw.NotesListBox.UpdateLayout();
        ThreePaneUiSmokeRunner.DoEvents();

        string expectedName = CardGestureCopy.ButtonAutomationName(expectExpanded);
        string expectedContent = CardGestureCopy.ButtonText(expectExpanded);
        var match = FindVisualChildren<Button>(mw.NotesListBox)
            .FirstOrDefault(b =>
                string.Equals(AutomationProperties.GetName(b), expectedName, StringComparison.Ordinal)
                || string.Equals(b.Content as string, expectedContent, StringComparison.Ordinal));
        if (match == null)
        {
            throw new InvalidOperationException("Explicit card expand button was not found: " + expectedContent);
        }
    }

    private static void SetExpanded(MainViewModel vm, bool expanded)
    {
        var card = vm.Notes.FirstOrDefault(n => n.DisplayTitle == NoteTitle)
            ?? throw new InvalidOperationException("C14 smoke note was not loaded.");
        card.IsExpanded = expanded;
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

    private static void Seed(MainViewModel vm)
    {
        using var db = vm.ContextFactory();
        if (db.Notes.Any(n => n.Title == NoteTitle))
        {
            return;
        }

        var now = DateTime.Now;
        db.Notes.Add(new Note
        {
            Title = NoteTitle,
            Text = LongBody,
            IsInbox = false,
            CreatedAt = now.AddHours(-3),
            UpdatedAt = now.AddMinutes(-5)
        });
        db.SaveChanges();
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
