using System.Numerics;
using Prospect.Engine.Time;
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
    public FreeContainer Pose(in FreeContainer item, double tick)
    {
        var result = item;
        // One network interval behind authority, then at most 0.2 s of drift if updates stall.
        var shown = tick - 6;
        for (var i = 0; i < _previousCount && _previousTick >= 0 && shown < _tick; i++)
        {
            ref var old = ref _previous[i];
            if (old.Id != item.Id) continue;
            var alpha = (float)Math.Clamp((shown - _previousTick) / Math.Max(1, _tick - _previousTick), 0, 1);
            result.Position = Double3.Lerp(old.Position, item.Position, alpha);
            result.Orientation = Quaternion.Slerp(old.Orientation, item.Orientation, alpha);
            return result;
        }
        var dt = (float)Math.Clamp((shown - _tick) / TickLoop.SimulationHz, 0, .2);
        result.Position += item.Velocity * dt;
        var speed = item.AngularVelocity.Length();
        if (speed > 0) result.Orientation = Quaternion.Normalize(item.Orientation * Quaternion.CreateFromAxisAngle(item.AngularVelocity / speed, speed * dt));
        return result;
    }
}
