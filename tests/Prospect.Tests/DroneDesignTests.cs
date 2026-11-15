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

        // Half the former 0.5 block edge. Its engine stands one cell behind the rest on a plate, so the hull is
        // four cells long and three wide and high; its thrusters are mounted on faces and fill none.
        Assert.Equal(0.25f, drone.BlockSize);
        Assert.Equal(0.620f, flight.Radius, 0.01f);
        Assert.Equal(new Vector3(0.75f, 0.75f, 1f), drone.Stats.Size);

        // Figures that do not come from size: mass, thrust in each direction, shield, hull and power. Two tanks
        // came with fuel, twenty-four mounted thrusters weigh and push what six thruster blocks did, and a plate
        // carries the engine. A mounted part has no hit points of its own.
        Assert.Equal(3.75f, flight.Mass, 0.001f);
        Assert.Equal(16f, flight.Acceleration.Z, 0.001f);
        Assert.Equal(8f, flight.Acceleration.X, 0.001f);
        Assert.Equal(8f, flight.Acceleration.Y, 0.001f);
        Assert.Equal(16f, flight.ReverseAcceleration, 0.001f);
        Assert.Equal(120f, flight.ShieldCapacity, 0.001f);
        Assert.Equal(340f, flight.HullPoints, 0.001f);
        Assert.Equal(1.2f, flight.PowerGenerated, 0.001f);
        Assert.Equal(new FuelTanks { Nitrogen = 24f, Bipropellant = 90f }, flight.FuelCapacity);

        // The engine's plate and the longer lever behind it make the drone turn a little slower than before.
        Assert.Equal(new Vector3(0.60820f, 0.70618f, 0.69959f), flight.TurnRate, new VectorTolerance());
    }

    private sealed class VectorTolerance : IEqualityComparer<Vector3>
    {
        public bool Equals(Vector3 a, Vector3 b) => (a - b).Length() < 0.001f;

        public int GetHashCode(Vector3 v) => v.GetHashCode();
    }
}
