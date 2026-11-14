using System.Numerics;
using System.Security.Cryptography;
using Prospect.Engine.Diagnostics;
using Prospect.Game.Persistence;
using Prospect.Game.World;

namespace Prospect.Game.Server;

/// <summary>
/// The saved world's side of the server: restoring a snapshot before the first tick, copying the world into the
/// capture buffer between two intervals, the durable owners and entities, and closing. A server without a world
/// store has none of it and runs as it always did. Everything here runs on the tick thread except the
/// constructor's restore, the status getters and <see cref="BeginClose"/>.
/// <para>
/// A saved world keeps its own clock. The scheduler's count of callbacks starts at zero in every run and goes on
/// while nobody plays; the world's interval is saved with the world, is bound at the start of each callback and
/// is only used up by a callback that simulates it. With nobody let in the callback still reads the network,
/// ends handshakes, saves and closes, and the interval stays where it is.
/// </para>
/// <para>
/// Owners are durable, connections are not. An owner is created once, and only that gives a starting fleet; a
/// connection that is let in as an existing owner is put back into the hull the owner flies, and when it leaves
/// the hulls stay. For now the only proof of being an owner is the in-process link of the host's own client,
/// so a saved world lets nobody else in: an account a server can verify comes first.
/// </para>
/// </summary>
public sealed partial class ServerHost
{
    private readonly SaveWorker? _save;
    private readonly SaveLimits _saveLimits = SaveLimits.Default;
    private readonly SavedOwner[] _owners = [];
    private readonly SaveCompatibility _compatibility;
    private readonly Guid _worldId;
    private readonly string _worldName = "";
    private readonly long _worldCreated;
    private readonly string[] _settingIds = [];
    private readonly long _autosaveInterval;
    private readonly long _retryInterval;
    private readonly int _stopTimeout;
    private int _ownerCount;
    private ulong _nextEntityId = 1;

    // The world's count of changes, which a save is compared with to tell whether the world has moved on.
    private long _dirty;

    // Set when something outside a simulated interval changed the world: an owner let in, a command taken.
    private bool _touched;
    private long _lastCapture;
    private long _refusedGrowth;
    private volatile bool _captureBroken;

    private int _closePhase;
    private int _closeFinal;
    private volatile bool _tickFaulted;

    internal enum ClaimState { None, Pending, Committed, Failed, Conflict, Rejected, Unknown, AlreadyLinked, Refused, Cancelled }

    private int _claimOwner = -1;
    private SavedAccount _claimAccount;
    private long _claimOperation;
    private ClaimState _claimState;

    // Tick-thread status. Operation identity is independent of capture revision and store generation.
    internal (ClaimState State, long OperationId) Claim => (_claimState, _claimOperation);

    /// <summary>
    /// Trusted server-side seam only, on the tick thread after proof verification. It performs no authentication.
    /// Only the verified private proof path calls this in production; no network handler can call it.
    /// </summary>
    internal ClaimState StageVerifiedLocalClaim(SavedAccount account)
    {
        ProcessClaimResult();
        if (_visibility != Prospect.Game.Protocol.ServerVisibility.Private
            || _save is null || _save.Latest.Halted || _captureBroken || _tickFaulted
            || Volatile.Read(ref _closePhase) != (int)ClosePhase.Open
            || _claimOwner >= 0 || account == default || !account.IsValid)
            return ClaimState.Refused;
        var slot = LocalOwner();
        if (slot < 0)
            return ClaimState.Refused;
        var localConnected = false;
        foreach (var connection in _connections)
            if (connection.State == ConnectionState.Active && connection.OwnerSlot == slot && connection.Transport.IsInProcess)
                localConnected = true;
        if (!localConnected)
            return ClaimState.Refused;
        if (_owners[slot].Account == account)
            return ClaimState.AlreadyLinked;
        if (_owners[slot].Account != default)
            return ClaimState.Refused;
        for (var i = 0; i < _ownerCount; i++)
            if (_owners[i].Account == account)
                return ClaimState.Refused;
        _claimOwner = slot;
        _claimAccount = account;
        _claimOperation = 0;
        _claimState = ClaimState.Pending;
        return _claimState;
    }

    private void ProcessClaimResult()
    {
        if (_claimOwner < 0 || _claimOperation == 0 || _save is null)
            return;
        var report = _save.Latest;
        if (report.OperationId != _claimOperation || report.Outcome is not { } outcome)
            return;
        _claimState = outcome switch
        {
            CommitOutcome.Committed => ClaimState.Committed,
            CommitOutcome.Failed => ClaimState.Failed,
            CommitOutcome.Conflict => ClaimState.Conflict,
            CommitOutcome.Rejected => ClaimState.Rejected,
            _ => ClaimState.Unknown,
        };
        if (outcome == CommitOutcome.Committed)
        {
            _owners[_claimOwner].Account = _claimAccount;
            _touched = true;
        }
        _claimOwner = -1;
        _claimAccount = default;
    }

