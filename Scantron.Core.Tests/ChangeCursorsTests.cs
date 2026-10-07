using Scantron.Core.Models;
using Scantron.Core.Sync;
using Xunit;

namespace Scantron.Core.Tests;

/// <summary>
/// Covers the diff that decides what to send. It is also the echo suppressor, so the cases that
/// matter most are the ones that must produce <em>nothing</em>.
/// </summary>
public sealed class ChangeCursorsTests
{
    private static Item Row(string uuid = "u1", string name = "Drill", int quantity = 1, long updatedAt = 100) =>
        new()
        {
            Uuid = uuid,
            Name = name,
            Barcode = "AAA",
            Quantity = quantity,
            UpdatedAt = updatedAt,
        };

    private static Container Cont(string id = "BOX-101", params Item[] items) =>
        new() { Id = id, Items = items, UpdatedAt = 100 };

    private static InventoryDocument Doc(params Container[] containers) =>
        new() { Containers = containers, Version = InventoryFormat.CurrentVersion };

    [Fact]
    public void No_base_produces_a_single_snapshot()
    {
        var ops = ChangeCursors.Diff(null, Doc(Cont("BOX-101", Row())));

        var snapshot = Assert.IsType<SnapshotOp>(Assert.Single(ops));
        Assert.Single(snapshot.Document.Containers);
    }

    [Fact]
    public void An_unchanged_document_produces_nothing()
    {
        // This is the echo suppressor at work: once a change has been applied the base matches, so
        // it is never sent back to the device that sent it.
        var document = Doc(Cont("BOX-101", Row()));

        Assert.Empty(ChangeCursors.Diff(document, document));
    }

    [Fact]
    public void A_changed_row_is_sent_as_one_upsert()
    {
        var @base = Doc(Cont("BOX-101", Row(quantity: 1)));
        var current = Doc(Cont("BOX-101", Row(quantity: 4)));

        var op = Assert.IsType<UpsertItemOp>(Assert.Single(ChangeCursors.Diff(@base, current)));

        Assert.Equal("BOX-101", op.ContainerId);
        Assert.Equal(4, op.Item.Quantity);
    }

    [Fact]
    public void A_container_upsert_never_carries_its_rows()
    {
        // Rows travel separately so that a removal is expressible as a removal. A container upsert
        // that listed rows would let a delete look like an omission, which the merge reads as
        // "no opinion" and carries forward.
        var @base = Doc(Cont("BOX-101", Row()) with { Name = "Old" });
        var current = Doc(Cont("BOX-101", Row()) with { Name = "New" });

        var op = Assert.IsType<UpsertContainerOp>(Assert.Single(ChangeCursors.Diff(@base, current)));

        Assert.Equal("New", op.Container.Name);
        Assert.Empty(op.Container.Items);
    }

    [Fact]
    public void A_row_removed_since_the_base_is_sent_as_a_deletion()
    {
        var @base = Doc(Cont("BOX-101", Row(uuid: "u1"), Row(uuid: "u2", name: "Tape")));
        var current = Doc(Cont("BOX-101", Row(uuid: "u1")));

        var op = Assert.Single(ChangeCursors.Diff(@base, current));
        var delete = Assert.IsType<DeleteItemOp>(op);

        Assert.Equal("u2", delete.ItemUuid);
        Assert.Equal("Tape", delete.ItemName);
    }

    [Fact]
    public void A_container_removed_since_the_base_is_sent_as_a_deletion()
    {
        var @base = Doc(Cont("BOX-101", Row()), Cont("BIN-42", Row(uuid: "u9")));
        var current = Doc(Cont("BOX-101", Row()));

        var op = Assert.Single(ChangeCursors.Diff(@base, current));
        Assert.IsType<DeleteContainerOp>(op);
    }

    [Fact]
    public void A_row_frozen_by_an_open_conflict_is_withheld()
    {
        // Sending it would either re-report the conflict forever or settle it in favour of whoever
        // synced last. It stays put until a human decides.
        var @base = Doc(Cont("BOX-101", Row(uuid: "u1", quantity: 1), Row(uuid: "u2", name: "Tape")));
        var current = Doc(Cont("BOX-101", Row(uuid: "u1", quantity: 4), Row(uuid: "u2", name: "Tape 25ft")));

        var blocked = new HashSet<string>(StringComparer.Ordinal)
        {
            ChangeCursors.RowId("BOX-101", Row(uuid: "u1")),
        };

        var op = Assert.IsType<UpsertItemOp>(Assert.Single(ChangeCursors.Diff(@base, current, blocked)));

        Assert.Equal("u2", op.Item.Uuid);
    }

    [Fact]
    public void ConflictRows_marks_a_container_level_conflict_on_the_container()
    {
        // A conflict with no row uuid is a disagreement about the container's own name or location,
        // so it has to freeze the container rather than a row.
        var conflicts = new List<Scantron.Core.Merge.ItemConflict>
        {
            new("BOX-101", "", "Drill", "Name", "a", "b", "c"),
        };

        var rows = ChangeCursors.ConflictRows(conflicts);

        Assert.Contains("BOX-101", rows);
    }
}
