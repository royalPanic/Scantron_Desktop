using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Scantron.Core.Models;
using Scantron.Desktop.Services.Sync;

namespace Scantron.Desktop.Services.Transfer;

/// <summary>
/// What the operator is told about the hub, and the one thing they have to read off the screen.
/// </summary>
/// <param name="Listening">
/// True while a socket is accepting connections.
/// </param>
/// <param name="Status">
/// Plain text for the status line. On success this is the address to type into the CK65, which
/// is the single most important thing on screen: the operator reads the IP off the PC and enters
/// it by hand, because the two devices are on a shared network with nothing to discover each
/// other.
/// </param>
/// <param name="Error">The reason the hub is not listening, or null when it is.</param>
public sealed record HubState(bool Listening, string Status, string? Error = null)
{
    public static HubState Stopped { get; } = new(false, "Not sharing.");

    public static HubState Sharing(IReadOnlyList<string> addresses) => new(
        true,
        addresses.Count > 0
            ? "Sharing at " + string.Join(", ", addresses)
            : "Sharing. This machine has no network address the handheld can reach.");

    public static HubState Failed(string reason) => new(false, "Not sharing: " + reason, reason);
}

/// <summary>
/// The HTTP endpoint the CK65 talks to: <c>GET /health</c>, <c>POST /push</c>, <c>GET /pull</c>.
/// </summary>
/// <remarks>
/// <para>
/// The desktop hosts and the handheld connects, because the CK65 is a battery-powered warehouse
/// device that cannot be relied on to hold a listening socket while docked or asleep, while the
/// desk PC is on and on the network. <see cref="HubEndpoints"/> owns the meaning of every request;
/// this owns the socket, and deliberately nothing else - no request decisions and no state, so
/// the contract can be tested without binding a port.
/// </para>
/// <para>
/// Nothing here runs until the operator asks for it. A transfer endpoint left listening on a
/// shared warehouse network is a way for any device on that network to read the day's stock
/// count and overwrite it, so the hub is inert at startup and stops the moment sharing is
/// switched off.
/// </para>
/// <para>
/// The socket is a <see cref="TcpHttpListener"/> rather than a <see cref="HttpListener"/>, because
/// a non-loopback <see cref="HttpListener"/> prefix needs a URL ACL reservation that only an
/// elevated process can make. Without one, the hub could bind nothing but <c>127.0.0.1</c> and
/// then advertise a LAN address it was not listening on - the desktop reported "sharing" and the
/// handheld could not connect. A <see cref="TcpListener"/> needs no reservation and binds every
/// interface as an ordinary user, which is the only way "sharing" can actually mean reachable.
/// </para>
/// </remarks>
public sealed class TransferHub : IAsyncDisposable
{
    private readonly int _port;
    private readonly string _deviceName;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly object _gate = new();

    private TcpHttpListener? _listener;
    private CancellationTokenSource? _cancellation;
    private Task? _serve;

    public TransferHub(int port = DefaultPort, string? deviceName = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(port, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65535);

        _port = port;
        _deviceName = string.IsNullOrWhiteSpace(deviceName) ? Environment.MachineName : deviceName;
        State = HubState.Stopped;
    }

    /// <summary>Port the contract fixes. Not configurable by the operator on purpose.</summary>
    public const int DefaultPort = 8756;

    /// <summary>Name reported by <c>/health</c> so an operator can tell two PCs apart.</summary>
    public string DeviceName => _deviceName;

    /// <summary>Current state. Written only by the serve loop's owner; read by the UI thread.</summary>
    public HubState State { get; private set; }

    public event EventHandler<HubState>? StateChanged;

