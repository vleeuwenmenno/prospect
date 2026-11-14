using System.Numerics;
using System.Text.Json.Nodes;
using Prospect.Game.World;
using Xunit;

namespace Prospect.Tests;

/// <summary>Placed populations retain density, outskirts and asymmetric concentrations.</summary>
public sealed class FieldDistributionTests
{
    /// <summary>Regression floors recorded with generation version 4, including the crowded all-five preset.</summary>
    [Fact]
    public void RepresentativeFieldsKeepUsefulPlacedCounts()
    {
        var content = GameContent.Load();
        foreach (var factor in new[] { 1, 5 })
        {
            var assignments = new[] { "asteroidAmount", "asteroidSize", "asteroidFieldSize", "asteroidFrequency", "asteroidSpacing", "asteroidOreShare", "asteroidOreGrade" }.Select(id => $"{id}={factor}").Concat(["sectorSize=40", "gasSizeMin=8", "gasSizeMax=11.2", "gasHugeSizeMin=20", "gasHugeSizeMax=40"]).ToArray();
            var galaxy = content.WithSettings(content.SettingCatalog.Build(null, assignments)).Galaxy.WithSeed(42);
            var sector = new SectorContent();
            int[] baseline = factor == 1 ? [2278, 507, 734, 1805, 8704] : [5400, 1678, 2715, 4108, 5077];
            var index = 0;
            foreach (var coord in new SectorCoord[] { default, new(-40, 0, -29), new(8, 0, -8), new(-14, 0, -2), new(-26, 0, -31) })
            {
                galaxy.Generate(coord, sector);
                var planned = galaxy.Summarize(coord).AsteroidCount;
                TestContext.Current.SendDiagnosticMessage($"factor={factor} sector={coord} planned={planned} placed={sector.AsteroidCount}");
                Assert.True(sector.AsteroidCount >= baseline[index++] * 0.8, $"Only {sector.AsteroidCount}/{planned} placed in {coord} at {factor}x");
            }
        }
    }
    /// <summary>The crowded regression retains its population without borrowing space from walls or other rocks.</summary>
    [Fact]
    public void DenseMaximumPopulationRespectsEveryAcceptedPair()
    {
        var content = GameContent.Load();
        var settings = content.SettingCatalog.Build(null,
            ["asteroidAmount=5", "asteroidSize=5", "asteroidFieldSize=5", "asteroidFrequency=5", "asteroidSpacing=5", "asteroidOreShare=5", "asteroidOreGrade=5"]);
        var galaxy = content.WithSettings(settings).Galaxy.WithSeed(42);
        var sector = new SectorContent();
        galaxy.Generate(new SectorCoord(-26, 0, -31), sector);
        Assert.True(sector.AsteroidCount >= 5077 * 0.8);
        for (var i = 0; i < sector.AsteroidCount; i++)
        {
            var rock = sector.Asteroids[i];
            var p = rock.LocalPosition;
            var wall = galaxy.SectorSize * 0.5f - rock.Radius;
            Assert.True(Math.Abs(p.X) <= wall && Math.Abs(p.Y) <= wall && Math.Abs(p.Z) <= wall);
            var clear = rock.Radius + galaxy.AsteroidFields!.ArrivalClearance;
            Assert.True(p.X * p.X + p.Y * p.Y + p.Z * p.Z >= clear * clear);
            for (var j = 0; j < i; j++)
            {
                var previous = sector.Asteroids[j];
                Assert.True(previous.Radius >= rock.Radius);
                var delta = p - previous.LocalPosition;
                var apart = rock.Radius * 22f + previous.Radius;
                Assert.True(delta.X * delta.X + delta.Y * delta.Y + delta.Z * delta.Z >= apart * apart);
            }
        }
    }