    /// <summary>True when this server keeps its world in a store.</summary>
    public bool IsPersistent => _save is not null;

    /// <summary>Where the world's saves stand. Safe from any thread; takes no memory.</summary>
    public SaveStatus Save
    {
        get
        {
            if (_save is not { } worker)
                return SaveStatus.Off;
            var report = worker.Latest;
            var broken = _captureBroken;
            var state = report.Halted || broken
                ? SaveState.Halted
                : worker.BufferOwner switch
                {
                    SaveWorker.Owner.Capturing => SaveState.Capturing,
                    SaveWorker.Owner.Worker => SaveState.Writing,
                    _ => SaveState.Idle,
                };
            return new SaveStatus(
                state,
                Volatile.Read(ref _dirty) != report.SavedRevision,
                report.Generation,
                report.SavedTick,
                report.SavedAtUnixMs,
                report.Failures,
                broken ? "The world no longer fits a save." : report.Error,
                report.Backup,
                report.BackupGeneration,
                report.BackupError,
                Volatile.Read(ref _refusedGrowth)
            );
        }
    }

    /// <summary>Where closing the server stands. Safe from any thread; takes no memory.</summary>
    public CloseStatus Close
    {
        get
        {
            var phase = (ClosePhase)Volatile.Read(ref _closePhase);
            if (phase != ClosePhase.Completed)
                return new CloseStatus(phase, FinalCommitOutcome.Pending, CheckpointOutcome.NotAttempted, BackupOutcome.NotAttempted, null);
            var final = (FinalCommitOutcome)Volatile.Read(ref _closeFinal);
            var report = _save?.Latest;
            return final switch
            {
                FinalCommitOutcome.Committed => new CloseStatus(phase, final, report!.Checkpoint, report.FinalBackup, report.CloseError)
                {
                    CheckpointError = report.CheckpointError,
                    BackupError = report.BackupError,
                },
                FinalCommitOutcome.Failed => new CloseStatus(phase, final, CheckpointOutcome.NotAttempted, BackupOutcome.NotAttempted, _captureBroken ? "The world no longer fits a save." : report?.Error ?? "The final save failed."),
                FinalCommitOutcome.Unknown => new CloseStatus(phase, final, CheckpointOutcome.NotAttempted, BackupOutcome.NotAttempted, report?.Error ?? "The store could not tell whether the final save was committed."),
                FinalCommitOutcome.SkippedAfterFault => new CloseStatus(phase, final, CheckpointOutcome.NotAttempted, BackupOutcome.NotAttempted, "The simulation stopped inside an interval; the world was not saved in that state."),
                FinalCommitOutcome.Abandoned => new CloseStatus(phase, final, CheckpointOutcome.NotAttempted, BackupOutcome.NotAttempted, "The server was disposed before its final save was known to be complete."),
                _ => new CloseStatus(phase, final, CheckpointOutcome.NotAttempted, BackupOutcome.NotAttempted, null),
            };
        }
    }

    /// <summary>
    /// Asks the server to close: to stop the world after the interval it is in, save that state as the world's
    /// clean close and only then let its connections go. Returns at once and may be called from any thread, any
    /// number of times; watch <see cref="Close"/> for the end, then dispose the server. A server without a world
    /// store has nothing to save and is closed as soon as this returns.
    /// <para>
    /// From the next callback on nothing changes the world: the network is not read, so a client that
    /// disconnects during the close changes nothing that is saved, and no interval is simulated. The server
    /// must be ticking for the close to move on, by its own loop or, for a server that was never started, by
    /// whoever ticks it. Disposing before <see cref="CloseStatus.Completed"/> ends the wait and leaves the world
    /// not marked as closed cleanly.
    /// </para>
    /// </summary>
    public void BeginClose()
    {
        if (_save is null)
            CompleteClose(FinalCommitOutcome.NotPersisted);
        else
            Interlocked.CompareExchange(ref _closePhase, (int)ClosePhase.Freezing, (int)ClosePhase.Open);
    }

