using System.Numerics;

namespace Prospect.Game.World;

/// <summary>
/// The outline of every rock shape as a rock's interior sees it: how far the surface is from the centre in each
/// direction, and a slow noise that wobbles the crust's depth, on a cube map per shape. It follows
/// <see cref="AsteroidShape"/> but is built from arithmetic that comes out the same to the last bit on every machine
/// (<see cref="DetMath"/> for the squash, square roots, plain sums), because which layer a point lies in is a
/// discrete choice. It therefore differs from the drawn facets by about a percent of the radius at most; the
/// crust is far thicker than that.
/// </summary>
public static class RockInteriorSurface
{
    /// <summary>Cells along the edge of each cube face. Nodes sit on the cell corners.</summary>
    public const int Cells = 32;

    private const int Nodes = Cells + 1;
    private const int NodesPerFace = Nodes * Nodes;

    // Per shape: two floats a node (surface fraction, wobble), six faces.
    private static readonly float[][] Tables;

    // Per shape: the mean over the sphere of s cubed, and the means of s cubed times the wobble to the first,
    // second and third power divided by it. They turn the crust's depth into its share of the rock's volume.
    private static readonly Vector4[] Moments;

    static RockInteriorSurface()
    {
        Tables = new float[Galaxy.AsteroidShapes][];
        Moments = new Vector4[Galaxy.AsteroidShapes];
        for (var shape = 0; shape < Galaxy.AsteroidShapes; shape++)
            Build(shape);
    }

    /// <summary>Builds the tables, so the first query does not pay for it.</summary>
    public static void Prepare() => _ = Tables.Length;

    /// <summary>
    /// The surface's distance from the centre along a unit direction in the rock's own frame, as a fraction of the
    /// rock's radius, and the crust wobble there, from -1 to 1. Bilinear between the nodes; takes no memory.
    /// </summary>
    public static void Lookup(int shape, float x, float y, float z, out float surface, out float wobble)
    {
        var ax = Math.Abs(x);
        var ay = Math.Abs(y);
        var az = Math.Abs(z);
        int face;
        float major, u, v;
        if (ax >= ay && ax >= az)
        {
            face = x >= 0 ? 0 : 1;
            major = ax;
            u = y;
            v = z;
        }
        else if (ay >= az)
        {
            face = y >= 0 ? 2 : 3;
            major = ay;
            u = x;
            v = z;
        }
        else
        {
            face = z >= 0 ? 4 : 5;
            major = az;
            u = x;
            v = y;
        }

        if (!(major > 0))
        {
            face = 2;
            major = 1;
            u = v = 0;
        }

        var fu = Math.Clamp((u / major + 1f) * 0.5f * Cells, 0f, Cells);
        var fv = Math.Clamp((v / major + 1f) * 0.5f * Cells, 0f, Cells);
        var i = Math.Min((int)fu, Cells - 1);
        var j = Math.Min((int)fv, Cells - 1);
        var tu = fu - i;
        var tv = fv - j;
        var table = Tables[shape];
        var at = (face * NodesPerFace + j * Nodes + i) * 2;
        var up = at + Nodes * 2;
        var s0 = table[at] + (table[at + 2] - table[at]) * tu;
        var s1 = table[up] + (table[up + 2] - table[up]) * tu;
        var w0 = table[at + 1] + (table[at + 3] - table[at + 1]) * tu;
        var w1 = table[up + 1] + (table[up + 3] - table[up + 1]) * tu;
        surface = s0 + (s1 - s0) * tv;
        wobble = w0 + (w1 - w0) * tv;
    }

    /// <summary>
    /// The share of a rock of this shape that lies within <paramref name="depth"/> of its surface, depth being a
    /// fraction of the surface's own distance from the centre, wobbled by <paramref name="wobble"/> times the
    /// shape's crust noise. Exact for the tables <see cref="Lookup"/> reads, up to the quadrature over cells.
    /// </summary>
    public static float CrustShare(int shape, float depth, float wobble)
    {
        var m = Moments[shape];
        var inner = 1f - depth;
        var swing = depth * wobble;
        // The mean of (inner - swing w)^3 weighted by s^3, expanded in powers of w.
        var kept = inner * inner * inner - 3f * inner * inner * swing * m.Y + 3f * inner * swing * swing * m.Z - swing * swing * swing * m.W;
        return 1f - kept;
    }

