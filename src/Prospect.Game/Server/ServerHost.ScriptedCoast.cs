using System.Numerics;
using Prospect.Game.World;

namespace Prospect.Game.Server;

public sealed partial class ServerHost
{
    private float? _scriptedCoastSpeed;

    /// <summary>
    /// Capture only (<c>--coast</c>): two seconds in, every player's hull is set flying along its nose at the scripted
    /// speed, once. The client turns its dampeners off for it, so the hull coasts and prediction agrees; nothing pins it.
    /// </summary>
    private void TickScriptedCoast(long tick)
    {
        if (_scriptedCoastSpeed is not { } speed || tick < 2f / HullSim.Dt) return;
        foreach (var connection in _connections)
        {
            if (connection.State != ConnectionState.Active || !_hulls.IsAlive(connection.Hull)) continue;
            ref var state = ref _hulls.State(connection.Hull.Index);
            state.Velocity = HullRotation.Rotate(-Vector3.UnitZ, state.Orientation) * speed;
        }
        _scriptedCoastSpeed = null;
    }
}
