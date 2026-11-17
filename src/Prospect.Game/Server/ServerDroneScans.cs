using System.Numerics;
using Prospect.Game.Protocol;
using Prospect.Game.World;

namespace Prospect.Game.Server;

/// <summary>
/// Drone scans of rocks. A scan is a group of <see cref="OrderTask.Survey"/> or <see cref="OrderTask.DetailedScan"/>
/// orders around one rock, kept on the hull's bay page (<see cref="ScanGroup"/>): the server works out how many drones
/// the rock needs (<see cref="DroneScanning.Needed"/>), sends drones holding station and drones from the bays to places
/// spread evenly around it, waits until all hold their places (Ready), and runs the scan when the player orders it.
/// The result is worked out of the rock's interior (<see cref="DroneScanResults"/>) and kept per owner, saved, and sent
/// to that owner only. Scan drones come home and are used again. Takes no memory in a tick.
/// </summary>
public sealed partial class ServerHost
{
    /// <summary>A hull's drone scan: one at a time.</summary>
    private struct ScanGroup
    {
        public DroneScanKind Kind;
        public DroneScanPhase Phase;
        public RockRef Rock;

        /// <summary>The rock as generated: its interior is described from it when the scan ends.</summary>
        public Asteroid Asteroid;

        /// <summary>Where the rock was generated; where it is now adds its drift.</summary>
        public Double3 Centre;

        /// <summary>The bounding shell drones travel on, and how far off the surface their places are.</summary>
        public float Shell, Standoff;

        /// <summary>Drones the scan takes, launches still waiting for it, the next place to give, and how many hold theirs.</summary>
        public byte Drones, Pending, Slot, InPlace;

        /// <summary>When the current phase gives up (moving, waiting for the order), and when the scan began and ends.</summary>
        public long Until, Started, Completes;
    }

    private sealed class DroneScanOwner
    {
        internal uint Owner;
        internal int Count;
        internal readonly DroneScanRecord[] Records = new DroneScanRecord[DroneScanRecordRules.KnowledgePerOwner];
    }

    private DroneScanOwner[] _droneScanOwners = [];

    private void InitializeDroneScans(int ownerCapacity)
    {
        _droneScanOwners = new DroneScanOwner[ownerCapacity];
        for (var i = 0; i < ownerCapacity; i++) _droneScanOwners[i] = new();
    }

    private DroneScanOwner? DroneScanOwnerFor(uint owner, bool create = false)
    {
        foreach (var entry in _droneScanOwners) if (entry.Owner == owner) return entry;
        if (create)
            foreach (var entry in _droneScanOwners)
                if (entry.Owner == 0) { entry.Owner = owner; return entry; }
        return null;
    }

    /// <summary>Test-visible: what one owner's drones have found.</summary>
    internal ReadOnlySpan<DroneScanRecord> DroneScanKnowledge(uint owner)
    {
        var entry = DroneScanOwnerFor(owner);
        return entry is null ? [] : entry.Records.AsSpan(0, entry.Count);
    }

    private static OrderTask ScanTask(DroneScanKind kind) => kind == DroneScanKind.Detailed ? OrderTask.DetailedScan : OrderTask.Survey;

    private static bool IsScan(OrderTask task) => task is OrderTask.Survey or OrderTask.DetailedScan;

    /// <summary>A drone scan command, after admission and under its own rate limit. Only the owner's own live ship with bays takes one.</summary>
    private void ReceiveDroneScan(Connection connection, in DroneScanCommandMessage message)
    {
        if (connection.State != ConnectionState.Active || _tick < connection.DroneScanNext) return;
        connection.DroneScanNext = _tick + _content.Drones.CommandTicks;
        var hull = new HullHandle((int)(message.Hull & 0xFFFF), (int)(message.Hull >> 16));
        if (!_hulls.IsAlive(hull)) return;
        var info = _hulls.Info(hull.Index);
        if (info.Owner != connection.PlayerId || info.WorkDrone || info.Wreck) return;
        var page = PageOf(hull);
        if (page < 0) { ReplyDrones(connection, DroneReply.NoBay, 0); return; }
        int count;
        var reply = message.Action switch
        {
            DroneScanAction.Request => StartDroneScan(connection.PlayerId, hull, page, message.Kind, message.Rock, out count),
            DroneScanAction.Start => RunDroneScan(page, out count),
            _ => CancelDroneScan(page, out count),
        };
        ReplyDrones(connection, reply, count);
    }

