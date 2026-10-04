using System.Windows;
using System.Windows.Threading;

namespace Scantron.Desktop;

/// <summary>Application entry point.</summary>
public partial class App : Application
{
    /// <summary>
    /// Turns an unhandled UI exception into a logged report instead of a crash dialog with no
    /// context.
    /// </summary>
    /// <remarks>
    /// Without this a fault in a binding or a dispatcher callback silently kills the process on
    /// a warehouse workstation, and the operator has nothing to send back. The log file records
    /// the type and message; the operator is told where to find it.
    /// </remarks>
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += (_, args) =>
        {
            Services.Log.Error("Unhandled UI exception", args.Exception);
            MessageBox.Show(
                $"Something went wrong:\n\n{args.Exception.Message}\n\n" +
                $"Details were written to:\n{Services.Log.FilePath}",
                "Scantron",
                MessageBoxButton.OK,
                MessageBoxImage.Error);

            // Handled, because losing an in-progress edit to a modal dialog the operator then
            // dismisses is worse than continuing with one bad interaction.
            args.Handled = true;
        };
    }
}
