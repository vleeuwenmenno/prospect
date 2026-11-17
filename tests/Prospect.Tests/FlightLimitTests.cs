using System.Numerics;
using Prospect.Game.World;
using Xunit;

namespace Prospect.Tests;

/// <summary>
/// The rated speed caps what thrust can add, and only the dampeners bleed speed above it: boosting, cooling and
/// boosting again cannot ratchet a hull past its limit, and with the dampeners off a fast hull keeps its speed.
/// </summary>
public sealed class FlightLimitTests
{
    private const float Epsilon = 1e-2f;

    private static readonly FlightParams Pod = GameContent.Load().Pod.Flight;

    /// <summary>A hull at rest-spawn heading, flying forward at a speed, with the battery and tanks full.</summary>
    private static HullState Moving(float speed)
    {
        var state = HullState.Spawn(Double3.Zero, Pod);
        state.Velocity = state.Forward * speed;
        return state;
    }

    /// <summary>Steps the hull, topping up the energy each tick so the battery never decides the outcome.</summary>
    private static void Fly(ref HullState state, in FlightInput input, int ticks, bool refill = true)
    {
        for (var i = 0; i < ticks; i++)
        {
            if (refill)
                state.Energy = Pod.EnergyStorage;
            FlightModel.Step(ref state, input, Pod, HullSim.Dt);
        }
    }

    [Fact]
    public void RepeatedBoostAndCoolCyclesNeverPassTheBoostLimit()
    {
        var state = Moving(Pod.MaxSpeed);
        var fastest = 0f;
        var boosted = false;
        for (var i = 0; i < 60 * 60; i++)
        {
            state.Energy = Pod.EnergyStorage;
            state.Fuel = Pod.FuelCapacity;
            FlightModel.Step(ref state, new FlightInput { Forward = 127, Boost = true }, Pod, HullSim.Dt);
            boosted |= state.Boosting;
            fastest = Math.Max(fastest, state.Velocity.Length());
        }

        Assert.True(boosted);
        Assert.True(fastest > Pod.MaxSpeed * 1.1f, $"boost never raised the speed: {fastest}");
        Assert.True(fastest <= Pod.BoostMaxSpeed + Epsilon, $"{fastest} passed {Pod.BoostMaxSpeed}");
    }

    [Fact]
    public void ThrustAddsNothingWhileAboveTheLimitAndTheDampenersAreOff()
    {
        var state = Moving(Pod.BoostMaxSpeed);
        var before = state.Velocity.Length();
        Fly(ref state, new FlightInput { Forward = 127 }, 60 * 30);
        Assert.Equal(before, state.Velocity.Length(), 1);
    }

    [Fact]
    public void WithTheDampenersOffAHullKeepsItsSpeed()
    {
        var state = Moving(Pod.BoostMaxSpeed * 1.5f);
        var before = state.Velocity.Length();
        Fly(ref state, default, 60 * 30);
        Assert.Equal(before, state.Velocity.Length(), 2);
    }

    [Fact]
    public void WithTheDampenersOnTheExcessShedsToTheRatedSpeed()
    {
        var state = Moving(Pod.BoostMaxSpeed);
        var input = new FlightInput { Dampeners = true, Forward = 127 };
        var last = state.Velocity.Length();
        for (var i = 0; i < 60 * 120 && state.Velocity.Length() > Pod.MaxSpeed + Epsilon; i++)
        {
            Fly(ref state, input, 1);
            var now = state.Velocity.Length();
            Assert.True(now <= last + Epsilon, "speed rose while shedding");
            last = now;
        }

        Assert.Equal(Pod.MaxSpeed, state.Velocity.Length(), 1);
    }

    [Fact]
    public void SidewaysThrustAboveTheLimitTurnsTheVelocityWithoutRaisingItsSpeed()
    {
        var state = Moving(Pod.BoostMaxSpeed);
        var before = state.Velocity.Length();
        Fly(ref state, new FlightInput { Strafe = 127 }, 60 * 20);
        Assert.True(state.Velocity.Length() <= before + Epsilon);
        Assert.True(Vector3.Dot(Vector3.Normalize(state.Velocity), state.Forward) < 0.99f, "the velocity never turned");
        Assert.True(state.Velocity.X > 1f, "the hull did not move toward the strafe side");
    }

    [Fact]
    public void RestStaysAtRest()
    {
        var state = HullState.Spawn(Double3.Zero, Pod);
        Fly(ref state, new FlightInput { Dampeners = true }, 60);
        Assert.Equal(Vector3.Zero, state.Velocity);
        Fly(ref state, default, 60);
        Assert.Equal(Vector3.Zero, state.Velocity);
    }
}
