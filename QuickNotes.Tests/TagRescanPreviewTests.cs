using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using QuickNotes.App.Data;
using QuickNotes.App.Helpers;
using QuickNotes.App.Models;
using QuickNotes.App.Services;
using QuickNotes.App.ViewModels;
using Xunit;

namespace QuickNotes.Tests;

[TestCategory(TestCategories.Integration)]
public class TagRescanPreviewTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<QuickNotesDbContext> _options;
    private readonly string _tempSettingsPath;
    private readonly List<IDisposable> _disposables = new();
    private readonly TagDetectionService _detectionService = new();
    private readonly TagRescanPreviewService _previewService = new();

    public TagRescanPreviewTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        _options = new DbContextOptionsBuilder<QuickNotesDbContext>()
            .UseSqlite(_connection)
            .Options;

        using var db = new QuickNotesDbContext(_options);
        DbInitializer.Initialize(db);

        _tempSettingsPath = Path.Combine(Path.GetTempPath(), $"quicknotes-rescan-test-{Guid.NewGuid():N}.json");
    }

    public void Dispose()
    {
        foreach (var d in _disposables)
        {
            try { d.Dispose(); } catch { }
        }
        _connection.Dispose();
        if (File.Exists(_tempSettingsPath))
        {
            try { File.Delete(_tempSettingsPath); } catch { }
        }
    }

    private MainViewModel CreateViewModel()
    {
        var settingsService = new SettingsService(_tempSettingsPath, _ => { });
        var hotkey = new GlobalHotkeyService((_, _, _, _) => true, (_, _) => true, IntPtr.Zero);
        var tray = new TrayIconService();
        _disposables.Add(hotkey);
        _disposables.Add(tray);
        var debouncer = new SearchDebouncer(0, a => a());
        _disposables.Add(debouncer);

        var vm = MainViewModelTestComposition.Create(
            () => new QuickNotesDbContext(_options),
            _detectionService,
            new SearchService(),
            settingsService,
            hotkey,
            new ClipboardCaptureService(),
            tray,
            new BackupService(),
            debouncer,
            new TagMergeService(),
            _previewService,
            draftJournalService: NoOpDraftJournalService.Instance);
        _disposables.Add(vm);
        return vm;
    }

    [Fact]
    public void CalculatePreview_CalculatesCorrectTotalsAndPerNote_ForActiveAdditionsAndRemovals()
    {
        var oracleTag = new Tag { Id = 1, Name = "Oracle" };
        var javaTag = new Tag { Id = 2, Name = "Java" };
        var csharpTag = new Tag { Id = 3, Name = "CSharp" };
        var allTags = new[] { oracleTag, javaTag, csharpTag };

        // Note 1: Text has "Oracle", has no tags -> Oracle should be added (+1)
        var note1 = new Note
        {
            Id = 101,
            Text = "Working with Oracle database"
        };

        // Note 2: Text has "General notes", has active Auto "Java" -> Java should be removed (-1)
        var note2 = new Note
        {
            Id = 102,
            Text = "General notes about project architecture"
        };
        note2.NoteTags.Add(new NoteTag
        {
            NoteId = 102,
            TagId = 2,
            Tag = javaTag,
            Origin = TagOrigin.Auto,
            IsSuppressed = false
        });

        // Note 3: Text has "CSharp guide", already has active Auto "CSharp" -> no changes
        var note3 = new Note
        {
            Id = 103,
            Text = "CSharp guide and best practices"
        };
        note3.NoteTags.Add(new NoteTag
        {
            NoteId = 103,
            TagId = 3,
            Tag = csharpTag,
            Origin = TagOrigin.Auto,
            IsSuppressed = false
        });

        var preview = _previewService.CalculatePreview(new[] { note1, note2, note3 }, allTags);

        Assert.Equal(3, preview.TotalNotesScanned);
        Assert.Equal(2, preview.TotalNotesWithChanges);
        Assert.Equal(1, preview.TotalAddedTags);
        Assert.Equal(1, preview.TotalRemovedTags);
        Assert.True(preview.HasChanges);
        Assert.Equal(2, preview.NoteChanges.Count);

        var change1 = preview.NoteChanges.FirstOrDefault(c => c.NoteId == 101);
        Assert.NotNull(change1);
        Assert.Equal(new[] { "Oracle" }, change1.AddedTagNames);
        Assert.Empty(change1.RemovedTagNames);
        Assert.Equal(1, change1.AddedCount);
        Assert.Equal(0, change1.RemovedCount);
        Assert.True(change1.HasChanges);

        var change2 = preview.NoteChanges.FirstOrDefault(c => c.NoteId == 102);
        Assert.NotNull(change2);
        Assert.Empty(change2.AddedTagNames);
        Assert.Equal(new[] { "Java" }, change2.RemovedTagNames);
        Assert.Equal(0, change2.AddedCount);
        Assert.Equal(1, change2.RemovedCount);
        Assert.True(change2.HasChanges);
    }

    [Fact]
    public void CalculatePreview_PreservesManualTags_NeitherAddedNorRemoved()
    {
        var importantTag = new Tag { Id = 1, Name = "Important" };
        var csharpTag = new Tag { Id = 2, Name = "CSharp" };
        var pythonTag = new Tag { Id = 3, Name = "Python" };
        var allTags = new[] { importantTag, csharpTag, pythonTag };

        var note = new Note
        {
            Id = 201,
            // Text does not contain "Important", but contains "CSharp" and "Python"
            Text = "CSharp and Python tutorial"
        };

        // Important is Manual: must NOT be removed even though not in text
        note.NoteTags.Add(new NoteTag
        {
            NoteId = 201,
            TagId = 1,
            Tag = importantTag,
            Origin = TagOrigin.Manual,
            IsSuppressed = false
        });

        // CSharp is already Manual: must NOT be re-added as Auto even though detected
        note.NoteTags.Add(new NoteTag
        {
            NoteId = 201,
            TagId = 2,
            Tag = csharpTag,
            Origin = TagOrigin.Manual,
            IsSuppressed = false
        });

        var preview = _previewService.CalculatePreview(new[] { note }, allTags);

        Assert.Equal(1, preview.TotalNotesScanned);
        Assert.Equal(1, preview.TotalNotesWithChanges);
        Assert.Equal(1, preview.TotalAddedTags);
        Assert.Equal(0, preview.TotalRemovedTags);

        var change = Assert.Single(preview.NoteChanges);
        Assert.Equal(201, change.NoteId);
        // Only Python is added
        Assert.Equal(new[] { "Python" }, change.AddedTagNames);
        // Important and CSharp are preserved, not removed
        Assert.Empty(change.RemovedTagNames);
    }

    [Fact]
    public void CalculatePreview_RespectsSuppressedTags_NeitherReaddedNorRemoved()
    {
        var oracleTag = new Tag { Id = 1, Name = "Oracle" };
        var sqlTag = new Tag { Id = 2, Name = "SQL" };
        var allTags = new[] { oracleTag, sqlTag };

        var note = new Note
        {
            Id = 301,
            // Text explicitly mentions "Oracle"
            Text = "Oracle database administration"
        };

        // Oracle was previously suppressed by user: must NOT be re-added
        note.NoteTags.Add(new NoteTag
        {
            NoteId = 301,
            TagId = 1,
            Tag = oracleTag,
            Origin = TagOrigin.Auto,
            IsSuppressed = true
        });

        // SQL was also suppressed, but is not in text: must NOT be removed
        note.NoteTags.Add(new NoteTag
        {
            NoteId = 301,
            TagId = 2,
            Tag = sqlTag,
            Origin = TagOrigin.Auto,
            IsSuppressed = true
        });

        var preview = _previewService.CalculatePreview(new[] { note }, allTags);

        Assert.Equal(1, preview.TotalNotesScanned);
        Assert.Equal(0, preview.TotalNotesWithChanges);
        Assert.Equal(0, preview.TotalAddedTags);
        Assert.Equal(0, preview.TotalRemovedTags);
        Assert.False(preview.HasChanges);
        Assert.Empty(preview.NoteChanges);
    }

    [Fact]
    public void CalculatePreview_ExcludesDeletedNotes_Completely()
    {
        var tag = new Tag { Id = 1, Name = "Oracle" };
        var allTags = new[] { tag };

        var activeNote = new Note
        {
            Id = 401,
            Text = "Active note with Oracle",
            DeletedAt = null
        };

        var deletedNote = new Note
        {
            Id = 402,
            Text = "Deleted note with Oracle",
            DeletedAt = DateTime.Now.AddDays(-1)
        };

        var preview = _previewService.CalculatePreview(new[] { activeNote, deletedNote }, allTags);

        Assert.Equal(1, preview.TotalNotesScanned);
        Assert.Equal(1, preview.TotalNotesWithChanges);
        var change = Assert.Single(preview.NoteChanges);
        Assert.Equal(401, change.NoteId);
    }

    [Fact]
    public void CalculatePreview_DoesNotMutateNotesOrNoteTagsInMemory()
    {
        var tag1 = new Tag { Id = 1, Name = "Oracle" };
        var tag2 = new Tag { Id = 2, Name = "Java" };
        var allTags = new[] { tag1, tag2 };

        var note = new Note
        {
            Id = 501,
            Text = "Oracle guide" // Mentions Oracle, does not mention Java
        };
        note.NoteTags.Add(new NoteTag
        {
            NoteId = 501,
            TagId = 2,
            Tag = tag2,
            Origin = TagOrigin.Auto,
            IsSuppressed = false
        });

        int initialTagCount = note.NoteTags.Count;
        var initialNoteTag = note.NoteTags.First();

        var preview = _previewService.CalculatePreview(new[] { note }, allTags);

        // Preview predicted 1 add (Oracle) and 1 remove (Java)
        Assert.Equal(1, preview.TotalAddedTags);
        Assert.Equal(1, preview.TotalRemovedTags);

        // Verify note and note.NoteTags were NOT mutated
        Assert.Equal(initialTagCount, note.NoteTags.Count);
        Assert.Same(initialNoteTag, note.NoteTags.First());
        Assert.Equal(2, note.NoteTags.First().TagId);
    }

    [Fact]
    public void TagRescanPreviewViewModel_PropertiesAndCommands_WhenChangesExist()
    {
        var preview = new TagRescanPreviewResult
        {
            TotalNotesScanned = 10,
            TotalNotesWithChanges = 2,
            TotalAddedTags = 3,
            TotalRemovedTags = 1,
            NoteChanges = new List<TagRescanNoteChange>
            {
                new()
                {
                    NoteId = 1,
                    Title = "Note 1",
                    AddedTagNames = new[] { "Oracle", "SQL" },
                    RemovedTagNames = Array.Empty<string>()
                },
                new()
                {
                    NoteId = 2,
                    Title = "Note 2",
                    AddedTagNames = new[] { "CSharp" },
                    RemovedTagNames = new[] { "Java" }
                }
            }
        };

        var vm = new TagRescanPreviewViewModel(preview);

        Assert.True(vm.HasChanges);
        Assert.True(vm.CanConfirm);
        Assert.Equal("10", vm.TotalNotesScannedText);
        Assert.Equal("2", vm.TotalNotesWithChangesText);
        Assert.Equal("+3", vm.TotalAddedTagsText);
        Assert.Equal("-1", vm.TotalRemovedTagsText);
        Assert.Equal("Отмена", vm.CancelButtonText);
        Assert.Equal(Visibility.Visible, vm.HasChangesVisibility);
        Assert.Equal(Visibility.Collapsed, vm.NoChangesVisibility);
        Assert.Equal(Visibility.Visible, vm.ConfirmButtonVisibility);
        Assert.Contains("будут изменены только автоматически назначенные теги", vm.WarningMessage);
        Assert.True(vm.ConfirmCommand.CanExecute(null));

        bool? closeResult = null;
        vm.RequestClose += res => closeResult = res;

        vm.ConfirmCommand.Execute(null);
        Assert.True(closeResult);

        vm.CancelCommand.Execute(null);
        Assert.False(closeResult);
    }

    [Fact]
    public void TagRescanPreviewViewModel_PropertiesAndCommands_WhenNoChangesExist()
    {
        var preview = new TagRescanPreviewResult
        {
            TotalNotesScanned = 5,
            TotalNotesWithChanges = 0,
            TotalAddedTags = 0,
            TotalRemovedTags = 0,
            NoteChanges = Array.Empty<TagRescanNoteChange>()
        };

        var vm = new TagRescanPreviewViewModel(preview);

        Assert.False(vm.HasChanges);
        Assert.False(vm.CanConfirm);
        Assert.Equal("5", vm.TotalNotesScannedText);
        Assert.Equal("0", vm.TotalNotesWithChangesText);
        Assert.Equal("0", vm.TotalAddedTagsText);
        Assert.Equal("0", vm.TotalRemovedTagsText);
        Assert.Equal("Закрыть", vm.CancelButtonText);
        Assert.Equal(Visibility.Collapsed, vm.HasChangesVisibility);
        Assert.Equal(Visibility.Visible, vm.NoChangesVisibility);
        Assert.Equal(Visibility.Collapsed, vm.ConfirmButtonVisibility);
        Assert.Contains("Изменений нет", vm.StatusMessage);
        Assert.False(vm.ConfirmCommand.CanExecute(null));

        bool? closeResult = null;
        vm.RequestClose += res => closeResult = res;

        // ConfirmCommand cannot execute when CanConfirm is false
        vm.ConfirmCommand.Execute(null);
        Assert.Null(closeResult);

        // Cancel/Close executes
        vm.CancelCommand.Execute(null);
        Assert.False(closeResult);
    }

    [Fact]
    public void MainViewModel_RescanAllNotes_WhenCancelled_MakesNoMutationsInDatabase()
    {
        using (var db = new QuickNotesDbContext(_options))
        {
            var tag = new Tag { Id = 1, Name = "Oracle" };
            db.Tags.Add(tag);
            db.Notes.Add(new Note { Id = 10, Text = "Working with Oracle" });
            db.SaveChanges();
        }

        var vm = CreateViewModel();

        bool dialogOpened = false;
        vm.RequestOpenRescanPreview += previewVm =>
        {
            dialogOpened = true;
            Assert.True(previewVm.HasChanges);
            // Simulate user clicking Cancel
            return false;
        };

        vm.RescanAllNotes();

        Assert.True(dialogOpened);

        // Verify DB was NOT modified: note has 0 tags
        using (var db = new QuickNotesDbContext(_options))
        {
            var note = db.Notes.Include(n => n.NoteTags).Single(n => n.Id == 10);
            Assert.Empty(note.NoteTags);
        }
    }

    [Fact]
    public void MainViewModel_RescanAllNotes_WhenConfirmed_AppliesChangesAtomicallyAndUpdatesViewModel()
    {
        using (var db = new QuickNotesDbContext(_options))
        {
            var oracleTag = new Tag { Id = 1, Name = "Oracle" };
            var javaTag = new Tag { Id = 2, Name = "Java" };
            var reviewTag = new Tag { Id = 3, Name = "Review" };
            db.Tags.AddRange(oracleTag, javaTag, reviewTag);

            // Note 1: Active, mentions Oracle, currently no tags
            var note1 = new Note { Id = 1, Text = "Connected to Oracle server" };

            // Note 2: Active, text mentions nothing, currently has Auto Java and Manual Review
            var note2 = new Note { Id = 2, Text = "Architecture overview without language" };
            note2.NoteTags.Add(new NoteTag { NoteId = 2, TagId = 2, Origin = TagOrigin.Auto, IsSuppressed = false });
            note2.NoteTags.Add(new NoteTag { NoteId = 2, TagId = 3, Origin = TagOrigin.Manual, IsSuppressed = false });

            // Note 3: Deleted, mentions Oracle
            var note3 = new Note { Id = 3, Text = "Old Oracle note", DeletedAt = DateTime.Now.AddDays(-2) };

            // Note 4: Active, mentions Oracle, but Oracle is Suppressed
            var note4 = new Note { Id = 4, Text = "Oracle notes that user suppressed" };
            note4.NoteTags.Add(new NoteTag { NoteId = 4, TagId = 1, Origin = TagOrigin.Auto, IsSuppressed = true });

            db.Notes.AddRange(note1, note2, note3, note4);
            db.SaveChanges();
        }

        var vm = CreateViewModel();

        TagRescanPreviewViewModel? capturedPreviewVm = null;
        string? capturedNotification = null;

        vm.RequestOpenRescanPreview += previewVm =>
        {
            capturedPreviewVm = previewVm;
            return true; // Simulate user confirming
        };

        vm.NotificationHandler = (msg, title) =>
        {
            capturedNotification = msg;
        };

        vm.RescanAllNotes();

        Assert.NotNull(capturedPreviewVm);
        Assert.Equal(3, capturedPreviewVm.Preview.TotalNotesScanned); // note 3 excluded because deleted
        Assert.Equal(2, capturedPreviewVm.Preview.TotalNotesWithChanges); // note 1 and note 2
        Assert.Equal(1, capturedPreviewVm.Preview.TotalAddedTags); // Oracle on note 1
        Assert.Equal(1, capturedPreviewVm.Preview.TotalRemovedTags); // Java removed on note 2

        Assert.NotNull(capturedNotification);
        Assert.Contains("Изменено заметок: 2", capturedNotification);
        Assert.Contains("Добавлено автотегов: 1", capturedNotification);
        Assert.Contains("удалено: 1", capturedNotification);

        // Verify DB mutations
        using (var db = new QuickNotesDbContext(_options))
        {
            var note1 = db.Notes.Include(n => n.NoteTags).Single(n => n.Id == 1);
            Assert.Single(note1.NoteTags);
            Assert.Equal(1, note1.NoteTags.First().TagId);
            Assert.Equal(TagOrigin.Auto, note1.NoteTags.First().Origin);

            var note2 = db.Notes.Include(n => n.NoteTags).Single(n => n.Id == 2);
            // Java was removed, Review was kept
            Assert.Single(note2.NoteTags);
            Assert.Equal(3, note2.NoteTags.First().TagId);
            Assert.Equal(TagOrigin.Manual, note2.NoteTags.First().Origin);

            var note3 = db.Notes.Include(n => n.NoteTags).Single(n => n.Id == 3);
            Assert.Empty(note3.NoteTags); // Deleted note was untouched

            var note4 = db.Notes.Include(n => n.NoteTags).Single(n => n.Id == 4);
            Assert.Single(note4.NoteTags);
            Assert.True(note4.NoteTags.First().IsSuppressed); // Suppressed was not re-added
        }

        // Verify MainViewModel collections and tree were updated
        Assert.NotEmpty(vm.Notes);
        var card1 = vm.Notes.FirstOrDefault(n => n.Id == 1);
        Assert.NotNull(card1);
        Assert.Contains("Oracle", card1.Tags);
    }

    [Fact]
    public void MainViewModel_RescanAllNotes_WhenNoChanges_DoesNotOfferConfirmAction()
    {
        using (var db = new QuickNotesDbContext(_options))
        {
            var tag = new Tag { Id = 1, Name = "Oracle" };
            db.Tags.Add(tag);
            var note = new Note { Id = 1, Text = "Oracle notes" };
            note.NoteTags.Add(new NoteTag { NoteId = 1, TagId = 1, Origin = TagOrigin.Auto, IsSuppressed = false });
            db.Notes.Add(note);
            db.SaveChanges();
        }

        var vm = CreateViewModel();

        bool dialogOpened = false;
        vm.RequestOpenRescanPreview += previewVm =>
        {
            dialogOpened = true;
            Assert.False(previewVm.HasChanges);
            Assert.False(previewVm.CanConfirm);
            Assert.Equal("Закрыть", previewVm.CancelButtonText);
            Assert.Contains("Изменений нет", previewVm.StatusMessage);
            return false;
        };

        vm.RescanAllNotes();

        Assert.True(dialogOpened);
    }

    [Fact]
    public void MainViewModel_RescanNote_SingleNoteCommand_WorksWithoutPreview()
    {
        using (var db = new QuickNotesDbContext(_options))
        {
            var tag = new Tag { Id = 1, Name = "Oracle" };
            db.Tags.Add(tag);
            var note = new Note { Id = 1, Text = "Working with Oracle" };
            db.Notes.Add(note);
            db.SaveChanges();
        }

        var vm = CreateViewModel();

        bool previewOpened = false;
        vm.RequestOpenRescanPreview += _ =>
        {
            previewOpened = true;
            return true;
        };

        var card = vm.Notes.Single(n => n.Id == 1);
        vm.RescanNote(card);

        // Preview should NOT be opened for single note rescan
        Assert.False(previewOpened);

        // Note in DB must have Oracle tag added
        using (var db = new QuickNotesDbContext(_options))
        {
            var note = db.Notes.Include(n => n.NoteTags).Single(n => n.Id == 1);
            Assert.Single(note.NoteTags);
            Assert.Equal(1, note.NoteTags.First().TagId);
        }
    }
}
