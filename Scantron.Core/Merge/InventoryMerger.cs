using Scantron.Core.Identity;
using Scantron.Core.Models;

namespace Scantron.Core.Merge;

/// <summary>
/// What happened to one item row during a merge.
/// </summary>
public enum ItemMergeOutcome
{
    /// <summary>Present in base and identical on both sides.</summary>
    Unchanged,

    /// <summary>Only the desktop changed it; the handheld's version was kept.</summary>
    TookLocal,

    /// <summary>Only the handheld changed it; the desktop's version was kept.</summary>
    TookRemote,

    /// <summary>
    /// Both sides changed the same field to different values. Never auto-resolved: a silently
    /// discarded quantity is the exact failure this whole design exists to prevent.
    /// </summary>
    Conflicted,

    /// <summary>Only the desktop has it. A desktop addition.</summary>
    AddedLocally,

    /// <summary>Only the handheld has it. Must survive the merge or scans are lost.</summary>
    AddedRemotely,

    /// <summary>In the base but absent from the handheld export. Carried forward, see the merger.</summary>
    DeletedRemotely,
}

/// <summary>
/// One field on which the two sides disagreed.
/// </summary>
/// <param name="Field">Dotted path, e.g. "Quantity" or "Notes".</param>
/// <param name="BaseValue">Value at the last agreed snapshot.</param>
/// <param name="LocalValue">Value on the desktop.</param>
/// <param name="RemoteValue">Value in the handheld export.</param>
public sealed record ItemConflict(
    string ContainerId,
    string ItemUuid,
    string ItemName,
    string Field,
    string? BaseValue,
    string? LocalValue,
    string? RemoteValue);

/// <summary>
/// The result of a three-way merge.
/// </summary>
/// <param name="Document">Merged document, ready to write.</param>
/// <param name="Conflicts">Fields needing a human decision. Empty means a clean merge.</param>
/// <param name="ItemOutcomes">Per-row disposition, for the diff view and for tests.</param>
/// <param name="ClockSkewDetected">
/// True when the newest timestamp in any input is implausibly far from
/// <paramref name="LocalNow"/>, which usually means one device's clock is wrong.
/// </param>
public sealed record MergeResult(
    InventoryDocument Document,
    IReadOnlyList<ItemConflict> Conflicts,
    IReadOnlyDictionary<string, ItemMergeOutcome> ItemOutcomes,
    bool ClockSkewDetected);

