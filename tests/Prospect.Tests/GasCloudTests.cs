using System.Numerics;
using System.Runtime.InteropServices;
using Prospect.Client.Render;
using Prospect.Game.World;
using Xunit;

namespace Prospect.Tests;

public sealed class GasCloudTests
{
    private static readonly GameContent Content = GameContent.Load();

    [Fact]
    public void CaptureSectorContainsTheDefaultSeedCloud()
    {
        var galaxy = Content.Galaxy.WithSeed(0x50524F5350454354);
        var sector = new SectorContent();
        galaxy.Generate(new(-3, 0, 5), sector);
        Assert.Equal(1, sector.Summary.GasCloudCount);
        Assert.Equal(new Vector3(4071.2756f, 2646.329f, 3460.5845f), sector.GasCloud.HalfSize);
        Assert.Equal(new Vector3(-5364.9116f, 10668.583f, -2576.735f), sector.GasCloud.Center);
        Assert.Equal(73.74375f, sector.GasCloud.Seed);
        Assert.True(galaxy.GasClouds!.TryGetCloud(galaxy, new(-3, 0, 5), out var cloud));
        Assert.Equal(sector.GasCloud, cloud);
    }

    [Fact]
    public void OnlyRocksInGasHangOnTheGasModuleAndReusedDescriptorsDoNotLeak()
    {
        var galaxy = Content.Galaxy.WithSeed(1234);
        Assert.IsType<AsteroidFieldsModule>(galaxy.Modules[0]);
        var gas = galaxy.GasClouds!;
        var without = galaxy.WithModules(galaxy.AsteroidFields!);
        var a = new SectorContent(); var b = new SectorContent();
        Span<GasCloudPlacement> reaching = stackalloc GasCloudPlacement[GasCloudModule.MaxPerSector];
        var apart = 0;
        var changed = 0;
        for (var x = -12; x <= 12; x++)
            for (var z = -12; z <= 12; z++)
            {
                var coord = new SectorCoord(x, 0, z);
                galaxy.Generate(coord, a); without.Generate(coord, b);
                var same = Sectors.Bytes(a).SequenceEqual(Sectors.Bytes(b));

                // A cloud's rocks stay in the cloud: a sector no cloud reaches has the asteroids it had without gas.
                if (gas.Intersecting(galaxy, coord, reaching) == 0)
                {
                    Assert.True(same, $"sector {coord} has no cloud near it and changed with the gas module");
                    apart++;
                }
                else if (!same)
                {
                    Assert.True(a.AsteroidCount > b.AsteroidCount, $"sector {coord}: gas should add rocks");
                    changed++;
                }

                without.Generate(coord, a);
                Assert.Equal(0, a.Summary.GasCloudCount);
                Assert.Equal(default, a.GasCloud);
            }

        Assert.True(apart > 400, $"only {apart} sectors without a cloud near them");
        Assert.True(changed > 3, $"gas added rocks to only {changed} sectors");
    }

    [Fact]
    public void RocksInGasFollowTheContentAndTheWorldSettings()
    {
        // The share of clouds that hold rocks is content: at zero, gas changes no asteroid anywhere.
        using var copy = new ContentCopy();
        copy.Replace("galaxy/galaxy.json", "\"inGas\": { \"chance\": 0.5", "\"inGas\": { \"chance\": 0");
        var none = copy.Load().Galaxy.WithSeed(1234);
        var bare = none.WithModules(none.AsteroidFields!);
        var shipped = Content.Galaxy.WithSeed(1234);
        var scarce = Content.WithSettings(Content.SettingCatalog.Build(null, ["asteroidFrequency=0"])).Galaxy.WithSeed(1234);
        var a = new SectorContent(); var b = new SectorContent();
        var inGas = 0L;
        for (var x = -12; x <= 12; x++)
            for (var z = -12; z <= 12; z++)
            {
                var coord = new SectorCoord(x, 0, z);
                none.Generate(coord, a); bare.Generate(coord, b);
                Assert.True(Sectors.Bytes(a).SequenceEqual(Sectors.Bytes(b)));
                shipped.Generate(coord, b);
                inGas += b.AsteroidCount - a.AsteroidCount;

                // Field occurrence at zero leaves the home field and nothing else, gas or no gas.
                scarce.Generate(coord, a);
                Assert.Equal(coord == default, a.AsteroidCount > 0);
            }

        Assert.True(inGas > 300, $"the shipped clouds hold only {inGas} rocks in the sample");
    }

