using System.Windows;
using System.Windows.Input;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using QuickNotes.App.ViewModels;

namespace QuickNotes.App.Views;

public partial class NoteAssemblyWindow : Window
{
    private readonly NoteAssemblyViewModel _viewModel;

    public NoteAssemblyWindow(NoteAssemblyViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        viewModel.CloseAction = result =>
        {
            DialogResult = result;
            Close();
        };

        Loaded += (_, _) =>
        {
            AssemblyTitleBox.Focus();
            AssemblyTitleBox.SelectAll();
        };
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) != ModifierKeys.Control)
        {
            return;
        }

        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Up && _viewModel.MoveSourceUpCommand.CanExecute(null))
        {
            _viewModel.MoveSourceUpCommand.Execute(null);
            e.Handled = true;
        }
        else if (key == Key.Down && _viewModel.MoveSourceDownCommand.CanExecute(null))
        {
            _viewModel.MoveSourceDownCommand.Execute(null);
            e.Handled = true;
        }
    }
}
