using System.Numerics;
using Prospect.Game.World;

namespace Prospect.Game.Server;

public sealed partial class ServerHost
{
    private int _scriptedExhaustShield;
    private int _scriptedEscorts;

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
            var near = _content.Designs[_hulls.Info(own).Design].Stats.Size;
            for (var i = 0; i < _hulls.Slots; i++)
            {
                if (i == own || !_hulls.IsAliveAt(i) || _hulls.Info(i).Owner != connection.PlayerId) continue;
                // Box beside box with a short gap: behind the source for its engines, left of it for its right thrust.
                var far = _content.Designs[_hulls.Info(i).Design].Stats.Size;
                ref var target = ref _hulls.State(i);
                target.Position = source.Position + (_scriptedExhaustShield == 2
                    ? new Vector3(-(near.X + far.X) * 0.5f - 1f, 0, 0)
                    : new Vector3(0, 0, (near.Z + far.Z) * 0.5f + 0.8f));
                target.Velocity = Vector3.Zero;
                target.Orientation = Quaternion.Identity;
                target.AngularVelocity = Vector3.Zero;
                break;
            }
        }
    }
}
