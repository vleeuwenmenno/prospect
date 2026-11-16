using Brutal.Numerics;

namespace Prospect.Client.Render;

/// <summary>Client-only beam, turret, reach-line and warning appearance from propulsion.json.</summary>
internal sealed class TractorLook
{
    public uint BeamColor { get; init; }
    public uint WarningColor { get; init; }
    public float CoreWidth { get; init; }
    public float GlowWidth { get; init; }
    public float Opacity { get; init; }
    public float WarningTint { get; init; }
    public int PreviewPairsPerFrame { get; init; }
    /// <summary>How many strands a beam has.</summary>
    public int StrandCount { get; init; } = 4;
    public uint StrandColor { get; init; } = 7927274;
    public uint HazeColor { get; init; } = 1806006;
    /// <summary>A strand's width in metres; seen from farther than that covers, it spreads its light and fades.</summary>
    public float StrandWidth { get; init; } = 0.012f;
    public float StrandOpacity { get; init; } = 1.4f;
    /// <summary>The beam's radius at the lens, in metres.</summary>
    public float EmitterSpread { get; init; } = 0.035f;
    /// <summary>The beam's radius at the container, as a share of the container's bounding radius.</summary>
    public float TargetSpread { get; init; } = 0.55f;
    /// <summary>How far a strand wavers sideways, as a share of the beam's radius there.</summary>
    public float WaveAmplitude { get; init; } = 0.18f;
    /// <summary>Wavers per metre of beam (at least about half a waver on a short beam).</summary>
    public float WaveFrequency { get; init; } = 0.18f;
    public float WaveSpeed { get; init; } = 1.2f;
    /// <summary>How fast light flows along the strands towards the ship, in metres a second.</summary>
    public float FlowSpeed { get; init; } = 3f;
    /// <summary>Metres between the bright pulses that flow along a strand.</summary>
    public float PulseSpacing { get; init; } = 2.5f;
    public float HazeWidth { get; init; } = 0.25f;
    public float HazeOpacity { get; init; } = 0.05f;
    public float ContainerGlow { get; init; } = 0.22f;
    /// <summary>The aura's radius about the held container, as a share of its bounding radius.</summary>
    public float ContainerGlowScale { get; init; } = 1.5f;
    public float TurretBaseRadius { get; init; } = 0.88f;
    public float TurretBaseHeight { get; init; } = 0.18f;
    public int TurretBolts { get; init; } = 8;
    public float TurretYokeHeight { get; init; } = 0.72f;
    public float TurretYokeSpread { get; init; } = 0.56f;
    public float TurretHeadLength { get; init; } = 1.15f;
    public float TurretHeadRadius { get; init; } = 0.32f;
    public int TurretCoilCount { get; init; } = 3;
    public float TurretSlewRate { get; init; } = 180f;
    public float TurretMaxPitch { get; init; } = 82f;
    public uint TurretLensColor { get; init; } = 7927274;
    /// <summary>The paint of the field coils round the emitter barrel.</summary>
    public uint TurretCoilColor { get; init; } = 0xA8683A;
    public float ReachDashLength { get; init; } = 8f;
    public float ReachGapLength { get; init; } = 5f;
    public float ReachArrowSize { get; init; } = 8f;

    public TractorLookConstants Constants => new()
    {
        Strand = new float4(LinearColor(StrandColor), StrandWidth),
        Haze = new float4(LinearColor(HazeColor), HazeWidth),
        Motion = new float4(StrandCount, WaveAmplitude, WaveFrequency, WaveSpeed),
        Flow = new float4(FlowSpeed, StrandOpacity, HazeOpacity, ContainerGlow),
        Spread = new float4(EmitterSpread, TargetSpread, ContainerGlowScale, PulseSpacing),
    };

    private static float3 LinearColor(uint rgb) => new(
        MathF.Pow(((rgb >> 16) & 0xFF) / 255f, 2.2f),
        MathF.Pow(((rgb >> 8) & 0xFF) / 255f, 2.2f),
        MathF.Pow((rgb & 0xFF) / 255f, 2.2f));

    public void Check()
    {
        if (BeamColor > 0xFFFFFF || WarningColor > 0xFFFFFF || StrandColor > 0xFFFFFF
            || HazeColor > 0xFFFFFF || TurretLensColor > 0xFFFFFF || TurretCoilColor > 0xFFFFFF
            || !(CoreWidth is > 0 and <= 8) || !(GlowWidth >= CoreWidth && GlowWidth <= 32)
            || !(Opacity is > 0 and <= 1) || !(WarningTint is > 0 and <= 1)
            || PreviewPairsPerFrame is < 1 or > 4096
            || StrandCount is < 1 or > 8
            || !(StrandWidth is > 0 and <= 0.5f) || !(StrandOpacity is > 0 and <= 4)
            || !(EmitterSpread is >= 0 and <= 2) || !(TargetSpread is >= 0 and <= 4)
            || !(WaveAmplitude is >= 0 and <= 1.5f) || !(WaveFrequency is > 0 and <= 4)
            || !(WaveSpeed is >= 0 and <= 20) || !(FlowSpeed is > 0 and <= 20)
            || !(PulseSpacing is >= 0.1f and <= 50)
            || !(HazeWidth is > 0 and <= 4) || !(HazeOpacity is >= 0 and <= 1)
            || !(ContainerGlow is >= 0 and <= 2) || !(ContainerGlowScale is >= 1 and <= 4)
            || !(TurretBaseRadius is > 0.3f and <= 1f) || !(TurretBaseHeight is > 0.05f and <= 0.6f)
            || TurretBolts is < 4 or > 16
            || !(TurretYokeHeight is > 0.2f and <= 2f) || !(TurretYokeSpread is > 0.2f and <= 1f)
            || !(TurretHeadLength is > 0.4f and <= 2.5f) || !(TurretHeadRadius is > 0.1f and <= 0.6f)
            || TurretCoilCount is < 2 or > 4
            || !(TurretSlewRate is >= 15f and <= 720f) || !(TurretMaxPitch is >= 15f and <= 89f)
            || !(ReachDashLength is > 0 and <= 64) || !(ReachGapLength is > 0 and <= 64)
            || !(ReachArrowSize is >= 2 and <= 32))
            throw new InvalidDataException("Invalid tractor beam appearance or preview budget.");
    }
}

/// <summary>The tractor beam shader's push constants. Matches <c>tractor.vert</c> and <c>tractor.frag</c>.</summary>
internal struct TractorLookConstants
{
    public float4 Strand;
    public float4 Haze;
    public float4 Motion;
    public float4 Flow;
    public float4 Spread;
}
