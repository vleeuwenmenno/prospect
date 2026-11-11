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
    /// A rock of unit size, sampled from the shared shape function the simulation collides against.
    /// <paramref name="subdivisions"/> sets the level of detail.
    /// </summary>
    public static MeshData Asteroid(int shape, int subdivisions)
    {
        var sphere = Spheres[subdivisions].Value;
        var positions = new Vector3[sphere.Points.Length];
        for (var i = 0; i < positions.Length; i++)
            positions[i] = AsteroidShape.Point(shape, sphere.Points[i]);

        var normals = new Vector3[positions.Length];
        var indices = sphere.Indices;
        for (var i = 0; i < indices.Length; i += 3)
        {
            var (a, b, c) = ((int)indices[i], (int)indices[i + 1], (int)indices[i + 2]);
            var face = Vector3.Cross(positions[b] - positions[a], positions[c] - positions[a]);
            normals[a] += face;
            normals[b] += face;
            normals[c] += face;
        }

        var mesh = new MeshData { BoundingRadius = 1f };
        mesh.Vertices.Capacity = positions.Length;
        for (var i = 0; i < positions.Length; i++)
            mesh.Vertices.Add(new MeshVertex { Position = ToFloat3(positions[i]), Normal = ToFloat3(Vector3.Normalize(normals[i])) });
        mesh.Indices.AddRange(indices);
        return mesh;
    }

    /// <summary>A unit cube centred on the origin: one block, for hulls drawn block by block.</summary>
    public static MeshData Cube()
    {
        var mesh = new MeshData();
        foreach (var (nx, ny, nz) in FaceNormals)
        {
            var normal = new Vector3(nx, ny, nz);
            var u = nx != 0 ? new Vector3(0, 1, 0) : ny != 0 ? new Vector3(0, 0, 1) : new Vector3(1, 0, 0);
            var v = Vector3.Cross(normal, u);
            var center = normal * 0.5f;
            var first = (uint)mesh.Vertices.Count;
            AddVertex(mesh, center - u * 0.5f - v * 0.5f, normal);
            AddVertex(mesh, center + u * 0.5f - v * 0.5f, normal);
            AddVertex(mesh, center + u * 0.5f + v * 0.5f, normal);
            AddVertex(mesh, center - u * 0.5f + v * 0.5f, normal);
            mesh.Indices.AddRange([first, first + 1, first + 2, first, first + 2, first + 3]);
        }

        return mesh;
    }

    /// <summary>The most an asteroid mesh is subdivided.</summary>
    private const int MaxSubdivisions = 6;

    /// <summary>
    /// The unit sphere at each level of subdivision. Every asteroid shape of a level is the same sphere pushed in
    /// and out, so the sphere is built once and shared by the jobs that mesh the shapes.
    /// </summary>
    private static readonly Lazy<(Vector3[] Points, uint[] Indices)>[] Spheres = CreateSpheres();

    private static Lazy<(Vector3[] Points, uint[] Indices)>[] CreateSpheres()
    {
        var spheres = new Lazy<(Vector3[], uint[])>[MaxSubdivisions + 1];
        for (var i = 0; i < spheres.Length; i++)
        {
            var subdivisions = i;
            spheres[i] = new Lazy<(Vector3[], uint[])>(() => Icosphere(subdivisions), LazyThreadSafetyMode.ExecutionAndPublication);
        }

        return spheres;
    }

    private static (Vector3[] Points, uint[] Indices) Icosphere(int subdivisions)
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

        return ([.. points], [.. indices]);
    }

    private static void AddVertex(MeshData mesh, Vector3 position, Vector3 normal)
    {
        mesh.Vertices.Add(new MeshVertex { Position = ToFloat3(position), Normal = ToFloat3(normal) });
        mesh.BoundingRadius = Math.Max(mesh.BoundingRadius, position.Length());
    }

    private static float3 ToFloat3(Vector3 v) => new(v.X, v.Y, v.Z);
}
