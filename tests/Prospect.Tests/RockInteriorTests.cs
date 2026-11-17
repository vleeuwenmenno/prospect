using System.Numerics;
using Prospect.Game.World;
using Xunit;

namespace Prospect.Tests;

/// <summary>What is inside an asteroid: layers, shares that add up to its composition, and the same bits everywhere.</summary>
public sealed class RockInteriorTests(ITestOutputHelper output)
{
    private static readonly GameContent Content = GameContent.Load();
    private static readonly Galaxy World = Content.Galaxy.WithSeed(11);

    // Home, a dense sector, a very rich one with five resources, and a sector a giant lies in.
    private static readonly SectorCoord[] Coords = [default, new(-40, 0, -29), new(8, 0, -8), new(3, 0, 1)];

    /// <summary>Rocks of every kind from the sample sectors: with and without mix-ins, pockets and ore.</summary>
    private static List<(RockRef Rock, Asteroid Asteroid)> Rocks(Galaxy galaxy, int perSector)
    {
        var list = new List<(RockRef, Asteroid)>();
        var content = new SectorContent();
        foreach (var coord in Coords)
        {
            galaxy.Generate(coord, content);
            var stride = Math.Max(1, content.AsteroidCount / perSector);
            for (var i = 0; i < content.AsteroidCount; i += stride)
                list.Add((new RockRef(coord, i), content.Asteroids[i]));
        }

        return list;
    }

    [Fact]
    public void TheWholeInteriorAddsUpToTheRocksComposition()
    {
        var rules = World.Interiors!;
        var checkedRocks = 0;
        var withPocket = 0;
        var withMix = 0;
        var worst = 0f;
        foreach (var (rock, asteroid) in Rocks(World, 24))
        {
            if (asteroid.Resource < 0)
                continue;
            RockInterior.Describe(World, rock, asteroid, out var interior);
            var (main, mix1, mix2, _) = Integrate(interior, 44);
            Near(asteroid.Grade, main, "main ore");
            Near(asteroid.Mix1Share, mix1, "first mix-in");
            Near(asteroid.Mix2Share, mix2, "second mix-in");
            checkedRocks++;
            if (interior.PocketMix != 0)
                withPocket++;
            if (asteroid.Mix1 >= 0)
                withMix++;

            void Near(float published, float measured, string what)
            {
                if (published > 0.01f)
                    worst = Math.Max(worst, Math.Abs(measured - published) / published);
                Assert.True(Math.Abs(measured - published) <= rules.BulkTolerance * published + 0.002f,
                    $"{rock}: {what} published {published:F4}, interior holds {measured:F4} (crust {interior.CrustVolume:F3}, veins {interior.VeinVolume:F3}, pocket {interior.PocketVolume:F3})");
            }
        }

        output.WriteLine($"{checkedRocks} rocks, {withMix} with a mix-in, {withPocket} with a core pocket; worst error {worst:P1} of a share over 1 %");
        Assert.True(checkedRocks >= 30 && withPocket >= 3 && withMix >= 8, $"{checkedRocks} rocks, {withMix} mixed, {withPocket} pockets in the sample");
    }

    [Fact]
    public void LayersLieWhereTheyShould()
    {
        var rules = World.Interiors!;
        var veins = 0;
        var unstable = 0;
        foreach (var (rock, asteroid) in Rocks(World, 24))
        {
            RockInterior.Describe(World, rock, asteroid, out var interior);
            var (_, _, _, layers) = Integrate(interior, 24, (sample, inner) =>
            {
                Assert.True(sample.Ore <= rules.MaxLocalOre + 1e-5f, $"{rock}: {sample.Ore} ore at a point");
                Assert.True(sample.MainShare >= 0 && sample.Mix1Share >= 0 && sample.Mix2Share >= 0);
                if (sample.Unstable)
                    Assert.Equal(0f, sample.Ore);
                if (sample.Layer == RockLayer.Pocket && !sample.Unstable)
                    Assert.Equal(inner.PocketMix == 2 ? inner.Mix2 : inner.Mix1, sample.Resource);
                if (asteroid.Resource < 0)
                    Assert.Equal(-1, sample.Resource);
            });

            // Just under the surface is crust; the centre never is.
            RockInterior.Describe(World, rock, asteroid, out interior);
            for (var i = 0; i < 64; i++)
            {
                var d = Fibonacci(i, 64);
                RockInteriorSurface.Lookup(asteroid.Shape, d.X, d.Y, d.Z, out var surface, out _);
                interior.Sample(d * (surface * asteroid.Radius * 0.998f), out var skin);
                Assert.Equal(RockLayer.Crust, skin.Layer);
                Assert.True(skin.Inside);
                interior.Sample(d * (surface * asteroid.Radius * 1.05f), out var outside);
                Assert.False(outside.Inside);
            }

            interior.Sample(Vector3.Zero, out var centre);
            Assert.NotEqual(RockLayer.Crust, centre.Layer);
            if (asteroid.Shape == AsteroidShape.Icicle)
                Assert.Equal(RockMaterial.Ice, centre.Material);
            veins += interior.VeinCount;
            unstable += layers[4];

            // Veins are richer than the mantle, and the mantle than the crust, unless a rich rock is evened out.
            if (asteroid.Resource >= 0)
            {
                Assert.True(interior.VeinShares.X >= interior.MantleShares.X && interior.MantleShares.X >= interior.CrustShares.X);
                Assert.True(interior.CrustVolume is > 0.05f and < 0.6f, $"crust {interior.CrustVolume}");
            }
        }

        Assert.True(veins > 50 && unstable > 0, $"{veins} veins, {unstable} unstable samples");
    }

