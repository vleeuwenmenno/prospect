using System.Numerics;
using Prospect.Client.Render;
using Prospect.Game.Client;
using Prospect.Game.World;

namespace Prospect.Client.World;

// The mining laser's turret: the base is in the hull mesh, and the yoke, the head and the lens are posed here each
// frame through the shared turret code. The head lies along the beam the snapshot lists for its laser and rests along
// the face normal otherwise; the lens brightens while it fires and, on the flown hull, with the laser's heat.
internal sealed partial class WorldView
{
    /// <summary>How fast a laser's lens brightens when it fires and dims when it stops, in shares a second.</summary>
    private const float LaserGlowRate = 8f;

    /// <summary>How far off its face normal a beam must aim before the turret turns to it, in radians (1.5 degrees).</summary>
    internal const float LaserAimDeadband = 0.026f;

    /// <summary>
    /// The yoke's turn for a laser at rest on <paramref name="face"/>: the head points straight out of the face, and
    /// this turns its top toward the hull's nose (up, for a laser on the nose or the stern).
    /// </summary>
    internal static float LaserRestAzimuth(BlockFacing face)
    {
        TurretRest(face, 0f, out var azimuth, out _);
        return MathF.IEEERemainder(azimuth + MathF.PI, MathF.Tau);
    }

    /// <summary>
    /// The yoke's turn and the head's pitch that lay the barrel along <paramref name="to"/> (in the face's frame, z
    /// its outward normal), for a yoke now turned <paramref name="current"/>. A laser aims near its face normal, where
    /// a yoke that only pitched one way would spin half a turn each time the aim crossed the normal; so the head
    /// pitches either way, and of the two turns half a circle apart that reach the aim, the nearer is taken: the
    /// yoke never turns more than a quarter circle. An aim within <see cref="LaserAimDeadband"/> of the normal leaves
    /// the yoke where it is and the head straight out: so near the normal the turn is anyone's guess, and following
    /// it would spin the yoke for an aim no eye can tell from straight. The pitch stops at <paramref name="maxPitch"/>
    /// either way.
    /// </summary>
    internal static void LaserTurretAim(Vector3 to, float current, float maxPitch, out float azimuth, out float pitch)
    {
        azimuth = current;
        pitch = 0f;
        var across = MathF.Sqrt(to.X * to.X + to.Y * to.Y);
        var off = MathF.Atan2(across, to.Z);
        if (off <= LaserAimDeadband)
            return;
        azimuth = MathF.Atan2(-to.X, to.Y);
        pitch = MathF.Min(off, maxPitch);
        if (MathF.Abs(MathF.IEEERemainder(azimuth - current, MathF.Tau)) > 0.5f * MathF.PI)
        {
            azimuth = MathF.IEEERemainder(azimuth + MathF.PI, MathF.Tau);
            pitch = -pitch;
        }
    }

    // Where the beam of laser `part` of a hull ends, relative to the hull as it is drawn; false while it does not fire.
    private static bool FindLaserBeam(SessionSnapshot snapshot, uint hullId, int part, out Vector3 end)
    {
        for (var b = 0; b < snapshot.LaserBeamCount; b++)
        {
            ref readonly var beam = ref snapshot.LaserBeams[b];
            if (beam.Hull != hullId || beam.Part != part)
                continue;
            end = beam.End;
            return true;
        }
        end = default;
        return false;
    }

