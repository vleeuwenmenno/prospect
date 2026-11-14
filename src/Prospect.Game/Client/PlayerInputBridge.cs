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
    private bool _scan;
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
    public void RequestScan() { lock (_lock) _scan = true; }
    internal bool ConsumeScan() { lock (_lock) { var pending = _scan; _scan = false; return pending; } }
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
    }

    private bool _hasEditBegin, _hasEditCancel, _hasEditRepair;
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
