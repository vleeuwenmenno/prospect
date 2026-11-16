namespace Prospect.Game.Server;

public sealed partial class ServerHost
{
    private long _scriptedFleetHurtTick = -1;
    private bool _scriptedFleetHurtBroken;
    private bool _scriptedFleetHurtTank, _scriptedFleetHurtCargo;
    private bool _scriptedCutHull;

    /// <summary>
    /// The scripted capture's fleet damage: once, at the configured tick, the player's hulls other than the one flown
    /// are hurt so the target panel has something to show. Hurt means a shield half charged and a hull at 60 %; with
    /// <c>broken</c> it is a broken shield (as <c>HullSim.Damage</c> leaves it, a third of the way to its restart
    /// point) and a hull at 20 %. At most two hulls are touched, the rest are left alone. With <c>tank</c> the hull
    /// flown loses instead its last fuel tank, in design order, as a hit that destroys the block would take it, so
    /// the fuel callout has a lost tank to show.
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
            if (_scriptedCutHull)
            {
                var info = _hulls.Info(own);
                var design = _content.Designs[info.Design];
                var plane = (int)Math.Floor(design.CenterOfMass.Z);
                for (var b = 0; b < design.BlockCount; b++)
                {
                    var box = design.Boxes[b];
                    if (box.At.Z <= plane && box.At.Z + box.Size.Z > plane)
                        _hulls.BlockStates.Lose(info.BlockState, b);
                }
                _hulls.State(own).AngularVelocity = new System.Numerics.Vector3(0, 0.3f, 0);
                continue;
            }
            if (_scriptedFleetHurtCargo)
            {
                var info = _hulls.Info(own);
                var design = _content.Designs[info.Design];
                for (var b = 0; b < design.BlockCount; b++)
                {
                    if (design.Types[b].Cargo <= 0 || !_hulls.BlockStates.Hit(info.BlockState, b, float.MaxValue)) continue;
                    _hulls.Info(own).DamageTick = _tick;
                    _hulls.State(own).Hull = (float)_hulls.BlockStates.HitTotal(info.BlockState);
                    _field.Refit(own);
                    break;
                }
                continue;
            }
            if (_scriptedFleetHurtTank)
            {
                LoseLastTank(own);
                continue;
            }

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

    private void LoseLastTank(int index)
    {
        ref var info = ref _hulls.Info(index);
        var tanks = _content.Designs[info.Design].Tanks;
        if (tanks.Length == 0 || !_hulls.BlockStates.Hit(info.BlockState, tanks[^1].Block, float.MaxValue))
            return;
        info.DamageTick = _tick;
        _hulls.State(index).Hull = (float)_hulls.BlockStates.HitTotal(info.BlockState);
        _field.Refit(index);
    }
}
