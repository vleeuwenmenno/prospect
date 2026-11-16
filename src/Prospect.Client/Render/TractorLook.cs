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
    public void Check()
    {
        if (BeamColor > 0xFFFFFF || WarningColor > 0xFFFFFF || !(CoreWidth is > 0 and <= 8)
            || !(GlowWidth >= CoreWidth && GlowWidth <= 32) || !(Opacity is > 0 and <= 1)
            || !(WarningTint is > 0 and <= 1) || PreviewPairsPerFrame is < 1 or > 4096)
            throw new InvalidDataException("Invalid tractor beam appearance or preview budget.");
    }
}
