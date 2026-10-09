using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Microsoft.EntityFrameworkCore;
using QuickNotes.App.Data;
using QuickNotes.App.Helpers;
using QuickNotes.App.Models;
using QuickNotes.App.Services.Sync;

namespace QuickNotes.App.Services;

public sealed class NoteAssemblyService : INoteAssemblyService
{
    public const int MinimumSourceCount = 2;
    public const string DefaultTitle = "Собранная заметка";

    internal static Action<NoteAssemblyTransactionPhase>? TestInjectFailure { get; set; }

    public NoteAssemblyPreviewResult BuildPreview(QuickNotesDbContext db, NoteAssemblyPreviewRequest request)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(request);

        var validation = ValidateRequestShape(request.SourceNoteIds, request.Title, request.SeparatorKind, request.CustomSeparator);
        if (validation.Error != null)
        {
            return InvalidPreview(validation.Error, validation.Title, validation.Separator);
        }

        var loaded = LoadSources(db, request.SourceNoteIds);
        if (loaded.Error != null)
        {
            return InvalidPreview(loaded.Error, validation.Title, validation.Separator, loaded.Snapshots);
        }

        string markdown = ComposeMarkdown(validation.Title, validation.Separator, loaded.Bodies);
        var tags = UnionTags(loaded.TagsInOrder);
        string planHash = ComputePlanHash(validation.Title, request.SeparatorKind, validation.Separator, loaded.Snapshots, tags, markdown);

