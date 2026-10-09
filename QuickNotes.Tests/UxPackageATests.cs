using System;
using System.IO;
using System.Linq;
using System.Windows;
using Microsoft.EntityFrameworkCore;
using QuickNotes.App.Helpers;
using QuickNotes.App.Models;
using QuickNotes.App.Services;
using QuickNotes.App.ViewModels;
using QuickNotes.App.Views;
using Xunit;

namespace QuickNotes.Tests;

[Collection(LiveProfileSnapshotCollection.Name)]
[TestCategory(TestCategories.UiSensitive)]
public sealed class UxPackageATests
{
    [Fact]
    public void ShortcutCatalog_HeadingIsCtrlShiftH_AndDefaultOcrIsNotNumberedList()
    {
        Assert.Equal("Ctrl+Shift+H", ShortcutCatalog.HeadingGesture);
        Assert.Equal("Ctrl+H", ShortcutCatalog.HistoryGesture);
        Assert.Equal("Ctrl+Shift+O", ShortcutCatalog.NumberedListGesture);
        Assert.Equal("R", ShortcutCatalog.DefaultOcrKey);
        Assert.Equal("R", new AppSettings().OcrHotkeyKey);
        Assert.False(ShortcutCatalog.ConflictsWithEditorReserved(new AppSettings(), out _));

        var collidingOcr = new AppSettings
        {
            OcrHotkeyCtrl = true,
            OcrHotkeyShift = true,
            OcrHotkeyAlt = false,
            OcrHotkeyWin = false,
            OcrHotkeyKey = "O"
        };
        Assert.True(ShortcutCatalog.ConflictsWithEditorReserved(collidingOcr, out var name));
        Assert.Contains("нумерованный", name, StringComparison.OrdinalIgnoreCase);
        Assert.True(GlobalHotkeyService.ValidateSettings(collidingOcr, out var registerErr), registerErr);
        Assert.False(GlobalHotkeyService.ValidateOcrAgainstEditorReserved(collidingOcr, out var err));
        Assert.Contains("редактора", err, StringComparison.OrdinalIgnoreCase);

        string help = ShortcutCatalog.BuildHelpHotkeysParagraph(ShortcutCatalog.DefaultOcrGesture);
        Assert.Contains(ShortcutCatalog.DefaultOcrGesture, help, StringComparison.Ordinal);
        Assert.Contains(ShortcutCatalog.NumberedListGesture, help, StringComparison.Ordinal);
        Assert.Contains(ShortcutCatalog.HeadingGesture, help, StringComparison.Ordinal);
        Assert.Contains(ShortcutCatalog.HistoryGesture, help, StringComparison.Ordinal);
        Assert.Contains("Enter в списке открывает выбранную заметку", help, StringComparison.Ordinal);
        Assert.Contains("Ctrl+E", help, StringComparison.Ordinal);
    }

    [Fact]
    public void ToolbarAndHelp_UseShortcutCatalog_NotStaleCtrlHHeadingOrOcrOnO()
    {
        string root = FindSolutionRoot();
        string editorXaml = File.ReadAllText(Path.Combine(root, "QuickNotes.App", "Views", "NoteEditorWindow.xaml"));
        string mainXaml = File.ReadAllText(Path.Combine(root, "QuickNotes.App", "Views", "MainWindow.xaml"));
        string helpCs = File.ReadAllText(Path.Combine(root, "QuickNotes.App", "Views", "HelpWindow.xaml.cs"));
        string conflictsXaml = File.ReadAllText(Path.Combine(root, "QuickNotes.App", "Views", "SyncConflictsWindow.xaml"));

        Assert.Contains("ShortcutCatalog.HeadingToolbarTip", editorXaml);
        Assert.Contains("ShortcutCatalog.HeadingToolbarTip", mainXaml);
        Assert.DoesNotContain("Заголовок (Ctrl+H)", editorXaml);
        Assert.Contains("ShortcutCatalog.BuildHelpHotkeysParagraph", helpCs);
        Assert.DoesNotContain("Key=\"D1\"", conflictsXaml);
        Assert.Contains("Не сохранять (Esc)", editorXaml);
    }

