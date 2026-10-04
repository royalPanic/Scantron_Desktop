namespace Scantron.Desktop.ViewModels;

/// <summary>
/// Abstraction over the file pickers, so the view model never references a WPF dialog type.
/// </summary>
/// <remarks>
/// Each method returns null when the operator cancels. The distinction matters: a cancelled
/// picker is not an error, and the caller must not report it as a failed operation.
/// </remarks>
public interface IFilePicker
{
    /// <summary>Prompts for an export file to open as the working document.</summary>
    Uri? PickOpen();

    /// <summary>Prompts for where to write the export.</summary>
    /// <param name="lastPath">Previously used path, offered as the starting point.</param>
    Uri? PickSave(string? lastPath);

    /// <summary>Prompts for the handheld export to merge in.</summary>
    Uri? PickImport();
}
