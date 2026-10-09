using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using QuickNotes.App.Data;
using QuickNotes.App.Models;

namespace QuickNotes.App.Services;

public class TagMergeService
{
    /// <summary>
    /// Returns tags that can serve as a target for merging the given source tag.
    /// Excludes the source tag itself and all its recursive descendants to prevent cycles.
    /// </summary>
    public List<Tag> GetAvailableTargets(int sourceTagId, IEnumerable<Tag> allTags)
    {
        var tagsList = (allTags as IList<Tag>) ?? allTags.ToList();
        var invalidIds = TagHierarchyService.GetTagAndDescendantIds(sourceTagId, tagsList);

        return tagsList
            .Where(t => !invalidIds.Contains(t.Id))
            .OrderBy(t => t.Name)
            .ToList();
    }

    /// <summary>
    /// Checks whether sourceTagId can be merged into targetTagId.
    /// Rejects merging to self, descendants (cycles), or non-existent tags.
    /// </summary>
    public bool CanMerge(int sourceTagId, int targetTagId, IEnumerable<Tag> allTags, out string? reason)
    {
        var tagsList = (allTags as IList<Tag>) ?? allTags.ToList();

        if (sourceTagId == targetTagId)
        {
            reason = "Нельзя объединить тег с самим собой.";
            return false;
        }

        if (!tagsList.Any(t => t.Id == sourceTagId))
        {
            reason = $"Исходный тег #{sourceTagId} не найден.";
            return false;
        }

        if (!tagsList.Any(t => t.Id == targetTagId))
        {
            reason = $"Целевой тег #{targetTagId} не найден.";
            return false;
        }

        var invalidIds = TagHierarchyService.GetTagAndDescendantIds(sourceTagId, tagsList);
        if (invalidIds.Contains(targetTagId))
        {
            reason = "Целевой тег не может быть потомком исходного тега (это приведёт к циклу).";
            return false;
        }

        reason = null;
        return true;
    }

    /// <summary>
    /// Calculates preview statistics for merging sourceTagId into targetTagId.
    /// </summary>
    public TagMergePreviewResult CalculatePreview(
        QuickNotesDbContext db,
        int sourceTagId,
        int targetTagId,
        IEnumerable<Tag>? allTags = null)
    {
        var tagsList = (allTags as IList<Tag>) ?? (allTags?.ToList() ?? db.Tags.Include(t => t.Synonyms).ToList());

        var sourceTag = tagsList.FirstOrDefault(t => t.Id == sourceTagId) ?? db.Tags.Find(sourceTagId);
        var targetTag = tagsList.FirstOrDefault(t => t.Id == targetTagId) ?? db.Tags.Find(targetTagId);

        var result = new TagMergePreviewResult
        {
            SourceTagId = sourceTagId,
            SourceTagName = sourceTag?.Name ?? $"#{sourceTagId}",
            TargetTagId = targetTagId,
            TargetTagName = targetTag?.Name ?? $"#{targetTagId}"
        };

        if (!CanMerge(sourceTagId, targetTagId, tagsList, out var blockReason))
        {
            result.CanMerge = false;
            result.StatusExplanation = blockReason ?? "Недопустимое объединение.";
            return result;
        }

        // 1. Affected active notes (not in trash, unsuppressed source tag link)
        result.AffectedActiveNotesCount = db.NoteTags
            .Where(nt => nt.TagId == sourceTagId && !nt.IsSuppressed && nt.Note.DeletedAt == null)
            .Select(nt => nt.NoteId)
            .Distinct()
            .Count();

        // 2. Already existing duplicate NoteTag links
        var sourceNoteIds = db.NoteTags
            .Where(nt => nt.TagId == sourceTagId)
            .Select(nt => nt.NoteId)
            .Distinct()
            .ToList();

        result.DuplicateNoteTagsCount = db.NoteTags
            .Where(nt => nt.TagId == targetTagId && sourceNoteIds.Contains(nt.NoteId))
            .Select(nt => nt.NoteId)
            .Distinct()
            .Count();

        // 3. Synonyms
        var sourceSynonyms = db.TagSynonyms.Where(s => s.TagId == sourceTagId).ToList();
        var targetSynonyms = db.TagSynonyms.Where(s => s.TagId == targetTagId).ToList();

        result.SourceSynonymsCount = sourceSynonyms.Count;

        var existingTargetValues = new HashSet<string>(
            targetSynonyms.Select(s => s.Value.Trim()),
            StringComparer.OrdinalIgnoreCase);

        if (targetTag != null)
        {
            existingTargetValues.Add(targetTag.Name.Trim());
        }

        int uniqueCount = 0;
        var seenFromSource = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var syn in sourceSynonyms)
        {
            var val = syn.Value.Trim();
            if (!string.IsNullOrEmpty(val) && !existingTargetValues.Contains(val) && seenFromSource.Add(val))
            {
                uniqueCount++;
            }
        }
        result.UniqueSynonymsCount = uniqueCount;

