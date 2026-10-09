namespace QuickNotes.App.ViewModels;

public class TagSelectionItemViewModel : ViewModelBase
{
    private bool _isSelected;

    public int TagId { get; init; }
    public string TagName { get; init; } = string.Empty;

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}
