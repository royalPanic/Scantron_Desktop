using System.Windows.Input;

namespace Scantron.Desktop.Mvvm;

/// <summary>
/// <see cref="ICommand"/> over a delegate pair.
/// </summary>
/// <remarks>
/// <see cref="RaiseCanExecuteChanged"/> must be called by the view model whenever guard state
/// changes: WPF only re-queries <see cref="CanExecute"/> when told to, which otherwise leaves
/// buttons enabled for operations the app cannot service.
/// </remarks>
public sealed class RelayCommand : ICommand
{
    private readonly Action<object?> _execute;
    private readonly Predicate<object?>? _canExecute;

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
        : this(_ => execute(), canExecute is null ? null : _ => canExecute())
    {
    }

    public RelayCommand(Action<object?> execute, Predicate<object?>? canExecute = null)
    {
        ArgumentNullException.ThrowIfNull(execute);
        _execute = execute;
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;

    public void Execute(object? parameter)
    {
        if (CanExecute(parameter))
        {
            _execute(parameter);
        }
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>
/// <see cref="RelayCommand"/> whose parameter is strongly typed.
/// </summary>
/// <remarks>
/// Used by the file operations, which take a path from the picker. Keeping the parameter typed
/// means a <c>Uri</c> is never mistaken for a row selection at a call site, and that a command
/// bound to the wrong thing fails at the call rather than at runtime.
/// </remarks>
/// <typeparam name="T">Type of the command parameter.</typeparam>
public sealed class RelayCommand<T> : ICommand
{
    private readonly Action<T?> _execute;
    private readonly Func<bool>? _canExecute;

    public RelayCommand(Action<T?> execute, Func<bool>? canExecute = null)
    {
        ArgumentNullException.ThrowIfNull(execute);
        _execute = execute;
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => _canExecute?.Invoke() ?? true;

    public void Execute(object? parameter)
    {
        if (!_canExecute?.Invoke() ?? false)
        {
            return;
        }

        // A DataGrid can hand a command a parameter of the wrong type when a binding is miswired;
        // treating that as "nothing to do" is safer than a cast exception in a click handler.
        if (parameter is T typed)
        {
            _execute(typed);
        }
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
