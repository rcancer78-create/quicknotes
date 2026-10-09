using System;
using System.Windows.Input;
using QuickNotes.App.Helpers;
using QuickNotes.App.Models;

namespace QuickNotes.App.ViewModels;

public class SearchChipViewModel : ViewModelBase
{
    public SearchFilterChipModel Model { get; }
    public string DisplayText => Model.DisplayText;
    public ICommand RemoveCommand { get; }

    public SearchChipViewModel(SearchFilterChipModel model, Action<SearchFilterChipModel> onRemove)
    {
        Model = model;
        RemoveCommand = new RelayCommand(() => onRemove(model));
    }
}
