using System.Numerics;
using Prospect.Game.Persistence;
using Prospect.Game.World;
using Xunit;

namespace Prospect.Tests;

/// <summary>
/// A saved world at the edge of what its save holds: real hulls striking real asteroids when no more can be
/// moved, and worlds loaded with exactly, or one more than, the room an owner's drone needs.
/// </summary>
public sealed class SaveCapacityTests
{
    private static readonly GameContent Shipped = GameContent.Load();

    /// <summary>
    /// Asteroids of a sector that a hull can be flown at alone: small enough that they would visibly give way,
    /// and with nothing else near them. Sectors near home are searched until one has <paramref name="count"/>.
    /// </summary>
    private static RockRef[] LoneRocks(RockField rocks, int count, SectorCoord? not = null)
    {
        for (var x = -8; x <= 8; x++)
        {
            for (var z = -8; z <= 8; z++)
            {
                var sector = new SectorCoord(x, 0, z);
                if (sector == not)
                    continue;
                var content = rocks.Sector(sector);
                var found = new List<RockRef>();
                for (var i = content.AsteroidCount - 1; i >= 0 && found.Count < count; i--)
                {
                    var rock = content.Asteroids[i];
                    if (rock.Radius is < 6f or > 30f)
                        continue;
                    var alone = true;
                    for (var j = 0; j < content.AsteroidCount && alone; j++)
                        alone = j == i || (content.Asteroids[j].LocalPosition - rock.LocalPosition).Length() > content.Asteroids[j].Radius + rock.Radius + 120f;
                    if (alone)
                        found.Add(new RockRef(sector, i));
                }

                if (found.Count == count)
                    return [.. found];
            }
        }

        throw new InvalidOperationException("No sector near home has that many asteroids standing alone.");
    }

    /// <summary>
    /// Puts a hull into the surface of an asteroid, flying into it slower than does damage: the next interval
    /// is a real contact, found and resolved by the simulation, with an impulse to share.
    /// </summary>
    private static void Against(SaveRig rig, int slot, RockRef name)
    {
        var rock = rig.Rocks.Sector(name.Sector).Asteroids[name.Index];
        rig.Rocks.Displacement(name, rig.WorldTick, out var offset, out _);
        var surface = rock.Radius * AsteroidShape.RadiusAlong(rock.Shape, Vector3.Transform(Vector3.UnitZ, Quaternion.Conjugate(rock.Rotation)));
        ref var state = ref rig.Hulls.State(slot);
        state.Position = rig.Server.Galaxy.CenterOf(name.Sector) + (rock.LocalPosition + offset + Vector3.UnitZ * (surface + 0.5f));
        state.Orientation = Quaternion.Identity;
        state.Velocity = -Vector3.UnitZ * 4f;
        state.AngularVelocity = Vector3.Zero;
    }

    private static SaveRig Started(SaveLimits limits)
    {
        var rig = new SaveRig(SaveRig.NewWorld(Shipped), Shipped, limits: limits);
        rig.Join();
        rig.Tick(2);
        Assert.Equal(3, rig.Hulls.Count);
        return rig;
    }

    private static bool Moved(SaveRig rig, RockRef rock)
    {
        foreach (ref readonly var delta in rig.Rocks.Deltas)
            if (delta.Rock == rock) return true;
        return false;
    }

