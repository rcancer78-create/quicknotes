using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using QuickNotes.App.Data;
using QuickNotes.App.Models;

namespace QuickNotes.App.Services;

public class NoteHistoryService : INoteHistoryService
{
    public const int DefaultMaxRevisions = 20;

    public virtual NoteRevision? SaveSnapshot(QuickNotesDbContext db, Note note, int maxRevisions = DefaultMaxRevisions)
    {
        if (note.Id <= 0) return null;

        var currentTags = CreateTagSnapshots(note.NoteTags, db);

        var lastRevision = db.NoteRevisions
            .Where(r => r.NoteId == note.Id)
            .OrderByDescending(r => r.CreatedAt)
            .ThenByDescending(r => r.Id)
            .FirstOrDefault();

        if (lastRevision != null)
        {
            try
            {
                var lastTags = DeserializeTags(lastRevision.TagsJson);
                if (string.Equals(lastRevision.Title, note.Title, StringComparison.Ordinal) &&
                    string.Equals(lastRevision.Text, note.Text, StringComparison.Ordinal) &&
                    AreTagsEquivalent(lastTags, currentTags))
                {
                    // No-op: text, title and tags are identical, do not create duplicate version
                    return null;
                }
            }
            catch (Exception ex)
            {
                // Last revision has corrupted tags; cannot be considered equivalent to current state
                ErrorLogService.Write("History.SaveSnapshot", $"Last revision #{lastRevision.Id} has corrupted TagsJson: {ex.GetType().Name}: {ex.Message}");
            }
        }

        // Atomically prune oldest revisions if capacity reached
        int count = db.NoteRevisions.Count(r => r.NoteId == note.Id);
        if (count >= maxRevisions)
        {
            var oldest = db.NoteRevisions
                .Where(r => r.NoteId == note.Id)
                .OrderBy(r => r.CreatedAt)
                .ThenBy(r => r.Id)
                .Take(count - maxRevisions + 1)
                .ToList();

            db.NoteRevisions.RemoveRange(oldest);
        }

        var newRevision = new NoteRevision
        {
            NoteId = note.Id,
            CreatedAt = DateTime.Now,
            Title = note.Title,
            Text = note.Text,
            TagsJson = SerializeTags(currentTags)
        };

        db.NoteRevisions.Add(newRevision);

        var tx = db.Database.CurrentTransaction == null ? db.Database.BeginTransaction() : null;
        try
        {
            db.SaveChanges();
            tx?.Commit();
        }
        catch
        {
            tx?.Rollback();
            throw;
        }
        finally
        {
            tx?.Dispose();
        }

        return newRevision;
    }

    public virtual List<NoteHistoryItemDto> GetHistory(QuickNotesDbContext db, int noteId)
    {
        var revisions = db.NoteRevisions
            .Where(r => r.NoteId == noteId)
            .OrderBy(r => r.CreatedAt)
            .ThenBy(r => r.Id)
            .ToList();

        if (revisions.Count == 0)
        {
            return new List<NoteHistoryItemDto>();
        }

        var result = new List<NoteHistoryItemDto>();
        NoteRevision? prev = null;
        List<NoteRevisionTagSnapshot>? prevTags = null;

        foreach (var rev in revisions)
        {
            List<NoteRevisionTagSnapshot> currentTags;
            bool isCorrupted = false;
            try
            {
                currentTags = DeserializeTags(rev.TagsJson);
            }
            catch (Exception ex)
            {
                isCorrupted = true;
                currentTags = new List<NoteRevisionTagSnapshot>();
                ErrorLogService.Write("History.GetHistory", $"Ревизия #{rev.Id} заметки #{noteId}: повреждён TagsJson: {ex.GetType().Name}: {ex.Message}");
            }

            string diff;
            if (rev.IsProtected)
            {
                // Protected revision: never expose plaintext or tag names in the diff summary.
                // The editor decrypts the body in memory when the note is unlocked.
                diff = "Защищённая версия";
            }
            else if (isCorrupted)
            {
                diff = "[Данные тегов повреждены]";
            }
            else
            {
                diff = ComputeDiffSummary(prev?.Text, prevTags, rev.Text, currentTags);
            }

            result.Add(new NoteHistoryItemDto
            {
                Id = rev.Id,
                NoteId = rev.NoteId,
                CreatedAt = rev.CreatedAt,
                Title = rev.IsProtected ? string.Empty : (rev.Title ?? string.Empty),
                Text = rev.IsProtected ? string.Empty : rev.Text,
                Tags = rev.IsProtected ? new List<NoteRevisionTagSnapshot>() : currentTags,
                DiffSummary = diff,
                IsCorrupted = isCorrupted,
                IsProtectedRevision = rev.IsProtected
            });

            if (!isCorrupted && !rev.IsProtected)
            {
                prev = rev;
                prevTags = currentTags;
            }
        }

        // Return descending (newest version first)
        result.Reverse();
        return result;
    }

