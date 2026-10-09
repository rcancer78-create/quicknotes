using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using QuickNotes.App.Data;
using QuickNotes.App.Helpers;
using QuickNotes.App.Models;
using QuickNotes.App.Services;
using QuickNotes.App.ViewModels;
using QuickNotes.App.Views;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace QuickNotes.Tests;

[TestCategory(TestCategories.UiSensitive)]
public class RegressionTests
{
    private static void Sta(Action action) => StaTestHarness.Run(action, TimeSpan.FromSeconds(25));

    [Fact]
    public void SendInput_HasNativeSize() => Assert.Equal(IntPtr.Size == 8 ? 40 : 28, Marshal.SizeOf<Win32Helper.INPUT>());

    [Fact]
    public void RescanDetachedCache_AndEditorSuppression_PersistAcrossContexts()
    {
        string path = Path.Combine(Path.GetTempPath(), $"quicknotes-regression-{Guid.NewGuid():N}.db");
        try
        {
            List<Tag> cache;
            using (var db = new QuickNotesDbContext(path))
            {
                DbInitializer.Initialize(db);
                db.Tags.AddRange(new Tag { Name = "Oracle" }, new Tag { Name = "Important" });
                db.Notes.AddRange(new Note { Text = "Oracle" }, new Note { Text = "Oracle" });
                db.SaveChanges();
                cache = db.Tags.Include(t => t.Synonyms).ToList();
            }
            using (var db = new QuickNotesDbContext(path))
            {
                foreach (var note in db.Notes.Include(n => n.NoteTags).ToList())
                    new TagDetectionService().RescanNote(note, cache);
                db.SaveChanges();
                Assert.Equal(2, db.Tags.Count());
            }
            Sta(() =>
            {
                using var db = new QuickNotesDbContext(path);
                var note = db.Notes.Include(n => n.NoteTags).ThenInclude(nt => nt.Tag).OrderBy(n => n.Id).First();
                var created = note.CreatedAt;
                using var vm = new NoteEditorViewModel(new(), cache, note, draftJournalService: NoOpDraftJournalService.Instance);
                vm.RemoveTagCommand.Execute(vm.ActiveTags.Single());
                vm.SelectedTagToAdd = cache.Single(t => t.Name == "Important");
                vm.AddTagCommand.Execute(null);
                vm.Rescan();
                vm.ApplyToNote(note);
                db.SaveChanges();
                Assert.Equal(created, note.CreatedAt);
                // Edit the same link again: avoid duplicate tracked composite keys.
                vm.Text = "Oracle updated";
                vm.ApplyToNote(note);
                db.SaveChanges();
            });
            using (var db = new QuickNotesDbContext(path))
            {
                var notes = db.Notes.Include(n => n.NoteTags).OrderBy(n => n.Id).ToList();
                foreach (var note in notes) new TagDetectionService().RescanNote(note, cache);
                db.SaveChanges();
                Assert.Contains(notes[0].NoteTags, t => t.IsSuppressed);
                Assert.Contains(notes[0].NoteTags, t => t.Origin == TagOrigin.Manual);
                Assert.False(notes[1].NoteTags.Single().IsSuppressed);
                Assert.Equal(2, db.Tags.Count());
                using var vm = new NoteEditorViewModel(new(), cache, notes[0], draftJournalService: NoOpDraftJournalService.Instance);
                vm.SelectedTagToAdd = cache.Single(t => t.Name == "Oracle");
                vm.AddTagCommand.Execute(null);
                vm.ApplyToNote(notes[0]);
                db.SaveChanges();
                Assert.All(notes[0].NoteTags, t => Assert.False(t.IsSuppressed));
            }
        }
        finally { SqliteConnection.ClearAllPools(); File.Delete(path); }
    }

