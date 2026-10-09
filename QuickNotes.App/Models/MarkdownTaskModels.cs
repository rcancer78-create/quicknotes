using System;

namespace QuickNotes.App.Models;

/// <summary>
/// Derived open-checkbox locator. Produced on read from <see cref="Note.Text"/>;
/// never persisted as a Task row or as an absolute character range.
/// </summary>
public sealed class MarkdownTaskLocator
{
    public int NoteId { get; init; }
    public Guid NoteSyncId { get; init; }
    public int LineNumber { get; init; }
    public string TaskText { get; init; } = string.Empty;
    public DateOnly? DueDate { get; init; }
    public string Fingerprint { get; init; } = string.Empty;
    public int OccurrenceIndex { get; init; }
    public int SameFingerprintCount { get; init; }
    /// <summary>
    /// Non-persistent SHA-256 of the full <see cref="Note.Text"/> at index time.
    /// Digest bytes only; never logged or shown in UI.
    /// </summary>
    public byte[] NoteSnapshotProof { get; init; } = Array.Empty<byte>();
    public string NoteTitle { get; init; } = string.Empty;
    public DateTime NoteUpdatedAt { get; init; }
    public int LineStartCharIndex { get; init; }
    public int LineLength { get; init; }
    public string Bullet { get; init; } = "-";
}

public sealed class TaskNavigationResult
{
    public bool IsFallback { get; init; }
    public int LineNumber { get; init; }
    public int SelectionStart { get; init; }
    public int SelectionLength { get; init; }

    public static TaskNavigationResult Fallback { get; } = new()
    {
        IsFallback = true,
        LineNumber = 1,
        SelectionStart = 0,
        SelectionLength = 0
    };

    public static TaskNavigationResult Hit(MarkdownTaskLocator locator, int textLength)
    {
        int start = Math.Clamp(locator.LineStartCharIndex, 0, Math.Max(0, textLength));
        int maxLen = Math.Max(0, textLength - start);
        int length = Math.Clamp(locator.LineLength, 0, maxLen);
        return new TaskNavigationResult
        {
            IsFallback = false,
            LineNumber = locator.LineNumber,
            SelectionStart = start,
            SelectionLength = length
        };
    }
}

public sealed class TaskIndexPage
{
    public IReadOnlyList<MarkdownTaskLocator> Items { get; init; } = Array.Empty<MarkdownTaskLocator>();
    public int TotalCount { get; init; }
    public bool HasMore { get; init; }
}
