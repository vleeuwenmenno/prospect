using System.Numerics;
using Prospect.Game.Protocol;
using Prospect.Game.World;

namespace Prospect.Game.Client;

/// <summary>What the player is asking of their hull, gathered by the input thread between simulation ticks.</summary>
public struct PlayerControls
{
    /// <summary>Axes in -1..1: strafe right, climb, forward.</summary>
    public float Strafe;
    public float Vertical;
    public float Forward;

    /// <summary>Roll rate in -1..1, positive rolling right.</summary>
    public float Roll;

    /// <summary>The direction the player is looking, as a unit vector in the world.</summary>
    public Vector3 Aim;

    /// <summary>When set, the hull turns its nose toward <see cref="Aim"/>; otherwise it holds its heading.</summary>
    public bool Align;

    public bool Boost;
    public bool ToggleDampeners;
}

/// <summary>
/// Carries the player's intent from the input thread to the client simulation. Held values replace each other;
/// one-shot values (toggles, orders) accumulate until the simulation takes them, so nothing
/// that happens between two ticks is lost.
/// </summary>
public sealed class PlayerInputBridge
{
    private readonly Lock _lock = new();
    private ScanIntent _scan;
    private bool _modulePending;
    private ModuleScanRequest _moduleRequest;

    /// <summary>Requests local composition of the cloud identified by its anchor sector.</summary>
    public void StartGasScan(SectorCoord anchor) => QueueModuleScan(new(ModuleFamily.GasScanner, anchor));
    /// <summary>Requests analysis of the targeted asteroid's stable sector/index identity.</summary>
    public void StartAsteroidScan(RockRef target) => QueueModuleScan(new(ModuleFamily.AsteroidScanner, target.Sector, target.Index));
    /// <summary>Requests a locator pulse centered on the controlled hull.</summary>
    public void PulseAsteroidLocator() => QueueModuleScan(new(ModuleFamily.AsteroidLocator, default));
    private void QueueModuleScan(ModuleScanRequest request)
    { lock (_lock) { _moduleRequest = request; _modulePending = true; } }
    internal bool ConsumeModuleScan(out ModuleScanRequest request)
    { lock (_lock) { request = _moduleRequest; var pending = _modulePending; _modulePending = false; return pending; } }
    internal void RetryModuleScan(ModuleScanRequest request)
    { lock (_lock) { if (!_modulePending) { _moduleRequest = request; _modulePending = true; } } }

    private bool _newShip;
    private ShipName _shipName;
    /// <summary>Queues creation. False means the name is invalid; server feedback is in SessionSnapshot.NewShip.</summary>
    public bool RequestNewShip(ReadOnlySpan<char> name)
    {
        ShipName text = default;
        if (!text.Set(name)) return false;
        lock (_lock) { _shipName = text; _newShip = true; }
        return true;
    }
    internal bool ConsumeNewShip(out ShipName name)
    {
        lock (_lock) { name = _shipName; var pending = _newShip; _newShip = false; return pending; }
    }
    /// <summary>Asks the server to scan the sector the controlled hull is in. Replaces a cancel not yet sent.</summary>
    public void RequestScan() { lock (_lock) _scan = ScanIntent.Start; }

    /// <summary>Asks the server to cancel the player's own running scan. Replaces a request not yet sent.</summary>
    public void CancelScan() { lock (_lock) _scan = ScanIntent.Cancel; }

    /// <summary>Takes the one scan intent queued since the last call; a start and a cancel never both go out in one tick.</summary>
    internal ScanIntent ConsumeScan() { lock (_lock) { var pending = _scan; _scan = ScanIntent.None; return pending; } }
    private readonly SocialMessage[] _social = new SocialMessage[16];
    private int _socialHead, _socialCount;

    /// <summary>Queues chat without allocating; false means text is too long or the intent queue is full.</summary>
    public bool SendChat(ChatChannel channel, ReadOnlySpan<char> text)
    {
        var message = new SocialMessage { Kind = SocialKind.Chat, Value = (byte)channel };
        return message.SetText(text) && QueueSocial(message);
    }

