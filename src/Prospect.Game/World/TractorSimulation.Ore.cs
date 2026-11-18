using System.Numerics;

namespace Prospect.Game.World;

// Ore chunks in a beam. A chunk is taken like a loose container, by the same reach, power and stall rules, but it goes
// to an ore hopper's intake instead of a rack: to a point outside the intake face, then straight in, where its
// kilograms go into the hull's bins. A beam takes one only when it sees a hopper and the bins have room for all of it,
// so a full ship stops pulling. Takes no memory.
public sealed partial class TractorSimulation
{
    // Claims that no longer hold are let go before anything new is acquired, as the containers' are.
    private static void ValidateChunks(OreChunks chunks, HullField field)
    {
        var hulls = field.Hulls;
        for (var c = 0; c < chunks.Items.Length; c++)
        {
            ref var item = ref chunks.At(c);
            item.TractorPowered = false;
            if (item.TractorHull == 0) continue;
            var handle = Handle(item.TractorHull);
            if (!hulls.IsAlive(handle) || !Eligible(hulls, handle.Index)
                || !ValidChunk(chunks, field, handle.Index, item.TractorMount, ref item, needSight: false)) OreChunks.ClearTractor(ref item);
        }
    }

    // What each held chunk asks of its hull's power this tick.
    private static void DemandChunks(OreChunks chunks, HullField field)
    {
        var hulls = field.Hulls;
        foreach (ref readonly var item in chunks.Items)
        {
            if (item.TractorHull == 0) continue;
            var h = Handle(item.TractorHull).Index;
            var spec = field.Designs[hulls.Info(h).Design].Mounts[item.TractorMount].Type.Tractor!;
            hulls.Info(h).TractorDemand += spec.Power(item.Mass);
        }
    }

    // The first chunk in the pool's order the mount may take: nobody has it, it is in range and sight, it can still be
    // stopped before it reaches the emitter, and it fits.
    private static void AcquireChunk(OreChunks chunks, GameContent content, HullField field, int h, int mount, Double3 emitter, float reach, long tick)
    {
        var id = field.Hulls.HandleAt(h).Packed;
        var approach = content.Cargo.Tractor.ApproachBrakeShare;
        var hullVelocity = field.Hulls.State(h).Velocity;
        for (var c = 0; c < chunks.Items.Length; c++)
        {
            ref var item = ref chunks.At(c);
            if (item.TractorHull != 0 || item.FetchHull != 0 || tick < item.RetryTick
                || item.Position.RelativeTo(emitter).LengthSquared() > reach * reach) continue;
            if (!StopsBefore(content, field, h, mount, item.Position, item.Velocity - hullVelocity, item.Mass, approach)) continue;
            if (!ValidChunk(chunks, field, h, mount, ref item, needSight: true)) continue;
            item.TractorHull = id; item.TractorMount = mount; item.TractorStage = 0; item.TractorBest = 0; item.TractorIdle = 0;
            return;
        }
    }

    /// <summary>
    /// Whether a beam of mount <paramref name="mount"/> can still bring a body of <paramref name="mass"/> kilograms,
    /// closing at <paramref name="relative"/>, to rest before it reaches the emitter: the room is what is left between
    /// the two, less the body's own half edge. A beam that took a body it could not stop dragged it out of its reach
    /// and lost it; one that leaves it alone lets the drones bring it in (<see cref="Approach.StopsIn"/>).
    /// </summary>
    private static bool StopsBefore(GameContent content, HullField field, int h, int mount, Double3 position, Vector3 relative, double mass, float approach)
    {
        var hulls = field.Hulls; var design = field.Designs[hulls.Info(h).Design];
        if ((uint)mount >= (uint)design.Mounts.Length || design.Mounts[mount].Type.Tractor is not { } spec) return false;
        var emitter = hulls.State(h).Position + HullRotation.Rotate(design.MountOffsets[mount], hulls.State(h).Orientation);
        var room = (float)position.RelativeTo(emitter).Length() - OreChunks.Half(content.Mining, (float)mass);
        return Approach.StopsIn(spec.Acceleration, relative.Length(), room, approach);
    }