    /// <summary>
    /// Checks a snapshot's settings and catalogue against the content and returns the content as the saved
    /// world plays by it: its settings, and its whole catalogue in the saved order with the saved roles.
    /// Nothing of the current files' fleet is put back in: a design, a number or a role that the files have
    /// since changed stays as it was saved. Throws <see cref="InvalidDataException"/> for a save that does not
    /// belong to this content or does not hold together.
    /// </summary>
    private static GameContent RestoreContent(ServerConfig config, GameContent content, WorldSnapshot world)
    {
        if (world.Compatibility != SaveCompatibility.Of(content))
            throw new InvalidDataException("The world was saved with other content or by another build of the game; it was left as it is.");

        // The settings by id, each exactly once and each a value the catalogue would have produced itself.
        var catalog = content.SettingCatalog;
        var settings = catalog.Defaults;
        if (world.SettingCount != catalog.Definitions.Length || world.SettingIds.Length < world.SettingCount)
            throw new InvalidDataException("The saved world's settings are not this content's.");
        Span<bool> seen = stackalloc bool[WorldSettings.Capacity];
        for (var i = 0; i < world.SettingCount; i++)
        {
            var definition = catalog.Find(world.SettingIds[i]);
            if (definition is null || seen[definition.Index])
                throw new InvalidDataException("The saved world's settings are not this content's.");
            seen[definition.Index] = true;
            settings.Set(definition, world.SettingValues[i]);
            if (settings.Get(definition) != world.SettingValues[i])
                throw new InvalidDataException($"The saved world's setting {definition.Id} is outside its range.");
        }

        if (catalog.Sanitize(settings) != settings)
            throw new InvalidDataException("The saved world's settings do not hold together.");

        // A saved world is not reconfigured at launch. What the configuration leaves at its default is not a wish.
        if (config.Settings is { } asked && catalog.Sanitize(asked) != settings)
            throw new InvalidDataException("A saved world keeps the settings it was created with; they cannot be set when it is loaded.");
        if (config.GalaxySeed != ServerConfig.DefaultSeed && config.GalaxySeed != world.Seed)
            throw new InvalidDataException("A saved world keeps its seed; it cannot be set when the world is loaded.");
        if (config.SpawnSector != default && config.SpawnSector != world.SpawnSector)
            throw new InvalidDataException("A saved world keeps its spawn sector; it cannot be set when the world is loaded.");

        var count = world.DesignCount;
        if (count is < 1 or > SaveLimits.MaxDesigns || world.Designs.Length < count || world.DesignHashes.Length < count
            || (uint)world.PodDesign >= (uint)count || (uint)world.DroneDesign >= (uint)count || world.StartingFleet.Length > byte.MaxValue)
            throw new InvalidDataException("The saved design catalogue does not hold together.");
        foreach (var design in world.StartingFleet)
            if ((uint)design >= (uint)count) throw new InvalidDataException("The saved starting fleet names a design the catalogue does not have.");

        // A design is rebuilt from its bytes under the world's rules, as a client's is. One the files still
        // have byte for byte is the same design, so that object is used and its shell is not built again.
        var under = content.WithSettings(settings);
        var stock = new byte[under.Designs.Length][];
        for (var i = 0; i < stock.Length; i++)
            stock[i] = DesignBytes.Encode(under.Designs[i]);
        var designs = new HullDesign[count];
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        long total = 0;
        for (var i = 0; i < count; i++)
        {
            var bytes = world.Designs[i];
            total += bytes.Length;
            if (bytes.Length > under.BlockTransfers.MaxBytes || !under.BlockTransfers.AllowsCatalogue(total) || world.DesignHashes[i].Length != SHA256.HashSizeInBytes)
                throw new InvalidDataException("The saved design catalogue is larger than this content allows.");
            for (var j = 0; j < stock.Length && designs[i] is null; j++)
            {
                if (!bytes.AsSpan().SequenceEqual(stock[j]))
                    continue;
                SHA256.HashData(bytes, hash);
                if (!hash.SequenceEqual(world.DesignHashes[i]))
                    throw new InvalidDataException($"Saved design {i} does not match its hash.");
                designs[i] = under.Designs[j];
            }

            if (designs[i] is not null)
                continue;
            try
            {
                designs[i] = DesignBytes.Decode(bytes, world.DesignHashes[i], under);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                // Whatever the bytes make the decoder throw, the save is refused and says which design did it.
                throw new InvalidDataException($"Saved design {i} cannot be rebuilt: {e.Message}", e);
            }
        }

        return under.WithCatalogue(designs, world.PodDesign, world.DroneDesign, world.StartingFleet);
    }

