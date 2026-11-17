using System.Numerics;
using Prospect.Engine.Net;
using Prospect.Game.Protocol;
using Prospect.Game.World;
using Xunit;

namespace Prospect.Tests;

/// <summary>
/// Laser heat and what a cold gas cloud does to heat: the one rule (<see cref="LaserHeat"/>), the boost drive's share
/// of the cold in <see cref="FlightModel"/>, the content's figures, the snapshot header's part, and a real server
/// with real clients in which a laser overheats, shuts down and fires again, in open space and inside a cold cloud.
/// The lasers' wait for an emptied battery is the same rule's, and is held here the same way.
/// </summary>
public sealed class LaserHeatTests
{
    private static readonly Vector3 Up = Vector3.UnitY;

    /// <summary>The fixtures' pod, whose three lasers are the shipped mining laser.</summary>
    private static HullDesign Pod => MiningFixtures.Content.Pod;

    private static LaserSpec Spec => Pod.Mounts[Pod.Lasers[0]].Type.Laser!;

    /// <summary>The content with a laser that overheats in two seconds of firing and cools from full in four.</summary>
    private static GameContent Quick() => MiningFixtures.Make(blocks: root =>
    {
        var laser = root["blocks"]!["miningLaser"]!["laser"]!;
        laser["heatSeconds"] = 2;
        laser["coolSeconds"] = 4;
    });

    // ---------------------------------------------------------------- the rule

    [Fact]
    public void ALaserHeatsOnlyInAStepItFiredInAndThatWasPaidForAndCoolsOtherwise()
    {
        var flight = Pod.Flight;
        var lasers = new LaserHeatState { Firing = 0b011 };
        for (var t = 0; t < 60; t++) LaserHeat.Step(ref lasers, Pod, paid: true, energy: 0f, cold: false, flight, HullSim.Dt);

        // The two asked to fire gained a second of heat; the third, which was not, none.
        Assert.Equal(Spec.HeatRate, lasers.Heat[0], 4);
        Assert.Equal(Spec.HeatRate, lasers.Heat[1], 4);
        Assert.Equal(0f, lasers.Heat[2]);
        Assert.Equal(0ul, lasers.Locked);

        // A step the hull could not pay its lasers in fires none of them: they cool, however they were asked.
        for (var t = 0; t < 30; t++) LaserHeat.Step(ref lasers, Pod, paid: false, energy: 0f, cold: false, flight, HullSim.Dt);
        Assert.Equal(Spec.HeatRate - Spec.CoolRate * 0.5f, lasers.Heat[0], 4);

        // And one that is not asked cools to nothing and stays there.
        lasers.Firing = 0;
        for (var t = 0; t < 600; t++) LaserHeat.Step(ref lasers, Pod, paid: true, energy: 0f, cold: false, flight, HullSim.Dt);
        Assert.Equal(0f, lasers.Heat[0]);
        Assert.Equal(0f, lasers.Heat[1]);
    }

    [Fact]
    public void AtFullHeatALaserLocksAndStaysShutDownUntilItHasCooledToItsMark()
    {
        var flight = Pod.Flight;
        var lasers = new LaserHeatState { Firing = 0b001 };
        var ticks = 0;
        while (!lasers.IsLocked(0) && ticks < 10_000)
        {
            LaserHeat.Step(ref lasers, Pod, true, 0f, false, flight, HullSim.Dt);
            ticks++;
        }

        // Twenty seconds of firing, to the tick or one beside it.
        Assert.InRange(ticks, 1198, 1202);
        Assert.Equal(1f, lasers.Heat[0]);
        Assert.False(lasers.IsLocked(1));

        // Still asked to fire, it only cools: a shut-down laser does not heat itself back up.
        var cooling = 0;
        while (lasers.IsLocked(0) && cooling < 10_000)
        {
            var before = lasers.Heat[0];
            LaserHeat.Step(ref lasers, Pod, true, 0f, false, flight, HullSim.Dt);
            Assert.True(lasers.Heat[0] < before, "a locked laser cools even while it is asked to fire");
            cooling++;
        }

        // It unlocks at the mark the content sets, 30 %, after the seconds that takes at its cooling rate.
        Assert.InRange(lasers.Heat[0], Spec.UnlockHeat - Spec.CoolRate * HullSim.Dt * 1.5f, Spec.UnlockHeat);
        Assert.InRange(cooling, (int)((1f - Spec.UnlockHeat) / Spec.CoolRate * 60f) - 1, (int)((1f - Spec.UnlockHeat) / Spec.CoolRate * 60f) + 2);

        // And the next step it is asked in heats it again.
        var unlocked = lasers.Heat[0];
        LaserHeat.Step(ref lasers, Pod, true, 0f, false, flight, HullSim.Dt);
        Assert.True(lasers.Heat[0] > unlocked);
    }

