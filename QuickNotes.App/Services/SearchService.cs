using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using QuickNotes.App.Data;
using QuickNotes.App.Models;

namespace QuickNotes.App.Services;

public class SearchService
{
    public static string BuildFtsQuery(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return string.Empty;

        var terms = SearchPreview.ExtractSearchTerms(query);
        if (terms.Count == 0)
        {
            return "\"" + query.Trim().Replace("\"", "\"\"") + "\"*";
        }

        if (terms.Count == 1)
        {
            return "\"" + terms[0].Replace("\"", "\"\"") + "\"*";
        }

        return string.Join(" AND ", terms.Select(t => "\"" + t.Replace("\"", "\"\"") + "\"*"));
    }

    public List<int> SearchNoteIds(QuickNotesDbContext context, string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return new();
        return Search(context, query)
            .Where(n => n.DeletedAt == null)
            .Select(n => n.Id)
            .ToList();
    }

    private static IQueryable<Note> Search(QuickNotesDbContext context, string query)
    {
        var ftsQuery = BuildFtsQuery(query);
        if (string.IsNullOrWhiteSpace(ftsQuery))
        {
            return context.Notes.Where(n => false);
        }
        // Protected notes are excluded from the persistent FTS index (their plaintext is never
        // indexed). Searching unlocked protected notes happens in-memory via INoteProtectionService.
        return context.Notes.FromSqlInterpolated(
            $"SELECT * FROM Notes WHERE Id IN (SELECT NoteId FROM NotesFts WHERE NotesFts MATCH {ftsQuery}) AND IsProtected = 0");
    }

    public int GetInboxCount(QuickNotesDbContext context)
    {
        return context.Notes.Count(n => n.DeletedAt == null && n.IsInbox);
    }

    public IReadOnlySet<int> GetFilteredCandidateNoteIds(
        QuickNotesDbContext context,
        NavigationSection section,
        int? selectedTagId = null,
        IEnumerable<Tag>? allTags = null,
        SearchCondition? condition = null)
    {
        if (section == NavigationSection.Trash || section == NavigationSection.Tasks)
        {
            return new HashSet<int>();
        }

        var tags = allTags ?? Enumerable.Empty<Tag>();
        IQueryable<Note> query = context.Notes.Where(n => n.DeletedAt == null);
        query = ApplySectionAndTagFilters(query, section, selectedTagId, tags, condition);

        return query.Select(n => n.Id).ToHashSet();
    }

    private static IQueryable<Note> ApplySectionAndTagFilters(
        IQueryable<Note> query,
        NavigationSection section,
        int? selectedTagId,
        IEnumerable<Tag> tags,
        SearchCondition? condition)
    {
        switch (section)
        {
            case NavigationSection.Tasks:
                query = query.Where(n => false);
                break;

            case NavigationSection.Trash:
                query = query.Where(n => n.DeletedAt != null);
                break;

            case NavigationSection.Inbox:
                query = query.Where(n => n.DeletedAt == null && n.IsInbox);
                break;

            case NavigationSection.Favorites:
                query = query.Where(n => n.DeletedAt == null && n.IsFavorite);
                break;

            case NavigationSection.Today:
                var today = DateTime.Today;
                var tomorrow = today.AddDays(1);
                query = query.Where(n => n.DeletedAt == null && n.CreatedAt >= today && n.CreatedAt < tomorrow);
                break;

            case NavigationSection.Recent:
                var recentCutoff = DateTime.Today.AddDays(-7);
                query = query.Where(n => n.DeletedAt == null && (n.CreatedAt >= recentCutoff || n.UpdatedAt >= recentCutoff));
                break;

            case NavigationSection.Untagged:
                query = query.Where(n => n.DeletedAt == null && !n.NoteTags.Any(nt => !nt.IsSuppressed));
                break;

            case NavigationSection.All:
            default:
                query = query.Where(n => n.DeletedAt == null);
                break;
        }

        if (selectedTagId.HasValue && section != NavigationSection.Trash)
        {
            var ids = TagHierarchyService.GetTagAndDescendantIds(selectedTagId.Value, tags);
            query = query.Where(n => n.NoteTags.Any(nt => !nt.IsSuppressed && ids.Contains(nt.TagId)));
        }

        if (condition != null)
        {
            var tagHierarchy = new Dictionary<int, HashSet<int>>();
            foreach (var tag in tags)
            {
                tagHierarchy[tag.Id] = TagHierarchyService.GetTagAndDescendantIds(tag.Id, tags);
            }
            var expr = condition.ToExpression(DateTime.Now, tagHierarchy);
            query = query.Where(expr);
        }

        return query;
    }

    private static IOrderedQueryable<Note> ApplySorting(IQueryable<Note> query, NoteSortMode sortMode)
    {
        return sortMode switch
        {
            NoteSortMode.CreatedAt => query
                .OrderByDescending(n => n.CreatedAt)
                .ThenByDescending(n => n.Id),

            NoteSortMode.UpdatedAt => query
                .OrderByDescending(n => n.UpdatedAt)
                .ThenByDescending(n => n.CreatedAt)
                .ThenByDescending(n => n.Id),

            NoteSortMode.Pinned => query
                .OrderByDescending(n => n.IsPinned)
                .ThenByDescending(n => n.CreatedAt)
                .ThenByDescending(n => n.Id),

            _ => query
                .OrderByDescending(n => n.IsPinned)
                .ThenByDescending(n => n.CreatedAt)
                .ThenByDescending(n => n.Id)
        };
    }

