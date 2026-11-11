using Brutal.ImGuiApi;
using Brutal.Numerics;
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
/// </summary>
internal sealed class MapPointer
{
    private const int MaxBlocked = 2;
    private readonly float2[] _blockedMin = new float2[MaxBlocked];
    private readonly float2[] _blockedMax = new float2[MaxBlocked];
    private int _blockedCount;

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
        ImGui.SetCursorScreenPos(float2.Zero);
        ImGui.InvisibleButton("##mapcanvas"u8, new float2(Math.Max(display.X, 1f), Math.Max(display.Y, 1f)), ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonRight | ImGuiButtonFlags.MouseButtonMiddle);
        Hovered = ImGui.IsItemHovered() && !IsBlocked(mouse);
        _blockedCount = 0;

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

    // Unscaled pixels, matching the map's own heading and the sector panel above it.
    private const float Margin = 24f;
    private const float Width = 320f;
    private const float MinWidth = 240f;
    private const float FullHeadingBottom = 256f;
    private const float ShortHeadingLine = 20f;
    private const float Gap = 8f;
    private const float PadBelowRows = 6f;

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

        var top = Px(FullHeadingBottom) + Px(Gap);
        var shortHeading = bottom - top - header - pad < Math.Min(shown, 3) * compact;
        if (shortHeading)
            top = sectorPanelBottom + Px(Gap) + Px(ShortHeadingLine) + Px(Gap);

        var room = bottom - top - header - pad;
        var compactRows = room < Math.Min(shown, 4) * full;
        var row = compactRows ? compact : full;
        var rowsHeight = Math.Max(row, Math.Min(shown * row, room));
        var width = Math.Min(Math.Min(Px(Width), Math.Max(Px(MinWidth), rightLimit - Px(Gap) - Px(Margin))), display.X - Px(Margin) * 2);
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

    /// <summary>Opens the list's window. Pair with <see cref="End"/>.</summary>
    public static void Begin(in MapUnitLayout layout)
    {
        ImGui.SetNextWindowPos(layout.Min);
        ImGui.SetNextWindowSize(layout.Size);
        ImGui.Begin("##mapunits"u8, Flags);
        if (ImGui.IsWindowAppearing())
            ImGui.Internal.BringWindowToDisplayFront(ImGui.Internal.GetCurrentWindow());
    }

    /// <summary>Opens the scrolling area for the rows. Pair with <see cref="EndRows"/>.</summary>
    public static void BeginRows(in MapUnitLayout layout)
    {
        ImGui.SetCursorScreenPos(layout.RowsMin);
        ImGui.BeginChild("##mapunitrows"u8, layout.RowsSize, ImGuiChildFlags.None, ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoNav);
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