    /// <summary>Invites a player by their session id.</summary>
    public bool Invite(uint player) => QueueSocial(new SocialMessage { Kind = SocialKind.Invite, Player = player });
    /// <summary>Accepts only the displayed invitation, using its server-issued InvitationId.</summary>
    public bool AcceptInvitation(ulong invitationId) => QueueSocial(new SocialMessage { Kind = SocialKind.Accept, InvitationId = invitationId });
    /// <summary>Declines only the displayed invitation, using its server-issued InvitationId.</summary>
    public bool DeclineInvitation(ulong invitationId) => QueueSocial(new SocialMessage { Kind = SocialKind.Decline, InvitationId = invitationId });
    /// <summary>Leaves the current party.</summary>
    public bool LeaveParty() => QueueSocial(new SocialMessage { Kind = SocialKind.Leave });
    /// <summary>Asks the leader to remove a party member.</summary>
    public bool RemoveMember(uint player) => QueueSocial(new SocialMessage { Kind = SocialKind.Remove, Player = player });
    private bool QueueSocial(in SocialMessage message)
    {
        lock (_lock)
        {
            if (_socialCount == _social.Length) return false;
            _social[(_socialHead + _socialCount++) % _social.Length] = message;
            return true;
        }
    }
    internal bool ConsumeSocial(out SocialMessage message)
    {
        lock (_lock)
        {
            message = default;
            if (_socialCount == 0) return false;
            message = _social[_socialHead];
            _socialHead = (_socialHead + 1) % _social.Length;
            _socialCount--;
            return true;
        }
    }

    /// <summary>What the editor asks of the session next, as <see cref="ConsumeEdit"/> hands it over.</summary>
    internal enum EditIntent : byte
    {
        None,
        Begin,
        Cancel,
        Commit,
        Repair,

        /// <summary>Debug: fill tanks; the blocks are taken with <see cref="TakeFill"/>.</summary>
        Fill,
        AddContainer,
    }

    private bool _hasEditBegin, _hasEditCancel, _hasEditRepair, _hasEditFill;
    private bool _hasCargoAdd;
    private EditorRequestMessage _cargoAdd, _takenCargoAdd;
    public void AddContainer(uint token, int rack, int size, int resource)
    {
        lock (_lock)
        {
            _cargoAdd = new EditorRequestMessage { Kind = EditorRequestKind.AddContainer, Token = token, CargoRack = rack, CargoSize = size, CargoResource = resource };
            _hasCargoAdd = true;
        }
    }
    /// <summary>Queues a selected hold slot together with the authoritative hold tick the player saw.</summary>
    public void JettisonContainer(uint token, uint hull, int rack, int index, long tick, JettisonMode mode = JettisonMode.Abandon)
    {
        lock (_lock)
        {
            _cargoAdd = new EditorRequestMessage
            {
                Kind = EditorRequestKind.JettisonContainer,
                Token = token,
                Hull = hull,
                CargoRack = rack,
                CargoIndex = index,
                CargoTick = tick,
                JettisonMode = mode
            };
            _hasCargoAdd = true;
        }
    }
    private bool _hasJettisonCargo;
    private JettisonCargoMessage _jettisonCargo;

    /// <summary>Queues a flight release using the controlled hull and the observed hold tick; no editor is needed.</summary>
    public void JettisonCargo(uint hull, int rack, int index, long tick, JettisonMode mode)
    {
        if (hull == 0 || rack < 0 || (uint)index >= BlockStateStore.MaxContainers || tick < 0
            || mode is not (JettisonMode.Abandon or JettisonMode.KeepClaim)) return;
        lock (_lock)
        {
            _jettisonCargo = new(hull, rack, index, tick, mode);
            _hasJettisonCargo = true;
        }
    }

    internal bool ConsumeJettisonCargo(out JettisonCargoMessage request)
    {
        lock (_lock)
        {
            request = _jettisonCargo;
            var pending = _hasJettisonCargo;
            _hasJettisonCargo = false;
            return pending;
        }
    }

    private bool _hasLaserAim;
    private LaserAimMessage _laserAim;

