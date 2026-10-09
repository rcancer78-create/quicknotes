namespace QuickNotes.App.Models;

public class NoteTemplateTag
{
    public int TemplateId { get; set; }
    public NoteTemplate Template { get; set; } = null!;

    public int TagId { get; set; }
    public Tag Tag { get; set; } = null!;
}
