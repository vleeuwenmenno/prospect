using System.Numerics;
using Brutal.Numerics;
using Prospect.Client.Render;
using Prospect.Client.World;
using Prospect.Engine.IO;
using Prospect.Engine.Jobs;
using Prospect.Game.World;
using Xunit;

namespace Prospect.Tests;

/// <summary>
/// Far gas without a GPU: which clouds are known around a sector, how a cloud is shared between its levels of
/// detail, the order its draws are queued in and the part of the view they are cut to. No GLSL runs here.
/// </summary>
public sealed class GasLevelsTests
{
    private static readonly GameContent Content = GameContent.Load();
    private static readonly Galaxy Galaxy = Content.Galaxy.WithSeed(0x50524F5350454354);
    private static readonly GasTiers Tiers = GasTiers.Load();

    [Fact]
    public void QueryAgreesWithFullGenerationNearestSectorsFirst()
    {
        var center = new SectorCoord(-3, 0, 7);
        var found = new GasCloudPlacement[GasCloudField.Capacity];
        var count = GasCloudQuery.Collect(Galaxy, center, 3, found);

        var full = new SectorContent();
        var expected = 0;
        for (var x = -3; x <= 3; x++)
            for (var y = -3; y <= 3; y++)
                for (var z = -3; z <= 3; z++)
                {
                    var coord = center.Offset(x, y, z);
                    Galaxy.Generate(coord, full);
                    var index = Array.FindIndex(found, 0, count, placed => placed.Anchor == coord);
                    Assert.Equal(full.Summary.GasCloudCount != 0, index >= 0);
                    if (index < 0) continue;
                    expected++;
                    Assert.Equal(full.GasCloud, found[index].Cloud);
                    Assert.Equal(Galaxy.CenterOf(coord) + full.GasCloud.Center, GasCloudQuery.CenterOf(Galaxy, found[index]));
                }

        Assert.Equal(expected, count);
        Assert.Contains(new SectorCoord(-3, 0, 5), found.Take(count).Select(placed => placed.Anchor));
        var shells = found.Take(count).Select(placed => Shell(placed.Anchor, center)).ToArray();
        Assert.Equal(shells.Order(), shells);
    }

