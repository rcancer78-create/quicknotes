using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using QuickNotes.App.Data;
using QuickNotes.App.Models;
using QuickNotes.App.ViewModels;
using QuickNotes.App.Views;

namespace QuickNotes.App.Services;

public static class ManualAcceptancePrepRunner
{
    public const string SuccessMarker = "QN_MANUAL_ACCEPTANCE_PREP_SUCCESS";
    public const string ManifestFileName = "prep-manifest.json";
    public const string LogFileName = "prep.log";
    public const string InboxTitle = "Входящая для ручной приёмки";

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
                Console.Error.WriteLine($"QN_MANUAL_ACCEPTANCE_PREP_ERROR:{ex}");
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
        return Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "artifacts", "manual-acceptance", "prep"));
    }

    public static void Run(MainWindow window, string outputDir)
    {
        Directory.CreateDirectory(outputDir);
        if (window.DataContext is not MainViewModel vm)
        {
            throw new InvalidOperationException("MainWindow DataContext is not MainViewModel.");
        }

        SuppressBlockingFirstRun(window);
        Seed(vm);
        vm.ReloadAll();
        ThreePaneUiSmokeRunner.DoEvents();

        string profile = QuickNotesDbContext.GetDefaultProfileDirectory();
        var titles = vm.Notes.Select(n => n.DisplayTitle).Where(t => !string.IsNullOrWhiteSpace(t)).ToArray();
        var manifest = new
        {
            generatedUtc = DateTime.UtcNow.ToString("o"),
            profileDirectory = profile,
            liveProfileMustNotBeUsed = "%LOCALAPPDATA%\\QuickNotes",
            seededTitles = titles,
            expectedNotes = new[]
            {
                UxC08UiSmokeRunner.LongTitle,
                UxC08UiSmokeRunner.ConflictTitle,
                UxC12UiSmokeRunner.NoteTitle,
                UxC14UiSmokeRunner.NoteTitle,
                InboxTitle
            }
        };

        foreach (string expected in manifest.expectedNotes)
        {
            if (!titles.Contains(expected, StringComparer.Ordinal))
            {
                throw new InvalidOperationException("Prep did not seed expected note: " + expected);
            }
        }

        string json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(outputDir, ManifestFileName), json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var log = new StringBuilder();
        log.AppendLine("QuickNotes manual acceptance prep");
        log.AppendLine("generatedUtc=" + DateTime.UtcNow.ToString("o"));
        log.AppendLine("profile=" + profile);
        log.AppendLine("notes=" + titles.Length);
        foreach (string title in titles)
        {
            log.AppendLine("- " + title);
        }

        File.WriteAllText(Path.Combine(outputDir, LogFileName), log.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
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
        var now = DateTime.Now;

        if (!db.Notes.Any(n => n.Title == UxC08UiSmokeRunner.LongTitle))
        {
            db.Notes.Add(new Note
            {
                Title = UxC08UiSmokeRunner.LongTitle,
                Text = "Синтетическое тело без локального номера на карточке.",
                IsInbox = false,
                CreatedAt = now.AddHours(-26),
                UpdatedAt = now.AddMinutes(-4)
            });
        }

        if (!db.Notes.Any(n => n.Title == UxC08UiSmokeRunner.ConflictTitle))
        {
            db.Notes.Add(new Note
            {
                Title = UxC08UiSmokeRunner.ConflictTitle,
                Text = "Синтетический конфликт для индикатора карточки.",
                IsInbox = false,
                CreatedAt = now.AddHours(-8),
                UpdatedAt = now.AddMinutes(-2)
            });
        }

        if (!db.Notes.Any(n => n.Title == UxC14UiSmokeRunner.NoteTitle))
        {
            db.Notes.Add(new Note
            {
                Title = UxC14UiSmokeRunner.NoteTitle,
                Text = UxC14UiSmokeRunner.LongBody,
                IsInbox = false,
                CreatedAt = now.AddHours(-3),
                UpdatedAt = now.AddMinutes(-5)
            });
        }

        if (!db.Notes.Any(n => n.Title == InboxTitle))
        {
            db.Notes.Add(new Note
            {
                Title = InboxTitle,
                Text = "Очередь входящих для ручного Tab и Narrator.",
                IsInbox = true,
                CreatedAt = now.AddMinutes(-12),
                UpdatedAt = now.AddMinutes(-12)
            });
        }

        if (!db.Notes.Any(n => n.Title == UxC12UiSmokeRunner.NoteTitle))
        {
            var autoTag = db.Tags.FirstOrDefault(t => t.Name == UxC12UiSmokeRunner.AutoTagName)
                ?? db.Tags.Add(new Tag { Name = UxC12UiSmokeRunner.AutoTagName }).Entity;
            var manualTag = db.Tags.FirstOrDefault(t => t.Name == UxC12UiSmokeRunner.ManualTagName)
                ?? db.Tags.Add(new Tag { Name = UxC12UiSmokeRunner.ManualTagName }).Entity;
            var hiddenTag = db.Tags.FirstOrDefault(t => t.Name == UxC12UiSmokeRunner.HiddenTagName)
                ?? db.Tags.Add(new Tag { Name = UxC12UiSmokeRunner.HiddenTagName }).Entity;
            db.SaveChanges();

            var note = new Note
            {
                Title = UxC12UiSmokeRunner.NoteTitle,
                Text = "План на неделю: работа и встречи.",
                IsInbox = false,
                CreatedAt = now.AddHours(-2),
                UpdatedAt = now.AddMinutes(-8)
            };
            db.Notes.Add(note);
            db.SaveChanges();

            db.NoteTags.AddRange(
                new NoteTag { NoteId = note.Id, TagId = autoTag.Id, Origin = TagOrigin.Auto, IsSuppressed = false },
                new NoteTag { NoteId = note.Id, TagId = manualTag.Id, Origin = TagOrigin.Manual, IsSuppressed = false },
                new NoteTag { NoteId = note.Id, TagId = hiddenTag.Id, Origin = TagOrigin.Auto, IsSuppressed = true });
        }

        db.SaveChanges();
    }
}