    [Fact]
    public void GlobalHotkeyService_SuspendsOcrWhileEditorHasFocus()
    {
        StaTestHarness.Run(() =>
        {
            var registered = new System.Collections.Generic.HashSet<int>();
            GlobalHotkeyService.RegisterHotKeyDelegate mockRegister = (_, id, _, _) =>
            {
                registered.Add(id);
                return true;
            };
            GlobalHotkeyService.UnregisterHotKeyDelegate mockUnregister = (_, id) =>
            {
                registered.Remove(id);
                return true;
            };

            using var service = new GlobalHotkeyService(mockRegister, mockUnregister, testHwnd: (IntPtr)42);
            var settings = new AppSettings();
            Assert.True(service.Register(settings, out var error), error);
            Assert.True(service.IsOcrRegistered);

            service.SetOcrSuspended(true);
            Assert.False(service.IsOcrRegistered);
            Assert.True(service.IsEditorRegistered);

            service.SetOcrSuspended(false);
            Assert.True(service.IsOcrRegistered);
        }, TimeSpan.FromSeconds(15));
    }

    [Fact]
    public void GlobalHotkeyService_Register_AllowsLegacyOcrOnNumberedListOutsideEditor()
    {
        StaTestHarness.Run(() =>
        {
            var registered = new System.Collections.Generic.HashSet<int>();
            GlobalHotkeyService.RegisterHotKeyDelegate mockRegister = (_, id, _, _) =>
            {
                registered.Add(id);
                return true;
            };
            GlobalHotkeyService.UnregisterHotKeyDelegate mockUnregister = (_, id) =>
            {
                registered.Remove(id);
                return true;
            };

            using var service = new GlobalHotkeyService(mockRegister, mockUnregister, testHwnd: (IntPtr)42);
            var legacyOcr = new AppSettings
            {
                OcrHotkeyCtrl = true,
                OcrHotkeyShift = true,
                OcrHotkeyAlt = false,
                OcrHotkeyWin = false,
                OcrHotkeyKey = "O"
            };
            Assert.True(service.Register(legacyOcr, out var error), error);
            Assert.True(service.IsEditorRegistered);
            Assert.True(service.IsOcrRegistered);

            service.SetOcrSuspended(true);
            Assert.False(service.IsOcrRegistered);
            Assert.True(service.IsEditorRegistered);
        }, TimeSpan.FromSeconds(15));
    }

    [Fact]
    public void NoteEditor_EscCancelAndClose_UseSaveDiscardStayContract()
    {
        StaTestHarness.Run(() =>
        {
            using var vm = TestEditorFactory.CreateEditor(initialText: "hello");
            vm.Text = "hello world";
            Assert.True(vm.HasUnsavedChanges);

            bool closed = false;
            bool? closeResult = null;
            vm.RequestClose += r =>
            {
                closed = true;
                closeResult = r;
            };

            vm.RequestUnsavedEditorDecision = () => UnsavedEditorDecision.Stay;
            vm.CancelCommand.Execute(null);
            Assert.False(closed);
            Assert.True(vm.HasUnsavedChanges);

            vm.RequestUnsavedEditorDecision = () => UnsavedEditorDecision.Discard;
            vm.CancelCommand.Execute(null);
            Assert.True(closed);
            Assert.False(closeResult);
        }, TimeSpan.FromSeconds(15));
    }

