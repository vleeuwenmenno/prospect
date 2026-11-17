using System.Numerics;
using Brutal.Numerics;
using Prospect.Game.Client;
using Prospect.Game.World;

namespace Prospect.Client.World;

// The marks on loose ore chunks (diamonds, edge arrows, the hover tooltip and the DRONES chip's count) all read one
// per-frame set of markers, worked out here from the very pose the chunk mesh is drawn at, so nothing is placed twice.
internal sealed partial class WorldView
{
    private readonly OreMarker[] _oreMarkers = new OreMarker[OreChunks.Capacity];
    private int _oreMarkerCount;

    /// <summary>Every chunk the client knows of this frame, placed against the camera. Empty in the editor.</summary>
    public ReadOnlySpan<OreMarker> OreMarkers => _oreMarkers.AsSpan(0, _oreMarkerCount);

    /// <summary>Chunks within <see cref="OreChunkLook.NearMetres"/> of the flown hull.</summary>
    public int OreChunksNear { get; private set; }

    /// <summary>The chunk under a shown pointer; nothing when none is.</summary>
    public OreChunkInfo HoverOreChunk { get; private set; }

    /// <summary>Where the chunk tooltip hangs: the pointer's place in window pixels.</summary>
    public float2 HoverOreChunkAt { get; private set; }

    /// <summary>Scripted runs only (<c>--hover-chunk</c>): puts the pointer on the first chunk in view, as a person's would be.</summary>
    public bool ScriptedHoverChunk { get; set; }

    private void UpdateOreMarkers(SessionSnapshot snapshot, bool pointerOverUi)
    {
        _oreMarkerCount = 0;
        OreChunksNear = 0;
        HoverOreChunk = default;
        var items = _oreVisuals.Items;
        if (items.IsEmpty || snapshot.Galaxy is null || Editor.Shown) return;
        var look = OreChunkLook.Current;
        var rules = snapshot.Content.Mining;
        var tick = OreChunkVisuals.ShownTick(snapshot.Hulls.AsSpan(0, snapshot.HullCount), snapshot.OwnHull, snapshot.ServerTickPrecise, _frameAlpha);
        for (var i = 0; i < items.Length; i++)
        {
            ref readonly var item = ref items[i];
            var pose = _oreVisuals.Pose(i, tick);
            ref var marker = ref _oreMarkers[_oreMarkerCount++];
            marker.Id = item.Id;
            marker.Resource = item.Resource;
            marker.Grade = item.Grade;
            marker.Mass = item.Mass;
            marker.View = ToView(pose.Position);
            marker.OwnDistance = (float)pose.Position.RelativeTo(OwnDrawnPosition).Length();
            OreChunkMarks.Place(ref marker, OreChunks.Half(rules, item.Mass), _tanHalfFov, _screen, NearPlane);
        }

        OreChunksNear = OreChunkMarks.CountNear(OreMarkers, look);
        UpdateOreHover(snapshot, pointerOverUi, look);
    }

    private void UpdateOreHover(SessionSnapshot snapshot, bool pointerOverUi, OreChunkLook look)
    {
        // A container under the pointer has the tooltip; a chunk is picked only where the same rules let a container be.
        if ((Mode != ViewMode.Pilot && Mode != ViewMode.Command) || HoverContainer.Any) return;
        float2 pointer;
        if (ScriptedHoverChunk)
        {
            var found = false;
            pointer = default;
            foreach (var marker in OreMarkers)
                if (marker.OnScreen)
                {
                    pointer = marker.Screen;
                    found = true;
                    break;
                }

            if (!found) return;
        }
        else
        {
            if (!PointerShown || pointerOverUi || PointerOverPanel) return;
            pointer = _devices.MousePosition;
        }

        var at = OreChunkMarks.Pick(OreMarkers, pointer, look, 1f);
        if (at < 0) return;
        ref readonly var hit = ref _oreMarkers[at];
        // A rock or another ship in front hides it, as it hides a free container.
        var count = PickTargets(snapshot, pointer, true);
        for (var i = 0; i < count; i++)
            if (_pickHits[i].Distance < hit.Distance) return;
        HoverOreChunk = new OreChunkInfo(hit.Id, hit.Resource, hit.Grade, hit.Mass, hit.OwnDistance);
        HoverOreChunkAt = pointer;
    }
}
