using System.Numerics;
using Brutal.Numerics;
using Prospect.Client.Render;
using Prospect.Game.Client;
using Prospect.Game.World;

namespace Prospect.Client.World;

internal sealed partial class WorldView
{
    /// <summary>How fast a turret's bars brighten when its beam takes hold and dim when it lets go, in shares a second.</summary>
    private const float TurretGlowRate = 4f;

    public TractorPreview TractorPreview { get; }
    public TractorLook TractorLook => PropulsionLook.Shared.Tractor!;

    /// <summary>Beam endpoints use the same rendered hull and interpolated container poses as their models.</summary>
    public bool TractorBeam(in FreeContainer container, out Double3 from, out Double3 to) =>
        TractorBeam(container, out from, out to, out _);

    private bool TractorBeam(in FreeContainer container, out Double3 from, out Double3 to, out FreeContainer pose)
    {
        from = to = default;
        pose = default;
        if (!container.TractorPowered || !TractorEmitter(container.TractorHull, container.TractorMount, out from)
            || !TryGetContainer(container.Id, out pose)) return false;
        to = pose.Position;
        return true;
    }

    /// <summary>Where the beam of a drawn hull's tractor mount leaves it, on the hull's rendered pose. False for a hull or mount not drawn.</summary>
    private bool TractorEmitter(uint hullId, int tractor, out Double3 from)
    {
        from = default;
        if (hullId == 0 || _snapshot is null) return false;
        for (var i = 0; i < _snapshot.HullCount && i < _visuals.Hulls.Length; i++)
        {
            ref var hull = ref _snapshot.Hulls[i];
            if (hull.Id != hullId || hull.Design >= InstalledDesigns || hull.Design >= _designs.Length) continue;
            var design = _designs[hull.Design];
            if ((uint)tractor >= (uint)design.Mounts.Length) return false;
            var mount = design.Mounts[tractor];
            if (mount.Type.Tractor is null || !BlockSet.Has(hull.Blocks.Span, mount.Parent)) return false;
            ref readonly var visual = ref _visuals.Hulls[i];
            var offset = design.MountOffsets[tractor] + Facings.Vector(mount.Face) * design.MountHalfExtents[tractor];
            from = DrawnPosition(visual) + HullRotation.Rotate(offset, visual.Orientation);
            return true;
        }
        return false;
    }

    private bool FindHeldContainer(SessionSnapshot snapshot, uint hullId, int mount, Double3 camera, out Vector3 targetRel)
    {
        targetRel = default;
        for (var i = 0; i < snapshot.FreeContainerCount; i++)
        {
            ref readonly var item = ref snapshot.FreeContainers[i];
            if (item.TractorPowered && item.TractorHull == hullId && item.TractorMount == mount && TryGetContainer(item.Id, out var pose))
            {
                targetRel = pose.Position.RelativeTo(camera);
                return true;
            }
        }

        // An ore chunk the beam holds, where it is drawn.
        var chunks = _oreVisuals.Items;
        var tick = OreChunkVisuals.ShownTick(snapshot.Hulls.AsSpan(0, snapshot.HullCount), snapshot.OwnHull, snapshot.ServerTickPrecise, _frameAlpha);
        for (var i = 0; i < chunks.Length; i++)
        {
            ref readonly var item = ref chunks[i];
            if (!item.TractorPowered || item.TractorHull != hullId || item.TractorMount != mount) continue;
            targetRel = _oreVisuals.Pose(i, tick).Position.RelativeTo(camera);
            return true;
        }
        return false;
    }

