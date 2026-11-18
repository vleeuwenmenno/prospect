using Brutal.Numerics;

namespace Prospect.Client.Render;

/// <summary>
/// How a mounted mining laser looks, from the <c>laser</c> entry of <c>propulsion.json</c>: a turret and its beam. The
/// turret is a base ring flush on the block face (in the hull mesh), a yoke that turns about the face normal, and a
/// head on the yoke's trunnion that pitches to lie along the beam: a chunky body with accent panels, an octagonal
/// barrel and a muzzle ring round the lens the beam leaves from. Base and yoke are the shared turret
/// (<see cref="Turret"/>); every length is a share of the base ring's radius, itself a share of the mount's half
/// width, so a laser on any block size is the same model. The parts are pure functions of that width, built on a worker.
/// </summary>
internal sealed class LaserLook
{
    /// <summary>The base ring: its radius as a share of the mount's half width, its height as a share of that radius, its sides (even, 8 to 16) and how many of them carry a glow slot.</summary>
    public float BaseRadius { get; init; } = 0.94f;
    public float BaseHeight { get; init; } = 0.24f;
    public int BaseSides { get; init; } = 12;
    public int Slots { get; init; } = 6;

    /// <summary>The yoke: from the collar's top to the trunnion, from the axis to an arm, half an arm's thickness and the hub cap's radius.</summary>
    public float YokeHeight { get; init; } = 0.78f;
    public float YokeSpread { get; init; } = 0.64f;
    public float YokeArm { get; init; } = 0.1f;
    public float YokeHub { get; init; } = 0.3f;

    /// <summary>The head's body: how far it reaches behind and ahead of the trunnion, and its half width and half height.</summary>
    public float HeadBack { get; init; } = 0.34f;
    public float HeadFront { get; init; } = 0.46f;
    public float HeadWidth { get; init; } = 0.46f;
    public float HeadHeight { get; init; } = 0.44f;

    /// <summary>The barrel ahead of the body: its length and radius, and the muzzle ring that ends it (part of that length).</summary>
    public float BarrelLength { get; init; } = 0.6f;
    public float BarrelRadius { get; init; } = 0.28f;
    public float MuzzleLength { get; init; } = 0.2f;
    public float MuzzleRadius { get; init; } = 0.36f;

    /// <summary>The lens: its radius and how deep it sits in the muzzle.</summary>
    public float LensRadius { get; init; } = 0.25f;
    public float LensRecess { get; init; } = 0.05f;

    /// <summary>How fast the head turns onto a beam, in degrees a second, and how far off the face normal it can pitch.</summary>
    public float SlewRate { get; init; } = 360f;
    public float MaxPitch { get; init; } = 75f;

    /// <summary>The paint of the plating, of the dark frames and of the accent panels, as 0xRRGGBB.</summary>
    public uint BodyColor { get; init; } = 0x131316;
    public uint DarkColor { get; init; } = 0x080809;
    public uint AccentColor { get; init; } = 0xCE3D03;

    /// <summary>The light of the base's glow slots and the strips on the barrel.</summary>
    public uint LightColor { get; init; } = 0x08668C;

    /// <summary>The light the lens gives off, and what it turns toward as the laser heats.</summary>
    public uint LensColor { get; init; } = 0xFF6605;
    public uint HotColor { get; init; } = 0xFF1403;

    /// <summary>Brightness of the lens at rest and while it fires, what full heat adds, and the firing lens's flicker (a second, and how deep).</summary>
    public float IdleGlow { get; init; } = 0.45f;
    public float ActiveGlow { get; init; } = 2.6f;
    public float HeatGlow { get; init; } = 1.2f;
    public float PulseRate { get; init; } = 7f;
    public float PulseDepth { get; init; } = 0.12f;

    /// <summary>The beam's appearance.</summary>
    public LaserBeamLook Beam { get; init; } = new();

    /// <summary>The hot mark it leaves on the rock where it cuts, its cooling, and the dust a broken chunk throws.</summary>
    public LaserCutLook Cut { get; init; } = new();

