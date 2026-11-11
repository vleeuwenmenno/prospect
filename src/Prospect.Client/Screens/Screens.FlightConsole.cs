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
    /// nothing is told by colour alone.
    /// </summary>
    private void DrawFlightConsole(ImDrawListPtr draw, SessionSnapshot snapshot, in HullView own, float speed)
    {
        var flight = snapshot.Flight;
        var layout = FlightConsole.Layout(_ui.DisplaySize, Kit.Scale);
        var row = layout.Row;
        var second = layout.Top + row + layout.RowGap;

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
        var hasShield = flight.ShieldCapacity > 0f;
        ConsoleRow(draw, layout, layout.LeftX, layout.Top, ConsoleIcon.Shield, "SHIELD"u8, hasShield ? Theme.Info : Theme.LineStrong, out var track);
        if (hasShield)
            ConsoleReading(draw, layout, layout.LeftX, layout.Top, Points(own.Shield, flight.ShieldCapacity), _ui.Mono, Theme.Text, track);
        else
            ConsoleReading(draw, layout, layout.LeftX, layout.Top, "NONE"u8, _ui.SansMedium, Theme.TextMute, track);
        ConsoleSegments(draw, track, shield, _trailShield, Theme.Info, false);

        var hullColor = hull < 0.3f ? Theme.Bad : Theme.Good;
        ConsoleRow(draw, layout, layout.LeftX, second, ConsoleIcon.Hull, "HULL"u8, hullColor, out track);
        ConsoleReading(draw, layout, layout.LeftX, second, Points(own.Hull, flight.HullPoints), _ui.Mono, hull < 0.3f ? Theme.Bad : Theme.Text, track);
        ConsoleSegments(draw, track, hull, _trailHull, hullColor, false);

        // Right: the battery, and the boost drive's heat.
        var energyColor = energy < 0.15f ? Theme.Bad : Theme.Accent;
        ConsoleRow(draw, layout, layout.RightX, layout.Top, ConsoleIcon.Energy, "ENERGY"u8, energyColor, out track);
        DrawEnergyReadings(draw, layout, own, flight, energy, track);
        ConsoleSegments(draw, track, energy, _trailEnergy, energyColor, false);

        // The battery's boost floor: below the mark boost will not start, though boost already running carries on
        // until the battery is empty.
        if (flight.EnergyStorage > 0f)
            ConsoleMarker(draw, track, FlightConsole.Fraction(flight.BoostEnergyFloor, flight.EnergyStorage), 0.85f);

        // Why boost is unavailable, when it is: the drive overheated and is cooling, or the battery is below its floor.
        var noEnergy = !own.Boosting && (own.Energy <= 0f || own.Energy < flight.BoostEnergyFloor);
        var heatColor = own.BoostLocked || noEnergy ? Theme.Bad : FlightConsole.HeatColor(heat);
        ConsoleRow(draw, layout, layout.RightX, second, ConsoleIcon.Heat, own.Boosting ? "BOOSTING"u8 : "BOOST HEAT"u8, heatColor, out track, own.Boosting);
        DrawHeatReadings(draw, layout, second, own.BoostLocked ? "OVERHEAT"u8 : noEnergy ? "NO ENERGY"u8 : Kit.F($"{heat * 100f:0} %"), own.BoostLocked || noEnergy, heatColor, snapshot.Dampeners, ref track);
        ConsoleSegments(draw, track, heat, heat, own.BoostLocked ? Theme.Bad : heatColor, !own.BoostLocked);

        // While locked, the mark is where the drive has cooled enough to boost again.
        if (own.BoostLocked)
            ConsoleMarker(draw, track, FlightModel.BoostUnlockHeat, 0.7f);

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
    /// The battery's readings: stored charge out of capacity, and what the charge is doing now. The flow is the
    /// measured net rate (generation less everything drawn), never a forecast; full and empty say so in words.
    /// </summary>
    private void DrawEnergyReadings(ImDrawListPtr draw, in FlightConsoleLayout layout, in HullView own, in FlightParams flight, float energy, in ConsoleTrack track)
    {
        var x = layout.RightX;
        var y = layout.Top;
        var stored = flight.EnergyStorage > 0f
            ? Energy(own.Energy, flight.EnergyStorage)
            : "NONE"u8;
        var rate = float.IsFinite(own.EnergyRate) ? own.EnergyRate : 0f;
        var steady = MathF.Abs(rate) < FlightConsole.SteadyRate;
        var flowColor = steady ? Theme.TextMute : rate < 0f ? FlightConsole.DrawColor : FlightConsole.ChargeColor;
        ReadOnlySpan<byte> flow = flight.EnergyStorage <= 0f ? default
            : energy <= 0f && steady ? "EMPTY"u8
            : energy >= 1f && steady ? "FULL"u8
            : steady ? "STEADY"u8
            : MathF.Abs(rate) >= 1000f ? Kit.F($"{(rate < 0f ? "DRAW" : "CHARGE")} {MathF.Abs(rate) / 1000f:0.0} GW")
            : Kit.F($"{(rate < 0f ? "DRAW" : "CHARGE")} {MathF.Abs(rate):0.0} MW");
        var flowTone = energy <= 0f && steady ? Theme.Bad : flowColor;
        var readingColor = energy < 0.15f ? Theme.Bad : Theme.Text;

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
                if (labelEnd + flowSize.X + Kit.Px(FlightConsole.ColumnGap) <= right - storedSize.X)
                    Kit.Text(draw, _ui.Mono, Theme.FontCaption, new float2(labelEnd, y + Kit.Px(5)), flowTone, flow);
            }
        }

        draw.PopClipRect();

        // The flow strip under the segments: a notch at zero, draw growing left the way the charge is going and
        // charge growing right, each against the most this hull can draw or gain.
        var flowFraction = FlightConsole.Flow(rate, flight);
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
    }

    /// <summary>Shield or hull points out of the most the hull holds, in thousands once they no longer fit.</summary>
    private static ReadOnlySpan<byte> Points(float value, float capacity)
    {
        capacity = FlightConsole.Shown(capacity, float.MaxValue);
        var shown = FlightConsole.Shown(value, capacity);
        return FlightConsole.Thousands(capacity)
            ? Kit.F($"{shown / 1000f:0}k / {capacity / 1000f:0}k")
            : Kit.F($"{shown:0} / {capacity:0}");
    }

    /// <summary>Stored energy out of capacity, in megajoules, or gigajoules for the largest hulls.</summary>
    private static ReadOnlySpan<byte> Energy(float value, float capacity)
    {
        capacity = FlightConsole.Shown(capacity, float.MaxValue);
        var shown = FlightConsole.Shown(value, capacity);
        return FlightConsole.Thousands(capacity)
            ? Kit.F($"{shown / 1000f:0} / {capacity / 1000f:0} GJ")
            : Kit.F($"{shown:0} / {capacity:0} MJ");
    }

    private enum ConsoleIcon
    {
        Shield,
        Hull,
        Energy,
        Heat,
    }

    /// <summary>Where a row's segments go, and where its label ends.</summary>
    private readonly record struct ConsoleTrack(float Left, float Right, float Top, float Bottom, float LabelEnd);

    /// <summary>One slanted row: its frame, icon and label, and where its segments go.</summary>
    private void ConsoleRow(ImDrawListPtr draw, in FlightConsoleLayout layout, float x, float y, ConsoleIcon icon, ImString label, uint rgb, out ConsoleTrack track, bool lit = false)
    {
        var w = layout.GroupWidth;
        var h = layout.Row;
        var s = Kit.Px(FlightConsole.Slant);
        var p0 = new float2(x + s, y);
        var p1 = new float2(x + w, y);
        var p2 = new float2(x + w - s, y + h);
        var p3 = new float2(x, y + h);
        // The heads-up panel look: muted fill, a quiet border, and the reading's colour only on the leading edge.
        draw.AddQuadFilled(p0, p1, p2, p3, Kit.Color(Theme.Panel, Hud.FillAlpha));
        draw.AddQuad(p0, p1, p2, p3, Kit.Color(Hud.Border(rgb, lit)), 1f);
        draw.AddLine(p3, p0, Kit.Color(rgb, 0.85f), Kit.Px(2));

        var pad = Kit.Px(FlightConsole.Pad);
        var inner = x + s * 0.5f + pad;
        var labelEnd = inner;
        draw.PushClipRect(new float2(x, y), new float2(x + w, y + h), true);
        if (!layout.Compact)
        {
            ConsoleGlyph(draw, icon, new float2(inner, y + h * 0.5f - Kit.Px(7)), rgb);
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

    /// <summary>
    /// The bar: <see cref="FlightConsole.Segments"/> slanted segments. Lit up to <paramref name="fraction"/>, the
    /// leading segment partly; from there to <paramref name="trail"/> the charge just lost, fading as it catches up.
    /// Heat colours each segment by where it sits on the scale, so a hotter drive reaches into amber and red.
    /// </summary>
    private static void ConsoleSegments(ImDrawListPtr draw, in ConsoleTrack track, float fraction, float trail, uint rgb, bool heat)
    {
        const int count = FlightConsole.Segments;
        var gap = Kit.Px(3);
        var height = track.Bottom - track.Top;
        var skew = height * FlightConsole.Slant / FlightConsole.Row;
        var width = (track.Right - skew - track.Left - gap * (count - 1)) / count;
        if (width <= 0f)
            return;

        var off = Kit.Color(Theme.Line, 0.85f);
        var lost = Kit.Color(Kit.Mix(rgb, Theme.Text, 0.45f), 0.5f);
        for (var i = 0; i < count; i++)
        {
            var x = track.Left + i * (width + gap);
            var lit = Math.Clamp(fraction * count - i, 0f, 1f);
            var trailed = Math.Clamp(trail * count - i, 0f, 1f);
            // The hues stay, a shade towards the panel so the bars read without shouting.
            var color = Kit.Color(Kit.Mix(heat ? FlightConsole.HeatColor((i + 0.5f) / count) : rgb, Theme.Panel, FlightConsole.Mute));
            if (lit > 0f)
                SegmentPart(draw, x, x + width * lit, track.Top, track.Bottom, skew, color);
            if (trailed > lit)
                SegmentPart(draw, x + width * lit, x + width * trailed, track.Top, track.Bottom, skew, lost);
            if (lit < 1f && trailed < 1f)
                SegmentPart(draw, x + width * Math.Max(lit, trailed), x + width, track.Top, track.Bottom, skew, off);
        }
    }

    private static void SegmentPart(ImDrawListPtr draw, float from, float to, float top, float bottom, float skew, ImColor8 color) =>
        draw.AddQuadFilled(new float2(from + skew, top), new float2(to + skew, top), new float2(to, bottom), new float2(from, bottom), color);

    /// <summary>A slanted tick across the bar at a fraction of it: the boost floor, or where boost unlocks.</summary>
    private static void ConsoleMarker(ImDrawListPtr draw, in ConsoleTrack track, float fraction, float alpha)
    {
        var height = track.Bottom - track.Top;
        var skew = height * FlightConsole.Slant / FlightConsole.Row;
        var reach = Kit.Px(3);
        var x = track.Left + (track.Right - skew - track.Left) * Math.Clamp(fraction, 0f, 1f);
        var rise = reach * FlightConsole.Slant / FlightConsole.Row;
        draw.AddLine(
            new float2(x - rise, track.Bottom + reach),
            new float2(x + skew + rise, track.Top - reach),
            Kit.Color(Theme.Text, alpha),
            Kit.Px(1.5f)
        );
    }

    /// <summary>A row's icon in a 14 by 14 design-pixel box at <paramref name="at"/>.</summary>
    private static void ConsoleGlyph(ImDrawListPtr draw, ConsoleIcon icon, float2 at, uint rgb)
    {
        var u = Kit.Scale;
        var color = Kit.Color(rgb, 0.95f);
        var thickness = Kit.Px(1.5f);
        Span<float2> points = stackalloc float2[7];
        switch (icon)
        {
            case ConsoleIcon.Shield:
                points[0] = at + new float2(7, 0.5f) * u;
                points[1] = at + new float2(13, 2.5f) * u;
                points[2] = at + new float2(12.5f, 8) * u;
                points[3] = at + new float2(7, 13.5f) * u;
                points[4] = at + new float2(1.5f, 8) * u;
                points[5] = at + new float2(1, 2.5f) * u;
                draw.AddPolyline(points[..6], color, ImDrawFlags.Closed, thickness);
                break;
            case ConsoleIcon.Hull:
                points[0] = at + new float2(7, 0.5f) * u;
                points[1] = at + new float2(13, 4) * u;
                points[2] = at + new float2(13, 10) * u;
                points[3] = at + new float2(7, 13.5f) * u;
                points[4] = at + new float2(1, 10) * u;
                points[5] = at + new float2(1, 4) * u;
                draw.AddPolyline(points[..6], color, ImDrawFlags.Closed, thickness);
                draw.AddCircleFilled(at + new float2(7, 7) * u, 1.8f * u, color, 8);
                break;
            case ConsoleIcon.Energy:
                points[0] = at + new float2(8.5f, 0.5f) * u;
                points[1] = at + new float2(2.5f, 8) * u;
                points[2] = at + new float2(6.5f, 8) * u;
                points[3] = at + new float2(5, 13.5f) * u;
                points[4] = at + new float2(11.5f, 5.5f) * u;
                points[5] = at + new float2(7.5f, 5.5f) * u;
                points[6] = at + new float2(9.5f, 0.5f) * u;
                draw.AddPolyline(points, color, ImDrawFlags.Closed, thickness);
                break;
            default:
                for (var i = 0; i < 3; i++)
                {
                    var x = 2.5f + i * 4f;
                    points[0] = at + new float2(x, 13.5f) * u;
                    points[1] = at + new float2(x + 1.5f, 9.5f) * u;
                    points[2] = at + new float2(x, 5) * u;
                    points[3] = at + new float2(x + 1.5f, 0.5f) * u;
                    draw.AddPolyline(points[..4], color, ImDrawFlags.None, thickness);
                }

                break;
        }
    }
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

    /// <summary>Net megawatts below which the battery reads as steady.</summary>
    public const float SteadyRate = 0.05f;

    /// <summary>How fast a bar's trail of lost charge catches up, as an exponential rate per second.</summary>
    public const float TrailRate = 3f;

    /// <summary>How far a bar's colour is taken towards the panel, so the hues read without shouting.</summary>
    public const float Mute = 0.18f;

    /// <summary>Orange for charge leaving the battery, green for charge coming in.</summary>
    public const uint DrawColor = 0xFF7A2E;
    public const uint ChargeColor = Theme.Good;

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
    /// against its largest surplus (generation less what is always drawn). Zero while steady.
    /// </summary>
    public static float Flow(float rate, in FlightParams flight)
    {
        if (!float.IsFinite(rate) || MathF.Abs(rate) < SteadyRate)
            return 0f;

        if (rate < 0f)
        {
            var draw = flight.IdlePower + flight.ThrustPower * flight.BoostPowerMultiplier + flight.ShieldPower + flight.FtlPower - flight.PowerGenerated;
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
