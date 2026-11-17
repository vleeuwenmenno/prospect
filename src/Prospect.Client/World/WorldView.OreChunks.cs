using System.Numerics;
using Brutal.Numerics;
using Prospect.Client.Render;
using Prospect.Game.Client;
using Prospect.Game.World;

namespace Prospect.Client.World;

// Loose ore chunks a mining laser broke off a rock: small rough lumps, drawn from the set the server sends every six
// ticks and carried between sets by OreChunkVisuals, tumbling at the spin the server sends. The lump is one worker-built
// mesh scaled to the chunk's size (OreChunkModels: a broken-off rock with crystals, more of them the richer the chunk);
// mesh.frag shades the veins and the glint from the ore's colour and the grade carried in the instance's material. A
// chunk too small to cover half a pixel is not drawn.
internal sealed partial class WorldView
{
    /// <summary>A chunk smaller on screen than this, in pixels of radius, is left out.</summary>
    private const float OreChunkMinPixels = 0.5f;

    private readonly OreChunkVisuals _oreVisuals = new();

    /// <summary>How many chunks are in the set last received.</summary>
    public int OreChunkCount => _oreVisuals.Items.Length;

    /// <summary>Chunks written for the scene this frame.</summary>
    public int DrawnOreChunks { get; private set; }

    private void UpdateOreChunks(SessionSnapshot snapshot) =>
        _oreVisuals.Update(snapshot.OreChunks.AsSpan(0, snapshot.OreChunkCount), snapshot.OreChunkTick);

    private void DrawOreChunks(int frameIndex, SessionSnapshot snapshot, Double3 camera)
    {
        DrawnOreChunks = 0;
        DrawOreSheet(frameIndex, snapshot);
        var items = _oreVisuals.Items;
        if (items.IsEmpty || snapshot.Galaxy is not { } galaxy)
            return;
        var rules = snapshot.Content.Mining;
        var tick = OreChunkVisuals.ShownTick(snapshot.Hulls.AsSpan(0, snapshot.HullCount), snapshot.OwnHull, snapshot.ServerTickPrecise, _frameAlpha);
        var aspect = _viewport.X / _viewport.Y;
        var look = OreChunkModelLook.Current;
        var figures = new float4(look.VeinScale, look.VeinCoverMin, look.VeinCoverMax, look.Glint);
        for (var i = 0; i < items.Length; i++)
        {
            ref readonly var item = ref items[i];
            var pose = _oreVisuals.Pose(i, tick);
            var position = pose.Position.RelativeTo(camera);
            var half = OreChunks.Half(rules, item.Mass);
            if (!Visible(position, half * 1.8f, aspect, out var pixels) || pixels < OreChunkMinPixels)
                continue;
            var known = (uint)item.Resource < (uint)galaxy.Resources.Length;
            if (!_partModels.TryGetOreChunk((int)item.Id, known ? look.Band(item.Grade) : 0, out var mesh))
                continue;
            var instance = MeshTransform(position, pose.Orientation, new Vector3(half), known ? galaxy.Resources[item.Resource].Color : new Vector3(0.5f),
                OreChunkVisuals.Material(known ? item.Grade : 0f), 1f);
            instance.RockMarks = figures;
            if (_scene.AddTriangles(frameIndex, mesh, instance))
                DrawnOreChunks++;
        }
    }

    /// <summary>Capture only (<c>--ore-sheet</c>): chunks of every ore at three grades, in rows before the camera, one tumbling.</summary>
    public bool OreSheet { get; set; }

    private void DrawOreSheet(int frameIndex, SessionSnapshot snapshot)
    {
        if (!OreSheet || snapshot.Galaxy is not { } galaxy)
            return;
        var look = OreChunkModelLook.Current;
        var figures = new float4(look.VeinScale, look.VeinCoverMin, look.VeinCoverMax, look.Glint);
        ReadOnlySpan<float> grades = [0.1f, 0.5f, 0.95f];
        var columns = galaxy.Resources.Length;
        var spacing = 1.5f;
        for (var row = 0; row < grades.Length; row++)
            for (var column = 0; column < columns; column++)
            {
                var id = row * columns + column;
                if (!_partModels.TryGetOreChunk(id, look.Band(grades[row]), out var mesh))
                    continue;
                var orientation = Quaternion.CreateFromYawPitchRoll(0.7f + id * 1.3f, 0.4f + id * 0.7f, id * 0.9f);
                var position = _forward * 6.5f + _right * ((column - (columns - 1) * 0.5f) * spacing) + _up * ((1 - row) * spacing);
                var instance = MeshTransform(position, orientation, new Vector3(0.6f), galaxy.Resources[column].Color, OreChunkVisuals.Material(grades[row]), 1f);
                instance.RockMarks = figures;
                _scene.AddTriangles(frameIndex, mesh, instance);
            }
    }
}
