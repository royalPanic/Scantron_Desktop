using Scantron.Core.Identity;

namespace Scantron.Core.Sync;

/// <summary>
/// Message kinds on the live-sync wire.
/// </summary>
/// <remarks>
/// The set is deliberately closed and small. An unknown type is answered with
/// <see cref="Bye"/> and a reason rather than ignored, so a version mismatch surfaces as a sentence
/// on both screens instead of as a connection that mysteriously does nothing.
/// </remarks>
public enum SyncMessageType
{
    /// <summary>Device to desktop: who I am. Always the first frame.</summary>
    Hello,

    /// <summary>Device to desktop: the pairing code shown on the desktop.</summary>
    Pair,

    /// <summary>Desktop to device: accepted; carries the session id.</summary>
    Paired,

    /// <summary>Whole-document replacement, for a first sync.</summary>
    Snapshot,

    /// <summary>A batch of row changes, all relative to the shared base.</summary>
    Changes,

    /// <summary>Durable application of ops up to a sequence number.</summary>
    Ack,

    /// <summary>An open, unresolved same-field conflict.</summary>
    Conflict,

    /// <summary>An operator decision on a conflicted field.</summary>
    Resolve,

    /// <summary>Keepalive.</summary>
    Ping,

    /// <summary>Keepalive reply.</summary>
    Pong,

    /// <summary>Graceful, reasoned shutdown.</summary>
    Bye,
}

/// <summary>Identity a device announces in its <see cref="SyncMessageType.Hello"/>.</summary>
public sealed record HelloInfo(string DeviceId, string Name, int ProtocolVersion);

/// <summary>Pairing code an operator reads off the desktop and types on the handheld.</summary>
public sealed record PairInfo(string Code);

/// <summary>Desktop's acceptance of a device.</summary>
public sealed record PairedInfo(string DeviceId, string DesktopName, string SessionId);

/// <summary>
/// One unresolved conflict, as reported to the peer.
/// </summary>
/// <remarks>
/// Value strings, not typed values, mirroring <see cref="Merge.ItemConflict"/>: the conflict is a
/// presentation of "these two texts disagree", and rendering is the UI's job. Both devices show the
/// same three strings for the same conflict, so neither can be accused of inventing a value.
/// </remarks>
public sealed record ConflictInfo(
    string ConflictId,
    string ContainerId,
    string ItemUuid,
    string ItemName,
    string Field,
    string? BaseValue,
    string? LocalValue,
    string? RemoteValue);

/// <summary>
/// One frame on the live-sync wire.
/// </summary>
/// <remarks>
/// <see cref="Seq"/> is a per-device monotonic counter. It exists for <em>ordering and
/// acknowledgement on a single connection</em>, not for conflict resolution - the base document is
/// what decides who changed what. That is why an edit made while offline reconciles correctly on
/// reconnect without any cross-session clock agreement.
/// </remarks>
public sealed record SyncMessage
{
    /// <summary>Protocol version this build speaks. A mismatch is refused, not negotiated.</summary>
    public const int ProtocolVersion = 1;

    public required SyncMessageType Type { get; init; }

    /// <summary>Sender's monotonic sequence number. Zero for frames that carry no ops.</summary>
    public long Seq { get; init; }

    /// <summary>Sender's device id, so the receiver can ignore its own echo.</summary>
    public string Src { get; init; } = "";

    public HelloInfo? Hello { get; init; }

    public PairInfo? Pair { get; init; }

    public PairedInfo? Paired { get; init; }

    /// <summary>Ops carried by <see cref="SyncMessageType.Snapshot"/> or <c>Changes</c>.</summary>
    public IReadOnlyList<SyncOp> Ops { get; init; } = [];

    public ConflictInfo? Conflict { get; init; }

    /// <summary>Highest sender sequence the receiver has durably applied.</summary>
    public long AckSeq { get; init; }

    /// <summary>Human-readable reason for a <see cref="SyncMessageType.Bye"/>.</summary>
    public string? Reason { get; init; }

    /// <summary>True when this frame carries ops to apply.</summary>
    public bool CarriesOps => Ops.Count > 0;

    /// <summary>
    /// Rebuilds the merge key a <see cref="DeleteItemOp"/> identifies, so a peer can locate the
    /// identical row. Because both devices share the base, the recomputed key matches.
    /// </summary>
    public static ItemKey KeyOf(DeleteItemOp op)
    {
        ArgumentNullException.ThrowIfNull(op);
        return new ItemKey(op.KeyKind, op.KeyValue);
    }
}
