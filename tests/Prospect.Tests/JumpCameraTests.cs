using System.Numerics;
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
/// The pilot's camera through their own jump: a real server and client joined in process, the real
/// <see cref="WorldView"/> updated every frame from their snapshots, and the arrow keys turning the view as a pilot
/// would. A jump must never take the view away from the pilot, and looking around must not change the jump.
/// </summary>
public sealed class JumpCameraTests : IDisposable
{
    private const float Dt = 1f / TickLoop.SimulationHz;
    private static readonly float2 Screen = new(1280, 720);

    private readonly LoopbackLink _link = new();
    private readonly ClientSession _client;
    private readonly ServerHost _server;
    private readonly JobSystem _jobs = new(1, "TestJob");
    private readonly InputDevices _devices = new();
    private readonly WorldView _world;
    private readonly ActionMap _actions;
    private long _tick;

    public JumpCameraTests()
    {
        var content = GameContent.Load();
        _client = new ClientSession(_link.Client, content, "Pilot");
        _server = new ServerHost(new ServerConfig { MaxPlayers = 1 }, content, [_link.Server]);
        var actions = ActionMap.Load(Path.Combine(AppContext.BaseDirectory, "content", "input", "bindings.json"));
        _actions = actions;
        _world = new WorldView(_devices, actions, new ClientSettings(), _jobs, null!, content.Designs);
        for (var i = 0; i < 10; i++)
            Frame(false);
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

    /// <summary>One simulation tick on both ends and one client frame, the right arrow held or not.</summary>
    private SessionSnapshot Frame(bool turnRight, bool boost = false)
    {
        ((ITickHandler)_server).Tick(_tick);
        ((ITickHandler)_client).Tick(_tick++);
        var snapshot = _client.AcquireSnapshot();
        _devices.BeginFrame();
        _devices.OnKey(GlfwKey.Right, turnRight ? GlfwKeyAction.Press : GlfwKeyAction.Release);
        // Boost through whatever the bindings file binds it to, as a pilot would press it.
        var boostKey = (GlfwKey)_actions.Bindings(InputAction.Boost)[0].Code;
        if (boost || _devices.KeyDown(boostKey))
            _devices.OnKey(boostKey, boost ? GlfwKeyAction.Press : GlfwKeyAction.Release);
        if (snapshot.Galaxy is not null && snapshot.OwnHull >= 0)
            _world.Update(snapshot, _client.Input, Dt, true, Screen, Screen, false);
        return snapshot;
    }

    /// <summary>Where the camera looks, in world space.</summary>
    private Vector3 ViewForward()
    {
        var camera = _world.CameraPosition;
        return new Vector3(
            _world.ToView(camera + new Double3(1, 0, 0)).Z,
            _world.ToView(camera + new Double3(0, 1, 0)).Z,
            _world.ToView(camera + new Double3(0, 0, 1)).Z
        );
    }

    private static float Angle(Vector3 a, Vector3 b) => MathF.Acos(Math.Clamp(Vector3.Dot(Vector3.Normalize(a), Vector3.Normalize(b)), -1f, 1f));

    // Twenty frames turning, then forty still; the view is checked once it has caught up with the last turn.
    private const int Turn = 20;
    private const int Period = 60;
    private const int Settled = 40;

    [Theory]
    [InlineData(FtlKind.None)]
    [InlineData(FtlKind.Wormhole)]
    [InlineData(FtlKind.Rift)]
    public void TheViewTurnsOnlyWithThePilotThroughEveryStageOfAJump(FtlKind drawnAs)
    {
        _world.FtlPreview = drawnAs;
        var target = _client.AcquireSnapshot().Galaxy!.CenterOf(new SectorCoord(12, 0, -7));
        _client.Input.IssueOrder(new HullOrder { Kind = OrderKind.Jump, Target = target });

        var turnsChecked = new HashSet<JumpPhase>();
        var stillChecked = new HashSet<JumpPhase>();
        var started = false;
        var turnStart = Vector3.Zero;
        var previous = ViewForward();
        var orbit = _world.ToView(_world.OwnDrawnPosition).Z;
        for (var frame = 0; frame < TickLoop.SimulationHz * 60; frame++)
        {
            var into = frame % Period;
            var snapshot = Frame(into < Turn);
            ref readonly var own = ref snapshot.Hulls[snapshot.OwnHull];
            var forward = ViewForward();
            started |= own.Jump != JumpPhase.None;
            if (started && own.Jump == JumpPhase.None)
            {
                // Looking around all the way through left the jump where the server put it.
                Assert.True(own.Position.RelativeTo(target).Length() < 1f, $"arrived {own.Position}, ordered {target}");
                break;
            }

            // The camera stays on the pilot's own orbit around the hull, wherever the jump carries it: the hull in the
            // middle of the view, as far ahead as before the jump.
            var hull = _world.ToView(_world.OwnDrawnPosition);
            Assert.True(MathF.Abs(hull.X) < 0.01f && MathF.Abs(hull.Z - orbit) < orbit * 0.02f, $"{own.Jump}: hull at {hull} in view, orbit {orbit}");

            if (own.Jump == JumpPhase.Charge)
            {
                // The server holds the nose on the destination while the drive works, wherever the pilot looks.
                var nose = Vector3.Transform(-Vector3.UnitZ, own.Orientation);
                Assert.True(Vector3.Dot(nose, Vector3.Normalize(target.RelativeTo(own.Position))) > 0.99f);
            }

            if (into == 0)
                turnStart = forward;
            else if (into == Settled && started)
            {
                // The whole turn the pilot asked for, not pulled back toward the nose.
                var turned = Angle(turnStart, forward);
                var asked = Turn * Dt * WorldView.LookRadiansPerSecond;
                Assert.True(turned > asked * 0.9f && turned < asked * 1.05f, $"{own.Jump}: turned {turned}, asked {asked}");
                turnsChecked.Add(own.Jump);
            }
            else if (into > Settled && started)
            {
                // Without input the view stays where the pilot left it.
                Assert.True(Angle(previous, forward) < 2e-3f, $"{own.Jump}: drifted {Angle(previous, forward)} with no input");
                stillChecked.Add(own.Jump);
            }

            previous = forward;
        }

        Assert.True(started);
        Assert.Contains(JumpPhase.Charge, turnsChecked);
        Assert.Contains(JumpPhase.Tunnel, turnsChecked);
        Assert.Contains(JumpPhase.Arrive, stillChecked);
        Assert.Contains(JumpPhase.Tunnel, stillChecked);
    }

    [Fact]
    public void TheViewTurnsWithThePilotWhileACancelledJumpEasesAway()
    {
        _world.FtlPreview = FtlKind.Rift;
        var target = _client.AcquireSnapshot().Galaxy!.CenterOf(new SectorCoord(12, 0, -7));
        _client.Input.IssueOrder(new HullOrder { Kind = OrderKind.Jump, Target = target });
        for (var frame = 0; frame < TickLoop.SimulationHz * 30; frame++)
        {
            var snapshot = Frame(false);
            if (snapshot.Hulls[snapshot.OwnHull].Jump == JumpPhase.Charge && WorldView.JumpProgress(snapshot.Hulls[snapshot.OwnHull], _world.OwnFlight) > 0.8f)
                break;
        }

        _client.Input.IssueOrder(default);
        var start = ViewForward();
        for (var frame = 0; frame < Turn; frame++)
            Frame(true);
        Assert.Equal(JumpPhase.None, _client.AcquireSnapshot().Hulls[_client.AcquireSnapshot().OwnHull].Jump);
        for (var frame = 0; frame < Settled - Turn; frame++)
            Frame(false);
        var asked = Turn * Dt * WorldView.LookRadiansPerSecond;
        var turned = Angle(start, ViewForward());
        Assert.True(turned > asked * 0.9f && turned < asked * 1.05f, $"turned {turned}, asked {asked}");
    }

    [Theory]
    [InlineData(FtlKind.Rift, 0f)]
    [InlineData(FtlKind.Rift, 90f)]
    [InlineData(FtlKind.Rift, 180f)]
    [InlineData(FtlKind.Wormhole, 0f)]
    [InlineData(FtlKind.Wormhole, 90f)]
    [InlineData(FtlKind.Wormhole, 180f)]
    public void TheCameraIsThroughEachOpeningBeforeItCountsWhereverItLooks(FtlKind drawnAs, float lookDegrees)
    {
        // Looking ahead the camera passes through the openings; looking aside or back it passes beside them or
        // reaches them first. Either way it must be in the throat before the drive fires, and out of it before the
        // jump is over, or the view would cut when the stage changes.
        _world.FtlPreview = drawnAs;
        _world.LookDegrees = lookDegrees;
        var target = _client.AcquireSnapshot().Galaxy!.CenterOf(new SectorCoord(12, 0, -7));
        _client.Input.IssueOrder(new HullOrder { Kind = OrderKind.Jump, Target = target });

        var started = false;
        var previous = JumpPhase.None;
        var beforeFiring = -1f;
        var throughAtCharge = 1f;
        for (var frame = 0; frame < TickLoop.SimulationHz * 60; frame++)
        {
            var snapshot = Frame(false);
            var phase = snapshot.Hulls[snapshot.OwnHull].Jump;
            started |= phase != JumpPhase.None;
            if (phase == JumpPhase.Charge && WorldView.JumpProgress(snapshot.Hulls[snapshot.OwnHull], _world.OwnFlight) < 0.3f)
                throughAtCharge = Math.Min(throughAtCharge, _world.JumpThrough);
            if (previous == JumpPhase.Charge && phase == JumpPhase.Tunnel)
                Assert.True(beforeFiring > 0.95f, $"{beforeFiring} of the way into the throat when the drive fired");
            if (started && phase == JumpPhase.None)
            {
                Assert.True(_world.JumpThrough > 0.95f, $"{_world.JumpThrough} of the way out of the throat at the end");
                break;
            }

            beforeFiring = _world.JumpThrough;
            previous = phase;
        }

        Assert.True(started && previous == JumpPhase.Arrive);
        // Early in the charge the camera is still on the near side of the opening, whichever way it looks.
        Assert.True(throughAtCharge < 0.05f, $"{throughAtCharge} of the way into the throat early in the charge");
    }

    /// <summary>A point in another sector straight along the nose of the hull being flown, or off to its side.</summary>
    private Double3 NavPoint(float degreesOff)
    {
        var nose = Vector3.Transform(-Vector3.UnitZ, Quaternion.CreateFromAxisAngle(Vector3.UnitY, degreesOff * MathF.PI / 180f) * _world.OwnOrientation);
        var galaxy = _client.AcquireSnapshot().Galaxy!;
        return _world.OwnPosition + nose * (float)(galaxy.SectorSize * 2.5);
    }

    [Fact]
    public void AJumpPlottedOnTheMapWaitsForThePilotToAimAndBoost()
    {
        // With nothing selected the map sends nothing anywhere.
        var target = NavPoint(40f);
        _world.ClearSelection();
        _world.OrderJump(_client.Input, target);
        Assert.Null(_world.NavTarget);

        // Plotting from the map gives the flown hull, selected, a course, not an order.
        _world.Select(_world.Own.Id, false);
        Assert.True(_world.JumpPlotsOwnOnly);
        _world.OrderJump(_client.Input, target);
        for (var frame = 0; frame < 30; frame++)
            Frame(false);
        var own = _client.AcquireSnapshot().Hulls[_client.AcquireSnapshot().OwnHull];
        Assert.Equal(target, _world.NavTarget);
        Assert.Equal(JumpPhase.None, own.Jump);
        Assert.NotEqual(OrderKind.Jump, own.Order.Kind);
        Assert.False(_world.NavAimed);

        // Boost with the nose off the target is refused, and the hull stays put.
        Frame(false, boost: true);
        Assert.True(_world.Time - _world.NavRefusedAt < 0.1f);
        for (var frame = 0; frame < 30; frame++)
            Frame(false);
        Assert.Equal(JumpPhase.None, _client.AcquireSnapshot().Hulls[_client.AcquireSnapshot().OwnHull].Jump);

        // With the nose on it, boost starts the jump, which runs to the plotted point and clears the course there.
        target = NavPoint(0.5f);
        _world.OrderJump(_client.Input, target);
        Frame(false);
        Assert.True(_world.NavAimed);
        Frame(false, boost: true);
        var started = false;
        for (var frame = 0; frame < TickLoop.SimulationHz * 60; frame++)
        {
            var snapshot = Frame(false);
            ref readonly var hull = ref snapshot.Hulls[snapshot.OwnHull];
            started |= hull.Jump != JumpPhase.None;
            if (started && hull.Jump == JumpPhase.None)
            {
                Assert.True(hull.Position.RelativeTo(target).Length() < 1f);
                break;
            }
        }

        Assert.True(started);
        Assert.Null(_world.NavTarget);
    }

    [Fact]
    public void AJumpOrderedForTheFleetStillGoesStraightToTheOtherShips()
    {
        _world.SelectAllOwned();
        var target = NavPoint(40f);
        _world.OrderJump(_client.Input, target);
        for (var frame = 0; frame < 30; frame++)
            Frame(false);
        var snapshot = _client.AcquireSnapshot();
        var others = 0;
        for (var i = 0; i < snapshot.HullCount; i++)
        {
            ref readonly var hull = ref snapshot.Hulls[i];
            if (hull.Owner != snapshot.PlayerId || hull.IsOwn)
                continue;
            others++;
            Assert.True(hull.Jump != JumpPhase.None || hull.Order.Kind == OrderKind.Jump, $"fleet hull {hull.Id} was not sent");
        }

        Assert.True(others > 0);
        Assert.NotNull(_world.NavTarget);
        Assert.Equal(JumpPhase.None, snapshot.Hulls[snapshot.OwnHull].Jump);
    }
    /// <summary>
    /// Whether <c>mesh.frag</c> keeps a point of a hull cut by a plane, all relative to the camera: on the shown side,
    /// or on the hidden side but seen through the plane's window from the shown side.
    /// </summary>
    private static bool Kept(Vector3 at, Vector3 point, Vector3 hidden, float window)
    {
        var side = Vector3.Dot(at - point, hidden);
        if (hidden == Vector3.Zero || side <= 0f)
            return true;
        var camera = -Vector3.Dot(point, hidden);
        return window > 0f && camera < 0f && (at * (camera / (camera - side)) - point).Length() < window;
    }

    [Fact]
    public void AHullsMaterialSaysWhatItIsAndWhichOpeningCutsIt()
    {
        // As mesh.vert reads it back: the kind below four, and one more than the clip slot in steps of four.
        for (var clip = -1; clip < HullVisuals.MaxPassages; clip++)
        {
            foreach (var own in (ReadOnlySpan<bool>)[false, true])
            {
                var material = WorldView.Material(own, clip);
                var slot = (int)((material + 0.5f) * 0.25f);
                Assert.Equal(clip, slot - 1);
                Assert.Equal(own ? 2f : 1f, material - 4f * slot);
            }
        }
    }

    [Theory]
    [InlineData(FtlKind.Rift, 0f)]
    [InlineData(FtlKind.Rift, 90f)]
    [InlineData(FtlKind.Rift, 180f)]
    [InlineData(FtlKind.Wormhole, 0f)]
    [InlineData(FtlKind.Wormhole, 90f)]
    [InlineData(FtlKind.Wormhole, 180f)]
    public void ThePilotsOwnHullGoesIntoItsOpeningNoseFirstWhereverTheCameraIs(FtlKind drawnAs, float lookDegrees)
    {
        // The pilot's own hull, drawn from inside its jump, against the opening the composite shows: from the world's
        // side, what is through the opening is hidden, nose first in and stern last out; from the throat's side, what
        // is still in the world shows only through the window. Looking from behind, beside and ahead of the hull.
        _world.FtlPreview = drawnAs;
        _world.LookDegrees = lookDegrees;
        var target = _client.AcquireSnapshot().Galaxy!.CenterOf(new SectorCoord(12, 0, -7));
        _client.Input.IssueOrder(new HullOrder { Kind = OrderKind.Jump, Target = target });

        var started = false;
        var entryCut = 0;
        var exitCut = 0;
        var throatSeen = 0;
        for (var frame = 0; frame < TickLoop.SimulationHz * 120; frame++)
        {
            var snapshot = Frame(false);
            ref readonly var own = ref snapshot.Hulls[snapshot.OwnHull];
            started |= own.Jump != JumpPhase.None;
            if (started && own.Jump == JumpPhase.None)
                break;
            if (own.Jump == JumpPhase.None)
                continue;

            var exit = own.Jump is JumpPhase.Tunnel or JumpPhase.Arrive;
            var (centre, axis, radius) = _world.OwnOpening;
            var (point, hidden, window) = _world.OwnCut;
            if (radius <= 0.001f * _world.OwnFlight.Radius)
                continue;

            // The cut is the opening's own plane, fixed in the world whichever way the camera looks.
            var throat = exit ? -axis : axis;
            var past = _world.JumpThrough >= 0.5f;
            var inThroat = exit ? !past : past;
            Assert.Equal(centre, point);
            Assert.Equal(inThroat ? -throat : throat, hidden);
            Assert.Equal(inThroat ? radius : 0f, window);

            // Where each block is drawn, relative to the camera, and which side of the opening it is on.
            var camera = _world.CameraPosition;
            var design = snapshot.Content.Designs[own.Design];
            var hull = _world.OwnDrawnPosition.RelativeTo(camera);
            var cut = point.RelativeTo(camera);
            var through = 0;
            var outside = 0;
            var throatKept = 0;
            var worldKept = 0;
            foreach (var offset in design.BlockOffsets)
            {
                var at = hull + Vector3.Transform(offset, _world.OwnOrientation);
                var inThroatSide = Vector3.Dot(at - cut, throat) > 0f;
                var kept = Kept(at, cut, hidden, window);
                if (inThroatSide)
                {
                    through++;
                    throatKept += kept ? 1 : 0;
                }
                else
                {
                    outside++;
                    worldKept += kept ? 1 : 0;
                }
            }

            if (inThroat)
            {
                // Inside, the throat's side is all there; the world's side shows at most through the window.
                Assert.Equal(through, throatKept);
                throatSeen += through > 0 ? 1 : 0;
                continue;
            }

            // From the world, nothing in the throat is drawn and everything outside it is.
            Assert.Equal(0, throatKept);
            Assert.Equal(outside, worldKept);
            if (through > 0 && outside > 0)
            {
                if (exit)
                    exitCut++;
                else
                    entryCut++;
            }
        }

        Assert.True(started);
        // The hull is seen part way through its opening from the world's side, cut there, from every side of it.
        Assert.True(entryCut + exitCut > 0, $"{drawnAs} at {lookDegrees}: never seen cut part way through ({entryCut} in, {exitCut} out)");
        Assert.True(throatSeen > 0, $"{drawnAs} at {lookDegrees}: never seen from inside the throat");
    }
}
