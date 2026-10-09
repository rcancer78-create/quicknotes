using System;
using System.Collections.Generic;

namespace QuickNotes.App.Models;

public class NoteHistoryItemDto
{
    public int Id { get; set; }
    public int NoteId { get; set; }
    public DateTime CreatedAt { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public List<NoteRevisionTagSnapshot> Tags { get; set; } = new();
    public string DiffSummary { get; set; } = string.Empty;
    public bool IsCorrupted { get; set; }

    /// <summary>True when the revision body is encrypted (belongs to a protected note).</summary>
    public bool IsProtectedRevision { get; set; }
}
