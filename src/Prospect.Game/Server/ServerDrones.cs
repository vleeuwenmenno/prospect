using System.Numerics;
using System.Runtime.CompilerServices;
using Prospect.Engine.Diagnostics;
using Prospect.Engine.Net;
using Prospect.Game.Protocol;
using Prospect.Game.World;

namespace Prospect.Game.Server;

/// <summary>
/// Drone bays and the orders drones carry out. A hull with bays has a page of records here: drones inside each bay,
/// out of it, waiting to launch, and the tick a lost one is rebuilt. A drone is a small real hull of the work drone
/// design, owned by the bay's owner and flown by the server; what it does is a <see cref="TaskOrder"/> in one fixed
/// table, which <see cref="TickDrones"/> carries out each tick whoever gave it. The executor turns an order into the
/// drone's <see cref="HullOrder"/>, so the drone flies with the same autopilot a client predicts it with. Nothing here
/// takes memory in a tick except the page table growing when more hulls have bays than ever before.
/// </summary>
public sealed partial class ServerHost
{
    private struct BayRecord
    {
        public byte Stocked, Out, Pending;

        /// <summary>Index of the next place a drone of this bay takes around its hold point.</summary>
        public byte Slot;

        /// <summary>The tick the drone being rebuilt is done; zero while nothing is.</summary>
        public long RebuildDone;
        public long NextLaunch;
    }

    [InlineArray(DroneBayRules.MaxBays)]
    private struct BayRecords
    {
        private BayRecord _first;
    }

    /// <summary>A container auto pickup was refused for, and the tick it may try it again.</summary>
    private struct AutoSkipRecord
    {
        public uint Id;
        public long Until;
    }

    [InlineArray(AutoSkipSlots)]
    private struct AutoSkips
    {
        private AutoSkipRecord _first;
    }

    /// <summary>Refused containers one hull remembers; the oldest is forgotten first.</summary>
    private const int AutoSkipSlots = 4;

    private struct BayPage
    {
        public HullHandle Hull;
        public bool Used;

        /// <summary>Counts every change a pilot would see.</summary>
        public uint Revision;

        /// <summary>The point the next launch holds, in the hull's frame, when the player named one.</summary>
        public Vector3 LaunchPoint;
        public bool LaunchHasPoint;
        public byte LaunchSlot;

        /// <summary>The player whose launch is waiting, to be told how it went.</summary>
        public uint LaunchIssuer;

        /// <summary>
        /// Drones of the waiting launches that go to fetch <see cref="FetchSubject"/> rather than hold station, what
        /// they carry together, the next place beside the container and when the fetch gives up.
        /// </summary>
        public byte FetchPending, FetchSlot;
        public float FetchPendingKg;
        public uint FetchSubject;
        public long FetchUntil;

        /// <summary>
        /// Auto pickup (saved with the hull): while on, <see cref="TickAutoPickup"/> starts fetches for loose
        /// containers by itself. <see cref="AutoPausedUntil"/> is the tick a manual recall holds it off until,
        /// <see cref="AutoCursor"/> where the rotating look at the container pool goes on, and the skips the
        /// containers a fetch was refused for lately. None of the three is sent or saved.
        /// </summary>
        public bool AutoPickup;
        public long AutoPausedUntil;
        public int AutoCursor, AutoSkipNext;
        public AutoSkips AutoSkip;

        /// <summary>The hull's drone scan, if one is under way (<see cref="ServerHost.ScanGroup"/>).</summary>
        public ScanGroup Scan;
        public BayRecords Bays;
    }

    private BayPage[] _bayPages = new BayPage[64];
    private int _bayPageHigh;
    private readonly TaskOrder[] _orders = new TaskOrder[DroneBayRules.MaxOrders];
    private int _orderCount;

    /// <summary>Gives a new hull with bays its page, every bay full: until there is an economy a bay starts full.</summary>
    private void AttachBays(HullHandle hull)
    {
        ref var info = ref _hulls.Info(hull.Index);
        var design = _content.Designs[info.Design];
        if (design.Bays.Length == 0 || info.Owner == 0 || info.Wreck) return;
        var page = -1;
        for (var i = 0; i < _bayPageHigh && page < 0; i++)
            if (!_bayPages[i].Used) page = i;
        if (page < 0)
        {
            if (_bayPageHigh == _bayPages.Length) Array.Resize(ref _bayPages, _bayPages.Length * 2);
            page = _bayPageHigh++;
        }

        ref var p = ref _bayPages[page];
        p = default;
        p.Hull = hull;
        p.Used = true;
        for (var b = 0; b < design.Bays.Length; b++)
            p.Bays[b].Stocked = (byte)design.Types[design.Bays[b]].Bay!.Capacity;
        info.BayPage = page + 1;
    }

