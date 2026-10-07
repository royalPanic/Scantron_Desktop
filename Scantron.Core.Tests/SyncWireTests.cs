using Scantron.Core.Models;
using Scantron.Core.Sync;
using Xunit;

namespace Scantron.Core.Tests;

/// <summary>
/// Pins the live-sync wire shape. The Kotlin client is written against these same field names, so a
/// failure here means the two halves have drifted - which is the failure this test exists to catch
/// before it reaches a warehouse.
/// </summary>
public sealed class SyncWireTests
{
    private static Item Row(string uuid = "u1", string name = "Drill", int quantity = 1) =>
        new() { Uuid = uuid, Name = name, Barcode = "AAA", Quantity = quantity, UpdatedAt = 123 };

    [Fact]
    public void A_hello_round_trips()
    {
        var message = new SyncMessage
        {
            Type = SyncMessageType.Hello,
            Src = "ck65-1",
            Hello = new HelloInfo("ck65-1", "CK65-04", SyncMessage.ProtocolVersion),
        };

        Assert.True(SyncWire.TryRead(SyncWire.Write(message), out var read, out var error), error);
        Assert.Equal(SyncMessageType.Hello, read.Type);
        Assert.Equal("CK65-04", read.Hello!.Name);
        Assert.Equal(SyncMessage.ProtocolVersion, read.Hello.ProtocolVersion);
    }

    [Fact]
    public void An_item_upsert_round_trips_with_every_field()
    {
        var message = new SyncMessage
        {
            Type = SyncMessageType.Changes,
            Seq = 7,
            Src = "ck65-1",
            Ops = [new UpsertItemOp("BOX-101", Row(quantity: 12))],
        };

        Assert.True(SyncWire.TryRead(SyncWire.Write(message), out var read, out var error), error);

        var op = Assert.IsType<UpsertItemOp>(Assert.Single(read.Ops));
        Assert.Equal("BOX-101", op.ContainerId);
        Assert.Equal(12, op.Item.Quantity);
        Assert.Equal("AAA", op.Item.Barcode);
        Assert.Equal(123, op.Item.UpdatedAt);
        Assert.Equal(7, read.Seq);
    }

    [Fact]
    public void A_delete_round_trips_its_merge_key()
    {
        var message = new SyncMessage
        {
            Type = SyncMessageType.Changes,
            Ops = [new DeleteItemOp("BOX-101", "", Scantron.Core.Identity.ItemKeyKind.ContainerBarcode, "BOX-101\0BBB", "Tape", 99)],
        };

        Assert.True(SyncWire.TryRead(SyncWire.Write(message), out var read, out var error), error);

        var op = Assert.IsType<DeleteItemOp>(Assert.Single(read.Ops));
        Assert.Equal(Scantron.Core.Identity.ItemKeyKind.ContainerBarcode, op.KeyKind);
        Assert.Equal("BOX-101\0BBB", op.KeyValue);
        Assert.Equal(99, op.At);
    }

    [Fact]
    public void A_snapshot_carries_a_document_that_reads_back_as_an_export()
    {
        var document = new InventoryDocument
        {
            Containers = [new Container { Id = "BOX-101", Name = "Hardware", Items = [Row()] }],
        };

        var message = new SyncMessage { Type = SyncMessageType.Snapshot, Ops = [new SnapshotOp(document)] };

        Assert.True(SyncWire.TryRead(SyncWire.Write(message), out var read, out var error), error);

        var snapshot = Assert.IsType<SnapshotOp>(Assert.Single(read.Ops));
        Assert.Equal("Hardware", snapshot.Document.Containers.Single().Name);
        Assert.Equal(InventoryFormat.CurrentVersion, snapshot.Document.Version);
    }

    [Fact]
    public void A_resolve_round_trips_null_and_a_value()
    {
        foreach (var value in new[] { "9", null })
        {
            var message = new SyncMessage
            {
                Type = SyncMessageType.Resolve,
                Ops = [new ResolveOp("BOX-101", "u1", "Quantity", value, 1)],
            };

            Assert.True(SyncWire.TryRead(SyncWire.Write(message), out var read, out var error), error);

            var op = Assert.IsType<ResolveOp>(Assert.Single(read.Ops));
            Assert.Equal(value, op.Value);
        }
    }

    [Fact]
    public void A_conflict_frame_round_trips()
    {
        var message = new SyncMessage
        {
            Type = SyncMessageType.Conflict,
            Conflict = new ConflictInfo("BOX-101/u1/Quantity", "BOX-101", "u1", "Drill", "Quantity", "1", "4", "9"),
        };

        Assert.True(SyncWire.TryRead(SyncWire.Write(message), out var read, out var error), error);

        Assert.Equal("4", read.Conflict!.LocalValue);
        Assert.Equal("9", read.Conflict.RemoteValue);
    }

    [Fact]
    public void An_ack_round_trips()
    {
        var message = new SyncMessage { Type = SyncMessageType.Ack, Src = "desktop", AckSeq = 42 };

        Assert.True(SyncWire.TryRead(SyncWire.Write(message), out var read, out var error), error);

        Assert.Equal(42, read.AckSeq);
    }

    [Fact]
    public void An_unknown_message_type_is_refused_with_a_sentence()
    {
        var refused = SyncWire.TryRead("""{"type":"teleport","seq":1}""", out _, out var error);

        Assert.False(refused);
        Assert.NotNull(error);
        Assert.Contains("Scantron version", error);
    }

    [Fact]
    public void A_malformed_frame_is_refused_rather_than_thrown()
    {
        Assert.False(SyncWire.TryRead("{ not json", out _, out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void An_unreadable_op_poisons_the_whole_batch()
    {
        // Applying the readable half would leave the peer's view partially reconstructed, which is
        // worse than refusing the frame outright and asking for a fresh snapshot.
        var json = """
                   {"type":"changes","seq":1,"ops":[{"op":"teleport"},{"op":"upsertContainer","container":{"id":"BOX-1"}}]}
                   """;

        Assert.False(SyncWire.TryRead(json, out _, out var error));
        Assert.Contains("Scantron version", error);
    }

    [Fact]
    public void A_row_without_a_name_is_refused()
    {
        var json = """
                   {"type":"changes","seq":1,"ops":[{"op":"upsertItem","containerId":"BOX-1","item":{"uuid":"u1","name":""}}]}
                   """;

        Assert.False(SyncWire.TryRead(json, out _, out var error));
        Assert.Contains("name", error);
    }
}