    private void DrawLaserTurrets(int frameIndex, SessionSnapshot snapshot, in HullView hull, HullDesign design, Vector3 hullRel, Quaternion orientation, float material, float stretch)
    {
        if (design.Lasers.Length == 0 || PropulsionLook.Shared.Laser is not { } look)
            return;
        var frame = look.Turret;
        var alive = hull.Blocks.Span;
        var slewStep = look.SlewRate * (MathF.PI / 180f) * _turretDt;
        var maxPitch = look.MaxPitch * (MathF.PI / 180f);
        var heated = snapshot.LaserHeatHull == hull.Id;

        for (var k = 0; k < design.Lasers.Length; k++)
        {
            var m = design.Lasers[k];
            var part = design.Mounts[m];
            if (!BlockSet.Has(alive, part.Parent))
                continue;
            var small = MountHalfWidth(design.MountHalfExtents[m], part.Face);
            if (!_partModels.TryGetTurret(TurretKind.Laser, small, out var yoke, out var head, out var lens))
                continue;
            var seat = SeatTurret(frame, small, MountFace(design, m, hullRel, orientation, stretch), part.Face, orientation);

            var slot = AcquireTurretSlot(hull.Id, m, out var live);
            // Idle, the head points straight out of the face and the yoke turns back to its rest.
            var az = LaserRestAzimuth(part.Face);
            var pitch = 0f;
            var glow = 0f;
            if (FindLaserBeam(snapshot, hull.Id, k, out var end))
            {
                glow = 1f;
                LaserTurretAim(Vector3.Transform(hullRel + end - seat.Pivot, Quaternion.Conjugate(seat.Face)), live ? _turrets[slot].Azimuth : az, maxPitch, out az, out pitch);
            }
            SlewTurret(slot, live, slewStep, LaserGlowRate * _turretDt, ref az, ref pitch, ref glow);

            var heat = heated ? snapshot.LaserHeat.Of(k) : 0f;
            var headRot = DrawTurret(frameIndex, seat, az, pitch, yoke, head, lens, design.Color, material, look.LensLight(glow, heat, _lastTurretTime));
            if (slot >= 0)
            {
                ref var state = ref _turrets[slot];
                state.LensRel = seat.Pivot + Vector3.Transform(new Vector3(0f, 0f, PropulsionModels.LaserLensOffset(small, look)), headRot);
                state.LensFrame = _turretFrame;
            }
        }
    }

    // A laser capture leaves the command plane's mark out: it would cross the turret it looks at.
    private bool _laserCapture;

    /// <summary>
    /// Muted capture harness hook (<c>--laser-camera</c>): holds the command camera on the flown hull's first mining
    /// laser, where it is drawn, <paramref name="distance"/> metres off (closer than the command view lets a player
    /// come). The angles are radians in the hull's own frame, so a yaw of half a turn looks at the nose from ahead
    /// however the hull lies. False, changing nothing, for a hull without one. Never called from ordinary input.
    /// </summary>
    internal bool SetLaserCapturePose(float distance, float yaw, float pitch)
    {
        if (!_devices.Muted || _snapshot is not { OwnHull: >= 0 } snapshot || snapshot.OwnHull >= _visuals.Hulls.Length)
            return false;
        ref readonly var hull = ref snapshot.Hulls[snapshot.OwnHull];
        if (hull.Design >= InstalledDesigns || hull.Design >= _designs.Length || _designs[hull.Design].Lasers.Length == 0)
            return false;
        var design = _designs[hull.Design];
        var mount = design.Lasers[0];
        ref readonly var visual = ref _visuals.Hulls[snapshot.OwnHull];
        var at = design.MountOffsets[mount] + Facings.Vector(design.Mounts[mount].Face) * (3f * design.MountHalfExtents[mount][Facings.Axis(design.Mounts[mount].Face)]);
        var away = HullRotation.Rotate(Vector3.Transform(Vector3.UnitZ, Quaternion.CreateFromYawPitchRoll(yaw, pitch, 0f)), visual.Orientation);
        SetCommandCapturePose(DrawnPosition(visual) + HullRotation.Rotate(at, visual.Orientation), distance, MathF.Atan2(away.X, away.Z), 0f);
        _commandDistance = distance;
        _commandPitch = -MathF.Asin(Math.Clamp(away.Y, -1f, 1f));
        _laserCapture = true;
        return true;
    }

    /// <summary>
    /// Draws the light of every bell throat that fires this frame over its lining: one unlit model a plume
    /// (<see cref="PartModels.TryGetNozzleGlow"/>), scaled to its nozzle and as bright as the nozzle's thrust. Read from
    /// what <see cref="ThrustEffects"/> wrote for the plumes, so it never works thrust out again. Takes no memory.
    /// </summary>
    private void DrawNozzleGlow(int frameIndex)
    {
        var models = PropulsionLook.Shared.GlowModels;
        for (var i = 0; i < DrawnPlumes; i++)
        {
            var slot = _thrust.Glows[i];
            if ((uint)slot >= (uint)models.Length || !_partModels.TryGetNozzleGlow(slot, out var vertices))
                continue;
            ref readonly var bell = ref _thrust.Bells[i];
            _scene.AddTriangles(frameIndex, vertices, MeshTransform(bell.Start, bell.Turn, bell.Size, models[slot].Bell!.GlowLight(_thrust.Strengths[i]), MaterialLight, 1f));
        }
    }
}
