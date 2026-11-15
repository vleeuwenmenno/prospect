using System.Numerics;
using Brutal.Numerics;
using Prospect.Game.World;

namespace Prospect.Client.Screens;

/// <summary>
/// Which fuel's tanks are called out on the flown hull, and the pointer's part in it, apart from drawing so it can
/// be tested without fonts. A click on a fuel's cell of the console's fuel row opens that fuel's callout; a click
/// on the same fuel again closes it and a click on another fuel switches. Escape closes it through the back steps
/// (<see cref="ScreenHost.BackStep.FuelCallout"/>). It stays open while the pointer is captured again, and belongs
/// to one hull: another hull, or a hull with no tank of the fuel left, closes it.
/// </summary>
internal sealed class FuelCallout
{
    private uint _hull;

    /// <summary>The fuel whose tanks are called out, or <see cref="FuelKind.None"/> while closed.</summary>
    public FuelKind Open { get; private set; }

    /// <summary>True while a fuel's tanks are called out.</summary>
    public bool IsOpen => Open != FuelKind.None;

    /// <summary>Opens <paramref name="kind"/>, or closes the callout when it is the fuel already open.</summary>
    public void Toggle(FuelKind kind)
    {
        if (kind == FuelKind.None)
            return;
        Open = Open == kind ? FuelKind.None : kind;
    }

    /// <summary>Opens <paramref name="kind"/> on <paramref name="hull"/>, as a scripted run asks for it.</summary>
    public void Show(uint hull, FuelKind kind)
    {
        _hull = hull;
        Open = kind;
    }

    /// <summary>Closes the callout.</summary>
    public void Close() => Open = FuelKind.None;

    /// <summary>
    /// The fuel under the pointer on the console's fuel row, read as the console's hover is
    /// (<see cref="FlightConsole.Hovered"/>: only a pointer shown for the heads-up display and over no window), and
    /// a <paramref name="clicked"/> press on it opens, closes or switches the callout. None off the row, in a gap
    /// between cells, or on a hull with no tank. The pointer is the world's to ignore here: in the pilot view a
    /// shown pointer reaches no world button (<c>PointerRouter.BeginWorldMouse</c>), so nothing else needs blocking.
    /// </summary>
    public FuelKind Point(in FlightConsoleLayout layout, in FuelFlow fuel, float2 pointer, bool pointable, bool overInterface, bool clicked)
    {
        if (FlightConsole.Hovered(layout, pointer, pointable, overInterface) != ConsoleBar.Fuel)
            return FuelKind.None;
        var kind = ConsoleFuel.CellAt(layout, fuel, pointer);
        if (clicked)
            Toggle(kind);
        return kind;
    }

    /// <summary>
    /// Keeps the callout to the hull it was opened on: a change of hull closes it, and so does a hull that has no
    /// tank of the open fuel left, as its cell is then gone from the row.
    /// </summary>
    public void Follow(uint hull, in FuelFlow fuel)
    {
        if (hull != _hull)
        {
            _hull = hull;
            Open = FuelKind.None;
            return;
        }

        if (Open != FuelKind.None && !(ConsoleFuel.Capacity(fuel, Open) > 0f))
            Open = FuelKind.None;
    }
}

/// <summary>One tank of the open fuel as the callout sees it this frame.</summary>
internal struct CalloutTank
{
    /// <summary>The tank block's centre in the hull's frame, in metres: what grouping goes by, so groups do not change as the view turns.</summary>
    public Vector3 Local;

    /// <summary>The block's centre on the screen, and whether it is in front of the camera.</summary>
    public float2 Screen;

    public bool OnScreen;

    /// <summary>What the tank holds and can hold, in kilograms.</summary>
    public float Kilograms;

    public float Capacity;

    /// <summary>True when the hull has lost the tank's block.</summary>
    public bool Lost;
}

/// <summary>One label of the callout: a tank, or a group of tanks near each other, and where it goes.</summary>
internal struct CalloutLabel
{
    /// <summary>Where the leader line ends: the mean of the group's tanks on the screen.</summary>
    public float2 Anchor;

    /// <summary>False when none of the group's tanks is in front of the camera: no label is drawn.</summary>
    public bool Visible;

