using System.Windows;
using QuickNotes.App.ViewModels;

namespace QuickNotes.App.Views;

public partial class SavedViewEditDialog : Window
{
    public SavedViewEditDialog(SavedViewEditViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.RequestClose += result =>
        {
            DialogResult = result;
            Close();
        };

        Loaded += (s, e) =>
        {
            ViewNameBox.Focus();
            ViewNameBox.SelectAll();
        };
    }
}
