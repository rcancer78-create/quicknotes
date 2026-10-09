using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
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
public class NoteTemplateTests : IDisposable
{
    private readonly string _dbPath;
    private readonly QuickNotesDbContext _context;

    public NoteTemplateTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"quicknotes_templates_test_{Guid.NewGuid():N}.db");
        _context = new QuickNotesDbContext(_dbPath);
        DbInitializer.Initialize(_context);
    }

    private readonly List<IDisposable> _disposables = new();

    public void Dispose()
    {
        foreach (var d in _disposables)
        {
            try { d.Dispose(); } catch { }
        }
        _context.Dispose();
        if (File.Exists(_dbPath))
        {
            try { File.Delete(_dbPath); } catch { }
        }
    }

    private QuickNotesDbContext CreateContext() => new QuickNotesDbContext(_dbPath);

    private class FixedDateTimeProvider : IDateTimeProvider
    {
        public DateTime Now { get; }
        public DateTime UtcNow { get; }

        public FixedDateTimeProvider(DateTime fixedNow)
        {
            Now = fixedNow;
            UtcNow = fixedNow.ToUniversalTime();
        }
    }

    // 1. Schema migration test
    [Fact]
    public void NoteTemplates_SchemaMigration_ExistingDb_TriggersBackupAndAddsTablesAndPreservesData()
    {
        string migrationDbPath = Path.Combine(Path.GetTempPath(), $"migration_test_{Guid.NewGuid():N}.db");
        try
        {
            // 1. Create older database (schema version 3) with existing notes and tags, but without NoteTemplates tables
            using (var conn = new SqliteConnection($"Data Source={migrationDbPath}"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    CREATE TABLE Notes (
                        Id INTEGER PRIMARY KEY AUTOINCREMENT,
                        Text TEXT NOT NULL,
                        CreatedAt TEXT NOT NULL,
                        UpdatedAt TEXT NOT NULL,
                        IsPinned INTEGER NOT NULL DEFAULT 0,
                        IsFavorite INTEGER NOT NULL DEFAULT 0,
                        IsInbox INTEGER NOT NULL DEFAULT 0,
                        DeletedAt TEXT NULL,
                        CapturedAt TEXT NULL,
                        SourceProcessName TEXT NULL,
                        SourceWindowTitle TEXT NULL,
                        SourceUrl TEXT NULL
                    );
                    CREATE TABLE Tags (
                        Id INTEGER PRIMARY KEY AUTOINCREMENT,
                        Name TEXT NOT NULL,
                        ParentTagId INTEGER NULL
                    );
                    CREATE TABLE NoteTags (
                        NoteId INTEGER NOT NULL,
                        TagId INTEGER NOT NULL,
                        Origin INTEGER NOT NULL,
                        IsSuppressed INTEGER NOT NULL DEFAULT 0,
                        PRIMARY KEY (NoteId, TagId)
                    );
                    CREATE TABLE NoteRevisions (
                        Id INTEGER PRIMARY KEY AUTOINCREMENT,
                        NoteId INTEGER NOT NULL,
                        CreatedAt TEXT NOT NULL,
                        Text TEXT NOT NULL,
                        TagsJson TEXT NOT NULL
                    );
                    CREATE TABLE NoteAttachments (
                        Id INTEGER PRIMARY KEY AUTOINCREMENT,
                        NoteId INTEGER NOT NULL,
                        OriginalFileName TEXT NOT NULL,
                        StoredFileName TEXT NOT NULL,
                        RelativePath TEXT NOT NULL,
                        ContentType TEXT NOT NULL,
                        Size INTEGER NOT NULL,
                        Sha256 TEXT NOT NULL,
                        CreatedAt TEXT NOT NULL
                    );

                    INSERT INTO Tags (Id, Name) VALUES (1, 'Development');
                    INSERT INTO Notes (Id, Text, CreatedAt, UpdatedAt) VALUES (1, 'Legacy Note Content', '2026-09-01T10:00:00', '2026-09-01T10:00:00');
                    INSERT INTO NoteTags (NoteId, TagId, Origin, IsSuppressed) VALUES (1, 1, 1, 0);

                    PRAGMA user_version = 3;
                ";
                cmd.ExecuteNonQuery();
            }

            bool backupTriggered = false;

            // 2. Initialize with DbInitializer
            using (var ctx = new QuickNotesDbContext(migrationDbPath))
            {
                DbInitializer.Initialize(ctx, () => backupTriggered = true);
            }

            // Verify backup was triggered because existing user data was present and NoteTemplates table was missing
            Assert.True(backupTriggered, "Backup should be triggered before upgrading schema for existing database with user data");

            // 3. Verify NoteTemplates and NoteTemplateTags tables and indexes exist
            using (var verifyConn = new SqliteConnection($"Data Source={migrationDbPath}"))
            {
                verifyConn.Open();

                using (var cmd = verifyConn.CreateCommand())
                {
                    cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='NoteTemplates';";
                    long count = Convert.ToInt64(cmd.ExecuteScalar());
                    Assert.Equal(1, count);
                }

                using (var cmd = verifyConn.CreateCommand())
                {
                    cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='NoteTemplateTags';";
                    long count = Convert.ToInt64(cmd.ExecuteScalar());
                    Assert.Equal(1, count);
                }

                // Verify indexes exist
                using (var cmd = verifyConn.CreateCommand())
                {
                    cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name='IX_NoteTemplates_Title';";
                    long count = Convert.ToInt64(cmd.ExecuteScalar());
                    Assert.Equal(1, count);
                }

                Assert.True(DbInitializer.HasUniqueTitleIndex(verifyConn));

                using (var cmd = verifyConn.CreateCommand())
                {
                    cmd.CommandText = "PRAGMA user_version;";
                    long version = Convert.ToInt64(cmd.ExecuteScalar());
                    Assert.Equal(DbInitializer.CurrentSchemaVersion, version);
                }

                // Verify legacy note and tag data preserved
                using (var cmd = verifyConn.CreateCommand())
                {
                    cmd.CommandText = "SELECT Text FROM Notes WHERE Id = 1;";
                    var text = cmd.ExecuteScalar()?.ToString();
                    Assert.Equal("Legacy Note Content", text);
                }

                using (var cmd = verifyConn.CreateCommand())
                {
                    cmd.CommandText = "SELECT Name FROM Tags WHERE Id = 1;";
                    var name = cmd.ExecuteScalar()?.ToString();
                    Assert.Equal("Development", name);
                }
            }
        }
        finally
        {
            if (File.Exists(migrationDbPath))
            {
                try { File.Delete(migrationDbPath); } catch { }
            }
        }
    }

    // 2. CRUD and validation / uniqueness tests
    [Fact]
    public void NoteTemplates_CrudAndValidation_EnforcesLimitsAndUniqueness()
    {
        var service = new NoteTemplateService(CreateContext);

        // A. Empty title
        var resEmpty = service.CreateTemplate("   ", "Some text");
        Assert.False(resEmpty.Success);
        Assert.Contains("не может быть пустым", resEmpty.ErrorMessage);

        // B. Title too long (> 100)
        string longTitle = new string('A', 101);
        var resLongTitle = service.CreateTemplate(longTitle, "Text");
        Assert.False(resLongTitle.Success);
        Assert.Contains("100 символов", resLongTitle.ErrorMessage);

        // C. Text too long (> 50,000)
        string longText = new string('B', 50001);
        var resLongText = service.CreateTemplate("Valid Title", longText);
        Assert.False(resLongText.Success);
        Assert.Contains("50000 символов", resLongText.ErrorMessage);

        // D. Non-existent tag ID
        var resInvalidTag = service.CreateTemplate("Valid Title", "Text", new[] { 99999 });
        Assert.False(resInvalidTag.Success);
        Assert.Contains("не существуют", resInvalidTag.ErrorMessage);

        // E. Successful creation
        var tag1 = new Tag { Name = "Backend" };
        using (var db = CreateContext())
        {
            db.Tags.Add(tag1);
            db.SaveChanges();
        }

        var createRes = service.CreateTemplate(" Daily Plan ", "Tasks for {{date}}", new[] { tag1.Id });
        Assert.True(createRes.Success);
        Assert.NotNull(createRes.Template);
        Assert.Equal("Daily Plan", createRes.Template.Title); // Trimmed
        Assert.Equal("Tasks for {{date}}", createRes.Template.Text);

        int templateId = createRes.Template.Id;

        // F. Case-insensitive uniqueness check
        var dupRes1 = service.CreateTemplate("daily plan", "Another text");
        Assert.False(dupRes1.Success);
        Assert.Contains("уже существует", dupRes1.ErrorMessage);

        var dupRes2 = service.CreateTemplate("  DAILY PLAN  ", "Another text");
        Assert.False(dupRes2.Success);
        Assert.Contains("уже существует", dupRes2.ErrorMessage);

        // G. Update template with same name (allowed for same entity)
        var updateSelf = service.UpdateTemplate(templateId, "Daily Plan", "Updated content", new[] { tag1.Id });
        Assert.True(updateSelf.Success);

        // Create a second template
        var createSecond = service.CreateTemplate("Meeting Template", "Meeting minutes");
        Assert.True(createSecond.Success);

        // Update second template with title of first template -> rejected
        var updateDup = service.UpdateTemplate(createSecond.Template!.Id, "daily plan", "New text");
        Assert.False(updateDup.Success);
        Assert.Contains("уже существует", updateDup.ErrorMessage);

        // Update with valid new title and text
        var updateOk = service.UpdateTemplate(createSecond.Template.Id, "Weekly Report", "Report body");
        Assert.True(updateOk.Success);
        Assert.Equal("Weekly Report", updateOk.Template!.Title);

        // H. Delete template
        bool deleted = service.DeleteTemplate(templateId);
        Assert.True(deleted);

        var fetched = service.GetTemplateById(templateId);
        Assert.Null(fetched);

        bool deleteAgain = service.DeleteTemplate(templateId);
        Assert.False(deleteAgain);
    }

    // 3. Tag relations and cascade on tag delete
    [Fact]
    public void NoteTemplates_TagRelations_PreservesRelationsAndCascadesOnTagDelete()
    {
        var service = new NoteTemplateService(CreateContext);

        Tag t1, t2;
        using (var db = CreateContext())
        {
            t1 = new Tag { Name = "Frontend" };
            t2 = new Tag { Name = "Design" };
            db.Tags.AddRange(t1, t2);
            db.SaveChanges();
        }

        // Create template with both tags and duplicate tag IDs in input
        var createRes = service.CreateTemplate("Design Review", "Review text", new[] { t1.Id, t2.Id, t1.Id });
        Assert.True(createRes.Success);
        int templateId = createRes.Template!.Id;

        // Verify template has 2 distinct tags
        var loaded = service.GetTemplateById(templateId);
        Assert.NotNull(loaded);
        Assert.Equal(2, loaded.TemplateTags.Count);
        Assert.Contains(loaded.TemplateTags, tt => tt.TagId == t1.Id);
        Assert.Contains(loaded.TemplateTags, tt => tt.TagId == t2.Id);

        // Delete tag t2 from DB
        using (var db = CreateContext())
        {
            var tagToDelete = db.Tags.Find(t2.Id);
            Assert.NotNull(tagToDelete);

            // Cascade template tag
            var templateTagsToDelete = db.NoteTemplateTags.Where(tt => tt.TagId == t2.Id).ToList();
            db.NoteTemplateTags.RemoveRange(templateTagsToDelete);
            db.Tags.Remove(tagToDelete);
            db.SaveChanges();
        }

        // Verify template still exists and retains tag t1, while link to t2 was safely removed
        var afterTagDelete = service.GetTemplateById(templateId);
        Assert.NotNull(afterTagDelete);
        Assert.Single(afterTagDelete.TemplateTags);
        Assert.Equal(t1.Id, afterTagDelete.TemplateTags.First().TagId);

        // Delete template -> tag t1 must NOT be deleted
        bool deletedTemplate = service.DeleteTemplate(templateId);
        Assert.True(deletedTemplate);

        using (var db = CreateContext())
        {
            var remainingTag = db.Tags.Find(t1.Id);
            Assert.NotNull(remainingTag);
            Assert.Equal("Frontend", remainingTag.Name);
        }
    }

    // 4. Safe variable substitution with fixed clock and culture
    [Fact]
    public void TemplateExpansionService_AllVariables_SubstitutedWithFixedClockAndSource()
    {
        var fixedTime = new DateTime(2026, 9, 6, 14, 30, 45);
        var clock = new FixedDateTimeProvider(fixedTime);
        var expansion = new TemplateExpansionService(clock);
        var culture = new CultureInfo("ru-RU");

        string template = "Date: {{date}}, Time: {{time}}, DateTime: {{datetime}}, Source: {{source}}";
        string result = expansion.Expand(template, source: "CustomSource", culture: culture);

        string expectedDate = fixedTime.ToString("d", culture);
        string expectedTime = fixedTime.ToString("t", culture);
        string expectedDateTime = fixedTime.ToString("g", culture);

        Assert.Contains($"Date: {expectedDate}", result);
        Assert.Contains($"Time: {expectedTime}", result);
        Assert.Contains($"DateTime: {expectedDateTime}", result);
        Assert.Contains("Source: CustomSource", result);

        // Test case-insensitivity and spaces inside braces: {{ DATE }}, {{  time  }}, {{ Datetime }}
        string looseTemplate = "{{ DATE }} | {{  time   }} | {{ DateTime }} | {{  source  }}";
        string looseResult = expansion.Expand(looseTemplate, culture: culture);

        Assert.Equal($"{expectedDate} | {expectedTime} | {expectedDateTime} | QuickNotes", looseResult);
    }

    // 5. Unknown variables left unchanged
    [Fact]
    public void TemplateExpansionService_UnknownVariables_LeftUnchanged()
    {
        var expansion = new TemplateExpansionService();

        string template = "Hello {{user}}, your code is {{code_42}} and unknown {{foo.bar}}.";
        string result = expansion.Expand(template);

        Assert.Equal("Hello {{user}}, your code is {{code_42}} and unknown {{foo.bar}}.", result);
    }

    // 6. Draft selection: Empty vs Template
    [Fact]
    public void NoteTemplates_DraftSelection_EmptyVsTemplate()
    {
        var fixedTime = new DateTime(2026, 9, 6, 10, 0, 0);
        var expansionService = new TemplateExpansionService(new FixedDateTimeProvider(fixedTime));
        var templateService = new NoteTemplateService(CreateContext);

        Tag workTag;
        using (var db = CreateContext())
        {
            workTag = new Tag { Name = "Work" };
            db.Tags.Add(workTag);
            db.SaveChanges();
        }

        var tRes = templateService.CreateTemplate("Work Log", "Work done on {{date}}:", new[] { workTag.Id });
        Assert.True(tRes.Success);
        var template = templateService.GetTemplateById(tRes.Template!.Id)!;

        var mainVm = CreateTestMainViewModel(templateService, expansionService);

        NoteEditorViewModel? openedVm = null;
        mainVm.RequestOpenNoteEditor += vm =>
        {
            openedVm = vm;
            return false; // User closes without saving
        };

        // A. Create empty note
        mainVm.CreateEmptyNote();
        Assert.NotNull(openedVm);
        Assert.True(string.IsNullOrEmpty(openedVm.Text));
        Assert.Empty(openedVm.ActiveTags);
        Assert.Null(openedVm.NoteId);

        openedVm = null;

        // B. Create note from template
        mainVm.CreateNoteFromTemplate(template);
        Assert.NotNull(openedVm);
        string expectedExpanded = $"Work done on {fixedTime.ToString("d", CultureInfo.CurrentCulture)}:";
        Assert.Equal(expectedExpanded, openedVm.Text);
        Assert.Single(openedVm.ActiveTags);
        Assert.Equal(workTag.Id, openedVm.ActiveTags[0].TagId);
        Assert.Equal(TagOrigin.Manual, openedVm.ActiveTags[0].Origin);
        Assert.Equal("из шаблона", openedVm.ActiveTags[0].Reason);
        Assert.Null(openedVm.NoteId);

        // C. CreateNewNote when RequestTemplateSelection is provided
        openedVm = null;
        mainVm.RequestTemplateSelection = templates => templates.FirstOrDefault(t => t.Title == "Work Log");
        mainVm.CreateNewNote();
        Assert.NotNull(openedVm);
        Assert.Equal(expectedExpanded, openedVm.Text);
    }

    // 7. No save before commit, and saves text and tags on commit
    [Fact]
    public void NoteTemplates_NoSaveBeforeCommit_AndSavesTextAndTagsOnCommit()
    {
        var fixedTime = new DateTime(2026, 9, 6, 12, 0, 0);
        var expansionService = new TemplateExpansionService(new FixedDateTimeProvider(fixedTime));
        var templateService = new NoteTemplateService(CreateContext);

        Tag t1, t2;
        using (var db = CreateContext())
        {
            t1 = new Tag { Name = "BugReport" };
            t2 = new Tag { Name = "Critical" };
            db.Tags.AddRange(t1, t2);
            db.SaveChanges();
        }

        var tRes = templateService.CreateTemplate("Bug Template", "Bug description:\n{{datetime}}\nSource: {{source}}", new[] { t1.Id });
        var template = templateService.GetTemplateById(tRes.Template!.Id)!;

        var mainVm = CreateTestMainViewModel(templateService, expansionService);

        NoteEditorViewModel? openedVm = null;
        mainVm.RequestOpenNoteEditor += vm =>
        {
            openedVm = vm;
            // Simulate user modifying text and adding another tag before saving
            vm.Text += "\nCrash in login module.";
            vm.ActiveTags.Add(new NoteEditorTagItem
            {
                TagId = t2.Id,
                TagName = t2.Name,
                Origin = TagOrigin.Manual,
                Reason = "добавлен вручную"
            });

            // Commit!
            vm.Commit?.Invoke();
            return true;
        };

        // Ensure 0 notes in DB before creation
        using (var db = CreateContext())
        {
            Assert.Equal(0, db.Notes.Count());
        }

        mainVm.CreateNoteFromTemplate(template);

        // Ensure 1 note was saved to DB upon Commit
        using (var db = CreateContext())
        {
            Assert.Equal(1, db.Notes.Count());
            var note = db.Notes.Include(n => n.NoteTags).First();

            Assert.Contains("Bug description:", note.Text);
            Assert.Contains("Crash in login module.", note.Text);
            Assert.Contains("Source: QuickNotes", note.Text);

            Assert.Equal(2, note.NoteTags.Count);
            Assert.Contains(note.NoteTags, nt => nt.TagId == t1.Id);
            Assert.Contains(note.NoteTags, nt => nt.TagId == t2.Id);

            // History revision must be created
            var revision = db.NoteRevisions.FirstOrDefault(r => r.NoteId == note.Id);
            Assert.NotNull(revision);
            Assert.Equal(note.Text, revision.Text);
        }
    }

    // 8. Missing / deleted tag in template is skipped gracefully
    [Fact]
    public void NoteTemplates_DeletedOrMissingTag_SkippedGracefullyWithoutCrashing()
    {
        var templateService = new NoteTemplateService(CreateContext);

        Tag validTag;
        using (var db = CreateContext())
        {
            validTag = new Tag { Name = "ValidTag" };
            db.Tags.Add(validTag);
            db.SaveChanges();
        }

        var tRes = templateService.CreateTemplate("Test Template", "Content text", new[] { validTag.Id });
        var template = templateService.GetTemplateById(tRes.Template!.Id)!;

        // Manually inject a non-existent tagId into the template's join table in memory
        template.TemplateTags.Add(new NoteTemplateTag
        {
            TemplateId = template.Id,
            TagId = 987654 // Missing / deleted tag
        });

        var mainVm = CreateTestMainViewModel(templateService);

        NoteEditorViewModel? capturedVm = null;
        mainVm.RequestOpenNoteEditor += vm =>
        {
            capturedVm = vm;
            return false;
        };

        // Must NOT throw exception
        var exception = Record.Exception(() => mainVm.CreateNoteFromTemplate(template));
        Assert.Null(exception);

        Assert.NotNull(capturedVm);
        Assert.Single(capturedVm.ActiveTags);
        Assert.Equal(validTag.Id, capturedVm.ActiveTags[0].TagId);
        Assert.Equal("ValidTag", capturedVm.ActiveTags[0].TagName);
    }

    // 9. TemplateManagementViewModel Headless CRUD flow
    [Fact]
    public void NoteTemplates_ManagementViewModel_HeadlessCrudFlow()
    {
        var templateService = new NoteTemplateService(CreateContext);
        Tag tag1;
        using (var db = CreateContext())
        {
            tag1 = new Tag { Name = "Productivity" };
            db.Tags.Add(tag1);
            db.SaveChanges();
        }

        var vm = new TemplateManagementViewModel(templateService, () => new List<Tag> { tag1 });

        Assert.True(vm.IsEmpty);
        Assert.False(vm.HasTemplates);

        // A. Create template via dialog simulation
        vm.RequestTemplateEditDialog = editVm =>
        {
            editVm.Title = "Standup Notes";
            editVm.Text = "What did I do yesterday?\nWhat will I do today?";
            editVm.AvailableTags.First().IsSelected = true;
            editVm.SaveCommand.Execute(null);
            return true;
        };

        vm.CreateCommand.Execute(null);

        Assert.False(vm.IsEmpty);
        Assert.True(vm.HasTemplates);
        Assert.Single(vm.Templates);
        Assert.Equal("Standup Notes", vm.Templates[0].Title);
        Assert.True(vm.Templates[0].HasTags);
        Assert.Contains("Productivity", vm.Templates[0].TagsSummary);

        // B. Edit template
        vm.SelectedTemplate = vm.Templates[0];
        vm.RequestTemplateEditDialog = editVm =>
        {
            editVm.Title = "Daily Standup Notes";
            editVm.SaveCommand.Execute(null);
            return true;
        };

        vm.EditCommand.Execute(null);
        Assert.Equal("Daily Standup Notes", vm.SelectedTemplate.Title);

        // C. Delete template with confirmation
        vm.ConfirmDeleteHandler = (msg, title) => true;
        vm.DeleteCommand.Execute(null);

        Assert.True(vm.IsEmpty);
        Assert.Empty(vm.Templates);
    }

    // 10. TagMerge safely migrates NoteTemplateTags
    [Fact]
    public void NoteTemplates_TagMerge_SafelyMigratesTemplateTags()
    {
        var templateService = new NoteTemplateService(CreateContext);
        var mergeService = new TagMergeService();

        Tag sourceTag, targetTag;
        using (var db = CreateContext())
        {
            sourceTag = new Tag { Name = "OldTag" };
            targetTag = new Tag { Name = "NewTag" };
            db.Tags.AddRange(sourceTag, targetTag);
            db.SaveChanges();
        }

        var tRes = templateService.CreateTemplate("Tagged Template", "Template content", new[] { sourceTag.Id });
        Assert.True(tRes.Success);
        int templateId = tRes.Template!.Id;

        // Perform merge
        using (var db = CreateContext())
        {
            var mergeResult = mergeService.MergeTags(db, sourceTag.Id, targetTag.Id);
            Assert.True(mergeResult.Success);
        }

        // Verify template now references targetTag
        var loaded = templateService.GetTemplateById(templateId);
        Assert.NotNull(loaded);
        Assert.Single(loaded.TemplateTags);
        Assert.Equal(targetTag.Id, loaded.TemplateTags.First().TagId);
    }

    // 11. Normalization and Daily Plan vs daily plan test
    [Fact]
    public void NoteTemplates_WhitespaceNormalization_DailyPlanVsDailyPlan()
    {
        // 1. Static normalization check
        Assert.Equal("Daily Plan", NoteTemplateService.NormalizeTitle("  Daily   Plan  "));
        Assert.Equal("Daily Plan Next", NoteTemplateService.NormalizeTitle("Daily\t\tPlan\r\nNext"));
        Assert.Equal(string.Empty, NoteTemplateService.NormalizeTitle("   \t\r\n   "));

        var service = new NoteTemplateService(CreateContext);

        // 2. Create template with multiple whitespace characters
        var res1 = service.CreateTemplate("  Daily   Plan  ", "Text 1");
        Assert.True(res1.Success);
        Assert.NotNull(res1.Template);
        Assert.Equal("Daily Plan", res1.Template.Title);

        int firstId = res1.Template.Id;

        // 3. Validate against lowercase "daily plan"
        var valErr1 = service.Validate("daily plan", "Text 2");
        Assert.NotNull(valErr1);
        Assert.Contains("уже существует", valErr1);

        // 4. Create against "daily plan" fails
        var resDup1 = service.CreateTemplate("daily plan", "Text 2");
        Assert.False(resDup1.Success);
        Assert.Contains("уже существует", resDup1.ErrorMessage);

        // 5. Create against "  DAILY    PLAN  " fails
        var resDup2 = service.CreateTemplate("  DAILY    PLAN  ", "Text 3");
        Assert.False(resDup2.Success);
        Assert.Contains("уже существует", resDup2.ErrorMessage);

        // 6. Create another valid template
        var resSecond = service.CreateTemplate("Meeting Notes", "Notes text");
        Assert.True(resSecond.Success);
        int secondId = resSecond.Template!.Id;

        // 7. Updating second template to "Daily    Plan" fails
        var resUpdDup = service.UpdateTemplate(secondId, "  Daily    Plan  ", "Updated");
        Assert.False(resUpdDup.Success);
        Assert.Contains("уже существует", resUpdDup.ErrorMessage);

        // 8. Updating first template itself with different whitespace succeeds and normalizes
        var resUpdSelf = service.UpdateTemplate(firstId, "   Daily     Plan   ", "Updated Body");
        Assert.True(resUpdSelf.Success);
        Assert.Equal("Daily Plan", resUpdSelf.Template!.Title);
    }

    // 12. Database level unique constraint test (NOCASE) and concurrency handling
    [Fact]
    public void NoteTemplates_DatabaseUniqueConstraintAndConcurrency_RejectsDuplicateTitles()
    {
        var service = new NoteTemplateService(CreateContext);

        var createRes = service.CreateTemplate("Concurrency Test", "Original text");
        Assert.True(createRes.Success);

        // A. Direct database insert bypassing service Validate to test SQLite/EF unique constraint
        using (var directDb = CreateContext())
        {
            var duplicate = new NoteTemplate
            {
                Title = "concurrency test", // Same title differing only by case
                Text = "Bypassing validation text",
                CreatedAt = DateTime.Now,
                UpdatedAt = DateTime.Now
            };

            directDb.NoteTemplates.Add(duplicate);
            var ex = Assert.Throws<DbUpdateException>(() => directDb.SaveChanges());
            Assert.True(NoteTemplateService.IsUniqueConstraintViolation(ex), "Exception should be recognized as a unique constraint violation");
        }

        // B. NoteTemplateService catching DB unique constraint violation gracefully
        Assert.True(NoteTemplateService.IsUniqueConstraintViolation(
            new Microsoft.Data.Sqlite.SqliteException("SQLite Error 19: 'UNIQUE constraint failed: NoteTemplates.Title'.", 19)));
    }

    // 13. Intermediate schema migration: missing join table, non-unique index, conflicting data
    [Fact]
    public void NoteTemplates_IntermediateSchemaMigration_MissingJoinTableAndNonUniqueIndex_SafelyMigratesWithoutDataLoss()
    {
        string migrationDbPath = Path.Combine(Path.GetTempPath(), $"intermediate_stage19_{Guid.NewGuid():N}.db");
        try
        {
            // 1. Create older database representing intermediate stage 19:
            // - Has Notes, Tags, NoteTags, NoteRevisions, NoteAttachments
            // - Has NoteTemplates with non-unique index and NO NOCASE
            // - MISSING NoteTemplateTags join-table!
            // - Has conflicting duplicate templates
            using (var conn = new SqliteConnection($"Data Source={migrationDbPath}"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    CREATE TABLE Notes (
                        Id INTEGER PRIMARY KEY AUTOINCREMENT,
                        Text TEXT NOT NULL,
                        CreatedAt TEXT NOT NULL,
                        UpdatedAt TEXT NOT NULL,
                        IsPinned INTEGER NOT NULL DEFAULT 0,
                        IsFavorite INTEGER NOT NULL DEFAULT 0,
                        IsInbox INTEGER NOT NULL DEFAULT 0,
                        DeletedAt TEXT NULL,
                        CapturedAt TEXT NULL,
                        SourceProcessName TEXT NULL,
                        SourceWindowTitle TEXT NULL,
                        SourceUrl TEXT NULL
                    );
                    CREATE TABLE Tags (
                        Id INTEGER PRIMARY KEY AUTOINCREMENT,
                        Name TEXT NOT NULL,
                        ParentTagId INTEGER NULL
                    );
                    CREATE TABLE NoteTags (
                        NoteId INTEGER NOT NULL,
                        TagId INTEGER NOT NULL,
                        Origin INTEGER NOT NULL,
                        IsSuppressed INTEGER NOT NULL DEFAULT 0,
                        PRIMARY KEY (NoteId, TagId)
                    );
                    CREATE TABLE NoteRevisions (
                        Id INTEGER PRIMARY KEY AUTOINCREMENT,
                        NoteId INTEGER NOT NULL,
                        CreatedAt TEXT NOT NULL,
                        Text TEXT NOT NULL,
                        TagsJson TEXT NOT NULL
                    );
                    CREATE TABLE NoteAttachments (
                        Id INTEGER PRIMARY KEY AUTOINCREMENT,
                        NoteId INTEGER NOT NULL,
                        OriginalFileName TEXT NOT NULL,
                        StoredFileName TEXT NOT NULL,
                        RelativePath TEXT NOT NULL,
                        ContentType TEXT NOT NULL,
                        Size INTEGER NOT NULL,
                        Sha256 TEXT NOT NULL,
                        CreatedAt TEXT NOT NULL
                    );

                    -- Intermediate stage 19: NoteTemplates exists, but NO NoteTemplateTags and index is NOT unique!
                    CREATE TABLE NoteTemplates (
                        Id INTEGER PRIMARY KEY AUTOINCREMENT,
                        Title TEXT NOT NULL,
                        Text TEXT NOT NULL,
                        CreatedAt TEXT NOT NULL,
                        UpdatedAt TEXT NOT NULL
                    );
                    CREATE INDEX IX_NoteTemplates_Title ON NoteTemplates(Title);

                    INSERT INTO Tags (Id, Name) VALUES (1, 'Work');
                    INSERT INTO Notes (Id, Text, CreatedAt, UpdatedAt) VALUES (1, 'User Note', '2026-09-01T10:00:00', '2026-09-01T10:00:00');
                    INSERT INTO NoteTags (NoteId, TagId, Origin, IsSuppressed) VALUES (1, 1, 1, 0);

                    -- Insert conflicting templates to verify NO data is lost and conflicts are disambiguated safely
                    INSERT INTO NoteTemplates (Id, Title, Text, CreatedAt, UpdatedAt) VALUES (1, 'Daily   Plan', 'Text for plan 1', '2026-09-01T10:00:00', '2026-09-01T10:00:00');
                    INSERT INTO NoteTemplates (Id, Title, Text, CreatedAt, UpdatedAt) VALUES (2, 'daily plan', 'Text for plan 2', '2026-09-01T10:00:00', '2026-09-01T10:00:00');
                    INSERT INTO NoteTemplates (Id, Title, Text, CreatedAt, UpdatedAt) VALUES (3, 'DAILY PLAN', 'Text for plan 3', '2026-09-01T10:00:00', '2026-09-01T10:00:00');
                    INSERT INTO NoteTemplates (Id, Title, Text, CreatedAt, UpdatedAt) VALUES (4, 'Weekly Report', 'Text for report', '2026-09-01T10:00:00', '2026-09-01T10:00:00');

                    PRAGMA user_version = 3;
                ";
                cmd.ExecuteNonQuery();
            }

            bool backupTriggered = false;

            // 2. Initialize with DbInitializer
            using (var ctx = new QuickNotesDbContext(migrationDbPath))
            {
                DbInitializer.Initialize(ctx, () => backupTriggered = true);
            }

            Assert.True(backupTriggered, "Backup should be triggered before upgrading intermediate schema");

            // 3. Verify migration results
            using (var verifyConn = new SqliteConnection($"Data Source={migrationDbPath}"))
            {
                verifyConn.Open();

                // Both tables must exist
                Assert.True(DbInitializer.TableExists(verifyConn, "NoteTemplates"));
                Assert.True(DbInitializer.TableExists(verifyConn, "NoteTemplateTags"));

                // Unique title index must exist and be unique
                Assert.True(DbInitializer.HasUniqueTitleIndex(verifyConn));

                // user_version must be updated
                using (var verCmd = verifyConn.CreateCommand())
                {
                    verCmd.CommandText = "PRAGMA user_version;";
                    long version = Convert.ToInt64(verCmd.ExecuteScalar());
                    Assert.Equal(DbInitializer.CurrentSchemaVersion, version);
                }

                // All 4 templates must still exist (no rows lost, no texts overwritten)
                using (var countCmd = verifyConn.CreateCommand())
                {
                    countCmd.CommandText = "SELECT COUNT(*) FROM NoteTemplates;";
                    long count = Convert.ToInt64(countCmd.ExecuteScalar());
                    Assert.Equal(4, count);
                }

                // Verify specific rows and texts
                using (var queryCmd = verifyConn.CreateCommand())
                {
                    queryCmd.CommandText = "SELECT Id, Title, Text FROM NoteTemplates ORDER BY Id ASC;";
                    using var reader = queryCmd.ExecuteReader();

                    Assert.True(reader.Read());
                    Assert.Equal(1, reader.GetInt32(0));
                    Assert.Equal("Daily Plan", reader.GetString(1)); // Trimmed and collapsed
                    Assert.Equal("Text for plan 1", reader.GetString(2));

                    Assert.True(reader.Read());
                    Assert.Equal(2, reader.GetInt32(0));
                    Assert.Contains("daily plan (дубликат 2)", reader.GetString(1));
                    Assert.Equal("Text for plan 2", reader.GetString(2));

                    Assert.True(reader.Read());
                    Assert.Equal(3, reader.GetInt32(0));
                    Assert.Contains("DAILY PLAN (дубликат 3)", reader.GetString(1));
                    Assert.Equal("Text for plan 3", reader.GetString(2));

                    Assert.True(reader.Read());
                    Assert.Equal(4, reader.GetInt32(0));
                    Assert.Equal("Weekly Report", reader.GetString(1));
                    Assert.Equal("Text for report", reader.GetString(2));
                }

                // Check diagnostics were produced
                Assert.Contains(DbInitializer.LastMigrationDiagnostics, d => d.Contains("конфликт названий"));
            }
        }
        finally
        {
            if (File.Exists(migrationDbPath))
            {
                try { File.Delete(migrationDbPath); } catch { }
            }
        }
    }

    // 14. WindowOwnerResolver headless tests
    [Fact]
    public void WindowOwnerResolver_HeadlessOwnerResolution_SelectsSettingsWindowOverDisabledMainWindow()
    {
        // 1. Setup: MainWindow is disabled (modal SettingsWindow is open above it)
        var mainDesc = new WindowDescriptor("QuickNotes — Быстрые заметки", IsActive: false, IsVisible: true, IsEnabled: false, IsMainWindow: true);
        var settingsDesc = new WindowDescriptor("Настройки", IsActive: true, IsVisible: true, IsEnabled: true, IsMainWindow: false);

        var resolved = WindowOwnerResolver.ResolveOwnerDescriptor(new[] { mainDesc, settingsDesc }, mainDesc);
        Assert.NotNull(resolved);
        Assert.Equal("Настройки", resolved.Title);

        // 2. Setup: IsActive is false on all (e.g. during focus transition), but MainWindow is disabled by modal SettingsWindow
        var settingsInactiveDesc = new WindowDescriptor("Настройки", IsActive: false, IsVisible: true, IsEnabled: true, IsMainWindow: false);
        var resolved2 = WindowOwnerResolver.ResolveOwnerDescriptor(new[] { mainDesc, settingsInactiveDesc }, mainDesc);
        Assert.NotNull(resolved2);
        Assert.Equal("Настройки", resolved2.Title);

        // 3. Setup: Only MainWindow is open, active, visible and enabled
        var singleMain = new WindowDescriptor("QuickNotes", IsActive: true, IsVisible: true, IsEnabled: true, IsMainWindow: true);
        var resolvedMain = WindowOwnerResolver.ResolveOwnerDescriptor(new[] { singleMain }, singleMain);
        Assert.NotNull(resolvedMain);
        Assert.Equal("QuickNotes", resolvedMain.Title);

        // 4. Setup: Empty or null collections resolve fallback safely without throwing
        var resolvedEmpty = WindowOwnerResolver.ResolveOwnerDescriptor(Array.Empty<WindowDescriptor>(), singleMain);
        Assert.Equal("QuickNotes", resolvedEmpty?.Title);

        var resolvedNull = WindowOwnerResolver.ResolveOwnerDescriptor(null, null);
        Assert.Null(resolvedNull);
    }

    // 15. MainViewModel OpenTemplateManagement headless test
    [Fact]
    public void MainViewModel_OpenTemplateManagement_UsesWindowOwnerProviderAndNoExceptions()
    {
        var templateService = new NoteTemplateService(CreateContext);
        var vm = CreateTestMainViewModel(templateService);

        bool dialogRequested = false;
        vm.RequestOpenTemplateManagement = _ =>
        {
            dialogRequested = true;
            return true;
        };

        vm.OpenTemplateManagementCommand.Execute(null);
        Assert.True(dialogRequested);
    }

    private MainViewModel CreateTestMainViewModel(
        INoteTemplateService templateService,
        ITemplateExpansionService? expansionService = null)
    {
        var tagDetectionService = new TagDetectionService();
        var searchService = new SearchService();
        var settingsPath = Path.Combine(Path.GetTempPath(), $"qn_template_test_settings_{Guid.NewGuid():N}.json");
        var settingsService = new SettingsService(settingsPath, _ => { });
        var hotkeyService = new GlobalHotkeyService((_, _, _, _) => true, (_, _) => true, IntPtr.Zero);
        _disposables.Add(hotkeyService);
        var clipboardCaptureService = new ClipboardCaptureService();
        var trayIconService = new TrayIconService();
        _disposables.Add(trayIconService);
        var debouncer = new SearchDebouncer(0);
        _disposables.Add(debouncer);

        var vm = MainViewModelTestComposition.Create(
            CreateContext,
            tagDetectionService,
            searchService,
            settingsService,
            hotkeyService,
            clipboardCaptureService,
            trayIconService,
            searchDebouncer: debouncer,
            noteTemplateService: templateService,
            templateExpansionService: expansionService,
            draftJournalService: NoOpDraftJournalService.Instance);
        _disposables.Add(vm);
        return vm;
    }
}
