using System.Numerics;
using Brutal.Numerics;
using Prospect.Client.Screens;
using Prospect.Game.World;
using Xunit;
using IJob = Brutal.Concurrency.Jobs.IJob;

namespace Prospect.Tests;

/// <summary>
/// The outlines the galaxy map draws for asteroid fields and gas clouds, and the cache that keeps them: from shared
/// generation data, the same every time, bounded, whole across a sector wall, and rebuilt when the world changes.
/// </summary>
public sealed class MapFootprintTests
{
    private static readonly GameContent Content = GameContent.Load();
    private const ulong DefaultSeed = 0x50524F5350454354;
    private static readonly Galaxy Galaxy = Content.Galaxy.WithSeed(DefaultSeed);

    // Sectors with something to draw, with the default seed (see AGENTS.md): a cloud with rocks in it, and a large
    // field across the wall between two sectors.
    private static readonly SectorCoord CloudSector = new(-66, 1, 37);
    private static readonly SectorCoord WallWest = new(2, 0, 1);
    private static readonly SectorCoord WallEast = new(3, 0, 1);

    private static ChartField[] FieldsOf(SectorCoord sector)
    {
        var buffer = new ChartField[AsteroidFieldsModule.MaxFieldsInReach];
        var count = Galaxy.AsteroidFields!.ChartFields(Galaxy, sector, buffer);
        return buffer[..count];
    }

    private static bool Inside(Vector2 point, ReadOnlySpan<Vector2> polygon)
    {
        var inside = false;
        for (int i = 0, j = polygon.Length - 1; i < polygon.Length; j = i++)
        {
            var a = polygon[i];
            var b = polygon[j];
            if ((a.Y > point.Y) != (b.Y > point.Y) && point.X < (b.X - a.X) * (point.Y - a.Y) / (b.Y - a.Y) + a.X)
                inside = !inside;
        }

        return inside;
    }

    [Fact]
    public void ChangingSectorSizeReplacesCachedGeometryWithTheNewWorldsOutlines()
    {
        var cache = Cache();
        Vector2[] previous = [];
        foreach (var km in new[] { 20, 80 })
        {
            var galaxy = Content.WithSettings(Content.SettingCatalog.Build(null, [$"sectorSize={km}"])).Galaxy.WithSeed(DefaultSeed);
            var sector = new SectorCoord(-3, 0, 5);
            cache.Update(galaxy);
            Assert.False(cache.TryGet(galaxy, sector, out _));
            cache.Update(galaxy);
            Assert.True(cache.TryGet(galaxy, sector, out var slot));
            var fresh = Cache();
            var freshSlot = fresh.BuildNow(galaxy, sector);
            Assert.NotEmpty(cache.Features(slot).ToArray());
            Assert.Equal(fresh.Features(freshSlot).Length, cache.Features(slot).Length);
            for (var i = 0; i < cache.Features(slot).Length; i++)
                Assert.Equal(fresh.Outer(freshSlot, i).ToArray(), cache.Outer(slot, i).ToArray());
            var current = cache.Outer(slot, 0).ToArray();
            if (previous.Length > 0) Assert.NotEqual(previous, current);
            previous = current;
        }
    }

    [Fact]
    public void AFieldsOutlineIsTheSameEveryTimeBoundedAndInsideItsOwnReach()
    {
        var fields = FieldsOf(WallWest);
        Assert.NotEmpty(fields);
        var origin = Galaxy.CenterOf(WallWest);
        var first = new Vector2[MapFootprint.MaxVertices];
        var second = new Vector2[MapFootprint.MaxVertices];
        foreach (var field in fields)
        {
            var count = MapFootprint.Field(field, 1f, origin, first);
            Assert.Equal(MapFootprint.FieldVertices, count);
            Assert.Equal(count, MapFootprint.Field(field, 1f, origin, second));
            Assert.Equal(first[..count], second[..count]);

            // Every point is finite and within the ribbon's own box: its length along the axis, its width across.
            var reach = field.HalfExtents.X + field.HalfExtents.Z;
            var center = new Vector2((float)(field.Center.X - origin.X), (float)(field.Center.Z - origin.Z));
            for (var i = 0; i < count; i++)
            {
                Assert.True(float.IsFinite(first[i].X) && float.IsFinite(first[i].Y));
                Assert.True((first[i] - center).Length() <= reach * 1.001f);
            }
        }
    }

