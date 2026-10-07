using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace Scantron.Desktop.Services.Transfer;

/// <summary>
/// A local IPv4 interface: its own address and subnet mask. Carried together so a probe can be
/// answered with the address the sender can actually reach.
/// </summary>
/// <param name="Address">The interface's own IPv4 address.</param>
/// <param name="Mask">Its IPv4 subnet mask.</param>
public sealed record LanInterface(IPAddress Address, IPAddress Mask);

/// <summary>
/// Answers the handheld's UDP discovery probe, so the operator does not have to read an IP address
/// off the screen and type it into the CK65 by hand.
/// </summary>
/// <remarks>
/// <para>
/// The handheld broadcasts <c>WHO_HAS</c> to UDP <see cref="DefaultPort"/> and waits for a unicast
/// reply of <c>SCANTRON_HUB/1 &lt;name&gt; &lt;host&gt; &lt;port&gt;</c>; this is the desktop half
/// of that exchange. The reply carries the hub's port (<see cref="TransferHub.DefaultPort"/>), so
/// what the handheld fills in is exactly the address manual entry would have used.
/// </para>
/// <para>
/// Like the hub, nothing here listens until the operator starts sharing, and it stops the moment
/// sharing does. Discovery is a convenience: a failure to bind is reported and then ignored, because
/// manual-IP transfer must keep working regardless.
/// </para>
/// </remarks>
public sealed class DiscoveryResponder : IDisposable
{
    /// <summary>
    /// Port the contract fixes for the probe. Distinct from the hub's 8756 and the handheld's 8758,
    /// and not operator-configurable for the same reason as those.
    /// </summary>
    public const int DefaultPort = 8757;

    /// <summary>The probe payload the handheld sends.</summary>
    public const string Probe = "WHO_HAS";

    /// <summary>Protocol token that starts every reply, matching <c>Peer.PREFIX</c> on the handheld.</summary>
    public const string Prefix = "SCANTRON_HUB/1";

