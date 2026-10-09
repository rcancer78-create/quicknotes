using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using QuickNotes.App.Data;
using QuickNotes.App.Models;
using QuickNotes.App.Services;
using QuickNotes.App.Services.Sync;
using Xunit;

namespace QuickNotes.Tests;

[TestCategory(TestCategories.Integration)]
public sealed class ImportMigrationDedupAndTransactionTests : IDisposable
{
    private readonly string _root;
    private readonly string _dbPath;

    public ImportMigrationDedupAndTransactionTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "qn_imp81_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _dbPath = Path.Combine(_root, "db.sqlite");
        using var db = SqliteTestUtil.CreateContext(_dbPath);
        DbInitializer.Initialize(db);
    }

    public void Dispose()
    {
        NoteImportService.TestInjectFailure = null;
        SqliteTestUtil.TryDeleteDirectory(_root);
    }

    private QuickNotesDbContext Db() => SqliteTestUtil.CreateContext(_dbPath);

    private static ILocalMutationCoordinator Coord() => new LocalMutationCoordinator();

    [Fact]
    public void HtmlAndMarkdownFixtures_CyrillicLocalImage_AndNoNetworkFetch()
    {
        string dir = Path.Combine(_root, "src");
        Directory.CreateDirectory(dir);
        string img = Path.Combine(dir, "pic.png");
        File.WriteAllBytes(img, new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        File.WriteAllText(Path.Combine(dir, "note.md"), "# МД\n\n![x](pic.png)\n", Encoding.UTF8);
        File.WriteAllText(Path.Combine(dir, "note.html"), "<h2>HTML</h2><p>Кириллица</p><img src=\"pic.png\"><img src=\"https://example.com/x.png\">", Encoding.UTF8);

        using var db = Db();
        var preview = new NoteImportService().BuildPreviewFromDirectory(db, dir);
        Assert.Equal(2, preview.Items.Count);
        Assert.Contains(preview.Items, i => i.Text.Contains("Кириллица") || i.Title.Contains("HTML", StringComparison.Ordinal));
        Assert.Contains(preview.Items, i => i.PendingAttachments.Count > 0);
        Assert.Contains(preview.Items, i => i.LostElements.Count > 0);
        Assert.DoesNotContain(preview.Items, i => i.Text.Contains("example.com", StringComparison.Ordinal));
    }

    [Fact]
    public void TraversalAndUnsupportedWord_AreRejected()
    {
        string dir = Path.Combine(_root, "tree");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "ok.md"), "ok", Encoding.UTF8);
        File.WriteAllBytes(Path.Combine(dir, "legacy.docx"), new byte[] { 1, 2, 3 });
        using var db = Db();
        var svc = new NoteImportService();
        var preview = svc.BuildPreviewFromFiles(db, new[]
        {
            Path.Combine(dir, "ok.md"),
            Path.Combine(dir, "legacy.docx"),
            Path.Combine(dir, "..", "outside.md")
        }, dir);
        Assert.Contains(preview.Diagnostics, d => d.Reason.Contains("Word", StringComparison.OrdinalIgnoreCase) || d.Reason.Contains("OneNote"));
        Assert.Contains(preview.Diagnostics, d => d.IsBlocking || d.Reason.Contains("предел", StringComparison.Ordinal) || d.Reason.Contains("вне", StringComparison.OrdinalIgnoreCase) || d.Reason.Contains("не существует", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ResourceLimit_FileCount_IsBlocking()
    {
        string dir = Path.Combine(_root, "many");
        Directory.CreateDirectory(dir);
        var files = new List<string>();
        for (int i = 0; i < ImportLimits.MaxFileCount + 2; i++)
        {
            string p = Path.Combine(dir, i + ".md");
            File.WriteAllText(p, "n" + i, Encoding.UTF8);
            files.Add(p);
        }

        using var db = Db();
        var preview = new NoteImportService().BuildPreviewFromFiles(db, files, dir);
        Assert.True(preview.HasBlockingDiagnostics);
        Assert.False(preview.CanConfirm);
    }

    [Fact]
    public void ExactRetry_DoesNotDuplicate()
    {
        string file = Path.Combine(_root, "once.md");
        File.WriteAllText(file, "уникальный текст импорта", Encoding.UTF8);
        using var db = Db();
        var svc = new NoteImportService();
        var preview = svc.BuildPreviewFromFiles(db, new[] { file });
        Assert.Equal(1, preview.TotalNotesToImport);
        var first = svc.ExecuteImport(db, preview, Coord());
        Assert.True(first.Success);
        Assert.Equal(1, first.ImportedNotesCount);

        var preview2 = svc.BuildPreviewFromFiles(db, new[] { file });
        Assert.Equal(0, preview2.TotalNotesToImport);
        Assert.True(preview2.Items[0].IsConflict);
        var second = svc.ExecuteImport(db, preview2, Coord());
        Assert.True(second.Success);
        Assert.Equal(0, second.ImportedNotesCount);
        Assert.Equal(1, db.Notes.Count());
    }

    [Fact]
    public void SameTitleDifferentContent_DefaultSkip_ExplicitSeparateKeepsBoth()
    {
        using (var db = Db())
        {
            db.Notes.Add(new Note { Title = "Заголовок", Text = "старое тело" });
            db.SaveChanges();
        }

        string file = Path.Combine(_root, "t.md");
        File.WriteAllText(file, "# Заголовок\n\nновое тело", Encoding.UTF8);
        using var db2 = Db();
        var svc = new NoteImportService();
        var preview = svc.BuildPreviewFromFiles(db2, new[] { file });
        Assert.Equal(ImportDuplicateKind.SameTitle, preview.Items[0].DuplicateKind);
        Assert.Equal(0, preview.TotalNotesToImport);
        preview.Items[0].DuplicateAction = ImportDuplicateAction.ImportSeparate;
        svc.RecalculatePreviewTotals(db2, preview);
        Assert.Equal(1, preview.TotalNotesToImport);
        var exec = svc.ExecuteImport(db2, preview, Coord());
        Assert.True(exec.Success);
        Assert.Equal(2, db2.Notes.Count());
        Assert.Equal(2, db2.Notes.Select(n => n.Text).Distinct().Count());
    }

    [Fact]
    public void Replace_OnlyWhenSameFingerprint_AndNotProtected()
    {
        string file = Path.Combine(_root, "r.md");
        File.WriteAllText(file, "версия 1", Encoding.UTF8);
        using var db = Db();
        var svc = new NoteImportService();
        var p1 = svc.BuildPreviewFromFiles(db, new[] { file });
        Assert.True(svc.ExecuteImport(db, p1, Coord()).Success);

        File.WriteAllText(file, "версия 2", Encoding.UTF8);
        var p2 = svc.BuildPreviewFromFiles(db, new[] { file });
        Assert.Equal(ImportDuplicateKind.ExactSource, p2.Items[0].DuplicateKind);
        Assert.True(p2.Items[0].CanReplace);
        p2.Items[0].DuplicateAction = ImportDuplicateAction.Replace;
        svc.RecalculatePreviewTotals(db, p2);
        var exec = svc.ExecuteImport(db, p2, Coord());
        Assert.True(exec.Success);
        Assert.Equal(1, exec.ReplacedNotesCount);
        Assert.Equal("версия 2", db.Notes.Single().Text);
        Assert.Equal(1, db.Notes.Count());
    }

    [Fact]
    public void Fingerprint_IgnoresTimestamp_ChangesWithContentOrRelativePath()
    {
        string dir = Path.Combine(_root, "fp");
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, "n.md");
        File.WriteAllText(file, "тело", Encoding.UTF8);
        using var db = Db();
        var svc = new NoteImportService();
        var first = svc.BuildPreviewFromFiles(db, new[] { file }, dir);
        string fp1 = first.Items[0].SourceFingerprint;
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddHours(3));
        var touched = svc.BuildPreviewFromFiles(db, new[] { file }, dir);
        Assert.Equal(fp1, touched.Items[0].SourceFingerprint);

        File.WriteAllText(file, "другое тело", Encoding.UTF8);
        var changed = svc.BuildPreviewFromFiles(db, new[] { file }, dir);
        Assert.NotEqual(fp1, changed.Items[0].SourceFingerprint);

        string otherDir = Path.Combine(dir, "sub");
        Directory.CreateDirectory(otherDir);
        string moved = Path.Combine(otherDir, "n.md");
        File.Copy(file, moved);
        var pathChanged = svc.BuildPreviewFromFiles(db, new[] { moved }, dir);
        Assert.NotEqual(changed.Items[0].SourceFingerprint, pathChanged.Items[0].SourceFingerprint);
        Assert.NotEqual(changed.Items[0].SourceRelativePath, pathChanged.Items[0].SourceRelativePath);
    }

    [Fact]
    public void TimestampTouch_ExactRetry_DoesNotCreateNewNote()
    {
        string file = Path.Combine(_root, "touch.md");
        File.WriteAllText(file, "стабильный текст", Encoding.UTF8);
        using var db = Db();
        var svc = new NoteImportService();
        var p1 = svc.BuildPreviewFromFiles(db, new[] { file });
        Assert.True(svc.ExecuteImport(db, p1, Coord()).Success);
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(5));
        var p2 = svc.BuildPreviewFromFiles(db, new[] { file });
        Assert.Equal(ImportDuplicateKind.ExactSource, p2.Items[0].DuplicateKind);
        Assert.Equal(0, p2.TotalNotesToImport);
        Assert.True(svc.ExecuteImport(db, p2, Coord()).Success);
        Assert.Equal(1, db.Notes.Count());
    }

    [Fact]
    public void Rollback_DoesNotDeleteSharedPreexistingAttachmentFile()
    {
        string attDir = Path.Combine(_root, "shared-att");
        var storage = new AttachmentStorageService(attDir);
        byte[] bytes = { 137, 80, 78, 71, 13, 10, 26, 10, 9, 9, 9 };
        string existingSrc = Path.Combine(_root, "existing.png");
        File.WriteAllBytes(existingSrc, bytes);
        var saved = storage.SaveAttachment(existingSrc, ImportLimits.DefaultMaxAttachmentBytes);
        Assert.True(saved.WasCreated);

        using var db = Db();
        var owner = new Note { Title = "Владелец", Text = "уже есть" };
        db.Notes.Add(owner);
        db.SaveChanges();
        db.NoteAttachments.Add(new NoteAttachment
        {
            NoteId = owner.Id,
            OriginalFileName = saved.OriginalFileName,
            StoredFileName = saved.StoredFileName,
            RelativePath = saved.RelativePath,
            ContentType = saved.ContentType,
            Size = saved.Size,
            Sha256 = saved.Sha256,
            CreatedAt = DateTime.UtcNow
        });
        db.SaveChanges();
        string originalText = File.ReadAllText(saved.FullPath);

        string dir = Path.Combine(_root, "shared-src");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "pic.png"), bytes);
        File.WriteAllText(Path.Combine(dir, "new.md"), "новая\n\n![x](pic.png)", Encoding.UTF8);
        var svc = new NoteImportService();
        var preview = svc.BuildPreviewFromDirectory(db, dir);
        preview.LossesAcknowledged = true;
        NoteImportService.TestInjectFailure = p =>
        {
            if (p == ImportTransactionPhase.Publish)
            {
                throw new InvalidOperationException("injected publish");
            }
        };

        var exec = svc.ExecuteImport(db, preview, Coord(), attachmentStorage: storage);
        Assert.False(exec.Success);
        Assert.Equal(1, db.Notes.Count());
        Assert.Equal("уже есть", db.Notes.Single().Text);
        Assert.Equal(1, db.NoteAttachments.Count());
        Assert.True(File.Exists(saved.FullPath));
        Assert.Equal(originalText, File.ReadAllText(saved.FullPath));
        Assert.Equal(bytes, File.ReadAllBytes(saved.FullPath));
    }

    [Fact]
    public void Replace_ReconcilesStaleTagsAndAttachments_AndWritesHistory()
    {
        string attDir = Path.Combine(_root, "repl-att");
        var storage = new AttachmentStorageService(attDir);
        string dir = Path.Combine(_root, "repl-src");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "old.png"), new byte[] { 1, 2, 3, 4, 5 });
        File.WriteAllText(Path.Combine(dir, "note.md"), "---\ntitle: Старая\ntags: [obsolete, keep]\n---\nтело v1\n\n![o](old.png)\n", Encoding.UTF8);

        using var db = Db();
        var svc = new NoteImportService();
        var p1 = svc.BuildPreviewFromFiles(db, new[] { Path.Combine(dir, "note.md") }, dir);
        p1.LossesAcknowledged = true;
        Assert.True(svc.ExecuteImport(db, p1, Coord(), attachmentStorage: storage).Success);
        Assert.Equal(2, db.NoteTags.Count());
        Assert.Equal(1, db.NoteAttachments.Count());
        string? oldStored = db.NoteAttachments.Single().StoredFileName;

        File.WriteAllBytes(Path.Combine(dir, "new.png"), new byte[] { 9, 8, 7, 6 });
        File.WriteAllText(Path.Combine(dir, "note.md"), "---\ntitle: Новая\ntags: [keep, fresh]\n---\nтело v2\n\n![n](new.png)\n", Encoding.UTF8);
        var p2 = svc.BuildPreviewFromFiles(db, new[] { Path.Combine(dir, "note.md") }, dir);
        p2.LossesAcknowledged = true;
        var item = p2.Items.Single(i => i.SourceFileName == "note.md");
        Assert.Equal(ImportDuplicateKind.ExactSource, item.DuplicateKind);
        item.DuplicateAction = ImportDuplicateAction.Replace;
        svc.RecalculatePreviewTotals(db, p2);
        var exec = svc.ExecuteImport(db, p2, Coord(), attachmentStorage: storage);
        Assert.True(exec.Success, exec.ErrorMessage);
        using var verify = Db();
        var note = verify.Notes.Include(n => n.NoteTags).ThenInclude(nt => nt.Tag).Include(n => n.Attachments).Single(n => n.DeletedAt == null && n.Title == "Новая");
        Assert.Equal("Новая", note.Title);
        Assert.Contains("тело v2", note.Text);
        var tagNames = note.NoteTags.Select(nt => nt.Tag!.Name).OrderBy(x => x).ToArray();
        Assert.Equal(new[] { "fresh", "keep" }, tagNames);
        Assert.DoesNotContain(tagNames, t => t == "obsolete");
        Assert.Single(note.Attachments);
        Assert.NotEqual(oldStored, note.Attachments.Single().StoredFileName);
        Assert.True(db.NoteRevisions.Count(r => r.NoteId == note.Id) >= 2);
        var ftsHits = verify.Database.SqlQueryRaw<int>(
            "SELECT NoteId AS Value FROM NotesFts WHERE NotesFts MATCH {0}", "v2").ToList();
        Assert.Contains(note.Id, ftsHits);
    }

    [Fact]
    public void Replace_InjectedFailure_RestoresOldGraphAndKeepsSharedFiles()
    {
        string attDir = Path.Combine(_root, "repl-fail-att");
        var storage = new AttachmentStorageService(attDir);
        string dir = Path.Combine(_root, "repl-fail-src");
        Directory.CreateDirectory(dir);
        byte[] shared = { 10, 20, 30, 40 };
        File.WriteAllBytes(Path.Combine(dir, "pic.bin"), shared);
        File.WriteAllText(Path.Combine(dir, "note.md"), "---\ntags: [oldtag]\n---\nold body\n\n![p](pic.bin)\n", Encoding.UTF8);

        using var db = Db();
        var svc = new NoteImportService();
        var p1 = svc.BuildPreviewFromDirectory(db, dir);
        p1.LossesAcknowledged = true;
        Assert.True(svc.ExecuteImport(db, p1, Coord(), attachmentStorage: storage).Success);
        var original = db.Notes.Include(n => n.NoteTags).ThenInclude(nt => nt.Tag).Include(n => n.Attachments).Single();
        int noteId = original.Id;
        string oldText = original.Text;
        string[] oldTags = original.NoteTags.Select(nt => nt.Tag!.Name).OrderBy(x => x).ToArray();
        string oldStored = original.Attachments.Single().StoredFileName;
        string oldFull = Path.Combine(storage.AttachmentsDirectory, oldStored);
        Assert.True(File.Exists(oldFull));

        File.WriteAllBytes(Path.Combine(dir, "pic.bin"), new byte[] { 99, 98, 97 });
        File.WriteAllText(Path.Combine(dir, "note.md"), "---\ntags: [newtag]\n---\nnew body\n\n![p](pic.bin)\n", Encoding.UTF8);
        var p2 = svc.BuildPreviewFromDirectory(db, dir);
        p2.LossesAcknowledged = true;
        p2.Items.Single(i => i.SourceFileName == "note.md").DuplicateAction = ImportDuplicateAction.Replace;
        svc.RecalculatePreviewTotals(db, p2);
        NoteImportService.TestInjectFailure = p =>
        {
            if (p == ImportTransactionPhase.Publish)
            {
                throw new InvalidOperationException("injected replace publish");
            }
        };

        var exec = svc.ExecuteImport(db, p2, Coord(), attachmentStorage: storage);
        Assert.False(exec.Success);
        using var verify = Db();
        var restored = verify.Notes.Include(n => n.NoteTags).ThenInclude(nt => nt.Tag).Include(n => n.Attachments).Single(n => n.Id == noteId);
        Assert.Equal(oldText, restored.Text);
        Assert.Equal(oldTags, restored.NoteTags.Select(nt => nt.Tag!.Name).OrderBy(x => x).ToArray());
        Assert.Equal(oldStored, restored.Attachments.Single().StoredFileName);
        Assert.True(File.Exists(oldFull));
        Assert.Equal(shared, File.ReadAllBytes(oldFull));
        Assert.DoesNotContain(verify.Notes.AsEnumerable(), n => (n.Text ?? string.Empty).Contains("new body", StringComparison.Ordinal));
    }

    [Fact]
    public void DirectoryDiscovery_ReportsNestedWordAndUnsupported_WithoutParsingBinary()
    {
        string dir = Path.Combine(_root, "mix");
        Directory.CreateDirectory(Path.Combine(dir, "nested"));
        File.WriteAllText(Path.Combine(dir, "ok.md"), "заметка", Encoding.UTF8);
        byte[] hostile = { 0, 1, 2, 0, 0xD0, 0xCF, 0x11, 0xE0 };
        File.WriteAllBytes(Path.Combine(dir, "letter.docx"), hostile);
        File.WriteAllBytes(Path.Combine(dir, "nested", "section.one"), hostile);
        File.WriteAllBytes(Path.Combine(dir, "nested", "payload.exe"), hostile);
        using var db = Db();
        var preview = new NoteImportService().BuildPreviewFromDirectory(db, dir);
        Assert.Contains(preview.Diagnostics, d => d.FileName == "letter.docx" && d.IsLoss && d.Reason.Contains("HTML", StringComparison.Ordinal));
        Assert.Contains(preview.Diagnostics, d => d.FileName == "section.one" && d.Reason.Contains("OneNote", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(preview.Diagnostics, d => d.FileName == "payload.exe" && d.IsLoss && d.Reason.Contains("HTML", StringComparison.Ordinal));
        Assert.DoesNotContain(preview.Diagnostics, d => d.Reason.Contains("бинарные", StringComparison.OrdinalIgnoreCase));
        Assert.True(preview.HasNonBlockingLosses);
        Assert.True(preview.TotalSkippedOrErroneous >= 3);
        Assert.Contains(preview.Items, i => i.SourceFileName == "ok.md");
        Assert.False(preview.CanConfirm);
        preview.LossesAcknowledged = true;
        Assert.True(preview.CanConfirm);
    }

    [Fact]
    public void SourceChangedAfterPreview_FailsWithoutWrites()
    {
        string file = Path.Combine(_root, "chg.md");
        File.WriteAllText(file, "до", Encoding.UTF8);
        using var db = Db();
        var svc = new NoteImportService();
        var preview = svc.BuildPreviewFromFiles(db, new[] { file });
        File.WriteAllText(file, "после изменения", Encoding.UTF8);
        var exec = svc.ExecuteImport(db, preview, Coord());
        Assert.False(exec.Success);
        Assert.Contains("изменился", exec.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, db.Notes.Count());
    }

    [Fact]
    public void RepeatedExecute_IsIdempotent()
    {
        string file = Path.Combine(_root, "id.md");
        File.WriteAllText(file, "idem", Encoding.UTF8);
        using var db = Db();
        var svc = new NoteImportService();
        var preview = svc.BuildPreviewFromFiles(db, new[] { file });
        Assert.True(svc.ExecuteImport(db, preview, Coord()).Success);
        var again = svc.ExecuteImport(db, preview, Coord());
        Assert.True(again.Success);
        Assert.Equal(1, db.Notes.Count());
    }

    [Theory]
    [InlineData(ImportTransactionPhase.Parse)]
    [InlineData(ImportTransactionPhase.Validation)]
    [InlineData(ImportTransactionPhase.Database)]
    [InlineData(ImportTransactionPhase.History)]
    [InlineData(ImportTransactionPhase.Attachment)]
    [InlineData(ImportTransactionPhase.Publish)]
    public void FailureInjection_RollsBackDbAndFiles(ImportTransactionPhase phase)
    {
        string dir = Path.Combine(_root, "phase-" + phase);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "n.md"), "текст фазы\n\n![a](pic.bin)", Encoding.UTF8);
        File.WriteAllBytes(Path.Combine(dir, "pic.bin"), new byte[] { 1, 2, 3, 4 });

        using var db = Db();
        var svc = new NoteImportService();
        var preview = svc.BuildPreviewFromDirectory(db, dir);
        preview.LossesAcknowledged = true;
        NoteImportService.TestInjectFailure = p =>
        {
            if (p == phase)
            {
                throw new InvalidOperationException("injected " + phase);
            }
        };

        var storage = new AttachmentStorageService(Path.Combine(_root, "att-" + phase));
        var exec = svc.ExecuteImport(db, preview, Coord(), attachmentStorage: storage);
        Assert.False(exec.Success);
        Assert.Equal(0, db.Notes.Count());
        Assert.Equal(0, db.Tags.Count());
        Assert.Equal(0, db.NoteRevisions.Count());
        Assert.Equal(0, db.NoteAttachments.Count());
        if (Directory.Exists(storage.AttachmentsDirectory))
        {
            Assert.Empty(Directory.GetFiles(storage.AttachmentsDirectory, "*", SearchOption.AllDirectories)
                .Where(f => !f.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)));
        }
    }

    [Fact]
    public void HtmlIsNotStoredAsCanonicalFormat()
    {
        string file = Path.Combine(_root, "c.html");
        File.WriteAllText(file, "<h1>T</h1><p>body</p>", Encoding.UTF8);
        using var db = Db();
        var svc = new NoteImportService();
        var preview = svc.BuildPreviewFromFiles(db, new[] { file });
        Assert.True(svc.ExecuteImport(db, preview, Coord()).Success);
        var note = db.Notes.Single();
        Assert.DoesNotContain("<h1>", note.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("# T", note.Text);
    }

    [Fact]
    public void ConcurrentPreviewDoesNotWrite()
    {
        string file = Path.Combine(_root, "p.md");
        File.WriteAllText(file, "preview only", Encoding.UTF8);
        using var db = Db();
        var svc = new NoteImportService();
        Parallel.For(0, 8, _ =>
        {
            using var inner = Db();
            svc.BuildPreviewFromFiles(inner, new[] { file });
        });
        Assert.Equal(0, db.Notes.Count());
    }

    [Fact]
    public void NestedMutationCoordinator_ThrowsWithoutPartialImport()
    {
        string file = Path.Combine(_root, "nested.md");
        File.WriteAllText(file, "nested import body", Encoding.UTF8);
        var coordinator = new LocalMutationCoordinator();
        var svc = new NoteImportService();
        using var db = Db();
        var preview = svc.BuildPreviewFromFiles(db, new[] { file });
        Assert.Equal(1, preview.TotalNotesToImport);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            coordinator.ExecuteBulkMutation(() =>
                svc.ExecuteImport(db, preview, coordinator)));

        Assert.Contains("Nested", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, db.Notes.Count());
        Assert.Equal(0, db.NoteRevisions.Count());
        Assert.False(coordinator.IsBulkRunning);
    }

    [Fact]
    public async Task SyncApply_DoesNotInterleaveWithImportCommit()
    {
        string file = Path.Combine(_root, "race.md");
        File.WriteAllText(file, "import under sync apply", Encoding.UTF8);
        var coordinator = new LocalMutationCoordinator();
        var svc = new NoteImportService();
        ImportPreviewResult preview;
        using (var db = Db())
        {
            preview = svc.BuildPreviewFromFiles(db, new[] { file });
        }

        using var entered = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        var syncTask = Task.Run(async () =>
        {
            await coordinator.ExecuteSyncApplyAsync(() =>
            {
                entered.Set();
                if (!release.Wait(TimeSpan.FromSeconds(20)))
                {
                    throw new TimeoutException("sync apply release timed out");
                }

                return Task.FromResult(true);
            });
        });
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));

        ImportExecutionResult? result = null;
        Exception? importError = null;
        var importThread = new Thread(() =>
        {
            try
            {
                using var db = Db();
                result = svc.ExecuteImport(db, preview, coordinator);
            }
            catch (Exception ex)
            {
                importError = ex;
            }
        })
        {
            IsBackground = true
        };
        importThread.Start();
        Assert.True(coordinator.IsBulkRunning);
        Assert.False(importThread.Join(50));
        using (var db = Db())
        {
            Assert.Equal(0, db.Notes.Count());
        }

        release.Set();
        await syncTask;
        Assert.True(importThread.Join(TimeSpan.FromSeconds(10)));
        Assert.Null(importError);
        Assert.NotNull(result);
        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(1, result.ImportedNotesCount);

        using var verify = Db();
        Assert.Equal(1, verify.Notes.Count());
        Assert.False(coordinator.IsBulkRunning);
    }

    [Fact]
    public void CancelledImport_LeavesNoPartialNotes()
    {
        string file = Path.Combine(_root, "cancel.md");
        File.WriteAllText(file, "cancel import body", Encoding.UTF8);
        var svc = new NoteImportService();
        using var db = Db();
        var preview = svc.BuildPreviewFromFiles(db, new[] { file });
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var exec = svc.ExecuteImport(db, preview, Coord(), cancellationToken: cts.Token);
        Assert.False(exec.Success);
        Assert.Contains("отмен", exec.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, db.Notes.Count());
        Assert.Equal(0, db.NoteRevisions.Count());
    }
}