/// <summary>
/// Merges a desktop document with a handheld export against a shared base snapshot.
/// </summary>
/// <remarks>
/// <para>
/// A three-way merge, not a two-way one, because "changed" is undefined without a base: with
/// only local and remote you cannot tell a deliberate edit from an untouched row.
/// </para>
/// <para>
/// Field-level, not row-level: if the handheld moved <c>quantity</c> and the desktop moved
/// <c>category</c>, both wins are correct and neither is a conflict. Warehouse conflicts are
/// overwhelmingly quantity-versus-metadata, so this is the single highest-value behaviour here.
/// </para>
/// <para>
/// Conflicts are reported, never resolved. A true conflict is one both sides changed to
/// different values; picking either one silently loses data, and the operator has better
/// context than this code does.
/// </para>
/// </remarks>
public static class InventoryMerger
{
    /// <summary>
    /// Timestamps further than this from the local clock are treated as evidence of skew.
    /// </summary>
    /// <remarks>
    /// Each device stamps <c>updatedAt</c> with its own wall clock. If the CK65 is an hour out,
    /// naive last-write-wins would discard real handheld edits. Rather than silently trusting
    /// either clock, the merge still runs field-by-field but flags the document so the UI can
    /// warn that recency-based reasoning is unreliable here.
    /// </remarks>
    public static readonly TimeSpan MaxPlausibleSkew = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Merges <paramref name="local"/> and <paramref name="remote"/> against <paramref name="base"/>.
    /// </summary>
    /// <param name="base">
    /// Last state both sides agreed on. May be <see langword="null"/> for a first sync, in which
    /// case every difference is reported as a conflict rather than guessed at.
    /// </param>
    /// <param name="local">Desktop-side document.</param>
    /// <param name="remote">Fresh export from the handheld.</param>
    /// <param name="localNow">Local clock, injected for deterministic tests.</param>
    public static MergeResult Merge(
        InventoryDocument? @base,
        InventoryDocument local,
        InventoryDocument remote,
        DateTimeOffset localNow)
    {
        ArgumentNullException.ThrowIfNull(local);
        ArgumentNullException.ThrowIfNull(remote);

        var conflicts = new List<ItemConflict>();
        var outcomes = new Dictionary<string, ItemMergeOutcome>(StringComparer.Ordinal);

        var localById = local.Containers.ToDictionary(
            c => c.Id.Trim(),
            StringComparer.Ordinal);
        var remoteById = remote.Containers.ToDictionary(
            c => c.Id.Trim(),
            StringComparer.Ordinal);
        var baseById = @base?.Containers.ToDictionary(
                c => c.Id.Trim(),
                StringComparer.Ordinal)
            ?? new Dictionary<string, Container>(StringComparer.Ordinal);

        var mergedContainers = new List<Container>();

        // Union of ids, local first so the desktop's ordering is preserved where it has one.
        var allIds = new List<string>();
        allIds.AddRange(localById.Keys);
        allIds.AddRange(remoteById.Keys.Where(id => !localById.ContainsKey(id)));

        foreach (var id in allIds)
        {
            localById.TryGetValue(id, out var localContainer);
            remoteById.TryGetValue(id, out var remoteContainer);
            baseById.TryGetValue(id, out var baseContainer);

            // A container missing from the handheld export was either deleted there or simply
            // never synced. Treating absence as "no remote opinion" is the safe reading: it
            // keeps a container the desktop knows about rather than deleting it on a guess.
            var effectiveRemote = remoteContainer ?? localContainer;
            var effectiveLocal = localContainer ?? remoteContainer;

            if (effectiveLocal is null)
            {
                continue;
            }

            var merged = MergeContainer(
                            id,
                            effectiveLocal,
                            // effectiveLocal is non-null here, so this substitution cannot be null either.
                            effectiveRemote ?? effectiveLocal,
                            baseContainer,
                            conflicts,
                            outcomes,
                            localNow);

            mergedContainers.Add(merged);
        }

        var mergedDocument = new InventoryDocument
        {
            App = local.App,
            Version = InventoryFormat.CurrentVersion,
            // Stamped as an instant, then rendered in the device's local-wall-clock convention
            // so the handheld reads back the same moment the desktop meant.
            ExportedAt = InventoryFormat.FormatExportedAt(localNow),
            Containers = mergedContainers,
        };

        return new MergeResult(
            mergedDocument,
            conflicts,
            outcomes,
            DetectClockSkew(local, remote, localNow));
    }

    private static Container MergeContainer(
        string containerId,
        Container local,
        Container remote,
        Container? @base,
        List<ItemConflict> conflicts,
        Dictionary<string, ItemMergeOutcome> outcomes,
        DateTimeOffset localNow)
    {
        // Field-level merge of the container's own metadata, same rules as items.
        var name = MergeField(
            containerId, string.Empty, "Name", @base?.Name, local.Name, remote.Name, conflicts);
        var location = MergeField(
            containerId, string.Empty, "Location", @base?.Location, local.Location, remote.Location, conflicts);
        var notes = MergeField(
            containerId, string.Empty, "Notes", @base?.Notes, local.Notes, remote.Notes, conflicts);

        var baseItems = @base?.Items ?? [];
        var localItems = ItemKeyResolver.IndexByKey(containerId, local.Items);
        var remoteItems = ItemKeyResolver.IndexByKey(containerId, remote.Items);
        var baseItemsByKey = ItemKeyResolver.IndexByKey(containerId, baseItems);

        var mergedItems = new List<Item>();
        var seenKeys = new HashSet<ItemKey>();

        foreach (var key in localItems.Keys.Concat(
                     remoteItems.Keys.Where(k => !localItems.ContainsKey(k))))
        {
            if (!seenKeys.Add(key))
            {
                continue;
            }

            localItems.TryGetValue(key, out var localItem);
            remoteItems.TryGetValue(key, out var remoteItem);
            baseItemsByKey.TryGetValue(key, out var baseItem);

            // Same "absence is not a verdict" rule as containers: never drop a row just
            // because one side's export did not list it.
            var effectiveLocal = localItem ?? remoteItem;
            var effectiveRemote = remoteItem ?? localItem;

            if (effectiveLocal is null)
            {
                continue;
            }

            var merged = MergeItem(
                containerId,
                effectiveLocal,
                effectiveRemote!,
                baseItem,
                key,
                localItem is not null,
                remoteItem is not null,
                conflicts,
                outcomes,
                localNow);

            mergedItems.Add(merged);
        }

        // A row that existed at the last sync and is now gone from the handheld is flagged
        // rather than resurrected blindly: it may be a deliberate delete. It is carried into
        // the output so the operator can decide, which is safer than dropping stock silently.
        foreach (var (key, baseItem) in baseItemsByKey)
        {
            if (localItems.ContainsKey(key) || remoteItems.ContainsKey(key))
            {
                continue;
            }

            outcomes[OutcomeKey(containerId, baseItem)] = ItemMergeOutcome.DeletedRemotely;
            conflicts.Add(new ItemConflict(
                containerId,
                baseItem.Uuid,
                baseItem.Name,
                "Presence",
                "present",
                "absent",
                "absent"));
        }

        return new Container
        {
            Id = containerId,
            Name = name,
            Location = location,
            Notes = notes,
            // Never older than either input, so a merge cannot lose again on the next sync.
            UpdatedAt = Math.Max(
                Math.Max(local.UpdatedAt, remote.UpdatedAt),
                mergedItems.Count == 0 ? 0 : mergedItems.Max(i => i.UpdatedAt)),
            Items = mergedItems,
        };
    }

