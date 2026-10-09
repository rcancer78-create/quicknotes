using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Documents;
using System.Windows.Input;
using QuickNotes.App.Data;
using QuickNotes.App.Helpers;
using QuickNotes.App.Models;
using QuickNotes.App.Models.DraftJournal;
using QuickNotes.App.Services;
using QuickNotes.App.Services.DraftJournal;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;
using MessageBoxResult = System.Windows.MessageBoxResult;

namespace QuickNotes.App.ViewModels;

public class NoteEditorTagItem : ViewModelBase
{
    private string _reason = string.Empty;
    private TagOrigin _origin = TagOrigin.Auto;
    private bool _isSuppressed;

    public int TagId { get; set; }
    public string TagName { get; set; } = string.Empty;

    public TagOrigin Origin
    {
        get => _origin;
        set
        {
            if (SetProperty(ref _origin, value))
            {
                OnPropertyChanged(nameof(OriginMarker));
                OnPropertyChanged(nameof(OriginTooltip));
                OnPropertyChanged(nameof(ChipTooltip));
            }
        }
    }

    public bool IsSuppressed
    {
        get => _isSuppressed;
        set
        {
            if (SetProperty(ref _isSuppressed, value))
            {
                OnPropertyChanged(nameof(OriginMarker));
                OnPropertyChanged(nameof(OriginTooltip));
                OnPropertyChanged(nameof(ChipTooltip));
            }
        }
    }

    public string OriginMarker => TagOriginCopy.Marker(Origin, IsSuppressed);
    public string OriginTooltip => TagOriginCopy.Tooltip(Origin, IsSuppressed);
    public string ChipTooltip =>
        string.IsNullOrWhiteSpace(Reason)
            ? OriginTooltip
            : OriginTooltip + Environment.NewLine + Reason;

    public NoteEditorTagItem() { }

    public NoteEditorTagItem(int tagId, string tagName, TagOrigin origin = TagOrigin.Auto, string reason = "")
    {
        TagId = tagId;
        TagName = tagName;
        Origin = origin;
        Reason = reason;
    }

    public string Reason
    {
        get => _reason;
        set
        {
            if (SetProperty(ref _reason, value))
            {
                OnPropertyChanged(nameof(ChipTooltip));
            }
        }
    }
}

public class NoteEditorViewModel : ViewModelBase, IDisposable
{
    private const string AutoTagWithoutCurrentMatchReason = "автотег сохранён ранее (совпадение сейчас не найдено)";

    private readonly TagDetectionService _tagDetectionService;
    private readonly List<Tag> _allTags;
    private readonly MarkdownPreviewService _markdownPreviewService;
    private readonly IReadOnlyDictionary<int, TagRule>? _rules;
    private readonly INoteHistoryService _noteHistoryService;
    private readonly Func<QuickNotesDbContext> _contextFactory;
    private readonly INoteLinkService _noteLinkService;
    private readonly IAttachmentStorageService _attachmentStorageService;
    private readonly Services.Sync.ILocalMutationCoordinator? _mutationCoordinator;
    internal Services.Sync.ILocalMutationCoordinator? MutationCoordinatorForTests => _mutationCoordinator;
    private readonly SettingsService _settingsService;
    private readonly Services.NoteProtection.INoteProtectionService _noteProtectionService;
    private readonly IDraftJournalService _draftJournalService;
    private readonly System.Windows.Threading.Dispatcher? _dispatcher;
    private readonly System.Threading.Timer? _debounceTimer;
    private readonly object _draftLock = new();
    private readonly CancellationTokenSource _disposeCts = new();
    private Task _pendingSaveTask = Task.CompletedTask;
    private readonly object _saveTaskLock = new();
    private long _sequenceNumber;
    private string _draftId = string.Empty;
    private TimeSpan _debounceDelay = TimeSpan.FromMilliseconds(500);
    private bool _isInitialized;
    private bool _isDisposed;
    private bool _hasUncommittedDraftRecovery;
    private DraftRecoveryPromptArgs? _draftRecoveryInfo;
    private readonly List<NoteAttachmentItemViewModel> _deletedAttachments = new();
    private IReadOnlyList<int> _lastOutgoingIds = Array.Empty<int>();
    private string _title = string.Empty;
    private string _text = string.Empty;
    private Tag? _selectedTagToAdd;

    public Note? ExistingNote { get; }
    public IDraftJournalService DraftJournalService => _draftJournalService;
    public string DraftId => _draftId;
    public TimeSpan DebounceDelay { get => _debounceDelay; set => _debounceDelay = value; }
    public bool HasUncommittedDraftRecovery { get => _hasUncommittedDraftRecovery; private set => SetProperty(ref _hasUncommittedDraftRecovery, value); }
    public DraftRecoveryPromptArgs? DraftRecoveryInfo { get => _draftRecoveryInfo; private set => SetProperty(ref _draftRecoveryInfo, value); }
    public Func<DraftRecoveryPromptArgs, DraftRecoveryChoice>? RequestDraftRecoveryPrompt { get; set; }
    public ICommand PromptDraftRecoveryCommand { get; }
    public ICommand RestoreDraftCommand { get; }
    public ICommand DiscardDraftCommand { get; }
    public Func<UnsavedEditorDecision>? RequestUnsavedEditorDecision { get; set; }
    public Func<bool>? RequestDraftDiscardDecision { get; set; }
    public Action<bool>? SetOcrHotkeySuspended { get; set; }
    public Action<string, string, MessageBoxImage>? AlertHandler { get; set; }

    /// <summary>True when this note is protected and currently unlocked in this process.</summary>
    public bool IsNoteUnlocked { get; private set; }

    /// <summary>True when this note is protected (regardless of unlock state).</summary>
    public bool IsNoteProtected { get; private set; }

    public Func<QuickNotesDbContext> ContextFactory => _contextFactory;
    public TagDetectionService TagDetectionService => _tagDetectionService;
    public List<Tag> AllTags => _allTags;
    public IReadOnlyDictionary<int, TagRule>? Rules => _rules;
    public INoteHistoryService NoteHistoryService => _noteHistoryService;
    public INoteLinkService NoteLinkService => _noteLinkService;
    public IAttachmentStorageService AttachmentStorageService => _attachmentStorageService;
    public SettingsService SettingsService => _settingsService;
    public Services.NoteProtection.INoteProtectionService NoteProtectionService => _noteProtectionService;

    public event Action? NoteSaved;
    public void NotifyNoteSaved() => NoteSaved?.Invoke();

    /// <summary>Marks the note as locked in this editor instance (used by the lock action).</summary>
    public void MarkLocked()
    {
        IsNoteUnlocked = false;
        RefreshProtectionState();
    }

    /// <summary>Requests the editor window to close (used by the lock action).</summary>
    public void RequestCloseEditor() => RequestClose?.Invoke(false);

    public ObservableCollection<NoteAttachmentItemViewModel> Attachments { get; } = new();
    public IReadOnlyList<NoteAttachmentItemViewModel> DeletedAttachments => _deletedAttachments;
    public bool HasAttachments => Attachments.Count > 0;
    public string AttachmentsCountText => Attachments.Count > 0 ? $"({Attachments.Count})" : string.Empty;
    public string AttachmentsEmptyText => "Нет прикреплённых файлов";

    public ICommand AddAttachmentCommand { get; }
    public ICommand RemoveAttachmentCommand { get; }
    public Func<string?>? PickAttachmentFile { get; set; }

    private bool _isPinned;
    private bool _isFavorite;
    private bool _isInbox;
    private DateTime? _deletedAt;
    private MarkdownViewMode _viewMode = MarkdownViewMode.Edit;
    private CancellationTokenSource? _previewCts;
    private readonly HashSet<string> _collapsedHeadingAnchors = new(StringComparer.OrdinalIgnoreCase);
    private bool _isOutlineOpen;
    private FlowDocument? _previewDocument;
    private bool _isHistoryOpen;
    private NoteHistoryItemViewModel? _selectedRevision;

    public int? NoteId { get; }
    public string WindowTitle => NoteId.HasValue ? $"Редактирование заметки #{NoteId.Value}" : "Новая заметка";
    public bool CanDelete => NoteId.HasValue;
    public bool IsPermanentlyDeleted { get; private set; }
    public bool IsDeleted => IsPermanentlyDeleted;

    // ------------------------------------------------------------------
    // Password protection actions
    // ------------------------------------------------------------------

    public bool IsInline { get; set; }

    public ICommand ProtectNoteCommand { get; }
    public ICommand LockNoteCommand { get; }
    public ICommand UnlockNoteCommand { get; }
    public ICommand ChangePasswordCommand { get; }
    public ICommand RemoveProtectionCommand { get; }

    public bool CanProtect => NoteId.HasValue && !IsNoteProtected;
    public bool CanLock => IsNoteProtected && IsNoteUnlocked;
    public bool CanUnlock => IsNoteProtected && !IsNoteUnlocked;
    public bool IsLocked => IsNoteProtected && !IsNoteUnlocked;
    public bool CanChangePassword => IsNoteProtected && IsNoteUnlocked;
    public bool CanRemoveProtection => IsNoteProtected && IsNoteUnlocked;

    public string ProtectButtonText => IsNoteProtected ? "🔒 Защищено" : "🔒 Защитить паролем";
    public string LockButtonText => "🔒 Заблокировать";
    public string UnlockButtonText => "🔓 Ввести пароль";
    public string ChangePasswordButtonText => "🔑 Изменить пароль";
    public string RemoveProtectionButtonText => "🔓 Снять защиту";

    public Func<string, string, string?>? RequestPasswordDialog { get; set; }
    public Func<string, string, string?>? RequestSetPasswordDialog { get; set; }
    public Action? RequestLockNote { get; set; }
    public Action? RequestProtectionChanged { get; set; }

    public string Title
    {
        get => _title;
        set
        {
            if (SetProperty(ref _title, value))
            {
                OnPropertyChanged(nameof(DisplayTitle));
                ScheduleDraftSave();
            }
        }
    }

    public string DisplayTitle => QuickNotes.App.Helpers.NoteTitleHelper.GetDisplayTitle(Title, Text);

    public string Text
    {
        get => _text;
        set
        {
            if (SetProperty(ref _text, value))
            {
                OnPropertyChanged(nameof(DisplayTitle));
                if (IsPreviewVisible)
                {
                    SchedulePreviewUpdate();
                }
                else
                {
                    UpdateOutline();
                }
                if (IsFindOpen)
                {
                    UpdateFindMatches();
                }
                UpdateOutgoingLinksIfChanged();
                ScheduleDraftSave();
            }
        }
    }

    // ------------------------------------------------------------------
    // Find in current note & highlighting
    // ------------------------------------------------------------------

    private bool _isFindOpen;
    private string _findText = string.Empty;
    private readonly List<(int Start, int Length)> _findMatches = new();
    private int _currentMatchIndex = -1;
    private string? _highlightQuery;

    public bool IsFindOpen
    {
        get => _isFindOpen;
        set
        {
            if (SetProperty(ref _isFindOpen, value))
            {
                if (IsPreviewVisible)
                {
                    UpdatePreviewDocument();
                }
            }
        }
    }

    public string FindText
    {
        get => _findText;
        set
        {
            if (SetProperty(ref _findText, value))
            {
                OnFindTextChanged();
            }
        }
    }

