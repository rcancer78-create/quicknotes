using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using QuickNotes.App.Data;
using QuickNotes.App.Models;
using QuickNotes.App.Services;
using QuickNotes.App.ViewModels;
using QuickNotes.App.Services.Sync;
using Xunit;

namespace QuickNotes.Tests;

[TestCategory(TestCategories.Integration)]
public class OpenExportTests : IDisposable
{
    private readonly string _tempDirectory;
    private readonly string _dbPath;

    public OpenExportTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "QuickNotes_OpenExport_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);
        _dbPath = Path.Combine(_tempDirectory, "test.db");
        using var db = CreateContext();
        DbInitializer.Initialize(db);
    }

    public void Dispose()
    {
        SqliteTestUtil.TryDeleteDirectory(_tempDirectory);
    }

    private QuickNotesDbContext CreateContext() => SqliteTestUtil.CreateContext(_dbPath);

    [Fact]
    public void OpenExport_OmitsProtectedNotePlaintextAndAttachments_IncludesOpenNote()
    {
        var storage = new AttachmentStorageService(Path.Combine(_tempDirectory, "att"));
        const string secretText = "SECRET_PROTECTED_PLAINTEXT_MUST_NOT_EXPORT";
        const string secretFileName = "secret-protected.bin";
        const string openText = "Open note with title line";

        using (var db = CreateContext())
        {
            var open = new Note
            {
                Text = openText,
                SourceProcessName = "notepad.exe",
                SourceWindowTitle = "Open window",
                SourceUrl = "https://example.local/open"
            };
            var protectedNote = new Note { Text = secretText, IsProtected = true };
            db.Notes.AddRange(open, protectedNote);
            db.SaveChanges();

            db.NoteAttachments.Add(ToAttachment(open.Id, storage.SaveFromBytes(Encoding.UTF8.GetBytes("open-bytes"), "open.txt", 1024 * 1024)));
            db.NoteAttachments.Add(ToAttachment(
                protectedNote.Id,
                storage.SaveFromBytes(Encoding.UTF8.GetBytes("SECRET_ATTACHMENT_BYTES"), secretFileName, 1024 * 1024),
                isProtected: true));
            db.SaveChanges();
        }

        var exportDir = Path.Combine(_tempDirectory, "snapshot");
        var exporter = new NoteExportService();
        using (var db = CreateContext())
        {
            var res = exporter.ExportToMarkdown(db, exportDir, storage);
            Assert.True(res.Success, res.ErrorMessage);
            Assert.Equal(1, res.ExportedNotesCount);
            Assert.Equal(1, res.SkippedProtectedCount);
            Assert.Equal(1, res.ExportedAttachmentCount);
            Assert.False(res.IsComplete);
        }

        var snapshotText = ReadAllSnapshotText(exportDir);
        Assert.Contains(openText, snapshotText);
        Assert.DoesNotContain(secretText, snapshotText);
        Assert.DoesNotContain(secretFileName, snapshotText);
        Assert.DoesNotContain("SECRET_ATTACHMENT_BYTES", snapshotText);
        Assert.Contains("open.txt", snapshotText);

        using var jsonDb = CreateContext();
        var jsonPath = Path.Combine(_tempDirectory, "open.json");
        var jsonRes = exporter.ExportToJson(jsonDb, jsonPath);
        Assert.True(jsonRes.Success, jsonRes.ErrorMessage);
        var json = File.ReadAllText(jsonPath, Encoding.UTF8);
        Assert.Contains(openText, json);
        Assert.DoesNotContain(secretText, json);
    }

    [Fact]
    public void OpenExport_WritesAvailableAttachments_ManifestCountsErrorsAndChecksums()
    {
        var storage = new AttachmentStorageService(Path.Combine(_tempDirectory, "att"));
        var payload = new byte[] { 10, 20, 30, 40, 50 };

        using (var db = CreateContext())
        {
            var tagged = new Tag { Name = "export-tag" };
            db.Tags.Add(tagged);
            db.SaveChanges();

            var target = new Note { Text = "Linked target note" };
            var withFile = new Note
            {
                Text = "# Linked source\nSee placeholder",
                SourceProcessName = "notepad.exe",
                SourceWindowTitle = "Demo window",
                SourceUrl = "https://example.test/src"
            };
            var missingAtt = new Note { Text = "Missing attachment note" };
            db.Notes.AddRange(target, withFile, missingAtt);
            db.SaveChanges();
            withFile.Text = $"# Linked source\nSee [[{target.Id}]]";

            db.NoteTags.Add(new NoteTag { NoteId = withFile.Id, TagId = tagged.Id, Origin = TagOrigin.Manual });
            db.NoteAttachments.Add(ToAttachment(withFile.Id, storage.SaveFromBytes(payload, "photo.png", 1024 * 1024)));
            db.NoteAttachments.Add(new NoteAttachment
            {
                NoteId = missingAtt.Id,
                OriginalFileName = "gone.bin",
                StoredFileName = "gone.bin",
                RelativePath = "missing/gone.bin",
                ContentType = "application/octet-stream",
                Size = 4,
                Sha256 = "deadbeef"
            });
            db.SaveChanges();
        }

        var exportDir = Path.Combine(_tempDirectory, "snapshot");
        var exporter = new NoteExportService();
        using (var db = CreateContext())
        {
            var res = exporter.ExportToMarkdown(db, exportDir, storage);
            Assert.True(res.Success, res.ErrorMessage);
            Assert.Equal(3, res.ExportedNotesCount);
            Assert.Equal(1, res.ExportedAttachmentCount);
            Assert.Equal(1, res.SkippedMissingAttachmentCount);
            Assert.True(res.ErrorCount >= 1);
            Assert.False(res.IsComplete);
            Assert.Contains("неполный", res.CompletenessSummary);
        }

        var manifest = JsonSerializer.Deserialize<OpenExportManifest>(
            File.ReadAllText(Path.Combine(exportDir, "manifest.json"), Encoding.UTF8),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.NotNull(manifest);
        Assert.Equal(OpenExportManifest.CurrentFormatVersion, manifest!.FormatVersion);
        Assert.Equal(OpenExportManifest.FormatName, manifest.Format);
        Assert.True(manifest.ExportedAtUtc > DateTime.UtcNow.AddHours(-1));
        Assert.Equal(3, manifest.EntityCounts.Notes);
        Assert.True(manifest.EntityCounts.Tags >= 1);
        Assert.Equal(1, manifest.EntityCounts.Attachments);
        Assert.Equal(1, manifest.SkippedMissingAttachments);
        Assert.True(manifest.ErrorCount >= 1);
        Assert.NotEmpty(manifest.Errors);
        Assert.NotEmpty(manifest.Files);
        Assert.Contains(manifest.Files, f => f.Path.EndsWith(".md", StringComparison.OrdinalIgnoreCase) && f.Sha256.Length == 64);
        Assert.Contains(manifest.Files, f => f.Path.Contains("photo.png", StringComparison.OrdinalIgnoreCase) && f.Sha256.Length == 64);

        foreach (var file in manifest.Files)
        {
            var full = Path.Combine(exportDir, file.Path.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(full), file.Path);
            Assert.Equal(file.Sha256, AttachmentFileHelper.ComputeSha256(full), ignoreCase: true);
        }

        var copied = Directory.GetFiles(exportDir, "photo.png", SearchOption.AllDirectories);
        Assert.Single(copied);
        Assert.Equal(payload, File.ReadAllBytes(copied[0]));

        var md = Directory.GetFiles(exportDir, "*.md").Select(File.ReadAllText).First(t => t.Contains("Linked source"));
        Assert.Contains("title:", md);
        Assert.Contains("export-tag", md);
        Assert.Contains("links:", md);
        Assert.Contains("Linked target note", md);
        Assert.Contains("https://example.test/src", md);
        Assert.Contains("notepad.exe", md);
        Assert.Contains("attachments:", md);
    }

    [Fact]
    public void OpenExport_InjectedFailureBeforePublish_PreservesPreviousSnapshot()
    {
        var exportDir = Path.Combine(_tempDirectory, "snapshot");
        const string marker = "PREVIOUS_SNAPSHOT_MARKER";
        using (var db = CreateContext())
        {
            db.Notes.Add(new Note { Text = marker });
            db.SaveChanges();
        }

        var exporter = new NoteExportService();
        using (var db = CreateContext())
        {
            Assert.True(exporter.ExportToMarkdown(db, exportDir).Success);
        }

        using (var db = CreateContext())
        {
            db.Notes.Add(new Note { Text = "NEW_NOTE_AFTER_FAILURE" });
            db.SaveChanges();
        }

        exporter.BeforePublishForTests = _ => throw new InvalidOperationException("injected staging failure");
        using (var db = CreateContext())
        {
            var failed = exporter.ExportToMarkdown(db, exportDir);
            Assert.False(failed.Success);
            Assert.Contains("injected staging failure", failed.ErrorMessage);
        }

        var snapshot = ReadAllSnapshotText(exportDir);
        Assert.Contains(marker, snapshot);
        Assert.DoesNotContain("NEW_NOTE_AFTER_FAILURE", snapshot);
        Assert.Empty(Directory.GetDirectories(Path.GetDirectoryName(exportDir)!, ".tmp_export_*"));
        Assert.Empty(Directory.GetDirectories(Path.GetDirectoryName(exportDir)!, ".bak_export_*"));
    }

    [Fact]
    public void OpenExport_InjectedFailureAfterMoveAside_RestoresPreviousSnapshot()
    {
        var exportDir = Path.Combine(_tempDirectory, "snapshot");
        const string marker = "SNAPSHOT_BEFORE_SWAP";
        using (var db = CreateContext())
        {
            db.Notes.Add(new Note { Text = marker });
            db.SaveChanges();
        }

        var exporter = new NoteExportService();
        using (var db = CreateContext())
        {
            Assert.True(exporter.ExportToMarkdown(db, exportDir).Success);
        }

        using (var db = CreateContext())
        {
            db.Notes.Add(new Note { Text = "SHOULD_NOT_PUBLISH" });
            db.SaveChanges();
        }

        exporter.AfterTargetMovedAsideForTests = _ => throw new IOException("injected publish failure");
        using (var db = CreateContext())
        {
            Assert.False(exporter.ExportToMarkdown(db, exportDir).Success);
        }

        Assert.Contains(marker, ReadAllSnapshotText(exportDir));
        Assert.DoesNotContain("SHOULD_NOT_PUBLISH", ReadAllSnapshotText(exportDir));
        Assert.Empty(Directory.GetDirectories(Path.GetDirectoryName(exportDir)!, ".bak_export_*"));
    }

    [Fact]
    public async Task ImportExportViewModel_OpenExport_RequiresFolderAndPlaintextConfirm_PersistsLastSuccess()
    {
        using (var db = CreateContext())
        {
            db.Notes.Add(new Note { Text = "VM open export" });
            db.SaveChanges();
        }

        var settingsPath = Path.Combine(_tempDirectory, "settings.json");
        var settings = new SettingsService(settingsPath, _ => { });
        var folder = Path.Combine(_tempDirectory, "vm-snapshot");

        var vm = new ImportExportViewModel(
            CreateContext,
            new LocalMutationCoordinator(),
            new NoteExportService(),
            new NoteImportService(),
            attachmentStorage: new AttachmentStorageService(Path.Combine(_tempDirectory, "att")),
            settingsService: settings)
        {
            FolderBrowserDialogProvider = () => folder,
            ConfirmProvider = (msg, _) =>
            {
                Assert.Contains("plaintext", msg, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("не зашифрованный архив", msg, StringComparison.OrdinalIgnoreCase);
                return true;
            },
            MessageBoxProvider = (_, _, _) => { }
        };

        Assert.False(vm.HasLastSuccessfulExport);
        await vm.ExecuteExportMarkdownAsync();
        Assert.True(vm.IsExportSuccess);
        Assert.True(vm.HasLastSuccessfulExport);
        Assert.Equal(folder, vm.LastOpenExportPath);
        Assert.True(File.Exists(Path.Combine(folder, "manifest.json")));

        var reloaded = new SettingsService(settingsPath, _ => { });
        Assert.Equal(folder, reloaded.CurrentSettings.LastOpenExportPath);

        var vm2 = ImportExportViewModelTestComposition.Create(CreateContext, new NoteExportService(), settingsService: reloaded);
        Assert.True(vm2.HasLastSuccessfulExport);
        Assert.Equal(folder, vm2.LastOpenExportPath);
    }

    [Fact]
    public async Task ImportExportViewModel_OpenExport_CancelWarningOrFolder_DoesNotExport()
    {
        using (var db = CreateContext())
        {
            db.Notes.Add(new Note { Text = "should stay local" });
            db.SaveChanges();
        }

        var folder = Path.Combine(_tempDirectory, "cancelled");
        var vm = new ImportExportViewModel(CreateContext, new LocalMutationCoordinator(), new NoteExportService())
        {
            FolderBrowserDialogProvider = () => null,
            ConfirmProvider = (_, _) => true,
            MessageBoxProvider = (_, _, _) => { }
        };
        await vm.ExecuteExportMarkdownAsync();
        Assert.False(Directory.Exists(folder));

        vm.FolderBrowserDialogProvider = () => folder;
        vm.ConfirmProvider = (_, _) => false;
        await vm.ExecuteExportMarkdownAsync();
        Assert.False(Directory.Exists(folder));
        Assert.False(vm.HasLastSuccessfulExport);
    }

    [Fact]
    public void ImportExportWindowXaml_ShowsLastSuccessfulOpenExportFields()
    {
        var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
        string? root = null;
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "QuickNotes.sln")))
            {
                root = dir.FullName;
                break;
            }

            dir = dir.Parent;
        }

        Assert.NotNull(root);
        var xaml = File.ReadAllText(Path.Combine(root!, "QuickNotes.App", "Views", "ImportExportWindow.xaml"));
        Assert.Contains("LastSuccessfulExportBanner", xaml);
        Assert.Contains("ExportMarkdownCommand", xaml);
        Assert.Contains("plaintext", xaml, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ArchiveExport_OmitsProtectedPlaintext()
    {
        var storage = new AttachmentStorageService(Path.Combine(_tempDirectory, "att"));
        const string secret = "ARCHIVE_SECRET_TEXT";
        using (var db = CreateContext())
        {
            db.Notes.Add(new Note { Text = "visible archive note" });
            db.Notes.Add(new Note { Text = secret, IsProtected = true });
            db.SaveChanges();
        }

        var zipPath = Path.Combine(_tempDirectory, "pack.qnarchive.zip");
        using (var db = CreateContext())
        {
            var res = new NoteArchiveService().ExportArchive(db, zipPath, storage);
            Assert.True(res.Success, res.ErrorMessage);
            Assert.Equal(1, res.ExportedNotesCount);
            Assert.Equal(1, res.SkippedProtectedCount);
        }

        using var zip = ZipFile.OpenRead(zipPath);
        var combined = new StringBuilder();
        foreach (var entry in zip.Entries)
        {
            using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
            combined.Append(reader.ReadToEnd());
        }

        Assert.Contains("visible archive note", combined.ToString());
        Assert.DoesNotContain(secret, combined.ToString());
    }

    private static NoteAttachment ToAttachment(int noteId, AttachmentSaveResult saved, bool isProtected = false)
        => new()
        {
            NoteId = noteId,
            OriginalFileName = saved.OriginalFileName,
            StoredFileName = saved.StoredFileName,
            RelativePath = saved.RelativePath,
            ContentType = saved.ContentType,
            Size = saved.Size,
            Sha256 = saved.Sha256,
            IsProtected = isProtected
        };

    private static string ReadAllSnapshotText(string exportDir)
    {
        var sb = new StringBuilder();
        foreach (var file in Directory.GetFiles(exportDir, "*", SearchOption.AllDirectories))
        {
            sb.AppendLine(file);
            try
            {
                sb.AppendLine(File.ReadAllText(file, Encoding.UTF8));
            }
            catch
            {
                sb.AppendLine(Convert.ToHexString(File.ReadAllBytes(file)));
            }
        }

        return sb.ToString();
    }
}
