using System.Diagnostics;
using System.Numerics;
using Prospect.Game.Client;
using Prospect.Game.Server;
using Prospect.Game.World;
using Xunit;

namespace Prospect.Tests;

public sealed class GalaxyTests
{
    private static readonly GameContent Content = GameContent.Load();

    [Fact]
    public void SectorsAreAFunctionOfSeedAndCoordinate()
    {
        var a = Content.Galaxy.WithSeed(42);
        var b = Content.Galaxy.WithSeed(42);
        var other = Content.Galaxy.WithSeed(43);
        var first = new SectorContent();
        var second = new SectorContent();
        var differing = 0;

        for (var x = -3; x <= 3; x++)
        {
            var coord = new SectorCoord(x, 1, -x * 2);
            a.Generate(coord, first);
            b.Generate(coord, second);
            Assert.Equal(a.NameOf(coord), b.NameOf(coord));
            Assert.Equal(first.AsteroidCount, second.AsteroidCount);
            for (var i = 0; i < first.AsteroidCount; i++)
                Assert.Equal(first.Asteroids[i].LocalPosition, second.Asteroids[i].LocalPosition);

            if (other.NameOf(coord) != a.NameOf(coord))
                differing++;
        }

        Assert.True(differing >= 6);
    }

    [Fact]
    public void AsteroidsStayInsideTheirSectorAndClearOfTheSpawnPoint()
    {
        var galaxy = Content.Galaxy.WithSeed(7);
        var content = new SectorContent();
        var half = (float)(galaxy.SectorSize / 2);

        for (var x = -4; x <= 4; x++)
            for (var z = -4; z <= 4; z++)
            {
                galaxy.Generate(new SectorCoord(x, 0, z), content);
                Assert.InRange(content.AsteroidCount, 0, galaxy.MaxAsteroidsPerSector);
                for (var i = 0; i < content.AsteroidCount; i++)
                {
                    var rock = content.Asteroids[i];
                    Assert.True(MathF.Abs(rock.LocalPosition.X) + rock.Radius <= half + 0.01f);
                    Assert.True(MathF.Abs(rock.LocalPosition.Y) + rock.Radius <= half + 0.01f);
                    Assert.True(MathF.Abs(rock.LocalPosition.Z) + rock.Radius <= half + 0.01f);
                    Assert.InRange(rock.Shape, 0, Galaxy.AsteroidShapes - 1);
                    if (x == 0 && z == 0)
                        Assert.True(rock.LocalPosition.Length() >= rock.Radius + 149f);
                }
            }

        Assert.True(galaxy.Summarize(default).AsteroidCount > 0);
    }

