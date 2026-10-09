using System;
using System.Collections.Generic;
using System.Linq;
using QuickNotes.App.Models;

namespace QuickNotes.App.ViewModels;

public class TemplateItemViewModel : ViewModelBase
{
    public int Id { get; init; }
    public string Title { get; init; } = string.Empty;
    public string Text { get; init; } = string.Empty;
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }
    public IReadOnlyList<string> TagNames { get; init; } = Array.Empty<string>();
    public IReadOnlyList<int> TagIds { get; init; } = Array.Empty<int>();

    public bool HasTags => TagNames.Count > 0;
    public string TagsSummary => HasTags ? string.Join(", ", TagNames) : "Без тегов";

    public string Snippet
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Text))
                return "(Текст шаблона пуст)";

            var firstLine = Text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
            return firstLine.Length > 80 ? firstLine.Substring(0, 77) + "..." : firstLine;
        }
    }

    public string FormattedDate => $"Изменён: {UpdatedAt:dd.MM.yyyy HH:mm}";

    public static TemplateItemViewModel FromModel(NoteTemplate template)
    {
        var tagNames = template.TemplateTags
            .Select(tt => tt.Tag?.Name)
            .Where(name => !string.IsNullOrEmpty(name))
            .Select(name => name!)
            .ToList();

        var tagIds = template.TemplateTags
            .Select(tt => tt.TagId)
            .ToList();

        return new TemplateItemViewModel
        {
            Id = template.Id,
            Title = template.Title,
            Text = template.Text,
            CreatedAt = template.CreatedAt,
            UpdatedAt = template.UpdatedAt,
            TagNames = tagNames,
            TagIds = tagIds
        };
    }
}
