using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using Prospect.Engine.Net;
using Prospect.Engine.Time;
using Prospect.Game.Client;
using Prospect.Game.Protocol;
using Prospect.Game.Server;
using Prospect.Game.World;
using Xunit;

namespace Prospect.Tests;

public sealed class VisualDamageTests
{
    private static readonly GameContent Content = GameContent.Load();
    private static HullDesign Design(int count = 1, BlockShape shape = BlockShape.Box) => HullDesign.Of(new BoxDesign("Dent fixture", 1,
        Enumerable.Range(0, count).Select(i => new DesignBox(new(i * 3, 0, 0), new(2, 2, 2), new BlockType
        { Id = "dent", Name = "Hull", Core = i == 0, Mass = 10, Hull = 100, Shield = 10000 }, shape)).ToArray(), Content.Blocks.Limits, "dent"), Content.Rules);

    [Fact]
    public void StrongestCapPruningAndAllFacesAreBounded()
    {
        var design = Design(20);
        var state = new VisualDamageState();
        for (var b = 0; b < 20; b++) state.Hit(design, b, design.BlockHitPoints(b) * 0.1f, design.BlockOffsets[b], Vector3.UnitX, 1);
        Assert.Equal(16, state.Count);
        Assert.Equal(16u, state.Version);
        var first = state.Dents[0];
        state.Hit(design, 0, 1, Vector3.Zero, Vector3.UnitY, 2);
        Assert.Equal(first, state.Dents[0]);
        state.Hit(design, 0, design.BlockHitPoints(0) * 0.4f, Vector3.Zero, Vector3.UnitY, 3);
        Assert.True(state.Dents[0].Depth > first.Depth);
        var alive = design.FullSet.ToArray(); BlockSet.Remove(alive, 2); state.Prune(alive);
        state.Hit(design, 19, design.BlockHitPoints(19) * 0.1f, design.BlockOffsets[19], Vector3.UnitX, 4);
        Assert.Equal(16, state.Count); Assert.Equal(19, state.Dents[15].Block);
        for (var shape = 0; shape <= 12; shape++)
        {
            var wedge = Design(shape: (BlockShape)shape);
            for (var side = 0; side < 6; side++)
            {
                var normal = BlockShapes.FaceNormal((BlockShape)shape, Vector3.One, side);
                var p = DentProfile.At(wedge, 0, new Vector3(100, -50, 20), normal, 255);
                Assert.True(p.Valid(wedge));
                var local = p.Point(wedge) - wedge.BlockOffsets[0];
                Assert.True(Vector3.Distance(local, BlockShapes.Closest((BlockShape)shape, Vector3.One, local)) < 0.001f);
            }
        }
    }

    [Theory]
    [InlineData(false, 20, true)]
    [InlineData(true, 20, false)]
    [InlineData(false, 2, false)]
    [InlineData(false, 1000, false)]
    public void BothCollisionSidesGateOnSurvivingPostShieldDamage(bool shield, float speed, bool dent)
    {
        var design = Design();
        using var hulls = new HullStore(2);
        var a = hulls.Create(1, 0, design, new Double3(1.95, 0, 0));
        var b = hulls.Create(2, 0, design, Double3.Zero);
        if (!shield) { hulls.State(a.Index).Shield = hulls.State(b.Index).Shield = 0; hulls.State(a.Index).ShieldOffline = true; hulls.State(b.Index).ShieldOffline = true; }
        hulls.State(a.Index).Velocity = new Vector3(-speed, 0, 0);
        var field = new HullField(hulls, [design]); field.BeginRound(); field.Step(a.Index, default, tick: 17);
        foreach (var index in new[] { a.Index, b.Index })
        {
            var v = hulls.Info(index).Visual;
            Assert.Equal(dent ? 1 : 0, v.Count);
            if (dent) { Assert.Equal(17, v.Spark.Tick); Assert.Equal(1u, v.Spark.Sequence); Assert.True(v.Dents[0].Valid(design)); }
        }
    }