    /// <summary>How many tanks the label stands for, and how many of them are lost.</summary>
    public int Tanks;

    public int Lost;

    /// <summary>The first of its tanks, in tank order: the one a single label tells of.</summary>
    public int First;

    /// <summary>What its tanks hold and can hold together, in kilograms.</summary>
    public float Kilograms;

    public float Capacity;

    /// <summary>The label's box, once placed, and which side of the hull it went to.</summary>
    public HudRect Rect;

    public bool Left;
}

/// <summary>
/// The arithmetic of the fuel callout: which tanks share a label, and where the labels go so none covers another or
/// leaves its bounds. Plain loops over fixed buffers; nothing allocates and the same input gives the same output.
/// </summary>
internal static class FuelCalloutLayout
{
    /// <summary>The most labels a callout has; the content may ask for fewer.</summary>
    public const int MaxLabels = 8;

    /// <summary>
    /// Groups <paramref name="tanks"/> into at most <paramref name="maxLabels"/> labels and returns how many.
    /// With no more tanks than labels each tank has its own, in tank order. With more, groups are found in the
    /// hull's frame: the first tank starts a group, then again and again the tank farthest from every group begun
    /// so far (the lowest index on a tie), and each tank joins the nearest of those. <paramref name="group"/> gets
    /// each tank's label.
    /// </summary>
    public static int Group(ReadOnlySpan<CalloutTank> tanks, int maxLabels, Span<int> group, Span<CalloutLabel> labels)
    {
        var count = tanks.Length;
        var most = Math.Clamp(maxLabels, 1, Math.Min(MaxLabels, labels.Length));
        if (count == 0)
            return 0;

        Span<int> seeds = stackalloc int[MaxLabels];
        var made = 0;
        if (count <= most)
        {
            for (var i = 0; i < count; i++)
            {
                seeds[i] = i;
                group[i] = i;
            }

            made = count;
        }
        else
        {
            seeds[made++] = 0;
            while (made < most)
            {
                var best = -1;
                var bestDistance = -1f;
                for (var i = 0; i < count; i++)
                {
                    var nearest = float.MaxValue;
                    for (var s = 0; s < made; s++)
                        nearest = Math.Min(nearest, Vector3.DistanceSquared(tanks[i].Local, tanks[seeds[s]].Local));
                    if (nearest > bestDistance)
                    {
                        bestDistance = nearest;
                        best = i;
                    }
                }

                // Every tank left sits on a group already begun: more labels would say nothing more.
                if (bestDistance <= 0f)
                    break;
                seeds[made++] = best;
            }

            for (var i = 0; i < count; i++)
            {
                var nearest = 0;
                var distance = float.MaxValue;
                for (var s = 0; s < made; s++)
                {
                    var d = Vector3.DistanceSquared(tanks[i].Local, tanks[seeds[s]].Local);
                    if (d < distance)
                    {
                        distance = d;
                        nearest = s;
                    }
                }

                group[i] = nearest;
            }
        }

        for (var s = 0; s < made; s++)
            labels[s] = new CalloutLabel { First = -1 };
        Span<int> shown = stackalloc int[MaxLabels];
        shown.Clear();
        for (var i = 0; i < count; i++)
        {
            ref var label = ref labels[group[i]];
            ref readonly var tank = ref tanks[i];
            if (label.First < 0)
                label.First = i;
            label.Tanks++;
            if (tank.Lost)
                label.Lost++;
            label.Kilograms += tank.Lost ? 0f : Math.Max(0f, tank.Kilograms);
            label.Capacity += Math.Max(0f, tank.Capacity);
            if (tank.OnScreen)
            {
                label.Anchor += tank.Screen;
                shown[group[i]]++;
            }
        }

        for (var s = 0; s < made; s++)
        {
            labels[s].Visible = shown[s] > 0;
            if (shown[s] > 0)
                labels[s].Anchor /= shown[s];
        }

        return made;
    }

