using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using Microsoft.EntityFrameworkCore;
using QuickNotes.App.Data;
using QuickNotes.App.Helpers;
using QuickNotes.App.Models;
using QuickNotes.App.Services;
using QuickNotes.App.Services.Sync;

namespace QuickNotes.App.ViewModels;

public sealed class NoteAssemblySourceItemViewModel : ViewModelBase
{
    public int NoteId { get; }

    public string DisplayTitle { get; }

    public string OrderText => $"#{Order + 1}";

    private int _order;
    public int Order
    {
        get => _order;
        set
        {
            if (SetProperty(ref _order, value))
            {
                OnPropertyChanged(nameof(OrderText));
            }
        }
    }

    public NoteAssemblySourceItemViewModel(int noteId, string displayTitle, int order)
    {
        NoteId = noteId;
        DisplayTitle = displayTitle;
        _order = order;
    }
}

public sealed class NoteAssemblyCandidateItem
{
    public int NoteId { get; init; }

    public string DisplayTitle { get; init; } = string.Empty;

    public override string ToString() => DisplayTitle;
}

public sealed class NoteAssemblySeparatorOption
{
    public NoteAssemblySeparatorKind Kind { get; init; }

    public string Label { get; init; } = string.Empty;

    public string? CustomValue { get; init; }

    public override string ToString() => Label;
}

public sealed class NoteAssemblyViewModel : ViewModelBase
{
    private readonly Func<QuickNotesDbContext> _contextFactory;
    private readonly INoteAssemblyService _assemblyService;
    private readonly INoteHistoryService _historyService;
    private readonly ILocalMutationCoordinator _mutationCoordinator;
    private NoteAssemblyPreviewResult? _preview;
    private string _title = NoteAssemblyService.DefaultTitle;
    private NoteAssemblySeparatorOption? _selectedSeparator;
    private string _customSeparator = "---";
    private NoteAssemblyCandidateItem? _selectedCandidate;
    private NoteAssemblySourceItemViewModel? _selectedSource;
    private bool _moveSourcesToTrash;
    private bool _trashAcknowledged;
    private bool _hasCompletedAssembly;
    private string _statusMessage = "Выберите исходники, заголовок и разделитель. Предпросмотр не записывает базу.";
    private string _successSummary = string.Empty;
    private int _createdNoteId;
    private int _trashedSourceCount;
    private string _candidateSearchText = string.Empty;
    private bool _candidateHasMore;
    private int _candidateTotal;
    private int _candidateLoaded;

    public const int CandidatePageSize = 50;
    public const string ProtectedCandidateLabel = "Защищённая заметка";

    public ObservableCollection<NoteAssemblySourceItemViewModel> Sources { get; } = new();

    public ObservableCollection<NoteAssemblyCandidateItem> Candidates { get; } = new();

    public ObservableCollection<NoteAssemblySeparatorOption> SeparatorOptions { get; } = new();

    public ICommand AddSelectedCandidateCommand { get; }

    public ICommand RemoveSelectedSourceCommand { get; }

    public ICommand MoveSourceUpCommand { get; }

    public ICommand MoveSourceDownCommand { get; }

    public ICommand CommitCommand { get; }

    public ICommand CancelCommand { get; }

    public ICommand LoadMoreCandidatesCommand { get; }

    public Action<bool?>? CloseAction { get; set; }

    public NoteAssemblyViewModel(
        Func<QuickNotesDbContext> contextFactory,
        INoteAssemblyService? assemblyService = null,
        INoteHistoryService? historyService = null,
        IEnumerable<int>? initialSourceIds = null,
        ILocalMutationCoordinator? mutationCoordinator = null)
    {
        _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
        _assemblyService = assemblyService ?? new NoteAssemblyService();
        _historyService = historyService ?? new NoteHistoryService();
        _mutationCoordinator = mutationCoordinator ?? new LocalMutationCoordinator();

        SeparatorOptions.Add(new NoteAssemblySeparatorOption { Kind = NoteAssemblySeparatorKind.ThematicBreakDash, Label = "Линия ---" });
        SeparatorOptions.Add(new NoteAssemblySeparatorOption { Kind = NoteAssemblySeparatorKind.ThematicBreakStar, Label = "Линия ***" });
        SeparatorOptions.Add(new NoteAssemblySeparatorOption { Kind = NoteAssemblySeparatorKind.BlankLine, Label = "Пустая строка" });
        SeparatorOptions.Add(new NoteAssemblySeparatorOption { Kind = NoteAssemblySeparatorKind.Custom, Label = "Свой разделитель" });
        _selectedSeparator = SeparatorOptions[0];

        AddSelectedCandidateCommand = new RelayCommand(AddSelectedCandidate, () => SelectedCandidate != null && !HasCompletedAssembly);
        RemoveSelectedSourceCommand = new RelayCommand(RemoveSelectedSource, () => SelectedSource != null && !HasCompletedAssembly);
        MoveSourceUpCommand = new RelayCommand(() => MoveSelectedSource(-1), () => CanMoveSelected(-1));
        MoveSourceDownCommand = new RelayCommand(() => MoveSelectedSource(1), () => CanMoveSelected(1));
        LoadMoreCandidatesCommand = new RelayCommand(() => ReloadCandidates(append: true), () => CandidateHasMore && !HasCompletedAssembly);
        CommitCommand = new RelayCommand(Commit, () => CanCommit);
        CancelCommand = new RelayCommand(() => CloseAction?.Invoke(HasCompletedAssembly));

        ReloadCandidates();
        if (initialSourceIds != null)
        {
            foreach (int id in initialSourceIds.Distinct())
            {
                TryAddSource(id);
            }
        }

        RebuildPreview();
    }

