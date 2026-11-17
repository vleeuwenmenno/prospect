using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using Prospect.Engine.Net;
using Prospect.Engine.Time;
using Prospect.Game.Client;
using Prospect.Game.Protocol;
using Prospect.Game.Server;
using Prospect.Game.World;
using Xunit;

namespace Prospect.Tests;

/// <summary>
/// Directional thrust and fuel: a part pushes the hull away from where it faces and burns its own fuel, the sums of
/// each direction follow the blocks a hull has left, and the tanks are simulation state the client predicts.
/// </summary>
public sealed class PropulsionTests
{
    private static readonly GameContent Content = GameContent.Load();

    /// <summary>Every shipped design of a ship (not a drone), by catalogue index.</summary>
    public static TheoryData<int> Ships()
    {
        var ships = new TheoryData<int>();
        for (var i = 0; i < Content.Designs.Length; i++)
            if (Content.Designs[i].Class == HullClass.Ship) ships.Add(i);
        return ships;
    }
    private static readonly GridSize One = new(1, 1, 1);

    private static readonly BlockFacing[] Directions = [BlockFacing.Right, BlockFacing.Left, BlockFacing.Up, BlockFacing.Down, BlockFacing.Back, BlockFacing.Front];

    private static DesignBox Box(string type, int x, int y, int z, BlockFacing facing = BlockFacing.Back, GridSize? size = null) =>
        new(new GridSize(x, y, z), size ?? One, Content.Blocks.Get(type), BlockShape.Box, facing);

    /// <summary>A core with a generator, a gyro, a battery, one tank of each fuel, and the thrust parts a test adds.</summary>
    private static HullDesign Design(params DesignBox[] parts)
    {
        var boxes = new List<DesignBox>
        {
            Box("core", 0, 0, 0), Box("generator", 0, 1, 0), Box("gyro", 0, 2, 0), Box("battery", 0, 3, 0),
            Box("nitrogenTank", 0, 4, 0), Box("hydrazineTank", 0, 5, 0), Box("bipropellantTank", 0, 6, 0),
        };
        boxes.AddRange(parts);
        return TestDesigns.Of([.. boxes]);
    }

    /// <summary>The same seven blocks, the parts a test adds as blocks 7 and up, and the thrusters it mounts on any of them.</summary>
    private static HullDesign Design(DesignBox[] parts, DesignMount[] mounts)
    {
        var boxes = Design(parts).Boxes.ToArray();
        return TestDesigns.Mounted(boxes, mounts);
    }

    /// <summary>A thruster mounted on a face of a block, by default in the middle of a one-cell face.</summary>
    private static DesignMount Mount(string type, int parent, BlockFacing face, int u = 3, int v = 3) =>
        new(parent, face, u, v, Content.Blocks.Get(type));

    /// <summary>The input that asks for full thrust toward a direction, dampeners off.</summary>
    private static FlightInput Toward(BlockFacing push) => push switch
    {
        BlockFacing.Right => new FlightInput { Strafe = 127 },
        BlockFacing.Left => new FlightInput { Strafe = -127 },
        BlockFacing.Up => new FlightInput { Vertical = 127 },
        BlockFacing.Down => new FlightInput { Vertical = -127 },
        BlockFacing.Back => new FlightInput { Forward = -127 },
        _ => new FlightInput { Forward = 127 },
    };

    private static HullState Fly(HullState state, FlightInput input, in FlightParams flight, int ticks)
    {
        for (var i = 0; i < ticks; i++)
            FlightModel.Step(ref state, input, flight, HullSim.Dt);
        return state;
    }

    [Fact]
    public void FacingsTurnAsShapesDoAndNameTheSideTheyPointAt()
    {
        foreach (var facing in Directions)
        {
            Assert.Equal(facing, Facings.Parse(Facings.Name(facing)));
            Assert.Equal(facing, Facings.Opposite(Facings.Opposite(facing)));
            Assert.Equal(-Facings.Vector(facing), Facings.Vector(Facings.Opposite(facing)));

            // The number of a facing is the number of the block side it points out of.
            var normal = Vector3.Zero;
            normal[(int)facing >> 1] = ((int)facing & 1) == 0 ? 1 : -1;
            Assert.Equal(normal, Facings.Vector(facing));
            for (var axis = 0; axis < 3; axis++)
            {
                // A positive right-handed quarter turn: the vector turns as a rotation matrix turns it.
                var unit = Vector3.Zero;
                unit[axis] = 1;
                var turned = Vector3.Transform(Facings.Vector(facing), Quaternion.CreateFromAxisAngle(unit, MathF.PI / 2));
                Assert.True((turned - Facings.Vector(Facings.Rotate(facing, axis))).Length() < 1e-5f, $"{facing} about {axis}");
                var four = facing;
                for (var i = 0; i < 4; i++) four = Facings.Rotate(four, axis);
                Assert.Equal(facing, four);
            }
        }

        Assert.Equal(BlockFacing.Front, (BlockFacing)5);
        Assert.False(Facings.IsValid((BlockFacing)6));
        Assert.Throws<InvalidDataException>(() => Facings.Parse("forward"));
        Assert.Equal(BlockFacing.Back, new DesignBox(One, One, null!).Facing);
    }

