using Scantron.Core.Models;
using Scantron.Core.Sync;
using Scantron.Desktop.Services.Sync;
using Xunit;

namespace Scantron.Desktop.Tests;

/// <summary>
/// Drives the live-sync protocol state machine without a socket. Every branch that matters - a wrong
/// pairing code, a version mismatch, a first sync versus an established one, a replay - is exercised
/// directly, which is why the session keeps its decisions free of the network.
/// </summary>
public sealed class SyncSessionTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeMilliseconds(1_760_000_000_000);

    private static Item Row(string uuid = "u1", string name = "Drill", int quantity = 1) =>
        new() { Uuid = uuid, Name = name, Barcode = "AAA", Quantity = quantity, UpdatedAt = 100 };

    private static InventoryDocument Doc(params Container[] containers) =>
        new() { Containers = containers, Version = InventoryFormat.CurrentVersion };

    private static Container Cont(string id = "BOX-101", params Item[] items) =>
        new() { Id = id, Items = items, UpdatedAt = 100 };

    private static PairedPeerStore InTempDirectory(out string directory)
    {
        directory = Path.Combine(Path.GetTempPath(), "scantron-sync-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return new PairedPeerStore(directory);
    }

    private static SyncMessage Hello(string deviceId = "ck65-1", int version = SyncMessage.ProtocolVersion) =>
        new() { Type = SyncMessageType.Hello, Src = deviceId, Hello = new HelloInfo(deviceId, "CK65-04", version) };

    private static string Frame(SyncMessage message) => SyncWire.Write(message);

    [Fact]
    public void A_version_mismatch_is_refused_with_a_sentence()
    {
        var store = InTempDirectory(out _);
        var session = new SyncSession(store, "DESK-01");

        var replies = session.Receive(Frame(Hello(version: 99)), null, null, Now);

        var bye = Assert.Single(replies);
        Assert.Equal(SyncMessageType.Bye, bye.Type);
        Assert.Contains("version", bye.Reason);
    }

    [Fact]
    public void A_correct_pairing_code_admits_the_device()
    {
        var store = InTempDirectory(out _);
        var session = new SyncSession(store, "DESK-01");

        Assert.Empty(session.Receive(Frame(Hello()), null, null, Now));

        var replies = session.Receive(
            Frame(new SyncMessage { Type = SyncMessageType.Pair, Src = "ck65-1", Pair = new PairInfo(store.Code) }),
            null,
            null,
            Now);

        var paired = Assert.Single(replies);
        Assert.Equal(SyncMessageType.Paired, paired.Type);
        Assert.True(session.IsPaired);
        Assert.Equal("CK65-04", store.Peer.Name);
    }

    [Fact]
    public void A_wrong_pairing_code_is_refused_and_nothing_is_paired()
    {
        var store = InTempDirectory(out _);
        var session = new SyncSession(store, "DESK-01");
        session.Receive(Frame(Hello()), null, null, Now);

        var replies = session.Receive(
            Frame(new SyncMessage { Type = SyncMessageType.Pair, Src = "ck65-1", Pair = new PairInfo("000000") }),
            null,
            null,
            Now);

        var bye = Assert.Single(replies);
        Assert.Equal(SyncMessageType.Bye, bye.Type);
        Assert.Contains("pairing code", bye.Reason);
        Assert.False(store.Peer.IsPaired);
    }

    [Fact]
    public void The_pairing_code_is_one_shot()
    {
        // A code that survived its own use would be a standing key to the stock count.
        var store = InTempDirectory(out _);
        var first = store.Code;

        store.Pair("ck65-1", "CK65-04", null);

        Assert.NotEqual(first, store.Code);
    }

    [Fact]
    public void A_different_handheld_is_refused_while_one_is_already_paired()
    {
        // Refusing rather than swapping: silently replacing the peer would leave the previous unit
        // believing it was still in sync.
        var store = InTempDirectory(out _);
        store.Pair("ck65-1", "CK65-04", null);
        var session = new SyncSession(store, "DESK-01");

        session.Receive(Frame(Hello("ck65-2")), null, null, Now);
        var replies = session.Receive(
            Frame(new SyncMessage { Type = SyncMessageType.Pair, Src = "ck65-2", Pair = new PairInfo(store.Code) }),
            null,
            null,
            Now);

        var bye = Assert.Single(replies);
        Assert.Contains("already paired", bye.Reason);
    }

    [Fact]
    public void An_already_paired_handheld_skips_the_code()
    {
        var store = InTempDirectory(out _);
        store.Pair("ck65-1", "CK65-04", null);
        var session = new SyncSession(store, "DESK-01");

        var replies = session.Receive(Frame(Hello()), null, null, Now);

        var paired = Assert.Single(replies);
        Assert.Equal(SyncMessageType.Paired, paired.Type);
    }

    [Fact]
    public void Changes_before_pairing_are_refused()
    {
        var store = InTempDirectory(out _);
        var session = new SyncSession(store, "DESK-01");
        session.Receive(Frame(Hello()), null, null, Now);

        var replies = session.Receive(
            Frame(new SyncMessage { Type = SyncMessageType.Changes, Seq = 1, Ops = [new UpsertItemOp("BOX-101", Row())] }),
            Doc(Cont()),
            Doc(Cont()),
            Now);

        var bye = Assert.Single(replies);
        Assert.Equal(SyncMessageType.Bye, bye.Type);
        Assert.Contains("before pairing", bye.Reason);
    }

    [Fact]
    public void An_applied_batch_is_acknowledged_and_raised()
    {
        var store = InTempDirectory(out _);
        var session = new SyncSession(store, "DESK-01");
        session.Receive(Frame(Hello()), null, null, Now);
        session.Receive(
            Frame(new SyncMessage { Type = SyncMessageType.Pair, Src = "ck65-1", Pair = new PairInfo(store.Code) }),
            null, null, Now);

        SyncApplyResult? applied = null;
        session.Applied += (_, result) => applied = result;

        var baseDoc = Doc(Cont("BOX-101", Row(quantity: 1)));
        var replies = session.Receive(
            Frame(new SyncMessage
            {
                Type = SyncMessageType.Changes,
                Seq = 3,
                Ops = [new UpsertItemOp("BOX-101", Row(quantity: 5))],
            }),
            baseDoc,
            baseDoc,
            Now);

        var ack = Assert.Single(replies);
        Assert.Equal(SyncMessageType.Ack, ack.Type);
        Assert.Equal(3, ack.AckSeq);

        Assert.NotNull(applied);
        Assert.Equal(5, applied.Document.Containers.Single().Items.Single().Quantity);
        Assert.Equal(5, applied.Base.Containers.Single().Items.Single().Quantity);
    }

    [Fact]
    public void A_conflict_is_reported_to_the_peer_and_does_not_advance_the_base()
    {
        var store = InTempDirectory(out _);
        var session = new SyncSession(store, "DESK-01");
        session.Receive(Frame(Hello()), null, null, Now);
        session.Receive(
            Frame(new SyncMessage { Type = SyncMessageType.Pair, Src = "ck65-1", Pair = new PairInfo(store.Code) }),
            null, null, Now);

        var baseDoc = Doc(Cont("BOX-101", Row(quantity: 1)));
        var local = Doc(Cont("BOX-101", Row(quantity: 4)));

        var replies = session.Receive(
            Frame(new SyncMessage
            {
                Type = SyncMessageType.Changes,
                Seq = 1,
                Ops = [new UpsertItemOp("BOX-101", Row(quantity: 9))],
            }),
            local,
            baseDoc,
            Now);

        Assert.Equal(2, replies.Count);
        Assert.Equal(SyncMessageType.Ack, replies[0].Type);

        var conflict = replies[1];
        Assert.Equal(SyncMessageType.Conflict, conflict.Type);
        Assert.Equal("Quantity", conflict.Conflict!.Field);
        Assert.Equal("1", conflict.Conflict.BaseValue);
    }

    [Fact]
    public void A_replayed_batch_is_acknowledged_without_applying_again()
    {
        var store = InTempDirectory(out _);
        var session = new SyncSession(store, "DESK-01");
        session.Receive(Frame(Hello()), null, null, Now);
        session.Receive(
            Frame(new SyncMessage { Type = SyncMessageType.Pair, Src = "ck65-1", Pair = new PairInfo(store.Code) }),
            null, null, Now);

        var baseDoc = Doc(Cont("BOX-101", Row(quantity: 1)));
        var batch = new SyncMessage
        {
            Type = SyncMessageType.Changes,
            Seq = 7,
            Ops = [new UpsertItemOp("BOX-101", Row(quantity: 5))],
        };

        var applies = 0;
        session.Applied += (_, _) => applies++;

        session.Receive(Frame(batch), baseDoc, baseDoc, Now);
        var second = session.Receive(Frame(batch), baseDoc, baseDoc, Now);

        // Applied once, acknowledged twice - a reconnect must not double-apply a conflicted field.
        Assert.Equal(1, applies);
        Assert.Equal(SyncMessageType.Ack, Assert.Single(second).Type);
    }

    [Fact]
    public void An_unknown_message_type_is_answered_with_a_reason()
    {
        var store = InTempDirectory(out _);
        var session = new SyncSession(store, "DESK-01");

        var replies = session.Receive("""{"type":"teleport"}""", null, null, Now);

        var bye = Assert.Single(replies);
        Assert.Equal(SyncMessageType.Bye, bye.Type);
        Assert.Contains("Scantron version", bye.Reason);
    }

    [Fact]
    public void Catch_up_sends_a_snapshot_when_there_is_no_shared_base()
    {
        var store = InTempDirectory(out _);
        var session = new SyncSession(store, "DESK-01");

        var message = session.CatchUp(Doc(Cont("BOX-101", Row())), null, 1);

        Assert.Equal(SyncMessageType.Changes, message.Type);
        Assert.IsType<SnapshotOp>(Assert.Single(message.Ops));
    }

    [Fact]
    public void Catch_up_sends_nothing_at_all_when_no_document_is_loaded()
    {
        // The dangerous case: an empty snapshot would replace the handheld's whole database. Null is
        // "nothing loaded", which is not the same answer as an empty document, and the difference is
        // destructive.
        var store = InTempDirectory(out _);
        var session = new SyncSession(store, "DESK-01");

        var message = session.CatchUp(null, null, 1);

        Assert.Empty(message.Ops);
    }

    [Fact]
    public void Catch_up_sends_only_what_differs_from_the_base()
    {
        var store = InTempDirectory(out _);
        var session = new SyncSession(store, "DESK-01");

        var baseDoc = Doc(Cont("BOX-101", Row(quantity: 1), Row(uuid: "u2", name: "Tape")));
        var local = Doc(Cont("BOX-101", Row(quantity: 4), Row(uuid: "u2", name: "Tape")));

        var message = session.CatchUp(local, baseDoc, 1);

        var op = Assert.IsType<UpsertItemOp>(Assert.Single(message.Ops));
        Assert.Equal("u1", op.Item.Uuid);
    }
}
