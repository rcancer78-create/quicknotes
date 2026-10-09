using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using QuickNotes.App.Helpers;
using QuickNotes.App.Models;
using QuickNotes.App.Services;

namespace QuickNotes.App.ViewModels;

public class ChangeParentViewModel : ViewModelBase
{
    private Tag? _selectedParent;

    public Tag Tag { get; }
    public string Title => $"Изменить родителя для «{Tag.Name}»";

    public ObservableCollection<Tag?> AvailableParents { get; } = new();

    public Tag? SelectedParent
    {
        get => _selectedParent;
        set => SetProperty(ref _selectedParent, value);
    }

    public ICommand SaveCommand { get; }
    public ICommand CancelCommand { get; }

    public event Action<bool?>? RequestClose;

    public ChangeParentViewModel(Tag tag, List<Tag> allTags)
    {
        Tag = tag;

        // Root option is represented by null
        AvailableParents.Add(new Tag { Id = 0, Name = "[ Корень (без родителя) ]" });

        // Find valid parents that would NOT create a cycle
        foreach (var candidate in allTags.OrderBy(t => t.Name))
        {
            if (!TagHierarchyService.WouldCreateCycle(tag.Id, candidate.Id, allTags))
            {
                AvailableParents.Add(candidate);
            }
        }

        SelectedParent = AvailableParents.FirstOrDefault(t => t?.Id == tag.ParentTagId) ?? AvailableParents[0];

        SaveCommand = new RelayCommand(Save);
        CancelCommand = new RelayCommand(() => RequestClose?.Invoke(false));
    }

    private void Save()
    {
        RequestClose?.Invoke(true);
    }
}