    public string Title
    {
        get => _title;
        set
        {
            if (SetProperty(ref _title, value ?? string.Empty) && !HasCompletedAssembly)
            {
                RebuildPreview();
            }
        }
    }

    public NoteAssemblySeparatorOption? SelectedSeparator
    {
        get => _selectedSeparator;
        set
        {
            if (SetProperty(ref _selectedSeparator, value) && !HasCompletedAssembly)
            {
                OnPropertyChanged(nameof(IsCustomSeparator));
                RebuildPreview();
            }
        }
    }

    public string CustomSeparator
    {
        get => _customSeparator;
        set
        {
            if (SetProperty(ref _customSeparator, value ?? string.Empty) && !HasCompletedAssembly)
            {
                RebuildPreview();
            }
        }
    }

    public bool IsCustomSeparator => SelectedSeparator?.Kind == NoteAssemblySeparatorKind.Custom;

    public NoteAssemblyCandidateItem? SelectedCandidate
    {
        get => _selectedCandidate;
        set
        {
            if (SetProperty(ref _selectedCandidate, value))
            {
                RaiseCommands();
            }
        }
    }

    public NoteAssemblySourceItemViewModel? SelectedSource
    {
        get => _selectedSource;
        set
        {
            if (SetProperty(ref _selectedSource, value))
            {
                RaiseCommands();
            }
        }
    }

    public bool MoveSourcesToTrash
    {
        get => _moveSourcesToTrash;
        set
        {
            if (SetProperty(ref _moveSourcesToTrash, value))
            {
                OnPropertyChanged(nameof(PreserveSources));
                if (!value)
                {
                    TrashAcknowledged = false;
                }

                OnPropertyChanged(nameof(CanCommit));
                RaiseCommands();
            }
        }
    }

    public bool PreserveSources
    {
        get => !_moveSourcesToTrash;
        set
        {
            if (value)
            {
                MoveSourcesToTrash = false;
            }
        }
    }

    public bool TrashAcknowledged
    {
        get => _trashAcknowledged;
        set
        {
            if (SetProperty(ref _trashAcknowledged, value))
            {
                OnPropertyChanged(nameof(CanCommit));
                RaiseCommands();
            }
        }
    }

    public bool HasCompletedAssembly
    {
        get => _hasCompletedAssembly;
        private set
        {
            if (SetProperty(ref _hasCompletedAssembly, value))
            {
                OnPropertyChanged(nameof(ShowSetup));
                OnPropertyChanged(nameof(CanCommit));
                RaiseCommands();
            }
        }
    }

    public bool ShowSetup => !HasCompletedAssembly;

    public string StatusMessage
    {
        get => _statusMessage;
        internal set => SetProperty(ref _statusMessage, value);
    }

    public string SuccessSummary
    {
        get => _successSummary;
        private set => SetProperty(ref _successSummary, value);
    }

    public int CreatedNoteId
    {
        get => _createdNoteId;
        private set => SetProperty(ref _createdNoteId, value);
    }

    public int TrashedSourceCount
    {
        get => _trashedSourceCount;
        private set
        {
            if (SetProperty(ref _trashedSourceCount, value))
            {
                OnPropertyChanged(nameof(HasTrashedSources));
            }
        }
    }

    public bool HasTrashedSources => TrashedSourceCount > 0;

    public string PreviewMarkdown => _preview?.AssembledMarkdown ?? string.Empty;

    public bool HasBlockingReason => !string.IsNullOrWhiteSpace(_preview?.BlockingReason);

    public string BlockingReason => _preview?.BlockingReason ?? string.Empty;

    public bool HasValidPreview => _preview?.IsValid == true && !HasCompletedAssembly;

    public int PreviewSourceCount => _preview?.Sources.Count ?? Sources.Count;

