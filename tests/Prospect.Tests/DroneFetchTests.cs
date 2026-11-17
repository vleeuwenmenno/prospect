using System.Numerics;
using System.Reflection;
using Prospect.Engine.Net;
using Prospect.Game.Persistence;
using Prospect.Game.Protocol;
using Prospect.Game.Server;
using Prospect.Game.World;
using Xunit;

namespace Prospect.Tests;

/// <summary>Drones fetching free containers and handing them to the hull's tractor beam.</summary>
public sealed class DroneFetchTests
{
    private static readonly GameContent Shipped = FleetContent.Value;

    /// <summary>
    /// The shipped content with a pod that has a tractor beam on its core's top face, a one-cell rack in its view (it
    /// takes one M container), a drone bay whose hatch faces left, and with <paramref name="secondBay"/> a second one
    /// facing down.
    /// </summary>
    internal static GameContent Content(bool secondBay = false, bool tractor = true)
    {
        DesignBox Box(string type, int x, int y, int z, BlockFacing facing = BlockFacing.Front) =>
            new(new GridSize(x, y, z), new GridSize(1, 1, 1), Shipped.Blocks.Get(type), BlockShape.Box, facing);
        var boxes = new List<DesignBox>
        {
            Box("core", 0, 0, 0), Box("cargo", 2, 1, 0), Box("generator", 0, -1, 0), Box("battery", 0, -2, 0),
            Box("gyro", -1, 0, 0), Box("ionDrive", 0, 0, 1), Box("sensor", 0, 0, -1), Box("hull", 1, 0, 0),
            Box("hull", 2, 0, 0), Box("hull", 3, 1, 0), Box("droneBay", -1, 0, -1, BlockFacing.Left),
        };
        if (secondBay) boxes.Add(Box("droneBay", 0, -3, 0, BlockFacing.Down));
        DesignMount[] mounts = tractor ? [new DesignMount(0, BlockFacing.Up, 2, 2, Shipped.Blocks.Get("tractorBeam"))] : [];
        var pod = Shipped.Pod;
        var design = HullDesign.Of(new BoxDesign(pod.Name, 1, [.. boxes], Shipped.Blocks.Limits, "test", pod.Class, pod.Color, mounts), Shipped.Rules);
        var designs = Shipped.Designs.ToArray();
        designs[Shipped.PodDesign] = design;
        return Shipped.WithCatalogue(designs, Shipped.PodDesign, Shipped.DroneDesign, Shipped.StartingFleet, Shipped.WorkDroneDesign);
    }

    private static HullHandle Pod(SaveRig rig) { var own = rig.Link.LastSnapshot.OwnHull; return new((int)(own & 0xFFFF), (int)(own >> 16)); }

    private static FreeContainers Containers(SaveRig rig) => rig.Get<FreeContainers>("_containers");

    private static int Resource(GameContent content, string id)
    {
        for (var i = 0; i < content.Galaxy.Resources.Length; i++) if (content.Galaxy.Resources[i].Id == id) return i;
        throw new InvalidOperationException(id);
    }

    /// <summary>A full container of <paramref name="size"/> and <paramref name="resource"/> at <paramref name="offset"/> from the pod, at rest with it.</summary>
    private static uint Place(SaveRig rig, GameContent content, Vector3 offset, int size, string resource = "iron", uint claim = 0, long ready = 0)
    {
        var pod = rig.Hulls.State(Pod(rig).Index);
        var r = Resource(content, resource);
        var units = content.Cargo.Sizes[size].Units;
        var unit = (double)content.Cargo.UnitSize;
        Assert.True(Containers(rig).Add(content, new FreeContainer
        {
            Position = pod.Position + offset,
            Velocity = pod.Velocity,
            Orientation = Quaternion.Identity,
            Size = size,
            Resource = r,
            Amount = units * unit * unit * unit * content.Cargo.Density(content.Galaxy.Resources[r].Id),
            Hull = content.Cargo.Sizes[size].Hull,
            ClaimOwner = claim,
            PickupReadyTick = ready,
        }, out var id));
        return id;
    }

    private static void Fetch(SaveRig rig, uint id) =>
        rig.Link.Drones(new DroneCommandMessage(rig.Link.LastSnapshot.OwnHull, DroneCommand.Fetch, 0, false, default, id));

