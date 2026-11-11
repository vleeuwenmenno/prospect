using System.Numerics;
using Prospect.Game.World;
using Xunit;

namespace Prospect.Tests;

/// <summary>
/// The pilot's own rift or wormhole as the mesh pass cuts their hull with it and picking reads it back
/// (<see cref="FtlAperture"/>, mirroring <c>ftl_own.glsl</c>). Points are relative to the camera; the opening's axis
/// is +z, so the throat is past z = 0 at the entry and before it at the exit.
/// </summary>
public sealed class FtlApertureTests
{
    private static readonly Vector4 Lens = new(0.5f, 16f / 9f, 1280, 720);

    /// <summary>An opening of the given kind and stage (1 entry, 2 exit) at a camera-relative centre, facing +z.</summary>
    internal static FtlAperture Opening(FtlKind kind, int stage, Vector3 centre, float radius, float past, Vector3 forward, float open = 1f)
    {
        var mouth = new Vector4(centre, radius);
        forward = Vector3.Normalize(forward);
        var right = Vector3.Normalize(Vector3.Cross(forward, MathF.Abs(forward.Y) > 0.9f ? Vector3.UnitX : Vector3.UnitY));
        var up = Vector3.Cross(right, forward);
        return new FtlAperture(new Vector4((float)kind, stage, 0.5f, 0.3f), new Vector4(0, 0, 1, 0), new Vector4(0, 1, 0, past),
            stage == 1 ? mouth : default, stage == 1 ? default : mouth, new Vector4(0, open, open, 0), forward, right, up, Lens);
    }

    private static float Shows(FtlAperture opening, Vector3 point) => opening.HullShows(point, opening.PixelAngle);

    [Fact]
    public void NoOpeningHidesNothing()
    {
        Assert.False(default(FtlAperture).Active);
        Assert.Equal(1f, default(FtlAperture).HullShows(new Vector3(3, 1, -2), 0.001f));
    }

    [Theory]
    [InlineData(FtlKind.Rift)]
    [InlineData(FtlKind.Wormhole)]
    public void FromTheWorldTheWindowHidesWhatIsBehindItAsItHidesARock(FtlKind kind)
    {
        // The case the review raised: the entry at the origin, the camera at (0.2, 0, 4), on the throat's side of the
        // plane but with the view on the world's side (the hull has not reached the opening). A stern point at
        // (0.2, 0, -0.5) has not gone in; the ray to it meets the opening first, inside its outline. The composite
        // shows the throat there (far = through = 1 at past 0, the same cover it lays over a rock behind an opening),
        // so the stern is hidden behind it, not cut: it has not passed the opening.
        var camera = new Vector3(0.2f, 0, 4);
        var opening = Opening(kind, 1, -camera, 1.5f, 0f, -Vector3.UnitZ);
        var stern = new Vector3(0.2f, 0, -0.5f) - camera;
        Assert.True(opening.WindowBefore(stern, opening.PixelAngle) > 0.99f);
        Assert.True(Shows(opening, stern) < 1e-6f);
        // The same stern outside the outline, or nearer the camera than the opening, is in plain view.
        Assert.Equal(1f, Shows(opening, new Vector3(6, 0, -0.5f) - camera));
        Assert.Equal(1f, Shows(opening, new Vector3(0.2f, 3.5f, -0.5f) - camera));
        // From the throat's side the window shows the world, and the stern through it.
        Assert.Equal(1f, Shows(Opening(kind, 1, -camera, 1.5f, 1f, -Vector3.UnitZ), stern), 3);
    }

    /// <summary>Whether the segment from the camera to a point meets a sphere.</summary>
    private static bool Meets(Vector3 point, Vector3 centre, float radius)
    {
        var t = Math.Clamp(Vector3.Dot(centre, point) / point.LengthSquared(), 0f, 1f);
        return (point * t - centre).Length() <= radius;
    }

