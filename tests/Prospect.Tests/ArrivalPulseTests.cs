using Prospect.Client.World;
using Prospect.Game.World;
using Xunit;

namespace Prospect.Tests;

/// <summary>
/// How far a hyperspace arrival's pulse reaches, from the hull's derived mass and radius. Only the rule is checked
/// here; how the pulse looks needs a person at the screen.
/// </summary>
public sealed class ArrivalPulseTests
{
    private static FlightParams Hull(float mass, float radius) => new() { Mass = mass, Radius = radius };

    [Fact]
    public void HeavierHullsReachFurtherUpToABound()
    {
        var previous = 0f;
        foreach (var mass in (ReadOnlySpan<float>)[0f, 1f, 10f, 30f, 100f, 1_000f, 100_000f])
        {
            var reach = WorldView.ArrivalReach(Hull(mass, 3f));
            Assert.True(reach >= WorldView.MarkReach && reach < WorldView.ArrivalReachLimit, $"{mass} t reaches {reach}");
            Assert.True(reach >= previous, $"{mass} t reaches {reach}, less than a lighter hull's {previous}");
            previous = reach;
        }

        Assert.Equal(WorldView.MarkReach, WorldView.ArrivalReach(Hull(0f, 3f)));
        Assert.Equal((WorldView.MarkReach + WorldView.ArrivalReachLimit) / 2f, WorldView.ArrivalReach(Hull(WorldView.ArrivalPulseMass, 3f)), 4);
        // A broken or empty mass is the smallest pulse, never a negative or runaway one.
        Assert.Equal(WorldView.MarkReach, WorldView.ArrivalReach(Hull(-5f, 3f)));
    }

    [Fact]
    public void TheReachIsInHullRadiiSoBiggerHullsReachFurtherInMetres()
    {
        // The rule gives radii; the mark is that many of the hull's radii across, so at the same mass a hull twice
        // the size sends a pulse twice as far.
        var small = Hull(50f, 2f);
        var large = Hull(50f, 4f);
        Assert.Equal(WorldView.ArrivalReach(small), WorldView.ArrivalReach(large));
        Assert.Equal(2f * small.Radius * WorldView.ArrivalReach(small), large.Radius * WorldView.ArrivalReach(large), 4);
    }

    [Fact]
    public void EveryShippedDesignArrivesWithinTheBound()
    {
        foreach (var design in GameContent.Load().Designs)
        {
            var reach = WorldView.ArrivalReach(design.Flight);
            Assert.True(design.Flight.Mass > 0f, design.Name);
            Assert.True(reach > WorldView.MarkReach && reach < WorldView.ArrivalReachLimit, $"{design.Name}: {reach}");
        }
    }
}
