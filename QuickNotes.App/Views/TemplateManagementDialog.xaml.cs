using System.Windows;
using System.Windows.Input;
using QuickNotes.App.ViewModels;
using MessageBox = System.Windows.MessageBox;

namespace QuickNotes.App.Views;

public partial class TemplateManagementDialog : Window
{
    private readonly TemplateManagementViewModel _viewModel;

    public TemplateManagementDialog(TemplateManagementViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;

        viewModel.RequestTemplateEditDialog = vm =>
        {
            var dialog = new TemplateEditDialog(vm)
            {
                Owner = this
            };
            return dialog.ShowDialog();
        };

        viewModel.ConfirmDeleteHandler = (message, title) =>
        {
            var result = MessageBox.Show(this, message, title, MessageBoxButton.YesNo, MessageBoxImage.Question);
            return result == MessageBoxResult.Yes;
        };

        viewModel.AlertHandler = (message, title) =>
        {
            MessageBox.Show(this, message, title, MessageBoxButton.OK, MessageBoxImage.Warning);
        };

        viewModel.RequestClose = result =>
        {
            DialogResult = result;
            Close();
        };
    }

    private void TemplatesListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_viewModel.EditCommand.CanExecute(null))
        {
            _viewModel.EditCommand.Execute(null);
        }
    }

    private void TemplatesListBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Delete && _viewModel.DeleteCommand.CanExecute(null))
        {
            _viewModel.DeleteCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && _viewModel.EditCommand.CanExecute(null))
        {
            _viewModel.EditCommand.Execute(null);
            e.Handled = true;
        }
    }
}
