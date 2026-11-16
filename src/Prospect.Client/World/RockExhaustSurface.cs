using System.Numerics;
using Prospect.Game.World;

namespace Prospect.Client.World;

/// <summary>Immutable groups of the shared finest rock facets; prepared before frames start.</summary>
internal sealed class RockExhaustSurface
{
    private const int Group = 64;
    private readonly Vector3[][] _points = new Vector3[Galaxy.RockShapes][];
    private readonly ExhaustBounds[][] _bounds = new ExhaustBounds[Galaxy.RockShapes][];
    private readonly uint[] _indices;

    public RockExhaustSurface()
    {
        var sphere = AsteroidMesh.Sphere(AsteroidMesh.Subdivisions);
        _indices = sphere.Indices;
        for (var shape = 0; shape < _points.Length; shape++)
        {
            var points = _points[shape] = new Vector3[sphere.Points.Length];
            for (var i = 0; i < points.Length; i++) points[i] = AsteroidShape.Point(shape, sphere.Points[i]);
            var bounds = _bounds[shape] = new ExhaustBounds[(_indices.Length + Group * 3 - 1) / (Group * 3)];
            for (var g = 0; g < bounds.Length; g++)
            {
                var min = new Vector3(float.PositiveInfinity);
                var max = new Vector3(float.NegativeInfinity);
                for (var i = g * Group * 3; i < Math.Min((g + 1) * Group * 3, _indices.Length); i++)
                {
                    min = Vector3.Min(min, points[_indices[i]]);
                    max = Vector3.Max(max, points[_indices[i]]);
                }
                bounds[g] = new(min, max);
            }
        }
    }

    /// <summary>First entry into the unit rock, with its facet normal. No approximation by a sphere.</summary>
    public bool Hit(int shape, Vector3 origin, Vector3 direction, float reach, out float distance, out Vector3 normal)
    {
        distance = reach;
        normal = default;
        if (shape == AsteroidShape.Icicle)
        {
            var enter = 0f;
            var exit = reach;
            for (var face = 0; face < 8; face++)
            {
                var n = new Vector3((face & 1) == 0 ? 1 : -1, (face & 2) == 0 ? 6 : -6, (face & 4) == 0 ? 6 : -6);
                var offset = 1 - Vector3.Dot(n, origin);
                var speed = Vector3.Dot(n, direction);
                if (MathF.Abs(speed) < 1e-8f) { if (offset < 0) return false; continue; }
                var t = offset / speed;
                if (speed < 0 && t > enter) { enter = t; normal = Vector3.Normalize(n); }
                else if (speed > 0) exit = Math.Min(exit, t);
            }
            distance = enter;
            return enter < exit && normal != Vector3.Zero;
        }
        if ((uint)shape >= _points.Length || !float.IsFinite(reach) || reach <= 0) return false;
        var points = _points[shape];
        var bounds = _bounds[shape];
        var found = false;
        for (var g = 0; g < bounds.Length; g++)
        {
            if (!ShieldExhaust.Meets(bounds[g], origin, direction, distance)) continue;
            for (var i = g * Group * 3; i < Math.Min((g + 1) * Group * 3, _indices.Length); i += 3)
            {
                var a = points[_indices[i]]; var b = points[_indices[i + 1]]; var c = points[_indices[i + 2]];
                if (!ShieldExhaust.Intersect(origin, direction, a, b, c, out var t) || !(t < distance)) continue;
                var n = Vector3.Cross(b - a, c - a);
                if (Vector3.Dot(n, direction) >= 0) continue;
                distance = t;
                normal = Vector3.Normalize(n);
                found = true;
            }
        }
        return found;
    }
}
