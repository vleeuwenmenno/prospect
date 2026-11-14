namespace Prospect.Game.Server;

public sealed partial class ServerHost
{
    private long _scriptedFleetHurtTick = -1;
    private bool _scriptedFleetHurtBroken;

    /// <summary>
    /// The scripted capture's fleet damage: once, at the configured tick, the player's hulls other than the one flown
    /// are hurt so the target panel has something to show. Hurt means a shield half charged and a hull at 60 %; with
    /// <c>broken</c> it is a broken shield (as <c>HullSim.Damage</c> leaves it, a third of the way to its restart
    /// point) and a hull at 20 %. At most two hulls are touched, the rest are left alone.
    /// </summary>
    private void TickScriptedFleetHurt(long tick)
    {
        if (_scriptedFleetHurtTick < 0 || tick < _scriptedFleetHurtTick)
            return;
        _scriptedFleetHurtTick = -1;
        foreach (var connection in _connections)
        {
            if (connection.State != ConnectionState.Active)
                continue;
            var own = connection.Hull.Index;
            var owner = _hulls.Info(own).Owner;
            var hurt = 0;
            for (var i = 0; i < _hulls.Slots && hurt < 2; i++)
            {
                if (i == own || !_hulls.IsAliveAt(i) || _hulls.Info(i).Owner != owner)
                    continue;
                ref var state = ref _hulls.State(i);
                var flight = _hulls.Flight(i);
                if (!_scriptedFleetHurtBroken)
                {
                    state.Shield = flight.ShieldCapacity * 0.5f;
                    state.Hull = flight.HullPoints * 0.6f;
                }
                else
                {
                    state.Shield = flight.ShieldCapacity * flight.ShieldRestartFraction * 0.33f;
                    state.ShieldOffline = flight.ShieldCapacity > 0f;
                    state.ShieldChangeTicks = 0;
                    state.ShieldCooldown = 0f;
                    state.Hull = flight.HullPoints * 0.2f;
                }

                hurt++;
            }
        }
    }
}
