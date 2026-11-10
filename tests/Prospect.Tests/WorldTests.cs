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
        var start = HullState.At(Double3.Zero);

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
        var moving = Fly(HullState.At(Double3.Zero), new FlightInput { Forward = 127 }, 120);
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
        var plain = Fly(HullState.At(Double3.Zero), new FlightInput { Forward = 127 }, 120);
        var boosted = Fly(HullState.At(Double3.Zero), new FlightInput { Forward = 127, Boost = true }, 120);
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
        var state = HullState.At(Double3.Zero);
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

public sealed class HullStoreTests
{
    [Fact]
    public void HandlesGoStaleWhenTheirHullIsDestroyedAndSlotsAreReused()
    {
        using var store = new HullStore(2);
        var a = store.Create(1, HullState.At(new Double3(1, 0, 0)));
        var b = store.Create(2, HullState.At(new Double3(2, 0, 0)));
        var c = store.Create(3, HullState.At(new Double3(3, 0, 0)));
        Assert.Equal(3, store.Count);
        Assert.Equal(2.0, store.State(b.Index).Position.X);

        store.Destroy(b);
        Assert.False(store.IsAlive(b));
        Assert.True(store.IsAlive(a) && store.IsAlive(c));

        var d = store.Create(4, HullState.At(new Double3(4, 0, 0)));
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

        session.Client.Input.Submit(new PlayerControls { Forward = 1, Strafe = 0.5f, Yaw = 0.4f, Boost = true });
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

    private static HullView FindOther(GameSession session)
    {
        var snapshot = session.Client.AcquireSnapshot();
        for (var i = 0; i < snapshot.HullCount; i++)
        {
            if (!snapshot.Hulls[i].IsOwn)
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
