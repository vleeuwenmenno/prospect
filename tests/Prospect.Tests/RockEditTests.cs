using System.Numerics;
using System.Text;
using Prospect.Client.Render;
using Prospect.Game.World;
using Prospect.Game.World.RockEditing;
using Xunit;

namespace Prospect.Tests;

/// <summary>Rock edits: the edit list, the bricks worked out from it, contact against them and the drawn patch.</summary>
public sealed class RockEditTests
{
    private static readonly GameContent Content = GameContent.Load();
    private static readonly RockRef Name = new(new SectorCoord(1, 2, 3), 7);

    /// <summary>A groove of three passes over shape 0, as a laser held on one line would cut: each pass deeper.</summary>
    internal static RockScrape[] Groove(float radius, int shape = 0, float cut = 1f, int segments = 16)
    {
        var direction = Vector3.Normalize(new Vector3(1, 0.3f, 0.2f));
        var start = direction * radius * AsteroidShape.RadiusAlong(shape, direction);
        var normal = Vector3.Normalize(start);
        var tangent = Vector3.Normalize(Vector3.Cross(normal, Vector3.UnitY));
        var edits = new List<RockScrape>();
        foreach (var depth in (ReadOnlySpan<float>)[0.3f, 0.8f, 1.3f])
        {
            Vector3 Axis(int i)
            {
                var d = Vector3.Normalize(start + tangent * (i * 0.5f));
                return d * (radius * AsteroidShape.RadiusAlong(shape, d) - depth + cut);
            }

            for (var i = 0; i < segments; i++)
            {
                var edit = RockScrape.From(Axis(i), Axis(i + 1), cut);
                if (i > 0)
                {
                    var previous = edits[^1];
                    edit.Ax = previous.Bx;
                    edit.Ay = previous.By;
                    edit.Az = previous.Bz;
                }

                edits.Add(edit);
            }
        }

        return [.. edits];
    }

    private static RockEdits Applied(ReadOnlySpan<RockScrape> edits)
    {
        var set = new RockEdits(Content.RockEdits);
        foreach (ref readonly var edit in edits)
            set.Bricks.Apply(set.Log.Append(Name, edit));
        return set;
    }

    /// <summary>The groove's three strokes of sixteen, appended stroke by stroke in the order given; each stroke keeps its own order.</summary>
    private static RockEdits AppliedInOrder(RockScrape[] groove, params int[] strokes)
    {
        var set = new RockEdits(Content.RockEdits);
        foreach (var stroke in strokes)
            for (var i = stroke * 16; i < stroke * 16 + 16; i++)
                set.Bricks.Apply(set.Log.Append(Name, groove[i]));
        return set;
    }

    /// <summary>Brings a rock's bricks up to every edit its log holds, as the client does after a replay.</summary>
    private static void ApplyPending(RockEdits set, int slot)
    {
        for (var next = set.Bricks.NextPending(slot, set.Log.Count); next >= 0; next = set.Bricks.NextPending(slot, set.Log.Count))
            set.Bricks.Apply(next);
    }

    private static Asteroid Rock(float radius, Quaternion? rotation = null) =>
        new() { Radius = radius, Rotation = rotation ?? Quaternion.Identity, Shape = 0, Resource = -1, Mix1 = -1, Mix2 = -1 };

    /// <summary>Rules read from bytes the test writes, so a limit can be small enough to reach.</summary>
    private static RockEditRules Rules(string cell = "0.25", string minRadius = "0.25", string maxRadius = "2", string segment = "4", int editsPerRock = 3,
        int rocks = 2, int edits = 4, int bricksPerRock = 64, int bricks = 64, string debugRadius = "1", string debugRange = "600", string debugInterval = "0.1", string debugBite = "0.4") =>
        RockEditRules.Parse(Encoding.UTF8.GetBytes(
            $$"""
            { "cellMetres": {{cell}}, "minRadiusMetres": {{minRadius}}, "maxRadiusMetres": {{maxRadius}}, "maxSegmentMetres": {{segment}},
              "maxEditsPerRock": {{editsPerRock}}, "maxBricksPerRock": {{bricksPerRock}}, "maxEditedRocks": {{rocks}}, "maxEdits": {{edits}}, "maxBricks": {{bricks}},
              "debug": { "rangeMetres": {{debugRange}}, "intervalSeconds": {{debugInterval}}, "biteMetres": {{debugBite}}, "radiusMetres": {{debugRadius}} } }
            """));

    private static RockScrape Sphere(Vector3 centre, float radius) => RockScrape.From(centre, centre, radius);

    /// <summary>The unit direction at the middle of the groove's deepest pass, and the point of that pass's axis there.</summary>
    private static (Vector3 Direction, Vector3 Axis, Vector3 Along) Middle(RockScrape[] groove)
    {
        // Edit 32 + 8 starts at the ninth axis point (each edit after a pass's first starts where the one before ended).
        var edit = groove[32 + 8];
        var axis = edit.A;
        return (Vector3.Normalize(axis), axis, Vector3.Normalize(edit.B - edit.A));
    }

    // ------------------------------------------------------------------------------------------ codec

    [Fact]
    public void TheFullFormIs21BytesAndTheContinuingFormIs9()
    {
        var groove = Groove(100);
        Assert.Equal(21, RockEditCodec.ScrapeBytes);
        Assert.Equal(9, RockEditCodec.ScrapeOnBytes);
        Span<byte> bytes = stackalloc byte[RockEditCodec.ScrapeBytes];

        Assert.Equal(21, RockEditCodec.Write(groove[0], default, false, bytes));
        Assert.Equal(21, RockEditCodec.Size(groove[0], default, false));

        // The second stroke starts where the first ended, so with a chain it is the short form...
        Assert.True(groove[1].StartsAtEndOf(groove[0]));
        Assert.Equal(9, RockEditCodec.Write(groove[1], groove[0], true, bytes));
        Assert.Equal(9, RockEditCodec.Size(groove[1], groove[0], true));

        // ...and without one it is not, whatever it starts at.
        Assert.Equal(21, RockEditCodec.Write(groove[1], groove[0], false, bytes));
        Assert.Equal(21, RockEditCodec.Size(groove[1], groove[0], false));

        // A stroke that starts elsewhere than the previous one's end is full even in a chain.
        Assert.False(groove[16].StartsAtEndOf(groove[15]));
        Assert.Equal(21, RockEditCodec.Write(groove[16], groove[15], true, bytes));
    }

    [Fact]
    public void AStepTooLongForSixteenBitsFallsBackToTheFullForm()
    {
        var first = RockScrape.From(Vector3.Zero, new Vector3(10, 0, 0), 1f);
        var far = new RockScrape { Ax = first.Bx, Ay = first.By, Az = first.Bz, Bx = first.Bx + short.MaxValue + 1, By = first.By, Bz = first.Bz, RadiusMm = 1000 };
        Span<byte> bytes = stackalloc byte[RockEditCodec.ScrapeBytes];
        Assert.Equal(21, RockEditCodec.Size(far, first, true));
        Assert.Equal(21, RockEditCodec.Write(far, first, true, bytes));
        Assert.Equal(21, RockEditCodec.Read(bytes, first, true, out var back));
        Assert.Equal(far, back);

        // The largest step that does fit takes the short form and comes back exactly, in either direction.
        var near = far;
        near.Bx = first.Bx + short.MaxValue;
        near.By = first.By + short.MinValue;
        Assert.Equal(9, RockEditCodec.Write(near, first, true, bytes));
        Assert.Equal(9, RockEditCodec.Read(bytes, first, true, out back));
        Assert.Equal(near, back);
    }

    [Fact]
    public void AChainOfARealGrooveRoundTripsExactlyAndTakesTheDocumentedBytes()
    {
        var groove = Groove(100);
        Assert.Equal(48, groove.Length);
        var bytes = new byte[groove.Length * RockEditCodec.ScrapeBytes];
        var at = 0;
        var expected = 0;
        for (var i = 0; i < groove.Length; i++)
        {
            var previous = i > 0 ? groove[i - 1] : default;
            expected += RockEditCodec.Size(groove[i], previous, i > 0);
            at += RockEditCodec.Write(groove[i], previous, i > 0, bytes.AsSpan(at));
        }

        // Three strokes of sixteen: the first edit of each stroke in full, the other 45 as steps. docs/ quotes this figure.
        Assert.Equal(3 * 21 + 45 * 9, at);
        Assert.Equal(468, at);
        Assert.Equal(at, expected);

        var read = new RockScrape[groove.Length];
        var offset = 0;
        for (var i = 0; i < groove.Length; i++)
        {
            var taken = RockEditCodec.Read(bytes.AsSpan(offset, at - offset), i > 0 ? read[i - 1] : default, i > 0, out read[i]);
            Assert.True(taken > 0, $"edit {i} did not read back");
            offset += taken;
        }

        Assert.Equal(at, offset);
        Assert.Equal(groove, read);
    }

