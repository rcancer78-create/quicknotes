using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using QuickNotes.App.Data;
using QuickNotes.App.Helpers;
using QuickNotes.App.Models;
using QuickNotes.App.Models.Sync;
using QuickNotes.App.Services.Sync;
using QuickNotes.App.ViewModels;
using QuickNotes.App.Views;
using Button = System.Windows.Controls.Button;
using TextBlock = System.Windows.Controls.TextBlock;
using TextBox = System.Windows.Controls.TextBox;

namespace QuickNotes.App.Services;

public static class UxPackageAbUiSmokeRunner
{
    public const string SecretPlaintext = "UXAB_SECRET_MARKER_MUST_NOT_LEAK";
    public const string MergeDigitsBody = "Ручной merge без применения: цифры 1 2 3 4 остаются в редакторе.";
    public const string SuccessMarker = "QN_UX_PACKAGE_AB_SMOKE_SUCCESS";

    public static readonly string[] ExpectedFileNames =
    {
        "unsaved-editor-light-wide.png", "unsaved-editor-dark-wide.png",
        "unsaved-editor-light-narrow.png", "unsaved-editor-dark-narrow.png",
        "close-to-tray-light-wide.png", "close-to-tray-dark-wide.png",
        "close-to-tray-light-narrow.png", "close-to-tray-dark-narrow.png",
        "first-run-light-wide.png", "first-run-dark-wide.png",
        "first-run-light-narrow.png", "first-run-dark-narrow.png",
        "merge-digits-light-wide.png", "merge-digits-dark-wide.png",
        "merge-digits-light-narrow.png", "merge-digits-dark-narrow.png",
        "empty-library-light-wide.png", "empty-library-dark-wide.png",
        "empty-library-light-narrow.png", "empty-library-dark-narrow.png",
        "empty-section-light-wide.png", "empty-section-dark-wide.png",
        "empty-section-light-narrow.png", "empty-section-dark-narrow.png",
        "search-empty-light-wide.png", "search-empty-dark-wide.png",
        "search-empty-light-narrow.png", "search-empty-dark-narrow.png",
        "focus-ring-light-wide.png", "focus-ring-dark-wide.png",
        "focus-ring-light-narrow.png", "focus-ring-dark-narrow.png",
        "draft-discard-light-wide.png", "draft-discard-dark-wide.png",
        "draft-discard-light-narrow.png", "draft-discard-dark-narrow.png",
        "draft-recovery-existing-light-wide.png", "draft-recovery-existing-dark-wide.png",
        "draft-recovery-existing-light-narrow.png", "draft-recovery-existing-dark-narrow.png",
        "draft-recovery-new-light-wide.png", "draft-recovery-new-dark-wide.png",
        "draft-recovery-new-light-narrow.png", "draft-recovery-new-dark-narrow.png"
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
                Console.Error.WriteLine($"QN_UX_PACKAGE_AB_SMOKE_ERROR:{ex}");
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
        return Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "artifacts", "ux-package-ab-acceptance"));
    }

    public static void ValidateOutput(string path, string fileName)
    {
        var (width, height) = ExpectedSize(fileName);
        bool compactDialog = fileName.StartsWith("unsaved-editor-", StringComparison.Ordinal)
            || fileName.StartsWith("close-to-tray-", StringComparison.Ordinal)
            || fileName.StartsWith("first-run-", StringComparison.Ordinal)
            || fileName.StartsWith("draft-discard-", StringComparison.Ordinal)
            || fileName.StartsWith("draft-recovery-", StringComparison.Ordinal);
        ThreePaneUiSmokeRunner.ValidateScreenshot(
            path,
            width,
            height,
            minBytes: compactDialog ? 6_000 : 25_000,
            minDistinctColors: compactDialog ? 8 : 30);
    }

    public static (int Width, int Height) ExpectedSize(string fileName)
    {
        bool narrow = fileName.Contains("narrow", StringComparison.Ordinal);
        if (fileName.StartsWith("unsaved-editor-", StringComparison.Ordinal)
            || fileName.StartsWith("close-to-tray-", StringComparison.Ordinal)
            || fileName.StartsWith("first-run-", StringComparison.Ordinal)
            || fileName.StartsWith("draft-discard-", StringComparison.Ordinal)
            || fileName.StartsWith("draft-recovery-", StringComparison.Ordinal))
        {
            return narrow ? (640, 560) : (780, 720);
        }

        if (fileName.StartsWith("merge-digits-", StringComparison.Ordinal))
        {
            return narrow ? (640, 560) : (780, 720);
        }

        return narrow ? (800, 650) : (1100, 720);
    }

    public static void Run(MainWindow window, string outputDir)
    {
        Directory.CreateDirectory(outputDir);
        if (window.DataContext is not MainViewModel vm)
        {
            throw new InvalidOperationException("MainWindow DataContext is not MainViewModel.");
        }

        SuppressBlockingFirstRun(window);
        ThreePaneUiSmokeRunner.DoEvents();
        SeedHiddenDeletedMarkerNote(vm);

        CaptureDialogMatrix(outputDir, "unsaved-editor", () => new UnsavedEditorDialog(), AssertUnsavedEditor);
        CaptureDialogMatrix(outputDir, "close-to-tray", () => new CloseToTrayDialog(), AssertCloseToTray);
        CaptureDialogMatrix(outputDir, "first-run", () => new FirstRunWindow(vm), AssertFirstRun);
        CaptureDialogMatrix(outputDir, "draft-discard", () => new DraftDiscardDialog(), AssertDraftDiscard);
        CaptureDialogMatrix(outputDir, "draft-recovery-existing", () => new DraftRecoveryWindow(CreateExistingNoteRecoveryArgs()),
            window => AssertDraftRecovery(window, existingNote: true));
        CaptureDialogMatrix(outputDir, "draft-recovery-new", () => new DraftRecoveryWindow(CreateNewNoteRecoveryArgs()),
            window => AssertDraftRecovery(window, existingNote: false));

        vm.ReloadAll();
        ThreePaneUiSmokeRunner.DoEvents();
        CaptureMainMatrix(window, vm, outputDir, "empty-library", inner =>
        {
            inner.ResetFilter();
            inner.RefreshNotes();
            if (inner.NotesEmptyKind != NotesEmptyKind.EmptyLibrary)
            {
                throw new InvalidOperationException("Empty library scene expected NotesEmptyKind.EmptyLibrary.");
            }
        });

        SeedLibraryNote(vm);
        vm.ReloadAll();
        ThreePaneUiSmokeRunner.DoEvents();
        CaptureMainMatrix(window, vm, outputDir, "empty-section", inner =>
        {
            var inbox = inner.VirtualSections.First(s => s.Section == NavigationSection.Inbox);
            inner.SelectSectionCommand.Execute(inbox);
            inner.RefreshNotes();
            if (inner.NotesEmptyKind != NotesEmptyKind.EmptySection)
            {
                throw new InvalidOperationException("Empty section scene expected NotesEmptyKind.EmptySection.");
            }
        });

        CaptureMainMatrix(window, vm, outputDir, "search-empty", inner =>
        {
            inner.ResetFilter();
            inner.SearchQuery = "zzz-no-such-note";
            inner.RefreshNotes();
            if (inner.NotesEmptyKind != NotesEmptyKind.NoMatches || !inner.ShowEmptyStateResetFilter)
            {
                throw new InvalidOperationException("Search empty scene must show Reset Filter.");
            }
        }, afterLayout: w => AssertResetFilterKeyboardReachable(w));

        CaptureMainMatrix(window, vm, outputDir, "focus-ring", inner =>
        {
            inner.ResetFilter();
            inner.RefreshNotes();
        }, afterLayout: FocusPrimaryKeyboardTarget);

        CaptureMergeDigitsMatrix(outputDir);
        AssertVisibleTextDumpsHaveNoSecret(outputDir);
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

    private static void CaptureDialogMatrix(string outputDir, string scene, Func<Window> factory, Action<Window> assert)
    {
        foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark })
        {
            foreach (var size in new[] { "wide", "narrow" })
            {
                ThemeService.ApplyTheme(theme);
                var window = factory();
                window.ShowInTaskbar = false;
                window.Show();
                window.UpdateLayout();
                assert(window);

                string fileName = $"{scene}-{(theme == AppTheme.Dark ? "dark" : "light")}-{size}.png";
                var (width, height) = ExpectedSize(fileName);
                string path = Path.Combine(outputDir, fileName);
                CaptureGuardedScreenshot(window, path, width, height, _ => assert(window), assertLayoutBounds: false);
                ThreePaneUiSmokeRunner.ValidateScreenshot(path, width, height, minBytes: 8_000, minDistinctColors: 12);
                window.Close();
            }
        }
    }

    private static void CaptureMainMatrix(
        MainWindow window,
        MainViewModel vm,
        string outputDir,
        string scene,
        Action<MainViewModel> configure,
        Action<Window>? afterLayout = null)
    {
        foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark })
        {
            foreach (var size in new[] { "wide", "narrow" })
            {
                ThemeService.ApplyTheme(theme);
                bool narrow = size == "narrow";
                vm.IsNarrow = narrow;
                vm.IsDetailActiveInNarrow = false;
                configure(vm);
                ThreePaneUiSmokeRunner.DoEvents();
                window.UpdateLayout();

                string fileName = $"{scene}-{(theme == AppTheme.Dark ? "dark" : "light")}-{size}.png";
                var (width, height) = ExpectedSize(fileName);
                string path = Path.Combine(outputDir, fileName);
                CaptureGuardedScreenshot(window, path, width, height, w =>
                {
                    configure(vm);
                    w.UpdateLayout();
                    afterLayout?.Invoke(w);
                });
                ThreePaneUiSmokeRunner.ValidateScreenshot(path, width, height);
            }
        }
    }

    private static void CaptureMergeDigitsMatrix(string outputDir)
    {
        var device = new FixedDeviceIdProvider(Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"));
        SeedOpenConflict(device.GetDeviceId());

        var service = new SyncConflictService(() => new QuickNotesDbContext(), device, new NoteHistoryService(), new LocalMutationCoordinator());
        var vm = new SyncConflictsViewModel(service, loadOnStart: false);
        vm.LoadConflictsAsync().GetAwaiter().GetResult();
        if (vm.Conflicts.Count != 2)
        {
            throw new InvalidOperationException("Merge smoke expected a selected conflict and a hidden marker conflict.");
        }

        var visible = vm.Conflicts.Single(c => c.Reason.Contains("цифры 1-4", StringComparison.Ordinal));
        var hidden = vm.Conflicts.Single(c => c.Reason.Contains("Невыбранный конфликт UX AB", StringComparison.Ordinal));
        vm.SelectedConflict = visible;
        WaitForDetail(vm);
        AssertHiddenConflictMarkerIsSeededNotSelected(vm, visible, hidden);
        vm.UseLocalTitle = true;
        vm.UseRemoteTags = true;
        vm.MergedText = MergeDigitsBody;
        vm.StatusMessage = "Цифры 1-4 в редакторе объединения; конфликт не разрешён.";

        var window = new SyncConflictsWindow(vm);
        window.Show();
        window.UpdateLayout();

        foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark })
        {
            foreach (var size in new[] { "wide", "narrow" })
            {
                ThemeService.ApplyTheme(theme);
                string fileName = $"merge-digits-{(theme == AppTheme.Dark ? "dark" : "light")}-{size}.png";
                var (width, height) = ExpectedSize(fileName);
                string path = Path.Combine(outputDir, fileName);
                CaptureGuardedScreenshot(window, path, width, height, w => AssertMergeDigits(w, vm), assertLayoutBounds: false);
                ThreePaneUiSmokeRunner.ValidateScreenshot(path, width, height);
            }
        }

        if (vm.Conflicts.Count != 2)
        {
            throw new InvalidOperationException("Merge digits smoke resolved a conflict.");
        }

        window.Close();
    }

    private static void SeedLibraryNote(MainViewModel vm)
    {
        using var db = vm.ContextFactory();
        if (db.Notes.Any(n => n.DeletedAt == null))
        {
            return;
        }

        db.Notes.Add(new Note
        {
            Title = "Библиотечная заметка UX AB",
            Text = "Синтетическая заметка вне входящих.",
            IsInbox = false,
            CreatedAt = DateTime.Now.AddMinutes(-8),
            UpdatedAt = DateTime.Now.AddMinutes(-7)
        });
        db.SaveChanges();
    }

    private static void SeedHiddenDeletedMarkerNote(MainViewModel vm)
    {
        using var db = vm.ContextFactory();
        if (db.Notes.Any(n => n.Text == SecretPlaintext && n.DeletedAt != null))
        {
            return;
        }

        db.Notes.Add(new Note
        {
            Title = "Скрытая удалённая запись UX AB",
            Text = SecretPlaintext,
            IsInbox = false,
            DeletedAt = DateTime.Now.AddMinutes(-3),
            CreatedAt = DateTime.Now.AddMinutes(-9),
            UpdatedAt = DateTime.Now.AddMinutes(-3)
        });
        db.SaveChanges();

        if (!db.Notes.Any(n => n.DeletedAt != null && n.Text == SecretPlaintext))
        {
            throw new InvalidOperationException("Hidden deleted marker note was not persisted.");
        }

        if (db.Notes.Any(n => n.DeletedAt == null && n.Text != null && n.Text.Contains(SecretPlaintext)))
        {
            throw new InvalidOperationException("Secret marker must not be stored in a live library note.");
        }
    }

    private static void SeedOpenConflict(Guid localDeviceId)
    {
        using var db = new QuickNotesDbContext();
        DbInitializer.Initialize(db);

        var open = new Note
        {
            Title = "Локальный заголовок конфликта UX AB",
            Text = SyncConflictUiSmokeRunner.LocalBody,
            UpdatedAt = DateTime.Now.AddMinutes(-40)
        };
        var hidden = new Note
        {
            Title = "Невыбранный конфликт UX AB",
            Text = "Синтетическое тело, которое не должно быть на экране выбранного конфликта.",
            UpdatedAt = DateTime.Now.AddMinutes(-50)
        };
        db.Notes.AddRange(open, hidden);
        db.SaveChanges();

        var openState = new SyncEntityState
        {
            SyncId = open.SyncId,
            EntityType = "Note",
            RevisionId = Guid.NewGuid(),
            DeviceId = localDeviceId,
            UpdatedAtUtc = DateTime.UtcNow.AddMinutes(-40),
            ContentHash = "uxab-open"
        };
        var hiddenState = new SyncEntityState
        {
            SyncId = hidden.SyncId,
            EntityType = "Note",
            RevisionId = Guid.NewGuid(),
            DeviceId = localDeviceId,
            UpdatedAtUtc = DateTime.UtcNow.AddMinutes(-50),
            ContentHash = "uxab-hidden"
        };
        db.SyncEntityStates.AddRange(openState, hiddenState);
        db.SyncConflicts.Add(new SyncConflictRecord
        {
            SyncId = open.SyncId,
            EntityType = "Note",
            DetectedAtUtc = DateTime.UtcNow,
            SourceDeviceId = Guid.Parse("11111111-2222-3333-4444-555555555555"),
            Reason = "Параллельные правки; цифры 1-4 не должны разрешать конфликт",
            LocalRevisionId = openState.RevisionId,
            RemoteRevisionId = Guid.NewGuid(),
            LocalDataJson = JsonSerializer.Serialize(new SyncNoteDto
            {
                SyncId = open.SyncId,
                Title = open.Title,
                Text = open.Text,
                UpdatedAtUtc = DateTime.UtcNow.AddMinutes(-40),
                DeviceId = localDeviceId
            }),
            RemoteDataJson = JsonSerializer.Serialize(new SyncNoteDto
            {
                SyncId = open.SyncId,
                Title = "Облачный заголовок конфликта UX AB",
                Text = SyncConflictUiSmokeRunner.RemoteBody,
                UpdatedAtUtc = DateTime.UtcNow.AddMinutes(-10),
                DeviceId = Guid.Parse("11111111-2222-3333-4444-555555555555")
            })
        });
        db.SyncConflicts.Add(new SyncConflictRecord
        {
            SyncId = hidden.SyncId,
            EntityType = "Note",
            DetectedAtUtc = DateTime.UtcNow.AddMinutes(-5),
            SourceDeviceId = Guid.Parse("99999999-8888-7777-6666-555555555555"),
            Reason = "Невыбранный конфликт UX AB",
            LocalRevisionId = hiddenState.RevisionId,
            RemoteRevisionId = Guid.NewGuid(),
            LocalDataJson = JsonSerializer.Serialize(new SyncNoteDto
            {
                SyncId = hidden.SyncId,
                Title = hidden.Title,
                Text = hidden.Text,
                UpdatedAtUtc = DateTime.UtcNow.AddMinutes(-50),
                DeviceId = localDeviceId
            }),
            RemoteDataJson = JsonSerializer.Serialize(new SyncNoteDto
            {
                SyncId = hidden.SyncId,
                Title = "Невыбранное облако UX AB",
                Text = SecretPlaintext,
                UpdatedAtUtc = DateTime.UtcNow.AddMinutes(-12),
                DeviceId = Guid.Parse("99999999-8888-7777-6666-555555555555")
            })
        });
        db.SaveChanges();

        if (!db.SyncConflicts.Any(c => c.RemoteDataJson != null && c.RemoteDataJson.Contains(SecretPlaintext)))
        {
            throw new InvalidOperationException("Hidden conflict remote JSON must contain the secret marker.");
        }

        if (db.SyncConflicts.Count(c => !c.IsResolved) != 2)
        {
            throw new InvalidOperationException("Merge smoke must seed exactly two unresolved conflicts.");
        }
    }

    private static void AssertUnsavedEditor(Window window)
    {
        var prompt = Named<TextBlock>(window, "PromptText");
        var save = Named<Button>(window, "SaveButton");
        var discard = Named<Button>(window, "DiscardButton");
        var stay = Named<Button>(window, "StayButton");
        if (!string.Equals(save.Content?.ToString(), "Сохранить", StringComparison.Ordinal)
            || !string.Equals(discard.Content?.ToString(), "Не сохранять", StringComparison.Ordinal)
            || !string.Equals(stay.Content?.ToString(), "Вернуться", StringComparison.Ordinal)
            || prompt.Text != UnsavedEditorPromptText.Message)
        {
            throw new InvalidOperationException("Unsaved editor dialog must show Save / Discard / Stay wording.");
        }

        NoteAssemblyUiSmokeRunner.AssertVisibleInClient(window, save, "SaveButton");
        NoteAssemblyUiSmokeRunner.AssertVisibleInClient(window, discard, "DiscardButton");
        NoteAssemblyUiSmokeRunner.AssertVisibleInClient(window, stay, "StayButton");
    }

    private static void AssertDraftDiscard(Window window)
    {
        var discard = Named<Button>(window, "DiscardButton");
        var cancel = Named<Button>(window, "CancelButton");
        if (!string.Equals(discard.Content?.ToString(), "Удалить", StringComparison.Ordinal)
            || !string.Equals(cancel.Content?.ToString(), "Отмена", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Draft discard dialog must show Discard / Cancel.");
        }

        NoteAssemblyUiSmokeRunner.AssertVisibleInClient(window, discard, "DiscardButton");
        NoteAssemblyUiSmokeRunner.AssertVisibleInClient(window, cancel, "CancelButton");
    }

    private static QuickNotes.App.Models.DraftJournal.DraftRecoveryPromptArgs CreateExistingNoteRecoveryArgs()
    {
        return new QuickNotes.App.Models.DraftJournal.DraftRecoveryPromptArgs
        {
            NoteId = 42,
            DraftId = "note-42",
            JournalTimestamp = DateTime.Now,
            Source = "TestProcess",
            DraftTitle = "Новый черновик",
            DraftText = "Текст черновика",
            CommittedTitle = "Старый заголовок",
            CommittedText = "Старый текст",
            DiffSummary = "Заголовок: изменён; Текст: изменён"
        };
    }

    private static QuickNotes.App.Models.DraftJournal.DraftRecoveryPromptArgs CreateNewNoteRecoveryArgs()
    {
        return new QuickNotes.App.Models.DraftJournal.DraftRecoveryPromptArgs
        {
            NoteId = null,
            DraftId = "new-1",
            JournalTimestamp = DateTime.Now,
            Source = "TestProcess",
            DraftTitle = "Новый черновик",
            DraftText = "Текст черновика",
            DiffSummary = "Несохранённый черновик новой заметки"
        };
    }

    private static void AssertDraftRecovery(Window window, bool existingNote)
    {
        var restore = Named<Button>(window, "RestoreDraftButton");
        var discard = Named<Button>(window, "DiscardDraftButton");
        var keep = Named<Button>(window, "KeepCommittedButton");
        if (restore == null || discard == null || keep == null)
        {
            throw new InvalidOperationException("Draft recovery window is missing action buttons.");
        }

        if (!string.Equals(restore.Content?.ToString(), "✓ Восстановить черновик", StringComparison.Ordinal)
            || !string.Equals(discard.Content?.ToString(), "🗑️ Удалить черновик", StringComparison.Ordinal)
            || !string.Equals(keep.Content?.ToString(), "Отложить", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Draft recovery window must show Restore / Discard / Defer buttons.");
        }

        var explanation = Named<TextBlock>(window, "KeepCommittedExplanationText");
        string expectedFragment = existingNote
            ? "Сохранённая версия не изменится"
            : "Заметка не будет создана";
        if (string.IsNullOrWhiteSpace(explanation.Text)
            || !explanation.Text.Contains(expectedFragment, StringComparison.Ordinal)
            || !explanation.Text.Contains("Черновик останется на этом компьютере", StringComparison.Ordinal)
            || !explanation.Text.Contains("будет предложен позже", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Draft recovery window is missing the scenario-specific defer explanation.");
        }

        string visible = CollectVisibleUiText(window);
        AssertVisibleTextHasNoSecret(visible, existingNote ? "draft-recovery-existing" : "draft-recovery-new");

        NoteAssemblyUiSmokeRunner.AssertVisibleInClient(window, restore, "RestoreDraftButton");
        NoteAssemblyUiSmokeRunner.AssertVisibleInClient(window, discard, "DiscardDraftButton");
        NoteAssemblyUiSmokeRunner.AssertVisibleInClient(window, keep, "KeepCommittedButton");
    }

    private static void AssertCloseToTray(Window window)
    {
        var hide = Named<Button>(window, "HideButton");
        var exit = Named<Button>(window, "ExitButton");
        var cancel = Named<Button>(window, "CancelButton");
        if (!string.Equals(hide.Content?.ToString(), "Скрыть", StringComparison.Ordinal)
            || !string.Equals(exit.Content?.ToString(), "Выйти", StringComparison.Ordinal)
            || !string.Equals(cancel.Content?.ToString(), "Вернуться", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Close-to-tray dialog must show Hide / Exit / Return.");
        }

        NoteAssemblyUiSmokeRunner.AssertVisibleInClient(window, hide, "HideButton");
    }

    private static void AssertFirstRun(Window window)
    {
        if (window.ResizeMode != ResizeMode.CanResize)
        {
            throw new InvalidOperationException("First-run window must be resizable.");
        }

        var start = FindButtonByContent(window, "Начать работу")
            ?? throw new InvalidOperationException("First-run is missing Start action.");
        NoteAssemblyUiSmokeRunner.AssertVisibleInClient(window, start, "StartButton");
        string text = CollectText(window);
        if (!text.Contains("трей", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("First-run text must mention the tray.");
        }
    }

    private static void AssertMergeDigits(Window window, SyncConflictsViewModel vm)
    {
        var editor = Named<TextBox>(window, "MergeEditorBox");
        editor.BringIntoView();
        window.UpdateLayout();
        if (vm.Conflicts.Count != 2)
        {
            throw new InvalidOperationException("Merge digits scene must keep both the selected and hidden conflicts unresolved.");
        }

        if (vm.SelectedConflict == null
            || !vm.SelectedConflict.Reason.Contains("цифры 1-4", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Merge digits scene must keep the digit conflict selected.");
        }

        string text = editor.Text ?? string.Empty;
        if (!text.Contains('1') || !text.Contains('2') || !text.Contains('3') || !text.Contains('4'))
        {
            throw new InvalidOperationException("Merge editor must show digits 1-4.");
        }

        string selectedBound = string.Join("\n",
            vm.MergedText,
            vm.SelectedConflict.Detail?.LocalText,
            vm.SelectedConflict.Detail?.RemoteText,
            vm.SelectedConflict.Detail?.LocalTitle,
            vm.SelectedConflict.Detail?.RemoteTitle,
            Named<TextBox>(window, "LocalBodyBox").Text,
            Named<TextBox>(window, "RemoteBodyBox").Text);
        if (selectedBound.Contains(SecretPlaintext, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Selected conflict UI-bound text contains the secret marker.");
        }

        NoteAssemblyUiSmokeRunner.AssertVisibleInClient(window, editor, "MergeEditorBox");
    }

    private static void AssertResetFilterKeyboardReachable(Window window)
    {
        var reset = FindVisibleResetFilterButton(window)
            ?? throw new InvalidOperationException("Reset Filter button is not visible.");
        if (reset.IsTabStop == false)
        {
            throw new InvalidOperationException("Reset Filter is not keyboard-reachable.");
        }

        reset.Focus();
        Keyboard.Focus(reset);
        NoteAssemblyUiSmokeRunner.AssertVisibleInClient(window, reset, "ResetFilterButton");
    }

    private static void FocusPrimaryKeyboardTarget(Window window)
    {
        if (window is not MainWindow mw)
        {
            throw new InvalidOperationException("Focus ring scene requires MainWindow.");
        }

        mw.UpdateLayout();
        mw.VirtualSectionsList.Focus();
        if (mw.VirtualSectionsList.ItemContainerGenerator.ContainerFromIndex(0) is ListBoxItem item)
        {
            item.Focus();
            Keyboard.Focus(item);
            if (item.IsKeyboardFocused || mw.VirtualSectionsList.IsKeyboardFocusWithin)
            {
                NoteAssemblyUiSmokeRunner.AssertVisibleInClient(window, item, "VirtualSectionItem");
                return;
            }
        }

        mw.NewNoteButton.Focus();
        Keyboard.Focus(mw.NewNoteButton);
        if (!mw.NewNoteButton.IsKeyboardFocused && !mw.NewNoteButton.IsKeyboardFocusWithin)
        {
            throw new InvalidOperationException("Neither virtual section nor New Note received keyboard focus.");
        }

        NoteAssemblyUiSmokeRunner.AssertVisibleInClient(window, mw.NewNoteButton, "NewNoteButton");
    }

    private static void CaptureGuardedScreenshot(
        Window window,
        string path,
        int width,
        int height,
        Action<Window>? afterLayout,
        bool assertLayoutBounds = true)
    {
        ThreePaneUiSmokeRunner.RenderAndSaveScreenshot(window, path, width, height, w =>
        {
            afterLayout?.Invoke(w);
            w.UpdateLayout();
            AssertAndDumpVisibleText(w, path);
        }, assertLayoutBounds);
    }

    public static string CollectVisibleUiText(DependencyObject root)
    {
        var parts = new List<string>();
        if (root is Window window && !string.IsNullOrWhiteSpace(window.Title))
        {
            parts.Add(window.Title);
        }

        CollectVisibleUiTextCore(root, parts);
        return string.Join("\n", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
    }

    public static string VisibleTextDumpPath(string pngPath)
    {
        return Path.ChangeExtension(pngPath, ".visible.txt");
    }

    public static void AssertVisibleTextHasNoSecret(string visibleText, string sceneName)
    {
        if (visibleText.Contains(SecretPlaintext, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Secret marker is present in collected visible UI text: " + sceneName);
        }
    }

    private static void AssertAndDumpVisibleText(Window window, string pngPath)
    {
        string visible = CollectVisibleUiText(window);
        if (string.IsNullOrWhiteSpace(visible))
        {
            throw new InvalidOperationException("Collected visible UI text was empty for " + Path.GetFileName(pngPath));
        }

        AssertVisibleTextHasNoSecret(visible, Path.GetFileName(pngPath));
        File.WriteAllText(VisibleTextDumpPath(pngPath), visible, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static void AssertVisibleTextDumpsHaveNoSecret(string outputDir)
    {
        foreach (string name in ExpectedFileNames)
        {
            string dumpPath = VisibleTextDumpPath(Path.Combine(outputDir, name));
            if (!File.Exists(dumpPath))
            {
                throw new InvalidOperationException("Missing visible-text dump for " + name);
            }

            string visible = File.ReadAllText(dumpPath);
            if (string.IsNullOrWhiteSpace(visible))
            {
                throw new InvalidOperationException("Visible-text dump is empty for " + name);
            }

            AssertVisibleTextHasNoSecret(visible, name);
        }
    }

    private static void AssertHiddenConflictMarkerIsSeededNotSelected(
        SyncConflictsViewModel vm,
        SyncConflictItemViewModel visible,
        SyncConflictItemViewModel hidden)
    {
        if (!ReferenceEquals(vm.SelectedConflict, visible))
        {
            throw new InvalidOperationException("Digit merge conflict must be selected.");
        }

        if (ReferenceEquals(vm.SelectedConflict, hidden))
        {
            throw new InvalidOperationException("Hidden marker conflict must not be selected.");
        }

        if (hidden.Detail != null)
        {
            throw new InvalidOperationException("Hidden marker conflict detail must not be loaded into the UI.");
        }

        string selectedBound = string.Join("\n",
            visible.Title,
            visible.Reason,
            visible.Detail?.LocalText,
            visible.Detail?.RemoteText,
            visible.Detail?.LocalTitle,
            visible.Detail?.RemoteTitle);
        if (selectedBound.Contains(SecretPlaintext, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Selected conflict bound fields contain the secret marker.");
        }

        using var db = new QuickNotesDbContext();
        var hiddenRecord = db.SyncConflicts.First(c => c.Id == hidden.ConflictId);
        if (hiddenRecord.RemoteDataJson == null
            || !hiddenRecord.RemoteDataJson.Contains(SecretPlaintext, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Hidden conflict remote JSON must contain the secret marker.");
        }

        if (hiddenRecord.LocalDataJson != null
            && hiddenRecord.LocalDataJson.Contains(SecretPlaintext, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Hidden conflict local JSON must not contain the secret marker.");
        }
    }

    private static void CollectVisibleUiTextCore(DependencyObject parent, List<string> parts)
    {
        if (parent is UIElement element && element.Visibility != Visibility.Visible)
        {
            return;
        }

        switch (parent)
        {
            case TextBox textBox:
                parts.Add(textBox.Text ?? string.Empty);
                break;
            case TextBlock textBlock:
                parts.Add(textBlock.Text ?? string.Empty);
                break;
            case AccessText accessText:
                parts.Add(accessText.Text ?? string.Empty);
                break;
            case ContentControl contentControl:
                if (contentControl.Content is string contentText)
                {
                    parts.Add(contentText);
                }

                if (contentControl is HeaderedContentControl headered && headered.Header is string headerText)
                {
                    parts.Add(headerText);
                }

                break;
        }

        if (parent is FrameworkElement fe)
        {
            if (fe.ToolTip is string tip)
            {
                parts.Add(tip);
            }

            string automationName = AutomationProperties.GetName(fe);
            if (!string.IsNullOrWhiteSpace(automationName))
            {
                parts.Add(automationName);
            }

            string helpText = AutomationProperties.GetHelpText(fe);
            if (!string.IsNullOrWhiteSpace(helpText))
            {
                parts.Add(helpText);
            }
        }

        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            CollectVisibleUiTextCore(VisualTreeHelper.GetChild(parent, i), parts);
        }

        if (parent is ContentControl cc && cc.Content is DependencyObject contentObject)
        {
            CollectVisibleUiTextCore(contentObject, parts);
        }
    }

    private static Button? FindVisibleResetFilterButton(DependencyObject root)
    {
        foreach (var button in FindVisualChildren<Button>(root))
        {
            if (button.Visibility != Visibility.Visible || button.ActualHeight <= 0)
            {
                continue;
            }

            string content = button.Content?.ToString() ?? string.Empty;
            string name = AutomationProperties.GetName(button);
            if (content.Equals("Сбросить фильтр", StringComparison.Ordinal)
                || name.Equals("Сбросить фильтр", StringComparison.Ordinal))
            {
                return button;
            }
        }

        return null;
    }

    private static Button? FindButtonByContent(DependencyObject root, string content)
    {
        return FindVisualChildren<Button>(root)
            .FirstOrDefault(b => string.Equals(b.Content?.ToString(), content, StringComparison.Ordinal));
    }

    private static string CollectText(DependencyObject root)
    {
        return string.Join(" ", FindVisualChildren<TextBlock>(root).Select(t => t.Text));
    }

    private static T Named<T>(DependencyObject root, string name) where T : FrameworkElement
    {
        var found = FindNamed<T>(root, name);
        if (found == null)
        {
            throw new InvalidOperationException($"Missing element {name}.");
        }

        return found;
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
            var found = FindNamed<T>(VisualTreeHelper.GetChild(root, i), name);
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

    private static System.Collections.Generic.IEnumerable<T> FindVisualChildren<T>(DependencyObject parent)
        where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typed)
            {
                yield return typed;
            }

            foreach (var nested in FindVisualChildren<T>(child))
            {
                yield return nested;
            }
        }

        if (parent is ContentControl cc && cc.Content is DependencyObject content)
        {
            if (content is T typedContent)
            {
                yield return typedContent;
            }

            foreach (var nested in FindVisualChildren<T>(content))
            {
                yield return nested;
            }
        }
    }

    private static void WaitForDetail(SyncConflictsViewModel vm)
    {
        vm.PendingDetailLoad.GetAwaiter().GetResult();
        for (int i = 0; i < 80; i++)
        {
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            if (vm.SelectedConflict?.Detail != null || vm.SelectedConflict == null)
            {
                return;
            }

            System.Threading.Thread.Sleep(25);
        }

        throw new InvalidOperationException("Conflict detail did not load.");
    }
}
