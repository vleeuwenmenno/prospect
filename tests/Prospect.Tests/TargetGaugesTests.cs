using Prospect.Client.Screens;
using Prospect.Client.Ui;
using Prospect.Engine.IO;
using Prospect.Game.World;
using Xunit;

namespace Prospect.Tests;

/// <summary>The target panel's compact bars: the geometry they share with the console, and what they say.</summary>
public sealed class TargetGaugesTests
{
    [Fact]
    public void TheConsoleAndTheTargetBarShareTheSameCellArithmetic()
    {
        // The console's 36-pixel row leans 10 pixels over a 12-pixel bar by the same ratio as at any size.
        var skew = BarGeometry.Skew(12f, ConsoleBars.ConsoleSlantRatio);
        Assert.Equal(12f * 10f / 36f, skew, 4);

        // Ten cells and nine 3-pixel gaps fill the track exactly once the lean is taken off.
        var width = BarGeometry.CellWidth(100f, 400f, skew, 3f, 10);
        Assert.Equal(300f - skew, width * 10 + 27f, 3);
        Assert.True(BarGeometry.CellWidth(0f, 10f, 4f, 3f, 10) <= 0f);
        Assert.Equal(0f, BarGeometry.CellWidth(0f, 100f, 0f, 1f, 0));
    }

    [Theory]
    [InlineData(0f, 0, 10, 0f)]
    [InlineData(0.55f, 4, 10, 1f)]
    [InlineData(0.55f, 5, 10, 0.5f)]
    [InlineData(0.55f, 6, 10, 0f)]
    [InlineData(1f, 9, 10, 1f)]
    [InlineData(2f, 9, 10, 1f)]
    [InlineData(-1f, 0, 10, 0f)]
    public void ASegmentIsLitByHowFarTheFractionReachesIt(float fraction, int index, int count, float lit) =>
        Assert.Equal(lit, BarGeometry.Fill(fraction, index, count), 4);

    [Theory]
    [InlineData(0f, 0)]
    [InlineData(0.5f, 5)]
    [InlineData(0.99f, 9)]
    [InlineData(1f, 10)]
    [InlineData(float.NaN, 0)]
    public void WholeLitSegmentsCountFromTheFraction(float fraction, int expected) => Assert.Equal(expected, BarGeometry.Lit(fraction, 10));

    [Fact]
    public void TheFractionsAddUpToWhatTheBarShows()
    {
        // Every cell's share of a fraction sums back to that fraction of the whole bar.
        for (var f = 0f; f <= 1.0001f; f += 0.037f)
        {
            var sum = 0f;
            for (var i = 0; i < 8; i++)
                sum += BarGeometry.Fill(f, i, 8);
            Assert.Equal(Math.Min(f, 1f) * 8f, sum, 3);
        }
    }

    [Theory]
    [InlineData(1f)]
    [InlineData(2f)]
    public void ARowFitsItsWidthAndTheBarNeverGoesNegative(float scale)
    {
        var look = TargetLook.Current;
        foreach (var width in new[] { 400f, 292f * scale, 120f, 10f })
        {
            var row = TargetGauges.Place(look, scale, 50f, 20f, width, 90f);
            Assert.True(row.Track.Right >= row.Track.Left);
            Assert.True(row.Track.Left >= 50f + look.IconSize * scale - 1f);
            Assert.True(row.ReadingRight <= 50f + width + 0.01f);
            Assert.True(row.Track.Top >= 20f && row.Track.Bottom <= 20f + row.Height + 0.01f);
            Assert.True(row.IconY >= 20f && row.IconY + MathF.Round(look.IconSize * scale) <= 20f + row.Height + 0.01f);
        }

        // A full-size panel keeps readable cells: 10 segments of at least 4 pixels each.
        var full = TargetGauges.Place(look, scale, 0f, 0f, MathF.Round(292f * scale), 90f * scale);
        var cell = BarGeometry.CellWidth(full.Track.Left, full.Track.Right, BarGeometry.Skew(full.Track.Bottom - full.Track.Top, look.Slant), look.Gap * scale, look.Segments);
        Assert.True(cell >= 4f * scale, $"cell {cell}");

        // The second row clears the first.
        Assert.True(TargetGauges.Next(look, scale, 0f) >= full.Height);
    }

    [Fact]
    public void TheShieldBarSaysWhatTheSnapshotGives()
    {
        var shielded = new FlightParams { ShieldCapacity = 1000f, ShieldRestartFraction = 0.25f };
        Assert.Equal(ShieldReading.None, TargetGauges.Shield(default, false));
        Assert.Equal(ShieldReading.None, TargetGauges.Shield(default, true));
        Assert.Equal(ShieldReading.None, TargetGauges.Shield(new FlightParams { ShieldCapacity = float.NaN }, false));
        Assert.Equal(ShieldReading.Online, TargetGauges.Shield(shielded, false));
        Assert.Equal(ShieldReading.Restarting, TargetGauges.Shield(shielded, true));
        shielded.ShieldRestartFraction = 0f;
        Assert.Equal(ShieldReading.Offline, TargetGauges.Shield(shielded, true));
    }

    [Fact]
    public void TheTargetBarsUseTheConsolesColours()
    {
        Assert.Equal(Theme.Bad, FlightConsole.HullColor(FlightConsole.HullLow - 0.01f));
        Assert.Equal(Theme.Good, FlightConsole.HullColor(FlightConsole.HullLow));
        Assert.Equal(Theme.Info, FlightConsole.ShieldColor(true, false));
        Assert.Equal(Theme.Bad, FlightConsole.ShieldColor(true, true));
        Assert.Equal(Theme.LineStrong, FlightConsole.ShieldColor(false, false));
    }

    [Fact]
    public void TheBarGeometryTakesNoMemory()
    {
        var look = TargetLook.Current;
        var probe = new AllocationProbe();
        var sink = 0f;
        while (probe.Measuring())
        {
            for (var i = 0; i < 200; i++)
            {
                var row = TargetGauges.Place(look, 1.5f, 10f, 10f + i, 300f, 90f);
                sink += BarGeometry.CellWidth(row.Track.Left, row.Track.Right, BarGeometry.Skew(8f, look.Slant), 2f, look.Segments);
                sink += BarGeometry.Fill(i / 200f, i % look.Segments, look.Segments);
                sink += (int)TargetGauges.Shield(new FlightParams { ShieldCapacity = i }, i % 2 == 0);
            }
        }

        Assert.True(sink > 0f);
        Assert.Equal(0, probe.Bytes);
    }

    [Fact]
    public void TheTargetLookLoadsAndRefusesWhatIsOutOfRange()
    {
        var path = AppPaths.Content("visuals", "target.json");
        var shipped = TargetLook.Load(path);
        Assert.Equal(shipped.Segments, TargetLook.Current.Segments);

        var text = File.ReadAllText(path);
        var scratch = Path.Combine(Path.GetTempPath(), "target-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            foreach (var (from, to) in new[]
            {
                ("\"segments\": 10", "\"segments\": 1"),
                ("\"barHeight\": 8", "\"barHeight\": 20"),
                ("\"slant\": 0.28", "\"slant\": 2"),
            })
            {
                Assert.Contains(from, text);
                File.WriteAllText(scratch, text.Replace(from, to));
                Assert.Throws<InvalidDataException>(() => TargetLook.Load(scratch));
            }
        }
        finally
        {
            File.Delete(scratch);
        }
    }
}
