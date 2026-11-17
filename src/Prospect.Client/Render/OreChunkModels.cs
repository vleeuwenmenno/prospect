using System.Numerics;
using Brutal.Numerics;

namespace Prospect.Client.Render;

/// <summary>
/// The mesh of a loose ore chunk: a lump of rock broken off a larger one, with the largest coordinate of any vertex
/// one, so a chunk is drawn by scaling it to <c>OreChunks.Half</c>. The body is a convex polyhedron cut from a box by
/// a few random planes, so it has sharp, flat break faces and a different outline in every variant (picked by a
/// chunk's id). Richer chunks add ore crystals standing out of the biggest break faces: the facets carry a normal of
/// length <see cref="CrystalTag"/>, which <c>mesh.vert</c> reads back to light them as ore. Figures come from
/// <see cref="OreChunkModelLook"/>. Every vertex of the body is one shared point, so faces meet with no gap. Built on a
/// worker, never in a frame.
/// </summary>
internal static class OreChunkModels
{
    /// <summary>The length of a crystal facet's normal; <c>mesh.vert</c> takes a length between 1.1 and 1.5 as ore crystal.</summary>
    public const float CrystalTag = 1.25f;

    /// <summary>Sides of a crystal's prism.</summary>
    private const int Sides = 5;

    /// <summary>How close two planes' intersection points must be to count as one vertex, and how far from a plane a vertex may be to lie on it.</summary>
    private const float Epsilon = 1e-3f;

    // A deterministic stream of numbers from 0 up to 1, one per variant.
    private sealed class Rng(int seed)
    {
        private int _counter;
        public float Next() => Hash(seed * 7919 + _counter++ * 104729 + 13);
    }

    private sealed class Face
    {
        public Vector3 Normal;
        public int[] Vertices = [];
        public Vector3 Centroid;
        public float Area;
        public Vector3 U, W;
    }

    /// <summary>The lump of variant <paramref name="variant"/> with its first <paramref name="crystals"/> crystals, as a triangle list.</summary>
    public static MeshVertex[] Build(OreChunkModelLook look, int variant, int crystals)
    {
        var rng = new Rng(variant);
        float Next() => rng.Next();
        float Between(float low, float high) => low + (high - low) * rng.Next();

        // The planes: a box with its own proportions, then the cuts that break its corners and edges off.
        var box = new Vector3(Between(look.BoxHalfMin, look.BoxHalfMax), Between(look.BoxHalfMin, look.BoxHalfMax), Between(look.BoxHalfMin, look.BoxHalfMax));
        var normals = new List<Vector3>();
        var distances = new List<float>();
        for (var axis = 0; axis < 3; axis++)
            for (var sign = -1; sign <= 1; sign += 2)
            {
                var n = new Vector3();
                n[axis] = sign;
                normals.Add(n);
                distances.Add(box[axis]);
            }

        var cuts = look.CutsMin + (int)(Next() * (look.CutsMax - look.CutsMin + 0.999f));
        for (var i = 0; i < cuts; i++)
        {
            Vector3 n;
            do n = new Vector3(Next() * 2 - 1, Next() * 2 - 1, Next() * 2 - 1);
            while (n.LengthSquared() is < 0.04f or > 1f);
            n = Vector3.Normalize(n);
            // A plane nearly parallel to one already there only makes a sliver beside its face, so it is left out.
            var parallel = false;
            foreach (var other in normals)
                parallel |= Vector3.Dot(n, other) > 0.985f;
            if (parallel)
                continue;
            var support = MathF.Abs(n.X) * box.X + MathF.Abs(n.Y) * box.Y + MathF.Abs(n.Z) * box.Z;
            normals.Add(n);
            distances.Add(support * Between(look.CutDepthMin, look.CutDepthMax));
        }

        var points = Vertices(normals, distances, out var onPlanes);
        // Centre the box around the origin and scale it to a largest coordinate of one; planes keep their normals.
        var low = new Vector3(float.MaxValue);
        var high = new Vector3(float.MinValue);
        foreach (var p in points)
        {
            low = Vector3.Min(low, p);
            high = Vector3.Max(high, p);
        }

        var middle = (low + high) / 2f;
        var reach = 0f;
        foreach (var p in points)
            reach = MathF.Max(reach, MathF.Max(MathF.Abs(p.X - middle.X), MathF.Max(MathF.Abs(p.Y - middle.Y), MathF.Abs(p.Z - middle.Z))));
        for (var i = 0; i < points.Count; i++)
            points[i] = (points[i] - middle) / reach;
        for (var i = 0; i < distances.Count; i++)
            distances[i] = (distances[i] - Vector3.Dot(normals[i], middle)) / reach;

        var faces = new List<Face>();
        for (var m = 0; m < normals.Count; m++)
            if (MakeFace(m, normals[m], points, onPlanes) is { } face)
                faces.Add(face);

        var inside = Vector3.Zero;
        foreach (var p in points)
            inside += p;
        inside /= points.Count;

        var soup = new List<MeshVertex>(faces.Count * 12 + crystals * 54);
        foreach (var face in faces)
        {
            // A fan round a middle pushed in or out: each triangle is a flat facet of its own, the rim stays shared.
            var middle3 = face.Centroid + face.Normal * ((Next() * 2f - 1f) * look.FacetRelief * MathF.Sqrt(face.Area));
            for (var i = 0; i < face.Vertices.Length; i++)
                Triangle(soup, points[face.Vertices[i]], points[face.Vertices[(i + 1) % face.Vertices.Length]], middle3, inside, 1f);
        }

        // Crystals stand out of the biggest faces, the first of the list on the biggest of all.
        faces.Sort((a, b) => b.Area.CompareTo(a.Area));
        var pool = Math.Min(8, faces.Count);
        var shift = (int)(Next() * pool);
        for (var k = 0; k < crystals && pool > 0; k++)
            Crystal(soup, look, faces[(k + shift) % pool], points, rng);
        return [.. soup];
    }

