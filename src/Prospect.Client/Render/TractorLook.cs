namespace Prospect.Client.Render;

/// <summary>Client-only beam, reach-line and warning colours from propulsion.json.</summary>
internal sealed class TractorLook
{
    public uint BeamColor { get; init; }
    public uint WarningColor { get; init; }
    public float CoreWidth { get; init; }
    public float GlowWidth { get; init; }
    public float Opacity { get; init; }
    public float WarningTint { get; init; }
    public int PreviewPairsPerFrame { get; init; }
    public float EmitterRadius { get; init; } = 0.14f;
    public float TargetRadius { get; init; } = 0.6f;
    public float MaxConeHalfWidth { get; init; } = 40f;
    public float ConeOpacity { get; init; } = 0.24f;
    public int PulseCount { get; init; } = 3;
    public float PulseSpeed { get; init; } = 1.1f;
    public float PulseWidth { get; init; } = 0.14f;
    public float PulseOpacity { get; init; } = 0.5f;
    public float EndGlowOpacity { get; init; } = 0.38f;
    public float EndGlowScale { get; init; } = 2.2f;
    public float ReachDashLength { get; init; } = 8f;
    public float ReachGapLength { get; init; } = 5f;
    public float ReachArrowSize { get; init; } = 8f;

    public void Check()
    {
        if (BeamColor > 0xFFFFFF || WarningColor > 0xFFFFFF || !(CoreWidth is > 0 and <= 8)
            || !(GlowWidth >= CoreWidth && GlowWidth <= 32) || !(Opacity is > 0 and <= 1)
            || !(WarningTint is > 0 and <= 1) || PreviewPairsPerFrame is < 1 or > 4096
            || !(EmitterRadius is > 0 and <= 4) || !(TargetRadius >= EmitterRadius && TargetRadius <= 8)
            || !(MaxConeHalfWidth is >= 4 and <= 128) || !(ConeOpacity is > 0 and <= 1)
            || PulseCount is < 1 or > 8 || !(PulseSpeed is > 0 and <= 10)
            || !(PulseWidth is > 0 and <= 0.5f) || !(PulseOpacity is >= 0 and <= 1)
            || !(EndGlowOpacity is >= 0 and <= 1) || !(EndGlowScale is >= 1 and <= 6)
            || !(ReachDashLength is > 0 and <= 64) || !(ReachGapLength is > 0 and <= 64)
            || !(ReachArrowSize is >= 2 and <= 32))
            throw new InvalidDataException("Invalid tractor beam appearance or preview budget.");
    }
}
