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
/// Starting a plotted jump with forward and boost together: a real server and client joined in process, the real
/// <see cref="WorldView"/> reading real <see cref="InputDevices"/> through the loaded bindings, keys held and let go
/// as a pilot would. No key but halt cancels a started jump: boost and thrust, pressed, held, tapped or let go, do not.
/// </summary>
public sealed class JumpChordTests : IDisposable
{
    private const float Dt = 1f / TickLoop.SimulationHz;
    private static readonly float2 Screen = new(1280, 720);

    private readonly LoopbackLink _link = new();
    private readonly ClientSession _client;
    private readonly ServerHost _server;
    private readonly JobSystem _jobs = new(1, "TestJob");
    private readonly InputDevices _devices = new();
    private readonly GameContent _content;
    private readonly HashSet<GlfwKey> _down = [];
    private ActionMap _actions;
    private WorldView _world;
    private long _tick;

    public JumpChordTests()
    {
        _content = GameContent.Load();
        _client = new ClientSession(_link.Client, _content, "Pilot");
        _server = new ServerHost(new ServerConfig { MaxPlayers = 1 }, _content, [_link.Server]);
        _actions = ActionMap.Load(Path.Combine(AppContext.BaseDirectory, "content", "input", "bindings.json"));
        _world = new WorldView(_devices, _actions, new ClientSettings(), _jobs, null!, _content.Designs);
        for (var i = 0; i < 10; i++)
            Step();
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

    // The first key the loaded bindings give each control, so a remapped file is followed.
    private GlfwKey Forward => (GlfwKey)_actions.Bindings(InputAxis.Forward)[0].Positive!.Value.Code;
    private GlfwKey Reverse => (GlfwKey)_actions.Bindings(InputAxis.Forward)[0].Negative!.Value.Code;
    private GlfwKey Strafe => (GlfwKey)_actions.Bindings(InputAxis.Strafe)[0].Positive!.Value.Code;
    private GlfwKey Boost => (GlfwKey)_actions.Bindings(InputAction.Boost)[0].Code;
    private GlfwKey Halt => (GlfwKey)_actions.Bindings(InputAction.Halt)[0].Code;

    /// <summary>One client frame with exactly the keys in <see cref="_down"/> held; edges come from the changes.</summary>
    private void Draw(bool inputEnabled = true)
    {
        var snapshot = _client.AcquireSnapshot();
        _devices.BeginFrame();
        foreach (var key in (ReadOnlySpan<GlfwKey>)[Forward, Reverse, Strafe, Boost, Halt])
            _devices.OnKey(key, _down.Contains(key) ? GlfwKeyAction.Press : GlfwKeyAction.Release);
        if (snapshot.Galaxy is not null && snapshot.OwnHull >= 0)
            _world.Update(snapshot, _client.Input, Dt, inputEnabled, Screen, Screen, false);
    }

    /// <summary>One simulation tick on both ends, then the given number of client frames.</summary>
    private void Step(int frames = 1, bool inputEnabled = true)
    {
        ((ITickHandler)_server).Tick(_tick);
        ((ITickHandler)_client).Tick(_tick++);
        for (var i = 0; i < frames; i++)
            Draw(inputEnabled);
    }

    private HullView Own
    {
        get
        {
            var snapshot = _client.AcquireSnapshot();
            return snapshot.Hulls[snapshot.OwnHull];
        }
    }

    private float ForwardSpeed => Vector3.Dot(Own.Velocity, Vector3.Transform(-Vector3.UnitZ, Own.Orientation));

    /// <summary>Plots a course straight ahead, or off to the side, and waits until the view has it.</summary>
    private Double3 Plot(float degreesOff = 0.5f)
    {
        var nose = Vector3.Transform(-Vector3.UnitZ, Quaternion.CreateFromAxisAngle(Vector3.UnitY, degreesOff * MathF.PI / 180f) * _world.OwnOrientation);
        var target = _world.OwnPosition + nose * (float)(_client.AcquireSnapshot().Galaxy!.SectorSize * 2.5);
        _world.Select(_world.Own.Id, false);
        _world.OrderJump(_client.Input, target);
        Step();
        Assert.Equal(target, _world.NavTarget);
        return target;
    }

    /// <summary>
    /// Runs ticks with the keys as they are and checks the jump is never cancelled: the order stands and the drive's
    /// calculation never starts over. A key pressed just before is read in the first frame, so the order it gives
    /// is in the simulation from the second tick. Returns the phase reached.
    /// </summary>
    private JumpPhase HoldsTheJump(int ticks, int framesPerTick = 1)
    {
        var calculation = 0f;
        for (var i = 0; i < ticks; i++)
        {
            Step(framesPerTick);
            var own = Own;
            if (i == 0 && own.Order.Kind != OrderKind.Jump)
                continue;
            Assert.True(own.Order.Kind == OrderKind.Jump, $"tick {i}: the jump was cancelled ({own.Jump})");
            Assert.True(own.JumpCalculation >= calculation, $"tick {i}: calculation went back from {calculation} to {own.JumpCalculation}");
            calculation = own.JumpCalculation;
        }

        Assert.True(Own.Jump is JumpPhase.Align or JumpPhase.Charge);
        Assert.True(calculation > 0f);
        return Own.Jump;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void ForwardHeldBeforeBoostStartsTheJumpAndFliesItThroughAfterBoostIsLetGo(int framesPerTick)
    {
        var target = Plot();
        _down.Add(Forward);
        for (var i = 0; i < 10; i++)
            Step(framesPerTick);
        Assert.True(_world.NavAimed);
        Assert.True(ForwardSpeed > 0f);

        _down.Add(Boost);
        HoldsTheJump(10, framesPerTick);
        _down.Remove(Boost);
        Assert.Equal(JumpPhase.Charge, HoldsTheJump(TickLoop.SimulationHz * 2, framesPerTick));

        // Forward still held the whole way: the server flies the jump to the plotted point.
        var started = false;
        for (var i = 0; i < TickLoop.SimulationHz * 60; i++)
        {
            Step(framesPerTick);
            started |= Own.Jump != JumpPhase.None;
            if (started && Own.Jump == JumpPhase.None)
                break;
        }

        Assert.True(Own.Position.RelativeTo(target).Length() < 1f, $"arrived {Own.Position}, plotted {target}");

        // Once the jump is over, the forward key still held is plain thrust again.
        var speed = ForwardSpeed;
        for (var i = 0; i < 20; i++)
            Step(framesPerTick);
        Assert.True(ForwardSpeed > speed + 1f, $"forward speed {speed} then {ForwardSpeed}");
    }

    [Fact]
    public void ForwardAndBoostInTheSamePollStartTheJump()
    {
        Plot();
        _down.Add(Forward);
        _down.Add(Boost);
        HoldsTheJump(TickLoop.SimulationHz * 2);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void ForwardJoiningAfterBoostBelongsToTheSameStart(int framesPerTick)
    {
        Plot();
        _down.Add(Boost);
        Step(framesPerTick);
        _down.Add(Forward);
        HoldsTheJump(5, framesPerTick);
        _down.Remove(Boost);
        HoldsTheJump(TickLoop.SimulationHz * 2, framesPerTick);
    }

    [Fact]
    public void ForwardPressedWhileBoostIsAlreadyHeldStartsTheJump()
    {
        // Boost held first, before there is any course: ordinary boost, nothing ordered.
        _down.Add(Boost);
        for (var i = 0; i < 5; i++)
            Step();
        Plot();
        for (var i = 0; i < 5; i++)
            Step();
        Assert.NotEqual(OrderKind.Jump, Own.Order.Kind);

        // Pushing forward with boost still held completes the start.
        _down.Add(Forward);
        HoldsTheJump(TickLoop.SimulationHz);
    }

    [Fact]
    public void FreshForwardAfterLettingGoOfBothDoesNotCancel()
    {
        Plot();
        _down.Add(Forward);
        _down.Add(Boost);
        HoldsTheJump(30);
        _down.Clear();
        HoldsTheJump(5);

        _down.Add(Forward);
        HoldsTheJump(TickLoop.SimulationHz);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ReverseOrStrafeNeverCancelsWhetherOrNotBoostIsHeld(bool reverse)
    {
        Plot();
        _down.Add(Forward);
        _down.Add(Boost);
        HoldsTheJump(30);
        _down.Remove(Forward);
        _down.Add(reverse ? Reverse : Strafe);
        HoldsTheJump(TickLoop.SimulationHz);
        _down.Remove(Boost);
        HoldsTheJump(TickLoop.SimulationHz);
        _down.Clear();
        HoldsTheJump(5);
        _down.Add(reverse ? Reverse : Strafe);
        HoldsTheJump(TickLoop.SimulationHz);
    }

    [Fact]
    public void HaltCancelsWhileTheChordIsHeld()
    {
        Plot();
        _down.Add(Forward);
        _down.Add(Boost);
        HoldsTheJump(30);
        _down.Add(Halt);
        Step();
        Step();
        Assert.NotEqual(OrderKind.Jump, Own.Order.Kind);
        Assert.Equal(JumpPhase.None, Own.Jump);

        // With the jump gone, forward still held flies the hull again.
        _down.Remove(Halt);
        var speed = ForwardSpeed;
        for (var i = 0; i < 20; i++)
            Step();
        Assert.True(ForwardSpeed > speed + 1f, $"forward speed {speed} then {ForwardSpeed}");
    }

    /// <summary>
    /// Boost pressed, held, tapped and let go through the whole jump, calculation to arrival, with a real server and the
    /// real view: the order stands, the drive's calculation never goes back, the hull lands on the plotted point, and the
    /// boost key held on arrival starts no second jump.
    /// </summary>
    [Fact]
    public void BoostPressedHeldTappedAndLetGoNeverCancelsAnyStageOfTheJump()
    {
        var target = Plot();
        _down.Add(Boost);
        Step();
        var calculation = 0f;
        var started = false;
        var tunnelSeen = false;
        var arriveSeen = false;
        for (var i = 0; i < TickLoop.SimulationHz * 60; i++)
        {
            // Held for 7 ticks, let go for 5, tapped, let go, a short hold, let go: a different edge every tick.
            switch (i % 24)
            {
                case 0 or 8 or 17:
                    _down.Add(Boost);
                    break;
                case 7 or 13 or 18 or 20:
                    _down.Remove(Boost);
                    break;
            }

            Step();
            var own = Own;
            started |= own.Jump != JumpPhase.None;
            tunnelSeen |= own.Jump == JumpPhase.Tunnel;
            arriveSeen |= own.Jump == JumpPhase.Arrive;
            if (started && own.Jump == JumpPhase.None)
                break;
            if (own.Jump is JumpPhase.Align or JumpPhase.Charge)
            {
                Assert.True(own.Order.Kind == OrderKind.Jump, $"tick {i}: cancelled in {own.Jump}");
                Assert.True(own.JumpCalculation >= calculation, $"tick {i}: calculation went from {calculation} to {own.JumpCalculation}");
                calculation = own.JumpCalculation;
            }
        }

        Assert.True(tunnelSeen && arriveSeen);
        Assert.True(Own.Position.RelativeTo(target).Length() < 1f, $"arrived {Own.Position}, plotted {target}");
        _down.Add(Boost);
        for (var i = 0; i < 30; i++)
        {
            Step();
            Assert.Equal(JumpPhase.None, Own.Jump);
            Assert.NotEqual(OrderKind.Jump, Own.Order.Kind);
        }
    }

    [Fact]
    public void BoostPressedAgainWhileTheJumpIsOrderedIsNotARefusal()
    {
        Plot();
        _down.Add(Boost);
        Step();
        _down.Remove(Boost);
        HoldsTheJump(TickLoop.SimulationHz);
        var shown = _world.NavRefusedAt;
        for (var i = 0; i < 6; i++)
        {
            _down.Add(Boost);
            HoldsTheJump(2);
            _down.Remove(Boost);
            HoldsTheJump(2);
        }

        Assert.Equal(shown, _world.NavRefusedAt);
    }

    [Fact]
    public void HaltWinsOverForwardAndBoostPressedInTheSamePoll()
    {
        Plot();
        Assert.True(_world.NavAimed);
        _down.Add(Forward);
        _down.Add(Boost);
        _down.Add(Halt);
        for (var i = 0; i < 10; i++)
        {
            Step();
            Assert.NotEqual(OrderKind.Jump, Own.Order.Kind);
            Assert.Equal(JumpPhase.None, Own.Jump);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BoostWithoutAnAimedCourseIsOrdinaryBoostAndThrust(bool offAim)
    {
        if (offAim)
            Plot(40f);
        Assert.False(_world.NavAimed);
        _down.Add(Forward);
        Step();
        _down.Add(Boost);
        var speed = ForwardSpeed;
        for (var i = 0; i < 20; i++)
            Step();
        Assert.NotEqual(OrderKind.Jump, Own.Order.Kind);
        Assert.Equal(JumpPhase.None, Own.Jump);
        Assert.True(Own.Boosting);
        Assert.True(ForwardSpeed > speed + 1f, $"forward speed {speed} then {ForwardSpeed}");
        if (offAim)
            Assert.True(_world.Time - _world.NavRefusedAt < 1f);
    }

    [Fact]
    public void BoostAloneStillStartsTheJump()
    {
        Plot();
        _down.Add(Boost);
        Step();
        _down.Remove(Boost);
        HoldsTheJump(TickLoop.SimulationHz);
    }

    [Fact]
    public void AMenuOverHeldForwardIsNotALetGo()
    {
        Plot();
        _down.Add(Forward);
        _down.Add(Boost);
        HoldsTheJump(10);
        _down.Remove(Boost);
        HoldsTheJump(10);

        // A menu takes the controls for a while; forward stays down under it, and is still part of the start after.
        for (var i = 0; i < 10; i++)
            Step(inputEnabled: false);
        HoldsTheJump(TickLoop.SimulationHz);

        // Let go under the menu, then pressed again once the world has the controls back: that is fresh thrust.
        _down.Clear();
        for (var i = 0; i < 5; i++)
            Step(inputEnabled: false);
        Step();
        Assert.Equal(OrderKind.Jump, Own.Order.Kind);
        _down.Add(Forward);
        HoldsTheJump(TickLoop.SimulationHz);
    }

    [Fact]
    public void RemappedForwardAndBoostAreFollowed()
    {
        // The default file with forward and boost moved to other keys; the old keys do nothing.
        var text = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "content", "input", "bindings.json"))
            .Replace("\"Boost\": [\"Key:Space\"", "\"Boost\": [\"Key:G\"")
            .Replace("\"positive\": \"Key:W\"", "\"positive\": \"Key:I\"");
        var path = Path.Combine(Path.GetTempPath(), $"prospect-bindings-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, text);
        try
        {
            _actions = ActionMap.Load(path);
        }
        finally
        {
            File.Delete(path);
        }

        Assert.Equal(GlfwKey.G, Boost);
        Assert.Equal(GlfwKey.I, Forward);
        _world = new WorldView(_devices, _actions, new ClientSettings(), _jobs, null!, _content.Designs);
        for (var i = 0; i < 5; i++)
            Step();

        Plot();
        _down.Add(Forward);
        for (var i = 0; i < 5; i++)
            Step();
        _down.Add(Boost);
        HoldsTheJump(10);
        _down.Remove(Boost);
        HoldsTheJump(TickLoop.SimulationHz);
    }

    [Fact]
    public void FlyingAnotherHullEndsTheStart()
    {
        Plot();
        _down.Add(Forward);
        _down.Add(Boost);
        HoldsTheJump(10);
        _down.Remove(Boost);

        var snapshot = _client.AcquireSnapshot();
        uint other = 0;
        for (var i = 0; i < snapshot.HullCount && other == 0; i++)
        {
            if (snapshot.Hulls[i].Owner == snapshot.PlayerId && !snapshot.Hulls[i].IsOwn)
                other = snapshot.Hulls[i].Id;
        }

        Assert.NotEqual(0u, other);
        _client.Input.RequestControl(other);
        for (var i = 0; i < 30 && Own.Id != other; i++)
            Step();
        Assert.Equal(other, Own.Id);

        // Forward, held all along, flies the new hull: nothing from the old start holds it back.
        var speed = ForwardSpeed;
        for (var i = 0; i < 20; i++)
            Step();
        Assert.True(ForwardSpeed > speed + 1f, $"forward speed {speed} then {ForwardSpeed}");
    }
}
