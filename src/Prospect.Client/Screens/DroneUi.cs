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
        _ => default,
    };

    /// <summary>Whether an answer is a refusal, shown in the warning colour.</summary>
    public static bool Refused(DroneReply reply) => reply is not (DroneReply.None or DroneReply.Launching or DroneReply.Recalling or DroneReply.Fetching or DroneReply.Delivered);
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
}
