using System.Diagnostics;
using System.Numerics;
using Prospect.Game.World;
using Xunit;

namespace Prospect.Tests;

public sealed class TargetPickingTests
{
    private static Asteroid Rock(float radius, Vector3 position) => new() { Radius = radius, LocalPosition = position, Rotation = Quaternion.Identity };
    private static PickHull Hull(float x = 0, float z = 8000) => new(1, new Double3(x, 0, z), Quaternion.Identity, [Vector3.Zero], 2, 1, 2);
    private static PickSector Sector(params Asteroid[] rocks) => new(default, default, rocks, rocks.Length, null);
    private static PickHit[] Pick(PickHull[] hulls, PickSector sector, Vector3? ray = null, Double3 camera = default)
    {
        var hits = new PickHit[hulls.Length + sector.Count];
        var count = TargetPicking.Pick(ray ?? Vector3.UnitZ, camera, ray ?? Vector3.UnitZ, 1000, hulls, [sector], hits);
        return hits[..count];
    }

    [Fact]
    public void BoundingSphereMissPicksHullBehind()
    {
        var rock = Rock(3000, new Vector3(0, 2700, 4000));
        Assert.False(RockPicking.Hit(Vector3.UnitZ, rock.LocalPosition, rock, out _));
        var hits = Pick([Hull()], Sector(rock));
        Assert.Single(hits);
        Assert.Equal(1u, hits[0].Hull);
    }

    [Fact]
    public void MissingHullBlocksCannotBePicked()
    {
        Assert.Empty(Pick([Hull() with { Mask = 0 }], Sector()));
    }

    [Fact]
    public void GrabRadiusReachesBesideLargeHullBlocks()
    {
        var hull = Hull(55, 1000) with { BlockSize = 100, Radius = 87 };
        Assert.Single(Pick([hull], Sector()));
    }

    [Fact]
    public void SurfaceHitPicksRock()
    {
        var hits = Pick([], Sector(Rock(3000, new Vector3(0, 0, 6000))));
        Assert.Single(hits);
        Assert.Equal(new RockRef(default, 0), hits[0].Rock);
        Assert.InRange(hits[0].Distance, 3000, 6000);
    }

    [Fact]
    public void CameraInsideBoundsOutsideSurfaceCanPickAway()
    {
        var rock = Rock(3000, default);
        var x = (3000 * AsteroidShape.RadiusAlong(0, Vector3.UnitX) + 3000) / 2;
        Assert.Empty(Pick([], Sector(rock), Vector3.UnitX, new Double3(x, 0, 0)));
    }

    [Fact]
    public void HullInFrontOfRockWins()
    {
        var hits = Pick([Hull(z: 1000)], Sector(Rock(3000, new Vector3(0, 0, 6000))));
        Assert.Equal(2, hits.Length);
        Assert.Equal(1u, hits[0].Hull);
        Assert.True(hits[0].Distance < hits[1].Distance);
    }

    [Fact]
    public void HullBesidePointerWinsOverSlightlyNearerSurface()
    {
        var rock = Rock(100, new Vector3(0, 0, 1000));
        Assert.True(RockPicking.Hit(Vector3.UnitZ, rock.LocalPosition, rock, out var distance));
        var hits = Pick([Hull(8, distance + 20)], Sector(rock));
        Assert.Equal(2, hits.Length);
        Assert.Equal(1u, hits[0].Hull);
        Assert.True(hits[0].Distance > hits[1].Distance);
    }

    [Fact]
    public void SmallRockBesideBigOneKeepsGrabRadius()
    {
        var hits = Pick([], Sector(Rock(3000, new Vector3(0, 2700, 4000)), Rock(1, new Vector3(8, 0, 1000))));
        Assert.Single(hits);
        Assert.Equal(1, hits[0].Rock!.Value.Index);
    }

    [Fact]
    public void GrazingRayFindsSurfaceMinimumBetweenSamples()
    {
        // Find the silhouette in the XZ plane independently with a dense surface scan.
        var rock = Rock(100, new Vector3(0, 0, 300));
        var maximumX = 0f;
        var z = 0f;
        for (var i = 0; i < 100000; i++)
        {
            var angle = MathF.PI * 2 * i / 100000;
            var direction = new Vector3(MathF.Cos(angle), 0, MathF.Sin(angle));
            var point = direction * (rock.Radius * AsteroidShape.RadiusAlong(0, direction));
            if (point.X > maximumX) { maximumX = point.X; z = point.Z; }
        }
        Assert.True(RockPicking.Hit(Vector3.UnitZ, new Vector3(-maximumX, 0, 300), rock, out var hit));
        Assert.InRange(MathF.Abs(hit - (300 + z)), 0, 1);
        Assert.False(RockPicking.Hit(Vector3.UnitZ, new Vector3(-maximumX - 0.02f, 0, 300), rock, out _));
    }

    [Fact]
    public void RepeatedClicksCycleAndWrap()
    {
        PickHit[] hits = [new(1, null, 10), new(0, new RockRef(default, 0), 20)];
        var cycle = new PickCycle();
        var first = cycle.Click(hits, default, 0, null);
        var second = cycle.Click(hits, default, first.Hull, first.Rock);
        var third = cycle.Click(hits, default, second.Hull, second.Rock);
        Assert.Equal(hits[0], first);
        Assert.Equal(hits[1], second);
        Assert.Equal(first, third);
    }

    [Fact]
    public void PointerMovementResetsCycleEvenWhenItReturns()
    {
        PickHit[] hits = [new(1, null, 10), new(2, null, 20)];
        var cycle = new PickCycle();
        cycle.Click(hits, default, 0, null);
        cycle.Observe(new Vector2(5, 0));
        cycle.Observe(default);
        Assert.Equal(hits[0], cycle.Click(hits, default, 1, null));
    }

    [Fact]
    public void DisplacedRockIsPickedAtCurrentPosition()
    {
        var sector = Sector(Rock(20, new Vector3(1000, 0, 1000)));
        Assert.Empty(Pick([], sector));
        sector = sector with { Offsets = [new Vector3(-1000, 0, 0)] };
        Assert.Single(Pick([], sector));
        Assert.Empty(Pick([], sector, Vector3.Normalize(new Vector3(1, 0, 1))));
    }

    [Fact]
    public void SectorOf4096RocksTakesUnderFiveMillisecondsAndAllocatesNothing()
    {
        var rocks = new Asteroid[4096];
        for (var i = 0; i < rocks.Length; i++) rocks[i] = Rock(10, new Vector3(500 + i % 64 * 100, i / 64 * 100, 5000));
        rocks[0] = Rock(100, new Vector3(0, 0, 5000));
        PickSector[] sectors = [Sector(rocks)];
        var hits = new PickHit[rocks.Length];
        for (var i = 0; i < 20; i++) TargetPicking.Pick(Vector3.UnitZ, default, Vector3.UnitZ, 1000, [], sectors, hits);
        var start = Stopwatch.GetTimestamp();
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        var count = 0;
        for (var i = 0; i < 100; i++) count = TargetPicking.Pick(Vector3.UnitZ, default, Vector3.UnitZ, 1000, [], sectors, hits);
        allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
        var milliseconds = Stopwatch.GetElapsedTime(start).TotalMilliseconds / 100;
        Console.WriteLine($"PICK 4096: {milliseconds:F3} ms per pick; {allocated} bytes; budget 5 ms");
        Assert.Equal(1, count);
        Assert.Equal(0, allocated);
        Assert.True(milliseconds < 5, $"4096 rocks: {milliseconds:F3} ms per pick; budget 5 ms");
    }
}
