using System.Numerics;

namespace Prospect.Game.World;

/// <summary>
/// How the server steers a drone through the ordinary <see cref="Autopilot"/>, so a client predicts it with the same
/// code from the order's target alone. The autopilot flies to come to rest at its target in the world; a drone's place
/// usually moves with something (its hull, a carried body), so the server hands it a target worked out to make the
/// autopilot ask, this tick, for the velocity the drone should have: the frame's own plus closing on its place at the
/// speed it can still stop from, plus a push away from drones of its group that are too near. It also routes a drone
/// around its own hull's bounding sphere when the straight way would cross it. Takes no memory.
/// </summary>
public static class DroneSteering
{
    /// <summary>The share of the speed it can stop from that the autopilot asks for (<see cref="Autopilot.Steer"/>).</summary>
    private const float Margin = 0.8f;

    /// <summary>
    /// The velocity, in the world, a drone at <paramref name="position"/> should have to reach <paramref name="place"/>
    /// in a frame moving at <paramref name="frame"/>: the frame's velocity plus closing on the place at the speed the
    /// autopilot would fly toward a place at rest with a push of <paramref name="push"/>.
    /// </summary>
    public static Vector3 Wanted(Double3 position, Double3 place, Vector3 frame, float push)
    {
        var delta = place.RelativeTo(position);
        var distance = delta.Length();
        if (distance < 1e-4f || push <= 0) return frame;
        return frame + delta * (Margin * MathF.Sqrt(2f * push * distance) / distance);
    }

    /// <summary>
    /// The target that makes the autopilot of a hull at <paramref name="position"/> moving at <paramref name="velocity"/>
    /// with a weakest push of <paramref name="push"/> ask for <paramref name="wanted"/> this tick: along it, as far as
    /// the speed asked for is the speed the autopilot would fly from there (<c>0.8 * sqrt(2 a d)</c> solved for
    /// <c>d</c>). A slow hull is never handed a target so near that the autopilot would count it as arrived and stop
    /// steering (<see cref="Autopilot.ArrivalDistance"/>) while it is asked to move: it is given one just beyond.
    /// </summary>
    public static Double3 Target(Double3 position, Vector3 velocity, Vector3 wanted, float push)
    {
        var speed = wanted.Length();
        if (speed < 1e-4f || push <= 0) return position;
        var reach = speed * speed / (2f * Margin * Margin * push);
        if (speed >= Autopilot.ArrivalSpeed && velocity.LengthSquared() < Autopilot.ArrivalSpeed * Autopilot.ArrivalSpeed)
            reach = Math.Max(reach, Autopilot.ArrivalDistance * 1.1f);
        return position + wanted * (reach / speed);
    }

    /// <summary>
    /// A way point the autopilot passes rather than stops short of: <paramref name="waypoint"/> pushed out along the way
    /// from <paramref name="position"/> to at least twice <see cref="Autopilot.ArrivalDistance"/>, as the autopilot counts
    /// a slow hull nearer its target than that as arrived.
    /// </summary>
    public static Double3 Beyond(Double3 position, Double3 waypoint)
    {
        var way = waypoint.RelativeTo(position);
        var distance = way.Length();
        var least = 2f * Autopilot.ArrivalDistance;
        return distance >= least || distance < 1e-4f ? waypoint : position + way * (least / distance);
    }

    /// <summary>
    /// Velocity added to keep a drone at <paramref name="offset"/> from another (from it to this drone), moving at
    /// <paramref name="relative"/> to it, at least <paramref name="spacing"/> apart: up to <paramref name="speed"/>, none at
    /// the spacing. It looks <paramref name="ahead"/> seconds on and acts on the nearest the two will come in that time,
    /// pushing to the side they would pass on, so two drones closing fast part before they meet, not after.
    /// </summary>
    public static Vector3 Apart(Vector3 offset, Vector3 relative, float spacing, float speed, float ahead)
    {
        if (spacing <= 0) return Vector3.Zero;
        var closing = relative.LengthSquared();
        var t = closing > 1e-8f ? Math.Clamp(-Vector3.Dot(offset, relative) / closing, 0, ahead) : 0;
        var nearest = offset + relative * t;
        var distance = nearest.Length();
        if (distance >= spacing) return Vector3.Zero;
        // Two drones meeting dead centre part along their present offset, else along a fixed axis, the same everywhere.
        var length = offset.Length();
        var away = distance > 1e-3f ? nearest / distance : length > 1e-4f ? offset / length : Vector3.UnitY;
        return away * (speed * (1f - distance / spacing));
    }

    /// <summary>
    /// A way point around the sphere of <paramref name="radius"/> about <paramref name="centre"/> (a hull's, padded) for
    /// something at <paramref name="position"/> going to <paramref name="target"/>, when the straight way would cross
    /// it. From inside the sphere to a target outside, the way point is straight out from the centre. From outside, it is
    /// on the sphere's surface beyond the point where the straight way passes nearest the centre. A target inside the
    /// sphere (a hatch, a hand-over point) is come at from its own side: first to the sphere's surface over it, around
    /// the sphere if need be, then straight in once within the cone over it. False when the way is clear.
    /// </summary>
    public static bool Around(Double3 position, Double3 target, Double3 centre, float radius, out Double3 waypoint)
    {
        waypoint = target;
        var outside = radius * 1.1f;
        var to = target.RelativeTo(centre);
        var from = position.RelativeTo(centre);
        var fromLength = from.Length();
        var toLength = to.Length();
        var entry = false;
        if (toLength < radius)
        {
            // Both inside: nothing to go round. Within the cone over the target: straight in.
            if (fromLength < radius || toLength < 1e-3f || Vector3.Dot(from, to) > EntryCone * fromLength * toLength) return false;
            to *= outside / toLength;
            toLength = outside;
            entry = true;
        }
        else if (fromLength < radius)
        {
            waypoint = centre + (fromLength > 1e-3f ? from / fromLength : to / toLength) * outside;
            return true;
        }

        var segment = to - from;
        var lengthSquared = segment.LengthSquared();
        var t = lengthSquared > 1e-8f ? -Vector3.Dot(from, segment) / lengthSquared : 0;
        if (t <= 0 || t >= 1)
        {
            if (!entry) return false;
            waypoint = centre + to;
            return true;
        }
        var nearest = from + segment * t;
        var gap = nearest.Length();
        if (gap >= radius)
        {
            if (!entry) return false;
            waypoint = centre + to;
            return true;
        }

        // Straight through the centre: go round on the side of a fixed axis across the way, the same on every machine.
        var side = gap > 1e-3f ? nearest / gap
            : Vector3.Normalize(Vector3.Cross(segment, MathF.Abs(segment.Y) < 0.9f * MathF.Sqrt(lengthSquared) ? Vector3.UnitY : Vector3.UnitX));
        waypoint = centre + side * outside;
        return true;
    }

    /// <summary>The cosine of the cone over a target inside the sphere within which the way in is straight.</summary>
    public const float EntryCone = 0.7f;
}
