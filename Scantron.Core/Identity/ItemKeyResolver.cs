using Scantron.Core.Models;

namespace Scantron.Core.Identity;

/// <summary>
/// How a particular item is keyed for merge purposes.
/// </summary>
public enum ItemKeyKind
{
    /// <summary>Matched on <see cref="Item.Uuid"/>. The reliable path, available from v1.1.</summary>
    Uuid,

    /// <summary>
    /// Matched on container + barcode, mirroring <c>getItemByBarcode</c>. Used for legacy
    /// documents that carry no uuid.
    /// </summary>
    ContainerBarcode,

    /// <summary>
    /// Matched on container + lowercased trimmed name, but only against rows that carry no
    /// barcode, mirroring <c>getNameOnlyItemByName</c>.
    /// </summary>
    NameOnly,
}

/// <summary>
/// A stable, comparable key for one item row.
/// </summary>
/// <param name="Kind">Which rule produced the key.</param>
/// <param name="Value">Opaque key text; equality and hashing use <see cref="Kind"/> and this.</param>
public readonly record struct ItemKey(ItemKeyKind Kind, string Value)
{
    public override string ToString() => $"{Kind}:{Value}";
}

/// <summary>
/// Derives merge keys for items, mirroring the handheld's <c>addItemMerging</c> rules.
/// </summary>
/// <remarks>
/// <para>
/// The handheld folds a scan into an existing row using barcode first, falling back to
/// name-only matching. The desktop has to agree exactly, or a re-import produces rows the
/// device would immediately merge again - quantities that drift and duplicates that never
/// settle.
/// </para>
/// <para>
/// Two rules in the original are reproduced deliberately even though they look odd: a barcode
/// is scoped to its container (the same barcode legitimately exists in two containers), and an
/// item that <em>has</em> a barcode is never matched by name, because that barcode is its
/// identity.
/// </para>
/// <para>
/// This is a fallback only. From version 1.1 every item carries a uuid, and
/// <see cref="ItemKeyKind.Uuid"/> supersedes these heuristics entirely.
/// </para>
/// </remarks>
public static class ItemKeyResolver
{
    /// <summary>
    /// Separator between key parts. NUL is used because container tags and barcodes are
    /// operator-supplied free text: a printable separator could appear inside a tag or barcode
    /// and let two genuinely different (container, discriminator) pairs collide into one key,
    /// silently merging unrelated items. NUL cannot appear in JSON text.
    /// </summary>
    private const char KeySeparator = '\0';

    /// <summary>
    /// Produces the key for <paramref name="item"/> as it sits in <paramref name="containerId"/>.
    /// </summary>
    public static ItemKey Resolve(string containerId, Item item)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(containerId);

        if (!string.IsNullOrWhiteSpace(item.Uuid))
        {
            return new ItemKey(ItemKeyKind.Uuid, item.Uuid.Trim());
        }

        if (!item.IsNameOnly)
        {
            // Scoped to the container, and both sides trimmed, because the device's query is
            // `WHERE containerId = :c AND TRIM(barcode) = TRIM(:b)`.
            return new ItemKey(
                ItemKeyKind.ContainerBarcode,
                ComposeKey(containerId, item.Barcode));
        }

        // Lowercased and trimmed to match `LOWER(TRIM(name)) = LOWER(TRIM(:name))`. The name
        // is required non-blank by the validator, so it cannot collapse to an empty key.
        return new ItemKey(
            ItemKeyKind.NameOnly,
            ComposeKey(containerId, item.Name.Trim().ToLowerInvariant()));
    }

    /// <summary>
    /// Indexes a container's items by key, keeping the most recently updated row when two rows
    /// collide.
    /// </summary>
    /// <remarks>
    /// Collisions can only occur in legacy documents, where the heuristic keys are genuinely
    /// ambiguous. Resolving them by newest <c>updatedAt</c> mirrors the device, whose item
    /// queries all order <c>updatedAt DESC</c> before taking <c>LIMIT 1</c>. Matching that tie
    /// break keeps the desktop and the handheld converging on the same row instead of each
    /// picking a different one and oscillating on every sync.
    /// </remarks>
    public static Dictionary<ItemKey, Item> IndexByKey(
        string containerId,
        IEnumerable<Item> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        var index = new Dictionary<ItemKey, Item>();
        foreach (var item in items)
        {
            var key = Resolve(containerId, item);
            if (index.TryGetValue(key, out var existing))
            {
                if (item.UpdatedAt > existing.UpdatedAt)
                {
                    index[key] = item;
                }
            }
            else
            {
                index[key] = item;
            }
        }

        return index;
    }

    private static string ComposeKey(string containerId, string discriminator) =>
        string.Concat(containerId.Trim(), KeySeparator.ToString(), discriminator.Trim());
}