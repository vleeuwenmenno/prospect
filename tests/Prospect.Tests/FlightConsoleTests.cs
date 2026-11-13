using Brutal.Numerics;
using Prospect.Client.Screens;
using Prospect.Client.Ui;
using Prospect.Game.World;
using Xunit;

namespace Prospect.Tests;

/// <summary>The pilot console's placement on small and scaled windows, and the arithmetic behind its readings.</summary>
public sealed class FlightConsoleTests
{
    [Theory]
    [InlineData(1280f, 720f, 1f, false)]
    [InlineData(1655f, 688f, 1f, false)]
    [InlineData(1920f, 1080f, 1f, false)]
    [InlineData(1280f, 720f, 1.5f, true)]
    [InlineData(1280f, 720f, 2f, true)]
    [InlineData(1655f, 688f, 2f, true)]
    [InlineData(3840f, 2160f, 2f, false)]
    public void TheConsoleFitsTheScreenAndKeepsEveryBarReadable(float width, float height, float scale, bool compact)
    {
        float Px(float design) => MathF.Round(design * scale);
        var layout = FlightConsole.Layout(new float2(width, height), scale);

        Assert.Equal(compact, layout.Compact);

        // On screen, inside the side margins, and resting on the bottom margin.
        Assert.True(layout.LeftX >= Px(FlightConsole.Side));
        Assert.True(layout.Right <= width - Px(FlightConsole.Side));
        Assert.Equal(height - Px(FlightConsole.Bottom), layout.Top + layout.Height);

        // The two groups clear the speed readout between them, and mirror each other about it.
        var half = Px(FlightConsole.Center) * 0.5f;
        Assert.True(layout.LeftX + layout.GroupWidth <= layout.CenterX - half);
        Assert.True(layout.RightX >= layout.CenterX + half);
        Assert.Equal(layout.CenterX - layout.LeftX, layout.Right - layout.CenterX);

        // A full row has room for its bar beside label and reading; a compact row stacks them, so its bar takes
        // the whole row and still shows every segment at a readable width.
        var inner = layout.GroupWidth - Px(FlightConsole.Slant) - 2 * Px(FlightConsole.Pad);
        var track = compact ? inner : inner - Px(FlightConsole.LabelColumn) - Px(FlightConsole.ReadingColumn) - 2 * Px(FlightConsole.ColumnGap);
        Assert.True(track >= (compact ? Px(150) : Px(FlightConsole.TrackMin)), $"track {track}");
        Assert.True(track / FlightConsole.Segments >= Px(10));
    }

    [Fact]
    public void TheConsoleNeverGrowsPastItsWidestAndLeavesTheRestToTheWorld()
    {
        var layout = FlightConsole.Layout(new float2(3840f, 1080f), 1f);
        Assert.Equal(FlightConsole.GroupMax, layout.GroupWidth);
        Assert.False(layout.Compact);
    }

    [Theory]
    [InlineData(750f, 1500f, 0.5f)]
    [InlineData(1600f, 1500f, 1f)]
    [InlineData(-20f, 1500f, 0f)]
    [InlineData(10f, 0f, 0f)]
    [InlineData(10f, -5f, 0f)]
    [InlineData(float.NaN, 1500f, 0f)]
    [InlineData(10f, float.NaN, 0f)]
    [InlineData(float.PositiveInfinity, 1500f, 0f)]
    [InlineData(10f, float.PositiveInfinity, 0f)]
    public void FractionsStayOnTheBar(float value, float capacity, float expected) =>
        Assert.Equal(expected, FlightConsole.Fraction(value, capacity), 4);

    [Fact]
    public void ReadingsArePrintedWithinTheirCapacityAndInThousandsOnlyForHugeHulls()
    {
        Assert.Equal(2700f, FlightConsole.Shown(2900f, 2700f));
        Assert.Equal(0f, FlightConsole.Shown(-3f, 2700f));
        Assert.Equal(0f, FlightConsole.Shown(float.NaN, 2700f));
        Assert.Equal(0f, FlightConsole.Shown(5f, float.NaN));

        // Five digits print as they are; past that (a box hull of 150,000 blocks has 60,000,000 points) they
        // print in thousands, so a reading never needs more than six characters a side.
        Assert.False(FlightConsole.Thousands(5780f));
        Assert.False(FlightConsole.Thousands(99_999f));
        Assert.True(FlightConsole.Thousands(100_000f));
        Assert.True(FlightConsole.Thousands(60_000_000f));
        Assert.True((60_000_000f / 1000f).ToString("0").Length <= 6);
    }

