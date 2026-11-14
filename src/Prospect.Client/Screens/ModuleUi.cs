using Prospect.Client.Input;
using Prospect.Engine.Time;
using Prospect.Game.Client;
using Prospect.Game.World;

namespace Prospect.Client.Screens;

/// <summary>What a request for one module would meet now, by what the snapshot and the player's view say.</summary>
internal enum ModuleGate : byte
{
    /// <summary>The ship being flown has no such module: nothing is shown and its key does nothing.</summary>
    Absent,

    /// <summary>A request may go out; the server still decides.</summary>
    Ready,

    /// <summary>This module's scan is running.</summary>
    Running,

    /// <summary>Another module's scan is running; the modules take one request at a time.</summary>
    Waiting,

    /// <summary>The owner's shared cooldown is still running.</summary>
    Cooling,

    /// <summary>The asteroid scanner has nothing targeted.</summary>
    NoTarget,

    /// <summary>The target, or the nearest gas cloud's envelope, is beyond the module's reach.</summary>
    OutOfRange,

    /// <summary>The gas scanner has no cloud in reach: the ship is in empty space.</summary>
    NoCloud,
}

/// <summary>
/// Why a module request was not carried out, or that it was: what a chip says for a few seconds. The server's
/// refusals and the client's own checks (nothing targeted, no cloud in reach) share one list so a chip reads the same
/// whichever caught it.
/// </summary>
internal enum ModuleNotice : byte
{
    None,
    Done,
    Cancelled,
    NoPower,
    OutOfRange,
    Cooldown,
    InvalidTarget,
    NoEquipment,
    Busy,
    KnowledgeLimit,
    Unavailable,
    NoTarget,
    NoCloud,
}

/// <summary>The three modules' state this frame, as the chips and the buttons read it.</summary>
internal struct ModuleTargets
{
    /// <summary>The asteroid the scanner would scan: targeted, or the first one selected in the command view.</summary>
    public RockRef? Rock;

    /// <summary>True when that asteroid's surface is within the scanner's reach.</summary>
    public bool RockInRange;

    /// <summary>The cloud the gas scanner would sample, and whether the ship is within the module's reach of it.</summary>
    public SectorCoord Cloud;

    public bool HasCloud;
    public bool CloudInRange;
}

/// <summary>Font-free rules for the module keys, chips and refusals. The server decides; this only keeps the keys from asking when the answer is known.</summary>
internal static class ModuleRules
{
    /// <summary>The families a ship can carry, in the order the strip lists them.</summary>
    public static ReadOnlySpan<ModuleFamily> Families => [ModuleFamily.GasScanner, ModuleFamily.AsteroidScanner, ModuleFamily.AsteroidLocator];

    /// <summary>The action that runs a family's module.</summary>
    public static InputAction ActionOf(ModuleFamily family) => family switch
    {
        ModuleFamily.GasScanner => InputAction.ScanGas,
        ModuleFamily.AsteroidScanner => InputAction.ScanAsteroid,
        _ => InputAction.PulseLocator,
    };

    /// <summary>The chip's slot, 0 to 2, for a family; the order of <see cref="Families"/>.</summary>
    public static int SlotOf(ModuleFamily family) => family switch
    {
        ModuleFamily.GasScanner => 0,
        ModuleFamily.AsteroidScanner => 1,
        _ => 2,
    };

    /// <summary>
    /// What a request for <paramref name="family"/> would meet. A running scan belongs to the hull that began it, so
    /// with another hull flown nothing counts as running; the cooldown belongs to the owner and counts for any hull.
    /// </summary>
    public static ModuleGate Gate(ModuleFamily family, in ModuleEquipment equipment, in ModuleScanView scan, uint ownHull, long serverTick, in ModuleTargets targets)
    {
        if (equipment.Block == 0 || ownHull == 0)
            return ModuleGate.Absent;
        if (scan.Hull == ownHull && scan.State is ModuleScanState.Preparing or ModuleScanState.Scanning)
            return scan.Family == family ? ModuleGate.Running : ModuleGate.Waiting;
        if (scan.CooldownUntilTick > serverTick)
            return ModuleGate.Cooling;
        switch (family)
        {
            case ModuleFamily.GasScanner:
                return !targets.HasCloud ? ModuleGate.NoCloud : targets.CloudInRange ? ModuleGate.Ready : ModuleGate.OutOfRange;
            case ModuleFamily.AsteroidScanner:
                return targets.Rock is null ? ModuleGate.NoTarget : targets.RockInRange ? ModuleGate.Ready : ModuleGate.OutOfRange;
            default:
                return ModuleGate.Ready;
        }
    }