    /// <summary>
    /// Queues the pilot's firing intent for <paramref name="hull"/>: the action groups that fire (a mask, bit
    /// <c>g - 1</c> for group <c>g</c>; zero for none) and the aim, a ray from the camera through the pointer, its
    /// origin relative to the hull's position. Call it when it changes; a newer intent replaces one not yet sent.
    /// </summary>
    public void AimLasers(uint hull, ushort groups, System.Numerics.Vector3 offset, System.Numerics.Vector3 direction)
    {
        if (hull == 0 || (groups & ~ActionGroups.AllMask) != 0 || !float.IsFinite(offset.LengthSquared())) return;
        var length = direction.Length();
        if (!(length > 1e-3f) || !float.IsFinite(length)) return;
        lock (_lock)
        {
            _laserAim = new LaserAimMessage(hull, groups, offset, direction / length);
            _hasLaserAim = true;
        }
    }

    internal bool ConsumeLaserAim(out LaserAimMessage message)
    {
        lock (_lock)
        {
            message = _laserAim;
            var pending = _hasLaserAim;
            _hasLaserAim = false;
            return pending;
        }
    }

    private readonly SetActionGroupMessage[] _groupChanges = new SetActionGroupMessage[8];
    private int _groupChangeCount;

    /// <summary>
    /// Asks the server to fire groupable part <paramref name="part"/> (its place in <see cref="HullDesign.Lasers"/>)
    /// of an owned hull with group <paramref name="group"/>, without a rebuild; <see cref="ActionGroups.NotOverridden"/>
    /// returns it to the design's group. The answer shows in <see cref="SessionSnapshot.ActionGroupOverrides"/>.
    /// </summary>
    public void SetActionGroup(uint hull, int part, byte group)
    {
        if (hull == 0 || (uint)part >= ActionGroupsMessage.MaxParts || (group != ActionGroups.NotOverridden && !ActionGroups.Valid(group))) return;
        lock (_lock)
        {
            // A change of the same part not yet sent is replaced; the queue keeps the newest few parts.
            for (var i = 0; i < _groupChangeCount; i++)
                if (_groupChanges[i].Hull == hull && _groupChanges[i].Part == part)
                {
                    _groupChanges[i] = new SetActionGroupMessage(hull, (byte)part, group);
                    return;
                }
            if (_groupChangeCount == _groupChanges.Length) return;
            _groupChanges[_groupChangeCount++] = new SetActionGroupMessage(hull, (byte)part, group);
        }
    }

    internal bool ConsumeSetActionGroup(out SetActionGroupMessage message)
    {
        lock (_lock)
        {
            message = default;
            if (_groupChangeCount == 0) return false;
            message = _groupChanges[0];
            Array.Copy(_groupChanges, 1, _groupChanges, 0, --_groupChangeCount);
            return true;
        }
    }

    private bool _hasRockScrape;
    private RockScrapeRequest _rockScrape;

    /// <summary>
    /// Queues the temporary debug scrape: rays in the rock's own frame along which the server looks for its surface.
    /// Local games only; the server refuses it anywhere else. A newer request replaces one not sent yet.
    /// </summary>
    public void DebugScrape(in RockScrapeRequest request)
    {
        if (request.Rays is < 1 or > RockScrapeRequest.MaxRays || request.RadiusMm == 0) return;
        lock (_lock)
        {
            _rockScrape = request;
            _hasRockScrape = true;
        }
    }

    internal bool ConsumeRockScrape(out RockScrapeRequest request)
    {
        lock (_lock)
        {
            request = _rockScrape;
            var pending = _hasRockScrape;
            _hasRockScrape = false;
            return pending;
        }
    }

    internal EditorRequestMessage TakeCargoAdd() => _takenCargoAdd;

    private bool _hasDroneCommand;
    private DroneCommandMessage _droneCommand;

    /// <summary>
    /// Asks the server to launch <paramref name="count"/> drones from <paramref name="hull"/>'s bays to hold station:
    /// at <paramref name="point"/> (the hull's own frame, metres from its pivot) when given, else each in front of
    /// its own hatch. The answer shows in <see cref="SessionSnapshot.Bays"/>. A newer command replaces one not yet sent.
    /// </summary>
    public void LaunchDrones(uint hull, int count, System.Numerics.Vector3? point = null)
    {
        if (hull == 0 || count <= 0) return;
        var at = point ?? System.Numerics.Vector3.Zero;
        if (!float.IsFinite(at.X) || !float.IsFinite(at.Y) || !float.IsFinite(at.Z)) return;
        lock (_lock)
        {
            _droneCommand = new DroneCommandMessage(hull, DroneCommand.Launch, (byte)Math.Min(count, byte.MaxValue), point.HasValue, at);
            _hasDroneCommand = true;
        }
    }

