using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using QuickNotes.App.Data;
using QuickNotes.App.Models;

namespace QuickNotes.App.Services;

public interface INoteTemplateService
{
    IReadOnlyList<NoteTemplate> GetAllTemplates();
    NoteTemplate? GetTemplateById(int id);
    string? Validate(string? title, string? text, IEnumerable<int>? tagIds = null, int? excludeId = null);
    NoteTemplateResult CreateTemplate(string title, string text, IEnumerable<int>? tagIds = null);
    NoteTemplateResult UpdateTemplate(int id, string title, string text, IEnumerable<int>? tagIds = null);
    bool DeleteTemplate(int id);
    event Action? TemplateChanged;
}

public class NoteTemplateService : INoteTemplateService
{
    public event Action? TemplateChanged;
    public const int MaxTitleLength = 100;
    public const int MaxTextLength = 50_000;

    private static readonly Regex WhitespaceCollapseRegex = new(@"\s+", RegexOptions.Compiled);

    public static string NormalizeTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return string.Empty;

        return WhitespaceCollapseRegex.Replace(title.Trim(), " ");
    }

    private readonly Func<QuickNotesDbContext> _contextFactory;

    public NoteTemplateService(Func<QuickNotesDbContext>? contextFactory = null)
    {
        _contextFactory = contextFactory ?? (() => new QuickNotesDbContext());
    }

    public IReadOnlyList<NoteTemplate> GetAllTemplates()
    {
        using var db = _contextFactory();
        return db.NoteTemplates
            .Include(t => t.TemplateTags)
                .ThenInclude(tt => tt.Tag)
            .OrderBy(t => t.Title)
            .ToList();
    }

    public NoteTemplate? GetTemplateById(int id)
    {
        using var db = _contextFactory();
        return db.NoteTemplates
            .Include(t => t.TemplateTags)
                .ThenInclude(tt => tt.Tag)
            .FirstOrDefault(t => t.Id == id);
    }

    public string? Validate(string? title, string? text, IEnumerable<int>? tagIds = null, int? excludeId = null)
    {
        var normalizedTitle = NormalizeTitle(title);
        if (string.IsNullOrWhiteSpace(normalizedTitle))
        {
            return "Название шаблона не может быть пустым.";
        }

        if (normalizedTitle.Length > MaxTitleLength)
        {
            return $"Длина названия не должна превышать {MaxTitleLength} символов (введено: {normalizedTitle.Length}).";
        }

        if (text != null && text.Length > MaxTextLength)
        {
            return $"Длина текста не должна превышать {MaxTextLength} символов (введено: {text.Length}).";
        }

        using var db = _contextFactory();

        // Check title uniqueness case-insensitively and whitespace-normalized
        var allTitles = db.NoteTemplates
            .Where(t => !excludeId.HasValue || t.Id != excludeId.Value)
            .Select(t => t.Title)
            .ToList();

        if (allTitles.Any(t => string.Equals(NormalizeTitle(t), normalizedTitle, StringComparison.OrdinalIgnoreCase)))
        {
            return "Шаблон с таким названием уже существует.";
        }

        // Validate tag IDs if provided
        if (tagIds != null)
        {
            var uniqueTagIds = tagIds.Distinct().ToList();
            if (uniqueTagIds.Count > 0)
            {
                var existingTagIds = db.Tags
                    .Where(t => uniqueTagIds.Contains(t.Id))
                    .Select(t => t.Id)
                    .ToHashSet();

                if (uniqueTagIds.Any(id => !existingTagIds.Contains(id)))
                {
                    return "Один или несколько выбранных тегов не существуют.";
                }
            }
        }

        return null;
    }

    public static bool IsUniqueConstraintViolation(Exception? ex)
    {
        var current = ex;
        while (current != null)
        {
            if (current is SqliteException sqlEx && (sqlEx.SqliteErrorCode == 19 || sqlEx.SqliteExtendedErrorCode == 2067))
                return true;
            if (current.Message.IndexOf("UNIQUE constraint failed", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
            current = current.InnerException;
        }
        return false;
    }

    public NoteTemplateResult CreateTemplate(string title, string text, IEnumerable<int>? tagIds = null)
    {
        var normalizedTitle = NormalizeTitle(title);
        var validationError = Validate(normalizedTitle, text, tagIds);
        if (validationError != null)
        {
            return NoteTemplateResult.Fail(validationError);
        }

        try
        {
            using var db = _contextFactory();
            var template = new NoteTemplate
            {
                Title = normalizedTitle,
                Text = text ?? string.Empty,
                CreatedAt = DateTime.Now,
                UpdatedAt = DateTime.Now
            };

            if (tagIds != null)
            {
                var uniqueTagIds = tagIds.Distinct().ToList();
                foreach (var tagId in uniqueTagIds)
                {
                    template.TemplateTags.Add(new NoteTemplateTag
                    {
                        TagId = tagId
                    });
                }
            }

            db.NoteTemplates.Add(template);
            db.SaveChanges();
            TemplateChanged?.Invoke();

            return NoteTemplateResult.Ok(template);
        }
        catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex))
        {
            return NoteTemplateResult.Fail("Шаблон с таким названием уже существует.");
        }
        catch (Exception ex)
        {
            ErrorLogService.Write("NoteTemplateService.CreateTemplate", ex);
            return NoteTemplateResult.Fail($"Ошибка сохранения шаблона: {ex.Message}");
        }
    }

    public NoteTemplateResult UpdateTemplate(int id, string title, string text, IEnumerable<int>? tagIds = null)
    {
        var normalizedTitle = NormalizeTitle(title);
        var validationError = Validate(normalizedTitle, text, tagIds, excludeId: id);
        if (validationError != null)
        {
            return NoteTemplateResult.Fail(validationError);
        }

        try
        {
            using var db = _contextFactory();
            var template = db.NoteTemplates
                .Include(t => t.TemplateTags)
                .FirstOrDefault(t => t.Id == id);

            if (template == null)
            {
                return NoteTemplateResult.Fail("Шаблон не найден.");
            }

            template.Title = normalizedTitle;
            template.Text = text ?? string.Empty;
            template.UpdatedAt = DateTime.Now;

            // Synchronize tags
            var newTagIds = (tagIds ?? Enumerable.Empty<int>()).Distinct().ToHashSet();
            var currentTagIds = template.TemplateTags.Select(tt => tt.TagId).ToHashSet();

            var toRemove = template.TemplateTags.Where(tt => !newTagIds.Contains(tt.TagId)).ToList();
            foreach (var r in toRemove)
            {
                db.NoteTemplateTags.Remove(r);
            }

            foreach (var newTagId in newTagIds)
            {
                if (!currentTagIds.Contains(newTagId))
                {
                    template.TemplateTags.Add(new NoteTemplateTag
                    {
                        TemplateId = id,
                        TagId = newTagId
                    });
                }
            }

            db.SaveChanges();
            TemplateChanged?.Invoke();
            return NoteTemplateResult.Ok(template);
        }
        catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex))
        {
            return NoteTemplateResult.Fail("Шаблон с таким названием уже существует.");
        }
        catch (Exception ex)
        {
            ErrorLogService.Write("NoteTemplateService.UpdateTemplate", ex);
            return NoteTemplateResult.Fail($"Ошибка обновления шаблона: {ex.Message}");
        }
    }

    public bool DeleteTemplate(int id)
    {
        try
        {
            using var db = _contextFactory();
            var template = db.NoteTemplates
                .Include(t => t.TemplateTags)
                .FirstOrDefault(t => t.Id == id);

            if (template == null)
            {
                return false;
            }

            db.NoteTemplates.Remove(template);
            db.SaveChanges();
            TemplateChanged?.Invoke();
            return true;
        }
        catch (Exception ex)
        {
            ErrorLogService.Write("NoteTemplateService.DeleteTemplate", ex);
            return false;
        }
    }
}
