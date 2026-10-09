using QuickNotes.App.Models;

namespace QuickNotes.App.ViewModels;

/// <summary>
/// Presentation wrapper for a <see cref="SavedWorkspaceView"/> in the navigation panel.
/// The wrapped model is the persisted filter query; applying the item never copies
/// notes or creates revisions / Sync packets.
/// </summary>
public class SavedViewItemViewModel : ViewModelBase
{
    private SavedWorkspaceView _view;
    private bool _isSelected;

    public SavedViewItemViewModel(SavedWorkspaceView view)
    {
        _view = view;
    }

    public SavedWorkspaceView View => _view;

    public void UpdateFrom(SavedWorkspaceView view)
    {
        _view = view;
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Tooltip));
    }

    public System.Guid Id => _view.Id;

    public string Name => _view.Name;

    public string Title => _view.Name;

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public string Tooltip =>
        $"Сохранённый вид «{_view.Name}» — фильтр, а не копия заметок. Применение не меняет заметки.";
}
