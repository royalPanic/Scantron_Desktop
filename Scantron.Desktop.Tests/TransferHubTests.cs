using System.Net;
using Scantron.Core.Models;
using Scantron.Core.Serialization;
using Scantron.Desktop.Services.Transfer;
using Xunit;

namespace Scantron.Desktop.Tests;

/// <summary>
/// Covers the hub's whole request surface as the pure function it is.
/// </summary>
/// <remarks>
/// No sockets are involved, and that is the point: every route, every status code and every
/// failure message is reachable here without binding a port, so these cases run in every build
/// on a machine with no network. The one that matters most is
/// <see cref="A_pull_with_no_document_is_refused_rather_than_served_empty"/>.
/// </remarks>
public sealed class TransferHubTests
{
    private static InventoryDocument Doc(params Container[] containers) => new() { Containers = containers };

    private static Container Cont(string id = "BOX-101", params Item[] items) =>
        new() { Id = id, Name = "Shelf stock", Items = items };

    private static Item Row(string uuid = "u1", string name = "Drill", int quantity = 1) =>
        new() { Uuid = uuid, Name = name, Quantity = quantity, UpdatedAt = 1_700_000_000_000 };

    /// <summary>A handler that records what it was given and reports a clean merge.</summary>
    private static Func<InventoryDocument, HubResponse> Accept(List<InventoryDocument>? received = null) =>
        document =>
        {
            received?.Add(document);
            return new HubResponse(200, HubResponse.Json, HubEndpoints.Acknowledgement(document));
        };

    private static HubResponse Call(
        string method,
        string path,
        string body = "",
        InventoryDocument? current = null,
        Func<InventoryDocument, HubResponse>? onPush = null,
        string deviceName = "DESK-01") =>
        HubEndpoints.Handle(method, path, body, current, onPush ?? Accept(), deviceName);

        // ---- advertised addresses ----------------------------------------------------------------------

        private static IPAddress Addr(string text) => IPAddress.Parse(text);

        /// <summary>
        /// The regression that matters: a wildcard bind with real addresses on the machine must
        /// advertise those addresses.
        /// </summary>
        /// <remarks>
        /// The wildcard prefix is the first bind attempt and the one most likely to succeed, so it is
        /// what usually actually binds. It carries no address of its own, and deriving the status line
        /// from the bound set alone therefore reported "this machine has no network address the
        /// handheld can reach" on exactly the setups that worked - leaving the operator with no
        /// address to type into the CK65.
        /// </remarks>
        [Fact]
        public void A_wildcard_bind_advertises_the_machine_addresses_rather_than_nothing()
        {
            var advertised = TransferHub.SelectAdvertised(
                [IPAddress.Any],
                [Addr("192.168.1.50"), Addr("10.0.4.12")]);

            Assert.Equal([Addr("192.168.1.50"), Addr("10.0.4.12")], advertised);
        }

        [Fact]
        public void Loopback_and_the_wildcard_are_never_advertised()
        {
            var advertised = TransferHub.SelectAdvertised(
                [IPAddress.Loopback, IPAddress.Any],
                [Addr("127.0.0.1"), IPAddress.Any, Addr("192.168.1.50"), Addr("0.0.0.0")]);

            Assert.Equal([Addr("192.168.1.50")], advertised);
        }

        [Fact]
        public void A_link_local_address_is_advertised_last_rather_than_first()
        {
            // APIPA does work when nothing else does, so it cannot be dropped - but it is the address
            // that works least often and must not be the first one read off the screen.
            var advertised = TransferHub.SelectAdvertised(
                [IPAddress.Any],
                [Addr("169.254.10.20"), Addr("192.168.1.50")]);

            Assert.Equal([Addr("192.168.1.50"), Addr("169.254.10.20")], advertised);
        }

        [Fact]
        public void An_ipv6_link_local_address_keeps_its_scope_id_out_of_the_url()
        {
            var advertised = TransferHub.SelectAdvertised(
                [IPAddress.Any],
                [IPAddress.Parse("fe80::1%12"), Addr("192.168.1.50")]);

            Assert.Equal([Addr("192.168.1.50"), IPAddress.Parse("fe80::1%12")], advertised);
            Assert.DoesNotContain("%12", HubState.Sharing(["http://fe80::1:8756"]).Status, StringComparison.Ordinal);
        }

