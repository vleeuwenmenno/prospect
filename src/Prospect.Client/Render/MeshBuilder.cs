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

    /// <summary>The nozzles of a hull's thrust parts, in the hull's frame, in block order; empty for every other mesh.</summary>
    public ThrustNozzle[] Nozzles = [];
}

internal static class MeshBuilder
{
    /// <summary>
    /// Meshes a block hull: one polygon for every block face that is not covered by a neighbouring block, centred
    /// on the hull's centre of mass. A wedge's faces are its two full sides, two triangular caps and the slant, which
    /// its exterior-face bit (u, +su) stands for; its bit (v, +sv) has no face. A thrust part with a model in
    /// <paramref name="look"/> is drawn as that model instead (<see cref="PropulsionModels"/>), and its nozzles are
    /// listed in <see cref="MeshData.Nozzles"/> for the plumes. The parts mounted on block faces follow the blocks,
    /// each as its model in the small box it fills, or as that box when it has no model.
    /// </summary>
    public static MeshData Hull(HullDesign design, PropulsionLook? look = null)
    {
        look ??= PropulsionLook.Shared;
        var mesh = new MeshData();
        var nozzles = new List<ThrustNozzle>();
        for (var b = 0; b < design.BlockCount; b++)
        {
            var first = mesh.Vertices.Count;
            if (PropulsionModels.Add(design, b, look, mesh.Vertices, mesh.Indices, nozzles))
            {
                for (var i = first; i < mesh.Vertices.Count; i++)
                {
                    var p = mesh.Vertices[i].Position;
                    mesh.BoundingRadius = Math.Max(mesh.BoundingRadius, new Vector3(p.X, p.Y, p.Z).Length());
                }
                continue;
            }
            for (var side = 0; side < 6; side++)
            {
                if ((design.ExteriorFaces[b] & (1 << side)) != 0)
                    AddFace(mesh, design.Shapes[b], design.BlockOffsets[b], design.BlockHalfExtents[b], side);
            }
        }

        for (var m = 0; m < design.Mounts.Length; m++)
        {
            var first = mesh.Vertices.Count;
            if (!PropulsionModels.AddMount(design, m, look, mesh.Vertices, mesh.Indices, nozzles))
            {
                // Every side but the one against the block it sits on.
                var back = (int)Facings.Opposite(design.Mounts[m].Face);
                for (var side = 0; side < 6; side++)
                    if (side != back)
                        AddFace(mesh, BlockShape.Box, design.MountOffsets[m], design.MountHalfExtents[m], side);
            }

            for (var i = first; i < mesh.Vertices.Count; i++)
            {
                var p = mesh.Vertices[i].Position;
                mesh.BoundingRadius = Math.Max(mesh.BoundingRadius, new Vector3(p.X, p.Y, p.Z).Length());
            }
        }

        mesh.Nozzles = [.. nozzles];
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
        if (shape == AsteroidShape.Icicle)
            return Icicle();
        var sphere = AsteroidMesh.Sphere(subdivisions);
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

        for (var i = 0; i < normals.Length; i++)
            normals[i] = Vector3.Normalize(normals[i]);

        // The patches of scraped rocks are meshed against exactly these values.
        if (subdivisions == AsteroidMesh.Subdivisions)
            RockBaseMesh.Adopt(shape, positions, normals);

        var mesh = new MeshData { BoundingRadius = 1f };
        mesh.Vertices.Capacity = positions.Length;
        for (var i = 0; i < positions.Length; i++)
            mesh.Vertices.Add(new MeshVertex { Position = ToFloat3(positions[i]), Normal = ToFloat3(normals[i]) });
        mesh.Indices.AddRange(indices);
        return mesh;
    }

    /// <summary>
    /// The ice shard exactly as the simulation collides with it (<see cref="AsteroidShape.RadiusAlong"/>): an
    /// octahedron with its tips on the axes, at 1 along x and 1/6 along y and z. Eight flat faces with their own
    /// normals, so the edges stay sharp; sampling a sphere would cut the tips off. One mesh for every level of
    /// detail: it is already cheaper than the coarsest rock.
    /// </summary>
    public static MeshData Icicle()
    {
        const float Thin = 1f / 6f;
        var mesh = new MeshData { BoundingRadius = 1f };
        for (var face = 0; face < 8; face++)
        {
            var sx = (face & 1) == 0 ? 1f : -1f;
            var sy = (face & 2) == 0 ? 1f : -1f;
            var sz = (face & 4) == 0 ? 1f : -1f;
            var a = new Vector3(sx, 0, 0);
            var b = new Vector3(0, sy * Thin, 0);
            var c = new Vector3(0, 0, sz * Thin);
            var normal = Vector3.Normalize(Vector3.Cross(b - a, c - a));
            // Wind every face outward, whatever the signs did to its order.
            if (Vector3.Dot(normal, a) < 0)
            {
                (b, c) = (c, b);
                normal = -normal;
            }
            var first = (uint)mesh.Vertices.Count;
            mesh.Vertices.Add(new MeshVertex { Position = ToFloat3(a), Normal = ToFloat3(normal) });
            mesh.Vertices.Add(new MeshVertex { Position = ToFloat3(b), Normal = ToFloat3(normal) });
            mesh.Vertices.Add(new MeshVertex { Position = ToFloat3(c), Normal = ToFloat3(normal) });
            mesh.Indices.AddRange([first, first + 1, first + 2]);
        }
        return mesh;
    }

    private static void AddVertex(MeshData mesh, Vector3 position, Vector3 normal)
    {
        mesh.Vertices.Add(new MeshVertex { Position = ToFloat3(position), Normal = ToFloat3(normal) });
        mesh.BoundingRadius = Math.Max(mesh.BoundingRadius, position.Length());
    }

    private static float3 ToFloat3(Vector3 v) => new(v.X, v.Y, v.Z);
}
