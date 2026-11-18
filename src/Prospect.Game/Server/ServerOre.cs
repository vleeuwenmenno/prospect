using System.Numerics;
using Prospect.Engine.Diagnostics;
using Prospect.Engine.Net;
using Prospect.Game.Protocol;
using Prospect.Game.World;

namespace Prospect.Game.Server;

/// <summary>
/// Ore collection (R4): the hull's bins as their pilot is told of them, what an edit keeps of them, and drones that
/// bring loose ore chunks to the hull's tractor beam. The beam's own pickup is <see cref="TractorSimulation"/>'s; a
/// drone fetch of a chunk is the container fetch with another body (<see cref="OrderTask.FetchOre"/>): the same
/// carriers, places, pull and hand-over point, and the beam leads the chunk into a hopper by its own rule. Takes no
/// memory in a tick.
/// </summary>
public sealed partial class ServerHost
{
    /// <summary>Sends each pilot what their flown hull's bins hold, when it changes or the flown hull does.</summary>
    private void SendOreBins()
    {
        foreach (var c in _connections)
        {
            if (c.State != ConnectionState.Active || !c.CatalogueReady || !_hulls.IsAlive(c.Hull)) continue;
            var info = _hulls.Info(c.Hull.Index);
            if (!BlockSet.Has(c.ReadyDesigns, info.Design)) continue;
            var revision = _hulls.BlockStates.OreRevision(info.BlockState);
            if (c.OreHull == c.Hull.Packed && c.OreRevision == revision) continue;
            var writer = new NetWriter(_sendBuffer);
            OreBinsMessage.Write(ref writer, c.Hull.Packed, info.Design, _tick, _hulls.BlockStates.Bins(info.BlockState));
            if (c.Transport.Send(c.Peer, NetChannel.Reliable, writer.Written))
            { c.OreHull = c.Hull.Packed; c.OreRevision = revision; }
        }
    }

    /// <summary>
    /// Gives a hull that replaces another (an edit or a repair) the ore the old one held where its bins survive: by
    /// overlap in the shared grid first, the rest into other bins (<see cref="OreBins.Transfer"/>). What finds no room
    /// is dumped and logged. Call it before the new hull's figures are read.
    /// </summary>
    private void CarryOre(BlockStateHandle oldState, HullDesign from, BlockStateHandle newState, HullDesign to)
    {
        if (from.Bins.Length == 0) return;
        Span<OreBin> kept = stackalloc OreBin[OreBins.MaxBins];
        kept = kept[..to.Bins.Length];
        var dumped = OreBins.Transfer(from, _hulls.BlockStates.Bins(oldState), to, kept);
        if (!_hulls.BlockStates.TrySetOre(newState, kept, Galaxy.Resources.Length))
            Logs.Sim.Warning("An edit's ore did not fit the new hull's bins and was dropped.");
        else if (dumped > OreBins.Crumb)
            Logs.Sim.Info($"An edit dumped {dumped:0} kg of ore that its bins had no room for.");
    }

    /// <summary>
    /// The one way drones are sent for an ore chunk: checks everything and, when it all holds, puts drones on
    /// <see cref="OrderTask.FetchOre"/> orders for it and marks it as being fetched. Returns
    /// <see cref="DroneReply.Fetching"/> (with how many carry it) or why it refused; never replies itself.
    /// </summary>
    private DroneReply StartOreFetch(uint viewer, HullHandle hull, int page, uint id, out int carriers)
    {
        carriers = 0;
        if (!_content.Drones.OreFetch) return DroneReply.NotFound;
        if (_hulls.State(hull.Index).Jump != JumpPhase.None) return DroneReply.InTransit;
        var index = _chunks.Find(id);
        if (index < 0 || !Covered(viewer, _chunks.Items[index].Position)) return DroneReply.NotFound;
        var item = _chunks.Items[index];
        if (item.TractorHull != 0 || item.FetchHull != 0) return DroneReply.Taken;
        var rules = _content.Drones;
        var range = (double)rules.FetchRangeMetres;
        if (item.Position.RelativeTo(_hulls.State(hull.Index).Position).LengthSquared() > range * range) return DroneReply.TooFar;
        var info = _hulls.Info(hull.Index);
        var design = _content.Designs[info.Design];
        var alive = _hulls.Blocks(hull.Index);
        var best = 0f;
        for (var b = 0; b < design.Bays.Length; b++)
            if (BlockSet.Has(alive, design.Bays[b])) best = Math.Max(best, design.Types[design.Bays[b]].Bay!.CarryKg);
        if (best * Math.Min(rules.MaxCarriers, rules.MaxPerPlayer) < item.Mass) return DroneReply.TooHeavy;
        var mount = _tractors.OreReceiver(_containers, _chunks, _field, hull.Index, item, rules.HandoverMetres, free: false, out var why, BusyMounts(hull));
        if (mount < 0)
            return why switch { FetchRefusal.NoRoom => DroneReply.NoRoom, FetchRefusal.TooHeavy => DroneReply.TooHeavy, _ => DroneReply.NoTractor };

        var reply = AssignCarriers(viewer, hull, page, id, item.Mass, new Vector3(OreChunks.Half(_content.Mining, item.Mass)), OrderTask.FetchOre, out carriers);
        if (reply != DroneReply.Fetching) return reply;
        ref var target = ref _chunks.At(index);
        target.FetchHull = hull.Packed;
        target.FetchMount = mount;
        return DroneReply.Fetching;
    }