    /// <summary>The page of a live hull's bays, or -1.</summary>
    private int PageOf(HullHandle hull)
    {
        if (!_hulls.IsAlive(hull)) return -1;
        var page = _hulls.Info(hull.Index).BayPage - 1;
        return page >= 0 && page < _bayPageHigh && _bayPages[page].Used && _bayPages[page].Hull == hull ? page : -1;
    }

    /// <summary>
    /// A hull replaced by an edit or a repair keeps its bays where they match one of the new design's (the same
    /// cells and type): drones inside, out and being rebuilt. A bay that is new starts full; one whose block was lost
    /// starts empty and rebuilds. Drones out of a bay that is gone are given another bay with room, or are lost.
    /// Call it after the new hull exists and before the old one is destroyed.
    /// </summary>
    private void CarryBays(HullHandle old, HullHandle next)
    {
        var from = PageOf(old);
        var to = PageOf(next);
        if (from < 0) return;
        var before = _content.Designs[_hulls.Info(old.Index).Design];
        var after = _content.Designs[_hulls.Info(next.Index).Design];
        Span<int> moved = stackalloc int[DroneBayRules.MaxBays];
        for (var b = 0; b < before.Bays.Length; b++)
        {
            moved[b] = -1;
            var box = before.Boxes[before.Bays[b]];
            for (var n = 0; to >= 0 && n < after.Bays.Length && moved[b] < 0; n++)
            {
                var other = after.Boxes[after.Bays[n]];
                if (other.At == box.At && other.Size == box.Size && other.Type == box.Type) moved[b] = n;
            }
            if (moved[b] >= 0)
            {
                ref var target = ref _bayPages[to].Bays[moved[b]];
                target = _bayPages[from].Bays[b];
                target.Pending = 0;
            }
        }

        for (var i = _orderCount - 1; i >= 0; i--)
        {
            ref var order = ref _orders[i];
            if (order.Base != old) continue;
            var bay = moved[order.Bay];
            if (bay < 0 && to >= 0)
            {
                for (var n = 0; n < after.Bays.Length && bay < 0; n++)
                {
                    ref var candidate = ref _bayPages[to].Bays[n];
                    if (candidate.Stocked + candidate.Out < after.Types[after.Bays[n]].Bay!.Capacity) { bay = n; candidate.Out++; }
                }
            }
            if (bay < 0)
            {
                // Nothing to come home to: the drone is lost with its bay.
                _hulls.Destroy(order.Actor);
                RemoveOrder(i);
                continue;
            }
            order.Base = next;
            order.Bay = bay;
        }
        if (to >= 0) { _bayPages[to].AutoPickup = _bayPages[from].AutoPickup; _bayPages[to].Scan = _bayPages[from].Scan; _bayPages[to].Revision++; }
    }

    private void RemoveOrder(int index) => _orders[index] = _orders[--_orderCount];

    /// <summary>How many drones a player has out or waiting to launch.</summary>
    private int DronesOf(uint player)
    {
        var count = 0;
        for (var i = 0; i < _orderCount; i++)
            if (_orders[i].Issuer == player) count++;
        for (var p = 0; p < _bayPageHigh; p++)
        {
            ref var page = ref _bayPages[p];
            if (!page.Used || !_hulls.IsAlive(page.Hull) || _hulls.Info(page.Hull.Index).Owner != player) continue;
            var bays = _content.Designs[_hulls.Info(page.Hull.Index).Design].Bays.Length;
            for (var b = 0; b < bays; b++) count += page.Bays[b].Pending;
        }
        return count;
    }

    /// <summary>Drones waiting to launch anywhere.</summary>
    private int PendingDrones()
    {
        var count = 0;
        for (var p = 0; p < _bayPageHigh; p++)
        {
            ref var page = ref _bayPages[p];
            if (!page.Used) continue;
            for (var b = 0; b < DroneBayRules.MaxBays; b++) count += page.Bays[b].Pending;
        }
        return count;
    }

