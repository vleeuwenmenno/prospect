using System.Numerics;
using Prospect.Engine.Net;
using Prospect.Engine.Time;
using Prospect.Game.Client;
using Prospect.Game.Server;
using Prospect.Game.World;
using Xunit;

namespace Prospect.Tests;

/// <summary>
/// A started jump is ended by halt and nothing else: not by boost pressed, held, tapped or let go, not by thrust.
/// The shared step ignores the pilot's input while the drive works, and <see cref="HullSim.InputEndsOrder"/> is the one
/// place that decides whether input cancels an order, for the server's records and the client's prediction alike.
/// </summary>
public sealed class JumpInputTests
{
    private static readonly GameContent Content = GameContent.Load();
    private static readonly Double3 Origin = new(1e12, -1e12, 1e12);

    /// <summary>Boost pressed, held, let go, tapped and let go, then each kind of thrust, over twelve ticks.</summary>
    private static FlightInput Pattern(int tick) => (tick % 12) switch
    {
        0 or 1 or 2 or 3 => new FlightInput { Boost = true, Dampeners = true },
        4 or 5 => new FlightInput { Dampeners = true },
        6 => new FlightInput { Boost = true },
        7 => default,
        8 => new FlightInput { Forward = 127, Boost = true },
        9 => new FlightInput { Strafe = -127, Vertical = 127 },
        10 => new FlightInput { Roll = 127, Forward = -127 },
        _ => new FlightInput { Yaw = 127, Pitch = -127, Boost = true },
    };

    /// <summary>A hull partway through the given jump stage, with its order. Align takes a target behind the nose.</summary>
    private static (HullState State, HullOrder Order, FlightParams Flight) InStage(JumpPhase stage)
    {
        var flight = Content.Pod.Flight;
        var state = HullState.Spawn(Origin, flight);
        var order = new HullOrder { Kind = OrderKind.Jump, Target = Origin + new Double3(0, 0, stage == JumpPhase.Align ? 100000 : -100000) };
        var guard = 0;
        while (state.Jump != stage && guard++ < 20000)
            HullSim.Step(ref state, ref order, default, flight);
        Assert.Equal(stage, state.Jump);
        // A few ticks into it, so there is progress to lose.
        for (var i = 0; i < 5 && state.Jump == stage; i++)
            HullSim.Step(ref state, ref order, default, flight);
        Assert.Equal(stage, state.Jump);
        return (state, order, flight);
    }

    [Theory]
    [InlineData(JumpPhase.Align)]
    [InlineData(JumpPhase.Charge)]
    [InlineData(JumpPhase.Tunnel)]
    [InlineData(JumpPhase.Arrive)]
    public void BoostAndThrustPressedHeldTappedAndLetGoChangeNothingOfAJumpInAnyStage(JumpPhase stage)
    {
        var (state, order, flight) = InStage(stage);
        var (quiet, quietOrder) = (state, order);
        var input = (State: state, Order: order);
        var ticks = 0;
        while (quiet.Jump == stage && ticks < 4000)
        {
            var pilot = Pattern(ticks);
            Assert.False(HullSim.InputEndsOrder(input.Order, pilot));
            HullSim.Step(ref input.State, ref input.Order, pilot, flight);
            HullSim.Step(ref quiet, ref quietOrder, default, flight);
            Assert.Equal(quiet.Jump, input.State.Jump);
            Assert.Equal(quiet.JumpTimer, input.State.JumpTimer);
            Assert.Equal(quiet.JumpCalculation, input.State.JumpCalculation);
            Assert.Equal(quiet.JumpTunnelElapsed, input.State.JumpTunnelElapsed);
            Assert.Equal(quietOrder.Kind, input.Order.Kind);
            Assert.Equal(quietOrder.Target, input.Order.Target);
            // Once the hull is out of the drive's hands (arrival) the pilot flies it again; before that nothing moves it.
            if (stage != JumpPhase.Arrive)
                Assert.Equal(quiet.Position, input.State.Position);
            ticks++;
        }

        Assert.True(ticks > 0);
        Assert.NotEqual(stage, quiet.Jump);
    }

    [Fact]
    public void ThePatternFromTheOrderOnReachesTheTunnelAtTheTarget()
    {
        var flight = Content.Pod.Flight;
        var state = HullState.Spawn(Origin, flight);
        var target = Origin + new Double3(0, 0, -100000);
        var order = new HullOrder { Kind = OrderKind.Jump, Target = target };
        var entered = false;
        for (var i = 0; i < 20000 && !entered; i++)
        {
            HullSim.Step(ref state, ref order, Pattern(i), flight);
            entered = state.Jump == JumpPhase.Tunnel;
        }

        Assert.True(entered);
        Assert.Equal(target, state.Position);
    }

    [Theory]
    [InlineData(JumpPhase.Align)]
    [InlineData(JumpPhase.Charge)]
    public void AnExplicitCancelEndsAJumpBeforeTheDriveFiresAndLosesTheCharge(JumpPhase stage)
    {
        var (state, order, flight) = InStage(stage);
        HullSim.SetOrder(ref state, ref order, default);
        HullSim.Step(ref state, ref order, default, flight);
        Assert.Equal(JumpPhase.None, state.Jump);
        Assert.Equal(OrderKind.None, order.Kind);
        Assert.Equal(0f, state.JumpCalculation);
        Assert.Equal(0f, state.JumpTimer);
    }

