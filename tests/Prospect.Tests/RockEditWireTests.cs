using System.Numerics;
using Prospect.Engine.Net;
using Prospect.Game.Protocol;
using Prospect.Game.World;
using Prospect.Game.World.RockEditing;
using Xunit;

namespace Prospect.Tests;

/// <summary>The two rock scrape messages: the run of edits the server sends and the debug request a client sends.</summary>
public sealed class RockEditWireTests
{
    private static readonly RockRef Rock = new(new SectorCoord(-3, 4, 70_000), 20_479);

    // The message's bytes before the edits: id, sector, rock, place of the first edit, count.
    private const int HeaderBytes = 1 + 12 + 2 + 2 + 1;

    // Where the count sits in the whole message, in both: after the id, the sector, the rock and the place or the radius.
    private const int CountAt = HeaderBytes - 1;

    private static byte[] Encode(RockRef rock, int first, ReadOnlySpan<RockScrape> edits)
    {
        var bytes = new byte[RockEditsMessage.MaxWireSize];
        var writer = new NetWriter(bytes);
        RockEditsMessage.Write(ref writer, rock, first, edits);
        return writer.Written.ToArray();
    }

    /// <summary>Edits that each start somewhere new, so none takes the short form: the largest a run can be.</summary>
    private static RockScrape[] Scattered(int count)
    {
        var edits = new RockScrape[count];
        for (var i = 0; i < count; i++)
            edits[i] = RockScrape.From(new Vector3(i * 2f, 1, 0), new Vector3(i * 2f + 1, 1, 0), 0.5f);
        return edits;
    }

    [Fact]
    public void ARunOfARealGrooveRoundTripsAndTakesTheDocumentedBytes()
    {
        var groove = RockEditTests.Groove(100);
        var bytes = Encode(Rock, 513, groove);
        Assert.Equal((byte)MessageId.RockEdits, bytes[0]);

        // The header, then three edits in full and 45 as steps.
        Assert.Equal(HeaderBytes + 3 * 21 + 45 * 9, bytes.Length);

        var edits = new RockScrape[RockEditsMessage.MaxEdits];
        var reader = new NetReader(bytes.AsSpan(1));
        Assert.True(RockEditsMessage.TryRead(ref reader, out var rock, out var first, edits, out var count));
        Assert.Equal(Rock, rock);
        Assert.Equal(513, first);
        Assert.Equal(groove.Length, count);
        Assert.Equal(groove, edits[..count]);
    }

    [Fact]
    public void ARunOfSixtyFourEditsNeverPassesTheMaxWireSize()
    {
        Assert.Equal(1 + 12 + 2 + 2 + 1 + 64 * 21, RockEditsMessage.MaxWireSize);

        // Every edit in the full form is the worst case, and it is exactly the budget.
        var worst = Scattered(RockEditsMessage.MaxEdits);
        var bytes = Encode(Rock, 0, worst);
        Assert.Equal(RockEditsMessage.MaxWireSize, bytes.Length);

        var edits = new RockScrape[RockEditsMessage.MaxEdits];
        var reader = new NetReader(bytes.AsSpan(1));
        Assert.True(RockEditsMessage.TryRead(ref reader, out _, out _, edits, out var count));
        Assert.Equal(RockEditsMessage.MaxEdits, count);
        Assert.Equal(worst, edits);

        // Sixty-four continuing edits are far below it, and a single edit is a message of its own.
        var chain = new RockScrape[RockEditsMessage.MaxEdits];
        chain[0] = worst[0];
        for (var i = 1; i < chain.Length; i++)
            chain[i] = new RockScrape { Ax = chain[i - 1].Bx, Ay = chain[i - 1].By, Az = chain[i - 1].Bz, Bx = chain[i - 1].Bx + 30, By = chain[i - 1].By, Bz = chain[i - 1].Bz, RadiusMm = 500 };
        var small = Encode(Rock, 7, chain);
        Assert.Equal(HeaderBytes + 21 + 63 * 9, small.Length);
        Assert.True(small.Length < RockEditsMessage.MaxWireSize);
        Assert.Equal(HeaderBytes + 21, Encode(Rock, 0, chain.AsSpan(0, 1)).Length);
    }