    /// <summary>
    /// Whether the mount may hold the chunk and has somewhere to put it: the mass is within the beam's limit, it sees
    /// a hopper's intake, and the hull's bins have room for the whole chunk after every other chunk its beams hold.
    /// A new claim also needs the beam's line of sight to the chunk.
    /// </summary>
    private static bool ValidChunk(OreChunks chunks, HullField field, int h, int mount, ref OreChunk item, bool needSight)
    {
        var hulls = field.Hulls; var info = hulls.Info(h); var design = field.Designs[info.Design];
        if ((uint)mount >= (uint)design.Mounts.Length || design.Mounts[mount].Type.Tractor is not { } spec || item.Mass > spec.MaxMass) return false;
        var alive = hulls.Blocks(h);
        if (needSight && !TractorReach.Container(design.Boxes, alive, design.BlockSize, design.Mounts[mount],
            Local(design, hulls.State(h), item.Position))) return false;
        var hopper = TractorReach.ChooseHopper(design.Boxes, design.Mounts, alive, design.BlockSize, mount, design.Hoppers);
        if (hopper < 0 || !HasRoom(chunks, hulls, h, item)) return false;
        if (item.TractorHopper != hopper) item.TractorStage = 0;
        item.TractorHopper = hopper;
        return true;
    }

    // Room for the whole chunk, after every other chunk the hull's beams already hold.
    private static bool HasRoom(OreChunks chunks, HullStore hulls, int h, in OreChunk item)
    {
        var id = hulls.HandleAt(h).Packed;
        var reserved = 0.0;
        foreach (ref readonly var other in chunks.Items)
            if (other.Id != item.Id && other.TractorHull == id) reserved += other.Mass;
        return hulls.BlockStates.OreRoom(hulls.Info(h).BlockState, item.Resource) - reserved >= item.Mass;
    }

    /// <summary>
    /// The chunks' pull, by the containers' rules: only power the flight step already billed, the hull's new pose
    /// against where the chunk will be at the end of the tick, a stall lets it go for
    /// <see cref="TractorGuidance.RetrySeconds"/>. A chunk whose centre reaches the point inside the intake is taken by
    /// the hopper: its kilograms go into the bins (<see cref="BlockStateStore.AddOre"/>) and the chunk is gone. A
    /// claim is checked for room every tick, so the bins take the whole chunk; should they ever take less, what is
    /// left stays a smaller chunk, let go where it is, and no ore is lost.
    /// </summary>
    private void PullChunks(OreChunks chunks, GameContent content, HullField field, long tick)
    {
        var hulls = field.Hulls; var tuning = content.Cargo.Tractor;
        var retry = (long)Math.Ceiling(tuning.RetrySeconds * Prospect.Engine.Time.TickLoop.SimulationHz);
        for (var c = chunks.Items.Length - 1; c >= 0; c--)
        {
            ref var item = ref chunks.At(c);
            if (item.TractorHull == 0) continue;
            var handle = Handle(item.TractorHull);
            if (!hulls.IsAlive(handle) || !Eligible(hulls, handle.Index)) { OreChunks.ClearTractor(ref item); continue; }
            var h = handle.Index; ref var info = ref hulls.Info(h);
            var design = field.Designs[info.Design]; var state = hulls.State(h);
            var alive = hulls.Blocks(h);
            var half = OreChunks.Half(content.Mining, item.Mass);
            if (!ValidChunk(chunks, field, h, item.TractorMount, ref item, needSight: false)
                || !TractorPath.TryGuideIntake(design, alive, item.TractorMount, item.TractorHopper, half, tuning.StagingMarginMetres, out var guide))
            { OreChunks.ClearTractor(ref item); continue; }
            var mount = design.Mounts[item.TractorMount]; var spec = mount.Type.Tractor!;
            var final = item.TractorStage != 0;
            var local = Local(design, state, item.Position + item.Velocity * HullSim.Dt);
            var sight = final ? TractorReach.InRange(design.Boxes, alive, design.BlockSize, mount, local, guide.Slack)
                : TractorReach.Container(design.Boxes, alive, design.BlockSize, mount, local, guide.Slack);
            var viaExit = !final && !TractorReach.Clear(design.Boxes, alive, design.BlockSize, local, guide.Staging);
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
                item.RetryTick = tick + retry;
                OreChunks.ClearTractor(ref item);
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
            if (distance <= tolerance && !viaExit)
            {
                if (!final) { item.TractorStage = 1; item.Velocity = carrierVelocity; continue; }
                var taken = hulls.BlockStates.AddOre(info.BlockState, item.Resource, item.Mass, item.Grade);
                if (taken > 0) field.Refit(h);
                if (item.Mass - taken <= OreBins.Crumb) { chunks.Remove(c); continue; }
                item.Mass = (float)(item.Mass - taken);
                item.RetryTick = tick + retry;
                OreChunks.ClearTractor(ref item);
                continue;
            }

            Steer(ref item.Velocity, delta, distance, relative, state.Orientation, spec.Acceleration, final ? tuning.FinalSpeed : float.PositiveInfinity, tuning.ApproachBrakeShare);
        }
    }