    /// <summary>Whole seconds of the shared cooldown left, rounded up so it never reads 0 while cooling; 0 when none.</summary>
    public static int CooldownSeconds(in ModuleScanView scan, long serverTick) =>
        scan.CooldownUntilTick > serverTick ? (int)((scan.CooldownUntilTick - serverTick + TickLoop.SimulationHz - 1) / TickLoop.SimulationHz) : 0;

    /// <summary>How far a running scan is, 0 to 1; zero while the server is still preparing it.</summary>
    public static float Progress(in ModuleScanView scan, long serverTick)
    {
        if (scan.State != ModuleScanState.Scanning)
            return 0f;
        var span = scan.CompletesTick - scan.StartedTick;
        return span <= 0 ? 1f : Math.Clamp((float)(serverTick - scan.StartedTick) / span, 0f, 1f);
    }

    /// <summary>Seconds a running scan has left; the whole duration is not known while it is still being prepared, so that is zero.</summary>
    public static float SecondsLeft(in ModuleScanView scan, long serverTick) =>
        scan.State == ModuleScanState.Scanning ? Math.Max(0, scan.CompletesTick - serverTick) / (float)TickLoop.SimulationHz : 0f;

    /// <summary>The notice a gate that stops a request on the client's own word gives; none for the gates that let it through.</summary>
    public static ModuleNotice NoticeOf(ModuleGate gate) => gate switch
    {
        ModuleGate.NoTarget => ModuleNotice.NoTarget,
        ModuleGate.NoCloud => ModuleNotice.NoCloud,
        ModuleGate.OutOfRange => ModuleNotice.OutOfRange,
        ModuleGate.Cooling => ModuleNotice.Cooldown,
        ModuleGate.Waiting => ModuleNotice.Busy,
        _ => ModuleNotice.None,
    };

    /// <summary>The notice a finished or refused server state gives; none while a scan is only running.</summary>
    public static ModuleNotice NoticeOf(ModuleScanState state) => state switch
    {
        ModuleScanState.Completed => ModuleNotice.Done,
        ModuleScanState.Cancelled => ModuleNotice.Cancelled,
        ModuleScanState.NoEquipment => ModuleNotice.NoEquipment,
        ModuleScanState.NoPower => ModuleNotice.NoPower,
        ModuleScanState.OutOfRange => ModuleNotice.OutOfRange,
        ModuleScanState.Cooldown => ModuleNotice.Cooldown,
        ModuleScanState.InvalidTarget => ModuleNotice.InvalidTarget,
        ModuleScanState.Busy => ModuleNotice.Busy,
        ModuleScanState.KnowledgeLimit => ModuleNotice.KnowledgeLimit,
        ModuleScanState.Unavailable => ModuleNotice.Unavailable,
        _ => ModuleNotice.None,
    };

    /// <summary>The words a chip shows for a notice; short, upper case.</summary>
    public static ReadOnlySpan<byte> Words(ModuleNotice notice) => notice switch
    {
        ModuleNotice.Done => "DONE"u8,
        ModuleNotice.Cancelled => "CANCELLED"u8,
        ModuleNotice.NoPower => "NO POWER"u8,
        ModuleNotice.OutOfRange => "OUT OF RANGE"u8,
        ModuleNotice.Cooldown => "COOLING"u8,
        ModuleNotice.InvalidTarget => "BAD TARGET"u8,
        ModuleNotice.NoEquipment => "MODULE LOST"u8,
        ModuleNotice.Busy => "BUSY"u8,
        ModuleNotice.KnowledgeLimit => "MEMORY FULL"u8,
        ModuleNotice.Unavailable => "UNAVAILABLE"u8,
        ModuleNotice.NoTarget => "NO TARGET"u8,
        ModuleNotice.NoCloud => "NO CLOUD"u8,
        _ => default,
    };

    /// <summary>A refusal in a sentence, for a tooltip.</summary>
    public static ReadOnlySpan<byte> Sentence(ModuleNotice notice) => notice switch
    {
        ModuleNotice.Cancelled => "The scan stopped: the ship jumped, changed hands or lost the module."u8,
        ModuleNotice.NoPower => "Not enough energy to run the module. Generation refills the battery."u8,
        ModuleNotice.OutOfRange => "The target is beyond the module's range."u8,
        ModuleNotice.Cooldown => "The modules share one cooldown, which has not run out."u8,
        ModuleNotice.InvalidTarget => "There is nothing there to scan."u8,
        ModuleNotice.NoEquipment => "The ship no longer has this module."u8,
        ModuleNotice.Busy => "Another scan is running; the modules take one request at a time."u8,
        ModuleNotice.KnowledgeLimit => "Your knowledge is full. Nothing old is dropped to make room."u8,
        ModuleNotice.Unavailable => "The server could not run the scan."u8,
        ModuleNotice.NoTarget => "Target an asteroid first."u8,
        ModuleNotice.NoCloud => "No gas cloud within reach of the scanner."u8,
        _ => default,
    };