    [Fact]
    public void AnInteriorIsTheSameOnEveryThread()
    {
        var rocks = Rocks(World, 16);
        var expected = Hash(World, rocks);
        var hashes = new ulong[8];
        Parallel.For(0, hashes.Length, new ParallelOptions { MaxDegreeOfParallelism = hashes.Length }, t => hashes[t] = Hash(World, rocks));
        foreach (var hash in hashes)
            Assert.Equal(expected, hash);
    }

    [Fact]
    public void InteriorsMatchTheRecordedOnesOnEveryMachine()
    {
        // Frozen copies of the galaxy and interior rules and a hash recorded on Linux x64. The same number must come
        // out on Windows and Apple Silicon: which layer a point is in is a discrete choice both sides make. If the
        // interior is changed on purpose, record the new hash; if this fails without such a change, something in it
        // depends on the machine.
        var fixtures = Path.Combine(AppContext.BaseDirectory, "fixtures");
        var galaxy = Galaxy.Load(Path.Combine(fixtures, "galaxy.json"), 0x50524F5350454354)
            .Under(Content.SettingCatalog, Content.SettingCatalog.Defaults)
            .WithInteriors(RockInteriorRules.Load(Path.Combine(fixtures, "interiors.json")));
        var hash = Hash(galaxy, Rocks(galaxy, 16));
        output.WriteLine($"interior hash {hash}");
        Assert.Equal(RecordedHash, hash);
    }

    private const ulong RecordedHash = 9236901811372772278;

    [Fact]
    public void AskingTakesNoMemory()
    {
        var content = new SectorContent();
        World.Generate(new SectorCoord(8, 0, -8), content);
        var asteroid = content.Asteroids[0];
        var rock = new RockRef(new SectorCoord(8, 0, -8), 0);
        RockInterior.Describe(World, rock, asteroid, out var warm);
        warm.Sample(Vector3.One, out _);
        warm.Average(Vector3.Zero, asteroid.Radius * 0.5f, 6, out _);
        var probe = new AllocationProbe();
        var sum = 0f;
        for (var i = 0; i < 50; i++)
            while (probe.Measuring())
            {
                RockInterior.Describe(World, rock with { Index = i % content.AsteroidCount }, content.Asteroids[i % content.AsteroidCount], out var interior);
                interior.Sample(new Vector3(i * 0.3f, -i * 0.1f, 1), out var sample);
                interior.Average(new Vector3(0, i * 0.2f, 0), interior.Radius * 0.3f, 6, out var volume);
                sum += sample.Grade + volume.MainShare;
            }

        Assert.Equal(0, probe.Bytes);
        Assert.True(sum >= 0);
    }