    /// <summary>
    /// The one way a drone scan starts. Checks the rock (in the player's sight, in range), works out how many drones it
    /// takes and whether the hull can field them now (drones holding station first, then drones in bays whose hatch is
    /// open), and sends them. Returns <see cref="DroneReply.ScanSent"/> with the count, or why not (with the drones
    /// needed where that is the reason).
    /// </summary>
    internal DroneReply StartDroneScan(uint viewer, HullHandle hull, int page, DroneScanKind kind, RockRef rock, out int count)
    {
        count = 0;
        if (Galaxy.Interiors is null) return DroneReply.ScanUnavailable;
        if (kind is not (DroneScanKind.Survey or DroneScanKind.Detailed)) return DroneReply.ScanUnavailable;
        if (_hulls.State(hull.Index).Jump != JumpPhase.None) return DroneReply.InTransit;
        ref var p = ref _bayPages[page];
        if (p.Scan.Phase != DroneScanPhase.None || p.FetchPending > 0) return DroneReply.ScanBusy;
        if ((uint)rock.Index >= (uint)Galaxy.MaxAsteroidsPerSector) return DroneReply.NotFound;
        var sector = _rocks.Sector(rock.Sector);
        if (rock.Index >= sector.AsteroidCount) return DroneReply.NotFound;
        var asteroid = sector.Asteroids[rock.Index];
        var centre = Galaxy.CenterOf(rock.Sector) + asteroid.LocalPosition;
        _rocks.Displacement(rock, _tick, out var offset, out _);
        var position = centre + offset;
        if (!Covered(viewer, position)) return DroneReply.NotFound;

        var rules = _content.Drones.Scans;
        var standoff = DroneScanning.Standoff(rules, asteroid.Radius);
        var shell = DroneScanning.Shell(rules, asteroid.Radius, asteroid.Shape);
        var surface = position.RelativeTo(_hulls.State(hull.Index).Position).Length() - (shell - standoff);
        if (surface > rules.RangeMetres) return DroneReply.TooFar;
        var needed = DroneScanning.Needed(rules, kind, asteroid.Radius, asteroid.Shape);
        count = Math.Min(needed, byte.MaxValue);
        if (needed > rules.Of(kind).MaxDrones) return DroneReply.TooLarge;

        // Drones holding station first, then drones in bays with an open hatch, as far as the player may have out.
        var holders = 0;
        for (var i = 0; i < _orderCount && holders < needed; i++)
            if (_orders[i].Base == hull && _orders[i].Task == OrderTask.Hold) holders++;
        var design = _content.Designs[_hulls.Info(hull.Index).Design];
        var alive = _hulls.Blocks(hull.Index);
        var room = DroneRoom(viewer, out _);
        Span<byte> taken = stackalloc byte[DroneBayRules.MaxBays];
        var launches = 0;
        for (var b = 0; b < design.Bays.Length && holders + launches < needed && launches < room; b++)
        {
            var block = design.Bays[b];
            if (!BlockSet.Has(alive, block) || !HatchClear(hull, block)) continue;
            var free = p.Bays[b].Stocked - p.Bays[b].Pending;
            while (free-- > 0 && holders + launches < needed && launches < room) { taken[b]++; launches++; }
        }
        if (holders + launches < needed) return DroneReply.TooFew;

        p.Scan = new ScanGroup
        {
            Kind = kind,
            Phase = DroneScanPhase.Moving,
            Rock = rock,
            Asteroid = asteroid,
            Centre = centre,
            Shell = shell,
            Standoff = standoff,
            Drones = (byte)needed,
            Pending = (byte)launches,
            Until = _tick + rules.MoveTicks,
        };
        var slot = 0;
        for (var i = 0; i < _orderCount && slot < holders; i++)
        {
            ref var order = ref _orders[i];
            if (order.Base != hull || order.Task != OrderTask.Hold) continue;
            order.Task = ScanTask(kind);
            order.Phase = ScanOrderPhase.Moving;
            order.Subject = 0;
            order.Until = 0;
            order.Point = DroneScanning.Place(slot++, needed, asteroid.Radius, asteroid.Shape, standoff);
        }
        p.Scan.Slot = (byte)slot;
        for (var b = 0; b < design.Bays.Length; b++)
        {
            if (taken[b] == 0) continue;
            p.Bays[b].Pending += taken[b];
            if (p.Bays[b].NextLaunch < _tick) p.Bays[b].NextLaunch = _tick;
        }
        if (launches > 0) p.LaunchIssuer = viewer;
        p.Revision++;
        count = needed;
        return DroneReply.ScanSent;
    }

