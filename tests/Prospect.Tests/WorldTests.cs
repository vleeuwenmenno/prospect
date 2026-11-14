using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Prospect.Game.Client;
using Prospect.Game.Server;
using Prospect.Game.World;
using Xunit;

namespace Prospect.Tests;

public sealed class FlightTests
{
    private static readonly FlightParams Pod = GameContent.Load().Pod.Flight;

    private static HullState Fly(HullState state, FlightInput input, int ticks)
    {
        for (var i = 0; i < ticks; i++)
            FlightModel.Step(ref state, input, Pod, HullSim.Dt);
        return state;
    }

    [Fact]
    public void GasCoverageTickUsesSharedRangeBudget()
    {
        var content = GameContent.Load();
        var cache = new SensorGasCache(content.Galaxy, content.Exploration.SensorRadius);
        Assert.True(GasSensors.Covers(content.Galaxy, cache.At(default), content.Exploration, default, new Double3(250, 0, 0), false));
        Assert.False(GasSensors.Covers(content.Galaxy, cache.At(default), content.Exploration, default, new Double3(5001, 0, 0)));
    }

    [Fact]
    public void JumpPreviewAndRollTakeNoTickMemory()
    {
        var content = GameContent.Load();
        var probe = new AllocationProbe();
        for (var i = 0; i < 100; i++)
            while (probe.Measuring())
            {
                var eligibility = JumpRules.Evaluate(default, new Double3(0, 0, -220000), Pod, content.Exploration, false);
                _ = JumpRules.DamageFraction(eligibility, 1, 1, i);
            }
        Assert.Equal(0, probe.Bytes);
    }

    [Fact]
    public void ControlsMoveTheHullTheWayThePilotExpects()
    {
        var start = HullState.Spawn(Double3.Zero, Pod);

        var forward = Fly(start, new FlightInput { Forward = 127 }, 60);
        Assert.True(forward.Position.Z < -5 && Math.Abs(forward.Position.X) < 1e-3);

        var right = Fly(start, new FlightInput { Strafe = 127 }, 60);
        Assert.True(right.Position.X > 3);

        var up = Fly(start, new FlightInput { Vertical = 127 }, 60);
        Assert.True(up.Position.Y > 3);

        var noseUp = Fly(start, new FlightInput { Pitch = 127 }, 30);
        Assert.True(noseUp.Forward.Y > 0.3f);

        var turnRight = Fly(start, new FlightInput { Yaw = 127 }, 30);
        Assert.True(turnRight.Forward.X > 0.3f);

        var rollRight = Fly(start, new FlightInput { Roll = 127 }, 30);
        var rightWing = Vector3.Transform(Vector3.UnitX, rollRight.Orientation);
        Assert.True(rightWing.Y < -0.3f);
    }

    [Fact]
    public void VelocityPersistsUntilDampenersBleedItOff()
    {
        var moving = Fly(HullState.Spawn(Double3.Zero, Pod), new FlightInput { Forward = 127 }, 120);
        var speed = moving.Velocity.Length();
        Assert.True(speed > 40);

        var coasting = Fly(moving, default, 300);
        Assert.Equal(speed, coasting.Velocity.Length(), 3);

        var damped = Fly(moving, new FlightInput { Dampeners = true }, 300);
        Assert.True(damped.Velocity.Length() < 0.01f);
    }

    [Fact]
    public void BoostIsFasterHeatsUpAndLocksOutWhenOverheated()
    {
        var plain = Fly(HullState.Spawn(Double3.Zero, Pod), new FlightInput { Forward = 127 }, 120);
        var boosted = Fly(HullState.Spawn(Double3.Zero, Pod), new FlightInput { Forward = 127, Boost = true }, 120);
        Assert.True(boosted.Velocity.Length() > plain.Velocity.Length() * 1.5f);
        Assert.True(boosted.Heat > 0.2f);

        // The pod's battery runs dry before its drive overheats, so keep it charged: this is about heat.
        var overheated = boosted;
        for (var i = 0; i < 600 && !overheated.BoostLocked; i++)
        {
            overheated.Energy = Pod.EnergyStorage;
            overheated = Fly(overheated, new FlightInput { Forward = 127, Boost = true }, 1);
        }

        Assert.True(overheated.BoostLocked);

        // Start above normal speed explicitly: the tuned thrust need not get there before overheating.
        overheated.Velocity = overheated.Forward * Pod.BoostMaxSpeed;
        // Locked out, holding boost no longer helps: the hull sheds speed back toward its rated maximum.
        var slowing = Fly(overheated, new FlightInput { Forward = 127, Boost = true }, 120);
        Assert.True(slowing.Velocity.Length() < overheated.Velocity.Length());

        var cooled = Fly(overheated, default, 900);
        Assert.False(cooled.BoostLocked);
        Assert.Equal(0f, cooled.Heat);
    }

    [Fact]
    public void AutopilotReachesItsTargetAndStops()
    {
        var state = HullState.Spawn(Double3.Zero, Pod);
        var order = new HullOrder { Kind = OrderKind.MoveTo, Target = new Double3(900, 250, -1800) };

        var ticks = 0;
        while (order.Kind != OrderKind.None && ticks++ < 60 * 120)
            HullSim.Step(ref state, ref order, default, Pod);

        Assert.Equal(OrderKind.None, order.Kind);
        Assert.True(order.Target.RelativeTo(state.Position).Length() < 3.5f);
        Assert.True(state.Velocity.Length() < 2f);
        Assert.True(ticks < 60 * 60, $"took {ticks} ticks");
    }
}

public sealed class HullDesignTests
{
    private static readonly GameContent Content = GameContent.Load();

    [Theory]
    [InlineData(0, 624f, 352f, 37.5f)]
    [InlineData(1, 1248f, 704f, 37.5f)]
    [InlineData(2, 1248f, 704f, 37.5f)]
    [InlineData(3, 46.4f, 22.4f, 5f)]
    public void ShippedFlightBalanceReachesRuntimeAndEditorStats(int index, float forwardThrust, float lateralThrust, float recharge)
    {
        var design = Content.Designs[index];
        var exported = BuildDraft.FromBlueprint(design, Content).Export();
        Assert.True(exported.Ok, exported.Problem);
        foreach (var hull in new[] { design, exported.Design! })
        {
            var flight = hull.Flight;
            Assert.Equal(forwardThrust / flight.Mass, flight.Acceleration.Z, 4);
            Assert.Equal(lateralThrust / flight.Mass, flight.Acceleration.X, 4);
            Assert.Equal(recharge, flight.ShieldRecharge);
            Assert.Equal(8f, flight.ShieldDelay);
            Assert.Equal(2f, flight.BoostMultiplier);
            Assert.Equal(4f, flight.BoostPowerMultiplier);
            Assert.Equal(60f * MathF.Sqrt(flight.Acceleration.Z), flight.MaxSpeed, 4);
            Assert.Equal(flight.MaxSpeed * 2f, flight.BoostMaxSpeed);
            Assert.Equal(flight.Acceleration.Z, hull.Stats.Acceleration);
            Assert.Equal(flight.MaxSpeed, hull.Stats.MaxSpeed);
            Assert.Equal(flight.BoostMaxSpeed, hull.Stats.BoostMaxSpeed);
            Assert.Equal(recharge, hull.Stats.ShieldRecharge);

            var plain = HullState.Spawn(Double3.Zero, flight);
            var boosted = plain;
            FlightModel.Step(ref plain, new FlightInput { Forward = 127 }, flight, HullSim.Dt);
            FlightModel.Step(ref boosted, new FlightInput { Forward = 127, Boost = true }, flight, HullSim.Dt);
            Assert.Equal(forwardThrust / flight.Mass * HullSim.Dt, plain.Velocity.Length(), 5);
            Assert.Equal(plain.Velocity.Length() * 2f, boosted.Velocity.Length(), 5);
        }
    }

    [Fact]
    public void StatsFollowFromTheBlocks()
    {
        var pod = Content.Pod;
        var stats = pod.Stats;
        var mass = 0f;
        var hull = 0f;
        foreach (var type in pod.Types)
        {
            mass += type.Mass;
            hull += type.Hull;
        }

        Assert.Equal(pod.Cells.Length, stats.Blocks);
        Assert.Equal(mass, stats.Mass, 3);
        Assert.Equal(hull, stats.HullPoints, 3);
        Assert.Equal(new Vector3(5, 2, 4), stats.Size);

        // Main engines only push forward, so the pod accelerates harder than it brakes or strafes.
        var thrusters = Content.Blocks.Get("thruster").Thrust * 2;
        var engine = Content.Blocks.Get("engine").Thrust;
        Assert.Equal(thrusters / mass, stats.Deceleration, 3);
        Assert.Equal((thrusters + engine) / mass, stats.Acceleration, 3);
        Assert.True(stats.Acceleration > stats.Deceleration);

        // Rolling turns the least mass about the shortest lever, so it is the quickest axis.
        Assert.True(stats.TurnRate.Z > stats.TurnRate.Y);
        Assert.Equal(pod.Flight.MaxSpeed, stats.MaxSpeed);
        Assert.True(stats.PowerGenerated > 0 && stats.EnergyStorage > 0 && stats.ShieldCapacity > 0);
    }
}

[Collection(TimingBudgets.Name)]
public sealed class PowerAndDamageTests
{
    private static readonly GameContent Content = GameContent.Load();
    private static readonly FlightParams Pod = Content.Pod.Flight;

    [Fact]
    public void BoostDrainsTheBatteryAndTheGeneratorRefillsIt()
    {
        var state = HullState.Spawn(Double3.Zero, Pod);
        var boost = new FlightInput { Forward = 127, Boost = true };
        for (var i = 0; i < 120; i++)
            FlightModel.Step(ref state, boost, Pod, HullSim.Dt);
        var drained = state.Energy;
        Assert.True(drained < Pod.EnergyStorage * 0.8f);

        // Coasting, the surplus of generation over idle draw goes back into the battery at exactly that rate.
        for (var i = 0; i < 60; i++)
            FlightModel.Step(ref state, default, Pod, HullSim.Dt);
        Assert.Equal(drained + (Pod.PowerGenerated - Pod.IdlePower), state.Energy, 1);

        for (var i = 0; i < 60 * 60; i++)
            FlightModel.Step(ref state, default, Pod, HullSim.Dt);
        Assert.Equal(Pod.EnergyStorage, state.Energy);
    }

    [Fact]
    public void AFlatBatteryStopsBoostAndLimitsThrustToWhatTheGeneratorFeeds()
    {
        var state = HullState.Spawn(Double3.Zero, Pod);
        state.Energy = 0;
        var before = state;
        FlightModel.Step(ref state, new FlightInput { Forward = 127, Boost = true }, Pod, HullSim.Dt);

        Assert.Equal(0f, state.Heat);
        var gained = state.Velocity.Length() / HullSim.Dt;
        Assert.Equal(Pod.Acceleration.Z, gained, 4);
        Assert.Equal(before.Shield, state.Shield);
    }

    [Fact]
    public void TheEnergyFloorComesFromTheRulesAndScalesWithTheBattery()
    {
        Assert.Equal(0.05f, Content.Blocks.Rules.BoostEnergyFloor);
        Assert.Equal(Pod.EnergyStorage * 0.05f, Pod.BoostEnergyFloor, 3);
    }

    [Fact]
    public void BoostWillNotEngageWithTheBatteryBelowItsFloor()
    {
        var boost = new FlightInput { Forward = 127, Boost = true };
        var low = HullState.Spawn(Double3.Zero, Pod);
        low.Energy = Pod.BoostEnergyFloor * 0.9f;
        FlightModel.Step(ref low, boost, Pod, HullSim.Dt);
        Assert.False(low.Boosting);
        Assert.Equal(0f, low.Heat);

        var enough = HullState.Spawn(Double3.Zero, Pod);
        enough.Energy = Pod.BoostEnergyFloor * 1.1f;
        FlightModel.Step(ref enough, boost, Pod, HullSim.Dt);
        Assert.True(enough.Boosting);
        Assert.True(enough.Heat > 0f);
    }

    [Fact]
    public void RunningBoostCarriesOnBelowTheFloorAndCutsOutWhenTheBatteryIsEmpty()
    {
        var state = HullState.Spawn(Double3.Zero, Pod);
        var boost = new FlightInput { Forward = 127, Boost = true };
        var belowFloorWhileBoosting = false;
        var ticks = 0;
        while (state.Energy > 0f && ticks++ < 60 * 30)
        {
            FlightModel.Step(ref state, boost, Pod, HullSim.Dt);
            belowFloorWhileBoosting |= state.Boosting && state.Energy < Pod.BoostEnergyFloor;
        }

        // The pod empties its battery before its drive overheats: it is the energy that stops the boost here.
        Assert.True(belowFloorWhileBoosting);
        Assert.Equal(0f, state.Energy);
        Assert.False(state.Boosting);
        Assert.False(state.BoostLocked);

        // Still holding boost, the hull flies on under plain thrust; it may still be below normal rated speed.
        var speed = state.Velocity.Length();
        FlightModel.Step(ref state, boost, Pod, HullSim.Dt);
        Assert.False(state.Boosting);
        Assert.True(state.Velocity.Length() <= speed + Pod.Acceleration.Z * HullSim.Dt + 0.0001f);
    }

    [Fact]
    public void HoldingBoostOnAnEmptyBatteryWaitsForTheFloorInsteadOfFlickering()
    {
        var state = HullState.Spawn(Double3.Zero, Pod);
        state.Energy = 0f;
        var boost = new FlightInput { Forward = 127, Boost = true };
        var engaged = 0;
        for (var i = 0; i < 60 * 20; i++)
        {
            var before = state;
            FlightModel.Step(ref state, boost, Pod, HullSim.Dt);
            if (!before.Boosting && state.Boosting)
            {
                // Boost starts again only from a battery that had climbed back above the floor.
                Assert.True(before.Energy >= Pod.BoostEnergyFloor);
                engaged++;
            }
        }

        // Each start spends the margin above the floor and then waits for the generator to refill it: a few
        // bursts in twenty seconds, not one every other tick.
        Assert.InRange(engaged, 1, 20);
    }

    [Fact]
    public void AHullWithoutABatteryNeverBoostsButStillFlies()
    {
        var flight = Pod;
        flight.EnergyStorage = 0f;
        flight.BoostEnergyFloor = 0f;
        var state = HullState.Spawn(Double3.Zero, flight);
        for (var i = 0; i < 120; i++)
            FlightModel.Step(ref state, new FlightInput { Forward = 127, Boost = true }, flight, HullSim.Dt);

        Assert.False(state.Boosting);
        Assert.Equal(0f, state.Heat);
        Assert.Equal(0f, state.Energy);
        Assert.True(state.Velocity.Length() > 1f);
    }

