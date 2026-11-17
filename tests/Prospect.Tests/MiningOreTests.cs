using System.Numerics;
using Prospect.Game.Protocol;
using Prospect.Game.Server;
using Prospect.Game.World;
using Xunit;

namespace Prospect.Tests;

/// <summary>The ore a mining laser frees: what a chunk is, where it appears and how it moves, and how the pool holds them.</summary>
public sealed class MiningOreTests
{
    private static readonly Vector3 Up = Vector3.UnitY;
    private static readonly GameContent Content = MiningFixtures.Content;

    /// <summary>Sweeps the aim along the surface, as a pilot moves the pointer, until <paramref name="done"/> or the ticks run out.</summary>
    private static bool Sweep(MiningRig rig, Func<bool> done, int ticks = 3000, ushort groups = 3)
    {
        for (var t = 0; t < ticks; t++)
        {
            var angle = (t % 400) * 0.0012f;
            rig.Aim(0, groups, Vector3.Normalize(Up + Vector3.UnitZ * angle));
            rig.Step();
            if (done()) return true;
        }

        return false;
    }

    private static OreChunk Make(int i, Double3 at, long born = 0) => new()
    {
        Position = at,
        Orientation = Quaternion.Identity,
        Velocity = new Vector3(0, 0, 1),
        Resource = 0,
        Grade = 0.5f,
        Mass = 100,
        Born = born + i,
    };

    // ---------------------------------------------------------------- what a laser frees

    [Fact]
    public void CuttingAFreesAChunkOfTheRocksOreNearTheCutAndThrownBackFromTheRock()
    {
        using var rig = new MiningRig();
        var (rock, asteroid) = MiningFixtures.FindRock(rig.Rocks);
        rig.Place(0, rock, Up, 25);
        var hull = rig.Hulls.State(rig.Slot()).Position;
        Assert.Empty(rig.Server.OreChunks.Items.ToArray());

        Assert.True(Sweep(rig, () => rig.Server.OreChunks.Items.Length > 0), "cutting a rock for fifty seconds frees some ore");
        var chunk = rig.Server.OreChunks.Items[0];
        var rules = rig.Content.Mining;

        // Its ore is one the rock holds, its share a proper fraction, and what the laser gave it at least what the content asks.
        var ores = new[] { asteroid.Resource, asteroid.Mix1, asteroid.Mix2 }.Where(r => r >= 0).ToArray();
        Assert.Contains(chunk.Resource, ores);
        Assert.InRange(chunk.Grade, 1e-4f, 1f);
        Assert.True(chunk.Mass > 0);
        Assert.True(chunk.OreKg >= rules.ChunkOreKg * 0.999f, $"{chunk.OreKg} kg of ore in a chunk that is made at {rules.ChunkOreKg}");
        Assert.Equal(1u, chunk.Id);
        Assert.True(chunk.Born >= 0 && chunk.Age <= 1);
        Assert.True(OreChunks.Valid(rig.Content, chunk));
        Assert.Equal(1f, chunk.Orientation.Length(), 3);

        // It breaks off where the beam meets the rock: on the surface above the hull, outside the rock, and it moves away from it.
        var centre = rig.Content.Galaxy.CenterOf(rock.Sector) + asteroid.LocalPosition;
        var surface = hull + Up * 25;
        Assert.True(chunk.Position.RelativeTo(surface).Length() < 8, "the chunk is near the cut");
        var outward = Vector3.Normalize(chunk.Position.RelativeTo(centre));
        Assert.True(Vector3.Dot(Vector3.Normalize(chunk.Velocity), outward) > 0.3f, "thrown away from the rock");
        Assert.True(chunk.Velocity.Length() > rules.ChunkSpeed * 0.5f);
        Assert.True(chunk.AngularVelocity.Length() > 0);
        Assert.Equal(rules.ChunkSpin, chunk.AngularVelocity.Length(), 2);

        // The client that can see it holds the same chunk.
        rig.Until(() => rig.Snap().OreChunkCount > 0);
        var seen = rig.Snap().OreChunks.AsSpan(0, rig.Snap().OreChunkCount).ToArray().First(c => c.Id == 1);
        Assert.Equal((chunk.Resource, chunk.Mass, chunk.Grade), (seen.Resource, seen.Mass, seen.Grade));

        // Over more cutting, every chunk is of an ore the rock holds and no richer than the interior's richest spot: the
        // ore a laser frees comes from the rock's interior at the cut and from nowhere else.
        Sweep(rig, () => false, 600);
        var all = rig.Server.OreChunks.Items.ToArray();
        Assert.True(all.Length > 3, "more cutting breaks more chunks");
        var richest = rig.Content.Galaxy.Interiors!.MaxLocalOre;
        Assert.All(all, c => Assert.Contains(c.Resource, ores));
        Assert.All(all, c => Assert.True(c.Grade <= richest + 1e-3f, $"a chunk of grade {c.Grade} from a rock whose richest spot is {richest}"));
        Assert.Equal(all.Length, all.Select(c => c.Id).Distinct().Count());
        Assert.Equal(all.OrderBy(c => c.Born).Select(c => c.Id), all.Select(c => c.Id));
    }

