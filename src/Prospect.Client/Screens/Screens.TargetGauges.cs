using Brutal.ImGuiApi;
using Brutal.Numerics;
using Prospect.Client.Ui;
using Prospect.Game.Client;
using Prospect.Game.World;

namespace Prospect.Client.Screens;

internal sealed partial class ScreenHost
{
    /// <summary>
    /// The target panel's shield and hull rows from <paramref name="top"/>: the console's icons and slanted
    /// segments at a smaller size (<see cref="TargetLook"/>), with the figures at the right where they fit and the
    /// shield's state in words when it is broken. A row the snapshot gives nothing for is left out: a hull
    /// without a shield says NONE, one without hull strength shows no hull row. Returns the top of whatever
    /// follows the rows.
    /// </summary>
    private float DrawTargetGauges(ImDrawListPtr draw, in HullView target, in FlightParams flight, float left, float top, float width)
    {
        var look = TargetLook.Current;
        var scale = Kit.Scale;
        // One reading column for both rows, so their bars end together: wide enough for the longest thing either says.
        var column = MathF.Max(
            Kit.Measure(_ui.Mono, Theme.FontCaption, "RESTART 100 %"u8).X,
            Kit.Measure(_ui.Mono, Theme.FontCaption, "99999 / 99999"u8).X);
        column = MathF.Min(column, MathF.Floor(width * 0.45f));

        var shield = TargetGauges.Shield(flight, target.ShieldOffline);
        var shieldFraction = FlightConsole.Fraction(target.Shield, flight.ShieldCapacity);
        var hasShield = shield != ShieldReading.None;
        var offline = shield is ShieldReading.Offline or ShieldReading.Restarting;
        var shieldTone = FlightConsole.ShieldColor(hasShield, offline);
        var row = TargetGauges.Place(look, scale, left, top, width, column);
        ConsoleBars.Glyph(draw, ConsoleIcon.Shield, new float2(row.IconX, row.IconY), shieldTone, look.IconSize);
        ConsoleBars.Segments(draw, row.Track, shieldFraction, shieldFraction, offline ? Theme.LineStrong : Theme.Info, false, look.Segments, look.Gap, look.Slant);
        switch (shield)
        {
            case ShieldReading.Restarting:
                var progress = FlightConsole.RestartProgress(target.Shield, flight);
                ConsoleBars.Marker(draw, row.Track, FlightConsole.RestartMark(flight), 0.85f, look.Slant);
                TargetReading(draw, row, _ui.Mono, Theme.Bad, Kit.F($"RESTART {progress * 100f:0} %"));
                break;
            case ShieldReading.Offline:
                TargetReading(draw, row, _ui.SansMedium, Theme.Bad, "OFFLINE"u8);
                break;
            case ShieldReading.Online:
                TargetReading(draw, row, _ui.Mono, Theme.Text, Points(target.Shield, flight.ShieldCapacity));
                break;
            default:
                TargetReading(draw, row, _ui.SansMedium, Theme.TextMute, "NONE"u8);
                break;
        }

        top = TargetGauges.Next(look, scale, top);
        if (!(flight.HullPoints > 0f))
            return top;

        var hull = FlightConsole.Fraction(target.Hull, flight.HullPoints);
        var hullTone = FlightConsole.HullColor(hull);
        row = TargetGauges.Place(look, scale, left, top, width, column);
        ConsoleBars.Glyph(draw, ConsoleIcon.Hull, new float2(row.IconX, row.IconY), hullTone, look.IconSize);
        ConsoleBars.Segments(draw, row.Track, hull, hull, hullTone, false, look.Segments, look.Gap, look.Slant);
        TargetReading(draw, row, _ui.Mono, hull < FlightConsole.HullLow ? Theme.Bad : Theme.Text, Points(target.Hull, flight.HullPoints));
        return TargetGauges.Next(look, scale, top);
    }

    /// <summary>A row's reading, right-aligned and clipped to its column, centred on the row's height.</summary>
    private void TargetReading(ImDrawListPtr draw, in TargetGauges.Row row, ImFontPtr font, uint rgb, ImString reading)
    {
        var size = Kit.Measure(font, Theme.FontCaption, reading);
        var left = MathF.Max(row.Track.Right, row.ReadingRight - size.X);
        draw.PushClipRect(new float2(left, row.IconY - Kit.Px(2)), new float2(row.ReadingRight + 1f, row.IconY + row.Height), true);
        Kit.Text(draw, font, Theme.FontCaption, new float2(row.ReadingRight - size.X, row.Track.Top + (row.Track.Bottom - row.Track.Top - size.Y) * 0.5f), rgb, reading);
        draw.PopClipRect();
    }
}
