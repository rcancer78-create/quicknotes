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

namespace QuickNotes.App.ViewModels;

public class NoteLinkPickerItem
{
    public int Id { get; }
    public Guid SyncId { get; }
    public string IdText => $"#{Id}";
    public string Title { get; }
    public string Preview { get; }
    public DateTime UpdatedAt { get; }
    public string UpdatedAtText => $"Изменено: {UpdatedAt:dd.MM.yyyy HH:mm}";

    public NoteLinkPickerItem(int id, string title, string preview, DateTime updatedAt, Guid syncId = default)
    {
        Id = id;
        Title = title;
        Preview = preview;
        UpdatedAt = updatedAt;
        SyncId = syncId;
    }
}

public class NoteLinkPickerViewModel : ViewModelBase
{
    private readonly Func<QuickNotesDbContext>? _contextFactory;
    private readonly int? _currentNoteId;
    private readonly List<NoteLinkPickerItem> _allNotes = new();
    private ObservableCollection<NoteLinkPickerItem> _filteredNotes = new();
    private NoteLinkPickerItem? _selectedNote;
    private string _searchText = string.Empty;

    public event Action<bool?>? RequestClose;

    public string Title => "Вставка ссылки на заметку";

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
            {
                ApplyFilter();
            }
        }
    }

    public ObservableCollection<NoteLinkPickerItem> FilteredNotes
    {
        get => _filteredNotes;
        private set => SetProperty(ref _filteredNotes, value);
    }

    public NoteLinkPickerItem? SelectedNote
    {
        get => _selectedNote;
        set
        {
            if (SetProperty(ref _selectedNote, value))
            {
                OnPropertyChanged(nameof(CanInsert));
            }
        }
    }

    public bool CanInsert => SelectedNote != null;
    public bool HasNotes => FilteredNotes.Count > 0;
    public int? SelectedNoteId => SelectedNote?.Id;
    public string FormattedLink => SelectedNote == null
        ? string.Empty
        : SelectedNote.SyncId != Guid.Empty
            ? new NoteLinkService().FormatLink(SelectedNote.SyncId)
            : $"[[{SelectedNote.Id}]]";

    public ICommand InsertCommand { get; }
    public ICommand CancelCommand { get; }

    public NoteLinkPickerViewModel(Func<QuickNotesDbContext> contextFactory, int? currentNoteId = null)
    {
        _contextFactory = contextFactory;
        _currentNoteId = currentNoteId;

        InsertCommand = new RelayCommand(Insert, () => CanInsert);
        CancelCommand = new RelayCommand(Cancel);

        LoadNotes();
    }

    public NoteLinkPickerViewModel(IEnumerable<Note> notes, int? currentNoteId = null)
    {
        _contextFactory = null;
        _currentNoteId = currentNoteId;

        InsertCommand = new RelayCommand(Insert, () => CanInsert);
        CancelCommand = new RelayCommand(Cancel);

        PopulateNotes(notes);
    }

    private void LoadNotes()
    {
        if (_contextFactory == null) return;

        try
        {
            using var db = _contextFactory();
            var query = db.Notes
                .AsNoTracking()
                .Where(n => n.DeletedAt == null);

            if (_currentNoteId.HasValue)
            {
                query = query.Where(n => n.Id != _currentNoteId.Value);
            }

            var notes = query
                .OrderByDescending(n => n.UpdatedAt)
                .Select(n => new { n.Id, n.SyncId, n.Title, n.Text, n.UpdatedAt })
                .Take(200)
                .ToList();

            _allNotes.Clear();
            foreach (var n in notes)
            {
                string title = Helpers.NoteTitleHelper.GetDisplayTitle(n.Title, n.Text);
                string preview = SearchPreview.BuildSnippet(n.Text, null, 100, false);
                _allNotes.Add(new NoteLinkPickerItem(n.Id, title, preview, n.UpdatedAt, n.SyncId));
            }

            ApplyFilter();
        }
        catch (Exception ex)
        {
            ErrorLogService.Write("NoteLinkPicker.LoadNotes", ex);
        }
    }

    private void PopulateNotes(IEnumerable<Note> notes)
    {
        _allNotes.Clear();
        var activeNotes = notes
            .Where(n => n.DeletedAt == null && (!_currentNoteId.HasValue || n.Id != _currentNoteId.Value))
            .OrderByDescending(n => n.UpdatedAt);

        foreach (var n in activeNotes)
        {
            string title = Helpers.NoteTitleHelper.GetDisplayTitle(n.Title, n.Text);
            string preview = SearchPreview.BuildSnippet(n.Text, null, 100, false);
            _allNotes.Add(new NoteLinkPickerItem(n.Id, title, preview, n.UpdatedAt, n.SyncId));
        }

        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var term = _searchText?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(term))
        {
            FilteredNotes = new ObservableCollection<NoteLinkPickerItem>(_allNotes);
        }
        else
        {
            bool isNumber = int.TryParse(term, out int searchId);

            var matches = _allNotes.Where(n =>
                (isNumber && n.Id == searchId) ||
                n.Id.ToString().Contains(term) ||
                n.Title.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                n.Preview.Contains(term, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(n => isNumber && n.Id == searchId ? 1 : 0)
                .ThenByDescending(n => n.UpdatedAt)
                .ToList();

            FilteredNotes = new ObservableCollection<NoteLinkPickerItem>(matches);
        }

        OnPropertyChanged(nameof(HasNotes));
        SelectedNote = FilteredNotes.FirstOrDefault();
    }

    private void Insert()
    {
        if (SelectedNote != null)
        {
            RequestClose?.Invoke(true);
        }
    }

    private void Cancel()
    {
        RequestClose?.Invoke(false);
    }
}
