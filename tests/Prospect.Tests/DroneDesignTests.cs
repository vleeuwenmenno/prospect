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

        // Half the former 0.5 block edge, so the hull is half as long and wide and half the radius. Its thrusters
        // and tanks fill corners of the same three by three by three cells, so it is no larger than it was.
        Assert.Equal(0.25f, drone.BlockSize);
        Assert.Equal(0.6f, flight.Radius, 0.01f);
        Assert.Equal(new Vector3(0.75f), drone.Stats.Size);

        // Figures that do not come from size: mass, thrust in each direction, shield, hull and power. Six
        // thrusters and two tanks came with directional thrust and fuel.
        Assert.Equal(3.6f, flight.Mass, 0.001f);
        Assert.Equal(16.666666f, flight.Acceleration.Z, 0.001f);
        Assert.Equal(8.333333f, flight.Acceleration.X, 0.001f);
        Assert.Equal(8.333333f, flight.Acceleration.Y, 0.001f);
        Assert.Equal(16.666666f, flight.ReverseAcceleration, 0.001f);
        Assert.Equal(120f, flight.ShieldCapacity, 0.001f);
        Assert.Equal(490f, flight.HullPoints, 0.001f);
        Assert.Equal(1.2f, flight.PowerGenerated, 0.001f);
        Assert.Equal(new FuelTanks { Nitrogen = 24f, Bipropellant = 90f }, flight.FuelCapacity);

        // The gyro's torque was raised with the mass the thrusters and tanks added, so the drone turns about as before.
        Assert.Equal(new Vector3(0.67531f, 0.74611f, 0.78999f), flight.TurnRate, new VectorTolerance());
    }

    private sealed class VectorTolerance : IEqualityComparer<Vector3>
    {
        public bool Equals(Vector3 a, Vector3 b) => (a - b).Length() < 0.001f;

        public int GetHashCode(Vector3 v) => v.GetHashCode();
    }
}
