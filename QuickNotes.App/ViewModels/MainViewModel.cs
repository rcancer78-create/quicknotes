using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using Microsoft.EntityFrameworkCore;
using QuickNotes.App.Composition;
using QuickNotes.App.Data;
using QuickNotes.App.Helpers;
using QuickNotes.App.Models;
using QuickNotes.App.Models.Sync;
using QuickNotes.App.Services;
using QuickNotes.App.Services.Reminders;
using QuickNotes.App.Services.Sync;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;
using MessageBoxResult = System.Windows.MessageBoxResult;

namespace QuickNotes.App.ViewModels;

public enum MainSyncStatus
{
    Disabled,
    NeedsConfig,
    Ready,
    Syncing,
    Offline,
    Conflicts,
    Error
}

/// <summary>
/// Visual tone of the first-level status-bar state (UX-C11). Kept brush-free so the
/// ViewModel stays independent of WPF resources.
/// </summary>
public enum SyncStatusActionTone
{
    Neutral,
    Success,
    Warning,
    Error
}

public class NoteSortOption
{
    public NoteSortMode Mode { get; }
    public string Title { get; }

    public NoteSortOption(NoteSortMode mode, string title)
    {
        Mode = mode;
        Title = title;
    }
}

public class MainViewModel : ViewModelBase, IDisposable
{
    private readonly Func<QuickNotesDbContext> _contextFactory;
    public Func<QuickNotesDbContext> ContextFactory => _contextFactory;
    private readonly TagDetectionService _tagDetectionService;
    private readonly SearchService _searchService;
    private readonly SettingsService _settingsService;
    private readonly GlobalHotkeyService _hotkeyService;
    private readonly ClipboardCaptureService _clipboardCaptureService;
    private readonly TrayIconService _trayIconService;
    private readonly BackupService _backupService;
    private readonly TagMergeService _tagMergeService;
    private readonly TagRescanPreviewService _tagRescanPreviewService;
    private readonly TagSuggestionService _tagSuggestionService;
    private readonly SearchDebouncer _searchDebouncer;
    private readonly ActionCoalescer _refreshCoalescer;
    private bool _isDisposed;
    private readonly object _shutdownGate = new();
    private Task? _shutdownTask;
    private readonly INoteHistoryService _noteHistoryService;
    private readonly INoteExportService _noteExportService;
    private readonly INoteImportService _noteImportService;
    private readonly INoteAssemblyService _noteAssemblyService;
    private readonly ITaskIndexService _taskIndexService;
    private readonly ITaskReminderScheduler _taskReminderScheduler;
    private readonly INoteLinkService _noteLinkService;
    private readonly IAttachmentStorageService _attachmentStorageService;
    private readonly Services.NoteProtection.INoteProtectionService _noteProtectionService;
    private Services.NoteProtection.IProtectedNoteSessionStore _noteProtectionSessions;
    private readonly IScreenCaptureService _screenCaptureService;
    private readonly IOcrService _ocrService;
    private readonly IScreenOcrCoordinator _screenOcrCoordinator;
    private readonly SyncCloudServices _syncCloud;
    private readonly IS3CredentialsStorage _credentialsStorage;
    private readonly ISyncPasswordStorage _passwordStorage;
    private readonly ISyncConflictService _conflictService;
    private readonly ISyncScheduler? _syncScheduler;
    private readonly BoundedOwnership _syncOwnership = new();
    private bool _ignoreSchedulerSyncApply;
    private readonly ILocalMutationCoordinator _mutationCoordinator;
    private readonly IDeviceIdProvider _deviceIdProvider;
    private IDisposable? _testOwnedInfrastructure;
    private readonly Services.DraftJournal.IDraftJournalService _draftJournalService;
    public Services.DraftJournal.IDraftJournalService DraftJournalService => _draftJournalService;
    public ILocalMutationCoordinator MutationCoordinator => _mutationCoordinator;
    public LocalCommitSyncStatus PublicationStatus => _syncScheduler?.PublicationStatus ?? LocalCommitSyncStatus.SavedLocally;
    private readonly List<NoteEditorViewModel> _activeEditors = new();
    private readonly object _editorsLock = new();
    private readonly System.Windows.Threading.Dispatcher? _uiDispatcher;
    private CancellationTokenSource? _syncCts;
    private CancellationTokenSource? _ocrCts;
    internal TimeSpan ManualSyncTimeout { get; set; } = BoundedOperation.ManualSyncTimeout;
    internal TimeSpan OcrRecognizeTimeout { get; set; } = BoundedOperation.OcrRecognizeTimeout;
    private MainSyncStatus _syncStatus = MainSyncStatus.Disabled;
    private bool _isSyncBusy;
    private int _unresolvedConflictsCount;
    private SyncCycleResult? _lastSyncResult;
    private DateTime? _lastSyncTime;
    private string? _lastSyncError;

    public ActionCoalescer RefreshCoalescer => _refreshCoalescer;
    public IAttachmentStorageService AttachmentStorageService => _attachmentStorageService;
    public IScreenCaptureService ScreenCaptureService => _screenCaptureService;
    public IOcrService OcrService => _ocrService;
    public IScreenOcrCoordinator ScreenOcrCoordinator => _screenOcrCoordinator;
    public ISyncConflictService ConflictService => _conflictService;
    public IS3CredentialsStorage CredentialsStorage => _credentialsStorage;
    public ISyncPasswordStorage PasswordStorage => _passwordStorage;
    public ISyncScheduler? SyncScheduler => _syncScheduler;
    public ITaskReminderScheduler TaskReminderScheduler => _taskReminderScheduler;
    public string TaskRemindersStatusText => _taskReminderScheduler.Status.CompactText;
    public ICloudUsageService? CloudUsageService => _syncCloud.CurrentUsage;
    public string SyncQueueStatusText => _syncScheduler?.QueueStatusDescription ?? "Очередь: ожидание";
    public string CloudUsageText => _syncCloud.CurrentUsage?.CachedUsage?.FormatUsageText() ?? "Объём ещё не вычислен";
    public string CloudUsageStatusBarText
    {
        get
        {
            var usage = _syncCloud.CurrentUsage?.CachedUsage;
            if (usage == null) return string.Empty;
            if (!usage.IsSuccess)
                return "| Хранилище: объём неизвестен";
            string size = CloudUsageResult.FormatBytes(usage.TotalBytes);
            if (usage.IsTruncated || usage.IsPartial)
                return $"| Хранилище: ≥ {size} (неполно)";
            if (usage.BlocksNewAttachmentUploads)
                return $"| Хранилище: {size} — новые вложения заблокированы";
            if (usage.IsQuotaWarning)
                return $"| Хранилище: {size} — предупреждение 800 МиБ";
            return $"| Хранилище: {size}";
        }
    }
    public string CloudUsageExplanationText => "Занятый объём в бакете по префиксу QuickNotes. Не включает другие папки и объекты в бакете.";
    public bool IsCloudUsageBusy => _syncCloud.CurrentUsage?.IsCalculating ?? false;
    public DateTime? LastSyncAttemptTime => _syncScheduler?.LastAttemptTimeUtc?.ToLocalTime();
    public DateTime? LastSyncSuccessTime => _syncScheduler?.LastSuccessTimeUtc?.ToLocalTime() ?? _lastSyncTime;
    public ICommand RefreshCloudUsageCommand { get; }

    public MainSyncStatus SyncStatus
    {
        get => _syncStatus;
        private set
        {
            if (SetProperty(ref _syncStatus, value))
            {
                OnPropertyChanged(nameof(SyncStatusIcon));
                OnPropertyChanged(nameof(SyncStatusShortText));
                (SyncNowCommand as RelayCommand)?.RaiseCanExecuteChanged();
                NotifySyncStatusActionChanged();
            }
        }
    }

    public bool IsSyncBusy
    {
        get => _isSyncBusy;
        private set
        {
            if (SetProperty(ref _isSyncBusy, value))
            {
                (SyncNowCommand as RelayCommand)?.RaiseCanExecuteChanged();
                NotifySyncStatusActionChanged();
            }
        }
    }

    public int UnresolvedConflictsCount
    {
        get => _unresolvedConflictsCount;
        private set
        {
            if (SetProperty(ref _unresolvedConflictsCount, value))
            {
                OnPropertyChanged(nameof(HasUnresolvedConflicts));
                OnPropertyChanged(nameof(ConflictBadgeText));
                NotifySyncStatusActionChanged();
            }
        }
    }

    public bool HasUnresolvedConflicts => UnresolvedConflictsCount > 0;
    public string ConflictBadgeText => $"Конфликты: {UnresolvedConflictsCount}";

    public SyncCycleResult? LastSyncResult
    {
        get => _lastSyncResult;
        private set
        {
            if (SetProperty(ref _lastSyncResult, value))
            {
                NotifySyncStatusActionChanged();
            }
        }
    }

    public DateTime? LastSyncTime
    {
        get => _lastSyncTime;
        private set
        {
            if (SetProperty(ref _lastSyncTime, value))
            {
                NotifySyncStatusActionChanged();
            }
        }
    }

    public string? LastSyncError
    {
        get => _lastSyncError;
        private set
        {
            if (SetProperty(ref _lastSyncError, value))
            {
                NotifySyncStatusActionChanged();
            }
        }
    }

    public string SyncStatusIcon => SyncStatus switch
    {
        MainSyncStatus.Disabled => "☁",
        MainSyncStatus.NeedsConfig => "⚙",
        MainSyncStatus.Ready => "✓",
        MainSyncStatus.Syncing => "🔄",
        MainSyncStatus.Offline => "⚠",
        MainSyncStatus.Conflicts => "⚡",
        MainSyncStatus.Error => "✕",
        _ => "☁"
    };

    public string SyncStatusShortText => SyncStatus switch
    {
        MainSyncStatus.Disabled => "Отключена",
        MainSyncStatus.NeedsConfig => "Настройка",
        MainSyncStatus.Ready => "Готова",
        MainSyncStatus.Syncing => "Обмен…",
        MainSyncStatus.Offline => "Офлайн",
        MainSyncStatus.Conflicts => $"Конфликты ({UnresolvedConflictsCount})",
        MainSyncStatus.Error => "Ошибка",
        _ => "Синхронизация"
    };

    public string SyncStatusTooltip
    {
        get
        {
            string firstLevel = SyncStatusSummaryText;
            var baseText = SyncStatus switch
            {
                MainSyncStatus.Disabled => "Синхронизация с Yandex Cloud отключена. Для включения перейдите в Настройки.",
                MainSyncStatus.NeedsConfig => "Синхронизация не настроена. Укажите бакет, ключи S3 и пароль шифрования в Настройках.",
                MainSyncStatus.Ready => "Синхронизация готова к работе. Нажмите «Синхронизировать сейчас» для обмена.",
                MainSyncStatus.Syncing => "Выполняется обмен данными с Yandex Cloud Object Storage…",
                MainSyncStatus.Offline => $"Офлайн: нет связи с сервером ({LastSyncError ?? "проверьте подключение к сети"}).",
                MainSyncStatus.Conflicts => $"Обнаружены неразрешённые конфликты ({UnresolvedConflictsCount}). Нажмите для разрешения.",
                MainSyncStatus.Error => $"Сбой синхронизации: {LastSyncError ?? "неизвестная ошибка"}.",
                _ => "Синхронизация QuickNotes"
            };

            if (_syncScheduler != null)
            {
                baseText += $"\n\nСостояние очереди: {_syncScheduler.QueueStatusDescription}";
                if (_syncScheduler.LastAttemptTimeUtc.HasValue)
                {
                    baseText += $"\nПоследняя попытка: {_syncScheduler.LastAttemptTimeUtc.Value.ToLocalTime():dd.MM.yyyy HH:mm:ss}";
                }
            }

            if (LastSyncResult != null && LastSyncTime.HasValue)
            {
                int applied = LastSyncResult.RemoteEntitiesApplied.Created +
                              LastSyncResult.RemoteEntitiesApplied.Updated +
                              LastSyncResult.RemoteEntitiesApplied.Deleted;
                int uploaded = LastSyncResult.PackageUploaded ? 1 : 0;
                baseText += $"\n\nПоследний обмен: {LastSyncTime.Value:dd.MM.yyyy HH:mm:ss}\n" +
                            $"• Получено пакетов: {LastSyncResult.RemotePackagesPulled}\n" +
                            $"• Применено изменений: {applied}\n" +
                            $"• Отправлено пакетов: {uploaded}\n" +
                            $"• Пропущено изменений: {LastSyncResult.RemoteEntitiesSkipped}\n" +
                            $"• Неразрешённых конфликтов: {UnresolvedConflictsCount}\n" +
                            $"• Вложения: загружено {LastSyncResult.BlobsUploaded}, скачано {LastSyncResult.BlobsDownloaded}, пропущено {LastSyncResult.BlobsSkipped}, ошибок {LastSyncResult.BlobErrors}";
            }

            var usage = _syncCloud.CurrentUsage?.CachedUsage;
            if (usage != null)
            {
                baseText += $"\n\n{usage.FormatUsageText()}\n{usage.FormatQuotaStatusText()}";
            }

            if (SelectedNote != null)
            {
                return PublicationStatusCopy.Tooltip(SelectedNote.PublicationStatus) + "\n\n" + baseText;
            }

            return firstLevel + "\n\n" + baseText;
        }
    }

    public string SyncStatusSummaryText => SelectedNote != null
        ? PublicationStatusCopy.FirstLevel(SelectedNote.PublicationStatus)
        : PublicationStatusCopy.FirstLevel(SyncStatus, LastSyncTime.HasValue);

    private bool SyncStatusActionOpensConflicts =>
        HasUnresolvedConflicts ||
        SyncStatus == MainSyncStatus.Conflicts ||
        SelectedNote?.PublicationStatus == LocalCommitSyncStatus.Conflict;

    /// <summary>
    /// Visual tone for the first-level status text. Follows the same source as
    /// <see cref="SyncStatusSummaryText"/> so colour never contradicts the wording.
    /// </summary>
    public SyncStatusActionTone SyncStatusActionTone
    {
        get
        {
            if (SelectedNote != null)
            {
                return SelectedNote.PublicationStatus switch
                {
                    LocalCommitSyncStatus.Synchronized => SyncStatusActionTone.Success,
                    LocalCommitSyncStatus.Conflict => SyncStatusActionTone.Warning,
                    LocalCommitSyncStatus.Error => SyncStatusActionTone.Error,
                    _ => SyncStatusActionTone.Neutral
                };
            }

            return SyncStatus switch
            {
                MainSyncStatus.Ready => SyncStatusActionTone.Success,
                MainSyncStatus.Conflicts => SyncStatusActionTone.Warning,
                MainSyncStatus.Error => SyncStatusActionTone.Error,
                _ => SyncStatusActionTone.Neutral
            };
        }
    }

    /// <summary>
    /// Single notification point for every property that describes the status-bar action.
    /// </summary>
    private void NotifySyncStatusActionChanged()
    {
        OnPropertyChanged(nameof(SyncStatusSummaryText));
        OnPropertyChanged(nameof(SyncStatusTooltip));
        OnPropertyChanged(nameof(SyncStatusActionAutomationName));
        OnPropertyChanged(nameof(SyncStatusActionTone));
        (SyncStatusActionCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    private void OnSelectedNotePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(NoteCardViewModel.PublicationStatus))
        {
            return;
        }

        NotifySyncStatusActionChanged();
    }

    public string SyncStatusActionAutomationName
    {
        get
        {
            if (IsSyncBusy || SyncStatus == MainSyncStatus.Syncing)
            {
                return "Синхронизация выполняется";
            }

            if (SyncStatusActionOpensConflicts)
            {
                return "Открыть разрешение конфликтов синхронизации";
            }

            if (SyncStatus is MainSyncStatus.Disabled or MainSyncStatus.NeedsConfig)
            {
                return "Настроить облачные копии";
            }

            if (SyncStatus is MainSyncStatus.Offline or MainSyncStatus.Error)
            {
                return $"Повторить синхронизацию: {SyncStatusSummaryText}";
            }

            return $"Синхронизировать сейчас: {SyncStatusSummaryText}";
        }
    }

    private void ExecuteSyncStatusAction()
    {
        if (IsSyncBusy || SyncStatus == MainSyncStatus.Syncing)
        {
            return;
        }

        if (SyncStatusActionOpensConflicts)
        {
            if (OpenSyncConflictsCommand.CanExecute(null))
            {
                OpenSyncConflictsCommand.Execute(null);
            }

            return;
        }

        if (SyncStatus is MainSyncStatus.Disabled or MainSyncStatus.NeedsConfig)
        {
            if (OpenCloudSetupWizardCommand.CanExecute(null))
            {
                OpenCloudSetupWizardCommand.Execute(null);
            }

            return;
        }

        if (SyncNowCommand.CanExecute(null))
        {
            SyncNowCommand.Execute(null);
        }
    }

    private bool CanExecuteSyncStatusAction()
    {
        if (IsSyncBusy || SyncStatus == MainSyncStatus.Syncing)
        {
            return false;
        }

        if (SyncStatusActionOpensConflicts)
        {
            return OpenSyncConflictsCommand.CanExecute(null);
        }

        if (SyncStatus is MainSyncStatus.Disabled or MainSyncStatus.NeedsConfig)
        {
            return OpenCloudSetupWizardCommand.CanExecute(null);
        }

        return SyncNowCommand.CanExecute(null);
    }

    public string OcrHotkeyGestureText
    {
        get
        {
            var s = _settingsService.CurrentSettings;
            var parts = new List<string>();
            if (s.OcrHotkeyCtrl) parts.Add("Ctrl");
            if (s.OcrHotkeyShift) parts.Add("Shift");
            if (s.OcrHotkeyAlt) parts.Add("Alt");
            if (s.OcrHotkeyWin) parts.Add("Win");
            parts.Add(string.IsNullOrWhiteSpace(s.OcrHotkeyKey) ? ShortcutCatalog.DefaultOcrKey : s.OcrHotkeyKey);
            return string.Join("+", parts);
        }
    }

    public string ScreenOcrButtonToolTip => $"Захватить область экрана и распознать текст ({OcrHotkeyGestureText})";

    public string OcrStatusBarGestureText => $"{OcrHotkeyGestureText}: OCR";

    public bool MatchesOcrHotkey(Key key, bool isCtrl, bool isShift, bool isAlt, bool isWin)
    {
        var s = _settingsService.CurrentSettings;
        if (s.OcrHotkeyCtrl != isCtrl ||
            s.OcrHotkeyShift != isShift ||
            s.OcrHotkeyAlt != isAlt ||
            s.OcrHotkeyWin != isWin)
        {
            return false;
        }

        if (Enum.TryParse<Key>(s.OcrHotkeyKey, true, out var configuredKey))
        {
            return key == configuredKey;
        }

        return Enum.TryParse<Key>(ShortcutCatalog.DefaultOcrKey, true, out var fallbackKey) && key == fallbackKey;
    }

    public void RefreshHotkeyPrompts()
    {
        OnPropertyChanged(nameof(OcrHotkeyGestureText));
        OnPropertyChanged(nameof(ScreenOcrButtonToolTip));
        OnPropertyChanged(nameof(OcrStatusBarGestureText));
    }

    private bool _isScreenOcrInProgress;
    public bool IsScreenOcrInProgress
    {
        get => _isScreenOcrInProgress;
        private set
        {
            if (SetProperty(ref _isScreenOcrInProgress, value))
            {
                OnPropertyChanged(nameof(ScreenOcrButtonText));
                (ScreenOcrCommand as RelayCommand)?.RaiseCanExecuteChanged();
            }
        }
    }

    public string ScreenOcrButtonText => IsScreenOcrInProgress ? "Распознавание…" : "Снимок";

    public Action<string, string, MessageBoxImage>? AlertHandler { get; set; }
    public Func<string, string, MessageBoxButton, MessageBoxImage, MessageBoxResult>? ConfirmHandler { get; set; }
    public Func<UnsavedEditorDecision>? RequestUnsavedEditorDecision { get; set; }
    public Func<bool>? RequestDraftDiscardDecision { get; set; }
    public Func<CloseMainWindowDecision>? RequestCloseMainWindowDecision { get; set; }

    private bool _hotkeyBusy;
    private string _searchQuery = string.Empty;
    private TagTreeItemViewModel? _selectedTag;
    private NavigationSectionItemViewModel? _selectedSection;
    private string _statusText = string.Empty;
    private List<Tag> _allTagsCache = new();
    private double _navigationPanelWidth = WorkspaceLayoutHelper.DefaultNavWidth;
    private NavigationPanelState _navigationPanelState = NavigationPanelState.Normal;
    private double _noteListPanelWidth = WorkspaceLayoutHelper.DefaultListWidth;
    private bool _isNarrow;
    private bool _isDetailActiveInNarrow;
    private bool _isFullscreen;
    private WorkspaceViewMode _workspaceViewMode = WorkspaceViewMode.List;
    private WorkspaceViewMode _focusReturnViewMode = WorkspaceViewMode.List;
    private NoteEditorViewModel? _detailEditor;
    private bool _compactCards;
    private NoteSortMode _sortMode = NoteSortMode.Pinned;
    private NoteSortOption _selectedSortModeItem = null!;
    private NoteCardViewModel? _selectedNote;
    private bool _hasLibraryNotes;
    private bool _hasLockedProtectedNotes;
    private TaskCardViewModel? _selectedTask;
    private TaskNavigationResult? _pendingTaskNavigation;
    private bool _lastTaskNavigationWasExact;
    private Task _taskRefreshTask = Task.CompletedTask;
    private bool _isTasksBusy;
    private readonly List<SavedWorkspaceView> _savedViews = new();

    public ObservableCollection<NavigationSectionItemViewModel> VirtualSections { get; } = new();
    public ObservableCollection<SavedViewItemViewModel> SavedViews { get; } = new();
    public ObservableCollection<NoteCardViewModel> Notes { get; } = new();
    public ObservableCollection<TaskCardViewModel> Tasks { get; } = new();
    public ObservableCollection<TagTreeItemViewModel> TagTreeRoots { get; } = new();
    public ObservableCollection<SearchChipViewModel> ActiveFilterChips { get; } = new();
    public List<NoteSortOption> AvailableSortModes { get; }

    public NoteSortOption SelectedSortModeItem
    {
        get => _selectedSortModeItem;
        set
        {
            if (SetProperty(ref _selectedSortModeItem, value) && value != null)
            {
                SortMode = value.Mode;
            }
        }
    }

    public NoteSortMode SortMode
    {
        get => _sortMode;
        set
        {
            if (SetProperty(ref _sortMode, value))
            {
                CancelSearch();
                var match = AvailableSortModes.FirstOrDefault(m => m.Mode == value);
                if (match != null && _selectedSortModeItem != match)
                {
                    _selectedSortModeItem = match;
                    OnPropertyChanged(nameof(SelectedSortModeItem));
                }
                PersistLayout();
                RefreshNotes();
            }
        }
    }