    /// <summary>A convex oval has no deep inward boundary dents; measure occupied outlines, not centroids.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    public void OccupiedSupportHasDeepGapsAtOrdinaryAndMaximumSize(int factor)
    {
        var content = GameContent.Load();

        // At five times the size a field runs on into the sectors around its own, and theirs into it. With no
        // other field anywhere the home field stands alone, 7,500 rocks at five times the amount, and its
        // outline is read from every sector it reaches.
        var assignments = new[] { "asteroidAmount", "asteroidSize", "asteroidFieldSize", "asteroidFrequency", "asteroidSpacing", "asteroidOreShare", "asteroidOreGrade" }
            .Select(id => $"{id}={(factor == 5 && id == "asteroidFrequency" ? 0 : factor)}").Concat(["asteroidCountMin=7500", "asteroidRadiusMin=1", "asteroidRadiusMax=1"]).ToArray();
        var world = content.WithSettings(content.SettingCatalog.Build(null, assignments));
        var nonconvex = 0;
        foreach (var seed in new ulong[] { 0x50524F5350454354, 7, 11, 42, 999 })
        {
            var sector = new SectorContent();
            var galaxy = world.Galaxy.WithSeed(seed);
            var edge = (float)galaxy.SectorSize;
            var points = new List<Vector3>();
            for (var x = -1; x <= 1; x++)
                for (var z = -1; z <= 1; z++)
                {
                    if (factor == 1 && (x | z) != 0)
                        continue;
                    galaxy.Generate(new SectorCoord(x, 0, z), sector);
                    points.AddRange(sector.Asteroids.Take(sector.AsteroidCount).Select(r => r.LocalPosition + new Vector3(x * edge, 0, z * edge)));
                }

            Assert.True(points.Count >= 7000, $"only {points.Count} rocks at {factor}x under seed {seed}");
            var minX = points.Min(p => p.X);
            var maxX = points.Max(p => p.X);
            var minZ = points.Min(p => p.Z);
            var maxZ = points.Max(p => p.Z);
            const int bins = 16;
            var top = Enumerable.Repeat(float.MinValue, bins).ToArray();
            var bottom = Enumerable.Repeat(float.MaxValue, bins).ToArray();
            foreach (var p in points)
            {
                var bin = Math.Min(bins - 1, (int)((p.X - minX) / (maxX - minX) * bins));
                top[bin] = Math.Max(top[bin], p.Z);
                bottom[bin] = Math.Min(bottom[bin], p.Z);
            }
            var dent = 0f;
            // Exclude the sparsely sampled tips. Chords of a convex upper boundary never pass above it.
            for (var left = 1; left < bins - 3; left++)
                for (var right = left + 2; right < bins - 1; right++)
                    for (var middle = left + 1; middle < right; middle++)
                    {
                        var share = (middle - left) / (float)(right - left);
                        dent = Math.Max(dent, top[left] + (top[right] - top[left]) * share - top[middle]);
                        dent = Math.Max(dent, bottom[middle] - (bottom[left] + (bottom[right] - bottom[left]) * share));
                    }
            var relativeDent = dent / (maxZ - minZ);
            TestContext.Current.SendDiagnosticMessage($"support factor={factor} seed={seed} count={points.Count} inwardGap={relativeDent:F4}");
            if (relativeDent > 0.1f) nonconvex++;
            var output = Environment.GetEnvironmentVariable("PROSPECT_FIELD_POSITIONS");
            if (!string.IsNullOrEmpty(output))
            {
                Directory.CreateDirectory(output);
                File.WriteAllLines(Path.Combine(output, $"field-{factor}-{seed}.csv"),
                    new[] { "x,y,z" }.Concat(points.Select(p => FormattableString.Invariant($"{p.X},{p.Y},{p.Z}"))));
            }
        }
        Assert.True(nonconvex >= 3, $"Only {nonconvex} of five fields have deep occupied-boundary gaps at {factor}x");
    }

