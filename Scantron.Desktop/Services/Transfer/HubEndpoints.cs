using System.Globalization;
using Scantron.Core.Models;
using Scantron.Core.Serialization;

namespace Scantron.Desktop.Services.Transfer;

/// <summary>
/// What a request produced: a status code, a content type, and a body already encoded as text.
/// </summary>
/// <remarks>
/// Deliberately no headers and no stream. The caller writes these three values to the socket and
/// nothing else, which keeps the wire contract small enough to be read in one sitting - and keeps
/// the whole request pipeline expressible as a pure function.
/// </remarks>
public sealed record HubResponse(int StatusCode, string ContentType, string Body)
{
    /// <summary>Content type for the JSON documents the contract carries.</summary>
    public const string Json = "application/json";

    /// <summary>
    /// Content type for the short status lines and for every error body.
    /// </summary>
    /// <remarks>
    /// Errors are plain text on purpose. The handheld surfaces this body verbatim in a toast, and
    /// a human-readable sentence is what an operator standing in an aisle can act on. A JSON
    /// error envelope would be one more thing for the client to have to understand before it can
    /// tell the operator what went wrong.
    /// </remarks>
    public const string Text = "text/plain; charset=utf-8";
}

/// <summary>
/// The transfer hub's whole request surface, as a pure function.
/// </summary>
/// <remarks>
/// <para>
/// <c>(method, path, body) -&gt; (status, contentType, body)</c>. No sockets, no threads, no WPF
/// and no state. That is not architectural purity for its own sake - it is what lets the entire
/// contract, including the failure paths that matter, be covered by the xUnit project without
/// binding a port. The socket layer in <see cref="TransferHub"/> is thin enough that very little
/// of it is left untested once this is.
/// </para>
/// <para>
/// The body of <c>/push</c> and <c>/pull</c> is the raw export document - exactly what
/// <see cref="InventoryReader.Write"/> produces and what the device's <c>ExportImportManager</c>
/// reads. No envelope, no base64, no zip, deliberately: both sides already have a parser and a
/// validator for exactly this document, so an envelope would be a second place to keep in step
/// and a second way for the two halves to disagree. A file copied over USB and a file pushed over
/// Wi-Fi have to be interchangeable, and the surest way to get that is for the bytes to be the
/// same bytes.
/// </para>
/// </remarks>
public static class HubEndpoints
{
    /// <summary>
    /// Largest request body accepted, in bytes.
    /// </summary>
    /// <remarks>
    /// A real export is well under a megabyte, so 16 MB is roughly fifteen times that. The cap
    /// exists because this is an unauthenticated endpoint on a shared warehouse network: without
    /// it, a malformed or hostile request is a way to exhaust the memory of the machine holding
    /// the day's stock count. It is enforced on the raw bytes as they arrive, before the body is
    /// decoded, so an oversized request never becomes a large string in the first place.
    /// </remarks>
    public const int MaxBodyBytes = 16 * 1024 * 1024;

    /// <summary>Protocol token the handheld expects from <c>/health</c>.</summary>
    private const string ProtocolToken = "scantron-hub/1";

    /// <summary>
    /// Served for <c>/pull</c> when the desktop has no document loaded.
    /// </summary>
    /// <remarks>
    /// This is the single most dangerous failure mode in the feature, so the wording is
    /// deliberate. The handheld imports by clearing and replacing, so an empty
    /// <c>containers</c> array is not a harmless empty document - it is an instruction to delete
    /// everything the device holds. Refusing to answer at all, with a reason the operator can
    /// read, is the only safe answer here. Never replace this with an empty document.
    /// </remarks>
    private const string NothingLoadedMessage =
        "Nothing to send - open a document on the desktop first.";

    /// <summary>
    /// Serves one request.
    /// </summary>
    /// <param name="method">HTTP method, compared case-insensitively.</param>
    /// <param name="path">Request path, normalised by <see cref="NormalisePath"/>.</param>
    /// <param name="body">Raw request body; empty for a <c>GET</c>.</param>
    /// <param name="current">
    /// The live desktop document, or null when nothing is loaded. Supplied per request by the
    /// caller rather than cached, because what the operator sees on the grid is what has to go
    /// over the wire - for the same reason the merge path rebuilds before merging.
    /// </param>
    /// <param name="onPush">
    /// Routes a parsed inbound document into the merge path and returns the response to relay.
    /// </param>
    /// <param name="deviceName">Reported by <c>/health</c>; defaults to this machine's name.</param>
    public static HubResponse Handle(
        string method,
        string path,
        string body,
        InventoryDocument? current,
        Func<InventoryDocument, HubResponse> onPush,
        string deviceName = "")
    {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(onPush);

        var route = NormalisePath(path);

                // Route names are matched case-insensitively. A URL path is case-sensitive by RFC, but
                // the three routes here are fixed literals in a contract both sides wrote down, so the
                // only way to reach a capitalised variant is to type it - and a client that uppercases
                // its paths should be answered, not left wondering why nothing connected.
                switch (route.ToUpperInvariant())
                {
                    case "/HEALTH" when Is(method, "GET"):
                        return Health(deviceName);

                    case "/PULL" when Is(method, "GET"):
                        return Pull(current);

                    case "/PUSH" when Is(method, "POST"):
                        return Push(body, onPush);

                    // A known route reached with the wrong verb is a mistake worth naming, and 405 says
                    // so without the client having to work out which of the three routes it hit.
                    case "/HEALTH" or "/PULL" or "/PUSH":
                        return TextResponse(405, $"Use {(route.EndsWith("push", StringComparison.OrdinalIgnoreCase) ? "POST" : "GET")} for {route}.");

                    default:
                        return TextResponse(404, $"No such hub endpoint: {route}");
                }
    }