    [Fact]
    public void ReadTakesNothingFromBytesThatAreNotAnEdit()
    {
        var groove = Groove(100);
        var full = new byte[RockEditCodec.ScrapeBytes];
        var on = new byte[RockEditCodec.ScrapeOnBytes];
        RockEditCodec.Write(groove[16], default, false, full);
        RockEditCodec.Write(groove[17], groove[16], true, on);

        Assert.Equal(0, RockEditCodec.Read([], default, false, out _));
        Assert.Equal(0, RockEditCodec.Read([], groove[16], true, out _));

        // No kind but 1 and 2 is an edit: 0 is what empty memory holds, 3 is kept for cracks.
        foreach (var kind in new byte[] { 0, 3, 4, 127, 255 })
        {
            var copy = (byte[])full.Clone();
            copy[0] = kind;
            Assert.Equal(0, RockEditCodec.Read(copy, default, false, out _));
            copy = (byte[])on.Clone();
            copy[0] = kind;
            Assert.Equal(0, RockEditCodec.Read(copy, groove[16], true, out _));
        }

        // Fewer bytes than the kind says follow: every shorter prefix is refused, never read past its end.
        for (var n = 0; n < full.Length; n++)
            Assert.Equal(0, RockEditCodec.Read(full.AsSpan(0, n), default, false, out _));
        for (var n = 0; n < on.Length; n++)
            Assert.Equal(0, RockEditCodec.Read(on.AsSpan(0, n), groove[16], true, out _));

        // A zero radius is no scrape, in either form.
        var zero = (byte[])full.Clone();
        zero[19] = 0;
        zero[20] = 0;
        Assert.Equal(0, RockEditCodec.Read(zero, default, false, out _));
        zero = (byte[])on.Clone();
        zero[7] = 0;
        zero[8] = 0;
        Assert.Equal(0, RockEditCodec.Read(zero, groove[16], true, out _));

        // A continuing edit has nothing to continue when there is no chain.
        Assert.Equal(0, RockEditCodec.Read(on, groove[16], false, out _));
        Assert.Equal(9, RockEditCodec.Read(on, groove[16], true, out _));

        // Bytes after an edit are not its business: the caller takes the next from what is left.
        var longer = new byte[full.Length + 5];
        full.CopyTo(longer, 0);
        Assert.Equal(21, RockEditCodec.Read(longer, default, false, out var edit));
        Assert.Equal(groove[16], edit);
    }

    [Fact]
    public void ReadRefusesAPositionOutsideTheSymmetricRange()
    {
        var edit = new RockScrape { Ax = RockScrape.MaxCoordinate, Ay = -RockScrape.MaxCoordinate, Az = 5, Bx = 0, By = 0, Bz = 0, RadiusMm = 500 };
        var bytes = new byte[RockEditCodec.ScrapeBytes];
        RockEditCodec.Write(edit, default, false, bytes);
        Assert.Equal(21, RockEditCodec.Read(bytes, default, false, out var back));
        Assert.Equal(edit, back);

        // -8,388,608 fits 24 bits, but no scrape is made there, so a save or a message that holds it is damaged.
        edit.Ay = -RockScrape.MaxCoordinate - 1;
        RockEditCodec.Write(edit, default, false, bytes);
        Assert.Equal(0, RockEditCodec.Read(bytes, default, false, out _));

        // A step that leaves the range ends the chain.
        var last = new RockScrape { Ax = 0, Ay = 0, Az = 0, Bx = RockScrape.MaxCoordinate, By = 0, Bz = 0, RadiusMm = 500 };
        var on = new byte[RockEditCodec.ScrapeOnBytes];
        on[0] = 2;
        on[1] = 1;
        on[2] = 0;
        Assert.Equal(0, RockEditCodec.Read(on, last, true, out _));
    }

    [Fact]
    public void FromRoundsToWholeUnitsAndClampsTheRadius()
    {
        var scrape = RockScrape.From(new Vector3(1.5f, -2.25f, 0.01f), new Vector3(0, 0, 3), 0.0004f);
        Assert.Equal((96, -144, 1), (scrape.Ax, scrape.Ay, scrape.Az));
        Assert.Equal((0, 0, 192), (scrape.Bx, scrape.By, scrape.Bz));
        Assert.Equal(1, scrape.RadiusMm);
        Assert.Equal(ushort.MaxValue, RockScrape.From(Vector3.Zero, Vector3.Zero, 1000f).RadiusMm);
        Assert.Equal(new Vector3(1.5f, -2.25f, 1 / 64f), scrape.A);
        Assert.Equal(0.001f, scrape.Radius);

        // A position outside the 24-bit range is held at its edge instead of wrapping.
        var wide = RockScrape.From(new Vector3(1e9f, -1e9f, 0), Vector3.Zero, 1f);
        Assert.Equal(RockScrape.MaxCoordinate, wide.Ax);
        Assert.Equal(-RockScrape.MaxCoordinate, wide.Ay);
    }

    // ------------------------------------------------------------------------------------------ rules

    [Fact]
    public void ValidRefusesARadiusOutsideTheLimits()
    {
        var rules = Content.RockEdits;
        var edit = Sphere(new Vector3(0, 0, 9), 1f);
        Assert.True(rules.Valid(edit, 10));
        edit.RadiusMm = (ushort)(rules.MinRadiusMm - 1);
        Assert.False(rules.Valid(edit, 10));
        edit.RadiusMm = (ushort)rules.MinRadiusMm;
        Assert.True(rules.Valid(edit, 10));
        edit.RadiusMm = (ushort)rules.MaxRadiusMm;
        Assert.True(rules.Valid(edit, 10));
        edit.RadiusMm = (ushort)(rules.MaxRadiusMm + 1);
        Assert.False(rules.Valid(edit, 10));
        edit.RadiusMm = 0;
        Assert.False(rules.Valid(edit, 10));
        edit.RadiusMm = ushort.MaxValue;
        Assert.False(rules.Valid(edit, 10));
    }

    [Fact]
    public void ValidRefusesAStrokeLongerThanTheLimit()
    {
        var rules = Content.RockEdits;
        var edit = new RockScrape { Ax = 0, Ay = 0, Az = 0, Bx = rules.MaxSegmentUnits, By = 0, Bz = 0, RadiusMm = 1000 };
        Assert.True(rules.Valid(edit, 100));
        edit.Bx = rules.MaxSegmentUnits + 1;
        Assert.False(rules.Valid(edit, 100));

        // The length is the straight distance, not the longest axis: a diagonal just over the limit is refused...
        var step = (int)Math.Ceiling(rules.MaxSegmentUnits / Math.Sqrt(3)) + 1;
        edit = new RockScrape { Ax = 0, Ay = 0, Az = 0, Bx = step, By = step, Bz = step, RadiusMm = 1000 };
        Assert.False(rules.Valid(edit, 100));

        // ...and one just under is not, though each axis moves less than a third of the limit.
        step = (int)Math.Floor(rules.MaxSegmentUnits / Math.Sqrt(3)) - 1;
        edit = new RockScrape { Ax = 0, Ay = 0, Az = 0, Bx = step, By = step, Bz = step, RadiusMm = 1000 };
        Assert.True(rules.Valid(edit, 100));
    }

