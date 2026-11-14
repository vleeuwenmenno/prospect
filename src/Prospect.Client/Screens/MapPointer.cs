using Brutal.ImGuiApi;
using Brutal.Numerics;
using Prospect.Client.Ui;
using Prospect.Client.World;
using Prospect.Game.Client;

namespace Prospect.Client.Screens;

/// <summary>
/// The galaxy map's own pointer gestures: left click and box, middle drag, wheel and right click. The map is one
/// full-window surface, so Dear ImGui's "pointer over the interface" is always true on it and cannot tell the map
/// from the windows drawn over it (the unit list, the course menu). The map is therefore one Dear ImGui item, and
/// a gesture is the map's only when the press lands on that item and outside every area blocked this frame. The
/// blocked areas are this frame's layout, so a window that has only now appeared, or has just grown, is respected
/// before Dear ImGui has seen it once. A gesture keeps its owner until its button is let go: a drag begun on a
/// window never becomes a map drag, and a map drag that crosses a window stays the map's.
/// <para>
/// Dear ImGui picks the hovered window when the frame starts, from where every window was last frame. On the frame a
/// window appears or grows under the pointer it still names the map, so the map's surface would take the press (its
/// active item) and the window's own buttons, drawn later, could not see it. For a point this frame blocks, the map
/// therefore gives that stale hover up before its surface is submitted, and each window drawn over the map calls
/// <see cref="Claim"/> after its <c>Begin</c>, when its rectangle is this frame's, to take it. Dear ImGui then
/// completes the click on the window's button as on any other frame.
/// </para>
/// </summary>
internal sealed class MapPointer
{
    // The map blocks at most five areas a frame (unit list, bar, layer bar, course menu, pinned card); a block past
    // the limit would be dropped silently, so the limit keeps room to spare.
    private const int MaxBlocked = 8;
    private readonly float2[] _blockedMin = new float2[MaxBlocked];
    private readonly float2[] _blockedMax = new float2[MaxBlocked];
    private int _blockedCount;
    private float2 _mouse;
    private bool _handedOver;

    /// <summary>True while the pointer is on the map itself, not on a window over it or a blocked area.</summary>
    public bool Hovered { get; private set; }

    /// <summary>True on the frame the map took a left press.</summary>
    public bool Pressed { get; private set; }

    /// <summary>True while a left gesture the map took is held.</summary>
    public bool Boxing { get; private set; }

    /// <summary>True once the held left gesture has moved far enough to be a box rather than a click.</summary>
    public bool BoxMoved { get; private set; }

    /// <summary>Where the held left gesture began.</summary>
    public float2 BoxStart { get; private set; }

    /// <summary>True on the frame the map's left gesture was let go: a click, or a box if it moved.</summary>
    public bool BoxReleased { get; private set; }

    /// <summary>True on the frame the map took a right press.</summary>
    public bool RightClicked { get; private set; }

    /// <summary>True while a middle drag the map took is held.</summary>
    public bool Panning { get; private set; }

    /// <summary>The pointer's motion this frame while the map's middle drag is held.</summary>
    public float2 Pan { get; private set; }

    /// <summary>The wheel this frame when the pointer is on the map, otherwise zero.</summary>
    public float Wheel { get; private set; }

    /// <summary>Marks an area of this frame's layout as not the map's. Call before <see cref="Begin"/>.</summary>
    public void Block(float2 min, float2 size)
    {
        if (_blockedCount == MaxBlocked)
            return;
        _blockedMin[_blockedCount] = min;
        _blockedMax[_blockedCount++] = min + size;
    }

    /// <summary>Forgets every gesture, for when the map opens.</summary>
    public void Reset()
    {
        Boxing = BoxMoved = BoxReleased = Pressed = RightClicked = Panning = false;
        Pan = default;
        Wheel = 0;
    }

    /// <summary>Ends the held left gesture without a release, as when a click only closes the course menu.</summary>
    public void CancelBox() => Boxing = BoxMoved = false;