    [Fact]
    public void InsideAColdCloudALaserHeatsSlowerAndCoolsFasterByTheContentsFigures()
    {
        var flight = Pod.Flight;
        Assert.Equal((0.5f, 2f), (flight.ColdHeating, flight.ColdCooling));
        Assert.Equal((0.5f, 2f, 0.25f), (MiningFixtures.Content.Rules.ColdCloudHeating, MiningFixtures.Content.Rules.ColdCloudCooling, MiningFixtures.Content.Rules.ColdCloudCheckSeconds));

        var open = new LaserHeatState { Firing = 1 };
        var cold = new LaserHeatState { Firing = 1 };
        for (var t = 0; t < 120; t++)
        {
            LaserHeat.Step(ref open, Pod, true, 0f, false, flight, HullSim.Dt);
            LaserHeat.Step(ref cold, Pod, true, 0f, true, flight, HullSim.Dt);
        }

        Assert.Equal(Spec.HeatRate * 2f, open.Heat[0], 4);
        Assert.Equal(Spec.HeatRate * 2f * 0.5f, cold.Heat[0], 4);

        // From the same heat, a second of cooling sheds twice as much in the cold.
        open.Heat[0] = cold.Heat[0] = 0.8f;
        open.Firing = cold.Firing = 0;
        for (var t = 0; t < 60; t++)
        {
            LaserHeat.Step(ref open, Pod, true, 0f, false, flight, HullSim.Dt);
            LaserHeat.Step(ref cold, Pod, true, 0f, true, flight, HullSim.Dt);
        }

        Assert.Equal(0.8f - Spec.CoolRate, open.Heat[0], 4);
        Assert.Equal(0.8f - Spec.CoolRate * 2f, cold.Heat[0], 4);
    }

    [Fact]
    public void TheBoostDriveHeatsSlowerAndCoolsFasterInsideAColdCloud()
    {
        var flight = Pod.Flight;
        var input = new FlightInput { Forward = 127, Boost = true, Dampeners = true };

        float Heated(bool cold, int ticks)
        {
            var p = flight;
            p.Cold = cold;
            var state = HullState.Spawn(Double3.Zero, p);
            for (var t = 0; t < ticks; t++) FlightModel.Step(ref state, input, p, HullSim.Dt);
            Assert.True(state.Boosting, "control: the pod boosts for this long");
            return state.Heat;
        }

        Assert.Equal(flight.HeatRate, Heated(false, 60), 4);
        Assert.Equal(flight.HeatRate * flight.ColdHeating, Heated(true, 60), 4);

        float Cooled(bool cold)
        {
            var p = flight;
            p.Cold = cold;
            var state = HullState.Spawn(Double3.Zero, p);
            state.Heat = 0.9f;
            for (var t = 0; t < 60; t++) FlightModel.Step(ref state, default, p, HullSim.Dt);
            return state.Heat;
        }

        Assert.Equal(0.9f - flight.CoolRate, Cooled(false), 4);
        Assert.Equal(0.9f - flight.CoolRate * flight.ColdCooling, Cooled(true), 4);

        // The figures a readout shows are the ones the step used.
        var here = flight;
        here.Cold = true;
        Assert.Equal((flight.HeatRate * 0.5f, flight.CoolRate * 2f), (here.HeatRateHere, here.CoolRateHere));
        Assert.Equal((flight.HeatRate, flight.CoolRate), (flight.HeatRateHere, flight.CoolRateHere));
    }

    [Fact]
    public void TheFieldStepsLaserHeatWithTheHullAndTakesTheColdFromTheHullsInfo()
    {
        var design = Pod;
        using var hulls = new HullStore(4);
        var handle = hulls.Create(1, (byte)Array.IndexOf(MiningFixtures.Content.Designs, design), design, Double3.Zero);
        var field = new HullField(hulls, MiningFixtures.Content.Designs);
        ref var info = ref hulls.Info(handle.Index);
        var power = Spec.Power;

        // Asked and paid: the laser heats. The demand and the lasers that fire are prepared before each step.
        for (var t = 0; t < 60; t++)
        {
            field.BeginRound();
            info.LaserDemand = power;
            info.Lasers.Firing = 0b010;
            field.Step(handle.Index, default);
        }

        Assert.Equal(power, info.Power.Laser);
        Assert.Equal(Spec.HeatRate, info.Lasers.Heat[1], 4);
        Assert.Equal(0f, info.Lasers.Heat[0]);

        // In the cold the same second adds half as much, and the flight step was told of the cold too.
        info.Cold = true;
        for (var t = 0; t < 60; t++)
        {
            field.BeginRound();
            field.Step(handle.Index, default);
        }

        Assert.True(hulls.Flight(handle.Index).Cold);
        Assert.Equal(Spec.HeatRate * 1.5f, info.Lasers.Heat[1], 4);

        // A battery that cannot pay fires nothing, so nothing heats.
        info.Cold = false;
        hulls.State(handle.Index).Energy = 0;
        info.LaserDemand = 1000;
        var before = info.Lasers.Heat[1];
        field.BeginRound();
        field.Step(handle.Index, default);
        Assert.Equal(0f, info.Power.Laser);
        Assert.True(info.Lasers.Heat[1] < before);
    }

