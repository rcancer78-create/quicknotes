using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace QuickNotes.App.Models;

public class TagRescanNoteChange
{
    public int NoteId { get; init; }
    public string Title { get; init; } = string.Empty;
    public IReadOnlyList<string> AddedTagNames { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> RemovedTagNames { get; init; } = Array.Empty<string>();

    public int AddedCount => AddedTagNames.Count;
    public int RemovedCount => RemovedTagNames.Count;
    public bool HasChanges => AddedTagNames.Count > 0 || RemovedTagNames.Count > 0;

    public bool HasAddedTags => AddedTagNames.Count > 0;
    public bool HasRemovedTags => RemovedTagNames.Count > 0;

    public string AddedTagsDisplay => AddedTagNames.Count > 0 ? string.Join(", ", AddedTagNames) : string.Empty;
    public string RemovedTagsDisplay => RemovedTagNames.Count > 0 ? string.Join(", ", RemovedTagNames) : string.Empty;

    public Visibility AddedTagsVisibility => HasAddedTags ? Visibility.Visible : Visibility.Collapsed;
    public Visibility RemovedTagsVisibility => HasRemovedTags ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// Formats a compact title or text preview for a note.
    /// </summary>
    public static string FormatNoteTitle(Note note, int maxLength = 50)
    {
        if (note == null)
            return string.Empty;

        return Helpers.NoteTitleHelper.GetDisplayTitle(note.Title, note.Text, maxLength);
    }
}
