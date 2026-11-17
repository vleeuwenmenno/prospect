using System.Numerics;

namespace Prospect.Game.World;

/// <summary>
/// Where a mining laser sits on its hull and which way it may fire. Shared by the server, which fires, and the client,
/// which draws the beam from the same point: the emitter is the outer face of the mount's box, the outward direction
/// the normal of the face it sits on.
/// </summary>
public static class LaserAim
{
    /// <summary>The design's pivot in grid metres: a hull's position is this point of its grid.</summary>
    public static Vector3 Pivot(HullDesign design) => (design.CenterOfMass + new Vector3(.5f)) * design.BlockSize;

    /// <summary>
    /// The emitting point of mount <paramref name="mount"/> in grid metres (as <see cref="TractorReach"/> works), and
    /// its outward normal in the hull's frame.
    /// </summary>
    public static void Local(HullDesign design, int mount, out Vector3 grid, out Vector3 outward)
    {
        ref readonly var part = ref design.Mounts[mount];
        grid = TractorReach.Emitter(design.Boxes, part, design.BlockSize);
        outward = Facings.Vector(part.Face);
    }

    /// <summary>The emitting point and outward normal in the world, for a hull at <paramref name="position"/> turned by <paramref name="orientation"/>.</summary>
    public static void World(HullDesign design, int mount, Double3 position, Quaternion orientation, out Double3 point, out Vector3 outward)
    {
        Local(design, mount, out var grid, out var normal);
        point = position + HullRotation.Rotate(grid - Pivot(design), orientation);
        outward = HullRotation.Rotate(normal, orientation);
    }
}

/// <summary>Finds the first rock surface along a ray, among the generated rocks of the sectors the ray crosses, scrapes included.</summary>
public static class RockRay
{
    /// <summary>
    /// The first rock that <paramref name="direction"/> (unit) from <paramref name="origin"/> meets within
    /// <paramref name="length"/> metres, and how far along it is. Ice shards and rocks too far off the line are
    /// ruled out on their bounding spheres before any surface is sampled. Takes no memory; may generate a sector the
    /// field has not cached, as contact does.
    /// </summary>
    public static bool First(RockField rocks, Double3 origin, Vector3 direction, float length, double tick, out RockRef rock, out float distance)
    {
        rock = default;
        distance = float.PositiveInfinity;
        if (!(length > 0) || !float.IsFinite(length)) return false;
        var galaxy = rocks.Galaxy;
        var end = origin + direction * length;
        var low = galaxy.SectorOf(new Double3(Math.Min(origin.X, end.X), Math.Min(origin.Y, end.Y), Math.Min(origin.Z, end.Z)));
        var high = galaxy.SectorOf(new Double3(Math.Max(origin.X, end.X), Math.Max(origin.Y, end.Y), Math.Max(origin.Z, end.Z)));
        if (high.X - low.X is < 0 or > 1 || high.Y - low.Y is < 0 or > 1 || high.Z - low.Z is < 0 or > 1) return false;
        var half = length * 0.5f;
        for (var x = low.X; x <= high.X; x++)
            for (var y = low.Y; y <= high.Y; y++)
                for (var z = low.Z; z <= high.Z; z++)
                {
                    var coord = new SectorCoord(x, y, z);
                    var content = rocks.Sector(coord);
                    if (content.AsteroidCount == 0) continue;
                    var centre = galaxy.CenterOf(coord);
                    var start = origin.RelativeTo(centre);
                    var middle = start + direction * half;
                    var moved = rocks.MovedIn(coord);
                    var near = content.Rocks.Near(middle, half);
                    while (near.Next(out var i))
                    {
                        if (moved is not null && moved.EntryOf(i) > 0) continue;
                        Test(rocks, coord, i, content.Asteroids[i], start, direction, default, ref rock, ref distance);
                    }

                    if (moved is null) continue;
                    for (var entry = moved.First; entry >= 0; entry = moved.Next(entry))
                    {
                        var i = rocks.DeltaAt(entry).Rock.Index;
                        if (i >= content.AsteroidCount) continue;
                        ref readonly var candidate = ref content.Asteroids[i];
                        var bound = candidate.Radius + half + rocks.ReachAt(entry);
                        if ((middle - candidate.LocalPosition).LengthSquared() >= bound * bound) continue;
                        rocks.DeltaAt(entry).Evaluate(tick, galaxy.DriftDecay, out var offset, out _);
                        Test(rocks, coord, i, candidate, start, direction, offset, ref rock, ref distance);
                    }
                }

        if (distance > length) return false;
        return distance < float.PositiveInfinity;

        static void Test(RockField rocks, SectorCoord coord, int index, in Asteroid candidate, Vector3 start, Vector3 direction, Vector3 offset,
            ref RockRef rock, ref float distance)
        {
            if (!RockEditing.RockEditRules.Scrapable(candidate.Shape) && candidate.Shape != AsteroidShape.Icicle) return;
            var relative = candidate.LocalPosition + offset - start;
            var ahead = Vector3.Dot(relative, direction);
            if (ahead < -candidate.Radius || ahead - candidate.Radius > distance) return;
            if (Vector3.Cross(relative, direction).LengthSquared() > candidate.Radius * candidate.Radius * 1.05f) return;
            var name = new RockRef(coord, index);
            var cut = rocks.Edits is { } edits ? edits.CutOf(name) : default;
            if (!RockPicking.Hit(direction, relative, candidate, cut, distance < float.PositiveInfinity ? distance : ahead + candidate.Radius * 1.05f, out var hit) || hit >= distance) return;
            distance = hit;
            rock = name;
        }
    }
}