    public NoteCardViewModel? SelectedNote
    {
        get => _selectedNote;
        set
        {
            if (ReferenceEquals(_selectedNote, value))
            {
                return;
            }

            if (!CanReplaceDetailEditor(value))
            {
                OnPropertyChanged(nameof(SelectedNote));
                return;
            }

            var previous = _selectedNote;
            if (ReferenceEquals(previous, value))
            {
                return;
            }

            if (previous != null)
            {
                previous.PropertyChanged -= OnSelectedNotePropertyChanged;
            }

            _selectedNote = value;
            OnPropertyChanged(nameof(SelectedNote));

            LoadNoteIntoDetail(value);

            if (value != null)
            {
                value.PropertyChanged += OnSelectedNotePropertyChanged;
            }

            NotifySyncStatusActionChanged();
        }
    }

    public TaskCardViewModel? SelectedTask
    {
        get => _selectedTask;
        set
        {
            if (SetProperty(ref _selectedTask, value) && value != null)
            {
                OpenTask(value);
            }
        }
    }

    public string NotesHeaderTitle => SelectedSection?.Title ?? SelectedTag?.Name ?? "Все заметки";

    public string SearchQuery
    {
        get => _searchQuery;
        set
        {
            if (SetProperty(ref _searchQuery, value))
            {
                CancelSearch();
                UpdateFilterChips();
                NotifyEmptyStateProperties();
                _searchDebouncer.Debounce(() => RefreshNotes());
            }
        }
    }

    public NavigationSectionItemViewModel? SelectedSection
    {
        get => _selectedSection;
        set
        {
            if (SetProperty(ref _selectedSection, value))
            {
                CancelSearch();
                foreach (var s in VirtualSections)
                {
                    s.IsSelected = s == value;
                }

                if (value != null)
                {
                    // Clear tag selection when virtual section is selected
                    if (_selectedTag != null)
                    {
                        _selectedTag.IsSelected = false;
                        _selectedTag = null;
                        OnPropertyChanged(nameof(SelectedTag));
                    }
                }
                RefreshNotes();
                OnPropertyChanged(nameof(IsTrashSelected));
                OnPropertyChanged(nameof(IsTasksSelected));
                OnPropertyChanged(nameof(HasActiveFilter));
                OnPropertyChanged(nameof(NotesHeaderTitle));
                OnPropertyChanged(nameof(CurrentListCount));
                OnPropertyChanged(nameof(IsNotesListVisible));
                NotifyEmptyStateProperties();
            }
        }
    }

    public TagTreeItemViewModel? SelectedTag
    {
        get => _selectedTag;
        set
        {
            if (SetProperty(ref _selectedTag, value))
            {
                CancelSearch();
                if (value != null)
                {
                    // Clear virtual section selection when tag is selected
                    if (_selectedSection != null)
                    {
                        _selectedSection.IsSelected = false;
                        _selectedSection = null;
                        OnPropertyChanged(nameof(SelectedSection));
                        OnPropertyChanged(nameof(IsTrashSelected));
                        OnPropertyChanged(nameof(IsTasksSelected));
                        OnPropertyChanged(nameof(IsNotesListVisible));
                        OnPropertyChanged(nameof(ShowNotesEmptyState));
                        OnPropertyChanged(nameof(ShowTasksEmptyState));
                        OnPropertyChanged(nameof(CurrentListCount));
                    }
                }
                OnPropertyChanged(nameof(HasSelectedTag));
                OnPropertyChanged(nameof(SelectedTagFullPath));
                OnPropertyChanged(nameof(HasActiveFilter));
                OnPropertyChanged(nameof(NotesHeaderTitle));
                RefreshNotes();
                NotifyEmptyStateProperties();
            }
        }
    }

    public bool HasSelectedTag => SelectedTag != null;
    public string? SelectedTagFullPath => SelectedTag?.FullPath;

    public bool IsTrashSelected => SelectedSection?.Section == NavigationSection.Trash;
    public bool IsTasksSelected => SelectedSection?.Section == NavigationSection.Tasks;
    public bool IsNotesListVisible => !IsTasksSelected;
    public int CurrentListCount => IsTasksSelected ? Tasks.Count : Notes.Count;
    public bool ShowNotesEmptyState => !IsTasksSelected && Notes.Count == 0;
    public bool ShowTasksEmptyState => IsTasksSelected && Tasks.Count == 0 && !IsTasksBusy;
    public bool HasLibraryNotes => _hasLibraryNotes;
    public bool HasLockedProtectedNotes => _hasLockedProtectedNotes;
    public bool HasSearchOrTagFilter =>
        !string.IsNullOrWhiteSpace(SearchQuery) || SelectedTag != null;

    public NotesEmptyKind NotesEmptyKind
    {
        get
        {
            if (IsTasksSelected)
            {
                return NotesEmptyKind.None;
            }

            if (!ShowNotesEmptyState)
            {
                return NotesEmptyKind.None;
            }

            if (IsTrashSelected)
            {
                return HasSearchOrTagFilter ? NotesEmptyKind.NoMatches : NotesEmptyKind.EmptyTrash;
            }

            if (HasSearchOrTagFilter)
            {
                return NotesEmptyKind.NoMatches;
            }

            if (!_hasLibraryNotes)
            {
                return NotesEmptyKind.EmptyLibrary;
            }

            if (SelectedSection != null && SelectedSection.Section != NavigationSection.All)
            {
                return NotesEmptyKind.EmptySection;
            }

            return NotesEmptyKind.EmptyLibrary;
        }
    }

    public bool ShowEmptyStateResetFilter =>
        (ShowNotesEmptyState && (NotesEmptyKind is NotesEmptyKind.NoMatches or NotesEmptyKind.EmptySection)) ||
        (ShowTasksEmptyState && !string.IsNullOrWhiteSpace(SearchQuery));

    public bool ShowProtectedSearchHint
    {
        get
        {
            if (!_hasLockedProtectedNotes)
            {
                return false;
            }

            string freeText = SearchQueryParser.Parse(SearchQuery, _allTagsCache).FreeText;
            return !string.IsNullOrWhiteSpace(freeText) && (ShowNotesEmptyState || ShowTasksEmptyState);
        }
    }

    public string EmptyStateProtectedHint =>
        "Закрытые защищённые заметки в поиск не входят, пока вы не откроете их паролем. Их заголовки и текст здесь не показываются.";

    public string EmptyStateTitle
    {
        get
        {
            if (IsTasksSelected)
            {
                return string.IsNullOrWhiteSpace(SearchQuery) ? "Нет открытых задач" : "Задачи не найдены";
            }

            return NotesEmptyKind switch
            {
                NotesEmptyKind.EmptyTrash => "Корзина пуста",
                NotesEmptyKind.NoMatches => "Ничего не найдено",
                NotesEmptyKind.EmptySection => $"В разделе «{SelectedSection?.Title}» пусто",
                _ => "Заметок пока нет"
            };
        }
    }

    public string EmptyStateDescription
    {
        get
        {
            if (IsTasksSelected)
            {
                return string.IsNullOrWhiteSpace(SearchQuery)
                    ? "Открытые Markdown-checkbox из незащищённых заметок появятся здесь."
                    : "Измените запрос или сбросьте фильтр.";
            }

            return NotesEmptyKind switch
            {
                NotesEmptyKind.EmptyTrash => "Удалённые заметки появятся здесь.",
                NotesEmptyKind.NoMatches => "Измените запрос или сбросьте фильтр, чтобы снова увидеть заметки.",
                NotesEmptyKind.EmptySection => "В этом разделе нет заметок. Можно показать все заметки или создать новую.",
                _ => "Нажмите «Новая заметка» или выделите текст в любой программе и нажмите горячую клавишу."
            };
        }
    }

    public bool IsTasksBusy
    {
        get => _isTasksBusy;
        private set
        {
            if (SetProperty(ref _isTasksBusy, value))
            {
                OnPropertyChanged(nameof(ShowTasksEmptyState));
                NotifyEmptyStateProperties();
            }
        }
    }

    public Task TaskRefreshTask => _taskRefreshTask;

    public bool HasActiveFilter =>
        !string.IsNullOrWhiteSpace(SearchQuery) ||
        SelectedTag != null ||
        (SelectedSection != null && SelectedSection.Section != NavigationSection.All);

    public NoteEditorViewModel? DetailEditor
    {
        get => _detailEditor;
        private set => SetProperty(ref _detailEditor, value);
    }

    public bool IsNarrow
    {
        get => _isNarrow;
        set => SetProperty(ref _isNarrow, value);
    }

    public bool IsDetailActiveInNarrow
    {
        get => _isDetailActiveInNarrow;
        set => SetProperty(ref _isDetailActiveInNarrow, value);
    }

    /// <summary>
    /// Current workspace representation. Changing it only persists the setting;
    /// it never commits a draft, creates a revision, or enqueues Sync.
    /// <see cref="WorkspaceViewMode.Focus"/> is transient: the persisted value is
    /// always the List/Board mode to return to.
    /// </summary>
    public WorkspaceViewMode WorkspaceViewMode
    {
        get => _workspaceViewMode;
        set
        {
            var sanitized = SanitizeViewMode(value);
            if (!SetProperty(ref _workspaceViewMode, sanitized))
            {
                return;
            }

            OnPropertyChanged(nameof(IsBoardMode));
            OnPropertyChanged(nameof(IsListViewMode));
            OnPropertyChanged(nameof(IsFocusMode));
            if (sanitized != WorkspaceViewMode.Focus && _navigationPanelState == NavigationPanelState.Hidden)
            {
                NavigationPanelState = NavigationPanelState.Normal;
            }
            PersistLayout();
        }
    }

    public bool IsBoardMode
    {
        get => EffectiveToggleViewMode() == WorkspaceViewMode.Board;
        set => RequestWorkspaceViewMode(value ? WorkspaceViewMode.Board : WorkspaceViewMode.List);
    }

    public bool IsListViewMode => EffectiveToggleViewMode() == WorkspaceViewMode.List;

    public bool IsFocusMode => _workspaceViewMode == WorkspaceViewMode.Focus;

    /// <summary>List/Board mode that Focus returns to (defaults to List).</summary>
    public WorkspaceViewMode FocusReturnViewMode => _focusReturnViewMode;

    private WorkspaceViewMode EffectiveToggleViewMode()
        => _workspaceViewMode == WorkspaceViewMode.Focus ? _focusReturnViewMode : _workspaceViewMode;

    /// <summary>
    /// Requests a List/Board representation. When Focus is active this is a
    /// leave-Focus request and therefore goes through the unsaved/journal contract.
    /// </summary>
    public void RequestWorkspaceViewMode(WorkspaceViewMode target)
    {
        target = target == WorkspaceViewMode.Board ? WorkspaceViewMode.Board : WorkspaceViewMode.List;
        if (_workspaceViewMode == WorkspaceViewMode.Focus)
        {
            LeaveFocus(target);
            return;
        }

        WorkspaceViewMode = target;
    }

    /// <summary>
    /// Opens Focus for the selected note in the existing detail editor surface.
    /// Replacing a different dirty editor follows the existing leave contract.
    /// </summary>
    public bool EnterFocus(NoteCardViewModel? card)
    {
        if (card == null || _isDisposed)
        {
            return false;
        }

        var returnMode = _workspaceViewMode == WorkspaceViewMode.Board
            ? WorkspaceViewMode.Board
            : WorkspaceViewMode.List;

        if (!LoadNoteIntoDetail(card))
        {
            return false;
        }

        if (_workspaceViewMode != WorkspaceViewMode.Focus)
        {
            _focusReturnViewMode = returnMode;
            WorkspaceViewMode = WorkspaceViewMode.Focus;
        }

        return true;
    }

    /// <summary>
    /// Leaves Focus for the given List/Board mode using the shared unsaved/journal
    /// contract. Returns false (and stays in Focus) when the user stays or a save fails.
    /// </summary>
    public bool LeaveFocus(WorkspaceViewMode target)
    {
        target = target == WorkspaceViewMode.Board ? WorkspaceViewMode.Board : WorkspaceViewMode.List;
        if (_workspaceViewMode != WorkspaceViewMode.Focus)
        {
            WorkspaceViewMode = target;
            return true;
        }

        if (!TryLeaveDetailEditor())
        {
            OnPropertyChanged(nameof(IsBoardMode));
            OnPropertyChanged(nameof(IsListViewMode));
            return false;
        }

        _focusReturnViewMode = target;
        WorkspaceViewMode = target;
        if (_navigationPanelState == NavigationPanelState.Hidden)
        {
            NavigationPanelState = NavigationPanelState.Normal;
        }
        return true;
    }

    private static WorkspaceViewMode SanitizeViewMode(WorkspaceViewMode mode)
    {
        return Enum.IsDefined(typeof(WorkspaceViewMode), mode) ? mode : WorkspaceViewMode.List;
    }

    private static WorkspaceViewMode SanitizePersistedViewMode(WorkspaceViewMode mode)
    {
        return mode == WorkspaceViewMode.Focus ? WorkspaceViewMode.List : SanitizeViewMode(mode);
    }

    public double NavigationPanelWidth
    {
        get => _navigationPanelWidth;
        set
        {
            double clamped = WorkspaceLayoutHelper.ClampNavWidth(value);
            if (SetProperty(ref _navigationPanelWidth, clamped))
            {
                OnPropertyChanged(nameof(TagPanelWidth));
                PersistLayout();
            }
        }
    }

    public double TagPanelWidth
    {
        get => NavigationPanelWidth;
        set => NavigationPanelWidth = value;
    }

    public NavigationPanelState NavigationPanelState
    {
        get => _navigationPanelState;
        set
        {
            var clamped = WorkspaceLayoutHelper.ClampNavigationPanelState(value, _isFullscreen && IsFocusMode);
            if (SetProperty(ref _navigationPanelState, clamped))
            {
                OnPropertyChanged(nameof(IsCompactNav));
                OnPropertyChanged(nameof(IsNavHidden));
                OnPropertyChanged(nameof(NavigationStateToggleText));
                PersistLayout();
            }
        }
    }

    public bool IsCompactNav => _navigationPanelState == NavigationPanelState.Compact;
    public bool IsNavHidden => _navigationPanelState == NavigationPanelState.Hidden;
    public string NavigationStateToggleText => _navigationPanelState switch
    {
        NavigationPanelState.Compact => "Развернуть навигацию",
        NavigationPanelState.Hidden => "Показать навигацию",
        _ => "Свернуть навигацию"
    };

    public bool IsFullscreen
    {
        get => _isFullscreen;
        set
        {
            if (SetProperty(ref _isFullscreen, value))
            {
                OnPropertyChanged(nameof(FullscreenButtonText));
                OnPropertyChanged(nameof(FullscreenButtonTooltip));
                OnPropertyChanged(nameof(FullscreenHintText));
                if (!value && _navigationPanelState == NavigationPanelState.Hidden)
                {
                    NavigationPanelState = NavigationPanelState.Normal;
                }
            }
        }
    }

    public string FullscreenButtonText => _isFullscreen ? "Обычный экран" : "Во весь экран";
    public string FullscreenButtonTooltip => _isFullscreen
        ? "Выйти из полноэкранного режима (F11, Esc)"
        : "Полноэкранный режим (F11)";
    public string FullscreenHintText => _isFullscreen
        ? "F11: выйти из полноэкранного режима | "
        : "";

    public ICommand ToggleFullscreenCommand { get; }
    public ICommand ToggleNavigationStateCommand { get; }
    public ICommand RestoreNavigationCommand { get; }
    public Action? RequestToggleFullscreen { get; set; }

    public double NoteListPanelWidth
    {
        get => _noteListPanelWidth;
        set
        {
            double clamped = WorkspaceLayoutHelper.ClampListWidth(value);
            if (SetProperty(ref _noteListPanelWidth, clamped))
                PersistLayout();
        }
    }

    public double? WindowLeft => _settingsService.CurrentSettings.WindowLeft;
    public double? WindowTop => _settingsService.CurrentSettings.WindowTop;
    public double WindowWidth => _settingsService.CurrentSettings.WindowWidth;
    public double WindowHeight => _settingsService.CurrentSettings.WindowHeight;
    public System.Windows.WindowState WindowState => _settingsService.CurrentSettings.WindowState;
    public SettingsService SettingsService => _settingsService;

    public ICommand BackToMasterCommand { get; }
    public ICommand OpenNoteInDetailCommand { get; }
    public ICommand SetListViewModeCommand { get; }
    public ICommand SetBoardViewModeCommand { get; }

    public bool CompactCards
    {
        get => _compactCards;
        set
        {
            if (SetProperty(ref _compactCards, value))
            {
                OnPropertyChanged(nameof(CompactButtonText));
                PersistLayout();
                RefreshNotes();
            }
        }
    }

    public string CompactButtonText => CompactCards ? "Комфортно" : "Компактно";

    public const int DefaultPageSize = 50;
    public int PageSize { get; set; } = DefaultPageSize;

    private int _totalNotesCount;
    public int TotalNotesCount
    {
        get => _totalNotesCount;
        private set => SetProperty(ref _totalNotesCount, value);
    }

    private bool _hasMoreNotes;
    public bool HasMoreNotes
    {
        get => _hasMoreNotes;
        private set => SetProperty(ref _hasMoreNotes, value);
    }

    public string LoadMoreButtonText => $"Загрузить ещё ({CurrentListCount} из {TotalNotesCount})";

    public string StatusText
    {
        get => _statusText;
        set => SetProperty(ref _statusText, value);
    }

    // Commands
    public ICommand NewNoteCommand { get; }
    public ICommand LoadMoreNotesCommand { get; }
    public ICommand OpenTaskCommand { get; }

    public const string TaskNavigationFallbackStatus = "Заметка открыта; точная строка задачи неоднозначна.";

    public bool LastTaskNavigationWasExact => _lastTaskNavigationWasExact;
    public ICommand CreateEmptyNoteCommand { get; }
    public ICommand CreateFromTemplateCommand { get; }
    public ICommand OpenTemplateManagementCommand { get; }
    public ICommand ScreenOcrCommand { get; }
    public ICommand ResetFilterCommand { get; }
    public ICommand ClearSearchCommand { get; }
    public ICommand ToggleCompactCommand { get; }
    public ICommand RescanAllCommand { get; }
    public ICommand SuggestTagsCommand { get; }
    public ICommand OpenSettingsCommand { get; }
    public ICommand OpenImportExportCommand { get; }
    public ICommand OpenNoteAssemblyCommand { get; }
    public ICommand OpenHelpCommand { get; }
    public ICommand OpenCloudSetupWizardCommand { get; }
    public ICommand SyncNowCommand { get; }
    public ICommand OpenSyncConflictsCommand { get; }
    public ICommand SyncStatusActionCommand { get; }
    public ICommand MarkProcessedAndNextCommand { get; }
    public ICommand ApplySearchExampleCommand { get; }

    public ICommand EditNoteCommand { get; }
    public ICommand DeleteNoteCommand { get; }
    public ICommand RescanNoteCommand { get; }

    public ICommand TogglePinCommand { get; }
    public ICommand ToggleFavoriteCommand { get; }
    public ICommand MarkProcessedCommand { get; }
    public ICommand MoveToTrashCommand { get; }
    public ICommand RestoreNoteCommand { get; }
    public ICommand PermanentDeleteCommand { get; }
    public ICommand EmptyTrashCommand { get; }

    public ICommand SelectSectionCommand { get; }

    public ICommand SaveCurrentViewCommand { get; }
    public ICommand ApplySavedViewCommand { get; }
    public ICommand RenameSavedViewCommand { get; }
    public ICommand DeleteSavedViewCommand { get; }
    public bool HasSavedViews => SavedViews.Count > 0;

    /// <summary>Opens the local view-name editor (wired by MainWindow; tests may replace it).</summary>
    public Func<SavedViewEditViewModel, bool?>? RequestOpenSavedViewEditor { get; set; }

    public ICommand AddRootTagCommand { get; }
    public ICommand AddChildTagCommand { get; }
    public ICommand RenameTagCommand { get; }
    public ICommand ChangeParentTagCommand { get; }
    public ICommand ManageSynonymsCommand { get; }
    public ICommand ManageRulesCommand { get; }
    public ICommand MergeTagCommand { get; }
    public ICommand DeleteTagCommand { get; }

    public event Func<NoteEditorViewModel, bool?>? RequestOpenNoteEditor;
    public event Func<TagEditViewModel, bool?>? RequestOpenTagEditor;
    public event Func<TagSynonymsViewModel, bool?>? RequestOpenSynonymsEditor;
    public event Func<TagRuleViewModel, bool?>? RequestOpenTagRules;
    public event Func<ChangeParentViewModel, bool?>? RequestOpenChangeParentEditor;
    public event Func<TagMergeViewModel, bool?>? RequestOpenTagMerge;
    public event Func<TagRescanPreviewViewModel, bool?>? RequestOpenRescanPreview;
    public event Func<TagSuggestionsViewModel, bool?>? RequestOpenTagSuggestions;
    public event Func<SettingsViewModel, bool?>? RequestOpenSettings;
    public event Func<ImportExportViewModel, bool?>? RequestOpenImportExport;
    public event Func<NoteAssemblyViewModel, bool?>? RequestOpenNoteAssembly;
    public event Action? RequestOpenHelp;
    public event Action? RequestOpenCloudSetupWizard;
    public event Action? RequestOpenFirstRun;
    public event Action? RequestFocusNotesList;
    public event Func<SyncConflictsViewModel, bool?>? RequestOpenSyncConflicts;
    public event Action? RequestBringToFront;

    public Func<TemplateManagementViewModel, bool?>? RequestOpenTemplateManagement { get; set; }
    public Func<IReadOnlyList<NoteTemplate>, NoteTemplate?>? RequestTemplateSelection { get; set; }

    public ObservableCollection<NoteTemplate> Templates { get; } = new();
    public bool HasTemplates => Templates.Count > 0;

    public Action<string, string>? NotificationHandler { get; set; }

    private readonly TagRuleService _tagRuleService;
    public TagRuleService TagRuleService => _tagRuleService;

    private readonly INoteTemplateService _noteTemplateService;
    public INoteTemplateService NoteTemplateService => _noteTemplateService;

    private readonly ITemplateExpansionService _templateExpansionService;
    public ITemplateExpansionService TemplateExpansionService => _templateExpansionService;

