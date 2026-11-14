using Brutal.ImGuiApi;
using Brutal.Numerics;
using Prospect.Client.Ui;
using Prospect.Game.Client;
using Prospect.Game.World;

namespace Prospect.Client.Screens;

internal sealed partial class ScreenHost
{
    // The console's only memory: how far each bar's recent loss still shows, as a fraction of the bar. It belongs
    // to one hull; another hull, or a gap in drawing (the map, the command view, another session), starts afresh.
    private float _trailShield;
    private float _trailHull;
    private float _trailEnergy;
    private uint _consoleHull;
    private int _consoleFrame = int.MinValue;

    /// <summary>
    /// The pilot console at the bottom centre: shield and hull on the left, energy and boost heat on the right,
    /// speed in a hexagon between them, and the dampeners beside the boost heat. Each bar is a row of slanted
    /// segments in a slanted frame. Every reading has a word or a number beside its colour, so
    /// nothing is told by colour alone. With the pointer <paramref name="pointable"/> (the release key held in the
    /// pilot view), the row under it is lit and its figures are shown above the console.
    /// </summary>
    private void DrawFlightConsole(ImDrawListPtr draw, SessionSnapshot snapshot, in HullView own, float speed, bool pointable)
    {
        // The live figures of the blocks the hull has left, except the hull's full strength: that is the
        // blueprint's, so a damaged hull's bar and its tooltip read against what it had when built.
        var flight = FlightConsole.WithBlueprintHull(snapshot.Flight, snapshot.Content.Designs, own.Design);
        var layout = FlightConsole.Layout(_ui.DisplaySize, Kit.Scale);
        var row = layout.Row;
        var second = layout.Top + row + layout.RowGap;
        var hovered = FlightConsole.Hovered(layout, ImGui.GetIO().MousePos, pointable, _ui.PointerOverInterface);

        var shield = FlightConsole.Fraction(own.Shield, flight.ShieldCapacity);
        var hull = FlightConsole.Fraction(own.Hull, flight.HullPoints);
        var energy = FlightConsole.Fraction(own.Energy, flight.EnergyStorage);
        var heat = FlightConsole.Fraction(own.Heat, 1f);

        var frame = ImGui.GetFrameCount();
        if (own.Id != _consoleHull || frame - _consoleFrame > 1)
        {
            _consoleHull = own.Id;
            _trailShield = shield;
            _trailHull = hull;
            _trailEnergy = energy;
        }

        _consoleFrame = frame;
        var dt = _ui.DeltaTime;
        _trailShield = FlightConsole.Trail(_trailShield, shield, dt);
        _trailHull = FlightConsole.Trail(_trailHull, hull, dt);
        _trailEnergy = FlightConsole.Trail(_trailEnergy, energy, dt);

        // Left: what keeps the hull alive.
        // A broken shield says so: the bar still shows the charge it is building back, dimmed, because that charge
        // takes no hits until the simulation brings the shield online again. The mark is where it restarts.
        var hasShield = flight.ShieldCapacity > 0f;
        var offline = hasShield && own.ShieldOffline;
        var restart = offline ? FlightConsole.RestartMark(flight) : 0f;
        ConsoleRow(draw, layout, layout.LeftX, layout.Top, ConsoleIcon.Shield, "SHIELD"u8, FlightConsole.ShieldColor(hasShield, offline), out var track, hot: hovered == ConsoleBar.Shield);
        if (restart > 0f)
            DrawShieldRestart(draw, layout, FlightConsole.RestartProgress(own.Shield, flight), track);
        else if (offline)
            ConsoleReading(draw, layout, layout.LeftX, layout.Top, "OFFLINE"u8, _ui.SansMedium, Theme.Bad, track);
        else if (hasShield)
            ConsoleReading(draw, layout, layout.LeftX, layout.Top, Points(own.Shield, flight.ShieldCapacity), _ui.Mono, Theme.Text, track);
        else
            ConsoleReading(draw, layout, layout.LeftX, layout.Top, "NONE"u8, _ui.SansMedium, Theme.TextMute, track);
        ConsoleBars.Segments(draw, track, shield, _trailShield, offline ? Theme.LineStrong : Theme.Info, false);
        if (restart > 0f)
            ConsoleBars.Marker(draw, track, restart, 0.85f);

        var hullColor = FlightConsole.HullColor(hull);
        ConsoleRow(draw, layout, layout.LeftX, second, ConsoleIcon.Hull, "HULL"u8, hullColor, out track, hot: hovered == ConsoleBar.Hull);
        ConsoleReading(draw, layout, layout.LeftX, second, Points(own.Hull, flight.HullPoints), _ui.Mono, hull < FlightConsole.HullLow ? Theme.Bad : Theme.Text, track);
        ConsoleBars.Segments(draw, track, hull, _trailHull, hullColor, false);

        // Right: the battery, and the boost drive's heat.
        var energyColor = energy < 0.15f ? Theme.Bad : Theme.Accent;
        ConsoleRow(draw, layout, layout.RightX, layout.Top, ConsoleIcon.Energy, "ENERGY"u8, energyColor, out track, hot: hovered == ConsoleBar.Energy);
        DrawEnergyReadings(draw, layout, own, flight, energy, track);
        ConsoleBars.Segments(draw, track, energy, _trailEnergy, energyColor, false);

        // The battery's boost floor: below the mark boost will not start, though boost already running carries on
        // until the battery is empty.
        if (flight.EnergyStorage > 0f)
            ConsoleBars.Marker(draw, track, FlightConsole.Fraction(flight.BoostEnergyFloor, flight.EnergyStorage), 0.85f);

        // Why boost is unavailable, when it is: the drive overheated and is cooling, or the battery is below its floor.
        var noEnergy = FlightConsole.BoostStarved(own, flight);
        var heatColor = own.BoostLocked || noEnergy ? Theme.Bad : FlightConsole.HeatColor(heat);
        ConsoleRow(draw, layout, layout.RightX, second, ConsoleIcon.Heat, own.Boosting ? "BOOSTING"u8 : "BOOST HEAT"u8, heatColor, out track, own.Boosting, hovered == ConsoleBar.Heat);
        DrawHeatReadings(draw, layout, second, own.BoostLocked ? "OVERHEAT"u8 : noEnergy ? "NO ENERGY"u8 : Kit.F($"{heat * 100f:0} %"), own.BoostLocked || noEnergy, heatColor, snapshot.Dampeners, ref track);
        ConsoleBars.Segments(draw, track, heat, heat, own.BoostLocked ? Theme.Bad : heatColor, !own.BoostLocked);

        // While locked, the mark is where the drive has cooled enough to boost again.
        if (own.BoostLocked)
            ConsoleBars.Marker(draw, track, FlightModel.BoostUnlockHeat, 0.7f);

        // Centre: speed and its unit in a hexagon with a quiet outline; the dampeners are said in the heat row.
        var cx = layout.CenterX;
        var top = layout.Top;
        var reach = Kit.Px(FlightConsole.Center * 0.5f);
        Hud.Pointed(draw, new float2(cx - reach, top), new float2(cx + reach, top + layout.Height), Kit.Px(FlightConsole.HexPoint), Theme.LineStrong, 0.9f, false);

        var shown = float.IsFinite(speed) ? speed : 0f;
        var speedText = Kit.F($"{shown:0}");
        var speedSize = Kit.Measure(_ui.Mono, FlightConsole.SpeedFont, speedText);
        Kit.Text(draw, _ui.Mono, FlightConsole.SpeedFont, new float2(cx - speedSize.X * 0.5f, top + Kit.Px(FlightConsole.SpeedTop)), Theme.Text, speedText);
        var unit = Kit.Measure(_ui.SansMedium, Theme.FontCaption, "M/S"u8);
        Kit.Text(draw, _ui.SansMedium, Theme.FontCaption, new float2(cx - unit.X * 0.5f, top + Kit.Px(FlightConsole.UnitTop)), Theme.TextMute, "M/S"u8);

        // The hovered row's figures, above the console and over any window: a tooltip, which takes no input.
        if (hovered != ConsoleBar.None)
        {
            var size = ConsoleTip.Measure(_ui.Mono, _ui.SansMedium, Kit.Scale, hovered, own, flight);
            var rect = FlightConsole.TipRect(layout, hovered, size, _ui.DisplaySize, Kit.Scale);
            var tone = hovered switch
            {
                ConsoleBar.Shield => offline ? Theme.Bad : hasShield ? Theme.Info : Theme.LineStrong,
                ConsoleBar.Hull => hullColor,
                ConsoleBar.Energy => energyColor,
                _ => heatColor,
            };
            ConsoleTip.Draw(ImGui.GetForegroundDrawList(), _ui.Mono, _ui.SansMedium, Kit.Scale, rect, tone, hovered, own, flight);
        }
    }