    /// <summary>How many more drones the player and the world have room for, and what stops more.</summary>
    private int DroneRoom(uint player, out DroneReply limit)
    {
        var rules = _content.Drones;
        var pending = PendingDrones();
        var personal = rules.MaxPerPlayer - DronesOf(player);
        var world = DroneBayRules.HullCeiling - rules.HullReserve - _hulls.Count - pending;
        var table = DroneBayRules.MaxOrders - _orderCount - pending;
        var drone = _content.Designs[_content.WorkDroneDesign];
        var saved = 0;
        while (saved < Math.Min(world, table) && HasRoomQuiet(pending + saved + 1, (long)(pending + saved + 1) * drone.BlockCount)) saved++;
        world = Math.Min(Math.Min(world, table), saved);
        limit = personal <= world ? DroneReply.PlayerLimit : DroneReply.WorldFull;
        return Math.Max(0, Math.Min(personal, world));
    }

    /// <summary><see cref="HasRoom"/> without counting a refusal: asking how much room there is refuses nothing.</summary>
    private bool HasRoomQuiet(int hulls, long blocks) =>
        _save is null || (_hulls.Count + hulls <= _saveLimits.MaxHulls - _saveLimits.MaxOwners
            && _hulls.BlockTotal + blocks <= _saveLimits.MaxBlocks - (long)_saveLimits.MaxOwners * _content.Designs[_content.DroneDesign].BlockCount);

    private bool HatchClear(HullHandle hull, int block) =>
        DroneBayRules.HatchClear(_content.Designs[_hulls.Info(hull.Index).Design], _hulls.Blocks(hull.Index), block,
            _content.Drones.HatchClearanceMetres, _content.Rules.CollisionTreeVisits);

    /// <summary>A point of a hull's own frame, in metres from its pivot, in the world.</summary>
    private Double3 WorldPoint(HullHandle hull, Vector3 local)
    {
        var state = _hulls.State(hull.Index);
        return state.Position + HullRotation.Rotate(local, state.Orientation);
    }

    private void ReceiveDroneCommand(Connection connection, in DroneCommandMessage command)
    {
        if (_tick < connection.DroneNextCommand) return;
        connection.DroneNextCommand = _tick + _content.Drones.CommandTicks;
        var hull = new HullHandle((int)(command.Hull & 0xFFFF), (int)(command.Hull >> 16));
        if (!_hulls.IsAlive(hull)) return;
        var info = _hulls.Info(hull.Index);
        if (info.Owner != connection.PlayerId || info.WorkDrone || info.Wreck) return;
        var page = PageOf(hull);
        if (page < 0) { ReplyDrones(connection, DroneReply.NoBay, 0); return; }
        if (command.Command == DroneCommand.Recall) RecallDrones(connection, hull, page);
        else if (command.Command == DroneCommand.Fetch) FetchContainer(connection, hull, page, command.Subject);
        else
        {
            _bayPages[page].AutoPausedUntil = 0;
            LaunchDrones(connection, hull, page, command);
        }
    }

    /// <summary>
    /// A drone setting, after admission and under its own rate limit (the same spacing as a command, kept apart so
    /// a launch and a click on the checkbox never cost each other). Only the owner's own, live, non-drone, non-wreck
    /// hull with bays takes one.
    /// </summary>
    private void ReceiveDroneSettings(Connection connection, in DroneSettingsMessage message)
    {
        if (connection.State != ConnectionState.Active || _tick < connection.DroneSettingsNext) return;
        connection.DroneSettingsNext = _tick + _content.Drones.CommandTicks;
        var hull = new HullHandle((int)(message.Hull & 0xFFFF), (int)(message.Hull >> 16));
        if (!_hulls.IsAlive(hull)) return;
        var info = _hulls.Info(hull.Index);
        if (info.Owner != connection.PlayerId || info.WorkDrone || info.Wreck) return;
        var page = PageOf(hull);
        if (page < 0) { ReplyDrones(connection, DroneReply.NoBay, 0); return; }
        ref var p = ref _bayPages[page];
        if (p.AutoPickup == message.AutoPickup) return;
        p.AutoPickup = message.AutoPickup;
        p.AutoPausedUntil = 0;
        p.Revision++;
    }

