using System.Globalization;
using Scantron.Core.Models;
using Scantron.Core.Serialization;

namespace Scantron.Desktop.Services.Transfer;

/// <summary>
/// Stages inbound pushes to disk under <c>inbox\</c>, beside the workspace files.
/// </summary>
/// <remarks>
/// <para>
/// Every push is written out before it is merged. The established rule in this app is that a
/// transfer landing mid-write must not be able to corrupt state, and the same reasoning runs in
/// the other direction: the desktop must never be the only copy of what a handheld just sent.
/// If a merge goes wrong, or the operator wants to see what actually arrived, the file is there.
/// </para>
/// <para>
/// Writes go through a temporary file and are then moved into place, exactly as
/// <c>WorkspaceStore</c> does it. A staged file is therefore always a whole file: a crash leaves
/// the previous good copy rather than a truncated one.
/// </para>
/// <para>
/// The inbox is capped and pruned. An unbounded inbox on a workstation that receives a push
/// whenever a scanner syncs is a slow disk failure discovered on the day it matters.
/// </para>
/// </remarks>
public sealed class InboxStore
{
    /// <summary>
    /// How many staged files are kept. The newest survive; older ones are pruned.
    /// </summary>
    /// <remarks>
    /// Twenty is a working week of pushes for one operator. Anything older has either been merged
    /// or abandoned, and the file log records that it arrived.
    /// </remarks>
    public const int Retention = 20;

    /// <summary>
    /// Highest sequence number the timestamped file name carries.
    /// </summary>
    /// <remarks>
    /// Three digits is what sorts correctly against itself: the name is a fixed-width field, so
    /// lexical order and arrival order agree without having to parse anything.
    /// </remarks>
    private const int MaxSequence = 999;

    /// <summary>Directory the staged pushes live in.</summary>
    public string Directory { get; }

    public InboxStore(string? directory = null)
    {
        var root = directory ?? AppContext.BaseDirectory;
        Directory = Path.Combine(root, "inbox");
    }