    public int CountNotes(
        QuickNotesDbContext context,
        string? searchQuery,
        int? selectedTagId = null,
        IEnumerable<Tag>? allTags = null,
        NavigationSection section = NavigationSection.All,
        IReadOnlyCollection<int>? protectedNoteIds = null,
        System.Threading.CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (section == NavigationSection.Trash)
        {
            protectedNoteIds = null;
        }

        var tags = allTags ?? Enumerable.Empty<Tag>();
        var parsed = SearchQueryParser.Parse(searchQuery, tags);

        var exactQuery = GetExactMatchesQuery(context, parsed.FreeText, protectedNoteIds);
        exactQuery = ApplySectionAndTagFilters(exactQuery, section, selectedTagId, tags, parsed.Condition);
        return exactQuery.Count();
    }

    private static IQueryable<Note> GetExactMatchesQuery(
        QuickNotesDbContext context,
        string? freeText,
        IReadOnlyCollection<int>? protectedNoteIds = null)
    {
        if (string.IsNullOrWhiteSpace(freeText))
        {
            return context.Notes;
        }

        var ftsQuery = BuildFtsQuery(freeText);
        bool hasFts = !string.IsNullOrWhiteSpace(ftsQuery);
        bool hasProtected = protectedNoteIds != null && protectedNoteIds.Count > 0;

        if (!hasFts && !hasProtected)
        {
            return context.Notes.Where(n => false);
        }

        if (hasFts && !hasProtected)
        {
            return context.Notes.FromSqlInterpolated(
                $"SELECT * FROM Notes WHERE Id IN (SELECT NoteId FROM NotesFts WHERE NotesFts MATCH {ftsQuery}) AND IsProtected = 0");
        }

        if (!hasFts && hasProtected)
        {
            var pList = protectedNoteIds!.ToList();
            return context.Notes.Where(n => pList.Contains(n.Id) && n.IsProtected);
        }

        var protectedList = protectedNoteIds!.ToList();
        var ftsQueryable = context.Notes.FromSqlInterpolated(
            $"SELECT * FROM Notes WHERE Id IN (SELECT NoteId FROM NotesFts WHERE NotesFts MATCH {ftsQuery}) AND IsProtected = 0");
        var protectedQueryable = context.Notes.Where(n => protectedList.Contains(n.Id) && n.IsProtected);

        return ftsQueryable.Concat(protectedQueryable);
    }

    public List<Note> QueryNotes(
        QuickNotesDbContext context,
        string? searchQuery,
        int? selectedTagId = null,
        IEnumerable<Tag>? allTags = null,
        NavigationSection section = NavigationSection.All,
        NoteSortMode sortMode = NoteSortMode.Pinned,
        IReadOnlyCollection<int>? protectedNoteIds = null,
        int skip = 0,
        int take = int.MaxValue,
        System.Threading.CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (section == NavigationSection.Trash)
        {
            protectedNoteIds = null;
        }

        var tags = allTags ?? Enumerable.Empty<Tag>();
        var parsed = SearchQueryParser.Parse(searchQuery, tags);

        var exactQuery = GetExactMatchesQuery(context, parsed.FreeText, protectedNoteIds);
        exactQuery = ApplySectionAndTagFilters(exactQuery, section, selectedTagId, tags, parsed.Condition);

        // Two-stage hydration: build a deterministic paged list of IDs via SQL LIMIT/OFFSET first,
        // then hydrate only the final page by primary key with tags. This avoids EF Core collection
        // subquery translation errors when combining raw SQL/FTS queries with protected note filters via Concat.
        IQueryable<Note> sortedQuery = ApplySorting(exactQuery, sortMode);
        if (skip > 0)
        {
            sortedQuery = sortedQuery.Skip(skip);
        }
        if (take < int.MaxValue)
        {
            sortedQuery = sortedQuery.Take(take);
        }

        var pageIds = sortedQuery
            .Select(n => n.Id)
            .ToList();

        if (pageIds.Count == 0)
        {
            return new List<Note>();
        }

        cancellationToken.ThrowIfCancellationRequested();
        var notes = context.Notes
            .Where(n => pageIds.Contains(n.Id))
            .Include(n => n.NoteTags)
            .ThenInclude(nt => nt.Tag)
            .AsNoTracking()
            .ToList();

        var notesById = notes.ToDictionary(n => n.Id);
        var pageNotes = new List<Note>(pageIds.Count);
        foreach (var id in pageIds)
        {
            if (notesById.TryGetValue(id, out var note))
            {
                pageNotes.Add(note);
            }
        }
        return pageNotes;
    }

    public int PurgeOldTrash(QuickNotesDbContext context, DateTime cutoffUtcOrLocal, int maxBatch = 500, Action<IEnumerable<string>>? onDeletedAttachments = null)
    {
        if (maxBatch <= 0) maxBatch = 500;
        int totalDeleted = 0;
        while (true)
        {
            var expired = context.Notes
                .Include(n => n.Attachments)
                .Where(n => n.DeletedAt != null && n.DeletedAt < cutoffUtcOrLocal)
                .OrderBy(n => n.DeletedAt)
                .Take(maxBatch)
                .ToList();

            if (expired.Count == 0)
                break;

            var storedFileNames = expired
                .SelectMany(n => n.Attachments)
                .Select(a => a.StoredFileName)
                .Distinct()
                .ToList();

            context.Notes.RemoveRange(expired);
            int saved = context.SaveChanges();
            totalDeleted += expired.Count;

            if (storedFileNames.Count > 0 && onDeletedAttachments != null)
            {
                onDeletedAttachments(storedFileNames);
            }

            if (expired.Count < maxBatch)
                break;

            if (saved == 0)
                break;
        }

        return totalDeleted;
    }
}