    [Fact]
    public void TurningAHullDoesNotTurnZeroIntoADriftAndTheTurnAgreesWithTheLibrarys()
    {
        // Flight turns the velocity into the hull's frame and back every tick: a hull at rest must stay at exactly
        // zero, whatever way it faces. (Vector3.Transform gives about 7.5e-9 for zero on .NET 10.)
        var random = new Random(7);
        for (var i = 0; i < 1000; i++)
        {
            var q = Quaternion.Normalize(new Quaternion(random.NextSingle() * 2 - 1, random.NextSingle() * 2 - 1, random.NextSingle() * 2 - 1, random.NextSingle() * 2 - 1));
            Assert.Equal(Vector3.Zero, HullRotation.Rotate(Vector3.Zero, q));
            Assert.Equal(Vector3.Zero, HullRotation.Unrotate(Vector3.Zero, q));
            var v = new Vector3(random.NextSingle() * 200 - 100, random.NextSingle() * 200 - 100, random.NextSingle() * 200 - 100);
            Assert.True((HullRotation.Rotate(v, q) - Vector3.Transform(v, q)).Length() < 1e-4f, $"{q} {v}");
            Assert.True((HullRotation.Unrotate(HullRotation.Rotate(v, q), q) - v).Length() < 1e-4f, $"{q} {v}");
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void AHullThatTurnsWithItsDampenersOnBrakesItsDriftThenFiresAndSpendsNothingAtRest(int index)
    {
        // The developer's report: at 0 m/s with the dampeners on, after steering with the mouse, parts on opposite
        // sides glowed at once and kept glowing. Steer as a mouse does (yaw, pitch and roll together, never still)
        // from a drift, then keep steering at rest.
        var design = Content.Designs[index];
        var flight = design.Flight;
        var state = HullState.Spawn(Double3.Zero, flight);
        state.Velocity = new Vector3(3f, -2f, 5f);
        for (var tick = 0; tick < 60 * 30; tick++)
        {
            var input = new FlightInput { Dampeners = true, Yaw = (sbyte)(tick % 7 - 3), Pitch = (sbyte)(tick % 5 - 2), Roll = (sbyte)(tick % 3 - 1) };
            var fuelBefore = state.Fuel;
            var energyBefore = state.Energy;
            FlightModel.Step(ref state, input, flight, HullSim.Dt, out var flow, out var fuel);
            if (tick < 60 * 5)
                continue;

            // Braked to exactly nothing within five seconds, and from then on nothing fires, burns or draws.
            Assert.Equal(Vector3.Zero, state.Velocity);
            Assert.True(state.Thrust.Equals(default(ThrustState)), $"{design.Name} tick {tick}: thrust {state.Thrust.X} {state.Thrust.Y} {state.Thrust.Z}, flags {state.Thrust.Flags}");
            Assert.Equal(default, fuel.Burn);
            Assert.Equal(0f, flow.Thrust);
            Assert.Equal(fuelBefore, state.Fuel);
            Assert.True(state.Energy >= energyBefore, $"{design.Name} tick {tick}: the battery fell");
        }
    }

    [Fact]
    public void TheDampenersTakeADriftUnderTheRestSpeedToNothingWithoutFiringAndBrakeOneAboveIt()
    {
        var flight = Content.Designs[Content.DroneDesign].Flight;
        var state = HullState.Spawn(Double3.Zero, flight);
        state.Velocity = new Vector3(FlightModel.RestSpeed * 0.5f, 0, -FlightModel.RestSpeed * 0.5f);
        FlightModel.Step(ref state, new FlightInput { Dampeners = true }, flight, HullSim.Dt, out var flow);
        Assert.Equal(Vector3.Zero, state.Velocity);
        Assert.True(state.Thrust.Equals(default(ThrustState)));
        Assert.Equal(0f, flow.Thrust);

        // Twice the rest speed is drift: the parts that oppose it brake it, and show it.
        state.Velocity = new Vector3(FlightModel.RestSpeed * 2f, 0, 0);
        FlightModel.Step(ref state, new FlightInput { Dampeners = true }, flight, HullSim.Dt, out flow);
        Assert.Equal(Vector3.Zero, state.Velocity);
        Assert.Equal(-1, state.Thrust.X);
        Assert.True(state.Thrust.Ion);
        Assert.True(flow.Thrust > 0f);

        // With the dampeners off, a drift that small is left alone.
        state.Velocity = new Vector3(FlightModel.RestSpeed * 0.5f, 0, 0);
        FlightModel.Step(ref state, default, flight, HullSim.Dt);
        Assert.Equal(FlightModel.RestSpeed * 0.5f, state.Velocity.X, 6);
    }

    /// <summary>A core, a gyro, a battery and the parts a test adds, with no tank at all.</summary>
    private static HullDesign Tankless(params DesignBox[] parts) =>
        TestDesigns.Of([Box("core", 0, 0, 0), Box("gyro", 0, 2, 0), Box("battery", 0, 3, 0), .. parts]);

    [Fact]
    public void AnIonPartPushesOnPowerAloneAndBurnsNoFuel()
    {
        var ion = Content.Blocks.Get("ionDrive");
        Assert.True(ion.Ion);
        Assert.Equal(FuelKind.None, ion.Fuel);
        var design = Tankless(Box("generator", 0, 1, 0), Box("ionDrive", 1, 0, 0, BlockFacing.Back));
        var flight = design.Flight;

        // Its thrust sits in the ion column of the direction it pushes, its power under that direction, and it
        // asks for no tank and burns nothing.
        Assert.True(flight.HasIon);
        Assert.Equal(ion.Thrust / flight.Mass, flight.Push(BlockFacing.Front), 4);
        Assert.Equal(ion.Thrust / flight.Mass, flight.Thrust[ThrustTable.IonIndex(BlockFacing.Front)], 4);
        Assert.Equal(ion.PowerDraw, flight.IonPower[BlockFacing.Front], 5);
        Assert.Equal(0f, flight.IonPower[BlockFacing.Back]);
        Assert.Equal(ion.PowerDraw, flight.ThrustPower, 5);
        Assert.Equal(default, flight.FuelCapacity);
        Assert.Equal(default, flight.ThrustPowerByFuel);
        for (var i = 0; i < Facings.Count * ThrustTable.Kinds; i++)
            Assert.Equal(0f, flight.Burn[i]);
        Assert.Equal(FuelKind.None, HullDesign.MissingTank(flight));

        // A second of full thrust: the full push, no fuel, its power drawn, the parts shown firing.
        var state = HullState.Spawn(Double3.Zero, flight);
        state = Fly(state, Toward(BlockFacing.Front), flight, 59);
        FlightModel.Step(ref state, Toward(BlockFacing.Front), flight, HullSim.Dt, out var flow, out var fuel);
        Assert.Equal(flight.Push(BlockFacing.Front), -state.Velocity.Z, 2);
        Assert.Equal(default, state.Fuel);
        Assert.Equal(default, fuel.Burn);
        Assert.Equal(ion.PowerDraw, flow.Thrust, 5);
        Assert.True(state.Thrust.Ion);
        Assert.True(state.Thrust.Burning(FuelKind.None));
        Assert.False(state.Thrust.Burning(FuelKind.Bipropellant));
        Assert.Equal(1f, state.Thrust.Firing(FuelKind.None, BlockFacing.Back));
        Assert.Equal(0f, state.Thrust.Firing(FuelKind.Bipropellant, BlockFacing.Back));
        Assert.Equal(flight.EnergyStorage, state.Energy, 3);

        // The dampeners brake with it and pay for what they use: a braking tick that needs part of the thrust
        // draws that part of the power.
        state = HullState.Spawn(Double3.Zero, flight);
        state.Velocity = new Vector3(0, 0, 0.05f);
        FlightModel.Step(ref state, new FlightInput { Dampeners = true }, flight, HullSim.Dt, out flow, out _);
        Assert.Equal(0f, state.Velocity.Z, 5);
        var share = 0.05f / (flight.Push(BlockFacing.Front) * HullSim.Dt);
        Assert.Equal(ion.PowerDraw * share, flow.Thrust, 3);
        Assert.True(state.Thrust.Ion);
    }

    [Fact]
    public void BoostWithDryTanksNeitherRunsNorHeatsTheDrive()
    {
        // The pod with every tank empty: holding boost and forward pushes nothing, so the drive stays cold.
        var flight = Content.Pod.Flight;
        var state = HullState.Spawn(Double3.Zero, flight);
        state.Fuel = default;
        var input = new FlightInput { Forward = 127, Boost = true };
        state = Fly(state, input, flight, 120);
        Assert.Equal(0f, state.Heat);
        Assert.False(state.Boosting);

        // With fuel the same keys boost and heat it, so the rule above is not just boost never running.
        var fuelled = Fly(HullState.Spawn(Double3.Zero, flight), input, flight, 120);
        Assert.True(fuelled.Heat > 0f);
    }

    [Fact]
    public void AnIonPartWithoutPowerGivesNoThrustAndOneShortOfPowerIsCutBack()
    {
        // No generator: the battery runs flat, then neither the pilot nor the dampeners can push.
        var design = Tankless(Box("ionDrive", 1, 0, 0, BlockFacing.Back));
        var flight = design.Flight;
        var state = HullState.Spawn(Double3.Zero, flight);
        var ticks = 0;
        while (state.Energy > 0f && ticks++ < 60 * 600)
            FlightModel.Step(ref state, Toward(BlockFacing.Front), flight, HullSim.Dt);
        Assert.Equal(0f, state.Energy);
        var speed = state.Velocity;
        state = Fly(state, Toward(BlockFacing.Front), flight, 60);
        Assert.Equal(speed, state.Velocity);
        Assert.False(state.Thrust.Ion);
        Assert.Equal(0f, state.Thrust.Firing(FuelKind.None, BlockFacing.Back));
        state = Fly(state, new FlightInput { Dampeners = true }, flight, 60);
        Assert.Equal(speed, state.Velocity);
        Assert.Equal(default, state.Fuel);

        // A generator that cannot feed two drives: once the battery is flat, thrust is what it can feed.
        var two = Tankless(Box("generator", 0, 1, 0), Box("ionDrive", 1, 0, 0, BlockFacing.Back), Box("ionDrive", 1, 1, 0, BlockFacing.Back)).Flight;
        Assert.True(two.IonPower[BlockFacing.Front] + two.IdlePower > two.PowerGenerated);
        state = HullState.Spawn(Double3.Zero, two);
        ticks = 0;
        while (state.Energy > 0f && ticks++ < 60 * 600)
            FlightModel.Step(ref state, Toward(BlockFacing.Front), two, HullSim.Dt);
        Assert.Equal(0f, state.Energy);
        // From rest, so the rated speed does not cap the step.
        state.Velocity = Vector3.Zero;
        var before = state.Velocity.Z;
        FlightModel.Step(ref state, Toward(BlockFacing.Front), two, HullSim.Dt, out var flow);
        var scale = (two.PowerGenerated - two.IdlePower) / two.IonPower[BlockFacing.Front];
        Assert.Equal(two.Push(BlockFacing.Front) * scale * HullSim.Dt, before - state.Velocity.Z, 4);
        Assert.Equal(two.PowerGenerated - two.IdlePower, flow.Thrust, 3);
        Assert.True(state.Thrust.Ion);
    }

    [Fact]
    public void AHullWithoutTanksStepsTheSameWithItsOwnEmptyTankListAndWithThePerFuelFigures()
    {
        // The server steps a hull over its design's tanks, which for the drone are none; a client flying a hull it
        // only knows the motion of steps it from the three per-fuel figures. The two must agree to the bit.
        var drone = Content.Designs[Content.DroneDesign];
        Assert.True(drone.Tanks.IsEmpty);
        var flight = drone.Flight;
        var a = HullState.Spawn(Double3.Zero, flight);
        var b = a;
        var inputs = new[]
        {
            new FlightInput { Forward = 127, Strafe = 90, Boost = true },
            new FlightInput { Vertical = -127, Yaw = 40, Dampeners = true },
            new FlightInput { Dampeners = true },
        };
        for (var tick = 0; tick < 60 * 30; tick++)
        {
            var input = inputs[tick / 600];
            FlightModel.Step(ref a, input, flight, HullSim.Dt, out var flowA, out var fuelA);
            FlightModel.Step(ref b, input, flight, HullSim.Dt, Span<double>.Empty, ReadOnlySpan<DesignTank>.Empty, out var flowB, out var fuelB);
            Assert.Equal(a.Position, b.Position);
            Assert.Equal(a.Velocity, b.Velocity);
            Assert.Equal(a.Energy, b.Energy);
            Assert.Equal(a.Thrust, b.Thrust);
            Assert.Equal(flowA.Thrust, flowB.Thrust);
            Assert.Equal(fuelA.Burn, fuelB.Burn);
        }
    }

    [Fact]
    public void TheDroneIsElectricHoldsForwardOnItsGeneratorAndRunsItsBatteryDownWithMore()
    {
        var drone = Content.Designs[Content.DroneDesign];
        var flight = drone.Flight;
        Assert.True(drone.Tanks.IsEmpty);
        Assert.Equal(default, flight.FuelCapacity);
        Assert.Equal(default, flight.ThrustPowerByFuel);
        for (var kind = FuelKind.Nitrogen; kind <= FuelKind.Bipropellant; kind++)
            Assert.False(flight.Burns(kind));
        foreach (var push in Directions)
        {
            Assert.True(flight.Thrust[ThrustTable.IonIndex(push)] > 0f, $"no ion part pushes {push}");
            Assert.Equal(flight.Thrust[ThrustTable.IonIndex(push)], flight.Push(push));
        }

        // Full forward thrust for a minute: the generator carries it and the battery stays full.
        var state = HullState.Spawn(Double3.Zero, flight);
        state = Fly(state, Toward(BlockFacing.Front), flight, 60 * 60);
        Assert.Equal(flight.EnergyStorage, state.Energy, 3);
        Assert.True(state.Velocity.Length() > 100f);

        // Forward, sideways and up at once runs it down, faster still with the shield recharging, and boost
        // empties it in seconds.
        static float Empties(in FlightParams flight, FlightInput input, bool shield)
        {
            var state = HullState.Spawn(Double3.Zero, flight);
            if (shield)
                state.Shield = 0f;
            for (var tick = 1; tick <= 60 * 600; tick++)
            {
                FlightModel.Step(ref state, input, flight, HullSim.Dt);
                if (shield)
                    state.Shield = 0f;
                if (state.Energy <= 0f)
                    return tick / 60f;
            }
            return float.PositiveInfinity;
        }

        var all = new FlightInput { Forward = 127, Strafe = 127, Vertical = 127 };
        var three = Empties(flight, all, false);
        var strafing = Empties(flight, new FlightInput { Forward = 127, Strafe = 127 }, true);
        var boost = Empties(flight, new FlightInput { Forward = 127, Boost = true }, false);
        Console.WriteLine($"DRONE ion: forward {flight.Push(BlockFacing.Front):0.00} m/s2, sideways {flight.Push(BlockFacing.Right):0.00}, braking {flight.Push(BlockFacing.Back):0.00}; three axes empty the battery in {three:0.0} s, forward and sideways with the shield recharging in {strafing:0.0} s, boost in {boost:0.0} s");
        Assert.True(three is > 10f and < 120f, $"three axes: {three} s");
        Assert.True(strafing is > 5f and < 60f, $"forward, sideways and shield: {strafing} s");
        Assert.True(boost < 6f, $"boost: {boost} s");
        Assert.True(Empties(flight, Toward(BlockFacing.Front), true) > 20f);
    }

    [Fact]
    public void EveryPartPushesAwayFromWhereItFacesAndTheSumsKeepEachDirectionAndFuelApart()
    {
        // One engine facing back, a hot thruster mounted facing right and cold gas thrusters facing every other
        // way: on the battery, the gyro, the top tank, under the core, and three on front faces.
        var design = Design(
            [Box("engine", 1, 0, 0, BlockFacing.Back)],
            [
                Mount("hotThruster", 3, BlockFacing.Right),
                Mount("coldGasThruster", 2, BlockFacing.Left),
                Mount("coldGasThruster", 6, BlockFacing.Up),
                Mount("coldGasThruster", 0, BlockFacing.Down),
                Mount("coldGasThruster", 1, BlockFacing.Front, 1, 1),
                Mount("coldGasThruster", 1, BlockFacing.Front, 5, 1),
                Mount("coldGasThruster", 2, BlockFacing.Front),
            ]);
        var flight = design.Flight;
        var mass = flight.Mass;
        var engine = Content.Blocks.Get("engine");
        var hot = Content.Blocks.Get("hotThruster");
        var cold = Content.Blocks.Get("coldGasThruster");

        Assert.Equal(engine.Thrust / mass, flight.Push(BlockFacing.Front), 4);
        Assert.Equal(hot.Thrust / mass, flight.Push(BlockFacing.Left), 4);
        Assert.Equal(cold.Thrust / mass, flight.Push(BlockFacing.Right), 4);
        Assert.Equal(cold.Thrust / mass, flight.Push(BlockFacing.Down), 4);
        Assert.Equal(cold.Thrust / mass, flight.Push(BlockFacing.Up), 4);
        Assert.Equal(cold.Thrust * 3 / mass, flight.Push(BlockFacing.Back), 4);

        // Each figure sits under its own direction and fuel, and nowhere else.
        Assert.Equal(engine.Thrust / mass, flight.Thrust[ThrustTable.Index(BlockFacing.Front, FuelKind.Bipropellant)], 4);
        Assert.Equal(0f, flight.Thrust[ThrustTable.Index(BlockFacing.Front, FuelKind.Nitrogen)]);
        Assert.Equal(hot.Thrust / mass, flight.Thrust[ThrustTable.Index(BlockFacing.Left, FuelKind.Hydrazine)], 4);
        Assert.Equal(engine.FuelBurn, flight.Burn[ThrustTable.Index(BlockFacing.Front, FuelKind.Bipropellant)], 6);
        Assert.Equal(hot.FuelBurn, flight.Burn[ThrustTable.Index(BlockFacing.Left, FuelKind.Hydrazine)], 6);
        Assert.Equal(cold.FuelBurn * 3, flight.Burn[ThrustTable.Index(BlockFacing.Back, FuelKind.Nitrogen)], 6);
        Assert.Equal(new FuelTanks { Nitrogen = cold.PowerDraw * 6, Hydrazine = hot.PowerDraw, Bipropellant = engine.PowerDraw }, flight.ThrustPowerByFuel);
        Assert.Equal(cold.PowerDraw * 6 + hot.PowerDraw + engine.PowerDraw, flight.ThrustPower, 5);
        Assert.Equal(
            new FuelTanks { Nitrogen = Content.Blocks.Get("nitrogenTank").FuelCapacity, Hydrazine = Content.Blocks.Get("hydrazineTank").FuelCapacity, Bipropellant = Content.Blocks.Get("bipropellantTank").FuelCapacity },
            flight.FuelCapacity);

        // The data sheet reads the same directions, and the rated speed follows the strongest of them.
        Assert.Equal(flight.Push(BlockFacing.Front), design.Stats.Acceleration);
        Assert.Equal(flight.Push(BlockFacing.Back), design.Stats.Deceleration);
        Assert.Equal(cold.Thrust / mass, design.Stats.LateralAcceleration, 4);
        Assert.Equal(Content.Rules.SpeedPerRootAcceleration * MathF.Sqrt(flight.StrongestPush), flight.MaxSpeed, 3);
        Assert.Equal(flight.Push(BlockFacing.Front), flight.StrongestPush);

        // A mounted thruster points out of the face it sits on, whichever face that is, and pushes only the other way.
        foreach (var face in Directions)
        {
            var one = Design([Box("hull", 3, 0, 0)], [Mount("coldGasThruster", 7, face)]).Flight;
            foreach (var push in Directions)
                Assert.Equal(push == Facings.Opposite(face) ? cold.Thrust / one.Mass : 0f, one.Push(push), 4);
        }

        // An engine turned a quarter about the nose pushes the way it now faces.
        var turned = Design(Box("engine", 1, 0, 0, Facings.Rotate(BlockFacing.Right, 2))).Flight;
        Assert.Equal(engine.Thrust / turned.Mass, turned.Push(BlockFacing.Down), 4);
        Assert.Equal(0f, turned.Push(BlockFacing.Front));
    }

    [Fact]
    public void LosingAPartTakesItsDirectionAndLosingATankTakesItsRoomAndWhatItHeld()
    {
        // Two blocks of plating carry a braking thruster each: a mounted part is there exactly while its block is.
        var design = Design(
            [Box("engine", 1, 0, 0), Box("hull", 2, 0, 0), Box("hull", 3, 0, 0), Box("nitrogenTank", 4, 0, 0)],
            [Mount("coldGasThruster", 8, BlockFacing.Front), Mount("coldGasThruster", 9, BlockFacing.Front)]);
        using var hulls = new HullStore();
        var field = new HullField(hulls, [design]);
        var hull = hulls.Create(1, 0, design, Double3.Zero).Index;
        var states = hulls.BlockStates;
        var handle = hulls.Info(hull).BlockState;
        var intact = design.Flight;
        var tank = Content.Blocks.Get("nitrogenTank").FuelCapacity;
        Assert.Equal(tank * 2, hulls.State(hull).Fuel.Nitrogen);
        Assert.Equal(intact.FuelCapacity, hulls.State(hull).Fuel);

        // One of the two blocks goes, and its braking thruster with it: half the braking, the same forward push over less mass.
        var thruster = design.BlockCount - 3;
        Assert.True(states.Lose(handle, thruster));
        field.Refit(hull);
        var flight = hulls.Flight(hull);
        var cold = Content.Blocks.Get("coldGasThruster");
        Assert.Equal(cold.Thrust / flight.Mass, flight.Push(BlockFacing.Back), 4);
        Assert.Equal(cold.FuelBurn, flight.Burn[ThrustTable.Index(BlockFacing.Back, FuelKind.Nitrogen)], 6);
        Assert.Equal(Content.Blocks.Get("engine").Thrust / flight.Mass, flight.Push(BlockFacing.Front), 4);
        Assert.Equal(intact.FuelCapacity, flight.FuelCapacity);
        Assert.Equal(intact.FuelCapacity, hulls.State(hull).Fuel);

        // A tank goes: its room goes with it, and so does what it held, however little the other tank holds.
        Assert.Equal(4, design.Tanks.Length);
        Assert.Equal((design.BlockCount - 1, FuelKind.Nitrogen, tank), (design.Tanks[3].Block, design.Tanks[3].Kind, design.Tanks[3].Capacity));
        Assert.Equal(3, design.TankOf(design.BlockCount - 1));
        Assert.Equal(-1, design.TankOf(design.BlockCount - 2));
        states.TankLevels(handle)[0] = tank * 0.25;
        hulls.State(hull).Fuel = TankFuel.Total(states.Tanks(handle), design.Tanks);
        Assert.Equal(tank * 1.25f, hulls.State(hull).Fuel.Nitrogen);
        Assert.True(states.Lose(handle, design.BlockCount - 1));
        field.Refit(hull);
        Assert.Equal(tank, hulls.Flight(hull).FuelCapacity.Nitrogen);
        Assert.Equal(0.0, states.Tanks(handle)[3]);
        Assert.Equal(tank * 0.25, states.Tanks(handle)[0]);
        Assert.Equal(tank * 0.25f, hulls.State(hull).Fuel.Nitrogen);
        Assert.Equal(intact.FuelCapacity.Bipropellant, hulls.State(hull).Fuel.Bipropellant);

        // A block that is no tank takes no fuel with it.
        Assert.True(states.Lose(handle, design.BlockCount - 2));
        field.Refit(hull);
        Assert.Equal(0f, hulls.Flight(hull).Push(BlockFacing.Back));
        Assert.Equal(tank * 0.25f, hulls.State(hull).Fuel.Nitrogen);

        // The last tank of a fuel goes: none of it is left, and no part that burns it fires.
        Assert.True(states.Lose(handle, 4));
        field.Refit(hull);
        Assert.Equal(0f, hulls.State(hull).Fuel.Nitrogen);
        Assert.Equal(0f, hulls.Flight(hull).FuelCapacity.Nitrogen);
        Assert.Equal([0.0, design.Tanks[1].Capacity, design.Tanks[2].Capacity, 0.0], states.Tanks(handle).ToArray());

        // The sums agree with a scan of what is left, whatever order the blocks went in.
        Figures.Reference(design, Content.Rules, states.Alive(handle), out var expected, out var expectedStats);
        design.Figures(states.Sums(handle), out var figures, out var stats);
        Figures.AssertClose(expected, figures);
        Figures.AssertClose(expectedStats, stats);
        var fresh = new HullSum[design.Sums.NodeCount];
        design.Sums.Build(fresh, states.Alive(handle));
        Assert.True(Figures.SameSums(fresh, states.Sums(handle)));
    }

    [Theory]
    [MemberData(nameof(Ships))]
    public void EveryShippedDesignFliesAllSixWaysOnItsOwnTanksAndHasFuelForEveryPart(int index)
    {
        var design = Content.Designs[index];
        var flight = design.Flight;
        Assert.Equal(FuelKind.None, HullDesign.MissingTank(flight));
        Assert.True(flight.WeakestPush > 0f, $"{design.Name} cannot be pushed some way");
        foreach (var push in Directions)
        {
            var state = Fly(HullState.Spawn(Double3.Zero, flight), Toward(push), flight, 60);
            var along = Vector3.Dot(state.Velocity, Facings.Vector(push));
            Assert.Equal(flight.Push(push), along, 2);
            Assert.True(along > 5f, $"{design.Name} gained {along} m/s toward {push} in a second");
            Assert.Equal(along, state.Velocity.Length(), 3);
            Assert.True(state.Fuel.Nitrogen < flight.FuelCapacity.Nitrogen || state.Fuel.Bipropellant < flight.FuelCapacity.Bipropellant);
        }

        // Engines face back on every shipped design: forward is the strongest way, and braking is the thrusters'.
        Assert.Equal(flight.StrongestPush, flight.Push(BlockFacing.Front));
        Assert.Equal(0f, flight.Thrust[ThrustTable.Index(BlockFacing.Back, FuelKind.Bipropellant)]);
        Assert.True(flight.Thrust[ThrustTable.Index(BlockFacing.Back, FuelKind.Nitrogen)] > 0f);
    }

    [Theory]
    [MemberData(nameof(Ships))]
    public void ShippedTanksHoldMoreThanAnHourOfFullThrustAndBoostingWheneverItBurnsThemInUnderThreeFifthsOfThatTime(int index)
    {
        var design = Content.Designs[index];
        var flight = design.Flight;

        // Full thrust in any one direction, without boost: what the tanks hold over what that direction burns.
        foreach (var push in Directions)
        {
            for (var fuel = FuelKind.Nitrogen; fuel <= FuelKind.Bipropellant; fuel++)
            {
                var burn = flight.Burn[ThrustTable.Index(push, fuel)];
                if (burn > 0f)
                    Assert.True(flight.FuelCapacity[fuel] / burn >= 3600f, $"{design.Name}: {fuel} toward {push} lasts {flight.FuelCapacity[fuel] / burn / 60f:0} min");
            }
        }

        // Thrusters burn far less than engines, part for part.
        var engine = flight.Burn[ThrustTable.Index(BlockFacing.Front, FuelKind.Bipropellant)];
        var side = flight.Burn[ThrustTable.Index(BlockFacing.Right, FuelKind.Nitrogen)];
        Assert.True(engine > 0f && side > 0f && side < engine * 0.25f, $"{design.Name}: engines burn {engine} kg/s, side thrusters {side} kg/s");

        // Engines at full with boost held for as long as the drive lets it run, the battery kept charged: ten
        // minutes of it, measured, against ten minutes without.
        var plain = HullState.Spawn(Double3.Zero, flight);
        var boosted = plain;
        for (var tick = 0; tick < 600 * 60; tick++)
        {
            plain.Energy = boosted.Energy = flight.EnergyStorage;
            FlightModel.Step(ref plain, new FlightInput { Forward = 127 }, flight, HullSim.Dt);
            FlightModel.Step(ref boosted, new FlightInput { Forward = 127, Boost = true }, flight, HullSim.Dt);
        }

        var plainMinutes = flight.FuelCapacity.Bipropellant / ((flight.FuelCapacity.Bipropellant - plain.Fuel.Bipropellant) / 10f);
        var boostMinutes = flight.FuelCapacity.Bipropellant / ((flight.FuelCapacity.Bipropellant - boosted.Fuel.Bipropellant) / 10f);
        Assert.InRange(plainMinutes, 60f, 120f);
        Assert.InRange(boostMinutes, 25f, plainMinutes * 0.6f);
        Console.WriteLine($"FUEL {design.Name}: engines {plainMinutes:0.0} min at full thrust, {boostMinutes:0.0} min boosting whenever the drive allows");
    }

    [Fact]
    public void ThrustBurnsItsOwnFuelByTheThrottleAndBoostBurnsMoreForTheThrustItAdds()
    {
        var flight = Content.Pod.Flight;
        var engine = flight.Burn[ThrustTable.Index(BlockFacing.Front, FuelKind.Bipropellant)];
        var thruster = flight.Burn[ThrustTable.Index(BlockFacing.Right, FuelKind.Nitrogen)];

        // Forward at full: the engine's fuel and nothing else, exactly its rate, reported as it is spent.
        var state = HullState.Spawn(Double3.Zero, flight);
        FlightModel.Step(ref state, new FlightInput { Forward = 127 }, flight, HullSim.Dt, out _, out var fuel);
        Assert.Equal(engine, fuel.Burn.Bipropellant, 6);
        Assert.Equal(0f, fuel.Burn.Nitrogen);
        Assert.Equal(flight.FuelCapacity.Bipropellant - engine * HullSim.Dt, state.Fuel.Bipropellant, 4);
        Assert.Equal(flight.FuelCapacity.Nitrogen, state.Fuel.Nitrogen);
        Assert.Equal(state.Fuel, fuel.Amount);
        Assert.Equal(flight.FuelCapacity, fuel.Capacity);

        // Half throttle sideways and a quarter up: the two thrusters' rates by their throttles, no engine.
        state = HullState.Spawn(Double3.Zero, flight);
        FlightModel.Step(ref state, new FlightInput { Strafe = 64, Vertical = -32 }, flight, HullSim.Dt, out _, out fuel);
        Assert.Equal(thruster * (64 + 32) / 127f, fuel.Burn.Nitrogen, 6);
        Assert.Equal(0f, fuel.Burn.Bipropellant);

        // Boost: twice the thrust for the content's multiple of the fuel.
        var plain = HullState.Spawn(Double3.Zero, flight);
        var boosted = plain;
        FlightModel.Step(ref plain, new FlightInput { Forward = 127 }, flight, HullSim.Dt);
        FlightModel.Step(ref boosted, new FlightInput { Forward = 127, Boost = true }, flight, HullSim.Dt, out _, out fuel);
        Assert.True(boosted.Boosting);
        Assert.Equal(engine * Content.Rules.BoostFuelMultiplier, fuel.Burn.Bipropellant, 5);
        Assert.Equal(plain.Velocity.Length() * flight.BoostMultiplier, boosted.Velocity.Length(), 5);
        Assert.Equal(4.5f, Content.Rules.BoostFuelMultiplier);

        // At rest with nothing asked, nothing burns; the dampeners burn only while they brake, and only what the
        // braking takes: the last tick of a stop uses a share of the thrust and burns that share.
        state = HullState.Spawn(Double3.Zero, flight);
        FlightModel.Step(ref state, new FlightInput { Dampeners = true }, flight, HullSim.Dt, out _, out fuel);
        Assert.Equal(default, fuel.Burn);
        Assert.Equal(flight.FuelCapacity, state.Fuel);
        var reach = flight.Push(BlockFacing.Left) * HullSim.Dt;
        state.Velocity = new Vector3(reach * 2.5f, 0, 0);
        FlightModel.Step(ref state, new FlightInput { Dampeners = true }, flight, HullSim.Dt, out _, out fuel);
        Assert.Equal(thruster, fuel.Burn.Nitrogen, 6);
        FlightModel.Step(ref state, new FlightInput { Dampeners = true }, flight, HullSim.Dt, out _, out fuel);
        Assert.Equal(thruster, fuel.Burn.Nitrogen, 6);
        FlightModel.Step(ref state, new FlightInput { Dampeners = true }, flight, HullSim.Dt, out _, out fuel);
        Assert.Equal(thruster * 0.5f, fuel.Burn.Nitrogen, 4);
        Assert.Equal(0f, state.Velocity.Length(), 5);
    }

    [Fact]
    public void APartWithNoFuelOfItsKindGivesNoThrustDrawsNoPowerAndTheOthersFlyOn()
    {
        // The test fleet's pod: one engine, the only part that pushes forward, and thrusters of one other fuel.
        var flight = FleetContent.Value.Pod.Flight;
        var state = HullState.Spawn(Double3.Zero, flight);

        // The last of the bipropellant feeds one more tick and is then gone, never below nothing.
        state.Fuel.Bipropellant = flight.Burn[ThrustTable.Index(BlockFacing.Front, FuelKind.Bipropellant)] * HullSim.Dt * 0.5f;
        FlightModel.Step(ref state, new FlightInput { Forward = 127 }, flight, HullSim.Dt, out var flow, out var fuel);
        Assert.Equal(flight.Push(BlockFacing.Front) * HullSim.Dt, state.Velocity.Length(), 5);
        Assert.Equal(0f, state.Fuel.Bipropellant);
        Assert.Equal(flight.ThrustPower, flow.Thrust, 5);
        Assert.True(state.Thrust.Burning(FuelKind.Bipropellant));

        // Dry: the engine gives nothing, its pumps draw nothing, and the readout says no burn.
        var speed = state.Velocity.Length();
        FlightModel.Step(ref state, new FlightInput { Forward = 127 }, flight, HullSim.Dt, out flow, out fuel);
        Assert.Equal(speed, state.Velocity.Length(), 6);
        Assert.Equal(0f, fuel.Burn.Bipropellant);
        Assert.Equal(flight.ThrustPowerByFuel.Nitrogen, flow.Thrust, 5);
        Assert.False(state.Thrust.Burning(FuelKind.Bipropellant));
        Assert.Equal(0f, state.Thrust.Firing(FuelKind.Bipropellant, BlockFacing.Back));

        // The thrusters still have nitrogen: the hull strafes, brakes and reverses as before.
        var sideways = Fly(state, new FlightInput { Strafe = 127 }, flight, 30);
        Assert.Equal(flight.Push(BlockFacing.Right) * 0.5f, Vector3.Dot(sideways.Velocity, Vector3.UnitX), 3);
        var braked = Fly(state, new FlightInput { Dampeners = true }, flight, 600);
        Assert.Equal(0f, braked.Velocity.Length(), 4);

        // With the nitrogen gone too the dampeners have nothing to brake with, and boost has nothing to boost.
        state.Fuel = default;
        state.Velocity = new Vector3(3, -4, 5);
        var adrift = Fly(state, new FlightInput { Forward = 127, Strafe = -127, Vertical = 127, Boost = true, Dampeners = true }, flight, 120);
        Assert.Equal(state.Velocity, adrift.Velocity);
        Assert.Equal(default, adrift.Fuel);
        Assert.Equal(default, adrift.Thrust with { Flags = 0 });
        var held = Fly(state, new FlightInput { Dampeners = true }, flight, 120);
        Assert.Equal(state.Velocity, held.Velocity);

        // Gyros run on electricity: a dry hull still turns.
        var turned = Fly(state, new FlightInput { Yaw = 127 }, flight, 30);
        Assert.True(turned.Forward.X > 0.3f);
    }

    [Fact]
    public void TheThrustStateSaysWhichWayTheHullIsPushedAndWhichKindsOfPartFire()
    {
        var flight = Content.Pod.Flight;
        var state = HullState.Spawn(Double3.Zero, flight);
        FlightModel.Step(ref state, new FlightInput { Forward = 127 }, flight, HullSim.Dt);
        Assert.Equal((0, 0, -127), (state.Thrust.X, state.Thrust.Y, state.Thrust.Z));
        Assert.True(state.Thrust.Burning(FuelKind.Bipropellant));
        Assert.False(state.Thrust.Burning(FuelKind.Nitrogen));
        Assert.False(state.Thrust.Boosted);
        Assert.True(state.Thrust.IsValid);

        // The engine faces back and fires; a thruster facing front, which would brake, does not.
        Assert.Equal(1f, state.Thrust.Firing(FuelKind.Bipropellant, BlockFacing.Back));
        Assert.Equal(0f, state.Thrust.Firing(FuelKind.Nitrogen, BlockFacing.Front));
        Assert.Equal(1f, state.Thrust.Throttle(BlockFacing.Front));
        Assert.Equal(0f, state.Thrust.Throttle(BlockFacing.Back));

        // Half throttle to the left and boost: the thruster facing right fires at half, and the flag says boost.
        state = HullState.Spawn(Double3.Zero, flight);
        FlightModel.Step(ref state, new FlightInput { Strafe = -64, Boost = true }, flight, HullSim.Dt);
        Assert.Equal(-64, state.Thrust.X);
        Assert.Equal(64 / 127f, state.Thrust.Firing(FuelKind.Nitrogen, BlockFacing.Right), 5);
        Assert.Equal(0f, state.Thrust.Firing(FuelKind.Nitrogen, BlockFacing.Left));
        Assert.True(state.Thrust.Boosted);

        // The dampeners fire the parts that oppose the drift, and nothing once the hull is at rest.
        state = HullState.Spawn(Double3.Zero, flight);
        state.Velocity = new Vector3(0, 30, -40);
        FlightModel.Step(ref state, new FlightInput { Dampeners = true }, flight, HullSim.Dt);
        Assert.Equal((0, -127, 127), (state.Thrust.X, state.Thrust.Y, state.Thrust.Z));
        Assert.Equal(1f, state.Thrust.Firing(FuelKind.Nitrogen, BlockFacing.Up));
        Assert.Equal(1f, state.Thrust.Firing(FuelKind.Nitrogen, BlockFacing.Front));
        state = Fly(state, new FlightInput { Dampeners = true }, flight, 900);
        Assert.Equal(default, state.Thrust);

        // A throttle too small for the scale still reads as firing; a hull in the tunnel fires nothing.
        Assert.Equal(1, ThrustState.Of(0.001f, 0, 0, true, false, false, false, false).X);
        Assert.Equal(-1, ThrustState.Of(0, 0, -0.001f, true, false, false, false, false).Z);
        var order = new HullOrder { Kind = OrderKind.Jump, Target = new Double3(0, 0, -1e6) };
        var body = default(HullBody);
        var jumping = HullState.Spawn(Double3.Zero, flight);
        jumping.Velocity = new Vector3(0, 0, -20);
        for (var i = 0; i < 60 * 120 && jumping.Jump != JumpPhase.Tunnel; i++)
            HullSim.Step(ref jumping, ref order, default, flight, ref body);
        Assert.Equal(JumpPhase.Tunnel, jumping.Jump);
        HullSim.Step(ref jumping, ref order, default, flight, ref body);
        Assert.Equal(default, jumping.Thrust);
        Assert.Equal(default, body.Fuel.Burn);
        Assert.Equal(jumping.Fuel, body.Fuel.Amount);
    }

    [Fact]
    public void EnginesOnTheFrontBrakeAndAHullWithNoPartFacingAWayCannotBePushedThatWay()
    {
        // An engine facing back and one facing front: it brakes as hard as it accelerates, and cannot strafe.
        var design = Design(Box("engine", 1, 0, 0, BlockFacing.Back), Box("engine", -1, 0, 0, BlockFacing.Front));
        var flight = design.Flight;
        Assert.Equal(flight.Push(BlockFacing.Front), flight.Push(BlockFacing.Back));
        Assert.Equal(0f, flight.Acceleration.X);
        Assert.Equal(0f, flight.WeakestPush);

        var state = Fly(HullState.Spawn(Double3.Zero, flight), new FlightInput { Forward = 127 }, flight, 60);
        var speed = state.Velocity.Length();
        Assert.Equal(flight.Push(BlockFacing.Front), speed, 2);
        var reversed = Fly(state, new FlightInput { Forward = -127 }, flight, 60);
        Assert.Equal(0f, reversed.Velocity.Length(), 2);

        // The dampeners brake along the nose with the engine that faces it, and leave a sideways drift alone.
        state.Velocity += new Vector3(7, 0, 0);
        var damped = Fly(state, new FlightInput { Dampeners = true }, flight, 600);
        Assert.Equal(new Vector3(7, 0, 0), damped.Velocity);
        var asked = Fly(damped, new FlightInput { Strafe = -127, Vertical = 127 }, flight, 60);
        Assert.Equal(damped.Velocity, asked.Velocity);
        Assert.Equal(damped.Fuel, asked.Fuel);
    }

    /// <summary>A hull with four nitrogen tanks, one of them two cells, a cold gas thruster that burns from them to push it up and a hot thruster that pushes it back.</summary>
    private static HullDesign ManyTanks() => Design(
        [Box("hull", 1, 0, 0), Box("nitrogenTank", 2, 0, 0), Box("nitrogenTank", 3, 0, 0), Box("nitrogenTank", 4, 0, 0, size: new GridSize(2, 1, 1))],
        [Mount("coldGasThruster", 7, BlockFacing.Down), Mount("hotThruster", 7, BlockFacing.Front)]);

    private static long Bits(double value) => BitConverter.DoubleToInt64Bits(value);

    [Fact]
    public void BurningDrawsFromEveryTankOfTheFuelInProportionToWhatItHoldsAndEqualTanksStayEqual()
    {
        var design = ManyTanks();
        using var hulls = new HullStore();
        var field = new HullField(hulls, [design]);
        var hull = hulls.Create(1, 0, design, Double3.Zero).Index;
        var states = hulls.BlockStates;
        var handle = hulls.Info(hull).BlockState;

        // The tanks in block order, each with its own capacity; a hull is created with every one full.
        var tanks = design.Tanks.ToArray();
        Assert.Equal([FuelKind.Nitrogen, FuelKind.Hydrazine, FuelKind.Bipropellant, FuelKind.Nitrogen, FuelKind.Nitrogen, FuelKind.Nitrogen], tanks.Select(tank => tank.Kind));
        Assert.Equal([4, 5, 6, 8, 9, 10], tanks.Select(tank => tank.Block));
        var one = Content.Blocks.Get("nitrogenTank").FuelCapacity;
        Assert.Equal(one, tanks[0].Capacity);
        Assert.Equal(one * 2f, tanks[5].Capacity);
        Assert.Equal(tanks.Select(tank => (double)tank.Capacity), states.Tanks(handle).ToArray());
        Assert.Equal(design.Flight.FuelCapacity, hulls.State(hull).Fuel);

        // Filled unevenly: two equal tanks, one a fifth full, the large one three quarters.
        var levels = states.TankLevels(handle);
        levels[0] = one * 0.6;
        levels[3] = one * 0.6;
        levels[4] = one * 0.2;
        levels[5] = one * 1.5;
        hulls.State(hull).Fuel = TankFuel.Total(levels, tanks);
        var before = levels.ToArray();
        var total = hulls.State(hull).Fuel;
        Assert.Equal(one * 2.9f, total.Nitrogen, 3);

        // Ten minutes of full thrust upward on the cold gas thruster, which burns 0.0075 kg a second.
        var burnt = 0.0;
        for (var tick = 0; tick < 36000; tick++)
        {
            field.BeginRound();
            Assert.False(field.Step(hull, Toward(BlockFacing.Up)));
            burnt += (double)hulls.Info(hull).Fuel.Burn.Nitrogen * HullSim.Dt;
        }

        var after = states.Tanks(handle).ToArray();
        var left = hulls.State(hull).Fuel;
        Assert.Equal(Content.Blocks.Get("coldGasThruster").FuelBurn * 600.0, burnt, 4);

        // Nothing is lost to rounding: the tanks fell by what was burnt, to a milligram in ten minutes.
        Assert.Equal(before[0] + before[3] + before[4] + before[5] - burnt, after[0] + after[3] + after[4] + after[5], 6);

        // What the hull has of a fuel is its tanks added up, and the readout says the same.
        Assert.Equal(TankFuel.Total(after, tanks), left);
        Assert.Equal((float)(after[0] + after[3] + after[4] + after[5]), left.Nitrogen);
        Assert.Equal(left, hulls.Info(hull).Fuel.Amount);

        // Each tank gave the share it held: every one is at the same fraction of what it had, and the two that
        // were equal are still equal to the bit.
        var share = (after[0] + after[3] + after[4] + after[5]) / (before[0] + before[3] + before[4] + before[5]);
        foreach (var i in new[] { 0, 3, 4, 5 })
            Assert.Equal(share, after[i] / before[i], 9);
        Assert.Equal(Bits(after[0]), Bits(after[3]));
        Assert.True(after[4] < after[0] && after[0] < after[5]);

        // The fuels nothing burnt are untouched, tank and total.
        Assert.Equal(Bits(before[1]), Bits(after[1]));
        Assert.Equal(Bits(before[2]), Bits(after[2]));
        Assert.Equal(total.Hydrazine, left.Hydrazine);

        // Two fuels at once, each from its own tanks: back on the hot thruster's hydrazine, up on nitrogen.
        field.BeginRound();
        field.Step(hull, new FlightInput { Vertical = 127, Forward = -127 });
        Assert.True(states.Tanks(handle)[1] < before[1]);
        Assert.True(states.Tanks(handle)[0] < after[0]);
        Assert.Equal(Bits(before[2]), Bits(states.Tanks(handle)[2]));

        // Run dry: every tank of the fuel is at exactly nothing in the same step, none before another.
        levels = states.TankLevels(handle);
        foreach (var i in new[] { 0, 3, 4, 5 })
            levels[i] = before[i] * 1e-5;
        hulls.State(hull).Fuel = TankFuel.Total(levels, tanks);
        var steps = 0;
        while (hulls.State(hull).Fuel.Nitrogen > 0f && steps++ < 600)
        {
            foreach (var i in new[] { 0, 3, 4, 5 })
                Assert.True(states.Tanks(handle)[i] > 0.0, "a tank ran dry before the others");
            field.BeginRound();
            field.Step(hull, Toward(BlockFacing.Up));
        }

        Assert.InRange(steps, 1, 599);
        foreach (var i in new[] { 0, 3, 4, 5 })
            Assert.Equal(0.0, states.Tanks(handle)[i]);

        // With none of it left the thruster gives nothing, and the tanks stay at nothing.
        var speed = hulls.State(hull).Velocity;
        field.BeginRound();
        field.Step(hull, Toward(BlockFacing.Up));
        Assert.Equal(speed, hulls.State(hull).Velocity);
        Assert.False(hulls.State(hull).Thrust.Burning(FuelKind.Nitrogen));
        Assert.Equal(0f, hulls.State(hull).Fuel.Nitrogen);
    }

    [Fact]
    public void ALargeTankAtALowThrottleStillFallsByWhatIsBurnt()
    {
        // 64 cells of nitrogen, 9,600 kg, and one two by two cold gas array at a tenth of its throttle: 1.25e-5 kg a tick.
        // In single precision that is less than half a unit in the tank's last place, and the tank would never
        // fall. Kept in double precision it falls by what is burnt.
        var design = Design(
            [Box("hull", 1, 0, 0), Box("nitrogenTank", 2, 0, 0, size: new GridSize(4, 4, 4))],
            [Mount("coldGasThruster", 7, BlockFacing.Down) with { Size = 2 }]);
        using var hulls = new HullStore();
        var field = new HullField(hulls, [design]);
        var hull = hulls.Create(1, 0, design, Double3.Zero).Index;
        var handle = hulls.Info(hull).BlockState;
        var large = design.TankOf(8);
        Assert.Equal(Content.Blocks.Get("nitrogenTank").FuelCapacity * 64f, design.Tanks[large].Capacity);
        var before = hulls.BlockStates.Tanks(handle).ToArray();
        var burnt = 0.0;
        for (var tick = 0; tick < 6000; tick++)
        {
            field.BeginRound();
            field.Step(hull, new FlightInput { Vertical = 13 });
            burnt += (double)hulls.Info(hull).Fuel.Burn.Nitrogen * HullSim.Dt;
        }

        var after = hulls.BlockStates.Tanks(handle).ToArray();
        Assert.InRange(burnt, 0.07, 0.08);
        Assert.Equal(before[0] + before[large] - burnt, after[0] + after[large], 6);
        Assert.Equal(after[0] / before[0], after[large] / before[large], 12);
        Assert.True(after[large] < before[large]);
    }

    [Fact]
    public void TwoHullsSteppedAlikeFromTheSameTanksHoldTheSameBitsAndAHullFlownFromItsTotalsAgreesWithItsTanks()
    {
        // The test fleet's pod has one tank of each fuel it burns. Stepped over its tanks through a field, and stepped from
        // the per-fuel figures alone as a client steps a hull it only knows the motion of, it flies the same and
        // holds the same fuel to within what single precision keeps of it: one piece of arithmetic either way.
        var design = FleetContent.Value.Pod;
        Assert.Equal(1, design.Tanks.ToArray().Count(tank => tank.Kind == FuelKind.Nitrogen));
        Assert.Equal(1, design.Tanks.ToArray().Count(tank => tank.Kind == FuelKind.Bipropellant));
        using var hulls = new HullStore();
        var field = new HullField(hulls, [design]);
        var hull = hulls.Create(1, 0, design, Double3.Zero).Index;
        var alone = hulls.State(hull);
        var order = default(HullOrder);
        for (var tick = 0; tick < 1200; tick++)
        {
            var input = tick < 400 ? new FlightInput { Forward = 127, Strafe = 60, Boost = tick > 100 }
                : tick < 800 ? new FlightInput { Vertical = -127, Strafe = -127 }
                : new FlightInput { Dampeners = true };
            field.BeginRound();
            field.Step(hull, input);
            HullSim.AdvanceShieldClock(ref alone);
            HullSim.Step(ref alone, ref order, input, design.Flight);
        }

        Assert.Equal(alone.Position, hulls.State(hull).Position);
        Assert.Equal(alone.Velocity, hulls.State(hull).Velocity);
        Assert.Equal(alone.Thrust, hulls.State(hull).Thrust);
        Assert.True(alone.Fuel.Nitrogen < design.Flight.FuelCapacity.Nitrogen - 0.1f && alone.Fuel.Bipropellant < design.Flight.FuelCapacity.Bipropellant - 1f);
        Assert.Equal(alone.Fuel.Nitrogen, hulls.State(hull).Fuel.Nitrogen, 2);
        Assert.Equal(alone.Fuel.Bipropellant, hulls.State(hull).Fuel.Bipropellant, 1);

        // Two hulls of one design, stepped by the same inputs from the same tanks, hold the same bits in every
        // tank: the server's code on a predicting client gives the server's answer.
        var many = ManyTanks();
        using var pair = new HullStore();
        var both = new HullField(pair, [many]);
        var server = pair.Create(1, 0, many, Double3.Zero).Index;
        var client = pair.Create(1, 0, many, new Double3(1e6, 0, 0)).Index;
        foreach (var index in new[] { server, client })
        {
            var levels = pair.BlockStates.TankLevels(pair.Info(index).BlockState);
            for (var i = 0; i < levels.Length; i++)
                levels[i] *= (i + 2.0) / (levels.Length + 3.0);
            pair.State(index).Fuel = TankFuel.Total(levels, many.Tanks);
        }

        var full = pair.State(server).Fuel;
        for (var tick = 0; tick < 900; tick++)
        {
            var input = new FlightInput { Vertical = (sbyte)(tick % 255 - 127), Forward = (sbyte)(-(tick % 127)), Dampeners = tick % 90 > 45 };
            both.BeginRound();
            both.Step(server, input);
            both.Step(client, input);
        }

        Assert.Equal(
            pair.BlockStates.Tanks(pair.Info(server).BlockState).ToArray().Select(Bits),
            pair.BlockStates.Tanks(pair.Info(client).BlockState).ToArray().Select(Bits));
        Assert.Equal(pair.State(server).Fuel, pair.State(client).Fuel);
        Assert.True(pair.State(server).Fuel.Nitrogen < full.Nitrogen && pair.State(server).Fuel.Hydrazine < full.Hydrazine);
    }

    [Fact]
    public void TankLevelsThatAreNotTrustedAreRefusedForASaveAndBroughtIntoTheirTanksForASnapshot()
    {
        var design = ManyTanks();
        using var states = new BlockStateStore();
        var handle = states.Create(design, hitPoints: true);
        var full = states.Tanks(handle).ToArray();
        var half = full.Select(level => level * 0.5).ToArray();
        float[] Single(double[] levels) => [.. levels.Select(level => (float)level)];

        // What a hull of the design can hold is taken as it is, either way.
        Assert.True(states.TryRestoreTanks(handle, half));
        Assert.Equal(half, states.Tanks(handle).ToArray());
        Assert.True(states.SetTanks(handle, Single(full)));
        Assert.Equal(full, states.Tanks(handle).ToArray());

        // A save: the wrong count, no number, less than nothing and more than the tank holds are all refused.
        Assert.False(states.TryRestoreTanks(handle, half.AsSpan(1)));
        foreach (var hostile in new[] { double.NaN, double.PositiveInfinity, -0.5, full[2] * 1.000001 })
        {
            var bad = (double[])half.Clone();
            bad[2] = hostile;
            Assert.False(states.TryRestoreTanks(handle, bad));
        }

        // A snapshot: each figure is brought into its tank, and a wrong count changes nothing.
        Assert.True(states.SetTanks(handle, Single(half)));
        Assert.False(states.SetTanks(handle, Single(full).AsSpan(1)));
        Assert.Equal(half, states.Tanks(handle).ToArray());
        Assert.True(states.SetTanks(handle, [float.NaN, (float)full[1] * 4f, -1f, (float)half[3], float.PositiveInfinity, float.NegativeInfinity]));
        Assert.Equal([0.0, full[1], 0.0, half[3], full[4], 0.0], states.Tanks(handle).ToArray());

        // A tank that is lost holds nothing: a save that says otherwise is refused, a snapshot's figure is dropped.
        Assert.True(states.SetTanks(handle, Single(half)));
        Assert.True(states.Lose(handle, design.Tanks[3].Block));
        Assert.Equal(0.0, states.Tanks(handle)[3]);
        Assert.False(states.TryRestoreTanks(handle, half));
        var gone = (double[])half.Clone();
        gone[3] = 0.0;
        Assert.True(states.TryRestoreTanks(handle, gone));
        Assert.True(states.SetTanks(handle, Single(half)));
        Assert.Equal(gone, states.Tanks(handle).ToArray());

        // A copy for prediction takes the tanks with the blocks.
        var copy = states.Create(design, hitPoints: true);
        states.Lose(copy, design.Tanks[3].Block);
        states.CopyFrom(copy, states, handle);
        Assert.Equal(gone, states.Tanks(copy).ToArray());
        states.EmptyTanks(copy);
        Assert.All(states.Tanks(copy).ToArray(), level => Assert.Equal(0.0, level));
        Assert.Equal(gone, states.Tanks(handle).ToArray());

        // The tanks sit after the block arrays whatever the design's size and whether hit points are kept:
        // writing every tank and losing every other block leaves both as they were written.
        foreach (var hitPoints in new[] { true, false })
        {
            var state = states.Create(design, hitPoints);
            var levels = states.TankLevels(state);
            for (var i = 0; i < levels.Length; i++)
                levels[i] = i + 0.25;
            for (var block = 0; block < design.BlockCount; block++)
                if (design.TankOf(block) < 0 && block != 0)
                    Assert.True(states.Lose(state, block));
            Assert.Equal([0.25, 1.25, 2.25, 3.25, 4.25, 5.25], states.Tanks(state).ToArray());
            Assert.Equal(design.BlockCount - 6 - 1, states.Losses(state).Length);
        }
    }

    [Fact]
    public void ADesignHasAtMostAsManyTanksAsASnapshotCanTellThePilotOf()
    {
        Assert.Equal(255, TankFuel.MaxTanks);
        Assert.Equal(255, Content.Blocks.Limits.MaxTanks);

        // 255 tanks in a row are a design; one more is refused, by the file's rule and so by Decode's and the editor's.
        DesignBox[] Row(int tanks)
        {
            var boxes = new List<DesignBox> { Box("core", 0, 0, 0) };
            for (var i = 0; i < tanks; i++)
                boxes.Add(Box(i % 2 == 0 ? "nitrogenTank" : "bipropellantTank", 1 + i, 0, 0));
            return [.. boxes];
        }

        var most = new BoxDesign("Tanker", 1f, Row(255), Content.Blocks.Limits, "test");
        Assert.Equal(256, most.Blocks.Length);
        var refused = Assert.Throws<InvalidDataException>(() => new BoxDesign("Tanker", 1f, Row(256), Content.Blocks.Limits, "test"));
        Assert.Contains("too many tanks (limit 255)", refused.Message);

        // The editor stops at the same count, for a block placed and for blocks pasted, and only for tanks.
        var draft = BuildDraft.FromBlueprint(HullDesign.Of(most, Content.Rules), Content);
        var tank = Content.Blocks.Get("nitrogenTank");
        Assert.Equal(DraftEdit.TooManyTanks, draft.CanAdd(new GridSize(0, 1, 0), One, tank));
        Assert.Equal(DraftEdit.TooManyTanks, draft.Add(new GridSize(0, 1, 0), One, tank));
        Assert.Equal(DraftEdit.TooManyTanks, draft.Paste([Box("hull", 0, 1, 0), Box("hydrazineTank", 0, 2, 0)], default));
        Assert.Equal(DraftEdit.Done, draft.CanAdd(new GridSize(0, 1, 0), One, Content.Blocks.Get("hull")));
        Assert.Equal(DraftEdit.Done, draft.Delete(1));
        Assert.Equal(DraftEdit.Done, draft.Add(new GridSize(0, 1, 0), One, tank));

        // The limit is what keeps the wire's promise: a header with every tank still leaves room for 255 records.
        Assert.True(SnapshotHeader.MaxWireSize + byte.MaxValue * (HullRecord.MaxWireSize - HullRecord.MaxHealth * 6) < 65536);
    }

    [Fact]
    public void SteppingOverTanksTakesNoManagedMemory()
    {
        var design = ManyTanks();
        using var hulls = new HullStore();
        var field = new HullField(hulls, [design]);
        var hull = hulls.Create(1, 0, design, Double3.Zero).Index;
        var input = new FlightInput { Vertical = 127, Forward = -100, Dampeners = true };
        field.BeginRound();
        field.Step(hull, input);
        var probe = new AllocationProbe();
        for (var tick = 0; tick < 600; tick++)
        {
            if (tick == 300)
                hulls.BlockStates.EmptyTanks(hulls.Info(hull).BlockState);
            while (probe.Measuring())
            {
                field.BeginRound();
                field.Step(hull, input);
            }
        }

        Assert.Equal(0L, probe.Bytes);
        Assert.Equal(default, hulls.State(hull).Fuel);
    }

    [Fact]
    public void FlyingAndRunningDryTakeNoManagedMemory()
    {
        var flight = Content.Pod.Flight;
        var state = HullState.Spawn(Double3.Zero, flight);
        var order = default(HullOrder);
        var body = default(HullBody);
        var input = new FlightInput { Forward = 127, Strafe = -90, Vertical = 40, Boost = true, Dampeners = true };
        HullSim.Step(ref state, ref order, input, flight, ref body);
        var probe = new AllocationProbe();
        for (var tick = 0; tick < 600; tick++)
        {
            if (tick == 300)
                state.Fuel = default;
            while (probe.Measuring())
                HullSim.Step(ref state, ref order, input, flight, ref body);
        }

        Assert.Equal(0L, probe.Bytes);
    }

    [Fact]
    public void DesignFilesOfEveryVersionLoadAndOnlyVersionFourAndLaterMayNameAFacing()
    {
        static BoxDesign Parse(string json)
        {
            using var document = JsonDocument.Parse(json);
            return BoxDesign.Parse(document.RootElement, Content.Blocks, "test.json");
        }

        const string head = "\"name\": \"T\", \"class\": \"ship\", \"blockSize\": 1, \"color\": [1, 1, 1], \"blocks\": [{ \"at\": [0, 0, 0], \"type\": \"core\" }, ";

        // Versions one to three name no facing: every block faces back, so an engine pushes forward as it always did.
        foreach (var version in new[] { "", "\"version\": 2, ", "\"version\": 3, " })
        {
            var old = Parse("{ " + version + head + "{ \"at\": [0, 0, 1], \"type\": \"engine\" }] }");
            Assert.Equal(BlockFacing.Back, old.Blocks[0].Facing);
            Assert.Equal(BlockFacing.Back, old.Blocks[1].Facing);
            var figures = new BoxHullSums(old);
            figures.Evaluate(figures.Intact, Content.Rules, out var flight, out _);
            Assert.True(flight.Push(BlockFacing.Front) > 0f);
            Assert.Equal(flight.StrongestPush, flight.Push(BlockFacing.Front));
            var refused = Assert.Throws<InvalidDataException>(() => Parse("{ " + version + head + "{ \"at\": [0, 0, 1], \"type\": \"engine\", \"facing\": \"front\" }] }"));
            Assert.Contains("Facing requires version 4", refused.Message);
        }

        // Version four: each of the six names, a block that names none, and nothing else.
        foreach (var facing in Directions)
        {
            var design = Parse("{ \"version\": 4, " + head + "{ \"at\": [0, 0, 1], \"type\": \"engine\", \"facing\": \"" + Facings.Name(facing) + "\" }] }");
            Assert.Equal(BlockFacing.Back, design.Blocks[0].Facing);
            Assert.Equal(facing, design.Blocks[1].Facing);

            // Written out and read back, the facing is kept.
            using var stream = new MemoryStream();
            design.Write(stream);
            var again = Parse(System.Text.Encoding.UTF8.GetString(stream.ToArray()));
            Assert.Equal(design.Blocks.ToArray(), again.Blocks.ToArray());
            Assert.Contains("\"version\":9", System.Text.Encoding.UTF8.GetString(stream.ToArray()));
        }

        Assert.Contains("Unknown block facing", Assert.Throws<InvalidDataException>(() => Parse("{ \"version\": 4, " + head + "{ \"at\": [0, 0, 1], \"type\": \"engine\", \"facing\": \"forward\" }] }")).Message);
        Assert.Contains("unsupported design version 10", Assert.Throws<InvalidDataException>(() => Parse("{ \"version\": 10, " + head + "{ \"at\": [0, 0, 1], \"type\": \"engine\" }] }")).Message);
        Assert.Equal(9, BoxDesign.FormatVersion);
    }

    [Fact]
    public void DesignBytesCarryEachFacingAndDecodeRefusesAnUnknownOneAnOlderEncodingAndAPartLargerThanItsTypeAllows()
    {
        var design = Design(
            Box("engine", 1, 0, 0, BlockFacing.Back),
            Box("engine", 2, 0, 0, BlockFacing.Left, new GridSize(2, 2, 1)),
            Box("engine", 4, 0, 0, BlockFacing.Up, new GridSize(1, 4, 4)),
            Box("hull", 5, 0, 0, BlockFacing.Down));
        var bytes = DesignBytes.Encode(design);
        Assert.Equal(7, DesignBytes.Version);
        var decoded = DesignBytes.Decode(bytes, SHA256.HashData(bytes), Content);
        Assert.Equal(design.Boxes.ToArray(), decoded.Boxes.ToArray());
        Assert.Equal(design.Flight, decoded.Flight);
        Assert.Equal(BlockFacing.Left, decoded.Boxes[^3].Facing);
        Assert.Equal(BlockFacing.Down, decoded.Boxes[^1].Facing);

        // A block's facing is its last byte. The last block is plating; after it comes only the count of mounts, none here.
        var hostile = (byte[])bytes.Clone();
        Assert.Equal((byte)BlockFacing.Down, hostile[^5]);
        hostile[^5] = 6;
        Assert.Contains("Unknown block facing", Assert.Throws<InvalidDataException>(() => DesignBytes.Decode(hostile, SHA256.HashData(hostile), Content)).Message);

        // Another facing is another design: other bytes, another hash, and for a thrust part other figures.
        var other = (byte[])bytes.Clone();
        other[^5] = (byte)BlockFacing.Up;
        Assert.Equal(BlockFacing.Up, DesignBytes.Decode(other, SHA256.HashData(other), Content).Boxes[^1].Facing);
        Assert.False(SHA256.HashData(other).AsSpan().SequenceEqual(SHA256.HashData(bytes)));

        // The encodings before mounts are not read: a save or a server that has one is of another version altogether.
        foreach (var version in new byte[] { 2, 3 })
        {
            var older = (byte[])bytes.Clone();
            older[0] = version;
            Assert.Contains("Unknown design encoding", Assert.Throws<InvalidDataException>(() => DesignBytes.Decode(older, SHA256.HashData(older), Content)).Message);
        }

        // A size the type does not allow is refused wherever a design is made. No shipped block has a limit of
        // its own since thrusters became mounts, so the catalogue here gives the engine one.
        var limits = Content.Blocks.Limits;
        Assert.Equal(limits.MaxBoxSide, BoxDesign.MaxSide(Content.Blocks.Get("engine"), limits));
        Assert.Equal(limits.MaxBoxSide, BoxDesign.MaxSide(Content.Blocks.Get("hull"), limits));
        var text = File.ReadAllText(Prospect.Engine.IO.AppPaths.Content("blocks", "blocks.json"));
        var limited = BlockCatalog.Parse(System.Text.Encoding.UTF8.GetBytes(text.Replace("\"engine\": { \"name\": \"Engine\",", "\"engine\": { \"maxSide\": 2, \"name\": \"Engine\",")), "limited");
        Assert.Equal(2, BoxDesign.MaxSide(limited.Get("engine"), limited.Limits));
        DesignBox[] small = [new(new GridSize(0, 0, 0), One, limited.Get("core")), new(new GridSize(1, 0, 0), new GridSize(1, 2, 2), limited.Get("engine"))];
        Assert.Equal(2, new BoxDesign("T", 1, small, limited.Limits, "received design").Blocks.Length);
        small[1] = small[1] with { Size = new GridSize(1, 3, 1) };
        var refused = Assert.Throws<InvalidDataException>(() => new BoxDesign("T", 1, small, limited.Limits, "received design"));
        Assert.Contains("at most 2 cells a side", refused.Message);
    }

    [Fact]
    public void AMountedThrusterIsReadWrittenEncodedAndHeldToItsFace()
    {
        static BoxDesign Parse(string json)
        {
            using var document = JsonDocument.Parse(json);
            return BoxDesign.Parse(document.RootElement, Content.Blocks, "test.json");
        }

        const string blocks = "\"name\": \"T\", \"class\": \"ship\", \"blockSize\": 1, \"color\": [1, 1, 1], \"blocks\": [{ \"at\": [0, 0, 0], \"type\": \"core\" }, { \"at\": [1, 0, 0], \"type\": \"hull\", \"size\": [2, 1, 1] }]";
        static string File5(string mounts) => "{ \"version\": 5, " + blocks + ", \"mounts\": [" + mounts + "] }";
        static string At(int on, string face, int u, int v, string type = "coldGasThruster") => $"{{ \"on\": {on}, \"face\": \"{face}\", \"at\": [{u}, {v}], \"type\": \"{type}\" }}";

        // Version five lists mounts apart from the blocks. The design keeps them by block, then face, then place.
        // On a top face the first position runs along z and the second along x.
        var design = Parse(File5(At(1, "up", 5, 14) + ", " + At(1, "up", 0, 0) + ", " + At(0, "left", 3, 3, "hotThruster")));
        Assert.Equal(2, design.Blocks.Length);
        Assert.Equal(
            [new DesignMount(0, BlockFacing.Left, 3, 3, Content.Blocks.Get("hotThruster")), new DesignMount(1, BlockFacing.Up, 0, 0, Content.Blocks.Get("coldGasThruster"), Size: 2), new DesignMount(1, BlockFacing.Up, 5, 14, Content.Blocks.Get("coldGasThruster"), Size: 2)],
            design.Mounts.ToArray());
        design.MountsOf(1, out var start, out var end);
        Assert.Equal((1, 3), (start, end));

        // Where it sits: on the top of the plating, 0.2 of a cell high, its middle over the square it was given.
        var middle = design.MountMiddle(design.Mounts[2]);
        Assert.Equal(new Double3(2.1, 0.6, 0.2), new Double3(Math.Round(middle.X, 6), Math.Round(middle.Y, 6), Math.Round(middle.Z, 6)));
        Assert.Equal(new Vector3(0.4f, 0.2f, 0.4f), BoxDesign.MountSize(design.Mounts[2]));

        // Written and read back, and encoded and decoded, they are the same mounts; the bytes list them in the design's order.
        using var stream = new MemoryStream();
        design.Write(stream);
        Assert.Equal(design.Mounts.ToArray(), Parse(System.Text.Encoding.UTF8.GetString(stream.ToArray())).Mounts.ToArray());
        var hull = HullDesign.Of(design, Content.Rules);
        var bytes = DesignBytes.Encode(hull);
        var decoded = DesignBytes.Decode(bytes, SHA256.HashData(bytes), Content);
        Assert.Equal(design.Mounts.ToArray(), decoded.Mounts.ToArray());
        Assert.Equal(hull.Flight, decoded.Flight);

        // The same mounts in another order are other bytes for the same design, and are refused as such.
        var swapped = (byte[])bytes.Clone();
        var cold = "coldGasThruster"u8;
        var first = bytes.AsSpan().IndexOf(cold);
        var second = first + cold.Length + 11 + 1;
        Assert.True(bytes.AsSpan(second).StartsWith(cold));
        bytes.AsSpan(second + cold.Length, 11).CopyTo(swapped.AsSpan(first + cold.Length));
        bytes.AsSpan(first + cold.Length, 11).CopyTo(swapped.AsSpan(second + cold.Length));
        Assert.Contains("Noncanonical", Assert.Throws<InvalidDataException>(() => DesignBytes.Decode(swapped, SHA256.HashData(swapped), Content)).Message);

        // What a file or a sender may not do.
        void Refused(string mounts, string why) => Assert.Contains(why, Assert.Throws<InvalidDataException>(() => Parse(File5(mounts))).Message);
        Refused(At(2, "up", 0, 0), "no block 2");
        Refused(At(1, "up", 7, 0), "does not fit on that face");
        Refused(At(1, "up", 0, 17), "does not fit on that face");
        Refused(At(1, "up", -1, 0), "does not fit on that face");
        Refused(At(0, "right", 3, 3), "in its way");
        Refused(At(1, "up", 0, 0) + ", " + At(1, "up", 3, 3), "in its way");
        Refused(At(1, "up", 0, 0, "engine"), "is a block and cannot be mounted");
        Refused(At(1, "sideways", 0, 0), "Unknown block facing");
        Assert.Contains("mounts on a face", Assert.Throws<InvalidDataException>(() => Parse("{ \"version\": 5, " + blocks[..^1] + ", { \"at\": [0, 1, 0], \"type\": \"coldGasThruster\" }] }")).Message);
        Assert.Contains("mounts require version 5", Assert.Throws<InvalidDataException>(() => Parse("{ \"version\": 4, " + blocks + ", \"mounts\": [] }")).Message);

        // Mounts on faces that meet at an inside corner may not run into each other, and neighbours on one face may touch.
        Assert.Equal(2, Parse(File5(At(1, "up", 0, 0) + ", " + At(1, "up", 4, 0))).Mounts.Length);
        var coldGas = Content.Blocks.Get("coldGasThruster");
        DesignBox[] corner = [new(new GridSize(0, 0, 0), One, Content.Blocks.Get("core")), new(new GridSize(1, 1, 0), One, Content.Blocks.Get("hull"))];
        DesignMount[] meeting = [new(0, BlockFacing.Right, 6, 3, coldGas, Size: 2), new(1, BlockFacing.Down, 3, 0, coldGas, Size: 2)];
        Assert.Contains("in its way", Assert.Throws<InvalidDataException>(() => new BoxDesign("T", 1, corner, Content.Blocks.Limits, "received design", mounts: meeting)).Message);
        meeting[0] = meeting[0] with { U = 4 };
        Assert.Equal(2, new BoxDesign("T", 1, corner, Content.Blocks.Limits, "received design", mounts: meeting).Mounts.Length);

        // A wedge carries mounts on its two whole sides only.
        const string wedge = "{ \"version\": 5, \"name\": \"T\", \"class\": \"ship\", \"blockSize\": 1, \"color\": [1, 1, 1], \"blocks\": [{ \"at\": [0, 0, 0], \"type\": \"core\" }, { \"at\": [2, 0, 0], \"type\": \"hull\", \"shape\": \"wedge-z++\" }], \"mounts\": [";
        var whole = 0;
        foreach (var face in Directions)
        {
            try
            {
                Parse(wedge + At(1, Facings.Name(face), 3, 3) + "] }");
                Assert.True(BlockShapes.IsFullFace(BlockShape.WedgeZPlusPlus, (int)face));
                whole++;
            }
            catch (InvalidDataException e)
            {
                Assert.Contains("does not fit on that face", e.Message);
            }
        }

        Assert.Equal(2, whole);

        // The content's count is the most a design may carry.
        var limits = Content.Blocks.Limits with { MaxMounts = 1 };
        DesignBox[] boxes = [.. design.Blocks];
        Assert.Single(new BoxDesign("T", 1, boxes, limits, "received design", mounts: [design.Mounts[0]]).Mounts.ToArray());
        Assert.Contains("too many mounted parts", Assert.Throws<InvalidDataException>(() => new BoxDesign("T", 1, boxes, limits, "received design", mounts: [design.Mounts[0], design.Mounts[1]])).Message);
    }

    [Fact]
    public void AnOldFilesThrusterBlocksBecomePlatingThatCarriesTheThrustersOnTheFaceTheyPointedFrom()
    {
        static BoxDesign Parse(string json)
        {
            using var document = JsonDocument.Parse(json);
            return BoxDesign.Parse(document.RootElement, Content.Blocks, "test.json");
        }

        const string head = "\"name\": \"T\", \"class\": \"ship\", \"blockSize\": 1, \"color\": [1, 1, 1], \"blocks\": [{ \"at\": [0, 0, 0], \"type\": \"core\" }, ";
        var cold = Content.Blocks.Get("coldGasThruster");
        var plating = Content.Blocks.Get("hull");

        // A one-cell thruster facing right: the cell stays, as plating, with four thrusters on its right face.
        var old = Parse("{ \"version\": 4, " + head + "{ \"at\": [1, 0, 0], \"type\": \"coldGasThruster\", \"facing\": \"right\" }] }");
        Assert.Equal(new DesignBox(new GridSize(1, 0, 0), One, plating), old.Blocks[1]);
        Assert.Equal(
            [new DesignMount(1, BlockFacing.Right, 0, 0, cold, Size: 2), new DesignMount(1, BlockFacing.Right, 5, 0, cold, Size: 2), new DesignMount(1, BlockFacing.Right, 0, 5, cold, Size: 2), new DesignMount(1, BlockFacing.Right, 5, 5, cold, Size: 2)],
            old.Mounts.ToArray());
        var sums = new BoxHullSums(old);
        sums.Evaluate(sums.Intact, Content.Rules, out var flight, out _);
        // Four arrays of two by two nozzles: what the one-cell block of thrusters pushed.
        Assert.Equal(cold.Thrust * 4 * 4 / flight.Mass, flight.Push(BlockFacing.Left), 4);
        Assert.Equal(400f / flight.Mass, flight.Push(BlockFacing.Left), 4);

        // Versions before facings: the thruster faced back. A sized one carries four a cell of its face.
        var wide = Parse("{ \"version\": 2, " + head + "{ \"at\": [0, 0, 1], \"type\": \"hotThruster\", \"size\": [2, 1, 3] }] }");
        Assert.Equal(8, wide.Mounts.Length);
        Assert.All(wide.Mounts.ToArray(), mount => Assert.Equal((1, BlockFacing.Back), (mount.Parent, mount.Face)));

        // A thruster whose nozzle was covered pushes no more: nothing is mounted where a block stands in front of it.
        var covered = Parse("{ \"version\": 4, " + head + "{ \"at\": [1, 0, 0], \"type\": \"coldGasThruster\", \"facing\": \"left\" }] }");
        Assert.Equal(plating, covered.Blocks[1].Type);
        Assert.Equal(0, covered.Mounts.Length);

        // The drone's thruster becomes drone plating.
        var drone = Parse("{ \"version\": 4, " + head.Replace("\"core\"", "\"microcore\"") + "{ \"at\": [0, 1, 0], \"type\": \"microthruster\", \"facing\": \"up\" }] }");
        Assert.Equal(Content.Blocks.Get("microhull"), drone.Blocks[1].Type);
        Assert.Equal(4, drone.Mounts.Length);
    }

    [Fact]
    public void ADraftKeepsEachFacingThroughRotationUndoMoveCopyAndMerge()
    {
        var draft = BuildDraft.FromBlueprint(Content.Pod, Content);
        var count = draft.Boxes.Length;
        var thruster = Content.Blocks.Get("engine");
        Assert.Equal(DraftEdit.Done, draft.Add(new GridSize(0, 3, 0), One, thruster, BlockShape.Box, BlockFacing.Up));
        Assert.Equal(BlockFacing.Up, draft.Boxes[count].Facing);

        // A quarter turn about x takes up to back, as it takes a wedge's slope; undo and redo give each back.
        Assert.Equal(DraftEdit.Done, draft.Rotate(count, 0));
        Assert.Equal(Facings.Rotate(BlockFacing.Up, 0), draft.Boxes[count].Facing);
        Assert.Equal(BlockFacing.Back, draft.Boxes[count].Facing);
        Assert.True(draft.Undo());
        Assert.Equal(BlockFacing.Up, draft.Boxes[count].Facing);
        Assert.True(draft.Redo());
        Assert.Equal(BlockFacing.Back, draft.Boxes[count].Facing);

        // A turn about the axis a cube faces along changes nothing, and says so.
        Assert.Equal(DraftEdit.Unchanged, draft.Rotate(count, 2));

        // Moved and pasted blocks face as they did; undoing the add takes the block away and redo brings it back facing the same way.
        Assert.Equal(DraftEdit.Done, draft.Move([count], new GridSize(0, 1, 0)));
        Assert.Equal(BlockFacing.Back, draft.Boxes[count].Facing);
        Assert.Equal(DraftEdit.Done, draft.Paste([draft.Boxes[count] with { Facing = BlockFacing.Left }], new GridSize(0, 1, 0)));
        Assert.Equal(BlockFacing.Left, draft.Boxes[count + 1].Facing);
        Assert.True(draft.Undo());
        Assert.True(draft.Undo());
        Assert.True(draft.Undo());
        Assert.True(draft.Undo());
        Assert.Equal(count, draft.Boxes.Length);
        Assert.True(draft.Redo());
        Assert.Equal(BlockFacing.Up, draft.Boxes[count].Facing);

        // Two engines merge into one that faces as the first did.
        Assert.Equal(DraftEdit.Done, draft.Add(new GridSize(0, 4, 0), One, thruster, BlockShape.Box, BlockFacing.Down));
        Assert.Equal(DraftEdit.Done, draft.CanMerge([count, count + 1], out var merged));
        Assert.Equal((new GridSize(1, 2, 1), BlockFacing.Up), (merged.Size, merged.Facing));

        // The exported design has the facings, and what they do: the engine facing up pushes the ship down.
        Assert.True(draft.Undo());
        var export = draft.Export();
        Assert.True(export.Ok, export.Problem);
        Assert.Equal(BlockFacing.Up, export.Design!.Boxes[count].Facing);
        Assert.True(export.Design.Flight.Push(BlockFacing.Down) > Content.Pod.Flight.Push(BlockFacing.Down) * 1.5f);
        Assert.Equal(Content.Pod.Flight.Push(BlockFacing.Up) * Content.Pod.Flight.Mass / export.Design.Flight.Mass, export.Design.Flight.Push(BlockFacing.Up), 4);
    }

    [Fact]
    public void TheSnapshotHeaderCarriesTheOwnTanksAndEveryRecordWhatItsThrustPartsDid()
    {
        // The header names how many tanks the own hull's design has and the kilograms in each, exactly.
        Span<byte> buffer = stackalloc byte[SnapshotHeader.MaxWireSize];
        var writer = new NetWriter(buffer);
        var header = new SnapshotHeader { ServerTick = 9, OwnHull = 7, HullCount = 1, OwnTankCount = 3 };
        header.OwnTanks[0] = 1.5f;
        header.OwnTanks[1] = 0f;
        header.OwnTanks[2] = 999.25f;
        header.Write(ref writer);
        Assert.Equal(25 + 1 + 3 * 4 + 1, writer.Length);
        Assert.Equal(header.WireSize, writer.Length);
        var reader = new NetReader(writer.Written[1..]);
        Assert.True(SnapshotHeader.TryRead(ref reader, out var read));
        Assert.Equal(3, read.OwnTankCount);
        for (var i = 0; i < 3; i++)
            Assert.Equal(BitConverter.SingleToUInt32Bits(header.OwnTanks[i]), BitConverter.SingleToUInt32Bits(read.OwnTanks[i]));

        // A hull without tanks or lasers costs a byte for each count, and one with as many of both as a design may
        // have fills the bound (the lasers' part is LaserHeatTests' to hold).
        writer = new NetWriter(buffer);
        (header with { OwnTankCount = 0 }).Write(ref writer);
        Assert.Equal(27, writer.Length);
        writer = new NetWriter(buffer);
        (header with { OwnTankCount = TankFuel.MaxTanks, OwnLaserCount = LaserHeat.MaxLasers }).Write(ref writer);
        Assert.Equal(SnapshotHeader.MaxWireSize, writer.Length);
        Assert.Equal(25 + 1 + 255 * 4 + 1 + 64 * 2 + 8, SnapshotHeader.MaxWireSize);
        reader = new NetReader(writer.Written[1..]);
        Assert.True(SnapshotHeader.TryRead(ref reader, out read));
        Assert.Equal(TankFuel.MaxTanks, read.OwnTankCount);

        // Tanks that are no number or below nothing are refused with the header, and so is one cut short.
        foreach (var hostile in new[] { float.NaN, float.PositiveInfinity, -1f })
        {
            var bad = header;
            bad.OwnTanks[1] = hostile;
            writer = new NetWriter(buffer);
            bad.Write(ref writer);
            reader = new NetReader(writer.Written[1..]);
            Assert.False(SnapshotHeader.TryRead(ref reader, out _));
        }

        writer = new NetWriter(buffer);
        header.Write(ref writer);
        reader = new NetReader(writer.Written[1..^1]);
        Assert.False(SnapshotHeader.TryRead(ref reader, out _));

        // A record carries the thrust state and no tanks: the receiver reads them empty.
        var bytes = new byte[HullRecord.MaxWireSize];
        var record = new HullRecord { Id = 7, State = HullState.Spawn(Double3.Zero, Content.Pod.Flight) };
        record.State.Thrust = new ThrustState { X = -127, Y = 5, Z = 127, Flags = 29 };
        writer = new NetWriter(bytes);
        record.Write(ref writer);
        reader = new NetReader(writer.Written);
        Assert.True(HullRecord.TryRead(ref reader, out var copy));
        Assert.Equal(record.State.Thrust, copy.State.Thrust);
        Assert.Equal(default, copy.State.Fuel);
        Assert.Equal(4, HullRecord.ThrustBytes);

        // Bits no step writes, and a throttle beyond full, are refused.
        foreach (var hostile in new[] { new ThrustState { Flags = 32 }, new ThrustState { X = sbyte.MinValue }, new ThrustState { Z = sbyte.MinValue } })
        {
            record.State.Thrust = hostile;
            writer = new NetWriter(bytes);
            record.Write(ref writer);
            reader = new NetReader(writer.Written);
            Assert.False(HullRecord.TryRead(ref reader, out _));
        }

        // 255 records without health still fit one snapshot beside a header with every tank a design may have and
        // the heat of every laser: that is what holds a design to 255 tanks, and a record to no more than what it
        // carries now.
        Assert.True(SnapshotHeader.MaxWireSize + byte.MaxValue * (HullRecord.MaxWireSize - HullRecord.MaxHealth * 6) < 65536);
        Assert.False(SnapshotHeader.MaxWireSize + byte.MaxValue * (HullRecord.MaxWireSize - HullRecord.MaxHealth * 6 + 1) < 65536);
    }

    [Fact]
    public void ThePredictedTanksFollowTheServersWhileThrustersEnginesAndBoostBurnAndAWatcherSeesThePartsFire()
    {
        // The test fleet's small pod: one tank of each fuel, so a few ticks' burn stays within the figures compared.
        var content = FleetContent.Value;
        using var first = new LoopbackLink();
        using var second = new LoopbackLink();
        using var server = new ServerHost(new ServerConfig { MaxPlayers = 2 }, content, first.Server, second.Server);
        var clock = new ManualTime();
        using var pilot = new ClientSession(first.Client, content, "Pilot", clock: clock);
        using var watcher = new ClientSession(second.Client, content, "Watcher", clock: clock);
        var hulls = (HullStore)typeof(ServerHost).GetField("_hulls", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(server)!;
        long tick = 0;
        void Step()
        {
            clock.Advance(ManualTime.Tick);
            ((ITickHandler)server).Tick(tick);
            ((ITickHandler)pilot).Tick(tick);
            ((ITickHandler)watcher).Tick(tick++);
        }

        for (var i = 0; i < 2400 && (pilot.AcquireSnapshot().OwnHull < 0 || watcher.AcquireSnapshot().OwnHull < 0 || watcher.AcquireSnapshot().HullCount < 2); i++)
        {
            TestWorkers.Settle(pilot, "the pilot");
            TestWorkers.Settle(watcher, "the watcher");
            Step();
        }

        var start = pilot.AcquireSnapshot();
        Assert.True(start.OwnHull >= 0);
        var id = start.Hulls[start.OwnHull].Id;
        var slot = (int)(id & 0xFFFF);
        var capacity = hulls.Flight(slot).FuelCapacity;
        Assert.Equal(capacity, start.Hulls[start.OwnHull].Fuel.Capacity);
        Assert.Equal(capacity, start.Hulls[start.OwnHull].Fuel.Amount);

        // The pilot is shown every tank of their hull: one figure for each tank of the design, full at the start.
        var tanks = content.Pod.Tanks.ToArray();
        Assert.True(tanks.Length >= 2);
        Assert.Equal(tanks.Select(tank => tank.Capacity), start.Hulls[start.OwnHull].Tanks.ToArray());
        Assert.Same(start.OwnTanks, System.Runtime.InteropServices.MemoryMarshal.TryGetArray(start.Hulls[start.OwnHull].Tanks, out var memory) ? memory.Array : null);

        // Engines with boost, then thrusters on two axes, then the dampeners braking it all: every way fuel is
        // spent. The prediction runs the server's code on the server's tanks, so it is never corrected.
        var worst = 0f;
        var sawEngine = false;
        var sawThrusters = false;
        var sawBoost = false;
        for (var i = 0; i < 600; i++)
        {
            var controls = i < 200 ? new PlayerControls { Forward = 1, Boost = i > 60 }
                : i < 400 ? new PlayerControls { Strafe = -1, Vertical = 0.5f }
                : default;
            pilot.Input.Submit(controls);
            Step();
            var own = pilot.AcquireSnapshot();
            worst = Math.Max(worst, own.PredictionError);

            // What the watcher is shown of the pilot's hull: the thrust state the server stored, a few ticks late.
            var seen = watcher.AcquireSnapshot();
            for (var h = 0; h < seen.HullCount; h++)
            {
                if (seen.Hulls[h].Id != id)
                    continue;
                var thrust = seen.Hulls[h].Thrust;
                sawEngine |= thrust.Firing(FuelKind.Bipropellant, BlockFacing.Back) > 0.9f;
                sawThrusters |= thrust.Firing(FuelKind.Nitrogen, BlockFacing.Right) > 0.9f && thrust.Firing(FuelKind.Nitrogen, BlockFacing.Down) > 0.4f;
                sawBoost |= thrust.Boosted;

                // Nobody is told another player's tanks.
                Assert.Equal(default, seen.Hulls[h].Fuel.Amount);
                Assert.Equal(default, seen.Hulls[h].Fuel.Capacity);
                Assert.True(seen.Hulls[h].Tanks.IsEmpty);
            }
        }

        Assert.True(worst < 0.01f, $"prediction was off by up to {worst:0.000} m");
        Assert.True(sawEngine, "the watcher should have seen the engine fire");
        Assert.True(sawThrusters, "the watcher should have seen the thrusters fire");
        Assert.True(sawBoost, "the watcher should have seen boost");

        // Both fuels were spent, and the client's tanks are the server's a few ticks on: within what those ticks burn.
        var after = pilot.AcquireSnapshot();
        var view = after.Hulls[after.OwnHull];
        var truth = hulls.State(slot).Fuel;
        Assert.True(truth.Bipropellant < capacity.Bipropellant - 1f, $"the engine burnt {capacity.Bipropellant - truth.Bipropellant} kg");
        Assert.True(truth.Nitrogen < capacity.Nitrogen - 0.1f, $"the thrusters burnt {capacity.Nitrogen - truth.Nitrogen} kg");
        Assert.Equal(truth.Bipropellant, view.Fuel.Amount.Bipropellant, 2);
        Assert.Equal(truth.Nitrogen, view.Fuel.Amount.Nitrogen, 2);
        Assert.Equal(capacity, view.Fuel.Capacity);

        // Tank by tank the same holds, and the tanks shown add up to the fuel shown.
        var levels = hulls.BlockStates.Tanks(hulls.Info(slot).BlockState).ToArray();
        Assert.Equal(tanks.Length, view.Tanks.Length);
        for (var i = 0; i < tanks.Length; i++)
        {
            Assert.Equal(levels[i], view.Tanks.Span[i], 2);
            Assert.Equal(view.Fuel.Amount[tanks[i].Kind], view.Tanks.Span[i], 3);
        }

        // At rest nothing burns, and then the client shows the server's own figures: each tank what the server
        // holds of it, in the single precision it is sent in.
        pilot.Input.Submit(new PlayerControls());
        for (var i = 0; i < 1200 && hulls.State(slot).Velocity != Vector3.Zero; i++)
            Step();
        for (var i = 0; i < 30; i++)
            Step();
        Assert.Equal(Vector3.Zero, hulls.State(slot).Velocity);
        view = pilot.AcquireSnapshot().Hulls[pilot.AcquireSnapshot().OwnHull];
        levels = hulls.BlockStates.Tanks(hulls.Info(slot).BlockState).ToArray();
        Assert.Equal(levels.Select(level => (float)level), view.Tanks.ToArray());
        Assert.Equal(hulls.State(slot).Fuel, view.Fuel.Amount);
        Assert.True(levels[0] < tanks[0].Capacity && levels[1] < tanks[1].Capacity);
        Assert.Null(server.Fault);
    }
}
