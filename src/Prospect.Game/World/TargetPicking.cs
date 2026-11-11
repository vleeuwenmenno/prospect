using System.Numerics;

namespace Prospect.Game.World;

/// <summary>
/// A hull's visible blocks at the interpolated frame position, shaped as drawn: <paramref name="Stretch"/> draws it out
/// along its local z (1 is not at all), and the part where <c>dot(p - Position - CutPoint, CutNormal) &gt; 0</c> is cut
/// away. Both are in world axes; a zero <paramref name="CutNormal"/> cuts nothing.
/// </summary>
public readonly record struct PickHull(uint Id, Double3 Position, Quaternion Rotation, Vector3[] Blocks, float BlockSize, ulong Mask, float Radius,
    float Stretch = 1, Vector3 CutPoint = default, Vector3 CutNormal = default);

/// <summary>A loaded sector and its current displacement offsets, borrowed without copying.</summary>
public readonly record struct PickSector(SectorCoord Id, Double3 Center, Asteroid[] Rocks, int Count, Vector3[]? Offsets, Vector3 BoundsCenter = default, float BoundsRadius = 0);

/// <summary>A target and its first intersection distance. Hulls have priority over rocks.</summary>
public readonly record struct PickHit(uint Hull, RockRef? Rock, float Distance) : IComparable<PickHit>
{
    /// <summary>Orders hulls first, then surface distance.</summary>
    public int CompareTo(PickHit other)
    {
        var priority = (Hull == 0).CompareTo(other.Hull == 0);
        if (priority != 0) return priority;
        var depth = Distance.CompareTo(other.Distance);
        if (depth != 0) return depth;
        return Hull.CompareTo(other.Hull);
    }
}

/// <summary>Window-independent, allocation-free picking shared by both camera modes.</summary>
public static class TargetPicking
{
    /// <summary>The minimum pick radius in window pixels, shared by hulls and small rocks.</summary>
    public const float GrabPixels = 12;

    /// <summary>Matches frame interpolation without sweeping a hull across a jump discontinuity.</summary>
    public static Double3 HullPosition(Double3 previous, Double3 current, float alpha, float jumpGap) =>
        current.RelativeTo(previous).LengthSquared() > jumpGap * jumpGap ? current : Double3.Lerp(previous, current, alpha);

