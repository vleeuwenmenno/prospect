using System.Numerics;
using Prospect.Engine.Net;
using Prospect.Game.Protocol;
using Prospect.Game.Server;
using Prospect.Game.World;
using Xunit;

namespace Prospect.Tests;

/// <summary>
/// How drones fly when they bring something in, on a real server: they keep up with a moving hull, keep a light load
/// beside them, never touch each other or their own hull, and take a load round the hull rather than into it. Each test
/// holds one cause of drones that wandered, dropped or lost their load, or never came home.
/// </summary>
public sealed class DroneFlightTests
{
    private static HullHandle Pod(SaveRig rig) { var own = rig.Link.LastSnapshot.OwnHull; return new((int)(own & 0xFFFF), (int)(own >> 16)); }

    private static FreeContainers Containers(SaveRig rig) => rig.Get<FreeContainers>("_containers");

    private static SaveRig Rig(GameContent content)
    {
        var rig = new SaveRig(SaveRig.NewWorld(content), content);
        rig.Join();
        rig.Tick();
        return rig;
    }

    private static List<int> Drones(SaveRig rig)
    {
        var found = new List<int>();
        for (var i = 0; i < rig.Hulls.Slots; i++)
            if (rig.Hulls.IsAliveAt(i) && rig.Hulls.Info(i).WorkDrone) found.Add(i);
        return found;
    }

    /// <summary>A container of <paramref name="size"/> holding <paramref name="fill"/> of what it can, at rest with the pod.</summary>
    private static uint Place(SaveRig rig, GameContent content, Vector3 offset, int size, double fill = 1)
    {
        var pod = rig.Hulls.State(Pod(rig).Index);
        var units = content.Cargo.Sizes[size].Units;
        var unit = (double)content.Cargo.UnitSize;
        Assert.True(Containers(rig).Add(content, new FreeContainer
        {
            Position = pod.Position + offset,
            Velocity = pod.Velocity,
            Orientation = Quaternion.Identity,
            Size = size,
            Resource = 0,
            Amount = fill * units * unit * unit * unit * content.Cargo.Density(content.Galaxy.Resources[0].Id),
            Hull = content.Cargo.Sizes[size].Hull,
        }, out var id));
        return id;
    }

    private static void Fetch(SaveRig rig, uint id) =>
        rig.Link.Drones(new DroneCommandMessage(rig.Link.LastSnapshot.OwnHull, DroneCommand.Fetch, 0, false, default, id));

    /// <summary>Counts contacts between drones, and between a drone and the pod, from the next tick on.</summary>
    private static (Func<int> Drones, Func<int> Hull) CountContacts(SaveRig rig)
    {
        var drones = 0;
        var hull = 0;
        var pod = Pod(rig).Index;
        rig.Field.ContactObserved = (a, b, _) =>
        {
            var da = rig.Hulls.Info(a).WorkDrone;
            var db = rig.Hulls.Info(b).WorkDrone;
            if (da && db) drones++;
            else if ((da && b == pod) || (db && a == pod)) hull++;
        };
        return (() => drones, () => hull);
    }

    /// <summary>
    /// The pilot's input for the next interval with the dampeners off, so the pod coasts at whatever speed it was given,
    /// as a player flying on does.
    /// </summary>
    private static void Coast(SaveRig rig, uint sequence)
    {
        object? connection = null;
        foreach (var c in rig.Get<System.Collections.IList>("_connections"))
            if (c is not null && c.GetType().GetField("State")!.GetValue(c)!.ToString() == "Active") connection = c;
        var epoch = (uint)connection!.GetType().GetField("ControlEpoch")!.GetValue(connection)!;
        var buffer = new byte[InputMessage.MaxWireSize];
        var writer = new NetWriter(buffer);
        InputMessage.Write(ref writer, [new InputRecord { TargetTick = rig.WorldTick + 1, Sequence = sequence, Input = new FlightInput { Dampeners = false } }], epoch);
        rig.Link.Deliver(writer.Written.ToArray(), NetChannel.Unreliable);
    }

    [Fact]
    public void DronesComeHomeToAHullFlyingOnAndBringALoadToIt()
    {
        // The autopilot stops at its target in the world, so a drone flown to a point on a moving hull trailed it: on a
        // hull at 10 m/s drones never lined up at the hatch and never docked.
        var content = DroneFetchTests.Content();
        using var rig = Rig(content);
        var pod = Pod(rig);
        var forward = HullRotation.Rotate(-Vector3.UnitZ, rig.Hulls.State(pod.Index).Orientation);
        var id = Place(rig, content, new Vector3(-120, 15, 30), size: 2);
        Containers(rig).At(0).Velocity = forward * 10;
        rig.Hulls.State(pod.Index).Velocity = forward * 10;
        Fetch(rig, id);
        var sequence = 1u;
        var delivered = -1;
        for (var t = 0; t < 60 * 90 && (delivered < 0 || Drones(rig).Count > 0); t++)
        {
            Coast(rig, sequence++);
            rig.Tick();
            if (delivered < 0 && Containers(rig).Find(id) >= 0 && Containers(rig).Items[Containers(rig).Find(id)].TractorHull != 0) delivered = t;
        }
        Assert.InRange((rig.Hulls.State(pod.Index).Velocity - forward * 10).Length(), 0, 0.5f);
        Assert.True(delivered >= 0, "the drone handed the container to the beam");
        Assert.InRange(delivered / 60f, 0, 45);
        Assert.Empty(Drones(rig));
        Assert.Equal(DroneReply.Delivered, rig.Link.Bays.Reply);
    }

