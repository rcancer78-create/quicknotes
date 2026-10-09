using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using QuickNotes.App.Helpers;
using QuickNotes.App.Models;

namespace QuickNotes.App.ViewModels;

public class TagSuggestionsViewModel : ViewModelBase
{
    private TagSuggestionItem? _selectedSuggestion;

    public string Title => "Предложения новых тегов";

    public string InfoBannerText =>
        "💡 Теги не создаются автоматически. QuickNotes находит слова, часто повторяющиеся в ваших заметках. Выберите предложение и нажмите «Создать тег», чтобы добавить его в дерево тегов.";

    public ObservableCollection<TagSuggestionItem> Suggestions { get; } = new();

    public TagSuggestionItem? SelectedSuggestion
    {
        get => _selectedSuggestion;
        set
        {
            if (SetProperty(ref _selectedSuggestion, value))
            {
                OnPropertyChanged(nameof(CanCreateTag));
                OnPropertyChanged(nameof(SelectedSuggestionSummary));
                OnPropertyChanged(nameof(SelectedTagName));
            }
        }
    }

    public bool CanCreateTag => SelectedSuggestion != null;

    public string SelectedTagName => SelectedSuggestion?.Word ?? string.Empty;

    public string SelectedSuggestionSummary => SelectedSuggestion != null
        ? $"Выбран тег: «{SelectedSuggestion.Word}» (встречается в {SelectedSuggestion.NotesCount} заметках). Будет создан корневой тег."
        : "Выберите слово из списка выше для создания тега.";

    public bool HasSuggestions => Suggestions.Count > 0;

    public Visibility HasSuggestionsVisibility => HasSuggestions ? Visibility.Visible : Visibility.Collapsed;

    public Visibility NoSuggestionsVisibility => HasSuggestions ? Visibility.Collapsed : Visibility.Visible;

    public string StatusText => HasSuggestions
        ? $"Найдено кандидатов в теги: {Suggestions.Count}"
        : "Подходящих повторяющихся слов не найдено (слово должно встречаться минимум в 2 заметках).";

    public ICommand CreateTagCommand { get; }
    public ICommand CancelCommand { get; }

    public event Action<bool?>? RequestClose;

    public TagSuggestionsViewModel(IEnumerable<TagSuggestionItem>? suggestions = null)
    {
        if (suggestions != null)
        {
            foreach (var item in suggestions)
            {
                Suggestions.Add(item);
            }
        }

        CreateTagCommand = new RelayCommand(Confirm, () => CanCreateTag);
        CancelCommand = new RelayCommand(Cancel);

        if (Suggestions.Count > 0)
        {
            SelectedSuggestion = Suggestions[0];
        }
    }

    private void Confirm()
    {
        if (!CanCreateTag) return;
        RequestClose?.Invoke(true);
    }

    private void Cancel()
    {
        RequestClose?.Invoke(false);
    }
}
