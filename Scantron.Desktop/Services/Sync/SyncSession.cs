using Scantron.Core.Models;
using Scantron.Core.Sync;

namespace Scantron.Desktop.Services.Sync;

/// <summary>What a peer connection is doing, for the status line.</summary>
public enum SyncConnectionState
{
    /// <summary>No peer connected.</summary>
    Idle,

    /// <summary>Connected, waiting for the peer to identify itself.</summary>
    Greeting,

    /// <summary>A code has been offered and refused, or none has been offered yet.</summary>
    AwaitingPair,

    /// <summary>Paired and exchanging changes.</summary>
    Syncing,

    /// <summary>Paired, connected and agreed.</summary>
    InSync,

    /// <summary>Paired, but not connected right now.</summary>
    Offline,
}

/// <summary>
/// One paired connection's protocol state machine.
/// </summary>
/// <remarks>
/// <para>
/// Testable without a socket: it consumes and produces <see cref="SyncMessage"/> values, so every
/// branch that matters - a wrong pairing code, a version mismatch, an unknown message, a first sync
/// against an established one - is exercised directly rather than through a network.
/// </para>
/// <para>
/// The session owns no UI and no view model. It raises <see cref="Applied"/> with documents, and
/// whoever is hosting it decides when and where to put them. That is what lets the desktop marshal
/// to the dispatcher and a test run inline on the calling thread.
/// </para>
/// </remarks>
public sealed class SyncSession
{
    private readonly PairedPeerStore _paired;
    private readonly string _deviceName;

    private HelloInfo? _peer;
    private bool _pairedThisConnection;
    private long _lastPeerSeq;
    private long _lastAckSent;

    public SyncSession(PairedPeerStore paired, string deviceName)
    {
        _paired = paired;
        _deviceName = deviceName;
    }

    /// <summary>Identity of the connected peer, once it has greeted.</summary>
    public HelloInfo? Peer => _peer;

    /// <summary>True once the pairing code has been accepted on this connection.</summary>
    public bool IsPaired => _pairedThisConnection;

    /// <summary>
    /// Raised with the documents a batch of peer ops produced, on the calling thread.
    /// </summary>
    /// <remarks>
    /// The receiver is handed <em>both</em> the new document and the new base, because they are not
    /// the same thing and only the base may be persisted as the agreed snapshot. The document is
    /// what the operator sees; the base is what the next round diffs against. Confusing the two is
    /// how a conflict gets silently settled.
    /// </remarks>
    public event EventHandler<SyncApplyResult>? Applied;

    /// <summary>
    /// Handles a message from the peer.
    /// </summary>
    /// <param name="json">Raw frame text.</param>
    /// <param name="local">What this desktop currently holds.</param>
    /// <param name="baseDocument">Last agreed snapshot, or null on a first sync.</param>
    /// <param name="now">Injected clock, for deterministic tests.</param>
    /// <returns>Messages to send back, in order. Empty when nothing is owed.</returns>
    public IReadOnlyList<SyncMessage> Receive(
        string json,
        InventoryDocument? local,
        InventoryDocument? baseDocument,
        DateTimeOffset now)
    {
        if (!SyncWire.TryRead(json, out var message, out var error))
        {
            return [Bye(error ?? "The desktop could not read that frame.")];
        }

        switch (message.Type)
        {
            case SyncMessageType.Hello:
                return OnHello(message);

            case SyncMessageType.Pair:
                return OnPair(message, local, baseDocument, now);

            case SyncMessageType.Changes or SyncMessageType.Snapshot:
                return OnChanges(message, local, baseDocument, now);

            case SyncMessageType.Ping:
                return [new SyncMessage { Type = SyncMessageType.Pong, Src = _deviceName }];

            case SyncMessageType.Pong:
            case SyncMessageType.Ack:
                return [];

            case SyncMessageType.Bye:
                return [];

            default:
                // Anything a desktop would only ever *send* arriving from the peer is a protocol
                // error worth naming rather than ignoring.
                return [Bye($"The handheld sent a message a device should not send (\"{SyncWire.Name(message.Type)}\").")];
        }
    }

    /// <summary>The messages that open a fresh connection to an already-paired handheld.</summary>
    public IReadOnlyList<SyncMessage> Greet() =>
        [new SyncMessage { Type = SyncMessageType.Ping, Src = _deviceName }];

    /// <summary>
    /// Builds the catch-up the desktop owes the peer, once it is paired.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A desktop that already shares a base with the peer sends only what differs from it. A
    /// desktop meeting the device for the first time sends a snapshot instead, because there is
    /// nothing to be incremental against.
    /// </para>
    /// <para>
    /// A desktop with <em>no document loaded at all</em> sends nothing, and that is a deliberate
    /// safety rule rather than an optimisation. An empty document is not a harmless "I have
    /// nothing"; on the handheld a snapshot replaces its whole database, so an empty one is an
    /// instruction to delete the day's scanning. The manual <c>/pull</c> route refuses outright in
    /// this situation for exactly this reason, and live sync has to refuse in the same place.
    /// Null is the "nothing loaded" signal, distinct from a real empty document.
    /// </para>
    /// </remarks>
    public SyncMessage CatchUp(InventoryDocument? local, InventoryDocument? baseDocument, long seq)
    {
        if (local is null)
        {
            Log.Info("Nothing is loaded, so no catch-up snapshot was sent - an empty document would clear the handheld.");
            return new SyncMessage { Type = SyncMessageType.Changes, Seq = seq, Src = _deviceName };
        }

        var ops = ChangeCursors.Diff(baseDocument, local);

        return new SyncMessage
        {
            Type = SyncMessageType.Changes,
            Seq = seq,
            Src = _deviceName,
            Ops = ops,
        };
    }