    /// <summary>
    /// Sends the request a pressed module key or a clicked chip stands for. Only a ready module asks; a known no is
    /// said on the chip instead and returns its notice so the chip can show it. Nothing for a ship without the module.
    /// </summary>
    public static ModuleNotice Request(ModuleFamily family, ModuleGate gate, in ModuleTargets targets, PlayerInputBridge bridge)
    {
        if (gate == ModuleGate.Ready)
        {
            switch (family)
            {
                case ModuleFamily.GasScanner:
                    bridge.StartGasScan(targets.Cloud);
                    break;
                case ModuleFamily.AsteroidScanner:
                    bridge.StartAsteroidScan(targets.Rock!.Value);
                    break;
                default:
                    bridge.PulseAsteroidLocator();
                    break;
            }

            return ModuleNotice.None;
        }

        return gate == ModuleGate.Running ? ModuleNotice.None : NoticeOf(gate);
    }
}

/// <summary>
/// Follows the module scan the snapshot reports for what the interface says about it after it ended: a short result on
/// the chip of the family that ran, the first moment a locator pulse finished (which starts the overlay), and the
/// client's own refusals. It holds a few numbers and allocates nothing.
/// </summary>
internal sealed class ModuleTracker
{
    private uint _serial;
    private ModuleScanState _state;
    private readonly ModuleNotice[] _notice = new ModuleNotice[3];
    private readonly float[] _since = [float.MaxValue, float.MaxValue, float.MaxValue];

    /// <summary>Seconds since a locator pulse of this session finished; large while none has.</summary>
    public float PulseAge { get; private set; } = float.MaxValue;

    /// <summary>The tick the pulse's observations carry: records at or after it came from that pulse.</summary>
    public long PulseTick { get; private set; } = long.MaxValue;

    /// <summary>Where the pulse went off, and how far it reached.</summary>
    public Double3 PulseOrigin { get; private set; }

    public float PulseRange { get; private set; }

    /// <summary>Whether the last pulse could not list everything (the server's <see cref="ModuleScanView.Partial"/>).</summary>
    public bool PulsePartial { get; private set; }

    /// <summary>Forgets everything: a new game, or another hull taking the view.</summary>
    public void Reset()
    {
        _serial = 0;
        _state = ModuleScanState.Idle;
        for (var i = 0; i < _notice.Length; i++)
        {
            _notice[i] = ModuleNotice.None;
            _since[i] = float.MaxValue;
        }

        PulseAge = float.MaxValue;
        PulseTick = long.MaxValue;
    }

    /// <summary>Follows the snapshot's scan; <paramref name="locatorRange"/> is the own hull's locator reach, for the wave.</summary>
    public void Update(in ModuleScanView scan, uint ownHull, float locatorRange, float deltaTime)
    {
        for (var i = 0; i < _since.Length; i++)
            if (_since[i] < float.MaxValue)
                _since[i] += deltaTime;
        if (PulseAge < float.MaxValue)
            PulseAge += deltaTime;
        if (scan.Serial == _serial && scan.State == _state)
            return;
        _serial = scan.Serial;
        _state = scan.State;
        var notice = ModuleRules.NoticeOf(scan.State);
        if (notice == ModuleNotice.None || scan.Family is < ModuleFamily.GasScanner or > ModuleFamily.AsteroidLocator)
            return;
        // Only the hull that began it gets the result; a state left over from another hull is not news.
        if (scan.Hull != ownHull && scan.State != ModuleScanState.Cooldown)
            return;
        Note(scan.Family, notice);
        if (scan.State == ModuleScanState.Completed && scan.Family == ModuleFamily.AsteroidLocator)
        {
            PulseAge = 0f;
            PulseTick = scan.StartedTick;
            PulseOrigin = scan.Origin;
            PulseRange = locatorRange;
            PulsePartial = scan.Partial;
        }
    }

    /// <summary>Shows a notice on a family's chip.</summary>
    public void Note(ModuleFamily family, ModuleNotice notice)
    {
        var slot = ModuleRules.SlotOf(family);
        _notice[slot] = notice;
        _since[slot] = 0f;
    }

    /// <summary>The notice on a family's chip while it is within <paramref name="seconds"/> of its start, or none.</summary>
    public ModuleNotice Notice(ModuleFamily family, float seconds)
    {
        var slot = ModuleRules.SlotOf(family);
        return _since[slot] <= seconds ? _notice[slot] : ModuleNotice.None;
    }

    /// <summary>Seconds a family's notice has been up.</summary>
    public float NoticeAge(ModuleFamily family) => _since[ModuleRules.SlotOf(family)];
}