    private void LaunchDrones(Connection connection, HullHandle hull, int page, in DroneCommandMessage command)
    {
        if (_hulls.State(hull.Index).Jump != JumpPhase.None) { ReplyDrones(connection, DroneReply.InTransit, 0); return; }
        var rules = _content.Drones;
        if (command.HasPoint && command.Point.Length() > rules.MaxHoldDistanceMetres) { ReplyDrones(connection, DroneReply.TooFar, 0); return; }
        var room = DroneRoom(connection.PlayerId, out var limit);
        var want = Math.Min((int)command.Count, room);
        var design = _content.Designs[_hulls.Info(hull.Index).Design];
        var alive = _hulls.Blocks(hull.Index);
        ref var p = ref _bayPages[page];
        bool live = false, stocked = false;
        var taken = 0;
        for (var b = 0; b < design.Bays.Length && taken < want; b++)
        {
            var block = design.Bays[b];
            if (!BlockSet.Has(alive, block)) continue;
            live = true;
            ref var bay = ref p.Bays[b];
            var free = bay.Stocked - bay.Pending;
            if (free <= 0) continue;
            stocked = true;
            if (!HatchClear(hull, block)) continue;
            var count = Math.Min(free, want - taken);
            bay.Pending += (byte)count;
            if (bay.NextLaunch < _tick) bay.NextLaunch = _tick;
            taken += count;
        }

        if (taken == 0)
        {
            var reason = want == 0 && room == 0 ? limit : !live ? DroneReply.NoBay : !stocked ? DroneReply.Empty : DroneReply.HatchBlocked;
            ReplyDrones(connection, reason, 0);
            return;
        }

        // A point named again starts its group over; drones already holding keep their places.
        p.LaunchHasPoint = command.HasPoint;
        p.LaunchPoint = command.Point;
        p.LaunchIssuer = connection.PlayerId;
        if (command.HasPoint) p.LaunchSlot = 0;
        p.Revision++;
        ReplyDrones(connection, DroneReply.Launching, taken);
    }

    private void RecallDrones(Connection connection, HullHandle hull, int page)
    {
        var count = 0;
        ref var p = ref _bayPages[page];
        for (var b = 0; b < DroneBayRules.MaxBays; b++) p.Bays[b].Pending = 0;
        p.FetchPending = 0; p.FetchPendingKg = 0;
        // A scan under way is given up: its drones fly around the rock and home like the rest.
        p.Scan.Pending = 0;
        if (p.Scan.Phase is not (DroneScanPhase.None or DroneScanPhase.Returning)) EndDroneScan(page);
        // A recall by hand means "come home": auto pickup holds off for a while instead of sending them straight out again.
        if (p.AutoPickup) p.AutoPausedUntil = _tick + _content.Drones.AutoPickupPauseTicks;
        for (var i = 0; i < _orderCount; i++)
        {
            ref var order = ref _orders[i];
            if (order.Base != hull) continue;
            // Scan drones were sent around the rock by EndDroneScan; they take the recall once on the ship's side.
            if (order.Task != OrderTask.Recall && !IsScan(order.Task)) ToRecall(ref order);
            count++;
        }

        // Containers the drones were fetching are let go where they are; the answer is the recall's.
        for (var c = 0; c < _containers.Items.Length; c++)
            if (_containers.Items[c].FetchHull == hull.Packed) FreeContainers.ClearFetch(ref _containers.At(c));
        p.Revision++;
        ReplyDrones(connection, count > 0 ? DroneReply.Recalling : DroneReply.NoneOut, count);
    }

    private static void ReplyDrones(Connection connection, DroneReply reply, int count)
    {
        connection.DroneReplySequence++;
        connection.DroneReply = reply;
        connection.DroneReplyCount = (byte)Math.Clamp(count, 0, byte.MaxValue);
    }

    private void ReplyDrones(uint player, DroneReply reply, int count)
    {
        foreach (var connection in _connections)
            if (connection.State == ConnectionState.Active && connection.PlayerId == player) ReplyDrones(connection, reply, count);
    }

    /// <summary>
    /// Once a tick, before the hulls step: frees the pages of hulls that are gone, rebuilds lost drones, lets waiting
    /// drones out through their hatches, and carries out every order.
    /// </summary>
    private void TickDrones()
    {
        for (var p = 0; p < _bayPageHigh; p++)
        {
            ref var page = ref _bayPages[p];
            if (!page.Used) continue;
            if (!_hulls.IsAlive(page.Hull) || _hulls.Info(page.Hull.Index).BayPage != p + 1) { page = default; continue; }
            TickBays(p);
            TickAutoPickup(p);
            TickDroneScan(p);
        }
        ForgetDroneScans();

        for (var i = _orderCount - 1; i >= 0; i--)
            if (!TickOrder(i)) RemoveOrder(i);
    }

