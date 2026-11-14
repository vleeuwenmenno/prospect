using Prospect.Client.Render;
using Prospect.Client.Settings;
using Prospect.Client.World;
using Prospect.Engine.IO;
using Xunit;

namespace Prospect.Tests;

/// <summary>
/// The gas quality preference without a GPU: how it is kept, what the preset table refuses, and the font-free rules
/// that pick the march's resolution, steps and noise. The shaders read the same figures from the frame constants.
/// </summary>
public sealed class GasQualityTests
{
    private static readonly GasTiers Tiers = GasTiers.Load();

    [Fact]
    public void NewInstallsTakeMedium()
    {
        Assert.Equal(GasQuality.Medium, new ClientSettings().GasQuality);
        var path = Path.Combine(Path.GetTempPath(), "prospect-settings-" + Guid.NewGuid().ToString("N") + ".json");
        Assert.Equal(GasQuality.Medium, ClientSettings.Load(path).GasQuality);
    }

    [Theory]
    [InlineData(GasQuality.Low)]
    [InlineData(GasQuality.Medium)]
    [InlineData(GasQuality.High)]
    [InlineData(GasQuality.Ultra)]
    public void EveryQualitySavesAndLoadsBack(GasQuality quality)
    {
        var path = Path.Combine(Path.GetTempPath(), "prospect-settings-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            new ClientSettings { GasQuality = quality }.Save(path);
            Assert.Contains($"\"GasQuality\": \"{quality}\"", File.ReadAllText(path));
            Assert.Equal(quality, ClientSettings.Load(path).GasQuality);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("{ \"GasQuality\": 99 }")]
    [InlineData("{ \"GasQuality\": -1 }")]
    [InlineData("{ \"GasQuality\": \"Gigantic\" }")]
    public void ANumberOrNameOutsideThePresetsFallsBackToMedium(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), "prospect-settings-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path, json);
            Assert.Equal(GasQuality.Medium, ClientSettings.Load(path).GasQuality);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void TheShippedPresetsAreWhatTheSettingPromises()
    {
        var low = Tiers.Quality.For(GasQuality.Low);
        var medium = Tiers.Quality.For(GasQuality.Medium);
        var high = Tiers.Quality.For(GasQuality.High);
        var ultra = Tiers.Quality.For(GasQuality.Ultra);
        Assert.Equal(4, low.Resolution);
        Assert.True(low.Steps < 1 && low.NoiseTo > low.NoiseFrom);
        // Medium is what the game ran before there was a setting.
        Assert.Equal(2, medium.Resolution);
        Assert.Equal(1f, medium.Steps);
        Assert.Equal(0f, medium.NoiseTo);
        Assert.Equal(1, high.Resolution);
        Assert.Equal(1f, high.Steps);
        Assert.Equal(1, ultra.Resolution);
        Assert.True(ultra.Steps > 1);
        Assert.Equal(0f, ultra.NoiseTo);
        Assert.False(GasQualityRules.Marches(high));
        Assert.False(GasQualityRules.Marches(ultra));
        Assert.True(GasQualityRules.Marches(medium) && GasQualityRules.Marches(low));
    }

    [Fact]
    public void ThePresetsSendTheirFiguresToTheShaderInOrder()
    {
        var low = Tiers.Quality.For(GasQuality.Low).ToConstants();
        Assert.Equal(4f, low.X);
        Assert.Equal(Tiers.Quality.Low!.Steps, low.Y);
        Assert.Equal(Tiers.Quality.Low.NoiseFrom, low.Z);
        Assert.Equal(Tiers.Quality.Low.NoiseTo, low.W);
        // A fixture runs the exact march whatever the player chose: every pixel, every step, all of the noise.
        Assert.Equal(1f, GasQualityFigures.Exact.X);
        Assert.Equal(1f, GasQualityFigures.Exact.Y);
        Assert.Equal(0f, GasQualityFigures.Exact.Z);
        Assert.Equal(0f, GasQualityFigures.Exact.W);
    }

    [Theory]
    [InlineData("low", GasQuality.Low)]
    [InlineData("Medium", GasQuality.Medium)]
    [InlineData("HIGH", GasQuality.High)]
    [InlineData("ultra", GasQuality.Ultra)]
    public void TheFlagNamesEachPreset(string text, GasQuality expected)
    {
        Assert.True(GasQualityTable.TryParse(text, out var quality));
        Assert.Equal(expected, quality);
    }

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("2")]
    [InlineData("extreme")]
    [InlineData(null)]
    public void TheFlagRefusesAnythingElse(string? text) => Assert.False(GasQualityTable.TryParse(text, out _));

