using System.Numerics;

namespace Prospect.Game.World;

/// <summary>Authoritative automatic pickup. The fixed free-body pool also holds exclusive transient claims.</summary>
public sealed class TractorSimulation
{
    private int _acquireHull, _acquireMount;
    private readonly HoldContainer[] _reserved = new HoldContainer[BlockStateStore.MaxContainers];

    /// <summary>Validates existing claims first, then lets free mounts acquire in hull/mount/body order.</summary>
    public void Plan(FreeContainers containers, GameContent content, HullField field, Span<byte> occupied, long tick = 0)
    {
        var hulls = field.Hulls;
        for (var h = 0; h < hulls.Slots; h++)
            if (hulls.IsAliveAt(h)) hulls.Info(h).TractorDemand = 0;
        for (var c = 0; c < containers.Items.Length; c++)
        {
            ref var item = ref containers.At(c);
            item.TractorPowered = false;
            if (item.TractorHull == 0) continue;
            var handle = Handle(item.TractorHull);
            if (!hulls.IsAlive(handle) || !Eligible(hulls, handle.Index)
                || !item.MayPickup(hulls.Info(handle.Index).Owner, tick)
                || !Valid(containers, content, field, handle.Index, item.TractorMount, ref item, occupied, needSight: false)) Release(ref item);
        }
        Acquire(containers, content, field, occupied, tick);
        foreach (var item in containers.Items)
            if (item.TractorHull != 0)
            {
                var h = Handle(item.TractorHull).Index;
                var spec = field.Designs[hulls.Info(h).Design].Mounts[item.TractorMount].Type.Tractor!;
                hulls.Info(h).TractorDemand += spec.Power(Mass(content.Cargo, item));
            }
    }

    private void Acquire(FreeContainers containers, GameContent content, HullField field, Span<byte> occupied, long tick = 0)
    {
        var hulls = field.Hulls;
        if (containers.Items.IsEmpty || hulls.Slots == 0) return;
        var budget = FreeContainers.Capacity;
        var start = _acquireHull % hulls.Slots;
        for (var offset = 0; offset < hulls.Slots; offset++)
        {
            var h = (start + offset) % hulls.Slots;
            if (!hulls.IsAliveAt(h) || !Eligible(hulls, h)) continue;
            var design = field.Designs[hulls.Info(h).Design];
            var id = hulls.HandleAt(h).Packed;
            for (var t = offset == 0 ? _acquireMount : 0; t < design.Tractors.Length; t++)
            {
                var m = design.Tractors[t];
                var busy = false;
                foreach (var item in containers.Items)
                    if (item.TractorHull == id && item.TractorMount == m) { busy = true; break; }
                if (busy || !BlockSet.Has(hulls.Blocks(h), design.Mounts[m].Parent)) continue;
                // Reserve a full bounded pool scan before starting a mount. Resume here next tick so crowded,
                // unsuitable cargo cannot starve later mounts or hulls, and no body's tail is skipped forever.
                if (budget < containers.Items.Length) { _acquireHull = h; _acquireMount = t; return; }
                var spec = design.Mounts[m].Type.Tractor!;
                var emitter = hulls.State(h).Position + HullRotation.Rotate(design.MountOffsets[m], hulls.State(h).Orientation);
                for (var c = 0; c < containers.Items.Length; c++)
                {
                    budget--;
                    ref var item = ref containers.At(c);
                    if (!item.MayPickup(hulls.Info(h).Owner, tick) || item.TractorHull != 0 || item.Position.RelativeTo(emitter).LengthSquared() > (spec.Range + design.BlockSize) * (spec.Range + design.BlockSize)) continue;
                    if (!Valid(containers, content, field, h, m, ref item, occupied, needSight: true)) continue;
                    item.TractorHull = id; item.TractorMount = m; item.TractorStage = 0; item.TractorBest = 0; item.TractorIdle = 0;
                    break;
                }
            }
        }
        _acquireHull = _acquireMount = 0;
    }

