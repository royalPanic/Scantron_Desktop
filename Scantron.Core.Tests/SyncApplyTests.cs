using Scantron.Core.Models;
using Scantron.Core.Sync;
using Xunit;

namespace Scantron.Core.Tests;

/// <summary>
/// Covers the two properties live sync has to have: a change made on one device is reproduced on
/// the other, and two devices that start from the same base agree again after exchanging changes.
/// </summary>
public sealed class SyncApplyTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeMilliseconds(1_760_000_000_000);

    private static Item Row(
        string uuid = "u1",
        string name = "Drill",
        string barcode = "AAA",
        int quantity = 1,
        string category = "Tools",
        string notes = "") =>
        new()
        {
            Uuid = uuid,
            Name = name,
            Barcode = barcode,
            Quantity = quantity,
            Category = category,
            Notes = notes,
            UpdatedAt = Now.ToUnixTimeMilliseconds(),
        };

    private static Container Cont(string id = "BOX-101", params Item[] items) =>
        new() { Id = id, Items = items, UpdatedAt = Now.ToUnixTimeMilliseconds() };

    private static InventoryDocument Doc(params Container[] containers) =>
        new() { Containers = containers, Version = InventoryFormat.CurrentVersion };

    private static Item Only(InventoryDocument document, string containerId = "BOX-101") =>
        document.Containers.Single(c => c.Id == containerId).Items.Single();

    // ---- reconstruction: base + ops is the peer's view -------------------------------------------

    [Fact]
    public void An_item_upsert_reconstructs_the_peers_row()
    {
        var @base = Doc(Cont("BOX-101", Row(quantity: 1)));

        var remote = SyncApply.Reconstruct(@base, [new UpsertItemOp("BOX-101", Row(quantity: 12))]);

        Assert.Equal(12, Only(remote).Quantity);
    }

    [Fact]
    public void A_container_upsert_does_not_drop_the_rows_the_base_already_holds()
    {
        // The wire deliberately omits items from a container upsert, so applying one must not read
        // as "this container is now empty" - that would delete every row the peer never mentioned.
        var @base = Doc(Cont("BOX-101", Row(uuid: "u1"), Row(uuid: "u2", name: "Tape")));

        var remote = SyncApply.Reconstruct(
            @base,
            [new UpsertContainerOp(new Container { Id = "BOX-101", Name = "Hardware" })]);

        var container = remote.Containers.Single();
        Assert.Equal("Hardware", container.Name);
        Assert.Equal(2, container.Items.Count);
    }

    [Fact]
    public void A_delete_removes_exactly_one_row()
    {
        var @base = Doc(Cont("BOX-101", Row(uuid: "u1"), Row(uuid: "u2", name: "Tape")));

        var remote = SyncApply.Reconstruct(
            @base,
            [new DeleteItemOp("BOX-101", "u1", Scantron.Core.Identity.ItemKeyKind.Uuid, "u1", "Drill", 1)]);

        Assert.Equal("u2", Only(remote).Uuid);
    }

    [Fact]
    public void A_legacy_row_with_no_uuid_is_deleted_by_its_merge_key()
    {
        // The one case a uuid cannot carry: a 1.0 row has none, so the deletion travels with the
        // key the emitting device computed and the receiver recomputes the same key from its copy.
        var legacy = Row(uuid: "", name: "Tape", barcode: "BBB");
        var @base = Doc(Cont("BOX-101", legacy));

        var remote = SyncApply.Reconstruct(
            @base,
            [new DeleteItemOp("BOX-101", "", Scantron.Core.Identity.ItemKeyKind.ContainerBarcode, "BOX-101\0BBB", "Tape", 1)]);

        Assert.Empty(remote.Containers.Single().Items);
    }

    // ---- deletes are instructions, not omissions --------------------------------------------------

    [Fact]
    public void A_remote_delete_removes_the_row_and_advances_the_base()
    {
        // The bug this pins: the merger reads a row missing from one side as "no opinion" and carries
        // it forward, which is right for a partial export but wrong for a tombstone. A delete that
        // only reached Reconstruct was therefore undone by the merge, so the row came back on every
        // sync and the operator saw a "Presence" conflict they had no way to settle.
        var @base = Doc(Cont("BOX-101", Row(uuid: "u1"), Row(uuid: "u2", name: "Tape")));
        var local = Doc(Cont("BOX-101", Row(uuid: "u1"), Row(uuid: "u2", name: "Tape")));

        var result = SyncApply.Apply(
            @base,
            local,
            [new DeleteItemOp("BOX-101", "u1", Scantron.Core.Identity.ItemKeyKind.Uuid, "u1", "Drill", 1)],
            Now);

        Assert.Equal("u2", Only(result.Document).Uuid);
        Assert.Equal("u2", Only(result.Base).Uuid);
        Assert.Empty(result.Conflicts);
    }

    [Fact]
    public void A_remote_delete_converges_when_the_row_was_already_gone_locally()
    {
        // Both devices deleted the same row. Neither should be told the other disagrees, and the
        // second application must be a no-op rather than re-reporting the deletion.
        var @base = Doc(Cont("BOX-101", Row()));
        var local = Doc(Cont("BOX-101"));

        var first = SyncApply.Apply(
            @base, local,
            [new DeleteItemOp("BOX-101", "u1", Scantron.Core.Identity.ItemKeyKind.Uuid, "u1", "Drill", 1)],
            Now);

        Assert.Empty(first.Document.Containers.Single().Items);
        Assert.Empty(first.Base.Containers.Single().Items);
        Assert.Empty(first.Conflicts);

        var replay = SyncApply.Apply(
            first.Base, first.Document,
            [new DeleteItemOp("BOX-101", "u1", Scantron.Core.Identity.ItemKeyKind.Uuid, "u1", "Drill", 1)],
            Now);

        Assert.Empty(replay.Document.Containers.Single().Items);
        Assert.Empty(replay.Conflicts);
    }

    [Fact]
    public void A_remote_container_delete_removes_the_container_and_its_rows()
    {
        var @base = Doc(Cont("BOX-101", Row()), Cont("BOX-202", Row(uuid: "u9", name: "Tape")));
        var local = Doc(Cont("BOX-101", Row()), Cont("BOX-202", Row(uuid: "u9", name: "Tape")));

        var result = SyncApply.Apply(@base, local, [new DeleteContainerOp("BOX-101", 1)], Now);

        Assert.Equal("BOX-202", result.Document.Containers.Single().Id);
        Assert.Equal("BOX-202", result.Base.Containers.Single().Id);
        Assert.Empty(result.Conflicts);
    }

    [Fact]
    public void A_delete_wins_over_a_local_edit_to_the_same_row()
    {
        // The delete-vs-edit race. The tombstone is an instruction and is applied, and the local edit
        // goes with the row. This is deliberately NOT reported as a conflict, and the reason is not
        // that the edit does not matter - it is that a conflict here would be unanswerable. The row
        // is gone, so a resolution has nothing to set a field on: it would be filtered out of the
        // list without changing anything, leaving the operator a conflict that silently disappears
        // when they try to settle it. A conflict that cannot be acted on is worse than none.
        var @base = Doc(Cont("BOX-101", Row(quantity: 1)));
        var local = Doc(Cont("BOX-101", Row(quantity: 5)));

        var result = SyncApply.Apply(
            @base,
            local,
            [new DeleteItemOp("BOX-101", "u1", Scantron.Core.Identity.ItemKeyKind.Uuid, "u1", "Drill", 1)],
            Now);

        Assert.Empty(result.Document.Containers.Single().Items);
        Assert.Empty(result.Base.Containers.Single().Items);
        Assert.Empty(result.Conflicts);
    }

    [Fact]
    public void A_delete_does_not_settle_a_conflict_about_a_field_value()
    {
        // A tombstone says nothing about what a field's value should be, so it must not be allowed to
        // close a disagreement about one. If it did, deleting a row would silently discard a
        // quantity the operator had deliberately chosen - and they would never be asked.
        var @base = Doc(Cont("BOX-101", Row(quantity: 1)));
        var local = Doc(Cont("BOX-101", Row(quantity: 5)));

        var result = SyncApply.Apply(
            @base,
            local,
            [
                new UpsertItemOp("BOX-101", Row(quantity: 9)),
                new DeleteItemOp("BOX-101", "u1", Scantron.Core.Identity.ItemKeyKind.Uuid, "u1", "Drill", 1),
            ],
            Now);

        Assert.Empty(result.Document.Containers.Single().Items);

        // The row is gone, so the quantity disagreement is moot and must NOT be left open - an
        // operator cannot be asked to choose a value for a row that no longer exists.
        Assert.DoesNotContain(result.Conflicts, c => c.Field == "Quantity");
    }

    [Fact]
    public void A_delete_of_a_row_the_base_never_held_is_harmless()
    {
        // A tombstone can arrive for a row this device never saw - the peer added and deleted it
        // between syncs. That is not an error and must not throw or invent a conflict.
        var @base = Doc(Cont("BOX-101", Row(uuid: "u1")));
        var local = Doc(Cont("BOX-101", Row(uuid: "u1")));

        var result = SyncApply.Apply(
            @base,
            local,
            [new DeleteItemOp("BOX-101", "ghost", Scantron.Core.Identity.ItemKeyKind.Uuid, "ghost", "Ghost", 1)],
            Now);

        Assert.Equal("u1", Only(result.Document).Uuid);
        Assert.Empty(result.Conflicts);
    }

    // ---- the two core behaviours ------------------------------------------------------------------

    [Fact]
    public void A_remote_only_change_is_applied_and_advances_the_base()
    {
        var @base = Doc(Cont("BOX-101", Row(quantity: 1)));

        var result = SyncApply.Apply(@base, @base, [new UpsertItemOp("BOX-101", Row(quantity: 5))], Now);

        Assert.Empty(result.Conflicts);
        Assert.Equal(5, Only(result.Document).Quantity);
        Assert.Equal(5, Only(result.Base).Quantity);
    }

    [Fact]
    public void A_local_only_change_survives_and_advances_the_base()
    {
        var @base = Doc(Cont("BOX-101", Row(category: "Tools")));
        var local = Doc(Cont("BOX-101", Row(category: "Power Tools")));

        var result = SyncApply.Apply(@base, local, [], Now);

        Assert.Empty(result.Conflicts);
        Assert.Equal("Power Tools", Only(result.Document).Category);
        Assert.Equal("Power Tools", Only(result.Base).Category);
    }

    [Fact]
    public void Edits_to_different_fields_on_the_two_devices_both_survive()
    {
        // The highest-value behaviour: the handheld moved quantity, the desktop moved category.
        // Neither edit is lost and neither is a conflict.
        var @base = Doc(Cont("BOX-101", Row(quantity: 1, category: "Tools")));
        var local = Doc(Cont("BOX-101", Row(quantity: 1, category: "Power Tools")));

        var result = SyncApply.Apply(@base, local, [new UpsertItemOp("BOX-101", Row(quantity: 9, category: "Tools"))], Now);

        Assert.Empty(result.Conflicts);
        Assert.Equal(9, Only(result.Document).Quantity);
        Assert.Equal("Power Tools", Only(result.Document).Category);
    }

    [Fact]
    public void The_same_field_changed_on_both_devices_is_a_conflict_and_does_not_advance_the_base()
    {
        var @base = Doc(Cont("BOX-101", Row(quantity: 1)));
        var local = Doc(Cont("BOX-101", Row(quantity: 4)));

        var result = SyncApply.Apply(@base, local, [new UpsertItemOp("BOX-101", Row(quantity: 9))], Now);

        var conflict = Assert.Single(result.Conflicts);
        Assert.Equal("Quantity", conflict.Field);

        // The base stays at the last agreed value, which is what keeps the conflict reproducible
        // and stops either device being declared the winner behind the operator's back.
        Assert.Equal(1, Only(result.Base).Quantity);
    }

    // ---- idempotency: the reconnect-and-resend story ----------------------------------------------

    [Fact]
    public void Replaying_the_same_batch_changes_nothing_the_second_time()
    {
        var @base = Doc(Cont("BOX-101", Row(quantity: 1)));
        var ops = new List<SyncOp> { new UpsertItemOp("BOX-101", Row(quantity: 5)) };

        var first = SyncApply.Apply(@base, @base, ops, Now);
        var second = SyncApply.Apply(first.Base, first.Document, ops, Now);

        Assert.Empty(second.Conflicts);
        Assert.Equal(first.Document.Containers.Single().Items.Single().Quantity,
                     second.Document.Containers.Single().Items.Single().Quantity);
        Assert.Equal(first.Base.Containers.Single().Items.Single().Quantity,
                     second.Base.Containers.Single().Items.Single().Quantity);
    }

    [Fact]
    public void A_replayed_conflict_stays_a_conflict_rather_than_settling_itself()
    {
        // The failure this guards: a resend advancing the base past the disagreement, which would
        // silently pick a winner and drop one of the two edits.
        var @base = Doc(Cont("BOX-101", Row(quantity: 1)));
        var local = Doc(Cont("BOX-101", Row(quantity: 4)));
        var ops = new List<SyncOp> { new UpsertItemOp("BOX-101", Row(quantity: 9)) };

        var first = SyncApply.Apply(@base, local, ops, Now);
        var second = SyncApply.Apply(first.Base, first.Document, ops, Now);

        Assert.Single(first.Conflicts);
        Assert.Single(second.Conflicts);
    }

    // ---- convergence: the property that makes merging a non-issue ---------------------------------

    [Fact]
    public void Both_devices_converge_and_agree_on_the_conflicts()
    {
        // The desktop and the handheld each start from the same base and change different rows, then
        // exchange what they changed. Afterwards both must hold the same document and report the
        // same conflicts - the definition of a sync that has finished.
        var @base = Doc(Cont("BOX-101", Row(uuid: "u1", quantity: 1), Row(uuid: "u2", name: "Tape", barcode: "BBB")));

        var desktop = Doc(Cont("BOX-101", Row(uuid: "u1", quantity: 1), Row(uuid: "u2", name: "Tape 25ft", barcode: "BBB")));
        var handheld = Doc(Cont("BOX-101", Row(uuid: "u1", quantity: 7), Row(uuid: "u2", name: "Tape", barcode: "BBB")));

        var desktopOps = ChangeCursors.Diff(@base, desktop);
        var handheldOps = ChangeCursors.Diff(@base, handheld);

        var desktopAfter = SyncApply.Apply(@base, desktop, handheldOps, Now);
        var handheldAfter = SyncApply.Apply(@base, handheld, desktopOps, Now);

        Assert.Empty(desktopAfter.Conflicts);
        Assert.Empty(handheldAfter.Conflicts);

        var desktopRow1 = desktopAfter.Document.Containers.Single().Items.Single(i => i.Uuid == "u1");
        var handheldRow1 = handheldAfter.Document.Containers.Single().Items.Single(i => i.Uuid == "u1");

        Assert.Equal(7, desktopRow1.Quantity);
        Assert.Equal(7, handheldRow1.Quantity);
        Assert.Equal("Tape 25ft", desktopAfter.Document.Containers.Single().Items.Single(i => i.Uuid == "u2").Name);
        Assert.Equal("Tape 25ft", handheldAfter.Document.Containers.Single().Items.Single(i => i.Uuid == "u2").Name);
    }

    [Fact]
    public void A_conflict_is_reported_by_both_devices_and_the_agreed_value_stays_visible()
    {
        var @base = Doc(Cont("BOX-101", Row(quantity: 1)));
        var desktop = Doc(Cont("BOX-101", Row(quantity: 4)));
        var handheld = Doc(Cont("BOX-101", Row(quantity: 9)));

        var desktopAfter = SyncApply.Apply(@base, desktop, ChangeCursors.Diff(@base, handheld), Now);
        var handheldAfter = SyncApply.Apply(@base, handheld, ChangeCursors.Diff(@base, desktop), Now);

        // Each side keeps showing the value its own operator typed - an edit must not appear to
        // vanish the instant the peer's change arrives - but both know there is a conflict.
        Assert.Equal(4, Only(desktopAfter.Document).Quantity);
        Assert.Equal(9, Only(handheldAfter.Document).Quantity);
        Assert.Single(desktopAfter.Conflicts);
        Assert.Single(handheldAfter.Conflicts);

        // And both bases stayed at the last agreed value, so the next round reports it again rather
        // than settling it by accident.
        Assert.Equal(1, Only(desktopAfter.Base).Quantity);
        Assert.Equal(1, Only(handheldAfter.Base).Quantity);
    }

    [Fact]
    public void Resolving_a_conflict_on_either_device_clears_it_on_both()
    {
        var @base = Doc(Cont("BOX-101", Row(uuid: "u1", quantity: 1)));
        var desktop = Doc(Cont("BOX-101", Row(uuid: "u1", quantity: 4)));
        var handheld = Doc(Cont("BOX-101", Row(uuid: "u1", quantity: 9)));

        var desktopAfter = SyncApply.Apply(@base, desktop, ChangeCursors.Diff(@base, handheld), Now);
        var handheldAfter = SyncApply.Apply(@base, handheld, ChangeCursors.Diff(@base, desktop), Now);
        Assert.Single(desktopAfter.Conflicts);

        // The operator settles it on the handheld, choosing 9. That decision is an op like any other.
        var decision = new ResolveOp("BOX-101", "u1", "Quantity", "9", Now.ToUnixTimeMilliseconds());
        var handheldResolved = SyncApply.Apply(
            handheldAfter.Base, handheldAfter.Document, [decision], Now);

        var desktopResolved = SyncApply.Apply(
            desktopAfter.Base, desktopAfter.Document, [decision], Now);

        Assert.Empty(handheldResolved.Conflicts);
        Assert.Empty(desktopResolved.Conflicts);
        Assert.Equal(9, Only(handheldResolved.Document).Quantity);
        Assert.Equal(9, Only(desktopResolved.Document).Quantity);
        Assert.Equal(9, Only(desktopResolved.Base).Quantity);
    }
}
