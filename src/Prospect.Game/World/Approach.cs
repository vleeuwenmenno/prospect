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
    /// Whether a body closing at <paramref name="speed"/> can still be brought to rest within
    /// <paramref name="distance"/> metres: the condition under which a beam may take it at all.
    /// </summary>
    public static bool StopsIn(float acceleration, float speed, float distance, float share) =>
        speed <= 0 || speed * speed <= 2 * acceleration * share * distance;
}
