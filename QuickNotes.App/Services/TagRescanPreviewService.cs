using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using QuickNotes.App.Data;
using QuickNotes.App.Models;

namespace QuickNotes.App.Services;

public class TagRescanPreviewService
{
    private readonly TagDetectionService _tagDetectionService;
    private readonly TagRuleService? _tagRuleService;

    public TagRescanPreviewService(
        TagDetectionService? tagDetectionService = null,
        TagRuleService? tagRuleService = null)
    {
        _tagDetectionService = tagDetectionService ?? new TagDetectionService(tagRuleService);
        _tagRuleService = tagRuleService;
    }

    /// <summary>
    /// Calculates preview metrics for bulk tag rescanning across the provided notes against available tags.
    /// Excludes deleted notes (DeletedAt != null).
    /// Preserves Manual tags (neither added as Auto nor removed).
    /// Respects Suppressed tags (neither re-added nor removed).
    /// Evaluates additions for newly detected tags and removals for undetected active auto tags.
    /// Does NOT mutate the provided notes or their NoteTags collection.
    /// </summary>
    public TagRescanPreviewResult CalculatePreview(
        IEnumerable<Note> notes,
        IEnumerable<Tag> availableTags,
        IReadOnlyDictionary<int, TagRule>? rules = null)
    {
        if (notes == null)
            return new TagRescanPreviewResult();

        var availableList = (availableTags as IList<Tag>) ?? availableTags?.ToList() ?? new List<Tag>();
        var tagMap = availableList.ToDictionary(t => t.Id);

        // Exclude deleted notes
        var nonDeletedNotes = notes.Where(n => n.DeletedAt == null).ToList();
        var changes = new List<TagRescanNoteChange>();

        int totalAdded = 0;
        int totalRemoved = 0;

        var effectiveRules = rules ?? _tagRuleService?.GetAllRules();

        foreach (var note in nonDeletedNotes)
        {
            var detectedTags = _tagDetectionService.DetectTags(note.Text ?? string.Empty, availableList, effectiveRules);
            var detectedTagIds = detectedTags.Select(t => t.Id).ToHashSet();

            // 1. Auto tags to remove (active, Auto, and no longer detected)
            var toRemove = note.NoteTags
                .Where(nt => nt.Origin == TagOrigin.Auto && !nt.IsSuppressed && !detectedTagIds.Contains(nt.TagId))
                .Select(nt => nt.Tag?.Name ?? (tagMap.TryGetValue(nt.TagId, out var tag) ? tag.Name : $"#{nt.TagId}"))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            // 2. Auto tags to add (detected, not present in note's NoteTags at all)
            var existingTagIds = note.NoteTags.Select(nt => nt.TagId).ToHashSet();
            var toAdd = detectedTags
                .Where(dt => !existingTagIds.Contains(dt.Id))
                .Select(dt => dt.Name)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            if (toAdd.Count > 0 || toRemove.Count > 0)
            {
                changes.Add(new TagRescanNoteChange
                {
                    NoteId = note.Id,
                    Title = TagRescanNoteChange.FormatNoteTitle(note),
                    AddedTagNames = toAdd,
                    RemovedTagNames = toRemove
                });

                totalAdded += toAdd.Count;
                totalRemoved += toRemove.Count;
            }
        }

        return new TagRescanPreviewResult
        {
            TotalNotesScanned = nonDeletedNotes.Count,
            TotalNotesWithChanges = changes.Count,
            TotalAddedTags = totalAdded,
            TotalRemovedTags = totalRemoved,
            NoteChanges = changes
        };
    }

    /// <summary>
    /// Queries non-deleted notes with their NoteTags from the database and calculates the rescan preview.
    /// </summary>
    public TagRescanPreviewResult CalculatePreview(
        QuickNotesDbContext db,
        IEnumerable<Tag> availableTags,
        IReadOnlyDictionary<int, TagRule>? rules = null)
    {
        var nonDeletedNotes = db.Notes
            .Where(n => n.DeletedAt == null)
            .Include(n => n.NoteTags)
            .ThenInclude(nt => nt.Tag)
            .ToList();

        return CalculatePreview(nonDeletedNotes, availableTags, rules);
    }
}