    [Fact]
    public void ALightLoadIsCarriedNoFasterThanItsDronesCanFollow()
    {
        // The carry's acceleration grew with what the drones lift over what the load weighs: a nearly empty container
        // shot off at many times the drones' own push, left them behind and flew far past the hull before they caught it.
        var content = DroneFetchTests.Content();
        using var rig = Rig(content);
        var pod = Pod(rig);
        var id = Place(rig, content, new Vector3(-90, 10, 20), size: 0, fill: 0.02);
        var start = Containers(rig).Items[0].Position.RelativeTo(rig.Hulls.State(pod.Index).Position).Length();
        Fetch(rig, id);
        var orders = rig.Get<TaskOrder[]>("_orders");
        var held = false;
        var lost = 0;
        var farthest = 0f;
        var push = content.Designs[content.WorkDroneDesign].Flight.WeakestPush;
        var fastest = 0f;
        var last = Vector3.Zero;
        for (var t = 0; t < 60 * 60 && Containers(rig).Find(id) >= 0; t++)
        {
            rig.Tick();
            var index = Containers(rig).Find(id);
            if (index < 0) break;
            var item = Containers(rig).Items[index];
            if (item.TractorHull != 0) break;
            var relative = item.Velocity - rig.Hulls.State(pod.Index).Velocity;
            if (held)
            {
                if (orders[0].Task == OrderTask.Fetch && orders[0].Phase == 0) lost++;
                farthest = Math.Max(farthest, item.Position.RelativeTo(rig.Hulls.State(pod.Index).Position).Length());
                fastest = Math.Max(fastest, (relative - last).Length() / HullSim.Dt);
            }
            held |= orders[0].Task == OrderTask.Fetch && orders[0].Phase == 1;
            last = relative;
        }
        Assert.True(held);
        Assert.Equal(0, lost);
        Assert.InRange(farthest, 0, start + 2);
        Assert.InRange(fastest, 0, content.Drones.CarryFollowShare * push + 0.05f);
        rig.Tick(60 * 30);
        Assert.Equal(1, rig.Hulls.BlockStates.Cargo(rig.Hulls.Info(pod.Index).BlockState).Length);
    }

    [Fact]
    public void DronesLaunchedTogetherNeverTouchEachOtherOrTheirHull()
    {
        // Two drones leave a hatch one behind the other; their places beside the container lie the other way round, so
        // they crossed and flew most of the way touching. Drones also left the hatch straight into the hull.
        var content = DroneFetchTests.Content();
        using var rig = Rig(content);
        var id = Place(rig, content, new Vector3(150, -40, 60), size: 3);
        var contacts = CountContacts(rig);
        Fetch(rig, id);
        for (var t = 0; t < 60 * 90 && (Containers(rig).Find(id) >= 0 || Drones(rig).Count > 0); t++) rig.Tick();
        Assert.Equal(-1, Containers(rig).Find(id));
        Assert.Empty(Drones(rig));
        Assert.Equal(0, contacts.Drones());
        Assert.Equal(0, contacts.Hull());
    }

    [Fact]
    public void ALoadOnTheFarSideOfTheHullIsBroughtRoundIt()
    {
        // The beam's hand-over point is above the hull: a container below it was pulled straight up into the hull's belly
        // and stuck there, its drones grinding against the hull, and never handed over.
        var content = DroneFetchTests.Content();
        using var rig = Rig(content);
        var pod = Pod(rig);
        var id = Place(rig, content, new Vector3(0, -120, 0), size: 3);
        var contacts = CountContacts(rig);
        Fetch(rig, id);
        var stowed = -1;
        for (var t = 0; t < 60 * 120 && Drones(rig).Count + (Containers(rig).Find(id) >= 0 ? 1 : 0) > 0; t++)
        {
            rig.Tick();
            if (stowed < 0 && Containers(rig).Find(id) < 0) stowed = t;
        }
        Assert.True(stowed >= 0, "the container stowed");
        Assert.InRange(stowed / 60f, 0, 60);
        Assert.Equal(1, rig.Hulls.BlockStates.Cargo(rig.Hulls.Info(pod.Index).BlockState).Length);
        Assert.Equal(0, contacts.Hull());
        Assert.Empty(Drones(rig));
    }

    [Fact]
    public void DronesComingHomeTogetherWaitApartAndDockOneByOne()
    {
        // Every drone coming home flew to the same hold point and then the same point in front of the hatch.
        var content = DroneFetchTests.Content();
        using var rig = Rig(content);
        rig.Link.Drones(new DroneCommandMessage(rig.Link.LastSnapshot.OwnHull, DroneCommand.Launch, 4, false, default));
        rig.Tick(60 * 8);
        Assert.Equal(4, Drones(rig).Count);
        var contacts = CountContacts(rig);
        rig.Link.Drones(new DroneCommandMessage(rig.Link.LastSnapshot.OwnHull, DroneCommand.Recall, 0, false, default));
        rig.Tick();
        var orders = rig.Get<TaskOrder[]>("_orders");
        var places = new HashSet<Vector3>();
        for (var i = 0; i < 4; i++) { Assert.Equal(OrderTask.Recall, orders[i].Task); places.Add(orders[i].Point); }
        Assert.Equal(4, places.Count);
        for (var t = 0; t < 60 * 60 && Drones(rig).Count > 0; t++)
        {
            rig.Tick();
            var lining = 0;
            for (var i = 0; i < rig.Get<int>("_orderCount"); i++) if (orders[i].Task == OrderTask.Recall && orders[i].Phase == 0) lining++;
            Assert.InRange(lining, 0, 1);
        }
        Assert.Empty(Drones(rig));
        Assert.Equal(0, contacts.Drones());
        Assert.Equal(0, contacts.Hull());
    }
}
