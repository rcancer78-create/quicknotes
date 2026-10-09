namespace QuickNotes.App.Models;

public enum TagMatchSource
{
    TagName,
    Synonym
}

public class TagDetectionMatch
{
    public Tag Tag { get; set; } = null!;
    public TagMatchSource Source { get; set; } = TagMatchSource.TagName;
    public bool MatchedByTagName => Source == TagMatchSource.TagName;
    public TagSynonym? MatchedSynonym { get; set; }
    public string MatchedTerm { get; set; } = string.Empty;

    public TagRule? AppliedRule { get; set; }
    public string? RuleDescription { get; set; }

    public string BaseReason => MatchedByTagName
        ? "по имени тега"
        : (!string.IsNullOrWhiteSpace(MatchedTerm) ? $"по синониму «{MatchedTerm}»" : "по синониму");

    public string Reason => string.IsNullOrWhiteSpace(RuleDescription)
        ? BaseReason
        : $"{BaseReason} (правило: {RuleDescription})";
}
