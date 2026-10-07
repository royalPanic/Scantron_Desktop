using Scantron.Core.Identity;
using Scantron.Core.Models;

namespace Scantron.Core.Sync;

/// <summary>
/// Immutable edits to an <see cref="InventoryDocument"/>, used to rebuild a peer's view of the
/// world from the shared base plus a batch of ops.
/// </summary>
/// <remarks>
/// <para>
/// The live-sync receiver does not merge field-by-field on the wire. It reconstructs what the peer
/// now holds - <em>base + ops</em> - and then hands that to the existing
/// <see cref="Merge.InventoryMerger"/>, which already knows the field rules and is where they are
/// tested. Doing it this way means a live sync and a USB import cannot drift: they are the same
/// comparison, over the same two documents.
/// </para>
/// <para>
/// Every method returns a new document and never mutates its input, matching the immutable
/// <see cref="Container"/> and <see cref="Item"/> records this project is built on.
/// </para>
/// </remarks>
public static class SyncDocuments
{
    /// <summary>Empty document, used when a peer sends a snapshot or an op with no usable base.</summary>
    public static InventoryDocument Empty { get; } = new() { Containers = [] };

    /// <summary>Adds <paramref name="container"/>, replacing any container with the same tag.</summary>
    public static InventoryDocument UpsertContainer(InventoryDocument document, Container container)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(container);

        var id = container.Id.Trim();
        var containers = document.Containers.ToList();
        var index = IndexOfContainer(containers, id);

        if (index >= 0)
        {
            containers[index] = container;
        }
        else
        {
            containers.Add(container);
        }

        return document with { Containers = containers };
    }

    /// <summary>Removes a container and every row inside it.</summary>
    public static InventoryDocument RemoveContainer(InventoryDocument document, string containerId)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(containerId);

        var id = containerId.Trim();
        var containers = document.Containers
            .Where(c => !string.Equals(c.Id.Trim(), id, StringComparison.Ordinal))
            .ToList();

        return containers.Count == document.Containers.Count
            ? document
            : document with { Containers = containers };
    }

    /// <summary>
    /// Adds or replaces one row, matched on the same key the merge uses.
    /// </summary>
    /// <remarks>
    /// Matching on <see cref="ItemKeyResolver"/> rather than on the uuid alone is what lets a legacy
    /// 1.0 row - which has no uuid - be updated instead of duplicated.
    /// </remarks>
    public static InventoryDocument UpsertItem(InventoryDocument document, string containerId, Item item)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(item);

        var id = containerId.Trim();
        var containers = document.Containers.ToList();
        var index = IndexOfContainer(containers, id);
        var container = index >= 0 ? containers[index] : new Container { Id = id, Items = [] };

        var items = container.Items.ToList();
        var key = ItemKeyResolver.Resolve(id, item);
        var existing = IndexOfItem(items, id, key);

        if (existing >= 0)
        {
            items[existing] = item;
        }
        else
        {
            items.Add(item);
        }

        var updated = container with { Items = items };
        if (index >= 0)
        {
            containers[index] = updated;
        }
        else
        {
            containers.Add(updated);
        }

        return document with { Containers = containers };
    }

    /// <summary>Removes one row, identified by the key the deleting side computed.</summary>
    public static InventoryDocument RemoveItem(
        InventoryDocument document,
        string containerId,
        ItemKey key)
    {
        ArgumentNullException.ThrowIfNull(document);

        var id = containerId.Trim();
        var containers = document.Containers.ToList();
        var index = IndexOfContainer(containers, id);

        if (index < 0)
        {
            return document;
        }

        var items = containers[index].Items.ToList();
        var existing = IndexOfItem(items, id, key);

        if (existing < 0)
        {
            return document;
        }

        items.RemoveAt(existing);
        containers[index] = containers[index] with { Items = items };
        return document with { Containers = containers };
    }

    /// <summary>
    /// Sets one named field on a container. Unknown field names are ignored rather than throwing:
    /// a newer peer may legitimately know a field this build does not, and dropping the connection
    /// over it would be worse than ignoring the one field.
    /// </summary>
    public static InventoryDocument SetContainerField(
        InventoryDocument document,
        string containerId,
        string field,
        string? value)
    {
        ArgumentNullException.ThrowIfNull(document);

        var id = containerId.Trim();
        var containers = document.Containers.ToList();
        var index = IndexOfContainer(containers, id);

        if (index < 0)
        {
            return document;
        }

        var container = containers[index];
        containers[index] = field switch
        {
            "Name" => container with { Name = value ?? "" },
            "Location" => container with { Location = value ?? "" },
            "Notes" => container with { Notes = value ?? "" },
            _ => container,
        };

        return document with { Containers = containers };
    }

    /// <summary>
    /// Sets one named field on a row, found by its key. Used to revert a conflicted field back to
    /// the base value, which is how an unresolved conflict stays unresolved instead of being
    /// silently settled by whichever side happened to sync first.
    /// </summary>
    public static InventoryDocument SetItemField(
        InventoryDocument document,
        string containerId,
        ItemKey key,
        string field,
        string? value)
    {
        ArgumentNullException.ThrowIfNull(document);

        var id = containerId.Trim();
        var containers = document.Containers.ToList();
        var index = IndexOfContainer(containers, id);

        if (index < 0)
        {
            return document;
        }

        var items = containers[index].Items.ToList();
        var existing = IndexOfItem(items, id, key);

        if (existing < 0)
        {
            return document;
        }

        var item = items[existing];
        items[existing] = field switch
        {
            "Name" => item with { Name = value ?? "" },
            "Barcode" => item with { Barcode = value ?? "" },
            "Quantity" => item with { Quantity = ParseQuantity(value, item.Quantity) },
            "Category" => item with { Category = value ?? "" },
            "Notes" => item with { Notes = value ?? "" },
            _ => item,
        };

        // The row's timestamp is deliberately not advanced: an unresolved conflict must not look
        // like a fresh edit, or the next merge would rank it above the peer's real change.
        return document with
        {
            Containers = Replace(containers, index, containers[index] with { Items = items }),
        };
    }

    /// <summary>Finds a container by trimmed tag.</summary>
    private static int IndexOfContainer(List<Container> containers, string id)
    {
        for (var i = 0; i < containers.Count; i++)
        {
            if (string.Equals(containers[i].Id.Trim(), id, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    private static int IndexOfItem(List<Item> items, string containerId, ItemKey key)
    {
        for (var i = 0; i < items.Count; i++)
        {
            if (ItemKeyResolver.Resolve(containerId, items[i]) == key)
            {
                return i;
            }
        }

        return -1;
    }

    private static List<Container> Replace(List<Container> containers, int index, Container value)
    {
        var copy = containers.ToList();
        copy[index] = value;
        return copy;
    }

    /// <summary>
    /// Parses a quantity from a conflict's text form, falling back to the current value.
    /// </summary>
    /// <remarks>
    /// Values travel as strings because a conflict is a presentation of two disagreeing texts, so
    /// the round-trip has to tolerate anything a human could have put in the row. A value that is
    /// not a number keeps the row's existing quantity rather than throwing on a sync path.
    /// </remarks>
    private static int ParseQuantity(string? value, int fallback) =>
        int.TryParse(value, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;
}