    public virtual Note RestoreRevision(QuickNotesDbContext db, int noteId, int revisionId, int maxRevisions = DefaultMaxRevisions)
    {
        var revision = db.NoteRevisions.FirstOrDefault(r => r.Id == revisionId && r.NoteId == noteId);
        if (revision == null)
        {
            throw new InvalidOperationException($"Версия #{revisionId} для заметки #{noteId} не найдена.");
        }

        var note = db.Notes
            .Include(n => n.NoteTags)
            .ThenInclude(nt => nt.Tag)
            .FirstOrDefault(n => n.Id == noteId);

        if (note == null)
        {
            throw new InvalidOperationException($"Заметка #{noteId} не найдена.");
        }

        // Validate tags BEFORE touching anything in note or DB
        List<NoteRevisionTagSnapshot> restoredTags;
        try
        {
            restoredTags = DeserializeTags(revision.TagsJson);
        }
        catch (Exception ex)
        {
            ErrorLogService.Write("History.RestoreRevision", $"Попытка восстановления повреждённой ревизии #{revisionId}: {ex.GetType().Name}: {ex.Message}");
            throw new InvalidOperationException($"Не удалось восстановить версию #{revisionId}: снимок тегов повреждён.", ex);
        }

        var tx = db.Database.CurrentTransaction == null ? db.Database.BeginTransaction() : null;
        try
        {
            // Update note title, text and timestamp
            if (!revision.IsProtected)
            {
                note.Title = revision.Title ?? string.Empty;
            }
            note.Text = revision.Text;
            note.UpdatedAt = DateTime.Now;

            // Synchronize note tags
            var allDbTags = db.Tags.ToList();
            var tagById = allDbTags.ToDictionary(t => t.Id);
            var tagByName = allDbTags.ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);

            var desired = new Dictionary<int, NoteTag>();
            foreach (var st in restoredTags)
            {
                int targetTagId;
                if (tagById.TryGetValue(st.TagId, out var existingTag))
                {
                    targetTagId = existingTag.Id;
                }
                else if (tagByName.TryGetValue(st.TagName, out var existingByName))
                {
                    targetTagId = existingByName.Id;
                }
                else
                {
                    var createdTag = new Tag { Name = st.TagName };
                    db.Tags.Add(createdTag);
                    db.SaveChanges();
                    allDbTags.Add(createdTag);
                    tagById[createdTag.Id] = createdTag;
                    tagByName[createdTag.Name] = createdTag;
                    targetTagId = createdTag.Id;
                }

                desired[targetTagId] = new NoteTag
                {
                    NoteId = note.Id,
                    TagId = targetTagId,
                    Origin = st.Origin,
                    IsSuppressed = st.IsSuppressed
                };
            }

            foreach (var link in note.NoteTags.ToList())
            {
                if (desired.Remove(link.TagId, out var updated))
                {
                    link.Origin = updated.Origin;
                    link.IsSuppressed = updated.IsSuppressed;
                }
                else
                {
                    note.NoteTags.Remove(link);
                }
            }
            foreach (var link in desired.Values)
            {
                note.NoteTags.Add(link);
            }

            PersistRestoredRevision(db, note, revision, maxRevisions);

            tx?.Commit();
            return note;
        }
        catch
        {
            tx?.Rollback();
            throw;
        }
        finally
        {
            tx?.Dispose();
        }
    }

    public virtual void PersistRestoredRevision(QuickNotesDbContext db, Note note, NoteRevision revision, int maxRevisions)
    {
        var newRevision = new NoteRevision
        {
            NoteId = note.Id,
            CreatedAt = DateTime.Now,
            Title = note.Title ?? string.Empty,
            Text = note.Text,
            TagsJson = revision.TagsJson
        };

        int count = db.NoteRevisions.Count(r => r.NoteId == note.Id);
        if (count >= maxRevisions)
        {
            var oldest = db.NoteRevisions
                .Where(r => r.NoteId == note.Id)
                .OrderBy(r => r.CreatedAt)
                .ThenBy(r => r.Id)
                .Take(count - maxRevisions + 1)
                .ToList();

            db.NoteRevisions.RemoveRange(oldest);
        }

        db.NoteRevisions.Add(newRevision);
        db.SaveChanges();
    }

    public virtual string ComputeDiffSummary(
        string? oldText,
        IReadOnlyCollection<NoteRevisionTagSnapshot>? oldTags,
        string newText,
        IReadOnlyCollection<NoteRevisionTagSnapshot> newTags)
    {
        if (oldText == null && oldTags == null)
        {
            int activeCount = newTags.Count(t => !t.IsSuppressed);
            return $"Создание заметки ({newText.Length} симв., тегов: {activeCount})";
        }

        string? textDiff = null;
        if (!string.Equals(oldText, newText, StringComparison.Ordinal))
        {
            int charDiff = newText.Length - (oldText?.Length ?? 0);
            string charPart = charDiff > 0 ? $"+{charDiff} симв." : charDiff < 0 ? $"{charDiff} симв." : "текст изменён";
            int oldLines = CountLines(oldText ?? string.Empty);
            int newLines = CountLines(newText);
            int lineDiff = newLines - oldLines;
            if (lineDiff != 0)
            {
                charPart += lineDiff > 0 ? $" (+{lineDiff} стр.)" : $" ({lineDiff} стр.)";
            }
            textDiff = charPart;
        }

        var prevActive = oldTags?.Where(t => !t.IsSuppressed).Select(t => t.TagName).ToHashSet(StringComparer.OrdinalIgnoreCase) ?? new();
        var currActive = newTags.Where(t => !t.IsSuppressed).Select(t => t.TagName).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var addedActive = currActive.Except(prevActive).OrderBy(x => x).ToList();
        var removedActive = prevActive.Except(currActive).OrderBy(x => x).ToList();

        var prevSuppressed = oldTags?.Where(t => t.IsSuppressed).Select(t => t.TagName).ToHashSet(StringComparer.OrdinalIgnoreCase) ?? new();
        var currSuppressed = newTags.Where(t => t.IsSuppressed).Select(t => t.TagName).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var addedSuppressed = currSuppressed.Except(prevSuppressed).OrderBy(x => x).ToList();
        var unsuppressed = prevSuppressed.Except(currSuppressed).OrderBy(x => x).ToList();

        var prevTagMap = oldTags?.ToDictionary(t => t.TagId) ?? new();
        var originChanges = new List<string>();
        foreach (var ct in newTags.Where(t => !t.IsSuppressed).OrderBy(t => t.TagName))
        {
            if (prevTagMap.TryGetValue(ct.TagId, out var pt) && !pt.IsSuppressed && pt.Origin != ct.Origin)
            {
                originChanges.Add($"{ct.TagName} ({(ct.Origin == TagOrigin.Manual ? "вручную" : "авто")})");
            }
        }

        var tagParts = new List<string>();
        foreach (var a in addedActive) tagParts.Add($"+{a}");
        foreach (var r in removedActive) tagParts.Add($"-{r}");
        foreach (var s in addedSuppressed) tagParts.Add($"~скрыт {s}");
        foreach (var u in unsuppressed.Where(x => !currActive.Contains(x))) tagParts.Add($"~снят {u}");
        foreach (var o in originChanges) tagParts.Add($"~{o}");

        if (!string.IsNullOrEmpty(textDiff) && tagParts.Count > 0)
        {
            return $"{textDiff}; теги: {string.Join(", ", tagParts)}";
        }
        if (!string.IsNullOrEmpty(textDiff))
        {
            return textDiff;
        }
        if (tagParts.Count > 0)
        {
            return $"Теги: {string.Join(", ", tagParts)}";
        }

        return "Без существенных изменений";
    }

    public static List<NoteRevisionTagSnapshot> CreateTagSnapshots(IEnumerable<NoteTag> noteTags, QuickNotesDbContext db)
    {
        var list = new List<NoteRevisionTagSnapshot>();
        Dictionary<int, string>? tagsMap = null;

        foreach (var nt in noteTags)
        {
            string? name = nt.Tag?.Name;
            if (string.IsNullOrWhiteSpace(name))
            {
                tagsMap ??= db.Tags.ToDictionary(t => t.Id, t => t.Name);
                if (!tagsMap.TryGetValue(nt.TagId, out name))
                {
                    name = $"Тег #{nt.TagId}";
                }
            }

            list.Add(new NoteRevisionTagSnapshot
            {
                TagId = nt.TagId,
                TagName = name,
                Origin = nt.Origin,
                IsSuppressed = nt.IsSuppressed
            });
        }

        return list.OrderBy(t => t.TagId).ToList();
    }

    public static bool AreTagsEquivalent(
        IReadOnlyCollection<NoteRevisionTagSnapshot> savedTags,
        IReadOnlyCollection<NoteRevisionTagSnapshot> currentTags)
    {
        if (savedTags.Count != currentTags.Count) return false;

        var savedOrdered = savedTags.OrderBy(t => t.TagId).ToList();
        var currentOrdered = currentTags.OrderBy(t => t.TagId).ToList();

        for (int i = 0; i < savedOrdered.Count; i++)
        {
            var s = savedOrdered[i];
            var c = currentOrdered[i];
            if (s.TagId != c.TagId || s.Origin != c.Origin || s.IsSuppressed != c.IsSuppressed)
            {
                return false;
            }
        }

        return true;
    }

    public static string SerializeTags(IEnumerable<NoteRevisionTagSnapshot> tags)
    {
        return JsonSerializer.Serialize(tags);
    }

    public static List<NoteRevisionTagSnapshot> DeserializeTags(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new List<NoteRevisionTagSnapshot>();
        try
        {
            return JsonSerializer.Deserialize<List<NoteRevisionTagSnapshot>>(json) ?? new List<NoteRevisionTagSnapshot>();
        }
        catch (JsonException ex)
        {
            ErrorLogService.Write("NoteHistoryService.DeserializeTags", $"{ex.GetType().Name}: {ex.Message}");
            throw new FormatException($"Повреждённый формат снимка тегов: {ex.Message}", ex);
        }
    }

    private static int CountLines(string text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        return text.Split('\n').Length;
    }
}