    /// <summary>
    /// Whether the mount may hold the body and has a rack to load it into. A new claim also needs the beam's line of
    /// sight to it; a held body that has lost it is not let go at once, <see cref="Pull"/> times it out.
    /// </summary>
    private bool Valid(FreeContainers containers, GameContent content, HullField field, int h, int mount,
        ref FreeContainer item, Span<byte> occupied, bool needSight)
    {
        var hulls = field.Hulls; var info = hulls.Info(h); var design = field.Designs[info.Design];
        if (item.ClaimOwner != 0 && item.ClaimOwner != info.Owner) return false;
        if ((uint)mount >= (uint)design.Mounts.Length || design.Mounts[mount].Type.Tractor is not { } spec
            || Mass(content.Cargo, item) > spec.MaxMass) return false;
        if (needSight && !TractorReach.Container(design.Boxes, hulls.Blocks(h), design.BlockSize, design.Mounts[mount],
            Local(design, hulls.State(h), item.Position))) return false;
        var count = 0; var id = hulls.HandleAt(h).Packed;
        foreach (var other in containers.Items)
            if (other.Id != item.Id && other.TractorHull == id)
            {
                if (count == _reserved.Length) return false;
                _reserved[count++] = Held(other);
            }
        var rack = TractorReach.ChooseRack(content.Cargo, design.Boxes, design.Mounts, hulls.Blocks(h), design.BlockSize,
            mount, hulls.BlockStates.Cargo(info.BlockState), _reserved.AsSpan(0, count), item.Size, occupied);
        if (rack < 0) return false;
        if (item.TractorRack != rack) item.TractorStage = 0;
        item.TractorRack = rack;
        return true;
    }

