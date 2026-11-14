using System.Numerics;
using Prospect.Game.World;
using Xunit;

namespace Prospect.Tests;

/// <summary>The escape drone's size and the figures derived from its blocks.</summary>
public sealed class DroneDesignTests
{
    private static readonly GameContent Shipped = GameContent.Load();

    [Fact]
    public void TheDroneIsHalfItsFormerLinearSizeAndKeepsEveryOtherDerivedFigure()
    {
        var drone = Shipped.Designs[Shipped.DroneDesign];
        var flight = drone.Flight;

        // Half the former 0.5 block edge, so the hull is half as long and wide and half the radius.
        Assert.Equal(0.25f, drone.BlockSize);
        Assert.Equal(0.55f, flight.Radius, 0.01f);

        // Figures that do not come from size are what they were: mass, thrust, shield, hull and power.
        Assert.Equal(2.8f, flight.Mass, 0.001f);
        Assert.Equal(16.571428f, flight.Acceleration.Z, 0.001f);
        Assert.Equal(8f, flight.Acceleration.X, 0.001f);
        Assert.Equal(120f, flight.ShieldCapacity, 0.001f);
        Assert.Equal(310f, flight.HullPoints, 0.001f);
        Assert.Equal(1.2f, flight.PowerGenerated, 0.001f);

        // Inertia falls with the square of the size, so the gyro's torque was cut to match and the drone turns as before.
        Assert.Equal(new Vector3(0.66627f, 0.81834f, 0.75911f), flight.TurnRate, new VectorTolerance());
    }

    private sealed class VectorTolerance : IEqualityComparer<Vector3>
    {
        public bool Equals(Vector3 a, Vector3 b) => (a - b).Length() < 0.001f;

        public int GetHashCode(Vector3 v) => v.GetHashCode();
    }
}
