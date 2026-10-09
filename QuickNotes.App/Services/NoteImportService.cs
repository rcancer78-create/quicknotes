using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using Microsoft.EntityFrameworkCore;
using QuickNotes.App.Data;
using QuickNotes.App.Models;

namespace QuickNotes.App.Services;

public partial class NoteImportService : INoteImportService
{
    internal static Action<ImportTransactionPhase>? TestInjectFailure { get; set; }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public ImportPreviewResult BuildPreviewFromDirectory(QuickNotesDbContext db, string directoryPath, bool recursive = true, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(directoryPath) || !Directory.Exists(directoryPath))
        {
            return new ImportPreviewResult
            {
                TotalFilesDiscovered = 0,
                Diagnostics = new List<ImportDiagnosticItem>
                {
                    new() { FileName = directoryPath, Reason = "Папка не существует или недоступна.", IsBlocking = true }
                }
            };
        }

        if (!ImportPathSafety.TryGetSafeFullPath(directoryPath, out var root, out var pathError))
        {
            return BlockingPreview(directoryPath, pathError ?? "Недопустимый путь.");
        }

        if (ImportPathSafety.TreeContainsReparse(root, out var reparseError))
        {
            return BlockingPreview(root, reparseError ?? "Reparse-точка.");
        }

        var searchOption = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        string[] discovered;
        try
        {
            discovered = Directory.GetFiles(root, "*", searchOption);
        }
        catch (Exception ex)
        {
            ErrorLogService.Write("NoteImportService.BuildPreviewFromDirectory", $"{ex.GetType().Name}: {ex.Message}");
            return BlockingPreview(root, $"Ошибка при сканировании каталога: {ex.Message}");
        }

