using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Scantron.Desktop.Services.Transfer;

/// <summary>One request read off the wire, already decoded.</summary>
/// <param name="Method">Verbatim HTTP method.</param>
/// <param name="Path">Verbatim request target, query string included - routing normalises it.</param>
/// <param name="Body">Decoded request body; empty when there was none.</param>
/// <param name="RemoteEndPoint">Where it came from, for the log.</param>
internal sealed record TcpHttpRequest(
    string Method,
    string Path,
    string Body,
    IPEndPoint? RemoteEndPoint);

/// <summary>
/// The HTTP/1.1 server the hub actually listens with.
/// </summary>
/// <remarks>
/// <para>
/// Written on <see cref="TcpListener"/> rather than <see cref="HttpListener"/> for one
/// concrete reason: every non-loopback <see cref="HttpListener"/> prefix needs a URL ACL
/// reservation, and reserving one needs elevation. Without that reservation the wildcard
/// (<c>http://+:8756/</c>) and every literal-address prefix are both refused with
/// <c>Access is denied</c>, so the only prefix an ordinary user can bind is
/// <c>http://127.0.0.1:8756/</c> - a hub that reports itself as sharing and that no handheld on
/// the network can reach. <see cref="TcpListener"/> has no such ACL at all: it binds
/// <c>0.0.0.0:8756</c> as any user, which is what "sharing" is supposed to mean.
/// </para>
/// <para>
/// Deliberately small and sequential. The hub serves one request at a time, the contract is
/// three routes, and no part of it needs keep-alive, pipelining or concurrency - so this
/// implements only what is used, and closes the connection after each reply.
/// </para>
/// <para>
/// The body cap is enforced on the bytes as they arrive, before decoding, so an oversized or
/// hostile request never becomes a large string in memory.
/// </para>
/// </remarks>
internal sealed class TcpHttpListener : IDisposable
{
    /// <summary>Header block ceiling. A few hundred bytes is generous; anything past this is not a handheld.</summary>
    private const int MaxHeaderBytes = 32 * 1024;

    private static readonly Dictionary<int, string> ReasonPhrases = new()
    {
        [100] = "Continue",
        [200] = "OK",
        [400] = "Bad Request",
        [404] = "Not Found",
        [405] = "Method Not Allowed",
        [413] = "Content Too Large",
        [417] = "Expectation Failed",
        [431] = "Request Header Fields Too Large",
        [500] = "Internal Server Error",
        [503] = "Service Unavailable",
    };

    private readonly int _port;
    private readonly int _maxBodyBytes;
    private TcpListener? _listener;

    public TcpHttpListener(int port, int maxBodyBytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(port, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65535);
        ArgumentOutOfRangeException.ThrowIfNegative(maxBodyBytes);

        _port = port;
        _maxBodyBytes = maxBodyBytes;
    }

    /// <summary>
    /// Binds every interface, or throws.
    /// </summary>
    /// <remarks>
    /// Binding to <see cref="IPAddress.Any"/> rather than to the enumerated addresses: it needs
    /// no ACL, it survives the machine changing networks mid-shift, and it is what makes a port
    /// already taken by another copy actually fail. <see cref="Socket.ExclusiveAddressUse"/> is
    /// set so that second case is reported instead of silently hijacked - without it Windows
    /// lets a second process bind over a live listener, and two hubs on one port is precisely the
    /// situation the operator needs told about.
    /// </remarks>
    public void Start()
    {
        var listener = new TcpListener(IPAddress.Any, _port);
        listener.Server.ExclusiveAddressUse = true;

        try
        {
            listener.Start();
        }
        catch (SocketException ex)
        {
            listener.Dispose();

            // The port being taken is the likeliest cause by a wide margin - another copy of this
            // app is already sharing - so it is named rather than left to be inferred from a
            // Windows error code the operator has never seen.
            throw new IOException(
                $"the port {_port} is not available - {ex.Message} (Windows error {ex.NativeErrorCode}). " +
                "Another copy of Scantron may already be sharing.",
                ex);
        }

        _listener = listener;
    }

    /// <summary>
    /// Accepts and serves connections one at a time until stopped.
    /// </summary>
    /// <remarks>
    /// Sequential on purpose - see the class remarks. A client that vanishes mid-request is an
    /// ordinary event in a warehouse, so every per-connection failure is logged and the loop
    /// continues: one scanner walking out of range must not take sharing down with it.
    /// </remarks>
    public async Task RunAsync(
        Func<TcpHttpRequest, CancellationToken, Task<HubResponse>> handler,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(handler);

        var listener = _listener ?? throw new InvalidOperationException("The listener has not been started.");

        while (!token.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                // The normal way out: Stop() closed the listener out from under the accept.
                if (!token.IsCancellationRequested)
                {
                    Log.Warning($"The transfer hub stopped accepting requests: {ex.Message}");
                }

                break;
            }

            using (client)
            {
                try
                {
                    await ServeAsync(client, handler, token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException)
                {
                    Log.Warning($"A hub connection failed: {ex.Message}");
                }
            }
        }
    }

