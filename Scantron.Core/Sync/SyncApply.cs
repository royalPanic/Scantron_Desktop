using Scantron.Core.Identity;
using Scantron.Core.Merge;
using Scantron.Core.Models;

namespace Scantron.Core.Sync;

/// <summary>
/// The outcome of applying a peer's ops to the shared base.
/// </summary>
/// <param name="Document">What this device should now show. Converged where it can be.</param>
/// <param name="Base">
/// The new shared base. Advanced for every field the two devices agree on, and deliberately
/// <em>not</em> advanced for a conflicted field. This is the value that must be persisted as the
/// base after the batch is durably applied - a base advanced too far is what lets a real conflict
/// be silently overwritten on the next round.
/// </param>
/// <param name="Conflicts">Same-field disagreements needing a human, whose base entry did not move.</param>
/// <param name="ClockSkewDetected">Passed through from <see cref="InventoryMerger"/>.</param>
public sealed record SyncApplyResult(
    InventoryDocument Document,
    InventoryDocument Base,
    IReadOnlyList<ItemConflict> Conflicts,
    bool ClockSkewDetected);

/// <summary>
/// Applies a peer's ops to the shared base, then converges the two devices through the one merge
/// implementation the project already has.
/// </summary>
/// <remarks>
/// <para>
/// The receiver does not try to be clever about the wire. It first reconstructs what the peer now
/// holds - <em>base + ops</em> - and then asks
/// <see cref="InventoryMerger.Merge"/> the same three-way question it would ask for a USB import:
/// given what we agreed on, what did each side change? Keeping that comparison in one place is what
/// makes a live sync and a file transfer behave identically, down to the conflict rules.
/// </para>
/// <para>
/// The only live-sync-specific rule is how the base advances: for a field both sides changed to
/// different values, the base <em>stays put</em>. Advancing it would declare one side the winner,
/// which is exactly the silent data loss the whole design exists to prevent, and it would also make
/// the conflict unreproducible on the next round. Keeping the base at the last agreed value is what
/// makes a conflict durable until a human settles it.
/// </para>
/// <para>
/// A replay is harmless. Because the base is only advanced for what both sides agreed on, applying
/// the identical batch twice produces the same result the second time and reports no new change -
/// which is what makes reconnect-and-resend safe without any de-duplication bookkeeping.
/// </para>
/// </remarks>
public static class SyncApply
{
    /// <summary>Applies <paramref name="ops"/> and converges against <paramref name="local"/>.</summary>
    /// <param name="base">Last agreed state, or null when there is no shared history yet.</param>
    /// <param name="local">What this device holds now.</param>
    /// <param name="ops">Ops from the peer, all relative to <paramref name="base"/>.</param>
    /// <param name="now">Local clock, injected for deterministic tests.</param>
    public static SyncApplyResult Apply(
        InventoryDocument? @base,
        InventoryDocument local,
        IReadOnlyList<SyncOp> ops,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(local);
        ArgumentNullException.ThrowIfNull(ops);

        // A resolution is not an edit, it is a verdict: "this field is now this value, stop
        // arguing about it". Feeding one through conflict detection would make the decision look
        // like a fresh simultaneous edit and re-open the very conflict it settles, so resolutions
        // are lifted out and applied directly to both the document and the base.
        var resolutions = ops.OfType<ResolveOp>().ToList();
        var changes = ops.Where(op => op is not ResolveOp).ToList();

        var remote = Reconstruct(@base, changes);

        var merged = InventoryMerger.Merge(@base, local, remote, now);

        // The displayed document is exactly what the merge produced: each side keeps seeing its own
        // value for a field it is right about, which avoids an operator's edit appearing to vanish
        // the moment a peer's change lands. Convergence is achieved through the base below.
        var document = merged.Document;
        var nextBase = RevertConflictedFields(merged.Document, @base, merged.Conflicts);

        // An explicit delete is an instruction, not an omission, and the merger cannot express one:
        // it reads a row missing from one side as "no opinion" and carries it forward. That is right
        // for a partial export, but it means a tombstone would be resurrected on every sync and
        // reported as a "Presence" conflict the operator has no way to settle - the row is simply
        // back. Deletes are therefore applied here, authoritatively, to both the document and the
        // base, which is what lets a deletion converge instead of oscillating.
        var deletes = changes.Where(op => op is DeleteItemOp or DeleteContainerOp).ToList();
        foreach (var delete in deletes)
        {
            document = ApplyDelete(document, delete);
            nextBase = ApplyDelete(nextBase, delete);
        }

        foreach (var resolution in resolutions)
        {
            document = ApplyResolution(document, resolution);
            nextBase = ApplyResolution(nextBase, resolution);
        }

        // A conflict that a resolution in this same batch has answered is settled, not open. Leaving
        // it in the list is what would make a resolved conflict reappear on every subsequent sync.
        // A "Presence" conflict answered by a delete in the same batch is settled for the same
        // reason: both devices now agree the row is gone, so there is nothing left to decide.
        var conflicts = merged.Conflicts
            .Where(c => !IsSettledBy(c, resolutions) && !IsSettledByDelete(c, deletes))
            .ToList();

        return new SyncApplyResult(document, nextBase, conflicts, merged.ClockSkewDetected);
    }

