using System;
using System.Windows.Input;
using QuickNotes.App.Helpers;

namespace QuickNotes.App.ViewModels;

public class NoteLinkItemViewModel : ViewModelBase
{
    private readonly Action<int>? _onOpen;

    public int TargetNoteId { get; }
    public string IdText => $"#{TargetNoteId}";
    public string Title { get; }
    public bool IsAvailable { get; }

    public string TitleTooltip => IsAvailable ? Title : "Заметка недоступна или перемещена в корзину";
    public string OpenTooltip => IsAvailable ? $"Открыть заметку #{TargetNoteId}" : "Заметка недоступна";

    public ICommand OpenCommand { get; }

    public NoteLinkItemViewModel(int targetNoteId, string title, bool isAvailable, Action<int>? onOpen = null)
    {
        TargetNoteId = targetNoteId;
        Title = title;
        IsAvailable = isAvailable;
        _onOpen = onOpen;

        OpenCommand = new RelayCommand(
            () => _onOpen?.Invoke(TargetNoteId),
            () => IsAvailable);
    }
}
