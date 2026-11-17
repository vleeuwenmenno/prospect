using System.Numerics;
using Prospect.Engine.Diagnostics;
using Prospect.Game.World;

namespace Prospect.Game.Server;

public sealed partial class ServerHost
{
    // How far from the spawn sector the fixture looks for a cold cloud, in sectors on each axis.
    private const int ScriptedColdCloudReach = 4;

    private bool _scriptedColdCloud;

    /// <summary>
    /// Capture only (<c>--cold-cloud</c>): two and a half seconds in, every player's hull is set at rest in the
    /// middle of the cold gas cloud anchored nearest the spawn sector, once. It only moves the hull: what the cold
    /// does to heat is the ordinary rule. Without a cold cloud within reach it is logged and nothing moves.
    /// </summary>
    private void TickScriptedColdCloud(long tick)
    {
        if (!_scriptedColdCloud || tick < 2.5f / HullSim.Dt) return;
        _scriptedColdCloud = false;
        if (Galaxy.GasClouds is not { } gas)
        {
            Logs.Sim.Warning("The scripted cold cloud needs a galaxy with gas clouds.");
            return;
        }

        Span<GasCloudPlacement> clouds = stackalloc GasCloudPlacement[64];
        var count = GasCloudQuery.Collect(Galaxy, _spawnSector, ScriptedColdCloudReach, clouds);
        for (var i = 0; i < count; i++)
        {
            if (clouds[i].Cloud.Kind != GasCloudKind.Cold) continue;
            var centre = GasCloudQuery.CenterOf(Galaxy, clouds[i]);
            foreach (var connection in _connections)
            {
                if (connection.State != ConnectionState.Active || !_hulls.IsAlive(connection.Hull)) continue;
                ref var state = ref _hulls.State(connection.Hull.Index);
                state.Position = centre;
                state.Velocity = Vector3.Zero;
                state.AngularVelocity = Vector3.Zero;
            }

            return;
        }

        Logs.Sim.Warning($"The scripted cold cloud found no cold cloud within {ScriptedColdCloudReach} sectors of the spawn sector.");
    }
}
