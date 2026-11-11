using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Prospect.Game.World;
using Xunit;

namespace Prospect.Tests;

/// <summary>Helpers for tests that need a particular kind of sector or asteroid out of the generated galaxy.</summary>
internal static class Sectors
{
    /// <summary>The sector with the most asteroids in a square of the home layer, by its summary alone.</summary>
    public static SectorCoord Densest(Galaxy galaxy, int reach = 40)
    {
        var best = default(SectorCoord);
        var most = -1;
        for (var x = -reach; x <= reach; x++)
            for (var z = -reach; z <= reach; z++)
            {
                var count = galaxy.Summarize(new SectorCoord(x, 0, z)).AsteroidCount;
                if (count > most)
                {
                    most = count;
                    best = new SectorCoord(x, 0, z);
                }
            }

        return best;
    }

    /// <summary>The bytes of a generated sector's asteroids: what the server and a client must agree on.</summary>
    public static ReadOnlySpan<byte> Bytes(SectorContent content) => MemoryMarshal.AsBytes(content.Asteroids.AsSpan(0, content.AsteroidCount));

    public static ulong Hash(SectorContent content, ulong hash = 0xCBF29CE484222325)
    {
        foreach (var b in Bytes(content))
            hash = (hash ^ b) * 0x100000001B3;
        return hash;
    }
}

public sealed class GalaxyTests
{
    private static readonly GameContent Content = GameContent.Load();

    private static AsteroidFieldsModule Fields(Galaxy galaxy) => galaxy.AsteroidFields!;

    [Fact]
    public void TheSameSeedGivesByteIdenticalSectorsOnEveryRunAndFromEveryThread()
    {
        var a = Content.Galaxy.WithSeed(42);
        var b = Content.Galaxy.WithSeed(42);
        var other = Content.Galaxy.WithSeed(43);
        var coords = new List<SectorCoord> { default, Sectors.Densest(a) };
        for (var x = -6; x <= 6; x++)
            coords.Add(new SectorCoord(x * 3, x % 2, -x * 2));

        // Twice in a row on this thread, into the same buffers and into fresh ones.
        var reused = new SectorContent();
        var hashes = new ulong[coords.Count];
        var differing = 0;
        var rocks = 0;
        for (var i = 0; i < coords.Count; i++)
        {
            var fresh = new SectorContent();
            a.Generate(coords[i], reused);
            b.Generate(coords[i], fresh);
            Assert.Equal(a.NameOf(coords[i]), b.NameOf(coords[i]));
            Assert.Equal(reused.AsteroidCount, fresh.AsteroidCount);
            Assert.True(Sectors.Bytes(reused).SequenceEqual(Sectors.Bytes(fresh)), $"sector {coords[i]} differs between two runs");
            hashes[i] = Sectors.Hash(reused);
            rocks += reused.AsteroidCount;

            if (other.NameOf(coords[i]) != a.NameOf(coords[i]))
                differing++;
        }

        Assert.True(rocks > 3000, "the sample should hold some real fields");
        Assert.True(differing >= coords.Count - 2, "another seed is another galaxy");

        // And from eight threads at once, each with its own buffers.
        var fromThreads = new ulong[8][];
        Parallel.For(
            0,
            fromThreads.Length,
            new ParallelOptions { MaxDegreeOfParallelism = fromThreads.Length },
            t =>
            {
                var content = new SectorContent();
                var mine = new ulong[coords.Count];
                for (var pass = 0; pass < 3; pass++)
                    for (var i = 0; i < coords.Count; i++)
                    {
                        // Each thread walks the sectors from a different start, so the buffers are reused differently.
                        var index = (i + t * 3) % coords.Count;
                        a.Generate(coords[index], content);
                        mine[index] = Sectors.Hash(content);
                    }

                fromThreads[t] = mine;
            }
        );
        foreach (var mine in fromThreads)
            Assert.Equal(hashes, mine);
    }

