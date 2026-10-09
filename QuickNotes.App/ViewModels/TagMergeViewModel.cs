using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using QuickNotes.App.Data;
using QuickNotes.App.Helpers;
using QuickNotes.App.Models;
using QuickNotes.App.Services;

namespace QuickNotes.App.ViewModels;

public class TagMergeViewModel : ViewModelBase
{
    private readonly Func<QuickNotesDbContext> _contextFactory;
    private readonly TagMergeService _tagMergeService;
    private readonly List<Tag> _allTags;
    private Tag? _selectedTargetTag;
    private TagMergePreviewResult? _preview;

    public Tag SourceTag { get; }
    public string Title => $"Объединение тега «{SourceTag.Name}»";
    public string SourceTagName => SourceTag.Name;
    public string SourceTagFullPath { get; }

    public ObservableCollection<Tag> AvailableTargets { get; } = new();

    public Tag? SelectedTargetTag
    {
        get => _selectedTargetTag;
        set
        {
            if (SetProperty(ref _selectedTargetTag, value))
            {
                UpdatePreview();
            }
        }
    }

    public TagMergePreviewResult? Preview
    {
        get => _preview;
        private set
        {
            if (SetProperty(ref _preview, value))
            {
                OnPropertyChanged(nameof(CanConfirm));
                OnPropertyChanged(nameof(AffectedNotesText));
                OnPropertyChanged(nameof(DuplicatesText));
                OnPropertyChanged(nameof(SynonymsText));
                OnPropertyChanged(nameof(ChildTagsText));
                OnPropertyChanged(nameof(StatusExplanation));
            }
        }
    }

    public bool CanConfirm => SelectedTargetTag != null && (Preview?.CanMerge ?? false);

    public string WarningMessage =>
        $"Внимание: Исходный тег «{SourceTag.Name}» будет безвозвратно удалён после переноса данных. Это действие нельзя отменить.";

    public string AffectedNotesText => Preview != null ? Preview.AffectedActiveNotesCount.ToString() : "—";

    public string DuplicatesText => Preview != null ? Preview.DuplicateNoteTagsCount.ToString() : "—";

    public string SynonymsText => Preview != null
        ? (Preview.UniqueSynonymsCount < Preview.SourceSynonymsCount
            ? $"{Preview.SourceSynonymsCount} ({Preview.UniqueSynonymsCount} уникальных к переносу)"
            : $"{Preview.SourceSynonymsCount} (все уникальны)")
        : "—";

    public string ChildTagsText => Preview != null
        ? (Preview.ChildTagsCount > 0
            ? $"{Preview.ChildTagsCount} (будут перемещены под целевой тег)"
            : "0")
        : "—";

    public string StatusExplanation
    {
        get
        {
            if (AvailableTargets.Count == 0)
            {
                return "Нет доступных тегов для объединения. Нельзя объединить тег сам с собой или со своими потомками.";
            }
            if (SelectedTargetTag == null)
            {
                return "Выберите целевой тег из списка для предварительного просмотра последствий.";
            }
            return Preview?.StatusExplanation ?? string.Empty;
        }
    }

    public ICommand ConfirmCommand { get; }
    public ICommand CancelCommand { get; }

    public event Action<bool?>? RequestClose;

    public TagMergeViewModel(
        Tag sourceTag,
        List<Tag> allTags,
        Func<QuickNotesDbContext> contextFactory,
        TagMergeService? tagMergeService = null)
    {
        SourceTag = sourceTag;
        _allTags = allTags;
        _contextFactory = contextFactory;
        _tagMergeService = tagMergeService ?? new TagMergeService();

        SourceTagFullPath = TagHierarchyService.GetFullPath(sourceTag.Id, allTags);

        var targets = _tagMergeService.GetAvailableTargets(sourceTag.Id, allTags);
        foreach (var t in targets)
        {
            AvailableTargets.Add(t);
        }

        ConfirmCommand = new RelayCommand(Confirm, () => CanConfirm);
        CancelCommand = new RelayCommand(() => RequestClose?.Invoke(false));

        if (AvailableTargets.Count > 0)
        {
            SelectedTargetTag = AvailableTargets[0];
        }
        else
        {
            UpdatePreview();
        }
    }

    private void UpdatePreview()
    {
        if (SelectedTargetTag == null)
        {
            Preview = null;
            return;
        }

        using var db = _contextFactory();
        Preview = _tagMergeService.CalculatePreview(db, SourceTag.Id, SelectedTargetTag.Id, _allTags);
    }

    private void Confirm()
    {
        if (!CanConfirm) return;
        RequestClose?.Invoke(true);
    }
}
