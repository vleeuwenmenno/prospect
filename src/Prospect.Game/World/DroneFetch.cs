using System.Numerics;

namespace Prospect.Game.World;

/// <summary>
/// The shared rules of drones fetching a free container: where each carrier holds on, how far ahead of a moving
/// container it aims, how the carriers' pull moves the container, and where they hand it to the hull's tractor beam.
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
    /// How many seconds ahead of a container moving at <paramref name="speed"/> a carrier aims, so its autopilot (which
    /// flies at the speed it can still stop from, <see cref="Autopilot"/>) keeps up instead of trailing behind: the
    /// autopilot settles where <c>0.8 * sqrt(2 a d)</c> equals the speed, which this distance makes the place itself.
    /// </summary>
    public static float Lead(float speed, float weakestPush) => weakestPush > 0 ? speed / (1.28f * weakestPush) : 0;

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
    /// One tick of the carriers' pull: the container's velocity relative to <paramref name="carrier"/> (the hull's
    /// motion at the hand-over point) is steered toward <paramref name="delta"/> (the way left, in the world) at the
    /// speed it can still stop from at <paramref name="acceleration"/>, at most <paramref name="maxSpeed"/>, changing by no
    /// more than the acceleration allows in a tick.
    /// </summary>
    public static void Pull(ref FreeContainer container, Vector3 delta, Vector3 carrier, float acceleration, float maxSpeed)
    {
        var distance = delta.Length();
        var speed = Math.Min(maxSpeed, MathF.Sqrt(2 * acceleration * distance));
        var desired = distance > 1e-6f ? delta / distance * speed : Vector3.Zero;
        var change = desired - (container.Velocity - carrier);
        var length = change.Length();
        if (length > 0) container.Velocity += change * (Math.Min(length, acceleration * HullSim.Dt) / length);
    }
}
