using System.Numerics;
using Brutal.Numerics;

namespace Prospect.Client.Render;

/// <summary>
/// The mesh of a loose ore chunk: a rough, rounded cube with half an edge of one, so a chunk is drawn by scaling it to
/// <c>OreChunks.Half</c>. A few variants (picked by a chunk's id) keep a field of chunks from looking stamped. Every
/// facet is flat shaded, and the lattice points are displaced by a hash of their place alone, so the faces meet along
/// shared edges with no gap. Built on a worker, never in a frame.
/// </summary>
internal static class OreChunkModels
{
    /// <summary>How many different lumps there are.</summary>
    public const int Variants = 4;

    /// <summary>Quads along each side of a face; the lattice has one more point.</summary>
    private const int Cells = 2;

    /// <summary>The lump of variant <paramref name="variant"/> as a triangle list, three vertices a triangle.</summary>
    public static MeshVertex[] Build(int variant)
    {
        var soup = new List<MeshVertex>(6 * Cells * Cells * 6);
        for (var face = 0; face < 6; face++)
        {
            var axis = face / 2;
            var sign = face % 2 == 0 ? 1f : -1f;
            var u = (axis + 1) % 3;
            var v = (axis + 2) % 3;
            for (var i = 0; i < Cells; i++)
                for (var j = 0; j < Cells; j++)
                {
                    var a = Point(variant, axis, sign, u, v, i, j);
                    var b = Point(variant, axis, sign, u, v, i + 1, j);
                    var c = Point(variant, axis, sign, u, v, i + 1, j + 1);
                    var d = Point(variant, axis, sign, u, v, i, j + 1);
                    Triangle(soup, a, b, c);
                    Triangle(soup, a, c, d);
                }
        }

        return [.. soup];
    }

    // A lattice point of a face, on the unit cube, rounded toward a sphere and pulled in or out by its own hash.
    private static Vector3 Point(int variant, int axis, float sign, int u, int v, int i, int j)
    {
        var p = new Vector3();
        p[axis] = sign;
        p[u] = (2f * i / Cells) - 1f;
        p[v] = (2f * j / Cells) - 1f;
        var rounded = Vector3.Lerp(p, Vector3.Normalize(p) * 1.12f, 0.3f);
        // The same lattice point on any of the faces that share it gets the same lump.
        var key = (((int)MathF.Round(p.X) + 1) * 9 + ((int)MathF.Round(p.Y) + 1) * 3 + (int)MathF.Round(p.Z) + 1) + 27 * variant;
        var pull = 0.8f + 0.28f * Hash(key * 3 + 1);
        var shift = new Vector3(Hash(key * 3 + 2) - 0.5f, Hash(key * 3 + 3) - 0.5f, Hash(key * 3 + 4) - 0.5f) * 0.16f;
        return rounded * pull + shift;
    }

    // A flat triangle wound counter-clockwise seen from outside (away from the lump's middle).
    private static void Triangle(List<MeshVertex> soup, Vector3 a, Vector3 b, Vector3 c)
    {
        var normal = Vector3.Cross(b - a, c - a);
        if (normal.LengthSquared() < 1e-10f)
            return;
        if (Vector3.Dot(normal, (a + b + c) / 3f) < 0)
        {
            (b, c) = (c, b);
            normal = -normal;
        }

        normal = Vector3.Normalize(normal);
        var n = new float3(normal.X, normal.Y, normal.Z);
        soup.Add(new MeshVertex { Position = new float3(a.X, a.Y, a.Z), Normal = n });
        soup.Add(new MeshVertex { Position = new float3(b.X, b.Y, b.Z), Normal = n });
        soup.Add(new MeshVertex { Position = new float3(c.X, c.Y, c.Z), Normal = n });
    }

    private static float Hash(int n)
    {
        var x = (uint)n * 0x9E3779B1u;
        x ^= x >> 15;
        x *= 0x85EBCA77u;
        x ^= x >> 13;
        x *= 0xC2B2AE3Du;
        x ^= x >> 16;
        return (x & 0xFFFFFF) / (float)0x1000000;
    }
}
