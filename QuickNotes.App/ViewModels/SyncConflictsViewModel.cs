using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using QuickNotes.App.Helpers;
using QuickNotes.App.Models.Sync;
using QuickNotes.App.Services;
using QuickNotes.App.Services.Sync;

namespace QuickNotes.App.ViewModels;

public class SyncConflictItemViewModel : ViewModelBase
{
    private readonly SyncConflictRecord _record;
    private SyncConflictDetail? _detail;

    public int ConflictId => _record.Id;
    public Guid SyncId => _record.SyncId;
    public string EntityType => _record.EntityType;
    public DateTime DetectedAtUtc => _record.DetectedAtUtc;
    public Guid SourceDeviceId => _record.SourceDeviceId;
    public string Reason => _record.Reason;

    public string EntityTypeDisplay => EntityType switch
    {
        "Note" => "Заметка",
        "Tag" => "Тег",
        "NoteTemplate" => "Шаблон",
        "NoteAttachment" => "Вложение",
        _ => EntityType
    };

    public string DetectedAtDisplay => DetectedAtUtc.ToLocalTime().ToString("dd.MM.yyyy HH:mm");
    public string ShortDeviceId => SourceDeviceId == Guid.Empty
        ? "неизвестно"
        : SourceDeviceId.ToString("N")[..8];

    public SyncConflictDetail? Detail
    {
        get => _detail;
        set
        {
            if (SetProperty(ref _detail, value))
            {
                OnPropertyChanged(nameof(Title));
                OnPropertyChanged(nameof(CanMerge));
                OnPropertyChanged(nameof(CanKeepBoth));
                OnPropertyChanged(nameof(ReasonDisplay));
                OnPropertyChanged(nameof(LocalDeviceDisplay));
                OnPropertyChanged(nameof(RemoteDeviceDisplay));
                OnPropertyChanged(nameof(LocalUpdatedDisplay));
                OnPropertyChanged(nameof(RemoteUpdatedDisplay));
                OnPropertyChanged(nameof(IsProtectedConflict));
                OnPropertyChanged(nameof(ShowManualMerge));
            }
        }
    }

    public string Title
    {
        get
        {
            if (Detail != null)
            {
                if (Detail.LocalIsProtected || Detail.RemoteIsProtected)
                {
                    return $"Защищённый конфликт ({EntityTypeDisplay})";
                }

                return Detail.LocalTitle ?? Detail.RemoteTitle ?? $"Конфликт {EntityTypeDisplay}";
            }
            return $"Конфликт #{ConflictId} ({EntityTypeDisplay})";
        }
    }

    public string ReasonDisplay => Detail?.ReasonDisplay ?? (string.IsNullOrWhiteSpace(Reason) ? "Причина конфликта не указана." : Reason);
    public string LocalDeviceDisplay => Detail?.LocalDeviceDisplay ?? "устройство неизвестно";
    public string RemoteDeviceDisplay => Detail?.RemoteDeviceDisplay ?? (SourceDeviceId == Guid.Empty ? "устройство неизвестно" : $"Устройство {ShortDeviceId}");
    public string LocalUpdatedDisplay => Detail?.LocalUpdatedDisplay ?? "время версии неизвестно";
    public string RemoteUpdatedDisplay => Detail?.RemoteUpdatedDisplay ?? "время версии неизвестно";

    public bool CanMerge => Detail?.CanMerge ?? false;
    public bool CanKeepBoth => Detail?.CanKeepBoth ?? (EntityType != "NoteAttachment");
    public bool IsProtectedConflict => Detail?.LocalIsProtected == true || Detail?.RemoteIsProtected == true;
    public bool ShowManualMerge => CanMerge;

    public SyncConflictItemViewModel(SyncConflictRecord record, SyncConflictDetail? detail = null)
    {
        _record = record;
        _detail = detail;
    }
}

public class SyncConflictsViewModel : ViewModelBase, IDisposable
{
    private readonly ISyncConflictService _conflictService;
    private SyncConflictItemViewModel? _selectedConflict;
    private string _mergedText = string.Empty;
    private bool _isLoading;
    private bool _isResolving;
    private string? _statusMessage;
    private bool _hasAnyResolved;
    private bool _useLocalTitle;
    private bool _useRemoteTitle;
    private bool _useLocalTags;
    private bool _useRemoteTags;
    private string? _successSummary;
    private CancellationTokenSource? _detailLoadCts;
    private int _detailLoadGeneration;
    private bool _disposed;
    private Task _pendingDetailLoad = Task.CompletedTask;