    [Fact]
    public void TheShieldRechargesAfterADelay()
    {
        var state = HullState.Spawn(Double3.Zero, Pod);
        state.Shield = Pod.ShieldCapacity - 400;
        state.ShieldCooldown = Pod.ShieldDelay;

        for (var i = 0; i < 60 * 2; i++)
            FlightModel.Step(ref state, default, Pod, HullSim.Dt);
        Assert.Equal(Pod.ShieldCapacity - 400, state.Shield);

        for (var i = 0; i < 60 * 20; i++)
            FlightModel.Step(ref state, default, Pod, HullSim.Dt);
        Assert.Equal(Pod.ShieldCapacity, state.Shield);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ShieldRecoverySpendsOnlyEnergyForPointsRestored(bool offline)
    {
        var flight = Pod;
        flight.PowerGenerated = flight.IdlePower;
        var state = HullState.Spawn(Double3.Zero, flight);
        state.Shield = offline ? 0f : flight.ShieldCapacity - 100f;
        state.ShieldOffline = offline;
        var before = state;
        var order = default(HullOrder);
        HullSim.Step(ref state, ref order, default, flight);
        var cost = (offline ? flight.ShieldRestartPower : flight.ShieldPower) / flight.ShieldRecharge;
        Assert.Equal(flight.ShieldRecharge * HullSim.Dt, state.Shield - before.Shield, 4);
        Assert.Equal((state.Shield - before.Shield) * cost, before.Energy - state.Energy, 4);
        Assert.True(cost > 0f);

        // A fractional last top-up pays only for the missing points.
        state.ShieldOffline = false;
        state.Shield = flight.ShieldCapacity - 0.125f;
        before = state;
        HullSim.Step(ref state, ref order, default, flight);
        Assert.Equal(flight.ShieldCapacity, state.Shield);
        Assert.Equal(0.125f * flight.ShieldPower / flight.ShieldRecharge, before.Energy - state.Energy, 4);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ShieldRecoveryUsesPartialEnergyThenWaitsWithoutGoingNegative(bool offline)
    {
        var flight = Pod;
        flight.PowerGenerated = flight.IdlePower;
        var state = HullState.Spawn(Double3.Zero, flight);
        state.Shield = 0f;
        state.ShieldOffline = offline;
        var cost = (offline ? flight.ShieldRestartPower : flight.ShieldPower) / flight.ShieldRecharge;
        state.Energy = cost * flight.ShieldRecharge * HullSim.Dt / 4f;
        var order = default(HullOrder);
        HullSim.Step(ref state, ref order, default, flight);
        Assert.Equal(flight.ShieldRecharge * HullSim.Dt / 4f, state.Shield, 5);
        Assert.Equal(0f, state.Energy);
        var shield = state.Shield;
        for (var i = 0; i < 120; i++)
        {
            HullSim.Step(ref state, ref order, default, flight);
            Assert.Equal(0f, state.Energy);
            Assert.Equal(shield, state.Shield);
        }
    }

    [Fact]
    public void BoostAndFtlTakeEnergyBeforeShieldRecovery()
    {
        var flight = Pod;
        flight.PowerGenerated = flight.IdlePower;
        flight.BoostEnergyFloor = 0;
        var state = HullState.Spawn(Double3.Zero, flight);
        state.Shield = 0;
        state.Energy = flight.ThrustPower * flight.BoostPowerMultiplier * HullSim.Dt + flight.ShieldPower * HullSim.Dt / 2f;
        var order = default(HullOrder);
        HullSim.Step(ref state, ref order, new FlightInput { Forward = 127, Boost = true }, flight);
        Assert.True(state.Boosting);
        Assert.Equal(flight.ShieldRecharge * HullSim.Dt / 2f, state.Shield, 4);
        Assert.InRange(state.Energy, 0f, 0.00001f);

        state = HullState.Spawn(Double3.Zero, flight);
        state.Shield = 0;
        state.Energy = flight.FtlPower * HullSim.Dt / 2f;
        var energy = state.Energy;
        order = new HullOrder { Kind = OrderKind.Jump, Target = new Double3(0, 0, -220000) };
        HullSim.Step(ref state, ref order, default, flight);
        Assert.Equal(JumpPhase.Charge, state.Jump);
        Assert.Equal(energy, state.JumpTimer);
        Assert.Equal(0f, state.Energy);
        Assert.Equal(0f, state.Shield);
    }

    [Fact]
    public void RecoveryAtTheRestartThresholdPaysNoPremiumForOnlinePoints()
    {
        var flight = Pod;
        flight.PowerGenerated = flight.IdlePower;
        var state = HullState.Spawn(Double3.Zero, flight);
        state.ShieldOffline = true;
        var threshold = flight.ShieldCapacity * flight.ShieldRestartFraction;
        state.Shield = threshold - 0.125f;
        var before = state.Energy;
        var order = default(HullOrder);
        HullSim.Step(ref state, ref order, default, flight);
        Assert.False(state.ShieldOffline);
        Assert.Equal(threshold, state.Shield);
        Assert.Equal(0.125f * flight.ShieldRestartPower / flight.ShieldRecharge, before - state.Energy, 4);
    }

    [Fact]
    public void ShieldRecoveryIsDeterministicAndTakesNoTickMemory()
    {
        var authority = HullState.Spawn(Double3.Zero, Pod);
        authority.Shield = 0;
        authority.ShieldOffline = true;
        var predicted = authority;
        var authoritativeOrder = default(HullOrder);
        var predictedOrder = authoritativeOrder;
        var probe = new AllocationProbe();
        for (var i = 0; i < 2400; i++)
        {
            var input = new FlightInput { Forward = (sbyte)(i % 240 < 60 ? 127 : 0), Boost = i % 240 < 60 };
            var before = authority;
            var orderBefore = authoritativeOrder;
            while (probe.Measuring())
            {
                authority = before;
                authoritativeOrder = orderBefore;
                HullSim.Step(ref authority, ref authoritativeOrder, input, Pod);
            }
            HullSim.Step(ref predicted, ref predictedOrder, input, Pod);
            Assert.Equal(predicted, authority);
            Assert.InRange(authority.Energy, 0f, Pod.EnergyStorage);
        }
        Assert.Equal(0, probe.Bytes);
        Assert.True(authority.Shield > 0f);
    }

    /// <summary>The galaxy the crash tests fly in, and in its home sector an ordinary rock with room around it.</summary>
    private static readonly Galaxy CrashGalaxy = Content.Galaxy.WithSeed(11);
    private static readonly RockRef OrdinaryRock = ClearRock(CrashGalaxy, default, 80f, 260f, new Vector3(-1, 0, 0), new Vector3(0, 1, 0));

    /// <summary>A single fixed rock, so contact tests do not change with galaxy placement or appearance streams.</summary>
    internal static RockField ContactFixture(Galaxy galaxy)
    {
        var rocks = new RockField(galaxy);
        var sector = rocks.Sector(default);
        sector.Asteroids = [new Asteroid { Radius = 128f, Rotation = Quaternion.Identity, Shape = 0, Resource = -1 }];
        sector.AsteroidCount = sector.Summary.AsteroidCount = 1;
        sector.BoundsCenter = Vector3.Zero;
        sector.BoundsRadius = 128f;
        sector.Rocks.Reset(1, galaxy.SectorSize);
        sector.Rocks.Add(0, Vector3.Zero, 128f);
        return rocks;
    }

    /// <summary>How far an asteroid's surface is from its centre toward a side, in metres.</summary>
    internal static float SurfaceToward(in Asteroid rock, Vector3 side) =>
        rock.Radius * AsteroidShape.RadiusAlong(rock.Shape, Vector3.Transform(Vector3.Normalize(side), Quaternion.Conjugate(rock.Rotation)));

    /// <summary>
    /// Finds an asteroid of a given size that a hull can fly at from each of the given sides without meeting
    /// another asteroid on the way. Fields are dense; a test that means to hit one rock has to pick it.
    /// </summary>
    internal static RockRef ClearRock(Galaxy galaxy, SectorCoord sector, float minRadius, float maxRadius, params Vector3[] sides)
    {
        var content = new SectorContent();
        galaxy.Generate(sector, content);
        for (var i = 0; i < content.AsteroidCount; i++)
        {
            var rock = content.Asteroids[i];
            if (rock.Radius < minRadius || rock.Radius > maxRadius)
                continue;

            var clear = true;
            foreach (var side in sides)
            {
                // The corridor from a little beyond where the hull starts down to the rock's centre.
                var start = rock.LocalPosition + Vector3.Normalize(side) * (rock.Radius + 120f);
                for (var j = 0; j < content.AsteroidCount && clear; j++)
                {
                    if (j == i)
                        continue;
                    var other = content.Asteroids[j];
                    var along = Math.Clamp(Vector3.Dot(other.LocalPosition - rock.LocalPosition, start - rock.LocalPosition) / (start - rock.LocalPosition).LengthSquared(), 0f, 1f);
                    var nearest = rock.LocalPosition + (start - rock.LocalPosition) * along;
                    clear = (other.LocalPosition - nearest).Length() > other.Radius + 40f;
                }
            }

            if (clear)
                return new RockRef(sector, i);
        }

        throw new InvalidOperationException($"no asteroid of {minRadius:0} to {maxRadius:0} m with a clear approach in sector {sector}");
    }

    /// <summary>Flies a pod at the ordinary rock from one side until it hits or the ticks run out.</summary>
    private static bool Crash(Vector3 from, Vector3 velocity, in FlightParams flight, out HullState state, out ulong alive, out float[] hp, RockImpulses? impulses = null) =>
        Crash(new RockField(CrashGalaxy), OrdinaryRock, from, velocity, flight, out state, out alive, out hp, impulses);

    /// <summary>Flies a pod at an asteroid from one side, starting 60 m off its surface, until it hits or the ticks run out.</summary>
    private static bool Crash(RockField rocks, RockRef target, Vector3 from, Vector3 velocity, in FlightParams flight, out HullState state, out ulong alive, out float[] hp, RockImpulses? impulses = null)
    {
        var galaxy = rocks.Galaxy;
        var rock = rocks.Sector(target.Sector).Asteroids[target.Index];
        rocks.Displacement(target, 0, out var pushed, out _);
        var rockCenter = galaxy.CenterOf(target.Sector) + rock.LocalPosition + pushed;

        var design = Content.Pod;
        using var states = new BlockStateStore();
        var body = states.Body(design);

        state = HullState.Spawn(rockCenter + Vector3.Normalize(from) * (SurfaceToward(rock, from) + 60f), flight);
        state.Velocity = velocity;
        var order = default(HullOrder);
        var destroyed = false;
        for (var tick = 0; tick < 300 && !destroyed; tick++)
        {
            destroyed = HullSim.Step(ref state, ref order, default, flight, ref body, rocks, tick, impulses);
            if (Vector3.Dot(state.Velocity, velocity) < 0)
                break;
        }

        alive = BlockMasks.Word(body.Alive);
        hp = states.HitPoints(body.State).ToArray();
        return destroyed;
    }

    [Fact]
    public void ALightAsteroidTapShowsFeedbackWithoutDamage()
    {
        var rocks = new RockField(CrashGalaxy);
        var rock = rocks.Sector(OrdinaryRock.Sector).Asteroids[OrdinaryRock.Index];
        var from = -Vector3.UnitX;
        var center = CrashGalaxy.CenterOf(OrdinaryRock.Sector) + rock.LocalPosition;
        using var states = new BlockStateStore();
        var body = states.Body(Content.Pod);
        var state = HullState.Spawn(center + from * (SurfaceToward(rock, from) + 2f), Pod);
        state.Velocity = Vector3.UnitX;
        var order = default(HullOrder);
        for (var tick = 0; tick < 300 && state.ShieldHit.Strength == 0; tick++)
            HullSim.Step(ref state, ref order, default, Pod, ref body, rocks, tick);
        Assert.True(state.ShieldHit.Strength > 0);
        Assert.Equal(Pod.ShieldCapacity, state.Shield);
        Assert.Equal(Pod.HullPoints, state.Hull);
        Assert.Equal(0f, state.ShieldCooldown);
    }

    [Fact]
    public void AGentleBumpBouncesTheHullAndOnlyCostsShield()
    {
        var destroyed = Crash(new Vector3(-1, 0, 0), new Vector3(40, 0, 0), Pod, out var state, out var alive, out _);

        Assert.False(destroyed);
        Assert.True(state.Velocity.X < 0, "the hull should have bounced back");
        Assert.InRange(state.Shield, 1f, Pod.ShieldCapacity - 1f);
        Assert.Equal(Content.Pod.FullMask(), alive);
        Assert.Equal(Pod.HullPoints, state.Hull);
    }

    [Fact]
    public void AHardImpactDestroysTheBlocksThatHitAndTheHullFliesOnWhatIsLeft()
    {
        // At the fixed rock: which blocks a hit this hard reaches hangs on the face the rock turns to it, and a
        // generated rock is another rock whenever generation changes.
        var rock = new RockRef(default, 0);
        var destroyed = Crash(ContactFixture(CrashGalaxy), rock, new Vector3(-1, 0, 0), new Vector3(160, 0, 0), Pod, out var state, out var alive, out var hp);

        // Hitting side-on costs the blocks on that side; the core in the middle survives.
        Assert.False(destroyed);
        Assert.NotEqual(Content.Pod.FullMask(), alive);
        Assert.Equal(Content.Pod.CoreMask(), alive & Content.Pod.CoreMask());
        Assert.Equal(0f, state.Shield);
        Assert.Equal(hp.Sum(), state.Hull, 1);
        Assert.True(state.Hull < Pod.HullPoints);

        Content.Pod.Evaluate([alive], out var flight, out var stats);
        Assert.True(flight.Mass < Pod.Mass);
        Assert.True(stats.Blocks < Content.Pod.Cells.Length);

        // Damage grows with the square of the speed over the safe limit: a faster hit takes more with it.
        Crash(ContactFixture(CrashGalaxy), rock, new Vector3(-1, 0, 0), new Vector3(95, 0, 0), Pod, out var slower, out _, out _);
        Assert.True(slower.Hull > state.Hull);
    }

    [Fact]
    public void LosingTheCoreDestroysTheHull()
    {
        // Belly-first at speed: the core sits in the bottom layer and takes the hit.
        Assert.True(Crash(new Vector3(0, 1, 0), new Vector3(0, -300, 0), Pod, out _, out var alive, out _));
        Assert.NotEqual(Content.Pod.CoreMask(), alive & Content.Pod.CoreMask());
    }

    [Fact]
    public void AFullImpulseBufferResolvesContactAgainstAFixedAsteroid()
    {
        var rocks = new RockField(CrashGalaxy);
        var rock = rocks.Sector(default).Asteroids[OrdinaryRock.Index];
        var heavy = Pod;
        heavy.Mass = rocks.Mass(rock.Radius);
        heavy.Inertia = Pod.Inertia * (heavy.Mass / Pod.Mass);
        heavy.CollisionDamagePerTonneSpeedSquared = 0;
        var full = new RockImpulses();
        full.Count = full.Items.Length;
        var before = full.Items.ToArray();
        Crash(rocks, OrdinaryRock, -Vector3.UnitX, Vector3.UnitX * 20f, heavy, out var actual, out _, out _, full);
        var fixedRocks = new RockField(CrashGalaxy, maxDeltas: 0);
        Crash(fixedRocks, OrdinaryRock, -Vector3.UnitX, Vector3.UnitX * 20f, heavy, out var expected, out _, out _, new RockImpulses());
        Assert.True(actual.Velocity.X < 0f);
        Assert.Equal(expected.Position, actual.Position);
        Assert.Equal(expected.Velocity, actual.Velocity);
        Assert.Equal(full.Items.Length, full.Count);
        Assert.Equal(before, full.Items);
        Assert.Empty(rocks.Deltas.ToArray());
    }

    [Fact]
    public void AHeavyHullShovesAnAsteroidWhichThenDriftsToRest()
    {
        var galaxy = CrashGalaxy;
        var rocks = ContactFixture(galaxy);
        var rock = rocks.Sector(default).Asteroids[0];
        var rockMass = rocks.Mass(rock.Radius);

        // A hull as heavy as the rock itself, with damage switched off to look at the momentum alone.
        var heavy = Pod;
        heavy.Mass = rockMass;
        heavy.Inertia = Pod.Inertia * (rockMass / Pod.Mass);
        heavy.CollisionDamagePerTonneSpeedSquared = 0;
        var impulses = new RockImpulses();
        Crash(rocks, new RockRef(default, 0), new Vector3(-1, 0, 0), new Vector3(20, 0, 0), heavy, out var state, out _, out _, impulses);

        Assert.True(impulses.Count >= 1);
        var delta = impulses.Items[0];
        Assert.Equal(new RockRef(default, 0), delta.Rock);

        // Equal masses share the impulse: the rock leaves with a good part of the speed the hull loses.
        Assert.InRange(delta.Velocity.X, 6f, 14f);
        Assert.True(state.Velocity.X < 14f);

        // The drift dies away, and the rock ends up a finite distance from where it started.
        rocks.Set(delta);
        rocks.Displacement(delta.Rock, delta.StartTick + 60 * 600, out var offset, out var velocity);
        Assert.True(velocity.Length() < 1e-3f);
        Assert.Equal(delta.Velocity.X / galaxy.DriftDecay, offset.X, 1);

        // A pod hitting the same rock barely moves it.
        var light = new RockImpulses();
        Crash(ContactFixture(galaxy), new RockRef(default, 0), new Vector3(0, 1, 0), new Vector3(0, -20, 0), Pod, out _, out _, out _, light);
        Assert.True(light.Count >= 1);
        Assert.True(light.Items[0].Velocity.Length() < 0.1f);
    }

    [Fact]
    public void ATinyAsteroidIsStruckAndKnockedAwayAndAHugeOneDoesNotBudge()
    {
        // Keep this physical pebble fixture at its original size despite the larger production baseline.
        var galaxy = Content.WithSettings(Content.SettingCatalog.Build(null, ["asteroidSize=0.5"])).Galaxy.WithSeed(11);
        var fields = galaxy.AsteroidFields!;
        var side = new Vector3(-1, 0, 0);
        var speed = new Vector3(30, 0, 0);

        // A pebble at the small end of the size table, in the home field.
        var pebble = ClearRock(galaxy, default, fields.SmallestRadius, fields.SmallestRadius * 2f, side);
        var rocks = new RockField(galaxy);
        var struck = new RockImpulses();
        var destroyed = Crash(rocks, pebble, side, speed, Pod, out var state, out var alive, out _, struck);
        Assert.False(destroyed);
        Assert.True(struck.Count >= 1, "the pod flew through the pebble");
        Assert.Equal(pebble, struck.Items[0].Rock);
        Assert.Equal(Content.Pod.FullMask(), alive);

        // Even a pebble outweighs a pod several times over: the pod comes back off it, and the pebble is sent
        // on its way at a speed anyone can see.
        var pebbleSpeed = struck.Items[0].Velocity.X;
        Assert.True(pebbleSpeed > 0.2f, $"the pebble left at {pebbleSpeed:0.00} m/s");
        Assert.True(state.Velocity.X < 0, "the pod should have bounced back");

        // A rock kilometres across: find one, in the nearest sector that has one.
        var giant = default(RockRef);
        var content = new SectorContent();
        for (var ring = 1; ring < 60 && giant.Sector == default; ring++)
            for (var x = -ring; x <= ring && giant.Sector == default; x++)
                for (var z = -ring; z <= ring && giant.Sector == default; z += x == -ring || x == ring ? 1 : 2 * ring)
                {
                    var coord = new SectorCoord(x, 0, z);
                    if (galaxy.Summarize(coord).AsteroidCount < 600)
                        continue;
                    galaxy.Generate(coord, content);
                    if (content.Asteroids[0].Radius >= 1000f)
                        giant = new RockRef(coord, 0);
                }

        Assert.NotEqual(default, giant.Sector);
        var huge = rocks.Sector(giant.Sector).Asteroids[0];
        var approach = default(Vector3);
        foreach (var candidate in new[] { Vector3.UnitY, -Vector3.UnitY, Vector3.UnitX, -Vector3.UnitX, Vector3.UnitZ, -Vector3.UnitZ })
        {
            try
            {
                if (ClearRock(galaxy, giant.Sector, huge.Radius, huge.Radius, candidate) == giant)
                    approach = candidate;
            }
            catch (InvalidOperationException)
            {
                // Another rock is in the way on this side; try the next.
            }
        }

        Assert.NotEqual(default, approach);
        var stopped = new RockImpulses();
        destroyed = Crash(rocks, giant, approach, -approach * 30f, Pod, out state, out alive, out _, stopped);
        Assert.False(destroyed);
        Assert.True(stopped.Count >= 1, $"the pod flew into a {huge.Radius:0} m asteroid without touching it");
        Assert.Equal(giant, stopped.Items[0].Rock);

        // The pod comes back off it and the asteroid stays where it is, to less than a millimetre a second.
        Assert.True(Vector3.Dot(state.Velocity, approach) > 0, "the pod should have bounced back");
        Assert.True(stopped.Items[0].Velocity.Length() < 1e-3f);
        Assert.True(stopped.Items[0].Velocity.Length() * 1000f < pebbleSpeed);

        // It met the surface the mesh shows, not the sphere around it: the pod is outside the rock and close to it.
        var fromCenter = state.Position.RelativeTo(galaxy.CenterOf(giant.Sector) + huge.LocalPosition);
        var surface = SurfaceToward(huge, fromCenter);
        Assert.InRange(fromCenter.Length() - surface, -0.5f, Pod.Radius + 12f);
    }

    [Fact]
    public void APushedAsteroidIsMetWhereItIsNowAndNotWhereItWasGenerated()
    {
        var galaxy = CrashGalaxy;
        var rocks = new RockField(galaxy);
        var rock = rocks.Sector(default).Asteroids[OrdinaryRock.Index];
        var side = new Vector3(0, 1, 0);

        // Shoved 700 m straight up and long since at rest.
        var moved = new Vector3(0, 700, 0);
        rocks.Set(new RockDelta { Rock = OrdinaryRock, Offset = moved, StartTick = 0 });

        // Where it was generated there is nothing left to hit.
        var gone = new RockImpulses();
        var design = Content.Pod;
        using var states = new BlockStateStore();
        var body = states.Body(design);
        var state = HullState.Spawn(galaxy.CenterOf(default) + rock.LocalPosition + side * (SurfaceToward(rock, side) + 60f), Pod);
        state.Velocity = new Vector3(0, -40, 0);
        var order = default(HullOrder);
        for (var tick = 0; tick < 120; tick++)
            Assert.False(HullSim.Step(ref state, ref order, default, Pod, ref body, rocks, tick, gone));
        Assert.Equal(0, gone.Count);
        Assert.Equal(-40f, state.Velocity.Y, 3);

        // Where it is now, it is as solid as ever.
        var met = new RockImpulses();
        Crash(rocks, OrdinaryRock, side, new Vector3(0, -40, 0), Pod, out var bounced, out _, out _, met);
        Assert.True(met.Count >= 1, "the pod flew through the asteroid at its new place");
        Assert.Equal(OrdinaryRock, met.Items[0].Rock);
        Assert.Equal(moved, met.Items[0].Offset);
        Assert.True(bounced.Velocity.Y > 0);
    }

    [Fact]
    public void ATickInsideTheDensestFieldStaysWithinBudget()
    {
        // The budget: one hull's step in the thick of the densest field, asteroids being shoved about included,
        // takes under 100 microseconds in a debug build. A release build is several times faster; either way a
        // thousand hulls in such a field fit in a tick with room to spare. Before the index a step looked at
        // every asteroid of the sector, and this field has thousands.
        const double budgetMicroseconds = 100;
        var galaxy = CrashGalaxy;
        var sector = Sectors.Densest(galaxy);
        var rocks = new RockField(galaxy);
        var content = rocks.Sector(sector);
        Assert.True(content.AsteroidCount > 2500, $"the densest sector has only {content.AsteroidCount} asteroids");

        // The most crowded spot: the asteroid with the most others within 300 m of it.
        var anchor = 0;
        var crowd = -1;
        for (var i = 0; i < content.AsteroidCount; i++)
        {
            var count = 0;
            var near = content.Rocks.Near(content.Asteroids[i].LocalPosition, 300f);
            while (near.Next(out _))
                count++;
            if (count > crowd && content.Asteroids[i].Radius is > 20f and < 120f)
            {
                crowd = count;
                anchor = i;
            }
        }

        Assert.True(crowd > 20, $"only {crowd} asteroids near the most crowded spot");

        // A pod dropped onto that asteroid again and again, bouncing off it and shoving it, stepped as the server
        // steps it: the asteroids it moves are recorded and from then on found where they have drifted to.
        var rock = content.Asteroids[anchor];
        var design = Content.Pod;
        using var states = new BlockStateStore();
        var body = states.Body(design);
        for (var i = 0; i < design.BlockCount; i++)
            states.SetHitPoints(body.State, i, 1e9f);
        var flight = Pod;
        flight.CollisionDamagePerTonneSpeedSquared = 0;
        var state = default(HullState);
        var order = default(HullOrder);
        var impulses = new RockImpulses();
        var contacts = 0;
        var best = double.MaxValue;
        var tick = 0;
        for (var batch = 0; batch < 8; batch++)
        {
            const int steps = 600;
            var start = Stopwatch.GetTimestamp();
            for (var i = 0; i < steps; i++, tick++)
            {
                if (i % 100 == 0)
                {
                    rocks.Displacement(new RockRef(sector, anchor), tick, out var drifted, out _);
                    state = HullState.Spawn(galaxy.CenterOf(sector) + rock.LocalPosition + drifted + Vector3.UnitY * (SurfaceToward(rock, Vector3.UnitY) + Pod.Radius + 5f), flight);
                    state.Velocity = new Vector3(0, -25, 0);
                }

                HullSim.Step(ref state, ref order, default, flight, ref body, rocks, tick, impulses);
                contacts += impulses.Count;
                foreach (ref readonly var delta in impulses.Items.AsSpan(0, impulses.Count))
                    rocks.Set(delta);
                impulses.Count = 0;
            }

            best = Math.Min(best, Stopwatch.GetElapsedTime(start).TotalMicroseconds / steps);
        }

        Assert.True(contacts > 20, $"the pod touched an asteroid only {contacts} times");
        Assert.Equal(galaxy.SectorOf(state.Position), sector);
        TestContext.Current.SendDiagnosticMessage($"hull step in a field of {content.AsteroidCount}: {best:0.00} us");
        Assert.True(best < budgetMicroseconds, $"a hull step in the densest field took {best:0.0} us; the budget is {budgetMicroseconds} us");
    }

    [Fact]
    public void TheCollisionSurfaceIsTheSurfaceOfTheMesh()
    {
        // Every shape reaches its nominal radius somewhere and never exceeds it by more than sampling error.
        for (var shape = 0; shape < Galaxy.AsteroidShapes; shape++)
        {
            var largest = 0f;
            var smallest = float.MaxValue;
            for (var i = 0; i < 2000; i++)
            {
                var direction = Vector3.Normalize(new Vector3(MathF.Sin(i * 1.7f), MathF.Cos(i * 0.9f), MathF.Sin(i * 0.37f + 1f)));
                var point = AsteroidShape.Point(shape, direction);
                largest = Math.Max(largest, point.Length());
                smallest = Math.Min(smallest, point.Length());

                // The distance to the surface along the point's own direction is the point's distance.
                Assert.Equal(point.Length(), AsteroidShape.RadiusAlong(shape, Vector3.Normalize(point)), 3);
            }

            if (shape == AsteroidShape.Icicle)
            {
                // Sharp tips need an axial sample; arbitrary directions do not approach them closely.
                Assert.Equal(Vector3.UnitX, AsteroidShape.Point(shape, Vector3.UnitX));
                Assert.InRange(largest, 0.1f, 1.000001f);
                Assert.True(smallest > 0.1f);
            }
            else
            {
                Assert.InRange(largest, 0.9f, 1.03f);
                Assert.True(smallest > 0.25f);
            }
        }
    }
}

/// <summary>Hulls running into each other, stepped the way the server steps them.</summary>
public sealed class HullCollisionTests : IDisposable
{
    private static readonly GameContent Content = GameContent.Load();
    private static readonly int Vessel = Content.StartingFleet[0];
    private readonly HullStore _hulls = new();
    private readonly HullField _field;

    public HullCollisionTests() => _field = new HullField(_hulls, Content.Designs);

    public void Dispose() => _hulls.Dispose();

    private int Add(int design, Double3 position, Vector3 velocity = default)
    {
        var index = _hulls.Create(1, (byte)design, Content.Designs[design], position).Index;
        _hulls.State(index).Velocity = velocity;
        return index;
    }

    /// <summary>Steps every hull once, in slot order, and removes the ones that were destroyed.</summary>
    private void Tick(FlightInput input = default)
    {
        _field.BeginRound();
        for (var i = 0; i < _hulls.Slots; i++)
        {
            if (!_hulls.IsAliveAt(i))
                continue;
            if (_field.Step(i, input))
                _hulls.Destroy(_hulls.HandleAt(i));
            foreach (var lost in _field.Lost)
                _hulls.Destroy(_hulls.HandleAt(lost));
        }
    }

    /// <summary>Runs a number of ticks with nobody at the controls. Returns the least the two hulls were apart.</summary>
    private float Run(int ticks, int a, int b)
    {
        var closest = float.MaxValue;
        for (var i = 0; i < ticks; i++)
        {
            Tick();
            if (_hulls.IsAliveAt(a) && _hulls.IsAliveAt(b))
                closest = Math.Min(closest, Gap(a, b));
        }

        return closest;
    }

    private Vector3 Momentum(int hull) => _hulls.State(hull).Velocity * _hulls.Flight(hull).Mass;

    /// <summary>Energy of motion and of spin, in tonne square metres per second squared.</summary>
    private float Energy(int hull)
    {
        ref var state = ref _hulls.State(hull);
        ref var flight = ref _hulls.Flight(hull);
        return 0.5f * flight.Mass * state.Velocity.LengthSquared() + 0.5f * Vector3.Dot(state.AngularVelocity * flight.Inertia, state.AngularVelocity);
    }

    /// <summary>The smallest distance between a block one hull still has and one the other still has, centre to centre.</summary>
    private float Gap(int a, int b)
    {
        var smallest = float.MaxValue;
        var first = Content.Designs[_hulls.Info(a).Design];
        var second = Content.Designs[_hulls.Info(b).Design];
        for (var i = 0; i < first.BlockOffsets.Length; i++)
        {
            if ((_hulls.Mask(a) & 1UL << i) == 0)
                continue;
            var world = _hulls.State(a).Position + Vector3.Transform(first.BlockOffsets[i], _hulls.State(a).Orientation);
            for (var j = 0; j < second.BlockOffsets.Length; j++)
            {
                if ((_hulls.Mask(b) & 1UL << j) == 0)
                    continue;
                var other = _hulls.State(b).Position + Vector3.Transform(second.BlockOffsets[j], _hulls.State(b).Orientation);
                smallest = Math.Min(smallest, world.RelativeTo(other).Length());
            }
        }

        return smallest;
    }

    /// <summary>
    /// Where to put a hull so that one of its blocks meets a block of another hull, which sits at the origin,
    /// squarely along the X axis.
    /// </summary>
    private static Double3 Facing(HullDesign design, (int, int, int) cell, HullDesign other, (int, int, int) otherCell, double x)
    {
        var mine = design.BlockOffsets[Array.IndexOf(design.Cells, cell)];
        var theirs = other.BlockOffsets[Array.IndexOf(other.Cells, otherCell)];
        return new Double3(x, theirs.Y - mine.Y, theirs.Z - mine.Z);
    }

    private int DamagedBlocks(int hull)
    {
        var design = Content.Designs[_hulls.Info(hull).Design];
        var hp = _hulls.BlockHp(hull);
        var count = 0;
        for (var i = 0; i < design.Types.Length; i++)
            count += hp[i] < design.Types[i].Hull ? 1 : 0;
        return count;
    }

    [Fact]
    public void AFastRamDamagesTheBlocksThatTouchedOnBothHullsAndConservesMomentum()
    {
        var vessel = Add(Vessel, Double3.Zero);
        var pod = Add(Content.PodDesign, new Double3(30, 0, 0), new Vector3(-80, 0, 0));
        var before = Momentum(pod) + Momentum(vessel);
        var shed = Vector3.Zero;
        var closest = float.MaxValue;
        var checkedFirstDamage = false;
        for (var tick = 0; tick < 60; tick++)
        {
            _field.BeginRound();
            foreach (var stepping in new[] { vessel, pod })
            {
                var podMass = _hulls.Flight(pod).Mass;
                var vesselMass = _hulls.Flight(vessel).Mass;
                var momentum = Momentum(pod) + Momentum(vessel);
                Assert.False(_field.Step(stepping, default));
                Assert.Empty(_field.Lost.ToArray());
                // Lost material leaves with its hull's velocity at that impact. Subsequent box contacts use
                // the reduced mass, so using the original masses after sixty ticks is not momentum accounting.
                var debris = _hulls.State(pod).Velocity * (podMass - _hulls.Flight(pod).Mass)
                    + _hulls.State(vessel).Velocity * (vesselMass - _hulls.Flight(vessel).Mass);
                if (!checkedFirstDamage && DamagedBlocks(vessel) > 0)
                {
                    var hp = _hulls.BlockHp(vessel);
                    var design = Content.Designs[Vessel];
                    for (var i = 0; i < design.Types.Length; i++)
                        Assert.True(hp[i] >= design.Types[i].Hull || design.BlockOffsets[i].X > 0, $"block {i} on the far side was damaged at first impact");
                    checkedFirstDamage = true;
                }
                shed += debris;
                closest = Math.Min(closest, Gap(pod, vessel));
                Assert.True((Momentum(pod) + Momentum(vessel) + debris - momentum).Length() < before.Length() * 1e-3f);
            }
        }

        var podVelocity = _hulls.State(pod).Velocity;
        var vesselVelocity = _hulls.State(vessel).Velocity;
        Assert.True(podVelocity.X > -60f, $"the pod kept {podVelocity.X} m/s");
        Assert.True(vesselVelocity.X < -15f, $"the vessel only reached {vesselVelocity.X} m/s");
        var after = Momentum(pod) + Momentum(vessel) + shed;
        Assert.True((after - before).Length() < before.Length() * 1e-3f, $"momentum went from {before} to {after}");

        // The shields went first, then the blocks that touched: some on each hull, and not all of either.
        Assert.Equal(0f, _hulls.State(pod).Shield);
        Assert.Equal(0f, _hulls.State(vessel).Shield);
        Assert.InRange(DamagedBlocks(pod), 1, Content.Pod.Cells.Length - 1);
        Assert.InRange(DamagedBlocks(vessel), 1, Content.Designs[Vessel].Cells.Length - 1);
        Assert.True(_hulls.State(pod).Hull < Content.Pod.Flight.HullPoints);
        Assert.True(_hulls.State(vessel).Hull < Content.Designs[Vessel].Flight.HullPoints);

        // Later box contacts can reach another side after the first impact turns either hull.
        Assert.True(checkedFirstDamage);

        // At no point were they inside each other, and they have parted.
        Assert.True(closest > 0.95f, $"the hulls were {closest} m apart at the closest");
        Assert.True(Gap(pod, vessel) > 3f);
    }

    [Fact]
    public void AHeavyHullBarelyMovesWhenALightOneHitsIt()
    {
        // The drone's left thruster meets the vessel's right one squarely.
        var vessel = Add(Vessel, Double3.Zero);
        var from = Facing(Content.Designs[Content.DroneDesign], (-1, 0, 0), Content.Designs[Vessel], (2, 0, 0), 20);
        var drone = Add(Content.DroneDesign, from, new Vector3(-40, 0, 0));
        var droneShield = _hulls.State(drone).Shield;
        var vesselShield = _hulls.State(vessel).Shield;

        Run(60, drone, vessel);

        // The drone is thrown back; the vessel, twenty times its mass, picks up a twentieth of the change.
        Assert.True(_hulls.State(drone).Velocity.X > 0, $"the drone kept going at {_hulls.State(drone).Velocity}");
        Assert.InRange(_hulls.State(vessel).Velocity.Length(), 0.2f, 3f);

        // Both take the same damage, and it is the drone's weight that sets it: a scratch on the vessel's shield.
        var onDrone = droneShield - _hulls.State(drone).Shield;
        var onVessel = vesselShield - _hulls.State(vessel).Shield;
        Assert.InRange(onDrone, 20f, 110f);
        Assert.Equal(onDrone, onVessel, 1);
        Assert.Equal(Content.Designs[Vessel].Flight.HullPoints, _hulls.State(vessel).Hull);
    }

    [Fact]
    public void AHeavyHullShovesALightOneAsideAndKeepsGoing()
    {
        var drone = Add(Content.DroneDesign, Double3.Zero);
        var from = Facing(Content.Designs[Vessel], (-2, 0, 0), Content.Designs[Content.DroneDesign], (1, 0, 0), 20);
        var vessel = Add(Vessel, from, new Vector3(-40, 0, 0));

        Run(60, drone, vessel);

        Assert.InRange(_hulls.State(vessel).Velocity.X, -40f, -37f);
        Assert.True(_hulls.State(drone).Velocity.X < -40f, $"the drone only reached {_hulls.State(drone).Velocity}");
    }

    [Fact]
    public void ATouchBelowTheSafeSpeedDoesNoDamageAndDoesNotBounce()
    {
        var vessel = Add(Vessel, Double3.Zero);
        var pod = Add(Content.PodDesign, new Double3(8, 0, 0), new Vector3(-4, 0, 0));
        var before = Momentum(pod) + Momentum(vessel);

        var closest = Run(180, pod, vessel);

        Assert.Equal(Content.Pod.Flight.ShieldCapacity, _hulls.State(pod).Shield);
        Assert.Equal(Content.Designs[Vessel].Flight.ShieldCapacity, _hulls.State(vessel).Shield);
        Assert.True(_hulls.State(pod).ShieldHit.Strength > 0);
        Assert.True(_hulls.State(vessel).ShieldHit.Strength > 0);
        Assert.Equal(0f, _hulls.State(pod).ShieldCooldown);
        Assert.Equal(0f, _hulls.State(vessel).ShieldCooldown);
        Assert.Equal(0, DamagedBlocks(pod) + DamagedBlocks(vessel));
        Assert.True(closest > 0.95f, $"the hulls were {closest} m apart at the closest");

        // A touch this slow is a nudge: the pod is not thrown back, and the vessel is carried along with it.
        Assert.True((Momentum(pod) + Momentum(vessel) - before).Length() < before.Length() * 1e-3f);
        Assert.True(_hulls.State(pod).Velocity.X < 0, "the pod should not have bounced back");
        Assert.True(_hulls.State(vessel).Velocity.X < -0.5f);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(0.3f)]
    [InlineData(2.5f)]
    public void HullsLeftInsideEachOtherSeparateAndRestWithoutDamageOrMotion(float offset)
    {
        var a = Add(Vessel, Double3.Zero);
        var b = Add(Vessel, new Double3(offset, 0, offset * 0.5));

        // Pushing apart moves the hulls without giving them any speed, so nothing is ever gained.
        for (var i = 0; i < 300; i++)
        {
            Tick(new FlightInput { Dampeners = true });
            Assert.Equal(0f, Energy(a) + Energy(b));
        }

        Assert.True(Gap(a, b) > 0.99f, $"still {Gap(a, b)} m apart at the closest");
        var settled = (_hulls.State(a).Position, _hulls.State(b).Position);
        for (var i = 0; i < 60; i++)
            Tick(new FlightInput { Dampeners = true });
        Assert.Equal(settled, (_hulls.State(a).Position, _hulls.State(b).Position));
        Assert.Equal(Content.Designs[Vessel].Flight.ShieldCapacity, _hulls.State(a).Shield);
        Assert.Equal(Content.Designs[Vessel].Flight.ShieldCapacity, _hulls.State(b).Shield);
        Assert.Equal(0, DamagedBlocks(a) + DamagedBlocks(b));
    }

    [Fact]
    public void HullsDriftingAgainstEachOtherLoseEnergyAndComeToRestUndamaged()
    {
        // Two vessels drifting together off-centre, slower than the safe speed, with nobody at the controls.
        var a = Add(Vessel, Double3.Zero, new Vector3(1.5f, 0, 0));
        var b = Add(Vessel, new Double3(7, 0, 2), new Vector3(-1.5f, 0, 0.2f));

        var energy = Energy(a) + Energy(b);
        for (var i = 0; i < 600; i++)
        {
            Tick();
            var now = Energy(a) + Energy(b);
            Assert.True(now <= energy * 1.0001f + 1e-4f, $"energy rose from {energy} to {now} on tick {i}");
            energy = now;
        }

        Assert.True(Gap(a, b) > 0.99f);

        // With the dampeners on they stop, and stay stopped.
        for (var i = 0; i < 600; i++)
            Tick(new FlightInput { Dampeners = true });
        Assert.True(_hulls.State(a).Velocity.Length() + _hulls.State(b).Velocity.Length() < 0.01f);
        var settled = (_hulls.State(a).Position, _hulls.State(b).Position);
        for (var i = 0; i < 120; i++)
            Tick(new FlightInput { Dampeners = true });
        Assert.True(_hulls.State(a).Position.RelativeTo(settled.Item1).Length() < 0.01f);
        Assert.True(_hulls.State(b).Position.RelativeTo(settled.Item2).Length() < 0.01f);
        Assert.Equal(Content.Designs[Vessel].Flight.ShieldCapacity, _hulls.State(a).Shield);
        Assert.Equal(0, DamagedBlocks(a) + DamagedBlocks(b));
    }

    [Fact]
    public void FleetHullsOrderedToTheSameSpotLeanOnEachOtherWithoutShakingOrWear()
    {
        var target = new Double3(0, 0, -200);
        var a = Add(Vessel, new Double3(-30, 0, 0));
        var b = Add(Vessel, new Double3(30, 0, 0));
        _hulls.Info(a).Order = new HullOrder { Kind = OrderKind.MoveTo, Target = target };
        _hulls.Info(b).Order = new HullOrder { Kind = OrderKind.MoveTo, Target = target };

        for (var i = 0; i < 60 * 40; i++)
            Tick(new FlightInput { Dampeners = true });

        // Whichever did not get the spot is pressed against the one that did. Neither moves more than a
        // hair from tick to tick, and nothing wears off either of them.
        var worst = 0f;
        for (var i = 0; i < 60 * 5; i++)
        {
            var before = (_hulls.State(a).Position, _hulls.State(b).Position);
            Tick(new FlightInput { Dampeners = true });
            worst = Math.Max(worst, _hulls.State(a).Position.RelativeTo(before.Item1).Length());
            worst = Math.Max(worst, _hulls.State(b).Position.RelativeTo(before.Item2).Length());
        }

        Assert.True(worst < 0.02f, $"a hull moved {worst} m in one tick");
        Assert.True(Gap(a, b) > 0.98f);
        Assert.True(target.RelativeTo(_hulls.State(a).Position).Length() < 15f);
        Assert.True(target.RelativeTo(_hulls.State(b).Position).Length() < 15f);
        Assert.Equal(Content.Designs[Vessel].Flight.ShieldCapacity, _hulls.State(a).Shield);
        Assert.Equal(Content.Designs[Vessel].Flight.ShieldCapacity, _hulls.State(b).Shield);
        Assert.Equal(0, DamagedBlocks(a) + DamagedBlocks(b));
    }

    [Fact]
    public void ADeadBlockDoesNotCollide()
    {
        // A pod stripped to the one block at its far left. A drone dropped on where its far right block was
        // falls straight through; dropped on the block that is left, it is stopped.
        var design = Content.Pod;
        var left = Array.FindIndex(design.Cells, c => c.X == -2);
        var right = Array.FindIndex(design.Cells, c => c.X == 2);

        Vector3 Drop(int over)
        {
            _hulls.Clear();
            var pod = Add(Content.PodDesign, Double3.Zero);
            _hulls.Lose(pod, design.FullMask() & ~(1UL << left));
            var drone = Add(Content.DroneDesign, Double3.Zero + design.BlockOffsets[over] + new Vector3(0, 6, 0), new Vector3(0, -4, 0));
            for (var i = 0; i < 180; i++)
                Tick();
            Assert.Equal(1UL << left, _hulls.Mask(pod));
            return _hulls.State(drone).Velocity;
        }

        Assert.Equal(new Vector3(0, -4, 0), Drop(right));
        Assert.True(Drop(left).Y > -3.9f, "the drone should have been stopped by the block that is left");
    }

    [Fact]
    public void AHullTooFastToBeCaughtInsideAnotherStillStrikesIt()
    {
        // 400 m/s is more than six metres a tick, further than either hull is thick. Damage is switched off to
        // watch the motion alone.
        var pod = Add(Content.PodDesign, Double3.Zero);
        var drone = Add(Content.DroneDesign, new Double3(101, 0, 0.5), new Vector3(-400, 0, 0));
        _hulls.Flight(pod).CollisionDamagePerTonneSpeedSquared = 0;
        _hulls.Flight(drone).CollisionDamagePerTonneSpeedSquared = 0;

        var closest = float.MaxValue;
        for (var i = 0; i < 60; i++)
        {
            Tick();
            closest = Math.Min(closest, Gap(drone, pod));
        }

        // It never got inside the pod, let alone through it: it was turned aside, and the pod thrown back.
        Assert.True(closest > 0.74f, $"the hulls were {closest} m apart at the closest");
        Assert.True(_hulls.State(drone).Velocity.X > -200f, $"the drone kept {_hulls.State(drone).Velocity}");
        Assert.True(_hulls.State(pod).Velocity.X < -10f);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void HullsCoastingNoseToTailCloserThanATicksFlightDoNotDisturbEachOther(bool trailingStepsFirst)
    {
        // Two vessels one behind the other at the same speed, with less room between them than they cover in
        // a tick. Whichever steps first has moved a tick on while the other has not, and they never meet.
        var design = Content.Designs[Vessel];
        var velocity = new Vector3(0, 0, -design.Flight.MaxSpeed);
        var room = design.Flight.MaxSpeed * HullSim.Dt * 0.25f;
        var length = design.BlockOffsets.Max(o => o.Z) - design.BlockOffsets.Min(o => o.Z);
        var behind = new Double3(0, 0, length + design.BlockSize + room);
        var trailing = trailingStepsFirst ? Add(Vessel, behind, velocity) : -1;
        var leading = Add(Vessel, Double3.Zero, velocity);
        if (!trailingStepsFirst)
            trailing = Add(Vessel, behind, velocity);
        Assert.InRange(Gap(leading, trailing) - design.BlockSize, room * 0.5f, room * 1.5f);

        for (var i = 0; i < 300; i++)
        {
            Tick();
            var spacing = _hulls.State(trailing).Position.RelativeTo(_hulls.State(leading).Position);
            Assert.True((spacing - behind.RelativeTo(Double3.Zero)).Length() < 1e-3f, $"the hulls are {spacing} apart on tick {i}");
            Assert.True((_hulls.State(leading).Velocity - velocity).Length() < 1e-3f, $"the leading hull flies at {_hulls.State(leading).Velocity} on tick {i}");
            Assert.True((_hulls.State(trailing).Velocity - velocity).Length() < 1e-3f, $"the trailing hull flies at {_hulls.State(trailing).Velocity} on tick {i}");
        }

        Assert.Equal(Vector3.Zero, _hulls.State(leading).AngularVelocity + _hulls.State(trailing).AngularVelocity);
        Assert.Equal(design.Flight.ShieldCapacity, _hulls.State(leading).Shield);
        Assert.Equal(design.Flight.ShieldCapacity, _hulls.State(trailing).Shield);
        Assert.Equal(0, DamagedBlocks(leading) + DamagedBlocks(trailing));
    }

    [Fact]
    public void AHullWhosePilotSentNothingIsMetWhereItStands()
    {
        // The leading vessel has the same velocity on record, but its pilot's input has not arrived and it does
        // not move. The one behind, with less room than it covers in a tick, must not fly into it.
        var design = Content.Designs[Vessel];
        var velocity = new Vector3(0, 0, -design.Flight.MaxSpeed);
        var room = design.Flight.MaxSpeed * HullSim.Dt * 0.25f;
        var length = design.BlockOffsets.Max(o => o.Z) - design.BlockOffsets.Min(o => o.Z);
        var trailing = Add(Vessel, new Double3(0, 0, length + design.BlockSize + room), velocity);
        var leading = Add(Vessel, Double3.Zero, velocity);

        for (var i = 0; i < 5; i++)
        {
            _field.BeginRound();
            _field.Hold(leading);
            _field.Step(trailing, default);
            Assert.True(Gap(leading, trailing) > design.BlockSize * 0.95f, $"the hulls are {Gap(leading, trailing)} m apart on tick {i}");
        }

        Assert.Equal(Double3.Zero, _hulls.State(leading).Position);
    }

    [Fact]
    public void HowFarAHullCameInARoundCountsWhatStruckItBeforeItStepped()
    {
        // The drone is in the lower slot: it steps first and strikes the pod, which has yet to fly its tick.
        var drone = Add(Content.DroneDesign, new Double3(101, 0, 0.5), new Vector3(-400, 0, 0));
        var pod = Add(Content.PodDesign, Double3.Zero);
        _hulls.Flight(pod).CollisionDamagePerTonneSpeedSquared = 0;
        _hulls.Flight(drone).CollisionDamagePerTonneSpeedSquared = 0;

        var struck = false;
        for (var i = 0; i < 60; i++)
        {
            var droneFrom = _hulls.State(drone).Position;
            var podFrom = _hulls.State(pod).Position;
            Tick();
            struck |= _hulls.State(pod).Velocity != Vector3.Zero;
            Assert.True((_hulls.Info(drone).Flown - _hulls.State(drone).Position.RelativeTo(droneFrom)).Length() < 1e-3f, $"the drone's record is off on tick {i}");
            Assert.True((_hulls.Info(pod).Flown - _hulls.State(pod).Position.RelativeTo(podFrom)).Length() < 1e-3f, $"the pod's record is off on tick {i}");
        }

        Assert.True(struck);
    }

    [Fact]
    public void AHullInTheTunnelIsNotThereAndOneJustArrivedDoesNoHarm()
    {
        var vessel = Add(Vessel, Double3.Zero);
        var pod = Add(Content.PodDesign, new Double3(12, 0, 0), new Vector3(-60, 0, 0));

        // In the tunnel the vessel's position is already its destination, but it is not there yet.
        _hulls.State(vessel).Jump = JumpPhase.Tunnel;
        for (var i = 0; i < 24; i++)
            _field.Step(pod, default);
        Assert.Equal(new Vector3(-60, 0, 0), _hulls.State(pod).Velocity);
        Assert.True(_hulls.State(pod).Position.X < -6);

        // It comes out with the pod flying through the spot. They knock into each other, and neither is hurt.
        _hulls.State(pod).Position = new Double3(3, 0, 0);
        _hulls.State(vessel).Jump = JumpPhase.Arrive;
        for (var i = 0; i < 30; i++)
            Tick();
        Assert.NotEqual(new Vector3(-60, 0, 0), _hulls.State(pod).Velocity);
        Assert.True(_hulls.State(vessel).Velocity.Length() > 1f);
        Assert.True(Gap(pod, vessel) > 0.99f);
        Assert.Equal(Content.Pod.Flight.ShieldCapacity, _hulls.State(pod).Shield);
        Assert.Equal(Content.Designs[Vessel].Flight.ShieldCapacity, _hulls.State(vessel).Shield);
        Assert.Equal(0, DamagedBlocks(pod) + DamagedBlocks(vessel));
    }

    [Fact]
    public void AHullDestroyedByTheOneThatRanIntoItIsReportedLostAndStillTakesItsShare()
    {
        // A vessel coming up under a pod at speed: the pod's core is in its belly and takes the hit.
        var pod = Add(Content.PodDesign, Double3.Zero);
        var vessel = Add(Vessel, new Double3(0, -20, 0), new Vector3(0, 250, 0));

        var lost = false;
        for (var i = 0; i < 30 && !lost; i++)
        {
            Assert.False(_field.Step(vessel, default), "the vessel's core is under its deck and should survive");
            lost = _field.Lost.Length == 1 && _field.Lost[0] == pod;
        }

        Assert.True(lost, "the pod should have been reported destroyed");
        Assert.NotEqual(Content.Pod.CoreMask(), _hulls.Mask(pod) & Content.Pod.CoreMask());
        Assert.True(_hulls.State(pod).Velocity.Y > 50f, "the pod should still have been thrown upward");
        Assert.True(DamagedBlocks(vessel) > 0);
    }

    [Fact]
    public void AHullDestroyedByItsOwnRamStillShovesAndDamagesTheOther()
    {
        // The same meeting the other way round: the pod drops belly-first onto the vessel's deck.
        var vessel = Add(Vessel, Double3.Zero);
        var pod = Add(Content.PodDesign, new Double3(0, 20, 0), new Vector3(0, -250, 0));

        var destroyed = false;
        for (var i = 0; i < 30 && !destroyed; i++)
            destroyed = _field.Step(pod, default);

        Assert.True(destroyed, "the pod should have destroyed itself");
        Assert.Equal(0, _field.Lost.Length);
        Assert.True(_hulls.State(vessel).Velocity.Y < -30f, "the vessel should have been pushed down");
        Assert.Equal(0f, _hulls.State(vessel).Shield);
        Assert.True(DamagedBlocks(vessel) > 0);
    }

    [Fact]
    public void SteppingHullsThatAreInContactAllocatesNothing()
    {
        // A pod ramming a vessel, and beside them two vessels that start inside each other.
        float Scenario()
        {
            _hulls.Clear();
            var struck = Add(Vessel, Double3.Zero);
            Add(Content.PodDesign, new Double3(0, 0, 12), new Vector3(0, 0, -60));
            Add(Vessel, new Double3(40, 0, 0));
            Add(Vessel, new Double3(40.4, 0, 0.3));
            for (var i = 0; i < 240; i++)
                Tick(new FlightInput { Dampeners = true });
            return _hulls.State(struck).Shield;
        }

        // Once through to have everything compiled, then measure.
        Scenario();
        var allocation = new AllocationProbe();
        var shield = 0f;
        while (allocation.Measuring()) shield = Scenario();
        Assert.Equal(0, allocation.Bytes);
        Assert.True(shield < Content.Designs[Vessel].Flight.ShieldCapacity, "the pod never hit the vessel");
    }

    [Fact]
    public void APredictingClientGetsTheSameContactAsTheServerAndLeavesTheBlocksToIt()
    {
        var hold = new FlightInput { Dampeners = true };
        var thrust = new FlightInput { Forward = 127, Dampeners = true };

        // The client's copy of the same two hulls, in the same slots. It has no say over them, and it does not
        // know how many hull points their blocks have left.
        using var copy = new HullStore();
        var predicted = new HullField(copy, Content.Designs, authoritative: false);

        (int Pod, int Vessel) Start(float speed)
        {
            _hulls.Clear();
            copy.Clear();
            var vessel = Add(Vessel, Double3.Zero);
            var pod = Add(Content.PodDesign, new Double3(6, 0, 0), new Vector3(-speed, 0, 0));
            copy.Create(1, (byte)Vessel, Content.Designs[Vessel], Double3.Zero);
            copy.Create(1, (byte)Content.PodDesign, Content.Pod, Double3.Zero);
            copy.State(pod) = _hulls.State(pod);
            return (pod, vessel);
        }

        // A ram the shields can take: the client's two hulls move exactly as the server's do, tick for tick.
        var (pod, vessel) = Start(50);
        for (var i = 0; i < 30; i++)
        {
            _field.BeginRound();
            _field.Step(pod, thrust);
            _field.Step(vessel, hold);
            predicted.BeginRound();
            predicted.Step(pod, thrust);
            predicted.Step(vessel, hold);
            foreach (var hull in new[] { pod, vessel })
            {
                Assert.Equal(_hulls.State(hull).Position, copy.State(hull).Position);
                Assert.Equal(_hulls.State(hull).Velocity, copy.State(hull).Velocity);
                Assert.Equal(_hulls.State(hull).AngularVelocity, copy.State(hull).AngularVelocity);
                Assert.Equal(_hulls.State(hull).Shield, copy.State(hull).Shield);
            }
        }

        Assert.True(_hulls.State(vessel).Velocity.X < -5f, "the ram never happened");
        Assert.InRange(copy.State(pod).Shield, 1f, Content.Pod.Flight.ShieldCapacity - 1f);

        // A ram that breaks blocks off: the server decides which. The client shows the hull points falling
        // and keeps every block until it is told.
        (pod, vessel) = Start(150);
        for (var i = 0; i < 10; i++)
        {
            _field.BeginRound();
            predicted.BeginRound();
            _field.Step(pod, thrust);
            predicted.Step(pod, thrust);
            Assert.Equal(0, predicted.Lost.Length);
        }

        Assert.NotEqual(Content.Pod.FullMask(), _hulls.Mask(pod));
        Assert.Equal(Content.Pod.FullMask(), copy.Mask(pod));
        Assert.Equal(Content.Designs[Vessel].FullMask(), copy.Mask(vessel));
        Assert.Equal(0f, copy.State(pod).Shield);
        Assert.True(copy.State(pod).Hull < Content.Pod.Flight.HullPoints);
        Assert.True(copy.State(vessel).Hull < Content.Designs[Vessel].Flight.HullPoints);
    }
}

public sealed class JumpTests
{
    private static readonly GameContent Content = GameContent.Load();

    [Theory]
    [InlineData(0d)]
    [InlineData(0.01d)]
    [InlineData(40000d)]
    [InlineData(160000d)]
    public void DistanceChangesOnlyTransitAndAllDrivesUseIt(double distance)
    {
        foreach (var kind in new[] { FtlKind.Hyperspace, FtlKind.Wormhole, FtlKind.Rift })
        {
            var flight = Content.Pod.Flight;
            flight.Ftl = kind;
            var origin = new Double3(1e12, -1e12, 1e12);
            var target = origin + new Double3(0, 0, -distance);
            var state = HullState.Spawn(origin, flight);
            var order = new HullOrder { Kind = OrderKind.Jump, Target = target };
            var expected = HullSim.JumpTravelSeconds(origin, target, flight);
            var preparation = 0;
            while (state.Jump != JumpPhase.Tunnel && preparation++ < 1000)
                HullSim.Step(ref state, ref order, default, flight);
            Assert.InRange(preparation, 719, 721);
            Assert.Equal(expected, state.JumpTunnelSeconds);
            Assert.Equal(target, state.Position);
            var ticks = 0;
            while (state.Jump == JumpPhase.Tunnel && ticks++ < 1000)
                HullSim.Step(ref state, ref order, default, flight);
            Assert.Equal((int)Math.Ceiling(expected / (double)HullSim.Dt), ticks);
            Assert.Equal(JumpPhase.Arrive, state.Jump);
            Assert.Equal(expected, state.JumpTunnelSeconds);
            var arrival = 0;
            while (state.Jump == JumpPhase.Arrive && arrival++ < 1000)
                HullSim.Step(ref state, ref order, default, flight);
            Assert.InRange(arrival * HullSim.Dt, flight.FtlArriveSeconds, flight.FtlArriveSeconds + 2 * HullSim.Dt);
            Assert.Equal(0, state.JumpTunnelSeconds);
        }
    }

    [Fact]
    public void TravelUsesDoublePrecisionThreeDimensionalDistanceAndRejectsOverflow()
    {
        var flight = Content.Pod.Flight;
        var origin = new Double3(1e12, -1e12, 1e12);
        Assert.Equal(3.325f, HullSim.JumpTravelSeconds(origin, origin + new Double3(3000, 4000, -12000), flight));
        Assert.True(float.IsFinite(HullSim.JumpTravelSeconds(new Double3(-9e12, -9e12, -9e12), new Double3(9e12, 9e12, 9e12), flight)));
        Assert.True(float.IsNaN(HullSim.JumpTravelSeconds(new Double3(-double.MaxValue, 0, 0), new Double3(double.MaxValue, 0, 0), flight)));
        flight.FtlSpeed = 0;
        Assert.True(float.IsNaN(HullSim.JumpTravelSeconds(default, new Double3(0, 0, -40000), flight)));
        flight.FtlSpeed = float.Epsilon;
        var state = HullState.Spawn(default, flight);
        var order = new HullOrder { Kind = OrderKind.Jump, Target = new Double3(0, 0, -40000) };
        HullSim.Step(ref state, ref order, default, flight);
        Assert.Equal(OrderKind.None, order.Kind);
        Assert.Equal(JumpPhase.None, state.Jump);
    }

    [Fact]
    public void RetargetResetsDurationButIdenticalOrderPreservesItAndLongTransitAdvancesWithoutAllocation()
    {
        var flight = Content.Pod.Flight;
        var state = HullState.Spawn(default, flight);
        var order = new HullOrder { Kind = OrderKind.Jump, Target = new Double3(0, 0, -40000) };
        HullSim.Step(ref state, ref order, default, flight);
        Assert.Equal(4, state.JumpTunnelSeconds);
        HullSim.SetOrder(ref state, ref order, order);
        Assert.Equal(4, state.JumpTunnelSeconds);
        HullSim.SetOrder(ref state, ref order, new HullOrder { Kind = OrderKind.Jump, Target = new Double3(0, 0, -80000) });
        Assert.Equal(0, state.JumpTunnelSeconds);
        HullSim.Step(ref state, ref order, default, flight);
        Assert.Equal(5, state.JumpTunnelSeconds);
        HullSim.SetOrder(ref state, ref order, default);
        Assert.Equal(0, state.JumpTunnelSeconds);
        state.Jump = JumpPhase.Tunnel;
        state.JumpTunnelSeconds = 1000000000;
        state.JumpTunnelElapsed = 999999999;
        state.JumpTimer = (float)state.JumpTunnelElapsed;

        // The steps bring the hull out of the tunnel once, so a run in doubt starts again from the same state.
        var tunnel = state;
        var tunnelOrder = order;
        var allocation = new AllocationProbe();
        while (allocation.Run())
        {
            state = tunnel;
            order = tunnelOrder;
            for (var i = 0; i < 10; i++) HullSim.Step(ref state, ref order, default, flight);
            for (var i = 0; i < 51; i++)
            {
                allocation.Start();
                HullSim.Step(ref state, ref order, default, flight);
                allocation.Stop();
            }
        }
        Assert.Equal(0, allocation.Bytes);
        Assert.Equal(JumpPhase.Arrive, state.Jump);
    }

    [Theory]
    [InlineData(0f, 1f)]
    [InlineData(2f, 1f)]
    [InlineData(1f, 2f)]
    public void CalculationAndChargeRunConcurrentlyAndDebitOnlyRequiredEnergy(float calculation, float chargeSeconds)
    {
        var flight = Content.Pod.Flight;
        flight.FtlCalculateSeconds = calculation;
        flight.FtlPower = 7f;
        flight.FtlEnergy = chargeSeconds * flight.FtlPower;
        flight.PowerGenerated = flight.IdlePower = 0;
        var state = HullState.Spawn(Double3.Zero, flight);
        var initialEnergy = state.Energy;
        var order = new HullOrder { Kind = OrderKind.Jump, Target = new Double3(0, 0, -1e6) };
        var ticks = 0;
        while (state.Jump != JumpPhase.Tunnel && ticks++ < 300)
        {
            HullSim.Step(ref state, ref order, default, flight);
            if (state.Jump == JumpPhase.Charge)
            {
                Assert.InRange(state.JumpTimer, 0, flight.FtlEnergy);
                Assert.InRange(state.JumpCalculation, 0, calculation);
                if (ticks * HullSim.Dt < Math.Min(calculation, chargeSeconds))
                    Assert.InRange(state.JumpCalculation, ticks * HullSim.Dt - 0.0001f, ticks * HullSim.Dt + 0.0001f);
            }
        }
        Assert.Equal(JumpPhase.Tunnel, state.Jump);
        Assert.Equal(calculation, state.JumpCalculation);
        Assert.InRange(ticks * HullSim.Dt, Math.Max(calculation, chargeSeconds) - HullSim.Dt, Math.Max(calculation, chargeSeconds) + 2 * HullSim.Dt);
        Assert.InRange(initialEnergy - state.Energy, flight.FtlEnergy - 0.002f, flight.FtlEnergy + 0.002f);
    }

    [Fact]
    public void EmptyBatteryDoesNotDelayCalculationAndCompletedChargeAllowsRegeneration()
    {
        var flight = Content.Pod.Flight;
        flight.FtlCalculateSeconds = 1;
        flight.FtlEnergy = 4;
        flight.FtlPower = 100;
        flight.PowerGenerated = 1;
        flight.IdlePower = 0;
        var state = HullState.Spawn(Double3.Zero, flight);
        state.Energy = 0;
        var order = new HullOrder { Kind = OrderKind.Jump, Target = new Double3(0, 0, -1e6) };
        for (var i = 0; i < 120; i++) HullSim.Step(ref state, ref order, default, flight);
        Assert.Equal(JumpPhase.Charge, state.Jump);
        Assert.Equal(1, state.JumpCalculation);
        Assert.InRange(state.JumpTimer, 1.9f, 2.1f);
        for (var i = 0; i < 125 && state.Jump != JumpPhase.Tunnel; i++) HullSim.Step(ref state, ref order, default, flight);
        Assert.Equal(JumpPhase.Tunnel, state.Jump);

        flight.FtlCalculateSeconds = 12;
        flight.EnergyStorage = 100;
        state = HullState.Spawn(Double3.Zero, flight);
        state.Energy = 20;
        for (var i = 0; i < 300; i++) HullSim.Step(ref state, ref order, default, flight);
        Assert.Equal(JumpPhase.Charge, state.Jump);
        Assert.Equal(flight.FtlEnergy, state.JumpTimer);
        var energy = state.Energy;
        for (var i = 0; i < 60; i++) HullSim.Step(ref state, ref order, default, flight);
        Assert.InRange(state.Energy - energy, 0.999f, 1.001f);
    }

    [Fact]
    public void AlignmentReplacementCancellationAndDriveLossDiscardCalculation()
    {
        var flight = Content.Pod.Flight;
        var state = HullState.Spawn(Double3.Zero, flight);
        var order = new HullOrder { Kind = OrderKind.Jump, Target = new Double3(1e6, 0, 0) };
        HullSim.Step(ref state, ref order, default, flight);
        Assert.Equal(JumpPhase.Align, state.Jump);
        Assert.Equal(0, state.JumpCalculation);
        order.Target = new Double3(0, 0, -1e6);
        for (var i = 0; i < 120; i++) HullSim.Step(ref state, ref order, default, flight);
        Assert.True(state.JumpCalculation > 1);
        var energy = state.Energy;
        HullSim.SetOrder(ref state, ref order, new HullOrder { Kind = OrderKind.Jump, Target = new Double3(-1e6, 0, 0) });
        Assert.Equal(0, state.JumpCalculation);
        Assert.Equal(0, state.JumpTimer);
        Assert.Equal(energy, state.Energy);
        order.Target = new Double3(0, 0, -1e6);
        for (var i = 0; i < 120; i++) HullSim.Step(ref state, ref order, default, flight);
        flight.Ftl = FtlKind.None;
        HullSim.Step(ref state, ref order, default, flight);
        Assert.Equal(OrderKind.None, order.Kind);
        Assert.Equal(JumpPhase.None, state.Jump);
        Assert.Equal(0, state.JumpCalculation);
        flight = Content.Pod.Flight;
        order.Kind = OrderKind.Jump;
        HullSim.Step(ref state, ref order, default, flight);
        Assert.InRange(state.JumpCalculation, 0, HullSim.Dt);
        order = default;
        HullSim.Step(ref state, ref order, default, flight);
        Assert.Equal(0, state.JumpCalculation);
    }

    [Fact]
    public void ChargingTickAllocatesNothingAfterWarmup()
    {
        var flight = Content.Pod.Flight;
        flight.FtlCalculateSeconds = float.MaxValue;
        var state = HullState.Spawn(Double3.Zero, flight);
        var order = new HullOrder { Kind = OrderKind.Jump, Target = new Double3(0, 0, -1e6) };
        for (var i = 0; i < 2000; i++) HullSim.Step(ref state, ref order, default, flight);
        var allocation = new AllocationProbe();
        for (var i = 0; i < 2000; i++)
            while (allocation.Measuring()) HullSim.Step(ref state, ref order, default, flight);
        Assert.Equal(0, allocation.Bytes);
    }

    [Fact]
    public void EveryDriveKindIsFittedToSomeHull()
    {
        var kinds = Content.Designs.Select(d => d.Stats.Ftl).ToHashSet();
        Assert.Contains(FtlKind.Hyperspace, kinds);
        Assert.Contains(FtlKind.Wormhole, kinds);
        Assert.Contains(FtlKind.Rift, kinds);
    }

    [Fact]
    public void AJumpAlignsChargesTunnelsAndArrivesAnyDistanceAway()
    {
        var flight = Content.Pod.Flight;
        var galaxy = Content.Galaxy;
        var destination = galaxy.CenterOf(new SectorCoord(1, 0, -1));
        var state = HullState.Spawn(Double3.Zero, flight);
        state.Velocity = new Vector3(0, 0, -30);
        var order = new HullOrder { Kind = OrderKind.Jump, Target = destination };

        var phases = new List<JumpPhase>();
        var chargeTicks = 0;
        var ticks = 0;
        while ((order.Kind != OrderKind.None || state.Jump != JumpPhase.None) && ticks++ < 60 * 60)
        {
            HullSim.Step(ref state, ref order, default, flight);
            if (phases.Count == 0 || phases[^1] != state.Jump)
                phases.Add(state.Jump);

            // Until the drive fires the hull is still where it was.
            if (state.Jump is JumpPhase.Align or JumpPhase.Charge)
                Assert.True(state.Position.RelativeTo(Double3.Zero).Length() < 100f);

            // It only charges once its nose is on the destination, and it stays there.
            if (state.Jump == JumpPhase.Charge)
            {
                chargeTicks++;
                var toward = Vector3.Normalize(destination.RelativeTo(state.Position));
                Assert.True(Vector3.Dot(state.Forward, toward) > 0.99f);
            }
        }

        Assert.Equal([JumpPhase.Align, JumpPhase.Charge, JumpPhase.Tunnel, JumpPhase.Arrive, JumpPhase.None], phases);
        Assert.True(destination.RelativeTo(state.Position).Length() < 1f);
        Assert.Equal(new SectorCoord(1, 0, -1), galaxy.SectorOf(state.Position));
        Assert.True(state.Velocity.Length() < 0.01f);

        // Charging takes the jump's energy at the drive's power, here with a full battery behind it.
        Assert.InRange(chargeTicks * HullSim.Dt, Math.Max(flight.FtlCalculateSeconds, flight.FtlEnergy / flight.FtlPower) - 0.1f, Math.Max(flight.FtlCalculateSeconds, flight.FtlEnergy / flight.FtlPower) + 0.1f);
        Assert.True(flight.FtlEnergy / flight.FtlPower > 5f, "the charge should be slow enough to watch");
    }

    [Fact]
    public void ChargingDrawsFromTheBatteryAndAFlatBatteryOnlySlowsItDown()
    {
        var flight = Content.Pod.Flight;
        // Keep generation low enough that a flat battery outlasts the calculation gate.
        flight.PowerGenerated = flight.IdlePower + flight.FtlPower / 4f;
        var order = new HullOrder { Kind = OrderKind.Jump, Target = new Double3(0, 0, -1e6) };

        float SecondsToFire(float startingEnergy, out float lowestEnergy)
        {
            var state = HullState.Spawn(Double3.Zero, flight);
            state.Energy = startingEnergy;
            var jump = order;
            var ticks = 0;
            lowestEnergy = startingEnergy;
            while (state.Jump != JumpPhase.Tunnel && ticks++ < 60 * 300)
            {
                lowestEnergy = Math.Min(lowestEnergy, state.Energy);
                HullSim.Step(ref state, ref jump, default, flight);
            }

            return ticks * HullSim.Dt;
        }

        var full = SecondsToFire(flight.EnergyStorage, out var left);
        Assert.True(left < flight.EnergyStorage - 5f, "charging should have drained the battery");

        // With nothing stored the drive lives off the generator's surplus: slower, but it gets there.
        var flat = SecondsToFire(0, out _);
        var surplus = flight.PowerGenerated - flight.IdlePower;
        Assert.True(flat > full * 1.2f);
        Assert.InRange(flat, flight.FtlEnergy / surplus * 0.9f, flight.FtlEnergy / surplus * 1.15f);
    }

    [Fact]
    public void WithdrawingTheOrderBeforeTheDriveFiresCancelsTheJumpAndLosesTheCharge()
    {
        var flight = Content.Pod.Flight;
        var state = HullState.Spawn(Double3.Zero, flight);
        var order = new HullOrder { Kind = OrderKind.Jump, Target = new Double3(0, 0, -1e6) };
        for (var i = 0; i < 120; i++)
            HullSim.Step(ref state, ref order, default, flight);
        Assert.Equal(JumpPhase.Charge, state.Jump);
        Assert.True(state.JumpTimer > 1f);

        order = default;
        HullSim.Step(ref state, ref order, default, flight);
        Assert.Equal(JumpPhase.None, state.Jump);
        Assert.Equal(0f, state.JumpTimer);
        Assert.True(state.Position.RelativeTo(Double3.Zero).Length() < 1f);
    }

    [Fact]
    public void AHullThatHasLostItsDriveCannotJump()
    {
        var pod = Content.Pod;
        var drive = Array.FindIndex(pod.Types, t => t.Ftl != FtlKind.None);
        pod.Evaluate([pod.FullMask() & ~(1UL << drive)], out var flight, out var stats);
        Assert.Equal(FtlKind.None, stats.Ftl);

        var state = HullState.Spawn(Double3.Zero, flight);
        var order = new HullOrder { Kind = OrderKind.Jump, Target = new Double3(1e6, 0, 0) };
        HullSim.Step(ref state, ref order, default, flight);
        Assert.Equal(OrderKind.None, order.Kind);
        Assert.Equal(JumpPhase.None, state.Jump);
    }
}

public sealed class HullStoreTests
{
    [Fact]
    public void DisposingAStoreGivesBackTheBlockStatesItTookFromASharedStoreAndLeavesTheBorrowedOnes()
    {
        var pod = GameContent.Load().Pod;
        using var states = new BlockStateStore();
        var lent = states.Create(pod, hitPoints: false);

        var store = new HullStore(2, states);
        store.Create(1, 0, pod, new Double3(1, 0, 0));
        store.Create(2, 0, pod, new Double3(2, 0, 0), lent);
        Assert.Equal(2, states.Count);

        store.Dispose();
        Assert.Equal(1, states.Count);
        Assert.True(states.IsLive(lent));

        // Disposing it again gives nothing back twice.
        store.Dispose();
        Assert.Equal(1, states.Count);
    }

    [Fact]
    public void HandlesGoStaleWhenTheirHullIsDestroyedAndSlotsAreReused()
    {
        var pod = GameContent.Load().Pod;
        using var store = new HullStore(2);
        var a = store.Create(1, 0, pod, new Double3(1, 0, 0));
        var b = store.Create(2, 0, pod, new Double3(2, 0, 0));
        var c = store.Create(3, 0, pod, new Double3(3, 0, 0));
        Assert.Equal(3, store.Count);
        Assert.Equal(2.0, store.State(b.Index).Position.X);
        Assert.Equal(pod.FullMask(), store.Mask(b.Index));
        Assert.Equal(pod.Flight.HullPoints, store.BlockHp(b.Index).ToArray().Sum(), 1);
        Assert.Equal(pod.Flight.Mass, store.Flight(c.Index).Mass);

        store.Destroy(b);
        Assert.False(store.IsAlive(b));
        Assert.True(store.IsAlive(a) && store.IsAlive(c));

        var d = store.Create(4, 0, pod, new Double3(4, 0, 0));
        Assert.Equal(b.Index, d.Index);
        Assert.NotEqual(b.Generation, d.Generation);
        Assert.False(store.IsAlive(b));
        Assert.Equal(4u, store.Info(d.Index).Owner);
        Assert.NotEqual(b.Packed, d.Packed);
    }
}

/// <summary>Flies real sessions: prediction against the authoritative server, orders, and a second player.</summary>
public sealed class FlightSessionTests
{
    private static readonly GameContent Content = GameContent.Load();

    [Fact]
    public void PredictedHullTracksTheServerWhileFlying()
    {
        using var session = GameSession.StartLocal(new ServerConfig { MaxPlayers = 1 }, Content, "Pilot");
        var start = WaitForOwnHull(session);

        session.Client.Input.Submit(new PlayerControls { Forward = 1, Strafe = 0.5f, Aim = Vector3.Normalize(new Vector3(0.6f, 0.2f, -1f)), Align = true, Boost = true });
        Thread.Sleep(1500);
        var flying = OwnHull(session);
        Assert.True(flying.Position.RelativeTo(start.Position).Length() > 30f);
        Assert.True(flying.Heat > 0.05f);

        // Release the controls: the dampeners stop the hull and the prediction settles on the server's answer.
        session.Client.Input.Submit(default);
        Thread.Sleep(6000);
        var first = OwnHull(session);
        Thread.Sleep(300);
        var second = OwnHull(session);
        Assert.True(second.Velocity.Length() < 0.05f);
        Assert.True(second.Position.RelativeTo(first.Position).Length() < 0.05f);
        Assert.Null(session.Fault);
    }

    [Fact]
    public void PredictionAgreesWithTheServerWhileManoeuvringInALocalGame()
    {
        using var session = GameSession.StartLocal(new ServerConfig { MaxPlayers = 1 }, Content, "Pilot");
        WaitForOwnHull(session);

        // Thrust, strafe, climb, roll and keep swinging the nose around for a few seconds.
        var worst = 0f;
        var start = Stopwatch.GetTimestamp();
        while (Stopwatch.GetElapsedTime(start).TotalSeconds < 5)
        {
            var t = (float)Stopwatch.GetElapsedTime(start).TotalSeconds;
            var aim = Vector3.Normalize(new Vector3(MathF.Sin(t * 1.3f), 0.4f * MathF.Cos(t * 0.9f), -MathF.Cos(t * 1.3f)));
            session.Client.Input.Submit(new PlayerControls { Forward = 1, Strafe = MathF.Sin(t * 2f), Vertical = 0.5f, Roll = MathF.Cos(t), Aim = aim, Align = true, Boost = t > 2 });
            worst = Math.Max(worst, session.Client.AcquireSnapshot().PredictionError);
            Thread.Sleep(5);
        }

        // With no network between them the two run the same inputs through the same code: no correction at all.
        Assert.True(worst < 0.01f, $"prediction was off by up to {worst:0.000} m");
    }

    [Fact]
    public void PredictionAgreesWithTheServerWhileBoostingThroughAnEmptyBattery()
    {
        using var session = GameSession.StartLocal(new ServerConfig { MaxPlayers = 1 }, Content, "Pilot");
        WaitForOwnHull(session);

        // Boost with the nose swinging until the battery runs dry, and keep holding it: boost cuts out at empty
        // and comes back only once the battery has climbed over its floor, on both sides alike.
        var worst = 0f;
        var cutOut = false;
        var start = Stopwatch.GetTimestamp();
        while (Stopwatch.GetElapsedTime(start).TotalSeconds < 9)
        {
            var t = (float)Stopwatch.GetElapsedTime(start).TotalSeconds;
            var aim = Vector3.Normalize(new Vector3(0.5f * MathF.Sin(t * 0.8f), 0.2f * MathF.Cos(t * 0.6f), -1f));
            session.Client.Input.Submit(new PlayerControls { Forward = 1, Strafe = 0.3f * MathF.Sin(t), Aim = aim, Align = true, Boost = true });
            var snapshot = session.Client.AcquireSnapshot();
            worst = Math.Max(worst, snapshot.PredictionError);
            if (snapshot.OwnHull >= 0)
            {
                var own = snapshot.Hulls[snapshot.OwnHull];
                cutOut |= own.Energy < snapshot.Flight.BoostEnergyFloor && !own.Boosting && !own.BoostLocked;
            }

            Thread.Sleep(5);
        }

        Assert.True(cutOut, "the battery never ran down far enough to stop the boost");
        Assert.True(worst < 0.01f, $"prediction was off by up to {worst:0.000} m");
        Assert.Null(session.Fault);
    }

    [Fact]
    public void MoveOrderFliesTheHullThereAndManualInputCancelsIt()
    {
        using var session = GameSession.StartLocal(new ServerConfig { MaxPlayers = 1 }, Content, "Pilot");
        var start = WaitForOwnHull(session);
        var target = start.Position + new Vector3(120, 30, -260);

        session.Client.Input.IssueOrder(new HullOrder { Kind = OrderKind.MoveTo, Target = target });
        var arrived = WaitFor(session, hull => hull.Order.Kind == OrderKind.None && target.RelativeTo(hull.Position).Length() < 4f, 30);
        Assert.True(arrived.Velocity.Length() < 2f);

        session.Client.Input.IssueOrder(new HullOrder { Kind = OrderKind.MoveTo, Target = start.Position });
        WaitFor(session, hull => hull.Order.Kind == OrderKind.MoveTo, 5);
        session.Client.Input.Submit(new PlayerControls { Roll = 1 });
        WaitFor(session, hull => hull.Order.Kind == OrderKind.None, 5);
    }

    [Fact]
    public void PlayersSeeEachOtherMoveOverUdp()
    {
        var port = (ushort)(45000 + Environment.ProcessId % 2000);
        using var server = new ServerHost(new ServerConfig { MaxPlayers = 2 }, Content, Prospect.Engine.Net.RakNetTransport.Listen(port, 8));
        server.Start();
        using var pilot = GameSession.Join("127.0.0.1", port, Content, "Pilot");
        using var watcher = GameSession.Join("127.0.0.1", port, Content, "Watcher");
        WaitForOwnHull(pilot);
        WaitForOwnHull(watcher);

        HullView Other() => FindOther(watcher);
        var before = WaitForValue(Other);

        pilot.Client.Input.Submit(new PlayerControls { Forward = 1 });
        Thread.Sleep(2000);
        pilot.Client.Input.Submit(default);
        Thread.Sleep(2500);

        // The watcher's interpolated view of the pilot ends where the pilot's own prediction ended.
        var after = WaitForValue(Other);
        var own = OwnHull(pilot);
        Assert.True(after.Position.RelativeTo(before.Position).Length() > 20f);
        Assert.True(after.Position.RelativeTo(own.Position).Length() < 0.5f);
        Assert.Null(server.Fault);
    }

    [Fact]
    public void ThePilotAndAFleetHullJumpToAnotherSectorAndTheClientFollows()
    {
        using var session = GameSession.StartLocal(new ServerConfig { MaxPlayers = 1 }, Content, "Pilot");
        var pod = WaitForOwnHull(session);
        var galaxy = session.Client.AcquireSnapshot().Galaxy!;
        var far = new SectorCoord(1, 0, -1);

        HullView[] Fleet()
        {
            var snapshot = session.Client.AcquireSnapshot();
            return snapshot.Hulls.Take(snapshot.HullCount).Where(h => h.Owner == snapshot.PlayerId && !h.IsOwn).ToArray();
        }

        var deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 10;
        while (Fleet().Length == 0 && Stopwatch.GetTimestamp() < deadline)
            Thread.Sleep(20);
        var vessel = Fleet()[0];

        // The flown hull's jump is predicted by the client; the fleet hull's is carried out by the server.
        session.Client.Input.IssueOrder(new HullOrder { Kind = OrderKind.Jump, Target = galaxy.CenterOf(far) });
        session.Client.Input.IssueFleetOrder(vessel.Id, new HullOrder { Kind = OrderKind.Jump, Target = galaxy.CenterOf(far) + new Vector3(60, 0, 0) });

        var spooling = WaitFor(session, hull => hull.Jump == JumpPhase.Charge, 8);
        Assert.Equal(new SectorCoord(0, 0, 0), galaxy.SectorOf(spooling.Position));

        var arrived = WaitFor(session, hull => hull.Jump == JumpPhase.None && galaxy.SectorOf(hull.Position) == far, 30);
        Assert.Equal(pod.Id, arrived.Id);
        Assert.Equal(OrderKind.None, arrived.Order.Kind);

        // Prediction and server agree on where it came out.
        Thread.Sleep(600);
        var settled = OwnHull(session);
        Assert.True(settled.Position.RelativeTo(arrived.Position).Length() < 30f);
        Assert.Equal(far, galaxy.SectorOf(settled.Position));

        deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 40;
        while (galaxy.SectorOf(Fleet().First(h => h.Id == vessel.Id).Position) != far && Stopwatch.GetTimestamp() < deadline)
            Thread.Sleep(50);
        Assert.Equal(far, galaxy.SectorOf(Fleet().First(h => h.Id == vessel.Id).Position));
        Assert.Null(session.Fault);
    }

    [Fact]
    public void ThePlayerCanLeaveInADroneFlyItAndTakeAHullBack()
    {
        using var session = GameSession.StartLocal(new ServerConfig { MaxPlayers = 1 }, Content, "Pilot");
        var pod = WaitForOwnHull(session);
        Assert.Equal(Content.PodDesign, pod.Design);

        // Leaving the pod puts the player in a drone beside it; the pod stays, flown by the server.
        session.Client.Input.RequestControl(0);
        var drone = WaitFor(session, hull => hull.Design == Content.DroneDesign, 5);
        Assert.NotEqual(pod.Id, drone.Id);
        Assert.Equal(Content.Designs[Content.DroneDesign].Flight.HullPoints, drone.Hull);
        Assert.True(drone.Position.RelativeTo(pod.Position).Length() < 20f);
        Assert.True(Content.Designs[Content.DroneDesign].Stats.HullPoints < Content.Pod.Stats.HullPoints / 4);

        // The drone flies under the player's hand, predicted with its own flight figures.
        session.Client.Input.Submit(new PlayerControls { Forward = 1 });
        Thread.Sleep(1000);
        session.Client.Input.Submit(default);
        Thread.Sleep(2500);
        var flown = OwnHull(session);
        Assert.Equal(drone.Id, flown.Id);
        Assert.True(flown.Position.RelativeTo(drone.Position).Length() > 3f);
        Thread.Sleep(300);
        Assert.True(OwnHull(session).Position.RelativeTo(flown.Position).Length() < 0.05f);

        // Asking again while already in a drone does nothing.
        session.Client.Input.RequestControl(0);
        Thread.Sleep(300);
        Assert.Equal(drone.Id, OwnHull(session).Id);

        // Taking the pod back removes the drone.
        session.Client.Input.RequestControl(pod.Id);
        WaitFor(session, hull => hull.Id == pod.Id, 5);
        var deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 6;
        bool DroneGone()
        {
            var snapshot = session.Client.AcquireSnapshot();
            return !snapshot.Hulls.Take(snapshot.HullCount).Any(h => h.Design == Content.DroneDesign);
        }

        while (!DroneGone() && Stopwatch.GetTimestamp() < deadline)
            Thread.Sleep(50);
        Assert.True(DroneGone());
        Assert.Null(session.Fault);
    }

    [Fact]
    public void RammingAnAsteroidCostsBlocksAndInTheEndTheHullLeavingThePlayerInADrone()
    {
        using var link = new Prospect.Engine.Net.LoopbackLink();
        using var server = new ServerHost(new ServerConfig { MaxPlayers = 1 }, Content, link.Server);
        using var client = new ClientSession(link.Client, Content, "Pilot");
        long tick = 0;
        void Step()
        {
            ((Prospect.Engine.Time.ITickHandler)server).Tick(tick);
            ((Prospect.Engine.Time.ITickHandler)client).Tick(tick++);
        }

        for (var i = 0; i < 30; i++)
            Step();
        var snapshot = client.AcquireSnapshot();
        Assert.True(snapshot.OwnHull >= 0);
        var pod = snapshot.Hulls[snapshot.OwnHull];
        Assert.Equal(Content.Pod.FullMask(), pod.Mask());
        var hulls = (HullStore)typeof(ServerHost).GetField("_hulls", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(server)!;
        var rocks = (RockField)typeof(ServerHost).GetField("_rocks", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(server)!;
        var index = (int)(pod.Id & 65535);
        var handle = hulls.HandleAt(index);
        var sector = rocks.Sector(default);
        var rockIndex = Enumerable.Range(0, sector.AsteroidCount).Where(i => sector.Asteroids[i].Radius >= 40f)
            .OrderBy(i => sector.Asteroids[i].LocalPosition.Length() - sector.Asteroids[i].Radius).First();
        var rock = sector.Asteroids[rockIndex];
        var surface = rock.Radius * AsteroidShape.RadiusAlong(rock.Shape, Vector3.Transform(Vector3.UnitZ, Quaternion.Conjugate(rock.Rotation)));
        var lostBlocks = false;

        // Stage each approach independently: a damaged pod may lose its gyro or thrusters before its core.
        // Only pose and velocity are set; real server collisions decide every loss and the eventual respawn.
        for (var pass = 0; pass < 30 && hulls.IsAlive(handle); pass++)
        {
            rocks.Displacement(new RockRef(default, rockIndex), tick, out var offset, out _);
            ref var state = ref hulls.State(index);
            state.Position = new Double3(rock.LocalPosition.X + offset.X, rock.LocalPosition.Y + offset.Y, rock.LocalPosition.Z + offset.Z + surface + 20);
            state.Orientation = Quaternion.Identity;
            state.Velocity = -Vector3.UnitZ * 100;
            state.AngularVelocity = Vector3.Zero;
            client.Input.Submit(new PlayerControls { Forward = 1 });
            for (var i = 0; i < 120 && hulls.IsAlive(handle); i++)
            {
                Step();
                snapshot = client.AcquireSnapshot();
                var own = snapshot.Hulls[snapshot.OwnHull];
                if (own.Id == pod.Id && own.Mask() != Content.Pod.FullMask())
                {
                    Assert.True(snapshot.Flight.Mass < Content.Pod.Flight.Mass);
                    lostBlocks = true;
                }
            }
        }

        Assert.True(lostBlocks, "the client never received a nonfatal collision loss");
        Assert.False(hulls.IsAlive(handle), "the staged collisions never destroyed the pod");
        client.Input.Submit(default);
        for (var i = 0; i < 6; i++)
            Step();
        snapshot = client.AcquireSnapshot();
        var drone = snapshot.Hulls[snapshot.OwnHull];
        Assert.Equal(Content.DroneDesign, drone.Design);
        Assert.NotEqual(pod.Id, drone.Id);
        Assert.Equal(Content.Designs[Content.DroneDesign].FullMask(), drone.Mask());
        Assert.DoesNotContain(snapshot.Hulls.Take(snapshot.HullCount), h => h.Id == pod.Id);
        Assert.Null(server.Fault);
        Assert.Null(client.Fault);
    }

    [Fact]
    public void RammingAFleetHullShovesAndDamagesBothAndThePredictionHoldsThroughTheContact()
    {
        using var session = GameSession.StartLocal(new ServerConfig { MaxPlayers = 1 }, Content, "Pilot");
        var pod = WaitForOwnHull(session);

        HullView Vessel(uint id = 0)
        {
            var snapshot = session.Client.AcquireSnapshot();
            return snapshot.Hulls.Take(snapshot.HullCount).FirstOrDefault(h => !h.IsOwn && (id == 0 || h.Id == id));
        }

        var deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 10;
        while (Vessel().Id == 0 && Stopwatch.GetTimestamp() < deadline)
            Thread.Sleep(20);
        var parked = Vessel();
        Assert.NotEqual(0u, parked.Id);
        var vesselFlight = Content.Designs[parked.Design].Flight;
        Assert.Equal(vesselFlight.ShieldCapacity, parked.Shield);

        // The vessel is parked behind and to one side of the pod. Back into it on the thrusters, without turning,
        // and keep them on for a while after the impact so the bounces and the shoving that follow count too.
        var toVessel = Vector3.Normalize(parked.Position.RelativeTo(pod.Position));
        session.Client.Input.Submit(new PlayerControls { Strafe = toVessel.X, Forward = -toVessel.Z });
        var worst = 0f;
        var struckAt = 0L;
        var start = Stopwatch.GetTimestamp();
        while (Stopwatch.GetElapsedTime(start).TotalSeconds < 15 && (struckAt == 0 || Stopwatch.GetElapsedTime(struckAt).TotalSeconds < 2))
        {
            worst = Math.Max(worst, session.Client.AcquireSnapshot().PredictionError);
            if (struckAt == 0 && Vessel(parked.Id).Shield < vesselFlight.ShieldCapacity)
                struckAt = Stopwatch.GetTimestamp();
            Thread.Sleep(5);
        }

        session.Client.Input.Submit(default);
        Assert.True(struckAt != 0, "the pod never reached the vessel");

        // The server shoved and damaged the vessel, and damaged the pod: the client is shown all of it.
        var after = Vessel(parked.Id);
        var flown = OwnHull(session);
        Assert.Equal(pod.Id, flown.Id);
        Assert.True(after.Position.RelativeTo(parked.Position).Length() > 1f, "the vessel was not moved");
        Assert.True(after.Shield < vesselFlight.ShieldCapacity, "the vessel was not damaged");
        Assert.True(flown.Shield < Content.Pod.Flight.ShieldCapacity, "the pod was not damaged");

        // The client ran into the vessel in its own prediction and moved it there as the server did: the pod
        // was never shown inside the vessel and pulled back out.
        Assert.True(worst < 0.15f, $"prediction was off by up to {worst:0.000} m");
        Assert.Null(session.Fault);
    }

    [Fact]
    public void AServerAndAClientAgreeOnADenseSectorAndARockPushedThereReplicates()
    {
        // The server puts new players in the densest sector near home: thousands of asteroids, sizes from pebbles up.
        var seed = new ServerConfig().GalaxySeed;
        var dense = Sectors.Densest(Content.Galaxy.WithSeed(seed));
        var port = (ushort)(45000 + Environment.ProcessId % 2000);
        using var server = new ServerHost(new ServerConfig { MaxPlayers = 2, GalaxySeed = seed, SpawnSector = dense }, Content, Prospect.Engine.Net.RakNetTransport.Listen(port, 8));
        server.Start();
        using var pilot = GameSession.Join("127.0.0.1", port, Content, "Pilot");
        var pod = WaitForOwnHull(pilot);
        var galaxy = pilot.Client.AcquireSnapshot().Galaxy!;
        Assert.Equal(dense, galaxy.SectorOf(pod.Position));

        // Each end builds the sector for itself from the seed it was given. They must come out the same to the
        // last bit, or the two would mean different asteroids by the same name.
        var serverSide = new SectorContent();
        var clientSide = new SectorContent();
        server.Galaxy.Generate(dense, serverSide);
        galaxy.Generate(dense, clientSide);
        Assert.NotSame(server.Galaxy, galaxy);
        Assert.True(serverSide.AsteroidCount > 2500, $"the sector has only {serverSide.AsteroidCount} asteroids");
        Assert.Equal(serverSide.AsteroidCount, clientSide.AsteroidCount);
        Assert.True(Sectors.Bytes(serverSide).SequenceEqual(Sectors.Bytes(clientSide)), "the server and the client generated different asteroids");
        Assert.Equal(server.Galaxy.Summarize(dense).Character.Resources, galaxy.Summarize(dense).Character.Resources);

        // Fly the pod at the nearest asteroid of some size under boost, steering all the way. It cannot get there
        // without hitting something.
        var center = galaxy.CenterOf(dense);
        var start = pod.Position.RelativeTo(center);
        var nearest = clientSide.Asteroids.Take(clientSide.AsteroidCount).Where(a => a.Radius >= 40f).OrderBy(a => (a.LocalPosition - start).Length() - a.Radius).First();
        var deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 60;
        while (pilot.Client.AcquireSnapshot().RockCount == 0 && Stopwatch.GetTimestamp() < deadline)
        {
            var aim = Vector3.Normalize(nearest.LocalPosition - OwnHull(pilot).Position.RelativeTo(center));
            pilot.Client.Input.Submit(new PlayerControls { Forward = 1, Boost = true, Aim = aim, Align = true });
            Thread.Sleep(50);
        }

        pilot.Client.Input.Submit(default);
        var seen = pilot.Client.AcquireSnapshot();
        Assert.True(seen.RockCount > 0, "the client was never told an asteroid moved");

        // The asteroid the server names is one the client has, and it lies on the way the pod flew: the name
        // means the same rock at both ends.
        var hit = seen.Rocks[0];
        Assert.Equal(dense, hit.Rock.Sector);
        Assert.InRange(hit.Rock.Index, 0, clientSide.AsteroidCount - 1);
        var rock = clientSide.Asteroids[hit.Rock.Index];
        var flown = nearest.LocalPosition - start;
        var along = Math.Clamp(Vector3.Dot(rock.LocalPosition - start, flown) / flown.LengthSquared(), 0f, 1f);
        var offCourse = (rock.LocalPosition - (start + flown * along)).Length();
        Assert.True(offCourse < rock.Radius + 120f, $"the {rock.Radius:0} m asteroid the server says was hit is {offCourse:0} m off the pod's course");

        // Someone joining afterwards learns about the displaced asteroid too.
        using var late = GameSession.Join("127.0.0.1", port, Content, "Late");
        WaitForOwnHull(late);
        deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 10;
        while (late.Client.AcquireSnapshot().RockCount == 0 && Stopwatch.GetTimestamp() < deadline)
            Thread.Sleep(50);
        var lateSeen = late.Client.AcquireSnapshot();
        Assert.True(lateSeen.RockCount > 0);
        Assert.Contains(lateSeen.Rocks.Take(lateSeen.RockCount), r => r.Rock == hit.Rock);
        Assert.Null(server.Fault);
        Assert.Null(pilot.Fault);
    }

    [Fact]
    public void FleetHullsFollowOrdersFromTheirOwnerAndIgnoreEveryoneElse()
    {
        var port = (ushort)(47000 + Environment.ProcessId % 2000);
        using var server = new ServerHost(new ServerConfig { MaxPlayers = 2 }, Content, Prospect.Engine.Net.RakNetTransport.Listen(port, 8));
        server.Start();
        using var owner = GameSession.Join("127.0.0.1", port, Content, "Owner");
        using var rival = GameSession.Join("127.0.0.1", port, Content, "Rival");
        WaitForOwnHull(owner);
        WaitForOwnHull(rival);

        // Each player starts with the pod and the starting fleet.
        HullView[] Fleet(GameSession session)
        {
            var snapshot = session.Client.AcquireSnapshot();
            return snapshot.Hulls.Take(snapshot.HullCount).Where(h => h.Owner == snapshot.PlayerId && !h.IsOwn).ToArray();
        }

        var deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 10;
        while (Fleet(owner).Length < Content.StartingFleet.Length && Stopwatch.GetTimestamp() < deadline)
            Thread.Sleep(20);
        var vessel = Fleet(owner)[0];
        Assert.Equal(Content.StartingFleet.Length, Fleet(owner).Length);
        Assert.NotEqual(Content.PodDesign, vessel.Design);

        // The rival's order for a hull it does not own changes nothing, and it cannot take the hull over.
        var target = vessel.Position + new Vector3(0, 60, -300);
        var rivalHull = OwnHull(rival).Id;
        rival.Client.Input.IssueFleetOrder(vessel.Id, new HullOrder { Kind = OrderKind.MoveTo, Target = target });
        rival.Client.Input.RequestControl(vessel.Id);
        Thread.Sleep(700);
        Assert.Equal(rivalHull, OwnHull(rival).Id);
        var unmoved = Fleet(owner).First(h => h.Id == vessel.Id);
        Assert.Equal(OrderKind.None, unmoved.Order.Kind);
        Assert.True(unmoved.Position.RelativeTo(vessel.Position).Length() < 0.5f);

        // The owner's order is carried out by the server, with nobody flying the hull.
        owner.Client.Input.IssueFleetOrder(vessel.Id, new HullOrder { Kind = OrderKind.MoveTo, Target = target });
        deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 40;
        HullView moved;
        do
        {
            Thread.Sleep(50);
            moved = Fleet(owner).First(h => h.Id == vessel.Id);
        } while ((moved.Order.Kind != OrderKind.None || target.RelativeTo(moved.Position).Length() > 5f) && Stopwatch.GetTimestamp() < deadline);

        Assert.True(target.RelativeTo(moved.Position).Length() < 5f);
        Assert.Null(server.Fault);
    }

    private static HullView FindOther(GameSession session)
    {
        var snapshot = session.Client.AcquireSnapshot();
        for (var i = 0; i < snapshot.HullCount; i++)
        {
            if (snapshot.Hulls[i].Owner != snapshot.PlayerId && snapshot.Hulls[i].Design == Content.PodDesign)
                return snapshot.Hulls[i];
        }

        return default;
    }

    private static HullView WaitForValue(Func<HullView> read)
    {
        var deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 10;
        while (Stopwatch.GetTimestamp() < deadline)
        {
            var value = read();
            if (value.Id != 0)
                return value;
            Thread.Sleep(10);
        }

        throw new TimeoutException("The other player's hull never appeared");
    }

    private static HullView OwnHull(GameSession session)
    {
        var snapshot = session.Client.AcquireSnapshot();
        Assert.True(snapshot.OwnHull >= 0);
        return snapshot.Hulls[snapshot.OwnHull];
    }

    private static HullView WaitForOwnHull(GameSession session) => WaitFor(session, _ => true, 10);

    private static HullView WaitFor(GameSession session, Func<HullView, bool> condition, int seconds)
    {
        var deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * seconds;
        while (Stopwatch.GetTimestamp() < deadline)
        {
            var snapshot = session.Client.AcquireSnapshot();
            if (snapshot.OwnHull >= 0 && condition(snapshot.Hulls[snapshot.OwnHull]))
                return snapshot.Hulls[snapshot.OwnHull];
            Assert.Null(session.Fault);
            Thread.Sleep(5);
        }

        throw new TimeoutException("The hull did not reach the expected state");
    }
}

public sealed class SocialWorldTests
{
    [Fact]
    public void SocialLimitsFitTheSimulationAndWireStorage()
    {
        var rules = GameContent.Load().Social;
        Assert.InRange(rules.PartySize, 2, 16);
        Assert.Equal(0, Prospect.Engine.Time.TickLoop.SimulationHz % rules.StatusHz);
        Assert.InRange(rules.ChatChars, 1, 256);
        Assert.InRange(rules.ChatBytes, 1, 768);
    }
}

/// <summary>
/// The shield's state machine: a shield that pays out all it had breaks, takes no hits while it charges back to
/// its restart threshold on extra power, and then tops up as before. The same code runs on the server and in
/// prediction, so these run it directly.
/// </summary>
public sealed class ShieldStateTests
{
    private static readonly GameContent Content = GameContent.Load();
    private static readonly HullDesign PodDesign = Content.Pod;
    private static readonly FlightParams Pod = PodDesign.Flight;
    private static readonly int Emitter = Array.FindIndex(PodDesign.Types, t => t.Shield > 0);

    /// <summary>Where the bodies these tests damage keep their blocks and hit points.</summary>
    private static readonly BlockStateStore States = new();

    private static bool Hit(ref HullState state, in FlightParams flight, ref HullBody body, int block, float amount) =>
        HullSim.Damage(ref state, flight, ref body, [1UL << block], 1, amount);

    [Fact]
    public void AWorldRoundAgesHeldHullsAndCoalescesHitsAcrossBatchedStepsWithoutAllocating()
    {
        var store = new HullStore(4);
        var hull = store.Create(1, (byte)Content.PodDesign, PodDesign, Double3.Zero);
        var field = new HullField(store, Content.Designs);
        var body = new HullBody { Design = PodDesign, Alive = PodDesign.FullSet.ToArray(), Scratch = PodDesign.Scratch() };
        ref var state = ref store.State(hull.Index);
        field.BeginRound();
        Hit(ref state, Pod, ref body, 1, 10);
        var serial = state.ShieldHit.Serial;
        field.Step(hull.Index, default);
        Hit(ref state, Pod, ref body, Emitter, 20);
        field.Step(hull.Index, default);
        Assert.Equal(serial, state.ShieldHit.Serial);
        Assert.Equal(0, state.ShieldHit.AgeTicks);
        field.BeginRound();
        field.Hold(hull.Index);
        Assert.Equal(1, state.ShieldHit.AgeTicks);
        Hit(ref state, Pod, ref body, 1, 5);
        Assert.Equal((byte)(serial + 1), state.ShieldHit.Serial);
        for (var i = 0; i < 100; i++) field.BeginRound();
        var allocation = new AllocationProbe();
        for (var i = 0; i < 1000; i++)
            while (allocation.Measuring()) field.BeginRound();
        Assert.Equal(0, allocation.Bytes);
        Assert.Equal(ShieldHit.MaxAgeTicks, state.ShieldHit.AgeTicks);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RepeatedDamagePostponesRecoveryUntilEightSecondsAfterTheLastHit(bool offline)
    {
        var body = States.Body(PodDesign);
        var state = HullState.Spawn(Double3.Zero, Pod);
        state.ShieldOffline = offline;
        state.Shield = offline ? 0f : 1000f;
        for (var hit = 0; hit < 3; hit++)
        {
            Assert.False(Hit(ref state, Pod, ref body, 1, 10f));
            Assert.Equal(8f, state.ShieldCooldown);
            var charge = state.Shield;
            for (var tick = 0; tick < 7 * 60; tick++)
                FlightModel.Step(ref state, default, Pod, HullSim.Dt);
            Assert.Equal(charge, state.Shield);
        }

        // Seven seconds have passed since the final hit; even visible harmless taps do not restart the wait.
        var before = state.Shield;
        for (var tick = 0; tick < 59; tick++)
        {
            HullSim.ShieldContact(ref state, Pod, PodDesign, body.Alive, Pod.ShieldContactSpeed * 2f);
            FlightModel.Step(ref state, default, Pod, HullSim.Dt);
        }
        Assert.Equal(before, state.Shield);
        Assert.InRange(state.ShieldCooldown, 0f, 2 * HullSim.Dt);

        // Allow one tick of floating-point countdown rounding, then measure a full second of recharge.
        for (var tick = 0; tick < 2; tick++)
            FlightModel.Step(ref state, default, Pod, HullSim.Dt);
        Assert.True(state.Shield > before);
        before = state.Shield;
        for (var tick = 0; tick < 60; tick++)
            FlightModel.Step(ref state, default, Pod, HullSim.Dt);
        Assert.Equal(37.5f, state.Shield - before, 3);
        Assert.Equal(offline, state.ShieldOffline);
    }

    [Fact]
    public void TheRestartRulesComeFromTheContent()
    {
        Assert.Equal(0.25f, Content.Rules.ShieldRestartFraction);
        Assert.Equal(4.5f, Content.Rules.ShieldRestartPowerMultiplier);
        Assert.Equal(Pod.ShieldPower * 4.5f, Pod.ShieldRestartPower);
        Assert.Equal(0.25f, Pod.ShieldRestartFraction);
    }

    [Fact]
    public void ABreakingHitTakesTheShieldOfflineAndOnlyTheExcessReachesTheBlocks()
    {
        var body = States.Body(PodDesign);
        var state = HullState.Spawn(Double3.Zero, Pod);
        state.Shield = 100f;

        Assert.False(Hit(ref state, Pod, ref body, 1, 130f));
        Assert.Equal(0f, state.Shield);
        Assert.True(state.ShieldOffline);
        Assert.Equal(Pod.HullPoints - 30f, state.Hull, 3);
        Assert.Equal(Pod.ShieldDelay, state.ShieldCooldown);
        Assert.Equal(1, state.ShieldHit.Serial);
        Assert.Equal(0, state.ShieldHit.AgeTicks);
        Assert.True(state.ShieldHit.Strength > 0);
    }

    [Fact]
    public void AnOfflineShieldTakesNoHitsWhateverChargeItIsBuildingBack()
    {
        var body = States.Body(PodDesign);
        var state = HullState.Spawn(Double3.Zero, Pod);
        state.Shield = Pod.ShieldCapacity * 0.2f;
        state.ShieldOffline = true;
        state.ShieldCooldown = 0f;
        var hit = state.ShieldHit;

        Hit(ref state, Pod, ref body, 1, 50f);

        Assert.Equal(Pod.ShieldCapacity * 0.2f, state.Shield);
        Assert.True(state.ShieldOffline);
        Assert.Equal(Pod.HullPoints - 50f, state.Hull, 3);
        Assert.Equal(Pod.ShieldDelay, state.ShieldCooldown);
        Assert.Equal(hit, state.ShieldHit);
    }

    [Fact]
    public void ABrokenShieldWaitsOutTheDelayThenChargesOnRestartPowerToItsThreshold()
    {
        // A battery big enough to carry the whole restart, so the draw and the recovery time are the rule's own.
        var flight = Pod;
        flight.EnergyStorage = 300f;
        var state = HullState.Spawn(Double3.Zero, flight);
        state.Shield = 0f;
        state.ShieldOffline = true;
        state.ShieldCooldown = flight.ShieldDelay;
        state.Energy = flight.EnergyStorage * 0.5f;

        // Through the delay nothing charges; the battery gains what the generator makes over the idle draw.
        var delayTicks = (int)MathF.Round(flight.ShieldDelay / HullSim.Dt);
        for (var i = 0; i < delayTicks - 1; i++)
            FlightModel.Step(ref state, default, flight, HullSim.Dt);
        Assert.Equal(0f, state.Shield);
        Assert.True(state.ShieldOffline);

        // Charging back costs the restart draw, a multiple of the online top-up set by the content.
        state.Energy = flight.EnergyStorage * 0.5f;
        var before = state.Energy;
        FlightModel.Step(ref state, default, flight, HullSim.Dt);
        Assert.Equal((flight.PowerGenerated - flight.IdlePower - flight.ShieldRestartPower) * HullSim.Dt, state.Energy - before, 4);
        Assert.True(state.Shield > 0f && state.ShieldOffline, "one tick of charge must not bring the shield back");

        // It stays offline right up to the threshold and comes online on reaching it.
        var threshold = flight.ShieldCapacity * flight.ShieldRestartFraction;
        var ticks = 1;
        while (state.ShieldOffline && ticks < 60 * 60)
        {
            Assert.True(state.Shield < threshold);
            FlightModel.Step(ref state, default, flight, HullSim.Dt);
            ticks++;
        }

        Assert.False(state.ShieldOffline);
        Assert.InRange(state.Shield, threshold, threshold + flight.ShieldRecharge * HullSim.Dt);
        Assert.InRange(ticks * HullSim.Dt, threshold / flight.ShieldRecharge - HullSim.Dt, threshold / flight.ShieldRecharge + 2 * HullSim.Dt);

        // Online, the top-up draws the ordinary shield power again.
        state.Energy = flight.EnergyStorage * 0.5f;
        before = state.Energy;
        FlightModel.Step(ref state, default, flight, HullSim.Dt);
        Assert.Equal((flight.PowerGenerated - flight.IdlePower - flight.ShieldPower) * HullSim.Dt, state.Energy - before, 4);
    }

    [Fact]
    public void WithoutPowerABrokenShieldGainsNothingAndTheBatteryNeverGoesNegative()
    {
        var starved = Pod;
        starved.PowerGenerated = starved.IdlePower;
        var state = HullState.Spawn(Double3.Zero, starved);
        state.Shield = 0f;
        state.ShieldOffline = true;
        state.Energy = 0f;

        // Idle systems consume all generation, leaving no energy for recovery.
        for (var i = 0; i < 60 * 30; i++)
        {
            FlightModel.Step(ref state, default, starved, HullSim.Dt);
            Assert.True(state.Energy >= 0f);
        }

        Assert.Equal(0f, state.Shield);
        Assert.True(state.ShieldOffline);

        // A ship with no generator and an empty battery stays down as well.
        var dead = starved;
        dead.PowerGenerated = 0f;
        FlightModel.Step(ref state, default, dead, HullSim.Dt);
        Assert.Equal(0f, state.Shield);
    }

    [Fact]
    public void AShieldJustBackOnlineIsNotKnockedOffAgainByOnePoint()
    {
        var body = States.Body(PodDesign);
        var state = HullState.Spawn(Double3.Zero, Pod);
        state.Shield = 1f;
        state.ShieldOffline = true;
        state.ShieldCooldown = 0f;

        // One point of charge is not the threshold: still offline, and the hit goes to the hull.
        FlightModel.Step(ref state, default, Pod, HullSim.Dt);
        Assert.True(state.ShieldOffline);
        Hit(ref state, Pod, ref body, 1, 1f);
        Assert.True(state.ShieldOffline);
        Assert.Equal(Pod.HullPoints - 1f, state.Hull, 3);

        // At the threshold it is online, and a small hit is taken by the shield without breaking it.
        state.Shield = Pod.ShieldCapacity * Pod.ShieldRestartFraction;
        state.ShieldCooldown = 0f;
        FlightModel.Step(ref state, default, Pod, HullSim.Dt);
        Assert.False(state.ShieldOffline);
        var hull = state.Hull;
        Hit(ref state, Pod, ref body, 1, 1f);
        Assert.False(state.ShieldOffline);
        Assert.Equal(hull, state.Hull);
    }

    [Fact]
    public void LosingEmittersCapacityShrinksOrVanishesWithoutAnImaginaryHit()
    {
        // Capacity shrinks under a broken shield's charge: it comes back once the delay is over, even though
        // a full shield never recharges.
        var smaller = Pod;
        smaller.ShieldCapacity = 100f;
        var state = HullState.Spawn(Double3.Zero, Pod);
        state.Shield = 100f;
        state.ShieldOffline = true;
        state.ShieldCooldown = 0f;
        var hit = state.ShieldHit;
        FlightModel.Step(ref state, default, smaller, HullSim.Dt);
        Assert.False(state.ShieldOffline);
        Assert.Equal(hit.Serial, state.ShieldHit.Serial);
        Assert.Equal(hit.Strength, state.ShieldHit.Strength);

        // No emitter left: there is no shield to be offline, and nothing is recorded as a hit.
        var none = Pod;
        none.ShieldCapacity = 0f;
        none.ShieldRecharge = 0f;
        state.ShieldOffline = true;
        state.Shield = 0f;
        FlightModel.Step(ref state, default, none, HullSim.Dt);
        Assert.False(state.ShieldOffline);
        Assert.Equal(0f, state.Shield);
        Assert.Equal(hit.Serial, state.ShieldHit.Serial);

        // A hull without a shield takes every hit on its blocks and records none.
        var body = States.Body(PodDesign);
        Hit(ref state, none, ref body, 1, 10f);
        Assert.Equal(hit.Serial, state.ShieldHit.Serial);
        Assert.False(state.ShieldOffline);
    }

    [Fact]
    public void AHitThatDestroysTheLastEmitterCollapsesTheShieldOnce()
    {
        var body = States.Body(PodDesign);
        var state = HullState.Spawn(Double3.Zero, Pod);
        state.Shield = 10f;

        // The shield breaks on the hit and the emitter is shot away by what it could not hold.
        Hit(ref state, Pod, ref body, Emitter, 10f + PodDesign.Types[Emitter].Hull + 1f);
        Assert.Equal(0UL, BlockMasks.Word(body.Alive) & 1UL << Emitter);
        Assert.True(state.ShieldOffline);
        Assert.Equal(1, state.ShieldHit.Serial);

        PodDesign.Evaluate(body.Alive, out var left, out _);
        Assert.Equal(0f, left.ShieldCapacity);
        FlightModel.Step(ref state, default, left, HullSim.Dt);
        Assert.False(state.ShieldOffline);
        Assert.Equal(1, state.ShieldHit.Serial);
    }

    [Fact]
    public void HitsInOneTickAreOneEventAndTheHarderOneSaysWhere()
    {
        var body = States.Body(PodDesign);
        var state = HullState.Spawn(Double3.Zero, Pod);

        Hit(ref state, Pod, ref body, 1, 10f);
        var first = state.ShieldHit;
        Hit(ref state, Pod, ref body, Emitter, 200f);
        var both = state.ShieldHit;
        Assert.Equal(first.Serial, both.Serial);
        Assert.True(both.Strength > first.Strength);
        var expected = PodDesign.BlockOffsets[Emitter] * (127f / Pod.Radius);
        Assert.Equal(expected.X, both.X, 1f);
        Assert.Equal(expected.Z, both.Z, 1f);

        // A weaker hit in the same tick cannot replace the strongest impact.
        Hit(ref state, Pod, ref body, 1, 1f);
        Assert.Equal(both.X, state.ShieldHit.X);
        Assert.Equal(both.Serial, state.ShieldHit.Serial);
        Assert.Equal(both.Strength, state.ShieldHit.Strength);

        // The next tick's hit is a new event, and the age counts up to its cap and stops.
        HullOrder order = default;
        HullSim.AdvanceShieldClock(ref state);
        HullSim.Step(ref state, ref order, default, Pod, ref body);
        Assert.Equal(1, state.ShieldHit.AgeTicks);
        Hit(ref state, Pod, ref body, 1, 10f);
        Assert.Equal((byte)(both.Serial + 1), state.ShieldHit.Serial);
        for (var i = 0; i < 400; i++)
        {
            HullSim.AdvanceShieldClock(ref state);
            HullSim.Step(ref state, ref order, default, Pod, ref body);
        }
        Assert.Equal(ShieldHit.MaxAgeTicks, state.ShieldHit.AgeTicks);
    }

    [Fact]
    public void TheStrongestThirdContactSuppliesTheRipplePoint()
    {
        var body = States.Body(PodDesign);
        var state = HullState.Spawn(Double3.Zero, Pod);
        Hit(ref state, Pod, ref body, 0, 100f);
        Hit(ref state, Pod, ref body, 1, 100f);
        Hit(ref state, Pod, ref body, Emitter, 150f);
        var expected = PodDesign.BlockOffsets[Emitter] * (127f / Pod.Radius);
        Assert.Equal(expected.X, state.ShieldHit.X, 1f);
        Assert.Equal(expected.Y, state.ShieldHit.Y, 1f);
        Assert.Equal(expected.Z, state.ShieldHit.Z, 1f);
        Assert.Equal(102, state.ShieldHit.Strength);
        Assert.Equal(1, state.ShieldHit.Serial);
    }

    [Fact]
    public void AnImpactAgesOncePerTickThroughoutTransitWithoutAdvancingRecharge()
    {
        var body = States.Body(PodDesign);
        var state = HullState.Spawn(Double3.Zero, Pod);
        Hit(ref state, Pod, ref body, 1, 100f);
        var cooldown = state.ShieldCooldown;
        var shield = state.Shield;
        state.Jump = JumpPhase.Tunnel;
        state.JumpTunnelSeconds = 600f;
        var order = new HullOrder { Kind = OrderKind.Jump, Target = new Double3(40000, 0, 0) };
        for (var tick = 1; tick <= ShieldHit.MaxAgeTicks + 10; tick++)
        {
            HullSim.AdvanceShieldClock(ref state);
            HullSim.Step(ref state, ref order, default, Pod, ref body);
            Assert.Equal(Math.Min(tick, ShieldHit.MaxAgeTicks), state.ShieldHit.AgeTicks);
        }
        Assert.Equal(JumpPhase.Tunnel, state.Jump);
        Assert.Equal(cooldown, state.ShieldCooldown);
        Assert.Equal(shield, state.Shield);
    }

    [Fact]
    public void LightShieldContactDoesNotDamageOrDelayRechargeAndRespectsOfflineState()
    {
        var body = States.Body(PodDesign);
        var state = HullState.Spawn(Double3.Zero, Pod);
        HullSim.ShieldContact(ref state, Pod, PodDesign, body.Alive, Pod.ShieldContactSpeed * 0.5f);
        Assert.Equal(0, state.ShieldHit.Strength);
        HullSim.ShieldContact(ref state, Pod, PodDesign, body.Alive, Pod.ShieldContactSpeed * 2f);
        Assert.Equal(1, state.ShieldHit.Strength);
        Assert.Equal(Pod.ShieldCapacity, state.Shield);
        Assert.Equal(Pod.HullPoints, state.Hull);
        Assert.Equal(0f, state.ShieldCooldown);
        var hit = state.ShieldHit;
        state.ShieldOffline = true;
        state.ShieldHit.AgeTicks = 1;
        HullSim.ShieldContact(ref state, Pod, PodDesign, body.Alive, Pod.CollisionSafeSpeed);
        Assert.Equal(hit.Serial, state.ShieldHit.Serial);
        Assert.Equal(1, state.ShieldHit.AgeTicks);
    }

    [Fact]
    public void CollisionsThatPassTheShieldNeverBreakIt()
    {
        var through = Pod;
        through.CollisionShieldCost = 0f;
        var body = States.Body(PodDesign);
        var state = HullState.Spawn(Double3.Zero, through);
        Hit(ref state, through, ref body, 1, 50f);
        Assert.Equal(Pod.ShieldCapacity, state.Shield);
        Assert.False(state.ShieldOffline);
        Assert.Equal(0, state.ShieldHit.Strength);
    }

    [Fact]
    public void APredictingClientBreaksTheShieldExactlyAsTheServerDoes()
    {
        var server = States.Body(PodDesign);
        var client = new HullBody { Design = PodDesign, Alive = PodDesign.FullSet.ToArray(), Scratch = PodDesign.Scratch() };
        var a = HullState.Spawn(Double3.Zero, Pod);
        a.Shield = 40f;
        var b = a;
        Hit(ref a, Pod, ref server, 1, 70f);
        Hit(ref b, Pod, ref client, 1, 70f);
        Assert.Equal(a.Shield, b.Shield);
        Assert.Equal(a.ShieldOffline, b.ShieldOffline);
        Assert.Equal(a.ShieldHit, b.ShieldHit);
        for (var i = 0; i < 600; i++)
        {
            FlightModel.Step(ref a, default, Pod, HullSim.Dt);
            FlightModel.Step(ref b, default, Pod, HullSim.Dt);
        }

        Assert.Equal(a.Shield, b.Shield);
        Assert.Equal(a.ShieldOffline, b.ShieldOffline);
    }

    [Theory]
    [InlineData(5f)]
    [InlineData(1000f)]
    public void ALongStepStaysWithinTheShieldsLimits(float dt)
    {
        // Enough power that two long steps do bring the shield back; the point is that nothing leaves its limits.
        var flight = Pod;
        flight.PowerGenerated = flight.IdlePower + flight.ShieldRestartPower;
        var state = HullState.Spawn(Double3.Zero, flight);
        state.Shield = 0f;
        state.ShieldOffline = true;
        state.Energy = flight.EnergyStorage;
        FlightModel.Step(ref state, default, flight, dt);
        FlightModel.Step(ref state, default, flight, dt);
        Assert.InRange(state.Shield, 0f, flight.ShieldCapacity);
        Assert.InRange(state.Energy, 0f, flight.EnergyStorage);
        Assert.False(state.ShieldOffline);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(-0.1f)]
    [InlineData(1.5f)]
    public void ContentRejectsARestartThresholdOutsideItsRange(float fraction) =>
        Assert.Throws<InvalidDataException>(() => LoadWith("\"shieldRestartFraction\": 0.25", $"\"shieldRestartFraction\": {Json(fraction)}"));

    [Theory]
    [InlineData(0.5f)]
    [InlineData(11f)]
    public void ContentRejectsARestartPowerOutsideItsRange(float multiplier) =>
        Assert.Throws<InvalidDataException>(() => LoadWith("\"shieldRestartPowerMultiplier\": 4.5", $"\"shieldRestartPowerMultiplier\": {Json(multiplier)}"));

    private static string Json(float value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static void LoadWith(string from, string to)
    {
        var source = File.ReadAllText(Prospect.Engine.IO.AppPaths.Content("blocks", "blocks.json"));
        Assert.Contains(from, source);
        var path = Path.Combine(Path.GetTempPath(), $"prospect-blocks-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, source.Replace(from, to));
        try
        {
            BlockCatalog.Load(path);
        }
        finally
        {
            File.Delete(path);
        }
    }
}

/// <summary>
/// The age of the shield's last change: a break, a restart or the loss of its last emitter starts it, and it counts
/// once per tick, transit included, up to the same cap as a hit's age. Viewers time collapses and fade-ins by it.
/// </summary>
public sealed class ShieldChangeTests
{
    private static readonly GameContent Content = GameContent.Load();
    private static readonly HullDesign PodDesign = Content.Pod;
    private static readonly FlightParams Pod = PodDesign.Flight;
    private static readonly int Emitter = Array.FindIndex(PodDesign.Types, t => t.Shield > 0);

    /// <summary>Where the bodies these tests damage keep their blocks and hit points.</summary>
    private static readonly BlockStateStore States = new();

    private static HullBody Body() => States.Body(PodDesign);

    [Fact]
    public void ANewHullsLastChangeIsLongPast() =>
        Assert.Equal(ShieldHit.MaxAgeTicks, HullState.Spawn(Double3.Zero, Pod).ShieldChangeTicks);

    [Fact]
    public void ABreakAndARestartEachStartTheClockWhichCountsThroughTransit()
    {
        var body = Body();
        var state = HullState.Spawn(Double3.Zero, Pod);
        state.Shield = 10f;
        HullSim.Damage(ref state, Pod, ref body, [1UL << 1], 1, 50f);
        Assert.True(state.ShieldOffline);
        Assert.Equal(0, state.ShieldChangeTicks);

        // A hit on a shield that is already down is not a change.
        HullOrder order = default;
        HullSim.AdvanceShieldClock(ref state);
        HullSim.Step(ref state, ref order, default, Pod, ref body);
        HullSim.Damage(ref state, Pod, ref body, [1UL << 1], 1, 5f);
        Assert.Equal(1, state.ShieldChangeTicks);

        // Through a long transit the clock keeps counting, and stops at its cap.
        state.Jump = JumpPhase.Tunnel;
        state.JumpTunnelSeconds = 600f;
        order = new HullOrder { Kind = OrderKind.Jump, Target = new Double3(40000, 0, 0) };
        for (var tick = 2; tick <= ShieldHit.MaxAgeTicks + 10; tick++)
        {
            HullSim.AdvanceShieldClock(ref state);
            HullSim.Step(ref state, ref order, default, Pod, ref body);
            Assert.Equal(Math.Min(tick, ShieldHit.MaxAgeTicks), state.ShieldChangeTicks);
        }

        // Back in flight with the threshold reached, the restart starts it again.
        state.Jump = JumpPhase.None;
        order = default;
        state.ShieldCooldown = 0f;
        state.Shield = Pod.ShieldCapacity * Pod.ShieldRestartFraction;
        HullSim.AdvanceShieldClock(ref state);
        HullSim.Step(ref state, ref order, default, Pod, ref body);
        Assert.False(state.ShieldOffline);
        Assert.Equal(0, state.ShieldChangeTicks);
        HullSim.Step(ref state, ref order, default, Pod, ref body);
        Assert.Equal(0, state.ShieldChangeTicks);
        HullSim.AdvanceShieldClock(ref state);
        HullSim.Step(ref state, ref order, default, Pod, ref body);
        Assert.Equal(1, state.ShieldChangeTicks);
    }

    [Fact]
    public void LosingTheLastEmitterWhileOnlineIsAChangeWithoutAHit()
    {
        var store = new HullStore(4);
        var hull = store.Create(1, (byte)Content.PodDesign, PodDesign, Double3.Zero);
        var field = new HullField(store, Content.Designs);
        ref var state = ref store.State(hull.Index);
        var hit = state.ShieldHit;

        // Losing any other block changes nothing about the shield.
        store.Lose(hull.Index, 1UL << 1);
        field.Refit(hull.Index);
        Assert.Equal(ShieldHit.MaxAgeTicks, state.ShieldChangeTicks);

        store.Lose(hull.Index, 1UL << Emitter);
        field.Refit(hull.Index);
        Assert.Equal(0, state.ShieldChangeTicks);
        Assert.Equal(0f, store.Flight(hull.Index).ShieldCapacity);
        Assert.Equal(hit, state.ShieldHit);
        field.BeginRound();
        field.Step(hull.Index, default);
        field.Refit(hull.Index);
        Assert.Equal(1, state.ShieldChangeTicks);

        // An offline shield losing its last emitter shows no change: it was not drawn.
        var other = store.Create(1, (byte)Content.PodDesign, PodDesign, Double3.Zero);
        ref var down = ref store.State(other.Index);
        down.ShieldOffline = true;
        down.ShieldChangeTicks = 50;
        store.Lose(other.Index, 1UL << Emitter);
        field.Refit(other.Index);
        Assert.Equal(50, down.ShieldChangeTicks);
    }
}

/// <summary>Sets of block ids as 64-bit words, at the sizes a design can have.</summary>
public sealed class BlockSetTests
{
    [Theory]
    [InlineData(1, 1)]
    [InlineData(63, 1)]
    [InlineData(64, 1)]
    [InlineData(65, 2)]
    [InlineData(4096, 64)]
    [InlineData(150_000, 2344)]
    public void ASetTakesAWordPerSixtyFourBlocksAndNeverHoldsABlockPastItsDesign(int blocks, int words)
    {
        Assert.Equal(words, BlockSet.WordCount(blocks));

        // Filled over whatever was there before, it is the design's blocks and nothing else.
        var set = new ulong[words];
        Array.Fill(set, ulong.MaxValue);
        BlockSet.Fill(set, blocks);
        Assert.Equal(blocks, BlockSet.Count(set));
        Assert.True(BlockSet.IsFull(set, blocks));
        Assert.True(BlockSet.Has(set, 0));
        Assert.True(BlockSet.Has(set, blocks - 1));
        for (var past = blocks; past < words * 64; past++)
            Assert.False(BlockSet.Has(set, past));

        // Losing the last block leaves it short of full, and no bit past the design appears.
        BlockSet.Remove(set, blocks - 1);
        Assert.False(BlockSet.IsFull(set, blocks));
        Assert.Equal(blocks - 1, BlockSet.Count(set));
        Assert.Equal(blocks > 1, BlockSet.Any(set));
        for (var past = blocks; past < words * 64; past++)
            Assert.False(BlockSet.Has(set, past));
    }

    [Fact]
    public void BlocksAreWalkedFromTheLowestIdUpAcrossWords()
    {
        int[] members = [0, 5, 63, 64, 100, 127, 128, 4095];
        var set = new ulong[BlockSet.WordCount(4096)];

        // Put in out of order; the walk is by id all the same.
        foreach (var block in members.Reverse())
            BlockSet.Add(set, block);
        var walked = new List<int>();
        foreach (var block in BlockSet.Each(set))
            walked.Add(block);
        Assert.Equal(members, walked);
        Assert.Equal(members.Length, BlockSet.Count(set));

        foreach (var _ in BlockSet.Each(new ulong[3]))
            Assert.Fail("an empty set has no blocks to walk");
        foreach (var _ in BlockSet.Each([]))
            Assert.Fail("a set of no words has no blocks to walk");
    }

    [Fact]
    public void SetsOfOneDesignCompareCopyAndContain()
    {
        var all = new ulong[2];
        BlockSet.Fill(all, 100);
        var some = new ulong[2];
        BlockSet.Add(some, 3);
        BlockSet.Add(some, 99);
        Assert.True(BlockSet.HasAll(all, some));
        Assert.False(BlockSet.HasAll(some, all));
        Assert.False(BlockSet.Equal(all, some));
        Assert.False(BlockSet.Any(new ulong[2]));

        var copy = new ulong[2];
        BlockSet.Copy(some, copy);
        Assert.True(BlockSet.Equal(some, copy));
        BlockSet.Remove(copy, 99);
        Assert.False(BlockSet.Has(copy, 99));
        Assert.True(BlockSet.Has(some, 99));
        Assert.False(BlockSet.HasAll(copy, some));
    }

    [Fact]
    public void TheShippedDesignsKeepTheirSetsAsWords()
    {
        foreach (var design in GameContent.Load().Designs)
        {
            Assert.Equal(BlockSet.WordCount(design.BlockCount), design.FullSet.Length);
            Assert.True(BlockSet.IsFull(design.FullSet, design.BlockCount));
            Assert.True(BlockSet.Any(design.CoreSet));
            Assert.True(BlockSet.HasAll(design.FullSet, design.CoreSet));
            foreach (var block in BlockSet.Each(design.CoreSet))
                Assert.True(design.Types[block].Core);
            Assert.InRange(design.BlockCount, 1, 64);
        }
    }
}

/// <summary>Where a hull's blocks, sums and hit points are kept: taken when it appears, given back when it goes.</summary>
public sealed class BlockStateStoreTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(64)]
    [InlineData(65)]
    [InlineData(4096)]
    public void AStateIsSizedToItsDesignAndStartsIntact(int blocks)
    {
        var design = TestDesigns.Cubes(blocks);
        using var store = new BlockStateStore();
        var decided = store.Create(design, hitPoints: true);
        var predicted = store.Create(design, hitPoints: false);
        Assert.Equal(2, store.Count);

        foreach (var state in new[] { decided, predicted })
        {
            Assert.True(store.IsLive(state));
            Assert.Equal(blocks, store.Blocks(state));
            Assert.Same(design, store.Design(state));
            Assert.Equal(BlockSet.WordCount(blocks), store.Alive(state).Length);
            Assert.True(BlockSet.IsFull(store.Alive(state), blocks));
            Assert.Equal(0u, store.Revision(state));
            Assert.Equal(0, store.CoresLost(state));

            // The sums start as the design's intact sums, bit for bit, and so do the figures read off them.
            Assert.True(Figures.SameSums(design.Sums.Intact, store.Sums(state)));
            design.Figures(store.Sums(state), out var flight, out var stats);
            Assert.Equal(design.Flight, flight);
            Assert.Equal(design.Stats, stats);
        }

        // Where the hull is decided every block has its hit points; a predicting client has none.
        Assert.Equal(blocks, store.HitPoints(decided).Length);
        Assert.True(store.HitPoints(predicted).IsEmpty);
        var total = 0.0;
        for (var b = 0; b < blocks; b++)
        {
            Assert.Equal(design.BlockHitPoints(b), store.HitPoints(decided)[b]);
            total += design.BlockHitPoints(b);
        }

        Assert.Equal(total, store.HitTotal(decided));
        Assert.Equal(0.0, store.HitTotal(predicted));
        Assert.Throws<InvalidOperationException>(() => store.Hit(predicted, 0, 1f));

        // The two do not share memory: what one loses the other keeps.
        Assert.True(store.Lose(decided, blocks - 1));
        Assert.True(BlockSet.Has(store.Alive(predicted), blocks - 1));
        Assert.True(Figures.SameSums(design.Sums.Intact, store.Sums(predicted)));
        Assert.Equal(0f, store.HitPoints(decided)[blocks - 1]);
    }

    [Fact]
    public void AReleasedStatesMemoryServesTheNextAndItsHandleGoesStale()
    {
        var design = TestDesigns.Cubes(64);
        using var store = new BlockStateStore(2);
        var first = store.Create(design, hitPoints: true);
        var kept = store.Create(design, hitPoints: true);
        var where = store.Alive(first);
        var sums = store.Sums(first);
        store.Lose(first, 10);
        store.Hit(first, 20, 3f);
        store.SetHitPoints(first, 30, 3f);

        store.Release(first);
        Assert.False(store.IsLive(first));
        Assert.Equal(1, store.Count);
        Assert.Throws<InvalidOperationException>(() => store.Alive(first));
        Assert.Throws<InvalidOperationException>(() => store.Revision(first));
        Assert.Throws<InvalidOperationException>(() => store.Sums(first));
        Assert.False(store.IsLive(default));

        // The next hull of that size gets the same memory under a new handle, as an intact hull: its blocks,
        // its hit points and its sums are the design's again.
        var next = store.Create(design, hitPoints: true);
        Assert.Equal(first.Index, next.Index);
        Assert.NotEqual(first, next);
        Assert.False(store.IsLive(first));
        Assert.True(where.Overlaps(store.Alive(next), out var offset));
        Assert.Equal(0, offset);
        Assert.True(sums.Overlaps(store.Sums(next), out var sumsOffset));
        Assert.Equal(0, sumsOffset);
        Assert.True(BlockSet.IsFull(store.Alive(next), 64));
        Assert.Equal(0u, store.Revision(next));
        Assert.True(Figures.SameSums(design.Sums.Intact, store.Sums(next)));
        Assert.Equal(store.HitTotal(kept), store.HitTotal(next));
        for (var b = 0; b < 64; b++)
            Assert.Equal(design.BlockHitPoints(b), store.HitPoints(next)[b]);

        // Releasing twice, or releasing a handle that was never taken, does nothing to anyone else.
        store.Release(first);
        store.Release(default);
        Assert.Equal(2, store.Count);
        Assert.True(store.IsLive(kept) && store.IsLive(next));
    }

    [Fact]
    public void AStateStaysWhereItIsAndAsItIsWhileOthersComeAndGo()
    {
        using var store = new BlockStateStore(1);
        int[] sizes = [1, 64, 65, 4096];
        var designs = sizes.Select(TestDesigns.Cubes).ToArray();
        var kept = new BlockStateHandle[sizes.Length];
        for (var i = 0; i < sizes.Length; i++)
        {
            kept[i] = store.Create(designs[i], hitPoints: true);
            for (var b = 0; b < sizes[i]; b++)
                store.SetHitPoints(kept[i], b, sizes[i] + b);
            store.Lose(kept[i], sizes[i] / 2);
        }

        // The spans taken now are compared with what the store hands out at the end.
        var alive0 = store.Alive(kept[0]);
        var alive1 = store.Alive(kept[1]);
        var alive2 = store.Alive(kept[2]);
        var alive3 = store.Alive(kept[3]);
        var points3 = store.HitPoints(kept[3]);
        var sums3 = store.Sums(kept[3]);
        var before = new ulong[sizes.Length][];
        var sumsBefore = new byte[sizes.Length][];
        var totals = new double[sizes.Length];
        for (var i = 0; i < sizes.Length; i++)
        {
            before[i] = store.Alive(kept[i]).ToArray();
            sumsBefore[i] = MemoryMarshal.AsBytes(store.Sums(kept[i])).ToArray();
            totals[i] = store.HitTotal(kept[i]);
        }

        // Thousands of hulls of every size appear, are knocked about and go: new chunks are taken and the
        // handle table grows.
        var others = new List<BlockStateHandle>();
        for (var round = 0; round < 6; round++)
        {
            for (var i = 0; i < 600; i++)
            {
                var design = designs[(i + round) % sizes.Length];
                var state = store.Create(design, hitPoints: i % 3 != 0);
                for (var b = 0; b < design.BlockCount; b += Math.Max(1, design.BlockCount / 5))
                    store.Lose(state, b);
                if (i % 3 != 0)
                    store.Hit(state, design.BlockCount - 1, 1e9f);
                others.Add(state);
            }

            for (var i = others.Count - 1; i >= 0; i -= 2)
            {
                store.Release(others[i]);
                others.RemoveAt(i);
            }
        }

        Assert.Equal(sizes.Length + others.Count, store.Count);
        Assert.True(alive0.Overlaps(store.Alive(kept[0]), out var offset0) && offset0 == 0);
        Assert.True(alive1.Overlaps(store.Alive(kept[1]), out var offset1) && offset1 == 0);
        Assert.True(alive2.Overlaps(store.Alive(kept[2]), out var offset2) && offset2 == 0);
        Assert.True(alive3.Overlaps(store.Alive(kept[3]), out var offset3) && offset3 == 0);
        Assert.True(points3.Overlaps(store.HitPoints(kept[3]), out var offsetPoints) && offsetPoints == 0);
        Assert.True(sums3.Overlaps(store.Sums(kept[3]), out var offsetSums) && offsetSums == 0);
        for (var i = 0; i < sizes.Length; i++)
        {
            Assert.Equal(before[i], store.Alive(kept[i]).ToArray());
            Assert.Equal(sumsBefore[i], MemoryMarshal.AsBytes(store.Sums(kept[i])).ToArray());
            Assert.Equal(totals[i], store.HitTotal(kept[i]));
            Assert.Equal(1u, store.Revision(kept[i]));
            Assert.False(BlockSet.Has(store.Alive(kept[i]), sizes[i] / 2));
            var points = store.HitPoints(kept[i]);
            for (var b = 0; b < points.Length; b++)
                Assert.Equal(b == sizes[i] / 2 ? 0f : sizes[i] + b, points[b]);
        }
    }

    [Fact]
    public void ALossKeepsTheSetTheCountTheSumsAndTheHitTotalInStep()
    {
        var design = TestDesigns.Cubes(300);
        using var store = new BlockStateStore();
        var state = store.Create(design, hitPoints: true);

        void InStep(int lost)
        {
            Assert.Equal((uint)lost, store.Revision(state));
            Assert.Equal(300 - lost, BlockSet.Count(store.Alive(state)));

            // The sums are those built afresh for the blocks that are left, to the last bit.
            var fresh = new HullSum[design.Sums.NodeCount];
            design.Sums.Build(fresh, store.Alive(state));
            Assert.True(Figures.SameSums(fresh, store.Sums(state)));
            design.Figures(store.Sums(state), out _, out var stats);
            Assert.Equal(300 - lost, stats.Blocks);

            // The total is what the blocks that are left have, and a block that is gone has nothing.
            var total = 0.0;
            for (var b = 0; b < 300; b++)
            {
                Assert.True(BlockSet.Has(store.Alive(state), b) || store.HitPoints(state)[b] == 0f);
                total += store.HitPoints(state)[b];
            }

            Assert.Equal(total, store.HitTotal(state), 1e-6);
        }

        InStep(0);
        Assert.True(store.Lose(state, 70));
        InStep(1);
        Assert.True(store.Lose(state, 299));
        InStep(2);

        // A block that is gone cannot be lost again, and has no hit points to set.
        Assert.False(store.Lose(state, 70));
        InStep(2);
        Assert.Throws<ArgumentOutOfRangeException>(() => store.Lose(state, 300));
        Assert.Throws<InvalidOperationException>(() => store.SetHitPoints(state, 70, 5f));

        // A hit a block survives is no loss; the one that empties it is, in the same call.
        var points = design.BlockHitPoints(5);
        Assert.False(store.Hit(state, 5, points * 0.5f));
        InStep(2);
        Assert.Equal(points * 0.5f, store.HitPoints(state)[5]);
        Assert.True(store.Hit(state, 5, points));
        InStep(3);
        Assert.False(store.Hit(state, 5, points));
        InStep(3);

        // The core is block 0 of these designs: its loss is told apart from any other.
        Assert.Equal(0, store.CoresLost(state));
        Assert.True(store.Lose(state, 0));
        Assert.Equal(1, store.CoresLost(state));
        InStep(4);
    }

    [Fact]
    public void AStationOfAHundredAndFiftyThousandBlocksIsStoredAndItsMemoryServesTheNextStation()
    {
        const int blocks = 150_000;
        var design = TestDesigns.Station;
        using var store = new BlockStateStore();
        var station = store.Create(design, hitPoints: true);
        Assert.Equal(2344, store.Alive(station).Length);
        Assert.Equal(blocks, BlockSet.Count(store.Alive(station)));
        Assert.Equal(blocks, store.HitPoints(station).Length);
        var where = store.Alive(station);

        // The first and the last block are as much its own as any between.
        Assert.True(store.Lose(station, blocks - 1));
        Assert.True(store.Lose(station, 1));
        Assert.Equal(blocks - 2, BlockSet.Count(store.Alive(station)));
        Assert.Equal(2u, store.Revision(station));

        // A small hull beside it lives in a chunk; the station has a block of its own, kept for the next one.
        var pod = store.Create(TestDesigns.Cubes(27), hitPoints: true);
        store.Release(station);
        var next = store.Create(design, hitPoints: true);
        Assert.True(where.Overlaps(store.Alive(next), out var offset) && offset == 0);
        Assert.True(BlockSet.IsFull(store.Alive(next), blocks));
        Assert.True(BlockSet.IsFull(store.Alive(pod), 27));
        Assert.Equal(0u, store.Revision(next));
        Assert.True(Figures.SameSums(design.Sums.Intact, store.Sums(next)));
    }

    [Fact]
    public void LosingBlocksAndHullsComingAndGoingAtASteadyNumberTakeNoManagedMemory()
    {
        var small = TestDesigns.Cubes(64);
        var large = TestDesigns.Cubes(300);
        using var store = new BlockStateStore();
        var held = store.Create(TestDesigns.Cubes(4096), hitPoints: true);
        var states = new BlockStateHandle[64];

        void Round()
        {
            for (var i = 0; i < states.Length; i++)
                states[i] = store.Create(i % 2 == 0 ? small : large, hitPoints: i % 4 < 2);
            for (var i = 0; i < states.Length; i++)
            {
                store.Lose(states[i], i % 64);
                if (i % 4 < 2)
                    store.Hit(states[i], 63 - i % 64, 10f);
                store.Release(states[i]);
            }
        }

        Round();

        // The held hull loses each block once, so a run in doubt starts again with a fresh one.
        var allocation = new AllocationProbe();
        var runs = 0;
        while (allocation.Run())
        {
            if (runs++ > 0)
            {
                store.Release(held);
                held = store.Create(TestDesigns.Cubes(4096), hitPoints: true);
            }

            for (var round = 0; round < 50; round++)
            {
                allocation.Start();
                Round();
                store.Lose(held, round);
                allocation.Stop();
            }
        }

        Assert.Equal(0, allocation.Bytes);
        Assert.Equal(50u, store.Revision(held));
    }
}

/// <summary>
/// Hulls of more blocks than the loader admits yet, built straight from boxes: stored, damaged, refitted and
/// collided through the same code as the shipped designs, with the blocks past the first 64 doing the work.
/// </summary>
public sealed class LargeHullTests
{
    private static readonly GameContent Content = GameContent.Load();

    /// <summary>The slab's core, in the middle of it and well past the first word of its block set.</summary>
    private const int Core = 150;
    private static readonly HullDesign Slab = BuildSlab();
    private static readonly HullDesign[] Designs = [Slab];

    /// <summary>
    /// A slab of 300 unit cubes, 12 long, 5 high and 5 deep, numbered along its length: block 25 x + 5 y + z, so
    /// the face at the far end of x is blocks 275 to 299. Its working blocks sit in the middle.
    /// </summary>
    private static HullDesign BuildSlab()
    {
        var boxes = new DesignBox[300];
        for (var i = 0; i < boxes.Length; i++)
        {
            var type = Content.Blocks.Get(
                i switch
                {
                    Core => "core",
                    151 or 152 => "generator",
                    >= 153 and <= 156 => "thruster",
                    >= 157 and <= 160 => "gyro",
                    161 or 162 => "shield",
                    _ => "hull",
                }
            );
            boxes[i] = new DesignBox(new GridSize(i / 25, i / 5 % 5, i % 5), new GridSize(1, 1, 1), type);
        }

        return HullDesign.Of(new BoxDesign("Slab", 1f, boxes, Content.Blocks.Limits, "slab", HullClass.Ship, new Vector3(0.5f)), Content.Rules);
    }

    private static ulong[] Set(params int[] blocks)
    {
        var set = new ulong[Slab.FullSet.Length];
        foreach (var block in blocks)
            BlockSet.Add(set, block);
        return set;
    }

    private static List<int> LostBlocks(HullStore store, int hull)
    {
        var lost = new List<int>();
        for (var b = 0; b < Slab.BlockCount; b++)
        {
            if (!BlockSet.Has(store.Blocks(hull), b))
                lost.Add(b);
        }

        return lost;
    }

    [Fact]
    public void TheLoaderAdmitsMoreThanOneWord()
    {
        Assert.True(Slab.BlockCount > 64);
        Assert.Equal(5, Slab.FullSet.Length);
        Assert.True(BlockSet.IsFull(Slab.FullSet, 300));
        Assert.True(BlockSet.Equal(Slab.CoreSet, Set(Core)));

        // A normal file now admits more than one word of live blocks.
        var dir = Path.Combine(Path.GetTempPath(), $"prospect-slab-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var blocks = string.Join(",", Enumerable.Range(0, 65).Select(i => $"{{ \"at\": [{i}, 0, 0], \"type\": \"{(i switch { 0 => "core", 1 => "thruster", 2 => "gyro", 3 => "generator", _ => "hull" })}\" }}"));
            var path = Path.Combine(dir, "slab.json");
            File.WriteAllText(path, $"{{ \"name\": \"T\", \"class\": \"ship\", \"blockSize\": 1.0, \"color\": [0.5, 0.5, 0.5], \"blocks\": [{blocks}] }}");
            Assert.Equal(65, HullDesign.Load(path, Content.Blocks, Content.Rules).BlockCount);
        }
        finally
        {
            Directory.Delete(dir, true);
        }

        Assert.Equal(300, Slab.Stats.Blocks);
        Assert.True(Slab.Flight.ShieldCapacity > 0f);
    }

    [Fact]
    public void AHullOfThreeHundredBlocksLosesBlocksPastTheFirstWordIsRefittedOnceAndDiesWithItsCore()
    {
        using var store = new HullStore(4);
        var field = new HullField(store, Designs);
        var hull = store.Create(1, 0, Slab, Double3.Zero).Index;
        ref var state = ref store.State(hull);
        Assert.Equal(300, store.BlockHp(hull).Length);
        Assert.Equal(Slab.Flight.HullPoints, store.BlockHp(hull).ToArray().Sum(), 0);

        // With the shield down a blow lands on the blocks it touched: three plates, each given exactly its own.
        state.ShieldOffline = true;
        var body = new HullBody { Design = Slab, Alive = store.Blocks(hull), States = store.BlockStates, State = store.Info(hull).BlockState };
        var plate = Slab.BlockHitPoints(70);
        Assert.False(HullSim.Damage(ref state, store.Flight(hull), ref body, Set(70, 200, 299), 3, plate * 3));
        Assert.Equal([70, 200, 299], LostBlocks(store, hull));
        Assert.Equal(3u, store.Revision(hull));
        Assert.Equal(Slab.Flight.HullPoints - plate * 3, state.Hull, 0);

        // The stored set changed at once; the figures follow at the refit, and are those of a fresh evaluation.
        Assert.Equal(Slab.Flight.Mass, store.Flight(hull).Mass);
        field.Refit(hull);
        Slab.Evaluate(store.Blocks(hull), out var flight, out var stats);
        Assert.Equal(flight, store.Flight(hull));
        Assert.Equal(297, stats.Blocks);
        Assert.True(flight.Mass < Slab.Flight.Mass);
        Assert.Equal(3u, store.Info(hull).FittedRevision);

        // With nothing lost since, a refit works nothing out: figures changed behind its back stay as they are.
        store.Flight(hull).Mass = 1f;
        field.Refit(hull);
        Assert.Equal(1f, store.Flight(hull).Mass);
        store.Flight(hull) = flight;

        // A blow a block survives is no loss.
        Assert.False(HullSim.Damage(ref state, store.Flight(hull), ref body, Set(250), 1, plate * 0.5f));
        Assert.Equal(3u, store.Revision(hull));
        field.Refit(hull);
        Assert.Equal(flight, store.Flight(hull));

        // Both emitters go in one blow: two losses, one refit, and the shield is gone with them.
        var emitter = Slab.BlockHitPoints(161);
        Assert.False(HullSim.Damage(ref state, store.Flight(hull), ref body, Set(161, 162), 2, emitter * 2));
        Assert.Equal(5u, store.Revision(hull));
        field.Refit(hull);
        Slab.Evaluate(store.Blocks(hull), out flight, out _);
        Assert.Equal(flight, store.Flight(hull));
        Assert.Equal(0f, flight.ShieldCapacity);
        Assert.Equal(0f, state.Shield);

        // The core is block 150. When it goes the hull is destroyed, and its block state goes back with it.
        Assert.True(HullSim.Damage(ref state, store.Flight(hull), ref body, Set(Core), 1, Slab.BlockHitPoints(Core)));
        Assert.False(BlockSet.Has(store.Blocks(hull), Core));
        Assert.Equal(6u, store.Revision(hull));
        var blocks = store.Info(hull).BlockState;
        store.Destroy(store.HandleAt(hull));
        Assert.False(store.BlockStates.IsLive(blocks));
        Assert.Equal(0, store.BlockStates.Count);
    }

    /// <summary>Two slabs end to end along x, the second turned about so its far face meets the first's, closing at a speed.</summary>
    private static (int Struck, int Rammer) Place(HullStore store, float speed)
    {
        store.Clear();
        var struck = store.Create(1, 0, Slab, Double3.Zero).Index;
        var rammer = store.Create(2, 0, Slab, new Double3(14, 0, 0)).Index;
        store.State(rammer).Orientation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI);
        store.State(rammer).Velocity = new Vector3(-speed, 0, 0);
        return (struck, rammer);
    }

    [Fact]
    public void TwoLargeHullsRammingLoseBlocksPastTheFirstWordAndThePredictionPicksTheSameContact()
    {
        using var store = new HullStore();
        using var copy = new HullStore();
        var field = new HullField(store, Designs);
        var predicted = new HullField(copy, Designs, authoritative: false);

        // A ram the shields can take: the predicting field, which knows no hit points, moves both hulls exactly
        // as the deciding one does, tick for tick, through the contact and after it.
        var (struck, rammer) = Place(store, 12f);
        Place(copy, 12f);
        for (var i = 0; i < 40; i++)
        {
            field.BeginRound();
            field.Step(rammer, default);
            field.Step(struck, default);
            predicted.BeginRound();
            predicted.Step(rammer, default);
            predicted.Step(struck, default);
            foreach (var hull in new[] { struck, rammer })
            {
                Assert.Equal(store.State(hull).Position, copy.State(hull).Position);
                Assert.Equal(store.State(hull).Velocity, copy.State(hull).Velocity);
                Assert.Equal(store.State(hull).AngularVelocity, copy.State(hull).AngularVelocity);
                Assert.Equal(store.State(hull).Shield, copy.State(hull).Shield);
            }
        }

        Assert.True(store.State(struck).Velocity.X < -1f, "the ram never happened");
        Assert.True(store.State(struck).Shield < Slab.Flight.ShieldCapacity, "the shield took nothing");
        Assert.Equal(0u, store.Revision(struck));
        Assert.Equal(0u, store.Revision(rammer));

        // A ram that breaks blocks off both. The faces that meet are blocks 275 to 299 of each hull.
        (struck, rammer) = Place(store, 160f);
        Place(copy, 160f);
        for (var i = 0; i < 10; i++)
        {
            field.BeginRound();
            field.Step(rammer, default);
            field.Step(struck, default);
            predicted.BeginRound();
            predicted.Step(rammer, default);
            predicted.Step(struck, default);
            Assert.Equal(0, predicted.Lost.Length);
        }

        foreach (var hull in new[] { struck, rammer })
        {
            var lost = LostBlocks(store, hull);
            Assert.NotEmpty(lost);
            Assert.All(lost, block => Assert.InRange(block, 275, 299));
            Assert.Equal((uint)lost.Count, store.Revision(hull));

            // Each was refitted for what it lost, to the figures a fresh evaluation gives.
            Assert.Equal(store.Revision(hull), store.Info(hull).FittedRevision);
            Slab.Evaluate(store.Blocks(hull), out var flight, out _);
            Assert.Equal(flight, store.Flight(hull));
            Assert.Equal(store.BlockHp(hull).ToArray().Sum(), store.State(hull).Hull, 0);

            // The prediction shows the hull points falling and keeps every block until it is told.
            Assert.True(BlockSet.IsFull(copy.Blocks(hull), Slab.BlockCount));
            Assert.Equal(0u, copy.Revision(hull));
            Assert.Equal(Slab.Flight, copy.Flight(hull));
            Assert.True(copy.State(hull).Hull < Slab.Flight.HullPoints);
            Assert.Equal(0f, copy.State(hull).Shield);
        }
    }
}

public sealed class SizedCollisionTests
{
    private static readonly GameContent Content = GameContent.Load();

    private static HullDesign Beam(FlightRules? rules = null)
    {
        var boxes = new DesignBox[70];
        for (var i = 0; i < 69; i++)
            boxes[i] = new(new GridSize(i * 3, 100, 0), new GridSize(1, 1, 1), Content.Blocks.Get(i == 0 ? "core" : "hull"));
        boxes[69] = new(new GridSize(0, 0, 0), new GridSize(32, 1, 1), Content.Blocks.Get("hull"));
        return HullDesign.Of(new BoxDesign("Beam", 1, boxes, Content.Blocks.Limits, "collision fixture"), rules ?? Content.Rules);
    }

    [Fact]
    public void RotatedThinBoxesUseAllSeparatingAxesAndRejectCorners()
    {
        var rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 4);
        var end = Vector3.Transform(new Vector3(14, 0, 0), rotation);
        Assert.True(BoxCollision.Contact(-end, new Vector3(16, 0.5f, 0.5f), rotation, new Vector3(0.5f), out _, out var depth));
        Assert.True(depth > 0);
        Assert.False(BoxCollision.Contact(new Vector3(8, -8, 0), new Vector3(16, 0.5f, 0.5f), rotation, new Vector3(0.5f), out _, out _));
    }

    [Fact]
    public void FieldTouchesRotatedBeamFarFromItsCenterBeyondId63AndSkipsDeadBranches()
    {
        var beam = Beam();
        var cube = HullDesign.Of(new BoxDesign("Cube", 1, [new(new GridSize(0, 0, 0), new GridSize(1, 1, 1), Content.Blocks.Get("core"))], Content.Blocks.Limits, "cube"), Content.Rules with { CollisionTreeVisits = 32, CollisionBoxTests = 1, CollisionContacts = 1 });
        using var store = new HullStore();
        var a = store.Create(1, 0, beam, Double3.Zero).Index;
        var rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 4);
        store.State(a).Orientation = rotation;
        var hit = Vector3.Transform(beam.BlockOffsets[69] + new Vector3(14, 0.8f, 0), rotation);
        var b = store.Create(2, 1, cube, Double3.Zero + hit).Index;
        var field = new HullField(store, [beam, cube]);
        store.BlockStates.SetHitPoints(store.Info(a).BlockState, 69, 0.01f);
        store.State(b).Velocity = Vector3.Transform(new Vector3(0, -10, 0), rotation);
        var before = store.State(b).Position;
        field.Step(b, default);
        Assert.NotEqual(before, store.State(b).Position);
        Assert.False(BlockSet.Has(store.Blocks(a), 69));
        store.State(b).Velocity = Vector3.Zero;
        store.State(b).Position = before;
        field.Step(b, default);
        Assert.Equal(before, store.State(b).Position);
        Assert.Equal(69, store.BlockStates.CollisionCounts(store.Info(a).BlockState)[0]);
    }

    [Fact]
    public void SizedFieldContactAndDeadBranchQueriesTakeNoWarmMemory()
    {
        var design = Beam(Content.Rules with { CollisionDamagePerTonneSpeedSquared = 0 });
        using var store = new HullStore();
        var a = store.Create(1, 0, design, Double3.Zero).Index;
        var b = store.Create(2, 0, design, new Double3(0, 0.9, 0)).Index;
        var field = new HullField(store, [design]);
        var stateA = store.State(a); var stateB = store.State(b);
        void Contact()
        {
            store.State(a) = stateA; store.State(b) = stateB;
            field.BeginRound(); field.Step(a, default); field.Step(b, default);
        }
        for (var i = 0; i < 10; i++) Contact();
        var allocation = new AllocationProbe();
        for (var i = 0; i < 100; i++)
            while (allocation.Measuring()) Contact();
        Assert.Equal(0, allocation.Bytes);
        Assert.NotEqual(stateA.Position, store.State(a).Position);
    }

    [Fact]
    public void RockSamplesFindAnEdgeAwayFromTheBoxCenterAndRejectEmptySpace()
    {
        var rock = new Asteroid { Radius = 2, Rotation = Quaternion.Identity, Shape = 0 };
        Assert.True(BoxCollision.Rock(new Vector3(15, 0, 0), new Vector3(16, 0.5f, 0.5f), Quaternion.Identity, rock, out _, out var depth, out _));
        Assert.True(depth > 0);
        Assert.False(BoxCollision.Rock(new Vector3(15, 5, 0), new Vector3(16, 0.5f, 0.5f), Quaternion.Identity, rock, out _, out _, out _));
    }

    [Fact]
    public void TreeBudgetAndOrderAreFixedAndWarmTraversalAllocatesNothing()
    {
        var design = Beam(Content.Rules with { CollisionTreeVisits = 20, CollisionBoxTests = 1, CollisionContacts = 1 });
        int Query()
        {
            var cursor = 0; var visits = 20; var hash = 0;
            while (design.Collision.Next(ref cursor, new Vector3(-1000), new Vector3(1000), design.FullSet, ref visits, out var id)) hash = unchecked(hash * 31 + id);
            return unchecked(hash * 31 + visits);
        }
        var expected = Query();
        var allocation = new AllocationProbe();
        var same = true;
        for (var i = 0; i < 100; i++)
            while (allocation.Measuring()) same &= expected == Query();
        Assert.True(same);
        Assert.Equal(0, allocation.Bytes);
    }
}

/// <summary>The local proof mailbox adds no work or memory traffic requiring an account request during ordinary ticks.</summary>
public sealed class LocalClaimTickTests
{
    [Fact]
    public void OfflinePrivateTicksAllocateNothingAndNeverStartProof()
    {
        var content = GameContent.Load();
        using var rig = new SaveRig(SaveRig.NewWorld(content), content);
        rig.Join();
        rig.Tick(30);
        var allocation = new AllocationProbe();
        for (var i = 0; i < 30; i++)
            while (allocation.Measuring()) rig.Tick();
        Assert.Equal(0, allocation.Bytes);
        Assert.Equal(LocalClaimState.None, rig.Server.LocalClaim);
    }
}

public sealed class ExplorationWorldTests
{
    [Fact]
    public void SensorVolumeCrossesEverySectorAxisWithoutChartingOrAllocating()
    {
        var content = GameContent.Load();
        var radius = content.Exploration.SensorRadius;
        var origin = new Double3(content.Galaxy.SectorSize / 2, content.Galaxy.SectorSize / 2, content.Galaxy.SectorSize / 2);
        Assert.True(content.Exploration.Covers(origin, origin + new Double3(radius, 0, 0)));
        Assert.True(content.Exploration.Covers(origin, origin + new Double3(0, radius, 0)));
        Assert.True(content.Exploration.Covers(origin, origin + new Double3(0, 0, radius)));
        Assert.False(content.Exploration.Covers(origin, origin + new Double3(radius, radius, radius)));
        var probe = new AllocationProbe();
        while (probe.Measuring()) _ = content.Exploration.Covers(origin, origin + new Double3(1, 1, 1));
        Assert.Equal(0, probe.Bytes);
    }
}


public sealed class NewShipProtocolTests
{
    [Fact]
    public void NamesRoundTripAtTheUnicodeLimitWithoutAllocatingAndTruncationIsRejected()
    {
        var text = string.Concat(Enumerable.Repeat("🚀", 32));
        Prospect.Game.Protocol.ShipName name = default;
        Assert.True(name.Set(text));
        Assert.False(name.Set(text + "x"));
        var buffer = new byte[Prospect.Game.Protocol.EditorRequestMessage.MaxWireSize];
        var writer = new Prospect.Engine.Net.NetWriter(buffer);
        new Prospect.Game.Protocol.EditorRequestMessage { Kind = Prospect.Game.Protocol.EditorRequestKind.NewShip, Name = name }.Write(ref writer, default);
        Assert.Equal(buffer.Length, writer.Length);
        var allocation = new AllocationProbe();
        for (var i = 0; i < 100; i++)
        {
            while (allocation.Measuring())
            {
                var reader = new Prospect.Engine.Net.NetReader(buffer.AsSpan(1));
                if (!Prospect.Game.Protocol.EditorRequestMessage.TryRead(ref reader, default, out var parsed) || parsed.Name.Length != 64)
                    throw new InvalidOperationException("Name did not round trip.");
            }
        }
        Assert.Equal(0, allocation.Bytes);
        for (var size = 1; size < buffer.Length; size++)
        {
            var reader = new Prospect.Engine.Net.NetReader(buffer.AsSpan(1, size - 1));
            Assert.False(Prospect.Game.Protocol.EditorRequestMessage.TryRead(ref reader, default, out _));
        }
    }
}

public sealed class GasEnvironmentWorldTests
{
    [Fact]
    public void LightningWorksWithoutOpticalPenaltyAndColdDoesNotDisrupt()
    {
        var content = GameContent.Load();
        var rules = content.Exploration with { GasOpticalPenalty = 0 };
        GasCloudPlacement[] clouds = [new(default, new(Vector3.Zero, new Vector3(1000), 0, GasCloudKind.Lightning))];
        Assert.Equal(HullEnvironment.InLightningCloud | HullEnvironment.SensorsDisrupted,
            GasSensors.EnvironmentAt(content.Galaxy, clouds, rules, default, 0));
        Assert.False(GasSensors.Covers(content.Galaxy, clouds, rules, default, new Double3(1000, 0, 0), tick: 0));
        Assert.True(GasSensors.Covers(content.Galaxy, clouds, rules, default, new Double3(1000, 0, 0), tick: rules.LightningBurstTicks));
        clouds[0] = clouds[0] with { Cloud = clouds[0].Cloud with { Kind = GasCloudKind.Cold } };
        Assert.Equal(HullEnvironment.InColdCloud, GasSensors.EnvironmentAt(content.Galaxy, clouds, rules, default, 0));
        Assert.True(GasSensors.Covers(content.Galaxy, clouds, rules, default, new Double3(1000, 0, 0), tick: 0));
    }
}
