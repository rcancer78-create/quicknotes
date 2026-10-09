using System;
using System.Collections;
using System.Collections.Generic;

namespace QuickNotes.App.Models;

public record NoteLinkItemDto(
    int TargetNoteId,
    string Title,
    bool IsAvailable)
{
    public string IdText => $"#{TargetNoteId}";
}

public record IncomingLinksResult(
    IReadOnlyList<NoteLinkItemDto> Items,
    bool IsTruncated) : IReadOnlyList<NoteLinkItemDto>
{
    public static IncomingLinksResult Empty { get; } = new(Array.Empty<NoteLinkItemDto>(), false);

    public IReadOnlyList<NoteLinkItemDto> Links => Items;

    public bool IsLimited => IsTruncated;

    public int Count => Items.Count;

    public NoteLinkItemDto this[int index] => Items[index];

    public IEnumerator<NoteLinkItemDto> GetEnumerator() => Items.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
