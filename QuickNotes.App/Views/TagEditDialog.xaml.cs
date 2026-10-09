using System.Windows;
using QuickNotes.App.ViewModels;

namespace QuickNotes.App.Views;

public partial class TagEditDialog : Window
{
    public TagEditDialog(TagEditViewModel viewModel)
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
            TagNameBox.Focus();
            TagNameBox.SelectAll();
        };
    }
}
