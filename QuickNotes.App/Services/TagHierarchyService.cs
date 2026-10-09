using System.Collections.Generic;
using System.Linq;
using QuickNotes.App.Models;

namespace QuickNotes.App.Services;

public static class TagHierarchyService
{
    /// <summary>
    /// Checks if setting targetParentId as parent for tagId would introduce a cycle.
    /// Cycle occurs if targetParentId is tagId itself or any descendant of tagId.
    /// </summary>
    public static bool WouldCreateCycle(int tagId, int? targetParentId, IEnumerable<Tag> allTags)
    {
        if (targetParentId == null)
            return false;

        if (targetParentId == tagId)
            return true;

        var tagsDict = allTags.ToDictionary(t => t.Id);

        int? current = targetParentId;
        var visited = new HashSet<int>();

        while (current != null)
        {
            if (current == tagId)
                return true;

            if (!visited.Add(current.Value))
                return true; // Detected loop in existing structure

            if (tagsDict.TryGetValue(current.Value, out var parentTag))
            {
                current = parentTag.ParentTagId;
            }
            else
            {
                break;
            }
        }

        return false;
    }

    /// <summary>
    /// Returns the tag ID and all its recursive descendant tag IDs.
    /// </summary>
    public static HashSet<int> GetTagAndDescendantIds(int tagId, IEnumerable<Tag> allTags)
    {
        var result = new HashSet<int> { tagId };
        var childrenLookup = allTags
            .Where(t => t.ParentTagId != null)
            .ToLookup(t => t.ParentTagId!.Value, t => t.Id);

        var queue = new Queue<int>();
        queue.Enqueue(tagId);

        while (queue.Count > 0)
        {
            int current = queue.Dequeue();
            foreach (var childId in childrenLookup[current])
            {
                if (result.Add(childId))
                {
                    queue.Enqueue(childId);
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Returns the sequence of tag names from the root down to the given tag.
    /// Safely terminates on loops/cycles in corrupted data.
    /// </summary>
    public static List<string> GetBreadcrumbPath(int tagId, IEnumerable<Tag> allTags)
    {
        var tagsDict = new Dictionary<int, Tag>();
        foreach (var t in allTags)
        {
            tagsDict.TryAdd(t.Id, t);
        }

        var names = new List<string>();
        var visited = new HashSet<int>();

        int? currentId = tagId;
        while (currentId.HasValue && visited.Add(currentId.Value))
        {
            if (tagsDict.TryGetValue(currentId.Value, out var tag))
            {
                names.Add(tag.Name);
                currentId = tag.ParentTagId;
            }
            else
            {
                break;
            }
        }

        names.Reverse();
        return names;
    }

    /// <summary>
    /// Returns the full path string of the tag (e.g. "IT / Database / Oracle").
    /// </summary>
    public static string GetFullPath(int tagId, IEnumerable<Tag> allTags, string separator = " / ")
    {
        var parts = GetBreadcrumbPath(tagId, allTags);
        return string.Join(separator, parts);
    }

    /// <summary>
    /// Checks if a tag can be reparented to targetParentId.
    /// Rejects self-parenting, descendant cycles, already-parent (no-op), and non-existent targets.
    /// </summary>
    public static bool CanReparent(int sourceTagId, int? targetParentId, IEnumerable<Tag> allTags)
    {
        var tagsList = (allTags as IList<Tag>) ?? allTags.ToList();
        var sourceTag = tagsList.FirstOrDefault(t => t.Id == sourceTagId);
        if (sourceTag == null)
            return false;

        // Cannot reparent to self
        if (targetParentId.HasValue && targetParentId.Value == sourceTagId)
            return false;

        // Cannot reparent to already current parent (no-op)
        if (sourceTag.ParentTagId == targetParentId)
            return false;

        // Target parent must exist if specified
        if (targetParentId.HasValue && !tagsList.Any(t => t.Id == targetParentId.Value))
            return false;

        // Cannot create cycles (target is a descendant of source)
        if (WouldCreateCycle(sourceTagId, targetParentId, tagsList))
            return false;

        return true;
    }

    /// <summary>
    /// Calculates the count of active notes for each tag, including notes associated with
    /// any recursive descendants. Each note is counted at most once per tag.
    /// </summary>
    public static Dictionary<int, int> CalculateTagNoteCounts(
        IEnumerable<Tag> allTags,
        IEnumerable<(int TagId, int NoteId)> activeTagNotePairs)
    {
        var tagsList = (allTags as IList<Tag>) ?? allTags.ToList();

        // Group active note IDs by tag ID
        var tagToNotes = new Dictionary<int, HashSet<int>>();
        foreach (var (tagId, noteId) in activeTagNotePairs)
        {
            if (!tagToNotes.TryGetValue(tagId, out var notes))
            {
                notes = new HashSet<int>();
                tagToNotes[tagId] = notes;
            }
            notes.Add(noteId);
        }

        var result = new Dictionary<int, int>();
        foreach (var tag in tagsList)
        {
            var subtreeIds = GetTagAndDescendantIds(tag.Id, tagsList);
            var distinctNotes = new HashSet<int>();
            foreach (var subId in subtreeIds)
            {
                if (tagToNotes.TryGetValue(subId, out var notes))
                {
                    distinctNotes.UnionWith(notes);
                }
            }
            result[tag.Id] = distinctNotes.Count;
        }

        return result;
    }

    /// <summary>
    /// Convenience overload taking NoteTag objects.
    /// Automatically excludes suppressed tags and deleted notes.
    /// </summary>
    public static Dictionary<int, int> CalculateTagNoteCounts(
        IEnumerable<Tag> allTags,
        IEnumerable<NoteTag> noteTags)
    {
        var activePairs = noteTags
            .Where(nt => !nt.IsSuppressed && (nt.Note == null || nt.Note.DeletedAt == null))
            .Select(nt => (nt.TagId, nt.NoteId));

        return CalculateTagNoteCounts(allTags, activePairs);
    }
}
