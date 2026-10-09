using System;
using System.IO;
using System.Linq;
using System.Windows;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using QuickNotes.App.Data;
using QuickNotes.App.Helpers;
using QuickNotes.App.Models;
using QuickNotes.App.Models.DraftJournal;
using QuickNotes.App.Services;
using QuickNotes.App.ViewModels;
using QuickNotes.App.Views;
using Xunit;

namespace QuickNotes.Tests;

[Collection(LiveProfileSnapshotCollection.Name)]
[TestCategory(TestCategories.UiSensitive)]
public sealed class UxPackageBTests
{
    [Fact]
    public void EmptyStates_DistinguishLibrarySectionAndSearch()
    {
        using var harness = new EmptyStateHarness();
        var vm = harness.ViewModel;

        Assert.Equal(NotesEmptyKind.EmptyLibrary, vm.NotesEmptyKind);
        Assert.Equal("Заметок пока нет", vm.EmptyStateTitle);
        Assert.False(vm.ShowEmptyStateResetFilter);

        harness.AddNote(new Note { Text = "alpha body", Title = "alpha", IsInbox = false });
        vm.ReloadAll();
        Assert.Equal(NotesEmptyKind.None, vm.NotesEmptyKind);

        var inbox = vm.VirtualSections.First(s => s.Section == NavigationSection.Inbox);
        vm.SelectSectionCommand.Execute(inbox);
        Assert.Equal(NotesEmptyKind.EmptySection, vm.NotesEmptyKind);
        Assert.Contains("Входящие", vm.EmptyStateTitle, StringComparison.Ordinal);
        Assert.True(vm.ShowEmptyStateResetFilter);

        vm.ResetFilter();
        vm.SearchQuery = "zzz-no-such-note";
        vm.RefreshNotes();
        Assert.Equal(NotesEmptyKind.NoMatches, vm.NotesEmptyKind);
        Assert.Equal("Ничего не найдено", vm.EmptyStateTitle);
        Assert.True(vm.ShowEmptyStateResetFilter);
        Assert.False(vm.ShowProtectedSearchHint);

        harness.AddNote(new Note { Text = "cipher", Title = "hidden", IsProtected = true, IsInbox = false });
        vm.ReloadAll();
        vm.SearchQuery = "cipher";
        vm.RefreshNotes();
        Assert.Equal(NotesEmptyKind.NoMatches, vm.NotesEmptyKind);
        Assert.True(vm.ShowProtectedSearchHint);
        Assert.DoesNotContain("hidden", vm.EmptyStateProtectedHint, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("cipher", vm.EmptyStateProtectedHint, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FirstRun_CloseWithoutStart_DoesNotCompleteOnboarding()
    {
        StaTestHarness.Run(() =>
        {
            using var harness = new EmptyStateHarness();
            var settings = harness.SettingsService.CurrentSettings;
            Assert.False(settings.HasCompletedOnboarding);

            var window = new FirstRunWindow(harness.ViewModel)
            {
                ShowInTaskbar = false,
                Left = -10000,
                Top = -10000,
                WindowStartupLocation = WindowStartupLocation.Manual
            };
            window.Show();
            window.Close();

            Assert.False(harness.SettingsService.CurrentSettings.HasCompletedOnboarding);
        }, TimeSpan.FromSeconds(20));
    }

    [Fact]
    public void FirstRunAndHelp_AreResizableAndMentionTray()
    {
        string root = FindSolutionRoot();
        string firstRun = File.ReadAllText(Path.Combine(root, "QuickNotes.App", "Views", "FirstRunWindow.xaml"));
        string help = File.ReadAllText(Path.Combine(root, "QuickNotes.App", "Views", "HelpWindow.xaml"));
        string main = File.ReadAllText(Path.Combine(root, "QuickNotes.App", "Views", "MainWindow.xaml"));

        Assert.Contains("ResizeMode=\"CanResize\"", firstRun);
        Assert.Contains("ScrollViewer", firstRun);
        Assert.DoesNotContain("ResizeMode=\"NoResize\"", firstRun);
        Assert.Contains("трей", firstRun, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("системный трей", help, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("VirtualSectionsList", main);
        Assert.Contains("из текста заметок", main);
        Assert.Contains("Новый тег", main);
        Assert.DoesNotContain("виртуальный индекс", main);
        Assert.DoesNotContain("+ Корень", main);
        Assert.Contains("AutomationProperties.Name=\"Новая заметка\"", main);
        Assert.Contains("ShowEmptyStateResetFilter", main);
        Assert.Contains("KeyboardNavigation.IsTabStop=\"False\"", main);
        Assert.DoesNotContain("NewNoteButton\" KeyboardNavigation.IsTabStop=\"False\"", main);
    }

    [Fact]
    public void CloseToTray_PromptRemembersHideThenReusesIt()
    {
        using var harness = new EmptyStateHarness();
        var vm = harness.ViewModel;
        int prompts = 0;
        vm.RequestCloseMainWindowDecision = () =>
        {
            prompts++;
            return CloseMainWindowDecision.Hide;
        };

        Assert.False(harness.SettingsService.CurrentSettings.CloseToTrayPromptCompleted);
        Assert.Equal(CloseMainWindowDecision.Hide, vm.DecideUserClose());
        Assert.Equal(1, prompts);
        Assert.True(harness.SettingsService.CurrentSettings.CloseToTrayPromptCompleted);
        Assert.True(harness.SettingsService.CurrentSettings.PreferCloseToTray);

        Assert.Equal(CloseMainWindowDecision.Hide, vm.DecideUserClose());
        Assert.Equal(1, prompts);
    }

    [Fact]
    public void CloseToTray_WithoutHandler_HidesAndDoesNotRemember()
    {
        using var harness = new EmptyStateHarness();
        var vm = harness.ViewModel;
        Assert.Null(vm.RequestCloseMainWindowDecision);
        Assert.Equal(CloseMainWindowDecision.Hide, vm.DecideUserClose());
        Assert.False(harness.SettingsService.CurrentSettings.CloseToTrayPromptCompleted);
    }

    [Fact]
    public void CloseToTrayDialog_ShowsHideExitReturn()
    {
        StaTestHarness.Run(() =>
        {
            var dialog = new CloseToTrayDialog
            {
                ShowInTaskbar = false,
                Left = -10000,
                Top = -10000,
                WindowStartupLocation = WindowStartupLocation.Manual
            };
            dialog.Show();
            Assert.Equal("Скрыть", dialog.HideButton.Content);
            Assert.Equal("Выйти", dialog.ExitButton.Content);
            Assert.Equal("Вернуться", dialog.CancelButton.Content);
            dialog.Close();
        }, TimeSpan.FromSeconds(15));
    }

    [Fact]
    public void NewSettingsFile_StartsCloseToTrayPrompt()
    {
        var missing = new AppSettings();
        Assert.True(missing.CloseToTrayPromptCompleted);
        Assert.True(missing.PreferCloseToTray);

        string path = Path.Combine(Path.GetTempPath(), $"qn-uxb-{Guid.NewGuid():N}.json");
        try
        {
            var service = new SettingsService(path, _ => { });
            Assert.False(service.CurrentSettings.HasCompletedOnboarding);
            Assert.False(service.CurrentSettings.CloseToTrayPromptCompleted);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public void DraftRecovery_ExistingNote_ExplainsDeferredJournalOutcome()
    {
        StaTestHarness.Run(() =>
        {
            var args = new DraftRecoveryPromptArgs
            {
                NoteId = 42,
                DraftId = "note-42",
                Source = "Блокнот",
                JournalTimestamp = new DateTime(2026, 9, 17, 10, 0, 0),
                CommittedTitle = "Сохранённый заголовок",
                CommittedText = "Сохранённый текст",
                DraftTitle = "Черновик",
                DraftText = "Текст черновика",
                DiffSummary = "Заголовок: изменён; Текст: изменён"
            };

            var window = new DraftRecoveryWindow(args)
            {
                ShowInTaskbar = false,
                Left = -10000,
                Top = -10000,
                WindowStartupLocation = WindowStartupLocation.Manual
            };

            try
            {
                window.Show();
                window.UpdateLayout();

                Assert.Equal("Отложить", window.KeepCommittedButton.Content);
                Assert.Equal("🗑️ Удалить черновик", window.DiscardDraftButton.Content);
                Assert.Equal("✓ Восстановить черновик", window.RestoreDraftButton.Content);

                string explanation = window.KeepCommittedExplanationText.Text;
                Assert.Contains("Сохранённая версия не изменится", explanation, StringComparison.Ordinal);
                Assert.Contains("Черновик останется на этом компьютере", explanation, StringComparison.Ordinal);
                Assert.Contains("будет предложен позже", explanation, StringComparison.Ordinal);

                Assert.DoesNotContain(
                    "Оставить сохранённую",
                    window.KeepCommittedButton.Content?.ToString() ?? string.Empty,
                    StringComparison.Ordinal);

                Assert.Equal(
                    "Отложить восстановление черновика",
                    System.Windows.Automation.AutomationProperties.GetName(window.KeepCommittedButton));

                Assert.Equal(DraftRecoveryChoice.KeepCommitted, window.Choice);
            }
            finally
            {
                window.Close();
            }
        }, TimeSpan.FromSeconds(20));
    }

    [Fact]
    public void DraftRecovery_NewNote_ExplainsJournalIsKeptForLater()
    {
        StaTestHarness.Run(() =>
        {
            var args = new DraftRecoveryPromptArgs
            {
                NoteId = null,
                DraftId = "new-1",
                Source = "Неизвестный источник",
                JournalTimestamp = new DateTime(2026, 9, 17, 10, 0, 0),
                DraftTitle = "Новая",
                DraftText = "Текст новой заметки",
                DiffSummary = "Несохранённый черновик новой заметки"
            };

            var window = new DraftRecoveryWindow(args)
            {
                ShowInTaskbar = false,
                Left = -10000,
                Top = -10000,
                WindowStartupLocation = WindowStartupLocation.Manual
            };

            try
            {
                window.Show();
                window.UpdateLayout();

                Assert.Equal("Отложить", window.KeepCommittedButton.Content);

                string explanation = window.KeepCommittedExplanationText.Text;
                Assert.Contains("Заметка не будет создана", explanation, StringComparison.Ordinal);
                Assert.Contains("будет предложен позже", explanation, StringComparison.Ordinal);
            }
            finally
            {
                window.Close();
            }
        }, TimeSpan.FromSeconds(20));
    }

    [Fact]
    public void DraftRecovery_FooterButtons_AreFullyVisible_At550x400_And750x550()
    {
        StaTestHarness.Run(() =>
        {
            var sizes = new[] { (550d, 400d), (750d, 550d) };
            foreach (var (width, height) in sizes)
            {
                var args = new DraftRecoveryPromptArgs
                {
                    NoteId = 42,
                    DraftId = "note-42",
                    Source = "Блокнот",
                    JournalTimestamp = new DateTime(2026, 9, 17, 10, 0, 0),
                    CommittedTitle = "Сохранённый заголовок",
                    CommittedText = "Сохранённый текст",
                    DraftTitle = "Черновик",
                    DraftText = "Текст черновика",
                    DiffSummary = "Заголовок: изменён; Текст: изменён"
                };

                var window = new DraftRecoveryWindow(args)
                {
                    ShowInTaskbar = false,
                    Left = -10000,
                    Top = -10000,
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Width = width,
                    Height = height,
                    MinWidth = width,
                    MinHeight = height,
                    MaxWidth = width,
                    MaxHeight = height
                };

                try
                {
                    window.Show();
                    window.Width = width;
                    window.Height = height;
                    window.UpdateLayout();

                    Assert.InRange(window.ActualWidth, width - 1, width + 1);
                    Assert.InRange(window.ActualHeight, height - 1, height + 1);

                    NoteAssemblyUiSmokeRunner.AssertVisibleInClient(window, window.DiscardDraftButton, "DiscardDraftButton");
                    NoteAssemblyUiSmokeRunner.AssertVisibleInClient(window, window.KeepCommittedButton, "KeepCommittedButton");
                    NoteAssemblyUiSmokeRunner.AssertVisibleInClient(window, window.RestoreDraftButton, "RestoreDraftButton");
                    Assert.True(window.KeepCommittedExplanationText.IsVisible);
                    Assert.True(window.KeepCommittedExplanationText.ActualHeight > 0);
                }
                finally
                {
                    window.Close();
                }
            }
        }, TimeSpan.FromSeconds(20));
    }

    [Fact]
    public void EditorRecoveryBanners_UseDeleteDraftLabel()
    {
        string root = FindSolutionRoot();
        string main = File.ReadAllText(Path.Combine(root, "QuickNotes.App", "Views", "MainWindow.xaml"));
        string editor = File.ReadAllText(Path.Combine(root, "QuickNotes.App", "Views", "NoteEditorWindow.xaml"));

        Assert.Contains("Content=\"Удалить черновик\"", main, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=\"Удалить черновик\"", main, StringComparison.Ordinal);
        Assert.DoesNotContain("Content=\"Отклонить\"", main, StringComparison.Ordinal);

        Assert.Contains("Content=\"Удалить черновик\"", editor, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=\"Удалить черновик\"", editor, StringComparison.Ordinal);
        Assert.DoesNotContain("Content=\"Отклонить\"", editor, StringComparison.Ordinal);
    }

    private static string FindSolutionRoot()
    {
        var current = AppDomain.CurrentDomain.BaseDirectory;
        while (!string.IsNullOrEmpty(current))
        {
            if (File.Exists(Path.Combine(current, "QuickNotes.sln")))
            {
                return current;
            }

            var parent = Directory.GetParent(current);
            if (parent == null)
            {
                break;
            }

            current = parent.FullName;
        }

        return @"D:\work\QuickNotes";
    }

    private sealed class EmptyStateHarness : IDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly DbContextOptions<QuickNotesDbContext> _options;
        private readonly string _settingsPath;
        private readonly GlobalHotkeyService _hotkey;
        private readonly TrayIconService _tray;
        private readonly SearchDebouncer _debouncer;

        public SettingsService SettingsService { get; }
        public MainViewModel ViewModel { get; }

        public EmptyStateHarness()
        {
            _connection = new SqliteConnection("Data Source=:memory:");
            _connection.Open();
            _options = new DbContextOptionsBuilder<QuickNotesDbContext>().UseSqlite(_connection).Options;
            using (var db = new QuickNotesDbContext(_options))
            {
                DbInitializer.Initialize(db);
            }

            _settingsPath = Path.Combine(Path.GetTempPath(), $"qn-uxb-{Guid.NewGuid():N}.json");
            SettingsService = new SettingsService(_settingsPath, _ => { });
            _hotkey = new GlobalHotkeyService((_, _, _, _) => true, (_, _) => true, IntPtr.Zero);
            _tray = new TrayIconService();
            _debouncer = new SearchDebouncer(0, a => a());
            ViewModel = MainViewModelTestComposition.Create(
                () => new QuickNotesDbContext(_options),
                new TagDetectionService(),
                new SearchService(),
                SettingsService,
                _hotkey,
                new ClipboardCaptureService(),
                _tray,
                new BackupService(),
                _debouncer,
                draftJournalService: NoOpDraftJournalService.Instance);
            ViewModel.RefreshNotes();
        }

        public void AddNote(Note note)
        {
            using var db = new QuickNotesDbContext(_options);
            db.Notes.Add(note);
            db.SaveChanges();
        }

        public void Dispose()
        {
            ViewModel.Dispose();
            _debouncer.Dispose();
            _tray.Dispose();
            _hotkey.Dispose();
            _connection.Dispose();
            if (File.Exists(_settingsPath))
            {
                File.Delete(_settingsPath);
            }
        }
    }
}