    public ObservableCollection<SyncConflictItemViewModel> Conflicts { get; } = new();

    public SyncConflictItemViewModel? SelectedConflict
    {
        get => _selectedConflict;
        set
        {
            if (SetProperty(ref _selectedConflict, value))
            {
                BeginSelectedConflictDetailLoad();
            }
        }
    }

    public string MergedText
    {
        get => _mergedText;
        set
        {
            if (SetProperty(ref _mergedText, value))
            {
                RaiseCommandCanExecute();
            }
        }
    }

    public bool UseLocalTitle
    {
        get => _useLocalTitle;
        set
        {
            if (SetProperty(ref _useLocalTitle, value) && value)
            {
                UseRemoteTitle = false;
                RaiseCommandCanExecute();
            }
        }
    }

    public bool UseRemoteTitle
    {
        get => _useRemoteTitle;
        set
        {
            if (SetProperty(ref _useRemoteTitle, value) && value)
            {
                UseLocalTitle = false;
                RaiseCommandCanExecute();
            }
        }
    }

    public bool UseLocalTags
    {
        get => _useLocalTags;
        set
        {
            if (SetProperty(ref _useLocalTags, value) && value)
            {
                UseRemoteTags = false;
                RaiseCommandCanExecute();
            }
        }
    }

    public bool UseRemoteTags
    {
        get => _useRemoteTags;
        set
        {
            if (SetProperty(ref _useRemoteTags, value) && value)
            {
                UseLocalTags = false;
                RaiseCommandCanExecute();
            }
        }
    }

    public bool HasExplicitTitleChoice => UseLocalTitle || UseRemoteTitle;
    public bool HasExplicitTagsChoice => UseLocalTags || UseRemoteTags;
    public bool HasDefaultDestructiveChoice => false;

