using System.Numerics;
using Prospect.Game.World;
using Xunit;

namespace Prospect.Tests;

public sealed class RockPickingTests
{
    [Fact]
    public void CenterRayHitsSurfaceAndDisplacedRockMisses()
    {
        var rock = new Asteroid { Radius = 3000, Rotation = Quaternion.Identity, Shape = 0 };
        Assert.True(RockPicking.Hit(Vector3.UnitZ, new Vector3(0, 0, 6000), rock, out var distance));
        var expected = 6000 - 3000 * AsteroidShape.RadiusAlong(0, -Vector3.UnitZ);
        Assert.InRange(Math.Abs(distance - expected), 0, 0.05f);
        Assert.False(RockPicking.Hit(Vector3.UnitZ, new Vector3(6000, 0, 6000), rock, out _));
    }

    [Fact]
    public void CameraInsideBoundsCanLookAwayFromSurface()
    {
        var rock = new Asteroid { Radius = 3000, Rotation = Quaternion.Identity, Shape = 0 };
        var surface = rock.Radius * AsteroidShape.RadiusAlong(0, Vector3.UnitX);
        var camera = new Vector3((surface + rock.Radius) * 0.5f, 0, 0);
        Assert.False(RockPicking.Hit(Vector3.UnitX, -camera, rock, out _));
        Assert.True(RockPicking.Hit(-Vector3.UnitX, -camera, rock, out _));
    }
}
