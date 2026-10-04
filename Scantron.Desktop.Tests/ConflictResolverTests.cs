using Scantron.Core.Models;
using Scantron.Core.Merge;
using Scantron.Core.Serialization;
using Scantron.Desktop.Services;
using Xunit;

namespace Scantron.Desktop.Tests;

/// <summary>
/// Asserts that an operator's decision actually reaches the exported document.
/// </summary>
/// <remarks>
/// The merger deliberately resolves nothing, so this resolver is the only thing standing between
/// a reported conflict and a silently wrong quantity. Every case here is a way that mapping can
/// go wrong: the wrong row, the wrong field, or no row at all.
/// </remarks>
public sealed class ConflictResolverTests
{
    private static Item Item(string uuid = "u1", string name = "Drill", int quantity = 1) =>
        new() { Uuid = uuid, Name = name, Quantity = quantity };

    private static Container Cont(params Item[] items) =>
        new() { Id = "BOX-101", Items = items };

    private static InventoryDocument Doc(params Container[] containers) =>
        new() { Containers = containers };

    private static ItemConflict Conflict(
        string field,
        string? local,
        string? remote,
        string uuid = "u1",
        string name = "Drill") =>
        new("BOX-101", uuid, name, field, "base", local, remote);

    private static Item MergedItem(InventoryDocument document, string uuid = "u1") =>
        document.Containers[0].Items.Single(i => i.Uuid == uuid);

    /// <summary>Applies a decision and returns the rewritten document.</summary>
    private static InventoryDocument TakeRemote(
        InventoryDocument document,
        ItemConflict conflict,
        out ResolutionOutcome outcome)
    {
        outcome = ConflictResolver.Apply(document, conflict, ConflictResolution.TakeRemote);
        Assert.True(outcome.Succeeded, outcome.Message);
        return outcome.Document!;
    }

    [Fact]
    public void Taking_the_remote_quantity_rewrites_the_merged_row()
    {
        var document = Doc(Cont(Item(quantity: 3)));

        var updated = TakeRemote(document, Conflict("Quantity", "3", "9"), out _);

        Assert.Equal(9, MergedItem(updated).Quantity);
    }

    [Fact]
    public void Taking_the_remote_name_rewrites_only_the_name()
    {
        var document = Doc(Cont(Item(name: "Drill", quantity: 4)));

        var updated = TakeRemote(document, Conflict("Name", "Drill", "Hammer"), out _);
        var item = MergedItem(updated);

        Assert.Equal("Hammer", item.Name);

        // The rest of the row must survive: taking one side of a conflict is not taking the
        // whole handheld version of the row.
        Assert.Equal(4, item.Quantity);
    }

    [Fact]
    public void Keeping_local_leaves_the_merged_value_untouched()
    {
        var document = Doc(Cont(Item(quantity: 3)));

        var outcome = ConflictResolver.Apply(
            document, Conflict("Quantity", "3", "9"), ConflictResolution.KeepLocal);

        Assert.True(outcome.Succeeded, outcome.Message);
        Assert.Equal(3, MergedItem(outcome.Document!).Quantity);
    }

    [Fact]
    public void Applying_a_conflict_does_not_mutate_the_input_document()
    {
        var document = Doc(Cont(Item(quantity: 3)));

        _ = TakeRemote(document, Conflict("Quantity", "3", "9"), out _);

        // The view model holds the merged document and re-applies each decision to it in turn.
        // If Apply mutated in place, later decisions would be resolved against an already
        // rewritten document and the conflict list would stop describing the file.
        Assert.Equal(3, document.Containers[0].Items[0].Quantity);
    }

    [Fact]
    public void A_container_field_conflict_is_resolved_on_the_container()
    {
        var document = Doc(new Container { Id = "BOX-101", Name = "Old", Notes = "keep me", Items = [Item()] });

        var updated = TakeRemote(
            document,
            new ItemConflict("BOX-101", "", "Name", "Name", "base", "Old", "New"),
            out _);

        Assert.Equal("New", updated.Containers[0].Name);
        Assert.Equal("keep me", updated.Containers[0].Notes);
    }

    [Fact]
    public void A_location_conflict_is_only_ever_matched_against_the_container()
    {
        var document = Doc(new Container { Id = "BOX-101", Location = "Shelf 2-A", Items = [Item()] });

        var updated = TakeRemote(
            document,
            new ItemConflict("BOX-101", "", "Location", "Location", "base", "Shelf 2-A", "Bay 9"),
            out _);

        Assert.Equal("Bay 9", updated.Containers[0].Location);
    }

