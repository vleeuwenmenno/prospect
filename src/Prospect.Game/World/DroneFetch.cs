using System.Numerics;

namespace Prospect.Game.World;

/// <summary>
/// The shared rules of drones fetching a free container: where each carrier holds on, how the carriers' pull moves the
/// container, how fast they may carry it, the way it takes around the hull, and where they hand it to the hull's
/// tractor beam.
/// Drones never stow a container themselves: the beam takes it at the hand-over point and leads it into a rack by
/// <see cref="TractorPath"/>, so packing, the rack's open face and stowing have one rule. Takes no memory.
/// </summary>
public static class DroneFetch
{
    /// <summary>Places around one container: above, below, the four sides, then two upper edges.</summary>
    public const int Places = 8;

    // Directions in the base hull's frame, as the carried container is turned onto the hull's axes.
    private static ReadOnlySpan<sbyte> Directions =>
    [
        0, 1, 0, 0, -1, 0, 1, 0, 0, -1, 0, 0, 0, 0, 1, 0, 0, -1, 1, 1, 0, -1, 1, 0,
    ];

    /// <summary>
    /// The <paramref name="slot"/>-th carrier's place beside a container of half extents <paramref name="half"/>, in
    /// the base hull's frame, from the container's centre: clear of its side by the drone's radius and a gap.
    /// </summary>
    public static Vector3 Place(int slot, Vector3 half, float droneRadius, float gap)
    {
        var entry = (int)((uint)slot % Places);
        var reach = new Vector3(droneRadius + gap);
        var x = Directions[3 * entry]; var y = Directions[3 * entry + 1]; var z = Directions[3 * entry + 2];
        return new Vector3(x * (half.X + reach.X), y * (half.Y + reach.Y), z * (half.Z + reach.Z));
    }

    /// <summary>Drones needed to lift <paramref name="mass"/> kilograms when each carries <paramref name="carryKg"/>.</summary>
    public static int Needed(double mass, float carryKg) => carryKg > 0 ? Math.Max(1, (int)Math.Ceiling(mass / carryKg - 1e-9)) : int.MaxValue;

    /// <summary>
    /// The hand-over point for <paramref name="mount"/>, in the hull's own frame in metres from its pivot: straight out
    /// from the beam's emitter along the face it is mounted on.
    /// </summary>
    public static Vector3 Handover(HullDesign design, int mount, float distance) =>
        TractorReach.Emitter(design.Boxes, design.Mounts[mount], design.BlockSize) + Facings.Vector(design.Mounts[mount].Face) * distance
        - (design.CenterOfMass + new Vector3(.5f)) * design.BlockSize;

    /// <summary>The same point in the grid-origin metres <see cref="TractorReach"/> works in.</summary>
    public static Vector3 HandoverGrid(HullDesign design, int mount, float distance) =>
        TractorReach.Emitter(design.Boxes, design.Mounts[mount], design.BlockSize) + Facings.Vector(design.Mounts[mount].Face) * distance;

    /// <summary>
    /// The acceleration the carriers give a body of <paramref name="mass"/> kilograms when those holding on carry
    /// <paramref name="holding"/> together: <paramref name="acceleration"/> scaled by how much more they carry than it
    /// weighs, but never more than <paramref name="share"/> of <paramref name="weakestPush"/>, the push of the weakest
    /// of them, so the drones can always keep their places beside it.
    /// </summary>
    public static float CarryAcceleration(float acceleration, float holding, double mass, float share, float weakestPush) =>
        Math.Min(acceleration * holding / (float)Math.Max(mass, 1e-3), share * weakestPush);

    /// <summary>
    /// Where a carried body heads this tick for <paramref name="handover"/>: straight there, or round the hull's sphere
    /// (<paramref name="centre"/>, <paramref name="hullRadius"/>) padded by the body's own reach and
    /// <paramref name="clearance"/> when the straight way would cross it (<see cref="DroneSteering.Around"/>).
    /// </summary>
    public static Double3 CarryGoal(Double3 body, Vector3 half, Double3 handover, Double3 centre, float hullRadius, float clearance) =>
        DroneSteering.Around(body, handover, centre, hullRadius + half.Length() + clearance, out var waypoint) ? waypoint : handover;

    /// <summary>
    /// One tick of the carriers' pull: the container's velocity relative to <paramref name="carrier"/> (the hull's
    /// motion at the hand-over point) is steered toward <paramref name="delta"/> (the way left, in the world) at the
    /// speed it can still stop from at <paramref name="acceleration"/>, at most <paramref name="maxSpeed"/>, changing by no
    /// more than the acceleration allows in a tick.
    /// </summary>
    public static void Pull(ref FreeContainer container, Vector3 delta, Vector3 carrier, float acceleration, float maxSpeed) =>
        Pull(ref container.Velocity, delta, carrier, acceleration, maxSpeed);

    /// <summary>The same pull on any carried body's velocity: an ore chunk is carried by the rule a container is.</summary>
    public static void Pull(ref Vector3 velocity, Vector3 delta, Vector3 carrier, float acceleration, float maxSpeed)
    {
        var distance = delta.Length();
        var speed = Math.Min(maxSpeed, MathF.Sqrt(2 * acceleration * distance));
        var desired = distance > 1e-6f ? delta / distance * speed : Vector3.Zero;
        var change = desired - (velocity - carrier);
        var length = change.Length();
        if (length > 0) velocity += change * (Math.Min(length, acceleration * HullSim.Dt) / length);
    }
}
