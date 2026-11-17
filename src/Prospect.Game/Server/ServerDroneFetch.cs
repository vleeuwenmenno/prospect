using System.Numerics;
using Prospect.Game.Protocol;
using Prospect.Game.World;

namespace Prospect.Game.Server;

/// <summary>
/// Drones fetching free containers. A fetch is a group of <see cref="OrderTask.Fetch"/> orders on one container: the
/// executor flies each drone to its place beside it (<see cref="DroneFetch.Place"/>); once the drones holding on carry
/// its mass together and the container may be picked up, their pull brings it to the hand-over point in front of one
/// of the hull's tractor beams, which takes it and stows it by its own rule. Claims are the tractor's: a fetch is
/// refused for a container someone else keeps a claim on, and the drones lift nothing before its pickup delay is over.
/// Takes no memory in a tick.
/// </summary>
public sealed partial class ServerHost
{
    /// <summary>A manual fetch: <see cref="StartFetch"/> for the connection's own player, with its answer told to the connection.</summary>
    private void FetchContainer(Connection connection, HullHandle hull, int page, uint id)
    {
        var reply = StartFetch(connection.PlayerId, hull, page, id, out var count);
        ReplyDrones(connection, reply, count);
    }

    /// <summary>
    /// The one way a fetch starts, for the key and for auto pickup alike: checks everything and, when it all holds,
    /// puts drones holding station or waiting in their bays on <see cref="OrderTask.Fetch"/> orders for the container
    /// and marks it as being fetched. Returns <see cref="DroneReply.Fetching"/> (with how many drones carry it) or why
    /// it refused; never replies itself.
    /// </summary>
    private DroneReply StartFetch(uint viewer, HullHandle hull, int page, uint id, out int carriers)
    {
        carriers = 0;
        if (_hulls.State(hull.Index).Jump != JumpPhase.None) return DroneReply.InTransit;
        var index = _containers.Find(id);
        if (index < 0 || !Covered(viewer, _containers.Items[index].Position)) return DroneReply.NotFound;
        var item = _containers.Items[index];
        if (item.TractorHull != 0 || item.FetchHull != 0) return DroneReply.Taken;
        var info = _hulls.Info(hull.Index);
        if (item.ClaimOwner != 0 && item.ClaimOwner != info.Owner) return DroneReply.Claimed;
        var rules = _content.Drones;
        var range = (double)rules.FetchRangeMetres;
        if (item.Position.RelativeTo(_hulls.State(hull.Index).Position).LengthSquared() > range * range) return DroneReply.TooFar;
        // The most the bays could ever lift: their best drones, as many as hold on to one container.
        var design = _content.Designs[info.Design];
        var alive = _hulls.Blocks(hull.Index);
        var mass = TractorSimulation.Mass(_content.Cargo, item);
        var best = 0f;
        for (var b = 0; b < design.Bays.Length; b++)
            if (BlockSet.Has(alive, design.Bays[b])) best = Math.Max(best, design.Types[design.Bays[b]].Bay!.CarryKg);
        var most = Math.Min(rules.MaxCarriers, rules.MaxPerPlayer);
        if (best * most < mass) return DroneReply.TooHeavy;
        var mount = _tractors.Receiver(_containers, _content, _field, hull.Index, item, rules.HandoverMetres, free: false, CargoScratch, out var why, out _);
        if (mount < 0)
            return why switch { FetchRefusal.NoRoom => DroneReply.NoRoom, FetchRefusal.TooHeavy => DroneReply.TooHeavy, _ => DroneReply.NoTractor };

        // Carriers: drones holding station first, then drones inside bays whose hatch is open, until they lift it.
        ref var p = ref _bayPages[page];
        var room = p.FetchPending > 0 ? 0 : DroneRoom(viewer, out _);
        var carry = 0f;
        var holders = 0;
        for (var i = 0; i < _orderCount && carry < mass && holders < rules.MaxCarriers; i++)
            if (_orders[i].Base == hull && _orders[i].Task == OrderTask.Hold) { carry += CarryOf(_orders[i]); holders++; }
        var launches = 0;
        Span<byte> taken = stackalloc byte[DroneBayRules.MaxBays];
        for (var b = 0; b < design.Bays.Length && carry < mass && holders + launches < rules.MaxCarriers && launches < room; b++)
        {
            var block = design.Bays[b];
            if (!BlockSet.Has(alive, block) || !HatchClear(hull, block)) continue;
            var spec = design.Types[block].Bay!;
            var free = p.Bays[b].Stocked - p.Bays[b].Pending;
            while (free-- > 0 && carry < mass && holders + launches < rules.MaxCarriers && launches < room)
            { carry += spec.CarryKg; taken[b]++; launches++; }
        }
        if (carry < mass) return DroneReply.TooFew;

        var half = FreeContainers.Half(_content.Cargo, item.Size);
        var radius = _content.Designs[_content.WorkDroneDesign].Flight.Radius;
        var until = _tick + rules.FetchTicks;
        var slot = 0;
        for (var i = 0; i < _orderCount && slot < holders; i++)
        {
            ref var order = ref _orders[i];
            if (order.Base != hull || order.Task != OrderTask.Hold) continue;
            order.Task = OrderTask.Fetch;
            order.Phase = 0;
            order.Subject = id;
            order.Point = DroneFetch.Place(slot++, half, radius, rules.AttachGapMetres);
            order.Until = until;
        }
        var pendingKg = 0f;
        for (var b = 0; b < design.Bays.Length; b++)
        {
            if (taken[b] == 0) continue;
            p.Bays[b].Pending += taken[b];
            if (p.Bays[b].NextLaunch < _tick) p.Bays[b].NextLaunch = _tick;
            pendingKg += taken[b] * design.Types[design.Bays[b]].Bay!.CarryKg;
        }
        p.FetchPending = (byte)launches;
        p.FetchPendingKg = pendingKg;
        p.FetchSubject = id;
        p.FetchSlot = (byte)slot;
        p.FetchUntil = until;
        if (launches > 0) p.LaunchIssuer = viewer;
        p.Revision++;
        ref var target = ref _containers.At(index);
        target.FetchHull = hull.Packed;
        target.FetchMount = mount;
        carriers = holders + launches;
        return DroneReply.Fetching;
    }