        return new NoteAssemblyPreviewResult
        {
            IsValid = true,
            Title = validation.Title,
            AssembledMarkdown = markdown,
            NormalizedSeparator = validation.Separator,
            SeparatorKind = request.SeparatorKind,
            Sources = loaded.Snapshots,
            ResultTags = tags,
            PlanHash = planHash
        };
    }

    public NoteAssemblyExecuteResult Execute(
        QuickNotesDbContext db,
        NoteAssemblyExecuteRequest request,
        ILocalMutationCoordinator mutationCoordinator,
        INoteHistoryService? historyService = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(mutationCoordinator);

        var preview = request.Preview;
        if (preview == null || !preview.IsValid)
        {
            return NoteAssemblyExecuteResult.Fail(preview?.BlockingReason ?? "Предпросмотр недействителен. Повторите сборку.");
        }

        if (request.MoveSourcesToTrash && !request.TrashAcknowledged)
        {
            return NoteAssemblyExecuteResult.Fail("Перемещение исходников в корзину требует отдельного подтверждения.");
        }

        return mutationCoordinator.ExecuteBulkMutation(() =>
            ExecuteInsideMutationBoundary(db, request, preview, historyService, cancellationToken));
    }

    private NoteAssemblyExecuteResult ExecuteInsideMutationBoundary(
        QuickNotesDbContext db,
        NoteAssemblyExecuteRequest request,
        NoteAssemblyPreviewResult preview,
        INoteHistoryService? historyService,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            InvokeTestHook(NoteAssemblyTransactionPhase.Validation);

            var reloaded = BuildPreview(db, new NoteAssemblyPreviewRequest
            {
                SourceNoteIds = preview.Sources.Select(s => s.NoteId).ToArray(),
                Title = preview.Title,
                SeparatorKind = preview.SeparatorKind,
                CustomSeparator = preview.SeparatorKind == NoteAssemblySeparatorKind.Custom
                    ? preview.NormalizedSeparator
                    : null
            });

            if (!reloaded.IsValid)
            {
                return NoteAssemblyExecuteResult.Fail(reloaded.BlockingReason ?? "Исходники больше нельзя собрать.");
            }

            if (!ResultTagsMatch(preview.ResultTags, reloaded.ResultTags))
            {
                return NoteAssemblyExecuteResult.Fail("Теги результата изменились после предпросмотра. Повторите сборку.");
            }

            if (!string.Equals(reloaded.PlanHash, preview.PlanHash, StringComparison.Ordinal) ||
                !string.Equals(reloaded.AssembledMarkdown, preview.AssembledMarkdown, StringComparison.Ordinal) ||
                !SnapshotsMatch(preview.Sources, reloaded.Sources))
            {
                return NoteAssemblyExecuteResult.Fail("Исходники изменились после предпросмотра. Повторите сборку.");
            }

            historyService ??= new NoteHistoryService();
            using var transaction = db.Database.BeginTransaction();
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                InvokeTestHook(NoteAssemblyTransactionPhase.InsertNote);

                var now = DateTime.Now;
                var note = new Note
                {
                    Title = reloaded.Title,
                    Text = reloaded.AssembledMarkdown,
                    CreatedAt = now,
                    UpdatedAt = now,
                    IsInbox = false,
                    IsPinned = false,
                    IsFavorite = false,
                    DeletedAt = null
                };
                db.Notes.Add(note);
                db.SaveChanges();

                cancellationToken.ThrowIfCancellationRequested();
                InvokeTestHook(NoteAssemblyTransactionPhase.Tags);

                foreach (var tag in CanonicalResultTags(preview.ResultTags))
                {
                    bool exists = db.Tags.Any(t => t.Id == tag.TagId);
                    if (!exists)
                    {
                        throw new InvalidOperationException("Набор тегов изменился после предпросмотра.");
                    }

                    db.NoteTags.Add(new NoteTag
                    {
                        NoteId = note.Id,
                        TagId = tag.TagId,
                        Origin = tag.Origin,
                        IsSuppressed = false
                    });
                }

                db.SaveChanges();

                cancellationToken.ThrowIfCancellationRequested();
                InvokeTestHook(NoteAssemblyTransactionPhase.History);
                historyService.SaveSnapshot(db, note);

                int trashed = 0;
                if (request.MoveSourcesToTrash)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    InvokeTestHook(NoteAssemblyTransactionPhase.TrashSources);
                    var trashNow = DateTime.Now;
                    foreach (var source in reloaded.Sources)
                    {
                        var entity = db.Notes.First(n => n.Id == source.NoteId);
                        entity.DeletedAt = trashNow;
                        entity.UpdatedAt = trashNow;
                        trashed++;
                    }

                    db.SaveChanges();
                }

                cancellationToken.ThrowIfCancellationRequested();
                InvokeTestHook(NoteAssemblyTransactionPhase.Commit);
                transaction.Commit();

                return new NoteAssemblyExecuteResult
                {
                    Success = true,
                    CreatedNoteId = note.Id,
                    CreatedSyncId = note.SyncId,
                    SourceCount = reloaded.Sources.Count,
                    TrashedSourceCount = trashed,
                    TagCount = preview.ResultTags.Count,
                    Summary = request.MoveSourcesToTrash
                        ? $"Создана заметка «{reloaded.Title}» из {reloaded.Sources.Count} исходников. Исходники перемещены в корзину: {trashed}."
                        : $"Создана заметка «{reloaded.Title}» из {reloaded.Sources.Count} исходников. Исходники сохранены."
                };
            }
            catch (OperationCanceledException)
            {
                transaction.Rollback();
                return NoteAssemblyExecuteResult.Fail("Сборка отменена. Изменения не сохранены.");
            }
            catch (Exception ex)
            {
                try { transaction.Rollback(); } catch { /* already rolled back */ }
                ErrorLogService.Write("NoteAssembly.Execute", ex);
                return NoteAssemblyExecuteResult.Fail("Не удалось выполнить сборку. Изменения откатены.");
            }
        }
        catch (OperationCanceledException)
        {
            return NoteAssemblyExecuteResult.Fail("Сборка отменена. Изменения не сохранены.");
        }
        catch (Exception ex)
        {
            ErrorLogService.Write("NoteAssembly.Execute", ex);
            return NoteAssemblyExecuteResult.Fail("Не удалось выполнить сборку. Изменения откатены.");
        }
    }

    public static string NormalizeSeparator(NoteAssemblySeparatorKind kind, string? custom)
    {
        return kind switch
        {
            NoteAssemblySeparatorKind.BlankLine => string.Empty,
            NoteAssemblySeparatorKind.ThematicBreakDash => "---",
            NoteAssemblySeparatorKind.ThematicBreakStar => "***",
            NoteAssemblySeparatorKind.Custom => NormalizeCustomSeparator(custom),
            _ => "---"
        };
    }

    public static string ComposeMarkdown(string title, string separator, IReadOnlyList<string> bodies)
    {
        var builder = new StringBuilder();
        builder.Append("# ");
        builder.Append(title);
        builder.Append("\n\n");

        for (int i = 0; i < bodies.Count; i++)
        {
            builder.Append(bodies[i]);
            if (i < bodies.Count - 1)
            {
                builder.Append("\n\n");
                if (!string.IsNullOrEmpty(separator))
                {
                    builder.Append(separator);
                    builder.Append("\n\n");
                }
            }
        }

        builder.Append('\n');
        return builder.ToString();
    }

    public static string NormalizeNoteBody(string? text)
    {
        string value = (text ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        return value.TrimEnd();
    }

    public static string ComputeContentHash(int noteId, DateTime updatedAt, string title, string text)
    {
        string payload = string.Concat(noteId.ToString(), "\n", updatedAt.Ticks.ToString(), "\n", title ?? string.Empty, "\n", text ?? string.Empty);
        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(bytes);
    }

    private static string NormalizeCustomSeparator(string? custom)
    {
        string sanitized = NoteTitleHelper.SanitizeSingleLine(custom).Trim();
        if (sanitized.Length > 120)
        {
            sanitized = sanitized.Substring(0, 120).TrimEnd();
        }

        return sanitized;
    }

    private static (string? Error, string Title, string Separator) ValidateRequestShape(
        IReadOnlyList<int> sourceIds,
        string? title,
        NoteAssemblySeparatorKind kind,
        string? customSeparator)
    {
        string sanitizedTitle = NoteTitleHelper.SanitizeSingleLine(title).Trim();
        if (sanitizedTitle.Length > NoteTitleHelper.DefaultMaxDerivedLength)
        {
            sanitizedTitle = sanitizedTitle.Substring(0, NoteTitleHelper.DefaultMaxDerivedLength).TrimEnd();
        }

        if (string.IsNullOrWhiteSpace(sanitizedTitle))
        {
            return ("Укажите итоговый заголовок.", sanitizedTitle, NormalizeSeparator(kind, customSeparator));
        }

        if (sourceIds == null || sourceIds.Count == 0)
        {
            return ("Выберите хотя бы две заметки для сборки.", sanitizedTitle, NormalizeSeparator(kind, customSeparator));
        }

        if (sourceIds.Count < MinimumSourceCount)
        {
            return ("Для сборки нужны минимум две заметки.", sanitizedTitle, NormalizeSeparator(kind, customSeparator));
        }

        if (sourceIds.Any(id => id <= 0))
        {
            return ("Набор исходников содержит некорректный идентификатор.", sanitizedTitle, NormalizeSeparator(kind, customSeparator));
        }

        if (sourceIds.Count != sourceIds.Distinct().Count())
        {
            return ("Один и тот же исходник нельзя выбрать дважды.", sanitizedTitle, NormalizeSeparator(kind, customSeparator));
        }

        if (kind == NoteAssemblySeparatorKind.Custom && string.IsNullOrWhiteSpace(NormalizeCustomSeparator(customSeparator)))
        {
            return ("Задайте непустой пользовательский разделитель.", sanitizedTitle, string.Empty);
        }

        return (null, sanitizedTitle, NormalizeSeparator(kind, customSeparator));
    }

    private static (string? Error, IReadOnlyList<NoteAssemblySourceSnapshot> Snapshots, IReadOnlyList<string> Bodies, List<List<NoteTag>> TagsInOrder)
        LoadSources(QuickNotesDbContext db, IReadOnlyList<int> sourceIds)
    {
        var notes = db.Notes
            .AsNoTracking()
            .Include(n => n.NoteTags)
            .ThenInclude(nt => nt.Tag)
            .Where(n => sourceIds.Contains(n.Id))
            .ToList();

        var byId = notes.ToDictionary(n => n.Id);
        var snapshots = new List<NoteAssemblySourceSnapshot>(sourceIds.Count);
        var bodies = new List<string>(sourceIds.Count);
        var tags = new List<List<NoteTag>>(sourceIds.Count);
        var missing = new List<int>();

        for (int i = 0; i < sourceIds.Count; i++)
        {
            int id = sourceIds[i];
            if (!byId.TryGetValue(id, out var note))
            {
                missing.Add(id);
                continue;
            }

            bool deleted = note.DeletedAt != null;
            snapshots.Add(new NoteAssemblySourceSnapshot
            {
                NoteId = note.Id,
                Order = i,
                UpdatedAt = note.UpdatedAt,
                ContentHash = ComputeContentHash(note.Id, note.UpdatedAt, note.Title, note.Text),
                IsProtected = note.IsProtected,
                IsDeleted = deleted,
                DisplayTitle = note.IsProtected
                    ? "Защищённая заметка"
                    : NoteTitleHelper.GetDisplayTitle(note.Title, note.Text)
            });

            if (note.IsProtected)
            {
                return ("Сборка заблокирована: среди исходников есть защищённая заметка. Plaintext не раскрывается.", snapshots, bodies, tags);
            }

            if (deleted)
            {
                return ("Нельзя собирать уже удалённые заметки.", snapshots, bodies, tags);
            }

            bodies.Add(NormalizeNoteBody(note.Text));
            tags.Add(note.NoteTags.Where(nt => !nt.IsSuppressed).ToList());
        }

        if (missing.Count > 0)
        {
            return ("Один или несколько исходников исчезли. Повторите выбор.", snapshots, bodies, tags);
        }

        return (null, snapshots, bodies, tags);
    }

    private static IReadOnlyList<NoteAssemblyTagDto> UnionTags(List<List<NoteTag>> tagsInOrder)
    {
        var result = new List<NoteAssemblyTagDto>();
        var origins = new Dictionary<int, TagOrigin>();
        var names = new Dictionary<int, string>();

        foreach (var group in tagsInOrder)
        {
            foreach (var nt in group.OrderBy(t => t.TagId))
            {
                names[nt.TagId] = nt.Tag?.Name ?? names.GetValueOrDefault(nt.TagId, string.Empty);
                if (!origins.TryGetValue(nt.TagId, out var existing))
                {
                    origins[nt.TagId] = nt.Origin;
                    result.Add(new NoteAssemblyTagDto
                    {
                        TagId = nt.TagId,
                        TagName = names[nt.TagId],
                        Origin = nt.Origin
                    });
                }
                else if (existing != TagOrigin.Manual && nt.Origin == TagOrigin.Manual)
                {
                    origins[nt.TagId] = TagOrigin.Manual;
                    int idx = result.FindIndex(t => t.TagId == nt.TagId);
                    if (idx >= 0)
                    {
                        result[idx] = new NoteAssemblyTagDto
                        {
                            TagId = nt.TagId,
                            TagName = names[nt.TagId],
                            Origin = TagOrigin.Manual
                        };
                    }
                }
            }
        }

        return CanonicalResultTags(result);
    }

    private static IReadOnlyList<NoteAssemblyTagDto> CanonicalResultTags(IEnumerable<NoteAssemblyTagDto> tags)
    {
        return tags
            .OrderBy(t => t.TagId)
            .ThenBy(t => (int)t.Origin)
            .ThenBy(t => t.TagName, StringComparer.Ordinal)
            .ToList();
    }

    private static string ComputePlanHash(
        string title,
        NoteAssemblySeparatorKind kind,
        string separator,
        IReadOnlyList<NoteAssemblySourceSnapshot> sources,
        IReadOnlyList<NoteAssemblyTagDto> resultTags,
        string markdown)
    {
        var builder = new StringBuilder();
        builder.Append(title);
        builder.Append('\n');
        builder.Append((int)kind);
        builder.Append('\n');
        builder.Append(separator);
        builder.Append('\n');
        foreach (var source in sources)
        {
            builder.Append(source.NoteId);
            builder.Append('|');
            builder.Append(source.Order);
            builder.Append('|');
            builder.Append(source.UpdatedAt.Ticks);
            builder.Append('|');
            builder.Append(source.ContentHash);
            builder.Append('|');
            builder.Append(source.IsProtected ? '1' : '0');
            builder.Append('|');
            builder.Append(source.IsDeleted ? '1' : '0');
            builder.Append('\n');
        }

        builder.Append("tags\n");
        foreach (var tag in CanonicalResultTags(resultTags))
        {
            builder.Append(tag.TagId);
            builder.Append('|');
            builder.Append((int)tag.Origin);
            builder.Append('|');
            builder.Append(tag.TagName);
            builder.Append('\n');
        }

        builder.Append(markdown);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    private static bool SnapshotsMatch(
        IReadOnlyList<NoteAssemblySourceSnapshot> expected,
        IReadOnlyList<NoteAssemblySourceSnapshot> actual)
    {
        if (expected.Count != actual.Count)
        {
            return false;
        }

        for (int i = 0; i < expected.Count; i++)
        {
            var a = expected[i];
            var b = actual[i];
            if (a.NoteId != b.NoteId ||
                a.Order != b.Order ||
                a.UpdatedAt.Ticks != b.UpdatedAt.Ticks ||
                !string.Equals(a.ContentHash, b.ContentHash, StringComparison.Ordinal) ||
                a.IsProtected != b.IsProtected ||
                a.IsDeleted != b.IsDeleted)
            {
                return false;
            }
        }

        return true;
    }

    private static bool ResultTagsMatch(
        IReadOnlyList<NoteAssemblyTagDto> expected,
        IReadOnlyList<NoteAssemblyTagDto> actual)
    {
        var left = CanonicalResultTags(expected);
        var right = CanonicalResultTags(actual);
        if (left.Count != right.Count)
        {
            return false;
        }

        for (int i = 0; i < left.Count; i++)
        {
            if (left[i].TagId != right[i].TagId ||
                left[i].Origin != right[i].Origin ||
                !string.Equals(left[i].TagName, right[i].TagName, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static NoteAssemblyPreviewResult InvalidPreview(
        string reason,
        string title,
        string separator,
        IReadOnlyList<NoteAssemblySourceSnapshot>? snapshots = null)
    {
        return new NoteAssemblyPreviewResult
        {
            IsValid = false,
            BlockingReason = reason,
            Title = title,
            NormalizedSeparator = separator,
            SeparatorKind = NoteAssemblySeparatorKind.BlankLine,
            AssembledMarkdown = string.Empty,
            Sources = snapshots ?? Array.Empty<NoteAssemblySourceSnapshot>(),
            ResultTags = Array.Empty<NoteAssemblyTagDto>(),
            PlanHash = string.Empty
        };
    }

    private static void InvokeTestHook(NoteAssemblyTransactionPhase phase)
    {
        TestInjectFailure?.Invoke(phase);
    }
}
