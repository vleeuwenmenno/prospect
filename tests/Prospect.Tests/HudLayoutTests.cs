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
    public void TheConsoleBracketsAndTheStripStayOnScreenAndClearOfEachOther(float width, float height, float scale)
    {
        var display = new float2(width, height);
        var console = FlightConsole.Layout(display, scale);

        // The brackets close the groups inside the screen.
        Assert.True(console.LeftX - Px(HudLayout.BracketOut, scale) > 0f);
        Assert.True(console.Right + Px(HudLayout.BracketOut, scale) < width);
        Assert.Equal(console.Top - Px(HudLayout.BracketRise, scale), HudLayout.ConsoleTop(display, scale));

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
        var right = HudLayout.ColumnRight(scale);

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
                Assert.True(free.Left >= HudLayout.ColumnRight(scale) + Px(HudLayout.Gap, scale));
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
                        if (!HudLayout.PlaceNavCard(marker, size, free, scale, out var min))
                            continue;
                        var max = min + size;
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
}
