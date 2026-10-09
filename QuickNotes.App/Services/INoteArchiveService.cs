using System.Collections.Generic;
using System.Threading;
using QuickNotes.App.Data;
using QuickNotes.App.Models;
using QuickNotes.App.Services.Sync;

namespace QuickNotes.App.Services;

public interface INoteArchiveService
{
    ExportResult ExportArchive(QuickNotesDbContext db, string targetFilePath, IAttachmentStorageService attachments, CancellationToken cancellationToken = default);

    ImportPreviewResult PreviewArchive(QuickNotesDbContext db, string archivePath);

    ImportExecutionResult ImportArchive(
        QuickNotesDbContext db,
        ImportPreviewResult preview,
        IAttachmentStorageService attachments,
        ILocalMutationCoordinator mutationCoordinator,
        INoteHistoryService? historyService = null,
        CancellationToken cancellationToken = default);
}

public sealed class ArchiveManifest
{
    public int SchemaVersion { get; set; } = 1;
    public string Format { get; set; } = "quicknotes-archive";
    public string AppVersion { get; set; } = "1.0";
    public System.DateTime ExportedAtUtc { get; set; }
    public List<ArchiveNoteEntry> Notes { get; set; } = new();
    public List<ExportTagDto> Tags { get; set; } = new();
}

public sealed class ArchiveNoteEntry
{
    public System.Guid SyncId { get; set; }
    public int OriginalLocalId { get; set; }
    public string TextPath { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
    public List<ArchiveAttachmentEntry> Attachments { get; set; } = new();
    public ExportNoteDto Note { get; set; } = new();
}

public sealed class ArchiveAttachmentEntry
{
    public string OriginalFileName { get; set; } = string.Empty;
    public string StoredFileName { get; set; } = string.Empty;
    public string ArchivePath { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
    public long Size { get; set; }
    public string ContentType { get; set; } = string.Empty;
}