    private static FlightParams Pod() => new()
    {
        PowerGenerated = 6f,
        IdlePower = 0.7f,
        ThrustPower = 4.1f,
        ShieldPower = 1.5f,
        ShieldRestartPower = 3f,
        BoostPowerMultiplier = 4f,
        FtlPower = 2f,
    };

    [Fact]
    public void TheFlowStripMeasuresNetRateAgainstWhatThisHullCanDrawOrGain()
    {
        var pod = Pod();
        // The shield's part is its restart draw, twice its 1.5 MW top-up, the most it can ever ask for.
        var peakDraw = 0.7f + 4.1f * 4f + 1.5f * 2f + 2f - 6f;
        var peakGain = 6f - 0.7f;

        Assert.Equal(0f, FlightConsole.Flow(0f, pod));
        Assert.Equal(0f, FlightConsole.Flow(0.04f, pod));
        Assert.Equal(0f, FlightConsole.Flow(float.NaN, pod));
        Assert.Equal(-0.5f, FlightConsole.Flow(-peakDraw * 0.5f, pod), 4);
        Assert.Equal(0.5f, FlightConsole.Flow(peakGain * 0.5f, pod), 4);

        // The measured rate is smoothed and can overshoot; the strip stops at its end.
        Assert.Equal(-1f, FlightConsole.Flow(-1000f, pod));
        Assert.Equal(1f, FlightConsole.Flow(1000f, pod));

        // A hull with no generator surplus, or no way to run the battery down, still shows which way it goes.
        Assert.Equal(1f, FlightConsole.Flow(0.5f, new FlightParams { PowerGenerated = 1f, IdlePower = 1f }));
        Assert.Equal(-1f, FlightConsole.Flow(-0.5f, new FlightParams { PowerGenerated = 50f }));
    }

    [Fact]
    public void ATrailFollowsGainsAtOnceAndLetsALossShowBeforeCatchingUp()
    {
        // A gain moves the trail with it.
        Assert.Equal(0.8f, FlightConsole.Trail(0.5f, 0.8f, 1f / 60f));

        // A hit of 40 percent stays mostly visible for a moment, then is gone within a couple of seconds.
        var trail = 1f;
        for (var i = 0; i < 3; i++)
            trail = FlightConsole.Trail(trail, 0.6f, 1f / 60f);
        Assert.True(trail > 0.9f);
        for (var i = 0; i < 120; i++)
            trail = FlightConsole.Trail(trail, 0.6f, 1f / 60f);
        Assert.Equal(0.6f, trail);

        // A steady drain keeps a short trail at the edge, no longer than the drain over a third of a second.
        var value = 1f;
        trail = 1f;
        for (var i = 0; i < 180; i++)
        {
            value -= 0.18f / 60f;
            trail = FlightConsole.Trail(trail, value, 1f / 60f);
        }

        Assert.InRange(trail - value, 0.03f, 0.18f / FlightConsole.TrailRate + 0.005f);

        // A broken trail starts again at the value.
        Assert.Equal(0.4f, FlightConsole.Trail(float.NaN, 0.4f, 1f / 60f));
    }