    private async Task ServeAsync(
        TcpClient client,
        Func<TcpHttpRequest, CancellationToken, Task<HubResponse>> handler,
        CancellationToken token)
    {
        // Scanner hardware is latency-sensitive and the request is a single small round trip,
        // so Nagle's algorithm would only add delay to a reply that is already one write.
        client.NoDelay = true;

        var stream = client.GetStream();
        var request = await ReadRequestAsync(stream, token).ConfigureAwait(false);

        if (request.Headers.TryGetValue("expect", out var expectation) &&
            expectation.Contains("100-continue", StringComparison.OrdinalIgnoreCase))
        {
            // Answer before the body is read, so a sender that would have streamed a megabyte it
            // should not have can stop. An oversized Content-Length never gets here: the 413 is
            // written first and the connection closed.
            if (request.OverBodyLimit)
            {
                await WriteAsync(stream, TooLarge(), token).ConfigureAwait(false);
                return;
            }

            await WriteContinueAsync(stream, token).ConfigureAwait(false);
        }

        if (request.OverBodyLimit)
        {
            Log.Warning($"Refused a request from {Describe(client)}: body larger than {_maxBodyBytes / (1024 * 1024)} MB");
            await WriteAsync(stream, TooLarge(), token).ConfigureAwait(false);
            return;
        }

        var response = await handler(
            new TcpHttpRequest(request.Method, request.Path, request.Body, client.Client.RemoteEndPoint as IPEndPoint),
            token).ConfigureAwait(false);

        await WriteAsync(stream, response, token).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads the request line, headers and body.
    /// </summary>
    /// <remarks>
    /// Headers are read a byte at a time because the body begins immediately after the blank
    /// line with no framing of its own - buffering ahead would consume body bytes that then have
    /// to be handed back. The body itself is read in bulk.
    /// </remarks>
    private async Task<ParsedRequest> ReadRequestAsync(NetworkStream stream, CancellationToken token)
    {
        var headerBytes = new List<byte>(512);

        // Loop invariant: headerBytes never contains the terminating "\r\n\r\n", so peeking at
        // the final four is safe without bounds arithmetic.
        while (true)
        {
            var b = await ReadByteAsync(stream, token).ConfigureAwait(false);
            if (b is null)
            {
                // Client connected and left without sending anything - a port scanner or a
                // half-open connection, not a transfer.
                throw new IOException("The connection closed before a request was sent.");
            }

            headerBytes.Add(b.Value);

            var count = headerBytes.Count;
            if (count >= 4 &&
                headerBytes[count - 4] == '\r' && headerBytes[count - 3] == '\n' &&
                headerBytes[count - 2] == '\r' && headerBytes[count - 1] == '\n')
            {
                break;
            }

            if (count > MaxHeaderBytes)
            {
                await WriteAsync(stream, new HubResponse(431, HubResponse.Text, "Request headers are too large."), token).ConfigureAwait(false);
                throw new IOException("Refused a request whose headers exceeded the limit.");
            }
        }

        var headerText = Encoding.Latin1.GetString(headerBytes.ToArray());
        var lines = headerText.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        var parts = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length < 2)
        {
            await WriteAsync(stream, new HubResponse(400, HubResponse.Text, "Malformed request line."), token).ConfigureAwait(false);
            throw new IOException("Refused a request with a malformed request line.");
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 1; i < lines.Length; i++)
        {
            var colon = lines[i].IndexOf(':');
            if (colon > 0)
            {
                headers[lines[i][..colon].Trim()] = lines[i][(colon + 1)..].Trim();
            }
        }

        var charset = CharsetOf(headers.GetValueOrDefault("content-type"));
        var overLimit = false;
        string body;

        if (headers.GetValueOrDefault("transfer-encoding") is { } encoding &&
            encoding.Contains("chunked", StringComparison.OrdinalIgnoreCase))
        {
            body = await ReadChunkedBodyAsync(stream, charset, token).ConfigureAwait(false);
        }
        else if (headers.TryGetValue("content-length", out var rawLength) &&
            long.TryParse(rawLength, out var length) &&
            length > 0)
        {
            if (length > _maxBodyBytes)
            {
                // Counted before reading so the bytes never accumulate.
                overLimit = true;
                body = "";
            }
            else
            {
                var buffer = new byte[length];
                await ReadExactlyAsync(stream, buffer, token).ConfigureAwait(false);
                body = charset.GetString(buffer);
            }
        }
        else
        {
            body = "";
        }

        return new ParsedRequest(parts[0].ToUpperInvariant(), parts[1], body, headers, overLimit);
    }
    private async Task<string> ReadChunkedBodyAsync(NetworkStream stream, Encoding charset, CancellationToken token)
    {
        var body = new MemoryStream();

        while (true)
        {
            var line = await ReadLineAsync(stream, token).ConfigureAwait(false);
            var sizeText = line.Split(';', StringSplitOptions.RemoveEmptyEntries)[0].Trim();

            if (!int.TryParse(sizeText, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var size))
            {
                throw new IOException("Malformed chunked request body.");
            }

            if (size == 0)
            {
                // Trailer section, terminated by its own blank line.
                do
                {
                    line = await ReadLineAsync(stream, token).ConfigureAwait(false);
                }
                while (line.Length > 0);

                break;
            }

            if (body.Length + size > _maxBodyBytes)
            {
                throw new IOException("The chunked request body exceeded the limit.");
            }

            var chunk = new byte[size];
            await ReadExactlyAsync(stream, chunk, token).ConfigureAwait(false);
            body.Write(chunk, 0, size);

            await ReadLineAsync(stream, token).ConfigureAwait(false);
        }

        return charset.GetString(body.ToArray());
    }

