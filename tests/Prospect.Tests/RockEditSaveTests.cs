using System.Buffers.Binary;
using Prospect.Game.Persistence;
using Prospect.Game.World;
using Prospect.Game.World.RockEditing;
using Xunit;

namespace Prospect.Tests;

/// <summary>What a save holds of rock edits: the codec's structure checks, a server's capture and load, and the load rule.</summary>
public sealed class RockEditSaveTests
{
    private static readonly GameContent Content = GameContent.Load();

    // Bytes of an edited rock's record before its edits: sector, index, count.
    private const int RecordBytes = 12 + 4 + 4;

    /// <summary>A world saved after two rocks were scraped, three times in all, and what it should hold.</summary>
    private sealed record Saved(byte[] Bytes, RockRef A, RockRef B, RockScrape[] EditsA, RockScrape[] EditsB, ulong HashA, ulong HashB, int Bricks)
    {
        public MemoryWorld Folder() => new() { Bytes = Bytes, Generation = 7 };
    }

    /// <summary>
    /// Scrapable rocks of a sector with at least this radius, largest first: a groove needs room on the surface. Shared
    /// with the tests that scrape through a server.
    /// </summary>
    internal static List<(RockRef Rock, Asteroid Asteroid)> ScrapableRocks(RockField rocks, SectorCoord coord, float minRadius)
    {
        var sector = rocks.Sector(coord);
        var found = new List<(RockRef, Asteroid)>();
        for (var i = 0; i < sector.AsteroidCount; i++)
            if (RockEditRules.Scrapable(sector.Asteroids[i].Shape) && sector.Asteroids[i].Radius >= minRadius)
                found.Add((new RockRef(coord, i), sector.Asteroids[i]));
        return found;
    }

    /// <summary>Two scrapable rocks of one sector near home.</summary>
    private static ((RockRef Rock, Asteroid Asteroid) First, (RockRef Rock, Asteroid Asteroid) Second) TwoRocks(RockField rocks)
    {
        for (var x = -6; x <= 6; x++)
            for (var z = -6; z <= 6; z++)
            {
                var found = ScrapableRocks(rocks, new SectorCoord(x, 0, z), 10f);
                if (found.Count >= 2) return (found[0], found[1]);
            }

        throw new InvalidOperationException("No two rocks of 10 m near home.");
    }

    private static Saved Make()
    {
        var folder = SaveRig.NewWorld(Content);
        using var rig = new SaveRig(folder, Content);
        rig.Join();
        var (first, second) = TwoRocks(rig.Rocks);
        var groove = RockEditTests.Groove(first.Asteroid.Radius, first.Asteroid.Shape);

        // One small pit just under the surface of the other rock, on the line a groove of its own would follow.
        var at = RockEditTests.Groove(second.Asteroid.Radius, second.Asteroid.Shape)[40].A;
        var sphere = RockScrape.From(at, at, 0.5f);

        // One rock is scraped in two strokes with another's between: the saved list holds each rock's edits together, in order.
        Assert.Equal(RockEditRefusal.None, rig.Server.ScrapeRock(first.Rock, groove.AsSpan(0, 16)));
        Assert.Equal(RockEditRefusal.None, rig.Server.ScrapeRock(second.Rock, sphere));
        Assert.Equal(RockEditRefusal.None, rig.Server.ScrapeRock(first.Rock, groove.AsSpan(16, 16)));
        var log = rig.Server.RockEdits.Log;
        var bricks = rig.Server.RockEdits.Bricks;
        Assert.Equal((2, 33), (log.RockCount, log.Count));
        var slotA = log.SlotOf(first.Rock);
        var slotB = log.SlotOf(second.Rock);
        var used = bricks.Used;
        var saved = new Saved([], first.Rock, second.Rock, groove[..32], [sphere], bricks.Hash(slotA), bricks.Hash(slotB), used);
        Assert.True(rig.Close().Clean);
        return saved with { Bytes = folder.Bytes! };
    }

    private static readonly Lazy<Saved> Shared = new(Make);

