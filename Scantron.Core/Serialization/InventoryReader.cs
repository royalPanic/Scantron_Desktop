using System.Text.Json;
using System.Text.Json.Serialization;
using Scantron.Core.Models;

namespace Scantron.Core.Serialization;

/// <summary>
/// Raised when a document cannot be read, or is not a usable Scantron export.
/// </summary>
/// <remarks>
/// Mirrors the Kotlin <c>ImportException</c>: readable file, unusable content.
/// </remarks>
public sealed class InventoryFormatException : Exception
{
    public InventoryFormatException(string message) : base(message)
    {
    }

    public InventoryFormatException(string message, Exception inner) : base(message, inner)
    {
    }
}

/// <summary>
/// Reads Scantron export documents into domain models.
/// </summary>
/// <remarks>
/// Validation is separate from parsing on purpose: <see cref="TryRead"/> only checks that the
/// document is structurally sound and that its version is one the device accepts, while
/// <see cref="InventoryValidator"/> applies the content rules. That split matches the device,
/// which throws <c>JSONException</c> for malformed input and <c>ImportException</c> for content
/// it rejects.
/// </remarks>
public static class InventoryReader
{
    /// <summary>
    /// Indentation the device uses when writing, reused so files look native to anyone who
    /// opens one in a text editor.
    /// </summary>
    public const int IndentSpaces = 2;

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        TypeInfoResolver = InventoryJsonContext.Default,
    };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        TypeInfoResolver = InventoryJsonContext.Default,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>
    /// Parses <paramref name="json"/>, throwing on anything the device would refuse.
    /// </summary>
    /// <exception cref="InventoryFormatException">
    /// The text is not valid JSON, lacks a containers array, or declares an unsupported version.
    /// </exception>
    public static InventoryDocument Read(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        InventoryDocumentDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize(json, InventoryJsonContext.Default.InventoryDocumentDto);
        }
        catch (JsonException ex)
        {
            throw new InventoryFormatException("File is not valid Scantron JSON", ex);
        }

        if (dto is null)
        {
            throw new InventoryFormatException("File is not valid Scantron JSON");
        }

        // The device defaults an absent version to its own, then range-checks it. Mirroring the
        // default keeps a hand-written file with no version field importable.
        var version = dto.Version ?? InventoryFormat.CurrentVersion;
        if (!InventoryFormat.ImportableVersions.Contains(version))
        {
            throw new InventoryFormatException(
                $"Unsupported export version \"{version}\" (expected one of " +
                string.Join(", ", InventoryFormat.ImportableVersions.Order()));
        }

        // A missing containers array is a hard failure on the device, not an empty inventory.
        if (dto.Containers is null)
        {
            throw new InventoryFormatException("File is missing a \"containers\" array");
        }

        return ToDomain(dto, version);
    }

    /// <summary>
    /// Parses without throwing, for drag-and-drop and watcher paths that should report a bad file
    /// rather than surface an exception.
    /// </summary>
    public static bool TryRead(string json, out InventoryDocument document, out string? error)
    {
        try
        {
            document = Read(json);
            error = null;
            return true;
        }
        catch (InventoryFormatException ex)
        {
            document = null!;
            error = ex.Message;
            return false;
        }
    }

    private static InventoryDocument ToDomain(InventoryDocumentDto dto, string version) =>
        new()
        {
            App = string.IsNullOrEmpty(dto.App) ? InventoryFormat.AppId : dto.App,
            Version = version,
            ExportedAt = dto.ExportedAt ?? "",
            Containers = dto.Containers!
                .Select(ToDomain)
                .ToList(),
        };

    private static Container ToDomain(ContainerDto dto) =>
        new()
        {
            // Trimmed up front because the device trims before testing, so " BOX-1 " and
            // "BOX-1" are the same container to it and must be the same container here.
            Id = (dto.Id ?? "").Trim(),
            Name = dto.Name ?? "",
            Location = dto.Location ?? "",
            Notes = dto.Notes ?? "",
            // Absent timestamps become "now" on the device; mirroring that keeps a file
            // without timestamps from looking universally stale to the merger.
            UpdatedAt = dto.UpdatedAt ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Items = dto.Items?.Select(ToDomain).ToList() ?? [],
        };

    private static Item ToDomain(ItemDto dto) =>
        new()
        {
            Uuid = (dto.Uuid ?? "").Trim(),
            // The device rejects a blank name outright, so trim here to surface it identically.
            Name = (dto.Name ?? "").Trim(),
            Barcode = dto.Barcode ?? "",
            Quantity = dto.Quantity ?? 1,
            Category = dto.Category ?? "",
            Notes = dto.Notes ?? "",
            UpdatedAt = dto.UpdatedAt ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };

    /// <summary>Serializes a document to the exact shape the device imports.</summary>
    public static string Write(InventoryDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return JsonSerializer.Serialize(ToDto(document), WriteOptions);
    }

    private static InventoryDocumentDto ToDto(InventoryDocument document) =>
        new()
        {
            App = document.App,
            Version = document.Version,
            ExportedAt = document.ExportedAt,
            Containers = document.Containers.Select(ToDto).ToList(),
        };

    private static ContainerDto ToDto(Container container) =>
        new()
        {
            Id = container.Id,
            Name = container.Name,
            Location = container.Location,
            Notes = container.Notes,
            UpdatedAt = container.UpdatedAt,
            Items = container.Items.Select(ToDto).ToList(),
        };

    private static ItemDto ToDto(Item item) =>
        new()
        {
            Name = item.Name,
            Uuid = item.Uuid,
            Barcode = item.Barcode,
            Quantity = item.Quantity,
            Category = item.Category,
            Notes = item.Notes,
            UpdatedAt = item.UpdatedAt,
        };
}