    /// <summary>The player's order to scan: only once every drone holds its place.</summary>
    internal DroneReply RunDroneScan(int page, out int count)
    {
        ref var p = ref _bayPages[page];
        count = p.Scan.Drones;
        if (p.Scan.Phase != DroneScanPhase.Ready) return p.Scan.Phase == DroneScanPhase.Scanning ? DroneReply.ScanRunning : DroneReply.ScanNotReady;
        p.Scan.Phase = DroneScanPhase.Scanning;
        p.Scan.Started = _tick;
        p.Scan.Completes = _tick + _content.Drones.Scans.Of(p.Scan.Kind).ScanTicks;
        p.Revision++;
        return DroneReply.ScanRunning;
    }

    private DroneReply CancelDroneScan(int page, out int count)
    {
        ref var p = ref _bayPages[page];
        count = p.Scan.Drones;
        if (p.Scan.Phase is DroneScanPhase.None or DroneScanPhase.Returning) return DroneReply.NoneOut;
        EndDroneScan(page);
        return DroneReply.ScanCancelled;
    }

    /// <summary>
    /// Ends a hull's scan without a result or after one: launches still waiting for it are dropped and its drones fly
    /// around the rock and home (<see cref="ScanOrderPhase.ScanLeaving"/>). The group stays, Returning, until the last
    /// is on its way home, so they still know where the rock is.
    /// </summary>
    private void EndDroneScan(int page)
    {
        ref var p = ref _bayPages[page];
        var hull = p.Hull;
        if (p.Scan.Pending > 0 && _hulls.IsAlive(hull))
        {
            var design = _content.Designs[_hulls.Info(hull.Index).Design];
            for (var b = 0; b < design.Bays.Length && p.Scan.Pending > 0; b++)
            {
                var drop = Math.Min(p.Bays[b].Pending, p.Scan.Pending);
                p.Bays[b].Pending -= drop;
                p.Scan.Pending -= drop;
            }
        }
        p.Scan.Pending = 0;
        p.Scan.Phase = DroneScanPhase.Returning;
        for (var i = 0; i < _orderCount; i++)
            if (_orders[i].Base == hull && IsScan(_orders[i].Task)) _orders[i].Phase = ScanOrderPhase.ScanLeaving;
        p.Revision++;
    }

    /// <summary>Once a tick per hull with bays: moves its scan group on from phase to phase.</summary>
    private void TickDroneScan(int index)
    {
        ref var page = ref _bayPages[index];
        ref var scan = ref page.Scan;
        if (scan.Phase == DroneScanPhase.None) return;
        var hull = page.Hull;
        int working = 0, inPlace = 0, leaving = 0;
        for (var i = 0; i < _orderCount; i++)
        {
            ref readonly var order = ref _orders[i];
            if (order.Base != hull || !IsScan(order.Task) || !_hulls.IsAlive(order.Actor)) continue;
            if (order.Phase == ScanOrderPhase.ScanLeaving) leaving++;
            else { working++; if (order.Phase == ScanOrderPhase.InPlace) inPlace++; }
        }

        if (scan.Phase == DroneScanPhase.Returning)
        {
            if (working + leaving == 0) { scan = default; page.Revision++; }
            return;
        }

        var owner = _hulls.Info(hull.Index).Owner;
        var rules = _content.Drones.Scans;
        if (_hulls.State(hull.Index).Jump != JumpPhase.None) { EndDroneScan(index); ReplyDrones(owner, DroneReply.InTransit, 0); return; }
        // A drone lost or a launch dropped: too few to scan with.
        if (working + scan.Pending < scan.Drones) { EndDroneScan(index); ReplyDrones(owner, DroneReply.ScanAborted, scan.Drones); return; }
        // A ship that leaves the rock far behind takes its drones with it.
        RockPose(scan, out var centre, out _);
        if (centre.RelativeTo(_hulls.State(hull.Index).Position).Length() - scan.Shell > 2 * rules.RangeMetres)
        { EndDroneScan(index); ReplyDrones(owner, DroneReply.TooFar, 0); return; }
        if (inPlace != scan.InPlace) { scan.InPlace = (byte)inPlace; page.Revision++; }

        switch (scan.Phase)
        {
            case DroneScanPhase.Moving:
                if (inPlace >= scan.Drones)
                {
                    scan.Phase = DroneScanPhase.Ready;
                    scan.Until = _tick + rules.ReadyTicks;
                    page.Revision++;
                    ReplyDrones(owner, DroneReply.ScanReady, scan.Drones);
                }
                else if (_tick >= scan.Until) { EndDroneScan(index); ReplyDrones(owner, DroneReply.ScanTimedOut, scan.Drones); }
                break;
            case DroneScanPhase.Ready:
                if (inPlace < scan.Drones) { scan.Phase = DroneScanPhase.Moving; scan.Until = _tick + rules.MoveTicks; page.Revision++; }
                else if (_tick >= scan.Until) { EndDroneScan(index); ReplyDrones(owner, DroneReply.ScanTimedOut, scan.Drones); }
                break;
            case DroneScanPhase.Scanning:
                if (_tick >= scan.Completes) CompleteDroneScan(index, owner);
                break;
        }
    }

