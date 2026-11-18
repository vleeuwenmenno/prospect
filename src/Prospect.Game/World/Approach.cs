using System.Numerics;

namespace Prospect.Game.World;

/// <summary>
/// The one approach rule for a body that a beam or drones bring in: the speed it may still close at, and whether it
/// can be stopped in the room there is.
/// The speed is a share of the speed that would stop it exactly on its goal
/// (<see cref="TractorGuidance.ApproachBrakeShare"/>), so it keeps braking room in hand: a tick in which the pull is
/// skipped then costs room it has to spare, instead of leaving it too fast to stop, after which it flies past its
/// goal — and past the hull — and is never brought in. It still creeps the last metres, because the speed only
/// reaches zero with the distance. Takes no memory.
/// </summary>
public static class Approach
{
    /// <summary>
    /// The speed a body may close at with <paramref name="distance"/> metres left and <paramref name="acceleration"/>
    /// to brake with: <paramref name="share"/> of the speed that would stop it exactly there.
    /// </summary>
    public static float Speed(float acceleration, float distance, float share) =>
        distance > 0 && acceleration > 0 && share > 0 ? MathF.Sqrt(2 * acceleration * share * distance) : 0;

    /// <summary>
    /// The least a held body's path must shrink by in one tick to count as progress: the configured distance, but
    /// never more than the approach rule lets it close at <paramref name="speed"/> in a tick, so a body creeping in at
    /// the speed that rule allows is never counted as stalled.
    /// </summary>
    public static float NeededProgress(float speed, float progressMetres) =>
        Math.Min(progressMetres, speed * HullSim.Dt);

    /// <summary>
    /// What is left for a held body to do before it is home: the <paramref name="path"/> still to go, and the room it
    /// needs to turn the velocity it has into the velocity the rule asks of it (<paramref name="error"/> is how far
    /// apart the two are, in metres per second). An unhindered pull makes this smaller every tick: while the body
    /// still flies the wrong way the path grows, but never as fast as the error shrinks. A body held back by
    /// something (pressed against a rock, pushed back as often as it is pulled in) gains nothing on it. Measuring the
    /// path alone called a body the beam was still braking stalled, since its path only grows until it has stopped.
    /// </summary>
    public static float Left(float path, float error, float acceleration) =>
        acceleration > 0 ? path + error * error / (2 * acceleration) : path;

    /// <summary>
    /// A held body goes along with what the ship holding it does: <paramref name="change"/>, the change in a tick of
    /// the velocity of the point the body is steered against (the ship's thrust, braking and turning), is added to
    /// <paramref name="velocity"/>, so the pull works on the way the body has left to go and not on the ship's own
    /// manoeuvres. No more than <paramref name="acceleration"/> allows in a tick
    /// (<see cref="TractorGuidance.CarryAcceleration"/>): false when the ship did more than that, and the rest is a
    /// lag the pull has to make up.
    /// </summary>
    public static bool Carry(ref Vector3 velocity, Vector3 change, float acceleration)
    {
        var length = change.Length();
        if (!(length > 1e-6f)) return true;
        var most = acceleration * HullSim.Dt;
        if (length <= most) { velocity += change; return true; }
        if (most > 0) velocity += change * (most / length);
        return false;
    }

    /// <summary>
    /// Whether a body closing at <paramref name="speed"/> can still be brought to rest within
    /// <paramref name="distance"/> metres: the condition under which a beam may take it at all.
    /// </summary>
    public static bool StopsIn(float acceleration, float speed, float distance, float share) =>
        speed <= 0 || speed * speed <= 2 * acceleration * share * distance;
}