    [Fact]
    public void AnAsteroidTheWorldCannotMoveIsStruckAsOneThatDoesNotGiveWay()
    {
        // The same hull into the same asteroid, twice: in a world with room, and in one that has moved all it may.
        using var free = Started(SaveLimits.Default);
        var rocks = LoneRocks(free.Rocks, 2);
        Against(free, 1, rocks[0]);
        free.Tick();
        Assert.True(Moved(free, rocks[0]));
        Assert.True(free.Rocks.Deltas[0].Velocity.Z < 0f, "the asteroid should have been pushed the way the hull flew");
        Assert.Equal(1, free.Link.RockMessages);
        Assert.Equal(0, free.Server.Save.RefusedGrowth);
        var gaveWay = free.Hulls.State(1);

        using var full = Started(new SaveLimits { MaxRocks = 1 });
        Assert.True(full.Rocks.Set(new RockDelta { Rock = rocks[1], Velocity = Vector3.UnitX, StartTick = full.WorldTick - 1 }));
        Against(full, 1, rocks[0]);
        var before = full.Hulls.State(1);
        full.Tick();
        var stood = full.Hulls.State(1);

        // Nothing of the asteroid's side exists: no motion in the world, none told to the client, none saved.
        Assert.False(Moved(full, rocks[0]));
        Assert.Equal(1, full.Rocks.Deltas.Length);
        Assert.Equal(0, full.Link.RockMessages);
        Assert.Equal(1, full.Server.Save.RefusedGrowth);
        full.Rocks.Displacement(rocks[0], full.WorldTick + 600, out var offset, out var velocity);
        Assert.Equal((Vector3.Zero, Vector3.Zero), (offset, velocity));

        // And the hull's side is the other half of that same outcome: it met a body that took none of the
        // impulse, so it came back harder than off the asteroid that gave way. Equal would mean the hull was
        // given the share of an impact whose other share was thrown away.
        Assert.Equal(before.Position.Z > stood.Position.Z, before.Position.Z > gaveWay.Position.Z);
        Assert.True(stood.Velocity.Z > gaveWay.Velocity.Z, $"{stood.Velocity.Z} against {gaveWay.Velocity.Z}");
        Assert.True(stood.Velocity.Z > 0f);

        // One that has moved before still gives way, however full the world is.
        Against(full, 2, rocks[1]);
        full.Tick();
        Assert.Equal(1, full.Link.RockMessages);
        Assert.Equal(1, full.Server.Save.RefusedGrowth);
        Assert.NotEqual(Vector3.UnitX, full.Rocks.Deltas[0].Velocity);

        Assert.True(full.Close().Clean);
        Assert.Equal(1, full.Folder.Decode().RockCount);
        Assert.True(free.Close().Clean);
        Assert.Equal(1, free.Folder.Decode().RockCount);
    }

    [Fact]
    public void PushesPromisedInTheSameIntervalCountAgainstTheRoomThatIsLeft()
    {
        // Room for one more asteroid, and two hulls strike two in one interval: the first to step moves its
        // asteroid, the second meets one that stands, and each contact is whole.
        using var rig = Started(new SaveLimits { MaxRocks = 1 });
        var rocks = LoneRocks(rig.Rocks, 2);
        Against(rig, 1, rocks[0]);
        Against(rig, 2, rocks[1]);
        rig.Tick();
        Assert.True(Moved(rig, rocks[0]));
        Assert.False(Moved(rig, rocks[1]));
        Assert.Equal(1, rig.Rocks.Deltas.Length);
        Assert.Equal(1, rig.Server.Save.RefusedGrowth);
        Assert.Equal(1, rig.Link.RockMessages);
        var stood = rig.Hulls.State(2).Velocity.Z;
        var gaveWay = rig.Hulls.State(1).Velocity.Z;
        Assert.True(stood > 0f);

        // The other way round in a world of its own: whichever steps first is the one that is given way to.
        using var other = Started(new SaveLimits { MaxRocks = 1 });
        Against(other, 1, rocks[1]);
        Against(other, 2, rocks[0]);
        other.Tick();
        Assert.True(Moved(other, rocks[1]));
        Assert.False(Moved(other, rocks[0]));
        Assert.True(stood > other.Hulls.State(1).Velocity.Z, "the same hull class off the same asteroid, standing and giving way");
        Assert.True(other.Hulls.State(2).Velocity.Z > gaveWay);

        // A later hit on an already-recorded asteroid needs no additional capacity.
        using var same = Started(new SaveLimits { MaxRocks = 1 });
        Against(same, 1, rocks[0]);
        same.Tick();
        Against(same, 1, rocks[0]);
        same.Tick();
        Assert.Equal(1, same.Rocks.Deltas.Length);
        Assert.Equal(0, same.Server.Save.RefusedGrowth);
        Assert.Equal(2, same.Link.RockMessages);

        Assert.True(rig.Close().Clean);
        var saved = rig.Folder.Decode();
        Assert.Equal(1, saved.RockCount);
        Assert.Equal(rocks[0], saved.Rocks[0].Rock);
    }

