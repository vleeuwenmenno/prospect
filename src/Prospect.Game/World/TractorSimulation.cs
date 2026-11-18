using System.Numerics;

namespace Prospect.Game.World;

/// <summary>Why no tractor beam of a hull can take a container drones bring it.</summary>
public enum FetchRefusal : byte
{
    None = 0,

    /// <summary>No live beam sees the hand-over point.</summary>
    NoTractor = 1,

    /// <summary>The container is heavier than any beam that sees the point may hold.</summary>
    TooHeavy = 2,

    /// <summary>No rack a beam sees has room for it.</summary>
    NoRoom = 3,

    /// <summary>Every beam that could take it holds another container now.</summary>
    Busy = 4,
}

/// <summary>
/// Authoritative automatic pickup. The fixed free-body pools also hold the exclusive transient claims: a beam holds
/// one loose container or one ore chunk at a time, and a container always comes first.
/// </summary>
public sealed partial class TractorSimulation
{
    private int _acquireHull, _acquireMount;
    private readonly HoldContainer[] _reserved = new HoldContainer[BlockStateStore.MaxContainers];

    /// <summary>The same for a world with no ore chunks.</summary>
    public void Plan(FreeContainers containers, GameContent content, HullField field, Span<byte> occupied, long tick = 0) =>
        Plan(containers, null, content, field, occupied, tick);

