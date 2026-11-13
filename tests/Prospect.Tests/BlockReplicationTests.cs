using System.Numerics;
using System.Reflection;
using Prospect.Engine.Net;
using Prospect.Engine.Time;
using Prospect.Game.Client;
using Prospect.Game.Protocol;
using Prospect.Game.Server;
using Prospect.Game.World;
using Xunit;

namespace Prospect.Tests;

public sealed class BlockReplicationTests
{
    private static GameContent LargeContent()
    {
        var content = GameContent.Load();
        // Two-cell gaps keep separate rounded shells inside admission limits; these tests exercise recovery, not shield blending.
        var boxes = new DesignBox[300];
        for (var i = 0; i < boxes.Length; i++) boxes[i] = new DesignBox(new GridSize(i * 3, 0, 0), new GridSize(1, 1, 1), content.Blocks.Get(i == 0 ? "core" : "hull"));
        content.Designs[content.DroneDesign] = HullDesign.Of(new BoxDesign("Network fixture", 1, boxes, content.Blocks.Limits, "network"), content.Rules);
        return content;
    }

    [Fact]
    public void CollisionLossPast63ReplicatesThroughARealSession()
    {
        var content = LargeContent();
        using var link = new LoopbackLink();
        using var server = new ServerHost(new ServerConfig { MaxPlayers = 1 }, content, link.Server);
        using var client = new ClientSession(link.Client, content, "Collision");
        var hulls = (HullStore)typeof(ServerHost).GetField("_hulls", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(server)!;
        var design = content.Designs[content.DroneDesign];
        var origin = new Double3(100000, 0, 0);
        var hull = hulls.Create(0, (byte)content.DroneDesign, design, origin);
        long tick = 0;
        void Step(int count) { for (var i = 0; i < count; i++) { ((ITickHandler)server).Tick(tick); ((ITickHandler)client).Tick(tick++); } }
        Step(10);
        var rammer = hulls.Create(0, (byte)content.PodDesign, content.Pod, origin + design.BlockOffsets[129] + new Vector3(0, 4, 0));
        hulls.State(rammer.Index).Velocity = new Vector3(0, -100, 0);
        var state = hulls.Info(hull.Index).BlockState;
        hulls.BlockStates.SetHitPoints(state, 129, 0.01f);
        Step(60);
        Assert.False(BlockSet.Has(hulls.Blocks(hull.Index), 129));
        var snapshot = client.AcquireSnapshot();
        var found = false;
        for (var i = 0; i < snapshot.HullCount; i++)
        {
            if (snapshot.Hulls[i].Id != hull.Packed) continue;
            found = true;
            Assert.False(BlockSet.Has(snapshot.Hulls[i].Blocks.Span, 129));
            Assert.Equal(hulls.Revision(hull.Index), snapshot.Hulls[i].Revision);
        }
        Assert.True(found);
    }

    private sealed class Wire(ITransport inner) : ITransport, ITransportHandler
    {
        internal bool Refuse, Drop, Duplicate, Gap, RefuseSnapshots;
        private readonly byte[] _rewrite = new byte[65536];
        internal int Bulk, Losses;
        private ITransportHandler? _handler;
        public string Description => inner.Description;
        public int MaxPayloadSize => inner.MaxPayloadSize;
        public bool IsInProcess => inner.IsInProcess;
        public bool Poll(ITransportHandler handler, ReceiveBudget budget) { _handler = handler; return inner.Poll(this, budget); }
        public bool Send(int peer, NetChannel channel, ReadOnlySpan<byte> payload)
        {
            if (payload[0] == (byte)MessageId.BlockState) Bulk++;
            if (payload[0] == (byte)MessageId.Snapshot)
            {
                var r = new NetReader(payload[1..]);
                SnapshotHeader.TryRead(ref r, out var h);
                for (var i = 0; i < h.HullCount && HullRecord.TryRead(ref r, out var record); i++) Losses += record.LossCount;
            }
            if (RefuseSnapshots && payload[0] == (byte)MessageId.Snapshot) return false;
            if (Gap && payload[0] == (byte)MessageId.Snapshot)
            {
                var reader = new NetReader(payload[1..]);
                SnapshotHeader.TryRead(ref reader, out var header);
                var writer = new NetWriter(_rewrite);
                header.Write(ref writer);
                for (var i = 0; i < header.HullCount; i++)
                {
                    HullRecord.TryRead(ref reader, out var record);
                    record.LossStart = record.Revision;
                    record.LossCount = 0;
                    record.Write(ref writer);
                }
                return inner.Send(peer, channel, writer.Written);
            }
            if (channel == NetChannel.Bulk && Refuse) return false;
            if (channel == NetChannel.Unreliable && Drop) return true;
            if (Duplicate && channel == NetChannel.Unreliable) inner.Send(peer, channel, payload);
            return inner.Send(peer, channel, payload);
        }
        public void OnConnected(ITransport t, int peer) => _handler!.OnConnected(this, peer);
        public void OnDisconnected(ITransport t, int peer, DisconnectReason reason) => _handler!.OnDisconnected(this, peer, reason);
        public void OnMessage(ITransport t, int peer, NetChannel channel, ReadOnlySpan<byte> payload) => _handler!.OnMessage(this, peer, channel, payload);
        public void Disconnect(int peer) => inner.Disconnect(peer);
        public int GetRoundTripMs(int peer) => inner.GetRoundTripMs(peer);
        public ulong GetPeerOrigin(int peer) => inner.GetPeerOrigin(peer);
        public void Dispose() { }
    }

    private sealed class Sink : ITransportHandler
    {
        public void OnConnected(ITransport transport, int peer) { }
        public void OnDisconnected(ITransport transport, int peer, DisconnectReason reason) { }
        public void OnMessage(ITransport transport, int peer, NetChannel channel, ReadOnlySpan<byte> payload) { }
    }

    [Fact]
    public void ZeroHullRecoveryCannotUseAnEmptyReplicaEntry()
    {
        var content = GameContent.Load();
        using var replica = new BlockReplica(content);
        Span<byte> bytes = stackalloc byte[64];
        var writer = new NetWriter(bytes);
        writer.WriteUInt32(0); writer.WriteByte(0); writer.WriteUInt32(0);
        writer.WriteInt32(1); writer.WriteInt32(0); writer.WriteUInt16(1);
        writer.WriteUInt64(content.Designs[0].FullSet[0]);
        var reader = new NetReader(writer.Written);
        replica.Receive(ref reader, 0);
        Assert.True(replica.Accept(new HullRecord { Id = 1, Design = 0 }, 1));
        Assert.Empty(replica.Losses(1).ToArray());
    }

    [Fact]
    public void ExistingHullCannotChangeDesignOrLoseBlocksFromAnotherDesign()
    {
        var content = LargeContent();
        using var replica = new BlockReplica(content);
        var original = new HullRecord { Id = 1, Design = 0 };
        Assert.True(replica.Accept(original, 0));
        var changed = new HullRecord { Id = 1, Design = (byte)content.DroneDesign, Revision = 1, LossCount = 1 };
        changed.Losses[0] = 299;
        Assert.False(replica.Accept(changed, 1));
        Assert.Empty(replica.Losses(1).ToArray());
        Assert.True(replica.Accept(original, 2));
    }

    [Fact]
    public void BulkBudgetIncludesHeadersAndRefusalKeepsTheOffset()
    {
        using var link = new LoopbackLink();
        link.Server.Poll(new Sink());
        link.Client.Poll(new Sink());
        var wire = new Wire(link.Server) { Refuse = true };
        var transfer = new BlockTransfer(32768) { Hull = 1, Count = 4096 };
        Span<byte> bytes = stackalloc byte[4096];
        var budget = 1024;
        Assert.False(transfer.Send(wire, 0, bytes, ref budget));
        Assert.Equal(0, transfer.Offset);
        Assert.InRange(budget, 0, 7);
        wire.Refuse = false;
        budget = 1024;
        Assert.True(transfer.Send(wire, 0, bytes, ref budget));
        Assert.Equal((1024 - BlockTransfer.HeaderBytes) / 8, transfer.Offset);
        Assert.InRange(budget, 0, 7);
    }

    [Fact]
    public void BulkChunksAreBoundedValidatedAndExpireWithoutPublishingPartialState()
    {
        var content = LargeContent();
        using var replica = new BlockReplica(content);
        using var link = new LoopbackLink();
        var record = new HullRecord { Id = 1, Design = (byte)content.DroneDesign, Revision = 3, LossStart = 3 };
        Assert.False(replica.Accept(record, 0));
        var words = content.Designs[content.DroneDesign].FullSet.ToArray();
        foreach (var block in new[] { 65, 129, 299 }) BlockSet.Remove(words, block);
        var bytes = new byte[128];
        void Chunk(int offset, int count, long tick, uint revision = 3)
        {
            var writer = new NetWriter(bytes);
            writer.WriteUInt32(1); writer.WriteByte(record.Design); writer.WriteUInt32(revision);
            writer.WriteInt32(words.Length); writer.WriteInt32(offset); writer.WriteUInt16((ushort)count);
            for (var i = 0; i < count; i++) writer.WriteUInt64(words[offset + i]);
            var reader = new NetReader(writer.Written);
            replica.Receive(ref reader, tick);
        }
        Chunk(2, 3, 1);
        Assert.Empty(replica.Losses(1).ToArray());
        Chunk(0, 2, 2);
        replica.SendAcks(link.Client, content.BlockTransfers.TransferTicks + 3);
        Chunk(2, 3, content.BlockTransfers.TransferTicks + 4);
        Assert.False(replica.Accept(record, content.BlockTransfers.TransferTicks + 100));
        Chunk(0, 5, content.BlockTransfers.TransferTicks + 101, 4); // The bit count must match the advertised revision.
        Assert.False(replica.Accept(record, content.BlockTransfers.TransferTicks + 102));
        Chunk(0, 2, content.BlockTransfers.TransferTicks + 103);
        Chunk(2, 3, content.BlockTransfers.TransferTicks + 104);
        Assert.True(replica.Accept(record, content.BlockTransfers.TransferTicks + 105));
        Assert.Equal(new[] { 65, 129, 299 }, replica.Losses(1).ToArray());
    }

    [Fact]
    public void UdpLateJoinReceivesLargeDamagedState()
    {
        var content = LargeContent();
        var port = TestNet.Port(29);
        using var server = new ServerHost(new ServerConfig { MaxPlayers = 1 }, content, RakNetTransport.Listen(port, 4));
        var hulls = (HullStore)typeof(ServerHost).GetField("_hulls", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(server)!;
        var hull = hulls.Create(0, (byte)content.DroneDesign, content.Designs[content.DroneDesign], new Double3(100000, 0, 0));
        for (var i = 100; i < 200; i++) hulls.BlockStates.Lose(hulls.Info(hull.Index).BlockState, i);
        server.Start();
        using var session = GameSession.Join("127.0.0.1", port, content, "Late UDP");
        var snapshot = TestNet.WaitFor(session, snapshot =>
        {
            for (var i = 0; i < snapshot.HullCount; i++)
                if (snapshot.Hulls[i].Id == hull.Packed && snapshot.Hulls[i].Revision == 100) return true;
            return false;
        });
        for (var i = 0; i < snapshot.HullCount; i++)
        {
            if (snapshot.Hulls[i].Id != hull.Packed) continue;
            Assert.False(BlockSet.Has(snapshot.Hulls[i].Blocks.Span, 199));
            Assert.True(BlockSet.Has(snapshot.Hulls[i].Blocks.Span, 299));
            Assert.Equal(200, snapshot.Hulls[i].Stats.Blocks);
        }
        Assert.Null(session.Fault);
    }

    [Fact]
    public void RealSessionRetriesLossesAndRecoveryHoldsPoseUntilStateArrives()
    {
        var content = LargeContent();
        using var link = new LoopbackLink();
        using var lateLink = new LoopbackLink();
        var serverWire = new Wire(link.Server);
        var clientWire = new Wire(link.Client);
        using var server = new ServerHost(new ServerConfig { MaxPlayers = 2 }, content, serverWire, lateLink.Server);
        using var client = new ClientSession(clientWire, content, "Blocks");
        var hulls = (HullStore)typeof(ServerHost).GetField("_hulls", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(server)!;
        var hull = hulls.Create(0, (byte)content.DroneDesign, content.Designs[content.DroneDesign], new Double3(100000, 0, 0));
        long tick = 0;
        void Step(int count) { for (var i = 0; i < count; i++) { ((ITickHandler)server).Tick(tick); ((ITickHandler)client).Tick(tick++); } }
        HullView View()
        {
            var snapshot = client.AcquireSnapshot();
            for (var i = 0; i < snapshot.HullCount; i++) if (snapshot.Hulls[i].Id == hull.Packed) return snapshot.Hulls[i];
            throw new InvalidOperationException("Missing fixture hull");
        }
        Step(10);
        Assert.Equal(0u, View().Revision);
        var state = hulls.Info(hull.Index).BlockState;
        hulls.BlockStates.Lose(state, 129);
        serverWire.RefuseSnapshots = true;
        Step(5);
        Assert.Equal(0u, View().Revision);
        serverWire.RefuseSnapshots = false;
        clientWire.Drop = true;
        serverWire.Duplicate = true;
        Step(12);
        Assert.False(BlockSet.Has(View().Blocks.Span, 129));
        var sent = serverWire.Losses;
        Step(12);
        Assert.True(serverWire.Losses > sent);
        clientWire.Drop = false;
        Step(12);
        sent = serverWire.Losses;
        Step(12);
        Assert.Equal(sent, serverWire.Losses);

        // More than one snapshot's loss payload requires recovery. Refused bulk sends retain the transfer.
        serverWire.Refuse = true;
        serverWire.Gap = true;
        for (var i = 180; i < 220; i++) hulls.BlockStates.Lose(state, i);
        Step(40);
        Assert.Equal(1u, View().Revision);
        Assert.True(serverWire.Bulk > 1);
        serverWire.Refuse = false;
        Step(40);
        Assert.Equal(41u, View().Revision);
        Assert.False(BlockSet.Has(View().Blocks.Span, 219));
        Assert.Equal(259, View().Stats.Blocks);

        Step(100);
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        Step(30);
        Assert.Equal(allocated, GC.GetAllocatedBytesForCurrentThread());

        using var late = new ClientSession(lateLink.Client, content, "Late");
        for (var i = 0; i < 80; i++) { Step(1); ((ITickHandler)late).Tick(tick - 1); }
        var joined = late.AcquireSnapshot();
        var found = false;
        for (var i = 0; i < joined.HullCount; i++)
        {
            if (joined.Hulls[i].Id != hull.Packed) continue;
            Assert.Equal(41u, joined.Hulls[i].Revision);
            Assert.False(BlockSet.Has(joined.Hulls[i].Blocks.Span, 219));
            found = true;
        }
        Assert.True(found);
    }

    [Fact]
    public void GapsDuplicatesReorderedLossesAndMalformedIdsCannotPublishWrongRevision()
    {
        var content = LargeContent();
        using var replica = new BlockReplica(content);
        var record = new HullRecord { Id = 1, Design = (byte)content.DroneDesign, Revision = 1, LossCount = 1 };
        record.Losses[0] = 299;
        Assert.True(replica.Accept(record, 1));
        Assert.True(replica.Accept(record, 2));
        var gap = record;
        gap.Revision = 3; gap.LossStart = 2; gap.Losses[0] = 297;
        Assert.False(replica.Accept(gap, 3));
        var next = record;
        next.Revision = 2; next.LossStart = 1; next.Losses[0] = 298;
        Assert.True(replica.Accept(next, 4));
        Assert.True(replica.Accept(gap, 5));
        Assert.False(replica.Accept(record, 6));
        gap.Revision = 4; gap.LossStart = 3; gap.Losses[0] = 300;
        Assert.False(replica.Accept(gap, 7));
        Assert.Equal(3, replica.Losses(1).Length);
    }
}