    [Fact]
    public void NoteEditor_SaveFromUnsavedPrompt_KeepsJournalOnCommitFailure()
    {
        string journalDir = Path.Combine(Path.GetTempPath(), "qn_ux_a_journal_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(journalDir);
        try
        {
            var service = new QuickNotes.App.Services.DraftJournal.DraftJournalService(baseDirectory: journalDir);
            StaTestHarness.Run(() =>
            {
                using var vm = new NoteEditorViewModel(
                    new TagDetectionService(),
                    new(),
                    initialTitle: "T",
                    initialText: "body",
                    draftJournalService: service);
                vm.Text = "changed body";
                vm.AlertHandler = (_, _, _) => { };
                vm.Commit = () => throw new InvalidOperationException("commit failed");
                vm.RequestUnsavedEditorDecision = () => UnsavedEditorDecision.Save;

                bool closed = false;
                vm.RequestClose += _ => closed = true;
                vm.CancelCommand.Execute(null);

                Assert.False(closed);
                Assert.True(service.HasJournal(vm.DraftId) || vm.HasUnsavedChanges);
            }, TimeSpan.FromSeconds(15));
        }
        finally
        {
            try { Directory.Delete(journalDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void InlineEditor_SwitchNote_EmptyAndNonEmptyUseSamePrompt_StayKeepsSelection()
    {
        string profile = Path.Combine(Path.GetTempPath(), "qn_ux_a_switch_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profile);
        try
        {
            StaTestHarness.Run(() =>
            {
                using var fixture = new TestProfileComposition(profile, seedNotes: 2, ownsDirectory: false);
                var vm = fixture.MainViewModel;
                vm.AlertHandler = (_, _, _) => { };
                int prompts = 0;
                UnsavedEditorDecision next = UnsavedEditorDecision.Stay;
                vm.RequestUnsavedEditorDecision = () =>
                {
                    prompts++;
                    return next;
                };

                vm.SelectedNote = vm.Notes[0];
                Assert.NotNull(vm.DetailEditor);
                int firstId = vm.Notes[0].Id;

                vm.DetailEditor!.Text = "   ";
                Assert.True(vm.DetailEditor.HasUnsavedChanges);

                vm.SelectedNote = vm.Notes[1];
                Assert.Equal(1, prompts);
                Assert.Equal(firstId, vm.SelectedNote!.Id);
                Assert.Equal(firstId, vm.DetailEditor.NoteId);

                next = UnsavedEditorDecision.Discard;
                vm.SelectedNote = vm.Notes[1];
                Assert.Equal(2, prompts);
                Assert.Equal(vm.Notes[1].Id, vm.SelectedNote!.Id);
                Assert.Equal(vm.Notes[1].Id, vm.DetailEditor!.NoteId);
            }, TimeSpan.FromSeconds(30));
        }
        finally
        {
            SqliteTestUtil.TryDeleteDirectory(profile);
        }
    }

    [Fact]
    public void InlineEditor_SaveOnSwitch_PersistsNonEmpty_AndEmptyIsBlockedWithVisibleError()
    {
        string profile = Path.Combine(Path.GetTempPath(), "qn_ux_a_save_switch_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profile);
        try
        {
            StaTestHarness.Run(() =>
            {
                using var fixture = new TestProfileComposition(profile, seedNotes: 2, ownsDirectory: false);
                var vm = fixture.MainViewModel;
                string? alert = null;
                vm.AlertHandler = (message, _, _) => alert = message;
                vm.RequestUnsavedEditorDecision = () => UnsavedEditorDecision.Save;

                vm.SelectedNote = vm.Notes[0];
                int firstId = vm.Notes[0].Id;
                vm.DetailEditor!.Text = string.Empty;
                vm.SelectedNote = vm.Notes[1];
                Assert.Equal(firstId, vm.SelectedNote!.Id);
                Assert.Contains("пустым", alert, StringComparison.OrdinalIgnoreCase);

                alert = null;
                vm.DetailEditor!.Text = "Committed from leave prompt";
                vm.SelectedNote = vm.Notes[1];
                Assert.Equal(vm.Notes[1].Id, vm.SelectedNote!.Id);

                using var db = fixture.CreateDbContext();
                Assert.Equal("Committed from leave prompt", db.Notes.Single(n => n.Id == firstId).Text);
            }, TimeSpan.FromSeconds(30));
        }
        finally
        {
            SqliteTestUtil.TryDeleteDirectory(profile);
        }
    }

    [Fact]
    public void UnsavedDialog_ShowsSaveDiscardStayButtons()
    {
        StaTestHarness.Run(() =>
        {
            var dialog = new UnsavedEditorDialog
            {
                ShowInTaskbar = false,
                Left = -20000,
                Top = -20000,
                WindowStartupLocation = WindowStartupLocation.Manual
            };
            dialog.Show();
            dialog.UpdateLayout();
            var prompt = (System.Windows.Controls.TextBlock)dialog.FindName("PromptText")!;
            var save = (System.Windows.Controls.Button)dialog.FindName("SaveButton")!;
            var discard = (System.Windows.Controls.Button)dialog.FindName("DiscardButton")!;
            var stay = (System.Windows.Controls.Button)dialog.FindName("StayButton")!;
            Assert.Equal(UnsavedEditorPromptText.Message, prompt.Text);
            Assert.Equal("Сохранить", save.Content);
            Assert.Equal("Не сохранять", discard.Content);
            Assert.Equal("Вернуться", stay.Content);
            dialog.Close();
        }, TimeSpan.FromSeconds(15));
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
}
