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
        // These visual fixtures exercise long transit independently of production drive range tuning.
        using var files = new ContentCopy();
        files.Write("blocks/blocks.json", files.Read("blocks/blocks.json").Replace("400000", "2000000"));
        var content = files.Load();
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
    private SessionSnapshot Frame(bool turnRight, bool boost = false, float scroll = 0f)
    {
        ((ITickHandler)_server).Tick(_tick);
        ((ITickHandler)_client).Tick(_tick++);
        var snapshot = _client.AcquireSnapshot();
        // These frames follow manual simulation ticks. Both views must render the same completed tick even if
        // the test runner pauses between their updates, rather than sampling different wall-clock fractions.
        snapshot.PublishedAt = 0;
        _devices.BeginFrame();
        if (scroll != 0f)
            _devices.OnScroll(scroll);
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
    [Fact]
    public void AHullsMaterialSaysWhatItIsAndWhichOpeningCutsIt()
    {
        // As mesh.vert reads it back: the kind below four, and one more than the clip slot in steps of four. The
        // pilot's own hull inside its jump takes no slot: its opening comes from the frame's own jump.
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

    /// <summary>Whether the segment from the camera to a point meets a sphere.</summary>
    private static bool Meets(Vector3 point, Vector3 centre, float radius)
    {
        var t = Math.Clamp(Vector3.Dot(centre, point) / point.LengthSquared(), 0f, 1f);
        return (point * t - centre).Length() <= radius;
    }

    /// <summary>Where each block of the pilot's own hull is drawn, relative to the camera.</summary>
    private IEnumerable<Vector3> OwnBlocks(SessionSnapshot snapshot)
    {
        var design = snapshot.Content.Designs[snapshot.Hulls[snapshot.OwnHull].Design];
        var hull = _world.OwnDrawnPosition.RelativeTo(_world.CameraPosition);
        foreach (var offset in design.BlockOffsets)
            yield return hull + Vector3.Transform(offset, _world.OwnOrientation);
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
        // The pilot's own hull, drawn from inside its jump, against the opening the frame constants give the GPU:
        // from the world's side, nothing of it in the throat shows, so it goes in nose first and comes out stern
        // last; as the camera's side eases into the throat, what is in the throat comes in with it. Looking from
        // behind, beside and ahead of the hull.
        _world.FtlPreview = drawnAs;
        _world.LookDegrees = lookDegrees;
        var target = _client.AcquireSnapshot().Galaxy!.CenterOf(new SectorCoord(12, 0, -7));
        _client.Input.IssueOrder(new HullOrder { Kind = OrderKind.Jump, Target = target });

        var started = false;
        var straddled = 0;
        var cutFromWorld = 0;
        var noseFirst = 0;
        var seenInThroat = 0;
        for (var frame = 0; frame < TickLoop.SimulationHz * 120; frame++)
        {
            var snapshot = Frame(false);
            ref readonly var own = ref snapshot.Hulls[snapshot.OwnHull];
            started |= own.Jump != JumpPhase.None;
            if (started && own.Jump == JumpPhase.None)
                break;
            if (own.Jump == JumpPhase.None)
                continue;

            // What the mesh pass is given is the opening the view has, where the view has it.
            var aperture = _world.OwnAperture;
            Assert.True(aperture.Active);
            var (centre, axis, radius) = _world.OwnOpening;
            var mouth = aperture.Mouth;
            Assert.True((new Vector3(mouth.X, mouth.Y, mouth.Z) - centre.RelativeTo(_world.CameraPosition)).Length() < 1e-3f);
            Assert.Equal(radius, mouth.W);
            Assert.Equal(_world.JumpThrough, aperture.Past);

            var exit = own.Jump is JumpPhase.Tunnel or JumpPhase.Arrive;
            var throat = exit ? -axis : axis;
            var inThroat = exit ? 1f - aperture.Past : aperture.Past;
            var cut = centre.RelativeTo(_world.CameraPosition);
            var through = 0;
            var throatShown = 0;
            var outsideShown = 0;
            float noseAlong = float.MinValue, sternAlong = float.MaxValue, noseShows = 0f, sternShows = 0f;
            foreach (var at in OwnBlocks(snapshot))
            {
                var shows = aperture.HullShows(at, _world.PixelAngle);
                Assert.InRange(shows, 0f, 1f);
                if (Vector3.Dot(at - cut, throat) > 0f)
                {
                    // Never more of it than the camera is in the throat.
                    Assert.True(shows <= inThroat + 1e-5f, $"{own.Jump}: a block in the throat shows {shows}, camera {inThroat} in");
                    through++;
                    throatShown += shows > 0.5f ? 1 : 0;
                }
                else
                {
                    // Not yet in (or already out): hidden only as far as the opening's window is in front of it, as a
                    // rock behind it would be; never cut. Nothing of an opening reaches further than 2.2 radii.
                    var window = aperture.WindowBefore(at, _world.PixelAngle);
                    Assert.Equal(1f - window * (1f - inThroat), shows, 5);
                    if (!Meets(at, cut, radius * 2.2f))
                        Assert.Equal(1f, shows);
                    outsideShown += shows > 0.5f ? 1 : 0;
                }

                // Nose first: the block furthest along is the first to go, the one furthest back the last.
                var along = Vector3.Dot(at, throat);
                if (along > noseAlong)
                    (noseAlong, noseShows) = (along, shows);
                if (along < sternAlong)
                    (sternAlong, sternShows) = (along, shows);
            }

            if (inThroat < 0.01f && noseShows < 0.5f && sternShows > 0.5f)
                noseFirst++;

            // Partway through with the view on the world's side: nothing through the opening shows (asserted
            // above), and the part still out of it does.
            if (inThroat < 0.01f && through > 0)
            {
                straddled++;
                cutFromWorld += outsideShown > 0 ? 1 : 0;
            }
            if (inThroat > 0.99f && throatShown > 0)
                seenInThroat++;
        }

        Assert.True(started);
        Assert.True(straddled > 0, $"{drawnAs} at {lookDegrees}: never part way through with the camera on the world's side");
        // Seen from behind or beside, the hull goes in nose first. From ahead, once the camera is beyond the opening
        // with the view still on the world's side, the window is between it and the whole hull and shows the throat,
        // as it would over a rock there; a wormhole's sphere is larger than the hull and covers it from its surface on.
        if (lookDegrees < 135f)
            Assert.True(noseFirst > 0, $"{drawnAs} at {lookDegrees}: never seen nose gone, stern still there");
        if (drawnAs == FtlKind.Rift && lookDegrees < 135f)
            Assert.True(cutFromWorld > 0, $"{drawnAs} at {lookDegrees}: never seen cut at the opening");
        Assert.True(seenInThroat > 0, $"{drawnAs} at {lookDegrees}: never seen from inside the throat");
    }

    [Fact]
    public void TheCommandViewPicksThePilotsOwnHullAsItIsStillDrawnFromInside()
    {
        // Partway into a rift, seen from beside, the command key pulls the view out; for a few frames the hull is
        // still drawn from inside its jump. What of it is through the opening is not drawn, and is not picked.
        _world.FtlPreview = FtlKind.Rift;
        _world.LookDegrees = 90f;
        var target = _client.AcquireSnapshot().Galaxy!.CenterOf(new SectorCoord(12, 0, -7));
        _client.Input.IssueOrder(new HullOrder { Kind = OrderKind.Jump, Target = target });

        SessionSnapshot? snapshot = null;
        for (var frame = 0; frame < TickLoop.SimulationHz * 60; frame++)
        {
            snapshot = Frame(false);
            var (centre, axis, _) = _world.OwnOpening;
            var cut = centre.RelativeTo(_world.CameraPosition);
            var deepest = float.MinValue;
            foreach (var at in OwnBlocks(snapshot))
                deepest = Math.Max(deepest, Vector3.Dot(at - cut, axis));
            // Well into it: the nose a block and a half through, the stern still out, the camera on the world's side.
            if (_world.Own.Jump == JumpPhase.Charge && deepest > 1.5f * snapshot.Content.Designs[_world.Own.Design].BlockSize && _world.JumpThrough < 0.01f)
                break;
        }

        // Where the nose and the stern are on the screen in the frame on display, by what that frame drew of them.
        (float2 Nose, float2 Stern) Shown(SessionSnapshot shown)
        {
            var aperture = _world.OwnAperture;
            Assert.True(aperture.Active, "the hull is no longer drawn from inside");
            var axis = _world.OwnOpening.Axis;
            Vector3 nose = default, stern = default;
            float noseShows = 1f, sternShows = 0f, noseAlong = float.MinValue, sternAlong = float.MaxValue;
            foreach (var at in OwnBlocks(shown))
            {
                var along = Vector3.Dot(at, axis);
                if (along > noseAlong)
                    (nose, noseAlong, noseShows) = (at, along, aperture.HullShows(at, _world.PixelAngle));
                if (along < sternAlong)
                    (stern, sternAlong, sternShows) = (at, along, aperture.HullShows(at, _world.PixelAngle));
            }

            Assert.True(noseShows < 0.5f && sternShows > 0.5f, $"nose shows {noseShows}, stern {sternShows}");
            Assert.True(_world.Project(_world.CameraPosition + nose, out var noseAt));
            Assert.True(_world.Project(_world.CameraPosition + stern, out var sternAt));
            return (noseAt, sternAt);
        }

        // One client frame as the window delivers it: the command key and the pointer, then the view's update, which
        // picks what is under the pointer before it moves anything, against the frame on display.
        void Poll(bool command, float2 pointer)
        {
            ((ITickHandler)_server).Tick(_tick);
            ((ITickHandler)_client).Tick(_tick++);
            _devices.BeginFrame();
            _devices.OnKey((GlfwKey)_actions.Bindings(InputAction.ToggleCommand)[0].Code, command ? GlfwKeyAction.Press : GlfwKeyAction.Release);
            _devices.OnCursorPos(new double2(pointer.X, pointer.Y));
            _world.Update(_client.AcquireSnapshot(), _client.Input, Dt, true, Screen, Screen, false);
        }

        // The command key and the pointer on the nose in the same poll: the view switches, the hull is still drawn
        // from inside, and the nose, gone through the opening, is not under the pointer.
        var (noseAt, _) = Shown(snapshot!);
        Poll(true, noseAt);
        Assert.Equal(ViewMode.Command, _world.Mode);
        Assert.NotEqual(_world.Own.Id, _world.HoveredHull);

        // The next poll, on the stern as that frame drew it: it is there, and picked.
        var (_, sternAt) = Shown(_client.AcquireSnapshot());
        Poll(false, sternAt);
        Assert.True(_world.OwnAperture.Active);
        Assert.Equal(_world.Own.Id, _world.HoveredHull);
    }

    [Theory]
    [InlineData(FtlKind.Rift)]
    [InlineData(FtlKind.Wormhole)]
    public void ZoomingDuringTheChargeMovesOnlyTheCamera(FtlKind drawnAs)
    {
        // Two views of the same jump, one zoomed in and out as the hull moves into its opening: the hull's path and
        // its opening are the same in both, frame by frame; only the cameras differ.
        var devices = new InputDevices();
        var zoomed = new WorldView(devices, _actions, new ClientSettings(), _jobs, null!, _client.AcquireSnapshot().Content.Designs);
        _world.FtlPreview = zoomed.FtlPreview = drawnAs;
        var target = _client.AcquireSnapshot().Galaxy!.CenterOf(new SectorCoord(12, 0, -7));
        _client.Input.IssueOrder(new HullOrder { Kind = OrderKind.Jump, Target = target });

        var compared = 0;
        for (var frame = 0; frame < TickLoop.SimulationHz * 60; frame++)
        {
            var snapshot = Frame(false);
            devices.BeginFrame();
            ref readonly var own = ref snapshot.Hulls[snapshot.OwnHull];
            var moving = own.Jump == JumpPhase.Charge && WorldView.JumpProgress(own, _world.OwnFlight) > 0.6f;
            if (moving)
                devices.OnScroll(frame % 20 < 10 ? -1f : 2f);
            zoomed.Update(snapshot, _client.Input, Dt, true, Screen, Screen, false);
            if (own.Jump is not (JumpPhase.Align or JumpPhase.Charge))
                continue;

            Assert.True((_world.OwnDrawnPosition.RelativeTo(zoomed.OwnDrawnPosition)).Length() < 0.05f, $"{own.Jump}: drawn {(_world.OwnDrawnPosition.RelativeTo(zoomed.OwnDrawnPosition)).Length()} m apart");
            Assert.True((_world.OwnOpening.Centre.RelativeTo(zoomed.OwnOpening.Centre)).Length() < 0.05f);
            Assert.Equal(_world.OwnOpening.Radius, zoomed.OwnOpening.Radius, 2);
            compared += moving ? 1 : 0;
        }

        Assert.True(compared > 30, $"compared {compared} frames while moving");
    }

    [Theory]
    [InlineData(FtlKind.Rift)]
    [InlineData(FtlKind.Wormhole)]
    public void ALongTransitWaitsInTheThroatAndComesOutOverTheLastSeconds(FtlKind drawnAs)
    {
        // A jump many sectors away takes far longer than the drive's base transit. The pilot's view waits in the
        // throat, the far opening barely begun, and comes out over the base transit's last seconds as for a short
        // jump, ending where the server put the hull.
        _world.FtlPreview = drawnAs;
        var target = _client.AcquireSnapshot().Galaxy!.CenterOf(new SectorCoord(12, 0, -7));
        _client.Input.IssueOrder(new HullOrder { Kind = OrderKind.Jump, Target = target });

        var flight = _world.OwnFlight;
        var basis = flight.FtlTunnelSeconds;
        var full = flight.Radius * HullVisuals.OpeningRadius;
        float? waiting = null;
        var waited = 0;
        var moved = false;
        var started = false;
        for (var frame = 0; frame < TickLoop.SimulationHz * 120; frame++)
        {
            var snapshot = Frame(false);
            ref readonly var own = ref snapshot.Hulls[snapshot.OwnHull];
            started |= own.Jump != JumpPhase.None;
            if (started && own.Jump == JumpPhase.None)
            {
                Assert.True(own.Position.RelativeTo(target).Length() < 1f);
                break;
            }

            if (own.Jump != JumpPhase.Tunnel)
                continue;
            Assert.True(own.JumpTunnelSeconds > basis + 5f, $"transit {own.JumpTunnelSeconds} s");
            var offset = Vector3.Dot(_world.OwnDrawnPosition.RelativeTo(_world.OwnPosition), _world.OwnOpening.Axis);
            var left = own.JumpTunnelSeconds - own.JumpTimer;
            if (left > basis + 0.1f)
            {
                // Waiting: the hull holds where the way out starts, the far opening only begun.
                waiting ??= offset;
                Assert.Equal(waiting.Value, offset, 2);
                Assert.True(_world.OwnOpening.Radius < full * 0.06f, $"far opening {_world.OwnOpening.Radius} with {left} s left");
                waited++;
            }
            else if (waiting is { } start && MathF.Abs(offset - start) > 1f)
            {
                moved = true;
            }
        }

        Assert.True(started);
        Assert.True(waited > TickLoop.SimulationHz * 5, $"waited {waited} frames");
        Assert.True(moved);
    }

    [Theory]
    [InlineData(FtlKind.Rift)]
    [InlineData(FtlKind.Wormhole)]
    [InlineData(FtlKind.Hyperspace)]
    public void TheLongestSupportedTransitEndsAsTheShortestFromInside(FtlKind drawnAs)
    {
        // Two views that first see the pilot's own jump in transit, as after joining late: one told it lasts the
        // longest the simulation supports, 2^40 seconds, the other 20. Through the transit's last seconds and the
        // arrival, both draw the hull and its opening the same, and nothing becomes NaN.
        var target = _client.AcquireSnapshot().Galaxy!.CenterOf(new SectorCoord(12, 0, -7));
        _client.Input.IssueOrder(new HullOrder { Kind = OrderKind.Jump, Target = target });
        SessionSnapshot snapshot;
        do
            snapshot = Frame(false);
        while (snapshot.Hulls[snapshot.OwnHull].Jump != JumpPhase.Tunnel);

        var designs = snapshot.Content.Designs;
        var longest = new WorldView(new InputDevices(), _actions, new ClientSettings(), _jobs, null!, designs) { FtlPreview = drawnAs };
        var shortest = new WorldView(new InputDevices(), _actions, new ClientSettings(), _jobs, null!, designs) { FtlPreview = drawnAs };
        const double Supported = 1099511627776.0;
        ref var own = ref snapshot.Hulls[snapshot.OwnHull];
        var saved = own;
        try
        {
            for (var left = 3.5; left > -1.4; left -= 0.05)
            {
                foreach (var (view, tunnel) in (ReadOnlySpan<(WorldView, double)>)[(longest, Supported), (shortest, 20.0)])
                {
                    own.Jump = left > 0 ? JumpPhase.Tunnel : JumpPhase.Arrive;
                    own.JumpTunnelSeconds = (float)tunnel;
                    own.JumpTunnelElapsed = left > 0 ? (float)tunnel - left : 0;
                    own.JumpTimer = left > 0 ? (float)own.JumpTunnelElapsed : (float)-left;
                    view.Update(snapshot, _client.Input, Dt, false, Screen, Screen, false);
                }

                var drawn = longest.OwnDrawnPosition.RelativeTo(longest.OwnPosition);
                Assert.True(float.IsFinite(drawn.X) && float.IsFinite(drawn.Y) && float.IsFinite(drawn.Z), $"{left} s left: drawn {drawn}");
                Assert.True((drawn - shortest.OwnDrawnPosition.RelativeTo(shortest.OwnPosition)).Length() < 0.01f, $"{left} s left");
                Assert.True(longest.OwnOpening.Centre.RelativeTo(shortest.OwnOpening.Centre).Length() < 0.01f, $"{left} s left");
                // The two views read the tick's clock a moment apart, which moves things by a hair.
                Assert.True(MathF.Abs(shortest.OwnOpening.Radius - longest.OwnOpening.Radius) < 0.01f, $"{left} s left");
                Assert.True(MathF.Abs(shortest.JumpThrough - longest.JumpThrough) < 0.01f, $"{left} s left");
            }
        }
        finally
        {
            own = saved;
        }
    }

    /// <summary>How far along its passage the pilot's own hull is drawn from where the simulation has it.</summary>
    private static float Offset(WorldView view) => Vector3.Dot(view.OwnDrawnPosition.RelativeTo(view.OwnPosition), view.OwnOpening.Axis);

    [Theory]
    [InlineData(FtlKind.Rift)]
    [InlineData(FtlKind.Wormhole)]
    public void ANewDestinationWhileTheHullIsGoingInStartsAFreshPassage(FtlKind drawnAs)
    {
        // Late in the charge, partway into the opening, the pilot zooms out and then orders a jump somewhere else.
        // The server restarts the drive within a tick, so the view never sees the hull without a jump. The hull eases
        // back out rather than jumping back in one frame, and the new passage is laid out for the camera as it is
        // now: once the old one has eased away it is the same as a view that only ever saw the new one.
        _world.FtlPreview = drawnAs;
        var galaxy = _client.AcquireSnapshot().Galaxy!;
        _client.Input.IssueOrder(new HullOrder { Kind = OrderKind.Jump, Target = galaxy.CenterOf(new SectorCoord(12, 0, -7)) });
        SessionSnapshot snapshot;
        do
            snapshot = Frame(false);
        while (snapshot.Hulls[snapshot.OwnHull].Jump != JumpPhase.Charge || WorldView.JumpProgress(snapshot.Hulls[snapshot.OwnHull], _world.OwnFlight) < 0.8f);
        Assert.True(Offset(_world) > _world.OwnFlight.Radius * 2f, $"only {Offset(_world)} m in");

        Frame(false, scroll: -3f);
        _client.Input.IssueOrder(new HullOrder { Kind = OrderKind.Jump, Target = galaxy.CenterOf(new SectorCoord(-9, 0, 11)) });
        var devices = new InputDevices();
        var fresh = new WorldView(devices, _actions, new ClientSettings(), _jobs, null!, snapshot.Content.Designs) { FtlPreview = drawnAs };
        var previous = Offset(_world);
        var restarted = false;
        var compared = 0;
        for (var frame = 0; frame < TickLoop.SimulationHz * 40; frame++)
        {
            snapshot = Frame(false);
            ref readonly var own = ref snapshot.Hulls[snapshot.OwnHull];
            Assert.NotEqual(JumpPhase.None, own.Jump);
            if (own.Jump == JumpPhase.Tunnel)
                break;
            devices.BeginFrame();
            if (frame == 0)
                devices.OnScroll(-3f);
            fresh.Update(snapshot, _client.Input, Dt, true, Screen, Screen, false);

            // Never more than a step at the speed the hull moves into an opening.
            var offset = Offset(_world);
            Assert.True(MathF.Abs(offset - previous) < 1.5f, $"{own.Jump}: jumped from {previous} to {offset} m");
            previous = offset;
            restarted |= own.Jump == JumpPhase.Align || own.JumpCalculation < 1f;

            if (restarted && frame > TickLoop.SimulationHz * 3 && own.Jump == JumpPhase.Charge)
            {
                Assert.True(MathF.Abs(offset - Offset(fresh)) < 0.05f, $"{offset} m in, a fresh view {Offset(fresh)}");
                Assert.True(_world.OwnOpening.Centre.RelativeTo(fresh.OwnOpening.Centre).Length() < 0.05f);
                compared += Offset(fresh) > 0.5f ? 1 : 0;
            }
        }

        Assert.True(restarted, "the drive never started over");
        Assert.True(compared > 10, $"compared {compared} frames of the new passage");
    }

    [Fact]
    public void TakingControlOfAnotherHullInTransitDrawsItsOwnPassage()
    {
        // The pilot's hull and the fleet jump together; while both are in transit the pilot takes control of a fleet
        // hull. Its passage is its own from that frame on: the same as a view that only ever saw it, its exit placed
        // for it and not left where the first hull's was.
        _world.FtlPreview = FtlKind.Rift;
        var target = _client.AcquireSnapshot().Galaxy!.CenterOf(new SectorCoord(12, 0, -7));
        _world.SelectAllOwned();
        _world.OrderJump(_client.Input, target);
        _client.Input.IssueOrder(new HullOrder { Kind = OrderKind.Jump, Target = target });

        // The fleet hull to switch to, while it is still in this client's view. (Driven tick by tick, the session
        // stamps snapshots with its loop's own tick, which does not move here, so remote hulls drop out of the view
        // after a few seconds; the hull the pilot takes over comes back as the player's own.)
        var snapshot = _client.AcquireSnapshot();
        uint other = 0;
        for (var i = 0; i < snapshot.HullCount && other == 0; i++)
        {
            if (snapshot.Hulls[i].Owner == snapshot.PlayerId && !snapshot.Hulls[i].IsOwn)
                other = snapshot.Hulls[i].Id;
        }

        do
            snapshot = Frame(false);
        while (snapshot.Hulls[snapshot.OwnHull].Jump != JumpPhase.Tunnel || snapshot.Hulls[snapshot.OwnHull].JumpTimer < 1f);

        Assert.NotEqual(0u, other);
        var before = _world.OwnOpening.Centre;
        _client.Input.RequestControl(other);
        do
            snapshot = Frame(false);
        while (_world.Own.Id != other);
        // Both hulls were in transit: the one taken over is still in its own.
        Assert.Equal(JumpPhase.Tunnel, _world.Own.Jump);

        var fresh = new WorldView(new InputDevices(), _actions, new ClientSettings(), _jobs, null!, snapshot.Content.Designs) { FtlPreview = FtlKind.Rift };
        fresh.Update(snapshot, _client.Input, Dt, false, Screen, Screen, false);
        var compared = 0;
        for (var frame = 0; frame < TickLoop.SimulationHz * 60; frame++)
        {
            Assert.True(_world.OwnOpening.Centre.RelativeTo(fresh.OwnOpening.Centre).Length() < 0.05f,
                $"{_world.Own.Jump}: exit {_world.OwnOpening.Centre.RelativeTo(fresh.OwnOpening.Centre).Length()} m from a fresh view's");
            Assert.True(MathF.Abs(_world.OwnOpening.Radius - fresh.OwnOpening.Radius) < 0.01f,
                $"{_world.Own.Jump}: opening radius {_world.OwnOpening.Radius} m, fresh view {fresh.OwnOpening.Radius} m");
            Assert.True(_world.OwnDrawnPosition.RelativeTo(fresh.OwnDrawnPosition).Length() < 0.05f);
            Assert.True(MathF.Abs(_world.JumpThrough - fresh.JumpThrough) < 0.01f);
            compared++;
            snapshot = Frame(false);
            if (snapshot.Hulls[snapshot.OwnHull].Jump == JumpPhase.None)
                break;
            fresh.Update(snapshot, _client.Input, Dt, false, Screen, Screen, false);
        }

        Assert.True(compared > TickLoop.SimulationHz, $"compared {compared} frames");
        // The first hull's exit, kept where it was relative to the new hull, would have been somewhere else.
        Assert.True(before.RelativeTo(fresh.OwnOpening.Centre).Length() > 1f, "the first hull's exit was where the new one's is");
    }
}
