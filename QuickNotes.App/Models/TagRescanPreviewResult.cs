using System;
using System.Collections.Generic;

namespace QuickNotes.App.Models;

public class TagRescanPreviewResult
{
    /// <summary>
    /// Total number of active (non-deleted) notes scanned.
    /// </summary>
    public int TotalNotesScanned { get; init; }

    /// <summary>
    /// Total number of notes that have at least one tag addition or removal.
    /// </summary>
    public int TotalNotesWithChanges { get; init; }

    /// <summary>
    /// Total number of auto tags that would be added.
    /// </summary>
    public int TotalAddedTags { get; init; }

    /// <summary>
    /// Total number of auto tags that would be removed.
    /// </summary>
    public int TotalRemovedTags { get; init; }

    /// <summary>
    /// Per-note change details for notes that have changes.
    /// </summary>
    public IReadOnlyList<TagRescanNoteChange> NoteChanges { get; init; } = Array.Empty<TagRescanNoteChange>();

    /// <summary>
    /// True if there is at least one tag addition or removal.
    /// </summary>
    public bool HasChanges => TotalAddedTags > 0 || TotalRemovedTags > 0;
}
