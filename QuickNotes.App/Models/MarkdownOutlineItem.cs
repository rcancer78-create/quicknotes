using System.Windows;

namespace QuickNotes.App.Models;

/// <summary>
/// Represents an entry in the derived document outline (table of contents) computed from markdown headings.
/// </summary>
public sealed class MarkdownOutlineItem
{
    public string Title { get; init; } = string.Empty;
    public int Level { get; init; } = 1;
    public string AnchorId { get; init; } = string.Empty;
    public int LineIndex { get; init; }
    public int CharacterIndex { get; init; }

    public Thickness IndentMargin => new Thickness(Math.Max(0, (Level - 1) * 12), 2, 4, 2);
    public string LevelBadge => $"H{Level}";

    public override string ToString() => $"H{Level}: {Title} (#{AnchorId})";
}