    [Fact]
    public void ACancelOnceTheHullIsInTheTunnelCannotBringItBack()
    {
        var (state, order, flight) = InStage(JumpPhase.Tunnel);
        var target = state.Position;
        HullSim.SetOrder(ref state, ref order, default);
        var ticks = 0;
        while (state.Jump == JumpPhase.Tunnel && ticks++ < 4000)
            HullSim.Step(ref state, ref order, default, flight);
        Assert.Equal(JumpPhase.Arrive, state.Jump);
        Assert.Equal(target, state.Position);
    }

    [Theory]
    [InlineData(OrderKind.Jump, 0, 0, 0, 0, false)]
    [InlineData(OrderKind.Jump, 127, 127, 127, 127, false)]
    [InlineData(OrderKind.None, 127, 127, 127, 127, false)]
    [InlineData(OrderKind.MoveTo, 0, 0, 0, 0, false)]
    [InlineData(OrderKind.MoveTo, 1, 0, 0, 0, true)]
    [InlineData(OrderKind.MoveTo, 0, 1, 0, 0, true)]
    [InlineData(OrderKind.MoveTo, 0, 0, -1, 0, true)]
    [InlineData(OrderKind.MoveTo, 0, 0, 0, 1, true)]
    public void OnlyAMoveOrderIsEndedByTheControls(OrderKind kind, int strafe, int vertical, int forward, int roll, bool ends)
    {
        var input = new FlightInput { Strafe = (sbyte)strafe, Vertical = (sbyte)vertical, Forward = (sbyte)forward, Roll = (sbyte)roll };
        Assert.Equal(ends, HullSim.InputEndsOrder(new HullOrder { Kind = kind }, input));
        // Boost, pitch and yaw were never the pilot taking the controls.
        Assert.False(HullSim.InputEndsOrder(new HullOrder { Kind = kind }, new FlightInput { Boost = true, Pitch = 127, Yaw = 127 }));
    }

    /// <summary>
    /// A real server and client: the pilot's controls change every tick (boost every way, thrust on every axis) from
    /// the order to the landing, and the server and the prediction keep the jump: the order stands, the calculation
    /// never goes back, the hull lands on the target. Then halt, sent as an order, does cancel.
    /// </summary>
    [Fact]
    public void ControlsThroughoutAJumpNeverCancelItOnTheServerOrInPredictionAndHaltDoes()
    {
        using var link = new LoopbackLink();
        using var server = new ServerHost(new ServerConfig { MaxPlayers = 1 }, Content, link.Server);
        using var client = new ClientSession(link.Client, Content, "Pilot");
        long tick = 0;
        void Step(int i)
        {
            var pilot = Pattern(i);
            client.Input.Submit(new PlayerControls
            {
                Boost = pilot.Boost,
                Strafe = pilot.Strafe / 127f,
                Vertical = pilot.Vertical / 127f,
                Forward = pilot.Forward / 127f,
                Roll = pilot.Roll / 127f,
            });
            ((ITickHandler)server).Tick(tick++);
            ((ITickHandler)client).Tick(0);
        }
        HullView Own()
        {
            var view = client.AcquireSnapshot();
            return view.Hulls[view.OwnHull];
        }

        for (var i = 0; i < 10; i++) Step(0);
        var own = Own();
        var nose = Vector3.Transform(-Vector3.UnitZ, own.Orientation);
        var target = own.Position + nose * 200000f;
        client.Input.IssueOrder(new HullOrder { Kind = OrderKind.Jump, Target = target });

        var calculation = 0f;
        var tunnel = false;
        var started = false;
        for (var i = 0; i < TickLoop.SimulationHz * 90; i++)
        {
            Step(i);
            own = Own();
            started |= own.Jump != JumpPhase.None;
            if (started && own.Jump == JumpPhase.None)
                break;
            tunnel |= own.Jump == JumpPhase.Tunnel;
            if (own.Jump is JumpPhase.Align or JumpPhase.Charge)
            {
                Assert.Equal(OrderKind.Jump, own.Order.Kind);
                Assert.True(own.JumpCalculation >= calculation, $"tick {i}: calculation went from {calculation} to {own.JumpCalculation}");
                calculation = own.JumpCalculation;
            }
        }

        Assert.True(tunnel);
        Assert.True(Own().Position.RelativeTo(target).Length() < 1f);

        // Explicit cancel: a second jump, halted with the same controls still changing.
        var second = Own().Position + Vector3.Transform(-Vector3.UnitZ, Own().Orientation) * 200000f;
        client.Input.IssueOrder(new HullOrder { Kind = OrderKind.Jump, Target = second });
        for (var i = 0; i < TickLoop.SimulationHz * 3; i++)
            Step(i);
        Assert.Equal(JumpPhase.Charge, Own().Jump);
        client.Input.IssueOrder(default);
        for (var i = 0; i < 20; i++)
            Step(i);
        Assert.Equal(JumpPhase.None, Own().Jump);
        Assert.Equal(OrderKind.None, Own().Order.Kind);
        Assert.Equal(0f, Own().JumpCalculation);
    }
}
