using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using Scantron.Core.Models;
using Scantron.Core.Serialization;
using Scantron.Desktop.Services;
using Scantron.Desktop.Services.Transfer;
using Scantron.Desktop.ViewModels;
using Xunit;

namespace Scantron.Desktop.Tests;

/// <summary>
/// Drives the hub over a real socket and the view model over its real merge path.
/// </summary>
/// <remarks>
/// Everything else about the feature is covered without a port; this is the one place that
/// proves the socket layer is wired to the pure core correctly, that a real
/// <see cref="HttpClient"/> receives the status codes and content types the contract promises,
/// and that a push arrives at the same merge <c>Import from handheld...</c> uses.
/// </remarks>
public sealed class TransferHubEndToEndTests : IAsyncLifetime
{
    /// <summary>
    /// Loopback, so the case never depends on the machine being on a network - and because the
    /// wildcard prefix is refused outright without a URL ACL, this is also the prefix the
    /// fallback path has to reach on a machine configured like this one.
    /// </summary>
    private const int Port = 18756;

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "scantron-hub-e2e", Guid.NewGuid().ToString("N"));

    private readonly TransferHub _hub = new(Port);

    private MainViewModel _vm = null!;

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_directory);
        _vm = new MainViewModel(new WorkspaceStore(_directory), new InboxStore(_directory), _hub);
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

    private static string Endpoint(string path) => $"http://127.0.0.1:{Port}{path}";

    private static InventoryDocument Doc(string id = "BOX-101", int quantity = 4) => new()
    {
        Containers =
        [
            new Container
            {
                Id = id,
                Name = "Shelf stock",
                Items = [new Item { Uuid = "u1", Name = "Drill", Quantity = quantity, UpdatedAt = 1_700_000_000_000 }],
            },
        ],
    };

    /// <summary>Opens a document through the real file path, so the grid has something in it.</summary>
    private void Load()
    {
        var path = Path.Combine(_directory, "seed.json");
        File.WriteAllText(path, InventoryReader.Write(Doc()));
        _vm.LoadFrom(new Uri(path));
    }

    private static HttpClient Client() => new() { Timeout = TimeSpan.FromSeconds(10) };

    private static void SkipUnlessListening(TransferHub hub) =>
        Assert.True(hub.State.Listening, $"The transfer hub could not bind: {hub.State.Error}");

    /// <summary>
    /// The machine's own non-loopback IPv4 address, or null when it has none.
    /// </summary>
    /// <remarks>
    /// The address a handheld would actually dial. Null only on a machine with no LAN adapter,
    /// where the LAN reachability assertions below cannot mean anything and are skipped rather
    /// than made to pass.
    /// </remarks>
    private static IPAddress? LanAddress() =>
        TransferHub.LocalAddresses()
            .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a));

    private static IPAddress RequireLanAddress()
    {
        var lan = LanAddress();
        Assert.True(lan is not null, "This machine has no non-loopback IPv4 address to test LAN reachability against.");
        return lan!;
    }

    [Fact]
    public async Task A_push_over_a_socket_reaches_the_merge_path_and_the_inbox()
    {
        Load();
        _hub.Start(_vm.LiveDocument, _vm.MergeFromNetwork);
        SkipUnlessListening(_hub);

        using var client = Client();
        var response = await client.PostAsync(
            Endpoint("/push"),
            new StringContent(InventoryReader.Write(Doc("BOX-7", 2)), Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("""{"ok":true,"containers":1,"items":1}""", await response.Content.ReadAsStringAsync());

        // The merge path was reached, not merely the endpoint.
        Assert.Contains(_vm.ActivityLog, a => a.Contains("the handheld over the network", StringComparison.Ordinal));
        Assert.Contains(_vm.ActivityLog, a => a.Contains("Merge preview", StringComparison.Ordinal));

        // And a copy landed on disk before any of it was merged, which is the desktop's only
        // copy of what the scanner sent.
        var staged = Directory.GetFiles(Path.Combine(_directory, "inbox"), "push-*.json");
        Assert.Single(staged);
        Assert.Equal("BOX-7", InventoryReader.Read(File.ReadAllText(staged[0])).Containers[0].Id);
    }

    [Fact]
    public async Task A_pull_over_a_socket_serves_what_the_grid_holds_right_now()
    {
        Load();
        _hub.Start(_vm.LiveDocument, _vm.MergeFromNetwork);
        SkipUnlessListening(_hub);

        using var client = Client();

        var before = await client.GetStringAsync(Endpoint("/pull"));
        Assert.Equal(4, InventoryReader.Read(before).Containers[0].Items[0].Quantity);

        // Edit the grid the way an operator would, then pull again.
        _vm.Containers[0].Items[0].Quantity = 11;
        var after = await client.GetStringAsync(Endpoint("/pull"));

        // Not a snapshot: serving what was loaded at Start would silently discard everything the
        // operator has typed since.
        Assert.Equal(11, InventoryReader.Read(after).Containers[0].Items[0].Quantity);
    }

    [Fact]
    public async Task A_pull_with_nothing_loaded_is_a_503_the_client_can_read()
    {
        // The app as it is launched onto an empty desk: sharing may be started, but there is no
        // document, so the hub must refuse rather than serve an empty one.
        _hub.Start(_vm.LiveDocument, _vm.MergeFromNetwork);
        SkipUnlessListening(_hub);

        using var client = Client();
        var response = await client.GetAsync(Endpoint("/pull"));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("text/plain", response.Content.Headers.ContentType?.MediaType);

        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("open a document", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("containers", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Health_and_an_unknown_route_come_back_over_the_wire_as_specified()
    {
        _hub.Start(_vm.LiveDocument, _vm.MergeFromNetwork);
        SkipUnlessListening(_hub);

        using var client = Client();

        var health = await client.GetAsync(Endpoint("/health"));
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.StartsWith("scantron-hub/1 ", await health.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var missing = await client.GetAsync(Endpoint("/nope"));
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Contains("/nope", await missing.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Malformed_json_comes_back_as_a_400_with_readable_text()
    {
        _hub.Start(_vm.LiveDocument, _vm.MergeFromNetwork);
        SkipUnlessListening(_hub);

        using var client = Client();
        var response = await client.PostAsync(
            Endpoint("/push"),
            new StringContent("{ not json", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        // The handheld shows this verbatim in a toast, so it has to be a sentence an operator can
        // act on rather than a type name.
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(nameof(InventoryFormatException), body, StringComparison.Ordinal);
        Assert.Contains("Scantron JSON", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Stopping_sharing_closes_the_port()
    {
        _hub.Start(_vm.LiveDocument, _vm.MergeFromNetwork);
        SkipUnlessListening(_hub);

        await _hub.StopAsync();
        Assert.False(_hub.State.Listening);

        // The hub has to actually stop accepting, not merely report that it has: a listener left
        // open after "Stop sharing" would keep serving the day's stock to anything on the network.
        using var client = Client();
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => client.GetAsync(Endpoint("/health")));
    }

    [Fact]
        public async Task The_hub_is_inert_until_it_is_started()
        {
            Assert.False(_hub.State.Listening);
            Assert.Equal("Not sharing.", _hub.State.Status);

            // Nothing is bound, so there is nothing to answer - the feature must not listen just
            // because the app was launched.
            using var client = Client();
            await Assert.ThrowsAnyAsync<HttpRequestException>(() => client.GetAsync(Endpoint("/health")));
        }

    [Fact]
    public void Starting_twice_is_harmless()
    {
        _hub.Start(_vm.LiveDocument, _vm.MergeFromNetwork);
        SkipUnlessListening(_hub);

        _hub.Start(_vm.LiveDocument, _vm.MergeFromNetwork);

        Assert.True(_hub.State.Listening);
    }

    /// <summary>
    /// The hub has to answer on a LAN address, not only on loopback.
    /// </summary>
    /// <remarks>
    /// This is the test that would have caught the bug this listener was rewritten for. The
    /// previous implementation bound through <see cref="HttpListener"/>, whose non-loopback
    /// prefixes need a URL ACL reservation that only an elevated process can make. Without one
    /// the wildcard and every literal address were refused with "Access is denied", the hub fell
    /// back to <c>127.0.0.1</c>, and it went on reporting "Sharing at http://192.168.x.x:8756"
    /// while the CK65 got "Connection refused". Every test above passes on loopback, and so did
    /// that implementation - the hub was only ever tested through the address it could not bind.
    /// <para>
    /// So this asserts on the socket itself: bind, then connect to the port using one of the
    /// machine's real non-loopback addresses, which is what the handheld does.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task The_hub_answers_on_a_lan_address_and_not_only_on_loopback()
    {
        var lan = RequireLanAddress();

        Load();
        _hub.Start(_vm.LiveDocument, _vm.MergeFromNetwork);
        SkipUnlessListening(_hub);

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        var response = await client.GetAsync($"http://{lan}:{Port}/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.StartsWith("scantron-hub/1 ", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    /// <summary>
    /// Every address on the status line has to be an address the hub is really listening on.
    /// </summary>
    /// <remarks>
    /// The status line is the only thing the operator can act on - it is where they read the IP
    /// to type into the CK65. Reporting an address that is not bound produces a toolbar that
    /// looks like sharing succeeded and a handheld that cannot connect, which is exactly the
    /// failure this pair of tests exists to keep fixed.
    /// </remarks>
    [Fact]
    public void The_status_line_only_names_addresses_the_hub_is_listening_on()
    {
        var advertised = TransferHub.AdvertisedAddresses([IPAddress.Any]);

        Assert.DoesNotContain(advertised, a => IPAddress.IsLoopback(a));
        Assert.DoesNotContain(advertised, a => a.Equals(IPAddress.Any));
    }

    [Fact]
    public async Task A_push_from_a_lan_address_reaches_the_merge_path()
    {
        var lan = RequireLanAddress();

        Load();
        _hub.Start(_vm.LiveDocument, _vm.MergeFromNetwork);
        SkipUnlessListening(_hub);

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        var response = await client.PostAsync(
            $"http://{lan}:{Port}/push",
            new StringContent(InventoryReader.Write(Doc("BOX-LAN", 3)), Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("""{"ok":true,"containers":1,"items":1}""", await response.Content.ReadAsStringAsync());

        var staged = Directory.GetFiles(Path.Combine(_directory, "inbox"), "push-*.json");
        Assert.Single(staged);
        Assert.Equal("BOX-LAN", InventoryReader.Read(File.ReadAllText(staged[0])).Containers[0].Id);
    }

    [Fact]
    public void A_refused_bind_names_the_port_instead_of_failing_silently()
    {
        var taken = new TcpListener(IPAddress.Loopback, 0);
        taken.Start();
        var port = ((IPEndPoint)taken.LocalEndpoint).Port;

        try
        {
            var blocked = new TransferHub(port);
            blocked.Start(() => null, _ => throw new InvalidOperationException("unreachable"));

            // The operator is left with a reason they can act on - a port another copy already
            // has - rather than a toolbar that appears to have started and cannot be reached.
            Assert.False(blocked.State.Listening);
            Assert.Contains(port.ToString(System.Globalization.CultureInfo.InvariantCulture), blocked.State.Error!, StringComparison.Ordinal);
            Assert.Contains("Scantron", blocked.State.Error!, StringComparison.Ordinal);
        }
        finally
        {
            taken.Stop();
        }
    }
}