    /// <summary>Asks the server to call every drone of <paramref name="hull"/> home and stop launches still waiting.</summary>
    public void RecallDrones(uint hull)
    {
        if (hull == 0) return;
        lock (_lock)
        {
            _droneCommand = new DroneCommandMessage(hull, DroneCommand.Recall, 0, false, default);
            _hasDroneCommand = true;
        }
    }

    /// <summary>
    /// Asks the server to send drones from <paramref name="hull"/>'s bays to fetch the free container
    /// <paramref name="container"/> and hand it to the hull's tractor beam. The server picks how many drones it takes
    /// and says why when it cannot (<see cref="SessionSnapshot.Bays"/>).
    /// </summary>
    public void FetchContainer(uint hull, uint container)
    {
        if (hull == 0 || container == 0) return;
        lock (_lock)
        {
            _droneCommand = new DroneCommandMessage(hull, DroneCommand.Fetch, 0, false, default, container);
            _hasDroneCommand = true;
        }
    }

    internal bool ConsumeDroneCommand(out DroneCommandMessage command)
    {
        lock (_lock)
        {
            command = _droneCommand;
            var pending = _hasDroneCommand;
            _hasDroneCommand = false;
            return pending;
        }
    }

    private FillBlocks _fillBlocks;
    private int _fillCount;
    private uint _editHull, _editToken;
    private byte[]? _editBytes;
    private int _installedDesigns, _failedDesigns;

    /// <summary>The most canonical bytes <see cref="CommitEdit"/> takes; the session sets it from the content's transfer limit.</summary>
    internal int MaxCommitBytes { get; set; } = 32768;

    /// <summary>
    /// Asks the server to let the player edit a hull, which must be the one they fly. The answer, an open edit
    /// with its token or a refusal with its reason and wait, shows in <see cref="SessionSnapshot.Editor"/>. A
    /// newer request replaces one the session has not taken yet.
    /// </summary>
    public void RequestEdit(uint hull)
    {
        lock (_lock)
        {
            _hasEditBegin = true;
            _editHull = hull;
            _hasEditCancel = false;
            _hasEditRepair = false;
            _editBytes = null;
        }
    }

    /// <summary>Ends the player's edit, whatever stage it is in, and drops a request or design the session has not taken yet.</summary>
    public void CancelEdit()
    {
        lock (_lock)
        {
            _hasEditCancel = true;
            _hasEditBegin = false;
            _hasEditRepair = false;
            _editBytes = null;
        }
    }

    /// <summary>
    /// Commits a design for the open edit <paramref name="token"/> names: the canonical bytes
    /// <see cref="BuildDraft.Export"/> gives. They are copied here, so the caller may do with its own what it
    /// likes; the copy is never written to again. False when they are empty or more than a design may be. The
    /// server checks the design itself and answers in <see cref="SessionSnapshot.Editor"/>; a newer commit
    /// replaces one the session has not taken yet. Allocates: call it when the player applies, not every frame.
    /// </summary>
    public bool CommitEdit(uint token, ReadOnlySpan<byte> canonicalBytes)
    {
        if (canonicalBytes.IsEmpty || canonicalBytes.Length > MaxCommitBytes)
            return false;
        var copy = canonicalBytes.ToArray();
        lock (_lock)
        {
            _editBytes = copy;
            _editToken = token;
            _hasEditCancel = false;
            _hasEditRepair = false;
        }

        return true;
    }