    /// <summary>A recall's phase while the drone first flies to its bay's hold point.</summary>
    internal const byte RecallFromAfar = 2;

    /// <summary>Kilograms the drone of an order carries: its bay's figure.</summary>
    private float CarryOf(in TaskOrder order)
    {
        var design = _content.Designs[_hulls.Info(order.Base.Index).Design];
        return (uint)order.Bay < (uint)design.Bays.Length ? design.Types[design.Bays[order.Bay]].Bay!.CarryKg : 0;
    }

    /// <summary>
    /// Turns an order home. A fetching drone gives up its place beside the container for its bay's hold point and
    /// flies there first (<see cref="RecallFromAfar"/>), so it comes in along the hatch's normal instead of cutting
    /// across the hull from wherever the fetch ended.
    /// </summary>
    private void ToRecall(ref TaskOrder order)
    {
        var far = false;
        if (order.Task == OrderTask.Fetch && _hulls.IsAlive(order.Base))
        {
            var design = _content.Designs[_hulls.Info(order.Base.Index).Design];
            if ((uint)order.Bay < (uint)design.Bays.Length)
            {
                order.Point = DroneBayRules.DefaultHold(design, design.Bays[order.Bay], _content.Drones.HoldDistanceMetres);
                far = true;
            }
        }
        order.Task = OrderTask.Recall;
        order.Phase = far ? RecallFromAfar : (byte)0;
        order.Subject = 0;
        order.Until = 0;
    }

    /// <summary>
    /// Flies a fetching drone to its place beside the container, aimed ahead of it by what its autopilot lags, and
    /// marks it holding on (phase 1) while it is within the tether of that place. Calls it home when the container is
    /// gone, has been let go or the fetch has taken too long.
    /// </summary>
    private bool Fetch(ref TaskOrder order, int page)
    {
        var index = _containers.Find((uint)order.Subject);
        if (index < 0 || _containers.Items[index].FetchHull != order.Base.Packed || (order.Until != 0 && _tick >= order.Until))
        {
            ToRecall(ref order);
            return Recall(ref order, page);
        }
        var item = _containers.Items[index];
        var rules = _content.Drones;
        var place = item.Position + HullRotation.Rotate(order.Point, _hulls.State(order.Base.Index).Orientation);
        var position = _hulls.State(order.Actor.Index).Position;
        order.Phase = place.RelativeTo(position).LengthSquared() <= rules.TetherMetres * rules.TetherMetres ? (byte)1 : (byte)0;
        var lead = DroneFetch.Lead(item.Velocity.Length(), _hulls.Flight(order.Actor.Index).WeakestPush);
        Fly(order.Actor, place + item.Velocity * lead, rules.TetherMetres * 0.5f);
        return true;
    }