    [Fact]
    public void TheOreAddsUpFromEachScrapeSoABiggerChunkNeedsMoreCuttingAndEveryChunkHoldsAtLeastTheMark()
    {
        int EditsUntilFirstChunk(GameContent content, out OreChunk chunk)
        {
            using var rig = new MiningRig(content);
            var (rock, _) = MiningFixtures.FindRock(rig.Rocks);
            rig.Place(0, rock, Up, 25);
            Assert.True(Sweep(rig, () => rig.Server.OreChunks.Items.Length > 0, 6000), "a chunk breaks off");
            chunk = rig.Server.OreChunks.Items[0];
            return rig.Server.RockEdits.Log.Count;
        }

        var small = EditsUntilFirstChunk(Content, out var first);
        var large = EditsUntilFirstChunk(MiningFixtures.Make(other: copy => copy.Replace("rules/mining.json", "\"oreKg\": 40", "\"oreKg\": 600")), out var second);
        Assert.True(first.OreKg >= 40 * 0.999f);
        Assert.True(second.OreKg >= 600 * 0.999f, $"{second.OreKg} kg in a chunk of a mark of 600");
        Assert.True(large > small, $"{large} scrapes for a mark of 600 and {small} for one of 40");
        Assert.True(second.Mass > first.Mass);
    }

    [Fact]
    public void AMarkNoLaserReachesMakesScrapesAndNoChunks()
    {
        // A mark no laser reaches within the test makes scrapes and no chunk: the scrape count is the laser's, the chunks the ore's.
        using var rig = new MiningRig(MiningFixtures.Make(other: copy => copy.Replace("rules/mining.json", "\"oreKg\": 40", "\"oreKg\": 1000000")));
        var (rock, _) = MiningFixtures.FindRock(rig.Rocks);
        rig.Place(0, rock, Up, 25);
        Assert.False(Sweep(rig, () => rig.Server.OreChunks.Items.Length > 0, 1500));
        Assert.True(rig.Edits(rock) >= 20);
        Assert.Equal(1u, rig.Server.OreChunks.NextId);
    }