    /// <summary>
    /// The beams this hull's other fetches already bring their chunks to, as a bit each: a new fetch hands its chunk to
    /// another beam when it can, so four chunks do not queue for one beam while the hull's others stand idle.
    /// </summary>
    private ulong BusyMounts(HullHandle hull)
    {
        var packed = hull.Packed;
        var mask = 0UL;
        foreach (ref readonly var chunk in _chunks.Items)
            if (chunk.FetchHull == packed && (uint)chunk.FetchMount < 64) mask |= 1UL << chunk.FetchMount;
        return mask;
    }

    /// <summary>A drone fetching an ore chunk: the container fetch's flight, beside the chunk.</summary>
    private bool FetchOre(ref TaskOrder order)
    {
        var index = _chunks.Find((uint)order.Subject);
        var held = index >= 0 && _chunks.Items[index].FetchHull == order.Base.Packed;
        // A chunk that left the pool without a word ends the fetch here, as a container that did.
        if (!held) AbandonFetch(order, ore: true);
        return FetchBody(ref order, held, held ? _chunks.Items[index].Position : default, held ? _chunks.Items[index].Velocity : default,
            held ? OreChunks.Half(_content.Mining, _chunks.Items[index].Mass) * 1.7320508f : 0);
    }

    /// <summary>
    /// Once a tick, after the hulls step and the tractors pull, before the chunks drift: each chunk drones are
    /// fetching is pulled by the drones holding on to it toward its hand-over point, and handed to the tractor beam
    /// there, which leads it into a hopper. A group that can no longer lift it lets it go.
    /// </summary>
    private void TickOreCarries()
    {
        var rules = _content.Drones;
        for (var c = _chunks.Items.Length - 1; c >= 0; c--)
        {
            ref var item = ref _chunks.At(c);
            if (item.FetchHull == 0) continue;
            var hull = new HullHandle((int)(item.FetchHull & 0xFFFF), (int)(item.FetchHull >> 16));
            var page = PageOf(hull);
            if (page < 0 || _hulls.Info(hull.Index).Wreck || _hulls.State(hull.Index).Jump != JumpPhase.None)
            {
                DropOreFetch(ref item, hull, DroneReply.Dropped);
                continue;
            }

            CarryGroup(hull, page, item.Id, OrderTask.FetchOre, out var holding, out var total, out var attached, out var push);
            if (total < item.Mass) { DropOreFetch(ref item, hull, DroneReply.Dropped); continue; }
            if (holding < item.Mass) continue;

            var info = _hulls.Info(hull.Index);
            var design = _content.Designs[info.Design];
            var state = _hulls.State(hull.Index);
            var local = DroneFetch.Handover(design, item.FetchMount, rules.HandoverMetres);
            var handover = WorldPoint(hull, local);
            var carrier = state.Velocity + HullRotation.Rotate(Vector3.Cross(state.AngularVelocity, local - _hulls.Flight(hull.Index).CenterOfMass), state.Orientation);
            // As the tractor does: the chunk drifts only after this, so measure where it will be at the tick's end.
            var next = item.Position + item.Velocity * HullSim.Dt;
            var delta = handover.RelativeTo(next);
            var relative = (item.Velocity - carrier).Length();
            if (delta.Length() <= rules.HandoverToleranceMetres && relative <= rules.HandoverSpeed)
            {
                var mount = _tractors.OreReceiver(_containers, _chunks, _field, hull.Index, item, rules.HandoverMetres, free: true, out var why);
                if (mount >= 0)
                {
                    OreChunks.ClearFetch(ref item);
                    OreChunks.ClearTractor(ref item);
                    item.TractorHull = hull.Packed;
                    item.TractorMount = mount;
                    item.Velocity = carrier;
                    EndFetch(item.Id, hull, ore: true);
                    ReplyDrones(info.Owner, DroneReply.Delivered, attached);
                    continue;
                }

                // A beam busy with something else: the drones hold the chunk where it is until the beam is free.
                if (why != FetchRefusal.Busy)
                {
                    DropOreFetch(ref item, hull, why switch { FetchRefusal.NoRoom => DroneReply.NoRoom, FetchRefusal.TooHeavy => DroneReply.TooHeavy, _ => DroneReply.NoTractor });
                    continue;
                }
            }

            var speed = Math.Min(rules.CarrySpeed, 0.9f * _content.Designs[_content.WorkDroneDesign].Flight.MaxSpeed);
            var flight = _hulls.Flight(hull.Index);
            var goal = DroneFetch.CarryGoal(next, new Vector3(OreChunks.Half(_content.Mining, item.Mass)), handover,
                WorldPoint(hull, flight.CenterOfMass), flight.Radius, rules.ClearanceMetres);
            DroneFetch.Pull(ref item.Velocity, goal.RelativeTo(next), carrier,
                DroneFetch.CarryAcceleration(rules.CarryAcceleration, holding, item.Mass, rules.CarryFollowShare, push), speed,
                _content.Cargo.Tractor.ApproachBrakeShare);
        }
    }