    [Fact]
    public void LasersTheBatteryCouldNotPayWaitUntilItHoldsItsFloorAgain()
    {
        var flight = Pod.Flight;
        var floor = flight.LaserEnergyFloor;
        Assert.Equal(flight.EnergyStorage * 0.2f, floor, 4);
        Assert.True(floor > 0f);

        // Asked and paid: nothing waits, however little the battery holds.
        var lasers = new LaserHeatState { Firing = 0b011 };
        LaserHeat.Step(ref lasers, Pod, paid: true, energy: 0f, cold: false, flight, HullSim.Dt);
        Assert.False(lasers.AwaitingPower);
        Assert.True(LaserHeat.MayFire(lasers));

        // Asked and not paid: the battery is empty, and the lasers wait. Nothing may be asked to fire meanwhile.
        LaserHeat.Step(ref lasers, Pod, paid: false, energy: 0f, cold: false, flight, HullSim.Dt);
        Assert.True(lasers.AwaitingPower);
        Assert.False(LaserHeat.MayFire(lasers));

        // The trickle of a generator does not end the wait: only the floor does, to the megajoule.
        lasers.Firing = 0;
        for (var t = 0; t < 600; t++) LaserHeat.Step(ref lasers, Pod, paid: false, energy: floor * 0.99f, cold: false, flight, HullSim.Dt);
        Assert.True(lasers.AwaitingPower);
        LaserHeat.Step(ref lasers, Pod, paid: false, energy: floor, cold: false, flight, HullSim.Dt);
        Assert.False(lasers.AwaitingPower);
        Assert.True(LaserHeat.MayFire(lasers));

        // A step that was not asked anything starts no wait, whatever the battery holds.
        LaserHeat.Step(ref lasers, Pod, paid: false, energy: 0f, cold: false, flight, HullSim.Dt);
        Assert.False(lasers.AwaitingPower);

        // Asked again on a battery that still cannot pay, the wait starts again, even with the floor reached: a
        // step either starts a wait or ends one.
        lasers.Firing = 0b001;
        LaserHeat.Step(ref lasers, Pod, paid: false, energy: floor, cold: false, flight, HullSim.Dt);
        Assert.True(lasers.AwaitingPower);

        // Lasers that wait cool like any that do not fire, and a wait neither locks nor unlocks one.
        lasers = new LaserHeatState { AwaitingPower = true };
        lasers.Heat[0] = 0.5f;
        LaserHeat.Step(ref lasers, Pod, paid: false, energy: 0f, cold: false, flight, HullSim.Dt);
        Assert.True(lasers.Heat[0] < 0.5f);
        Assert.Equal(0ul, lasers.Locked);

        // A hull with no laser has nothing that waits.
        var bare = MiningFixtures.Content.Designs.First(d => d.Lasers.Length == 0);
        var none = new LaserHeatState { Firing = 1 };
        LaserHeat.Step(ref none, bare, paid: false, energy: 0f, cold: false, bare.Flight, HullSim.Dt);
        Assert.False(none.AwaitingPower);
    }

    [Fact]
    public void TheFieldStartsTheWaitWhenTheBatteryRunsEmptyAndEndsItAtTheFloor()
    {
        var design = Pod;
        using var hulls = new HullStore(4);
        var handle = hulls.Create(1, (byte)Array.IndexOf(MiningFixtures.Content.Designs, design), design, Double3.Zero);
        var field = new HullField(hulls, MiningFixtures.Content.Designs);
        ref HullInfo Info() => ref hulls.Info(handle.Index);
        ref HullState State() => ref hulls.State(handle.Index);
        var flight = hulls.Flight(handle.Index);
        var power = Spec.Power * 2;
        Assert.True(power > flight.PowerGenerated - flight.IdlePower, "two lasers draw more than the generator spares");

        // What a plan does each tick: lasers are asked to fire only while they may.
        var fired = 0;
        bool Tick()
        {
            field.BeginRound();
            var may = LaserHeat.MayFire(Info().Lasers);
            Info().LaserDemand = may ? power : 0f;
            Info().Lasers.Firing = may ? 0b011ul : 0ul;
            field.Step(handle.Index, default);
            if (Info().Power.Laser > 0f) fired++;
            return Info().Power.Laser > 0f;
        }

        // Half a megajoule lasts two lasers a little while; then one step cannot pay and the wait starts.
        State().Shield = flight.ShieldCapacity;
        State().Energy = 0.5f;
        var ticks = 0;
        for (; ticks < 600 && !Info().Lasers.AwaitingPower; ticks++) Tick();
        Assert.InRange(ticks, 20, 60);
        Assert.Equal(ticks - 1, fired);
        Assert.Equal(0f, Info().Power.Laser);

        // While it lasts nothing fires, nothing is asked, and the battery fills at what the generator spares.
        fired = 0;
        var waited = 0;
        for (; waited < 2000 && Info().Lasers.AwaitingPower; waited++)
        {
            Assert.False(Tick());
            Assert.True(State().Energy < flight.LaserEnergyFloor + 0.2f);
        }

        Assert.Equal(0, fired);
        Assert.True(State().Energy >= flight.LaserEnergyFloor);
        var expected = flight.LaserEnergyFloor / (flight.PowerGenerated - flight.IdlePower) * 60f;
        Assert.InRange(waited, expected - 6f, expected + 6f);

        // From the floor the lasers fire again, for as long as it lasts: far longer than the trickle would give.
        var burst = 0;
        while (burst < 6000 && Tick()) burst++;
        Assert.True(Info().Lasers.AwaitingPower);
        var drain = power - (flight.PowerGenerated - flight.IdlePower);
        Assert.InRange(burst, flight.LaserEnergyFloor / drain * 60f - 8f, flight.LaserEnergyFloor / drain * 60f + 8f);
    }