    [Fact]
    public void AFieldsCoreIsNarrowerThanItsSupportAndTheEndsMeet()
    {
        var field = FieldsOf(WallWest)[0];
        var origin = Galaxy.CenterOf(WallWest);
        var whole = new Vector2[MapFootprint.MaxVertices];
        var core = new Vector2[MapFootprint.MaxVertices];
        var count = MapFootprint.Field(field, 1f, origin, whole);
        MapFootprint.Field(field, 0.5f, origin, core);
        var center = new Vector2((float)(field.Center.X - origin.X), (float)(field.Center.Z - origin.Z));
        // The first vertex is one pointed end and the middle of the run is the other; both sides share the ends.
        Assert.Equal(whole[0], core[0]);
        Assert.Equal(whole[MapFootprint.FieldSamples], core[MapFootprint.FieldSamples]);
        for (var i = 1; i < MapFootprint.FieldSamples; i++)
            Assert.True((core[i] - center).LengthSquared() <= (whole[i] - center).LengthSquared() + 1f);
        Assert.Equal(MapFootprint.FieldVertices, count);
    }

    [Fact]
    public void MostRocksOfASectorLieInsideTheOutlinesOfItsFields()
    {
        var sector = WallWest;
        var origin = Galaxy.CenterOf(sector);
        var outlines = new List<Vector2[]>();
        foreach (var field in FieldsOf(sector))
        {
            var points = new Vector2[MapFootprint.MaxVertices];
            var count = MapFootprint.Field(field, 1f, origin, points);
            outlines.Add(points[..count]);
        }

        var content = new SectorContent();
        Galaxy.Generate(sector, content);
        Assert.True(content.AsteroidCount > 50);
        var inside = 0;
        for (var i = 0; i < content.AsteroidCount; i++)
        {
            var position = content.Asteroids[i].LocalPosition;
            var point = new Vector2(position.X, position.Z);
            if (outlines.Any(outline => Inside(point, outline)))
                inside++;
        }

        // Rocks settle a little off where they first land, so the outline holds nearly all, not every one.
        Assert.True(inside >= content.AsteroidCount * 0.9, $"{inside} of {content.AsteroidCount} rocks inside");
    }

    [Fact]
    public void AFieldAcrossASectorWallHasOneOutlineWhicheverSectorAsks()
    {
        var wall = (WallWest.X + 0.5) * Galaxy.SectorSize;
        var west = FieldsOf(WallWest);
        var east = FieldsOf(WallEast);
        var crossing = 0;
        var worldOrigin = Double3.Zero;
        var a = new Vector2[MapFootprint.MaxVertices];
        var b = new Vector2[MapFootprint.MaxVertices];
        foreach (var field in west)
        {
            var match = east.Cast<ChartField?>().FirstOrDefault(other => other!.Value.Center == field.Center);
            if (match is not { } twin)
                continue;
            var count = MapFootprint.Field(field, 1f, worldOrigin, a);
            MapFootprint.Field(twin, 1f, worldOrigin, b);
            Assert.Equal(a[..count], b[..count]);
            var low = a[..count].Min(point => point.X);
            var high = a[..count].Max(point => point.X);
            if (low < wall && high > wall)
                crossing++;
        }

        Assert.True(crossing > 0, "no field reaching both sectors crosses the wall");
    }

