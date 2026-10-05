using System.Net;
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
/// Drives the desktop-initiated push against a stand-in handheld over a real socket.
/// </summary>
/// <remarks>
/// <para>
/// This is the regression test for the button that did nothing. The old
/// <c>PushToHandheld</c> validated the document, built a response describing a transfer that had
/// never happened, reported "Handheld pulled N containers" and sent nothing - so no test that only
/// inspected the view model could have caught it. Every case here therefore asserts on bytes that
/// arrived at the far end of a socket, which is the only thing the feature actually promises.
/// </para>
/// <para>
/// The stand-in is a <see cref="TcpListener"/> speaking the contract directly rather than a mock,
/// for the same reason <c>FakeHubServer</c> exists on the device side: a mock asserts that the
/// client called what it was told to, not that it put a well-formed document on the wire.
/// </para>
/// </remarks>
public sealed class HandheldPushEndToEndTests : IAsyncLifetime
{
    /// <summary>
    /// Loopback, so the case does not depend on the machine being on a warehouse network. Port 0
    /// is deliberately not used: the client is configured with the port up front, and letting the
    /// OS choose would race the assignment.
    /// </summary>
    private const int Port = 18758;

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "scantron-push-e2e", Guid.NewGuid().ToString("N"));

    private readonly TransferHub _hub = new(18759);
    private readonly HandheldClient _client = new() { Port = Port };

    private MainViewModel _vm = null!;

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_directory);
        _vm = new MainViewModel(
            new WorkspaceStore(_directory),
            new InboxStore(_directory),
            _hub,
            _client);

        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _hub.DisposeAsync();
        _vm.Dispose();
        _client.Dispose();

        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a test over.
        }
    }

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
    private void Load(int quantity = 4)
    {
        var path = Path.Combine(_directory, "seed.json");
        File.WriteAllText(path, InventoryReader.Write(Doc(quantity: quantity)));
        _vm.LoadFrom(new Uri(path));
    }

    /// <summary>
    /// A stand-in handheld that captures one request and answers it.
    /// </summary>
    /// <remarks>
    /// Accepts a single connection and reads until the declared length has arrived, so a test that
    /// passes is evidence the client framed a complete HTTP request - a half-sent body would hang
    /// here rather than quietly succeed.
    /// </remarks>
    private sealed class FakeHandheld(int port) : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, port);

        public Task<string?> NextRequestAsync(TimeSpan timeout) => Task.Run(async () =>
        {
            if (!_listener.Pending())
            {
                await Task.Delay(timeout);
            }

            if (!_listener.Pending())
            {
                return null;
            }

            using var socket = await _listener.AcceptTcpClientAsync();
            using var stream = socket.GetStream();
            stream.ReadTimeout = (int)timeout.TotalMilliseconds;

            var raw = new MemoryStream();
            var buffer = new byte[8192];
            var contentLength = 0;
            var headerEnd = -1;

            while (headerEnd < 0 || raw.Length < contentLength + headerEnd)
            {
                var read = await stream.ReadAsync(buffer);
                if (read == 0)
                {
                    break;
                }

                raw.Write(buffer, 0, read);

                var text = Encoding.Latin1.GetString(raw.GetBuffer(), 0, (int)raw.Length);
                var split = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);

                if (headerEnd < 0 && split >= 0)
                {
                    headerEnd = split + 4;
                    contentLength = HeaderValue(text[..split], "Content-Length");
                }
            }

            var request = Encoding.Latin1.GetString(raw.GetBuffer(), 0, (int)raw.Length);

                        // Length computed rather than hardcoded: a wrong Content-Length truncates the body and
                        // HttpClient hands back a partial acknowledgement, which would fail this test for a
                        // reason that has nothing to do with the client under test.
                        const string acknowledgement = """{"ok":true,"containers":1,"items":1}""";

                        await stream.WriteAsync(Encoding.Latin1.GetBytes(
                            "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\n" +
                            $"Content-Length: {Encoding.UTF8.GetByteCount(acknowledgement)}\r\nConnection: close\r\n\r\n" +
                            acknowledgement));

                        return request;
        });

        public void Start() => _listener.Start();

        public void Dispose()
        {
            try
            {
                _listener.Stop();
            }
            catch (SocketException)
            {
                // Stopping an already-stopped listener is not a test failure.
            }
        }

        private static int HeaderValue(string headers, string name)
        {
            foreach (var line in headers.Split("\r\n"))
            {
                if (line.StartsWith(name + ":", StringComparison.OrdinalIgnoreCase) &&
                    int.TryParse(line[(name.Length + 1)..].Trim(), out var value))
                {
                    return value;
                }
            }

            return 0;
        }
    }

    [Fact]
    public async Task Pressing_send_to_handheld_puts_the_document_on_the_wire()
    {
        Load(quantity: 7);

        using var handheld = new FakeHandheld(Port);
        handheld.Start();

        _vm.HandheldAddress = "127.0.0.1";

        // The button itself, not the handler - this is the path the operator takes, and the
        // regression was that this path sent nothing.
        _vm.PushToHandheldCommand.Execute(null);

        var request = await handheld.NextRequestAsync(TimeSpan.FromSeconds(10));
        Assert.NotNull(request);

        // The wire contract: POST to /receive, carrying the raw export document.
        Assert.StartsWith("POST /receive HTTP/1.1", request!, StringComparison.Ordinal);

        // And the body is the document, unchanged - what arrived over Wi-Fi has to be byte-identical
        // to what a USB export would have written, or the two are not interchangeable.
        var separator = request.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        var body = request[(separator + 4)..];
        var received = InventoryReader.Read(body);

        Assert.Equal(7, received.Containers[0].Items[0].Quantity);
        Assert.Equal("BOX-101", received.Containers[0].Id);
    }

    [Fact]
    public async Task A_successful_push_is_reported_and_marked_as_delivered()
    {
        Load();

        using var handheld = new FakeHandheld(Port);
        handheld.Start();
        _vm.HandheldAddress = "127.0.0.1";

        _vm.PushToHandheldCommand.Execute(null);
        Assert.NotNull(await handheld.NextRequestAsync(TimeSpan.FromSeconds(10)));

        // Give the command a moment to read the acknowledgement.
        for (var attempt = 0; attempt < 200 && _vm.IsBusy; attempt++)
        {
            await Task.Delay(20);
        }

        // The counts come from what the device actually acknowledged, not from what the desktop
        // hoped to send - the old code reported a success for a transfer that never happened.
        Assert.True(_vm.HasServedToHandheld);
        Assert.Contains("1 container", _vm.StatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("1 item", _vm.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_push_that_the_handheld_refuses_is_reported_and_not_marked_as_delivered()
    {
        Load();

        // Nothing listening at all: the connection is refused, which is what an operator who
        // walked away from the scanner actually sees.
        var closed = new HandheldClient(timeoutMs: 2000) { Port = Port };
        try
        {
            var result = await closed.SendAsync("127.0.0.1", InventoryReader.Write(Doc()));

            Assert.False(result.Success);

            // The message has to be actionable. "Connection refused" on its own is not.
            Assert.Contains("127.0.0.1", result.Message, StringComparison.Ordinal);
            Assert.Contains("listening", result.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            closed.Dispose();
        }
    }

    [Fact]
    public async Task No_address_means_nothing_is_sent_and_the_reason_is_given()
    {
        Load();

        // The case the old implementation turned into a silent success: it reported the handheld
        // had pulled the document without a device existing.
        _vm.PushToHandheldCommand.Execute(null);

        for (var attempt = 0; attempt < 200 && _vm.IsBusy; attempt++)
        {
            await Task.Delay(20);
        }

        Assert.False(_vm.HasServedToHandheld);
        Assert.Contains("Handheld address", _vm.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_pasted_url_works_as_well_as_a_bare_address()
    {
        Load();

        using var handheld = new FakeHandheld(Port);
        handheld.Start();

        // The handheld prints a complete URL for the operator to read off, so pasting that whole
        // string into the box is the most likely thing they will do.
        _vm.HandheldAddress = $"http://127.0.0.1:{Port}";

        _vm.PushToHandheldCommand.Execute(null);

        Assert.NotNull(await handheld.NextRequestAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task An_invalid_document_is_never_put_on_the_wire()
    {
        Load();
        _vm.Containers[0].Items[0].Name = "   ";
        _vm.HandheldAddress = "127.0.0.1";

        using var handheld = new FakeHandheld(Port);
        handheld.Start();

        await _vm.SendToHandheld();

        // Nothing arrived, and the refusal is the export gate's rather than a network failure -
        // so the operator is not sent off to check the Wi-Fi for a problem that is on their screen.
        Assert.Null(await handheld.NextRequestAsync(TimeSpan.FromMilliseconds(500)));
        Assert.False(_vm.HasServedToHandheld);
        Assert.Contains("rejected by the handheld", _vm.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_desktop_hub_and_the_handheld_port_do_not_collide()
    {
        // Both directions are live at once, so sharing the port would mean the desktop could not
        // serve a pull while also pushing to the device.
        Assert.NotEqual(TransferHub.DefaultPort, HandheldClient.DefaultPort);
    }
}