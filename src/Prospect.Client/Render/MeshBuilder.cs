using System.Numerics;
using Brutal.Numerics;
using Prospect.Game.World;

namespace Prospect.Client.Render;

internal struct MeshVertex
{
    public float3 Position;
    public float3 Normal;
}

/// <summary>Where one mesh sits inside the shared vertex and index buffers.</summary>
internal readonly record struct MeshRange(int FirstIndex, int IndexCount, int VertexOffset);

/// <summary>Geometry built on the CPU, ready to upload.</summary>
internal sealed class MeshData
{
    public readonly List<MeshVertex> Vertices = [];
    public readonly List<uint> Indices = [];

    /// <summary>Radius of the sphere around the origin that contains the mesh.</summary>
    public float BoundingRadius;
}

internal static class MeshBuilder
{
    private static readonly (int X, int Y, int Z)[] FaceNormals = [(1, 0, 0), (-1, 0, 0), (0, 1, 0), (0, -1, 0), (0, 0, 1), (0, 0, -1)];

    /// <summary>
    /// Meshes a block hull: one quad for every block face that is not covered by a neighbouring block, centred on
    /// the hull's centre of mass.
    /// </summary>
    public static MeshData Hull(HullDesign design)
    {
        var occupied = new HashSet<(int, int, int)>(design.Cells);
        var center = design.CenterOfMass;

        var mesh = new MeshData();
        var half = 0.5f;
        foreach (var (x, y, z) in design.Cells)
        {
            var blockCenter = new Vector3(x, y, z) - center;
            foreach (var (nx, ny, nz) in FaceNormals)
            {
                if (occupied.Contains((x + nx, y + ny, z + nz)))
                    continue;

                var normal = new Vector3(nx, ny, nz);
                // Two axes spanning the face, ordered so the quad winds counter-clockwise seen from outside.
                var u = nx != 0 ? new Vector3(0, 1, 0) : ny != 0 ? new Vector3(0, 0, 1) : new Vector3(1, 0, 0);
                var v = Vector3.Cross(normal, u);
                var faceCenter = blockCenter + normal * half;
                var first = (uint)mesh.Vertices.Count;
                AddVertex(mesh, (faceCenter - u * half - v * half) * design.BlockSize, normal);
                AddVertex(mesh, (faceCenter + u * half - v * half) * design.BlockSize, normal);
                AddVertex(mesh, (faceCenter + u * half + v * half) * design.BlockSize, normal);
                AddVertex(mesh, (faceCenter - u * half + v * half) * design.BlockSize, normal);
                mesh.Indices.AddRange([first, first + 1, first + 2, first, first + 2, first + 3]);
            }
        }

        return mesh;
    }

    /// <summary>
    /// A rock of unit size: a sphere pushed in and out by layered noise. <paramref name="subdivisions"/> sets the
    /// level of detail; the same seed gives the same shape at every level.
    /// </summary>
    public static MeshData Asteroid(int seed, int subdivisions)
    {
        var mesh = Icosphere(subdivisions);
        var offset = new Vector3(seed * 12.9898f % 97f, seed * 78.233f % 89f, seed * 37.719f % 83f);
        var squash = new Vector3(1f + 0.35f * MathF.Sin(seed * 1.7f), 1f + 0.3f * MathF.Sin(seed * 2.9f + 1f), 1f + 0.35f * MathF.Sin(seed * 4.1f + 2f));

        var positions = new Vector3[mesh.Vertices.Count];
        var largest = 0f;
        for (var i = 0; i < positions.Length; i++)
        {
            var direction = ToVector(mesh.Vertices[i].Position);
            var broad = Noise.Fractal(direction * 1.3f + offset, 4);
            var ridges = 1f - MathF.Abs(Noise.Fractal(direction * 3.1f + offset * 1.7f, 3) * 2f - 1f);
            var radius = 0.62f + 0.42f * broad + 0.1f * ridges * ridges;
            positions[i] = direction * squash * radius;
            largest = Math.Max(largest, positions[i].Length());
        }

        // Normalise so the rock just fits a unit sphere; instances scale it to their radius.
        var normals = new Vector3[positions.Length];
        for (var i = 0; i < positions.Length; i++)
            positions[i] /= largest;
        for (var i = 0; i < mesh.Indices.Count; i += 3)
        {
            var (a, b, c) = ((int)mesh.Indices[i], (int)mesh.Indices[i + 1], (int)mesh.Indices[i + 2]);
            var face = Vector3.Cross(positions[b] - positions[a], positions[c] - positions[a]);
            normals[a] += face;
            normals[b] += face;
            normals[c] += face;
        }

        for (var i = 0; i < positions.Length; i++)
            mesh.Vertices[i] = new MeshVertex { Position = ToFloat3(positions[i]), Normal = ToFloat3(Vector3.Normalize(normals[i])) };
        mesh.BoundingRadius = 1f;
        return mesh;
    }

