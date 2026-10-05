using System.Windows.Input;
using Scantron.Desktop.Services;

namespace Scantron.Desktop.Mvvm;

/// <summary>
/// <see cref="ICommand"/> whose handler is asynchronous.
/// </summary>
/// <remarks>
/// <para>
/// Exists for the transfer commands, which wait on a socket. <see cref="RelayCommand"/> cannot
/// carry those: <see cref="ICommand.Execute"/> is void, so there is nowhere to await, and doing
/// the work inline would freeze the window for the length of the handheld's timeout - an operator
/// holding a carton of stock, staring at a dead app.
/// </para>
/// <para>
/// The handler is fire-and-forget. That is deliberate, and it is why <c>_running</c> exists and
/// why <see cref="RaiseCanExecuteChanged"/> has to be called again when the handler finishes: an
/// unobserved exception from a void-returning async method would otherwise reach the dispatcher
/// and take the process down, and the button would stay enabled with nothing to show for it. The
/// catch below turns any escape into a logged fault instead, which is all that is left once the
/// handlers here have already reported their own failures through the status line.
/// </para>
/// </remarks>
public sealed class AsyncRelayCommand : ICommand
{
    private readonly Func<object?, Task> _execute;
    private readonly Predicate<object?>? _canExecute;

    /// <summary>Guards against a second run starting while the first is still awaiting.</summary>
    private bool _running;

    public AsyncRelayCommand(Func<Task> execute, Func<bool>? canExecute = null)
        : this(_ => execute(), canExecute is null ? null : _ => canExecute())
    {
    }

    public AsyncRelayCommand(Func<object?, Task> execute, Predicate<object?>? canExecute = null)
    {
        ArgumentNullException.ThrowIfNull(execute);
        _execute = execute;
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;

    /// <summary>
    /// False while the handler is running, whatever the guard says.
    /// </summary>
    /// <remarks>
    /// The guard is a property of the world - a document loaded, the hub listening - and none of
    /// those change while a transfer is in flight, so the guard alone would happily allow a
    /// second run. This is what makes the command genuinely single-shot.
    /// </remarks>
    public bool CanExecute(object? parameter) =>
        !_running && (_canExecute?.Invoke(parameter) ?? true);

    public void Execute(object? parameter)
    {
        if (!CanExecute(parameter))
        {
            return;
        }

        _running = true;
        RaiseCanExecuteChanged();

        _ = RunAsync(parameter);
    }

    private async Task RunAsync(object? parameter)
    {
        try
        {
            await _execute(parameter).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            // Only reachable by a handler that let something escape. Logged rather than rethrown:
            // rethrowing would surface on the dispatcher and close the window, and the handlers in
            // this app report their own failures through the status line, so there is nothing left
            // here that an operator could act on.
            Log.Error($"Unhandled error in {nameof(AsyncRelayCommand)}", ex);
        }
        finally
        {
            _running = false;
            RaiseCanExecuteChanged();
        }
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}