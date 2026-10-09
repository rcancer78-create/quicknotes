namespace QuickNotes.App.Models;

public class TagMergeResult
{
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }

    public int NotesMigratedCount { get; set; }
    public int DuplicatesResolvedCount { get; set; }
    public int SynonymsTransferredCount { get; set; }
    public int ChildTagsReparentedCount { get; set; }
}
