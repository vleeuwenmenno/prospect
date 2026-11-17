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
        var mount = _tractors.Receiver(_containers, _content, _field, hull.Index, item, rules.HandoverMetres, free: false, CargoScratch, out var why, out _, _chunks);
        if (mount < 0)
            return why switch { FetchRefusal.NoRoom => DroneReply.NoRoom, FetchRefusal.TooHeavy => DroneReply.TooHeavy, _ => DroneReply.NoTractor };

        var reply = AssignCarriers(viewer, hull, page, id, mass, FreeContainers.Half(_content.Cargo, item.Size), OrderTask.Fetch, out carriers);
        if (reply != DroneReply.Fetching) return reply;
        ref var target = ref _containers.At(index);
        target.FetchHull = hull.Packed;
        target.FetchMount = mount;
        return DroneReply.Fetching;
    }

    /// <summary>
    /// Puts enough of a hull's drones on <paramref name="task"/> orders (a fetch of a container or of an ore chunk) to
    /// lift <paramref name="mass"/> kilograms: drones holding station first, then drones inside bays whose hatch is
    /// open. <see cref="DroneReply.Fetching"/> with how many carry it, or <see cref="DroneReply.TooFew"/> with nothing
    /// changed.
    /// </summary>
    private DroneReply AssignCarriers(uint viewer, HullHandle hull, int page, uint id, double mass, Vector3 half, OrderTask task, out int carriers)
    {
        carriers = 0;
        var rules = _content.Drones;
        var info = _hulls.Info(hull.Index);
        var design = _content.Designs[info.Design];
        var alive = _hulls.Blocks(hull.Index);
        ref var p = ref _bayPages[page];
        var room = p.FetchPending > 0 || p.Scan.Pending > 0 ? 0 : DroneRoom(viewer, out _);
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

        var radius = _content.Designs[_content.WorkDroneDesign].Flight.Radius;
        var until = _tick + rules.FetchTicks;
        var slot = 0;
        for (var i = 0; i < _orderCount && slot < holders; i++)
        {
            ref var order = ref _orders[i];
            if (order.Base != hull || order.Task != OrderTask.Hold) continue;
            order.Task = task;
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
        p.FetchOre = task == OrderTask.FetchOre;
        p.FetchSlot = (byte)slot;
        p.FetchUntil = until;
        if (launches > 0) p.LaunchIssuer = viewer;
        p.Revision++;
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
    /// Turns an order home. Every drone first flies to its own place beside its bay's hold point
    /// (<see cref="RecallFromAfar"/>), going round the hull, so it comes in along the hatch's normal instead of cutting
    /// across the hull from wherever its task ended, and drones coming home together wait their turn apart.
    /// </summary>
    private void ToRecall(ref TaskOrder order)
    {
        // A scan drone may be on the far side of the rock: it first flies around it to the ship's side (ScanOrder), and
        // only then takes the recall, from its bay's hold point as a fetching drone does.
        if (IsScan(order.Task) && order.Phase != ScanOrderPhase.ScanLeaving) { order.Phase = ScanOrderPhase.ScanLeaving; return; }
        var far = false;
        var page = PageOf(order.Base);
        if (page >= 0 && _hulls.IsAlive(order.Base))
        {
            var design = _content.Designs[_hulls.Info(order.Base.Index).Design];
            if ((uint)order.Bay < (uint)design.Bays.Length)
            {
                order.Point = HomeSlot(design, order.Bay, ref _bayPages[page].Bays[order.Bay]);
                far = true;
            }
        }
        order.Task = OrderTask.Recall;
        order.Phase = far ? RecallFromAfar : (byte)0;
        order.Subject = 0;
        order.Until = 0;
    }

    /// <summary>
    /// Flies a fetching drone to its place beside the container, closing on it as seen from the container, and
    /// marks it holding on (phase 1) while it is within the tether of that place. Calls it home when the container is
    /// gone, has been let go or the fetch has taken too long.
    /// </summary>
    private bool Fetch(ref TaskOrder order, int page)
    {
        var index = _containers.Find((uint)order.Subject);
        var held = index >= 0 && _containers.Items[index].FetchHull == order.Base.Packed;
        return FetchBody(ref order, page, held, held ? _containers.Items[index].Position : default, held ? _containers.Items[index].Velocity : default,
            held ? FreeContainers.Half(_content.Cargo, _containers.Items[index].Size).Length() : 0);
    }

    /// <summary>
    /// The one flight of a fetching drone, whatever it fetches: to its place beside the body at
    /// <paramref name="bodyPosition"/> (reaching <paramref name="bodyRadius"/> from its centre), closing on it as seen from
    /// the body (<see cref="Steer"/>), phase 1 while within the tether.
    /// Home when the body is no longer <paramref name="held"/> for its hull or the fetch has taken too long.
    /// </summary>
    private bool FetchBody(ref TaskOrder order, int page, bool held, Double3 bodyPosition, Vector3 bodyVelocity, float bodyRadius)
    {
        if (!held || (order.Until != 0 && _tick >= order.Until))
        {
            ToRecall(ref order);
            return Recall(ref order, page);
        }
        var rules = _content.Drones;
        var place = bodyPosition + HullRotation.Rotate(order.Point, _hulls.State(order.Base.Index).Orientation);
        var position = _hulls.State(order.Actor.Index).Position;
        order.Phase = place.RelativeTo(position).LengthSquared() <= rules.TetherMetres * rules.TetherMetres ? (byte)1 : (byte)0;
        // Seen from the body, round it to a place on its far side and clear of the other drones; on its way, round its
        // own hull too (holding on, it stays beside the body, which keeps clear of the hull itself).
        Steer(order, place, bodyVelocity, rules.TetherMetres * 0.25f, group: true, hull: order.Phase == 0, bodyPosition, bodyRadius);
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

            CarryGroup(hull, page, item.Id, OrderTask.Fetch, out var holding, out var total, out var attached, out var push);
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
            var next = item.Position + item.Velocity * HullSim.Dt;
            var delta = handover.RelativeTo(next);
            var relative = (item.Velocity - carrier).Length();
            item.Orientation = Quaternion.Normalize(Quaternion.Slerp(item.Orientation, state.Orientation,
                Math.Min(1f, _content.Cargo.Tractor.AlignRate * HullSim.Dt)));
            item.AngularVelocity = state.AngularVelocity;
            if (delta.Length() <= rules.HandoverToleranceMetres && relative <= rules.HandoverSpeed)
            {
                var mount = _tractors.Receiver(_containers, _content, _field, hull.Index, item, rules.HandoverMetres, free: true, CargoScratch, out var why, out var rack, _chunks);
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
            var flight = _hulls.Flight(hull.Index);
            var goal = DroneFetch.CarryGoal(next, FreeContainers.Half(_content.Cargo, item.Size), handover,
                WorldPoint(hull, flight.CenterOfMass), flight.Radius, rules.ClearanceMetres);
            DroneFetch.Pull(ref item, goal.RelativeTo(next), carrier,
                DroneFetch.CarryAcceleration(rules.CarryAcceleration, holding, mass, rules.CarryFollowShare, push), speed);
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

    /// <summary>
    /// What the drones of <paramref name="hull"/> on <paramref name="task"/> for body <paramref name="id"/> carry: the
    /// kilograms of those holding on (phase 1), how many they are and the weakest push among them, and the kilograms of
    /// all of them, drones still waiting to launch for it included.
    /// </summary>
    private void CarryGroup(HullHandle hull, int page, uint id, OrderTask task, out float holding, out float total, out int attached, out float push)
    {
        holding = total = 0;
        attached = 0;
        push = float.MaxValue;
        for (var i = 0; i < _orderCount; i++)
        {
            ref readonly var order = ref _orders[i];
            if (order.Task != task || order.Subject != id || order.Base != hull || !_hulls.IsAlive(order.Actor)) continue;
            var carry = CarryOf(order);
            total += carry;
            if (order.Phase == 1) { holding += carry; attached++; push = Math.Min(push, _hulls.Flight(order.Actor.Index).WeakestPush); }
        }
        if (attached == 0) push = 0;
        ref var p = ref _bayPages[page];
        if (p.FetchPending > 0 && p.FetchSubject == id && p.FetchOre == (task == OrderTask.FetchOre)) total += p.FetchPendingKg;
    }

    /// <summary>
    /// Calls home every drone of <paramref name="hull"/> fetching the container <paramref name="id"/> (the ore chunk,
    /// with <paramref name="ore"/>), and stops launches for it.
    /// </summary>
    private void EndFetch(uint id, HullHandle hull, bool ore = false)
    {
        var task = ore ? OrderTask.FetchOre : OrderTask.Fetch;
        for (var i = 0; i < _orderCount; i++)
            if (_orders[i].Task == task && _orders[i].Subject == id && _orders[i].Base == hull) ToRecall(ref _orders[i]);
        var page = PageOf(hull);
        if (page < 0) return;
        ref var p = ref _bayPages[page];
        if (p.FetchPending > 0 && p.FetchSubject == id && p.FetchOre == ore)
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
            if (_orders[i].Task is OrderTask.Fetch or OrderTask.FetchOre && _orders[i].Base == hull) count++;
        return count;
    }
}
