using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using QuickNotes.App.Helpers;
using QuickNotes.App.Models;
using QuickNotes.App.Services;

namespace QuickNotes.App.ViewModels;

public class TemplateManagementViewModel : ViewModelBase
{
    private readonly INoteTemplateService _templateService;
    private readonly Func<IReadOnlyList<Tag>> _tagsProvider;
    private TemplateItemViewModel? _selectedTemplate;

    public ObservableCollection<TemplateItemViewModel> Templates { get; } = new();

    public TemplateItemViewModel? SelectedTemplate
    {
        get => _selectedTemplate;
        set
        {
            if (SetProperty(ref _selectedTemplate, value))
            {
                OnPropertyChanged(nameof(CanEditOrDelete));
            }
        }
    }

    public bool CanEditOrDelete => SelectedTemplate != null;
    public bool HasTemplates => Templates.Count > 0;
    public bool IsEmpty => !HasTemplates;

    public ICommand CreateCommand { get; }
    public ICommand EditCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand CloseCommand { get; }

    public Func<TemplateEditViewModel, bool?>? RequestTemplateEditDialog { get; set; }
    public Func<string, string, bool>? ConfirmDeleteHandler { get; set; }
    public Action<string, string>? AlertHandler { get; set; }
    public Action<bool?>? RequestClose { get; set; }

    public TemplateManagementViewModel(
        INoteTemplateService templateService,
        Func<IReadOnlyList<Tag>> tagsProvider)
    {
        _templateService = templateService;
        _tagsProvider = tagsProvider;

        CreateCommand = new RelayCommand(CreateTemplate);
        EditCommand = new RelayCommand(EditTemplate, () => CanEditOrDelete);
        DeleteCommand = new RelayCommand(DeleteTemplate, () => CanEditOrDelete);
        CloseCommand = new RelayCommand(() => RequestClose?.Invoke(true));

        ReloadTemplates();
    }

    public void ReloadTemplates()
    {
        var currentSelectedId = SelectedTemplate?.Id;
        Templates.Clear();

        var models = _templateService.GetAllTemplates();
        foreach (var m in models)
        {
            Templates.Add(TemplateItemViewModel.FromModel(m));
        }

        OnPropertyChanged(nameof(HasTemplates));
        OnPropertyChanged(nameof(IsEmpty));

        if (currentSelectedId.HasValue)
        {
            SelectedTemplate = Templates.FirstOrDefault(t => t.Id == currentSelectedId.Value);
        }

        if (SelectedTemplate == null)
        {
            SelectedTemplate = Templates.FirstOrDefault();
        }
    }

    public void CreateTemplate()
    {
        var tags = _tagsProvider();
        var vm = new TemplateEditViewModel(_templateService, tags);

        bool? result = RequestTemplateEditDialog != null
            ? RequestTemplateEditDialog(vm)
            : false;

        if (result == true)
        {
            var saveResult = _templateService.CreateTemplate(vm.Title, vm.Text, vm.SelectedTagIds);
            if (!saveResult.Success)
            {
                ShowAlert(saveResult.ErrorMessage ?? "Не удалось сохранить шаблон.", "Ошибка сохранения");
                return;
            }

            ReloadTemplates();
            if (saveResult.Template != null)
            {
                SelectedTemplate = Templates.FirstOrDefault(t => t.Id == saveResult.Template.Id);
            }
        }
    }

    public void EditTemplate()
    {
        if (SelectedTemplate == null) return;

        var tags = _tagsProvider();
        var vm = new TemplateEditViewModel(_templateService, tags, SelectedTemplate);

        bool? result = RequestTemplateEditDialog != null
            ? RequestTemplateEditDialog(vm)
            : false;

        if (result == true)
        {
            var saveResult = _templateService.UpdateTemplate(SelectedTemplate.Id, vm.Title, vm.Text, vm.SelectedTagIds);
            if (!saveResult.Success)
            {
                ShowAlert(saveResult.ErrorMessage ?? "Не удалось обновить шаблон.", "Ошибка сохранения");
                return;
            }

            var updatedId = SelectedTemplate.Id;
            ReloadTemplates();
            SelectedTemplate = Templates.FirstOrDefault(t => t.Id == updatedId);
        }
    }

    public void DeleteTemplate()
    {
        if (SelectedTemplate == null) return;

        var title = SelectedTemplate.Title;
        bool confirmed = ConfirmDeleteHandler != null
            ? ConfirmDeleteHandler($"Вы уверены, что хотите удалить шаблон «{title}»?", "Подтверждение удаления шаблона")
            : false;

        if (confirmed)
        {
            bool deleted = _templateService.DeleteTemplate(SelectedTemplate.Id);
            if (!deleted)
            {
                ShowAlert("Не удалось удалить выбранный шаблон.", "Ошибка удаления");
                return;
            }

            ReloadTemplates();
        }
    }

    private void ShowAlert(string message, string title)
    {
        if (AlertHandler != null)
        {
            AlertHandler(message, title);
        }
        else
        {
            System.Windows.MessageBox.Show(message, title, System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
        }
    }
}
