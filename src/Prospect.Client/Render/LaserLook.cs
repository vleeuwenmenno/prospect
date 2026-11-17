using Brutal.Numerics;

namespace Prospect.Client.Render;

/// <summary>
/// How a mounted mining laser looks, from the <c>laser</c> entry of <c>propulsion.json</c>: its model (round base
/// plate, housing with field coils, a muzzle shroud round a recessed lens) and its beam. A laser is a small part on a
/// block face, so every model figure is a share of the thin box it fills (radii of the face's half width, heights of
/// its depth), exactly as for <see cref="ScannerLook"/>; the model is a pure function of that box and is built on a
/// worker.
/// </summary>
internal sealed class LaserLook
{
    /// <summary>Sides of every round part: 8 to 32.</summary>
    public int Sides { get; init; } = 16;

    /// <summary>The base plate's radius as a share of the face's half width, and its height as a share of the depth.</summary>
    public float PlateRadius { get; init; } = 0.94f;
    public float PlateHeight { get; init; } = 0.2f;

    /// <summary>The housing's radius, and where the muzzle shroud starts (a share of the depth); the shroud's radius.</summary>
    public float HousingRadius { get; init; } = 0.58f;
    public float MuzzleStart { get; init; } = 0.62f;
    public float MuzzleRadius { get; init; } = 0.78f;

    /// <summary>The lens's radius (a share of the half width) and how deep it sits (a share of the depth, from the face).</summary>
    public float LensRadius { get; init; } = 0.5f;
    public float LensDepth { get; init; } = 0.8f;

    /// <summary>Field coils round the housing, 1 to 3, and their radius.</summary>
    public int Coils { get; init; } = 2;
    public float CoilRadius { get; init; } = 0.7f;

    /// <summary>The plate's tier-coloured rim as a share of its radius.</summary>
    public float RimShare { get; init; } = 0.9f;

    /// <summary>Shades, 0 dark to 1 bright, as the other models give them (<see cref="PropulsionModels.Tag"/>).</summary>
    public float PlateShade { get; init; } = 0.36f;
    public float HousingShade { get; init; } = 0.5f;
    public float DetailShade { get; init; } = 0.66f;
    public float DarkShade { get; init; } = 0.05f;

    /// <summary>The light the lens gives off, and the paint of the coils, as 0xRRGGBB.</summary>
    public uint LensColor { get; init; } = 0xFF5A1E;
    public uint CoilColor { get; init; } = 0x8A3A22;

    /// <summary>The beam's appearance.</summary>
    public LaserBeamLook Beam { get; init; } = new();

    /// <summary>Refuses a look outside what the model's geometry and the beam shader can hold.</summary>
    public void Check(string path)
    {
        static bool Share(float v, float low = 0.01f) => v >= low && v <= 1f;
        if (Sides is < 8 or > 32 || Coils is < 1 or > 3
            || !Share(PlateRadius, 0.2f) || !Share(PlateHeight, 0.05f) || !Share(HousingRadius, 0.1f) || !Share(MuzzleStart, 0.1f)
            || !Share(MuzzleRadius, 0.1f) || !Share(LensRadius, 0.05f) || !Share(LensDepth, 0.1f) || !Share(CoilRadius, 0.1f) || !Share(RimShare, 0.2f)
            || PlateHeight >= MuzzleStart || MuzzleStart >= LensDepth || HousingRadius >= PlateRadius || LensRadius >= MuzzleRadius
            || HousingRadius > MuzzleRadius || CoilRadius <= HousingRadius || CoilRadius > PlateRadius
            || !(PlateShade is >= 0 and <= 1) || !(HousingShade is >= 0 and <= 1) || !(DetailShade is >= 0 and <= 1) || !(DarkShade is >= 0 and <= 1)
            || LensColor > 0xFFFFFF || CoilColor > 0xFFFFFF)
            throw new InvalidDataException($"{path}: laser model is out of range: 8 to 32 sides, 1 to 3 coils, shares up to 1 with the plate under the muzzle, the muzzle under the lens and the lens in the shroud");
        Beam.Check(path);
    }
}

/// <summary>
/// A mining laser's beam, from <c>propulsion.json</c> (<c>laser.beam</c>): a bright core in a wider glow along the ray,
/// pulses running out of the emitter, a glow and a few sparks where it cuts. Widths are in metres; the beam is never
/// drawn thinner than <see cref="MinPixels"/> on screen (its light is then spread, not lost).
/// </summary>
internal sealed class LaserBeamLook
{
    /// <summary>The sparks one cut throws at most: the beam shader's loop bound.</summary>
    public const int MaxSparks = 8;

