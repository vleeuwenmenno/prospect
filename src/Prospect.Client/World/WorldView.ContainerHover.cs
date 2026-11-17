using System.Numerics;
using Brutal.Numerics;
using Prospect.Game.Client;
using Prospect.Game.World;

namespace Prospect.Client.World;

internal sealed partial class WorldView
{
    /// <summary>What the pointer is on: a container in an own rack or loose in space. Read by the tooltip; nothing when none.</summary>
    public ContainerInfo HoverContainer { get; private set; }

    /// <summary>The id of the loose container under the pointer; zero when none is, or the one hovered is in a rack.</summary>
    public uint HoverContainerId { get; private set; }

    /// <summary>Where the tooltip hangs: the pointer's place in window pixels.</summary>
    public float2 HoverContainerAt { get; private set; }

    /// <summary>True while the pointer is shown (the interface mode, a held release key, the command view); set by the frame loop.</summary>
    public bool PointerShown { get; set; }

    /// <summary>Scripted runs only: <c>stowed</c> or <c>free</c> puts the pointer on the first such container, as a person's would be.</summary>
    public string? ScriptedHover { get; set; }

    private void UpdateContainerHover(SessionSnapshot snapshot, bool pointerOverUi)
    {
        HoverContainer = default;
        HoverContainerId = 0;
        if (Editor.Shown || (Mode != ViewMode.Pilot && Mode != ViewMode.Command) || snapshot.Galaxy is not { } galaxy) return;
        float2 pointer;
        if (ScriptedHover is { } script)
        {
            if (!ScriptedHoverPoint(snapshot, script == "stowed", out pointer)) return;
        }
        else
        {
            // Only a shown pointer hovers; one over a window belongs to the window.
            if (!PointerShown || pointerOverUi || PointerOverPanel) return;
            pointer = _devices.MousePosition;
        }
        var nx = pointer.X / _screen.X * 2 - 1;
        var ny = pointer.Y / _screen.Y * 2 - 1;
        var ray = Vector3.Normalize(_forward + _right * (nx * _tanHalfFov * _screen.X / _screen.Y) - _up * (ny * _tanHalfFov));
        var cargo = snapshot.Content.Cargo;
        var best = float.PositiveInfinity;
        var info = default(ContainerInfo);
        var id = 0u;
        foreach (var item in _freeVisuals.Items)
        {
            if (!_partModels.TryGetContainer(item.Size, out _)) continue;
            var pose = ContainerPose(item);
            var hit = TargetPicking.Container(ray, CameraPosition, pose, cargo, NearPlane);
            if (hit >= best) continue;
            best = hit;
            info = ContainerHover.Free(cargo, galaxy.Resources, item, snapshot.PlayerId, snapshot.FreeContainerTick);
            id = item.Id;
        }
        var visuals = _visuals.Hulls;
        for (var i = 0; i < Math.Min(snapshot.HullCount, visuals.Length); i++)
        {
            ref var hull = ref snapshot.Hulls[i];
            ref readonly var visual = ref visuals[i];
            if (hull.Cargo.Length == 0 || !visual.Visible || hull.Design >= _designs.Length) continue;
            var design = _designs[hull.Design];
            var held = hull.Cargo.Span;
            for (var k = 0; k < held.Length; k++)
            {
                ref readonly var container = ref held[k];
                if ((uint)container.Rack >= (uint)design.BlockCount || (uint)container.Size >= (uint)cargo.Sizes.Length) continue;
                var hit = TargetPicking.Container(ray, CameraPosition, StowedPose(design, visual, container, cargo), cargo, NearPlane);
                if (hit >= best) continue;
                best = hit;
                info = ContainerHover.Stowed(cargo, galaxy.Resources, container);
                id = 0;
            }
        }
        if (!info.Any) return;
        // A rock or another ship in front hides it, as it hides a free container for the middle click.
        var count = PickTargets(snapshot, pointer, true);
        for (var i = 0; i < count; i++) if (_pickHits[i].Distance < best) return;
        HoverContainer = info;
        HoverContainerId = id;
        HoverContainerAt = pointer;
    }

    private FreeContainer StowedPose(HullDesign design, in HullVisuals.HullVisual visual, in HoldContainer container, CargoCatalog cargo) =>
        ContainerHover.StowedPose(design, DrawnPosition(visual), visual.Orientation, visual.Stretch, container, cargo);

    private bool ScriptedHoverPoint(SessionSnapshot snapshot, bool stowed, out float2 screen)
    {
        screen = default;
        var cargo = snapshot.Content.Cargo;
        if (!stowed)
        {
            foreach (var item in _freeVisuals.Items)
                if (ProjectDisc(ContainerPose(item).Position, 0, out screen, out _, out _)) return true;
            return false;
        }
        var visuals = _visuals.Hulls;
        for (var i = 0; i < Math.Min(snapshot.HullCount, visuals.Length); i++)
        {
            ref var hull = ref snapshot.Hulls[i];
            if (hull.Cargo.Length == 0 || !visuals[i].Visible || hull.Design >= _designs.Length) continue;
            var container = hull.Cargo.Span[0];
            if ((uint)container.Rack >= (uint)_designs[hull.Design].BlockCount) continue;
            if (ProjectDisc(StowedPose(_designs[hull.Design], visuals[i], container, cargo).Position, 0, out screen, out _, out _)) return true;
        }
        return false;
    }
}
