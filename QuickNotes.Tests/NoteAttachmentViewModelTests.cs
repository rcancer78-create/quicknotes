using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using QuickNotes.App.Data;
using QuickNotes.App.Models;
using QuickNotes.App.Services;
using QuickNotes.App.Services.DraftJournal;
using QuickNotes.App.ViewModels;
using Xunit;

namespace QuickNotes.Tests;

[TestCategory(TestCategories.Integration)]
public class NoteAttachmentViewModelTests : IDisposable
{
    private readonly string _testBaseDir;
    private readonly string _settingsFile;
    private readonly AttachmentStorageService _storageService;
    private readonly SettingsService _settingsService;
    private readonly TagDetectionService _tagDetectionService;

    public NoteAttachmentViewModelTests()
    {
        _testBaseDir = Path.Combine(Path.GetTempPath(), $"QuickNotes_AttVmTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testBaseDir);

        _settingsFile = Path.Combine(_testBaseDir, "settings.json");
        _settingsService = new SettingsService(_settingsFile, _ => { });

        _storageService = new AttachmentStorageService(_testBaseDir);
        _tagDetectionService = new TagDetectionService();
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testBaseDir))
            {
                Directory.Delete(_testBaseDir, recursive: true);
            }
        }
        catch
        {
            // Ignore cleanup errors
        }
    }

    private static (QuickNotesDbContext Context, SqliteConnection Connection) CreateInMemoryDb()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<QuickNotesDbContext>()
            .UseSqlite(connection)
            .Options;

        var context = new QuickNotesDbContext(options);
        DbInitializer.Initialize(context);

        return (context, connection);
    }

    private static Func<QuickNotesDbContext> CreateContextFactory(SqliteConnection conn)
    {
        var options = new DbContextOptionsBuilder<QuickNotesDbContext>()
            .UseSqlite(conn)
            .Options;
        return () => new QuickNotesDbContext(options);
    }

    [Fact]
    public void NoteEditorViewModel_AddAttachment_Success_UpdatesCollectionsAndUnsavedChanges()
    {
        var (db, conn) = CreateInMemoryDb();
        using var _conn = conn;
        using var _db = db;

        var sampleFile = Path.Combine(_testBaseDir, "document.txt");
        File.WriteAllText(sampleFile, "Note content test 123");

        using var vm = new NoteEditorViewModel(
            _tagDetectionService,
            new List<Tag>(),
            contextFactory: CreateContextFactory(conn),
            attachmentStorageService: _storageService,
            settingsService: _settingsService,
            draftJournalService: NoOpDraftJournalService.Instance);

        Assert.False(vm.HasAttachments);
        Assert.Equal(string.Empty, vm.AttachmentsCountText);
        Assert.False(vm.HasUnsavedChanges);

        bool added = vm.AddAttachment(sampleFile, out var error);

        Assert.True(added);
        Assert.Null(error);
        Assert.True(vm.HasAttachments);
        Assert.Equal("(1)", vm.AttachmentsCountText);
        Assert.Single(vm.Attachments);
        Assert.Equal("document.txt", vm.Attachments[0].OriginalFileName);
        Assert.True(vm.Attachments[0].IsAvailable);
        Assert.True(vm.Attachments[0].CanOpen);
        Assert.True(vm.HasUnsavedChanges);
    }

    [Fact]
    public void NoteEditorViewModel_AddDuplicateAttachmentToSameNote_FailsWithClearMessage()
    {
        var (db, conn) = CreateInMemoryDb();
        using var _conn = conn;
        using var _db = db;

        var sampleFile = Path.Combine(_testBaseDir, "image.png");
        File.WriteAllBytes(sampleFile, new byte[] { 1, 2, 3, 4, 5 });

        using var vm = new NoteEditorViewModel(
            _tagDetectionService,
            new List<Tag>(),
            contextFactory: CreateContextFactory(conn),
            attachmentStorageService: _storageService,
            settingsService: _settingsService,
            draftJournalService: NoOpDraftJournalService.Instance);

        bool first = vm.AddAttachment(sampleFile, out var firstErr);
        Assert.True(first);
        Assert.Null(firstErr);

        // Attempt to add the exact same file again
        bool second = vm.AddAttachment(sampleFile, out var secondErr);
        Assert.False(second);
        Assert.NotNull(secondErr);
        Assert.Contains("уже прикреплён к этой заметке", secondErr);

        Assert.Single(vm.Attachments);
    }

    [Fact]
    public void NoteEditorViewModel_AddAttachment_ExceedsSettingLimit_FailsWithClearLimitMessage()
    {
        var (db, conn) = CreateInMemoryDb();
        using var _conn = conn;
        using var _db = db;

        // Set max limit to 1 MB
        var settings = _settingsService.CurrentSettings;
        settings.MaxAttachmentSizeMb = 1;
        _settingsService.SaveSettings(settings);

        // Create a 2 MB file
        var sampleFile = Path.Combine(_testBaseDir, "bigfile.bin");
        using (var fs = File.Create(sampleFile))
        {
            fs.SetLength(2 * 1024 * 1024);
        }

        using var vm = new NoteEditorViewModel(
            _tagDetectionService,
            new List<Tag>(),
            contextFactory: CreateContextFactory(conn),
            attachmentStorageService: _storageService,
            settingsService: _settingsService,
            draftJournalService: NoOpDraftJournalService.Instance);

        bool result = vm.AddAttachment(sampleFile, out var error);

        Assert.False(result);
        Assert.NotNull(error);
        Assert.Contains("превышает установленный лимит", error);
        Assert.Contains("1 МБ", error);
        Assert.Empty(vm.Attachments);
    }

    [Fact]
    public void NoteEditorViewModel_MissingFile_DoesNotCrash_ShowsUnavailableAndDisablesOpen()
    {
        var att = new NoteAttachment
        {
            Id = 10,
            NoteId = 1,
            OriginalFileName = "missing.pdf",
            StoredFileName = "nonexistent.pdf",
            RelativePath = "Attachments\\nonexistent.pdf",
            ContentType = "application/pdf",
            Size = 1024,
            Sha256 = new string('0', 64)
        };

        var fullPath = Path.Combine(_storageService.AttachmentsDirectory, "nonexistent.pdf");
        var itemVm = new NoteAttachmentItemViewModel(att, fullPath);

        Assert.False(itemVm.IsAvailable);
        Assert.False(itemVm.CanOpen);
        Assert.False(itemVm.OpenCommand.CanExecute(null));
        Assert.Equal("Файл недоступен", itemVm.StatusText);
        Assert.True(itemVm.HasStatusText);
        Assert.Contains("Файл недоступен", itemVm.FullFileNameTooltip);
    }

    [Fact]
    public void NoteEditorViewModel_RemoveAttachment_UpdatesCollectionAndMarksDeleted()
    {
        var (db, conn) = CreateInMemoryDb();
        using var _conn = conn;
        using var _db = db;

        var file = Path.Combine(_testBaseDir, "test.txt");
        File.WriteAllText(file, "hello");

        using var vm = new NoteEditorViewModel(
            _tagDetectionService,
            new List<Tag>(),
            contextFactory: CreateContextFactory(conn),
            attachmentStorageService: _storageService,
            settingsService: _settingsService,
            draftJournalService: NoOpDraftJournalService.Instance);

        vm.AddAttachment(file, out _);
        Assert.Single(vm.Attachments);

        var item = vm.Attachments[0];
        vm.RemoveAttachment(item);

        Assert.Empty(vm.Attachments);
        Assert.False(vm.HasAttachments);
        Assert.Equal(string.Empty, vm.AttachmentsCountText);
    }

    [Fact]
    public void NoteEditorViewModel_Cancel_CleansUpUncommittedManagedFiles()
    {
        var (db, conn) = CreateInMemoryDb();
        using var _conn = conn;
        using var _db = db;

        var file = Path.Combine(_testBaseDir, "cancel_test.txt");
        File.WriteAllText(file, "content to cancel");

        using var vm = new NoteEditorViewModel(
            _tagDetectionService,
            new List<Tag>(),
            contextFactory: CreateContextFactory(conn),
            attachmentStorageService: _storageService,
            settingsService: _settingsService,
            draftJournalService: NoOpDraftJournalService.Instance);

        vm.AddAttachment(file, out _);
        var storedName = vm.Attachments[0].StoredFileName;
        var managedFile = Path.Combine(_storageService.AttachmentsDirectory, storedName);
        Assert.True(File.Exists(managedFile));

        // Call CleanupUncommittedAttachments (simulates closing without saving)
        vm.CleanupUncommittedAttachments();

        // Physical uncommitted file should be cleaned up
        Assert.False(File.Exists(managedFile));
    }

    [Fact]
    public void NoteEditorViewModel_ApplyToNote_SynchronizesAttachments()
    {
        var (db, conn) = CreateInMemoryDb();
        using var _conn = conn;
        using var _db = db;

        var file1 = Path.Combine(_testBaseDir, "f1.txt");
        var file2 = Path.Combine(_testBaseDir, "f2.txt");
        File.WriteAllText(file1, "first");
        File.WriteAllText(file2, "second");

        using var vm = new NoteEditorViewModel(
            _tagDetectionService,
            new List<Tag>(),
            contextFactory: CreateContextFactory(conn),
            attachmentStorageService: _storageService,
            settingsService: _settingsService,
            draftJournalService: NoOpDraftJournalService.Instance);

        vm.AddAttachment(file1, out _);
        vm.AddAttachment(file2, out _);

        var note = new Note { Text = "Testing Apply" };
        vm.ApplyToNote(note);

        Assert.Equal(2, note.Attachments.Count);
        Assert.Contains(note.Attachments, a => a.OriginalFileName == "f1.txt");
        Assert.Contains(note.Attachments, a => a.OriginalFileName == "f2.txt");
    }

    [Fact]
    public async Task SettingsViewModel_MaxAttachmentSize_ValidationAndPersistence()
    {
        string? capturedMessage = null;
        string? capturedTitle = null;
        var hotkeyService = new GlobalHotkeyService((_, _, _, _) => true, (_, _) => true, IntPtr.Zero);
        var vm = SettingsViewModelTestComposition.Create(
            _settingsService,
            hotkeyService,
            showMessage: (msg, title, _) =>
            {
                capturedMessage = msg;
                capturedTitle = title;
            });

        Assert.Equal(25, vm.MaxAttachmentSizeMb);

        // Update to valid setting
        vm.MaxAttachmentSizeMb = 50;
        Assert.True(vm.IsMaxAttachmentSizeValid);
        Assert.True(vm.ValidateMaxAttachmentSize(out var validErr));
        Assert.Null(validErr);

        await vm.SaveAsync();

        var reloaded = _settingsService.LoadSettings();
        Assert.Equal(50, reloaded.MaxAttachmentSizeMb);
        Assert.Null(capturedMessage);

        // Test non-positive validation via pure VM method without UI
        vm.MaxAttachmentSizeMb = 0;
        Assert.False(vm.IsMaxAttachmentSizeValid);
        bool isValid = vm.ValidateMaxAttachmentSize(out var error);
        Assert.False(isValid);
        Assert.Equal(SettingsViewModel.InvalidMaxAttachmentSizeErrorMessage, error);

        // Test negative validation
        vm.MaxAttachmentSizeMb = -10;
        Assert.False(vm.IsMaxAttachmentSizeValid);
        Assert.False(vm.ValidateMaxAttachmentSize(out _));

        // When SaveCommand is executed with invalid limit: UI warning is recorded and settings are unchanged
        vm.MaxAttachmentSizeMb = 0;
        await vm.SaveAsync();

        Assert.Equal(SettingsViewModel.InvalidMaxAttachmentSizeErrorMessage, capturedMessage);
        Assert.Equal("Неверный размер вложения", capturedTitle);

        // Settings should remain 50
        var unchanged = _settingsService.LoadSettings();
        Assert.Equal(50, unchanged.MaxAttachmentSizeMb);
    }

    [Fact]
    public void SQLite_NoteAttachment_CascadeDelete_RemovesAttachmentsWhenNoteDeleted()
    {
        var (db, conn) = CreateInMemoryDb();
        using var _conn = conn;
        using var _db = db;

        var note = new Note { Text = "Note with attachments" };
        var att = new NoteAttachment
        {
            OriginalFileName = "photo.png",
            StoredFileName = "abc.png",
            RelativePath = "Attachments\\abc.png",
            ContentType = "image/png",
            Size = 2048,
            Sha256 = new string('f', 64)
        };
        note.Attachments.Add(att);

        db.Notes.Add(note);
        db.SaveChanges();

        Assert.True(att.Id > 0);
        Assert.Equal(note.Id, att.NoteId);

        // Delete note
        db.Notes.Remove(note);
        db.SaveChanges();

        var remaining = db.NoteAttachments.Where(a => a.NoteId == note.Id).ToList();
        Assert.Empty(remaining);
    }

    [Fact]
    public void AttachmentOperations_DoNotBreakNoteHistory_AndDoNotIncludeBinaryData()
    {
        var (db, conn) = CreateInMemoryDb();
        using var _conn = conn;
        using var _db = db;
        var historyService = new NoteHistoryService();

        var note = new Note
        {
            Text = "Original text",
            CreatedAt = DateTime.Now
        };
        var att = new NoteAttachment
        {
            OriginalFileName = "binary.bin",
            StoredFileName = "bin.bin",
            RelativePath = "Attachments\\bin.bin",
            ContentType = "application/octet-stream",
            Size = 100000,
            Sha256 = new string('1', 64)
        };
        note.Attachments.Add(att);
        db.Notes.Add(note);
        db.SaveChanges();

        // Save revision
        var rev = historyService.SaveSnapshot(db, note);
        Assert.NotNull(rev);
        Assert.Equal("Original text", rev.Text);
        // NoteRevision only stores text and TagsJson
        Assert.DoesNotContain("binary.bin", rev.TagsJson);
        Assert.DoesNotContain("bin.bin", rev.TagsJson);

        // Mutate note text and save another revision
        note.Text = "Updated text with attachment still attached";
        db.SaveChanges();
        var rev2 = historyService.SaveSnapshot(db, note);
        Assert.NotNull(rev2);
        Assert.Equal("Updated text with attachment still attached", rev2.Text);

        var revisions = db.NoteRevisions.Where(r => r.NoteId == note.Id).ToList();
        Assert.Equal(2, revisions.Count);
    }
}