    public uint CoreColor { get; init; } = 0xFFE6C8;
    public uint GlowColor { get; init; } = 0xFF4A12;
    public float CoreWidth { get; init; } = 0.07f;
    public float GlowWidth { get; init; } = 0.4f;
    public float Opacity { get; init; } = 1f;

    /// <summary>The thinnest the beam is drawn, in pixels.</summary>
    public float MinPixels { get; init; } = 2f;

    /// <summary>How fast the pulses run along the beam (m/s), the metres between them, and how deep they dim it (0 steady, 1 off between).</summary>
    public float PulseSpeed { get; init; } = 24f;
    public float PulseSpacing { get; init; } = 2.5f;
    public float PulseDepth { get; init; } = 0.25f;

    /// <summary>A beam that finds no rock fades out over the part of its length after this share.</summary>
    public float FadeStart { get; init; } = 0.5f;

    /// <summary>Where the beam cuts: the glow's radius (m), colour and strength.</summary>
    public float HitRadius { get; init; } = 0.9f;
    public uint HitColor { get; init; } = 0xFF7A2A;
    public float HitStrength { get; init; } = 1.4f;

    /// <summary>The sparks: how many, how fast they fly (m/s), how long each lives (s), how long and how thick a streak is (m), and its colour.</summary>
    public int Sparks { get; init; } = 8;
    public float SparkSpeed { get; init; } = 9f;
    public float SparkSeconds { get; init; } = 0.45f;
    public float SparkLength { get; init; } = 0.55f;
    public float SparkWidth { get; init; } = 0.035f;
    public uint SparkColor { get; init; } = 0xFFC06A;

    /// <summary>Refuses figures outside what the shader draws.</summary>
    public void Check(string path)
    {
        if (CoreColor > 0xFFFFFF || GlowColor > 0xFFFFFF || HitColor > 0xFFFFFF || SparkColor > 0xFFFFFF
            || !(CoreWidth is > 0 and <= 2) || !(GlowWidth >= CoreWidth && GlowWidth <= 8) || !(Opacity is > 0 and <= 4)
            || !(MinPixels is >= 0 and <= 16) || !(PulseSpeed is >= 0 and <= 400) || !(PulseSpacing is >= 0.1f and <= 100) || !(PulseDepth is >= 0 and <= 1)
            || !(FadeStart is >= 0 and < 1)
            || !(HitRadius is > 0 and <= 8) || !(HitStrength is >= 0 and <= 8)
            || Sparks is < 0 or > MaxSparks || !(SparkSpeed is >= 0 and <= 200) || !(SparkSeconds is > 0 and <= 5)
            || !(SparkLength is > 0 and <= 10) || !(SparkWidth is > 0 and <= 1))
            throw new InvalidDataException($"{path}: laser beam is out of range");
    }

    /// <summary>
    /// The instance for <c>laser.vert</c> of a beam from <paramref name="from"/> to <paramref name="to"/> (both
    /// camera-relative) that cuts a rock or fires into space. <paramref name="seed"/> (0 to 1) makes beams differ. Takes
    /// no memory.
    /// </summary>
    public LaserInstance Instance(float3 from, float3 to, bool cutting, float seed) => new()
    {
        FromCore = new float4(from.X, from.Y, from.Z, CoreWidth),
        ToState = new float4(to.X, to.Y, to.Z, cutting ? 1f : 0f),
        CoreColor = new float4(Linear(CoreColor), Opacity),
        GlowColor = new float4(Linear(GlowColor), GlowWidth),
        Motion = new float4(seed, PulseSpeed, PulseSpacing, FadeStart),
        Pulse = new float4(PulseDepth, MinPixels, HitRadius, HitStrength),
        HitColor = new float4(Linear(HitColor), Sparks),
        Spark = new float4(Linear(SparkColor), SparkLength),
        SparkShape = new float4(SparkWidth, SparkSpeed, SparkSeconds, 0),
    };

    private static float3 Linear(uint rgb) => new(
        MathF.Pow(((rgb >> 16) & 0xFF) / 255f, 2.2f),
        MathF.Pow(((rgb >> 8) & 0xFF) / 255f, 2.2f),
        MathF.Pow((rgb & 0xFF) / 255f, 2.2f));
}