    private static Item MergeItem(
        string containerId,
        Item local,
        Item remote,
        Item? @base,
        ItemKey key,
        bool presentLocally,
        bool presentRemotely,
        List<ItemConflict> conflicts,
        Dictionary<string, ItemMergeOutcome> outcomes,
        DateTimeOffset localNow)
    {
        var outcome = ClassifyOutcome(@base, local, remote, presentLocally, presentRemotely);

        if (outcome is ItemMergeOutcome.AddedLocally or ItemMergeOutcome.AddedRemotely)
        {
            outcomes[OutcomeKey(containerId, local)] = outcome;

            // An added row is taken wholesale: there is no base to diff against, so any
            // attempt at field merging would be invention. The timestamp is still advanced so
            // the row sorts sensibly and wins the next sync.
            var adopted = outcome == ItemMergeOutcome.AddedLocally ? local : remote;
            return Stamp(adopted, Math.Max(local.UpdatedAt, remote.UpdatedAt), localNow);
        }

        var mergedName = MergeField(
            containerId, local.Uuid, "Name", @base?.Name, local.Name, remote.Name, conflicts);
        var mergedBarcode = MergeField(
            containerId, local.Uuid, "Barcode", @base?.Barcode, local.Barcode, remote.Barcode, conflicts);
        var mergedQuantity = MergeField(
            containerId, local.Uuid, "Quantity", @base?.Quantity, local.Quantity, remote.Quantity, conflicts);
        var mergedCategory = MergeField(
            containerId, local.Uuid, "Category", @base?.Category, local.Category, remote.Category, conflicts);
        var mergedNotes = MergeField(
            containerId, local.Uuid, "Notes", @base?.Notes, local.Notes, remote.Notes, conflicts);

        outcomes[OutcomeKey(containerId, local)] = outcome;

        var merged = local with
        {
            // Identity is taken from whichever side has one. A legacy base can leave both sides
            // without a uuid; in that case the merged row is minted so the output is a valid
            // 1.1 document rather than silently re-introducing unidentifiable rows.
            Uuid = FirstNonBlank(local.Uuid, remote.Uuid) ?? Guid.NewGuid().ToString(),
            Name = mergedName,
            Barcode = mergedBarcode,
            Quantity = mergedQuantity,
            Category = mergedCategory,
            Notes = mergedNotes,
        };

        return Stamp(merged, Math.Max(local.UpdatedAt, remote.UpdatedAt), localNow);
    }

    private static ItemMergeOutcome ClassifyOutcome(
        Item? @base,
        Item local,
        Item remote,
        bool presentLocally,
        bool presentRemotely)
    {
        if (presentLocally && !presentRemotely)
        {
            return ItemMergeOutcome.AddedLocally;
        }

        if (!presentLocally && presentRemotely)
        {
            return ItemMergeOutcome.AddedRemotely;
        }

        // No base row means one side has no opinion to compare against, so this is a first
        // sync rather than a conflict. Everything is taken as remote-new or local-new.
        if (@base is null)
        {
            return local == remote ? ItemMergeOutcome.Unchanged : ItemMergeOutcome.TookLocal;
        }

        if (local == remote)
        {
            return ItemMergeOutcome.Unchanged;
        }

        return local == @base ? ItemMergeOutcome.TookRemote : ItemMergeOutcome.TookLocal;
    }

