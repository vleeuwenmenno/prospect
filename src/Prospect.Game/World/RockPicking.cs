using System.Numerics;

namespace Prospect.Game.World;

/// <summary>Window-independent ray intersection with the radial asteroid surface.</summary>
public static class RockPicking
{
    /// <summary>
    /// Returns the first surface crossing along a unit ray, in metres. Positions are relative to the camera.
    /// Brackets crossings and local minima over 512 intervals, then refines to radius / 65536.
    /// Grazing contact is accepted within radius / 65536 metres of the radial surface.
    /// </summary>
    public static bool Hit(Vector3 ray, Vector3 center, in Asteroid rock, out float distance)
    {
        distance = 0;
        var along = Vector3.Dot(center, ray);
        // The shape normalization samples its maximum, so allow a small margin around the nominal sphere.
        var radius = rock.Radius * 1.02f;
        var discriminant = radius * radius - Vector3.Cross(center, ray).LengthSquared();
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
        var beforePrevious = float.PositiveInfinity;
        var tolerance = rock.Radius / 65536f;
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
            // A tangent need not change sign, so refine every sampled valley, not just crossings.
            if (previous > 0 && previous < beforePrevious && previous <= value)
            {
                var low = Math.Max(start, next - 2 * step);
                var high = next;
                var left = low;
                for (var j = 0; j < 28; j++)
                {
                    var a = low + (high - low) / 3;
                    var b = high - (high - low) / 3;
                    if (Surface(origin + direction * a, rock) < Surface(origin + direction * b, rock)) high = b;
                    else low = a;
                }
                var minimum = (low + high) * 0.5f;
                if (Surface(origin + direction * minimum, rock) <= tolerance)
                {
                    high = minimum;
                    low = left;
                    for (var j = 0; j < 16; j++)
                    {
                        var middle = (low + high) * 0.5f;
                        if (Surface(origin + direction * middle, rock) > tolerance) low = middle;
                        else high = middle;
                    }
                    distance = high;
                    return true;
                }
            }
            beforePrevious = previous;
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