    [Fact]
    public void GenerationMatchesTheRecordedGalaxyOnEveryMachine()
    {
        // A frozen copy of the galaxy rules and a hash recorded on Linux x64. The same number must come out on
        // Windows and on Apple Silicon: the server and a client on different machines build their asteroids
        // separately and must name the same ones. If generation is changed on purpose, bump ProtocolInfo.Version
        // and record the new hash here; if this fails without such a change, something in generation depends on
        // the machine.
        var galaxy = Galaxy.Load(Path.Combine(AppContext.BaseDirectory, "fixtures", "galaxy.json"), 0x50524F5350454354);
        var content = new SectorContent();
        var hash = 0xCBF29CE484222325;
        var rocks = 0;
        SectorCoord[] coords = [default, new(1, 0, 0), new(-7, 1, 12), new(23, 0, -23), new(-40, 0, -29), new(-150, 0, 100), new(-120, 2, 60), new(310, -1, -45)];
        foreach (var coord in coords)
        {
            galaxy.Generate(coord, content);
            var summary = content.Summary;
            hash = Sectors.Hash(content, hash);
            hash = (hash ^ (ulong)summary.AsteroidCount) * 0x100000001B3;
            hash = (hash ^ summary.Character.Resources) * 0x100000001B3;
            hash = (hash ^ BitConverter.SingleToUInt32Bits(summary.Character.Richness)) * 0x100000001B3;
            hash = (hash ^ BitConverter.SingleToUInt32Bits(summary.Density)) * 0x100000001B3;
            rocks += content.AsteroidCount;
        }

        Assert.True(rocks > 2000, $"only {rocks} asteroids in the sample");
        Assert.Equal(RecordedHash, hash);
    }

    private const ulong RecordedHash = 6024413946682158343;

    /// <summary>A second module, for the tests only: it draws from its own stream and remembers what came out.</summary>
    private sealed class ProbeModule(string id, int draws) : SectorModule(id)
    {
        public ulong Last { get; private set; }

        public override void Summarize(in SectorContext sector, ref SectorSummary summary) { }

        public override void Generate(in SectorContext sector, SectorContent content)
        {
            var random = Stream(sector, 1);
            for (var i = 0; i < draws; i++)
                Last = random.NextUInt64();
        }
    }

