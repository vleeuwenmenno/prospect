using System.Diagnostics;
using System.Numerics;
using Prospect.Engine.Net;
using Prospect.Engine.Time;
using Prospect.Game.Client;
using Prospect.Game.Persistence;
using Prospect.Game.Protocol;
using Prospect.Game.Server;
using Prospect.Game.World;
using Xunit;

namespace Prospect.Tests;

/// <summary>
/// The lifecycle of a saved world around a store: what a save holds and a load brings back, the world's own
/// clock, the one capture buffer, durable owners and entities, closing, faults and refusals. The store is in
/// memory; the server, the worker, the codec and the client are the real ones.
/// </summary>
public sealed class SaveLifecycleTests
{
    private const long Far = 5_000_000_000;
    private static readonly GameContent Shipped = GameContent.Load();
    private static readonly GameContent Custom = SaveRig.WithCustomDesign(Shipped);

    /// <summary>A world with one of everything a save must keep, as bytes: made once, loaded by many tests.</summary>
    internal static readonly Lazy<byte[]> Populated = new(() =>
    {
        var folder = SaveRig.NewWorld(Custom);
        using (var rig = new SaveRig(folder, Shipped))
        {
            Populate(rig);
            Assert.True(rig.Close().Clean);
        }

        return folder.Bytes!;
    });

    private static MemoryWorld PopulatedWorld() => new() { Bytes = Populated.Value, Generation = 7 };

    [Fact]
    public void NewShipKeepsItsOwnerRootNameAndEntityAcrossSaveLoad()
    {
        var folder = SaveRig.NewWorld(Shipped);
        ulong entity;
        uint owner;
        Double3 position;
        using (var rig = new SaveRig(folder, Shipped))
        {
            rig.Join();
            rig.Link.Control(0);
            rig.Tick();
            ShipName name = default;
            Assert.True(name.Set("Saved new ship"));
            var bytes = new byte[EditorRequestMessage.MaxWireSize];
            var writer = new NetWriter(bytes);
            new EditorRequestMessage { Kind = EditorRequestKind.NewShip, Name = name }.Write(ref writer, default);
            rig.Link.Deliver(writer.Written.ToArray());
            rig.Tick();
            TestWorkers.Settle(rig.Server);
            rig.Tick(2);
            Assert.Equal(EditorPhase.Publishing, rig.Link.Editor.Phase);
            writer = new NetWriter(bytes);
            DesignReadyMessage.Write(ref writer, (byte)(rig.Server.Content.Designs.Length - 1), true);
            rig.Link.Deliver(writer.Written.ToArray());
            rig.Tick();
            Assert.Equal(NewShipStatus.Created, rig.Link.Editor.NewShip);
            var index = (int)(rig.Link.Editor.Hull & 0xffff);
            entity = rig.Hulls.Info(index).Entity;
            owner = rig.Hulls.Info(index).Owner;
            position = rig.Hulls.State(index).Position;
            Assert.True(rig.Close().Clean);
        }
        using var loaded = new SaveRig(folder, Shipped);
        var found = false;
        for (var i = 0; i < loaded.Hulls.Slots; i++)
        {
            if (!loaded.Hulls.IsAliveAt(i) || loaded.Hulls.Info(i).Entity != entity) continue;
            found = true;
            Assert.Equal(owner, loaded.Hulls.Info(i).Owner);
            Assert.False(loaded.Hulls.Info(i).EscapeDrone);
            Assert.Equal(position, loaded.Hulls.State(i).Position);
            var design = loaded.Server.Content.Designs[loaded.Hulls.Info(i).Design];
            Assert.Equal("Saved new ship", design.Name);
            Assert.Equal(1, design.BlockCount);
            Assert.Equal("core", design.Boxes[0].Type.Id);
        }
        Assert.True(found);
    }

    [Fact]
    public void MalformedChartsAreRefusedByDecoderBeforeWritableStoreOpen()
    {
        var folder = PopulatedWorld();
        var snapshot = folder.Decode();
        Assert.True(snapshot.ChartCount > 0);
        var original = snapshot.Charts[0];
        snapshot.Charts[0] = original with { Owner = uint.MaxValue };
        Assert.False(WorldSnapshotCodec.TryDecode(WorldSnapshotCodec.Encode(snapshot), SaveLimits.Default, out _, out _));
        snapshot.Charts[0] = original with { RecordedTick = snapshot.NextWorldTick };
        Assert.False(WorldSnapshotCodec.TryDecode(WorldSnapshotCodec.Encode(snapshot), SaveLimits.Default, out _, out _));
        snapshot.Charts[0] = original;
        snapshot.Charts[1] = original;
        Assert.False(WorldSnapshotCodec.TryDecode(WorldSnapshotCodec.Encode(snapshot), SaveLimits.Default, out _, out _));
    }

    [Fact]
    public void ChartsKeepTheirOwnerSectorAndSurveyTickAcrossSaveLoad()
    {
        var folder = SaveRig.NewWorld(Shipped);
        SectorChart expected;
        using (var rig = new SaveRig(folder, Shipped))
        {
            rig.Join();
            expected = new SectorChart(rig.Hulls.Info(0).Owner, new SectorCoord(4, -2, 7), rig.WorldTick);
            Assert.True(rig.Server.RecordChart(expected.Owner, expected.Sector, expected.RecordedTick));
            rig.Tick();
            Assert.True(rig.Close().Clean);
        }
        using var loaded = new SaveRig(folder, Shipped);
        Assert.Equal(expected, Assert.Single(loaded.Server.Charts.ToArray()));
    }

    [Fact]
    public void ReorderedSettingsKeepTheirValuesAcrossLoadCloseAndReload()
    {
        var folder = SaveRig.NewWorld(Shipped);
        var snapshot = folder.Decode();
        Array.Reverse(snapshot.SettingIds);
        Array.Reverse(snapshot.SettingValues, 0, snapshot.SettingCount);
        folder.Bytes = WorldSnapshotCodec.Encode(snapshot);
        using (var rig = new SaveRig(folder, Shipped))
        {
            Assert.Equal(Shipped.Settings, rig.Server.Content.Settings);
            Assert.True(rig.Close().Clean);
        }

        using var reloaded = new SaveRig(folder, Shipped);
        Assert.Equal(Shipped.Settings, reloaded.Server.Content.Settings);
    }

    [Fact]
    public void CheckpointFailureKeepsItsReasonWithoutFailingTheCommit()
    {
        var folder = SaveRig.NewWorld(Shipped);
        folder.Checkpoint = CheckpointOutcome.Failed;
        using var rig = new SaveRig(folder, Shipped);
        var close = rig.Close();
        Assert.True(close.Clean);
        Assert.True(close.Degraded);
        Assert.Equal("checkpoint could not complete", close.CheckpointError);
        Assert.Equal(close.CheckpointError, close.Error);
        Assert.Null(close.BackupError);
    }

    private static void Jumping(HullStore hulls, int slot, JumpPhase phase)
    {
        ref var state = ref hulls.State(slot);
        ref var info = ref hulls.Info(slot);
        var flight = hulls.Flight(slot);
        Assert.NotEqual(FtlKind.None, flight.Ftl);
        var target = state.Position + new Vector3(6000 + 1500 * slot, 0, 0);
        info.Order = new HullOrder { Kind = phase == JumpPhase.Arrive ? OrderKind.None : OrderKind.Jump, Target = target };
        state.Jump = phase;
        state.JumpTunnelSeconds = HullSim.JumpTravelSeconds(state.Position, target, flight);
        switch (phase)
        {
            case JumpPhase.Charge:
                state.JumpTimer = flight.FtlEnergy * 0.25f;
                state.JumpCalculation = flight.FtlCalculateSeconds * 0.5f;
                state.Energy *= 0.5f;
                break;
            case JumpPhase.Tunnel:
                state.Position = target;
                state.Velocity = default;
                state.JumpTunnelElapsed = 1.25;
                state.JumpTimer = 1.25f;
                break;
            case JumpPhase.Arrive:
                state.Position = target;
                state.Velocity = default;
                state.JumpTunnelElapsed = state.JumpTunnelSeconds;
                state.JumpTimer = 0.5f;
                break;
        }
    }

