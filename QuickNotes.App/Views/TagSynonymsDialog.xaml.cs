using System.Windows;
using QuickNotes.App.ViewModels;

namespace QuickNotes.App.Views;

public partial class TagSynonymsDialog : Window
{
    public TagSynonymsDialog(TagSynonymsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.RequestClose += result =>
        {
            DialogResult = result;
            Close();
        };

        Loaded += (s, e) => NewSynonymBox.Focus();
    }
}
