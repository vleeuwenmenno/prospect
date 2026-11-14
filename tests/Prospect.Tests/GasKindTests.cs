using System.Numerics;
using Prospect.Game.World;
using Xunit;

namespace Prospect.Tests;

public sealed class GasKindTests
{
    private static readonly GameContent Content = GameContent.Load();

    [Theory]
    [InlineData(0, 0, GasCloudKind.Normal)]
    [InlineData(1, 0, GasCloudKind.Cold)]
    [InlineData(0, 1, GasCloudKind.Lightning)]
    public void ChanceEndpointsAndSeedMapping(int cold, int lightning, GasCloudKind expected)
    {
        var world = Content.WithSettings(Content.SettingCatalog.Build(null, [$"gasColdChance={cold}", $"gasLightningChance={lightning}"]));
        for (var i = 0; i < 1000; i++) Assert.Equal(expected, world.Galaxy.GasClouds!.KindOf(i * 128f / 1000));
        var gas = Content.Galaxy.GasClouds!;
        Assert.Equal(GasCloudKind.Cold, gas.KindOf(0));
        Assert.Equal(GasCloudKind.Lightning, gas.KindOf(128 * 0.21f));
        Assert.Equal(GasCloudKind.Normal, gas.KindOf(128 * 0.5f));
    }

    [Fact]
    public void IndependentColdAndNormalEnvelopesActuallyMix()
    {
        using var copy = new ContentCopy();
        copy.Replace("galaxy/galaxy.json", "\"chance\": 0.04", "\"chance\": 0.4");
        var content = copy.Load();
        var world = content.WithSettings(content.SettingCatalog.Build(null, ["gasColdChance=0.5", "gasLightningChance=0", "gasHugeChance=1", "gasHugeSizeMin=80", "gasHugeSizeMax=80"]));
        var galaxy = world.Galaxy.WithSeed(42);
        var clouds = new List<GasCloudPlacement>();
        for (var x = -40; x <= 40; x++)
            for (var z = -40; z <= 40; z++)
            {
                var anchor = new SectorCoord(x, 0, z);
                if (galaxy.GasClouds!.TryGetCloud(galaxy, anchor, out var cloud)) clouds.Add(new(anchor, cloud));
            }
        var mixed = 0;
        for (var i = 0; i < clouds.Count; i++)
            for (var j = 0; j < i; j++)
            {
                var a = clouds[i]; var b = clouds[j];
                var delta = GasCloudQuery.CenterOf(galaxy, a) - GasCloudQuery.CenterOf(galaxy, b);
                var radii = (a.Cloud.HalfSize + b.Cloud.HalfSize) * galaxy.GasClouds!.Shape.X;
                var x = delta.X / radii.X; var y = delta.Y / radii.Y; var z = delta.Z / radii.Z;
                if (x * x + y * y + z * z > 1) continue;
                Assert.True(GasCloudModule.MayOverlap(a.Cloud.Kind, b.Cloud.Kind));
                mixed++;
            }
        Assert.True(mixed > 0);
    }

    [Fact]
    public void BurstCoverageAndSampleReachAgreeForWholeCycleWithoutAllocation()
    {
        GasCloudPlacement[] clouds = [new(default, new(Vector3.Zero, new Vector3(1000), 17, GasCloudKind.Lightning))];
        Double3[] directions = [new(1, 0, 0)];
        var reaches = new double[1];
        var rules = Content.Exploration;
        var bursts = 0;
        for (var tick = 0; tick < rules.LightningPeriodTicks; tick++)
        {
            var status = GasSensors.EnvironmentAt(Content.Galaxy, clouds, rules, default, tick);
            if ((status & HullEnvironment.SensorsDisrupted) != 0) bursts++;
            Assert.Equal(status, GasSensors.EnvironmentAt(Content.Galaxy, clouds, rules, default, tick + rules.LightningPeriodTicks));
            GasSensors.SampleReach(Content.Galaxy, clouds, rules, default, directions, reaches, tick);
            Assert.True(GasSensors.Covers(Content.Galaxy, clouds, rules, default, directions[0] * reaches[0], tick: tick));
            Assert.False(GasSensors.Covers(Content.Galaxy, clouds, rules, default, directions[0] * (reaches[0] + .001), tick: tick));
            Assert.True(GasSensors.Covers(Content.Galaxy, clouds, rules, default, new Double3(rules.ProximityRadius, 0, 0), false, tick));
        }
        Assert.Equal(rules.LightningBurstTicks, bursts);
        var probe = new AllocationProbe();
        while (probe.Measuring()) GasSensors.SampleReach(Content.Galaxy, clouds, rules, default, directions, reaches, 0);
        Assert.Equal(0, probe.Bytes);
    }

    [Fact]
    public void IcicleSurfaceAndCollisionRadiusAgreeAndStayBounded()
    {
        for (var x = -10; x <= 10; x++)
            for (var y = -10; y <= 10; y++)
                for (var z = -10; z <= 10; z++)
                {
                    if ((x | y | z) == 0) continue;
                    var direction = Vector3.Normalize(new Vector3(x, y, z));
                    var point = AsteroidShape.Point(AsteroidShape.Icicle, direction);
                    Assert.InRange(point.Length(), 0, 1.000001f);
                    Assert.InRange(Math.Abs(point.Length() - AsteroidShape.RadiusAlong(AsteroidShape.Icicle, direction)), 0, .000001f);
                }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void IciclesAreGeneratedOnlyInsideColdEnvelopes(int iceAbundance)
    {
        var world = Content.WithSettings(Content.SettingCatalog.Build(null, ["gasColdChance=1", "gasHugeChance=1", $"iceAbundance={iceAbundance}", "iceGradeMin=0.7", "iceGradeMax=0.7"]));
        var galaxy = world.Galaxy.WithSeed(42);
        var sector = new SectorContent();
        var clouds = new GasCloudPlacement[27];
        var found = 0;
        for (var x = -20; x <= 20 && found < 100; x++)
            for (var z = -20; z <= 20 && found < 100; z++)
            {
                var coord = new SectorCoord(x, 0, z);
                if (!galaxy.GasClouds!.TryGetCloud(galaxy, coord, out _)) continue;
                galaxy.Generate(coord, sector);
                var count = galaxy.GasClouds.Intersecting(galaxy, coord, clouds);
                for (var i = 0; i < sector.AsteroidCount; i++)
                {
                    var rock = sector.Asteroids[i];
                    if (rock.Shape != AsteroidShape.Icicle) continue;
                    found++;
                    Assert.Contains(clouds[..count], c => c.Cloud.Kind == GasCloudKind.Cold && GasSensors.Contains(galaxy, c, galaxy.CenterOf(coord) + rock.LocalPosition));
                    if (iceAbundance == 0) Assert.Equal(-1, rock.Resource);
                    if (rock.Resource >= 0)
                    {
                        Assert.Equal("ice", galaxy.Resources[rock.Resource].Id);
                        Assert.Equal(0.7f, rock.Grade);
                    }
                }
            }
        Assert.True(found >= 100, $"Only {found} icicles");
    }
}