    /// <summary>
    /// A broken shield's readings: that it is offline, and how far its stored charge is towards the restart mark
    /// on the bar. With room the two stack in the reading column; in a compact row the progress goes beside the
    /// label while it fits. No time is promised: the shield also waits out its delay after the last hit.
    /// </summary>
    private void DrawShieldRestart(ImDrawListPtr draw, in FlightConsoleLayout layout, float progress, in ConsoleTrack track)
    {
        var x = layout.LeftX;
        var y = layout.Top;
        var right = x + layout.GroupWidth - Kit.Px(FlightConsole.Slant * 0.5f + FlightConsole.Pad);
        var state = Kit.Measure(_ui.SansMedium, Theme.FontSmall, "OFFLINE"u8);
        var restart = Kit.F($"RESTART {progress * 100f:0} %");
        var restartSize = Kit.Measure(_ui.Mono, Theme.FontCaption, restart);
        draw.PushClipRect(new float2(x, y), new float2(x + layout.GroupWidth, y + layout.Row), true);
        if (!layout.Compact)
        {
            Kit.Text(draw, _ui.SansMedium, Theme.FontSmall, new float2(right - state.X, y + Kit.Px(4)), Theme.Bad, "OFFLINE"u8);
            Kit.Text(draw, _ui.Mono, Theme.FontCaption, new float2(right - restartSize.X, y + Kit.Px(20)), Theme.TextDim, restart);
        }
        else
        {
            Kit.Text(draw, _ui.SansMedium, Theme.FontSmall, new float2(right - state.X, y + Kit.Px(3)), Theme.Bad, "OFFLINE"u8);
            var labelEnd = track.LabelEnd + Kit.Px(FlightConsole.ColumnGap);
            if (labelEnd + restartSize.X + Kit.Px(FlightConsole.ColumnGap) <= right - state.X)
                Kit.Text(draw, _ui.Mono, Theme.FontCaption, new float2(labelEnd, y + Kit.Px(5)), Theme.TextDim, restart);
        }

        draw.PopClipRect();
    }

