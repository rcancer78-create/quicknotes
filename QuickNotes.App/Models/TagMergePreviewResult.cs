namespace QuickNotes.App.Models;

public class TagMergePreviewResult
{
    public int SourceTagId { get; set; }
    public string SourceTagName { get; set; } = string.Empty;

    public int TargetTagId { get; set; }
    public string TargetTagName { get; set; } = string.Empty;

    /// <summary>
    /// Number of active notes (not deleted) that currently have the source tag.
    /// </summary>
    public int AffectedActiveNotesCount { get; set; }
    public int ActiveNotesCount => AffectedActiveNotesCount;

    /// <summary>
    /// Number of notes that already have both SourceTag and TargetTag NoteTags.
    /// </summary>
    public int DuplicateNoteTagsCount { get; set; }
    public int DuplicateLinksCount => DuplicateNoteTagsCount;

    /// <summary>
    /// Total number of synonyms defined on the source tag.
    /// </summary>
    public int SourceSynonymsCount { get; set; }
    public int SynonymsCount => SourceSynonymsCount;

    /// <summary>
    /// Number of unique synonyms from source tag that will be transferred to target tag (case-insensitive deduplication).
    /// </summary>
    public int UniqueSynonymsCount { get; set; }
    public int SynonymsToTransferCount => UniqueSynonymsCount;

    /// <summary>
    /// Number of direct child tags that will be reparented under the target tag.
    /// </summary>
    public int ChildTagsCount { get; set; }

    /// <summary>
    /// Whether the merge operation can be safely confirmed.
    /// </summary>
    public bool CanMerge { get; set; } = true;

    /// <summary>
    /// Explanation of status, warnings, or why merge is blocked.
    /// </summary>
    public string StatusExplanation { get; set; } = string.Empty;
}