    private static RockScrape[] EditsOf(RockEditLog log, RockRef rock)
    {
        var slot = log.SlotOf(rock);
        var edits = new List<RockScrape>();
        for (var index = log.FirstOf(slot); index >= 0; index = log.NextOf(index)) edits.Add(log.EditAt(index));
        return [.. edits];
    }

    /// <summary>Bytes of a rock's record and edits as the codec writes them: the record, then each edit's own size.</summary>
    private static int RockBytes(ReadOnlySpan<RockScrape> edits)
    {
        var size = RecordBytes;
        for (var i = 0; i < edits.Length; i++) size += RockEditCodec.Size(edits[i], i > 0 ? edits[i - 1] : default, i > 0);
        return size;
    }

    [Fact]
    public void ASnapshotWithRockEditsEncodesAndDecodesToTheSameEdits()
    {
        var saved = Shared.Value;
        Assert.True(WorldSnapshotCodec.TryDecode(saved.Bytes, SaveLimits.Default, out var snapshot, out var error), error);
        Assert.Equal(2, snapshot!.EditedRockCount);
        Assert.Equal(33, snapshot.RockEditCount);
        Assert.Equal(new[] { saved.A, saved.B }, snapshot.EditedRocks[..2]);
        Assert.Equal(new[] { 32, 1 }, snapshot.EditedRockEdits[..2]);
        Assert.Equal([.. saved.EditsA, .. saved.EditsB], snapshot.RockEdits[..33]);

        // Encoded again, the snapshot is the same bytes and decodes to the same edits.
        var again = WorldSnapshotCodec.Encode(snapshot);
        Assert.Equal(WorldSnapshotCodec.EncodedSize(snapshot), again.Length);
        Assert.Equal(saved.Bytes.Length, again.Length);
        Assert.True(WorldSnapshotCodec.TryDecode(again, SaveLimits.Default, out var copy, out error), error);
        Assert.Equal(snapshot.EditedRocks, copy!.EditedRocks);
        Assert.Equal(snapshot.EditedRockEdits, copy.EditedRockEdits);
        Assert.Equal(snapshot.RockEdits, copy.RockEdits);
        Assert.Equal((2, 33), (copy.EditedRockCount, copy.RockEditCount));

        // The edits take the short form where one starts at the end of the one before, as the wire does: two strokes of
        // sixteen in the first rock, and one scrape in the second.
        var section = RockBytes(saved.EditsA) + RockBytes(saved.EditsB);
        Assert.Equal(RecordBytes + 2 * 21 + 30 * 9 + RecordBytes + 21, section);
    }