    /// <summary>Sparse proposals reach the ribbon's ends instead of filling the holes around its bends.</summary>
    [Fact]
    public void OutskirtsPopulateTaperedTails()
    {
        var content = GameContent.Load();
        var json = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "content", "galaxy", "galaxy.json")))!;
        var tailCounts = new int[2];
        for (var sample = 0; sample < 2; sample++)
        {
            json["sectorModules"]![0]!["distribution"]!["outskirts"] = sample;
            var galaxy = Galaxy.Parse(System.Text.Encoding.UTF8.GetBytes(json.ToJsonString()), "test", 42)
                .Under(content.SettingCatalog, content.SettingCatalog.Build(null, ["asteroidCountMin=7500", "asteroidRadiusMin=1", "asteroidRadiusMax=1"]));
            var sector = new SectorContent();
            galaxy.Generate(default, sector);
            Assert.True(sector.AsteroidCount >= 7000);
            tailCounts[sample] = sector.Asteroids.Take(sector.AsteroidCount).Count(r => Math.Abs(r.LocalPosition.X) > 7200 * 0.8f);
        }
        Assert.True(tailCounts[0] > 100);
        Assert.True(tailCounts[1] > tailCounts[0] * 2);
    }

    /// <summary>Reusing admitted sector storage takes no managed memory for the new density sampling.</summary>
    [Fact]
    public void WarmGenerationAllocatesNothing()
    {
        var galaxy = GameContent.Load().Galaxy.WithSeed(42);
        var sector = new SectorContent();
        for (var i = 0; i < 3; i++) galaxy.Generate(default, sector);
        var allocation = new AllocationProbe();
        while (allocation.Measuring()) galaxy.Generate(default, sector);
        Assert.Equal(0, allocation.Bytes);
    }

    /// <summary>Two coincident field envelopes both populate while one shared spacing budget protects every pair.</summary>
    [Fact]
    public void CoincidentFieldsShareSpacingWithoutExcludingEitherRegion()
    {
        var json = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "content", "galaxy", "galaxy.json")))!;
        var module = json["sectorModules"]![0]!;
        module.AsObject().Remove("baseline");

        // Even fields: this is about spacing, not about the world's density noise.
        module.AsObject().Remove("density");
        module["fieldsPerSector"] = new JsonArray(2, 2);
        module["voidFieldChance"] = 1;
        module["emptySectorChance"] = 0;
        module["sparseFill"] = 1;
        module["fieldCenters"]!["reach"] = 0;
        module["arrivalClearance"] = 20;
        module["spacing"] = 4;
        module["kinds"] = JsonNode.Parse("""[{"weight":1,"asteroids":[200,200],"length":[200,200],"width":[200,200],"height":[200,200]}]"""u8);
        module["sizes"] = JsonNode.Parse("""[{"name":"Test","weight":1,"radius":[5,5]}]"""u8);
        var galaxy = Galaxy.Parse(System.Text.Encoding.UTF8.GetBytes(json.ToJsonString()), "test", 42);
        var sector = new SectorContent();
        galaxy.Generate(new SectorCoord(1, 0, 0), sector);
        Assert.Equal(2, sector.Summary.FieldCount);
        Assert.InRange(sector.AsteroidCount, 300, 400);
        for (var i = 0; i < sector.AsteroidCount; i++)
            for (var j = 0; j < i; j++)
                Assert.True(Vector3.Distance(sector.Asteroids[i].LocalPosition, sector.Asteroids[j].LocalPosition) >= 25f);
    }

    /// <summary>Invalid art parameters cannot create nonfinite or unbounded support.</summary>
    [Theory]
    [InlineData("bend", -0.1f)]
    [InlineData("bend", 1.1f)]
    [InlineData("outskirts", -0.1f)]
    [InlineData("outskirts", 1.1f)]
    public void InvalidDistributionIsRejected(string key, float value)
    {
        var json = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "content", "galaxy", "galaxy.json")))!;
        json["sectorModules"]![0]!["distribution"]![key] = value;
        Assert.Throws<InvalidDataException>(() => Galaxy.Parse(System.Text.Encoding.UTF8.GetBytes(json.ToJsonString()), "test"));
    }

    /// <summary>The world-space rules are bounded on load: a field's centre stays in its sector, the noise lattice fits its buffers.</summary>
    [Theory]
    [InlineData("distribution", "fade", 1.1f)]
    [InlineData("distribution", "jitter", -1f)]
    [InlineData("fieldCenters", "reach", 1.1f)]
    [InlineData("fieldCenters", "flatten", 1.1f)]
    [InlineData("density", "cells", 1f)]
    [InlineData("density", "cells", 33f)]
    [InlineData("density", "broadCells", 9f)]
    [InlineData("density", "from", 0.7f)]
    [InlineData("density", "sparse", 1.5f)]
    [InlineData("inGas", "chance", 1.5f)]
    [InlineData("inGas", "fill", 0f)]
    [InlineData("inGas", "max", 0f)]
    public void InvalidWorldSpaceRulesAreRejected(string block, string key, float value)
    {
        var json = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "content", "galaxy", "galaxy.json")))!;
        Galaxy.Parse(System.Text.Encoding.UTF8.GetBytes(json.ToJsonString()), "test");
        json["sectorModules"]![0]![block]![key] = value;
        Assert.Throws<InvalidDataException>(() => Galaxy.Parse(System.Text.Encoding.UTF8.GetBytes(json.ToJsonString()), "test"));
    }

}
