using System.Numerics;
using Brutal.Numerics;
using Prospect.Client.Render;
using Prospect.Game.Client;
using Prospect.Game.World;

namespace Prospect.Client.World;

internal sealed partial class WorldView
{
    private const int MaxTurretStates = 256;

    private struct TurretState
    {
        public uint HullId;
        public int Mount;
        public float Azimuth;
        public float Pitch;
        public Vector3 LensRel;
        public long LastFrame;
        public long LensFrame;
    }

    private readonly TurretState[] _turrets = new TurretState[MaxTurretStates];
    private long _turretFrame;
    private float _lastTurretTime;
    private float _turretDt;

    public TractorPreview TractorPreview { get; }
    public TractorLook TractorLook => PropulsionLook.Shared.Tractor!;

    private void BeginTractorTurrets(float time)
    {
        _turretFrame++;
        _turretDt = _lastTurretTime > 0f ? Math.Clamp(time - _lastTurretTime, 0f, 0.1f) : 0f;
        _lastTurretTime = time;
    }

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

    private static Quaternion FaceRotation(BlockFacing face)
    {
        var axis = Facings.Axis(face);
        var u = Vector3.Zero;
        u[(axis + 1) % 3] = 1f;
        var n = Facings.Vector(face);
        var v = Vector3.Cross(n, u);
        return Quaternion.CreateFromRotationMatrix(new Matrix4x4(
            u.X, u.Y, u.Z, 0f,
            v.X, v.Y, v.Z, 0f,
            n.X, n.Y, n.Z, 0f,
            0f, 0f, 0f, 1f));
    }

    private static float MountHalfWidth(Vector3 half, BlockFacing face)
    {
        var axis = Facings.Axis(face);
        return MathF.Min(half[(axis + 1) % 3], half[(axis + 2) % 3]);
    }

    private int AcquireTurretSlot(uint hullId, int mount)
    {
        var free = -1;
        var oldest = long.MaxValue;
        for (var i = 0; i < _turrets.Length; i++)
        {
            ref var s = ref _turrets[i];
            if (s.HullId == hullId && s.Mount == mount)
                return i;
            var age = s.HullId == 0 ? long.MinValue : s.LastFrame;
            if (s.LastFrame < _turretFrame && age < oldest)
            {
                oldest = age;
                free = i;
            }
        }
        if (free >= 0)
            _turrets[free] = new TurretState { HullId = hullId, Mount = mount, LastFrame = _turretFrame };
        return free;
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

    private static float StepAngle(float current, float target, float maxStep)
    {
        var diff = MathF.IEEERemainder(target - current, MathF.Tau);
        if (MathF.Abs(diff) <= maxStep)
            return target;
        return MathF.IEEERemainder(current + MathF.CopySign(maxStep, diff), MathF.Tau);
    }

    private static float StepPitch(float current, float target, float maxStep)
    {
        var diff = target - current;
        return MathF.Abs(diff) <= maxStep ? target : current + MathF.CopySign(maxStep, diff);
    }

    private void DrawTractorTurrets(int frameIndex, SessionSnapshot snapshot, in HullView hull, HullDesign design, Double3 camera, Vector3 hullRel, Quaternion orientation, float material, float stretch)
    {
        if (design.Tractors.Length == 0)
            return;
        var look = TractorLook;
        var alive = hull.Blocks.Span;
        var slewStep = look.TurretSlewRate * (MathF.PI / 180f) * _turretDt;
        var maxPitch = look.TurretMaxPitch * (MathF.PI / 180f);

        for (var t = 0; t < design.Tractors.Length; t++)
        {
            var m = design.Tractors[t];
            var part = design.Mounts[m];
            if (!BlockSet.Has(alive, part.Parent))
                continue;

            var half = design.MountHalfExtents[m];
            var small = MountHalfWidth(half, part.Face);
            if (!_partModels.TryGetTractorTurret(small, out var yoke, out var head))
                continue;

            var axis = Facings.Axis(part.Face);
            var nLocal = Facings.Vector(part.Face);
            var faceLocal = design.MountOffsets[m] - nLocal * half[axis];
            faceLocal.Z *= stretch;
            var faceRel = hullRel + Vector3.Transform(faceLocal, orientation);
            var faceRot = Quaternion.Concatenate(FaceRotation(part.Face), orientation);
            var collarRel = faceRel + Vector3.Transform(new Vector3(0f, 0f, PropulsionModels.TractorCollarOffset(small, look)), faceRot);
            var pivotRel = faceRel + Vector3.Transform(new Vector3(0f, 0f, PropulsionModels.TractorPivotOffset(small, look)), faceRot);

            var targetAz = 0f;
            var targetPitch = 0f;
            if (FindHeldContainer(snapshot, hull.Id, m, camera, out var targetRel))
            {
                var toTarget = Vector3.Transform(targetRel - pivotRel, Quaternion.Conjugate(faceRot));
                var horiz = MathF.Sqrt(toTarget.X * toTarget.X + toTarget.Y * toTarget.Y);
                if (horiz > 1e-4f)
                    targetAz = MathF.Atan2(-toTarget.X, toTarget.Y);
                targetPitch = Math.Clamp(MathF.Atan2(horiz, toTarget.Z), 0f, maxPitch);
            }

            var slot = AcquireTurretSlot(hull.Id, m);
            var az = targetAz;
            var pitch = targetPitch;
            if (slot >= 0)
            {
                ref var state = ref _turrets[slot];
                if (state.LastFrame == _turretFrame - 1 && _turretDt > 0f)
                {
                    az = StepAngle(state.Azimuth, targetAz, slewStep);
                    pitch = StepPitch(state.Pitch, targetPitch, slewStep);
                }
                state.Azimuth = az;
                state.Pitch = pitch;
                state.LastFrame = _turretFrame;
            }

            var qAz = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, az);
            var qPitch = Quaternion.CreateFromAxisAngle(Vector3.UnitX, -pitch);
            var yokeRot = Quaternion.Concatenate(qAz, faceRot);
            var headRot = Quaternion.Concatenate(qPitch, yokeRot);

            _scene.AddTriangles(frameIndex, yoke, MeshTransform(collarRel, yokeRot, Vector3.One, design.Color, material, 1f));
            _scene.AddTriangles(frameIndex, head, MeshTransform(pivotRel, headRot, Vector3.One, design.Color, material, 1f));

            if (slot >= 0)
            {
                ref var state = ref _turrets[slot];
                state.LensRel = pivotRel + Vector3.Transform(new Vector3(0f, 0f, PropulsionModels.TractorLensOffset(small, look)), headRot);
                state.LensFrame = _turretFrame;
            }
        }
    }

