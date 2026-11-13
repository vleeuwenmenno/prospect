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
    /// <summary>
    /// Meshes a block hull: one polygon for every block face that is not covered by a neighbouring block, centred
    /// on the hull's centre of mass. A wedge's faces are its two full sides, two triangular caps and the slant, which
    /// its exterior-face bit (u, +su) stands for; its bit (v, +sv) has no face.
    /// </summary>
    public static MeshData Hull(HullDesign design)
    {
        var mesh = new MeshData();
        for (var b = 0; b < design.BlockCount; b++)
        {
            for (var side = 0; side < 6; side++)
            {
                if ((design.ExteriorFaces[b] & (1 << side)) != 0)
                    AddFace(mesh, design.Shapes[b], design.BlockOffsets[b], design.BlockHalfExtents[b], side);
            }
        }

        return mesh;
    }

    /// <summary>
    /// One block of unit bounding box centred on the origin, for hulls drawn block by block: a cube for
    /// <see cref="BlockShape.Box"/>, a triangular prism for a wedge. Scaled per axis it is the block at any size;
    /// the mesh shader turns its normals for that scaling.
    /// </summary>
    public static MeshData Primitive(BlockShape shape)
    {
        var mesh = new MeshData();
        for (var side = 0; side < 6; side++)
            AddFace(mesh, shape, Vector3.Zero, new Vector3(0.5f), side);
        return mesh;
    }

    /// <summary>A unit cube centred on the origin: one block, for hulls drawn block by block.</summary>
    public static MeshData Cube() => Primitive(BlockShape.Box);

    private static void AddFace(MeshData mesh, BlockShape shape, Vector3 center, Vector3 half, int side)
    {
        Span<int> corners = stackalloc int[4];
        var count = BlockShapes.Face(shape, side, corners);
        if (count == 0)
            return;
        var normal = BlockShapes.FaceNormal(shape, half, side);
        var first = (uint)mesh.Vertices.Count;
        for (var i = 0; i < count; i++)
            AddVertex(mesh, center + BlockShapes.Corner(corners[i], half), normal);
        for (var i = 1; i + 1 < count; i++)
            mesh.Indices.AddRange([first, first + (uint)i, first + (uint)i + 1]);
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