    public string ResultTagsText =>
        _preview == null || _preview.ResultTags.Count == 0
            ? "Теги не будут добавлены"
            : "Теги результата: " + string.Join(", ", _preview.ResultTags.Select(t => t.TagName));

    public string PreviewConfigurationText
    {
        get
        {
            string order = Sources.Count == 0
                ? "нет исходников"
                : string.Join(" → ", Sources.Select(s => s.DisplayTitle));
            string sep = SelectedSeparator?.Label ?? "—";
            return $"Заголовок: {Title} · порядок: {order} · {sep}";
        }
    }

    public string CandidateSearchText
    {
        get => _candidateSearchText;
        set
        {
            string next = value ?? string.Empty;
            if (SetProperty(ref _candidateSearchText, next) && !HasCompletedAssembly)
            {
                ReloadCandidates();
            }
        }
    }

    public bool CandidateHasMore
    {
        get => _candidateHasMore;
        private set
        {
            if (SetProperty(ref _candidateHasMore, value))
            {
                RaiseCommands();
            }
        }
    }

    public int CandidateTotalCount => _candidateTotal;

    public string CandidateStatusText
    {
        get
        {
            if (_candidateTotal == 0)
            {
                return string.IsNullOrWhiteSpace(CandidateSearchText)
                    ? "Нет доступных заметок."
                    : "Ничего не найдено. Измените поиск — каталог не загружает весь архив сразу.";
            }

            if (CandidateHasMore)
            {
                return $"Показаны {Candidates.Count} из {_candidateTotal}. Поиск или «Ещё» откроют остальные.";
            }

            return $"Показаны {Candidates.Count} из {_candidateTotal}.";
        }
    }

    public bool CanCommit =>
        !HasCompletedAssembly &&
        _preview != null &&
        _preview.IsValid &&
        (!MoveSourcesToTrash || TrashAcknowledged);

    public NoteAssemblyPreviewResult? PreviewResult => _preview;

    public int Execute(INoteHistoryService? historyService = null)
    {
        if (!CanCommit || _preview == null)
        {
            return 0;
        }

        using var db = _contextFactory();
        var result = _assemblyService.Execute(
            db,
            new NoteAssemblyExecuteRequest
            {
                Preview = _preview,
                MoveSourcesToTrash = MoveSourcesToTrash,
                TrashAcknowledged = TrashAcknowledged
            },
            _mutationCoordinator,
            historyService ?? _historyService);

        if (!result.Success)
        {
            StatusMessage = result.ErrorMessage ?? "Сборка не выполнена.";
            OnPropertyChanged(nameof(CanCommit));
            return 0;
        }

        CreatedNoteId = result.CreatedNoteId;
        TrashedSourceCount = result.TrashedSourceCount;
        SuccessSummary = result.Summary;
        StatusMessage = result.Summary;
        HasCompletedAssembly = true;
        OnPropertyChanged(nameof(PreviewMarkdown));
        OnPropertyChanged(nameof(HasValidPreview));
        return result.CreatedNoteId;
    }

    public void RebuildPreview()
    {
        if (HasCompletedAssembly)
        {
            return;
        }

        using var db = _contextFactory();
        int notesBefore = db.Notes.Count();
        _preview = _assemblyService.BuildPreview(db, new NoteAssemblyPreviewRequest
        {
            SourceNoteIds = Sources.Select(s => s.NoteId).ToArray(),
            Title = Title,
            SeparatorKind = SelectedSeparator?.Kind ?? NoteAssemblySeparatorKind.ThematicBreakDash,
            CustomSeparator = CustomSeparator
        });
        int notesAfter = db.Notes.Count();
        if (notesAfter != notesBefore)
        {
            throw new InvalidOperationException("Предпросмотр сборки не должен изменять базу.");
        }

        StatusMessage = _preview.IsValid
            ? "Предпросмотр готов. База не изменялась."
            : _preview.BlockingReason ?? StatusMessage;
        OnPropertyChanged(nameof(PreviewMarkdown));
        OnPropertyChanged(nameof(HasBlockingReason));
        OnPropertyChanged(nameof(BlockingReason));
        OnPropertyChanged(nameof(HasValidPreview));
        OnPropertyChanged(nameof(PreviewSourceCount));
        OnPropertyChanged(nameof(ResultTagsText));
        OnPropertyChanged(nameof(PreviewConfigurationText));
        OnPropertyChanged(nameof(CanCommit));
        RaiseCommands();
    }

