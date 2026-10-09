using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using Microsoft.EntityFrameworkCore;
using QuickNotes.App.Data;
using QuickNotes.App.Helpers;
using QuickNotes.App.Models;
using QuickNotes.App.Models.DraftJournal;
using QuickNotes.App.Services;
using QuickNotes.App.Services.DraftJournal;
using QuickNotes.App.ViewModels;
using QuickNotes.App.Views;
using Xunit;

namespace QuickNotes.Tests;

/// <summary>
/// W3 focus mode acceptance: Enter opens the selected note in the existing inline
/// editor, Escape leaves through the shared unsaved/journal/commit contract, and
/// Focus never introduces a silent draft loss, double commit, extra revision, or
/// extra Sync enqueue.
/// </summary>
[Collection(LiveProfileSnapshotCollection.Name)]
[TestCategory(TestCategories.UiSensitive)]
public sealed class WorkspaceFocusTests
{
    private static void RunInSta(Action action) => StaTestHarness.Run(action, TimeSpan.FromSeconds(60));

    private static string[] SnapshotLiveProfile(string? liveRoot = null)
    {
        liveRoot ??= QuickNotesDbContext.LiveProfileDirectory;
        if (!Directory.Exists(liveRoot))
        {
            return Array.Empty<string>();
        }

        return Directory.GetFileSystemEntries(liveRoot, "*", SearchOption.AllDirectories)
            .Select(p => p + "|" + (File.Exists(p) ? new FileInfo(p).Length + "|" + File.GetLastWriteTimeUtc(p).Ticks : "dir"))
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string CreateTempProfile(string prefix)
    {
        string dir = Path.Combine(Path.GetTempPath(), prefix + "_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void RaiseKey(MainWindow window, Key key)
    {
        var source = PresentationSource.FromVisual(window);
        Assert.NotNull(source);
        var args = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key)
        {
            RoutedEvent = Keyboard.PreviewKeyDownEvent
        };
        window.RaiseEvent(args);
        ThreePaneUiSmokeRunner.DoEvents();
    }

    private static void PrepareWideWindow(MainWindow window)
    {
        window.Width = 1100;
        window.Height = 720;
        window.Measure(new Size(1100, 720));
        window.Arrange(new Rect(0, 0, 1100, 720));
        window.UpdateLayout();
        window.Show();
        ThreePaneUiSmokeRunner.DoEvents();
    }

    [Fact]
    public void WorkspaceFocus_EnterOnBoard_OpensFocusForSelectedNote()
    {
        string liveRoot = QuickNotesDbContext.LiveProfileDirectory;
        string[] liveBefore = SnapshotLiveProfile(liveRoot);
        string tempProfile = CreateTempProfile("qn_focus_board");
        try
        {
            RunInSta(() =>
            {
                ThemeService.ApplyThemeColors(isDark: false);
                using var fixture = new TestProfileComposition(tempProfile, seedNotes: 4, ownsDirectory: false);
                var (window, vm) = fixture;
                PrepareWideWindow(window);
                try
                {
                    vm.SetBoardViewModeCommand.Execute(null);
                    window.UpdateWorkspaceLayout();
                    window.UpdateLayout();

                    var target = vm.Notes[2];
                    vm.SelectedNote = target;
                    window.BoardListBox.Focus();
                    ThreePaneUiSmokeRunner.DoEvents();

                    Assert.Equal(WorkspaceViewMode.Board, vm.WorkspaceViewMode);
                    RaiseKey(window, Key.Enter);

                    Assert.Equal(WorkspaceViewMode.Focus, vm.WorkspaceViewMode);
                    Assert.True(vm.IsFocusMode);
                    Assert.Equal(target.Id, vm.DetailEditor?.NoteId);
                    window.UpdateWorkspaceLayout();
                    Assert.Equal(Visibility.Visible, window.DetailPaneBorder.Visibility);
                    Assert.Equal(Visibility.Collapsed, window.BoardHost.Visibility);
                    Assert.Equal(target.Id, window.BoardListBox.SelectedItem is NoteCardViewModel board ? board.Id : -1);
                }
                finally
                {
                    window.CloseWithoutShutdown();
                }
            });

            Assert.Equal(liveBefore, SnapshotLiveProfile(liveRoot));
        }
        finally
        {
            SqliteTestUtil.TryDeleteDirectory(tempProfile);
        }
    }

    [Fact]
    public void WorkspaceFocus_EnterOnList_OpensFocusForSelectedNote()
    {
        string liveRoot = QuickNotesDbContext.LiveProfileDirectory;
        string[] liveBefore = SnapshotLiveProfile(liveRoot);
        string tempProfile = CreateTempProfile("qn_focus_list");
        try
        {
            RunInSta(() =>
            {
                ThemeService.ApplyThemeColors(isDark: false);
                using var fixture = new TestProfileComposition(tempProfile, seedNotes: 4, ownsDirectory: false);
                var (window, vm) = fixture;
                PrepareWideWindow(window);
                try
                {
                    Assert.Equal(WorkspaceViewMode.List, vm.WorkspaceViewMode);
                    var target = vm.Notes[3];
                    vm.SelectedNote = target;
                    window.NotesListBox.Focus();
                    ThreePaneUiSmokeRunner.DoEvents();

                    RaiseKey(window, Key.Enter);

                    Assert.Equal(WorkspaceViewMode.Focus, vm.WorkspaceViewMode);
                    Assert.Equal(target.Id, vm.DetailEditor?.NoteId);
                    window.UpdateWorkspaceLayout();
                    Assert.Equal(Visibility.Visible, window.DetailPaneBorder.Visibility);
                    Assert.Equal(Visibility.Collapsed, window.NotesListGrid.Visibility);
                }
                finally
                {
                    window.CloseWithoutShutdown();
                }
            });

            Assert.Equal(liveBefore, SnapshotLiveProfile(liveRoot));
        }
        finally
        {
            SqliteTestUtil.TryDeleteDirectory(tempProfile);
        }
    }

    [Fact]
    public void WorkspaceFocus_EscFromCleanFocus_ReturnsToPreviousModeAndPreservesSelection()
    {
        string liveRoot = QuickNotesDbContext.LiveProfileDirectory;
        string[] liveBefore = SnapshotLiveProfile(liveRoot);
        string tempProfile = CreateTempProfile("qn_focus_esc");
        try
        {
            RunInSta(() =>
            {
                ThemeService.ApplyThemeColors(isDark: false);
                using var fixture = new TestProfileComposition(tempProfile, seedNotes: 4, ownsDirectory: false);
                var (window, vm) = fixture;
                PrepareWideWindow(window);
                try
                {
                    // Board -> Focus -> Board
                    vm.SetBoardViewModeCommand.Execute(null);
                    window.UpdateWorkspaceLayout();
                    window.UpdateLayout();
                    var boardTarget = vm.Notes[1];
                    vm.SelectedNote = boardTarget;
                    window.BoardListBox.Focus();
                    ThreePaneUiSmokeRunner.DoEvents();
                    RaiseKey(window, Key.Enter);
                    Assert.True(vm.IsFocusMode);

                    RaiseKey(window, Key.Escape);
                    window.UpdateWorkspaceLayout();
                    Assert.Equal(WorkspaceViewMode.Board, vm.WorkspaceViewMode);
                    Assert.Equal(boardTarget.Id, vm.SelectedNote?.Id);
                    Assert.Equal(Visibility.Visible, window.BoardHost.Visibility);

                    // List -> Focus -> List
                    vm.SetListViewModeCommand.Execute(null);
                    window.UpdateWorkspaceLayout();
                    window.UpdateLayout();
                    var listTarget = vm.Notes[2];
                    vm.SelectedNote = listTarget;
                    window.NotesListBox.Focus();
                    ThreePaneUiSmokeRunner.DoEvents();
                    RaiseKey(window, Key.Enter);
                    Assert.True(vm.IsFocusMode);

                    RaiseKey(window, Key.Escape);
                    window.UpdateWorkspaceLayout();
                    Assert.Equal(WorkspaceViewMode.List, vm.WorkspaceViewMode);
                    Assert.Equal(listTarget.Id, vm.SelectedNote?.Id);
                    Assert.Equal(Visibility.Visible, window.NotesListGrid.Visibility);
                }
                finally
                {
                    window.CloseWithoutShutdown();
                }
            });

            Assert.Equal(liveBefore, SnapshotLiveProfile(liveRoot));
        }
        finally
        {
            SqliteTestUtil.TryDeleteDirectory(tempProfile);
        }
    }

    [Fact]
    public void WorkspaceFocus_DirtyStay_RemainsInFocus_WithoutRevisionOrSync()
    {
        string liveRoot = QuickNotesDbContext.LiveProfileDirectory;
        string[] liveBefore = SnapshotLiveProfile(liveRoot);
        string tempProfile = CreateTempProfile("qn_focus_stay");
        try
        {
            RunInSta(() =>
            {
                ThemeService.ApplyThemeColors(isDark: false);
                var scheduler = new FakeTrackingSyncScheduler();
                using var fixture = new TestProfileComposition(tempProfile, seedNotes: 3, ownsDirectory: false);
                var vm = fixture.CreateMainViewModel(syncScheduler: scheduler);

                vm.RequestUnsavedEditorDecision = () => UnsavedEditorDecision.Stay;
                vm.RequestDraftDiscardDecision = () => true;

                var card = vm.Notes[0];
                vm.SelectedNote = card;
                Assert.True(vm.EnterFocus(card));
                var editor = vm.DetailEditor!;
                editor.Text = "Черновик в фокусе " + Guid.NewGuid().ToString("N");
                Assert.True(editor.HasUnsavedChanges);

                int revisionsBefore;
                using (var db = vm.ContextFactory())
                {
                    revisionsBefore = db.NoteRevisions.Count();
                }
                scheduler.EnqueueLocalChangeCallCount = 0;

                bool left = vm.LeaveFocus(WorkspaceViewMode.List);

                Assert.False(left);
                Assert.True(vm.IsFocusMode);
                Assert.Same(editor, vm.DetailEditor);
                Assert.True(editor.HasUnsavedChanges);
                Assert.Equal(revisionsBefore, CountRevisions(vm));
                Assert.Equal(0, scheduler.EnqueueLocalChangeCallCount);
            });

            Assert.Equal(liveBefore, SnapshotLiveProfile(liveRoot));
        }
        finally
        {
            SqliteTestUtil.TryDeleteDirectory(tempProfile);
        }
    }

    [Fact]
    public void WorkspaceFocus_DirtySave_CommitsOnceAndLeavesFocus_SingleRevision()
    {
        string liveRoot = QuickNotesDbContext.LiveProfileDirectory;
        string[] liveBefore = SnapshotLiveProfile(liveRoot);
        string tempProfile = CreateTempProfile("qn_focus_save");
        try
        {
            RunInSta(() =>
            {
                ThemeService.ApplyThemeColors(isDark: false);
                var scheduler = new FakeTrackingSyncScheduler();
                using var fixture = new TestProfileComposition(tempProfile, seedNotes: 3, ownsDirectory: false);
                var vm = fixture.CreateMainViewModel(syncScheduler: scheduler);

                vm.RequestUnsavedEditorDecision = () => UnsavedEditorDecision.Save;
                vm.RequestDraftDiscardDecision = () => true;

                vm.SetBoardViewModeCommand.Execute(null);
                var card = vm.Notes[1];
                vm.SelectedNote = card;
                Assert.True(vm.EnterFocus(card));

                string updatedText = "Сохранено из фокуса " + Guid.NewGuid().ToString("N");
                var editor = vm.DetailEditor!;
                editor.Text = updatedText;

                int revisionsBefore = CountRevisions(vm);
                scheduler.EnqueueLocalChangeCallCount = 0;

                vm.RequestWorkspaceViewMode(WorkspaceViewMode.Board);

                Assert.Equal(WorkspaceViewMode.Board, vm.WorkspaceViewMode);
                Assert.False(vm.IsFocusMode);
                Assert.Equal(revisionsBefore + 1, CountRevisions(vm));
                Assert.Equal(1, scheduler.EnqueueLocalChangeCallCount);

                using var db = vm.ContextFactory();
                Assert.Equal(updatedText, db.Notes.Single(n => n.Id == card.Id).Text);

                // Leaving once must not commit a second time.
                Assert.Equal(revisionsBefore + 1, db.NoteRevisions.Count());
            });

            Assert.Equal(liveBefore, SnapshotLiveProfile(liveRoot));
        }
        finally
        {
            SqliteTestUtil.TryDeleteDirectory(tempProfile);
        }
    }

    [Fact]
    public void WorkspaceFocus_DirtySaveFailure_StaysInFocus_KeepsDraft_NoFalseSavedStatus()
    {
        string liveRoot = QuickNotesDbContext.LiveProfileDirectory;
        string[] liveBefore = SnapshotLiveProfile(liveRoot);
        string tempProfile = CreateTempProfile("qn_focus_savefail");
        try
        {
            RunInSta(() =>
            {
                ThemeService.ApplyThemeColors(isDark: false);
                var scheduler = new FakeTrackingSyncScheduler();
                using var fixture = new TestProfileComposition(tempProfile, seedNotes: 3, ownsDirectory: false);
                var vm = fixture.CreateMainViewModel(syncScheduler: scheduler);

                var alerts = new List<string>();
                vm.AlertHandler = (message, _, _) => alerts.Add(message);
                vm.RequestUnsavedEditorDecision = () => UnsavedEditorDecision.Save;
                vm.RequestDraftDiscardDecision = () => true;

                var card = vm.Notes[0];
                vm.SelectedNote = card;
                Assert.True(vm.EnterFocus(card));
                var editor = vm.DetailEditor!;
                string dirty = "Провал сохранения " + Guid.NewGuid().ToString("N");
                editor.Text = dirty;
                editor.Commit = () => throw new InvalidOperationException("injected commit failure");

                int revisionsBefore = CountRevisions(vm);
                scheduler.EnqueueLocalChangeCallCount = 0;

                bool left = vm.LeaveFocus(WorkspaceViewMode.List);

                Assert.False(left);
                Assert.True(vm.IsFocusMode);
                Assert.Same(editor, vm.DetailEditor);
                Assert.Equal(dirty, editor.Text);
                Assert.True(editor.HasUnsavedChanges);
                Assert.Contains(alerts, message => message.Contains("Не удалось сохранить", StringComparison.Ordinal));
                Assert.Equal(revisionsBefore, CountRevisions(vm));
                Assert.Equal(0, scheduler.EnqueueLocalChangeCallCount);
            });

            Assert.Equal(liveBefore, SnapshotLiveProfile(liveRoot));
        }
        finally
        {
            SqliteTestUtil.TryDeleteDirectory(tempProfile);
        }
    }

    [Fact]
    public void WorkspaceFocus_DirtyDiscard_LeavesFocus_DiscardsJournal_NoteTextUnchanged()
    {
        string liveRoot = QuickNotesDbContext.LiveProfileDirectory;
        string[] liveBefore = SnapshotLiveProfile(liveRoot);
        string tempProfile = CreateTempProfile("qn_focus_discard");
        try
        {
            RunInSta(() =>
            {
                ThemeService.ApplyThemeColors(isDark: false);
                var scheduler = new FakeTrackingSyncScheduler();
                using var fixture = new TestProfileComposition(tempProfile, seedNotes: 3, ownsDirectory: false);
                var vm = fixture.CreateMainViewModel(syncScheduler: scheduler);

                vm.RequestUnsavedEditorDecision = () => UnsavedEditorDecision.Discard;
                vm.RequestDraftDiscardDecision = () => true;

                var card = vm.Notes[0];
                vm.SelectedNote = card;
                string committedText;
                using (var db = vm.ContextFactory())
                {
                    committedText = db.Notes.Single(n => n.Id == card.Id).Text;
                }

                Assert.True(vm.EnterFocus(card));
                var editor = vm.DetailEditor!;
                editor.Text = "Отбрасываемый черновик " + Guid.NewGuid().ToString("N");

                SaveDraftJournalSync(fixture.DraftJournalService, editor.CreateDraftSnapshot());
                Assert.True(fixture.DraftJournalService.HasJournalForNote(card.Id));

                int revisionsBefore = CountRevisions(vm);
                scheduler.EnqueueLocalChangeCallCount = 0;

                bool left = vm.LeaveFocus(WorkspaceViewMode.Board);

                Assert.True(left);
                Assert.False(vm.IsFocusMode);
                Assert.Equal(WorkspaceViewMode.Board, vm.WorkspaceViewMode);
                Assert.Null(vm.DetailEditor);
                Assert.False(fixture.DraftJournalService.HasJournalForNote(card.Id));
                Assert.Equal(revisionsBefore, CountRevisions(vm));
                Assert.Equal(0, scheduler.EnqueueLocalChangeCallCount);

                using var verifyDb = vm.ContextFactory();
                Assert.Equal(committedText, verifyDb.Notes.Single(n => n.Id == card.Id).Text);
            });

            Assert.Equal(liveBefore, SnapshotLiveProfile(liveRoot));
        }
        finally
        {
            SqliteTestUtil.TryDeleteDirectory(tempProfile);
        }
    }

    [Fact]
    public void WorkspaceFocus_BoardAndListTransitions_WithDirtySave_PreserveSelectedNoteId()
    {
        string liveRoot = QuickNotesDbContext.LiveProfileDirectory;
        string[] liveBefore = SnapshotLiveProfile(liveRoot);
        string tempProfile = CreateTempProfile("qn_focus_race");
        try
        {
            RunInSta(() =>
            {
                ThemeService.ApplyThemeColors(isDark: false);
                using var fixture = new TestProfileComposition(tempProfile, seedNotes: 4, ownsDirectory: false);
                var vm = fixture.CreateMainViewModel();

                vm.RequestUnsavedEditorDecision = () => UnsavedEditorDecision.Save;
                vm.RequestDraftDiscardDecision = () => true;

                // Board -> Focus -> List with a dirty then saved note.
                vm.SetBoardViewModeCommand.Execute(null);
                var boardTarget = vm.Notes[1];
                vm.SelectedNote = boardTarget;
                Assert.True(vm.EnterFocus(boardTarget));
                string boardSaved = "Сохранено при переходе Доска-Фокус-Список " + Guid.NewGuid().ToString("N");
                vm.DetailEditor!.Text = boardSaved;
                vm.RequestWorkspaceViewMode(WorkspaceViewMode.List);

                Assert.Equal(WorkspaceViewMode.List, vm.WorkspaceViewMode);
                Assert.Equal(boardTarget.Id, vm.SelectedNote?.Id);
                using (var db = vm.ContextFactory())
                {
                    Assert.Equal(boardSaved, db.Notes.Single(n => n.Id == boardTarget.Id).Text);
                }

                // List -> Focus -> Board with a dirty then saved note.
                var listTarget = vm.Notes[2];
                vm.SelectedNote = listTarget;
                Assert.True(vm.EnterFocus(listTarget));
                string listSaved = "Сохранено при переходе Список-Фокус-Доска " + Guid.NewGuid().ToString("N");
                vm.DetailEditor!.Text = listSaved;
                vm.RequestWorkspaceViewMode(WorkspaceViewMode.Board);

                Assert.Equal(WorkspaceViewMode.Board, vm.WorkspaceViewMode);
                Assert.Equal(listTarget.Id, vm.SelectedNote?.Id);
                using (var db2 = vm.ContextFactory())
                {
                    Assert.Equal(listSaved, db2.Notes.Single(n => n.Id == listTarget.Id).Text);
                }
            });

            Assert.Equal(liveBefore, SnapshotLiveProfile(liveRoot));
        }
        finally
        {
            SqliteTestUtil.TryDeleteDirectory(tempProfile);
        }
    }

    [Fact]
    public void WorkspaceFocus_RestartAfterFocus_RestoresListOrBoardNotFocus()
    {
        string liveRoot = QuickNotesDbContext.LiveProfileDirectory;
        string[] liveBefore = SnapshotLiveProfile(liveRoot);
        string tempProfile = CreateTempProfile("qn_focus_restart");
        try
        {
            RunInSta(() =>
            {
                ThemeService.ApplyThemeColors(isDark: false);

                using (var first = new TestProfileComposition(tempProfile, seedNotes: 2, ownsDirectory: false))
                {
                    var vm = first.CreateMainViewModel();
                    vm.SetBoardViewModeCommand.Execute(null);
                    var card = vm.Notes[0];
                    vm.SelectedNote = card;
                    Assert.True(vm.EnterFocus(card));
                    Assert.True(vm.IsFocusMode);
                    Assert.Equal(WorkspaceViewMode.Board, first.SettingsService.CurrentSettings.WorkspaceViewMode);
                    Assert.NotEqual(WorkspaceViewMode.Focus, first.SettingsService.CurrentSettings.WorkspaceViewMode);
                }

                using var restarted = new TestProfileComposition(tempProfile, seedNotes: 2, ownsDirectory: false);
                var restartedVm = restarted.CreateMainViewModel();
                Assert.Equal(WorkspaceViewMode.Board, restartedVm.WorkspaceViewMode);
                Assert.False(restartedVm.IsFocusMode);

                string settingsJson = File.ReadAllText(restarted.SettingsPath);
                Assert.DoesNotContain("Focus", settingsJson, StringComparison.Ordinal);
            });

            Assert.Equal(liveBefore, SnapshotLiveProfile(liveRoot));
        }
        finally
        {
            SqliteTestUtil.TryDeleteDirectory(tempProfile);
        }
    }

    [Fact]
    public void WorkspaceFocus_ToggleToBoardFromFocus_LeavesViaSharedContract()
    {
        string liveRoot = QuickNotesDbContext.LiveProfileDirectory;
        string[] liveBefore = SnapshotLiveProfile(liveRoot);
        string tempProfile = CreateTempProfile("qn_focus_toggle");
        try
        {
            RunInSta(() =>
            {
                ThemeService.ApplyThemeColors(isDark: false);
                using var fixture = new TestProfileComposition(tempProfile, seedNotes: 3, ownsDirectory: false);
                var vm = fixture.CreateMainViewModel();

                var card = vm.Notes[0];
                vm.SelectedNote = card;
                Assert.True(vm.EnterFocus(card));
                Assert.True(vm.IsFocusMode);

                // Clean Focus: toggling to Board leaves immediately.
                vm.IsBoardMode = true;
                Assert.False(vm.IsFocusMode);
                Assert.Equal(WorkspaceViewMode.Board, vm.WorkspaceViewMode);

                // Dirty Focus + Stay: toggling must not drop the draft and must remain in Focus.
                vm.RequestUnsavedEditorDecision = () => UnsavedEditorDecision.Stay;
                Assert.True(vm.EnterFocus(card));
                vm.DetailEditor!.Text = "Остаться в фокусе " + Guid.NewGuid().ToString("N");
                vm.IsBoardMode = true;
                Assert.True(vm.IsFocusMode);
                Assert.True(vm.DetailEditor!.HasUnsavedChanges);
            });

            Assert.Equal(liveBefore, SnapshotLiveProfile(liveRoot));
        }
        finally
        {
            SqliteTestUtil.TryDeleteDirectory(tempProfile);
        }
    }

    [Fact]
    public void WorkspaceFocus_ProgrammaticModeCommand_DuringDirtyFocus_DoesNotBypassLeaveContract()
    {
        string liveRoot = QuickNotesDbContext.LiveProfileDirectory;
        string[] liveBefore = SnapshotLiveProfile(liveRoot);
        string tempProfile = CreateTempProfile("qn_focus_cmd");
        try
        {
            RunInSta(() =>
            {
                ThemeService.ApplyThemeColors(isDark: false);
                var scheduler = new FakeTrackingSyncScheduler();
                using var fixture = new TestProfileComposition(tempProfile, seedNotes: 3, ownsDirectory: false);
                var vm = fixture.CreateMainViewModel(syncScheduler: scheduler);

                vm.RequestUnsavedEditorDecision = () => UnsavedEditorDecision.Stay;
                vm.RequestDraftDiscardDecision = () => true;

                var card = vm.Notes[0];
                vm.SelectedNote = card;
                Assert.True(vm.EnterFocus(card));
                var editor = vm.DetailEditor!;
                string dirty = "Черновик против команды " + Guid.NewGuid().ToString("N");
                editor.Text = dirty;
                Assert.True(editor.HasUnsavedChanges);

                int revisionsBefore = CountRevisions(vm);
                scheduler.EnqueueLocalChangeCallCount = 0;

                // Stay: the programmatic commands must not silently drop the draft.
                vm.SetBoardViewModeCommand.Execute(null);
                Assert.True(vm.IsFocusMode);
                Assert.Equal(WorkspaceViewMode.Focus, vm.WorkspaceViewMode);
                Assert.Same(editor, vm.DetailEditor);
                Assert.Equal(dirty, editor.Text);
                Assert.True(editor.HasUnsavedChanges);

                vm.SetListViewModeCommand.Execute(null);
                Assert.True(vm.IsFocusMode);
                Assert.Equal(WorkspaceViewMode.Focus, vm.WorkspaceViewMode);
                Assert.Same(editor, vm.DetailEditor);
                Assert.True(editor.HasUnsavedChanges);
                Assert.Equal(revisionsBefore, CountRevisions(vm));
                Assert.Equal(0, scheduler.EnqueueLocalChangeCallCount);

                // Save: the same commands leave through the shared contract instead.
                vm.RequestUnsavedEditorDecision = () => UnsavedEditorDecision.Save;
                vm.SetBoardViewModeCommand.Execute(null);
                Assert.False(vm.IsFocusMode);
                Assert.Equal(WorkspaceViewMode.Board, vm.WorkspaceViewMode);
                Assert.Equal(revisionsBefore + 1, CountRevisions(vm));
                Assert.Equal(1, scheduler.EnqueueLocalChangeCallCount);
                using var db = vm.ContextFactory();
                Assert.Equal(dirty, db.Notes.Single(n => n.Id == card.Id).Text);
            });

            Assert.Equal(liveBefore, SnapshotLiveProfile(liveRoot));
        }
        finally
        {
            SqliteTestUtil.TryDeleteDirectory(tempProfile);
        }
    }

    [Fact]
    public void WorkspaceFocus_CtrlEInFocus_DoesNotOpenSecondEditor()
    {
        string liveRoot = QuickNotesDbContext.LiveProfileDirectory;
        string[] liveBefore = SnapshotLiveProfile(liveRoot);
        string tempProfile = CreateTempProfile("qn_focus_ctrle");
        try
        {
            RunInSta(() =>
            {
                ThemeService.ApplyThemeColors(isDark: false);
                using var fixture = new TestProfileComposition(tempProfile, seedNotes: 3, ownsDirectory: false);
                var (window, vm) = fixture;
                PrepareWideWindow(window);
                try
                {
                    int editorOpenCount = 0;
                    var eventField = typeof(MainViewModel).GetField(
                        nameof(MainViewModel.RequestOpenNoteEditor),
                        BindingFlags.Instance | BindingFlags.NonPublic);
                    Assert.NotNull(eventField);
                    eventField!.SetValue(vm, (Func<NoteEditorViewModel, bool?>)(_ =>
                    {
                        editorOpenCount++;
                        return false;
                    }));

                    vm.SetBoardViewModeCommand.Execute(null);
                    var card = vm.Notes[1];
                    vm.SelectedNote = card;
                    window.UpdateWorkspaceLayout();
                    window.UpdateLayout();
                    window.BoardListBox.Focus();
                    ThreePaneUiSmokeRunner.DoEvents();
                    RaiseKey(window, Key.Enter);
                    Assert.True(vm.IsFocusMode);

                    var source = PresentationSource.FromVisual(window);
                    Assert.NotNull(source);
                    SetCtrlKeyState(true);
                    KeyEventArgs? ctrlEArgs;
                    try
                    {
                        ctrlEArgs = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.E)
                        {
                            RoutedEvent = Keyboard.PreviewKeyDownEvent
                        };
                        window.RaiseEvent(ctrlEArgs);
                        ThreePaneUiSmokeRunner.DoEvents();
                    }
                    finally
                    {
                        SetCtrlKeyState(false);
                    }

                    Assert.True(ctrlEArgs!.Handled);
                    Assert.Equal(0, editorOpenCount);
                    Assert.True(vm.IsFocusMode);
                }
                finally
                {
                    window.CloseWithoutShutdown();
                }
            });

            Assert.Equal(liveBefore, SnapshotLiveProfile(liveRoot));
        }
        finally
        {
            SqliteTestUtil.TryDeleteDirectory(tempProfile);
        }
    }

    [Fact]
    public void WorkspaceFocus_PersistedFocusMode_FailsSafeToList()
    {
        const string focusString = """{ "WorkspaceViewMode": "Focus" }""";
        var fromFocus = JsonSerializer.Deserialize<AppSettings>(focusString);
        Assert.NotNull(fromFocus);
        Assert.Equal(WorkspaceViewMode.List, fromFocus!.WorkspaceViewMode);

        const string focusNumber = """{ "WorkspaceViewMode": 2 }""";
        var fromNumber = JsonSerializer.Deserialize<AppSettings>(focusNumber);
        Assert.NotNull(fromNumber);
        Assert.Equal(WorkspaceViewMode.List, fromNumber!.WorkspaceViewMode);

        string writtenBoard = JsonSerializer.Serialize(new AppSettings { WorkspaceViewMode = WorkspaceViewMode.Board });
        Assert.Contains("Board", writtenBoard, StringComparison.Ordinal);

        string writtenFocus = JsonSerializer.Serialize(new AppSettings { WorkspaceViewMode = WorkspaceViewMode.Focus });
        Assert.DoesNotContain("Focus", writtenFocus, StringComparison.Ordinal);
        Assert.Contains("List", writtenFocus, StringComparison.Ordinal);
    }

    [Fact]
    public void WorkspaceFocus_EnterAndLeaveClean_DoesNotCreateRevisionOrEnqueue()
    {
        string liveRoot = QuickNotesDbContext.LiveProfileDirectory;
        string[] liveBefore = SnapshotLiveProfile(liveRoot);
        string tempProfile = CreateTempProfile("qn_focus_clean");
        try
        {
            RunInSta(() =>
            {
                ThemeService.ApplyThemeColors(isDark: false);
                var scheduler = new FakeTrackingSyncScheduler();
                using var fixture = new TestProfileComposition(tempProfile, seedNotes: 3, ownsDirectory: false);
                var vm = fixture.CreateMainViewModel(syncScheduler: scheduler);
                vm.RequestUnsavedEditorDecision = () => UnsavedEditorDecision.Discard;
                vm.RequestDraftDiscardDecision = () => true;

                var card = vm.Notes[0];
                vm.SelectedNote = card;
                int revisionsBefore = CountRevisions(vm);
                scheduler.EnqueueLocalChangeCallCount = 0;

                Assert.True(vm.EnterFocus(card));
                Assert.True(vm.LeaveFocus(WorkspaceViewMode.Board));

                Assert.Equal(WorkspaceViewMode.Board, vm.WorkspaceViewMode);
                Assert.False(vm.IsFocusMode);
                Assert.Equal(revisionsBefore, CountRevisions(vm));
                Assert.Equal(0, scheduler.EnqueueLocalChangeCallCount);
            });

            Assert.Equal(liveBefore, SnapshotLiveProfile(liveRoot));
        }
        finally
        {
            SqliteTestUtil.TryDeleteDirectory(tempProfile);
        }
    }

    private static int CountRevisions(MainViewModel vm)
    {
        using var db = vm.ContextFactory();
        return db.NoteRevisions.Count();
    }

    private static void SaveDraftJournalSync(IDraftJournalService service, DraftJournalSnapshot snapshot)
    {
        service.SaveJournalAsync(snapshot).GetAwaiter().GetResult();
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetKeyboardState(byte[] lpKeyState);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetKeyboardState(byte[] lpKeyState);

    private static void SetCtrlKeyState(bool isDown)
    {
        byte[] keys = new byte[256];
        GetKeyboardState(keys);
        keys[0x11] = isDown ? (byte)0x80 : (byte)0;
        keys[0xA2] = isDown ? (byte)0x80 : (byte)0;
        SetKeyboardState(keys);
    }
}
