using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using Microsoft.EntityFrameworkCore;
using QuickNotes.App.Data;
using QuickNotes.App.Models;
using QuickNotes.App.Services.Sync;

namespace QuickNotes.App.Services;

public class NoteArchiveService : INoteArchiveService
{
    public const int ManifestSchemaVersion = 1;
    public const string FormatName = "quicknotes-archive";

    internal static Action? TestInjectFailure { get; set; }

    /// <summary>Test hook: invoked after the last SaveChanges and immediately before commit.</summary>
    internal static Action? TestBeforeCommit { get; set; }

    /// <summary>Test hook: invoked after the plaintext temp ZIP exists and before atomic publish.</summary>
    internal Action<string>? AfterTempZipCreatedForTests { get; set; }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public ExportResult ExportArchive(QuickNotesDbContext db, string targetFilePath, IAttachmentStorageService attachments, CancellationToken cancellationToken = default)
    {
        if (attachments == null) throw new ArgumentNullException(nameof(attachments));

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            string tempDir = Path.Combine(Path.GetTempPath(), "qn_archive_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                var notes = db.Notes
                    .Where(n => n.DeletedAt == null)
                    .Include(n => n.NoteTags).ThenInclude(nt => nt.Tag)
                    .Include(n => n.Attachments)
                    .ToList();
                int skippedProtected = notes.Count(n => n.IsProtected);
                notes = notes.Where(n => !n.IsProtected).ToList();

                var tags = db.Tags.Include(t => t.Synonyms).Include(t => t.ParentTag).ToList();
                var manifest = new ArchiveManifest
                {
                    SchemaVersion = ManifestSchemaVersion,
                    Format = FormatName,
                    ExportedAtUtc = DateTime.UtcNow
                };

                Directory.CreateDirectory(Path.Combine(tempDir, "notes"));
                Directory.CreateDirectory(Path.Combine(tempDir, "files"));

                foreach (var note in notes)
                {
                    string noteFile = $"notes/{note.SyncId:D}.json";
                    string noteJson = JsonSerializer.Serialize(ToExportNote(note), JsonOptions);
                    string noteFull = Path.Combine(tempDir, noteFile.Replace('/', Path.DirectorySeparatorChar));
                    File.WriteAllText(noteFull, noteJson, Encoding.UTF8);

                    var entry = new ArchiveNoteEntry
                    {
                        SyncId = note.SyncId,
                        OriginalLocalId = note.Id,
                        TextPath = noteFile,
                        Sha256 = AttachmentFileHelper.ComputeSha256(noteFull),
                        Note = ToExportNote(note)
                    };

                    foreach (var att in note.Attachments)
                    {
                        string fullPath = attachments.GetFullPath(att.RelativePath);
                        if (!File.Exists(fullPath))
                        {
                            continue;
                        }

                        string sha = string.IsNullOrWhiteSpace(att.Sha256)
                            ? AttachmentFileHelper.ComputeSha256(fullPath)
                            : att.Sha256.ToLowerInvariant();
                        string safeName = SanitizeArchiveFileName(att.OriginalFileName);
                        string archiveRel = $"files/{sha}/{safeName}";
                        string dest = Path.Combine(tempDir, archiveRel.Replace('/', Path.DirectorySeparatorChar));
                        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                        if (!File.Exists(dest))
                        {
                            File.Copy(fullPath, dest, overwrite: false);
                        }

                        string actualSha = AttachmentFileHelper.ComputeSha256(dest);
                        if (!string.Equals(actualSha, sha, StringComparison.OrdinalIgnoreCase))
                        {
                            throw new InvalidOperationException($"Контрольная сумма вложения «{att.OriginalFileName}» не совпала при упаковке архива.");
                        }

                        entry.Attachments.Add(new ArchiveAttachmentEntry
                        {
                            OriginalFileName = att.OriginalFileName,
                            StoredFileName = att.StoredFileName,
                            ArchivePath = archiveRel,
                            Sha256 = actualSha,
                            Size = att.Size,
                            ContentType = att.ContentType
                        });
                    }

                    manifest.Notes.Add(entry);
                }

                manifest.Tags = tags.Select(t => new ExportTagDto
                {
                    Id = t.Id,
                    SyncId = t.SyncId,
                    Name = t.Name,
                    ParentTagId = t.ParentTagId,
                    ParentTagName = t.ParentTag?.Name,
                    Synonyms = t.Synonyms.Select(s => s.Value).ToList()
                }).ToList();

                File.WriteAllText(Path.Combine(tempDir, "manifest.json"), JsonSerializer.Serialize(manifest, JsonOptions), Encoding.UTF8);

                string? tempZip = targetFilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    if (File.Exists(tempZip)) File.Delete(tempZip);
                    ZipFile.CreateFromDirectory(tempDir, tempZip, CompressionLevel.Optimal, includeBaseDirectory: false);
                    AfterTempZipCreatedForTests?.Invoke(tempZip);
                    cancellationToken.ThrowIfCancellationRequested();
                    File.Move(tempZip, targetFilePath, overwrite: true);
                    tempZip = null;
                }
                finally
                {
                    TryDeleteFile(tempZip);
                }

                return new ExportResult
                {
                    Success = true,
                    ExportedNotesCount = notes.Count,
                    SkippedProtectedCount = skippedProtected,
                    ExportPath = targetFilePath
                };
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { /* ignore */ }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            ErrorLogService.Write("NoteArchiveService.Export", ex);
            return new ExportResult
            {
                Success = false,
                ErrorMessage = $"Не удалось создать архив: {ex.Message}"
            };
        }
    }

    public ImportPreviewResult PreviewArchive(QuickNotesDbContext db, string archivePath)
    {
        var preview = new ImportPreviewResult
        {
            ArchivePath = archivePath,
            IsPortableArchive = true,
            ConflictPolicyDescription = "Политика при конфликтах: пропускать существующие заметки (без перезаписи). Архив содержит файлы-вложения."
        };

        try
        {
            if (!File.Exists(archivePath))
            {
                preview.Diagnostics.Add(new ImportDiagnosticItem { FileName = archivePath, Reason = "Файл архива не найден." });
                return preview;
            }

            using var zip = ZipFile.OpenRead(archivePath);
            var manifestEntry = zip.GetEntry("manifest.json");
            if (manifestEntry == null)
            {
                preview.Diagnostics.Add(new ImportDiagnosticItem { FileName = Path.GetFileName(archivePath), Reason = "В архиве нет manifest.json — это не полный архив QuickNotes." });
                return preview;
            }

            using var manifestStream = manifestEntry.Open();
            using var reader = new StreamReader(manifestStream, Encoding.UTF8);
            var manifest = JsonSerializer.Deserialize<ArchiveManifest>(reader.ReadToEnd(), JsonOptions);
            if (manifest == null || !string.Equals(manifest.Format, FormatName, StringComparison.OrdinalIgnoreCase))
            {
                preview.Diagnostics.Add(new ImportDiagnosticItem { FileName = "manifest.json", Reason = "Неизвестный формат архива." });
                return preview;
            }

            preview.PackageTags = manifest.Tags ?? new List<ExportTagDto>();
            preview.TotalFilesDiscovered = 1;
            var existingSync = db.Notes.AsNoTracking().Select(n => n.SyncId).ToHashSet();
            var existingText = db.Notes.AsNoTracking().Where(n => n.DeletedAt == null).Select(n => n.Text).ToList()
                .Select(t => Normalize(t)).ToHashSet(StringComparer.Ordinal);

            foreach (var entry in manifest.Notes)
            {
                if (entry.Note == null)
                {
                    preview.Diagnostics.Add(new ImportDiagnosticItem { FileName = entry.TextPath, Reason = "Повреждённая запись заметки." });
                    preview.TotalSkippedOrErroneous++;
                    continue;
                }

                foreach (var att in entry.Attachments ?? new List<ArchiveAttachmentEntry>())
                {
                    if (!IsSafeArchivePath(att.ArchivePath))
                    {
                        preview.Diagnostics.Add(new ImportDiagnosticItem
                        {
                            FileName = att.OriginalFileName,
                            Reason = "Небезопасный путь вложения в архиве — файл пропущен."
                        });
                    }
                    else
                    {
                        var zipEntry = zip.GetEntry(att.ArchivePath);
                        if (zipEntry == null)
                        {
                            preview.Diagnostics.Add(new ImportDiagnosticItem
                            {
                                FileName = att.OriginalFileName,
                                Reason = "Файл вложения отсутствует в архиве."
                            });
                        }
                    }
                }

                bool conflict = (entry.SyncId != Guid.Empty && existingSync.Contains(entry.SyncId))
                                || existingText.Contains(Normalize(entry.Note.Text));
                var item = new ImportItemPreview
                {
                    SourceFileName = Path.GetFileName(archivePath),
                    OriginalId = entry.OriginalLocalId,
                    SyncId = entry.SyncId,
                    Title = entry.Note.Title ?? string.Empty,
                    Text = entry.Note.Text,
                    CreatedAt = entry.Note.CreatedAt,
                    UpdatedAt = entry.Note.UpdatedAt,
                    IsPinned = entry.Note.IsPinned,
                    IsFavorite = entry.Note.IsFavorite,
                    IsInbox = entry.Note.IsInbox,
                    SourceProcessName = entry.Note.SourceProcessName,
                    SourceWindowTitle = entry.Note.SourceWindowTitle,
                    SourceUrl = entry.Note.SourceUrl,
                    CapturedAt = entry.Note.CapturedAt,
                    Tags = entry.Note.Tags ?? new List<ExportNoteTagDto>(),
                    ArchiveAttachments = entry.Attachments ?? new List<ArchiveAttachmentEntry>(),
                    IsConflict = conflict,
                    Status = conflict ? "Пропуск (уже есть)" : "Готова к импорту",
                    ConflictReason = conflict ? "Заметка с таким содержимым или идентификатором уже есть в базе." : string.Empty
                };

                preview.Items.Add(item);
                if (conflict)
                {
                    preview.TotalConflicts++;
                }
                else
                {
                    preview.TotalNotesToImport++;
                }
            }

            preview.NewTagsCount = preview.PackageTags.Count;
            return preview;
        }
        catch (Exception ex)
        {
            ErrorLogService.Write("NoteArchiveService.Preview", ex);
            preview.Diagnostics.Add(new ImportDiagnosticItem
            {
                FileName = Path.GetFileName(archivePath),
                Reason = $"Не удалось прочитать архив: {ex.Message}"
            });
            return preview;
        }
    }

    public ImportExecutionResult ImportArchive(
        QuickNotesDbContext db,
        ImportPreviewResult preview,
        IAttachmentStorageService attachments,
        ILocalMutationCoordinator mutationCoordinator,
        INoteHistoryService? historyService = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mutationCoordinator);

        if (preview == null || !preview.IsPortableArchive || string.IsNullOrWhiteSpace(preview.ArchivePath))
        {
            return new ImportExecutionResult { Success = false, ErrorMessage = "Нет подготовленного архива для импорта." };
        }

        string archivePath = preview.ArchivePath;
        return mutationCoordinator.ExecuteBulkMutation(() =>
            ImportArchiveInsideMutationBoundary(db, preview, archivePath, attachments, historyService, cancellationToken));
    }

    private static ImportExecutionResult ImportArchiveInsideMutationBoundary(
        QuickNotesDbContext db,
        ImportPreviewResult preview,
        string archivePath,
        IAttachmentStorageService attachments,
        INoteHistoryService? historyService,
        CancellationToken cancellationToken)
    {
        var createdFiles = new List<string>();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var zip = ZipFile.OpenRead(archivePath);
            using var tx = db.Database.BeginTransaction();
            int imported = 0;
            var tagMap = EnsureTags(db, preview.PackageTags);

            foreach (var item in preview.Items.Where(i => !i.IsConflict))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var note = new Note
                {
                    SyncId = item.SyncId.HasValue && item.SyncId.Value != Guid.Empty ? item.SyncId.Value : Guid.NewGuid(),
                    Title = item.Title ?? string.Empty,
                    Text = item.Text ?? string.Empty,
                    CreatedAt = item.CreatedAt,
                    UpdatedAt = item.UpdatedAt,
                    IsPinned = item.IsPinned,
                    IsFavorite = item.IsFavorite,
                    IsInbox = item.IsInbox,
                    SourceProcessName = item.SourceProcessName,
                    SourceWindowTitle = item.SourceWindowTitle,
                    SourceUrl = item.SourceUrl,
                    CapturedAt = item.CapturedAt
                };
                db.Notes.Add(note);
                db.SaveChanges();

                foreach (var tag in item.Tags)
                {
                    if (!tagMap.TryGetValue(tag.TagName, out int tagId))
                    {
                        continue;
                    }

                    db.NoteTags.Add(new NoteTag
                    {
                        NoteId = note.Id,
                        TagId = tagId,
                        Origin = tag.Origin,
                        IsSuppressed = tag.IsSuppressed
                    });
                }

                foreach (var att in item.ArchiveAttachments)
                {
                    if (!IsSafeArchivePath(att.ArchivePath))
                    {
                        continue;
                    }

                    var zipEntry = zip.GetEntry(att.ArchivePath);
                    if (zipEntry == null)
                    {
                        continue;
                    }

                    using var entryStream = zipEntry.Open();
                    using var ms = new MemoryStream();
                    entryStream.CopyTo(ms);
                    var bytes = ms.ToArray();
                    string sha = AttachmentFileHelper.ComputeSha256(bytes);
                    if (!string.IsNullOrWhiteSpace(att.Sha256) &&
                        !string.Equals(sha, att.Sha256, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException($"SHA-256 вложения «{att.OriginalFileName}» не совпал.");
                    }

                    string predicted = Path.Combine(
                        attachments.AttachmentsDirectory,
                        AttachmentFileHelper.GetSafeStoredFileName(
                            sha,
                            string.IsNullOrWhiteSpace(att.OriginalFileName) ? "file.bin" : Path.GetFileName(att.OriginalFileName)));
                    bool existed = File.Exists(predicted);
                    var saved = attachments.SaveFromBytes(bytes, att.OriginalFileName, 0);
                    if (!existed)
                    {
                        createdFiles.Add(saved.FullPath);
                    }

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
                }

                db.SaveChanges();
                historyService?.SaveSnapshot(db, note);
                imported++;
                TestInjectFailure?.Invoke();
            }

            db.SaveChanges();
            TestBeforeCommit?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            tx.Commit();
            return new ImportExecutionResult { Success = true, ImportedNotesCount = imported };
        }
        catch (OperationCanceledException)
        {
            RollbackCreatedFiles(createdFiles);
            return new ImportExecutionResult { Success = false, ErrorMessage = "Импорт архива отменён." };
        }
        catch (Exception ex)
        {
            RollbackCreatedFiles(createdFiles);
            ErrorLogService.Write("NoteArchiveService.Import", ex);
            return new ImportExecutionResult { Success = false, ErrorMessage = $"Импорт архива не выполнен: {ex.Message}" };
        }
    }

    private static void TryDeleteFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch
        {
            // Best-effort cleanup of unpublished plaintext ZIP.
        }
    }

    private static void RollbackCreatedFiles(List<string> createdFiles)
    {
        foreach (var file in createdFiles.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (File.Exists(file))
                {
                    File.Delete(file);
                }
            }
            catch
            {
                // Best-effort cleanup of files created by a failed import.
            }
        }
    }

    public static bool IsSafeArchivePath(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return false;
        }

        var normalized = relativePath.Replace('\\', '/');
        if (normalized.StartsWith('/') || normalized.Contains("..") || Path.IsPathRooted(relativePath))
        {
            return false;
        }

        return normalized.StartsWith("files/", StringComparison.OrdinalIgnoreCase)
               || normalized.StartsWith("notes/", StringComparison.OrdinalIgnoreCase);
    }

    private static string SanitizeArchiveFileName(string? name)
    {
        var file = Path.GetFileName(name ?? "file.bin");
        foreach (var c in Path.GetInvalidFileNameChars())
        {
            file = file.Replace(c, '_');
        }

        return string.IsNullOrWhiteSpace(file) ? "file.bin" : file;
    }

    private static string Normalize(string? text)
        => (text ?? string.Empty).Replace("\r\n", "\n").Trim();

    private static ExportNoteDto ToExportNote(Note note)
    {
        return new ExportNoteDto
        {
            Id = note.Id,
            Title = note.Title ?? string.Empty,
            Text = note.Text,
            CreatedAt = note.CreatedAt,
            UpdatedAt = note.UpdatedAt,
            IsPinned = note.IsPinned,
            IsFavorite = note.IsFavorite,
            IsInbox = note.IsInbox,
            SourceProcessName = note.SourceProcessName,
            SourceWindowTitle = note.SourceWindowTitle,
            SourceUrl = note.SourceUrl,
            CapturedAt = note.CapturedAt,
            Tags = note.NoteTags.Select(nt => new ExportNoteTagDto
            {
                TagId = nt.TagId,
                TagName = nt.Tag?.Name ?? $"#{nt.TagId}",
                Origin = nt.Origin,
                IsSuppressed = nt.IsSuppressed
            }).ToList()
        };
    }

    private static Dictionary<string, int> EnsureTags(QuickNotesDbContext db, List<ExportTagDto> packageTags)
    {
        var map = db.Tags.ToDictionary(t => t.Name, t => t.Id, StringComparer.OrdinalIgnoreCase);
        var bySync = db.Tags.Where(t => t.SyncId != Guid.Empty).ToDictionary(t => t.SyncId, t => t.Id);

        foreach (var tag in packageTags.OrderBy(t => t.ParentTagId.HasValue ? 1 : 0))
        {
            if (string.IsNullOrWhiteSpace(tag.Name))
            {
                continue;
            }

            string name = tag.Name.Trim();
            Tag? entity = null;
            if (tag.SyncId != Guid.Empty && bySync.TryGetValue(tag.SyncId, out int existingId))
            {
                entity = db.Tags.Find(existingId);
            }

            if (entity == null && map.TryGetValue(name, out int byNameId))
            {
                entity = db.Tags.Find(byNameId);
            }

            if (entity == null)
            {
                entity = new Tag
                {
                    Name = name,
                    SyncId = tag.SyncId != Guid.Empty ? tag.SyncId : Guid.NewGuid()
                };
                db.Tags.Add(entity);
                db.SaveChanges();
            }

            map[entity.Name] = entity.Id;
            if (entity.SyncId != Guid.Empty)
            {
                bySync[entity.SyncId] = entity.Id;
            }

            foreach (var synonym in tag.Synonyms ?? new List<string>())
            {
                if (string.IsNullOrWhiteSpace(synonym))
                {
                    continue;
                }

                string syn = synonym.Trim();
                bool exists = db.TagSynonyms.Any(s => s.TagId == entity.Id && s.Value == syn);
                if (!exists)
                {
                    db.TagSynonyms.Add(new TagSynonym { TagId = entity.Id, Value = syn });
                }
            }
        }

        db.SaveChanges();

        foreach (var tag in packageTags)
        {
            if (string.IsNullOrWhiteSpace(tag.Name) || string.IsNullOrWhiteSpace(tag.ParentTagName))
            {
                continue;
            }

            if (!map.TryGetValue(tag.Name, out int childId) || !map.TryGetValue(tag.ParentTagName, out int parentId) || childId == parentId)
            {
                continue;
            }

            var child = db.Tags.Find(childId);
            if (child != null && child.ParentTagId != parentId)
            {
                child.ParentTagId = parentId;
            }
        }

        db.SaveChanges();
        return map;
    }
}
