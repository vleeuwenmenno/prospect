using System.Text;
using Brutal.Numerics;
using Prospect.Client.Screens;
using Prospect.Game.World;
using Xunit;

namespace Prospect.Tests;

/// <summary>
/// Where the heads-up pieces sit on the screens the game is checked at, and what the jump strip says. Font widths
/// are checked with the shipped fonts in <see cref="FrameOrderTests"/>, which owns the Dear ImGui context.
/// </summary>
public sealed class HudLayoutTests
{
    public static TheoryData<float, float, float> Screens => new()
    {
        { 1280f, 720f, 1f },
        { 1655f, 688f, 1f },
        { 1920f, 1080f, 1f },
        { 1280f, 720f, 2f },
        { 1655f, 688f, 2f },
        { 1920f, 1080f, 2f },
    };

    private static float Px(float design, float scale) => MathF.Round(design * scale);

    [Theory]
    [MemberData(nameof(Screens))]
    public void TheConsoleAndTheStripStayOnScreenAndClearOfEachOther(float width, float height, float scale)
    {
        var display = new float2(width, height);
        var console = FlightConsole.Layout(display, scale);

        // The rows stay inside the screen, and nothing reaches above them: the console's top is the rows' top.
        Assert.True(console.LeftX > 0f);
        Assert.True(console.Right < width);
        Assert.Equal(console.Top, HudLayout.ConsoleTop(display, scale));

        // The strip sits above them with its gap, inside the side margins, centred on the speed readout.
        var strip = HudLayout.Strip(display, scale);
        Assert.Equal(HudLayout.ConsoleTop(display, scale) - Px(HudLayout.StripGap, scale), strip.Bottom);
        Assert.Equal(Px(HudLayout.StripHeight, scale), strip.Height);
        Assert.True(strip.Left >= Px(FlightConsole.Side, scale) && strip.Right <= width - Px(FlightConsole.Side, scale));
        Assert.InRange(strip.Left + strip.Width * 0.5f - console.CenterX, -1f, 1f);

        // Its columns: a status column and an action column wide enough for a key and a short instruction.
        HudLayout.StripColumns(strip, scale, out var statusLeft, out var divider, out var actionLeft, out var actionRight);
        Assert.True(statusLeft > strip.Left && divider > statusLeft && actionLeft > divider && actionRight < strip.Right);
        Assert.True(actionRight - actionLeft >= Px(260, scale), $"action {actionRight - actionLeft}");

        // And the middle of the view, where the hull flies, stays clear of it.
        Assert.True(strip.Top > height * 0.5f + Px(20, scale), $"strip top {strip.Top}");
    }

    [Theory]
    [MemberData(nameof(Screens))]
    public void TheLeftColumnAndTheRecentChatStayAboveEveryReservation(float width, float height, float scale)
    {
        var display = new float2(width, height);
        var strip = HudLayout.Strip(display, scale);
        var left = Px(HudLayout.Margin, scale);
        var right = HudLayout.ColumnRight(display, scale);

        // With the strip hidden only the console counts; with it drawn, a column that shares its width stops above
        // it too, and one that does not is not pushed up for nothing.
        Assert.Equal(HudLayout.ConsoleTop(display, scale), HudLayout.ReservedTop(display, scale, left, right, false));
        var reserved = HudLayout.ReservedTop(display, scale, left, right, true);
        Assert.True(reserved <= HudLayout.ConsoleTop(display, scale));
        Assert.Equal(strip.Spans(left, right) ? strip.Top : HudLayout.ConsoleTop(display, scale), reserved);

        // At the interface's own scale the column and the strip sit side by side, so plotting a jump never
        // shuffles the party list.
        if (scale == 1f)
            Assert.False(strip.Spans(left, right));

        // The recent chat is narrowed to stay beside the strip where a readable line remains; otherwise it keeps
        // its width and stops above the strip while it is drawn.
        var feed = HudLayout.FeedWidth(display, scale, left, Px(440, scale), Px(300, scale));
        if (feed < Px(440, scale))
        {
            Assert.True(feed >= Px(300, scale));
            Assert.True(left + feed <= strip.Left - Px(HudLayout.Gap, scale));
            Assert.Equal(HudLayout.ConsoleTop(display, scale), HudLayout.ReservedTop(display, scale, left, left + feed, true));
        }
        else
        {
            Assert.True(HudLayout.ReservedTop(display, scale, left, left + feed, true) <= strip.Top || !strip.Spans(left, left + feed));
        }
    }

