using System;
using System.Windows.Input;

namespace ClickSyncMouseTester.Infrastructure;

public class DelegateCommand : ICommand
{
    private readonly Action<object> _execute;
    private readonly Predicate<object> _canExecute;

    public event EventHandler CanExecuteChanged;

    public DelegateCommand(Action execute)
        : this(_ => execute(), null)
    {
    }

    public DelegateCommand(Action execute, Func<bool> canExecute)
        : this(_ => execute(), canExecute == null ? null : new Predicate<object>(_ => canExecute()))
    {
    }

    public DelegateCommand(Action<object> execute, Predicate<object> canExecute)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    public bool CanExecute(object parameter)
    {
        return _canExecute == null || _canExecute(parameter);
    }

    public void Execute(object parameter)
    {
        _execute(parameter);
    }

    public void RaiseCanExecuteChanged()
    {
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}


