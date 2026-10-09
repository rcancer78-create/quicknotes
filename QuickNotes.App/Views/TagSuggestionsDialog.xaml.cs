using System.Windows;
using QuickNotes.App.ViewModels;

namespace QuickNotes.App.Views;

public partial class TagSuggestionsDialog : Window
{
    public TagSuggestionsDialog(TagSuggestionsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.RequestClose += result =>
        {
            DialogResult = result;
            Close();
        };
    }
}