    [Fact]
    public void DuplicatePendingPushesReserveOneRockAndOneSector()
    {
        var rocks = new RockField(Shipped.Galaxy, maxDeltas: 2, maxMovedSectors: 1);
        var first = new RockRef(default, 0);
        var second = new RockRef(default, 1);
        RockDelta[] pending = [new() { Rock = first }, new() { Rock = first }];
        Assert.True(rocks.CanMove(first, pending));
        Assert.True(rocks.CanMove(second, pending));
        Assert.False(rocks.CanMove(new RockRef(new SectorCoord(1, 0, 0), 0), pending));
        RockDelta[] full = [.. pending, new() { Rock = second }];
        Assert.True(rocks.CanMove(second, full));
        Assert.False(rocks.CanMove(new RockRef(default, 2), full));
    }

    [Fact]
    public void ASectorTheWorldCannotOpenForMovedAsteroidsKeepsItsAsteroidsStill()
    {
        var limits = new SaveLimits { MaxMovedSectors = 1 };
        using var rig = Started(limits);
        var near = LoneRocks(rig.Rocks, 2);
        var far = LoneRocks(rig.Rocks, 1, not: near[0].Sector)[0];

        // Two sectors in one interval and room for one: the first is opened, the second is not.
        Against(rig, 1, near[0]);
        Against(rig, 2, far);
        rig.Tick();
        Assert.True(Moved(rig, near[0]));
        Assert.False(Moved(rig, far));
        Assert.Equal((1, 1), (rig.Rocks.MovedSectors, rig.Rocks.Deltas.Length));
        Assert.Equal(1, rig.Server.Save.RefusedGrowth);
        Assert.Equal(1, rig.Link.RockMessages);
        Assert.True(rig.Hulls.State(2).Velocity.Z > rig.Hulls.State(1).Velocity.Z);

        // With the one sector open, the other stays shut in every later interval, and the open one takes more.
        Against(rig, 2, far);
        Against(rig, 1, near[1]);
        rig.Tick();
        Assert.False(Moved(rig, far));
        Assert.True(Moved(rig, near[1]));
        Assert.Equal((1, 2), (rig.Rocks.MovedSectors, rig.Rocks.Deltas.Length));
        Assert.Equal(2, rig.Server.Save.RefusedGrowth);

        // Two asteroids of one new sector in one interval open it once and both move.
        using var one = Started(limits);
        Against(one, 1, near[0]);
        Against(one, 2, near[1]);
        one.Tick();
        Assert.Equal((1, 2), (one.Rocks.MovedSectors, one.Rocks.Deltas.Length));
        Assert.Equal(0, one.Server.Save.RefusedGrowth);

        Assert.True(rig.Close().Clean);
        var folder = rig.Folder;
        rig.Dispose();
        using var again = new SaveRig(folder, Shipped, limits: limits);
        Assert.Equal((1, 2), (again.Rocks.MovedSectors, again.Rocks.Deltas.Length));
    }

    [Fact]
    public void AWorldFullOfHullsLoadsOnlyWithRoomForTheDroneAnOwnerIsPutIn()
    {
        // One owner, four hulls at most: three of them may be ordinary, the fourth place is the owner's drone.
        var limits = new SaveLimits { MaxOwners = 1, MaxHulls = 4 };
        var folder = SaveRig.NewWorld(Shipped);
        using (var rig = new SaveRig(folder, Shipped, limits: limits))
        {
            rig.Join();
            rig.Tick(2);
            Assert.True(rig.Close().Clean);
        }

        var three = folder.Bytes!;
        MemoryWorld World() => new() { Bytes = three, Generation = 5 };

        // Four ordinary hulls, with the owner in one or in none: joining or losing a hull would make a fifth,
        // which no save holds. Refused before play, the store released, the save untouched.
        foreach (var flying in new[] { true, false })
        {
            var world = World();
            var error = Assert.Throws<InvalidDataException>(() => new SaveRig(world, Shipped, limits: limits, edit: saved =>
            {
                var four = SaveLifecycleTests.Repeated(saved, 2, hulls: 4);
                if (!flying)
                    four.Owners[0].Controlled = 0;
                return four;
            }));
            Assert.Contains("drone", error.Message);
            Assert.False(world.Leased);
            Assert.Same(three, world.Bytes);
        }

        // Three ordinary hulls and an owner who flies none of them: loads, and the drone they are put in is the fourth.
        var loaded = World();
        using (var rig = new SaveRig(loaded, Shipped, limits: limits, edit: saved => { saved.Owners[0].Controlled = 0; return saved; }))
        {
            Assert.Equal(3, rig.Hulls.Count);
            rig.Join();
            Assert.Equal(4, rig.Hulls.Count);
            var drone = (int)(rig.Link.Welcome.HullId & 0xFFFF);
            Assert.True(rig.Hulls.Info(drone).EscapeDrone);

            // Full, and it still saves: the autosave, and then the drone lost and replaced, and the close.
            rig.Tick(5);
            rig.Autosave();
            Assert.Equal(SaveState.Idle, rig.Server.Save.State);
            Assert.Equal(4, loaded.Decode().HullCount);
            rig.WithConnection("LoseHull");
            Assert.Equal(4, rig.Hulls.Count);
            rig.Tick(2);
            Assert.True(rig.Close().Clean);
        }

        // Four hulls of which one is the drone the owner flies: that is a world this build makes, and it loads.
        Assert.Equal(4, loaded.Decode().HullCount);
        using (var rig = new SaveRig(loaded, Shipped, limits: limits))
        {
            Assert.Equal(4, rig.Hulls.Count);
            rig.Join();
            Assert.Equal(4, rig.Hulls.Count);
            rig.WithConnection("LoseHull");
            Assert.Equal(4, rig.Hulls.Count);

            // Back into the pod: the drone goes, and stepping out again is a drone again, never a fifth hull.
            var pod = -1;
            for (var i = 0; i < rig.Hulls.Slots; i++)
                if (rig.Hulls.IsAliveAt(i) && rig.Hulls.Info(i).Design == Shipped.PodDesign) pod = i;
            rig.Link.Control(rig.Hulls.HandleAt(pod).Packed);
            rig.Tick();
            Assert.Equal(3, rig.Hulls.Count);
            Assert.True(rig.Close().Clean);
        }

        Assert.Equal(3, loaded.Decode().HullCount);
    }

