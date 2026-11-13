using System.Reflection;
using Brutal.GlfwApi;
using Brutal.Numerics;
using Prospect.Client.Input;
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
/// Taking direct control reaches only hulls in the sector the player flies in, on every axis. The rule is one
/// function on <see cref="Galaxy"/>; the server enforces it on its own positions and the client asks it of the
/// snapshot for honest feedback. A real server and client run by hand, one tick at a time.
/// </summary>
public sealed class ControlRangeTests : IDisposable
{
    private static readonly float2 Screen = new(1280, 720);
    private readonly LoopbackLink _link = new();
    private readonly ClientSession _client;
    private readonly ServerHost _server;
    private readonly JobSystem _jobs = new(1, "TestJob");
    private readonly InputDevices _devices = new();
    private readonly WorldView _world;
    private readonly Galaxy _galaxy;
    private readonly HullStore _hulls;
    private long _tick;

    public ControlRangeTests()
    {
        var content = GameContent.Load();
        _client = new ClientSession(_link.Client, content, "Pilot");
        _server = new ServerHost(new ServerConfig { MaxPlayers = 1 }, content, [_link.Server]);
        var actions = ActionMap.Load(Path.Combine(AppContext.BaseDirectory, "content", "input", "bindings.json"));
        _world = new WorldView(_devices, actions, new ClientSettings(), _jobs, null!, content.Designs);
        _hulls = (HullStore)typeof(ServerHost).GetField("_hulls", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_server)!;
        Run(10);
        _galaxy = _client.AcquireSnapshot().Galaxy!;
        Assert.True(_client.AcquireSnapshot().OwnHull >= 0);
    }

    public void Dispose()
    {
        _jobs.CompleteAll();
        _jobs.Dispose();
        _client.Dispose();
        _server.Dispose();
        _link.Dispose();
    }

    private SessionSnapshot Run(int ticks)
    {
        SessionSnapshot snapshot = null!;
        for (var i = 0; i < ticks; i++)
        {
            ((ITickHandler)_server).Tick(_tick);
            ((ITickHandler)_client).Tick(_tick++);
            snapshot = _client.AcquireSnapshot();
            snapshot.PublishedAt = 0;
            _devices.BeginFrame();
            if (snapshot.Galaxy is not null && snapshot.OwnHull >= 0)
                _world.Update(snapshot, _client.Input, 1f / TickLoop.SimulationHz, false, Screen, Screen, false);
        }

        return snapshot;
    }

    private static HullHandle HandleOf(uint id) => new((int)(id & 0xFFFF), (int)(id >> 16));

    private HullView Own(SessionSnapshot s) => s.Hulls[s.OwnHull];

    private uint FleetHull(SessionSnapshot s)
    {
        for (var i = 0; i < s.HullCount; i++)
        {
            if (s.Hulls[i].Owner == s.PlayerId && !s.Hulls[i].IsOwn)
                return s.Hulls[i].Id;
        }

        throw new InvalidOperationException("no fleet hull");
    }

    private ref HullState ServerState(uint id) => ref _hulls.State(HandleOf(id).Index);

    private void Place(uint id, Double3 position) => ServerState(id).Position = position;

    private double Size => _galaxy.SectorSize;