    /// <summary>The base ring and yoke of the turret, as the shared turret models take them.</summary>
    public TurretFrame Turret => new(BaseRadius, BaseHeight, BaseSides, Slots, YokeHeight, YokeSpread, YokeArm, YokeHub, BodyColor, DarkColor, AccentColor, LightColor);

    /// <summary>
    /// The light the lens gives off: <paramref name="firing"/> (0 to 1) brightens it and <paramref name="heat"/>
    /// (0 to 1) turns it toward the hot colour and adds its own glow; <paramref name="time"/> is the flicker's clock.
    /// </summary>
    public System.Numerics.Vector3 LensLight(float firing, float heat, float time)
    {
        var pulse = 1f + PulseDepth * MathF.Sin(time * PulseRate * MathF.Tau);
        var level = IdleGlow + (ActiveGlow * pulse - IdleGlow) * firing + HeatGlow * heat;
        // A firing lens stays its own colour; the heat shows as it stops.
        var hot = heat * (1f - 0.5f * firing);
        static float Mix(uint a, uint b, int shift, float t) => (((a >> shift) & 0xFF) * (1f - t) + ((b >> shift) & 0xFF) * t) / 255f;
        return new System.Numerics.Vector3(Mix(LensColor, HotColor, 16, hot), Mix(LensColor, HotColor, 8, hot), Mix(LensColor, HotColor, 0, hot)) * level;
    }

    /// <summary>Refuses a look outside what the turret's geometry and the beam shader can hold.</summary>
    public void Check(string path)
    {
        var turret = Turret;
        var pitch = MaxPitch * (MathF.PI / 180f);
        var front = HeadFront + BarrelLength;
        var room = YokeHeight - TurretFrame.TableHeight - 0.05f;
        if (!turret.Valid
            || !(HeadBack is >= 0.1f and <= 1f) || !(HeadFront is >= 0.1f and <= 1.5f) || !(HeadHeight is >= 0.1f and <= 0.8f)
            // The head fits between the arms, and its back turns clear of the turntable and the brace at every pitch.
            || !(HeadWidth >= 0.1f && HeadWidth * PropulsionModels.LaserHoodWidth <= YokeSpread - YokeArm - 0.01f)
            || !turret.Clears(MathF.Sqrt(HeadBack * HeadBack + HeadHeight * HeadHeight))
            || !(BarrelLength is >= 0.2f and <= 2f) || !(BarrelRadius >= 0.05f && BarrelRadius <= 0.8f * MathF.Min(HeadWidth, HeadHeight))
            || !(MuzzleLength >= 0.05f && MuzzleLength <= 0.8f * BarrelLength) || !(MuzzleRadius > BarrelRadius && MuzzleRadius <= HeadWidth)
            || !(LensRadius >= 0.03f && LensRadius <= 0.85f * MuzzleRadius) || !(LensRecess >= 0.01f && LensRecess <= 0.8f * MuzzleLength)
            || !(SlewRate is >= 30f and <= 1440f) || !(MaxPitch is >= 15f and <= 85f)
            // Pitched as far as it goes, neither the body's front nor the muzzle dips into the turntable.
            || HeadFront * MathF.Cos(pitch) - HeadHeight * MathF.Sin(pitch) < -room
            || front * MathF.Cos(pitch) - MuzzleRadius * MathF.Sin(pitch) < -room
            || LensColor > 0xFFFFFF || HotColor > 0xFFFFFF
            || !(IdleGlow is >= 0 and <= 8) || !(ActiveGlow >= IdleGlow && ActiveGlow <= 8) || !(HeatGlow is >= 0 and <= 8)
            || !(PulseRate is >= 0 and <= 30) || !(PulseDepth is >= 0 and <= 1))
            throw new InvalidDataException($"{path}: laser turret is out of range: a base of 8 to 16 sides, a head that fits between the yoke's arms and turns clear of the yoke at every pitch, a muzzle wider than the barrel and a lens inside it");
        Beam.Check(path);
        Cut.Check(path);
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