    private static async Task<byte?> ReadByteAsync(NetworkStream stream, CancellationToken token)
    {
        var buffer = new byte[1];
        var read = await stream.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false);
        return read == 0 ? null : buffer[0];
    }

    private static async Task<string> ReadLineAsync(NetworkStream stream, CancellationToken token)
    {
        var bytes = new List<byte>(32);

        while (true)
        {
            var b = await ReadByteAsync(stream, token).ConfigureAwait(false);
            if (b is null)
            {
                throw new IOException("The connection closed part-way through a request.");
            }

            if (b == '\n')
            {
                if (bytes.Count > 0 && bytes[^1] == '\r')
                {
                    bytes.RemoveAt(bytes.Count - 1);
                }

                return Encoding.Latin1.GetString(bytes.ToArray());
            }

            bytes.Add(b.Value);
        }
    }

    private static async Task ReadExactlyAsync(NetworkStream stream, byte[] buffer, CancellationToken token)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset), token).ConfigureAwait(false);
            if (read == 0)
            {
                throw new IOException("The connection closed part-way through the request body.");
            }

            offset += read;
        }
    }

    /// <summary>
    /// Honours an explicit <c>charset</c>, and otherwise reads UTF-8.
    /// </summary>
    /// <remarks>
    /// The handheld always sends UTF-8, and every device that writes a Scantron document writes
    /// it as UTF-8. Unknown charsets fall back rather than fail, because refusing a push over an
    /// encoding that would have decoded correctly is a worse outcome than the alternative.
    /// </remarks>
    private static Encoding CharsetOf(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
        {
            return Encoding.UTF8;
        }

        foreach (var part in contentType.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (part.StartsWith("charset=", StringComparison.OrdinalIgnoreCase))
            {
                var name = part["charset=".Length..].Trim('"');
                try
                {
                    return Encoding.GetEncoding(name);
                }
                catch (ArgumentException)
                {
                    return Encoding.UTF8;
                }
            }
        }

        return Encoding.UTF8;
    }

    private HubResponse TooLarge() =>
        new(413, HubResponse.Text, $"This desktop accepts transfers up to {_maxBodyBytes / (1024 * 1024)} MB.");

    /// <summary>Sends the interim <c>100 Continue</c> so the sender may proceed with its body.</summary>
    private static Task WriteContinueAsync(NetworkStream stream, CancellationToken token) =>
        WriteRawAsync(stream, "HTTP/1.1 100 Continue\r\n\r\n", token);

    /// <summary>
    /// Writes the reply and leaves closing to the caller, which owns the socket.
    /// </summary>
    /// <remarks>
    /// Always length-delimited and always <c>Connection: close</c>. The handheld sends one
    /// request at a time, so there is nothing to gain from reuse - and a wrong or missing
    /// Content-Length is the classic way an HTTP client ends up reading somebody else's reply.
    /// </remarks>
    private static async Task WriteAsync(NetworkStream stream, HubResponse response, CancellationToken token)
    {
        var body = Encoding.UTF8.GetBytes(response.Body);
        var reason = ReasonPhrases.TryGetValue(response.StatusCode, out var known) ? known : "Unknown";
        var contentType = string.IsNullOrWhiteSpace(response.ContentType) ? HubResponse.Text : response.ContentType;

        var head = new StringBuilder()
            .Append("HTTP/1.1 ").Append(response.StatusCode).Append(' ').Append(reason).Append("\r\n")
            .Append("Content-Type: ").Append(contentType).Append("\r\n")
            .Append("Content-Length: ").Append(body.Length).Append("\r\n")
            .Append("Connection: close\r\n\r\n")
            .ToString();

        await WriteRawAsync(stream, head, token).ConfigureAwait(false);
        await stream.WriteAsync(body.AsMemory(), token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);
    }

    private static Task WriteRawAsync(NetworkStream stream, string text, CancellationToken token) =>
        stream.WriteAsync(Encoding.Latin1.GetBytes(text).AsMemory(), token).AsTask();

    private static string Describe(TcpClient client) =>
        (client.Client.RemoteEndPoint as IPEndPoint)?.ToString() ?? "an unknown address";

    /// <summary>Stops accepting and releases the port.</summary>
    public void Stop()
    {
        _listener?.Stop();
        _listener = null;
    }

    public void Dispose() => Stop();

    private sealed record ParsedRequest(
        string Method,
        string Path,
        string Body,
        Dictionary<string, string> Headers,
        bool OverBodyLimit);
}