    /// <summary>Lets a fetched chunk go where it is and parks its drones, telling the owner why.</summary>
    private void DropOreFetch(ref OreChunk item, HullHandle hull, DroneReply reply)
    {
        var id = item.Id;
        OreChunks.ClearFetch(ref item);
        EndFetch(id, hull, ore: true);
        if (_hulls.IsAlive(hull)) ReplyDrones(_hulls.Info(hull.Index).Owner, reply, 0);
    }

    /// <summary>
    /// "Pick up ores": once a tick for each hull with bays and the setting on, looks at the next
    /// <see cref="DroneRules.AutoPickupWindow"/> chunks from a rotating start and sends the drones for each that is in
    /// range and sight, that nobody (a tractor, other drones) has, that the hull's own beam cannot take by itself, that
    /// the bins still have room for after what is already on its way, and that a fetch was not refused for lately. Every
    /// free drone goes out, one fetch a chunk, up to <see cref="DroneRules.AutoPickupMaxFetches"/> fetches. It is
    /// <see cref="StartOreFetch"/>, never a second path; a refusal is told to the owner and the chunk left alone for
    /// <see cref="DroneRules.AutoPickupRetrySeconds"/>. A hull with no hopper, no bin or no room sends nobody, so a
    /// full ship stops fetching without a word.
    /// </summary>
    private void TickAutoOre(int index)
    {
        ref var page = ref _bayPages[index];
        if (!_content.Drones.OreFetch || !page.PickUpOres || _tick < page.AutoPausedUntil) return;
        var items = _chunks.Items;
        if (items.IsEmpty) return;
        var hull = page.Hull;
        var info = _hulls.Info(hull.Index);
        if (info.Wreck || _hulls.State(hull.Index).Jump != JumpPhase.None) return;
        var design = _content.Designs[info.Design];
        if (design.Hoppers.Length == 0 || design.Bins.Length == 0) return;

        // What is left of the hull's fetches, containers and chunks counted together: a fetch that is over leaves its
        // drones holding station, which the next one takes where they are.
        var rules = _content.Drones;
        var left = rules.AutoPickupMaxFetches - FetchesOf(hull, index);
        if (left <= 0) return;

        var range = (double)rules.FetchRangeMetres;
        var origin = _hulls.State(hull.Index).Position;
        var window = Math.Min(rules.AutoPickupWindow, items.Length);
        var start = page.OreCursor % items.Length;
        var k = 0;
        for (; k < window && left > 0; k++)
        {
            var c = (start + k) % items.Length;
            ref readonly var item = ref items[c];
            if (item.TractorHull != 0 || item.FetchHull != 0 || _tick < item.RetryTick) continue;
            if (item.Position.RelativeTo(origin).LengthSquared() > range * range) continue;
            if (OreSkipped(ref page, item.Id) || !Covered(info.Owner, item.Position)) continue;
            // Room the bins can still promise: the chunks the drones are fetching and the beams are pulling in land in
            // the same bins, so they are counted against the room before one more is sent for.
            if (_hulls.BlockStates.OreRoom(info.BlockState, item.Resource) - OreOnItsWay(hull, item.Resource) < item.Mass) continue;
            if (TractorSimulation.Reaches(_content, _field, hull.Index, item)) continue;
            // One drone a chunk at least: with none free there is nothing more to start this tick.
            if (FreeCarriers(hull, index, info.Owner) == 0) break;

            page.OreCursor = (c + 1) % items.Length;
            var id = item.Id;
            var reply = StartOreFetch(info.Owner, hull, index, id, out var carriers);
            ReplyDrones(info.Owner, reply, carriers);
            if (reply == DroneReply.Fetching) { left--; continue; }
            ref var slot = ref page.OreSkip[page.OreSkipNext];
            slot.Id = id;
            slot.Until = _tick + rules.AutoPickupRetryTicks;
            page.OreSkipNext = (page.OreSkipNext + 1) % AutoSkipSlots;
        }
        if (k >= window) page.OreCursor = (start + window) % items.Length;
    }

    /// <summary>
    /// Kilograms of one ore a hull's bins can no longer promise: what its drones are fetching and what its beams are
    /// pulling in. Several ore fetches under way at once would otherwise each be told the whole room that is left.
    /// </summary>
    private double OreOnItsWay(HullHandle hull, int resource)
    {
        var packed = hull.Packed;
        var kg = 0.0;
        foreach (ref readonly var chunk in _chunks.Items)
            if (chunk.Resource == resource && (chunk.FetchHull == packed || chunk.TractorHull == packed)) kg += chunk.Mass;
        return kg;
    }

    private bool OreSkipped(ref BayPage page, uint id)
    {
        for (var i = 0; i < AutoSkipSlots; i++)
            if (page.OreSkip[i].Id == id && _tick < page.OreSkip[i].Until) return true;
        return false;
    }
}
