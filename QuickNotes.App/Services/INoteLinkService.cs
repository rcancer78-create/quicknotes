using System;
using System.Collections.Generic;
using QuickNotes.App.Data;
using QuickNotes.App.Models;

namespace QuickNotes.App.Services;

public readonly record struct NoteLinkRef(int? LocalId, Guid? SyncId, string RawToken);

public interface INoteLinkService
{
    IReadOnlyList<int> ExtractLinkedNoteIds(string? text);

    IReadOnlyList<NoteLinkRef> ExtractLinkRefs(string? text);

    string FormatLink(int noteId);

    string FormatLink(Guid syncId);

    string FormatLink(Note note);

    string RewriteLegacyLinks(string? text, IReadOnlyDictionary<int, Guid> idToSyncId);

    IReadOnlyList<NoteLinkItemDto> GetOutgoingLinks(QuickNotesDbContext db, string? text);

    IncomingLinksResult GetIncomingLinks(QuickNotesDbContext db, int? currentNoteId);

    IncomingLinksResult GetIncomingLinksResult(QuickNotesDbContext db, int? currentNoteId);
}