    [Fact]
    public void TheRangeIsTheSameSectorOnEveryAxisIncludingNegativeCoordinatesAndLayers()
    {
        var s = Size;
        var home = new Double3(0, 0, 0);
        // A sector spans half a size either side of its centre; the edge belongs to the sector above it.
        Assert.True(_galaxy.InSameSector(home, new Double3(0.5 * s - 1, -0.5 * s, 0.5 * s - 1)));
        Assert.False(_galaxy.InSameSector(home, new Double3(0.5 * s, 0, 0)));
        Assert.False(_galaxy.InSameSector(home, new Double3(-0.5 * s - 1, 0, 0)));
        Assert.False(_galaxy.InSameSector(home, new Double3(0, 0.5 * s, 0)));
        Assert.False(_galaxy.InSameSector(home, new Double3(0, 0, -0.5 * s - 1)));

        // Negative sectors: the two sides of a boundary are neighbours even a metre apart, and the same sector is
        // the same far from the boundary.
        var edge = -1.5 * s;
        Assert.False(_galaxy.InSameSector(new Double3(edge - 1, 0, 0), new Double3(edge + 1, 0, 0)));
        Assert.True(_galaxy.InSameSector(new Double3(-s - 0.4 * s, -s, -s), new Double3(-s + 0.4 * s, -s - 0.4 * s, -s + 0.4 * s)));
        Assert.False(_galaxy.InSameSector(new Double3(0, 0.5 * s - 1, 0), new Double3(0, 0.5 * s + 1, 0)));
        Assert.False(_galaxy.InSameSector(new Double3(0, -0.5 * s - 1, 0), new Double3(0, -0.5 * s + 1, 0)));
    }

    [Fact]
    public void TheServerTakesAFarHullInTheSameSectorAndRefusesANearHullInTheNextOne()
    {
        var s = Size;
        var snapshot = Run(2);
        var pod = Own(snapshot).Id;
        var fleet = FleetHull(snapshot);
        var center = _galaxy.CenterOf(_galaxy.SectorOf(Own(snapshot).Position));
        var podAt = ServerState(pod).Position;

        // Same sector, nearly a whole sector apart on three axes: accepted.
        Place(fleet, center + new Double3(0.45 * s, -0.45 * s, 0.45 * s));
        _client.Input.RequestControl(fleet);
        snapshot = Run(3);
        Assert.Equal(fleet, Own(snapshot).Id);
        Assert.True(_hulls.IsAlive(HandleOf(pod)));
        Assert.False(_hulls.Info(HandleOf(pod).Index).Piloted);
        Assert.True(_hulls.Info(HandleOf(fleet).Index).Piloted);

        // Back to the pod, then pod and fleet hull a few metres apart across a sector boundary: refused, and nothing
        // about either hull changes.
        _client.Input.RequestControl(pod);
        snapshot = Run(3);
        Assert.Equal(pod, Own(snapshot).Id);
        Place(pod, center + new Double3(0.5 * s - 1, 0, 0));
        Place(fleet, center + new Double3(0.5 * s + 1, 0, 0));
        _client.Input.RequestControl(fleet);
        snapshot = Run(3);
        Assert.Equal(pod, Own(snapshot).Id);
        Assert.True(_hulls.Info(HandleOf(pod).Index).Piloted);
        Assert.False(_hulls.Info(HandleOf(fleet).Index).Piloted);
        Assert.True(_hulls.IsAlive(HandleOf(fleet)));

        // Travelling into that sector makes the same request work.
        Place(pod, center + new Double3(0.5 * s + 20, 0, 0));
        _client.Input.RequestControl(fleet);
        snapshot = Run(3);
        Assert.Equal(fleet, Own(snapshot).Id);
        Assert.False(_hulls.Info(HandleOf(pod).Index).Piloted);
        Assert.Equal(podAt.Y, ServerState(pod).Position.Y);
    }

