using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using Scantron.Core.Models;

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
/// <see cref="HttpListener"/> is in the BCL, handles headers and keep-alive, and needs no
/// package. Its one sharp edge is that a wildcard prefix needs a URL ACL, which a non-elevated
/// process may not have - see <see cref="Bind"/> for the three attempts made before giving up.
/// </para>
/// </remarks>
public sealed class TransferHub : IAsyncDisposable
{
    private readonly int _port;
    private readonly string _deviceName;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly object _gate = new();

    private HttpListener? _listener;
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
    public void Start(Func<InventoryDocument?> currentDocument, Func<InventoryDocument, HubResponse> onPush)
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

            if (!TryBind(out var listener, out var addresses, out var failure))
                        {
                            Log.Error("Could not start the transfer hub: " + failure);
                            SetState(HubState.Failed(failure));
                            return;
                        }

                        _listener = listener;

                        var token = _cancellation.Token;
                        _serve = Task.Run(() => ServeAsync(listener, currentDocument, onPush, token), CancellationToken.None);

                        SetState(HubState.Sharing(Advertised(addresses)));
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
    /// The listener is closed first because that is what unblocks
    /// <see cref="HttpListener.GetContextAsync"/>; cancelling alone would leave the loop parked
    /// on a socket that never yields another request, and the hub would keep accepting traffic
    /// after the operator switched sharing off.
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
                            // Closed before awaiting: this is what unblocks GetContextAsync. Cancelling
                            // alone would leave the loop parked on a socket that never yields another request,
                            // and the hub would keep accepting traffic after the operator stopped sharing.
                            listener.Close();
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
    /// Binds the listener, trying three strategies in turn.
    /// </summary>
    /// <remarks>
    /// <list type="number">
    /// <item>
    /// <c>http://+:port/</c> - one prefix, every interface. Preferred because it needs no
    /// per-address bookkeeping, and because it survives the PC changing networks mid-shift.
    /// Windows allows it for ordinary ports on most configurations without elevation, but the
    /// ACL is not guaranteed, so it cannot be the only attempt.
    /// </item>
    /// <item>
    /// One prefix per up, non-loopback IPv4 address. Slower and more code, but it needs no URL
    /// reservation at all, and it means the prefixes can be built from the addresses the UI is
    /// about to show the operator - so the two can never disagree about what is reachable.
    /// </item>
    /// <item>
    /// Loopback alone. Not useful to a handheld, but it proves the port is free and it makes a
    /// failure diagnosable rather than mysterious; the address is never advertised.
    /// </item>
    /// </list>
    /// </remarks>
    private bool TryBind(out HttpListener listener, out IReadOnlyList<IPAddress> advertised, out string failure)
    {
        listener = new HttpListener();
        advertised = [];
        failure = "";

            // Close enough to started that the exception has to be caught rather than avoided:
            // HttpListener does not validate a prefix until Start, so this is the only point at
            // which a missing URL ACL or a taken port becomes visible.
            foreach (var addresses in Prefixes())
            {
                try
                {
                    foreach (var prefix in PrefixesFor(addresses))
                    {
                        listener.Prefixes.Add(prefix);
                    }

                    listener.Start();
                    advertised = addresses;
                    Log.Debug($"Transfer hub bound via {string.Join(", ", PrefixesFor(addresses))}");
                    return true;
                }
                catch (HttpListenerException ex)
                {
                    Log.Debug($"Bind failed for {string.Join(", ", PrefixesFor(addresses))}: {ex.Message}");

                    // Rebuilt per attempt: a half-registered prefix must not carry into the next
                    // strategy, and a closed listener is what lets this one be abandoned cleanly.
                    listener.Close();
                    listener = new HttpListener();
                    failure = $"{ex.Message} (Windows error {ex.ErrorCode})";
                }
            }

            // The port being taken is the likeliest cause by a wide margin - another copy of this
            // app is already sharing - so it is named rather than left to be inferred from a
            // Windows error code the operator has never seen.
            failure = $"the port {_port} is not available - {failure}. Another copy of Scantron may already be sharing.";
            return false;
        }

        /// <summary>Turns bind addresses into the URL prefixes a listener registers.</summary>
        private IEnumerable<string> PrefixesFor(IEnumerable<IPAddress> addresses)
        {
            foreach (var address in addresses)
            {
                // IPAddress.Any needs the '+' form - a wildcard prefix and a literal 0.0.0.0 are
                // not the same thing to Windows, and the '+' form is the one that survives a
                // missing URL ACL on most configurations.
                yield return address.Equals(IPAddress.Any)
                    ? $"http://+:{_port}/"
                    : $"http://{address}:{_port}/";
            }
        }

    /// <summary>The candidate prefix sets, most capable first.</summary>
        private IEnumerable<IReadOnlyList<IPAddress>> Prefixes()
    {
            yield return [IPAddress.Any];

            var interfaces = LocalAddresses();
        if (interfaces.Count > 0)
        {
            yield return interfaces;
        }

            yield return [IPAddress.Loopback];
    }

