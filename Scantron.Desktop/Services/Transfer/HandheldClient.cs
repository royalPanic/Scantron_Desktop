using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;

namespace Scantron.Desktop.Services.Transfer;

/// <summary>
/// What a push to a handheld produced.
/// </summary>
/// <remarks>
/// The same shape as <see cref="HubResponse"/> on the way in: a success carries something to say,
/// a failure carries a sentence an operator can act on. Deliberately not an exception - the
/// ordinary outcomes of "the scanner was not listening" and "the scanner is on a different
/// network" are the common cases in a warehouse, and neither is a fault in the desktop.
/// </remarks>
public sealed record HandheldResult(bool Success, string Message)
{
    public static HandheldResult Ok(string message) => new(true, message);

    public static HandheldResult Fail(string message) => new(false, message);
}

/// <summary>
/// Pushes the desktop's document to a handheld that is listening for it.
/// </summary>
/// <remarks>
/// <para>
/// This is the reverse direction of <see cref="TransferHub"/>. The hub exists because the CK65
/// cannot be relied on to hold a listening socket while docked or asleep, so the desktop hosts
/// and the device connects. This type is the mirror image: the operator presses a button on the
/// desk and the desktop dials the scanner, which is listening on a port it opened itself.
/// </para>
/// <para>
/// Neither side is privileged. The desktop-initiated direction exists because the person standing
/// at the desk with a merged document is not always the person holding the scanner, and "get your
/// data off this PC" has to work even when nobody thinks to go and find the handheld's screen.
/// </para>
/// <para>
/// Built on <see cref="HttpClient"/> rather than a hand-rolled socket, and for one reason only:
/// the request-handling halves of this app are already exercised without a socket by keeping the
/// decision in a pure function. Here the decision is thin enough that a real client - which gets
/// keep-alive, redirect, header and chunked-encoding handling right for free - is the better
/// trade. The wire format is still the raw export document, byte for byte, exactly as it would be
/// written to a USB stick.
/// </para>
/// </remarks>
public sealed class HandheldClient : IDisposable
{
    /// <summary>Port the handheld listens on for a desktop-initiated push.</summary>
    /// <remarks>
    /// Distinct from the desktop's own 8756 and from the 8757 discovery probe port. All three
    /// are fixed by the contract and are not operator-configurable: a second copy of the app
    /// holding the wrong port is a failure mode worth more than the flexibility.
    /// </remarks>
    public const int DefaultPort = 8758;

    /// <summary>
    /// How long to wait for the handheld to answer.
    /// </remarks>
    /// <remarks>
    /// The device stages the document and answers as soon as it is parsed, so this is a
    /// database-free operation in practice. It is generous because the failure it guards against
    /// is a warehouse dead spot rather than a slow disk, and a long wait there costs less than a
    /// false "it failed" on a transfer that would have worked.
    /// </remarks>
    public const int DefaultTimeoutMs = 15_000;

    /// <summary>
    /// Largest document accepted for sending, mirroring the hub's inbound cap.
    /// </summary>
    /// <remarks>
    /// The same 16 MB ceiling <see cref="HubEndpoints.MaxBodyBytes"/> enforces on the way in. A
    /// document the handheld would refuse to read is not worth putting on the wire, and the
    /// export gate catches the realistic cases well below this.
    /// </remarks>
    public const int MaxDocumentBytes = 16 * 1024 * 1024;

    private readonly HttpClient _http;

    public HandheldClient(int timeoutMs = DefaultTimeoutMs)
    {
        _http = new HttpClient(new SocketsHttpHandler
        {
            // No keep-alive: a push is one request to one device, and a pooled connection held
            // against a handheld that has since walked out of range is a socket that eventually
            // blocks the next push behind a stale one.
            PooledConnectionLifetime = TimeSpan.Zero,

                // A desktop on a corporate network is frequently configured with a proxy it cannot
                // reach, and HttpClient honours that by default. The result is a push failing with a
                // proxy error naming a host the operator never typed. A scanner on the warehouse LAN
                // is always addressed directly.
                UseProxy = false,
            })
            {
                Timeout = TimeSpan.FromMilliseconds(timeoutMs),
            };

            _http.DefaultRequestHeaders.Accept.ParseAdd(HubResponse.Json);
        }

    /// <summary>Port the desktop pushes to.</summary>
    public int Port { get; init; } = DefaultPort;