    [Fact]
    public void ChunksAreSentToTheClientsThatCoverThemAndToNoOneElse()
    {
        using var rig = new MiningRig();
        var (rock, _) = MiningFixtures.FindRock(rig.Rocks);
        rig.Place(0, rock, Up, 25);

        // The other player's whole fleet is a long way off, out of any sensor of theirs that could cover the rock.
        var owner = rig.Hulls.Info(rig.Slot(1)).Owner;
        var stay = new List<(int Slot, Double3 Position)>();
        for (var i = 0; i < rig.Hulls.Slots; i++)
            if (rig.Hulls.IsAliveAt(i) && rig.Hulls.Info(i).Owner == owner)
            {
                stay.Add((i, rig.Hulls.State(i).Position));
                rig.Hulls.State(i).Position += new Double3(rig.Content.Galaxy.SectorSize * 3, 0, 0);
            }

        rig.Step(30);
        Assert.True(Sweep(rig, () => rig.Server.OreChunks.Items.Length > 0));
        rig.Step(30);
        Assert.True(rig.Snap(0).OreChunkCount > 0, "the owner's sensors cover it");
        Assert.Equal(0, rig.Snap(1).OreChunkCount);
        Assert.Equal(0, rig.Snap(1).LaserBeamCount);
        Assert.True(rig.Snap(0).LaserBeamCount > 0);

        // Bring the other fleet to the rock: now its sensors cover the chunk and the beam.
        foreach (var (slot, position) in stay)
            rig.Hulls.State(slot).Position = rig.Hulls.State(rig.Slot()).Position + new Double3(120, 0, 0) + (position - stay[0].Position);
        rig.Until(() => rig.Snap(1).OreChunkCount > 0 && rig.Snap(1).LaserBeamCount > 0);
        Assert.True(rig.Snap(0).OreChunkCount > 0);
    }

    // ---------------------------------------------------------------- the pool

    [Fact]
    public void AChunkPoolHoldsTheSectorCapAndTheOldestOfAFullSectorGoesFirstWithIdsNeverReused()
    {
        var chunks = new OreChunks();
        var rules = Content.Mining;
        var home = Content.Galaxy.CenterOf(default);
        var ids = new List<uint>();
        for (var i = 0; i < rules.ChunksPerSector + 5; i++)
        {
            Assert.True(chunks.Add(Content, Make(i, home), out var id));
            ids.Add(id);
        }

        Assert.Equal(rules.ChunksPerSector, chunks.Items.Length);
        Assert.Equal(Enumerable.Range(1, rules.ChunksPerSector + 5).Select(i => (uint)i), ids);
        Assert.Equal(-1, chunks.Find(1));
        Assert.Equal(-1, chunks.Find(5));
        Assert.True(chunks.Find(6) >= 0);
        Assert.Equal(ids[^1] + 1, chunks.NextId);
        Assert.Equal(6u, chunks.Items[0].Id);
        Assert.Equal(ids.Last(), chunks.Items[^1].Id);

        // Another sector is its own count.
        var elsewhere = home + new Double3(Content.Galaxy.SectorSize, 0, 0);
        Assert.True(chunks.Add(Content, Make(100, elsewhere), out _));
        Assert.Equal(rules.ChunksPerSector + 1, chunks.Items.Length);
        Assert.True(chunks.Find(6) >= 0);
    }

    [Fact]
    public void TheWholePoolIsCappedAndTheOldestOverallGoesFirstWhateverItsSector()
    {
        var chunks = new OreChunks();
        var home = Content.Galaxy.CenterOf(default);
        var size = Content.Galaxy.SectorSize;
        for (var i = 0; i < 320; i++)
            Assert.True(chunks.Add(Content, Make(i, home + new Double3(size * (i % 5), 0, 0)), out _));
        Assert.Equal(OreChunks.Capacity, chunks.Items.Length);
        Assert.Equal(65u, chunks.Items[0].Id);
        Assert.Equal(-1, chunks.Find(64));
        Assert.Equal(321u, chunks.NextId);
        var born = chunks.Items.ToArray().Select(c => c.Born).ToArray();
        Assert.Equal(born.OrderBy(b => b), born);
    }

    [Fact]
    public void AChunkThatIsNotPhysicallySoundOrHoldsNoOreIsNeverAdmitted()
    {
        var chunks = new OreChunks();
        var at = Content.Galaxy.CenterOf(default);
        Assert.True(chunks.Add(Content, Make(0, at), out _));
        var bad = new Func<OreChunk, OreChunk>[]
        {
            c => { c.Mass = 0; return c; },
            c => { c.Mass = float.NaN; return c; },
            c => { c.Mass = 2e7f; return c; },
            c => { c.Grade = 0; return c; },
            c => { c.Grade = 1.5f; return c; },
            c => { c.Resource = -1; return c; },
            c => { c.Resource = Content.Galaxy.Resources.Length; return c; },
            c => { c.Velocity = new Vector3(float.NaN, 0, 0); return c; },
            c => { c.Position = new Double3(double.PositiveInfinity, 0, 0); return c; },
            c => { c.Orientation = new Quaternion(1, 2, 3, 4); return c; },
            c => { c.Born = -1; return c; },
            c => { c.Age = -1; return c; },
        };
        foreach (var change in bad)
        {
            Assert.False(chunks.Add(Content, change(Make(1, at)), out var id));
            Assert.Equal(0u, id);
        }

        Assert.Equal(1, chunks.Items.Length);
        Assert.Equal(2u, chunks.NextId);
    }