    [Fact]
    public void ValidRefusesAPointBeyondTheRocksReach()
    {
        var rules = Content.RockEdits;
        const float rock = 10f;

        // The reach is the rock's radius plus two per cent plus the cut's own radius.
        var inside = Sphere(new Vector3(0, 0, rock * 1.02f + 1f - 0.05f), 1f);
        Assert.True(rules.Valid(inside, rock));
        var outside = Sphere(new Vector3(0, 0, rock * 1.02f + 1f + 0.1f), 1f);
        Assert.False(rules.Valid(outside, rock));

        // Either end, and along any axis, not only the first and the z axis.
        var end = new RockScrape { Ax = 0, Ay = 0, Az = 0, Bx = 20 * RockScrape.Units, By = 0, Bz = 0, RadiusMm = 1000 };
        Assert.False(rules.Valid(end, rock));
        var start = new RockScrape { Ax = -20 * RockScrape.Units, Ay = 0, Az = 0, Bx = -19 * RockScrape.Units, By = 0, Bz = 0, RadiusMm = 1000 };
        Assert.False(rules.Valid(start, rock));

        // The same point is fine on a bigger rock, and a rock is judged by its own radius.
        Assert.True(rules.Valid(outside, 20f));
        Assert.False(rules.Valid(Sphere(new Vector3(0, 0, 21f), 1f), 10f));
    }

    [Fact]
    public void ValidAcceptsTheGroove()
    {
        foreach (var radius in new[] { 10f, 100f, 1000f })
            foreach (var edit in Groove(radius))
                Assert.True(Content.RockEdits.Valid(edit, radius), $"a groove edit on a rock of {radius} m was refused");

        // On a rock too small to reach the groove, the same edits are not allowed.
        Assert.Contains(Groove(100), edit => !Content.RockEdits.Valid(edit, 20));
    }

    [Fact]
    public void AnIceShardCannotBeScrapedButEveryOtherShapeCan()
    {
        Assert.False(RockEditRules.Scrapable(AsteroidShape.Icicle));
        Assert.False(RockEditRules.Scrapable(-1));
        Assert.False(RockEditRules.Scrapable(Galaxy.RockShapes + 1));
        for (var shape = 0; shape < Galaxy.RockShapes; shape++)
            Assert.True(RockEditRules.Scrapable(shape));
    }

    [Fact]
    public void TheShippedRulesAreWhatTheDocumentsQuote()
    {
        var rules = Content.RockEdits;
        Assert.Equal(0.25f, rules.CellMetres);
        Assert.Equal(16, rules.CellUnits);
        Assert.Equal(128, rules.BrickUnits);
        Assert.Equal(2f, rules.BrickMetres);
        Assert.Equal((250, 2000), (rules.MinRadiusMm, rules.MaxRadiusMm));
        Assert.Equal(4 * RockScrape.Units, rules.MaxSegmentUnits);

        // A sample holds a distance up to 127 steps of a cell's 32, a little under four cells.
        Assert.InRange(rules.ReachUnits, 63, 65);
    }

    [Theory]
    [InlineData("0.3", "0.3")]
    [InlineData("0.01", "0.25")]
    [InlineData("3", "3")]
    public void ParseRefusesACellThatIsNotAWholeNumberOfUnitsFromASixteenthToTwoMetres(string cell, string minRadius)
    {
        Assert.Throws<InvalidDataException>(() => Rules(cell: cell, minRadius: minRadius));
    }

    [Fact]
    public void ParseRefusesFiguresThatContradictEachOther()
    {
        Assert.NotNull(Rules());
        Assert.Throws<InvalidDataException>(() => Rules(minRadius: "3"));
        Assert.Throws<InvalidDataException>(() => Rules(minRadius: "0.1"));
        Assert.Throws<InvalidDataException>(() => Rules(edits: 2, editsPerRock: 3));
        Assert.Throws<InvalidDataException>(() => Rules(bricksPerRock: 63));
        Assert.Throws<InvalidDataException>(() => Rules(bricks: 64, bricksPerRock: 65));
        Assert.Throws<InvalidDataException>(() => Rules(debugRadius: "3"));
        Assert.Throws<InvalidDataException>(() => Rules(debugInterval: "0"));
        Assert.Throws<InvalidDataException>(() => Rules(debugBite: "0"));

        // A cell of half a metre is allowed, but then no radius may be below it.
        Assert.Throws<InvalidDataException>(() => Rules(cell: "0.5"));
        Assert.Equal(32, Rules(cell: "0.5", minRadius: "0.5").CellUnits);
    }

    // ------------------------------------------------------------------------------------------ log

    [Fact]
    public void TheLogKeepsEachRocksEditsInTheOrderTheyWereMadeWhateverIsInterleaved()
    {
        var log = new RockEditLog(Content.RockEdits);
        var a = new RockRef(new SectorCoord(0, 0, 0), 1);
        var b = new RockRef(new SectorCoord(0, 0, 0), 2);
        Assert.Equal((0, 0), (log.Count, log.RockCount));
        Assert.Equal(-1, log.SlotOf(a));

        var edits = Groove(100);
        var order = new[] { a, b, a, a, b, a };
        var indices = new int[order.Length];
        for (var i = 0; i < order.Length; i++)
            indices[i] = log.Append(order[i], edits[i]);

        Assert.Equal(new[] { 0, 1, 2, 3, 4, 5 }, indices);
        Assert.Equal((6, 2), (log.Count, log.RockCount));
        Assert.Equal((0, 1), (log.SlotOf(a), log.SlotOf(b)));
        Assert.Equal((4, 2), (log.EditsOf(0), log.EditsOf(1)));
        Assert.Equal(a, log.RockAt(0));
        Assert.Equal(b, log.RockAt(1));

        var walked = new List<int>();
        for (var index = log.FirstOf(0); index >= 0; index = log.NextOf(index)) walked.Add(index);
        Assert.Equal(new[] { 0, 2, 3, 5 }, walked);
        Assert.Equal(5, log.LastOf(0));
        walked.Clear();
        for (var index = log.FirstOf(1); index >= 0; index = log.NextOf(index)) walked.Add(index);
        Assert.Equal(new[] { 1, 4 }, walked);
        Assert.Equal(new[] { 0, 1, 0, 0, 1, 0 }, indices.Select(log.RockOf));
        for (var i = 0; i < 6; i++) Assert.Equal(edits[i], log.EditAt(i));
    }

    [Fact]
    public void TheLogRefusesAnEditPastWhatOneRockMayHold()
    {
        var log = new RockEditLog(Rules(editsPerRock: 3, rocks: 2, edits: 10));
        var edit = Sphere(Vector3.Zero, 1f);
        Assert.Equal(RockEditRefusal.None, log.Check(Name));
        for (var i = 0; i < 3; i++) Assert.True(log.Append(Name, edit) >= 0);

        Assert.Equal(RockEditRefusal.RockFull, log.Check(Name));
        Assert.Equal(-1, log.Append(Name, edit));
        Assert.Equal(3, log.Count);
        Assert.Equal(3, log.EditsOf(log.SlotOf(Name)));

        // A stroke is judged whole: two more would pass the limit though one would not.
        var other = new RockRef(Name.Sector, 8);
        Assert.Equal(RockEditRefusal.None, log.Check(other, 3));
        Assert.Equal(RockEditRefusal.RockFull, log.Check(other, 4));
        Assert.Equal(RockEditRefusal.RockFull, log.Check(Name, 1));

        // Another rock is not held back by this one.
        Assert.True(log.Append(other, edit) >= 0);
    }

    [Fact]
    public void TheLogRefusesAnEditPastWhatAWorldMayHoldOfRocksAndEdits()
    {
        var log = new RockEditLog(Rules(editsPerRock: 3, rocks: 2, edits: 4));
        var edit = Sphere(Vector3.Zero, 1f);
        var a = new RockRef(Name.Sector, 1);
        var b = new RockRef(Name.Sector, 2);
        var c = new RockRef(Name.Sector, 3);
        Assert.True(log.Append(a, edit) >= 0);
        Assert.True(log.Append(b, edit) >= 0);

        // A third rock does not fit the two rocks the world may hold; an edit of the two already there still does.
        Assert.Equal(RockEditRefusal.WorldFull, log.Check(c));
        Assert.Equal(-1, log.Append(c, edit));
        Assert.Equal((2, 2), (log.Count, log.RockCount));
        Assert.Equal(-1, log.SlotOf(c));
        Assert.True(log.Append(a, edit) >= 0);
        Assert.True(log.Append(a, edit) >= 0);

        // Four edits fill the world's list: that refusal is the world's, though the rock's own limit is met too.
        Assert.Equal(4, log.Count);
        Assert.Equal(RockEditRefusal.WorldFull, log.Check(b));
        Assert.Equal(RockEditRefusal.WorldFull, log.Check(a));
        Assert.Equal(-1, log.Append(b, edit));
        Assert.Equal(4, log.Count);

        // A stroke that would pass the world's edits is refused as the world's.
        Assert.Equal(RockEditRefusal.WorldFull, new RockEditLog(Rules(edits: 4)).Check(c, 5));
    }

