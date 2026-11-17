using System.Numerics;
using Brutal.Numerics;
using Prospect.Client.Render;
using Prospect.Game.Client;
using Prospect.Game.World;

namespace Prospect.Client.World;

// Loose ore chunks a mining laser broke off a rock: small rough lumps, drawn from the set the server sends every six
// ticks and carried between sets by OreChunkVisuals. The lump is one worker-built mesh scaled to the chunk's size; its
// colour is the ore's mixed with bare rock by the chunk's grade. A chunk too small to cover half a pixel is not drawn.
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
        var items = _oreVisuals.Items;
        if (items.IsEmpty || snapshot.Galaxy is not { } galaxy)
            return;
        var rules = snapshot.Content.Mining;
        var tick = OreChunkVisuals.ShownTick(snapshot.Hulls.AsSpan(0, snapshot.HullCount), snapshot.OwnHull, snapshot.ServerTickPrecise, _frameAlpha);
        var aspect = _viewport.X / _viewport.Y;
        for (var i = 0; i < items.Length; i++)
        {
            ref readonly var item = ref items[i];
            var pose = _oreVisuals.Pose(i, tick);
            var position = pose.Position.RelativeTo(camera);
            var half = OreChunks.Half(rules, item.Mass);
            if (!Visible(position, half * 1.8f, aspect, out var pixels) || pixels < OreChunkMinPixels
                || !_partModels.TryGetOreChunk((int)item.Id, out var mesh))
                continue;
            var tint = OreChunkVisuals.Tint((uint)item.Resource < (uint)galaxy.Resources.Length ? galaxy.Resources[item.Resource].Color : Vector3.One, item.Grade);
            // Material 0 is rock: mottled, lit by the star only.
            if (_scene.AddTriangles(frameIndex, mesh, MeshTransform(position, pose.Orientation, new Vector3(half), tint, 0f, 1f)))
                DrawnOreChunks++;
        }
    }
}
