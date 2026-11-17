using System.Numerics;
using Prospect.Engine.Time;
using Prospect.Game.Client;
using Prospect.Game.World;

namespace Prospect.Client.World;

/// <summary>
/// Retains the last two sets of loose ore chunks the server sent (one every six ticks) and works out where each is at
/// an instant: between the two sets by a straight line and a slerp, past the newest by flying on at its velocity and
/// spin for a bounded time. A set carries no names for what changed, so each chunk of the newest set is matched to its
/// place in the one before once, when the set arrives; a pose then costs no search and no memory.
/// </summary>
internal sealed class OreChunkVisuals
{
    /// <summary>
    /// The most a pose is carried past the newest set, in ticks: the same room <see cref="FreeContainerVisuals.MaxAheadTicks"/>
    /// gives a container, for the own hull's prediction lead and the six ticks between sets. Beyond it the chunk waits.
    /// </summary>
    public const double MaxAheadTicks = FreeContainerVisuals.MaxAheadTicks;

    private readonly OreChunk[] _current = new OreChunk[OreChunks.Capacity];
    private readonly OreChunk[] _previous = new OreChunk[OreChunks.Capacity];
    private readonly int[] _match = new int[OreChunks.Capacity];
    private int _count, _previousCount;
    private long _tick = -1, _previousTick = -1;

    /// <summary>The newest set, in the server's order.</summary>
    public ReadOnlySpan<OreChunk> Items => _current.AsSpan(0, _count);

    /// <summary>Forgets both sets: another world.</summary>
    public void Clear()
    {
        _count = _previousCount = 0;
        _tick = _previousTick = -1;
    }

    /// <summary>
    /// Takes the set of server tick <paramref name="tick"/>; false, changing nothing, when it is not newer than the one
    /// held. Takes no memory.
    /// </summary>
    public bool Update(ReadOnlySpan<OreChunk> items, long tick)
    {
        if (tick <= _tick || items.Length > _current.Length) return false;
        _current.AsSpan(0, _count).CopyTo(_previous);
        _previousCount = _count;
        _previousTick = _tick;
        items.CopyTo(_current);
        _count = items.Length;
        _tick = tick;
        // Both sets are in the pool's order, and a surviving chunk keeps its place relative to the others, so one pass
        // that never goes back finds nearly every match at once; a miss falls back to a search of the whole set.
        var from = 0;
        for (var i = 0; i < _count; i++)
        {
            var id = _current[i].Id;
            var found = -1;
            for (var k = from; k < _previousCount && found < 0; k++)
                if (_previous[k].Id == id) found = k;
            if (found < 0)
                for (var k = 0; k < from && found < 0; k++)
                    if (_previous[k].Id == id) found = k;
            _match[i] = found;
            if (found >= 0) from = found + 1;
        }

        return true;
    }

    /// <summary>
    /// Chunk <paramref name="index"/> of <see cref="Items"/> at <paramref name="tick"/>, in the server's convention (the
    /// state after that interval). Between the last two sets a chunk that was in both is interpolated; otherwise, and past
    /// the newest set, it flies on at its velocity for at most <see cref="MaxAheadTicks"/>.
    /// </summary>
    public OreChunk Pose(int index, double tick)
    {
        var result = _current[index];
        if (_previousTick >= 0 && tick < _tick && _match[index] is var at and >= 0)
        {
            ref readonly var old = ref _previous[at];
            var alpha = (float)Math.Clamp((tick - _previousTick) / Math.Max(1, _tick - _previousTick), 0, 1);
            result.Position = Double3.Lerp(old.Position, result.Position, alpha);
            result.Orientation = Quaternion.Slerp(old.Orientation, result.Orientation, alpha);
            return result;
        }

        var dt = (float)(Math.Clamp(tick - _tick, 0, MaxAheadTicks) / TickLoop.SimulationHz);
        result.Position += result.Velocity * dt;
        var speed = result.AngularVelocity.Length();
        if (speed > 0)
            result.Orientation = Quaternion.Normalize(result.Orientation * Quaternion.CreateFromAxisAngle(result.AngularVelocity / speed, speed * dt));
        return result;
    }

    /// <summary>
    /// The instant chunks are drawn at: that of the own hull, which is predicted ahead of the server, so a chunk beside
    /// it holds still against it at any speed (as <see cref="FreeContainerVisuals.ShownTick"/> does for a container). With
    /// no hull it is the server's own tick.
    /// </summary>
    public static double ShownTick(ReadOnlySpan<HullView> hulls, int own, double serverTick, float alpha) =>
        (uint)own < (uint)hulls.Length ? hulls[own].ShownTick - 1 + alpha : serverTick + alpha;

    /// <summary>
    /// The colour a chunk is drawn in: bare rock's grey, the ore's own colour coming through more the higher the
    /// <paramref name="grade"/>. Brighter and more saturated than the rock it came from, so a loose chunk reads as ore.
    /// </summary>
    public static Vector3 Tint(Vector3 ore, float grade)
    {
        var rock = new Vector3(0.22f, 0.21f, 0.19f);
        return Vector3.Lerp(rock, ore * 0.85f, 0.35f + 0.65f * Math.Clamp(grade, 0f, 1f));
    }
}
