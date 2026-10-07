using System.Net;
using System.Net.Sockets;
using Scantron.Core.Models;
using Scantron.Core.Sync;

namespace Scantron.Desktop.Services.Sync;

/// <summary>
/// Owns the live-sync connection: one peer at a time, over sockets the hub has already upgraded.
/// </summary>
/// <remarks>
/// <para>
/// Sits between <see cref="SyncSession"/>, which decides what messages mean, and the view model,
/// which owns the documents. It is the only place that knows about both a socket and the UI thread,
/// so it is the only place that has to get the crossing right.
/// </para>
/// <para>
/// Exactly one connection is live at a time. That is what makes the whole design tractable: with a
/// single peer there is one writer on the far side, so two devices cannot be racing on the same row
/// and the merge stays the question it is good at answering rather than a coordination problem.
/// A second connection is refused with a sentence rather than queued.
/// </para>
/// </remarks>
public sealed class SyncCoordinator : IAsyncDisposable
{
    private readonly PairedPeerStore _paired;
    private readonly string _deviceName;
    private readonly object _gate = new();

    private WebSocketConnection? _connection;
    private bool _caughtUp;
    private bool _pairedThisConnection;

    public SyncCoordinator(PairedPeerStore paired, string? deviceName = null)
    {
        ArgumentNullException.ThrowIfNull(paired);
        _paired = paired;
        _deviceName = string.IsNullOrWhiteSpace(deviceName) ? Environment.MachineName : deviceName;
        State = _paired.Peer.IsPaired ? SyncConnectionState.Offline : SyncConnectionState.Idle;
    }

    /// <summary>Supplies the document the operator is working on. Called on the UI thread.</summary>
    public Func<InventoryDocument?> Document { get; set; } = () => null;

    /// <summary>Supplies the last agreed snapshot. Called on the UI thread.</summary>
    public Func<InventoryDocument?> Base { get; set; } = () => null;

    /// <summary>
    /// Applies a batch to the view model and persists the new base. Runs on the UI thread.
    /// </summary>
    /// <remarks>
    /// Invoked from inside the marshalled receive, before the acknowledgement is written, because
    /// an ack means "I have durably applied this". The work is synchronous - it rebuilds documents
    /// and stages the workspace file - so by the time the session returns, the base on disk is the
    /// base the ack refers to.
    /// </remarks>
    public Action<SyncApplyResult>? Applied { get; set; }

    /// <summary>Allocates the next outgoing sequence number. Called on the UI thread.</summary>
    public Func<long> NextSeq { get; set; } = () => 0;

    /// <summary>What the connection is doing, for the status line.</summary>
    public SyncConnectionState State { get; private set; }

    /// <summary>Name of the paired handheld, for the status line. Empty when nothing is paired.</summary>
    public string PeerName => _paired.Peer.Name;

    /// <summary>The pairing code the operator reads off this screen.</summary>
    public string PairingCode => _paired.Code;

    public event EventHandler<SyncConnectionState>? StateChanged;

    /// <summary>Forgets the paired handheld.</summary>
    public void Unpair()
    {
        _paired.Unpair();
        SetState(SyncConnectionState.Idle);
    }