    public string? HighlightQuery
    {
        get => _highlightQuery;
        set
        {
            if (SetProperty(ref _highlightQuery, value))
            {
                if (IsPreviewMode)
                {
                    UpdatePreviewDocument();
                }
            }
        }
    }

    public IReadOnlyList<(int Start, int Length)> FindMatches => _findMatches;

    public int CurrentMatchIndex
    {
        get => _currentMatchIndex;
        private set
        {
            if (SetProperty(ref _currentMatchIndex, value))
            {
                OnPropertyChanged(nameof(CurrentMatchDisplayIndex));
                OnPropertyChanged(nameof(MatchCountText));
            }
        }
    }

    public int CurrentMatchDisplayIndex => _currentMatchIndex >= 0 ? _currentMatchIndex + 1 : 0;
    public int TotalMatchCount => _findMatches.Count;

    public string MatchCountText
    {
        get
        {
            if (string.IsNullOrEmpty(_findText)) return string.Empty;
            if (_findMatches.Count == 0) return "0/0";
            return $"{CurrentMatchDisplayIndex}/{_findMatches.Count}";
        }
    }

    public ICommand OpenFindCommand { get; }
    public ICommand CloseFindCommand { get; }
    public ICommand FindNextCommand { get; }
    public ICommand FindPreviousCommand { get; }

    public Action<int, int>? RequestSelectMatch { get; set; }
    public Action? RequestFocusFind { get; set; }
    public Action? RequestFocusEditor { get; set; }

    public void OpenFind(string? initialQuery = null)
    {
        IsFindOpen = true;
        if (!string.IsNullOrEmpty(initialQuery))
        {
            _findText = initialQuery;
            OnPropertyChanged(nameof(FindText));
        }
        else if (string.IsNullOrEmpty(_findText) && !string.IsNullOrEmpty(_highlightQuery))
        {
            _findText = _highlightQuery;
            OnPropertyChanged(nameof(FindText));
        }
        UpdateFindMatches();
        if (_findMatches.Count > 0)
        {
            SelectMatch(0);
        }
        else
        {
            CurrentMatchIndex = -1;
        }
        if (IsPreviewVisible)
        {
            UpdatePreviewDocument();
        }
        RequestFocusFind?.Invoke();
    }

    public void CloseFind()
    {
        IsFindOpen = false;
        CurrentMatchIndex = -1;
        if (IsPreviewVisible)
        {
            UpdatePreviewDocument();
        }
        RequestFocusEditor?.Invoke();
    }

    public void FindNext()
    {
        if (_findMatches.Count == 0) return;
        int next = (_currentMatchIndex + 1) % _findMatches.Count;
        SelectMatch(next);
    }

    public void FindPrevious()
    {
        if (_findMatches.Count == 0) return;
        int prev = (_currentMatchIndex - 1 + _findMatches.Count) % _findMatches.Count;
        SelectMatch(prev);
    }

    private void SelectMatch(int index)
    {
        if (index < 0 || index >= _findMatches.Count) return;
        CurrentMatchIndex = index;
        var m = _findMatches[index];
        RequestSelectMatch?.Invoke(m.Start, m.Length);
    }

    private void OnFindTextChanged()
    {
        _highlightQuery = _findText;
        OnPropertyChanged(nameof(HighlightQuery));
        UpdateFindMatches();
        if (_findMatches.Count > 0)
        {
            SelectMatch(0);
        }
        else
        {
            CurrentMatchIndex = -1;
        }
        if (IsPreviewVisible)
        {
            UpdatePreviewDocument();
        }
    }

    public void UpdateFindMatches()
    {
        _findMatches.Clear();
        if (!string.IsNullOrEmpty(_findText) && !string.IsNullOrEmpty(_text))
        {
            int idx = 0;
            while (idx < _text.Length)
            {
                int match = _text.IndexOf(_findText, idx, StringComparison.OrdinalIgnoreCase);
                if (match < 0) break;
                _findMatches.Add((match, _findText.Length));
                idx = match + Math.Max(1, _findText.Length);
            }
        }
        OnPropertyChanged(nameof(TotalMatchCount));
        OnPropertyChanged(nameof(MatchCountText));
    }

    public void SetInitialHighlight(string? query)
    {
        if (!string.IsNullOrWhiteSpace(query))
        {
            _highlightQuery = query;
            _findText = query;
            OnPropertyChanged(nameof(HighlightQuery));
            OnPropertyChanged(nameof(FindText));
            UpdateFindMatches();
            if (IsPreviewVisible)
            {
                UpdatePreviewDocument();
            }
        }
    }

    public MarkdownViewMode ViewMode
    {
        get => _viewMode;
        set
        {
            if (SetProperty(ref _viewMode, value))
            {
                OnPropertyChanged(nameof(IsPreviewMode));
                OnPropertyChanged(nameof(IsEditMode));
                OnPropertyChanged(nameof(IsSplitMode));
                OnPropertyChanged(nameof(IsEditorVisible));
                OnPropertyChanged(nameof(IsPreviewVisible));
                if (IsPreviewVisible)
                {
                    UpdatePreviewDocument();
                }
            }
        }
    }

    public bool IsPreviewMode
    {
        get => _viewMode == MarkdownViewMode.Preview;
        set => ViewMode = value ? MarkdownViewMode.Preview : MarkdownViewMode.Edit;
    }

    public bool IsEditMode => _viewMode != MarkdownViewMode.Preview;
    public bool IsSplitMode => _viewMode == MarkdownViewMode.Split;
    public bool IsEditorVisible => _viewMode != MarkdownViewMode.Preview;
    public bool IsPreviewVisible => _viewMode != MarkdownViewMode.Edit;

    public bool IsOutlineOpen
    {
        get => _isOutlineOpen;
        set => SetProperty(ref _isOutlineOpen, value);
    }

    public ObservableCollection<MarkdownOutlineItem> OutlineItems { get; } = new();
    public bool HasOutline => OutlineItems.Count > 0;
    public IReadOnlySet<string> CollapsedHeadingAnchors => _collapsedHeadingAnchors;

    public Func<(int Start, int Length)>? GetSelection { get; set; }
    public Action<int, int>? SetSelection { get; set; }
    public event Action<MarkdownOutlineItem>? RequestNavigateToOutline;

    public FlowDocument? PreviewDocument
    {
        get => _previewDocument;
        private set => SetProperty(ref _previewDocument, value);
    }

    public void SchedulePreviewUpdate(int delayMs = 90)
    {
        _previewCts?.Cancel();
        var cts = new CancellationTokenSource();
        _previewCts = cts;

        Task.Delay(delayMs, cts.Token).ContinueWith(t =>
        {
            if (!t.IsCanceled && !cts.IsCancellationRequested)
            {
                var disp = _dispatcher ?? System.Windows.Application.Current?.Dispatcher;
                if (disp != null && !disp.HasShutdownStarted && !disp.CheckAccess())
                {
                    disp.InvokeAsync(UpdatePreviewDocument);
                }
                else
                {
                    UpdatePreviewDocument();
                }
            }
        }, cts.Token, TaskContinuationOptions.NotOnCanceled, TaskScheduler.Default);
    }

    public void UpdatePreviewDocument()
    {
        _previewCts?.Cancel();
        _previewCts = null;
        PreviewDocument = _markdownPreviewService.BuildFlowDocument(
            Text,
            _highlightQuery ?? (_isFindOpen ? _findText : null),
            _collapsedHeadingAnchors,
            ToggleHeadingCollapse,
            _attachmentStorageService);
        UpdateOutline();
    }

    public void UpdateOutline()
    {
        var items = _markdownPreviewService.ExtractOutline(Text);
        OutlineItems.Clear();
        foreach (var item in items)
        {
            OutlineItems.Add(item);
        }
        OnPropertyChanged(nameof(HasOutline));
    }

    public void ToggleHeadingCollapse(string anchorId)
    {
        if (string.IsNullOrWhiteSpace(anchorId)) return;
        if (!_collapsedHeadingAnchors.Remove(anchorId))
        {
            _collapsedHeadingAnchors.Add(anchorId);
        }
        UpdatePreviewDocument();
    }

    public void NavigateToOutlineItem(MarkdownOutlineItem? item)
    {
        if (item == null) return;
        RequestNavigateToOutline?.Invoke(item);
    }

    public void ApplyFormatting(Func<string?, int, int, FormattingResult> op)
    {
        if (IsLocked) return;
        var (start, len) = GetSelection?.Invoke() ?? (Text?.Length ?? 0, 0);
        var res = op(Text, start, len);
        Text = res.Text;
        SetSelection?.Invoke(res.SelectionStart, res.SelectionLength);
        if (IsPreviewVisible)
        {
            UpdatePreviewDocument();
        }
    }

    public bool IsPinned
    {
        get => _isPinned;
        set
        {
            if (SetProperty(ref _isPinned, value))
            {
                OnPropertyChanged(nameof(PinButtonText));
                OnPropertyChanged(nameof(PinIcon));
            }
        }
    }

    public bool IsFavorite
    {
        get => _isFavorite;
        set
        {
            if (SetProperty(ref _isFavorite, value))
            {
                OnPropertyChanged(nameof(FavoriteButtonText));
                OnPropertyChanged(nameof(FavoriteIcon));
            }
        }
    }

    public bool IsInbox
    {
        get => _isInbox;
        set
        {
            if (SetProperty(ref _isInbox, value))
            {
                OnPropertyChanged(nameof(IsMarkProcessedVisible));
            }
        }
    }

    public DateTime? DeletedAt
    {
        get => _deletedAt;
        set
        {
            if (SetProperty(ref _deletedAt, value))
            {
                OnPropertyChanged(nameof(IsInTrash));
                OnPropertyChanged(nameof(CanMoveToTrash));
            }
        }
    }

    public bool IsInTrash => _deletedAt != null;
    public bool CanMoveToTrash => NoteId.HasValue && !IsInTrash;
    public bool IsMarkProcessedVisible => IsInbox && !IsInTrash;

    public string PinButtonText => IsPinned ? "📌 Закреплено" : "📍 Закрепить";
    public string PinIcon => IsPinned ? "📌" : "📍";
    public string FavoriteButtonText => IsFavorite ? "★ В избранном" : "☆ В избранное";
    public string FavoriteIcon => IsFavorite ? "★" : "☆";

    public Tag? SelectedTagToAdd
    {
        get => _selectedTagToAdd;
        set => SetProperty(ref _selectedTagToAdd, value);
    }

    public ObservableCollection<NoteEditorTagItem> ActiveTags { get; } = new();
    public List<int> SuppressedTagIds { get; } = new();
    public ObservableCollection<NoteEditorTagItem> SuppressedTags { get; } = new();
    public bool HasSuppressedTags => SuppressedTags.Count > 0;
    public ObservableCollection<Tag> AvailableTagsToAdd { get; } = new();

    public bool IsHistoryOpen
    {
        get => _isHistoryOpen;
        set
        {
            if (SetProperty(ref _isHistoryOpen, value))
            {
                OnPropertyChanged(nameof(HistoryButtonText));
            }
        }
    }

    public string HistoryButtonText => _isHistoryOpen
        ? "🕒 Скрыть историю"
        : (Revisions.Count > 0 ? $"🕒 История ({Revisions.Count})" : "🕒 История");