    public bool CanCommitMerge =>
        CanResolve()
        && (SelectedConflict?.CanMerge ?? false)
        && HasExplicitTitleChoice
        && HasExplicitTagsChoice
        && MergedText != null;

    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (SetProperty(ref _isLoading, value))
            {
                RaiseCommandCanExecute();
            }
        }
    }

    public bool IsResolving
    {
        get => _isResolving;
        private set
        {
            if (SetProperty(ref _isResolving, value))
            {
                RaiseCommandCanExecute();
            }
        }
    }

    public string? StatusMessage
    {
        get => _statusMessage;
        set
        {
            if (SetProperty(ref _statusMessage, value))
            {
                OnPropertyChanged(nameof(HasStatusMessage));
            }
        }
    }

    public string? SuccessSummary
    {
        get => _successSummary;
        private set
        {
            if (SetProperty(ref _successSummary, value))
            {
                OnPropertyChanged(nameof(HasCompletedResolution));
            }
        }
    }

    public bool HasCompletedResolution => !string.IsNullOrEmpty(SuccessSummary);
    public bool HasStatusMessage => !string.IsNullOrEmpty(StatusMessage);
    public bool HasConflicts => Conflicts.Count > 0;
    public bool HasNoConflicts => Conflicts.Count == 0;
    public bool ShowComparePane => SelectedConflict != null && !HasCompletedResolution;

    public ICommand KeepBothCommand { get; }
    public ICommand KeepLocalCommand { get; }
    public ICommand AcceptRemoteCommand { get; }
    public ICommand MergeCommand { get; }
    public ICommand CloseCommand { get; }
    public ICommand RefreshCommand { get; }

    public event Action<bool?>? RequestClose;
    public event Action? ConflictsResolved;
    public event Func<Task>? ConflictsResolvedAsync;

    /// <summary>
    /// The in-flight (or last) selected-conflict detail load. Tests await this after changing selection.
    /// The task itself does not fault; stale success, errors, and cancellation are discarded internally.
    /// </summary>
    public Task PendingDetailLoad => _pendingDetailLoad;

    public SyncConflictsViewModel(ISyncConflictService conflictService, bool loadOnStart = true)
    {
        _conflictService = conflictService ?? throw new ArgumentNullException(nameof(conflictService));

        KeepBothCommand = new RelayCommand(async () => await ResolveKeepBothAsync(), () => CanResolve() && (SelectedConflict?.CanKeepBoth ?? false));
        KeepLocalCommand = new RelayCommand(async () => await ResolveKeepLocalAsync(), CanResolve);
        AcceptRemoteCommand = new RelayCommand(async () => await ResolveAcceptRemoteAsync(), CanResolve);
        MergeCommand = new RelayCommand(async () => await ResolveMergeAsync(), () => CanCommitMerge);
        CloseCommand = new RelayCommand(() => RequestClose?.Invoke(_hasAnyResolved));
        RefreshCommand = new RelayCommand(async () => await LoadConflictsAsync(), () => !IsLoading && !IsResolving);

        if (loadOnStart)
        {
            AsyncEventBridge.Fire(() => LoadConflictsAsync(), "SyncConflicts.Load");
        }
    }

    private bool CanResolve() => !IsResolving && !IsLoading && SelectedConflict != null;

    private void RaiseCommandCanExecute()
    {
        OnPropertyChanged(nameof(HasExplicitTitleChoice));
        OnPropertyChanged(nameof(HasExplicitTagsChoice));
        OnPropertyChanged(nameof(CanCommitMerge));
        OnPropertyChanged(nameof(ShowComparePane));
        (KeepBothCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (KeepLocalCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (AcceptRemoteCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (MergeCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (RefreshCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    public async Task LoadConflictsAsync(CancellationToken ct = default)
    {
        if (IsLoading) return;
        IsLoading = true;
        SuccessSummary = null;
        StatusMessage = "Загрузка конфликтов…";

        try
        {
            var records = await _conflictService.GetUnresolvedConflictsAsync(ct);
            if (_disposed) return;
            CancelPendingDetailLoad();
            Conflicts.Clear();

            foreach (var rec in records)
            {
                var item = new SyncConflictItemViewModel(rec);
                Conflicts.Add(item);
            }

            OnPropertyChanged(nameof(HasConflicts));
            OnPropertyChanged(nameof(HasNoConflicts));

            if (Conflicts.Count > 0)
            {
                SelectedConflict = Conflicts[0];
                StatusMessage = null;
            }
            else
            {
                SelectedConflict = null;
                StatusMessage = "Неразрешённых конфликтов нет.";
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (!_disposed)
            {
                StatusMessage = $"Ошибка загрузки списка конфликтов: {ex.Message}";
            }
        }
        finally
        {
            if (!_disposed)
            {
                IsLoading = false;
            }
        }
    }

    private void BeginSelectedConflictDetailLoad()
    {
        var load = LoadSelectedConflictDetailAsync();
        _pendingDetailLoad = load;
        ObserveDetailLoad(load);
    }

    private void ObserveDetailLoad(Task load)
    {
        AsyncEventBridge.Fire(() => load, "SyncConflicts.DetailLoad");
    }

    internal async Task LoadSelectedConflictDetailAsync()
    {
        CancelPendingDetailLoad();
        int generation = _detailLoadGeneration;
        var item = _selectedConflict;
        int conflictId = item?.ConflictId ?? 0;

        ResetMergeChoices();
        MergedText = string.Empty;
        RaiseCommandCanExecute();

        if (item == null || _disposed)
        {
            return;
        }

        if (item.Detail != null)
        {
            if (!IsDetailLoadCurrent(item, conflictId, generation))
            {
                return;
            }

            MergedText = item.Detail.CanMerge ? item.Detail.InitialMergedText : string.Empty;
            RaiseCommandCanExecute();
            return;
        }

        var cts = new CancellationTokenSource();
        _detailLoadCts = cts;
        try
        {
            var detail = await _conflictService.GetConflictDetailAsync(conflictId, cts.Token);
            if (!IsDetailLoadCurrent(item, conflictId, generation))
            {
                return;
            }

            if (detail != null)
            {
                item.Detail = detail;
                MergedText = detail.CanMerge ? detail.InitialMergedText : string.Empty;
            }

            RaiseCommandCanExecute();
        }
        catch (OperationCanceledException)
        {
            // Stale or disposed load must not change the current selection, status, merge choices, or CanExecute.
        }
        catch (Exception ex)
        {
            if (!IsDetailLoadCurrent(item, conflictId, generation) || _disposed)
            {
                return;
            }

            StatusMessage = $"Не удалось загрузить подробности конфликта: {ex.Message}";
        }
        finally
        {
            if (ReferenceEquals(_detailLoadCts, cts))
            {
                _detailLoadCts = null;
            }

            cts.Dispose();
        }
    }

    private bool IsDetailLoadCurrent(SyncConflictItemViewModel item, int conflictId, int generation)
    {
        return !_disposed
            && generation == _detailLoadGeneration
            && ReferenceEquals(_selectedConflict, item)
            && _selectedConflict.ConflictId == conflictId;
    }

    private void CancelPendingDetailLoad()
    {
        _detailLoadGeneration++;
        var previous = _detailLoadCts;
        _detailLoadCts = null;
        if (previous == null)
        {
            return;
        }

        try
        {
            previous.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            previous.Dispose();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        CancelPendingDetailLoad();
    }

    public void ClearMergeChoices()
    {
        ResetMergeChoices();
        RaiseCommandCanExecute();
    }

    private void ResetMergeChoices()
    {
        _useLocalTitle = false;
        _useRemoteTitle = false;
        _useLocalTags = false;
        _useRemoteTags = false;
        OnPropertyChanged(nameof(UseLocalTitle));
        OnPropertyChanged(nameof(UseRemoteTitle));
        OnPropertyChanged(nameof(UseLocalTags));
        OnPropertyChanged(nameof(UseRemoteTags));
    }

    public async Task ResolveKeepBothAsync(CancellationToken ct = default)
    {
        await ExecuteResolutionAsync(
            conflictId => _conflictService.ResolveKeepBothAsync(conflictId, ct),
            "Разрешено: сохранены обе версии (создана отдельная копия).");
    }

    public async Task ResolveKeepLocalAsync(CancellationToken ct = default)
    {
        await ExecuteResolutionAsync(
            conflictId => _conflictService.ResolveKeepLocalAsync(conflictId, ct),
            "Разрешено: сохранена локальная версия.");
    }

    public async Task ResolveAcceptRemoteAsync(CancellationToken ct = default)
    {
        await ExecuteResolutionAsync(
            conflictId => _conflictService.ResolveAcceptRemoteAsync(conflictId, ct),
            "Разрешено: принята облачная версия.");
    }

    public async Task ResolveMergeAsync(CancellationToken ct = default)
    {
        if (!CanCommitMerge || SelectedConflict == null) return;
        var choices = new SyncConflictMergeChoices
        {
            Title = UseLocalTitle ? SyncConflictFieldChoice.Local : SyncConflictFieldChoice.Remote,
            Tags = UseLocalTags ? SyncConflictFieldChoice.Local : SyncConflictFieldChoice.Remote
        };

        await ExecuteResolutionAsync(
            conflictId => _conflictService.ResolveMergeNoteAsync(conflictId, MergedText, choices, ct),
            "Разрешено: создана объединённая версия заметки.");
    }

    private async Task ExecuteResolutionAsync(
        Func<int, Task<SyncConflictResolutionResult>> resolve,
        string successMessage)
    {
        if (!CanResolve() || SelectedConflict == null) return;
        IsResolving = true;
        int conflictId = SelectedConflict.ConflictId;

        try
        {
            var result = await resolve(conflictId);
            if (result.Success)
            {
                StatusMessage = successMessage;
                await OnConflictResolvedSuccessfullyAsync(conflictId, successMessage);
            }
            else
            {
                StatusMessage = result.ErrorMessage ?? "Ошибка при разрешении конфликта.";
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            StatusMessage = $"Ошибка: {ex.Message}";
        }
        finally
        {
            IsResolving = false;
        }
    }

    private async Task OnConflictResolvedSuccessfullyAsync(int conflictId, string successMessage)
    {
        _hasAnyResolved = true;
        var itemToRemove = Conflicts.FirstOrDefault(c => c.ConflictId == conflictId);
        if (itemToRemove != null)
        {
            int index = Conflicts.IndexOf(itemToRemove);
            Conflicts.Remove(itemToRemove);
            OnPropertyChanged(nameof(HasConflicts));
            OnPropertyChanged(nameof(HasNoConflicts));
            OnPropertyChanged(nameof(HasCompletedResolution));

            if (Conflicts.Count > 0)
            {
                int newIndex = Math.Min(index, Conflicts.Count - 1);
                SelectedConflict = Conflicts[newIndex];
            }
            else
            {
                SelectedConflict = null;
                SuccessSummary = successMessage + " Новая ревизия записана и будет опубликована Sync идемпотентно.";
            }
        }

        RaiseCommandCanExecute();

        if (ConflictsResolvedAsync != null)
        {
            foreach (Func<Task> handler in ConflictsResolvedAsync.GetInvocationList().Cast<Func<Task>>())
            {
                await handler();
            }
        }
        else
        {
            ConflictsResolved?.Invoke();
        }
    }
}
