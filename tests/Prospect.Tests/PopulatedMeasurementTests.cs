using Prospect.Client.Diagnostics;
using Prospect.Game.Server;
using Xunit;

namespace Prospect.Tests;

public sealed class PopulatedMeasurementTests
{
    private static PopulatedMeasurement.Sample Effect(float seconds, bool drawn = true) => new()
    {
        Seconds = seconds,
        Ms = 50,
        Bytes = 17,
        Hulls = 30,
        Visible = 30,
        Dents = 464,
        Draws = 29,
        Sparks = 72,
        Upload = 8000000,
        Fallbacks = 2,
        Rocks = 1000,
        VisibleRocks = 500,
        Drawn = drawn,
    };

    [Fact]
    public void SkippedFramePreservesTimeAndAllocationButCannotSupplyEffectEvidence()
    {
        var measurement = new PopulatedMeasurement(4);
        measurement.Record(Effect(1) with { Dents = 0, Draws = 0, Sparks = 0 });
        measurement.Record(Effect(15, false));
        var skipped = measurement.Samples[1];
        Assert.Equal(50, skipped.Ms);
        Assert.Equal(17, skipped.Bytes);
        Assert.False(skipped.Drawn);
        Assert.Equal(0, skipped.Visible + skipped.Dents + skipped.Draws + skipped.Sparks + skipped.Upload + skipped.Fallbacks + skipped.VisibleRocks);
        Assert.False(measurement.Verify(new VisualBenchmarkScene(30, "dents", "near"), 0));
        Assert.False(measurement.Verify(new VisualBenchmarkScene(30, "impacts", "near"), 0));
        measurement.Record(Effect(16));
        Assert.True(measurement.Verify(new VisualBenchmarkScene(30, "dents", "near"), 0));
    }

    [Fact]
    public void FixedBufferOverflowRetainsRealEndAndFailsVerificationWithoutAllocating()
    {
        // The overflow happens once to a measurement, so a run in doubt starts again with a fresh one.
        var allocation = new AllocationProbe();
        var measurement = new PopulatedMeasurement(2);
        while (allocation.Run())
        {
            measurement = new PopulatedMeasurement(2);
            measurement.Record(Effect(1));
            measurement.Record(Effect(2));
            Assert.True(measurement.Verify(new VisualBenchmarkScene(30, "dents", "near"), 0));
            allocation.Start();
            measurement.Record(Effect(80));
            measurement.Record(Effect(120));
            allocation.Stop();
        }
        Assert.Equal(0, allocation.Bytes);
        Assert.Equal(2, measurement.Samples.Length);
        Assert.Equal(2, measurement.Overflow);
        Assert.Equal(120, measurement.EndSeconds);
        Assert.False(measurement.Complete);
        Assert.False(measurement.Verify(new VisualBenchmarkScene(30, "dents", "near"), 0));
    }
}
