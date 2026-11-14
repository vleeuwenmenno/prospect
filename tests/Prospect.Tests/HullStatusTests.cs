using Brutal.Numerics;
using Prospect.Client.Screens;
using Prospect.Game.World;
using Xunit;

namespace Prospect.Tests;

public sealed class HullStatusTests
{
    private static readonly string ShippedPath = Path.Combine(AppContext.BaseDirectory, "content", "visuals", "status.json");

    [Fact]
    public void FlagsMapToIconsLabelsAndPulse()
    {
        Assert.Equal(0, HullStatus.Count(HullEnvironment.None));

        var cloud = HullEnvironment.InLightningCloud;
        Assert.Equal(1, HullStatus.Count(cloud));
        Assert.Equal(StatusIcon.Lightning, HullStatus.Icon(cloud, 0));
        Assert.False(HullStatus.Bursting(cloud, 0));
        Assert.Equal("LIGHTNING CLOUD", System.Text.Encoding.UTF8.GetString(HullStatus.Label(cloud, 0)));

        var burst = HullEnvironment.InLightningCloud | HullEnvironment.SensorsDisrupted;
        Assert.Equal(1, HullStatus.Count(burst));
        Assert.True(HullStatus.Bursting(burst, 0));
        Assert.Equal("SENSORS DISRUPTED", System.Text.Encoding.UTF8.GetString(HullStatus.Label(burst, 0)));

        var cold = HullEnvironment.InColdCloud;
        Assert.Equal(1, HullStatus.Count(cold));
        Assert.Equal(StatusIcon.Cold, HullStatus.Icon(cold, 0));
        Assert.False(HullStatus.Bursting(cold, 0));
        Assert.Equal("COLD CLOUD", System.Text.Encoding.UTF8.GetString(HullStatus.Label(cold, 0)));

        // Both at once (the server never sends it, but a list must not lose either): lightning first, then cold.
        var both = cold | burst;
        Assert.Equal(2, HullStatus.Count(both));
        Assert.Equal(StatusIcon.Lightning, HullStatus.Icon(both, 0));
        Assert.Equal(StatusIcon.Cold, HullStatus.Icon(both, 1));

        var look = StatusTuning.Load(ShippedPath);
        // Steady in a cloud, pulsing only during a burst, and a burst never drops below its minimum.
        Assert.Equal(look.CloudAlpha, HullStatus.Alpha(look, cloud, 0, 0.3f));
        Assert.Equal(look.ColdAlpha, HullStatus.Alpha(look, cold, 0, 0.3f));
        float low = 2, high = 0;
        for (var t = 0f; t < 1f / look.BurstPulseHz; t += 0.01f)
        {
            var alpha = HullStatus.Alpha(look, burst, 0, t);
            low = Math.Min(low, alpha);
            high = Math.Max(high, alpha);
        }

        Assert.InRange(low, look.BurstMinAlpha - 0.001f, look.BurstMinAlpha + 0.02f);
        Assert.InRange(high, 0.98f, 1f);
    }

    [Fact]
    public void HitFollowsTheIconsAndTheFleetRowGrowsOnlyForAStatus()
    {
        var both = HullEnvironment.InLightningCloud | HullEnvironment.InColdCloud;
        Assert.Equal(0, HullStatus.Hit(both, 100f, 50f, 10f, 4f, new float2(105f, 50f)));
        Assert.Equal(1, HullStatus.Hit(both, 100f, 50f, 10f, 4f, new float2(116f, 52f)));
        Assert.Equal(-1, HullStatus.Hit(both, 100f, 50f, 10f, 4f, new float2(112f, 50f)));
        Assert.Equal(-1, HullStatus.Hit(both, 100f, 50f, 10f, 4f, new float2(105f, 70f)));
        Assert.Equal(-1, HullStatus.Hit(HullEnvironment.None, 100f, 50f, 10f, 4f, new float2(105f, 50f)));
        Assert.Equal(24f, HullStatus.IconsWidth(both, 10f, 4f));
        Assert.Equal(44f, HullStatus.FleetRowHeight(HullEnvironment.None, 44f, 16f));
        Assert.Equal(60f, HullStatus.FleetRowHeight(HullEnvironment.InColdCloud, 44f, 16f));
    }

    [Fact]
    public void MappingTakesNoMemory()
    {
        var look = StatusTuning.Load(ShippedPath);
        var burst = HullEnvironment.InLightningCloud | HullEnvironment.SensorsDisrupted;
        var sum = 0f;
        var probe = new AllocationProbe();
        while (probe.Measuring())
        {
            sum += HullStatus.Count(burst) + HullStatus.Alpha(look, burst, 0, 1.3f) + HullStatus.Label(burst, 0).Length
                + HullStatus.Meaning(burst, 0, 1).Length + HullStatus.Hit(burst, 0f, 0f, 10f, 4f, new float2(3f, 0f)) + HullStatus.Rgb(look, StatusIcon.Cold);
        }

        Assert.Equal(0, probe.Bytes);
        Assert.NotEqual(0f, sum);
    }

    [Fact]
    public void TheShippedTuningLoadsAndBadFiguresAreRefused()
    {
        var look = StatusTuning.Load(ShippedPath);
        Assert.True(look.IconSize > 0);
        var folder = Directory.CreateTempSubdirectory();
        try
        {
            var text = File.ReadAllText(ShippedPath);
            foreach (var (from, to) in new[] { ("\"coldColor\": \"BFE3F7\"", "\"coldColor\": \"ice\""), ("\"burstMinAlpha\": 0.5", "\"burstMinAlpha\": 2"), ("\"iconSize\": 11", "\"iconSize\": 1") })
            {
                var bad = Path.Combine(folder.FullName, "status.json");
                File.WriteAllText(bad, text.Replace(from, to));
                Assert.Throws<InvalidDataException>(() => StatusTuning.Load(bad));
            }
        }
        finally
        {
            folder.Delete(true);
        }
    }
}
