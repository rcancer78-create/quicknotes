using System.Globalization;
using QuickNotes.App.Models;

namespace QuickNotes.App.ViewModels;

public sealed class TaskCardViewModel : ViewModelBase
{
    public TaskCardViewModel(MarkdownTaskLocator locator)
    {
        Locator = locator;
    }

    public MarkdownTaskLocator Locator { get; }

    public int NoteId => Locator.NoteId;
    public string TaskText => Locator.TaskText;
    public string NoteTitle => string.IsNullOrWhiteSpace(Locator.NoteTitle) ? "Без заголовка" : Locator.NoteTitle;
    public bool HasDueDate => Locator.DueDate.HasValue;
    public string DueDateText => Locator.DueDate.HasValue
        ? Locator.DueDate.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
        : string.Empty;
    public string AccessibilityName
    {
        get
        {
            if (HasDueDate)
            {
                return $"Задача: {TaskText}, срок {DueDateText}, заметка {NoteTitle}";
            }

            return $"Задача: {TaskText}, заметка {NoteTitle}";
        }
    }
}