    private void DrawTractorTurrets(int frameIndex, SessionSnapshot snapshot, in HullView hull, HullDesign design, Double3 camera, Vector3 hullRel, Quaternion orientation, float material, float stretch)
    {
        if (design.Tractors.Length == 0)
            return;
        var look = TractorLook;
        var frame = look.Turret;
        var alive = hull.Blocks.Span;
        var slewStep = look.TurretSlewRate * (MathF.PI / 180f) * _turretDt;
        var maxPitch = look.TurretMaxPitch * (MathF.PI / 180f);

        for (var t = 0; t < design.Tractors.Length; t++)
        {
            var m = design.Tractors[t];
            var part = design.Mounts[m];
            if (!BlockSet.Has(alive, part.Parent))
                continue;

            var small = MountHalfWidth(design.MountHalfExtents[m], part.Face);
            if (!_partModels.TryGetTurret(TurretKind.Tractor, small, out var yoke, out var head, out var bars))
                continue;
            var seat = SeatTurret(frame, small, MountFace(design, m, hullRel, orientation, stretch), part.Face, orientation);

            TurretRest(part.Face, look.TurretRestPitch, out var az, out var pitch);
            var holding = FindHeldContainer(snapshot, hull.Id, m, camera, out var targetRel);
            if (holding)
            {
                var toTarget = Vector3.Transform(targetRel - seat.Pivot, Quaternion.Conjugate(seat.Face));
                var horiz = MathF.Sqrt(toTarget.X * toTarget.X + toTarget.Y * toTarget.Y);
                if (horiz > 1e-4f)
                    az = MathF.Atan2(-toTarget.X, toTarget.Y);
                pitch = Math.Clamp(MathF.Atan2(horiz, toTarget.Z), 0f, maxPitch);
            }

            var slot = AcquireTurretSlot(hull.Id, m, out var live);
            var glow = holding ? 1f : 0f;
            SlewTurret(slot, live, slewStep, TurretGlowRate * _turretDt, ref az, ref pitch, ref glow);

            // The bars are light: idle they glow low, and a beam at work brightens and pulses them.
            var pulse = 1f + look.TurretPulseDepth * MathF.Sin(_lastTurretTime * look.TurretPulseRate * MathF.Tau);
            var level = look.TurretIdleGlow + (look.TurretActiveGlow * pulse - look.TurretIdleGlow) * glow;
            var headRot = DrawTurret(frameIndex, seat, az, pitch, yoke, head, bars, design.Color, material, look.BarLight(level));

            if (slot >= 0)
            {
                ref var state = ref _turrets[slot];
                state.LensRel = seat.Pivot + Vector3.Transform(new Vector3(0f, 0f, PropulsionModels.TractorLensOffset(small, look)), headRot);
                state.LensFrame = _turretFrame;
            }
        }
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
            var fromRel = TurretLens(item.TractorHull, item.TractorMount, out var lensRel) ? lensRel : from.RelativeTo(camera);
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

        // Ore chunks a beam holds: the same beam, ending on the chunk where it is drawn.
        var chunks = _oreVisuals.Items;
        var tick = OreChunkVisuals.ShownTick(snapshot.Hulls.AsSpan(0, snapshot.HullCount), snapshot.OwnHull, snapshot.ServerTickPrecise, _frameAlpha);
        for (var i = 0; i < chunks.Length && count < output.Length; i++)
        {
            ref readonly var item = ref chunks[i];
            if (!item.TractorPowered || !TractorEmitter(item.TractorHull, item.TractorMount, out var from)) continue;
            var fromRel = TurretLens(item.TractorHull, item.TractorMount, out var lensRel) ? lensRel : from.RelativeTo(camera);
            var pose = _oreVisuals.Pose(i, tick);
            var toRel = pose.Position.RelativeTo(camera);
            var len = (toRel - fromRel).Length();
            if (len < 1e-4f) continue;
            var half = OreChunks.Half(snapshot.Content.Mining, item.Mass);
            var q = pose.Orientation;
            var seed = ((item.Id * 0x85EBCA6Bu) & 0xFFFFu) / 65536f;
            output[count++] = new TractorInstance
            {
                FromSeed = new float4(fromRel.X, fromRel.Y, fromRel.Z, seed),
                ToLength = new float4(toRel.X, toRel.Y, toRel.Z, len),
                ContainerQuat = new float4(q.X, q.Y, q.Z, q.W),
                ContainerHalf = new float4(half, half, half, half * 1.7320508f),
            };
        }
        _scene.QueueTractors(count, TractorLook.Constants);
    }
}