    /// <summary>The scan's result, worked out of the rock's interior, kept for its owner; then the drones come home.</summary>
    private void CompleteDroneScan(int index, uint owner)
    {
        ref var scan = ref _bayPages[index].Scan;
        var drones = scan.Drones;
        RockInterior.Describe(Galaxy, scan.Rock, scan.Asteroid, out var interior);
        var record = new DroneScanRecord { Owner = owner, Sector = scan.Rock.Sector, Index = scan.Rock.Index, RecordedTick = _tick };
        DroneScanResults.Build(scan.Kind, scan.Asteroid, interior, _content.Drones.Scans.Resolution, ref record);
        var kept = StoreDroneScan(record);
        EndDroneScan(index);
        ReplyDrones(owner, kept ? DroneReply.ScanDone : DroneReply.ScanUnavailable, drones);
    }

    /// <summary>
    /// Keeps a result for its owner: replaces what they had of that rock (a survey never replaces a detailed scan,
    /// which already holds all it says), or takes the place of their oldest when they keep as many as they may.
    /// </summary>
    internal bool StoreDroneScan(in DroneScanRecord record)
    {
        if (!DroneScanRecordRules.Valid(record)) return false;
        var owner = DroneScanOwnerFor(record.Owner, true);
        if (owner is null) return false;
        var at = -1;
        var oldest = 0;
        for (var i = 0; i < owner.Count; i++)
        {
            if (owner.Records[i].Sector == record.Sector && owner.Records[i].Index == record.Index) { at = i; break; }
            if (owner.Records[i].RecordedTick < owner.Records[oldest].RecordedTick) oldest = i;
        }
        if (at >= 0 && owner.Records[at].Kind == DroneScanKind.Detailed && record.Kind == DroneScanKind.Survey) return true;
        if (at < 0) at = owner.Count < owner.Records.Length ? owner.Count++ : oldest;
        owner.Records[at] = record;
        _touched = true;
        return true;
    }

    /// <summary>Where a scanned rock is this tick, and how it is turned.</summary>
    private void RockPose(in ScanGroup scan, out Double3 centre, out Quaternion rotation)
    {
        _rocks.Displacement(scan.Rock, _tick, out var offset, out _);
        centre = scan.Centre + offset;
        rotation = scan.Asteroid.Rotation;
    }

    /// <summary>
    /// Carries out a scan order for a tick: around the rock's shell to the drone's place and holding it (in place within
    /// the tolerance, out again past twice it), or, once the scan is over, around the rock to the side the ship is on
    /// and then home by an ordinary recall.
    /// </summary>
    private bool ScanOrder(ref TaskOrder order, int page)
    {
        ref var scan = ref _bayPages[page].Scan;
        if (scan.Phase == DroneScanPhase.None)
        {
            order.Phase = ScanOrderPhase.ScanLeaving;
            ToRecall(ref order);
            return Recall(ref order, page);
        }
        var rules = _content.Drones.Scans;
        RockPose(scan, out var centre, out var rotation);
        var local = HullRotation.Unrotate(_hulls.State(order.Actor.Index).Position.RelativeTo(centre), rotation);
        var step = rules.TravelStepRadians;
        var tolerance = rules.PlaceToleranceMetres;
        if (order.Phase == ScanOrderPhase.ScanLeaving || scan.Phase == DroneScanPhase.Returning)
        {
            order.Phase = ScanOrderPhase.ScanLeaving;
            var home = HullRotation.Unrotate(_hulls.State(order.Base.Index).Position.RelativeTo(centre), rotation);
            var length = home.Length();
            var goal = length > 1e-3f ? home * (scan.Shell / length) : local;
            var next = DroneScanning.Travel(local, goal, scan.Shell, step);
            if (next == goal)
            {
                ToRecall(ref order);
                return Recall(ref order, page);
            }
            Fly(order.Actor, centre + HullRotation.Rotate(next, rotation), tolerance);
            return true;
        }

        var target = DroneScanning.Travel(local, order.Point, scan.Shell, step);
        var distance = (local - order.Point).Length();
        order.Phase = distance <= tolerance || (order.Phase == ScanOrderPhase.InPlace && distance <= 2 * tolerance)
            ? ScanOrderPhase.InPlace : ScanOrderPhase.Moving;
        Fly(order.Actor, centre + HullRotation.Rotate(target, rotation), tolerance * 0.5f);
        return true;
    }