    /// <summary>
    /// The boost heat row's readings: the heat, or why boost cannot run, and whether the dampeners hold the hull
    /// still. With room, the two stack in the reading column; in a compact row the dampeners go at the right of
    /// the bar's line and the bar gives up the width. Off is amber, the one dampener state that wants attention.
    /// </summary>
    private void DrawHeatReadings(ImDrawListPtr draw, in FlightConsoleLayout layout, float y, ReadOnlySpan<byte> reading, bool warning, uint color, bool dampeners, ref ConsoleTrack track)
    {
        var x = layout.RightX;
        var right = x + layout.GroupWidth - Kit.Px(FlightConsole.Slant * 0.5f + FlightConsole.Pad);
        var font = warning ? _ui.SansMedium : _ui.Mono;
        var readingColor = warning ? Theme.Bad : color;
        var state = dampeners ? "DAMPENERS ON"u8 : "DAMPENERS OFF"u8;
        var stateColor = dampeners ? Theme.TextMute : Theme.Accent;
        var stateSize = Kit.Measure(_ui.SansMedium, Theme.FontCaption, state);
        var readingSize = Kit.Measure(font, Theme.FontSmall, reading);
        draw.PushClipRect(new float2(x, y), new float2(x + layout.GroupWidth, y + layout.Row), true);
        if (!layout.Compact)
        {
            Kit.Text(draw, font, Theme.FontSmall, new float2(right - readingSize.X, y + Kit.Px(4)), readingColor, reading);
            Kit.Text(draw, _ui.SansMedium, Theme.FontCaption, new float2(right - stateSize.X, y + Kit.Px(20)), stateColor, state);
        }
        else
        {
            Kit.Text(draw, font, Theme.FontSmall, new float2(Math.Max(right - readingSize.X, track.LabelEnd + Kit.Px(FlightConsole.ColumnGap)), y + Kit.Px(3)), readingColor, reading);
            Kit.Text(draw, _ui.SansMedium, Theme.FontCaption, new float2(right - stateSize.X, track.Top + (track.Bottom - track.Top - stateSize.Y) * 0.5f), stateColor, state);
            track = track with { Right = Math.Max(track.Left, right - stateSize.X - Kit.Px(FlightConsole.ColumnGap)) };
        }

        draw.PopClipRect();
    }

