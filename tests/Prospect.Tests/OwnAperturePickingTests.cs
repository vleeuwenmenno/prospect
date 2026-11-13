using System.Numerics;
using Brutal.Numerics;
using Prospect.Game.World;
using Xunit;

namespace Prospect.Tests;

/// <summary>
/// Picking the pilot's own hull while its rift or wormhole cuts it, as the mesh pass draws it: sample by sample at the
/// viewport's pixels, with the twelve-pixel grab measured to drawn samples, edges the opening makes inside a face
/// included. One block ten metres across, its front face 25 m ahead of the camera, which looks along -z.
/// </summary>
public sealed class OwnAperturePickingTests
{
    private static readonly Vector3 Forward = -Vector3.UnitZ;
    private const float PixelsPerTangent = 720f / (2f * 0.5f);
    private static readonly Double3 Centre = new(0, 0, -30);

    /// <summary>A rift entry across x at <paramref name="x"/>, the throat past it along +x.</summary>
    private static FtlAperture Entry(float x, float past) =>
        new(new Vector4((float)FtlKind.Rift, 1, 0.5f, 0.3f), new Vector4(1, 0, 0, 0), new Vector4(0, 1, 0, past),
            new Vector4(x, 0, -30, 2f), default, new Vector4(0, 1, 1, 0), Forward, Vector3.UnitX, Vector3.UnitY,
            new Vector4(0.5f, 16f / 9f, 1280, 720));

    private static PickHull Hull(FtlAperture aperture) =>
        new(7, Centre, Quaternion.Identity, [Vector3.Zero], 1f, BlockMasks.Set(1), 8.7f, Aperture: aperture, HalfExtents: [new Vector3(5f)]);

    private static PickHit? Pick(FtlAperture aperture, Vector2 pixel)
    {
        Span<PickHit> hits = stackalloc PickHit[4];
        var count = TargetPicking.Pick(aperture.RayThrough(pixel), default, Forward, PixelsPerTangent, [Hull(aperture)], [], hits);
        return count == 0 ? null : hits[0];
    }

    /// <summary>The distance along the ray through a pixel to the block's front face, at z = -25.</summary>
    private static float FaceDistance(FtlAperture aperture, Vector2 pixel) => 25f / -aperture.RayThrough(pixel).Z;

    [Fact]
    public void TheGrabReachesAnEdgeTheOpeningMakesInsideAFace()
    {
        // The entry crosses the front face at x = 1: the face is drawn left of it and gone right of it. 25 m away a
        // metre is 28.8 pixels, so the face's own edges are 110 and 144 pixels from the cut.
        var aperture = Entry(1f, 0f);
        var cut = aperture.PixelOf(new Vector3(1, 0, -25));
        var edges = new[] { aperture.PixelOf(new Vector3(5, 0, -25)), aperture.PixelOf(new Vector3(1, 5, -25)) };
        Assert.True((edges[0] - cut).Length() > 100f && (edges[1] - cut).Length() > 100f);

        // Five pixels past the cut nothing of the hull is drawn under the pointer, but the drawn face is within the
        // grab, and it is picked there, a sample's distance away.
        var near = new Vector2(MathF.Floor(cut.X) + 5.5f, MathF.Floor(cut.Y) + 0.5f);
        Assert.False(aperture.Drawn(aperture.RayThrough(near) * FaceDistance(aperture, near), near));
        var hit = Pick(aperture, near);
        Assert.NotNull(hit);
        Assert.Equal(7u, hit.Value.Hull);
        Assert.True(MathF.Abs(hit.Value.Distance - FaceDistance(aperture, near)) < 0.05f);

        // Twenty pixels past it, out of the grab, nothing is: the hidden part is not a surface to grab.
        Assert.Null(Pick(aperture, near + new Vector2(15, 0)));
        // Left of the cut the face is drawn and picked where the pointer is.
        var drawn = near - new Vector2(10, 0);
        Assert.Equal(FaceDistance(aperture, drawn), Pick(aperture, drawn)!.Value.Distance, 3);
    }

    [Fact]
    public void ADissolvingSurfaceIsPickedWhereItsSamplesAreDrawn()
    {
        // The whole face is through the entry (at x = -10) and the view is 30% into the throat: the mesh pass keeps
        // the pixels whose pattern is under 0.3, about 30% of them. Each of those is picked under the pointer, exactly
        // at the face; between them the grab finds one; with the view still on the world's side, none is drawn.
        var aperture = Entry(-10f, 0.3f);
        var region = aperture.PixelOf(new Vector3(-2, -2, -25));
        int drawn = 0, total = 0;
        for (var y = 0; y < 40; y++)
        {
            for (var x = 0; x < 40; x++)
            {
                var pixel = new Vector2(MathF.Floor(region.X) + x + 0.5f, MathF.Floor(region.Y) - y + 0.5f);
                var point = aperture.RayThrough(pixel) * FaceDistance(aperture, pixel);
                var shown = aperture.Drawn(point, pixel);
                Assert.Equal(FtlAperture.Pattern(pixel) < 0.3f, shown);
                var hit = Pick(aperture, pixel);
                Assert.NotNull(hit);
                if (shown)
                    Assert.Equal(FaceDistance(aperture, pixel), hit.Value.Distance, 3);
                drawn += shown ? 1 : 0;
                total++;
            }
        }

        Assert.InRange(drawn / (float)total, 0.2f, 0.4f);
        Assert.Null(Pick(Entry(-10f, 0f), region));
    }
}
