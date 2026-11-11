using System.Numerics;

namespace Prospect.Game.World;

/// <summary>
/// A hull's visible blocks at the interpolated frame position, shaped as drawn: <paramref name="Stretch"/> draws it out
/// along its local z (1 is not at all), and the part where <c>dot(p - Position - CutPoint, CutNormal) &gt; 0</c> is cut
/// away. Both are in world axes; a zero <paramref name="CutNormal"/> cuts nothing. <paramref name="Faces"/> holds, per
/// block, the faces drawn in <see cref="HullDesign.ExteriorFaces"/> order; null draws all six, a block as its own cube.
/// Only faces turned to the camera are drawn, and nothing caps a cut, so only those faces can be picked.
/// <paramref name="Aperture"/>, for the pilot's own hull inside its own rift or wormhole, leaves out the points its
/// mesh pass leaves out, counting a point as drawn where more than half of it shows.
/// </summary>
public readonly record struct PickHull(uint Id, Double3 Position, Quaternion Rotation, Vector3[] Blocks, float BlockSize, ulong Mask, float Radius,
    float Stretch = 1, Vector3 CutPoint = default, Vector3 CutNormal = default, byte[]? Faces = null, FtlAperture Aperture = default);

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
            // Everything is worked out in the hull's own axes, from the camera. Rotation keeps lengths, so distances
            // along the local ray stay world metres.
            var inverse = Quaternion.Conjugate(hull.Rotation);
            var origin = Vector3.Transform(-center, inverse);
            var direction = Vector3.Transform(ray, inverse);
            var view = Vector3.Transform(forward, inverse);
            var cut = Vector3.Transform(hull.CutNormal, inverse);
            // A point q from the camera is drawn where dot(q, cut) <= cutAt.
            var cutAt = Vector3.Dot(Vector3.Transform(hull.CutPoint, inverse) - origin, cut);
            var half = new Vector3(hull.BlockSize * 0.5f, hull.BlockSize * 0.5f, hull.BlockSize * 0.5f * stretch);
            // Nothing nearer than the near plane is drawn, so a ray only starts to hit there.
            var rayDepth = Vector3.Dot(ray, forward);
            var start = rayDepth > 0 ? nearPlane / rayDepth : float.PositiveInfinity;
            var nearest = float.PositiveInfinity;
            var padded = float.PositiveInfinity;
            var shown = new Shown(hull.Aperture, hull.Rotation, 1f / pixelsPerTangent);
            for (var b = 0; b < hull.Blocks.Length; b++)
            {
                if ((hull.Mask & (1UL << b)) == 0) continue;
                var block = hull.Blocks[b];
                block.Z *= stretch;
                nearest = Faces(block - origin, direction, half, hull.Faces?[b] ?? AllFaces, cut, cutAt, start, nearest, shown);
            }
            // The pointer is on no drawn face, so the grab is measured to their edges.
            for (var b = 0; b < hull.Blocks.Length && !float.IsFinite(nearest) && rayDepth > 0; b++)
            {
                if ((hull.Mask & (1UL << b)) == 0) continue;
                var block = hull.Blocks[b];
                block.Z *= stretch;
                if (NearFaces(direction / rayDepth, view, pixelsPerTangent, block - origin, half, hull.Faces?[b] ?? AllFaces, cut, cutAt, nearPlane, shown))
                    padded = Math.Min(padded, Math.Max(0, Vector3.Dot(block - origin, direction)));
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

    private const int AllFaces = 0x3F;

    /// <summary>Whether a point of a hull, from the camera in the hull's own axes, is shown by its aperture.</summary>
    private readonly struct Shown(FtlAperture aperture, Quaternion rotation, float pixelAngle)
    {
        public bool At(Vector3 local) => !aperture.Active || aperture.HullShows(Vector3.Transform(local, rotation), pixelAngle) > 0.5f;
    }
    private static readonly Vector3[] Axes = [Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ];

    /// <summary>
    /// The nearest of <paramref name="nearest"/> and the drawn faces of a block, centred at <paramref name="block"/>
    /// from the camera, that the ray meets from <paramref name="start"/> on, on the drawn side of the cut. A face is
    /// only met from its outside, which is the side it is drawn from.
    /// </summary>
    private static float Faces(Vector3 block, Vector3 ray, Vector3 half, int faces, Vector3 cut, float cutAt, float start, float nearest, in Shown shown)
    {
        for (var side = 0; side < 6; side++)
        {
            var axis = side >> 1;
            var sign = (side & 1) == 0 ? 1f : -1f;
            if ((faces & 1 << side) == 0 || ray[axis] * sign >= 0) continue;
            var t = (block[axis] + sign * half[axis]) / ray[axis];
            if (t < start || t >= nearest) continue;
            var offset = ray * t - block;
            var u = (axis + 1) % 3;
            var v = (axis + 2) % 3;
            if (MathF.Abs(offset[u]) <= half[u] && MathF.Abs(offset[v]) <= half[v] && Vector3.Dot(ray * t, cut) <= cutAt && shown.At(ray * t))
                nearest = t;
        }
        return nearest;
    }

    /// <summary>
    /// Whether the pointer is within the grab of a block's drawn faces turned to the camera, each less what is past
    /// the cut and nearer than the near plane. Both planes can cut a face, so each is clipped as a polygon and its
    /// corners need not be the box's.
    /// </summary>
    private static bool NearFaces(Vector3 pointer, Vector3 forward, float focal, Vector3 block, Vector3 half, int faces,
        Vector3 cut, float cutAt, float nearPlane, in Shown shown)
    {
        var squared = GrabPixels * GrabPixels / (focal * focal);
        // A quad clipped by two planes keeps at most six corners.
        Span<Vector3> face = stackalloc Vector3[8];
        Span<Vector3> clipped = stackalloc Vector3[8];
        for (var side = 0; side < 6; side++)
        {
            var axis = side >> 1;
            var sign = (side & 1) == 0 ? 1f : -1f;
            var plane = block[axis] + sign * half[axis];
            // The camera must be outside the face's plane to see it; the renderer culls it otherwise.
            if ((faces & 1 << side) == 0 || plane * sign >= 0) continue;
            var middle = block + Axes[axis] * (sign * half[axis]);
            var u = Axes[(axis + 1) % 3] * half[(axis + 1) % 3];
            var v = Axes[(axis + 2) % 3] * half[(axis + 2) % 3];
            face[0] = middle - u - v;
            face[1] = middle + u - v;
            face[2] = middle + u + v;
            face[3] = middle - u + v;
            var count = Keep(face[..4], clipped, forward, nearPlane);
            count = Keep(clipped[..count], face, -cut, -cutAt);
            // The pointer is on no drawn face, so its nearest point on one is on the edge of what is left of it.
            for (var i = 0; i < count; i++)
            {
                var a = face[i] / Math.Max(nearPlane, Vector3.Dot(face[i], forward));
                var next = face[(i + 1) % count];
                var b = next / Math.Max(nearPlane, Vector3.Dot(next, forward));
                var edge = b - a;
                var length = edge.LengthSquared();
                var t = length == 0 ? 0 : Math.Clamp(Vector3.Dot(pointer - a, edge) / length, 0, 1);
                // The nearest point of the edge as drawn, in its plane: an aperture may hide it there.
                var along = Math.Clamp(Along(a + edge * t, face[i], next), 0, 1);
                if (Vector3.DistanceSquared(pointer, a + edge * t) <= squared && shown.At(Vector3.Lerp(face[i], next, along))) return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Where along the edge from <paramref name="from"/> to <paramref name="to"/>, as a share of it, the ray from the
    /// camera through <paramref name="on"/> meets it: perspective makes this differ from the share on the screen.
    /// </summary>
    private static float Along(Vector3 on, Vector3 from, Vector3 to)
    {
        // from + (to - from) s lies on the ray when its cross product with the ray is zero.
        var edge = Vector3.Cross(to - from, on);
        var start = Vector3.Cross(from, on);
        var squared = edge.LengthSquared();
        return squared < 1e-20f ? 0 : -Vector3.Dot(start, edge) / squared;
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
