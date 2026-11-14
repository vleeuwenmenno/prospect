using Brutal.Numerics;

namespace Prospect.Client.Render;

/// <summary>
/// The player's graphics quality for gas clouds, a preference of this client. It changes how finely the primary
/// cloud is marched, never what a cloud is: nothing about it is on the wire or in a saved world.
/// </summary>
public enum GasQuality
{
    /// <summary>A quarter of the rays, fewer steps, and the finest noise octave smoothed away at a distance.</summary>
    Low,

    /// <summary>One ray in four pixels, as the game shipped it: the default.</summary>
    Medium,

    /// <summary>Every pixel marches its own ray.</summary>
    High,

    /// <summary>Every pixel marches its own ray, with more and finer steps.</summary>
    Ultra,
}

/// <summary>
/// What one <see cref="GasQuality"/> preset sets, from <c>content/visuals/gas.json</c> (<c>quality</c>). The shaders
/// read all of it from <see cref="FrameConstants.GasQuality"/>, so a change applies on the next frame without
/// rebuilding a pipeline or a target.
/// </summary>
internal sealed class GasQualityFigures
{
    /// <summary>
    /// How many pixels, along each side, one marched ray stands for: 1 marches every pixel, 2 and 4 march the
    /// half-resolution target (<c>gas_half.glsl</c>) at that fraction of the window.
    /// </summary>
    public int Resolution { get; init; }

    /// <summary>
    /// Multiplies the content's view sample count of the full level, so 0.6 takes about three fifths of the steps and
    /// 1.5 a half as many again, each step shorter. The shader keeps the result between 16 and
    /// <see cref="GasQualityRules.MaxSteps"/>.
    /// </summary>
    public float Steps { get; init; }

    /// <summary>
    /// Where, in x half-extents of the cloud from the camera, the finest noise octave starts to be smoothed away
    /// (<c>gasDensity</c> detail 3).
    /// </summary>
    public float NoiseFrom { get; init; }

    /// <summary>Where the finest octave is wholly smoothed away. Zero in both figures keeps it at every distance.</summary>
    public float NoiseTo { get; init; }

    /// <summary>The four figures as <c>frame.gasQuality</c> takes them: resolution, steps, noise start and noise end.</summary>
    public float4 ToConstants() => new(Resolution, Steps, NoiseFrom, NoiseTo);

    /// <summary>The figures the gas fixtures run with: the exact march, whatever the player chose.</summary>
    public static readonly float4 Exact = new(1, 1, 0, 0);
}

/// <summary>The four presets, as <c>content/visuals/gas.json</c> names them.</summary>
internal sealed class GasQualityTable
{
    public GasQualityFigures? Low { get; init; }
    public GasQualityFigures? Medium { get; init; }
    public GasQualityFigures? High { get; init; }
    public GasQualityFigures? Ultra { get; init; }

    /// <summary>The figures of <paramref name="quality"/>; a value outside the enum takes Medium's.</summary>
    public GasQualityFigures For(GasQuality quality) => quality switch
    {
        GasQuality.Low => Low!,
        GasQuality.High => High!,
        GasQuality.Ultra => Ultra!,
        _ => Medium!,
    };

    /// <summary>Reads a preset's name as written on the command line or in the file; false for anything else.</summary>
    public static bool TryParse(string? text, out GasQuality quality)
    {
        quality = GasQuality.Medium;
        return text is not null && Enum.TryParse(text, ignoreCase: true, out quality) && Enum.IsDefined(quality) && !int.TryParse(text, out _);
    }

    /// <summary>Refuses a table with a missing preset, a figure out of range, or presets that do not rise in cost from Low to Ultra.</summary>
    public void Validate(string path)
    {
        GasQualityFigures[] presets = [Low ?? Missing(path, "low"), Medium ?? Missing(path, "medium"), High ?? Missing(path, "high"), Ultra ?? Missing(path, "ultra")];
        foreach (var figures in presets)
        {
            if (figures.Resolution is not (1 or 2 or 4))
                throw new InvalidDataException($"{path}: quality resolution is 1, 2 or 4");
            if (!(figures.Steps is >= 0.25f and <= GasQualityRules.MaxStepScale))
                throw new InvalidDataException($"{path}: quality steps is 0.25 to {GasQualityRules.MaxStepScale}");
            var noiseOff = figures.NoiseFrom == 0 && figures.NoiseTo == 0;
            if (!noiseOff && !(figures.NoiseFrom is >= 0.1f && figures.NoiseTo > figures.NoiseFrom && figures.NoiseTo <= 8))
                throw new InvalidDataException($"{path}: quality noiseFrom and noiseTo are both 0, or 0.1 or more and below noiseTo, which is 8 at most (x half-extents)");
        }

        for (var i = 1; i < presets.Length; i++)
        {
            if (presets[i].Resolution > presets[i - 1].Resolution || presets[i].Steps < presets[i - 1].Steps)
                throw new InvalidDataException($"{path}: quality presets must not get cheaper from low to ultra: resolution may only fall and steps only rise");
        }

        // Medium is what the game ran before it had a setting: its figures are not a choice.
        if (Medium!.Resolution != 2 || Medium.Steps != 1f || Medium.NoiseTo != 0)
            throw new InvalidDataException($"{path}: quality medium is resolution 2, steps 1 and the noise untouched");
    }

    private static GasQualityFigures Missing(string path, string name) =>
        throw new InvalidDataException($"{path}: quality.{name} is missing");
}

/// <summary>The font-free rules that turn a preset into what the renderer and the shader do.</summary>
internal static class GasQualityRules
{
    /// <summary>The most steps the shader's march takes whatever the content and the preset say; its loops are this long.</summary>
    public const int MaxSteps = 128;

    /// <summary>The largest <see cref="GasQualityFigures.Steps"/>: with 64 content samples it reaches <see cref="MaxSteps"/>.</summary>
    public const float MaxStepScale = 2f;

    /// <summary>Whether the primary cloud goes through the half-resolution march at these figures; at resolution 1 every pixel marches itself.</summary>
    public static bool Marches(GasQualityFigures figures) => figures.Resolution >= 2;

    /// <summary>The size of the march target for a window: a share of it, rounded up so the last pixels still have a texel.</summary>
    public static (int Width, int Height) TargetSize(int width, int height, int resolution)
    {
        var scale = Math.Max(resolution, 1);
        return ((width + scale - 1) / scale, (height + scale - 1) / scale);
    }

    /// <summary>The view steps a cloud of <paramref name="samples"/> content samples takes at <paramref name="steps"/>, as the shader works it out for the full level.</summary>
    public static int StepBudget(float samples, float steps) => Math.Clamp((int)(samples * steps + 0.5f), 16, MaxSteps);

    /// <summary>
    /// How much of the finest noise octave a sample keeps at <paramref name="distance"/> x half-extents from the
    /// camera: 1 up to <paramref name="from"/>, 0 from <paramref name="to"/>, smoothly between. Both zero keeps it all.
    /// The shader works the same out in <c>gasMarch</c>.
    /// </summary>
    public static float NoiseKept(float distance, float from, float to)
    {
        if (!(to > from)) return 1f;
        var t = Math.Clamp((distance - from) / (to - from), 0f, 1f);
        return 1f - t * t * (3f - 2f * t);
    }
}
