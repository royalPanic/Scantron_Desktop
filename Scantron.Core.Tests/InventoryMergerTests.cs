using Scantron.Core.Merge;
using Scantron.Core.Models;
using Xunit;

namespace Scantron.Core.Tests;

/// <summary>
/// Exercises the three-way merge, which is the part of this project that can destroy data if it
/// is wrong: import on the handheld replaces the whole database.
/// </summary>
public sealed class InventoryMergerTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeMilliseconds(1_760_000_000_000);

    private static long Ms(long offsetSeconds = 0) => Now.ToUnixTimeMilliseconds() + (offsetSeconds * 1000);

    private static Item Item(
        string uuid = "u1",
        string name = "Drill",
        string barcode = "AAA",
        int quantity = 1,
        string category = "Tools",
        string notes = "",
        long updatedAt = 0) =>
        new()
        {
            Uuid = uuid,
            Name = name,
            Barcode = barcode,
            Quantity = quantity,
            Category = category,
            Notes = notes,
            UpdatedAt = updatedAt == 0 ? Now.ToUnixTimeMilliseconds() : updatedAt,
        };

    private static InventoryDocument Doc(params Container[] containers) =>
        new() { Containers = containers, Version = InventoryFormat.CurrentVersion };

    private static Container Cont(string id = "BOX-101", params Item[] items) =>
        new() { Id = id, Items = items, UpdatedAt = Now.ToUnixTimeMilliseconds() };

    private static Item Merged(InventoryDocument doc, string containerId = "BOX-101") =>
        doc.Containers.Single(c => c.Id == containerId).Items.Single();

    [Fact]
    public void Identical_sides_merge_to_no_conflicts()
    {
        var baseDoc = Doc(Cont("BOX-101", Item()));

        var result = InventoryMerger.Merge(baseDoc, baseDoc, baseDoc, Now);

        Assert.Empty(result.Conflicts);
        Assert.Equal(ItemMergeOutcome.Unchanged, result.ItemOutcomes.Values.Single());
    }

    [Fact]
    public void A_remote_only_change_is_taken()
    {
        // The handheld recorded a scan; the desktop is untouched.
        var baseDoc = Doc(Cont("BOX-101", Item(quantity: 1, updatedAt: Ms(-100))));
        var local = baseDoc;
        var remote = Doc(Cont("BOX-101", Item(quantity: 5, updatedAt: Ms(100))));

        var result = InventoryMerger.Merge(baseDoc, local, remote, Now);

        Assert.Empty(result.Conflicts);
        Assert.Equal(5, Merged(result.Document).Quantity);
    }

    [Fact]
    public void A_local_only_change_is_taken()
    {
        var baseDoc = Doc(Cont("BOX-101", Item(category: "Tools", updatedAt: Ms(-100))));
        var local = Doc(Cont("BOX-101", Item(category: "Power Tools", updatedAt: Ms(100))));
        var remote = baseDoc;

        var result = InventoryMerger.Merge(baseDoc, local, remote, Now);

        Assert.Empty(result.Conflicts);
        Assert.Equal("Power Tools", Merged(result.Document).Category);
    }

    [Fact]
    public void Changes_to_different_fields_both_survive()
    {
        // The highest-value case: warehouse conflicts are almost always quantity on one side
        // and metadata on the other, and this is the behaviour that makes them non-conflicts.
        var baseDoc = Doc(Cont("BOX-101", Item(quantity: 1, category: "Tools", updatedAt: Ms(-100))));
        var local = Doc(Cont("BOX-101", Item(quantity: 1, category: "Power Tools", updatedAt: Ms(100))));
        var remote = Doc(Cont("BOX-101", Item(quantity: 7, category: "Tools", updatedAt: Ms(100))));

        var result = InventoryMerger.Merge(baseDoc, local, remote, Now);

        Assert.Empty(result.Conflicts);
        var merged = Merged(result.Document);
        Assert.Equal(7, merged.Quantity);
        Assert.Equal("Power Tools", merged.Category);
    }

    [Fact]
    public void A_true_conflict_is_reported_and_never_silently_resolved()
    {
        var baseDoc = Doc(Cont("BOX-101", Item(quantity: 1, updatedAt: Ms(-100))));
        var local = Doc(Cont("BOX-101", Item(quantity: 3, updatedAt: Ms(100))));
        var remote = Doc(Cont("BOX-101", Item(quantity: 9, updatedAt: Ms(100))));

        var result = InventoryMerger.Merge(baseDoc, local, remote, Now);

        var conflict = Assert.Single(result.Conflicts);
        Assert.Equal("Quantity", conflict.Field);
        Assert.Equal("1", conflict.BaseValue);
        Assert.Equal("3", conflict.LocalValue);
        Assert.Equal("9", conflict.RemoteValue);
    }

    [Fact]
    public void A_remote_only_item_survives_the_merge()
    {
        // The whole point: a scan made in the warehouse must not vanish because the desktop
        // had never heard of it.
        var baseDoc = Doc(Cont("BOX-101", Item("Drill", "u1")));
        var local = baseDoc;
        var remote = Doc(Cont("BOX-101", Item("Drill", "u1"), Item("Mallet", "u2")));

        var result = InventoryMerger.Merge(baseDoc, local, remote, Now);

        Assert.Equal(2, result.Document.Containers.Single().Items.Count);
        Assert.Contains(result.ItemOutcomes.Values, o => o == ItemMergeOutcome.AddedRemotely);
    }

    [Fact]
    public void A_local_only_item_is_carried_through()
    {
        var baseDoc = Doc(Cont("BOX-101", Item("Drill", "u1")));
        var local = Doc(Cont("BOX-101", Item("Drill", "u1"), Item("Chisel", "u2")));
        var remote = baseDoc;

        var result = InventoryMerger.Merge(baseDoc, local, remote, Now);

        Assert.Contains(result.ItemOutcomes.Values, o => o == ItemMergeOutcome.AddedLocally);
        Assert.Equal(2, result.Document.Containers.Single().Items.Count);
    }

    [Fact]
    public void A_container_only_on_the_handheld_is_not_dropped()
    {
        var local = Doc(Cont("BOX-101"));
        var remote = Doc(Cont("BOX-101"), Cont("BIN-42"));

        var result = InventoryMerger.Merge(null, local, remote, Now);

        Assert.Equal(2, result.Document.Containers.Count);
        Assert.Contains(result.Document.Containers, c => c.Id == "BIN-42");
    }

    [Fact]
    public void A_container_only_on_the_desktop_is_not_dropped()
    {
        var local = Doc(Cont("BOX-101"), Cont("BIN-42"));
        var remote = Doc(Cont("BOX-101"));

        var result = InventoryMerger.Merge(null, local, remote, Now);

        Assert.Contains(result.Document.Containers, c => c.Id == "BIN-42");
    }

    [Fact]
    public void A_merged_timestamp_is_never_older_than_either_input()
    {
        // Otherwise the row loses the same argument again on the next sync.
        var baseDoc = Doc(Cont("BOX-101", Item(quantity: 1, updatedAt: Ms(-100))));
        var local = Doc(Cont("BOX-101", Item(quantity: 1, updatedAt: Ms(-50))));
        var remote = Doc(Cont("BOX-101", Item(quantity: 9, updatedAt: Ms(500))));

        var result = InventoryMerger.Merge(baseDoc, local, remote, Now);

        Assert.True(Merged(result.Document).UpdatedAt >= Ms(500));
    }

    [Fact]
    public void Clock_skew_is_flagged_when_a_device_clock_is_wildly_off()
    {
        var baseDoc = Doc(Cont("BOX-101", Item()));
        var local = baseDoc;

        // The CK65's clock is a day out; naive last-write-wins would discard its edits.
        var skewed = Now.AddDays(-1);
        var remote = Doc(Cont(
            "BOX-101",
            new Item
            {
                Uuid = "u1", Name = "Drill", Barcode = "AAA", Quantity = 42,
                UpdatedAt = skewed.ToUnixTimeMilliseconds(),
            }));

        var result = InventoryMerger.Merge(baseDoc, local, remote, Now);

        Assert.True(result.ClockSkewDetected);
    }

    [Fact]
    public void Clock_skew_is_not_flagged_for_a_healthy_document()
    {
        var baseDoc = Doc(Cont("BOX-101", Item()));
        var result = InventoryMerger.Merge(baseDoc, baseDoc, baseDoc, Now);

        Assert.False(result.ClockSkewDetected);
    }

    [Fact]
    public void Merged_output_always_validates()
    {
        var baseDoc = Doc(Cont("BOX-101", Item(quantity: 1, updatedAt: Ms(-100))));
        var local = Doc(Cont("BOX-101", Item(quantity: 3, updatedAt: Ms(50))));
        var remote = Doc(Cont("BOX-101", Item(quantity: 9, updatedAt: Ms(50))));

        var result = InventoryMerger.Merge(baseDoc, local, remote, Now);

        Assert.Empty(result.Document.Containers.SelectMany(c => c.Items).Where(i => i.Name.Length == 0));
    }

    [Fact]
    public void Merged_output_declares_the_current_version()
    {
        var baseDoc = new InventoryDocument
        {
            Containers = [Cont()],
            Version = InventoryFormat.LegacyVersion,
        };

        var result = InventoryMerger.Merge(baseDoc, baseDoc, baseDoc, Now);

        Assert.Equal(InventoryFormat.CurrentVersion, result.Document.Version);
    }

    [Fact]
    public void Merged_legacy_items_gain_identity()
    {
        // Two sides of a 1.0 base have no uuid, so the merge must not emit unidentifiable rows.
        var legacy = new Item { Name = "Drill", Barcode = "AAA", Quantity = 1, UpdatedAt = Now.ToUnixTimeMilliseconds() };
        var doc = Doc(Cont("BOX-101", legacy));

        var result = InventoryMerger.Merge(null, doc, doc, Now);

        Assert.NotEqual("", Merged(result.Document).Uuid);
    }

    [Fact]
    public void A_missing_base_yields_no_conflicts_on_a_first_sync()
    {
        // With no shared history there is nothing to disagree about, so everything is adopted
        // rather than being flagged as a conflict the operator cannot act on.
        var local = Doc(Cont("BOX-101", Item(quantity: 3)));
        var remote = Doc(Cont("BOX-101", Item(quantity: 9)));

        var result = InventoryMerger.Merge(null, local, remote, Now);

        Assert.Empty(result.Conflicts);
    }
}