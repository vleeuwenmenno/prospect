using System.Numerics;

namespace Prospect.Game.World;

/// <summary>Authoritative automatic pickup. The fixed free-body pool also holds exclusive transient claims.</summary>
public sealed class TractorSimulation
{
    private int _acquireHull, _acquireMount;
    private readonly HoldContainer[] _reserved = new HoldContainer[BlockStateStore.MaxContainers];

    /// <summary>Validates existing claims first, then lets free mounts acquire in hull/mount/body order.</summary>
    public void Plan(FreeContainers containers, GameContent content, HullField field, Span<byte> occupied)
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
                || !Valid(containers, content, field, handle.Index, item.TractorMount, ref item, occupied)) Release(ref item);
        }
        Acquire(containers, content, field, occupied);
        foreach (var item in containers.Items)
            if (item.TractorHull != 0)
            {
                var h = Handle(item.TractorHull).Index;
                var spec = field.Designs[hulls.Info(h).Design].Mounts[item.TractorMount].Type.Tractor!;
                hulls.Info(h).TractorDemand += spec.Power(Mass(content.Cargo, item));
            }
    }

    private void Acquire(FreeContainers containers, GameContent content, HullField field, Span<byte> occupied)
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
                    if (item.TractorHull != 0 || item.Position.RelativeTo(emitter).LengthSquared() > (spec.Range + design.BlockSize) * (spec.Range + design.BlockSize)) continue;
                    if (!Valid(containers, content, field, h, m, ref item, occupied)) continue;
                    item.TractorHull = id; item.TractorMount = m; item.TractorStage = 0;
                    break;
                }
            }
        }
        _acquireHull = _acquireMount = 0;
    }

    private bool Valid(FreeContainers containers, GameContent content, HullField field, int h, int mount,
        ref FreeContainer item, Span<byte> occupied)
    {
        var hulls = field.Hulls; var info = hulls.Info(h); var design = field.Designs[info.Design];
        if ((uint)mount >= (uint)design.Mounts.Length || design.Mounts[mount].Type.Tractor is not { } spec
            || Mass(content.Cargo, item) > spec.MaxMass) return false;
        var local = Local(design, hulls.State(h), item.Position);
        if (!TractorReach.Container(design.Boxes, hulls.Blocks(h), design.BlockSize, design.Mounts[mount], local)) return false;
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

    /// <summary>Uses only power already billed by FlightModel. Bodies remain free until the rack accepts them.</summary>
    public void Pull(FreeContainers containers, GameContent content, HullField field, Span<byte> occupied)
    {
        var hulls = field.Hulls;
        for (var c = containers.Items.Length - 1; c >= 0; c--)
        {
            ref var item = ref containers.At(c);
            if (item.TractorHull == 0) continue;
            var handle = Handle(item.TractorHull);
            if (!hulls.IsAlive(handle) || !Eligible(hulls, handle.Index)) { Release(ref item); continue; }
            var h = handle.Index; ref var info = ref hulls.Info(h);
            if (info.TractorDemand <= 0 || info.Power.Tractor < info.TractorDemand) continue;
            var design = field.Designs[info.Design]; var state = hulls.State(h);
            if (!Valid(containers, content, field, h, item.TractorMount, ref item, occupied)) { Release(ref item); continue; }
            var mount = design.Mounts[item.TractorMount]; var spec = mount.Type.Tractor!;
            var emitter = TractorReach.Emitter(design.Boxes, mount, design.BlockSize);
            var clearance = FreeContainers.Half(content.Cargo, item.Size).Length() + content.Cargo.UnitSize * .1f;
            var target = emitter + Facings.Vector(mount.Face) * clearance;
            if (item.TractorStage != 0)
            {
                TractorReach.Rack(design.Boxes, design.Mounts, hulls.Blocks(h), design.BlockSize,
                    item.TractorMount, item.TractorRack, out target, out var side);
                target += Facings.Vector(side) * clearance;
            }
            var local = Local(design, state, item.Position);
            var delta = target - local;
            var distance = delta.Length();
            var offset = target - Pivot(design);
            var carrierVelocity = state.Velocity + HullRotation.Rotate(Vector3.Cross(state.AngularVelocity,
                offset - hulls.Flight(h).CenterOfMass), state.Orientation);
            var relative = item.Velocity - carrierVelocity;
            var tolerance = Math.Max(.02f, relative.Length() * HullSim.Dt);
            item.TractorPowered = true;
            if (distance <= tolerance)
            {
                if (item.TractorStage == 0) { item.TractorStage = 1; item.Velocity = carrierVelocity; }
                else if (hulls.BlockStates.TryAddCargo(info.BlockState, content, Held(item), occupied))
                { containers.Remove(c); field.Refit(h); }
                continue;
            }
            var desired = HullRotation.Rotate(delta / distance, state.Orientation) * MathF.Sqrt(2 * spec.Acceleration * distance);
            var change = desired - relative;
            var length = change.Length();
            if (length > 0) item.Velocity += change * (Math.Min(length, spec.Acceleration * HullSim.Dt) / length);
            item.Orientation = state.Orientation;
            item.AngularVelocity = state.AngularVelocity;
        }
    }

    /// <summary>Payload and shell mass in kilograms.</summary>
    public static double Mass(CargoCatalog cargo, in FreeContainer item) => item.Amount + cargo.SpaceShellKg * cargo.Sizes[item.Size].Units;
    private static bool Eligible(HullStore hulls, int h) => !hulls.Info(h).Wreck && hulls.State(h).Jump == JumpPhase.None;
    private static HullHandle Handle(uint id) => new((int)(id & 65535), (int)(id >> 16));
    private static HoldContainer Held(in FreeContainer item) => new(item.TractorRack, item.Size, item.Resource, item.Amount, item.Hull, default);
    private static Vector3 Pivot(HullDesign design) => (design.CenterOfMass + new Vector3(.5f)) * design.BlockSize;
    private static Vector3 Local(HullDesign design, in HullState state, Double3 point) =>
        HullRotation.Rotate(point.RelativeTo(state.Position), Quaternion.Conjugate(state.Orientation)) + Pivot(design);
    private static void Release(ref FreeContainer item)
    { item.TractorHull = 0; item.TractorMount = 0; item.TractorRack = 0; item.TractorStage = 0; item.TractorPowered = false; }
}