    // ------------------------------------------------------------------------------------------ bricks

    [Fact]
    public void TheSameEditsInAnotherOrderOfStrokesGiveTheSameBricks()
    {
        var groove = Groove(100);
        var forward = AppliedInOrder(groove, 0, 1, 2);
        var shuffled = AppliedInOrder(groove, 2, 0, 1);
        var backward = AppliedInOrder(groove, 2, 1, 0);
        var slot = forward.Log.SlotOf(Name);
        Assert.Equal(0, slot);
        Assert.True(forward.Bricks.BrickCount(slot) > 10);
        Assert.Equal(forward.Bricks.Hash(slot), shuffled.Bricks.Hash(slot));
        Assert.Equal(forward.Bricks.Hash(slot), backward.Bricks.Hash(slot));
        Assert.Equal(forward.Bricks.BrickCount(slot), shuffled.Bricks.BrickCount(slot));
        Assert.Equal(forward.Bricks.Used, backward.Bricks.Used);

        // And the hash is not blind: a groove with one pass left out is another cut.
        var short2 = AppliedInOrder(groove, 0, 1);
        Assert.NotEqual(forward.Bricks.Hash(slot), short2.Bricks.Hash(slot));
    }

    [Fact]
    public async Task TheSameEditsOnTwoThreadsAtOnceGiveTheSameBricks()
    {
        var groove = Groove(100);
        var expected = AppliedInOrder(groove, 0, 1, 2).Bricks.Hash(0);
        using var gate = new Barrier(2);
        ulong Run(int[] order)
        {
            // Each thread has its own store; the rig that allocates them runs apart, and the edits start together.
            var set = new RockEdits(Content.RockEdits);
            gate.SignalAndWait();
            foreach (var stroke in order)
                for (var i = stroke * 16; i < stroke * 16 + 16; i++)
                    set.Bricks.Apply(set.Log.Append(Name, groove[i]));
            return set.Bricks.Hash(0);
        }

        var first = Task.Run(() => Run([0, 1, 2]), TestContext.Current.CancellationToken);
        var second = Task.Run(() => Run([2, 0, 1]), TestContext.Current.CancellationToken);
        var hashes = await Task.WhenAll(first, second);
        Assert.Equal(expected, hashes[0]);
        Assert.Equal(expected, hashes[1]);
    }

    [Theory]
    [InlineData(1f, 1f, 1f, 1, 1)]
    [InlineData(0f, 0f, 0f, 8, 8)]
    [InlineData(2f, 1f, 1f, 2, 4)]
    public void ABrickExistsOnlyWhereACutIsWithinACell(float x, float y, float z, int least, int most)
    {
        var set = Applied([Sphere(new Vector3(x, y, z), 0.5f)]);
        var bricks = set.Bricks;
        var count = bricks.BrickCount(0);
        Assert.InRange(count, least, most);
        Assert.Equal(count, bricks.Used);
        var seen = 0;
        for (var brick = bricks.FirstBrick(0); brick >= 0; brick = bricks.NextBrick(brick))
        {
            seen++;
            Assert.True(bricks.Samples(brick).ToArray().Min() < RockEditRules.StepsPerCell, "a brick holds no sample within a cell of the cut");
            bricks.Coordinates(brick, out var bx, out var by, out var bz);
            Assert.Equal(brick, bricks.Find(0, bx, by, bz));
        }

        Assert.Equal(count, seen);
        Assert.Equal(-1, bricks.Find(0, 5, 5, 5));
    }

    // Rock next to a cut's wall must read its distance to that wall, or contact there would fall back to the base
    // shape and push a hull out of a groove through its floor. So the brick beyond a face the cut comes close to
    // exists too, though none of its samples is inside the cut.
    [Fact]
    public void RockWithinACellOfACutReadsItsDistanceToTheWallAcrossABrickFace()
    {
        var set = Applied([Sphere(new Vector3(1f, 1f, 0.6f), 0.5f)]);
        var bricks = set.Bricks;
        Assert.Equal(2, bricks.BrickCount(0));
        var below = bricks.Find(0, 0, 0, -1);
        Assert.True(below >= 0, "the brick under the face the cut stops short of was not made");
        var least = bricks.Samples(below).ToArray().Min();
        Assert.InRange(least, 0, RockEditRules.StepsPerCell - 1);

        // 0.15 m under the cut's lowest point, across the face.
        var wall = bricks.Sample(0, new Vector3(1f, 1f, -0.05f), out var slope);
        Assert.InRange(wall, 0.12f, 0.18f);
        Assert.True(slope.Z < -0.5f, "the field does not fall toward the cut above");
    }

    [Fact]
    public void MayAddIsNeverLessThanWhatApplyThenAdds()
    {
        var groove = Groove(100);
        var set = new RockEdits(Content.RockEdits);
        foreach (var edit in groove)
        {
            // As the server asks it: before the edit is in the log, with the rock's slot or -1 for a rock with none.
            var may = set.Bricks.MayAdd(set.Log.SlotOf(Name), edit);
            var before = set.Bricks.Used;
            set.Bricks.Apply(set.Log.Append(Name, edit));
            var added = set.Bricks.Used - before;
            Assert.True(added <= may, $"an edit added {added} bricks of the {may} that MayAdd allowed");
        }

        // It is also not absurd: the first edit of a fresh rock reaches at most the bricks its bounds touch.
        Assert.InRange(new RockEdits(Content.RockEdits).Bricks.MayAdd(-1, groove[0]), 1, 64);
    }

    [Fact]
    public void ClearThenApplyingAgainGivesTheSameBricks()
    {
        var set = Applied(Groove(100));
        var bricks = set.Bricks;
        var hash = bricks.Hash(0);
        var used = bricks.Used;
        var version = bricks.Version(0);
        Assert.True(used > 10);

        bricks.Clear(0);
        Assert.Equal((0, 0, 0), (bricks.Used, bricks.BrickCount(0), bricks.Applied(0)));
        Assert.Equal(-1, bricks.FirstBrick(0));
        Assert.False(bricks.CutOf(0).Any);
        Assert.Equal(float.PositiveInfinity, bricks.Sample(0, set.Log.EditAt(5).A, out _));
        Assert.NotEqual(version, bricks.Version(0));

        // The log still has every edit, so the bricks are worked out from it again.
        Assert.Equal(set.Log.FirstOf(0), bricks.NextPending(0, set.Log.Count));
        ApplyPending(set, 0);
        Assert.Equal(set.Log.EditsOf(0), bricks.Applied(0));
        Assert.Equal(used, bricks.Used);
        Assert.Equal(hash, bricks.Hash(0));
    }

    [Fact]
    public void NextPendingNamesEachEditOfARockOnceAndInOrderAndNeverOnePastTheLimitRead()
    {
        var groove = Groove(100);
        var set = new RockEdits(Content.RockEdits);
        for (var i = 0; i < 6; i++) set.Log.Append(Name, groove[i]);
        var bricks = set.Bricks;

        // A reader that has read a count of 4 never takes the edits beyond it, though the log has them linked.
        var taken = new List<int>();
        for (var next = bricks.NextPending(0, 4); next >= 0; next = bricks.NextPending(0, 4))
        {
            taken.Add(next);
            bricks.Apply(next);
        }

        Assert.Equal(new[] { 0, 1, 2, 3 }, taken);
        Assert.Equal(4, bricks.Applied(0));
        Assert.Equal(-1, bricks.NextPending(0, 4));
        Assert.Equal(4, bricks.NextPending(0, 6));
    }

