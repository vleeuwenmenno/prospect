using Brutal.Numerics;
using Prospect.Client.Ui;
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

    /// <summary>The widest the left column grows: the sector panel and the party list.</summary>
    public const float ColumnMax = 360f;

    /// <summary>Half the width kept clear round the middle of the view, where the reticle and the hull are.</summary>
    public const float CenterClear = 64f;

    /// <summary>The space kept between two neighbouring pieces.</summary>
    public const float Gap = 12f;

    /// <summary>Height of the quick buttons at the top right.</summary>
    public const float QuickBarHeight = 32f;

    /// <summary>The target readout at the top centre, at its tallest, and its width.</summary>
    public const float TargetReadoutHeight = 104f;
    public const float TargetReadoutWidth = 340f;

    /// <summary>
    /// How far a direction cue reaches round its point: the target's arrow with its ring, the bracket it shrinks
    /// from away from its target, and the NAV arrow. Its label goes beside it, within this height.
    /// </summary>
    public const float CueReach = 18f;

    /// <summary>How far a cue's label sits from the cue's point, to the label's middle.</summary>
    public const float CueLabel = 34f;

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

    /// <summary>The top of the pilot console.</summary>
    public static float ConsoleTop(float2 display, float scale) => FlightConsole.Layout(display, scale).Top;

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

    /// <summary>The left edge of the middle kept clear: the left column and the recent chat stop short of it.</summary>
    public static float MiddleLeft(float2 display, float scale) => MathF.Round(display.X * 0.5f) - Px(CenterClear, scale) - Px(Gap, scale);

    /// <summary>The left column's width: its full width where that leaves the middle clear, narrower where not.</summary>
    public static float ColumnWidth(float2 display, float scale) =>
        Math.Max(0f, Math.Min(Px(ColumnMax, scale), MiddleLeft(display, scale) - Px(Margin, scale)));

    /// <summary>The left column's right edge: the sector panel's and the party list's.</summary>
    public static float ColumnRight(float2 display, float scale) => Px(Margin, scale) + ColumnWidth(display, scale);

    /// <summary>The party rows and their bottom edge, shared by drawing and this frame's marker reservations.</summary>
    public static (float Top, float Step, int Count, float Bottom) PartyRows(float top, float limit, bool cards, bool invitation, int others, float scale)
    {
        var y = top + Px(Theme.Space3, scale);
        if (invitation)
            y += Px(cards ? 52f : 24f, scale) + Px(Theme.Space2, scale);
        var step = Px(cards ? 58f : 24f, scale) + Px(Theme.Space1, scale);
        if (others <= 0)
            return (y, step, 0, y);
        y += Px(18f, scale);
        var count = Math.Min(others, Math.Max(1, (int)((limit - y) / step)));
        return (y, step, count, y + count * step);
    }

    /// <summary>The recent chat lines' width: as asked, but never into the middle.</summary>
    public static float ChatWidth(float2 display, float scale, float left, float width) => Math.Min(width, MiddleLeft(display, scale) - left);

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
        return new HudRect(ColumnRight(display, scale) + Px(Gap, scale), top, display.X - Px(Margin, scale), Strip(display, scale).Top - Px(Gap, scale));
    }

    /// <summary>
    /// Places the NAV card above and to the right of its marker, or to the left where the right has no room, inside
    /// <paramref name="free"/>. False when it does not fit there without covering the marker: the card then
    /// collapses to the marker's label.
    /// </summary>
    public static bool PlaceNavCard(float2 marker, float2 size, in HudRect free, in HudCueRoom room, float scale, out float2 min)
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
        // The tie from the marker to the card never crosses a panel, the strip or the console.
        return !covers && !room.Crosses(marker, NavTieAnchor(marker, min, size));
    }

    /// <summary>Where the tie meets the card: the middle of its side nearer the marker.</summary>
    public static float2 NavTieAnchor(float2 marker, float2 min, float2 size) =>
        new(min.X > marker.X ? min.X : min.X + size.X, min.Y + size.Y * 0.5f);

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