    /// <summary>
    /// The battery's readings: stored charge out of capacity, and the power flowing now. The flow is generation
    /// less everything drawn, as the simulation split it, with an arrow for its direction: a shield recharging
    /// off a full battery shows here though the battery does not move. Empty says so in words; steady says
    /// full or steady. Under the segments a strip draws the flow against the most this hull can draw or gain, and
    /// marks how much of it the shield accounts for.
    /// </summary>
    private void DrawEnergyReadings(ImDrawListPtr draw, in FlightConsoleLayout layout, in HullView own, in FlightParams flight, float energy, in ConsoleTrack track)
    {
        var x = layout.RightX;
        var y = layout.Top;
        var stored = flight.EnergyStorage > 0f
            ? Energy(own.Energy, flight.EnergyStorage)
            : "NONE"u8;
        var net = FlightConsole.NetPower(own);
        var steady = MathF.Abs(net) < FlightConsole.SteadyRate;
        var flowColor = steady ? Theme.TextMute : net < 0f ? FlightConsole.DrawColor : FlightConsole.ChargeColor;
        var empty = flight.EnergyStorage > 0f && energy <= 0f && net < FlightConsole.SteadyRate;
        ReadOnlySpan<byte> flow = flight.EnergyStorage <= 0f ? default
            : empty ? "EMPTY"u8
            : steady ? energy >= 1f ? "FULL"u8 : "STEADY"u8
            : MathF.Abs(net) >= 1000f ? Kit.F($"{net / 1000f:+0.0;-0.0} GW")
            : Kit.F($"{net:+0.0;-0.0} MW");
        var arrow = !flow.IsEmpty && !steady && !empty;
        var flowTone = empty ? Theme.Bad : flowColor;
        var readingColor = energy < 0.15f ? Theme.Bad : Theme.Text;
        var tuning = PowerReadout.Current;

        var right = x + layout.GroupWidth - Kit.Px(FlightConsole.Slant * 0.5f + FlightConsole.Pad);
        draw.PushClipRect(new float2(x, y), new float2(x + layout.GroupWidth, y + layout.Row), true);
        if (!layout.Compact)
        {
            // Two lines in the reading column: what is stored, then where it is going.
            var storedSize = Kit.Measure(_ui.Mono, Theme.FontSmall, stored);
            Kit.Text(draw, _ui.Mono, Theme.FontSmall, new float2(right - storedSize.X, y + Kit.Px(4)), readingColor, stored);
            if (!flow.IsEmpty)
            {
                var flowSize = Kit.Measure(_ui.Mono, Theme.FontCaption, flow);
                Kit.Text(draw, _ui.Mono, Theme.FontCaption, new float2(right - flowSize.X, y + Kit.Px(20)), flowTone, flow);
                if (arrow)
                    DrawTrendArrow(draw, tuning, right - flowSize.X, y + Kit.Px(20), flowSize.Y, net > 0f, flowTone);
            }
        }
        else
        {
            // One caption line: the stored reading at the right, and the flow beside the label while it fits.
            var storedSize = Kit.Measure(_ui.Mono, Theme.FontSmall, stored);
            Kit.Text(draw, _ui.Mono, Theme.FontSmall, new float2(right - storedSize.X, y + Kit.Px(3)), readingColor, stored);
            if (!flow.IsEmpty)
            {
                var labelEnd = track.LabelEnd + Kit.Px(FlightConsole.ColumnGap);
                var flowSize = Kit.Measure(_ui.Mono, Theme.FontCaption, flow);
                var arrowRoom = arrow ? Kit.Px(tuning.ArrowWidth + tuning.ArrowGap) : 0f;
                if (labelEnd + arrowRoom + flowSize.X + Kit.Px(FlightConsole.ColumnGap) <= right - storedSize.X)
                {
                    Kit.Text(draw, _ui.Mono, Theme.FontCaption, new float2(labelEnd + arrowRoom, y + Kit.Px(5)), flowTone, flow);
                    if (arrow)
                        DrawTrendArrow(draw, tuning, labelEnd + arrowRoom, y + Kit.Px(5), flowSize.Y, net > 0f, flowTone);
                }
            }
        }

        draw.PopClipRect();

        // The flow strip under the segments: a notch at zero, draw growing left the way the charge is going and
        // charge growing right, each against the most this hull can draw or gain. The shield's share is a muted
        // blue line from where the flow is to where it would be without the shield's draw, ending in a tick.
        var flowFraction = FlightConsole.Flow(net, flight);
        var stripTop = track.Bottom + Kit.Px(layout.Compact ? 2 : 4);
        var stripBottom = stripTop + Kit.Px(2);
        var middle = MathF.Round((track.Left + track.Right) * 0.5f);
        var half = (track.Right - track.Left) * 0.5f;
        draw.AddRectFilled(new float2(track.Left, stripTop), new float2(track.Right, stripBottom), Kit.Color(Theme.Line, 0.8f));
        if (flowFraction < 0f)
            draw.AddRectFilled(new float2(middle + half * flowFraction, stripTop), new float2(middle, stripBottom), Kit.Color(FlightConsole.DrawColor));
        else if (flowFraction > 0f)
            draw.AddRectFilled(new float2(middle, stripTop), new float2(middle + half * flowFraction, stripBottom), Kit.Color(FlightConsole.ChargeColor));
        draw.AddRectFilled(new float2(middle - Kit.Px(0.5f), stripTop - Kit.Px(2)), new float2(middle + Kit.Px(1), stripBottom + Kit.Px(2)), Kit.Color(Theme.TextDim, 0.9f));

        var shield = FlightConsole.ShieldDraw(own);
        if (flight.EnergyStorage > 0f && shield >= FlightConsole.SteadyRate)
        {
            var without = FlightConsole.Flow(net + shield, flight);
            var from = middle + half * flowFraction;
            var to = middle + half * without;
            var line = Kit.Px(tuning.ShieldMarkThickness);
            var centre = (stripTop + stripBottom) * 0.5f;
            var mark = Kit.Color(Theme.Info, tuning.ShieldMarkAlpha);
            draw.AddRectFilled(new float2(Math.Min(from, to), centre - line * 0.5f), new float2(Math.Max(from, to), centre + line * 0.5f), mark);
            var tick = Kit.Px(tuning.ShieldMarkTick);
            draw.AddRectFilled(new float2(to - Kit.Px(0.5f), stripTop - tick), new float2(to + Kit.Px(1), stripBottom + tick), mark);
        }
    }