    private readonly int _port;
    private readonly int _hubPort;
    private readonly string _deviceName;
    private readonly Func<IReadOnlyList<LanInterface>> _interfaces;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);

    private UdpClient? _client;
    private CancellationTokenSource? _cancellation;

    public DiscoveryResponder(
        int port = DefaultPort,
        int hubPort = TransferHub.DefaultPort,
        string? deviceName = null,
        Func<IReadOnlyList<LanInterface>>? interfaces = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(port, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65535);

        _port = port;
        _hubPort = hubPort;
        _deviceName = string.IsNullOrWhiteSpace(deviceName) ? Environment.MachineName : deviceName;
        _interfaces = interfaces ?? LocalInterfaces;
    }

    /// <summary>True while the socket is bound and answering.</summary>
    public bool IsListening { get; private set; }

    /// <summary>The port actually bound, which differs from the request only when 0 was asked for.</summary>
    public int BoundPort { get; private set; }

    /// <summary>
    /// Binds and begins answering. Returns null on success, or the reason it could not bind.
    /// </summary>
    /// <remarks>
    /// Never throws. Discovery is optional, so a refusal - another copy of the app already holding
    /// the port - must leave the rest of sharing intact.
    /// </remarks>
    public string? Start()
    {
        _lifecycle.Wait();
        try
        {
            if (IsListening)
            {
                return null;
            }

            _cancellation?.Dispose();
            _cancellation = new CancellationTokenSource();

            UdpClient client;
            try
            {
                // Bound to the wildcard so a probe sent to the subnet broadcast arrives here, and
                // to the fixed port so the handheld does not have to learn it.
                client = new UdpClient(new IPEndPoint(IPAddress.Any, _port)) { EnableBroadcast = true };
            }
            catch (SocketException ex)
            {
                Log.Error("Could not start the discovery responder: " + ex.Message, ex);
                return ex.Message;
            }

            _client = client;
            BoundPort = ((IPEndPoint)client.Client.LocalEndPoint!).Port;
            IsListening = true;

            var token = _cancellation.Token;
            _ = Task.Run(() => RunAsync(client, token), CancellationToken.None);

            Log.Info($"Discovery responder listening on UDP {BoundPort} as {_deviceName}");
            return null;
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    /// <summary>Closes the socket. Safe to call when not listening.</summary>
    public void Stop()
    {
        _lifecycle.Wait();
        try
        {
            if (!IsListening)
            {
                return;
            }

            IsListening = false;
            _cancellation?.Cancel();

            // Disposed synchronously, which is what frees the port; the receive loop unblocks on
            // the disposal rather than being waited for here.
            _client?.Dispose();
            _client = null;
            _cancellation?.Dispose();
            _cancellation = null;

            Log.Info("Discovery responder stopped");
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public void Dispose() => Stop();

    /// <summary>
    /// Answers probes until the socket is closed.
    /// </summary>
    /// <remarks>
    /// One datagram at a time. A malformed or stray packet is ignored rather than allowed to
    /// escape: anything on the warehouse network can send to this port, and none of it should be
    /// able to stop the responder.
    /// </remarks>
    private async Task RunAsync(UdpClient client, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            UdpReceiveResult received;
            try
            {
                received = await client.ReceiveAsync(token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                // Cancellation and disposal are how this loop is meant to end.
                return;
            }
            catch (Exception ex)
            {
                Log.Error("The discovery responder failed to receive", ex);
                continue;
            }

            if (!IsProbe(received.Buffer))
            {
                continue;
            }

            var reply = Encoding.UTF8.GetBytes(
                BuildReply(_deviceName, _hubPort, ChooseHost(received.RemoteEndPoint.Address, _interfaces())));
            try
            {
                await client.SendAsync(reply, reply.Length, received.RemoteEndPoint).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is ObjectDisposedException or SocketException)
            {
                return;
            }
            catch (Exception ex)
            {
                Log.Error("The discovery responder failed to answer a probe", ex);
            }
        }
    }

    /// <summary>True when a datagram's payload is the handheld's probe, ignoring padding.</summary>
    internal static bool IsProbe(byte[] payload) =>
        Encoding.UTF8.GetString(payload).Trim().Equals(Probe, StringComparison.Ordinal);

    /// <summary>The reply line the handheld parses: <c>SCANTRON_HUB/1 &lt;name&gt; &lt;host&gt; &lt;port&gt;</c>.</summary>
    internal static string BuildReply(string deviceName, int hubPort, IPAddress host) =>
        $"{Prefix} {deviceName} {host} {hubPort}";

    /// <summary>
    /// The local address to answer with: the one on the sender's subnet when there is one,
    /// otherwise the first usable address, otherwise the sender's own address as a last resort.
    /// </summary>
    /// <remarks>
    /// The subnet match is what stops a multi-homed PC (Wi-Fi plus a VPN or a Docker bridge) from
    /// handing back the first adapter Windows happens to list - an address the handheld cannot
    /// reach, presented to the operator as one that works.
    /// </remarks>
    internal static IPAddress ChooseHost(IPAddress sender, IReadOnlyList<LanInterface> interfaces)
    {
        var ipv4 = interfaces.Where(i => i.Address.AddressFamily == AddressFamily.InterNetwork).ToList();

        foreach (var candidate in ipv4)
        {
            if (SameSubnet(candidate.Address, sender, candidate.Mask))
            {
                return candidate.Address;
            }
        }

        return ipv4.Count > 0 ? ipv4[0].Address : sender;
    }

    private static bool SameSubnet(IPAddress a, IPAddress b, IPAddress mask)
    {
        var left = a.GetAddressBytes();
        var right = b.GetAddressBytes();
        var bits = mask.GetAddressBytes();
        if (left.Length != 4 || right.Length != 4 || bits.Length != 4)
        {
            return false;
        }

        for (var i = 0; i < 4; i++)
        {
            if ((left[i] & bits[i]) != (right[i] & bits[i]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Every up, non-loopback interface's IPv4 address and mask.</summary>
    internal static IReadOnlyList<LanInterface> LocalInterfaces()
    {
        var found = new List<LanInterface>();

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
                    if (unicast.Address.AddressFamily == AddressFamily.InterNetwork && unicast.IPv4Mask is not null)
                    {
                        found.Add(new LanInterface(unicast.Address, unicast.IPv4Mask));
                    }
                }
            }
        }
        catch (NetworkInformationException ex)
        {
            // A locked-down machine can refuse enumeration; discovery then simply has nothing to
            // advertise, which is the same outcome as having no adapter.
            Log.Warning("Could not enumerate local interfaces for discovery: " + ex.Message);
        }

        return found;
    }
}
