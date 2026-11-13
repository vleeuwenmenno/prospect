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
        Assert.Equal(new Vector3(4800, 3120, 4080), sector.GasCloud.HalfSize);
        Assert.InRange(sector.GasCloud.Center.X, -537, -536);
        Assert.Equal(73.74375f, sector.GasCloud.Seed);
    }

    [Fact]
    public void ModuleDoesNotChangeAsteroidBytesOrLeakReusedDescriptors()
    {
        var galaxy = Content.Galaxy.WithSeed(1234);
        Assert.IsType<AsteroidFieldsModule>(galaxy.Modules[0]);
        var without = galaxy.WithModules(galaxy.AsteroidFields!);
        var a = new SectorContent(); var b = new SectorContent();
        foreach (var coord in new SectorCoord[] { default, new(-40, 0, -29), new(8, 0, -8) })
        {
            galaxy.Generate(coord, a); without.Generate(coord, b);
            Assert.True(Sectors.Bytes(a).SequenceEqual(Sectors.Bytes(b)));
            without.Generate(coord, a);
            Assert.Equal(0, a.Summary.GasCloudCount);
            Assert.Equal(default, a.GasCloud);
        }
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
            Assert.True(Vector3.Max(Vector3.Abs(a.GasCloud.Center) + a.GasCloud.HalfSize, new Vector3((float)galaxy.SectorSize / 2)) == new Vector3((float)galaxy.SectorSize / 2));
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
    public void VisibilityFadesBeforeSupportCanReachANonNeighbourAndPushFitsLayout()
    {
        Assert.Equal(1, GasCloudModule.Visibility(12000, 40000));
        Assert.Equal(0.5f, GasCloudModule.Visibility(14000, 40000));
        Assert.Equal(0, GasCloudModule.Visibility(16000, 40000));
        Assert.Equal(0, GasCloudModule.Visibility(1e20f, 40000));
        Assert.Equal(112, Marshal.SizeOf<GasConstants>());
        Assert.Equal(64, Marshal.OffsetOf<GasConstants>(nameof(GasConstants.Budget)).ToInt32());
        Assert.Equal(80, Marshal.OffsetOf<GasConstants>(nameof(GasConstants.Shape)).ToInt32());
        Assert.Equal(96, Marshal.OffsetOf<GasConstants>(nameof(GasConstants.Look)).ToInt32());
        // Vulkan guarantees 128 push-constant bytes; the gas pass shares its range with the shield passes.
        Assert.True(Marshal.SizeOf<GasConstants>() <= 128);
    }
}