    /// <summary>
    /// Validates existing claims first, then lets free mounts acquire in hull/mount/body order: a loose container
    /// first, an ore chunk only when the beam took no container and sees a hopper with room behind it.
    /// </summary>
    public void Plan(FreeContainers containers, OreChunks? chunks, GameContent content, HullField field, Span<byte> occupied, long tick = 0)
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
        if (chunks is not null) ValidateChunks(chunks, field);
        Acquire(containers, chunks, content, field, occupied, tick);
        foreach (var item in containers.Items)
            if (item.TractorHull != 0)
            {
                var h = Handle(item.TractorHull).Index;
                var spec = field.Designs[hulls.Info(h).Design].Mounts[item.TractorMount].Type.Tractor!;
                hulls.Info(h).TractorDemand += spec.Power(Mass(content.Cargo, item));
            }
        if (chunks is not null) DemandChunks(chunks, field);
    }

    private void Acquire(FreeContainers containers, OreChunks? chunks, GameContent content, HullField field, Span<byte> occupied, long tick = 0)
    {
        var hulls = field.Hulls;
        var loose = chunks is null ? 0 : chunks.Items.Length;
        if ((containers.Items.IsEmpty && loose == 0) || hulls.Slots == 0) return;
        var budget = FreeContainers.Capacity;
        var chunkBudget = OreChunks.Capacity;
        var start = _acquireHull % hulls.Slots;
        for (var offset = 0; offset < hulls.Slots; offset++)
        {
            var h = (start + offset) % hulls.Slots;
            if (!hulls.IsAliveAt(h) || !Eligible(hulls, h)) continue;
            var design = field.Designs[hulls.Info(h).Design];
            var id = hulls.HandleAt(h).Packed;
            // Chunks are for a hull with a hopper and a bin: every other hull never pays for looking at them.
            var ore = loose > 0 && design.Hoppers.Length > 0 && design.Bins.Length > 0;
            for (var t = offset == 0 ? _acquireMount : 0; t < design.Tractors.Length; t++)
            {
                var m = design.Tractors[t];
                var busy = Holds(containers, chunks, id, m);
                if (busy || !BlockSet.Has(hulls.Blocks(h), design.Mounts[m].Parent)) continue;
                // Reserve a full bounded pool scan before starting a mount. Resume here next tick so crowded,
                // unsuitable cargo cannot starve later mounts or hulls, and no body's tail is skipped forever.
                if (budget < containers.Items.Length || (ore && chunkBudget < loose)) { _acquireHull = h; _acquireMount = t; return; }
                var spec = design.Mounts[m].Type.Tractor!;
                var emitter = hulls.State(h).Position + HullRotation.Rotate(design.MountOffsets[m], hulls.State(h).Orientation);
                var taken = false;
                for (var c = 0; c < containers.Items.Length; c++)
                {
                    budget--;
                    ref var item = ref containers.At(c);
                    if (!item.MayPickup(hulls.Info(h).Owner, tick) || item.TractorHull != 0 || item.FetchHull != 0 || item.Position.RelativeTo(emitter).LengthSquared() > (spec.Range + design.BlockSize) * (spec.Range + design.BlockSize)) continue;
                    if (!Valid(containers, content, field, h, m, ref item, occupied, needSight: true)) continue;
                    item.TractorHull = id; item.TractorMount = m; item.TractorStage = 0; item.TractorBest = 0; item.TractorIdle = 0; item.TractorRoute = 0;
                    taken = true;
                    break;
                }

                // A loose container always comes first: the beam looks at chunks only when it took none.
                if (taken || !ore) continue;
                chunkBudget -= loose;
                AcquireChunk(chunks!, content, field, h, m, emitter, spec.Range + design.BlockSize, tick);
            }
        }
        _acquireHull = _acquireMount = 0;
    }

    /// <summary>Whether mount <paramref name="mount"/> of hull <paramref name="id"/> holds a container or an ore chunk now.</summary>
    private static bool Holds(FreeContainers containers, OreChunks? chunks, uint id, int mount)
    {
        foreach (ref readonly var item in containers.Items)
            if (item.TractorHull == id && item.TractorMount == mount) return true;
        if (chunks is null) return false;
        foreach (ref readonly var item in chunks.Items)
            if (item.TractorHull == id && item.TractorMount == mount) return true;
        return false;
    }

    /// <summary>
    /// Whether the mount may hold the body and has a rack to load it into. A new claim also needs the beam's line of
    /// sight to it; a held body that has lost it is pulled back into view (<see cref="Pull"/>).
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
    /// when the straight line is blocked, or when the beam has lost sight of it), then a slow glide along the face
    /// normal into its slot, where it is stowed. <see cref="ContainerSimulation"/> lets it pass its own hull's blocks
    /// meanwhile. A body the beam fails to bring closer for <see cref="TractorGuidance.StallSeconds"/> is let go and
    /// waits <see cref="TractorGuidance.RetrySeconds"/> before any tractor tries it again, so nothing jams for ever.
    /// </summary>
    public void Pull(FreeContainers containers, GameContent content, HullField field, Span<byte> occupied, long tick = 0) =>
        Pull(containers, null, content, field, occupied, tick);

    /// <summary>The containers' pull, then the ore chunks' (<see cref="PullChunks"/>).</summary>
    public void Pull(FreeContainers containers, OreChunks? chunks, GameContent content, HullField field, Span<byte> occupied, long tick = 0)
    {
        if (chunks is not null) PullChunks(chunks, content, field, tick);
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
            // The hull has already stepped this tick and the body drifts only after the pull, so its position now is a
            // tick older than the hull's pose. Measure where it will be at the end of the tick instead: at 240 m/s the
            // stale position sat 4 m astern, behind the hull's own blocks, and the beam lost sight and let go.
            var local = Local(design, state, item.Position + item.Velocity * HullSim.Dt);
            // On the final glide the slot may lie behind the hull's own corner as the emitter sees it; what held the
            // container was the rack's open face in view (TryGuide), so only the range is asked of it then.
            var sight = final ? TractorReach.InRange(design.Boxes, hulls.Blocks(h), design.BlockSize, mount, local, guide.Slack)
                : TractorReach.Container(design.Boxes, hulls.Blocks(h), design.BlockSize, mount, local, guide.Slack);
            // A body the beam has lost sight of is pulled back to just outside the emitter, the one place the beam
            // always sees, instead of hanging where it is until the stall lets it go: a body lost behind the hull, or
            // carried out of range, used to drift unsteered for the whole stall time. Losing the range on the final
            // glide takes it off that leg and it starts again at the exit.
            if (!sight && final) { final = false; item.TractorStage = 0; }
            // Staging first; by way of the emitter's exit when the container has no clear line to it, or none at all.
            var viaExit = !final && (!sight
                || !TractorReach.Clear(design.Boxes, hulls.Blocks(h), design.BlockSize, local, guide.Staging));
            var target = final ? guide.Slot : viaExit ? guide.Exit : guide.Staging;
            var delta = target - local;
            var distance = delta.Length();
            var left = distance + (viaExit ? Vector3.Distance(guide.Exit, guide.Staging) : 0)
                + (final ? 0 : Vector3.Distance(guide.Staging, guide.Slot));
            var powered = info.TractorDemand > 0 && info.Power.Tractor >= info.TractorDemand;
            var speed = Math.Min(Approach.Speed(spec.Acceleration, distance, tuning.ApproachBrakeShare),
                final ? tuning.FinalSpeed : float.PositiveInfinity);
            // The beam must keep bringing the body closer: a tick it spends without power, or one in which it pulls
            // and the body still gains nothing on the way it is taking, counts against it.
            var gained = false;
            if (powered)
            {
                // A longer way (the exit route instead of the straight one, or the glide given up for the exit) is
                // measured from its own start: a body sent round the hull's own blocks is coming in, not stalled. Only
                // a change of leg may do this. Raising the best whenever the path grew let a body pressed against a
                // rock, pushed back and pulled in again each tick, count as gaining for ever, so it never reached the
                // stall and the beam never let it go. The idle count is kept: a change of leg is not progress.
                var route = (byte)(final ? 3 : viaExit ? 2 : 1);
                if (route != item.TractorRoute)
                {
                    if (item.TractorRoute != 0 && item.TractorBest > 0) item.TractorBest = Math.Max(left, 1e-6f);
                    item.TractorRoute = route;
                }
                // The least shrink that counts is the configured distance, but never more than the approach rule lets
                // the body close in a tick: a body creeping in on the final glide is coming in, not stalled.
                if (item.TractorBest <= 0 || left < item.TractorBest - Approach.NeededProgress(speed, tuning.ProgressMetres))
                {
                    item.TractorBest = Math.Max(left, 1e-6f);
                    gained = true;
                }
            }
            if (gained) item.TractorIdle = 0;
            else item.TractorIdle++;
            if (item.TractorIdle >= tuning.StallTicks)
            {
                item.PickupReadyTick = tick + (long)Math.Ceiling(tuning.RetrySeconds * Prospect.Engine.Time.TickLoop.SimulationHz);
                Release(ref item);
                continue;
            }
            if (!powered) continue;

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

            Steer(ref item.Velocity, delta, distance, relative, state.Orientation, spec.Acceleration, speed);
        }
    }

    /// <summary>
    /// One tick of a beam's pull on a body it holds: its velocity relative to the hull at the target is steered along
    /// <paramref name="delta"/> (the way left, in the hull's frame) at <paramref name="speed"/> (what
    /// <see cref="Approach.Speed"/> gives it), changing by no more than the beam's acceleration allows in a tick.
    /// </summary>
    private static void Steer(ref Vector3 velocity, Vector3 delta, float distance, Vector3 relative, Quaternion orientation, float acceleration, float speed)
    {
        var desired = distance > 1e-6f ? HullRotation.Rotate(delta / distance, orientation) * speed : Vector3.Zero;
        var change = desired - relative;
        var length = change.Length();
        if (length > 0) velocity += change * (Math.Min(length, acceleration * HullSim.Dt) / length);
    }

    /// <summary>
    /// Which of hull <paramref name="h"/>'s tractor beams could take <paramref name="item"/> from drones at the hand-over
    /// point <paramref name="handover"/> metres out in front of it: its block is there, the mass is within its limit,
    /// the point is in its range and line of sight, and a rack it sees has room after everything its beams already
    /// hold. With <paramref name="free"/>, only a beam holding nothing now. The mount and the rack it would load, or -1
    /// with what stopped it.
    /// </summary>
    public int Receiver(FreeContainers containers, GameContent content, HullField field, int h, in FreeContainer item,
        float handover, bool free, Span<byte> occupied, out FetchRefusal why, out int rack, OreChunks? chunks = null)
    {
        rack = -1;
        var hulls = field.Hulls; var info = hulls.Info(h); var design = field.Designs[info.Design];
        var id = hulls.HandleAt(h).Packed; var mass = Mass(content.Cargo, item);
        why = FetchRefusal.NoTractor;
        var count = 0;
        foreach (var other in containers.Items)
            if (other.Id != item.Id && other.TractorHull == id)
            {
                if (count == _reserved.Length) { why = FetchRefusal.NoRoom; return -1; }
                _reserved[count++] = Held(other);
            }
        foreach (var m in design.Tractors)
        {
            var mount = design.Mounts[m];
            if (!BlockSet.Has(hulls.Blocks(h), mount.Parent) || mount.Type.Tractor is not { } spec) continue;
            if (mass > spec.MaxMass) { if (why == FetchRefusal.NoTractor) why = FetchRefusal.TooHeavy; continue; }
            if (!TractorReach.Container(design.Boxes, hulls.Blocks(h), design.BlockSize, mount, DroneFetch.HandoverGrid(design, m, handover))) continue;
            var chosen = TractorReach.ChooseRack(content.Cargo, design.Boxes, design.Mounts, hulls.Blocks(h), design.BlockSize, m,
                hulls.BlockStates.Cargo(info.BlockState), _reserved.AsSpan(0, count), item.Size, occupied);
            if (chosen < 0) { why = FetchRefusal.NoRoom; continue; }
            if (free)
            {
                var busy = false;
                foreach (var other in containers.Items)
                    if (other.Id != item.Id && other.TractorHull == id && other.TractorMount == m) { busy = true; break; }
                if (chunks is not null)
                    foreach (ref readonly var other in chunks.Items)
                        if (other.TractorHull == id && other.TractorMount == m) { busy = true; break; }
                if (busy) { why = FetchRefusal.Busy; continue; }
            }
            why = FetchRefusal.None;
            rack = chosen;
            return m;
        }
        return -1;
    }

    /// <summary>
    /// Whether one of hull <paramref name="h"/>'s live beams could take <paramref name="item"/> by itself where it is: the
    /// mass is within its limit and it is in range and line of sight. Claims and the pickup delay are not asked here;
    /// auto pickup uses it to leave alone what the tractor will fetch without drones.
    /// </summary>
    public static bool Reaches(GameContent content, HullField field, int h, in FreeContainer item)
    {
        var hulls = field.Hulls; var design = field.Designs[hulls.Info(h).Design];
        var mass = Mass(content.Cargo, item);
        foreach (var m in design.Tractors)
        {
            var mount = design.Mounts[m];
            if (!BlockSet.Has(hulls.Blocks(h), mount.Parent) || mount.Type.Tractor is not { } spec || mass > spec.MaxMass) continue;
            var emitter = hulls.State(h).Position + HullRotation.Rotate(design.MountOffsets[m], hulls.State(h).Orientation);
            var reach = spec.Range + design.BlockSize;
            if (item.Position.RelativeTo(emitter).LengthSquared() > reach * reach) continue;
            if (TractorReach.Container(design.Boxes, hulls.Blocks(h), design.BlockSize, mount, Local(design, hulls.State(h), item.Position))) return true;
        }
        return false;
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