    [Theory]
    [MemberData(nameof(Screens))]
    public void TheNavCardStaysInItsRoomAndNeverCoversItsMarker(float width, float height, float scale)
    {
        var display = new float2(width, height);
        var strip = HudLayout.Strip(display, scale);
        foreach (var cardWidth in new[] { HudLayout.NavCardMin, HudLayout.NavCardMax })
        {
            var size = new float2(Px(cardWidth, scale), Px(HudLayout.NavCardHeight, scale));
            foreach (var target in new[] { false, true })
            {
                var free = HudLayout.NavFree(display, scale, target);
                var room = new HudCueRoom(display, scale, Px(260, scale), target, false);
                Assert.True(free.Left >= HudLayout.ColumnRight(display, scale) + Px(HudLayout.Gap, scale));
                Assert.True(free.Right <= width - Px(HudLayout.Margin, scale));
                Assert.True(free.Bottom <= strip.Top - Px(HudLayout.Gap, scale));
                Assert.True(free.Top >= Px(HudLayout.Margin + (target ? HudLayout.TargetReadoutHeight : HudLayout.QuickBarHeight), scale));

                // The narrowest card fits on every checked screen, a target shown or not; the widest at the
                // interface's own scale. Where one does not fit it folds back to the marker's label.
                var fits = free.Width >= size.X && free.Height >= size.Y;
                if (scale == 1f || cardWidth == HudLayout.NavCardMin)
                    Assert.True(fits, $"free {free}");
                if (!fits)
                    continue;

                var clear = Px(HudLayout.NavMarkerClear, scale);
                var placedNearMiddle = false;
                for (var x = 0f; x <= width; x += 37f)
                {
                    for (var y = 0f; y <= height; y += 29f)
                    {
                        var marker = new float2(x, y);
                        if (!HudLayout.PlaceNavCard(marker, size, free, room, scale, out var min))
                            continue;
                        var max = min + size;
                        Assert.False(room.Crosses(marker, HudLayout.NavTieAnchor(marker, min, size)));
                        Assert.True(min.X >= free.Left && max.X <= free.Right && min.Y >= free.Top && max.Y <= free.Bottom);
                        Assert.False(marker.X > min.X - clear && marker.X < max.X + clear && marker.Y > min.Y - clear && marker.Y < max.Y + clear);
                        if (MathF.Abs(x - width * 0.5f) < 40f && MathF.Abs(y - height * 0.5f) < 30f)
                            placedNearMiddle = true;
                    }
                }

                // Where the nose usually is, with the hull aimed at the marker, the card is shown.
                Assert.True(placedNearMiddle);
            }
        }
    }

    [Fact]
    public void TheStripSaysOnlyWhatHasHappened()
    {
        // Aimed is aligned, never ready: the drive has still to calculate and charge.
        var aligned = HudLayout.Prompt(true, true, false, JumpPhase.None, false, false);
        Assert.Equal(JumpPrompt.Aligned, aligned);
        Assert.Equal("COURSE ALIGNED", Encoding.UTF8.GetString(HudLayout.PromptWord(aligned, false)));
        foreach (var prompt in Enum.GetValues<JumpPrompt>())
        {
            Assert.DoesNotContain("READY", Encoding.UTF8.GetString(HudLayout.PromptWord(prompt, false)));
            Assert.DoesNotContain("READY", Encoding.UTF8.GetString(HudLayout.PromptWord(prompt, true)));
        }

        // Off the marker: off course, and refused only while the nose is still off it.
        Assert.Equal(JumpPrompt.OffCourse, HudLayout.Prompt(true, false, false, JumpPhase.None, false, false));
        Assert.Equal(JumpPrompt.Refused, HudLayout.Prompt(true, false, true, JumpPhase.None, false, false));
        Assert.Equal(JumpPrompt.Aligned, HudLayout.Prompt(true, true, true, JumpPhase.None, false, false));

        // The drive's own stages come first; calculation and charge run together, so the word follows the
        // calculation until it is done and then says charging.
        Assert.Equal(JumpPrompt.Ordered, HudLayout.Prompt(true, true, false, JumpPhase.None, true, false));
        Assert.Equal(JumpPrompt.Aligning, HudLayout.Prompt(true, false, false, JumpPhase.Align, true, false));
        Assert.Equal(JumpPrompt.Working, HudLayout.Prompt(false, false, false, JumpPhase.Charge, true, false));
        Assert.Equal("CALCULATING", Encoding.UTF8.GetString(HudLayout.PromptWord(JumpPrompt.Working, false)));
        Assert.Equal("CHARGING", Encoding.UTF8.GetString(HudLayout.PromptWord(JumpPrompt.Working, true)));
        Assert.Equal(JumpPrompt.Transit, HudLayout.Prompt(false, false, false, JumpPhase.Tunnel, true, false));

        // The arrival belongs to the world, and with nothing plotted the strip is gone; a move order shows last.
        Assert.Equal(JumpPrompt.None, HudLayout.Prompt(false, false, false, JumpPhase.Arrive, false, true));
        Assert.Equal(JumpPrompt.None, HudLayout.Prompt(false, false, false, JumpPhase.None, false, false));
        Assert.Equal(JumpPrompt.Autopilot, HudLayout.Prompt(false, false, false, JumpPhase.None, false, true));
        Assert.Equal(JumpPrompt.OffCourse, HudLayout.Prompt(true, false, false, JumpPhase.None, false, true));
    }

