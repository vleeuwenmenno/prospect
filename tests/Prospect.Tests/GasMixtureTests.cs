using System.Numerics;
using Prospect.Game.World;
using Xunit;

namespace Prospect.Tests;

public sealed class GasMixtureTests
{
    [Fact]
    public void MixturesAreRepeatableLocalNormalizedAndBiasedByKindWithoutAllocation()
    {
        var galaxy = GameContent.Load().Galaxy.WithSeed(1234);
        var rules = galaxy.GasComposition!;
        var normal = new GasCloudPlacement(new(1, 2, 3), new(default, new Vector3(1000), 43));
        var cold = normal with { Cloud = normal.Cloud with { Kind = GasCloudKind.Cold } };
        var lightning = normal with { Cloud = normal.Cloud with { Kind = GasCloudKind.Lightning } };
        var center = GasCloudQuery.CenterOf(galaxy, normal);
        var sample = rules.Sample(galaxy, normal, center);
        var again = rules.Sample(galaxy, normal, center);
        var frozen = rules.Sample(galaxy, cold, center);
        var charged = rules.Sample(galaxy, lightning, center);
        var sum = 0f;
        for (var i = 0; i < GasComposition.Capacity; i++)
        {
            Assert.Equal(sample.Shares[i], again.Shares[i]);
            sum += sample.Shares[i];
        }
        Assert.InRange(sum, .999999f, 1.000001f);
        Assert.True(frozen.Shares[3] + frozen.Shares[6] > .5);
        Assert.True(charged.Shares[7] + charged.Shares[5] > .5);
        Assert.True(sample.Shares[0] + sample.Shares[1] > .5);
        Assert.InRange(sample.Purity, .35f, .95f);
        Assert.Equal(1, sample.Density);
        var other = rules.Sample(galaxy, normal, center - new Double3(1, 1, 1));
        Assert.NotEqual(other.Region, sample.Region);
        Assert.NotEqual(other.Shares[0], sample.Shares[0]);
        Assert.Equal(0, rules.Sample(galaxy, normal, center + new Double3(2000, 0, 0)).Density);
        var probe = new AllocationProbe();
        while (probe.Measuring()) sample = rules.Sample(galaxy, normal, center);
        Assert.Equal(0, probe.Bytes);
        Assert.Equal(again.Purity, sample.Purity);
    }
}
