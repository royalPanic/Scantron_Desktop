using System.Text.Json;
using System.Text.Json.Serialization;

namespace Scantron.Core.Serialization;

/// <summary>
/// On-disk DTOs for the Scantron export document.
/// </summary>
/// <remarks>
/// Deliberately separate from <see cref="Models.InventoryDocument"/> so the wire format can stay
/// frozen while the domain model evolves. Property names are camelCase to match what the Android
/// app emits and expects; the <c>[JsonPropertyName]</c> attributes pin them explicitly so a C#
/// rename can never silently change the contract.
/// </remarks>
public sealed class ContainerDto
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("location")]
    public string? Location { get; set; }

    [JsonPropertyName("notes")]
    public string? Notes { get; set; }

    [JsonPropertyName("updatedAt")]
    public long? UpdatedAt { get; set; }

    [JsonPropertyName("items")]
    public List<ItemDto>? Items { get; set; }
}

public sealed class ItemDto
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>
    /// Present in version 1.1 only. A missing value decodes to empty, which the repository
    /// treats as "no identity yet" rather than as a null barcode - the device's
    /// <c>optString</c> on an explicit JSON null yields the literal text "null", so this is
    /// never round-tripped back as null.
    /// </summary>
    [JsonPropertyName("uuid")]
    public string? Uuid { get; set; }

    [JsonPropertyName("barcode")]
    public string? Barcode { get; set; }

    [JsonPropertyName("quantity")]
    public int? Quantity { get; set; }

    [JsonPropertyName("category")]
    public string? Category { get; set; }

    [JsonPropertyName("notes")]
    public string? Notes { get; set; }

    [JsonPropertyName("updatedAt")]
    public long? UpdatedAt { get; set; }
}

public sealed class InventoryDocumentDto
{
    [JsonPropertyName("app")]
    public string? App { get; set; }

    [JsonPropertyName("version")]
    public string? Version { get; set; }

    [JsonPropertyName("exportedAt")]
    public string? ExportedAt { get; set; }

    [JsonPropertyName("containers")]
    public List<ContainerDto>? Containers { get; set; }
}

/// <summary>
/// Source-generated serializer metadata for the Scantron document.
/// </summary>
/// <remarks>
/// Source generation keeps the publish free of reflection, which is what makes a trimmed or
/// single-file build predictable. Register it in <c>JsonSerializerOptions.TypeInfoResolver</c>.
/// </remarks>
[JsonSerializable(typeof(InventoryDocumentDto))]
[JsonSerializable(typeof(List<ContainerDto>))]
[JsonSerializable(typeof(List<ItemDto>))]
public sealed partial class InventoryJsonContext : JsonSerializerContext;