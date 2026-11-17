using System.Numerics;
using Prospect.Client.Render;
using Prospect.Game.World;

namespace Prospect.Client.World;

// What every posed turret shares, whatever head it carries (the tractor beam's emitter, the mining laser's barrel):
// where its yoke and head sit on the mount, the angles it remembers from frame to frame and turns at a limited rate,
// and the three draws it takes (yoke, head, and the part of the head that is light). Fixed tables, no memory in a frame.
internal sealed partial class WorldView
{
    private const int MaxTurretStates = 256;

    /// <summary>
    /// The material of a part that is only light: <c>mesh.frag</c> writes the instance's colour as it is, unlit, for a
    /// material below zero (as it does for sparks), so the colour's size is the brightness.
    /// </summary>
    private const float MaterialLight = -1f;

    private struct TurretState
    {
        public uint HullId;
        public int Mount;
        public float Azimuth;
        public float Pitch;
        public float Glow;
        public Vector3 LensRel;
        public long LastFrame;
        public long LensFrame;
    }

    /// <summary>Where a turret's parts sit this frame, camera-relative: the collar's top, the trunnion, and the face's frame.</summary>
    private readonly struct TurretSeat(Vector3 collar, Vector3 pivot, Quaternion face)
    {
        public readonly Vector3 Collar = collar, Pivot = pivot;
        public readonly Quaternion Face = face;
    }

    private readonly TurretState[] _turrets = new TurretState[MaxTurretStates];
    private long _turretFrame;
    private float _lastTurretTime;
    private float _turretDt;

    private void BeginTurrets(float time)
    {
        _turretFrame++;
        _turretDt = _lastTurretTime > 0f ? Math.Clamp(time - _lastTurretTime, 0f, 0.1f) : 0f;
        _lastTurretTime = time;
    }

    /// <summary>The turn from a face's own frame (x its first axis, z its outward normal) to the hull's.</summary>
    internal static Quaternion FaceRotation(BlockFacing face)
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

    /// <summary>
    /// Where a turret on <paramref name="face"/> rests while it has nothing to aim at: turned to look along the hull's
    /// nose (a turret on the nose or the stern looks up instead) and pitched <paramref name="restPitch"/> degrees off
    /// the face normal. The angles are the ones the aim uses.
    /// </summary>
    internal static void TurretRest(BlockFacing face, float restPitch, out float azimuth, out float pitch)
    {
        var axis = Facings.Axis(face);
        var u = Vector3.Zero;
        u[(axis + 1) % 3] = 1f;
        var v = Vector3.Cross(Facings.Vector(face), u);
        var ahead = axis == Facings.Axis(BlockFacing.Front) ? Vector3.UnitY : Facings.Vector(BlockFacing.Front);
        azimuth = MathF.Atan2(-Vector3.Dot(ahead, u), Vector3.Dot(ahead, v));
        pitch = restPitch * (MathF.PI / 180f);
    }

    /// <summary>The middle of the face mount <paramref name="mount"/> of a drawn hull sits on, camera-relative.</summary>
    private static Vector3 MountFace(HullDesign design, int mount, Vector3 hullRel, Quaternion orientation, float stretch)
    {
        var face = design.Mounts[mount].Face;
        var local = design.MountOffsets[mount] - Facings.Vector(face) * design.MountHalfExtents[mount][Facings.Axis(face)];
        local.Z *= stretch;
        return hullRel + Vector3.Transform(local, orientation);
    }

    /// <summary>Where the yoke and the head of a turret of <paramref name="frame"/> sit over the middle of its face.</summary>
    private static TurretSeat SeatTurret(in TurretFrame frame, float small, Vector3 faceRel, BlockFacing face, Quaternion orientation)
    {
        var faceRot = Quaternion.Concatenate(FaceRotation(face), orientation);
        return new TurretSeat(
            faceRel + Vector3.Transform(new Vector3(0f, 0f, frame.CollarOffset(small)), faceRot),
            faceRel + Vector3.Transform(new Vector3(0f, 0f, frame.PivotOffset(small)), faceRot),
            faceRot);
    }

