using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.EntityFrameworkCore;
using QuickNotes.App.Data;
using QuickNotes.App.Models;
using QuickNotes.App.Services.Sync;

namespace QuickNotes.App.Services;

public partial class NoteImportService
{
    public ImportExecutionResult ExecuteImport(
        QuickNotesDbContext db,
        ImportPreviewResult preview,
        ILocalMutationCoordinator mutationCoordinator,
        INoteHistoryService? historyService = null,
        TagDetectionService? tagDetectionService = null,
        IAttachmentStorageService? attachmentStorage = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mutationCoordinator);

        if (preview == null || preview.Items.Count == 0)
        {
            return new ImportExecutionResult { Success = true, ImportedNotesCount = 0 };
        }

        if (preview.HasBlockingDiagnostics)
        {
            return new ImportExecutionResult { Success = false, ErrorMessage = "Импорт заблокирован диагностикой. Исправьте ошибки и повторите предпросмотр." };
        }

        if (preview.HasNonBlockingLosses && !preview.LossesAcknowledged)
        {
            return new ImportExecutionResult { Success = false, ErrorMessage = "Подтвердите потери конвертации до commit." };
        }

        var toCommit = preview.Items.Where(ImportSourceIdentity.ShouldCommit).ToList();
        var skipped = preview.Items.Count - toCommit.Count;
        if (toCommit.Count == 0)
        {
            return new ImportExecutionResult
            {
                Success = true,
                ImportedNotesCount = 0,
                SkippedNotesCount = skipped,
                Summary = "Нечего импортировать: все кандидаты пропущены."
            };
        }