    /// <summary>
    /// Lets the host in and then puts the world into a state with one of everything: a custom hull that has
    /// lost blocks and has blocks of very different size damaged, dents, a shield half charged and cooling
    /// down, a hull in every stage of a jump (the piloted pod among them), a standing move order, and pushed
    /// asteroids in two sectors.
    /// </summary>
    private static void Populate(SaveRig rig)
    {
        rig.Join();
        rig.Tick(5);
        var hulls = rig.Hulls;
        var states = hulls.BlockStates;
        var tick = rig.WorldTick;
        Assert.True(rig.Server.RecordChart(hulls.Info(0).Owner, new SectorCoord(3, -2, 1), tick - 1));
        Assert.True(rig.Server.RecordChart(hulls.Info(0).Owner, new SectorCoord(4, -2, 1), tick - 1));
        Assert.Equal(6, hulls.Count);

        var custom = rig.Server.Content.Designs[hulls.Info(3).Design];
        Assert.Equal("Custom Hauler", custom.Name);
        var big = custom.BlockCount - 1;
        var handle = hulls.Info(3).BlockState;
        var scratched = SaveRig.PlainBlock(custom, big, 2);
        Assert.True(custom.BlockHitPoints(big) > custom.BlockHitPoints(scratched) * 4f);
        Assert.True(states.Hit(handle, SaveRig.PlainBlock(custom, big, 1), 1e9f));
        Assert.True(states.Hit(handle, SaveRig.PlainBlock(custom, big), 1e9f));
        Assert.False(states.Hit(handle, big, custom.BlockHitPoints(big) * 0.37f));
        Assert.False(states.Hit(handle, scratched, custom.BlockHitPoints(scratched) * 0.5f));
        rig.Field.Refit(3);
        ref var damaged = ref hulls.State(3);
        var flight = hulls.Flight(3);
        damaged.Hull = (float)states.HitTotal(handle);
        damaged.Shield = flight.ShieldCapacity * 0.4f;
        damaged.ShieldCooldown = 3.5f;
        damaged.Energy = flight.EnergyStorage * 0.6f;
        damaged.ShieldHit = new ShieldHit { Serial = 7, Strength = 200, X = 10, Y = -20, Z = 30, AgeTicks = 12 };
        damaged.ShieldChangeTicks = 40;
        damaged.Velocity = new Vector3(1.5f, -0.25f, 0.125f);
        damaged.AngularVelocity = new Vector3(0.01f, 0.02f, -0.03f);
        ref var info = ref hulls.Info(3);
        info.DamageTick = tick - 1;
        info.Visual.Hit(custom, big, custom.BlockHitPoints(big) * 0.37f, custom.BlockOffsets[big] + new Vector3(custom.BlockHalfExtents[big].X, 0, 0), Vector3.UnitX, tick - 1);
        Assert.Equal(1, info.Visual.Count);
        info.Order = new HullOrder { Kind = OrderKind.MoveTo, Target = damaged.Position + new Vector3(0, 300, 0) };

        Jumping(hulls, 0, JumpPhase.Charge);
        Jumping(hulls, 1, JumpPhase.Align);
        Jumping(hulls, 2, JumpPhase.Charge);
        Jumping(hulls, 4, JumpPhase.Tunnel);
        Jumping(hulls, 5, JumpPhase.Arrive);

        var sector = SaveRig.RockySector(rig.Rocks);
        Assert.True(rig.Rocks.Set(new RockDelta { Rock = new RockRef(sector, 0), Offset = new Vector3(1, 2, 3), Velocity = new Vector3(0.5f, 0, -0.25f), StartTick = tick - 3 }));
        Assert.True(rig.Rocks.Set(new RockDelta { Rock = new RockRef(sector, 3), Offset = new Vector3(-4, 0, 9), Velocity = new Vector3(0, 0.75f, 0), StartTick = tick - 2 }));
    }

    private static byte[] Canonical(byte[] bytes)
    {
        Assert.True(WorldSnapshotCodec.TryDecode(bytes, SaveLimits.Default, out var snapshot, out var error), error);
        snapshot!.CapturedUnixMs = 0;
        return WorldSnapshotCodec.Encode(snapshot);
    }

    [Fact]
    public void ASavedWorldComesBackExactlyAndSavesToTheSameBytes()
    {
        var folder = SaveRig.NewWorld(Custom);
        var expected = new List<(HullState State, FlightParams Flight, float[] Points, int[] Lost, double Total, ulong Entity, uint Owner, byte Design, HullOrder Order, VisualDamageState Visual, bool Piloted, long DamageTick)>();
        RockDelta[] rocks;
        long tick;
        using (var rig = new SaveRig(folder, Shipped))
        {
            Populate(rig);
            var hulls = rig.Hulls;
            for (var i = 0; i < hulls.Slots; i++)
            {
                var info = hulls.Info(i);
                expected.Add((hulls.State(i), hulls.Flight(i), hulls.BlockHp(i).ToArray(), hulls.BlockStates.Losses(info.BlockState).ToArray(), hulls.BlockStates.HitTotal(info.BlockState), info.Entity, info.Owner, info.Design, info.Order, info.Visual, info.Piloted, info.DamageTick));
            }

            rocks = rig.Rocks.Deltas.ToArray();
            tick = rig.WorldTick;
            var close = rig.Close();
            Assert.Equal(FinalCommitOutcome.Committed, close.FinalCommit);
            Assert.Equal(CheckpointOutcome.Completed, close.Checkpoint);
            Assert.Equal(tick, rig.WorldTick);
        }

        Assert.True(folder.Clean);
        Assert.False(folder.Leased);
        var first = folder.Bytes!;
        Assert.Equal(first.Length, WorldSnapshotCodec.EncodedSize(folder.Decode()));

        // The files' catalogue has four designs and two hulls in a fleet; the world's has five and five.
        using (var rig = new SaveRig(folder, Shipped))
        {
            Assert.True(rig.WasClean);
            Assert.Equal(tick, rig.WorldTick);
            Assert.Equal(Custom.Designs.Length, rig.Server.Content.Designs.Length);
            Assert.Equal(Custom.StartingFleet, rig.Server.Content.StartingFleet);
            var hulls = rig.Hulls;
            Assert.Equal(expected.Count, hulls.Count);
            for (var i = 0; i < expected.Count; i++)
            {
                var info = hulls.Info(i);
                var was = expected[i];
                Assert.Equal(was.State, hulls.State(i));
                Assert.Equal(was.Flight, hulls.Flight(i));
                Assert.Equal(was.Points, hulls.BlockHp(i).ToArray());
                Assert.Equal(was.Lost, hulls.BlockStates.Losses(info.BlockState).ToArray());
                Assert.Equal(BitConverter.DoubleToInt64Bits(was.Total), BitConverter.DoubleToInt64Bits(hulls.BlockStates.HitTotal(info.BlockState)));
                Assert.Equal((uint)was.Lost.Length, hulls.Revision(i));
                Assert.Equal(info.FittedRevision, hulls.Revision(i));
                Assert.Equal((was.Entity, was.Owner, was.Design, was.Piloted, was.DamageTick), (info.Entity, info.Owner, info.Design, info.Piloted, info.DamageTick));
                Assert.Equal(was.Order, info.Order);
                Assert.Equal(was.Visual.Version, info.Visual.Version);
                Assert.Equal(was.Visual.Count, info.Visual.Count);
                Assert.Equal(was.Visual.Spark.Sequence, info.Visual.Spark.Sequence);
            }

            Assert.Equal(2u, hulls.Revision(3));
            Assert.Equal(rocks, rig.Rocks.Deltas.ToArray());
            Assert.Equal(1, rig.Rocks.MovedSectors);
            Assert.True(rig.Close().Clean);
        }

        // Nothing was simulated in between, so the second save is the first but for the moment it was taken.
        Assert.Equal(Canonical(first), Canonical(folder.Bytes!));
    }

