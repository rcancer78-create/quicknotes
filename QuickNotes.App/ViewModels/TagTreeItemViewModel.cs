using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace QuickNotes.App.ViewModels;

public class TagTreeItemViewModel : ViewModelBase
{
    private string _name = string.Empty;
    private bool _isExpanded = true;
    private bool _isSelected;
    private int _noteCount;
    private string _fullPath = string.Empty;
    private IReadOnlyList<string> _breadcrumb = Array.Empty<string>();
    private bool _isDropTarget;

    public int Id { get; set; }
    public int? ParentId { get; set; }

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public int NoteCount
    {
        get => _noteCount;
        set => SetProperty(ref _noteCount, value);
    }

    public string FullPath
    {
        get => _fullPath;
        set => SetProperty(ref _fullPath, value);
    }

    public IReadOnlyList<string> Breadcrumb
    {
        get => _breadcrumb;
        set => SetProperty(ref _breadcrumb, value);
    }

    public bool IsDropTarget
    {
        get => _isDropTarget;
        set => SetProperty(ref _isDropTarget, value);
    }

    public ObservableCollection<TagTreeItemViewModel> Children { get; } = new();

    public TagTreeItemViewModel(int id, string name, int? parentId = null)
    {
        Id = id;
        Name = name;
        ParentId = parentId;
    }
}