        return mutationCoordinator.ExecuteBulkMutation(() =>
            ExecuteImportInsideMutationBoundary(
                db,
                preview,
                toCommit,
                skipped,
                historyService,
                tagDetectionService,
                attachmentStorage,
                cancellationToken));
    }

    private ImportExecutionResult ExecuteImportInsideMutationBoundary(
        QuickNotesDbContext db,
        ImportPreviewResult preview,
        List<ImportItemPreview> toCommit,
        int skipped,
        INoteHistoryService? historyService,
        TagDetectionService? tagDetectionService,
        IAttachmentStorageService? attachmentStorage,
        CancellationToken cancellationToken)
    {
        historyService ??= new NoteHistoryService();
        string? stagingDir = null;
        var publishedFiles = new List<string>();
        var staleAttachmentNames = new List<string>();

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            InvokeTestHook(ImportTransactionPhase.Parse);

            if (!string.IsNullOrWhiteSpace(preview.PlanHash))
            {
                string current = ImportSourceIdentity.ComputePlanHash(preview.Items, preview.Diagnostics);
                if (!string.Equals(current, preview.PlanHash, StringComparison.Ordinal))
                {
                    return new ImportExecutionResult
                    {
                        Success = false,
                        ErrorMessage = "Источник изменился после предпросмотра. Повторите анализ."
                    };
                }

                foreach (var item in toCommit)
                {
                    if (string.IsNullOrWhiteSpace(item.SourcePath) || !File.Exists(item.SourcePath))
                    {
                        continue;
                    }

                    var info = new FileInfo(item.SourcePath);
                    if (info.Length != item.SourceLength || info.LastWriteTimeUtc.Ticks != item.SourceLastWriteUtcTicks)
                    {
                        return new ImportExecutionResult
                        {
                            Success = false,
                            ErrorMessage = "Файл-источник изменился после предпросмотра. Повторите анализ."
                        };
                    }
                }
            }

            InvokeTestHook(ImportTransactionPhase.Validation);

            stagingDir = Path.Combine(Path.GetTempPath(), "qn_import_stage_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(stagingDir);

            using var transaction = db.Database.BeginTransaction();
            try
            {
                int createdTagsCount = 0;
                int importedNotesCount = 0;
                int replacedNotesCount = 0;
                var allDbTags = db.Tags.Include(t => t.Synonyms).ToList();
                var tagByName = allDbTags.ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);

                EnsurePackageTags(db, preview, tagByName, allDbTags, ref createdTagsCount);

                foreach (var item in toCommit)
                {
                    foreach (var tagDto in item.Tags)
                    {
                        if (string.IsNullOrWhiteSpace(tagDto.TagName))
                        {
                            continue;
                        }

                        var cleanName = tagDto.TagName.Trim();
                        if (!tagByName.TryGetValue(cleanName, out var existingTag))
                        {
                            existingTag = new Tag { Name = cleanName };
                            db.Tags.Add(existingTag);
                            db.SaveChanges();
                            allDbTags.Add(existingTag);
                            tagByName[existingTag.Name] = existingTag;
                            createdTagsCount++;
                        }
                    }
                }

                InvokeTestHook(ImportTransactionPhase.Database);

                foreach (var item in toCommit)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Note? existingByFingerprint = null;
                    if (!string.IsNullOrWhiteSpace(item.SourceFingerprint))
                    {
                        existingByFingerprint = db.Notes.FirstOrDefault(n =>
                            n.DeletedAt == null && n.ImportSourceFingerprint == item.SourceFingerprint);
                    }

                    if (existingByFingerprint != null
                        && item.DuplicateAction != ImportDuplicateAction.Replace
                        && !(item.DuplicateAction == ImportDuplicateAction.ImportSeparate && item.DuplicateKind == ImportDuplicateKind.ExactSource))
                    {
                        skipped++;
                        continue;
                    }

                    Note note;
                    bool isReplace = item.DuplicateAction == ImportDuplicateAction.Replace && item.CanReplace && item.MatchingNoteId.HasValue;
                    if (isReplace)
                    {
                        int matchId = existingByFingerprint?.Id ?? item.MatchingNoteId!.Value;
                        note = db.Notes
                            .Include(n => n.NoteTags)
                            .Include(n => n.Attachments)
                            .First(n => n.Id == matchId);
                        if (note.IsProtected)
                        {
                            throw new InvalidOperationException("Нельзя заменить защищённую заметку.");
                        }

                        ApplyNoteFields(note, item, copyFingerprint: true);
                        ReconcileNoteTags(note, item, tagByName, allDbTags, tagDetectionService, replaceExactSet: true);
                        staleAttachmentNames.AddRange(note.Attachments.Select(a => a.StoredFileName));
                        foreach (var oldAtt in note.Attachments.ToList())
                        {
                            db.NoteAttachments.Remove(oldAtt);
                        }

                        note.Attachments.Clear();
                        replacedNotesCount++;
                    }
                    else
                    {
                        note = new Note();
                        ApplyNoteFields(
                            note,
                            item,
                            copyFingerprint: !(item.DuplicateAction == ImportDuplicateAction.ImportSeparate
                                && item.DuplicateKind == ImportDuplicateKind.ExactSource));
                        note.CreatedAt = item.CreatedAt;
                        db.Notes.Add(note);
                        importedNotesCount++;
                    }

                    db.SaveChanges();

                    if (!isReplace)
                    {
                        ReconcileNoteTags(note, item, tagByName, allDbTags, tagDetectionService, replaceExactSet: false);
                    }

                    db.SaveChanges();

                    if (item.PendingAttachments.Count > 0)
                    {
                        InvokeTestHook(ImportTransactionPhase.Attachment);
                        attachmentStorage ??= new AttachmentStorageService();
                        foreach (var pending in item.PendingAttachments)
                        {
                            if (!File.Exists(pending.SourceFullPath))
                            {
                                throw new InvalidOperationException("Вложение исчезло до commit.");
                            }

                            string staged = Path.Combine(stagingDir, Guid.NewGuid().ToString("N") + Path.GetExtension(pending.OriginalFileName));
                            File.Copy(pending.SourceFullPath, staged, overwrite: false);
                            var saved = attachmentStorage.SaveAttachment(staged, ImportLimits.DefaultMaxAttachmentBytes);
                            if (saved.WasCreated)
                            {
                                publishedFiles.Add(saved.FullPath);
                            }

                            db.NoteAttachments.Add(new NoteAttachment
                            {
                                NoteId = note.Id,
                                OriginalFileName = saved.OriginalFileName,
                                StoredFileName = saved.StoredFileName,
                                RelativePath = saved.RelativePath,
                                ContentType = saved.ContentType,
                                Size = saved.Size,
                                Sha256 = saved.Sha256,
                                CreatedAt = DateTime.UtcNow
                            });
                            if (!string.IsNullOrWhiteSpace(pending.MarkdownPlaceholder))
                            {
                                note.Text = note.Text.Replace(pending.MarkdownPlaceholder, saved.RelativePath.Replace('\\', '/'), StringComparison.Ordinal);
                            }
                        }

                        db.SaveChanges();
                    }
                    else if (isReplace)
                    {
                        InvokeTestHook(ImportTransactionPhase.Attachment);
                    }

                    InvokeTestHook(ImportTransactionPhase.History);
                    historyService.SaveSnapshot(db, note);
                }

                InvokeTestHook(ImportTransactionPhase.Publish);
                cancellationToken.ThrowIfCancellationRequested();
                transaction.Commit();

                if (attachmentStorage != null && staleAttachmentNames.Count > 0)
                {
                    try
                    {
                        attachmentStorage.CleanupUnreferencedFiles(db, staleAttachmentNames);
                    }
                    catch (Exception cleanupEx)
                    {
                        ErrorLogService.Write("NoteImportService.ExecuteImport.PostCommitAttachmentCleanup", cleanupEx);
                    }
                }

                var summary = $"Импортировано новых: {importedNotesCount}. Заменено: {replacedNotesCount}. Пропущено: {skipped}. Новых тегов: {createdTagsCount}.";
                return new ImportExecutionResult
                {
                    Success = true,
                    ImportedNotesCount = importedNotesCount,
                    CreatedTagsCount = createdTagsCount,
                    ReplacedNotesCount = replacedNotesCount,
                    SkippedNotesCount = skipped,
                    Summary = summary
                };
            }
            catch (OperationCanceledException)
            {
                try { transaction.Rollback(); } catch { /* already rolled back */ }
                throw;
            }
            catch (Exception ex)
            {
                try { transaction.Rollback(); } catch { /* already rolled back */ }
                RollbackPublishedFiles(publishedFiles);
                ErrorLogService.Write("NoteImportService.ExecuteImport", $"{ex.GetType().Name}: {ex.Message}");
                return new ImportExecutionResult
                {
                    Success = false,
                    ErrorMessage = $"Ошибка транзакции импорта: {ex.Message}"
                };
            }
        }
        catch (OperationCanceledException)
        {
            RollbackPublishedFiles(publishedFiles);
            return new ImportExecutionResult { Success = false, ErrorMessage = "Импорт отменён." };
        }
        catch (Exception ex)
        {
            RollbackPublishedFiles(publishedFiles);
            ErrorLogService.Write("NoteImportService.ExecuteImport", $"{ex.GetType().Name}: {ex.Message}");
            return new ImportExecutionResult { Success = false, ErrorMessage = $"Ошибка транзакции импорта: {ex.Message}" };
        }
        finally
        {
            CleanupStaging(stagingDir);
        }
    }

    private static void ApplyNoteFields(Note note, ImportItemPreview item, bool copyFingerprint)
    {
        note.Title = item.Title ?? string.Empty;
        note.Text = item.Text;
        note.UpdatedAt = item.UpdatedAt == default ? DateTime.UtcNow : item.UpdatedAt;
        note.IsPinned = item.IsPinned;
        note.IsFavorite = item.IsFavorite;
        note.IsInbox = item.IsInbox;
        note.SourceProcessName = item.SourceProcessName;
        note.SourceWindowTitle = item.SourceWindowTitle;
        note.SourceUrl = item.SourceUrl;
        note.CapturedAt = item.CapturedAt;
        note.ImportSourceFingerprint = copyFingerprint ? item.SourceFingerprint : null;
        note.ImportSourceRelativePath = item.SourceRelativePath;
    }

    private static void ReconcileNoteTags(
        Note note,
        ImportItemPreview item,
        Dictionary<string, Tag> tagByName,
        List<Tag> allDbTags,
        TagDetectionService? tagDetectionService,
        bool replaceExactSet)
    {
        var desired = new Dictionary<int, ExportNoteTagDto>();
        foreach (var tagDto in item.Tags)
        {
            if (string.IsNullOrWhiteSpace(tagDto.TagName))
            {
                continue;
            }

            var cleanName = tagDto.TagName.Trim();
            if (tagByName.TryGetValue(cleanName, out var tag))
            {
                desired[tag.Id] = tagDto;
            }
        }

        if (replaceExactSet)
        {
            foreach (var existing in note.NoteTags.ToList())
            {
                if (!desired.TryGetValue(existing.TagId, out var keep))
                {
                    note.NoteTags.Remove(existing);
                    continue;
                }

                existing.Origin = keep.Origin;
                existing.IsSuppressed = keep.IsSuppressed;
                desired.Remove(existing.TagId);
            }
        }

        var linkedTagIds = note.NoteTags.Select(nt => nt.TagId).ToHashSet();
        foreach (var pair in desired)
        {
            if (!linkedTagIds.Add(pair.Key))
            {
                continue;
            }

            note.NoteTags.Add(new NoteTag
            {
                NoteId = note.Id,
                TagId = pair.Key,
                Origin = pair.Value.Origin,
                IsSuppressed = pair.Value.IsSuppressed
            });
        }

        if (!replaceExactSet
            && linkedTagIds.Count == 0
            && item.Tags.All(t => string.IsNullOrWhiteSpace(t.TagName))
            && tagDetectionService != null)
        {
            var detected = tagDetectionService.DetectTags(note.Text, allDbTags);
            foreach (var d in detected)
            {
                if (linkedTagIds.Add(d.Id))
                {
                    note.NoteTags.Add(new NoteTag
                    {
                        NoteId = note.Id,
                        TagId = d.Id,
                        Origin = TagOrigin.Auto,
                        IsSuppressed = false
                    });
                }
            }
        }
    }

    private static void EnsurePackageTags(
        QuickNotesDbContext db,
        ImportPreviewResult preview,
        Dictionary<string, Tag> tagByName,
        List<Tag> allDbTags,
        ref int createdTagsCount)
    {
        if (preview.PackageTags == null || preview.PackageTags.Count == 0)
        {
            return;
        }

        foreach (var pTag in preview.PackageTags)
        {
            if (string.IsNullOrWhiteSpace(pTag.Name))
            {
                continue;
            }

            var cleanName = pTag.Name.Trim();
            if (!tagByName.TryGetValue(cleanName, out var existingTag))
            {
                existingTag = new Tag { Name = cleanName };
                db.Tags.Add(existingTag);
                db.SaveChanges();
                allDbTags.Add(existingTag);
                tagByName[existingTag.Name] = existingTag;
                createdTagsCount++;
            }

            var existingSynonyms = existingTag.Synonyms.Select(s => s.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var syn in pTag.Synonyms)
            {
                if (!string.IsNullOrWhiteSpace(syn) && !existingSynonyms.Contains(syn.Trim()))
                {
                    var ts = new TagSynonym { TagId = existingTag.Id, Value = syn.Trim() };
                    db.TagSynonyms.Add(ts);
                    existingTag.Synonyms.Add(ts);
                    existingSynonyms.Add(ts.Value);
                }
            }
        }

        db.SaveChanges();

        foreach (var pTag in preview.PackageTags)
        {
            if (string.IsNullOrWhiteSpace(pTag.Name))
            {
                continue;
            }

            var cleanName = pTag.Name.Trim();
            if (!tagByName.TryGetValue(cleanName, out var childTag))
            {
                continue;
            }

            Tag? parentTag = null;
            if (!string.IsNullOrWhiteSpace(pTag.ParentTagName) &&
                tagByName.TryGetValue(pTag.ParentTagName.Trim(), out var pByName))
            {
                parentTag = pByName;
            }
            else if (pTag.ParentTagId.HasValue)
            {
                var parentDto = preview.PackageTags.FirstOrDefault(t => t.Id == pTag.ParentTagId.Value);
                if (parentDto != null && !string.IsNullOrWhiteSpace(parentDto.Name) && tagByName.TryGetValue(parentDto.Name.Trim(), out var p))
                {
                    parentTag = p;
                }
            }

            if (parentTag != null && parentTag.Id != childTag.Id && childTag.ParentTagId != parentTag.Id)
            {
                if (!TagHierarchyService.WouldCreateCycle(childTag.Id, parentTag.Id, allDbTags))
                {
                    childTag.ParentTagId = parentTag.Id;
                }
            }
        }

        db.SaveChanges();
    }

    private static void InvokeTestHook(ImportTransactionPhase phase)
    {
        TestInjectFailure?.Invoke(phase);
    }

    private static void RollbackPublishedFiles(List<string> published)
    {
        foreach (var file in published)
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
                // best-effort
            }
        }
    }

    private static void CleanupStaging(string? stagingDir)
    {
        if (string.IsNullOrWhiteSpace(stagingDir) || !Directory.Exists(stagingDir))
        {
            return;
        }

        try
        {
            Directory.Delete(stagingDir, recursive: true);
        }
        catch
        {
            // best-effort
        }
    }
}
