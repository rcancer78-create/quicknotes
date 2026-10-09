namespace QuickNotes.App.Models;

public enum ChipType
{
    Tag,
    Created,
    Updated,
    Untagged,
    Source,
    Url
}

public sealed class SearchFilterChipModel
{
    public ChipType Type { get; set; }
    public string DisplayText { get; set; } = string.Empty;
    public string RawTokenText { get; set; } = string.Empty;
    public int StartIndex { get; set; }
    public int Length { get; set; }
    public bool IsNegated { get; set; }
    public SearchCondition? Condition { get; set; }
}