    /// <summary>
    /// Writes ordered hits to caller storage large enough for every candidate. Ray is unit length;
    /// forward and pixelsPerTangent describe the perspective projection (height / (2 tan(fov/2))).
    /// </summary>
    public static int Pick(Vector3 ray, Double3 camera, Vector3 forward, float pixelsPerTangent,
        ReadOnlySpan<PickHull> hulls, ReadOnlySpan<PickSector> sectors, Span<PickHit> hits, float minimumRockPixels = 0, float nearPlane = 0.25f)
    {
        var count = 0;
        foreach (var hull in hulls)
        {
            var center = hull.Position.RelativeTo(camera);
            var depth = Vector3.Dot(center, forward);
            var stretch = Math.Max(1f, hull.Stretch);
            // Drawing out along z never moves a point further from the centre than the stretch times its distance.
            var radius = hull.Radius * stretch;
            if (!Sphere(ray, center, radius + (Math.Max(0, depth) + radius) * GrabPixels / pixelsPerTangent, out _)) continue;
            var inverse = Quaternion.Conjugate(hull.Rotation);
            // Rotation keeps lengths, so distances along the local ray stay world metres.
            var origin = Vector3.Transform(-center, inverse);
            var direction = Vector3.Transform(ray, inverse);
            var cut = Vector3.Transform(hull.CutNormal, inverse);
            var cutAt = Vector3.Dot(Vector3.Transform(hull.CutPoint, inverse), cut);
            var cutWorld = Vector3.Dot(center + hull.CutPoint, hull.CutNormal);
            var half = new Vector3(hull.BlockSize * 0.5f, hull.BlockSize * 0.5f, hull.BlockSize * 0.5f * stretch);
            // Nothing nearer than the near plane is drawn, so a ray only starts to hit there.
            var rayDepth = Vector3.Dot(ray, forward);
            var start = rayDepth > 0 ? nearPlane / rayDepth : float.PositiveInfinity;
            var nearest = float.PositiveInfinity;
            var padded = float.PositiveInfinity;
            for (var b = 0; b < hull.Blocks.Length; b++)
            {
                if ((hull.Mask & (1UL << b)) == 0) continue;
                var block = hull.Blocks[b];
                block.Z *= stretch;
                if (Box(origin - block, direction, half, cut, cutAt - Vector3.Dot(block, cut), start, out var t))
                {
                    nearest = Math.Min(nearest, t);
                    continue;
                }
                var world = center + Vector3.Transform(block, hull.Rotation);
                if (NearBlock(ray, forward, pixelsPerTangent, world, hull.Rotation, half, hull.CutNormal, cutWorld, nearPlane))
                    padded = Math.Min(padded, Math.Max(0, Vector3.Dot(world, ray)));
            }
            if (!float.IsFinite(nearest)) nearest = padded;
            if (float.IsFinite(nearest)) hits[count++] = new(hull.Id, null, nearest);
        }
        foreach (var sector in sectors)
        {
            var center = sector.Center.RelativeTo(camera);
            if (sector.Offsets is null && sector.BoundsRadius > 0)
            {
                var bounds = center + sector.BoundsCenter;
                var radius = sector.BoundsRadius * 1.02f;
                var padding = (Math.Max(0, Vector3.Dot(bounds, forward)) + radius) * GrabPixels / pixelsPerTangent;
                if (!Sphere(ray, bounds, radius + padding, out _)) continue;
            }
            for (var i = 0; i < sector.Count; i++)
            {
                ref readonly var rock = ref sector.Rocks[i];
                var position = center + rock.LocalPosition + (sector.Offsets is null ? Vector3.Zero : sector.Offsets[i]);
                var depth = Vector3.Dot(position, forward);
                if (depth > rock.Radius && rock.Radius * pixelsPerTangent / depth < minimumRockPixels) continue;
                var grab = Math.Max(0, depth) * GrabPixels / pixelsPerTangent;
                // Bounding rejection is cheap even for a dense sector; only nearby rays evaluate noise.
                if (!Sphere(ray, position, Math.Max(rock.Radius * 1.02f, grab), out var distance)) continue;
                if (!Sphere(ray, position, rock.Radius * 1.02f, out _) || !RockPicking.Hit(ray, position, rock, out distance))
                {
                    if (rock.Radius >= grab || !NearPoint(ray, position, forward, pixelsPerTangent)) continue;
                    distance = Math.Max(0, Vector3.Dot(position, ray));
                }
                hits[count++] = new(0, new RockRef(sector.Id, i), distance);
            }
        }
        hits[..count].Sort();
        return count;
    }

    private static bool NearPoint(Vector3 ray, Vector3 point, Vector3 forward, float focal)
    {
        var depth = Vector3.Dot(point, forward);
        var rayDepth = Vector3.Dot(ray, forward);
        if (depth <= 0 || rayDepth <= 0) return false;
        return (point / depth - ray / rayDepth).LengthSquared() <= GrabPixels * GrabPixels / (focal * focal);
    }

    /// <summary>
    /// Whether the pointer is within the grab of a block's silhouette: the box, less what is past the cut plane
    /// (<c>dot(p, cut) &gt; cutAt</c>) and nearer than the near plane. Every corner of that solid lies on a face of
    /// the box, so each face is clipped by both planes and the edges of what is left are its silhouette's edges.
    /// </summary>
    private static bool NearBlock(Vector3 ray, Vector3 forward, float focal, Vector3 center, Quaternion rotation, Vector3 half,
        Vector3 cut, float cutAt, float nearPlane)
    {
        var rayDepth = Vector3.Dot(ray, forward);
        if (rayDepth <= 0) return false;
        var pointer = ray / rayDepth;
        var squared = GrabPixels * GrabPixels / (focal * focal);
        Span<Vector3> axes = stackalloc Vector3[] { Vector3.Transform(Vector3.UnitX, rotation) * half.X,
            Vector3.Transform(Vector3.UnitY, rotation) * half.Y, Vector3.Transform(Vector3.UnitZ, rotation) * half.Z };
        // A quad clipped by two planes keeps at most six corners.
        Span<Vector3> face = stackalloc Vector3[8];
        Span<Vector3> clipped = stackalloc Vector3[8];
        for (var side = 0; side < 6; side++)
        {
            var normal = axes[side >> 1] * ((side & 1) == 0 ? -1 : 1);
            var u = axes[((side >> 1) + 1) % 3];
            var v = axes[((side >> 1) + 2) % 3];
            var middle = center + normal;
            face[0] = middle - u - v;
            face[1] = middle + u - v;
            face[2] = middle + u + v;
            face[3] = middle - u + v;
            var count = Keep(face[..4], clipped, forward, nearPlane);
            count = Keep(clipped[..count], face, -cut, -cutAt);
            // The exact box test already missed, so the pointer is outside this convex silhouette and its nearest
            // point is on an edge of the solid; edges inside the silhouette are never nearer.
            for (var i = 0; i < count; i++)
            {
                var a = face[i] / Math.Max(nearPlane, Vector3.Dot(face[i], forward));
                var next = face[(i + 1) % count];
                var b = next / Math.Max(nearPlane, Vector3.Dot(next, forward));
                var edge = b - a;
                var length = edge.LengthSquared();
                var t = length == 0 ? 0 : Math.Clamp(Vector3.Dot(pointer - a, edge) / length, 0, 1);
                if (Vector3.DistanceSquared(pointer, a + edge * t) <= squared) return true;
            }
        }
        return false;
    }

