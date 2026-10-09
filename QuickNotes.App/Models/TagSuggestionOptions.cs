namespace QuickNotes.App.Models;

public class TagSuggestionOptions
{
    /// <summary>
    /// Minimum number of distinct active notes where a term must appear to be suggested.
    /// Default is 2.
    /// </summary>
    public int MinNotesCount { get; set; } = 2;

    /// <summary>
    /// Maximum number of suggested tags to return.
    /// Default is 50.
    /// </summary>
    public int MaxSuggestions { get; set; } = 50;
}
