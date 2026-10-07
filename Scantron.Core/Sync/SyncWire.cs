using System.Text.Json;
using Scantron.Core.Identity;
using Scantron.Core.Models;
using Scantron.Core.Serialization;

namespace Scantron.Core.Sync;

/// <summary>
/// JSON for live-sync frames.
/// </summary>
/// <remarks>
/// <para>
/// Hand-written over <see cref="Utf8JsonWriter"/> and <see cref="JsonDocument"/> rather than
/// generated from a polymorphic type hierarchy. The payload set is six ops and twelve message
/// kinds, it is frozen by agreement between two codebases in two languages, and a discriminator
/// the compiler infers is a discriminator that can be changed by a rename. Writing the field names
/// out once, literally, is the same choice <c>HubEndpoints.Acknowledgement</c> makes and for the
/// same reason.
/// </para>
/// <para>
/// Rows and containers are written with the <em>same</em> DTOs the export document uses
/// (<see cref="ContainerDto"/>, <see cref="ItemDto"/>), so a row on the live wire and the same row
/// in a file are byte-identical in meaning. That is what keeps a live sync and a USB transfer
/// interchangeable.
/// </para>
/// <para>
/// This type never throws for bad input. A malformed frame on a socket is an ordinary event -
/// a version mismatch or a truncated read - and it has to become a sentence on the status line, not
/// an exception that takes the session down.
/// </para>
/// </remarks>
public static class SyncWire
{
    /// <summary>Serializes one frame to the text written into a WebSocket text frame.</summary>
    public static string Write(SyncMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("type", Name(message.Type));
            writer.WriteNumber("seq", message.Seq);

            if (message.Src.Length > 0)
            {
                writer.WriteString("src", message.Src);
            }

            WriteHello(writer, message.Hello);
            WritePair(writer, message.Pair);
            WritePaired(writer, message.Paired);
            WriteOps(writer, message.Ops);
            WriteConflict(writer, message.Conflict);

            if (message.AckSeq > 0)
            {
                writer.WriteNumber("ackSeq", message.AckSeq);
            }

            if (!string.IsNullOrEmpty(message.Reason))
            {
                writer.WriteString("reason", message.Reason);
            }

            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>Parses one frame. Returns false with a readable reason when it is not usable.</summary>
    public static bool TryRead(string json, out SyncMessage message, out string? error)
    {
        message = null!;
        error = null;

        if (string.IsNullOrWhiteSpace(json))
        {
            error = "The peer sent an empty frame.";
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "The peer sent a frame that is not a JSON object.";
                return false;
            }

            if (root.TryGetProperty("type", out var typeElement) &&
                typeElement.ValueKind == JsonValueKind.String &&
                Enum.TryParse<SyncMessageType>(typeElement.GetString(), ignoreCase: true, out var type))
            {
                message = new SyncMessage
                {
                    Type = type,
                    Seq = Long(root, "seq"),
                    Src = Text(root, "src") ?? "",
                    Hello = ReadHello(root),
                    Pair = ReadPair(root),
                    Paired = ReadPaired(root),
                    Ops = ReadOps(root, out var opsError),
                    Conflict = ReadConflict(root),
                    AckSeq = Long(root, "ackSeq"),
                    Reason = Text(root, "reason"),
                };

                if (opsError is not null)
                {
                    error = opsError;
                    return false;
                }

                return true;
            }

            error = "The peer sent a frame this build does not understand. " +
                    "Both devices must be running the same Scantron version.";
            return false;
        }
        catch (JsonException)
        {
            error = "The peer sent a frame that is not valid JSON.";
            return false;
        }
    }

    /// <summary>
    /// The stable wire name of a message type.
    /// </summary>
    /// <remarks>
    /// Lowercased explicitly rather than taking the enum's own name, so a future rename in C# shows
    /// up as a compile-time switch on the whole set instead of silently changing the contract.
    /// </remarks>
    public static string Name(SyncMessageType type) => type switch
    {
        SyncMessageType.Hello => "hello",
        SyncMessageType.Pair => "pair",
        SyncMessageType.Paired => "paired",
        SyncMessageType.Snapshot => "snapshot",
        SyncMessageType.Changes => "changes",
        SyncMessageType.Ack => "ack",
        SyncMessageType.Conflict => "conflict",
        SyncMessageType.Resolve => "resolve",
        SyncMessageType.Ping => "ping",
        SyncMessageType.Pong => "pong",
        SyncMessageType.Bye => "bye",
        _ => "unknown",
    };

