using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using QuickNotes.App.Helpers;
using QuickNotes.App.Models;
using QuickNotes.App.Services;

namespace QuickNotes.App.ViewModels;

public class TemplateEditViewModel : ViewModelBase
{
    private readonly INoteTemplateService _templateService;
    private string _title = string.Empty;
    private string _text = string.Empty;
    private string? _errorMessage;

    public int? EditingId { get; }
    public string DialogTitle => EditingId.HasValue ? "Редактирование шаблона" : "Новый шаблон";

    public string Title
    {
        get => _title;
        set
        {
            if (SetProperty(ref _title, value))
            {
                if (!string.IsNullOrEmpty(ErrorMessage))
                {
                    ErrorMessage = null;
                }
            }
        }
    }

    public string Text
    {
        get => _text;
        set
        {
            if (SetProperty(ref _text, value))
            {
                if (!string.IsNullOrEmpty(ErrorMessage))
                {
                    ErrorMessage = null;
                }
            }
        }
    }

    public string? ErrorMessage
    {
        get => _errorMessage;
        set
        {
            if (SetProperty(ref _errorMessage, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    public bool HasError => !string.IsNullOrEmpty(_errorMessage);

    public ObservableCollection<TagSelectionItemViewModel> AvailableTags { get; } = new();

    public IReadOnlyList<int> SelectedTagIds => AvailableTags
        .Where(t => t.IsSelected)
        .Select(t => t.TagId)
        .ToList();

    public ICommand SaveCommand { get; }
    public ICommand CancelCommand { get; }
    public Action<bool?>? RequestClose { get; set; }

    public TemplateEditViewModel(
        INoteTemplateService templateService,
        IEnumerable<Tag> allTags,
        TemplateItemViewModel? existingTemplate = null)
    {
        _templateService = templateService;

        if (existingTemplate != null)
        {
            EditingId = existingTemplate.Id;
            _title = existingTemplate.Title;
            _text = existingTemplate.Text;
            var selectedTagIdSet = existingTemplate.TagIds.ToHashSet();

            foreach (var tag in allTags.OrderBy(t => t.Name))
            {
                AvailableTags.Add(new TagSelectionItemViewModel
                {
                    TagId = tag.Id,
                    TagName = tag.Name,
                    IsSelected = selectedTagIdSet.Contains(tag.Id)
                });
            }
        }
        else
        {
            foreach (var tag in allTags.OrderBy(t => t.Name))
            {
                AvailableTags.Add(new TagSelectionItemViewModel
                {
                    TagId = tag.Id,
                    TagName = tag.Name,
                    IsSelected = false
                });
            }
        }

        SaveCommand = new RelayCommand(Save);
        CancelCommand = new RelayCommand(Cancel);
    }

    private void Save()
    {
        var error = _templateService.Validate(Title, Text, SelectedTagIds, EditingId);
        if (error != null)
        {
            ErrorMessage = error;
            return;
        }

        ErrorMessage = null;
        RequestClose?.Invoke(true);
    }

    private void Cancel()
    {
        RequestClose?.Invoke(false);
    }
}