    // The arrow drawn by DrawTargetIndicator and DrawNavTarget: a head 11 px deep, and the target's ring round it.
    private static float ArrowRing(float scale) => Px(11, scale) * 1.6f;

    /// <summary>The widest a target marker reaches from its point at a morph, as DrawTargetIndicator draws it.</summary>
    private static float TargetExtent(in HudCueRoom room, float morph, bool anchored, float scale)
    {
        var bracket = TargetCue.Size(Px(18, scale), Px(12, scale), MathF.Floor(room.Reach / 1.25f), anchored, morph) * 1.25f;
        var rock = TargetCue.Size(Px(14, scale), Px(10, scale), room.Reach - Px(5, scale), anchored, morph) + Px(5, scale);
        var extent = 0f;
        if (morph < 1f)
            extent = Math.Max(bracket, rock);
        if (morph > 0f)
            extent = Math.Max(extent, ArrowRing(scale));
        return extent;
    }

    /// <summary>True when a disc of <paramref name="radius"/> round a point touches none of the drawn pieces.</summary>
    private static bool Clear(in HudCueRoom room, float2 at, float radius) => room.Clear(at, radius);

    [Theory]
    [MemberData(nameof(Screens))]
    public void TheClearRoomLeavesTheMiddleToTheCuesAboveTheStripWhileItIsDrawn(float width, float height, float scale)
    {
        var display = new float2(width, height);
        foreach (var strip in new[] { false, true })
        {
            foreach (var target in new[] { false, true })
            {
                var room = new HudCueRoom(display, scale, Px(24 + 116, scale), target, strip);

                // A cue fits where its directions are measured from, and every arrow drawn straight out from there,
                // ring included, stays clear of every piece and on screen.
                Assert.True(Clear(room, room.Start, room.Reach + room.Gap - 0.5f), $"start {room.Start}");
                for (var a = 0; a < 64; a++)
                {
                    var angle = a * MathF.Tau / 64f;
                    var edge = room.Edge(room.Start, new float2(MathF.Cos(angle), MathF.Sin(angle)));
                    Assert.True(Clear(room, edge, ArrowRing(scale)), $"strip {strip} target {target} angle {a}: {edge}");
                }

                // The strip only pushes the cues up while it is drawn: with it hidden, an arrow straight down sits
                // over the console instead.
                var down = room.Edge(room.Start, new float2(0, 1));
                var limit = strip ? HudLayout.Strip(display, scale).Top : HudLayout.ConsoleTop(display, scale);
                Assert.True(down.Y + ArrowRing(scale) + room.Gap <= limit + 0.5f, $"down {down.Y} limit {limit}");
            }
        }
    }