    /// <summary>
    /// Sends <paramref name="document"/> to <paramref name="rawHost"/>.
    /// </summary>
    /// <remarks>
    /// Never throws for a network reason. Every outcome arrives as a
    /// <see cref="HandheldResult"/> with a sentence that names what to check, because "the send
    /// button did nothing" is the failure this whole type exists to prevent.
    /// </remarks>
    public async Task<HandheldResult> SendAsync(string rawHost, string document, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(document);

        if (SanitizeHost(rawHost) is not { } host)
        {
            return HandheldResult.Fail(
                "No handheld address is set. Start listening on the CK65 and type the address it " +
                "shows into the Handheld address box.");
        }

        var bytes = Encoding.UTF8.GetByteCount(document);
        if (bytes > MaxDocumentBytes)
        {
            return HandheldResult.Fail(
                $"This document is {bytes:N0} bytes, which is over the {MaxDocumentBytes:N0} byte " +
                "transfer limit. Export it to a file instead.");
        }

        var endpoint = $"http://{host}:{Port}{Path}";

        // StringContent, because the body is the export document and the device's parser expects
        // UTF-8 JSON. The bytes are what went through BuildDocument; nothing here reformats them.
        using var content = new StringContent(document, Encoding.UTF8, "application/json");

        try
        {
            using var response = await _http.PostAsync(endpoint, content, token).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);

            return Interpret((int)response.StatusCode, body, host);
        }
        catch (TaskCanceledException) when (!token.IsCancellationRequested)
        {
            // HttpClient surfaces its own timeout as a cancellation, so the two are told apart by
            // whether the caller asked for it. This one is the handheld not answering in time.
            Log.Warning($"A push to {host}:{Port} timed out");
            return HandheldResult.Fail(
                $"No answer from the handheld at {host}:{Port}. Check that the address is right, " +
                "that both devices are on the same Wi-Fi, and that the Transfer screen is still " +
                "listening.");
        }
        catch (HttpRequestException ex)
        {
            // The class of failure that has no useful inner detail: refused, reset, unroutable.
            // Logged, because on a warehouse network this is usually the only record that the
            // push was ever attempted.
            Log.Warning($"A push to {host}:{Port} failed to connect: {ex.Message}");
            return HandheldResult.Fail(
                $"Could not reach the handheld at {host}:{Port}. Check the address, the network, " +
                "and that the Transfer screen has been left open on the device.");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            Log.Error("A push to the handheld failed", ex);
            return HandheldResult.Fail($"The transfer could not be completed: {ex.Message}");
        }
    }

    /// <summary>
    /// Turns a status code and body into something to show.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="SendAsync"/> and free of any socket so the contract can be pinned
    /// by tests without a device on the network - the same reason
    /// <see cref="HubEndpoints.Handle"/> is a pure function. The handheld's own plain-text reason
    /// is preferred over anything invented here, because it is written by the code that actually
    /// refused the document and knows why.
    /// </remarks>
    internal static HandheldResult Interpret(int statusCode, string body, string host)
    {
        if (statusCode is >= 200 and <= 299)
        {
            var counts = Counts(body);
            return counts is { } counted
                ? HandheldResult.Ok(
                    $"Sent {counted.Containers} container(s) and {counted.Items} item(s) to the handheld.")
                : HandheldResult.Ok("Sent to the handheld.");
        }

        var reason = body?.Trim();

        return HandheldResult.Fail(
            string.IsNullOrEmpty(reason)
                ? $"The handheld refused the transfer (HTTP {statusCode}). It is listening, but it " +
                  "did not accept the document."
                : $"The handheld said: {reason}");
    }

    /// <summary>
    /// Reads the counts out of an acknowledgement, or null when the body is not one.
    /// </summary>
    /// <remarks>
    /// Hand-parsed off two well-known field names rather than deserialized into a DTO, so a
    /// rename on either side surfaces here as a missing count rather than as a silently empty
    /// object. The counts are for the operator's benefit only; the transfer already succeeded by
    /// the time this runs, so an unreadable body is not treated as a failure.
    /// </remarks>
    private static (int Containers, int Items)? Counts(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        var containers = Field(body, "containers");
        var items = Field(body, "items");

        return containers is { } c && items is { } i ? (c, i) : null;
    }

    private static int? Field(string body, string name)
    {
        var match = Regex.Match(body, $"\"{name}\"\\s*:\\s*(\\d+)");
        return match.Success && int.TryParse(match.Groups[1].Value, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    /// <summary>The path the handheld serves. Fixed by the contract.</summary>
    private const string Path = "/receive";

        /// <summary>Matches a trailing <c>:&lt;digits&gt;</c> - the port the device happens to display.</summary>
        private static readonly Regex TrailingPort = new(@":\d+$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Normalises an operator-typed address, or returns null when nothing usable is left.
    /// </summary>
    /// <remarks>
    /// Mirrors <c>TransferClient.sanitizeHost</c> on the device, for the same reason: the
    /// handheld prints a complete URL for the operator to read off, and the likeliest mistake is
    /// to paste that whole string into a box that wants only the host. Stripping the scheme, the
    /// trailing path and the redundant port turns that mistake into a working transfer instead of
    /// an error nobody can diagnose.
    /// </remarks>
    internal static string? SanitizeHost(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

            // The scheme has to come off *before* the path is split, or "http://host" truncates at
            // the "//" and leaves the bare word "http".
            var host = raw.Trim();
            foreach (var scheme in new[] { "http://", "https://" })
            {
                if (host.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
                {
                    host = host[scheme.Length..];
                    break;
                }
            }

            // Then the path and query, which is everything from the first "/" onward.
            host = host.Split('/', '?', '#')[0].Trim();

            // The port is supplied separately, so one typed in is always redundant - leaving it
            // would produce "http://192.168.1.50:8758:8758".
            host = TrailingPort.Replace(host, string.Empty).Trim();

            return host.Length > 0 && !host.Any(char.IsWhiteSpace) ? host : null;
        }

        public void Dispose() => _http.Dispose();
    }