using System.Net;
using System.Net.Sockets;
using System.Text;
using Scantron.Desktop.Services.Transfer;
using Xunit;

namespace Scantron.Desktop.Tests;

/// <summary>
/// The desktop half of discovery, driven over a real loopback socket.
/// </summary>
/// <remarks>
/// The regression these exist for: the desktop had no responder at all, so the handheld's probe was
/// broadcast into the void and "Find desktops" was guaranteed to stay empty. These pin the reply the
/// handheld expects - its exact prefix, name, host and port - and the multi-interface rule that
/// picks a host the handheld can actually reach.
/// </remarks>
public sealed class DiscoveryResponderTests
{
    /// <summary>
    /// A loopback interface, injected so the reply's host is deterministic rather than depending on
    /// whatever adapters the build machine happens to have.
    /// </summary>
    private static readonly IReadOnlyList<LanInterface> Loopback =
        [new LanInterface(IPAddress.Loopback, IPAddress.Parse("255.0.0.0"))];

    private static DiscoveryResponder Responder(int port = 0) =>
        new(port: port, hubPort: 8756, deviceName: "DESKTOP-TEST", interfaces: () => Loopback);

    /// <summary>Sends a payload and returns the reply, or null if nothing comes back in time.</summary>
    private static string? Probe(int port, string payload)
    {
        using var client = new UdpClient(AddressFamily.InterNetwork) { Client = { ReceiveTimeout = 2_000 } };
        var bytes = Encoding.UTF8.GetBytes(payload);
        client.Send(bytes, bytes.Length, new IPEndPoint(IPAddress.Loopback, port));

        try
        {
            var from = new IPEndPoint(IPAddress.Any, 0);
            return Encoding.UTF8.GetString(client.Receive(ref from));
        }
        catch (SocketException)
        {
            // A receive timeout is the expected "no answer" result, not a failure.
            return null;
        }
    }

    [Fact]
    public void a_probe_is_answered_with_the_address_the_handheld_should_dial()
    {
        using var responder = Responder();
        Assert.Null(responder.Start());
        Assert.True(responder.IsListening);

        var reply = Probe(responder.BoundPort, DiscoveryResponder.Probe);

        Assert.Equal("SCANTRON_HUB/1 DESKTOP-TEST 127.0.0.1 8756", reply);
    }

    [Fact]
    public void a_payload_that_is_not_the_probe_is_ignored()
    {
        using var responder = Responder();
        Assert.Null(responder.Start());

        // Silence, not an exception: the datagram was simply not ours.
        Assert.Null(Probe(responder.BoundPort, "HELLO?"));
    }

    [Fact]
    public void stopping_releases_the_port_it_was_listening_on()
    {
        var first = Responder();
        Assert.Null(first.Start());
        var port = first.BoundPort;

        first.Stop();
        Assert.False(first.IsListening);

        // Rebinding the very same port is what proves the first socket really closed.
        using var second = Responder(port);
        Assert.Null(second.Start());
    }

    [Fact]
    public void the_host_chosen_is_the_interface_on_the_senders_subnet()
    {
        // A VPN address first, the LAN second: the probe came from the LAN, so the LAN wins even
        // though it is not the first adapter listed.
        var interfaces = new[]
        {
            new LanInterface(IPAddress.Parse("10.8.0.2"), IPAddress.Parse("255.255.0.0")),
            new LanInterface(IPAddress.Parse("192.168.50.182"), IPAddress.Parse("255.255.255.0")),
        };

        var host = DiscoveryResponder.ChooseHost(IPAddress.Parse("192.168.50.25"), interfaces);

        Assert.Equal(IPAddress.Parse("192.168.50.182"), host);
    }

    [Fact]
    public void a_probe_only_a_prefix_away_is_not_mistaken_for_the_probe()
    {
        Assert.True(DiscoveryResponder.IsProbe(Encoding.UTF8.GetBytes("WHO_HAS")));
        Assert.True(DiscoveryResponder.IsProbe(Encoding.UTF8.GetBytes("  WHO_HAS\r\n")));
        Assert.False(DiscoveryResponder.IsProbe(Encoding.UTF8.GetBytes("WHO_HAS_EXTRA")));
        Assert.False(DiscoveryResponder.IsProbe(Encoding.UTF8.GetBytes("SCANTRON_HUB/1")));
    }
}