    [Fact]
    public void TheServerRefusesAnotherLayerAndKeepsTheEscapeDrone()
    {
        var s = Size;
        var snapshot = Run(2);
        var pod = Own(snapshot).Id;
        var fleet = FleetHull(snapshot);
        var center = _galaxy.CenterOf(_galaxy.SectorOf(Own(snapshot).Position));

        var owned = snapshot.Hulls.Take(snapshot.HullCount).Count(h => h.Owner == snapshot.PlayerId);

        // Stepping out into a drone is not limited by range.
        Place(fleet, center + new Double3(0, s, 0));
        _client.Input.RequestControl(0);
        snapshot = Run(3);
        var drone = Own(snapshot).Id;
        Assert.NotEqual(pod, drone);
        Assert.Equal(owned + 1, snapshot.Hulls.Take(snapshot.HullCount).Count(h => h.Owner == snapshot.PlayerId));

        // A hull one layer up is refused; the drone stays, is still flown, and is not removed.
        _client.Input.RequestControl(fleet);
        snapshot = Run(3);
        Assert.Equal(drone, Own(snapshot).Id);
        Assert.True(_hulls.IsAlive(HandleOf(drone)));
        Assert.True(_hulls.Info(HandleOf(drone).Index).Piloted);

        // A stale handle and a drone asking for itself change nothing.
        _client.Input.RequestControl(0xFFFF_FF00u);
        _client.Input.RequestControl(drone);
        snapshot = Run(3);
        Assert.Equal(drone, Own(snapshot).Id);

        // Asking to eject again from the drone does nothing; taking a hull in the drone's sector replaces it.
        _client.Input.RequestControl(0);
        snapshot = Run(3);
        Assert.Equal(drone, Own(snapshot).Id);
        Place(fleet, center + new Double3(0.3 * s, 0, 0));
        _client.Input.RequestControl(fleet);
        snapshot = Run(3);
        Assert.Equal(fleet, Own(snapshot).Id);
        Assert.False(_hulls.IsAlive(HandleOf(drone)));
    }

    [Fact]
    public void TheClientAsksTheSameRuleOfTheSnapshotAndTheLabelMovesOnlyWithTheConfirmedSnapshot()
    {
        var s = Size;
        var snapshot = Run(2);
        var pod = Own(snapshot).Id;
        var fleet = FleetHull(snapshot);
        var center = _galaxy.CenterOf(_galaxy.SectorOf(Own(snapshot).Position));

        // Out of the sector: the snapshot shows it, the view will not ask, and says why.
        Place(fleet, center + new Double3(0, 0, -0.5 * s - 50));
        snapshot = Run(40);
        var target = snapshot.Hulls.Take(snapshot.HullCount).Single(h => h.Id == fleet);
        Assert.False(_world.CanTakeControlOf(target));
        _world.Select(fleet, false);
        Assert.False(Press(snapshot, false));
        Assert.True(_world.ControlOutOfSector);
        Assert.False(_world.ControlDenied);
        Assert.Equal(pod, Own(Run(3)).Id);
        Assert.Equal(1, _world.Selection.Length);

        // Back inside: the request goes out, the label is still the pod's until the server's snapshot says otherwise.
        Place(fleet, center + new Double3(-0.4 * s, 0.3 * s, 0.4 * s));
        snapshot = Run(40);
        Assert.True(_world.CanTakeControlOf(snapshot.Hulls.Take(snapshot.HullCount).Single(h => h.Id == fleet)));
        Assert.True(Press(snapshot, true));
        Assert.True(snapshot.Hulls.Take(snapshot.HullCount).Single(h => h.Id == pod).IsOwn);
        snapshot = Run(3);
        Assert.Equal(fleet, Own(snapshot).Id);
        Assert.True(snapshot.Hulls.Take(snapshot.HullCount).Single(h => h.Id == fleet).IsOwn);
        Assert.Equal(fleet, snapshot.Hulls.Take(snapshot.HullCount).Single(h => h.IsOwn).Id);
        Assert.Equal(fleet, _world.Selection[0]);
    }

    private bool Press(SessionSnapshot snapshot, bool inRange)
    {
        _devices.BeginFrame();
        _devices.OnKey(GlfwKey.T, GlfwKeyAction.Press);
        _world.Update(snapshot, _client.Input, 0f, false, Screen, Screen, false);
        var sent = _world.SwitchToSelectedFromMap(_client.Input, true);
        Assert.Equal(inRange, sent);
        _devices.BeginFrame();
        _devices.OnKey(GlfwKey.T, GlfwKeyAction.Release);
        return sent;
    }
}