    [Fact]
    public void ScrapingDepthsCostLittle()
    {
        // A scrape samples many points a tick: a describe and a thousand samples must stay well under a millisecond
        // in a release build. This only reports; timing in a shared debug run is too noisy to assert on.
        var content = new SectorContent();
        World.Generate(new SectorCoord(8, 0, -8), content);
        var rock = new RockRef(new SectorCoord(8, 0, -8), 0);
        RockInterior.Describe(World, rock, content.Asteroids[0], out var interior);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var sum = 0f;
        for (var i = 0; i < 100_000; i++)
        {
            interior.Sample(new Vector3(i % 97, i % 31, i % 13) * (interior.Radius / 97f), out var sample);
            sum += sample.Grade;
        }

        output.WriteLine($"{watch.Elapsed.TotalMilliseconds * 10:F1} ns a sample ({interior.VeinCount} veins, {interior.PocketCount} pockets), sum {sum}");
        watch.Restart();
        for (var i = 0; i < 10_000; i++)
            RockInterior.Describe(World, rock with { Index = i % content.AsteroidCount }, content.Asteroids[i % content.AsteroidCount], out interior);
        output.WriteLine($"{watch.Elapsed.TotalMilliseconds * 100:F1} ns a describe");
    }

    [Fact]
    public void BadRulesAreRefused()
    {
        var text = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "interiors.json"));
        foreach (var (from, to) in new[] { ("\"depth\": 0.14", "\"depth\": 0.7"), ("\"count\": [3, 7]", "\"count\": [3, 9]"), ("\"segments\": 14", "\"segments\": 16"), ("\"maxLocalOre\": 0.92", "\"maxLocalOre\": 1.5") })
        {
            Assert.Contains(from, text);
            Assert.Throws<InvalidDataException>(() => RockInteriorRules.Parse(System.Text.Encoding.UTF8.GetBytes(text.Replace(from, to)), "interiors.json"));
        }
    }

    /// <summary>Means of the shares over a lattice of points inside the rock, and how many fell in each layer.</summary>
    private static (float Main, float Mix1, float Mix2, int[] Layers) Integrate(in RockInterior interior, int perAxis, Action<RockSample, RockInterior>? check = null)
    {
        double main = 0, mix1 = 0, mix2 = 0;
        var inside = 0;
        var layers = new int[5];
        var step = 2f / perAxis;
        for (var k = 0; k < perAxis; k++)
            for (var j = 0; j < perAxis; j++)
                for (var i = 0; i < perAxis; i++)
                {
                    var p = new Vector3(-1f + step * (i + 0.5f), -1f + step * (j + 0.5f), -1f + step * (k + 0.5f)) * interior.Radius;
                    interior.Sample(p, out var sample);
                    if (!sample.Inside)
                        continue;
                    check?.Invoke(sample, interior);
                    inside++;
                    layers[(int)sample.Layer]++;
                    if (sample.Unstable)
                        layers[4]++;
                    main += sample.MainShare;
                    mix1 += sample.Mix1Share;
                    mix2 += sample.Mix2Share;
                }

        return ((float)(main / inside), (float)(mix1 / inside), (float)(mix2 / inside), layers);
    }

    private static ulong Hash(Galaxy galaxy, List<(RockRef Rock, Asteroid Asteroid)> rocks)
    {
        var hash = 0xCBF29CE484222325;
        void Add(float value) => hash = (hash ^ BitConverter.SingleToUInt32Bits(value)) * 0x100000001B3;
        foreach (var (rock, asteroid) in rocks)
        {
            RockInterior.Describe(galaxy, rock, asteroid, out var interior);
            hash = (hash ^ (ulong)(interior.VeinCount * 16 + interior.PocketCount)) * 0x100000001B3;
            Add(interior.CrustVolume);
            Add(interior.MantleShares.X);
            Add(interior.VeinShares.Y);
            Add(interior.PocketShares.Z);
            for (var i = 0; i < 6; i++)
                for (var j = 0; j < 6; j++)
                    for (var k = 0; k < 6; k++)
                    {
                        interior.Sample(new Vector3(i - 2.5f, j - 2.5f, k - 2.5f) * (asteroid.Radius / 2.6f), out var sample);
                        hash = (hash ^ (ulong)((int)sample.Layer * 64 + (int)sample.Material * 8 + (int)sample.Pocket + (sample.Resource + 1) * 512)) * 0x100000001B3;
                        Add(sample.Grade);
                        Add(sample.Depth);
                    }
        }

        return hash;
    }

    private static Vector3 Fibonacci(int i, int n)
    {
        var y = 1f - 2f * (i + 0.5f) / n;
        var ring = MathF.Sqrt(1f - y * y);
        var angle = i * 2.39996323f;
        return new Vector3(MathF.Cos(angle) * ring, y, MathF.Sin(angle) * ring);
    }
}
