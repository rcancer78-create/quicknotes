using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using QuickNotes.App.Helpers;
using QuickNotes.App.Models;
using QuickNotes.App.Models.Sync;
using QuickNotes.App.Services;

namespace QuickNotes.App.ViewModels;

public class NoteCardViewModel : ViewModelBase
{
    private readonly Note _note;
    private bool _isExpanded;
    private bool _isCompact;
    private string _previewText = string.Empty;
    private string? _highlightQuery;
    private double _previewMaxHeight = 125;

    public Note Note => _note;

    public int Id => _note.Id;
    public string IdText => $"#{_note.Id}";

    /// <summary>True when the note is protected by a password (locked or unlocked).</summary>
    public bool IsProtected => _note.IsProtected;

    /// <summary>
    /// Neutral title for protected notes - never reveals the real title.
    /// </summary>
    public string DisplayTitle => _note.IsProtected
        ? "🔒 Защищённая заметка"
        : NoteTitleHelper.GetDisplayTitle(_note.Title, _note.Text, 72);

    public string Title => _note.IsProtected ? string.Empty : (_note.Title ?? string.Empty);

    public string Text => _note.IsProtected ? string.Empty : _note.Text;
    public string PreviewText => _note.IsProtected ? "Содержимое защищено паролем." : _previewText;
    public string? HighlightQuery => _highlightQuery;
    public double PreviewMaxHeight => _previewMaxHeight;
    public bool IsCompact => _isCompact;
    public string CreatedAtText => $"Создано: {_note.CreatedAt:dd.MM.yyyy HH:mm}";
    public string UpdatedAtText => $"Изменено: {_note.UpdatedAt:dd.MM.yyyy HH:mm}";
    public string UpdatedAtShortText => _note.UpdatedAt.ToString("dd.MM HH:mm");
    public string HeaderMetaTooltip
    {
        get
        {
            string text = $"{CreatedAtText}\n{UpdatedAtText}";
            if (!string.IsNullOrEmpty(CapturedAtText))
            {
                text += "\n" + CapturedAtText;
            }

            return text;
        }
    }

    public bool IsPinned => _note.IsPinned;
    public bool IsFavorite => _note.IsFavorite;
    public bool IsInbox => _note.IsInbox;
    public bool IsDeleted => _note.DeletedAt != null;
    public DateTime? DeletedAt => _note.DeletedAt;
    public string DeletedAtText => _note.DeletedAt.HasValue ? $"Удалено: {_note.DeletedAt.Value:dd.MM.yyyy HH:mm}" : string.Empty;
    public string DeletedAtShortText => _note.DeletedAt.HasValue ? _note.DeletedAt.Value.ToString("dd.MM HH:mm") : string.Empty;

    private LocalCommitSyncStatus _publicationStatus = LocalCommitSyncStatus.SavedLocally;
    public LocalCommitSyncStatus PublicationStatus
    {
        get => _publicationStatus;
        set
        {
            if (SetProperty(ref _publicationStatus, value))
            {
                OnPropertyChanged(nameof(PublicationStatusText));
                OnPropertyChanged(nameof(PublicationStatusIcon));
                OnPropertyChanged(nameof(PublicationStatusTooltip));
                OnPropertyChanged(nameof(ShowPublicationStatusOnCard));
            }
        }
    }

    public string PublicationStatusText => PublicationStatusCopy.FirstLevel(_publicationStatus);

    public string PublicationStatusIcon => PublicationStatusCopy.Icon(_publicationStatus);

    public string PublicationStatusTooltip => PublicationStatusCopy.Tooltip(_publicationStatus);

    /// <summary>
    /// Cloud/publication glyph on the card is reserved for actionable states.
    /// Quiet local-save and already-synced notes keep the title uncluttered.
    /// </summary>
    public bool ShowPublicationStatusOnCard => _publicationStatus is
        LocalCommitSyncStatus.PendingUpload
        or LocalCommitSyncStatus.Syncing
        or LocalCommitSyncStatus.Conflict
        or LocalCommitSyncStatus.Error;

    public string FavoriteSymbol => IsFavorite ? "★" : "☆";
    public string FavoriteTooltip => IsFavorite ? "Убрать из избранного" : "Добавить в избранное";
    public string PinTooltip => IsPinned ? "Открепить" : "Закрепить";
    public string PinSymbol => IsPinned ? "●" : "○";

    public string? SourceProcessName => _note.IsProtected ? null : _note.SourceProcessName;
    public string? SourceWindowTitle => _note.IsProtected ? null : _note.SourceWindowTitle;
    public string? SourceUrl => _note.IsProtected ? null : _note.SourceUrl;
    public DateTime? CapturedAt => _note.CapturedAt;
    public string? CapturedAtText => _note.CapturedAt.HasValue ? $"Захвачено: {_note.CapturedAt.Value:dd.MM.yyyy HH:mm}" : null;