    /// <summary>
    /// Binds and begins serving.
    /// </summary>
    /// <remarks>
    /// Never throws for a binding failure. A refusal to bind is the single most likely thing to
    /// go wrong here - another copy of the app is running, or the URL ACL is missing - and an
    /// operator staring at a toolbar that silently does nothing has no way to diagnose it. The
    /// reason lands in <see cref="State"/> instead.
    /// </remarks>
    /// <param name="currentDocument">
    /// Supplies the document <c>/pull</c> serves. Called per request, on the UI thread: what the
    /// operator sees on the grid is what has to go over the wire, exactly as the merge path
    /// rebuilds from the grid rather than from the last saved snapshot.
    /// </param>
    /// <param name="onPush">
    /// Routes an inbound document into the merge path. Called on the UI thread, because it
    /// touches the view model and that is not thread-safe.
    /// </param>
    public void Start(
        Func<InventoryDocument?> currentDocument,
        Func<InventoryDocument, HubResponse> onPush,
        SyncCoordinator? sync = null)
    {
        ArgumentNullException.ThrowIfNull(currentDocument);
        ArgumentNullException.ThrowIfNull(onPush);

        _lifecycle.Wait();
        try
        {
            if (State.Listening)
            {
                return;
            }

            _cancellation?.Dispose();
            _cancellation = new CancellationTokenSource();

            var listener = new TcpHttpListener(_port, HubEndpoints.MaxBodyBytes);

            try
            {
                listener.Start();
            }
            catch (IOException ex)
            {
                listener.Dispose();

                Log.Error("Could not start the transfer hub: " + ex.Message);
                SetState(HubState.Failed(ex.Message));
                return;
            }

            _listener = listener;

            var token = _cancellation.Token;
            _serve = Task.Run(
                () => RunAsync(listener, currentDocument, onPush, sync, token),
                CancellationToken.None);

            SetState(HubState.Sharing(Advertised()));
            Log.Info($"Transfer hub listening on port {_port} for {_deviceName}");
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    /// <summary>
    /// Stops listening and waits for the serve loop to finish.
    /// </summary>
    /// <remarks>
    /// The listener is closed first because that is what unblocks the pending accept; cancelling
    /// alone would leave the loop parked on a socket that never yields another request, and the
    /// hub would keep accepting traffic after the operator switched sharing off.
    /// </remarks>
    public async Task StopAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_listener is null && _serve is null)
            {
                SetState(HubState.Stopped);
                return;
            }

            _cancellation?.Cancel();

            var listener = _listener;
            _listener = null;

                        if (listener is not null)
                        {
                            // Closed before awaiting: this is what unblocks the pending accept.
                            // Cancelling alone would leave the loop parked on a socket that never
                            // yields another request, and the hub would keep accepting traffic
                            // after the operator stopped sharing.
                            listener.Stop();
                        }

                        if (_serve is { } serve)
                        {
                            _serve = null;

                            // Awaited, not waited on: a request in flight owns the view model, and letting
                            // the loop touch it after Start could run again would be the one race in this
                            // feature that matters.
                            await serve.ConfigureAwait(false);
                        }

            SetState(HubState.Stopped);
            Log.Info("Transfer hub stopped");
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _cancellation?.Dispose();
        _lifecycle.Dispose();
    }

            /// <summary>
            /// The addresses an operator can actually type into a handheld, most likely to work first.
            /// </summary>

                /// <remarks>
                /// <para>
                /// Taken from the machine's real addresses, because the hub binds every interface - so the
                /// addresses it is reachable on and the addresses worth showing the operator are the same
                /// set. The previous implementation derived this from the bound prefixes, which made the
                /// address on screen depend on which bind attempt happened to win; with a single
                /// wildcard bind that produced a hub listening on loopback while announcing a LAN address.
                /// </para>
                /// <para>
                /// Loopback and the wildcard are dropped: neither is reachable from a scanner. Link-local is
                /// kept but sorted last - APIPA does work when nothing else does, and the operator cannot know
                /// that in advance - but it is the address that works least often, so it must not be the first
                /// one read off the screen.
                /// </para>
                /// </remarks>
            internal static IReadOnlyList<IPAddress> AdvertisedAddresses(IReadOnlyList<IPAddress> bound) =>
                SelectAdvertised(bound, LocalAddresses());

            /// <summary>
            /// The selection itself, with the enumeration passed in so it can be tested without a network.
            /// </summary>
            /// <remarks>
            /// <paramref name="local"/> takes priority over <paramref name="bound"/> because it is what a
            /// handheld can actually reach. The bound set is only the fallback for the case where
            /// enumeration found nothing at all - otherwise a wildcard bind would advertise nothing.
            /// </remarks>
            internal static IReadOnlyList<IPAddress> SelectAdvertised(
                IReadOnlyList<IPAddress> bound,
                IReadOnlyList<IPAddress> local)
            {
                var reachable = local.Where(IsReachable).ToList();

        if (reachable.Count == 0)
        {
            reachable = bound.Where(IsReachable).ToList();
        }

                // OrderBy is stable, so the enumeration order of the interfaces is preserved within each
                // group rather than being reshuffled by the sort.
                return reachable.OrderBy(a => IsLinkLocal(a) ? 1 : 0).ToList();
            }

            /// <summary>Formats the advertised addresses as the URLs shown on the status line.</summary>
            private IReadOnlyList<string> Advertised() =>
                AdvertisedAddresses([IPAddress.Any])
                    .Select(a => $"http://{WithoutScopeId(a)}:{_port}")
                    .ToList();

            /// <summary>False for loopback and the unspecified address; neither is reachable remotely.</summary>
            private static bool IsReachable(IPAddress address) =>
                !IPAddress.IsLoopback(address) && !address.Equals(IPAddress.Any) && !address.Equals(IPAddress.None);

            /// <summary>True for IPv4 APIPA (169.254.0.0/16) and IPv6 link-local addresses.</summary>
            private static bool IsLinkLocal(IPAddress address) =>
                address.AddressFamily == AddressFamily.InterNetworkV6
                    ? address.IsIPv6LinkLocal
                    : address.AddressFamily == AddressFamily.InterNetwork &&
                      address.GetAddressBytes() is [169, 254, _, ..];

