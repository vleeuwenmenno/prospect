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
        Assert.Equal(0.7688f, flight.Radius, 0.01f);
        Assert.Equal(new Vector3(0.75f, 0.75f, 1f), drone.Stats.Size);

        // Figures that do not come from size: mass, thrust in each direction, shield, hull and power. The drone is
        // electric: a two-cell ion drive and twenty-four ion thrusters, no tank. A mounted
        // part has no hit points of its own.
        Assert.Equal(5.17f, flight.Mass, 0.001f);
        Assert.Equal(60f / 5.17f, flight.Acceleration.Z, 0.001f);
        Assert.Equal(15f / 5.17f, flight.Acceleration.X, 0.001f);
        Assert.Equal(15f / 5.17f, flight.Acceleration.Y, 0.001f);
        Assert.Equal(30f / 5.17f, flight.ReverseAcceleration, 0.001f);
        Assert.Equal(120f, flight.ShieldCapacity, 0.001f);
        Assert.Equal(500f, flight.HullPoints, 0.001f);
        Assert.Equal(2.4f, flight.PowerGenerated, 0.001f);
        Assert.Equal(default, flight.FuelCapacity);
        Assert.True(drone.Tanks.IsEmpty);

        // Power: two generators carry the double-length ion drive (1.8 MW at full forward thrust) and the mounted
        // thrusters; the shield's 0.7 MW draw keeps a restart above the surplus (PowerFlowTests).
        Assert.Equal(0.2f, flight.IdlePower, 0.0001f);
        Assert.Equal(1.8f, flight.IonPower[BlockFacing.Front], 0.0001f);
        Assert.Equal(0.9f, flight.IonPower[BlockFacing.Back], 0.0001f);
        Assert.Equal(0.45f, flight.IonPower[BlockFacing.Right], 0.0001f);
        Assert.Equal(0.45f, flight.IonPower[BlockFacing.Up], 0.0001f);
        Assert.Equal(2.7f, flight.ThrustPower, 0.0001f);

        // Three gyros (above and to each side of the core) turn it faster than the earlier single-gyro drone did
        // (0.64, 0.74, 0.74), though it is heavier.
        Assert.Equal(new Vector3(0.89077f, 0.90031f, 1.03566f), flight.TurnRate, new VectorTolerance());
    }

    private sealed class VectorTolerance : IEqualityComparer<Vector3>
    {
        public bool Equals(Vector3 a, Vector3 b) => (a - b).Length() < 0.001f;

        public int GetHashCode(Vector3 v) => v.GetHashCode();
    }
}