    /// <summary>A small filled triangle before the net figure: up for power coming in, down for power going out.</summary>
    private static void DrawTrendArrow(ImDrawListPtr draw, PowerReadout tuning, float textLeft, float textTop, float textHeight, bool up, uint color)
    {
        var width = Kit.Px(tuning.ArrowWidth);
        var height = Kit.Px(tuning.ArrowHeight);
        var centreX = textLeft - Kit.Px(tuning.ArrowGap) - width * 0.5f;
        var centreY = textTop + textHeight * 0.5f;
        var col = Kit.Color(color);
        if (up)
            draw.AddTriangleFilled(new float2(centreX - width * 0.5f, centreY + height * 0.5f), new float2(centreX + width * 0.5f, centreY + height * 0.5f), new float2(centreX, centreY - height * 0.5f), col);
        else
            draw.AddTriangleFilled(new float2(centreX - width * 0.5f, centreY - height * 0.5f), new float2(centreX + width * 0.5f, centreY - height * 0.5f), new float2(centreX, centreY + height * 0.5f), col);
    }

    /// <summary>Shield or hull points out of the most the hull holds, in thousands once they no longer fit.</summary>
    internal static ReadOnlySpan<byte> Points(float value, float capacity)
    {
        capacity = FlightConsole.Shown(capacity, float.MaxValue);
        var shown = FlightConsole.Shown(value, capacity);
        return FlightConsole.Thousands(capacity)
            ? Kit.F($"{shown / 1000f:0}k / {capacity / 1000f:0}k")
            : Kit.F($"{shown:0} / {capacity:0}");
    }

    /// <summary>Stored energy out of capacity, in megajoules, or gigajoules for the largest hulls.</summary>
    internal static ReadOnlySpan<byte> Energy(float value, float capacity)
    {
        capacity = FlightConsole.Shown(capacity, float.MaxValue);
        var shown = FlightConsole.Shown(value, capacity);
        return FlightConsole.Thousands(capacity)
            ? Kit.F($"{shown / 1000f:0} / {capacity / 1000f:0} GJ")
            : Kit.F($"{shown:0} / {capacity:0} MJ");
    }

    /// <summary>
    /// One slanted row: its frame, icon and label, and where its segments go. A <paramref name="hot"/> row, under
    /// the pointer, is raised a shade and has its border lit.
    /// </summary>
    private void ConsoleRow(ImDrawListPtr draw, in FlightConsoleLayout layout, float x, float y, ConsoleIcon icon, ImString label, uint rgb, out ConsoleTrack track, bool lit = false, bool hot = false)
    {
        var w = layout.GroupWidth;
        var h = layout.Row;
        var s = Kit.Px(FlightConsole.Slant);
        var p0 = new float2(x + s, y);
        var p1 = new float2(x + w, y);
        var p2 = new float2(x + w - s, y + h);
        var p3 = new float2(x, y + h);
        // The heads-up panel look: muted fill, a quiet border, and the reading's colour only on the leading edge.
        draw.AddQuadFilled(p0, p1, p2, p3, Kit.Color(hot ? Theme.PanelRaised : Theme.Panel, Hud.FillAlpha));
        draw.AddQuad(p0, p1, p2, p3, Kit.Color(Hud.Border(rgb, lit || hot)), 1f);
        draw.AddLine(p3, p0, Kit.Color(rgb, 0.85f), Kit.Px(2));

        var pad = Kit.Px(FlightConsole.Pad);
        var inner = x + s * 0.5f + pad;
        var labelEnd = inner;
        draw.PushClipRect(new float2(x, y), new float2(x + w, y + h), true);
        if (!layout.Compact)
        {
            ConsoleBars.Glyph(draw, icon, new float2(inner, y + h * 0.5f - Kit.Px(7)), rgb);
            var labelX = inner + Kit.Px(14 + 8);
            labelEnd = labelX + Kit.Text(draw, _ui.SansMedium, Theme.FontSmall, new float2(labelX, y + Kit.Px(11)), lit ? rgb : Theme.TextDim, label).X;
            var left = x + s * 0.5f + pad + Kit.Px(FlightConsole.LabelColumn + FlightConsole.ColumnGap);
            var right = x + w - s * 0.5f - pad - Kit.Px(FlightConsole.ReadingColumn + FlightConsole.ColumnGap);
            track = new ConsoleTrack(left, right, y + Kit.Px(11), y + Kit.Px(23), labelEnd);
        }
        else
        {
            labelEnd = inner + Kit.Text(draw, _ui.SansMedium, Theme.FontSmall, new float2(inner, y + Kit.Px(3)), lit ? rgb : Theme.TextDim, label).X;
            track = new ConsoleTrack(inner, x + w - s * 0.5f - pad, y + Kit.Px(21), y + Kit.Px(30), labelEnd);
        }

        draw.PopClipRect();
    }