    /// <summary>
    /// Every up, non-loopback IPv4 address on this machine.
    /// </summary>
    /// <remarks>
    /// Virtual adapters, VPN clients and Docker bridges are filtered out only by being
    /// loopback or link-local. Everything else is kept: a PC on a warehouse network can be
    /// handing out addresses this code has never heard of, and the alternative is an address the
    /// operator can see but the hub does not listen on.
    /// </remarks>
    public static IReadOnlyList<IPAddress> LocalAddresses()
    {
        var found = new List<IPAddress>();

        try
        {
            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (adapter.OperationalStatus != OperationalStatus.Up ||
                    adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                {
                    continue;
                }

                foreach (var unicast in adapter.GetIPProperties().UnicastAddresses)
                {
                    var address = unicast.Address;
                    if (address.AddressFamily == AddressFamily.InterNetwork &&
                        !IPAddress.IsLoopback(address) &&
                                        !address.Equals(IPAddress.Any) &&
                                        !address.Equals(IPAddress.None))
                    {
                        found.Add(address);
                    }
                }
            }
        }
        catch (NetworkInformationException ex)
        {
            // Reported rather than thrown: no addresses means the fallback and loopback attempts
            // run, which is better than refusing to start at all.
            Log.Warning($"Could not enumerate network interfaces: {ex.Message}");
        }

        return found;
    }

    /// <summary>
    /// Serves one connection at a time until cancelled.
    /// </summary>
    /// <remarks>
    /// Deliberately sequential. The handheld has exactly one transfer in flight, and a
    /// concurrent one would only raise the chance of two pushes reaching
    /// <paramref name="onPush"/> at once - two threads rebuilding the same document. Serial
    /// handling is what lets the whole hub be reasoned about as "one request, one merge".
    /// </remarks>
    private async Task RunAsync(
        TcpHttpListener listener,
        Func<InventoryDocument?> currentDocument,
        Func<InventoryDocument, HubResponse> onPush,
        SyncCoordinator? sync,
        CancellationToken token)
    {
        await listener.RunAsync(
            (request, cancellation) => DispatchAsync(request, currentDocument, onPush, cancellation),
            sync is null
                ? null
                : (request, stream, remote, cancellation) => DispatchUpgradeAsync(request, stream, remote, cancellation, sync),
            token).ConfigureAwait(false);
    }

    private static Task DispatchUpgradeAsync(
        TcpHttpRequest request,
        System.Net.Sockets.NetworkStream stream,
        System.Net.IPEndPoint? remote,
        CancellationToken token,
        SyncCoordinator sync)
    {
        // Only the live-sync route upgrades. Anything else that asked to switch protocols is a
        // client confusion worth naming, and leaving the socket untouched is the safest answer -
        // the HTTP layer has already written a refusal for it.
        if (!string.Equals(request.Path.Split('?', '#')[0].TrimEnd('/'), "/sync", StringComparison.OrdinalIgnoreCase))
        {
            Log.Warning($"Refused a WebSocket upgrade for {request.Path}");
            return Task.CompletedTask;
        }

        return sync.ServeAsync(stream, remote, token);
    }

    private Task<HubResponse> DispatchAsync(
        TcpHttpRequest request,
        Func<InventoryDocument?> currentDocument,
        Func<InventoryDocument, HubResponse> onPush,
        CancellationToken token)
    {
        Log.Debug($"{request.Method} {request.Path} from {Describe(request)}");

        // Both callbacks cross onto the UI thread together. currentDocument reads the grid and
        // onPush merges into it, so splitting them across threads would let a merge rebuild the
        // document while a pull was reading it.
        return OnUiAsync(
            () => HubEndpoints.Handle(
                request.Method,
                request.Path,
                request.Body,
                currentDocument(),
                onPush,
                _deviceName),
            token);
    }

    /// <summary>
    /// Runs work on the WPF dispatcher and awaits the result.
    /// </summary>
    /// <remarks>
    /// The serve loop is not the UI thread and the view model is not thread-safe, so everything
    /// that touches the document crosses back. When there is no dispatcher - a unit test, or a
    /// run before the window exists - the work runs inline on the calling thread, which is safe
    /// precisely because nothing else is using it.
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

    /// <summary>
        /// IP to advertise, with the binding prefix stripped.
        /// </summary>
        /// <remarks>
        /// Only IPv6 needs it - the scope id on a link-local address would otherwise end up in the
        /// URL the operator reads off the screen. IPv4 prefixes carry no scope, so the address goes
        /// into the text exactly as Windows reported it.
        /// </remarks>
        private static IPAddress WithoutScopeId(IPAddress address) =>
            address.AddressFamily == AddressFamily.InterNetworkV6
                ? new IPAddress(address.GetAddressBytes(), 0)
                : address;

    private static string Describe(TcpHttpRequest request) =>
        request.RemoteEndPoint?.ToString() ?? "an unknown address";

    private void SetState(HubState state)
    {
        // Guarded because Start and StopAsync are both reachable from the UI thread and the
        // handler below will run bindings on it. Assigning twice would report a change that
        // never happened and leave a stale toolbar button enabled.
        lock (_gate)
        {
            if (State == state)
            {
                return;
            }

            State = state;
        }

        Log.Info($"Transfer hub state: {state.Status}");
        StateChanged?.Invoke(this, state);
    }
}