    /// <summary>
    /// Opens the map's full-window surface and reads this frame's gestures from it. Pair with <c>ImGui.End</c>.
    /// The areas given to <see cref="Block"/> apply to this frame only.
    /// </summary>
    public void Begin(float2 display)
    {
        ImGui.SetNextWindowPos(float2.Zero);
        ImGui.SetNextWindowSize(display);
        ImGui.Begin("##map"u8, ScreenHost.RootFlags | ImGuiWindowFlags.NoScrollWithMouse);
        if (ImGui.IsWindowAppearing())
            ImGui.Internal.BringWindowToDisplayFront(ImGui.Internal.GetCurrentWindow());

        var io = ImGui.GetIO();
        var mouse = io.MousePos;
        var blocked = IsBlocked(mouse);
        _blockedCount = 0;
        _mouse = mouse;

        // Dear ImGui still names the map for a point a window covers only from this frame: give it up, so the
        // surface below cannot take the press and the window can claim it.
        var context = ImGui.GetCurrentContext();
        var hovered = context.HoveredWindow;
        _handedOver = blocked && !hovered.IsNull() && hovered.ID == ImGui.Internal.GetCurrentWindow().ID;
        if (_handedOver)
            context.HoveredWindow = default;

        ImGui.SetCursorScreenPos(float2.Zero);
        ImGui.InvisibleButton("##mapcanvas"u8, new float2(Math.Max(display.X, 1f), Math.Max(display.Y, 1f)), ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonRight | ImGuiButtonFlags.MouseButtonMiddle);
        Hovered = ImGui.IsItemHovered() && !blocked;

        Pressed = Hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left);
        if (Pressed)
        {
            Boxing = true;
            BoxMoved = false;
            BoxStart = mouse;
        }

        BoxReleased = false;
        if (Boxing)
        {
            var moved = mouse - BoxStart;
            if (MathF.Abs(moved.X) + MathF.Abs(moved.Y) > 5f)
                BoxMoved = true;
            if (!ImGui.IsMouseDown(ImGuiMouseButton.Left))
            {
                Boxing = false;
                BoxReleased = true;
            }
        }

        if (Hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Middle))
            Panning = true;
        if (Panning && !ImGui.IsMouseDown(ImGuiMouseButton.Middle))
            Panning = false;
        Pan = Panning ? io.MouseDelta : default;

        RightClicked = Hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Right);
        Wheel = Hovered ? io.MouseWheel : 0f;
    }

    /// <summary>
    /// Call right after a window drawn over the map begins (a child window too). If the map gave up a stale hover
    /// this frame and the pointer is inside this window's rectangle as laid out this frame, the window becomes the
    /// hovered one, unless a window above it already is.
    /// </summary>
    public void Claim()
    {
        if (!_handedOver)
            return;
        var window = ImGui.Internal.GetCurrentWindow();
        var rect = window.OuterRectClipped;
        if (_mouse.X < rect.Min.X || _mouse.Y < rect.Min.Y || _mouse.X >= rect.Max.X || _mouse.Y >= rect.Max.Y)
            return;
        var context = ImGui.GetCurrentContext();
        var hovered = context.HoveredWindow;
        if (hovered.IsNull() || ImGui.Internal.IsWindowAbove(window, hovered))
            context.HoveredWindow = window;
    }

    private bool IsBlocked(float2 point)
    {
        for (var i = 0; i < _blockedCount; i++)
        {
            if (point.X >= _blockedMin[i].X && point.Y >= _blockedMin[i].Y && point.X < _blockedMax[i].X && point.Y < _blockedMax[i].Y)
                return true;
        }

        return false;
    }
}

/// <summary>Where the map's unit list goes, and whether the map's heading must shrink to make room for it.</summary>
internal readonly record struct MapUnitLayout(float2 Min, float2 Size, float Header, float Row, float Pad, bool CompactRows, bool ShortHeading)
{
    /// <summary>The scrolling area that holds the rows.</summary>
    public float2 RowsMin => Min + new float2(0, Header);

    public float2 RowsSize => new(Size.X, Size.Y - Header - Pad);
}

/// <summary>
/// The interactive part of the map's unit list: its window, its scrolling rows and what a row press does. The
/// look is drawn by <c>ScreenHost</c>; this part has no fonts, so the frame order tests run it as it is.
/// </summary>
internal static class MapUnitList
{
    /// <summary>Header height in unscaled pixels: the caption, the count and the All and Clear buttons.</summary>
    public const float HeaderHeight = 40f;
    public const float FullRowHeight = 44f;
    public const float CompactRowHeight = 28f;

    // Unscaled pixels, matching the map's own heading and the sector panel above it: one left column, one gap.
    private const float Margin = HudLayout.Margin;
    private const float MinWidth = 240f;
    private const float ShortHeadingLine = 20f;
    private const float Gap = 8f;
    private const float PadBelowRows = 6f;