    /// <summary>
    /// Uses only power already billed by FlightModel. Bodies remain free until the rack accepts them. A held body
    /// follows <see cref="TractorPath"/>: to the staging point outside the rack's face (by way of the emitter's exit
    /// when the straight line is blocked), then a slow glide along the face normal into its slot, where it is stowed.
    /// <see cref="ContainerSimulation"/> lets it pass its own hull's blocks meanwhile. A body that makes no progress, or
    /// that the beam cannot see, for <see cref="TractorGuidance.StallSeconds"/> is let go and waits
    /// <see cref="TractorGuidance.RetrySeconds"/> before any tractor tries it again, so nothing jams for ever.
    /// </summary>
    public void Pull(FreeContainers containers, GameContent content, HullField field, Span<byte> occupied, long tick = 0)
    {
        var hulls = field.Hulls; var tuning = content.Cargo.Tractor;
        for (var c = containers.Items.Length - 1; c >= 0; c--)
        {
            ref var item = ref containers.At(c);
            if (item.TractorHull == 0) continue;
            var handle = Handle(item.TractorHull);
            if (!hulls.IsAlive(handle) || !Eligible(hulls, handle.Index)) { Release(ref item); continue; }
            var h = handle.Index; ref var info = ref hulls.Info(h);
            var design = field.Designs[info.Design]; var state = hulls.State(h);
            if (!Valid(containers, content, field, h, item.TractorMount, ref item, occupied, needSight: false)) { Release(ref item); continue; }
            if (!TractorPath.TryGuide(content.Cargo, design, hulls.Blocks(h), item.TractorMount, item.TractorRack, item.Size,
                hulls.BlockStates.Cargo(info.BlockState), occupied, out var guide)) { Release(ref item); continue; }
            var mount = design.Mounts[item.TractorMount]; var spec = mount.Type.Tractor!;
            var final = item.TractorStage != 0;
            var local = Local(design, state, item.Position);
            // On the final glide the slot may lie behind the hull's own corner as the emitter sees it; what held the
            // container was the rack's open face in view (TryGuide), so only the range is asked of it then.
            var sight = final ? TractorReach.InRange(design.Boxes, hulls.Blocks(h), design.BlockSize, mount, local, guide.Slack)
                : TractorReach.Container(design.Boxes, hulls.Blocks(h), design.BlockSize, mount, local, guide.Slack);

            // Staging first; by way of the emitter's exit only when the container has no clear line to it.
            var viaExit = !final && !TractorReach.Clear(design.Boxes, hulls.Blocks(h), design.BlockSize, local, guide.Staging);
            var target = final ? guide.Slot : viaExit ? guide.Exit : guide.Staging;
            var left = Vector3.Distance(local, target) + (viaExit ? Vector3.Distance(guide.Exit, guide.Staging) : 0)
                + (final ? 0 : Vector3.Distance(guide.Staging, guide.Slot));
            var powered = info.TractorDemand > 0 && info.Power.Tractor >= info.TractorDemand;
            if (!sight) item.TractorIdle++;
            else if (powered)
            {
                if (item.TractorBest <= 0 || left < item.TractorBest - tuning.ProgressMetres) { item.TractorBest = Math.Max(left, 1e-6f); item.TractorIdle = 0; }
                else item.TractorIdle++;
            }
            if (item.TractorIdle >= tuning.StallTicks)
            {
                item.PickupReadyTick = tick + (long)Math.Ceiling(tuning.RetrySeconds * Prospect.Engine.Time.TickLoop.SimulationHz);
                Release(ref item);
                continue;
            }
            if (!powered || !sight) continue;

            var delta = target - local;
            var distance = delta.Length();
            var offset = target - Pivot(design);
            var carrierVelocity = state.Velocity + HullRotation.Rotate(Vector3.Cross(state.AngularVelocity,
                offset - hulls.Flight(h).CenterOfMass), state.Orientation);
            var relative = item.Velocity - carrierVelocity;
            var tolerance = Math.Max(tuning.ToleranceMetres, relative.Length() * HullSim.Dt);
            item.TractorPowered = true;
            // Turn onto the hull's axes, as the slot is, and take its spin; a container never settles tumbling.
            item.Orientation = Quaternion.Normalize(Quaternion.Slerp(item.Orientation, state.Orientation,
                Math.Min(1f, tuning.AlignRate * HullSim.Dt)));
            item.AngularVelocity = state.AngularVelocity;
            if (distance <= tolerance && !viaExit)
            {
                if (!final) { item.TractorStage = 1; item.Velocity = carrierVelocity; continue; }
                var aligned = Math.Abs(Quaternion.Dot(item.Orientation, state.Orientation)) >= AlignedDot;
                if (aligned && hulls.BlockStates.TryAddCargo(info.BlockState, content, Held(item), occupied))
                { containers.Remove(c); field.Refit(h); }
                continue;
            }
            // Inside its slot's cell and nearly still counts as home, so the sqrt braking profile need not creep in.
            if (final && relative.Length() <= tuning.SettleSpeed && Math.Abs(delta.X) <= guide.Half.X
                && Math.Abs(delta.Y) <= guide.Half.Y && Math.Abs(delta.Z) <= guide.Half.Z
                && Math.Abs(Quaternion.Dot(item.Orientation, state.Orientation)) >= AlignedDot
                && hulls.BlockStates.TryAddCargo(info.BlockState, content, Held(item), occupied))
            { containers.Remove(c); field.Refit(h); continue; }

            var speed = MathF.Sqrt(2 * spec.Acceleration * distance);
            if (final) speed = Math.Min(speed, tuning.FinalSpeed);
            var desired = distance > 1e-6f ? HullRotation.Rotate(delta / distance, state.Orientation) * speed : Vector3.Zero;
            var change = desired - relative;
            var length = change.Length();
            if (length > 0) item.Velocity += change * (Math.Min(length, spec.Acceleration * HullSim.Dt) / length);
        }
    }

    /// <summary>Cosine of half the turn (about 0.1 rad in all) a container may still be short of the hull's axes when stowed.</summary>
    private const float AlignedDot = .9987f;

    /// <summary>Payload and shell mass in kilograms.</summary>
    public static double Mass(CargoCatalog cargo, in FreeContainer item) => item.Amount + cargo.SpaceShellKg * cargo.Sizes[item.Size].Units;
    private static bool Eligible(HullStore hulls, int h) => !hulls.Info(h).Wreck && hulls.State(h).Jump == JumpPhase.None;
    private static HullHandle Handle(uint id) => new((int)(id & 65535), (int)(id >> 16));
    private static HoldContainer Held(in FreeContainer item) => new(item.TractorRack, item.Size, item.Resource, item.Amount, item.Hull, default);
    private static Vector3 Pivot(HullDesign design) => (design.CenterOfMass + new Vector3(.5f)) * design.BlockSize;
    private static Vector3 Local(HullDesign design, in HullState state, Double3 point) =>
        HullRotation.Rotate(point.RelativeTo(state.Position), Quaternion.Conjugate(state.Orientation)) + Pivot(design);
    private static void Release(ref FreeContainer item) => FreeContainers.ClearTractor(ref item);
}