    /// <summary>Clips a convex polygon to the side where <c>dot(p, normal) &gt;= at</c>; returns the corners kept.</summary>
    private static int Keep(ReadOnlySpan<Vector3> polygon, Span<Vector3> kept, Vector3 normal, float at)
    {
        var count = 0;
        for (var i = 0; i < polygon.Length; i++)
        {
            var a = polygon[i];
            var b = polygon[(i + 1) % polygon.Length];
            var da = Vector3.Dot(a, normal) - at;
            var db = Vector3.Dot(b, normal) - at;
            if (da >= 0) kept[count++] = a;
            if ((da >= 0) != (db >= 0)) kept[count++] = Vector3.Lerp(a, b, da / (da - db));
        }
        return count;
    }

    private static bool Sphere(Vector3 ray, Vector3 center, float radius, out float distance)
    {
        var along = Vector3.Dot(center, ray);
        var off = Vector3.Cross(center, ray).LengthSquared();
        var square = radius * radius - off;
        distance = 0;
        if (square < 0 || along + MathF.Sqrt(square) < 0) return false;
        distance = Math.Max(0, along - MathF.Sqrt(square));
        return true;
    }

    /// <summary>
    /// The first point along the ray, from <paramref name="near"/> on, inside the box and on the shown side of
    /// <c>dot(p, cut) &lt;= cutAt</c>.
    /// </summary>
    private static bool Box(Vector3 origin, Vector3 ray, Vector3 half, Vector3 cut, float cutAt, float near, out float distance)
    {
        var far = float.PositiveInfinity;
        for (var axis = 0; axis < 3; axis++)
        {
            if (MathF.Abs(ray[axis]) < 1e-8f)
            {
                if (MathF.Abs(origin[axis]) > half[axis]) { distance = 0; return false; }
                continue;
            }
            var a = (-half[axis] - origin[axis]) / ray[axis];
            var b = (half[axis] - origin[axis]) / ray[axis];
            near = Math.Max(near, Math.Min(a, b));
            far = Math.Min(far, Math.Max(a, b));
        }
        var past = Vector3.Dot(origin, cut) - cutAt;
        var rate = Vector3.Dot(ray, cut);
        if (rate > 0) far = Math.Min(far, -past / rate);
        else if (rate < 0) near = Math.Max(near, -past / rate);
        else if (past > 0) { distance = 0; return false; }
        distance = near;
        return far >= near;
    }
}

/// <summary>Cycles a stable click through ordered targets; pointer travel resets the cycle.</summary>
public struct PickCycle
{
    private Vector2 _anchor;
    private bool _active;
    private PickHit _last;

    /// <summary>External selection changes invalidate the click sequence.</summary>
    public void Reset() => _active = false;

    /// <summary>Advances independently of the number of targets in an additive selection.</summary>
    public PickHit Click(ReadOnlySpan<PickHit> hits, Vector2 pointer) => Click(hits, pointer, _last.Hull, _last.Rock);

    /// <summary>Observe every pointer update, including movement away and back between clicks.</summary>
    public void Observe(Vector2 pointer)
    {
        if (Vector2.DistanceSquared(pointer, _anchor) > 16) _active = false;
    }

    /// <summary>Selects the next hit only when the current selection belongs to this click location.</summary>
    public PickHit Click(ReadOnlySpan<PickHit> hits, Vector2 pointer, uint hull, RockRef? rock)
    {
        Observe(pointer);
        var next = 0;
        if (_active)
            for (var i = 0; i < hits.Length; i++)
                if (hits[i].Hull == hull && hits[i].Rock == rock) { next = (i + 1) % hits.Length; break; }
        _anchor = pointer;
        _active = true;
        _last = hits.IsEmpty ? default : hits[next];
        return _last;
    }
}