/// <summary>
/// Where the target's and the plotted jump's markers may sit over the pilot view: inside the screen's margins,
/// under the quick buttons, above the pilot console, and clear of the left column, the target readout and the jump
/// strip while each is drawn. Built once a frame and handed to every marker, so the target's arrow, the NAV marker
/// and the NAV card's tie all keep the same clearances. Rectangles are the pieces themselves; each test grows them
/// by the room it needs.
/// </summary>
internal readonly struct HudCueRoom
{
    /// <summary>The area markers live in: inside the margins, under the quick buttons, above the console.</summary>
    public readonly HudRect Screen;

    /// <summary>The left column, the target readout and the jump strip; an empty rectangle when not drawn.</summary>
    public readonly HudRect Column;
    public readonly HudRect Readout;
    public readonly HudRect Strip;

    /// <summary>A cue's reach round its point, and the gap kept between a cue and a piece, in pixels.</summary>
    public readonly float Reach;
    public readonly float Gap;

    /// <summary>
    /// Where a cue's direction is measured from: the middle of the view, or the nearest point to it a cue fits at
    /// when a piece is drawn too close to the middle (a short screen at a large scale).
    /// </summary>
    public readonly float2 Start;

    public HudCueRoom(float2 display, float scale, float columnBottom, bool targetShown, bool strip)
    {
        float Px(float design) => MathF.Round(design * scale);

        Reach = Px(HudLayout.CueReach);
        Gap = Px(HudLayout.Gap);
        Screen = new HudRect(Px(HudLayout.Margin), Px(HudLayout.Margin + HudLayout.QuickBarHeight), display.X - Px(HudLayout.Margin), HudLayout.ConsoleTop(display, scale));
        Column = new HudRect(0f, 0f, HudLayout.ColumnRight(display, scale), columnBottom);
        var half = Px(HudLayout.TargetReadoutWidth) * 0.5f;
        Readout = targetShown ? new HudRect(display.X * 0.5f - half, 0f, display.X * 0.5f + half, Px(HudLayout.Margin + HudLayout.TargetReadoutHeight)) : default;
        Strip = strip ? HudLayout.Strip(display, scale) : default;

        // Straight down from the middle of the view to the first piece under it, straight up to the first over it.
        var pad = Gap + Reach;
        var middle = new float2(MathF.Round(display.X * 0.5f), MathF.Round(display.Y * 0.5f));
        var top = Screen.Top + pad;
        var bottom = Screen.Bottom - pad;
        if (Column.Spans(middle.X - pad, middle.X + pad))
            top = Math.Max(top, Column.Bottom + pad);
        if (Readout.Spans(middle.X - pad, middle.X + pad))
            top = Math.Max(top, Readout.Bottom + pad);
        if (Strip.Spans(middle.X - pad, middle.X + pad))
            bottom = Math.Min(bottom, Strip.Top - pad);
        // With no height to fit a cue between them, it goes halfway; that only happens on screens far shorter than
        // any the interface is laid out for.
        Start = new float2(middle.X, top <= bottom ? Math.Clamp(middle.Y, top, bottom) : MathF.Round((top + bottom) * 0.5f));
    }

    /// <summary>The space round a cue's point: its reach, and the gap to the pieces once it no longer marks its target.</summary>
    public float Pad(bool anchored) => anchored ? Reach : Gap + Reach;

    /// <summary>
    /// True when a point is on screen and <paramref name="pad"/> clear of every piece: a marker there is drawn on
    /// its target. A point under a panel, the strip or the console is obstructed.
    /// </summary>
    public bool Clear(float2 point, float pad)
    {
        if (point.X < Screen.Left + pad || point.X > Screen.Right - pad || point.Y < Screen.Top + pad || point.Y > Screen.Bottom - pad)
            return false;
        return !Inside(Column, point, pad) && !Inside(Readout, point, pad) && !Inside(Strip, point, pad);
    }

    /// <summary>
    /// The last point clear for a cue on the way from <paramref name="from"/> along <paramref name="direction"/>
    /// (a unit vector): where the arrow for a target that way sits.
    /// </summary>
    public float2 Edge(float2 from, float2 direction)
    {
        var pad = Gap + Reach;
        var t = float.PositiveInfinity;
        if (direction.X > 1e-6f)
            t = (Screen.Right - pad - from.X) / direction.X;
        else if (direction.X < -1e-6f)
            t = (Screen.Left + pad - from.X) / direction.X;
        if (direction.Y > 1e-6f)
            t = Math.Min(t, (Screen.Bottom - pad - from.Y) / direction.Y);
        else if (direction.Y < -1e-6f)
            t = Math.Min(t, (Screen.Top + pad - from.Y) / direction.Y);
        t = Math.Min(t, Entry(Column, pad, from, direction));
        t = Math.Min(t, Entry(Readout, pad, from, direction));
        t = Math.Min(t, Entry(Strip, pad, from, direction));
        return from + direction * Math.Max(float.IsFinite(t) ? t : 0f, 0f);
    }

    /// <summary>True when the straight line from <paramref name="from"/> to <paramref name="to"/> passes over a piece.</summary>
    public bool Crosses(float2 from, float2 to)
    {
        var along = to - from;
        return Entry(Column, 0f, from, along) < 1f || Entry(Readout, 0f, from, along) < 1f || Entry(Strip, 0f, from, along) < 1f;
    }

    private static bool Inside(in HudRect box, float2 point, float pad) =>
        box.Right > box.Left && point.X > box.Left - pad && point.X < box.Right + pad && point.Y > box.Top - pad && point.Y < box.Bottom + pad;

    /// <summary>How far along <paramref name="along"/> from <paramref name="from"/> the grown box starts; infinite when never.</summary>
    private static float Entry(in HudRect box, float pad, float2 from, float2 along)
    {
        if (box.Right <= box.Left)
            return float.PositiveInfinity;
        var near = float.NegativeInfinity;
        var far = float.PositiveInfinity;
        if (!Slab(box.Left - pad, box.Right + pad, from.X, along.X, ref near, ref far) || !Slab(box.Top - pad, box.Bottom + pad, from.Y, along.Y, ref near, ref far))
            return float.PositiveInfinity;
        return far > 0f && near < far ? Math.Max(near, 0f) : float.PositiveInfinity;
    }

    private static bool Slab(float low, float high, float from, float along, ref float near, ref float far)
    {
        if (MathF.Abs(along) < 1e-6f)
            return from > low && from < high;
        var a = (low - from) / along;
        var b = (high - from) / along;
        near = Math.Max(near, Math.Min(a, b));
        far = Math.Min(far, Math.Max(a, b));
        return true;
    }
}

