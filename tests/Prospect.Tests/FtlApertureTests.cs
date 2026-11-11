using System.Numerics;
using Prospect.Game.World;
using Xunit;

namespace Prospect.Tests;

/// <summary>
/// The pilot's own rift or wormhole as the mesh pass cuts their hull with it and picking reads it back
/// (<see cref="FtlAperture"/>, mirroring <c>ftl_own.glsl</c>). Points are relative to the camera.
/// </summary>
public sealed class FtlApertureTests
{
    private const float PixelAngle = 0.001f;

    /// <summary>An opening of the given kind and stage (1 entry, 2 exit) at a camera-relative centre, facing +z.</summary>
    private static FtlAperture Opening(FtlKind kind, int stage, Vector3 centre, float radius, float past, Vector3 forward, float open = 1f)
    {
        var mouth = new Vector4(centre, radius);
        return new FtlAperture(new Vector4((float)kind, stage, 0.5f, 0.3f), new Vector4(0, 0, 1, 0), new Vector4(0, 1, 0, past),
            stage == 1 ? mouth : default, stage == 1 ? default : mouth, new Vector4(0, open, open, 0), Vector3.Normalize(forward));
    }

    [Fact]
    public void NoOpeningHidesNothing()
    {
        Assert.False(default(FtlAperture).Active);
        Assert.Equal(1f, default(FtlAperture).HullShows(new Vector3(3, 1, -2), PixelAngle));
    }

    [Fact]
    public void FromTheThroatTheWorldShowsThroughTheWholeSphereNotADiscAcrossIt()
    {
        // The wormhole at the origin, radius 1, the camera at (4, 0, 0.5) past its plane and in the throat. A point of
        // the hull on the world's side inside the sphere, at (0.5, 0, -0.1), is seen through the sphere's surface,
        // though the ray to it crosses the plane through the centre outside a disc of the sphere's radius.
        var camera = new Vector3(4, 0, 0.5f);
        var point = new Vector3(0.5f, 0, -0.1f) - camera;
        var opening = Opening(FtlKind.Wormhole, 1, -camera, 1f, 1f, point);
        Assert.True(opening.HullShows(point, PixelAngle) > 0.99f);
        // Beside the sphere, the same side of its plane, nothing of the world shows from the throat.
        Assert.True(opening.HullShows(new Vector3(0.5f, 3f, -0.1f) - camera, PixelAngle) < 0.01f);
    }

    [Fact]
    public void FromTheThroatTheWorldShowsOnlyInsideTheRiftsTornOutline()
    {
        // The camera in the throat at z = 4 looks back through the rift at z = 0; points beyond it at z = -1.
        var camera = new Vector3(0, 0, 4);
        var opening = Opening(FtlKind.Rift, 1, -camera, 2f, 1f, -Vector3.UnitZ);
        Assert.True(opening.HullShows(new Vector3(0.2f, 0.1f, -1) - camera, PixelAngle) > 0.99f);
        Assert.True(opening.HullShows(new Vector3(6, 0, -1) - camera, PixelAngle) < 0.01f);
        Assert.True(opening.HullShows(new Vector3(0, 6, -1) - camera, PixelAngle) < 0.01f);
    }

    [Theory]
    [InlineData(FtlKind.Rift)]
    [InlineData(FtlKind.Wormhole)]
    public void FromTheWorldNothingThroughTheOpeningShowsWhereverItIsSeen(FtlKind kind)
    {
        // Camera on the world's side, behind and beside: points past the entry are hidden whether or not the ray to
        // them passes through the window; points on the world's side, clear of the opening, show.
        foreach (var camera in (ReadOnlySpan<Vector3>)[new(0, 0.3f, -8), new(7, 0, -0.5f), new(-3, 4, -2)])
        {
            var opening = Opening(kind, 1, -camera, 1.5f, 0f, -camera);
            foreach (var point in (ReadOnlySpan<Vector3>)[new(0, 0, 0.5f), new(0.4f, 0.2f, 2f), new(3, 0, 1)])
                Assert.True(opening.HullShows(point - camera, PixelAngle) < 1e-6f, $"{kind}: {point} from {camera}");
            Assert.True(opening.HullShows(new Vector3(0, -5, -6) - camera, PixelAngle) > 0.99f);
        }
    }

    [Fact]
    public void AsTheCameraEasesIntoTheThroatWhatIsThereComesInSmoothly()
    {
        // A point of the hull through the entry, in front of the opening from the camera: it shows the square of how
        // far the camera is in, rising without a step from none to all.
        var camera = new Vector3(0, 2, 0.5f);
        var point = new Vector3(0, 0, 0.8f) - camera;
        var previous = 0f;
        for (var past = 0f; past <= 1.0001f; past += 0.05f)
        {
            var shows = Opening(FtlKind.Rift, 1, -camera, 1.5f, past, point).HullShows(point, PixelAngle);
            Assert.Equal(past * past, shows, 4);
            Assert.True(shows >= previous && shows - previous < 0.11f);
            previous = shows;
        }
    }

    [Fact]
    public void AtTheExitTheSidesSwap()
    {
        // Through the exit (-z of it) is the throat; past it (+z) the destination. With the camera out in the world,
        // the stern still in the throat is hidden and the nose out of it shows.
        var camera = new Vector3(3, 0, 3);
        var opening = Opening(FtlKind.Rift, 2, -camera, 1.5f, 1f, -camera);
        Assert.True(opening.HullShows(new Vector3(0, 0, -0.5f) - camera, PixelAngle) < 1e-6f);
        Assert.True(opening.HullShows(new Vector3(0, 0, 0.8f) - camera, PixelAngle) > 0.99f);
    }
}