    [Fact]
    public void ARunWithNoEditsOrTooManyOrTheWrongNumberIsRefused()
    {
        var groove = RockEditTests.Groove(100);
        var good = Encode(Rock, 0, groove[..16]);
        var edits = new RockScrape[RockEditsMessage.MaxEdits];

        bool Read(byte[] bytes, Span<RockScrape> into, out int count)
        {
            var reader = new NetReader(bytes.AsSpan(1));
            return RockEditsMessage.TryRead(ref reader, out _, out _, into, out count);
        }

        Assert.True(Read(good, edits, out var count));
        Assert.Equal(16, count);

        // Count 0 is not a run, and nor is one past the most a message holds, whatever bytes follow.
        var none = (byte[])good.Clone();
        none[CountAt] = 0;
        Assert.False(Read(none, edits, out _));
        var many = (byte[])good.Clone();
        many[CountAt] = RockEditsMessage.MaxEdits + 1;
        Assert.False(Read(many, edits, out _));
        many[CountAt] = 255;
        Assert.False(Read(many, edits, out _));

        // A caller whose buffer holds fewer than the run says is refused too, not written past.
        Assert.False(Read(good, edits.AsSpan(0, 15), out _));
        Assert.True(Read(good, edits.AsSpan(0, 16), out _));

        // The count must be met exactly: one more than the bytes hold, and one fewer.
        var more = (byte[])good.Clone();
        more[CountAt] = 17;
        Assert.False(Read(more, edits, out _));
        var fewer = (byte[])good.Clone();
        fewer[CountAt] = 15;
        Assert.False(Read(fewer, edits, out _));
    }

    [Fact]
    public void ARunWithTrailingBytesOrGarbageEditsIsRefused()
    {
        var groove = RockEditTests.Groove(100);
        var good = Encode(Rock, 0, groove[..16]);
        var edits = new RockScrape[RockEditsMessage.MaxEdits];

        bool Read(byte[] bytes)
        {
            var reader = new NetReader(bytes.AsSpan(1));
            return RockEditsMessage.TryRead(ref reader, out _, out _, edits, out _);
        }

        Assert.True(Read(good));
        Assert.False(Read([.. good, 0]));
        Assert.False(Read([.. good, .. good[HeaderBytes..]]));

        // Garbage where the first edit starts: an unknown kind, a zero radius, a continuation with nothing to continue.
        foreach (var kind in new byte[] { 0, 3, 9, 255 })
        {
            var garbage = (byte[])good.Clone();
            garbage[HeaderBytes] = kind;
            Assert.False(Read(garbage), $"kind {kind} was read as an edit");
        }

        var radius = (byte[])good.Clone();
        radius[HeaderBytes + 19] = 0;
        radius[HeaderBytes + 20] = 0;
        Assert.False(Read(radius));

        // A message reads without any other, so the first edit is always in the full form.
        var continuing = (byte[])good.Clone();
        continuing[HeaderBytes] = 2;
        Assert.False(Read(continuing));

        // A garbage edit later in the run spoils it all, not just the rest.
        var later = (byte[])good.Clone();
        later[HeaderBytes + 21] = 9;
        Assert.False(Read(later));
    }

