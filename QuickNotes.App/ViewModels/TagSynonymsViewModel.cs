using System;
using System.Collections.ObjectModel;
using System.Windows.Input;
using QuickNotes.App.Helpers;
using QuickNotes.App.Models;

namespace QuickNotes.App.ViewModels;

public class TagSynonymsViewModel : ViewModelBase
{
    private string _newSynonymValue = string.Empty;

    public Tag Tag { get; }
    public string Title => $"Синонимы для тега «{Tag.Name}»";

    public string NewSynonymValue
    {
        get => _newSynonymValue;
        set => SetProperty(ref _newSynonymValue, value);
    }

    public ObservableCollection<TagSynonym> Synonyms { get; } = new();

    public ICommand AddSynonymCommand { get; }
    public ICommand DeleteSynonymCommand { get; }
    public ICommand CloseCommand { get; }

    public event Action<bool?>? RequestClose;

    public TagSynonymsViewModel(Tag tag)
    {
        Tag = tag;
        foreach (var syn in tag.Synonyms)
        {
            Synonyms.Add(syn);
        }

        AddSynonymCommand = new RelayCommand(AddSynonym);
        DeleteSynonymCommand = new RelayCommand(param => DeleteSynonym(param as TagSynonym));
        CloseCommand = new RelayCommand(() => RequestClose?.Invoke(true));
    }

    private void AddSynonym()
    {
        if (string.IsNullOrWhiteSpace(NewSynonymValue))
            return;

        var val = NewSynonymValue.Trim();
        if (Synonyms.Any(s => string.Equals(s.Value, val, StringComparison.OrdinalIgnoreCase)))
        {
            System.Windows.MessageBox.Show(
                "Такой синоним уже существует.",
                "Информация",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Information);
            return;
        }

        var newSyn = new TagSynonym
        {
            TagId = Tag.Id,
            Tag = Tag,
            Value = val
        };

        Synonyms.Add(newSyn);
        Tag.Synonyms.Add(newSyn);
        NewSynonymValue = string.Empty;
    }

    private void DeleteSynonym(TagSynonym? syn)
    {
        if (syn == null) return;
        Synonyms.Remove(syn);
        Tag.Synonyms.Remove(syn);
    }
}
