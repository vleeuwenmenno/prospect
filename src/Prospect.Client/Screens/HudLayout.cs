using Brutal.Numerics;
using Prospect.Game.World;

namespace Prospect.Client.Screens;

/// <summary>A screen rectangle in pixels.</summary>
internal readonly record struct HudRect(float Left, float Top, float Right, float Bottom)
{
    public float Width => Right - Left;
    public float Height => Bottom - Top;

    /// <summary>True when a column from <paramref name="left"/> to <paramref name="right"/> shares any width with this.</summary>
    public bool Spans(float left, float right) => left < Right && right > Left;

    public bool Contains(float2 point) => point.X >= Left && point.X <= Right && point.Y >= Top && point.Y <= Bottom;
}

/// <summary>What the jump strip above the pilot console says, from the most to the least urgent.</summary>
internal enum JumpPrompt : byte
{
    /// <summary>Nothing to say: the strip is not drawn and reserves no room.</summary>
    None,

    /// <summary>The hull flies a move order; no jump is plotted.</summary>
    Autopilot,

    /// <summary>A jump is plotted and the nose is not on it.</summary>
    OffCourse,

    /// <summary>The boost key asked for the jump in the last moments while the nose was off it.</summary>
    Refused,

    /// <summary>The nose is on the plotted jump: the boost key starts the drive. The drive is not ready yet.</summary>
    Aligned,

    /// <summary>A jump order is out and the drive has not started on it yet.</summary>
    Ordered,

    /// <summary>The hull brakes and turns onto the destination.</summary>
    Aligning,

    /// <summary>The drive calculates the jump and charges at once; it fires when both are done.</summary>
    Working,

    /// <summary>The hull has left and is between sectors.</summary>
    Transit,
}

/// <summary>
/// Where the heads-up display's pieces sit, apart from drawing so tests can check them without fonts: the left
/// column (sector panel, party, chat), the jump strip above the pilot console and the NAV card beside its marker.
/// Every piece takes its clearances from here, so none can grow into another. Sizes are in unscaled pixels.
/// </summary>
internal static class HudLayout
{
    /// <summary>The space between the screen's edge and every panel.</summary>
    public const float Margin = 24f;

    /// <summary>The width of the left column: the sector panel and the party list.</summary>
    public const float ColumnWidth = 360f;

    /// <summary>The space kept between two neighbouring pieces.</summary>
    public const float Gap = 12f;

    /// <summary>Height of the quick buttons at the top right.</summary>
    public const float QuickBarHeight = 32f;

    /// <summary>The target readout at the top centre, at its tallest.</summary>
    public const float TargetReadoutHeight = 104f;

    /// <summary>How far the console's end brackets reach outside its rows: out to the side, and above and below.</summary>
    public const float BracketOut = 10f;
    public const float BracketRise = 5f;

    /// <summary>The jump strip: its height, the gap under it and its widest.</summary>
    public const float StripHeight = 40f;
    public const float StripGap = 8f;
    public const float StripMax = 480f;

    /// <summary>How far each pointed end of the strip and the NAV card reaches in.</summary>
    public const float StripPoint = 10f;
    public const float StripPad = 10f;

    /// <summary>The strip's status column, left of its divider: the state and a line under it.</summary>
    public const float StripStatus = 136f;

    /// <summary>The NAV card: its height, its width limits and where it sits from the marker.</summary>
    public const float NavCardHeight = 40f;
    public const float NavCardMin = 150f;
    public const float NavCardMax = 260f;
    public const float NavCardReach = 34f;
    public const float NavCardRise = 34f;

    /// <summary>The space kept clear round the NAV marker, which the card never covers.</summary>
    public const float NavMarkerClear = 16f;

    private static float Px(float design, float scale) => MathF.Round(design * scale);

    /// <summary>The top of the pilot console, its end brackets included.</summary>
    public static float ConsoleTop(float2 display, float scale) => FlightConsole.Layout(display, scale).Top - Px(BracketRise, scale);

    /// <summary>The jump strip, centred over the pilot console. It reserves room only while it is drawn.</summary>
    public static HudRect Strip(float2 display, float scale)
    {
        var width = Math.Min(Px(StripMax, scale), display.X - 2 * Px(FlightConsole.Side, scale));
        var cx = MathF.Round(display.X * 0.5f);
        var bottom = ConsoleTop(display, scale) - Px(StripGap, scale);
        var left = MathF.Round(cx - width * 0.5f);
        return new HudRect(left, bottom - Px(StripHeight, scale), left + width, bottom);
    }

    /// <summary>
    /// The lowest a column from <paramref name="left"/> to <paramref name="right"/> may reach over the pilot
    /// console: the console's top, or the strip's while the strip is drawn and the column shares its width.
    /// </summary>
    public static float ReservedTop(float2 display, float scale, float left, float right, bool strip)
    {
        var top = ConsoleTop(display, scale);
        if (!strip)
            return top;
        var rect = Strip(display, scale);
        return rect.Spans(left, right) ? Math.Min(top, rect.Top) : top;
    }