    [Fact]
    public void JumpsOrdersAndChargeGoOnAfterALoadWithTheOwnerBackAtTheControls()
    {
        using var rig = new SaveRig(PopulatedWorld(), Shipped);
        var hulls = rig.Hulls;
        var charge = hulls.State(0).JumpTimer;
        var energy = hulls.State(2).JumpTimer;
        var shield = hulls.State(3).Shield;
        var cooldown = hulls.State(3).ShieldCooldown;
        var distance = hulls.Info(3).Order.Target.RelativeTo(hulls.State(3).Position).Length();
        var fleet = hulls.Count;

        rig.Join();
        Assert.Equal(fleet, hulls.Count);
        Assert.Equal(hulls.HandleAt(0).Packed, rig.Link.Welcome.HullId);
        rig.Tick(29);

        // The pod's owner is back in it, and its drive is still charging: taking a seat is not taking control.
        Assert.True(hulls.Info(0).Piloted);
        Assert.Equal(OrderKind.Jump, hulls.Info(0).Order.Kind);
        Assert.True(hulls.State(0).Jump >= JumpPhase.Charge && (hulls.State(0).Jump > JumpPhase.Charge || hulls.State(0).JumpTimer > charge));
        Assert.True(hulls.State(1).Jump is JumpPhase.Align or JumpPhase.Charge);
        Assert.Equal(OrderKind.Jump, hulls.Info(1).Order.Kind);
        Assert.True(hulls.State(2).Jump > JumpPhase.Charge || hulls.State(2).JumpTimer > energy);
        Assert.Equal(JumpPhase.Tunnel, hulls.State(4).Jump);
        Assert.Equal(1.25 + 30 * (double)HullSim.Dt, hulls.State(4).JumpTunnelElapsed, 6);
        Assert.Equal(JumpPhase.Arrive, hulls.State(5).Jump);
        Assert.Equal(1.0f, hulls.State(5).JumpTimer, 3);

        // The shield went on cooling down from where it was and has not been topped up or reset by the load.
        Assert.Equal(shield, hulls.State(3).Shield);
        Assert.Equal(cooldown - 30 * HullSim.Dt, hulls.State(3).ShieldCooldown, 3);
        Assert.Equal(OrderKind.MoveTo, hulls.Info(3).Order.Kind);
        Assert.True(hulls.Info(3).Order.Target.RelativeTo(hulls.State(3).Position).Length() < distance);

        rig.Tick(240);
        Assert.Equal(JumpPhase.None, hulls.State(5).Jump);
        Assert.True(hulls.State(4).Jump is JumpPhase.Arrive or JumpPhase.None);
        Assert.True(hulls.State(3).Shield > shield);
    }

    [Fact]
    public void AnOwnerIsGivenAFleetOnceAndFindsItAgainAcrossConnectionsAndRuns()
    {
        var folder = SaveRig.NewWorld(Shipped);
        ulong[] entities;
        uint player;
        using (var rig = new SaveRig(folder, Shipped))
        {
            rig.Join();
            player = rig.Link.Welcome.PlayerId;
            Assert.Equal(3, rig.Hulls.Count);
            entities = [rig.Hulls.Info(0).Entity, rig.Hulls.Info(1).Entity, rig.Hulls.Info(2).Entity];
            Assert.Equal(3, entities.Distinct().Count(entity => entity != 0));
            Assert.Equal(entities[0], rig.Owners[0].Controlled);
            rig.Tick(30);

            // Leaving takes nothing out of the world, and the world stops with nobody in it.
            var position = rig.Hulls.State(1).Position;
            rig.Leave();
            var paused = rig.WorldTick;
            rig.Tick(120);
            Assert.Equal(paused, rig.WorldTick);
            Assert.Equal(3, rig.Hulls.Count);
            Assert.Equal(position, rig.Hulls.State(1).Position);

            rig.Join();
            Assert.Equal(player, rig.Link.Welcome.PlayerId);
            Assert.Equal(paused, rig.Link.Welcome.ServerTick);
            Assert.Equal(rig.Hulls.HandleAt(0).Packed, rig.Link.Welcome.HullId);
            Assert.Equal(3, rig.Hulls.Count);
            rig.Tick(10);
            Assert.True(rig.Close().Clean);

            // Closing let the connection go after the save, and the fleet is in it.
            Assert.True(rig.Link.ClosedByServer);
        }

        var saved = folder.Decode();
        Assert.Equal(1, saved.OwnerCount);
        Assert.Equal(3, saved.HullCount);
        Assert.Equal(entities[0], saved.Owners[0].Controlled);

        using (var rig = new SaveRig(folder, Shipped))
        {
            Assert.Equal(3, rig.Hulls.Count);
            Assert.Equal(entities, new[] { rig.Hulls.Info(0).Entity, rig.Hulls.Info(1).Entity, rig.Hulls.Info(2).Entity });
            rig.Join();
            Assert.Equal(player, rig.Link.Welcome.PlayerId);
            Assert.Equal(3, rig.Hulls.Count);
            Assert.True(rig.Hulls.Info(0).Piloted);
        }
    }

    [Fact]
    public void ARepairKeepsAHullsNameAndEveryNewHullGetsANewOne()
    {
        var folder = SaveRig.NewWorld(Shipped);
        using var rig = new SaveRig(folder, Shipped);
        rig.Join();
        rig.Tick(3);
        var hulls = rig.Hulls;
        var pod = hulls.HandleAt(0);
        var name = hulls.Info(0).Entity;
        var highest = Math.Max(name, Math.Max(hulls.Info(1).Entity, hulls.Info(2).Entity));

        // Repair: a new hull, a new handle, the same ship.
        Assert.True(hulls.BlockStates.Hit(hulls.Info(0).BlockState, SaveRig.PlainBlock(Shipped.Pod), 1e9f));
        rig.Link.Edit(EditorRequestKind.Begin, pod.Packed);
        rig.Tick();
        Assert.Equal(EditorPhase.Open, rig.Link.Editor.Phase);
        rig.Link.Edit(EditorRequestKind.Repair, rig.Link.Editor.Token);
        rig.Tick(3);
        Assert.Equal(EditorPhase.Applied, rig.Link.Editor.Phase);
        var repaired = rig.Link.Editor.Hull;
        Assert.NotEqual(pod.Packed, repaired);
        Assert.False(hulls.IsAlive(pod));
        var slot = (int)(repaired & 0xFFFF);
        Assert.Equal(name, hulls.Info(slot).Entity);
        Assert.Equal(0u, hulls.Revision(slot));
        Assert.Equal(name, rig.Owners[0].Controlled);
        Assert.Equal(3, hulls.Count);

        // Stepping out: the drone is a new entity and the one the owner flies; the pod keeps its name.
        rig.Link.Control(0);
        rig.Tick();
        Assert.Equal(4, hulls.Count);
        var drone = rig.Owners[0].Controlled;
        Assert.True(drone > highest);
        Assert.Equal(name, hulls.Info(slot).Entity);
        Assert.False(hulls.Info(slot).Piloted);

        // Destroyed: the drone that follows is another new entity, never the lost hull's.
        rig.WithConnection("LoseHull");
        Assert.Equal(4, hulls.Count);
        var next = rig.Owners[0].Controlled;
        Assert.True(next > drone);

        Assert.True(rig.Close().Clean);
        var saved = folder.Decode();
        Assert.Equal(next, saved.Owners[0].Controlled);
        Assert.Equal(next + 1, saved.NextEntityId);
        Assert.Contains(saved.Hulls.Take(saved.HullCount), hull => hull.Entity == name && !hull.EscapeDrone);
        Assert.Contains(saved.Hulls.Take(saved.HullCount), hull => hull.Entity == next && hull.EscapeDrone);
    }

    [Fact]
    public void AnOwnerWhoseHullIsGoneGetsADroneAndNoFleet()
    {
        var folder = SaveRig.NewWorld(Shipped);
        using (var rig = new SaveRig(folder, Shipped))
        {
            rig.Join();
            rig.Tick(2);
            rig.Leave();

            // The hull the owner flew is destroyed while they are away.
            rig.Hulls.Destroy(rig.Hulls.HandleAt(0));
            Assert.True(rig.Close().Clean);
        }

        Assert.Equal(0ul, folder.Decode().Owners[0].Controlled);
        using var again = new SaveRig(folder, Shipped);
        Assert.Equal(2, again.Hulls.Count);
        again.Join();
        Assert.Equal(3, again.Hulls.Count);
        var mine = (int)(again.Link.Welcome.HullId & 0xFFFF);
        Assert.True(again.Hulls.Info(mine).EscapeDrone);
        Assert.Equal(again.Hulls.Info(mine).Entity, again.Owners[0].Controlled);
    }