    /// <summary>A row's reading, right-aligned in its column and clipped to the row.</summary>
    private void ConsoleReading(ImDrawListPtr draw, in FlightConsoleLayout layout, float x, float y, ImString reading, ImFontPtr font, uint rgb, in ConsoleTrack track)
    {
        var right = x + layout.GroupWidth - Kit.Px(FlightConsole.Slant * 0.5f + FlightConsole.Pad);
        var size = Kit.Measure(font, Theme.FontSmall, reading);
        var left = Math.Max(right - size.X, layout.Compact ? track.LabelEnd + Kit.Px(FlightConsole.ColumnGap) : right - Kit.Px(FlightConsole.ReadingColumn));
        draw.PushClipRect(new float2(left, y), new float2(right + 1f, y + layout.Row), true);
        Kit.Text(draw, font, Theme.FontSmall, new float2(right - size.X, y + Kit.Px(layout.Compact ? 3 : 11)), rgb, reading);
        draw.PopClipRect();
    }
}

/// <summary>One of the pilot console's four bars, as the pointer finds them.</summary>
internal enum ConsoleBar
{
    None,
    Shield,
    Hull,
    Energy,
    Heat,
}

/// <summary>Where the pilot console sits on a screen of a given size and interface scale.</summary>
/// <param name="Top">The console's top edge; the party list and chat stay above it.</param>
/// <param name="LeftX">The left group's left edge, which is also the console's.</param>
/// <param name="RightX">The right group's left edge.</param>
/// <param name="CenterX">The middle of the speed readout.</param>
/// <param name="GroupWidth">The width of each group of two rows, slant included.</param>
/// <param name="Row">The height of one row.</param>
/// <param name="RowGap">The space between a group's two rows.</param>
/// <param name="Compact">
/// True when the groups are too narrow for label, bar and reading side by side: each row then puts label and
/// reading on a line above its bar and drops its icon.
/// </param>
internal readonly record struct FlightConsoleLayout(float Top, float LeftX, float RightX, float CenterX, float GroupWidth, float Row, float RowGap, bool Compact)
{
    public float Height => Row * 2 + RowGap;

    /// <summary>The console's right edge.</summary>
    public float Right => RightX + GroupWidth;
}

/// <summary>
/// The pilot console's geometry and the arithmetic behind its readings, apart from drawing so it can be tested
/// without fonts. Sizes are in unscaled pixels.
/// </summary>
internal static class FlightConsole
{
    public const float Row = 36f;
    public const float RowGap = 6f;
    public const float Bottom = 20f;
    public const float Side = 16f;

    /// <summary>The speed readout between the two groups, and the space either side of it.</summary>
    public const float Center = 120f;
    public const float CenterGap = 10f;

    /// <summary>How far the speed hexagon's side points reach out past its top and bottom edges.</summary>
    public const float HexPoint = 12f;

    /// <summary>The widest a group grows; wider screens leave the space to the world.</summary>
    public const float GroupMax = 430f;

    /// <summary>How far a row's top edge leans right of its bottom edge.</summary>
    public const float Slant = 10f;
    public const float Pad = 10f;
    public const float LabelColumn = 90f;
    public const float ReadingColumn = 96f;
    public const float ColumnGap = 12f;

    /// <summary>The shortest bar beside its label and reading; narrower groups stack them instead.</summary>
    public const float TrackMin = 140f;

    /// <summary>The narrowest group that still holds label, bar and reading on one line.</summary>
    public const float FullMin = Slant + 2 * Pad + LabelColumn + ReadingColumn + 2 * ColumnGap + TrackMin;

    public const int Segments = 10;
    public const float SpeedFont = 48f;

    /// <summary>Where the speed and its unit sit from the hexagon's top, centred in its height together.</summary>
    public const float SpeedTop = 4f;
    public const float UnitTop = 48f;