    [Fact]
    public void A_legacy_name_only_conflict_narrows_to_the_matching_row()
    {
        // A version 1.0 row has no uuid, so the merger reports the uuid slot empty and puts the
        // field name in the name slot. The row can only be found by matching the local value.
        var document = Doc(Cont(Item("u-drill", "Drill"), Item("u-nails", "Nails", 500)));

        var updated = TakeRemote(
            document,
            Conflict("Quantity", "500", "2", uuid: "", name: "Quantity"),
            out _);

        Assert.Equal(2, MergedItem(updated, "u-nails").Quantity);

        // The other row must not be touched, even though both carry a quantity.
        Assert.Equal(1, MergedItem(updated, "u-drill").Quantity);
    }

    [Fact]
    public void An_ambiguous_legacy_conflict_is_refused_rather_than_guessed()
    {
        // Two rows both report the same local value, so the conflict cannot be attributed to
        // one. Writing to either would silently take the wrong row's remote value.
        var document = Doc(Cont(Item("u1", "Nails", 5), Item("u2", "Screws", 5)));

        var outcome = ConflictResolver.Apply(
            document, Conflict("Quantity", "5", "9", uuid: "", name: "Nails"), ConflictResolution.TakeRemote);

        Assert.False(outcome.Succeeded);
        Assert.Contains("ambiguous", outcome.Message!, StringComparison.OrdinalIgnoreCase);

        // Nothing was rewritten.
        Assert.All(document.Containers[0].Items, i => Assert.Equal(5, i.Quantity));
    }

    [Fact]
    public void A_presence_conflict_is_accepted_without_a_rewrite()
    {
        var document = Doc(Cont(Item()));

        var outcome = ConflictResolver.Apply(
            document,
            new ItemConflict("BOX-101", "u1", "Drill", "Presence", "present", "absent", "absent"),
            ConflictResolution.TakeRemote);

        // Both sides agree the row is gone, so there is no value to choose between. The row is
        // carried forward for the operator to deal with, and accepting the conflict must not
        // fail the whole merge over it.
        Assert.True(outcome.Succeeded, outcome.Message);
        Assert.Single(outcome.Document!.Containers[0].Items);
    }

    [Fact]
    public void A_stale_conflict_for_a_deleted_row_does_not_block_the_merge()
    {
        var document = Doc(Cont(Item("u-other")));

        var outcome = ConflictResolver.Apply(
            document, Conflict("Quantity", "3", "9", uuid: "u-gone"), ConflictResolution.TakeRemote);

        Assert.True(outcome.Succeeded, outcome.Message);
    }

    [Fact]
    public void A_conflict_for_a_missing_container_is_reported()
    {
        var document = Doc(Cont(Item()));

        var outcome = ConflictResolver.Apply(
            document,
            new ItemConflict("BOX-999", "u1", "Drill", "Quantity", "1", "3", "9"),
            ConflictResolution.TakeRemote);

        Assert.False(outcome.Succeeded);
        Assert.Contains("BOX-999", outcome.Message!, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unknown_field_is_refused()
    {
        var document = Doc(Cont(Item()));

        var outcome = ConflictResolver.Apply(
            document, Conflict("Colour", "red", "blue"), ConflictResolution.TakeRemote);

        Assert.False(outcome.Succeeded);
        Assert.Contains("Colour", outcome.Message!, StringComparison.Ordinal);
    }

    [Fact]
    public void A_non_numeric_remote_quantity_is_reported_rather_than_written()
    {
        var document = Doc(Cont(Item()));

        var outcome = ConflictResolver.Apply(
            document, Conflict("Quantity", "3", "unknown"), ConflictResolution.TakeRemote);

        Assert.False(outcome.Succeeded);
        Assert.Equal(1, document.Containers[0].Items[0].Quantity);
    }

    [Fact]
    public void Every_resolution_produces_a_document_the_device_will_import()
    {
        // The end-to-end guarantee: after applying decisions, the result still round-trips
        // through the wire reader and passes the device's own import rules.
        var document = Doc(new Container { Id = "BOX-101", Name = "Shelf stock", Items = [Item(quantity: 3)] });

        var updated = TakeRemote(document, Conflict("Quantity", "3", "9"), out _);

        var round = InventoryReader.Read(InventoryReader.Write(updated));
        Assert.True(InventoryValidator.Validate(round).IsValid);
        Assert.Equal(9, round.Containers[0].Items[0].Quantity);
    }
}
