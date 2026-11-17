using Prospect.Client.Input;
using Prospect.Engine.Time;
using Prospect.Game.Client;
using Prospect.Game.Protocol;
using Prospect.Game.World;

namespace Prospect.Client.Screens;

/// <summary>What a drone scan key did, so a test can say it.</summary>
internal enum DroneScanKeyAction : byte
{
    /// <summary>Nothing: no bay, no target, or another kind of scan is under way.</summary>
    None,

    /// <summary>Asked the server to send drones around the targeted rock.</summary>
    Request,

    /// <summary>Ordered the scan, the drones being in place.</summary>
    Start,

    /// <summary>Gave the scan under way up.</summary>
    Cancel,
}

/// <summary>
/// Font-free rules for the drone scans: the keys, what the panels and the chip read off the flown hull's scan, and the
/// words for a vein hint. The server decides every scan and says how it stands (<see cref="BayView.ScanPhase"/>); this
/// only reads the view and asks. Takes no memory.
/// </summary>
internal static class DroneScanUi
{
    /// <summary>The drones are on their way, in place or scanning: a scan is under way and the player may give it up.</summary>
    public static bool Active(in BayView view) => view.ScanPhase is DroneScanPhase.Moving or DroneScanPhase.Ready or DroneScanPhase.Scanning;

    /// <summary>The hull's scan is of <paramref name="rock"/> and has not ended.</summary>
    public static bool OnRock(in BayView view, RockRef rock) => view.ScanPhase != DroneScanPhase.None && view.ScanRock == rock;

    /// <summary>Whole and tenths of seconds until a running scan ends; zero when it is not running.</summary>
    public static float SecondsLeft(in BayView view, long serverTick) =>
        view.ScanPhase == DroneScanPhase.Scanning && view.ScanCompletes > serverTick ? (view.ScanCompletes - serverTick) / (float)TickLoop.SimulationHz : 0f;

    /// <summary>How far a running scan has got, from 0 to 1; zero when it is not running.</summary>
    public static float Progress(in BayView view, long serverTick)
    {
        if (view.ScanPhase != DroneScanPhase.Scanning || view.ScanCompletes <= view.ScanStarted) return 0f;
        return Math.Clamp((serverTick - view.ScanStarted) / (float)(view.ScanCompletes - view.ScanStarted), 0f, 1f);
    }

    /// <summary>
    /// What a press of a scan key does. Ready: the drones are in place, so either key orders the scan. Moving or
    /// scanning: the same kind again gives it up, the other kind does nothing. Otherwise: ask for a scan of
    /// <paramref name="target"/>, if there is one; the server's answer (too large, too far, busy) shows on the chip.
    /// </summary>
    public static DroneScanKeyAction Press(in BayView view, uint ownHull, DroneScanKind kind, RockRef? target, PlayerInputBridge bridge)
    {
        if (!DroneUi.Present(view, ownHull)) return DroneScanKeyAction.None;
        switch (view.ScanPhase)
        {
            case DroneScanPhase.Ready:
                bridge.StartDroneScan(ownHull);
                return DroneScanKeyAction.Start;
            case DroneScanPhase.Moving or DroneScanPhase.Scanning:
                if (view.ScanKind != kind) return DroneScanKeyAction.None;
                bridge.CancelDroneScan(ownHull);
                return DroneScanKeyAction.Cancel;
            default:
                if (target is not { } rock) return DroneScanKeyAction.None;
                bridge.RequestDroneScan(ownHull, kind, rock);
                return DroneScanKeyAction.Request;
        }
    }

    /// <summary>The two scan keys: the survey key and the detailed scan key.</summary>
    public static void HandleKeys(ActionMap actions, InputDevices devices, bool keyboard, in BayView view, uint ownHull, RockRef? target, PlayerInputBridge bridge)
    {
        if (!DroneUi.Present(view, ownHull)) return;
        if (actions.Pressed(devices, InputAction.SurveyRock, keyboard)) Press(view, ownHull, DroneScanKind.Survey, target, bridge);
        else if (actions.Pressed(devices, InputAction.DetailedScan, keyboard)) Press(view, ownHull, DroneScanKind.Detailed, target, bridge);
    }

