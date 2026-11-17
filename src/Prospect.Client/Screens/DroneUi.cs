using Prospect.Client.Input;
using Prospect.Engine.Time;
using Prospect.Game.Client;
using Prospect.Game.Protocol;

namespace Prospect.Client.Screens;

/// <summary>What the drone chip says about the flown hull's bays.</summary>
internal enum DroneChipState : byte
{
    /// <summary>The hull has no bay: no chip.</summary>
    Absent,

    /// <summary>Drones are inside and none is out.</summary>
    Ready,

    /// <summary>Some drones are out of their bays.</summary>
    Out,

    /// <summary>No drone inside and none out: every one is being rebuilt or the bays are gone.</summary>
    Empty,
}

/// <summary>
/// Font-free rules for the drone keys and chip. The server decides every launch and recall and says why it refused
/// (<see cref="BayView.Reply"/>); this only reads the view and asks. Takes no memory.
/// </summary>
internal static class DroneUi
{
    /// <summary>Whether <paramref name="view"/> is of the hull flown, and it has bays.</summary>
    public static bool Present(in BayView view, uint ownHull) => ownHull != 0 && view.Hull == ownHull && view.Count > 0;

    /// <summary>What the chip shows for the flown hull.</summary>
    public static DroneChipState State(in BayView view, uint ownHull)
    {
        if (!Present(view, ownHull)) return DroneChipState.Absent;
        view.Totals(out var stocked, out var outside, out _, out _);
        return outside > 0 ? DroneChipState.Out : stocked > 0 ? DroneChipState.Ready : DroneChipState.Empty;
    }

    /// <summary>Whole seconds until the next rebuilt drone is done, rounded up; zero when none is being rebuilt.</summary>
    public static int RebuildSeconds(in BayView view, long serverTick)
    {
        var next = view.NextRebuild();
        return next > serverTick ? (int)((next - serverTick + TickLoop.SimulationHz - 1) / TickLoop.SimulationHz) : 0;
    }

    /// <summary>
    /// The launch key sends every drone that is inside and not yet waiting to leave; the recall key calls them all
    /// home. A press with nothing to launch still asks, so the server's answer (empty, hatch blocked) is shown.
    /// </summary>
    /// <remarks>
    /// The fetch key sends drones for <paramref name="fetchSubject"/>: the loose container under a shown pointer, else
    /// the one targeted (<see cref="FetchSubject"/>); nothing when there is none.
    /// </remarks>
    public static void HandleKeys(ActionMap actions, InputDevices devices, bool keyboard, in BayView view, uint ownHull, PlayerInputBridge bridge, uint fetchSubject = 0)
    {
        if (!Present(view, ownHull)) return;
        if (actions.Pressed(devices, InputAction.LaunchDrones, keyboard)) Launch(view, ownHull, bridge);
        if (actions.Pressed(devices, InputAction.RecallDrones, keyboard)) bridge.RecallDrones(ownHull);
        if (actions.Pressed(devices, InputAction.FetchContainer, keyboard) && fetchSubject != 0) bridge.FetchContainer(ownHull, fetchSubject);
    }

    /// <summary>The container the fetch key acts on: the loose one under the pointer first, else the targeted one; zero for none.</summary>
    public static uint FetchSubject(uint hovered, uint targeted) => hovered != 0 ? hovered : targeted;

    /// <summary>What the launch key and a click on the chip with nothing out do: launch every drone that is inside.</summary>
    public static void Launch(in BayView view, uint ownHull, PlayerInputBridge bridge)
    {
        view.Totals(out var stocked, out _, out var pending, out _);
        bridge.LaunchDrones(ownHull, Math.Max(1, stocked - pending));
    }

    /// <summary>The words a refusal or an answer speaks on the chip.</summary>
    public static ReadOnlySpan<byte> Words(DroneReply reply) => reply switch
    {
        DroneReply.Launching => "LAUNCHING"u8,
        DroneReply.Recalling => "RECALLING"u8,
        DroneReply.NoBay => "NO BAY"u8,
        DroneReply.Empty => "BAYS EMPTY"u8,
        DroneReply.HatchBlocked => "HATCH BLOCKED"u8,
        DroneReply.PlayerLimit => "DRONE LIMIT"u8,
        DroneReply.WorldFull => "WORLD FULL"u8,
        DroneReply.InTransit => "IN TRANSIT"u8,
        DroneReply.TooFar => "TOO FAR"u8,
        DroneReply.NoneOut => "NONE OUT"u8,
        DroneReply.Fetching => "FETCHING"u8,
        DroneReply.NotFound => "NOT IN SIGHT"u8,
        DroneReply.Taken => "ALREADY TAKEN"u8,
        DroneReply.Claimed => "CLAIMED"u8,
        DroneReply.NoTractor => "NO TRACTOR"u8,
        DroneReply.NoRoom => "NO RACK ROOM"u8,
        DroneReply.TooHeavy => "TOO HEAVY"u8,
        DroneReply.TooFew => "TOO FEW DRONES"u8,
        DroneReply.Dropped => "DROPPED"u8,
        DroneReply.Delivered => "DELIVERED"u8,
        DroneReply.ScanSent => "SCAN SENT"u8,
        DroneReply.ScanReady => "SCAN READY"u8,
        DroneReply.ScanRunning => "SCANNING"u8,
        DroneReply.ScanDone => "SCAN DONE"u8,
        DroneReply.TooLarge => "ROCK TOO LARGE"u8,
        DroneReply.ScanBusy => "SCAN BUSY"u8,
        DroneReply.ScanNotReady => "NOT IN PLACE"u8,
        DroneReply.ScanAborted => "SCAN ABORTED"u8,
        DroneReply.ScanTimedOut => "SCAN TIMED OUT"u8,
        DroneReply.ScanCancelled => "SCAN CANCELLED"u8,
        DroneReply.ScanUnavailable => "NO DRONE SCANS"u8,
        _ => default,
    };

