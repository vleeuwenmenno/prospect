using System.Numerics;
using Prospect.Engine.Time;
using Prospect.Game.Client;
using Prospect.Game.World;

namespace Prospect.Client.World;

/// <summary>Retains two authoritative sets for smooth, bounded interpolation and contact detection.</summary>
internal sealed class FreeContainerVisuals
{
    private readonly FreeContainer[] _current = new FreeContainer[FreeContainers.Capacity];
    private readonly FreeContainer[] _previous = new FreeContainer[FreeContainers.Capacity];
    private int _count, _previousCount;
    private long _tick = -1, _previousTick;
    public void Clear() { _count = _previousCount = 0; _tick = -1; }
    public ReadOnlySpan<FreeContainer> Items => _current.AsSpan(0, _count);
    public bool Update(ReadOnlySpan<FreeContainer> items, long tick)
    {
        if (tick <= _tick) return false;
        _current.AsSpan(0, _count).CopyTo(_previous);
        _previousCount = _count; _previousTick = _tick;
        items.CopyTo(_current); _count = items.Length; _tick = tick;
        return true;
    }
    public bool Contact(in FreeContainer item, out Vector3 normal)
    {
        normal = default;
        for (var i = 0; i < _previousCount; i++)
        {
            ref var old = ref _previous[i];
            if (old.Id != item.Id) continue;
            // Tractor acceleration and its hand-off are intentional motion, not evidence of a collision.
            if (item.TractorPowered || old.TractorPowered) return false;
            var change = item.Velocity - old.Velocity;
            if (change.LengthSquared() < .01f) return false;
            normal = Vector3.Normalize(change);
            return true;
        }
        return false;
    }
    /// <summary>
    /// The body at <paramref name="tick"/>, in the server's convention (state after that interval). Between the last two
    /// sets it is interpolated; past the newest it flies on at its velocity for at most <see cref="MaxAheadTicks"/>, since
    /// the own hull is drawn ahead of the server (its prediction lead) and a container riding along with it at speed
    /// must be drawn at that same instant, or it trails by the lead and the six-tick interval in metres.
    /// </summary>
    public FreeContainer Pose(in FreeContainer item, double tick)
    {
        var result = item;
        for (var i = 0; i < _previousCount && _previousTick >= 0 && tick < _tick; i++)
        {
            ref var old = ref _previous[i];
            if (old.Id != item.Id) continue;
            var alpha = (float)Math.Clamp((tick - _previousTick) / Math.Max(1, _tick - _previousTick), 0, 1);
            result.Position = Double3.Lerp(old.Position, item.Position, alpha);
            result.Orientation = Quaternion.Slerp(old.Orientation, item.Orientation, alpha);
            return result;
        }
        var dt = (float)(Math.Clamp(tick - _tick, 0, MaxAheadTicks) / TickLoop.SimulationHz);
        result.Position += item.Velocity * dt;
        var speed = item.AngularVelocity.Length();
        if (speed > 0) result.Orientation = Quaternion.Normalize(item.Orientation * Quaternion.CreateFromAxisAngle(item.AngularVelocity / speed, speed * dt));
        return result;
    }

    /// <summary>
    /// The instant a container is shown at: that of the hull it is seen against, so the two agree at any speed. A body a
    /// beam holds goes with the hull holding it, any other with the own hull, which is predicted ahead of the server; a
    /// remote hull is drawn in the past (<see cref="HullView.ShownTick"/>), so a container it holds is too. A hull's drawn
    /// place lies between its last two views, so the instant is its shown tick less one, plus the frame's
    /// <paramref name="alpha"/>. With no hull at all it is the server's own tick.
    /// </summary>
    public static double ShownTick(in FreeContainer item, ReadOnlySpan<HullView> hulls, int own, double serverTick, float alpha)
    {
        if (item.TractorHull != 0)
            foreach (ref readonly var hull in hulls)
                if (hull.Id == item.TractorHull) return hull.ShownTick - 1 + alpha;
        return (uint)own < (uint)hulls.Length ? hulls[own].ShownTick - 1 + alpha : serverTick + alpha;
    }

    /// <summary>
    /// The most a pose is carried past the newest set, in ticks: a prediction lead of up to 16 ticks plus the six between
    /// sets, with room for a late set. Beyond it the body waits rather than flying on unseen.
    /// </summary>
    public const double MaxAheadTicks = 30;
}