        // 4. Child tags of source tag
        result.ChildTagsCount = tagsList.Count(t => t.ParentTagId == sourceTagId);

        // 5. Status explanation
        result.CanMerge = true;
        if (result.ChildTagsCount > 0)
        {
            result.StatusExplanation = $"Все дочерние теги ({result.ChildTagsCount}) будут безопасно перемещены под целевой тег «{result.TargetTagName}».";
        }
        else
        {
            result.StatusExplanation = "Все связи и синонимы будут безопасно объединены с целевым тегом.";
        }

        return result;
    }

    /// <summary>
    /// Executes the tag merge in a single database transaction.
    /// Deduplicates NoteTags by (NoteId, TargetTagId), preserving active and manual states.
    /// Transfers unique synonyms case-insensitively.
    /// Reparents source tag children to target tag.
    /// Deletes the source tag only after successful transfer.
    /// Rolls back completely on failure.
    /// </summary>
    public TagMergeResult MergeTags(
        QuickNotesDbContext db,
        int sourceTagId,
        int targetTagId,
        IEnumerable<Tag>? allTags = null)
    {
        var tagsList = (allTags as IList<Tag>) ?? (allTags?.ToList() ?? db.Tags.Include(t => t.Synonyms).ToList());

        if (!CanMerge(sourceTagId, targetTagId, tagsList, out var reason))
        {
            throw new InvalidOperationException(reason ?? "Невозможно объединить теги.");
        }

        var sourceTag = db.Tags
            .Include(t => t.Synonyms)
            .Include(t => t.Children)
            .FirstOrDefault(t => t.Id == sourceTagId);

        if (sourceTag == null)
        {
            throw new InvalidOperationException($"Исходный тег #{sourceTagId} не найден.");
        }

        var targetTag = db.Tags
            .Include(t => t.Synonyms)
            .FirstOrDefault(t => t.Id == targetTagId);

        if (targetTag == null)
        {
            throw new InvalidOperationException($"Целевой тег #{targetTagId} не найден.");
        }

        using var transaction = db.Database.BeginTransaction();
        try
        {
            // 1. Reparent children of sourceTag to targetTag
            var childTags = db.Tags.Where(t => t.ParentTagId == sourceTagId).ToList();
            int reparentedChildrenCount = childTags.Count;
            foreach (var child in childTags)
            {
                child.ParentTagId = targetTagId;
            }
            db.SaveChanges();

            // 2. Transfer synonyms with case-insensitive deduplication
            var targetSynonymValues = new HashSet<string>(
                targetTag.Synonyms.Select(s => s.Value.Trim()),
                StringComparer.OrdinalIgnoreCase);
            targetSynonymValues.Add(targetTag.Name.Trim());

            int transferredSynonymsCount = 0;
            var seenSourceSynonyms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var syn in sourceTag.Synonyms.ToList())
            {
                var val = syn.Value.Trim();
                if (!string.IsNullOrEmpty(val) && !targetSynonymValues.Contains(val) && seenSourceSynonyms.Add(val))
                {
                    var newSyn = new TagSynonym
                    {
                        TagId = targetTagId,
                        Value = val
                    };
                    db.TagSynonyms.Add(newSyn);
                    targetTag.Synonyms.Add(newSyn);
                    transferredSynonymsCount++;
                }
            }

            db.TagSynonyms.RemoveRange(sourceTag.Synonyms);
            db.SaveChanges();

            // 3. Migrate NoteTags with deduplication
            var sourceLinks = db.NoteTags.Where(nt => nt.TagId == sourceTagId).ToList();
            var sourceNoteIds = sourceLinks.Select(sl => sl.NoteId).ToList();
            var targetLinks = db.NoteTags
                .Where(nt => nt.TagId == targetTagId && sourceNoteIds.Contains(nt.NoteId))
                .ToDictionary(nt => nt.NoteId);

            int duplicatesResolvedCount = 0;
            int notesMigratedCount = 0;

            foreach (var sLink in sourceLinks)
            {
                if (targetLinks.TryGetValue(sLink.NoteId, out var tLink))
                {
                    duplicatesResolvedCount++;
                    // Active link priority: if either is active (not suppressed), result is active
                    tLink.IsSuppressed = tLink.IsSuppressed && sLink.IsSuppressed;
                    // Manual origin priority: if either is manual, result is manual
                    if (sLink.Origin == TagOrigin.Manual)
                    {
                        tLink.Origin = TagOrigin.Manual;
                    }
                    db.NoteTags.Remove(sLink);
                }
                else
                {
                    notesMigratedCount++;
                    db.NoteTags.Remove(sLink);
                    db.NoteTags.Add(new NoteTag
                    {
                        NoteId = sLink.NoteId,
                        TagId = targetTagId,
                        Origin = sLink.Origin,
                        IsSuppressed = sLink.IsSuppressed
                    });
                }
            }
            db.SaveChanges();

            // 4. Migrate template tags from sourceTag to targetTag
            var sourceTemplateTags = db.NoteTemplateTags.Where(tt => tt.TagId == sourceTagId).ToList();
            if (sourceTemplateTags.Count > 0)
            {
                var targetTemplateTags = db.NoteTemplateTags
                    .Where(tt => tt.TagId == targetTagId)
                    .Select(tt => tt.TemplateId)
                    .ToHashSet();

                foreach (var stt in sourceTemplateTags)
                {
                    db.NoteTemplateTags.Remove(stt);
                    if (!targetTemplateTags.Contains(stt.TemplateId))
                    {
                        db.NoteTemplateTags.Add(new NoteTemplateTag
                        {
                            TemplateId = stt.TemplateId,
                            TagId = targetTagId
                        });
                        targetTemplateTags.Add(stt.TemplateId);
                    }
                }
            }

            // 5. Delete source tag
            db.Tags.Remove(sourceTag);
            db.SaveChanges();

            // 6. Commit transaction
            transaction.Commit();

            return new TagMergeResult
            {
                Success = true,
                NotesMigratedCount = notesMigratedCount,
                DuplicatesResolvedCount = duplicatesResolvedCount,
                SynonymsTransferredCount = transferredSynonymsCount,
                ChildTagsReparentedCount = reparentedChildrenCount
            };
        }
        catch (Exception ex)
        {
            ErrorLogService.Write("TagMergeService.MergeTags", ex);
            try
            {
                transaction.Rollback();
            }
            catch (Exception rollbackEx)
            {
                // Safe diagnostic logging for rollback error if transaction already rolled back
                System.Diagnostics.Debug.WriteLine($"TagMergeService rollback failed: {rollbackEx.Message}");
            }
            throw;
        }
    }
}