    [Fact]
    public void PositionsMapToSectorsWithoutGapsAtTheBoundaries()
    {
        var galaxy = Content.Galaxy;
        var size = galaxy.SectorSize;
        Assert.Equal(new SectorCoord(0, 0, 0), galaxy.SectorOf(new Double3(size * 0.49, -size * 0.49, 0)));
        Assert.Equal(new SectorCoord(1, -1, 0), galaxy.SectorOf(new Double3(size * 0.51, -size * 0.51, 0)));
        Assert.Equal(new SectorCoord(-3, 0, 5), galaxy.SectorOf(galaxy.CenterOf(new SectorCoord(-3, 0, 5))));
    }
}

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

        var overheated = boosted;
        for (var i = 0; i < 600 && !overheated.BoostLocked; i++)
            overheated = Fly(overheated, new FlightInput { Forward = 127, Boost = true }, 1);
        Assert.True(overheated.BoostLocked);

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
        Assert.True(gained <= Pod.Acceleration.Z + 0.01f);
        Assert.Equal(before.Shield, state.Shield);
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

    /// <summary>Flies a pod at an asteroid from one side until it hits or the ticks run out.</summary>
    private static bool Crash(Vector3 from, Vector3 velocity, in FlightParams flight, out HullState state, out ulong alive, out float[] hp, RockImpulses? impulses = null)
    {
        var galaxy = Content.Galaxy.WithSeed(11);
        var rocks = new RockField(galaxy);
        var rock = rocks.Sector(default).Asteroids[0];
        var rockCenter = galaxy.CenterOf(default) + rock.LocalPosition;

        var design = Content.Pod;
        hp = new float[HullDesign.MaxBlocks];
        for (var i = 0; i < design.Types.Length; i++)
            hp[i] = design.Types[i].Hull;
        var body = new HullBody { Design = design, Alive = design.FullMask, BlockHp = hp };

        state = HullState.Spawn(rockCenter + Vector3.Normalize(from) * (rock.Radius + 60f), flight);
        state.Velocity = velocity;
        var order = default(HullOrder);
        var destroyed = false;
        for (var tick = 0; tick < 300 && !destroyed; tick++)
        {
            destroyed = HullSim.Step(ref state, ref order, default, flight, ref body, rocks, tick, impulses);
            if (Vector3.Dot(state.Velocity, velocity) < 0)
                break;
        }

        alive = body.Alive;
        return destroyed;
    }

    [Fact]
    public void AGentleBumpBouncesTheHullAndOnlyCostsShield()
    {
        var destroyed = Crash(new Vector3(-1, 0, 0), new Vector3(40, 0, 0), Pod, out var state, out var alive, out _);

        Assert.False(destroyed);
        Assert.True(state.Velocity.X < 0, "the hull should have bounced back");
        Assert.InRange(state.Shield, 1f, Pod.ShieldCapacity - 1f);
        Assert.Equal(Content.Pod.FullMask, alive);
        Assert.Equal(Pod.HullPoints, state.Hull);
    }

    [Fact]
    public void AHardImpactDestroysTheBlocksThatHitAndTheHullFliesOnWhatIsLeft()
    {
        var destroyed = Crash(new Vector3(-1, 0, 0), new Vector3(170, 0, 0), Pod, out var state, out var alive, out var hp);

        // Hitting side-on costs the blocks on that side; the core in the middle survives.
        Assert.False(destroyed);
        Assert.NotEqual(Content.Pod.FullMask, alive);
        Assert.Equal(Content.Pod.CoreMask, alive & Content.Pod.CoreMask);
        Assert.Equal(0f, state.Shield);
        Assert.Equal(hp.Sum(), state.Hull, 1);
        Assert.True(state.Hull < Pod.HullPoints);

        Content.Pod.Evaluate(alive, out var flight, out var stats);
        Assert.True(flight.Mass < Pod.Mass);
        Assert.True(stats.Blocks < Content.Pod.Cells.Length);

        // Damage grows with the square of the speed over the safe limit: a faster hit takes more with it.
        Crash(new Vector3(-1, 0, 0), new Vector3(95, 0, 0), Pod, out var slower, out _, out _);
        Assert.True(slower.Hull > state.Hull);
    }

    [Fact]
    public void LosingTheCoreDestroysTheHull()
    {
        // Belly-first at speed: the core sits in the bottom layer and takes the hit.
        Assert.True(Crash(new Vector3(0, 1, 0), new Vector3(0, -300, 0), Pod, out _, out var alive, out _));
        Assert.NotEqual(Content.Pod.CoreMask, alive & Content.Pod.CoreMask);
    }

    [Fact]
    public void AHeavyHullShovesAnAsteroidWhichThenDriftsToRest()
    {
        var galaxy = Content.Galaxy.WithSeed(11);
        var rocks = new RockField(galaxy);
        var rock = rocks.Sector(default).Asteroids[0];
        var rockMass = rocks.Mass(rock.Radius);

        // A hull as heavy as the rock itself, with damage switched off to look at the momentum alone.
        var heavy = Pod;
        heavy.Mass = rockMass;
        heavy.Inertia = Pod.Inertia * (rockMass / Pod.Mass);
        heavy.CollisionDamagePerTonneSpeedSquared = 0;
        var impulses = new RockImpulses();
        Crash(new Vector3(-1, 0, 0), new Vector3(20, 0, 0), heavy, out var state, out _, out _, impulses);

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
        Crash(new Vector3(0, 1, 0), new Vector3(0, -20, 0), Pod, out _, out _, out _, light);
        Assert.True(light.Count >= 1);
        Assert.True(light.Items[0].Velocity.Length() < 0.1f);
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

            Assert.InRange(largest, 0.9f, 1.03f);
            Assert.True(smallest > 0.25f);
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
            if ((_hulls.Info(a).Blocks & 1UL << i) == 0)
                continue;
            var world = _hulls.State(a).Position + Vector3.Transform(first.BlockOffsets[i], _hulls.State(a).Orientation);
            for (var j = 0; j < second.BlockOffsets.Length; j++)
            {
                if ((_hulls.Info(b).Blocks & 1UL << j) == 0)
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
        var podMass = _hulls.Flight(pod).Mass;
        var vesselMass = _hulls.Flight(vessel).Mass;

        var closest = Run(60, pod, vessel);

        // Both changed course: the pod lost speed, the vessel was shoved the way the pod was going.
        var podVelocity = _hulls.State(pod).Velocity;
        var vesselVelocity = _hulls.State(vessel).Velocity;
        Assert.True(podVelocity.X > -60f, $"the pod kept {podVelocity.X} m/s");
        Assert.True(vesselVelocity.X < -15f, $"the vessel only reached {vesselVelocity.X} m/s");

        // What one lost the other gained, weighed with the masses they had when they met.
        var after = podVelocity * podMass + vesselVelocity * vesselMass;
        Assert.True((after - before).Length() < before.Length() * 1e-3f, $"momentum went from {before} to {after}");

        // The shields went first, then the blocks that touched: some on each hull, and not all of either.
        Assert.Equal(0f, _hulls.State(pod).Shield);
        Assert.Equal(0f, _hulls.State(vessel).Shield);
        Assert.InRange(DamagedBlocks(pod), 1, Content.Pod.Cells.Length - 1);
        Assert.InRange(DamagedBlocks(vessel), 1, Content.Designs[Vessel].Cells.Length - 1);
        Assert.True(_hulls.State(pod).Hull < Content.Pod.Flight.HullPoints);
        Assert.True(_hulls.State(vessel).Hull < Content.Designs[Vessel].Flight.HullPoints);

        // The pod came from +X: the damage is on the vessel's side facing it.
        var hp = _hulls.BlockHp(vessel);
        var design = Content.Designs[Vessel];
        for (var i = 0; i < design.Types.Length; i++)
            Assert.True(hp[i] >= design.Types[i].Hull || design.BlockOffsets[i].X > 0, $"block {i} on the far side was damaged");

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
            _hulls.Info(pod).Blocks = 1UL << left;
            var drone = Add(Content.DroneDesign, Double3.Zero + design.BlockOffsets[over] + new Vector3(0, 6, 0), new Vector3(0, -4, 0));
            for (var i = 0; i < 180; i++)
                Tick();
            Assert.Equal(1UL << left, _hulls.Info(pod).Blocks);
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
        Assert.NotEqual(Content.Pod.CoreMask, _hulls.Info(pod).Blocks & Content.Pod.CoreMask);
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
        var before = GC.GetAllocatedBytesForCurrentThread();
        var shield = Scenario();
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
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
            _field.Step(pod, thrust);
            predicted.Step(pod, thrust);
            Assert.Equal(0, predicted.Lost.Length);
        }

        Assert.NotEqual(Content.Pod.FullMask, _hulls.Info(pod).Blocks);
        Assert.Equal(Content.Pod.FullMask, copy.Info(pod).Blocks);
        Assert.Equal(Content.Designs[Vessel].FullMask, copy.Info(vessel).Blocks);
        Assert.Equal(0f, copy.State(pod).Shield);
        Assert.True(copy.State(pod).Hull < Content.Pod.Flight.HullPoints);
        Assert.True(copy.State(vessel).Hull < Content.Designs[Vessel].Flight.HullPoints);
    }
}