    private void TickBays(int index)
    {
        ref var page = ref _bayPages[index];
        var hull = page.Hull;
        var info = _hulls.Info(hull.Index);
        var design = _content.Designs[info.Design];
        var alive = _hulls.Blocks(hull.Index);
        var jumping = _hulls.State(hull.Index).Jump != JumpPhase.None;
        for (var b = 0; b < design.Bays.Length; b++)
        {
            var block = design.Bays[b];
            var spec = design.Types[block].Bay!;
            ref var bay = ref page.Bays[b];
            if (!BlockSet.Has(alive, block))
            {
                // A bay that is gone loses what was inside and builds nothing; its drones out look for another.
                if (bay.Stocked != 0 || bay.Pending != 0 || bay.RebuildDone != 0) { bay.Stocked = bay.Pending = 0; bay.RebuildDone = 0; page.Revision++; }
                continue;
            }

            if (bay.Stocked + bay.Out < spec.Capacity)
            {
                if (bay.RebuildDone == 0) { bay.RebuildDone = _tick + spec.RebuildTicks; page.Revision++; }
                else if (_tick >= bay.RebuildDone) { bay.Stocked++; bay.RebuildDone = 0; page.Revision++; }
            }
            else if (bay.RebuildDone != 0) { bay.RebuildDone = 0; page.Revision++; }

            if (bay.Pending == 0 || _tick < bay.NextLaunch) continue;
            if (jumping) { CancelLaunches(index, DroneReply.InTransit); return; }
            if (!HatchClear(hull, block)) { bay.Pending = 0; page.Revision++; ReplyDrones(page.LaunchIssuer, DroneReply.HatchBlocked, 0); continue; }
            var count = Math.Min(Math.Min((int)bay.Pending, spec.LaunchRate), (int)bay.Stocked);
            for (var k = 0; k < count; k++)
            {
                var reply = LaunchOne(index, b, k);
                if (reply != DroneReply.None) { CancelLaunches(index, reply); return; }
            }
            bay.NextLaunch = _tick + _content.Drones.LaunchIntervalTicks;
            if (bay.Pending > bay.Stocked) bay.Pending = bay.Stocked;
        }

        // Launches that were dropped (a hatch blocked, a bay lost) take their share of a fetch with them.
        var pending = 0;
        for (var b = 0; b < design.Bays.Length; b++) pending += page.Bays[b].Pending;
        if (page.FetchPending > pending) { page.FetchPending = (byte)pending; if (pending == 0) page.FetchPendingKg = 0; }
        var scanRoom = Math.Max(0, pending - page.FetchPending);
        if (page.Scan.Pending > scanRoom) page.Scan.Pending = (byte)scanRoom;
    }

    private void CancelLaunches(int index, DroneReply reply)
    {
        ref var page = ref _bayPages[index];
        for (var b = 0; b < DroneBayRules.MaxBays; b++) page.Bays[b].Pending = 0;
        page.FetchPending = 0; page.FetchPendingKg = 0;
        page.Scan.Pending = 0;
        page.Revision++;
        ReplyDrones(page.LaunchIssuer, reply, 0);
    }

