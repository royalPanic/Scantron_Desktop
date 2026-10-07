using Scantron.Core.Merge;
using Scantron.Core.Models;
using Scantron.Core.Sync;
using Scantron.Desktop.Services.Sync;
using Xunit;

namespace Scantron.Desktop.Tests;

/// <summary>
/// Covers the two things the emitter is responsible for: collapsing a burst of edits into one
/// frame, and not sending anything that does not need to go.
/// </summary>
public sealed class ChangeEmitterTests
{
    private static Item Row(string uuid = "u1", string name = "Drill", int quantity = 1) =>
        new() { Uuid = uuid, Name = name, Barcode = "AAA", Quantity = quantity, UpdatedAt = 100 };

    private static Container Cont(string id = "BOX-101", params Item[] items) =>
        new() { Id = id, Items = items, UpdatedAt = 100 };

    private static InventoryDocument Doc(params Container[] containers) =>
        new() { Containers = containers };

    /// <summary>Long enough for the quiet period to have elapsed and the callback to have run.</summary>
    private static Task WaitForQuietPeriodAsync() =>
        Task.Delay(ChangeEmitter.QuietPeriod + TimeSpan.FromMilliseconds(500));

    [Fact]
    public async Task A_burst_of_edits_produces_one_publish()
    {
        // The whole point of the debounce: typing a category is a dozen property changes and must
        // not be a dozen frames.
        var baseDoc = Doc(Cont("BOX-101", Row(quantity: 1)));
        var current = Doc(Cont("BOX-101", Row(quantity: 5)));

        var published = new List<IReadOnlyList<SyncOp>>();
        using var emitter = new ChangeEmitter(
            () => (current, baseDoc, []),
            ops => { published.Add(ops); return Task.CompletedTask; });

        for (var i = 0; i < 25; i++)
        {
            emitter.PublishSoon();
        }

        await WaitForQuietPeriodAsync();

        Assert.Single(published);
        Assert.IsType<UpsertItemOp>(Assert.Single(published[0]));
    }

    [Fact]
    public async Task Nothing_is_published_when_the_document_matches_the_base()
    {
        // This is the echo suppressor. After an inbound change has been applied the base matches,
        // and sending it back is the loop that never ends.
        var document = Doc(Cont("BOX-101", Row(quantity: 4)));

        var published = new List<IReadOnlyList<SyncOp>>();
        using var emitter = new ChangeEmitter(
            () => (document, document, []),
            ops => { published.Add(ops); return Task.CompletedTask; });

        emitter.PublishSoon();
        await WaitForQuietPeriodAsync();

        Assert.Empty(published);
    }

    [Fact]
    public async Task A_row_frozen_by_an_open_conflict_is_not_published()
    {
        // Sending it would either re-report the conflict for ever or settle it in favour of
        // whichever device happened to publish first.
        var baseDoc = Doc(Cont("BOX-101", Row(uuid: "u1", quantity: 1)));
        var current = Doc(Cont("BOX-101", Row(uuid: "u1", quantity: 5)));

        var conflict = new ItemConflict("BOX-101", "u1", "Drill", "Quantity", "1", "5", "9");

        var published = new List<IReadOnlyList<SyncOp>>();
        using var emitter = new ChangeEmitter(
            () => (current, baseDoc, [conflict]),
            ops => { published.Add(ops); return Task.CompletedTask; });

        emitter.PublishSoon();
        await WaitForQuietPeriodAsync();

        Assert.Empty(published);
    }

    [Fact]
    public async Task A_conflicted_row_does_not_block_an_unconflicted_one()
    {
        // Freezing one row must not stall the rest of the document, or a single conflict would stop
        // every subsequent scan from syncing.
        var baseDoc = Doc(Cont("BOX-101", Row(uuid: "u1", quantity: 1), Row(uuid: "u2", name: "Tape")));
        var current = Doc(Cont("BOX-101", Row(uuid: "u1", quantity: 5), Row(uuid: "u2", name: "Tape 25ft")));

        var conflict = new ItemConflict("BOX-101", "u1", "Drill", "Quantity", "1", "5", "9");

        var published = new List<IReadOnlyList<SyncOp>>();
        using var emitter = new ChangeEmitter(
            () => (current, baseDoc, [conflict]),
            ops => { published.Add(ops); return Task.CompletedTask; });

        emitter.PublishSoon();
        await WaitForQuietPeriodAsync();

        var op = Assert.IsType<UpsertItemOp>(Assert.Single(Assert.Single(published)));
        Assert.Equal("u2", op.Item.Uuid);
    }

    [Fact]
    public async Task FlushAsync_publishes_without_waiting_for_the_quiet_period()
    {
        var baseDoc = Doc(Cont("BOX-101", Row(quantity: 1)));
        var current = Doc(Cont("BOX-101", Row(quantity: 5)));

        var published = new List<IReadOnlyList<SyncOp>>();
        using var emitter = new ChangeEmitter(
            () => (current, baseDoc, []),
            ops => { published.Add(ops); return Task.CompletedTask; });

        await emitter.FlushAsync();

        Assert.Single(published);
    }
}
