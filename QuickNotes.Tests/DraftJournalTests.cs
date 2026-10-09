using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using QuickNotes.App.Data;
using QuickNotes.App.Helpers;
using QuickNotes.App.Models;
using QuickNotes.App.Models.DraftJournal;
using QuickNotes.App.Services;
using QuickNotes.App.Services.DraftJournal;
using QuickNotes.App.Services.NoteProtection;
using QuickNotes.App.ViewModels;
using Xunit;

namespace QuickNotes.Tests;

[TestCategory(TestCategories.UiSensitive)]
public class DraftJournalTests : IDisposable
{
    private readonly List<string> _tempPaths = new();

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var path in _tempPaths)
        {
            if (File.Exists(path))
            {
                try { File.Delete(path); } catch { }
            }
            if (Directory.Exists(path))
            {
                try { Directory.Delete(path, recursive: true); } catch { }
            }
        }
    }

    private string MakeTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "qn_draft_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _tempPaths.Add(dir);
        return dir;
    }

    private string CreateTempDb()
    {
        string path = Path.Combine(Path.GetTempPath(), $"qn_draft_db_{Guid.NewGuid():N}.db");
        _tempPaths.Add(path);
        return path;
    }

    private static QuickNotesDbContext CreateContext(string dbPath)
    {
        var options = new DbContextOptionsBuilder<QuickNotesDbContext>()
            .UseSqlite($"Data Source={dbPath}")
            .Options;
        return new QuickNotesDbContext(options);
    }

    private static void InitDb(string dbPath)
    {
        using var db = CreateContext(dbPath);
        db.Database.EnsureCreated();
    }

    private static void RunInSta(Action action) => StaTestHarness.Run(action, TimeSpan.FromSeconds(15));

    // ==================================================================
    // 1. Debounce timing and non-blocking persistence
    // ==================================================================

    [Fact]
    public async Task DebounceTiming_PersistsAsynchronouslyWithinOneSecond_WithoutModifyingDatabase()
    {
        string journalDir = MakeTempDir();
        string dbPath = CreateTempDb();
        InitDb(dbPath);

        var service = new DraftJournalService(baseDirectory: journalDir);
        var tagDetection = new TagDetectionService();
        var allTags = new List<Tag>();

        NoteEditorViewModel? vm = null;
        RunInSta(() =>
        {
            vm = new NoteEditorViewModel(
                tagDetection,
                allTags,
                existingNote: null,
                initialTitle: "Начальный заголовок",
                initialText: "Начальный текст",
                draftJournalService: service);

            vm.DebounceDelay = TimeSpan.FromMilliseconds(150);

            // Simulate typing
            vm.Title = "Черновик заголовка";
            vm.Text = "Черновик текста из редактора";
        });

        Assert.NotNull(vm);

        // Verify that typing did NOT write anything to the database
        using (var db = CreateContext(dbPath))
        {
            Assert.Empty(db.Notes.ToList());
            Assert.Empty(db.NoteRevisions.ToList());
        }

        // Wait for debounce timer to fire (150ms delay + buffer)
        await Task.Delay(400);

        // Verify that the journal file was written to disk
        Assert.True(service.HasJournal(vm.DraftId), "Журнал черновика должен существовать на диске");

        var read = service.ReadJournal(vm.DraftId);
        Assert.Equal(DraftJournalReadStatus.Success, read.Status);
        Assert.Equal("Черновик заголовка", read.DecryptedTitle);
        Assert.Equal("Черновик текста из редактора", read.DecryptedText);

        // Database remains completely untouched
        using (var db = CreateContext(dbPath))
        {
            Assert.Empty(db.Notes.ToList());
            Assert.Empty(db.NoteRevisions.ToList());
        }

        vm.Dispose();
    }

    // ==================================================================
    // 2. Crash recovery and explicit discard
    // ==================================================================

    [Fact]
    public async Task CrashRecovery_DetectsUncommittedJournal_SideBySideComparison_RestoresDraft()
    {
        string journalDir = MakeTempDir();
        string dbPath = CreateTempDb();
        InitDb(dbPath);

        int noteId;
        using (var db = CreateContext(dbPath))
        {
            var note = new Note
            {
                Title = "Сохранённый заголовок",
                Text = "Сохранённый текст заметки в базе данных",
                CreatedAt = DateTime.Now.AddDays(-1),
                UpdatedAt = DateTime.Now.AddDays(-1)
            };
            db.Notes.Add(note);
            db.SaveChanges();
            noteId = note.Id;
        }

        var service = new DraftJournalService(baseDirectory: journalDir);
        string draftId = service.GetDraftIdForNote(noteId);

        // Simulate crash: an uncommitted journal was saved to disk before process died
        var crashSnapshot = new DraftJournalSnapshot
        {
            DraftId = draftId,
            NoteId = noteId,
            SequenceNumber = 10,
            Title = "Заголовок из упавшего сеанса",
            Text = "Текст, набранный перед падением программы",
            IsProtected = false
        };
        var saveResult = await service.SaveJournalAsync(crashSnapshot);
        Assert.True(saveResult.Success);

        RunInSta(() =>
        {
            using var db = CreateContext(dbPath);
            var committedNote = db.Notes.Find(noteId)!;

            var vm = new NoteEditorViewModel(
                new TagDetectionService(),
                new List<Tag>(),
                existingNote: committedNote,
                draftJournalService: service);

            // Detect uncommitted recovery without showing modal UI
            DraftRecoveryPromptArgs? capturedArgs = null;
            vm.RequestDraftRecoveryPrompt = args =>
            {
                capturedArgs = args;
                return DraftRecoveryChoice.Restore;
            };

            bool detected = vm.CheckAndPromptDraftRecovery();
            Assert.True(detected);
            Assert.NotNull(capturedArgs);

            // Validate comparison information
            Assert.Equal("Сохранённый заголовок", capturedArgs.CommittedTitle);
            Assert.Equal("Сохранённый текст заметки в базе данных", capturedArgs.CommittedText);
            Assert.Equal("Заголовок из упавшего сеанса", capturedArgs.DraftTitle);
            Assert.Equal("Текст, набранный перед падением программы", capturedArgs.DraftText);
            Assert.Contains("Заголовок", capturedArgs.DiffSummary);
            Assert.Contains("Текст", capturedArgs.DiffSummary);

            // After restoration, VM properties must reflect the draft
            Assert.Equal("Заголовок из упавшего сеанса", vm.Title);
            Assert.Equal("Текст, набранный перед падением программы", vm.Text);

            vm.Dispose();
        });
    }

    [Fact]
    public async Task CrashRecovery_ExplicitDiscard_DeletesJournal_PreservesCommittedData()
    {
        string journalDir = MakeTempDir();
        string dbPath = CreateTempDb();
        InitDb(dbPath);

        int noteId;
        using (var db = CreateContext(dbPath))
        {
            var note = new Note
            {
                Title = "Каноничный заголовок",
                Text = "Каноничный текст заметки",
                CreatedAt = DateTime.Now.AddHours(-2),
                UpdatedAt = DateTime.Now.AddHours(-2)
            };
            db.Notes.Add(note);
            db.SaveChanges();
            noteId = note.Id;
        }

        var service = new DraftJournalService(baseDirectory: journalDir);
        string draftId = service.GetDraftIdForNote(noteId);

        // Pre-create an uncommitted journal
        var preSave = await service.SaveJournalAsync(new DraftJournalSnapshot
        {
            DraftId = draftId,
            NoteId = noteId,
            SequenceNumber = 4,
            Title = "Мусорный заголовок",
            Text = "Мусорный текст",
            IsProtected = false
        });
        Assert.True(preSave.Success);

        Assert.True(service.HasJournal(draftId));

        RunInSta(() =>
        {
            using var db = CreateContext(dbPath);
            var committedNote = db.Notes.Find(noteId)!;

            var vm = new NoteEditorViewModel(
                new TagDetectionService(),
                new List<Tag>(),
                existingNote: committedNote,
                draftJournalService: service);

            vm.RequestDraftRecoveryPrompt = args => DraftRecoveryChoice.Discard;

            bool restored = vm.CheckAndPromptDraftRecovery();
            Assert.False(restored);

            // VM retains committed content
            Assert.Equal("Каноничный заголовок", vm.Title);
            Assert.Equal("Каноничный текст заметки", vm.Text);

            // Journal must be deleted from disk
            Assert.False(service.HasJournal(draftId));

            vm.Dispose();
        });

        // Committed note in DB remains intact
        using (var db = CreateContext(dbPath))
        {
            var note = db.Notes.Find(noteId)!;
            Assert.Equal("Каноничный заголовок", note.Title);
            Assert.Equal("Каноничный текст заметки", note.Text);
        }
    }

    [Fact]
    public async Task DiscardDraftCommand_Confirm_DeletesJournal_AfterDecision()
    {
        string journalDir = MakeTempDir();
        string dbPath = CreateTempDb();
        InitDb(dbPath);

        int noteId;
        using (var db = CreateContext(dbPath))
        {
            var note = new Note
            {
                Title = "Каноничный заголовок",
                Text = "Каноничный текст заметки",
                CreatedAt = DateTime.Now.AddHours(-2),
                UpdatedAt = DateTime.Now.AddHours(-2)
            };
            db.Notes.Add(note);
            db.SaveChanges();
            noteId = note.Id;
        }

        var service = new DraftJournalService(baseDirectory: journalDir);
        string draftId = service.GetDraftIdForNote(noteId);
        var preSave = await service.SaveJournalAsync(new DraftJournalSnapshot
        {
            DraftId = draftId,
            NoteId = noteId,
            SequenceNumber = 4,
            Title = "Мусорный заголовок",
            Text = "Мусорный текст",
            IsProtected = false
        });
        Assert.True(preSave.Success);
        Assert.True(service.HasJournal(draftId));

        RunInSta(() =>
        {
            using var db = CreateContext(dbPath);
            var committedNote = db.Notes.Find(noteId)!;
            var vm = new NoteEditorViewModel(
                new TagDetectionService(),
                new List<Tag>(),
                existingNote: committedNote,
                draftJournalService: service);

            int promptCount = 0;
            vm.RequestDraftRecoveryPrompt = _ => DraftRecoveryChoice.KeepCommitted;
            vm.RequestDraftDiscardDecision = () =>
            {
                promptCount++;
                return true;
            };

            Assert.False(vm.CheckAndPromptDraftRecovery());
            Assert.True(vm.HasUncommittedDraftRecovery);
            Assert.True(service.HasJournal(draftId));

            vm.DiscardDraftCommand.Execute(null);

            Assert.Equal(1, promptCount);
            Assert.False(vm.HasUncommittedDraftRecovery);
            Assert.False(service.HasJournal(draftId));
            Assert.Equal("Каноничный заголовок", vm.Title);
            Assert.Equal("Каноничный текст заметки", vm.Text);
            vm.Dispose();
        });
    }

    [Fact]
    public async Task DiscardDraftCommand_Cancel_KeepsJournal_AndDoesNotDelete()
    {
        string journalDir = MakeTempDir();
        string dbPath = CreateTempDb();
        InitDb(dbPath);

        int noteId;
        using (var db = CreateContext(dbPath))
        {
            var note = new Note
            {
                Title = "Каноничный заголовок",
                Text = "Каноничный текст заметки",
                CreatedAt = DateTime.Now.AddHours(-2),
                UpdatedAt = DateTime.Now.AddHours(-2)
            };
            db.Notes.Add(note);
            db.SaveChanges();
            noteId = note.Id;
        }

        var service = new DraftJournalService(baseDirectory: journalDir);
        string draftId = service.GetDraftIdForNote(noteId);
        var preSave = await service.SaveJournalAsync(new DraftJournalSnapshot
        {
            DraftId = draftId,
            NoteId = noteId,
            SequenceNumber = 4,
            Title = "Мусорный заголовок",
            Text = "Мусорный текст",
            IsProtected = false
        });
        Assert.True(preSave.Success);
        Assert.True(service.HasJournal(draftId));

        RunInSta(() =>
        {
            using var db = CreateContext(dbPath);
            var committedNote = db.Notes.Find(noteId)!;
            var vm = new NoteEditorViewModel(
                new TagDetectionService(),
                new List<Tag>(),
                existingNote: committedNote,
                draftJournalService: service);

            int promptCount = 0;
            vm.RequestDraftRecoveryPrompt = _ => DraftRecoveryChoice.KeepCommitted;
            vm.RequestDraftDiscardDecision = () =>
            {
                promptCount++;
                return false;
            };

            Assert.False(vm.CheckAndPromptDraftRecovery());
            Assert.True(vm.HasUncommittedDraftRecovery);

            vm.DiscardDraftCommand.Execute(null);

            Assert.Equal(1, promptCount);
            Assert.True(vm.HasUncommittedDraftRecovery);
            Assert.True(service.HasJournal(draftId));
            Assert.Equal("Каноничный заголовок", vm.Title);
            Assert.Equal("Каноничный текст заметки", vm.Text);
            vm.Dispose();
        });
    }

    // ==================================================================
    // 3. Cleanup after successful commit and preservation after failure
    // ==================================================================

    [Fact]
    public async Task Commit_SuccessDeletesMatchingJournal_FailurePreservesJournal()
    {
        string journalDir = MakeTempDir();
        string dbPath = CreateTempDb();
        InitDb(dbPath);

        var service = new DraftJournalService(baseDirectory: journalDir);

        // Case A: Commit throws exception -> journal MUST be preserved
        string failDraftId = "fail-draft-test";
        await service.SaveJournalAsync(new DraftJournalSnapshot
        {
            DraftId = failDraftId,
            Title = "Заголовок для сбоя",
            Text = "Текст для сбоя"
        });
        Assert.True(service.HasJournal(failDraftId));

        RunInSta(() =>
        {
            var vm = new NoteEditorViewModel(
                new TagDetectionService(),
                new List<Tag>(),
                existingNote: null,
                initialTitle: "Заголовок для сбоя",
                initialText: "Текст для сбоя",
                draftJournalService: service,
                initialDraftId: failDraftId);

            // Commit delegate simulates database error
            vm.AlertHandler = (_, _, _) => { };
            vm.Commit = () => throw new InvalidOperationException("Имитация сбоя транзакции БД");

            var commitAndCloseMethod = typeof(NoteEditorViewModel).GetMethod(
                "CommitAndClose",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            commitAndCloseMethod?.Invoke(vm, null);

            // The journal must still exist because commit failed!
            Assert.True(service.HasJournal(vm.DraftId), "Журнал должен сохраниться при ошибке сохранения в БД");

            vm.Dispose();
        });
        Assert.True(service.HasJournal(failDraftId));

        // Case B: Commit succeeds -> journal MUST be deleted
        string successDraftId = "success-draft-test";
        await service.SaveJournalAsync(new DraftJournalSnapshot
        {
            DraftId = successDraftId,
            Title = "Успешный заголовок",
            Text = "Успешный текст"
        });
        Assert.True(service.HasJournal(successDraftId));

        RunInSta(() =>
        {
            var vm = new NoteEditorViewModel(
                new TagDetectionService(),
                new List<Tag>(),
                existingNote: null,
                initialTitle: "Успешный заголовок",
                initialText: "Успешный текст",
                draftJournalService: service,
                initialDraftId: successDraftId);

            bool commitRan = false;
            vm.Commit = () =>
            {
                commitRan = true;
                // successful commit callback
            };

            var commitAndCloseMethod = typeof(NoteEditorViewModel).GetMethod(
                "CommitAndClose",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            commitAndCloseMethod?.Invoke(vm, null);

            Assert.True(commitRan);
            Assert.False(service.HasJournal(vm.DraftId), "Журнал должен удаляться после успешного коммита");

            vm.Dispose();
        });
        Assert.False(service.HasJournal(successDraftId));
    }

    // ==================================================================
    // 4. Stale callback and race protection
    // ==================================================================

    [Fact]
    public async Task StaleCallbackProtection_MonotonicSequence_DropsOlderWrites()
    {
        string journalDir = MakeTempDir();
        var service = new DraftJournalService(baseDirectory: journalDir);
        string draftId = "test-sequence-race";

        var snapSeq5 = new DraftJournalSnapshot
        {
            DraftId = draftId,
            SequenceNumber = 5,
            Title = "Версия 5",
            Text = "Текст версии 5"
        };
        var res5 = await service.SaveJournalAsync(snapSeq5);
        Assert.True(res5.Success);

        // Stale write with sequence 3 arrives late
        var snapSeq3 = new DraftJournalSnapshot
        {
            DraftId = draftId,
            SequenceNumber = 3,
            Title = "Устаревшая версия 3",
            Text = "Устаревший текст 3"
        };
        var res3 = await service.SaveJournalAsync(snapSeq3);
        Assert.True(res3.IsSuperseded);

        // Verify disk still has sequence 5
        var read = service.ReadJournal(draftId);
        Assert.Equal(DraftJournalReadStatus.Success, read.Status);
        Assert.Equal("Версия 5", read.DecryptedTitle);
        Assert.Equal("Текст версии 5", read.DecryptedText);
        Assert.Equal(5, read.Envelope!.SequenceNumber);
    }

    [Fact]
    public async Task ConcurrentWrites_AreSerializedSafely_WithoutCorruptingJournal()
    {
        string journalDir = MakeTempDir();
        var service = new DraftJournalService(baseDirectory: journalDir);
        string draftId = "concurrent-draft-test";

        var tasks = new List<Task<DraftSaveResult>>();
        for (int i = 1; i <= 20; i++)
        {
            int seq = i;
            tasks.Add(Task.Run(() => service.SaveJournalAsync(new DraftJournalSnapshot
            {
                DraftId = draftId,
                SequenceNumber = seq,
                Title = $"Заголовок #{seq}",
                Text = $"Текст заметки #{seq}"
            })));
        }

        var results = await Task.WhenAll(tasks);
        Assert.All(results, r => Assert.True(r.Success || r.IsSuperseded));

        // Disk must contain a valid uncorrupted journal with the highest persisted sequence
        var read = service.ReadJournal(draftId);
        Assert.Equal(DraftJournalReadStatus.Success, read.Status);
        Assert.NotNull(read.Envelope);
        Assert.True(read.Envelope.SequenceNumber >= 1);
        Assert.Equal($"Заголовок #{read.Envelope.SequenceNumber}", read.DecryptedTitle);
    }

    // ==================================================================
    // 5. Corrupt / stale journal handling
    // ==================================================================

    [Fact]
    public void CorruptJournal_ZeroByteFile_HandledSafelyWithoutCrash()
    {
        string journalDir = MakeTempDir();
        var service = new DraftJournalService(baseDirectory: journalDir);
        string draftId = "zero-byte-draft";

        string filePath = Path.Combine(journalDir, "DraftJournals", $"{draftId}.journal");
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        File.WriteAllBytes(filePath, Array.Empty<byte>());

        var read = service.ReadJournal(draftId);
        Assert.Equal(DraftJournalReadStatus.Corrupt, read.Status);
        Assert.Contains("пуст", read.ErrorMessage);
    }

    [Fact]
    public void CorruptJournal_MalformedJson_HandledSafelyWithoutCrash()
    {
        string journalDir = MakeTempDir();
        var service = new DraftJournalService(baseDirectory: journalDir);
        string draftId = "malformed-json-draft";

        string filePath = Path.Combine(journalDir, "DraftJournals", $"{draftId}.journal");
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        File.WriteAllText(filePath, "{ this is not valid json at all ...", Encoding.UTF8);

        var read = service.ReadJournal(draftId);
        Assert.Equal(DraftJournalReadStatus.Corrupt, read.Status);
        Assert.Contains("Повреждённый", read.ErrorMessage);
        Assert.DoesNotContain(journalDir, read.ErrorMessage);
    }

    [Fact]
    public void CorruptJournal_UnsupportedVersion_ReturnsCorruptWithVersionDetail()
    {
        string journalDir = MakeTempDir();
        var service = new DraftJournalService(baseDirectory: journalDir);
        string draftId = "unsupported-version-draft";

        string filePath = Path.Combine(journalDir, "DraftJournals", $"{draftId}.journal");
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        string json = "{\"Version\": 999, \"DraftId\": \"unsupported-version-draft\"}";
        File.WriteAllText(filePath, json, Encoding.UTF8);

        var read = service.ReadJournal(draftId);
        Assert.Equal(DraftJournalReadStatus.UnsupportedVersion, read.Status);
        Assert.Contains("999", read.ErrorMessage);
    }

    [Fact]
    public void UnsafeIdentifiers_PathTraversal_RejectedSafely()
    {
        string journalDir = MakeTempDir();
        var service = new DraftJournalService(baseDirectory: journalDir);

        Assert.False(DraftJournalService.IsSafeIdentifier("../secret"));
        Assert.False(DraftJournalService.IsSafeIdentifier("..\\windows"));
        Assert.False(DraftJournalService.IsSafeIdentifier("folder/sub"));
        Assert.False(DraftJournalService.IsSafeIdentifier("c:test"));
        Assert.False(DraftJournalService.IsSafeIdentifier(null));
        Assert.False(DraftJournalService.IsSafeIdentifier(""));
        Assert.False(DraftJournalService.IsSafeIdentifier(new string('a', 200)));

        Assert.True(DraftJournalService.IsSafeIdentifier("new-12345abcdef"));
        Assert.True(DraftJournalService.IsSafeIdentifier("note-42"));
        Assert.True(DraftJournalService.IsSafeIdentifier("draft_xyz-1"));

        var read = service.ReadJournal("../../secret");
        Assert.Equal(DraftJournalReadStatus.Corrupt, read.Status);
        Assert.False(service.DeleteJournal("../../secret"));
    }

    // ==================================================================
    // 6. Unique identity for new notes & no debris on cancel
    // ==================================================================

    [Fact]
    public void UniqueIdentity_MultipleNewNotes_HaveDistinctDraftIds_AndNoDebrisOnCancel()
    {
        string journalDir = MakeTempDir();
        var service = new DraftJournalService(baseDirectory: journalDir);

        string id1 = service.GenerateNewDraftId();
        string id2 = service.GenerateNewDraftId();

        Assert.NotEqual(id1, id2);
        Assert.StartsWith("new-", id1);
        Assert.StartsWith("new-", id2);

        RunInSta(() =>
        {
            // Create empty new note editor
            var vm = new NoteEditorViewModel(
                new TagDetectionService(),
                new List<Tag>(),
                existingNote: null,
                draftJournalService: service);

            string assignedDraftId = vm.DraftId;
            Assert.StartsWith("new-", assignedDraftId);

            // Cancelling an empty new note must not leave debris
            var cancelMethod = typeof(NoteEditorViewModel).GetMethod(
                "Cancel",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            cancelMethod?.Invoke(vm, null);

            Assert.False(service.HasJournal(assignedDraftId), "Отмена пустого черновика не должна оставлять файлов");

            vm.Dispose();
        });
    }

    // ==================================================================
    // 7. Protected journal contains no plaintext and decrypts only in session
    // ==================================================================

    [Fact]
    public async Task ProtectedJournal_ZeroPlaintextOnDisk_DecryptsOnlyInUnlockedSession()
    {
        string journalDir = MakeTempDir();
        string dbPath = CreateTempDb();
        InitDb(dbPath);

        int noteId;
        using (var db = CreateContext(dbPath))
        {
            var note = new Note
            {
                Title = "Засекречено",
                Text = "Секретный текст заметки",
                CreatedAt = DateTime.Now,
                UpdatedAt = DateTime.Now
            };
            db.Notes.Add(note);
            db.SaveChanges();
            noteId = note.Id;
        }

        var storage = new AttachmentStorageService(MakeTempDir());
        var protectionService = new NoteProtectionService(attachmentStorage: storage);

        // Protect note
        using (var db = CreateContext(dbPath))
        {
            var pRes = protectionService.ProtectNote(db, noteId, "strong-password-123");
            Assert.True(pRes.Success);
        }

        // Note is currently unlocked in session
        Assert.True(protectionService.IsUnlocked(noteId));

        var service = new DraftJournalService(
            baseDirectory: journalDir,
            noteProtectionService: protectionService);

        string draftId = service.GetDraftIdForNote(noteId);
        string secretDraftTitle = "Совершенно секретный заголовок черновика";
        string secretDraftText = "Суперсекретное содержимое черновика, которое никогда не должно появиться открытым текстом!";

        var snapshot = new DraftJournalSnapshot
        {
            DraftId = draftId,
            NoteId = noteId,
            SequenceNumber = 1,
            Title = secretDraftTitle,
            Text = secretDraftText,
            IsProtected = true
        };

        var saveRes = await service.SaveJournalAsync(snapshot);
        Assert.True(saveRes.Success);

        // 1. INVARIANT CHECK: Verify raw journal file on disk contains NO plaintext
        string journalFilePath = Path.Combine(journalDir, "DraftJournals", $"{draftId}.journal");
        Assert.True(File.Exists(journalFilePath));

        string rawDiskContent = File.ReadAllText(journalFilePath, Encoding.UTF8);
        Assert.DoesNotContain(secretDraftTitle, rawDiskContent);
        Assert.DoesNotContain(secretDraftText, rawDiskContent);
        Assert.DoesNotContain("Совершенно", rawDiskContent);
        Assert.DoesNotContain("Суперсекретное", rawDiskContent);

        // The envelope title and text must be null/empty
        Assert.Contains("\"IsProtected\": true", rawDiskContent);
        Assert.Contains("\"Crypto\":", rawDiskContent);

        // 2. Read while unlocked -> Decrypts successfully
        var readUnlocked = service.ReadJournal(draftId);
        Assert.Equal(DraftJournalReadStatus.Success, readUnlocked.Status);
        Assert.Equal(secretDraftTitle, readUnlocked.DecryptedTitle);
        Assert.Equal(secretDraftText, readUnlocked.DecryptedText);

        // 3. Lock note / wipe session
        protectionService.LockNote(noteId);
        Assert.False(protectionService.IsUnlocked(noteId));

        // 4. Read while locked -> Returns ProtectedSessionRequired and NO plaintext
        var readLocked = service.ReadJournal(draftId);
        Assert.Equal(DraftJournalReadStatus.ProtectedSessionRequired, readLocked.Status);
        Assert.Null(readLocked.DecryptedTitle);
        Assert.Null(readLocked.DecryptedText);

        // 5. Try saving a protected draft while locked -> CryptoUnavailable, writes NO plaintext
        var lockedSaveSnapshot = new DraftJournalSnapshot
        {
            DraftId = draftId,
            NoteId = noteId,
            SequenceNumber = 2,
            Title = "Ещё один секрет",
            Text = "Текст при заблокированной сессии",
            IsProtected = true
        };
        var lockedSaveResult = await service.SaveJournalAsync(lockedSaveSnapshot);
        Assert.False(lockedSaveResult.Success);
        Assert.True(lockedSaveResult.IsCryptoUnavailable);

        // Verify again that disk did NOT get the new text
        string rawAfterLockedAttempt = File.ReadAllText(journalFilePath, Encoding.UTF8);
        Assert.DoesNotContain("Ещё один секрет", rawAfterLockedAttempt);
        Assert.DoesNotContain("Текст при заблокированной сессии", rawAfterLockedAttempt);
    }

    // ==================================================================
    // 8. DraftDiffHelper unit tests
    // ==================================================================

    [Fact]
    public void DraftDiffHelper_IdenticalContent_ReportsNoDifference()
    {
        string summary = DraftDiffHelper.GenerateSummary("Заголовок", "Текст заметки", "Заголовок", "Текст заметки");
        Assert.Contains("без изменений", summary);
    }

    [Fact]
    public void DraftDiffHelper_ModifiedTitleAndText_ReportsDifferences()
    {
        string summary = DraftDiffHelper.GenerateSummary(
            "Старый заголовок",
            "Строка 1\nСтрока 2",
            "Новый заголовок",
            "Строка 1\nСтрока 2 изменена\nСтрока 3 добавлена");

        Assert.Contains("Заголовок:", summary);
        Assert.Contains("→", summary);
        Assert.Contains("Текст:", summary);
        Assert.Contains("строк", summary);
        Assert.Contains("симв.", summary);
    }

    // ==================================================================
    // 9. MainViewModel New Note Journal Recovery & Startup Flow
    // ==================================================================

    [Fact]
    public async Task MainViewModel_DiscoversUncommittedNewNoteJournals()
    {
        string journalDir = MakeTempDir();
        string dbPath = CreateTempDb();
        InitDb(dbPath);

        var service = new DraftJournalService(baseDirectory: journalDir);

        // Create an uncommitted new note draft
        string newDraftId = service.GenerateNewDraftId();
        await service.SaveJournalAsync(new DraftJournalSnapshot
        {
            DraftId = newDraftId,
            NoteId = null,
            SequenceNumber = 1,
            Title = "Новая несохранённая заметка",
            Text = "Важный текст новой заметки, который не должен пропасть"
        });

        RunInSta(() =>
        {
            var hotkeyService = new GlobalHotkeyService((_, _, _, _) => true, (_, _) => true, IntPtr.Zero);
            using var trayIcon = new TrayIconService();
            var mainVm = MainViewModelTestComposition.Create(
                () => CreateContext(dbPath),
                new TagDetectionService(),
                new SearchService(),
                new SettingsService(),
                hotkeyService,
                new ClipboardCaptureService(),
                trayIcon,
                new BackupService(),
                draftJournalService: service);

            var uncommitted = mainVm.GetUncommittedNewNoteJournals();
            Assert.Single(uncommitted);
            Assert.Equal(newDraftId, uncommitted[0].Envelope!.DraftId);
            Assert.Equal("Новая несохранённая заметка", uncommitted[0].DecryptedTitle);
            Assert.Equal("Важный текст новой заметки, который не должен пропасть", uncommitted[0].DecryptedText);

            mainVm.Dispose();
            hotkeyService.Dispose();
        });
    }

    [Fact]
    public async Task MainViewModel_LoadNoteIntoDetail_DetectsDraftJournal_WithoutModal()
    {
        string journalDir = MakeTempDir();
        string dbPath = CreateTempDb();
        InitDb(dbPath);

        int noteId;
        using (var db = CreateContext(dbPath))
        {
            var note = new Note
            {
                Title = "Оригинальный заголовок",
                Text = "Оригинальный текст в базе",
                CreatedAt = DateTime.Now.AddDays(-1),
                UpdatedAt = DateTime.Now.AddDays(-1)
            };
            db.Notes.Add(note);
            db.SaveChanges();
            noteId = note.Id;
        }

        var service = new DraftJournalService(baseDirectory: journalDir);
        string draftId = service.GetDraftIdForNote(noteId);

        await service.SaveJournalAsync(new DraftJournalSnapshot
        {
            DraftId = draftId,
            NoteId = noteId,
            SequenceNumber = 1,
            Title = "Отличающийся заголовок",
            Text = "Отличающийся текст в журнале"
        });

        RunInSta(() =>
        {
            var hotkeyService = new GlobalHotkeyService((_, _, _, _) => true, (_, _) => true, IntPtr.Zero);
            using var trayIcon = new TrayIconService();
            var mainVm = MainViewModelTestComposition.Create(
                () => CreateContext(dbPath),
                new TagDetectionService(),
                new SearchService(),
                new SettingsService(),
                hotkeyService,
                new ClipboardCaptureService(),
                trayIcon,
                new BackupService(),
                draftJournalService: service);

            mainVm.ReloadAll();
            var card = mainVm.Notes.First(n => n.Id == noteId);

            bool modalCalled = false;
            mainVm.RequestUnsavedEditorDecision = () =>
            {
                modalCalled = true;
                return UnsavedEditorDecision.Discard;
            };
            mainVm.RequestDraftDiscardDecision = () => true;

            mainVm.LoadNoteIntoDetail(card);

            Assert.NotNull(mainVm.DetailEditor);
            Assert.True(mainVm.DetailEditor!.HasUncommittedDraftRecovery);
            Assert.False(modalCalled);

            // Cleanup
            service.DeleteJournal(draftId);
            mainVm.Dispose();
            hotkeyService.Dispose();
        });
    }

    [Fact]
    public async Task StartupFlow_DiscoversUncommittedNewNoteJournals_RestoresOrDiscardsWithoutOverwritingCommittedData()
    {
        string journalDir = MakeTempDir();
        string dbPath = CreateTempDb();
        InitDb(dbPath);

        // Committed note in DB
        int committedNoteId;
        using (var db = CreateContext(dbPath))
        {
            var note = new Note
            {
                Title = "Каноничная заметка",
                Text = "Каноничный текст в базе",
                CreatedAt = DateTime.Now.AddDays(-1),
                UpdatedAt = DateTime.Now.AddDays(-1)
            };
            db.Notes.Add(note);
            db.SaveChanges();
            committedNoteId = note.Id;
        }

        var service = new DraftJournalService(baseDirectory: journalDir);
        string newDraftId = service.GenerateNewDraftId();

        await service.SaveJournalAsync(new DraftJournalSnapshot
        {
            DraftId = newDraftId,
            NoteId = null,
            SequenceNumber = 1,
            Title = "Несохранённая идея",
            Text = "Очень ценный черновик новой заметки"
        });

        Assert.True(service.HasJournal(newDraftId));

        RunInSta(() =>
        {
            var hotkeyService = new GlobalHotkeyService((_, _, _, _) => true, (_, _) => true, IntPtr.Zero);
            using var trayIcon = new TrayIconService();
            var mainVm = MainViewModelTestComposition.Create(
                () => CreateContext(dbPath),
                new TagDetectionService(),
                new SearchService(),
                new SettingsService(),
                hotkeyService,
                new ClipboardCaptureService(),
                trayIcon,
                new BackupService(),
                draftJournalService: service);

            // 1. Test Discard flow
            DraftRecoveryPromptArgs? capturedArgs = null;
            mainVm.RequestNewNoteDraftRecoveryPrompt = args =>
            {
                capturedArgs = args;
                return DraftRecoveryChoice.Discard;
            };

            mainVm.CheckAndPromptNewNoteRecoveries();

            Assert.NotNull(capturedArgs);
            Assert.Equal(newDraftId, capturedArgs.DraftId);
            Assert.Null(capturedArgs.NoteId);
            Assert.Equal("Несохранённая идея", capturedArgs.DraftTitle);
            Assert.Equal("Очень ценный черновик новой заметки", capturedArgs.DraftText);

            // Journal was discarded
            Assert.False(service.HasJournal(newDraftId));

            // Committed note in DB was completely untouched
            using (var db = CreateContext(dbPath))
            {
                var note = db.Notes.Find(committedNoteId);
                Assert.NotNull(note);
                Assert.Equal("Каноничная заметка", note.Title);
                Assert.Equal("Каноничный текст в базе", note.Text);
            }

            mainVm.Dispose();
            hotkeyService.Dispose();
        });

        // 2. Test Restore flow
        string newDraftId2 = service.GenerateNewDraftId();
        await service.SaveJournalAsync(new DraftJournalSnapshot
        {
            DraftId = newDraftId2,
            NoteId = null,
            SequenceNumber = 1,
            Title = "Восстанавливаемая идея",
            Text = "Текст, который будет восстановлен в редактор"
        });

        RunInSta(() =>
        {
            var hotkeyService = new GlobalHotkeyService((_, _, _, _) => true, (_, _) => true, IntPtr.Zero);
            using var trayIcon = new TrayIconService();
            var mainVm = MainViewModelTestComposition.Create(
                () => CreateContext(dbPath),
                new TagDetectionService(),
                new SearchService(),
                new SettingsService(),
                hotkeyService,
                new ClipboardCaptureService(),
                trayIcon,
                new BackupService(),
                draftJournalService: service);

            bool editorOpened = false;
            mainVm.RequestOpenNoteEditor += vm =>
            {
                editorOpened = true;
                Assert.Equal("Восстанавливаемая идея", vm.Title);
                Assert.Equal("Текст, который будет восстановлен в редактор", vm.Text);
                Assert.Equal(newDraftId2, vm.DraftId);
                return false;
            };

            mainVm.RequestNewNoteDraftRecoveryPrompt = args => DraftRecoveryChoice.Restore;
            mainVm.CheckAndPromptNewNoteRecoveries();

            Assert.True(editorOpened);

            mainVm.Dispose();
            hotkeyService.Dispose();
        });
    }

    // ==================================================================
    // 10. Protected journal leaks no metadata or attachments in plaintext
    // ==================================================================

    [Fact]
    public async Task ProtectedJournal_ZeroMetadataOrPlaintextLeak_UniqueSecretsTest()
    {
        string journalDir = MakeTempDir();
        string dbPath = CreateTempDb();
        InitDb(dbPath);

        int noteId;
        using (var db = CreateContext(dbPath))
        {
            var note = new Note
            {
                Title = "Заметка с секретами",
                Text = "Оригинальный текст",
                CreatedAt = DateTime.Now,
                UpdatedAt = DateTime.Now
            };
            db.Notes.Add(note);
            db.SaveChanges();
            noteId = note.Id;
        }

        var storage = new AttachmentStorageService(MakeTempDir());
        var protectionService = new NoteProtectionService(attachmentStorage: storage);

        using (var db = CreateContext(dbPath))
        {
            var pRes = protectionService.ProtectNote(db, noteId, "SuperSecretPassword123!");
            Assert.True(pRes.Success);
        }

        var service = new DraftJournalService(
            baseDirectory: journalDir,
            noteProtectionService: protectionService);

        string draftId = service.GetDraftIdForNote(noteId);

        // Every field has a unique secret string
        string secretTitle = "SEC_TITLE_" + Guid.NewGuid().ToString("N");
        string secretText = "SEC_TEXT_" + Guid.NewGuid().ToString("N");
        string secretProc = "SEC_PROC_" + Guid.NewGuid().ToString("N");
        string secretWin = "SEC_WIN_" + Guid.NewGuid().ToString("N");
        string secretUrl = "https://secret.company.internal/" + Guid.NewGuid().ToString("N");
        string secretTag = "SEC_TAG_" + Guid.NewGuid().ToString("N");
        string secretAttOrig = "SEC_ATT_ORIG_" + Guid.NewGuid().ToString("N") + ".pdf";
        string secretAttStored = "SEC_ATT_STORED_" + Guid.NewGuid().ToString("N") + ".bin";
        string secretAttRel = "secret_sub/" + Guid.NewGuid().ToString("N") + ".bin";
        string secretAttType = "application/x-secret-" + Guid.NewGuid().ToString("N");
        string secretAttSha = "SEC_SHA_" + Guid.NewGuid().ToString("N");

        var snapshot = new DraftJournalSnapshot
        {
            DraftId = draftId,
            NoteId = noteId,
            SequenceNumber = 1,
            Title = secretTitle,
            Text = secretText,
            SourceProcessName = secretProc,
            SourceWindowTitle = secretWin,
            SourceUrl = secretUrl,
            CapturedAt = new DateTime(2026, 9, 11, 10, 30, 0, DateTimeKind.Utc),
            IsProtected = true,
            Tags = new List<DraftJournalTagDto>
            {
                new() { TagId = 101, TagName = secretTag, Origin = TagOrigin.Manual, IsSuppressed = false }
            },
            Attachments = new List<DraftJournalAttachmentDto>
            {
                new()
                {
                    Id = 202,
                    OriginalFileName = secretAttOrig,
                    StoredFileName = secretAttStored,
                    RelativePath = secretAttRel,
                    ContentType = secretAttType,
                    Size = 12345,
                    Sha256 = secretAttSha,
                    IsNew = true
                }
            }
        };

        var saveResult = await service.SaveJournalAsync(snapshot);
        Assert.True(saveResult.Success);

        // Read raw .journal file directly from disk
        string journalFilePath = Path.Combine(journalDir, "DraftJournals", $"{draftId}.journal");
        Assert.True(File.Exists(journalFilePath));
        string rawJson = File.ReadAllText(journalFilePath, Encoding.UTF8);

        // Assert NONE of the unique secrets occur in the raw journal file
        Assert.DoesNotContain(secretTitle, rawJson, StringComparison.Ordinal);
        Assert.DoesNotContain(secretText, rawJson, StringComparison.Ordinal);
        Assert.DoesNotContain(secretProc, rawJson, StringComparison.Ordinal);
        Assert.DoesNotContain(secretWin, rawJson, StringComparison.Ordinal);
        Assert.DoesNotContain(secretUrl, rawJson, StringComparison.Ordinal);
        Assert.DoesNotContain(secretTag, rawJson, StringComparison.Ordinal);
        Assert.DoesNotContain(secretAttOrig, rawJson, StringComparison.Ordinal);
        Assert.DoesNotContain(secretAttStored, rawJson, StringComparison.Ordinal);
        Assert.DoesNotContain(secretAttRel, rawJson, StringComparison.Ordinal);
        Assert.DoesNotContain(secretAttType, rawJson, StringComparison.Ordinal);
        Assert.DoesNotContain(secretAttSha, rawJson, StringComparison.Ordinal);

        // Verify that outer envelope has null for content fields
        Assert.Contains("\"IsProtected\": true", rawJson);
        Assert.Contains("\"Title\": null", rawJson);
        Assert.Contains("\"Text\": null", rawJson);
        Assert.Contains("\"SourceProcessName\": null", rawJson);
        Assert.Contains("\"SourceWindowTitle\": null", rawJson);
        Assert.Contains("\"SourceUrl\": null", rawJson);
        Assert.Contains("\"CapturedAt\": null", rawJson);
        Assert.Contains("\"Tags\": null", rawJson);
        Assert.Contains("\"Attachments\": null", rawJson);

        // Verify no temp files exist
        var tmpFiles = Directory.GetFiles(Path.Combine(journalDir, "DraftJournals"), "*.tmp");
        Assert.Empty(tmpFiles);

        // Read while unlocked: everything is decrypted correctly
        var readResult = service.ReadJournal(draftId);
        Assert.Equal(DraftJournalReadStatus.Success, readResult.Status);
        Assert.Equal(secretTitle, readResult.DecryptedTitle);
        Assert.Equal(secretText, readResult.DecryptedText);
        Assert.Equal(secretProc, readResult.SourceProcessName);
        Assert.Equal(secretWin, readResult.SourceWindowTitle);
        Assert.Equal(secretUrl, readResult.SourceUrl);
        Assert.NotNull(readResult.Tags);
        Assert.Single(readResult.Tags);
        Assert.Equal(secretTag, readResult.Tags[0].TagName);
        Assert.NotNull(readResult.Attachments);
        Assert.Single(readResult.Attachments);
        Assert.Equal(secretAttOrig, readResult.Attachments[0].OriginalFileName);
        Assert.Equal(secretAttStored, readResult.Attachments[0].StoredFileName);
        Assert.Equal(secretAttRel, readResult.Attachments[0].RelativePath);
        Assert.Equal(secretAttType, readResult.Attachments[0].ContentType);
        Assert.Equal(secretAttSha, readResult.Attachments[0].Sha256);
    }

    // ==================================================================
    // 11. Full state recovery: tags, suppressions, source context, attachments
    // ==================================================================

    [Fact]
    public void DraftRecovery_RestoresCompleteDraftState_TagsSuppressionsContextAndAttachmentsWithoutDuplication()
    {
        string attDir = MakeTempDir();
        var storage = new AttachmentStorageService(attDir);

        // Create a real attachment file managed by storage
        byte[] fileBytes = Encoding.UTF8.GetBytes("attachment content for testing draft recovery");
        var saveResult = storage.SaveFromBytes(fileBytes, "test-doc.txt", 1024 * 1024);

        int filesBeforeRecovery = Directory.GetFiles(attDir, "*", SearchOption.AllDirectories).Length;

        RunInSta(() =>
        {
            var vm = new NoteEditorViewModel(
                new TagDetectionService(),
                new List<Tag>
                {
                    new() { Id = 10, Name = "АктивныйТег" },
                    new() { Id = 20, Name = "ПодавленныйТег" }
                },
                existingNote: null,
                attachmentStorageService: storage);

            var promptArgs = new DraftRecoveryPromptArgs
            {
                DraftId = "new-recovery-test",
                DraftTitle = "Восстановленный заголовок",
                DraftText = "Восстановленный полный текст",
                SourceProcessName = "devenv",
                SourceWindowTitle = "Visual Studio",
                SourceUrl = "https://github.com/example/repo",
                CapturedAt = new DateTime(2026, 9, 11, 14, 0, 0),
                Tags = new List<DraftJournalTagDto>
                {
                    new() { TagId = 10, TagName = "АктивныйТег", Origin = TagOrigin.Manual, IsSuppressed = false },
                    new() { TagId = 20, TagName = "ПодавленныйТег", Origin = TagOrigin.Auto, IsSuppressed = true }
                },
                Attachments = new List<DraftJournalAttachmentDto>
                {
                    new()
                    {
                        Id = 0,
                        OriginalFileName = saveResult.OriginalFileName,
                        StoredFileName = saveResult.StoredFileName,
                        RelativePath = saveResult.RelativePath,
                        ContentType = saveResult.ContentType,
                        Size = saveResult.Size,
                        Sha256 = saveResult.Sha256,
                        IsNew = true
                    }
                }
            };

            vm.RestoreDraft(promptArgs);

            // 1. Title and text
            Assert.Equal("Восстановленный заголовок", vm.Title);
            Assert.Equal("Восстановленный полный текст", vm.Text);

            // 2. Source context
            Assert.Equal("devenv", vm.SourceProcessName);
            Assert.Equal("Visual Studio", vm.SourceWindowTitle);
            Assert.Equal("https://github.com/example/repo", vm.SourceUrl);
            Assert.Equal(new DateTime(2026, 9, 11, 14, 0, 0), vm.CapturedAt);

            // 3. Tags & suppressions
            Assert.Single(vm.ActiveTags);
            Assert.Equal("АктивныйТег", vm.ActiveTags[0].TagName);
            Assert.Equal(TagOrigin.Manual, vm.ActiveTags[0].Origin);
            Assert.Contains(20, vm.SuppressedTagIds);

            // 4. Attachments
            Assert.Single(vm.Attachments);
            Assert.Equal(saveResult.OriginalFileName, vm.Attachments[0].OriginalFileName);
            Assert.True(vm.Attachments[0].IsAvailable);
            Assert.True(vm.Attachments[0].IsNew);

            // 5. Verify NO files were duplicated in attachment storage
            int filesAfterRecovery = Directory.GetFiles(attDir, "*", SearchOption.AllDirectories).Length;
            Assert.Equal(filesBeforeRecovery, filesAfterRecovery);

            vm.Dispose();
        });
    }

    // ==================================================================
    // 12. Windows-safe atomic replacement fault injection
    // ==================================================================

    private sealed class FaultyDraftJournalFileOperations : IDraftJournalFileOperations
    {
        private readonly DefaultDraftJournalFileOperations _inner = new();
        public bool ThrowOnReplace { get; set; }

        public bool FileExists(string path) => _inner.FileExists(path);
        public void MoveFile(string source, string destination) => _inner.MoveFile(source, destination);
        public void DeleteFile(string path) => _inner.DeleteFile(path);

        public void ReplaceFileSafely(string tempFile, string targetFile)
        {
            if (ThrowOnReplace)
            {
                throw new IOException("Имитация сбоя файловой операции при атомарной замене черновика.");
            }
            _inner.ReplaceFileSafely(tempFile, targetFile);
        }
    }

    [Fact]
    public async Task AtomicReplacement_FaultInjection_LeavesPreviousValidJournalReadableAndCleansUpDebris()
    {
        string journalDir = MakeTempDir();
        var faultOps = new FaultyDraftJournalFileOperations();
        var service = new DraftJournalService(baseDirectory: journalDir, fileOperations: faultOps);

        string draftId = "safe-replace-draft";

        // 1. First save succeeds
        var initialSnapshot = new DraftJournalSnapshot
        {
            DraftId = draftId,
            SequenceNumber = 1,
            Title = "Исходный стабильный заголовок",
            Text = "Исходный стабильный текст черновика",
            IsProtected = false
        };
        var firstResult = await service.SaveJournalAsync(initialSnapshot);
        Assert.True(firstResult.Success);

        // Verify it reads back correctly
        var firstRead = service.ReadJournal(draftId);
        Assert.Equal(DraftJournalReadStatus.Success, firstRead.Status);
        Assert.Equal("Исходный стабильный заголовок", firstRead.DecryptedTitle);
        Assert.Equal("Исходный стабильный текст черновика", firstRead.DecryptedText);

        // 2. Inject fault during atomic replacement of second save
        faultOps.ThrowOnReplace = true;

        var secondSnapshot = new DraftJournalSnapshot
        {
            DraftId = draftId,
            SequenceNumber = 2,
            Title = "Новый сбойный заголовок",
            Text = "Новый текст, замена которого завершится ошибкой",
            IsProtected = false
        };

        var secondResult = await service.SaveJournalAsync(secondSnapshot);
        Assert.False(secondResult.Success);
        Assert.Equal("Ошибка сохранения черновика заметки.", secondResult.ErrorMessage);

        // 3. INVARIANT: Previous valid journal MUST remain untouched and readable
        var afterFaultRead = service.ReadJournal(draftId);
        Assert.Equal(DraftJournalReadStatus.Success, afterFaultRead.Status);
        Assert.Equal("Исходный стабильный заголовок", afterFaultRead.DecryptedTitle);
        Assert.Equal("Исходный стабильный текст черновика", afterFaultRead.DecryptedText);

        // 4. INVARIANT: Temp debris MUST be cleaned up
        var tmpFiles = Directory.GetFiles(Path.Combine(journalDir, "DraftJournals"), "*.tmp");
        Assert.Empty(tmpFiles);
    }

    // ==================================================================
    // 13. User-facing draft results contain no paths or exception types
    // ==================================================================

    [Fact]
    public void UserFacingDraftResults_ContainNoFilesystemPathsOrTechnicalExceptionDetails()
    {
        string journalDir = MakeTempDir();
        var service = new DraftJournalService(baseDirectory: journalDir);

        // 1. Corrupt json
        string corruptDraftId = "corrupt-test";
        string corruptPath = Path.Combine(journalDir, "DraftJournals", $"{corruptDraftId}.journal");
        Directory.CreateDirectory(Path.GetDirectoryName(corruptPath)!);
        File.WriteAllText(corruptPath, "{ NOT VALID JSON");

        var readCorrupt = service.ReadJournal(corruptDraftId);
        Assert.Equal(DraftJournalReadStatus.Corrupt, readCorrupt.Status);
        Assert.NotNull(readCorrupt.ErrorMessage);
        Assert.DoesNotContain(journalDir, readCorrupt.ErrorMessage);
        Assert.DoesNotContain("JsonException", readCorrupt.ErrorMessage);
        Assert.DoesNotContain("System.", readCorrupt.ErrorMessage);

        // 2. Unsupported version
        string versionDraftId = "version-test";
        string versionPath = Path.Combine(journalDir, "DraftJournals", $"{versionDraftId}.journal");
        File.WriteAllText(versionPath, "{\"Version\": 888, \"DraftId\": \"version-test\"}");

        var readVersion = service.ReadJournal(versionDraftId);
        Assert.Equal(DraftJournalReadStatus.UnsupportedVersion, readVersion.Status);
        Assert.NotNull(readVersion.ErrorMessage);
        Assert.DoesNotContain(journalDir, readVersion.ErrorMessage);
        Assert.Contains("888", readVersion.ErrorMessage);
    }
}
