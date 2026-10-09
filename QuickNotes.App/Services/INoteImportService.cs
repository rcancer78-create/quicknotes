using System;
using System.Collections.Generic;
using System.Threading;
using QuickNotes.App.Data;
using QuickNotes.App.Models;
using QuickNotes.App.Services.Sync;

namespace QuickNotes.App.Services;

public interface INoteImportService
{
    ImportPreviewResult BuildPreviewFromFiles(QuickNotesDbContext db, IEnumerable<string> filePaths, CancellationToken cancellationToken = default);
    ImportPreviewResult BuildPreviewFromDirectory(QuickNotesDbContext db, string directoryPath, bool recursive = true, CancellationToken cancellationToken = default);
    ImportExecutionResult ExecuteImport(
        QuickNotesDbContext db,
        ImportPreviewResult preview,
        ILocalMutationCoordinator mutationCoordinator,
        INoteHistoryService? historyService = null,
        TagDetectionService? tagDetectionService = null,
        IAttachmentStorageService? attachmentStorage = null,
        CancellationToken cancellationToken = default);

    bool TryParseMarkdownFile(string filePath, out ImportItemPreview? item, out string? errorMessage);
    bool TryParseMarkdownContent(string content, string fileName, DateTime creationTime, DateTime lastWriteTime, out ImportItemPreview? item, out string? errorMessage);

    bool TryParsePlainTextFile(string filePath, out ImportItemPreview? item, out string? errorMessage);
    bool TryParsePlainTextContent(string content, string fileName, DateTime creationTime, DateTime lastWriteTime, out ImportItemPreview? item, out string? errorMessage);

    bool TryParseHtmlFile(string filePath, string importRoot, out ImportItemPreview? item, out string? errorMessage);
    bool TryParseHtmlContent(string html, string fileName, DateTime creationTime, DateTime lastWriteTime, out ImportItemPreview? item, out HtmlConversionResult conversion, out string? errorMessage);

    bool TryParseJsonFile(string filePath, out List<ImportItemPreview>? items, out List<ExportTagDto>? packageTags, out string? errorMessage);
    bool TryParseJsonContent(string jsonContent, string fileName, out List<ImportItemPreview>? items, out List<ExportTagDto>? packageTags, out string? errorMessage);

    string NormalizeContent(string? text);
    void RecalculatePreviewTotals(QuickNotesDbContext db, ImportPreviewResult preview);
}
