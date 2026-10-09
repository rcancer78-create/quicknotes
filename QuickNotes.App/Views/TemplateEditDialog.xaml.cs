using System.Windows;
using QuickNotes.App.ViewModels;

namespace QuickNotes.App.Views;

public partial class TemplateEditDialog : Window
{
    public TemplateEditDialog(TemplateEditViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.RequestClose = result =>
        {
            DialogResult = result;
            Close();
        };
    }
}
