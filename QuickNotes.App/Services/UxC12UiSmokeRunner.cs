using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.EntityFrameworkCore;
using QuickNotes.App.Helpers;
using QuickNotes.App.Models;
using QuickNotes.App.Services.DraftJournal;
using QuickNotes.App.ViewModels;
using QuickNotes.App.Views;

namespace QuickNotes.App.Services;

public static class UxC12UiSmokeRunner
{
    public const string NoteTitle = "Заметка для C12";
    public const string AutoTagName = "Работа";
    public const string ManualTagName = "Важно";
    public const string HiddenTagName = "Черновик";
    public const string SuccessMarker = "QN_UX_C12_SMOKE_SUCCESS";

    public static readonly string[] ExpectedFileNames =
    {
        "editor-tags-light-wide.png", "editor-tags-dark-wide.png",
        "editor-tags-light-narrow.png", "editor-tags-dark-narrow.png",
        "tag-rules-light-wide.png", "tag-rules-dark-wide.png"
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
                Console.Error.WriteLine($"QN_UX_C12_SMOKE_ERROR:{ex}");
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
        return Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "artifacts", "ux-c12-acceptance"));
    }

    public static void ValidateOutput(string path, string fileName)
    {
        var (width, height) = ExpectedSize(fileName);
        ThreePaneUiSmokeRunner.ValidateScreenshot(path, width, height, minBytes: 20_000, minDistinctColors: 25);
    }

    public static (int Width, int Height) ExpectedSize(string fileName)
    {
        if (fileName.StartsWith("tag-rules", StringComparison.Ordinal))
        {
            return (720, 540);
        }

        return fileName.Contains("narrow", StringComparison.Ordinal)
            ? (640, 720)
            : (900, 720);
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
        Seed(vm);
        CaptureEditor(vm, outputDir);
        CaptureTagRules(outputDir);
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

        var autoTag = new Tag { Name = AutoTagName };
        var manualTag = new Tag { Name = ManualTagName };
        var hiddenTag = new Tag { Name = HiddenTagName };
        db.Tags.AddRange(autoTag, manualTag, hiddenTag);
        db.SaveChanges();

        var note = new Note
        {
            Title = NoteTitle,
            Text = "План на неделю: работа и встречи.",
            IsInbox = false,
            CreatedAt = DateTime.Now.AddHours(-2),
            UpdatedAt = DateTime.Now.AddMinutes(-8)
        };
        db.Notes.Add(note);
        db.SaveChanges();

        db.NoteTags.AddRange(
            new NoteTag { NoteId = note.Id, TagId = autoTag.Id, Origin = TagOrigin.Auto, IsSuppressed = false },
            new NoteTag { NoteId = note.Id, TagId = manualTag.Id, Origin = TagOrigin.Manual, IsSuppressed = false },
            new NoteTag { NoteId = note.Id, TagId = hiddenTag.Id, Origin = TagOrigin.Auto, IsSuppressed = true });
        db.SaveChanges();
    }

    private static void CaptureEditor(MainViewModel vm, string outputDir)
    {
        using var db = vm.ContextFactory();
        var note = db.Notes
            .Include(n => n.NoteTags)
            .ThenInclude(nt => nt.Tag)
            .First(n => n.Title == NoteTitle);
        var tags = db.Tags.ToList();

        using var editorVm = new NoteEditorViewModel(
            new TagDetectionService(),
            tags,
            existingNote: note,
            contextFactory: vm.ContextFactory,
            settingsService: vm.SettingsService,
            draftJournalService: NoOpDraftJournalService.Instance);

        if (!editorVm.HasSuppressedTags
            || editorVm.ActiveTags.All(t => t.OriginMarker != TagOriginCopy.AutoMarker)
            || editorVm.ActiveTags.All(t => t.OriginMarker != TagOriginCopy.ManualMarker))
        {
            throw new InvalidOperationException("Editor smoke note must have auto, manual and hidden tags.");
        }

        var editor = new NoteEditorWindow(editorVm)
        {
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual
        };
        try
        {
            editor.Show();
            ThreePaneUiSmokeRunner.DoEvents();
            foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark })
            {
                foreach (var size in new[] { "wide", "narrow" })
                {
                    ThemeService.ApplyTheme(theme);
                    ThreePaneUiSmokeRunner.DoEvents();
                    string fileName = $"editor-tags-{(theme == AppTheme.Dark ? "dark" : "light")}-{size}.png";
                    var (width, height) = ExpectedSize(fileName);
                    string path = Path.Combine(outputDir, fileName);
                    ThreePaneUiSmokeRunner.RenderAndSaveScreenshot(editor, path, width, height, w =>
                    {
                        if (w is NoteEditorWindow ew)
                        {
                            ew.EditorTagsArea.BringIntoView();
                        }

                        w.UpdateLayout();
                        string visible = UxPackageAbUiSmokeRunner.CollectVisibleUiText(w);
                        AssertEditorVisible(visible);
                        File.WriteAllText(VisibleTextDumpPath(path), visible, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                    }, assertLayoutBounds: false);
                    ValidateOutput(path, fileName);
                }
            }
        }
        finally
        {
            editor.Close();
            ThreePaneUiSmokeRunner.DoEvents();
        }
    }

    private static void CaptureTagRules(string outputDir)
    {
        var tag = new Tag { Id = 7, Name = AutoTagName };
        var ruleVm = new TagRuleViewModel(tag);
        var dialog = new TagRuleDialog(ruleVm)
        {
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual
        };
        try
        {
            dialog.Show();
            ThreePaneUiSmokeRunner.DoEvents();
            foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark })
            {
                ThemeService.ApplyTheme(theme);
                ThreePaneUiSmokeRunner.DoEvents();
                string fileName = $"tag-rules-{(theme == AppTheme.Dark ? "dark" : "light")}-wide.png";
                var (width, height) = ExpectedSize(fileName);
                string path = Path.Combine(outputDir, fileName);
                ThreePaneUiSmokeRunner.RenderAndSaveScreenshot(dialog, path, width, height, w =>
                {
                    w.UpdateLayout();
                    string visible = UxPackageAbUiSmokeRunner.CollectVisibleUiText(w);
                    AssertRuleVisible(visible);
                    File.WriteAllText(VisibleTextDumpPath(path), visible, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                }, assertLayoutBounds: false);
                ValidateOutput(path, fileName);
            }
        }
        finally
        {
            dialog.Close();
            ThreePaneUiSmokeRunner.DoEvents();
        }
    }

    private static void AssertEditorVisible(string visible)
    {
        if (string.IsNullOrWhiteSpace(visible)
            || !visible.Contains(AutoTagName, StringComparison.Ordinal)
            || !visible.Contains(ManualTagName, StringComparison.Ordinal)
            || !visible.Contains(HiddenTagName, StringComparison.Ordinal)
            || !visible.Contains(TagOriginCopy.AutoMarker, StringComparison.Ordinal)
            || !visible.Contains(TagOriginCopy.ManualMarker, StringComparison.Ordinal)
            || !visible.Contains(TagOriginCopy.SuppressedSectionTitle, StringComparison.Ordinal)
            || !visible.Contains(TagOriginCopy.RestoreAction, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Editor tags smoke must show origin markers and restore of a hidden autotag.");
        }

        AssertNoEnglishLogicTokens(visible);
    }

    private static void AssertRuleVisible(string visible)
    {
        if (string.IsNullOrWhiteSpace(visible)
            || !visible.Contains(TagOriginCopy.RequiredAllLabel, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Tag rule dialog must show Russian required-all copy.");
        }

        AssertNoEnglishLogicTokens(visible);
    }

    public static void AssertNoEnglishLogicTokens(string text)
    {
        string[] forbidden = { "(All)", "(Any)", "(Excluded)", "(Auto)", "(Manual)", "(Suppressed)" };
        foreach (string token in forbidden)
        {
            if (text.Contains(token, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("English logic token leaked into UI: " + token);
            }
        }
    }
}
