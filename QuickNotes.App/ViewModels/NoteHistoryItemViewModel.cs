using System;
using System.Collections.Generic;
using System.Linq;
using QuickNotes.App.Models;

namespace QuickNotes.App.ViewModels;

public class NoteHistoryItemViewModel : ViewModelBase
{
    public int Id { get; }
    public int NoteId { get; }
    public DateTime CreatedAt { get; }
    public string CreatedAtFormatted => CreatedAt.ToString("dd.MM.yyyy HH:mm:ss");
    public string Title { get; }
    public string DisplayTitle => QuickNotes.App.Helpers.NoteTitleHelper.GetDisplayTitle(Title, Text);
    public string Text { get; }
    public IReadOnlyList<NoteRevisionTagSnapshot> Tags { get; }
    public string DiffSummary { get; }
    public bool IsCorrupted { get; }
    public bool CanRestore => !IsCorrupted;

    public string TagsSummary
    {
        get
        {
            if (IsCorrupted)
            {
                return "[Данные тегов повреждены / недоступны]";
            }

            var active = Tags.Where(t => !t.IsSuppressed).Select(t => t.TagName).ToList();
            var suppressed = Tags.Where(t => t.IsSuppressed).Select(t => t.TagName).ToList();

            var parts = new List<string>();
            if (active.Count > 0)
            {
                parts.Add("Теги: " + string.Join(", ", active));
            }
            else
            {
                parts.Add("Теги: нет");
            }

            if (suppressed.Count > 0)
            {
                parts.Add("Скрыты: " + string.Join(", ", suppressed));
            }

            return string.Join(" | ", parts);
        }
    }

    public NoteHistoryItemViewModel(NoteHistoryItemDto dto)
    {
        Id = dto.Id;
        NoteId = dto.NoteId;
        CreatedAt = dto.CreatedAt;
        Title = dto.Title ?? string.Empty;
        Text = dto.Text;
        Tags = dto.Tags;
        DiffSummary = dto.DiffSummary;
        IsCorrupted = dto.IsCorrupted;
    }
}