    private void DrawIdleTractorTurret(int frameIndex, Vector3 mountCenter, BlockFacing face, Vector3 half, Quaternion orientation, Vector3 color, float material)
    {
        var small = MountHalfWidth(half, face);
        if (!_partModels.TryGetTractorTurret(small, out var yoke, out var head))
            return;
        var axis = Facings.Axis(face);
        var nLocal = Facings.Vector(face);
        var faceRot = Quaternion.Concatenate(FaceRotation(face), orientation);
        var facePos = mountCenter - Vector3.Transform(nLocal * half[axis], orientation);
        var look = TractorLook;
        var collarPos = facePos + Vector3.Transform(new Vector3(0f, 0f, PropulsionModels.TractorCollarOffset(small, look)), faceRot);
        var pivotPos = facePos + Vector3.Transform(new Vector3(0f, 0f, PropulsionModels.TractorPivotOffset(small, look)), faceRot);
        _scene.AddTriangles(frameIndex, yoke, MeshTransform(collarPos, faceRot, Vector3.One, color, material, 1f));
        _scene.AddTriangles(frameIndex, head, MeshTransform(pivotPos, faceRot, Vector3.One, color, material, 1f));
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
            for (var s = 0; s < _turrets.Length; s++)
            {
                ref readonly var state = ref _turrets[s];
                if (state.HullId == item.TractorHull && state.Mount == item.TractorMount && state.LensFrame == _turretFrame)
                {
                    fromRel = state.LensRel;
                    break;
                }
            }
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
            var fromRel = from.RelativeTo(camera);
            for (var s = 0; s < _turrets.Length; s++)
            {
                ref readonly var state = ref _turrets[s];
                if (state.HullId == item.TractorHull && state.Mount == item.TractorMount && state.LensFrame == _turretFrame)
                {
                    fromRel = state.LensRel;
                    break;
                }
            }
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
