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

    /// <summary>True when the rectangles share an area.</summary>
    public bool Overlaps(in HudRect other) => Spans(other.Left, other.Right) && Top < other.Bottom && Bottom > other.Top;
}

/// <summary>What the jump strip at the top centre says, from the most to the least urgent.</summary>
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

    /// <summary>The server refused the last plot or jump start; shown for a few seconds, over the states of a plot.</summary>
    Denied,
}

/// <summary>
/// Where the heads-up display's pieces sit, apart from drawing so tests can check them without fonts: the left
/// column (sector panel, party, chat), the jump strip at the top centre and the NAV card beside its marker.
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

    /// <summary>The height of the pilot's status chip above the flight console.</summary>
    public const float ChipHeight = 26f;

    /// <summary>
    /// Height of the navigation bar along the top edge. Every piece under it starts at <see cref="Top"/>, so nothing
    /// is ever drawn under the bar.
    /// </summary>
    public const float TopBarHeight = 28f;

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

    /// <summary>The jump strip: its height and its widest. The gap above it is content (<c>jumpStripGap</c> in map.json).</summary>
    public const float StripHeight = 40f;
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

    /// <summary>The bar's bottom edge in pixels.</summary>
    public static float TopBarBottom(float scale) => Px(TopBarHeight, scale);

    /// <summary>
    /// Where the pieces under the navigation bar start, one small step below it. The bar and the step together
    /// cost the view only a few pixels more than the old top margin, so the target readout and the strip still
    /// leave the middle of a 1655 by 688 screen at twice the scale to the cues.
    /// </summary>
    public static float Top(float scale) => Px(TopBarHeight, scale) + Px(Theme.Space1, scale);

    /// <summary>The top of the pilot console.</summary>
    public static float ConsoleTop(float2 display, float scale) => FlightConsole.Layout(display, scale).Top;

    /// <summary>The jump strip at the top centre, its top edge at <paramref name="top"/>. It reserves room only while it is drawn.</summary>
    public static HudRect Strip(float2 display, float scale, float top)
    {
        var width = Math.Min(Px(StripMax, scale), display.X - 2 * Px(FlightConsole.Side, scale));
        var left = MathF.Round(MathF.Round(display.X * 0.5f) - width * 0.5f);
        return new HudRect(left, top, left + width, top + Px(StripHeight, scale));
    }

    /// <summary>
    /// Where the jump strip's top edge goes: <paramref name="gap"/> (pixels) under whatever else holds the top centre.
    /// That is the target readout while a target is shown and the scan strip while it is drawn
    /// (<paramref name="scanBottom"/>, zero when it is not), so the three never share a pixel.
    /// </summary>
    public static float StripTop(float scale, bool targetShown, float scanBottom, float gap)
    {
        var below = Top(scale) + (targetShown ? Px(TargetReadoutHeight, scale) : 0f);
        return Math.Max(below, scanBottom) + gap;
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
    /// Where the NAV card may go: right of the left column, under the navigation bar (and the target readout when a
    /// target is shown, and the jump strip while it is drawn), inside the right margin and above the console.
    /// </summary>
    public static HudRect NavFree(float2 display, float scale, bool targetShown, in HudRect strip = default)
    {
        // A small step under the readout, as under the bar: on a short screen at a large scale the card needs it.
        var top = Top(scale) + (targetShown ? Px(TargetReadoutHeight, scale) + Px(Theme.Space2, scale) : 0f);
        if (strip.Height > 0f)
            top = Math.Max(top, strip.Bottom + Px(Gap, scale));
        return new HudRect(ColumnRight(display, scale) + Px(Gap, scale), top, display.X - Px(Margin, scale), ConsoleTop(display, scale) - Px(Gap, scale));
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
        return !covers && !room.Covers(new HudRect(min.X, min.Y, min.X + size.X, min.Y + size.Y))
            && !room.Crosses(marker, NavTieAnchor(marker, min, size));
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
            JumpPrompt.Denied => "JUMP REFUSED"u8,
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
/// under the navigation bar, above the pilot console, and clear of the left column, recent chat, target readout, jump
/// strip and every window the player pinned over the view while each is drawn. Built once a frame and handed to every marker, so the target's arrow, the NAV marker
/// and the NAV card's tie all keep the same clearances. Rectangles are the pieces themselves; each test grows them
/// by the room it needs.
/// </summary>
internal readonly struct HudCueRoom
{
    /// <summary>The area markers live in: inside the margins, under the navigation bar, above the console.</summary>
    public readonly HudRect Screen;

    /// <summary>The left column, target readout, jump strip and recent chat; empty when not drawn.</summary>
    public readonly HudRect Column;
    public readonly HudRect Readout;
    public readonly HudRect Strip;
    public readonly HudRect Chat;

    /// <summary>
    /// Windows the player pinned over the view, the first <see cref="PanelCount"/> of them. The array is the
    /// caller's and is read only while this frame's markers are placed.
    /// </summary>
    public readonly HudRect[]? Panels;
    public readonly int PanelCount;

    /// <summary>A cue's reach round its point, and the gap kept between a cue and a piece, in pixels.</summary>
    public readonly float Reach;
    public readonly float Gap;

    /// <summary>
    /// Where a cue's direction is measured from: the middle of the view, or the nearest point to it a cue fits at
    /// when a piece is drawn too close to the middle (a short screen at a large scale).
    /// </summary>
    public readonly float2 Start;

    public HudCueRoom(float2 display, float scale, float columnBottom, bool targetShown, HudRect strip, HudRect chat = default, HudRect[]? panels = null, int panelCount = 0)
    {
        float Px(float design) => MathF.Round(design * scale);

        Reach = Px(HudLayout.CueReach);
        Gap = Px(HudLayout.Gap);
        Screen = new HudRect(Px(HudLayout.Margin), HudLayout.Top(scale), display.X - Px(HudLayout.Margin), HudLayout.ConsoleTop(display, scale));
        Column = new HudRect(0f, 0f, HudLayout.ColumnRight(display, scale), columnBottom);
        var half = Px(HudLayout.TargetReadoutWidth) * 0.5f;
        Readout = targetShown ? new HudRect(display.X * 0.5f - half, 0f, display.X * 0.5f + half, HudLayout.Top(scale) + Px(HudLayout.TargetReadoutHeight)) : default;
        Strip = strip;
        Chat = chat;
        Panels = panels;
        PanelCount = panels is null ? 0 : Math.Clamp(panelCount, 0, panels.Length);

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
            top = Math.Max(top, Strip.Bottom + pad);
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
        if (Inside(Column, point, pad) || Inside(Readout, point, pad) || Inside(Strip, point, pad) || Inside(Chat, point, pad))
            return false;
        for (var i = 0; i < PanelCount; i++)
        {
            if (Inside(Panels![i], point, pad))
                return false;
        }

        return true;
    }

    /// <summary>True when a rectangle shares an area with the recent chat or a pinned window.</summary>
    public bool Covers(in HudRect rect)
    {
        if (Chat.Overlaps(rect))
            return true;
        for (var i = 0; i < PanelCount; i++)
        {
            if (Panels![i].Overlaps(rect))
                return true;
        }

        return false;
    }

    /// <summary>The largest marker half-size that fits at a point; negative when the point is obstructed.</summary>
    public float Space(float2 point)
    {
        var space = Math.Min(Math.Min(point.X - Screen.Left, Screen.Right - point.X), Math.Min(point.Y - Screen.Top, Screen.Bottom - point.Y));
        space = Math.Min(space, Math.Min(Math.Min(Outside(Column, point), Outside(Readout, point)), Math.Min(Outside(Strip, point), Outside(Chat, point))));
        for (var i = 0; i < PanelCount; i++)
            space = Math.Min(space, Outside(Panels![i], point));
        return space;
    }

    private static float Outside(in HudRect box, float2 point) => box.Right <= box.Left ? float.PositiveInfinity
        : Math.Max(Math.Max(box.Left - point.X, point.X - box.Right), Math.Max(box.Top - point.Y, point.Y - box.Bottom));

    /// <summary>
    /// Where the arrow for a target along <paramref name="direction"/> (a unit vector) from <paramref name="from"/>
    /// sits: the far end of the first stretch of that ray that is clear of every piece by a cue's reach and gap,
    /// inside the screen. Where the ray starts under pieces (a window the player dragged over the middle of the
    /// view), the stretch begins where it leaves the last of them, so the arrow keeps its direction and sits
    /// beyond. False when no point of the ray on screen is clear: the cue is then not drawn at all.
    /// </summary>
    public bool Edge(float2 from, float2 direction, out float2 at)
    {
        var pad = Gap + Reach;
        var limit = float.PositiveInfinity;
        if (direction.X > 1e-6f)
            limit = (Screen.Right - pad - from.X) / direction.X;
        else if (direction.X < -1e-6f)
            limit = (Screen.Left + pad - from.X) / direction.X;
        if (direction.Y > 1e-6f)
            limit = Math.Min(limit, (Screen.Bottom - pad - from.Y) / direction.Y);
        else if (direction.Y < -1e-6f)
            limit = Math.Min(limit, (Screen.Top + pad - from.Y) / direction.Y);
        limit = Math.Max(float.IsFinite(limit) ? limit : 0f, 0f);

        // Step past every piece the point is under until it is under none; each piece is left at most once.
        var start = 0f;
        for (var pass = 0; pass <= PieceCount; pass++)
        {
            var point = from + direction * start;
            var exit = Exit(Column, pad, point, direction);
            exit = Math.Max(exit, Exit(Readout, pad, point, direction));
            exit = Math.Max(exit, Exit(Strip, pad, point, direction));
            exit = Math.Max(exit, Exit(Chat, pad, point, direction));
            for (var i = 0; i < PanelCount; i++)
                exit = Math.Max(exit, Exit(Panels![i], pad, point, direction));
            if (exit <= 0f)
                break;
            // A hair past the boundary, so rounding cannot leave the point on the piece's inside.
            start += exit + 0.01f;
        }

        at = from + direction * start;
        if (start > limit + 0.01f || !Clear(at, pad - 0.02f))
            return false;

        var stretch = from + direction * start;
        var end = limit - start;
        end = Math.Min(end, Entry(Column, pad, stretch, direction));
        end = Math.Min(end, Entry(Readout, pad, stretch, direction));
        end = Math.Min(end, Entry(Strip, pad, stretch, direction));
        end = Math.Min(end, Entry(Chat, pad, stretch, direction));
        for (var i = 0; i < PanelCount; i++)
            end = Math.Min(end, Entry(Panels![i], pad, stretch, direction));
        at = stretch + direction * Math.Max(end, 0f);
        return true;
    }

    /// <summary>The pieces a ray can be under: the column, readout, strip, recent chat and every window.</summary>
    private int PieceCount => 4 + PanelCount;

    /// <summary>How far along <paramref name="along"/> a point under the grown box leaves it; zero when it is not under it.</summary>
    private static float Exit(in HudRect box, float pad, float2 from, float2 along)
    {
        if (!Inside(box, from, pad))
            return 0f;
        var near = float.NegativeInfinity;
        var far = float.PositiveInfinity;
        Slab(box.Left - pad, box.Right + pad, from.X, along.X, ref near, ref far);
        Slab(box.Top - pad, box.Bottom + pad, from.Y, along.Y, ref near, ref far);
        return Math.Max(far, 0f);
    }

    /// <summary>True when the straight line from <paramref name="from"/> to <paramref name="to"/> passes over a piece.</summary>
    public bool Crosses(float2 from, float2 to)
    {
        var along = to - from;
        if (Entry(Column, 0f, from, along) < 1f || Entry(Readout, 0f, from, along) < 1f || Entry(Strip, 0f, from, along) < 1f || Entry(Chat, 0f, from, along) < 1f)
            return true;
        for (var i = 0; i < PanelCount; i++)
        {
            if (Entry(Panels![i], 0f, from, along) < 1f)
                return true;
        }

        return false;
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

    /// <summary>
    /// True when the arrow has nowhere clear to go on screen this frame (pieces cover every point that way): it is
    /// not drawn, rather than drawn under a piece.
    /// </summary>
    public bool Hidden;

    /// <summary>Where the marker was drawn last, and where it slides from while leaving its target.</summary>
    public float2 At;
    public float2 From;

    /// <summary>
    /// One frame. <paramref name="screen"/> is the target's point where <paramref name="inFront"/>; otherwise
    /// <paramref name="across"/> is its direction across the view, and <paramref name="settled"/> false when that
    /// direction is too close to straight behind to mean anything. Returns the eased morph; <see cref="At"/> is
    /// where to draw.
    /// </summary>
    public float Step(in HudCueRoom room, bool fresh, bool inFront, float2 screen, float2 across, bool settled, float scale, float dt, bool rock = false)
    {
        // A close target keeps a smaller marker on its centre; its projected size must not send it to an edge.
        var reach = Math.Max(room.Reach, MinimumReach(rock, scale));
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

        var wasHidden = Hidden;
        Hidden = false;
        if (!room.Edge(room.Start, new float2(MathF.Cos(Angle), MathF.Sin(Angle)), out var border))
        {
            // No clear room that way. On its target the bracket stays there; the arrow is not drawn.
            if (Out)
            {
                Hidden = true;
                Morph = 1f;
                return 1f;
            }

            At = From = screen;
            Morph = 0f;
            return 0f;
        }

        var morph = Morph * Morph * (3f - 2f * Morph);
        if ((fresh && Out) || wasHidden)
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

    /// <summary>The smallest on-target marker's bounds, including its ring, brackets and stroke.</summary>
    public static float MinimumReach(bool rock, float scale) =>
        (rock ? MathF.Round(14f * scale) + MathF.Round(5f * scale)
            : MathF.Round(18f * scale) * 1.25f) + MathF.Round(scale);

    /// <summary>
    /// The bracket's size at a morph: its size on the target, shrinking to <paramref name="arrow"/>. Off its target
    /// it starts no larger than <paramref name="cap"/>. Its final size fits the space at the drawn point.
    /// </summary>
    public static float Size(float onTarget, float arrow, float cap, bool anchored, float morph, float available = float.PositiveInfinity)
    {
        var start = anchored ? onTarget : Math.Min(onTarget, cap);
        return Math.Min(start + (arrow - start) * morph, Math.Max(0f, available));
    }
}
