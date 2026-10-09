using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using QuickNotes.App.Data;
using QuickNotes.App.Models;
using QuickNotes.App.Services;
using QuickNotes.App.Services.DraftJournal;
using QuickNotes.App.ViewModels;
using Xunit;

namespace QuickNotes.Tests;

[TestCategory(TestCategories.UiSensitive)]
public class AuditAndReliabilityTests
{
    private static void RunInSta(Action action) => StaTestHarness.Run(action, TimeSpan.FromSeconds(15));

    private class FaultyNoteHistoryService : NoteHistoryService
    {
        public bool ThrowOnSaveSnapshot { get; set; } = true;
        public bool ThrowOnPersistRestoredRevision { get; set; } = true;

        public override NoteRevision? SaveSnapshot(QuickNotesDbContext db, Note note, int maxRevisions = 20)
        {
            if (ThrowOnSaveSnapshot)
            {
                throw new InvalidOperationException("Injected snapshot failure");
            }
            return base.SaveSnapshot(db, note, maxRevisions);
        }

        public override void PersistRestoredRevision(QuickNotesDbContext db, Note note, NoteRevision revision, int maxRevisions)
        {
            if (ThrowOnPersistRestoredRevision)
            {
                throw new InvalidOperationException("Injected failure during restore revision persistence");
            }
            base.PersistRestoredRevision(db, note, revision, maxRevisions);
        }
    }

    private static MainViewModel CreateTestMainViewModel(string dbPath, INoteHistoryService historyService)
    {
        return MainViewModelTestComposition.Create(
            () => new QuickNotesDbContext(dbPath),
            new TagDetectionService(),
            new SearchService(),
            new SettingsService(),
            new GlobalHotkeyService((_, _, _, _) => true, (_, _) => true, IntPtr.Zero),
            new ClipboardCaptureService(),
            new TrayIconService(),
            new BackupService(),
            noteHistoryService: historyService,
            draftJournalService: NoOpDraftJournalService.Instance);
    }

