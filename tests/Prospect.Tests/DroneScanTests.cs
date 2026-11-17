using System.Linq.Expressions;
using System.Numerics;
using System.Reflection;
using Prospect.Engine.Net;
using Prospect.Game.Client;
using Prospect.Game.Persistence;
using Prospect.Game.Protocol;
using Prospect.Game.Server;
using Prospect.Game.World;
using Xunit;

namespace Prospect.Tests;

/// <summary>
/// Drone scans of rocks: how many drones a rock takes and where they hold, the server's refusals and flow, results
/// worked out of the rock's interior, the wire, the save and the tick's memory. The shared rules run on their own;
/// the server tests tick a real server and its host client by hand.
/// </summary>
public sealed class DroneScanTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly GameContent Shipped = GameContent.Load();
    private static readonly DroneScanRules Rules = Shipped.Drones.Scans;
    private static readonly Galaxy World = Shipped.Galaxy.WithSeed(11);

    // Home, a dense sector, a very rich one with five resources, and a sector a giant lies in.
    private static readonly SectorCoord[] Coords = [default, new(-40, 0, -29), new(8, 0, -8), new(3, 0, 1)];

    /// <summary>A pod with one drone bay (four drones), or with <paramref name="secondBay"/> two (eight), and no tractor beam.</summary>
    private static GameContent Content(bool secondBay = true) => DroneFetchTests.Content(secondBay, tractor: false);

    // ------------------------------------------------------------------------------------------------------------
    // Shared rules
    // ------------------------------------------------------------------------------------------------------------

    private static readonly (string Name, float Radius)[] Sizes = [("small", 10f), ("medium", 100f), ("giant", 1000f)];

    /// <summary>The shape with the largest and the one with the smallest ratio of farthest to nearest surface.</summary>
    private static (int Long, int Round) ExtremeShapes()
    {
        var longest = 0;
        var roundest = 0;
        var most = 0f;
        var least = float.MaxValue;
        for (var shape = 0; shape < Galaxy.AsteroidShapes; shape++)
        {
            DroneScanning.Extent(shape, out var far, out _, out var near);
            var ratio = far / near;
            if (ratio > most) { most = ratio; longest = shape; }
            if (ratio < least) { least = ratio; roundest = shape; }
        }

        Assert.True(most > least * 1.2f, $"the shapes are too alike to tell long from round: {most} against {least}");
        return (longest, roundest);
    }

    [Fact]
    public void TheDroneCountFollowsTheRocksSizeAndNeverFallsBelowTheMinimum()
    {
        var (_, round) = ExtremeShapes();
        var previous = 0;
        foreach (var (name, radius) in Sizes)
        {
            var needed = DroneScanning.Needed(Rules, radius, round);
            Assert.True(needed >= Rules.MinDrones, $"{name}: {needed} is under the minimum {Rules.MinDrones}");
            Assert.True(needed >= previous, $"{name}: {needed} drones after {previous} for a smaller rock");
            previous = needed;
        }

        // A speck takes the minimum however small.
        Assert.Equal(Rules.MinDrones, DroneScanning.Needed(Rules, 0.5f, round));
        Assert.True(DroneScanning.Needed(Rules, 1000f, round) > Rules.MaxDrones);
        // The documented figures: a medium rock cannot be done by a bay of four, and the giant is too large.
        Assert.InRange(DroneScanning.Needed(Rules, 100f, round), 5, Rules.MaxDrones);
        // Every shape's giant asks for at least as many drones as its small rock, and none is zero.
        for (var shape = 0; shape < Galaxy.AsteroidShapes; shape++)
            Assert.InRange(DroneScanning.Needed(Rules, 10f, shape), Rules.MinDrones, DroneScanning.Needed(Rules, 1000f, shape));
    }

    [Fact]
    public void AnElongatedRockTakesAtLeastAsManyDronesAsARoundOneOfTheSameMeanRadius()
    {
        // "The same size" is the same mean radius in metres: a shape's radius is only its nominal one, and the shapes
        // reach different fractions of it. Over the same mean sphere, the longer rock has more surface and reaches farther.
        var (elongated, round) = ExtremeShapes();
        DroneScanning.Extent(elongated, out var farL, out var meanL, out var nearL);
        DroneScanning.Extent(round, out var farR, out var meanR, out var nearR);
        Assert.True(farL / nearL > farR / nearR);
        foreach (var (name, mean) in new[] { ("small", 8f), ("medium", 80f), ("giant", 800f) })
        {
            var stretched = DroneScanning.Needed(Rules, mean / meanL, elongated);
            var plain = DroneScanning.Needed(Rules, mean / meanR, round);
            Assert.True(stretched >= plain, $"{name}: shape {elongated} (ratio {farL / nearL:F2}) takes {stretched}, shape {round} (ratio {farR / nearR:F2}) takes {plain}");
        }
    }

    [Fact]
    public void TheStandoffAndTheShellFollowTheRocksSizeAndShape()
    {
        Assert.Equal(Rules.MinStandoffMetres, DroneScanning.Standoff(Rules, 1f));
        Assert.Equal(Rules.MaxStandoffMetres, DroneScanning.Standoff(Rules, 1e6f));
        Assert.Equal(60f, DroneScanning.Standoff(Rules, 100f), 3);
        for (var shape = 0; shape < Galaxy.AsteroidShapes; shape++)
        {
            DroneScanning.Extent(shape, out var far, out var mean, out var near);
            Assert.True(near <= mean && mean <= far && near > 0, $"shape {shape}: {near}, {mean}, {far}");
            var shell = DroneScanning.Shell(Rules, 100f, shape);
            Assert.True(shell >= far * 100f + DroneScanning.Standoff(Rules, 100f) - 1e-3f);
        }
    }

    [Fact]
    public void PlacesLieTheStandoffOffTheSurfaceAndSpreadApart()
    {
        foreach (var shape in new[] { 0, 3, 7, ExtremeShapes().Long, ExtremeShapes().Round })
            foreach (var (_, radius) in Sizes)
                foreach (var count in new[] { 2, 3, 6, 8, 12 })
                {
                    var standoff = DroneScanning.Standoff(Rules, radius);
                    var shell = DroneScanning.Shell(Rules, radius, shape);
                    var places = new Vector3[count];
                    for (var slot = 0; slot < count; slot++)
                    {
                        places[slot] = DroneScanning.Place(slot, count, radius, shape, standoff);
                        var direction = Vector3.Normalize(places[slot]);
                        RockInteriorSurface.Lookup(shape, direction.X, direction.Y, direction.Z, out var surface, out _);
                        // Along the direction, exactly the standoff beyond the surface, and never past the shell.
                        Assert.Equal(surface * radius + standoff, places[slot].Length(), 2);
                        Assert.True(places[slot].Length() <= shell + 1e-2f);
                    }

                    // Even spread: on the farthest shell's sphere, about sqrt(4 pi / n) of its radius apart; none within half that.
                    var spacing = MathF.Sqrt(4f * MathF.PI / count);
                    var low = float.MaxValue;
                    for (var a = 0; a < count; a++)
                        for (var b = a + 1; b < count; b++)
                        {
                            var apart = (places[a] - places[b]).Length();
                            low = Math.Min(low, apart);
                            Assert.True(apart >= 0.5f * spacing * (shell - standoff) * 0.5f, $"shape {shape} r {radius} n {count}: places {a} and {b} only {apart:F2} m apart");
                        }

                    Assert.True(low > 0);
                }
    }

    [Fact]
    public void DirectionsSpreadOverTheWholeSphere()
    {
        for (var count = 2; count <= DroneScanning.MaxDrones; count++)
        {
            var sum = Vector3.Zero;
            for (var i = 0; i < count; i++)
            {
                var d = DroneScanning.Direction(i, count);
                Assert.Equal(1f, d.Length(), 3);
                sum += d;
            }

            // A spiral over the sphere balances about the centre.
            Assert.True(sum.Length() < 0.15f * count + 0.5f, $"{count} directions lean {sum.Length():F2}");
        }

        Assert.Equal(Vector3.UnitY, DroneScanning.Direction(0, 1));
    }

    [Fact]
    public void ALegAroundTheRockNeverCutsThroughIt()
    {
        var step = Rules.TravelStepRadians;
        var checkedLegs = 0;
        for (var shape = 0; shape < Galaxy.AsteroidShapes; shape++)
            foreach (var (_, radius) in Sizes)
            {
                var standoff = DroneScanning.Standoff(Rules, radius);
                var shell = DroneScanning.Shell(Rules, radius, shape);
                foreach (var count in new[] { 3, 8, 12 })
                    for (var slot = 0; slot < count; slot++)
                    {
                        var place = DroneScanning.Place(slot, count, radius, shape, standoff);
                        // From the shell and from outside it, in every direction of a 24-point spiral.
                        for (var s = 0; s < 24; s++)
                            foreach (var scale in new[] { 1f, 1.7f, 4f })
                            {
                                var at = DroneScanning.Direction(s, 24) * shell * scale;
                                for (var hops = 0; hops < 64; hops++)
                                {
                                    var next = DroneScanning.Travel(at, place, shell, step);
                                    for (var k = 0; k <= 48; k++)
                                    {
                                        var point = Vector3.Lerp(at, next, k / 48f);
                                        var d = Vector3.Normalize(point);
                                        RockInteriorSurface.Lookup(shape, d.X, d.Y, d.Z, out var surface, out _);
                                        Assert.True(point.Length() > surface * radius,
                                            $"shape {shape} r {radius} n {count} slot {slot} from {scale}x shell dir {s}, hop {hops}: {point.Length():F2} m from the centre, surface {surface * radius:F2} m");
                                    }

                                    checkedLegs++;
                                    if (next == place) break;
                                    at = next;
                                }

                                Assert.True(at != default);
                            }
                    }
            }

        Assert.True(checkedLegs > 10000);
    }

    [Fact]
    public void TravelReachesAPlaceInAFewStepsEvenFromTheFarSide()
    {
        var step = Rules.TravelStepRadians;
        var shell = DroneScanning.Shell(Rules, 100f, 0);
        var place = DroneScanning.Place(0, 6, 100f, 0, DroneScanning.Standoff(Rules, 100f));
        // From the exact opposite side the great circle has no unique axis: any one will do, and it still arrives.
        var at = -Vector3.Normalize(place) * shell;
        var hops = 0;
        while (at != place && hops++ < 100) at = DroneScanning.Travel(at, place, shell, step);
        Assert.Equal(place, at);
        Assert.InRange(hops, 2, (int)MathF.Ceiling(MathF.PI / step) + 2);
        // At the centre the rock has no side to go round: straight to the place.
        Assert.Equal(place, DroneScanning.Travel(Vector3.Zero, place, shell, step));
    }

    [Fact]
    public void TheScanFiguresAreReadAndNonsenseIsRefused()
    {
        Assert.Equal(3000f, Rules.RangeMetres);
        Assert.Equal(0.001f, Rules.Resolution);
        Assert.Equal(3, Rules.MinDrones);
        Assert.Equal(12, Rules.MaxDrones);
        Assert.Equal(360L, Rules.ScanTicks);
        Assert.Equal(180L * 60, Rules.MoveTicks);
        Assert.Equal(120L * 60, Rules.ReadyTicks);

        const string Good = """{ "rangeMetres": 3000, "standoffFraction": 0.6, "minStandoffMetres": 12, "maxStandoffMetres": 300, "placeToleranceMetres": 2.5, "travelStepDegrees": 25, "moveSeconds": 180, "readySeconds": 120, "resolution": 0.001, "coverageSquareMetres": 20000, "cover": 1, "minDrones": 3, "maxDrones": 12, "scanSeconds": 6 }""";
        using (var good = System.Text.Json.JsonDocument.Parse(Good)) Assert.Equal(Rules, DroneScanRules.Read(good.RootElement, 12));

        foreach (var broken in new[]
        {
            Good.Replace("\"rangeMetres\": 3000", "\"rangeMetres\": 0"),
            Good.Replace("\"travelStepDegrees\": 25", "\"travelStepDegrees\": 90"),
            Good.Replace("\"resolution\": 0.001", "\"resolution\": 0.5"),
            Good.Replace("\"minStandoffMetres\": 12", "\"minStandoffMetres\": 400"),
            Good.Replace("\"minDrones\": 3", "\"minDrones\": 13"),
            Good.Replace("\"maxDrones\": 12", "\"maxDrones\": 40"),
            Good.Replace("\"cover\": 1,", "\"cover\": 0,"),
        })
        {
            using var document = System.Text.Json.JsonDocument.Parse(broken);
            Assert.Throws<InvalidDataException>(() => DroneScanRules.Read(document.RootElement, 12));
        }

        // A scan may not ask for more drones than a player may have out.
        using var tight = System.Text.Json.JsonDocument.Parse(Good);
        Assert.Throws<InvalidDataException>(() => DroneScanRules.Read(tight.RootElement, 10));
    }

    // ------------------------------------------------------------------------------------------------------------
    // Results worked out of the interior
    // ------------------------------------------------------------------------------------------------------------

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

    /// <summary>A record built the way the server builds one, for any rock.</summary>
    internal static DroneScanRecord Record(Galaxy galaxy, RockRef rock, in Asteroid asteroid, uint owner, long tick)
    {
        var record = new DroneScanRecord { Owner = owner, Sector = rock.Sector, Index = rock.Index, RecordedTick = tick };
        RockInterior.Describe(galaxy, rock, asteroid, out var interior);
        DroneScanResults.Build(asteroid, interior, Rules.Resolution, ref record);
        return record;
    }

    /// <summary>The <paramref name="rank"/>-th richest rock of the sector's first few hundred (0 is the richest): ore, a mix-in, veins and pockets.</summary>
    internal static int RichRock(RockField rocks, Galaxy galaxy, SectorCoord sector, int rank = 0)
    {
        var content = rocks.Sector(sector);
        var scored = new List<(int Score, int Index)>();
        for (var i = 0; i < Math.Min(content.AsteroidCount, 400); i++)
        {
            var asteroid = content.Asteroids[i];
            if (asteroid.Resource < 0) continue;
            RockInterior.Describe(galaxy, new RockRef(sector, i), asteroid, out var interior);
            scored.Add((interior.VeinCount + interior.PocketCount + (asteroid.Mix1 >= 0 ? 4 : 0), i));
        }

        Assert.True(scored.Count > rank, "the sector has no rock with ore to scan");
        scored.Sort((x, y) => x.Score != y.Score ? y.Score.CompareTo(x.Score) : x.Index.CompareTo(y.Index));
        return scored[rank].Index;
    }

    private static float Floored(float share) => (float)(Math.Floor(share / (double)Rules.Resolution + 1e-4) * Rules.Resolution);

    [Fact]
    public void ABuiltRecordMatchesTheRocksInteriorAndIsValid()
    {
        var scanned = 0;
        var withVeins = 0;
        var withUnstable = 0;
        var withMix = 0;
        var heatUnstable = 0;
        var rocks = Rocks(World, 24);
        Assert.True(rocks.Count >= 40);
        foreach (var (rock, asteroid) in rocks)
        {
            RockInterior.Describe(World, rock, asteroid, out var interior);
            {
                var record = Record(World, rock, asteroid, 5, 9);
                Assert.True(DroneScanRecordRules.Valid(record), $"{rock}");

                // The bulk: the rock's true shares, floored to the drones' step, never more than is there.
                Assert.Equal(asteroid.Resource, record.Resource);
                Assert.Equal(asteroid.Resource >= 0 ? asteroid.Mix1 : -1, record.Mix1);
                Assert.Equal(asteroid.Resource >= 0 ? Floored(asteroid.Grade) : 0f, record.Grade, 6);
                if (asteroid.Resource >= 0)
                {
                    Assert.InRange(asteroid.Grade - record.Grade, -1e-6f, Rules.Resolution + 1e-5f);
                    Assert.Equal(MathF.Min(record.Grade, Floored(asteroid.Mix1Share)), asteroid.Mix1 >= 0 ? record.Mix1Share : 0f, 6);
                    if (asteroid.Mix1 >= 0 && asteroid.Mix2 >= 0)
                        Assert.Equal(MathF.Min(record.Mix1Share, Floored(asteroid.Mix2Share)), record.Mix2Share, 6);
                    if (asteroid.Mix1 >= 0) withMix++;
                }

                Assert.Equal(asteroid.Radius, record.Radius);
                scanned++;
                CheckMap(record, interior, asteroid);
                if (record.Map.VeinCount > 0) withVeins++;
                for (var p = 0; p < record.Map.PocketCount; p++) if (record.Map.Pockets[p].Unstable) { withUnstable++; break; }
                for (var c = 0; c < DroneScanning.HeatCells; c++) if (record.Map.Unstable(c)) { heatUnstable++; break; }
            }
        }

        Assert.True(scanned >= 40 && withVeins >= 10 && withUnstable >= 3 && withMix >= 4 && heatUnstable >= 3,
            $"{scanned} scanned, {withVeins} with veins, {withUnstable} with an unstable pocket, {withMix} mixed, {heatUnstable} with an unstable heat cell");
    }

    private static void CheckMap(in DroneScanRecord record, in RockInterior interior, in Asteroid asteroid)
    {
        var radius = asteroid.Radius;
        var shape = asteroid.Shape;
        // One quantum of a point is radius / 32767 metres; rounding costs half of it, the float arithmetic a little more.
        var quantum = radius / 32767f * 1.02f + 1e-4f;
        var radiusQuantum = radius / 65535f * 1.02f + 1e-4f;
        Assert.Equal(Math.Min(interior.VeinCount, RockInteriorRules.MaxVeins), record.Map.VeinCount);
        for (var v = 0; v < record.Map.VeinCount; v++)
        {
            Assert.Equal(interior.VeinPointCount, record.Map.Veins[v].Points);
            Assert.Equal(interior.Resource, record.Map.Veins[v].Resource);
            for (var k = 0; k < record.Map.Veins[v].Points; k++)
            {
                var n = interior.VeinPoint(v, k);
                var d = Vector3.Normalize(new Vector3(n.X, n.Y, n.Z));
                RockInteriorSurface.Lookup(shape, d.X, d.Y, d.Z, out var surface, out _);
                var expected = new Vector3(n.X, n.Y, n.Z) * surface * radius;
                var actual = record.VeinPoint(v, k);
                Assert.True(Math.Abs(actual.X - expected.X) <= quantum && Math.Abs(actual.Y - expected.Y) <= quantum && Math.Abs(actual.Z - expected.Z) <= quantum,
                    $"vein {v} point {k}: {actual} against {expected} (quantum {quantum})");
                Assert.True(Math.Abs(actual.W - n.W * surface * radius) <= radiusQuantum, $"vein {v} point {k}: radius {actual.W} against {n.W * surface * radius}");
            }
        }

        Assert.Equal(Math.Min(interior.PocketCount, RockInteriorRules.MaxPockets), record.Map.PocketCount);
        for (var p = 0; p < record.Map.PocketCount; p++)
        {
            Assert.Equal(interior.PocketKind(p), record.Map.Pockets[p].Kind);
            Assert.Equal(interior.PocketKind(p) != RockPocketKind.Ore, record.Map.Pockets[p].Unstable);
            var c = interior.PocketAt(p);
            var d = Vector3.Normalize(new Vector3(c.X, c.Y, c.Z));
            RockInteriorSurface.Lookup(shape, d.X, d.Y, d.Z, out var surface, out _);
            var at = record.PocketAt(p);
            var expected = new Vector3(c.X, c.Y, c.Z) * surface * radius;
            Assert.True(Math.Abs(at.X - expected.X) <= quantum && Math.Abs(at.Y - expected.Y) <= quantum && Math.Abs(at.Z - expected.Z) <= quantum, $"pocket {p}: {at} against {expected}");
            Assert.True(Math.Abs(at.W - c.W * surface * radius) <= radiusQuantum);
            if (record.Map.Pockets[p].Unstable) Assert.Equal(-1, record.Map.Pockets[p].Resource);
            else Assert.True(record.Map.Pockets[p].Resource >= 0);
        }

        // The heat map: a cell flagged unstable has an unstable sample on its ray, and an unflagged one has none.
        for (var cell = 0; cell < DroneScanning.HeatCells; cell++)
        {
            var d = DroneScanning.Direction(cell, DroneScanning.HeatCells);
            RockInteriorSurface.Lookup(shape, d.X, d.Y, d.Z, out var surface, out _);
            Assert.Equal((byte)Math.Round(surface * 255f), record.Map.Surface[cell]);
            var unstable = false;
            for (var k = 1; k <= DroneScanning.HeatDepthSamples; k++)
            {
                interior.Sample(d * (surface * radius * (1f - k / (DroneScanning.HeatDepthSamples + 1f))), out var sample);
                unstable |= sample.Unstable;
            }

            Assert.Equal(unstable, record.Map.Unstable(cell));
            Assert.InRange(record.Map.Ease(cell), 0f, 1f);
            // The point a cell reports lies on the rock's surface, to the byte.
            Assert.True(Math.Abs(record.HeatPoint(cell).Length() - surface * radius) <= radius / 255f * 0.51f + 1e-3f);
        }
    }

    [Fact]
    public void AnUnstablePocketIsFlaggedInTheHeatMapAboveIt()
    {
        // A ray that runs through the heart of an ice or gas pocket (in the rock's normalised space, where it is a ball)
        // flags its cell; the cell is found independently of how the map decides.
        var found = 0;
        foreach (var (rock, asteroid) in Rocks(World, 60))
        {
            RockInterior.Describe(World, rock, asteroid, out var interior);
            var record = Record(World, rock, asteroid, 5, 9);
            for (var p = 0; p < interior.PocketCount; p++)
            {
                if (interior.PocketKind(p) == RockPocketKind.Ore) continue;
                var c = interior.PocketAt(p);
                var centre = new Vector3(c.X, c.Y, c.Z);
                for (var cell = 0; cell < DroneScanning.HeatCells; cell++)
                {
                    var d = DroneScanning.Direction(cell, DroneScanning.HeatCells);
                    for (var k = 1; k <= DroneScanning.HeatDepthSamples; k++)
                    {
                        var depth = 1f - k / (DroneScanning.HeatDepthSamples + 1f);
                        if ((d * depth - centre).Length() >= 0.8f * c.W) continue;
                        // An earlier pocket that holds the point answers for it first, as Sample does.
                        var shadowed = false;
                        for (var q = 0; q < p; q++)
                        {
                            var other = interior.PocketAt(q);
                            shadowed |= (d * depth - new Vector3(other.X, other.Y, other.Z)).Length() <= other.W;
                        }

                        if (shadowed) continue;
                        Assert.True(record.Map.Unstable(cell), $"{rock} pocket {p} radius {c.W:F2}: cell {cell} runs through its heart at depth {depth:F2}");
                        found++;
                        break;
                    }
                }
            }
        }

        Assert.True(found >= 3, $"only {found} cells ran through an unstable pocket in the sample");
    }

    [Fact]
    public void ARockWithNoOreScansAsBareRock()
    {
        var bare = 0;
        foreach (var (rock, asteroid) in Rocks(World, 60))
        {
            if (asteroid.Resource >= 0) continue;
            bare++;
            var record = Record(World, rock, asteroid, 5, 9);
            Assert.Equal((-1, -1, -1, 0f), (record.Resource, record.Mix1, record.Mix2, record.Grade));
            Assert.Equal(0, record.Map.VeinCount);
            Assert.True(DroneScanRecordRules.Valid(record));
        }

        Assert.True(bare > 0, "no bare rock in the sample");
    }

    [Fact]
    public void AValidRecordIsRefusedWhenItsPartsDisagree()
    {
        var (rock, asteroid) = Rocks(World, 60).First(r => r.Asteroid.Resource >= 0 && r.Asteroid.Mix1 >= 0 && r.Asteroid.Mix2 >= 0);
        var good = Record(World, rock, asteroid, 5, 9);
        Assert.True(DroneScanRecordRules.Valid(good));
        var cases = new List<(string What, DroneScanRecord Record)>();
        void Case(string what, Func<DroneScanRecord, DroneScanRecord> change) => cases.Add((what, change(good)));
        Case("no owner", r => { r.Owner = 0; return r; });
        Case("negative index", r => { r.Index = -1; return r; });
        Case("past the wire's index", r => { r.Index = 70000; return r; });
        Case("negative tick", r => { r.RecordedTick = -1; return r; });
        Case("radius zero", r => { r.Radius = 0; return r; });
        Case("radius NaN", r => { r.Radius = float.NaN; return r; });
        Case("share above one", r => { r.Grade = 1.5f; return r; });
        Case("share NaN", r => { r.Mix1Share = float.NaN; return r; });
        Case("mix-in named twice", r => { r.Mix1 = r.Resource; return r; });
        Case("second above first", r => { r.Mix2Share = r.Mix1Share + 0.1f; return r; });
        Case("shares over one", r => { r.Grade = 0.9f; r.Mix1Share = 0.5f; return r; });
        Case("resource 32", r => { r.Resource = 32; return r; });
        Case("bare rock with a grade", r => { r.Resource = -1; return r; });
        Case("nine veins", r => { r.Map.VeinCount = 9; return r; });
        Case("five pockets", r => { r.Map.PocketCount = 5; return r; });
        Case("a vein of one point", r => { r.Map.VeinCount = 1; r.Map.Veins[0].Points = 1; return r; });
        Case("a vein of seventeen points", r => { r.Map.VeinCount = 1; r.Map.Veins[0].Points = 17; return r; });
        Case("a point at MinValue", r => { r.Map.VeinCount = 1; r.Map.Veins[0].Points = 2; r.Map.Points[0].X = short.MinValue; return r; });
        Case("an unstable pocket with ore", r => { r.Map.PocketCount = 1; r.Map.Pockets[0] = new DroneScanPocket { Kind = RockPocketKind.Ice, Resource = 1 }; return r; });
        Case("a pocket of kind 3", r => { r.Map.PocketCount = 1; r.Map.Pockets[0] = new DroneScanPocket { Kind = (RockPocketKind)3 }; return r; });
        foreach (var (what, record) in cases) Assert.False(DroneScanRecordRules.Valid(record), what);
    }

    // ------------------------------------------------------------------------------------------------------------
    // Wire
    // ------------------------------------------------------------------------------------------------------------

    private static DroneScanRecord RichRecord(uint owner = 7, long tick = 1234)
    {
        var content = new SectorContent();
        var best = default((RockRef Rock, Asteroid Asteroid));
        var score = -1;
        foreach (var coord in Coords)
        {
            World.Generate(coord, content);
            for (var i = 0; i < content.AsteroidCount; i++)
            {
                if (content.Asteroids[i].Resource < 0 || content.Asteroids[i].Mix1 < 0) continue;
                RockInterior.Describe(World, new RockRef(coord, i), content.Asteroids[i], out var interior);
                var s = interior.VeinCount * 4 + interior.PocketCount * 3 + interior.VeinPointCount;
                if (s > score) { score = s; best = (new RockRef(coord, i), content.Asteroids[i]); }
            }
        }

        Assert.True(score > 0);
        return Record(World, best.Rock, best.Asteroid, owner, tick);
    }

    internal static byte[] Bytes(in DroneScanRecord record)
    {
        var buffer = new byte[DroneScanCodec.MaxBytes + 16];
        var writer = new NetWriter(buffer);
        DroneScanCodec.Write(ref writer, record);
        return writer.Written.ToArray();
    }

    [Fact]
    public void ARecordRoundTripsThroughTheCodecAndSizeSaysHowManyBytes()
    {
        var record = RichRecord();
        var bytes = Bytes(record);
        Assert.Equal(DroneScanCodec.Size(record), bytes.Length);
        Assert.InRange(bytes.Length, DroneScanCodec.MinBytes, DroneScanCodec.MaxBytes);
        var reader = new NetReader(bytes);
        Assert.True(DroneScanCodec.Read(ref reader, out var copy));
        Assert.Equal(0, reader.Remaining);
        Assert.Equal(bytes, Bytes(copy));
        Assert.Equal((record.Owner, record.Sector, record.Index, record.RecordedTick, record.Radius), (copy.Owner, copy.Sector, copy.Index, copy.RecordedTick, copy.Radius));
        Assert.Equal((record.Resource, record.Mix1, record.Mix2, record.Grade, record.Mix1Share, record.Mix2Share),
            (copy.Resource, copy.Mix1, copy.Mix2, copy.Grade, copy.Mix1Share, copy.Mix2Share));
        Assert.Equal(record.Map.VeinCount, copy.Map.VeinCount);
        Assert.Equal(record.Map.PocketCount, copy.Map.PocketCount);
        Assert.True(record.Map.VeinCount > 0 && record.Map.PocketCount > 0, "the sample's richest rock has no veins and pockets to carry");
        for (var v = 0; v < record.Map.VeinCount; v++)
            for (var k = 0; k < record.Map.Veins[v].Points; k++)
                Assert.Equal(record.VeinPoint(v, k), copy.VeinPoint(v, k));
        for (var c = 0; c < DroneScanning.HeatCells; c++) Assert.Equal((record.Map.Heat[c], record.Map.Surface[c]), (copy.Map.Heat[c], copy.Map.Surface[c]));

        var message = new byte[DroneScanResultMessage.MaxWireSize];
        var writer = new NetWriter(message);
        DroneScanResultMessage.Write(ref writer, record);
        Assert.Equal(1 + bytes.Length, writer.Written.Length);
        Assert.Equal((byte)MessageId.DroneScanResult, writer.Written[0]);
        reader = new NetReader(writer.Written[1..]);
        Assert.True(DroneScanResultMessage.TryRead(ref reader, out var viaMessage));
        Assert.Equal(bytes, Bytes(viaMessage));
    }

    [Fact]
    public void TheLargestMapFitsTheBoundExactly()
    {
        var record = RichRecord();
        record.Map.VeinCount = RockInteriorRules.MaxVeins;
        record.Map.PocketCount = RockInteriorRules.MaxPockets;
        for (var v = 0; v < RockInteriorRules.MaxVeins; v++)
        {
            record.Map.Veins[v] = new DroneScanVein { Points = RockInteriorRules.MaxVeinPoints, Resource = 0, Grade = 200 };
            for (var k = 0; k < RockInteriorRules.MaxVeinPoints; k++)
                record.Map.Points[v * RockInteriorRules.MaxVeinPoints + k] = new DroneScanPoint { X = (short)(v * 100 + k), Y = (short)-k, Z = 32767, R = 65535 };
        }

        for (var p = 0; p < RockInteriorRules.MaxPockets; p++)
            record.Map.Pockets[p] = new DroneScanPocket { X = 1, Y = 2, Z = 3, R = 4, Kind = p == 0 ? RockPocketKind.Ore : RockPocketKind.Ice, Resource = (sbyte)(p == 0 ? 1 : -1), Grade = (byte)(p == 0 ? 10 : 0) };
        Assert.True(DroneScanRecordRules.Valid(record));
        Assert.Equal(DroneScanCodec.MaxBytes, DroneScanCodec.Size(record));
        var bytes = Bytes(record);
        Assert.Equal(DroneScanCodec.MaxBytes, bytes.Length);
        Assert.Equal(1 + DroneScanCodec.MaxBytes, DroneScanResultMessage.MaxWireSize);
        var reader = new NetReader(bytes);
        Assert.True(DroneScanCodec.Read(ref reader, out var copy));
        Assert.Equal(bytes, Bytes(copy));
        Assert.Equal(DroneScanCodec.HeadBytes + DroneScanCodec.MapFixedBytes + 8 * (DroneScanCodec.VeinBytes + 16 * DroneScanCodec.PointBytes) + 4 * DroneScanCodec.PocketBytes, DroneScanCodec.MaxBytes);
    }

    [Fact]
    public void MalformedRecordBytesAreRefused()
    {
        var bytes = Bytes(RichRecord());
        var veinCountAt = DroneScanCodec.HeadBytes;
        var pocketCountAt = DroneScanCodec.HeadBytes + 1;
        var firstVeinPointsAt = DroneScanCodec.HeadBytes + 2;

        bool Reads(byte[] data) { var r = new NetReader(data); return DroneScanCodec.Read(ref r, out _); }
        byte[] With(int at, byte value) { var copy = (byte[])bytes.Clone(); copy[at] = value; return copy; }

        Assert.True(Reads(bytes));
        Assert.False(Reads(With(veinCountAt, (byte)(RockInteriorRules.MaxVeins + 1))));
        Assert.False(Reads(With(pocketCountAt, (byte)(RockInteriorRules.MaxPockets + 1))));
        Assert.False(Reads(With(firstVeinPointsAt, (byte)(RockInteriorRules.MaxVeinPoints + 1))));
        Assert.False(Reads(With(firstVeinPointsAt, 1)));
        Assert.False(Reads(With(firstVeinPointsAt + 1, 40)));
        Assert.False(Reads(bytes[..^1]));
        Assert.False(Reads(bytes[..DroneScanCodec.HeadBytes]));
        Assert.False(Reads(bytes[..10]));
        Assert.False(Reads([]));
        // The head: an owner of zero.
        Assert.False(Reads([.. new byte[4], .. bytes[4..]]));

        // Trailing bytes: a message with anything after the record is refused.
        var trailing = new byte[bytes.Length + 1];
        bytes.CopyTo(trailing, 0);
        var reader = new NetReader(trailing);
        Assert.False(DroneScanResultMessage.TryRead(ref reader, out _));
        reader = new NetReader(bytes);
        Assert.True(DroneScanResultMessage.TryRead(ref reader, out _));
    }

    [Fact]
    public void TheCommandReadsOnlyWhatTheServerCouldHaveBeenAsked()
    {
        var buffer = new byte[64];
        bool Reads(DroneScanCommandMessage message, out DroneScanCommandMessage read, int trailing = 0)
        {
            var writer = new NetWriter(buffer);
            message.Write(ref writer);
            Assert.Equal(DroneScanCommandMessage.MaxWireSize, writer.Written.Length);
            Assert.Equal((byte)MessageId.DroneScan, writer.Written[0]);
            var reader = new NetReader(trailing == 0 ? writer.Written[1..] : [.. writer.Written[1..], .. new byte[trailing]]);
            return DroneScanCommandMessage.TryRead(ref reader, out read);
        }

        var rock = new RockRef(new SectorCoord(-3, 0, 7), 41);
        var hull = 7u | 1u << 16;
        Assert.True(Reads(new(hull, DroneScanAction.Request, rock), out var read));
        Assert.Equal(new DroneScanCommandMessage(hull, DroneScanAction.Request, rock), read);
        Assert.True(Reads(new(hull, DroneScanAction.Request, new RockRef(default, ushort.MaxValue)), out _));
        Assert.True(Reads(new(hull, DroneScanAction.Start, default), out _));
        Assert.True(Reads(new(hull, DroneScanAction.Cancel, default), out _));

        // A request names a rock; a start or cancel names none; the hull is never zero; nothing trails.
        Assert.False(Reads(new(hull, DroneScanAction.Request, new RockRef(default, -1)), out _));
        Assert.False(Reads(new(hull, DroneScanAction.Request, new RockRef(default, ushort.MaxValue + 1)), out _));
        Assert.False(Reads(new(hull, DroneScanAction.Start, rock), out _));
        Assert.False(Reads(new(hull, DroneScanAction.Cancel, new RockRef(default, 1)), out _));
        Assert.False(Reads(new(hull, (DroneScanAction)0, default), out _));
        Assert.False(Reads(new(hull, (DroneScanAction)4, default), out _));
        Assert.False(Reads(new(0, DroneScanAction.Start, default), out _));
        Assert.False(Reads(new(hull, DroneScanAction.Start, default), out _, trailing: 1));

        // A message that is cut short is refused too.
        var writer = new NetWriter(buffer);
        new DroneScanCommandMessage(hull, DroneScanAction.Start, default).Write(ref writer);
        var reader = new NetReader(writer.Written[1..^3]);
        Assert.False(DroneScanCommandMessage.TryRead(ref reader, out _));
    }

    [Fact]
    public void TheBayViewCarriesAScanAndRefusesOneThatDisagreesWithItself()
    {
        var buffer = new byte[BayMessage.MaxWireSize];
        var rock = new RockRef(new SectorCoord(1, -2, 3), 9);
        BayView Scanning(DroneScanPhase phase, byte drones = 6, byte inPlace = 4, long started = 0, long completes = 0) => new()
        {
            Hull = 9,
            Tick = 500,
            Reply = DroneReply.ScanSent,
            ReplyCount = 6,
            ScanPhase = phase,
            ScanRock = rock,
            ScanDrones = drones,
            ScanInPlace = inPlace,
            ScanStarted = started,
            ScanCompletes = completes,
        };

        bool Round(BayView view, out BayView copy)
        {
            var writer = new NetWriter(buffer);
            BayMessage.Write(ref writer, view);
            Assert.Equal(BayMessage.HeaderBytes + view.Count * BayMessage.RecordBytes, writer.Written.Length);
            var reader = new NetReader(writer.Written[1..]);
            return BayMessage.TryRead(ref reader, out copy);
        }

        foreach (var phase in new[] { DroneScanPhase.Moving, DroneScanPhase.Ready, DroneScanPhase.Returning })
        {
            Assert.True(Round(Scanning(phase), out var copy));
            Assert.Equal((phase, rock, (byte)6, (byte)4), (copy.ScanPhase, copy.ScanRock, copy.ScanDrones, copy.ScanInPlace));
            Assert.Equal(DroneReply.ScanSent, copy.Reply);
        }

        Assert.True(Round(Scanning(DroneScanPhase.Scanning, 6, 6, 100, 460), out var running));
        Assert.Equal((100L, 460L), (running.ScanStarted, running.ScanCompletes));
        Assert.True(Round(new BayView { Hull = 9, Tick = 5 }, out var none));
        Assert.Equal(DroneScanPhase.None, none.ScanPhase);

        // The last reply a view may carry is the last one the enum has.
        Assert.Equal(31, (int)DroneReply.ScanUnavailable);
        Assert.True(Round(new BayView { Hull = 9, Tick = 5, Reply = DroneReply.ScanUnavailable }, out _));
        Assert.False(Round(new BayView { Hull = 9, Tick = 5, Reply = (DroneReply)32 }, out _));

        Assert.False(Round(Scanning(DroneScanPhase.None), out _));                                   // a rock with no phase
        Assert.False(Round(new BayView { Hull = 9, Tick = 5, ScanDrones = 2 }, out _));              // drones with no scan
        Assert.False(Round(new BayView { Hull = 9, Tick = 5, ScanInPlace = 1 }, out _));
        Assert.False(Round(new BayView { Hull = 9, Tick = 5, ScanRock = rock }, out _));
        Assert.False(Round(Scanning(DroneScanPhase.Moving, drones: 0, inPlace: 0), out _));
        Assert.False(Round(Scanning(DroneScanPhase.Moving, drones: 33, inPlace: 0), out _));         // more than any scan takes
        Assert.False(Round(Scanning(DroneScanPhase.Moving, drones: 6, inPlace: 7), out _));          // more in place than there are
        Assert.False(Round(Scanning((DroneScanPhase)5), out _));
        Assert.False(Round(Scanning(DroneScanPhase.Scanning, 6, 6, 460, 100), out _));               // ends before it began
        Assert.False(Round(Scanning(DroneScanPhase.Scanning, 6, 6, -1, 100), out _));
        Assert.False(Round(Scanning(DroneScanPhase.Moving, 6, 4, 100, 0), out _));                   // times with no scan running
        Assert.False(Round(Scanning(DroneScanPhase.Ready, 6, 4, 0, 460), out _));
        var badRock = Scanning(DroneScanPhase.Moving); badRock.ScanRock = new RockRef(rock.Sector, -1);
        Assert.False(Round(badRock, out _));

        Assert.Equal(73u, ProtocolInfo.Version);
        Assert.Equal(49, (int)MessageId.DroneScan);
        Assert.Equal(50, (int)MessageId.DroneScanResult);
        Assert.True(BayMessage.MaxWireSize >= BayMessage.HeaderBytes + DroneBayRules.MaxBays * BayMessage.RecordBytes);
    }

    [Fact]
    public void TheClientTakesOnlyItsOwnResultsAndKeepsOnePerRock()
    {
        using var world = new ClientWorld(Shipped);
        world.SetPlayer(7);
        var view = new DroneScanKnowledgeView();

        void Deliver(in DroneScanRecord record)
        {
            var buffer = new byte[DroneScanResultMessage.MaxWireSize];
            var writer = new NetWriter(buffer);
            DroneScanResultMessage.Write(ref writer, record);
            var reader = new NetReader(writer.Written[1..]);
            world.OnDroneScanResult(ref reader);
            world.CopyDroneScans(view);
        }

        var first = RichRecord(owner: 7, tick: 100);
        Deliver(first);
        Assert.Equal(1, view.Count);
        Assert.Equal(0, view.Find(first.Rock));
        // Someone else's result is ignored.
        Deliver(RichRecord(owner: 8, tick: 101));
        Assert.Equal(1, view.Count);
        // A newer scan of the same rock replaces the first rather than adding to it.
        var revision = view.Revision;
        Deliver(RichRecord(owner: 7, tick: 200));
        Assert.Equal(1, view.Count);
        Assert.Equal(200L, view.Records[0].RecordedTick);
        Assert.NotEqual(revision, view.Revision);
        // A resource the galaxy does not have is refused whole.
        var odd = RichRecord(owner: 7, tick: 300);
        odd.Index++;
        odd.Resource = Shipped.Galaxy.Resources.Length + 1;
        odd.Grade = 0.1f;
        odd.Mix1 = -1; odd.Mix2 = -1; odd.Mix1Share = 0; odd.Mix2Share = 0;
        Deliver(odd);
        Assert.Equal(1, view.Count);
        // More rocks than the view holds: the oldest gives way.
        for (var i = 0; i < DroneScanRecordRules.KnowledgePerOwner + 3; i++)
        {
            var record = RichRecord(owner: 7, tick: 1000 + i);
            record.Index = 1000 + i;
            Deliver(record);
        }

        Assert.Equal(DroneScanRecordRules.KnowledgePerOwner, view.Count);
        Assert.True(view.Find(new RockRef(first.Sector, 1000 + DroneScanRecordRules.KnowledgePerOwner + 2)) >= 0);
        Assert.True(view.Find(first.Rock) < 0);
    }

    // ------------------------------------------------------------------------------------------------------------
    // A real server and its host client, ticked by hand
    // ------------------------------------------------------------------------------------------------------------

    private static SaveRig Rig(GameContent content)
    {
        var rig = new SaveRig(SaveRig.NewWorld(content), content);
        rig.Join();
        rig.Tick();
        return rig;
    }

    private static uint Own(SaveRig rig) => rig.Link.LastSnapshot.OwnHull;

    private static HullHandle Pod(SaveRig rig) { var own = Own(rig); return new((int)(own & 0xFFFF), (int)(own >> 16)); }

    private static uint Owner(SaveRig rig) => rig.Hulls.Info(Pod(rig).Index).Owner;

    private static List<int> Drones(SaveRig rig)
    {
        var found = new List<int>();
        for (var i = 0; i < rig.Hulls.Slots; i++)
            if (rig.Hulls.IsAliveAt(i) && rig.Hulls.Info(i).WorkDrone) found.Add(i);
        return found;
    }

    private static void TickUntil(SaveRig rig, Func<bool> done, float seconds, string what)
    {
        for (var i = 0; i < seconds * 60 && !done(); i++)
            rig.Tick();
        Assert.True(done(), what);
    }

    /// <summary>A rock of the spawn sector, where it lies, and how far its farthest surface reaches in metres.</summary>
    private sealed record Target(RockRef Rock, Asteroid Asteroid, Double3 Centre, float Reach);

    /// <summary>
    /// The rock nearest <paramref name="radius"/> metres across: of the spawn sector, or with <paramref name="clear"/> of
    /// the sectors around it, among those with no other rock near enough to be in a drone's way.
    /// </summary>
    private static Target Pick(SaveRig rig, float radius, bool clear = false, Func<Asteroid, bool>? where = null)
    {
        var spawn = rig.Get<SectorCoord>("_spawnSector");
        var galaxy = rig.Server.Galaxy;
        Target? best = null;
        var span = clear ? 4 : 0;
        for (var dx = -span; dx <= span; dx++)
            for (var dz = -span; dz <= span; dz++)
            {
                var sector = new SectorCoord(spawn.X + dx, spawn.Y, spawn.Z + dz);
                var content = rig.Rocks.Sector(sector);
                for (var i = 0; i < content.AsteroidCount; i++)
                {
                    var asteroid = content.Asteroids[i];
                    if (where is not null && !where(asteroid)) continue;
                    if (best is not null && MathF.Abs(asteroid.Radius - radius) >= MathF.Abs(best.Asteroid.Radius - radius)) continue;
                    DroneScanning.Extent(asteroid.Shape, out var far, out _, out _);
                    var reach = far * asteroid.Radius;
                    var crowded = false;
                    for (var j = 0; j < content.AsteroidCount && clear && !crowded; j++)
                        crowded = j != i && (content.Asteroids[j].LocalPosition - asteroid.LocalPosition).Length() < reach + content.Asteroids[j].Radius * 1.5f + 500f;
                    if (!crowded) best = new Target(new RockRef(sector, i), asteroid, galaxy.CenterOf(sector) + asteroid.LocalPosition, reach);
                }
            }

        Assert.True(best is not null, "no rock to pick");
        Assert.InRange(best!.Asteroid.Radius, radius * 0.5f, radius * 3f);
        return best;
    }

    /// <summary>Sets the flown hull at rest <paramref name="away"/> metres past the rock's farthest surface, so the rock lies along -X from it.</summary>
    private static void Stand(SaveRig rig, Target target, float away)
    {
        ref var state = ref rig.Hulls.State(Pod(rig).Index);
        state.Position = target.Centre + new Double3(target.Reach + away, 0, 0);
        state.Orientation = Quaternion.Identity;
        state.Velocity = Vector3.Zero;
        state.AngularVelocity = Vector3.Zero;
        rig.Tick(2);
    }

    /// <summary>How far a point is outside a rock's surface, in metres (negative inside it).</summary>
    private static float Clearance(Double3 point, Target target)
    {
        var local = HullRotation.Unrotate(point.RelativeTo(target.Centre), target.Asteroid.Rotation);
        var length = local.Length();
        var d = length > 1e-4f ? local / length : Vector3.UnitY;
        RockInteriorSurface.Lookup(target.Asteroid.Shape, d.X, d.Y, d.Z, out var surface, out _);
        return length - surface * target.Asteroid.Radius;
    }

    private static void Command(SaveRig rig, DroneScanAction action, RockRef rock = default)
    {
        rig.Link.DroneScan(new DroneScanCommandMessage(Own(rig), action, rock));
        rig.Tick();
    }

    /// <summary>Asks for a scan and returns the server's answer, ticking half a second afterwards so the next command is not rate limited.</summary>
    private static DroneReply Ask(SaveRig rig, RockRef rock, out int count)
    {
        var before = rig.Link.Bays.ReplySequence;
        Command(rig, DroneScanAction.Request, rock);
        Assert.NotEqual(before, rig.Link.Bays.ReplySequence);
        var reply = rig.Link.Bays.Reply;
        count = rig.Link.Bays.ReplyCount;
        rig.Tick(30);
        return reply;
    }

    private static DroneReply Say(SaveRig rig, DroneScanAction action)
    {
        var before = rig.Link.Bays.ReplySequence;
        Command(rig, action);
        Assert.NotEqual(before, rig.Link.Bays.ReplySequence);
        var reply = rig.Link.Bays.Reply;
        rig.Tick(30);
        return reply;
    }

    private static int Needed(Target target) => DroneScanning.Needed(Rules, target.Asteroid.Radius, target.Asteroid.Shape);

    [Fact]
    public void TheHomeSectorHasASmallAMediumAndAGiantRockToScan()
    {
        using var rig = Rig(Content());
        Assert.InRange(Pick(rig, 20f, clear: true).Asteroid.Radius, 10f, 60f);
        Assert.InRange(Pick(rig, 200f).Asteroid.Radius, 100f, 400f);
        Assert.InRange(Pick(rig, 1900f).Asteroid.Radius, 900f, 5000f);
    }

    [Fact]
    public void AScanIsRefusedWithTheReasonAndSendsNoDrone()
    {
        var content = Content(secondBay: false);
        using var rig = Rig(content);
        var small = Pick(rig, 20f, clear: true);
        var medium = Pick(rig, 200f, where: a => DroneScanning.Needed(Rules, a.Radius, a.Shape) is >= 5 and <= 12);
        var giant = Pick(rig, 1900f);

        // No sensor reaches the rock: it is not found, however near it is on the map.
        Stand(rig, small, 8000f);
        Assert.Equal(DroneReply.NotFound, Ask(rig, small.Rock, out _));

        // In sight but out of the drones' range (their range is counted from the rock's surface).
        Stand(rig, small, Rules.RangeMetres + 500f);
        Assert.Equal(DroneReply.TooFar, Ask(rig, small.Rock, out _));

        // Rocks the sector does not have.
        Stand(rig, small, 150f);
        var sector = small.Rock.Sector;
        var count = rig.Rocks.Sector(sector).AsteroidCount;
        Assert.Equal(DroneReply.NotFound, Ask(rig, new RockRef(sector, count), out _));
        Assert.Equal(DroneReply.NotFound, Ask(rig, new RockRef(sector, ushort.MaxValue), out _));

        // A giant is too large, and the answer says how many drones it would take.
        Stand(rig, giant, 150f);
        Assert.True(Needed(giant) > Rules.MaxDrones);
        Assert.Equal(DroneReply.TooLarge, Ask(rig, giant.Rock, out var asked));
        Assert.Equal(Math.Min(Needed(giant), byte.MaxValue), asked);

        // A bay of four cannot scan a medium rock: that takes more drones than it holds.
        Stand(rig, medium, 150f);
        var needed = Needed(medium);
        Assert.InRange(needed, 5, Rules.MaxDrones);
        Assert.Equal(DroneReply.TooFew, Ask(rig, medium.Rock, out var few));
        Assert.Equal(needed, few);

        Assert.Empty(Drones(rig));
        Assert.Equal(DroneScanPhase.None, rig.Link.Bays.ScanPhase);
        Assert.Equal(4, rig.Link.Bays.Bays[0].Stocked);
        Assert.Equal(0, rig.Link.Bays.Bays[0].Pending);
        Assert.Empty(rig.Server.DroneScanKnowledge(Owner(rig)).ToArray());

        // Nothing is under way, so there is nothing to start; and nothing to give up.
        Assert.Equal(DroneReply.ScanNotReady, Say(rig, DroneScanAction.Start));
        Assert.Equal(DroneReply.NoneOut, Say(rig, DroneScanAction.Cancel));
    }

    [Fact]
    public void AHullWithNoBayIsToldSo()
    {
        // The shipped Prospector has a bay; the test fleet's small pod has none.
        using var rig = Rig(FleetContent.Value);
        var small = Pick(rig, 20f, clear: true);
        Stand(rig, small, 150f);
        Assert.Equal(DroneReply.NoBay, Ask(rig, small.Rock, out _));
        Assert.Equal(DroneReply.NoBay, Say(rig, DroneScanAction.Start));
        Assert.Equal(DroneReply.NoBay, Say(rig, DroneScanAction.Cancel));
    }

    [Fact]
    public void ASecondRequestAndAStartBeforeReadyAreRefusedWhileDronesAreOnTheirWay()
    {
        using var rig = Rig(Content());
        var small = Pick(rig, 20f, clear: true);
        Stand(rig, small, 150f);
        Assert.Equal(DroneReply.ScanSent, Ask(rig, small.Rock, out var sent));
        Assert.Equal(Needed(small), sent);
        Assert.Equal((DroneScanPhase.Moving, small.Rock, (byte)sent), (rig.Link.Bays.ScanPhase, rig.Link.Bays.ScanRock, rig.Link.Bays.ScanDrones));

        Assert.Equal(DroneReply.ScanBusy, Ask(rig, small.Rock, out _));
        Assert.Equal(DroneReply.ScanBusy, Ask(rig, small.Rock, out _));
        Assert.Equal(DroneReply.ScanNotReady, Say(rig, DroneScanAction.Start));
        Assert.Equal(DroneScanPhase.Moving, rig.Link.Bays.ScanPhase);

        // Commands come at a limited pace: a second inside the same quarter second is dropped, the first stands.
        rig.Link.DroneScan(new DroneScanCommandMessage(Own(rig), DroneScanAction.Start, default));
        var sequence = rig.Link.Bays.ReplySequence;
        rig.Link.DroneScan(new DroneScanCommandMessage(Own(rig), DroneScanAction.Cancel, default));
        rig.Tick();
        Assert.Equal(DroneScanPhase.Moving, rig.Link.Bays.ScanPhase);
        Assert.Equal(sequence + 1, rig.Link.Bays.ReplySequence);
    }

    /// <summary>One finished scan, and what was seen while it ran.</summary>
    private sealed class Outcome
    {
        public DroneScanRecord Record;
        public float MinClearance = float.MaxValue;
        public int MostOut;
        public long ReadyTick, StartTick, DoneTick;
    }

    /// <summary>
    /// Asks for a scan, waits for Ready, orders it and waits for the result and for the drones to be home, checking
    /// on every tick that none of them is inside the rock.
    /// </summary>
    private static Outcome Scan(SaveRig rig, Target target)
    {
        var outcome = new Outcome();
        var owner = Owner(rig);
        var before = rig.Server.DroneScanKnowledge(owner).Length;
        var needed = Needed(target);
        Assert.Equal(DroneReply.ScanSent, Ask(rig, target.Rock, out var sent));
        Assert.Equal(needed, sent);

        void Watch()
        {
            var drones = Drones(rig);
            outcome.MostOut = Math.Max(outcome.MostOut, drones.Count);
            foreach (var drone in drones)
                outcome.MinClearance = Math.Min(outcome.MinClearance, Clearance(rig.Hulls.State(drone).Position, target));
        }

        void Until(Func<bool> done, float seconds, string what)
        {
            for (var i = 0; i < seconds * 60 && !done(); i++) { rig.Tick(); Watch(); }
            Assert.True(done(), what);
        }

        Until(() => rig.Link.Bays.ScanPhase == DroneScanPhase.Ready, Rules.MoveSeconds, "every drone in its place");
        outcome.ReadyTick = rig.WorldTick;
        Assert.Equal(DroneReply.ScanReady, rig.Link.Bays.Reply);
        Assert.Equal((byte)needed, rig.Link.Bays.ScanDrones);
        Assert.Equal((byte)needed, rig.Link.Bays.ScanInPlace);
        Assert.Equal(needed, Drones(rig).Count);

        // Each drone holds one of the places, and the places are all taken.
        var orders = rig.Get<TaskOrder[]>("_orders");
        var count = rig.Get<int>("_orderCount");
        var taken = new HashSet<int>();
        var standoff = DroneScanning.Standoff(Rules, target.Asteroid.Radius);
        for (var i = 0; i < count; i++)
        {
            if (orders[i].Task != OrderTask.Scan) continue;
            Assert.Equal(ScanOrderPhase.InPlace, orders[i].Phase);
            var at = HullRotation.Unrotate(rig.Hulls.State(orders[i].Actor.Index).Position.RelativeTo(target.Centre), target.Asteroid.Rotation);
            for (var slot = 0; slot < needed; slot++)
                if ((DroneScanning.Place(slot, needed, target.Asteroid.Radius, target.Asteroid.Shape, standoff) - at).Length() <= 2f * Rules.PlaceToleranceMetres) taken.Add(slot);
        }

        Assert.Equal(needed, taken.Count);

        // Ready waits for the order, and nothing is found before it is given.
        rig.Tick(60);
        Assert.Equal(DroneScanPhase.Ready, rig.Link.Bays.ScanPhase);
        Assert.Equal(before, rig.Server.DroneScanKnowledge(owner).Length);

        Command(rig, DroneScanAction.Start);
        outcome.StartTick = rig.WorldTick;
        Assert.Equal(DroneReply.ScanRunning, rig.Link.Bays.Reply);
        Assert.Equal(DroneScanPhase.Scanning, rig.Link.Bays.ScanPhase);
        Assert.Equal(Rules.ScanTicks, rig.Link.Bays.ScanCompletes - rig.Link.Bays.ScanStarted);
        Assert.InRange(rig.Link.Bays.ScanStarted, outcome.StartTick - 2, outcome.StartTick);

        // A second order while it runs is answered "running" and changes nothing.
        rig.Tick(20);
        Assert.Equal(DroneReply.ScanRunning, Say(rig, DroneScanAction.Start));
        var completes = rig.Link.Bays.ScanCompletes;
        Until(() => rig.Server.DroneScanKnowledge(owner).Length > before || rig.Link.Bays.ScanPhase == DroneScanPhase.Returning, Rules.ScanSeconds + 5, "the result");
        outcome.DoneTick = rig.WorldTick;
        Assert.InRange(outcome.DoneTick, completes, completes + 4);
        Assert.Equal(DroneReply.ScanDone, rig.Link.Bays.Reply);
        Assert.Equal((byte)needed, rig.Link.Bays.ReplyCount);

        var knowledge = rig.Server.DroneScanKnowledge(owner);
        var at2 = -1;
        for (var i = 0; i < knowledge.Length; i++) if (knowledge[i].Rock == target.Rock) at2 = i;
        Assert.True(at2 >= 0, "the result is kept for the rock");
        outcome.Record = knowledge[at2];

        // The drones come home: out of the rock's way, into their bay, and the bay is full again.
        Until(() => Drones(rig).Count == 0 && rig.Link.Bays.ScanPhase == DroneScanPhase.None, 150, "the drones home");
        rig.Tick(30);
        Assert.Equal(DroneScanPhase.None, rig.Link.Bays.ScanPhase);
        var bays = rig.Link.Bays;
        for (var b = 0; b < bays.Count; b++) Assert.Equal((byte)4, bays.Bays[b].Stocked);
        Assert.Equal(0, rig.Get<int>("_orderCount"));
        return outcome;
    }

    private static void CheckRecord(SaveRig rig, Target target, Outcome outcome)
    {
        var record = outcome.Record;
        Assert.Equal((Owner(rig), target.Rock.Sector, target.Rock.Index), (record.Owner, record.Sector, record.Index));
        Assert.InRange(record.RecordedTick, outcome.StartTick, outcome.DoneTick);
        Assert.True(DroneScanRecordRules.Valid(record));

        // It is what the interior says, to the byte.
        var expected = Record(rig.Server.Galaxy, target.Rock, target.Asteroid, record.Owner, record.RecordedTick);
        Assert.Equal(Bytes(expected), Bytes(record));

        // And it reached the client's snapshot view the way the server sent it.
        Assert.NotEmpty(rig.Link.DroneScanResults);
        var sent = rig.Link.DroneScanResults[^1];
        Assert.Equal(Bytes(record), Bytes(sent));
        using var world = new ClientWorld(rig.Server.Content);
        world.SetPlayer(record.Owner);
        var buffer = new byte[DroneScanResultMessage.MaxWireSize];
        var writer = new NetWriter(buffer);
        DroneScanResultMessage.Write(ref writer, sent);
        var reader = new NetReader(writer.Written[1..]);
        world.OnDroneScanResult(ref reader);
        var view = new DroneScanKnowledgeView();
        world.CopyDroneScans(view);
        var at = view.Find(target.Rock);
        Assert.True(at >= 0);
        Assert.Equal(Bytes(record), Bytes(view.Records[at]));

        // The drones kept clear of the rock all the way there, in place and home.
        Assert.True(outcome.MinClearance > 0f, $"a drone came to {outcome.MinClearance:F2} m from the rock's surface");
        Assert.True(outcome.MostOut >= Needed(target));
    }

    [Fact]
    public void AScanGoesThroughRequestPlaceScanResultAndHome()
    {
        using var rig = Rig(Content());
        var small = Pick(rig, 20f, clear: true);
        Stand(rig, small, 150f);
        Assert.True(Needed(small) <= 8, "two bays hold the drones a small rock's scan takes");
        var outcome = Scan(rig, small);
        CheckRecord(rig, small, outcome);
        Assert.Equal(1, rig.Server.DroneScanKnowledge(Owner(rig)).Length);

        // The same drones can go straight out again, and the newer scan of the rock replaces the first.
        var again = Scan(rig, small);
        Assert.Equal(1, rig.Server.DroneScanKnowledge(Owner(rig)).Length);
        Assert.True(again.Record.RecordedTick > outcome.Record.RecordedTick);
        Assert.Equal(Bytes(Record(rig.Server.Galaxy, small.Rock, small.Asteroid, again.Record.Owner, again.Record.RecordedTick)), Bytes(again.Record));
        Assert.True(again.MinClearance > 0f);
    }

    [Fact]
    public void ACancelSendsTheDronesHomeWithNoResult()
    {
        using var rig = Rig(Content());
        var small = Pick(rig, 20f, clear: true);
        Stand(rig, small, 150f);
        Assert.Equal(DroneReply.ScanSent, Ask(rig, small.Rock, out var sent));
        TickUntil(rig, () => Drones(rig).Count == sent, 5, "the drones out");
        rig.Tick(60);
        Command(rig, DroneScanAction.Cancel);
        Assert.Equal(DroneReply.ScanCancelled, rig.Link.Bays.Reply);
        Assert.Equal(DroneScanPhase.Returning, rig.Link.Bays.ScanPhase);
        var low = float.MaxValue;
        TickUntil(rig, () =>
        {
            foreach (var drone in Drones(rig)) low = Math.Min(low, Clearance(rig.Hulls.State(drone).Position, small));
            return Drones(rig).Count == 0 && rig.Link.Bays.ScanPhase == DroneScanPhase.None;
        }, 150, "the drones home after the cancel");
        Assert.True(low > 0f);
        Assert.Empty(rig.Server.DroneScanKnowledge(Owner(rig)).ToArray());
        Assert.Empty(rig.Link.DroneScanResults);
        Assert.Equal(4, rig.Link.Bays.Bays[0].Stocked);

        // The hull is free to scan again at once.
        rig.Tick(30);
        Assert.Equal(DroneReply.ScanSent, Ask(rig, small.Rock, out _));
    }

    [Fact]
    public void ALostDroneAbortsTheScanAndTheRestComeHome()
    {
        using var rig = Rig(Content());
        var small = Pick(rig, 20f, clear: true);
        Stand(rig, small, 150f);
        Assert.Equal(DroneReply.ScanSent, Ask(rig, small.Rock, out var sent));
        TickUntil(rig, () => Drones(rig).Count == sent, 5, "the drones out");
        rig.Hulls.Destroy(rig.Hulls.HandleAt(Drones(rig)[0]));
        rig.Tick(2);
        Assert.Equal(DroneReply.ScanAborted, rig.Link.Bays.Reply);
        Assert.Equal((byte)sent, rig.Link.Bays.ReplyCount);
        TickUntil(rig, () => Drones(rig).Count == 0 && rig.Link.Bays.ScanPhase == DroneScanPhase.None, 150, "the others home");
        Assert.Empty(rig.Server.DroneScanKnowledge(Owner(rig)).ToArray());
    }

    [Fact]
    public void AScanNobodyOrdersTimesOutAndTheDronesComeHome()
    {
        using var rig = Rig(Content());
        var small = Pick(rig, 20f, clear: true);
        Stand(rig, small, 150f);
        Assert.Equal(DroneReply.ScanSent, Ask(rig, small.Rock, out _));
        TickUntil(rig, () => rig.Link.Bays.ScanPhase == DroneScanPhase.Ready, Rules.MoveSeconds, "ready");
        TickUntil(rig, () => rig.Link.Bays.ScanPhase == DroneScanPhase.Returning, Rules.ReadySeconds + 5, "the wait is given up");
        Assert.Equal(DroneReply.ScanTimedOut, rig.Link.Bays.Reply);
        TickUntil(rig, () => Drones(rig).Count == 0 && rig.Link.Bays.ScanPhase == DroneScanPhase.None, 150, "the drones home");
        Assert.Empty(rig.Server.DroneScanKnowledge(Owner(rig)).ToArray());
    }

    [Fact]
    public void ADroneScanTakesNoMemoryFromLaunchedToDone()
    {
        using var rig = Rig(Content());
        var small = Pick(rig, 20f, clear: true);
        Stand(rig, small, 150f);
        Assert.Equal(DroneReply.ScanSent, Ask(rig, small.Rock, out var sent));
        TickUntil(rig, () => Drones(rig).Count == sent, 10, "the drones out");

        var tickDrones = typeof(ServerHost).GetMethod("TickDrones", Private)!.CreateDelegate<Action>(rig.Server);
        var sendBays = typeof(ServerHost).GetMethod("SendBays", Private)!.CreateDelegate<Action>(rig.Server);
        var sendScans = Bind(rig.Server, "SendDroneScans");
        // Warm every path once: the first call of each binds what it needs.
        tickDrones(); sendBays(); sendScans();

        var drones = new AllocationProbe();
        var bays = new AllocationProbe();
        var scans = new AllocationProbe();
        var started = false;
        var finished = false;
        var phases = new HashSet<DroneScanPhase>();
        for (var i = 0; i < 60 * 150 && !finished; i++)
        {
            while (drones.Measuring()) tickDrones();
            while (bays.Measuring()) sendBays();
            // The test's own link keeps what it is sent in a list, which grows: measure the send where it has nothing new
            // to send (the scan's steady state), and call it plainly where it sends the result.
            if (rig.Link.DroneScanResults.Count == rig.Server.DroneScanKnowledge(Owner(rig)).Length) { while (scans.Measuring()) sendScans(); }
            else sendScans();
            rig.Tick();
            phases.Add(rig.Link.Bays.ScanPhase);
            if (!started && rig.Link.Bays.ScanPhase == DroneScanPhase.Ready)
            {
                started = true;
                rig.Link.DroneScan(new DroneScanCommandMessage(Own(rig), DroneScanAction.Start, default));
            }

            finished = started && rig.Link.Bays.ScanPhase == DroneScanPhase.None && Drones(rig).Count == 0;
        }

        Assert.True(finished, "the scan ran to its end");
        Assert.Contains(DroneScanPhase.Moving, phases);
        Assert.Contains(DroneScanPhase.Ready, phases);
        Assert.Contains(DroneScanPhase.Scanning, phases);
        Assert.Contains(DroneScanPhase.Returning, phases);
        Assert.Equal(1, rig.Server.DroneScanKnowledge(Owner(rig)).Length);
        Assert.Equal((0L, 0L, 0L), (drones.Bytes, bays.Bytes, scans.Bytes));
    }

    /// <summary>A private server method, called with the host's own connection, as a delegate that takes no memory to call.</summary>
    private static Action Bind(ServerHost server, string method)
    {
        var info = typeof(ServerHost).GetMethod(method, Private)!;
        foreach (var connection in (Array)typeof(ServerHost).GetField("_connections", Private)!.GetValue(server)!)
        {
            if (connection!.GetType().GetField("State")!.GetValue(connection)!.ToString() != "Active") continue;
            var call = Expression.Call(Expression.Constant(server), info, Expression.Constant(connection, info.GetParameters()[0].ParameterType));
            return Expression.Lambda<Action>(call).Compile();
        }

        throw new InvalidOperationException("Nobody is let in.");
    }

    // ------------------------------------------------------------------------------------------------------------
    // Saved worlds
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>A world saved with two results of two rocks of one sector, closed, and the bytes of each result.</summary>
    private static (MemoryWorld Folder, GameContent Content, uint Owner, byte[][] Saved) SavedWithScans()
    {
        var content = Content();
        var folder = SaveRig.NewWorld(content);
        using var rig = new SaveRig(folder, content);
        rig.Join();
        rig.Tick(5);
        var owner = Owner(rig);
        var sector = rig.Get<SectorCoord>("_spawnSector");
        var tick = rig.WorldTick;
        var galaxy = rig.Server.Galaxy;
        var rocks = rig.Rocks.Sector(sector);
        var first = RichRock(rig.Rocks, galaxy, sector, 0);
        var second = RichRock(rig.Rocks, galaxy, sector, 1);
        Assert.True(rig.Server.StoreDroneScan(Record(galaxy, new RockRef(sector, first), rocks.Asteroids[first], owner, tick - 2)));
        Assert.True(rig.Server.StoreDroneScan(Record(galaxy, new RockRef(sector, second), rocks.Asteroids[second], owner, tick - 1)));
        var saved = new List<byte[]>();
        foreach (var record in rig.Server.DroneScanKnowledge(owner)) saved.Add(Bytes(record));
        Assert.Equal(2, saved.Count);
        Assert.True(rig.Close().Clean);
        return (folder, content, owner, [.. saved]);
    }

    [Fact]
    public void DroneScanResultsComeBackFromASavedWorldExactlyAndSaveAgainTheSame()
    {
        var (folder, content, owner, saved) = SavedWithScans();
        var snapshot = folder.Decode();
        Assert.Equal(2, snapshot.DroneScanCount);
        Assert.Equal(23, WorldSnapshotCodec.Version);
        var first = folder.Bytes!;

        using (var rig = new SaveRig(folder, content))
        {
            var knowledge = rig.Server.DroneScanKnowledge(owner);
            Assert.Equal(saved.Length, knowledge.Length);
            for (var i = 0; i < saved.Length; i++) Assert.Equal(saved[i], Bytes(knowledge[i]));
            Assert.True(rig.Close().Clean);
        }

        Assert.Equal(2, folder.Decode().DroneScanCount);
        var again = folder.Decode();
        for (var i = 0; i < saved.Length; i++) Assert.Equal(saved[i], Bytes(again.DroneScans[i]));
        Assert.Equal(WorldSnapshotCodec.EncodedSize(again), folder.Bytes!.Length);
        Assert.Equal(first.Length, folder.Bytes.Length);
    }

    [Fact]
    public void ADroneScanResultJoinsTheSaveAfterTheScanAndIsKeptByTheNextSessionToo()
    {
        // A scan run on a real server, saved at close, is there when the world is opened again.
        var content = Content();
        var folder = SaveRig.NewWorld(content);
        uint owner;
        byte[] kept;
        using (var rig = new SaveRig(folder, content))
        {
            rig.Join();
            rig.Tick();
            var small = Pick(rig, 20f, clear: true);
            Stand(rig, small, 150f);
            owner = Owner(rig);
            Assert.Equal(DroneReply.ScanSent, Ask(rig, small.Rock, out _));
            TickUntil(rig, () => rig.Link.Bays.ScanPhase == DroneScanPhase.Ready, Rules.MoveSeconds, "ready");
            Command(rig, DroneScanAction.Start);
            TickUntil(rig, () => rig.Server.DroneScanKnowledge(owner).Length == 1, 10, "the result");
            kept = Bytes(rig.Server.DroneScanKnowledge(owner)[0]);
            // Recall the rest so the world closes with nothing out.
            TickUntil(rig, () => Drones(rig).Count == 0 && rig.Link.Bays.ScanPhase == DroneScanPhase.None, 150, "drones home");
            Assert.True(rig.Close().Clean);
        }

        using var again = new SaveRig(folder, content);
        Assert.Equal(kept, Bytes(again.Server.DroneScanKnowledge(owner)[0]));
    }

    [Fact]
    public void TheSaveCodecRefusesDroneScansThatCannotBe()
    {
        var (folder, _, owner, _) = SavedWithScans();
        var snapshot = folder.Decode();
        bool Takes(WorldSnapshot world) => WorldSnapshotCodec.TryDecode(WorldSnapshotCodec.Encode(world), SaveLimits.Default, out _, out _);
        Assert.True(Takes(snapshot));
        var first = snapshot.DroneScans[0];
        var second = snapshot.DroneScans[1];

        // One rock twice for one owner.
        snapshot.DroneScans[1].Sector = first.Sector;
        snapshot.DroneScans[1].Index = first.Index;
        Assert.False(Takes(snapshot));
        snapshot.DroneScans[1] = second;

        // An owner the world does not have.
        snapshot.DroneScans[0].Owner = uint.MaxValue;
        Assert.False(Takes(snapshot));
        snapshot.DroneScans[0] = first;
        snapshot.DroneScans[0].Owner = owner + 1000;
        Assert.False(Takes(snapshot));
        snapshot.DroneScans[0] = first;

        // A result from the future, or from this very tick.
        snapshot.DroneScans[0].RecordedTick = snapshot.NextWorldTick;
        Assert.False(Takes(snapshot));
        snapshot.DroneScans[0].RecordedTick = snapshot.NextWorldTick + 5;
        Assert.False(Takes(snapshot));
        snapshot.DroneScans[0].RecordedTick = snapshot.NextWorldTick - 1;
        Assert.True(Takes(snapshot));
        snapshot.DroneScans[0] = first;

        // A record that breaks the structure does not even encode.
        snapshot.DroneScans[0].Radius = 0;
        Assert.Throws<InvalidDataException>(() => WorldSnapshotCodec.Encode(snapshot));
        snapshot.DroneScans[0] = first;

        // Bytes of a valid save cut short or with a corrupted record are refused.
        var bytes = WorldSnapshotCodec.Encode(snapshot);
        Assert.True(WorldSnapshotCodec.TryDecode(bytes, SaveLimits.Default, out _, out _));
        Assert.False(WorldSnapshotCodec.TryDecode(bytes[..^40], SaveLimits.Default, out _, out _));
        Assert.True(Takes(snapshot));
    }

    [Fact]
    public void TheServerRefusesASaveWhoseDroneScanNamesARockTheWorldDoesNotHaveOrResourcesItDoesNotKnow()
    {
        var (folder, content, _, _) = SavedWithScans();
        var sector = folder.Decode().DroneScans[0].Sector;
        var count = new SectorContent();
        content.Galaxy.WithSeed(ServerConfig.DefaultSeed).Generate(sector, count);
        Assert.True(count.AsteroidCount < ushort.MaxValue);

        void Refuses(Action<WorldSnapshot> change, string what)
        {
            try
            {
                new SaveRig(folder, content, edit: world => { change(world); return world; }).Dispose();
            }
            catch (InvalidDataException)
            {
                return;
            }

            Assert.Fail($"the server took a save with {what}");
        }

        Refuses(world => world.DroneScans[0].Index = ushort.MaxValue, "a rock past the sector's last");
        Refuses(world => world.DroneScans[1].Index = count.AsteroidCount, "the first rock past the end");
        Refuses(world => world.DroneScans[0].Resource = 31, "a resource the galaxy does not have");
        Refuses(world => { world.DroneScans[0].Map.Veins[0].Resource = 31; world.DroneScans[0].Map.VeinCount = Math.Max((byte)1, world.DroneScans[0].Map.VeinCount); }, "a vein of an unknown ore");
        Refuses(world => world.DroneScans[0].Owner = 999_999, "an owner the world has not got");
        Refuses(world => world.DroneScans[0].RecordedTick = world.NextWorldTick, "a result of the save's own moment");
        Refuses(world => { world.DroneScans[1].Sector = world.DroneScans[0].Sector; world.DroneScans[1].Index = world.DroneScans[0].Index; }, "a rock twice");
        Refuses(world => world.DroneScans[0].Radius = 0, "a rock of no size");

        // And the untouched save still opens.
        new SaveRig(folder, content).Dispose();
    }
}