    [Theory]
    [InlineData(FtlKind.Rift)]
    [InlineData(FtlKind.Wormhole)]
    public void APointNotYetInIsHiddenOnlyWhereTheOpeningIsInFrontOfIt(FtlKind kind)
    {
        // Cameras ahead, beside and behind the opening, with the view on either side of it and between: a point of the
        // hull still in the world shows in full unless the segment to it passes the opening. Not where the view has
        // dissolved into the throat around it, which is what cut a stern away before it had gone in.
        const float Radius = 1.5f;
        // Nothing of a rift's torn outline or a wormhole reaches further than this from the centre.
        var reach = kind == FtlKind.Rift ? Radius * 2.2f : Radius;
        var checkedPoints = 0;
        foreach (var camera in (ReadOnlySpan<Vector3>)[new(0, 0.5f, 6), new(7, 0, 0.3f), new(-2, 5, 2), new(0.4f, 0, -8), new(6, -3, -1)])
        {
            foreach (var past in (ReadOnlySpan<float>)[0f, 0.3f, 0.7f, 1f])
            {
                foreach (var stage in (ReadOnlySpan<int>)[1, 2])
                {
                    var opening = Opening(kind, stage, -camera, Radius, past, -camera);
                    for (var x = -6f; x <= 6f; x += 1.5f)
                    {
                        for (var z = -6f; z <= 6f; z += 0.75f)
                        {
                            var world = new Vector3(x, 0.3f, z);
                            var inWorld = stage == 1 ? world.Z < 0f : world.Z > 0f;
                            if (!inWorld || Meets(world - camera, -camera, reach))
                                continue;
                            Assert.Equal(1f, Shows(opening, world - camera));
                            checkedPoints++;
                        }
                    }
                }
            }
        }

        Assert.True(checkedPoints > 500, $"{checkedPoints} points");
    }

    [Theory]
    [InlineData(FtlKind.Rift, 1)]
    [InlineData(FtlKind.Rift, 2)]
    [InlineData(FtlKind.Wormhole, 1)]
    [InlineData(FtlKind.Wormhole, 2)]
    public void APointThroughTheOpeningShowsOnlyAsTheViewIsInTheThroat(FtlKind kind, int stage)
    {
        // From the world nothing through the opening shows, from any side; with the view in the throat, what is in
        // the throat shows where the window is not in front of it.
        foreach (var camera in (ReadOnlySpan<Vector3>)[new(0, 0.5f, 6), new(7, 0, 0.3f), new(0.4f, 0, -8)])
        {
            foreach (var throat in (ReadOnlySpan<Vector3>)[new(0.3f, 0.2f, 0.5f), new(3, 0, 2), new(-1, 1, 4)])
            {
                var point = (stage == 1 ? throat : throat with { Z = -throat.Z }) - camera;
                var worldView = stage == 1 ? 0f : 1f;
                Assert.True(Shows(Opening(kind, stage, -camera, 1.5f, worldView, -camera), point) < 1e-6f, $"{kind} {stage}: {throat} from {camera}");
                var inside = Opening(kind, stage, -camera, 1.5f, 1f - worldView, -camera);
                Assert.Equal(1f - inside.WindowBefore(point, inside.PixelAngle), Shows(inside, point), 5);
            }
        }
    }

    [Fact]
    public void AsTheViewEasesIntoTheThroatWhatIsThereComesInSmoothly()
    {
        // A point of the hull through the entry, the opening behind the camera: it shows as far as the view is in the
        // throat, rising without a step from none to all.
        var camera = new Vector3(0, 2, 0.5f);
        var point = new Vector3(0, 0, 0.8f) - camera;
        var previous = 0f;
        for (var past = 0f; past <= 1.0001f; past += 0.05f)
        {
            var shows = Shows(Opening(FtlKind.Rift, 1, -camera, 1.5f, past, point), point);
            Assert.Equal(Math.Min(past, 1f), shows, 4);
            Assert.True(shows >= previous && shows - previous < 0.06f);
            previous = shows;
        }
    }

    [Fact]
    public void ThePixelGridIsTheViewports()
    {
        // A ray through a pixel lands back in it, and the dither pattern is the mesh pass's, from 0 to 1.
        var opening = Opening(FtlKind.Rift, 1, new Vector3(0, 0, -20), 1.5f, 0f, new Vector3(0.1f, -0.2f, -1f));
        foreach (var pixel in (ReadOnlySpan<Vector2>)[new(0.5f, 0.5f), new(640.5f, 360.5f), new(1279.5f, 719.5f), new(17.5f, 503.5f)])
        {
            Assert.True((opening.PixelOf(opening.RayThrough(pixel)) - pixel).Length() < 1e-2f);
            Assert.InRange(FtlAperture.Pattern(pixel), 0f, 1f);
        }
    }
}
