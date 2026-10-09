using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using Microsoft.EntityFrameworkCore;
using QuickNotes.App.Data;
using QuickNotes.App.Models;

namespace QuickNotes.App.Services;

public class NoteExportService : INoteExportService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public ExportResult ExportToJson(QuickNotesDbContext db, string targetFilePath, CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var notes = db.Notes
                .Where(n => n.DeletedAt == null && n.IsProtected == false)
                .Include(n => n.NoteTags)
                .ThenInclude(nt => nt.Tag)
                .ToList();

            var tags = db.Tags
                .Include(t => t.Synonyms)
                .Include(t => t.ParentTag)
                .ToList();

            var json = BuildJson(notes, tags);

            SafeWriteFile(targetFilePath, tempPath => File.WriteAllText(tempPath, json, Encoding.UTF8), cancellationToken);

            return Succeeded(notes.Count, CountTags(tags), 0, CountProtected(db), 0, 0, targetFilePath);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            ErrorLogService.Write("NoteExportService.ExportToJson", $"{ex.GetType().Name}: {ex.Message}");
            return new ExportResult
            {
                Success = false,
                ErrorMessage = $"Ошибка при экспорте в JSON: {ex.Message}"
            };
        }
    }

    public ExportResult ExportToCsv(QuickNotesDbContext db, string targetFilePath, CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var notes = db.Notes
                .Where(n => n.DeletedAt == null && n.IsProtected == false)
                .Include(n => n.NoteTags)
                .ThenInclude(nt => nt.Tag)
                .ToList();

            var csv = BuildCsv(notes);

            var utf8WithBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
            SafeWriteFile(targetFilePath, tempPath => File.WriteAllText(tempPath, csv, utf8WithBom), cancellationToken);

            return Succeeded(notes.Count, CountDistinctTags(notes), 0, CountProtected(db), 0, 0, targetFilePath);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            ErrorLogService.Write("NoteExportService.ExportToCsv", $"{ex.GetType().Name}: {ex.Message}");
            return new ExportResult
            {
                Success = false,
                ErrorMessage = $"Ошибка при экспорте в CSV: {ex.Message}"
            };
        }
    }

    private static int CountProtected(QuickNotesDbContext db)
    {
        try
        {
            return db.Notes.Count(n => n.DeletedAt == null && n.IsProtected);
        }
        catch
        {
            return 0;
        }
    }

    private static int CountTags(IEnumerable<Tag> tags) => tags.Count();

    private static int CountDistinctTags(IEnumerable<Note> notes)
        => notes.SelectMany(n => n.NoteTags.Where(nt => !nt.IsSuppressed && nt.Tag != null).Select(nt => nt.TagId)).Distinct().Count();

    internal static ExportResult Succeeded(
        int notes,
        int tags,
        int attachments,
        int skippedProtected,
        int skippedMissingAttachments,
        int errors,
        string path)
    {
        bool complete = skippedProtected == 0 && skippedMissingAttachments == 0 && errors == 0;
        return new ExportResult
        {
            Success = true,
            ExportedNotesCount = notes,
            ExportedTagCount = tags,
            ExportedAttachmentCount = attachments,
            SkippedProtectedCount = skippedProtected,
            SkippedMissingAttachmentCount = skippedMissingAttachments,
            ErrorCount = errors,
            FormatVersion = OpenExportManifest.CurrentFormatVersion,
            ExportedAtUtc = DateTime.UtcNow,
            IsComplete = complete,
            CompletenessSummary = BuildCompletenessSummary(skippedProtected, skippedMissingAttachments, errors),
            ExportPath = path
        };
    }

    internal static string BuildCompletenessSummary(int skippedProtected, int skippedMissingAttachments, int errors)
    {
        if (skippedProtected == 0 && skippedMissingAttachments == 0 && errors == 0)
        {
            return "полный (все незащищённые заметки и доступные вложения)";
        }

        return $"неполный: пропущено защищённых заметок {skippedProtected}; недоступных вложений {skippedMissingAttachments}; ошибок {errors}";
    }

    public static void SafeWriteFile(string targetFilePath, Action<string> writeTempFile, CancellationToken cancellationToken = default)
    {
        var fullTargetPath = Path.GetFullPath(targetFilePath);
        var dir = Path.GetDirectoryName(fullTargetPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var directory = string.IsNullOrEmpty(dir) ? "." : dir;
        var fileName = Path.GetFileName(fullTargetPath);
        var tempFilePath = Path.Combine(directory, $".tmp_{fileName}_{Guid.NewGuid():N}");

        try
        {
            writeTempFile(tempFilePath);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(tempFilePath, fullTargetPath, overwrite: true);
        }
        catch
        {
            if (File.Exists(tempFilePath))
            {
                try
                {
                    File.Delete(tempFilePath);
                }
                catch
                {
                    // Ignore temp file deletion errors
                }
            }
            throw;
        }
    }

    /// <summary>
    /// Test hook: invoked after the staging tree is complete, before the commit point.
    /// Cancellation here rolls back: previous target is untouched and staging is deleted.
    /// </summary>
    internal Action<string>? BeforePublishForTests { get; set; }

    /// <summary>
    /// Test hook: invoked after the previous snapshot was moved aside and before the new staging
    /// directory is published. This is inside the post-commit-point critical section; cancellation
    /// is ignored and the swap either completes or restores the previous snapshot.
    /// </summary>
    internal Action<string>? AfterTargetMovedAsideForTests { get; set; }

    public ExportResult ExportToMarkdown(
        QuickNotesDbContext db,
        string targetDirectoryPath,
        IAttachmentStorageService? attachments = null,
        CancellationToken cancellationToken = default)
    {
        string? tempDir = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var normalizedTargetPath = Path.GetFullPath(targetDirectoryPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            var notes = db.Notes
                .Where(n => n.DeletedAt == null && n.IsProtected == false)
                .Include(n => n.NoteTags)
                .ThenInclude(nt => nt.Tag)
                .Include(n => n.Attachments)
                .ToList();

            var tags = db.Tags
                .Include(t => t.Synonyms)
                .Include(t => t.ParentTag)
                .ToList();

            var notesById = notes.ToDictionary(n => n.Id);
            var notesBySync = notes
                .Where(n => n.SyncId != Guid.Empty)
                .GroupBy(n => n.SyncId)
                .ToDictionary(g => g.Key, g => g.First());

            var protectedIds = db.Notes
                .Where(n => n.DeletedAt == null && n.IsProtected)
                .Select(n => n.Id)
                .ToHashSet();
            var protectedSyncIds = db.Notes
                .Where(n => n.DeletedAt == null && n.IsProtected)
                .Select(n => n.SyncId)
                .ToHashSet();

            var parentDir = Path.GetDirectoryName(normalizedTargetPath);
            if (!string.IsNullOrEmpty(parentDir) && !Directory.Exists(parentDir))
            {
                Directory.CreateDirectory(parentDir);
            }

            var directoryForTemp = string.IsNullOrEmpty(parentDir) ? "." : parentDir;
            var targetDirName = Path.GetFileName(normalizedTargetPath);
            tempDir = Path.Combine(directoryForTemp, $".tmp_export_{targetDirName}_{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);

            var linker = new NoteLinkService();
            var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var checksums = new List<OpenExportFileChecksum>();
            var errors = new List<string>();
            int copiedAttachments = 0;
            int skippedMissingAttachments = 0;

            foreach (var note in notes)
            {
                int collisionIndex = 0;
                string fileName = BuildSafeFileName(note.Id, note.Title, note.Text, collisionIndex);
                while (!usedNames.Add(fileName))
                {
                    fileName = BuildSafeFileName(note.Id, note.Title, note.Text, ++collisionIndex);
                }

                var extraLines = new List<string>
                {
                    $"title: {EscapeYamlValue(note.Title ?? string.Empty)}",
                    $"sync_id: {note.SyncId:D}"
                };

                var linkLines = new List<string>();
                foreach (var link in linker.ExtractLinkRefs(note.Text))
                {
                    Note? target = null;
                    if (link.SyncId.HasValue)
                    {
                        notesBySync.TryGetValue(link.SyncId.Value, out target);
                    }

                    if (target == null && link.LocalId.HasValue)
                    {
                        notesById.TryGetValue(link.LocalId.Value, out target);
                    }

                    bool pointsAtProtected =
                        (link.LocalId.HasValue && protectedIds.Contains(link.LocalId.Value))
                        || (link.SyncId.HasValue && protectedSyncIds.Contains(link.SyncId.Value));

                    if (target == null || pointsAtProtected)
                    {
                        continue;
                    }

                    linkLines.Add($"  - id: {target.Id}");
                    linkLines.Add($"    sync_id: {target.SyncId:D}");
                    linkLines.Add($"    title: {EscapeYamlValue(Helpers.NoteTitleHelper.GetDisplayTitle(target.Title, target.Text))}");
                }

                if (linkLines.Count > 0)
                {
                    extraLines.Add("links:");
                    extraLines.AddRange(linkLines);
                }
                else
                {
                    extraLines.Add("links: []");
                }

                var attachmentLines = new List<string>();
                foreach (var att in note.Attachments)
                {
                    if (att.IsProtected)
                    {
                        skippedMissingAttachments++;
                        continue;
                    }

                    string? fullPath = attachments != null ? attachments.GetFullPath(att.RelativePath) : null;
                    if (string.IsNullOrWhiteSpace(fullPath) || !File.Exists(fullPath))
                    {
                        skippedMissingAttachments++;
                        errors.Add($"Вложение «{att.OriginalFileName}» заметки {note.Id} недоступно на диске.");
                        continue;
                    }

                    try
                    {
                        string sha = string.IsNullOrWhiteSpace(att.Sha256)
                            ? AttachmentFileHelper.ComputeSha256(fullPath)
                            : att.Sha256.ToLowerInvariant();
                        string safeName = SanitizeExportFileName(att.OriginalFileName);
                        string archiveRel = $"files/{sha}/{safeName}";
                        string dest = Path.Combine(tempDir, archiveRel.Replace('/', Path.DirectorySeparatorChar));
                        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                        if (!File.Exists(dest))
                        {
                            File.Copy(fullPath, dest, overwrite: false);
                        }

                        string actualSha = AttachmentFileHelper.ComputeSha256(dest);
                        if (!string.IsNullOrWhiteSpace(att.Sha256)
                            && !string.Equals(actualSha, att.Sha256, StringComparison.OrdinalIgnoreCase))
                        {
                            errors.Add($"SHA-256 вложения «{att.OriginalFileName}» не совпал при копировании.");
                        }

                        copiedAttachments++;
                        attachmentLines.Add($"  - original: {EscapeYamlValue(att.OriginalFileName)}");
                        attachmentLines.Add($"    path: {archiveRel}");
                        attachmentLines.Add($"    sha256: {actualSha}");
                    }
                    catch (Exception ex)
                    {
                        skippedMissingAttachments++;
                        errors.Add($"Не удалось скопировать вложение «{att.OriginalFileName}»: {ex.Message}");
                    }
                }

                if (attachmentLines.Count > 0)
                {
                    extraLines.Add("attachments:");
                    extraLines.AddRange(attachmentLines);
                }
                else
                {
                    extraLines.Add("attachments: []");
                }

                string filePath = Path.Combine(tempDir, fileName);
                string content = GenerateMarkdownContent(note, extraLines);
                File.WriteAllText(filePath, content, Encoding.UTF8);
            }

            var exportedAt = DateTime.UtcNow;
            CollectChecksums(tempDir, checksums);
            var manifest = new OpenExportManifest
            {
                FormatVersion = OpenExportManifest.CurrentFormatVersion,
                Format = OpenExportManifest.FormatName,
                ExportedAtUtc = exportedAt,
                SkippedProtectedNotes = CountProtected(db),
                SkippedMissingAttachments = skippedMissingAttachments,
                ErrorCount = errors.Count,
                Errors = errors,
                Files = checksums,
                EntityCounts = new OpenExportEntityCounts
                {
                    Notes = notes.Count,
                    Tags = tags.Count,
                    Attachments = copiedAttachments,
                    Files = checksums.Count
                }
            };

            File.WriteAllText(
                Path.Combine(tempDir, "manifest.json"),
                JsonSerializer.Serialize(manifest, JsonOptions),
                Encoding.UTF8);

            PublishDirectorySnapshot(tempDir, normalizedTargetPath, cancellationToken);
            tempDir = null;

            var result = Succeeded(
                notes.Count,
                tags.Count,
                copiedAttachments,
                manifest.SkippedProtectedNotes,
                skippedMissingAttachments,
                errors.Count,
                targetDirectoryPath);
            result.ExportedAtUtc = exportedAt;
            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            ErrorLogService.Write("NoteExportService.ExportToMarkdown", $"{ex.GetType().Name}: {ex.Message}");
            return new ExportResult
            {
                Success = false,
                ErrorMessage = $"Ошибка при экспорте в Markdown: {ex.Message}"
            };
        }
        finally
        {
            if (tempDir != null && Directory.Exists(tempDir))
            {
                TryDeleteDirectory(tempDir);
            }
        }
    }

    /// <summary>
    /// Atomically replaces <paramref name="targetDir"/> with <paramref name="stagingDir"/>.
    /// Cancellation is honored only before the commit point (the first directory move). After
    /// that, the short critical section runs to completion: publish staging or restore backup.
    /// </summary>
    internal void PublishDirectorySnapshot(string stagingDir, string targetDir, CancellationToken cancellationToken = default)
    {
        BeforePublishForTests?.Invoke(stagingDir);
        cancellationToken.ThrowIfCancellationRequested();

        var parent = Path.GetDirectoryName(targetDir);
        if (string.IsNullOrEmpty(parent))
        {
            parent = ".";
        }

        string? backup = null;
        try
        {
            if (Directory.Exists(targetDir))
            {
                backup = Path.Combine(parent, $".bak_export_{Path.GetFileName(targetDir)}_{Guid.NewGuid():N}");
                Directory.Move(targetDir, backup);
                AfterTargetMovedAsideForTests?.Invoke(backup);
            }

            Directory.Move(stagingDir, targetDir);

            if (backup != null)
            {
                TryDeleteDirectory(backup);
            }
        }
        catch
        {
            if (backup != null && !Directory.Exists(targetDir) && Directory.Exists(backup))
            {
                try
                {
                    Directory.Move(backup, targetDir);
                }
                catch (Exception restoreEx)
                {
                    ErrorLogService.Write("NoteExportService.PublishDirectorySnapshot.Restore", restoreEx);
                }
            }

            throw;
        }
    }

    private static void CollectChecksums(string root, List<OpenExportFileChecksum> checksums)
    {
        foreach (var file in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            checksums.Add(new OpenExportFileChecksum
            {
                Path = relative,
                Sha256 = AttachmentFileHelper.ComputeSha256(file)
            });
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup of staging/backup directories.
        }
    }

    private static string SanitizeExportFileName(string? name)
    {
        var file = Path.GetFileName(name ?? "file.bin");
        foreach (var c in Path.GetInvalidFileNameChars())
        {
            file = file.Replace(c, '_');
        }

        return string.IsNullOrWhiteSpace(file) ? "file.bin" : file;
    }


    public string BuildJson(IEnumerable<Note> notes, IEnumerable<Tag> tags)
    {
        var package = new QuickNotesExportPackage
        {
            SchemaVersion = 1,
            ExportedAt = DateTime.UtcNow,
            AppVersion = "1.0",
            Tags = tags.Select(t => new ExportTagDto
            {
                Id = t.Id,
                Name = t.Name,
                ParentTagId = t.ParentTagId,
                ParentTagName = t.ParentTag?.Name,
                Synonyms = t.Synonyms.Select(s => s.Value).ToList()
            }).ToList(),
            Notes = notes.Select(n => new ExportNoteDto
            {
                Id = n.Id,
                Title = n.Title ?? string.Empty,
                Text = n.Text,
                CreatedAt = n.CreatedAt,
                UpdatedAt = n.UpdatedAt,
                IsPinned = n.IsPinned,
                IsFavorite = n.IsFavorite,
                IsInbox = n.IsInbox,
                SourceProcessName = SanitizeProcessName(n.SourceProcessName),
                SourceWindowTitle = n.SourceWindowTitle,
                SourceUrl = n.SourceUrl,
                CapturedAt = n.CapturedAt,
                Tags = n.NoteTags.Select(nt => new ExportNoteTagDto
                {
                    TagId = nt.TagId,
                    TagName = nt.Tag?.Name ?? $"#{nt.TagId}",
                    Origin = nt.Origin,
                    IsSuppressed = nt.IsSuppressed
                }).ToList()
            }).ToList()
        };

        return JsonSerializer.Serialize(package, JsonOptions);
    }

    public string BuildCsv(IEnumerable<Note> notes)
    {
        var sb = new StringBuilder();
        // Header
        sb.AppendLine("Id,Text,CreatedAt,UpdatedAt,IsPinned,IsFavorite,IsInbox,Tags,SourceProcessName,SourceWindowTitle,SourceUrl,CapturedAt");

        foreach (var n in notes)
        {
            var activeTags = n.NoteTags
                .Where(nt => !nt.IsSuppressed)
                .Select(nt => nt.Tag?.Name ?? $"#{nt.TagId}")
                .Where(name => !string.IsNullOrWhiteSpace(name));

            var tagsStr = string.Join("; ", activeTags);

            sb.Append(EscapeCsv(n.Id.ToString())).Append(',');
            sb.Append(EscapeCsv(n.Text)).Append(',');
            sb.Append(EscapeCsv(n.CreatedAt.ToString("o"))).Append(',');
            sb.Append(EscapeCsv(n.UpdatedAt.ToString("o"))).Append(',');
            sb.Append(EscapeCsv(n.IsPinned ? "true" : "false")).Append(',');
            sb.Append(EscapeCsv(n.IsFavorite ? "true" : "false")).Append(',');
            sb.Append(EscapeCsv(n.IsInbox ? "true" : "false")).Append(',');
            sb.Append(EscapeCsv(tagsStr)).Append(',');
            sb.Append(EscapeCsv(SanitizeProcessName(n.SourceProcessName))).Append(',');
            sb.Append(EscapeCsv(n.SourceWindowTitle)).Append(',');
            sb.Append(EscapeCsv(n.SourceUrl)).Append(',');
            sb.Append(EscapeCsv(n.CapturedAt?.ToString("o")));
            sb.AppendLine();
        }

        return sb.ToString();
    }

    public string GenerateMarkdownContent(Note note)
        => GenerateMarkdownContent(note, extraFrontMatter: null);

    public string GenerateMarkdownContent(Note note, IReadOnlyList<string>? extraFrontMatter)
    {
        var sb = new StringBuilder();
        sb.AppendLine("---");
        sb.AppendLine($"id: {note.Id}");
        if (extraFrontMatter != null)
        {
            foreach (var line in extraFrontMatter)
            {
                sb.AppendLine(line);
            }
        }

        sb.AppendLine($"created_at: {note.CreatedAt:o}");
        sb.AppendLine($"updated_at: {note.UpdatedAt:o}");
        sb.AppendLine($"is_pinned: {(note.IsPinned ? "true" : "false")}");
        sb.AppendLine($"is_favorite: {(note.IsFavorite ? "true" : "false")}");
        sb.AppendLine($"is_inbox: {(note.IsInbox ? "true" : "false")}");

        var activeTags = note.NoteTags
            .Where(nt => !nt.IsSuppressed)
            .Select(nt => nt.Tag?.Name ?? $"#{nt.TagId}")
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToList();

        if (activeTags.Count == 0)
        {
            sb.AppendLine("tags: []");
        }
        else
        {
            sb.AppendLine("tags:");
            foreach (var tag in activeTags)
            {
                sb.AppendLine($"  - {EscapeYamlValue(tag)}");
            }
        }

        var process = SanitizeProcessName(note.SourceProcessName);
        if (!string.IsNullOrEmpty(process))
        {
            sb.AppendLine($"source_process: {EscapeYamlValue(process)}");
        }

        if (!string.IsNullOrEmpty(note.SourceWindowTitle))
        {
            sb.AppendLine($"source_window: {EscapeYamlValue(note.SourceWindowTitle)}");
        }

        if (!string.IsNullOrEmpty(note.SourceUrl))
        {
            sb.AppendLine($"source_url: {EscapeYamlValue(note.SourceUrl)}");
        }

        if (note.CapturedAt.HasValue)
        {
            sb.AppendLine($"captured_at: {note.CapturedAt.Value:o}");
        }

        sb.AppendLine("---");
        sb.Append(note.Text);

        return sb.ToString();
    }

    public string BuildSafeFileName(int noteId, string? title, string? text, int collisionIndex = 0)
    {
        string candidate = !string.IsNullOrWhiteSpace(title) ? title : (text ?? string.Empty);
        return BuildSafeFileName(noteId, candidate, collisionIndex);
    }

    public string BuildSafeFileName(int noteId, string? text, int collisionIndex = 0)
    {
        string slug = string.Empty;
        if (!string.IsNullOrWhiteSpace(text))
        {
            var firstLine = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault()?.Trim() ?? string.Empty;

            firstLine = firstLine.TrimStart('#', '*', '-', ' ', '\t');
            slug = SanitizeFileName(firstLine);
        }

        if (slug.Length > 40)
        {
            slug = slug[..40].Trim();
        }

        string baseName;
        if (string.IsNullOrWhiteSpace(slug))
        {
            baseName = $"note_{noteId}";
        }
        else
        {
            baseName = $"note_{noteId}_{slug}";
        }

        if (collisionIndex > 0)
        {
            baseName += $"_{collisionIndex}";
        }

        return baseName + ".md";
    }

    public static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder();
        foreach (var ch in name)
        {
            if (!invalid.Contains(ch) && ch != '/' && ch != '\\' && ch != ':' && ch != '*' && ch != '?' && ch != '"' && ch != '<' && ch != '>' && ch != '|')
            {
                sb.Append(ch);
            }
            else
            {
                sb.Append('_');
            }
        }

        var clean = sb.ToString().Trim(' ', '.', '_');
        return string.IsNullOrWhiteSpace(clean) ? string.Empty : clean;
    }

    public static string? SanitizeProcessName(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName))
            return null;

        try
        {
            return Path.GetFileName(processName);
        }
        catch
        {
            return processName.Trim();
        }
    }

    public static string EscapeCsv(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        bool needsQuotes = value.Contains(',') ||
                           value.Contains('"') ||
                           value.Contains('\r') ||
                           value.Contains('\n') ||
                           value.StartsWith(' ') ||
                           value.EndsWith(' ');

        if (!needsQuotes)
            return value;

        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    public static string EscapeYamlValue(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return "\"\"";

        var singleLine = value.Replace("\r", " ").Replace("\n", " ").Trim();
        if (singleLine.Contains(':') || singleLine.Contains('#') || singleLine.Contains('"') ||
            singleLine.Contains('\'') || singleLine.Contains('[') || singleLine.Contains(']') ||
            singleLine.StartsWith('@') || singleLine.StartsWith('`') || singleLine.StartsWith('-'))
        {
            return $"\"{singleLine.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"";
        }

        return singleLine;
    }
}