    private static void WriteHello(Utf8JsonWriter writer, HelloInfo? hello)
    {
        if (hello is null)
        {
            return;
        }

        writer.WriteStartObject("hello");
        writer.WriteString("deviceId", hello.DeviceId);
        writer.WriteString("name", hello.Name);
        writer.WriteNumber("protocolVersion", hello.ProtocolVersion);
        writer.WriteEndObject();
    }

    private static void WritePair(Utf8JsonWriter writer, PairInfo? pair)
    {
        if (pair is null)
        {
            return;
        }

        // Kept out of the log and out of the UI: it is a shared secret for the pair, however small.
        writer.WriteStartObject("pair");
        writer.WriteString("code", pair.Code);
        writer.WriteEndObject();
    }

    private static void WritePaired(Utf8JsonWriter writer, PairedInfo? paired)
    {
        if (paired is null)
        {
            return;
        }

        writer.WriteStartObject("paired");
        writer.WriteString("deviceId", paired.DeviceId);
        writer.WriteString("desktopName", paired.DesktopName);
        writer.WriteString("sessionId", paired.SessionId);
        writer.WriteEndObject();
    }

    private static void WriteOps(Utf8JsonWriter writer, IReadOnlyList<SyncOp> ops)
    {
        if (ops.Count == 0)
        {
            return;
        }

        writer.WriteStartArray("ops");
        foreach (var op in ops)
        {
            WriteOp(writer, op);
        }

        writer.WriteEndArray();
    }

    private static void WriteOp(Utf8JsonWriter writer, SyncOp op)
    {
        writer.WriteStartObject();
        writer.WriteString("op", OpName(op.Kind));

        switch (op)
        {
            case SnapshotOp snapshot:
                writer.WritePropertyName("document");
                writer.WriteRawValue(InventoryReader.Write(snapshot.Document), skipInputValidation: true);
                break;

            case UpsertContainerOp upsert:
                writer.WritePropertyName("container");
                writer.WriteRawValue(
                    JsonSerializer.Serialize(ToDto(upsert.Container with { Items = [] }), DtoOptions),
                    skipInputValidation: true);
                break;

            case UpsertItemOp row:
                writer.WriteString("containerId", row.ContainerId);
                writer.WritePropertyName("item");
                writer.WriteRawValue(
                    JsonSerializer.Serialize(ToDto(row.Item), DtoOptions),
                    skipInputValidation: true);
                break;

            case DeleteContainerOp delete:
                writer.WriteString("containerId", delete.ContainerId);
                writer.WriteNumber("at", delete.At);
                break;

            case DeleteItemOp delete:
                writer.WriteString("containerId", delete.ContainerId);
                writer.WriteString("itemUuid", delete.ItemUuid);
                writer.WriteString("keyKind", delete.KeyKind.ToString());
                writer.WriteString("keyValue", delete.KeyValue);
                writer.WriteString("itemName", delete.ItemName);
                writer.WriteNumber("at", delete.At);
                break;

            case ResolveOp resolve:
                writer.WriteString("containerId", resolve.ContainerId);
                writer.WriteString("itemUuid", resolve.ItemUuid);
                writer.WriteString("field", resolve.Field);
                if (resolve.Value is null)
                {
                    writer.WriteNull("value");
                }
                else
                {
                    writer.WriteString("value", resolve.Value);
                }

                writer.WriteNumber("at", resolve.At);
                break;
        }

        writer.WriteEndObject();
    }

