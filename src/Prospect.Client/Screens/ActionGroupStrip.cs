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
}

/// <summary>
/// Font-free state of the action group strip beside the flight console: one cell for each group the flown hull has a
/// part in, in key order (1 to 9, then 0), with whether it is on, firing, and cutting. Nothing is shown for a group no
/// part is in, and the strip is empty (and not drawn) without any. Worked out once a frame from what the server
/// reported (the design, its per-hull overrides and the beams); it takes no memory.
/// </summary>
internal sealed class ActionGroupStrip
{
    private readonly ActionGroupCell[] _cells = new ActionGroupCell[ActionGroups.Count];
    private readonly int[] _parts = new int[ActionGroups.Count];
    private readonly bool[] _cutting = new bool[ActionGroups.Count];

    /// <summary>How many cells there are.</summary>
    public int Count { get; private set; }

    /// <summary>The cells, in key order.</summary>
    public ReadOnlySpan<ActionGroupCell> Cells => _cells.AsSpan(0, Count);

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
        Count = 0;
    }

    /// <summary>Counts a part that fires with <paramref name="group"/>; a part with no group (or a bad one) is not in any cell.</summary>
    public void Add(int group, bool cutting)
    {
        if (group is < 1 or > ActionGroups.Count) return;
        _parts[group - 1]++;
        _cutting[group - 1] |= cutting;
    }

    /// <summary>
    /// Ends the count: a cell for every group with a part, on or off by <paramref name="on"/> (a bit a group), and
    /// firing while <paramref name="firing"/> says the fire button is held where it may fire.
    /// </summary>
    public void End(ushort on, bool firing)
    {
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
                Add(snapshot.GroupOf(hull, design, part), IsCutting(snapshot, hull, part));
        }

        End(on, firing);
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
