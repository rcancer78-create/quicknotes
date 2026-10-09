using System;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using QuickNotes.App.Helpers;
using QuickNotes.App.Models;

namespace QuickNotes.App.ViewModels;

public class TagRescanPreviewViewModel : ViewModelBase
{
    private TagRescanPreviewResult _preview;

    public TagRescanPreviewResult Preview
    {
        get => _preview;
        private set
        {
            if (SetProperty(ref _preview, value))
            {
                OnPropertyChanged(nameof(TotalNotesScannedText));
                OnPropertyChanged(nameof(TotalNotesWithChangesText));
                OnPropertyChanged(nameof(TotalAddedTagsText));
                OnPropertyChanged(nameof(TotalRemovedTagsText));
                OnPropertyChanged(nameof(HasChanges));
                OnPropertyChanged(nameof(CanConfirm));
                OnPropertyChanged(nameof(CancelButtonText));
                OnPropertyChanged(nameof(StatusMessage));
                OnPropertyChanged(nameof(HasChangesVisibility));
                OnPropertyChanged(nameof(NoChangesVisibility));
                OnPropertyChanged(nameof(ConfirmButtonVisibility));
            }
        }
    }

    public ObservableCollection<TagRescanNoteChange> ChangedNotes { get; } = new();

    public string Title => "Предварительный просмотр пересканирования тегов";

    public string WarningMessage =>
        "Внимание: будут изменены только автоматически назначенные теги. Теги, добавленные вручную, и скрытые автотеги останутся без изменений.";

    public string TotalNotesScannedText => Preview.TotalNotesScanned.ToString();

    public string TotalNotesWithChangesText => Preview.TotalNotesWithChanges.ToString();

    public string TotalAddedTagsText => Preview.TotalAddedTags > 0 ? $"+{Preview.TotalAddedTags}" : "0";

    public string TotalRemovedTagsText => Preview.TotalRemovedTags > 0 ? $"-{Preview.TotalRemovedTags}" : "0";

    public bool HasChanges => Preview.HasChanges;

    public bool CanConfirm => Preview.HasChanges;

    public string CancelButtonText => HasChanges ? "Отмена" : "Закрыть";

    public Visibility HasChangesVisibility => HasChanges ? Visibility.Visible : Visibility.Collapsed;

    public Visibility NoChangesVisibility => HasChanges ? Visibility.Collapsed : Visibility.Visible;

    public Visibility ConfirmButtonVisibility => CanConfirm ? Visibility.Visible : Visibility.Collapsed;

    public string StatusMessage => HasChanges
        ? $"К пересканированию: {Preview.TotalNotesWithChanges} заметок. Будет добавлено автотегов: {Preview.TotalAddedTags}, удалено: {Preview.TotalRemovedTags}."
        : "Изменений нет: все автотеги заметок соответствуют текущему тексту.";

    public ICommand ConfirmCommand { get; }
    public ICommand CancelCommand { get; }

    public event Action<bool?>? RequestClose;

    public TagRescanPreviewViewModel(TagRescanPreviewResult preview)
    {
        _preview = preview ?? new TagRescanPreviewResult();

        foreach (var change in _preview.NoteChanges)
        {
            ChangedNotes.Add(change);
        }

        ConfirmCommand = new RelayCommand(Confirm, () => CanConfirm);
        CancelCommand = new RelayCommand(Cancel);
    }

    private void Confirm()
    {
        if (!CanConfirm) return;
        RequestClose?.Invoke(true);
    }

    private void Cancel()
    {
        RequestClose?.Invoke(false);
    }
}