    [Fact]
    public void Fts_UpdateDeleteRussianAndLiteralQueries()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var db = new QuickNotesDbContext(new DbContextOptionsBuilder<QuickNotesDbContext>().UseSqlite(connection).Options);
        DbInitializer.Initialize(db);
        DbInitializer.Initialize(db);
        var note = new Note { Text = "ОРАКЛ Oracle C#" };
        db.Notes.Add(note); db.SaveChanges();
        var search = new SearchService();
        Assert.Single(search.SearchNoteIds(db, "оракл"));
        note.Text = "PostgreSQL"; db.SaveChanges();
        Assert.Empty(search.SearchNoteIds(db, "Oracle"));
        Assert.Single(search.SearchNoteIds(db, "Postgre"));
        Assert.Empty(search.SearchNoteIds(db, "\" OR *"));
        db.Notes.Remove(note); db.SaveChanges();
        Assert.Empty(search.SearchNoteIds(db, "PostgreSQL"));
    }

    [Fact]
    public void SettingsFailure_DoesNotPublishUnsavedState()
    {
        string path = Path.Combine(Path.GetTempPath(), $"quicknotes-settings-{Guid.NewGuid():N}.json");
        try
        {
            var service = new SettingsService(path, _ => throw new IOException("Simulated registry failure"));
            Assert.Throws<IOException>(() => service.SaveSettings(new AppSettings { HotkeyKey = "F9" }));
            Assert.Equal("Space", service.CurrentSettings.HotkeyKey);
            Assert.False(File.Exists(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Hotkey_ReplacementConflictAndDispose_PreserveRegistration() => Sta(() =>
    {
        const uint modifiers = Win32Helper.MOD_CONTROL | Win32Helper.MOD_ALT | Win32Helper.MOD_SHIFT | Win32Helper.MOD_NOREPEAT;
        var first = new AppSettings
        {
            HotkeyCtrl = true,
            HotkeyShift = true,
            HotkeyAlt = true,
            HotkeyKey = "F23",
            InstantHotkeyCtrl = true,
            InstantHotkeyShift = true,
            InstantHotkeyAlt = true,
            InstantHotkeyKey = "F20",
            OcrHotkeyKey = "F21"
        };
        var second = new AppSettings
        {
            HotkeyCtrl = true,
            HotkeyShift = true,
            HotkeyAlt = true,
            HotkeyKey = "F24",
            InstantHotkeyCtrl = true,
            InstantHotkeyShift = true,
            InstantHotkeyAlt = true,
            InstantHotkeyKey = "F20",
            OcrHotkeyKey = "F21"
        };
        using var service = new GlobalHotkeyService();
        Assert.True(service.Register(first, out var error), error);
        Assert.True(service.Register(first, out error), error);
        Assert.False(Win32Helper.RegisterHotKey(IntPtr.Zero, 20001, modifiers, 0x86));
        Assert.True(Win32Helper.RegisterHotKey(IntPtr.Zero, 20002, modifiers, 0x87));
        try
        {
            Assert.False(service.Register(second, out _));
            Assert.False(Win32Helper.RegisterHotKey(IntPtr.Zero, 20003, modifiers, 0x86));
        }
        finally { Win32Helper.UnregisterHotKey(IntPtr.Zero, 20002); }
        Assert.True(service.Register(second, out error), error);
        Assert.True(Win32Helper.RegisterHotKey(IntPtr.Zero, 20004, modifiers, 0x86));
        Win32Helper.UnregisterHotKey(IntPtr.Zero, 20004);
        service.Dispose();
        Assert.True(Win32Helper.RegisterHotKey(IntPtr.Zero, 20005, modifiers, 0x87));
        Win32Helper.UnregisterHotKey(IntPtr.Zero, 20005);
    });

    private static IEnumerable<T> Visuals<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typed) yield return typed;
            foreach (var nested in Visuals<T>(child)) yield return nested;
        }
    }

    [Fact]
    public void Wpf_CardsMenusEditorAndTrayClose() => Sta(() =>
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var options = new DbContextOptionsBuilder<QuickNotesDbContext>().UseSqlite(connection).Options;
        using (var db = new QuickNotesDbContext(options))
        {
            DbInitializer.Initialize(db);
            db.Tags.Add(new Tag { Name = "Oracle" });
            db.Notes.AddRange(new Note { Text = "short" }, new Note { Text = string.Join("\n", Enumerable.Range(1, 20).Select(i => $"line {i}")) });
            db.SaveChanges();
        }
        string settingsPath = Path.Combine(Path.GetTempPath(), $"quicknotes-ui-{Guid.NewGuid():N}.json");
        using var hotkey = new GlobalHotkeyService((_, _, _, _) => true, (_, _) => true, IntPtr.Zero);
        using var tray = new TrayIconService();
        using var vm = MainViewModelTestComposition.Create(() => new QuickNotesDbContext(options), new(), new(),
            new SettingsService(settingsPath, _ => { }), hotkey, new(), tray, draftJournalService: NoOpDraftJournalService.Instance);
        vm.RequestUnsavedEditorDecision = () => UnsavedEditorDecision.Discard;
        var app = StaTestHarness.EnsureApplication();
        app.Resources["CanvasBrush"] = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.WhiteSmoke);
        app.Resources["SurfaceBrush"] = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.White);
        app.Resources["LineBrush"] = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.LightGray);
        app.Resources["AccentBrush"] = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Indigo);
        app.Resources["InkBrush"] = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Black);
        app.Resources["MutedBrush"] = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Gray);
        var window = new MainWindow(vm) { ShowInTaskbar = false, Left = -10000, Top = -10000, WindowStartupLocation = WindowStartupLocation.Manual };
        try
        {
            window.Show(); window.UpdateLayout();
            static string BlockText(TextBlock t)
            {
                if (!string.IsNullOrEmpty(t.Text)) return t.Text;
                return string.Concat(t.Inlines.OfType<System.Windows.Documents.Run>().Select(r => r.Text));
            }
            var list = window.NotesListBox;
            var bodies = Visuals<TextBlock>(list).Where(t => t.TextWrapping == TextWrapping.Wrap && (BlockText(t) == "short" || BlockText(t).StartsWith("line 1\n"))).ToList();
            Assert.Equal(2, bodies.Count);
            var shortBody = bodies.Single(t => BlockText(t) == "short");
            var longBody = bodies.Single(t => BlockText(t) != "short");
            Assert.True(shortBody.ActualHeight < longBody.ActualHeight);
            Assert.InRange(longBody.ActualHeight, 90, 125);
            vm.Notes.Single(n => n.Text != "short").IsExpanded = true;
            window.UpdateLayout();
            Assert.True(longBody.ActualHeight > 300);
            foreach (var target in Visuals<FrameworkElement>(window).Where(e => e.ContextMenu != null))
            {
                var menu = target.ContextMenu;
                menu.PlacementTarget = target;
                menu.DataContext = target.DataContext;
                Assert.All(menu.Items.OfType<MenuItem>(), item => Assert.NotNull(item.Command));
            }
            using var editorVm = new NoteEditorViewModel(new(), new(), draftJournalService: NoOpDraftJournalService.Instance)
            {
                RequestUnsavedEditorDecision = () => UnsavedEditorDecision.Discard
            };
            var editor = new NoteEditorWindow(editorVm) { ShowInTaskbar = false, Left = -10000, Top = -10000, WindowStartupLocation = WindowStartupLocation.Manual };
            editor.Show(); editor.UpdateLayout();
            editorVm.ApplyDiscardUnsaved(deleteJournal: true);
            editor.Close();
            var settings = new SettingsWindow(SettingsViewModelTestComposition.Create(new SettingsService(settingsPath, _ => { }), hotkey))
            {
                ShowInTaskbar = false,
                Left = -10000,
                Top = -10000,
                WindowStartupLocation = WindowStartupLocation.Manual
            };
            settings.Show(); settings.UpdateLayout();
            Assert.True(settings.ActualHeight >= settings.MinHeight);
            var saveCancel = Visuals<Button>(settings)
                .Where(b => b.Content is "Отмена" or "Сохранить")
                .ToList();
            var visibleSaveCancel = saveCancel.Where(b => b.IsVisible).ToList();
            Assert.Equal(2, visibleSaveCancel.Count);
            Assert.Contains(visibleSaveCancel, b => Equals(b.Content, "Отмена"));
            Assert.Contains(visibleSaveCancel, b => Equals(b.Content, "Сохранить"));
            Assert.Contains(saveCancel, b => !b.IsVisible && Equals(b.Content, "Сохранить"));
            settings.Close();
            window.Close();
            Assert.False(window.IsVisible);
            window.Show();
            Assert.True(window.IsVisible);
        }
        finally { window.CloseWithoutShutdown(); File.Delete(settingsPath); }
    });
}