    /// <summary>Whether an answer is a refusal, shown in the warning colour.</summary>
    public static bool Refused(DroneReply reply) => reply is not (DroneReply.None or DroneReply.Launching or DroneReply.Recalling or DroneReply.Fetching or DroneReply.Delivered
        or DroneReply.ScanSent or DroneReply.ScanReady or DroneReply.ScanRunning or DroneReply.ScanDone or DroneReply.ScanCancelled);

    /// <summary>Whether an answer is a drone scan's: its words tell how the scan stands, and the panel puts them by its buttons.</summary>
    public static bool IsScanReply(DroneReply reply) => reply is >= DroneReply.ScanSent and <= DroneReply.ScanUnavailable or DroneReply.TooFar or DroneReply.NotFound or DroneReply.InTransit;
}

/// <summary>
/// The drone settings popup, apart from drawing so it can be tested without fonts. A click on the DRONES chip opens it
/// over the chip and a click on the chip again closes it; so do a press anywhere else that is not on the popup and
/// Escape (<see cref="ScreenHost.BackStep.DronePopup"/>). It belongs to one hull: another hull, or a hull that has
/// lost its bays, closes it. Like the fuel callout it stays open while the pointer is captured again; it only takes
/// presses while the pointer is shown, which is when ImGui is given the mouse at all. What its buttons do is
/// <see cref="DroneUi"/>'s: the same launch and recall as the keys, and the server's own settings ("Pick up cargo" and
/// "Pick up ores"), which the checkboxes read back from <see cref="BayView.AutoPickup"/> and
/// <see cref="BayView.PickUpOres"/> rather than keeping a copy of their own.
/// </summary>
internal sealed class DronePopup
{
    private uint _hull;

    /// <summary>True while the popup is open.</summary>
    public bool IsOpen { get; private set; }

    /// <summary>Opens the popup, or closes it when it is open (a click on the chip).</summary>
    public void Toggle() => IsOpen = !IsOpen;

    /// <summary>Closes the popup.</summary>
    public void Close() => IsOpen = false;

    /// <summary>Keeps the popup to the hull it was opened on, and to a hull that still has bays (<paramref name="present"/>).</summary>
    public void Follow(uint hull, bool present)
    {
        if (hull != _hull) { _hull = hull; IsOpen = false; }
        if (!present) IsOpen = false;
    }

    /// <summary>
    /// A press that was neither on the popup nor on the chip closes it. The chip's own press toggles it instead
    /// (<see cref="Toggle"/>), so it is excluded here: otherwise a click on the chip would close the popup and then
    /// open it again.
    /// </summary>
    public void Press(bool pressed, bool overPopup, bool overChip)
    {
        if (pressed && IsOpen && !overPopup && !overChip) IsOpen = false;
    }

    /// <summary>The popup's one button: recall while drones are out, otherwise launch what is inside, as the keys do.</summary>
    public static void LaunchOrRecall(in BayView view, uint ownHull, PlayerInputBridge bridge)
    {
        view.Totals(out _, out var outside, out _, out _);
        if (outside > 0) bridge.RecallDrones(ownHull);
        else DroneUi.Launch(view, ownHull, bridge);
    }

    /// <summary>The checkbox flips the server's setting, read from the view: a lost message shows as a box that did not change.</summary>
    public static void ToggleAutoPickup(in BayView view, uint ownHull, PlayerInputBridge bridge) =>
        bridge.SetDroneSettings(ownHull, !view.AutoPickup, view.PickUpOres);

    /// <summary>
    /// The "Pick up ores" checkbox, by the same rule: it flips the server's setting as the view has it and leaves
    /// "Pick up cargo" as the view has it, since the two travel in one message.
    /// </summary>
    public static void ToggleOres(in BayView view, uint ownHull, PlayerInputBridge bridge) =>
        bridge.SetDroneSettings(ownHull, view.AutoPickup, !view.PickUpOres);
}

/// <summary>
/// Keeps the newest answer the server gave to a drone command for a few seconds, so the chip can speak it. A new
/// answer is a new <see cref="BayView.ReplySequence"/>; the first view seen sets the count without speaking.
/// </summary>
internal sealed class DroneTracker
{
    private uint _sequence;
    private bool _seen;
    private DroneReply _reply;
    private float _age = float.MaxValue;

    /// <summary>Reads this frame's view; <paramref name="dt"/> is the frame's seconds.</summary>
    public void Update(in BayView view, float dt)
    {
        _age += dt;
        if (view.Hull == 0) return;
        if (!_seen) { _seen = true; _sequence = view.ReplySequence; return; }
        if (view.ReplySequence == _sequence) return;
        _sequence = view.ReplySequence;
        _reply = view.Reply;
        _age = 0;
    }

    /// <summary>The answer to speak, or <see cref="DroneReply.None"/> once it is <paramref name="seconds"/> old.</summary>
    public DroneReply Speaking(float seconds) => _age <= seconds ? _reply : DroneReply.None;

    /// <summary>The newest answer however old (<see cref="DroneReply.None"/> before the first), for the popup's last-answer line.</summary>
    public DroneReply Latest => _reply;
}