    [Fact]
    public void SaveInstantNote_FailureInjectionInSnapshot_RollsBackEntireNoteCreation()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"qn_test_instant_rollback_{Guid.NewGuid():N}.db");
        try
        {
            using (var db = new QuickNotesDbContext(dbPath))
            {
                DbInitializer.Initialize(db);
            }

            RunInSta(() =>
            {
                var faultyHistory = new FaultyNoteHistoryService { ThrowOnSaveSnapshot = true };
                var mainVm = CreateTestMainViewModel(dbPath, faultyHistory);

                var capture = new CapturedNoteContext
                {
                    Text = "Instant Note Rollback Test",
                    CapturedAt = DateTime.Now
                };

                Assert.Throws<InvalidOperationException>(() => mainVm.SaveInstantNote(capture));

                using (var verifyDb = new QuickNotesDbContext(dbPath))
                {
                    Assert.Empty(verifyDb.Notes.ToList());
                    Assert.Empty(verifyDb.NoteRevisions.ToList());
                    Assert.Empty(verifyDb.NoteTags.ToList());
                }
            });
        }
        finally
        {
            if (File.Exists(dbPath)) try { File.Delete(dbPath); } catch { }
        }
    }

    [Fact]
    public void SaveNewNoteFromEditor_FailureInjectionInSnapshot_RollsBackEntireNoteCreation()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"qn_test_editor_rollback_{Guid.NewGuid():N}.db");
        try
        {
            using (var db = new QuickNotesDbContext(dbPath))
            {
                DbInitializer.Initialize(db);
            }

            RunInSta(() =>
            {
                var faultyHistory = new FaultyNoteHistoryService { ThrowOnSaveSnapshot = true };
                using var mainVm = CreateTestMainViewModel(dbPath, faultyHistory);

                mainVm.RequestOpenNoteEditor += vm =>
                {
                    using var _ = vm;
                    vm.Text = "Editor Note Rollback Test";
                    Assert.Throws<InvalidOperationException>(() => vm.Commit?.Invoke());
                    return false;
                };

                mainVm.CreateNewNote();

                using var verifyDb = new QuickNotesDbContext(dbPath);
                Assert.Empty(verifyDb.Notes.ToList());
                Assert.Empty(verifyDb.NoteRevisions.ToList());
            });
        }
        finally
        {
            if (File.Exists(dbPath)) try { File.Delete(dbPath); } catch { }
        }
    }

    [Fact]
    public void EditNote_FailureInjectionInSnapshot_RollsBackNoteModifications()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"qn_test_edit_rollback_{Guid.NewGuid():N}.db");
        try
        {
            int noteId;
            using (var initDb = new QuickNotesDbContext(dbPath))
            {
                DbInitializer.Initialize(initDb);
                var note = new Note
                {
                    Text = "Original Text",
                    CreatedAt = DateTime.Now,
                    UpdatedAt = DateTime.Now
                };
                initDb.Notes.Add(note);
                initDb.SaveChanges();
                new NoteHistoryService().SaveSnapshot(initDb, note);
                noteId = note.Id;
            }

            RunInSta(() =>
            {
                var faultyHistory = new FaultyNoteHistoryService { ThrowOnSaveSnapshot = true };
                using var mainVm = CreateTestMainViewModel(dbPath, faultyHistory);

                mainVm.RequestOpenNoteEditor += vm =>
                {
                    using var _ = vm;
                    vm.Text = "Modified Text Should Rollback";
                    Assert.Throws<InvalidOperationException>(() => vm.Commit?.Invoke());
                    return false;
                };

                mainVm.EditNote(new NoteCardViewModel(new Note { Id = noteId, Text = "Original Text" }));

                using var verifyDb = new QuickNotesDbContext(dbPath);
                var note = verifyDb.Notes.Single(n => n.Id == noteId);
                Assert.Equal("Original Text", note.Text);
                Assert.Single(verifyDb.NoteRevisions.Where(r => r.NoteId == noteId));
            });
        }
        finally
        {
            if (File.Exists(dbPath)) try { File.Delete(dbPath); } catch { }
        }
    }

    [Fact]
    public void RestoreRevision_FailureInjectionInSaveSnapshot_RollsBackNoteModifications()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"qn_test_restore_rollback_{Guid.NewGuid():N}.db");
        try
        {
            int noteId;
            int rev1Id;
            using (var db = new QuickNotesDbContext(dbPath))
            {
                DbInitializer.Initialize(db);
                var note = new Note
                {
                    Text = "Version 1 Initial",
                    CreatedAt = DateTime.Now.AddMinutes(-5),
                    UpdatedAt = DateTime.Now.AddMinutes(-5)
                };
                db.Notes.Add(note);
                db.SaveChanges();
                var rev1 = new NoteHistoryService().SaveSnapshot(db, note);
                noteId = note.Id;
                rev1Id = rev1!.Id;

                note.Text = "Version 2 Modified";
                note.UpdatedAt = DateTime.Now;
                db.SaveChanges();
                new NoteHistoryService().SaveSnapshot(db, note);
            }

            using (var db = new QuickNotesDbContext(dbPath))
            {
                var faultyHistory = new FaultyNoteHistoryService { ThrowOnPersistRestoredRevision = true };
                Assert.Throws<InvalidOperationException>(() => faultyHistory.RestoreRevision(db, noteId, rev1Id));
            }

            using (var verifyDb = new QuickNotesDbContext(dbPath))
            {
                var note = verifyDb.Notes.Single(n => n.Id == noteId);
                Assert.Equal("Version 2 Modified", note.Text);
                Assert.Equal(2, verifyDb.NoteRevisions.Count(r => r.NoteId == noteId));
            }
        }
        finally
        {
            if (File.Exists(dbPath)) try { File.Delete(dbPath); } catch { }
        }
    }

    [Fact]
    public void RescanNote_FailureInjectionInSaveSnapshot_RollsBackNoteTagsAndRevision()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"qn_test_rescan_rollback_{Guid.NewGuid():N}.db");
        try
        {
            int noteId;
            using (var db = new QuickNotesDbContext(dbPath))
            {
                DbInitializer.Initialize(db);
                db.Tags.Add(new Tag { Name = "Backend" });
                var note = new Note
                {
                    Text = "This is Backend architecture",
                    CreatedAt = DateTime.Now,
                    UpdatedAt = DateTime.Now
                };
                db.Notes.Add(note);
                db.SaveChanges();
                noteId = note.Id;
            }

            RunInSta(() =>
            {
                var faultyHistory = new FaultyNoteHistoryService { ThrowOnSaveSnapshot = true };
                var mainVm = CreateTestMainViewModel(dbPath, faultyHistory);
                var card = new NoteCardViewModel(new Note { Id = noteId, Text = "This is Backend architecture" });

                Assert.Throws<InvalidOperationException>(() => mainVm.RescanNote(card));

                using var verifyDb = new QuickNotesDbContext(dbPath);
                Assert.Empty(verifyDb.NoteTags.Where(nt => nt.NoteId == noteId));
                Assert.Empty(verifyDb.NoteRevisions.Where(r => r.NoteId == noteId));
            });
        }
        finally
        {
            if (File.Exists(dbPath)) try { File.Delete(dbPath); } catch { }
        }
    }

    [Fact]
    public void RescanAllNotes_FailureInjectionInSaveSnapshot_RollsBackAllNotes()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"qn_test_rescan_all_rollback_{Guid.NewGuid():N}.db");
        try
        {
            using (var db = new QuickNotesDbContext(dbPath))
            {
                DbInitializer.Initialize(db);
                db.Tags.Add(new Tag { Name = "Frontend" });
                db.Notes.Add(new Note { Text = "Frontend work 1", CreatedAt = DateTime.Now, UpdatedAt = DateTime.Now });
                db.Notes.Add(new Note { Text = "Frontend work 2", CreatedAt = DateTime.Now, UpdatedAt = DateTime.Now });
                db.SaveChanges();
            }

            RunInSta(() =>
            {
                var faultyHistory = new FaultyNoteHistoryService { ThrowOnSaveSnapshot = true };
                var mainVm = CreateTestMainViewModel(dbPath, faultyHistory);
                mainVm.RequestOpenRescanPreview += pvm => true;

                Assert.Throws<InvalidOperationException>(() => mainVm.RescanAllNotes());

                using var verifyDb = new QuickNotesDbContext(dbPath);
                Assert.Empty(verifyDb.NoteTags.ToList());
                Assert.Empty(verifyDb.NoteRevisions.ToList());
            });
        }
        finally
        {
            if (File.Exists(dbPath)) try { File.Delete(dbPath); } catch { }
        }
    }

    [Fact]
    public void RestoreRevision_CorruptTagsJson_ThrowsAndLeavesNoteTextAndTagsUnchanged_NoNewRevision()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"qn_test_corrupt_restore_{Guid.NewGuid():N}.db");
        try
        {
            using var db = new QuickNotesDbContext(dbPath);
            DbInitializer.Initialize(db);

            var tag = new Tag { Name = "Backend" };
            db.Tags.Add(tag);
            db.SaveChanges();

            var note = new Note
            {
                Text = "Current Valid Text",
                CreatedAt = DateTime.Now,
                UpdatedAt = DateTime.Now
            };
            note.NoteTags.Add(new NoteTag { TagId = tag.Id, Origin = TagOrigin.Manual });
            db.Notes.Add(note);
            db.SaveChanges();

            new NoteHistoryService().SaveSnapshot(db, note);

            // Insert a corrupted revision directly into NoteRevisions
            var corruptRev = new NoteRevision
            {
                NoteId = note.Id,
                CreatedAt = DateTime.Now.AddMinutes(1),
                Text = "Text From Corrupted Revision",
                TagsJson = "{ this is completely invalid JSON [[[ }}"
            };
            db.NoteRevisions.Add(corruptRev);
            db.SaveChanges();

            var historyService = new NoteHistoryService();

            // Attempting to restore corrupt revision must throw an exception
            var ex = Assert.Throws<InvalidOperationException>(() => historyService.RestoreRevision(db, note.Id, corruptRev.Id));
            Assert.Contains("снимок тегов повреждён", ex.Message, StringComparison.OrdinalIgnoreCase);

            // Verify in a fresh context that Note.Text, NoteTags, and Revisions count were NOT modified
            using var verifyDb = new QuickNotesDbContext(dbPath);
            var freshNote = verifyDb.Notes.Include(n => n.NoteTags).ThenInclude(nt => nt.Tag).Single(n => n.Id == note.Id);
            Assert.Equal("Current Valid Text", freshNote.Text);
            Assert.Single(freshNote.NoteTags);
            Assert.Equal("Backend", freshNote.NoteTags.First().Tag?.Name);
            Assert.Equal(2, verifyDb.NoteRevisions.Count(r => r.NoteId == note.Id));
        }
        finally
        {
            if (File.Exists(dbPath)) try { File.Delete(dbPath); } catch { }
        }
    }

    [Fact]
    public void GetHistory_CorruptTagsJson_MarksRevisionAsCorrupted_WithoutThrowing()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"qn_test_corrupt_gethistory_{Guid.NewGuid():N}.db");
        try
        {
            using var db = new QuickNotesDbContext(dbPath);
            DbInitializer.Initialize(db);

            var note = new Note
            {
                Text = "Note with corrupt history",
                CreatedAt = DateTime.Now,
                UpdatedAt = DateTime.Now
            };
            db.Notes.Add(note);
            db.SaveChanges();

            var rev1 = new NoteRevision
            {
                NoteId = note.Id,
                CreatedAt = DateTime.Now,
                Text = "Version 1",
                TagsJson = "[]"
            };
            var rev2 = new NoteRevision
            {
                NoteId = note.Id,
                CreatedAt = DateTime.Now.AddMinutes(1),
                Text = "Version 2 Corrupted",
                TagsJson = "{ invalid: json: 123"
            };
            db.NoteRevisions.AddRange(rev1, rev2);
            db.SaveChanges();

            var history = new NoteHistoryService().GetHistory(db, note.Id);
            Assert.Equal(2, history.Count);

            var corruptDto = history.First(h => h.Id == rev2.Id);
            Assert.True(corruptDto.IsCorrupted);
            Assert.Contains("повреждены", corruptDto.DiffSummary, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(corruptDto.Tags);

            var validDto = history.First(h => h.Id == rev1.Id);
            Assert.False(validDto.IsCorrupted);

            var itemVm = new NoteHistoryItemViewModel(corruptDto);
            Assert.False(itemVm.CanRestore);
            Assert.Contains("повреждены", itemVm.TagsSummary, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (File.Exists(dbPath)) try { File.Delete(dbPath); } catch { }
        }
    }

    [Fact]
    public void DbInitializer_BackupBeforeUpgrade_TriggeredWhenTagsAndSynonymsExistWithoutNotes()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"qn_test_backup_tags_{Guid.NewGuid():N}.db");
        var emptyDbPath = Path.Combine(Path.GetTempPath(), $"qn_test_backup_empty_{Guid.NewGuid():N}.db");
        try
        {
            using (var conn = new SqliteConnection($"Data Source={dbPath}"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    CREATE TABLE Notes (Id INTEGER PRIMARY KEY AUTOINCREMENT, Text TEXT, CreatedAt TEXT, UpdatedAt TEXT);
                    CREATE TABLE Tags (Id INTEGER PRIMARY KEY AUTOINCREMENT, Name TEXT, ParentTagId INTEGER);
                    CREATE TABLE TagSynonyms (Id INTEGER PRIMARY KEY AUTOINCREMENT, TagId INTEGER, Value TEXT);
                    INSERT INTO Tags (Id, Name) VALUES (1, 'Architecture');
                    INSERT INTO TagSynonyms (Id, TagId, Value) VALUES (1, 1, 'Arch');
                    PRAGMA user_version = 1;
                ";
                cmd.ExecuteNonQuery();
            }

            bool backupTriggered = false;
            using (var context = new QuickNotesDbContext(dbPath))
            {
                DbInitializer.Initialize(context, () => backupTriggered = true);
            }
            Assert.True(backupTriggered, "Backup before upgrade should be triggered even if 0 notes exist, when tags/synonyms are present");

            // Truly empty database must NOT trigger backup
            bool emptyBackupTriggered = false;
            using (var emptyContext = new QuickNotesDbContext(emptyDbPath))
            {
                DbInitializer.Initialize(emptyContext, () => emptyBackupTriggered = true);
            }
            Assert.False(emptyBackupTriggered, "Backup before upgrade should NOT be triggered for a truly empty database");
        }
        finally
        {
            if (File.Exists(dbPath)) try { File.Delete(dbPath); } catch { }
            if (File.Exists(emptyDbPath)) try { File.Delete(emptyDbPath); } catch { }
        }
    }

    [Fact]
    public void PurgeOldTrash_MoreThan500OldNotes_PurgesAllInBatchesAndKeepsFreshAndActive()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"qn_test_purge550_{Guid.NewGuid():N}.db");
        try
        {
            using var db = new QuickNotesDbContext(dbPath);
            DbInitializer.Initialize(db);
            var now = DateTime.UtcNow;

            // 550 expired notes (older than 30 days)
            var oldNotes = Enumerable.Range(1, 550).Select(i => new Note
            {
                Text = $"Expired note {i}",
                CreatedAt = now.AddDays(-60),
                UpdatedAt = now.AddDays(-60),
                DeletedAt = now.AddDays(-35)
            }).ToList();
            db.Notes.AddRange(oldNotes);

            // 5 fresh trash notes (within 30 days)
            var freshTrashNotes = Enumerable.Range(1, 5).Select(i => new Note
            {
                Text = $"Fresh trash note {i}",
                CreatedAt = now.AddDays(-10),
                UpdatedAt = now.AddDays(-10),
                DeletedAt = now.AddDays(-5)
            }).ToList();
            db.Notes.AddRange(freshTrashNotes);

            // 5 active notes (not in trash)
            var activeNotes = Enumerable.Range(1, 5).Select(i => new Note
            {
                Text = $"Active note {i}",
                CreatedAt = now.AddDays(-2),
                UpdatedAt = now.AddDays(-2),
                DeletedAt = null
            }).ToList();
            db.Notes.AddRange(activeNotes);

            db.SaveChanges();

            // Run purge with batch size 200 to verify multiple batch execution
            var searchService = new SearchService();
            int totalPurged = searchService.PurgeOldTrash(db, now.AddDays(-30), maxBatch: 200);

            Assert.Equal(550, totalPurged);

            using var verifyDb = new QuickNotesDbContext(dbPath);
            Assert.Equal(10, verifyDb.Notes.Count());
            Assert.Equal(5, verifyDb.Notes.Count(n => n.DeletedAt != null));
            Assert.Equal(5, verifyDb.Notes.Count(n => n.DeletedAt == null));
        }
        finally
        {
            if (File.Exists(dbPath)) try { File.Delete(dbPath); } catch { }
        }
    }

    [Fact]
    public void SearchPreview_CompositeQueries_MultipleTerms_QuotedPhrase_StructuredFilters()
    {
        // 1. Multiple terms extraction & highlighting
        string multiQuery = "railway production";
        string text = "Incident on railway staging and production servers";
        var spans = SearchPreview.SplitHighlights(text, multiQuery);
        Assert.Contains(spans, s => s.IsMatch && s.Text.Equals("railway", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(spans, s => s.IsMatch && s.Text.Equals("production", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(spans, s => !s.IsMatch && s.Text.Contains("Incident on "));

        // 2. Quoted phrase extraction & highlighting
        string quotedQuery = "\"railway server\" critical";
        string textWithPhrase = "Error on railway server was critical";
        var phraseSpans = SearchPreview.SplitHighlights(textWithPhrase, quotedQuery);
        Assert.Contains(phraseSpans, s => s.IsMatch && s.Text.Equals("railway server", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(phraseSpans, s => s.IsMatch && s.Text.Equals("critical", StringComparison.OrdinalIgnoreCase));

        // 3. Structured filters and operators exclusion
        string complexQuery = "tag:production created:today \"database error\" timeout WITHOUT tag:archive";
        var terms = SearchPreview.ExtractSearchTerms(complexQuery);
        Assert.Equal(2, terms.Count);
        Assert.Contains("database error", terms);
        Assert.Contains("timeout", terms);
        Assert.DoesNotContain("tag:production", terms);
        Assert.DoesNotContain("created:today", terms);
        Assert.DoesNotContain("WITHOUT", terms);
        Assert.DoesNotContain("tag:archive", terms);

        // 4. Snippet positioning around actual match when first term is missing
        string longText = new string('a', 200) + " CRITICAL_ERROR " + new string('b', 200);
        string snippetQuery = "missing_term CRITICAL_ERROR";
        string snippet = SearchPreview.BuildSnippet(longText, snippetQuery, 80, expanded: false);
        Assert.Contains("CRITICAL_ERROR", snippet);
        Assert.Contains("…", snippet);

        // 5. Backward compatibility for simple query
        Assert.Equal("Oracle", SearchPreview.FirstTerm("tag:Work Oracle AND"));
        var singleSpans = SearchPreview.SplitHighlights("railway and Oracle DB", "oracle");
        Assert.Contains(singleSpans, s => s.IsMatch && s.Text.Equals("Oracle", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void DatabaseRecoveryService_DetectsCorruption_ValidatesBackup_SafelyRecovers()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"qn_test_recovery_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var dbPath = Path.Combine(tempDir, "quicknotes.db");
        var backupsDir = Path.Combine(tempDir, "Backups");
        Directory.CreateDirectory(backupsDir);

        try
        {
            // 1. Create a healthy original database and create a backup
            using (var db = new QuickNotesDbContext(dbPath))
            {
                DbInitializer.Initialize(db);
                db.Notes.Add(new Note { Text = "Pre-corruption note", CreatedAt = DateTime.Now, UpdatedAt = DateTime.Now });
                db.SaveChanges();
            }

            var backupService = new BackupService(dbPath, backupsDir);
            string backupPath = backupService.CreateBackup();
            Assert.True(File.Exists(backupPath));

            var recoveryService = new DatabaseRecoveryService(dbPath, backupService);

            // Initially not corrupt
            Assert.False(recoveryService.IsDatabaseCorrupt(out _));

            // Clear connection pools before overwriting dbPath
            SqliteConnection.ClearAllPools();

            // 2. Corrupt the database file by overwriting it with garbage bytes
            File.WriteAllBytes(dbPath, new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0x01, 0x02, 0x03, 0x04 });

            // Now it must be detected as corrupt
            Assert.True(recoveryService.IsDatabaseCorrupt(out var corruptReason));
            Assert.NotEmpty(corruptReason);

            // 3. Find latest healthy backup
            var healthyBackup = recoveryService.FindLatestHealthyBackup(out var backupDiag);
            Assert.NotNull(healthyBackup);
            Assert.Equal(Path.GetFileName(backupPath), healthyBackup.Name);

            // 4. Perform recovery
            var result = recoveryService.PerformRecovery(healthyBackup.FullName);
            Assert.True(result.Success);
            Assert.NotNull(result.PreservedCorruptPath);
            Assert.True(File.Exists(result.PreservedCorruptPath));

            // Verify original db is now recovered and contains the note
            Assert.False(recoveryService.IsDatabaseCorrupt(out _));
            using (var db = new QuickNotesDbContext(dbPath))
            {
                var note = db.Notes.FirstOrDefault();
                Assert.NotNull(note);
                Assert.Equal("Pre-corruption note", note.Text);
            }

            // 5. Test rejecting corrupt backup without destroying original
            var corruptBackupPath = Path.Combine(backupsDir, "quicknotes_backup_corrupt.db");
            File.WriteAllBytes(corruptBackupPath, new byte[] { 0x00, 0x11, 0x22 });
            Assert.False(recoveryService.CheckBackupIntegrity(corruptBackupPath, out _));

            var rejectResult = recoveryService.PerformRecovery(corruptBackupPath);
            Assert.False(rejectResult.Success);
            Assert.Contains("повреждена", rejectResult.ErrorMessage, StringComparison.OrdinalIgnoreCase);

            // Original file is still intact
            using (var db = new QuickNotesDbContext(dbPath))
            {
                Assert.NotNull(db.Notes.FirstOrDefault());
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }
    }
}
