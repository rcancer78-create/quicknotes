using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using QuickNotes.App.Data;
using QuickNotes.App.Models;
using QuickNotes.App.Services;
using QuickNotes.App.Services.Sync;
using Xunit;

namespace QuickNotes.Tests;

[TestCategory(TestCategories.Integration)]
public sealed class ArchiveImportMutationBoundaryTests : IDisposable
{
    private readonly string _dir;

    public ArchiveImportMutationBoundaryTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "qn_arch_mut_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        NoteArchiveService.TestInjectFailure = null;
        NoteArchiveService.TestBeforeCommit = null;
        SqliteTestUtil.TryDeleteDirectory(_dir);
    }

    [Fact]
    public async Task SyncApply_DoesNotInterleaveWithPortableArchiveImport()
    {
        string archivePath = CreateArchiveWithNotes("archive race");
        string destDb = Path.Combine(_dir, "dest-race.sqlite");
        InitializeDb(destDb);
        var coordinator = new LocalMutationCoordinator();
        var service = new NoteArchiveService();
        ImportPreviewResult preview;
        using (var db = SqliteTestUtil.CreateContext(destDb))
        {
            preview = service.PreviewArchive(db, archivePath);
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
                using var db = SqliteTestUtil.CreateContext(destDb);
                result = service.ImportArchive(
                    db,
                    preview,
                    new AttachmentStorageService(Path.Combine(_dir, "att-race")),
                    coordinator);
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
        using (var db = SqliteTestUtil.CreateContext(destDb))
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

        using var verify = SqliteTestUtil.CreateContext(destDb);
        Assert.Equal(1, verify.Notes.Count());
        Assert.False(coordinator.IsBulkRunning);
    }

    [Fact]
    public void CancelledArchiveImport_LeavesNoPartialMutation()
    {
        string archivePath = CreateArchiveWithNotes("cancel archive");
        string destDb = Path.Combine(_dir, "dest-cancel.sqlite");
        InitializeDb(destDb);
        var service = new NoteArchiveService();
        using var db = SqliteTestUtil.CreateContext(destDb);
        var preview = service.PreviewArchive(db, archivePath);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var exec = service.ImportArchive(
            db,
            preview,
            new AttachmentStorageService(Path.Combine(_dir, "att-cancel")),
            new LocalMutationCoordinator(),
            cancellationToken: cts.Token);
        Assert.False(exec.Success);
        Assert.Contains("отмен", exec.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, db.Notes.Count());
        Assert.Equal(0, db.NoteRevisions.Count());
        Assert.Equal(0, db.NoteAttachments.Count());
        Assert.Equal(0, db.Tags.Count());
    }

    [Fact]
    public void FailedArchiveImport_LeavesNoPartialMutation()
    {
        string archivePath = CreateArchiveWithNotes("fail archive");
        string destDb = Path.Combine(_dir, "dest-fail.sqlite");
        InitializeDb(destDb);
        var service = new NoteArchiveService();
        using var db = SqliteTestUtil.CreateContext(destDb);
        var preview = service.PreviewArchive(db, archivePath);
        var storage = new AttachmentStorageService(Path.Combine(_dir, "att-fail"));
        NoteArchiveService.TestInjectFailure = () => throw new InvalidOperationException("injected archive failure");
        var exec = service.ImportArchive(db, preview, storage, new LocalMutationCoordinator());
        Assert.False(exec.Success);
        Assert.Contains("injected archive failure", exec.ErrorMessage, StringComparison.Ordinal);
        Assert.Equal(0, db.Notes.Count());
        Assert.Equal(0, db.NoteRevisions.Count());
        Assert.Equal(0, db.NoteAttachments.Count());
        Assert.Equal(0, db.Tags.Count());
        if (Directory.Exists(storage.AttachmentsDirectory))
        {
            Assert.Empty(Directory.GetFiles(storage.AttachmentsDirectory, "*", SearchOption.AllDirectories)
                .Where(f => !f.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)));
        }
    }

    [Fact]
    public void NestedMutationCoordinator_OnArchiveImport_ThrowsWithoutPartialMutation()
    {
        string archivePath = CreateArchiveWithNotes("nested archive");
        string destDb = Path.Combine(_dir, "dest-nested.sqlite");
        InitializeDb(destDb);
        var coordinator = new LocalMutationCoordinator();
        var service = new NoteArchiveService();
        using var db = SqliteTestUtil.CreateContext(destDb);
        var preview = service.PreviewArchive(db, archivePath);
        Assert.Equal(1, preview.TotalNotesToImport);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            coordinator.ExecuteBulkMutation(() =>
                service.ImportArchive(
                    db,
                    preview,
                    new AttachmentStorageService(Path.Combine(_dir, "att-nested")),
                    coordinator)));

        Assert.Contains("Nested", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, db.Notes.Count());
        Assert.Equal(0, db.NoteRevisions.Count());
        Assert.False(coordinator.IsBulkRunning);
    }

    [Fact]
    public void PreviewArchive_DoesNotRequireCoordinator_AndDoesNotWrite()
    {
        string archivePath = CreateArchiveWithNotes("preview only");
        string destDb = Path.Combine(_dir, "dest-preview.sqlite");
        InitializeDb(destDb);
        var service = new NoteArchiveService();
        using var db = SqliteTestUtil.CreateContext(destDb);
        var preview = service.PreviewArchive(db, archivePath);
        Assert.True(preview.IsPortableArchive);
        Assert.Equal(1, preview.TotalNotesToImport);
        Assert.Equal(0, db.Notes.Count());
        Assert.Equal(0, db.Tags.Count());
    }

    private static void InitializeDb(string dbPath)
    {
        string? dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }

        using var db = SqliteTestUtil.CreateContext(dbPath);
        DbInitializer.Initialize(db);
    }

    private string CreateArchiveWithNotes(string text)
    {
        string srcDb = Path.Combine(_dir, "src-" + Guid.NewGuid().ToString("N") + ".sqlite");
        InitializeDb(srcDb);
        var attachDir = Path.Combine(_dir, "src-att-" + Guid.NewGuid().ToString("N"));
        var storage = new AttachmentStorageService(attachDir);
        using (var db = SqliteTestUtil.CreateContext(srcDb))
        {
            db.Notes.Add(new Note { Text = text, SyncId = Guid.NewGuid() });
            db.SaveChanges();
        }

        string archivePath = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".qnarchive.zip");
        var service = new NoteArchiveService();
        using (var db = SqliteTestUtil.CreateContext(srcDb))
        {
            Assert.True(service.ExportArchive(db, archivePath, storage).Success);
        }

        return archivePath;
    }
}
