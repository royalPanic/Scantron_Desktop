using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Scantron.Desktop.Services.Sync;

/// <summary>One WebSocket frame read off the wire.</summary>
/// <param name="Opcode">1 = text, 8 = close, 9 = ping, 10 = pong.</param>
/// <param name="Payload">Decoded payload bytes, unmasked.</param>
internal sealed record WebSocketFrame(int Opcode, byte[] Payload)
{
    /// <summary>Text payload, decoded as UTF-8. Empty for a control frame.</summary>
    public string Text => Payload.Length == 0 ? "" : Encoding.UTF8.GetString(Payload);

    /// <summary>Opcode for a text frame.</summary>
    public const int TextOpcode = 1;

    public const int CloseOpcode = 8;
    public const int PingOpcode = 9;
    public const int PongOpcode = 10;
}

/// <summary>
/// The RFC 6455 bits of live sync: the opening handshake and the frame codec.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately hand-written and deliberately small. The live-sync channel carries UTF-8 JSON text
/// frames between two machines on one warehouse LAN; it needs no extensions, no compression, no
/// subprotocols and no fragmentation. Everything outside that subset is <em>refused</em> rather
/// than silently mishandled, because a partially-understood WebSocket is far more dangerous than an
/// obviously unsupported one.
/// </para>
/// <para>
/// The whole type is a set of pure functions over byte arrays. That is the same choice
/// <c>HubEndpoints</c> makes and for the same reason: masking, all three length forms and the
/// control-frame rules are where a WebSocket implementation is actually wrong, and they are all
/// testable here without binding a port or opening a socket.
/// </para>
/// <para>
/// <c>System.Net.WebSockets</c> is not used because its server side needs an <c>HttpListener</c>,
/// which needs a URL ACL reservation - the exact restriction <c>TcpHttpListener</c> exists to
/// avoid.
/// </para>
/// </remarks>
internal static class WebSocketCodec
{
    /// <summary>Handshake magic value, fixed by RFC 6455 section 1.3.</summary>
    private const string HandshakeGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

    /// <summary>Largest inbound message accepted, mirroring <c>HubEndpoints.MaxBodyBytes</c>.</summary>
    public const int MaxMessageBytes = 16 * 1024 * 1024;

    /// <summary>
    /// Builds the <c>101 Switching Protocols</c> reply for a valid upgrade request.
    /// </summary>
    /// <remarks>
    /// Returns null when the request is not a usable WebSocket upgrade, so the caller can answer it
    /// as an ordinary HTTP request instead. Accepting a malformed upgrade would produce a
    /// connection that looks open on both sides and carries nothing.
    /// </remarks>
    public static string? AcceptHandshake(IReadOnlyDictionary<string, string> headers)
    {
        ArgumentNullException.ThrowIfNull(headers);

        if (!headers.TryGetValue("upgrade", out var upgrade) ||
            !upgrade.Contains("websocket", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (headers.TryGetValue("connection", out var connection) &&
            !connection.Contains("Upgrade", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (!headers.TryGetValue("sec-websocket-key", out var key) || !IsValidKey(key))
        {
            return null;
        }

        // Version must be 13. A client offering 8 speaks a draft the framing rules changed under,
        // and answering it with 13-era frames would corrupt every message.
        if (headers.TryGetValue("sec-websocket-version", out var version) &&
            !string.Equals(version.Trim(), "13", StringComparison.Ordinal))
        {
            return null;
        }

        var accept = Convert.ToBase64String(
            SHA1.HashData(Encoding.ASCII.GetBytes(key.Trim() + HandshakeGuid)));

        return "HTTP/1.1 101 Switching Protocols\r\n" +
               "Upgrade: websocket\r\n" +
               "Connection: Upgrade\r\n" +
               $"Sec-WebSocket-Accept: {accept}\r\n\r\n";
    }

    /// <summary>
    /// A base64 value that decodes to exactly 16 bytes, which is what RFC 6455 requires of the key.
    /// </summary>
    private static bool IsValidKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return false;
        }

        Span<byte> decoded = stackalloc byte[32];
        return Convert.TryFromBase64String(key.Trim(), decoded, out var written) && written == 16;
    }

    /// <summary>Encodes one unmasked text frame, the shape a server sends.</summary>
    public static byte[] EncodeText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return Encode(WebSocketFrame.TextOpcode, Encoding.UTF8.GetBytes(text));
    }

    /// <summary>Encodes one unmasked control frame.</summary>
    public static byte[] EncodeControl(int opcode, byte[] payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        if (payload.Length > 125)
        {
            throw new ArgumentException("A control frame payload cannot exceed 125 bytes.", nameof(payload));
        }

        return Encode(opcode, payload);
    }

    private static byte[] Encode(int opcode, byte[] payload)
    {
        // FIN is always set: this implementation never fragments, so a frame is always a whole
        // message. RSV1-3 stay clear because no extension was negotiated.
        var headerLength = payload.Length switch
        {
            <= 125 => 2,
            <= ushort.MaxValue => 4,
            _ => 10,
        };

        var frame = new byte[headerLength + payload.Length];
        frame[0] = (byte)(0x80 | (opcode & 0x0F));

        switch (payload.Length)
        {
            case <= 125:
                frame[1] = (byte)payload.Length;
                break;

            case <= ushort.MaxValue:
                frame[1] = 126;
                BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(2), (ushort)payload.Length);
                break;

            default:
                frame[1] = 127;
                BinaryPrimitives.WriteInt64BigEndian(frame.AsSpan(2), payload.Length);
                break;
        }

        payload.CopyTo(frame.AsSpan(headerLength));
        return frame;
    }

