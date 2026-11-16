using System.Numerics;
using Prospect.Game.World;

namespace Prospect.Game.Server;

public sealed partial class ServerHost
{
    private int _scriptedExhaustShield;

    // Hold two real hulls beside one another for repeatable exhaust captures. Thrust and fuel still use normal flight.
    private void TickScriptedExhaustShield()
    {
        if (_scriptedExhaustShield == 0) return;
        foreach (var connection in _connections)
        {
            if (connection.State != ConnectionState.Active) continue;
            var own = connection.Hull.Index;
            ref var source = ref _hulls.State(own);
            source.Position = Galaxy.CenterOf(_spawnSector);
            source.Velocity = Vector3.Zero;
            source.Orientation = Quaternion.Identity;
            source.AngularVelocity = Vector3.Zero;
            for (var i = 0; i < _hulls.Slots; i++)
            {
                if (i == own || !_hulls.IsAliveAt(i) || _hulls.Info(i).Owner != connection.PlayerId) continue;
                ref var target = ref _hulls.State(i);
                target.Position = source.Position + (_scriptedExhaustShield == 2 ? new Vector3(-4.5f, 0, 0) : new Vector3(0, 0, 9));
                target.Velocity = Vector3.Zero;
                target.Orientation = Quaternion.Identity;
                target.AngularVelocity = Vector3.Zero;
                break;
            }
        }
    }
}
