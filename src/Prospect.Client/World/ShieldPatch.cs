using System.Numerics;
using Prospect.Game.World;

namespace Prospect.Client.World;

/// <summary>A bounded tangent patch, expressed in the struck hull's frame.</summary>
internal readonly record struct ShieldPatch(Vector3 Normal, float Extent, float Band)
{
    /// <summary>Samples the shared rock surface around the hit to find its tangent.</summary>
    public static ShieldPatch Rock(in Asteroid rock, Vector3 fromCenter, Quaternion hullRotation, float hullRadius, float band)
    {
        var inverse = Quaternion.Conjugate(rock.Rotation);
        var p = Vector3.Transform(fromCenter, inverse);
        var step = Math.Max(band, rock.Radius * 0.0001f);
        var normal = Vector3.Transform(AsteroidShape.Normal(rock.Shape, p, rock.Radius, step), rock.Rotation);
        return new ShieldPatch(Vector3.Transform(normal, Quaternion.Conjugate(hullRotation)), Math.Min(2f * hullRadius, MathF.Sqrt(2f * rock.Radius * band)), band);
    }

    /// <summary>Nearest outward face, including a hit quantized onto or just inside a box.</summary>
    public static Vector3 BlockNormal(Vector3 point, Vector3 half, BlockShape shape)
    {
        var offset = point - BlockShapes.Closest(shape, half, point);
        if (offset.LengthSquared() > 1e-12f) return Vector3.Normalize(offset);
        var gap = half - Vector3.Abs(point);
        var axis = gap.X <= gap.Y && gap.X <= gap.Z ? 0 : gap.Y <= gap.Z ? 1 : 2;
        var normal = Vector3.Zero;
        normal[axis] = point[axis] < 0 ? -1f : 1f;
        if (shape != BlockShape.Box)
        {
            var slant = Vector3.Normalize(BlockShapes.Slant(shape, half));
            if (-Vector3.Dot(slant, point) < gap[axis]) normal = slant;
        }
        return normal;
    }

    /// <summary>Signed radial distance, using the same shape as collision and drawing.</summary>
    public static float Distance(in Asteroid rock, Vector3 local)
    {
        var length = local.Length();
        return length - rock.Radius * AsteroidShape.RadiusAlong(rock.Shape, length > 1e-6f ? local / length : Vector3.UnitY);
    }
}