    /// <summary>
    /// Reads the header of one frame, reporting how many bytes it will need in total.
    /// </summary>
    /// <remarks>
    /// Two-phase on purpose. The serve loop owns the socket and reads from a stream, so it can peek
    /// a small header and then know exactly how much payload to pull - rather than over-reading and
    /// having to hand bytes back.
    /// </remarks>
    public static bool TryReadHeader(
        ReadOnlySpan<byte> header,
        out int opcode,
        out int payloadLength,
        out int headerLength,
        out string? error)
    {
        opcode = 0;
        payloadLength = 0;
        headerLength = 0;
        error = null;

        if (header.Length < 2)
        {
            error = "The peer sent a WebSocket frame that is too short to be one.";
            return false;
        }

        var first = header[0];
        var second = header[1];

        if ((first & 0x70) != 0)
        {
            // RSV bits are only legal when an extension was negotiated, and none is.
            error = "The peer used a WebSocket extension this desktop did not negotiate.";
            return false;
        }

        if ((first & 0x80) == 0)
        {
            // Fragmentation would need a reassembly buffer and a continuation state machine for no
            // benefit - every frame this protocol sends is a whole, small JSON message.
            error = "The peer sent a fragmented WebSocket message, which live sync does not use.";
            return false;
        }

        opcode = first & 0x0F;
        var masked = (second & 0x80) != 0;
        var length = second & 0x7F;

        var cursor = 2;

        switch (length)
        {
            case 126:
                if (header.Length < 4)
                {
                    error = "The peer sent a truncated WebSocket frame header.";
                    return false;
                }

                length = BinaryPrimitives.ReadUInt16BigEndian(header[2..]);
                cursor = 4;
                break;

            case 127:
                if (header.Length < 10)
                {
                    error = "The peer sent a truncated WebSocket frame header.";
                    return false;
                }

                var wide = BinaryPrimitives.ReadInt64BigEndian(header[2..]);
                if (wide < 0 || wide > MaxMessageBytes)
                {
                    error = $"The peer announced a WebSocket message larger than {MaxMessageBytes / (1024 * 1024)} MB.";
                    return false;
                }

                length = (int)wide;
                cursor = 10;
                break;
        }

        if (length > MaxMessageBytes)
        {
            error = $"The peer announced a WebSocket message larger than {MaxMessageBytes / (1024 * 1024)} MB.";
            return false;
        }

        if (opcode >= 8)
        {
            // Control frames must be small and must never be fragmented; both are already enforced
            // above, so only the size rule is left.
            if (length > 125)
            {
                error = "The peer sent a WebSocket control frame that is too large.";
                return false;
            }
        }

        if (masked)
        {
            // Every message from a client is masked - it is what stops a client's bytes being
            // interpreted as a request by an intermediary that shares the connection.
            cursor += 4;
        }

        // A client-to-server frame must be masked. Accepting an unmasked one would be lenient in a
        // way that hides a broken client until it meets a strict one.
        if (!masked)
        {
            error = "The peer sent an unmasked WebSocket frame, which a client must never do.";
            return false;
        }

        headerLength = cursor;
        payloadLength = length;
        return true;
    }

    /// <summary>Unmasks a payload in place, using the four-byte key that followed the header.</summary>
    public static void Unmask(Span<byte> payload, ReadOnlySpan<byte> key)
    {
        if (key.Length != 4)
        {
            throw new ArgumentException("A WebSocket mask key is four bytes.", nameof(key));
        }

        for (var i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)(payload[i] ^ key[i % 4]);
        }
    }
}