    /// <summary>Serializes a document to the exact bytes the handheld imports.</summary>
    public static string ToExportJson(InventoryDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return InventoryReader.Write(document);
    }

    /// <summary>
    /// Builds the acknowledgement <c>{"ok":true,"containers":N,"items":M}</c>.
    /// </summary>
    /// <remarks>
    /// Hand-built rather than serialized so the field names and their order cannot drift with a
    /// change to the desktop's own DTO naming. The handheld reads the two counts and ignores the
    /// rest, but a contract this small should still be written down once, literally.
    /// </remarks>
    public static string Acknowledgement(InventoryDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var items = 0;
        foreach (var container in document.Containers)
        {
            items += container.Items.Count;
        }

        return string.Format(
            CultureInfo.InvariantCulture,
            "{{\"ok\":true,\"containers\":{0},\"items\":{1}}}",
            document.Containers.Count,
            items);
    }

    /// <summary>
    /// Reduces a path to a comparable form.
    /// </summary>
    /// <remarks>
    /// Trims a query string and any trailing slash, and compares case-insensitively, because a
    /// handheld built against a different URL library will happily send <c>/pull/</c> or
    /// <c>/PULL</c>, and refusing either would be a bug on the operator rather than a security
    /// control. Only these three literal routes exist, so normalising cannot widen what is
    /// reachable.
    /// </remarks>
    private static string NormalisePath(string path)
    {
        var cut = path.IndexOfAny(new[] { '?', '#' });
        var route = cut >= 0 ? path[..cut] : path;

        if (route.Length > 1)
        {
            route = route.TrimEnd('/');
        }

        return route.Length == 0 ? "/" : route;
    }

    private static bool Is(string method, string expected) =>
        string.Equals(method, expected, StringComparison.OrdinalIgnoreCase);

    private static HubResponse Health(string deviceName)
    {
        var name = string.IsNullOrWhiteSpace(deviceName) ? Environment.MachineName : deviceName.Trim();
        return new HubResponse(200, HubResponse.Text, $"{ProtocolToken} {name}");
    }

    /// <summary>
    /// Serves the desktop's live document.
    /// </summary>
    /// <remarks>
    /// Refuses outright when nothing is loaded. See <see cref="NothingLoadedMessage"/> for why an
    /// empty document is the one answer that must never be given here.
    /// </remarks>
    private static HubResponse Pull(InventoryDocument? current)
    {
        if (current is null)
        {
            Log.Warning("Refused a /pull request: no document is loaded on the desktop");
            return TextResponse(503, NothingLoadedMessage);
        }

        return new HubResponse(200, HubResponse.Json, ToExportJson(current));
    }

    /// <summary>
    /// Parses, validates and hands an inbound push to the merge path.
    /// </summary>
    /// <remarks>
    /// Validation is not optional, and deliberately does not belong to <paramref name="onPush"/>.
    /// A push the desktop would itself refuse to export must not be accepted because it arrived
    /// over the network instead of off a USB stick. The device accepts a document, clears its
    /// database and replaces it wholesale, so one that merged here would be discovered on the
    /// handheld's next sync - having already destroyed the data it was meant to update. The
    /// desktop is the last checkpoint before that happens.
    /// </remarks>
    private static HubResponse Push(string body, Func<InventoryDocument, HubResponse> onPush)
    {
        InventoryDocument document;
        try
        {
            document = InventoryReader.Read(body);
        }
        catch (InventoryFormatException ex)
        {
            // Already written for a human: it is the same text the open-file path shows, so it
            // goes to the operator verbatim, with no exception type name attached.
            Log.Warning($"Refused a /push request: {ex.Message}");
            return TextResponse(400, ex.Message);
        }

        if (InventoryFileService.Validate(document) is { } invalid)
        {
            Log.Warning($"Refused a /push request: the handheld sent a document the desktop rejects: {invalid}");
            return TextResponse(400, "This document would be rejected by the desktop:" + Environment.NewLine + invalid);
        }

        // The caller owns the merge, the conflicts and the status text; whatever it returns is
        // what the handheld sees. Guarded because an exception escaping here would take the
        // serve loop down with it and leave the hub silently unreachable.
        try
        {
            return onPush(document);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            Log.Error("A /push handler failed", ex);
            return TextResponse(500, $"The desktop could not accept the transfer: {ex.Message}");
        }
    }

    private static HubResponse TextResponse(int statusCode, string body) =>
        new(statusCode, HubResponse.Text, body);
}