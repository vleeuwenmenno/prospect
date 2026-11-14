using Prospect.Game.World;

namespace Prospect.Game.Server;

public sealed partial class ServerHost
{
    private sealed class ModuleOwner
    {
        internal uint Owner;
        internal int Count;
        internal long CooldownUntilTick;
        internal readonly ModuleObservation[] Records = new ModuleObservation[ModuleScanRules.KnowledgePerOwner];
    }

    private ModuleSurveyWorker? _moduleWorker;
    private ModuleOwner[] _moduleOwners = [];

    private void InitializeModules(int ownerCapacity)
    {
        _moduleOwners = new ModuleOwner[ownerCapacity];
        for (var i = 0; i < ownerCapacity; i++) _moduleOwners[i] = new();
        _moduleWorker = new ModuleSurveyWorker(Galaxy, _connections.Length);
    }

    private ModuleOwner? ModuleOwnerFor(uint owner, bool create = false)
    {
        foreach (var entry in _moduleOwners) if (entry.Owner == owner) return entry;
        if (create)
            foreach (var entry in _moduleOwners)
                if (entry.Owner == 0) { entry.Owner = owner; return entry; }
        return null;
    }

    /// <summary>Test-visible authoritative observations, never an unfiltered client publication.</summary>
    internal ReadOnlySpan<ModuleObservation> ModuleKnowledge(uint owner)
    {
        var entry = ModuleOwnerFor(owner);
        return entry is null ? [] : entry.Records.AsSpan(0, entry.Count);
    }

    /// <summary>Routes an admitted owner's request through the same execution path as its network intent.</summary>
    internal void RequestModuleScan(uint owner, ModuleScanRequest request)
    {
        foreach (var connection in _connections)
            if (connection.State == ConnectionState.Active && connection.PlayerId == owner)
            { StartModuleScan(connection, request); return; }
    }

    private ModuleEquipment InstalledModule(Connection connection, ModuleFamily family)
    {
        if (!_hulls.IsAlive(connection.Hull)) return default;
        ref var info = ref _hulls.Info(connection.Hull.Index);
        _content.Designs[info.Design].Figures(_hulls.BlockStates.Sums(info.BlockState), out _, out var stats);
        return stats.Module(family);
    }

    private void StartModuleScan(Connection connection, ModuleScanRequest request)
    {
        ref var scan = ref connection.ModuleScan;
        if (scan.State is ModuleScanState.Preparing or ModuleScanState.Scanning)
        { scan.Refusal = ModuleScanState.Busy; return; }
        var serial = unchecked(scan.Serial + 1);
        scan = new ModuleScanView
        {
            Serial = serial,
            Hull = connection.Hull.Packed,
            Family = request.Family,
            Sector = request.Sector,
            Index = request.Index,
            State = ModuleScanState.InvalidTarget
        };
        if (request.Family is < ModuleFamily.GasScanner or > ModuleFamily.AsteroidLocator || request.Index < 0) return;
        var owner = ModuleOwnerFor(connection.PlayerId, true);
        if (owner is null) { scan.State = ModuleScanState.KnowledgeLimit; return; }
        scan.CooldownUntilTick = owner.CooldownUntilTick;
        if (_tick < scan.CooldownUntilTick) { scan.State = ModuleScanState.Cooldown; return; }
        var equipment = InstalledModule(connection, request.Family);
        if (equipment.Block == 0) { scan.State = ModuleScanState.NoEquipment; return; }
        ref var state = ref _hulls.State(connection.Hull.Index);
        if (state.Energy < equipment.PowerDraw / Prospect.Engine.Time.TickLoop.SimulationHz)
        { scan.State = ModuleScanState.NoPower; return; }
        scan.Origin = state.Position;
        scan.Quality = equipment.Quality;
        // Refuse remote generation requests before dispatch. A cloud reaches at most one neighboring sector.
        if (request.Family != ModuleFamily.AsteroidLocator)
        {
            var target = Galaxy.CenterOf(request.Sector);
            if (!ModuleScanRules.InRange(state.Position, target, Galaxy.SectorSize * 2, equipment.Range))
            { scan.State = ModuleScanState.OutOfRange; return; }
        }
        if (equipment.Range > Galaxy.SectorSize) { scan.State = ModuleScanState.OutOfRange; return; }
        var index = Array.IndexOf(_connections, connection);
        if (!_moduleWorker!.Start(index, request, state.Position, equipment.Range, connection.PlayerId))
        { scan.State = ModuleScanState.Busy; return; }
        connection.ModuleEquipment = equipment;
        scan.State = ModuleScanState.Preparing;
    }

    private void EndModuleScan(Connection connection, ModuleScanState state, long tick)
    {
        ref var scan = ref connection.ModuleScan;
        scan.State = state;
        var owner = ModuleOwnerFor(connection.PlayerId);
        scan.CooldownUntilTick = tick + ModuleScanRules.Ticks(connection.ModuleEquipment.CooldownSeconds);
        if (owner is not null) owner.CooldownUntilTick = scan.CooldownUntilTick;
    }