    [Fact]
    public void ARockNamedTwiceACountThatOverrunsAndACorruptedEditAreRefused()
    {
        var saved = Shared.Value;
        var bytes = saved.Bytes;
        var sectionBytes = RockBytes(saved.EditsA) + RockBytes(saved.EditsB);
        var section = bytes.Length - WorldSnapshotCodec.MiningHeaderBytes - sectionBytes;
        var secondRecord = section + RockBytes(saved.EditsA);
        Assert.True(WorldSnapshotCodec.TryDecode(bytes, SaveLimits.Default, out _, out _));

        bool Decodes(byte[] changed) => WorldSnapshotCodec.TryDecode(changed, SaveLimits.Default, out _, out _);
        byte[] Changed(Action<byte[]> change)
        {
            var copy = (byte[])bytes.Clone();
            change(copy);
            return copy;
        }

        // The records are where the arithmetic says: the first rock's index and count.
        Assert.Equal(saved.A.Index, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(section + 12)));
        Assert.Equal(32, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(section + 16)));
        Assert.Equal(saved.B.Index, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(secondRecord + 12)));

        // A rock named twice: the second record says the first's sector and index.
        Assert.False(Decodes(Changed(copy => bytes.AsSpan(section, 16).CopyTo(copy.AsSpan(secondRecord, 16)))));

        // A count that overruns the edits there are, or leaves the next rock none, or is zero or negative.
        foreach (var count in new[] { 34, 33, 0, -1, int.MaxValue })
            Assert.False(Decodes(Changed(copy => BinaryPrimitives.WriteInt32LittleEndian(copy.AsSpan(section + 16), count))), $"a count of {count} was read");
        Assert.False(Decodes(Changed(copy => BinaryPrimitives.WriteInt32LittleEndian(copy.AsSpan(secondRecord + 16), 2))));
        Assert.False(Decodes(Changed(copy => BinaryPrimitives.WriteInt32LittleEndian(copy.AsSpan(secondRecord + 16), 0))));

        // A negative index is no asteroid.
        Assert.False(Decodes(Changed(copy => BinaryPrimitives.WriteInt32LittleEndian(copy.AsSpan(section + 12), -1))));

        // The kind of an edit is its first byte: 0 and 3 and the rest are not edits, and a continuation cannot start a rock.
        foreach (var kind in new byte[] { 0, 3, 9, 255, 2 })
            Assert.False(Decodes(Changed(copy => copy[section + RecordBytes] = kind)), $"kind {kind} was read as a rock's first edit");

        // A later edit, short form for a full one: the bytes after it no longer line up and the save is damaged.
        Assert.Equal(2, bytes[section + RecordBytes + 21]);
        Assert.False(Decodes(Changed(copy => copy[section + RecordBytes + 21] = 1)));
        Assert.False(Decodes(Changed(copy => copy[section + RecordBytes + 21] = 3)));

        // A radius of zero, in the first edit.
        Assert.False(Decodes(Changed(copy =>
        {
            copy[section + RecordBytes + 19] = 0;
            copy[section + RecordBytes + 20] = 0;
        })));

        // Bytes cut anywhere in the rock edits, or left over after them.
        for (var length = section; length < bytes.Length; length++)
            Assert.False(WorldSnapshotCodec.TryDecode(bytes.AsSpan(0, length), SaveLimits.Default, out _, out _), $"a save cut at {length} was read");
        Assert.False(Decodes([.. bytes, 0]));
    }

    [Fact]
    public void ASaveWithMoreRockEditsThanTheLimitsAllowIsRefusedBeforeAnythingIsTaken()
    {
        var bytes = Shared.Value.Bytes;
        Assert.True(WorldSnapshotCodec.TryDecode(bytes, SaveLimits.Default with { MaxEditedRocks = 2, MaxRockEdits = 33 }, out _, out _));
        Assert.False(WorldSnapshotCodec.TryDecode(bytes, SaveLimits.Default with { MaxEditedRocks = 1 }, out _, out var error));
        Assert.Contains("more than a world may hold", error);
        Assert.False(WorldSnapshotCodec.TryDecode(bytes, SaveLimits.Default with { MaxRockEdits = 32 }, out _, out _));
    }

    [Fact]
    public void ScrapingARealRockThroughTheServerSurvivesACloseAndALoadWithTheSameBricks()
    {
        var saved = Shared.Value;
        var folder = saved.Folder();
        using (var loaded = new SaveRig(folder, Content))
        {
            var edits = loaded.Server.RockEdits;
            Assert.Equal((2, 33), (edits.Log.RockCount, edits.Log.Count));

            // Each rock's edits are back in the order they were made, and the bricks are the ones worked out before the close.
            Assert.Equal(saved.EditsA, EditsOf(edits.Log, saved.A));
            Assert.Equal(saved.EditsB, EditsOf(edits.Log, saved.B));
            Assert.Equal(saved.HashA, edits.Bricks.Hash(edits.Log.SlotOf(saved.A)));
            Assert.Equal(saved.HashB, edits.Bricks.Hash(edits.Log.SlotOf(saved.B)));
            Assert.Equal(saved.Bricks, edits.Bricks.Used);
            Assert.Equal(32, edits.Bricks.Applied(edits.Log.SlotOf(saved.A)));
            Assert.True(edits.CutOf(saved.A).Any);
            Assert.False(edits.Bricks.Overflowed);

            // And the world goes on being edited: another scrape of the first rock is taken and the list grows.
            var rock = loaded.Rocks.Sector(saved.A.Sector).Asteroids[saved.A.Index];
            var more = RockEditTests.Groove(rock.Radius, rock.Shape)[32];
            Assert.Equal(RockEditRefusal.None, loaded.Server.ScrapeRock(saved.A, more));
            Assert.Equal(33, edits.Log.EditsOf(edits.Log.SlotOf(saved.A)));
            Assert.NotEqual(saved.HashA, edits.Bricks.Hash(edits.Log.SlotOf(saved.A)));
            Assert.True(loaded.Close().Clean);
        }

        // The save after that holds one more edit of that rock, still ahead of the other rock's.
        Assert.True(WorldSnapshotCodec.TryDecode(folder.Bytes!, SaveLimits.Default, out var snapshot, out var error), error);
        Assert.Equal(new[] { 33, 1 }, snapshot!.EditedRockEdits[..2]);
        Assert.Equal(34, snapshot.RockEditCount);
    }

    [Fact]
    public void AWorldThatIsLoadedAndClosedAgainSavesTheSameBytes()
    {
        var saved = Shared.Value;
        var folder = saved.Folder();
        using (var loaded = new SaveRig(folder, Content))
            Assert.True(loaded.Close().Clean);

        // Nothing was simulated in between, so the second save is the first but for the moment it was taken.
        static byte[] Canonical(byte[] bytes)
        {
            Assert.True(WorldSnapshotCodec.TryDecode(bytes, SaveLimits.Default, out var snapshot, out var error), error);
            snapshot!.CapturedUnixMs = 0;
            return WorldSnapshotCodec.Encode(snapshot);
        }

        Assert.Equal(Canonical(saved.Bytes), Canonical(folder.Bytes!));
    }

    [Theory]
    [InlineData("radius above the limit")]
    [InlineData("radius below the limit")]
    [InlineData("stroke longer than the limit")]
    [InlineData("point beyond the rock")]
    [InlineData("rock past the sector's rocks")]
    [InlineData("rock that no sector holds")]
    [InlineData("rock named twice")]
    [InlineData("count of zero")]
    [InlineData("count that overruns")]
    [InlineData("edits that do not add up")]
    public void ASaveWhoseEditsTheRulesDoNotAllowIsRefusedWholeAndItsStoreReleased(string what)
    {
        var saved = Shared.Value;
        var folder = saved.Folder();
        var rules = Content.RockEdits;
        var error = Assert.Throws<InvalidDataException>(() => new SaveRig(folder, Content, world =>
        {
            // Well formed for the codec, so the world loads and the server's own rules decide.
            switch (what)
            {
                case "radius above the limit":
                    world.RockEdits[3].RadiusMm = (ushort)(rules.MaxRadiusMm + 1);
                    break;
                case "radius below the limit":
                    world.RockEdits[3].RadiusMm = (ushort)(rules.MinRadiusMm - 1);
                    break;
                case "stroke longer than the limit":
                    world.RockEdits[3].Bx = world.RockEdits[3].Ax + rules.MaxSegmentUnits + 1;
                    world.RockEdits[3].By = world.RockEdits[3].Ay;
                    world.RockEdits[3].Bz = world.RockEdits[3].Az;
                    break;
                case "point beyond the rock":
                    world.RockEdits[0].Ax = 4_000_000;
                    break;
                case "rock past the sector's rocks":
                    world.EditedRocks[0] = world.EditedRocks[0] with { Index = Content.Galaxy.MaxAsteroidsPerSector - 1 };
                    break;
                case "rock that no sector holds":
                    world.EditedRocks[0] = world.EditedRocks[0] with { Index = 1_000_000 };
                    break;
                case "rock named twice":
                    world.EditedRocks[1] = world.EditedRocks[0];
                    break;
                case "count of zero":
                    world.EditedRockEdits[0] = 0;
                    break;
                case "count that overruns":
                    world.EditedRockEdits[1] = 5;
                    break;
                case "edits that do not add up":
                    world.EditedRockEdits[0]--;
                    break;
                default:
                    throw new ArgumentException(what);
            }

            return world;
        }));

        Assert.NotEqual("", error.Message);
        Assert.False(folder.Leased);
        Assert.Same(saved.Bytes, folder.Bytes);
    }
}
