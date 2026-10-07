using Scantron.Core.Identity;
using Scantron.Core.Merge;
using Scantron.Core.Models;

namespace Scantron.Core.Sync;

/// <summary>
/// Turns "what we hold now" and "what we last agreed on" into the ops a peer still needs.
/// </summary>
/// <remarks>
/// <para>
/// This single rule does two jobs, and that is the point of the design:
/// </para>
/// <list type="number">
/// <item>
/// <description>
/// <b>It is the change detector.</b> There is no per-keystroke instrumentation, no dirty flags on
/// the view models and no change tracking in the database. A row is "changed" exactly when it
/// differs from the base, which is the same question the three-way merge already asks.
/// </description>
/// </item>
/// <item>
/// <description>
/// <b>It is the echo suppressor.</b> Applying an inbound change advances the base for the affected
/// rows, so the change that just arrived immediately stops differing from the base and is never
/// sent back. Without this, two devices each reflecting the other's change is an infinite loop -
/// the classic failure of naive bidirectional sync.
/// </description>
/// </item>
/// </list>
/// <para>
/// A row with an <em>unresolved conflict</em> is withheld from both sides of that equation: it is
/// neither emitted nor allowed to advance the base. It is the one row about which the two devices
/// have genuinely disagreed, and re-sending it would either re-report the conflict forever or
/// silently settle it in favour of whichever device synced last.
/// </para>
/// </remarks>
public static class ChangeCursors
{
    /// <summary>
    /// The ops a peer needs to reach <paramref name="current"/> from <paramref name="base"/>.
    /// </summary>
    /// <param name="base">Last state both devices agreed on, or null on a first sync.</param>
    /// <param name="current">What this device holds now.</param>
    /// <param name="blocked">
    /// Row identities frozen by an open conflict, from <see cref="ConflictRows"/>. Empty when there
    /// are no conflicts, which is the normal case.
    /// </param>
    /// <returns>
    /// An empty list when both sides already agree. A single <see cref="SnapshotOp"/> when there is
    /// no base to diff against - a first sync has nothing to be incremental about.
    /// </returns>
    public static IReadOnlyList<SyncOp> Diff(
        InventoryDocument? @base,
        InventoryDocument current,
        IReadOnlySet<string>? blocked = null)
    {
        ArgumentNullException.ThrowIfNull(current);

        // No shared history means every row would look like a change, and the peer has no base to
        // merge against either. Sending the whole document is both smaller and the only thing the
        // peer can use.
        if (@base is null)
        {
            return [new SnapshotOp(current)];
        }

        var ops = new List<SyncOp>();
        blocked ??= EmptyRowSet;

        var baseById = IndexContainers(@base);
        var currentById = IndexContainers(current);

        // Containers the base had and we no longer do. Emitted as a deletion so the peer removes
        // it; an omission would be read as "no opinion" and the container would come back.
        foreach (var id in baseById.Keys)
        {
            if (!currentById.ContainsKey(id) && !blocked.Contains(id))
            {
                ops.Add(new DeleteContainerOp(id, Now()));
            }
        }

        foreach (var container in current.Containers)
        {
            var id = container.Id.Trim();

            if (blocked.Contains(id))
            {
                continue;
            }

            baseById.TryGetValue(id, out var baseContainer);

            if (baseContainer is null || ContainerMetadataDiffers(baseContainer, container))
            {
                // Items deliberately omitted: they travel as their own ops so a removal is
                // expressible as a removal rather than as an absence.
                ops.Add(new UpsertContainerOp(container with { Items = [] }));
            }

            var baseItems = baseContainer is null
                ? new Dictionary<ItemKey, Item>()
                : ItemKeyResolver.IndexByKey(id, baseContainer.Items);
            var currentItems = ItemKeyResolver.IndexByKey(id, container.Items);

            foreach (var (key, item) in currentItems)
            {
                if (blocked.Contains(RowId(id, item)))
                {
                    continue;
                }

                if (!baseItems.TryGetValue(key, out var baseItem) || baseItem != item)
                {
                    ops.Add(new UpsertItemOp(id, item));
                }
            }

            // Rows the base had and this container no longer does.
            foreach (var (key, baseItem) in baseItems)
            {
                if (currentItems.ContainsKey(key) || blocked.Contains(RowId(id, baseItem)))
                {
                    continue;
                }

                ops.Add(new DeleteItemOp(
                    id,
                    baseItem.Uuid,
                    key.Kind,
                    key.Value,
                    baseItem.Name,
                    Now()));
            }
        }

        return ops;
    }

    /// <summary>
    /// Identities of the rows frozen by open conflicts, for <see cref="Diff"/>'s blocked set.
    /// </summary>
    /// <remarks>
    /// A conflict with an empty <c>ItemUuid</c> is a container-level disagreement (its name or
    /// location), so it blocks the container itself. Row conflicts block the row.
    /// </remarks>
    public static HashSet<string> ConflictRows(IReadOnlyList<ItemConflict> conflicts)
    {
        ArgumentNullException.ThrowIfNull(conflicts);

        var rows = new HashSet<string>(StringComparer.Ordinal);
        foreach (var conflict in conflicts)
        {
            rows.Add(string.IsNullOrWhiteSpace(conflict.ItemUuid)
                ? conflict.ContainerId.Trim()
                : $"{conflict.ContainerId.Trim()}{RowSeparator}{conflict.ItemUuid.Trim()}");
        }

        return rows;
    }

    /// <summary>Stable identity of one row, for the blocked set.</summary>
    /// <remarks>
    /// Public because the emitter on each side builds the same blocked set that
    /// <see cref="Diff"/> consumes, and the two must agree on the identity exactly.
    /// </remarks>
    public static string RowId(string containerId, Item item) =>
        $"{containerId.Trim()}{RowSeparator}{item.Uuid.Trim()}";

    /// <summary>
    /// Separator between the container and the row in a blocked-set entry.
    /// </summary>
    /// <remarks>
    /// A control character, for the same reason <see cref="ItemKeyResolver"/> uses NUL: container
    /// tags are operator-supplied free text, and a printable separator inside a tag would let two
    /// different rows collide into one blocked entry.
    /// </remarks>
    private const char RowSeparator = '\u0001';

    private static readonly HashSet<string> EmptyRowSet = [];

    /// <summary>
    /// Compares only the fields a container owns. <see cref="Container"/> is a record, so its
    /// generated equality includes <c>Items</c>, and comparing records directly would report every
    /// container as changed the moment one of its rows did.
    /// </summary>
    private static bool ContainerMetadataDiffers(Container left, Container right) =>
        !string.Equals(left.Name, right.Name, StringComparison.Ordinal)
        || !string.Equals(left.Location, right.Location, StringComparison.Ordinal)
        || !string.Equals(left.Notes, right.Notes, StringComparison.Ordinal);

    private static Dictionary<string, Container> IndexContainers(InventoryDocument document)
    {
        var index = new Dictionary<string, Container>(StringComparer.Ordinal);
        foreach (var container in document.Containers)
        {
            var id = container.Id.Trim();
            if (id.Length > 0)
            {
                index[id] = container;
            }
        }

        return index;
    }

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}