    /// <summary>The map's full heading under the sector panel: the gap above it and its height, in unscaled pixels.</summary>
    public const float HeadingGap = 16f;
    public const float HeadingHeight = 84f;

    /// <summary>The hover card's distance from the right edge: tucked into the top right corner, under the bar.</summary>
    public const float HoverRight = HudLayout.Margin;

    /// <summary>Where the map's full heading starts, under the sector panel.</summary>
    public static float HeadingTop(float sectorPanelBottom, float scale) => sectorPanelBottom + MathF.Round(HeadingGap * scale);

    // Like every surface: no title bar, no move, never raised by a click (so the course menu stays above it), and
    // the wheel scrolls only the rows.
    private const ImGuiWindowFlags Flags = ScreenHost.RootFlags | ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse;

    /// <summary>Keeps the hover card beside the minimum-width list by reducing its outer margin first.</summary>
    public static float HoverCardLeft(float width, float scale, float cardWidth, float preferredRight)
    {
        var room = width - MathF.Round((Margin + MinWidth + Gap + cardWidth) * scale);
        var right = Math.Clamp(room, MathF.Round(Gap * scale), MathF.Round(preferredRight * scale));
        return width - MathF.Round(cardWidth * scale) - right;
    }

    /// <summary>
    /// Lays the list out on the left, under the map's heading. Where that leaves too little room the heading
    /// shrinks to one line under the sector panel, and where it is still short the rows lose their second line.
    /// The rows scroll when they do not all fit; at least one row is always shown. The list narrows to stay left
    /// of <paramref name="rightLimit"/> (the hover card), down to a width that still shows a name.
    /// </summary>
    public static MapUnitLayout Layout(float2 display, float scale, int rows, float sectorPanelBottom, float rightLimit)
    {
        float Px(float value) => MathF.Round(value * scale);
        var header = Px(HeaderHeight);
        var full = Px(FullRowHeight);
        var compact = Px(CompactRowHeight);
        var pad = Px(PadBelowRows);
        var bottom = display.Y - Px(Margin);
        var shown = Math.Max(rows, 1);

        var top = HeadingTop(sectorPanelBottom, scale) + Px(HeadingHeight) + Px(Gap);
        var shortHeading = bottom - top - header - pad < Math.Min(shown, 3) * compact;
        if (shortHeading)
            top = sectorPanelBottom + Px(Gap) + Px(ShortHeadingLine) + Px(Gap);

        var room = bottom - top - header - pad;
        var compactRows = room < Math.Min(shown, 4) * full;
        var row = compactRows ? compact : full;
        var rowsHeight = Math.Max(row, Math.Min(shown * row, room));
        var width = Math.Min(Math.Min(HudLayout.ColumnWidth(display, scale), Math.Max(Px(MinWidth), rightLimit - Px(Gap) - Px(Margin))), display.X - Px(Margin) * 2);
        return new MapUnitLayout(new float2(Px(Margin), top), new float2(width, header + rowsHeight + pad), header, row, pad, compactRows, shortHeading);
    }

    /// <summary>The player's hulls, by id, so a row keeps its place while others are selected or come and go.</summary>
    public static int Collect(SessionSnapshot snapshot, Span<uint> ids)
    {
        var count = 0;
        for (var i = 0; i < snapshot.HullCount && count < ids.Length; i++)
        {
            if (snapshot.Hulls[i].Owner == snapshot.PlayerId)
                ids[count++] = snapshot.Hulls[i].Id;
        }

        ids[..count].Sort();
        return count;
    }

    /// <summary>Opens the list's window over the map. Pair with <see cref="End"/>.</summary>
    public static void Begin(in MapUnitLayout layout, MapPointer map)
    {
        ImGui.SetNextWindowPos(layout.Min);
        ImGui.SetNextWindowSize(layout.Size);
        ImGui.Begin("##mapunits"u8, Flags);
        if (ImGui.IsWindowAppearing())
            ImGui.Internal.BringWindowToDisplayFront(ImGui.Internal.GetCurrentWindow());
        map.Claim();
    }

    /// <summary>Opens the scrolling area for the rows. Pair with <see cref="EndRows"/>.</summary>
    public static void BeginRows(in MapUnitLayout layout, MapPointer map)
    {
        ImGui.SetCursorScreenPos(layout.RowsMin);
        ImGui.BeginChild("##mapunitrows"u8, layout.RowsSize, ImGuiChildFlags.None, ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoNav);
        map.Claim();
    }