    [Fact]
    public void HeatRunsFromCoolCyanThroughAmberToRed()
    {
        Assert.Equal(Theme.Info, FlightConsole.HeatColor(0f));
        Assert.Equal(Theme.Info, FlightConsole.HeatColor(0.45f));
        Assert.Equal(Theme.Accent, FlightConsole.HeatColor(0.55f));
        Assert.Equal(FlightConsole.DrawColor, FlightConsole.HeatColor(0.85f));
        Assert.Equal(Theme.Bad, FlightConsole.HeatColor(1f));

        // Hotter is never greener or less red, all the way up.
        var previous = FlightConsole.HeatColor(0f);
        for (var heat = 0.05f; heat <= 1f; heat += 0.05f)
        {
            var color = FlightConsole.HeatColor(heat);
            Assert.True((color >> 16 & 0xFF) >= (previous >> 16 & 0xFF));
            Assert.True((color >> 8 & 0xFF) <= (previous >> 8 & 0xFF));
            previous = color;
        }
    }

    [Theory]
    [InlineData(1280f, 720f, 1f)]
    [InlineData(1655f, 688f, 1f)]
    [InlineData(1920f, 1080f, 1f)]
    [InlineData(1280f, 720f, 2f)]
    [InlineData(1655f, 688f, 2f)]
    [InlineData(1920f, 1080f, 2f)]
    public void ARowsDetailsSitAboveItsGroupAndStayOnScreen(float width, float height, float scale)
    {
        var display = new float2(width, height);
        var layout = FlightConsole.Layout(display, scale);
        var side = MathF.Round(FlightConsole.Side * scale);
        for (var bar = ConsoleBar.Shield; bar <= ConsoleBar.Heat; bar++)
        {
            // A modest box starts at the row's outer edge, above the console.
            var row = FlightConsole.BarRect(layout, bar);
            var rect = FlightConsole.TipRect(layout, bar, new float2(MathF.Round(200 * scale), MathF.Round(120 * scale)), display, scale);
            if (bar is ConsoleBar.Shield or ConsoleBar.Hull)
                Assert.Equal(row.Left, rect.Left);
            else
                Assert.Equal(row.Right, rect.Right);
            Assert.True(rect.Bottom <= layout.Top);

            // One wider than the screen is cut to its margins.
            var wide = FlightConsole.TipRect(layout, bar, new float2(width * 2, MathF.Round(120 * scale)), display, scale);
            Assert.Equal(side, wide.Left);
            Assert.Equal(width - side, wide.Right);
        }
    }

    [Fact]
    public void OnlyAShownPointerOffEveryWindowFindsARow()
    {
        var layout = FlightConsole.Layout(new float2(1280f, 720f), 1f);
        var shield = FlightConsole.BarRect(layout, ConsoleBar.Shield);
        var on = new float2(shield.Left + 40, shield.Top + 10);
        Assert.Equal(ConsoleBar.Shield, FlightConsole.Hovered(layout, on, true, false));
        Assert.Equal(ConsoleBar.None, FlightConsole.Hovered(layout, on, false, false));
        Assert.Equal(ConsoleBar.None, FlightConsole.Hovered(layout, on, true, true));
        Assert.Equal(ConsoleBar.None, FlightConsole.Hovered(layout, new float2(-float.MaxValue, -float.MaxValue), true, false));
        var heat = FlightConsole.BarRect(layout, ConsoleBar.Heat);
        Assert.Equal(ConsoleBar.Heat, FlightConsole.Hovered(layout, new float2(heat.Right - 2, heat.Bottom - 2), true, false));
        Assert.Equal(ConsoleBar.None, FlightConsole.Hovered(layout, new float2(layout.CenterX, layout.Top + 10), true, false));
    }

    [Theory]
    [InlineData(3000f, 0.4f, 600f, 0.4f, 0.5f)]
    [InlineData(3000f, 1f, 1500f, 1f, 0.5f)]
    [InlineData(3000f, 0.25f, 900f, 0.25f, 1f)]
    [InlineData(0f, 0.25f, 0f, 0f, 0f)]
    [InlineData(3000f, 0f, 600f, 0f, 0f)]
    public void TheRestartMarkFollowsTheHullsOwnRule(float capacity, float fraction, float stored, float mark, float progress)
    {
        var flight = new FlightParams { ShieldCapacity = capacity, ShieldRestartFraction = fraction };
        Assert.Equal(mark, FlightConsole.RestartMark(flight));
        Assert.Equal(progress, FlightConsole.RestartProgress(stored, flight), 5);
    }
}