    /// <summary>
    /// Writes <paramref name="document"/> to a timestamped file.
    /// </summary>
    /// <returns>The path written, or a message describing why it could not be.</returns>
    public string? Stage(InventoryDocument document, out string? path)
    {
        ArgumentNullException.ThrowIfNull(document);

        path = null;

        try
        {
            System.IO.Directory.CreateDirectory(Directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error($"Could not create the inbox at {Directory}", ex);
            return $"Could not create the inbox at {Directory}: {ex.Message}";
        }

        var name = NextFileName();
        path = Path.Combine(Directory, name);

        try
        {
            // Re-serialized from the parsed document rather than forwarded as received. A staged
            // file has to be one the desktop could itself have written - text arriving over the
            // network is exactly the input worth not trusting a second time.
            var staged = document with { ExportedAt = InventoryFormat.FormatExportedAt(DateTimeOffset.Now) };
            StageWrite(path, InventoryReader.Write(staged));

            Prune();
            Log.Info($"Staged {staged.Containers.Count} container(s) from the handheld to {name}");
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error($"Could not stage an inbound push to {path}", ex);
            return $"Could not save the incoming transfer to the inbox: {ex.Message}";
        }
    }

    /// <summary>
    /// Deletes everything beyond the newest <see cref="Retention"/> files.
    /// </summary>
    /// <remarks>
    /// Ordered by name rather than by write time. The name leads with a second-resolution
    /// timestamp and ends with the sequence, so lexical order is chronological order - and it is
    /// the same order whatever file system is underneath.
    /// </remarks>
    private void Prune()
    {
        try
        {
                // Temporaries first. A .tmp is by definition a file whose move into place never
                // happened - a crash between the two - so it is a copy of a push that never landed.
                // Leaving them is what turns an interrupted transfer into a growing pile of files
                // that look like data and are not.
                foreach (var orphan in System.IO.Directory.GetFiles(Directory, "*.tmp"))
                {
                    File.Delete(orphan);
                    Log.Debug($"Pruned the abandoned temporary {Path.GetFileName(orphan)}");
                }

                var staged = System.IO.Directory
                    .GetFiles(Directory, "push-*.json")
                    .OrderByDescending(f => f, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                for (var i = Retention; i < staged.Count; i++)
                {
                    File.Delete(staged[i]);
                    Log.Debug($"Pruned {Path.GetFileName(staged[i])} from the inbox");
                }
            }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Pruning is housekeeping, not the transfer. An inbox that stays a few files too long
            // is not worth failing a push over, and the next push will try again.
            Log.Warning($"Could not prune the inbox at {Directory}: {ex.Message}");
        }
    }

    /// <summary>
    /// Builds the next file name: timestamp first, then a sequence to break ties.
    /// </summary>
    /// <remarks>
    /// The sequence matters more than it looks. Two pushes inside the same second is ordinary -
    /// a CK65 finishing a sync the moment the last one landed - and without a tiebreak one of
    /// them silently overwrites the other.
        /// <para>
        /// The sequence is also strictly increasing and never reused within a second, which is what
        /// makes the name safe to sort by. Pruning frees the lowest sequence numbers, so a
        /// "first free slot" search hands them straight back out: the next push takes
        /// <c>-001</c> again, which sorts <em>before</em> everything already on disk, and the
        /// pruning pass then deletes the push that just arrived while keeping stale ones. The newest
        /// transfers would be the ones thrown away, silently, on the one day they matter. Counting up
        /// from the highest sequence present keeps lexical order equal to arrival order.
        /// </para>
        /// </remarks>
        private string NextFileName()
        {
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            var next = HighestSequenceFor(stamp) + 1;

            for (; next <= MaxSequence; next++)
            {
                var candidate = $"push-{stamp}-{next:D3}.json";
                if (!File.Exists(Path.Combine(Directory, candidate)))
                {
                    return candidate;
                }
            }

            // Past the sequence ceiling the width stops sorting correctly (D3 overflows to four
            // digits, which orders before three), so the name falls back to something that is unique
            // rather than something that sorts. Retention is twenty, so this is unreachable in
            // practice - it exists so the failure mode is a file with an odd name, not a collision
            // that overwrites an earlier push.
            return $"push-{stamp}-{Guid.NewGuid():N}.json";
        }

        /// <summary>
        /// Highest sequence number already on disk for <paramref name="stamp"/>, or 0 if none.
        /// </summary>
        /// <remarks>
        /// Read by scanning rather than by remembering, because the sequence has to stay monotonic
        /// across process restarts: a hub started twice in the same second would otherwise reuse the
        /// names the first run wrote. Only files carrying this second's stamp are considered, and an
        /// unparseable name is ignored rather than allowed to poison the count - pruning only ever
        /// removes files, so the count can only ever be too low, never too high.
        /// </remarks>
        private int HighestSequenceFor(string stamp)
        {
            var highest = 0;

            foreach (var path in System.IO.Directory.GetFiles(Directory, $"push-{stamp}-*.json"))
            {
                var name = Path.GetFileNameWithoutExtension(path);

                if (name.Length <= $"push-{stamp}-".Length)
                {
                    continue;
                }

                if (int.TryParse(
                        name.AsSpan($"push-{stamp}-".Length),
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out var sequence))
                {
                    highest = Math.Max(highest, sequence);
                }
            }

            return highest;
        }

    /// <summary>
    /// Writes <paramref name="path"/> via a temporary file in the same directory.
    /// </summary>
    /// <remarks>
    /// The temporary must share a volume with the target, otherwise the last step degrades from
    /// an atomic rename to a copy - which is precisely the window this exists to close.
    /// </remarks>
    private static void StageWrite(string path, string content)
    {
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, content);
        File.Move(temporary, path, overwrite: true);
    }
}