    [Fact]
    public void QueryKeepsTheNearestWhenFullAndTakesNoMemory()
    {
        var all = new GasCloudPlacement[GasCloudField.Capacity];
        var count = GasCloudQuery.Collect(Galaxy, default, 7, all);
        Assert.InRange(count, 8, GasCloudField.Capacity - 1);

        var few = new GasCloudPlacement[5];
        Assert.Equal(5, GasCloudQuery.Collect(Galaxy, default, 7, few));
        Assert.Equal(all.Take(5), few);
        Assert.Equal(0, GasCloudQuery.Collect(Galaxy.WithModules(Galaxy.AsteroidFields!), default, 2, all));

        var before = GC.GetAllocatedBytesForCurrentThread();
        GasCloudQuery.Collect(Galaxy, new(4, -1, 9), 7, all);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void LevelsShareACloudContinuouslyOutToTheViewRange()
    {
        var sector = (float)Galaxy.SectorSize;
        var range = Tiers.ViewRangeSectors * sector;
        Assert.True(range >= 2 * sector, "gas must show from several sectors away");
        foreach (var radius in new[] { 4800f, 12000f, sector })
        {
            // Up close nothing but the full march is drawn, as before there were levels.
            Tiers.Shares(radius * 2.4f, radius, sector, out var near, out var mid, out var far);
            if (radius * 2.4f < range * (1 - Tiers.RangeFade)) Assert.Equal((1f, 0f, 0f), (near, mid, far));

            var last = (Near: 1f, Mid: 0f, Far: 0f);
            var seen = (Mid: false, Far: false);
            for (var distance = 0f; distance <= range * 1.05f; distance += 20f)
            {
                Tiers.Shares(distance, radius, sector, out near, out mid, out far);
                Assert.InRange(near, 0, 1);
                Assert.InRange(mid, -1e-6f, 1);
                Assert.InRange(far, 0, 1);
                Assert.InRange(near + mid + far, 0, 1 + 1e-5f);
                // No level appears or goes at once: 20 m of travel never moves a share by more than a hundredth.
                Assert.True(MathF.Abs(near - last.Near) < 0.01f && MathF.Abs(mid - last.Mid) < 0.01f && MathF.Abs(far - last.Far) < 0.01f,
                    $"a level jumped at {distance} m from a cloud of {radius} m");
                // The far level only starts once the full march has gone: at most two draws per cloud.
                Assert.True(near == 0 || far == 0);
                if (distance < range * (1 - Tiers.RangeFade)) Assert.Equal(1f, near + mid + far, 1e-5f);
                seen = (seen.Mid || mid > 0.99f, seen.Far || far > 0.99f);
                last = (near, mid, far);
            }

            Assert.Equal((0f, 0f, 0f), last);
            // A cloud of ordinary size goes through every level before the view range ends.
            if (radius < 6000) Assert.True(seen.Mid && seen.Far);
        }
    }

    [Fact]
    public void LevelFilesOutOfRangeAreRefused()
    {
        var shipped = File.ReadAllText(AppPaths.Content("visuals", "gas.json"));
        var path = Path.Combine(Path.GetTempPath(), "prospect-gas-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path, shipped);
            Assert.Equal(Tiers.MidSamples, GasTiers.Load(path).MidSamples);
            foreach (var (old, bad) in new[]
            {
                ("\"viewRangeSectors\": 5", "\"viewRangeSectors\": 9"),
                ("\"rangeFade\": 0.3", "\"rangeFade\": 0"),
                ("\"nearEnd\": 3.0", "\"nearEnd\": 1.0"),
                ("\"midEnd\": 9.0", "\"midEnd\": 3.5"),
                ("\"midSamples\": 24", "\"midSamples\": 65"),
                ("\"farSamples\": 10", "\"farSamples\": 3"),
                ("\"farDetail\": 1", "\"farDetail\": 3"),
                ("\"maxDraws\": 32", "\"maxDraws\": 65"),
                ("\"fromHalfSize\": 7000", "\"fromHalfSize\": 12000"),
                ("\"strands\": 0.9", "\"strands\": 1.5"),
                ("\"stepBend\": 0.95", "\"stepBend\": 1"),
                ("\"reach\": 0.8", "\"reach\": 0"),
                ("\"clearing\": 0.6", "\"clearing\": 1"),
            })
            {
                Assert.Contains(old, shipped);
                File.WriteAllText(path, shipped.Replace(old, bad));
                Assert.Throws<InvalidDataException>(() => GasTiers.Load(path));
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void SourceNamesHugeCloudsAndCloudsThatReachInFromOtherSectors()
    {
        var gas = Galaxy.GasClouds!;
        var edge = (float)Galaxy.SectorSize;
        var found = new GasCloudPlacement[GasCloudField.Capacity];
        var reaching = new GasCloudPlacement[GasCloudModule.MaxPerSector];

        // A huge cloud is listed by its anchor from every sector around, whether or not its box reaches that one.
        var anchor = new SectorCoord(133, 0, -77);
        Assert.True(gas.TryGetCloud(Galaxy, anchor, out var huge));
        Assert.True(huge.HalfSize.X > 0.4f * edge);
        foreach (var beside in new[] { anchor.Offset(-1, 0, 0), anchor.Offset(1, 1, 0), anchor.Offset(0, -1, 1) })
            Assert.Contains(new GasCloudPlacement(anchor, huge), found.AsSpan(0, new GeneratedGasClouds().Collect(Galaxy, beside, 1, found)).ToArray());
        // Its box lies across the wall to the sector at -x, where the module lists it too.
        Assert.Contains(new GasCloudPlacement(anchor, huge), reaching.AsSpan(0, gas.Intersecting(Galaxy, anchor.Offset(-1, 0, 0), reaching)).ToArray());

        // Whatever the generation module says reaches a sector or a sphere, the source has listed by its anchor.
        var crossing = 0;
        for (var i = 0; i < 12 * 3 * 12; i++)
        {
            var sector = new SectorCoord(126 + i % 12, i / 12 % 3 - 1, -82 + i / 36);
            var listed = found.AsSpan(0, GasCloudQuery.Collect(Galaxy, sector, 1, found)).ToArray();
            foreach (var placement in reaching.AsSpan(0, gas.Intersecting(Galaxy, sector, reaching)))
            {
                Assert.Contains(placement, listed);
                if (placement.Anchor != sector) crossing++;
            }

            var wide = found.AsSpan(0, GasCloudQuery.Collect(Galaxy, sector, 4, found)).ToArray();
            var near = new GasCloudPlacement[64];
            foreach (var placement in near.AsSpan(0, gas.Intersecting(Galaxy, Galaxy.CenterOf(sector), 2.5 * edge, near)))
                Assert.Contains(placement, wide);
        }
        Assert.True(crossing > 0, "no cloud in the sample reaches a sector it is not anchored in");
    }

    [Fact]
    public void OnlyLargeCloudsTakeTheCloseUpFigures()
    {
        // The historical small-cloud preset remains unchanged by close-up treatment.
        var Galaxy = Content.WithSettings(Content.SettingCatalog.Build(null, ["sectorSize=40", "gasSizeMin=8", "gasSizeMax=11.2", "gasHugeSizeMin=20", "gasHugeSizeMax=40"])).Galaxy.WithSeed(0x50524F5350454354);
        var gas = Galaxy.GasClouds!;
        var found = new GasCloudPlacement[GasCloudField.Capacity];
        var count = GasCloudQuery.Collect(Galaxy, new(130, 0, -80), 9, found);
        var ordinary = 0;
        var huge = 0;
        foreach (var placement in found.AsSpan(0, count))
        {
            var figures = Tiers.CloseUpFor(placement.Cloud.HalfSize.X);
            if (placement.Cloud.HalfSize.X <= Tiers.CloseUp.FromHalfSize)
            {
                Assert.Equal(default, figures);
                ordinary++;
            }
            else
            {
                Assert.Equal(new float4(Tiers.CloseUp.Strands, Tiers.CloseUp.StepBend, Tiers.CloseUp.Reach, Tiers.CloseUp.Clearing), figures);
                huge++;
            }
        }
        Assert.True(ordinary > 20 && huge > 0, $"{ordinary} ordinary and {huge} huge clouds in the sample");

        // Between the two sizes the figures rise evenly; the reach does not, it is a distance.
        var half = Tiers.CloseUpFor((Tiers.CloseUp.FromHalfSize + Tiers.CloseUp.FullHalfSize) / 2);
        Assert.Equal(Tiers.CloseUp.Strands / 2, half.X, 1e-5f);
        Assert.Equal(Tiers.CloseUp.StepBend / 2, half.Y, 1e-5f);
        Assert.Equal(Tiers.CloseUp.Reach, half.Z);
        Assert.Equal(Tiers.CloseUp.Clearing / 2, half.W, 1e-5f);
        Assert.Equal(default, Tiers.CloseUpFor(Tiers.CloseUp.FromHalfSize));
    }

    private static GasConstants Draw(float distance, float level, float seed = 0) =>
        new() { Center = new float4(0, 0, 0, seed), Motion = new float4(0, 1, 0, distance), Budget = new float4(24, level, 0, 0) };

    [Fact]
    public void QueueDrawsFarthestFirstWithTheNearestFullCloudLast()
    {
        var queue = new GasQueue();
        queue.Begin();
        queue.Add(Draw(9000, GasConstants.Full, 1), SceneRenderer.WholeView);
        queue.Add(Draw(60000, GasConstants.Far, 2), SceneRenderer.WholeView);
        queue.Add(Draw(30000, GasConstants.Middle, 3), SceneRenderer.WholeView);
        queue.Add(Draw(150000, GasConstants.Far, 4), SceneRenderer.WholeView);
        // One cloud crossing from the middle to the far level: the cheaper draw is queued first and stays under.
        queue.Add(Draw(50000, GasConstants.Far, 5), SceneRenderer.WholeView);
        queue.Add(Draw(50000, GasConstants.Middle, 6), SceneRenderer.WholeView);
        // A second full cloud, nearer: it becomes the primary and the first one joins the order.
        queue.Add(Draw(4000, GasConstants.Full, 7), new float4(0.25f, 0.25f, 0.5f, 0.5f));
        // A far draw that is nearer than another cloud is not sky: it must be drawn over that cloud.
        queue.Add(Draw(20000, GasConstants.Far, 8), SceneRenderer.WholeView);

        Assert.True(queue.HasPrimary);
        Assert.Equal(7, queue.Primary.Center.W);
        Assert.Equal(new float4(0.25f, 0.25f, 0.5f, 0.5f), queue.PrimaryBounds);
        var order = Enumerable.Range(0, queue.Count).Select(i => queue[i].Center.W).ToArray();
        Assert.Equal([4f, 2f, 5f, 6f, 3f, 8f, 1f], order);
        Assert.Equal(3, queue.SkyCount);

        queue.Begin();
        Assert.False(queue.HasPrimary);
        Assert.Equal(0, queue.Count);
        Assert.Equal(0, queue.SkyCount);
    }

    [Fact]
    public void FullQueueLeavesOutTheFarthestAndNeverAllocates()
    {
        var queue = new GasQueue();
        queue.Begin(4);
        for (var i = 1; i <= 10; i++) queue.Add(Draw(i * 1000, GasConstants.Far, i), SceneRenderer.WholeView);
        queue.Add(Draw(99000, GasConstants.Far, 99), SceneRenderer.WholeView);
        Assert.Equal(4, queue.Count);
        Assert.Equal([4f, 3f, 2f, 1f], Enumerable.Range(0, queue.Count).Select(i => queue[i].Center.W));
        Assert.Equal(4, queue.SkyCount);

        var before = GC.GetAllocatedBytesForCurrentThread();
        queue.Begin(GasQueue.Capacity + 100);
        for (var i = 0; i < 200; i++) queue.Add(Draw((i * 37 % 101) * 500, i % 3, i), SceneRenderer.WholeView);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.Equal(GasQueue.Capacity, queue.Count);
        for (var i = 1; i < queue.Count; i++) Assert.True(queue[i - 1].Motion.W >= queue[i].Motion.W);
    }

    [Fact]
    public void ScreenBoundsNeverCutASphere()
    {
        const float Tan = 0.6f, Aspect = 2.2f;
        var random = new Random(7);
        float Next(float from, float to) => from + (to - from) * random.NextSingle();
        for (var i = 0; i < 400; i++)
        {
            var center = new Vector3(Next(-90000, 90000), Next(-60000, 60000), Next(-20000, 200000));
            var radius = Next(500, 70000);
            var bounds = WorldView.SphereBounds(center, radius, Tan, Aspect);
            Assert.True(bounds.X <= bounds.Z && bounds.Y <= bounds.W);
            for (var p = 0; p < 200; p++)
            {
                var direction = Vector3.Normalize(new Vector3(Next(-1, 1), Next(-1, 1), Next(-1, 1)) + new Vector3(1e-3f));
                var point = center + direction * radius * Next(0, 1);
                if (point.Z < 0.25f) continue;
                var x = point.X / (point.Z * Tan * Aspect) * 0.5f + 0.5f;
                var y = 0.5f - point.Y / (point.Z * Tan) * 0.5f;
                // A point on screen is inside the bounds; the bounds themselves stop a view-width past the edges.
                if (x is < 0 or > 1 || y is < 0 or > 1) continue;
                Assert.InRange(x, bounds.X - 1e-4f, bounds.Z + 1e-4f);
                Assert.InRange(y, bounds.Y - 1e-4f, bounds.W + 1e-4f);
            }
        }

        // A camera inside the sphere sees it everywhere; a small sphere ahead is cut tightly.
        var around = WorldView.SphereBounds(new Vector3(100, -50, 200), 5000, Tan, Aspect);
        Assert.True(around.X <= 0 && around.Y <= 0 && around.Z >= 1 && around.W >= 1);
        var ahead = WorldView.SphereBounds(new Vector3(0, 0, 100000), 5000, Tan, Aspect);
        Assert.InRange(ahead.Z - ahead.X, 0.03f, 0.05f);
        Assert.InRange(ahead.W - ahead.Y, 0.08f, 0.09f);
    }

    /// <summary>A source the test holds back, to see what the frame does while a list is being worked out.</summary>
    private sealed class HeldSource : IGasCloudSource
    {
        public readonly ManualResetEventSlim Release = new(true);
        public int Calls;

        public int Collect(Galaxy galaxy, SectorCoord center, int radius, Span<GasCloudPlacement> into)
        {
            Release.Wait();
            Interlocked.Increment(ref Calls);
            into[0] = new GasCloudPlacement(center, new GasCloud(Vector3.Zero, new Vector3(radius), 1));
            return 1;
        }
    }

    [Fact]
    public void FieldFollowsTheCameraSectorWithoutTheFrameWaiting()
    {
        using var jobs = new JobSystem(1, "GasField");
        var source = new HeldSource();
        var field = new GasCloudField(jobs, source, Tiers.ViewRangeSectors);
        Assert.Equal((int)MathF.Ceiling(Tiers.ViewRangeSectors) + 2, field.Radius);
        Assert.True(field.Clouds(Galaxy).IsEmpty);

        void Frames(SectorCoord center, Func<bool> until)
        {
            var deadline = Environment.TickCount64 + 10_000;
            while (!until() && Environment.TickCount64 < deadline)
            {
                field.Update(Galaxy, center);
                jobs.Pump();
                Thread.Sleep(1);
            }
            Assert.True(until());
        }

        var first = new SectorCoord(2, 0, -1);
        Frames(first, () => !field.Clouds(Galaxy).IsEmpty);
        Assert.Equal(first, field.Clouds(Galaxy)[0].Anchor);
        Assert.Equal(field.Radius, field.Clouds(Galaxy)[0].Cloud.HalfSize.X);
        Assert.Equal(1, source.Calls);
        // Staying in the sector asks for nothing more.
        for (var i = 0; i < 20; i++) { field.Update(Galaxy, first); jobs.Pump(); }
        Assert.Equal(1, source.Calls);

        // The next sector's list is held back: the frame goes on with the old one.
        var second = new SectorCoord(3, 0, -1);
        source.Release.Reset();
        for (var i = 0; i < 20; i++) { field.Update(Galaxy, second); jobs.Pump(); }
        Assert.Equal(first, field.Clouds(Galaxy)[0].Anchor);
        source.Release.Set();
        Frames(second, () => field.Clouds(Galaxy)[0].Anchor == second);
        Assert.Equal(2, source.Calls);

        // Another galaxy's clouds are never shown as this one's, and its own list replaces them.
        var other = Galaxy.WithSeed(99);
        Assert.True(field.Clouds(other).IsEmpty);
        Frames(second, () => !field.Clouds(Galaxy).IsEmpty);
        var deadline = Environment.TickCount64 + 10_000;
        while (field.Clouds(other).IsEmpty && Environment.TickCount64 < deadline) { field.Update(other, second); jobs.Pump(); Thread.Sleep(1); }
        Assert.False(field.Clouds(other).IsEmpty);
        Assert.True(field.Clouds(Galaxy).IsEmpty);
    }

    [Fact]
    public void GeneratedSourceIsTheSharedQuery()
    {
        var expected = new GasCloudPlacement[GasCloudField.Capacity];
        var actual = new GasCloudPlacement[GasCloudField.Capacity];
        var count = GasCloudQuery.Collect(Galaxy, new(-3, 0, 5), 7, expected);
        Assert.Equal(count, new GeneratedGasClouds().Collect(Galaxy, new(-3, 0, 5), 7, actual));
        Assert.Equal(expected, actual);
        Assert.Equal(new SectorCoord(-3, 0, 5), actual[0].Anchor);
    }

    private static int Shell(SectorCoord a, SectorCoord b) => Math.Max(Math.Abs(a.X - b.X), Math.Max(Math.Abs(a.Y - b.Y), Math.Abs(a.Z - b.Z)));
}
