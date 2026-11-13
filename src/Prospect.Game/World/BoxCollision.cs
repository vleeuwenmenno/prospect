using System.Numerics;

namespace Prospect.Game.World;

/// <summary>Immutable median tree in metres about the original pivot; leaves retain file-order ids.</summary>
internal sealed class BoxCollision
{
    internal readonly record struct Node(Vector3 Min, Vector3 Max, int Id, int End);
    private readonly Node[] _nodes;
    private readonly int[] _parents;
    private readonly int[] _leaves;
    internal int[] IntactCounts { get; }

    /// <summary>How many nodes the tree has: a walk is done when its cursor reaches this.</summary>
    internal int NodeCount => _nodes.Length;
    internal void Lose(Span<int> counts, int block)
    {
        for (var node = _leaves[block]; node >= 0; node = _parents[node]) counts[node]--;
    }
    internal BoxCollision(Vector3[] centers, Vector3[] halves)
    {
        _nodes = new Node[centers.Length * 2 - 1];
        _parents = new int[_nodes.Length];
        _leaves = new int[centers.Length];
        IntactCounts = new int[_nodes.Length];
        var ids = Enumerable.Range(0, centers.Length).ToArray();
        var next = 0;
        Build(0, ids.Length, -1);
        void Build(int start, int count, int parent)
        {
            var slot = next++;
            _parents[slot] = parent; IntactCounts[slot] = count;
            if (count == 1) _leaves[ids[start]] = slot;
            var low = new Vector3(float.PositiveInfinity); var high = new Vector3(float.NegativeInfinity);
            for (var i = start; i < start + count; i++)
            { var id = ids[i]; low = Vector3.Min(low, centers[id] - halves[id]); high = Vector3.Max(high, centers[id] + halves[id]); }
            if (count > 1)
            {
                var extent = high - low;
                var axis = extent.X >= extent.Y && extent.X >= extent.Z ? 0 : extent.Y >= extent.Z ? 1 : 2;
                Array.Sort(ids, start, count, Comparer<int>.Create((a, b) => { var c = centers[a][axis].CompareTo(centers[b][axis]); return c == 0 ? a.CompareTo(b) : c; }));
                Build(start, count / 2, slot); Build(start + count / 2, count - count / 2, slot);
            }
            _nodes[slot] = new Node(low, high, count == 1 ? ids[start] : -1, next);
        }
    }

    internal bool Next(ref int cursor, Vector3 low, Vector3 high, ReadOnlySpan<ulong> alive, ref int visits, out int id, ReadOnlySpan<int> counts = default)
    {
        while (cursor < _nodes.Length && visits > 0)
        {
            visits--;
            var index = cursor++;
            var n = _nodes[index];
            if (!counts.IsEmpty && counts[index] == 0) { cursor = n.End; continue; }
            if (low.X > n.Max.X || low.Y > n.Max.Y || low.Z > n.Max.Z || high.X < n.Min.X || high.Y < n.Min.Y || high.Z < n.Min.Z)
            { cursor = n.End; continue; }
            if (n.Id >= 0 && BlockSet.Has(alive, n.Id)) { id = n.Id; return true; }
        }
        id = -1; return false;
    }

    internal static Vector3 Bounds(Vector3 half, Quaternion rotation) =>
        Vector3.Abs(Vector3.Transform(Vector3.UnitX, rotation)) * half.X +
        Vector3.Abs(Vector3.Transform(Vector3.UnitY, rotation)) * half.Y +
        Vector3.Abs(Vector3.Transform(Vector3.UnitZ, rotation)) * half.Z;

    // Fifteen separating axes, in a fixed order. Cross axes near zero carry no independent separation.
    internal static bool Contact(Vector3 between, Vector3 a, Quaternion rotation, Vector3 b, out Vector3 normal, out float depth)
    {
        Span<Vector3> axes = stackalloc Vector3[6];
        axes[0] = Vector3.UnitX; axes[1] = Vector3.UnitY; axes[2] = Vector3.UnitZ;
        axes[3] = Vector3.Transform(Vector3.UnitX, rotation);
        axes[4] = Vector3.Transform(Vector3.UnitY, rotation);
        axes[5] = Vector3.Transform(Vector3.UnitZ, rotation);
        normal = Vector3.UnitY; depth = float.PositiveInfinity;
        for (var i = 0; i < 15; i++)
        {
            var axis = i < 6 ? axes[i] : Vector3.Cross(axes[(i - 6) / 3], axes[3 + (i - 6) % 3]);
            var length = axis.LengthSquared();
            if (length < 1e-12f) continue;
            axis /= MathF.Sqrt(length);
            var radius = Vector3.Dot(Vector3.Abs(axis), b) +
                a.X * MathF.Abs(Vector3.Dot(axis, axes[3])) + a.Y * MathF.Abs(Vector3.Dot(axis, axes[4])) + a.Z * MathF.Abs(Vector3.Dot(axis, axes[5]));
            var along = Vector3.Dot(between, axis);
            var overlap = radius - MathF.Abs(along);
            if (overlap <= 1e-5f) return false;
            if (overlap < depth) { depth = overlap; normal = along < 0 ? -axis : axis; }
        }
        return true;
    }

