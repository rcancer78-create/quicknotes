using System.Windows;
using QuickNotes.App.ViewModels;

namespace QuickNotes.App.Views;

public partial class ChangeParentDialog : Window
{
    public ChangeParentDialog(ChangeParentViewModel viewModel)
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
