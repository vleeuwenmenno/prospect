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
        internal int LeakedHulls, RockMessages, Recoveries;
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
                    if (HullRecord.TryRead(ref reader, out var record) && record.Owner == ForbiddenOwner) LeakedHulls++;
            if (payload[0] == (byte)MessageId.RockDeltas) RockMessages++;
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
        internal readonly LoopbackLink[] Links = [new(), new()];
        internal readonly Wire[] Wires;
        internal readonly ClientSession[] Clients;
        internal readonly ServerHost Server;
        internal readonly HullStore Hulls;
        internal readonly ManualTime Clock = new();
        internal long Tick;
        internal Rig(bool hideSecondInitially = false)
        {
            Wires = [new(Links[0].Server), new(Links[1].Server)];
            Server = new ServerHost(new ServerConfig { Clock = Clock }, Content, [.. Wires]);
            // Intercept at the transport boundary while retaining the same identity for callbacks.
            Clients = [new(Links[0].Client, Content, "One", clock: Clock), new(Links[1].Client, Content, "Two", clock: Clock)];
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
            for (var i = 0; i < 600 && (Snap(0).OwnHull < 0 || Snap(1).OwnHull < 0); i++) { Step(); Thread.Yield(); }
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
        var scout = rig.Hulls.Create(rig.Owner(0), (byte)Content.PodDesign, Content.Pod, position + new Vector3(100, 0, 0));
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