    [Fact]
    public void ARebuiltHullKeepsItsLasersWaitForPower()
    {
        var from = new LaserHeatState { AwaitingPower = true, Firing = 0b1 };

        // A repair and an edit both keep the wait: rebuilding fills no battery. A hull left with no laser has none.
        Assert.True(LaserHeat.Carry(from, sameDesign: true, Pod).AwaitingPower);
        Assert.True(LaserHeat.Carry(from, sameDesign: false, Pod).AwaitingPower);
        var bare = MiningFixtures.Content.Designs.First(d => d.Lasers.Length == 0);
        Assert.False(LaserHeat.Carry(from, sameDesign: false, bare).AwaitingPower);
        Assert.False(LaserHeat.Carry(default, sameDesign: false, Pod).AwaitingPower);
    }

    [Fact]
    public void ARebuiltHullNeverCoolsItsLasers()
    {
        var from = new LaserHeatState { Firing = 0b11, Locked = 0b10 };
        from.Heat[0] = 0.4f;
        from.Heat[1] = 0.9f;

        // A repair is the same design: each laser keeps its own heat and its lock; nothing is asked to fire.
        var repaired = LaserHeat.Carry(from, sameDesign: true, Pod);
        Assert.Equal((0.4f, 0.9f, 0b10ul, 0ul), (repaired.Heat[0], repaired.Heat[1], repaired.Locked, repaired.Firing));

        // An edit may have moved the lasers: all three start as hot as the hottest, shut down as it was.
        var edited = LaserHeat.Carry(from, sameDesign: false, Pod);
        Assert.Equal(3, LaserHeat.Count(Pod));
        Assert.Equal((0.9f, 0.9f, 0.9f, 0f), (edited.Heat[0], edited.Heat[1], edited.Heat[2], edited.Heat[3]));
        Assert.Equal(0b111ul, edited.Locked);

        // Cool lasers stay cool, and a hot one that was not shut down shuts nothing down.
        Assert.Equal(default, LaserHeat.Carry(default, false, Pod).Locked);
        from.Locked = 0;
        Assert.Equal(0ul, LaserHeat.Carry(from, false, Pod).Locked);

        // A design with no laser keeps nothing.
        var bare = MiningFixtures.Content.Designs.First(d => d.Lasers.Length == 0);
        Assert.Equal(0f, LaserHeat.Carry(from, false, bare).Heat[0]);
    }

    [Fact]
    public void TheHottestOfAGroupAndWhetherAllOfItIsShutDownAreWhatItsCellShows()
    {
        var lasers = new LaserHeatState { Locked = 0b001 };
        lasers.Heat[0] = 1f;
        lasers.Heat[1] = 0.25f;
        lasers.Heat[2] = 0.5f;
        Assert.Equal(1f, LaserHeat.Hottest(lasers, 0b011, out var all));
        Assert.False(all);
        Assert.Equal(0.5f, LaserHeat.Hottest(lasers, 0b110, out all));
        Assert.False(all);
        Assert.Equal(1f, LaserHeat.Hottest(lasers, 0b001, out all));
        Assert.True(all);
        Assert.Equal(0f, LaserHeat.Hottest(lasers, 0, out all));
        Assert.False(all);
        Assert.False(lasers.IsLocked(-1));
        Assert.False(lasers.IsLocked(64));
        Assert.Equal(0f, lasers.Of(64));
    }

    [Fact]
    public void SteppingLaserHeatTakesNoManagedMemory()
    {
        var design = Pod;
        var flight = design.Flight;
        var lasers = new LaserHeatState { Firing = 0b101 };
        var probe = new AllocationProbe();
        while (probe.Run())
        {
            probe.Start();
            for (var t = 0; t < 4000; t++)
            {
                if (t % 700 == 0) lasers.Firing ^= 0b111;
                LaserHeat.Step(ref lasers, design, t % 5 != 0, t % 7, t % 3 == 0, flight, HullSim.Dt);
            }

            probe.Stop();
        }

        Assert.Equal(0, probe.Bytes);
    }

    // ---------------------------------------------------------------- content

    [Theory]
    [InlineData("heatSeconds", 0.0)]
    [InlineData("heatSeconds", 0.1)]
    [InlineData("heatSeconds", 4000.0)]
    [InlineData("coolSeconds", 0.0)]
    [InlineData("coolSeconds", 4000.0)]
    [InlineData("unlockHeat", 0.0)]
    [InlineData("unlockHeat", 1.0)]
    [InlineData("unlockHeat", -0.2)]
    public void ALasersHeatFigureOutsideItsBoundsIsRefused(string key, double value) =>
        Assert.ThrowsAny<Exception>(() => MiningFixtures.Make(blocks: root => root["blocks"]!["miningLaser"]!["laser"]![key] = value));

    [Fact]
    public void ALaserWithoutItsHeatFiguresIsRefused() =>
        Assert.ThrowsAny<Exception>(() => MiningFixtures.Make(blocks: root => root["blocks"]!["miningLaser"]!["laser"]!.AsObject().Remove("heatSeconds")));

    [Theory]
    [InlineData("coldCloudHeating", 0.0)]
    [InlineData("coldCloudHeating", 1.5)]
    [InlineData("coldCloudCooling", 0.5)]
    [InlineData("coldCloudCooling", 101.0)]
    [InlineData("coldCloudCheckSeconds", 0.0)]
    [InlineData("coldCloudCheckSeconds", 11.0)]
    public void AColdCloudFigureOutsideItsBoundsIsRefused(string key, double value) =>
        Assert.ThrowsAny<Exception>(() => MiningFixtures.Make(blocks: root => root["rules"]![key] = value));