    public ObservableCollection<NoteHistoryItemViewModel> Revisions { get; } = new();

    public NoteHistoryItemViewModel? SelectedRevision
    {
        get => _selectedRevision;
        set
        {
            if (SetProperty(ref _selectedRevision, value))
            {
                OnPropertyChanged(nameof(CanRestore));
            }
        }
    }

    public bool CanRestore => SelectedRevision != null && SelectedRevision.CanRestore;
    public bool HasRevisions => Revisions.Count > 0;
    public bool HasRestoredVersion { get; private set; }

    public ICommand SaveCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand MoveToTrashCommand { get; }
    public ICommand RestoreCommand { get; }
    public ICommand PermanentDeleteCommand { get; }
    public ICommand MarkProcessedCommand { get; }
    public ICommand MarkProcessedAndNextCommand { get; }
    public bool ContinueInboxAfterClose { get; private set; }
    public ICommand TogglePinCommand { get; }
    public ICommand ToggleFavoriteCommand { get; }

    public ICommand RemoveTagCommand { get; }
    public ICommand RestoreSuppressedTagCommand { get; }
    public ICommand AddTagCommand { get; }
    public ICommand RescanCommand { get; }

    public ICommand ShowTextModeCommand { get; }
    public ICommand ShowSplitModeCommand { get; }
    public ICommand ShowPreviewModeCommand { get; }
    public ICommand TogglePreviewModeCommand { get; }
    public ICommand ToggleOutlineCommand { get; }
    public ICommand NavigateToOutlineItemCommand { get; }

    public ICommand ToggleBoldCommand { get; }
    public ICommand ToggleItalicCommand { get; }
    public ICommand ToggleCodeCommand { get; }
    public ICommand ToggleHeadingCommand { get; }
    public ICommand ToggleBulletListCommand { get; }
    public ICommand ToggleNumberedListCommand { get; }
    public ICommand ToggleCheckboxCommand { get; }
    public ICommand InsertMarkdownLinkCommand { get; }
    public ICommand InsertTableCommand { get; }

    public ICommand ToggleHistoryCommand { get; }
    public ICommand RestoreVersionCommand { get; }

    public ObservableCollection<NoteLinkItemViewModel> OutgoingLinks { get; } = new();
    public ObservableCollection<NoteLinkItemViewModel> IncomingLinks { get; } = new();
    public bool HasOutgoingLinks => OutgoingLinks.Count > 0;
    public bool HasIncomingLinks => IncomingLinks.Count > 0;
    public string OutgoingLinksCountText => OutgoingLinks.Count > 0 ? $"({OutgoingLinks.Count})" : string.Empty;
    public string IncomingLinksCountText => IncomingLinks.Count > 0 ? $"({IncomingLinks.Count})" : string.Empty;
    public string IncomingLinksEmptyText => NoteId.HasValue ? "Нет ссылок на эту заметку" : "Для новой заметки входящие ссылки будут доступны после сохранения";

    public const string IncomingLinksTruncatedMessage = "Показана часть ссылок; уточните текст или сократите число заметок";

    private bool _isIncomingLinksTruncated;
    public bool IsIncomingLinksTruncated
    {
        get => _isIncomingLinksTruncated;
        private set
        {
            if (SetProperty(ref _isIncomingLinksTruncated, value))
            {
                OnPropertyChanged(nameof(IncomingLinksTruncatedText));
                OnPropertyChanged(nameof(HasIncomingLinksTruncated));
                OnPropertyChanged(nameof(IsIncomingLinksTruncatedVisible));
                OnPropertyChanged(nameof(ShowIncomingLinksTruncatedNotice));
            }
        }
    }

    public string IncomingLinksTruncatedText => IsIncomingLinksTruncated ? IncomingLinksTruncatedMessage : string.Empty;
    public bool HasIncomingLinksTruncated => IsIncomingLinksTruncated;
    public bool IsIncomingLinksTruncatedVisible => IsIncomingLinksTruncated;
    public bool ShowIncomingLinksTruncatedNotice => IsIncomingLinksTruncated;

    public ICommand InsertLinkCommand { get; }
    public Action<string>? InsertTextAtCaret { get; set; }
    public Func<int?>? PickLinkNoteId { get; set; }
    public Func<NoteLinkPickerItem?>? PickLinkedNote { get; set; }
    public event Action<int>? RequestOpenLinkedNote;

    public event Action<bool?>? RequestClose;
    public Action? Commit { get; set; }

    public string? SourceProcessName { get; set; }
    public string? SourceWindowTitle { get; set; }
    public string? SourceUrl { get; set; }
    public DateTime? CapturedAt { get; set; }

    public string SourceText => NoteSourceContext.FormatCompactSource(SourceProcessName, SourceWindowTitle, SourceUrl);
    public bool HasSourceText => !string.IsNullOrWhiteSpace(SourceText);
    public string? CapturedAtText => CapturedAt.HasValue ? $"Захвачено: {CapturedAt.Value:dd.MM.yyyy HH:mm}" : null;

    /// <summary>
    /// Applies common note metadata (pin, favorite, inbox, deleted/updated dates, tags, and attachment synchronization)
    /// without touching sensitive plaintext text or source context fields.
    /// </summary>
    public void ApplyMetadataAndLinks(Note note)
    {
        note.UpdatedAt = DateTime.Now;
        note.IsPinned = IsPinned;
        note.IsFavorite = IsFavorite;
        note.IsInbox = IsInbox;
        note.DeletedAt = DeletedAt;
        note.CapturedAt = CapturedAt;

        var desired = ActiveTags.Select(t => new NoteTag { TagId = t.TagId, Origin = t.Origin })
            .Concat(SuppressedTagIds.Select(id => new NoteTag { TagId = id, Origin = TagOrigin.Auto, IsSuppressed = true }))
            .ToDictionary(t => t.TagId);

        foreach (var link in note.NoteTags.ToList())
        {
            if (desired.Remove(link.TagId, out var updated))
            {
                link.Origin = updated.Origin;
                link.IsSuppressed = updated.IsSuppressed;
            }
            else
            {
                note.NoteTags.Remove(link);
            }
        }
        foreach (var link in desired.Values)
        {
            note.NoteTags.Add(link);
        }

        // Attachments synchronization: remove detached attachments from the entity
        var currentAttIds = Attachments.Where(a => a.Id > 0).Select(a => a.Id).ToHashSet();
        foreach (var att in note.Attachments.ToList())
        {
            if (!currentAttIds.Contains(att.Id))
            {
                note.Attachments.Remove(att);
            }
        }
    }

    public void ApplyToNote(Note note)
    {
        note.Title = Title ?? string.Empty;
        note.Text = Text;
        note.SourceProcessName = SourceProcessName;
        note.SourceWindowTitle = SourceWindowTitle;
        note.SourceUrl = SourceUrl;
        ApplyMetadataAndLinks(note);

        foreach (var item in Attachments.Where(a => a.Id == 0))
        {
            var entity = item.ToEntity();
            entity.NoteId = note.Id;
            note.Attachments.Add(entity);
        }
    }

    private void CommitAndClose() => TryCommitChanges(closeAfterSuccess: true);