        /// <summary>
        /// The addresses an operator can actually type into a handheld.
        /// </summary>
        /// <remarks>
        /// Loopback and the wildcard are dropped. Both can appear in the bound prefix set - the
        /// loopback one only on a machine with no network at all - and neither is reachable from a
        /// scanner, so putting either in the status line would have the operator typing an address
        /// that cannot work. The hub still binds them, so this only affects what is advertised.
        /// </remarks>
        private IReadOnlyList<string> Advertised(IReadOnlyList<IPAddress> bound) =>
            bound
                .Where(a => !IPAddress.IsLoopback(a) && !a.Equals(IPAddress.Any))
                .Select(a => $"http://{WithoutScopeId(a)}:{_port}")
                .ToList();

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
    private async Task ServeAsync(
        HttpListener listener,
        Func<InventoryDocument?> currentDocument,
        Func<InventoryDocument, HubResponse> onPush,
        CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                // The normal way out: StopAsync closed the listener out from under the await.
                if (!token.IsCancellationRequested)
                {
                    Log.Error("The transfer hub stopped accepting requests", ex);
                }

                break;
            }

            try
            {
                var response = await DispatchAsync(context, currentDocument, onPush, token)
                    .ConfigureAwait(false);
                await WriteAsync(context.Response, response).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                // A scanner that walks out of range mid-request is ordinary in a warehouse and
                // must not take the hub down with it.
                Log.Warning($"A hub connection failed: {ex.Message}");
            }
        }
    }

    private async Task<HubResponse> DispatchAsync(
        HttpListenerContext context,
        Func<InventoryDocument?> currentDocument,
        Func<InventoryDocument, HubResponse> onPush,
        CancellationToken token)
    {
        var request = context.Request;
        var body = request.HttpMethod == "POST" ? await ReadBodyAsync(request, token).ConfigureAwait(false) : "";

        if (body is null)
        {
            Log.Warning($"Refused a request from {Describe(request)}: body larger than {HubEndpoints.MaxBodyBytes / (1024 * 1024)} MB");
            return new HubResponse(
                413,
                HubResponse.Text,
                $"The transfer is larger than this desktop accepts ({HubEndpoints.MaxBodyBytes / (1024 * 1024)} MB).");
        }

        Log.Debug($"{request.HttpMethod} {request.Url?.AbsolutePath} from {Describe(request)}");

        // Both callbacks cross onto the UI thread together. currentDocument reads the grid and
        // onPush merges into it, so splitting them across threads would let a merge rebuild the
        // document while a pull was reading it.
        return await OnUiAsync(() =>
        {
            var current = currentDocument();
            return HubEndpoints.Handle(request.HttpMethod, request.Url?.AbsolutePath ?? "/", body ?? "", current, onPush, _deviceName);
        }, token).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads the request body to the end, refusing anything over the cap.
    /// </summary>
    /// <remarks>
    /// Returns null rather than throwing when the body is too large, and stops reading as soon as
    /// it knows - the point is that the bytes never accumulate, not that the sender is told
    /// politely. <c>Expect: 100-continue</c> is declined for the same reason: it lets the sender
    /// stop before sending at all.
    /// </remarks>
    private static async Task<string?> ReadBodyAsync(HttpListenerRequest request, CancellationToken token)
    {
        if (request.ContentLength64 > HubEndpoints.MaxBodyBytes)
        {
            return null;
        }

        request.Headers["Expect"] = null;

        using var reader = new StreamReader(request.InputStream, request.ContentEncoding ?? Encoding.UTF8);
        var buffer = new char[4096];
        var text = new StringBuilder();
        var limit = HubEndpoints.MaxBodyBytes / 4;

        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false)) > 0)
        {
            if (text.Length + read > limit)
            {
                return null;
            }

            text.Append(buffer, 0, read);
        }

        return text.ToString();
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

    /// <summary>Writes a response and closes the connection.</summary>
    private static async Task WriteAsync(HttpListenerResponse response, HubResponse hubResponse)
    {
        var body = Encoding.UTF8.GetBytes(hubResponse.Body);

        response.StatusCode = hubResponse.StatusCode;
                response.ContentType = hubResponse.ContentType;
        // Every response is length-delimited and closed. The handheld sends one request at a
        // time, so there is nothing to gain from connection reuse and a wrong Content-Length is
        // the classic way an HTTP client ends up reading somebody else's reply.
        response.ContentLength64 = body.Length;
        response.KeepAlive = false;

        await response.OutputStream.WriteAsync(body).ConfigureAwait(false);
        await response.OutputStream.FlushAsync().ConfigureAwait(false);
        response.Close();
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

    private static string Describe(HttpListenerRequest request) =>
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