    public MainViewModel(
        CoreNotesServices core,
        CaptureServices capture,
        SyncCloudServices syncCloud,
        SecurityServices security,
        TasksRemindersServices tasks,
        UiHostServices ui)
    {
        ArgumentNullException.ThrowIfNull(core);
        ArgumentNullException.ThrowIfNull(capture);
        ArgumentNullException.ThrowIfNull(syncCloud);
        ArgumentNullException.ThrowIfNull(security);
        ArgumentNullException.ThrowIfNull(tasks);
        ArgumentNullException.ThrowIfNull(ui);

        _contextFactory = core.ContextFactory;
        _uiDispatcher = Thread.CurrentThread.GetApartmentState() == ApartmentState.STA
            ? System.Windows.Threading.Dispatcher.CurrentDispatcher
            : System.Windows.Threading.Dispatcher.FromThread(Thread.CurrentThread);
        _mutationCoordinator = core.MutationCoordinator;
        _tagDetectionService = core.TagDetection;
        _searchService = core.Search;
        _settingsService = core.Settings;
        _hotkeyService = capture.Hotkeys;
        _clipboardCaptureService = capture.Clipboard;
        _trayIconService = ui.Tray;
        _attachmentStorageService = core.Attachments;
        _backupService = ui.Backup;
        _noteProtectionService = security.Protection;
        _noteProtectionSessions = (_noteProtectionService as Services.NoteProtection.NoteProtectionService)?.SessionStore ?? new Services.NoteProtection.ProtectedNoteSessionStore();
        _draftJournalService = core.DraftJournal;
        _tagMergeService = core.TagMerge;
        _tagRuleService = core.TagRules;
        _tagRescanPreviewService = core.TagRescan;
        _tagSuggestionService = core.TagSuggestions;
        _noteHistoryService = core.History;
        _noteExportService = core.Export;
        _noteImportService = core.Import;
        _noteAssemblyService = core.Assembly;
        _taskIndexService = tasks.TaskIndex;
        _taskReminderScheduler = tasks.Reminders;
        _taskReminderScheduler.StatusChanged += OnReminderStatusChanged;
        _taskReminderScheduler.NoteActivationRequested += OpenNoteFromReminderActivation;
        _noteLinkService = core.Links;
        _screenCaptureService = capture.ScreenCapture;
        _ocrService = capture.Ocr;
        _screenOcrCoordinator = capture.ScreenOcr;
        _noteTemplateService = core.Templates;
        _templateExpansionService = core.TemplateExpansion;
        _syncCloud = syncCloud;
        _credentialsStorage = syncCloud.Credentials;
        _passwordStorage = syncCloud.Password;
        _deviceIdProvider = syncCloud.DeviceId;
        _conflictService = syncCloud.Conflicts;
        _syncScheduler = syncCloud.Scheduler;

        _syncScheduler.StatusChanged += OnSchedulerStatusChanged;
        _syncScheduler.SyncCompleted += OnSchedulerSyncCompleted;
        _syncOwnership.DrainCompleted += OnManualSyncLeaseDrained;

        _noteTemplateService.TemplateChanged += () => _syncScheduler?.EnqueueLocalChange();
        _refreshCoalescer = ui.RefreshCoalescer;
        _searchDebouncer = ui.SearchDebouncer;


        _navigationPanelWidth = WorkspaceLayoutHelper.ClampNavWidth(_settingsService.CurrentSettings.NavigationPanelWidth);
        _navigationPanelState = WorkspaceLayoutHelper.ClampNavigationPanelState(_settingsService.CurrentSettings.NavigationPanelState, isFullscreenFocus: false);
        _noteListPanelWidth = WorkspaceLayoutHelper.ClampListWidth(_settingsService.CurrentSettings.NoteListPanelWidth);
        _compactCards = _settingsService.CurrentSettings.CompactCards;
        _workspaceViewMode = SanitizePersistedViewMode(_settingsService.CurrentSettings.WorkspaceViewMode);

        AvailableSortModes = new List<NoteSortOption>
        {
            new(NoteSortMode.Pinned, "Сначала закреплённые"),
            new(NoteSortMode.CreatedAt, "По дате создания"),
            new(NoteSortMode.UpdatedAt, "По дате изменения")
        };
        _sortMode = _settingsService.CurrentSettings.SortMode;
        _selectedSortModeItem = AvailableSortModes.FirstOrDefault(m => m.Mode == _sortMode) ?? AvailableSortModes[0];

        var allSection = new NavigationSectionItemViewModel(NavigationSection.All, "Все заметки", "");
        allSection.IsSelected = true;
        VirtualSections.Add(allSection);
        VirtualSections.Add(new NavigationSectionItemViewModel(NavigationSection.Tasks, "Задачи", ""));
        VirtualSections.Add(new NavigationSectionItemViewModel(NavigationSection.Inbox, "Входящие", ""));
        VirtualSections.Add(new NavigationSectionItemViewModel(NavigationSection.Favorites, "Избранное", ""));
        VirtualSections.Add(new NavigationSectionItemViewModel(NavigationSection.Today, "Сегодня", ""));
        VirtualSections.Add(new NavigationSectionItemViewModel(NavigationSection.Recent, "Недавние", ""));
        VirtualSections.Add(new NavigationSectionItemViewModel(NavigationSection.Untagged, "Без тегов", ""));
        VirtualSections.Add(new NavigationSectionItemViewModel(NavigationSection.Trash, "Корзина", ""));
        _selectedSection = allSection;

        LoadSavedViewsFromSettings();

        NewNoteCommand = new RelayCommand(CreateNewNote);
        LoadMoreNotesCommand = new RelayCommand(LoadMoreNotes);
        OpenTaskCommand = new RelayCommand(param => OpenTask(param as TaskCardViewModel));
        CreateEmptyNoteCommand = new RelayCommand(CreateEmptyNote);
        CreateFromTemplateCommand = new RelayCommand(param => CreateNoteFromTemplate(param as NoteTemplate));
        OpenTemplateManagementCommand = new RelayCommand(OpenTemplateManagement);
        ScreenOcrCommand = new RelayCommand(async () => await StartScreenOcrAsync(), () => !IsScreenOcrInProgress);
        ResetFilterCommand = new RelayCommand(ResetFilter);
        ClearSearchCommand = new RelayCommand(ClearSearch);
        ToggleCompactCommand = new RelayCommand(() => CompactCards = !CompactCards);
        ToggleFullscreenCommand = new RelayCommand(ToggleFullscreen);
        ToggleNavigationStateCommand = new RelayCommand(ToggleNavigationState);
        RestoreNavigationCommand = new RelayCommand(RestoreNavigation);
        RescanAllCommand = new RelayCommand(RescanAllNotes);
        SuggestTagsCommand = new RelayCommand(SuggestTags);
        OpenSettingsCommand = new RelayCommand(OpenSettings);
        OpenImportExportCommand = new RelayCommand(OpenImportExport);
        OpenNoteAssemblyCommand = new RelayCommand(OpenNoteAssembly);
        OpenHelpCommand = new RelayCommand(() => RequestOpenHelp?.Invoke());
        OpenCloudSetupWizardCommand = new RelayCommand(() => RequestOpenCloudSetupWizard?.Invoke());
        SyncNowCommand = new RelayCommand(async () => await SyncNowAsync(), () => !IsSyncBusy);
        OpenSyncConflictsCommand = new RelayCommand(OpenSyncConflicts);
        SyncStatusActionCommand = new RelayCommand(ExecuteSyncStatusAction, CanExecuteSyncStatusAction);
        MarkProcessedAndNextCommand = new RelayCommand(param => MarkProcessedAndNext((param as NoteCardViewModel)?.Id));
        ApplySearchExampleCommand = new RelayCommand(param => ApplySearchExample(param as string));
        RefreshCloudUsageCommand = new RelayCommand(async () => await RefreshCloudUsageAsync());

        EditNoteCommand = new RelayCommand(param => EditNote(param as NoteCardViewModel));
        DeleteNoteCommand = new RelayCommand(param => DeleteNote(param as NoteCardViewModel));
        RescanNoteCommand = new RelayCommand(param => RescanNote(param as NoteCardViewModel));
        BackToMasterCommand = new RelayCommand(() => IsDetailActiveInNarrow = false);
        SetListViewModeCommand = new RelayCommand(() => RequestWorkspaceViewMode(WorkspaceViewMode.List));
        SetBoardViewModeCommand = new RelayCommand(() => RequestWorkspaceViewMode(WorkspaceViewMode.Board));
        OpenNoteInDetailCommand = new RelayCommand(param =>
        {
            if (param is NoteCardViewModel card)
            {
                SelectedNote = card;
                if (IsNarrow && WorkspaceViewMode != WorkspaceViewMode.Board)
                {
                    IsDetailActiveInNarrow = true;
                }
            }
        });

        TogglePinCommand = new RelayCommand(param => TogglePinNote((param as NoteCardViewModel)?.Id));
        ToggleFavoriteCommand = new RelayCommand(param => ToggleFavoriteNote((param as NoteCardViewModel)?.Id));
        MarkProcessedCommand = new RelayCommand(param => MarkNoteProcessed((param as NoteCardViewModel)?.Id));
        MoveToTrashCommand = new RelayCommand(param => MoveNoteToTrash((param as NoteCardViewModel)?.Id));
        RestoreNoteCommand = new RelayCommand(param => RestoreNote((param as NoteCardViewModel)?.Id));
        PermanentDeleteCommand = new RelayCommand(param => PermanentDeleteNote((param as NoteCardViewModel)?.Id));
        EmptyTrashCommand = new RelayCommand(EmptyTrash);

        SelectSectionCommand = new RelayCommand(param =>
        {
            if (param is NavigationSectionItemViewModel sectionItem)
            {
                SelectedSection = sectionItem;
            }
        });

        SaveCurrentViewCommand = new RelayCommand(SaveCurrentView);
        ApplySavedViewCommand = new RelayCommand(param => ApplySavedView(param as SavedViewItemViewModel));
        RenameSavedViewCommand = new RelayCommand(param => RenameSavedView(param as SavedViewItemViewModel));
        DeleteSavedViewCommand = new RelayCommand(param => DeleteSavedView(param as SavedViewItemViewModel));

        AddRootTagCommand = new RelayCommand(AddRootTag);
        AddChildTagCommand = new RelayCommand(param => AddChildTag(param as TagTreeItemViewModel));
        RenameTagCommand = new RelayCommand(param => RenameTag(param as TagTreeItemViewModel));
        ChangeParentTagCommand = new RelayCommand(param => ChangeParentTag(param as TagTreeItemViewModel));
        ManageSynonymsCommand = new RelayCommand(param => ManageSynonyms(param as TagTreeItemViewModel));
        ManageRulesCommand = new RelayCommand(param => ManageRules(param as TagTreeItemViewModel));
        MergeTagCommand = new RelayCommand(param => MergeTag(param as TagTreeItemViewModel));
        DeleteTagCommand = new RelayCommand(param => DeleteTag(param as TagTreeItemViewModel));

        // Global hotkey events
        _hotkeyService.EditorHotkeyPressed += OnHotkeyPressed;
        _hotkeyService.InstantSaveHotkeyPressed += OnInstantSaveHotkeyPressed;
        _hotkeyService.OcrHotkeyPressed += () =>
        {
            if (ScreenOcrCommand.CanExecute(null))
            {
                AsyncEventBridge.Fire(StartScreenOcrAsync, "MainViewModel.OcrHotkey");
            }
        };

        // Tray events
        _trayIconService.OpenRequested += () => RequestBringToFront?.Invoke();
        _trayIconService.NewNoteRequested += CreateNewNote;
        _trayIconService.ScreenOcrRequested += () =>
        {
            if (ScreenOcrCommand.CanExecute(null))
            {
                AsyncEventBridge.Fire(StartScreenOcrAsync, "MainViewModel.TrayOcr");
            }
        };
        _trayIconService.SettingsRequested += OpenSettings;

        // Initial data load
        ReloadAll();
        PurgeExpiredTrash();
        _layoutReady = true;
        AsyncEventBridge.Fire(() => RefreshSyncStateAsync(), "MainViewModel.RefreshSyncState");
    }

    private bool _layoutReady;

    public void ReloadAll()
    {
        ReloadTags();
        ReloadTemplates();
        RefreshNotes();
    }

    public void ReloadTags()
    {
        using var db = _contextFactory();
        _allTagsCache = db.Tags
            .Include(t => t.Synonyms)
            .ToList();

        var tagCounts = QueryTagNoteCounts(db, _allTagsCache);

        BuildTagTree(tagCounts);
        UpdateFilterChips();
    }

    private static Dictionary<int, int> QueryTagNoteCounts(QuickNotesDbContext db, IEnumerable<Tag> tags)
    {
        var activePairs = db.NoteTags
            .Where(nt => !nt.IsSuppressed && nt.Note.DeletedAt == null)
            .Select(nt => new { nt.TagId, nt.NoteId })
            .Distinct()
            .ToList()
            .Select(p => (p.TagId, p.NoteId));

        return TagHierarchyService.CalculateTagNoteCounts(tags, activePairs);
    }

    private void BuildTagTree(Dictionary<int, int>? tagCounts = null)
    {
        var selectedId = SelectedTag?.Id;
        var expandedIds = GetExpandedTagIds(TagTreeRoots);
        TagTreeRoots.Clear();

        var nodesDict = _allTagsCache.ToDictionary(
            t => t.Id,
            t =>
            {
                var fullPath = TagHierarchyService.GetFullPath(t.Id, _allTagsCache);
                var breadcrumb = TagHierarchyService.GetBreadcrumbPath(t.Id, _allTagsCache);
                int count = tagCounts != null && tagCounts.TryGetValue(t.Id, out var c) ? c : 0;
                return new TagTreeItemViewModel(t.Id, t.Name, t.ParentTagId)
                {
                    FullPath = fullPath,
                    Breadcrumb = breadcrumb,
                    NoteCount = count,
                    IsExpanded = expandedIds.Count > 0 ? expandedIds.Contains(t.Id) : true
                };
            });

        foreach (var tag in _allTagsCache)
        {
            var node = nodesDict[tag.Id];
            if (tag.ParentTagId.HasValue && nodesDict.TryGetValue(tag.ParentTagId.Value, out var parentNode))
            {
                parentNode.Children.Add(node);
            }
            else
            {
                TagTreeRoots.Add(node);
            }
        }

        if (selectedId.HasValue && nodesDict.TryGetValue(selectedId.Value, out var selectedNode))
        {
            SelectedTag = selectedNode;
            selectedNode.IsSelected = true;
        }
        else if (selectedId.HasValue)
        {
            SelectedTag = null;
        }

        OnPropertyChanged(nameof(HasSelectedTag));
        OnPropertyChanged(nameof(SelectedTagFullPath));
    }

    private static HashSet<int> GetExpandedTagIds(IEnumerable<TagTreeItemViewModel> items)
    {
        var set = new HashSet<int>();
        void Traverse(TagTreeItemViewModel item)
        {
            if (item.IsExpanded) set.Add(item.Id);
            foreach (var child in item.Children) Traverse(child);
        }
        foreach (var item in items) Traverse(item);
        return set;
    }

    public void UpdateFilterChips()
    {
        var parsed = SearchQueryParser.Parse(_searchQuery, _allTagsCache);
        ActiveFilterChips.Clear();
        foreach (var chip in parsed.Chips)
        {
            ActiveFilterChips.Add(new SearchChipViewModel(chip, RemoveFilterChip));
        }
    }

    public void RemoveFilterChip(SearchFilterChipModel chip)
    {
        _searchDebouncer.Cancel();
        CancelSearch();
        var newQuery = SearchQueryParser.RemoveChip(SearchQuery, chip);
        _searchQuery = newQuery;
        OnPropertyChanged(nameof(SearchQuery));
        UpdateFilterChips();
        RefreshNotes();
    }

    private CancellationTokenSource? _searchCts;
    private IReadOnlyCollection<int>? _currentProtectedNoteIds;
    private int _searchGeneration;

    private void CancelSearch()
    {
        Interlocked.Increment(ref _searchGeneration);
        try
        {
            _searchCts?.Cancel();
        }
        catch (ObjectDisposedException) { }
    }

    private void RunOnDispatcher(Action action)
    {
        var dispatcher = _uiDispatcher;
        if (dispatcher != null && !dispatcher.HasShutdownStarted && !dispatcher.HasShutdownFinished)
        {
            if (dispatcher.CheckAccess())
            {
                action();
                return;
            }

            try
            {
                dispatcher.BeginInvoke(action);
                return;
            }
            catch
            {
                // Fall through to a direct call if the dispatcher has already torn down.
            }
        }

        action();
    }