    /// <summary>
    /// Places the visible labels in two columns beside the hull, each label the size <paramref name="sizes"/> gives
    /// it. A label goes to the side of <paramref name="centre"/> its anchor is on; a column stands
    /// <paramref name="offset"/> beyond the farthest anchor on its side, its labels in the order of their anchors
    /// down the screen, as near their anchors as <paramref name="gap"/> between them allows, all inside
    /// <paramref name="bounds"/>. When the screen's edge pushes the two columns into each other, every label goes
    /// to the side with more room.
    /// </summary>
    public static void Place(Span<CalloutLabel> labels, int count, ReadOnlySpan<float2> sizes, float2 centre, in HudRect bounds, float offset, float gap)
    {
        for (var i = 0; i < count; i++)
            labels[i].Left = labels[i].Anchor.X < centre.X;
        PlaceColumns(labels, count, sizes, bounds, offset, gap);
        if (!ColumnsOverlap(labels, count))
            return;

        var left = centre.X - bounds.Left >= bounds.Right - centre.X;
        for (var i = 0; i < count; i++)
            labels[i].Left = left;
        PlaceColumns(labels, count, sizes, bounds, offset, gap);
    }

    /// <summary>True when two visible labels share any area.</summary>
    public static bool AnyOverlap(ReadOnlySpan<CalloutLabel> labels, int count)
    {
        for (var a = 0; a < count; a++)
        {
            for (var b = a + 1; b < count; b++)
            {
                if (labels[a].Visible && labels[b].Visible && labels[a].Rect.Overlaps(labels[b].Rect))
                    return true;
            }
        }

        return false;
    }

    private static bool ColumnsOverlap(ReadOnlySpan<CalloutLabel> labels, int count)
    {
        for (var a = 0; a < count; a++)
        {
            for (var b = 0; b < count; b++)
            {
                if (labels[a].Visible && labels[b].Visible && labels[a].Left && !labels[b].Left && labels[a].Rect.Overlaps(labels[b].Rect))
                    return true;
            }
        }

        return false;
    }

    private static void PlaceColumns(Span<CalloutLabel> labels, int count, ReadOnlySpan<float2> sizes, in HudRect bounds, float offset, float gap)
    {
        PlaceColumn(labels, count, sizes, bounds, offset, gap, true);
        PlaceColumn(labels, count, sizes, bounds, offset, gap, false);
    }

    private static void PlaceColumn(Span<CalloutLabel> labels, int count, ReadOnlySpan<float2> sizes, in HudRect bounds, float offset, float gap, bool left)
    {
        // The column's labels in the order of their anchors down the screen, the lower index first on a tie.
        Span<int> order = stackalloc int[MaxLabels];
        var n = 0;
        var edge = left ? float.MaxValue : float.MinValue;
        for (var i = 0; i < count && n < MaxLabels; i++)
        {
            if (!labels[i].Visible || labels[i].Left != left)
                continue;
            var at = n++;
            while (at > 0 && labels[order[at - 1]].Anchor.Y > labels[i].Anchor.Y)
            {
                order[at] = order[at - 1];
                at--;
            }

            order[at] = i;
            edge = left ? Math.Min(edge, labels[i].Anchor.X) : Math.Max(edge, labels[i].Anchor.X);
        }

        if (n == 0)
            return;

        // Down the column: each label centred on its anchor's height where it can be, never above the one before.
        var below = bounds.Top;
        for (var k = 0; k < n; k++)
        {
            ref var label = ref labels[order[k]];
            var size = sizes[order[k]];
            var x = left ? edge - offset - size.X : edge + offset;
            x = Math.Clamp(x, bounds.Left, Math.Max(bounds.Left, bounds.Right - size.X));
            var y = Math.Max(below, label.Anchor.Y - size.Y * 0.5f);
            label.Rect = new HudRect(x, y, x + size.X, y + size.Y);
            below = y + size.Y + gap;
        }

        // Back up from the bottom: a column pushed past it moves up as a whole, still a gap apart.
        var limit = bounds.Bottom;
        for (var k = n - 1; k >= 0; k--)
        {
            ref var label = ref labels[order[k]];
            if (label.Rect.Bottom <= limit)
                break;
            var height = label.Rect.Height;
            var top = Math.Max(bounds.Top, limit - height);
            label.Rect = label.Rect with { Top = top, Bottom = top + height };
            limit = top - gap;
        }
    }
}
