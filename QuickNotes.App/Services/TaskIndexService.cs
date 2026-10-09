using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using QuickNotes.App.Data;
using QuickNotes.App.Helpers;
using QuickNotes.App.Models;

namespace QuickNotes.App.Services;

/// <summary>
/// Derived open-task index. Reads unprotected live <see cref="Note.Text"/> in batches.
/// Protected and deleted notes are omitted; no plaintext cache is written to disk.
/// </summary>
public sealed class TaskIndexService : ITaskIndexService
{
    public const int NoteScanBatchSize = 64;
    public const int DefaultPageSize = 50;

    private readonly Func<QuickNotesDbContext> _contextFactory;

    public TaskIndexService(Func<QuickNotesDbContext> contextFactory)
    {
        _contextFactory = contextFactory;
    }

    public Task<TaskIndexPage> QueryAsync(
        string? searchQuery,
        int skip,
        int take,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() => Query(searchQuery, skip, take, cancellationToken), cancellationToken);
    }

    public TaskIndexPage Query(
        string? searchQuery,
        int skip,
        int take,
        CancellationToken cancellationToken = default)
    {
        if (take <= 0)
        {
            return new TaskIndexPage();
        }

        if (skip < 0)
        {
            skip = 0;
        }

        string? needle = string.IsNullOrWhiteSpace(searchQuery) ? null : searchQuery.Trim();
        var matches = new List<MarkdownTaskLocator>();

        using var db = _contextFactory();
        int lastId = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batch = db.Notes
                .AsNoTracking()
                .Where(n => n.DeletedAt == null && !n.IsProtected && n.Id > lastId)
                .OrderBy(n => n.Id)
                .Select(n => new { n.Id, n.SyncId, n.Title, n.Text, n.UpdatedAt })
                .Take(NoteScanBatchSize)
                .ToList();

            if (batch.Count == 0)
            {
                break;
            }

            foreach (var note in batch)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string title = NoteTitleHelper.GetDisplayTitle(note.Title, note.Text, 72);
                var locators = MarkdownTaskParser.Parse(note.Text, note.Id, note.SyncId, title, note.UpdatedAt);
                foreach (var locator in locators)
                {
                    if (needle != null && !MatchesSearch(locator, needle))
                    {
                        continue;
                    }

                    matches.Add(locator);
                }
            }

            lastId = batch[^1].Id;
        }

        var ordered = matches
            .OrderBy(t => t.DueDate.HasValue ? 0 : 1)
            .ThenBy(t => t.DueDate ?? DateOnly.MaxValue)
            .ThenByDescending(t => t.NoteUpdatedAt)
            .ThenBy(t => t.NoteId)
            .ThenBy(t => t.LineNumber)
            .ToList();

        int total = ordered.Count;
        var page = ordered.Skip(skip).Take(take).ToList();
        return new TaskIndexPage
        {
            Items = page,
            TotalCount = total,
            HasMore = skip + page.Count < total
        };
    }

    public IReadOnlyList<MarkdownTaskLocator> QueryDatedOpenTasks(CancellationToken cancellationToken = default)
    {
        var matches = new List<MarkdownTaskLocator>();
        using var db = _contextFactory();
        int lastId = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batch = db.Notes
                .AsNoTracking()
                .Where(n => n.DeletedAt == null && !n.IsProtected && n.Id > lastId)
                .OrderBy(n => n.Id)
                .Select(n => new { n.Id, n.SyncId, n.Title, n.Text, n.UpdatedAt })
                .Take(NoteScanBatchSize)
                .ToList();

            if (batch.Count == 0)
            {
                break;
            }

            foreach (var note in batch)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string title = NoteTitleHelper.GetDisplayTitle(note.Title, note.Text, 72);
                foreach (var locator in MarkdownTaskParser.Parse(note.Text, note.Id, note.SyncId, title, note.UpdatedAt))
                {
                    if (locator.DueDate.HasValue)
                    {
                        matches.Add(locator);
                    }
                }
            }

            lastId = batch[^1].Id;
        }

        return matches;
    }

    private static bool MatchesSearch(MarkdownTaskLocator locator, string needle)
    {
        return locator.TaskText.Contains(needle, StringComparison.OrdinalIgnoreCase)
            || locator.NoteTitle.Contains(needle, StringComparison.OrdinalIgnoreCase)
            || (locator.DueDate.HasValue
                && locator.DueDate.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                    .Contains(needle, StringComparison.OrdinalIgnoreCase));
    }
}