    private void TickModules(long tick)
    {
        if (_moduleWorker is null) return;
        for (var index = 0; index < _connections.Length; index++)
        {
            var connection = _connections[index];
            var slot = _moduleWorker.Slots[index];
            var ready = Volatile.Read(ref slot.State) == 3;
            ref var scan = ref connection.ModuleScan;
            if (connection.State != ConnectionState.Active || scan.State is not (ModuleScanState.Preparing or ModuleScanState.Scanning))
            { if (ready) Volatile.Write(ref slot.State, 0); continue; }
            if (!_hulls.IsAlive(connection.Hull) || scan.Hull != connection.Hull.Packed
                || InstalledModule(connection, scan.Family) != connection.ModuleEquipment)
            { EndModuleScan(connection, ModuleScanState.Cancelled, tick); if (ready) Volatile.Write(ref slot.State, 0); continue; }
            if (!ready) continue;
            if (slot.Owner != connection.PlayerId)
            { EndModuleScan(connection, ModuleScanState.Cancelled, tick); Volatile.Write(ref slot.State, 0); continue; }
            if (slot.Failure != ModuleScanState.Idle)
            { EndModuleScan(connection, slot.Failure, tick); Volatile.Write(ref slot.State, 0); continue; }
            ref var hull = ref _hulls.State(connection.Hull.Index);
            if (hull.Jump != JumpPhase.None)
            { EndModuleScan(connection, ModuleScanState.Cancelled, tick); Volatile.Write(ref slot.State, 0); continue; }
            if (scan.Family != ModuleFamily.AsteroidLocator && slot.Count == 1)
            {
                var target = slot.Results[0];
                var position = target.Position;
                if (scan.Family == ModuleFamily.AsteroidScanner)
                { _rocks.Displacement(new(target.Sector, target.Index), tick, out var offset, out _); position += offset; }
                if (!ModuleScanRules.InRange(hull.Position, position, target.Radius, connection.ModuleEquipment.Range))
                { EndModuleScan(connection, ModuleScanState.OutOfRange, tick); Volatile.Write(ref slot.State, 0); continue; }
            }
            if (scan.State == ModuleScanState.Preparing)
            {
                scan.StartedTick = tick;
                scan.CompletesTick = tick + ModuleScanRules.Ticks(connection.ModuleEquipment.ScanSeconds);
                scan.State = ModuleScanState.Scanning;
            }
            if (tick < scan.CompletesTick)
            {
                if (!ModuleScanRules.Spend(ref hull.Energy, connection.ModuleEquipment))
                { EndModuleScan(connection, ModuleScanState.NoPower, tick); Volatile.Write(ref slot.State, 0); }
                continue;
            }
            var owner = ModuleOwnerFor(connection.PlayerId)!;
            scan.Partial = slot.Partial;
            // Generation filters a bounded candidate set. Moved rocks may enter from outside it; mark that limit honestly.
            if (scan.Family == ModuleFamily.AsteroidLocator && !_rocks.Deltas.IsEmpty) scan.Partial = true;
            var needed = 0;
            for (var i = 0; i < slot.Count; i++)
            {
                ref var record = ref slot.Results[i];
                if (record.Family != ModuleFamily.GasScanner)
                {
                    _rocks.Displacement(new(record.Sector, record.Index), tick, out var offset, out _);
                    record.Position += offset;
                    if (record.Family == ModuleFamily.AsteroidLocator
                        && !ModuleScanRules.InRange(scan.Origin, record.Position, 0, connection.ModuleEquipment.Range))
                    { record.Owner = 0; continue; }
                }
                if (FindObservation(owner, record) == owner.Count) needed++;
            }
            if (!HasRoomForModuleKnowledge(owner, needed))
            { EndModuleScan(connection, ModuleScanState.KnowledgeLimit, tick); Volatile.Write(ref slot.State, 0); continue; }
            for (var i = 0; i < slot.Count; i++)
            {
                var record = slot.Results[i];
                if (record.Owner == 0) continue;
                record.RecordedTick = tick;
                ModuleScanRules.Disclose(ref record, connection.ModuleEquipment);
                var at = FindObservation(owner, record);
                owner.Records[at] = record;
                if (at == owner.Count) owner.Count++;
            }
            _touched = true;
            EndModuleScan(connection, ModuleScanState.Completed, tick);
            Volatile.Write(ref slot.State, 0);
        }
        if (_save is null)
            foreach (var owner in _moduleOwners)
            {
                if (owner.Owner == 0) continue;
                var active = false;
                foreach (var connection in _connections) active |= connection.State == ConnectionState.Active && connection.PlayerId == owner.Owner;
                if (!active) { owner.Owner = 0; owner.Count = 0; owner.CooldownUntilTick = 0; }
            }
    }

    private bool HasRoomForModuleKnowledge(ModuleOwner owner, int needed)
    {
        if (owner.Count + needed <= owner.Records.Length) return true;
        if (_save is not null) Volatile.Write(ref _refusedGrowth, _refusedGrowth + 1);
        return false;
    }

    private static int FindObservation(ModuleOwner owner, in ModuleObservation record)
    {
        for (var i = 0; i < owner.Count; i++) if (owner.Records[i].SameKey(record)) return i;
        return owner.Count;
    }
}