    /// <summary>The left column's right edge: the sector panel's and the party list's.</summary>
    public static float ColumnRight(float scale) => Px(Margin, scale) + Px(ColumnWidth, scale);

    /// <summary>
    /// The recent chat lines' width: as wide as asked, narrowed to stay beside the strip when it can keep at least
    /// <paramref name="least"/>, so the lines do not move when the strip comes and goes.
    /// </summary>
    public static float FeedWidth(float2 display, float scale, float left, float width, float least)
    {
        var strip = Strip(display, scale);
        var beside = strip.Left - Px(Gap, scale) - left;
        return left + width > strip.Left - Px(Gap, scale) && beside >= least ? beside : width;
    }

    /// <summary>
    /// Where the NAV card may go: right of the left column, under the quick buttons (or the target readout when a
    /// target is shown), inside the right margin and above the jump strip.
    /// </summary>
    public static HudRect NavFree(float2 display, float scale, bool targetShown)
    {
        var top = Px(Margin, scale) + Px(targetShown ? TargetReadoutHeight : QuickBarHeight, scale) + Px(Gap, scale);
        return new HudRect(ColumnRight(scale) + Px(Gap, scale), top, display.X - Px(Margin, scale), Strip(display, scale).Top - Px(Gap, scale));
    }

    /// <summary>
    /// Places the NAV card above and to the right of its marker, or to the left where the right has no room, inside
    /// <paramref name="free"/>. False when it does not fit there without covering the marker: the card then
    /// collapses to the marker's label.
    /// </summary>
    public static bool PlaceNavCard(float2 marker, float2 size, in HudRect free, float scale, out float2 min)
    {
        var reach = Px(NavCardReach, scale);
        min = new float2(marker.X + reach, marker.Y - Px(NavCardRise, scale) - size.Y * 0.5f);
        if (size.X > free.Width || size.Y > free.Height)
            return false;
        if (min.X + size.X > free.Right)
            min.X = marker.X - reach - size.X;
        min.X = Math.Clamp(min.X, free.Left, free.Right - size.X);
        min.Y = Math.Clamp(min.Y, free.Top, free.Bottom - size.Y);
        var clear = Px(NavMarkerClear, scale);
        var covers = marker.X > min.X - clear && marker.X < min.X + size.X + clear && marker.Y > min.Y - clear && marker.Y < min.Y + size.Y + clear;
        return !covers;
    }

    /// <summary>What the strip says, from the hull's jump and what the pilot has plotted.</summary>
    public static JumpPrompt Prompt(bool nav, bool aimed, bool refused, JumpPhase phase, bool jumpOrdered, bool autopilot) =>
        phase switch
        {
            JumpPhase.Align => JumpPrompt.Aligning,
            JumpPhase.Charge => JumpPrompt.Working,
            JumpPhase.Tunnel => JumpPrompt.Transit,
            // The arrival belongs to the world: the effects round the hull say it, and the strip steps aside.
            JumpPhase.Arrive => JumpPrompt.None,
            _ when jumpOrdered => JumpPrompt.Ordered,
            _ when nav => aimed ? JumpPrompt.Aligned : refused ? JumpPrompt.Refused : JumpPrompt.OffCourse,
            _ when autopilot => JumpPrompt.Autopilot,
            _ => JumpPrompt.None,
        };

    /// <summary>
    /// The strip's state word. Only what has happened is claimed: an aimed nose is "course aligned", never "ready",
    /// since the drive still has to calculate and charge after the boost key starts it.
    /// </summary>
    public static ReadOnlySpan<byte> PromptWord(JumpPrompt prompt, bool calculated) =>
        prompt switch
        {
            JumpPrompt.Autopilot => "AUTOPILOT"u8,
            JumpPrompt.OffCourse => "OFF COURSE"u8,
            JumpPrompt.Refused => "NOT ALIGNED"u8,
            JumpPrompt.Aligned => "COURSE ALIGNED"u8,
            JumpPrompt.Ordered => "JUMP ORDERED"u8,
            JumpPrompt.Aligning => "ALIGNING"u8,
            JumpPrompt.Working => calculated ? "CHARGING"u8 : "CALCULATING"u8,
            JumpPrompt.Transit => "IN TRANSIT"u8,
            _ => default,
        };

    /// <summary>The strip's divider, and the column right of it where the action or the progress goes.</summary>
    public static void StripColumns(in HudRect strip, float scale, out float statusLeft, out float divider, out float actionLeft, out float actionRight)
    {
        var inset = Px(StripPoint, scale) + Px(StripPad, scale);
        statusLeft = strip.Left + inset;
        divider = statusLeft + Px(StripStatus, scale);
        actionLeft = divider + Px(StripPad, scale);
        actionRight = strip.Right - inset;
    }
}
