using Scantron.Core.Identity;
using Scantron.Core.Models;

namespace Scantron.Core.Sync;

/// <summary>
/// Discriminator for <see cref="SyncOp"/>.
/// </summary>
/// <remarks>
/// A string is written on the wire rather than the enum's numeric value, so inserting a kind
/// later cannot silently reinterpret an existing one as something else.
/// </remarks>
public enum SyncOpKind
{
    /// <summary>Whole-document replacement. The first-sync / no-usable-base case.</summary>
    Snapshot,

    /// <summary>Container metadata, without its items.</summary>
    UpsertContainer,

    /// <summary>One item row, whole, identified by <see cref="ItemKeyResolver"/>.</summary>
    UpsertItem,

    /// <summary>A container that no longer exists.</summary>
    DeleteContainer,

    /// <summary>An item row that no longer exists.</summary>
    DeleteItem,

    /// <summary>An operator's decision on one conflicted field.</summary>
    Resolve,
}

/// <summary>
/// One unit of change on the live-sync wire.
/// </summary>
/// <remarks>
/// <para>
/// Granularity is deliberately <em>whole rows</em>, not field diffs. The field-level intelligence
/// already lives in <see cref="Merge.InventoryMerger"/>, which is where it is tested; duplicating
/// it into the transport would be a second place for the two devices to disagree. A row is
/// therefore sent whole, and the receiver lets the existing three-way merge decide what actually
/// changed.
/// </para>
/// <para>
/// Every op that is not a <see cref="SnapshotOp"/> is expressed <em>relative to the shared base</em>
/// - the last state both devices agreed on. That is what makes an op idempotent: replaying it
/// rebuilds the same "remote" view of the row, and the base advances only once.
/// </para>
/// </remarks>
public abstract record SyncOp
{
    /// <summary>Which op this is, for the wire codec and for tests.</summary>
    public abstract SyncOpKind Kind { get; }
}

/// <summary>Replaces the peer's entire view. Sent only when no shared base exists.</summary>
/// <remarks>
/// Carries a full export document, so it goes through exactly the same reader and validator as a
/// USB import. A live sync and a file transfer remain interchangeable by construction.
/// </remarks>
public sealed record SnapshotOp(InventoryDocument Document) : SyncOp
{
    public override SyncOpKind Kind => SyncOpKind.Snapshot;
}

/// <summary>
/// Container metadata changed. <see cref="Container.Items"/> is intentionally empty: rows travel as
/// their own ops so that a deletion can be expressed as a deletion rather than as an omission.
/// </summary>
/// <remarks>
/// Omission cannot carry a delete. <see cref="Merge.InventoryMerger"/> treats a row missing from one
/// side as "no opinion" and carries it forward, so a container upsert that merely listed fewer items
/// would resurrect them on the next sync. Only an explicit <see cref="DeleteItemOp"/> removes a row.
/// </remarks>
public sealed record UpsertContainerOp(Container Container) : SyncOp
{
    public override SyncOpKind Kind => SyncOpKind.UpsertContainer;
}

/// <summary>One item row, whole, as it now stands.</summary>
/// <param name="ContainerId">Owning container tag, trimmed.</param>
/// <param name="Item">The row. Its <see cref="Item.Uuid"/> is its merge identity.</param>
public sealed record UpsertItemOp(string ContainerId, Item Item) : SyncOp
{
    public override SyncOpKind Kind => SyncOpKind.UpsertItem;
}

/// <summary>A container that has been removed.</summary>
/// <remarks>Its rows go with it; no per-item tombstones are emitted for a container delete.</remarks>
public sealed record DeleteContainerOp(string ContainerId, long At) : SyncOp
{
    public override SyncOpKind Kind => SyncOpKind.DeleteContainer;
}

/// <summary>
/// An item row that has been removed.
/// </summary>
/// <remarks>
/// <para>
/// Carries the merge <em>key</em> as well as the uuid. The key is what <see cref="ItemKeyResolver"/>
/// produced for the row on the emitting side, and because both devices share the same base the
/// receiver recomputes the identical key against its own copy. That makes the delete resolvable even
/// for a legacy 1.0 row that has no uuid - the one case where the uuid alone is not enough.
/// </para>
/// <para>
/// <paramref name="ItemName"/> is not used to locate anything; it exists so a conflict or a log line
/// can name the row to an operator instead of showing a uuid.
/// </para>
/// </remarks>
public sealed record DeleteItemOp(
    string ContainerId,
    string ItemUuid,
    ItemKeyKind KeyKind,
    string KeyValue,
    string ItemName,
    long At) : SyncOp
{
    public override SyncOpKind Kind => SyncOpKind.DeleteItem;
}

/// <summary>An operator's decision on one conflicted field.</summary>
/// <remarks>
/// Sent by whichever device the human was standing at. Applying it sets the field and does not
/// conflict, because it is not a simultaneous edit - it is the resolution of one - so the field's
/// base advances and the conflict clears on both sides.
/// </remarks>
/// <param name="ContainerId">Owning container tag.</param>
/// <param name="ItemUuid">Row identity; empty for a container-level conflict.</param>
/// <param name="Field">"Name", "Quantity", "Location", "Presence", and so on.</param>
/// <param name="Value">The chosen value, rendered as text.</param>
public sealed record ResolveOp(
    string ContainerId,
    string ItemUuid,
    string Field,
    string? Value,
    long At) : SyncOp
{
    public override SyncOpKind Kind => SyncOpKind.Resolve;
}