    public static void EndRows() => ImGui.EndChild();

    public static void End() => ImGui.End();

    /// <summary>
    /// One row at the cursor, the full width of the rows. A click adds the hull to the selection or takes it out,
    /// leaving every other hull as it was. The row's id is the hull's, so a press held while the list changes can
    /// only complete on the hull it began on.
    /// </summary>
    public static bool Row(WorldView world, uint hull, float height)
    {
        ImGui.PushID(unchecked((int)hull));
        var pressed = ImGui.InvisibleButton("##unit"u8, new float2(Math.Max(ImGui.GetContentRegionAvail().X, 1f), height));
        ImGui.PopID();
        if (pressed)
            world.Select(hull, true);
        return pressed;
    }
}

/// <summary>
/// The interactive part of the course menu, which the map and the command view both open: where it opens, where it is while it drops in, its window and
/// its rows. The look and what a row orders are <c>ScreenHost</c>'s; this part has no fonts, so the frame order
/// tests run it as it is. The map blocks exactly <see cref="Shown"/>, the rectangle the window is drawn at.
/// </summary>
internal static class MapCourseMenu
{
    // Unscaled pixels.
    public const float HeaderHeight = 100f;
    public const float RowHeight = 48f;
    private const float Width = 340f;
    private const float Gap = 6f;
    private const float Drop = 6f;
    private const float AppearSeconds = 0.12f;

    // Unlike the full-window surfaces this one may come to the front: a window created with
    // NoBringToFrontOnFocus is placed behind every other, which would put it under the map.
    private const ImGuiWindowFlags Flags = (ScreenHost.RootFlags & ~ImGuiWindowFlags.NoBringToFrontOnFocus) | ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoFocusOnAppearing;

    /// <summary>The menu's size: a header and two rows.</summary>
    public static float2 Size(float scale) => new(MathF.Round(Width * scale), MathF.Round(HeaderHeight * scale) + MathF.Round(RowHeight * scale) * 2 + MathF.Round(Theme.Space2 * scale));

    /// <summary>Below and right of <paramref name="at"/>, flipped to the other side where that would leave the screen.</summary>
    public static float2 Place(float2 at, float2 size, float2 display, float scale)
    {
        var margin = MathF.Round(Theme.Space2 * scale);
        var gap = MathF.Round(Gap * scale);
        var min = at + new float2(gap, gap);
        if (min.X + size.X > display.X - margin)
            min.X = at.X - size.X - gap;
        if (min.Y + size.Y > display.Y - margin)
            min.Y = at.Y - size.Y - gap;
        min.X = Math.Clamp(min.X, margin, Math.Max(margin, display.X - size.X - margin));
        min.Y = Math.Clamp(min.Y, margin, Math.Max(margin, display.Y - size.Y - margin));
        return min;
    }

    /// <summary>How far the menu has dropped in, 0 to 1, <paramref name="time"/> seconds after it opened.</summary>
    public static float Appear(float time) => Kit.EaseOut(time / AppearSeconds);

    /// <summary>Where the menu placed at <paramref name="min"/> is drawn while it drops in.</summary>
    public static float2 Shown(float2 min, float time, float scale) => min + new float2(0, (Appear(time) - 1f) * MathF.Round(Drop * scale));

    /// <summary>The top left of row <paramref name="row"/> of the menu drawn at <paramref name="min"/>.</summary>
    public static float2 RowMin(float2 min, int row, float scale) =>
        new(min.X + MathF.Round(6 * scale), min.Y + MathF.Round(HeaderHeight * scale) + MathF.Round(Theme.Space1 * scale) + row * MathF.Round(RowHeight * scale));

    /// <summary>The size of a row of a menu of <paramref name="size"/>.</summary>
    public static float2 RowSize(float2 size, float scale) => new(size.X - MathF.Round(12 * scale), MathF.Round(RowHeight * scale) - MathF.Round(4 * scale));

    /// <summary>Opens the menu's window over the map, drawn at <paramref name="min"/>. Pair with <see cref="End"/>.</summary>
    public static void Begin(float2 min, float2 size, MapPointer map)
    {
        ImGui.SetNextWindowPos(min);
        ImGui.SetNextWindowSize(size);
        ImGui.Begin("##mapmenu"u8, Flags);
        if (ImGui.IsWindowAppearing())
            ImGui.Internal.BringWindowToDisplayFront(ImGui.Internal.GetCurrentWindow());
        map.Claim();
    }

