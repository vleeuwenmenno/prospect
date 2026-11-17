using Brutal.Numerics;
using Prospect.Game.Client;
using Prospect.Game.Protocol;
using Prospect.Game.World;

namespace Prospect.Client.Screens;

/// <summary>What kind of part is in an action group; today only mining lasers can be grouped.</summary>
internal enum ActionPartKind : byte
{
    Laser,
}

/// <summary>One cell of the action group strip: a group that has a part on the flown hull, and what it is doing now.</summary>
internal struct ActionGroupCell
{
    /// <summary>The group, 1 to 10; 10 is the key <c>0</c>.</summary>
    public int Group;

    /// <summary>How many parts of the flown hull fire with this group, and the kind of the first of them.</summary>
    public int Parts;

    public ActionPartKind Kind;

    /// <summary>The group is on: the fire button fires it.</summary>
    public bool On;

    /// <summary>The group is on and the fire button is held where it may fire.</summary>
    public bool Firing;

    /// <summary>At least one laser in the group is cutting rock, as the server last reported.</summary>
    public bool Cutting;

    /// <summary>The heat of the group's hottest laser, from 0 to 1: the cell's heat bar.</summary>
    public float Heat;

    /// <summary>How many of the group's lasers overheated and are shut down until they have cooled.</summary>
    public int Locked;

    /// <summary>The heat a shut-down laser of the group must cool to before it fires again; zero while none is shut down.</summary>
    public float UnlockHeat;

    /// <summary>
    /// The hull's lasers wait for its battery: they ran it empty and fire again once it holds
    /// <see cref="ResumeBattery"/>. The whole hull's, so every cell of a strip has it alike.
    /// </summary>
    public bool AwaitingPower;

    /// <summary>What the battery holds and what it must hold before the lasers fire again, as shares of its capacity; zero while they do not wait.</summary>
    public float Battery;

    public float ResumeBattery;

    /// <summary>Every laser of the group is shut down by heat: the group fires nothing until one has cooled.</summary>
    public readonly bool Overheated => Parts > 0 && Locked == Parts;

    /// <summary>The cell says the lasers wait for power. Overheated comes first: a group that is both says it is hot, as that lasts longer.</summary>
    public readonly bool NoPower => AwaitingPower && !Overheated;

    /// <summary>How far the battery is to where the lasers fire again, from 0 to 1: the bar of a cell that waits for power.</summary>
    public readonly float PowerProgress => ResumeBattery > 0f ? Math.Clamp(Battery / ResumeBattery, 0f, 1f) : 1f;
}

/// <summary>
/// Font-free state of the action group strip beside the flight console: one cell for each group the flown hull has a
/// part in, in key order (1 to 9, then 0), with whether it is on, firing, and cutting, how hot its lasers are and
/// whether they wait for the battery.
/// Nothing is shown for a group no part is in, and the strip is empty (and not drawn) without any. Worked out once a
/// frame from what the server reported (the design, its per-hull overrides, the beams and the lasers' heat); it takes
/// no memory.
/// </summary>
internal sealed class ActionGroupStrip
{
    private readonly ActionGroupCell[] _cells = new ActionGroupCell[ActionGroups.Count];
    private readonly int[] _parts = new int[ActionGroups.Count];
    private readonly bool[] _cutting = new bool[ActionGroups.Count];
    private readonly float[] _heat = new float[ActionGroups.Count];
    private readonly int[] _locked = new int[ActionGroups.Count];
    private readonly float[] _unlock = new float[ActionGroups.Count];

    /// <summary>How many cells there are.</summary>
    public int Count { get; private set; }

    /// <summary>The cells, in key order.</summary>
    public ReadOnlySpan<ActionGroupCell> Cells => _cells.AsSpan(0, Count);

    /// <summary>Whether a group's lasers are all shut down by heat.</summary>
    public bool AnyOverheated
    {
        get
        {
            foreach (var cell in Cells)
                if (cell.Overheated) return true;
            return false;
        }
    }

    /// <summary>Whether the hull's lasers wait for its battery, as a cell says it: a group that is not overheated shows it.</summary>
    public bool AnyNoPower
    {
        get
        {
            foreach (var cell in Cells)
                if (cell.NoPower) return true;
            return false;
        }
    }

    /// <summary>The cell of <paramref name="group"/> (1 to 10), or false when the hull has no part in it.</summary>
    public bool TryGet(int group, out ActionGroupCell cell)
    {
        foreach (var candidate in Cells)
            if (candidate.Group == group)
            {
                cell = candidate;
                return true;
            }

        cell = default;
        return false;
    }