    [Fact]
    public void PresetFilesOutOfRangeAreRefused()
    {
        var shipped = File.ReadAllText(AppPaths.Content("visuals", "gas.json"));
        var path = Path.Combine(Path.GetTempPath(), "prospect-gas-quality-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path, shipped);
            Assert.Equal(4, GasTiers.Load(path).Quality.Low!.Resolution);
            foreach (var (old, bad) in new[]
            {
                // A resolution that is not 1, 2 or 4.
                ("\"low\":    { \"resolution\": 4", "\"low\":    { \"resolution\": 3"),
                ("\"high\":   { \"resolution\": 1", "\"high\":   { \"resolution\": 0"),
                // Steps out of range, either way.
                ("\"steps\": 0.6", "\"steps\": 0.1"),
                ("\"steps\": 1.5", "\"steps\": 2.5"),
                // The noise range: one end alone, reversed, or too far.
                ("\"noiseFrom\": 0.5, \"noiseTo\": 1.1", "\"noiseFrom\": 0.5, \"noiseTo\": 0"),
                ("\"noiseFrom\": 0.5, \"noiseTo\": 1.1", "\"noiseFrom\": 1.2, \"noiseTo\": 1.1"),
                ("\"noiseFrom\": 0.5, \"noiseTo\": 1.1", "\"noiseFrom\": 0.5, \"noiseTo\": 9"),
                // Presets that get cheaper going up, or a Medium that is not the old default.
                ("\"high\":   { \"resolution\": 1, \"steps\": 1.0", "\"high\":   { \"resolution\": 1, \"steps\": 0.9"),
                ("\"ultra\":  { \"resolution\": 1", "\"ultra\":  { \"resolution\": 2"),
                ("\"medium\": { \"resolution\": 2, \"steps\": 1.0", "\"medium\": { \"resolution\": 2, \"steps\": 0.9"),
                ("\"medium\": { \"resolution\": 2", "\"medium\": { \"resolution\": 4"),
            })
            {
                Assert.Contains(old, shipped);
                File.WriteAllText(path, shipped.Replace(old, bad));
                Assert.Throws<InvalidDataException>(() => GasTiers.Load(path));
            }

            // A missing preset is refused, not defaulted: the setting would otherwise offer a choice that does nothing.
            var start = shipped.LastIndexOf(',', shipped.IndexOf("\"ultra\":", StringComparison.Ordinal));
            var end = shipped.IndexOf('}', start) + 1;
            File.WriteAllText(path, shipped.Remove(start, end - start));
            var refused = Assert.Throws<InvalidDataException>(() => GasTiers.Load(path));
            Assert.Contains("quality.ultra is missing", refused.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void TheMarchTargetFollowsTheResolutionAndRoundsUp()
    {
        Assert.Equal((960, 540), GasQualityRules.TargetSize(1920, 1080, 2));
        Assert.Equal((480, 270), GasQualityRules.TargetSize(1920, 1080, 4));
        // The last odd pixel still has a texel to read.
        Assert.Equal((2549, 696), GasQualityRules.TargetSize(5097, 1391, 2));
        Assert.Equal((1275, 348), GasQualityRules.TargetSize(5097, 1391, 4));
        Assert.Equal((1921, 1081), GasQualityRules.TargetSize(1921, 1081, 1));
        // The target is allocated for half resolution, so a quarter always fits inside it.
        var half = GasQualityRules.TargetSize(5097, 1391, 2);
        var quarter = GasQualityRules.TargetSize(5097, 1391, 4);
        Assert.True(quarter.Width <= half.Width && quarter.Height <= half.Height);
    }

    [Fact]
    public void StepsFollowThePresetAndStayInsideTheShadersLoopsAndFloor()
    {
        const float samples = 48;
        var low = GasQualityRules.StepBudget(samples, Tiers.Quality.For(GasQuality.Low).Steps);
        var medium = GasQualityRules.StepBudget(samples, Tiers.Quality.For(GasQuality.Medium).Steps);
        var high = GasQualityRules.StepBudget(samples, Tiers.Quality.For(GasQuality.High).Steps);
        var ultra = GasQualityRules.StepBudget(samples, Tiers.Quality.For(GasQuality.Ultra).Steps);
        // Medium and High take the content's count exactly, as the game always did.
        Assert.Equal(48, medium);
        Assert.Equal(48, high);
        Assert.True(low < medium && medium < ultra, $"{low} {medium} {ultra}");
        Assert.Equal(16, GasQualityRules.StepBudget(20, 0.25f));
        Assert.Equal(GasQualityRules.MaxSteps, GasQualityRules.StepBudget(64, GasQualityRules.MaxStepScale));
        Assert.Equal(GasQualityRules.MaxSteps, GasQualityRules.StepBudget(500, 2));
    }

    [Fact]
    public void TheFinestOctaveFadesOutOnlyWhereThePresetSays()
    {
        // No range: it is always all there.
        Assert.Equal(1f, GasQualityRules.NoiseKept(0, 0, 0));
        Assert.Equal(1f, GasQualityRules.NoiseKept(50, 0, 0));
        var low = Tiers.Quality.For(GasQuality.Low);
        Assert.Equal(1f, GasQualityRules.NoiseKept(low.NoiseFrom * 0.5f, low.NoiseFrom, low.NoiseTo));
        Assert.Equal(0f, GasQualityRules.NoiseKept(low.NoiseTo * 2, low.NoiseFrom, low.NoiseTo));
        var middle = GasQualityRules.NoiseKept((low.NoiseFrom + low.NoiseTo) * 0.5f, low.NoiseFrom, low.NoiseTo);
        Assert.InRange(middle, 0.45f, 0.55f);
        // Monotone: farther never brings the detail back.
        var last = 1f;
        for (var d = 0f; d < 3; d += 0.05f)
        {
            var kept = GasQualityRules.NoiseKept(d, low.NoiseFrom, low.NoiseTo);
            Assert.True(kept <= last + 1e-6f);
            last = kept;
        }
    }

    [Fact]
    public void TheShadersReadTheQualityFromTheFrameAndKeepOneMarchSite()
    {
        static string Shader(string name) => File.ReadAllText(AppPaths.Asset(Path.Combine("shaders", name)));
        var frame = Shader("frame.glsl");
        var gas = Shader("gas.glsl");
        var half = Shader("gas_half.glsl");
        Assert.Contains("vec4 gasQuality;", frame);
        Assert.Contains($"const int GasMaxSteps={GasQualityRules.MaxSteps};", gas);
        Assert.Contains("frame.gasQuality.y", gas);
        Assert.Contains("frame.gasQuality.x", half);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(gas, @"gasMarch\(second"));
    }
}
