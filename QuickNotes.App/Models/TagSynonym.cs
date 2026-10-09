namespace QuickNotes.App.Models;

public class TagSynonym
{
    public int Id { get; set; }

    public int TagId { get; set; }
    public Tag Tag { get; set; } = null!;

    public string Value { get; set; } = string.Empty;
}