    /// <summary>
    /// Which of hull <paramref name="h"/>'s tractor beams could take the chunk <paramref name="item"/> from drones at
    /// the hand-over point <paramref name="handover"/> metres out in front of it: its block is there, the mass is
    /// within its limit, the point is in its range and line of sight, it sees a hopper's intake and the bins have room
    /// for the whole chunk. With <paramref name="free"/>, only a beam holding nothing now. The mount, or -1 with what
    /// stopped it.
    /// </summary>
    public int OreReceiver(FreeContainers containers, OreChunks chunks, HullField field, int h, in OreChunk item,
        float handover, bool free, out FetchRefusal why)
    {
        var hulls = field.Hulls; var design = field.Designs[hulls.Info(h).Design];
        var alive = hulls.Blocks(h); var id = hulls.HandleAt(h).Packed;
        why = FetchRefusal.NoTractor;
        foreach (var m in design.Tractors)
        {
            var mount = design.Mounts[m];
            if (!BlockSet.Has(alive, mount.Parent) || mount.Type.Tractor is not { } spec) continue;
            if (item.Mass > spec.MaxMass) { if (why == FetchRefusal.NoTractor) why = FetchRefusal.TooHeavy; continue; }
            if (!TractorReach.Container(design.Boxes, alive, design.BlockSize, mount, DroneFetch.HandoverGrid(design, m, handover))
                || TractorReach.ChooseHopper(design.Boxes, design.Mounts, alive, design.BlockSize, m, design.Hoppers) < 0) continue;
            if (!HasRoom(chunks, hulls, h, item)) { why = FetchRefusal.NoRoom; continue; }
            if (free && Holds(containers, chunks, id, m)) { why = FetchRefusal.Busy; continue; }
            why = FetchRefusal.None;
            return m;
        }

        return -1;
    }

    /// <summary>
    /// Whether one of hull <paramref name="h"/>'s live beams could take the chunk by itself where it is: the mass is
    /// within its limit, it is in range and line of sight, the beam can still stop it before it reaches the emitter,
    /// and the beam sees a hopper's intake. Room in the bins is not asked here; the drones' ore pickup uses it to
    /// leave alone what the tractor will take without them.
    /// </summary>
    public static bool Reaches(GameContent content, HullField field, int h, in OreChunk item)
    {
        var hulls = field.Hulls; var design = field.Designs[hulls.Info(h).Design];
        var alive = hulls.Blocks(h);
        var approach = content.Cargo.Tractor.ApproachBrakeShare;
        foreach (var m in design.Tractors)
        {
            var mount = design.Mounts[m];
            if (!BlockSet.Has(alive, mount.Parent) || mount.Type.Tractor is not { } spec || item.Mass > spec.MaxMass) continue;
            var emitter = hulls.State(h).Position + HullRotation.Rotate(design.MountOffsets[m], hulls.State(h).Orientation);
            var reach = spec.Range + design.BlockSize;
            if (item.Position.RelativeTo(emitter).LengthSquared() > reach * reach) continue;
            if (!StopsBefore(content, field, h, m, item.Position, item.Velocity - hulls.State(h).Velocity, item.Mass, approach)) continue;
            if (TractorReach.Container(design.Boxes, alive, design.BlockSize, mount, Local(design, hulls.State(h), item.Position))
                && TractorReach.ChooseHopper(design.Boxes, design.Mounts, alive, design.BlockSize, m, design.Hoppers) >= 0) return true;
        }

        return false;
    }
}
