using System.Numerics;
using Prospect.Game.World;
using Xunit;

namespace Prospect.Tests;

/// <summary>
/// The one ship a player is given: it is the pod, nothing else is spawned beside it, and it carries every part a
/// ship can take, each of them usable where it sits (the tractor beams reach a rack, the bay's hatch opens on space).
/// </summary>
public sealed class DefaultShipTests(ITestOutputHelper output)
{
    private static readonly GameContent Content = GameContent.Load();

    [Fact]
    public void ThePlayerStartsInOneShipAndTheCatalogueHoldsOnlyItAndTheDrones()
    {
        Assert.Empty(Content.StartingFleet);
        Assert.Equal(3, Content.Designs.Length);
        Assert.Equal(HullClass.Ship, Content.Pod.Class);
        Assert.Equal(3, new[] { Content.PodDesign, Content.DroneDesign, Content.WorkDroneDesign }.Distinct().Count());
    }

    [Fact]
    public void TheShipCarriesEveryPartAShipCanTake()
    {
        // A ship takes FTL drives of one kind only (BoxDesign.RequireOneDriveKind), so a drive of another kind than
        // the ship's own is the only part it may leave out.
        var pod = Content.Pod;
        var carried = new HashSet<string>();
        foreach (var box in pod.Boxes) carried.Add(box.Type.Id);
        foreach (var mount in pod.Mounts) carried.Add(mount.Type.Id);
        foreach (var type in Content.Blocks.Types)
        {
            if (!type.FitsClass(HullClass.Ship) || (type.Ftl != FtlKind.None && type.Ftl != pod.Flight.Ftl)) continue;
            Assert.True(carried.Contains(type.Id), $"the shipped ship has no {type.Name} ({type.Id})");
        }

        Assert.NotEqual(FtlKind.None, pod.Flight.Ftl);
        Assert.Contains(pod.Boxes.ToArray(), box => box.Shape != BlockShape.Box);
    }

    [Fact]
    public void EveryTractorBeamReachesARackAndEveryRackIsReachable()
    {
        var pod = Content.Pod;
        Assert.NotEmpty(pod.Tractors);
        var reached = new HashSet<int>();
        foreach (var tractor in pod.Tractors)
        {
            var any = false;
            for (var block = 0; block < pod.BlockCount; block++)
            {
                if (pod.Boxes[block].Type.Cargo <= 0) continue;
                if (!TractorReach.Rack(pod.Boxes, pod.Mounts, pod.FullSet, pod.BlockSize, tractor, block, out _, out _)) continue;
                any = true;
                reached.Add(block);
            }

            Assert.True(any, $"tractor beam {tractor} reaches no rack");
        }

        for (var block = 0; block < pod.BlockCount; block++)
            if (pod.Boxes[block].Type.Cargo > 0)
                Assert.True(reached.Contains(block), $"no tractor beam reaches rack {block}");
    }

    [Fact]
    public void EveryTractorBeamSeesTheHoppersIntakeAndItsBinsHoldSeveralChunks()
    {
        var pod = Content.Pod;
        Assert.Single(pod.Hoppers);
        Assert.Equal(4, pod.Bins.Length);
        Assert.NotEmpty(pod.Tractors);
        foreach (var tractor in pod.Tractors)
        {
            Assert.True(TractorReach.Intake(pod.Boxes, pod.Mounts, pod.FullSet, pod.BlockSize, tractor, pod.Hoppers[0], out _),
                $"tractor mount {tractor} does not see the hopper's intake");
            // A chunk of the mining rules' mark at a tenth ore, the leanest worth a word, is led there and fits the intake's path.
            var half = OreChunks.Half(Content.Mining, Content.Mining.ChunkOreKg * 10);
            Assert.True(TractorPath.TryGuideIntake(pod, pod.FullSet, tractor, pod.Hoppers[0], half, Content.Cargo.Tractor.StagingMarginMetres, out var guide));
            Assert.True(TractorReach.Container(pod.Boxes, pod.FullSet, pod.BlockSize, pod.Mounts[tractor], guide.Staging), "the beam does not see the point outside the intake");
        }

        // The hopper's hatch-like intake is not mounted over, and the bins together hold at least six such chunks' ore.
        var capacity = 0.0;
        foreach (var bin in pod.Bins) capacity += bin.Capacity;
        Assert.True(capacity >= 6 * Content.Mining.ChunkOreKg * 10, $"the bins hold {capacity:0} kg");
        output.WriteLine($"SHIP ore: {pod.Hoppers.Length} hopper, {pod.Bins.Length} bins, {capacity / 1000:0.0} t of raw ore, {capacity / 1000 / pod.Flight.Mass * 100:0} % of the ship's mass when full");
    }

    [Fact]
    public void EveryDroneBayHatchOpensOnClearSpace()
    {
        var pod = Content.Pod;
        Assert.NotEmpty(pod.Bays);
        foreach (var bay in pod.Bays)
            Assert.True(DroneBayRules.HatchClear(pod, pod.FullSet, bay, Content.Drones.HatchClearanceMetres, Content.Rules.CollisionTreeVisits),
                $"bay {bay}'s hatch ({pod.Boxes[bay].Facing}) is blocked");
    }

