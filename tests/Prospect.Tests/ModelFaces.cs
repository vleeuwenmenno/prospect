using System.Numerics;
using Prospect.Client.Render;

namespace Prospect.Tests;

/// <summary>
/// Faces of a worker-built model that would fight for a pixel: two triangles in one plane, facing the same way, one
/// over the other. <c>mesh.frag</c> shades both alike when they carry the same paint (the normal's length) and the
/// same normal, so the pixel comes out the same whichever wins; faces of different paints flicker, which is what an
/// empty cargo rack's walls did before the seam backing was recessed behind its panels.
/// </summary>
internal static class ModelFaces
{
    /// <summary>A vertex's position.</summary>
    internal static Vector3 Point(MeshVertex v) => new(v.Position.X, v.Position.Y, v.Position.Z);

    /// <summary>
    /// The first pair of differently painted faces that lie in one plane, face the same way and overlap, described, or
    /// null when the mesh has none.
    /// </summary>
    internal static string? Fight(List<MeshVertex> vertices, List<uint> indices)
    {
        var faces = new List<Face>();
        for (var i = 0; i + 2 < indices.Count; i += 3)
        {
            var a = vertices[(int)indices[i]];
            var normal = new Vector3(a.Normal.X, a.Normal.Y, a.Normal.Z);
            var paint = normal.Length();
            faces.Add(new Face(Point(a), Point(vertices[(int)indices[i + 1]]), Point(vertices[(int)indices[i + 2]]), normal / paint, paint));
        }
        for (var i = 0; i < faces.Count; i++)
            for (var j = i + 1; j < faces.Count; j++)
            {
                if (MathF.Abs(faces[i].Paint - faces[j].Paint) < 1e-4f) continue;
                if (!OverlapInOnePlane(faces[i], faces[j], out var area) || area <= 1e-5f) continue;
                return $"faces {i} and {j} lie in one plane at {faces[i].A} facing {faces[i].Normal} over {area:F5} m2";
            }
        return null;
    }

    private readonly record struct Face(Vector3 A, Vector3 B, Vector3 C, Vector3 Normal, float Paint);

    // Whether two faces lie in one plane, face the same way and share an area: they fight for that area's pixels.
    private static bool OverlapInOnePlane(in Face x, in Face y, out float area)
    {
        area = 0;
        if (Vector3.Dot(x.Normal, y.Normal) < .999f) return false;
        if (MathF.Abs(Vector3.Dot(x.Normal, y.A - x.A)) > 1e-5f) return false;
        // Clip one triangle against the other in the plane, dropping the axis the normal is most aligned with.
        var n = Vector3.Abs(x.Normal);
        var axis = n.X >= n.Y && n.X >= n.Z ? 0 : n.Y >= n.Z ? 1 : 2;
        Span<Vector2> polygon = stackalloc Vector2[8];
        Span<Vector2> next = stackalloc Vector2[8];
        Span<Vector2> clip = stackalloc Vector2[3];
        polygon[0] = Flat(x.A, axis); polygon[1] = Flat(x.B, axis); polygon[2] = Flat(x.C, axis);
        clip[0] = Flat(y.A, axis); clip[1] = Flat(y.B, axis); clip[2] = Flat(y.C, axis);
        var count = 3;
        var round = Cross(clip[0], clip[1], clip[2]) > 0 ? 1f : -1f;
        for (var edge = 0; edge < 3 && count > 0; edge++)
        {
            Vector2 from = clip[edge], to = clip[(edge + 1) % 3];
            var kept = 0;
            for (var k = 0; k < count; k++)
            {
                Vector2 here = polygon[k], before = polygon[(k + count - 1) % count];
                var insideHere = round * Cross(from, to, here);
                var insideBefore = round * Cross(from, to, before);
                if (insideHere >= 0)
                {
                    if (insideBefore < 0) next[kept++] = Vector2.Lerp(before, here, insideBefore / (insideBefore - insideHere));
                    next[kept++] = here;
                }
                else if (insideBefore >= 0) next[kept++] = Vector2.Lerp(before, here, insideBefore / (insideBefore - insideHere));
            }
            for (var k = 0; k < kept; k++) polygon[k] = next[k];
            count = kept;
        }
        for (var k = 1; k + 1 < count; k++)
            area += .5f * MathF.Abs(Cross(polygon[0], polygon[k], polygon[k + 1]));
        return area > 0;
    }

    private static Vector2 Flat(Vector3 p, int axis) => axis switch { 0 => new(p.Y, p.Z), 1 => new(p.X, p.Z), _ => new(p.X, p.Y) };

    private static float Cross(Vector2 a, Vector2 b, Vector2 c) => (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
}