    [Fact]
    public void ATargetBelowTheMiddleOfAShortScaledScreenIsMarkedAboveTheStripRingIncluded()
    {
        // 1655 x 688 at twice the scale: the strip runs from 396 to 476 (386 to 466 while the console had its end
        // brackets), barely 50 px under the middle of the view. The old ellipse put the arrow's point at 424 and
        // its ring down to 459, under the strip either way.
        var display = new float2(1655, 688);
        const float scale = 2f;
        var strip = HudLayout.Strip(display, scale);
        Assert.Equal(396f, strip.Top);
        Assert.True(424f + ArrowRing(scale) > strip.Top);

        var room = new HudCueRoom(display, scale, Px(24 + 116, scale), true, true);
        var cue = new TargetCue();
        var middle = new float2(827.5f, 344f);

        // In view in the middle, then dropping under the strip, then behind and below: every frame of the morph,
        // the marker is either on its target or wholly clear of every piece.
        var frames = 0;
        for (var y = 344f; frames < 90; frames++, y += 3f)
        {
            var behind = frames >= 60;
            var screen = new float2(middle.X, y);
            var morph = cue.Step(room, frames == 0, !behind, screen, new float2(0, 1), true, scale, 1f / 60f);
            if (frames == 0)
                Assert.False(cue.Out);
            if (cue.Out)
                Assert.True(Clear(room, cue.At, TargetExtent(room, morph, false, scale) - 0.01f), $"frame {frames} at {cue.At} morph {morph}");
            else
                Assert.True(room.Clear(screen, room.Pad(true)) || morph > 0f);
        }

        // Settled: an arrow pointing down, its ring clear of the strip by the gap.
        Assert.True(cue.Out);
        Assert.Equal(1f, cue.Morph);
        Assert.InRange(cue.Angle, MathF.PI * 0.5f - 0.01f, MathF.PI * 0.5f + 0.01f);
        Assert.True(cue.At.Y + ArrowRing(scale) + room.Gap <= strip.Top, $"ring bottom {cue.At.Y + ArrowRing(scale)}");
    }

    [Theory]
    [MemberData(nameof(Screens))]
    public void ATargetLeavingAndReturningKeepsItsMarkerOnItOrClearAtEveryMorph(float width, float height, float scale)
    {
        var display = new float2(width, height);
        var room = new HudCueRoom(display, scale, Px(24 + 116, scale), true, true);
        var cue = new TargetCue();
        var middle = room.Start;

        // Across the view to the right and off it, then back: the marker slides out and back with no jumps.
        float2 last = default;
        var limit = Px(60, scale);
        for (var frame = 0; frame < 160; frame++)
        {
            var x = frame < 80 ? frame * width / 60f : (160 - frame) * width / 60f;
            var screen = middle + new float2(x, 0);
            var inFront = screen.X < width * 1.5f;
            var morph = cue.Step(room, frame == 0, inFront, screen, new float2(1, 0), true, scale, 1f / 60f);
            if (cue.Out)
                Assert.True(Clear(room, cue.At, TargetExtent(room, morph, false, scale) - 0.01f), $"frame {frame} at {cue.At} morph {morph}");
            else if (morph == 0f)
                Assert.Equal(screen, cue.At);
            if (frame > 0 && cue.Out)
                Assert.True(MathF.Abs(cue.At.X - last.X) <= limit + width / 60f, $"jump at {frame}: {last} to {cue.At}");
            last = cue.At;
        }

        // Back on its target once it is in view again.
        Assert.False(cue.Out);
        Assert.Equal(0f, cue.Morph);
    }

    [Fact]
    public void ANavPointUnderTheStripIsObstructedAndItsArrowAndLabelSitClear()
    {
        var display = new float2(1655, 688);
        const float scale = 2f;
        var room = new HudCueRoom(display, scale, Px(24 + 116, scale), false, true);
        var strip = HudLayout.Strip(display, scale);

        // The projected point from the review passes the old 40 px edge margins yet lies under the strip.
        var projected = new float2(827.5f, 426f);
        Assert.True(strip.Contains(projected));
        Assert.False(room.Clear(projected, room.Pad(true)));

        // The arrow points at it from above the strip, and the old ellipse's clamped tip (392) is no longer used.
        var toward = projected - room.Start;
        var direction = toward / MathF.Sqrt(toward.X * toward.X + toward.Y * toward.Y);
        var at = room.Edge(room.Start, direction);
        Assert.True(direction.Y > 0.99f);
        Assert.True(Clear(room, at, ArrowRing(scale)));
        Assert.True(at.Y + Px(11, scale) < strip.Top);

        // The label goes beside the arrow, clear too, on whichever side has room.
        var label = Px(HudLayout.CueLabel, scale);
        Assert.True(room.Clear(at + new float2(label, 0), room.Pad(true)) || room.Clear(at - new float2(label, 0), room.Pad(true)));

        // A NAV point off the bottom of the view gives the same arrow.
        var off = room.Edge(room.Start, new float2(0, 1));
        Assert.Equal(at.Y, off.Y, 0.5f);
    }