    private IReadOnlyList<SyncMessage> OnHello(SyncMessage message)
    {
        var hello = message.Hello;
        if (hello is null)
        {
            return [Bye("The handheld sent a greeting with no identity.")];
        }

        if (hello.ProtocolVersion != SyncMessage.ProtocolVersion)
        {
            // Refused rather than negotiated. A live sync that half-works because the two sides
            // disagreed about one field is far harder to diagnose than one that declines to start.
            return [Bye(
                $"This desktop speaks live sync version {SyncMessage.ProtocolVersion}, and the handheld " +
                $"speaks version {hello.ProtocolVersion}. Update the app on both devices.")];
        }

        _peer = hello;

        // A device the desktop already paired with skips the code. Re-pairing every morning would
        // make the feature unusable, and the code's job - admitting a *new* device - is done.
        if (_paired.Peer.IsPaired &&
            string.Equals(_paired.Peer.DeviceId, hello.DeviceId, StringComparison.Ordinal))
        {
            _pairedThisConnection = true;

            return
            [
                new SyncMessage
                {
                    Type = SyncMessageType.Paired,
                    Src = _deviceName,
                    Paired = new PairedInfo(_paired.Peer.DeviceId, _deviceName, SessionId()),
                },
            ];
        }

        return [];
    }

    private IReadOnlyList<SyncMessage> OnPair(
        SyncMessage message,
        InventoryDocument? local,
        InventoryDocument? baseDocument,
        DateTimeOffset now)
    {
        if (_peer is null)
        {
            return [Bye("The handheld tried to pair before identifying itself.")];
        }

        // A different device while one is already paired is refused, not swapped. Silently replacing
        // the paired unit would leave the previous device believing it was still in sync.
        if (_paired.Peer.IsPaired &&
            !string.Equals(_paired.Peer.DeviceId, _peer.DeviceId, StringComparison.Ordinal))
        {
            return [Bye(
                $"This desktop is already paired with {_paired.Peer.Name}. " +
                "Unpair it first if this is now the handheld to use.")];
        }

        if (!_paired.Accepts(message.Pair?.Code))
        {
            return [Bye("That pairing code is not correct. Read the six digits off the desktop's screen.")];
        }

        _pairedThisConnection = true;
        _paired.Pair(_peer.DeviceId, _peer.Name, null);

        return
        [
            new SyncMessage
            {
                Type = SyncMessageType.Paired,
                Src = _deviceName,
                Paired = new PairedInfo(_peer.DeviceId, _deviceName, SessionId()),
            },
        ];
    }

    private IReadOnlyList<SyncMessage> OnChanges(
        SyncMessage message,
        InventoryDocument? local,
        InventoryDocument? baseDocument,
        DateTimeOffset now)
    {
        if (!_pairedThisConnection)
        {
            return [Bye("The handheld sent changes before pairing.")];
        }

        if (!message.CarriesOps)
        {
            return [];
        }

        // Replay guard. A reconnect may resend frames the desktop already applied, and while
        // applying them again is harmless to the document, advancing the base twice on a *conflict*
        // would not be - so the sequence number is checked before anything is applied.
        if (message.Seq > 0 && message.Seq <= _lastPeerSeq)
        {
            return [new SyncMessage { Type = SyncMessageType.Ack, Src = _deviceName, AckSeq = _lastPeerSeq }];
        }

        SyncApplyResult result;
        try
        {
            result = SyncApply.Apply(baseDocument, local ?? SyncDocuments.Empty, message.Ops, now);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            // A bad batch must not take the connection - or the app - down with it.
            Log.Error("Could not apply a batch of live-sync changes", ex);
            return [Bye("The desktop could not apply that change. The handheld should send a full snapshot.")];
        }

        Applied?.Invoke(this, result);

        if (message.Seq > 0)
        {
            _lastPeerSeq = message.Seq;
        }

        var replies = new List<SyncMessage>
        {
            new() { Type = SyncMessageType.Ack, Src = _deviceName, AckSeq = _lastPeerSeq },
        };

        _lastAckSent = _lastPeerSeq;

        // A conflict is reported to the peer so both screens show the same list. It is not a
        // refusal: the change *was* applied everywhere it safely could be, and only the disputed
        // field is held back.
        foreach (var conflict in result.Conflicts)
        {
            replies.Add(new SyncMessage
            {
                Type = SyncMessageType.Conflict,
                Src = _deviceName,
                Conflict = new ConflictInfo(
                    ConflictId(conflict),
                    conflict.ContainerId,
                    conflict.ItemUuid,
                    conflict.ItemName,
                    conflict.Field,
                    conflict.BaseValue,
                    conflict.LocalValue,
                    conflict.RemoteValue),
            });
        }

        return replies;
    }

    /// <summary>Stable identity of a conflict, shared with the handheld so both can match a decision.</summary>
    public static string ConflictId(Scantron.Core.Merge.ItemConflict conflict)
    {
        ArgumentNullException.ThrowIfNull(conflict);
        return $"{conflict.ContainerId.Trim()}/{conflict.ItemUuid.Trim()}/{conflict.Field}";
    }

    private SyncMessage Bye(string reason) =>
        new() { Type = SyncMessageType.Bye, Src = _deviceName, Reason = reason };

    private static string SessionId() => Guid.NewGuid().ToString("N")[..12];
}