    [Fact]
    public void ASavedWorldKeepsItsOwnClockWhateverTheSchedulerCountsAndStandsStillWhenEmpty()
    {
        var folder = SaveRig.NewWorld(Shipped);
        using var rig = new SaveRig(folder, Shipped, world => { world.NextWorldTick = Far; return world; });

        // The scheduler counts from zero; the world is where it was saved, and stays there with nobody in it.
        rig.Tick(200);
        Assert.Equal(Far, rig.WorldTick);

        // The door is still kept while the world stands still: a handshake that never comes is ended by the clock.
        var silent = rig.Remote.Connect(0x0A000001);
        rig.Tick();
        Assert.True(rig.Remote[silent].Open);
        rig.Clock.AdvanceSeconds(ConnectionLimits.Default.HandshakeSeconds + 1);
        rig.Tick();
        Assert.False(rig.Remote[silent].Open);

        // A stranger who does say hello is refused: nothing they can send proves they own anything here.
        var stranger = rig.Remote.Connect(0x0A000002);
        rig.Remote.Deliver(stranger, OwnerLink.HelloBytes(Shipped, "Host"));
        rig.Tick();
        Assert.Equal(1, rig.Remote[stranger].Rejects);
        Assert.Equal(RejectReason.ServerFull, rig.Remote[stranger].Reject);
        Assert.Equal(0, rig.Hulls.Count);
        Assert.Equal(Far, rig.WorldTick);

        // The host is let in at the world's interval, and it runs from the welcome on, download or not.
        rig.Link.Hello();
        rig.Tick();
        Assert.Equal(Far, rig.Link.Welcome.ServerTick);
        Assert.Equal(Far + 1, rig.WorldTick);
        rig.Tick(10);
        Assert.Equal(Far + 11, rig.WorldTick);
        Assert.Equal(0, rig.Link.Snapshots);
        rig.Link.Ready();
        rig.Tick(20);
        Assert.Equal(20, rig.Link.Snapshots);
        Assert.Equal(Far + 30, rig.Link.LastSnapshot.ServerTick);

        // A pause uses up no interval and sends no snapshot of one twice.
        rig.Leave();
        Assert.Equal(Far + 31, rig.WorldTick);
        rig.Tick(500);
        Assert.Equal(Far + 31, rig.WorldTick);
        rig.Join();
        Assert.Equal(Far + 31, rig.Link.Welcome.ServerTick);
        rig.Tick(5);
        Assert.Equal(Far + 36, rig.Link.LastSnapshot.ServerTick);
        Assert.False(rig.Link.SnapshotRepeated);

        Assert.True(rig.Close().Clean);
        Assert.Equal(Far + 37, folder.Decode().NextWorldTick);
    }

