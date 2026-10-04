namespace Scantron.Core.Models;

/// <summary>
/// A single item row within a container.
/// </summary>
/// <remarks>
/// Identity is <see cref="Uuid"/>, which survives an export/import round-trip. The device-local
/// row id is deliberately not modelled: the Android importer hard-resets it to 0 on every
/// import, so it carries no meaning outside the database that issued it.
/// </remarks>
public sealed record Item
{
    /// <summary>Stable identity. Empty means "no identity yet" and must be minted before use.</summary>
    public string Uuid { get; init; } = "";

    public required string Name { get; init; }

    /// <summary>
    /// Optional barcode / SKU / UPC. Empty rather than null: the Android entity is non-null with
    /// an empty-string default, and its merge queries test <c>TRIM(barcode) = ''</c>.
    /// </summary>
    public string Barcode { get; init; } = "";

    /// <summary>Units held. The importer defaults a missing value to 1 and does not range-check.</summary>
    public int Quantity { get; init; } = 1;

    public string Category { get; init; } = "";

    public string Notes { get; init; } = "";

    /// <summary>Epoch milliseconds of the last edit.</summary>
    public long UpdatedAt { get; init; }

    /// <summary>
    /// True when this row carries no barcode after trimming, which is what makes name-only
    /// matching legal against it (mirrors the device's <c>getNameOnlyItemByName</c> rule).
    /// </summary>
    public bool IsNameOnly => string.IsNullOrWhiteSpace(Barcode);

    /// <summary>Returns a copy with a freshly minted <see cref="Uuid"/>.</summary>
    public Item WithNewUuid() => this with { Uuid = Guid.NewGuid().ToString() };
}