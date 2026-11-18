using Prospect.Game.World;

namespace Prospect.Game.Server;

public sealed partial class ServerHost
{
    private float? _scriptedFillBinsSeconds;
    private bool _scriptedFillBinsOther;

    /// <summary>
    /// The scripted capture's bins: once, at the configured moment, every hull a player flies has its ore bins filled
    /// the way a hopper fills them (<see cref="OreBins.Add"/>), so a capture can show a ship that cannot take
    /// the ore around it. The plain kind fills every bin whole with the ore of the chunks nearest the hull, which is
    /// the "bins full" a player sees. The other kind fills every bin half full of a different ore, which leaves room
    /// in kilograms that the chunks near it cannot have: the "no bin for this ore" a player sees. Nothing is sent that
    /// the ordinary bins message does not carry.
    /// </summary>
    private void TickScriptedFillBins(long tick)
    {
        if (_scriptedFillBinsSeconds is not { } at || tick < (long)MathF.Ceiling(at / HullSim.Dt)) return;
        _scriptedFillBinsSeconds = null;
        foreach (var connection in _connections)
        {
            if (connection.State != ConnectionState.Active || !_hulls.IsAlive(connection.Hull)) continue;
            var index = connection.Hull.Index;
            ref var info = ref _hulls.Info(index);
            var design = _content.Designs[info.Design];
            if (design.Bins.Length == 0 || design.Hoppers.Length == 0) continue;

            // The ore of the chunk nearest the hull: that is the ore the capture shows it failing to take.
            var ore = -1;
            var nearest = double.PositiveInfinity;
            foreach (ref readonly var chunk in _chunks.Items)
            {
                var distance = chunk.Position.RelativeTo(_hulls.State(index).Position).LengthSquared();
                if (!(distance < nearest)) continue;
                nearest = distance;
                ore = chunk.Resource;
            }

            var resources = _content.Galaxy.Resources.Length;
            if (ore < 0) ore = 0;
            // A delivery goes into the bins that hold its ore first, so one ore half filling every bin would fill the
            // first one up instead. The other kind gives each bin an ore of its own, which leaves every bin half full
            // of something the chunks near it are not.
            var grade = 0.5f;
            for (var bin = 0; bin < design.Bins.Length; bin++)
            {
                if (!_scriptedFillBinsOther)
                    _hulls.BlockStates.AddOre(info.BlockState, ore, design.Bins[bin].Capacity, grade);
                else if (resources > 1)
                    _hulls.BlockStates.AddOre(info.BlockState, (ore + 1 + bin) % resources, design.Bins[bin].Capacity * 0.5, grade);
            }

            _field.Refit(index);
        }
    }
}
