using System.Numerics;
using Prospect.Client.World;
using Prospect.Game.World;
using Xunit;

namespace Prospect.Tests;

/// <summary>
/// How far a hyperspace arrival's pulse reaches, from the hull's derived mass and radius and the tuning in
/// <c>content/blocks/blocks.json</c>. Only the rule is checked here; how the pulse looks needs a person at the screen.
/// </summary>
public sealed class ArrivalPulseTests
{
    private static readonly GameContent Content = GameContent.Load();

    private static FlightParams Hull(float mass, float radius) => new() { Mass = mass, Radius = radius };

    [Fact]
    public void ArrivalLightStaysBehindTheAnchorInWorldSpace()
    {
        var anchor = new Double3(1e12, -2e12, 3e12);
        var axis = Vector3.Normalize(new Vector3(2f, 3f, -4f));
        var arrival = WorldView.HyperspaceMarkPosition(anchor, axis, 10f, false);
        Assert.True(Vector3.Distance(-axis * 15f, arrival.RelativeTo(anchor)) < 0.001f);
        var charge = WorldView.HyperspaceMarkPosition(anchor, axis, 10f, true);
        Assert.True(Vector3.Distance(axis * (10f * HullVisuals.HyperspaceLight), charge.RelativeTo(anchor)) < 0.001f);
    }

    [Fact]
    public void HeavierHullsReachFurtherUpToTheContentsBound()
    {
        var rules = Content.Rules;
        var previous = 0f;
        foreach (var mass in (ReadOnlySpan<float>)[0f, 1f, 10f, 30f, 100f, 1_000f, 100_000f])
        {
            var reach = WorldView.ArrivalReach(Hull(mass, 3f), rules);
            Assert.True(reach >= WorldView.MarkReach && reach < rules.FtlArrivalReach, $"{mass} t reaches {reach}");
            Assert.True(reach >= previous, $"{mass} t reaches {reach}, less than a lighter hull's {previous}");
            previous = reach;
        }

        Assert.Equal(WorldView.MarkReach, WorldView.ArrivalReach(Hull(0f, 3f), rules));
        Assert.Equal((WorldView.MarkReach + rules.FtlArrivalReach) / 2f, WorldView.ArrivalReach(Hull(rules.FtlArrivalPulseMass, 3f), rules), 4);
        // A broken or empty mass is the smallest pulse, never a negative or runaway one.
        Assert.Equal(WorldView.MarkReach, WorldView.ArrivalReach(Hull(-5f, 3f), rules));
        Assert.Equal(WorldView.MarkReach, WorldView.ArrivalReach(Hull(float.NaN, 3f), rules));
    }

    [Fact]
    public void TheTuningMovesTheReachAndNeverPastItsBounds()
    {
        var rules = Content.Rules;
        var hull = Hull(200f, 3f);
        var wider = WorldView.ArrivalReach(hull, rules with { FtlArrivalReach = 10f });
        Assert.True(wider > WorldView.ArrivalReach(hull, rules with { FtlArrivalReach = 5f }));
        Assert.True(WorldView.ArrivalReach(hull, rules with { FtlArrivalPulseMass = 20f }) > WorldView.ArrivalReach(hull, rules with { FtlArrivalPulseMass = 2_000f }));
        Assert.True(WorldView.ArrivalReach(Hull(1e6f, 3f), rules with { FtlArrivalReach = 1_000f }) <= FlightRules.MaxArrivalReach);
        Assert.Equal(WorldView.MarkReach, WorldView.ArrivalReach(hull, rules with { FtlArrivalReach = 0f }));
    }

    [Fact]
    public void TheReachIsInHullRadiiSoBiggerHullsReachFurtherInMetres()
    {
        // The rule gives radii; the mark is that many of the hull's radii across, so at the same mass a hull twice
        // the size sends a pulse twice as far.
        var small = Hull(50f, 2f);
        var large = Hull(50f, 4f);
        Assert.Equal(WorldView.ArrivalReach(small, Content.Rules), WorldView.ArrivalReach(large, Content.Rules));
        Assert.Equal(2f * small.Radius * WorldView.ArrivalReach(small, Content.Rules), large.Radius * WorldView.ArrivalReach(large, Content.Rules), 4);
    }

    [Fact]
    public void EveryShippedDesignArrivesWithinTheBound()
    {
        foreach (var design in Content.Designs)
        {
            var reach = WorldView.ArrivalReach(design.Flight, Content.Rules);
            Assert.True(design.Flight.Mass > 0f, design.Name);
            Assert.True(reach > WorldView.MarkReach && reach < Content.Rules.FtlArrivalReach, $"{design.Name}: {reach}");
        }
    }

    [Theory]
    [InlineData("\"ftlArrivalReach\": 6.0", "\"ftlArrivalReach\": 2.0")]
    [InlineData("\"ftlArrivalReach\": 6.0", "\"ftlArrivalReach\": 40.0")]
    [InlineData("\"ftlArrivalPulseMass\": 100.0", "\"ftlArrivalPulseMass\": 0")]
    [InlineData("\"ftlArrivalPulseMass\": 100.0", "\"ftlArrivalPulseMass\": -3")]
    public void TuningOutsideItsBoundsIsRefused(string authored, string broken)
    {
        var text = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "content", "blocks", "blocks.json"));
        Assert.Contains(authored, text);
        var path = Path.Combine(Path.GetTempPath(), $"prospect-blocks-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, text.Replace(authored, broken));
        try
        {
            Assert.Throws<InvalidDataException>(() => BlockCatalog.Load(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