    /// <summary>Sends each player their newest drone scan results, a few a tick, each once.</summary>
    private void SendDroneScans(Connection connection)
    {
        if (!connection.CatalogueReady) return;
        var owner = DroneScanOwnerFor(connection.PlayerId);
        if (owner is null || owner.Count == 0) return;
        var sent = 0;
        for (var visited = 0; visited < owner.Count && sent < 2; visited++)
        {
            if (connection.DroneScanCursor >= owner.Count) connection.DroneScanCursor = 0;
            var index = connection.DroneScanCursor++;
            ref readonly var record = ref owner.Records[index];
            if (connection.DroneScanSent[index] == record.RecordedTick) continue;
            var writer = new Prospect.Engine.Net.NetWriter(_sendBuffer);
            DroneScanResultMessage.Write(ref writer, record);
            if (!connection.Transport.Send(connection.Peer, Prospect.Engine.Net.NetChannel.Reliable, writer.Written)) return;
            connection.DroneScanSent[index] = record.RecordedTick;
            sent++;
        }
    }

    /// <summary>In a world that is not saved, what a player's drones found is forgotten when they leave.</summary>
    private void ForgetDroneScans()
    {
        if (_save is not null) return;
        foreach (var owner in _droneScanOwners)
        {
            if (owner.Owner == 0) continue;
            var active = false;
            foreach (var connection in _connections) active |= connection.State == ConnectionState.Active && connection.PlayerId == owner.Owner;
            if (!active) { owner.Owner = 0; owner.Count = 0; }
        }
    }

    private bool CaptureDroneScans(Persistence.WorldSnapshot buffer)
    {
        var count = 0;
        foreach (var owner in _droneScanOwners) count += owner.Count;
        if (count > buffer.DroneScans.Length) return false;
        var cursor = 0;
        foreach (var owner in _droneScanOwners)
        {
            owner.Records.AsSpan(0, owner.Count).CopyTo(buffer.DroneScans.AsSpan(cursor));
            cursor += owner.Count;
        }
        buffer.DroneScanCount = count;
        return true;
    }

    /// <summary>
    /// The load rule: every saved result names an owner of the world, a rock this galaxy has, resources it knows, a
    /// time before the save, and no rock twice for one owner, at most <see cref="DroneScanRecordRules.KnowledgePerOwner"/>
    /// an owner. Anything else refuses the save.
    /// </summary>
    private void RestoreDroneScans(Persistence.WorldSnapshot world)
    {
        if ((uint)world.DroneScanCount > (uint)world.DroneScans.Length || world.DroneScanCount > _saveLimits.MaxDroneScans)
            throw new InvalidDataException("Too many saved drone scans.");
        var sector = new SectorContent();
        SectorCoord? previous = null;
        var resources = Galaxy.Resources.Length;
        foreach (var record in world.DroneScans.AsSpan(0, world.DroneScanCount))
        {
            if (!DroneScanRecordRules.Valid(record) || OwnerSlot(record.Owner) < 0 || record.RecordedTick >= world.NextWorldTick
                || record.Resource >= resources || record.Mix1 >= resources || record.Mix2 >= resources)
                throw new InvalidDataException("Invalid saved drone scan.");
            for (var v = 0; v < record.Map.VeinCount; v++)
                if (record.Map.Veins[v].Resource >= resources) throw new InvalidDataException("Invalid saved drone scan.");
            for (var p = 0; p < record.Map.PocketCount; p++)
                if (record.Map.Pockets[p].Resource >= resources) throw new InvalidDataException("Invalid saved drone scan.");
            if (previous != record.Sector) { Galaxy.Generate(record.Sector, sector); previous = record.Sector; }
            if (record.Index >= sector.AsteroidCount) throw new InvalidDataException("A saved drone scan names a missing asteroid.");
            var owner = DroneScanOwnerFor(record.Owner, true);
            if (owner is null || owner.Count == owner.Records.Length) throw new InvalidDataException("Too many saved drone scans for one owner.");
            for (var i = 0; i < owner.Count; i++)
                if (owner.Records[i].Sector == record.Sector && owner.Records[i].Index == record.Index)
                    throw new InvalidDataException("A saved drone scan names a rock twice.");
            owner.Records[owner.Count++] = record;
        }
    }
}
