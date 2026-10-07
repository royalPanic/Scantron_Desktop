using System.Buffers.Binary;
using System.Text;
using Scantron.Desktop.Services.Sync;
using Xunit;

namespace Scantron.Desktop.Tests;

/// <summary>
/// Pins the RFC 6455 details live sync actually depends on. The Android client is written against
/// these same byte shapes, so a change here has to be made there too.
/// </summary>
public sealed class WebSocketCodecTests
{
    private static Dictionary<string, string> UpgradeHeaders() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["upgrade"] = "websocket",
        ["connection"] = "Upgrade",
        ["sec-websocket-key"] = Convert.ToBase64String(Encoding.ASCII.GetBytes("0123456789abcdef")),
        ["sec-websocket-version"] = "13",
    };

    [Fact]
    public void A_valid_upgrade_is_accepted_with_the_rfc_accept_value()
    {
        // The canonical example from RFC 6455 section 1.3, so this is pinned to the published value
        // rather than to whatever this implementation happens to produce. The Android client pins
        // the same vector: agreement on the handshake is agreement between two independent codecs,
        // not one codec agreeing with itself.
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["upgrade"] = "websocket",
            ["connection"] = "Upgrade",
            ["sec-websocket-key"] = "dGhlIHNhbXBsZSBub25jZQ==",
            ["sec-websocket-version"] = "13",
        };

        var accept = WebSocketCodec.AcceptHandshake(headers);

        Assert.NotNull(accept);
        Assert.StartsWith("HTTP/1.1 101", accept);
        Assert.Contains("Sec-WebSocket-Accept: s3pPLMBiTxaQ9kYGzzhZRbK+xOo=", accept);
        Assert.Contains("Upgrade: websocket", accept);
    }

    [Fact]
    public void An_upgrade_without_the_magic_key_is_refused()
    {
        var headers = UpgradeHeaders();
        headers.Remove("sec-websocket-key");

        Assert.Null(WebSocketCodec.AcceptHandshake(headers));
    }

    [Fact]
    public void An_upgrade_with_a_key_of_the_wrong_length_is_refused()
    {
        var headers = UpgradeHeaders();
        headers["sec-websocket-key"] = Convert.ToBase64String(Encoding.ASCII.GetBytes("too-short"));

        Assert.Null(WebSocketCodec.AcceptHandshake(headers));
    }

    [Fact]
    public void An_upgrade_offering_a_draft_version_is_refused()
    {
        // Version 8 frames differ from 13. Answering it with 13-era framing would corrupt every
        // message on the connection.
        var headers = UpgradeHeaders();
        headers["sec-websocket-version"] = "8";

        Assert.Null(WebSocketCodec.AcceptHandshake(headers));
    }

    [Fact]
    public void A_plain_request_is_not_treated_as_an_upgrade()
    {
        Assert.Null(WebSocketCodec.AcceptHandshake(new Dictionary<string, string>()));
    }

    [Fact]
    public void A_short_text_frame_uses_the_seven_bit_length()
    {
        var frame = WebSocketCodec.EncodeText("hi");

        Assert.Equal(0x81, frame[0]);
        Assert.Equal(2, frame[1]);
        Assert.Equal("hi", Encoding.UTF8.GetString(frame[2..]));
    }

    [Fact]
    public void A_medium_text_frame_uses_the_sixteen_bit_length()
    {
        var text = new string('a', 200);
        var frame = WebSocketCodec.EncodeText(text);

        Assert.Equal(126, frame[1]);
        Assert.Equal(200, BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(2)));
        Assert.Equal(4 + 200, frame.Length);
    }

    [Fact]
    public void A_large_text_frame_uses_the_sixty_four_bit_length()
    {
        var text = new string('a', 70_000);
        var frame = WebSocketCodec.EncodeText(text);

        Assert.Equal(127, frame[1]);
        Assert.Equal(70_000, BinaryPrimitives.ReadInt64BigEndian(frame.AsSpan(2)));
        Assert.Equal(10 + 70_000, frame.Length);
    }

    [Fact]
    public void A_masked_header_is_read_and_the_payload_unmasked()
    {
        // The same fixed vector the Android codec's test uses - "Hi" with mask 01 02 03 04. Both
        // sides pinning one byte array is what makes drift between two independent implementations
        // show up as a failure here rather than as a session that will not open in a warehouse.
        var payload = Encoding.UTF8.GetBytes("Hi");
        var frame = new byte[] { 0x81, 0x82, 0x01, 0x02, 0x03, 0x04, (byte)'H' ^ 0x01, (byte)'i' ^ 0x02 };

        Assert.True(WebSocketCodec.TryReadHeader(frame, out var opcode, out var length, out var headerLength, out var error), error);
        Assert.Equal(WebSocketFrame.TextOpcode, opcode);
        Assert.Equal(2, length);
        Assert.Equal(6, headerLength);
        Assert.Equal(8, frame.Length);

        var received = (byte[])frame[headerLength..(headerLength + length)];
        WebSocketCodec.Unmask(received, frame.AsSpan(2, 4));

        Assert.Equal(payload, received);
    }

    [Fact]
    public void A_masked_header_of_five_bytes_is_read_and_unmasked()
    {
        var payload = Encoding.UTF8.GetBytes("hello");
        var key = new byte[] { 0x01, 0x02, 0x03, 0x04 };

        var frame = new byte[2 + 4 + payload.Length];
        frame[0] = 0x81;
        frame[1] = (byte)(0x80 | payload.Length);
        key.CopyTo(frame, 2);
        for (var i = 0; i < payload.Length; i++)
        {
            frame[6 + i] = (byte)(payload[i] ^ key[i % 4]);
        }

        Assert.True(WebSocketCodec.TryReadHeader(frame, out var opcode, out var length, out var headerLength, out var error), error);
        Assert.Equal(WebSocketFrame.TextOpcode, opcode);
        Assert.Equal(5, length);
        Assert.Equal(6, headerLength);

        var received = (byte[])frame[headerLength..(headerLength + length)];
        WebSocketCodec.Unmask(received, frame.AsSpan(2, 4));

        Assert.Equal("hello", Encoding.UTF8.GetString(received));
    }

    [Fact]
    public void An_unmasked_client_frame_is_refused()
    {
        // A client must mask. Accepting an unmasked frame is lenient in a way that hides a broken
        // client until it meets a strict one.
        var frame = new byte[] { 0x81, 0x02, (byte)'h', (byte)'i' };

        Assert.False(WebSocketCodec.TryReadHeader(frame, out _, out _, out _, out var error));
        Assert.Contains("unmasked", error);
    }

    [Fact]
    public void A_fragmented_frame_is_refused()
    {
        var frame = new byte[] { 0x01, 0x80, 0x00, 0x00, 0x00, 0x00 };

        Assert.False(WebSocketCodec.TryReadHeader(frame, out _, out _, out _, out var error));
        Assert.Contains("fragmented", error);
    }

    [Fact]
    public void A_reserved_bit_is_refused()
    {
        var frame = new byte[] { 0xC1, 0x80, 0x00, 0x00, 0x00, 0x00 };

        Assert.False(WebSocketCodec.TryReadHeader(frame, out _, out _, out _, out var error));
        Assert.Contains("extension", error);
    }

    [Fact]
    public void An_oversized_frame_is_refused_before_the_payload_is_read()
    {
        // Announced length is checked up front, so a hostile client cannot make the desktop
        // allocate a gigabyte by lying in the header.
        var frame = new byte[10];
        frame[0] = 0x81;
        frame[1] = 127;
        BinaryPrimitives.WriteInt64BigEndian(frame.AsSpan(2), 1024L * 1024 * 1024);

        Assert.False(WebSocketCodec.TryReadHeader(frame, out _, out _, out _, out var error));
        Assert.Contains("larger than", error);
    }

    [Fact]
    public void An_oversized_control_frame_is_refused()
    {
        var payload = new byte[126];
        Assert.Throws<ArgumentException>(() => WebSocketCodec.EncodeControl(WebSocketFrame.PingOpcode, payload));
    }

    [Fact]
    public void A_ping_round_trips_through_the_control_encoding()
    {
        var frame = WebSocketCodec.EncodeControl(WebSocketFrame.PingOpcode, [1, 2, 3]);

        Assert.Equal(0x89, frame[0]);
        Assert.Equal(3, frame[1]);
    }
}