    /// <summary>Lets one drone out of a bay's hatch and gives it its order to hold. None when it went, else why not.</summary>
    private DroneReply LaunchOne(int index, int bayIndex, int order)
    {
        var hull = _bayPages[index].Hull;
        var owner = _hulls.Info(hull.Index).Owner;
        if (_orderCount >= DroneBayRules.MaxOrders || _hulls.Count >= DroneBayRules.HullCeiling - _content.Drones.HullReserve) return DroneReply.WorldFull;
        var designIndex = (byte)_content.WorkDroneDesign;
        var drone = _content.Designs[designIndex];
        if (!HasRoom(1, drone.BlockCount)) return DroneReply.WorldFull;
        var rules = _content.Drones;
        var design = _content.Designs[_hulls.Info(hull.Index).Design];
        var block = design.Bays[bayIndex];
        var face = DroneBayRules.Hatch(design, block);
        var normal = DroneBayRules.Normal(face);
        var local = DroneBayRules.LaunchPoint(design, block, drone.Flight.Radius, rules.HatchMarginMetres, order);
        var baseState = _hulls.State(hull.Index);
        var lever = local - _hulls.Flight(hull.Index).CenterOfMass;
        var velocity = baseState.Velocity + HullRotation.Rotate(Vector3.Cross(baseState.AngularVelocity, lever) + normal * rules.LaunchSpeed, baseState.Orientation);
        var handle = CreateHull(owner, designIndex, WorldPoint(hull, local), fullTanks: true);

        ref var state = ref _hulls.State(handle.Index);
        state.Orientation = baseState.Orientation;
        state.Velocity = velocity;
        ref var info = ref _hulls.Info(handle.Index);
        info.WorkDrone = true;
        info.DamageTick = _tick;

        ref var page = ref _bayPages[index];
        ref var bay = ref page.Bays[bayIndex];
        bay.Stocked--; bay.Pending--; bay.Out++;
        page.Revision++;
        if (page.FetchPending > 0)
        {
            // A drone launched for a fetch goes straight to its place beside the container.
            var container = _containers.Find(page.FetchSubject);
            var half = container >= 0 ? FreeContainers.Half(_content.Cargo, _containers.Items[container].Size) : Vector3.Zero;
            page.FetchPending--;
            page.FetchPendingKg = page.FetchPending == 0 ? 0 : Math.Max(0, page.FetchPendingKg - design.Types[block].Bay!.CarryKg);
            _orders[_orderCount++] = new TaskOrder
            {
                Task = OrderTask.Fetch,
                Issuer = owner,
                Actor = handle,
                Base = hull,
                Bay = bayIndex,
                Point = DroneFetch.Place(page.FetchSlot++, half, drone.Flight.Radius, rules.AttachGapMetres),
                Subject = page.FetchSubject,
                Until = page.FetchUntil,
            };
            return DroneReply.None;
        }

        if (page.Scan.Pending > 0 && page.Scan.Phase is DroneScanPhase.Moving or DroneScanPhase.Ready)
        {
            // A drone launched for a scan goes straight to its place around the rock.
            ref var scan = ref page.Scan;
            scan.Pending--;
            _orders[_orderCount++] = new TaskOrder
            {
                Task = ScanTask(scan.Kind),
                Phase = ScanOrderPhase.Moving,
                Issuer = owner,
                Actor = handle,
                Base = hull,
                Bay = bayIndex,
                Point = DroneScanning.Place(scan.Slot++, scan.Drones, scan.Asteroid.Radius, scan.Asteroid.Shape, scan.Standoff),
            };
            return DroneReply.None;
        }

        Vector3 point;
        if (page.LaunchHasPoint) point = DroneBayRules.HoldSlot(page.LaunchPoint, face, page.LaunchSlot++, rules.HoldSpacingMetres);
        else point = DroneBayRules.HoldSlot(DroneBayRules.DefaultHold(design, block, rules.HoldDistanceMetres), face, bay.Slot++, rules.HoldSpacingMetres);
        _orders[_orderCount++] = new TaskOrder
        {
            Task = OrderTask.Hold,
            Issuer = owner,
            Actor = handle,
            Base = hull,
            Bay = bayIndex,
            Point = point,
        };
        return DroneReply.None;
    }

    /// <summary>Carries out one order for a tick. False when the order is over and leaves the table.</summary>
    private bool TickOrder(int index)
    {
        ref var order = ref _orders[index];
        var page = PageOf(order.Base);
        if (!_hulls.IsAlive(order.Actor))
        {
            // The drone was destroyed: its bay builds another.
            if (page >= 0) { ref var lost = ref _bayPages[page].Bays[order.Bay]; if (lost.Out > 0) lost.Out--; _bayPages[page].Revision++; }
            return false;
        }

        // A drone whose hull is gone, or has jumped away, has nothing to come home to.
        var jump = page >= 0 ? _hulls.State(order.Base.Index).Jump : JumpPhase.None;
        if (page < 0 || jump is JumpPhase.Tunnel or JumpPhase.Arrive)
        {
            _hulls.Destroy(order.Actor);
            if (page >= 0) { ref var lost = ref _bayPages[page].Bays[order.Bay]; if (lost.Out > 0) lost.Out--; _bayPages[page].Revision++; }
            return false;
        }

        // A hull that starts a jump calls its drones home; those still out when it leaves are lost.
        if (jump != JumpPhase.None && order.Task != OrderTask.Recall && !IsScan(order.Task)) ToRecall(ref order);

        switch (order.Task)
        {
            case OrderTask.Hold:
                Fly(order.Actor, WorldPoint(order.Base, order.Point), _content.Drones.HoldSlackMetres);
                return true;
            case OrderTask.Recall:
                return Recall(ref order, page);
            case OrderTask.Fetch:
                return Fetch(ref order, page);
            case OrderTask.Survey:
            case OrderTask.DetailedScan:
                return ScanOrder(ref order, page);
            default:
                return false;
        }
    }

