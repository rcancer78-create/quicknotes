using System;
using System.Windows.Input;
using QuickNotes.App.Helpers;

namespace QuickNotes.App.ViewModels;

/// <summary>
/// Name editor for a saved workspace view. Editing the name never touches notes,
/// revisions, or Sync; the caller persists the local view list only.
/// </summary>
public class SavedViewEditViewModel : ViewModelBase
{
    private string _viewName = string.Empty;

    public string Title { get; }

    public string ViewName
    {
        get => _viewName;
        set => SetProperty(ref _viewName, value);
    }

    public ICommand SaveCommand { get; }
    public ICommand CancelCommand { get; }

    public event Action<bool?>? RequestClose;

    public SavedViewEditViewModel(string title, string initialName = "")
    {
        Title = title;
        ViewName = initialName;

        SaveCommand = new RelayCommand(Save);
        CancelCommand = new RelayCommand(() => RequestClose?.Invoke(false));
    }

    private void Save()
    {
        if (string.IsNullOrWhiteSpace(ViewName))
        {
            System.Windows.MessageBox.Show(
                "Название вида не может быть пустым.",
                "Предупреждение",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Warning);
            return;
        }

        RequestClose?.Invoke(true);
    }
}
