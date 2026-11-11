using System.Numerics;

namespace Prospect.Game.World;

/// <summary>A hull's visible blocks at the interpolated frame position.</summary>
public readonly record struct PickHull(uint Id, Double3 Position, Quaternion Rotation, Vector3[] Blocks, float BlockSize, ulong Mask, float Radius);

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
            if (!Sphere(ray, center, hull.Radius + (Math.Max(0, depth) + hull.Radius) * GrabPixels / pixelsPerTangent, out _)) continue;
            var inverse = Quaternion.Conjugate(hull.Rotation);
            var origin = Vector3.Transform(-center, inverse);
            var direction = Vector3.Transform(ray, inverse);
            var nearest = float.PositiveInfinity;
            var padded = float.PositiveInfinity;
            for (var b = 0; b < hull.Blocks.Length; b++)
            {
                if ((hull.Mask & (1UL << b)) == 0) continue;
                var blockOrigin = origin - hull.Blocks[b];
                if (Box(blockOrigin, direction, hull.BlockSize * 0.5f, out var t))
                    nearest = Math.Min(nearest, t);
                else if (NearBlock(ray, forward, pixelsPerTangent, center + Vector3.Transform(hull.Blocks[b], hull.Rotation), hull.Rotation, hull.BlockSize * 0.5f, nearPlane))
                    padded = Math.Min(padded, Math.Max(0, Vector3.Dot(center + Vector3.Transform(hull.Blocks[b], hull.Rotation), ray)));
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

    private static bool NearBlock(Vector3 ray, Vector3 forward, float focal, Vector3 center, Quaternion rotation, float half, float nearPlane)
    {
        var rayDepth = Vector3.Dot(ray, forward);
        if (rayDepth <= 0) return false;
        var pointer = ray / rayDepth;
        Span<Vector3> corners = stackalloc Vector3[8];
        Span<Vector3> projected = stackalloc Vector3[20];
        var count = 0;
        for (var i = 0; i < 8; i++)
        {
            var corner = center + Vector3.Transform(new Vector3((i & 1) == 0 ? -half : half,
                (i & 2) == 0 ? -half : half, (i & 4) == 0 ? -half : half), rotation);
            corners[i] = corner;
            var depth = Vector3.Dot(corner, forward);
            if (depth >= nearPlane) projected[count++] = corner / depth;
        }
        // Clip before division so rotated boxes crossing the camera plane have a finite silhouette.
        for (var i = 0; i < 8; i++)
        {
            for (var bit = 1; bit <= 4; bit *= 2)
            {
                var j = i ^ bit;
                if (j < i) continue;
                var a = corners[i];
                var b = corners[j];
                var da = Vector3.Dot(a, forward);
                var db = Vector3.Dot(b, forward);
                if ((da >= nearPlane) == (db >= nearPlane)) continue;
                projected[count++] = Vector3.Lerp(a, b, (nearPlane - da) / (db - da)) / nearPlane;
            }
        }
        var squared = GrabPixels * GrabPixels / (focal * focal);
        // The exact box test already missed, so the pointer is outside this convex silhouette.
        // Its nearest boundary lies on a segment between clipped vertices; interior chords cannot be nearer.
        for (var i = 0; i < count; i++)
        {
            if (Vector3.DistanceSquared(pointer, projected[i]) <= squared) return true;
            for (var j = 0; j < i; j++)
            {
                var edge = projected[j] - projected[i];
                var length = edge.LengthSquared();
                if (length == 0) continue;
                var t = Math.Clamp(Vector3.Dot(pointer - projected[i], edge) / length, 0, 1);
                if (Vector3.DistanceSquared(pointer, projected[i] + edge * t) <= squared) return true;
            }
        }
        return false;
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

    private static bool Box(Vector3 origin, Vector3 ray, float half, out float distance)
    {
        var near = 0f;
        var far = float.PositiveInfinity;
        for (var axis = 0; axis < 3; axis++)
        {
            if (MathF.Abs(ray[axis]) < 1e-8f)
            {
                if (MathF.Abs(origin[axis]) > half) { distance = 0; return false; }
                continue;
            }
            var a = (-half - origin[axis]) / ray[axis];
            var b = (half - origin[axis]) / ray[axis];
            near = Math.Max(near, Math.Min(a, b));
            far = Math.Min(far, Math.Max(a, b));
        }
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
