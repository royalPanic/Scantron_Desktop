namespace Scantron.Core.Models;

/// <summary>
/// A whole Scantron export document: the exact shape the Android app reads and writes.
/// </summary>
public sealed record InventoryDocument
{
    /// <summary>Producer identifier. The device stamps "Scantron" and does not validate it.</summary>
    public string App { get; init; } = InventoryFormat.AppId;

    /// <summary>
    /// Format version. <c>1.1</c> adds per-item <c>uuid</c> identity; <c>1.0</c> predates it and
    /// is still importable by the device.
    /// </summary>
    public string Version { get; init; } = InventoryFormat.CurrentVersion;

    /// <summary>
    /// Export timestamp. The device formats this with a literal <c>Z</c> but no UTC time zone,
    /// so the value is really local time wearing a UTC suffix - see
    /// <see cref="InventoryFormat.ParseExportedAt"/>.
    /// </summary>
    public string ExportedAt { get; init; } = "";

    public required IReadOnlyList<Container> Containers { get; init; }
}