    // The distinct corners of the polyhedron the planes enclose: every point three planes share that no plane cuts off, with
    // the set of planes (a bit each) that met there, so a face takes exactly the corners its plane made.
    private static List<Vector3> Vertices(List<Vector3> normals, List<float> distances, out List<uint> onPlanes)
    {
        var points = new List<Vector3>();
        onPlanes = [];
        for (var i = 0; i < normals.Count; i++)
            for (var j = i + 1; j < normals.Count; j++)
                for (var k = j + 1; k < normals.Count; k++)
                {
                    var jk = Vector3.Cross(normals[j], normals[k]);
                    var det = Vector3.Dot(normals[i], jk);
                    if (MathF.Abs(det) < 1e-4f)
                        continue;
                    var p = (distances[i] * jk + distances[j] * Vector3.Cross(normals[k], normals[i])
                        + distances[k] * Vector3.Cross(normals[i], normals[j])) / det;
                    var cut = false;
                    for (var m = 0; m < normals.Count && !cut; m++)
                        cut = Vector3.Dot(normals[m], p) > distances[m] + Epsilon;
                    if (cut)
                        continue;
                    var made = (1u << i) | (1u << j) | (1u << k);
                    var at = -1;
                    for (var q = 0; q < points.Count && at < 0; q++)
                        if (Vector3.DistanceSquared(p, points[q]) < Epsilon * Epsilon * 4f)
                            at = q;
                    if (at < 0)
                    {
                        points.Add(p);
                        onPlanes.Add(made);
                    }
                    else
                        onPlanes[at] |= made;
                }

        return points;
    }