    public bool TryAddSource(int noteId)
    {
        if (HasCompletedAssembly || noteId <= 0 || Sources.Any(s => s.NoteId == noteId))
        {
            return false;
        }

        using var db = _contextFactory();
        var note = db.Notes.AsNoTracking().FirstOrDefault(n => n.Id == noteId);
        if (note == null || note.DeletedAt != null)
        {
            StatusMessage = "Нельзя добавить исчезнувшую или удалённую заметку.";
            return false;
        }

        string title = note.IsProtected
            ? ProtectedCandidateLabel
            : NoteTitleHelper.GetDisplayTitle(note.Title, note.Text);
        Sources.Add(new NoteAssemblySourceItemViewModel(note.Id, title, Sources.Count));
        ReloadCandidates();
        RebuildPreview();
        return true;
    }

    private void AddSelectedCandidate()
    {
        if (SelectedCandidate == null)
        {
            return;
        }

        TryAddSource(SelectedCandidate.NoteId);
    }

    private void RemoveSelectedSource()
    {
        if (SelectedSource == null)
        {
            return;
        }

        Sources.Remove(SelectedSource);
        ReindexSources();
        SelectedSource = null;
        ReloadCandidates();
        RebuildPreview();
    }

    private bool CanMoveSelected(int delta)
    {
        if (HasCompletedAssembly || SelectedSource == null)
        {
            return false;
        }

        int index = Sources.IndexOf(SelectedSource);
        int target = index + delta;
        return index >= 0 && target >= 0 && target < Sources.Count;
    }

    private void MoveSelectedSource(int delta)
    {
        if (SelectedSource == null)
        {
            return;
        }

        int index = Sources.IndexOf(SelectedSource);
        int target = index + delta;
        if (index < 0 || target < 0 || target >= Sources.Count)
        {
            return;
        }

        Sources.Move(index, target);
        ReindexSources();
        RebuildPreview();
        RaiseCommands();
    }

    private void ReindexSources()
    {
        for (int i = 0; i < Sources.Count; i++)
        {
            Sources[i].Order = i;
        }
    }

    public void LoadMoreCandidates() => ReloadCandidates(append: true);

    private void ReloadCandidates(bool append = false)
    {
        if (HasCompletedAssembly)
        {
            return;
        }

        int? keepId = SelectedCandidate?.NoteId;
        var selected = new HashSet<int>(Sources.Select(s => s.NoteId));
        using var db = _contextFactory();
        IQueryable<Note> query = db.Notes.AsNoTracking()
            .Where(n => n.DeletedAt == null && !selected.Contains(n.Id));

        string term = (CandidateSearchText ?? string.Empty).Trim();
        if (!string.IsNullOrEmpty(term))
        {
            var ftsIds = new SearchService().SearchNoteIds(db, term);
            string idText = term.TrimStart('#');
            bool hasId = int.TryParse(idText, out int parsedId);
            bool labelMatch = ProtectedCandidateLabel.Contains(term, StringComparison.OrdinalIgnoreCase);

            query = query.Where(n =>
                (!n.IsProtected && (
                    ftsIds.Contains(n.Id) ||
                    n.Title.Contains(term) ||
                    n.Text.Contains(term) ||
                    n.Id.ToString() == idText ||
                    (hasId && n.Id == parsedId)))
                || (n.IsProtected && ((hasId && n.Id == parsedId) || labelMatch)));
        }

        query = query.OrderByDescending(n => n.UpdatedAt).ThenBy(n => n.Id);
        _candidateTotal = query.Count();
        if (!append)
        {
            Candidates.Clear();
            _candidateLoaded = 0;
        }

        var page = query
            .Skip(_candidateLoaded)
            .Take(CandidatePageSize)
            .Select(n => new { n.Id, n.IsProtected, n.Title, n.Text })
            .ToList();

        foreach (var n in page)
        {
            Candidates.Add(new NoteAssemblyCandidateItem
            {
                NoteId = n.Id,
                DisplayTitle = n.IsProtected
                    ? $"#{n.Id} · {ProtectedCandidateLabel}"
                    : $"#{n.Id} · {NoteTitleHelper.GetDisplayTitle(n.Title, n.Text)}"
            });
        }

        _candidateLoaded += page.Count;
        CandidateHasMore = _candidateLoaded < _candidateTotal;
        OnPropertyChanged(nameof(CandidateStatusText));
        OnPropertyChanged(nameof(CandidateTotalCount));

        if (keepId != null)
        {
            SelectedCandidate = Candidates.FirstOrDefault(i => i.NoteId == keepId.Value);
        }
        else if (!append)
        {
            SelectedCandidate = Candidates.FirstOrDefault();
        }
    }

    private void Commit()
    {
        Execute();
        if (HasCompletedAssembly)
        {
            OnPropertyChanged(nameof(CanCommit));
        }
    }

    private void RaiseCommands()
    {
        (AddSelectedCandidateCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (RemoveSelectedSourceCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (MoveSourceUpCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (MoveSourceDownCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (CommitCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (LoadMoreCandidatesCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }
}