    /// <summary>
    /// Keeps a drone's autopilot on <paramref name="target"/>: a new order once it has drifted <paramref name="slack"/>
    /// away, or once the point it was given has moved a quarter of that.
    /// </summary>
    private void Fly(HullHandle drone, Double3 target, float slack)
    {
        ref var state = ref _hulls.State(drone.Index);
        ref var info = ref _hulls.Info(drone.Index);
        var moved = info.Order.Kind == OrderKind.MoveTo
            ? info.Order.Target.RelativeTo(target).LengthSquared() > slack * slack * 0.0625f
            : target.RelativeTo(state.Position).LengthSquared() > slack * slack;
        if (moved) HullSim.SetOrder(ref state, ref info.Order, new HullOrder { Kind = OrderKind.MoveTo, Target = target });
    }

    /// <summary>Lines a drone up in front of its hatch, flies it straight in, and docks it. False once docked.</summary>
    private bool Recall(ref TaskOrder order, int page)
    {
        var rules = _content.Drones;
        var design = _content.Designs[_hulls.Info(order.Base.Index).Design];
        var alive = _hulls.Blocks(order.Base.Index);
        ref var p = ref _bayPages[page];
        if (!BlockSet.Has(alive, design.Bays[order.Bay]))
        {
            // Its bay is gone: another live bay with room takes it, or it holds where it is told to wait.
            var found = -1;
            for (var b = 0; b < design.Bays.Length && found < 0; b++)
                if (BlockSet.Has(alive, design.Bays[b]) && p.Bays[b].Stocked + p.Bays[b].Out < design.Types[design.Bays[b]].Bay!.Capacity) found = b;
            if (found < 0)
            {
                Fly(order.Actor, WorldPoint(order.Base, order.Point), rules.HoldSlackMetres);
                return true;
            }
            if (p.Bays[order.Bay].Out > 0) p.Bays[order.Bay].Out--;
            p.Bays[found].Out++;
            p.Revision++;
            order.Bay = found;
            order.Phase = 0;
        }

        var block = design.Bays[order.Bay];
        var normal = DroneBayRules.Normal(DroneBayRules.Hatch(design, block));
        var radius = _content.Designs[_content.WorkDroneDesign].Flight.Radius;
        var staging = WorldPoint(order.Base, DroneBayRules.StagingPoint(design, block, rules.StagingMetres));
        var position = _hulls.State(order.Actor.Index).Position;
        if (order.Phase == RecallFromAfar)
        {
            var hold = WorldPoint(order.Base, order.Point);
            if (hold.RelativeTo(position).LengthSquared() <= rules.StagingRadiusMetres * rules.StagingRadiusMetres) order.Phase = 0;
            else { Fly(order.Actor, hold, rules.StagingRadiusMetres * 0.5f); return true; }
        }
        if (order.Phase == 0)
        {
            if (staging.RelativeTo(position).LengthSquared() <= rules.StagingRadiusMetres * rules.StagingRadiusMetres) order.Phase = 1;
            else { Fly(order.Actor, staging, rules.StagingRadiusMetres * 0.5f); return true; }
        }

        // A drone only passes an open hatch; while it is blocked the drone waits lined up in front of it.
        if (!HatchClear(order.Base, block)) { Fly(order.Actor, staging, rules.StagingRadiusMetres * 0.5f); return true; }
        var dock = DroneBayRules.DockPoint(design, block, radius);
        if (WorldPoint(order.Base, dock).RelativeTo(position).LengthSquared() <= rules.DockRadiusMetres * rules.DockRadiusMetres)
        {
            _hulls.Destroy(order.Actor);
            ref var bay = ref p.Bays[order.Bay];
            if (bay.Out > 0) bay.Out--;
            bay.Stocked++;
            if (bay.Out == 0) bay.Slot = 0;
            p.Revision++;
            return false;
        }

        // Aim past the hatch, so the autopilot is still closing when the drone reaches it.
        Fly(order.Actor, WorldPoint(order.Base, dock - normal * rules.DockPushMetres), rules.DockRadiusMetres);
        return true;
    }

