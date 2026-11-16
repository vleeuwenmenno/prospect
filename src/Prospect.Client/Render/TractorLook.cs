using Brutal.Numerics;

namespace Prospect.Client.Render;

/// <summary>Client-only beam, reach-line and warning appearance from propulsion.json.</summary>
internal sealed class TractorLook
{
    public uint BeamColor { get; init; }
    public uint WarningColor { get; init; }
    public float CoreWidth { get; init; }
    public float GlowWidth { get; init; }
    public float Opacity { get; init; }
    public float WarningTint { get; init; }
    public int PreviewPairsPerFrame { get; init; }
    public int StrandCount { get; init; } = 4;
    public uint StrandColor { get; init; } = 7927274;
    public uint HazeColor { get; init; } = 1806006;
    public float StrandWidth { get; init; } = 0.022f;
    public float StrandOpacity { get; init; } = 0.65f;
    public float EmitterSpread { get; init; } = 0.08f;
    public float TargetSpread { get; init; } = 0.72f;
    public float WaveAmplitude { get; init; } = 0.055f;
    public float WaveFrequency { get; init; } = 2.2f;
    public float WaveSpeed { get; init; } = 1.6f;
    public float FlowSpeed { get; init; } = 2.0f;
    public float HazeWidth { get; init; } = 0.42f;
    public float HazeOpacity { get; init; } = 0.085f;
    public float ContainerGlow { get; init; } = 0.42f;
    public float ContainerGlowScale { get; init; } = 1.45f;
    public float ReachDashLength { get; init; } = 8f;
    public float ReachGapLength { get; init; } = 5f;
    public float ReachArrowSize { get; init; } = 8f;

    public TractorLookConstants Constants => new()
    {
        Strand = new float4(LinearColor(StrandColor), StrandWidth),
        Haze = new float4(LinearColor(HazeColor), HazeWidth),
        Motion = new float4(StrandCount, WaveAmplitude, WaveFrequency, WaveSpeed),
        Flow = new float4(FlowSpeed, StrandOpacity, HazeOpacity, ContainerGlow),
        Spread = new float4(EmitterSpread, TargetSpread, ContainerGlowScale, 0f),
    };

    private static float3 LinearColor(uint rgb) => new(
        MathF.Pow(((rgb >> 16) & 0xFF) / 255f, 2.2f),
        MathF.Pow(((rgb >> 8) & 0xFF) / 255f, 2.2f),
        MathF.Pow((rgb & 0xFF) / 255f, 2.2f));

    public void Check()
    {
        if (BeamColor > 0xFFFFFF || WarningColor > 0xFFFFFF || StrandColor > 0xFFFFFF || HazeColor > 0xFFFFFF
            || !(CoreWidth is > 0 and <= 8) || !(GlowWidth >= CoreWidth && GlowWidth <= 32)
            || !(Opacity is > 0 and <= 1) || !(WarningTint is > 0 and <= 1)
            || PreviewPairsPerFrame is < 1 or > 4096
            || StrandCount is < 1 or > 8
            || !(StrandWidth is > 0 and <= 0.5f) || !(StrandOpacity is > 0 and <= 2)
            || !(EmitterSpread is >= 0 and <= 2) || !(TargetSpread is >= 0 and <= 4)
            || !(WaveAmplitude is >= 0 and <= 1) || !(WaveFrequency is > 0 and <= 20)
            || !(WaveSpeed is >= 0 and <= 20) || !(FlowSpeed is > 0 and <= 20)
            || !(HazeWidth is > 0 and <= 4) || !(HazeOpacity is >= 0 and <= 1)
            || !(ContainerGlow is >= 0 and <= 2) || !(ContainerGlowScale is >= 1 and <= 4)
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