/// <summary>
/// The target marker's motion, apart from drawing so it can be tested: on its target while the target's point is
/// clear, and an arrow at the edge of the clear room, pointing the way to turn, while the target is behind or
/// under a piece. Crossing between the two, the marker slides from where it last was to the edge, or from the
/// edge to the target, while the bracket shrinks into the arrow or grows back out of it.
/// </summary>
internal struct TargetCue
{
    /// <summary>Seconds the bracket takes to become the arrow, or the arrow the bracket.</summary>
    public const float MorphSeconds = 0.3f;

    /// <summary>How much further clear a target must be before the arrow goes back to it, so it cannot flicker.</summary>
    public const float Hysteresis = 2f;

    /// <summary>True while the arrow shows, or is taking over.</summary>
    public bool Out;

    /// <summary>0 for the bracket on its target, 1 for the arrow at the edge.</summary>
    public float Morph;

    /// <summary>The arrow's direction, in radians on the screen.</summary>
    public float Angle;

    /// <summary>Where the marker was drawn last, and where it slides from while leaving its target.</summary>
    public float2 At;
    public float2 From;

    /// <summary>
    /// One frame. <paramref name="screen"/> is the target's point where <paramref name="inFront"/>; otherwise
    /// <paramref name="across"/> is its direction across the view, and <paramref name="settled"/> false when that
    /// direction is too close to straight behind to mean anything. Returns the eased morph; <see cref="At"/> is
    /// where to draw.
    /// </summary>
    public float Step(in HudCueRoom room, bool fresh, bool inFront, float2 screen, float2 across, bool settled, float scale, float dt, float targetReach = 0f)
    {
        var reach = Math.Max(room.Reach, targetReach);
        var clear = inFront && room.Clear(screen, reach);
        var deep = inFront && room.Clear(screen, reach + MathF.Round(Hysteresis * scale));

        // The way to turn: toward the target's point while it is in front, so the arrow points at a target under a
        // panel and agrees with the bracket where one becomes the other; across the view while it is behind.
        var toward = inFront ? screen - room.Start : across;
        var length = MathF.Sqrt(toward.X * toward.X + toward.Y * toward.Y);
        settled = inFront ? length > 1e-3f : settled && length > 1e-4f;
        var angle = settled ? MathF.Atan2(toward.Y, toward.X) : Angle;

        // A new target starts in whichever form fits, with no animation from the last one.
        if (fresh)
        {
            Out = !clear;
            Morph = Out ? 1f : 0f;
            Angle = settled ? angle : -MathF.PI * 0.5f;
            At = clear ? screen : room.Start;
        }

        var wasOut = Out;
        Out = Out ? !deep : !clear;
        if (Out && !wasOut)
            From = At;
        Morph = Math.Clamp(Morph + (Out ? dt : -dt) / MorphSeconds, 0f, 1f);

        // While the bracket shows, the arrow's angle follows the target exactly; while the arrow shows, it slides
        // round the edge to a new direction rather than jumping there.
        if (!Out || Morph <= 0f)
        {
            Angle = angle;
        }
        else
        {
            var turn = angle - Angle;
            turn -= MathF.Round(turn / MathF.Tau) * MathF.Tau;
            Angle += turn * (1f - MathF.Exp(-14f * dt));
        }

        var border = room.Edge(room.Start, new float2(MathF.Cos(Angle), MathF.Sin(Angle)));
        var morph = Morph * Morph * (3f - 2f * Morph);
        if (fresh && Out)
            From = border;
        At = Out ? From + (border - From) * morph : screen + (border - screen) * morph;
        // A panel may appear under the old point, or the animation may cross an occupied corner. Both endpoints
        // are checked against this frame's layout; skip the obstructed animation rather than draw under it.
        var extent = Out ? room.Reach : room.Reach + (reach - room.Reach) * (1f - morph);
        if (!room.Clear(At, extent))
        {
            At = Out ? border : screen;
            From = At;
            Morph = Out ? 1f : 0f;
            morph = Morph;
        }
        return morph;
    }

    /// <summary>The full on-target marker's bounds, including its ring, brackets and stroke.</summary>
    public static float OnTargetReach(float pixels, bool rock, float scale) =>
        (rock ? Math.Max(pixels * 1.08f, MathF.Round(14f * scale)) + MathF.Round(5f * scale)
            : Math.Max(pixels * 1.3f, MathF.Round(18f * scale)) * 1.25f) + MathF.Round(scale);

    /// <summary>
    /// The bracket's size at a morph: its size on the target, shrinking to <paramref name="arrow"/>. Off its target
    /// it starts no larger than <paramref name="cap"/>, so it never reaches past the cue's room.
    /// </summary>
    public static float Size(float onTarget, float arrow, float cap, bool anchored, float morph)
    {
        var start = anchored ? onTarget : Math.Min(onTarget, cap);
        return start + (arrow - start) * morph;
    }
}
