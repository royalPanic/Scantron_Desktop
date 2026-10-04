using Scantron.Core.Models;
using Scantron.Core.Serialization;

namespace Scantron.Desktop.Services;

/// <summary>
/// Loads and saves export files, refusing to write anything the handheld would reject.
/// </summary>
/// <remarks>
/// The validator gate is the point of this type. The device parses and validates a whole
/// document before touching its database, then clears and replaces - so a bad file does not
/// corrupt device data, but it does waste a trip through a warehouse with a carton of stock in
/// the scanner. Catching it here, before the file leaves the desk, is what turns that into a
/// dialog instead of a field problem.
/// </remarks>
public sealed class InventoryFileService
{
    /// <summary>
    /// Reads an export file.
    /// </summary>
    /// <returns>Null on success, or a message to show the operator.</returns>
    public string? TryLoad(string path, out InventoryDocument? document)
    {
        document = null;

        try
        {
            document = InventoryReader.Read(File.ReadAllText(path));
            Log.Info($"Read {document.Containers.Count} container(s) from {Path.GetFileName(path)}");
            return null;
        }
        catch (Exception ex) when (ex is InventoryFormatException or IOException or UnauthorizedAccessException)
        {
            Log.Error($"Failed reading {path}", ex);
            return $"Could not read {Path.GetFileName(path)}: {ex.Message}";
        }
    }

    /// <summary>
    /// Validates a document against the device's import rules.
    /// </summary>
    /// <returns>Null when importable, otherwise one message per rule that failed.</returns>
    public static string? Validate(InventoryDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var result = InventoryValidator.Validate(document);
        return result.IsValid ? null : string.Join(Environment.NewLine, result.Errors);
    }

    /// <summary>
    /// Writes <paramref name="document"/> in the exact format the device imports.
    /// </summary>
    /// <remarks>
    /// Validation runs first and is not overridable. This is the only place a file reaches the
    /// operator's chosen destination, and an export the device refuses is worse than no export:
    /// it looks successful until someone tries to import it in the aisle.
    /// </remarks>
    /// <returns>Null on success, or a message to show the operator.</returns>
    public string? TrySave(string path, InventoryDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        if (Validate(document) is { } invalid)
        {
            Log.Warning($"Refused to write {Path.GetFileName(path)}: {invalid}");
            return $"This document would be rejected by the handheld:\n{invalid}";
        }

        try
        {
            // The timestamp is stamped here rather than taken from the in-memory copy: the
            // operator may have been editing for an hour since the last merge produced it.
            var stamped = document with { ExportedAt = InventoryFormat.FormatExportedAt(DateTimeOffset.Now) };
            File.WriteAllText(path, InventoryReader.Write(stamped));
            Log.Info($"Wrote {stamped.Containers.Count} container(s) to {Path.GetFileName(path)}");
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error($"Failed writing {path}", ex);
            return $"Could not write {Path.GetFileName(path)}: {ex.Message}";
        }
    }
}
