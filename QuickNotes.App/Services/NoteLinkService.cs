using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using QuickNotes.App.Data;
using QuickNotes.App.Models;

namespace QuickNotes.App.Services;

public class NoteLinkService : INoteLinkService
{
    public const string UnavailableNoteTitle = "Заметка недоступна";
    public const string SyncLinkPrefix = "qn:";

    private static readonly Regex LinkPattern = new(
        @"(?<!\[)\[\[\s*([^\r\n\[\]]+?)\s*\]\](?!\])",
        RegexOptions.Compiled);

    public IReadOnlyList<int> ExtractLinkedNoteIds(string? text)
    {
        var refs = ExtractLinkRefs(text);
        var result = new List<int>();
        var seen = new HashSet<int>();
        foreach (var link in refs)
        {
            if (link.LocalId.HasValue && seen.Add(link.LocalId.Value))
            {
                result.Add(link.LocalId.Value);
            }
        }

        return result;
    }

    public IReadOnlyList<NoteLinkRef> ExtractLinkRefs(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Array.Empty<NoteLinkRef>();
        }

        var matches = LinkPattern.Matches(text);
        if (matches.Count == 0)
        {
            return Array.Empty<NoteLinkRef>();
        }

        var result = new List<NoteLinkRef>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match match in matches)
        {
            var raw = match.Groups[1].Value.Trim();
            if (string.IsNullOrEmpty(raw) || !seen.Add(raw))
            {
                continue;
            }

            if (TryParseSyncId(raw, out var syncId))
            {
                result.Add(new NoteLinkRef(null, syncId, raw));
                continue;
            }

            if (int.TryParse(raw, out int id) && id > 0)
            {
                result.Add(new NoteLinkRef(id, null, raw));
            }
        }

        return result;
    }

    public string FormatLink(int noteId)
    {
        return $"[[{noteId}]]";
    }

    public string FormatLink(Guid syncId)
    {
        if (syncId == Guid.Empty)
        {
            throw new ArgumentException("SyncId не может быть пустым.", nameof(syncId));
        }

        return $"[[{SyncLinkPrefix}{syncId:D}]]";
    }

    public string FormatLink(Note note)
    {
        if (note == null) throw new ArgumentNullException(nameof(note));
        if (note.SyncId != Guid.Empty)
        {
            return FormatLink(note.SyncId);
        }

        return FormatLink(note.Id);
    }

    public string RewriteLegacyLinks(string? text, IReadOnlyDictionary<int, Guid> idToSyncId)
    {
        if (string.IsNullOrEmpty(text) || idToSyncId == null || idToSyncId.Count == 0)
        {
            return text ?? string.Empty;
        }

        return LinkPattern.Replace(text, match =>
        {
            var raw = match.Groups[1].Value.Trim();
            if (TryParseSyncId(raw, out _))
            {
                return match.Value;
            }

            if (int.TryParse(raw, out int id) && id > 0 && idToSyncId.TryGetValue(id, out var syncId) && syncId != Guid.Empty)
            {
                return FormatLink(syncId);
            }

            return match.Value;
        });
    }

    public static bool TryParseSyncId(string raw, out Guid syncId)
    {
        syncId = Guid.Empty;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        var value = raw.Trim();
        if (value.StartsWith(SyncLinkPrefix, StringComparison.OrdinalIgnoreCase))
        {
            value = value.Substring(SyncLinkPrefix.Length).Trim();
        }

        return Guid.TryParse(value, out syncId) && syncId != Guid.Empty;
    }

    public static string FormatNoteTitle(string? text, int noteId, int maxLength = 50)
    {
        return Helpers.NoteTitleHelper.GetDisplayTitle(string.Empty, text, maxLength);
    }

    public static string FormatNoteTitle(string? title, string? text, int noteId, int maxLength = 50)
    {
        return Helpers.NoteTitleHelper.GetDisplayTitle(title, text, maxLength);
    }

    private static string StripMarkdownHeading(string line)
    {
        var trimmed = line.TrimStart();
        if (trimmed.StartsWith('#'))
        {
            trimmed = trimmed.TrimStart('#').TrimStart();
        }

        return trimmed;
    }

    public IReadOnlyList<NoteLinkItemDto> GetOutgoingLinks(QuickNotesDbContext db, string? text)
    {
        var refs = ExtractLinkRefs(text);
        if (refs.Count == 0)
        {
            return Array.Empty<NoteLinkItemDto>();
        }

        var localIds = refs.Where(r => r.LocalId.HasValue).Select(r => r.LocalId!.Value).Distinct().ToList();
        var syncIds = refs.Where(r => r.SyncId.HasValue).Select(r => r.SyncId!.Value).Distinct().ToList();

        var notes = db.Notes
            .AsNoTracking()
            .Where(n => localIds.Contains(n.Id) || syncIds.Contains(n.SyncId))
            .Select(n => new NoteMatch(n.Id, n.SyncId, n.Title, n.Text, n.DeletedAt))
            .ToList();

        var byId = notes.ToDictionary(n => n.Id);
        var bySync = notes
            .Where(n => n.SyncId != Guid.Empty)
            .GroupBy(n => n.SyncId)
            .ToDictionary(g => g.Key, g => g.First());

        var result = new List<NoteLinkItemDto>(refs.Count);
        var seen = new HashSet<int>();
        foreach (var link in refs)
        {
            NoteMatch? match = null;
            if (link.SyncId.HasValue)
            {
                bySync.TryGetValue(link.SyncId.Value, out match);
            }

            if (match == null && link.LocalId.HasValue)
            {
                byId.TryGetValue(link.LocalId.Value, out match);
            }

            if (match != null && match.DeletedAt == null)
            {
                if (!seen.Add(match.Id))
                {
                    continue;
                }

                result.Add(new NoteLinkItemDto(match.Id, Helpers.NoteTitleHelper.GetDisplayTitle(match.Title, match.Text), IsAvailable: true));
            }
            else
            {
                result.Add(new NoteLinkItemDto(link.LocalId ?? 0, UnavailableNoteTitle, IsAvailable: false));
            }
        }

        return result;
    }

    public const int MaxIncomingLinks = 100;
    public const int MaxIncomingCandidates = 500;

    public IncomingLinksResult GetIncomingLinksResult(QuickNotesDbContext db, int? currentNoteId)
    {
        if (!currentNoteId.HasValue || currentNoteId.Value <= 0)
        {
            return IncomingLinksResult.Empty;
        }

        int id = currentNoteId.Value;
        var current = db.Notes.AsNoTracking().FirstOrDefault(n => n.Id == id);
        Guid currentSyncId = current?.SyncId ?? Guid.Empty;

        string idPattern = $"%[[%{id}%]]%";
        string? qnPattern = currentSyncId != Guid.Empty ? $"%[[qn:{currentSyncId:D}]]%" : null;
        string? guidPattern = currentSyncId != Guid.Empty ? $"%[[{currentSyncId:D}]]%" : null;

        var candidates = db.Notes
            .AsNoTracking()
            .Where(n => n.DeletedAt == null && n.Id != id && (
                EF.Functions.Like(n.Text, idPattern) ||
                (qnPattern != null && EF.Functions.Like(n.Text, qnPattern)) ||
                (guidPattern != null && EF.Functions.Like(n.Text, guidPattern))))
            .OrderBy(n => n.Id)
            .Take(MaxIncomingCandidates + 1)
            .Select(n => new { n.Id, n.Title, n.Text })
            .ToList();

        bool isTruncated = candidates.Count > MaxIncomingCandidates;
        var candidatesToProcess = isTruncated
            ? candidates.Take(MaxIncomingCandidates)
            : candidates;

        var result = new List<NoteLinkItemDto>();
        foreach (var cand in candidatesToProcess)
        {
            if (LinksToNote(cand.Text, id, currentSyncId))
            {
                result.Add(new NoteLinkItemDto(cand.Id, Helpers.NoteTitleHelper.GetDisplayTitle(cand.Title, cand.Text), IsAvailable: true));
                if (result.Count >= MaxIncomingLinks)
                {
                    break;
                }
            }
        }

        var sortedItems = result.OrderBy(r => r.TargetNoteId).ToList();
        return new IncomingLinksResult(sortedItems, isTruncated);
    }

    public IncomingLinksResult GetIncomingLinks(QuickNotesDbContext db, int? currentNoteId)
    {
        return GetIncomingLinksResult(db, currentNoteId);
    }

    public bool LinksToNote(string? text, int localId, Guid syncId)
    {
        foreach (var link in ExtractLinkRefs(text))
        {
            if (link.LocalId.HasValue && link.LocalId.Value == localId)
            {
                return true;
            }

            if (link.SyncId.HasValue && syncId != Guid.Empty && link.SyncId.Value == syncId)
            {
                return true;
            }
        }

        return false;
    }

    private sealed record NoteMatch(int Id, Guid SyncId, string Title, string Text, DateTime? DeletedAt);
}