    [Fact]
    public void ACloudOutlineIsConvexBoundedAndNarrowsAsTheLayerLeavesItsCentre()
    {
        var gas = Galaxy.GasClouds!;
        var anchor = Galaxy.CenterOf(CloudSector);
        Assert.True(gas.TryGetCloud(Galaxy, CloudSector, out var cloud));
        var fill = gas.Shape.X;
        var points = new Vector2[MapFootprint.MaxVertices];
        var half = Galaxy.SectorSize / 2;
        var centre = anchor.Y + cloud.Center.Y;

        var count = MapFootprint.Cloud(cloud, anchor, fill, gas.Shape.Y, centre - 1, centre + 1, anchor, points);
        Assert.Equal(MapFootprint.CloudVertices, count);
        foreach (var point in points[..count])
        {
            Assert.True(MathF.Abs(point.X - cloud.Center.X) <= cloud.HalfSize.X);
            Assert.True(MathF.Abs(point.Y - cloud.Center.Z) <= cloud.HalfSize.Z);
        }

        // Convex: every turn is the same way.
        var turns = 0;
        for (var i = 0; i < count; i++)
        {
            var p = points[i];
            var q = points[(i + 1) % count];
            var r = points[(i + 2) % count];
            turns += (q.X - p.X) * (r.Y - q.Y) - (q.Y - p.Y) * (r.X - q.X) > 0 ? 1 : -1;
        }

        Assert.Equal(count, Math.Abs(turns));

        // A layer slab above the cloud's middle cuts it narrower; one past its top does not cut it at all.
        var near = new Vector2[MapFootprint.MaxVertices];
        var top = centre + cloud.HalfSize.Y * fill;
        Assert.Equal(count, MapFootprint.Cloud(cloud, anchor, fill, gas.Shape.Y, top - cloud.HalfSize.Y * fill * 0.5, top + 1000, anchor, near));
        Assert.True(Extent(near[..count]) < Extent(points[..count]));
        Assert.Equal(0, MapFootprint.Cloud(cloud, anchor, fill, gas.Shape.Y, top + 1, top + 1000, anchor, near));
        Assert.Equal(0, MapFootprint.Cloud(cloud, anchor, fill, gas.Shape.Y, centre - half * 40, centre - cloud.HalfSize.Y * fill - 1, anchor, near));

        // The fringe round it is wider.
        var fringe = new Vector2[MapFootprint.MaxVertices];
        MapFootprint.Cloud(cloud, anchor, MapFootprint.FringeShare(fill), gas.Shape.Y, centre - 1, centre + 1, anchor, fringe);
        Assert.True(Extent(fringe[..count]) > Extent(points[..count]));
        _ = half;
    }

    private static float Extent(ReadOnlySpan<Vector2> points)
    {
        var low = new Vector2(float.MaxValue);
        var high = new Vector2(float.MinValue);
        foreach (var point in points)
        {
            low = Vector2.Min(low, point);
            high = Vector2.Max(high, point);
        }

        return (high.X - low.X) * (high.Y - low.Y);
    }

    [Fact]
    public void ACloudAcrossAWallHasTheSameOutlineFromEverySectorItReaches()
    {
        var gas = Galaxy.GasClouds!;
        var found = 0;
        for (var dx = -2; dx <= 2 && found == 0; dx++)
            for (var dz = -2; dz <= 2 && found == 0; dz++)
            {
                var sector = CloudSector.Offset(dx, 0, dz);
                var here = new GasCloudPlacement[GasCloudModule.MaxPerSector];
                var count = gas.Intersecting(Galaxy, sector, here);
                for (var i = 0; i < count; i++)
                {
                    var other = CloudSector.Offset(dx + 1, 0, dz);
                    var there = new GasCloudPlacement[GasCloudModule.MaxPerSector];
                    var thereCount = gas.Intersecting(Galaxy, other, there);
                    if (!there[..thereCount].Any(p => p.Anchor == here[i].Anchor))
                        continue;
                    found++;
                    var world = Double3.Zero;
                    var a = new Vector2[MapFootprint.MaxVertices];
                    var b = new Vector2[MapFootprint.MaxVertices];
                    var anchor = Galaxy.CenterOf(here[i].Anchor);
                    var y = anchor.Y + here[i].Cloud.Center.Y;
                    var n = MapFootprint.Cloud(here[i].Cloud, anchor, gas.Shape.X, gas.Shape.Y, y - 1, y + 1, world, a);
                    var m = MapFootprint.Cloud(there.First(p => p.Anchor == here[i].Anchor).Cloud, anchor, gas.Shape.X, gas.Shape.Y, y - 1, y + 1, world, b);
                    Assert.Equal(n, m);
                    Assert.Equal(a[..n], b[..m]);
                    break;
                }
            }

        Assert.True(found > 0);
    }

    private static MapFeatureCache Cache(List<IJob>? jobs = null) => new(job =>
    {
        if (jobs is null)
            job.Run();
        else
            jobs.Add(job);
    }, 0.5f);

