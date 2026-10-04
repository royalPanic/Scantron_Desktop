using Scantron.Core.Models;
using Scantron.Core.Serialization;

namespace Scantron.Desktop.Services;

/// <summary>
/// Reads and writes the desktop's working state as two export-format documents.
/// </summary>
/// <remarks>
/// <para>
/// Two documents are persisted, not one. <em>Current</em> is what the operator is editing;
/// <em>base</em> is the snapshot both sides last agreed on, and it is the third input to the
/// three-way merge. Losing base silently downgrades every future merge to a first sync, where
/// no difference can be told apart from a conflict - so it is stored as carefully as current.
/// </para>
/// <para>
/// Both files are written in the device's own export format and parsed by
/// <see cref="InventoryReader"/>, so this store holds no DTO mapping of its own. That is what
/// keeps it from drifting: a change to the wire layer cannot make the workspace disagree with
/// what an export would produce, and an operator can inspect either file with any text editor.
/// </para>
/// <para>
/// Each file is staged in a temporary and then moved into place, so a crash mid-save leaves the
/// previous good copy rather than a truncated one. Without that, a power cut during a sync
/// would be indistinguishable from a corrupted inventory.
/// </para>
/// </remarks>
public sealed class WorkspaceStore
{
    /// <summary>File holding the document the operator is editing.</summary>
    public string CurrentPath { get; }

    /// <summary>File holding the last mutually-agreed snapshot. Absent on a first run.</summary>
    public string BasePath { get; }

    public WorkspaceStore(string? directory = null)
    {
        var root = directory ?? AppContext.BaseDirectory;
        CurrentPath = Path.Combine(root, "workspace.current.json");
        BasePath = Path.Combine(root, "workspace.base.json");
    }

    /// <summary>What the operator is editing. Null when nothing has been loaded yet.</summary>
    public InventoryDocument? Current { get; private set; }

    /// <summary>Last mutually-agreed snapshot, or null on a first run.</summary>
    public InventoryDocument? Base { get; private set; }

    /// <summary>
    /// Loads the workspace if present.
    /// </summary>
    /// <remarks>
    /// A missing file is the normal first-run case and is not an error. A corrupt file is
    /// reported but never deleted: the operator may still be able to salvage an export from it,
    /// and a sync tool that quietly discards inventory is worse than one that complains.
    /// </remarks>
    public bool TryLoad(out string? error)
    {
        error = null;
        Current = ReadOptional(CurrentPath, "working document", out var currentError);
        Base = ReadOptional(BasePath, "last-synced snapshot", out var baseError);

        error = Join(currentError, baseError);
        Log.Info($"Workspace: {Current?.Containers.Count ?? 0} container(s), base {(Base is null ? "absent" : "present")}");
        return Current is not null;
    }

    /// <summary>Records the documents to persist. Nothing is written until <see cref="Save"/>.</summary>
    public void SetDocuments(InventoryDocument? current, InventoryDocument? @base)
    {
        Current = current;
        Base = @base;
    }

    /// <summary>
    /// Promotes <paramref name="document"/> to the shared base.
    /// </summary>
    /// <remarks>
    /// Called after a successful sync. Until this happens the next merge would still diff
    /// against the pre-sync snapshot and re-report every row the operator just settled.
    /// </remarks>
    public void PromoteBase(InventoryDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        Base = document;
    }

    /// <summary>Writes both documents atomically.</summary>
    /// <returns>Null on success, or a message describing the failure.</returns>
    public string? Save()
    {
        if (Current is null)
        {
            return "Nothing to save";
        }

        try
        {
            var directory = Path.GetDirectoryName(CurrentPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            StageWrite(CurrentPath, InventoryReader.Write(Current));

            // The base is only ever advanced, so a failed base write leaves a stale snapshot
            // rather than none: the next merge re-reports settled rows, which is recoverable.
            if (Base is not null)
            {
                StageWrite(BasePath, InventoryReader.Write(Base));
            }

            Log.Debug($"Workspace saved to {Path.GetDirectoryName(CurrentPath)}");
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error("Workspace save failed", ex);
            return $"Could not save the workspace: {ex.Message}";
        }
    }

    /// <summary>
    /// Writes <paramref name="path"/> via a temporary file in the same directory.
    /// </summary>
    /// <remarks>
    /// The temporary must share a volume with the target, otherwise the final step degrades from
    /// an atomic rename to a copy, which is exactly the window this is here to close.
    /// </remarks>
    private static void StageWrite(string path, string content)
    {
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, content);
        File.Move(temporary, path, overwrite: true);
    }

    private static InventoryDocument? ReadOptional(string path, string description, out string? error)
    {
        error = null;

        if (!File.Exists(path))
        {
            Log.Debug($"No {description} at {path}");
            return null;
        }

        try
        {
            return InventoryReader.Read(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is InventoryFormatException or IOException or UnauthorizedAccessException)
        {
            error = $"Could not read the {description} ({Path.GetFileName(path)}): {ex.Message}. " +
                    "The file has been left in place.";
            Log.Error($"Failed reading {description}", ex);
            return null;
        }
    }

    private static string? Join(string? first, string? second) =>
        (first, second) switch
        {
            (null, null) => null,
            (null, _) => second,
            (_, null) => first,
            _ => $"{first} {second}",
        };
}