    /// <summary>The galaxy rules with the asteroid module changed, loaded as the game loads them.</summary>
    private static Galaxy Retuned(Action<JsonNode> change, ulong seed)
    {
        var json = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "content", "galaxy", "galaxy.json")))!;
        change(json["sectorModules"]![0]!);
        var path = Path.Combine(Path.GetTempPath(), $"prospect-galaxy-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, json.ToJsonString());
        try
        {
            return Galaxy.Load(path, seed);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AModuleNeverChangesWhatAnotherModuleProduces()
    {
        var plain = Content.Galaxy.WithSeed(5);
        var asteroids = plain.Modules[0];

        // The probe runs before the asteroids here, and draws a different amount in the third galaxy.
        var probe = new ProbeModule("probe", 7);
        var greedy = new ProbeModule("probe", 4000);
        var withProbe = plain.WithModules(probe, asteroids);
        var withGreedy = plain.WithModules(asteroids, greedy);

        // The same galaxy with the asteroid module retuned: other sizes, other spacing, more fields.
        var retuned = Retuned(
            module =>
            {
                module["sizes"]![0]!["weight"] = 5;
                module["spacing"] = 2.0;
                module["fieldsPerSector"] = new JsonArray(2, 5);
            },
            5
        );
        var retunedProbe = new ProbeModule("probe", 7);
        var retunedWithProbe = retuned.WithModules(retuned.Modules[0], retunedProbe);

        // And with the module only renamed: its id keys its streams, so it must come out different.
        var renamed = Retuned(module => module["id"] = "asteroid-fields-renamed", 5);

        var a = new SectorContent();
        var b = new SectorContent();
        var sectors = 0;
        var changedByRetuning = 0;
        var changedByRenaming = 0;
        for (var x = -12; x <= 12; x++)
            for (var z = -12; z <= 12; z++)
            {
                var coord = new SectorCoord(x, 0, z);
                plain.Generate(coord, a);
                if (a.AsteroidCount == 0)
                    continue;
                sectors++;

                // Adding a module, before or after, leaves the asteroids exactly as they were.
                withProbe.Generate(coord, b);
                Assert.True(Sectors.Bytes(a).SequenceEqual(Sectors.Bytes(b)), $"a module ahead of the asteroids changed sector {coord}");
                var drawn = probe.Last;
                withGreedy.Generate(coord, b);
                Assert.True(Sectors.Bytes(a).SequenceEqual(Sectors.Bytes(b)), $"a module after the asteroids changed sector {coord}");

                // Retuning the asteroids changes them and leaves the probe's stream exactly as it was.
                retunedWithProbe.Generate(coord, b);
                Assert.Equal(drawn, retunedProbe.Last);
                if (!Sectors.Bytes(a).SequenceEqual(Sectors.Bytes(b)))
                    changedByRetuning++;

                renamed.Generate(coord, b);
                if (!Sectors.Bytes(a).SequenceEqual(Sectors.Bytes(b)))
                    changedByRenaming++;
            }

        Assert.True(sectors > 100);
        Assert.True(changedByRetuning > sectors * 0.9, "retuning the asteroid module should have changed its sectors");
        Assert.True(changedByRenaming > sectors * 0.9, "a module's id keys its random streams");
        Assert.Throws<InvalidDataException>(() => plain.WithModules(probe, new ProbeModule("probe", 1)));
    }

    [Fact]
    public void SectorsDifferInCharacterAndNeighboursAreRelated()
    {
        var galaxy = Content.Galaxy.WithSeed(7);
        const int reach = 50;
        var richness = new float[reach * 2, reach * 2];
        var occupied = new bool[reach * 2, reach * 2];
        var levels = new int[galaxy.RichnessLevels.Length];
        var absent = new int[galaxy.Resources.Length];
        var withNone = 0;
        var withAll = 0;
        var withAFew = 0;
        var sectors = 0;
        for (var x = -reach; x < reach; x++)
            for (var z = -reach; z < reach; z++)
            {
                var summary = galaxy.Summarize(new SectorCoord(x, 0, z));
                if (summary.AsteroidCount == 0)
                    continue;
                var character = summary.Character;
                sectors++;
                occupied[x + reach, z + reach] = true;
                richness[x + reach, z + reach] = character.Richness;
                levels[character.Level]++;
                Assert.Equal(galaxy.RichnessLevelOf(character.Richness), character.Level);

                // Barren and without resources are the same thing: a sector has ore of some kind or it has none.
                Assert.Equal(character.Level == 0, character.Resources == 0);
                Assert.Equal(character.Resources == 0, character.Main < 0);
                var count = character.ResourceCount;
                withNone += count == 0 ? 1 : 0;
                withAll += count == galaxy.Resources.Length ? 1 : 0;
                withAFew += count > 0 && count < galaxy.Resources.Length ? 1 : 0;

                var total = 0f;
                for (var i = 0; i < galaxy.Resources.Length; i++)
                {
                    if (!character.Has(i))
                        absent[i]++;
                    total += character.Shares[i];
                    Assert.Equal(character.Has(i), character.Shares[i] > 0);
                }

                if (count > 0)
                {
                    Assert.Equal(1f, total, 3);
                    Assert.True(character.Shares[character.Main] >= 1f / count - 1e-4f);
                }
            }

        // A few thousand sectors with asteroids: some with nothing in them, some with everything, most with a few.
        Assert.True(sectors > 2000, $"only {sectors} sectors with asteroids");
        Assert.InRange(withNone / (float)sectors, 0.05f, 0.45f);
        Assert.InRange(withAll / (float)sectors, 0.01f, 0.2f);
        Assert.True(withAFew > sectors / 2);
        foreach (var level in levels)
            Assert.True(level > sectors / 50, "every richness level should occur");

        // Every resource is simply not there in a good share of sectors, the common ones included, and is there in others.
        for (var i = 0; i < absent.Length; i++)
            Assert.InRange(absent[i] / (float)sectors, 0.2f, 0.9f);

        // Rich space and poor space come in regions: a sector's richness says a lot about its neighbour's and
        // nothing about a sector far away.
        var near = Correlation(richness, occupied, 1);
        var far = Correlation(richness, occupied, 37);
        Assert.True(near > 0.45f, $"neighbouring sectors correlate only {near:0.00}");
        Assert.True(MathF.Abs(far) < 0.15f, $"distant sectors correlate {far:0.00}");
    }

    /// <summary>Pearson correlation of a value between sectors a fixed step apart, over the pairs that both have asteroids.</summary>
    private static float Correlation(float[,] value, bool[,] present, int step)
    {
        double sumA = 0;
        double sumB = 0;
        double sumAA = 0;
        double sumBB = 0;
        double sumAB = 0;
        var n = 0;
        for (var x = 0; x + step < value.GetLength(0); x++)
            for (var z = 0; z < value.GetLength(1); z++)
            {
                if (!present[x, z] || !present[x + step, z])
                    continue;
                var a = value[x, z];
                var b = value[x + step, z];
                sumA += a;
                sumB += b;
                sumAA += a * a;
                sumBB += b * b;
                sumAB += a * b;
                n++;
            }

        var covariance = sumAB / n - sumA / n * (sumB / n);
        return (float)(covariance / Math.Sqrt((sumAA / n - sumA / n * (sumA / n)) * (sumBB / n - sumB / n * (sumB / n))));
    }

    [Fact]
    public void TheSummaryTellsWhatGenerationMakes()
    {
        var galaxy = Content.Galaxy.WithSeed(9);
        var content = new SectorContent();
        var withOre = 0;
        var barrenWithRock = 0;
        for (var x = -10; x <= 10; x++)
            for (var z = -10; z <= 10; z++)
            {
                var coord = new SectorCoord(x, 0, z);
                var summary = galaxy.Summarize(coord);
                galaxy.Generate(coord, content);

                // The cheap step promises exactly what the full one delivers.
                Assert.Equal(summary.AsteroidCount, content.AsteroidCount);
                Assert.Equal(summary.Character.Resources, content.Summary.Character.Resources);
                Assert.Equal(summary.Character.Richness, content.Summary.Character.Richness);
                Assert.Equal(summary.FieldCount > 0, summary.AsteroidCount > 0);
                Assert.InRange(summary.FieldCount, 0, AsteroidFieldsModule.MaxFields);

                // Ore comes only from the resources the sector has; a barren sector's rocks are all bare.
                var ore = 0;
                for (var i = 0; i < content.AsteroidCount; i++)
                {
                    var rock = content.Asteroids[i];
                    if (rock.Resource < 0)
                        continue;
                    ore++;
                    Assert.True(summary.Character.Has(rock.Resource), $"sector {coord} has a rock of a resource it does not have");
                    Assert.InRange(rock.Grade, 0.05f, 1f);
                }

                if (summary.Character.Level == 0)
                {
                    Assert.Equal(0, ore);
                    barrenWithRock += content.AsteroidCount > 0 ? 1 : 0;
                }
                else if (content.AsteroidCount >= 40)
                {
                    Assert.True(ore > 0, $"sector {coord} is {galaxy.RichnessLevels[summary.Character.Level].Name} and has no ore");
                    withOre++;
                }
            }

        Assert.True(withOre > 20);
        Assert.True(barrenWithRock > 3);
    }

    [Fact]
    public void RicherSectorsCarryMoreOreInMoreOfTheirRocks()
    {
        var galaxy = Content.Galaxy.WithSeed(9);
        var content = new SectorContent();
        var top = galaxy.RichnessLevels.Length - 1;
        double poorShare = 0;
        double richShare = 0;
        double poorGrade = 0;
        double richGrade = 0;
        var poor = 0;
        var rich = 0;
        for (var x = -25; x <= 25; x++)
            for (var z = -25; z <= 25; z++)
            {
                var coord = new SectorCoord(x, 0, z);
                var summary = galaxy.Summarize(coord);
                if (summary.AsteroidCount < 60 || summary.Character.Level is not (1 or 4) || top != 4)
                    continue;
                galaxy.Generate(coord, content);
                var ore = 0;
                var grade = 0f;
                for (var i = 0; i < content.AsteroidCount; i++)
                {
                    if (content.Asteroids[i].Resource < 0)
                        continue;
                    ore++;
                    grade += content.Asteroids[i].Grade;
                }

                if (summary.Character.Level == 1)
                {
                    poor++;
                    poorShare += ore / (double)content.AsteroidCount;
                    poorGrade += grade / Math.Max(ore, 1);
                }
                else
                {
                    rich++;
                    richShare += ore / (double)content.AsteroidCount;
                    richGrade += grade / Math.Max(ore, 1);
                }
            }

        Assert.True(poor > 10 && rich > 10, $"{poor} poor and {rich} very rich sectors");
        Assert.True(richShare / rich > 2 * poorShare / poor, "a very rich sector should have ore in far more of its rocks than a poor one");
        Assert.True(richGrade / rich > poorGrade / poor + 0.15, "and of a better grade");
    }

    [Fact]
    public void FieldsRunFromEmptyThroughSparseToDenseBelts()
    {
        var galaxy = Content.Galaxy.WithSeed(7);
        var empty = 0;
        var sparse = 0;
        var middling = 0;
        var dense = 0;
        var sectors = 0;
        for (var x = -50; x < 50; x++)
            for (var z = -50; z < 50; z++)
            {
                var count = galaxy.Summarize(new SectorCoord(x, 0, z)).AsteroidCount;
                Assert.InRange(count, 0, galaxy.MaxAsteroidsPerSector);
                sectors++;
                if (count == 0)
                    empty++;
                else if (count < 100)
                    sparse++;
                else if (count < 1000)
                    middling++;
                else
                    dense++;
            }

        var occupied = sectors - empty;
        Assert.True(empty > sectors / 5, "space should be mostly empty somewhere");
        Assert.True(sparse > occupied / 5, $"{sparse} sparse sectors of {occupied}");
        Assert.True(middling > occupied / 5, $"{middling} middling sectors of {occupied}");
        Assert.InRange(dense / (float)occupied, 0.03f, 0.3f);

        // The home sector always has a field to work, with ore in it, and it is a dense one by the old measure.
        var home = galaxy.Summarize(default);
        Assert.True(home.AsteroidCount > 300);
        Assert.True(home.Character.Level >= 2);
        Assert.True(home.Character.Has(0));

        // Far outside the disc there is next to nothing.
        var outside = 0;
        for (var x = 0; x < 40; x++)
            for (var z = 0; z < 40; z++)
                outside += galaxy.Summarize(new SectorCoord(900 + x, 0, 900 + z)).AsteroidCount > 0 ? 1 : 0;
        Assert.True(outside < 1600 / 10);
    }

    [Fact]
    public void AsteroidsAreMostlyTinyWithAFewHugeAndNoneInTheWay()
    {
        var galaxy = Content.Galaxy.WithSeed(7);
        var fields = Fields(galaxy);
        var content = new SectorContent();
        var half = (float)(galaxy.SectorSize / 2);
        var radii = new List<float>();
        var overlapping = 0;
        var largest = 0f;
        var sectorsWithHuge = 0;
        var sectors = 0;

        for (var x = -16; x <= 16; x++)
            for (var z = -16; z <= 16; z++)
            {
                galaxy.Generate(new SectorCoord(x, 0, z), content);
                if (content.AsteroidCount == 0)
                    continue;
                sectors++;
                Assert.InRange(content.AsteroidCount, 1, fields.MaxPerSector);
                sectorsWithHuge += content.Asteroids[0].Radius >= 500f ? 1 : 0;
                for (var i = 0; i < content.AsteroidCount; i++)
                {
                    var rock = content.Asteroids[i];
                    radii.Add(rock.Radius);
                    largest = Math.Max(largest, rock.Radius);

                    // Wholly inside its sector, and clear of where ships arrive: the middle of every sector.
                    Assert.True(MathF.Abs(rock.LocalPosition.X) + rock.Radius <= half + 0.01f);
                    Assert.True(MathF.Abs(rock.LocalPosition.Y) + rock.Radius <= half + 0.01f);
                    Assert.True(MathF.Abs(rock.LocalPosition.Z) + rock.Radius <= half + 0.01f);
                    Assert.True(rock.LocalPosition.Length() >= rock.Radius + fields.ArrivalClearance - 0.5f, $"a {rock.Radius:0} m rock sits {rock.LocalPosition.Length():0} m from the arrival point");
                    Assert.InRange(rock.Shape, 0, Galaxy.AsteroidShapes - 1);
                    Assert.InRange(rock.Radius, fields.SmallestRadius, fields.LargestRadius);
                    Assert.Equal(1f, rock.Rotation.Length(), 4);

                    // Largest first: that order is what lets a far sector be drawn by its few large rocks.
                    if (i > 0)
                        Assert.True(rock.Radius <= content.Asteroids[i - 1].Radius);

                    // Inside the sphere the sector says holds them all.
                    Assert.True((rock.LocalPosition - content.BoundsCenter).Length() + rock.Radius <= content.BoundsRadius + 0.5f);

                    // Not inside a rock placed before it.
                    var near = content.Rocks.Near(rock.LocalPosition, rock.Radius);
                    while (near.Next(out var other))
                    {
                        if (other < i && (content.Asteroids[other].LocalPosition - rock.LocalPosition).Length() < content.Asteroids[other].Radius + rock.Radius)
                        {
                            overlapping++;
                            break;
                        }
                    }
                }
            }

        radii.Sort();
        var count = radii.Count;
        float Share(float from, float to) => radii.Count(r => r >= from && r < to) / (float)count;
        Assert.True(count > 50000, $"only {count} asteroids in the sample");

        // Many tiny, fewer medium, a small chance of huge: each step up in size is rarer than the one before.
        Assert.True(radii[count / 2] < 15f, $"the median asteroid is {radii[count / 2]:0.0} m in radius");
        Assert.True(Share(0, 12) > 0.4f);
        Assert.True(Share(12, 45) < Share(0, 12));
        Assert.True(Share(45, 160) < Share(12, 45));
        Assert.True(Share(160, 500) < Share(45, 160) / 2);
        Assert.InRange(Share(500, float.MaxValue), 0.0002f, 0.005f);

        // Far beyond the 260 m that used to be the largest, and still a rarity by sector.
        Assert.True(largest > 1500f, $"the largest asteroid in the sample is {largest:0} m in radius");
        Assert.InRange(sectorsWithHuge / (float)sectors, 0.05f, 0.5f);

        // A few tries per rock keep nearly all of them out of each other.
        Assert.True(overlapping < count / 200, $"{overlapping} of {count} asteroids overlap one placed before");
    }

    [Fact]
    public void TheHomeSectorKeepsItsArrivalPointClearUnderEverySeed()
    {
        var content = new SectorContent();
        for (var seed = 1UL; seed <= 40; seed++)
        {
            var galaxy = Content.Galaxy.WithSeed(seed * 0x9E3779B97F4A7C15);
            var clearance = Fields(galaxy).ArrivalClearance;
            galaxy.Generate(default, content);
            Assert.True(content.AsteroidCount > 300);
            var nearest = float.MaxValue;
            for (var i = 0; i < content.AsteroidCount; i++)
                nearest = Math.Min(nearest, content.Asteroids[i].LocalPosition.Length() - content.Asteroids[i].Radius);

            // Clear, and still close enough to fly to: the field is beside the arrival point, not far away.
            Assert.InRange(nearest, clearance - 0.5f, clearance + 400f);
        }
    }

    [Fact]
    public void TheIndexFindsEveryAsteroidNearAPoint()
    {
        var galaxy = Content.Galaxy.WithSeed(7);
        var content = new SectorContent();
        galaxy.Generate(Sectors.Densest(galaxy), content);
        Assert.True(content.AsteroidCount > 2000);

        var random = new Random(1);
        var found = new HashSet<int>();
        var checkedRocks = 0;
        var returned = 0L;
        const int probes = 400;
        for (var probe = 0; probe < probes; probe++)
        {
            // Around a random asteroid, at every scale from a block to a station, and now and then out in the void.
            var anchor = content.Asteroids[random.Next(content.AsteroidCount)];
            var radius = MathF.Pow(10f, 0.5f + random.NextSingle() * 2.5f);
            var away = probe % 10 == 0 ? 15000f : anchor.Radius + radius;
            var point = anchor.LocalPosition + new System.Numerics.Vector3(random.NextSingle() - 0.5f, random.NextSingle() - 0.5f, random.NextSingle() - 0.5f) * 2f * away;

            found.Clear();
            var near = content.Rocks.Near(point, radius);
            while (near.Next(out var rock))
            {
                Assert.InRange(rock, 0, content.AsteroidCount - 1);
                Assert.True(found.Add(rock), "an asteroid was returned twice");
            }

            returned += found.Count;
            for (var i = 0; i < content.AsteroidCount; i++)
            {
                var reach = content.Asteroids[i].Radius + radius;
                if ((content.Asteroids[i].LocalPosition - point).LengthSquared() >= reach * reach)
                    continue;
                checkedRocks++;
                Assert.True(found.Contains(i), $"the index missed a {content.Asteroids[i].Radius:0} m asteroid near a {radius:0} m probe");
            }
        }

        // Rocks sit about twice as far apart as they once did, so a probe meets fewer: 903 in this sample.
        Assert.True(checkedRocks > 500, $"checked {checkedRocks}");

        // It is an index, not a list: a probe is handed a small part of the sector.
        Assert.True(returned / probes < content.AsteroidCount / 8, $"{returned / probes} of {content.AsteroidCount} asteroids per probe");
    }

    [Fact]
    public void GeneratingTheDensestSectorStaysWithinBudget()
    {
        // The budget: the fullest sector a galaxy can hold is generated in under 15 ms in a debug build, a few
        // milliseconds in release. The client does this on a streaming job; the server does it on the tick that
        // first touches a sector, once, and a tick is 16.7 ms long.
        const double budgetMilliseconds = 15;
        var galaxy = Content.Galaxy.WithSeed(11);
        var dense = Sectors.Densest(galaxy);
        var content = new SectorContent();
        var best = double.MaxValue;
        for (var i = 0; i < 20; i++)
        {
            var start = System.Diagnostics.Stopwatch.GetTimestamp();
            galaxy.Generate(dense, content);
            best = Math.Min(best, System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        }

        Assert.True(content.AsteroidCount > 2500);
        TestContext.Current.SendDiagnosticMessage($"generating a sector of {content.AsteroidCount}: {best:0.000} ms");
        Assert.True(best < budgetMilliseconds, $"generating {content.AsteroidCount} asteroids took {best:0.00} ms; the budget is {budgetMilliseconds} ms");

        // A summary costs next to nothing, so the map can ask for thousands.
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var rocks = 0L;
        for (var x = 0; x < 100; x++)
            for (var z = 0; z < 100; z++)
                rocks += galaxy.Summarize(new SectorCoord(x - 50, 0, z - 50)).AsteroidCount;
        var each = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMicroseconds / 10000;
        TestContext.Current.SendDiagnosticMessage($"summarising a sector: {each:0.00} us");
        Assert.True(rocks > 0);
        Assert.True(each < 50, $"a sector summary took {each:0.0} us");
    }

    [Fact]
    public void TheRockFieldDropsSectorsItHasNotUsedAndMakesThemAgain()
    {
        var galaxy = Content.Galaxy.WithSeed(7);
        var rocks = new RockField(galaxy);
        var fresh = new SectorContent();
        var dense = Sectors.Densest(galaxy);
        var before = Sectors.Hash(rocks.Sector(dense));
        Assert.Same(rocks.Sector(dense), rocks.Sector(dense));

        // Far more sectors than the field keeps, coming back to the dense one now and then.
        for (var i = 0; i < 600; i++)
        {
            var coord = new SectorCoord(i % 25 - 12, 0, i / 25 - 12);
            var content = rocks.Sector(coord);
            Assert.Equal(coord, content.Summary.Coord);
            if (i % 97 == 0)
            {
                galaxy.Generate(coord, fresh);
                Assert.True(Sectors.Bytes(fresh).SequenceEqual(Sectors.Bytes(content)), $"sector {coord} came back different");
                Assert.Equal(before, Sectors.Hash(rocks.Sector(dense)));
            }
        }

        // What has been pushed is kept apart from what is generated, and outlives any amount of forgetting.
        rocks.Set(new RockDelta { Rock = new RockRef(dense, 3), Offset = new System.Numerics.Vector3(5, 0, 0) });
        for (var i = 0; i < 300; i++)
            rocks.Sector(new SectorCoord(100 + i, 0, 0));
        rocks.Displacement(new RockRef(dense, 3), 10, out var offset, out _);
        Assert.Equal(new System.Numerics.Vector3(5, 0, 0), offset);
        Assert.Equal(1, rocks.MovedIn(dense)!.Count);
        Assert.Equal(before, Sectors.Hash(rocks.Sector(dense)));
    }

    [Fact]
    public void AnAsteroidIsNamedForItsSize()
    {
        var fields = Fields(Content.Galaxy);
        Assert.Equal("Pebble", fields.SizeName(fields.SmallestRadius));
        Assert.Equal("Giant", fields.SizeName(fields.LargestRadius));
        Assert.Equal("Medium", fields.SizeName(100f));
        Assert.True(fields.LargestRadius >= 2500f);
        Assert.True(fields.MaxPerSector >= 4096);
    }

    [Fact]
    public void DeterministicMathAgreesWithTheSystemsOwn()
    {
        for (var i = -400; i <= 400; i++)
        {
            var x = i * 0.0371;
            Assert.Equal(Math.Exp(x), DetMath.Exp(x), 1e-9 * Math.Max(1.0, Math.Exp(x)));
            Assert.Equal(Math.Sin(x), DetMath.Sin(x), 1e-9);
            Assert.Equal(Math.Cos(x), DetMath.Cos((float)x), 1e-6);
            if (x > 0)
                Assert.Equal(Math.Log(x), DetMath.Log(x), 1e-9);
            for (var j = -5; j <= 5; j++)
            {
                var y = j * 0.83;
                if (x != 0 || y != 0)
                    Assert.Equal(Math.Atan2(y, x), DetMath.Atan2(y, x), 1e-9);
            }
        }

        Assert.Equal(0.0, DetMath.Exp(-800.0));
        Assert.Equal(0.0, DetMath.Atan2(0.0, 0.0));
    }

    [Fact]
    public void PositionsMapToSectorsWithoutGapsAtTheBoundaries()
    {
        var galaxy = Content.Galaxy;
        var size = galaxy.SectorSize;
        Assert.Equal(new SectorCoord(0, 0, 0), galaxy.SectorOf(new Double3(size * 0.49, -size * 0.49, 0)));
        Assert.Equal(new SectorCoord(1, -1, 0), galaxy.SectorOf(new Double3(size * 0.51, -size * 0.51, 0)));
        Assert.Equal(new SectorCoord(-3, 0, 5), galaxy.SectorOf(galaxy.CenterOf(new SectorCoord(-3, 0, 5))));
    }
}