    [Fact]
    public void MetadataDoesNotChangePhysicsAndWarmContactAllocatesNothing()
    {
        var design = Design();
        using var states = new BlockStateStore();
        var body = states.Body(design);
        var visual = new VisualDamageState[1];
        var contacts = new[] { new VisualContact { Block = 0, Point = Vector3.UnitX, Normal = Vector3.UnitX } };
        body.Visual = visual; body.Contacts = contacts;
        var state = HullState.Spawn(default, design.Flight); state.ShieldOffline = true;
        for (var i = 0; i < 50; i++) { states.SetHitPoints(body.State, 0, design.BlockHitPoints(0)); HullSim.Damage(ref state, design.Flight, ref body, design.FullSet, 1, 20); }
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++) { states.SetHitPoints(body.State, 0, design.BlockHitPoints(0)); HullSim.Damage(ref state, design.Flight, ref body, design.FullSet, 1, 20); }
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        var withVisual = state;
        states.SetHitPoints(body.State, 0, design.BlockHitPoints(0)); body.Visual = default;
        HullSim.Damage(ref state, design.Flight, ref body, design.FullSet, 1, 20);
        Assert.Equal(withVisual, state);
        Assert.True(design.NeedsRepair(0, design.Flight.HullPoints, visual[0].Count));
    }

    [Fact]
    public void CollisionPhysicsIsBitIdenticalWithCosmeticsDisabledAndPredictionCannotWrite()
    {
        var design = Design(); var disabled = design.WithRules(Content.Rules with { DentThreshold = float.MaxValue });
        using var a = new HullStore(2); using var b = new HullStore(2);
        var fieldA = new HullField(a, [design]); var fieldB = new HullField(b, [disabled]);
        foreach (var store in new[] { a, b })
        {
            store.Create(1, 0, design, new Double3(1.95, 0, 0)); store.Create(2, 0, design, default);
        }
        void Reset(HullStore store)
        {
            for (var i = 0; i < 2; i++)
            {
                store.State(i) = HullState.Spawn(i == 0 ? new Double3(1.95, 0, 0) : default, design.Flight);
                store.State(i).Shield = 0; store.State(i).ShieldOffline = true;
                store.BlockStates.SetHitPoints(store.Info(i).BlockState, 0, design.BlockHitPoints(0));
            }
            store.State(0).Velocity = new Vector3(-20, 0, 0);
        }
        Reset(a); Reset(b); fieldA.BeginRound(); fieldB.BeginRound(); fieldA.Step(0, default); fieldB.Step(0, default);
        Assert.True(a.Info(0).Visual.Count > 0); Assert.Equal(0, b.Info(0).Visual.Count);
        for (var i = 0; i < 2; i++) { Assert.Equal(a.State(i), b.State(i)); Assert.Equal(a.BlockHp(i).ToArray(), b.BlockHp(i).ToArray()); }
        for (var i = 0; i < 20; i++) { Reset(a); fieldA.BeginRound(); fieldA.Step(0, default, tick: i); }
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 100; i++) { Reset(a); fieldA.BeginRound(); fieldA.Step(0, default, tick: i); }
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        var predicted = new HullField(b, [design], authoritative: false); Reset(b); predicted.BeginRound(); predicted.Step(0, default);
        Assert.Equal(0u, b.Info(0).Visual.Version); Assert.Equal(0u, b.Info(1).Visual.Spark.Sequence);
    }

    [Fact]
    public void BulkIsAtomicAndRejectsGapsMalformedStaleAndWrongIdentity()
    {
        var content = GameContent.Load(); var design = content.Pod;
        using var replica = new BlockReplica(content);
        var record = new HullRecord { Id = 65536, Design = (byte)content.PodDesign, VisualVersion = 2 };
        Assert.True(replica.Accept(record, 0));
        var state = new VisualDamageState(); state.Hit(design, 0, design.BlockHitPoints(0) * 0.2f, design.BlockOffsets[0], Vector3.UnitX, 1);
        var data = new byte[168]; var w = new NetWriter(data); state.Write(ref w);
        void Chunk(int offset, int count, uint version = 1, uint id = 65536, bool malformed = false)
        {
            var buffer = new byte[256]; var writer = new NetWriter(buffer);
            writer.WriteUInt32(id); writer.WriteByte(record.Design); writer.WriteUInt32(version);
            writer.WriteInt32(21); writer.WriteInt32(offset); writer.WriteUInt16((ushort)count);
            writer.WriteBytes(data.AsSpan(offset * 8, count * 8));
            var reader = new NetReader(writer.Written[..(writer.Length - (malformed ? 1 : 0))]);
            replica.Receive(ref reader, 1, true);
        }
        Chunk(10, 11); Assert.Equal(0u, replica.Visual(record.Id).Version);
        Chunk(0, 10); Chunk(10, 11, malformed: true); Assert.Equal(0u, replica.Visual(record.Id).Version);
        Chunk(10, 11); Assert.Equal(1u, replica.Visual(record.Id).Version);
        var owned = replica.Visual(record.Id);
        data[0] = 17; Chunk(0, 21, 2); Assert.Equal(1u, replica.Visual(record.Id).Version);
        data[0] = 1; Chunk(0, 21, 2, 0); Assert.Equal(1u, replica.Visual(record.Id).Version);
        Chunk(0, 21, 2); Chunk(0, 21, 1); Assert.Equal(2u, replica.Visual(record.Id).Version);
        Assert.Equal(1u, owned.Version); Assert.Equal(0u, replica.Visual(record.Id).Spark.Sequence);
        Assert.False(replica.Accept(new HullRecord { Id = record.Id, Design = (byte)content.DroneDesign }, 2));
    }

    private sealed class RefuseVisual(ITransport inner) : ITransport, ITransportHandler
    {
        private ITransportHandler? _handler;
        internal bool Refuse = true;
        internal int Attempts;
        internal uint LastBlockHull;
        internal readonly List<uint> VisualAttempts = [];
        public string Description => inner.Description;
        public int MaxPayloadSize => inner.MaxPayloadSize;
        public bool IsInProcess => inner.IsInProcess;
        public bool Poll(ITransportHandler handler, ReceiveBudget budget) { _handler = handler; return inner.Poll(this, budget); }
        public void OnConnected(ITransport t, int peer) => _handler!.OnConnected(this, peer);
        public void OnDisconnected(ITransport t, int peer, DisconnectReason reason) => _handler!.OnDisconnected(this, peer, reason);
        public void OnMessage(ITransport t, int peer, NetChannel channel, ReadOnlySpan<byte> payload) => _handler!.OnMessage(this, peer, channel, payload);
        public bool Send(int peer, NetChannel channel, ReadOnlySpan<byte> payload)
        {
            if (payload[0] == (byte)MessageId.BlockState)
            {
                var reader = new NetReader(payload[1..]);
                LastBlockHull = reader.ReadUInt32();
            }
            if (payload[0] == (byte)MessageId.VisualState)
            {
                Attempts++;
                var reader = new NetReader(payload[1..]);
                VisualAttempts.Add(reader.ReadUInt32());
                if (Refuse) return false;
            }
            return inner.Send(peer, channel, payload);
        }
        public int GetRoundTripMs(int peer) => inner.GetRoundTripMs(peer);
        public ulong GetPeerOrigin(int peer) => inner.GetPeerOrigin(peer);
        public void Disconnect(int peer) => inner.Disconnect(peer);
        public void Dispose() { }
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void CosmeticRetriesDoNotStarveOtherHullsRecovery(bool activeCosmetic, bool lossRecovery)
    {
        var design = Content.Pod;
        using var hulls = new HullStore(2);
        var a = hulls.Create(1, 0, design, default);
        var b = hulls.Create(2, 0, design, default);
        foreach (var hull in new[] { a, b })
            hulls.Info(hull.Index).Visual.Hit(design, 0, design.BlockHitPoints(0) * 0.2f, design.BlockOffsets[0], Vector3.UnitX, 1);
        var sender = new BlockSender(Content.BlockTransfers);
        using var link = new LoopbackLink();
        var wire = new RefuseVisual(link.Server);
        var buffer = new byte[Content.BlockTransfers.BytesPerTick];
        void Request(bool includeB, long tick)
        {
            Span<byte> bytes = stackalloc byte[27];
            var writer = new NetWriter(bytes);
            writer.WriteByte(includeB ? (byte)2 : (byte)1);
            writer.WriteUInt32(a.Packed); writer.WriteUInt32(0); writer.WriteByte(2); writer.WriteUInt32(0);
            if (includeB)
            {
                writer.WriteUInt32(b.Packed); writer.WriteUInt32(0);
                writer.WriteByte(lossRecovery ? (byte)1 : (byte)2); writer.WriteUInt32(0);
            }
            var reader = new NetReader(writer.Written);
            sender.Receive(ref reader, hulls, tick);
        }
        if (activeCosmetic)
        {
            Request(false, 0);
            sender.Send(wire, 0, buffer, 0);
            Assert.Equal([a.Packed], wire.VisualAttempts);
        }
        Request(true, Content.BlockTransfers.RequestTicks);
        sender.Send(wire, 0, buffer, Content.BlockTransfers.RequestTicks);
        if (lossRecovery)
        {
            Assert.Equal(b.Packed, wire.LastBlockHull);
            Assert.Equal(activeCosmetic ? 1 : 0, wire.Attempts);
        }
        else Assert.Contains(b.Packed, wire.VisualAttempts);
    }

    [Fact]
    public void RealSessionReplicatesBothSidesAndLateJoinSuppressesSparks()
    {
        var content = GameContent.Load(); var design = Design();
        content.Designs[content.DroneDesign] = design;
        using var link = new LoopbackLink(); using var lateLink = new LoopbackLink(); using var observerLink = new LoopbackLink();
        var wire = new RefuseVisual(link.Server);
        using var server = new ServerHost(new ServerConfig { MaxPlayers = 3 }, content, wire, lateLink.Server, observerLink.Server);
        using var client = new ClientSession(link.Client, content, "Dent observer");
        using var observer = new ClientSession(observerLink.Client, content, "Second observer");
        var hulls = (HullStore)typeof(ServerHost).GetField("_hulls", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(server)!;
        var a = hulls.Create(0, (byte)content.DroneDesign, design, new Double3(100001.95, 0, 0));
        var b = hulls.Create(0, (byte)content.DroneDesign, design, new Double3(100000, 0, 0));
        long tick = 0;
        void Step(int count, ClientSession? late = null) { for (var i = 0; i < count; i++) { ((ITickHandler)server).Tick(tick++); ((ITickHandler)client).Tick(0); ((ITickHandler)observer).Tick(0); if (late is not null) ((ITickHandler)late).Tick(0); } }
        Step(20);
        hulls.State(a.Index).Position = new Double3(100001.95, 0, 0); hulls.State(b.Index).Position = new Double3(100000, 0, 0);
        hulls.State(a.Index).Shield = hulls.State(b.Index).Shield = 0;
        hulls.State(a.Index).ShieldOffline = hulls.State(b.Index).ShieldOffline = true;
        hulls.State(a.Index).Velocity = new Vector3(-20, 0, 0);
        Step(6);
        var recent = client.AcquireSnapshot(); var recentView = recent.Hulls.Take(recent.HullCount).Single(v => v.Id == a.Packed);
        Assert.True(recentView.Visual.Spark.Sequence > 0);
        Step(100); Assert.True(wire.Attempts > 0);
        var held = client.AcquireSnapshot(); var heldView = held.Hulls.Take(held.HullCount).Single(v => v.Id == a.Packed);
        Assert.Equal(0u, heldView.Visual.Version);
        Assert.True(heldView.Position != recentView.Position);
        hulls.Info(a.Index).Visual.Hit(design, 0, design.BlockHitPoints(0) * 0.4f, Vector3.UnitX, Vector3.UnitX, tick);
        wire.Refuse = false; Step(160);
        Assert.Equal(0u, heldView.Visual.Version);
        foreach (var h in new[] { a, b })
        {
            Assert.True(hulls.Info(h.Index).Visual.Count > 0);
            var snapshot = client.AcquireSnapshot();
            var view = snapshot.Hulls.Take(snapshot.HullCount).Single(v => v.Id == h.Packed);
            Assert.Equal(hulls.Info(h.Index).Visual.Version, view.Visual.Version);
            Assert.Equal(hulls.Info(h.Index).Visual.Dents[0], view.Visual.Dents[0]);
            var second = observer.AcquireSnapshot(); var secondView = second.Hulls.Take(second.HullCount).Single(v => v.Id == h.Packed);
            Assert.Equal(view.Visual.Version, secondView.Visual.Version); Assert.Equal(view.Visual.Dents[0], secondView.Visual.Dents[0]);
        }
        using var late = new ClientSession(lateLink.Client, content, "Late dent observer"); Step(100, late);
        var lateSnapshot = late.AcquireSnapshot(); var lateHull = lateSnapshot.Hulls.Take(lateSnapshot.HullCount).Single(v => v.Id == a.Packed);
        Assert.Equal(hulls.Info(a.Index).Visual.Version, lateHull.Visual.Version); Assert.Equal(0u, lateHull.Visual.Spark.Sequence);
        hulls.Destroy(a); var fresh = hulls.Create(0, (byte)content.DroneDesign, design, new Double3(100100, 0, 0));
        Assert.NotEqual(a.Packed, fresh.Packed); Assert.Equal(0, hulls.Info(fresh.Index).Visual.Count); Step(60, late);
        var snapshotFresh = client.AcquireSnapshot(); Assert.Equal(0u, snapshotFresh.Hulls.Take(snapshotFresh.HullCount).Single(v => v.Id == fresh.Packed).Visual.Version);
        Assert.Null(server.Fault);
    }

    [Fact]
    public void RockContactReplicatesWithoutDeformingAsteroid()
    {
        var content = GameContent.Load(); var design = Design(); content.Designs[content.DroneDesign] = design;
        using var link = new LoopbackLink();
        using var server = new ServerHost(new ServerConfig { MaxPlayers = 1 }, content, link.Server);
        using var client = new ClientSession(link.Client, content, "Rock observer");
        var hulls = (HullStore)typeof(ServerHost).GetField("_hulls", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(server)!;
        long tick = 0;
        void Step(int count) { for (var i = 0; i < count; i++) { ((ITickHandler)server).Tick(tick++); ((ITickHandler)client).Tick(0); } }
        Step(20);
        var galaxy = client.AcquireSnapshot().Galaxy!;
        var rocks = new RockField(galaxy); var sector = rocks.Sector(default);
        var rock = sector.Asteroids.Take(sector.AsteroidCount).First(a => a.Radius >= 80);
        var outward = Vector3.UnitX;
        var radius = rock.Radius * AsteroidShape.RadiusAlong(rock.Shape, Vector3.Transform(outward, Quaternion.Conjugate(rock.Rotation)));
        var position = galaxy.CenterOf(default) + rock.LocalPosition + outward * (radius + 0.9f);
        var h = hulls.Create(0, (byte)content.DroneDesign, design, position);
        hulls.State(h.Index).Shield = 0; hulls.State(h.Index).ShieldOffline = true; hulls.State(h.Index).Velocity = -outward * 20;
        Step(90);
        Assert.True(hulls.Info(h.Index).Visual.Count > 0);
        var snapshot = client.AcquireSnapshot(); var view = snapshot.Hulls.Take(snapshot.HullCount).Single(v => v.Id == h.Packed);
        Assert.Equal(hulls.Info(h.Index).Visual.Dents[0], view.Visual.Dents[0]);
        Assert.Equal(hulls.Info(h.Index).Visual.Version, view.Visual.Version);
        Assert.Equal(rock, sector.Asteroids.Take(sector.AsteroidCount).First(a => a.Radius >= 80));
    }

    [Fact]
    public void InvalidProfilesAndNonfiniteContactsNeverPublish()
    {
        var design = Design(); var state = new VisualDamageState();
        state.Hit(design, 0, float.NaN, Vector3.Zero, Vector3.UnitX, 0);
        state.Hit(design, 0, 100, new Vector3(float.PositiveInfinity), Vector3.UnitX, 0);
        Assert.Equal(0u, state.Version); Assert.Equal(0u, state.Spark.Sequence);
        var valid = DentProfile.At(design, 0, Vector3.UnitX, Vector3.UnitX, 1);
        var bytes = new byte[161];
        for (var variation = 0; variation < 4; variation++)
        {
            var malformed = valid;
            if (variation == 0) malformed.Block = design.BlockCount;
            if (variation == 1) malformed.Face = 6;
            if (variation == 2) malformed.Depth = 0;
            var writer = new NetWriter(bytes); writer.WriteByte(variation == 3 ? (byte)2 : (byte)1);
            malformed.Write(ref writer); if (variation == 3) malformed.Write(ref writer);
            var reader = new NetReader(writer.Written);
            Assert.False(VisualDamageState.Read(ref reader, design, 2, out _));
        }
    }

    [Fact]
    public void TuningIsHashedAndStateSizeIsFixed()
    {
        using var copy = new ContentCopy(); copy.Replace("blocks/blocks.json", "\"dentThreshold\": 0.02", "\"dentThreshold\": 0.03");
        Assert.NotEqual(Content.Identity, copy.Load().Identity);
        Assert.Equal(12, Marshal.SizeOf<DentProfile>());
        Assert.InRange(Marshal.SizeOf<VisualDamageState>(), 220, 240);
    }
}