    [Fact]
    public void AWorldFullOfBlocksLoadsOnlyWithRoomForTheBlocksOfThatDrone()
    {
        long fleet = Shipped.Pod.BlockCount;
        foreach (var design in Shipped.StartingFleet)
            fleet += Shipped.Designs[design].BlockCount;
        var drone = Shipped.Designs[Shipped.DroneDesign].BlockCount;
        var exact = new SaveLimits { MaxOwners = 1, MaxBlocks = (int)fleet + drone };
        var tight = new SaveLimits { MaxOwners = 1, MaxBlocks = (int)fleet + drone - 1 };

        // Exactly a fleet and a drone: the owner gets the fleet, loses the pod, is put in the drone, and it saves.
        var folder = SaveRig.NewWorld(Shipped);
        using (var rig = new SaveRig(folder, Shipped, limits: exact))
        {
            rig.Join();
            Assert.Equal(3, rig.Hulls.Count);
            Assert.Equal(fleet, rig.Hulls.BlockTotal);
            rig.Tick(2);
            rig.Autosave();
            var whole = folder.Bytes!;

            // One block less and the same fleet is one block too many for the drone to be sure of its place.
            var world = new MemoryWorld { Bytes = whole, Generation = 9 };
            var error = Assert.Throws<InvalidDataException>(() => new SaveRig(world, Shipped, limits: tight));
            Assert.Contains("drone", error.Message);
            Assert.False(world.Leased);

            rig.WithConnection("LoseHull");
            Assert.Equal(fleet - Shipped.Pod.BlockCount + drone, rig.Hulls.BlockTotal);
            rig.Tick(2);
            Assert.True(rig.Close().Clean);
        }

        // The drone the owner flies is not counted against the room kept for it: this loads under the tight
        // limits as well, where the fleet with its pod did not.
        using (var rig = new SaveRig(folder, Shipped, limits: exact))
            Assert.Equal(3, rig.Hulls.Count);
        using var smaller = new SaveRig(folder, Shipped, limits: tight);
        smaller.Join();
        Assert.Equal(3, smaller.Hulls.Count);
        smaller.WithConnection("LoseHull");
        smaller.Tick(2);
        Assert.True(smaller.Close().Clean);

        // Under the tight limits no new owner would have been given a fleet in the first place.
        var empty = SaveRig.NewWorld(Shipped);
        using var refused = new SaveRig(empty, Shipped, limits: tight);
        refused.Link.Join();
        refused.Tick();
        Assert.Equal(0, refused.Link.Welcomes);
        Assert.Equal(0, refused.Hulls.Count);
        Assert.Equal(1, refused.Server.Save.RefusedGrowth);
    }
}
