using System;
using System.Windows.Input;
using QuickNotes.App.Helpers;

namespace QuickNotes.App.ViewModels;

public class TagEditViewModel : ViewModelBase
{
    private string _tagName = string.Empty;

    public string Title { get; }

    public string TagName
    {
        get => _tagName;
        set => SetProperty(ref _tagName, value);
    }

    public ICommand SaveCommand { get; }
    public ICommand CancelCommand { get; }

    public event Action<bool?>? RequestClose;

    public TagEditViewModel(string title, string initialName = "")
    {
        Title = title;
        TagName = initialName;

        SaveCommand = new RelayCommand(Save);
        CancelCommand = new RelayCommand(() => RequestClose?.Invoke(false));
    }

    private void Save()
    {
        if (string.IsNullOrWhiteSpace(TagName))
        {
            System.Windows.MessageBox.Show(
                "Имя тега не может быть пустым.",
                "Предупреждение",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Warning);
            return;
        }

        RequestClose?.Invoke(true);
    }
}
