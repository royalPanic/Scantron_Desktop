using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Scantron.Core.Models;
using Scantron.Desktop.ViewModels;

namespace Scantron.Desktop.Views;

/// <summary>
/// Shell window: wires the view model to the file pickers and the item grid.
/// </summary>
/// <remarks>
/// Deliberately thin. All state lives in <see cref="MainViewModel"/>; this file exists only for
/// the two things a view has to do itself - raise a dialog, and tell the grid that a quantity
/// edit has finished so the row can be stamped.
/// </remarks>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        ViewModel = new MainViewModel { Picker = new FilePicker(this) };
        DataContext = ViewModel;
        Closed += (_, _) => ViewModel.Dispose();
    }

    private MainViewModel ViewModel { get; }

    /// <summary>
    /// Stamps the edited row when a grid cell edit completes.
    /// </summary>
    /// <remarks>
    /// Quantity advances <c>updatedAt</c> through the property setter, but a malformed entry -
    /// a quantity box left empty or holding text - fails the <c>int</c> conversion and never
    /// reaches the setter. That leaves a row whose value is being retyped but whose timestamp
    /// still claims the pre-edit state, which is precisely how an edit gets reverted by the
    /// next merge. Re-stamping after the edit has been applied or discarded closes that gap.
    /// </remarks>
    private void OnItemCellEditEnding(object? sender, DataGridCellEditEndingEventArgs e)
    {
        if (e.Row.Item is not ItemViewModel item || e.Column.DisplayIndex != 2)
        {
            return;
        }

        if (e.EditAction == DataGridEditAction.Cancel)
        {
            return;
        }

        item.Touch();

        if (item.Quantity <= 0)
        {
            // The device does not range-check quantities, so a negative value would import
            // cleanly and silently corrupt stock counts. The validator deliberately allows it,
            // which makes this the right place to catch it.
            ViewModel.StatusMessage =
                $"\"{item.Name}\" has a quantity of {item.Quantity}. Stock cannot be zero or negative.";
        }
    }

    /// <summary>
    /// <see cref="Microsoft.Win32"/> dialogs wrapped for <see cref="IFilePicker"/>.
    /// </summary>
    /// <remarks>
    /// A local implementation rather than a shared one so picker types stay out of the view
    /// model's dependencies: <see cref="MainViewModel"/> only ever sees a <see cref="Uri"/>.
    /// </remarks>
    private sealed class FilePicker : IFilePicker
    {
        private readonly Window _owner;

        public FilePicker(Window owner) => _owner = owner;

        public Uri? PickOpen()
        {
            var dialog = new OpenFileDialog
            {
                Title = "Open a Scantron export",
                Filter = "Scantron export (*.json)|*.json|All files (*.*)|*.*",
                CheckFileExists = true,
            };

            return dialog.ShowDialog(_owner) == true ? SafeUri(dialog.FileName) : null;
        }

        public Uri? PickSave(string? lastPath)
        {
            var dialog = new SaveFileDialog
            {
                Title = "Export for the handheld",
                Filter = "Scantron export (*.json)|*.json",
                // The device's own default name, so an operator who has synced before knows
                // exactly which file to load onto the CK65.
                FileName = lastPath is null
                    ? InventoryFormat.DefaultFileName
                    : Path.GetFileName(lastPath),
                OverwritePrompt = true,
            };

            return dialog.ShowDialog(_owner) == true ? SafeUri(dialog.FileName) : null;
        }

        public Uri? PickImport()
        {
            var dialog = new OpenFileDialog
            {
                Title = "Import the handheld export",
                Filter = "Scantron export (*.json)|*.json|All files (*.*)|*.*",
                CheckFileExists = true,
            };

            return dialog.ShowDialog(_owner) == true ? SafeUri(dialog.FileName) : null;
        }

        /// <summary>Builds a file <see cref="Uri"/>, returning null for a path Windows rejects.</summary>
        private static Uri? SafeUri(string path)
        {
            try
            {
                return new Uri(path, UriKind.Absolute);
            }
            catch (UriFormatException)
            {
                return null;
            }
        }
    }
}