    [Fact]
    public void ARealClientResumesAWorldFarFromItsFirstInterval()
    {
        var folder = SaveRig.NewWorld(Shipped);
        using (var first = new SaveRig(folder, Shipped, world => { world.NextWorldTick = Far; return world; }))
        {
            first.Join();
            first.Tick(20);
            Assert.True(first.Close().Clean);
        }

        var open = MemoryWorldStore.Open(folder, SaveCompatibility.Of(Shipped));
        using var link = new LoopbackLink();
        var clock = new ManualTime();
        using var server = new ServerHost(new ServerConfig { Clock = clock, World = new WorldPersistence(open.Store!, open.Snapshot!, open.Generation) }, Shipped, link.Server);
        using var client = new ClientSession(link.Client, Shipped, "Host", clock: clock);
        long callback = 0;
        void Step()
        {
            clock.Advance(ManualTime.Tick);
            ((ITickHandler)server).Tick(callback);
            ((ITickHandler)client).Tick(callback++);
        }

        for (var n = 0; n < 2400 && client.AcquireSnapshot().HullCount < 3; n++)
        {
            Step();
            Thread.Sleep(1);
        }

        var hulls = (HullStore)typeof(ServerHost).GetField("_hulls", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(server)!;
        var snapshot = client.AcquireSnapshot();
        Assert.Equal(SessionState.Active, snapshot.State);
        Assert.Equal(3, snapshot.HullCount);
        Assert.Equal(3, hulls.Count);
        Assert.True(snapshot.OwnHull >= 0);
        Assert.True(server.Status.Tick > Far + 20);
        for (var n = 0; n < 120; n++) Step();
        Assert.InRange(client.AcquireSnapshot().ServerTick - server.Status.Tick, -8, 8);

        // The controls reach the hull at intervals in the billions as they do at zero.
        client.Input.Submit(new PlayerControls { Roll = .5f });
        for (var n = 0; n < 90; n++) Step();
        var own = (int)(client.AcquireSnapshot().Hulls[client.AcquireSnapshot().OwnHull].Id & 0xFFFF);
        Assert.True(hulls.State(own).AngularVelocity.LengthSquared() > 0.01f);
        Assert.Null(server.Fault);
        Assert.Null(client.Fault);
    }

    [Fact]
    public void ASlowSaveDelaysTheNextOneAndNeverSeesWhatHappenedAfterItsCapture()
    {
        var folder = SaveRig.NewWorld(Shipped);
        using var rig = new SaveRig(folder, Shipped);
        rig.Join();
        rig.Tick(10);
        Assert.False(rig.Server.Save.State == SaveState.Off);

        folder.Gate = new ManualResetEventSlim(false);
        rig.Autosave(wait: false);
        Assert.True(folder.Entered.Wait(5000, TestContext.Current.CancellationToken));
        var captured = rig.WorldTick;

        // The worker holds the buffer. The world plays on, grows, and five more saves fall due: none is made.
        var sector = SaveRig.RockySector(rig.Rocks);
        for (var i = 0; i < 5; i++)
        {
            rig.Clock.AdvanceSeconds(31);
            rig.Tick();
        }

        Assert.True(rig.Rocks.Set(new RockDelta { Rock = new RockRef(sector, 1), Velocity = Vector3.UnitX, StartTick = rig.WorldTick - 1 }));
        rig.Tick(3);
        Assert.Equal(SaveState.Writing, rig.Server.Save.State);
        Assert.Equal(1, folder.Attempts);
        Assert.True(rig.Server.Save.Dirty);

        folder.Gate.Set();
        rig.WaitIdle();
        var saved = folder.Decode();
        Assert.Equal(captured, saved.NextWorldTick);
        Assert.Equal(0, saved.RockCount);
        Assert.Equal(captured, rig.Server.Save.SavedTick);

        // The save that completed was of an older state: the world is still not saved.
        Assert.True(rig.Server.Save.Dirty);
        rig.Autosave();
        saved = folder.Decode();
        Assert.Equal(rig.WorldTick, saved.NextWorldTick);
        Assert.Equal(1, saved.RockCount);
        Assert.False(rig.Server.Save.Dirty);
        Assert.Equal(2, folder.Attempts);
    }

    [Fact]
    public void AStoreThatThrowsHasNotSaidThatNothingChangedAndIsNotWrittenToAgain()
    {
        var folder = SaveRig.NewWorld(Shipped);
        using var rig = new SaveRig(folder, Shipped);
        rig.Join();
        rig.Tick(5);
        folder.FailCommits = 1;
        folder.Throw = true;

        // "Failed" is a store's word that the world is as it was. An exception is not that word.
        rig.Autosave();
        var status = rig.Server.Save;
        Assert.Equal((SaveState.Halted, "the disk is full", true), (status.State, status.Error, status.Dirty));
        rig.Autosave();
        rig.Autosave();
        Assert.Equal(1, folder.Attempts);

        // Not even the final save is tried on top of a state nobody can name, and the close says which it was.
        var close = rig.Close();
        Assert.Equal(FinalCommitOutcome.Unknown, close.FinalCommit);
        Assert.False(close.Clean);
        Assert.Equal("the disk is full", close.Error);
        Assert.Equal(1, folder.Attempts);
        Assert.False(folder.Clean);
    }

    [Fact]
    public void AFailedSaveKeepsTheLastOneAndIsMadeAgainAlsoWhileNobodyPlays()
    {
        var folder = SaveRig.NewWorld(Shipped);
        using var rig = new SaveRig(folder, Shipped);
        rig.Join();
        rig.Tick(5);
        var bytes = folder.Bytes;
        var generation = folder.Generation;
        folder.FailCommits = 2;

        rig.Autosave();
        var status = rig.Server.Save;
        Assert.Equal((1, "the disk is full", true, generation), (status.FailedAttempts, status.Error, status.Dirty, status.Generation));
        Assert.Same(bytes, folder.Bytes);

        // Nobody plays: the world stands still, and the save is still tried again, after the shorter wait.
        rig.Leave();
        var paused = rig.WorldTick;
        rig.Tick(10);
        Assert.Equal(1, folder.Attempts);
        rig.Clock.AdvanceSeconds(6);
        rig.Tick();
        rig.WaitIdle();
        Assert.Equal(2, rig.Server.Save.FailedAttempts);
        rig.Clock.AdvanceSeconds(6);
        rig.Tick();
        rig.WaitIdle();

        status = rig.Server.Save;
        Assert.Equal((0, null, false, generation + 1), (status.FailedAttempts, status.Error, status.Dirty, status.Generation));
        Assert.Equal(paused, rig.WorldTick);
        Assert.Equal(paused, folder.Decode().NextWorldTick);
        Assert.Equal(paused, status.SavedTick);

        // Saved and unchanged: nothing more is written, however long nobody plays.
        rig.Clock.AdvanceSeconds(600);
        rig.Tick(5);
        Assert.Equal(3, folder.Attempts);
    }

    [Fact]
    public void AWorldRefusesToGrowPastWhatItsSaveHolds()
    {
        var folder = SaveRig.NewWorld(Shipped);
        var limits = new SaveLimits { MaxOwners = 1, MaxHulls = 4, MaxRocks = 2, MaxMovedSectors = 1 };
        using (var rig = new SaveRig(folder, Shipped, limits: limits))
        {
            rig.Join();
            rig.Tick(2);
            Assert.Equal(3, rig.Hulls.Count);

            // No room for a drone beside the fleet: the pilot stays in the hull, and it is counted.
            rig.Link.Control(0);
            rig.Tick();
            Assert.Equal(3, rig.Hulls.Count);
            Assert.Equal(1, rig.Server.Save.RefusedGrowth);

            // A pilot whose hull is destroyed still gets out: that drone's room was kept back.
            rig.WithConnection("LoseHull");
            Assert.Equal(3, rig.Hulls.Count);

            // Four asteroids are hit in one interval: two fit, the third is one too many and the fourth is in
            // a second sector. The two that fit move; clients hear of those two and of no other.
            var sector = SaveRig.RockySector(rig.Rocks);
            var impulses = rig.Get<RockImpulses>("_impulses");
            for (var i = 0; i < 3; i++)
                impulses.Items[i] = new RockDelta { Rock = new RockRef(sector, i), Velocity = Vector3.UnitY, StartTick = rig.WorldTick };
            impulses.Items[3] = new RockDelta { Rock = new RockRef(sector.Offset(1, 0, 0), 0), Velocity = Vector3.UnitY, StartTick = rig.WorldTick };
            impulses.Count = 4;
            rig.Tick();
            Assert.Equal(2, rig.Rocks.Deltas.Length);
            Assert.Equal(1, rig.Rocks.MovedSectors);
            Assert.Equal(3, rig.Server.Save.RefusedGrowth);
            Assert.Equal(0, rig.Link.RockMessages); // No sensor covers these remote collisions.
            rig.Rocks.Displacement(new RockRef(sector, 2), rig.WorldTick + 600, out var offset, out _);
            Assert.Equal(Vector3.Zero, offset);

            // One that has moved before can always move again.
            impulses.Items[0] = new RockDelta { Rock = new RockRef(sector, 1), Velocity = Vector3.UnitZ, StartTick = rig.WorldTick };
            impulses.Count = 1;
            rig.Tick();
            Assert.Equal(2, rig.Rocks.Deltas.Length);
            Assert.Equal(Vector3.UnitZ, rig.Rocks.Deltas[1].Velocity);
            Assert.Equal(3, rig.Server.Save.RefusedGrowth);

            // What the world holds is what its save holds: the close succeeds and loses nothing.
            Assert.True(rig.Close().Clean);
        }

        var saved = folder.Decode();
        Assert.Equal((3, 2), (saved.HullCount, saved.RockCount));
        using var again = new SaveRig(folder, Shipped, limits: limits);
        Assert.Equal(2, again.Rocks.Deltas.Length);
    }

    [Fact]
    public void ClosingWaitsForABusySaveThenSavesTheFrozenWorldOnce()
    {
        var folder = SaveRig.NewWorld(Shipped);
        using var rig = new SaveRig(folder, Shipped);
        rig.Join();
        rig.Tick(10);
        folder.Gate = new ManualResetEventSlim(false);
        rig.Autosave(wait: false);
        Assert.True(folder.Entered.Wait(5000, TestContext.Current.CancellationToken));
        rig.Tick(5);
        var frozen = rig.WorldTick;

        // Asked three times, from other threads as well: it is one close.
        rig.Server.BeginClose();
        Parallel.Invoke(rig.Server.BeginClose, rig.Server.BeginClose);
        rig.Link.HangUp();
        rig.Tick(50);
        Assert.Equal(ClosePhase.Freezing, rig.Server.Close.Phase);
        Assert.Equal(FinalCommitOutcome.Pending, rig.Server.Close.FinalCommit);

        // Frozen: no interval is simulated, and the client that hung up is not even heard.
        Assert.Equal(frozen, rig.WorldTick);
        Assert.Equal(1, rig.Server.Status.PlayerCount);
        Assert.Equal(1, folder.Attempts);

        folder.Gate.Set();
        var close = rig.Close();
        Assert.Equal((FinalCommitOutcome.Committed, CheckpointOutcome.Completed, BackupOutcome.Completed), (close.FinalCommit, close.Checkpoint, close.Backup));
        Assert.False(close.Degraded);
        Assert.Equal(2, folder.Commits);
        Assert.Equal(1, folder.FinalCommits);
        Assert.Equal(frozen, folder.Decode().NextWorldTick);
        Assert.Equal(3, folder.Decode().HullCount);
        Assert.True(folder.Clean);

        // Over is over: more callbacks and more requests change nothing and save nothing.
        rig.Server.BeginClose();
        rig.Tick(20);
        Assert.Equal(2, folder.Commits);
        Assert.Equal(frozen, rig.WorldTick);
        Assert.Equal(0, rig.Server.Status.PlayerCount);
    }

    [Fact]
    public void AFinalSaveThatKeepsFailingEndsTheCloseWithAFailureAndNoCleanMark()
    {
        var folder = SaveRig.NewWorld(Shipped);
        using var rig = new SaveRig(folder, Shipped);
        rig.Join();
        rig.Tick(10);
        rig.Autosave();
        var bytes = folder.Bytes;
        folder.FailCommits = 1000;

        var close = rig.Close();
        Assert.Equal(FinalCommitOutcome.Failed, close.FinalCommit);
        Assert.Equal("the disk is full", close.Error);
        Assert.False(close.Clean);

        // Tried a bounded number of times, then given up: three for the final save, after the one autosave.
        Assert.Equal(4, folder.Attempts);
        Assert.False(folder.Clean);
        Assert.Same(bytes, folder.Bytes);
        rig.Dispose();
        Assert.False(folder.Leased);
    }

    [Fact]
    public void AStoreWrittenBySomethingElseStopsSavingForGood()
    {
        var folder = SaveRig.NewWorld(Shipped);
        using var rig = new SaveRig(folder, Shipped);
        rig.Join();
        rig.Tick(5);
        lock (folder.Lock)
            folder.Generation += 3;

        rig.Autosave();
        Assert.Equal(SaveState.Halted, rig.Server.Save.State);
        Assert.NotNull(rig.Server.Save.Error);
        rig.Autosave();
        rig.Autosave();
        Assert.Equal(1, folder.Attempts);

        // The world plays on unsaved, and its close says so without touching the store again.
        rig.Tick(10);
        Assert.Equal(FinalCommitOutcome.Failed, rig.Close().FinalCommit);
        Assert.Equal(1, folder.Attempts);
    }

    [Fact]
    public void RecoveryCopiesFollowTheirOwnScheduleAndTheirFailureDoesNotUndoASave()
    {
        var folder = SaveRig.NewWorld(Shipped);
        using (var rig = new SaveRig(folder, Shipped))
        {
            rig.Join();
            rig.Autosave();
            Assert.Equal([BackupReason.Initial], folder.Backups);
            rig.Tick(5);
            rig.Autosave();
            Assert.Single(folder.Backups);
            rig.Tick(5);
            rig.Clock.AdvanceSeconds(600);
            rig.Autosave();
            Assert.Equal([BackupReason.Initial, BackupReason.Interval], folder.Backups);
            Assert.Equal((BackupOutcome.Completed, folder.Generation), (rig.Server.Save.Backup, rig.Server.Save.BackupGeneration));
            Assert.True(rig.Close().Clean);
            Assert.Equal(BackupReason.Final, folder.Backups[^1]);
        }

        // The copy fails and the store cannot be made portable: the save itself stands, and the close says both.
        folder.FailBackups = true;
        folder.Checkpoint = CheckpointOutcome.Failed;
        using (var rig = new SaveRig(folder, Shipped))
        {
            rig.Join();
            rig.Tick(5);
            var close = rig.Close();
            Assert.Equal((FinalCommitOutcome.Committed, CheckpointOutcome.Failed, BackupOutcome.Failed), (close.FinalCommit, close.Checkpoint, close.Backup));
            Assert.True(close.Clean);
            Assert.True(close.Degraded);
            Assert.Equal("no room for a recovery copy", close.BackupError);
            Assert.Equal("checkpoint could not complete", close.CheckpointError);
            Assert.Equal("checkpoint could not complete; no room for a recovery copy", close.Error);
        }

        Assert.True(folder.Clean);
    }

    [Fact]
    public void DisposingWithoutClosingSavesNothingAndIsNeverACleanClose()
    {
        var folder = SaveRig.NewWorld(Shipped);
        long saved;
        using (var rig = new SaveRig(folder, Shipped))
        {
            Assert.False(rig.WasClean);
            rig.Join();
            rig.Tick(10);
            Assert.True(rig.Close().Clean);
        }

        using (var rig = new SaveRig(folder, Shipped))
        {
            // The world was closed cleanly, and opening it made it a running world again.
            Assert.True(rig.WasClean);
            Assert.False(folder.Clean);
            rig.Join();
            rig.Tick(10);
            rig.Autosave();
            saved = rig.WorldTick;
            rig.Tick(50);
            rig.Dispose();
            Assert.Equal(FinalCommitOutcome.NotRequested, rig.Server.Close.FinalCommit);
            Assert.True(rig.Server.Close.Completed);
            Assert.False(rig.Server.Close.Clean);
        }

        Assert.False(folder.Clean);
        Assert.False(folder.Leased);
        Assert.Equal(0, folder.History.Count(entry => entry.Generation == folder.Generation && entry.Final));
        using var again = new SaveRig(folder, Shipped);
        Assert.False(again.WasClean);
        Assert.Equal(saved, again.WorldTick);
        Assert.Equal(3, again.Hulls.Count);
    }

    [Fact]
    public void AStoreThatNeverReturnsDoesNotHoldDisposeForever()
    {
        var folder = SaveRig.NewWorld(Shipped);
        var rig = new SaveRig(folder, Shipped, stopTimeoutMilliseconds: 200);
        rig.Join();
        rig.Tick(5);
        folder.Gate = new ManualResetEventSlim(false);
        rig.Autosave(wait: false);
        Assert.True(folder.Entered.Wait(5000, TestContext.Current.CancellationToken));
        rig.Server.BeginClose();
        rig.Tick(3);

        var watch = Stopwatch.StartNew();
        rig.Dispose();
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5));
        Assert.Equal(FinalCommitOutcome.Abandoned, rig.Server.Close.FinalCommit);
        Assert.False(rig.Server.Close.Clean);