    // The polygon one plane leaves, its corners in counter-clockwise order seen from outside; null when it touches fewer than three.
    private static Face? MakeFace(int plane, Vector3 normal, List<Vector3> points, List<uint> onPlanes)
    {
        var ids = new List<int>();
        for (var i = 0; i < points.Count; i++)
            if ((onPlanes[i] & (1u << plane)) != 0)
                ids.Add(i);
        if (ids.Count < 3)
            return null;
        var centroid = Vector3.Zero;
        foreach (var id in ids)
            centroid += points[id];
        centroid /= ids.Count;
        var u = Vector3.Normalize(Vector3.Cross(normal, MathF.Abs(normal.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX));
        var w = Vector3.Cross(normal, u);
        ids.Sort((a, b) => Angle(points[a] - centroid, u, w).CompareTo(Angle(points[b] - centroid, u, w)));
        var area = 0f;
        for (var i = 1; i + 1 < ids.Count; i++)
            area += Vector3.Cross(points[ids[i]] - points[ids[0]], points[ids[i + 1]] - points[ids[0]]).Length() / 2f;
        // A plane that only grazes an edge leaves no polygon; its corners still belong to the faces either side of it.
        if (area < 1e-4f)
            return null;
        return new Face { Normal = normal, Vertices = [.. ids], Centroid = centroid, Area = area, U = u, W = w };
    }

    private static float Angle(Vector3 v, Vector3 u, Vector3 w) => MathF.Atan2(Vector3.Dot(v, w), Vector3.Dot(v, u));

    // One five-sided crystal: a base buried under the face, a straight shaft and a pointed tip, leaning a little.
    private static void Crystal(List<MeshVertex> soup, OreChunkModelLook look, Face face, List<Vector3> points, Rng rng)
    {
        float Next() => rng.Next();
        var corner = points[face.Vertices[(int)(Next() * face.Vertices.Length) % face.Vertices.Length]];
        var anchor = face.Centroid + (corner - face.Centroid) * (0.2f + 0.4f * Next());
        var axis = Vector3.Normalize(face.Normal + (face.U * (Next() * 2 - 1) + face.W * (Next() * 2 - 1)) * look.CrystalTilt);
        var length = look.CrystalLengthMin + (look.CrystalLengthMax - look.CrystalLengthMin) * Next();
        var radius = look.CrystalRadiusMin + (look.CrystalRadiusMax - look.CrystalRadiusMin) * Next();
        var spin = Next() * 6.2831853f;
        var e1 = Vector3.Normalize(Vector3.Cross(axis, MathF.Abs(axis.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX));
        var e2 = Vector3.Cross(axis, e1);
        var baseCentre = anchor - axis * 0.2f;
        var shoulderCentre = anchor + axis * (length * 0.62f);
        var tip = anchor + axis * length;
        var inside = (baseCentre + tip) / 2f;
        Span<Vector3> baseRing = stackalloc Vector3[Sides];
        Span<Vector3> shoulderRing = stackalloc Vector3[Sides];
        for (var i = 0; i < Sides; i++)
        {
            var a = spin + 6.2831853f * i / Sides;
            var around = e1 * MathF.Cos(a) + e2 * MathF.Sin(a);
            baseRing[i] = baseCentre + around * radius;
            shoulderRing[i] = shoulderCentre + around * radius * 0.92f;
        }

        for (var i = 0; i < Sides; i++)
        {
            var j = (i + 1) % Sides;
            Triangle(soup, baseRing[i], baseRing[j], shoulderRing[j], inside, CrystalTag);
            Triangle(soup, baseRing[i], shoulderRing[j], shoulderRing[i], inside, CrystalTag);
            Triangle(soup, shoulderRing[i], shoulderRing[j], tip, inside, CrystalTag);
            // The base is shut so the mesh stays closed; it sits under the face and is never seen.
            if (i >= 1 && i + 1 < Sides)
                Triangle(soup, baseRing[0], baseRing[i], baseRing[i + 1], inside, CrystalTag);
        }
    }

    // A flat triangle wound counter-clockwise seen from outside (away from the point inside the shape).
    private static void Triangle(List<MeshVertex> soup, Vector3 a, Vector3 b, Vector3 c, Vector3 inside, float length)
    {
        var normal = Vector3.Cross(b - a, c - a);
        if (normal.LengthSquared() < 1e-12f)
            return;
        if (Vector3.Dot(normal, (a + b + c) / 3f - inside) < 0)
        {
            (b, c) = (c, b);
            normal = -normal;
        }

        normal = Vector3.Normalize(normal) * length;
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
