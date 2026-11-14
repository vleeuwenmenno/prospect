using System.Diagnostics;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Prospect.Game.World;
using Xunit;

namespace Prospect.Tests;

/// <summary>Rebased content, count headroom and opt-in CPU measurements without a window.</summary>
public sealed class AsteroidRetuningTests
{
    private static readonly GameContent Content = GameContent.Load();
    private static readonly SectorCoord[] Coords = [default, new(-40, 0, -29), new(8, 0, -8), new(1000, 0, 1000)];
    private static readonly string[] FormerMax = ["asteroidAmount=2", "asteroidSize=2", "asteroidFieldSize=1.5", "asteroidFrequency=2", "asteroidSpacing=2", "asteroidOreShare=2", "asteroidOreGrade=2", "asteroidCap=4096"];

    private static Galaxy World(params string[] assignments) =>
        Content.Galaxy.Under(Content.SettingCatalog, Content.SettingCatalog.Build(null, assignments)).WithSeed(42);

    private static Galaxy Former()
    {
        // These are the only two changes to the committed galaxy content.
        var json = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "content", "galaxy", "galaxy.json")))!;
        var module = json["sectorModules"]![0]!.AsObject();
        module.Remove("baseline");
        module["maxPerSector"] = 4096;
        return Galaxy.Parse(System.Text.Encoding.UTF8.GetBytes(json.ToJsonString()), "former preset", 42)
            .Under(Content.SettingCatalog, Content.SettingCatalog.Build(null, FormerMax));
    }

    [Fact]
    public void OneTimesMatchesFormerMaximumWhenCountCapMatches()
    {
        var former = Former();
        var current = World("asteroidCap=4096");
        var raw = Content.Galaxy.WithSeed(42);
        var applied = World();
        var a = new SectorContent();
        var b = new SectorContent();
        foreach (var coord in Coords.Append(Sectors.Densest(raw)))
        {
            former.Generate(coord, a);
            current.Generate(coord, b);
            Assert.Equal(former.Summarize(coord).AsteroidCount, current.Summarize(coord).AsteroidCount);
            Assert.Equal(Sectors.Hash(a), Sectors.Hash(b));
            raw.Generate(coord, a);
            applied.Generate(coord, b);
            Assert.Equal(Sectors.Hash(a), Sectors.Hash(b));
        }
    }

    [Fact]
    public void AmountHasRealHeadroomAndAbsoluteEndpointsStillControlCounts()
    {
        var sector = new SectorContent();
        var previous = 0;
        foreach (var amount in new[] { 1, 2, 5 })
        {
            // Small rocks close together, so that nearly every rock the home field proposes finds room.
            var galaxy = World($"asteroidAmount={amount}", "asteroidSize=0.25", "asteroidSpacing=0.5");
            galaxy.Generate(default, sector);
            Assert.Equal(1500 * amount, galaxy.AsteroidFields!.Proposed(galaxy, default, out var fields));
            Assert.Equal(1, fields);
            Assert.True(galaxy.Summarize(default).AsteroidCount >= 1500 * amount);
            Assert.True(sector.AsteroidCount > previous);
            previous = sector.AsteroidCount;
        }
        Assert.True(previous > 4096);

        // At five times the size the home field runs on into the sectors around it, and theirs into home.
        var capped = World("asteroidAmount=5", "asteroidCountMin=20480", "asteroidSize=0.25", "asteroidFieldSize=5", "asteroidSpacing=0.5");
        capped.Generate(default, sector);
        Assert.Equal(20480, capped.AsteroidFields!.Proposed(capped, default, out _));
        Assert.Equal(20480, capped.Summarize(default).AsteroidCount);
        Assert.InRange(sector.AsteroidCount, 4097, 20480);
        capped = World("asteroidCountMin=20480", "asteroidCap=64");
        capped.Generate(default, sector);
        Assert.Equal(64, capped.Summarize(default).AsteroidCount);
        Assert.InRange(sector.AsteroidCount, 1, 64);
    }

    [Theory]
    [InlineData(1, false, 0, 0, "8F2D633D2E399158")]
    [InlineData(2, false, 0, 0, "EA4340D7658DCAC9")]
    [InlineData(5, false, 0, 0, "79135253ED01588D")]
    [InlineData(5, true, 0, 0, "DCEBD9305F098398")]
    [InlineData(1, false, -26, -31, "A7EDE866BA60F8A3")]
    [InlineData(2, false, -26, -31, "0C4CC40B74AE0B95")]
    [InlineData(5, false, -26, -31, "2335622FB5772AC6")]
    [InlineData(5, true, -26, -31, "9C73B0E19B1AAB34")]
    public void PlacementOptimizationKeepsRecordedSectorBytes(int amount, bool all, int x, int z, string expected)
    {
        // Recorded with generation version 4: fields in world space, a stream for each rock and density noise.
        var assignments = new List<string> { $"asteroidAmount={amount}", "sectorSize=40", "gasSizeMin=8", "gasSizeMax=11.2", "gasHugeSizeMin=20", "gasHugeSizeMax=40" };
        if (all) assignments.AddRange(["asteroidSize=5", "asteroidFieldSize=5", "asteroidFrequency=5", "asteroidSpacing=5", "asteroidOreShare=5", "asteroidOreGrade=5"]);
        var galaxy = World(assignments.ToArray());
        var sector = new SectorContent();
        galaxy.Generate(new SectorCoord(x, 0, z), sector);
        Assert.Equal(expected, Sectors.Hash(sector).ToString("X16"));
    }

    [Fact]
    public void PlacementOverlapMatchesBothCompleteScanAndOriginalIndexAtExtremeReaches()
    {
        var random = new Random(173);
        var sector = new SectorContent { Asteroids = new Asteroid[1025], AsteroidCount = 1025 };
        sector.Rocks.Reset(0, 40000);
        Assert.Equal(16, sector.Rocks.QueryCellVisits(Vector3.Zero, 0));
        Assert.Equal(2147483648L, sector.Rocks.QueryCellVisits(Vector3.Zero, 3000000));
        var scanCases = 0;
        var indexCases = 0;
        foreach (var placed in new[] { 0, 1, 8, 1024 })
        {
            sector.Rocks.Reset(placed, 40000);
            for (var i = 0; i < placed; i++)
            {
                var radius = i == 0 ? 6000 : i < 4 ? 1024 : i < 8 ? 128 : 0.75f;
                var position = new Vector3(random.Next(-20000, 20001), random.Next(-20000, 20001), random.Next(-20000, 20001));
                sector.Asteroids[i] = new Asteroid { LocalPosition = position, Radius = radius };
                sector.Rocks.Add(i, position, radius);
            }
            // Unplaced entries can contain a previous sector and must never participate.
            sector.Asteroids[placed] = new Asteroid { Radius = float.MaxValue };
            foreach (var reach in new[] { 0f, 0.75f, 128f, 256f, 1024f, 6000f, 132000f })
                foreach (var point in new[] { Vector3.Zero, new Vector3(-20000), new Vector3(20000), new Vector3(30000), sector.Asteroids[0].LocalPosition })
                {
                    var expected = false;
                    for (var i = 0; i < placed; i++)
                        expected |= Touches(sector.Asteroids[i], point, reach);
                    var indexed = false;
                    if (placed != 0)
                    {
                        var query = sector.Rocks.Near(point, reach);
                        while (query.Next(out var i))
                            if (Touches(sector.Asteroids[i], point, reach))
                            {
                                indexed = true;
                                break;
                            }
                    }
                    Assert.Equal(expected, indexed);
                    Assert.Equal(expected, AsteroidFieldsModule.Overlaps(sector, placed, point, reach));
                    if (sector.Rocks.QueryCellVisits(point, reach) > placed) scanCases++;
                    else indexCases++;
                }
        }
        Assert.True(scanCases > 0 && indexCases > 0);

        // Strict inequality preserves touching surfaces, including the next representable point on either side.
        sector.Rocks.Reset(1, 40000);
        sector.Asteroids[0] = new Asteroid { Radius = 128 };
        sector.Rocks.Add(0, Vector3.Zero, 128);
        foreach (var distance in new[] { MathF.BitDecrement(256), 256, MathF.BitIncrement(256) })
            Assert.Equal(distance < 256, AsteroidFieldsModule.Overlaps(sector, 1, new Vector3(distance, 0, 0), 128));

        static bool Touches(in Asteroid rock, Vector3 position, float reach)
        {
            var dx = position.X - rock.LocalPosition.X;
            var dy = position.Y - rock.LocalPosition.Y;
            var dz = position.Z - rock.LocalPosition.Z;
            var apart = reach + rock.Radius;
            return dx * dx + dy * dy + dz * dz < apart * apart;
        }
    }

    /// <summary>Placement agrees with a complete scan through reused grids and strict contact boundaries.</summary>
    [Theory]
    [InlineData(173)]
    [InlineData(2026)]
    [InlineData(42)]
    public void PlacementMatchesScanAcrossGridPopulationsAndResets(int seed)
    {
        var random = new Random(seed);
        var index = new RockIndex();
        var rocks = new Asteroid[1024];
        // Reuse storage through populated, empty and single-level sectors.
        foreach (var population in new[] { 3, 0, 1, 2, 3 })
        {
            index.Reset(rocks.Length, 40000);
            for (var i = 0; i < rocks.Length; i++)
            {
                var radius = population switch
                {
                    0 => 0.75f,
                    1 => 128f,
                    2 => 1024f,
                    _ => i < 4 ? 6000f : i < 128 ? 1024f : 128f
                };
                var position = new Vector3(random.Next(-20000, 20001), random.Next(-20000, 20001), random.Next(-20000, 20001));
                rocks[i] = new Asteroid { LocalPosition = position, Radius = radius };
                index.Add(i, position, radius);
            }
            for (var probe = 0; probe < 2000; probe++)
            {
                var reach = probe % 7 == 0 ? 6000f : random.NextSingle() * 256f;
                var point = probe % 2 == 0
                    ? new Vector3(random.Next(-22000, 22001), random.Next(-22000, 22001), random.Next(-22000, 22001))
                    : rocks[probe % rocks.Length].LocalPosition;
                // Boundary neighbours exercise strict touching on the indexed path as well as the scan.
                if (probe % 2 != 0)
                {
                    var distance = reach + rocks[probe % rocks.Length].Radius;
                    point.X += probe % 3 == 0 ? MathF.BitDecrement(distance) : probe % 3 == 1 ? distance : MathF.BitIncrement(distance);
                }
                var expected = false;
                foreach (ref readonly var rock in rocks.AsSpan())
                {
                    var dx = point.X - rock.LocalPosition.X;
                    var dy = point.Y - rock.LocalPosition.Y;
                    var dz = point.Z - rock.LocalPosition.Z;
                    var apart = reach + rock.Radius;
                    expected |= dx * dx + dy * dy + dz * dz < apart * apart;
                }
                Assert.Equal(expected, index.PlacementOverlaps(rocks, rocks.Length, point, reach));
            }
            index.Reset(0, 40000);
            Assert.False(index.PlacementOverlaps(rocks, 0, Vector3.Zero, 6000));
        }
    }

    [Theory]
    [InlineData(20, "86A13E900A92B076")]
    [InlineData(80, "9030D0483872B74C")]
    [InlineData(160, "7ADDD50EB8C590C0")]
    public void SectorSizeOptimizationKeepsRecordedBytes(int km, string expected)
    {
        var galaxy = World($"sectorSize={km}").WithSeed(11);
        var sector = new SectorContent();
        galaxy.Generate(new SectorCoord(-39, 0, -19), sector);
        Assert.Equal(expected, Sectors.Hash(sector).ToString("X16"));
    }

    [Fact]
    public void CachedPlacementBlockerAgreesWithFreshSearch()
    {
        var galaxy = World("sectorSize=160").WithSeed(11);
        var sector = new SectorContent();
        galaxy.Generate(new SectorCoord(-39, 0, -19), sector);
        var random = new Random(329);
        var blocker = -1;
        for (var i = 0; i < 10000; i++)
        {
            var rock = sector.Asteroids[random.Next(sector.AsteroidCount)];
            var point = rock.LocalPosition + new Vector3(random.NextSingle() * 4000 - 2000,
                random.NextSingle() * 4000 - 2000, random.NextSingle() * 4000 - 2000);
            var reach = random.NextSingle() * 6000;
            var expected = false;
            foreach (var other in sector.Asteroids.AsSpan(0, sector.AsteroidCount))
            {
                var dx = point.X - other.LocalPosition.X;
                var dy = point.Y - other.LocalPosition.Y;
                var dz = point.Z - other.LocalPosition.Z;
                var apart = reach + other.Radius;
                expected |= dx * dx + dy * dy + dz * dz < apart * apart;
            }
            Assert.Equal(expected, sector.Rocks.PlacementOverlaps(sector.Asteroids,
                sector.AsteroidCount, point, reach, ref blocker));
        }
    }

    [Fact]
    public void DebugBudgetSectorKeepsItsRecordedBytes()
    {
        var galaxy = World("sectorSize=40", "gasSizeMin=8", "gasSizeMax=11.2", "gasHugeSizeMin=20", "gasHugeSizeMax=40").WithSeed(11);
        var coord = Sectors.Densest(galaxy);
        var sector = new SectorContent();
        galaxy.Generate(coord, sector);
        Assert.Equal(new SectorCoord(-39, 0, -19), coord);
        Assert.Equal(8711, sector.AsteroidCount);
        Assert.Equal("1173DCB7646A496B", Sectors.Hash(sector).ToString("X16"));
    }

    [Fact]
    public void CellTruncationPreservesTheOriginalFloorMapping()
    {
        var index = new RockIndex();
        var cell = typeof(RockIndex).GetMethod("Cell", BindingFlags.Instance | BindingFlags.NonPublic)!
            .CreateDelegate<Func<float, int, int>>(index);
        foreach (var sectorSize in new[] { 40000d, RockIndex.MaxSectorSize })
        {
            index.Reset(0, sectorSize);
            var half = (float)(sectorSize * 0.5);
            for (var level = 0; level < 2; level++)
            {
                var edge = level == 0 ? RockIndex.FineCell : RockIndex.CoarseCell;
                for (var i = -2; i <= 1025; i++)
                {
                    var boundary = i * edge - half;
                    foreach (var position in new[] { MathF.BitDecrement(boundary), boundary, MathF.BitIncrement(boundary) })
                        Assert.Equal(Math.Clamp((int)MathF.Floor((position + half) / edge), 0, 1023), cell(position, level));
                }
                foreach (var position in new[] { float.MinValue, -3000000f, 3000000f, float.MaxValue })
                    Assert.Equal(Math.Clamp((int)MathF.Floor((position + half) / edge), 0, 1023), cell(position, level));
            }
        }
    }

    [Fact]
    public void RecordCpuLimitsWhenRequested()
    {
        var output = Environment.GetEnvironmentVariable("PROSPECT_ASTEROID_MEASUREMENTS");
        if (string.IsNullOrEmpty(output)) return;
        var results = new List<object>();
        foreach (var coord in Coords)
        {
            Measure("former-max", Former(), coord);
            foreach (var amount in new[] { 1, 2, 5 })
                Measure($"amount-{amount}", World($"asteroidAmount={amount}"), coord);
            Measure("all-5", World("asteroidAmount=5", "asteroidSize=5", "asteroidFieldSize=5", "asteroidFrequency=5", "asteroidSpacing=5", "asteroidOreShare=5", "asteroidOreGrade=5"), coord);
        }
        var dense = Sectors.Densest(World());
        Measure("former-max-dense", Former(), dense);
        foreach (var amount in new[] { 1, 2, 5 })
            Measure($"dense-amount-{amount}", World($"asteroidAmount={amount}"), dense);
        Measure("dense-all-5", World("asteroidAmount=5", "asteroidSize=5", "asteroidFieldSize=5", "asteroidFrequency=5", "asteroidSpacing=5", "asteroidOreShare=5", "asteroidOreGrade=5"), dense);
        File.WriteAllText(output, JsonSerializer.Serialize(new
        {
            seed = 42,
            repetitions = 7,
            queriesPerRepetition = 10000,
            asteroidBytes = Marshal.SizeOf<Asteroid>(),
            pickHitBytes = Marshal.SizeOf<PickHit>(),
            results
        }, new JsonSerializerOptions { WriteIndented = true }));

        void Measure(string preset, Galaxy galaxy, SectorCoord coord)
        {
            var sector = new SectorContent();
            for (var i = 0; i < 3; i++) galaxy.Generate(coord, sector);
            Query(sector);
            var generationMs = new double[7];
            var queryMs = new double[7];
            var generationBytes = new long[7];
            var queryBytes = new long[7];
            for (var i = 0; i < 7; i++)
            {
                var generation = new AllocationProbe();
                while (generation.Measuring())
                {
                    var start = Stopwatch.GetTimestamp();
                    galaxy.Generate(coord, sector);
                    generationMs[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                }
                generationBytes[i] = generation.Bytes;
                var query = new AllocationProbe();
                while (query.Measuring())
                {
                    var start = Stopwatch.GetTimestamp();
                    Query(sector);
                    queryMs[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                }
                queryBytes[i] = query.Bytes;
                Assert.Equal(0, queryBytes[i]);
                Assert.Equal(0, generationBytes[i]);
            }
            var freshAllocation = new AllocationProbe();
            var freshGenerationMs = 0.0;
            while (freshAllocation.Measuring())
            {
                var freshStart = Stopwatch.GetTimestamp();
                galaxy.Generate(coord, new SectorContent());
                freshGenerationMs = Stopwatch.GetElapsedTime(freshStart).TotalMilliseconds;
            }
            var freshBytes = freshAllocation.Bytes;
            long retained = 0;
            foreach (var owner in new object[] { sector, sector.Rocks })
                foreach (var field in owner.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                    if (field.GetValue(owner) is Array array)
                        retained += 24 + (long)array.Length * Marshal.SizeOf(array.GetType().GetElementType()!);
            var radii = sector.Asteroids.Take(sector.AsteroidCount).Select(r => r.Radius).Order().ToArray();
            var low = new Vector3(float.MaxValue);
            var high = new Vector3(float.MinValue);
            foreach (var rock in sector.Asteroids.AsSpan(0, sector.AsteroidCount))
            {
                low = Vector3.Min(low, rock.LocalPosition - new Vector3(rock.Radius));
                high = Vector3.Max(high, rock.LocalPosition + new Vector3(rock.Radius));
            }
            results.Add(new
            {
                preset,
                coordinate = coord.ToString(),
                planned = galaxy.Summarize(coord).AsteroidCount,
                actual = sector.AsteroidCount,
                generationHash = Sectors.Hash(sector).ToString("X16"),
                freshGenerationMs,
                generationMs,
                queryMs,
                generationBytes,
                queryBytes,
                freshGenerationBytes = freshBytes,
                retainedArrayBytes = retained,
                radiusMin = radii.Length == 0 ? 0 : radii[0],
                radiusMedian = radii.Length == 0 ? 0 : radii[radii.Length / 2],
                radiusMax = radii.Length == 0 ? 0 : radii[^1],
                dimensions = radii.Length == 0 ? new float[3] : new[] { high.X - low.X, high.Y - low.Y, high.Z - low.Z }
            });
        }

        static void Query(SectorContent sector)
        {
            for (var i = 0; i < 10000; i++)
            {
                var center = sector.AsteroidCount == 0 ? Vector3.Zero : sector.Asteroids[i % sector.AsteroidCount].LocalPosition;
                var query = sector.Rocks.Near(center, 20);
                while (query.Next(out var rock))
                    if (rock >= sector.AsteroidCount) throw new InvalidOperationException("Index outside generated list");
            }
        }
    }
}
