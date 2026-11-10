using System.Numerics;

namespace Prospect.Game.World;

/// <summary>
/// The shapes asteroids come in. Each is a sphere pushed in and out by layered noise, defined as a function of
/// direction, so the mesh the client draws and the surface the simulation collides with are the same thing.
/// </summary>
public static class AsteroidShape
{
    private static readonly float[] Scales = ComputeScales();

    /// <summary>The surface point of a shape in a direction, for a rock of unit radius.</summary>
    public static Vector3 Point(int shape, Vector3 direction) => direction * Squash(shape) * (Lumps(shape, direction) * Scales[shape]);

    /// <summary>
    /// How far the surface is from the centre along a unit direction given in the rock's own frame, as a fraction
    /// of the rock's nominal radius.
    /// </summary>
    public static float RadiusAlong(int shape, Vector3 direction)
    {
        // The shape is defined before squashing; undo the squash to find which part of it lies this way.
        var unsquashed = direction / Squash(shape);
        var length = unsquashed.Length();
        return Lumps(shape, unsquashed / length) * Scales[shape] / length;
    }

    private static Vector3 Squash(int shape)
    {
        var seed = shape + 1;
        return new Vector3(1f + 0.35f * MathF.Sin(seed * 1.7f), 1f + 0.3f * MathF.Sin(seed * 2.9f + 1f), 1f + 0.35f * MathF.Sin(seed * 4.1f + 2f));
    }

    private static float Lumps(int shape, Vector3 direction)
    {
        var seed = shape + 1;
        var offset = new Vector3(seed * 12.9898f % 97f, seed * 78.233f % 89f, seed * 37.719f % 83f);
        var broad = Noise.Fractal(direction * 1.3f + offset, 4);
        var ridges = 1f - MathF.Abs(Noise.Fractal(direction * 3.1f + offset * 1.7f, 3) * 2f - 1f);
        return 0.62f + 0.42f * broad + 0.1f * ridges * ridges;
    }

    /// <summary>Per shape, the factor that makes its furthest point sit at the nominal radius.</summary>
    private static float[] ComputeScales()
    {
        const int samples = 4000;
        var scales = new float[Galaxy.AsteroidShapes];
        var golden = MathF.PI * (3f - MathF.Sqrt(5f));
        for (var shape = 0; shape < scales.Length; shape++)
        {
            var largest = 0f;
            for (var i = 0; i < samples; i++)
            {
                var y = 1f - 2f * (i + 0.5f) / samples;
                var ring = MathF.Sqrt(1f - y * y);
                var direction = new Vector3(MathF.Cos(golden * i) * ring, y, MathF.Sin(golden * i) * ring);
                largest = Math.Max(largest, (direction * Squash(shape) * Lumps(shape, direction)).Length());
            }

            scales[shape] = 1f / largest;
        }

        return scales;
    }
}

/// <summary>Value noise for shaping rocks and painting the galaxy.</summary>
public static class Noise
{
    public static float Fractal(Vector3 p, int octaves)
    {
        var sum = 0f;
        var amplitude = 0.5f;
        var total = 0f;
        for (var i = 0; i < octaves; i++)
        {
            sum += amplitude * Value(p);
            total += amplitude;
            p = p * 2.07f + new Vector3(13.1f, 7.7f, 29.3f);
            amplitude *= 0.5f;
        }

        return sum / total;
    }

    private static float Value(Vector3 p)
    {
        var cell = new Vector3(MathF.Floor(p.X), MathF.Floor(p.Y), MathF.Floor(p.Z));
        var f = p - cell;
        f = f * f * (new Vector3(3f) - 2f * f);
        var (x, y, z) = ((int)cell.X, (int)cell.Y, (int)cell.Z);

        static float Lerp(float a, float b, float t) => a + (b - a) * t;
        return Lerp(
            Lerp(Lerp(Hash(x, y, z), Hash(x + 1, y, z), f.X), Lerp(Hash(x, y + 1, z), Hash(x + 1, y + 1, z), f.X), f.Y),
            Lerp(Lerp(Hash(x, y, z + 1), Hash(x + 1, y, z + 1), f.X), Lerp(Hash(x, y + 1, z + 1), Hash(x + 1, y + 1, z + 1), f.X), f.Y),
            f.Z
        );
    }

    private static float Hash(int x, int y, int z)
    {
        var h = (uint)(x * 374761393 + y * 668265263 + z * 1274126177);
        h = (h ^ h >> 13) * 1274126177;
        return ((h ^ h >> 16) & 0xFFFFFF) / (float)0x1000000;
    }
}