    /// <summary>
    /// Three-way resolution for a single field.
    /// </summary>
    /// <remarks>
    /// Without a base value the two sides cannot be compared meaningfully, so the local side
    /// wins and the situation is reported. That is the safe direction for a desktop tool: it
    /// is the side the operator is looking at.
    /// </remarks>
    private static int MergeField(
        string containerId,
        string itemUuid,
        string field,
            int? baseValue,
            int local,
            int remote,
        List<ItemConflict> conflicts)
        {
            if (local == remote)
            {
                return local;
            }

            if (baseValue is null)
            {
                // No base means no shared history, so there is nothing to have "disagreed" about.
                // Reporting a conflict here would show the operator a field they have no way to
                // resolve, on a first sync where every row looks like one. Local wins by default
                // because this is a desktop tool and local is the side being looked at.
                return local;
            }

            if (local == baseValue.Value)
            {
                return remote;
            }

            if (remote == baseValue.Value)
            {
                return local;
            }

            conflicts.Add(new ItemConflict(
                containerId,
                itemUuid,
                field,
                field,
                Describe(baseValue.Value),
                Describe(local),
                Describe(remote)));

            return local;
        }

        private static string MergeField(
            string containerId,
            string itemUuid,
            string field,
            string? baseValue,
            string local,
            string remote,
            List<ItemConflict> conflicts)
        {
            if (string.Equals(local, remote, StringComparison.Ordinal))
            {
                return local;
            }

            if (baseValue is null)
            {
                // See the int overload: without a base there is no meaningful conflict to report.
                return local;
            }

            if (string.Equals(local, baseValue, StringComparison.Ordinal))
            {
                return remote;
            }

            if (string.Equals(remote, baseValue, StringComparison.Ordinal))
            {
                return local;
            }

            conflicts.Add(new ItemConflict(
                containerId,
                itemUuid,
                field,
                field,
                Describe(baseValue),
                Describe(local),
                Describe(remote)));

            return local;
        }

    /// <summary>
    /// Advances a merged row's timestamp past every input.
    /// </summary>
    /// <remarks>
    /// A merge that emitted the older <c>updatedAt</c> would lose the same argument again on
    /// the next sync, so the result is always at least "now".
    /// </remarks>
    private static Item Stamp(Item item, long newestInput, DateTimeOffset localNow) =>
        item with { UpdatedAt = Math.Max(item.UpdatedAt, Math.Max(newestInput, localNow.ToUnixTimeMilliseconds())) };

    private static bool DetectClockSkew(
        InventoryDocument local,
        InventoryDocument remote,
        DateTimeOffset localNow)
    {
        var now = localNow.ToUnixTimeMilliseconds();
        var tolerance = (long)MaxPlausibleSkew.TotalMilliseconds;

        return local.Containers
            .SelectMany(c => c.Items.Append<Item>(
                new Item { Name = c.Id, UpdatedAt = c.UpdatedAt }))
            .Concat(remote.Containers
                .SelectMany(c => c.Items.Append<Item>(
                    new Item { Name = c.Id, UpdatedAt = c.UpdatedAt })))
            .Any(i => Math.Abs(i.UpdatedAt - now) > tolerance);
    }

    private static string OutcomeKey(string containerId, Item item) =>
        $"{containerId.Trim()}/{item.Uuid}/{ItemKeyResolver.Resolve(containerId, item).Value}";

    private static string? FirstNonBlank(string? a, string? b) =>
        !string.IsNullOrWhiteSpace(a) ? a.Trim()
        : !string.IsNullOrWhiteSpace(b) ? b.Trim()
        : null;

    private static string? Describe<T>(T value) =>
        value switch
        {
            null => null,
            int i => i.ToString(System.Globalization.CultureInfo.InvariantCulture),
            long l => l.ToString(System.Globalization.CultureInfo.InvariantCulture),
            _ => value.ToString(),
        };
}