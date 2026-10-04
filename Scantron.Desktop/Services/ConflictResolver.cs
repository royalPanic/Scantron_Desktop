using System.Globalization;
using Scantron.Core.Models;
using Scantron.Core.Merge;

namespace Scantron.Desktop.Services;

/// <summary>Which side's value the operator chose for a conflicted field.</summary>
public enum ConflictResolution
{
    /// <summary>Keep the desktop value. Already what the merger emitted, so this is a no-op.</summary>
    KeepLocal,

    /// <summary>Overwrite with the handheld's value.</summary>
    TakeRemote,
}

/// <summary>
/// Outcome of applying one resolution.
/// </summary>
/// <remarks>
/// Carries the rewritten document because the domain model is immutable: the result is a new
/// document, and the caller has to thread it forward. Returning only a flag would leave nothing
/// to thread forward, which is exactly the bug this shape exists to prevent.
/// </remarks>
/// <param name="Document">The updated document, or null when the decision could not be applied.</param>
/// <param name="Message">Operator-facing explanation, set only on failure.</param>
public readonly record struct ResolutionOutcome(InventoryDocument? Document, string? Message)
{
    public bool Succeeded => Document is not null;

    /// <summary>Succeeded, and the document is unchanged.</summary>
    public static ResolutionOutcome Unchanged(InventoryDocument document) => new(document, null);

    public static ResolutionOutcome Failed(string message) => new(null, message);
}

/// <summary>
/// Applies operator decisions to a merged document.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="InventoryMerger"/> deliberately resolves nothing: it reports every field both
/// sides changed to different values and emits the desktop's value as a safe placeholder. This
/// type is the other half of that contract - it takes the human's decision and rewrites the
/// document so it can be exported.
/// </para>
/// <para>
/// Kept free of WPF types and of file access, so the decision logic is testable on its own and
/// the view model stays a thin adapter over it.
/// </para>
/// </remarks>
public static class ConflictResolver
{
    /// <summary>
    /// Pseudo-field the merger uses when a row present in the base is absent from both sides.
    /// </summary>
    /// <remarks>
    /// Not a real field: there is no value to choose between, because both sides are saying
    /// "absent". The merger carries the row forward rather than deleting it, and that decision
    /// belongs to the operator in the grid, not here.
    /// </remarks>
    private const string PresenceField = "Presence";

    /// <summary>Container metadata fields, which live on the container rather than an item.</summary>
    private static readonly string[] ContainerFields = [nameof(Container.Name), nameof(Container.Location), nameof(Container.Notes)];

    /// <summary>Item fields, keyed by the name the merger reports them under.</summary>
    private static readonly string[] ItemFields = [nameof(Item.Name), nameof(Item.Barcode), nameof(Item.Quantity), nameof(Item.Category), nameof(Item.Notes)];

    /// <summary>
    /// Rewrites <paramref name="document"/> with <paramref name="resolution"/> applied to
    /// <paramref name="conflict"/>.
    /// </summary>
    /// <remarks>
    /// Returns a new document; the input is untouched, because an aborted or half-resolved
    /// merge must not corrupt what the operator was looking at.
    /// </remarks>
    public static ResolutionOutcome Apply(
        InventoryDocument document,
        ItemConflict conflict,
        ConflictResolution resolution)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(conflict);

        // Nothing to choose between: both sides agree the row is gone. The merged document
        // already carries it forward, so any resolution here is informational.
        if (conflict.Field == PresenceField)
        {
            return ResolutionOutcome.Unchanged(document);
        }

        if (resolution == ConflictResolution.KeepLocal)
        {
            // The merger already emitted the local value on every true conflict.
            return ResolutionOutcome.Unchanged(document);
        }

        if (!ContainerFields.Contains(conflict.Field, StringComparer.Ordinal) &&
            !ItemFields.Contains(conflict.Field, StringComparer.Ordinal))
        {
            return ResolutionOutcome.Failed($"Unsupported conflict field \"{conflict.Field}\"");
        }

        var containerIndex = IndexOfContainer(document, conflict.ContainerId);
        if (containerIndex < 0)
        {
            return ResolutionOutcome.Failed($"Container \"{conflict.ContainerId}\" is no longer in the document");
        }

        var container = document.Containers[containerIndex];

        var targets = FindTargets(container, conflict);
        switch (targets.Count)
        {
            case 0:
                // Stale conflict: the row or field is gone. Nothing to write, and reporting
                // failure here would block the operator from clearing a list that is already
                // consistent.
                return ResolutionOutcome.Unchanged(document);
            case > 1:
                return ResolutionOutcome.Failed(
                    $"\"{conflict.Field}\" on {conflict.ItemName} in \"{conflict.ContainerId}\" is ambiguous - " +
                    "the handheld export carries no item uuid for it, so the row cannot be identified uniquely");
        }

        var target = targets[0];
        var updatedContainer = target switch
        {
            { IsContainer: true } => RewriteContainerField(container, target.Field, conflict.RemoteValue),
            { ItemIndex: { } index } => RewriteItemField(container, index, target.Field, conflict.RemoteValue),
            _ => null,
        };

