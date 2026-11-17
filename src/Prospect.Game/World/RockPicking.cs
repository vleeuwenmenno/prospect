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
    public static bool Hit(Vector3 ray, Vector3 center, in Asteroid rock, out float distance) => Hit(ray, center, rock, default, float.PositiveInfinity, out distance);

    /// <summary>
    /// The same against a scraped rock, no farther than <paramref name="range"/> metres along the ray: the surface is
    /// the base shape less its cuts. A crossing bracketed by the coarse intervals is walked at half a cell of the cut
    /// field, so the first surface behind a groove's opening is found, not one beyond it.
    /// </summary>
    public static bool Hit(Vector3 ray, Vector3 center, in Asteroid rock, RockEditing.RockCut cut, float range, out float distance)
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
        end = Math.Min(end, range);
        if (end <= start)
            return false;
        var inverse = Quaternion.Conjugate(rock.Rotation);
        var origin = Vector3.Transform(-center, inverse);
        var direction = Vector3.Transform(ray, inverse);
        var previous = Surface(origin + direction * start, rock, cut);
        var step = (end - start) / 512;
        var beforePrevious = float.PositiveInfinity;
        var tolerance = rock.Radius / 65536f;
        for (var i = 1; i <= 512; i++)
        {
            var next = start + step * i;
            var value = Surface(origin + direction * next, rock, cut);
            if ((previous <= 0) != (value <= 0))
            {
                var low = next - step;
                var high = next;
                if (cut.Any)
                {
                    // The interval may hold a groove: find the first change of side in it, half a cell at a time.
                    var fine = Math.Max(cut.CellMetres * 0.5f, step / 4096f);
                    for (var at = low + fine; at < high; at += fine)
                    {
                        if ((Surface(origin + direction * at, rock, cut) <= 0) == (previous <= 0))
                        {
                            low = at;
                            continue;
                        }

                        high = at;
                        break;
                    }
                }

                for (var j = 0; j < 9; j++)
                {
                    var middle = (low + high) * 0.5f;
                    if ((Surface(origin + direction * middle, rock, cut) <= 0) == (previous <= 0))
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
                    if (Surface(origin + direction * a, rock, cut) < Surface(origin + direction * b, rock, cut)) high = b;
                    else low = a;
                }
                var minimum = (low + high) * 0.5f;
                if (Surface(origin + direction * minimum, rock, cut) <= tolerance)
                {
                    high = minimum;
                    low = left;
                    for (var j = 0; j < 16; j++)
                    {
                        var middle = (low + high) * 0.5f;
                        if (Surface(origin + direction * middle, rock, cut) > tolerance) low = middle;
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

    /// <summary>
    /// Which side of a rock's surface a point of its own frame is on: negative in rock, positive outside it or
    /// inside a cut. Radial for the base shape, the cut field's own distance inside a cut.
    /// </summary>
    public static float Surface(Vector3 point, in Asteroid rock, RockEditing.RockCut cut = default)
    {
        var length = point.Length();
        var surface = length < 1e-6f ? -rock.Radius : length - rock.Radius * AsteroidShape.RadiusAlong(rock.Shape, point / length);
        if (!cut.Any) return surface;

        // Outside the base shape too: over a groove's opening the value must not fall to zero at the surface that
        // is no longer there, or a ray into the groove would be taken to graze it and stop at the lip.
        return Math.Max(surface, -cut.Sample(point, out _));
    }
}