    private static void WriteConflict(Utf8JsonWriter writer, ConflictInfo? conflict)
    {
        if (conflict is null)
        {
            return;
        }

        writer.WriteStartObject("conflict");
        writer.WriteString("conflictId", conflict.ConflictId);
        writer.WriteString("containerId", conflict.ContainerId);
        writer.WriteString("itemUuid", conflict.ItemUuid);
        writer.WriteString("itemName", conflict.ItemName);
        writer.WriteString("field", conflict.Field);
        WriteNullable(writer, "baseValue", conflict.BaseValue);
        WriteNullable(writer, "localValue", conflict.LocalValue);
        WriteNullable(writer, "remoteValue", conflict.RemoteValue);
        writer.WriteEndObject();
    }

    private static void WriteNullable(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is null)
        {
            writer.WriteNull(name);
        }
        else
        {
            writer.WriteString(name, value);
        }
    }

    private static HelloInfo? ReadHello(JsonElement root) =>
        Nested(root, "hello") is { } hello
            ? new HelloInfo(
                Text(hello, "deviceId") ?? "",
                Text(hello, "name") ?? "",
                (int)Long(hello, "protocolVersion"))
            : null;

    private static PairInfo? ReadPair(JsonElement root) =>
        Nested(root, "pair") is { } pair ? new PairInfo(Text(pair, "code") ?? "") : null;

    private static PairedInfo? ReadPaired(JsonElement root) =>
        Nested(root, "paired") is { } paired
            ? new PairedInfo(
                Text(paired, "deviceId") ?? "",
                Text(paired, "desktopName") ?? "",
                Text(paired, "sessionId") ?? "")
            : null;

    private static ConflictInfo? ReadConflict(JsonElement root) =>
        Nested(root, "conflict") is { } conflict
            ? new ConflictInfo(
                Text(conflict, "conflictId") ?? "",
                Text(conflict, "containerId") ?? "",
                Text(conflict, "itemUuid") ?? "",
                Text(conflict, "itemName") ?? "",
                Text(conflict, "field") ?? "",
                Text(conflict, "baseValue"),
                Text(conflict, "localValue"),
                Text(conflict, "remoteValue"))
            : null;

    private static IReadOnlyList<SyncOp> ReadOps(JsonElement root, out string? error)
    {
        error = null;
        var ops = new List<SyncOp>();

        if (!root.TryGetProperty("ops", out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return ops;
        }

        foreach (var element in array.EnumerateArray())
        {
            if (ReadOp(element, out var op, out var opError))
            {
                ops.Add(op!);
                continue;
            }

            // A single unreadable op poisons the batch: applying the rest would leave the peer's
            // view half-reconstructed, which is worse than refusing the whole frame.
            error = opError;
            return [];
        }

        return ops;
    }

    private static bool ReadOp(JsonElement element, out SyncOp? op, out string? error)
    {
        op = null;
        error = null;

        var name = Text(element, "op");
        switch (name)
        {
            case "snapshot":
                if (!element.TryGetProperty("document", out var documentElement))
                {
                    error = "A snapshot arrived without a document.";
                    return false;
                }

                if (!InventoryReader.TryRead(documentElement.GetRawText(), out var document, out var readError))
                {
                    error = "The peer sent a document this build cannot read: " + readError;
                    return false;
                }

                op = new SnapshotOp(document);
                return true;

            case "upsertContainer":
                if (ReadDto<ContainerDto>(element, "container", out var containerDto, out error) is false)
                {
                    return false;
                }

                var container = FromDto(containerDto!);
                if (container.Id.Trim().Length == 0)
                {
                    error = "A container arrived without a tag.";
                    return false;
                }

                op = new UpsertContainerOp(container);
                return true;

            case "upsertItem":
                if (ReadDto<ItemDto>(element, "item", out var itemDto, out error) is false)
                {
                    return false;
                }

                var containerId = (Text(element, "containerId") ?? "").Trim();
                if (containerId.Length == 0)
                {
                    error = "A row arrived without a container tag.";
                    return false;
                }

                var item = FromDto(itemDto!);
                if (string.IsNullOrWhiteSpace(item.Name))
                {
                    // The validator would reject this on the way into a document anyway; catching it
                    // here names the actual row instead of the whole document.
                    error = $"A row in \"{containerId}\" arrived without a name.";
                    return false;
                }

                op = new UpsertItemOp(containerId, item);
                return true;

            case "deleteContainer":
                op = new DeleteContainerOp(
                    (Text(element, "containerId") ?? "").Trim(),
                    Long(element, "at"));
                return true;

            case "deleteItem":
                if (!Enum.TryParse<ItemKeyKind>(Text(element, "keyKind"), ignoreCase: true, out var kind))
                {
                    error = "A row deletion arrived with an unrecognised identity kind.";
                    return false;
                }

                op = new DeleteItemOp(
                    (Text(element, "containerId") ?? "").Trim(),
                    (Text(element, "itemUuid") ?? "").Trim(),
                    kind,
                    Text(element, "keyValue") ?? "",
                    Text(element, "itemName") ?? "",
                    Long(element, "at"));
                return true;

            case "resolve":
                op = new ResolveOp(
                    (Text(element, "containerId") ?? "").Trim(),
                    (Text(element, "itemUuid") ?? "").Trim(),
                    Text(element, "field") ?? "",
                    Text(element, "value"),
                    Long(element, "at"));
                return true;

            default:
                error = $"The peer sent a change this build does not understand (\"{name}\"). " +
                        "Both devices must be running the same Scantron version.";
                return false;
        }
    }

    private static bool ReadDto<T>(
        JsonElement element,
        string property,
        out T? dto,
        out string? error)
        where T : class
    {
        dto = null;
        error = null;

        if (!element.TryGetProperty(property, out var nested))
        {
            error = $"A {property} change arrived without its \"{property}\" object.";
            return false;
        }

        try
        {
            dto = JsonSerializer.Deserialize<T>(nested.GetRawText(), DtoOptions);
        }
        catch (JsonException)
        {
            error = $"A {property} change arrived in a shape this build cannot read.";
            return false;
        }

        if (dto is null)
        {
            error = $"A {property} change arrived empty.";
            return false;
        }

        return true;
    }

    private static string OpName(SyncOpKind kind) => kind switch
    {
        SyncOpKind.Snapshot => "snapshot",
        SyncOpKind.UpsertContainer => "upsertContainer",
        SyncOpKind.UpsertItem => "upsertItem",
        SyncOpKind.DeleteContainer => "deleteContainer",
        SyncOpKind.DeleteItem => "deleteItem",
        SyncOpKind.Resolve => "resolve",
        _ => "unknown",
    };

    private static JsonElement? Nested(JsonElement root, string name) =>
        root.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.Object
            ? element
            : null;

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static long Long(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt64(out var parsed)
            ? parsed
            : 0;

    private static readonly JsonSerializerOptions DtoOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        TypeInfoResolver = InventoryJsonContext.Default,
    };

    private static ContainerDto ToDto(Container container) => new()
    {
        Id = container.Id,
        Name = container.Name,
        Location = container.Location,
        Notes = container.Notes,
        UpdatedAt = container.UpdatedAt,
        Items = null,
    };

    private static ItemDto ToDto(Item item) => new()
    {
        Name = item.Name,
        Uuid = item.Uuid,
        Barcode = item.Barcode,
        Quantity = item.Quantity,
        Category = item.Category,
        Notes = item.Notes,
        UpdatedAt = item.UpdatedAt,
    };

    private static Container FromDto(ContainerDto dto) => new()
    {
        Id = (dto.Id ?? "").Trim(),
        Name = dto.Name ?? "",
        Location = dto.Location ?? "",
        Notes = dto.Notes ?? "",
        UpdatedAt = dto.UpdatedAt ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        Items = [],
    };

    private static Item FromDto(ItemDto dto) => new()
    {
        Uuid = (dto.Uuid ?? "").Trim(),
        Name = (dto.Name ?? "").Trim(),
        Barcode = dto.Barcode ?? "",
        Quantity = dto.Quantity ?? 1,
        Category = dto.Category ?? "",
        Notes = dto.Notes ?? "",
        UpdatedAt = dto.UpdatedAt ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
    };
}