        var files = discovered
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var preview = BuildPreviewFromFiles(db, files, root, cancellationToken);
        preview.ImportRootPath = root;
        preview.FolderMappings = ImportFolderMapper.BuildMapping(root);
        ImportFolderMapper.ApplyMappingToItems(preview.Items, preview.FolderMappings, root);
        foreach (var item in preview.Items.Where(i => i.HasAmbiguousFolderMapping))
        {
            preview.Diagnostics.Add(new ImportDiagnosticItem
            {
                FileName = item.SourceFileName,
                Reason = "Неоднозначная вложенность папок не сглаживается молча.",
                IsLoss = true
            });
        }
        RecalculatePreviewTotals(db, preview);
        preview.PlanHash = ImportSourceIdentity.ComputePlanHash(preview.Items, preview.Diagnostics);
        return preview;
    }

    public ImportPreviewResult BuildPreviewFromFiles(QuickNotesDbContext db, IEnumerable<string> filePaths, CancellationToken cancellationToken = default)
        => BuildPreviewFromFiles(db, filePaths, importRoot: null, cancellationToken);

    public ImportPreviewResult BuildPreviewFromFiles(QuickNotesDbContext db, IEnumerable<string> filePaths, string? importRoot)
        => BuildPreviewFromFiles(db, filePaths, importRoot, CancellationToken.None);

    public ImportPreviewResult BuildPreviewFromFiles(QuickNotesDbContext db, IEnumerable<string> filePaths, string? importRoot, CancellationToken cancellationToken)
    {
        var discoveredItems = new List<ImportItemPreview>();
        var diagnostics = new List<ImportDiagnosticItem>();
        var packageTags = new List<ExportTagDto>();
        int totalFiles = 0;
        long aggregate = 0;
        string root = importRoot ?? string.Empty;

        foreach (var rawPath in filePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            totalFiles++;
            if (totalFiles > ImportLimits.MaxFileCount)
            {
                diagnostics.Add(new ImportDiagnosticItem
                {
                    FileName = rawPath,
                    Reason = $"Превышен лимит числа файлов ({ImportLimits.MaxFileCount}).",
                    IsBlocking = true
                });
                break;
            }

            if (!ImportPathSafety.TryGetSafeFullPath(rawPath, out var path, out var pathError))
            {
                diagnostics.Add(new ImportDiagnosticItem
                {
                    FileName = Path.GetFileName(rawPath),
                    Reason = pathError ?? "Недопустимый путь.",
                    IsBlocking = true
                });
                continue;
            }

            if (!string.IsNullOrEmpty(root) && !ImportPathSafety.TryEnsureInsideRoot(path, root, out var insideError))
            {
                diagnostics.Add(new ImportDiagnosticItem
                {
                    FileName = Path.GetFileName(path),
                    Reason = insideError ?? "Путь вне корня импорта.",
                    IsBlocking = true
                });
                continue;
            }

            if (!File.Exists(path))
            {
                diagnostics.Add(new ImportDiagnosticItem
                {
                    FileName = Path.GetFileName(path),
                    Reason = "Файл не существует."
                });
                continue;
            }

            var info = new FileInfo(path);
            var ext = Path.GetExtension(path).ToLowerInvariant();
            bool isSupportedNote = ext is ".md" or ".txt" or ".html" or ".htm" or ".json";
            if (!isSupportedNote)
            {
                diagnostics.Add(CreateUnsupportedFileDiagnostic(info.Name, ext));
                continue;
            }

            if (info.Length > ImportLimits.MaxIndividualFileBytes)
            {
                diagnostics.Add(new ImportDiagnosticItem
                {
                    FileName = info.Name,
                    Reason = "Файл превышает лимит размера.",
                    IsBlocking = true
                });
                continue;
            }

            aggregate += info.Length;
            if (aggregate > ImportLimits.MaxAggregateBytes)
            {
                diagnostics.Add(new ImportDiagnosticItem
                {
                    FileName = info.Name,
                    Reason = "Превышен совокупный лимит размера импорта.",
                    IsBlocking = true
                });
                break;
            }

            if (string.IsNullOrEmpty(root))
            {
                root = info.DirectoryName ?? Path.GetDirectoryName(path) ?? path;
            }

            if (ext == ".md")
            {
                if (TryParseMarkdownFile(path, out var item, out var error))
                {
                    StampAndCollect(item!, path, info, root);
                    discoveredItems.Add(item!);
                }
                else
                {
                    diagnostics.Add(new ImportDiagnosticItem { FileName = info.Name, Reason = error ?? "Невалидный Markdown-файл" });
                }
            }
            else if (ext == ".txt")
            {
                if (TryParsePlainTextFile(path, out var item, out var error))
                {
                    StampAndCollect(item!, path, info, root);
                    discoveredItems.Add(item!);
                }
                else
                {
                    diagnostics.Add(new ImportDiagnosticItem { FileName = info.Name, Reason = error ?? "Не удалось прочитать текстовый файл" });
                }
            }
            else if (ext is ".html" or ".htm")
            {
                if (TryParseHtmlFile(path, root, out var item, out var error))
                {
                    StampAndCollect(item!, path, info, root);
                    if (item!.HasLosses)
                    {
                        diagnostics.Add(new ImportDiagnosticItem
                        {
                            FileName = info.Name,
                            Reason = item.LostElementsDisplay,
                            IsLoss = true
                        });
                    }
                    discoveredItems.Add(item!);
                }
                else
                {
                    diagnostics.Add(new ImportDiagnosticItem
                    {
                        FileName = info.Name,
                        Reason = error ?? "Невалидный HTML-файл",
                        IsBlocking = error != null && error.Contains("лимит", StringComparison.OrdinalIgnoreCase)
                    });
                }
            }
            else if (ext == ".json")
            {
                if (TryParseJsonFile(path, out var items, out var tags, out var error))
                {
                    if (items != null)
                    {
                        foreach (var item in items)
                        {
                            StampAndCollect(item, path, info, root, item.OriginalId);
                            discoveredItems.Add(item);
                        }
                    }

                    if (tags != null)
                    {
                        packageTags.AddRange(tags);
                    }
                }
                else
                {
                    diagnostics.Add(new ImportDiagnosticItem { FileName = info.Name, Reason = error ?? "Невалидный JSON-файл" });
                }
            }
            else
            {
                diagnostics.Add(CreateUnsupportedFileDiagnostic(info.Name, ext));
            }
        }

        var preview = BuildPreview(db, discoveredItems, diagnostics, packageTags, totalFiles);
        preview.ImportRootPath = importRoot ?? root;
        preview.PlanHash = ImportSourceIdentity.ComputePlanHash(preview.Items, preview.Diagnostics);
        return preview;
    }

    private static ImportPreviewResult BlockingPreview(string fileName, string reason)
    {
        return new ImportPreviewResult
        {
            TotalFilesDiscovered = 0,
            Diagnostics = new List<ImportDiagnosticItem>
            {
                new() { FileName = fileName, Reason = reason, IsBlocking = true }
            }
        };
    }

    private static ImportDiagnosticItem CreateUnsupportedFileDiagnostic(string fileName, string ext)
    {
        bool isWordOrOneNote = ext is ".doc" or ".docx" or ".one" or ".onetoc2";
        string reason = isWordOrOneNote
            ? "Формат Word/OneNote напрямую не поддерживается. Экспортируйте в фильтрованный HTML или Markdown (см. docs/import-from-word-and-html.md)."
            : $"Формат '{ext}' не поддерживается. Импортируйте фильтрованный HTML или Markdown (см. docs/import-from-word-and-html.md).";
        return new ImportDiagnosticItem
        {
            FileName = fileName,
            Reason = reason,
            IsLoss = true
        };
    }

    private static int CountOmittedFiles(IEnumerable<ImportDiagnosticItem> diagnostics, IReadOnlyList<ImportItemPreview> items)
    {
        return diagnostics.Count(d =>
            d.IsBlocking
            || !items.Any(i => string.Equals(i.SourceFileName, d.FileName, StringComparison.OrdinalIgnoreCase)));
    }

    private static void MarkExactSource(ImportItemPreview item, Note? existing)
    {
        item.DuplicateKind = ImportDuplicateKind.ExactSource;
        item.IsConflict = true;
        item.MatchingNoteId = existing?.Id;
        item.CanReplace = existing != null && !existing.IsProtected;
        item.DuplicateAction = ImportDuplicateAction.Skip;
        item.Status = "Точный повтор источника (пропуск)";
        item.ConflictReason = "Этот файл уже импортировался (совпал отпечаток источника). По умолчанию пропуск; замена только если заметка не защищена.";
    }

    private static void StampAndCollect(ImportItemPreview item, string path, FileInfo info, string root, int? extraId = null)
    {
        item.SourcePath = path;
        item.SourceFileName = info.Name;
        item.SourceLength = info.Length;
        item.SourceLastWriteUtcTicks = info.LastWriteTimeUtc.Ticks;
        try
        {
            var rel = Path.GetRelativePath(root, path);
            item.SourceRelativePath = ImportSourceIdentity.NormalizeRelative(rel);
        }
        catch
        {
            item.SourceRelativePath = info.Name;
        }

        byte[] bytes = File.ReadAllBytes(path);
        item.ContentSha256 = ImportSourceIdentity.ComputeContentSha256(bytes);
        string identityKey = extraId.HasValue ? item.SourceRelativePath + "#" + extraId.Value : item.SourceRelativePath;
        item.SourceFingerprint = ImportSourceIdentity.ComputeFingerprint(identityKey, item.ContentSha256);
        ImportSourceIdentity.CollectMarkdownAttachments(item, root);
    }

    public ImportPreviewResult BuildPreview(
        QuickNotesDbContext db,
        IEnumerable<ImportItemPreview> discoveredItems,
        IEnumerable<ImportDiagnosticItem> diagnostics,
        List<ExportTagDto>? packageTags = null,
        int totalFilesCount = 0)
    {
        var existingNotes = db.Notes.Where(n => n.DeletedAt == null).ToList();
        var existingIds = existingNotes.Select(n => n.Id).ToHashSet();
        var fingerprints = existingNotes
            .Where(n => !string.IsNullOrWhiteSpace(n.ImportSourceFingerprint))
            .ToDictionary(n => n.ImportSourceFingerprint!, n => n, StringComparer.Ordinal);
        var titles = existingNotes
            .GroupBy(n => Helpers.NoteTitleHelper.GetDisplayTitle(n.Title, n.Text), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var normalizedDbTexts = existingNotes
            .Select(n => NormalizeContent(n.Text))
            .Where(t => !string.IsNullOrEmpty(t))
            .ToHashSet(StringComparer.Ordinal);

        var existingTagNames = db.Tags.Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var batchSeenIds = new HashSet<int>();
        var batchSeenTexts = new HashSet<string>(StringComparer.Ordinal);
        var batchFingerprints = new HashSet<string>(StringComparer.Ordinal);
        var discoveredNewTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var itemsList = discoveredItems.ToList();

        foreach (var item in itemsList)
        {
            item.Title ??= string.Empty;
            ClassifyDuplicate(item, existingNotes, existingIds, fingerprints, titles, normalizedDbTexts, batchSeenIds, batchSeenTexts, batchFingerprints);
            CountNewTags(item, existingTagNames, discoveredNewTags);
        }

        if (packageTags != null)
        {
            foreach (var pt in packageTags)
            {
                if (string.IsNullOrWhiteSpace(pt.Name))
                {
                    continue;
                }

                var cleanName = pt.Name.Trim();
                if (!existingTagNames.Contains(cleanName))
                {
                    discoveredNewTags.Add(cleanName);
                }
            }
        }

        var diagList = diagnostics.ToList();
        int totalNotesToImport = itemsList.Count(ImportSourceIdentity.ShouldCommit);
        int totalConflicts = itemsList.Count(i => i.IsConflict);

        return new ImportPreviewResult
        {
            TotalFilesDiscovered = totalFilesCount > 0 ? totalFilesCount : itemsList.Count + diagList.Count,
            TotalNotesToImport = totalNotesToImport,
            TotalConflicts = totalConflicts,
            TotalSkippedOrErroneous = CountOmittedFiles(diagList, itemsList),
            NewTagsCount = discoveredNewTags.Count,
            ConflictPolicyDescription = "Политика при конфликтах: пропускать существующие заметки по умолчанию. Явно выберите «Пропустить», «Импортировать отдельно» или «Заменить» (замена только для того же источника). Заметки с одинаковым заголовком и разным текстом не объединяются молча.",
            Items = itemsList,
            Diagnostics = diagList,
            PackageTags = packageTags ?? new List<ExportTagDto>()
        };
    }

    public void RecalculatePreviewTotals(QuickNotesDbContext db, ImportPreviewResult preview)
    {
        var existingTagNames = db.Tags.Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var discoveredNewTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in preview.Items)
        {
            CountNewTags(item, existingTagNames, discoveredNewTags);
        }

        preview.TotalNotesToImport = preview.Items.Count(ImportSourceIdentity.ShouldCommit);
        preview.TotalConflicts = preview.Items.Count(i => i.IsConflict);
        preview.TotalSkippedOrErroneous = CountOmittedFiles(preview.Diagnostics, preview.Items);
        preview.NewTagsCount = discoveredNewTags.Count;
        preview.PlanHash = ImportSourceIdentity.ComputePlanHash(preview.Items, preview.Diagnostics);
    }

    private void ClassifyDuplicate(
        ImportItemPreview item,
        List<Note> existingNotes,
        HashSet<int> existingIds,
        Dictionary<string, Note> fingerprints,
        Dictionary<string, Note> titles,
        HashSet<string> normalizedDbTexts,
        HashSet<int> batchSeenIds,
        HashSet<string> batchSeenTexts,
        HashSet<string> batchFingerprints)
    {
        if (item.HasBlockingIssue)
        {
            item.IsConflict = true;
            item.Status = "Блокирующая ошибка";
            item.DuplicateAction = ImportDuplicateAction.Skip;
            return;
        }

        if (!string.IsNullOrWhiteSpace(item.SourceFingerprint)
            && (fingerprints.TryGetValue(item.SourceFingerprint, out var byFp) || !batchFingerprints.Add(item.SourceFingerprint)))
        {
            MarkExactSource(item, byFp);
            return;
        }

        if (item.OriginalId == null && !string.IsNullOrWhiteSpace(item.SourceRelativePath))
        {
            var byPath = existingNotes.FirstOrDefault(n =>
                !string.IsNullOrWhiteSpace(n.ImportSourceFingerprint)
                && string.Equals(n.ImportSourceRelativePath, item.SourceRelativePath, StringComparison.OrdinalIgnoreCase));
            if (byPath != null)
            {
                MarkExactSource(item, byPath);
                item.ConflictReason = "Тот же относительный путь источника уже импортирован (содержимое могло измениться). По умолчанию пропуск; замена только если заметка не защищена.";
                return;
            }
        }

        if (item.OriginalId.HasValue && (existingIds.Contains(item.OriginalId.Value) || !batchSeenIds.Add(item.OriginalId.Value)))
        {
            item.DuplicateKind = ImportDuplicateKind.OriginalId;
            item.IsConflict = true;
            item.MatchingNoteId = item.OriginalId;
            item.CanReplace = false;
            item.DuplicateAction = ImportDuplicateAction.Skip;
            item.Status = "Конфликт (пропуск)";
            item.ConflictReason = $"Заметка с исходным ID #{item.OriginalId.Value} уже присутствует в базе данных.";
            return;
        }

        var norm = NormalizeContent(item.Text);
        if (!string.IsNullOrEmpty(norm) && (normalizedDbTexts.Contains(norm) || !batchSeenTexts.Add(norm)))
        {
            item.DuplicateKind = ImportDuplicateKind.IdenticalContent;
            item.IsConflict = true;
            item.CanReplace = false;
            item.DuplicateAction = ImportDuplicateAction.Skip;
            item.Status = "Конфликт (пропуск)";
            item.ConflictReason = "Заметка с идентичным содержимым уже существует.";
            return;
        }

        string displayTitle = Helpers.NoteTitleHelper.GetDisplayTitle(item.Title, item.Text);
        if (!string.IsNullOrWhiteSpace(displayTitle) && titles.TryGetValue(displayTitle, out var titled)
            && !string.Equals(NormalizeContent(titled.Text), norm, StringComparison.Ordinal))
        {
            item.DuplicateKind = ImportDuplicateKind.SameTitle;
            item.IsConflict = true;
            item.MatchingNoteId = titled.Id;
            item.CanReplace = false;
            item.DuplicateAction = ImportDuplicateAction.Skip;
            item.Status = "Совпадение заголовка (пропуск)";
            item.ConflictReason = "Другая заметка с таким заголовком уже есть. По умолчанию пропуск; чтобы сохранить обе, выберите «Импортировать отдельно».";
            return;
        }

        item.DuplicateKind = ImportDuplicateKind.None;
        item.IsConflict = false;
        item.DuplicateAction = ImportDuplicateAction.ImportSeparate;
        item.Status = "Готова к импорту";
        item.ConflictReason = string.Empty;
    }

    private static void CountNewTags(ImportItemPreview item, HashSet<string> existingTagNames, HashSet<string> discoveredNewTags)
    {
        if (!ImportSourceIdentity.ShouldCommit(item))
        {
            return;
        }

        foreach (var tag in item.Tags)
        {
            if (string.IsNullOrWhiteSpace(tag.TagName))
            {
                continue;
            }

            var cleanName = tag.TagName.Trim();
            if (!existingTagNames.Contains(cleanName))
            {
                discoveredNewTags.Add(cleanName);
            }
        }
    }

    public bool TryParseMarkdownFile(string filePath, out ImportItemPreview? item, out string? errorMessage)
    {
        item = null;
        errorMessage = null;

        if (!ReadFileSafely(filePath, out var content, out var readError))
        {
            errorMessage = readError;
            return false;
        }

        var fileInfo = new FileInfo(filePath);
        return TryParseMarkdownContent(content, Path.GetFileName(filePath), fileInfo.CreationTime, fileInfo.LastWriteTime, out item, out errorMessage);
    }

    public bool TryParseMarkdownContent(
        string content,
        string fileName,
        DateTime creationTime,
        DateTime lastWriteTime,
        out ImportItemPreview? item,
        out string? errorMessage)
    {
        item = null;
        errorMessage = null;

        content = (content ?? string.Empty).TrimStart('\uFEFF');

        if (string.IsNullOrWhiteSpace(content))
        {
            item = new ImportItemPreview
            {
                SourceFileName = fileName,
                Text = string.Empty,
                CreatedAt = creationTime,
                UpdatedAt = lastWriteTime,
                Title = fileName
            };
            return true;
        }

        var lines = content.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        if (lines.Length > 0 && lines[0].Trim() == "---")
        {
            // YAML front matter detected
            int closingIndex = -1;
            for (int i = 1; i < lines.Length; i++)
            {
                if (lines[i].Trim() == "---")
                {
                    closingIndex = i;
                    break;
                }
            }

            if (closingIndex == -1)
            {
                errorMessage = "Невалидный front matter: отсутствует закрывающий разделитель '---'.";
                return false;
            }

            var fmLines = lines.Skip(1).Take(closingIndex - 1).ToList();
            var bodyLines = lines.Skip(closingIndex + 1).ToList();
            var bodyText = string.Join("\n", bodyLines).TrimStart('\r', '\n');

            if (!TryParseFrontMatter(fmLines, out var parsedDto, out var fmError))
            {
                errorMessage = $"Невалидный front matter: {fmError}";
                return false;
            }

            string itemTitle = !string.IsNullOrWhiteSpace(parsedDto.Title)
                ? parsedDto.Title
                : ExtractTitle(bodyText, Path.GetFileNameWithoutExtension(fileName));

            item = new ImportItemPreview
            {
                SourceFileName = fileName,
                OriginalId = parsedDto.Id > 0 ? parsedDto.Id : null,
                Text = bodyText,
                CreatedAt = parsedDto.CreatedAt ?? creationTime,
                UpdatedAt = parsedDto.UpdatedAt ?? lastWriteTime,
                IsPinned = parsedDto.IsPinned,
                IsFavorite = parsedDto.IsFavorite,
                IsInbox = parsedDto.IsInbox,
                SourceProcessName = parsedDto.SourceProcessName,
                SourceWindowTitle = parsedDto.SourceWindowTitle,
                SourceUrl = parsedDto.SourceUrl,
                CapturedAt = parsedDto.CapturedAt,
                Tags = parsedDto.Tags,
                Title = itemTitle
            };
            return true;
        }

        // Plain Markdown without front matter
        item = new ImportItemPreview
        {
            SourceFileName = fileName,
            Text = content,
            CreatedAt = creationTime,
            UpdatedAt = lastWriteTime,
            Title = ExtractTitle(content, Path.GetFileNameWithoutExtension(fileName))
        };
        return true;
    }

    public bool TryParsePlainTextFile(string filePath, out ImportItemPreview? item, out string? errorMessage)
    {
        item = null;
        errorMessage = null;

        if (!ReadFileSafely(filePath, out var content, out var readError))
        {
            errorMessage = readError;
            return false;
        }

        var fileInfo = new FileInfo(filePath);
        return TryParsePlainTextContent(content, Path.GetFileName(filePath), fileInfo.CreationTime, fileInfo.LastWriteTime, out item, out errorMessage);
    }

    public bool TryParsePlainTextContent(
        string content,
        string fileName,
        DateTime creationTime,
        DateTime lastWriteTime,
        out ImportItemPreview? item,
        out string? errorMessage)
    {
        errorMessage = null;
        content = (content ?? string.Empty).TrimStart('\uFEFF');
        item = new ImportItemPreview
        {
            SourceFileName = fileName,
            Text = content,
            CreatedAt = creationTime,
            UpdatedAt = lastWriteTime,
            Title = ExtractTitle(content, Path.GetFileNameWithoutExtension(fileName))
        };
        return true;
    }

    public bool TryParseHtmlFile(string filePath, string importRoot, out ImportItemPreview? item, out string? errorMessage)
    {
        item = null;
        errorMessage = null;
        if (!ReadFileSafely(filePath, out var html, out var readError))
        {
            errorMessage = readError;
            return false;
        }

        var fileInfo = new FileInfo(filePath);
        if (!TryParseHtmlContent(html, Path.GetFileName(filePath), fileInfo.CreationTime, fileInfo.LastWriteTime, out item, out var conversion, out errorMessage))
        {
            return false;
        }

        item!.SourcePath = filePath;
        string root = string.IsNullOrWhiteSpace(importRoot) ? (fileInfo.DirectoryName ?? Path.GetDirectoryName(filePath) ?? filePath) : importRoot;
        ImportSourceIdentity.ApplyHtmlAttachments(item, conversion, root);
        return true;
    }

    public bool TryParseHtmlContent(
        string html,
        string fileName,
        DateTime creationTime,
        DateTime lastWriteTime,
        out ImportItemPreview? item,
        out HtmlConversionResult conversion,
        out string? errorMessage)
    {
        item = null;
        errorMessage = null;
        conversion = HtmlToMarkdownConverter.Convert(html ?? string.Empty);
        if (conversion.HasBlocking)
        {
            errorMessage = conversion.BlockingReasons[0];
            return false;
        }

        item = new ImportItemPreview
        {
            SourceFileName = fileName,
            Text = conversion.Markdown,
            CreatedAt = creationTime,
            UpdatedAt = lastWriteTime,
            Title = ExtractTitle(conversion.Markdown, Path.GetFileNameWithoutExtension(fileName)),
            LostElements = conversion.LostElements.Distinct(StringComparer.Ordinal).ToList()
        };
        return true;
    }

    public bool TryParseJsonFile(
        string filePath,
        out List<ImportItemPreview>? items,
        out List<ExportTagDto>? packageTags,
        out string? errorMessage)
    {
        items = null;
        packageTags = null;
        errorMessage = null;

        if (!ReadFileSafely(filePath, out var json, out var readError))
        {
            errorMessage = readError;
            return false;
        }

        return TryParseJsonContent(json, Path.GetFileName(filePath), out items, out packageTags, out errorMessage);
    }

    public bool TryParseJsonContent(
        string jsonContent,
        string fileName,
        out List<ImportItemPreview>? items,
        out List<ExportTagDto>? packageTags,
        out string? errorMessage)
    {
        items = null;
        packageTags = null;
        errorMessage = null;

        jsonContent = (jsonContent ?? string.Empty).TrimStart('\uFEFF');

        if (string.IsNullOrWhiteSpace(jsonContent))
        {
            errorMessage = "JSON-файл пуст.";
            return false;
        }

        try
        {
            var package = JsonSerializer.Deserialize<QuickNotesExportPackage>(jsonContent, JsonOptions);
            if (package != null && package.Notes != null)
            {
                packageTags = (package.Tags ?? new List<ExportTagDto>())
                    .Where(t => !string.IsNullOrWhiteSpace(t.Name))
                    .Select(t =>
                    {
                        t.Name = t.Name.Trim();
                        return t;
                    })
                    .ToList();

                items = package.Notes.Select(n => new ImportItemPreview
                {
                    SourceFileName = fileName,
                    OriginalId = n.Id > 0 ? n.Id : null,
                    Text = n.Text ?? string.Empty,
                    CreatedAt = n.CreatedAt,
                    UpdatedAt = n.UpdatedAt,
                    IsPinned = n.IsPinned,
                    IsFavorite = n.IsFavorite,
                    IsInbox = n.IsInbox,
                    SourceProcessName = n.SourceProcessName,
                    SourceWindowTitle = n.SourceWindowTitle,
                    SourceUrl = n.SourceUrl,
                    CapturedAt = n.CapturedAt,
                    Tags = (n.Tags ?? new List<ExportNoteTagDto>())
                        .Where(t => !string.IsNullOrWhiteSpace(t.TagName))
                        .Select(t =>
                        {
                            t.TagName = t.TagName.Trim();
                            return t;
                        })
                        .ToList(),
                    Title = n.Title ?? string.Empty
                }).ToList();

                return true;
            }

            // Fallback: direct list of notes
            var directNotes = JsonSerializer.Deserialize<List<ExportNoteDto>>(jsonContent, JsonOptions);
            if (directNotes != null)
            {
                packageTags = new List<ExportTagDto>();
                items = directNotes.Select(n => new ImportItemPreview
                {
                    SourceFileName = fileName,
                    OriginalId = n.Id > 0 ? n.Id : null,
                    Text = n.Text ?? string.Empty,
                    CreatedAt = n.CreatedAt,
                    UpdatedAt = n.UpdatedAt,
                    IsPinned = n.IsPinned,
                    IsFavorite = n.IsFavorite,
                    IsInbox = n.IsInbox,
                    SourceProcessName = n.SourceProcessName,
                    SourceWindowTitle = n.SourceWindowTitle,
                    SourceUrl = n.SourceUrl,
                    CapturedAt = n.CapturedAt,
                    Tags = (n.Tags ?? new List<ExportNoteTagDto>())
                        .Where(t => !string.IsNullOrWhiteSpace(t.TagName))
                        .Select(t =>
                        {
                            t.TagName = t.TagName.Trim();
                            return t;
                        })
                        .ToList(),
                    Title = n.Title ?? string.Empty
                }).ToList();

                return true;
            }

            errorMessage = "Неверная структура JSON (ожидался пакет QuickNotes или список заметок).";
            return false;
        }
        catch (JsonException ex)
        {
            errorMessage = $"Синтаксическая ошибка в JSON: {ex.Message}";
            return false;
        }
    }

    public string NormalizeContent(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var trimmedLines = lines.Select(l => l.TrimEnd());
        return string.Join("\n", trimmedLines).Trim();
    }

    private static bool ReadFileSafely(string filePath, out string content, out string? error)
    {
        content = string.Empty;
        error = null;

        try
        {
            var bytes = File.ReadAllBytes(filePath);

            // Check for binary null bytes
            int checkLength = Math.Min(bytes.Length, 1024);
            for (int i = 0; i < checkLength; i++)
            {
                if (bytes[i] == 0)
                {
                    error = "Файл содержит бинарные данные и не является текстовым.";
                    return false;
                }
            }

            // Try UTF-8 first
            try
            {
                var utf8Strict = new UTF8Encoding(false, throwOnInvalidBytes: true);
                content = utf8Strict.GetString(bytes);
            }
            catch (DecoderFallbackException)
            {
                // Fallback to standard UTF-8 or Default encoding
                content = Encoding.UTF8.GetString(bytes);
            }

            if (content.StartsWith("\uFEFF"))
            {
                content = content[1..];
            }
            return true;
        }
        catch (Exception ex)
        {
            error = $"Не удалось прочитать файл: {ex.Message}";
            return false;
        }
    }

    private static bool TryParseFrontMatter(List<string> lines, out ParsedFrontMatter result, out string? error)
    {
        result = new ParsedFrontMatter();
        error = null;

        bool inTagsBlock = false;

        for (int i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var trimmed = line.Trim();

            if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith('#'))
            {
                continue;
            }

            if (inTagsBlock)
            {
                if (trimmed.StartsWith('-'))
                {
                    var tagVal = trimmed[1..].Trim().Trim('"', '\'').Trim();
                    if (!string.IsNullOrWhiteSpace(tagVal))
                    {
                        result.Tags.Add(new ExportNoteTagDto
                        {
                            TagName = tagVal,
                            Origin = TagOrigin.Manual
                        });
                    }
                    continue;
                }
                else if (char.IsWhiteSpace(line[0]))
                {
                    // Continuation or nested property in tag block, ignore gracefully
                    continue;
                }
                else
                {
                    inTagsBlock = false;
                }
            }

            int colonIndex = trimmed.IndexOf(':');
            if (colonIndex == -1)
            {
                error = $"Ошибочная строка в front matter: отсутствует разделитель ':'.";
                return false;
            }

            var key = trimmed[..colonIndex].Trim().ToLowerInvariant().Replace("-", "_");
            var val = trimmed[(colonIndex + 1)..].Trim();

            // Unquote value if quoted
            if ((val.StartsWith('"') && val.EndsWith('"')) || (val.StartsWith('\'') && val.EndsWith('\'')))
            {
                val = val[1..^1].Trim();
            }

            switch (key)
            {
                case "title":
                    result.Title = val;
                    break;

                case "id":
                    if (int.TryParse(val, out int idVal))
                    {
                        result.Id = idVal;
                    }
                    else
                    {
                        error = $"Некорректный ID в front matter: '{val}'.";
                        return false;
                    }
                    break;

                case "created_at":
                case "createdat":
                case "date":
                    if (DateTime.TryParse(val, out var cDate))
                    {
                        result.CreatedAt = cDate;
                    }
                    break;

                case "updated_at":
                case "updatedat":
                    if (DateTime.TryParse(val, out var uDate))
                    {
                        result.UpdatedAt = uDate;
                    }
                    break;

                case "captured_at":
                case "capturedat":
                    if (DateTime.TryParse(val, out var capDate))
                    {
                        result.CapturedAt = capDate;
                    }
                    break;

                case "is_pinned":
                case "ispinned":
                case "pinned":
                    result.IsPinned = ParseBool(val);
                    break;

                case "is_favorite":
                case "isfavorite":
                case "favorite":
                    result.IsFavorite = ParseBool(val);
                    break;

                case "is_inbox":
                case "isinbox":
                case "inbox":
                    result.IsInbox = ParseBool(val);
                    break;

                case "source_process":
                case "sourceprocess":
                case "source_process_name":
                    result.SourceProcessName = val;
                    break;

                case "source_window":
                case "sourcewindow":
                case "source_window_title":
                    result.SourceWindowTitle = val;
                    break;

                case "source_url":
                case "sourceurl":
                    result.SourceUrl = val;
                    break;

                case "tags":
                    if (string.IsNullOrEmpty(val) || val == "[]")
                    {
                        inTagsBlock = true;
                    }
                    else if (val.StartsWith('[') && val.EndsWith(']'))
                    {
                        var inner = val[1..^1];
                        var parts = inner.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                        foreach (var p in parts)
                        {
                            var cleanTag = p.Trim().Trim('"', '\'').Trim();
                            if (!string.IsNullOrWhiteSpace(cleanTag))
                            {
                                result.Tags.Add(new ExportNoteTagDto
                                {
                                    TagName = cleanTag,
                                    Origin = TagOrigin.Manual
                                });
                            }
                        }
                    }
                    else
                    {
                        var parts = val.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                        foreach (var p in parts)
                        {
                            var cleanTag = p.Trim().Trim('"', '\'').Trim();
                            if (!string.IsNullOrWhiteSpace(cleanTag))
                            {
                                result.Tags.Add(new ExportNoteTagDto
                                {
                                    TagName = cleanTag,
                                    Origin = TagOrigin.Manual
                                });
                            }
                        }
                    }
                    break;
            }
        }

        return true;
    }

    private static bool ParseBool(string val)
    {
        val = val.Trim().ToLowerInvariant();
        return val == "true" || val == "1" || val == "yes" || val == "y";
    }

    private static string ExtractTitle(string text, string fallback)
    {
        if (string.IsNullOrWhiteSpace(text))
            return fallback;

        var firstLine = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault()?.Trim() ?? string.Empty;

        firstLine = firstLine.TrimStart('#', '*', '-', ' ', '\t');
        if (firstLine.Length > 60)
        {
            firstLine = firstLine[..60] + "…";
        }

        return string.IsNullOrWhiteSpace(firstLine) ? fallback : firstLine;
    }

    private class ParsedFrontMatter
    {
        public int Id { get; set; }
        public string? Title { get; set; }
        public DateTime? CreatedAt { get; set; }
        public DateTime? UpdatedAt { get; set; }
        public DateTime? CapturedAt { get; set; }
        public bool IsPinned { get; set; }
        public bool IsFavorite { get; set; }
        public bool IsInbox { get; set; }
        public string? SourceProcessName { get; set; }
        public string? SourceWindowTitle { get; set; }
        public string? SourceUrl { get; set; }
        public List<ExportNoteTagDto> Tags { get; set; } = new();
    }
}
