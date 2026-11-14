namespace Prospect.Game.Server;

public sealed partial class ServerHost
{
    private long _scriptedShieldBreakTick = -1;

    /// <summary>
    /// The scripted capture's shield break: once, at the configured tick, every player's hull loses its shield the way
    /// <c>HullSim.Damage</c> takes one, so the capture shows the real restart with the real rules.
    /// </summary>
    private void TickScriptedShieldBreak(long tick)
    {
        if (_scriptedShieldBreakTick < 0 || tick < _scriptedShieldBreakTick)
            return;
        _scriptedShieldBreakTick = -1;
        foreach (var connection in _connections)
        {
            if (connection.State != ConnectionState.Active)
                continue;
            var index = connection.Hull.Index;
            ref var state = ref _hulls.State(index);
            if (_hulls.Flight(index).ShieldCapacity <= 0f)
                continue;
            state.Shield = 0f;
            state.ShieldOffline = true;
            state.ShieldChangeTicks = 0;
            state.ShieldCooldown = _hulls.Flight(index).ShieldDelay;
        }
    }
}