    /// <summary>Net megawatts below which the battery reads as steady; data in <c>content/visuals/power.json</c>.</summary>
    public static float SteadyRate => PowerReadout.Current.SteadyMegawatts;

    /// <summary>
    /// What the hull makes less everything it draws, in megawatts: the figure the readout shows. It is the
    /// simulation's own power split, not the battery's change, so it stays visible while a full battery turns
    /// the surplus away.
    /// </summary>
    public static float NetPower(in HullView own) => float.IsFinite(own.Power.Net) ? own.Power.Net : 0f;

    /// <summary>The shield's draw, finite and never negative.</summary>
    public static float ShieldDraw(in HullView own) => float.IsFinite(own.Power.Shield) ? Math.Max(0f, own.Power.Shield) : 0f;

    /// <summary>How fast a bar's trail of lost charge catches up, as an exponential rate per second.</summary>
    public const float TrailRate = 3f;

    /// <summary>A hull bar turns red below this share of the blueprint's strength.</summary>
    public const float HullLow = 0.3f;

    /// <summary>The hull bar's colour: green, red once the hull is low. Shared with the target panel.</summary>
    public static uint HullColor(float fraction) => fraction < HullLow ? Theme.Bad : Theme.Good;

    /// <summary>The shield bar's accent: red while broken, blue with a shield, a dull line with none.</summary>
    public static uint ShieldColor(bool hasShield, bool offline) => offline && hasShield ? Theme.Bad : hasShield ? Theme.Info : Theme.LineStrong;

    /// <summary>How far a bar's colour is taken towards the panel, so the hues read without shouting.</summary>
    public const float Mute = 0.18f;

    /// <summary>Orange for charge leaving the battery, green for charge coming in.</summary>
    public const uint DrawColor = 0xFF7A2E;
    public const uint ChargeColor = Theme.Good;

    /// <summary>
    /// A row's whole rectangle, slant included: the shield and hull on the left, energy and boost heat on the right,
    /// each group's first row on top.
    /// </summary>
    public static HudRect BarRect(in FlightConsoleLayout layout, ConsoleBar bar)
    {
        var left = bar is ConsoleBar.Shield or ConsoleBar.Hull ? layout.LeftX : layout.RightX;
        var top = bar is ConsoleBar.Shield or ConsoleBar.Energy ? layout.Top : layout.Top + layout.Row + layout.RowGap;
        return new HudRect(left, top, left + layout.GroupWidth, top + layout.Row);
    }

    /// <summary>
    /// The row under the pointer, or none. Only a pointer <paramref name="shown"/> for the heads-up display (the
    /// pilot holding the release key, with nothing open) can point at a row, and never while it is over a window
    /// of the interface, such as a pinned social window covering the console. A captured pointer's last position
    /// therefore never counts.
    /// </summary>
    public static ConsoleBar Hovered(in FlightConsoleLayout layout, float2 pointer, bool shown, bool overInterface)
    {
        if (!shown || overInterface)
            return ConsoleBar.None;
        for (var bar = ConsoleBar.Shield; bar <= ConsoleBar.Heat; bar++)
        {
            if (BarRect(layout, bar).Contains(pointer))
                return bar;
        }

        return ConsoleBar.None;
    }

    /// <summary>
    /// Where a row's details go: above the console so they never cover a bar, from the row's outer edge, and kept
    /// inside the side margins of a <paramref name="display"/> of any size. Wider than the screen allows, the
    /// details are cut to it.
    /// </summary>
    public static HudRect TipRect(in FlightConsoleLayout layout, ConsoleBar bar, float2 size, float2 display, float scale)
    {
        var side = MathF.Round(Side * scale);
        var width = Math.Min(size.X, Math.Max(0f, display.X - 2 * side));
        var row = BarRect(layout, bar);
        var left = bar is ConsoleBar.Shield or ConsoleBar.Hull ? row.Left : row.Right - width;
        left = Math.Clamp(left, side, Math.Max(side, display.X - side - width));
        var top = Math.Max(side, layout.Top - MathF.Round(Theme.Space2 * scale) - size.Y);
        return new HudRect(left, top, left + width, top + size.Y);
    }

    /// <summary>
    /// Where a broken shield comes back online, as a share of its capacity: the hull's own restart rule. Zero when
    /// there is no shield, so no mark is drawn.
    /// </summary>
    public static float RestartMark(in FlightParams flight) =>
        flight.ShieldCapacity > 0f && float.IsFinite(flight.ShieldCapacity) && flight.ShieldRestartFraction is > 0f and <= 1f
            ? flight.ShieldRestartFraction
            : 0f;

