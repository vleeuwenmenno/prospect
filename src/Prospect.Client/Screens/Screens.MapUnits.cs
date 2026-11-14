using Brutal.ImGuiApi;
using Brutal.Numerics;
using Prospect.Client.Ui;
using Prospect.Client.World;
using Prospect.Game.Client;
using Prospect.Game.Protocol;
using Prospect.Game.World;

namespace Prospect.Client.Screens;

internal sealed partial class ScreenHost
{
    private uint[] _mapUnits = [];
    private int _mapUnitCount;

    /// <summary>Collects the player's hulls for the map's unit list and places the list for this frame.</summary>
    private MapUnitLayout LayoutMapUnits(SessionSnapshot snapshot, WorldView world)
    {
        // Sized once to the snapshot's hull capacity, never per frame.
        if (_mapUnits.Length < snapshot.Hulls.Length)
            _mapUnits = new uint[snapshot.Hulls.Length];
        _mapUnitCount = MapUnitList.Collect(snapshot, _mapUnits);
        var display = _ui.DisplaySize;
        return MapUnitList.Layout(display, Kit.Scale, _mapUnitCount, SectorColumnBottom(world, false), MapUnitList.HoverCardLeft(display.X, Kit.Scale, MapHoverWidth, MapUnitList.HoverRight));
    }

    /// <summary>
    /// The map's unit list: every hull the player owns, which of them are selected, and so which the course menu
    /// orders. A click on a row adds that hull or takes it out; All and Clear change the whole selection. The rows
    /// are the same selection the map's click and box and the command view's fleet list change.
    /// </summary>
    private void DrawMapUnits(SessionSnapshot snapshot, WorldView world, Galaxy galaxy, in MapUnitLayout layout, float fade)
    {
        MapUnitList.Begin(layout, _mapPointer);
        Kit.Fade = fade;
        var draw = ImGui.GetWindowDrawList();
        var min = layout.Min;
        var size = layout.Size;
        Kit.PanelFrame(draw, min, min + size);

        // Header: what the list is, how many are selected, and the two whole-selection buttons at the right.
        var line = min + new float2(Kit.Px(Theme.Space4), Kit.Px(Theme.Space3));
        var caption = Kit.Text(draw, _ui.SansMedium, Theme.FontCaption, line, Theme.Accent, "UNITS"u8).X;
        var selected = world.Selection.Length;
        var buttonPad = Kit.Px(Theme.Space2) * 2;
        var clearWidth = Kit.Measure(_ui.SansMedium, Theme.FontCaption, "CLEAR"u8).X + buttonPad;
        var allWidth = Kit.Measure(_ui.SansMedium, Theme.FontCaption, "ALL"u8).X + buttonPad;
        var buttonY = line.Y - Kit.Px(Theme.Space1);
        var right = min.X + size.X - Kit.Px(Theme.Space2);
        var allX = right - clearWidth - Kit.Px(Theme.Space1) - allWidth;
        var countAt = line + new float2(caption + Kit.Px(Theme.Space3), 0);
        var count = selected == 0 ? "NONE SELECTED"u8 : Kit.F($"{selected} OF {_mapUnitCount} SELECTED");
        if (Kit.Measure(_ui.Mono, Theme.FontCaption, count).X > allX - countAt.X - Kit.Px(Theme.Space2))
            count = Kit.F($"{selected}/{_mapUnitCount}");
        Kit.Text(draw, _ui.Mono, Theme.FontCaption, countAt, selected == 0 ? Theme.Accent : Theme.TextDim, count);
        ImGui.SetCursorScreenPos(new float2(right - clearWidth, buttonY));
        if (Kit.LinkButton("CLEAR"u8))
            world.ClearSelection();
        ImGui.SetCursorScreenPos(new float2(allX, buttonY));
        if (Kit.LinkButton("ALL"u8))
            world.SelectAllOwned();

        var divider = min.Y + layout.Header - 0.5f;
        draw.AddLine(new float2(min.X + Kit.Px(6), divider), new float2(min.X + size.X - Kit.Px(6), divider), Kit.Color(Theme.Line));

        MapUnitList.BeginRows(layout, _mapPointer);
        var rows = ImGui.GetWindowDrawList();
        var designs = snapshot.Content.Designs;
        for (var i = 0; i < _mapUnitCount; i++)
        {
            var id = _mapUnits[i];
            if (!world.TryGetHull(id, out var hull))
                continue;

            var rowMin = ImGui.GetCursorScreenPos();
            MapUnitList.Row(world, id, layout.Row);
            var rowSize = ImGui.GetItemRectSize();
            var hot = Kit.Animate(ImGui.GetItemID(), ImGui.IsItemHovered() ? 1f : 0f);
            var on = world.IsSelected(id);
            DrawMapUnitRow(rows, rowMin, rowSize, hull, designs[Math.Min(hull.Design, designs.Length - 1)], galaxy, world, on, hot, layout.CompactRows);
        }

        MapUnitList.EndRows();
        MapUnitList.End();
    }