    private static MeshData Icosphere(int subdivisions)
    {
        var t = (1f + MathF.Sqrt(5f)) / 2f;
        var points = new List<Vector3>
        {
            new(-1, t, 0), new(1, t, 0), new(-1, -t, 0), new(1, -t, 0),
            new(0, -1, t), new(0, 1, t), new(0, -1, -t), new(0, 1, -t),
            new(t, 0, -1), new(t, 0, 1), new(-t, 0, -1), new(-t, 0, 1),
        };
        for (var i = 0; i < points.Count; i++)
            points[i] = Vector3.Normalize(points[i]);

        var indices = new List<uint>
        {
            0, 11, 5, 0, 5, 1, 0, 1, 7, 0, 7, 10, 0, 10, 11,
            1, 5, 9, 5, 11, 4, 11, 10, 2, 10, 7, 6, 7, 1, 8,
            3, 9, 4, 3, 4, 2, 3, 2, 6, 3, 6, 8, 3, 8, 9,
            4, 9, 5, 2, 4, 11, 6, 2, 10, 8, 6, 7, 9, 8, 1,
        };

        var midpoints = new Dictionary<ulong, uint>();
        uint Midpoint(uint a, uint b)
        {
            var key = a < b ? (ulong)a << 32 | b : (ulong)b << 32 | a;
            if (midpoints.TryGetValue(key, out var index))
                return index;
            index = (uint)points.Count;
            points.Add(Vector3.Normalize(points[(int)a] + points[(int)b]));
            midpoints[key] = index;
            return index;
        }

        for (var level = 0; level < subdivisions; level++)
        {
            var next = new List<uint>(indices.Count * 4);
            for (var i = 0; i < indices.Count; i += 3)
            {
                var (a, b, c) = (indices[i], indices[i + 1], indices[i + 2]);
                var (ab, bc, ca) = (Midpoint(a, b), Midpoint(b, c), Midpoint(c, a));
                next.AddRange([a, ab, ca, b, bc, ab, c, ca, bc, ab, bc, ca]);
            }

            indices = next;
        }

        var mesh = new MeshData();
        foreach (var point in points)
            AddVertex(mesh, point, point);
        mesh.Indices.AddRange(indices);
        return mesh;
    }

    private static void AddVertex(MeshData mesh, Vector3 position, Vector3 normal)
    {
        mesh.Vertices.Add(new MeshVertex { Position = ToFloat3(position), Normal = ToFloat3(normal) });
        mesh.BoundingRadius = Math.Max(mesh.BoundingRadius, position.Length());
    }

    private static float3 ToFloat3(Vector3 v) => new(v.X, v.Y, v.Z);

    private static Vector3 ToVector(float3 v) => new(v.X, v.Y, v.Z);
}

/// <summary>Value noise for shaping rocks.</summary>
internal static class Noise
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

        float Lerp(float a, float b, float t) => a + (b - a) * t;
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