    /// <summary>Starts a new count of the hull's parts.</summary>
    public void Begin()
    {
        Array.Clear(_parts);
        Array.Clear(_cutting);
        Array.Clear(_heat);
        Array.Clear(_locked);
        Array.Clear(_unlock);
        Count = 0;
    }

    /// <summary>
    /// Counts a part that fires with <paramref name="group"/>, with its heat (0 to 1), whether heat has shut it
    /// down and the heat it then has to cool to; a part with no group (or a bad one) is not in any cell.
    /// </summary>
    public void Add(int group, bool cutting, float heat = 0f, bool locked = false, float unlock = 0f)
    {
        if (group is < 1 or > ActionGroups.Count) return;
        _parts[group - 1]++;
        _cutting[group - 1] |= cutting;
        _heat[group - 1] = Math.Max(_heat[group - 1], float.IsFinite(heat) ? Math.Clamp(heat, 0f, 1f) : 0f);
        if (!locked) return;
        _locked[group - 1]++;
        _unlock[group - 1] = Math.Max(_unlock[group - 1], unlock);
    }

    /// <summary>
    /// Ends the count: a cell for every group with a part, on or off by <paramref name="on"/> (a bit a group), and
    /// firing while <paramref name="firing"/> says the fire button is held where it may fire.
    /// <paramref name="awaitingPower"/> says the hull's lasers wait for its battery, which holds
    /// <paramref name="battery"/> of its capacity and must hold <paramref name="resume"/> of it.
    /// </summary>
    public void End(ushort on, bool firing, bool awaitingPower = false, float battery = 0f, float resume = 0f)
    {
        battery = awaitingPower && float.IsFinite(battery) ? Math.Clamp(battery, 0f, 1f) : 0f;
        resume = awaitingPower && float.IsFinite(resume) ? Math.Clamp(resume, 0f, 1f) : 0f;
        var count = 0;
        for (var group = 1; group <= ActionGroups.Count; group++)
        {
            if (_parts[group - 1] == 0) continue;
            var isOn = (on & ActionGroups.Bit(group)) != 0;
            _cells[count++] = new ActionGroupCell
            {
                Group = group,
                Parts = _parts[group - 1],
                Kind = ActionPartKind.Laser,
                On = isOn,
                Firing = isOn && firing,
                Cutting = _cutting[group - 1],
                Heat = _heat[group - 1],
                Locked = _locked[group - 1],
                UnlockHeat = _unlock[group - 1],
                AwaitingPower = awaitingPower,
                Battery = battery,
                ResumeBattery = resume,
            };
        }

        Count = count;
    }

    /// <summary>Reads the flown hull: its groupable parts' groups (an override or the design's) and the beams that are cutting.</summary>
    public void Read(SessionSnapshot snapshot, uint hull, HullDesign design, ushort on, bool firing)
    {
        Begin();
        if (hull != 0)
        {
            var parts = Math.Min(design.Lasers.Length, ActionGroupsMessage.MaxParts);
            for (var part = 0; part < parts; part++)
                Add(snapshot.GroupOf(hull, design, part), IsCutting(snapshot, hull, part), HeatOf(snapshot, hull, part), IsLocked(snapshot, hull, part),
                    design.Mounts[design.Lasers[part]].Type.Laser!.UnlockHeat);
        }

        End(on, firing, AwaitsPower(snapshot, hull, out var battery, out var resume), battery, resume);
    }

    /// <summary>
    /// Whether the lasers of the flown hull <paramref name="hull"/> wait for its battery, as prediction has it, with
    /// what the battery holds and what it must hold before they fire again, as shares of its capacity. False, and
    /// zero, for any other hull.
    /// </summary>
    public static bool AwaitsPower(SessionSnapshot snapshot, uint hull, out float battery, out float resume)
    {
        battery = 0f;
        resume = 0f;
        if (hull == 0 || hull != snapshot.LaserHeatHull || !snapshot.LaserHeat.AwaitingPower) return false;
        var storage = snapshot.Flight.EnergyStorage;
        if (storage > 0f)
        {
            resume = snapshot.Flight.LaserEnergyFloor / storage;
            if ((uint)snapshot.OwnHull < (uint)snapshot.HullCount && snapshot.Hulls[snapshot.OwnHull].Id == hull)
                battery = snapshot.Hulls[snapshot.OwnHull].Energy / storage;
        }

        return true;
    }

