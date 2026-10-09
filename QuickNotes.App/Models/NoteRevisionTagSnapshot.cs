namespace QuickNotes.App.Models;

public class NoteRevisionTagSnapshot
{
    public int TagId { get; set; }
    public string TagName { get; set; } = string.Empty;
    public TagOrigin Origin { get; set; } = TagOrigin.Auto;
    public bool IsSuppressed { get; set; }
}
