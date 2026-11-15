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

        // Half the former 0.5 block edge. Its drive stands one cell behind the rest on a plate, so the hull is
        // four cells long and three wide and high; its thrusters are mounted on faces and fill none.
        Assert.Equal(0.25f, drone.BlockSize);
        Assert.Equal(0.620f, flight.Radius, 0.01f);
        Assert.Equal(new Vector3(0.75f, 0.75f, 1f), drone.Stats.Size);

        // Figures that do not come from size: mass, thrust in each direction, shield, hull and power. The drone is
        // electric: an ion drive and twenty-four ion thrusters, no tank, plating where its two tanks were. A mounted
        // part has no hit points of its own.
        Assert.Equal(3.52f, flight.Mass, 0.001f);
        Assert.Equal(30f / 3.52f, flight.Acceleration.Z, 0.001f);
        Assert.Equal(15f / 3.52f, flight.Acceleration.X, 0.001f);
        Assert.Equal(15f / 3.52f, flight.Acceleration.Y, 0.001f);
        Assert.Equal(30f / 3.52f, flight.ReverseAcceleration, 0.001f);
        Assert.Equal(120f, flight.ShieldCapacity, 0.001f);
        Assert.Equal(340f, flight.HullPoints, 0.001f);
        Assert.Equal(1.2f, flight.PowerGenerated, 0.001f);
        Assert.Equal(default, flight.FuelCapacity);
        Assert.True(drone.Tanks.IsEmpty);

        // Power: forward thrust on the generator, with room to spare after the core and gyro.
        Assert.Equal(0.1f, flight.IdlePower, 0.0001f);
        Assert.Equal(0.9f, flight.IonPower[BlockFacing.Front], 0.0001f);
        Assert.Equal(0.9f, flight.IonPower[BlockFacing.Back], 0.0001f);
        Assert.Equal(0.45f, flight.IonPower[BlockFacing.Right], 0.0001f);
        Assert.Equal(0.45f, flight.IonPower[BlockFacing.Up], 0.0001f);
        Assert.Equal(1.8f, flight.ThrustPower, 0.0001f);

        // Lighter parts at the sides and behind turn it a little faster than the fuelled drone did (0.61, 0.71, 0.70).
        Assert.Equal(new Vector3(0.63573f, 0.74397f, 0.73570f), flight.TurnRate, new VectorTolerance());
    }

    private sealed class VectorTolerance : IEqualityComparer<Vector3>
    {
        public bool Equals(Vector3 a, Vector3 b) => (a - b).Length() < 0.001f;

        public int GetHashCode(Vector3 v) => v.GetHashCode();
    }
}