    /// <summary>Whether the server reports laser <paramref name="part"/> of <paramref name="hull"/> as cutting rock.</summary>
    public static bool IsCutting(SessionSnapshot snapshot, uint hull, int part)
    {
        for (var b = 0; b < snapshot.LaserBeamCount; b++)
        {
            ref readonly var beam = ref snapshot.LaserBeams[b];
            if (beam.Hull == hull && beam.Part == part && beam.State == LaserBeamState.Cutting) return true;
        }

        return false;
    }

    /// <summary>The heat of laser <paramref name="part"/> of the flown hull <paramref name="hull"/>, from 0 to 1; zero for any other hull.</summary>
    public static float HeatOf(SessionSnapshot snapshot, uint hull, int part) =>
        hull != 0 && hull == snapshot.LaserHeatHull ? snapshot.LaserHeat.Of(part) : 0f;

    /// <summary>Whether heat has shut down laser <paramref name="part"/> of the flown hull <paramref name="hull"/>.</summary>
    public static bool IsLocked(SessionSnapshot snapshot, uint hull, int part) =>
        hull != 0 && hull == snapshot.LaserHeatHull && snapshot.LaserHeat.IsLocked(part);

    /// <summary>The input action of <paramref name="group"/> (1 to 10): the number keys, the tenth being <c>0</c>.</summary>
    public static Prospect.Client.Input.InputAction ActionOf(int group) => Prospect.Client.Input.InputAction.ActionGroup1 + (group - 1);

    /// <summary>
    /// The strip's rectangle: centred over the console, its bottom <see cref="ActionGroupLook.RowGap"/> above
    /// <paramref name="below"/> (the top of the module chips, or of the console when it has none). The cells shrink
    /// (never under <see cref="ActionGroupLook.MinCellWidth"/>) to keep within the screen's margins; the rest is cut off by the
    /// cell's own clipping. Empty for no cells.
    /// </summary>
    public static HudRect Place(float2 display, float scale, int count, float below, ActionGroupLook look, out float cellWidth)
    {
        float Px(float design) => MathF.Round(design * scale);
        cellWidth = 0f;
        if (count <= 0) return default;
        var height = Px(look.CellHeight);
        var gap = Px(look.Gap);
        var room = display.X - 2 * Px(FlightConsole.Side) - gap * (count - 1);
        cellWidth = Math.Max(Px(look.MinCellWidth), Math.Min(Px(look.CellWidth), MathF.Floor(room / count)));
        var total = cellWidth * count + gap * (count - 1);
        var left = MathF.Round(MathF.Round(display.X * 0.5f) - total * 0.5f);
        var bottom = below - Px(look.RowGap);
        return new HudRect(left, bottom - height, left + total, bottom);
    }

    /// <summary>The rectangle of the <paramref name="index"/>th cell of a strip placed by <see cref="Place"/>.</summary>
    public static HudRect CellRect(in HudRect strip, int index, float cellWidth, float gap) =>
        new(strip.Left + (cellWidth + gap) * index, strip.Top, strip.Left + (cellWidth + gap) * index + cellWidth, strip.Bottom);
}

/// <summary>
/// The choices of one laser's group selector: the ten groups, none, and back to the design's own. Font-free, so the
/// rules of what is chosen and what a click sends can be tested.
/// </summary>
internal static class ActionGroupSelector
{
    /// <summary>One choice for each group (1 to 10), then none, then the design's group.</summary>
    public const int Options = ActionGroups.Count + 2;

    /// <summary>The option of "no group": the part never fires.</summary>
    public const int NoneOption = ActionGroups.Count;

    /// <summary>The option of "design": the override is dropped and the group the design gave stands.</summary>
    public const int DesignOption = ActionGroups.Count + 1;

    /// <summary>The value <see cref="Prospect.Game.Client.PlayerInputBridge.SetActionGroup"/> takes for an option.</summary>
    public static byte ValueOf(int option) =>
        option == DesignOption ? ActionGroups.NotOverridden : option == NoneOption ? ActionGroups.None : (byte)(option + 1);

    /// <summary>The option that is chosen for a part: its override's, or "design" while it has none.</summary>
    public static int Chosen(byte overridden) =>
        overridden == ActionGroups.NotOverridden ? DesignOption : overridden == ActionGroups.None ? NoneOption : overridden - 1;

    /// <summary>The override the server last reported for part <paramref name="part"/> of <paramref name="hull"/>, or <see cref="ActionGroups.NotOverridden"/>.</summary>
    public static byte OverrideOf(SessionSnapshot snapshot, uint hull, int part) =>
        hull != 0 && hull == snapshot.ActionGroupHull && (uint)part < (uint)snapshot.ActionGroupCount ? snapshot.ActionGroupOverrides[part] : ActionGroups.NotOverridden;
}