    /// <summary>Sends each pilot their flown hull's bays when they change, with the newest answer to a drone command.</summary>
    private void SendBays()
    {
        foreach (var c in _connections)
        {
            if (c.State != ConnectionState.Active || !c.CatalogueReady || !_hulls.IsAlive(c.Hull)) continue;
            var page = PageOf(c.Hull);
            var revision = (page >= 0 ? _bayPages[page].Revision : 0u) + _hulls.Revision(c.Hull.Index);
            if (c.BaysHull == c.Hull.Packed && c.BaysRevision == revision && c.BaysReply == c.DroneReplySequence) continue;
            var view = new BayView
            {
                Hull = c.Hull.Packed,
                Tick = _tick,
                ReplySequence = c.DroneReplySequence,
                Reply = c.DroneReply,
                ReplyCount = c.DroneReplyCount,
                Fetching = (byte)Math.Min(byte.MaxValue, FetchingDrones(c.Hull, page)),
                AutoPickup = page >= 0 && _bayPages[page].AutoPickup,
            };
            if (page >= 0 && _bayPages[page].Scan.Phase != DroneScanPhase.None)
            {
                ref readonly var scan = ref _bayPages[page].Scan;
                view.ScanKind = scan.Kind;
                view.ScanPhase = scan.Phase;
                view.ScanRock = scan.Rock;
                view.ScanDrones = scan.Drones;
                view.ScanInPlace = scan.InPlace;
                view.ScanStarted = scan.Phase == DroneScanPhase.Scanning ? scan.Started : 0;
                view.ScanCompletes = scan.Phase == DroneScanPhase.Scanning ? scan.Completes : 0;
            }
            if (page >= 0)
            {
                var design = _content.Designs[_hulls.Info(c.Hull.Index).Design];
                var alive = _hulls.Blocks(c.Hull.Index);
                view.Count = design.Bays.Length;
                for (var b = 0; b < view.Count; b++)
                {
                    var block = design.Bays[b];
                    ref readonly var bay = ref _bayPages[page].Bays[b];
                    var live = BlockSet.Has(alive, block);
                    view.Bays[b] = new BayEntry
                    {
                        Block = (ushort)block,
                        Capacity = (byte)design.Types[block].Bay!.Capacity,
                        Stocked = bay.Stocked,
                        Out = bay.Out,
                        Pending = bay.Pending,
                        Live = live,
                        HatchClear = live && HatchClear(c.Hull, block),
                        RebuildDone = bay.RebuildDone,
                    };
                }
            }
            var writer = new NetWriter(_sendBuffer);
            BayMessage.Write(ref writer, view);
            if (c.Transport.Send(c.Peer, NetChannel.Reliable, writer.Written))
            { c.BaysHull = c.Hull.Packed; c.BaysRevision = revision; c.BaysReply = c.DroneReplySequence; }
        }
    }

    /// <summary>The drones out of a hull that is about to be captured, counted back into their bays: a saved world docks them.</summary>
    private int SavedStock(int page, int bay, int capacity) =>
        Math.Min(capacity, _bayPages[page].Bays[bay].Stocked + _bayPages[page].Bays[bay].Out);

    /// <summary>Ticks left on a bay's rebuild as a save keeps them; zero for none.</summary>
    private int SavedRebuild(int page, int bay) =>
        _bayPages[page].Bays[bay].RebuildDone == 0 ? 0 : (int)Math.Clamp(_bayPages[page].Bays[bay].RebuildDone - _tick, 1, int.MaxValue);

    /// <summary>Gives a restored hull's bays what the save says, after its page is attached. False with the reason.</summary>
    private bool RestoreBays(HullHandle hull, ReadOnlySpan<byte> stocked, ReadOnlySpan<int> rebuild, long nextTick, out string error)
    {
        var info = _hulls.Info(hull.Index);
        var design = _content.Designs[info.Design];
        var page = PageOf(hull);
        var expected = page >= 0 ? design.Bays.Length : 0;
        if (stocked.Length != expected || rebuild.Length != expected) { error = "its drone bays do not match its design"; return false; }
        var alive = _hulls.Blocks(hull.Index);
        for (var b = 0; b < expected; b++)
        {
            var spec = design.Types[design.Bays[b]].Bay!;
            var live = BlockSet.Has(alive, design.Bays[b]);
            if (stocked[b] > spec.Capacity || rebuild[b] < 0 || rebuild[b] > spec.RebuildTicks
                || (!live && (stocked[b] != 0 || rebuild[b] != 0)) || (rebuild[b] > 0 && stocked[b] >= spec.Capacity))
            { error = "its drone bays hold what such a bay cannot"; return false; }
            ref var bay = ref _bayPages[page].Bays[b];
            bay.Stocked = stocked[b];
            bay.RebuildDone = rebuild[b] == 0 ? 0 : nextTick + rebuild[b];
        }
        error = "";
        return true;
    }
}
