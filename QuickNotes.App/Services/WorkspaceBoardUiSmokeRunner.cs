using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using QuickNotes.App.Data;
using QuickNotes.App.Helpers;
using QuickNotes.App.Models;
using QuickNotes.App.Models.Sync;
using QuickNotes.App.ViewModels;
using QuickNotes.App.Views;
using TextBlock = System.Windows.Controls.TextBlock;

namespace QuickNotes.App.Services;

public static class WorkspaceBoardUiSmokeRunner
{
    public const string LongTitle = "Длинный заголовок карточки на адаптивной доске";
    public const string ConflictTitle = "Конфликтная заметка на доске";
    public const string ProtectedTitle = "Защищённая заметка";
    public const string LeakSecretProtectedBodyMarker = "LEAK_SECRET_PROTECTED_BODY_MARKER";
    public const string SuccessMarker = "QN_WORKSPACE_BOARD_SMOKE_SUCCESS";

    public static readonly string[] ExpectedFileNames =
    {
        "workspace-board-light-wide.png", "workspace-board-dark-wide.png",
        "workspace-board-light-narrow.png", "workspace-board-dark-narrow.png"
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
                Console.Error.WriteLine($"QN_WORKSPACE_BOARD_SMOKE_ERROR:{ex}");
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
        return Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "artifacts", "workspace-board-acceptance"));
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

        // Switch to Board mode
        vm.SetBoardViewModeCommand.Execute(null);
        window.UpdateWorkspaceLayout();
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

                string fileName = $"workspace-board-{(theme == AppTheme.Dark ? "dark" : "light")}-{size}.png";
                var (width, height) = ExpectedSize(fileName);

                window.Width = width;
                window.Height = height;
                window.UpdateWorkspaceLayout();
                window.UpdateBoardLayout(width);
                ApplyConflictStatus(vm);
                ThreePaneUiSmokeRunner.DoEvents();
                window.UpdateLayout();

                string path = Path.Combine(outputDir, fileName);
                ThreePaneUiSmokeRunner.RenderAndSaveScreenshot(window, path, width, height, w =>
                {
                    ApplyConflictStatus(vm);
                    if (w is MainWindow mw)
                    {
                        mw.UpdateBoardLayout(width);
                        ThreePaneUiSmokeRunner.DoEvents();
                    }
                    w.UpdateLayout();
                    ThreePaneUiSmokeRunner.DoEvents();
                    if (w is MainWindow mwFinal)
                    {
                        mwFinal.UpdateBoardLayout();
                        w.UpdateLayout();
                        ThreePaneUiSmokeRunner.DoEvents();
                    }
                    AssertReadableBoardSurface(w);
                    string visible = UxPackageAbUiSmokeRunner.CollectVisibleUiText(w);
                    if (string.IsNullOrWhiteSpace(visible) || !visible.Contains(LongTitle, StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException("Visible UI text must contain the long card title.");
                    }

                    if (visible.Contains(LeakSecretProtectedBodyMarker, StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException("LEAK DETECTED: Protected body marker appeared in visible UI text!");
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
            Text = "Синтетическое тело заметки на адаптивной доске без локального номера.",
            IsInbox = false,
            CreatedAt = now.AddHours(-26),
            UpdatedAt = now.AddMinutes(-4)
        });
        db.Notes.Add(new Note
        {
            Title = ConflictTitle,
            Text = "Синтетический конфликт для проверки индикатора на доске.",
            IsInbox = false,
            CreatedAt = now.AddHours(-8),
            UpdatedAt = now.AddMinutes(-2)
        });
        db.Notes.Add(new Note
        {
            Title = ProtectedTitle,
            Text = LeakSecretProtectedBodyMarker,
            IsProtected = true,
            IsInbox = false,
            CreatedAt = now.AddHours(-4),
            UpdatedAt = now.AddMinutes(-1)
        });
        db.Notes.Add(new Note
        {
            Title = "Обычная заметка с тегом",
            Text = "Текст заметки для проверки адаптивной сетки доски.",
            IsInbox = true,
            CreatedAt = now.AddHours(-2),
            UpdatedAt = now.AddMinutes(-10)
        });
        db.SaveChanges();
    }

    private static void ApplyConflictStatus(MainViewModel vm)
    {
        var conflict = vm.Notes.FirstOrDefault(n => n.DisplayTitle == ConflictTitle);
        if (conflict != null)
        {
            conflict.PublicationStatus = LocalCommitSyncStatus.Conflict;
        }

        var quiet = vm.Notes.FirstOrDefault(n => n.DisplayTitle == LongTitle);
        if (quiet != null)
        {
            quiet.PublicationStatus = LocalCommitSyncStatus.SavedLocally;
        }
    }

    private static void AssertReadableBoardSurface(Window window)
    {
        if (window is not MainWindow mw)
        {
            throw new InvalidOperationException("Expected MainWindow.");
        }

        if (mw.BoardHost.Visibility != Visibility.Visible)
        {
            throw new InvalidOperationException("BoardHost must be visible in board mode.");
        }

        mw.BoardListBox.UpdateLayout();
        ThreePaneUiSmokeRunner.DoEvents();

        bool sawLongTitle = false;
        bool sawConflictTitle = false;
        bool sawConflictIcon = false;
        var localId = new Regex(@"^#\d+$", RegexOptions.CultureInvariant);

        foreach (var block in FindVisualChildren<TextBlock>(mw.BoardListBox))
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

            if (text.Contains(LeakSecretProtectedBodyMarker, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("LEAK DETECTED: Protected body marker visible on board card!");
            }

            string autoName = AutomationProperties.GetName(block);
            if (!string.IsNullOrEmpty(autoName) && autoName.Contains(LeakSecretProtectedBodyMarker, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("LEAK DETECTED: Protected body marker in AutomationProperties.Name!");
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
                throw new InvalidOperationException("Local technical ID is visible on the board card surface: " + text);
            }

            if (text.StartsWith("Создано:", StringComparison.Ordinal)
                || text.StartsWith("Изменено:", StringComparison.Ordinal)
                || text.StartsWith("Захвачено:", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Full secondary date prefix is visible on the board card surface: " + text);
            }

            if (text.Contains("💾", StringComparison.Ordinal) || text.Contains("☁️", StringComparison.Ordinal)
                || text == PublicationStatusCopy.SavedOnThisPc
                || text == PublicationStatusCopy.InCloud)
            {
                throw new InvalidOperationException("Quiet cloud/local status must not occupy the board card surface: " + text);
            }
        }

        if (!sawLongTitle)
        {
            throw new InvalidOperationException("Long note title was not found on the board card surface.");
        }

        if (!sawConflictTitle)
        {
            throw new InvalidOperationException("Conflict note title was not found on the board card surface.");
        }

        if (!sawConflictIcon)
        {
            throw new InvalidOperationException("Actionable conflict status was not visible on the board card.");
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
