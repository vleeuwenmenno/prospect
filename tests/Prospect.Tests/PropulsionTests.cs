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
    public void EveryPartPushesAwayFromWhereItFacesAndTheSumsKeepEachDirectionAndFuelApart()
    {
        // One engine facing back, a hot thruster facing right and cold gas thrusters facing every other way.
        var design = Design(
            Box("engine", 1, 0, 0, BlockFacing.Back),
            Box("hotThruster", 2, 0, 0, BlockFacing.Right),
            Box("coldGasThruster", 3, 0, 0, BlockFacing.Left),
            Box("coldGasThruster", 4, 0, 0, BlockFacing.Up),
            Box("coldGasThruster", 5, 0, 0, BlockFacing.Down),
            Box("coldGasThruster", 6, 0, 0, BlockFacing.Front),
            Box("coldGasThruster", 7, 0, 0, BlockFacing.Front, new GridSize(2, 1, 1)));
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
        Assert.Equal(flight.Push(BlockFacing.Back), flight.StrongestPush);

        // The same design turned a quarter about the nose pushes the way its parts now face.
        var turned = design.Boxes.ToArray();
        for (var i = 0; i < turned.Length; i++)
            turned[i] = turned[i] with { At = new GridSize(-turned[i].At.Y, turned[i].At.X, turned[i].At.Z), Size = new GridSize(turned[i].Size.Y, turned[i].Size.X, turned[i].Size.Z), Facing = Facings.Rotate(turned[i].Facing, 2) };
        var other = TestDesigns.Of(turned).Flight;
        Assert.Equal(flight.Push(BlockFacing.Left), other.Push(BlockFacing.Down), 5);
        Assert.Equal(flight.Push(BlockFacing.Up), other.Push(BlockFacing.Left), 5);
        Assert.Equal(flight.Push(BlockFacing.Front), other.Push(BlockFacing.Front), 5);
    }

    [Fact]
    public void LosingAPartTakesItsDirectionAndLosingATankTakesItsRoomAndClampsWhatItHeld()
    {
        var design = Design(
            Box("engine", 1, 0, 0),
            Box("coldGasThruster", 2, 0, 0, BlockFacing.Front),
            Box("coldGasThruster", 3, 0, 0, BlockFacing.Front),
            Box("nitrogenTank", 4, 0, 0));
        using var hulls = new HullStore();
        var field = new HullField(hulls, [design]);
        var hull = hulls.Create(1, 0, design, Double3.Zero).Index;
        var states = hulls.BlockStates;
        var handle = hulls.Info(hull).BlockState;
        var intact = design.Flight;
        var tank = Content.Blocks.Get("nitrogenTank").FuelCapacity;
        Assert.Equal(tank * 2, hulls.State(hull).Fuel.Nitrogen);
        Assert.Equal(intact.FuelCapacity, hulls.State(hull).Fuel);

        // One of the two braking thrusters goes: half the braking, the same forward push over less mass.
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

        // A tank goes: its room goes with it, and what the tanks left cannot hold is gone.
        hulls.State(hull).Fuel.Nitrogen = tank * 1.5f;
        Assert.True(states.Lose(handle, design.BlockCount - 1));
        field.Refit(hull);
        Assert.Equal(tank, hulls.Flight(hull).FuelCapacity.Nitrogen);
        Assert.Equal(tank, hulls.State(hull).Fuel.Nitrogen);

        // Less than the room left is kept as it is.
        hulls.State(hull).Fuel.Nitrogen = tank * 0.25f;
        Assert.True(states.Lose(handle, design.BlockCount - 2));
        field.Refit(hull);
        Assert.Equal(0f, hulls.Flight(hull).Push(BlockFacing.Back));
        Assert.Equal(tank * 0.25f, hulls.State(hull).Fuel.Nitrogen);

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
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
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
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
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
        var flight = Content.Pod.Flight;
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
        Assert.Equal(1, ThrustState.Of(0.001f, 0, 0, true, false, false, false).X);
        Assert.Equal(-1, ThrustState.Of(0, 0, -0.001f, true, false, false, false).Z);
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
    public void DesignFilesOfEveryVersionLoadAndOnlyVersionFourMayNameAFacing()
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
            Assert.Contains("\"version\":4", System.Text.Encoding.UTF8.GetString(stream.ToArray()));
        }

        Assert.Contains("Unknown block facing", Assert.Throws<InvalidDataException>(() => Parse("{ \"version\": 4, " + head + "{ \"at\": [0, 0, 1], \"type\": \"engine\", \"facing\": \"forward\" }] }")).Message);
        Assert.Contains("unsupported design version 5", Assert.Throws<InvalidDataException>(() => Parse("{ \"version\": 5, " + head + "{ \"at\": [0, 0, 1], \"type\": \"engine\" }] }")).Message);
        Assert.Equal(4, BoxDesign.FormatVersion);
    }

    [Fact]
    public void DesignBytesCarryEachFacingAndDecodeRefusesAnUnknownOneAnOlderEncodingAndAPartLargerThanItsTypeAllows()
    {
        var design = Design(
            Box("engine", 1, 0, 0, BlockFacing.Back),
            Box("coldGasThruster", 2, 0, 0, BlockFacing.Left, new GridSize(2, 2, 1)),
            Box("hotThruster", 4, 0, 0, BlockFacing.Up, new GridSize(1, 4, 4)),
            Box("hull", 5, 0, 0, BlockFacing.Down));
        var bytes = DesignBytes.Encode(design);
        Assert.Equal(3, DesignBytes.Version);
        var decoded = DesignBytes.Decode(bytes, SHA256.HashData(bytes), Content);
        Assert.Equal(design.Boxes.ToArray(), decoded.Boxes.ToArray());
        Assert.Equal(design.Flight, decoded.Flight);
        Assert.Equal(BlockFacing.Left, decoded.Boxes[^3].Facing);
        Assert.Equal(BlockFacing.Down, decoded.Boxes[^1].Facing);

        // A block's facing is its last byte. The last block is plating, and the design ends with it.
        var hostile = (byte[])bytes.Clone();
        Assert.Equal((byte)BlockFacing.Down, hostile[^1]);
        hostile[^1] = 6;
        Assert.Contains("Unknown block facing", Assert.Throws<InvalidDataException>(() => DesignBytes.Decode(hostile, SHA256.HashData(hostile), Content)).Message);

        // Another facing is another design: other bytes, another hash, and for a thrust part other figures.
        var other = (byte[])bytes.Clone();
        other[^1] = (byte)BlockFacing.Up;
        Assert.Equal(BlockFacing.Up, DesignBytes.Decode(other, SHA256.HashData(other), Content).Boxes[^1].Facing);
        Assert.False(SHA256.HashData(other).AsSpan().SequenceEqual(SHA256.HashData(bytes)));

        // The encoding before facings is not read: a save or a server that has it is of another version altogether.
        var older = (byte[])bytes.Clone();
        older[0] = 2;
        Assert.Contains("Unknown design encoding", Assert.Throws<InvalidDataException>(() => DesignBytes.Decode(older, SHA256.HashData(older), Content)).Message);

        // A size the type does not allow is refused wherever a design is made: built, drafted or received.
        var limits = Content.Blocks.Limits;
        Assert.Equal(2, BoxDesign.MaxSide(Content.Blocks.Get("coldGasThruster"), limits));
        Assert.Equal(4, BoxDesign.MaxSide(Content.Blocks.Get("hotThruster"), limits));
        Assert.Equal(limits.MaxBoxSide, BoxDesign.MaxSide(Content.Blocks.Get("engine"), limits));
        Assert.Equal(limits.MaxBoxSide, BoxDesign.MaxSide(Content.Blocks.Get("hull"), limits));
        var large = design.Boxes.ToArray();
        large[^3] = large[^3] with { Size = new GridSize(1, 3, 1) };
        var refused = Assert.Throws<InvalidDataException>(() => new BoxDesign("T", 1, large, limits, "received design"));
        Assert.Contains("at most 2 cells a side", refused.Message);
        var draft = BuildDraft.FromBlueprint(design, Content);
        Assert.Equal(DraftEdit.BadSize, draft.CanAdd(new GridSize(9, 0, 0), new GridSize(3, 1, 1), Content.Blocks.Get("coldGasThruster")));
        Assert.Equal(DraftEdit.Done, draft.CanAdd(new GridSize(9, 0, 0), new GridSize(2, 1, 1), Content.Blocks.Get("coldGasThruster")));
        Assert.Equal(DraftEdit.Done, draft.CanAdd(new GridSize(9, 0, 0), new GridSize(3, 1, 1), Content.Blocks.Get("engine")));
        Assert.Equal(DraftEdit.BadSize, draft.Resize(design.BlockCount - 3, 0, 3));
        Assert.Equal(DraftEdit.BadSize, draft.CanAdd(new GridSize(9, 0, 0), new GridSize(1, 5, 1), Content.Blocks.Get("hotThruster")));
    }

    [Fact]
    public void ADraftKeepsEachFacingThroughRotationUndoMoveCopyAndMerge()
    {
        var draft = BuildDraft.FromBlueprint(Content.Pod, Content);
        var count = draft.Boxes.Length;
        var thruster = Content.Blocks.Get("coldGasThruster");
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

        // Two thrusters merge into one that faces as the first did, and no larger than the type allows.
        Assert.Equal(DraftEdit.Done, draft.Add(new GridSize(0, 4, 0), One, thruster, BlockShape.Box, BlockFacing.Down));
        Assert.Equal(DraftEdit.Done, draft.CanMerge([count, count + 1], out var merged));
        Assert.Equal((new GridSize(1, 2, 1), BlockFacing.Up), (merged.Size, merged.Facing));
        Assert.Equal(DraftEdit.Done, draft.Add(new GridSize(0, 5, 0), One, thruster));
        Assert.Equal(DraftEdit.BadSize, draft.CanMerge([count, count + 1, count + 2], out _));

        // The exported design has the facings, and what they do: the thruster facing up pushes the ship down.
        Assert.True(draft.Undo());
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
        Span<byte> buffer = stackalloc byte[SnapshotHeader.MaxWireSize];
        var writer = new NetWriter(buffer);
        var header = new SnapshotHeader { ServerTick = 9, OwnHull = 7, HullCount = 1, OwnFuel = new FuelTanks { Nitrogen = 1.5f, Hydrazine = 0f, Bipropellant = 999.25f } };
        header.Write(ref writer);
        Assert.Equal(SnapshotHeader.MaxWireSize, writer.Length);
        Assert.Equal(37, SnapshotHeader.MaxWireSize);
        var reader = new NetReader(writer.Written[1..]);
        Assert.True(SnapshotHeader.TryRead(ref reader, out var read));
        Assert.Equal(header.OwnFuel, read.OwnFuel);

        // Tanks that are no number or below nothing are refused with the header.
        foreach (var hostile in new[] { float.NaN, float.PositiveInfinity, -1f })
        {
            writer = new NetWriter(buffer);
            (header with { OwnFuel = new FuelTanks { Hydrazine = hostile } }).Write(ref writer);
            reader = new NetReader(writer.Written[1..]);
            Assert.False(SnapshotHeader.TryRead(ref reader, out _));
        }

        // A record carries the thrust state and no tanks: the receiver reads them empty.
        var bytes = new byte[HullRecord.MaxWireSize];
        var record = new HullRecord { Id = 7, State = HullState.Spawn(Double3.Zero, Content.Pod.Flight) };
        record.State.Thrust = new ThrustState { X = -127, Y = 5, Z = 127, Flags = 13 };
        writer = new NetWriter(bytes);
        record.Write(ref writer);
        reader = new NetReader(writer.Written);
        Assert.True(HullRecord.TryRead(ref reader, out var copy));
        Assert.Equal(record.State.Thrust, copy.State.Thrust);
        Assert.Equal(default, copy.State.Fuel);
        Assert.Equal(4, HullRecord.ThrustBytes);

        // Bits no step writes, and a throttle beyond full, are refused.
        foreach (var hostile in new[] { new ThrustState { Flags = 16 }, new ThrustState { X = sbyte.MinValue }, new ThrustState { Z = sbyte.MinValue } })
        {
            record.State.Thrust = hostile;
            writer = new NetWriter(bytes);
            record.Write(ref writer);
            reader = new NetReader(writer.Written);
            Assert.False(HullRecord.TryRead(ref reader, out _));
        }

        // 255 records without health still fit one snapshot, with room to spare for no more than four bytes each.
        Assert.True(SnapshotHeader.MaxWireSize + byte.MaxValue * (HullRecord.MaxWireSize - HullRecord.MaxHealth * 6) < 65536);
    }

    [Fact]
    public void ThePredictedTanksFollowTheServersWhileThrustersEnginesAndBoostBurnAndAWatcherSeesThePartsFire()
    {
        using var first = new LoopbackLink();
        using var second = new LoopbackLink();
        using var server = new ServerHost(new ServerConfig { MaxPlayers = 2 }, Content, first.Server, second.Server);
        var clock = new ManualTime();
        using var pilot = new ClientSession(first.Client, Content, "Pilot", clock: clock);
        using var watcher = new ClientSession(second.Client, Content, "Watcher", clock: clock);
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
        Assert.Null(server.Fault);
    }
}