    /// <summary>
    /// Puts a snapshot's owners, hulls and pushed asteroids into the empty world, in the saved order, and
    /// judges every one of them on the way. Block state is brought back by replaying the losses, flight figures
    /// are read off the restored sums, and motion, charge, jumps and orders are then left exactly as saved.
    /// Throws <see cref="InvalidDataException"/> for anything a world of this content could not have saved.
    /// </summary>
    private void RestoreWorld(WorldSnapshot world)
    {
        if (world.NextWorldTick is < 0 or > long.MaxValue / 2 || world.NextOwnerId == 0 || world.NextEntityId == 0)
            throw new InvalidDataException("The saved world's clock or counters do not hold together.");
        if (world.OwnerCount > _saveLimits.MaxOwners || world.HullCount > _saveLimits.MaxHulls || world.BlockCount > _saveLimits.MaxBlocks
            || world.RockCount > _saveLimits.MaxRocks || world.LossCount > world.BlockCount)
            throw new InvalidDataException("The saved world holds more than a world may.");
        for (var i = 0; i < _catalogue.Count; i++)
        {
            if (!_catalogue.Bytes[i].AsSpan().SequenceEqual(world.Designs[i]) || !_catalogue.Hashes[i].AsSpan().SequenceEqual(world.DesignHashes[i]))
                throw new InvalidDataException($"Saved design {i} is not in its canonical form.");
        }

        if (!SavedOwner.Valid(world.Owners.AsSpan(0, world.OwnerCount), world.NextOwnerId))
            throw new InvalidDataException("The saved owners do not hold together.");
        world.Owners.AsSpan(0, world.OwnerCount).CopyTo(_owners);

        _ownerCount = world.OwnerCount;
        RestoreCharts(world);
        RestoreModules(world);

        var entities = new HashSet<ulong>(world.HullCount);
        var blocks = 0;
        var losses = 0;
        for (var i = 0; i < world.HullCount; i++)
        {
            ref var saved = ref world.Hulls[i];
            if (saved.Design >= _content.Designs.Length)
                throw new InvalidDataException($"Saved hull {i} is of a design the catalogue does not have.");
            var design = _content.Designs[saved.Design];
            var owner = OwnerSlot(saved.Owner);

            // The ranges are checked against the running totals: a hull cannot claim another hull's blocks.
            if (owner < 0 || saved.Entity == 0 || saved.Entity >= world.NextEntityId || !entities.Add(saved.Entity)
                || saved.Blocks != design.BlockCount || saved.BlockStart != blocks || saved.LossStart != losses
                || (uint)saved.LossCount > (uint)saved.Blocks || blocks + saved.Blocks > world.BlockCount || losses + saved.LossCount > world.LossCount)
                throw new InvalidDataException($"Saved hull {i} does not hold together.");

            var handle = _hulls.Restore(saved.Owner, saved.Design, design, saved.State);
            ref var info = ref _hulls.Info(handle.Index);
            if (!_hulls.BlockStates.TryRestore(info.BlockState, world.Losses.AsSpan(losses, saved.LossCount), world.HitPoints.AsSpan(blocks, saved.Blocks), saved.HitTotal, out var error))
                throw new InvalidDataException($"Saved hull {i}: {error}.");
            if (_hulls.Revision(handle.Index) == (uint)saved.Blocks || _hulls.BlockStates.CoresLost(info.BlockState) > 0)
                throw new InvalidDataException($"Saved hull {i} is a hull that was destroyed.");
            blocks += saved.Blocks;
            losses += saved.LossCount;

            _field.RestoreFigures(handle.Index);
            error = JudgeHull(saved, _hulls.Flight(handle.Index), design, world.NextWorldTick);
            if (error is not null)
                throw new InvalidDataException($"Saved hull {i}: {error}.");

            info.Entity = saved.Entity;
            info.EscapeDrone = saved.EscapeDrone;
            info.DamageTick = saved.DamageTick;
            info.Order = saved.Order;
            info.Visual = saved.Visual;

            // Which hull is flown follows from the owner: one fact, kept in one place.
            info.Piloted = _owners[owner].Controlled == saved.Entity;
        }

        if (blocks != world.BlockCount || losses != world.LossCount)
            throw new InvalidDataException("The saved hulls do not add up to the saved blocks.");

        for (var i = 0; i < _ownerCount; i++)
        {
            if (_owners[i].Controlled != 0 && !FindEntity(_owners[i].Controlled, _owners[i].Id).IsValid)
                throw new InvalidDataException("A saved owner flies a hull that is not theirs or is not there.");
        }

        // What the running world guarantees must hold from the first interval: every owner can still be put in
        // a drone, whatever else the world holds, because that drone is created without asking. A drone an owner
        // already flies is that owner's; every other hull and block must leave the room HasRoom keeps back.
        long normalHulls = world.HullCount;
        long normalBlocks = world.BlockCount;
        var droneBlocks = _content.Designs[_content.DroneDesign].BlockCount;
        for (var i = 0; i < _ownerCount; i++)
        {
            var flown = _owners[i].Controlled != 0 ? FindEntity(_owners[i].Controlled, _owners[i].Id) : default;
            if (!flown.IsValid || !_hulls.Info(flown.Index).EscapeDrone)
                continue;
            normalHulls--;
            normalBlocks -= Math.Min(droneBlocks, _hulls.BlockStates.Blocks(_hulls.Info(flown.Index).BlockState));
        }

        if (normalHulls > _saveLimits.MaxHulls - _saveLimits.MaxOwners || normalBlocks > _saveLimits.MaxBlocks - (long)_saveLimits.MaxOwners * droneBlocks)
            throw new InvalidDataException("The saved world leaves no room for the drone a pilot is put in when their hull is gone.");

        for (var i = 0; i < world.RockCount; i++)
        {
            ref readonly var rock = ref world.Rocks[i];

            // Against the list this seed and this generator actually make: an index means nothing against another.
            if (!IsFinite(rock.Offset) || !IsFinite(rock.Velocity) || rock.StartTick > world.NextWorldTick
                || (uint)rock.Rock.Index >= (uint)_rocks.Sector(rock.Rock.Sector).AsteroidCount
                || _rocks.MovedIn(rock.Rock.Sector) is { } moved && moved.Entries[rock.Rock.Index] > 0
                || !_rocks.Set(rock))
                throw new InvalidDataException($"Saved asteroid {i} is not an asteroid of this galaxy, is named twice, or is one more than a world may move.");
        }

        _nextPlayerId = world.NextOwnerId;
        _nextEntityId = world.NextEntityId;
        _tick = world.NextWorldTick;
    }