    /// <summary>How far a broken shield's stored charge is towards its restart mark, 0 to 1.</summary>
    public static float RestartProgress(float shield, in FlightParams flight) =>
        Fraction(shield, flight.ShieldCapacity * RestartMark(flight));

    public static FlightConsoleLayout Layout(float2 display, float scale)
    {
        float Px(float design) => MathF.Round(design * scale);

        var center = Px(Center);
        var gap = Px(CenterGap);
        var room = MathF.Floor((display.X - 2 * Px(Side) - center - 2 * gap) * 0.5f);
        var group = Math.Max(0f, Math.Min(Px(GroupMax), room));
        var row = Px(Row);
        var rowGap = Px(RowGap);
        var top = display.Y - Px(Bottom) - (row * 2 + rowGap);
        var cx = MathF.Round(display.X * 0.5f);
        var half = center * 0.5f;
        return new FlightConsoleLayout(top, cx - half - gap - group, cx + half + gap, cx, group, row, rowGap, group < Px(FullMin));
    }

    /// <summary>
    /// True when boost cannot start for want of energy: not already running, and the battery is empty or below
    /// its boost floor.
    /// </summary>
    public static bool BoostStarved(in HullView own, in FlightParams flight) =>
        !own.Boosting && (own.Energy <= 0f || own.Energy < flight.BoostEnergyFloor);

    /// <summary>
    /// The hull's live flight figures with <see cref="FlightParams.HullPoints"/> set to its design's full hull
    /// points. The live figure is worked out from the blocks the hull still has, so it shrinks with them and a
    /// damaged hull would otherwise read as nearly whole.
    /// </summary>
    public static FlightParams WithBlueprintHull(FlightParams flight, HullDesign[] designs, byte design)
    {
        if (design < designs.Length)
            flight.HullPoints = designs[design].Flight.HullPoints;
        return flight;
    }

    /// <summary>A reading as a fraction of its capacity: 0 to 1, and 0 for no capacity or a value that is not a number.</summary>
    public static float Fraction(float value, float capacity) =>
        capacity > 0f && float.IsFinite(capacity) && float.IsFinite(value) ? Math.Clamp(value / capacity, 0f, 1f) : 0f;

    /// <summary>A value as the console prints it: within 0 and the capacity, and 0 when it is not a number.</summary>
    public static float Shown(float value, float capacity) =>
        float.IsFinite(value) && float.IsFinite(capacity) ? Math.Clamp(value, 0f, Math.Max(capacity, 0f)) : 0f;

    /// <summary>True when a capacity needs more than five digits, and so is printed in thousands.</summary>
    public static bool Thousands(float capacity) => capacity >= 99_999.5f;

    /// <summary>
    /// The battery's net rate as a signed share of the most this hull can move: negative against its heaviest
    /// net draw (boosted thrust, shield recharge and the jump drive all at once, less generation), positive
    /// against its largest surplus (generation less what is always drawn). Zero while steady. A broken shield
    /// charging back draws its restart power, the larger of its two draws, so that is what the scale allows for.
    /// </summary>
    public static float Flow(float rate, in FlightParams flight)
    {
        if (!float.IsFinite(rate) || MathF.Abs(rate) < SteadyRate)
            return 0f;

        if (rate < 0f)
        {
            var draw = flight.IdlePower + flight.ThrustPower * flight.BoostPowerMultiplier + Math.Max(flight.ShieldPower, flight.ShieldRestartPower) + flight.FtlPower - flight.PowerGenerated;
            return draw > SteadyRate ? -Math.Min(-rate / draw, 1f) : -1f;
        }

        var surplus = flight.PowerGenerated - flight.IdlePower;
        return surplus > SteadyRate ? Math.Min(rate / surplus, 1f) : 1f;
    }

    /// <summary>
    /// Moves a bar's trail toward its value: at once when the value rises, easing down after a loss so the lost
    /// part stays visible for a moment. A steady drain keeps a short trail at the edge.
    /// </summary>
    public static float Trail(float trail, float value, float dt)
    {
        if (!float.IsFinite(trail) || value >= trail)
            return value;
        var next = trail + (value - trail) * (1f - MathF.Exp(-TrailRate * Math.Max(dt, 0f)));
        return next - value < 0.002f ? value : next;
    }

    /// <summary>Boost heat's colour: cool cyan, then amber, orange and red as the drive nears overheating.</summary>
    public static uint HeatColor(float heat)
    {
        // Cyan steps straight to amber at half heat: a blend of the two would pass through a muddy green.
        if (heat < 0.5f)
            return Theme.Info;
        if (heat < 0.85f)
            return Kit.Mix(Theme.Accent, DrawColor, (heat - 0.6f) / 0.25f);
        return Kit.Mix(DrawColor, Theme.Bad, (heat - 0.85f) / 0.12f);
    }
}
