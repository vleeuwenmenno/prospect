using System.Numerics;

namespace Prospect.Game.World;

/// <summary>The fixed tessellation shared by drawing and asteroid contact queries.</summary>
public static class AsteroidMesh
{
    /// <summary>Finest contact mesh: 81,920 triangles.</summary>
    public const int Subdivisions = 6;
    private static readonly (Vector3[] Points, uint[] Indices)[] Levels = BuildLevels();

    /// <summary>Unit directions and outward triangles, in stable subdivision order.</summary>
    public static (Vector3[] Points, uint[] Indices) Sphere(int level) => Levels[level];

    private static (Vector3[], uint[])[] BuildLevels()
    {
        var levels = new (Vector3[], uint[])[Subdivisions + 1];
        for (var i = 0; i < levels.Length; i++) levels[i] = Icosphere(i);
        return levels;
    }

    // Each parent has four consecutive children. Select in the unsquashed direction space;
    // positive radial displacement cannot change which cone contains a triangle.
    internal static int Triangle(Vector3 direction)
    {
        var triangle = 0;
        for (var level = 0; level <= Subdivisions; level++)
        {
            var sphere = Levels[level];
            var first = level == 0 ? 0 : triangle * 4;
            var count = level == 0 ? 20 : 4;
            var best = float.NegativeInfinity;
            for (var i = first; i < first + count; i++)
            {
                var a = sphere.Points[sphere.Indices[i * 3]];
                var b = sphere.Points[sphere.Indices[i * 3 + 1]];
                var c = sphere.Points[sphere.Indices[i * 3 + 2]];
                var score = Math.Min(Vector3.Dot(Vector3.Cross(a, b), direction),
                    Math.Min(Vector3.Dot(Vector3.Cross(b, c), direction), Vector3.Dot(Vector3.Cross(c, a), direction)));
                if (score > best) { best = score; triangle = i; }
            }
        }
        return triangle;
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

}
