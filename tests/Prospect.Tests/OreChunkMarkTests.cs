using System.Numerics;
using Brutal.Numerics;
using Prospect.Client.World;
using Prospect.Engine.IO;
using Xunit;

namespace Prospect.Tests;

/// <summary>
/// The marks on loose ore chunks without a window: the shipped look and its refusals, the fade and cull by distance,
/// where a chunk lands on screen, edge arrows for those off it, the near count for the DRONES chip, and which chunk the
/// pointer is on.
/// </summary>
public sealed class OreChunkMarkTests
{
    private static readonly float2 Screen = new(1600, 900);
    private const float Tan = 0.5f;

    private static OreMarker Chunk(Vector3 view, uint id = 1, float own = 10, float half = 1)
    {
        var marker = new OreMarker { Id = id, View = view, OwnDistance = own };
        OreChunkMarks.Place(ref marker, half, Tan, Screen, 0.25f);
        return marker;
    }

    private static string Scratch(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), "ore_chunks_" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, json);
        return path;
    }

    [Fact]
    public void TheShippedLookLoadsAndIsInOrder()
    {
        var look = OreChunkLook.Load(AppPaths.Content("visuals", "ore_chunks.json"));
        Assert.True(look.FadeStartMetres < look.FadeEndMetres && look.FadeEndMetres <= look.CullMetres);
        Assert.True(look.NearMetres > 0 && look.MinAlpha > 0);
        // A mark whose chunk the bins cannot take is dimmed and hollow, and struck through.
        Assert.True(look.BlockedAlpha < 1f && look.BlockedFillAlpha == 0f && look.BlockedMarkPixels > 0);
        Assert.Equal(0xFF5A52u, look.BlockedRgb);
    }

    [Theory]
    [InlineData("""{ "fadeStartMetres": 500, "fadeEndMetres": 400 }""")]
    [InlineData("""{ "fadeEndMetres": 9000, "cullMetres": 6000 }""")]
    [InlineData("""{ "minAlpha": 0 }""")]
    [InlineData("""{ "markerPixels": 0 }""")]
    [InlineData("""{ "nearMetres": -1 }""")]
    [InlineData("""{ "hoverPixels": 500 }""")]
    [InlineData("""{ "blockedAlpha": 0 }""")]
    [InlineData("""{ "blockedFillAlpha": 2 }""")]
    [InlineData("""{ "blockedMarkPixels": 0 }""")]
    [InlineData("""{ "blockedColor": "red" }""")]
    public void ALookOutOfRangeIsRefused(string json)
    {
        var path = Scratch(json);
        try { Assert.Throws<InvalidDataException>(() => OreChunkLook.Load(path)); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void AMarkIsFullNearFadesInALineAndIsGoneBeyondTheCull()
    {
        var look = new OreChunkLook { FadeStartMetres = 100, FadeEndMetres = 1100, MinAlpha = 0.2f, CullMetres = 2000 };
        Assert.Equal(1f, OreChunkMarks.Alpha(0, look));
        Assert.Equal(1f, OreChunkMarks.Alpha(100, look));
        Assert.Equal(0.6f, OreChunkMarks.Alpha(600, look), 4);
        Assert.Equal(0.2f, OreChunkMarks.Alpha(1100, look), 4);
        Assert.Equal(0.2f, OreChunkMarks.Alpha(2000, look), 4);
        Assert.Equal(0f, OreChunkMarks.Alpha(2001, look));
        Assert.Equal(0f, OreChunkMarks.Alpha(float.NaN, look));
        Assert.Equal(0f, OreChunkMarks.Alpha(float.PositiveInfinity, look));
    }

    [Fact]
    public void AChunkAheadLandsOnScreenAndOneBehindOrBesideDoesNot()
    {
        var ahead = Chunk(new Vector3(0, 0, 50));
        Assert.True(ahead.OnScreen);
        Assert.Equal(800f, ahead.Screen.X, 2);
        Assert.Equal(450f, ahead.Screen.Y, 2);
        Assert.Equal(50f, ahead.Distance, 3);
        // Radius 1 m at 50 m: 1 / (50 * 0.5) of the half height, 450 px.
        Assert.Equal(18f, ahead.PixelRadius, 2);
        Assert.False(Chunk(new Vector3(0, 0, -50)).OnScreen);
        Assert.False(Chunk(new Vector3(0, 0, 0.1f)).OnScreen);
        Assert.False(Chunk(new Vector3(500, 0, 50)).OnScreen);
        // Up is up the screen: a smaller y.
        Assert.True(Chunk(new Vector3(0, 10, 50)).Screen.Y < 450f);
    }

    [Fact]
    public void TheNearCountUsesTheDistanceFromTheShipNotTheCamera()
    {
        var look = new OreChunkLook { NearMetres = 500 };
        ReadOnlySpan<OreMarker> none = [];
        Assert.Equal(0, OreChunkMarks.CountNear(none, look));
        OreMarker[] markers = [Chunk(new Vector3(0, 0, 5000), 1, 499), Chunk(new Vector3(0, 0, 5), 2, 500), Chunk(new Vector3(0, 0, 5), 3, 501)];
        Assert.Equal(2, OreChunkMarks.CountNear(markers, look));
    }

    [Fact]
    public void ThePointerPicksTheNearestChunkItIsOnAndNothingElse()
    {
        var look = new OreChunkLook { HoverPixels = 14, CullMetres = 1000 };
        OreMarker[] markers =
        [
            Chunk(new Vector3(0, 0, 100), 1),
            Chunk(new Vector3(0, 0, 40), 2),
            Chunk(new Vector3(100, 0, 100), 3),
            Chunk(new Vector3(0, 0, -50), 4),
        ];
        Assert.Equal(1, OreChunkMarks.Pick(markers, new float2(805, 455), look, 1f));
        Assert.Equal(-1, OreChunkMarks.Pick(markers, new float2(600, 700), look, 1f));
        // Just outside the grab radius misses; the scale widens it.
        Assert.Equal(0, OreChunkMarks.Pick(markers.AsSpan(0, 1), new float2(800 + 14, 450), look, 1f));
        Assert.Equal(-1, OreChunkMarks.Pick(markers.AsSpan(0, 1), new float2(800 + 20, 450), look, 1f));
        Assert.Equal(0, OreChunkMarks.Pick(markers.AsSpan(0, 1), new float2(800 + 20, 450), look, 2f));
        // A chunk past the cull is never picked.
        var far = new OreChunkLook { HoverPixels = 14, CullMetres = 30 };
        Assert.Equal(-1, OreChunkMarks.Pick(markers.AsSpan(0, 1), new float2(800, 450), far, 1f));
        // A chunk big on screen is picked over its whole drawn size.
        OreMarker[] big = [Chunk(new Vector3(0, 0, 4), 9, 10, 1)];
        Assert.True(big[0].PixelRadius > 100);
        Assert.Equal(0, OreChunkMarks.Pick(big, new float2(800 + 90, 450), look, 1f));
    }

    [Fact]
    public void TheDiamondFramesABigChunkAndKeepsItsSizeOtherwise()
    {
        var look = new OreChunkLook { MarkerPixels = 7, FramePaddingPixels = 3 };
        Assert.Equal(7f, OreChunkMarks.DiamondHalf(Chunk(new Vector3(0, 0, 500)), look, 1f));
        var near = Chunk(new Vector3(0, 0, 4));
        Assert.True(OreChunkMarks.DiamondHalf(near, look, 1f) > near.PixelRadius);
    }

    [Fact]
    public void OffScreenChunksGatherByDirectionAndTheArrowSitsAtTheEdge()
    {
        var arrows = new OreEdgeArrows();
        arrows.Clear();
        // Two to the right (one nearer), one straight behind, one on screen that is ignored.
        arrows.Add(Chunk(new Vector3(500, 0, 50), 1, 10));
        arrows.Add(Chunk(new Vector3(300, 0, 50), 2, 10));
        arrows.Add(Chunk(new Vector3(0, 0, -80), 3, 10));
        arrows.Add(Chunk(new Vector3(0, 0, 50), 4, 10));
        var right = OreEdgeArrows.SectorOf(new float2(1, 0));
        Assert.Equal(2, arrows.CountOf(right));
        Assert.Equal(1f, arrows.DirectionOf(right).X, 3);
        var behind = OreEdgeArrows.SectorOf(new float2(0, -1));
        Assert.Equal(1, arrows.CountOf(behind));
        var total = 0;
        for (var s = 0; s < OreEdgeArrows.Sectors; s++) total += arrows.CountOf(s);
        Assert.Equal(3, total);
        // To the right it sits on the margin line; straight up, on the top one.
        var at = arrows.Place(right, Screen, 40);
        Assert.Equal(Screen.X - 40, at.X, 2);
        Assert.Equal(450f, at.Y, 2);
        var top = arrows.Place(behind, Screen, 40);
        Assert.Equal(40f, top.Y, 2);
        Assert.Equal(800f, top.X, 2);
        arrows.Clear();
        Assert.Equal(0, arrows.CountOf(right));
    }

    [Fact]
    public void EverySectorIsReachedAndOppositeDirectionsDiffer()
    {
        var seen = new HashSet<int>();
        for (var d = 0; d < 360; d += 5)
        {
            var angle = d * MathF.PI / 180f;
            seen.Add(OreEdgeArrows.SectorOf(new float2(MathF.Cos(angle), MathF.Sin(angle))));
        }

        Assert.Equal(OreEdgeArrows.Sectors, seen.Count);
        Assert.NotEqual(OreEdgeArrows.SectorOf(new float2(1, 0)), OreEdgeArrows.SectorOf(new float2(-1, 0)));
    }

    [Fact]
    public void PlacingFadingPickingAndGatheringTakeNoMemory()
    {
        var look = OreChunkLook.Current;
        var markers = new OreMarker[Capacity];
        for (var i = 0; i < markers.Length; i++)
            markers[i] = new OreMarker { Id = (uint)i + 1, View = new Vector3(i * 3 - 380, i - 120, i % 5 == 0 ? -40 - i : 20 + i), OwnDistance = i * 4 };
        var arrows = new OreEdgeArrows();
        var sink = 0f;

        void Run()
        {
            arrows.Clear();
            for (var i = 0; i < markers.Length; i++)
            {
                OreChunkMarks.Place(ref markers[i], 1.2f, Tan, Screen, 0.25f);
                sink += OreChunkMarks.Alpha(markers[i].Distance, look) + OreChunkMarks.DiamondHalf(markers[i], look, 1f);
                arrows.Add(markers[i]);
            }

            sink += OreChunkMarks.CountNear(markers, look) + OreChunkMarks.Pick(markers, new float2(800, 450), look, 1f);
            for (var s = 0; s < OreEdgeArrows.Sectors; s++)
                if (arrows.CountOf(s) > 0) sink += arrows.Place(s, Screen, 40).X;
        }

        for (var i = 0; i < 100; i++) Run();
        var allocation = new AllocationProbe();
        for (var i = 0; i < 100; i++)
            while (allocation.Measuring()) Run();
        Assert.Equal(0, allocation.Bytes);
        Assert.True(float.IsFinite(sink));
    }

    private const int Capacity = 256;
}
