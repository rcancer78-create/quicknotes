using System;
using System.Collections.Generic;

namespace QuickNotes.App.Models;

public class TagSuggestionItem
{
    /// <summary>
    /// Suggested tag name / term in normalized display form.
    /// </summary>
    public string Word { get; init; } = string.Empty;

    /// <summary>
    /// Number of distinct active notes containing this word.
    /// </summary>
    public int NotesCount { get; init; }

    /// <summary>
    /// Short preview snippet from a note demonstrating word usage.
    /// </summary>
    public string Preview { get; init; } = string.Empty;

    /// <summary>
    /// IDs of distinct active notes where this word was detected.
    /// </summary>
    public IReadOnlyList<int> NoteIds { get; init; } = Array.Empty<int>();
}