    [Theory]
    [InlineData(-0.01)]
    [InlineData(1.01)]
    [InlineData(double.NaN)]
    public void ABatteryFloorForLasersOutsideItsBoundsIsRefused(double value) =>
        Assert.ThrowsAny<Exception>(() => MiningFixtures.Make(blocks: root => root["rules"]!["laserResumeBattery"] = value));

    [Fact]
    public void TheBatteryFloorForLasersIsSharedContentAndWithoutItLasersWaitForNoFloor()
    {
        // The shipped figure: a fifth of the battery, in megajoules of each design's own.
        Assert.Equal(Pod.Flight.EnergyStorage * 0.2f, Pod.Flight.LaserEnergyFloor, 4);
        var half = MiningFixtures.Make(blocks: root => root["rules"]!["laserResumeBattery"] = 0.5);
        Assert.Equal(half.Pod.Flight.EnergyStorage * 0.5f, half.Pod.Flight.LaserEnergyFloor, 4);
        Assert.NotEqual(MiningFixtures.Content.Identity, half.Identity);

        // Content from before the figure: no floor, so a wait ends with the first step that was asked nothing.
        var without = MiningFixtures.Make(blocks: root => root["rules"]!.AsObject().Remove("laserResumeBattery"));
        Assert.Equal(0f, without.Pod.Flight.LaserEnergyFloor);
        var lasers = new LaserHeatState { Firing = 1 };
        LaserHeat.Step(ref lasers, without.Pod, paid: false, energy: 0f, cold: false, without.Pod.Flight, HullSim.Dt);
        Assert.True(lasers.AwaitingPower);
        lasers.Firing = 0;
        LaserHeat.Step(ref lasers, without.Pod, paid: false, energy: 0f, cold: false, without.Pod.Flight, HullSim.Dt);
        Assert.False(lasers.AwaitingPower);
    }

    [Fact]
    public void TheHeatFiguresAreSharedContentAndWithoutTheColdFiguresAColdCloudChangesNothing()
    {
        // Both sides simulate from them, so a server and a client that differ in one do not pass for the same content.
        Assert.NotEqual(MiningFixtures.Content.Identity, Quick().Identity);
        Assert.NotEqual(MiningFixtures.Content.Identity, MiningFixtures.Make(blocks: root => root["rules"]!["coldCloudCooling"] = 3).Identity);

        var without = MiningFixtures.Make(blocks: root =>
        {
            var rules = root["rules"]!.AsObject();
            rules.Remove("coldCloudHeating");
            rules.Remove("coldCloudCooling");
            rules.Remove("coldCloudCheckSeconds");
        });
        Assert.Equal((1f, 1f), (without.Pod.Flight.ColdHeating, without.Pod.Flight.ColdCooling));
    }

    // ---------------------------------------------------------------- the wire

    [Fact]
    public void TheSnapshotHeaderCarriesTheOwnLasersHeatAndWhichAreShutDown()
    {
        Assert.Equal(LaserHeat.MaxLasers, ActionGroupsMessage.MaxParts);

        var lasers = new LaserHeatState { Locked = 0b101, Firing = 0b111 };
        lasers.Heat[0] = 1f;
        lasers.Heat[1] = 0.25f;
        lasers.Heat[2] = 0.3333f;
        lasers.Heat[5] = 0.7f;
        var header = new SnapshotHeader { ServerTick = 9, OwnHull = 7, HullCount = 0, OwnTankCount = 1 };
        header.OwnTanks[0] = 12f;
        header.SetLasers(lasers, 3);

        Span<byte> buffer = stackalloc byte[SnapshotHeader.MaxWireSize];
        var writer = new NetWriter(buffer);
        header.Write(ref writer);

        // A count, two bytes a laser and a byte for every eight of them.
        Assert.Equal(25 + 1 + 4 + 1 + 3 * 2 + 1, writer.Length);
        Assert.Equal(header.WireSize, writer.Length);
        var reader = new NetReader(writer.Written[1..]);
        Assert.True(SnapshotHeader.TryRead(ref reader, out var read));
        Assert.Equal(0, reader.Remaining);
        var got = read.Lasers();
        Assert.Equal(1f, got.Heat[0]);
        Assert.Equal(0.25f, got.Heat[1], 4);
        Assert.Equal(0.3333f, got.Heat[2], 4);
        Assert.Equal(0b101ul, got.Locked);

        // Only the lasers the design has heat for are sent, and nothing is asked to fire by a header.
        Assert.Equal(0f, got.Heat[5]);
        Assert.Equal(0ul, got.Firing);

        // A lock bit past the lasers sent is taken off when the header is made, and refused when it arrives.
        lasers.Locked = 0b1101;
        header.SetLasers(lasers, 3);
        Assert.Equal(0b101ul, header.OwnLasersLocked);
        header.OwnLasersLocked = 0b1101;
        writer = new NetWriter(buffer);
        header.Write(ref writer);
        reader = new NetReader(writer.Written[1..]);
        Assert.False(SnapshotHeader.TryRead(ref reader, out _));

        // More lasers than a hull has heat for are refused, and so is a header cut short in its lasers.
        header.SetLasers(lasers, 3);
        writer = new NetWriter(buffer);
        header.Write(ref writer);
        var bytes = writer.Written.ToArray();
        bytes[25 + 1 + 4] = LaserHeat.MaxLasers + 1;
        reader = new NetReader(bytes.AsSpan(1));
        Assert.False(SnapshotHeader.TryRead(ref reader, out _));
        reader = new NetReader(writer.Written[1..^1]);
        Assert.False(SnapshotHeader.TryRead(ref reader, out _));

        // Every laser a hull may have: all sixty-four bits travel, and the header fills its bound with every tank.
        var full = new LaserHeatState { Locked = ulong.MaxValue };
        for (var part = 0; part < LaserHeat.MaxLasers; part++) full.Heat[part] = part / 63f;
        header = new SnapshotHeader { ServerTick = 1, OwnHull = 1, OwnTankCount = TankFuel.MaxTanks };
        header.SetLasers(full, 200);
        Assert.Equal(LaserHeat.MaxLasers, header.OwnLaserCount);
        writer = new NetWriter(buffer);
        header.Write(ref writer);
        Assert.Equal(SnapshotHeader.MaxWireSize, writer.Length);
        reader = new NetReader(writer.Written[1..]);
        Assert.True(SnapshotHeader.TryRead(ref reader, out read));
        got = read.Lasers();
        Assert.Equal(ulong.MaxValue, got.Locked);
        Assert.Equal(1f, got.Heat[63]);
        Assert.Equal(31 / 63f, got.Heat[31], 4);
    }