    [Fact]
    public void ACopyOfARocksBricksFromAnotherCacheOfTheSameLogIsTheSame()
    {
        var set = Applied(Groove(100));
        var source = set.Bricks;
        var copy = new RockBricks(set.Log, Content.RockEdits.MaxBricks);
        Assert.True(copy.CopyRock(source, 0));
        Assert.Equal(source.Hash(0), copy.Hash(0));
        Assert.Equal(source.BrickCount(0), copy.BrickCount(0));
        Assert.Equal(source.Applied(0), copy.Applied(0));
        Assert.Equal(source.Used, copy.Used);

        // Samples agree everywhere that matters: along the edit's axes and around them.
        var checkedPoints = 0;
        foreach (var edit in Groove(100))
        {
            foreach (var offset in new[] { Vector3.Zero, new Vector3(0.3f, 0, 0), new Vector3(0, -0.7f, 0.2f), new Vector3(0, 0, 1.1f) })
            {
                var point = edit.A + offset;
                var one = source.Sample(0, point, out var slopeOne);
                var two = copy.Sample(0, point, out var slopeTwo);
                Assert.Equal(BitConverter.SingleToInt32Bits(one), BitConverter.SingleToInt32Bits(two));
                Assert.Equal(slopeOne, slopeTwo);
                checkedPoints++;
            }
        }

        Assert.True(checkedPoints > 100);

        // A copy replaces what the target held of that rock, and a copy that does not fit leaves it with none.
        Assert.True(copy.CopyRock(source, 0));
        Assert.Equal(source.Used, copy.Used);
        var tiny = new RockBricks(set.Log, 3);
        Assert.False(tiny.CopyRock(source, 0));
        Assert.True(tiny.Overflowed);
        Assert.Equal(0, tiny.Used);
        Assert.False(tiny.CutOf(0).Any);
    }

    [Fact]
    public void ARocksBricksNeverPassTheLimitsAndAnOverflowIsSaid()
    {
        var rules = Rules(bricksPerRock: 64, bricks: 100, editsPerRock: 3, rocks: 2, edits: 6);
        var set = new RockEdits(rules);
        var wide = new RockScrape { Ax = -300, Ay = -300, Az = -300, Bx = 300, By = 300, Bz = 300, RadiusMm = 2000 };

        // One long fat stroke reaches more bricks than a rock may hold; the store takes what fits and never writes past its pool.
        Assert.True(set.Bricks.MayAdd(-1, wide) > 64);
        Assert.False(set.Bricks.Overflowed);
        set.Bricks.Apply(set.Log.Append(Name, wide));
        Assert.True(set.Bricks.Overflowed);
        Assert.Equal(64, set.Bricks.BrickCount(0));
        Assert.Equal(64, set.Bricks.Used);

        // The rock is full and the world is not: the two refusals are told apart.
        Assert.Equal(RockEditRefusal.None, set.Bricks.Check(0, 0));
        Assert.Equal(RockEditRefusal.RockFull, set.Bricks.Check(0, 1));
        Assert.Equal(RockEditRefusal.None, set.Bricks.Check(-1, 36));
        Assert.Equal(RockEditRefusal.WorldFull, set.Bricks.Check(-1, 37));
    }

    [Fact]
    public void SampleIsInfiniteFarFromAnyCutNegativeInsideItAndAHalfMetreOutsideItsSurface()
    {
        // On the corner of eight bricks, so every direction out of the scrape has a brick to be read in (a brick exists
        // only where a sample is inside a cut, so beside a sphere's diagonal a point may lie in none).
        var centre = Vector3.Zero;
        var set = Applied([Sphere(centre, 1f)]);
        Assert.Equal(8, set.Bricks.BrickCount(0));
        var bricks = set.Bricks;

        // Far from the scrape: no brick, no distance.
        Assert.Equal(float.PositiveInfinity, bricks.Sample(0, new Vector3(50, 50, 50), out var slope));
        Assert.Equal(Vector3.Zero, slope);
        Assert.Equal(float.PositiveInfinity, bricks.Sample(0, new Vector3(-30, 0.5f, 0), out _));

        // At its centre the field is the cut's depth, held at 127 steps of a cell's 32, and the cone at the centre is
        // rounded off by the grid: a little under the radius, never below it.
        var inside = bricks.Sample(0, centre, out _);
        Assert.True(inside < -0.8f, $"the centre of a 1 m scrape reads {inside}");
        Assert.True(inside >= -1.0f);

        // Half a metre beyond the surface the field is that distance, to a couple of centimetres, in any direction.
        foreach (var direction in new[] { Vector3.UnitX, -Vector3.UnitY, Vector3.UnitZ, Vector3.Normalize(new Vector3(1, 1, 0)), Vector3.Normalize(new Vector3(-1, 1, -1)) })
        {
            var outside = bricks.Sample(0, centre + direction * 1.5f, out var gradient);
            Assert.InRange(outside, 0.48f, 0.52f);

            // The slope points away from the cut and is about one metre a metre next to it.
            Assert.True(Vector3.Dot(Vector3.Normalize(gradient), direction) > 0.9f);
            Assert.InRange(gradient.Length(), 0.7f, 1.3f);
        }

        // On the surface itself the field is about zero.
        Assert.InRange(bricks.Sample(0, centre + Vector3.UnitX, out _), -0.02f, 0.02f);

        // The cut handed to contact reads the same.
        var cut = set.CutOf(Name);
        Assert.True(cut.Any);
        Assert.Equal(0.25f, cut.CellMetres);
        Assert.Equal(inside, cut.Sample(centre, out _));
    }

    [Fact]
    public void APointInsideTheEditsBoundsButInNoBrickReadsInfinite()
    {
        // A long diagonal stroke (the bricks do not mind its length, the rules do): its bounding box holds bricks it never touches.
        var set = Applied([RockScrape.From(new Vector3(-5f, -5f, -5f), new Vector3(5f, 5f, 5f), 0.25f)]);
        var bricks = set.Bricks;
        var found = 0;
        for (var x = -3; x <= 2; x++)
            for (var y = -3; y <= 2; y++)
                for (var z = -3; z <= 2; z++)
                {
                    if (bricks.Find(0, x, y, z) >= 0) continue;
                    found++;
                    var middle = new Vector3(x + 0.5f, y + 0.5f, z + 0.5f) * 2f;
                    Assert.Equal(float.PositiveInfinity, bricks.Sample(0, middle, out _));
                }

        // 216 bricks lie in the box; a thin diagonal touches only a few.
        Assert.InRange(found, 150, 210);
        Assert.Equal(216 - found, bricks.BrickCount(0));
    }

    [Fact]
    public void ACutOfARockWithNoEditsIsEmptyAndTouchesNothing()
    {
        var set = new RockEdits(Content.RockEdits);
        var cut = set.CutOf(Name);
        Assert.False(cut.Any);
        Assert.Equal(0f, cut.CellMetres);
        Assert.Equal(float.PositiveInfinity, cut.Sample(Vector3.Zero, out var gradient));
        Assert.Equal(Vector3.Zero, gradient);
        Assert.False(default(RockCut).Any);

        // A rock the log has never seen has no slot, and so no cut, even when other rocks are edited.
        set.Bricks.Apply(set.Log.Append(Name, Sphere(new Vector3(1, 1, 1), 0.5f)));
        Assert.True(set.CutOf(Name).Any);
        Assert.False(set.CutOf(new RockRef(Name.Sector, Name.Index + 1)).Any);
    }