    [Fact]
    public void RestoringKeepsIdentitiesAndTheCounterAndACrowdedSectorLosesItsOldestOnTheNextCheck()
    {
        var chunks = new OreChunks();
        var home = Content.Galaxy.CenterOf(default);
        var saved = Enumerable.Range(0, 70).Select(i => { var c = Make(i, home); c.Id = (uint)(i * 3 + 7); return c; }).ToArray();
        chunks.Restore(saved, 999);
        Assert.Equal(70, chunks.Items.Length);
        Assert.Equal(999u, chunks.NextId);
        Assert.Equal(7u, chunks.Items[0].Id);

        chunks.EnforceCaps(Content);
        Assert.Equal(Content.Mining.ChunksPerSector, chunks.Items.Length);
        Assert.Equal(-1, chunks.Find(7));
        Assert.True(chunks.Find(saved[^1].Id) >= 0);
        Assert.True(chunks.Add(Content, Make(200, home + new Double3(Content.Galaxy.SectorSize * 2, 0, 0)), out var id));
        Assert.Equal(999u, id);
        Assert.Throws<InvalidDataException>(() => chunks.Restore(new OreChunk[OreChunks.Capacity + 1], 5));
    }

    [Fact]
    public void ChunksDriftAndSpinAndAgeOnlyWhileNoPlayerIsNearAndEndAtTheirLifetime()
    {
        var chunks = new OreChunks();
        var home = Content.Galaxy.CenterOf(default);
        var moving = Make(0, home);
        moving.Velocity = new Vector3(3, 0, 0);
        moving.AngularVelocity = new Vector3(0, 1, 0);
        Assert.True(chunks.Add(Content, moving, out _));
        for (var i = 0; i < 60; i++) chunks.Drift();
        Assert.Equal(3.0, chunks.Items[0].Position.X - home.X, 4);
        Assert.Equal(1f, chunks.Items[0].Orientation.Length(), 4);
        var turned = chunks.Items[0].Orientation;
        Assert.Equal(1f, 2 * MathF.Asin(MathF.Abs(turned.Y)), 3);

        // A player within the radius keeps its clock still; one farther away lets it run.
        Span<Double3> near = [home + new Double3(3, 0, 0)];
        Span<Double3> far = [home + new Double3(1e7, 0, 0)];
        for (var i = 0; i < 100; i++) chunks.Cleanup(1000, near, 5000);
        Assert.Equal(0, chunks.Items[0].Age);
        for (var i = 0; i < 100; i++) chunks.Cleanup(1000, far, 5000);
        Assert.Equal(100, chunks.Items[0].Age);
        for (var i = 0; i < 100; i++) chunks.Cleanup(1000, [], 5000);
        Assert.Equal(200, chunks.Items[0].Age);
        for (var i = 0; i < 799; i++) chunks.Cleanup(1000, [], 5000);
        Assert.Equal(1, chunks.Items.Length);
        chunks.Cleanup(1000, [], 5000);
        Assert.Equal(0, chunks.Items.Length);

        // A lifetime of zero means never.
        Assert.True(chunks.Add(Content, Make(1, home), out _));
        for (var i = 0; i < 5000; i++) chunks.Cleanup(0, [], 5000);
        Assert.Equal(1, chunks.Items.Length);
        Assert.Equal(5000, chunks.Items[0].Age);
    }

    // ---------------------------------------------------------------- how a loose chunk moves