    [Fact]
    public void TheCacheFillsASectorOnItsWorkerAndHoldsItsFieldsAndCloudsWithinTheBounds()
    {
        var jobs = new List<IJob>();
        var cache = Cache(jobs);
        cache.Update(Galaxy);
        Assert.False(cache.TryGet(Galaxy, CloudSector, out _));
        Assert.True(cache.Busy);
        Assert.Single(jobs);

        // While the worker has it the frame reads nothing and starts nothing else.
        Assert.False(cache.TryGet(Galaxy, WallWest, out _));
        Assert.Single(jobs);
        jobs[0].Run();
        cache.Update(Galaxy);
        Assert.False(cache.Busy);
        Assert.True(cache.TryGet(Galaxy, CloudSector, out var slot));

        var features = cache.Features(slot);
        Assert.Contains(features.ToArray(), f => f.Kind == MapFeatureKind.Cloud);
        Assert.Contains(features.ToArray(), f => f.Kind == MapFeatureKind.Field);
        Assert.InRange(features.Length, 1, MapFeatureCache.MaxFeatures);
        for (var i = 0; i < features.Length; i++)
        {
            Assert.InRange(cache.Outer(slot, i).Length, 3, MapFootprint.MaxVertices);
            Assert.InRange(cache.Inner(slot, i).Length, 0, MapFootprint.MaxVertices);
        }

        // The features are the clouds that reach the sector and the fields in reach, and nothing else.
        var clouds = new GasCloudPlacement[GasCloudModule.MaxPerSector];
        var reaching = Galaxy.GasClouds!.Intersecting(Galaxy, CloudSector, clouds);
        Assert.True(features.ToArray().Count(f => f.Kind == MapFeatureKind.Cloud) <= reaching);
        Assert.Equal(FieldsOf(CloudSector).Length, features.ToArray().Count(f => f.Kind == MapFeatureKind.Field));
    }

    [Fact]
    public void TheCacheRebuildsEverythingWhenTheGalaxyIsAnother()
    {
        var cache = Cache();
        cache.Update(Galaxy);
        Assert.False(cache.TryGet(Galaxy, WallWest, out _));
        cache.Update(Galaxy);
        Assert.True(cache.TryGet(Galaxy, WallWest, out var slot));
        var before = cache.Outer(slot, 0).ToArray();

        // Another seed is another galaxy: the old entry is gone, the new one is different.
        var other = Content.Galaxy.WithSeed(7);
        cache.Update(other);
        Assert.False(cache.TryGet(other, WallWest, out _));
        cache.Update(other);
        Assert.True(cache.TryGet(other, WallWest, out slot));
        var after = cache.Features(slot).Length == 0 ? [] : cache.Outer(slot, 0).ToArray();
        Assert.NotEqual(before, after);
        Assert.False(cache.TryGet(Galaxy, WallWest, out _));
    }

    [Fact]
    public void AJobThatFinishesAfterTheGalaxyChangedIsDiscarded()
    {
        var jobs = new List<IJob>();
        var cache = Cache(jobs);
        cache.Update(Galaxy);
        cache.TryGet(Galaxy, WallWest, out _);
        var other = Content.Galaxy.WithSeed(9);
        cache.Update(other);
        jobs[0].Run();
        cache.Update(other);
        Assert.False(cache.Busy);
        jobs.Clear();
        Assert.False(cache.TryGet(other, WallWest, out _));
        Assert.Single(jobs);
    }

    [Fact]
    public void SectorsThatShareAHomeSlotDoNotEvictEachOtherEveryFrame()
    {
        var cache = Cache();
        cache.Update(Galaxy);

        // Find two sectors with one home slot by trying until the hash collides.
        var homes = new Dictionary<int, SectorCoord>();
        SectorCoord a = default;
        SectorCoord b = default;
        for (var x = 0; x < 400 && b == default; x++)
        {
            var sector = new SectorCoord(x, 0, 0);
            var home = (int)((uint)(sector.X * 73856093 ^ sector.Y * 19349663 ^ sector.Z * 83492791) % MapFeatureCache.Slots);
            if (homes.TryGetValue(home, out a))
                b = sector;
            else
                homes[home] = sector;
        }

        Assert.NotEqual(default, b);
        for (var frame = 0; frame < 4; frame++)
        {
            cache.Update(Galaxy);
            cache.TryGet(Galaxy, a, out _);
            cache.Update(Galaxy);
            cache.TryGet(Galaxy, b, out _);
        }

        cache.Update(Galaxy);
        Assert.True(cache.TryGet(Galaxy, a, out var slotA));
        Assert.True(cache.TryGet(Galaxy, b, out var slotB));
        Assert.NotEqual(slotA, slotB);
    }

    [Fact]
    public void AMapOutlineBuildNowMatchesTheWorkersAndNeverGrowsPastTheBound()
    {
        var cache = Cache();
        var slot = cache.BuildNow(Galaxy, CloudSector);
        Assert.True(cache.TryGet(Galaxy, CloudSector, out var again));
        Assert.Equal(slot, again);
        Assert.True(cache.Features(slot).Length <= MapFeatureCache.MaxFeatures);
    }
}