    // ------------------------------------------------------------------------------------------ contact

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ABoxInTheMiddleOfTheGrooveTouchesNothingOnceTheCutIsKnown(bool turned)
    {
        const float radius = 100f;
        var groove = Groove(radius);
        var set = Applied(groove);
        var cut = set.CutOf(Name);
        var rotation = turned ? Quaternion.Normalize(Quaternion.CreateFromYawPitchRoll(0.7f, -0.4f, 1.1f)) : Quaternion.Identity;
        var rock = Rock(radius, rotation);
        var (direction, _, _) = Middle(groove);

        // 0.6 m under the original surface, on the deepest pass's axis line: inside the cut with room to spare all around the box.
        var local = direction * (radius * AsteroidShape.RadiusAlong(0, direction) - 0.6f);
        Assert.True(cut.Sample(local, out _) < -0.5f);
        var centre = Vector3.Transform(local, rotation);
        var half = new Vector3(0.2f);

        Assert.True(BoxCollision.Rock(centre, half, Quaternion.Identity, rock, out _, out var depthWithout, out _), "without the cut the box is in solid rock");
        Assert.True(depthWithout > 0.3f);
        Assert.False(BoxCollision.Rock(centre, half, Quaternion.Identity, rock, out _, out _, out _, BlockShape.Box, cut));

        // A box turned about its own axes is as clear: the turned box reaches farther at its corners.
        var turnedBox = Quaternion.CreateFromYawPitchRoll(0.5f, 0.3f, 0.2f);
        Assert.False(BoxCollision.Rock(centre, half, turnedBox, rock, out _, out _, out _, BlockShape.Box, cut));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ABoxJustInsideTheGroovesWallIsPushedOutTowardTheGroove(bool turned)
    {
        const float radius = 100f;
        var groove = Groove(radius);
        var set = Applied(groove);
        var cut = set.CutOf(Name);
        var rotation = turned ? Quaternion.Normalize(Quaternion.CreateFromYawPitchRoll(-1.2f, 0.5f, 0.3f)) : Quaternion.Identity;
        var rock = Rock(radius, rotation);
        var (direction, axis, along) = Middle(groove);

        // Beside the deepest pass and a little under its axis, 1.1 m from the axis line: 0.1 m beyond the cut's wall (a metre radius).
        var tangent = Vector3.Normalize(along - direction * Vector3.Dot(along, direction));
        var side = Vector3.Normalize(Vector3.Cross(direction, tangent));
        var best = float.NegativeInfinity;
        var point = Vector3.Zero;
        foreach (var sign in new[] { 1f, -1f })
        {
            var candidate = axis + side * sign * 0.98f - direction * 0.5f;
            var below = -RockPicking.Surface(candidate, rock);
            if (below > best) (best, point) = (below, candidate);
        }

        // The wall must be the nearer thing: the point lies well under the original surface.
        Assert.True(best > 0.25f, $"the point is only {best} m under the surface");
        var wall = cut.Sample(point, out var gradient);
        Assert.InRange(wall, 0.07f, 0.13f);

        // A tiny box, so every sample of it is about the point: the box's own extent is not what is measured.
        var centre = Vector3.Transform(point, rotation);
        Assert.True(BoxCollision.Rock(centre, new Vector3(0.01f), Quaternion.Identity, rock, out var normal, out var depth, out _, BlockShape.Box, cut));
        Assert.InRange(depth, 0.07f, 0.14f);

        // The way out is the wall's: minus the field's slope, which runs from the groove to the point, in the rock's own frame.
        var expected = Vector3.Normalize(-gradient);
        var normalInRock = Vector3.Transform(normal, Quaternion.Conjugate(rotation));
        Assert.Equal(1f, normalInRock.Length(), 3);
        Assert.True(Vector3.Dot(normalInRock, expected) > 0.99f, $"the push is {normalInRock} but the wall's is {expected}");

        // And it points to the groove's axis, the nearest of the cut, not out through the rock's far side.
        Assert.True(Vector3.Dot(normalInRock, Vector3.Normalize(axis - point)) > 0.95f);

        // Without the cut, the same box is pushed out along the original surface's normal instead, a metre under it or more.
        Assert.True(BoxCollision.Rock(centre, new Vector3(0.01f), Quaternion.Identity, rock, out var plain, out var plainDepth, out _));
        Assert.True(plainDepth > depth);
        Assert.True(Vector3.Dot(Vector3.Transform(plain, Quaternion.Conjugate(rotation)), direction) > 0.5f);
    }

    [Fact]
    public void ABoxInAnUneditedPartOfARockIsJudgedAsBefore()
    {
        const float radius = 100f;
        var groove = Groove(radius);
        var set = Applied(groove);
        var cut = set.CutOf(Name);
        var rock = Rock(radius);

        // On the far side of the rock from the groove no brick is near, so the cut changes nothing, to the bit.
        var away = Vector3.Normalize(new Vector3(-1, -0.3f, -0.2f));
        var centre = away * (radius * AsteroidShape.RadiusAlong(0, away) - 0.5f);
        var half = new Vector3(0.3f, 0.2f, 0.4f);
        Assert.True(BoxCollision.Rock(centre, half, Quaternion.Identity, rock, out var plainNormal, out var plainDepth, out var plainPoint));
        Assert.True(BoxCollision.Rock(centre, half, Quaternion.Identity, rock, out var cutNormal, out var cutDepth, out var cutPoint, BlockShape.Box, cut));
        Assert.Equal((plainNormal, plainDepth, plainPoint), (cutNormal, cutDepth, cutPoint));
    }

    // A ray from outside into a groove must find its floor, not the surface that used to be there: over the opening
    // the side function stays positive (RockPicking.Surface), or the walk would take the lip for a grazing touch and a
    // scrape aimed into an old groove would cut at the lip again and never deepen it.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ARayFromOutsideIntoTheGrooveHitsDeeperByRoughlyTheGroovesDepth(bool turned)
    {
        const float radius = 100f;
        var groove = Groove(radius);
        var cut = Applied(groove).CutOf(Name);
        var rotation = turned ? Quaternion.Normalize(Quaternion.CreateFromYawPitchRoll(0.3f, 1.0f, -0.6f)) : Quaternion.Identity;
        var rock = Rock(radius, rotation);
        var (direction, _, _) = Middle(groove);

        // From 20 m above the groove's middle, straight at the rock's centre: the ray is in the world, the rock turned in it.
        var worldUp = Vector3.Transform(direction, rotation);
        var origin = worldUp * (radius * AsteroidShape.RadiusAlong(0, direction) + 20f);
        Assert.True(RockPicking.Hit(-worldUp, -origin, rock, out var plain));
        Assert.InRange(plain, 19.9f, 20.1f);
        Assert.True(RockPicking.Hit(-worldUp, -origin, rock, cut, float.PositiveInfinity, out var scraped));

        // The deepest pass cuts to 1.3 m under the surface; the ray must go that far further than it did.
        Assert.InRange(scraped - plain, 1.15f, 1.5f);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ARayInsideTheGrooveHitsItsFloorAndOneBesideItHitsTheOriginalSurface(bool turned)
    {
        const float radius = 100f;
        var groove = Groove(radius);
        var cut = Applied(groove).CutOf(Name);
        var rotation = turned ? Quaternion.Normalize(Quaternion.CreateFromYawPitchRoll(0.3f, 1.0f, -0.6f)) : Quaternion.Identity;
        var rock = Rock(radius, rotation);
        var (direction, _, along) = Middle(groove);
        var surface = radius * AsteroidShape.RadiusAlong(0, direction);
        var worldUp = Vector3.Transform(direction, rotation);

        // From 0.4 m under the original surface, inside the cut, looking down: the floor is 1.3 m under it.
        var origin = worldUp * (surface - 0.4f);
        Assert.True(cut.Sample(direction * (surface - 0.4f), out _) < 0);
        Assert.True(RockPicking.Hit(-worldUp, -origin, rock, cut, float.PositiveInfinity, out var floor));
        Assert.InRange(floor, 0.8f, 1.1f);

        // A range that ends before the floor finds nothing there.
        Assert.False(RockPicking.Hit(-worldUp, -origin, rock, cut, 0.5f, out _));

        // A ray beside the groove, 5 m to its side, meets the original surface, with the cut or without it.
        var side = Vector3.Normalize(Vector3.Cross(direction, along));
        var local = Vector3.Normalize(direction * radius + side * 5f);
        var sideOrigin = Vector3.Transform(local * (radius * 1.5f), rotation);
        var aim = -Vector3.Transform(local, rotation);
        Assert.True(RockPicking.Hit(aim, -sideOrigin, rock, out var sidePlain));
        Assert.True(RockPicking.Hit(aim, -sideOrigin, rock, cut, float.PositiveInfinity, out var sideCut));
        Assert.Equal(sidePlain, sideCut, 2);

        // The overload with a range and no cut is the plain one.
        Assert.True(RockPicking.Hit(aim, -sideOrigin, rock, default, float.PositiveInfinity, out var again));
        Assert.Equal(sidePlain, again);
    }

    [Fact]
    public void SurfaceIsPositiveInsideACutAndNegativeInTheRockAroundIt()
    {
        const float radius = 100f;
        var groove = Groove(radius);
        var cut = Applied(groove).CutOf(Name);
        var rock = Rock(radius);
        var (direction, _, _) = Middle(groove);
        var surface = radius * AsteroidShape.RadiusAlong(0, direction);

        Assert.True(RockPicking.Surface(direction * (surface - 0.6f), rock, default) < 0);
        Assert.True(RockPicking.Surface(direction * (surface - 0.6f), rock, cut) > 0);
        Assert.True(RockPicking.Surface(direction * (surface + 1f), rock, cut) > 0);
        Assert.True(RockPicking.Surface(direction * (surface - 10f), rock, cut) < 0);
        Assert.True(RockPicking.Surface(Vector3.Zero, rock, cut) < 0);
    }

    [Fact]
    public void AContainerInTheGrooveIsNotTouchedAndOneOutsideItIsPushedOut()
    {
        const float radius = 100f;
        var groove = Groove(radius);
        var cut = Applied(groove).CutOf(Name);
        var rock = Rock(radius);
        var (direction, _, _) = Middle(groove);
        var inside = direction * (radius * AsteroidShape.RadiusAlong(0, direction) - 0.6f);

        FreeContainer Container(Vector3 at) => new()
        {
            Position = new Double3(at.X, at.Y, at.Z),
            Orientation = Quaternion.Identity,
            Size = 0,
            Resource = -1,
            Hull = 50,
        };

        // Without the cut the container is inside solid rock and is pushed; with it, it sits in the groove and nothing happens.
        var loose = Container(inside);
        Assert.True(FreeContainers.RockContact(ref loose, Content.Cargo, rock, Double3.Zero, Vector3.Zero, 100f, 0f));
        Assert.NotEqual(new Double3(inside.X, inside.Y, inside.Z), loose.Position);

        var free = Container(inside);
        var before = free.Position;
        Assert.False(FreeContainers.RockContact(ref free, Content.Cargo, rock, Double3.Zero, Vector3.Zero, 100f, 0f, cut));
        Assert.Equal(before, free.Position);

        // A container just under the surface 40 m from the groove is plain rock: handing in the cut changes nothing there.
        var tangent = Vector3.Normalize(Vector3.Cross(direction, Vector3.UnitY));
        var elsewhere = Vector3.Normalize(direction + Vector3.Cross(direction, tangent) * 0.4f);
        var other = Container(elsewhere * (radius * AsteroidShape.RadiusAlong(0, elsewhere) - 0.3f));
        Assert.True(FreeContainers.RockContact(ref other, Content.Cargo, rock, Double3.Zero, Vector3.Zero, 100f, 0f, cut));
    }

    // ------------------------------------------------------------------------------------------ allocation

    [Fact]
    public void ApplyingAnEditIntoAWarmStoreTakesNoMemory()
    {
        var groove = Groove(100);
        var set = Applied(groove);
        var bricks = set.Bricks;
        var probe = new AllocationProbe();
        while (probe.Run())
        {
            // The log keeps its edits; the bricks start again from none, so each measured step takes in one edit as new.
            bricks.Clear(0);
            for (var next = bricks.NextPending(0, set.Log.Count); next >= 0; next = bricks.NextPending(0, set.Log.Count))
            {
                probe.Start();
                bricks.Apply(next);
                probe.Stop();
            }
        }

        Assert.Equal(0, probe.Bytes);
        Assert.Equal(groove.Length, bricks.Applied(0));
    }

    [Fact]
    public void SamplingTheFieldTakesNoMemory()
    {
        var groove = Groove(100);
        var set = Applied(groove);
        var bricks = set.Bricks;
        var cut = set.CutOf(Name);
        var points = groove.Select(edit => edit.A + new Vector3(0.2f, 0.1f, -0.3f)).ToArray();
        var total = 0f;
        foreach (var point in points) total += bricks.Sample(0, point, out _);
        var probe = new AllocationProbe();
        while (probe.Measuring())
            foreach (var point in points)
                total += bricks.Sample(0, point, out _) + cut.Sample(point, out _);
        Assert.Equal(0, probe.Bytes);
        Assert.False(float.IsNaN(total));
    }

    [Fact]
    public void ARayAgainstACutRockTakesNoMemory()
    {
        const float radius = 100f;
        var groove = Groove(radius);
        var cut = Applied(groove).CutOf(Name);
        var rock = Rock(radius);
        var (direction, _, _) = Middle(groove);
        var origin = direction * (radius * AsteroidShape.RadiusAlong(0, direction) + 20f);
        Assert.True(RockPicking.Hit(-direction, -origin, rock, cut, float.PositiveInfinity, out _));
        var probe = new AllocationProbe();
        var hit = false;
        while (probe.Measuring())
            hit = RockPicking.Hit(-direction, -origin, rock, cut, float.PositiveInfinity, out _);
        Assert.True(hit);
        Assert.Equal(0, probe.Bytes);
    }

    [Fact]
    public void ContactAgainstACutRockTakesNoMemory()
    {
        const float radius = 100f;
        var groove = Groove(radius);
        var cut = Applied(groove).CutOf(Name);
        var rock = Rock(radius);
        var (direction, _, _) = Middle(groove);

        // Near the groove's floor, where the box reaches into the cut and the wall's slope is read.
        var centre = direction * (radius * AsteroidShape.RadiusAlong(0, direction) - 1.2f);
        var hit = BoxCollision.Rock(centre, new Vector3(0.5f), Quaternion.Identity, rock, out _, out _, out _, BlockShape.Box, cut);
        var probe = new AllocationProbe();
        while (probe.Measuring())
            hit = BoxCollision.Rock(centre, new Vector3(0.5f), Quaternion.Identity, rock, out _, out _, out _, BlockShape.Box, cut);
        Assert.Equal(0, probe.Bytes);
        Assert.True(hit, "the box at the groove's floor should touch the wall");
    }

    [Fact]
    public void AppendingToTheLogTakesNoMemoryNotEvenForANewRock()
    {
        var groove = Groove(100);
        var rocks = new[] { new RockRef(Name.Sector, 1), new RockRef(Name.Sector, 2), new RockRef(Name.Sector, 3) };
        var probe = new AllocationProbe();
        while (probe.Run())
        {
            // The log reserves all it may hold when it is made; that is not what is measured.
            var log = new RockEditLog(Content.RockEdits);
            probe.Start();
            for (var i = 0; i < groove.Length; i++)
                Assert.True(log.Append(rocks[i % rocks.Length], groove[i]) >= 0);
            for (var i = 0; i < log.RockCount; i++) _ = log.EditsOf(i) + log.SlotOf(log.RockAt(i)) + log.Check(rocks[i]).GetHashCode();
            probe.Stop();
        }

        Assert.Equal(0, probe.Bytes);
    }

    [Fact]
    public void BuildingThePatchAgainTakesNoMemoryOnceTheBuffersHaveGrown()
    {
        const float radius = 10f;
        var set = Applied(Groove(radius));
        var mesher = new RockPatchMesher();

        // The first build sizes the buffers and the shape's base mesh; the second and every later one reuse them.
        Assert.True(mesher.Build(set.Bricks, 0, 0, radius, 4_000_000, 12));
        Assert.True(mesher.Build(set.Bricks, 0, 0, radius, 4_000_000, 12));
        var count = mesher.VertexCount;
        var probe = new AllocationProbe();
        var ok = false;
        while (probe.Measuring())
            ok = mesher.Build(set.Bricks, 0, 0, radius, 4_000_000, 12);
        Assert.True(ok);
        Assert.Equal(count, mesher.VertexCount);
        Assert.Equal(0, probe.Bytes);
    }

    // ------------------------------------------------------------------------------------------ mesher

    [Theory]
    [InlineData(10f)]
    [InlineData(100f)]
    [InlineData(1000f)]
    public void ThePatchClosesTheRockAndLiesOnItsSurface(float radius)
    {
        var edits = Groove(radius);
        var set = Applied(edits);
        var mesher = new RockPatchMesher { GrowScale = 0 };
        Assert.True(mesher.Build(set.Bricks, 0, 0, radius, 4_000_000, 0));
        Assert.True(mesher.VertexCount > 0);
        Assert.True(mesher.Hidden > 0);
        var cut = set.CutOf(Name);
        var rock = new Asteroid { Radius = radius, Rotation = Quaternion.Identity, Shape = 0, Mix1 = -1, Mix2 = -1, Resource = -1 };

        // Every corner of the patch is on the edited surface: on a facet and not in a cut, or on a cut's wall and not outside the rock.
        var worst = 0f;
        for (var i = 0; i < mesher.VertexCount; i++)
        {
            var p = mesher.Vertices[i].Position;
            var point = new Vector3(p.X, p.Y, p.Z) * radius;
            var direction = Vector3.Normalize(point);
            var facet = AsteroidShape.Normal(0, direction, radius, 0);
            var surface = (point.Length() - radius * AsteroidShape.RadiusAlong(0, direction)) * Vector3.Dot(facet, direction);
            var wall = cut.Sample(point, out _);
            var off = MathF.Abs(surface) < MathF.Abs(wall) ? MathF.Max(MathF.Abs(surface), MathF.Max(0, -wall)) : MathF.Max(MathF.Abs(wall), MathF.Max(0, surface));
            worst = MathF.Max(worst, off);
        }

        Assert.True(worst < 0.004f + radius * 2e-6f, $"a patch corner is {worst} m off the edited surface");

        // A closed surface's faces add up to nothing: the facets the shared mesh still draws and the patch together.
        var mesh = RockBaseMesh.Of(0);
        double sx = 0, sy = 0, sz = 0, area = 0;
        void Face(Vector3 a, Vector3 b, Vector3 c, bool patch)
        {
            double ux = (double)b.X - a.X, uy = (double)b.Y - a.Y, uz = (double)b.Z - a.Z;
            double vx = (double)c.X - a.X, vy = (double)c.Y - a.Y, vz = (double)c.Z - a.Z;
            double fx = (uy * vz - uz * vy) * 0.5, fy = (uz * vx - ux * vz) * 0.5, fz = (ux * vy - uy * vx) * 0.5;
            sx += fx;
            sy += fy;
            sz += fz;
            if (patch) area += Math.Sqrt(fx * fx + fy * fy + fz * fz);
        }

        for (var run = 0; run < mesher.RunCount; run++)
            for (var t = mesher.Runs[run * 2]; t < mesher.Runs[run * 2] + mesher.Runs[run * 2 + 1]; t++)
                Face(mesh.Points[mesh.Indices[t * 3]], mesh.Points[mesh.Indices[t * 3 + 1]], mesh.Points[mesh.Indices[t * 3 + 2]], false);
        for (var i = 0; i < mesher.VertexCount; i += 3)
            Face(Unit(mesher.Vertices[i].Position), Unit(mesher.Vertices[i + 1].Position), Unit(mesher.Vertices[i + 2].Position), true);
        var open = Math.Sqrt(sx * sx + sy * sy + sz * sz) * radius * radius;
        area *= radius * radius;
        Assert.True(open < 0.002 + area * 2e-4, $"the surface is open by {open} m2 of {area} m2 patch");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(12)]
    [InlineData(5000)]
    public void TheRunsAndTheHiddenFacetsAddUpToEveryTriangleOfTheRock(int mergeGap)
    {
        const float radius = 10f;
        var set = Applied(Groove(radius));
        var mesher = new RockPatchMesher();
        Assert.True(mesher.Build(set.Bricks, 0, 0, radius, 4_000_000, mergeGap));
        var total = RockBaseMesh.Of(0).Indices.Length / 3;
        Assert.Equal(81920, total);
        Assert.True(mesher.Hidden > 0);

        var drawn = 0;
        var next = 0;
        for (var run = 0; run < mesher.RunCount; run++)
        {
            var first = mesher.Runs[run * 2];
            var count = mesher.Runs[run * 2 + 1];
            Assert.True(count > 0, "an empty run");
            Assert.True(first >= next, $"run {run} starts at {first}, before the end of the one before it ({next})");
            next = first + count;
            drawn += count;
        }

        Assert.True(next <= total);
        Assert.Equal(total, drawn + mesher.Hidden);
    }

    [Fact]
    public void MergingGapsMakesFewerRunsAndNeverMoreHiddenFacetsThanTheyAreWorth()
    {
        const float radius = 10f;
        var set = Applied(Groove(radius));
        var tight = new RockPatchMesher();
        var merged = new RockPatchMesher();
        Assert.True(tight.Build(set.Bricks, 0, 0, radius, 4_000_000, 0));
        Assert.True(merged.Build(set.Bricks, 0, 0, radius, 4_000_000, 12));

        // Facets in a gap are drawn by the patch instead: fewer runs, more hidden facets, more patch vertices.
        Assert.True(merged.RunCount <= tight.RunCount);
        Assert.True(merged.Hidden >= tight.Hidden);
        Assert.True(merged.VertexCount >= tight.VertexCount);
        Assert.Equal(tight.Bricks, merged.Bricks);

        // The mesher reads the bricks that hold a cut; one that only lies within a cell of a cut draws nothing.
        var cut = 0;
        for (var brick = set.Bricks.FirstBrick(0); brick >= 0; brick = set.Bricks.NextBrick(brick))
            if (set.Bricks.HasCut(brick)) cut++;
        Assert.Equal(cut, merged.Bricks);
        Assert.True(cut < set.Bricks.BrickCount(0));
    }

    [Fact]
    public void ARockThatNeedsMoreVerticesThanAreAllowedIsRefused()
    {
        const float radius = 10f;
        var set = Applied(Groove(radius));
        var mesher = new RockPatchMesher();
        Assert.True(mesher.Build(set.Bricks, 0, 0, radius, 4_000_000, 12));
        var needed = mesher.VertexCount;
        Assert.True(needed > 100);

        // Exactly enough is enough; one short is not, and a build that failed leaves a later one able to succeed.
        Assert.True(mesher.Build(set.Bricks, 0, 0, radius, needed, 12));
        Assert.False(mesher.Build(set.Bricks, 0, 0, radius, needed - 1, 12));
        Assert.False(mesher.Build(set.Bricks, 0, 0, radius, 30, 12));
        Assert.False(mesher.Build(set.Bricks, 0, 0, radius, 0, 12));
        Assert.True(mesher.Build(set.Bricks, 0, 0, radius, 4_000_000, 12));
        Assert.Equal(needed, mesher.VertexCount);
    }

    [Fact]
    public void AScrapeWhollyInsideTheRockHidesNoFacetAndStillMakesAClosedWall()
    {
        const float radius = 10f;
        var direction = Vector3.Normalize(new Vector3(0.3f, 1f, -0.4f));
        var centre = direction * (radius * AsteroidShape.RadiusAlong(0, direction) - 3f);
        var set = Applied([Sphere(centre, 1f)]);
        var mesher = new RockPatchMesher { GrowScale = 0 };
        Assert.True(mesher.Build(set.Bricks, 0, 0, radius, 4_000_000, 0));

        // The cut never reaches the surface, so every base facet stays the shared mesh's: one run of all of them.
        Assert.Equal(0, mesher.Hidden);
        Assert.Equal(1, mesher.RunCount);
        Assert.Equal((0, 81920), (mesher.Runs[0], mesher.Runs[1]));
        Assert.True(mesher.VertexCount > 0);
        Assert.Equal(0, mesher.VertexCount % 3);

        // The wall is a closed surface of its own: its faces add up to nothing, and it lies a metre from the cut's centre.
        double sx = 0, sy = 0, sz = 0, area = 0, worst = 0;
        for (var i = 0; i < mesher.VertexCount; i += 3)
        {
            var a = Unit(mesher.Vertices[i].Position) * radius;
            var b = Unit(mesher.Vertices[i + 1].Position) * radius;
            var c = Unit(mesher.Vertices[i + 2].Position) * radius;
            foreach (var corner in new[] { a, b, c })
                worst = Math.Max(worst, Math.Abs((corner - centre).Length() - 1.0));
            double ux = (double)b.X - a.X, uy = (double)b.Y - a.Y, uz = (double)b.Z - a.Z;
            double vx = (double)c.X - a.X, vy = (double)c.Y - a.Y, vz = (double)c.Z - a.Z;
            double fx = (uy * vz - uz * vy) * 0.5, fy = (uz * vx - ux * vz) * 0.5, fz = (ux * vy - uy * vx) * 0.5;
            sx += fx;
            sy += fy;
            sz += fz;
            area += Math.Sqrt(fx * fx + fy * fy + fz * fz);
        }

        Assert.True(Math.Sqrt(sx * sx + sy * sy + sz * sz) < 0.002 + area * 2e-4, $"the wall is open by {Math.Sqrt(sx * sx + sy * sy + sz * sz)} m2 of {area} m2");

        // A sphere of radius 1 m has 4 pi = 12.6 m2; the mesh is a little under it.
        Assert.InRange(area, 10.5, 13.5);
        Assert.True(worst < 0.03, $"the wall is {worst} m off the cut's surface");
    }

    private static Vector3 Unit(Brutal.Numerics.float3 p) => new(p.X, p.Y, p.Z);
}
