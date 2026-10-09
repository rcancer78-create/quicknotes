using System.Collections.Generic;
using System.Threading;
using QuickNotes.App.Data;
using QuickNotes.App.Models;

namespace QuickNotes.App.Services;

public interface INoteExportService
{
    ExportResult ExportToJson(QuickNotesDbContext db, string targetFilePath, CancellationToken cancellationToken = default);
    ExportResult ExportToCsv(QuickNotesDbContext db, string targetFilePath, CancellationToken cancellationToken = default);
    ExportResult ExportToMarkdown(
        QuickNotesDbContext db,
        string targetDirectoryPath,
        IAttachmentStorageService? attachments = null,
        CancellationToken cancellationToken = default);

    string BuildJson(IEnumerable<Note> notes, IEnumerable<Tag> tags);
    string BuildCsv(IEnumerable<Note> notes);
    string GenerateMarkdownContent(Note note);
    string BuildSafeFileName(int noteId, string? text, int collisionIndex = 0);
}
