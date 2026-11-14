using Prospect.Game.World;

namespace Prospect.Game.Server;

public sealed partial class ServerHost
{
    private JumpEligibility JumpEligibility(int slot, Double3 target)
    {
        var owner = _hulls.Info(slot).Owner;
        var sector = Galaxy.SectorOf(target);
        var charted = false;
        for (var i = 0; i < _chartCount; i++)
            if (_charts[i].Owner == owner && _charts[i].Sector == sector) { charted = true; break; }
        return JumpRules.Evaluate(_hulls.State(slot).Position, target, _hulls.Flight(slot), _content.Exploration, charted);
    }

    private bool ValidateJump(Connection connection, int slot, Double3 target)
    {
        var reason = _hulls.State(slot).Jump is JumpPhase.Tunnel or JumpPhase.Arrive ? JumpRefusal.Busy : JumpEligibility(slot, target).Reason;
        connection.Navigation.Refusal = reason;
        connection.Navigation.ResultHull = _hulls.HandleAt(slot).Packed;
        return reason == JumpRefusal.None;
    }
}