    /// <summary>
    /// Why a saved hull's motion cannot be what a world of this content saved, or null. Charge is held to the
    /// figures the hull has after its losses, exactly: the simulation clamps it to them every time they change.
    /// Hull points are held loosely: an intact hull's come from the design's figures and a damaged hull's from
    /// its hit total, which are different arithmetic.
    /// </summary>
    private static string? JudgeHull(in SavedHull hull, in FlightParams flight, HullDesign design, long nextTick)
    {
        ref readonly var state = ref hull.State;
        var orientation = state.Orientation.LengthSquared();
        if (!double.IsFinite(state.Position.X) || !double.IsFinite(state.Position.Y) || !double.IsFinite(state.Position.Z)
            || !IsFinite(state.Velocity) || !IsFinite(state.AngularVelocity) || !(MathF.Abs(orientation - 1f) < 1e-3f))
            return "its place or motion is not a number";
        if (!(state.Heat >= 0f && state.Heat <= 1f) || !(state.Energy >= 0f && state.Energy <= flight.EnergyStorage)
            || !(state.Shield >= 0f && state.Shield <= flight.ShieldCapacity) || !(state.ShieldCooldown >= 0f) || !float.IsFinite(state.ShieldCooldown))
            return "its charge is outside what the hull holds";
        var intact = Math.Max(design.Flight.HullPoints, (float)design.IntactHitTotal);
        if (!(state.Hull >= 0f && state.Hull <= intact * 1.0001f))
            return "its hull points are outside what the design has";
        if (state.ShieldHit.AgeTicks > ShieldHit.MaxAgeTicks || state.ShieldChangeTicks > ShieldHit.MaxAgeTicks)
            return "its shield marks are older than a mark gets";
        if (state.Jump > JumpPhase.Arrive || !(state.JumpTimer >= 0f) || !float.IsFinite(state.JumpTimer) || !(state.JumpCalculation >= 0f) || !float.IsFinite(state.JumpCalculation)
            || !(state.JumpTunnelSeconds >= 0f && state.JumpTunnelSeconds <= HullSim.MaxJumpTravelSeconds) || !(state.JumpTunnelElapsed >= 0.0) || !double.IsFinite(state.JumpTunnelElapsed))
            return "its jump does not hold together";
        if (hull.Order.Kind > OrderKind.Jump || !double.IsFinite(hull.Order.Target.X) || !double.IsFinite(hull.Order.Target.Y) || !double.IsFinite(hull.Order.Target.Z))
            return "its order does not hold together";
        if (hull.DamageTick > nextTick || hull.Visual.Spark.Tick > nextTick && hull.Visual.Spark.Sequence != 0)
            return "it was damaged in an interval the world has not reached";
        if (hull.Visual.Count > VisualDamageState.Capacity || hull.Visual.Spark.Sequence != 0 && !hull.Visual.Spark.Contact.Valid(design))
            return "its dents do not hold together";
        for (var i = 0; i < hull.Visual.Count; i++)
        {
            if (!hull.Visual.Dents[i].Valid(design))
                return "its dents do not hold together";
            for (var j = 0; j < i; j++)
                if (hull.Visual.Dents[j].Block == hull.Visual.Dents[i].Block) return "its dents do not hold together";
        }

        return null;
    }