    /// <summary>
    /// Asks the server to repair the hull of the open edit <paramref name="token"/> names: to rebuild it as the
    /// design it has, with every block back and whole. Nothing is sent but the request, and the draft plays no
    /// part, so an interface with unsaved changes should say so before it asks. The server refuses a hull with
    /// nothing to repair (<see cref="EditorReason.NoChange"/>; <see cref="HullDesign.NeedsRepair"/> says so
    /// beforehand) and spaces repairs as it spaces commits. The answer shows in
    /// <see cref="SessionSnapshot.Editor"/>, ending in <see cref="EditorPhase.Applied"/> with the new hull. A
    /// repair replaces a commit the session has not taken yet, and a commit replaces a repair.
    /// </summary>
    public void RepairEdit(uint token)
    {
        lock (_lock)
        {
            _hasEditRepair = true;
            _editToken = token;
            _hasEditCancel = false;
            _editBytes = null;
        }
    }

    /// <summary>
    /// Debug: asks the server to fill the tanks in the blocks listed (indices of the edited hull's design) of the
    /// edit <paramref name="token"/> names. The server decides, and answers with <see cref="EditorReason.FillDone"/>,
    /// <see cref="EditorReason.FillOff"/> or <see cref="EditorReason.FillRefused"/>. At most
    /// <see cref="EditorRequestMessage.MaxFill"/> blocks are taken; a newer request replaces one not yet sent.
    /// To be removed with the Fill tank button.
    /// </summary>
    public void FillTanks(uint token, ReadOnlySpan<int> blocks)
    {
        lock (_lock)
        {
            _fillCount = Math.Min(blocks.Length, EditorRequestMessage.MaxFill);
            for (var i = 0; i < _fillCount; i++)
                _fillBlocks[i] = blocks[i];
            _editToken = token;
            _hasEditFill = _fillCount > 0;
        }
    }

    /// <summary>Called by the simulation after <see cref="EditIntent.Fill"/>: the blocks to fill.</summary>
    internal int TakeFill(out FillBlocks blocks)
    {
        lock (_lock)
        {
            blocks = _fillBlocks;
            return _fillCount;
        }
    }

    /// <summary>
    /// Called by the simulation: takes the next editor request, a cancel before a begin before a repair or a
    /// commit. The bytes of a commit are the session's from here on.
    /// </summary>
    internal EditIntent ConsumeEdit(out uint value, out byte[]? bytes)
    {
        lock (_lock)
        {
            value = 0;
            bytes = null;
            if (_hasEditCancel)
            {
                _hasEditCancel = false;
                return EditIntent.Cancel;
            }

            if (_hasEditBegin)
            {
                _hasEditBegin = false;
                value = _editHull;
                return EditIntent.Begin;
            }

            if (_hasEditRepair)
            {
                _hasEditRepair = false;
                value = _editToken;
                return EditIntent.Repair;
            }

            if (_hasEditFill)
            {
                _hasEditFill = false;
                value = _editToken;
                return EditIntent.Fill;
            }

            if (_hasCargoAdd)
            {
                _hasCargoAdd = false;
                _takenCargoAdd = _cargoAdd;
                value = _takenCargoAdd.Token;
                return EditIntent.AddContainer;
            }
            if (_editBytes is null)
                return EditIntent.None;
            value = _editToken;
            bytes = _editBytes;
            _editBytes = null;
            return EditIntent.Commit;
        }
    }

    /// <summary>
    /// Called by the render thread: the meshes of the first <paramref name="count"/> designs of the session's
    /// catalogue are in the renderer. The session tells the server it can show a design added during play only
    /// once the design is counted here, and until then the server sends no hull of it. A session with no renderer
    /// never reports, and so is never sent such a hull.
    /// </summary>
    public void ReportInstalledDesigns(int count) => Volatile.Write(ref _installedDesigns, count);

    /// <summary>
    /// Called by the render thread: the meshes for the session's catalogue of <paramref name="count"/> designs
    /// could not be prepared. The designs past the installed ones stay unavailable, and the session tells the
    /// server so, which ends an edit that was waiting for one of them.
    /// </summary>
    public void ReportDesignFailure(int count) => Volatile.Write(ref _failedDesigns, count);

    /// <summary>How many designs the renderer has installed, and the size of the newest catalogue it could not prepare.</summary>
    internal int InstalledDesigns => Volatile.Read(ref _installedDesigns);
    internal int FailedDesigns => Volatile.Read(ref _failedDesigns);