    /// <summary>
    /// One row of the unit list: a check box that is filled when the hull is selected, its name and number, and
    /// what it is doing. A full row adds its class and the sector it is in; a compact one keeps one line.
    /// </summary>
    private void DrawMapUnitRow(ImDrawListPtr draw, float2 rowMin, float2 rowSize, in HullView hull, HullDesign design, Galaxy galaxy, WorldView world, bool on, float hot, bool compact)
    {
        var rowMax = rowMin + rowSize;
        draw.AddRectFilled(rowMin, rowMax, Kit.Color(Theme.PanelRaised, on ? 0.95f : 0.55f * hot));
        if (on)
            draw.AddRectFilled(rowMin, rowMin + new float2(Kit.Px(3), rowSize.Y), Kit.Color(Theme.Accent));
        draw.AddLine(new float2(rowMin.X, rowMax.Y - 0.5f), new float2(rowMax.X, rowMax.Y - 0.5f), Kit.Color(Theme.Line, 0.6f));

        var top = compact ? rowMin.Y + MathF.Round((rowSize.Y - Kit.Px(17)) * 0.5f) : rowMin.Y + Kit.Px(5);
        var box = Kit.Px(12);
        var boxMin = new float2(rowMin.X + Kit.Px(Theme.Space3), top + Kit.Px(3));
        if (on)
        {
            draw.AddRectFilled(boxMin, boxMin + new float2(box, box), Kit.Color(Theme.Accent));
            var ink = Kit.Color(Theme.AccentInk);
            draw.AddLine(boxMin + new float2(box * 0.22f, box * 0.52f), boxMin + new float2(box * 0.42f, box * 0.72f), ink, Kit.Px(1.6f));
            draw.AddLine(boxMin + new float2(box * 0.42f, box * 0.72f), boxMin + new float2(box * 0.78f, box * 0.3f), ink, Kit.Px(1.6f));
        }
        else
        {
            draw.AddRect(boxMin, boxMin + new float2(box, box), Kit.Color(Kit.Mix(Theme.LineStrong, Theme.Accent, hot)), 0f, ImDrawFlags.None, Kit.Px(1));
        }

        var textX = boxMin.X + box + Kit.Px(Theme.Space2);
        var edge = rowMax.X - Kit.Px(Theme.Space3);

        // What the hull is doing, as the fleet list says it.
        uint statusColor;
        ReadOnlySpan<byte> status;
        if (hull.Order.Kind == OrderKind.Jump || hull.Jump != JumpPhase.None)
        {
            statusColor = Theme.Info;
            status = "JUMPING"u8;
        }
        else if (hull.Order.Kind == OrderKind.MoveTo)
        {
            statusColor = Theme.Accent;
            var distance = hull.Order.Target.RelativeTo(world.HullPosition(hull)).Length();
            status = compact ? "MOVING"u8 : distance >= 1000f ? Kit.F($"MOVING {distance / 1000f:0.0} KM") : Kit.F($"MOVING {distance:0} M");
        }
        else
        {
            statusColor = Theme.TextMute;
            status = "HOLDING"u8;
        }

        // The right-hand tag: the class on a full row, what the hull is doing on a compact one.
        ReadOnlySpan<byte> tag = compact ? status
            : hull.IsOwn ? "YOU"u8
            : design.Class switch
            {
                HullClass.Fighter => "FIGHTER"u8,
                HullClass.Station => "STATION"u8,
                _ => "SHIP"u8,
            };
        var tagColor = compact ? statusColor : hull.IsOwn ? Theme.Accent : Theme.TextMute;
        var tagWidth = Kit.Measure(_ui.SansMedium, Theme.FontCaption, tag).X;
        Kit.Text(draw, _ui.SansMedium, Theme.FontCaption, new float2(edge - tagWidth, top + Kit.Px(2)), tagColor, tag);

        // A compact row has one line, so its status icons sit between the name and the tag; a full row keeps them
        // for the second line.
        var environment = hull.Environment;
        var iconsWidth = compact ? HullStatus.IconsWidth(environment, Kit.Px(StatusLook.IconSize), Kit.Px(StatusLook.IconGap)) : 0f;
        var iconsLeft = edge - tagWidth - Kit.Px(Theme.Space2) - iconsWidth;
        var iconsMiddle = top + Kit.Px(9);
        var name = Kit.F($"{design.Name} {(hull.Id & 0xFFFF) + 1}");
        Kit.ClippedText(draw, _ui.SansMedium, Theme.FontSmall, new float2(textX, top), on ? Theme.Text : Kit.Mix(Theme.TextDim, Theme.Text, hot), name, Math.Max(0f, edge - tagWidth - Kit.Px(Theme.Space2) - textX - (iconsWidth > 0f ? iconsWidth + Kit.Px(Theme.Space2) : 0f)));
        if (compact)
        {
            if (iconsWidth > 0f)
            {
                DrawStatusIcons(draw, environment, iconsLeft, iconsMiddle);
                DrawStatusTip(environment, iconsLeft, iconsMiddle, true);
            }

            return;
        }

        var sector = galaxy.SectorOf(world.HullPosition(hull));
        var second = new float2(textX, rowMin.Y + Kit.Px(24));
        var where = Kit.Text(draw, _ui.Mono, Theme.FontCaption, second, Theme.TextMute, Kit.F($"{sector.X} {sector.Y} {sector.Z}")).X;
        var statusLeft = second.X + where + Kit.Px(Theme.Space3);
        var statusRight = edge;
        var middle = second.Y + Kit.Px(7);
        if (HullStatus.Count(environment) > 0)
        {
            // Icons and words where the second line has room for them, the icons alone where it has not.
            var statusWidth = Kit.Measure(_ui.Mono, Theme.FontCaption, status).X;
            var words = StatusLineWidth(environment);
            var icons = HullStatus.IconsWidth(environment, Kit.Px(StatusLook.IconSize), Kit.Px(StatusLook.IconGap));
            if (statusLeft + statusWidth + Kit.Px(Theme.Space3) + words <= edge)
            {
                DrawStatusLine(draw, environment, edge - words, middle, edge);
                statusRight = edge - words - Kit.Px(Theme.Space3);
            }
            else
            {
                DrawStatusIcons(draw, environment, edge - icons, middle);
                DrawStatusTip(environment, edge - icons, middle, true);
                statusRight = edge - icons - Kit.Px(Theme.Space2);
            }
        }

        Kit.ClippedText(draw, _ui.Mono, Theme.FontCaption, new float2(statusLeft, second.Y), statusColor, status, Math.Max(0f, statusRight - statusLeft));
    }
}