    [Fact]
    public void TheSnapshotHeaderSaysTheOwnLasersWaitForPowerInItsFlagsAndGrowsByNothing()
    {
        Span<byte> buffer = stackalloc byte[SnapshotHeader.MaxWireSize];
        SnapshotHeader Round(SnapshotHeader header, out int length)
        {
            Span<byte> bytes = stackalloc byte[SnapshotHeader.MaxWireSize];
            var writer = new NetWriter(bytes);
            header.Write(ref writer);
            length = writer.Length;
            var reader = new NetReader(writer.Written[1..]);
            Assert.True(SnapshotHeader.TryRead(ref reader, out var read));
            Assert.Equal(0, reader.Remaining);
            return read;
        }

        var waiting = new LaserHeatState { AwaitingPower = true };
        waiting.Heat[1] = 0.5f;
        foreach (var drone in new[] { false, true })
        {
            var header = new SnapshotHeader { ServerTick = 3, OwnHull = 5, OwnIsDrone = drone };
            header.SetLasers(waiting, 2);
            var read = Round(header, out var length);
            Assert.True(read.OwnLasersAwaitingPower);
            Assert.True(read.Lasers().AwaitingPower);
            Assert.Equal(drone, read.OwnIsDrone);
            Assert.Equal(0.5f, read.Lasers().Heat[1], 4);

            header.SetLasers(default, 2);
            read = Round(header, out var plain);
            Assert.False(read.Lasers().AwaitingPower);
            Assert.Equal(drone, read.OwnIsDrone);
            Assert.Equal(plain, length);
        }

        // A hull with no laser has none that waits: the bit is not set for it, and one that arrives so is refused.
        var bare = new SnapshotHeader { ServerTick = 3, OwnHull = 5 };
        bare.SetLasers(waiting, 0);
        Assert.False(bare.OwnLasersAwaitingPower);
        bare.OwnLasersAwaitingPower = true;
        var bad = new NetWriter(buffer);
        bare.Write(ref bad);
        var badReader = new NetReader(bad.Written[1..]);
        Assert.False(SnapshotHeader.TryRead(ref badReader, out _));

        // Flag bits the header does not know are refused.
        var known = new SnapshotHeader { ServerTick = 3, OwnHull = 5, OwnIsDrone = true };
        known.SetLasers(waiting, 1);
        var writer = new NetWriter(buffer);
        known.Write(ref writer);
        var bytes = writer.Written.ToArray();
        Assert.Equal(3, bytes[24]);
        bytes[24] = 4;
        var reader = new NetReader(bytes.AsSpan(1));
        Assert.False(SnapshotHeader.TryRead(ref reader, out _));
    }

    // ---------------------------------------------------------------- a real server and clients