    [Fact]
    public void TheNavCardIsDroppedWhenItsTieWouldCrossTheLeftColumn()
    {
        var display = new float2(1280, 720);
        const float scale = 2f;
        var room = new HudCueRoom(display, scale, 400f, false, false);
        var marker = new float2(120, 470);
        Assert.True(room.Clear(marker, room.Pad(true)));

        var size = new float2(Px(HudLayout.NavCardMin, scale), Px(HudLayout.NavCardHeight, scale));
        var free = HudLayout.NavFree(display, scale, false);
        Assert.False(HudLayout.PlaceNavCard(marker, size, free, room, scale, out var min));
        Assert.True(room.Crosses(marker, HudLayout.NavTieAnchor(marker, min, size)));

        // The same card with no column in the way is placed.
        var open = new HudCueRoom(display, scale, 0f, false, false);
        Assert.True(HudLayout.PlaceNavCard(new float2(700, 360), size, free, open, scale, out _));
    }

    [Theory]
    [MemberData(nameof(Screens))]
    public void TheLeftColumnKeepsItsWidthWhereItFitsAndStopsShortOfTheMiddle(float width, float height, float scale)
    {
        var display = new float2(width, height);
        var right = HudLayout.ColumnRight(display, scale);
        var middle = MathF.Round(width * 0.5f);

        // Short of the reticle with a gap, whatever the scale; the full width at the interface's own scale.
        Assert.True(right + Px(HudLayout.Gap, scale) <= middle - Px(HudLayout.CenterClear, scale), $"right {right}");
        if (scale == 1f)
            Assert.Equal(Px(HudLayout.ColumnMax, scale), HudLayout.ColumnWidth(display, scale));

        // The recent chat lines stop short of it too, and the quick buttons and the NAV card take the same edge.
        Assert.True(Px(24, scale) + HudLayout.ChatWidth(display, scale, Px(24, scale), Px(440, scale)) <= HudLayout.MiddleLeft(display, scale));
        Assert.Equal(right + Px(HudLayout.Gap, scale), HudLayout.NavFree(display, scale, false).Left);
    }

    [Fact]
    public void AtTwiceTheScaleOn1280By720ThePartyColumnNoLongerCrossesTheMiddle()
    {
        // The column used to run from 48 to 768 and cross the middle at 640, hiding a NAV marker at (640, 331).
        var display = new float2(1280, 720);
        Assert.True(Px(24, 2f) + Px(HudLayout.ColumnMax, 2f) > 640f);
        var right = HudLayout.ColumnRight(display, 2f);
        Assert.True(right < 640f - Px(HudLayout.CenterClear, 2f));
        Assert.True(HudLayout.ColumnWidth(display, 2f) >= Px(200, 2f), "the column keeps room for a name and its bars");

        // With the column reaching down to the strip, a marker at the mock-up's NAV point is still in view.
        var room = new HudCueRoom(display, 2f, HudLayout.Strip(display, 2f).Top, false, true);
        Assert.True(room.Clear(new float2(640f, 331.2f), room.Pad(true)));
    }

    [Fact]
    public void TheMapRouteShowsOnTheLayerItLiesOnAndIsCutToTheScreen()
    {
        // The route needs the hull and the point on the layer shown; the point alone shows on its own layer.
        Assert.True(MapRoute.Shows(0, 0, 0, out var line) && line);
        Assert.True(MapRoute.Shows(1, 0, 0, out line) && !line);
        Assert.False(MapRoute.Shows(0, 1, 0, out _));

        // Cut to the screen: a line from far off the map into it starts on the screen's edge.
        var screen = new HudRect(0f, 0f, 1280f, 720f);
        var from = new float2(-1e7f, 360f);
        var to = new float2(640f, 360f);
        Assert.True(MapRoute.Clip(ref from, ref to, screen));
        Assert.Equal(0f, from.X, 0.5f);
        Assert.Equal(640f, to.X);

        // However far the zoom throws the ends, the dashes are only those on screen; none for a line off it.
        var dashes = MapRoute.Dashes(new float2(-1e9f, -1e9f), new float2(1e9f, 1e9f), screen, 6f, 5f);
        Assert.InRange(dashes, 1, (int)MathF.Ceiling(MathF.Sqrt(1280f * 1280f + 720f * 720f) / 11f) + 1);
        Assert.Equal(0, MapRoute.Dashes(new float2(-100, -100), new float2(-10, -50), screen, 6f, 5f));
    }
}