    /// <summary>The scan button's own click: orders a ready scan. Nothing otherwise, and never the key's cancel.</summary>
    public static bool StartIfReady(in BayView view, uint ownHull, PlayerInputBridge bridge)
    {
        if (!DroneUi.Present(view, ownHull) || view.ScanPhase != DroneScanPhase.Ready) return false;
        bridge.StartDroneScan(ownHull);
        return true;
    }

    /// <summary>The cancel button's click: gives the scan under way up, whatever its kind.</summary>
    public static bool CancelIfActive(in BayView view, uint ownHull, PlayerInputBridge bridge)
    {
        if (!DroneUi.Present(view, ownHull) || !Active(view)) return false;
        bridge.CancelDroneScan(ownHull);
        return true;
    }

    /// <summary>A key's name as the narrow scan buttons write it: punctuation keys as their mark, every other name as it is.</summary>
    public static string ShortKey(string upperName) => upperName switch
    {
        "SEMICOLON" => ";",
        "APOSTROPHE" => "'",
        "BACKSLASH" => "\\",
        _ => upperName,
    };

    /// <summary>The words of a vein hint band: none, a few (1 to 3), several (4 or 5), many (6 or more).</summary>
    public static ReadOnlySpan<byte> VeinHintWords(byte hint) => hint switch
    {
        0 => "VEINS: NONE"u8,
        1 => "VEINS: FEW (1-3)"u8,
        2 => "VEINS: SEVERAL (4-5)"u8,
        _ => "VEINS: MANY (6+)"u8,
    };

    /// <summary>How many pockets a detailed scan found and how many of them are unstable.</summary>
    public static void Pockets(in DroneScanRecord record, out int total, out int unstable)
    {
        total = Math.Min((int)record.Map.PocketCount, RockInteriorRules.MaxPockets);
        unstable = 0;
        for (var p = 0; p < total; p++)
            if (record.Map.Pockets[p].Unstable) unstable++;
    }

    /// <summary>Rows of the text under the composition for a record: its name and age, then its veins; a detailed scan adds the hologram's button and, when it is on, its legend.</summary>
    public static float RecordFooterHeight(ModuleLook.DroneScanLook look, DroneScanKind kind, bool hologramOn)
    {
        var height = look.LineHeight * 2;
        if (kind == DroneScanKind.Detailed) height += look.ButtonHeight + look.Gap + (hologramOn ? look.LineHeight : 0f);
        return height;
    }

    /// <summary>The height of the block with the scan buttons and their status line, in unscaled pixels.</summary>
    public static float ButtonBlockHeight(ModuleLook.DroneScanLook look) => look.Gap + look.ButtonHeight + look.Gap + look.LineHeight;
}

/// <summary>
/// Whether the detailed scan's hologram shows over its rock. A session choice, never saved; it turns on by itself
/// when a detailed result arrives for the rock targeted (or the rock the hull's scan is of), so the first look at a
/// scan needs no key. The first view seen sets what is already known without showing it.
/// </summary>
internal sealed class ScanHologramState
{
    private bool _seen;
    private uint _revision;
    private long _newest;

    /// <summary>True while the hologram shows.</summary>
    public bool On { get; private set; }

    /// <summary>The key or the button.</summary>
    public void Toggle() => On = !On;

    /// <summary>Reads this frame's results; <paramref name="target"/> and <paramref name="scanRock"/> are the rocks a new detailed result turns it on for.</summary>
    public void Observe(DroneScanKnowledgeView view, RockRef? target, RockRef? scanRock)
    {
        if (_seen && view.Revision == _revision) return;
        var fresh = !_seen || view.Revision < _revision;
        _seen = true;
        _revision = view.Revision;
        var newest = 0L;
        for (var i = 0; i < view.Count; i++)
        {
            ref var record = ref view.Records[i];
            newest = Math.Max(newest, record.RecordedTick);
            if (fresh || record.Kind != DroneScanKind.Detailed || record.RecordedTick <= _newest) continue;
            var rock = record.Rock;
            if (target == rock || scanRock == rock) On = true;
        }

        _newest = fresh ? newest : Math.Max(_newest, newest);
    }
}