    private static bool IsFinite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    /// <summary>
    /// Copies the world as it stands between two intervals into the capture buffer. Takes no memory and looks
    /// at nothing but the server's own state. The catalogue is taken by its arrays, which are replaced and never
    /// written to, with the count that goes with them. False if the world does not fit the buffer, which the
    /// checks before every growth are there to make impossible: nothing is cut to fit, the capture is given up.
    /// </summary>
    private bool Capture(WorldSnapshot buffer)
    {
        if (_ownerCount > buffer.Owners.Length || _hulls.Count > buffer.Hulls.Length || _hulls.BlockTotal > buffer.HitPoints.Length
            || _rocks.Deltas.Length > buffer.Rocks.Length || _chartCount > buffer.Charts.Length)
            return false;

        if (!CaptureModules(buffer)) return false;
        _charts.AsSpan(0, _chartCount).CopyTo(buffer.Charts);
        buffer.ChartCount = _chartCount;
        buffer.Compatibility = _compatibility;
        buffer.WorldId = _worldId;
        buffer.Name = _worldName;
        buffer.CreatedUnixMs = _worldCreated;
        buffer.Seed = _galaxySeed;
        buffer.SpawnSector = _spawnSector;
        buffer.SettingIds = _settingIds;
        buffer.SettingCount = _settingIds.Length;
        var definitions = _content.SettingCatalog.Definitions;
        var settings = _content.Settings;
        for (var i = 0; i < definitions.Length; i++)
            buffer.SettingValues[i] = settings.Get(definitions[i]);

        buffer.NextWorldTick = _tick;
        buffer.CapturedUnixMs = _clock.GetUtcNow().ToUnixTimeMilliseconds();
        buffer.NextOwnerId = _nextPlayerId;
        buffer.NextEntityId = _nextEntityId;

        // One read of each: the two arrays were replaced together by this thread, so they are one catalogue.
        buffer.Designs = _catalogue.Bytes;
        buffer.DesignHashes = _catalogue.Hashes;
        buffer.DesignCount = buffer.Designs.Length;
        buffer.PodDesign = _content.PodDesign;
        buffer.DroneDesign = _content.DroneDesign;
        buffer.StartingFleet = _content.StartingFleet;

        buffer.HullCount = 0;
        buffer.BlockCount = 0;
        buffer.LossCount = 0;
        for (var i = 0; i < _hulls.Slots; i++)
        {
            if (!_hulls.IsAliveAt(i))
                continue;
            ref var info = ref _hulls.Info(i);
            var points = _hulls.BlockStates.HitPoints(info.BlockState);
            var lost = _hulls.BlockStates.Losses(info.BlockState);
            ref var saved = ref buffer.Hulls[buffer.HullCount++];
            saved.Entity = info.Entity;
            saved.Owner = info.Owner;
            saved.Design = info.Design;
            saved.EscapeDrone = info.EscapeDrone;
            saved.DamageTick = info.DamageTick;
            saved.State = _hulls.State(i);
            saved.Order = info.Order;
            saved.Visual = info.Visual;
            saved.HitTotal = _hulls.BlockStates.HitTotal(info.BlockState);
            saved.BlockStart = buffer.BlockCount;
            saved.Blocks = points.Length;
            saved.LossStart = buffer.LossCount;
            saved.LossCount = lost.Length;
            points.CopyTo(buffer.HitPoints.AsSpan(buffer.BlockCount));
            lost.CopyTo(buffer.Losses.AsSpan(buffer.LossCount));
            buffer.BlockCount += points.Length;
            buffer.LossCount += lost.Length;
        }

        // An owner whose hull went while they were away is saved as flying nothing: they get a drone when let in.
        for (var i = 0; i < _ownerCount; i++)
        {
            buffer.Owners[i] = _owners[i];
            if (_owners[i].Controlled != 0 && !FindEntity(_owners[i].Controlled, _owners[i].Id).IsValid)
                buffer.Owners[i].Controlled = 0;
        }

        buffer.OwnerCount = _ownerCount;
        var rocks = _rocks.Deltas;
        rocks.CopyTo(buffer.Rocks);
        buffer.RockCount = rocks.Length;
        return true;
    }

    /// <summary>
    /// Once per callback of a saved world, after its interval or instead of it: starts a save when the world
    /// has changed, the time has come and the buffer is free. A save that failed is made again, of the state the
    /// world has by then, after a shorter wait. Nothing is queued: while a save is being written this does
    /// nothing, and the world's changes wait in the world.
    /// </summary>
    private void ServiceSave(long now)
    {
        var worker = _save!;
        ProcessClaimResult();
        var report = worker.Latest;
        if (_claimOwner >= 0)
        {
            if (_claimOperation != 0)
                return;
            if (Volatile.Read(ref _closePhase) != (int)ClosePhase.Open)
                return;
            if (report.Halted || _captureBroken)
            {
                _claimState = report.Unknown ? ClaimState.Unknown : report.Outcome == CommitOutcome.Conflict ? ClaimState.Conflict : ClaimState.Rejected;
                _claimOwner = -1;
                _claimAccount = default;
                return;
            }
            if (!worker.TryBeginCapture(out var candidate))
                return;
            report = worker.Latest;
            if (report.Halted)
            {
                worker.Abandon();
                _claimState = report.Unknown ? ClaimState.Unknown : report.Outcome == CommitOutcome.Conflict ? ClaimState.Conflict : ClaimState.Rejected;
                _claimOwner = -1;
                _claimAccount = default;
                return;
            }
            if (!Capture(candidate))
            {
                worker.Abandon();
                _captureBroken = true;
                _claimState = ClaimState.Rejected;
                _claimOwner = -1;
                return;
            }
            candidate.Owners[_claimOwner].Account = _claimAccount;
            _claimOperation = worker.Submit(false, _dirty);
            _lastCapture = now;
            return;
        }
        if (report.Halted || _captureBroken || _dirty == report.SavedRevision)
            return;
        if (now - _lastCapture < (report.Failures > 0 ? _retryInterval : _autosaveInterval))
            return;
        if (!worker.TryBeginCapture(out var buffer))
            return;

        _lastCapture = now;
        if (Capture(buffer))
        {
            worker.Submit(final: false, _dirty);
            return;
        }

        worker.Abandon();
        _captureBroken = true;
        Logs.Sim.Error("The world no longer fits a save; saving has stopped and the last completed save stands.");
    }

