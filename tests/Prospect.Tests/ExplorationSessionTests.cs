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

public sealed class ExplorationSessionTests
{
    private static readonly GameContent Content = GameContent.Load();

    private sealed class Wire(ITransport inner) : ITransport, ITransportHandler
    {
        internal uint ForbiddenOwner;
        internal Action<uint>? Welcomed;
        internal int LeakedHulls, RockMessages, Recoveries, ExplorationBytes, RockBytes, TargetLeaks, NavigationBytes;
        internal bool RefuseNavigation;
        public string Description => inner.Description;
        public int MaxPayloadSize => inner.MaxPayloadSize;
        public bool IsInProcess => inner.IsInProcess;
        private ITransportHandler? _handler;
        public bool Poll(ITransportHandler handler, ReceiveBudget budget) { _handler = handler; return inner.Poll(this, budget); }
        public void OnConnected(ITransport transport, int peer) => _handler!.OnConnected(this, peer);
        public void OnDisconnected(ITransport transport, int peer, DisconnectReason reason) => _handler!.OnDisconnected(this, peer, reason);
        public void OnMessage(ITransport transport, int peer, NetChannel channel, ReadOnlySpan<byte> payload) => _handler!.OnMessage(this, peer, channel, payload);
        public bool Send(int peer, NetChannel channel, ReadOnlySpan<byte> payload)
        {
            var reader = new NetReader(payload[1..]);
            if (payload[0] == (byte)MessageId.Welcome && WelcomeMessage.TryRead(ref reader, Content.SettingCatalog, out var welcome)) Welcomed?.Invoke(welcome.PlayerId);
            reader = new NetReader(payload[1..]);
            if (payload[0] == (byte)MessageId.Snapshot && SnapshotHeader.TryRead(ref reader, out var header))
                for (var i = 0; i < header.HullCount; i++)
                    if (HullRecord.TryRead(ref reader, out var record) && record.Owner == ForbiddenOwner)
                    { LeakedHulls++; if (record.Order.Target != default) TargetLeaks++; }
            if (payload[0] == (byte)MessageId.RockDeltas) { RockMessages++; RockBytes += payload.Length; }
            if (payload[0] == (byte)MessageId.Exploration) ExplorationBytes += payload.Length;
            if (payload[0] == (byte)MessageId.Navigation)
            {
                NavigationBytes += payload.Length;
                if (RefuseNavigation) return false;
            }
            if (payload[0] is (byte)MessageId.BlockState or (byte)MessageId.VisualState) Recoveries++;
            return inner.Send(peer, channel, payload);
        }
        public void Disconnect(int peer) => inner.Disconnect(peer);
        public int GetRoundTripMs(int peer) => inner.GetRoundTripMs(peer);
        public ulong GetPeerOrigin(int peer) => inner.GetPeerOrigin(peer);
        public void Dispose() { }
    }