    /// <summary>Applies one tombstone to a document.</summary>
    private static InventoryDocument ApplyDelete(InventoryDocument document, SyncOp op) => op switch
    {
        DeleteContainerOp delete => SyncDocuments.RemoveContainer(document, delete.ContainerId),

        DeleteItemOp delete => SyncDocuments.RemoveItem(
            document,
            delete.ContainerId,
            SyncMessage.KeyOf(delete)),

        _ => document,
    };

    /// <summary>
    /// True when a delete in this batch removes the row a "Presence" conflict is about.
    /// </summary>
    /// <remarks>
    /// Only "Presence" can be settled this way. A delete says nothing about a field's value, so a
    /// conflict on "Quantity" or "Name" must still be decided by a human even if the row is going
    /// away - the operator may be deleting the wrong row, and silently dropping the disagreement
    /// would hide that.
    /// </remarks>
    private static bool IsSettledByDelete(ItemConflict conflict, List<SyncOp> deletes)
    {
        if (!string.Equals(conflict.Field, "Presence", StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var delete in deletes)
        {
            switch (delete)
            {
                case DeleteContainerOp container
                    when string.Equals(container.ContainerId.Trim(), conflict.ContainerId.Trim(), StringComparison.Ordinal):
                    return true;

                case DeleteItemOp row
                    when string.Equals(row.ContainerId.Trim(), conflict.ContainerId.Trim(), StringComparison.Ordinal)
                        && (string.Equals(row.ItemUuid.Trim(), conflict.ItemUuid.Trim(), StringComparison.Ordinal)
                            || string.Equals(row.KeyValue.Trim(), conflict.ItemUuid.Trim(), StringComparison.Ordinal)):
                    return true;
            }
        }

        return false;
    }

    /// <summary>True when one of <paramref name="resolutions"/> answers <paramref name="conflict"/>.</summary>
    private static bool IsSettledBy(ItemConflict conflict, List<ResolveOp> resolutions)
    {
        foreach (var resolution in resolutions)
        {
            if (string.Equals(resolution.ContainerId.Trim(), conflict.ContainerId.Trim(), StringComparison.Ordinal) &&
                string.Equals(resolution.ItemUuid.Trim(), conflict.ItemUuid.Trim(), StringComparison.Ordinal) &&
                string.Equals(resolution.Field, conflict.Field, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Rebuilds the peer's document from the shared base plus its ops.
    /// </summary>
    /// <remarks>
    /// Exposed because this is the one step where a bug is invisible: a wrong reconstruction looks
    /// like a legitimate merge outcome. Tests pin it directly rather than inferring it from a merge.
    /// </remarks>
    public static InventoryDocument Reconstruct(InventoryDocument? @base, IReadOnlyList<SyncOp> ops)
    {
        ArgumentNullException.ThrowIfNull(ops);

        var document = @base ?? SyncDocuments.Empty;

        foreach (var op in ops)
        {
            document = op switch
            {
                SnapshotOp snapshot => snapshot.Document,

                // A container upsert deliberately carries no items, so only its metadata replaces
                // anything. Applying it must not drop the rows the base already holds.
                UpsertContainerOp upsert => SyncDocuments.UpsertContainer(
                    document,
                    upsert.Container with
                    {
                        Items = ExistingItems(document, upsert.Container.Id),
                    }),

                UpsertItemOp row => SyncDocuments.UpsertItem(document, row.ContainerId, row.Item),

                DeleteContainerOp delete => SyncDocuments.RemoveContainer(document, delete.ContainerId),

                DeleteItemOp delete => SyncDocuments.RemoveItem(
                    document,
                    delete.ContainerId,
                    SyncMessage.KeyOf(delete)),

                ResolveOp resolve => ApplyResolution(document, resolve),

                _ => document,
            };
        }

        return document;
    }

    /// <summary>
    /// Applies an operator's decision. A decision is not a simultaneous edit, so it simply sets the
    /// field - there is nothing to conflict with.
    /// </summary>
    /// <remarks>
    /// The row is found by uuid rather than by the merge key, because the conflict the operator
    /// answered was already resolved to a specific row on both devices when it was reported.
    /// </remarks>
    private static InventoryDocument ApplyResolution(InventoryDocument document, ResolveOp resolve)
    {
        if (string.IsNullOrWhiteSpace(resolve.ItemUuid))
        {
            return SyncDocuments.SetContainerField(
                document, resolve.ContainerId, resolve.Field, resolve.Value);
        }

        var container = document.Containers.FirstOrDefault(
            c => string.Equals(c.Id.Trim(), resolve.ContainerId.Trim(), StringComparison.Ordinal));

        var row = container?.Items.FirstOrDefault(
            i => string.Equals(i.Uuid.Trim(), resolve.ItemUuid.Trim(), StringComparison.Ordinal));

        if (row is null)
        {
            return document;
        }

        return SyncDocuments.SetItemField(
            document,
            resolve.ContainerId,
            Scantron.Core.Identity.ItemKeyResolver.Resolve(resolve.ContainerId, row),
            resolve.Field,
            resolve.Value);
    }

    /// <summary>
    /// Walks a document back to the last agreed value for every conflicted field, giving the new
    /// base. Non-conflicted fields keep the merged value, so they advance normally.
    /// </summary>
    private static InventoryDocument RevertConflictedFields(
        InventoryDocument document,
        InventoryDocument? @base,
        IReadOnlyList<ItemConflict> conflicts)
    {
        if (conflicts.Count == 0)
        {
            return document;
        }

        var reverted = document;

        foreach (var conflict in conflicts)
        {
            // "Presence" is not a field, it is the row's existence. The merger already carries the
            // row forward rather than dropping it, so the last agreed answer is "present" and there
            // is nothing to walk back.
            if (string.Equals(conflict.Field, "Presence", StringComparison.Ordinal))
            {
                continue;
            }

            // The base's own value is the last thing both devices agreed on. It is taken from the
            // supplied base rather than from the conflict record so that a conflict reported by the
            // peer and one discovered locally resolve to the same text.
            var agreed = BaseValueOf(@base, conflict);

            reverted = string.IsNullOrWhiteSpace(conflict.ItemUuid)
                ? SyncDocuments.SetContainerField(reverted, conflict.ContainerId, conflict.Field, agreed)
                : RevertRow(reverted, conflict, agreed);
        }

        return reverted;
    }

    private static InventoryDocument RevertRow(
        InventoryDocument document,
        ItemConflict conflict,
        string? agreed)
    {
        var container = document.Containers.FirstOrDefault(
            c => string.Equals(c.Id.Trim(), conflict.ContainerId.Trim(), StringComparison.Ordinal));

        // A row conflict is keyed by uuid on both sides, so the identity is unambiguous here.
        var row = container?.Items.FirstOrDefault(
            i => string.Equals(i.Uuid.Trim(), conflict.ItemUuid.Trim(), StringComparison.Ordinal));

        if (row is null)
        {
            return document;
        }

        return SyncDocuments.SetItemField(
            document,
            conflict.ContainerId,
            Scantron.Core.Identity.ItemKeyResolver.Resolve(conflict.ContainerId, row),
            conflict.Field,
            agreed);
    }

    /// <summary>
    /// The last agreed value of a conflicted field, or null when the base never held the row.
    /// </summary>
    /// <remarks>
    /// A null here means the field is absent from the base, and writing that back is correct: it
    /// restores "we never agreed on this", which is precisely why it conflicted.
    /// </remarks>
    private static string? BaseValueOf(InventoryDocument? @base, ItemConflict conflict)
    {
        if (@base is null)
        {
            return null;
        }

        var container = @base.Containers.FirstOrDefault(
            c => string.Equals(c.Id.Trim(), conflict.ContainerId.Trim(), StringComparison.Ordinal));

        if (container is null)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(conflict.ItemUuid))
        {
            return conflict.Field switch
            {
                "Name" => container.Name,
                "Location" => container.Location,
                "Notes" => container.Notes,
                _ => null,
            };
        }

        var row = container.Items.FirstOrDefault(
            i => string.Equals(i.Uuid.Trim(), conflict.ItemUuid.Trim(), StringComparison.Ordinal));

        if (row is null)
        {
            return null;
        }

        return conflict.Field switch
        {
            "Name" => row.Name,
            "Barcode" => row.Barcode,
            "Quantity" => row.Quantity.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "Category" => row.Category,
            "Notes" => row.Notes,
            _ => null,
        };
    }

    /// <summary>The rows a document already holds for a container, or empty when it has none.</summary>
    private static IReadOnlyList<Item> ExistingItems(InventoryDocument document, string containerId)
    {
        var id = containerId.Trim();
        return document.Containers
            .FirstOrDefault(c => string.Equals(c.Id.Trim(), id, StringComparison.Ordinal))
            ?.Items ?? [];
    }
}