    internal static bool Contact(Vector3 between, Vector3 a, Quaternion rotation, Vector3 b,
        BlockShape shapeA, BlockShape shapeB, out Vector3 normal, out float depth)
    {
        if (shapeA == BlockShape.Box && shapeB == BlockShape.Box)
            return Contact(between, a, rotation, b, out normal, out depth);
        Span<Vector3> faces = stackalloc Vector3[8];
        Span<Vector3> edgesA = stackalloc Vector3[4];
        Span<Vector3> edgesB = stackalloc Vector3[4];
        faces[0] = edgesB[0] = Vector3.UnitX; faces[1] = edgesB[1] = Vector3.UnitY; faces[2] = edgesB[2] = Vector3.UnitZ;
        for (var i = 0; i < 3; i++) faces[3 + i] = edgesA[i] = Vector3.Transform(edgesB[i], rotation);
        var count = 6; var na = 3; var nb = 3;
        if (shapeA != BlockShape.Box)
        {
            var n = BlockShapes.Slant(shapeA, a);
            faces[count++] = Vector3.Transform(n, rotation);
            BlockShapes.Decode(shapeA, out var e, out _, out _, out _, out _);
            var axis = Vector3.Zero; axis[e] = 1;
            edgesA[na++] = Vector3.Transform(Vector3.Cross(n, axis), rotation);
        }
        if (shapeB != BlockShape.Box)
        {
            var n = BlockShapes.Slant(shapeB, b);
            faces[count++] = n;
            BlockShapes.Decode(shapeB, out var e, out _, out _, out _, out _);
            var axis = Vector3.Zero; axis[e] = 1;
            edgesB[nb++] = Vector3.Cross(n, axis);
        }
        normal = Vector3.UnitY; depth = float.PositiveInfinity;
        var inverse = Quaternion.Conjugate(rotation);
        for (var i = 0; i < count + na * nb; i++)
        {
            var axis = i < count ? faces[i] : Vector3.Cross(edgesA[(i - count) / nb], edgesB[(i - count) % nb]);
            var length = axis.LengthSquared();
            if (length < 1e-12f) continue;
            axis /= MathF.Sqrt(length);
            var local = Vector3.Transform(axis, inverse);
            var along = Vector3.Dot(between, axis);
            var positive = BlockShapes.Support(shapeB, b, axis) + BlockShapes.Support(shapeA, a, -local) - along;
            var negative = BlockShapes.Support(shapeB, b, -axis) + BlockShapes.Support(shapeA, a, local) + along;
            var overlap = Math.Min(positive, negative);
            if (overlap <= 1e-5f) return false;
            if (overlap < depth) { depth = overlap; normal = positive <= negative ? axis : -axis; }
        }
        return true;
    }

    // shortcut: finite box samples can miss a narrow rock ridge between samples; refine adaptively in stage 3.
    internal static bool Rock(Vector3 center, Vector3 half, Quaternion orientation, in Asteroid rock, out Vector3 normal, out float depth, out Vector3 point, BlockShape shape = BlockShape.Box)
    {
        normal = Vector3.UnitY; depth = 0; point = default;
        var inverse = Quaternion.Conjugate(orientation);
        var nearest = BlockShapes.Closest(shape, half, Vector3.Transform(-center, inverse));
        var toRock = Quaternion.Conjugate(rock.Rotation);
        Span<Vector3> vertices = stackalloc Vector3[8];
        var count = BlockShapes.Vertices(shape, half, vertices);
        for (var i = -1; i < count; i++)
        {
            var local = i < 0 ? nearest : vertices[i];
            var p = center + Vector3.Transform(local, orientation);
            var distance = p.Length();
            var direction = distance > 1e-6f ? p / distance : Vector3.UnitY;
            if (distance >= rock.Radius) continue;
            var penetration = rock.Radius * AsteroidShape.RadiusAlong(rock.Shape, Vector3.Transform(direction, toRock)) - distance;
            if (penetration > depth) { depth = penetration; normal = direction; point = Vector3.Transform(local, orientation); }
        }
        return depth > 0;
    }
}
