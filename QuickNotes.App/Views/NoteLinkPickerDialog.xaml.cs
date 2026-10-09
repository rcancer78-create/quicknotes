using System.Windows;
using System.Windows.Input;
using QuickNotes.App.ViewModels;

namespace QuickNotes.App.Views;

public partial class NoteLinkPickerDialog : Window
{
    private readonly NoteLinkPickerViewModel _viewModel;

    public NoteLinkPickerDialog(NoteLinkPickerViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;

        viewModel.RequestClose += result =>
        {
            DialogResult = result;
            Close();
        };

        Loaded += (s, e) =>
        {
            SearchBox.Focus();
        };
    }

    private void OnListMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_viewModel.InsertCommand.CanExecute(null))
        {
            _viewModel.InsertCommand.Execute(null);
        }
    }
}
