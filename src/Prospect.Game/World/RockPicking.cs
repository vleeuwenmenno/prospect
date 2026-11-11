using System.Numerics;

namespace Prospect.Game.World;

/// <summary>Window-independent ray intersection with the radial asteroid surface.</summary>
public static class RockPicking
{
    /// <summary>
    /// Returns the first surface crossing along a unit ray, in metres. Positions are relative to the camera.
    /// Samples at most 512 intervals across the bounding sphere and refines crossings to radius / 65536.
    /// Features thinner than radius / 250 along the ray can be missed, including grazing tangencies.
    /// </summary>
    public static bool Hit(Vector3 ray, Vector3 center, in Asteroid rock, out float distance)
    {
        distance = 0;
        var along = Vector3.Dot(center, ray);
        // The shape normalization samples its maximum, so allow a small margin around the nominal sphere.
        var radius = rock.Radius * 1.02f;
        var discriminant = radius * radius - (center.LengthSquared() - along * along);
        if (discriminant < 0)
            return false;
        var half = MathF.Sqrt(discriminant);
        var end = along + half;
        if (end < 0)
            return false;
        var start = Math.Max(0, along - half);
        var inverse = Quaternion.Conjugate(rock.Rotation);
        var origin = Vector3.Transform(-center, inverse);
        var direction = Vector3.Transform(ray, inverse);
        var previous = Surface(origin + direction * start, rock);
        var step = (end - start) / 512;
        for (var i = 1; i <= 512; i++)
        {
            var next = start + step * i;
            var value = Surface(origin + direction * next, rock);
            if ((previous <= 0) != (value <= 0))
            {
                var low = next - step;
                var high = next;
                for (var j = 0; j < 9; j++)
                {
                    var middle = (low + high) * 0.5f;
                    if ((Surface(origin + direction * middle, rock) <= 0) == (previous <= 0))
                        low = middle;
                    else
                        high = middle;
                }
                distance = (low + high) * 0.5f;
                return true;
            }
            previous = value;
        }
        return false;
    }

    private static float Surface(Vector3 point, in Asteroid rock)
    {
        var length = point.Length();
        return length < 1e-6f ? -rock.Radius : length - rock.Radius * AsteroidShape.RadiusAlong(rock.Shape, point / length);
    }
}