    [Fact]
    public void DescriptorsAreBoundedRepeatableAndAllocationFree()
    {
        var galaxy = Content.Galaxy.WithSeed(1234).WithModules(Content.Galaxy.GasClouds!);
        var a = new SectorContent(); var b = new SectorContent();
        var present = 0;
        for (var x = -100; x < 100; x++)
        {
            var coord = new SectorCoord(x, 0, 0);
            galaxy.Generate(coord, a); galaxy.Generate(coord, b);
            Assert.Equal(a.GasCloud, b.GasCloud);
            Assert.InRange(a.Summary.GasCloudCount, 0, 1);
            if (a.Summary.GasCloudCount == 0) continue;
            present++;
            // Centred in the sector that anchors it and at most a sector wide: it reaches no further than the
            // sectors around that one.
            var half = new Vector3((float)galaxy.SectorSize / 2);
            Assert.True(Vector3.Max(Vector3.Abs(a.GasCloud.Center), half) == half);
            Assert.True(Vector3.Max(a.GasCloud.HalfSize, half) == half);
            Assert.True(galaxy.GasClouds!.TryGetCloud(galaxy, coord, out var cloud));
            Assert.Equal(a.GasCloud, cloud);
        }
        Assert.InRange(present, 1, 30);
        for (var i = 0; i < 100; i++) galaxy.Generate(new(i, 0, 0), a);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 100; i++) galaxy.Generate(new(i, 0, 0), a);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void GasRulesAreSharedAndInvalidBudgetsAreRejected()
    {
        using var copy = new ContentCopy();
        copy.Replace("galaxy/galaxy.json", "\"chance\": 0.04", "\"chance\": 0.05");
        Assert.NotEqual(Content.Identity, copy.Load().Identity);
        copy.Replace("galaxy/galaxy.json", "\"samples\": 48", "\"samples\": 65");
        Assert.Throws<InvalidDataException>(() => copy.Load());
    }

    [Theory]
    [InlineData("\"fill\": 0.56", "\"fill\": 0.29")]
    [InlineData("\"knots\": 2.2", "\"knots\": 6.5")]
    [InlineData("\"veil\": 0.03", "\"veil\": -0.1")]
    [InlineData("\"edge\": 0.14", "\"edge\": 0.01")]
    // Each within its own bounds, but together the swollen surface would reach past the box face.
    [InlineData("\"fill\": 0.56", "\"fill\": 0.7")]
    [InlineData("\"lean\": 0.24", "\"lean\": 0.4")]
    // A cloud is centred in the sector that anchors it and is at most a sector wide.
    [InlineData("\"centerReach\": 1.0", "\"centerReach\": 1.1")]
    [InlineData("\"halfSizeShare\": [0.25, 0.5]", "\"halfSizeShare\": [0.25, 0.6]")]
    [InlineData("\"halfSizeShare\": [0.1, 0.14]", "\"halfSizeShare\": [0.14, 0.1]")]
    public void ShapeDataIsBoundedOnItsOwnAndAgainstTheBox(string from, string to)
    {
        using var copy = new ContentCopy();
        copy.Replace("galaxy/galaxy.json", from, to);
        Assert.Throws<InvalidDataException>(() => copy.Load());
    }

    [Fact]
    public void ShippedShapeDataIsReadSharedAndInsideTheBox()
    {
        var gas = Content.Galaxy.GasClouds!;
        Assert.Equal(new Vector4(0.56f, 1.8f, 0.1f, 0.24f), gas.Shape);
        Assert.Equal(new Vector4(0.14f, 2.2f, 1.5f, 0.03f), gas.Look);
        Assert.True(gas.Shape.X / MathF.Sqrt(GasCloudModule.MinSwell) + gas.Shape.W / 2 + gas.Shape.Z / GasCloudModule.Proportions.Y <= 1);
        using var copy = new ContentCopy();
        copy.Replace("galaxy/galaxy.json", "\"wisps\": 1.5", "\"wisps\": 1.0");
        Assert.NotEqual(Content.Identity, copy.Load().Identity);
    }

    [Fact]
    public void LargerCloudsAreThinnerAndPushFitsLayout()
    {
        // A larger cloud is thinner by as much as it is larger. How far a cloud shows is the client's (GasTiers).
        var gas = Content.Galaxy.GasClouds!;
        Assert.Equal(gas.Extinction, gas.ExtinctionOf(new GasCloud(default, new Vector3(4000, 2600, 3400), 0), 40000));
        Assert.Equal(gas.Extinction * 0.25f, gas.ExtinctionOf(new GasCloud(default, new Vector3(19200, 12480, 16320), 0), 40000), 1e-9f);
        Assert.Equal(128, Marshal.SizeOf<GasConstants>());
        Assert.Equal(64, Marshal.OffsetOf<GasConstants>(nameof(GasConstants.Budget)).ToInt32());
        Assert.Equal(80, Marshal.OffsetOf<GasConstants>(nameof(GasConstants.Shape)).ToInt32());
        Assert.Equal(96, Marshal.OffsetOf<GasConstants>(nameof(GasConstants.Look)).ToInt32());
        // Vulkan guarantees 128 push-constant bytes; the gas pass shares its range with the shield passes.
        Assert.True(Marshal.SizeOf<GasConstants>() <= 128);
    }
}