public sealed class JumpTests
{
    private static readonly GameContent Content = GameContent.Load();

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
        var destination = galaxy.CenterOf(new SectorCoord(100, 2, -37));
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
        Assert.Equal(new SectorCoord(100, 2, -37), galaxy.SectorOf(state.Position));
        Assert.True(state.Velocity.Length() < 0.01f);

        // Charging takes the jump's energy at the drive's power, here with a full battery behind it.
        Assert.InRange(chargeTicks * HullSim.Dt, flight.FtlEnergy / flight.FtlPower - 0.1f, flight.FtlEnergy / flight.FtlPower + 0.1f);
        Assert.True(flight.FtlEnergy / flight.FtlPower > 5f, "the charge should be slow enough to watch");
    }

    [Fact]
    public void ChargingDrawsFromTheBatteryAndAFlatBatteryOnlySlowsItDown()
    {
        var flight = Content.Pod.Flight;
        var order = new HullOrder { Kind = OrderKind.Jump, Target = new Double3(0, 0, -1e6) };

        float SecondsToFire(float startingEnergy, out float energyAtFire)
        {
            var state = HullState.Spawn(Double3.Zero, flight);
            state.Energy = startingEnergy;
            var jump = order;
            var ticks = 0;
            energyAtFire = 0;
            while (state.Jump != JumpPhase.Tunnel && ticks++ < 60 * 300)
            {
                energyAtFire = state.Energy;
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
        pod.Evaluate(pod.FullMask & ~(1UL << drive), out var flight, out var stats);
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
    public void HandlesGoStaleWhenTheirHullIsDestroyedAndSlotsAreReused()
    {
        var pod = GameContent.Load().Pod;
        using var store = new HullStore(2);
        var a = store.Create(1, 0, pod, new Double3(1, 0, 0));
        var b = store.Create(2, 0, pod, new Double3(2, 0, 0));
        var c = store.Create(3, 0, pod, new Double3(3, 0, 0));
        Assert.Equal(3, store.Count);
        Assert.Equal(2.0, store.State(b.Index).Position.X);
        Assert.Equal(pod.FullMask, store.Info(b.Index).Blocks);
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
    public void ThePilotAndAFleetHullJumpToADistantSectorAndTheClientFollows()
    {
        using var session = GameSession.StartLocal(new ServerConfig { MaxPlayers = 1 }, Content, "Pilot");
        var pod = WaitForOwnHull(session);
        var galaxy = session.Client.AcquireSnapshot().Galaxy!;
        var far = new SectorCoord(64, 0, -20);

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
        using var session = GameSession.StartLocal(new ServerConfig { MaxPlayers = 1 }, Content, "Pilot");
        var pod = WaitForOwnHull(session);
        Assert.Equal(Content.Pod.FullMask, pod.Blocks);

        // Point the pod at the nearest asteroid and hold full boost. It rams the rock, bounces, and rams again.
        var content = new SectorContent();
        session.Client.AcquireSnapshot().Galaxy!.Generate(default, content);
        var nearest = content.Asteroids.Take(content.AsteroidCount).OrderBy(a => a.LocalPosition.Length()).First();
        var aim = Vector3.Normalize(nearest.LocalPosition - pod.Position.RelativeTo(Double3.Zero));
        session.Client.Input.Submit(new PlayerControls { Forward = 1, Boost = true, Aim = aim, Align = true });

        // First it loses blocks, and the client is told and flies on what is left.
        var damaged = WaitFor(session, hull => hull.Blocks != Content.Pod.FullMask || hull.Design == Content.DroneDesign, 60);
        if (damaged.Design != Content.DroneDesign)
        {
            Assert.Equal(pod.Id, damaged.Id);
            Assert.True(session.Client.AcquireSnapshot().Flight.Mass < Content.Pod.Flight.Mass);
        }

        // Keep steering back at the rock. In the end the core goes, the pod is gone, and the player is in a
        // drone where it died.
        var deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 100;
        var drone = OwnHull(session);
        while (drone.Design != Content.DroneDesign && Stopwatch.GetTimestamp() < deadline)
        {
            var toRock = nearest.LocalPosition - drone.Position.RelativeTo(Double3.Zero);
            session.Client.Input.Submit(new PlayerControls { Forward = 1, Boost = true, Aim = Vector3.Normalize(toRock), Align = true });
            Thread.Sleep(100);
            drone = OwnHull(session);
        }

        session.Client.Input.Submit(default);
        Assert.Equal(Content.DroneDesign, drone.Design);
        Assert.NotEqual(pod.Id, drone.Id);
        Assert.Equal(Content.Designs[Content.DroneDesign].FullMask, drone.Blocks);
        var snapshot = session.Client.AcquireSnapshot();
        Assert.DoesNotContain(snapshot.Hulls.Take(snapshot.HullCount), h => h.Id == pod.Id && !h.IsOwn && h.Design == Content.PodDesign);
        Assert.Null(session.Fault);
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
    public void AnAsteroidHitOnTheServerIsReportedToClientsIncludingLateJoiners()
    {
        var port = (ushort)(49000 + Environment.ProcessId % 2000);
        using var server = new ServerHost(new ServerConfig { MaxPlayers = 2 }, Content, Prospect.Engine.Net.RakNetTransport.Listen(port, 8));
        server.Start();
        using var pilot = GameSession.Join("127.0.0.1", port, Content, "Pilot");
        WaitForOwnHull(pilot);

        // Send the pod straight at the nearest asteroid's centre; it cannot get there without hitting it.
        var content = new SectorContent();
        server.Galaxy.Generate(default, content);
        var nearest = content.Asteroids.Take(content.AsteroidCount).OrderBy(a => a.LocalPosition.Length()).First();
        pilot.Client.Input.IssueOrder(new HullOrder { Kind = OrderKind.MoveTo, Target = server.Galaxy.CenterOf(default) + nearest.LocalPosition });

        var deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 60;
        while (pilot.Client.AcquireSnapshot().RockCount == 0 && Stopwatch.GetTimestamp() < deadline)
            Thread.Sleep(50);
        var seen = pilot.Client.AcquireSnapshot();
        Assert.True(seen.RockCount > 0, "the client was never told the asteroid moved");
        Assert.Equal(default, seen.Rocks[0].Rock.Sector);

        // Someone joining afterwards learns about the displaced asteroid too.
        using var late = GameSession.Join("127.0.0.1", port, Content, "Late");
        WaitForOwnHull(late);
        deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 10;
        while (late.Client.AcquireSnapshot().RockCount == 0 && Stopwatch.GetTimestamp() < deadline)
            Thread.Sleep(50);
        Assert.True(late.Client.AcquireSnapshot().RockCount > 0);
        Assert.Null(server.Fault);
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
