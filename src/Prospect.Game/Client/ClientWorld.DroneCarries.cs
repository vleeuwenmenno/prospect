using Prospect.Engine.Net;
using Prospect.Game.Protocol;

namespace Prospect.Game.Client;

/// <summary>
/// What the client knows of drones carrying something: the drone and the body it holds, as the server sent them. A
/// drone merely near a loose container is not carrying it; only this says which pull is real.
/// </summary>
internal sealed partial class ClientWorld
{
    private readonly DroneCarry[] _carries = new DroneCarry[DroneCarryMessage.MaxCarries];
    private long _carryTick = -1;
    private int _carryCount;

    internal void OnDroneCarries(ref NetReader reader)
    {
        Span<DroneCarry> incoming = stackalloc DroneCarry[DroneCarryMessage.MaxCarries];
        if (!DroneCarryMessage.TryRead(ref reader, incoming, out var tick, out var count) || tick <= _carryTick) return;
        incoming[..count].CopyTo(_carries);
        _carryTick = tick;
        _carryCount = count;
    }

    internal void PublishDroneCarries(SessionSnapshot snapshot)
    {
        snapshot.DroneCarryCount = _carryCount;
        _carries.AsSpan(0, _carryCount).CopyTo(snapshot.DroneCarries);
    }
}
