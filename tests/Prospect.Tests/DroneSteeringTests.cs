using System.Numerics;
using Prospect.Game.World;
using Xunit;

namespace Prospect.Tests;

/// <summary>The shared rules a drone is steered by: aim in a moving frame, keeping apart, going round a hull, carry speed.</summary>
public sealed class DroneSteeringTests
{
    private static readonly Double3 Origin = new(1000, -2000, 500);

    [Fact]
    public void TheTargetMakesTheAutopilotAskForTheWantedVelocity()
    {
        const float push = 5f;
        var wanted = new Vector3(3, -4, 12);
        var moving = new Vector3(0, 0, 8);
        var target = DroneSteering.Target(Origin, moving, wanted, push);
        var way = target.RelativeTo(Origin);

        // Along the wanted velocity, as far as the autopilot's own speed rule asks for exactly that speed.
        Assert.True(Vector3.Dot(Vector3.Normalize(way), Vector3.Normalize(wanted)) > 0.99999f);
        Assert.Equal(wanted.Length(), 0.8f * MathF.Sqrt(2f * push * way.Length()), 3);

        // Wanted nothing: the drone's own spot. A place in a frame at rest is the place itself.
        Assert.Equal(Origin, DroneSteering.Target(Origin, moving, Vector3.Zero, push));
        var place = Origin + new Vector3(30, 0, 0);
        var still = DroneSteering.Wanted(Origin, place, Vector3.Zero, push);
        Assert.True(DroneSteering.Target(Origin, moving, still, push).RelativeTo(place).Length() < 1e-3f);

        // At its place in a moving frame the drone is asked for the frame's own velocity.
        Assert.Equal(moving, DroneSteering.Wanted(place, place, moving, push));
    }

    [Fact]
    public void ASlowDroneIsNeverGivenATargetItWouldCountAsReached()
    {
        // At rest and asked for 2 m/s, the exact target would lie 0.2 m off: the autopilot counts that as arrived and
        // stops steering, so the drone would never move. It is handed one just past the arrival distance instead.
        var wanted = new Vector3(2, 0, 0);
        var near = DroneSteering.Target(Origin, Vector3.Zero, wanted, 5f);
        Assert.True(near.RelativeTo(Origin).Length() > Autopilot.ArrivalDistance);
        // Moving already, the target is exact; and a wish slower than the arrival speed is left as it is.
        Assert.True(DroneSteering.Target(Origin, new Vector3(3, 0, 0), wanted, 5f).RelativeTo(Origin).Length() < 1f);
        Assert.True(DroneSteering.Target(Origin, Vector3.Zero, new Vector3(1, 0, 0), 5f).RelativeTo(Origin).Length() < 1f);
        // A way point is pushed out past twice the arrival distance; one farther is left alone.
        var beyond = DroneSteering.Beyond(Origin, Origin + new Vector3(0, 2, 0));
        Assert.Equal(2 * Autopilot.ArrivalDistance, beyond.RelativeTo(Origin).Length(), 4);
        Assert.Equal(Origin + new Vector3(0, 40, 0), DroneSteering.Beyond(Origin, Origin + new Vector3(0, 40, 0)));
    }

    [Fact]
    public void DronesClosingOnEachOtherArePushedApartBeforeTheyMeet()
    {
        // Far apart, or passing wide: nothing.
        Assert.Equal(Vector3.Zero, DroneSteering.Apart(new Vector3(10, 0, 0), Vector3.Zero, 1.5f, 4f, 0.75f));
        Assert.Equal(Vector3.Zero, DroneSteering.Apart(new Vector3(4, 3, 0), new Vector3(-8, 0, 0), 1.5f, 4f, 0.75f));
        // Closing head on from 3 m at 6 m/s: pushed already, to the side they would pass on.
        var push = DroneSteering.Apart(new Vector3(3, 0.2f, 0), new Vector3(-6, 0, 0), 1.5f, 4f, 0.75f);
        Assert.True(push.Length() > 3f);
        Assert.True(push.Y > 0);
        // Side by side and still: pushed straight apart, harder the nearer.
        var close = DroneSteering.Apart(new Vector3(0, 0.5f, 0), Vector3.Zero, 1.5f, 4f, 0.75f);
        var closer = DroneSteering.Apart(new Vector3(0, 0.2f, 0), Vector3.Zero, 1.5f, 4f, 0.75f);
        Assert.True(close.Y > 0 && closer.Y > close.Y);
        // In the same spot: a fixed axis, so every machine agrees.
        Assert.Equal(new Vector3(0, 4, 0), DroneSteering.Apart(Vector3.Zero, Vector3.Zero, 1.5f, 4f, 0.75f));
    }

    [Fact]
    public void AWayThroughTheHullGoesRoundIt()
    {
        var centre = Origin;
        const float radius = 8f;
        var below = centre + new Vector3(0.5f, -60, 0);
        var above = centre + new Vector3(0, 60, 0);

        // Clear on one side: straight.
        Assert.False(DroneSteering.Around(centre + new Vector3(30, -40, 0), centre + new Vector3(30, 40, 0), centre, radius, out _));

        // Straight through: a way point on the sphere's side, past its surface.
        Assert.True(DroneSteering.Around(below, above, centre, radius, out var round));
        var off = round.RelativeTo(centre);
        Assert.True(off.Length() > radius);
        Assert.True(MathF.Abs(off.Y) < 1f);

        // From inside, out first, straight away from the centre.
        Assert.True(DroneSteering.Around(centre + new Vector3(0, 2, 0), below, centre, radius, out var leave));
        Assert.True(Vector3.Normalize(leave.RelativeTo(centre)).Y > 0.99f);

        // A target inside the sphere is come at from its own side: from far below to a point just above the centre, first
        // to the surface over it; within the cone over it, straight in.
        var hatch = centre + new Vector3(0, 6, 0);
        Assert.True(DroneSteering.Around(below, hatch, centre, radius, out var entry));
        Assert.True(entry.RelativeTo(centre).Length() > radius);
        Assert.True(entry.RelativeTo(centre).Y > -1f);
        Assert.False(DroneSteering.Around(above, hatch, centre, radius, out _));
        // Both inside: straight.
        Assert.False(DroneSteering.Around(centre + new Vector3(0, -3, 0), hatch, centre, radius, out _));
    }

    [Fact]
    public void ACarryIsNeverFasterThanItsDronesCanFollow()
    {
        // Lifting just its mass, the figure itself; more drones than needed, faster, until the weakest's share of push.
        Assert.Equal(2f, DroneFetch.CarryAcceleration(2f, 5000, 5000, 0.6f, 5f), 5);
        Assert.Equal(2.5f, DroneFetch.CarryAcceleration(2f, 10000, 8000, 0.6f, 5f), 5);
        Assert.Equal(3f, DroneFetch.CarryAcceleration(2f, 5000, 300, 0.6f, 5f), 5);

        // The carried body heads round the hull, not into it; on a clear way, straight to the hand-over point.
        var centre = Origin;
        var handover = centre + new Vector3(0, 12, 0);
        var half = new Vector3(1, 1, 1);
        var goal = DroneFetch.CarryGoal(centre + new Vector3(0, -80, 0), half, handover, centre, 6f, 1.5f);
        Assert.NotEqual(handover, goal);
        Assert.True(goal.RelativeTo(centre).Length() > 6f + half.Length());
        Assert.Equal(handover, DroneFetch.CarryGoal(centre + new Vector3(0, 80, 0), half, handover, centre, 6f, 1.5f));
    }
}