    [Fact]
    public void LasersThatRunTheBatteryEmptyShutOffAndFireAgainAtTheFloorAndTheirPilotIsToldEveryStep()
    {
        using var rig = new MiningRig();
        var design = rig.DesignOf();
        var a = MiningFixtures.PartA(design);
        var b = MiningFixtures.PartB(design);
        var spec = design.Mounts[design.Lasers[a]].Type.Laser!;
        var (rock, _) = MiningFixtures.FindRock(rig.Rocks);
        rig.Place(0, rock, Up, 25);
        var slot = rig.Slot();
        var flight = rig.Hulls.Flight(slot);
        var floor = flight.LaserEnergyFloor;
        var spare = flight.PowerGenerated - flight.IdlePower;
        Assert.True(spec.Power * 2 > spare && floor > 1f);
        ref HullInfo Info() => ref rig.Hulls.Info(slot);
        ref HullState State() => ref rig.Hulls.State(slot);

        void Agree(string when)
        {
            // The client predicts its own hull's wait with the server's rule from the last snapshot's figures; it
            // runs a few ticks ahead, so the two are compared away from the tick the wait starts or ends on.
            var snapshot = rig.Snap(0);
            Assert.Equal(rig.Own(0), snapshot.LaserHeatHull);
            Assert.True(snapshot.LaserHeat.AwaitingPower == Info().Lasers.AwaitingPower, $"{when}: the client has waiting {snapshot.LaserHeat.AwaitingPower}, the server {Info().Lasers.AwaitingPower}");
            var shown = snapshot.Hulls[snapshot.OwnHull].Energy;
            Assert.True(MathF.Abs(shown - State().Energy) < 1.5f, $"{when}: the client shows {shown:0.00} MJ, the server has {State().Energy:0.00} MJ");
        }

        // Both groups fire on a battery with a megajoule left: the two lasers draw more than the generator spares.
        State().Shield = flight.ShieldCapacity;
        State().Energy = 1f;
        rig.Step(4);
        rig.Aim(0, MiningFixtures.Mask(1, 2), Up);
        rig.Step(20);
        Assert.Equal(spec.Power * 2, Info().Power.Laser);
        Assert.False(Info().Lasers.AwaitingPower);
        Assert.Equal(2, rig.Snap(0).LaserBeamCount);
        Agree("firing");

        // It runs empty within a few seconds, and the lasers shut off: no beam, no demand, no draw, the intent held.
        var ticks = 0;
        for (; ticks < 600 && !Info().Lasers.AwaitingPower; ticks++) rig.Step();
        Assert.InRange(ticks, 10, 200);
        rig.Step(8);
        Assert.True(Info().Lasers.AwaitingPower);
        Assert.Equal(0f, Info().LaserDemand);
        Assert.Equal(0f, Info().Power.Laser);
        Assert.Equal(0ul, Info().Lasers.Firing);
        Assert.Equal(0, rig.Snap(0).LaserBeamCount);
        Assert.True(rig.Snap(0).LaserHeat.AwaitingPower, "the pilot is told the lasers wait for power");
        Agree("shut off");

        // Nothing fires and nothing is cut while the battery fills to the floor, at what the generator spares: no
        // flicker on the trickle.
        var edits = rig.Edits(rock);
        var waited = 8;
        for (; waited < 2000 && Info().Lasers.AwaitingPower; waited++)
        {
            Assert.Equal(0f, Info().Power.Laser);
            if (waited % 30 == 0) Agree("waiting");
            rig.Step();
        }

        Assert.InRange(waited, floor / spare * 60f - 12f, floor / spare * 60f + 12f);
        Assert.Equal(edits, rig.Edits(rock));
        Assert.True(State().Energy >= floor - 0.5f);

        // The intent was held all along, so they fire again on their own, and the pilot sees the beams and the drain.
        rig.Step(20);
        Assert.Equal(spec.Power * 2, Info().Power.Laser);
        Assert.False(Info().Lasers.AwaitingPower);
        Assert.Equal(2, rig.Snap(0).LaserBeamCount);
        Assert.False(rig.Snap(0).LaserHeat.AwaitingPower);
        Agree("firing again");
        rig.Step(120);
        Assert.True(rig.Edits(rock) > edits, "the lasers cut again");

        // Letting go while they wait: the wait still ends at the floor, with nothing fired.
        State().Energy = 0.2f;
        rig.Step(60);
        Assert.True(Info().Lasers.AwaitingPower);
        rig.Aim(0, 0, Up);
        rig.Step((int)(floor / spare * 60f) + 30);
        Assert.False(Info().Lasers.AwaitingPower);
        Assert.Equal(0f, Info().Power.Laser);
        Agree("released");

        // The other player is told only of the hull they fly themselves.
        Assert.Equal(rig.Own(1), rig.Snap(1).LaserHeatHull);
        Assert.False(rig.Snap(1).LaserHeat.AwaitingPower);
    }