        // The worker still owns the store; it lets go by itself once the store returns.
        Assert.True(folder.Leased);
        folder.Gate.Set();
        TestNet.WaitUntil(() => !folder.Leased, "the abandoned worker to release the store");
        Assert.False(folder.Clean);
    }

    [Fact]
    public void ATickThatThrowsIsNeverSavedAsTheFinalState()
    {
        var folder = SaveRig.NewWorld(Shipped);
        var link = new OwnerLink(Shipped);
        var open = MemoryWorldStore.Open(folder, SaveCompatibility.Of(Shipped));
        var server = new ServerHost(new ServerConfig { World = new WorldPersistence(open.Store!, open.Snapshot!, open.Generation) { AutosaveSeconds = 1 } }, Shipped, link);
        try
        {
            // A save is in the store, complete, when the tick fails.
            folder.Gate = new ManualResetEventSlim(false);
            link.Join();
            server.Start();
            Assert.True(folder.Entered.Wait(15000, TestContext.Current.CancellationToken));
            link.Fault = true;
            TestNet.WaitUntil(() => !server.IsRunning, "the loop to stop on the fault");
            Assert.NotNull(server.Fault);

            var close = server.Close;
            Assert.True(close.Completed);
            Assert.Equal(FinalCommitOutcome.SkippedAfterFault, close.FinalCommit);
            server.BeginClose();
            Assert.Equal(FinalCommitOutcome.SkippedAfterFault, server.Close.FinalCommit);

            // The capture that was whole before the fault is written, as an ordinary save and nothing more.
            folder.Gate.Set();
        }
        finally
        {
            folder.Gate?.Set();
            server.Dispose();
        }

        Assert.Equal(1, folder.Commits);
        Assert.Equal(0, folder.FinalCommits);
        Assert.False(folder.Clean);
        Assert.False(folder.Leased);
        Assert.Equal(FinalCommitOutcome.SkippedAfterFault, server.Close.FinalCommit);
        Assert.Equal(3, folder.Decode().HullCount);
    }

    [Fact]
    public void AFaultInAServerTickedByHandAlsoForbidsTheFinalSave()
    {
        var folder = SaveRig.NewWorld(Shipped);
        using var rig = new SaveRig(folder, Shipped);
        rig.Join();
        rig.Tick(5);
        rig.Link.Fault = true;
        Assert.Throws<InvalidOperationException>(() => rig.Tick());
        rig.Link.Fault = false;

        rig.Server.BeginClose();
        rig.Tick(5);
        Assert.Equal(FinalCommitOutcome.SkippedAfterFault, rig.Server.Close.FinalCommit);
        Assert.Equal(0, folder.Commits);
    }

    [Fact]
    public void ALocalSessionClosesItsWorldBeforeItsClientLeaves()
    {
        var folder = SaveRig.NewWorld(Shipped);
        var open = MemoryWorldStore.Open(folder, SaveCompatibility.Of(Shipped));
        var session = GameSession.StartLocal(new ServerConfig { World = new WorldPersistence(open.Store!, open.Snapshot!, open.Generation) }, Shipped, "Host");
        try
        {
            TestNet.WaitFor(session, snapshot => snapshot.HullCount == 3);
            Assert.True(session.IsPersistent);
            Assert.Equal(CloseStatus.Running, session.Close);
            Assert.NotEqual(SaveState.Off, session.Save.State);

            session.BeginClose();
            session.BeginClose();
            TestNet.WaitUntil(() => session.Close.Completed, "the session to close");
            Assert.True(session.Close.Clean);
            Assert.Null(session.Fault);
        }
        finally
        {
            session.Dispose();
        }

        Assert.True(folder.Clean);
        Assert.False(folder.Leased);
        Assert.Equal(1, folder.FinalCommits);
        var saved = folder.Decode();
        Assert.Equal(3, saved.HullCount);
        Assert.NotEqual(0ul, saved.Owners[0].Controlled);

        // The same session, disposed without a close: the fleet is still in the last save, which is not clean.
        open = MemoryWorldStore.Open(folder, SaveCompatibility.Of(Shipped));
        Assert.True(open.WasCleanClose);
        session = GameSession.StartLocal(new ServerConfig { World = new WorldPersistence(open.Store!, open.Snapshot!, open.Generation) }, Shipped, "Host");
        TestNet.WaitFor(session, snapshot => snapshot.HullCount == 3);
        session.Dispose();
        Assert.False(folder.Clean);
        Assert.False(folder.Leased);
        Assert.Equal(3, folder.Decode().HullCount);
    }

    [Fact]
    public void AGameWithoutAWorldStoreClosesAtOnceAndIsNotSaved()
    {
        using var rig = new ScriptedServer(Shipped);
        Assert.False(rig.Server.IsPersistent);
        Assert.Equal(SaveStatus.Off, rig.Server.Save);
        Assert.Equal(CloseStatus.Running, rig.Server.Close);
        var peer = rig.Join(0x0A000001);
        rig.Tick(5);
        Assert.Equal(1, rig.Net[peer].Welcomes);

        // It takes the scheduler's count as its interval, as it always did, and keeps simulating after the request.
        rig.Server.BeginClose();
        Assert.Equal(new CloseStatus(ClosePhase.Completed, FinalCommitOutcome.NotPersisted, CheckpointOutcome.NotAttempted, BackupOutcome.NotAttempted, null), rig.Server.Close);
        var before = rig.Server.Status.Tick;
        rig.Tick(5);
        Assert.Equal(before + 5, rig.Server.Status.Tick);

        // Its players' hulls still leave with them.
        rig.Net.HangUp(peer);
        rig.Tick();
        var hulls = (HullStore)typeof(ServerHost).GetField("_hulls", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(rig.Server)!;
        Assert.Equal(0, hulls.Count);
    }

    [Theory]
    [InlineData("generator")]
    [InlineData("save version")]
    [InlineData("setting")]
    [InlineData("points above intact")]
    [InlineData("points on a lost block")]
    [InlineData("points that are no number")]
    [InlineData("loss twice")]
    [InlineData("loss out of range")]
    [InlineData("total")]
    [InlineData("entity twice")]
    [InlineData("entity ahead of the counter")]
    [InlineData("owner flies nothing")]
    [InlineData("unknown owner")]
    [InlineData("owner ahead of the counter")]
    [InlineData("rock index")]
    [InlineData("rock twice")]
    [InlineData("rock from the future")]
    [InlineData("rock that is no number")]
    [InlineData("position")]
    [InlineData("orientation")]
    [InlineData("shield")]
    [InlineData("jump")]
    [InlineData("design number")]
    [InlineData("block count")]
    [InlineData("design bytes")]
    [InlineData("design hash")]
    [InlineData("pod role")]
    [InlineData("fleet role")]
    [InlineData("dent")]
    [InlineData("clock")]
    public void ASaveThatDoesNotHoldTogetherIsRefusedWholeAndItsStoreReleased(string what)
    {
        var folder = PopulatedWorld();
        var error = Assert.Throws<InvalidDataException>(() => new SaveRig(folder, Shipped, world =>
        {
            ref var damaged = ref world.Hulls[3];
            Assert.Equal(2, damaged.LossCount);
            switch (what)
            {
                case "generator":
                    world.Compatibility = world.Compatibility with { GeneratorVersion = Galaxy.GenerationVersion + 1 };
                    break;
                case "save version":
                    world.Compatibility = world.Compatibility with { SaveVersion = WorldSnapshotCodec.Version + 1 };
                    break;
                case "setting":
                    world.SettingValues[0] = 1e9f;
                    break;
                case "points above intact":
                    world.HitPoints[world.Hulls[1].BlockStart] *= 2f;
                    break;
                case "points on a lost block":
                    world.HitPoints[damaged.BlockStart + world.Losses[damaged.LossStart]] = 1f;
                    break;
                case "points that are no number":
                    world.HitPoints[world.Hulls[1].BlockStart] = float.NaN;
                    break;
                case "loss twice":
                    world.Losses[damaged.LossStart + 1] = world.Losses[damaged.LossStart];
                    break;
                case "loss out of range":
                    world.Losses[damaged.LossStart] = damaged.Blocks;
                    break;
                case "total":
                    damaged.HitTotal += 50;
                    break;
                case "entity twice":
                    world.Hulls[1].Entity = world.Hulls[0].Entity;
                    break;
                case "entity ahead of the counter":
                    world.NextEntityId = 2;
                    break;
                case "owner flies nothing":
                    world.Owners[0].Controlled = world.NextEntityId - 1 + 1000;
                    break;
                case "unknown owner":
                    world.Hulls[2].Owner = 77;
                    break;
                case "owner ahead of the counter":
                    world.NextOwnerId = 1;
                    break;
                case "rock index":
                    world.Rocks[0].Rock = world.Rocks[0].Rock with { Index = 1_000_000 };
                    break;
                case "rock twice":
                    world.Rocks[1].Rock = world.Rocks[0].Rock;
                    break;
                case "rock from the future":
                    world.Rocks[0].StartTick = world.NextWorldTick + 5;
                    break;
                case "rock that is no number":
                    world.Rocks[0].Velocity = new Vector3(float.PositiveInfinity, 0, 0);
                    break;
                case "position":
                    world.Hulls[0].State.Position = new Double3(double.NaN, 0, 0);
                    break;
                case "orientation":
                    world.Hulls[0].State.Orientation = new Quaternion(0, 0, 0, 3);
                    break;
                case "shield":
                    world.Hulls[0].State.Shield = 1e9f;
                    break;
                case "jump":
                    world.Hulls[4].State.JumpTunnelElapsed = double.NegativeInfinity;
                    break;
                case "design number":
                    world.Hulls[0].Design = 200;
                    break;
                case "block count":
                    world.Hulls[5].Blocks--;
                    break;
                case "design bytes":
                    world.Designs = (byte[][])world.Designs.Clone();
                    world.Designs[4] = (byte[])world.Designs[4].Clone();
                    world.Designs[4][^1] ^= 1;
                    break;
                case "design hash":
                    world.DesignHashes = (byte[][])world.DesignHashes.Clone();
                    world.DesignHashes[0] = new byte[32];
                    break;
                case "pod role":
                    world.PodDesign = 99;
                    break;
                case "fleet role":
                    world.StartingFleet = [1, 99];
                    break;
                case "dent":
                    damaged.Visual.Dents[0].Block = damaged.Blocks + 4;
                    break;
                case "clock":
                    world.NextWorldTick = -1;
                    break;
                default:
                    throw new ArgumentException(what);
            }

            return world;
        }));

        Assert.NotEqual("", error.Message);

        // The server took the store with the world it refused, and gave the lease back; the save is as it was.
        Assert.False(folder.Leased);
        Assert.Same(Populated.Value, folder.Bytes);
        Assert.Equal(7, folder.Generation);
    }

    [Fact]
    public void ASavedWorldIsNotReconfiguredWhenItIsLoaded()
    {
        var catalog = Shipped.SettingCatalog;
        var other = catalog.Defaults;
        var definition = catalog.Definitions[0];
        other.Set(definition, definition.Default == definition.Maximum ? definition.Minimum : definition.Maximum);
        Assert.NotEqual(catalog.Defaults, other);

        foreach (var config in new[] { new ServerConfig { GalaxySeed = 123 }, new ServerConfig { SpawnSector = new SectorCoord(2, 0, 0) }, new ServerConfig { Settings = other } })
        {
            var folder = PopulatedWorld();
            Assert.Throws<InvalidDataException>(() => new SaveRig(folder, Shipped, config: config));
            Assert.False(folder.Leased);
        }

        // Saying what the world already has is no wish to change it.
        using var same = new SaveRig(PopulatedWorld(), Shipped, config: new ServerConfig { Settings = catalog.Defaults, GalaxySeed = ServerConfig.DefaultSeed });
        Assert.Equal(6, same.Hulls.Count);

        // A world with its own seed, settings and spawn sector brings them back whatever the launch says.
        var world = new MemoryWorld();
        var initial = WorldSnapshot.NewWorld(Shipped, Guid.NewGuid(), "Elsewhere", DateTimeOffset.UnixEpoch, 99, other, new SectorCoord(3, 0, -2));
        MemoryWorldStore.Create(world, initial).Store!.Dispose();
        using var rig = new SaveRig(world, Shipped);
        Assert.Equal(other, rig.Server.Content.Settings);
        Assert.Equal(99ul, rig.Server.Galaxy.Seed);
        rig.Join();
        Assert.Equal(99ul, rig.Link.Welcome.GalaxySeed);
        Assert.Equal(new SectorCoord(3, 0, -2), rig.Server.Galaxy.SectorOf(rig.Hulls.State(0).Position));
        Assert.True(rig.Close().Clean);
        var saved = world.Decode();
        Assert.Equal((99ul, new SectorCoord(3, 0, -2), "Elsewhere"), (saved.Seed, saved.SpawnSector, saved.Name));
        Assert.Equal(initial.WorldId, saved.WorldId);
    }

    [Fact]
    public void TheCodecReadsBackWhatItWroteAndRefusesEverythingElse()
    {
        var bytes = Populated.Value;
        Assert.True(WorldSnapshotCodec.TryDecode(bytes, SaveLimits.Default, out var snapshot, out _));
        Assert.Equal(bytes.Length, WorldSnapshotCodec.EncodedSize(snapshot!));
        Assert.Equal(bytes, WorldSnapshotCodec.Encode(snapshot!));
        Assert.True(WorldSnapshotCodec.TryReadCompatibility(bytes, out var compatibility));
        Assert.Equal(SaveCompatibility.Of(Shipped), compatibility);
        Assert.Equal((1, 6, 2), (snapshot!.OwnerCount, snapshot.HullCount, snapshot.RockCount));
        Assert.True(bytes.Length <= SaveLimits.Default.MaxEncodedBytes);

        // Cut anywhere, or with anything after it, it is not a save.
        for (var length = 0; length < bytes.Length; length += Math.Max(1, bytes.Length / 257))
            Assert.False(WorldSnapshotCodec.TryDecode(bytes.AsSpan(0, length), SaveLimits.Default, out _, out _));
        Assert.False(WorldSnapshotCodec.TryDecode(bytes.AsSpan(0, bytes.Length - 1), SaveLimits.Default, out _, out _));
        Assert.False(WorldSnapshotCodec.TryDecode([.. bytes, 0], SaveLimits.Default, out _, out _));

        // Not this layout, not a save at all.
        var other = (byte[])bytes.Clone();
        other[4]++;
        Assert.False(WorldSnapshotCodec.TryDecode(other, SaveLimits.Default, out _, out var error));
        Assert.Contains("layout", error);
        other = (byte[])bytes.Clone();
        other[0] ^= 0x40;
        Assert.False(WorldSnapshotCodec.TryDecode(other, SaveLimits.Default, out _, out _));
        Assert.False(WorldSnapshotCodec.TryReadCompatibility(other, out _));

        // The counts are held to the limits before memory is taken for them: one hull, one block, one asteroid too many.
        Assert.False(WorldSnapshotCodec.TryDecode(bytes, new SaveLimits { MaxHulls = 5 }, out _, out error));
        Assert.Contains("more than a world may hold", error);
        Assert.False(WorldSnapshotCodec.TryDecode(bytes, new SaveLimits { MaxBlocks = snapshot.BlockCount - 1 }, out _, out _));
        Assert.False(WorldSnapshotCodec.TryDecode(bytes, new SaveLimits { MaxRocks = 1 }, out _, out _));
        Assert.False(WorldSnapshotCodec.TryDecode(bytes, new SaveLimits { MaxOwners = 0 }, out _, out _));
        Assert.True(WorldSnapshotCodec.TryDecode(bytes, new SaveLimits { MaxOwners = 1, MaxHulls = 6, MaxBlocks = snapshot.BlockCount, MaxRocks = 2 }, out _, out _));
    }

    [Fact]
    public void WarmTicksOfASavedWorldAllocateNothingWhileItIsSavedEverySecond()
    {
        var folder = SaveRig.NewWorld(Shipped);
        var open = MemoryWorldStore.Open(folder, SaveCompatibility.Of(Shipped));
        using var link = new LoopbackLink();
        var clock = new ManualTime();
        using var server = new ServerHost(new ServerConfig { Clock = clock, World = new WorldPersistence(open.Store!, open.Snapshot!, open.Generation) { AutosaveSeconds = 1 } }, Shipped, link.Server);
        using var client = new ClientSession(link.Client, Shipped, "Host", clock: clock);
        long callback = 0;
        void Step()
        {
            clock.Advance(ManualTime.Tick);
            ((ITickHandler)server).Tick(callback);
            ((ITickHandler)client).Tick(callback++);
        }

        for (var n = 0; n < 2400 && client.AcquireSnapshot().HullCount == 0; n++)
        {
            Step();
            Thread.Sleep(1);
        }

        Assert.True(client.AcquireSnapshot().HullCount > 0);
        for (var i = 0; i < 300; i++) Step();
        var commits = folder.Commits;
        var start = Stopwatch.GetTimestamp();
        var allocation = new AllocationProbe();
        var steps = 0;
        for (var i = 0; i < 600; i++)
        {
            // An attempt in doubt is measured again on the next tick: every pair is a warm one.
            while (allocation.Measuring())
            {
                Step();
                steps++;
            }

            // Give the worker its turn, so the tick finds the buffer free again.
            if (i % 60 == 59) Thread.Sleep(20);
        }

        var allocated = allocation.Bytes;
        var elapsed = Stopwatch.GetElapsedTime(start);
        Assert.Equal(0, allocated);
        Assert.True(folder.Commits - commits >= 5, $"only {folder.Commits - commits} saves in ten seconds of ticks");
        Console.WriteLine($"Warm saved world: {folder.Commits - commits} saves in {steps} server/client pairs, {allocated} managed bytes on the tick thread, {(elapsed.TotalMilliseconds - 200) / steps:F4} ms per pair.");
        Assert.Null(server.Fault);
    }

    /// <summary>A world of many hulls made by repeating a small one, each copy with its own names and its own place.</summary>
    internal static WorldSnapshot Repeated(WorldSnapshot world, int copies, int hulls = int.MaxValue)
    {
        var big = new WorldSnapshot(world.OwnerCount, world.HullCount * copies, world.BlockCount * copies, 0)
        {
            Compatibility = world.Compatibility,
            WorldId = world.WorldId,
            Name = world.Name,
            CreatedUnixMs = world.CreatedUnixMs,
            Seed = world.Seed,
            SpawnSector = world.SpawnSector,
            SettingIds = world.SettingIds,
            SettingCount = world.SettingCount,
            NextWorldTick = world.NextWorldTick,
            NextOwnerId = world.NextOwnerId,
            NextEntityId = 10_000_000,
            Designs = world.Designs,
            DesignHashes = world.DesignHashes,
            DesignCount = world.DesignCount,
            PodDesign = world.PodDesign,
            DroneDesign = world.DroneDesign,
            StartingFleet = world.StartingFleet,
            OwnerCount = world.OwnerCount,
        };
        world.SettingValues.CopyTo(big.SettingValues, 0);
        world.Owners.CopyTo(big.Owners, 0);
        for (var copy = 0; copy < copies; copy++)
        {
            for (var i = 0; i < world.HullCount && big.HullCount < hulls; i++)
            {
                var hull = world.Hulls[i];
                world.HitPoints.AsSpan(hull.BlockStart, hull.Blocks).CopyTo(big.HitPoints.AsSpan(big.BlockCount));
                world.Losses.AsSpan(hull.LossStart, hull.LossCount).CopyTo(big.Losses.AsSpan(big.LossCount));
                hull.Entity += (ulong)copy * 1000;
                hull.State.Position += new Vector3(copy % 32 * 900f, 0, copy / 32 * 900f);
                hull.BlockStart = big.BlockCount;
                hull.LossStart = big.LossCount;
                big.BlockCount += hull.Blocks;
                big.LossCount += hull.LossCount;
                big.Hulls[big.HullCount++] = hull;
            }
        }

        return big;
    }

    [Fact]
    public void CapturingAThousandHullsTakesNoMemoryOnTheTickAndLittleTime()
    {
        // The capture measured is a world's first, so a run in doubt is made again with a fresh world.
        var allocation = new AllocationProbe();
        while (allocation.Run())
            CaptureAThousandHulls(allocation);
        Assert.Equal(0, allocation.Bytes);
    }

    private static void CaptureAThousandHulls(AllocationProbe allocation)
    {
        var folder = SaveRig.NewWorld(Shipped);
        using (var small = new SaveRig(folder, Shipped))
        {
            small.Join();
            small.Tick(3);
            Assert.True(small.Close().Clean);
        }

        var start = Stopwatch.GetTimestamp();
        using var rig = new SaveRig(folder, Shipped, world => Repeated(world, 334));
        var restore = Stopwatch.GetElapsedTime(start);
        Assert.Equal(1002, rig.Hulls.Count);
        rig.Join();
        rig.Tick(2);
        rig.Leave();

        // Nobody plays, so the callback is the capture and little else: it can be timed and weighed.
        rig.Tick(3);
        rig.Clock.AdvanceSeconds(31);
        start = Stopwatch.GetTimestamp();
        allocation.Start();
        rig.Tick();
        allocation.Stop();
        var capture = Stopwatch.GetElapsedTime(start);
        rig.WaitIdle();
        Assert.Equal(1, folder.Attempts - 1);

        var saved = folder.Decode();
        Assert.Equal(1002, saved.HullCount);
        Assert.Equal(rig.Hulls.BlockTotal, saved.BlockCount);
        Assert.True(capture < TimeSpan.FromMilliseconds(TickLoop.SimulationDt * 1000), $"capturing took {capture.TotalMilliseconds:F3} ms");
        var limits = SaveLimits.Default;
        Console.WriteLine(
            $"Save of {saved.HullCount} hulls, {saved.BlockCount} blocks: capture {capture.TotalMilliseconds:F3} ms and {allocation.Bytes} managed bytes on the tick thread, "
                + $"encode {folder.LastEncodeMs:F3} ms on the worker, {folder.Bytes!.Length} bytes, restore {restore.TotalMilliseconds:F1} ms. "
                + $"Capture buffer at the limits ({limits.MaxHulls} hulls, {limits.MaxBlocks} blocks, {limits.MaxRocks} asteroids): {limits.MaxDecodedBytes - SaveLimits.MaxCatalogueBytes} bytes, taken once; "
                + $"largest encoded save {limits.MaxEncodedBytes} bytes."
        );
    }
}