    [Fact]
    public void AChunkBouncesOffARockAndLosesSomeOfItsSpeed()
    {
        using var rig = new MiningRig();
        var (rock, asteroid) = MiningFixtures.FindRock(rig.Rocks);
        var centre = rig.Content.Galaxy.CenterOf(rock.Sector) + asteroid.LocalPosition;
        var surface = MiningFixtures.Surface(asteroid, Up);
        var chunk = Make(0, centre + Up * surface);
        chunk.Mass = 1000;
        chunk.Velocity = new Vector3(0, -4, 0);
        Assert.True(rig.Server.OreChunks.Add(rig.Content, chunk, out var id));
        rig.Step();
        var after = rig.Server.OreChunks.Items[rig.Server.OreChunks.Find(id)];
        Assert.True(after.Velocity.Y > 0, $"it bounces back up, at {after.Velocity}");
        Assert.True(after.Velocity.Length() < 4, "and keeps less than it came with");
        Assert.True(after.Position.RelativeTo(centre).Length() >= surface - 0.3, "and is not left inside the rock");
    }

    [Fact]
    public void AChunkBouncesOffTheHullsBlocksPushingTheHullALittleAndDealsNoDamage()
    {
        using var rig = new MiningRig();
        var (rock, _) = MiningFixtures.FindRock(rig.Rocks);
        rig.Place(0, rock, Up, 200);
        var slot = rig.Slot();
        var design = rig.DesignOf();
        var plate = Enumerable.Range(0, design.BlockCount).First(b => design.Boxes[b] is { At: { X: 2, Y: 0, Z: 0 } });
        var half = OreChunks.Half(rig.Content.Mining, 500);
        var hull = rig.Hulls.State(slot);
        var chunk = Make(0, hull.Position + design.BlockOffsets[plate] + new Vector3(0, design.BlockHalfExtents[plate].Y + half - 0.05f, 0));
        chunk.Mass = 500;
        chunk.Velocity = hull.Velocity + new Vector3(0, -3, 0);
        Assert.True(rig.Server.OreChunks.Add(rig.Content, chunk, out var id));

        var blocks = rig.Hulls.Info(slot).BlockState;
        var points = rig.Hulls.BlockStates.HitTotal(blocks);
        var shield = rig.Hulls.State(slot).Shield;
        var hullPoints = rig.Hulls.State(slot).Hull;
        var speed = rig.Hulls.State(slot).Velocity;
        rig.Step();
        var after = rig.Server.OreChunks.Items[rig.Server.OreChunks.Find(id)];
        Assert.True(after.Velocity.Y > 0, $"the chunk is turned back, now at {after.Velocity}");
        Assert.True(rig.Hulls.State(slot).Velocity.Y < speed.Y, "and pushes the hull the other way");
        Assert.True(MathF.Abs(rig.Hulls.State(slot).Velocity.Y - speed.Y) < 0.5f, "though only a little: it weighs half a tonne");
        Assert.Equal(points, rig.Hulls.BlockStates.HitTotal(blocks));
        Assert.Equal(hullPoints, rig.Hulls.State(slot).Hull);
        Assert.True(rig.Hulls.State(slot).Shield >= shield);
    }

    [Fact]
    public void AChunkThatIsLeftAloneLastsUntilItsLifetimeIsOverThroughTheServer()
    {
        using var rig = new MiningRig(MiningFixtures.Make(other: copy => copy.Replace("rules/mining.json", "\"lifetimeMinutes\": 30", "\"lifetimeMinutes\": 1")));
        Assert.Equal(60 * 60, rig.Content.Mining.ChunkLifetimeTicks);
        var chunk = Make(0, rig.Hulls.State(rig.Slot()).Position + new Double3(0, 0, 50_000));
        chunk.Velocity = Vector3.Zero;
        Assert.True(rig.Server.OreChunks.Add(rig.Content, chunk, out var id));
        rig.Step(3000);
        Assert.True(rig.Server.OreChunks.Find(id) >= 0);
        Assert.True(rig.Server.OreChunks.Items[0].Age >= 2999);
        rig.Step(700);
        Assert.Equal(-1, rig.Server.OreChunks.Find(id));
    }
}
