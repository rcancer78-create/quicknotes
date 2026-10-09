using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using QuickNotes.App.Data;
using QuickNotes.App.Helpers;
using QuickNotes.App.Models;
using QuickNotes.App.Services;
using QuickNotes.App.Services.Sync;
using QuickNotes.App.ViewModels;
using Xunit;

namespace QuickNotes.Tests;

[TestCategory(TestCategories.Integration)]
public sealed class PortabilityAndDailyTests : IDisposable
{
    private readonly string _dir;
    private readonly SqliteConnectionHolder _db;

    public PortabilityAndDailyTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "qn_port_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _db = SqliteConnectionHolder.Create();
    }

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_dir, true); } catch { /* ignore */ }
    }

    [Fact]
    public void FormatLink_UsesPortableSyncId()
    {
        var service = new NoteLinkService();
        var syncId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        Assert.Equal("[[qn:aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee]]", service.FormatLink(syncId));
        Assert.Equal("[[154]]", service.FormatLink(154));
    }

    [Fact]
    public void ExtractLinkRefs_ReadsLegacyAndPortableLinks()
    {
        var service = new NoteLinkService();
        var syncId = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var refs = service.ExtractLinkRefs($"см. [[42]] и [[qn:{syncId:D}]] и [[{syncId:D}]]");
        Assert.Contains(refs, r => r.LocalId == 42);
        Assert.Equal(2, refs.Count(r => r.SyncId == syncId));
    }

    [Fact]
    public void RewriteLegacyLinks_ReplacesKnownIdsOnly()
    {
        var service = new NoteLinkService();
        var map = new Dictionary<int, Guid> { [154] = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee") };
        var rewritten = service.RewriteLegacyLinks("Ссылка [[154]] и неизвестная [[999]]", map);
        Assert.Contains("[[qn:aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee]]", rewritten);
        Assert.Contains("[[999]]", rewritten);
    }

    [Fact]
    public void OutgoingLinks_ResolvePortableSyncId_IndependentOfLocalId()
    {
        using var db = _db.CreateContext();
        var target = new Note { Text = "Цель", SyncId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee") };
        var source = new Note { Text = "См. [[qn:aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee]]" };
        db.Notes.AddRange(target, source);
        db.SaveChanges();

        var links = new NoteLinkService().GetOutgoingLinks(db, source.Text);
        Assert.Single(links);
        Assert.True(links[0].IsAvailable);
        Assert.Equal(target.Id, links[0].TargetNoteId);
        Assert.Equal("Цель", links[0].Title);
    }

    [Fact]
    public void IncomingLinks_FindPortableAndLegacyMarkers()
    {
        using var db = _db.CreateContext();
        var target = new Note { Text = "Цель" };
        db.Notes.Add(target);
        db.SaveChanges();

        db.Notes.Add(new Note { Text = $"старая [[{target.Id}]]" });
        db.Notes.Add(new Note { Text = $"новая [[qn:{target.SyncId:D}]]" });
        db.SaveChanges();

        var incoming = new NoteLinkService().GetIncomingLinks(db, target.Id);
        Assert.Equal(2, incoming.Count);
    }

    [Fact]
    public void Archive_Roundtrip_IncludesBinaryAttachment_WithoutOverwrite()
    {
        var attachDir = Path.Combine(_dir, "att");
        var storage = new AttachmentStorageService(attachDir);
        using (var db = _db.CreateContext())
        {
            var note = new Note { Text = "С вложением", SyncId = Guid.NewGuid() };
            db.Notes.Add(note);
            db.SaveChanges();
            var bytes = new byte[] { 1, 2, 3, 4, 9 };
            var saved = storage.SaveFromBytes(bytes, "photo.png", 1024 * 1024);
            db.NoteAttachments.Add(new NoteAttachment
            {
                NoteId = note.Id,
                OriginalFileName = saved.OriginalFileName,
                StoredFileName = saved.StoredFileName,
                RelativePath = saved.RelativePath,
                ContentType = saved.ContentType,
                Size = saved.Size,
                Sha256 = saved.Sha256
            });
            db.SaveChanges();
        }

        var archivePath = Path.Combine(_dir, "pack.qnarchive.zip");
        var service = new NoteArchiveService();
        using (var db = _db.CreateContext())
        {
            var exported = service.ExportArchive(db, archivePath, storage);
            Assert.True(exported.Success);
        }

        Assert.True(File.Exists(archivePath));

        using (var db = _db.CreateContext())
        {
            var preview = service.PreviewArchive(db, archivePath);
            Assert.True(preview.IsPortableArchive);
            Assert.True(preview.TotalConflicts >= 1);
            Assert.Equal(0, preview.TotalNotesToImport);
        }

        using var empty = SqliteConnectionHolder.Create();
        using (var db2 = empty.CreateContext())
        {
            var preview = service.PreviewArchive(db2, archivePath);
            Assert.Equal(1, preview.TotalNotesToImport);
            var imported = service.ImportArchive(db2, preview, new AttachmentStorageService(Path.Combine(_dir, "att2")), new LocalMutationCoordinator());
            Assert.True(imported.Success);
            Assert.Equal(1, imported.ImportedNotesCount);
            Assert.Equal(1, db2.NoteAttachments.Count());
        }

        empty.Dispose();
    }

    [Fact]
    public void Archive_RestoresTagParentAndSynonym()
    {
        using (var db = _db.CreateContext())
        {
            var parent = new Tag { Name = "Работа", SyncId = Guid.NewGuid() };
            db.Tags.Add(parent);
            db.SaveChanges();
            var child = new Tag { Name = "Срочно", ParentTagId = parent.Id, SyncId = Guid.NewGuid() };
            db.Tags.Add(child);
            db.SaveChanges();
            db.TagSynonyms.Add(new TagSynonym { TagId = parent.Id, Value = "office" });
            db.Notes.Add(new Note { Text = "С тегом" });
            db.SaveChanges();
            db.NoteTags.Add(new NoteTag { NoteId = db.Notes.Single().Id, TagId = child.Id, Origin = TagOrigin.Manual });
            db.SaveChanges();
        }

        var archivePath = Path.Combine(_dir, "tags.qnarchive.zip");
        var storage = new AttachmentStorageService(Path.Combine(_dir, "att-tags"));
        var service = new NoteArchiveService();
        using (var db = _db.CreateContext())
        {
            Assert.True(service.ExportArchive(db, archivePath, storage).Success);
        }

        using var empty = SqliteConnectionHolder.Create();
        using (var db2 = empty.CreateContext())
        {
            var preview = service.PreviewArchive(db2, archivePath);
            var imported = service.ImportArchive(db2, preview, new AttachmentStorageService(Path.Combine(_dir, "att-tags-2")), new LocalMutationCoordinator());
            Assert.True(imported.Success);
            var child = db2.Tags.Single(t => t.Name == "Срочно");
            var parent = db2.Tags.Single(t => t.Name == "Работа");
            Assert.Equal(parent.Id, child.ParentTagId);
            Assert.Contains(db2.TagSynonyms, s => s.TagId == parent.Id && s.Value == "office");
        }
    }

    [Fact]
    public void Archive_RejectsPathTraversal()
    {
        Assert.False(NoteArchiveService.IsSafeArchivePath("../secret.bin"));
        Assert.False(NoteArchiveService.IsSafeArchivePath("/files/x.bin"));
        Assert.True(NoteArchiveService.IsSafeArchivePath("files/abc/photo.png"));
    }

    [Fact]
    public void Search_SourceAndUrl_MatchNoteContext()
    {
        var tags = Array.Empty<Tag>();
        var source = SearchQueryParser.Parse("source:chrome", tags);
        Assert.IsType<SourceCondition>(source.Condition);
        Assert.True(source.Condition!.Matches(new Note { SourceProcessName = "chrome" }, DateTime.Now, new Dictionary<int, HashSet<int>>()));
        Assert.False(source.Condition.Matches(new Note { SourceProcessName = "notepad" }, DateTime.Now, new Dictionary<int, HashSet<int>>()));

        var url = SearchQueryParser.Parse("url:example.com", tags);
        Assert.IsType<UrlCondition>(url.Condition);
        Assert.True(url.Condition!.Matches(new Note { SourceUrl = "https://example.com/a" }, DateTime.Now, new Dictionary<int, HashSet<int>>()));
    }

    [Fact]
    public void CardTitle_UsesFirstMeaningfulLine()
    {
        var note = new Note { Id = 7, Text = "# Заголовок\nтело" };
        var card = new NoteCardViewModel(note);
        Assert.Equal("Заголовок", card.DisplayTitle);
        Assert.Equal("#7", card.IdText);
    }

    [Fact]
    public void SettingsXaml_DescribesAesGcm_AndOfficialS3Endpoint()
    {
        var root = FindSolutionRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "QuickNotes.App", "Views", "SettingsWindow.xaml"));
        Assert.Contains("x:Static h:UserTaskCopy.CloudRequirements", xaml, StringComparison.Ordinal);
        Assert.Contains("AES-256-GCM", UserTaskCopy.CloudRequirements, StringComparison.Ordinal);
        Assert.Contains("PBKDF2-HMAC-SHA256", UserTaskCopy.CloudRequirements, StringComparison.Ordinal);
        Assert.DoesNotContain("XChaCha20-Poly1305", xaml, StringComparison.Ordinal);
        Assert.Contains("https://s3.yandexcloud.net", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("по умолчанию https://storage.yandexcloud.net", xaml, StringComparison.Ordinal);
        Assert.Contains("Основные", xaml, StringComparison.Ordinal);
        Assert.Contains("Облако", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Static h:UserTaskCopy.CloudDisconnectButton", xaml, StringComparison.Ordinal);
        Assert.Contains("не чаще одного раза в 15 минут", xaml, StringComparison.Ordinal);
        Assert.Contains("MinWidth=\"200\"", xaml, StringComparison.Ordinal);
        Assert.Contains("WrapPanel", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void MainWindowXaml_KeepsSearchAndNewNote_MovesRareCommandsToMoreMenu()
    {
        var root = FindSolutionRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "QuickNotes.App", "Views", "MainWindow.xaml"));
        Assert.Contains("x:Name=\"SearchBox\"", xaml);
        Assert.Contains("Новая заметка", xaml);
        Assert.Contains("MoreMenuButton", xaml);
        Assert.Contains("Ещё ▾", xaml);
        Assert.Contains("<WrapPanel", xaml);
        Assert.DoesNotContain("Content=\"Пересканировать\"", xaml);
        Assert.DoesNotContain("Content=\"Импорт / экспорт…\"", xaml);
        Assert.DoesNotContain("Content=\"Синхронизировать\"", xaml);
    }

    [Fact]
    public void NewSettingsFile_StartsOnboarding_ExistingDefaultsSkipWizard()
    {
        var missing = new AppSettings();
        Assert.True(missing.HasCompletedOnboarding);

        var path = Path.Combine(_dir, "settings.json");
        var service = new SettingsService(path, _ => { });
        Assert.False(service.CurrentSettings.HasCompletedOnboarding);
    }

    [Fact]
    public void ImportExportWindow_MentionsTextFormatsAreNotFullArchive()
    {
        var root = FindSolutionRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "QuickNotes.App", "Views", "ImportExportWindow.xaml"));
        Assert.Contains("Это не полный архив", xaml);
        Assert.Contains("ExportArchiveCommand", xaml);
        Assert.Contains("Полный архив с вложениями", xaml);
        Assert.Contains("Binding TotalNotesToImport, Mode=OneWay", xaml);
    }

    [Fact]
    public void SchemaV9_RewritesLegacyWikiLinks_PreservesUnknownIds()
    {
        using var db = _db.CreateContext();
        var target = new Note { Text = "Цель" };
        db.Notes.Add(target);
        db.SaveChanges();
        var source = new Note { Text = $"см. [[{target.Id}]] и неизвестную [[999]]" };
        db.Notes.Add(source);
        db.SaveChanges();
        Guid targetSync = target.SyncId;
        int sourceId = source.Id;

        db.Database.ExecuteSqlRaw("PRAGMA user_version = 8;");
        DbInitializer.Initialize(db);

        using var verify = _db.CreateContext();
        using var conn = verify.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
        {
            conn.Open();
        }

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "PRAGMA user_version;";
            Assert.Equal(DbInitializer.CurrentSchemaVersion, Convert.ToInt64(cmd.ExecuteScalar()));
        }

        var rewritten = verify.Notes.AsNoTracking().First(n => n.Id == sourceId).Text;
        Assert.Contains($"[[qn:{targetSync:D}]]", rewritten);
        Assert.Contains("[[999]]", rewritten);
        Assert.Equal("Цель", verify.Notes.AsNoTracking().First(n => n.Id == target.Id).Text);
    }

    [Fact]
    public void ClipboardCaptureService_DocumentsUserFacingFailureReasons()
    {
        var root = FindSolutionRoot();
        var source = File.ReadAllText(Path.Combine(root, "QuickNotes.App", "Services", "ClipboardCaptureService.cs"));
        Assert.Contains("Буфер обмена занят", source);
        Assert.Contains("нет выделенного текста", source);
        Assert.Contains("повышенными правами", source);
        Assert.Contains("Копирование не успело завершиться", source);
    }

    [Fact]
    public void OverlayWindow_ClosesNativelyOnEscapeAndForceClose()
    {
        var root = FindSolutionRoot();
        var overlay = File.ReadAllText(Path.Combine(root, "QuickNotes.App", "Views", "ScreenCropOverlayWindow.xaml.cs"));
        Assert.Contains("TryHardHide", overlay);
        Assert.Contains("VkEscape", overlay);
        Assert.Contains("ShowWindow", overlay);
        Assert.Contains("RequestCancel", overlay);
    }

    [Fact]
    public void Installer_PreservesUserData_AndRemovesAutostartOnUninstall()
    {
        var root = FindSolutionRoot();
        var iss = File.ReadAllText(Path.Combine(root, "installer", "QuickNotes.iss"));
        Assert.Contains("{localappdata}\\QuickNotes", iss);
        Assert.DoesNotContain("DestDir: \"{localappdata}\\QuickNotes\"", iss);
        Assert.Contains("RegDeleteValue", iss);
        Assert.Contains("Software\\Microsoft\\Windows\\CurrentVersion\\Run", iss);
    }

    private static string FindSolutionRoot()
    {
        var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "QuickNotes.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }
        return @"D:\work\QuickNotes";
    }

    private sealed class SqliteConnectionHolder : IDisposable
    {
        private readonly Microsoft.Data.Sqlite.SqliteConnection _connection;
        private readonly DbContextOptions<QuickNotesDbContext> _options;

        private SqliteConnectionHolder(Microsoft.Data.Sqlite.SqliteConnection connection, DbContextOptions<QuickNotesDbContext> options)
        {
            _connection = connection;
            _options = options;
        }

        public static SqliteConnectionHolder Create()
        {
            var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
            connection.Open();
            var options = new DbContextOptionsBuilder<QuickNotesDbContext>().UseSqlite(connection).Options;
            using (var db = new QuickNotesDbContext(options))
            {
                DbInitializer.Initialize(db);
            }
            return new SqliteConnectionHolder(connection, options);
        }

        public QuickNotesDbContext CreateContext() => new(_options);

        public void Dispose() => _connection.Dispose();
    }
}
