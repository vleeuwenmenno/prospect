using System.Numerics;
using System.Reflection;
using Prospect.Engine.Net;
using Prospect.Game.Protocol;
using Prospect.Game.Server;
using Prospect.Game.World;
using Xunit;

namespace Prospect.Tests;

/// <summary>Drone bays, the drones they launch and the orders the server carries out for them.</summary>
public sealed class DroneBayTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly GameContent Shipped = GameContent.Load();

    /// <summary>
    /// The shipped content with the pod given drone bays: one under the sensor whose hatch faces down by default,
    /// more with <paramref name="bays"/>, and with <paramref name="blocked"/> a plate of hull right below that hatch.
    /// </summary>
    internal static GameContent Content(int bays = 1, bool blocked = false)
    {
        var pod = Shipped.Pod;
        var bay = Shipped.Blocks.Get("droneBay");
        var one = new GridSize(1, 1, 1);
        var places = new (GridSize At, BlockFacing Hatch)[]
        {
            (new GridSize(0, -1, -1), BlockFacing.Down),
            (new GridSize(0, -1, -2), BlockFacing.Down),
            (new GridSize(1, 0, -1), BlockFacing.Right),
            (new GridSize(-1, 0, -1), BlockFacing.Left),
        };
        var boxes = pod.Boxes.ToArray().ToList();
        for (var i = 0; i < bays; i++)
            boxes.Add(new DesignBox(places[i].At, one, bay, BlockShape.Box, places[i].Hatch));
        if (blocked)
            boxes.Add(new DesignBox(new GridSize(0, -2, -1), one, Shipped.Blocks.Get("hull")));
        var design = HullDesign.Of(new BoxDesign(pod.Name, pod.BlockSize, [.. boxes], Shipped.Blocks.Limits, "test", pod.Class, pod.Color, pod.Mounts.ToArray()), Shipped.Rules);
        var designs = Shipped.Designs.ToArray();
        designs[Shipped.PodDesign] = design;
        return Shipped.WithCatalogue(designs, Shipped.PodDesign, Shipped.DroneDesign, Shipped.StartingFleet, Shipped.WorkDroneDesign);
    }

    private static uint Own(SaveRig rig) => rig.Link.LastSnapshot.OwnHull;

    private static HullHandle Handle(uint packed) => new((int)(packed & 0xFFFF), (int)(packed >> 16));

    private static List<int> Drones(SaveRig rig)
    {
        var found = new List<int>();
        for (var i = 0; i < rig.Hulls.Slots; i++)
            if (rig.Hulls.IsAliveAt(i) && rig.Hulls.Info(i).WorkDrone) found.Add(i);
        return found;
    }

    private static void Launch(SaveRig rig, int count, Vector3? point = null) =>
        rig.Link.Drones(new DroneCommandMessage(Own(rig), DroneCommand.Launch, (byte)count, point.HasValue, point ?? default));

    private static void Recall(SaveRig rig) => rig.Link.Drones(new DroneCommandMessage(Own(rig), DroneCommand.Recall, 0, false, default));

    /// <summary>Ticks until <paramref name="done"/> holds, at most <paramref name="seconds"/> of play.</summary>
    private static void TickUntil(SaveRig rig, Func<bool> done, float seconds, string what)
    {
        for (var i = 0; i < seconds * 60 && !done(); i++)
            rig.Tick();
        Assert.True(done(), what);
    }

    private static BayEntry Bay(SaveRig rig, int index = 0) => rig.Link.Bays.Bays[index];

    [Fact]
    public void TheBayIsABoxOnlyStorageBlockWithTiersAndTheWorkDroneFliesEveryWay()
    {
        var type = Shipped.Blocks.Get("droneBay");
        Assert.NotNull(type.Bay);
        Assert.True(type.BoxOnly);
        Assert.False(type.Drone);
        Assert.Equal(ModuleQuality.Common, type.Bay!.Quality);
        Assert.Equal(4, type.Bay.Capacity);
        Assert.Equal(2, type.Bay.LaunchRate);
        Assert.Equal(90, type.Bay.RebuildSeconds);
        Assert.Equal(90L * 60, type.Bay.RebuildTicks);

        // One bay type per tier is never needed: every bay in the palette is Common, as every tractor is.
        foreach (var other in Shipped.Blocks.Types)
            if (other.Bay is not null) Assert.Equal(ModuleQuality.Common, other.Bay.Quality);

        var drone = Shipped.Designs[Shipped.WorkDroneDesign];
        Assert.Equal(HullClass.Fighter, drone.Class);
        Assert.Equal(FtlKind.None, drone.Flight.Ftl);
        foreach (var block in drone.Types) Assert.True(block.FitsClass(drone.Class), block.Id);
        for (var face = 0; face < 6; face++)
            Assert.True(drone.Flight.Push((BlockFacing)face, default) > 0, ((BlockFacing)face).ToString());
        Assert.True(drone.Flight.Radius < 1f);
    }

    [Fact]
    public void BayTiersScaleCapacityRateAndRebuildAndRefuseNonsense()
    {
        var json = """{ "blocks": { "b": { "bay": { "quality": "Rare", "capacity": 4, "launchRate": 2, "rebuildSeconds": 100, "carryKg": 1000 } } }, "bayTiers": { "Rare": { "capacity": 2, "launchRate": 1.5, "rebuildSeconds": 0.5, "carry": 2 } } }""";
        using var document = System.Text.Json.JsonDocument.Parse(json);
        var spec = BaySpec.Read(document.RootElement.GetProperty("blocks"u8).GetProperty("b"u8), document.RootElement)!;
        Assert.Equal(new BaySpec(8, 3, 50, ModuleQuality.Rare, 2000), spec);

        var tooMany = json.Replace("\"capacity\": 4", "\"capacity\": 40");
        using var big = System.Text.Json.JsonDocument.Parse(tooMany);
        Assert.Throws<InvalidDataException>(() => BaySpec.Read(big.RootElement.GetProperty("blocks"u8).GetProperty("b"u8), big.RootElement));
        var negative = json.Replace("\"rebuildSeconds\": 100", "\"rebuildSeconds\": -1");
        using var bad = System.Text.Json.JsonDocument.Parse(negative);
        Assert.Throws<InvalidDataException>(() => BaySpec.Read(bad.RootElement.GetProperty("blocks"u8).GetProperty("b"u8), bad.RootElement));
    }

    [Fact]
    public void AHatchFacesOpenSpaceUnlessABlockOrAMountStandsInFront()
    {
        var open = Content().Pod;
        var block = open.Bays[0];
        Assert.Equal(BlockFacing.Down, DroneBayRules.Hatch(open, block));
        Assert.True(DroneBayRules.HatchClear(open, open.FullSet, block, 3f, 8192));

        var shut = Content(blocked: true).Pod;
        Assert.False(DroneBayRules.HatchClear(shut, shut.FullSet, shut.Bays[0], 3f, 8192));

        // The block in front is gone: the hatch is open again.
        var alive = shut.FullSet.ToArray();
        BlockSet.Remove(alive, shut.BlockCount - 1);
        Assert.True(DroneBayRules.HatchClear(shut, alive, shut.Bays[0], 3f, 8192));

        // A part mounted on the hatch itself blocks it.
        var pod = Shipped.Pod;
        var bay = new DesignBox(new GridSize(0, -1, -1), new GridSize(1, 1, 1), Shipped.Blocks.Get("droneBay"), BlockShape.Box, BlockFacing.Down);
        DesignMount[] mounts = [.. pod.Mounts.ToArray(), new DesignMount(pod.BlockCount, BlockFacing.Down, 3, 3, Shipped.Blocks.Get("coldGasThruster"))];
        var mounted = HullDesign.Of(new BoxDesign("m", 1, [.. pod.Boxes.ToArray(), bay], Shipped.Blocks.Limits, "test", pod.Class, pod.Color, mounts), Shipped.Rules);
        Assert.False(DroneBayRules.HatchClear(mounted, mounted.FullSet, pod.BlockCount, 3f, 8192));
    }

    [Fact]
    public void ADesignHasAtMostSixteenBays()
    {
        var core = new DesignBox(new GridSize(0, 0, 0), new GridSize(1, 1, 1), Shipped.Blocks.Get("core"));
        var boxes = new List<DesignBox> { core };
        for (var i = 0; i < DroneBayRules.MaxBays + 1; i++)
            boxes.Add(new DesignBox(new GridSize(i + 1, 0, 0), new GridSize(1, 1, 1), Shipped.Blocks.Get("droneBay"), BlockShape.Box, BlockFacing.Up));
        var error = Assert.Throws<InvalidDataException>(() => new BoxDesign("bays", 1, [.. boxes], Shipped.Blocks.Limits, "test"));
        Assert.Contains("too many drone bays", error.Message);
    }

    [Fact]
    public void LaunchedDronesLeaveThroughTheHatchHoldStationAndDockWhenRecalled()
    {
        var content = Content();
        using var rig = new SaveRig(SaveRig.NewWorld(content), content);
        rig.Join();
        rig.Tick();
        var pod = Handle(Own(rig));
        Assert.Equal(1, rig.Link.Bays.Count);
        Assert.Equal(4, Bay(rig).Stocked);
        Assert.True(Bay(rig).HatchClear);

        Launch(rig, 3);
        rig.Tick();
        Assert.Equal(DroneReply.Launching, rig.Link.Bays.Reply);
        Assert.Equal(3, rig.Link.Bays.ReplyCount);

        // Two leave at once, the third a launch interval later.
        TickUntil(rig, () => Drones(rig).Count == 3, 3, "three drones launched");
        Assert.Equal(1, Bay(rig).Stocked);
        Assert.Equal(3, Bay(rig).Out);

        var design = content.Pod;
        var block = design.Bays[0];
        var state = rig.Hulls.State(pod.Index);
        foreach (var drone in Drones(rig))
        {
            var info = rig.Hulls.Info(drone);
            Assert.Equal(rig.Hulls.Info(pod.Index).Owner, info.Owner);
            Assert.Equal((byte)content.WorkDroneDesign, info.Design);
            Assert.False(info.Piloted);
        }

        // They gather in front of the hatch, holding still.
        var hold = state.Position + HullRotation.Rotate(DroneBayRules.DefaultHold(design, block, content.Drones.HoldDistanceMetres), state.Orientation);
        rig.Tick(60 * 25);
        foreach (var drone in Drones(rig))
        {
            var at = rig.Hulls.State(drone);
            Assert.InRange(at.Position.RelativeTo(hold).Length(), 0, content.Drones.HoldSpacingMetres * 1.5f + content.Drones.HoldSlackMetres + 3.5f);
            Assert.InRange(at.Velocity.Length(), 0, 1.5f);
        }

        Recall(rig);
        rig.Tick();
        Assert.Equal(DroneReply.Recalling, rig.Link.Bays.Reply);
        Assert.Equal(3, rig.Link.Bays.ReplyCount);
        TickUntil(rig, () => Drones(rig).Count == 0, 60, "every drone docked");
        rig.Tick();
        Assert.Equal(4, Bay(rig).Stocked);
        Assert.Equal(0, Bay(rig).Out);
        Assert.Equal(0, Bay(rig).RebuildDone);

        // Nobody to call home now.
        rig.Tick(30);
        Recall(rig);
        rig.Tick();
        Assert.Equal(DroneReply.NoneOut, rig.Link.Bays.Reply);
    }

    [Fact]
    public void ABlockedHatchRefusesALaunchAndALostDroneIsRebuiltSlowly()
    {
        var blocked = Content(blocked: true);
        using (var rig = new SaveRig(SaveRig.NewWorld(blocked), blocked))
        {
            rig.Join();
            rig.Tick();
            Assert.False(Bay(rig).HatchClear);
            Launch(rig, 1);
            rig.Tick();
            Assert.Equal(DroneReply.HatchBlocked, rig.Link.Bays.Reply);
            rig.Tick(120);
            Assert.Empty(Drones(rig));
        }

        var content = Content();
        using var open = new SaveRig(SaveRig.NewWorld(content), content);
        open.Join();
        open.Tick();
        Launch(open, 1);
        TickUntil(open, () => Drones(open).Count == 1, 2, "a drone launched");
        open.Hulls.Destroy(open.Hulls.HandleAt(Drones(open)[0]));
        open.Tick(2);
        Assert.Equal(3, Bay(open).Stocked);
        Assert.Equal(0, Bay(open).Out);
        var done = Bay(open).RebuildDone;
        Assert.InRange(done - open.WorldTick, 90 * 60 - 3, 90 * 60);
        TickUntil(open, () => Bay(open).Stocked == 4, 91, "the lost drone rebuilt");
        Assert.Equal(0, Bay(open).RebuildDone);
    }

    [Fact]
    public void APlayerHasAtMostTheirShareOfDronesOutAndAJumpCallsThemHome()
    {
        var content = Content(bays: 4);
        using var rig = new SaveRig(SaveRig.NewWorld(content), content);
        rig.Join();
        rig.Tick();
        Assert.Equal(4, rig.Link.Bays.Count);
        Launch(rig, 16);
        rig.Tick();
        Assert.Equal(DroneReply.Launching, rig.Link.Bays.Reply);
        Assert.Equal(content.Drones.MaxPerPlayer, rig.Link.Bays.ReplyCount);
        TickUntil(rig, () => Drones(rig).Count == content.Drones.MaxPerPlayer, 4, "the player's share launched");
        rig.Tick(30);
        Launch(rig, 1);
        rig.Tick();
        Assert.Equal(DroneReply.PlayerLimit, rig.Link.Bays.Reply);
        rig.Tick(120);
        Assert.Equal(content.Drones.MaxPerPlayer, Drones(rig).Count);

        // A point too far away is refused before anything leaves.
        Recall(rig);
        TickUntil(rig, () => Drones(rig).Count == 0, 60, "every drone docked");
        rig.Tick(30);
        Launch(rig, 1, new Vector3(0, 0, content.Drones.MaxHoldDistanceMetres + 1));
        rig.Tick();
        Assert.Equal(DroneReply.TooFar, rig.Link.Bays.Reply);
    }

    [Fact]
    public void ANamedPointIsHeldInTheHullsFrame()
    {
        var content = Content(bays: 2);
        using var rig = new SaveRig(SaveRig.NewWorld(content), content);
        rig.Join();
        rig.Tick();
        var point = new Vector3(0, -12, 6);
        Launch(rig, 2, point);
        TickUntil(rig, () => Drones(rig).Count == 2, 3, "two drones launched");
        rig.Tick(60 * 25);
        var pod = rig.Hulls.State(Handle(Own(rig)).Index);
        var target = pod.Position + HullRotation.Rotate(point, pod.Orientation);
        foreach (var drone in Drones(rig))
            Assert.InRange(rig.Hulls.State(drone).Position.RelativeTo(target).Length(), 0, content.Drones.HoldSpacingMetres + content.Drones.HoldSlackMetres + 3.5f);
    }

    [Fact]
    public void ASavedWorldDocksItsDronesAndKeepsTheRebuild()
    {
        var content = Content();
        var folder = SaveRig.NewWorld(content);
        long left;
        using (var rig = new SaveRig(folder, content))
        {
            rig.Join();
            rig.Tick();
            Launch(rig, 2);
            TickUntil(rig, () => Drones(rig).Count == 2, 2, "two drones launched");
            rig.Hulls.Destroy(rig.Hulls.HandleAt(Drones(rig)[0]));
            rig.Tick(2);
            Assert.Equal(2, Bay(rig).Stocked);
            Assert.Equal(1, Bay(rig).Out);
            left = Bay(rig).RebuildDone - rig.WorldTick;
            Assert.True(rig.Close().Clean);
        }

        var saved = folder.Decode();
        Assert.Equal(1, saved.BayCount);
        Assert.Equal(3, saved.BayStocked[0]);
        Assert.InRange(saved.BayRebuild[0], left - 2, left + 2);
        for (var i = 0; i < saved.HullCount; i++)
            Assert.NotEqual((byte)content.WorkDroneDesign, saved.Hulls[i].Design);

        using var again = new SaveRig(folder, content);
        again.Join();
        again.Tick();
        Assert.Empty(Drones(again));
        Assert.Equal(3, Bay(again).Stocked);
        Assert.Equal(0, Bay(again).Out);
        Assert.InRange(Bay(again).RebuildDone - again.WorldTick, left - 4, left + 2);

        // A save that claims more than a bay keeps is refused.
        again.Dispose();
        Assert.Throws<InvalidDataException>(() => new SaveRig(folder, content, edit: world =>
        {
            world.BayStocked[0] = 9;
            return world;
        }).Dispose());
    }

    [Fact]
    public void AnEditKeepsItsBaysAndTheirDronesOut()
    {
        var content = Content();
        using var rig = new SaveRig(SaveRig.NewWorld(content), content);
        rig.Join();
        rig.Tick();
        Launch(rig, 1);
        TickUntil(rig, () => Drones(rig).Count == 1, 2, "a drone launched");
        var old = Handle(Own(rig));
        var info = rig.Hulls.Info(old.Index);
        var create = typeof(ServerHost).GetMethod("CreateHull", Private)!;
        var next = (HullHandle)create.Invoke(rig.Server, [info.Owner, info.Design, rig.Hulls.State(old.Index).Position, false, info.Entity])!;
        typeof(ServerHost).GetMethod("CarryBays", Private)!.Invoke(rig.Server, [old, next]);
        rig.Hulls.Destroy(old);
        var tickDrones = typeof(ServerHost).GetMethod("TickDrones", Private)!.CreateDelegate<Action>(rig.Server);
        tickDrones();
        Assert.Single(Drones(rig));
        var record = Record(rig, next);
        Assert.Equal((3, 1), record);
        var order = ((Array)typeof(ServerHost).GetField("_orders", Private)!.GetValue(rig.Server)!).GetValue(0)!;
        Assert.Equal(next, (HullHandle)order.GetType().GetField("Base")!.GetValue(order)!);
    }

    /// <summary>The first bay of a hull's page as the server keeps it: drones inside and out.</summary>
    private static (int Stocked, int Out) Record(SaveRig rig, HullHandle hull)
    {
        var pages = (Array)typeof(ServerHost).GetField("_bayPages", Private)!.GetValue(rig.Server)!;
        var page = pages.GetValue(rig.Hulls.Info(hull.Index).BayPage - 1)!;
        var bays = page.GetType().GetField("Bays")!.GetValue(page)!;
        var first = bays.GetType().GetField("_first", Private)!.GetValue(bays)!;
        return ((byte)first.GetType().GetField("Stocked")!.GetValue(first)!, (byte)first.GetType().GetField("Out")!.GetValue(first)!);
    }

    [Fact]
    public void DronesTakeNoMemoryInATick()
    {
        var content = Content(bays: 2);
        using var rig = new SaveRig(SaveRig.NewWorld(content), content);
        rig.Join();
        rig.Tick();
        Launch(rig, 4);
        TickUntil(rig, () => Drones(rig).Count == 4, 3, "four drones launched");
        rig.Tick(60);
        var tickDrones = typeof(ServerHost).GetMethod("TickDrones", Private)!.CreateDelegate<Action>(rig.Server);
        var sendBays = typeof(ServerHost).GetMethod("SendBays", Private)!.CreateDelegate<Action>(rig.Server);
        var probe = new AllocationProbe();
        for (var i = 0; i < 120; i++)
        {
            while (probe.Measuring()) { tickDrones(); sendBays(); }
            rig.Tick();
        }
        Recall(rig);
        rig.Tick();
        for (var i = 0; i < 600 && Drones(rig).Count > 0; i++)
        {
            while (probe.Measuring()) { tickDrones(); sendBays(); }
            rig.Tick();
        }
        Assert.Empty(Drones(rig));
        Assert.Equal(0, probe.Bytes);
    }

    [Fact]
    public void CommandsAndBayViewsRoundTripAndMalformedOnesAreRefused()
    {
        var bytes = new byte[DroneCommandMessage.MaxWireSize];
        var command = new DroneCommandMessage(0x00010002, DroneCommand.Launch, 3, true, new Vector3(1, -2, 3));
        var writer = new NetWriter(bytes);
        command.Write(ref writer);
        Assert.Equal(DroneCommandMessage.MaxWireSize, writer.Length);
        var reader = new NetReader(bytes.AsSpan(1));
        Assert.True(DroneCommandMessage.TryRead(ref reader, out var read));
        Assert.Equal(command, read);

        byte[] Bad(Action<byte[]> change)
        {
            var copy = (byte[])bytes.Clone();
            change(copy);
            return copy;
        }
        foreach (var bad in new[]
        {
            Bad(b => b[5] = 9),                     // no such command
            Bad(b => b[6] = 0),                     // launching nothing
            Bad(b => b[7] = 2),                     // flag out of range
            Bad(b => BitConverter.TryWriteBytes(b.AsSpan(8), float.NaN)),
        })
        {
            var r = new NetReader(bad.AsSpan(1));
            Assert.False(DroneCommandMessage.TryRead(ref r, out _));
        }
        var unpointed = new byte[DroneCommandMessage.MaxWireSize];
        var w = new NetWriter(unpointed);
        new DroneCommandMessage(5, DroneCommand.Recall, 0, false, new Vector3(1, 0, 0)).Write(ref w);
        var u = new NetReader(unpointed.AsSpan(1));
        Assert.False(DroneCommandMessage.TryRead(ref u, out _));

        var view = new BayView { Hull = 7, Tick = 100, ReplySequence = 3, Reply = DroneReply.Recalling, ReplyCount = 2, Count = DroneBayRules.MaxBays };
        for (var i = 0; i < view.Count; i++)
            view.Bays[i] = new BayEntry { Block = (ushort)i, Capacity = 4, Stocked = 2, Out = 1, Pending = 1, Live = true, HatchClear = i % 2 == 0, RebuildDone = 500 };
        var buffer = new byte[BayMessage.MaxWireSize];
        writer = new NetWriter(buffer);
        BayMessage.Write(ref writer, view);
        Assert.Equal(BayMessage.MaxWireSize, writer.Length);
        reader = new NetReader(buffer.AsSpan(1));
        Assert.True(BayMessage.TryRead(ref reader, out var back));
        Assert.Equal(view.Hull, back.Hull);
        Assert.Equal(view.Reply, back.Reply);
        for (var i = 0; i < view.Count; i++) Assert.Equal(view.Bays[i], back.Bays[i]);
        back.Totals(out var stocked, out var outside, out var pending, out var capacity);
        Assert.Equal((32, 16, 16, 64), (stocked, outside, pending, capacity));

        // A bay holding more than it keeps is no bay a server sends.
        buffer[BayMessage.HeaderBytes + 3] = 4;
        reader = new NetReader(buffer.AsSpan(1));
        Assert.False(BayMessage.TryRead(ref reader, out _));
    }
}
