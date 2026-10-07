using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using Scantron.Core.Models;
using Scantron.Core.Sync;
using Scantron.Desktop.Services;
using Scantron.Desktop.Services.Sync;
using Scantron.Desktop.Services.Transfer;
using Scantron.Desktop.Tests;
using Scantron.Desktop.ViewModels;
using Xunit;

/// <summary>
/// Drives live sync over a real upgraded socket, from the client side.
/// </summary>
/// <remarks>
/// <para>
/// Everything else about live sync is covered without a port - the codec from byte arrays, the
/// session from messages, the core from documents. This is the one test that proves those pieces
/// are actually joined up: a real WebSocket client shakes hands with the hub, pairs, and exchanges
/// changes that land in the view model.
/// </para>
/// <para>
/// <c>ClientWebSocket</c> is used as the client precisely because it is an independent
/// implementation. A client written against this desktop's own codec would agree with it by
/// construction and would not notice the framing being wrong.
/// </para>
/// </remarks>
public sealed class LiveSyncEndToEndTests : IAsyncLifetime
{
    /// <summary>
    /// Loopback on a port of its own, so the suite never depends on the machine being networked and
    /// never collides with the manual-transfer end-to-end test.
    /// </summary>
    private const int Port = 18757;

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "scantron-sync-e2e", Guid.NewGuid().ToString("N"));

    private readonly TransferHub _hub = new(Port);
    private readonly PairedPeerStore _paired;
    private readonly SyncCoordinator _coordinator;

    private MainViewModel _vm = null!;

    public LiveSyncEndToEndTests()
    {
        _paired = new PairedPeerStore(_directory);
        _coordinator = new SyncCoordinator(_paired, "DESK-01");
    }

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_directory);
        _vm = new MainViewModel(
            new WorkspaceStore(_directory),
            new InboxStore(_directory),
            _hub,
            paired: _paired,
            coordinator: _coordinator);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _hub.DisposeAsync();
        _vm.Dispose();

        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a test over.
        }
    }

    private static InventoryDocument Doc(string containerId = "BOX-101", params Item[] items) =>
        new()
        {
            Containers = [new Container { Id = containerId, Name = "Shelf stock", Items = items, UpdatedAt = 100 }],
            Version = InventoryFormat.CurrentVersion,
        };

    private static Item Row(string uuid = "u1", string name = "Drill", int quantity = 4) =>
        new() { Uuid = uuid, Name = name, Barcode = "AAA", Quantity = quantity, UpdatedAt = 100 };

    /// <summary>Opens a document through the real file path, so the grid has something in it.</summary>
    private void Load(InventoryDocument document)
    {
        var path = Path.Combine(_directory, "seed.json");
        File.WriteAllText(path, Scantron.Core.Serialization.InventoryReader.Write(document));
        _vm.LoadFrom(new Uri(path));
    }

    /// <summary>Starts live sync through the real command, so the hub wiring is what is exercised.</summary>
    private void StartSync()
    {
        _vm.StartSyncingCommand.Execute(null);
    }

    private async Task<ClientWebSocket> ConnectAsync()
    {
        var socket = new ClientWebSocket();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{Port}/sync"), timeout.Token);

        // Attempting to bind the port is asynchronous, so the retry loop below tolerates the first
        // connection losing the race rather than assuming the hub is already accepting.
        return socket;
    }

    private static async Task SendAsync(ClientWebSocket socket, SyncMessage message)
    {
        var bytes = Encoding.UTF8.GetBytes(SyncWire.Write(message));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, timeout.Token);
    }

    private static async Task<SyncMessage> ReceiveAsync(ClientWebSocket socket)
    {
        var buffer = new byte[64 * 1024];
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var result = await socket.ReceiveAsync(buffer, timeout.Token);

        Assert.Equal(WebSocketMessageType.Text, result.MessageType);

        var text = Encoding.UTF8.GetString(buffer, 0, result.Count);
        Assert.True(SyncWire.TryRead(text, out var message, out var error), error);
        return message;
    }

    /// <summary>
    /// Waits for the hub to accept a connection, tolerating the listener still binding.
    /// </summary>
    /// <remarks>
    /// <c>TcpListener.Start</c> returns before the socket is necessarily accepting, and the
    /// handshake itself is what proves it. Retrying here keeps the test about the protocol rather
    /// than about a start-up race.
    /// </remarks>
    private async Task<ClientWebSocket> ConnectWithRetryAsync()
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                return await ConnectAsync();
            }
            catch (Exception ex) when (ex is WebSocketException or SocketException or OperationCanceledException)
            {
                await Task.Delay(100);
            }
        }

        throw new InvalidOperationException("The hub never accepted a WebSocket connection.");
    }

    [Fact]
    public async Task A_paired_handheld_pushes_a_change_that_lands_in_the_desktop()
    {
        Load(Doc("BOX-101", Row(quantity: 4)));
        StartSync();
        Assert.True(_hub.State.Listening, _hub.State.Status);

        using var socket = await ConnectWithRetryAsync();

        await SendAsync(socket, new SyncMessage
        {
            Type = SyncMessageType.Hello,
            Src = "ck65-1",
            Hello = new HelloInfo("ck65-1", "CK65-04", SyncMessage.ProtocolVersion),
        });

        // No reply is owed for a greeting from an unpaired device - it is the pair that is answered.

        await SendAsync(socket, new SyncMessage
        {
            Type = SyncMessageType.Pair,
            Src = "ck65-1",
            Pair = new PairInfo(_paired.Code),
        });

        var paired = await ReceiveAsync(socket);
        Assert.Equal(SyncMessageType.Paired, paired.Type);

        // Nothing follows yet, and that is correct: the desktop has no changes the handheld does not
        // already have, and it does not send empty frames just to say so. `paired` is the last
        // guaranteed reply, so the handheld may now send its own changes.

        // The handheld reports the scan it just made.
        await SendAsync(socket, new SyncMessage
        {
            Type = SyncMessageType.Changes,
            Seq = 1,
            Src = "ck65-1",
            Ops = [new UpsertItemOp("BOX-101", Row(quantity: 12))],
        });

        var ack = await ReceiveAsync(socket);
        Assert.Equal(SyncMessageType.Ack, ack.Type);

        // And the desktop shows it, with no Accept click anywhere in the path.
        var container = _vm.Containers.Single(c => c.Id == "BOX-101");
        Assert.Equal(12, container.Items.Single().Quantity);
    }

    [Fact]
    public async Task An_unpaired_handheld_is_refused_and_nothing_changes()
    {
        Load(Doc("BOX-101", Row(quantity: 4)));
        StartSync();

        using var socket = await ConnectWithRetryAsync();

        await SendAsync(socket, new SyncMessage
        {
            Type = SyncMessageType.Hello,
            Src = "ck65-9",
            Hello = new HelloInfo("ck65-9", "CK65-99", SyncMessage.ProtocolVersion),
        });

        await SendAsync(socket, new SyncMessage
        {
            Type = SyncMessageType.Pair,
            Src = "ck65-9",
            Pair = new PairInfo("000000"),
        });

        var bye = await ReceiveAsync(socket);
        Assert.Equal(SyncMessageType.Bye, bye.Type);
        Assert.Contains("pairing code", bye.Reason);

        // The pair was refused, so the document is untouched.
        Assert.Equal(4, _vm.Containers.Single(c => c.Id == "BOX-101").Items.Single().Quantity);
    }

    [Fact]
    public async Task A_conflict_is_badged_then_settled_and_the_handheld_is_told()
    {
        // The complete conflict story over a real socket: both devices change the same field, the
        // desktop applies everything it safely can and badges the one field it cannot, and settling
        // it writes the agreed value and tells the handheld.
        Load(Doc("BOX-101", Row(quantity: 4)));
        StartSync();

        using var socket = await ConnectWithRetryAsync();
        await PairAsync(socket);

        // The desktop's own edit: quantity 4 -> 7.
        _vm.SelectedContainer!.Items.Single().Quantity = 7;

        // The handheld changed the same field to 9 while the desktop was editing.
        await SendAsync(socket, new SyncMessage
        {
            Type = SyncMessageType.Changes,
            Seq = 1,
            Src = "ck65-1",
            Ops = [new UpsertItemOp("BOX-101", Row(quantity: 9))],
        });

        var ack = await ReceiveAsync(socket);
        Assert.Equal(SyncMessageType.Ack, ack.Type);

        var conflict = await ReceiveAsync(socket);
        Assert.Equal(SyncMessageType.Conflict, conflict.Type);
        Assert.Equal("Quantity", conflict.Conflict!.Field);
        Assert.Equal("4", conflict.Conflict.BaseValue);

        // The badge is up, and the operator's own value is still what they see - an edit must not
        // appear to vanish the instant the peer's change lands.
        Assert.Equal(1, _vm.OpenConflictCount);
        Assert.Equal(7, _vm.SelectedContainer!.Items.Single().Quantity);

        // Settle it, keeping the handheld's value.
        _vm.Conflicts.Single().TakeRemote();
        _vm.CommitMergeCommand.Execute(null);

        Assert.Equal(0, _vm.OpenConflictCount);
        Assert.Equal(9, _vm.SelectedContainer!.Items.Single().Quantity);

        // And the handheld is told, so it does not keep the field frozen.
        var resolve = await ReceiveAsync(socket);
        Assert.Equal(SyncMessageType.Changes, resolve.Type);
        var op = Assert.IsType<ResolveOp>(Assert.Single(resolve.Ops));
        Assert.Equal("9", op.Value);
    }

    [Fact]
    public async Task An_unresolved_conflict_survives_and_is_not_settled_by_default()
    {
        // The dangerous default: silently picking a winner. Leaving a conflict open must leave the
        // base where it was, so the disagreement is reported again rather than lost.
        Load(Doc("BOX-101", Row(quantity: 4)));
        StartSync();

        using var socket = await ConnectWithRetryAsync();
        await PairAsync(socket);

        _vm.SelectedContainer!.Items.Single().Quantity = 7;

        await SendAsync(socket, new SyncMessage
        {
            Type = SyncMessageType.Changes,
            Seq = 1,
            Src = "ck65-1",
            Ops = [new UpsertItemOp("BOX-101", Row(quantity: 9))],
        });

        await ReceiveAsync(socket);
        await ReceiveAsync(socket);

        // Applying with nothing decided must refuse rather than commit a guess.
        _vm.CommitMergeCommand.Execute(null);

        Assert.Equal(1, _vm.OpenConflictCount);
        Assert.Equal(7, _vm.SelectedContainer!.Items.Single().Quantity);
    }

    [Fact]
    public async Task A_plain_http_client_asking_for_the_sync_path_is_told_to_upgrade()
    {
        // The route exists, so it must not answer 404 - that would send an operator looking for a
        // typo in an address that was correct all along.
        StartSync();

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        var response = await http.GetAsync($"http://127.0.0.1:{Port}/sync");

        Assert.Equal(HttpStatusCode.UpgradeRequired, response.StatusCode);
        Assert.Contains("websocket", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_shared_base_survives_a_restart_so_nothing_is_re_sent()
    {
        // The base is the change cursor, so it has to outlive the process. If it were only in
        // memory, every restart would make the whole document look changed and the desktop would
        // push its entire inventory at the handheld again.
        Load(Doc("BOX-101", Row(quantity: 4)));
        StartSync();

        using var socket = await ConnectWithRetryAsync();
        await PairAsync(socket);

        await SendAsync(socket, new SyncMessage
        {
            Type = SyncMessageType.Changes,
            Seq = 1,
            Src = "ck65-1",
            Ops = [new UpsertItemOp("BOX-101", Row(quantity: 12))],
        });

        Assert.Equal(SyncMessageType.Ack, (await ReceiveAsync(socket)).Type);

        // Reopened the way a fresh launch would: same directory, nothing carried over in memory.
        var reopened = new WorkspaceStore(_directory);
        Assert.True(reopened.TryLoad(out var error), error);
        Assert.NotNull(reopened.Base);
        Assert.Equal(12, reopened.Base!.Containers.Single().Items.Single().Quantity);

        // And nothing is outstanding, which is what stops the restart from re-sending.
        Assert.Empty(ChangeCursors.Diff(reopened.Base, reopened.Current!));
    }

    private async Task PairAsync(ClientWebSocket socket)
    {
        await SendAsync(socket, new SyncMessage
        {
            Type = SyncMessageType.Hello,
            Src = "ck65-1",
            Hello = new HelloInfo("ck65-1", "CK65-04", SyncMessage.ProtocolVersion),
        });

        await SendAsync(socket, new SyncMessage
        {
            Type = SyncMessageType.Pair,
            Src = "ck65-1",
            Pair = new PairInfo(_paired.Code),
        });

        var paired = await ReceiveAsync(socket);
        Assert.Equal(SyncMessageType.Paired, paired.Type);
    }
}