    /// <summary>The mean over all directions of the surface fraction cubed: the rock's volume over its sphere's.</summary>
    public static float MeanCube(int shape) => Moments[shape].X;

    private static void Build(int shape)
    {
        var table = new float[6 * NodesPerFace * 2];
        var seed = shape + 1;
        // The squash of AsteroidShape, from functions that agree on every machine.
        var squashX = 1f + 0.35f * DetMath.Sin(seed * 1.7f);
        var squashY = 1f + 0.3f * DetMath.Sin(seed * 2.9f + 1f);
        var squashZ = 1f + 0.35f * DetMath.Sin(seed * 4.1f + 2f);
        var wobbleOffset = new Vector3(shape * 5.31f + 3.7f, shape * 2.77f + 11.1f, shape * 9.13f + 1.9f);
        var largest = 0f;
        for (var face = 0; face < 6; face++)
        {
            for (var j = 0; j < Nodes; j++)
            {
                for (var i = 0; i < Nodes; i++)
                {
                    Direction(face, -1f + 2f * i / Cells, -1f + 2f * j / Cells, out var x, out var y, out var z);
                    float raw;
                    if (shape == AsteroidShape.Icicle)
                        raw = 1f / (Math.Abs(x) + 6f * Math.Abs(y) + 6f * Math.Abs(z));
                    else
                    {
                        // The shape is defined before squashing; undo the squash to find which part of it lies this way.
                        var ux = x / squashX;
                        var uy = y / squashY;
                        var uz = z / squashZ;
                        var length = MathF.Sqrt(ux * ux + uy * uy + uz * uz);
                        raw = AsteroidShape.Lumps(shape, new Vector3(ux / length, uy / length, uz / length)) / length;
                    }

                    var at = (face * NodesPerFace + j * Nodes + i) * 2;
                    table[at] = raw;
                    table[at + 1] = Noise.Fractal(new Vector3(x * 2.3f, y * 2.3f, z * 2.3f) + wobbleOffset, 3) * 2f - 1f;
                    if (raw > largest)
                        largest = raw;
                }
            }
        }

        // The icicle is already of unit size; a rock is scaled so that its furthest point is at its radius.
        var scale = shape == AsteroidShape.Icicle ? 1f : 1f / largest;
        for (var k = 0; k < table.Length; k += 2)
            table[k] *= scale;
        Tables[shape] = table;

        // The moments, from each cell's centre (where the bilinear lookup is the mean of its corners), weighted by
        // the solid angle the cell covers.
        double weights = 0, cube = 0, first = 0, second = 0, third = 0;
        for (var face = 0; face < 6; face++)
        {
            for (var j = 0; j < Cells; j++)
            {
                for (var i = 0; i < Cells; i++)
                {
                    var u = -1f + (2f * i + 1f) / Cells;
                    var v = -1f + (2f * j + 1f) / Cells;
                    var r2 = 1.0 + u * u + v * v;
                    var weight = 1.0 / (r2 * Math.Sqrt(r2));
                    var at = (face * NodesPerFace + j * Nodes + i) * 2;
                    var up = at + Nodes * 2;
                    var s = 0.25 * ((double)table[at] + table[at + 2] + table[up] + table[up + 2]);
                    var w = 0.25 * ((double)table[at + 1] + table[at + 3] + table[up + 1] + table[up + 3]);
                    var s3 = s * s * s * weight;
                    weights += weight;
                    cube += s3;
                    first += s3 * w;
                    second += s3 * w * w;
                    third += s3 * w * w * w;
                }
            }
        }

        Moments[shape] = new Vector4((float)(cube / weights), (float)(first / cube), (float)(second / cube), (float)(third / cube));
    }

    /// <summary>The unit direction of a point (u, v) on a cube face, with face axes as <see cref="Lookup"/> reads them.</summary>
    private static void Direction(int face, float u, float v, out float x, out float y, out float z)
    {
        var sign = (face & 1) == 0 ? 1f : -1f;
        switch (face >> 1)
        {
            case 0: x = sign; y = u; z = v; break;
            case 1: x = u; y = sign; z = v; break;
            default: x = u; y = v; z = sign; break;
        }

        var length = MathF.Sqrt(x * x + y * y + z * z);
        x /= length;
        y /= length;
        z /= length;
    }
}
