using Brutal.ImGuiApi;
using Brutal.Numerics;
using Prospect.Client.Ui;
using Prospect.Game.Client;
using Prospect.Game.World;

namespace Prospect.Client.Screens;

internal sealed partial class ScreenHost
{
    /// <summary>
    /// The fuel row under the console: one cell for each fuel the hull has a tank for, side by side across the whole
    /// console. Each cell has the name, stored out of capacity, and the net flow a second with its arrow (negative
    /// while burning, a dash at rest), and a thin strip of how full the tank is. A narrow cell drops the capacity,
    /// then the name, before it clips the flow. A hull with no tank (the electric drone) gets one cell that says so.
    /// </summary>
    private void DrawFuelRow(ImDrawListPtr draw, in FlightConsoleLayout layout, in HullView own, bool hot)
    {
        var fuel = own.Fuel;
        var count = ConsoleFuel.Count(fuel);
        var cells = Math.Max(1, count);
        var gap = Kit.Px(FlightConsole.RowGap);
        var total = layout.Right - layout.LeftX;
        var width = MathF.Floor((total - gap * (cells - 1)) / cells);
        var x = layout.LeftX;
        var shown = 0;
        for (var i = 0; i < ConsoleFuel.Kinds && count > 0; i++)
        {
            var kind = ConsoleFuel.KindAt(i);
            if (ConsoleFuel.Capacity(fuel, kind) > 0f)
            {
                DrawFuelCell(draw, layout, kind, fuel, x, width, hot);
                x += width + gap;
                shown++;
            }
        }

        if (shown == 0)
            DrawFuelCell(draw, layout, FuelKind.None, fuel, x, width, hot);
    }

    private void DrawFuelCell(ImDrawListPtr draw, in FlightConsoleLayout layout, FuelKind kind, in FuelFlow fuel, float x, float w, bool hot)
    {
        var y = layout.FuelTop;
        var h = layout.FuelRow;
        var s = Kit.Px(FlightConsole.Slant * 0.6f);
        var capacity = ConsoleFuel.Capacity(fuel, kind);
        var color = kind == FuelKind.None ? Theme.LineStrong : ConsoleFuel.Color(fuel, kind);
        var p0 = new float2(x + s, y);
        var p1 = new float2(x + w, y);
        var p2 = new float2(x + w - s, y + h);
        var p3 = new float2(x, y + h);
        draw.AddQuadFilled(p0, p1, p2, p3, Kit.Color(hot ? Theme.PanelRaised : Theme.Panel, Hud.FillAlpha));
        draw.AddQuad(p0, p1, p2, p3, Kit.Color(Hud.Border(color, hot)), 1f);
        draw.AddLine(p3, p0, Kit.Color(color, 0.85f), Kit.Px(2));

        var pad = Kit.Px(FlightConsole.Pad);
        var left = x + s * 0.5f + pad;
        var right = x + w - s * 0.5f - pad;
        var textTop = y + Kit.Px(2);
        draw.PushClipRect(new float2(x, y), new float2(x + w, y + h), true);
        if (kind == FuelKind.None)
        {
            var none = Kit.Text(draw, _ui.SansMedium, Theme.FontCaption, new float2(left, textTop), Theme.TextDim, "FUEL"u8);
            Kit.Text(draw, _ui.SansMedium, Theme.FontCaption, new float2(left + none.X + Kit.Px(FlightConsole.ColumnGap), textTop), Theme.TextMute, "NO TANKS"u8);
            draw.PopClipRect();
            return;
        }

        var amount = ConsoleFuel.Amount(fuel, kind);
        var level = ConsoleFuel.Level(fuel, kind);
        var flowValue = ConsoleFuel.Flow(fuel, kind);
        var flow = level == FuelLevel.Empty ? "EMPTY"u8 : ConsoleFuel.FlowText(flowValue);
        var flowColor = level == FuelLevel.Empty ? Theme.Bad : flowValue < 0f ? FlightConsole.DrawColor : flowValue > 0f ? FlightConsole.ChargeColor : Theme.TextMute;
        var arrow = flowValue != 0f && level != FuelLevel.Empty;
        var tuning = PowerReadout.Current;
        var arrowRoom = arrow ? Kit.Px(tuning.ArrowWidth + tuning.ArrowGap) : 0f;
        var name = ConsoleFuel.Name(kind);
        var full = ConsoleFuel.Stored(amount, capacity);
        var short_ = ConsoleFuel.Held(amount, capacity);
        var colGap = Kit.Px(FlightConsole.ColumnGap);
        var nameW = Kit.Measure(_ui.SansMedium, Theme.FontCaption, name).X;
        var flowW = Kit.Measure(_ui.Mono, Theme.FontCaption, flow).X + arrowRoom;
        var fullW = Kit.Measure(_ui.Mono, Theme.FontCaption, full).X;
        var shortW = Kit.Measure(_ui.Mono, Theme.FontCaption, short_).X;
        var room = right - left;
        var readingColor = level >= FuelLevel.Low ? Theme.Bad : Theme.Text;

        // The widest form that fits: name, stored and capacity, flow; then without capacity; then without the name.
        var showName = nameW + colGap + fullW + colGap + flowW <= room || nameW + colGap + shortW + colGap + flowW <= room;
        var showFull = nameW + colGap + fullW + colGap + flowW <= room;
        var stored = showFull ? full : short_;
        var storedW = showFull ? fullW : shortW;
        if (showName)
            Kit.Text(draw, _ui.SansMedium, Theme.FontCaption, new float2(left, textTop), Theme.TextDim, name);

        var flowLeft = right - flowW + arrowRoom;
        Kit.Text(draw, _ui.Mono, Theme.FontCaption, new float2(flowLeft, textTop), flowColor, flow);
        if (arrow)
        {
            var size = Kit.Measure(_ui.Mono, Theme.FontCaption, flow);
            DrawTrendArrow(draw, tuning, flowLeft, textTop, size.Y, flowValue > 0f, flowColor);
        }

        var storedLeft = showName ? Math.Max(left + nameW + colGap, flowLeft - arrowRoom - colGap - storedW) : left;
        Kit.Text(draw, _ui.Mono, Theme.FontCaption, new float2(storedLeft, textTop), readingColor, stored);

        // How full the tank is, along the cell's foot.
        var stripTop = y + h - Kit.Px(4);
        var stripLeft = left;
        draw.AddRectFilled(new float2(stripLeft, stripTop), new float2(right, stripTop + Kit.Px(2)), Kit.Color(Theme.Line, 0.8f));
        draw.AddRectFilled(new float2(stripLeft, stripTop), new float2(stripLeft + (right - stripLeft) * FlightConsole.Fraction(amount, capacity), stripTop + Kit.Px(2)), Kit.Color(color));
        draw.PopClipRect();
    }
}
