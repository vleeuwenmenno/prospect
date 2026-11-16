using System.Numerics;
using Prospect.Client.Render;
using Prospect.Game.World;

namespace Prospect.Client.World;

internal sealed partial class WorldView
{
    public TractorPreview TractorPreview { get; }
    public TractorLook TractorLook => PropulsionLook.Shared.Tractor!;

    /// <summary>Beam endpoints use the same rendered hull and interpolated container poses as their models.</summary>
    public bool TractorBeam(in FreeContainer container, out Double3 from, out Double3 to)
    {
        from = to = default;
        if (!container.TractorPowered || container.TractorHull == 0 || _snapshot is null) return false;
        for (var i = 0; i < _snapshot.HullCount && i < _visuals.Hulls.Length; i++)
        {
            ref var hull = ref _snapshot.Hulls[i];
            if (hull.Id != container.TractorHull || hull.Design >= InstalledDesigns || hull.Design >= _designs.Length) continue;
            var design = _designs[hull.Design];
            if ((uint)container.TractorMount >= (uint)design.Mounts.Length) return false;
            var mount = design.Mounts[container.TractorMount];
            if (mount.Type.Tractor is null || !BlockSet.Has(hull.Blocks.Span, mount.Parent)) return false;
            ref readonly var visual = ref _visuals.Hulls[i];
            var offset = design.MountOffsets[container.TractorMount]
                + Facings.Vector(mount.Face) * design.MountHalfExtents[container.TractorMount];
            from = DrawnPosition(visual) + HullRotation.Rotate(offset, visual.Orientation);
            if (!TryGetContainer(container.Id, out var pose)) return false;
            to = pose.Position;
            return true;
        }
        return false;
    }
}
