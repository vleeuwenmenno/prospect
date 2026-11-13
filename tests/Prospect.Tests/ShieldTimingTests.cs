using System.Numerics;
using Brutal.Numerics;
using Prospect.Client.Input;
using Prospect.Client.Render;
using Prospect.Client.Settings;
using Prospect.Client.World;
using Prospect.Engine.Jobs;
using Prospect.Engine.Net;
using Prospect.Engine.Time;
using Prospect.Game.Client;
using Prospect.Game.Server;
using Prospect.Game.World;
using Xunit;

namespace Prospect.Tests;

/// <summary>
/// The clocks the shield effects run on, as the game wires them: what a real session publishes for the pilot's
/// hull and for a hull it only watches, and the real <see cref="WorldView"/> following publications it is handed
/// the way the application hands them over, some drawn once, some again and again, some after a stall.
/// </summary>
public sealed class ShieldTimingTests : IDisposable
{
    private const float Dt = 1f / TickLoop.SimulationHz;
    private static readonly float2 Screen = new(1280, 720);
    private static readonly ShieldLook Look = ShieldLook.Load();

    private readonly GameContent _content = GameContent.Load();
    private readonly LoopbackLink _link = new();
    private readonly LoopbackLink _observerLink = new();
    private readonly ClientSession _client;
    private readonly ClientSession _observer;
    private readonly ServerHost _server;
    private readonly JobSystem _jobs = new(1, "TestJob");
    private readonly InputDevices _devices = new();
    private readonly WorldView _world;
    private readonly ShieldDraw[] _draws = new ShieldDraw[ShieldVisuals.MaxShields];
    private readonly ShieldInstance[] _instances = new ShieldInstance[ShieldVisuals.MaxShields];
    private readonly MeshVertex[] _vertices = new MeshVertex[ShieldVisuals.MaxVertices];
    private readonly ushort[] _indices = new ushort[ShieldVisuals.MaxIndices];
    private long _tick;

    public ShieldTimingTests()
    {
        _client = new ClientSession(_link.Client, _content, "Pilot");
        _observer = new ClientSession(_observerLink.Client, _content, "Observer");
        _server = new ServerHost(new ServerConfig { MaxPlayers = 2 }, _content, _link.Server, _observerLink.Server);
        var actions = ActionMap.Load(Path.Combine(AppContext.BaseDirectory, "content", "input", "bindings.json"));
        _world = new WorldView(_devices, actions, new ClientSettings(), _jobs, null!, _content.Designs);
        for (var i = 0; i < 20; i++)
            Step();
        Assert.True(_client.AcquireSnapshot().OwnHull >= 0);
    }

    public void Dispose()
    {
        _jobs.CompleteAll();
        _jobs.Dispose();
        _client.Dispose();
        _observer.Dispose();
        _server.Dispose();
        _link.Dispose();
        _observerLink.Dispose();
    }

    private void Step()
    {
        ((ITickHandler)_server).Tick(_tick);
        ((ITickHandler)_client).Tick(_tick);
        ((ITickHandler)_observer).Tick(_tick++);
    }

    private static HullView Find(SessionSnapshot snapshot, uint id)
    {
        for (var i = 0; i < snapshot.HullCount; i++)
        {
            if (snapshot.Hulls[i].Id == id)
                return snapshot.Hulls[i];
        }

        Assert.Fail($"hull {id} is not in view");
        return default;
    }

    [Fact]
    public void ASessionNamesEveryPublicationAndSaysWhatMomentEachHullIsShownAt()
    {
        var pilot = _client.AcquireSnapshot();
        var id = pilot.Hulls[pilot.OwnHull].Id;
        var published = pilot.PublishedAt;
        var ownShown = pilot.Hulls[pilot.OwnHull].ShownTick;
        var watched = _observer.AcquireSnapshot();
        var watchedAt = watched.PublishedAt;
        var remoteShown = Find(watched, id).ShownTick;

        for (var i = 0; i < 90; i++)
        {
            Step();

            // Every tick is a publication of its own, and the pilot's hull is one predicted input further on.
            pilot = _client.AcquireSnapshot();
            Assert.True(pilot.PublishedAt > published);
            published = pilot.PublishedAt;
            var own = pilot.Hulls[pilot.OwnHull];
            Assert.True(own.IsOwn);
            Assert.Equal(ownShown + 1, own.ShownTick);
            ownShown = own.ShownTick;

            // The same hull, watched: shown at the delayed server tick, which moves with the observer's estimate
            // of the server's clock and not one tick a publication. It is not the pilot's count of inputs.
            watched = _observer.AcquireSnapshot();
            Assert.True(watched.PublishedAt > watchedAt);
            watchedAt = watched.PublishedAt;
            var seen = Find(watched, id);
            Assert.False(seen.IsOwn);
            Assert.Equal(watched.ServerTickPrecise - 6, seen.ShownTick, 9);
            Assert.True(seen.ShownTick != own.ShownTick);
            remoteShown = seen.ShownTick;
        }

        // Drawing a publication again shows the same moment under the same name.
        var again = _observer.AcquireSnapshot();
        Assert.Equal(watchedAt, again.PublishedAt);
        Assert.Equal(remoteShown, Find(again, id).ShownTick);
    }