    /// <summary>
    /// Once a tick, after the hulls step and the tractors pull, before loose cargo drifts: each container drones are
    /// fetching is pulled by the drones holding on to it toward its hand-over point, and handed to the tractor beam there.
    /// A group that can no longer lift it (a drone lost, its ship gone or jumping, the fetch timed out) lets it go.
    /// </summary>
    private void TickCarries()
    {
        var rules = _content.Drones;
        for (var c = _containers.Items.Length - 1; c >= 0; c--)
        {
            ref var item = ref _containers.At(c);
            if (item.FetchHull == 0) continue;
            var hull = new HullHandle((int)(item.FetchHull & 0xFFFF), (int)(item.FetchHull >> 16));
            var page = PageOf(hull);
            if (page < 0 || _hulls.Info(hull.Index).Wreck || _hulls.State(hull.Index).Jump != JumpPhase.None)
            {
                DropFetch(ref item, hull, DroneReply.Dropped);
                continue;
            }

            float holding = 0, total = 0;
            var attached = 0;
            for (var i = 0; i < _orderCount; i++)
            {
                ref readonly var order = ref _orders[i];
                if (order.Task != OrderTask.Fetch || order.Subject != item.Id || order.Base != hull || !_hulls.IsAlive(order.Actor)) continue;
                var carry = CarryOf(order);
                total += carry;
                if (order.Phase == 1) { holding += carry; attached++; }
            }
            ref var p = ref _bayPages[page];
            if (p.FetchPending > 0 && p.FetchSubject == item.Id) total += p.FetchPendingKg;
            var mass = TractorSimulation.Mass(_content.Cargo, item);
            if (total < mass) { DropFetch(ref item, hull, DroneReply.Dropped); continue; }
            var info = _hulls.Info(hull.Index);
            if (holding < mass || !item.MayPickup(info.Owner, _tick)) continue;

            var design = _content.Designs[info.Design];
            var state = _hulls.State(hull.Index);
            var local = DroneFetch.Handover(design, item.FetchMount, rules.HandoverMetres);
            var handover = WorldPoint(hull, local);
            var carrier = state.Velocity + HullRotation.Rotate(Vector3.Cross(state.AngularVelocity, local - _hulls.Flight(hull.Index).CenterOfMass), state.Orientation);
            // As the tractor does: the container drifts only after this, so measure where it will be at the tick's end.
            var delta = handover.RelativeTo(item.Position + item.Velocity * HullSim.Dt);
            var relative = (item.Velocity - carrier).Length();
            item.Orientation = Quaternion.Normalize(Quaternion.Slerp(item.Orientation, state.Orientation,
                Math.Min(1f, _content.Cargo.Tractor.AlignRate * HullSim.Dt)));
            item.AngularVelocity = state.AngularVelocity;
            if (delta.Length() <= rules.HandoverToleranceMetres && relative <= rules.HandoverSpeed)
            {
                var mount = _tractors.Receiver(_containers, _content, _field, hull.Index, item, rules.HandoverMetres, free: true, CargoScratch, out var why, out var rack);
                if (mount >= 0)
                {
                    FreeContainers.ClearFetch(ref item);
                    item.TractorHull = hull.Packed;
                    item.TractorMount = mount;
                    item.TractorRack = rack;
                    item.TractorStage = 0; item.TractorBest = 0; item.TractorIdle = 0;
                    item.Velocity = carrier;
                    EndFetch(item.Id, hull);
                    ReplyDrones(info.Owner, DroneReply.Delivered, attached);
                    continue;
                }
                // A beam busy with another container: the drones hold this one where it is until the beam is free.
                if (why != FetchRefusal.Busy)
                {
                    DropFetch(ref item, hull, why switch { FetchRefusal.NoRoom => DroneReply.NoRoom, FetchRefusal.TooHeavy => DroneReply.TooHeavy, _ => DroneReply.NoTractor });
                    continue;
                }
            }
            var speed = Math.Min(rules.CarrySpeed, 0.9f * _content.Designs[_content.WorkDroneDesign].Flight.MaxSpeed);
            DroneFetch.Pull(ref item, delta, carrier, rules.CarryAcceleration * holding / (float)mass, speed);
        }
    }

    /// <summary>Lets a fetched container go where it is and calls its drones home, telling the owner why.</summary>
    private void DropFetch(ref FreeContainer item, HullHandle hull, DroneReply reply)
    {
        var id = item.Id;
        FreeContainers.ClearFetch(ref item);
        EndFetch(id, hull);
        if (_hulls.IsAlive(hull)) ReplyDrones(_hulls.Info(hull.Index).Owner, reply, 0);
    }

    /// <summary>Calls home every drone of <paramref name="hull"/> fetching container <paramref name="id"/>, and stops launches for it.</summary>
    private void EndFetch(uint id, HullHandle hull)
    {
        for (var i = 0; i < _orderCount; i++)
            if (_orders[i].Task == OrderTask.Fetch && _orders[i].Subject == id && _orders[i].Base == hull) ToRecall(ref _orders[i]);
        var page = PageOf(hull);
        if (page < 0) return;
        ref var p = ref _bayPages[page];
        if (p.FetchPending > 0 && p.FetchSubject == id)
        {
            var design = _content.Designs[_hulls.Info(hull.Index).Design];
            for (var b = 0; b < design.Bays.Length && p.FetchPending > 0; b++)
            {
                var drop = Math.Min(p.Bays[b].Pending, p.FetchPending);
                p.Bays[b].Pending -= drop;
                p.FetchPending -= drop;
            }
            p.FetchPending = 0;
            p.FetchPendingKg = 0;
        }
        p.Revision++;
    }

    /// <summary>Drones of a hull out on a fetch or waiting to launch for one.</summary>
    private int FetchingDrones(HullHandle hull, int page)
    {
        var count = page >= 0 ? _bayPages[page].FetchPending : 0;
        for (var i = 0; i < _orderCount; i++)
            if (_orders[i].Task == OrderTask.Fetch && _orders[i].Base == hull) count++;
        return count;
    }
}
