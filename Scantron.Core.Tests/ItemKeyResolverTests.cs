using Scantron.Core.Identity;
using Scantron.Core.Models;
using Xunit;

namespace Scantron.Core.Tests;

/// <summary>
/// Pins the key rules the merger depends on. These must stay identical to the device's
/// <c>addItemMerging</c> behaviour, or the desktop and handheld will disagree about which rows
/// are the same item.
/// </summary>
public sealed class ItemKeyResolverTests
{
    [Fact]
    public void A_uuid_takes_precedence_over_everything()
    {
        var key = ItemKeyResolver.Resolve("BOX-101", new Item
        {
            Name = "Drill",
            Uuid = "abc",
            Barcode = "AAA",
        });

        Assert.Equal(ItemKeyKind.Uuid, key.Kind);
        Assert.Equal("abc", key.Value);
    }

    [Fact]
    public void A_barcoded_item_without_a_uuid_keys_on_container_and_barcode()
    {
        var key = ItemKeyResolver.Resolve("BOX-101", new Item
        {
            Name = "Drill",
            Barcode = "AAA",
        });

        Assert.Equal(ItemKeyKind.ContainerBarcode, key.Kind);
    }

    [Fact]
    public void A_name_only_item_keys_on_container_and_lowercased_name()
    {
        var key = ItemKeyResolver.Resolve("BOX-101", new Item { Name = "  Drill  " });

        Assert.Equal(ItemKeyKind.NameOnly, key.Kind);
    }

    [Fact]
    public void The_same_barcode_in_two_containers_keys_differently()
    {
        // The device scopes barcode matching to the container, so one barcode legitimately
        // exists in several containers. Collapsing them would merge unrelated stock.
        var item = new Item { Name = "Drill", Barcode = "AAA" };

        var a = ItemKeyResolver.Resolve("BOX-101", item);
        var b = ItemKeyResolver.Resolve("BIN-42", item);

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Name_matching_is_case_and_whitespace_insensitive()
    {
        var a = ItemKeyResolver.Resolve("BOX-101", new Item { Name = "Drill" });
        var b = ItemKeyResolver.Resolve("BOX-101", new Item { Name = "  dRiLl " });

        Assert.Equal(a, b);
    }

    [Fact]
    public void Barcode_matching_ignores_surrounding_whitespace()
    {
        var a = ItemKeyResolver.Resolve("BOX-101", new Item { Name = "Drill", Barcode = "AAA" });
        var b = ItemKeyResolver.Resolve("BOX-101", new Item { Name = "Drill", Barcode = "  AAA  " });

        Assert.Equal(a, b);
    }

    [Fact]
    public void A_barcoded_item_is_never_keyed_by_name()
    {
        // Its barcode is its identity; matching it by name could conflate two different SKUs
        // that happen to share a description.
        var barcoded = ItemKeyResolver.Resolve("BOX-101", new Item { Name = "Drill", Barcode = "AAA" });
        var nameOnly = ItemKeyResolver.Resolve("BOX-101", new Item { Name = "Drill" });

        Assert.NotEqual(barcoded, nameOnly);
    }

    [Fact]
    public void A_separator_inside_a_container_tag_cannot_forge_a_key()
    {
        // Guards the key separator: operator-entered free text must not be able to construct
        // a key that collides with a different (container, value) pair.
        var real = ItemKeyResolver.Resolve("A", new Item { Name = "X", Barcode = "B" });
        var forged = ItemKeyResolver.Resolve("A\0B", new Item { Name = "X", Barcode = "" });

        Assert.NotEqual(real, forged);
    }

    [Fact]
    public void IndexByKey_keeps_the_newest_row_when_two_collide()
    {
        // Mirrors the device, whose queries order updatedAt DESC before LIMIT 1. Without this
        // the two sides could pick different rows from the same legacy document and oscillate.
        var older = new Item { Name = "Drill", Barcode = "AAA", UpdatedAt = 1_000 };
        var newer = new Item { Name = "Drill", Barcode = "AAA", UpdatedAt = 2_000 };

        var index = ItemKeyResolver.IndexByKey("BOX-101", [older, newer]);

        Assert.Single(index);
        Assert.Equal(2_000, index.Values.Single().UpdatedAt);
    }

        [Fact]
        public void IndexByKey_keeps_both_rows_when_one_uuid_is_claimed_twice()
        {
            // The device re-mints the second row rather than discarding it (assignMissingItemUuids),
            // so collapsing a shared UUID here would silently drop stock the handheld still holds.
            var first = new Item { Name = "Drill", Uuid = "same-uuid", UpdatedAt = 1_000 };
            var second = new Item { Name = "Screws", Uuid = "same-uuid", UpdatedAt = 2_000 };

            var index = ItemKeyResolver.IndexByKey("BOX-101", [first, second]);

            Assert.Equal(2, index.Count);
            Assert.Equal("Drill", index[new ItemKey(ItemKeyKind.Uuid, "same-uuid")].Name);

            // The re-minted row must carry a new identity, and no two rows may claim the same one.
            var reminted = Assert.Single(index.Values, i => i.Name == "Screws");
            Assert.NotEqual("same-uuid", reminted.Uuid);

            var identities = index.Values.Select(i => i.Uuid).ToList();
            Assert.Equal(identities.Count, identities.Distinct(StringComparer.Ordinal).Count());
        }

        [Fact]
        public void IndexByKey_gives_a_uuid_and_a_heuristic_key_the_same_row()
        {
            // A legacy export can carry one row with an identity and another without. Once both have
            // been indexed they must not be merged together just because their other fields agree.
            var withUuid = new Item { Name = "Drill", Uuid = "abc", Barcode = "AAA" };
            var legacy = new Item { Name = "Drill", Barcode = "AAA" };

            var index = ItemKeyResolver.IndexByKey("BOX-101", [withUuid, legacy]);

            Assert.Equal(2, index.Count);
            Assert.Equal(ItemKeyKind.Uuid, ItemKeyResolver.Resolve("BOX-101", index.Values.First(i => i.Uuid == "abc")).Kind);
        }

    [Fact]
    public void IsNameOnly_treats_whitespace_as_no_barcode()
    {
        Assert.True(new Item { Name = "x", Barcode = "   " }.IsNameOnly);
        Assert.False(new Item { Name = "x", Barcode = "AAA" }.IsNameOnly);
    }
}