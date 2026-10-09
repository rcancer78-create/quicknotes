namespace QuickNotes.App.Models;

public class NoteTag
{
    public int NoteId { get; set; }
    public Note Note { get; set; } = null!;

    public int TagId { get; set; }
    public Tag Tag { get; set; } = null!;

    public TagOrigin Origin { get; set; } = TagOrigin.Auto;

    /// <summary>
    /// If true, the user removed this tag when it was auto-detected.
    /// Subsequent re-scans will not automatically re-add this tag.
    /// </summary>
    public bool IsSuppressed { get; set; }
}
