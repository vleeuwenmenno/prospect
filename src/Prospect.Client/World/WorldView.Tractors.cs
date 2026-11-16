using System.Numerics;
using Brutal.Numerics;
using Prospect.Client.Render;
using Prospect.Game.Client;
using Prospect.Game.World;

namespace Prospect.Client.World;

internal sealed partial class WorldView
{
    public TractorPreview TractorPreview { get; }
    public TractorLook TractorLook => PropulsionLook.Shared.Tractor!;

    /// <summary>Beam endpoints use the same rendered hull and interpolated container poses as their models.</summary>
    public bool TractorBeam(in FreeContainer container, out Double3 from, out Double3 to) =>
        TractorBeam(container, out from, out to, out _);

    private bool TractorBeam(in FreeContainer container, out Double3 from, out Double3 to, out FreeContainer pose)
    {
        from = to = default;
        pose = default;
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
            if (!TryGetContainer(container.Id, out pose)) return false;
            to = pose.Position;
            return true;
        }
        return false;
    }

    private void DrawTractorBeams(int frameIndex, SessionSnapshot snapshot, Double3 camera)
    {
        var output = _scene.Tractors(frameIndex);
        var count = 0;
        var cargo = snapshot.Content.Cargo;
        for (var i = 0; i < snapshot.FreeContainerCount && count < output.Length; i++)
        {
            ref readonly var item = ref snapshot.FreeContainers[i];
            if (!TractorBeam(item, out var from, out var to, out var pose)) continue;
            var fromRel = from.RelativeTo(camera);
            var toRel = to.RelativeTo(camera);
            var len = (toRel - fromRel).Length();
            if (len < 1e-4f) continue;

            var half = FreeContainers.Half(cargo, item.Size);
            var q = pose.Orientation;
            var seed = ((item.Id * 0x9E3779B9u) & 0xFFFFu) / 65536f;
            output[count++] = new TractorInstance
            {
                FromSeed = new float4(fromRel.X, fromRel.Y, fromRel.Z, seed),
                ToLength = new float4(toRel.X, toRel.Y, toRel.Z, len),
                ContainerQuat = new float4(q.X, q.Y, q.Z, q.W),
                ContainerHalf = new float4(half.X, half.Y, half.Z, half.Length()),
            };
        }
        _scene.QueueTractors(count, TractorLook.Constants);
    }
}