    /// <summary>
    /// Opens the menu's window at <paramref name="min"/> in a view with no map: the command view's menu. The
    /// pointer there is the interface's own, so a press on the window is already kept from the world. Pair with
    /// <see cref="End"/>.
    /// </summary>
    public static void BeginFree(ImString name, float2 min, float2 size)
    {
        ImGui.SetNextWindowPos(min);
        ImGui.SetNextWindowSize(size);
        ImGui.Begin(name, Flags);
        if (ImGui.IsWindowAppearing())
            ImGui.Internal.BringWindowToDisplayFront(ImGui.Internal.GetCurrentWindow());
    }

    /// <summary>One row's button. A disabled row still takes the click, so the menu stays open.</summary>
    public static bool Item(ImString label, float2 rowMin, float2 rowSize)
    {
        ImGui.SetCursorScreenPos(rowMin);
        return ImGui.InvisibleButton(label, rowSize);
    }

    public static void End() => ImGui.End();
}

/// <summary>
/// The plotted jump's route on the map: a dotted line from the hull being flown to the NAV point, apart from
/// drawing so its rules can be tested. Sizes are in unscaled pixels.
/// </summary>
internal static class MapRoute
{
    /// <summary>A dash and the space after it.</summary>
    public const float Dash = 6f;
    public const float Space = 5f;

    /// <summary>The most dashes drawn, whatever the zoom: a clipped line across the whole screen needs far fewer.</summary>
    public const int MaxDashes = 1024;

    /// <summary>
    /// Whether the map shows the NAV point on the layer it is looking at, and with <paramref name="line"/> the
    /// route to it: only when the hull and the point are both on that layer, as the map is flat per layer.
    /// </summary>
    public static bool Shows(int ownLayer, int navLayer, int mapLayer, out bool line)
    {
        line = ownLayer == mapLayer && navLayer == mapLayer;
        return navLayer == mapLayer;
    }

    /// <summary>
    /// Cuts the line from <paramref name="from"/> to <paramref name="to"/> to <paramref name="clip"/>. False when
    /// none of it is inside.
    /// </summary>
    public static bool Clip(ref float2 from, ref float2 to, in HudRect clip)
    {
        var along = to - from;
        var enter = 0f;
        var leave = 1f;
        if (!Edge(-along.X, from.X - clip.Left, ref enter, ref leave) || !Edge(along.X, clip.Right - from.X, ref enter, ref leave)
            || !Edge(-along.Y, from.Y - clip.Top, ref enter, ref leave) || !Edge(along.Y, clip.Bottom - from.Y, ref enter, ref leave))
            return false;
        var start = from;
        from = start + along * enter;
        to = start + along * leave;
        return true;
    }

    /// <summary>The dashes of the clipped line, at most <see cref="MaxDashes"/>; nothing when it lies off the screen.</summary>
    public static int Dashes(float2 from, float2 to, in HudRect clip, float dash, float space)
    {
        if (!Clip(ref from, ref to, clip) || dash <= 0f)
            return 0;
        var along = to - from;
        var length = MathF.Sqrt(along.X * along.X + along.Y * along.Y);
        return Math.Min(MaxDashes, (int)MathF.Ceiling(length / (dash + space)));
    }

    /// <summary>Draws the route dotted, cut to <paramref name="clip"/>.</summary>
    public static void Dotted(ImDrawListPtr draw, float2 from, float2 to, in HudRect clip, float dash, float space, ImColor8 color, float thickness)
    {
        var count = Dashes(from, to, clip, dash, space);
        if (count == 0)
            return;
        Clip(ref from, ref to, clip);
        var along = to - from;
        var length = MathF.Sqrt(along.X * along.X + along.Y * along.Y);
        var unit = along / length;
        for (var i = 0; i < count; i++)
        {
            var start = i * (dash + space);
            draw.AddLine(from + unit * start, from + unit * Math.Min(start + dash, length), color, thickness);
        }
    }

    private static bool Edge(float p, float q, ref float enter, ref float leave)
    {
        if (MathF.Abs(p) < 1e-6f)
            return q >= 0f;
        var t = q / p;
        if (p < 0f)
            enter = Math.Max(enter, t);
        else
            leave = Math.Min(leave, t);
        return enter <= leave;
    }
}