    private sealed class Rig : IDisposable
    {
        internal readonly LoopbackLink[] Links;
        internal readonly Wire[] Wires;
        internal readonly ClientSession[] Clients;
        internal readonly ServerHost Server;
        internal readonly HullStore Hulls;
        internal readonly ManualTime Clock = new();
        internal long Tick;
        internal Rig(bool hideSecondInitially = false, int players = 2)
        {
            Links = Enumerable.Range(0, players).Select(_ => new LoopbackLink()).ToArray();
            Wires = Links.Select(l => new Wire(l.Server)).ToArray();
            Server = new ServerHost(new ServerConfig { Clock = Clock, MaxPlayers = players }, Content, [.. Wires]);
            // Intercept at the transport boundary while retaining the same identity for callbacks.
            Clients = Links.Select((l, i) => new ClientSession(l.Client, Content, $"Player{i}", clock: Clock)).ToArray();
            Hulls = Get<HullStore>("_hulls");
            if (hideSecondInitially)
                Wires[1].Welcomed = owner =>
                {
                    Wires[0].ForbiddenOwner = owner;
                    for (var i = 0; i < Hulls.Slots; i++)
                        if (Hulls.IsAliveAt(i) && Hulls.Info(i).Owner == owner) Hulls.State(i).Position += new Double3(0, 1_000_000, 0);
                    var rocks = Get<RockField>("_rocks");
                    var sector = SaveRig.RockySector(rocks);
                    Assert.True(rocks.Set(new RockDelta { Rock = new RockRef(sector, 0), Offset = new Vector3(0, 1_000_000, 0), StartTick = Tick }));
                };
            for (var i = 0; i < 600 && Clients.Any(c => c.AcquireSnapshot().OwnHull < 0); i++) { Step(); Thread.Yield(); }
            Assert.True(Snap(0).OwnHull >= 0 && Snap(1).OwnHull >= 0);
        }
        internal T Get<T>(string field) => (T)typeof(ServerHost).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Server)!;
        internal SessionSnapshot Snap(int i) => Clients[i].AcquireSnapshot();
        internal uint Owner(int i) => Snap(i).PlayerId;
        internal uint Own(int i) { var s = Snap(i); return s.Hulls[s.OwnHull].Id; }
        internal void Step(int count = 1)
        {
            for (var n = 0; n < count; n++)
            {
                Clock.Advance(ManualTime.Tick);
                ((ITickHandler)Server).Tick(Tick);
                foreach (var client in Clients) ((ITickHandler)client).Tick(Tick);
                Tick++;
            }
        }
        internal void MoveOwner(int client, Double3 delta)
        {
            for (var i = 0; i < Hulls.Slots; i++)
                if (Hulls.IsAliveAt(i) && Hulls.Info(i).Owner == Owner(client)) Hulls.State(i).Position += delta;
        }
        public void Dispose() { foreach (var client in Clients) client.Dispose(); Server.Dispose(); }
    }

    [Fact]
    public void GasHidesHullOnWireAndProximityStillDisclosesIt()
    {
        using var rig = new Rig();
        var galaxy = rig.Server.Galaxy;
        var anchor = new SectorCoord(127, -1, -127);
        Assert.True(galaxy.GasClouds!.TryGetCloud(galaxy, anchor, out var cloud));
        var center = GasCloudQuery.CenterOf(galaxy, new(anchor, cloud));
        // Put every contributing ship on its owner's side of the cloud.
        for (var i = 0; i < rig.Hulls.Slots; i++)
            if (rig.Hulls.IsAliveAt(i))
                rig.Hulls.State(i).Position = center + new Double3(0, rig.Hulls.Info(i).Owner == rig.Owner(0) ? -2300 : 2300, i * 30);
        rig.Step(20);
        rig.Wires[0].ForbiddenOwner = rig.Owner(1);
        rig.Wires[0].LeakedHulls = 0;
        rig.Step(20);
        Assert.Equal(0, rig.Wires[0].LeakedHulls);
        var target = rig.Hulls.State((int)(rig.Own(1) & 65535)).Position;
        Assert.False(rig.Server.Covered(rig.Owner(0), target));
        rig.Hulls.State((int)(rig.Own(1) & 65535)).Position = rig.Hulls.State((int)(rig.Own(0) & 65535)).Position + new Double3(100, 0, 0);
        rig.Step(20);
        Assert.True(rig.Wires[0].LeakedHulls > 0);
    }

    [Fact]
    public void ManySensorsNearGasHaveMeasuredTickCost()
    {
        using var rig = new Rig(players: 32);
        var anchor = new SectorCoord(133, 0, -77);
        Assert.True(rig.Server.Galaxy.GasClouds!.TryGetCloud(rig.Server.Galaxy, anchor, out var cloud));
        var center = GasCloudQuery.CenterOf(rig.Server.Galaxy, new(anchor, cloud));
        for (var i = 0; i < rig.Hulls.Slots; i++)
            if (rig.Hulls.IsAliveAt(i)) rig.Hulls.State(i).Position = center + new Double3(i % 12 * 400 - 2000, i / 12 * 400 - 1000, 0);
        for (var owner = 0; owner < 32; owner++)
            for (var j = 0; j < 3; j++)
                rig.Hulls.Create(rig.Owner(owner), (byte)Content.PodDesign, Content.Pod,
                    center + new Double3(owner % 8 * 400 - 1500, owner / 8 * 400 - 600, 500 + j * 400));
        rig.Step(150);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var probe = new AllocationProbe();
        long serverTime = 0;
        var rounds = 0;
        for (var i = 0; i < 60; i++)
            while (probe.Measuring())
            {
                rig.Clock.Advance(ManualTime.Tick);
                var started = System.Diagnostics.Stopwatch.GetTimestamp();
                ((ITickHandler)rig.Server).Tick(rig.Tick);
                serverTime += System.Diagnostics.Stopwatch.GetTimestamp() - started;
                foreach (var client in rig.Clients) ((ITickHandler)client).Tick(rig.Tick);
                rig.Tick++;
                rounds++;
            }
        watch.Stop();
        Console.WriteLine($"Gas sensors: {rig.Hulls.Count} hulls, 32 clients, {watch.Elapsed.TotalMilliseconds / rounds:F4} ms/full round, {serverTime * 1000.0 / System.Diagnostics.Stopwatch.Frequency / rounds:F4} ms/server tick; {probe.Bytes} allocated bytes.");
        Assert.Equal(192, rig.Hulls.Count);
        Assert.Equal(0, probe.Bytes);
    }

    [Fact]
    public void NavigationRetriesOnlyToItsOwnerAndDoesNotDiscloseJumpTargets()
    {
        using var rig = new Rig();
        var id = rig.Own(0);
        var target = rig.Hulls.State((int)(id & 65535)).Position + new Double3(0, 0, -20000);
        rig.Wires[1].ForbiddenOwner = rig.Owner(0);
        rig.Wires[0].RefuseNavigation = true;
        rig.Clients[0].Input.PlotJump(id, target);
        rig.Step(15);
        Assert.False(rig.Snap(0).Exploration.Navigation.HasPlot);
        rig.Wires[0].RefuseNavigation = false;
        rig.Step(15);
        Assert.Equal(target, rig.Snap(0).Exploration.Navigation.Target);
        Assert.True(rig.Snap(0).Exploration.Navigation.HasPlot);
        Assert.False(rig.Snap(1).Exploration.Navigation.HasPlot);
        Assert.Equal(default, rig.Snap(1).Exploration.Navigation.Target);
        rig.Clients[0].Input.IssueOrder(new HullOrder { Kind = OrderKind.Jump, Target = target });
        rig.Step(30);
        Assert.Equal(JumpPhase.Charge, rig.Hulls.State((int)(id & 65535)).Jump);
        Assert.True(rig.Wires[1].LeakedHulls > 0);
        Assert.Equal(0, rig.Wires[1].TargetLeaks);
        Assert.Equal(target, rig.Snap(0).Hulls[rig.Snap(0).OwnHull].Order.Target);
        rig.Wires[0].NavigationBytes = 0;
        rig.Step(30);
        Assert.Equal(0, rig.Wires[0].NavigationBytes);
    }

    [Fact]
    public void PlotDoesNotJumpAndManualStartRechecksMovedHull()
    {
        using var rig = new Rig();
        var id = rig.Own(0);
        var slot = (int)(id & 65535);
        var target = rig.Hulls.State(slot).Position + new Double3(0, 0, -20000);
        rig.Clients[0].Input.PlotJump(id, target);
        rig.Step(30);
        Assert.True(rig.Snap(0).Exploration.Navigation.HasPlot);
        Assert.Equal(target, rig.Snap(0).Exploration.Navigation.Target);
        Assert.Equal(JumpPhase.None, rig.Hulls.State(slot).Jump);
        rig.Hulls.State(slot).Position = target;
        rig.Clients[0].Input.IssueOrder(new HullOrder { Kind = OrderKind.Jump, Target = target });
        rig.Step(30);
        Assert.Equal(JumpRefusal.TooClose, rig.Snap(0).Exploration.Navigation.Refusal);
        Assert.Equal(JumpPhase.None, rig.Hulls.State(slot).Jump);
        rig.Clients[0].Input.IssueOrder(new HullOrder { Kind = OrderKind.Jump, Target = target + new Double3(0, 900000, 0) });
        rig.Step(30);
        Assert.Equal(JumpRefusal.TooFar, rig.Snap(0).Exploration.Navigation.Refusal);
        Assert.Equal(JumpPhase.None, rig.Hulls.State(slot).Jump);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LongBlindJumpDamagesOnceAndChartRemovesRisk(bool charted)
    {
        using var rig = new Rig();
        var slot = (int)(rig.Own(0) & 65535);
        var start = rig.Hulls.State(slot).Position;
        var target = start + new Double3(0, 0, -800000);
        if (charted) Assert.True(rig.Server.RecordChart(rig.Owner(0), rig.Server.Galaxy.SectorOf(target), rig.Tick));
        var intact = rig.Hulls.State(slot).Hull;
        rig.Clients[0].Input.IssueOrder(new HullOrder { Kind = OrderKind.Jump, Target = target });
        for (var i = 0; i < 1600 && rig.Hulls.State(slot).Jump != JumpPhase.Tunnel; i++) rig.Step();
        Assert.Equal(JumpPhase.Tunnel, rig.Hulls.State(slot).Jump);
        var after = rig.Hulls.State(slot).Hull;
        if (charted) Assert.Equal(intact, after);
        else Assert.InRange(after, intact * 0.1f, intact * 0.10001f);
        rig.Step(30);
        Assert.Equal(after, rig.Hulls.State(slot).Hull);
        Assert.InRange(rig.Snap(0).Hulls[rig.Snap(0).OwnHull].Hull, after - 0.1f, after + 0.1f);
    }

    [Fact]
    public void FleetJumpStartsAutonomouslyAndOutOfRangeIsRefused()
    {
        using var rig = new Rig();
        var own = (int)(rig.Own(0) & 65535);
        var handle = rig.Hulls.Create(rig.Owner(0), (byte)Content.PodDesign, Content.Pod,
            rig.Hulls.State(own).Position + new Double3(0, 100000, 0));
        var target = rig.Hulls.State(handle.Index).Position + new Double3(0, 0, -900000);
        rig.Clients[0].Input.IssueFleetOrder(handle.Packed, new HullOrder { Kind = OrderKind.Jump, Target = target });
        rig.Step(15);
        Assert.Equal(JumpRefusal.TooFar, rig.Snap(0).Exploration.Navigation.Refusal);
        Assert.Equal(JumpPhase.None, rig.Hulls.State(handle.Index).Jump);
        target = rig.Hulls.State(handle.Index).Position + new Double3(0, 0, -20000);
        rig.Clients[0].Input.IssueFleetOrder(handle.Packed, new HullOrder { Kind = OrderKind.Jump, Target = target });
        rig.Step(15);
        Assert.Equal(JumpPhase.Charge, rig.Hulls.State(handle.Index).Jump);
    }

    [Fact]
    public void OtherOwnersOrdersKeepKindWithoutDestination()
    {
        using var rig = new Rig();
        var position = rig.Hulls.State((int)(rig.Own(0) & 65535)).Position + new Vector3(0, 1000, 0);
        var hull = rig.Hulls.Create(rig.Owner(1), (byte)Content.PodDesign, Content.Pod, position);
        rig.Hulls.Info(hull.Index).Order = new HullOrder { Kind = OrderKind.MoveTo, Target = position + new Vector3(0, 1000, 0) };
        rig.Wires[0].ForbiddenOwner = rig.Owner(1);
        rig.Step(20);
        Assert.True(rig.Wires[0].LeakedHulls > 0);
        Assert.Equal(0, rig.Wires[0].TargetLeaks);
    }

    [Fact]
    public void OldSnapshotsStillAcceptRetainedSlots()
    {
        using var rig = new Rig();
        rig.Step(10);
        var world = typeof(ClientSession).GetField("_world", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(rig.Clients[0])!;
        var visible = world.GetType().GetMethod("Visible", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var maskTick = (long)world.GetType().GetField("_visibilityTick", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(world)!;
        Assert.True((bool)visible.Invoke(world, [rig.Own(0), maskTick - 1])!);
        rig.MoveOwner(1, new Double3(0, 10000, 0));
        rig.Step(10);
        maskTick = (long)world.GetType().GetField("_visibilityTick", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(world)!;
        Assert.False((bool)visible.Invoke(world, [rig.Own(1), maskTick - 1])!);
        Assert.True((bool)visible.Invoke(world, [rig.Own(1), maskTick + 1])!);
    }

    [Fact]
    public void GeneratedAndPreviouslyDisclosedRockPositionsReceiveChanges()
    {
        using var rig = new Rig();
        var rocks = rig.Get<RockField>("_rocks");
        var rock = new RockRef(SaveRig.RockySector(rocks), 0);
        Assert.True(rocks.Set(new RockDelta { Rock = rock, Offset = new Vector3(0, 10000, 0), StartTick = rig.Tick }));
        var slot = (int)(rig.Own(0) & 65535);
        var origin = rocks.GeneratedPositionOf(rock);
        rig.Hulls.State(slot).Position = origin + new Vector3(0, -4000, 0);
        rig.Step(30);
        Assert.True(rig.Wires[0].RockMessages > 0);
        rig.Hulls.State(slot).Position = origin + new Vector3(0, 10000, 0);
        Assert.True(rocks.Set(new RockDelta { Rock = rock, Offset = new Vector3(0, 20000, 0), StartTick = rig.Tick }));
        rig.Wires[0].RockMessages = 0;
        rig.Step(30);
        Assert.True(rig.Wires[0].RockMessages > 0);
    }

    [Fact]
    public void ThirtyTwoOwnersWith192SensorHullsHaveMeasuredTickCost()
    {
        using var rig = new Rig(players: 32);
        var origin = new Double3(1e9, 1e9, 1e9);
        for (var owner = 0; owner < 32; owner++)
        {
            rig.MoveOwner(owner, origin + new Double3(0, owner * 20000, 0));
            for (var i = 0; i < 3; i++)
                rig.Hulls.Create(rig.Owner(owner), (byte)Content.PodDesign, Content.Pod, origin + new Vector3(i * 100, owner * 20000 + 1000, 0));
        }
        rig.Step(60);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var probe = new AllocationProbe();
        for (var i = 0; i < 60; i++) while (probe.Measuring()) rig.Step();
        watch.Stop();
        Console.WriteLine($"Exploration scale: {rig.Hulls.Count} hulls, 32 clients, {watch.Elapsed.TotalMilliseconds / 60:F4} ms/full round; {probe.Bytes} allocated bytes.");
        Assert.Equal(192, rig.Hulls.Count);
        Assert.Equal(0, probe.Bytes);
    }

    [Fact]
    public void PartyOutsideSensorsPublishesCompleteUnknownBatchAndRecovers()
    {
        using var rig = new Rig();
        void Send(int i, SocialKind kind, uint player = 0)
        {
            Span<byte> bytes = stackalloc byte[SocialMessage.MaxWireSize];
            var writer = new NetWriter(bytes);
            new SocialMessage { Kind = kind, Player = player, InvitationId = rig.Snap(i).Social.Invitation.InvitationId }.Write(ref writer);
            rig.Links[i].Client.Send(0, NetChannel.Reliable, writer.Written);
            rig.Step(65);
        }
        Send(0, SocialKind.Invite, rig.Owner(1));
        Send(1, SocialKind.Accept);
        rig.MoveOwner(1, new Double3(0, 10000, 0));
        rig.Step(120);
        var social = rig.Snap(0).Social;
        Assert.Equal(2, social.PartyCount);
        var member = social.Party.Take(social.PartyCount).Single(m => m.Player == rig.Owner(1));
        Assert.True(member.Unknown);
        Assert.Equal(0, member.X); Assert.Equal(0, member.Y); Assert.Equal(0, member.Z);
        Assert.Equal(0, member.Value); Assert.Equal(0, member.Hull); Assert.Equal(0, member.Shield);
        rig.MoveOwner(1, new Double3(0, -10000, 0));
        rig.Step(120);
        Assert.False(rig.Snap(0).Social.Party.Take(2).Single(m => m.Player == rig.Owner(1)).Unknown);
    }

    [Fact]
    public void IdleChartsAndRocksStopSendingReliableBytes()
    {
        using var rig = new Rig();
        for (var i = 0; i < 40; i++) Assert.True(rig.Server.RecordChart(rig.Owner(0), new(i, 0, 0), rig.Tick));
        var rocks = rig.Get<RockField>("_rocks");
        var rock = new RockRef(SaveRig.RockySector(rocks), 0);
        Assert.True(rocks.Set(new RockDelta { Rock = rock, StartTick = rig.Tick }));
        var own = (int)(rig.Own(0) & 65535);
        rig.Hulls.State(own).Position = rocks.GeneratedPositionOf(rock) + new Vector3(0, 4000, 0);
        rig.Step(90);
        Assert.Equal(40, rig.Snap(0).Exploration.ChartCount);
        Assert.True(rig.Wires[0].RockBytes > 0);
        rig.Wires[0].ExplorationBytes = rig.Wires[0].RockBytes = rig.Wires[0].NavigationBytes = 0;
        rig.Step(120);
        Assert.Equal(0, rig.Wires[0].ExplorationBytes);
        Assert.Equal(0, rig.Wires[0].NavigationBytes);
        Assert.Equal(0, rig.Wires[0].RockBytes);
    }

    [Fact]
    public void SensorlessHullRetainsNearbyCollisionContacts()
    {
        using var rig = new Rig();
        rig.MoveOwner(0, new Double3(1e9, 0, 0));
        var position = rig.Hulls.State((int)(rig.Own(0) & 65535)).Position;
        var drone = rig.Hulls.Create(rig.Owner(1), (byte)Content.DroneDesign, Content.Designs[Content.DroneDesign], position + new Vector3(100, 0, 0));
        rig.Step(20);
        Assert.True(rig.Server.Covered(rig.Owner(1), position));
        Assert.Contains(rig.Snap(1).Exploration.Contacts.Take(rig.Snap(1).Exploration.ContactCount), c => c.Hull == rig.Own(0) && c.Live);
        rig.Hulls.State(drone.Index).Position += new Double3(0, 1000, 0);
        rig.Step(20);
        Assert.False(rig.Server.Covered(rig.Owner(1), position));
    }

    [Fact]
    public void ObservedDestructionRemovesContactImmediately()
    {
        using var rig = new Rig();
        var position = rig.Hulls.State((int)(rig.Own(0) & 65535)).Position + new Vector3(0, 1000, 0);
        var hull = rig.Hulls.Create(rig.Owner(1), (byte)Content.PodDesign, Content.Pod, position);
        rig.Step(20);
        Assert.Contains(rig.Snap(0).Exploration.Contacts.Take(rig.Snap(0).Exploration.ContactCount), c => c.Hull == hull.Packed);
        rig.Hulls.Destroy(hull);
        rig.Step(1);
        Assert.DoesNotContain(rig.Snap(0).Exploration.Contacts.Take(rig.Snap(0).Exploration.ContactCount), c => c.Hull == hull.Packed);
    }

    [Fact]
    public void ReusingAHullSlotDoesNotEraseItsLostContact()
    {
        using var rig = new Rig();
        var position = rig.Hulls.State((int)(rig.Own(0) & 65535)).Position + new Vector3(0, 1000, 0);
        var old = rig.Hulls.Create(rig.Owner(1), (byte)Content.PodDesign, Content.Pod, position);
        rig.Step(10);
        rig.Hulls.State(old.Index).Position += new Double3(0, 1_000_000, 0);
        rig.Step(10);
        rig.Hulls.Destroy(old);
        var replacement = rig.Hulls.Create(rig.Owner(1), (byte)Content.PodDesign, Content.Pod, position);
        Assert.Equal(old.Index, replacement.Index);
        rig.Step(10);
        var view = rig.Snap(0).Exploration;
        Assert.Contains(view.Contacts.Take(view.ContactCount), c => c.Hull == old.Packed && !c.Live);
        Assert.Contains(view.Contacts.Take(view.ContactCount), c => c.Hull == replacement.Packed && c.Live);
    }

    [Fact]
    public void InitiallyUnseenOwnerAndInitialAsteroidReplayNeverReachFirstClient()
    {
        using var rig = new Rig(hideSecondInitially: true);
        rig.Step(60);
        Assert.Equal(0, rig.Wires[0].LeakedHulls);
        Assert.Equal(0, rig.Wires[0].RockMessages);
        Assert.DoesNotContain(rig.Snap(0).Exploration.Contacts.Take(rig.Snap(0).Exploration.ContactCount), c => c.Owner == rig.Owner(1));
    }

    [Fact]
    public void RemoteOwnedSensorAddsCoverageAndLosingItRemovesCoverage()
    {
        using var rig = new Rig();
        rig.MoveOwner(1, new Double3(0, Content.Galaxy.SectorSize * 4, Content.Galaxy.SectorSize * 3));
        rig.Step(10);
        var other = rig.Own(1);
        var position = rig.Hulls.State((int)(other & 65535)).Position;
        var scout = rig.Hulls.Create(rig.Owner(0), (byte)Content.PodDesign, Content.Pod, position + new Vector3(1000, 0, 0));
        rig.Step(10);
        Assert.Contains(rig.Snap(0).Exploration.Contacts.Take(rig.Snap(0).Exploration.ContactCount), c => c.Hull == other && c.Live);
        Assert.Equal(0, rig.Snap(0).Exploration.ChartCount);
        var sensor = Array.FindIndex(Content.Pod.Types, t => t.Sensor);
        Assert.True(rig.Hulls.BlockStates.Lose(rig.Hulls.Info(scout.Index).BlockState, sensor));
        rig.Step(10);
        Assert.Contains(rig.Snap(0).Exploration.Contacts.Take(rig.Snap(0).Exploration.ContactCount), c => c.Hull == other && !c.Live);
    }

    [Fact]
    public void LosingChartingEquipmentOrChangingControlCancelsScan()
    {
        using var rig = new Rig();
        rig.Clients[0].Input.RequestScan();
        rig.Step(10);
        var slot = (int)(rig.Own(0) & 65535);
        var charting = Array.FindIndex(Content.Pod.Types, t => t.Charting);
        Assert.True(rig.Hulls.BlockStates.Lose(rig.Hulls.Info(slot).BlockState, charting));
        rig.Step(5);
        Assert.Equal(ScanState.Cancelled, rig.Snap(0).Exploration.Scan.State);
        Assert.Equal(0, rig.Snap(0).Exploration.ChartCount);
        rig.Clients[1].Input.RequestScan();
        rig.Step(10);
        rig.Clients[1].Input.RequestControl(0);
        rig.Step(5);
        Assert.Equal(ScanState.Cancelled, rig.Snap(1).Exploration.Scan.State);
    }

    [Fact]
    public void ChartCapacityRefusesNewSectorsWithoutEvictingOrBlockingRefresh()
    {
        using var rig = new Rig();
        for (var i = 0; i < Content.Exploration.ChartsPerOwner; i++)
            Assert.True(rig.Server.RecordChart(rig.Owner(0), new SectorCoord(i, 0, 0), rig.Tick));
        Assert.False(rig.Server.RecordChart(rig.Owner(0), new SectorCoord(-1, 0, 0), rig.Tick));
        Assert.True(rig.Server.RecordChart(rig.Owner(0), default, rig.Tick));
        Assert.Equal(Content.Exploration.ChartsPerOwner, rig.Server.Charts.Length);
    }

    [Fact]
    public void ManyHullTicksAreMeasuredAndAllocateNothingAfterWarmup()
    {
        using var rig = new Rig();
        var origin = new Double3(1e9, 1e9, 1e9);
        rig.MoveOwner(0, origin);
        rig.MoveOwner(1, origin);
        for (var i = 0; i < 122; i++)
            rig.Hulls.Create(rig.Owner(i % 2), (byte)Content.PodDesign, Content.Pod, origin + new Vector3(0, 500 + i * 20, 0));
        rig.Step(150);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var probe = new AllocationProbe();
        for (var i = 0; i < 120; i++) while (probe.Measuring()) rig.Step();
        watch.Stop();
        Console.WriteLine($"Exploration: {rig.Hulls.Count} hulls, two clients, {watch.Elapsed.TotalMilliseconds / 120:F4} ms per full tick/client pair round; {probe.Bytes} allocated bytes.");
        Assert.Equal(0, probe.Bytes);
    }

    [Fact]
    public void FlyingScanChartsOnlyCurrentSectorAndContactsNeverChart()
    {
        using var rig = new Rig();
        rig.Step(20);
        Assert.Equal(0, rig.Snap(0).Exploration.ChartCount);
        var slot = (int)(rig.Own(0) & 65535);
        var before = rig.Hulls.State(slot).Position;
        var sector = rig.Server.Galaxy.SectorOf(before);
        rig.Clients[0].Input.RequestScan();
        rig.Clients[0].Input.Submit(new PlayerControls { Forward = 1 });
        rig.Step(Content.Exploration.ScanTicks + 20);
        var view = rig.Snap(0).Exploration;
        Assert.Equal(ScanState.Completed, view.Scan.State);
        Assert.True((rig.Hulls.State(slot).Position - before).Length > 1);
        Assert.Equal(1, view.ChartCount);
        Assert.Equal(sector, view.Charts[0].Sector);
        Assert.Equal(0, rig.Snap(1).Exploration.ChartCount);
    }

    [Fact]
    public void SectorChangeCancelsScan()
    {
        using var rig = new Rig();
        rig.Clients[0].Input.RequestScan();
        rig.Step(10);
        rig.MoveOwner(0, new Double3(0, Content.Galaxy.SectorSize * 2, 0));
        rig.Step(5);
        Assert.Equal(ScanState.Cancelled, rig.Snap(0).Exploration.Scan.State);
        Assert.Equal(0, rig.Snap(0).Exploration.ChartCount);
    }

    [Fact]
    public void ACompletedScanStartsASectorCooldownTheServerEnforcesAndARefusalDoesNotMoveIt()
    {
        using var rig = new Rig();
        rig.Step(20);
        var sector = rig.Server.Galaxy.SectorOf(rig.Hulls.State((int)(rig.Own(0) & 65535)).Position);
        Assert.Equal(600, Content.Exploration.ScanCooldownTicks);
        rig.Clients[0].Input.RequestScan();
        rig.Step(Content.Exploration.ScanTicks + 10);
        var done = rig.Snap(0).Exploration.Scan;
        Assert.Equal(ScanState.Completed, done.State);
        Assert.Equal(sector, done.CooldownSector);
        var until = done.CooldownUntilTick;
        Assert.InRange(until - done.CompletesTick, Content.Exploration.ScanCooldownTicks, Content.Exploration.ScanCooldownTicks + 2);
        var recorded = rig.Snap(0).Exploration.Charts[0].RecordedTick;

        // The server refuses with a reason, charts nothing again and leaves the cooldown where it was.
        rig.Clients[0].Input.RequestScan();
        rig.Step(10);
        var refused = rig.Snap(0).Exploration.Scan;
        Assert.Equal(ScanState.Cooldown, refused.State);
        Assert.Equal(sector, refused.Sector);
        Assert.Equal(until, refused.CooldownUntilTick);
        Assert.Equal(recorded, rig.Snap(0).Exploration.Charts[0].RecordedTick);

        // After the cooldown the same sector can be scanned again, and refreshes its chart.
        rig.Step((int)(until - rig.Tick) + 5);
        rig.Clients[0].Input.RequestScan();
        rig.Step(10);
        Assert.Equal(ScanState.Scanning, rig.Snap(0).Exploration.Scan.State);
        rig.Step(Content.Exploration.ScanTicks + 10);
        Assert.Equal(ScanState.Completed, rig.Snap(0).Exploration.Scan.State);
        Assert.True(rig.Snap(0).Exploration.Charts[0].RecordedTick > recorded);
    }

    [Fact]
    public void ACancelledScanAlsoStartsTheCooldownButOnlyForItsOwnSector()
    {
        using var rig = new Rig();
        rig.Step(20);
        var home = rig.Server.Galaxy.SectorOf(rig.Hulls.State((int)(rig.Own(0) & 65535)).Position);
        var hop = new Double3(0, Content.Galaxy.SectorSize * 2, 0);
        rig.Clients[0].Input.RequestScan();
        rig.Step(10);
        rig.MoveOwner(0, hop);
        rig.Step(5);
        var cancelled = rig.Snap(0).Exploration.Scan;
        Assert.Equal(ScanState.Cancelled, cancelled.State);
        Assert.Equal(home, cancelled.CooldownSector);
        Assert.True(cancelled.CooldownUntilTick > rig.Tick);

        // The sector left behind stays cooling down.
        rig.MoveOwner(0, new Double3(0, -hop.Y, 0));
        rig.Step(5);
        rig.Clients[0].Input.RequestScan();
        rig.Step(10);
        Assert.Equal(ScanState.Cooldown, rig.Snap(0).Exploration.Scan.State);
        Assert.Equal(0, rig.Snap(0).Exploration.ChartCount);
    }

    [Fact]
    public void AnotherSectorCanBeScannedAtOnceAfterACancelledScan()
    {
        using var rig = new Rig();
        rig.Step(20);
        var home = rig.Server.Galaxy.SectorOf(rig.Hulls.State((int)(rig.Own(0) & 65535)).Position);
        rig.Clients[0].Input.RequestScan();
        rig.Step(10);
        rig.MoveOwner(0, new Double3(0, Content.Galaxy.SectorSize * 2, 0));
        rig.Step(5);
        Assert.Equal(ScanState.Cancelled, rig.Snap(0).Exploration.Scan.State);
        rig.Clients[0].Input.RequestScan();
        rig.Step(10);
        var scan = rig.Snap(0).Exploration.Scan;
        Assert.Equal(ScanState.Scanning, scan.State);
        Assert.NotEqual(home, scan.Sector);
        Assert.Equal(home, scan.CooldownSector);
    }

    [Fact]
    public void SwitchingShipCancelsAScanAndCoolsItsSectorDown()
    {
        using var rig = new Rig();
        rig.Clients[1].Input.RequestScan();
        rig.Step(10);
        rig.Clients[1].Input.RequestControl(0);
        rig.Step(5);
        var scan = rig.Snap(1).Exploration.Scan;
        Assert.Equal(ScanState.Cancelled, scan.State);
        Assert.Equal(scan.Sector, scan.CooldownSector);
        Assert.True(scan.CooldownUntilTick > rig.Tick);
    }

    [Fact]
    public void TheScanWireCarriesTheCooldownAndRefusesANegativeTick()
    {
        var scan = new ScanView { State = ScanState.Cooldown, Sector = new SectorCoord(3, -1, 4), CooldownSector = new SectorCoord(3, -1, 4), CooldownUntilTick = 1234 };
        Span<byte> bytes = stackalloc byte[ExplorationMessage.MaxWireSize];
        var writer = new NetWriter(bytes);
        ExplorationMessage.Write(ref writer, scan, default);
        var reader = new NetReader(writer.Written[1..]);
        Span<SectorChart> charts = stackalloc SectorChart[ExplorationMessage.MaxCharts];
        Assert.True(ExplorationMessage.Read(ref reader, charts, out var read, out var count));
        Assert.Equal(0, count);
        Assert.Equal(ScanState.Cooldown, read.State);
        Assert.Equal(scan.CooldownSector, read.CooldownSector);
        Assert.Equal(1234, read.CooldownUntilTick);

        scan.CooldownUntilTick = -1;
        writer = new NetWriter(bytes);
        ExplorationMessage.Write(ref writer, scan, default);
        reader = new NetReader(writer.Written[1..]);
        Assert.False(ExplorationMessage.Read(ref reader, charts, out _, out _));
    }

    [Fact]
    public void HiddenShipsAndAsteroidChangesNeverReachOtherClientAndLostContactFreezes()
    {
        using var rig = new Rig();
        rig.Step(20);
        var other = rig.Own(1);
        var previous = rig.Snap(0).Exploration.Contacts.Take(rig.Snap(0).Exploration.ContactCount).Single(c => c.Hull == other);
        rig.MoveOwner(1, new Double3(0, Content.Galaxy.SectorSize * 4, 0));
        rig.Wires[0].ForbiddenOwner = rig.Owner(1);
        rig.Step(10);
        Assert.Equal(0, rig.Wires[0].LeakedHulls);
        var lost = rig.Snap(0).Exploration.Contacts.Take(rig.Snap(0).Exploration.ContactCount).Single(c => c.Hull == other);
        Assert.False(lost.Live);
        Assert.Equal(previous.Position, lost.Position);
        var rocks = rig.Get<RockField>("_rocks");
        var sector = SaveRig.RockySector(rocks);
        var rock = new RockRef(sector, 0);
        Assert.True(rocks.Set(new RockDelta { Rock = rock, Offset = new Vector3(0, 1_000_000, 0), StartTick = rig.Tick }));
        rig.Wires[0].RockMessages = 0;
        rig.Step(10);
        Assert.Equal(0, rig.Wires[0].RockMessages);
        Span<byte> ack = stackalloc byte[15];
        var writer = new NetWriter(ack);
        writer.WriteByte((byte)MessageId.BlockAck); writer.WriteByte(1); writer.WriteUInt32(other);
        writer.WriteUInt32(0); writer.WriteByte(3); writer.WriteUInt32(0);
        rig.Links[0].Client.Send(0, NetChannel.Unreliable, writer.Written);
        rig.Wires[0].Recoveries = 0;
        rig.Step(10);
        Assert.Equal(0, rig.Wires[0].Recoveries);
        rig.Step(Content.Exploration.ContactTicks + 10);
        Assert.DoesNotContain(rig.Snap(0).Exploration.Contacts.Take(rig.Snap(0).Exploration.ContactCount), c => c.Hull == other);
        rig.MoveOwner(1, new Double3(0, -Content.Galaxy.SectorSize * 4, 0));
        rig.Step(10);
        Assert.Contains(rig.Snap(0).Exploration.Contacts.Take(rig.Snap(0).Exploration.ContactCount), c => c.Hull == other && c.Live);
    }
}