    /// <summary>
    /// One client frame of <paramref name="dt"/> seconds through the real view, which is handed the publication
    /// and names it to the shields itself. Returns the pilot's own shield as drawn, if it is.
    /// </summary>
    private ShieldInstance? Frame(SessionSnapshot snapshot, float dt)
    {
        _devices.BeginFrame();
        _world.Update(snapshot, _client.Input, dt, false, Screen, Screen, false);
        var shields = _world.Shields;
        shields.Begin(_draws);
        shields.Add(snapshot.Hulls[snapshot.OwnHull].Id, Vector3.Zero, Quaternion.Identity, 1f, 2f, _instances, _vertices, _indices);
        return shields.Draws.Length > 0 ? _instances[0] : null;
    }

    /// <summary>
    /// Makes the session's latest publication a new one that shows the pilot's hull
    /// <paramref name="ticks"/> further on, with its shield as given. The session publishes on the wall clock; the
    /// test says what each publication holds instead.
    /// </summary>
    private SessionSnapshot Publish(double ticks, bool offline, float changeAge, ShieldHit hit = default, float hitAge = 0f)
    {
        var snapshot = _client.AcquireSnapshot();
        snapshot.PublishedAt += 1000;
        ref var own = ref snapshot.Hulls[snapshot.OwnHull];
        own.ShownTick += ticks;
        own.ShieldOffline = offline;
        own.ShieldChangeAge = changeAge;
        own.ShieldHit = hit;
        own.ShieldHitAge = hitAge;
        return snapshot;
    }

    [Fact]
    public void TheViewTellsANewPublicationFromOneDrawnAgainWhateverTheirAgesRead()
    {
        Assert.Equal(1f, Frame(Publish(1, false, 2f), Dt)!.Value.Params.X);

        // A break 0.1 s old, drawn by four frames: the first takes the publication's age, the rest move on.
        var broken = Publish(1, true, 0.1f);
        Assert.Equal(0.1f / Look.CollapseSeconds, Frame(broken, Dt)!.Value.Params.Z, 3);
        var drawn = default(ShieldInstance);
        for (var i = 0; i < 3; i++)
            drawn = Frame(broken, Dt)!.Value;
        Assert.Equal((0.1f + 3 * Dt) / Look.CollapseSeconds, drawn.Params.Z, 3);

        // A new publication that reads the same age sets it all the same.
        Assert.Equal(0.1f / Look.CollapseSeconds, Frame(Publish(1, true, 0.1f), Dt)!.Value.Params.Z, 3);

        // The age crosses the collapse's end and is corrected back across it.
        Assert.Equal(0.69f / Look.CollapseSeconds, Frame(Publish(1, true, 0.69f), Dt)!.Value.Params.Z, 3);
        Assert.Null(Frame(Publish(1, true, 0.71f), Dt));
        Assert.Equal(0.68f / Look.CollapseSeconds, Frame(Publish(1, true, 0.68f), Dt)!.Value.Params.Z, 3);
        Assert.Null(Frame(Publish(1, true, 2f), Dt));

        // A restart 0.1 s old is drawn on to 0.3 s by frames alone; then a break, published at the age the
        // restart was published at, starts at that age.
        var restart = Publish(1, false, 0.1f);
        Assert.Equal(0.1f / Look.FadeInSeconds, Frame(restart, Dt)!.Value.Params.X, 3);
        for (var i = 0; i < 12; i++)
            drawn = Frame(restart, Dt)!.Value;
        Assert.Equal(0.3f / Look.FadeInSeconds, drawn.Params.X, 3);
        drawn = Frame(Publish(1, true, 0.1f), Dt)!.Value;
        Assert.Equal(0.1f / Look.CollapseSeconds, drawn.Params.Z, 3);
        Assert.Equal(0f, drawn.Params.X);
    }

    [Fact]
    public void TheViewEndsAnOlderRippleAfterAStallThatBringsADifferentHit()
    {
        Frame(Publish(1, false, 2f), Dt);
        var first = new ShieldHit { Serial = 1, Strength = 200, X = 50 };
        var drawn = Frame(Publish(1, false, 2f, first, 0.2f), Dt)!.Value;
        Assert.Equal(1, Ripples(drawn, out var age));
        Assert.Equal(0.2f, age, 3);

        // Two seconds pass without a frame; the application caps the frame's own length at a tenth of one. The
        // publication drawn next holds another hit, 0.05 s old: the first one's ripple is long over.
        var second = new ShieldHit { Serial = 2, Strength = 200, X = -50 };
        drawn = Frame(Publish(2 * TickLoop.SimulationHz, false, 2f, second, 0.05f), 0.1f)!.Value;
        Assert.Equal(1, Ripples(drawn, out age));
        Assert.Equal(0.05f, age, 3);
    }

    /// <summary>How many ripples are spreading, and the age of the youngest.</summary>
    private static int Ripples(in ShieldInstance drawn, out float youngest)
    {
        Span<float> ages = [drawn.Ripple0.W, drawn.Ripple1.W, drawn.Ripple2.W, drawn.Ripple3.W];
        Span<float> strengths = [drawn.Strengths.X, drawn.Strengths.Y, drawn.Strengths.Z, drawn.Strengths.W];
        var count = 0;
        youngest = float.MaxValue;
        for (var i = 0; i < ages.Length; i++)
        {
            if (strengths[i] <= 0f)
                continue;
            count++;
            youngest = Math.Min(youngest, ages[i]);
        }

        return count;
    }
}