    [Fact]
    public void ItsTwoMiningLasersFireStraightAheadTogetherAndTheGeneratorsAlmostCarryThem()
    {
        // Both lasers are in action group 1 and can fire along the nose: within their gimbal and clear of the ship.
        var pod = Content.Pod;
        Assert.Equal(2, pod.Lasers.Length);
        var draw = 0f;
        foreach (var m in pod.Lasers)
        {
            var mount = pod.Mounts[m];
            var spec = mount.Type.Laser!;
            Assert.Equal(1, mount.Group);
            LaserAim.Local(pod, m, out var grid, out var outward);
            var ahead = -Vector3.UnitZ;
            Assert.True(spec.WithinGimbal(outward, ahead), $"laser {m} cannot aim straight ahead");
            Assert.True(TractorReach.Clear(pod.Boxes, pod.FullSet, pod.BlockSize, grid, grid + ahead * spec.Range), $"laser {m}'s beam ahead crosses the ship");
            draw += spec.Power;
        }

        // At rest with the shield full, firing both takes about 7 MW more than the generators spare: the battery
        // lasts about half a minute of cutting, and flying or recharging the shield while cutting runs it down faster.
        var flight = pod.Flight;
        var net = flight.PowerGenerated - flight.IdlePower - draw;
        Assert.InRange(net, -7.5f, -6.5f);
        Assert.InRange(flight.EnergyStorage / -net, 28f, 38f);

        // Heat stops a burn before the battery does: a laser overheats in twenty seconds of firing, and has cooled
        // to the mark it fires again at before the generators have put back what the burn took.
        var laser = pod.Mounts[pod.Lasers[0]].Type.Laser!;
        var burn = 1f / laser.HeatRate;
        var rest = (1f - laser.UnlockHeat) / laser.CoolRate;
        Assert.InRange(burn, 15f, flight.EnergyStorage / -net);
        Assert.InRange(rest, 5f, burn);
        Assert.True((flight.PowerGenerated - flight.IdlePower) * rest < -net * burn, "cooling alone does not refill the battery: power still limits a long cut");
        flight.LaserPower = draw;
        var state = HullState.Spawn(Double3.Zero, flight);
        FlightModel.Step(ref state, default, flight, HullSim.Dt, out var flow);
        Assert.Equal(draw, flow.Laser);
        Assert.Equal(net, flow.Net, 4);
        output.WriteLine($"SHIP lasers: {pod.Lasers.Length} in group 1, {draw:0} MW firing, net {net:0.0} MW at rest, battery {flight.EnergyStorage / -net:0} s, overheats in {burn:0} s, fires again after {rest:0.0} s");
    }

    [Fact]
    public void ItsLargestContainerFitsAndItsFiguresAreReported()
    {
        var pod = Content.Pod;
        var flight = pod.Flight;
        var stats = pod.Stats;
        var largest = -1;
        for (var block = 0; block < pod.BlockCount; block++)
            if (pod.Boxes[block].Type.Cargo > 0)
                largest = Math.Max(largest, CargoPacking.Largest(Content.Cargo, pod.Boxes[block].Size, pod.BlockSize));
        Assert.True(largest >= 3, $"the largest container any rack takes is size {largest}");

        var degrees = flight.TurnRate * (180f / MathF.PI);
        var engines = flight.Burn[ThrustTable.Index(BlockFacing.Front, FuelKind.Bipropellant)];
        output.WriteLine($"SHIP {pod.Name}: {pod.BlockCount} blocks, {pod.Mounts.Length} mounts, {flight.Mass:0.0} t, size {stats.Size.X:0} x {stats.Size.Y:0} x {stats.Size.Z:0} m");
        output.WriteLine($"SHIP rated {flight.MaxSpeed:0} m/s (boost {flight.BoostMaxSpeed:0}), turn pitch {degrees.X:0} yaw {degrees.Y:0} roll {degrees.Z:0} deg/s");
        output.WriteLine($"SHIP push m/s2: front {flight.Push(BlockFacing.Front):0.0} back {flight.Push(BlockFacing.Back):0.0} right {flight.Push(BlockFacing.Right):0.0} left {flight.Push(BlockFacing.Left):0.0} up {flight.Push(BlockFacing.Up):0.0} down {flight.Push(BlockFacing.Down):0.0}");
        output.WriteLine($"SHIP power: generated {flight.PowerGenerated:0.00} MW, idle {flight.IdlePower:0.00}, surplus {flight.PowerGenerated - flight.IdlePower:0.00}, thrust {flight.ThrustPower:0.00} (fuel {flight.ThrustPowerByFuel.Nitrogen + flight.ThrustPowerByFuel.Hydrazine + flight.ThrustPowerByFuel.Bipropellant:0.00}), shield {flight.ShieldPower:0.00} (restart {flight.ShieldRestartPower:0.00}), battery {flight.EnergyStorage:0} MJ, jump {flight.FtlEnergy:0} MJ at {flight.FtlPower:0} MW");
        output.WriteLine($"SHIP fuel kg: nitrogen {flight.FuelCapacity.Nitrogen:0}, hydrazine {flight.FuelCapacity.Hydrazine:0}, bipropellant {flight.FuelCapacity.Bipropellant:0}; engines burn {engines:0.000} kg/s ({flight.FuelCapacity.Bipropellant / engines / 60f:0} min)");
        output.WriteLine($"SHIP shield {flight.ShieldCapacity:0}, hull {flight.HullPoints:0}, cargo {stats.CargoHold:0} units, crew {stats.Crew}, bays {pod.Bays.Length}, tractors {pod.Tractors.Length}, largest container {Content.Cargo.Sizes[largest].Name}");
    }
}
