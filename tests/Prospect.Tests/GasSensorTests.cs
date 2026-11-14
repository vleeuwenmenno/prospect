using System.Numerics;
using Prospect.Game.World;
using Xunit;

namespace Prospect.Tests;

public sealed class GasSensorTests
{
    private static readonly GameContent Content = GameContent.Load();
    private static readonly GasCloudPlacement[] Clouds = [new(default, new(Vector3.Zero, new Vector3(1000), 0))];
    private static readonly Double3[] Directions = [new(1, 0, 0), new(-1, 0, 0), new(0, 1, 0), new(0, -1, 0), new(0, 0, 1), new(0, 0, -1)];

    [Fact]
    public void ClearSpaceIsSphereAndInsideShrinksEveryDirection()
    {
        foreach (var direction in Directions)
        {
            Assert.Equal(5000, GasSensors.Reach(Content.Galaxy, [], Content.Exploration, default, direction));
            var reach = GasSensors.Reach(Content.Galaxy, Clouds, Content.Exploration, default, direction);
            Assert.Equal(0x407DD914C2A00000L, BitConverter.DoubleToInt64Bits(reach));
            Assert.True(GasSensors.Covers(Content.Galaxy, Clouds, Content.Exploration, default, direction * reach));
            Assert.False(GasSensors.Covers(Content.Galaxy, Clouds, Content.Exploration, default, direction * (reach + 0.001)));
        }
    }

    [Fact]
    public void EdgeLooksFarOutAndCloudOccludesBehindButNotBefore()
    {
        var edge = new Double3(1000 * Content.Galaxy.GasClouds!.Shape.X, 0, 0);
        var outward = GasSensors.Reach(Content.Galaxy, Clouds, Content.Exploration, edge, Directions[0]);
        var inward = GasSensors.Reach(Content.Galaxy, Clouds, Content.Exploration, edge, Directions[1]);
        Assert.Equal(5000, outward);
        Assert.True(inward < outward);
        var origin = new Double3(-1500, 0, 0);
        Assert.True(GasSensors.Covers(Content.Galaxy, Clouds, Content.Exploration, origin, new(-1000, 0, 0)));
        Assert.False(GasSensors.Covers(Content.Galaxy, Clouds, Content.Exploration, origin, new(1500, 0, 0)));
        Assert.True(GasSensors.Covers(Content.Galaxy, Clouds, Content.Exploration, default, new(250, 0, 0), false));
        Assert.False(GasSensors.Covers(Content.Galaxy, Clouds, Content.Exploration, default, new(251, 0, 0), false));
    }

    [Fact]
    public void WispinessCannotChangeSensorReach()
    {
        var dense = Content.WithSettings(Content.SettingCatalog.Build(null, ["gasWispiness=0"])).Galaxy;
        var wispy = Content.WithSettings(Content.SettingCatalog.Build(null, ["gasWispiness=2"])).Galaxy;
        foreach (var direction in Directions)
            Assert.Equal(GasSensors.Reach(dense, Clouds, Content.Exploration, default, direction),
                GasSensors.Reach(wispy, Clouds, Content.Exploration, default, direction));
    }

    [Fact]
    public void SampleContractIsDeterministicAndAllocationFree()
    {
        var reaches = new double[Directions.Length];
        GasSensors.SampleReach(Content.Galaxy, Clouds, Content.Exploration, default, Directions, reaches);
        var expected = (double[])reaches.Clone();
        var probe = new AllocationProbe();
        for (var i = 0; i < 100; i++)
            while (probe.Measuring()) GasSensors.SampleReach(Content.Galaxy, Clouds, Content.Exploration, default, Directions, reaches);
        Assert.Equal(0, probe.Bytes);
        Assert.Equal(expected, reaches);
    }

    [Fact]
    public void CacheFindsNeighbourCloudAcrossWallAndDoesNotAllocateOnMiss()
    {
        var galaxy = Content.Galaxy.WithSeed(0x50524F5350454354);
        var cache = new SensorGasCache(galaxy, Content.Exploration.SensorRadius);
        var anchor = new SectorCoord(133, 0, -77);
        Assert.True(galaxy.GasClouds!.TryGetCloud(galaxy, anchor, out var cloud));
        var placement = new GasCloudPlacement(anchor, cloud);
        var position = GasCloudQuery.CenterOf(galaxy, placement);
        Assert.Contains(placement, cache.At(position).ToArray());
        var neighbour = galaxy.CenterOf(anchor) + new Double3(-galaxy.SectorSize * 0.5 - 1, 0, 0);
        Assert.NotEqual(anchor, galaxy.SectorOf(neighbour));
        Assert.Contains(placement, cache.At(neighbour).ToArray());
        var probe = new AllocationProbe();
        for (var i = 0; i < 20; i++)
            while (probe.Measuring()) cache.At(position + new Double3(i * galaxy.SectorSize, 0, 0));
        Assert.Equal(0, probe.Bytes);
    }
}