    /// <summary>
    /// The slot that remembers the turret on <paramref name="mount"/> of a hull, or -1 when every slot is in use this
    /// frame. <paramref name="live"/> says it was drawn last frame, so its angles are where the turret is now.
    /// </summary>
    private int AcquireTurretSlot(uint hullId, int mount, out bool live)
    {
        live = false;
        var free = -1;
        var oldest = long.MaxValue;
        for (var i = 0; i < _turrets.Length; i++)
        {
            ref var s = ref _turrets[i];
            if (s.HullId == hullId && s.Mount == mount)
            {
                live = s.LastFrame == _turretFrame - 1 && _turretDt > 0f;
                return i;
            }
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

    /// <summary>
    /// Turns a remembered turret toward the angles and glow given, at most <paramref name="slewStep"/> radians and
    /// <paramref name="glowStep"/> of glow this frame, and gives back where it is now. A turret not drawn last frame
    /// is there at once.
    /// </summary>
    private void SlewTurret(int slot, bool live, float slewStep, float glowStep, ref float azimuth, ref float pitch, ref float glow)
    {
        if (slot < 0)
            return;
        ref var state = ref _turrets[slot];
        if (live)
        {
            azimuth = StepAngle(state.Azimuth, azimuth, slewStep);
            pitch = StepPitch(state.Pitch, pitch, slewStep);
            glow = StepPitch(state.Glow, glow, glowStep);
        }
        state.Azimuth = azimuth;
        state.Pitch = pitch;
        state.Glow = glow;
        state.LastFrame = _turretFrame;
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

    /// <summary>
    /// Draws a turret's three posed parts: the yoke turned <paramref name="azimuth"/> about the face normal, the head
    /// pitched <paramref name="pitch"/> off it on the yoke's trunnion, and the head's light as <paramref name="light"/>,
    /// unlit. Gives back the head's turn, for where its beam starts.
    /// </summary>
    private Quaternion DrawTurret(int frameIndex, in TurretSeat seat, float azimuth, float pitch, ReadOnlySpan<MeshVertex> yoke, ReadOnlySpan<MeshVertex> head,
        ReadOnlySpan<MeshVertex> lit, Vector3 color, float material, Vector3 light)
    {
        var yokeRot = Quaternion.Concatenate(Quaternion.CreateFromAxisAngle(Vector3.UnitZ, azimuth), seat.Face);
        var headRot = Quaternion.Concatenate(Quaternion.CreateFromAxisAngle(Vector3.UnitX, -pitch), yokeRot);
        _scene.AddTriangles(frameIndex, yoke, MeshTransform(seat.Collar, yokeRot, Vector3.One, color, material, 1f));
        _scene.AddTriangles(frameIndex, head, MeshTransform(seat.Pivot, headRot, Vector3.One, color, material, 1f));
        _scene.AddTriangles(frameIndex, lit, MeshTransform(seat.Pivot, headRot, Vector3.One, light, MaterialLight, 1f));
        return headRot;
    }

    /// <summary>Where the beam of the turret on <paramref name="mount"/> of a hull leaves its head this frame; false for a turret not drawn.</summary>
    private bool TurretLens(uint hullId, int mount, out Vector3 lensRel)
    {
        for (var s = 0; s < _turrets.Length; s++)
        {
            ref readonly var state = ref _turrets[s];
            if (state.HullId == hullId && state.Mount == mount && state.LensFrame == _turretFrame)
            {
                lensRel = state.LensRel;
                return true;
            }
        }
        lensRel = default;
        return false;
    }

    /// <summary>
    /// The idle turret of a mounted part in the editor's draft or as a placement ghost: a tractor beam's or a mining
    /// laser's yoke and head at rest over the base the part's own model draws. Nothing for any other part.
    /// </summary>
    private void DrawIdleTurret(int frameIndex, BlockType type, Vector3 mountCenter, BlockFacing face, Vector3 half, Quaternion orientation, Vector3 color, float material)
    {
        var small = MountHalfWidth(half, face);
        var facePos = mountCenter - Vector3.Transform(Facings.Vector(face) * half[Facings.Axis(face)], orientation);
        if (type.Tractor is not null)
        {
            if (!_partModels.TryGetTurret(TurretKind.Tractor, small, out var yoke, out var head, out var bars))
                return;
            var look = TractorLook;
            TurretRest(face, look.TurretRestPitch, out var azimuth, out var pitch);
            DrawTurret(frameIndex, SeatTurret(look.Turret, small, facePos, face, orientation), azimuth, pitch, yoke, head, bars, color, material, look.BarLight(look.TurretIdleGlow));
        }
        else if (type.Laser is not null && PropulsionLook.Shared.Laser is { } laser)
        {
            if (!_partModels.TryGetTurret(TurretKind.Laser, small, out var yoke, out var head, out var lens))
                return;
            DrawTurret(frameIndex, SeatTurret(laser.Turret, small, facePos, face, orientation), LaserRestAzimuth(face), 0f, yoke, head, lens, color, material, laser.LensLight(0f, 0f, 0f));
        }
    }
}