    /// <summary>
    /// A callback of a saved world that was asked to close. The world is frozen: this is all that runs. It
    /// waits for the buffer an earlier save may still hold, captures the frozen state once, and waits for the
    /// worker to commit it; then the connections are let go and the loop is told to stop.
    /// </summary>
    private void ServiceClose()
    {
        var worker = _save!;
        ProcessClaimResult();
        if (_claimOwner >= 0)
        {
            if (_claimOperation != 0)
                return;
            _claimOwner = -1;
            _claimAccount = default;
            _claimState = ClaimState.Cancelled;
        }
        switch ((ClosePhase)Volatile.Read(ref _closePhase))
        {
            case ClosePhase.Freezing:
                if (worker.Latest.Halted || _captureBroken)
                {
                    // Saving stopped earlier in this run: nothing more is written, and the close says why.
                    EndClose(worker.Latest.Unknown ? FinalCommitOutcome.Unknown : FinalCommitOutcome.Failed);
                }
                else if (worker.TryBeginCapture(out var buffer))
                {
                    if (Capture(buffer))
                    {
                        worker.Submit(final: true, _dirty);
                        Volatile.Write(ref _closePhase, (int)ClosePhase.Saving);
                    }
                    else
                    {
                        worker.Abandon();
                        _captureBroken = true;
                        EndClose(FinalCommitOutcome.Failed);
                    }
                }

                break;

            case ClosePhase.Saving:
                var report = worker.Latest;
                if (report.FinalDone)
                    EndClose(report.FinalCommitted ? FinalCommitOutcome.Committed : report.Unknown ? FinalCommitOutcome.Unknown : FinalCommitOutcome.Failed);
                break;
        }
    }

    private void EndClose(FinalCommitOutcome outcome)
    {
        CompleteClose(outcome);
        DisconnectAll();
        Logs.Sim.Info(outcome == FinalCommitOutcome.Committed ? $"World saved and closed at tick {_tick}" : $"World closed at tick {_tick} without a final save");

        // From the loop's own thread this only asks; the loop ends after this callback.
        _loop.Stop();
    }

    /// <summary>Ends the close with an outcome, once. The outcome is written before the phase that says it is there.</summary>
    private void CompleteClose(FinalCommitOutcome outcome)
    {
        if (Volatile.Read(ref _closePhase) == (int)ClosePhase.Completed)
            return;
        Volatile.Write(ref _closeFinal, (int)(_save is null ? FinalCommitOutcome.NotPersisted : outcome));
        Volatile.Write(ref _closePhase, (int)ClosePhase.Completed);
    }

    /// <summary>
    /// Stops the save worker when the server is disposed and settles what the close came to. The worker writes
    /// the capture it already holds, a final one included, and is waited for a bounded time: a store that does
    /// not return is left to its background thread, and the close is reported as abandoned.
    /// </summary>
    private void StopSaving()
    {
        if (_save is not { } worker)
        {
            CompleteClose(FinalCommitOutcome.NotPersisted);
            return;
        }

        worker.RequestStop();
        var stopped = worker.Join(_stopTimeout);
        var report = worker.Latest;
        CompleteClose(
            (ClosePhase)Volatile.Read(ref _closePhase) switch
            {
                _ when !stopped => FinalCommitOutcome.Abandoned,
                ClosePhase.Saving when report.FinalDone => report.FinalCommitted ? FinalCommitOutcome.Committed : report.Unknown ? FinalCommitOutcome.Unknown : FinalCommitOutcome.Failed,
                ClosePhase.Saving or ClosePhase.Freezing => FinalCommitOutcome.Abandoned,
                _ => _tickFaulted ? FinalCommitOutcome.SkippedAfterFault : FinalCommitOutcome.NotRequested,
            }
        );
    }

    /// <summary>The slot of the owner with a number, or -1.</summary>
    private int OwnerSlot(uint id)
    {
        for (var i = 0; i < _ownerCount; i++)
            if (_owners[i].Id == id) return i;
        return -1;
    }

    /// <summary>The slot of the world's local owner, or -1 if it has none yet.</summary>
    private int LocalOwner()
    {
        for (var i = 0; i < _ownerCount; i++)
            if (_owners[i].Kind == OwnerKind.Local) return i;
        return -1;
    }