    private bool _hasPlot;
    private NavigationView _plot;

    /// <summary>Plot a destination for the controlled hull without starting its jump. Zero target is a valid point.</summary>
    public void PlotJump(uint hull, Double3 target)
    {
        lock (_lock) { _plot = new NavigationView { Hull = hull, HasPlot = true, Target = target }; _hasPlot = true; }
    }

    internal bool ConsumePlot(out NavigationView plot)
    {
        lock (_lock) { plot = _plot; var pending = _hasPlot; _hasPlot = false; return pending; }
    }

    private PlayerControls _controls;
    private bool _hasOrder;
    private HullOrder _order;
    private readonly FleetOrder[] _fleetOrders = new FleetOrder[FleetOrdersMessage.MaxOrders];
    private int _fleetOrderCount;
    private bool _hasControlRequest;
    private uint _controlRequest;

    /// <summary>Called by the input thread once per frame.</summary>
    public void Submit(in PlayerControls frame)
    {
        lock (_lock)
        {
            var toggle = _controls.ToggleDampeners ^ frame.ToggleDampeners;
            _controls = frame;
            _controls.ToggleDampeners = toggle;
        }
    }

    /// <summary>Gives the player's hull a standing order, replacing any it has. Kind None cancels.</summary>
    public void IssueOrder(in HullOrder order)
    {
        lock (_lock)
        {
            _order = order;
            _hasOrder = true;
        }
    }

    /// <summary>
    /// Orders a hull the player owns but is not flying. A newer order for the same hull replaces a queued one.
    /// </summary>
    public void IssueFleetOrder(uint hull, in HullOrder order)
    {
        lock (_lock)
        {
            for (var i = 0; i < _fleetOrderCount; i++)
            {
                if (_fleetOrders[i].Hull != hull)
                    continue;
                _fleetOrders[i].Order = order;
                return;
            }

            if (_fleetOrderCount < _fleetOrders.Length)
                _fleetOrders[_fleetOrderCount++] = new FleetOrder { Hull = hull, Order = order };
        }
    }

    /// <summary>
    /// Asks to take direct control of a hull the player owns. Zero asks to leave the current hull in a drone.
    /// </summary>
    private WreckSettingsMessage _wreckSettings;
    private bool _hasWreckSettings;
    /// <summary>Requests new wreck settings; only the local host is authorized by the server.</summary>
    public void RequestWreckSettings(WreckSettingsMessage value)
    {
        if (!value.IsValid) return;
        lock (_lock) { _wreckSettings = value; _hasWreckSettings = true; }
    }
    /// <summary>Takes a pending wreck setting request on the simulation thread.</summary>
    public bool ConsumeWreckSettings(out WreckSettingsMessage value)
    {
        lock (_lock) { value = _wreckSettings; var pending = _hasWreckSettings; _hasWreckSettings = false; return pending; }
    }

    public void RequestControl(uint hull)
    {
        lock (_lock)
        {
            _controlRequest = hull;
            _hasControlRequest = true;
        }
    }

    /// <summary>Called by the simulation once per tick: takes a pending control request.</summary>
    public bool ConsumeControlRequest(out uint hull)
    {
        lock (_lock)
        {
            hull = _controlRequest;
            var pending = _hasControlRequest;
            _hasControlRequest = false;
            return pending;
        }
    }

    /// <summary>Called by the simulation once per tick: takes the queued fleet orders.</summary>
    public int ConsumeFleetOrders(Span<FleetOrder> destination)
    {
        lock (_lock)
        {
            var count = _fleetOrderCount;
            _fleetOrders.AsSpan(0, count).CopyTo(destination);
            _fleetOrderCount = 0;
            return count;
        }
    }

    /// <summary>Called by the simulation once per tick: takes the accumulated one-shot values.</summary>
    public PlayerControls Consume(out bool hasOrder, out HullOrder order)
    {
        lock (_lock)
        {
            var result = _controls;
            _controls.ToggleDampeners = false;
            hasOrder = _hasOrder;
            order = _order;
            _hasOrder = false;
            return result;
        }
    }
}

/// <summary>What the player asked of their scan since the last tick.</summary>
public enum ScanIntent : byte { None, Start, Cancel }
