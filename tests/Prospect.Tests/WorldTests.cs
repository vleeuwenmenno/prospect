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
