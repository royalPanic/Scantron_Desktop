using System.Net.Sockets;

namespace Scantron.Desktop.Services.Sync;

/// <summary>
/// One live-sync WebSocket, already upgraded, carrying UTF-8 JSON text frames.
/// </summary>
/// <remarks>
/// <para>
/// Owns the framing over a <see cref="NetworkStream"/> and nothing else - no protocol meaning, no
/// state machine. <see cref="SyncSession"/> decides what the messages mean; this decides how they
/// get onto and off the wire, which is the part that has to be byte-exact.
/// </para>
/// <para>
/// Reads are sequential and single-threaded by contract. A WebSocket is a framed stream, so two
/// concurrent readers would interleave half-frames; the session runs one read loop and hands
/// complete messages to whoever cares.
/// </para>
/// </remarks>
internal sealed class WebSocketConnection : IDisposable
{
    private readonly NetworkStream _stream;
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly byte[] _header = new byte[14];
    private bool _disposed;

    public WebSocketConnection(NetworkStream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        _stream = stream;
    }

    /// <summary>
    /// Reads one message.
    /// </summary>
    /// <returns>
    /// The text of a data frame; null when the peer closed the connection. Ping frames are answered
    /// here and transparently retried, because a keepalive is not something the session above
    /// should ever have to see.
    /// </returns>
    /// <exception cref="IOException">The frame was malformed or the connection failed.</exception>
    public async Task<string?> ReceiveAsync(CancellationToken token)
    {
        while (true)
        {
            var (opcode, payload) = await ReadFrameAsync(token).ConfigureAwait(false);

            switch (opcode)
            {
                case WebSocketFrame.TextOpcode:
                    return System.Text.Encoding.UTF8.GetString(payload);

                case WebSocketFrame.PingOpcode:
                    // RFC 6455 requires a pong carrying the ping's payload. Sent inline so ordering
                    // is preserved with respect to the messages around it.
                    await SendControlAsync(WebSocketFrame.PongOpcode, payload, token).ConfigureAwait(false);
                    continue;

                case WebSocketFrame.PongOpcode:
                    continue;

                case WebSocketFrame.CloseOpcode:
                    return null;

                default:
                    throw new IOException($"The peer used WebSocket opcode {opcode}, which live sync does not use.");
            }
        }
    }

    /// <summary>Sends one text frame.</summary>
    public async Task SendAsync(string text, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(text);

        var frame = WebSocketCodec.EncodeText(text);

        // Serialised so two callers cannot interleave two frames into one stream and produce a
        // message neither of them wrote. The session can legitimately send from a read loop and
        // from a change notification at the same time.
        await _sendGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await _stream.WriteAsync(frame.AsMemory(), token).ConfigureAwait(false);
            await _stream.FlushAsync(token).ConfigureAwait(false);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    /// <summary>Sends a ping, so a silent connection is noticed rather than assumed healthy.</summary>
    public Task PingAsync(CancellationToken token) =>
        SendControlAsync(WebSocketFrame.PingOpcode, [], token);

    /// <summary>
    /// Sends a close frame with a status code and reason, then leaves closing to the caller.
    /// </summary>
    /// <remarks>
    /// The reason travels in the close frame so the peer can show the same sentence the desktop
    /// would have shown. Without it a refused pair looks like a random disconnect on the handheld.
    /// </remarks>
    public Task CloseAsync(string reason, CancellationToken token)
    {
        var payload = new byte[2 + System.Text.Encoding.UTF8.GetByteCount(reason ?? "")];
        payload[0] = 0x03;
        payload[1] = 0xE8;
        System.Text.Encoding.UTF8.GetBytes(reason ?? "", payload.AsSpan(2));
        return SendControlAsync(WebSocketFrame.CloseOpcode, payload, token);
    }

    private async Task SendControlAsync(int opcode, byte[] payload, CancellationToken token)
    {
        var frame = WebSocketCodec.EncodeControl(opcode, payload);

        await _sendGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await _stream.WriteAsync(frame.AsMemory(), token).ConfigureAwait(false);
            await _stream.FlushAsync(token).ConfigureAwait(false);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    private async Task<(int Opcode, byte[] Payload)> ReadFrameAsync(CancellationToken token)
    {
        // Two bytes are enough to learn the length form; the rest of the header is read only once
        // the form is known, so a short frame costs one read rather than a fixed ten.
        var length = await ReadHeaderAsync(token).ConfigureAwait(false);

        var payload = new byte[length.PayloadLength];
        if (payload.Length > 0)
        {
            await ReadExactlyAsync(payload, token).ConfigureAwait(false);
        }

        if (length.Masked)
        {
            WebSocketCodec.Unmask(payload, length.MaskKey);
        }

        return (length.Opcode, payload);
    }

    private async Task<ParsedHeader> ReadHeaderAsync(CancellationToken token)
    {
        // Two bytes first: they name the length form, which is what decides how much header is
        // left. Reading a fixed ten bytes would over-read into the payload of a short frame and
        // there is no way to give those bytes back on a stream.
        await ReadIntoAsync(_header, 0, 2, token).ConfigureAwait(false);

        var len7 = _header[1] & 0x7F;
        var masked = (_header[1] & 0x80) != 0;

        var beforeMask = len7 switch
        {
            <= 125 => 2,
            126 => 4,
            _ => 10,
        };

        if (beforeMask > 2)
        {
            await ReadIntoAsync(_header, 2, beforeMask - 2, token).ConfigureAwait(false);
        }

        if (masked)
        {
            await ReadIntoAsync(_header, beforeMask, 4, token).ConfigureAwait(false);
        }

        var total = beforeMask + (masked ? 4 : 0);
        if (!WebSocketCodec.TryReadHeader(
                _header.AsSpan(0, total),
                out var opcode,
                out var payloadLength,
                out var headerLength,
                out var error))
        {
            throw new IOException(error ?? "The peer sent an unreadable WebSocket frame.");
        }

        var maskKey = new byte[4];
        if (masked && headerLength >= 4)
        {
            _header.AsSpan(headerLength - 4, 4).CopyTo(maskKey);
        }

        return new ParsedHeader(opcode, payloadLength, maskKey, masked);
    }

    private async Task ReadIntoAsync(byte[] buffer, int offset, int count, CancellationToken token)
    {
        var have = 0;
        while (have < count)
        {
            var read = await _stream.ReadAsync(buffer.AsMemory(offset + have, count - have), token).ConfigureAwait(false);
            if (read == 0)
            {
                throw new IOException("The connection closed part-way through a WebSocket frame.");
            }

            have += read;
        }
    }

    private async Task ReadExactlyAsync(byte[] buffer, CancellationToken token)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await _stream.ReadAsync(buffer.AsMemory(offset), token).ConfigureAwait(false);
            if (read == 0)
            {
                throw new IOException("The connection closed part-way through a WebSocket frame.");
            }

            offset += read;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _sendGate.Dispose();
    }

    private sealed record ParsedHeader(int Opcode, int PayloadLength, byte[] MaskKey, bool Masked);
}