    /// <summary>
    /// Serves one upgraded socket until the peer goes away.
    /// </summary>
    /// <remarks>
    /// Called by the hub for every accepted <c>/sync</c> connection. Returns rather than throws on
    /// every ordinary failure - a handheld walking out of range is normal in a warehouse - so one
    /// dropped scanner cannot take live sync down with it.
    /// </remarks>
    public async Task ServeAsync(NetworkStream stream, IPEndPoint? remote, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(stream);

        WebSocketConnection connection;
        lock (_gate)
        {
            if (_connection is not null)
            {
                // Refused rather than queued: a second peer would mean two writers, and the single
                // -peer rule is the thing that keeps the merge simple.
                Log.Warning($"Refused a second live-sync connection from {remote}");
                return;
            }

            connection = new WebSocketConnection(stream);
            _connection = connection;
            _caughtUp = false;
            _pairedThisConnection = false;
        }

        var session = new SyncSession(_paired, _deviceName);
        PeerAddress = remote?.ToString();

        // The session decides what a message means; this is where its verdict reaches the view
        // model. The handler is not invoked here but from inside the marshalled receive below, so
        // the document work always runs on the UI thread.
        session.Applied += (_, result) => Applied?.Invoke(result);

        // The session parks on a socket read, and cancelling a read token does not reliably
        // interrupt a pending socket read on every platform. Disposing the connection is what
        // guarantees the loop wakes and the hub's Stop can finish instead of waiting on a peer
        // that is still happily connected.
        using var registration = token.Register(() => connection.Dispose());

        // The session's apply runs through the coordinator so it crosses to the UI thread and the
        // acknowledgement is not written until the base has actually been persisted.
        SetState(SyncConnectionState.Greeting);
        Log.Info($"Live-sync connection from {remote}");

        try
        {
            using (connection)
            {
                while (!token.IsCancellationRequested)
                {
                    var frame = await connection.ReceiveAsync(token).ConfigureAwait(false);
                    if (frame is null)
                    {
                        break;
                    }

                    var replies = await OnUiAsync(
                        () => session.Receive(frame, Document(), Base(), DateTimeOffset.Now),
                        token).ConfigureAwait(false);

                    foreach (var reply in replies)
                    {
                        await connection.SendAsync(SyncWire.Write(reply), token).ConfigureAwait(false);

                        if (reply.Type == SyncMessageType.Bye)
                        {
                            Log.Warning($"Live sync closed: {reply.Reason}");
                            return;
                        }
                    }

                    if (session.IsPaired)
                    {
                        _pairedThisConnection = true;
                        SetState(SyncConnectionState.Syncing);
                        await SendCatchUpAsync(session, connection, token).ConfigureAwait(false);
                        SetState(SyncConnectionState.InSync);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        {
            // Ordinary: the handheld went out of range, was closed, or the app was killed.
            Log.Info($"Live-sync connection from {remote} ended: {ex.Message}");
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_connection, connection))
                {
                    _connection = null;
                }
            }

            SetState(_paired.Peer.IsPaired ? SyncConnectionState.Offline : SyncConnectionState.Idle);
        }
    }

    /// <summary>
    /// Sends the desktop's own outstanding changes, once per connection.
    /// </summary>
    /// <remarks>
    /// This is what makes an edit made while the handheld was away arrive when it comes back: the
    /// diff is taken against the base, so whatever the operator changed in the meantime is still
    /// "different from the base" and is sent now.
    /// </remarks>
    private async Task SendCatchUpAsync(
        SyncSession session,
        WebSocketConnection connection,
        CancellationToken token)
    {
        bool needed;
        lock (_gate)
        {
            needed = !_caughtUp;
            _caughtUp = true;
        }

        if (!needed)
        {
            return;
        }

        var message = await OnUiAsync(
            () => session.CatchUp(Document(), Base(), NextSeq()),
            token).ConfigureAwait(false);

        if (message.CarriesOps)
        {
            await connection.SendAsync(SyncWire.Write(message), token).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Publishes this desktop's changes to the paired handheld.
    /// </summary>
    /// <remarks>
    /// A no-op when nothing is connected. That is deliberate and is why offline edits work: the
    /// change is not lost, it just stays "different from the base" and goes out on the next
    /// catch-up. There is no queue to get out of step, because the base <em>is</em> the queue.
    /// </remarks>
    public async Task PublishAsync(IReadOnlyList<SyncOp> ops, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(ops);

        if (ops.Count == 0)
        {
            return;
        }

        WebSocketConnection? connection;
        lock (_gate)
        {
            connection = _connection;
            if (connection is null || !_pairedThisConnection)
            {
                return;
            }
        }

        var message = await OnUiAsync(
            () => new SyncMessage
            {
                Type = SyncMessageType.Changes,
                Seq = NextSeq(),
                Src = _deviceName,
                Ops = ops,
            },
            token).ConfigureAwait(false);

        try
        {
            await connection.SendAsync(SyncWire.Write(message), token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            // The peer vanished mid-send. Not a failure to report: the change is still ahead of the
            // base, so it will be sent again when the handheld reconnects.
            Log.Info($"Could not publish live-sync changes: {ex.Message}");
        }
    }

    /// <summary>True while a paired peer is connected.</summary>
    public bool IsConnected
    {
        get
        {
            lock (_gate)
            {
                return _connection is not null && _pairedThisConnection;
            }
        }
    }

    /// <summary>Where the peer connected from, for the status line.</summary>
    public string? PeerAddress { get; private set; }

    /// <summary>
    /// Runs work on the WPF dispatcher and awaits the result.
    /// </summary>
    /// <remarks>
    /// The same shape <c>TransferHub</c> uses, and for the same reason: the read loop is not the UI
    /// thread, and the view model is not thread-safe. When there is no dispatcher - a test, or a run
    /// before the window exists - the work runs inline, which is safe precisely because nothing else
    /// is using that thread.
    /// </remarks>
    private static Task<T> OnUiAsync<T>(Func<T> work, CancellationToken token)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;

        if (dispatcher is null || dispatcher.CheckAccess())
        {
            return Task.FromResult(work());
        }

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = dispatcher.InvokeAsync(
            () =>
            {
                try
                {
                    completion.TrySetResult(work());
                }
                catch (Exception ex)
                {
                    completion.TrySetException(ex);
                }
            });

        return completion.Task.WaitAsync(token);
    }

    private void SetState(SyncConnectionState state)
    {
        lock (_gate)
        {
            if (State == state)
            {
                return;
            }

            State = state;
        }

        StateChanged?.Invoke(this, state);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
