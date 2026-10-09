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
using QuickNotes.App.ViewModels;
using Xunit;

namespace QuickNotes.Tests;

[TestCategory(TestCategories.UiSensitive)]
public class NoteHistoryTests
{
    private static void RunInSta(Action action) => StaTestHarness.Run(action, TimeSpan.FromSeconds(15));

    [Fact]
    public void DatabaseInitialization_Version3Upgrade_AddsNoteRevisionsTableAndPreservesData()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"quicknotes_v3_migration_{Guid.NewGuid():N}.db");
        try
        {
            // 1. Create legacy database with v2 schema and existing notes/tags
            using (var connection = new SqliteConnection($"Data Source={dbPath}"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = @"
                    CREATE TABLE Notes (
                        Id INTEGER PRIMARY KEY AUTOINCREMENT,
                        Text TEXT NOT NULL,
                        CreatedAt TEXT NOT NULL,
                        UpdatedAt TEXT NOT NULL,
                        IsPinned INTEGER NOT NULL DEFAULT 0,
                        IsFavorite INTEGER NOT NULL DEFAULT 0,
                        IsInbox INTEGER NOT NULL DEFAULT 0,
                        DeletedAt TEXT NULL,
                        SourceProcessName TEXT NULL,
                        SourceWindowTitle TEXT NULL,
                        SourceUrl TEXT NULL,
                        CapturedAt TEXT NULL
                    );
                    CREATE TABLE Tags (
                        Id INTEGER PRIMARY KEY AUTOINCREMENT,
                        Name TEXT NOT NULL,
                        ParentTagId INTEGER NULL
                    );
                    CREATE TABLE NoteTags (
                        NoteId INTEGER NOT NULL,
                        TagId INTEGER NOT NULL,
                        Origin INTEGER NOT NULL DEFAULT 0,
                        IsSuppressed INTEGER NOT NULL DEFAULT 0,
                        PRIMARY KEY (NoteId, TagId)
                    );
                    INSERT INTO Notes (Id, Text, CreatedAt, UpdatedAt, IsPinned, IsFavorite, IsInbox)
                    VALUES (1, 'Existing v2 note content', '2026-03-01 12:00:00', '2026-03-01 12:00:00', 1, 0, 0);

                    INSERT INTO Tags (Id, Name) VALUES (1, 'Architecture');
                    INSERT INTO NoteTags (NoteId, TagId, Origin, IsSuppressed) VALUES (1, 1, 1, 0);

                    PRAGMA user_version = 2;
                ";
                command.ExecuteNonQuery();
            }

            // 2. Initialize with DbInitializer
            using (var context = new QuickNotesDbContext(dbPath))
            {
                DbInitializer.Initialize(context);
            }

            // 3. Verify user_version = 3, table NoteRevisions exists, indexes exist, and data preserved
            using (var verify = new SqliteConnection($"Data Source={dbPath}"))
            {
                verify.Open();

                long version = 0;
                using (var versionCmd = verify.CreateCommand())
                {
                    versionCmd.CommandText = "PRAGMA user_version;";
                    version = Convert.ToInt64(versionCmd.ExecuteScalar());
                }
                Assert.True(version >= 3);
                Assert.Equal(DbInitializer.CurrentSchemaVersion, version);

                // Check NoteRevisions table
                bool tableExists = false;
                using (var tblCmd = verify.CreateCommand())
                {
                    tblCmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='NoteRevisions';";
                    tableExists = Convert.ToInt64(tblCmd.ExecuteScalar()) > 0;
                }
                Assert.True(tableExists, "NoteRevisions table should exist after v3 migration");

                // Check columns in NoteRevisions
                var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                using (var colCmd = verify.CreateCommand())
                {
                    colCmd.CommandText = "PRAGMA table_info(NoteRevisions);";
                    using var reader = colCmd.ExecuteReader();
                    while (reader.Read())
                    {
                        columns.Add(reader.GetString(1));
                    }
                }
                Assert.Contains("Id", columns);
                Assert.Contains("NoteId", columns);
                Assert.Contains("CreatedAt", columns);
                Assert.Contains("Text", columns);
                Assert.Contains("TagsJson", columns);

                // Check indexes
                var indexes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                using (var idxCmd = verify.CreateCommand())
                {
                    idxCmd.CommandText = "SELECT name FROM sqlite_master WHERE type='index' AND tbl_name='NoteRevisions';";
                    using var reader = idxCmd.ExecuteReader();
                    while (reader.Read())
                    {
                        indexes.Add(reader.GetString(0));
                    }
                }
                Assert.Contains("IX_NoteRevisions_NoteId", indexes);
                Assert.Contains("IX_NoteRevisions_CreatedAt", indexes);
            }

            // 4. Verify existing notes and tags are intact via EF context
            using (var context = new QuickNotesDbContext(dbPath))
            {
                var note = context.Notes.Include(n => n.NoteTags).FirstOrDefault(n => n.Id == 1);
                Assert.NotNull(note);
                Assert.Equal("Existing v2 note content", note.Text);
                Assert.True(note.IsPinned);
                Assert.Single(note.NoteTags);
                Assert.Equal(1, note.NoteTags.First().TagId);
                Assert.Equal(TagOrigin.Manual, note.NoteTags.First().Origin);
            }
        }
        finally
        {
            if (File.Exists(dbPath))
            {
                try { File.Delete(dbPath); } catch { }
            }
        }
    }

    [Fact]
    public void NoteHistoryService_SaveSnapshot_CreatesSnapshot_AndDetectsNoOp()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"quicknotes_snapshot_{Guid.NewGuid():N}.db");
        try
        {
            using var context = new QuickNotesDbContext(dbPath);
            DbInitializer.Initialize(context);

            var service = new NoteHistoryService();

            var tag1 = new Tag { Name = "DotNet" };
            var tag2 = new Tag { Name = "WPF" };
            context.Tags.AddRange(tag1, tag2);
            context.SaveChanges();

            var note = new Note
            {
                Text = "Hello world note",
                CreatedAt = DateTime.Now,
                UpdatedAt = DateTime.Now
            };
            note.NoteTags.Add(new NoteTag { TagId = tag1.Id, Origin = TagOrigin.Manual, IsSuppressed = false });
            note.NoteTags.Add(new NoteTag { TagId = tag2.Id, Origin = TagOrigin.Auto, IsSuppressed = false });
            context.Notes.Add(note);
            context.SaveChanges();

            // 1. Initial snapshot on create
            var rev1 = service.SaveSnapshot(context, note);
            Assert.NotNull(rev1);
            Assert.Equal(note.Id, rev1.NoteId);
            Assert.Equal("Hello world note", rev1.Text);

            var tags = NoteHistoryService.DeserializeTags(rev1.TagsJson);
            Assert.Equal(2, tags.Count);
            Assert.Contains(tags, t => t.TagName == "DotNet" && t.Origin == TagOrigin.Manual && !t.IsSuppressed);
            Assert.Contains(tags, t => t.TagName == "WPF" && t.Origin == TagOrigin.Auto && !t.IsSuppressed);

            // Total revisions in DB should be 1
            Assert.Equal(1, context.NoteRevisions.Count(r => r.NoteId == note.Id));

            // 2. Second call without any changes (no-op)
            var revNoOp = service.SaveSnapshot(context, note);
            Assert.Null(revNoOp);
            Assert.Equal(1, context.NoteRevisions.Count(r => r.NoteId == note.Id));

            // 3. Change text -> not a no-op, should save revision 2
            note.Text = "Hello world note edited";
            note.UpdatedAt = DateTime.Now;
            var rev2 = service.SaveSnapshot(context, note);
            Assert.NotNull(rev2);
            Assert.Equal("Hello world note edited", rev2.Text);
            Assert.Equal(2, context.NoteRevisions.Count(r => r.NoteId == note.Id));

            // 4. Change tag origin -> not a no-op, should save revision 3
            var wpfTag = note.NoteTags.First(nt => nt.TagId == tag2.Id);
            wpfTag.Origin = TagOrigin.Manual;
            var rev3 = service.SaveSnapshot(context, note);
            Assert.NotNull(rev3);
            Assert.Equal(3, context.NoteRevisions.Count(r => r.NoteId == note.Id));

            // 5. Suppress a tag -> not a no-op, should save revision 4
            wpfTag.IsSuppressed = true;
            var rev4 = service.SaveSnapshot(context, note);
            Assert.NotNull(rev4);
            Assert.Equal(4, context.NoteRevisions.Count(r => r.NoteId == note.Id));
        }
        finally
        {
            if (File.Exists(dbPath))
            {
                try { File.Delete(dbPath); } catch { }
            }
        }
    }

    [Fact]
    public void NoteHistoryService_Max20Limit_AtomicallyPrunesOldestRevisions()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"quicknotes_limit20_{Guid.NewGuid():N}.db");
        try
        {
            using var context = new QuickNotesDbContext(dbPath);
            DbInitializer.Initialize(context);

            var service = new NoteHistoryService();

            var note = new Note
            {
                Text = "Version 0",
                CreatedAt = DateTime.Now,
                UpdatedAt = DateTime.Now
            };
            context.Notes.Add(note);
            context.SaveChanges();

            service.SaveSnapshot(context, note);

            // Make 24 more distinct edits (total 25 versions)
            for (int i = 1; i <= 24; i++)
            {
                note.Text = $"Version {i}";
                note.UpdatedAt = DateTime.Now.AddSeconds(i);
                var rev = service.SaveSnapshot(context, note, maxRevisions: 20);
                Assert.NotNull(rev);
            }

            // Total revisions must be exactly 20
            var allRevisions = context.NoteRevisions
                .Where(r => r.NoteId == note.Id)
                .OrderBy(r => r.Id)
                .ToList();

            Assert.Equal(20, allRevisions.Count);

            // The remaining versions should be Version 5 through Version 24
            Assert.Equal("Version 5", allRevisions.First().Text);
            Assert.Equal("Version 24", allRevisions.Last().Text);
        }
        finally
        {
            if (File.Exists(dbPath))
            {
                try { File.Delete(dbPath); } catch { }
            }
        }
    }

    [Fact]
    public void NoteHistoryService_ComputeDiffSummary_ProducesExpectedBriefDescriptions()
    {
        var service = new NoteHistoryService();

        // 1. Initial creation diff
        var tags1 = new List<NoteRevisionTagSnapshot>
        {
            new() { TagId = 1, TagName = "CSharp", Origin = TagOrigin.Manual, IsSuppressed = false },
            new() { TagId = 2, TagName = "WPF", Origin = TagOrigin.Auto, IsSuppressed = false }
        };
        var diff1 = service.ComputeDiffSummary(null, null, "Hello QuickNotes", tags1);
        Assert.Contains("Создание заметки", diff1);
        Assert.Contains("16 симв.", diff1);
        Assert.Contains("тегов: 2", diff1);

        // 2. Text only difference (added chars and line)
        var diff2 = service.ComputeDiffSummary("Hello QuickNotes", tags1, "Hello QuickNotes\nSecond line added", tags1);
        Assert.Contains("+18 симв.", diff2);
        Assert.Contains("+1 стр.", diff2);

        // 3. Tag added and removed
        var tags3 = new List<NoteRevisionTagSnapshot>
        {
            new() { TagId = 1, TagName = "CSharp", Origin = TagOrigin.Manual, IsSuppressed = false },
            new() { TagId = 3, TagName = "Architecture", Origin = TagOrigin.Auto, IsSuppressed = false }
        };
        var diff3 = service.ComputeDiffSummary("Hello QuickNotes", tags1, "Hello QuickNotes", tags3);
        Assert.Contains("Теги:", diff3);
        Assert.Contains("+Architecture", diff3);
        Assert.Contains("-WPF", diff3);

        // 4. Tag suppressed
        var tags4 = new List<NoteRevisionTagSnapshot>
        {
            new() { TagId = 1, TagName = "CSharp", Origin = TagOrigin.Manual, IsSuppressed = false },
            new() { TagId = 2, TagName = "WPF", Origin = TagOrigin.Auto, IsSuppressed = true }
        };
        var diff4 = service.ComputeDiffSummary("Hello QuickNotes", tags1, "Hello QuickNotes", tags4);
        Assert.Contains("~скрыт WPF", diff4);

        // 5. Tag origin changed from Auto to Manual
        var tags5 = new List<NoteRevisionTagSnapshot>
        {
            new() { TagId = 1, TagName = "CSharp", Origin = TagOrigin.Manual, IsSuppressed = false },
            new() { TagId = 2, TagName = "WPF", Origin = TagOrigin.Manual, IsSuppressed = false }
        };
        var diff5 = service.ComputeDiffSummary("Hello QuickNotes", tags1, "Hello QuickNotes", tags5);
        Assert.Contains("~WPF (вручную)", diff5);
    }

    [Fact]
    public void NoteHistoryService_GetHistory_ReturnsRevisionsNewestFirstWithDiffs()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"quicknotes_history_{Guid.NewGuid():N}.db");
        try
        {
            using var context = new QuickNotesDbContext(dbPath);
            DbInitializer.Initialize(context);

            var service = new NoteHistoryService();

            var tag = new Tag { Name = "General" };
            context.Tags.Add(tag);
            context.SaveChanges();

            var note = new Note
            {
                Text = "First line",
                CreatedAt = DateTime.Now,
                UpdatedAt = DateTime.Now
            };
            note.NoteTags.Add(new NoteTag { TagId = tag.Id, Origin = TagOrigin.Auto });
            context.Notes.Add(note);
            context.SaveChanges();

            service.SaveSnapshot(context, note);

            note.Text = "First line\nSecond line";
            note.UpdatedAt = DateTime.Now.AddMinutes(1);
            service.SaveSnapshot(context, note);

            var history = service.GetHistory(context, note.Id);
            Assert.Equal(2, history.Count);

            // Newest version should be first
            Assert.Equal("First line\nSecond line", history[0].Text);
            Assert.Contains("+12 симв.", history[0].DiffSummary);

            // Oldest version should be second
            Assert.Equal("First line", history[1].Text);
            Assert.Contains("Создание заметки", history[1].DiffSummary);
        }
        finally
        {
            if (File.Exists(dbPath))
            {
                try { File.Delete(dbPath); } catch { }
            }
        }
    }

    [Fact]
    public void NoteHistoryService_RestoreRevision_RestoresContent_CreatesNewVersion_PreservesHistory()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"quicknotes_restore_{Guid.NewGuid():N}.db");
        try
        {
            using var context = new QuickNotesDbContext(dbPath);
            DbInitializer.Initialize(context);

            var service = new NoteHistoryService();

            var tagAlpha = new Tag { Name = "Alpha" };
            var tagBeta = new Tag { Name = "Beta" };
            context.Tags.AddRange(tagAlpha, tagBeta);
            context.SaveChanges();

            // 1. Create note in Version 1
            var note = new Note
            {
                Text = "Original content V1",
                CreatedAt = DateTime.Now,
                UpdatedAt = DateTime.Now
            };
            note.NoteTags.Add(new NoteTag { TagId = tagAlpha.Id, Origin = TagOrigin.Manual, IsSuppressed = false });
            context.Notes.Add(note);
            context.SaveChanges();
            var rev1 = service.SaveSnapshot(context, note);
            Assert.NotNull(rev1);

            // 2. Edit to Version 2
            note.Text = "Modified content V2";
            note.NoteTags.Clear();
            note.NoteTags.Add(new NoteTag { TagId = tagBeta.Id, Origin = TagOrigin.Auto, IsSuppressed = false });
            context.SaveChanges();
            var rev2 = service.SaveSnapshot(context, note);
            Assert.NotNull(rev2);

            // 3. Edit to Version 3
            note.Text = "Final content V3";
            note.NoteTags.First().IsSuppressed = true;
            context.SaveChanges();
            var rev3 = service.SaveSnapshot(context, note);
            Assert.NotNull(rev3);

            Assert.Equal(3, context.NoteRevisions.Count(r => r.NoteId == note.Id));

            // 4. Restore Version 1
            var restored = service.RestoreRevision(context, note.Id, rev1.Id);

            // Note should now have V1 text and tags
            Assert.Equal("Original content V1", restored.Text);
            Assert.Single(restored.NoteTags);
            var restoredTag = restored.NoteTags.First();
            Assert.Equal(tagAlpha.Id, restoredTag.TagId);
            Assert.Equal(TagOrigin.Manual, restoredTag.Origin);
            Assert.False(restoredTag.IsSuppressed);

            // Total revisions in DB must now be 4: rev1, rev2, rev3, and new rev4
            var revisions = context.NoteRevisions.Where(r => r.NoteId == note.Id).OrderBy(r => r.Id).ToList();
            Assert.Equal(4, revisions.Count);
            Assert.Equal(rev1.Id, revisions[0].Id);
            Assert.Equal(rev2.Id, revisions[1].Id);
            Assert.Equal(rev3.Id, revisions[2].Id);

            var rev4 = revisions[3];
            Assert.Equal("Original content V1", rev4.Text);
            var rev4Tags = NoteHistoryService.DeserializeTags(rev4.TagsJson);
            Assert.Single(rev4Tags);
            Assert.Equal(tagAlpha.Id, rev4Tags[0].TagId);
            Assert.Equal(TagOrigin.Manual, rev4Tags[0].Origin);
            Assert.False(rev4Tags[0].IsSuppressed);
        }
        finally
        {
            if (File.Exists(dbPath))
            {
                try { File.Delete(dbPath); } catch { }
            }
        }
    }

    [Fact]
    public void NoteEditorViewModel_EmptyHistory_HandlesGracefullyWithoutCrash()
    {
        RunInSta(() =>
        {
            var tagDetection = new TagDetectionService();
            var tags = new List<Tag> { new() { Id = 1, Name = "Test" } };

            // New note (NoteId is null)
            using var vmNew = new NoteEditorViewModel(tagDetection, tags, draftJournalService: NoOpDraftJournalService.Instance);
            Assert.Null(vmNew.NoteId);
            Assert.Empty(vmNew.Revisions);
            Assert.False(vmNew.HasRevisions);
            Assert.False(vmNew.CanRestore);
            Assert.Equal("🕒 История", vmNew.HistoryButtonText);

            // Toggle history panel
            vmNew.ToggleHistoryCommand.Execute(null);
            Assert.True(vmNew.IsHistoryOpen);
            Assert.Equal("🕒 Скрыть историю", vmNew.HistoryButtonText);

            // Calling RestoreVersionCommand with no selected revision should not throw
            Assert.False(vmNew.RestoreVersionCommand.CanExecute(null));
        });
    }

    [Fact]
    public void NoteEditorViewModel_WithExistingNoteAndHistory_RestoresCorrectly()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"quicknotes_vmhistory_{Guid.NewGuid():N}.db");
        try
        {
            using (var context = new QuickNotesDbContext(dbPath))
            {
                DbInitializer.Initialize(context);

                var tag = new Tag { Name = "Backend" };
                context.Tags.Add(tag);
                context.SaveChanges();

                var note = new Note
                {
                    Text = "Initial V1 text",
                    CreatedAt = DateTime.Now,
                    UpdatedAt = DateTime.Now
                };
                note.NoteTags.Add(new NoteTag { TagId = tag.Id, Origin = TagOrigin.Manual, IsSuppressed = false });
                context.Notes.Add(note);
                context.SaveChanges();

                var service = new NoteHistoryService();
                service.SaveSnapshot(context, note);

                note.Text = "Updated V2 text";
                context.SaveChanges();
                service.SaveSnapshot(context, note);
            }

            RunInSta(() =>
            {
                var historyService = new NoteHistoryService();
                var tagDetection = new TagDetectionService();
                Note existingNote;
                List<Tag> allTags;

                using (var db = new QuickNotesDbContext(dbPath))
                {
                    allTags = db.Tags.ToList();
                    existingNote = db.Notes.Include(n => n.NoteTags).ThenInclude(nt => nt.Tag).First();
                }

                using var vm = new NoteEditorViewModel(
                    tagDetection,
                    allTags,
                    existingNote: existingNote,
                    noteHistoryService: historyService,
                    contextFactory: () => new QuickNotesDbContext(dbPath),
                    draftJournalService: NoOpDraftJournalService.Instance);

                // Should have 2 revisions loaded
                Assert.Equal(2, vm.Revisions.Count);
                Assert.True(vm.HasRevisions);
                Assert.Equal("🕒 История (2)", vm.HistoryButtonText);
                Assert.NotNull(vm.SelectedRevision);
                Assert.True(vm.CanRestore);

                // Select the older revision (V1)
                var olderRev = vm.Revisions.Last();
                Assert.Equal("Initial V1 text", olderRev.Text);

                // Restore V1
                vm.RestoreVersion(olderRev);

                Assert.True(vm.HasRestoredVersion);
                Assert.Equal("Initial V1 text", vm.Text);
                Assert.Single(vm.ActiveTags);
                Assert.Equal("Backend", vm.ActiveTags[0].TagName);
                Assert.Equal(TagOrigin.Manual, vm.ActiveTags[0].Origin);

                // Now history should have 3 revisions (new current version added)
                Assert.Equal(3, vm.Revisions.Count);
                Assert.Equal("Initial V1 text", vm.Revisions.First().Text);
            });
        }
        finally
        {
            if (File.Exists(dbPath))
            {
                try { File.Delete(dbPath); } catch { }
            }
        }
    }

    [Fact]
    public void AllSavePaths_GenerateHistoryRevisions()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"quicknotes_allpaths_{Guid.NewGuid():N}.db");
        try
        {
            RunInSta(() =>
            {
                var historyService = new NoteHistoryService();
                var tagDetection = new TagDetectionService();
                var searchService = new SearchService();
                var settingsService = new SettingsService();
                using var hotkeyService = new GlobalHotkeyService((_, _, _, _) => true, (_, _) => true, IntPtr.Zero);
                var clipboard = new ClipboardCaptureService();
                using var tray = new TrayIconService();
                var backup = new BackupService();

                using (var db = new QuickNotesDbContext(dbPath))
                {
                    DbInitializer.Initialize(db);
                    db.Tags.Add(new Tag { Name = "PathTag" });
                    db.SaveChanges();
                }

                using var mainVm = MainViewModelTestComposition.Create(
                    () => new QuickNotesDbContext(dbPath),
                    tagDetection,
                    searchService,
                    settingsService,
                    hotkeyService,
                    clipboard,
                    tray,
                    backup,
                    noteHistoryService: historyService,
                    draftJournalService: NoOpDraftJournalService.Instance);

                // Path 1: Instant save
                var capture = new CapturedNoteContext
                {
                    Text = "Instant Note Content",
                    CapturedAt = DateTime.Now,
                    ProcessName = "chrome.exe",
                    WindowTitle = "Docs",
                    Url = "https://example.com"
                };
                var instantNote = mainVm.SaveInstantNote(capture);
                Assert.True(instantNote.Id > 0);

                using (var db = new QuickNotesDbContext(dbPath))
                {
                    var revs = db.NoteRevisions.Where(r => r.NoteId == instantNote.Id).ToList();
                    Assert.Single(revs);
                    Assert.Equal("Instant Note Content", revs[0].Text);
                }

                // Path 2: New note via editor
                NoteEditorViewModel? capturedEditorVm = null;
                mainVm.RequestOpenNoteEditor += vm =>
                {
                    capturedEditorVm = vm;
                    vm.Text = "New Editor Note Content";
                    vm.SaveCommand.Execute(null);
                    return true;
                };

                mainVm.CreateNewNote();
                Assert.NotNull(capturedEditorVm);

                int newNoteId;
                using (var db = new QuickNotesDbContext(dbPath))
                {
                    var newNote = db.Notes.FirstOrDefault(n => n.Text == "New Editor Note Content");
                    Assert.NotNull(newNote);
                    newNoteId = newNote.Id;

                    var revs = db.NoteRevisions.Where(r => r.NoteId == newNoteId).ToList();
                    Assert.Single(revs);
                    Assert.Equal("New Editor Note Content", revs[0].Text);
                }

                // Path 3: Editing existing note
                var card = new NoteCardViewModel(new Note { Id = newNoteId, Text = "New Editor Note Content" });
                mainVm.RequestOpenNoteEditor += vm =>
                {
                    vm.Text = "New Editor Note Content Edited";
                    vm.SaveCommand.Execute(null);
                    return true;
                };

                mainVm.EditNote(card);

                using (var db = new QuickNotesDbContext(dbPath))
                {
                    var revs = db.NoteRevisions.Where(r => r.NoteId == newNoteId).OrderBy(r => r.Id).ToList();
                    Assert.Equal(2, revs.Count);
                    Assert.Equal("New Editor Note Content Edited", revs[1].Text);
                }

                // Path 4: Restoration
                using (var db = new QuickNotesDbContext(dbPath))
                {
                    var revs = db.NoteRevisions.Where(r => r.NoteId == newNoteId).OrderBy(r => r.Id).ToList();
                    int initialRevId = revs[0].Id;

                    historyService.RestoreRevision(db, newNoteId, initialRevId);

                    var noteAfterRestore = db.Notes.Find(newNoteId);
                    Assert.NotNull(noteAfterRestore);
                    Assert.Equal("New Editor Note Content", noteAfterRestore.Text);

                    var allRevsAfterRestore = db.NoteRevisions.Where(r => r.NoteId == newNoteId).OrderBy(r => r.Id).ToList();
                    Assert.Equal(3, allRevsAfterRestore.Count);
                    Assert.Equal("New Editor Note Content", allRevsAfterRestore[2].Text);
                }
            });
        }
        finally
        {
            if (File.Exists(dbPath))
            {
                try { File.Delete(dbPath); } catch { }
            }
        }
    }

    [Fact]
    public void RestoreRevision_PreservesTagOriginAndSuppression_Accurately()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"quicknotes_tag_origin_{Guid.NewGuid():N}.db");
        try
        {
            using var context = new QuickNotesDbContext(dbPath);
            DbInitializer.Initialize(context);

            var service = new NoteHistoryService();

            var tagAuto = new Tag { Name = "AutoActive" };
            var tagManual = new Tag { Name = "ManualActive" };
            var tagSuppressed = new Tag { Name = "AutoSuppressed" };
            var tagExtra = new Tag { Name = "ExtraNew" };
            context.Tags.AddRange(tagAuto, tagManual, tagSuppressed, tagExtra);
            context.SaveChanges();

            // 1. Initial version with mix of Manual, Auto, and Suppressed tags
            var note = new Note
            {
                Text = "Note with complex tags",
                CreatedAt = DateTime.Now,
                UpdatedAt = DateTime.Now
            };
            note.NoteTags.Add(new NoteTag { TagId = tagAuto.Id, Origin = TagOrigin.Auto, IsSuppressed = false });
            note.NoteTags.Add(new NoteTag { TagId = tagManual.Id, Origin = TagOrigin.Manual, IsSuppressed = false });
            note.NoteTags.Add(new NoteTag { TagId = tagSuppressed.Id, Origin = TagOrigin.Auto, IsSuppressed = true });
            context.Notes.Add(note);
            context.SaveChanges();

            var rev1 = service.SaveSnapshot(context, note);
            Assert.NotNull(rev1);

            // 2. Modify note tags completely
            note.NoteTags.Clear();
            note.NoteTags.Add(new NoteTag { TagId = tagExtra.Id, Origin = TagOrigin.Manual, IsSuppressed = false });
            context.SaveChanges();

            var rev2 = service.SaveSnapshot(context, note);
            Assert.NotNull(rev2);

            // 3. Restore Version 1
            var restored = service.RestoreRevision(context, note.Id, rev1.Id);

            // Verify all 3 tags are restored with exact origin and suppression
            Assert.Equal(3, restored.NoteTags.Count);

            var tAuto = restored.NoteTags.FirstOrDefault(nt => nt.TagId == tagAuto.Id);
            Assert.NotNull(tAuto);
            Assert.Equal(TagOrigin.Auto, tAuto.Origin);
            Assert.False(tAuto.IsSuppressed);

            var tManual = restored.NoteTags.FirstOrDefault(nt => nt.TagId == tagManual.Id);
            Assert.NotNull(tManual);
            Assert.Equal(TagOrigin.Manual, tManual.Origin);
            Assert.False(tManual.IsSuppressed);

            var tSuppressed = restored.NoteTags.FirstOrDefault(nt => nt.TagId == tagSuppressed.Id);
            Assert.NotNull(tSuppressed);
            Assert.Equal(TagOrigin.Auto, tSuppressed.Origin);
            Assert.True(tSuppressed.IsSuppressed);

            // tagExtra should not be present
            Assert.DoesNotContain(restored.NoteTags, nt => nt.TagId == tagExtra.Id);
        }
        finally
        {
            if (File.Exists(dbPath))
            {
                try { File.Delete(dbPath); } catch { }
            }
        }
    }

    [Fact]
    public void NoteEditorViewModel_RestoreVersion_SetsActiveAndSuppressedTagsAccurately()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"quicknotes_vm_tags_{Guid.NewGuid():N}.db");
        try
        {
            int noteId;
            int rev1Id;

            using (var context = new QuickNotesDbContext(dbPath))
            {
                DbInitializer.Initialize(context);

                var tag1 = new Tag { Name = "TagOne" };
                var tag2 = new Tag { Name = "TagTwo" };
                context.Tags.AddRange(tag1, tag2);
                context.SaveChanges();

                var note = new Note
                {
                    Text = "Initial Text",
                    CreatedAt = DateTime.Now,
                    UpdatedAt = DateTime.Now
                };
                note.NoteTags.Add(new NoteTag { TagId = tag1.Id, Origin = TagOrigin.Manual, IsSuppressed = false });
                note.NoteTags.Add(new NoteTag { TagId = tag2.Id, Origin = TagOrigin.Auto, IsSuppressed = true });
                context.Notes.Add(note);
                context.SaveChanges();

                noteId = note.Id;

                var service = new NoteHistoryService();
                var rev1 = service.SaveSnapshot(context, note);
                Assert.NotNull(rev1);
                rev1Id = rev1.Id;

                note.Text = "Modified Text";
                note.NoteTags.Clear();
                context.SaveChanges();
                service.SaveSnapshot(context, note);
            }

            RunInSta(() =>
            {
                var historyService = new NoteHistoryService();
                var tagDetection = new TagDetectionService();
                Note note;
                List<Tag> allTags;

                using (var db = new QuickNotesDbContext(dbPath))
                {
                    allTags = db.Tags.ToList();
                    note = db.Notes.Include(n => n.NoteTags).ThenInclude(nt => nt.Tag).First();
                }

                using var vm = new NoteEditorViewModel(
                    tagDetection,
                    allTags,
                    existingNote: note,
                    noteHistoryService: historyService,
                    contextFactory: () => new QuickNotesDbContext(dbPath),
                    draftJournalService: NoOpDraftJournalService.Instance);

                // Current note has no tags in editor
                Assert.Empty(vm.ActiveTags);
                Assert.Empty(vm.SuppressedTagIds);

                // Restore version 1
                var rev1Vm = vm.Revisions.First(r => r.Id == rev1Id);
                vm.RestoreVersion(rev1Vm);

                // Check text
                Assert.Equal("Initial Text", vm.Text);

                // Check active tags
                Assert.Single(vm.ActiveTags);
                Assert.Equal("TagOne", vm.ActiveTags[0].TagName);
                Assert.Equal(TagOrigin.Manual, vm.ActiveTags[0].Origin);
                Assert.False(vm.ActiveTags[0].IsSuppressed);

                // Check suppressed tags
                var tag2 = allTags.First(t => t.Name == "TagTwo");
                Assert.Single(vm.SuppressedTagIds);
                Assert.Contains(tag2.Id, vm.SuppressedTagIds);

                Assert.True(vm.HasRestoredVersion);
            });
        }
        finally
        {
            if (File.Exists(dbPath))
            {
                try { File.Delete(dbPath); } catch { }
            }
        }
    }

    [Fact]
    public void MainViewModel_SingleRescan_CreatesHistorySnapshotForTagChanges()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"quicknotes_single_rescan_history_{Guid.NewGuid():N}.db");
        var rulesPath = Path.Combine(Path.GetTempPath(), $"quicknotes_single_rescan_rules_{Guid.NewGuid():N}.json");
        try
        {
            RunInSta(() =>
            {
                using (var db = new QuickNotesDbContext(dbPath))
                {
                    DbInitializer.Initialize(db);
                    var tag = new Tag { Name = "Deploy" };
                    db.Tags.Add(tag);
                    db.SaveChanges();

                    var note = new Note
                    {
                        Text = "Deploy production",
                        CreatedAt = DateTime.Now,
                        UpdatedAt = DateTime.Now
                    };
                    note.NoteTags.Add(new NoteTag
                    {
                        TagId = tag.Id,
                        Origin = TagOrigin.Auto,
                        IsSuppressed = false
                    });
                    db.Notes.Add(note);
                    db.SaveChanges();
                    new NoteHistoryService().SaveSnapshot(db, note);
                }

                var ruleStore = new TagRuleService(rulesPath);
                ruleStore.SaveRule(new TagRule
                {
                    TagId = 1,
                    ExcludedTerms = new List<string> { "production" }
                });

                var history = new NoteHistoryService();
                using var hotkey = new GlobalHotkeyService((_, _, _, _) => true, (_, _) => true, IntPtr.Zero);
                using var tray = new TrayIconService();
                using var mainVm = MainViewModelTestComposition.Create(
                    () => new QuickNotesDbContext(dbPath),
                    new TagDetectionService(ruleStore),
                    new SearchService(),
                    new SettingsService(),
                    hotkey,
                    new ClipboardCaptureService(),
                    tray,
                    new BackupService(),
                    tagRuleService: ruleStore,
                    noteHistoryService: history,
                    draftJournalService: NoOpDraftJournalService.Instance);

                mainVm.RescanNote(new NoteCardViewModel(new Note { Id = 1 }));

                using var verify = new QuickNotesDbContext(dbPath);
                var savedNote = verify.Notes.Include(n => n.NoteTags).Single(n => n.Id == 1);
                Assert.Empty(savedNote.NoteTags);
                Assert.Equal(2, verify.NoteRevisions.Count(r => r.NoteId == 1));
            });
        }
        finally
        {
            if (File.Exists(dbPath))
            {
                try { File.Delete(dbPath); } catch { }
            }
            if (File.Exists(rulesPath))
            {
                try { File.Delete(rulesPath); } catch { }
            }
        }
    }
}