    [Fact]
    public void AFiringLaserOverheatsShutsDownAndFiresAgainOnceCooledAndItsPilotIsToldEveryStep()
    {
        using var rig = new MiningRig(Quick());
        var design = rig.DesignOf();
        var part = MiningFixtures.PartB(design);
        var other = MiningFixtures.PartA(design);
        var spec = design.Mounts[design.Lasers[part]].Type.Laser!;
        var (rock, _) = MiningFixtures.FindRock(rig.Rocks);
        rig.Place(0, rock, Up, 25);
        var slot = rig.Slot();
        ref LaserHeatState Server() => ref rig.Hulls.Info(slot).Lasers;

        void Agree(string when)
        {
            // The client steps its own hull's laser heat with the server's rule from the last snapshot's figures:
            // what it shows is the server's heat give or take the few ticks it runs ahead and the beams it knew then.
            var snapshot = rig.Snap(0);
            Assert.Equal(rig.Own(0), snapshot.LaserHeatHull);
            Assert.True(MathF.Abs(snapshot.LaserHeat.Heat[part] - Server().Heat[part]) < 0.08f, $"{when}: the client shows {snapshot.LaserHeat.Heat[part]:0.000}, the server has {Server().Heat[part]:0.000}");
        }

        // Group 2 fires its one laser: half a second in it is about a quarter hot, and the laser not fired is cool.
        rig.Aim(0, MiningFixtures.Mask(2), Up);
        rig.Step(30);
        Assert.InRange(Server().Heat[part], 0.2f, 0.26f);
        Assert.Equal(0f, Server().Heat[other]);
        Assert.Equal(spec.Power, rig.Hulls.Info(slot).Power.Laser);
        Agree("heating");

        // It reaches full heat within its two seconds and is shut down: no beam, no demand, no draw, with the fire
        // intent still held.
        var ticks = 30;
        for (; ticks < 200 && !Server().IsLocked(part); ticks++) rig.Step();
        Assert.InRange(ticks, 115, 126);
        rig.Step(4);
        Assert.True(Server().IsLocked(part));
        Assert.Equal(0f, rig.Hulls.Info(slot).LaserDemand);
        Assert.Equal(0f, rig.Hulls.Info(slot).Power.Laser);
        Assert.Equal(0ul, Server().Firing);
        Assert.Equal(0, rig.Snap(0).LaserBeamCount);
        Assert.True(rig.Snap(0).LaserHeat.IsLocked(part), "the pilot is told the laser is shut down");
        Agree("shut down");

        // While it cools above its mark nothing fires, and nothing more is cut.
        var edits = rig.Edits(rock);
        var cooling = 0;
        for (; cooling < 400 && Server().IsLocked(part); cooling++)
        {
            Assert.Equal(0f, rig.Hulls.Info(slot).Power.Laser);
            rig.Step();
        }

        // Four seconds from full to nothing: 70 % of that to the 30 % mark.
        Assert.InRange(cooling, 160, 172);
        Assert.Equal(edits, rig.Edits(rock));
        Agree("cooled to the mark");

        // The intent was held all along, so it fires again on its own, and heats again.
        rig.Step(20);
        Assert.Equal(spec.Power, rig.Hulls.Info(slot).Power.Laser);
        Assert.True(Server().Heat[part] > spec.UnlockHeat);
        Assert.True(rig.Snap(0).LaserBeamCount > 0, "the beam is back");
        Assert.False(rig.Snap(0).LaserHeat.IsLocked(part));
        Agree("firing again");

        // Letting go cools it to nothing; the pilot's figure follows it down.
        rig.Aim(0, 0, Up);
        rig.Step(300);
        Assert.Equal(0f, Server().Heat[part]);
        Assert.Equal(0f, rig.Snap(0).LaserHeat.Heat[part]);

        // The other player is told only of the hull they fly themselves.
        Assert.Equal(rig.Own(1), rig.Snap(1).LaserHeatHull);
        Assert.Equal(0f, rig.Snap(1).LaserHeat.Heat[part]);
    }

    [Fact]
    public void InsideAColdCloudTheServerAndThePredictingClientHeatALaserHalfAsFast()
    {
        using var rig = new MiningRig(Quick());

        // The world's own galaxy: a server gives its world a seed of its own.
        var galaxy = rig.Server.Galaxy;
        var gas = galaxy.GasClouds!;
        var design = rig.DesignOf();
        var part = MiningFixtures.PartB(design);
        var slot = rig.Slot();

        // The nearest cold cloud to home, by its anchor; its centre is well inside its smooth envelope.
        GasCloudPlacement? found = null;
        for (var ring = 1; ring <= 60 && found is null; ring++)
            for (var x = -ring; x <= ring && found is null; x++)
                for (var y = -1; y <= 1 && found is null; y++)
                    for (var z = -ring; z <= ring && found is null; z++)
                    {
                        if (Math.Max(Math.Abs(x), Math.Abs(z)) != ring) continue;
                        var anchor = new SectorCoord(x, y, z);
                        if (gas.TryGetCloud(galaxy, anchor, out var cloud) && cloud.Kind == GasCloudKind.Cold) found = new GasCloudPlacement(anchor, cloud);
                    }

        Assert.NotNull(found);
        var centre = GasCloudQuery.CenterOf(galaxy, found.Value);
        Assert.True(GasSensors.Contains(galaxy, found.Value, centre));

        float Burn(Double3 position, bool cold)
        {
            ref var state = ref rig.Hulls.State(slot);
            state.Position = position;
            state.Velocity = Vector3.Zero;
            state.AngularVelocity = Vector3.Zero;
            rig.Hulls.Info(slot).Lasers = default;

            // Within the content's quarter second the server has looked, and the client has heard with a snapshot.
            rig.Step(30);
            Assert.Equal(cold, rig.Hulls.Info(slot).Cold);
            var snapshot = rig.Snap(0);
            Assert.Equal(cold, (snapshot.Hulls[snapshot.OwnHull].Environment & HullEnvironment.InColdCloud) != 0);

            rig.Aim(0, MiningFixtures.Mask(2), Up);
            rig.Step(60);
            var heat = rig.Hulls.Info(slot).Lasers.Heat[part];
            var shown = rig.Snap(0).LaserHeat.Heat[part];
            Assert.True(MathF.Abs(shown - heat) < 0.06f, $"cold {cold}: the client shows {shown:0.000}, the server has {heat:0.000}");
            rig.Aim(0, 0, Up);
            rig.Step(4);
            return heat;
        }

        // Two seconds to overheat in open space: a second of firing is about half; in the cold, about a quarter.
        var home = rig.Hulls.State(slot).Position;
        var inCloud = Burn(centre, true);
        var inOpen = Burn(home, false);
        Assert.InRange(inOpen, 0.44f, 0.5f);
        Assert.InRange(inCloud, 0.22f, 0.25f);
        Assert.Equal(inOpen * 0.5f, inCloud, 2);
    }
}
