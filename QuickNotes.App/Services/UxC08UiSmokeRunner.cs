using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using QuickNotes.App.Helpers;
using QuickNotes.App.Models;
using QuickNotes.App.Models.Sync;
using QuickNotes.App.ViewModels;
using QuickNotes.App.Views;
using TextBlock = System.Windows.Controls.TextBlock;

namespace QuickNotes.App.Services;

public static class UxC08UiSmokeRunner
{
    public const string LongTitle = "Длинный заголовок карточки остаётся первой сущностью";
    public const string ConflictTitle = "Конфликт облака UX-C08";
    public const string SuccessMarker = "QN_UX_C08_SMOKE_SUCCESS";

    public static readonly string[] ExpectedFileNames =
    {
        "note-cards-light-wide.png", "note-cards-dark-wide.png",
        "note-cards-light-narrow.png", "note-cards-dark-narrow.png"
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
                Console.Error.WriteLine($"QN_UX_C08_SMOKE_ERROR:{ex}");
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
        return Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "artifacts", "ux-c08-acceptance"));
    }

    public static void ValidateOutput(string path, string fileName)
    {
        var (width, height) = ExpectedSize(fileName);
        ThreePaneUiSmokeRunner.ValidateScreenshot(path, width, height, minBytes: 25_000, minDistinctColors: 30);
    }

    public static (int Width, int Height) ExpectedSize(string fileName)
    {
        bool narrow = fileName.Contains("narrow", StringComparison.Ordinal);
        return narrow ? (800, 650) : (1100, 720);
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
        SeedCards(vm);
        vm.ReloadAll();
        ApplyConflictStatus(vm);
        ThreePaneUiSmokeRunner.DoEvents();

        foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark })
        {
            foreach (var size in new[] { "wide", "narrow" })
            {
                ThemeService.ApplyTheme(theme);
                bool narrow = size == "narrow";
                vm.IsNarrow = narrow;
                vm.IsDetailActiveInNarrow = false;
                ApplyConflictStatus(vm);
                ThreePaneUiSmokeRunner.DoEvents();
                window.UpdateLayout();

                string fileName = $"note-cards-{(theme == AppTheme.Dark ? "dark" : "light")}-{size}.png";
                var (width, height) = ExpectedSize(fileName);
                string path = Path.Combine(outputDir, fileName);
                ThreePaneUiSmokeRunner.RenderAndSaveScreenshot(window, path, width, height, w =>
                {
                    ApplyConflictStatus(vm);
                    w.UpdateLayout();
                    AssertReadableCardSurface(w);
                    string visible = UxPackageAbUiSmokeRunner.CollectVisibleUiText(w);
                    if (string.IsNullOrWhiteSpace(visible) || !visible.Contains(LongTitle, StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException("Visible UI text must contain the long card title.");
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
        if (db.Notes.Any(n => n.Title == LongTitle || n.Title == ConflictTitle))
        {
            return;
        }

        var now = DateTime.Now;
        db.Notes.Add(new Note
        {
            Title = LongTitle,
            Text = "Синтетическое тело без локального номера на карточке.",
            IsInbox = false,
            CreatedAt = now.AddHours(-26),
            UpdatedAt = now.AddMinutes(-4)
        });
        db.Notes.Add(new Note
        {
            Title = ConflictTitle,
            Text = "Синтетический конфликт для индикатора карточки.",
            IsInbox = false,
            CreatedAt = now.AddHours(-8),
            UpdatedAt = now.AddMinutes(-2)
        });
        db.SaveChanges();
    }

    private static void ApplyConflictStatus(MainViewModel vm)
    {
        var conflict = vm.Notes.FirstOrDefault(n => n.DisplayTitle == ConflictTitle);
        if (conflict == null)
        {
            throw new InvalidOperationException("Conflict smoke note was not loaded.");
        }

        conflict.PublicationStatus = LocalCommitSyncStatus.Conflict;

        var quiet = vm.Notes.FirstOrDefault(n => n.DisplayTitle == LongTitle);
        if (quiet == null)
        {
            throw new InvalidOperationException("Long-title smoke note was not loaded.");
        }

        quiet.PublicationStatus = LocalCommitSyncStatus.SavedLocally;
    }

    private static void AssertReadableCardSurface(Window window)
    {
        if (window is not MainWindow mw)
        {
            throw new InvalidOperationException("Expected MainWindow.");
        }

        mw.NotesListBox.UpdateLayout();
        ThreePaneUiSmokeRunner.DoEvents();

        bool sawLongTitle = false;
        bool sawConflictTitle = false;
        bool sawConflictIcon = false;
        var localId = new Regex(@"^#\d+$", RegexOptions.CultureInvariant);

        foreach (var block in FindVisualChildren<TextBlock>(mw.NotesListBox))
        {
            if (block.Visibility != Visibility.Visible)
            {
                continue;
            }

            string text = ReadBlockText(block);
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            if (text == LongTitle || HighlightText.GetSource(block) == LongTitle)
            {
                sawLongTitle = true;
            }

            if (text == ConflictTitle || HighlightText.GetSource(block) == ConflictTitle)
            {
                sawConflictTitle = true;
            }

            if (text == PublicationStatusCopy.Conflict)
            {
                sawConflictIcon = true;
            }

            if (localId.IsMatch(text.Trim()))
            {
                throw new InvalidOperationException("Local technical ID is visible on the note card surface: " + text);
            }

            if (text.StartsWith("Создано:", StringComparison.Ordinal)
                || text.StartsWith("Изменено:", StringComparison.Ordinal)
                || text.StartsWith("Захвачено:", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Full secondary date prefix is visible on the card surface: " + text);
            }

            if (text.Contains("💾", StringComparison.Ordinal) || text.Contains("☁️", StringComparison.Ordinal)
                || text == PublicationStatusCopy.SavedOnThisPc
                || text == PublicationStatusCopy.InCloud)
            {
                throw new InvalidOperationException("Quiet cloud/local status must not occupy the card surface: " + text);
            }
        }

        if (!sawLongTitle)
        {
            throw new InvalidOperationException("Long note title was not found on the card surface.");
        }

        if (!sawConflictTitle)
        {
            throw new InvalidOperationException("Conflict note title was not found on the card surface.");
        }

        if (!sawConflictIcon)
        {
            throw new InvalidOperationException("Actionable conflict first-level status was not visible on the card.");
        }
    }

    private static string ReadBlockText(TextBlock block)
    {
        if (!string.IsNullOrEmpty(block.Text))
        {
            return block.Text;
        }

        return string.Concat(block.Inlines.OfType<Run>().Select(r => r.Text));
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