    private static List<int> Drones(SaveRig rig)
    {
        var found = new List<int>();
        for (var i = 0; i < rig.Hulls.Slots; i++)
            if (rig.Hulls.IsAliveAt(i) && rig.Hulls.Info(i).WorkDrone) found.Add(i);
        return found;
    }

    private static void TickUntil(SaveRig rig, Func<bool> done, float seconds, string what)
    {
        for (var i = 0; i < seconds * 60 && !done(); i++)
            rig.Tick();
        Assert.True(done(), what);
    }

    private static int Held(SaveRig rig) => rig.Hulls.BlockStates.Cargo(rig.Hulls.Info(Pod(rig).Index).BlockState).Length;

    private static SaveRig Rig(GameContent content)
    {
        var rig = new SaveRig(SaveRig.NewWorld(content), content);
        rig.Join();
        rig.Tick();
        return rig;
    }

    private static void Reply(SaveRig rig, DroneReply expected)
    {
        rig.Tick();
        Assert.Equal(expected, rig.Link.Bays.Reply);
    }

    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Fact]
    public void ADroneFetchesALightContainerFromBeyondTractorRangeAndTheBeamStowsIt()
    {
        var content = Content();
        using var rig = Rig(content);
        var id = Place(rig, content, new Vector3(-150, 20, 40), size: 2);
        rig.Tick();
        Fetch(rig, id);
        Reply(rig, DroneReply.Fetching);
        Assert.Equal(1, rig.Link.Bays.ReplyCount);
        Assert.Equal(1, rig.Link.Bays.Fetching);

        TickUntil(rig, () => Drones(rig).Count == 1, 3, "one drone launched");
        // Close enough for the beam, but the drones have it: no tractor takes it on the way.
        TickUntil(rig, () => Containers(rig).Items[0].Position.RelativeTo(rig.Hulls.State(Pod(rig).Index).Position).Length() < 30, 90, "the container brought close");
        Assert.Equal(0u, Containers(rig).Items[0].TractorHull);
        TickUntil(rig, () => Containers(rig).Items.Length == 0, 60, "the container stowed");
        Assert.Equal(1, Held(rig));
        Assert.Equal(DroneReply.Delivered, rig.Link.Bays.Reply);
        TickUntil(rig, () => Drones(rig).Count == 0, 60, "the drone docked");
        rig.Tick();
        Assert.Equal(0, rig.Link.Bays.Fetching);
        Assert.Equal(4, rig.Link.Bays.Bays[0].Stocked);
    }

    [Fact]
    public void AHeavyContainerTakesTwoDronesWhoseCarryAdds()
    {
        var content = Content();
        using var rig = Rig(content);
        // A full M container of iron weighs about 7.9 t; a Common bay's drone carries 5 t.
        var id = Place(rig, content, new Vector3(-80, 10, 0), size: 3);
        var mass = TractorSimulation.Mass(content.Cargo, Containers(rig).Items[0]);
        Assert.Equal(2, DroneFetch.Needed(mass, content.Blocks.Get("droneBay").Bay!.CarryKg));
        Fetch(rig, id);
        Reply(rig, DroneReply.Fetching);
        Assert.Equal(2, rig.Link.Bays.ReplyCount);
        TickUntil(rig, () => Drones(rig).Count == 2, 3, "two drones launched");

        // One drone alone never moves it: the container waits until both hold on.
        var orders = rig.Get<TaskOrder[]>("_orders");
        var moved = false;
        TickUntil(rig, () =>
        {
            var holding = 0;
            for (var i = 0; i < 2; i++) if (orders[i].Task == OrderTask.Fetch && orders[i].Phase == 1) holding++;
            var item = Containers(rig).Items[0];
            if (holding < 2 && (item.Velocity - rig.Hulls.State(Pod(rig).Index).Velocity).Length() > 0.05f) moved = true;
            return holding == 2;
        }, 60, "both drones holding on");
        Assert.False(moved);
        TickUntil(rig, () => Containers(rig).Items.Length == 0, 120, "the container stowed");
        Assert.Equal(1, Held(rig));
        TickUntil(rig, () => Drones(rig).Count == 0, 60, "both drones docked");
    }

    [Fact]
    public void ClaimsTakenContainersAndRangeAreRefused()
    {
        var content = Content();
        using var rig = Rig(content);
        var owner = rig.Hulls.Info(Pod(rig).Index).Owner;
        var others = Place(rig, content, new Vector3(-60, 0, 0), size: 2, claim: owner + 77);
        Fetch(rig, others);
        Reply(rig, DroneReply.Claimed);
        rig.Tick(20);

        // The player's own claim is no obstacle, but a container already being fetched is taken.
        var mine = Place(rig, content, new Vector3(-60, 10, 0), size: 2, claim: owner);
        Fetch(rig, mine);
        Reply(rig, DroneReply.Fetching);
        rig.Tick(20);
        Fetch(rig, mine);
        Reply(rig, DroneReply.Taken);
        rig.Tick(20);

        var far = Place(rig, content, new Vector3(content.Drones.FetchRangeMetres + 50, 0, 0), size: 2);
        Fetch(rig, far);
        Reply(rig, DroneReply.TooFar);
        rig.Tick(20);
        Fetch(rig, 9999);
        Reply(rig, DroneReply.NotFound);
        rig.Tick(20);

        // A container whose pickup delay runs is fetched, but not lifted before the delay is over.
        var waiting = Place(rig, content, new Vector3(-40, -10, 0), size: 0, ready: rig.WorldTick + 60 * 40);
        Fetch(rig, waiting);
        Reply(rig, DroneReply.Fetching);
        rig.Tick(60 * 30);
        var item = Containers(rig).Items[Containers(rig).Find(waiting)];
        Assert.InRange((item.Velocity - rig.Hulls.State(Pod(rig).Index).Velocity).Length(), 0, 0.01f);
    }

    [Fact]
    public void ARefusalSaysWhyWhenNoBeamRackOrDronesCanTakeIt()
    {
        var bare = Content(tractor: false);
        using (var rig = Rig(bare))
        {
            Fetch(rig, Place(rig, bare, new Vector3(-60, 0, 0), size: 2));
            Reply(rig, DroneReply.NoTractor);
        }

        var content = Content();
        using var big = Rig(content);
        // An XXL container of iron (about 63 t) is more than eight Common drones lift.
        Fetch(big, Place(big, content, new Vector3(-60, 0, 0), size: 6));
        Reply(big, DroneReply.TooHeavy);
        big.Tick(20);
        // An L container does not fit the one-cell rack.
        Fetch(big, Place(big, content, new Vector3(-60, 10, 0), size: 4, resource: "ice"));
        Reply(big, DroneReply.NoRoom);
        big.Tick(20);
        // Two M fetches take all four drones; a third finds none.
        Fetch(big, Place(big, content, new Vector3(-60, 20, 0), size: 3));
        Reply(big, DroneReply.Fetching);
        TickUntil(big, () => Drones(big).Count == 2, 3, "two drones out");
        big.Tick(20);
        Fetch(big, Place(big, content, new Vector3(-60, -20, 0), size: 3));
        Reply(big, DroneReply.Fetching);
        TickUntil(big, () => Drones(big).Count == 4, 3, "four drones out");
        big.Tick(20);
        Fetch(big, Place(big, content, new Vector3(-60, 30, 0), size: 2));
        Reply(big, DroneReply.TooFew);
    }

    [Fact]
    public void ALostCarrierDropsTheContainerAndTheOthersComeHome()
    {
        var content = Content();
        using var rig = Rig(content);
        var id = Place(rig, content, new Vector3(-80, 10, 0), size: 3);
        Fetch(rig, id);
        Reply(rig, DroneReply.Fetching);
        var pod = Pod(rig);
        TickUntil(rig, () => (Containers(rig).Items[0].Velocity - rig.Hulls.State(pod.Index).Velocity).Length() > 1, 90, "the container carried");
        rig.Hulls.Destroy(rig.Hulls.HandleAt(Drones(rig)[0]));
        rig.Tick(2);
        Assert.Equal(DroneReply.Dropped, rig.Link.Bays.Reply);
        var item = Containers(rig).Items[0];
        Assert.Equal(0u, item.FetchHull);
        Assert.Equal(0u, item.TractorHull);
        var orders = rig.Get<TaskOrder[]>("_orders");
        Assert.Equal(OrderTask.Recall, orders[0].Task);
        TickUntil(rig, () => Drones(rig).Count == 0, 90, "the other drone docked");
        Assert.Single(Containers(rig).Items.ToArray());
    }

    [Fact]
    public void ALoadedWorldHasTheCarriedContainerFreeWhereItWas()
    {
        var store = new FreeContainers();
        FreeContainer[] items = [new FreeContainer { Id = 3, FetchHull = 5 | 1 << 16, FetchMount = 2, Orientation = Quaternion.Identity, Hull = 10 }];
        store.Restore(items, 4);
        Assert.Equal(0u, store.Items[0].FetchHull);
        Assert.Equal(0, store.Items[0].FetchMount);
    }

    [Fact]
    public void TheCommandAndTheBaysCarryTheFetchOnTheWire()
    {
        var buffer = new byte[256];
        var writer = new NetWriter(buffer);
        new DroneCommandMessage(7 | 1 << 16, DroneCommand.Fetch, 0, false, default, 42).Write(ref writer);
        Assert.Equal(DroneCommandMessage.MaxWireSize, writer.Written.Length);
        var reader = new NetReader(writer.Written[1..]);
        Assert.True(DroneCommandMessage.TryRead(ref reader, out var command));
        Assert.Equal(42u, command.Subject);
        Assert.Equal(DroneCommand.Fetch, command.Command);

        // A fetch names a container and nothing else; no other command names one.
        foreach (var bad in new[]
        {
            new DroneCommandMessage(7, DroneCommand.Fetch, 0, false, default, 0),
            new DroneCommandMessage(7, DroneCommand.Fetch, 2, false, default, 42),
            new DroneCommandMessage(7, DroneCommand.Recall, 0, false, default, 42),
        })
        {
            writer = new NetWriter(buffer);
            bad.Write(ref writer);
            reader = new NetReader(writer.Written[1..]);
            Assert.False(DroneCommandMessage.TryRead(ref reader, out _));
        }

        var view = new BayView { Hull = 9, Tick = 5, Reply = DroneReply.Delivered, ReplyCount = 2, Fetching = 3 };
        writer = new NetWriter(buffer);
        BayMessage.Write(ref writer, view);
        reader = new NetReader(writer.Written[1..]);
        Assert.True(BayMessage.TryRead(ref reader, out var copy));
        Assert.Equal(3, copy.Fetching);
        Assert.Equal(DroneReply.Delivered, copy.Reply);
    }

    [Fact]
    public void TheExecutorAndTheCarryTakeNoMemory()
    {
        var content = Content();
        using var rig = Rig(content);
        var id = Place(rig, content, new Vector3(-120, 10, 0), size: 3);
        Fetch(rig, id);
        rig.Tick(60);
        var tickDrones = typeof(ServerHost).GetMethod("TickDrones", Private)!.CreateDelegate<Action>(rig.Server);
        var tickCarries = typeof(ServerHost).GetMethod("TickCarries", Private)!.CreateDelegate<Action>(rig.Server);
        var sendBays = typeof(ServerHost).GetMethod("SendBays", Private)!.CreateDelegate<Action>(rig.Server);
        var probe = new AllocationProbe();
        var carried = false;
        for (var i = 0; i < 60 * 60 && Containers(rig).Items.Length > 0; i++)
        {
            while (probe.Measuring()) { tickDrones(); tickCarries(); sendBays(); }
            rig.Tick();
            carried |= Containers(rig).Items.Length > 0 && Containers(rig).Items[0].FetchHull != 0
                && (Containers(rig).Items[0].Velocity - rig.Hulls.State(Pod(rig).Index).Velocity).Length() > 1;
        }
        Assert.True(carried);
        Assert.Empty(Containers(rig).Items.ToArray());
        Assert.Equal(0, probe.Bytes);
    }

    private static void AutoPickup(SaveRig rig, bool on) =>
        rig.Link.DroneSettings(new DroneSettingsMessage(rig.Link.LastSnapshot.OwnHull, on));

    private static bool Fetching(SaveRig rig, uint id)
    {
        var index = Containers(rig).Find(id);
        return index >= 0 && Containers(rig).Items[index].FetchHull != 0;
    }

    [Fact]
    public void AutoPickupFetchesOnItsOwnAndStopsWhenTurnedOff()
    {
        var content = Content();
        using var rig = Rig(content);
        var first = Place(rig, content, new Vector3(-150, 20, 40), size: 2);
        // Off by default: nothing goes out for a loose container.
        rig.Tick(120);
        Assert.False(rig.Link.Bays.AutoPickup);
        Assert.False(Fetching(rig, first));
        Assert.Empty(Drones(rig));

        AutoPickup(rig, true);
        rig.Tick();
        Assert.True(rig.Link.Bays.AutoPickup);
        TickUntil(rig, () => Fetching(rig, first), 3, "auto pickup started a fetch");
        Assert.Equal(DroneReply.Fetching, rig.Link.Bays.Reply);
        TickUntil(rig, () => Containers(rig).Items.Length == 0, 150, "the container stowed by the beam");
        Assert.Equal(1, Held(rig));
        TickUntil(rig, () => Drones(rig).Count == 0, 60, "the drone docked");

        // Turned off, a new container is left where it lies.
        AutoPickup(rig, false);
        rig.Tick();
        Assert.False(rig.Link.Bays.AutoPickup);
        var second = Place(rig, content, new Vector3(-100, 10, 0), size: 2);
        rig.Tick(60 * 10);
        Assert.False(Fetching(rig, second));
        Assert.Empty(Drones(rig));
    }

    [Fact]
    public void AutoPickupRespectsClaimsTheDelayAndRange()
    {
        var content = Content();
        using var rig = Rig(content);
        var owner = rig.Hulls.Info(Pod(rig).Index).Owner;
        var others = Place(rig, content, new Vector3(-60, 0, 0), size: 2, claim: owner + 77);
        var far = Place(rig, content, new Vector3(content.Drones.FetchRangeMetres + 50, 0, 0), size: 2);
        var waiting = Place(rig, content, new Vector3(-40, -10, 0), size: 0, ready: rig.WorldTick + 60 * 20);
        AutoPickup(rig, true);
        rig.Tick(60 * 15);
        // Someone else's claim, the range and the pickup delay are all skipped silently: no drone, no answer.
        Assert.False(Fetching(rig, others));
        Assert.False(Fetching(rig, far));
        Assert.False(Fetching(rig, waiting));
        Assert.Empty(Drones(rig));
        Assert.Equal(DroneReply.None, rig.Link.Bays.Reply);

        // Once the delay is over the container is fetched, and the one claimed by another player never is.
        TickUntil(rig, () => Fetching(rig, waiting) || Containers(rig).Find(waiting) < 0, 15, "the delayed container fetched");
        Assert.False(Fetching(rig, others));
        Assert.False(Fetching(rig, far));

        // The owner's own claim is no obstacle.
        var mine = Place(rig, content, new Vector3(-50, 0, 30), size: 0, claim: owner);
        TickUntil(rig, () => Fetching(rig, mine) || Containers(rig).Find(mine) < 0, 60 * 3, "the owner's claimed container fetched");
    }

    [Fact]
    public void AutoPickupLeavesWhatTheTractorReachesAndTheDronesHaveAlone()
    {
        var content = Content();
        using var rig = Rig(content);
        AutoPickup(rig, true);
        // Within the beam's range and sight, the tractor takes it itself; no drone is sent for it.
        var near = Place(rig, content, new Vector3(0, 8, 0), size: 0);
        var seenDrone = false;
        var pulled = false;
        for (var i = 0; i < 60 * 8 && Containers(rig).Find(near) >= 0; i++)
        {
            rig.Tick();
            seenDrone |= Drones(rig).Count > 0;
            var at = Containers(rig).Find(near);
            pulled |= at >= 0 && Containers(rig).Items[at].TractorHull != 0;
        }
        Assert.False(seenDrone);
        Assert.True(pulled || Held(rig) == 1);

        // A container the drones are already fetching is not given to a second set: one fetch at a time per hull.
        var one = Place(rig, content, new Vector3(-150, 20, 40), size: 2);
        var two = Place(rig, content, new Vector3(-150, -20, 40), size: 2);
        TickUntil(rig, () => Fetching(rig, one) || Fetching(rig, two), 5, "a fetch started");
        rig.Tick(60);
        Assert.NotEqual(Fetching(rig, one), Fetching(rig, two));
    }

    [Fact]
    public void AutoPickupDoesNotNagAboutAContainerItWasRefusedFor()
    {
        var content = Content();
        using var rig = Rig(content);
        // The largest container is heavier than any drones or beam could take.
        var huge = Place(rig, content, new Vector3(-60, 0, 0), size: content.Cargo.Sizes.Length - 1);
        AutoPickup(rig, true);
        rig.Tick(30);
        Assert.Equal(DroneReply.TooHeavy, rig.Link.Bays.Reply);
        var answers = rig.Link.Bays.ReplySequence;
        Assert.False(Fetching(rig, huge));
        // Left alone for the retry time, then tried (and refused) again.
        rig.Tick((int)(content.Drones.AutoPickupRetrySeconds * 60) - 90);
        Assert.Equal(answers, rig.Link.Bays.ReplySequence);
        rig.Tick(180);
        Assert.NotEqual(answers, rig.Link.Bays.ReplySequence);
    }

    [Fact]
    public void AManualRecallHoldsAutoPickupOffForAWhile()
    {
        var content = Content();
        using var rig = Rig(content);
        var id = Place(rig, content, new Vector3(-150, 20, 40), size: 2);
        AutoPickup(rig, true);
        TickUntil(rig, () => Fetching(rig, id), 5, "auto pickup started a fetch");
        TickUntil(rig, () => Drones(rig).Count == 1, 5, "the drone is out");
        rig.Link.Drones(new DroneCommandMessage(rig.Link.LastSnapshot.OwnHull, DroneCommand.Recall, 0, false, default));
        rig.Tick(2);
        Assert.False(Fetching(rig, id));
        // The drone goes home and nothing sends it out again until the pause is over.
        TickUntil(rig, () => Drones(rig).Count == 0, 60, "the drone docked");
        rig.Tick(60 * (int)(content.Drones.AutoPickupPauseSeconds - 10));
        Assert.Empty(Drones(rig));
        Assert.False(Fetching(rig, id));
        TickUntil(rig, () => Fetching(rig, id), 30, "auto pickup went on after the pause");
    }

    [Fact]
    public void ADroneSettingIsCheckedAfterAdmissionAndRateLimited()
    {
        var content = Content();
        using var rig = Rig(content);
        var own = rig.Link.LastSnapshot.OwnHull;
        // A hull that is not the sender's, or does not exist, changes nothing.
        rig.Link.DroneSettings(new DroneSettingsMessage(own + 1, true));
        rig.Link.DroneSettings(new DroneSettingsMessage(0x00FF_FFFF, true));
        rig.Tick(2);
        Assert.False(rig.Link.Bays.AutoPickup);

        // Two settings in one rate window: the first holds, the second is dropped.
        rig.Tick(30);
        AutoPickup(rig, true);
        AutoPickup(rig, false);
        rig.Tick(2);
        Assert.True(rig.Link.Bays.AutoPickup);
        rig.Tick(30);
        AutoPickup(rig, false);
        rig.Tick(2);
        Assert.False(rig.Link.Bays.AutoPickup);
    }

    [Fact]
    public void TheSettingAndTheBaysCarryAutoPickupOnTheWire()
    {
        var buffer = new byte[256];
        var writer = new NetWriter(buffer);
        new DroneSettingsMessage(7 | 1 << 16, true).Write(ref writer);
        Assert.Equal(DroneSettingsMessage.MaxWireSize, writer.Written.Length);
        var reader = new NetReader(writer.Written[1..]);
        Assert.True(DroneSettingsMessage.TryRead(ref reader, out var message));
        Assert.Equal(new DroneSettingsMessage(7 | 1 << 16, true), message);

        // A hull of zero, a flag past one, a message cut short and one with anything after it are refused.
        foreach (var bytes in new[] { new byte[] { 0, 0, 0, 0, 1, 0 }, new byte[] { 7, 0, 0, 0, 2, 0 }, new byte[] { 7, 0, 0, 0, 1, 2 }, new byte[] { 7, 0, 0, 0, 1 }, new byte[] { 7, 0, 0, 0, 1, 0, 0 } })
        {
            reader = new NetReader(bytes);
            Assert.False(DroneSettingsMessage.TryRead(ref reader, out _));
        }

        var view = new BayView { Hull = 9, Tick = 5, AutoPickup = true };
        writer = new NetWriter(buffer);
        BayMessage.Write(ref writer, view);
        Assert.Equal(BayMessage.HeaderBytes, writer.Written.Length);
        reader = new NetReader(writer.Written[1..]);
        Assert.True(BayMessage.TryRead(ref reader, out var copy));
        Assert.True(copy.AutoPickup);
        var broken = writer.Written.ToArray();
        broken[1 + 4 + 8 + 4 + 3] = 2;
        reader = new NetReader(broken.AsSpan(1));
        Assert.False(BayMessage.TryRead(ref reader, out _));
        Assert.Equal(72u, ProtocolInfo.Version);
        Assert.Equal(43, (int)MessageId.DroneSettings);
    }

    [Fact]
    public void AutoPickupIsSavedWithTheHullAndLoadedBack()
    {
        var content = Content();
        var folder = SaveRig.NewWorld(content);
        using (var rig = new SaveRig(folder, content))
        {
            rig.Join();
            rig.Tick();
            AutoPickup(rig, true);
            rig.Tick(2);
            Assert.True(rig.Link.Bays.AutoPickup);
            Assert.True(rig.Close().Clean);
        }

        var saved = folder.Decode();
        Assert.Equal(23, WorldSnapshotCodec.Version);
        var flagged = 0;
        for (var i = 0; i < saved.HullCount; i++)
            if (saved.Hulls[i].AutoPickup) { flagged++; Assert.True(saved.Hulls[i].Bays > 0); }
        Assert.Equal(1, flagged);

        using var again = new SaveRig(folder, content);
        again.Join();
        again.Tick(2);
        Assert.True(again.Link.Bays.AutoPickup);
        again.Dispose();

        // A hull with no bay cannot have it on: such a save is refused.
        Assert.Throws<InvalidDataException>(() => new SaveRig(folder, content, edit: world =>
        {
            for (var i = 0; i < world.HullCount; i++)
                if (world.Hulls[i].Bays == 0) world.Hulls[i].AutoPickup = true;
            return world;
        }).Dispose());
    }

    [Fact]
    public void AnEditedHullKeepsAutoPickup()
    {
        var content = Content();
        using var rig = Rig(content);
        AutoPickup(rig, true);
        rig.Tick(2);
        var old = Pod(rig);
        var info = rig.Hulls.Info(old.Index);
        // CarryBays is what ReplaceHull calls after the new hull exists: the setting travels to the new hull's page.
        var next = (HullHandle)typeof(ServerHost).GetMethod("CreateHull", Private)!.Invoke(rig.Server, [info.Owner, info.Design, rig.Hulls.State(old.Index).Position, false, info.Entity])!;
        typeof(ServerHost).GetMethod("CarryBays", Private)!.Invoke(rig.Server, [old, next]);
        rig.Hulls.Destroy(old);
        var pages = rig.Get<Array>("_bayPages");
        var page = pages.GetValue(rig.Hulls.Info(next.Index).BayPage - 1)!;
        Assert.True((bool)page.GetType().GetField("AutoPickup")!.GetValue(page)!);
    }

    [Fact]
    public void AutoPickupTakesNoMemory()
    {
        var content = Content();
        using var rig = Rig(content);
        var owner = rig.Hulls.Info(Pod(rig).Index).Owner;
        // Containers the scan looks at and skips every tick: claimed by another, out of range, and in reach of the beam.
        for (var i = 0; i < 40; i++) Place(rig, content, new Vector3(-60 - i, 0, 0), size: 0, claim: owner + 77);
        Place(rig, content, new Vector3(content.Drones.FetchRangeMetres + 50, 0, 0), size: 2);
        AutoPickup(rig, true);
        rig.Tick(30);
        var tickDrones = typeof(ServerHost).GetMethod("TickDrones", Private)!.CreateDelegate<Action>(rig.Server);
        var probe = new AllocationProbe();
        for (var i = 0; i < 60; i++)
        {
            while (probe.Measuring()) { tickDrones(); }
            rig.Tick();
        }
        Assert.Equal(0, probe.Bytes);
    }
}
