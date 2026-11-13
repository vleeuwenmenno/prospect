using System.Numerics;
using Prospect.Game.World;

namespace Prospect.Game.Server;

/// <summary>Opt-in diagnostic population. Retained dents are seeded on the server; sparks require collisions.</summary>
public sealed record VisualBenchmarkScene(int Hulls, string Activity, string View, bool Large = false)
{
    /// <summary>Builds one connected 910-box plate through the normal design admission path before threads start.</summary>
    public GameContent PrepareContent(GameContent content)
    {
        if (!Large) return content;
        var boxes = new DesignBox[910];
        for (var i = 0; i < boxes.Length; i++)
            boxes[i] = new DesignBox(new GridSize(i % 35, i / 35, 0), new GridSize(1, 1, 1), content.Blocks.Get(i == 0 ? "core" : "hull"));
        var design = HullDesign.Of(new BoxDesign("Benchmark plate", 1, boxes, content.Blocks.Limits, "benchmark"), content.Rules);
        var bytes = DesignBytes.Encode(design);
        design = DesignBytes.Decode(bytes, System.Security.Cryptography.SHA256.HashData(bytes), content);
        return content.WithDesign(design);
    }

    /// <summary>Rejects unbounded or unknown scenarios before starting threads.</summary>
    public void Validate()
    {
        if (Hulls is not (30 or 50) || Activity is not ("clean" or "dents" or "impacts" or "repair" or "jump") || View is not ("near" or "far" or "behind"))
            throw new InvalidDataException("Visual scene: 30|50,clean|dents|impacts|repair|jump,near|far|behind");
    }
}

public sealed partial class ServerHost
{
    private readonly VisualBenchmarkScene? _visualBenchmark;
    private HullHandle[]? _benchmarkHulls;

    private void TickVisualBenchmark(long tick)
    {
        if (_visualBenchmark is not { } scene || _playerCount == 0) return;
        if (_benchmarkHulls is null)
        {
            scene.Validate();
            if (scene.Activity is "repair" or "jump")
                foreach (var connection in _connections)
                    if (connection.State == ConnectionState.Active)
                    {
                        var own = connection.Hull;
                        var ownDesign = _content.Designs[_content.PodDesign];
                        for (var b = 0; b < ownDesign.BlockCount && _hulls.Info(own.Index).Visual.Count < 16; b++)
                            if ((ownDesign.ExteriorFaces[b] & (1 << 4)) != 0)
                                _hulls.Info(own.Index).Visual.Hit(ownDesign, b, ownDesign.BlockHitPoints(b) * 0.4f,
                                    ownDesign.BlockOffsets[b] + Vector3.UnitZ * ownDesign.BlockHalfExtents[b].Z, Vector3.UnitZ, tick);
                    }
            _benchmarkHulls = new HullHandle[scene.Hulls - 1];
            for (var i = 0; i < _benchmarkHulls.Length; i++)
            {
                var index = (byte)(scene.Large ? _content.Designs.Length - 1 : _content.StartingFleet[i % _content.StartingFleet.Length]);
                var design = _content.Designs[index];
                var position = SpawnPoint + new Double3((i % 7 - 3) * (scene.Large ? 40 : 16), (i / 7 - 2) * (scene.Large ? 32 : 14), scene.View == "far" ? -1200 : scene.View == "behind" ? 200 : scene.Large ? -260 : -100);
                var h = CreateHull(0, index, position);
                _benchmarkHulls[i] = h;
                if (scene.Activity is "clean" or "repair" or "jump") continue;
                for (var b = 0; b < design.BlockCount && _hulls.Info(h.Index).Visual.Count < 16; b++)
                {
                    if ((design.ExteriorFaces[b] & (1 << 4)) == 0) continue;
                    _hulls.Info(h.Index).Visual.Hit(design, b, design.BlockHitPoints(b) * 0.4f,
                        design.BlockOffsets[b] + Vector3.UnitZ * design.BlockHalfExtents[b].Z, Vector3.UnitZ, tick);
                }
            }
        }
        if (scene.Activity == "impacts" && tick % 60 == 0)
            for (var i = 0; i < 2; i++)
            {
                if (_hulls.IsAlive(_benchmarkHulls[i])) _hulls.Destroy(_benchmarkHulls[i]);
                var index = (byte)(scene.Large ? _content.Designs.Length - 1 : _content.StartingFleet[i % _content.StartingFleet.Length]);
                _benchmarkHulls[i] = CreateHull(0, index, SpawnPoint + new Double3(i * 30 - 15, 0, -35));
            }
        // Re-stage two existing bodies, then let the ordinary collision solver decide the contact and damage.
        if (scene.Activity != "impacts" || tick % 60 != 30) return;
        for (var i = 0; i < 2; i++)
        {
            var h = _benchmarkHulls[i];
            if (!_hulls.IsAlive(h)) continue;
            ref var state = ref _hulls.State(h.Index);
            state.Position = SpawnPoint + new Double3(i * 3, 0, -35);
            state.Velocity = new Vector3(i == 0 ? 15 : -15, 0, 0);
            state.Shield = 0;
            state.ShieldOffline = true;
        }
    }
}
