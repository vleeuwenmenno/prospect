using System.Numerics;
using System.Text.Json.Nodes;
using Prospect.Game.World;
using Xunit;

namespace Prospect.Tests;

/// <summary>
/// Fields and clouds are things in world space: they lie across sector walls without a seam, and every sector
/// is still generated alone.
/// </summary>
public sealed class WorldSpaceGenerationTests
{
    private static readonly GameContent Content = GameContent.Load();
    private const ulong DefaultSeed = 0x50524F5350454354;

    [Fact]
    public void FieldsRunThroughSectorWallsWithoutASeam()
    {
        var galaxy = Content.Galaxy.WithSeed(42);
        var half = (float)(galaxy.SectorSize / 2);
        const int reach = 12;
        const int side = reach * 2 + 1;
        const int bins = 4;
        const float slab = 2000f;

        // For every sector, the small rocks within two kilometres of each of its four upright walls, in
        // stretches of ten kilometres along the wall. Small, so that a rock's own size keeps none from a wall.
        var low = new int[side, side, 2, bins];
        var high = new int[side, side, 2, bins];
        var content = new SectorContent();
        for (var x = -reach; x <= reach; x++)
            for (var z = -reach; z <= reach; z++)
            {
                galaxy.Generate(new SectorCoord(x, 0, z), content);
                for (var i = 0; i < content.AsteroidCount; i++)
                {
                    var rock = content.Asteroids[i];
                    if (rock.Radius > 60f)
                        continue;
                    var p = rock.LocalPosition;
                    var alongZ = Math.Clamp((int)((p.Z + half) / (half * 2 / bins)), 0, bins - 1);
                    var alongX = Math.Clamp((int)((p.X + half) / (half * 2 / bins)), 0, bins - 1);
                    if (p.X < -half + slab) low[x + reach, z + reach, 0, alongZ]++;
                    if (p.X >= half - slab) high[x + reach, z + reach, 0, alongZ]++;
                    if (p.Z < -half + slab) low[x + reach, z + reach, 1, alongX]++;
                    if (p.Z >= half - slab) high[x + reach, z + reach, 1, alongX]++;
                }
            }

        // Each wall has a sector on either side, generated apart. What lies against it on one side says what
        // lies against it on the other: the same field, at the same density.
        double sumA = 0, sumB = 0, sumAA = 0, sumBB = 0, sumAB = 0;
        var pairs = 0;
        for (var x = 0; x < side; x++)
            for (var z = 0; z < side; z++)
                for (var axis = 0; axis < 2; axis++)
                {
                    var nextX = x + (axis == 0 ? 1 : 0);
                    var nextZ = z + (axis == 1 ? 1 : 0);
                    if (nextX >= side || nextZ >= side)
                        continue;
                    for (var bin = 0; bin < bins; bin++)
                    {
                        double a = high[x, z, axis, bin];
                        double b = low[nextX, nextZ, axis, bin];
                        sumA += a;
                        sumB += b;
                        sumAA += a * a;
                        sumBB += b * b;
                        sumAB += a * b;
                        pairs++;
                    }
                }

        var covariance = sumAB / pairs - sumA / pairs * (sumB / pairs);
        var correlation = covariance / Math.Sqrt((sumAA / pairs - sumA / pairs * (sumA / pairs)) * (sumBB / pairs - sumB / pairs * (sumB / pairs)));
        TestContext.Current.SendDiagnosticMessage($"against the walls: {sumA:0} on one side, {sumB:0} on the other, correlation {correlation:0.000}");
        Assert.True(sumA > 10000 && sumB > 10000, "the sample should hold real fields against its walls");
        Assert.InRange(sumA / sumB, 0.9, 1.1);
        Assert.True(correlation > 0.6, $"the two sides of a wall correlate only {correlation:0.000}");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SpaceAgainstASectorWallIsAsFullAsSpaceInItsMiddle(bool thinned)
    {
        // A long field in every sector, so that the count is of where fields fall and not of which few there are.
        var galaxy = EvenFields(42, 1f, thinned);
        var content = new SectorContent();
        var byDistance = new long[2, 20];
        var rocks = 0L;
        for (var x = -6; x <= 6; x++)
            for (var z = -6; z <= 6; z++)
            {
                galaxy.Generate(new SectorCoord(x, 3, z), content);
                rocks += content.AsteroidCount;
                for (var i = 0; i < content.AsteroidCount; i++)
                {
                    var p = content.Asteroids[i].LocalPosition;
                    byDistance[0, Math.Min(19, (int)(MathF.Abs(p.X) / 1000f))]++;
                    byDistance[1, Math.Min(19, (int)(MathF.Abs(p.Z) / 1000f))]++;
                }
            }

        // 169 sectors, a field of 3,000 anchored in each: as many rocks come in over the sample's edge as leave.
        Assert.InRange(rocks, thinned ? 250_000 : 480_000, thinned ? 380_000 : 520_000);
        for (var axis = 0; axis < 2; axis++)
        {
            var middle = 0L;
            for (var band = 0; band < 18; band++)
                middle += byDistance[axis, band];
            var edge = byDistance[axis, 18] + byDistance[axis, 19];
            var ratio = edge / 2.0 / (middle / 18.0);
            TestContext.Current.SendDiagnosticMessage($"axis {axis}, thinned {thinned}: the last two kilometres hold {ratio:0.000} of the average kilometre");
            Assert.InRange(ratio, 0.9, 1.12);
        }
    }

    /// <summary>
    /// A galaxy of long fields of 3,000 one-metre rocks each, anchored in the given share of sectors: every
    /// rock finds room.
    /// </summary>
    private static Galaxy EvenFields(ulong seed, float share, bool thinned = false)
    {
        var json = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "content", "galaxy", "galaxy.json")))!;
        var module = json["sectorModules"]![0]!.AsObject();
        module.Remove("baseline");
        if (!thinned)
            module.Remove("density");
        module.Remove("inGas");
        module["fieldsPerSector"] = new JsonArray(1, 1);
        module["emptySectorChance"] = 1;
        module["voidFieldChance"] = share;
        module["sparseFill"] = 1;
        module["arrivalClearance"] = 20;
        module["spacing"] = 1;
        module["kinds"] = JsonNode.Parse("""[{"weight":1,"asteroids":[3000,3000],"length":[24000,24000],"width":[9000,9000],"height":[3000,3000]}]"""u8);
        module["sizes"] = JsonNode.Parse("""[{"name":"Test","weight":1,"radius":[1,1]}]"""u8);
        return Galaxy.Parse(System.Text.Encoding.UTF8.GetBytes(json.ToJsonString()), "even fields", seed);
    }

    [Fact]
    public void EveryRockOfAFieldLandsInExactlyOneSector()
    {
        var galaxy = EvenFields(42, 0.002f);
        var fields = galaxy.AsteroidFields!;
        var content = new SectorContent();
        var half = (float)(galaxy.SectorSize / 2);
        var found = 0;
        var crossing = 0;
        for (var x = -60; x <= 60 && found < 6; x++)
            for (var z = -60; z <= 60 && found < 6; z++)
            {
                var anchor = new SectorCoord(x, 0, z);
                if (fields.Proposed(galaxy, anchor, out _) != 3000)
                    continue;

                // Alone: nothing else is anchored within three sectors, so every rock within two is this field's.
                var alone = true;
                for (var dx = -3; dx <= 3 && alone; dx++)
                    for (var dy = -3; dy <= 3 && alone; dy++)
                        for (var dz = -3; dz <= 3 && alone; dz++)
                            alone = (dx | dy | dz) == 0 || fields.Proposed(galaxy, anchor.Offset(dx, dy, dz), out _) == 0;
                if (!alone)
                    continue;
                found++;

                var total = 0;
                var holding = 0;
                for (var dx = -2; dx <= 2; dx++)
                    for (var dy = -2; dy <= 2; dy++)
                        for (var dz = -2; dz <= 2; dz++)
                        {
                            galaxy.Generate(anchor.Offset(dx, dy, dz), content);
                            if (Math.Max(Math.Abs(dx), Math.Max(Math.Abs(dy), Math.Abs(dz))) == 2)
                            {
                                // A field reaches the sectors around the one that anchors it and no further.
                                Assert.Equal(0, content.AsteroidCount);
                                continue;
                            }

                            total += content.AsteroidCount;
                            holding += content.AsteroidCount >= 150 ? 1 : 0;
                            for (var i = 0; i < content.AsteroidCount; i++)
                            {
                                var p = content.Asteroids[i].LocalPosition;
                                Assert.True(MathF.Abs(p.X) <= half - 1f && MathF.Abs(p.Y) <= half - 1f && MathF.Abs(p.Z) <= half - 1f);
                            }
                        }

                // Taken once each: never more rocks than the field has, and none lost at a wall but the odd one
                // that found no room.
                Assert.InRange(total, 2990, 3000);
                crossing += holding >= 2 ? 1 : 0;
            }

        Assert.Equal(6, found);
        Assert.True(crossing >= 3, $"only {crossing} of the six fields lie across a sector wall");
    }

    [Fact]
    public void ASectorIsGeneratedAloneAndComesOutTheSameWhateverCameBefore()
    {
        var galaxy = Content.Galaxy.WithSeed(DefaultSeed);
        var fresh = new SectorContent();
        var reused = new SectorContent();
        foreach (var coord in new SectorCoord[] { default, new(3, 0, 1), new(2, 0, -16), new(-3, 0, 5) })
        {
            galaxy.Generate(coord, fresh);
            Assert.True(fresh.AsteroidCount > 0);

            // The sectors around it leave their own contours, noise and keys behind in the working space.
            for (var dx = -1; dx <= 1; dx++)
                for (var dz = -1; dz <= 1; dz++)
                    galaxy.Generate(coord.Offset(dx, 0, dz), reused);
            galaxy.Generate(coord, reused);
            Assert.True(Sectors.Bytes(fresh).SequenceEqual(Sectors.Bytes(reused)), $"sector {coord} came out different after its neighbours");
            Assert.Equal(fresh.Summary.AsteroidCount, reused.Summary.AsteroidCount);
            Assert.Equal(fresh.Summary.FieldCount, reused.Summary.FieldCount);
            Assert.Equal(fresh.GasCloud, reused.GasCloud);
            Assert.Equal(fresh.BoundsCenter, reused.BoundsCenter);
            Assert.Equal(fresh.BoundsRadius, reused.BoundsRadius);
        }

        // Warm, with fields from every side and the noise lattice in use, generation takes no memory.
        var before = GC.GetAllocatedBytesForCurrentThread();
        galaxy.Generate(new SectorCoord(3, 0, 1), reused);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    private static bool Overlaps(Galaxy galaxy, SectorCoord anchor, in GasCloud cloud, SectorCoord sector)
    {
        var edge = (float)galaxy.SectorSize;
        var half = edge / 2;
        return MathF.Abs((anchor.X - sector.X) * edge + cloud.Center.X) <= half + cloud.HalfSize.X
            && MathF.Abs((anchor.Y - sector.Y) * edge + cloud.Center.Y) <= half + cloud.HalfSize.Y
            && MathF.Abs((anchor.Z - sector.Z) * edge + cloud.Center.Z) <= half + cloud.HalfSize.Z;
    }

    [Fact]
    public void AHugeCloudIsListedByEverySectorItReaches()
    {
        var galaxy = Content.Galaxy.WithSeed(DefaultSeed);
        var gas = galaxy.GasClouds!;
        var half = (float)(galaxy.SectorSize / 2);
        var listed = new GasCloudPlacement[GasCloudModule.MaxPerSector];
        var checkedClouds = 0;
        for (var x = -30; x <= 30 && checkedClouds < 5; x++)
            for (var z = -30; z <= 30 && checkedClouds < 5; z++)
            {
                var anchor = new SectorCoord(x, 0, z);
                if (!gas.TryGetCloud(galaxy, anchor, out var cloud) || cloud.HalfSize.X < galaxy.SectorSize * 0.25)
                    continue;
                checkedClouds++;
                var placement = new GasCloudPlacement(anchor, cloud);
                var reached = 0;
                for (var dx = -2; dx <= 2; dx++)
                    for (var dy = -2; dy <= 2; dy++)
                        for (var dz = -2; dz <= 2; dz++)
                        {
                            // Every sector the box overlaps lists the cloud as the sector that anchors it has
                            // it, and no other sector does.
                            var sector = anchor.Offset(dx, dy, dz);
                            var count = gas.Intersecting(galaxy, sector, listed);
                            var overlaps = Overlaps(galaxy, anchor, cloud, sector);
                            Assert.Equal(overlaps, listed.AsSpan(0, count).Contains(placement));
                            reached += overlaps ? 1 : 0;
                            if (!overlaps || (dx | dy | dz) == 0)
                                continue;

                            // Seen from that sector the cloud is centred in another one, and still found by a
                            // point of it inside this sector.
                            Assert.NotEqual(sector, anchor);
                            var inside = new Vector3(
                                Math.Clamp(dx * half * 2, cloud.Center.X - cloud.HalfSize.X, cloud.Center.X + cloud.HalfSize.X),
                                Math.Clamp(dy * half * 2, cloud.Center.Y - cloud.HalfSize.Y, cloud.Center.Y + cloud.HalfSize.Y),
                                Math.Clamp(dz * half * 2, cloud.Center.Z - cloud.HalfSize.Z, cloud.Center.Z + cloud.HalfSize.Z));
                            var near = gas.Intersecting(galaxy, galaxy.CenterOf(anchor) + inside, 1.0, listed);
                            Assert.Contains(placement, listed.AsSpan(0, near).ToArray());
                        }

                Assert.True(reached >= 2, $"the cloud anchored in {anchor} stays in its sector");
                var around = gas.Intersecting(galaxy, galaxy.CenterOf(anchor) + cloud.Center, 1.0, listed);
                Assert.Equal(placement, Assert.Single(listed.AsSpan(0, around).ToArray()));
            }

        Assert.Equal(5, checkedClouds);
    }

    [Fact]
    public void ASectorsNeighboursAreAllThatCanReachItWithACloud()
    {
        var galaxy = Content.Galaxy.WithSeed(7);
        var gas = galaxy.GasClouds!;
        var listed = new GasCloudPlacement[GasCloudModule.MaxPerSector];
        var again = new GasCloudPlacement[GasCloudModule.MaxPerSector];
        var reaching = 0;
        var fromElsewhere = 0;
        for (var x = -20; x <= 20; x++)
            for (var z = -20; z <= 20; z++)
            {
                var sector = new SectorCoord(x, 0, z);
                var count = gas.Intersecting(galaxy, sector, listed);
                Assert.Equal(count, gas.Intersecting(galaxy, sector, again));
                Assert.True(listed.AsSpan(0, count).SequenceEqual(again.AsSpan(0, count)));
                reaching += count;

                // Looked for much further out than a cloud can reach: the same clouds and no more.
                var brute = 0;
                for (var dx = -3; dx <= 3; dx++)
                    for (var dy = -3; dy <= 3; dy++)
                        for (var dz = -3; dz <= 3; dz++)
                        {
                            var anchor = sector.Offset(dx, dy, dz);
                            if (!gas.TryGetCloud(galaxy, anchor, out var cloud) || !Overlaps(galaxy, anchor, cloud, sector))
                                continue;
                            brute++;
                            fromElsewhere += anchor != sector ? 1 : 0;
                            Assert.Contains(new GasCloudPlacement(anchor, cloud), listed.AsSpan(0, count).ToArray());
                        }

                Assert.Equal(brute, count);
            }

        Assert.True(reaching > 60 && fromElsewhere > 10, $"{reaching} clouds reach the sample's sectors, {fromElsewhere} from another sector");
    }

    [Fact]
    public void CloudsAreMostlySmallSometimesHugeAndNeverTwoInView()
    {
        var galaxy = Content.Galaxy.WithSeed(DefaultSeed);
        var gas = galaxy.GasClouds!;
        var edge = (float)galaxy.SectorSize;
        var clouds = new List<(Double3 Center, float Reach)>();
        var huge = 0;
        var largest = 0f;
        for (var x = -40; x <= 40; x++)
            for (var y = -2; y <= 2; y++)
                for (var z = -40; z <= 40; z++)
                {
                    var anchor = new SectorCoord(x, y, z);
                    if (!gas.TryGetCloud(galaxy, anchor, out var cloud))
                        continue;
                    clouds.Add((galaxy.CenterOf(anchor) + cloud.Center, cloud.HalfSize.X));
                    largest = Math.Max(largest, cloud.HalfSize.X);
                    if (cloud.HalfSize.X >= edge * 0.25f)
                        huge++;
                    else
                        Assert.InRange(cloud.HalfSize.X, edge * 0.1f, edge * 0.14f);
                    Assert.Equal(GasCloudModule.Proportions * cloud.HalfSize.X, cloud.HalfSize);
                }

        // Most as they were, a few of them huge, up to a sector across.
        Assert.True(clouds.Count > 800, $"only {clouds.Count} clouds in the sample");
        Assert.InRange(huge / (float)clouds.Count, 0.03f, 0.2f);
        Assert.True(largest > edge * 0.45f, $"the largest cloud is {largest * 2:0} m across");

        // Clouds stand apart: no camera is inside or close to two at once, so composing the nearest is enough.
        for (var i = 0; i < clouds.Count; i++)
            for (var j = 0; j < i; j++)
            {
                // Each keeps its reach and the margin clear around its centre.
                var apart = (clouds[i].Center - clouds[j].Center).Length;
                Assert.True(apart >= clouds[i].Reach + clouds[j].Reach + 2 * GasCloudModule.SeparationMargin * edge - 1.0, $"two clouds {apart:0} m apart stand too close");
            }
    }
}