        [Fact]
        public void The_bound_set_is_used_only_when_enumeration_finds_nothing()
        {
            var advertised = TransferHub.SelectAdvertised([Addr("192.168.1.99")], []);

            Assert.Equal([Addr("192.168.1.99")], advertised);
        }

    // ---- /health -----------------------------------------------------------------------------------

    [Fact]
    public void Health_reports_the_protocol_and_the_machine()
    {
        var response = Call("GET", "/health");

        Assert.Equal(200, response.StatusCode);
        Assert.StartsWith("scantron-hub/1 ", response.Body, StringComparison.Ordinal);
        Assert.Contains("DESK-01", response.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void Health_falls_back_to_the_machine_name_when_none_is_given()
    {
        var response = Call("GET", "/health", deviceName: "  ");

        Assert.Contains(Environment.MachineName, response.Body, StringComparison.Ordinal);
    }

    // ---- /pull -------------------------------------------------------------------------------------

    [Fact]
    public void Pull_serves_the_document_as_raw_export_json()
    {
        var document = Doc(Cont(items: [Row(quantity: 4)]));

        var response = Call("GET", "/pull", current: document);

        Assert.Equal(200, response.StatusCode);
        Assert.Equal("application/json", response.ContentType);

        // Byte-for-byte what a file export would contain. An envelope here would break the
        // promise that a USB file and a Wi-Fi file are interchangeable.
        Assert.Equal(InventoryReader.Write(document), response.Body);
        Assert.Equal(4, InventoryReader.Read(response.Body).Containers[0].Items[0].Quantity);
    }

    [Fact]
    public void Pull_serves_the_document_as_it_is_at_the_moment_it_is_asked_for()
    {
        var current = Doc(Cont(items: [Row(quantity: 1)]));

        var before = Call("GET", "/pull", current: current);
        current = current with { Containers = [Cont(items: [Row(quantity: 9)])] };
        var after = Call("GET", "/pull", current: current);

        // The hub must not snapshot: serving a stale document would silently roll back whatever
        // the operator has edited since.
        Assert.Equal(1, InventoryReader.Read(before.Body).Containers[0].Items[0].Quantity);
        Assert.Equal(9, InventoryReader.Read(after.Body).Containers[0].Items[0].Quantity);
    }

    [Fact]
    public void A_pull_with_no_document_is_refused_rather_than_served_empty()
    {
        var response = Call("GET", "/pull", current: null);

        // THE dangerous case. The handheld imports by clearing and replacing, so an empty
        // containers array is not an empty document - it is an instruction to delete everything
        // the device holds. A refusal the operator can read is the only safe answer.
        Assert.Equal(503, response.StatusCode);
        Assert.Contains("open a document", response.Body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("containers", response.Body, StringComparison.OrdinalIgnoreCase);
        Assert.False(InventoryReader.TryRead(response.Body, out _, out _));
    }

    [Fact]
    public void A_deliberately_empty_document_is_still_served()
    {
        // An empty document the operator built on purpose is a real document, and refusing it
        // would make the 503 above the only honest answer rather than the safe one.
        var response = Call("GET", "/pull", current: Doc());

        Assert.Equal(200, response.StatusCode);
        Assert.Empty(InventoryReader.Read(response.Body).Containers);
    }

    // ---- /push -------------------------------------------------------------------------------------

    [Fact]
    public void Push_parses_and_hands_the_document_to_the_merge_handler()
    {
        var received = new List<InventoryDocument>();
        var incoming = Doc(Cont("BOX-7", Row("u9", "Pallet jack", 2)));

        var response = Call("POST", "/push", InventoryReader.Write(incoming), onPush: Accept(received));

        Assert.Equal(200, response.StatusCode);
        Assert.Single(received);
        Assert.Equal("BOX-7", received[0].Containers[0].Id);
        Assert.Equal("""{"ok":true,"containers":1,"items":1}""", response.Body);
    }

    [Fact]
    public void Push_relays_the_handlers_response_unchanged()
    {
        var relayed = new HubResponse(200, HubResponse.Json, """{"ok":true,"conflicts":3,"message":"x"}""");

        var response = HubEndpoints.Handle(
            "POST", "/push", InventoryReader.Write(Doc(Cont())), null, _ => relayed);

        Assert.Equal(relayed, response);
    }

    [Fact]
    public void Push_refuses_json_the_desktop_could_not_read()
    {
        var reached = false;

        var response = Call("POST", "/push", "{ this is not json", onPush: _ =>
        {
            reached = true;
            return new HubResponse(200, HubResponse.Json, "{}");
        });

        Assert.Equal(400, response.StatusCode);
        Assert.False(reached);
        // Operator-readable, and never an exception type name.
        Assert.DoesNotContain(nameof(InventoryFormatException), response.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void Push_refuses_a_document_the_validator_rejects()
    {
        var reached = false;

        // Parses cleanly - so only the content gate can catch this - and the desktop would
        // refuse to export it too.
        var invalid = Doc(
            new Container { Id = "BOX-1", Items = [new Item { Uuid = "u1", Name = "  " }] });

        var response = Call("POST", "/push", InventoryReader.Write(invalid), onPush: _ =>
        {
            reached = true;
            return new HubResponse(200, HubResponse.Json, "{}");
        });

        Assert.Equal(400, response.StatusCode);
        Assert.False(reached);
        Assert.Contains("missing a name", response.Body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Push_refuses_a_duplicate_container_id_the_validator_rejects()
    {
        var duplicate = Doc(Cont("BOX-1"), Cont("BOX-1"));

        var response = Call("POST", "/push", InventoryReader.Write(duplicate));

        Assert.Equal(400, response.StatusCode);
        Assert.Contains("BOX-1", response.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void Push_refuses_an_unsupported_version()
    {
        var future = """{"app":"Scantron","version":"2.0","exportedAt":"","containers":[]}""";

        var response = Call("POST", "/push", future);

        Assert.Equal(400, response.StatusCode);
        Assert.Contains("2.0", response.Body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("1.0")]
    [InlineData("1.1")]
    public void Push_accepts_every_version_the_device_can_import(string version)
    {
        var document = new InventoryDocument { Version = version, Containers = [Cont()] };

        var response = Call("POST", "/push", InventoryReader.Write(document));

        Assert.Equal(200, response.StatusCode);
    }

    [Fact]
    public void A_failing_merge_handler_becomes_a_500_rather_than_killing_the_hub()
    {
        var response = Call("POST", "/push", InventoryReader.Write(Doc(Cont())), onPush: _ =>
            throw new InvalidOperationException("the grid is mid-edit"));

        Assert.Equal(500, response.StatusCode);
        Assert.Contains("the grid is mid-edit", response.Body, StringComparison.Ordinal);
    }

    // ---- routing -----------------------------------------------------------------------------------

    [Fact]
    public void An_unknown_path_is_a_404()
    {
        var response = Call("GET", "/teleport");

        Assert.Equal(404, response.StatusCode);
        Assert.Contains("/teleport", response.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void The_live_sync_route_asked_for_without_an_upgrade_explains_itself()
    {
        // The route exists, so 404 would be a lie; 426 names the one thing the client got wrong.
        var response = Call("GET", "/sync");

        Assert.Equal(426, response.StatusCode);
        Assert.Contains("websocket", response.Body, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("GET", "/push")]
    [InlineData("POST", "/pull")]
    [InlineData("POST", "/health")]
    public void A_known_route_with_the_wrong_verb_says_which_verb_it_wants(string method, string path)
    {
        var response = Call(method, path);

        Assert.Equal(405, response.StatusCode);
        Assert.Contains(path == "/push" ? "POST" : "GET", response.Body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/pull/")]
    [InlineData("/pull?cache=0")]
    [InlineData("/PULL")]
    public void Paths_a_different_http_client_might_send_are_accepted(string path)
    {
        // A handheld built against another URL library will happily send a trailing slash or a
        // different case. Refusing either would be a bug on the operator, not a security control.
        var response = Call("GET", path, current: Doc(Cont()));

        Assert.Equal(200, response.StatusCode);
    }

    // ---- acknowledgement ---------------------------------------------------------------------------

        [Fact]
        public void The_acknowledgement_reports_the_real_container_and_item_counts()
        {
            var body = HubEndpoints.Acknowledgement(Doc(
                Cont("A", Row("u1"), Row("u2")),
                Cont("B"),
                Cont("C", Row("u3", quantity: 5))));

            Assert.Equal("""{"ok":true,"containers":3,"items":3}""", body);
        }

        [Fact]
        public void An_acknowledgement_for_an_empty_document_is_still_well_formed()
        {
            Assert.Equal("""{"ok":true,"containers":0,"items":0}""", HubEndpoints.Acknowledgement(Doc()));
        }
    }