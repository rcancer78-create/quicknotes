using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using QuickNotes.App.Data;
using QuickNotes.App.Models;
using QuickNotes.App.Services;
using QuickNotes.App.Services.Sync;
using QuickNotes.App.ViewModels;
using Xunit;

namespace QuickNotes.Tests;

[TestCategory(TestCategories.Integration)]
public class ImportExportTests : IDisposable
{
    private readonly string _tempDirectory;
    private readonly string _dbPath;

    public ImportExportTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "QuickNotes_Test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);
        _dbPath = Path.Combine(_tempDirectory, "test.db");

        using var db = CreateContext();
        DbInitializer.Initialize(db);
    }

    public void Dispose()
    {
        SqliteTestUtil.TryDeleteDirectory(_tempDirectory);
    }

    private QuickNotesDbContext CreateContext()
    {
        return SqliteTestUtil.CreateContext(_dbPath);
    }

    private static ILocalMutationCoordinator Coord() => new LocalMutationCoordinator();

    #region 1. JSON Roundtrip Tests

    [Fact]
    public void JsonRoundtrip_PreservesAllFields_Tags_Hierarchy_AndSynonyms_WithoutLocalPaths()
    {
        using (var db = CreateContext())
        {
            // Setup tag tree: Parent -> Child
            var parentTag = new Tag { Name = "Работа" };
            db.Tags.Add(parentTag);
            db.SaveChanges();

            parentTag.Synonyms.Add(new TagSynonym { TagId = parentTag.Id, Value = "job" });
            parentTag.Synonyms.Add(new TagSynonym { TagId = parentTag.Id, Value = "work" });

            var childTag = new Tag { Name = "Проект А", ParentTagId = parentTag.Id };
            db.Tags.Add(childTag);
            db.SaveChanges();

            // Setup note with all service fields
            var note = new Note
            {
                Text = "Обсуждение архитектуры проекта А",
                CreatedAt = new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc),
                UpdatedAt = new DateTime(2026, 9, 2, 14, 30, 0, DateTimeKind.Utc),
                IsPinned = true,
                IsFavorite = true,
                IsInbox = false,
                SourceProcessName = @"C:\Program Files\App\app.exe", // local path to sanitize
                SourceWindowTitle = "Visual Studio Code",
                SourceUrl = "https://example.com/project",
                CapturedAt = new DateTime(2026, 9, 1, 9, 59, 0, DateTimeKind.Utc)
            };
            db.Notes.Add(note);
            db.SaveChanges();

            db.NoteTags.Add(new NoteTag { NoteId = note.Id, TagId = parentTag.Id, Origin = TagOrigin.Manual });
            db.NoteTags.Add(new NoteTag { NoteId = note.Id, TagId = childTag.Id, Origin = TagOrigin.Auto, IsSuppressed = false });
            db.SaveChanges();
        }

        var exportService = new NoteExportService();
        var jsonFilePath = Path.Combine(_tempDirectory, "export.json");

        // 1. Export to JSON
        using (var db = CreateContext())
        {
            var res = exportService.ExportToJson(db, jsonFilePath);
            Assert.True(res.Success);
            Assert.Equal(1, res.ExportedNotesCount);
        }

        string exportedJson = File.ReadAllText(jsonFilePath);

        // Verify versioned schema
        Assert.Contains("\"schemaVersion\": 1", exportedJson);
        Assert.Contains("\"synonyms\":", exportedJson);
        Assert.Contains("job", exportedJson);
        Assert.Contains("work", exportedJson);

        // Verify local absolute paths are NOT exported
        Assert.DoesNotContain(@"C:\Program Files\App", exportedJson);
        Assert.Contains("app.exe", exportedJson);

        // 2. Import into a fresh clean database
        var targetDbPath = Path.Combine(_tempDirectory, "target.db");
        using (var targetDb = new QuickNotesDbContext(targetDbPath))
        {
            DbInitializer.Initialize(targetDb);
        }

        var importService = new NoteImportService();
        using (var targetDb = new QuickNotesDbContext(targetDbPath))
        {
            var preview = importService.BuildPreviewFromFiles(targetDb, new[] { jsonFilePath });
            Assert.Equal(1, preview.TotalFilesDiscovered);
            Assert.Equal(1, preview.TotalNotesToImport);
            Assert.Equal(0, preview.TotalConflicts);
            Assert.Equal(0, preview.TotalSkippedOrErroneous);

            var execResult = importService.ExecuteImport(targetDb, preview, Coord());
            Assert.True(execResult.Success);
            Assert.Equal(1, execResult.ImportedNotesCount);
        }

        // 3. Verify target database state matches source
        using (var targetDb = new QuickNotesDbContext(targetDbPath))
        {
            var importedNote = targetDb.Notes
                .Include(n => n.NoteTags)
                .ThenInclude(nt => nt.Tag)
                .ThenInclude(t => t.Synonyms)
                .Include(n => n.Revisions)
                .FirstOrDefault();

            Assert.NotNull(importedNote);
            Assert.Equal("Обсуждение архитектуры проекта А", importedNote.Text);
            Assert.True(importedNote.IsPinned);
            Assert.True(importedNote.IsFavorite);
            Assert.False(importedNote.IsInbox);
            Assert.Equal("app.exe", importedNote.SourceProcessName);
            Assert.Equal("Visual Studio Code", importedNote.SourceWindowTitle);
            Assert.Equal("https://example.com/project", importedNote.SourceUrl);

            // Verify tags and hierarchy
            Assert.Equal(2, importedNote.NoteTags.Count);
            var parent = targetDb.Tags.Include(t => t.Children).Include(t => t.Synonyms).FirstOrDefault(t => t.Name == "Работа");
            Assert.NotNull(parent);
            Assert.Null(parent.ParentTagId);
            Assert.Equal(2, parent.Synonyms.Count);

            var child = targetDb.Tags.FirstOrDefault(t => t.Name == "Проект А");
            Assert.NotNull(child);
            Assert.Equal(parent.Id, child.ParentTagId);

            // Verify note revisions snapshot was created
            Assert.Single(importedNote.Revisions);
        }
    }

    #endregion

    #region 2. CSV Escaping Tests

    [Fact]
    public void CsvExport_RFC4180_EscapesCommasQuotesNewlinesAndEmitsBom()
    {
        var exportService = new NoteExportService();
        var csvPath = Path.Combine(_tempDirectory, "test_export.csv");

        using (var db = CreateContext())
        {
            var tag = new Tag { Name = "тег, с запятой" };
            db.Tags.Add(tag);
            db.SaveChanges();

            var note1 = new Note
            {
                Text = "Строка с запятой, и \"кавычками\", а также\r\nпереводом строки.",
                CreatedAt = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc),
                UpdatedAt = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc),
                IsPinned = true,
                SourceProcessName = @"C:\Windows\System32\notepad.exe",
                SourceWindowTitle = "Заголовок, \"тест\""
            };
            db.Notes.Add(note1);
            db.SaveChanges();

            db.NoteTags.Add(new NoteTag { NoteId = note1.Id, TagId = tag.Id, Origin = TagOrigin.Manual });
            db.SaveChanges();

            var res = exportService.ExportToCsv(db, csvPath);
            Assert.True(res.Success);
            Assert.Equal(1, res.ExportedNotesCount);
        }

        // 1. Check UTF-8 BOM
        var bytes = File.ReadAllBytes(csvPath);
        Assert.True(bytes.Length >= 3);
        Assert.Equal(0xEF, bytes[0]);
        Assert.Equal(0xBB, bytes[1]);
        Assert.Equal(0xBF, bytes[2]);

        // 2. Check content
        string csvContent = File.ReadAllText(csvPath, Encoding.UTF8);

        // Header check
        Assert.StartsWith("Id,Text,CreatedAt,UpdatedAt,IsPinned,IsFavorite,IsInbox,Tags,SourceProcessName,SourceWindowTitle,SourceUrl,CapturedAt", csvContent);

        // Double quotes escaped as ""
        Assert.Contains("\"\"кавычками\"\"", csvContent);

        // Path sanitized (no C:\Windows\System32\)
        Assert.DoesNotContain(@"C:\Windows\System32", csvContent);
        Assert.Contains("notepad.exe", csvContent);

        // Window title quotes escaped
        Assert.Contains("\"Заголовок, \"\"тест\"\"\"", csvContent);
    }

    [Theory]
    [InlineData("simple", "simple")]
    [InlineData("comma,value", "\"comma,value\"")]
    [InlineData("quote\"value", "\"quote\"\"value\"")]
    [InlineData("line\nbreak", "\"line\nbreak\"")]
    [InlineData(" spaced ", "\" spaced \"")]
    [InlineData(null, "")]
    [InlineData("", "")]
    public void EscapeCsv_HandlesAllCasesCorrectly(string? input, string expected)
    {
        var result = NoteExportService.EscapeCsv(input);
        Assert.Equal(expected, result);
    }

    #endregion

    #region 3. Markdown Export and Import Tests

    [Fact]
    public void MarkdownExport_CreatesIndividualFiles_SafeNames_FrontMatter_AndAtomicTempPublishing()
    {
        using (var db = CreateContext())
        {
            var tag1 = new Tag { Name = "important" };
            var tag2 = new Tag { Name = "c#" };
            db.Tags.AddRange(tag1, tag2);
            db.SaveChanges();

            var note = new Note
            {
                Text = "# Проект X: Важные задачи\n- [x] Реализовать экспорт\n- [ ] Написать тесты",
                CreatedAt = new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc),
                UpdatedAt = new DateTime(2026, 9, 2, 11, 0, 0, DateTimeKind.Utc),
                IsPinned = true,
                IsFavorite = false,
                IsInbox = true,
                SourceProcessName = "chrome.exe",
                SourceWindowTitle = "GitHub: Issues",
                SourceUrl = "https://github.com"
            };
            db.Notes.Add(note);
            db.SaveChanges();

            db.NoteTags.Add(new NoteTag { NoteId = note.Id, TagId = tag1.Id, Origin = TagOrigin.Manual });
            db.NoteTags.Add(new NoteTag { NoteId = note.Id, TagId = tag2.Id, Origin = TagOrigin.Auto });
            db.SaveChanges();
        }

        var exportService = new NoteExportService();
        var targetExportFolder = Path.Combine(_tempDirectory, "MarkdownExport");

        using (var db = CreateContext())
        {
            var res = exportService.ExportToMarkdown(db, targetExportFolder);
            Assert.True(res.Success);
            Assert.Equal(1, res.ExportedNotesCount);
        }

        // Verify folder exists and contains .md file
        Assert.True(Directory.Exists(targetExportFolder));
        var mdFiles = Directory.GetFiles(targetExportFolder, "*.md");
        Assert.Single(mdFiles);

        var mdFile = mdFiles[0];
        string fileName = Path.GetFileName(mdFile);
        Assert.StartsWith("note_", fileName);
        Assert.EndsWith(".md", fileName);

        // Verify front matter
        string content = File.ReadAllText(mdFile, Encoding.UTF8);
        Assert.StartsWith("---\n", content.Replace("\r\n", "\n"));
        Assert.Contains("id: ", content);
        Assert.Contains("created_at: 2026-09-01T10:00:00", content);
        Assert.Contains("is_pinned: true", content);
        Assert.Contains("is_inbox: true", content);
        Assert.Contains("source_process: chrome.exe", content);
        Assert.Contains("https://github.com", content);
        Assert.Contains("important", content);
        Assert.Contains("c#", content);
        Assert.Contains("# Проект X: Важные задачи", content);
        Assert.True(File.Exists(Path.Combine(targetExportFolder, "manifest.json")));

        using (var db = CreateContext())
        {
            var replaceRes = exportService.ExportToMarkdown(db, targetExportFolder);
            Assert.True(replaceRes.Success, replaceRes.ErrorMessage);
            Assert.Equal(1, replaceRes.ExportedNotesCount);
        }

        Assert.True(Directory.Exists(targetExportFolder));
        Assert.True(File.Exists(Path.Combine(targetExportFolder, "manifest.json")));
        var tempDirs = Directory.GetDirectories(_tempDirectory, ".tmp_export_*");
        Assert.Empty(tempDirs);
    }

    [Fact]
    public void MarkdownImport_ParsesFrontMatter_ReconstructsNoteAndTags()
    {
        var markdown = """
            ---
            id: 77
            created_at: 2026-08-20T08:00:00Z
            updated_at: 2026-08-21T09:30:00Z
            is_pinned: true
            is_favorite: true
            is_inbox: false
            tags:
              - obsidian
              - notes
            source_process: obsidian.exe
            source_window: My Notes Vault
            ---
            # Hello Obsidian
            This note was imported with full front matter.
            """;

        var filePath = Path.Combine(_tempDirectory, "test_note.md");
        File.WriteAllText(filePath, markdown, Encoding.UTF8);

        var importService = new NoteImportService();
        using var db = CreateContext();

        var preview = importService.BuildPreviewFromFiles(db, new[] { filePath });
        Assert.Equal(1, preview.TotalFilesDiscovered);
        Assert.Equal(1, preview.TotalNotesToImport);
        Assert.Equal(0, preview.TotalConflicts);

        var item = Assert.Single(preview.Items);
        Assert.Equal(77, item.OriginalId);
        Assert.Equal("Hello Obsidian", item.Title);
        Assert.True(item.IsPinned);
        Assert.True(item.IsFavorite);
        Assert.Equal(2, item.Tags.Count);

        var execRes = importService.ExecuteImport(db, preview, Coord());
        Assert.True(execRes.Success);
        Assert.Equal(1, execRes.ImportedNotesCount);

        var noteInDb = db.Notes.Include(n => n.NoteTags).ThenInclude(nt => nt.Tag).Include(n => n.Revisions).FirstOrDefault();
        Assert.NotNull(noteInDb);
        Assert.Equal("Hello Obsidian", noteInDb.Title);
        Assert.Contains("This note was imported", noteInDb.Text);
        Assert.Equal("obsidian.exe", noteInDb.SourceProcessName);
        Assert.Equal(2, noteInDb.NoteTags.Count);
        Assert.Single(noteInDb.Revisions);
    }

    [Fact]
    public void MarkdownImport_WithExplicitTitleInFrontMatter_PreservesExplicitTitle()
    {
        var markdown = """
            ---
            title: Custom Obsidian Title
            id: 88
            created_at: 2026-08-20T08:00:00Z
            updated_at: 2026-08-21T09:30:00Z
            ---
            # Heading That Should Not Override Title
            Body of note with explicit title in front matter.
            """;

        var filePath = Path.Combine(_tempDirectory, "explicit_title_note.md");
        File.WriteAllText(filePath, markdown, Encoding.UTF8);

        var importService = new NoteImportService();
        using var db = CreateContext();

        var preview = importService.BuildPreviewFromFiles(db, new[] { filePath });
        var item = Assert.Single(preview.Items);
        Assert.Equal("Custom Obsidian Title", item.Title);

        var execRes = importService.ExecuteImport(db, preview, Coord());
        Assert.True(execRes.Success);

        var noteInDb = db.Notes.FirstOrDefault();
        Assert.NotNull(noteInDb);
        Assert.Equal("Custom Obsidian Title", noteInDb.Title);
    }

    #endregion

    #region 4. Plain-Text Import Tests

    [Fact]
    public void PlainTextImport_ImportsTxtFile_RunsAutoDetection_AndSetsMetadata()
    {
        using (var db = CreateContext())
        {
            db.Tags.Add(new Tag { Name = "C#" });
            db.SaveChanges();
        }

        var textContent = "Заметка о программировании на C# и платформе .NET.";
        var txtFilePath = Path.Combine(_tempDirectory, "quick_idea.txt");
        File.WriteAllText(txtFilePath, textContent, Encoding.UTF8);

        var importService = new NoteImportService();
        var tagDetectionService = new TagDetectionService();
        using var dbContext = CreateContext();

        var preview = importService.BuildPreviewFromFiles(dbContext, new[] { txtFilePath });
        Assert.Equal(1, preview.TotalFilesDiscovered);
        Assert.Equal(1, preview.TotalNotesToImport);
        Assert.Equal(0, preview.TotalConflicts);

        var execRes = importService.ExecuteImport(dbContext, preview, Coord(), tagDetectionService: tagDetectionService);
        Assert.True(execRes.Success);
        Assert.Equal(1, execRes.ImportedNotesCount);

        var note = dbContext.Notes.Include(n => n.NoteTags).ThenInclude(nt => nt.Tag).FirstOrDefault();
        Assert.NotNull(note);
        Assert.Equal(textContent, note.Text);

        // Auto-detected "C#"
        var noteTag = Assert.Single(note.NoteTags);
        Assert.Equal("C#", noteTag.Tag.Name);
        Assert.Equal(TagOrigin.Auto, noteTag.Origin);
    }

    [Fact]
    public void DirectoryImport_RecursivelyFindsMdAndTxtFiles()
    {
        var root = Path.Combine(_tempDirectory, "dir-import-" + Guid.NewGuid().ToString("N"));
        var subDir = Path.Combine(root, "SubFolder");
        Directory.CreateDirectory(subDir);

        File.WriteAllText(Path.Combine(root, "note1.md"), "Markdown note 1", Encoding.UTF8);
        File.WriteAllText(Path.Combine(subDir, "note2.txt"), "Text note in subfolder", Encoding.UTF8);

        var importService = new NoteImportService();
        using var db = CreateContext();

        var preview = importService.BuildPreviewFromDirectory(db, root, recursive: true);
        Assert.Equal(2, preview.TotalFilesDiscovered);
        Assert.Equal(2, preview.TotalNotesToImport);
    }

    #endregion

    #region 5. Invalid Input Tests

    [Fact]
    public void InvalidInput_MalformedFrontMatter_SkipsFileWithDiagnostic_WithoutNoteContent()
    {
        var malformedMd = """
            ---
            id: abc_not_a_number
            created_at: bad_date
            ---
            Sensitive confidential user note text that must never appear in error diagnostics!
            """;

        var filePath = Path.Combine(_tempDirectory, "malformed.md");
        File.WriteAllText(filePath, malformedMd, Encoding.UTF8);

        var importService = new NoteImportService();
        using var db = CreateContext();

        var preview = importService.BuildPreviewFromFiles(db, new[] { filePath });
        Assert.Equal(1, preview.TotalFilesDiscovered);
        Assert.Equal(0, preview.TotalNotesToImport);
        Assert.Equal(1, preview.TotalSkippedOrErroneous);

        var diag = Assert.Single(preview.Diagnostics);
        Assert.Equal("malformed.md", diag.FileName);
        Assert.Contains("Некорректный ID", diag.Reason);

        // Diagnostic must NOT leak sensitive text
        Assert.DoesNotContain("Sensitive confidential", diag.Reason);
        Assert.DoesNotContain("confidential", diag.Reason);
    }

    [Fact]
    public void InvalidInput_UnclosedFrontMatter_SkipsWithDiagnostic()
    {
        var unclosedMd = """
            ---
            id: 123
            title: Unclosed
            No closing dashes here!
            Some body content.
            """;

        var filePath = Path.Combine(_tempDirectory, "unclosed.md");
        File.WriteAllText(filePath, unclosedMd, Encoding.UTF8);

        var importService = new NoteImportService();
        using var db = CreateContext();

        var preview = importService.BuildPreviewFromFiles(db, new[] { filePath });
        Assert.Equal(0, preview.TotalNotesToImport);
        Assert.Equal(1, preview.TotalSkippedOrErroneous);

        var diag = Assert.Single(preview.Diagnostics);
        Assert.Contains("отсутствует закрывающий разделитель", diag.Reason);
    }

    [Fact]
    public void InvalidInput_BinaryFile_SkipsWithDiagnostic()
    {
        var binaryPath = Path.Combine(_tempDirectory, "sample.bin.txt");
        File.WriteAllBytes(binaryPath, new byte[] { 0x00, 0x01, 0x02, 0x03 });

        var importService = new NoteImportService();
        using var db = CreateContext();

        var preview = importService.BuildPreviewFromFiles(db, new[] { binaryPath });
        Assert.Equal(0, preview.TotalNotesToImport);
        Assert.Equal(1, preview.TotalSkippedOrErroneous);
        Assert.Contains("бинарные", preview.Diagnostics[0].Reason);
    }

    [Fact]
    public void HtmlContent_NotExecuted_PreservedAsRawText()
    {
        var htmlContent = "<script>alert('pwned');</script><h1>Heading</h1><iframe src='http://evil.com'></iframe>";
        var path = Path.Combine(_tempDirectory, "html_note.md");
        File.WriteAllText(path, htmlContent, Encoding.UTF8);

        var importService = new NoteImportService();
        using var db = CreateContext();

        var preview = importService.BuildPreviewFromFiles(db, new[] { path });
        Assert.Equal(1, preview.TotalNotesToImport);

        var execRes = importService.ExecuteImport(db, preview, Coord());
        Assert.True(execRes.Success);

        var note = db.Notes.FirstOrDefault();
        Assert.NotNull(note);
        Assert.Equal(htmlContent, note.Text);
    }

    #endregion

    #region 6. Conflict Preview and Skip Policy Tests

    [Fact]
    public void ConflictPreview_IdentifiesIdAndContentDuplicates_AppliesSkipPolicy()
    {
        using (var db = CreateContext())
        {
            var existingNote = new Note
            {
                Id = 50,
                Text = "Существующий текст заметки"
            };
            db.Notes.Add(existingNote);
            db.SaveChanges();
        }

        // 1. Note matching by ID 50 (different text)
        var fileById = Path.Combine(_tempDirectory, "match_id.md");
        File.WriteAllText(fileById, "---\nid: 50\n---\nДругой текст, но ID тот же", Encoding.UTF8);

        // 2. Note matching by normalized content (no ID)
        var fileByContent = Path.Combine(_tempDirectory, "match_content.txt");
        File.WriteAllText(fileByContent, "  Существующий текст заметки  \r\n", Encoding.UTF8);

        // 3. New unique note
        var fileNew = Path.Combine(_tempDirectory, "new_note.txt");
        File.WriteAllText(fileNew, "Уникальный новый текст", Encoding.UTF8);

        var importService = new NoteImportService();
        using var dbContext = CreateContext();

        var preview = importService.BuildPreviewFromFiles(dbContext, new[] { fileById, fileByContent, fileNew });

        Assert.Equal(3, preview.TotalFilesDiscovered);
        Assert.Equal(1, preview.TotalNotesToImport);
        Assert.Equal(2, preview.TotalConflicts);
        Assert.Equal(0, preview.TotalSkippedOrErroneous);

        Assert.Contains("пропускать", preview.ConflictPolicyDescription);

        var idConflict = preview.Items.First(i => i.SourceFileName == "match_id.md");
        Assert.True(idConflict.IsConflict);
        Assert.Equal("Конфликт (пропуск)", idConflict.Status);
        Assert.Contains("ID #50", idConflict.ConflictReason);

        var contentConflict = preview.Items.First(i => i.SourceFileName == "match_content.txt");
        Assert.True(contentConflict.IsConflict);
        Assert.Equal("Конфликт (пропуск)", contentConflict.Status);
        Assert.Contains("идентичным содержимым", contentConflict.ConflictReason);

        var newNote = preview.Items.First(i => i.SourceFileName == "new_note.txt");
        Assert.False(newNote.IsConflict);
        Assert.Equal("Готова к импорту", newNote.Status);

        // Execute import: only the 1 non-conflicting note is imported, without overwriting anything!
        var execRes = importService.ExecuteImport(dbContext, preview, Coord());
        Assert.True(execRes.Success);
        Assert.Equal(1, execRes.ImportedNotesCount);

        // Verify existing note was NOT overwritten
        var existingNoteInDb = dbContext.Notes.FirstOrDefault(n => n.Id == 50);
        Assert.NotNull(existingNoteInDb);
        Assert.Equal("Существующий текст заметки", existingNoteInDb.Text);

        // Verify total notes is 2 (1 existing + 1 imported)
        Assert.Equal(2, dbContext.Notes.Count());
    }

    #endregion

    #region 7. Rollback Failure Injection Tests

    private class FaultyNoteHistoryService : NoteHistoryService
    {
        public override NoteRevision? SaveSnapshot(QuickNotesDbContext db, Note note, int maxRevisions = 20)
        {
            throw new InvalidOperationException("Injected snapshot failure during import");
        }
    }

    [Fact]
    public void ExecuteImport_RollbackFailureInjection_RollsBackAllNotesTagsAndRevisions()
    {
        var file1 = Path.Combine(_tempDirectory, "file1.txt");
        var file2 = Path.Combine(_tempDirectory, "file2.txt");
        File.WriteAllText(file1, "Заметка один", Encoding.UTF8);
        File.WriteAllText(file2, "Заметка два", Encoding.UTF8);

        var importService = new NoteImportService();
        using var db = CreateContext();

        var preview = importService.BuildPreviewFromFiles(db, new[] { file1, file2 });
        Assert.Equal(2, preview.TotalNotesToImport);

        // Inject failure via faulty history service
        var faultyHistoryService = new FaultyNoteHistoryService();
        var execResult = importService.ExecuteImport(db, preview, Coord(), faultyHistoryService);

        Assert.False(execResult.Success);
        Assert.Contains("Injected snapshot failure", execResult.ErrorMessage);

        // Verify complete rollback: ZERO notes, ZERO tags, ZERO revisions created
        Assert.Equal(0, db.Notes.Count());
        Assert.Equal(0, db.Tags.Count());
        Assert.Equal(0, db.NoteRevisions.Count());
    }

    #endregion

    #region 8. ViewModel Integration Tests

    [Fact]
    public async Task ImportExportViewModel_ExportAndImport_IntegratesCorrectly()
    {
        using (var db = CreateContext())
        {
            db.Notes.Add(new Note { Text = "Test Note" });
            db.SaveChanges();
        }

        var exportService = new NoteExportService();
        var importService = new NoteImportService();

        var vm = ImportExportViewModelTestComposition.Create(
            CreateContext,
            exportService,
            importService);

        Assert.Equal(1, vm.NonDeletedNotesCount);

        // Test export CSV command with mocked SaveFileDialog
        var exportedCsvPath = Path.Combine(_tempDirectory, "vm_export.csv");
        vm.SaveFileDialogProvider = (filter, ext) => exportedCsvPath;
        vm.MessageBoxProvider = (_, _, _) => { };

        await vm.ExecuteExportCsvAsync();
        Assert.True(vm.IsExportSuccess);
        Assert.True(File.Exists(exportedCsvPath));

        // Test select files for import
        var importFile = Path.Combine(_tempDirectory, "import_test.txt");
        File.WriteAllText(importFile, "Новая импортированная заметка", Encoding.UTF8);

        vm.OpenFileDialogProvider = filter => new[] { importFile };
        await vm.ExecuteSelectFilesAsync();

        Assert.True(vm.HasPreview);
        Assert.Equal(1, vm.TotalNotesToImport);
        Assert.True(vm.CanConfirmImport);

        // Confirm import
        await vm.ExecuteConfirmImportAsync();
        Assert.True(vm.IsImportSuccess);
        Assert.Equal(1, vm.ImportedNotesCount);

        using var verifyDb = CreateContext();
        Assert.Equal(2, verifyDb.Notes.Count());
    }

    [Fact]
    public void ImportExportViewModel_ComputedImportCounts_AreReadOnly()
    {
        var vm = ImportExportViewModelTestComposition.Create(CreateContext, new NoteExportService(), new NoteImportService());
        var type = typeof(ImportExportViewModel);

        foreach (var name in new[]
                 {
                     nameof(ImportExportViewModel.TotalNotesToImport),
                     nameof(ImportExportViewModel.TotalFilesDiscovered),
                     nameof(ImportExportViewModel.TotalConflicts),
                     nameof(ImportExportViewModel.NewTagsCount),
                     nameof(ImportExportViewModel.TotalSkippedOrErroneous),
                     nameof(ImportExportViewModel.CanConfirmImport),
                     nameof(ImportExportViewModel.HasPreview)
                 })
        {
            var prop = type.GetProperty(name);
            Assert.NotNull(prop);
            Assert.True(prop!.CanRead);
            Assert.False(prop.CanWrite);
        }

        Assert.Equal(0, vm.TotalNotesToImport);
        Assert.False(vm.CanConfirmImport);
    }

    [Fact]
    public void ImportExportWindowXaml_ReadOnlyRunBindings_UseOneWayMode()
    {
        var root = FindSolutionRoot();
        var xamlPath = Path.Combine(root, "QuickNotes.App", "Views", "ImportExportWindow.xaml");
        Assert.True(File.Exists(xamlPath), xamlPath);

        var content = File.ReadAllText(xamlPath);
        Assert.Contains("Binding TotalNotesToImport, Mode=OneWay", content);
        Assert.Contains("Binding TotalFilesDiscovered, Mode=OneWay", content);
        Assert.Contains("Binding CanConfirmImport, Mode=OneWay", content);
        Assert.Contains("Binding FileName, Mode=OneWay", content);
        Assert.Contains("Binding Reason, Mode=OneWay", content);
        Assert.Contains("Binding LastSuccessfulExportBanner, Mode=OneWay", content);
        Assert.DoesNotContain("Binding TotalNotesToImport, StringFormat", content);
    }

    private static string FindSolutionRoot()
    {
        var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "QuickNotes.sln")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }

        return @"D:\work\QuickNotes";
    }

    #endregion

    #region 9. Safe Export and Tag Validation Tests

    [Fact]
    public void ExportToJson_WhenPublicationFailsDueToLock_PreservesExistingFileAndCleansTempFiles()
    {
        var exportService = new NoteExportService();
        var jsonPath = Path.Combine(_tempDirectory, "locked_target.json");
        const string initialContent = "PREVIOUS_VALID_JSON_CONTENT";
        File.WriteAllText(jsonPath, initialContent, Encoding.UTF8);

        using (var db = CreateContext())
        {
            db.Notes.Add(new Note { Text = "New Note To Export" });
            db.SaveChanges();

            using (var lockStream = new FileStream(jsonPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var res = exportService.ExportToJson(db, jsonPath);
                Assert.False(res.Success);
                Assert.NotNull(res.ErrorMessage);
            }
        }

        Assert.Equal(initialContent, File.ReadAllText(jsonPath, Encoding.UTF8));
        var tmpFiles = Directory.GetFiles(_tempDirectory, ".tmp_*");
        Assert.Empty(tmpFiles);
    }

    [Fact]
    public void SafeWriteFile_WhenWriteThrowsException_CleansTempFileAndPreservesTarget()
    {
        var targetPath = Path.Combine(_tempDirectory, "safe_write_test.txt");
        const string initialContent = "EXISTING_DATA";
        File.WriteAllText(targetPath, initialContent, Encoding.UTF8);

        Assert.Throws<InvalidOperationException>(() =>
        {
            NoteExportService.SafeWriteFile(targetPath, tempFile =>
            {
                File.WriteAllText(tempFile, "PARTIAL_UNFINISHED_DATA");
                throw new InvalidOperationException("Simulated mid-write crash");
            });
        });

        Assert.Equal(initialContent, File.ReadAllText(targetPath, Encoding.UTF8));
        var tmpFiles = Directory.GetFiles(_tempDirectory, ".tmp_*");
        Assert.Empty(tmpFiles);
    }

    [Fact]
    public void MarkdownExport_IntoEmptyExistingFolder_SucceedsAndAtomicallyReplaces()
    {
        var targetExportFolder = Path.Combine(_tempDirectory, "EmptyExistingFolder");
        Directory.CreateDirectory(targetExportFolder);

        using (var db = CreateContext())
        {
            db.Notes.Add(new Note { Text = "Markdown Note in existing empty directory" });
            db.SaveChanges();
        }

        var exportService = new NoteExportService();
        using (var db = CreateContext())
        {
            var res = exportService.ExportToMarkdown(db, targetExportFolder);
            Assert.True(res.Success);
            Assert.Equal(1, res.ExportedNotesCount);
        }

        Assert.True(Directory.Exists(targetExportFolder));
        var mdFiles = Directory.GetFiles(targetExportFolder, "*.md");
        Assert.Single(mdFiles);

        var tempDirs = Directory.GetDirectories(_tempDirectory, ".tmp_export_*");
        Assert.Empty(tempDirs);
    }

    [Fact]
    public void Import_WithEmptyOrWhitespaceTagNamesInJson_DoesNotCreateEmptyTags()
    {
        var jsonContent = """
            {
              "schemaVersion": 1,
              "exportedAt": "2026-09-01T10:00:00Z",
              "tags": [
                { "name": "" },
                { "name": "   " },
                { "name": "  CleanTag1  " }
              ],
              "notes": [
                {
                  "text": "Note with empty tag names in JSON",
                  "tags": [
                    { "tagName": "" },
                    { "tagName": "  " },
                    { "tagName": "  CleanTag2  " }
                  ]
                }
              ]
            }
            """;

        var jsonPath = Path.Combine(_tempDirectory, "empty_tags_note.json");
        File.WriteAllText(jsonPath, jsonContent, Encoding.UTF8);

        var importService = new NoteImportService();
        using var db = CreateContext();

        var preview = importService.BuildPreviewFromFiles(db, new[] { jsonPath });
        Assert.Equal(1, preview.TotalNotesToImport);
        Assert.Equal(2, preview.NewTagsCount);

        var execRes = importService.ExecuteImport(db, preview, Coord());
        Assert.True(execRes.Success);
        Assert.Equal(1, execRes.ImportedNotesCount);
        Assert.Equal(2, execRes.CreatedTagsCount);

        Assert.False(db.Tags.Any(t => string.IsNullOrWhiteSpace(t.Name)));

        var tagsInDb = db.Tags.OrderBy(t => t.Name).ToList();
        Assert.Equal(2, tagsInDb.Count);
        Assert.Equal("CleanTag1", tagsInDb[0].Name);
        Assert.Equal("CleanTag2", tagsInDb[1].Name);

        var noteInDb = db.Notes.Include(n => n.NoteTags).ThenInclude(nt => nt.Tag).FirstOrDefault();
        Assert.NotNull(noteInDb);
        var noteTag = Assert.Single(noteInDb.NoteTags);
        Assert.Equal("CleanTag2", noteTag.Tag.Name);
    }

    [Fact]
    public void Import_MarkdownWithEmptyFrontMatterTags_DoesNotCreateEmptyTags()
    {
        var mdContent = """
            ---
            tags:
              - ""
              - "   "
              - "  ValidFrontMatterTag  "
            ---
            Заметка с пустыми тегами во front matter.
            """;

        var mdPath = Path.Combine(_tempDirectory, "empty_fm_tags.md");
        File.WriteAllText(mdPath, mdContent, Encoding.UTF8);

        var importService = new NoteImportService();
        using var db = CreateContext();

        var preview = importService.BuildPreviewFromFiles(db, new[] { mdPath });
        Assert.Equal(1, preview.TotalNotesToImport);

        var execRes = importService.ExecuteImport(db, preview, Coord());
        Assert.True(execRes.Success);
        Assert.Equal(1, execRes.ImportedNotesCount);

        Assert.False(db.Tags.Any(t => string.IsNullOrWhiteSpace(t.Name)));

        var tagInDb = Assert.Single(db.Tags);
        Assert.Equal("ValidFrontMatterTag", tagInDb.Name);

        var noteInDb = db.Notes.Include(n => n.NoteTags).ThenInclude(nt => nt.Tag).FirstOrDefault();
        Assert.NotNull(noteInDb);
        var noteTag = Assert.Single(noteInDb.NoteTags);
        Assert.Equal("ValidFrontMatterTag", noteTag.Tag.Name);
    }

    #endregion

    #region 10. Open export snapshot (M3)

    [Fact]
    public void OpenExport_OmitsProtectedNotes_FromMarkdownJsonAndArchive()
    {
        const string secret = "PROTECTED_SECRET_MUST_NOT_APPEAR_IN_OPEN_EXPORT";
        const string secretFile = "secret-protected.bin";
        const string secretBytesText = "SECRET_ATTACHMENT_BYTES";
        const string openText = "Open note with [[link]]";
        var storage = new AttachmentStorageService(Path.Combine(_tempDirectory, "att"));

        using (var db = CreateContext())
        {
            var open = new Note { Text = openText, SourceUrl = "https://example.test/open" };
            var hidden = new Note { Text = secret, IsProtected = true };
            db.Notes.AddRange(open, hidden);
            db.SaveChanges();

            var openSaved = storage.SaveFromBytes(Encoding.UTF8.GetBytes("open-bytes"), "open.txt", 1024 * 1024);
            db.NoteAttachments.Add(new NoteAttachment
            {
                NoteId = open.Id,
                OriginalFileName = openSaved.OriginalFileName,
                StoredFileName = openSaved.StoredFileName,
                RelativePath = openSaved.RelativePath,
                ContentType = openSaved.ContentType,
                Size = openSaved.Size,
                Sha256 = openSaved.Sha256
            });

            var secretSaved = storage.SaveFromBytes(Encoding.UTF8.GetBytes(secretBytesText), secretFile, 1024 * 1024);
            db.NoteAttachments.Add(new NoteAttachment
            {
                NoteId = hidden.Id,
                OriginalFileName = secretSaved.OriginalFileName,
                StoredFileName = secretSaved.StoredFileName,
                RelativePath = secretSaved.RelativePath,
                ContentType = secretSaved.ContentType,
                Size = secretSaved.Size,
                Sha256 = secretSaved.Sha256,
                IsProtected = true
            });
            db.SaveChanges();
        }

        var exportService = new NoteExportService();
        var jsonPath = Path.Combine(_tempDirectory, "open.json");
        var mdDir = Path.Combine(_tempDirectory, "open-md");
        var zipPath = Path.Combine(_tempDirectory, "open.qnarchive.zip");

        using (var db = CreateContext())
        {
            var json = exportService.ExportToJson(db, jsonPath);
            Assert.True(json.Success);
            Assert.Equal(1, json.ExportedNotesCount);
            Assert.Equal(1, json.SkippedProtectedCount);

            var md = exportService.ExportToMarkdown(db, mdDir, storage);
            Assert.True(md.Success, md.ErrorMessage);
            Assert.Equal(1, md.ExportedNotesCount);
            Assert.Equal(1, md.SkippedProtectedCount);
            Assert.Equal(1, md.ExportedAttachmentCount);
            Assert.False(md.IsComplete);

            var archive = new NoteArchiveService().ExportArchive(db, zipPath, storage);
            Assert.True(archive.Success, archive.ErrorMessage);
            Assert.Equal(1, archive.ExportedNotesCount);
            Assert.Equal(1, archive.SkippedProtectedCount);
        }

        string jsonText = File.ReadAllText(jsonPath, Encoding.UTF8);
        Assert.DoesNotContain(secret, jsonText);
        Assert.Contains(openText, jsonText);

        string snapshotFiles = string.Join('\n', Directory.GetFiles(mdDir, "*", SearchOption.AllDirectories));
        string snapshot = Directory.GetFiles(mdDir, "*", SearchOption.AllDirectories)
            .Select(f => File.ReadAllText(f, Encoding.UTF8))
            .Aggregate(new StringBuilder(), (sb, t) => sb.AppendLine(t))
            .ToString();
        Assert.DoesNotContain(secret, snapshot);
        Assert.DoesNotContain(secretFile, snapshot);
        Assert.DoesNotContain(secretFile, snapshotFiles);
        Assert.DoesNotContain(secretBytesText, snapshot);
        Assert.Contains(openText, snapshot);
        Assert.Contains("open.txt", snapshotFiles);

        using var zip = ZipFile.OpenRead(zipPath);
        foreach (var entry in zip.Entries)
        {
            using var stream = entry.Open();
            using var reader = new StreamReader(stream, Encoding.UTF8);
            Assert.DoesNotContain(secret, reader.ReadToEnd());
        }
    }

    [Fact]
    public void OpenExport_IncludesTitleTagsLinksSourceAndAvailableAttachments_AndManifestChecksums()
    {
        var storage = new AttachmentStorageService(Path.Combine(_tempDirectory, "att2"));
        byte[] bytes = Encoding.UTF8.GetBytes("attachment-bytes");
        string? attRel = null;
        string? attSha = null;

        using (var db = CreateContext())
        {
            var tag = new Tag { Name = "export-tag" };
            db.Tags.Add(tag);
            db.SaveChanges();

            var target = new Note { Text = "Linked target" };
            db.Notes.Add(target);
            db.SaveChanges();

            var source = new Note
            {
                Text = $"# Snapshot title\nsee [[{target.Id}]]",
                SourceProcessName = "notepad.exe",
                SourceWindowTitle = "Demo",
                SourceUrl = "https://example.test/src",
                CapturedAt = new DateTime(2026, 9, 10, 12, 0, 0, DateTimeKind.Utc)
            };
            db.Notes.Add(source);
            db.SaveChanges();
            db.NoteTags.Add(new NoteTag { NoteId = source.Id, TagId = tag.Id, Origin = TagOrigin.Manual });

            var saved = storage.SaveFromBytes(bytes, "clip.txt", 0);
            attRel = saved.RelativePath;
            attSha = saved.Sha256;
            db.NoteAttachments.Add(new NoteAttachment
            {
                NoteId = source.Id,
                OriginalFileName = saved.OriginalFileName,
                StoredFileName = saved.StoredFileName,
                RelativePath = saved.RelativePath,
                ContentType = saved.ContentType,
                Size = saved.Size,
                Sha256 = saved.Sha256
            });

            db.Notes.Add(new Note
            {
                Text = "Missing file note",
            });
            db.SaveChanges();
            var missingNote = db.Notes.Single(n => n.Text == "Missing file note");
            db.NoteAttachments.Add(new NoteAttachment
            {
                NoteId = missingNote.Id,
                OriginalFileName = "gone.bin",
                StoredFileName = "gone.bin",
                RelativePath = "Attachments/does-not-exist.bin",
                Size = 4,
                Sha256 = "abc"
            });
            db.SaveChanges();
        }

        var folder = Path.Combine(_tempDirectory, "snapshot");
        var exportService = new NoteExportService();
        using (var db = CreateContext())
        {
            var res = exportService.ExportToMarkdown(db, folder, storage);
            Assert.True(res.Success, res.ErrorMessage);
            Assert.Equal(3, res.ExportedNotesCount);
            Assert.Equal(1, res.ExportedAttachmentCount);
            Assert.Equal(1, res.SkippedMissingAttachmentCount);
            Assert.False(res.IsComplete);
            Assert.Contains("неполный", res.CompletenessSummary);
        }

        var md = Directory.GetFiles(folder, "*.md").Select(File.ReadAllText).ToList();
        Assert.Contains(md, t => t.Contains("title:") && t.Contains("Snapshot title"));
        Assert.Contains(md, t => t.Contains("export-tag"));
        Assert.Contains(md, t => t.Contains("links:") && t.Contains("Linked target"));
        Assert.Contains(md, t => t.Contains("https://example.test/src"));
        Assert.Contains(md, t => t.Contains("clip.txt"));

        string copied = Directory.GetFiles(Path.Combine(folder, "files"), "*", SearchOption.AllDirectories).Single();
        Assert.Equal(bytes, File.ReadAllBytes(copied));
        Assert.Equal(attSha, AttachmentFileHelper.ComputeSha256(copied), ignoreCase: true);

        var manifest = System.Text.Json.JsonSerializer.Deserialize<OpenExportManifest>(
            File.ReadAllText(Path.Combine(folder, "manifest.json")),
            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.NotNull(manifest);
        Assert.Equal(OpenExportManifest.FormatName, manifest!.Format);
        Assert.Equal(OpenExportManifest.CurrentFormatVersion, manifest.FormatVersion);
        Assert.True(manifest.ExportedAtUtc <= DateTime.UtcNow.AddMinutes(5));
        Assert.True(manifest.ExportedAtUtc > DateTime.UtcNow.AddDays(-1));
        Assert.Equal(3, manifest.EntityCounts.Notes);
        Assert.Equal(1, manifest.SkippedMissingAttachments);
        Assert.True(manifest.ErrorCount >= 1);
        Assert.NotEmpty(manifest.Files);
        Assert.All(manifest.Files, f =>
        {
            Assert.False(string.IsNullOrWhiteSpace(f.Sha256));
            Assert.Equal(64, f.Sha256.Length);
        });
        _ = attRel;
    }

    [Fact]
    public void OpenExport_FailureBeforePublish_PreservesPreviousSnapshot_AndCleansStaging()
    {
        var folder = Path.Combine(_tempDirectory, "keep-me");
        var exportService = new NoteExportService();

        using (var db = CreateContext())
        {
            db.Notes.Add(new Note { Text = "FIRST_SNAPSHOT_NOTE" });
            db.SaveChanges();
            var first = exportService.ExportToMarkdown(db, folder);
            Assert.True(first.Success, first.ErrorMessage);
        }

        string previous = File.ReadAllText(Directory.GetFiles(folder, "*.md").Single(), Encoding.UTF8);

        using (var db = CreateContext())
        {
            db.Notes.Add(new Note { Text = "SECOND_SNAPSHOT_NOTE" });
            db.SaveChanges();
            exportService.BeforePublishForTests = _ => throw new InvalidOperationException("simulated mid-export failure");
            var fail = exportService.ExportToMarkdown(db, folder);
            Assert.False(fail.Success);
            Assert.Contains("simulated mid-export failure", fail.ErrorMessage);
        }

        Assert.True(Directory.Exists(folder));
        string still = File.ReadAllText(Directory.GetFiles(folder, "*.md").Single(), Encoding.UTF8);
        Assert.Equal(previous, still);
        Assert.Contains("FIRST_SNAPSHOT_NOTE", still);
        Assert.DoesNotContain("SECOND_SNAPSHOT_NOTE", File.ReadAllText(Path.Combine(folder, "manifest.json")) + still);
        Assert.Empty(Directory.GetDirectories(_tempDirectory, ".tmp_export_*"));
        Assert.Empty(Directory.GetDirectories(_tempDirectory, ".bak_export_*"));
    }

    [Fact]
    public void OpenExport_FailureAfterPreviousMovedAside_RestoresPreviousSnapshot()
    {
        var folder = Path.Combine(_tempDirectory, "restore-me");
        var exportService = new NoteExportService();

        using (var db = CreateContext())
        {
            db.Notes.Add(new Note { Text = "KEEP_PREVIOUS" });
            db.SaveChanges();
            Assert.True(exportService.ExportToMarkdown(db, folder).Success);
        }

        using (var db = CreateContext())
        {
            db.Notes.Add(new Note { Text = "MUST_NOT_REPLACE" });
            db.SaveChanges();
            exportService.AfterTargetMovedAsideForTests = _ => throw new IOException("publish interrupted");
            var fail = exportService.ExportToMarkdown(db, folder);
            Assert.False(fail.Success);
        }

        var md = File.ReadAllText(Directory.GetFiles(folder, "*.md").Single(), Encoding.UTF8);
        Assert.Contains("KEEP_PREVIOUS", md);
        Assert.DoesNotContain("MUST_NOT_REPLACE", md);
        Assert.Empty(Directory.GetDirectories(_tempDirectory, ".tmp_export_*"));
        Assert.Empty(Directory.GetDirectories(_tempDirectory, ".bak_export_*"));
    }

    [Fact]
    public async Task ImportExportViewModel_OpenExport_RequiresPlaintextConfirm_AndPersistsLastSuccess()
    {
        var settingsPath = Path.Combine(_tempDirectory, "settings.json");
        var settings = new SettingsService(settingsPath, _ => { });
        var folder = Path.Combine(_tempDirectory, "ui-export");

        using (var db = CreateContext())
        {
            db.Notes.Add(new Note { Text = "UI export note" });
            db.SaveChanges();
        }

        var vm = ImportExportViewModelTestComposition.Create(
            CreateContext,
            new NoteExportService(),
            new NoteImportService(),
            settingsService: settings);

        Assert.False(vm.HasLastSuccessfulExport);
        Assert.Contains("ещё не выполнялся", vm.LastSuccessfulExportBanner);

        vm.FolderBrowserDialogProvider = () => folder;
        vm.MessageBoxProvider = (_, _, _) => { };
        vm.ConfirmProvider = (_, _) => false;
        await vm.ExecuteExportMarkdownAsync();
        Assert.False(Directory.Exists(folder));
        Assert.False(vm.HasLastSuccessfulExport);

        vm.ConfirmProvider = (msg, _) =>
        {
            Assert.Contains("открытым текстом", msg);
            Assert.Contains("не зашифрованный архив", msg);
            return true;
        };
        await vm.ExecuteExportMarkdownAsync();
        Assert.True(vm.IsExportSuccess);
        Assert.True(vm.HasLastSuccessfulExport);
        Assert.Equal(folder, vm.LastOpenExportPath);
        Assert.Contains("Полнота:", vm.LastSuccessfulExportBanner);
        Assert.Contains("Время (UTC):", vm.LastSuccessfulExportBanner);

        var reloaded = ImportExportViewModelTestComposition.Create(
            CreateContext,
            new NoteExportService(),
            new NoteImportService(),
            settingsService: new SettingsService(settingsPath, _ => { }));
        Assert.True(reloaded.HasLastSuccessfulExport);
        Assert.Equal(folder, reloaded.LastOpenExportPath);
        Assert.False(string.IsNullOrWhiteSpace(reloaded.LastOpenExportCompleteness));
    }

    #endregion
}