    /// <summary>True while a connection is let in as the owner in a slot.</summary>
    private bool OwnerBound(int slot)
    {
        foreach (var connection in _connections)
            if (connection.State == ConnectionState.Active && connection.OwnerSlot == slot) return true;
        return false;
    }

    /// <summary>The live hull with a durable name, if it belongs to an owner; an invalid handle otherwise.</summary>
    private HullHandle FindEntity(ulong entity, uint owner)
    {
        for (var i = 0; i < _hulls.Slots; i++)
        {
            if (_hulls.IsAliveAt(i) && _hulls.Info(i).Entity == entity)
                return _hulls.Info(i).Owner == owner ? _hulls.HandleAt(i) : default;
        }

        return default;
    }

    /// <summary>
    /// Whether a saved world has room for this many more hulls and blocks. Asked before anything is created, so
    /// the world never holds more than its save has room for. One drone per owner is kept back from every other
    /// use: a pilot whose hull is destroyed is put in a drone whatever else the world holds, and at most one
    /// such drone exists per owner at a time. A world that is not saved has no such bound. A refusal is
    /// counted here, for <see cref="SaveStatus.RefusedGrowth"/>: whoever asks only has to not grow.
    /// <para>
    /// The rule this keeps, and <see cref="RestoreWorld"/> checks of a save: leave out, for each owner, the
    /// escape drone they fly, up to a stock drone's blocks; every other hull and block fits the limits less one
    /// stock drone per owner. Every way a hull comes into the world keeps it: a fleet, a launched drone and a
    /// larger edit ask here first, and the drone of a pilot whose hull is gone, or of an owner let in without
    /// one, becomes that owner's one drone. So the unasked drone always fits, and a capture never overflows.
    /// </para>
    /// </summary>
    private bool HasRoom(int hulls, long blocks)
    {
        if (_save is null)
            return true;
        if (_hulls.Count + hulls <= _saveLimits.MaxHulls - _saveLimits.MaxOwners
            && _hulls.BlockTotal + blocks <= _saveLimits.MaxBlocks - (long)_saveLimits.MaxOwners * _content.Designs[_content.DroneDesign].BlockCount)
            return true;
        Volatile.Write(ref _refusedGrowth, _refusedGrowth + 1);
        return false;
    }

    /// <summary>Whether the world can take a new owner with a whole starting fleet.</summary>
    private bool HasRoomForOwner()
    {
        long blocks = _content.Pod.BlockCount;
        foreach (var design in _content.StartingFleet)
            blocks += _content.Designs[design].BlockCount;
        if (_ownerCount < _saveLimits.MaxOwners && _nextPlayerId != uint.MaxValue)
            return HasRoom(1 + _content.StartingFleet.Length, blocks);
        Volatile.Write(ref _refusedGrowth, _refusedGrowth + 1);
        return false;
    }

    /// <summary>
    /// Creates a hull and gives it its durable name: a new one, or <paramref name="entity"/> for the hull that
    /// replaces another in a repair or an edit and is still that ship. Creating a hull can move the hull
    /// storage: hold no reference into it across this call.
    /// </summary>
    private HullHandle CreateHull(uint owner, byte design, Double3 position, ulong entity = 0)
    {
        var handle = _hulls.Create(owner, design, _content.Designs[design], position);
        _hulls.Info(handle.Index).Entity = entity != 0 ? entity : _nextEntityId++;
        return handle;
    }

    /// <summary>Puts a connection at the controls of a hull, and writes it down for the owner the connection is let in as.</summary>
    private void SetHull(Connection connection, HullHandle hull)
    {
        connection.Hull = hull;
        if (connection.OwnerSlot >= 0)
            _owners[connection.OwnerSlot].Controlled = _hulls.Info(hull.Index).Entity;
    }

    /// <summary>
    /// Puts a connection that was let in as an existing owner back into the hull the owner flies. This is not a
    /// change of control: the hull keeps its order, so a jump that was charging when the world was saved goes on
    /// charging. Only what belongs to a connection starts over, the order counter with it. An owner whose hull
    /// is gone gets a drone from the spawn point, as a pilot whose drone was destroyed does.
    /// </summary>
    private void Rebind(Connection connection)
    {
        var owner = _owners[connection.OwnerSlot];
        var hull = owner.Controlled != 0 ? FindEntity(owner.Controlled, owner.Id) : default;
        if (!hull.IsValid)
        {
            hull = CreateHull(owner.Id, (byte)_content.DroneDesign, SpawnPoint);
            ref var drone = ref _hulls.Info(hull.Index);
            drone.EscapeDrone = true;
            drone.DamageTick = _tick;
        }

        ref var info = ref _hulls.Info(hull.Index);
        info.Piloted = true;
        info.OrderId = 0;
        SetHull(connection, hull);
    }
}