    public string SourceText => _note.IsProtected ? string.Empty : NoteSourceContext.FormatCompactSource(_note.SourceProcessName, _note.SourceWindowTitle, _note.SourceUrl);
    public bool HasSourceText => !_note.IsProtected && !string.IsNullOrWhiteSpace(SourceText);

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (SetProperty(ref _isExpanded, value))
            {
                RefreshPreview();
                OnPropertyChanged(nameof(ExpandButtonText));
                OnPropertyChanged(nameof(ExpandButtonTooltip));
                OnPropertyChanged(nameof(ExpandButtonAutomationName));
            }
        }
    }

    public string ExpandButtonText => CardGestureCopy.ButtonText(IsExpanded);
    public string ExpandButtonTooltip => CardGestureCopy.ButtonTooltip(IsExpanded);
    public string ExpandButtonAutomationName => CardGestureCopy.ButtonAutomationName(IsExpanded);

    public ObservableCollection<string> Tags { get; } = new();

    public ICommand ToggleExpandedCommand { get; }
    public ICommand? TogglePinCommand { get; set; }
    public ICommand? ToggleFavoriteCommand { get; set; }
    public ICommand? MarkProcessedCommand { get; set; }
    public ICommand? MarkProcessedAndNextCommand { get; set; }
    public ICommand? MoveToTrashCommand { get; set; }
    public ICommand? RestoreCommand { get; set; }
    public ICommand? PermanentDeleteCommand { get; set; }

    public NoteCardViewModel(Note note, string? highlightQuery = null, bool compact = false)
    {
        _note = note;
        _highlightQuery = highlightQuery;
        _isCompact = compact;
        ToggleExpandedCommand = new RelayCommand(() => IsExpanded = !IsExpanded);
        RefreshTags();
        RefreshPreview();
    }

    public void ApplyPresentation(string? highlightQuery, bool compact)
    {
        _highlightQuery = highlightQuery;
        _isCompact = compact;
        OnPropertyChanged(nameof(HighlightQuery));
        OnPropertyChanged(nameof(IsCompact));
        RefreshPreview();
    }

    public void RefreshState()
    {
        OnPropertyChanged(nameof(IsProtected));
        OnPropertyChanged(nameof(IsPinned));
        OnPropertyChanged(nameof(IsFavorite));
        OnPropertyChanged(nameof(IsInbox));
        OnPropertyChanged(nameof(IsDeleted));
        OnPropertyChanged(nameof(DeletedAtText));
        OnPropertyChanged(nameof(DeletedAtShortText));
        OnPropertyChanged(nameof(UpdatedAtShortText));
        OnPropertyChanged(nameof(HeaderMetaTooltip));
        OnPropertyChanged(nameof(FavoriteSymbol));
        OnPropertyChanged(nameof(FavoriteTooltip));
        OnPropertyChanged(nameof(PinTooltip));
        OnPropertyChanged(nameof(PinSymbol));
        OnPropertyChanged(nameof(Text));
        OnPropertyChanged(nameof(DisplayTitle));
        OnPropertyChanged(nameof(UpdatedAtText));
        OnPropertyChanged(nameof(ShowPublicationStatusOnCard));
        OnPropertyChanged(nameof(SourceText));
        OnPropertyChanged(nameof(HasSourceText));
        OnPropertyChanged(nameof(CapturedAtText));
        OnPropertyChanged(nameof(PublicationStatus));
        OnPropertyChanged(nameof(PublicationStatusText));
        OnPropertyChanged(nameof(PublicationStatusIcon));
        OnPropertyChanged(nameof(PublicationStatusTooltip));
        RefreshTags();
        RefreshPreview();
    }

    public void RefreshTags()
    {
        Tags.Clear();
        if (_note.IsProtected)
        {
            // For protected notes do not reveal tag names in the card.
            return;
        }

        var activeTags = _note.NoteTags
            .Where(nt => !nt.IsSuppressed && nt.Tag != null)
            .Select(nt => nt.Tag.Name)
            .Distinct()
            .OrderBy(name => name);

        foreach (var tag in activeTags)
        {
            Tags.Add(tag);
        }
    }

    private void RefreshPreview()
    {
        int maxChars = _isCompact ? SearchPreview.CompactMaxChars : SearchPreview.ComfortMaxChars;
        _previewText = SearchPreview.BuildSnippet(_note.Text, _highlightQuery, maxChars, _isExpanded);
        _previewMaxHeight = _isExpanded ? double.PositiveInfinity : (_isCompact ? 72 : 125);
        OnPropertyChanged(nameof(PreviewText));
        OnPropertyChanged(nameof(PreviewMaxHeight));
        OnPropertyChanged(nameof(HighlightQuery));
    }
}