    [Fact]
    public void ARunNeverThrowsOnBytesCutAtAnyLength()
    {
        foreach (var edits in new[] { RockEditTests.Groove(100)[..20], Scattered(5), Scattered(1) })
        {
            var bytes = Encode(Rock, 3, edits);
            var into = new RockScrape[RockEditsMessage.MaxEdits];
            for (var length = 0; length < bytes.Length - 1; length++)
            {
                var reader = new NetReader(bytes.AsSpan(1, length));
                Assert.False(RockEditsMessage.TryRead(ref reader, out _, out _, into, out _), $"{length} of {bytes.Length - 1} bytes were taken for a run");
            }

            var whole = new NetReader(bytes.AsSpan(1));
            Assert.True(RockEditsMessage.TryRead(ref whole, out _, out _, into, out var count));
            Assert.Equal(edits.Length, count);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(RockScrapeRequest.MaxRays)]
    public void AScrapeRequestRoundTrips(int rays)
    {
        var request = new RockScrapeRequest { Rock = Rock, RadiusMm = 1250, Rays = rays };
        for (var i = 0; i < rays; i++)
        {
            request.Origins[i] = new Vector3(i, -2 * i, 3.5f + i);
            request.Directions[i] = new Vector3(0.25f, -1, i * 0.1f);
        }

        var bytes = new byte[RockScrapeRequest.MaxWireSize];
        var writer = new NetWriter(bytes);
        request.Write(ref writer);
        Assert.Equal((byte)MessageId.RockScrape, writer.Written[0]);
        Assert.Equal(1 + 12 + 2 + 2 + 1 + rays * 24, writer.Length);

        var reader = new NetReader(writer.Written[1..]);
        Assert.True(RockScrapeRequest.TryRead(ref reader, out var copy));
        Assert.Equal((Rock, (ushort)1250, rays), (copy.Rock, copy.RadiusMm, copy.Rays));
        for (var i = 0; i < rays; i++)
        {
            Assert.Equal(request.Origins[i], copy.Origins[i]);
            Assert.Equal(request.Directions[i], copy.Directions[i]);
        }
    }

    [Fact]
    public void TheLargestScrapeRequestIsTheMaxWireSize()
    {
        var request = new RockScrapeRequest { Rock = Rock, RadiusMm = 500, Rays = RockScrapeRequest.MaxRays };
        for (var i = 0; i < request.Rays; i++) request.Directions[i] = Vector3.UnitZ;
        var bytes = new byte[RockScrapeRequest.MaxWireSize];
        var writer = new NetWriter(bytes);
        request.Write(ref writer);
        Assert.Equal(RockScrapeRequest.MaxWireSize, writer.Length);
        Assert.Equal(1 + 12 + 2 + 2 + 1 + 17 * 24, RockScrapeRequest.MaxWireSize);
    }

    [Fact]
    public void AScrapeRequestWithNoRaysTooManyOrABadNumberIsRefused()
    {
        var request = new RockScrapeRequest { Rock = Rock, RadiusMm = 1000, Rays = 3 };
        for (var i = 0; i < 3; i++) request.Directions[i] = -Vector3.UnitY;
        var good = new byte[RockScrapeRequest.MaxWireSize];
        var writer = new NetWriter(good);
        request.Write(ref writer);
        good = writer.Written.ToArray();

        static bool Read(byte[] bytes)
        {
            var reader = new NetReader(bytes.AsSpan(1));
            return RockScrapeRequest.TryRead(ref reader, out _);
        }

        Assert.True(Read(good));

        // Zero rays, one past the most, and a count the bytes do not hold: each refused.
        foreach (var rays in new byte[] { 0, RockScrapeRequest.MaxRays + 1, 255, 2, 4 })
        {
            var counted = (byte[])good.Clone();
            counted[CountAt] = rays;
            Assert.False(Read(counted), $"a count of {rays} was read");
        }

        // A request that holds a full set of rays but says zero is as refused as the empty one.
        var zero = new RockScrapeRequest { Rock = Rock, RadiusMm = 1000, Rays = 0 };
        var zeroWriter = new NetWriter(new byte[RockScrapeRequest.MaxWireSize]);
        zero.Write(ref zeroWriter);
        Assert.False(Read(zeroWriter.Written.ToArray()));

        // Bytes left over, even a whole ray's worth.
        Assert.False(Read([.. good, 0]));
        Assert.False(Read([.. good, .. new byte[24]]));

        // A figure that is not a number, in an origin or a direction, or an infinity.
        foreach (var bad in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity })
        {
            foreach (var slot in new[] { 0, 1, 2, 5 })
            {
                var wrong = (byte[])good.Clone();
                BitConverter.TryWriteBytes(wrong.AsSpan(HeaderBytes + slot * 4), bad);
                Assert.False(Read(wrong), $"{bad} at figure {slot} was read");
            }

            var last = (byte[])good.Clone();
            BitConverter.TryWriteBytes(last.AsSpan(good.Length - 4), bad);
            Assert.False(Read(last));
        }
    }

    [Fact]
    public void AScrapeRequestNeverThrowsOnBytesCutAtAnyLength()
    {
        var request = new RockScrapeRequest { Rock = Rock, RadiusMm = 1000, Rays = RockScrapeRequest.MaxRays };
        for (var i = 0; i < request.Rays; i++) request.Directions[i] = Vector3.UnitX;
        var writer = new NetWriter(new byte[RockScrapeRequest.MaxWireSize]);
        request.Write(ref writer);
        var bytes = writer.Written.ToArray();
        for (var length = 0; length < bytes.Length - 1; length++)
        {
            var reader = new NetReader(bytes.AsSpan(1, length));
            Assert.False(RockScrapeRequest.TryRead(ref reader, out _), $"{length} of {bytes.Length - 1} bytes were taken for a request");
        }
    }
}
