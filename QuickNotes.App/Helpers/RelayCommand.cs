using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using QuickNotes.App.Services;

namespace QuickNotes.App.Helpers;

public class RelayCommand : ICommand
{
    private readonly Action<object?> _execute;
    private readonly Predicate<object?>? _canExecute;

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public void RaiseCanExecuteChanged() => CommandManager.InvalidateRequerySuggested();

    public RelayCommand(Action<object?> execute, Predicate<object?>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
        : this(_ => execute(), canExecute == null ? null : _ => canExecute())
    {
    }

    public RelayCommand(Func<Task> execute, Func<bool>? canExecute = null)
    {
        ArgumentNullException.ThrowIfNull(execute);
        int running = 0;
        _execute = _ =>
        {
            if (Interlocked.CompareExchange(ref running, 1, 0) != 0)
            {
                return;
            }

            RaiseCanExecuteChanged();
            AsyncEventBridge.Fire(
                async () =>
                {
                    try
                    {
                        await execute().ConfigureAwait(true);
                    }
                    finally
                    {
                        Interlocked.Exchange(ref running, 0);
                        RaiseCanExecuteChanged();
                    }
                },
                "RelayCommand");
        };
        _canExecute = _ => Volatile.Read(ref running) == 0 && (canExecute?.Invoke() ?? true);
    }

    public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;

    public void Execute(object? parameter)
    {
        try { _execute(parameter); }
        catch (Exception ex)
        {
            ErrorLogService.Write("RelayCommand", ex);
            System.Windows.MessageBox.Show(
                UserFacingOperationError.GenericFailure,
                "Ошибка",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
        }
    }
}
