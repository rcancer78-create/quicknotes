using System.Windows;
using System.Windows.Input;
using QuickNotes.App.ViewModels;

namespace QuickNotes.App.Views;

public partial class SyncConflictsWindow : Window
{
    public SyncConflictsWindow(SyncConflictsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;

        viewModel.RequestClose += result =>
        {
            try
            {
                DialogResult = result;
            }
            catch (InvalidOperationException)
            {
                // Window was shown with Show(), not ShowDialog().
            }

            Close();
        };

        Loaded += (_, _) => ConflictListBox.Focus();
        Closed += (_, _) =>
        {
            if (DataContext is IDisposable disposable)
            {
                disposable.Dispose();
            }
        };
    }

    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (DataContext is not SyncConflictsViewModel vm)
        {
            return;
        }

        if (e.Key == Key.Escape)
        {
            vm.CloseCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (e.KeyboardDevice.Modifiers != ModifierKeys.Alt)
        {
            return;
        }

        switch (e.SystemKey)
        {
            case Key.L:
                if (vm.KeepLocalCommand.CanExecute(null))
                {
                    vm.KeepLocalCommand.Execute(null);
                    e.Handled = true;
                }
                break;
            case Key.R:
                if (vm.AcceptRemoteCommand.CanExecute(null))
                {
                    vm.AcceptRemoteCommand.Execute(null);
                    e.Handled = true;
                }
                break;
            case Key.B:
                if (vm.KeepBothCommand.CanExecute(null))
                {
                    vm.KeepBothCommand.Execute(null);
                    e.Handled = true;
                }
                break;
            case Key.M:
                if (vm.MergeCommand.CanExecute(null))
                {
                    vm.MergeCommand.Execute(null);
                    e.Handled = true;
                }
                break;
        }
    }
}