    public bool TryCommitChanges(bool closeAfterSuccess = true)
    {
        if (string.IsNullOrWhiteSpace(Text))
        {
            ShowEditorAlert(UnsavedEditorPromptText.EmptyTextWarning, "Предупреждение", MessageBoxImage.Warning);
            return false;
        }

        try
        {
            if (IsInline)
            {
                Commit?.Invoke();
                _draftJournalService.DeleteJournalIfContentMatches(_draftId, Title, Text);
                CaptureBaseline();
                NotifyNoteSaved();
            }
            else
            {
                Dispose();
                Commit?.Invoke();
                _draftJournalService.DeleteJournalIfContentMatches(_draftId, Title, Text);
                if (closeAfterSuccess)
                {
                    RequestClose?.Invoke(true);
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            ErrorLogService.Write("NoteEditor.Commit", ex);
            IsPermanentlyDeleted = false;
            ShowEditorAlert(UnsavedEditorPromptText.SaveFailure, "Ошибка", MessageBoxImage.Error);
            return false;
        }
    }

    private void OnThemeChanged(bool isDark)
    {
        if (IsPreviewMode)
        {
            UpdatePreviewDocument();
        }
    }

    private static Func<QuickNotesDbContext> ResolveDefaultContextFactory() => () => new QuickNotesDbContext();

    public NoteEditorViewModel(
        TagDetectionService tagDetectionService,
        List<Tag> allTags,
        Note? existingNote = null,
        string? initialText = null,
        List<Tag>? initialAutoTags = null,
        string? sourceProcessName = null,
        string? sourceWindowTitle = null,
        string? sourceUrl = null,
        DateTime? capturedAt = null,
        MarkdownPreviewService? markdownPreviewService = null,
        List<TagDetectionMatch>? initialAutoMatches = null,
        IReadOnlyDictionary<int, TagRule>? rules = null,
        INoteHistoryService? noteHistoryService = null,
        Func<QuickNotesDbContext>? contextFactory = null,
        INoteLinkService? noteLinkService = null,
        IAttachmentStorageService? attachmentStorageService = null,
        SettingsService? settingsService = null,
        IEnumerable<Tag>? initialManualTags = null,
        Services.NoteProtection.INoteProtectionService? noteProtectionService = null,
        string? initialTitle = null,
        IDraftJournalService? draftJournalService = null,
        string? initialDraftId = null,
        Services.Sync.ILocalMutationCoordinator? mutationCoordinator = null)
    {
        _tagDetectionService = tagDetectionService;
        _allTags = allTags;
        _markdownPreviewService = markdownPreviewService ?? new MarkdownPreviewService();
        _rules = rules;
        _noteHistoryService = noteHistoryService ?? new NoteHistoryService();
        _contextFactory = contextFactory ?? ResolveDefaultContextFactory();
        _noteLinkService = noteLinkService ?? new NoteLinkService();
        _attachmentStorageService = attachmentStorageService ?? new AttachmentStorageService();
        _mutationCoordinator = mutationCoordinator;
        _settingsService = settingsService ?? new SettingsService();
        _noteProtectionService = noteProtectionService ?? new Services.NoteProtection.NoteProtectionService(attachmentStorage: _attachmentStorageService);
        _draftJournalService = draftJournalService ?? NoOpDraftJournalService.Instance;
        ExistingNote = existingNote;
        if (existingNote != null)
        {
            _draftId = _draftJournalService.GetDraftIdForNote(existingNote.Id);
        }
        else
        {
            _draftId = initialDraftId ?? _draftJournalService.GenerateNewDraftId();
        }

        _dispatcher = Thread.CurrentThread.GetApartmentState() == ApartmentState.STA
            ? System.Windows.Threading.Dispatcher.CurrentDispatcher
            : System.Windows.Threading.Dispatcher.FromThread(Thread.CurrentThread);
        _debounceTimer = new System.Threading.Timer(OnDebounceTimerFired, null, Timeout.Infinite, Timeout.Infinite);

        PromptDraftRecoveryCommand = new RelayCommand(() => CheckAndPromptDraftRecovery(forcePrompt: true));
        RestoreDraftCommand = new RelayCommand(() => { if (_draftRecoveryInfo != null) RestoreDraft(_draftRecoveryInfo); });
        DiscardDraftCommand = new RelayCommand(() =>
        {
            if (_draftRecoveryInfo == null)
            {
                return;
            }

            bool shouldDiscard = RequestDraftDiscardDecision?.Invoke() ?? DraftDiscardPrompt.Show(null);
            if (!shouldDiscard)
            {
                return;
            }

            DiscardDraft(_draftRecoveryInfo);
        });

        AddAttachmentCommand = new RelayCommand(AddAttachmentFromFilePicker);
        RemoveAttachmentCommand = new RelayCommand(param => RemoveAttachment(param as NoteAttachmentItemViewModel));

        InsertLinkCommand = new RelayCommand(InsertLink);
        SaveCommand = new RelayCommand(Save);
        CancelCommand = new RelayCommand(Cancel);
        DeleteCommand = new RelayCommand(Delete, () => CanDelete);
        MoveToTrashCommand = new RelayCommand(MoveToTrash, () => CanMoveToTrash);
        RestoreCommand = new RelayCommand(Restore, () => IsInTrash);
        PermanentDeleteCommand = new RelayCommand(PermanentDelete, () => IsInTrash);
        MarkProcessedCommand = new RelayCommand(MarkProcessed);
        MarkProcessedAndNextCommand = new RelayCommand(MarkProcessedAndNext);
        TogglePinCommand = new RelayCommand(() => IsPinned = !IsPinned);
        ToggleFavoriteCommand = new RelayCommand(() => IsFavorite = !IsFavorite);

        ShowTextModeCommand = new RelayCommand(() => ViewMode = MarkdownViewMode.Edit);
        ShowSplitModeCommand = new RelayCommand(() => ViewMode = MarkdownViewMode.Split);
        ShowPreviewModeCommand = new RelayCommand(() => ViewMode = MarkdownViewMode.Preview);
        TogglePreviewModeCommand = new RelayCommand(() => ViewMode = ViewMode == MarkdownViewMode.Preview ? MarkdownViewMode.Edit : MarkdownViewMode.Preview);
        ToggleOutlineCommand = new RelayCommand(() => IsOutlineOpen = !IsOutlineOpen);
        NavigateToOutlineItemCommand = new RelayCommand(param => NavigateToOutlineItem(param as MarkdownOutlineItem));

        ToggleBoldCommand = new RelayCommand(() => ApplyFormatting(MarkdownEditorOperations.ToggleBold));
        ToggleItalicCommand = new RelayCommand(() => ApplyFormatting(MarkdownEditorOperations.ToggleItalic));
        ToggleCodeCommand = new RelayCommand(() => ApplyFormatting(MarkdownEditorOperations.ToggleCode));
        ToggleHeadingCommand = new RelayCommand(() => ApplyFormatting((t, s, l) => MarkdownEditorOperations.ToggleHeading(t, s, l, 1)));
        ToggleBulletListCommand = new RelayCommand(() => ApplyFormatting(MarkdownEditorOperations.ToggleBulletList));
        ToggleNumberedListCommand = new RelayCommand(() => ApplyFormatting(MarkdownEditorOperations.ToggleNumberedList));
        ToggleCheckboxCommand = new RelayCommand(() => ApplyFormatting(MarkdownEditorOperations.ToggleCheckbox));
        InsertMarkdownLinkCommand = new RelayCommand(() => ApplyFormatting((t, s, l) => MarkdownEditorOperations.InsertLink(t, s, l)));
        InsertTableCommand = new RelayCommand(() => ApplyFormatting((t, s, l) => MarkdownEditorOperations.InsertTable(t, s, l, 3, 3)));

        ThemeService.ThemeChanged += OnThemeChanged;

        ToggleHistoryCommand = new RelayCommand(() => IsHistoryOpen = !IsHistoryOpen);
        RestoreVersionCommand = new RelayCommand(RestoreSelectedVersion, () => CanRestore);

        RemoveTagCommand = new RelayCommand(param => RemoveTag(param as NoteEditorTagItem));
        RestoreSuppressedTagCommand = new RelayCommand(param => RestoreSuppressedTag(param as NoteEditorTagItem));
        AddTagCommand = new RelayCommand(AddTag);
        RescanCommand = new RelayCommand(Rescan);

        ProtectNoteCommand = new RelayCommand(ProtectNote, () => CanProtect);
        LockNoteCommand = new RelayCommand(LockNote, () => CanLock);
        UnlockNoteCommand = new RelayCommand(UnlockNote, () => CanUnlock);
        ChangePasswordCommand = new RelayCommand(ChangePassword, () => CanChangePassword);
        RemoveProtectionCommand = new RelayCommand(RemoveProtection, () => CanRemoveProtection);

        OpenFindCommand = new RelayCommand(() => OpenFind());
        CloseFindCommand = new RelayCommand(() => CloseFind());
        FindNextCommand = new RelayCommand(() => FindNext());
        FindPreviousCommand = new RelayCommand(() => FindPrevious());

        if (existingNote != null)
        {
            NoteId = existingNote.Id;
            IsPinned = existingNote.IsPinned;
            IsFavorite = existingNote.IsFavorite;
            IsInbox = existingNote.IsInbox;
            DeletedAt = existingNote.DeletedAt;
            CapturedAt = existingNote.CapturedAt;

            if (existingNote.IsProtected)
            {
                IsNoteProtected = true;
                IsNoteUnlocked = _noteProtectionService.IsUnlocked(existingNote.Id);
                if (IsNoteUnlocked)
                {
                    var session = _noteProtectionService.GetSession(existingNote.Id);
                    if (session != null)
                    {
                        Title = session.Payload.Title ?? string.Empty;
                        Text = session.Payload.Text;
                        SourceProcessName = session.Payload.SourceProcessName;
                        SourceWindowTitle = session.Payload.SourceWindowTitle;
                        SourceUrl = session.Payload.SourceUrl;
                    }
                    else
                    {
                        Title = existingNote.Title ?? string.Empty;
                        Text = existingNote.Text;
                        SourceProcessName = existingNote.SourceProcessName;
                        SourceWindowTitle = existingNote.SourceWindowTitle;
                        SourceUrl = existingNote.SourceUrl;
                    }
                }
                else
                {
                    // The note is protected and locked. The MainViewModel prompts for a password
                    // before constructing the editor; if it did not, do not leak plaintext.
                    Title = string.Empty;
                    Text = string.Empty;
                    SourceProcessName = null;
                    SourceWindowTitle = null;
                    SourceUrl = null;
                }
            }
            else
            {
                Title = existingNote.Title ?? string.Empty;
                Text = existingNote.Text;
                SourceProcessName = existingNote.SourceProcessName;
                SourceWindowTitle = existingNote.SourceWindowTitle;
                SourceUrl = existingNote.SourceUrl;
            }

            var detailedMap = _tagDetectionService.DetectDetailedTags(Text, _allTags, _rules)
                .ToDictionary(m => m.Tag.Id);

            foreach (var nt in existingNote.NoteTags)
            {
                if (nt.IsSuppressed)
                {
                    SuppressedTagIds.Add(nt.TagId);
                }
                else
                {
                    var tag = nt.Tag ?? _allTags.FirstOrDefault(t => t.Id == nt.TagId);
                    if (tag != null)
                    {
                        string reason = nt.Origin == TagOrigin.Manual
                            ? TagOriginCopy.ManualReason
                            : (detailedMap.TryGetValue(nt.TagId, out var match) ? match.Reason : AutoTagWithoutCurrentMatchReason);

                        ActiveTags.Add(new NoteEditorTagItem
                        {
                            TagId = nt.TagId,
                            TagName = tag.Name,
                            Origin = nt.Origin,
                            IsSuppressed = false,
                            Reason = reason
                        });
                    }
                }
            }

            LoadAttachments(existingNote);
        }
        else
        {
            Title = initialTitle ?? string.Empty;
            Text = initialText ?? string.Empty;
            IsPinned = false;
            IsFavorite = false;
            IsInbox = false; // Regular editor creation does not go to inbox
            DeletedAt = null;
            SourceProcessName = sourceProcessName;
            SourceWindowTitle = sourceWindowTitle;
            SourceUrl = sourceUrl;
            CapturedAt = capturedAt;

            if (initialManualTags != null)
            {
                foreach (var tag in initialManualTags)
                {
                    if (!ActiveTags.Any(t => t.TagId == tag.Id))
                    {
                        ActiveTags.Add(new NoteEditorTagItem
                        {
                            TagId = tag.Id,
                            TagName = tag.Name,
                            Origin = TagOrigin.Manual,
                            IsSuppressed = false,
                            Reason = "из шаблона"
                        });
                    }
                }
            }

            if (initialAutoMatches != null && initialAutoMatches.Count > 0)
            {
                foreach (var match in initialAutoMatches)
                {
                    if (!ActiveTags.Any(t => t.TagId == match.Tag.Id))
                    {
                        ActiveTags.Add(new NoteEditorTagItem
                        {
                            TagId = match.Tag.Id,
                            TagName = match.Tag.Name,
                            Origin = TagOrigin.Auto,
                            IsSuppressed = false,
                            Reason = match.Reason
                        });
                    }
                }
            }
            else if (initialAutoTags != null && initialAutoTags.Count > 0)
            {
                var detailedMap = _tagDetectionService.DetectDetailedTags(Text, _allTags, _rules)
                    .ToDictionary(m => m.Tag.Id);

                foreach (var tag in initialAutoTags)
                {
                    if (!ActiveTags.Any(t => t.TagId == tag.Id))
                    {
                        string reason = detailedMap.TryGetValue(tag.Id, out var match)
                            ? match.Reason
                            : AutoTagWithoutCurrentMatchReason;

                        ActiveTags.Add(new NoteEditorTagItem
                        {
                            TagId = tag.Id,
                            TagName = tag.Name,
                            Origin = TagOrigin.Auto,
                            IsSuppressed = false,
                            Reason = reason
                        });
                    }
                }
            }
            else if (!string.IsNullOrWhiteSpace(Text))
            {
                var detected = _tagDetectionService.DetectDetailedTags(Text, _allTags, _rules);
                foreach (var match in detected)
                {
                    if (!ActiveTags.Any(t => t.TagId == match.Tag.Id) && !SuppressedTagIds.Contains(match.Tag.Id))
                    {
                        ActiveTags.Add(new NoteEditorTagItem
                        {
                            TagId = match.Tag.Id,
                            TagName = match.Tag.Name,
                            Origin = TagOrigin.Auto,
                            IsSuppressed = false,
                            Reason = match.Reason
                        });
                    }
                }
            }
        }

        RefreshAvailableTagsToAdd();
        RefreshSuppressedTags();
        LoadHistory();
        _lastOutgoingIds = _noteLinkService.ExtractLinkedNoteIds(_text);
        LoadLinks();
        CaptureBaseline();
        _isInitialized = true;
    }

    private string _baseline = string.Empty;

    public bool HasUnsavedChanges => Fingerprint() != _baseline;

    private void CaptureBaseline() => _baseline = Fingerprint();

    private string Fingerprint()
    {
        var tags = string.Join(",", ActiveTags.OrderBy(t => t.TagId).Select(t => $"{t.TagId}:{(int)t.Origin}"));
        var suppressed = string.Join(",", SuppressedTagIds.OrderBy(id => id));
        var atts = string.Join(",", Attachments.OrderBy(a => a.Sha256).Select(a => $"{a.Sha256}:{a.OriginalFileName}"));
        return $"{Title}\u001f{Text}\u001f{IsPinned}|{IsFavorite}|{IsInbox}|{DeletedAt?.Ticks}|{tags}|{suppressed}|{atts}";
    }

    public void RefreshAvailableTagsToAdd()
    {
        var activeIds = ActiveTags.Select(t => t.TagId).ToHashSet();
        AvailableTagsToAdd.Clear();

        foreach (var tag in _allTags.Where(t => !activeIds.Contains(t.Id)).OrderBy(t => t.Name))
        {
            AvailableTagsToAdd.Add(tag);
        }

        SelectedTagToAdd = AvailableTagsToAdd.FirstOrDefault();
    }

    private void RemoveTag(NoteEditorTagItem? item)
    {
        if (item == null) return;

        ActiveTags.Remove(item);

        if (item.Origin == TagOrigin.Auto)
        {
            if (!SuppressedTagIds.Contains(item.TagId))
            {
                SuppressedTagIds.Add(item.TagId);
            }
        }

        RefreshAvailableTagsToAdd();
        RefreshSuppressedTags();
        ScheduleDraftSave();
    }

    private void RestoreSuppressedTag(NoteEditorTagItem? item)
    {
        if (item == null) return;

        SuppressedTagIds.Remove(item.TagId);
        if (!ActiveTags.Any(t => t.TagId == item.TagId))
        {
            ActiveTags.Add(new NoteEditorTagItem
            {
                TagId = item.TagId,
                TagName = item.TagName,
                Origin = TagOrigin.Auto,
                IsSuppressed = false,
                Reason = TagOriginCopy.RestoredAutoReason
            });
        }

        RefreshAvailableTagsToAdd();
        RefreshSuppressedTags();
        ScheduleDraftSave();
    }

    private void AddTag()
    {
        if (SelectedTagToAdd == null) return;

        var tag = SelectedTagToAdd;
        SuppressedTagIds.Remove(tag.Id);

        ActiveTags.Add(new NoteEditorTagItem
        {
            TagId = tag.Id,
            TagName = tag.Name,
            Origin = TagOrigin.Manual,
            IsSuppressed = false,
            Reason = TagOriginCopy.ManualReason
        });

        RefreshAvailableTagsToAdd();
        RefreshSuppressedTags();
        ScheduleDraftSave();
    }

    private void RefreshSuppressedTags()
    {
        SuppressedTags.Clear();
        foreach (int id in SuppressedTagIds.Distinct().OrderBy(x => x))
        {
            var tag = _allTags.FirstOrDefault(t => t.Id == id);
            SuppressedTags.Add(new NoteEditorTagItem
            {
                TagId = id,
                TagName = tag?.Name ?? ("#" + id),
                Origin = TagOrigin.Auto,
                IsSuppressed = true,
                Reason = TagOriginCopy.Tooltip(TagOrigin.Auto, isSuppressed: true)
            });
        }

        OnPropertyChanged(nameof(HasSuppressedTags));
    }

    public void Rescan()
    {
        var detected = _tagDetectionService.DetectDetailedTags(Text, _allTags, _rules);
        var detectedMap = detected.ToDictionary(m => m.Tag.Id);
        var detectedIds = detectedMap.Keys.ToHashSet();

        var toRemove = ActiveTags
            .Where(t => t.Origin == TagOrigin.Auto && !detectedIds.Contains(t.TagId))
            .ToList();

        foreach (var item in toRemove)
        {
            ActiveTags.Remove(item);
        }

        foreach (var item in ActiveTags.Where(t => t.Origin == TagOrigin.Auto))
        {
            if (detectedMap.TryGetValue(item.TagId, out var match))
            {
                item.Reason = match.Reason;
            }
        }

        var currentIds = ActiveTags.Select(t => t.TagId).ToHashSet();
        foreach (var match in detected)
        {
            if (!currentIds.Contains(match.Tag.Id) && !SuppressedTagIds.Contains(match.Tag.Id))
            {
                ActiveTags.Add(new NoteEditorTagItem
                {
                    TagId = match.Tag.Id,
                    TagName = match.Tag.Name,
                    Origin = TagOrigin.Auto,
                    IsSuppressed = false,
                    Reason = match.Reason
                });
            }
        }

        RefreshAvailableTagsToAdd();
        ScheduleDraftSave();
    }

    private void Save() => TryCommitChanges(closeAfterSuccess: true);

    public UnsavedEditorDecision AskUnsavedEditorDecision()
    {
        if (RequestUnsavedEditorDecision != null)
        {
            return RequestUnsavedEditorDecision();
        }

        return UnsavedEditorPrompt.Show(System.Windows.Application.Current?.MainWindow);
    }

    public bool TryHandleUnsavedClose(out bool shouldCloseWithoutSave, bool closeWindowOnSave = true)
    {
        shouldCloseWithoutSave = false;
        if (!HasUnsavedChanges)
        {
            if (!NoteId.HasValue && string.IsNullOrWhiteSpace(Text) && string.IsNullOrWhiteSpace(Title))
            {
                ApplyDiscardUnsaved(deleteJournal: true);
            }
            else
            {
                CleanupUncommittedAttachments();
            }

            shouldCloseWithoutSave = true;
            return true;
        }

        var decision = AskUnsavedEditorDecision();
        if (decision == UnsavedEditorDecision.Stay)
        {
            return false;
        }

        if (decision == UnsavedEditorDecision.Save)
        {
            return TryCommitChanges(closeAfterSuccess: closeWindowOnSave);
        }

        if (!ApplyDiscardUnsaved(deleteJournal: true))
        {
            return false;
        }
        shouldCloseWithoutSave = true;
        return true;
    }

    public bool ApplyDiscardUnsaved(bool deleteJournal)
    {
        if (HasUncommittedDraftRecovery)
        {
            bool shouldDiscard = RequestDraftDiscardDecision?.Invoke() ?? DraftDiscardPrompt.Show(null);
            if (!shouldDiscard)
            {
                return false;
            }
        }

        if (deleteJournal)
        {
            _draftJournalService.DeleteJournal(_draftId);
        }

        CleanupUncommittedAttachments();
        return true;
    }

    private void Cancel()
    {
        if (!TryHandleUnsavedClose(out var shouldCloseWithoutSave))
        {
            return;
        }

        if (shouldCloseWithoutSave)
        {
            Dispose();
            RequestClose?.Invoke(false);
        }
    }

    private void ShowEditorAlert(string message, string title, MessageBoxImage image)
    {
        if (AlertHandler != null)
        {
            AlertHandler(message, title, image);
        }
        else
        {
            MessageBox.Show(message, title, MessageBoxButton.OK, image);
        }
    }

    private void LoadAttachments(Note existingNote)
    {
        Attachments.Clear();
        List<NoteAttachment>? atts = existingNote.Attachments?.ToList();
        if (atts == null || atts.Count == 0)
        {
            try
            {
                using var db = _contextFactory();
                atts = db.NoteAttachments.Where(a => a.NoteId == existingNote.Id).ToList();
            }
            catch (Exception ex)
            {
                ErrorLogService.Write("NoteEditor.LoadAttachments", ex);
            }
        }

        if (atts != null)
        {
            foreach (var att in atts)
            {
                string fullPath;
                try
                {
                    fullPath = _attachmentStorageService.GetFullPath(att.RelativePath);
                }
                catch
                {
                    fullPath = string.Empty;
                }
                var item = new NoteAttachmentItemViewModel(att, fullPath, RemoveAttachment);
                Attachments.Add(item);
            }
        }
        OnPropertyChanged(nameof(HasAttachments));
        OnPropertyChanged(nameof(AttachmentsCountText));
    }

    public bool AddAttachmentFromBytes(byte[] content, string fileName, out string? statusMessage)
    {
        if (content == null || content.Length == 0)
        {
            statusMessage = "Нет данных изображения для вложения.";
            return false;
        }

        long maxBytes = (long)_settingsService.CurrentSettings.MaxAttachmentSizeMb * 1024 * 1024;
        if (maxBytes > 0 && content.Length > maxBytes)
        {
            statusMessage = $"Размер изображения превышает установленный лимит ({_settingsService.CurrentSettings.MaxAttachmentSizeMb} МБ).";
            return false;
        }

        string sha256 = AttachmentFileHelper.ComputeSha256(content);
        if (Attachments.Any(a => a.Sha256.Equals(sha256, StringComparison.OrdinalIgnoreCase) && a.Size == content.Length))
        {
            statusMessage = "Это изображение уже прикреплено к заметке.";
            return false;
        }

        try
        {
            var saveResult = _attachmentStorageService.SaveFromBytes(content, fileName, maxBytes);
            var item = new NoteAttachmentItemViewModel(saveResult, NoteId ?? 0, RemoveAttachment);
            Attachments.Add(item);
            OnPropertyChanged(nameof(HasAttachments));
            OnPropertyChanged(nameof(AttachmentsCountText));
            ScheduleDraftSave();
            statusMessage = null;
            return true;
        }
        catch (Exception ex)
        {
            ErrorLogService.Write("NoteEditor.AddAttachmentFromBytes", ex);
            statusMessage = $"Не удалось прикрепить изображение: {ex.Message}";
            return false;
        }
    }

    public bool TryPasteImageBytes(byte[] imageBytes, string fileName, int caretIndex, out string? statusMessage)
    {
        statusMessage = null;
        if (IsLocked)
        {
            statusMessage = "Заметка заблокирована.";
            return false;
        }

        if (imageBytes == null || imageBytes.Length == 0)
        {
            statusMessage = "Изображение пусто или повреждено.";
            return false;
        }

        long maxBytes = (long)_settingsService.CurrentSettings.MaxAttachmentSizeMb * 1024 * 1024;
        if (maxBytes > 0 && imageBytes.Length > maxBytes)
        {
            statusMessage = $"Размер изображения превышает установленный лимит ({_settingsService.CurrentSettings.MaxAttachmentSizeMb} МБ).";
            return false;
        }

        string sha256 = AttachmentFileHelper.ComputeSha256(imageBytes);
        if (Attachments.Any(a => a.Sha256.Equals(sha256, StringComparison.OrdinalIgnoreCase) && a.Size == imageBytes.Length))
        {
            statusMessage = "Это изображение уже прикреплено к заметке.";
            return false;
        }

        string originalText = Text ?? string.Empty;
        AttachmentSaveResult? saveResult = null;
        NoteAttachmentItemViewModel? item = null;

        try
        {
            saveResult = _attachmentStorageService.SaveFromBytes(imageBytes, fileName, maxBytes);
            item = new NoteAttachmentItemViewModel(saveResult, NoteId ?? 0, RemoveAttachment);
            Attachments.Add(item);

            int insertPos = Math.Clamp(caretIndex, 0, originalText.Length);
            string prefix = (insertPos > 0 && originalText[insertPos - 1] != '\n') ? "\n" : "";
            string suffix = (insertPos < originalText.Length && originalText[insertPos] != '\n') ? "\n" : "";
            string markdownLink = $"{prefix}![{saveResult.OriginalFileName}]({saveResult.RelativePath}){suffix}";

            Text = originalText.Insert(insertPos, markdownLink);
            OnPropertyChanged(nameof(HasAttachments));
            OnPropertyChanged(nameof(AttachmentsCountText));
            ScheduleDraftSave();
            SetSelection?.Invoke(insertPos + markdownLink.Length, 0);
            return true;
        }
        catch (Exception ex)
        {
            ErrorLogService.Write("NoteEditor.TryPasteImageBytes", ex);
            if (item != null)
            {
                Attachments.Remove(item);
            }
            if (saveResult != null)
            {
                try
                {
                    if (File.Exists(saveResult.FullPath))
                    {
                        File.Delete(saveResult.FullPath);
                    }
                }
                catch
                {
                }
            }
            Text = originalText;
            statusMessage = $"Не удалось вставить изображение: {ex.Message}";
            return false;
        }
    }

    public bool TryPasteImageFromClipboard(int caretIndex, out string? statusMessage)
    {
        statusMessage = null;
        if (IsLocked)
        {
            statusMessage = "Заметка заблокирована.";
            return false;
        }

        try
        {
            if (!System.Windows.Clipboard.ContainsImage())
            {
                statusMessage = "В буфере обмена нет изображения.";
                return false;
            }

            var image = System.Windows.Clipboard.GetImage();
            if (image == null)
            {
                statusMessage = "Не удалось прочитать изображение из буфера обмена.";
                return false;
            }

            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(image));
            using var ms = new MemoryStream();
            encoder.Save(ms);
            byte[] bytes = ms.ToArray();
            string fileName = $"clipboard-{DateTime.Now:yyyyMMdd-HHmmss}.png";
            return TryPasteImageBytes(bytes, fileName, caretIndex, out statusMessage);
        }
        catch (Exception ex)
        {
            ErrorLogService.Write("NoteEditor.PasteImage", ex);
            statusMessage = "Не удалось вставить изображение из буфера обмена.";
            return false;
        }
    }

    public bool TryPasteImageFromClipboard(out string? statusMessage) =>
        TryPasteImageFromClipboard(GetSelection?.Invoke().Start ?? (Text?.Length ?? 0), out statusMessage);

    public bool AddAttachment(string sourceFilePath, out string? statusMessage)
    {
        if (string.IsNullOrWhiteSpace(sourceFilePath) || !File.Exists(sourceFilePath))
        {
            statusMessage = "Указанный файл не найден.";
            return false;
        }

        var fileInfo = new FileInfo(sourceFilePath);
        long fileSize = fileInfo.Length;
        long maxBytes = (long)_settingsService.CurrentSettings.MaxAttachmentSizeMb * 1024 * 1024;

        if (maxBytes > 0 && fileSize > maxBytes)
        {
            statusMessage = $"Размер файла ({AttachmentFileHelper.FormatFileSize(fileSize)}) превышает установленный лимит ({_settingsService.CurrentSettings.MaxAttachmentSizeMb} МБ).";
            return false;
        }

        string sha256 = AttachmentFileHelper.ComputeSha256(sourceFilePath);

        if (Attachments.Any(a => a.Sha256.Equals(sha256, StringComparison.OrdinalIgnoreCase) && a.Size == fileSize))
        {
            statusMessage = $"Файл «{fileInfo.Name}» уже прикреплён к этой заметке.";
            return false;
        }

        try
        {
            var saveResult = _attachmentStorageService.SaveAttachment(sourceFilePath, maxBytes);
            var item = new NoteAttachmentItemViewModel(saveResult, NoteId ?? 0, RemoveAttachment);

            _deletedAttachments.RemoveAll(d => d.Sha256.Equals(sha256, StringComparison.OrdinalIgnoreCase) && d.Size == fileSize);

            Attachments.Add(item);
            OnPropertyChanged(nameof(HasAttachments));
            OnPropertyChanged(nameof(AttachmentsCountText));
            ScheduleDraftSave();
            statusMessage = null;
            return true;
        }
        catch (Exception ex)
        {
            ErrorLogService.Write("NoteEditor.AddAttachment", ex);
            statusMessage = $"Не удалось прикрепить файл: {ex.Message}";
            return false;
        }
    }

    public void RemoveAttachment(NoteAttachmentItemViewModel? item)
    {
        if (item == null) return;
        Attachments.Remove(item);
        ScheduleDraftSave();
        if (!item.IsNew)
        {
            _deletedAttachments.Add(item);
        }
        else
        {
            try
            {
                using var db = _contextFactory();
                _attachmentStorageService.DeleteManagedFileIfUnreferenced(db, item.StoredFileName);
            }
            catch (Exception ex)
            {
                ErrorLogService.Write("NoteEditor.RemoveUncommittedAttachment", ex);
            }
        }
        OnPropertyChanged(nameof(HasAttachments));
        OnPropertyChanged(nameof(AttachmentsCountText));
    }

    private void AddAttachmentFromFilePicker()
    {
        string? path = PickAttachmentFile?.Invoke();
        if (string.IsNullOrWhiteSpace(path)) return;

        bool success = AddAttachment(path, out var message);
        if (!success && !string.IsNullOrEmpty(message))
        {
            MessageBox.Show(message, "Вложение", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    public void CleanupUncommittedAttachments()
    {
        var uncommitted = Attachments.Where(a => a.IsNew).ToList();
        if (uncommitted.Count > 0)
        {
            try
            {
                using var db = _contextFactory();
                foreach (var att in uncommitted)
                {
                    _attachmentStorageService.DeleteManagedFileIfUnreferenced(db, att.StoredFileName);
                }
            }
            catch (Exception ex)
            {
                ErrorLogService.Write("NoteEditor.CleanupUncommitted", ex);
            }
        }
    }

    private void MarkProcessed()
    {
        IsInbox = false;
        Save();
    }

    private void MarkProcessedAndNext()
    {
        IsInbox = false;
        ContinueInboxAfterClose = true;
        Save();
    }

    private void MoveToTrash()
    {
        var result = MessageBox.Show(
            $"Переместить заметку #{NoteId} в корзину?",
            "Подтверждение",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result == MessageBoxResult.Yes)
        {
            DeletedAt = DateTime.Now;
            CommitAndClose();
        }
    }

    private void Restore()
    {
        DeletedAt = null;
        CommitAndClose();
    }

    private void PermanentDelete()
    {
        var result = MessageBox.Show(
            $"Вы действительно хотите безвозвратно удалить заметку #{NoteId}? Это действие нельзя отменить.",
            "Подтверждение безвозвратного удаления",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result == MessageBoxResult.Yes)
        {
            IsPermanentlyDeleted = true;
            CommitAndClose();
        }
    }

    private void Delete()
    {
        if (IsInTrash)
        {
            PermanentDelete();
        }
        else
        {
            MoveToTrash();
        }
    }

    public void LoadHistory()
    {
        Revisions.Clear();
        if (NoteId.HasValue)
        {
            try
            {
                using var db = _contextFactory();
                var dtos = _noteHistoryService.GetHistory(db, NoteId.Value);

                if (IsNoteProtected && IsNoteUnlocked)
                {
                    // Decrypt each revision body in memory for display (never persisted).
                    foreach (var dto in dtos)
                    {
                        if (!dto.IsProtectedRevision)
                        {
                            Revisions.Add(new NoteHistoryItemViewModel(dto));
                            continue;
                        }

                        var rev = db.NoteRevisions.FirstOrDefault(r => r.Id == dto.Id);
                        if (rev == null)
                        {
                            Revisions.Add(new NoteHistoryItemViewModel(dto));
                            continue;
                        }

                        try
                        {
                            var decryptedPayload = _noteProtectionService.DecryptRevisionPayload(db, NoteId.Value, rev);
                            string? decryptedTitle = decryptedPayload?.Title;
                            string? decryptedText = decryptedPayload?.Text;
                            Revisions.Add(new NoteHistoryItemViewModel(
                                new Models.NoteHistoryItemDto
                                {
                                    Id = dto.Id,
                                    NoteId = dto.NoteId,
                                    CreatedAt = dto.CreatedAt,
                                    Title = decryptedTitle ?? string.Empty,
                                    Text = decryptedText ?? dto.Text,
                                    Tags = dto.Tags,
                                    DiffSummary = dto.DiffSummary,
                                    IsCorrupted = decryptedText == null
                                }));
                        }
                        catch (Services.NoteProtection.NoteProtectionSecurityException)
                        {
                            Revisions.Add(new NoteHistoryItemViewModel(
                                new Models.NoteHistoryItemDto
                                {
                                    Id = dto.Id,
                                    NoteId = dto.NoteId,
                                    CreatedAt = dto.CreatedAt,
                                    Title = string.Empty,
                                    Text = dto.Text,
                                    Tags = dto.Tags,
                                    DiffSummary = "Не удалось расшифровать версию",
                                    IsCorrupted = true
                                }));
                        }
                    }
                }
                else
                {
                    foreach (var dto in dtos)
                    {
                        Revisions.Add(new NoteHistoryItemViewModel(dto));
                    }
                }
            }
            catch (Exception ex)
            {
                ErrorLogService.Write("History.Load", ex);
            }
        }

        OnPropertyChanged(nameof(HistoryButtonText));
        OnPropertyChanged(nameof(HasRevisions));
        SelectedRevision = Revisions.FirstOrDefault();
    }

    private void RestoreSelectedVersion()
    {
        if (SelectedRevision == null) return;

        var result = MessageBox.Show(
            $"Восстановить версию от {SelectedRevision.CreatedAtFormatted}?\n\nТекущий текст и теги заметки будут заменены содержимым этой версии. В истории будет создана новая текущая версия.",
            "Подтверждение восстановления",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result == MessageBoxResult.Yes)
        {
            RestoreVersion(SelectedRevision);
        }
    }

    public void RestoreVersion(NoteHistoryItemViewModel item)
    {
        if (item == null || item.IsCorrupted) return;

        if (NoteId.HasValue)
        {
            try
            {
                Models.Note updated;
                if (_mutationCoordinator != null)
                {
                    updated = _mutationCoordinator.ExecuteNoteMutation(NoteId.Value, () =>
                    {
                        using var db = _contextFactory();
                        return _noteHistoryService.RestoreRevision(db, NoteId.Value, item.Id);
                    });
                }
                else
                {
                    using var db = _contextFactory();
                    updated = _noteHistoryService.RestoreRevision(db, NoteId.Value, item.Id);
                }

                Title = IsNoteProtected ? (item.Title ?? string.Empty) : (updated.Title ?? string.Empty);
                Text = updated.Text;

                ActiveTags.Clear();
                SuppressedTagIds.Clear();

                var detailedMap = _tagDetectionService.DetectDetailedTags(Text, _allTags, _rules)
                    .ToDictionary(m => m.Tag.Id);

                foreach (var nt in updated.NoteTags)
                {
                    if (nt.IsSuppressed)
                    {
                        SuppressedTagIds.Add(nt.TagId);
                    }
                    else
                    {
                        var tag = nt.Tag ?? _allTags.FirstOrDefault(t => t.Id == nt.TagId);
                        if (tag != null)
                        {
                            string reason = nt.Origin == TagOrigin.Manual
                                ? "добавлен вручную"
                                : (detailedMap.TryGetValue(nt.TagId, out var match) ? match.Reason : AutoTagWithoutCurrentMatchReason);

                            ActiveTags.Add(new NoteEditorTagItem
                            {
                                TagId = nt.TagId,
                                TagName = tag.Name,
                                Origin = nt.Origin,
                                IsSuppressed = false,
                                Reason = reason
                            });
                        }
                    }
                }

                RefreshAvailableTagsToAdd();
                RefreshSuppressedTags();
                HasRestoredVersion = true;
                LoadHistory();
                LoadLinks();
                CaptureBaseline();
                NotifyNoteSaved();
            }
            catch (Exception ex)
            {
                ErrorLogService.Write("NoteEditor.RestoreVersion", ex);
                MessageBox.Show(
                    $"Не удалось восстановить версию #{item.Id}: {ex.Message}",
                    "Ошибка восстановления",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }
        else
        {
            Title = item.Title ?? string.Empty;
            Text = item.Text;
            ActiveTags.Clear();
            SuppressedTagIds.Clear();
            foreach (var st in item.Tags)
            {
                if (st.IsSuppressed)
                {
                    SuppressedTagIds.Add(st.TagId);
                }
                else
                {
                    ActiveTags.Add(new NoteEditorTagItem
                    {
                        TagId = st.TagId,
                        TagName = st.TagName,
                        Origin = st.Origin,
                        IsSuppressed = false,
                        Reason = st.Origin == TagOrigin.Manual ? TagOriginCopy.ManualReason : "автотег"
                    });
                }
            }
            RefreshAvailableTagsToAdd();
            RefreshSuppressedTags();
            LoadLinks();
        }
    }

    private void InsertLink()
    {
        var picked = PickLinkedNote?.Invoke();
        int? noteId = picked?.Id ?? PickLinkNoteId?.Invoke();
        if (picked != null || noteId.HasValue)
        {
            string marker = picked != null && picked.SyncId != Guid.Empty
                ? _noteLinkService.FormatLink(picked.SyncId)
                : _noteLinkService.FormatLink(noteId!.Value);
            if (InsertTextAtCaret != null)
            {
                InsertTextAtCaret(marker);
            }
            else
            {
                Text = string.IsNullOrEmpty(Text) ? marker : $"{Text} {marker}";
            }
            RefreshOutgoingLinks();
        }
    }

    public void OpenLinkedNote(int targetId)
    {
        RequestOpenLinkedNote?.Invoke(targetId);
    }

    public void LoadLinks()
    {
        RefreshOutgoingLinks();
        RefreshIncomingLinks();
    }

    public void RefreshLinks()
    {
        RefreshOutgoingLinks();
        RefreshIncomingLinks();
    }

    public void RefreshOutgoingLinks()
    {
        try
        {
            using var db = _contextFactory();
            var dtos = _noteLinkService.GetOutgoingLinks(db, Text);

            OutgoingLinks.Clear();
            foreach (var dto in dtos)
            {
                OutgoingLinks.Add(new NoteLinkItemViewModel(
                    dto.TargetNoteId,
                    dto.Title,
                    dto.IsAvailable,
                    OpenLinkedNote));
            }
        }
        catch (Exception ex)
        {
            ErrorLogService.Write("NoteEditor.RefreshOutgoingLinks", ex);
        }
        finally
        {
            OnPropertyChanged(nameof(HasOutgoingLinks));
            OnPropertyChanged(nameof(OutgoingLinksCountText));
        }
    }

    public void RefreshIncomingLinks()
    {
        try
        {
            IncomingLinks.Clear();
            if (NoteId.HasValue)
            {
                using var db = _contextFactory();
                var result = _noteLinkService.GetIncomingLinksResult(db, NoteId.Value);

                foreach (var dto in result.Items)
                {
                    IncomingLinks.Add(new NoteLinkItemViewModel(
                        dto.TargetNoteId,
                        dto.Title,
                        dto.IsAvailable,
                        OpenLinkedNote));
                }

                IsIncomingLinksTruncated = result.IsTruncated;
            }
            else
            {
                IsIncomingLinksTruncated = false;
            }
        }
        catch (Exception ex)
        {
            ErrorLogService.Write("NoteEditor.RefreshIncomingLinks", ex);
            IsIncomingLinksTruncated = false;
        }
        finally
        {
            OnPropertyChanged(nameof(HasIncomingLinks));
            OnPropertyChanged(nameof(IncomingLinksCountText));
            OnPropertyChanged(nameof(IncomingLinksEmptyText));
            OnPropertyChanged(nameof(IsIncomingLinksTruncated));
            OnPropertyChanged(nameof(IncomingLinksTruncatedText));
            OnPropertyChanged(nameof(HasIncomingLinksTruncated));
        }
    }

    private void UpdateOutgoingLinksIfChanged()
    {
        var currentIds = _noteLinkService.ExtractLinkedNoteIds(_text);
        if (!_lastOutgoingIds.SequenceEqual(currentIds))
        {
            _lastOutgoingIds = currentIds;
            RefreshOutgoingLinks();
        }
    }

    // ------------------------------------------------------------------
    // Password protection
    // ------------------------------------------------------------------

    private void ProtectNote()
    {
        if (!NoteId.HasValue)
        {
            MessageBox.Show(
                "Сначала сохраните заметку, затем защитите её паролем.",
                "Защита паролем",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        string? password = RequestSetPasswordDialog?.Invoke(
            "Защита заметки паролем",
            "Установите пароль для этой заметки. Содержимое будет зашифровано, и без пароля его нельзя будет прочитать.");
        if (string.IsNullOrEmpty(password))
        {
            return;
        }

        try
        {
            Services.NoteProtection.NoteProtectionResult result;
            if (_mutationCoordinator != null)
            {
                result = _mutationCoordinator.ExecuteNoteMutation(NoteId.Value, () =>
                {
                    using var db = _contextFactory();
                    return _noteProtectionService.ProtectNote(db, NoteId.Value, password);
                });
            }
            else
            {
                using var db = _contextFactory();
                result = _noteProtectionService.ProtectNote(db, NoteId.Value, password);
            }

            if (!result.Success)
            {
                MessageBox.Show(result.ErrorMessage, "Защита паролем", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // The note is now protected and unlocked in this process.
            IsNoteProtected = true;
            IsNoteUnlocked = true;
            RefreshProtectionState();
            NotifyNoteSaved();
        }
        catch (Exception ex)
        {
            ErrorLogService.Write("NoteEditor.ProtectNote", ex);
            MessageBox.Show($"Не удалось защитить заметку: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void LockNote()
    {
        if (!NoteId.HasValue) return;
        try
        {
            _noteProtectionService.LockNote(NoteId.Value);
            IsNoteUnlocked = false;
            Title = string.Empty;
            Text = string.Empty;
            SourceProcessName = null;
            SourceWindowTitle = null;
            SourceUrl = null;
            CaptureBaseline();
            RefreshProtectionState();

            if (!IsInline)
            {
                // Close the editor - a locked note must be reopened via password dialog.
                RequestLockNote?.Invoke();
            }
        }
        catch (Exception ex)
        {
            ErrorLogService.Write("NoteEditor.LockNote", ex);
        }
    }

    private void UnlockNote()
    {
        if (!NoteId.HasValue) return;
        try
        {
            string? password = RequestPasswordDialog != null
                ? RequestPasswordDialog("Ввод пароля", "Введите пароль для расшифровки заметки:")
                : null;
            if (string.IsNullOrEmpty(password))
            {
                return;
            }

            using var db = _contextFactory();
            var payload = _noteProtectionService.UnlockNote(db, NoteId.Value, password);
            IsNoteUnlocked = true;
            Title = payload.Title ?? string.Empty;
            Text = payload.Text ?? string.Empty;
            SourceProcessName = payload.SourceProcessName;
            SourceWindowTitle = payload.SourceWindowTitle;
            SourceUrl = payload.SourceUrl;
            CaptureBaseline();
            RefreshProtectionState();
        }
        catch (Services.NoteProtection.NoteProtectionSecurityException ex)
        {
            if (AlertHandler != null)
            {
                AlertHandler(ex.Message, "Защищённая заметка", MessageBoxImage.Warning);
            }
            else
            {
                MessageBox.Show(ex.Message, "Защищённая заметка", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch (Exception ex)
        {
            ErrorLogService.Write("NoteEditor.UnlockNote", ex);
            if (AlertHandler != null)
            {
                AlertHandler("Не удалось разблокировать заметку.", "Ошибка", MessageBoxImage.Error);
            }
            else
            {
                MessageBox.Show("Не удалось разблокировать заметку.", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private void ChangePassword()
    {
        if (!NoteId.HasValue || !IsNoteUnlocked) return;

        string? currentPassword = RequestPasswordDialog?.Invoke(
            "Смена пароля",
            "Введите текущий пароль заметки.");
        if (string.IsNullOrEmpty(currentPassword))
        {
            return;
        }

        string? newPassword = RequestSetPasswordDialog?.Invoke(
            "Новый пароль",
            "Введите новый пароль для заметки. Восстановить забытый пароль невозможно.");
        if (string.IsNullOrEmpty(newPassword))
        {
            return;
        }

        try
        {
            Services.NoteProtection.NoteProtectionResult result;
            if (_mutationCoordinator != null)
            {
                result = _mutationCoordinator.ExecuteNoteMutation(NoteId.Value, () =>
                {
                    using var db = _contextFactory();
                    return _noteProtectionService.ChangePassword(db, NoteId.Value, currentPassword, newPassword);
                });
            }
            else
            {
                using var db = _contextFactory();
                result = _noteProtectionService.ChangePassword(db, NoteId.Value, currentPassword, newPassword);
            }

            if (!result.Success)
            {
                MessageBox.Show(result.ErrorMessage, "Смена пароля", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            NotifyNoteSaved();
        }
        catch (Exception ex)
        {
            ErrorLogService.Write("NoteEditor.ChangePassword", ex);
            MessageBox.Show($"Не удалось изменить пароль: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void RemoveProtection()
    {
        if (!NoteId.HasValue || !IsNoteUnlocked) return;

        var confirm = MessageBox.Show(
            "Снять защиту с этой заметки? Содержимое снова будет храниться в открытом виде.",
            "Снятие защиты",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes)
        {
            return;
        }

        string? password = RequestPasswordDialog?.Invoke(
            "Снятие защиты",
            "Введите текущий пароль заметки для подтверждения.");
        if (string.IsNullOrEmpty(password))
        {
            return;
        }

        try
        {
            Services.NoteProtection.NoteProtectionResult result;
            if (_mutationCoordinator != null)
            {
                result = _mutationCoordinator.ExecuteNoteMutation(NoteId.Value, () =>
                {
                    using var db = _contextFactory();
                    return _noteProtectionService.RemoveProtection(db, NoteId.Value, password);
                });
            }
            else
            {
                using var db = _contextFactory();
                result = _noteProtectionService.RemoveProtection(db, NoteId.Value, password);
            }

            if (!result.Success)
            {
                MessageBox.Show(result.ErrorMessage, "Снятие защиты", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            IsNoteProtected = false;
            IsNoteUnlocked = false;
            RefreshProtectionState();
            NotifyNoteSaved();
        }
        catch (Exception ex)
        {
            ErrorLogService.Write("NoteEditor.RemoveProtection", ex);
            MessageBox.Show($"Не удалось снять защиту: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void RefreshProtectionState()
    {
        OnPropertyChanged(nameof(CanProtect));
        OnPropertyChanged(nameof(CanLock));
        OnPropertyChanged(nameof(CanUnlock));
        OnPropertyChanged(nameof(IsLocked));
        OnPropertyChanged(nameof(CanChangePassword));
        OnPropertyChanged(nameof(CanRemoveProtection));
        OnPropertyChanged(nameof(ProtectButtonText));
        (ProtectNoteCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (LockNoteCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (UnlockNoteCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (ChangePasswordCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (RemoveProtectionCommand as RelayCommand)?.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(IsNoteProtected));
        OnPropertyChanged(nameof(IsNoteUnlocked));
    }

    // ------------------------------------------------------------------
    // Draft Journal & Crash Recovery
    // ------------------------------------------------------------------

    private void ScheduleDraftSave()
    {
        if (!_isInitialized || _isDisposed) return;

        // If it's a new note and completely empty, don't write debris
        if (!NoteId.HasValue && string.IsNullOrWhiteSpace(Text) && string.IsNullOrWhiteSpace(Title))
        {
            return;
        }

        lock (_draftLock)
        {
            _sequenceNumber++;
        }

        try
        {
            _debounceTimer?.Change(_debounceDelay, Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void OnDebounceTimerFired(object? state)
    {
        lock (_draftLock)
        {
            if (_isDisposed) return;
        }

        try
        {
            CancellationToken ct = _disposeCts.Token;
            if (ct.IsCancellationRequested) return;

            var snapshot = CreateDraftSnapshot();

            lock (_saveTaskLock)
            {
                lock (_draftLock)
                {
                    if (_isDisposed) return;
                }

                if (ct.IsCancellationRequested) return;

                var task = _draftJournalService.SaveJournalAsync(snapshot, ct);
                _pendingSaveTask = task;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ErrorLogService.Write("NoteEditor.DraftDebounce", ex);
        }
    }

    public async Task<DraftSaveResult> FlushDraftJournalAsync(CancellationToken cancellationToken = default)
    {
        lock (_draftLock)
        {
            if (_isDisposed) return DraftSaveResult.Fail("Editor is disposed.");
        }

        var snapshot = CreateDraftSnapshot();
        Task<DraftSaveResult> task;
        lock (_saveTaskLock)
        {
            task = _draftJournalService.SaveJournalAsync(snapshot, cancellationToken);
            _pendingSaveTask = task;
        }
        return await task.ConfigureAwait(false);
    }

    public DraftJournalSnapshot CreateDraftSnapshot()
    {
        long seq;
        string title;
        string text;
        lock (_draftLock)
        {
            seq = _sequenceNumber;
            title = Title ?? string.Empty;
            text = Text;
        }

        var tags = ActiveTags.Select(t => new DraftJournalTagDto
        {
            TagId = t.TagId,
            TagName = t.TagName,
            Origin = t.Origin,
            IsSuppressed = false
        }).ToList();

        foreach (int suppId in SuppressedTagIds)
        {
            if (!tags.Any(t => t.TagId == suppId))
            {
                string name = _allTags.FirstOrDefault(t => t.Id == suppId)?.Name ?? string.Empty;
                tags.Add(new DraftJournalTagDto
                {
                    TagId = suppId,
                    TagName = name,
                    Origin = TagOrigin.Auto,
                    IsSuppressed = true
                });
            }
        }

        return new DraftJournalSnapshot
        {
            DraftId = _draftId,
            NoteId = NoteId,
            SyncId = ExistingNote?.ProtectedOriginalSyncId ?? ExistingNote?.SyncId,
            SequenceNumber = seq,
            Title = title,
            Text = text,
            SourceProcessName = SourceProcessName,
            SourceWindowTitle = SourceWindowTitle,
            SourceUrl = SourceUrl,
            CapturedAt = CapturedAt,
            IsProtected = IsNoteProtected,
            Tags = tags,
            Attachments = Attachments.Select(a => new DraftJournalAttachmentDto
            {
                Id = a.Id,
                OriginalFileName = a.OriginalFileName,
                StoredFileName = a.StoredFileName,
                RelativePath = a.RelativePath,
                ContentType = a.ContentType,
                Size = a.Size,
                Sha256 = a.Sha256,
                IsNew = a.IsNew
            }).ToList()
        };
    }

    public bool CheckAndPromptDraftRecovery(bool forcePrompt = false)
    {
        var readResult = _draftJournalService.ReadJournal(DraftId);
        if (readResult.Status != DraftJournalReadStatus.Success || readResult.Envelope == null)
        {
            return false;
        }

        string draftTitle = readResult.DecryptedTitle ?? string.Empty;
        string draftText = readResult.DecryptedText ?? string.Empty;

        // If draft is identical to current committed note, nothing to recover
        if (string.Equals(draftTitle, Title ?? string.Empty, StringComparison.Ordinal) &&
            string.Equals(draftText, Text, StringComparison.Ordinal))
        {
            return false;
        }

        string diffSummary = DraftDiffHelper.GenerateSummary(Title, Text, draftTitle, draftText);
        string source = !string.IsNullOrWhiteSpace(SourceText)
            ? SourceText
            : (NoteId.HasValue ? $"Заметка #{NoteId.Value}" : "Новая заметка");

        var args = new DraftRecoveryPromptArgs
        {
            NoteId = NoteId,
            DraftId = DraftId,
            Source = source,
            JournalTimestamp = readResult.Envelope.SavedAtUtc.ToLocalTime(),
            CommittedTitle = Title ?? string.Empty,
            CommittedText = Text,
            CommittedTimestamp = ExistingNote?.UpdatedAt,
            DraftTitle = draftTitle,
            DraftText = draftText,
            DiffSummary = diffSummary,
            SourceProcessName = readResult.SourceProcessName,
            SourceWindowTitle = readResult.SourceWindowTitle,
            SourceUrl = readResult.SourceUrl,
            CapturedAt = readResult.CapturedAt,
            Tags = readResult.Tags,
            Attachments = readResult.Attachments
        };

        DraftRecoveryInfo = args;
        HasUncommittedDraftRecovery = true;

        if (RequestDraftRecoveryPrompt != null)
        {
            var choice = RequestDraftRecoveryPrompt(args);
            if (choice == DraftRecoveryChoice.Restore)
            {
                RestoreDraft(args);
                return true;
            }
            else if (choice == DraftRecoveryChoice.Discard)
            {
                DiscardDraft(args);
                return false;
            }
            return false;
        }
        else if (forcePrompt)
        {
            var choice = ShowDefaultDraftRecoveryDialog(args);
            if (choice == DraftRecoveryChoice.Restore)
            {
                RestoreDraft(args);
                return true;
            }
            else if (choice == DraftRecoveryChoice.Discard)
            {
                DiscardDraft(args);
                return false;
            }
            return false;
        }

        return false;
    }

    private DraftRecoveryChoice ShowDefaultDraftRecoveryDialog(DraftRecoveryPromptArgs args)
    {
        try
        {
            DraftRecoveryChoice choice = DraftRecoveryChoice.KeepCommitted;
            if (System.Windows.Application.Current?.Dispatcher != null &&
                !System.Windows.Application.Current.Dispatcher.CheckAccess())
            {
                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                {
                    var dlg = new QuickNotes.App.Views.DraftRecoveryWindow(args);
                    dlg.ShowDialog();
                    choice = dlg.Choice;
                });
            }
            else
            {
                var dlg = new QuickNotes.App.Views.DraftRecoveryWindow(args);
                dlg.ShowDialog();
                choice = dlg.Choice;
            }
            return choice;
        }
        catch (Exception ex)
        {
            ErrorLogService.Write("NoteEditor.ShowDefaultDraftRecoveryDialog", ex);
            return DraftRecoveryChoice.KeepCommitted;
        }
    }

    public void RestoreDraft(DraftRecoveryPromptArgs args)
    {
        Title = args.DraftTitle;
        Text = args.DraftText;
        SourceProcessName = args.SourceProcessName;
        SourceWindowTitle = args.SourceWindowTitle;
        SourceUrl = args.SourceUrl;
        CapturedAt = args.CapturedAt;
        OnPropertyChanged(nameof(SourceText));
        OnPropertyChanged(nameof(HasSourceText));
        OnPropertyChanged(nameof(CapturedAtText));

        if (args.Tags != null)
        {
            ActiveTags.Clear();
            SuppressedTagIds.Clear();
            foreach (var tag in args.Tags)
            {
                if (tag.IsSuppressed)
                {
                    if (!SuppressedTagIds.Contains(tag.TagId))
                        SuppressedTagIds.Add(tag.TagId);
                }
                else
                {
                    ActiveTags.Add(new NoteEditorTagItem(tag.TagId, tag.TagName, tag.Origin));
                }
            }
            RefreshAvailableTagsToAdd();
            RefreshSuppressedTags();
        }

        if (args.Attachments != null)
        {
            Attachments.Clear();
            foreach (var att in args.Attachments)
            {
                string fullPath = _attachmentStorageService.GetFullPath(att.RelativePath);
                var entity = new NoteAttachment
                {
                    Id = att.Id,
                    NoteId = NoteId ?? 0,
                    OriginalFileName = att.OriginalFileName,
                    StoredFileName = att.StoredFileName,
                    RelativePath = att.RelativePath,
                    ContentType = att.ContentType,
                    Size = att.Size,
                    Sha256 = att.Sha256,
                    CreatedAt = DateTime.Now
                };
                var item = new NoteAttachmentItemViewModel(entity, fullPath, RemoveAttachment)
                {
                    IsNew = att.IsNew
                };
                Attachments.Add(item);
            }
            OnPropertyChanged(nameof(HasAttachments));
            OnPropertyChanged(nameof(AttachmentsCountText));
        }

        HasUncommittedDraftRecovery = false;
        DraftRecoveryInfo = null;
    }

    public void DiscardDraft(DraftRecoveryPromptArgs args)
    {
        _draftJournalService.DeleteJournal(args.DraftId);
        HasUncommittedDraftRecovery = false;
        DraftRecoveryInfo = null;
    }

    public void Dispose()
    {
        lock (_draftLock)
        {
            if (_isDisposed) return;
            _isDisposed = true;
        }

        try
        {
            _disposeCts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        try
        {
            _previewCts?.Cancel();
            _previewCts?.Dispose();
        }
        catch
        {
        }

        using (var timerDisposedHandle = new ManualResetEvent(false))
        {
            if (_debounceTimer != null)
            {
                if (_debounceTimer.Dispose(timerDisposedHandle))
                {
                    try
                    {
                        timerDisposedHandle.WaitOne(TimeSpan.FromSeconds(2));
                    }
                    catch { }
                }
            }
        }

        ThemeService.ThemeChanged -= OnThemeChanged;

        Task pending;
        lock (_saveTaskLock)
        {
            pending = _pendingSaveTask;
        }
        LifecycleShutdown.Wait(pending, BoundedOperation.EditorPendingSaveTimeout);

        try
        {
            _disposeCts.Dispose();
        }
        catch { }
    }
}
