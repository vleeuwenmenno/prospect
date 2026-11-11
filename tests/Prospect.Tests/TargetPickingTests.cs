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
    public void AdditiveClicksAdvancePastTwoSelectedHullsAndWrap()
    {
        PickHit[] hits = [new(1, null, 10), new(2, null, 20), new(3, null, 30)];
        var cycle = new PickCycle();
        var selected = new HashSet<uint>();
        for (var i = 0; i < 3; i++)
        {
            var hit = cycle.Click(hits, default);
            if (!selected.Add(hit.Hull)) selected.Remove(hit.Hull);
            Assert.Equal(i + 1, selected.Count);
        }
        Assert.Equal(1u, cycle.Click(hits, default).Hull);
        cycle.Reset();
        Assert.Equal(1u, cycle.Click(hits, default).Hull);
        cycle.Observe(new Vector2(5, 0));
        Assert.Equal(1u, cycle.Click(hits, default).Hull);
    }

    [Fact]
    public void MovingHullPickFollowsInterpolatedFrameAndSkipsJumpGap()
    {
        var previous = new Double3(-100, 0, 1000);
        var current = new Double3(100, 0, 1000);
        var position = TargetPicking.HullPosition(previous, current, 0.25f, 3000);
        Assert.Equal(new Double3(-50, 0, 1000), position);
        var hull = Hull() with { Position = position };
        Assert.Single(Pick([hull], Sector(), Vector3.Normalize(new Vector3(-50, 0, 1000))));
        Assert.Empty(Pick([hull], Sector(), Vector3.Normalize(new Vector3(100, 0, 1000))));
        var afterJump = TargetPicking.HullPosition(new Double3(-5000, 0, 1000), current, 0.25f, 3000);
        Assert.Equal(current, afterJump);
        Assert.Single(Pick([hull with { Position = afterJump }], Sector(), Vector3.Normalize(new Vector3(100, 0, 1000))));
    }

    [Theory]
    [InlineData(815, 0)]
    [InlineData(811, 1)]
    public void OffAxisSmallRockGrabUsesProjectedPixels(float x, int expected)
    {
        PickSector[] sectors = [Sector(Rock(1, new Vector3(x, 0, 1000)))];
        var hits = new PickHit[1];
        var count = TargetPicking.Pick(Vector3.Normalize(new Vector3(0.8f, 0, 1)), default,
            Vector3.UnitZ, 1000, [], sectors, hits);
        Assert.Equal(expected, count);
    }

    [Theory]
    [InlineData(60, 0)]
    [InlineData(58, 1)]
    public void HullCornerGrabIsCircularInScreenPixels(float offset, int expected)
    {
        var hull = Hull() with { Position = new Double3(offset, offset, 1000), BlockSize = 100, Radius = 87 };
        Assert.Equal(expected, Pick([hull], Sector()).Length);
        var rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.7f);
        var center = Vector3.Transform(new Vector3(offset, offset, 1000), rotation);
        hull = hull with { Position = new Double3(center.X, center.Y, center.Z), Rotation = rotation };
        Assert.Equal(expected, Pick([hull], Sector()).Length);
    }

    [Fact]
    public void RotatedBlockGrabClipsAtNearPlaneWithoutPickingBehindCamera()
    {
        var rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.7f);
        var center = Vector3.Transform(new Vector3(0.151f, 0, 0.2f), rotation);
        var hull = Hull() with { Position = new Double3(center.X, center.Y, center.Z), Rotation = rotation, BlockSize = 0.2f, Radius = 0.174f };
        var ray = Vector3.Transform(Vector3.Normalize(new Vector3(0.16f, 0, 1)), rotation);
        var hits = new PickHit[1];
        Assert.Equal(1, TargetPicking.Pick(ray, default, Vector3.UnitZ, 1000, [hull], [], hits));
        hull = hull with { Position = new Double3(center.X, center.Y, -0.2f) };
        Assert.Equal(0, TargetPicking.Pick(ray, default, Vector3.UnitZ, 1000, [hull], [], hits));
    }

    [Theory]
    [InlineData(-0.3f, 1)]
    [InlineData(-0.095f, 1)]
    [InlineData(-0.08f, 0)]
    [InlineData(0.3f, 0)]
    public void ACutBlockIsPickedOnlyWhereItIsShownAndAroundThat(float x, int expected)
    {
        // A one-metre block whose centre is 0.1 m past the cut: the slab from x = -0.5 to -0.1 is drawn. The grab
        // reaches 12 pixels, 0.012 m at 100 m here, past the cut's edge, which is no edge of the box.
        var hull = new PickHull(1, new Double3(0, 0, 100), Quaternion.Identity, [Vector3.Zero], 1, 1, 1,
            CutPoint: new Vector3(-0.1f, 0, 0), CutNormal: Vector3.UnitX);
        var ray = Vector3.Normalize(new Vector3(x, 0, 100));
        var hits = new PickHit[1];
        Assert.Equal(expected, TargetPicking.Pick(ray, default, Vector3.UnitZ, 1e5f, [hull], [], hits));
        if (x < -0.1f)
            Assert.InRange(hits[0].Distance, 99.49f, 99.51f);
    }

    [Theory]
    [InlineData(0.1f, 0, 0f)]
    [InlineData(0.25f, 1, 0.25f)]
    public void OnlyTheDrawnPartBeyondTheNearPlaneIsPicked(float z, int expected, float distance)
    {
        // A 0.2 m block from z - 0.1 to z + 0.1, with the near plane at 0.25 m: what is nearer is not drawn.
        var hull = new PickHull(1, new Double3(0, 0, z), Quaternion.Identity, [Vector3.Zero], 0.2f, 1, 0.18f);
        var hits = new PickHit[1];
        Assert.Equal(expected, TargetPicking.Pick(Vector3.UnitZ, default, Vector3.UnitZ, 1000, [hull], [], hits));
        if (expected == 1)
            Assert.InRange(hits[0].Distance, distance - 1e-4f, distance + 1e-4f);
    }

    [Fact]
    public void ACutDrawnOutBlockAcrossTheNearPlaneMatchesASampledSilhouette()
    {
        // Rotated blocks drawn out along z, with a tilted cut through them and the near plane through them too, so
        // the silhouette has corners on neither the box's corners nor its edges. Each pick is checked against the
        // solid sampled on a grid: inside the grab by more than the grid's error is a hit, outside by more a miss.
        const float Focal = 50f;
        const float NearPlane = 4f;
        const int Grid = 40;
        var random = new Random(17);
        var hits = new PickHit[1];
        var grab = TargetPicking.GrabPixels / Focal;
        int hitsSeen = 0, missesSeen = 0;
        for (var c = 0; c < 300; c++)
        {
            var axis = Vector3.Normalize(new Vector3(random.NextSingle() - 0.5f, random.NextSingle() - 0.5f, random.NextSingle() - 0.5f));
            var rotation = Quaternion.CreateFromAxisAngle(axis, random.NextSingle() * 6.28f);
            var stretch = 1f + random.NextSingle();
            var center = new Vector3(random.NextSingle() - 0.5f, random.NextSingle() - 0.5f, NearPlane + random.NextSingle() - 0.5f);
            var normal = Vector3.Normalize(new Vector3(random.NextSingle() - 0.5f, random.NextSingle() - 0.5f, random.NextSingle() - 0.5f));
            var cutPoint = new Vector3(random.NextSingle() - 0.5f, random.NextSingle() - 0.5f, random.NextSingle() - 0.5f) * 0.4f;
            var hull = new PickHull(1, new Double3(center.X, center.Y, center.Z), rotation, [Vector3.Zero], 1, 1, 0.87f, stretch, cutPoint, normal);
            var pointer = new Vector3(center.X / center.Z + (random.NextSingle() - 0.5f) * 1.2f, center.Y / center.Z + (random.NextSingle() - 0.5f) * 1.2f, 1);

            var nearest = float.PositiveInfinity;
            for (var i = 0; i < Grid; i++)
                for (var j = 0; j < Grid; j++)
                    for (var k = 0; k < Grid; k++)
                    {
                        var local = new Vector3(i, j, k) / (Grid - 1) - new Vector3(0.5f);
                        local.Z *= stretch;
                        var p = center + Vector3.Transform(local, rotation);
                        if (p.Z < NearPlane || Vector3.Dot(p - center - cutPoint, normal) > 0) continue;
                        nearest = Math.Min(nearest, Vector2.Distance(new Vector2(p.X, p.Y) / p.Z, new Vector2(pointer.X, pointer.Y)));
                    }
            // A grid point is at most half a diagonal from the solid's nearest point; seen no nearer than the near plane.
            var error = MathF.Sqrt(3) * 0.5f * stretch / (Grid - 1) / NearPlane * 1.5f;
            // A sliver the grid misses altogether, or a pointer at the grab's edge, is not decided by sampling.
            if (!float.IsFinite(nearest) || MathF.Abs(nearest - grab) < error) continue;
            var expected = nearest < grab ? 1 : 0;
            Assert.True(expected == TargetPicking.Pick(Vector3.Normalize(pointer), default, Vector3.UnitZ, Focal, [hull], [], hits, nearPlane: NearPlane), $"case {c}: nearest {nearest}, grab {grab}, error {error}");
            hitsSeen += expected;
            missesSeen += 1 - expected;
        }
        Assert.True(hitsSeen > 50 && missesSeen > 50, $"{hitsSeen} hits, {missesSeen} misses");
    }

    [Fact]
    public void CameraInsideRockPicksForwardExitAndOrdersBySurfaceDistance()
    {
        var rock = Rock(100, default);
        var expected = 100 * AsteroidShape.RadiusAlong(0, Vector3.UnitZ);
        Assert.True(RockPicking.Hit(Vector3.UnitZ, default, rock, out var distance));
        Assert.InRange(MathF.Abs(distance - expected), 0, 0.002f);
        var hits = Pick([], Sector(rock, Rock(1, new Vector3(0, 0, 20))));
        Assert.Equal(2, hits.Length);
        Assert.Equal(1, hits[0].Rock!.Value.Index);
        Assert.Equal(0, hits[1].Rock!.Value.Index);
        Assert.InRange(MathF.Abs(hits[1].Distance - expected), 0, 0.002f);
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