        if (updatedContainer is null)
        {
            return ResolutionOutcome.Failed($"Could not read \"{conflict.RemoteValue}\" as {conflict.Field}");
        }

        // Rebuilt rather than mutated: InventoryDocument is a record over an immutable list, so
        // an in-place edit would not be visible to the caller's reference.
        var containers = document.Containers.ToArray();
        containers[containerIndex] = updatedContainer;
        return ResolutionOutcome.Unchanged(document with { Containers = containers });
    }

    /// <summary>
    /// Locates the single row that <paramref name="conflict"/> describes.
    /// </summary>
    /// <remarks>
    /// A uuid-bearing item is identified directly. The legacy case is harder: the merger
    /// reports <c>itemUuid</c> as the input row's uuid, which is empty for a version 1.0
    /// export, so a container field and an item field of the same name become indistinguishable
    /// from the conflict record alone. Those are narrowed by value - on a real conflict the
    /// merger emitted the local value - and if that still leaves more than one candidate the
    /// conflict is reported as unresolvable rather than guessed at.
    /// </remarks>
    private static List<Target> FindTargets(Container container, ItemConflict conflict)
    {
        var targets = new List<Target>(1);

        if (!string.IsNullOrWhiteSpace(conflict.ItemUuid))
        {
            for (var i = 0; i < container.Items.Count; i++)
            {
                if (string.Equals(container.Items[i].Uuid, conflict.ItemUuid.Trim(), StringComparison.Ordinal) &&
                    HasField(container.Items[i], conflict.Field))
                {
                    targets.Add(Target.ForItem(i, conflict.Field));
                }
            }

            return targets;
        }

        if (string.Equals(conflict.Field, nameof(Container.Location), StringComparison.Ordinal))
        {
            // Only the container carries a location, so no ambiguity is possible.
            return [Target.ForContainer(conflict.Field)];
        }

        for (var i = 0; i < container.Items.Count; i++)
        {
            var item = container.Items[i];
            if (HasField(item, conflict.Field) && ValueMatches(item, conflict.Field, conflict.LocalValue))
            {
                targets.Add(Target.ForItem(i, conflict.Field));
            }
        }

        if (HasField(container, conflict.Field) && ValueMatches(container, conflict.Field, conflict.LocalValue))
        {
            targets.Add(Target.ForContainer(conflict.Field));
        }

        return targets;
    }

    private readonly record struct Target(bool IsContainer, string Field, int? ItemIndex)
    {
        public static Target ForContainer(string field) => new(true, field, null);

        public static Target ForItem(int index, string field) => new(false, field, index);
    }

    private static bool HasField(Container container, string field) =>
        ContainerFields.Contains(field, StringComparer.Ordinal);

    private static bool HasField(Item item, string field) =>
        ItemFields.Contains(field, StringComparer.Ordinal);

    private static bool ValueMatches(Container container, string field, string? localValue) =>
        string.Equals(ReadField(container, field), localValue, StringComparison.Ordinal);

    private static bool ValueMatches(Item item, string field, string? localValue) =>
        string.Equals(ReadField(item, field), localValue, StringComparison.Ordinal);

    private static string? ReadField(Container container, string field) => field switch
    {
        nameof(Container.Name) => container.Name,
        nameof(Container.Location) => container.Location,
        nameof(Container.Notes) => container.Notes,
        _ => null,
    };

    private static string? ReadField(Item item, string field) => field switch
    {
        nameof(Item.Name) => item.Name,
        nameof(Item.Barcode) => item.Barcode,
        // Quantity is compared through its invariant text form because the merger reports
        // conflict values via Describe(), which stringifies to the same representation.
        nameof(Item.Quantity) => item.Quantity.ToString(CultureInfo.InvariantCulture),
        nameof(Item.Category) => item.Category,
        nameof(Item.Notes) => item.Notes,
        _ => null,
    };

    private static Container? RewriteContainerField(Container container, string field, string? value)
    {
        var text = value ?? "";
        return field switch
        {
            nameof(Container.Name) => container with { Name = text },
            nameof(Container.Location) => container with { Location = text },
            nameof(Container.Notes) => container with { Notes = text },
            _ => null,
        };
    }

    private static Container? RewriteItemField(Container container, int itemIndex, string field, string? value)
    {
        var items = container.Items.ToArray();
        var item = items[itemIndex];

        Item? updated = field switch
        {
            nameof(Item.Name) => item with { Name = value ?? "" },
            nameof(Item.Barcode) => item with { Barcode = value ?? "" },
            nameof(Item.Quantity) => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var q)
                ? item with { Quantity = q }
                : null,
            nameof(Item.Category) => item with { Category = value ?? "" },
            nameof(Item.Notes) => item with { Notes = value ?? "" },
            _ => null,
        };

        if (updated is null)
        {
            return null;
        }

        items[itemIndex] = updated;
        return container with { Items = items };
    }

    private static int IndexOfContainer(InventoryDocument document, string containerId)
    {
        var wanted = containerId.Trim();
        for (var i = 0; i < document.Containers.Count; i++)
        {
            if (string.Equals(document.Containers[i].Id.Trim(), wanted, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }
}