    public void RefreshNotes()
    {
        if (_isDisposed) return;
        _searchDebouncer.Cancel();
        _taskReminderScheduler.Refresh();

        if (IsTasksSelected)
        {
            BeginRefreshTasks(reset: true);
            return;
        }

        Tasks.Clear();
        SelectedTask = null;

        int generation = Interlocked.Increment(ref _searchGeneration);
        try
        {
            _searchCts?.Cancel();
            _searchCts?.Dispose();
        }
        catch (ObjectDisposedException) { }

        var cts = new CancellationTokenSource();
        _searchCts = cts;
        var token = cts.Token;

        using var db = _contextFactory();
        var section = SelectedSection?.Section ?? NavigationSection.All;

        var freeText = SearchQueryParser.Parse(SearchQuery, _allTagsCache).FreeText;

        // In-memory search over unlocked protected notes (never persisted to FTS).
        List<int>? protectedInMemoryIds = null;
        if (!string.IsNullOrWhiteSpace(freeText) && section != NavigationSection.Trash)
        {
            var protectedHits = _noteProtectionService.SearchUnlockedInMemory(freeText);
            if (protectedHits.Count > 0)
            {
                protectedInMemoryIds = protectedHits.Select(h => h.NoteId).Distinct().ToList();
            }
        }
        _currentProtectedNoteIds = protectedInMemoryIds;

        int totalCount;
        List<Note> notes;
        try
        {
            totalCount = _searchService.CountNotes(
                db,
                SearchQuery,
                SelectedTag?.Id,
                _allTagsCache,
                section,
                protectedInMemoryIds,
                cancellationToken: token);

            if (token.IsCancellationRequested || generation != Volatile.Read(ref _searchGeneration)) return;

            notes = _searchService.QueryNotes(
                db,
                SearchQuery,
                SelectedTag?.Id,
                _allTagsCache,
                section,
                _sortMode,
                protectedInMemoryIds,
                skip: 0,
                take: PageSize,
                cancellationToken: token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (token.IsCancellationRequested || generation != Volatile.Read(ref _searchGeneration)) return;
        TotalNotesCount = totalCount;
        UpdateLibraryPresence(db);

        var previouslySelectedId = SelectedNote?.Id;
        var expanded = Notes.Where(n => n.IsExpanded).Select(n => n.Id).ToHashSet();
        Notes.Clear();
        NoteCardViewModel? toSelect = null;

        HashSet<Guid>? pendingSyncIds = null;
        HashSet<Guid>? conflictSyncIds = null;
        var syncSettings = _settingsService.CurrentSettings.CloudSync;
        bool isSyncConfigured = syncSettings != null && syncSettings.Enabled && !string.IsNullOrWhiteSpace(syncSettings.Bucket);
        if (isSyncConfigured)
        {
            try
            {
                var deviceId = _deviceIdProvider.GetDeviceId();
                pendingSyncIds = SyncSnapshotHelper.GetPendingNoteSyncIds(db, deviceId);
                conflictSyncIds = db.SyncConflicts
                    .AsNoTracking()
                    .Where(c => !c.IsResolved && c.EntityType == "Note")
                    .Select(c => c.SyncId)
                    .ToHashSet();
            }
            catch (Exception ex)
            {
                ErrorLogService.Write("MainViewModel.RefreshNotes.SyncStatus", ex);
            }
        }

        LocalCommitSyncStatus DetermineCardPublicationStatus(Note n)
        {
            if (!isSyncConfigured)
            {
                return LocalCommitSyncStatus.SavedLocally;
            }
            if (conflictSyncIds != null && conflictSyncIds.Contains(n.SyncId))
            {
                return LocalCommitSyncStatus.Conflict;
            }
            if (pendingSyncIds != null && pendingSyncIds.Contains(n.SyncId))
            {
                if (_isSyncBusy || (_syncScheduler?.IsSyncRunning ?? false))
                {
                    return LocalCommitSyncStatus.Syncing;
                }
                if (_syncStatus == MainSyncStatus.Error || _syncScheduler?.PublicationStatus == LocalCommitSyncStatus.Error)
                {
                    return LocalCommitSyncStatus.Error;
                }
                return LocalCommitSyncStatus.PendingUpload;
            }
            return LocalCommitSyncStatus.Synchronized;
        }

        foreach (var note in notes)
        {
            if (token.IsCancellationRequested || generation != Volatile.Read(ref _searchGeneration)) return;
            int noteId = note.Id;
            var card = new NoteCardViewModel(note, freeText, CompactCards)
            {
                IsExpanded = expanded.Contains(note.Id),
                PublicationStatus = DetermineCardPublicationStatus(note),
                TogglePinCommand = new RelayCommand(() => TogglePinNote(noteId)),
                ToggleFavoriteCommand = new RelayCommand(() => ToggleFavoriteNote(noteId)),
                MarkProcessedCommand = new RelayCommand(() => MarkNoteProcessed(noteId)),
                MarkProcessedAndNextCommand = new RelayCommand(() => MarkProcessedAndNext(noteId)),
                MoveToTrashCommand = new RelayCommand(() => MoveNoteToTrash(noteId)),
                RestoreCommand = new RelayCommand(() => RestoreNote(noteId)),
                PermanentDeleteCommand = new RelayCommand(() => PermanentDeleteNote(noteId))
            };

            if (note.Id == previouslySelectedId)
            {
                toSelect = card;
            }
            Notes.Add(card);
        }

        HasMoreNotes = Notes.Count < TotalNotesCount;
        OnPropertyChanged(nameof(LoadMoreButtonText));
        OnPropertyChanged(nameof(CurrentListCount));
        NotifyEmptyStateProperties();

        SelectedNote = toSelect ?? Notes.FirstOrDefault();
        OnPropertyChanged(nameof(SyncStatusSummaryText));
        OnPropertyChanged(nameof(SyncStatusTooltip));

        UpdateTagCounts(db);
        UpdateInboxCount(db);
        UpdateStatusText();
    }

    private void UpdateTagCounts(QuickNotesDbContext db)
    {
        var tagCounts = QueryTagNoteCounts(db, _allTagsCache);
        UpdateTreeCounts(TagTreeRoots, tagCounts);
        if (SelectedTag != null && tagCounts.TryGetValue(SelectedTag.Id, out var count))
        {
            SelectedTag.NoteCount = count;
        }
    }

    private static void UpdateTreeCounts(IEnumerable<TagTreeItemViewModel> items, Dictionary<int, int> counts)
    {
        foreach (var item in items)
        {
            item.NoteCount = counts.TryGetValue(item.Id, out var count) ? count : 0;
            UpdateTreeCounts(item.Children, counts);
        }
    }

    private void UpdateInboxCount(QuickNotesDbContext db)
    {
        int count = _searchService.GetInboxCount(db);
        var inbox = VirtualSections.FirstOrDefault(s => s.Section == NavigationSection.Inbox);
        if (inbox != null)
        {
            inbox.BadgeCount = count;
        }
    }

    private void UpdateStatusText()
    {
        string status;
        if (IsTasksSelected)
        {
            status = TotalNotesCount > Tasks.Count
                ? $"Задачи: {Tasks.Count} из {TotalNotesCount}"
                : $"Задачи: {Tasks.Count}";
        }
        else
        {
            status = TotalNotesCount > Notes.Count
                ? $"Заметок: {Notes.Count} из {TotalNotesCount}"
                : $"Заметок: {Notes.Count}";
        }
        if (SelectedSection != null)
        {
            status += $" | Раздел: {SelectedSection.Title}";
        }
        else if (SelectedTag != null)
        {
            status += $" | Тег: {SelectedTag.FullPath}";
        }
        if (!string.IsNullOrWhiteSpace(SearchQuery))
        {
            status += $" | Поиск: \"{SearchQuery}\"";
        }
        StatusText = status;
    }

    private void BeginRefreshTasks(bool reset)
    {
        if (_isDisposed) return;

        int generation = Interlocked.Increment(ref _searchGeneration);
        try
        {
            _searchCts?.Cancel();
            _searchCts?.Dispose();
        }
        catch (ObjectDisposedException) { }

        var cts = new CancellationTokenSource();
        _searchCts = cts;
        var token = cts.Token;
        int skip = reset ? 0 : Tasks.Count;
        int take = PageSize;
        string search = SearchQuery;
        IsTasksBusy = true;

        _taskRefreshTask = _taskIndexService.QueryAsync(search, skip, take, token)
            .ContinueWith(t =>
            {
                RunOnDispatcher(() =>
                {
                    if (_isDisposed || generation != Volatile.Read(ref _searchGeneration))
                    {
                        return;
                    }

                    try
                    {
                        if (t.IsCanceled || token.IsCancellationRequested)
                        {
                            return;
                        }

                        if (t.IsFaulted)
                        {
                            if (t.Exception?.GetBaseException() is OperationCanceledException)
                            {
                                return;
                            }

                            ErrorLogService.Write("MainViewModel.RefreshTasks", t.Exception?.GetBaseException() ?? t.Exception!);
                            return;
                        }

                        ApplyTaskPage(t.Result, reset);
                    }
                    finally
                    {
                        if (generation == Volatile.Read(ref _searchGeneration))
                        {
                            IsTasksBusy = false;
                        }
                    }
                });
            }, CancellationToken.None);
    }

    private void ApplyTaskPage(TaskIndexPage page, bool reset)
    {
        Notes.Clear();
        if (reset)
        {
            Tasks.Clear();
            _selectedTask = null;
            OnPropertyChanged(nameof(SelectedTask));
        }

        var existing = Tasks.Select(t => (t.NoteId, t.Locator.LineNumber, t.Locator.Fingerprint)).ToHashSet();
        foreach (var locator in page.Items)
        {
            var key = (locator.NoteId, locator.LineNumber, locator.Fingerprint);
            if (!reset && existing.Contains(key))
            {
                continue;
            }

            Tasks.Add(new TaskCardViewModel(locator));
        }

        TotalNotesCount = page.TotalCount;
        HasMoreNotes = Tasks.Count < page.TotalCount;
        OnPropertyChanged(nameof(LoadMoreButtonText));
        OnPropertyChanged(nameof(CurrentListCount));
        OnPropertyChanged(nameof(EmptyStateTitle));
        OnPropertyChanged(nameof(EmptyStateDescription));
        OnPropertyChanged(nameof(ShowNotesEmptyState));
        OnPropertyChanged(nameof(ShowTasksEmptyState));
        UpdateStatusText();
    }

    public void OpenTask(TaskCardViewModel? task)
    {
        if (task == null || _isDisposed)
        {
            return;
        }

        using var db = _contextFactory();
        var note = db.Notes.AsNoTracking().FirstOrDefault(n => n.Id == task.NoteId && n.DeletedAt == null);
        if (note == null || note.IsProtected)
        {
            return;
        }

        var nav = MarkdownTaskNavigator.Resolve(note.Text, task.Locator);
        _pendingTaskNavigation = nav;
        _lastTaskNavigationWasExact = !nav.IsFallback;
        OnPropertyChanged(nameof(LastTaskNavigationWasExact));
        var card = new NoteCardViewModel(note, null, CompactCards);
        if (_detailEditor != null && _detailEditor.NoteId == note.Id)
        {
            ApplyPendingTaskNavigation();
            return;
        }

        if (!LoadNoteIntoDetail(card))
        {
            return;
        }
        ApplyPendingTaskNavigation();
        if (IsNarrow)
        {
            IsDetailActiveInNarrow = true;
        }
    }

    public void ApplyPendingTaskNavigation()
    {
        var nav = _pendingTaskNavigation;
        _pendingTaskNavigation = null;
        var editor = DetailEditor;
        if (editor == null || nav == null)
        {
            return;
        }

        int textLength = editor.Text?.Length ?? 0;
        if (nav.IsFallback)
        {
            editor.SetSelection?.Invoke(0, 0);
            StatusText = TaskNavigationFallbackStatus;
            return;
        }

        int start = nav.SelectionStart;
        int length = nav.SelectionLength;
        start = Math.Clamp(start, 0, Math.Max(0, textLength));
        length = Math.Clamp(length, 0, Math.Max(0, textLength - start));
        editor.SetSelection?.Invoke(start, length);
        RequestScrollEditorToSelection?.Invoke();
    }

    public Action? RequestScrollEditorToSelection { get; set; }

    public void OpenNoteFromReminderActivation(Guid noteSyncId)
    {
        if (_isDisposed || noteSyncId == Guid.Empty)
        {
            return;
        }

        RunOnDispatcher(() =>
        {
            if (_isDisposed)
            {
                return;
            }

            RequestBringToFront?.Invoke();
            using var db = _contextFactory();
            var note = db.Notes.AsNoTracking().FirstOrDefault(n => n.SyncId == noteSyncId && n.DeletedAt == null);
            if (note == null || note.IsProtected)
            {
                return;
            }

            var card = new NoteCardViewModel(note, null, CompactCards);
            LoadNoteIntoDetail(card);
            if (DetailEditor?.NoteId != note.Id)
            {
                return;
            }
            if (IsNarrow)
            {
                IsDetailActiveInNarrow = true;
            }
        });
    }

    private void OnReminderStatusChanged()
    {
        RunOnDispatcher(() =>
        {
            if (_isDisposed)
            {
                return;
            }

            OnPropertyChanged(nameof(TaskRemindersStatusText));
        });
    }

    public void LoadMoreNotes()
    {
        if (_isDisposed || !HasMoreNotes) return;

        if (IsTasksSelected)
        {
            BeginRefreshTasks(reset: false);
            return;
        }

        int generation = Volatile.Read(ref _searchGeneration);
        var cts = _searchCts;
        if (cts == null || cts.IsCancellationRequested) return;
        var token = cts.Token;

        if (token.IsCancellationRequested || generation != Volatile.Read(ref _searchGeneration))
        {
            return;
        }

        using var db = _contextFactory();
        var section = SelectedSection?.Section ?? NavigationSection.All;
        int currentCount = Notes.Count;

        var notes = _searchService.QueryNotes(
            db,
            SearchQuery,
            SelectedTag?.Id,
            _allTagsCache,
            section,
            _sortMode,
            _currentProtectedNoteIds,
            skip: currentCount,
            take: PageSize,
            cancellationToken: token);

        if (token.IsCancellationRequested || generation != Volatile.Read(ref _searchGeneration))
        {
            return;
        }

        if (notes.Count == 0)
        {
            HasMoreNotes = false;
            OnPropertyChanged(nameof(LoadMoreButtonText));
            return;
        }

        var freeText = SearchQueryParser.Parse(SearchQuery, _allTagsCache).FreeText;

        HashSet<Guid>? pendingSyncIds = null;
        HashSet<Guid>? conflictSyncIds = null;
        var syncSettings = _settingsService.CurrentSettings.CloudSync;
        bool isSyncConfigured = syncSettings != null && syncSettings.Enabled && !string.IsNullOrWhiteSpace(syncSettings.Bucket);
        if (isSyncConfigured)
        {
            try
            {
                var deviceId = _deviceIdProvider.GetDeviceId();
                pendingSyncIds = SyncSnapshotHelper.GetPendingNoteSyncIds(db, deviceId);
                conflictSyncIds = db.SyncConflicts
                    .AsNoTracking()
                    .Where(c => !c.IsResolved && c.EntityType == "Note")
                    .Select(c => c.SyncId)
                    .ToHashSet();
            }
            catch (Exception ex)
            {
                ErrorLogService.Write("MainViewModel.LoadMoreNotes.SyncStatus", ex);
            }
        }

        LocalCommitSyncStatus DetermineCardPublicationStatus(Note n)
        {
            if (!isSyncConfigured) return LocalCommitSyncStatus.SavedLocally;
            if (conflictSyncIds != null && conflictSyncIds.Contains(n.SyncId)) return LocalCommitSyncStatus.Conflict;
            if (pendingSyncIds != null && pendingSyncIds.Contains(n.SyncId))
            {
                if (_isSyncBusy || (_syncScheduler?.IsSyncRunning ?? false)) return LocalCommitSyncStatus.Syncing;
                if (_syncStatus == MainSyncStatus.Error || _syncScheduler?.PublicationStatus == LocalCommitSyncStatus.Error) return LocalCommitSyncStatus.Error;
                return LocalCommitSyncStatus.PendingUpload;
            }
            return LocalCommitSyncStatus.Synchronized;
        }

        var existingIds = Notes.Select(n => n.Id).ToHashSet();
        foreach (var note in notes)
        {
            if (token.IsCancellationRequested || generation != Volatile.Read(ref _searchGeneration))
            {
                return;
            }
            if (existingIds.Contains(note.Id)) continue;
            int noteId = note.Id;
            var card = new NoteCardViewModel(note, freeText, CompactCards)
            {
                IsExpanded = false,
                PublicationStatus = DetermineCardPublicationStatus(note),
                TogglePinCommand = new RelayCommand(() => TogglePinNote(noteId)),
                ToggleFavoriteCommand = new RelayCommand(() => ToggleFavoriteNote(noteId)),
                MarkProcessedCommand = new RelayCommand(() => MarkNoteProcessed(noteId)),
                MarkProcessedAndNextCommand = new RelayCommand(() => MarkProcessedAndNext(noteId)),
                MoveToTrashCommand = new RelayCommand(() => MoveNoteToTrash(noteId)),
                RestoreCommand = new RelayCommand(() => RestoreNote(noteId)),
                PermanentDeleteCommand = new RelayCommand(() => PermanentDeleteNote(noteId))
            };

            Notes.Add(card);
        }

        HasMoreNotes = Notes.Count < TotalNotesCount;
        OnPropertyChanged(nameof(LoadMoreButtonText));
        UpdateStatusText();
    }

    public void ResetFilter()
    {
        SearchQuery = string.Empty;
        if (_selectedTag != null)
        {
            _selectedTag.IsSelected = false;
            _selectedTag = null;
            OnPropertyChanged(nameof(SelectedTag));
            OnPropertyChanged(nameof(HasSelectedTag));
            OnPropertyChanged(nameof(SelectedTagFullPath));
        }

        var all = VirtualSections.FirstOrDefault(s => s.Section == NavigationSection.All);
        foreach (var s in VirtualSections) s.IsSelected = s == all;
        _selectedSection = all;
        OnPropertyChanged(nameof(SelectedSection));
        OnPropertyChanged(nameof(IsTrashSelected));
        OnPropertyChanged(nameof(IsTasksSelected));
        OnPropertyChanged(nameof(IsNotesListVisible));
        OnPropertyChanged(nameof(HasActiveFilter));
        RefreshNotes();
        NotifyEmptyStateProperties();
    }

    private void LoadSavedViewsFromSettings()
    {
        _savedViews.Clear();
        SavedViews.Clear();

        var stored = _settingsService.CurrentSettings.SavedWorkspaceViews;
        if (stored != null)
        {
            foreach (var view in stored)
            {
                if (view == null || string.IsNullOrWhiteSpace(view.Name))
                {
                    continue;
                }

                view.ViewMode = SanitizePersistedViewMode(view.ViewMode);
                _savedViews.Add(view);
                SavedViews.Add(new SavedViewItemViewModel(view));
            }
        }

        OnPropertyChanged(nameof(HasSavedViews));
    }

    private static List<int> ExtractExcludedTagIds(string? query, IEnumerable<Tag> allTags)
    {
        var result = new List<int>();
        var parsed = SearchQueryParser.Parse(query, allTags);
        foreach (var chip in parsed.Chips)
        {
            if (chip.Type == ChipType.Tag && chip.IsNegated && chip.Condition is TagCondition tagCondition)
            {
                if (!result.Contains(tagCondition.TagId))
                {
                    result.Add(tagCondition.TagId);
                }
            }
        }

        return result;
    }

    private TagTreeItemViewModel? FindTagNode(int tagId)
    {
        TagTreeItemViewModel? Find(IEnumerable<TagTreeItemViewModel> nodes)
        {
            foreach (var node in nodes)
            {
                if (node.Id == tagId)
                {
                    return node;
                }

                var nested = Find(node.Children);
                if (nested != null)
                {
                    return nested;
                }
            }

            return null;
        }

        return Find(TagTreeRoots);
    }

    /// <summary>
    /// Captures the current search / section / tag / sort / mode / density into a new
    /// saved view. Creating a view only writes local UI settings: it never copies notes,
    /// creates revisions, or enqueues Sync.
    /// </summary>
    public SavedWorkspaceView CaptureCurrentView(string name)
    {
        var view = new SavedWorkspaceView
        {
            Id = Guid.NewGuid(),
            Name = name.Trim(),
            Section = SelectedSection?.Section ?? NavigationSection.All,
            IncludedTagIds = SelectedTag != null ? new List<int> { SelectedTag.Id } : new List<int>(),
            ExcludedTagIds = ExtractExcludedTagIds(SearchQuery, _allTagsCache),
            SearchQuery = SearchQuery ?? string.Empty,
            SortMode = _sortMode,
            ViewMode = EffectiveToggleViewMode() == WorkspaceViewMode.Board
                ? WorkspaceViewMode.Board
                : WorkspaceViewMode.List,
            CompactCards = _compactCards
        };
        return view;
    }

    private void SaveCurrentView()
    {
        if (_isDisposed)
        {
            return;
        }

        var editor = new SavedViewEditViewModel("Новый сохранённый вид");
        if (RequestOpenSavedViewEditor?.Invoke(editor) != true)
        {
            return;
        }

        var view = CaptureCurrentView(editor.ViewName);
        _savedViews.Add(view);
        SavedViews.Add(new SavedViewItemViewModel(view));
        OnPropertyChanged(nameof(HasSavedViews));
        PersistSavedViews();
    }

    private void RenameSavedView(SavedViewItemViewModel? item)
    {
        if (item == null || _isDisposed)
        {
            return;
        }

        var editor = new SavedViewEditViewModel("Переименование вида", item.Name);
        if (RequestOpenSavedViewEditor?.Invoke(editor) != true)
        {
            return;
        }

        item.View.Name = editor.ViewName.Trim();
        item.UpdateFrom(item.View);
        PersistSavedViews();
    }

    private void DeleteSavedView(SavedViewItemViewModel? item)
    {
        if (item == null || _isDisposed)
        {
            return;
        }

        var confirm = ShowConfirm(
            $"Удалить сохранённый вид «{item.Name}»?\n\nЗаметки не будут удалены: вид — это только фильтр.",
            "Удаление вида",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.OK)
        {
            return;
        }

        _savedViews.RemoveAll(v => v.Id == item.Id);
        SavedViews.Remove(item);
        OnPropertyChanged(nameof(HasSavedViews));
        PersistSavedViews();
    }

    /// <summary>
    /// Applies a saved view: sets the existing search / section / tag / sort / mode /
    /// density properties and triggers the existing refresh. It never copies notes,
    /// creates revisions, or enqueues Sync. When the open Focus editor is dirty the
    /// shared leave contract is used first, exactly like switching List/Board.
    /// </summary>
    public bool ApplySavedView(SavedViewItemViewModel? item)
    {
        if (item == null || _isDisposed)
        {
            return false;
        }

        if (!TryLeaveDetailEditor())
        {
            return false;
        }

        var view = item.View;

        var targetMode = view.ViewMode == WorkspaceViewMode.Board
            ? WorkspaceViewMode.Board
            : WorkspaceViewMode.List;
        if (IsFocusMode)
        {
            LeaveFocus(targetMode);
        }
        else
        {
            WorkspaceViewMode = targetMode;
        }

        _searchDebouncer.Cancel();
        CancelSearch();

        var includedTagIds = view.IncludedTagIds ?? new List<int>();
        var excludedTagIds = view.ExcludedTagIds ?? new List<int>();

        // The persisted tag-id lists are authoritative: pick the first included id that
        // resolves in the current tag tree as the selected tag, then express all remaining
        // included and excluded ids through the existing search query.
        TagTreeItemViewModel? selectedTagNode = null;
        foreach (var tagId in includedTagIds)
        {
            var node = FindTagNode(tagId);
            if (node != null)
            {
                selectedTagNode = node;
                break;
            }
        }

        _searchQuery = BuildSavedViewSearchQuery(view.SearchQuery, includedTagIds, excludedTagIds, selectedTagNode?.Id);
        OnPropertyChanged(nameof(SearchQuery));
        UpdateFilterChips();

        _sortMode = view.SortMode;
        var match = AvailableSortModes.FirstOrDefault(m => m.Mode == view.SortMode);
        if (match != null)
        {
            _selectedSortModeItem = match;
            OnPropertyChanged(nameof(SelectedSortModeItem));
        }
        OnPropertyChanged(nameof(SortMode));

        _compactCards = view.CompactCards;
        OnPropertyChanged(nameof(CompactCards));
        OnPropertyChanged(nameof(CompactButtonText));

        if (selectedTagNode != null)
        {
            _selectedSection = null;
            foreach (var s in VirtualSections)
            {
                s.IsSelected = false;
            }
            OnPropertyChanged(nameof(SelectedSection));

            if (_selectedTag != null && !ReferenceEquals(_selectedTag, selectedTagNode))
            {
                _selectedTag.IsSelected = false;
            }
            _selectedTag = selectedTagNode;
            selectedTagNode.IsSelected = true;
            OnPropertyChanged(nameof(SelectedTag));
            OnPropertyChanged(nameof(HasSelectedTag));
            OnPropertyChanged(nameof(SelectedTagFullPath));
        }
        else
        {
            if (_selectedTag != null)
            {
                _selectedTag.IsSelected = false;
                _selectedTag = null;
                OnPropertyChanged(nameof(SelectedTag));
                OnPropertyChanged(nameof(HasSelectedTag));
                OnPropertyChanged(nameof(SelectedTagFullPath));
            }

            var section = VirtualSections.FirstOrDefault(s => s.Section == view.Section)
                          ?? VirtualSections.FirstOrDefault(s => s.Section == NavigationSection.All);
            foreach (var s in VirtualSections)
            {
                s.IsSelected = s == section;
            }
            _selectedSection = section;
            OnPropertyChanged(nameof(SelectedSection));
        }

        OnPropertyChanged(nameof(IsTrashSelected));
        OnPropertyChanged(nameof(IsTasksSelected));
        OnPropertyChanged(nameof(IsNotesListVisible));
        OnPropertyChanged(nameof(HasActiveFilter));
        OnPropertyChanged(nameof(NotesHeaderTitle));
        OnPropertyChanged(nameof(CurrentListCount));
        NotifyEmptyStateProperties();

        foreach (var saved in SavedViews)
        {
            saved.IsSelected = ReferenceEquals(saved, item);
        }

        PersistLayout();
        RefreshNotes();
        return true;
    }

    /// <summary>
    /// Builds the effective search text for a saved view. The persisted
    /// <see cref="SavedWorkspaceView.IncludedTagIds"/> and
    /// <see cref="SavedWorkspaceView.ExcludedTagIds"/> lists are authoritative for tags: the
    /// first resolvable included id is applied through <see cref="SelectedTag"/>, and every
    /// other resolved included id / excluded id is expressed as a <c>tag:Name</c> /
    /// <c>-tag:Name</c> token. Tokens already present in the free-text query are not
    /// duplicated and unknown ids are skipped without throwing.
    /// </summary>
    private string BuildSavedViewSearchQuery(
        string? baseQuery,
        IReadOnlyList<int> includedTagIds,
        IReadOnlyList<int> excludedTagIds,
        int? selectedTagId)
    {
        string query = baseQuery ?? string.Empty;
        var existing = GetExistingTagChips(query);
        var additions = new List<string>();

        foreach (var tagId in includedTagIds)
        {
            if (tagId == selectedTagId || existing.Positive.Contains(tagId))
            {
                continue;
            }

            var name = GetTagNameById(tagId);
            if (name == null)
            {
                continue;
            }

            additions.Add("tag:" + FormatTagToken(name));
            existing.Positive.Add(tagId);
        }

        foreach (var tagId in excludedTagIds)
        {
            if (existing.Negative.Contains(tagId))
            {
                continue;
            }

            var name = GetTagNameById(tagId);
            if (name == null)
            {
                continue;
            }

            additions.Add("-tag:" + FormatTagToken(name));
            existing.Negative.Add(tagId);
        }

        if (additions.Count == 0)
        {
            return query;
        }

        string trimmed = query.Trim();
        string suffix = string.Join(" ", additions);
        return trimmed.Length == 0 ? suffix : trimmed + " " + suffix;
    }

    private (HashSet<int> Positive, HashSet<int> Negative) GetExistingTagChips(string query)
    {
        var positive = new HashSet<int>();
        var negative = new HashSet<int>();
        var parsed = SearchQueryParser.Parse(query, _allTagsCache);
        foreach (var chip in parsed.Chips)
        {
            if (chip.Type != ChipType.Tag || chip.Condition is not TagCondition tagCondition)
            {
                continue;
            }

            if (chip.IsNegated)
            {
                negative.Add(tagCondition.TagId);
            }
            else
            {
                positive.Add(tagCondition.TagId);
            }
        }

        return (positive, negative);
    }

    private string? GetTagNameById(int tagId)
    {
        foreach (var tag in _allTagsCache)
        {
            if (tag.Id == tagId)
            {
                return string.IsNullOrWhiteSpace(tag.Name) ? null : tag.Name;
            }
        }

        return null;
    }

    private static string FormatTagToken(string name)
    {
        bool needsQuotes = name.Any(ch => char.IsWhiteSpace(ch) || ch == '(' || ch == ')' || ch == '"');
        return needsQuotes ? "\"" + name.Replace("\"", string.Empty) + "\"" : name;
    }

    public void PersistSavedViews()
    {
        if (!_layoutReady)
        {
            return;
        }

        try
        {
            var settings = _settingsService.CurrentSettings;
            settings.SavedWorkspaceViews = _savedViews.Select(v => v.Clone()).ToList();
            _settingsService.SaveSettings(settings);
        }
        catch (Exception ex)
        {
            ErrorLogService.Write("MainViewModel.PersistSavedViews", ex);
        }
    }

    public CloseMainWindowDecision DecideUserClose()
    {
        var settings = _settingsService.CurrentSettings;
        if (settings.CloseToTrayPromptCompleted)
        {
            return settings.PreferCloseToTray
                ? CloseMainWindowDecision.Hide
                : CloseMainWindowDecision.Exit;
        }

        if (RequestCloseMainWindowDecision == null)
        {
            return CloseMainWindowDecision.Hide;
        }

        var decision = RequestCloseMainWindowDecision();
        if (decision is CloseMainWindowDecision.Hide or CloseMainWindowDecision.Exit)
        {
            RememberCloseToTrayChoice(closeToTray: decision == CloseMainWindowDecision.Hide);
        }

        return decision;
    }

    public void RememberCloseToTrayChoice(bool closeToTray)
    {
        var settings = _settingsService.CurrentSettings;
        settings.PreferCloseToTray = closeToTray;
        settings.CloseToTrayPromptCompleted = true;
        _settingsService.SaveSettings(settings);
    }

    private void UpdateLibraryPresence(QuickNotesDbContext db)
    {
        bool hasLibrary = db.Notes.Any(n => n.DeletedAt == null);
        var protectedIds = db.Notes
            .Where(n => n.DeletedAt == null && n.IsProtected)
            .Select(n => n.Id)
            .ToList();
        bool hasLocked = protectedIds.Exists(id => !_noteProtectionService.IsUnlocked(id));

        if (hasLibrary != _hasLibraryNotes)
        {
            _hasLibraryNotes = hasLibrary;
            OnPropertyChanged(nameof(HasLibraryNotes));
        }

        if (hasLocked != _hasLockedProtectedNotes)
        {
            _hasLockedProtectedNotes = hasLocked;
            OnPropertyChanged(nameof(HasLockedProtectedNotes));
        }
    }

    private void NotifyEmptyStateProperties()
    {
        OnPropertyChanged(nameof(ShowNotesEmptyState));
        OnPropertyChanged(nameof(ShowTasksEmptyState));
        OnPropertyChanged(nameof(NotesEmptyKind));
        OnPropertyChanged(nameof(EmptyStateTitle));
        OnPropertyChanged(nameof(EmptyStateDescription));
        OnPropertyChanged(nameof(ShowEmptyStateResetFilter));
        OnPropertyChanged(nameof(ShowProtectedSearchHint));
        OnPropertyChanged(nameof(HasSearchOrTagFilter));
    }

    public void ClearSearch()
    {
        SearchQuery = string.Empty;
    }

    public void ToggleFullscreen()
    {
        if (RequestToggleFullscreen != null)
        {
            RequestToggleFullscreen.Invoke();
        }
        else
        {
            IsFullscreen = !IsFullscreen;
        }
    }

    public void ToggleNavigationState()
    {
        if (_isFullscreen && IsFocusMode)
        {
            NavigationPanelState = _navigationPanelState switch
            {
                NavigationPanelState.Normal => NavigationPanelState.Compact,
                NavigationPanelState.Compact => NavigationPanelState.Hidden,
                _ => NavigationPanelState.Normal
            };
        }
        else
        {
            NavigationPanelState = _navigationPanelState == NavigationPanelState.Compact
                ? NavigationPanelState.Normal
                : NavigationPanelState.Compact;
        }
    }

    public void RestoreNavigation()
    {
        NavigationPanelState = NavigationPanelState.Normal;
    }

    public void PersistLayout()
    {
        if (!_layoutReady)
            return;

        try
        {
            var settings = _settingsService.CurrentSettings;
            settings.NavigationPanelWidth = _navigationPanelWidth;
            settings.NavigationPanelState = WorkspaceLayoutHelper.ClampNavigationPanelState(_navigationPanelState, _isFullscreen && IsFocusMode);
            settings.NoteListPanelWidth = _noteListPanelWidth;
            settings.CompactCards = _compactCards;
            settings.SortMode = _sortMode;
            settings.WorkspaceViewMode = _workspaceViewMode == WorkspaceViewMode.Focus
                ? _focusReturnViewMode
                : _workspaceViewMode;
            _settingsService.SaveSettings(settings);
        }
        catch (Exception ex)
        {
            ErrorLogService.Write("Settings.Layout", ex);
        }
    }

    public void SaveWindowBoundsAndLayout(double left, double top, double width, double height, System.Windows.WindowState state, double navWidth, double listWidth)
    {
        try
        {
            var settings = _settingsService.CurrentSettings;
            if (state == System.Windows.WindowState.Normal &&
                !double.IsNaN(left) && !double.IsInfinity(left) &&
                !double.IsNaN(top) && !double.IsInfinity(top) &&
                !double.IsNaN(width) && !double.IsInfinity(width) &&
                !double.IsNaN(height) && !double.IsInfinity(height))
            {
                settings.WindowLeft = left;
                settings.WindowTop = top;
                settings.WindowWidth = width;
                settings.WindowHeight = height;
            }
            settings.WindowState = WorkspaceLayoutHelper.NormalizeWindowState(state);
            settings.NavigationPanelWidth = WorkspaceLayoutHelper.ClampNavWidth(navWidth);
            settings.NavigationPanelState = WorkspaceLayoutHelper.ClampNavigationPanelState(_navigationPanelState, _isFullscreen && IsFocusMode);
            settings.NoteListPanelWidth = WorkspaceLayoutHelper.ClampListWidth(listWidth);
            _navigationPanelWidth = settings.NavigationPanelWidth;
            _noteListPanelWidth = settings.NoteListPanelWidth;
            _settingsService.SaveSettings(settings);
        }
        catch (Exception ex)
        {
            ErrorLogService.Write("Settings.SaveWindowBoundsAndLayout", ex);
        }
    }

    private void PurgeExpiredTrash()
    {
        var settings = _settingsService.CurrentSettings;
        if (!settings.AutoPurgeTrashEnabled)
            return;

        int days = settings.AutoPurgeTrashDays > 0 ? settings.AutoPurgeTrashDays : 30;
        var cutoff = DateTime.Now.AddDays(-days);
        try
        {
            using var db = _contextFactory();
            int removed = _searchService.PurgeOldTrash(
                db,
                cutoff,
                onDeletedAttachments: fileNames => _attachmentStorageService.CleanupUnreferencedFiles(db, fileNames));
            if (removed > 0)
            {
                RefreshNotes();
                StatusText = $"Автоочистка корзины: удалено {removed}";
            }
        }
        catch (Exception ex)
        {
            ErrorLogService.Write("Trash.Purge", ex);
        }
    }

    private void OnHotkeyPressed()
    {
        if (_hotkeyBusy) return;
        _hotkeyBusy = true;
        AsyncEventBridge.Fire(OnHotkeyPressedCoreAsync, "MainViewModel.OnHotkeyPressed", () => _hotkeyBusy = false);
    }

    private async Task OnHotkeyPressedCoreAsync()
    {
        try
        {
            var attempt = await _clipboardCaptureService.CaptureWithDiagnosticsAsync();
            if (!attempt.Success || attempt.Context == null)
            {
                StatusText = attempt.FailureReason ?? "Не удалось захватить выделенный текст.";
                _trayIconService.ShowNotification("Захват текста не выполнен", StatusText);
                return;
            }

            var capture = attempt.Context;
            var rules = _tagRuleService.GetAllRules();
            var detectedMatches = _tagDetectionService.DetectDetailedTags(capture.Text, _allTagsCache, rules);

            var vm = new NoteEditorViewModel(
                _tagDetectionService,
                _allTagsCache,
                existingNote: null,
                initialTitle: Helpers.NoteTitleHelper.DeriveTitleFromText(capture.Text),
                initialText: capture.Text,
                initialAutoTags: detectedMatches.Select(m => m.Tag).ToList(),
                sourceProcessName: capture.ProcessName,
                sourceWindowTitle: capture.WindowTitle,
                sourceUrl: capture.Url,
                capturedAt: capture.CapturedAt,
                initialAutoMatches: detectedMatches,
                rules: rules,
                noteHistoryService: _noteHistoryService,
                contextFactory: _contextFactory,
                noteLinkService: _noteLinkService,
                attachmentStorageService: _attachmentStorageService,
                settingsService: _settingsService,
                noteProtectionService: _noteProtectionService,
                draftJournalService: _draftJournalService);

            lock (_editorsLock)
            {
                _activeEditors.Add(vm);
            }

            vm.NoteSaved += () => _syncScheduler?.EnqueueLocalChange();
            vm.Commit = () => SaveNewNoteFromEditor(vm);
            if (RequestOpenNoteEditor != null)
            {
                var result = RequestOpenNoteEditor(vm);
                if ((result == true && !string.IsNullOrWhiteSpace(vm.Text)) || vm.HasRestoredVersion)
                {
                    RefreshNotes();
                }
            }
            else
            {
                vm.Dispose();
            }
        }
        catch (Exception ex)
        {
            ErrorLogService.Write("MainViewModel.OnHotkeyPressed", ex);
            StatusText = UserFacingOperationError.GenericFailure;
            MessageBox.Show(
                UserFacingOperationError.GenericFailure,
                "Ошибка",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            _hotkeyBusy = false;
        }
    }

    private void OnInstantSaveHotkeyPressed()
    {
        if (_hotkeyBusy) return;
        _hotkeyBusy = true;
        AsyncEventBridge.Fire(OnInstantSaveHotkeyPressedCoreAsync, "MainViewModel.OnInstantSaveHotkey", () => _hotkeyBusy = false);
    }

    private async Task OnInstantSaveHotkeyPressedCoreAsync()
    {
        try
        {
            var attempt = await _clipboardCaptureService.CaptureWithDiagnosticsAsync();
            if (!attempt.Success || attempt.Context == null)
            {
                StatusText = attempt.FailureReason ?? "Не удалось сохранить выделенный текст.";
                _trayIconService.ShowNotification("Мгновенное сохранение не выполнено", StatusText);
                return;
            }

            SaveInstantNote(attempt.Context);
            RefreshNotes();
        }
        catch (Exception ex)
        {
            ErrorLogService.Write("MainViewModel.OnInstantSaveHotkeyPressed", ex);
            StatusText = UserFacingOperationError.GenericFailure;
            MessageBox.Show(
                UserFacingOperationError.GenericFailure,
                "Ошибка",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            _hotkeyBusy = false;
        }
    }

    public Note SaveInstantNote(CapturedNoteContext capture)
    {
        var rules = _tagRuleService.GetAllRules();
        var detectedTags = _tagDetectionService.DetectTags(capture.Text, _allTagsCache, rules);

        Note note = _mutationCoordinator.ExecuteNoteMutation(null, () =>
        {
            using var db = _contextFactory();
            using var tx = db.Database.BeginTransaction();
            try
            {
                var n = new Note
                {
                    Title = Helpers.NoteTitleHelper.DeriveTitleFromText(capture.Text),
                    Text = capture.Text,
                    CreatedAt = DateTime.Now,
                    UpdatedAt = DateTime.Now,
                    CapturedAt = capture.CapturedAt,
                    SourceProcessName = capture.ProcessName,
                    SourceWindowTitle = capture.WindowTitle,
                    SourceUrl = capture.Url,
                    IsInbox = true,
                    IsPinned = false,
                    IsFavorite = false,
                    DeletedAt = null
                };

                foreach (var tag in detectedTags)
                {
                    n.NoteTags.Add(new NoteTag
                    {
                        TagId = tag.Id,
                        Origin = TagOrigin.Auto,
                        IsSuppressed = false
                    });
                }

                db.Notes.Add(n);
                db.SaveChanges();

                _noteHistoryService.SaveSnapshot(db, n);
                tx.Commit();
                return n;
            }
            catch
            {
                tx.Rollback();
                throw;
            }
        });

        _syncScheduler?.EnqueueLocalChange();

        if (_settingsService.CurrentSettings.ShowNotificationOnSave)
        {
            string preview = note.Text.Length > 50 ? note.Text[..50] + "..." : note.Text;
            _trayIconService.ShowNotification("Заметка сохранена во Входящие", preview);
        }

        return note;
    }

    public void CreateEmptyNote()
    {
        var vm = new NoteEditorViewModel(
            _tagDetectionService,
            _allTagsCache,
            rules: _tagRuleService.GetAllRules(),
            noteHistoryService: _noteHistoryService,
            contextFactory: _contextFactory,
            noteLinkService: _noteLinkService,
            attachmentStorageService: _attachmentStorageService,
            settingsService: _settingsService,
            noteProtectionService: _noteProtectionService,
            draftJournalService: _draftJournalService);

        lock (_editorsLock)
        {
            _activeEditors.Add(vm);
        }

        vm.NoteSaved += () => _syncScheduler?.EnqueueLocalChange();
        vm.Commit = () => SaveNewNoteFromEditor(vm);
        if (RequestOpenNoteEditor != null)
        {
            var result = RequestOpenNoteEditor(vm);
            if ((result == true && !string.IsNullOrWhiteSpace(vm.Text)) || vm.HasRestoredVersion)
            {
                RefreshNotes();
            }
        }
        else
        {
            vm.Dispose();
        }
    }

    public IReadOnlyList<Models.DraftJournal.DraftJournalReadResult> GetUncommittedNewNoteJournals()
    {
        return _draftJournalService.GetAllJournals()
            .Where(j => j.Status == Models.DraftJournal.DraftJournalReadStatus.Success &&
                        j.Envelope != null &&
                        j.Envelope.DraftId.StartsWith("new-", StringComparison.OrdinalIgnoreCase) &&
                        (!string.IsNullOrWhiteSpace(j.DecryptedText) || !string.IsNullOrWhiteSpace(j.DecryptedTitle)))
            .ToList();
    }

    public Func<Models.DraftJournal.DraftRecoveryPromptArgs, Models.DraftJournal.DraftRecoveryChoice>? RequestNewNoteDraftRecoveryPrompt { get; set; }

    public void CheckAndPromptNewNoteRecoveries()
    {
        var uncommitted = GetUncommittedNewNoteJournals();
        foreach (var journal in uncommitted)
        {
            if (journal.Envelope == null) continue;

            string source = NoteSourceContext.FormatCompactSource(
                journal.SourceProcessName,
                journal.SourceWindowTitle,
                journal.SourceUrl);
            if (string.IsNullOrWhiteSpace(source))
            {
                source = "Новая несохранённая заметка";
            }

            var args = new Models.DraftJournal.DraftRecoveryPromptArgs
            {
                NoteId = null,
                DraftId = journal.Envelope.DraftId,
                Source = source,
                JournalTimestamp = journal.Envelope.SavedAtUtc.ToLocalTime(),
                CommittedTitle = string.Empty,
                CommittedText = string.Empty,
                CommittedTimestamp = null,
                DraftTitle = journal.DecryptedTitle ?? string.Empty,
                DraftText = journal.DecryptedText ?? string.Empty,
                DiffSummary = "Несохранённый черновик новой заметки (найден после аварийного завершения).",
                SourceProcessName = journal.SourceProcessName,
                SourceWindowTitle = journal.SourceWindowTitle,
                SourceUrl = journal.SourceUrl,
                CapturedAt = journal.CapturedAt,
                Tags = journal.Tags,
                Attachments = journal.Attachments
            };

            Models.DraftJournal.DraftRecoveryChoice choice;
            if (RequestNewNoteDraftRecoveryPrompt != null)
            {
                choice = RequestNewNoteDraftRecoveryPrompt(args);
            }
            else
            {
                choice = ShowDefaultNewNoteRecoveryDialog(args);
            }

            if (choice == Models.DraftJournal.DraftRecoveryChoice.Restore)
            {
                OpenRecoveredNewNote(journal);
            }
            else if (choice == Models.DraftJournal.DraftRecoveryChoice.Discard)
            {
                _draftJournalService.DeleteJournal(journal.Envelope.DraftId);
            }
            // KeepCommitted / cancel preserves the journal safely on disk
        }
    }

    private Models.DraftJournal.DraftRecoveryChoice ShowDefaultNewNoteRecoveryDialog(Models.DraftJournal.DraftRecoveryPromptArgs args)
    {
        try
        {
            Models.DraftJournal.DraftRecoveryChoice choice = Models.DraftJournal.DraftRecoveryChoice.KeepCommitted;
            if (System.Windows.Application.Current?.Dispatcher != null &&
                !System.Windows.Application.Current.Dispatcher.CheckAccess())
            {
                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                {
                    var dlg = new Views.DraftRecoveryWindow(args);
                    if (System.Windows.Application.Current?.MainWindow != null)
                    {
                        dlg.Owner = System.Windows.Application.Current.MainWindow;
                    }
                    dlg.ShowDialog();
                    choice = dlg.Choice;
                });
            }
            else
            {
                var dlg = new Views.DraftRecoveryWindow(args);
                if (System.Windows.Application.Current?.MainWindow != null)
                {
                    dlg.Owner = System.Windows.Application.Current.MainWindow;
                }
                dlg.ShowDialog();
                choice = dlg.Choice;
            }
            return choice;
        }
        catch (Exception ex)
        {
            ErrorLogService.Write("MainViewModel.ShowDefaultNewNoteRecoveryDialog", ex);
            return Models.DraftJournal.DraftRecoveryChoice.KeepCommitted;
        }
    }

    public void OpenRecoveredNewNote(Models.DraftJournal.DraftJournalReadResult journalResult)
    {
        if (journalResult.Envelope == null) return;
        var vm = new NoteEditorViewModel(
            _tagDetectionService,
            _allTagsCache,
            existingNote: null,
            initialTitle: journalResult.DecryptedTitle,
            initialText: journalResult.DecryptedText,
            rules: _tagRuleService.GetAllRules(),
            noteHistoryService: _noteHistoryService,
            contextFactory: _contextFactory,
            noteLinkService: _noteLinkService,
            attachmentStorageService: _attachmentStorageService,
            settingsService: _settingsService,
            noteProtectionService: _noteProtectionService,
            draftJournalService: _draftJournalService,
            initialDraftId: journalResult.Envelope.DraftId);

        // Restore complete state (source context, tags, suppressions, attachments)
        vm.SourceProcessName = journalResult.SourceProcessName;
        vm.SourceWindowTitle = journalResult.SourceWindowTitle;
        vm.SourceUrl = journalResult.SourceUrl;
        vm.CapturedAt = journalResult.CapturedAt;

        if (journalResult.Tags != null)
        {
            vm.ActiveTags.Clear();
            vm.SuppressedTagIds.Clear();
            foreach (var tag in journalResult.Tags)
            {
                if (tag.IsSuppressed)
                {
                    if (!vm.SuppressedTagIds.Contains(tag.TagId))
                        vm.SuppressedTagIds.Add(tag.TagId);
                }
                else
                {
                    vm.ActiveTags.Add(new NoteEditorTagItem(tag.TagId, tag.TagName, tag.Origin));
                }
            }
        }

        if (journalResult.Attachments != null)
        {
            vm.Attachments.Clear();
            foreach (var att in journalResult.Attachments)
            {
                string fullPath = _attachmentStorageService.GetFullPath(att.RelativePath);
                var entity = new NoteAttachment
                {
                    Id = att.Id,
                    NoteId = 0,
                    OriginalFileName = att.OriginalFileName,
                    StoredFileName = att.StoredFileName,
                    RelativePath = att.RelativePath,
                    ContentType = att.ContentType,
                    Size = att.Size,
                    Sha256 = att.Sha256,
                    CreatedAt = DateTime.Now
                };
                var item = new NoteAttachmentItemViewModel(entity, fullPath, vm.RemoveAttachment)
                {
                    IsNew = att.IsNew
                };
                vm.Attachments.Add(item);
            }
        }

        lock (_editorsLock)
        {
            _activeEditors.Add(vm);
        }

        vm.NoteSaved += () => _syncScheduler?.EnqueueLocalChange();
        vm.Commit = () => SaveNewNoteFromEditor(vm);
        if (RequestOpenNoteEditor != null)
        {
            var result = RequestOpenNoteEditor(vm);
            if ((result == true && !string.IsNullOrWhiteSpace(vm.Text)) || vm.HasRestoredVersion)
            {
                RefreshNotes();
            }
        }
        else
        {
            vm.Dispose();
        }
    }

    public void CreateNoteFromTemplate(NoteTemplate? template)
    {
        if (template == null)
        {
            CreateEmptyNote();
            return;
        }

        string expandedText = _templateExpansionService.Expand(template.Text, source: "QuickNotes");
        var validTags = new List<Tag>();

        if (template.TemplateTags != null && template.TemplateTags.Count > 0)
        {
            foreach (var tt in template.TemplateTags)
            {
                var tag = _allTagsCache.FirstOrDefault(t => t.Id == tt.TagId);
                if (tag != null)
                {
                    validTags.Add(tag);
                }
                else
                {
                    ErrorLogService.Write("NoteTemplate", $"Тег #{tt.TagId} для шаблона «{template.Title}» не найден или удалён, пропущен.");
                }
            }
        }

        var vm = new NoteEditorViewModel(
            _tagDetectionService,
            _allTagsCache,
            existingNote: null,
            initialText: expandedText,
            rules: _tagRuleService.GetAllRules(),
            noteHistoryService: _noteHistoryService,
            contextFactory: _contextFactory,
            noteLinkService: _noteLinkService,
            attachmentStorageService: _attachmentStorageService,
            settingsService: _settingsService,
            noteProtectionService: _noteProtectionService,
            initialManualTags: validTags,
            draftJournalService: _draftJournalService);

        lock (_editorsLock)
        {
            _activeEditors.Add(vm);
        }

        vm.NoteSaved += () => _syncScheduler?.EnqueueLocalChange();
        vm.Commit = () => SaveNewNoteFromEditor(vm);
        if (RequestOpenNoteEditor != null)
        {
            var result = RequestOpenNoteEditor(vm);
            if ((result == true && !string.IsNullOrWhiteSpace(vm.Text)) || vm.HasRestoredVersion)
            {
                RefreshNotes();
            }
        }
        else
        {
            vm.Dispose();
        }
    }

    public void CreateNewNote()
    {
        if (Templates.Count == 0)
        {
            CreateEmptyNote();
            return;
        }

        if (RequestTemplateSelection != null)
        {
            var selected = RequestTemplateSelection(Templates);
            if (selected != null)
            {
                CreateNoteFromTemplate(selected);
            }
            return;
        }

        CreateEmptyNote();
    }

    public Func<System.Windows.Window?>? WindowOwnerProvider { get; set; } = QuickNotes.App.Helpers.WindowOwnerResolver.GetActiveWindowOwner;

    public void OpenTemplateManagement()
    {
        var vm = new TemplateManagementViewModel(_noteTemplateService, () => _allTagsCache);
        if (RequestOpenTemplateManagement != null)
        {
            RequestOpenTemplateManagement(vm);
        }
        else
        {
            var dialog = new Views.TemplateManagementDialog(vm);
            var owner = WindowOwnerProvider?.Invoke();
            if (owner != null && owner.IsVisible)
            {
                dialog.Owner = owner;
            }
            else if (System.Windows.Application.Current?.MainWindow != null && System.Windows.Application.Current.MainWindow.IsVisible)
            {
                dialog.Owner = System.Windows.Application.Current.MainWindow;
            }
            dialog.ShowDialog();
        }
        ReloadTemplates();
    }

    public void ReloadTemplates()
    {
        Templates.Clear();
        var list = _noteTemplateService.GetAllTemplates();
        foreach (var item in list)
        {
            Templates.Add(item);
        }
        OnPropertyChanged(nameof(HasTemplates));
    }

    private void ShowAlert(string message, string title, MessageBoxImage image)
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

    private MessageBoxResult ShowConfirm(string message, string title, MessageBoxButton button, MessageBoxImage image)
    {
        if (ConfirmHandler != null)
        {
            return ConfirmHandler(message, title, button, image);
        }

        return MessageBox.Show(message, title, button, image);
    }

    public async Task StartScreenOcrAsync()
    {
        if (IsScreenOcrInProgress) return;
        IsScreenOcrInProgress = true;
        _ocrCts?.Cancel();
        _ocrCts?.Dispose();
        _ocrCts = new CancellationTokenSource();
        var ocrToken = _ocrCts.Token;
        try
        {
            StatusText = "Захват области экрана...";
            var result = await _screenOcrCoordinator.ExecuteOcrWorkflowAsync(msg =>
            {
                StatusText = msg;
            }, ocrToken, OcrRecognizeTimeout);

            if (result.IsCancelled)
            {
                StatusText = "Захват экрана отменён";
                BringMainWindowToFrontIfItWasVisible();
                return;
            }

            if (result.IsTimeout)
            {
                StatusText = UserFacingOperationError.Timeout;
                BringMainWindowToFrontIfItWasVisible();
                ShowAlert(
                    UserFacingOperationError.Timeout,
                    "Снимок экрана и OCR",
                    MessageBoxImage.Warning);
                return;
            }

            if (!result.Success)
            {
                BringMainWindowToFrontIfItWasVisible();

                if (result.IsEmpty)
                {
                    StatusText = "Текст на снимке не обнаружен";
                    ShowAlert(
                        "На выделенной области экрана текст не обнаружен.\nПопробуйте выделить область крупнее или чётче.",
                        "Снимок экрана и OCR",
                        MessageBoxImage.Information);
                    return;
                }

                StatusText = result.ErrorMessage ?? "Ошибка распознавания текста";
                ShowAlert(
                    result.ErrorMessage ?? "Не удалось распознать текст со снимка экрана.",
                    "Снимок экрана и OCR",
                    MessageBoxImage.Warning);
                return;
            }

            if (string.IsNullOrWhiteSpace(result.Text))
            {
                BringMainWindowToFrontIfItWasVisible();

                StatusText = "Текст на снимке не обнаружен";
                ShowAlert(
                    "На выделенной области экрана текст не обнаружен.",
                    "Снимок экрана и OCR",
                    MessageBoxImage.Information);
                return;
            }

            StatusText = $"Текст распознан ({result.Text.Length} симв.)";
            OpenOcrDraftInEditor(result.Text, result.ImageBytes);
        }
        catch (Exception ex)
        {
            ErrorLogService.Write("MainViewModel.ScreenOcr", ex);
            StatusText = UserFacingOperationError.GenericFailure;
            BringMainWindowToFrontIfItWasVisible();
            ShowAlert(
                UserFacingOperationError.GenericFailure,
                "Ошибка",
                MessageBoxImage.Error);
        }
        finally
        {
            IsScreenOcrInProgress = false;
            _ocrCts?.Dispose();
            _ocrCts = null;
        }
    }

    private void BringMainWindowToFrontIfItWasVisible()
    {
        if (_screenOcrCoordinator.LastWasMainWindowVisible)
        {
            RequestBringToFront?.Invoke();
        }
    }

    private void OpenOcrDraftInEditor(string ocrText, byte[]? screenshotBytes = null)
    {
        var rules = _tagRuleService.GetAllRules();
        var detectedMatches = _tagDetectionService.DetectDetailedTags(ocrText, _allTagsCache, rules);

        var vm = new NoteEditorViewModel(
            _tagDetectionService,
            _allTagsCache,
            existingNote: null,
            initialTitle: Helpers.NoteTitleHelper.DeriveTitleFromText(ocrText),
            initialText: ocrText,
            initialAutoTags: detectedMatches.Select(m => m.Tag).ToList(),
            sourceProcessName: "Снимок экрана",
            sourceWindowTitle: "Распознавание текста (OCR)",
            capturedAt: DateTime.Now,
            initialAutoMatches: detectedMatches,
            rules: rules,
            noteHistoryService: _noteHistoryService,
            contextFactory: _contextFactory,
            noteLinkService: _noteLinkService,
            attachmentStorageService: _attachmentStorageService,
            settingsService: _settingsService,
            noteProtectionService: _noteProtectionService,
            draftJournalService: _draftJournalService);

        if (screenshotBytes != null && screenshotBytes.Length > 0)
        {
            var attach = ShowConfirm(
                "Сохранить исходный снимок вместе с распознанным текстом?",
                "Снимок экрана и OCR",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (attach == MessageBoxResult.Yes)
            {
                vm.AddAttachmentFromBytes(screenshotBytes, $"ocr-{DateTime.Now:yyyyMMdd-HHmmss}.png", out _);
            }
        }

        lock (_editorsLock)
        {
            _activeEditors.Add(vm);
        }

        vm.NoteSaved += () => _syncScheduler?.EnqueueLocalChange();
        vm.Commit = () => SaveNewNoteFromEditor(vm);
        if (RequestOpenNoteEditor != null)
        {
            var result = RequestOpenNoteEditor(vm);
            if ((result == true && !string.IsNullOrWhiteSpace(vm.Text)) || vm.HasRestoredVersion)
            {
                RefreshNotes();
                StatusText = "Заметка сохранена";
            }
            else
            {
                StatusText = "Создание заметки отменено (черновик не сохранён)";
            }
        }
        else
        {
            vm.Dispose();
        }
    }

    private void SaveNewNoteFromEditor(NoteEditorViewModel vm)
    {
        Note note = _mutationCoordinator.ExecuteNoteMutation(null, () =>
        {
            using var db = _contextFactory();
            using var tx = db.Database.BeginTransaction();
            try
            {
                var n = new Note
                {
                    Title = vm.Title ?? string.Empty,
                    Text = vm.Text,
                    CreatedAt = DateTime.Now,
                    UpdatedAt = DateTime.Now,
                    CapturedAt = vm.CapturedAt,
                    SourceProcessName = vm.SourceProcessName,
                    SourceWindowTitle = vm.SourceWindowTitle,
                    SourceUrl = vm.SourceUrl,
                    IsPinned = vm.IsPinned,
                    IsFavorite = vm.IsFavorite,
                    IsInbox = vm.IsInbox,
                    DeletedAt = vm.DeletedAt
                };

                vm.ApplyToNote(n);

                db.Notes.Add(n);
                db.SaveChanges();

                _noteHistoryService.SaveSnapshot(db, n);
                tx.Commit();
                _draftJournalService.DeleteJournalIfContentMatches(vm.DraftId, n.Title, n.Text);
                return n;
            }
            catch
            {
                tx.Rollback();
                throw;
            }
        });

        _syncScheduler?.EnqueueLocalChange();

        if (_settingsService.CurrentSettings.ShowNotificationOnSave)
        {
            string preview = note.Text.Length > 50 ? note.Text[..50] + "..." : note.Text;
            _trayIconService.ShowNotification("Заметка сохранена", preview);
        }
    }

    public void EditNote(NoteCardViewModel? card)
    {
        if (card == null) return;

        using var db = _contextFactory();
        var note = db.Notes
            .Include(n => n.NoteTags)
            .ThenInclude(nt => nt.Tag)
            .Include(n => n.Attachments)
            .FirstOrDefault(n => n.Id == card.Id);

        if (note == null) return;

        // Protected note: require unlock before opening the editor.
        if (note.IsProtected && !_noteProtectionService.IsUnlocked(note.Id))
        {
            if (!TryUnlockProtectedNote(note.Id))
            {
                return; // Cancel or wrong password: stay on the list
            }
        }

        var vm = new NoteEditorViewModel(
            _tagDetectionService,
            _allTagsCache,
            existingNote: note,
            rules: _tagRuleService.GetAllRules(),
            noteHistoryService: _noteHistoryService,
            contextFactory: _contextFactory,
            noteLinkService: _noteLinkService,
            attachmentStorageService: _attachmentStorageService,
            settingsService: _settingsService,
            noteProtectionService: _noteProtectionService,
            draftJournalService: _draftJournalService);

        var searchFreeText = SearchQueryParser.Parse(SearchQuery, _allTagsCache).FreeText;
        if (!string.IsNullOrWhiteSpace(searchFreeText))
        {
            vm.SetInitialHighlight(searchFreeText);
        }

        lock (_editorsLock)
        {
            _activeEditors.Add(vm);
        }

        vm.NoteSaved += () => _syncScheduler?.EnqueueLocalChange();

        vm.RequestPasswordDialog = (title, message) => ShowPasswordDialog(title, message, isSetMode: false);
        vm.RequestSetPasswordDialog = (title, message) => ShowPasswordDialog(title, message, isSetMode: true);
        vm.RequestLockNote = () =>
        {
            // Lock the note in the session store; the editor closes and returns to the list.
            _noteProtectionService.LockNote(note.Id);
            vm.MarkLocked();
            vm.RequestCloseEditor();
        };
        vm.RequestProtectionChanged = () => RefreshNotes();

        vm.Commit = () =>
        {
            var candidateFilesToCleanup = new List<string>();
            Models.Note? current = null;

            _mutationCoordinator.ExecuteNoteMutation(card.Id, () =>
            {
                using var writeDb = _contextFactory();
                using var tx = writeDb.Database.BeginTransaction();
                var createdProtectedFiles = new List<string>();
                try
                {
                    current = writeDb.Notes
                        .Include(n => n.NoteTags)
                        .Include(n => n.Attachments)
                        .Single(n => n.Id == card.Id);

                    if (vm.IsPermanentlyDeleted)
                    {
                        candidateFilesToCleanup.AddRange(current.Attachments.Select(a => a.StoredFileName).Distinct());
                        writeDb.Notes.Remove(current);
                    }
                    else
                    {
                        candidateFilesToCleanup.AddRange(vm.DeletedAttachments.Select(a => a.StoredFileName).Distinct());

                        if (current.IsProtected && vm.IsNoteUnlocked)
                        {
                            // Common metadata (pin, favorite, inbox, deleted/updated dates, tags, and attachment detachment)
                            vm.ApplyMetadataAndLinks(current);

                            // Plaintext columns are never populated for protected notes
                            current.Title = string.Empty;
                            current.Text = string.Empty;
                            current.SourceProcessName = null;
                            current.SourceWindowTitle = null;
                            current.SourceUrl = null;

                            // Protected envelope & encrypted revision snapshot
                            _noteProtectionService.ApplyUnlockedEdits(
                                writeDb, current, vm.Title, vm.Text, vm.SourceProcessName, vm.SourceWindowTitle, vm.SourceUrl);
                            _noteProtectionService.SaveProtectedRevision(writeDb, current, vm.Title, vm.Text);

                            // Encrypt any newly added attachments before commit
                            foreach (var item in vm.Attachments.Where(a => a.Id == 0))
                            {
                                var entity = item.ToEntity();
                                entity.NoteId = current.Id;
                                _noteProtectionService.EncryptAttachment(
                                    writeDb, current.Id, entity, item.OriginalFileName, item.FullPath,
                                    createdProtectedFiles, candidateFilesToCleanup);
                                current.Attachments.Add(entity);
                            }
                        }
                        else
                        {
                            vm.ApplyToNote(current);
                            _noteHistoryService.SaveSnapshot(writeDb, current);
                        }
                    }
                    writeDb.SaveChanges();
                    tx.Commit();
                    _draftJournalService.DeleteJournalIfContentMatches(vm.DraftId, vm.Title, vm.Text);
                }
                catch (Exception ex)
                {
                    ErrorLogService.Write("MainViewModel.SaveNote", ex);
                    try
                    {
                        tx.Rollback();
                    }
                    catch (Exception rbEx)
                    {
                        ErrorLogService.Write("MainViewModel.SaveNote.Rollback", rbEx);
                    }

                    foreach (var f in createdProtectedFiles)
                    {
                        try
                        {
                            _noteProtectionService.FileAdapter.Delete(f);
                        }
                        catch (Exception delEx)
                        {
                            ErrorLogService.Write("MainViewModel.SaveNote.CleanupCreatedProtectedFile", delEx);
                        }
                    }
                    throw;
                }
            });

            if (candidateFilesToCleanup.Count > 0)
            {
                try
                {
                    using var cleanupDb = _contextFactory();
                    _attachmentStorageService.CleanupUnreferencedFiles(cleanupDb, candidateFilesToCleanup);
                }
                catch (Exception cleanupEx)
                {
                    ErrorLogService.Write("MainViewModel.SaveNote.PostCommitCleanupUnreferencedFiles", cleanupEx);
                }
            }

            _syncScheduler?.EnqueueLocalChange();

            if (!vm.IsPermanentlyDeleted && _settingsService.CurrentSettings.ShowNotificationOnSave)
            {
                _trayIconService.ShowNotification("Заметка сохранена", $"Заметка #{card.Id} обновлена");
            }
        };

        if (RequestOpenNoteEditor != null)
        {
            if (RequestOpenNoteEditor.Invoke(vm) == true || vm.HasRestoredVersion)
            {
                int closedId = card.Id;
                bool continueInbox = vm.ContinueInboxAfterClose;
                RefreshNotes();
                if (continueInbox)
                {
                    OpenNextInboxNote(closedId);
                }
            }
        }
        else
        {
            vm.Dispose();
        }
    }

    public void SetOcrHotkeySuspended(bool suspended) => _hotkeyService.SetOcrSuspended(suspended);

    public bool LoadNoteIntoDetail(NoteCardViewModel? card)
    {
        if (!CanReplaceDetailEditor(card))
        {
            return false;
        }

        ReplaceDetailEditor(card);
        return true;
    }

    private bool CanReplaceDetailEditor(NoteCardViewModel? card)
    {
        if (_detailEditor == null)
        {
            return true;
        }

        if (card != null && _detailEditor.NoteId == card.Id)
        {
            return true;
        }

        return TryLeaveDetailEditor();
    }

    public bool TryLeaveDetailEditor()
    {
        if (_detailEditor == null)
        {
            return true;
        }

        if (!_detailEditor.HasUnsavedChanges)
        {
            DisposeDetailEditor();
            return true;
        }

        var decision = _detailEditor.RequestUnsavedEditorDecision?.Invoke()
                       ?? RequestUnsavedEditorDecision?.Invoke()
                       ?? UnsavedEditorPrompt.Show(null);
        if (decision == UnsavedEditorDecision.Stay)
        {
            return false;
        }

        if (decision == UnsavedEditorDecision.Save)
        {
            try
            {
                if (!_detailEditor.TryCommitChanges(closeAfterSuccess: false))
                {
                    return false;
                }
            }
            catch (Exception ex)
            {
                ErrorLogService.Write("MainViewModel.TryLeaveDetailEditor", ex);
                ShowAlert(UnsavedEditorPromptText.SaveFailure, "Ошибка", MessageBoxImage.Error);
                return false;
            }

            DisposeDetailEditor();
            return true;
        }

        if (!_detailEditor.ApplyDiscardUnsaved(deleteJournal: true))
        {
            return false;
        }
        DisposeDetailEditor();
        return true;
    }

    private void DisposeDetailEditor()
    {
        if (_detailEditor == null)
        {
            return;
        }

        _detailEditor.Dispose();
        DetailEditor = null;
    }

    private void ReplaceDetailEditor(NoteCardViewModel? card)
    {
        if (card == null)
        {
            DisposeDetailEditor();
            return;
        }

        if (_detailEditor != null && _detailEditor.NoteId == card.Id)
        {
            return;
        }

        DisposeDetailEditor();

        using var db = _contextFactory();
        var note = db.Notes
            .Include(n => n.NoteTags)
                .ThenInclude(nt => nt.Tag)
            .Include(n => n.Attachments)
            .FirstOrDefault(n => n.Id == card.Id);

        if (note == null)
        {
            DetailEditor = null;
            return;
        }

        var vm = new NoteEditorViewModel(
            _tagDetectionService,
            _allTagsCache,
            existingNote: note,
            rules: _tagRuleService.GetAllRules(),
            noteHistoryService: _noteHistoryService,
            contextFactory: _contextFactory,
            noteLinkService: _noteLinkService,
            attachmentStorageService: _attachmentStorageService,
            settingsService: _settingsService,
            noteProtectionService: _noteProtectionService,
            draftJournalService: _draftJournalService,
            mutationCoordinator: _mutationCoordinator);

        vm.IsInline = true;

        var searchFreeText = SearchQueryParser.Parse(SearchQuery, _allTagsCache).FreeText;
        if (!string.IsNullOrWhiteSpace(searchFreeText))
        {
            vm.SetInitialHighlight(searchFreeText);
        }

        vm.NoteSaved += () =>
        {
            _syncScheduler?.EnqueueLocalChange();
            UpdateCardFromEditor(card.Id, vm);
        };

        vm.RequestPasswordDialog = (title, message) => ShowPasswordDialog(title, message, isSetMode: false);
        vm.RequestSetPasswordDialog = (title, message) => ShowPasswordDialog(title, message, isSetMode: true);
        vm.RequestLockNote = () =>
        {
            _noteProtectionService.LockNote(note.Id);
            vm.MarkLocked();
        };
        vm.RequestProtectionChanged = () => RefreshNotes();

        vm.Commit = () => CommitDetailEditor(vm);
        vm.AlertHandler = ShowAlert;
        vm.RequestUnsavedEditorDecision = () =>
            RequestUnsavedEditorDecision?.Invoke() ?? UnsavedEditorPrompt.Show(null);
        vm.RequestDraftDiscardDecision = () =>
            RequestDraftDiscardDecision?.Invoke() ?? DraftDiscardPrompt.Show(null);
        vm.SetOcrHotkeySuspended = SetOcrHotkeySuspended;

        vm.CheckAndPromptDraftRecovery(forcePrompt: false);

        DetailEditor = vm;
    }

    private void CommitDetailEditor(NoteEditorViewModel vm)
    {
        if (!vm.NoteId.HasValue) return;
        int noteId = vm.NoteId.Value;

        var candidateFilesToCleanup = new List<string>();
        Models.Note? current = null;

        _mutationCoordinator.ExecuteNoteMutation(noteId, () =>
        {
            using var writeDb = _contextFactory();
            using var tx = writeDb.Database.BeginTransaction();
            var createdProtectedFiles = new List<string>();
            try
            {
                current = writeDb.Notes
                    .Include(n => n.NoteTags)
                    .Include(n => n.Attachments)
                    .Single(n => n.Id == noteId);

                if (vm.IsPermanentlyDeleted)
                {
                    candidateFilesToCleanup.AddRange(current.Attachments.Select(a => a.StoredFileName).Distinct());
                    writeDb.Notes.Remove(current);
                }
                else
                {
                    candidateFilesToCleanup.AddRange(vm.DeletedAttachments.Select(a => a.StoredFileName).Distinct());

                    if (current.IsProtected && vm.IsNoteUnlocked)
                    {
                        vm.ApplyMetadataAndLinks(current);
                        current.Title = string.Empty;
                        current.Text = string.Empty;
                        current.SourceProcessName = null;
                        current.SourceWindowTitle = null;
                        current.SourceUrl = null;

                        _noteProtectionService.ApplyUnlockedEdits(
                            writeDb, current, vm.Title, vm.Text, vm.SourceProcessName, vm.SourceWindowTitle, vm.SourceUrl);
                        _noteProtectionService.SaveProtectedRevision(writeDb, current, vm.Title, vm.Text);

                        foreach (var item in vm.Attachments.Where(a => a.Id == 0))
                        {
                            var entity = item.ToEntity();
                            entity.NoteId = current.Id;
                            _noteProtectionService.EncryptAttachment(
                                writeDb, current.Id, entity, item.OriginalFileName, item.FullPath,
                                createdProtectedFiles, candidateFilesToCleanup);
                            current.Attachments.Add(entity);
                        }
                    }
                    else
                    {
                        vm.ApplyToNote(current);
                        _noteHistoryService.SaveSnapshot(writeDb, current);
                    }
                }
                writeDb.SaveChanges();
                tx.Commit();
                _draftJournalService.DeleteJournalIfContentMatches(vm.DraftId, vm.Title, vm.Text);
            }
            catch (Exception ex)
            {
                ErrorLogService.Write("MainViewModel.CommitDetailEditor", ex);
                try { tx.Rollback(); } catch (Exception rbEx) { ErrorLogService.Write("MainViewModel.CommitDetailEditor.Rollback", rbEx); }

                foreach (var f in createdProtectedFiles)
                {
                    try { _noteProtectionService.FileAdapter.Delete(f); } catch (Exception delEx) { ErrorLogService.Write("MainViewModel.CommitDetailEditor.CleanupCreatedProtectedFile", delEx); }
                }
                throw;
            }
        });

        if (vm.IsPermanentlyDeleted)
        {
            RefreshNotes();
        }

        if (candidateFilesToCleanup.Count > 0)
        {
            try
            {
                using var cleanupDb = _contextFactory();
                _attachmentStorageService.CleanupUnreferencedFiles(cleanupDb, candidateFilesToCleanup);
            }
            catch (Exception cleanupEx)
            {
                ErrorLogService.Write("MainViewModel.CommitDetailEditor.PostCommitCleanupUnreferencedFiles", cleanupEx);
            }
        }

        if (!vm.IsPermanentlyDeleted && _settingsService.CurrentSettings.ShowNotificationOnSave)
        {
            _trayIconService.ShowNotification("Заметка сохранена", $"Заметка #{noteId} обновлена");
        }
    }

    private void UpdateCardFromEditor(int noteId, NoteEditorViewModel vm)
    {
        var card = Notes.FirstOrDefault(n => n.Id == noteId);
        if (card != null)
        {
            card.Note.Title = vm.Title ?? string.Empty;
            card.Note.Text = vm.Text;
            card.Note.IsPinned = vm.IsPinned;
            card.Note.IsFavorite = vm.IsFavorite;
            card.Note.IsInbox = vm.IsInbox;
            card.Note.UpdatedAt = DateTime.Now;
            card.RefreshState();
        }
    }

    /// <summary>
    /// Prompts for a protected note password. Returns true when the note is unlocked.
    /// The password is passed directly to the protection service and never stored.
    /// </summary>
    private bool TryUnlockProtectedNote(int noteId)
    {
        try
        {
            // Request a password from the caller (MainWindow shows the dialog).
            string? password = RequestProtectedNotePassword?.Invoke(noteId);
            if (string.IsNullOrEmpty(password))
            {
                return false;
            }

            using var db = _contextFactory();
            _noteProtectionService.UnlockNote(db, noteId, password);
            return true;
        }
        catch (Services.NoteProtection.NoteProtectionSecurityException ex)
        {
            // Neutral message; no data was modified.
            ShowAlert(ex.Message, "Защищённая заметка", System.Windows.MessageBoxImage.Warning);
            return false;
        }
        catch (Exception ex)
        {
            ErrorLogService.Write("MainViewModel.UnlockProtectedNote", ex);
            ShowAlert("Не удалось разблокировать заметку.", "Ошибка", System.Windows.MessageBoxImage.Error);
            return false;
        }
    }

    private string? ShowPasswordDialog(string title, string message, bool isSetMode)
    {
        return RequestProtectedPasswordDialog?.Invoke(title, message, isSetMode);
    }

    public Func<int, string?>? RequestProtectedNotePassword { get; set; }
    public Func<string, string, bool, string?>? RequestProtectedPasswordDialog { get; set; }

    private void OpenNextInboxNote(int closedNoteId)
    {
        var next = Notes.FirstOrDefault(n => n.IsInbox && n.Id != closedNoteId)
                   ?? Notes.FirstOrDefault(n => n.IsInbox);
        if (next != null)
        {
            SelectedNote = next;
            RequestFocusNotesList?.Invoke();
            EditNote(next);
        }
    }

    public void TogglePinNote(int? noteId)
    {
        if (!noteId.HasValue) return;

        _mutationCoordinator.ExecuteNoteMutation(noteId.Value, () =>
        {
            using var db = _contextFactory();
            var note = db.Notes.Find(noteId.Value);
            if (note != null)
            {
                note.IsPinned = !note.IsPinned;
                db.SaveChanges();
            }
        });

        _syncScheduler?.EnqueueLocalChange();
        RefreshNotes();
    }

    public void ToggleFavoriteNote(int? noteId)
    {
        if (!noteId.HasValue) return;

        _mutationCoordinator.ExecuteNoteMutation(noteId.Value, () =>
        {
            using var db = _contextFactory();
            var note = db.Notes.Find(noteId.Value);
            if (note != null)
            {
                note.IsFavorite = !note.IsFavorite;
                db.SaveChanges();
            }
        });

        _syncScheduler?.EnqueueLocalChange();
        RefreshNotes();
    }

    public void MarkNoteProcessed(int? noteId)
    {
        if (!noteId.HasValue) return;

        _mutationCoordinator.ExecuteNoteMutation(noteId.Value, () =>
        {
            using var db = _contextFactory();
            var note = db.Notes.Find(noteId.Value);
            if (note != null)
            {
                note.IsInbox = false;
                db.SaveChanges();
            }
        });

        _syncScheduler?.EnqueueLocalChange();
        RefreshNotes();
    }

    public void MarkProcessedAndNext(int? noteId)
    {
        if (!noteId.HasValue) return;

        int? nextId = null;
        var snapshot = Notes.ToList();
        int currentIndex = snapshot.FindIndex(n => n.Id == noteId.Value);
        if (currentIndex >= 0)
        {
            for (int i = currentIndex + 1; i < snapshot.Count; i++)
            {
                if (snapshot[i].IsInbox && snapshot[i].Id != noteId.Value)
                {
                    nextId = snapshot[i].Id;
                    break;
                }
            }

            if (nextId == null)
            {
                nextId = snapshot.FirstOrDefault(n => n.IsInbox && n.Id != noteId.Value)?.Id;
            }
        }

        MarkNoteProcessed(noteId);

        if (nextId.HasValue)
        {
            SelectedNote = Notes.FirstOrDefault(n => n.Id == nextId.Value) ?? SelectedNote;
        }
        else if (Notes.Count > 0)
        {
            int index = Math.Min(Math.Max(currentIndex, 0), Notes.Count - 1);
            SelectedNote = Notes[index];
        }

        RequestFocusNotesList?.Invoke();
    }

    public void ApplySearchExample(string? example)
    {
        if (string.IsNullOrWhiteSpace(example))
        {
            return;
        }

        SearchQuery = example.Trim();
    }

    public void TryShowFirstRun()
    {
        if (_settingsService.CurrentSettings.HasCompletedOnboarding)
        {
            return;
        }

        RequestOpenFirstRun?.Invoke();
    }

    public void CompleteOnboarding(bool createStarterTags)
    {
        if (createStarterTags)
        {
            CreateStarterTagsIfMissing();
        }

        var settings = _settingsService.CurrentSettings;
        settings.HasCompletedOnboarding = true;
        _settingsService.SaveSettings(settings);
        ReloadAll();
    }

    public void CreateStarterTagsIfMissing()
    {
        string[] names = { "Работа", "Личное", "Идеи" };
        using var db = _contextFactory();
        var existing = db.Tags.Select(t => t.Name).ToList();
        var existingSet = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);
        foreach (var name in names)
        {
            if (existingSet.Contains(name))
            {
                continue;
            }

            db.Tags.Add(new Tag { Name = name, SyncId = Guid.NewGuid() });
            existingSet.Add(name);
        }

        db.SaveChanges();
    }

    public void MoveNoteToTrash(int? noteId)
    {
        if (!noteId.HasValue) return;

        var result = MessageBox.Show(
            $"Переместить заметку #{noteId.Value} в корзину?",
            "Подтверждение удаления",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result == MessageBoxResult.Yes)
        {
            bool modified = false;
            _mutationCoordinator.ExecuteNoteMutation(noteId.Value, () =>
            {
                using var db = _contextFactory();
                var note = db.Notes.Find(noteId.Value);
                if (note != null)
                {
                    note.DeletedAt = DateTime.Now;
                    db.SaveChanges();
                    modified = true;
                }
            });

            if (modified)
            {
                _syncScheduler?.EnqueueLocalChange();
                RefreshNotes();
            }
        }
    }

    public void RestoreNote(int? noteId)
    {
        if (!noteId.HasValue) return;

        bool modified = false;
        _mutationCoordinator.ExecuteNoteMutation(noteId.Value, () =>
        {
            using var db = _contextFactory();
            var note = db.Notes.Find(noteId.Value);
            if (note != null)
            {
                note.DeletedAt = null;
                db.SaveChanges();
                modified = true;
            }
        });

        if (modified)
        {
            _syncScheduler?.EnqueueLocalChange();
            RefreshNotes();
        }
    }

    public void PermanentDeleteNote(int? noteId)
    {
        if (!noteId.HasValue) return;

        var result = MessageBox.Show(
            $"Вы действительно хотите окончательно удалить заметку #{noteId.Value}?\nЭто действие нельзя отменить.",
            "Подтверждение безвозвратного удаления",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result == MessageBoxResult.Yes)
        {
            bool deleted = false;
            List<string> candidateFiles = new();
            _mutationCoordinator.ExecuteNoteMutation(noteId.Value, () =>
            {
                using var db = _contextFactory();
                var note = db.Notes.Include(n => n.Attachments).FirstOrDefault(n => n.Id == noteId.Value);
                if (note != null)
                {
                    candidateFiles = note.Attachments.Select(a => a.StoredFileName).Distinct().ToList();
                    db.Notes.Remove(note);
                    db.SaveChanges();
                    if (candidateFiles.Count > 0)
                    {
                        _attachmentStorageService.CleanupUnreferencedFiles(db, candidateFiles);
                    }
                    deleted = true;
                }
            });

            if (deleted)
            {
                _syncScheduler?.EnqueueLocalChange();
                RefreshNotes();
            }
        }
    }

    public void DeleteNote(NoteCardViewModel? card)
    {
        if (card == null) return;

        if (card.IsDeleted || IsTrashSelected)
        {
            PermanentDeleteNote(card.Id);
        }
        else
        {
            MoveNoteToTrash(card.Id);
        }
    }

    public void EmptyTrash()
    {
        using var db = _contextFactory();
        var trashNotes = db.Notes.Include(n => n.Attachments).Where(n => n.DeletedAt != null).ToList();
        if (trashNotes.Count == 0)
        {
            MessageBox.Show("Корзина уже пуста.", "Очистка корзины", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var result = MessageBox.Show(
            $"Очистить корзину?\nБудет безвозвратно удалено заметок: {trashNotes.Count}.",
            "Очистка корзины",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result == MessageBoxResult.Yes)
        {
            List<int> deletedIds = new();
            _mutationCoordinator.ExecuteBulkMutation(() =>
            {
                using var bulkDb = _contextFactory();
                var notesToDelete = bulkDb.Notes.Include(n => n.Attachments).Where(n => n.DeletedAt != null).ToList();
                if (notesToDelete.Count > 0)
                {
                    var candidateFiles = notesToDelete.SelectMany(n => n.Attachments).Select(a => a.StoredFileName).Distinct().ToList();
                    deletedIds = notesToDelete.Select(n => n.Id).ToList();
                    bulkDb.Notes.RemoveRange(notesToDelete);
                    bulkDb.SaveChanges();
                    if (candidateFiles.Count > 0)
                    {
                        _attachmentStorageService.CleanupUnreferencedFiles(bulkDb, candidateFiles);
                    }
                }
            });

            if (deletedIds.Count > 0)
            {
                _syncScheduler?.EnqueueLocalChange();
                RefreshNotes();
            }
        }
    }

    public void RescanNote(NoteCardViewModel? card)
    {
        if (card == null) return;

        using var db = _contextFactory();
        var note = db.Notes
            .Include(n => n.NoteTags)
            .ThenInclude(nt => nt.Tag)
            .FirstOrDefault(n => n.Id == card.Id);

        if (note == null) return;

        var rules = _tagRuleService.GetAllRules();
        using var tx = db.Database.BeginTransaction();
        try
        {
            _tagDetectionService.RescanNote(note, _allTagsCache, rules);
            // Persist the note's updated auto-tag set and its history snapshot together.
            _noteHistoryService.SaveSnapshot(db, note);
            db.SaveChanges();
            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }

        _syncScheduler?.EnqueueLocalChange();
        RefreshNotes();
    }

    public void RescanAllNotes()
    {
        using var db = _contextFactory();

        // Ensure fresh tags cache with synonyms
        _allTagsCache = db.Tags
            .Include(t => t.Synonyms)
            .ToList();

        var allNotes = db.Notes
            .Where(n => n.DeletedAt == null)
            .Include(n => n.NoteTags)
            .ThenInclude(nt => nt.Tag)
            .ToList();

        var rules = _tagRuleService.GetAllRules();
        var preview = _tagRescanPreviewService.CalculatePreview(allNotes, _allTagsCache, rules);
        var previewVm = new TagRescanPreviewViewModel(preview);

        var dialogResult = RequestOpenRescanPreview?.Invoke(previewVm);
        if (dialogResult != true || !previewVm.CanConfirm)
        {
            return;
        }

        using var tx = db.Database.BeginTransaction();
        try
        {
            foreach (var note in allNotes)
            {
                _tagDetectionService.RescanNote(note, _allTagsCache, rules);
                _noteHistoryService.SaveSnapshot(db, note);
            }

            db.SaveChanges();
            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }

        _syncScheduler?.EnqueueLocalChange();
        ReloadTags();
        RefreshNotes();

        string summary = $"Пересканирование завершено. Изменено заметок: {preview.TotalNotesWithChanges}.\nДобавлено автотегов: {preview.TotalAddedTags}, удалено: {preview.TotalRemovedTags}.";
        ShowNotification(summary, "Массовое пересканирование");
    }

    private void ShowNotification(string message, string title)
    {
        if (NotificationHandler != null)
        {
            NotificationHandler(message, title);
            return;
        }

        MessageBox.Show(
            message,
            title,
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    public string HotkeyHint
    {
        get
        {
            var s = _settingsService.CurrentSettings;
            string h1 = string.Join("+", new[] {
                s.HotkeyCtrl ? "Ctrl" : null, s.HotkeyShift ? "Shift" : null,
                s.HotkeyAlt ? "Alt" : null, s.HotkeyWin ? "Win" : null, s.HotkeyKey }.Where(k => k != null));
            string h2 = string.Join("+", new[] {
                s.InstantHotkeyCtrl ? "Ctrl" : null, s.InstantHotkeyShift ? "Shift" : null,
                s.InstantHotkeyAlt ? "Alt" : null, s.InstantHotkeyWin ? "Win" : null, s.InstantHotkeyKey }.Where(k => k != null));
            return $"Редактор: {h1} | Мгновенно: {h2}";
        }
    }

    public SettingsViewModel CreateSettingsViewModel()
    {
        var vm = new SettingsViewModel(
            _settingsService,
            _hotkeyService,
            _backupService,
            _credentialsStorage,
            _passwordStorage,
            syncScheduler: _syncScheduler,
            cloudUsageService: GetOrCreateCloudUsageService(),
            cloudCleanupService: GetOrCreateCleanupService(),
            passwordRotationService: GetOrCreatePasswordRotationService(),
            cloudRetentionService: GetOrCreateRetentionService(),
            taskReminderScheduler: _taskReminderScheduler,
            transportFactory: _syncCloud.TransportFactory);
        vm.OpenTemplateManagementAction = OpenTemplateManagement;
        return vm;
    }

    public void OpenSettings()
    {
        var vm = CreateSettingsViewModel();
        RequestOpenSettings?.Invoke(vm);
        OnPropertyChanged(nameof(HotkeyHint));
        PurgeExpiredTrash();
        RefreshNotes();
        ResetProductionSyncEngine();
        _syncScheduler?.NotifySettingsChanged();
        _taskReminderScheduler.NotifySettingsChanged();
        AsyncEventBridge.Fire(() => RefreshSyncStateAsync(), "MainViewModel.RefreshSyncState");
    }

    public void OpenNoteAssembly()
    {
        int? seedId = SelectedNote is { IsDeleted: false } card ? card.Id : null;
        var vm = new NoteAssemblyViewModel(
            _contextFactory,
            _noteAssemblyService,
            _noteHistoryService,
            seedId.HasValue ? new[] { seedId.Value } : null,
            _mutationCoordinator);

        RequestOpenNoteAssembly?.Invoke(vm);
        if (!vm.HasCompletedAssembly || vm.CreatedNoteId <= 0)
        {
            return;
        }

        _syncScheduler?.EnqueueLocalChange();
        ReloadAll();
        StatusText = vm.SuccessSummary;
    }

    public void OpenImportExport()
    {
        var vm = new ImportExportViewModel(
            _contextFactory,
            _mutationCoordinator,
            _noteExportService,
            _noteImportService,
            _noteHistoryService,
            _tagDetectionService,
            archiveService: null,
            attachmentStorage: _attachmentStorageService,
            settingsService: _settingsService);

        RequestOpenImportExport?.Invoke(vm);
        if (vm.ImportedNotesCount > 0)
        {
            _syncScheduler?.EnqueueLocalChange();
            ReloadAll();
            StatusText = $"Успешно импортировано {vm.ImportedNotesCount} заметок.";
        }
    }

    public void NotifyCloudSettingsChanged()
    {
        ResetProductionSyncEngine();
        _syncScheduler?.NotifySettingsChanged();
        AsyncEventBridge.Fire(() => RefreshSyncStateAsync(), "MainViewModel.RefreshSyncState");
    }

    // Tag Operations
    public void SuggestTags()
    {
        using var db = _contextFactory();
        var allTags = db.Tags.Include(t => t.Synonyms).ToList();
        var notes = db.Notes
            .Where(n => n.DeletedAt == null)
            .AsNoTracking()
            .ToList();

        var suggestions = _tagSuggestionService.SuggestTags(notes, allTags);
        var vm = new TagSuggestionsViewModel(suggestions);

        var dialogResult = RequestOpenTagSuggestions?.Invoke(vm);
        if (dialogResult == true && vm.SelectedSuggestion != null)
        {
            CreateTagFromSuggestion(vm.SelectedSuggestion.Word);
        }
    }

    public bool CreateTagFromSuggestion(string tagName)
    {
        string normalized = tagName.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        using var db = _contextFactory();
        // Check for duplicates / race conditions
        bool alreadyExists = db.Tags
                                 .AsNoTracking()
                                 .Select(t => t.Name)
                                 .AsEnumerable()
                                 .Any(name => string.Equals(
                                     TagDetectionService.NormalizeTerm(name),
                                     TagDetectionService.NormalizeTerm(normalized),
                                     StringComparison.OrdinalIgnoreCase)) ||
                             db.TagSynonyms
                                 .AsNoTracking()
                                 .Select(s => s.Value)
                                 .AsEnumerable()
                                 .Any(value => string.Equals(
                                     TagDetectionService.NormalizeTerm(value),
                                     TagDetectionService.NormalizeTerm(normalized),
                                     StringComparison.OrdinalIgnoreCase));
        if (alreadyExists)
        {
            ShowNotification($"Тег или синоним «{normalized}» уже существует.", "Предложение тегов");
            return false;
        }

        var tag = new Tag
        {
            Name = normalized,
            ParentTagId = null
        };
        db.Tags.Add(tag);
        db.SaveChanges();

        _syncScheduler?.EnqueueLocalChange();
        ReloadTags();
        RefreshNotes();
        ShowNotification($"Корневой тег «{normalized}» успешно создан.", "Предложение тегов");
        return true;
    }

    private void AddRootTag()
    {
        var vm = new TagEditViewModel("Новый корневой тег");
        if (RequestOpenTagEditor?.Invoke(vm) == true)
        {
            using var db = _contextFactory();
            var tag = new Tag
            {
                Name = vm.TagName.Trim(),
                ParentTagId = null
            };
            db.Tags.Add(tag);
            db.SaveChanges();

            _syncScheduler?.EnqueueLocalChange();
            ReloadTags();
        }
    }

    private void AddChildTag(TagTreeItemViewModel? parentItem)
    {
        if (parentItem == null) return;

        var vm = new TagEditViewModel($"Новый дочерний тег для «{parentItem.Name}»");
        if (RequestOpenTagEditor?.Invoke(vm) == true)
        {
            using var db = _contextFactory();
            var tag = new Tag
            {
                Name = vm.TagName.Trim(),
                ParentTagId = parentItem.Id
            };
            db.Tags.Add(tag);
            db.SaveChanges();

            _syncScheduler?.EnqueueLocalChange();
            ReloadTags();
        }
    }

    private void RenameTag(TagTreeItemViewModel? item)
    {
        if (item == null) return;

        var vm = new TagEditViewModel("Переименование тега", item.Name);
        if (RequestOpenTagEditor?.Invoke(vm) == true)
        {
            using var db = _contextFactory();
            var tag = db.Tags.Find(item.Id);
            if (tag != null)
            {
                tag.Name = vm.TagName.Trim();
                db.SaveChanges();
                _syncScheduler?.EnqueueLocalChange();
                ReloadTags();
                RefreshNotes();
            }
        }
    }

    public bool CanMoveTag(int sourceTagId, int? targetParentId)
    {
        return TagHierarchyService.CanReparent(sourceTagId, targetParentId, _allTagsCache);
    }

    public bool MoveTag(int sourceTagId, int? targetParentId)
    {
        if (!CanMoveTag(sourceTagId, targetParentId))
            return false;

        using var db = _contextFactory();
        var dbTag = db.Tags.Find(sourceTagId);
        if (dbTag == null)
            return false;

        var allTags = db.Tags.ToList();
        if (!TagHierarchyService.CanReparent(sourceTagId, targetParentId, allTags))
            return false;

        dbTag.ParentTagId = targetParentId;
        db.SaveChanges();

        _syncScheduler?.EnqueueLocalChange();
        ReloadTags();
        RefreshNotes();
        return true;
    }

    private void ChangeParentTag(TagTreeItemViewModel? item)
    {
        if (item == null) return;

        var tag = _allTagsCache.FirstOrDefault(t => t.Id == item.Id);
        if (tag == null) return;

        var vm = new ChangeParentViewModel(tag, _allTagsCache);
        if (RequestOpenChangeParentEditor?.Invoke(vm) == true)
        {
            int? parentId = vm.SelectedParent?.Id is > 0 ? vm.SelectedParent.Id : null;
            if (parentId == tag.ParentTagId)
            {
                return;
            }

            MoveTag(tag.Id, parentId);
        }
    }

    private void ManageSynonyms(TagTreeItemViewModel? item)
    {
        if (item == null) return;

        using var db = _contextFactory();
        var tag = db.Tags
            .Include(t => t.Synonyms)
            .FirstOrDefault(t => t.Id == item.Id);

        if (tag == null) return;

        var vm = new TagSynonymsViewModel(tag);
        if (RequestOpenSynonymsEditor?.Invoke(vm) == true)
        {
            db.SaveChanges();
            _syncScheduler?.EnqueueLocalChange();
            ReloadTags();
        }
    }

    private void ManageRules(TagTreeItemViewModel? item)
    {
        if (item == null) return;

        var tag = _allTagsCache.FirstOrDefault(t => t.Id == item.Id);
        if (tag == null) return;

        var existingRule = _tagRuleService.GetRule(tag.Id);
        var vm = new TagRuleViewModel(tag, existingRule, _tagRuleService);
        if (RequestOpenTagRules?.Invoke(vm) == true)
        {
            UpdateFilterChips();
        }
    }

    private void DeleteTag(TagTreeItemViewModel? item)
    {
        if (item == null) return;

        var result = MessageBox.Show(
            $"Вы уверены, что хотите удалить тег «{item.Name}»?\nВсе дочерние теги также будут удалены.",
            "Подтверждение удаления тега",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result == MessageBoxResult.Yes)
        {
            using var db = _contextFactory();
            var allTags = db.Tags.ToList();
            var toDeleteIds = TagHierarchyService.GetTagAndDescendantIds(item.Id, allTags);

            var tagsToDelete = db.Tags.Where(t => toDeleteIds.Contains(t.Id)).ToList();
            var templateTagsToDelete = db.NoteTemplateTags.Where(tt => toDeleteIds.Contains(tt.TagId)).ToList();
            if (templateTagsToDelete.Count > 0)
            {
                db.NoteTemplateTags.RemoveRange(templateTagsToDelete);
            }
            db.Tags.RemoveRange(tagsToDelete);
            db.SaveChanges();

            foreach (var id in toDeleteIds)
            {
                _tagRuleService.DeleteRule(id);
            }

            if (SelectedTag != null && toDeleteIds.Contains(SelectedTag.Id))
            {
                SelectedTag = null;
            }

            _syncScheduler?.EnqueueLocalChange();
            ReloadTags();
            ReloadTemplates();
            RefreshNotes();
        }
    }

    private void MergeTag(TagTreeItemViewModel? item)
    {
        if (item == null) return;

        var tag = _allTagsCache.FirstOrDefault(t => t.Id == item.Id);
        if (tag == null) return;

        var vm = new TagMergeViewModel(tag, _allTagsCache, _contextFactory, _tagMergeService);
        if (RequestOpenTagMerge?.Invoke(vm) == true && vm.SelectedTargetTag != null)
        {
            ExecuteMergeTag(tag.Id, vm.SelectedTargetTag.Id);
        }
    }

    public bool ExecuteMergeTag(int sourceTagId, int targetTagId)
    {
        try
        {
            using var db = _contextFactory();
            var allTags = db.Tags.Include(t => t.Synonyms).ToList();

            var sourceTag = allTags.FirstOrDefault(t => t.Id == sourceTagId);
            var targetTag = allTags.FirstOrDefault(t => t.Id == targetTagId);
            if (sourceTag == null || targetTag == null)
            {
                return false;
            }

            string sourceName = sourceTag.Name;
            string targetName = targetTag.Name;
            bool wasSelectedSource = (SelectedTag != null && SelectedTag.Id == sourceTagId);

            _tagMergeService.MergeTags(db, sourceTagId, targetTagId, allTags);
            _tagRuleService.DeleteRule(sourceTagId);

            _syncScheduler?.EnqueueLocalChange();

            // 1. Update search query if it references the merged tag
            UpdateSearchQueryAfterTagMerge(sourceName, targetName);

            // 2. Reload tags tree, cache and filter chips
            ReloadTags();

            // 3. Update selected tag
            if (wasSelectedSource)
            {
                var targetNode = FindNodeInTree(TagTreeRoots, targetTagId);
                if (targetNode != null)
                {
                    targetNode.IsSelected = true;
                    SelectedTag = targetNode;
                }
                else
                {
                    SelectedTag = null;
                }
            }
            else if (SelectedTag != null)
            {
                var reselectedNode = FindNodeInTree(TagTreeRoots, SelectedTag.Id);
                if (reselectedNode != null)
                {
                    reselectedNode.IsSelected = true;
                    SelectedTag = reselectedNode;
                }
                else
                {
                    SelectedTag = null;
                }
            }

            // 4. Refresh notes and counters
            RefreshNotes();
            ReloadTemplates();
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Ошибка при объединении тегов: {ex.Message}",
                "Ошибка",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return false;
        }
    }

    private void UpdateSearchQueryAfterTagMerge(string sourceTagName, string targetTagName)
    {
        if (string.IsNullOrWhiteSpace(SearchQuery)) return;

        string pattern = $@"(?<=^|\s)tag:(?:""{System.Text.RegularExpressions.Regex.Escape(sourceTagName)}""|{System.Text.RegularExpressions.Regex.Escape(sourceTagName)})(?=\s|$)";
        string replacementTarget = targetTagName.Contains(' ') ? $"tag:\"{targetTagName}\"" : $"tag:{targetTagName}";

        string updated = System.Text.RegularExpressions.Regex.Replace(SearchQuery, pattern, replacementTarget, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!string.Equals(updated, SearchQuery, StringComparison.Ordinal))
        {
            _searchQuery = updated;
            OnPropertyChanged(nameof(SearchQuery));
        }
    }

    private static TagTreeItemViewModel? FindNodeInTree(IEnumerable<TagTreeItemViewModel> nodes, int tagId)
    {
        foreach (var node in nodes)
        {
            if (node.Id == tagId) return node;
            var found = FindNodeInTree(node.Children, tagId);
            if (found != null) return found;
        }
        return null;
    }

    public async Task RefreshSyncStateAsync(CancellationToken ct = default)
    {
        try
        {
            var cloud = _settingsService.CurrentSettings.CloudSync;
            bool enabled = cloud?.Enabled ?? false;
            bool hasCreds = _credentialsStorage.HasCredentials();
            bool hasPassword = _passwordStorage.HasPassword();
            bool hasBucket = !string.IsNullOrWhiteSpace(cloud?.Bucket);

            int conflicts = await _conflictService.GetUnresolvedConflictsCountAsync(ct);
            UnresolvedConflictsCount = conflicts;

            if (_syncScheduler != null)
            {
                await _syncScheduler.RefreshStateAsync(ct).ConfigureAwait(false);
            }

            if (IsSyncBusy)
            {
                SyncStatus = MainSyncStatus.Syncing;
            }
            else if (!enabled)
            {
                SyncStatus = MainSyncStatus.Disabled;
            }
            else if (!hasCreds || !hasPassword || !hasBucket)
            {
                SyncStatus = MainSyncStatus.NeedsConfig;
            }
            else if (conflicts > 0)
            {
                SyncStatus = MainSyncStatus.Conflicts;
            }
            else if (SyncStatus != MainSyncStatus.Offline && SyncStatus != MainSyncStatus.Error)
            {
                SyncStatus = MainSyncStatus.Ready;
            }
        }
        catch (Exception ex)
        {
            ErrorLogService.Write("MainViewModel.RefreshSyncState", ex);
        }
    }

    public async Task SyncNowAsync(CancellationToken ct = default)
    {
        if (IsSyncBusy || _syncOwnership.IsHeld)
        {
            return;
        }

        var cloud = _settingsService.CurrentSettings.CloudSync;
        bool enabled = cloud?.Enabled ?? false;
        bool hasCreds = _credentialsStorage.HasCredentials();
        bool hasPassword = _passwordStorage.HasPassword();
        bool hasBucket = !string.IsNullOrWhiteSpace(cloud?.Bucket);

        if (!enabled || !hasCreds || !hasPassword || !hasBucket)
        {
            SyncStatus = MainSyncStatus.NeedsConfig;
            StatusText = "Для синхронизации необходимо настроить бакет, ключи S3 и пароль шифрования в Настройках.";
            return;
        }

        IsSyncBusy = true;
        SyncStatus = MainSyncStatus.Syncing;
        StatusText = "Синхронизация с Yandex Cloud…";

        _syncCts?.Dispose();
        _syncCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        CancellationToken token = _syncCts.Token;
        bool refreshAfterBusy = false;

        try
        {
            BoundedOperationResult<SyncCycleResult?> bounded = _syncScheduler != null
                ? await BoundedOperation.RunAsync(
                    workCt => _syncScheduler.TriggerManualSyncAsync(workCt),
                    ManualSyncTimeout,
                    token,
                    "MainViewModel.SyncNow",
                    BoundedTimeoutBehavior.WaitForOwnedWork,
                    _syncOwnership).ConfigureAwait(false)
                : await BoundedOperation.RunAsync(
                    workCt => RunFallbackManualSyncAsync(workCt),
                    ManualSyncTimeout,
                    token,
                    "MainViewModel.SyncNow.Fallback",
                    BoundedTimeoutBehavior.WaitForOwnedWork,
                    _syncOwnership).ConfigureAwait(false);

            if (_syncOwnership.IsCancelling)
            {
                _ignoreSchedulerSyncApply = true;
            }

            RunOnDispatcher(() => ApplyBoundedManualSync(bounded, token.IsCancellationRequested));
            refreshAfterBusy = bounded.Outcome == BoundedOperationOutcome.Cancelled;
        }
        finally
        {
            if (!_syncOwnership.IsCancelling)
            {
                RunOnDispatcher(() =>
                {
                    if (!_isDisposed && !_syncOwnership.IsHeld)
                    {
                        IsSyncBusy = false;
                    }
                });
            }

            _syncCts?.Dispose();
            _syncCts = null;
        }

        if (refreshAfterBusy)
        {
            await RefreshSyncStateAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task<SyncCycleResult?> RunFallbackManualSyncAsync(CancellationToken workCt)
    {
        string? password = await _passwordStorage.LoadPasswordAsync(workCt).ConfigureAwait(false);
        if (string.IsNullOrEmpty(password))
        {
            return SyncCycleResult.Failure("Пароль сквозного шифрования не найден. Задайте пароль в Настройках.");
        }

        ISyncEngine engine = GetOrCreateSyncEngine();
        using var db = _contextFactory();
        try
        {
            return await engine.RunSyncCycleAsync(db, password, ct: workCt).ConfigureAwait(false);
        }
        catch (CloudOfflineException ex)
        {
            return SyncCycleResult.Offline(ex.Message);
        }
        catch (CloudAuthException ex)
        {
            return SyncCycleResult.AuthError(ex.Message);
        }
    }

    private void ApplyBoundedManualSync(BoundedOperationResult<SyncCycleResult?> bounded, bool callerCancelled)
    {
        if (_isDisposed)
        {
            return;
        }

        if (bounded.Outcome != BoundedOperationOutcome.Success)
        {
            if (bounded.Outcome == BoundedOperationOutcome.Cancelled || callerCancelled)
            {
                LastSyncError = null;
                StatusText = UserFacingOperationError.Cancelled;
                return;
            }

            if (bounded.Outcome == BoundedOperationOutcome.Timeout)
            {
                LastSyncError = UserFacingOperationError.Timeout;
                SyncStatus = MainSyncStatus.Error;
                StatusText = UserFacingOperationError.Timeout;
                return;
            }

            LastSyncError = string.IsNullOrWhiteSpace(bounded.UserMessage)
                ? UserFacingOperationError.GenericFailure
                : bounded.UserMessage;
            SyncStatus = MainSyncStatus.Error;
            StatusText = LastSyncError ?? UserFacingOperationError.GenericFailure;
            return;
        }

        SyncCycleResult? result = bounded.Value;
        if (result == null)
        {
            if (callerCancelled)
            {
                LastSyncError = null;
                StatusText = UserFacingOperationError.Cancelled;
                return;
            }

            LastSyncError = UserFacingOperationError.Timeout;
            SyncStatus = MainSyncStatus.Error;
            StatusText = UserFacingOperationError.Timeout;
            return;
        }

        LastSyncResult = result;
        int conflicts = _syncScheduler?.UnresolvedConflictsCount ?? UnresolvedConflictsCount;
        UnresolvedConflictsCount = conflicts;

        if (result.Success)
        {
            LastSyncError = null;
            LastSyncTime = DateTime.Now;
            SyncStatus = conflicts > 0 ? MainSyncStatus.Conflicts : MainSyncStatus.Ready;
            int applied = result.RemoteEntitiesApplied.Created +
                          result.RemoteEntitiesApplied.Updated +
                          result.RemoteEntitiesApplied.Deleted;
            int uploaded = result.PackageUploaded ? 1 : 0;
            StatusText = $"Синхронизация завершена: получено {result.RemotePackagesPulled}, применено {applied}, отправлено {uploaded}, пропущено {result.RemoteEntitiesSkipped}, конфликтов {conflicts}.";

            if (result.RemotePackagesPulled > 0 || applied > 0)
            {
                ReloadAll();
                RefreshNotes();
            }

            return;
        }

        if (result.IsCancelled)
        {
            LastSyncError = null;
            StatusText = UserFacingOperationError.Cancelled;
            return;
        }

        if (result.IsTimeout)
        {
            LastSyncError = UserFacingOperationError.Timeout;
            SyncStatus = MainSyncStatus.Error;
            StatusText = UserFacingOperationError.Timeout;
            return;
        }

        if (result.IsOffline)
        {
            LastSyncError = result.ErrorMessage ?? "Сеть недоступна";
            SyncStatus = MainSyncStatus.Offline;
            StatusText = $"Офлайн: {LastSyncError}";
            return;
        }

        if (result.IsAuthError)
        {
            LastSyncError = result.ErrorMessage ?? "Ошибка аутентификации";
            SyncStatus = MainSyncStatus.NeedsConfig;
            StatusText = $"Ошибка доступа к облаку: {LastSyncError}";
            return;
        }

        if (result.IsKdfWorkBudgetExceeded)
        {
            LastSyncError = result.ErrorMessage ?? UntrustedInboundKdfWorkBudget.UserFacingMessage;
            SyncStatus = MainSyncStatus.Error;
            StatusText = LastSyncError;
            return;
        }

        if (string.Equals(result.ErrorMessage, "Пароль сквозного шифрования не найден. Задайте пароль в Настройках.", StringComparison.Ordinal))
        {
            SyncStatus = MainSyncStatus.NeedsConfig;
            StatusText = result.ErrorMessage ?? "Пароль сквозного шифрования не найден. Задайте пароль в Настройках.";
            return;
        }

        LastSyncError = result.ErrorMessage ?? UserFacingOperationError.GenericFailure;
        SyncStatus = MainSyncStatus.Error;
        StatusText = $"Ошибка синхронизации: {LastSyncError}";
    }

    private void OnManualSyncLeaseDrained()
    {
        RunOnDispatcher(() =>
        {
            if (_isDisposed)
            {
                return;
            }

            _ignoreSchedulerSyncApply = false;
            if (_syncScheduler != null)
            {
                UnresolvedConflictsCount = _syncScheduler.UnresolvedConflictsCount;
            }

            OnPropertyChanged(nameof(SyncQueueStatusText));
            OnPropertyChanged(nameof(LastSyncAttemptTime));
            OnPropertyChanged(nameof(LastSyncSuccessTime));
            OnPropertyChanged(nameof(SyncStatusTooltip));
            OnPropertyChanged(nameof(SyncStatusSummaryText));
            OnPropertyChanged(nameof(PublicationStatus));
            IsSyncBusy = false;
        });
    }

    public void CancelSync()
    {
        try
        {
            _syncScheduler?.CancelCurrentCycle();
            _syncCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public void StartSyncScheduler()
    {
        _syncScheduler?.Start();
    }

    public void StartTaskReminders()
    {
        _taskReminderScheduler.Start();
    }

    private void OnSchedulerStatusChanged(object? sender, SyncSchedulerStatusChangedEventArgs e)
    {
        RunOnDispatcher(() =>
        {
            if (_isDisposed) return;

            bool holdManualLease = _syncOwnership.IsHeld || _ignoreSchedulerSyncApply;
            if (holdManualLease)
            {
                IsSyncBusy = true;
                return;
            }

            IsSyncBusy = e.IsSyncRunning;
            UnresolvedConflictsCount = e.ConflictsCount;
            if (e.LastResult != null)
            {
                LastSyncResult = e.LastResult;
            }
            if (e.LastSuccessTimeUtc.HasValue)
            {
                LastSyncTime = e.LastSuccessTimeUtc.Value.ToLocalTime();
            }

            var cloud = _settingsService.CurrentSettings.CloudSync;
            bool enabled = cloud?.Enabled ?? false;
            bool hasCreds = _credentialsStorage.HasCredentials();
            bool hasPassword = _passwordStorage.HasPassword();
            bool hasBucket = !string.IsNullOrWhiteSpace(cloud?.Bucket);

            if (e.IsSyncRunning)
            {
                SyncStatus = MainSyncStatus.Syncing;
            }
            else if (!enabled)
            {
                SyncStatus = MainSyncStatus.Disabled;
            }
            else if (!hasCreds || !hasPassword || !hasBucket || (e.LastResult?.IsAuthError == true))
            {
                SyncStatus = MainSyncStatus.NeedsConfig;
                if (e.LastResult?.IsAuthError == true)
                {
                    LastSyncError = e.LastResult.ErrorMessage;
                }
            }
            else if (e.ConflictsCount > 0)
            {
                SyncStatus = MainSyncStatus.Conflicts;
            }
            else if (e.QueueState == SyncSchedulerQueueState.ErrorBackoff || (e.LastResult != null && e.LastResult.IsOffline))
            {
                SyncStatus = MainSyncStatus.Offline;
                LastSyncError = e.LastResult?.ErrorMessage ?? "Сеть недоступна";
            }
            else if (e.LastResult != null && !e.LastResult.Success)
            {
                SyncStatus = MainSyncStatus.Error;
                LastSyncError = e.LastResult.ErrorMessage ?? "Ошибка синхронизации";
            }
            else if (SyncStatus != MainSyncStatus.Offline && SyncStatus != MainSyncStatus.Error)
            {
                SyncStatus = MainSyncStatus.Ready;
            }

            OnPropertyChanged(nameof(SyncQueueStatusText));
            OnPropertyChanged(nameof(LastSyncAttemptTime));
            OnPropertyChanged(nameof(LastSyncSuccessTime));
            OnPropertyChanged(nameof(SyncStatusTooltip));
            OnPropertyChanged(nameof(SyncStatusSummaryText));
            OnPropertyChanged(nameof(PublicationStatus));
        });
    }

    private void OnSchedulerSyncCompleted(object? sender, SyncCycleResult result)
    {
        RunOnDispatcher(() =>
        {
            if (_isDisposed || _syncOwnership.IsHeld || _ignoreSchedulerSyncApply)
            {
                return;
            }

            int applied = result.RemoteEntitiesApplied.Created +
                          result.RemoteEntitiesApplied.Updated +
                          result.RemoteEntitiesApplied.Deleted;
            int uploaded = result.PackageUploaded ? 1 : 0;
            int conflicts = _unresolvedConflictsCount;

            if (result.Success)
            {
                LastSyncError = null;
                StatusText = $"Синхронизация завершена: получено {result.RemotePackagesPulled}, применено {applied}, отправлено {uploaded}, пропущено {result.RemoteEntitiesSkipped}, конфликтов {conflicts}.";
                if (result.RemotePackagesPulled > 0 || applied > 0)
                {
                    ReloadAll();
                    RefreshNotes();
                }
                else if (result.PackageUploaded)
                {
                    RefreshNotes();
                }
            }
            else if (result.IsOffline)
            {
                StatusText = $"Офлайн: {result.ErrorMessage ?? "Сеть недоступна"}";
            }
            else if (result.IsAuthError)
            {
                StatusText = $"Ошибка доступа к облаку: {result.ErrorMessage ?? "Ошибка аутентификации"}";
            }
            else if (result.IsKdfWorkBudgetExceeded)
            {
                StatusText = result.ErrorMessage ?? UntrustedInboundKdfWorkBudget.UserFacingMessage;
            }
            else if (result.IsQuotaExceeded)
            {
                StatusText = $"Квота облака: {result.ErrorMessage ?? "хранилище переполнено или лимит исчерпан"}";
            }
            else
            {
                StatusText = $"Ошибка синхронизации: {result.ErrorMessage ?? "Неизвестная ошибка"}";
            }

            OnPropertyChanged(nameof(SyncQueueStatusText));
            OnPropertyChanged(nameof(LastSyncAttemptTime));
            OnPropertyChanged(nameof(LastSyncSuccessTime));
            OnPropertyChanged(nameof(SyncStatusTooltip));
            OnPropertyChanged(nameof(SyncStatusSummaryText));
            OnPropertyChanged(nameof(PublicationStatus));
        });
    }

    public async Task RefreshCloudUsageAsync(CancellationToken ct = default)
    {
        var svc = GetOrCreateCloudUsageService();
        if (svc == null || svc.IsCalculating) return;

        OnPropertyChanged(nameof(IsCloudUsageBusy));
        try
        {
            await svc.CalculateUsageAsync(forceRefresh: true, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ErrorLogService.Write("MainViewModel.RefreshCloudUsage", ex);
        }
        finally
        {
            RunOnDispatcher(() =>
            {
                OnPropertyChanged(nameof(IsCloudUsageBusy));
                OnPropertyChanged(nameof(CloudUsageText));
                OnPropertyChanged(nameof(CloudUsageStatusBarText));
                OnPropertyChanged(nameof(SyncStatusTooltip));
            });
        }
    }

    public ICloudUsageService GetOrCreateCloudUsageService() => _syncCloud.GetOrCreateUsage();

    public ICloudCleanupService GetOrCreateCleanupService() => _syncCloud.GetOrCreateCleanup();

    public ICloudRetentionService GetOrCreateRetentionService() => _syncCloud.GetOrCreateRetention();

    public ISyncPasswordRotationService GetOrCreatePasswordRotationService() => _syncCloud.GetOrCreateRotation();

    public async Task RefreshConflictsStateAsync()
    {
        try
        {
            int conflicts = await _conflictService.GetUnresolvedConflictsCountAsync();
            RunOnDispatcher(() =>
            {
                UnresolvedConflictsCount = conflicts;
                if (conflicts == 0 && SyncStatus == MainSyncStatus.Conflicts)
                {
                    SyncStatus = MainSyncStatus.Ready;
                }
                ReloadAll();
                RefreshNotes();
            });
        }
        catch (Exception ex)
        {
            ErrorLogService.Write("MainViewModel.ConflictsResolved", ex);
            throw;
        }
    }

    public void OpenSyncConflicts()
    {
        var vm = new SyncConflictsViewModel(_conflictService);
        vm.ConflictsResolvedAsync += async () =>
        {
            _syncScheduler?.EnqueueLocalChange();
            await RefreshConflictsStateAsync();
        };

        RequestOpenSyncConflicts?.Invoke(vm);
    }

    private ISyncEngine GetOrCreateSyncEngine() => _syncCloud.GetOrCreateEngine();

    private void ResetProductionSyncEngine()
    {
        _syncScheduler?.CancelCurrentCycle();
        _syncCloud.ResetAfterSettingsChange();
    }

    internal ISyncEngine? ProductionSyncEngine => _syncCloud.CurrentEngine;
    internal ICloudObjectStoreTransport? ProductionTransport => _syncCloud.CurrentTransport;
    internal ISyncEngine GetOrCreateSyncEngineForTesting() => GetOrCreateSyncEngine();

    internal void AttachTestOwnedInfrastructure(IDisposable owned)
    {
        _testOwnedInfrastructure = owned;
    }

    public Task ShutdownAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        lock (_shutdownGate)
        {
            if (_shutdownTask != null)
            {
                return _shutdownTask;
            }

            _shutdownTask = ShutdownCoreAsync(timeout, ct);
            return _shutdownTask;
        }
    }

    private async Task ShutdownCoreAsync(TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            if (_syncScheduler != null)
            {
                try
                {
                    await _syncScheduler.DrainAndStopAsync(timeout, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    ErrorLogService.Write("MainViewModel.ShutdownAsync", ex);
                }
            }
        }
        finally
        {
            Dispose();
        }
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        _syncOwnership.SuppressCallbacks();

        if (_selectedNote != null)
        {
            _selectedNote.PropertyChanged -= OnSelectedNotePropertyChanged;
        }

        CancelSync();
        try
        {
            _ocrCts?.Cancel();
            _ocrCts?.Dispose();
        }
        catch (ObjectDisposedException)
        {
        }

        _ocrCts = null;
        _syncCts?.Dispose();
        _syncCts = null;

        if (_syncScheduler != null)
        {
            _syncScheduler.StatusChanged -= OnSchedulerStatusChanged;
            _syncScheduler.SyncCompleted -= OnSchedulerSyncCompleted;
            try
            {
                _syncScheduler.Stop();
            }
            catch
            {
                // Stop must never prevent shutdown or throw from Dispose.
            }
        }

        if (_hotkeyService != null)
        {
            _hotkeyService.EditorHotkeyPressed -= OnHotkeyPressed;
            _hotkeyService.InstantSaveHotkeyPressed -= OnInstantSaveHotkeyPressed;
        }

        try
        {
            _searchDebouncer.Cancel();
        }
        catch
        {
        }

        _searchCts?.Cancel();
        _searchCts?.Dispose();
        _searchCts = null;

        _taskReminderScheduler.StatusChanged -= OnReminderStatusChanged;
        _taskReminderScheduler.NoteActivationRequested -= OpenNoteFromReminderActivation;
        try
        {
            _taskReminderScheduler.Stop();
        }
        catch
        {
        }

        lock (_editorsLock)
        {
            foreach (var editor in _activeEditors)
            {
                try { editor.Dispose(); } catch { }
            }
            _activeEditors.Clear();
        }

        try
        {
            _testOwnedInfrastructure?.Dispose();
        }
        catch
        {
        }

        _testOwnedInfrastructure = null;
    }
}
