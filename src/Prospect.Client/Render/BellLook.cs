namespace Prospect.Client.Render;

/// <summary>
/// The main engine's look, from the <c>bell</c> entry of an engine's model in <c>content/visuals/propulsion.json</c>:
/// instead of the plain housing and round nozzle, a square bolted mount plate over the whole mounting face, and for
/// each nozzle a ring collar with accent bands and a light on each side, then a faceted bell whose throat is lit and
/// glows brighter with thrust (<see cref="PropulsionModels.BuildEngineGlow"/>). The nozzle's own figures (exit, throat,
/// length, facets, rings) stay in the model's entry; a box deeper than plate, collar and bell need grows the model's
/// housing between plate and collar. Every length is a share of the box or of the nozzle's exit radius.
/// </summary>
internal sealed class BellLook
{
    /// <summary>The mount plate's thickness, as a share of the box's smaller half side.</summary>
    public float PlateDepth { get; init; } = 0.2f;

    /// <summary>How far the raised plate under each nozzle stands in from its square's edges, as a share of that square's half side; its corners are cut by twice that.</summary>
    public float PlateInset { get; init; } = 0.1f;

    /// <summary>Bolts at the corners of each nozzle's plate: 0 or 4.</summary>
    public int Bolts { get; init; } = 4;

    /// <summary>Sides of the ring collar: a multiple of four, 8 to 24.</summary>
    public int CollarSides { get; init; } = 16;

    /// <summary>The collar's radius in exit radii, and how far its band stands proud, as a share of that radius.</summary>
    public float CollarRadius { get; init; } = 0.92f;
    public float BandLift { get; init; } = 0.07f;

    /// <summary>The paint of the collar and bell, of the mount plate and collar rings, and of the accent bands, as 0xRRGGBB.</summary>
    public uint BodyColor { get; init; } = 0x131316;
    public uint PlateColor { get; init; } = 0x313234;
    public uint AccentColor { get; init; } = 0xCE3D03;

    /// <summary>The light of the collar's side lights, and of the throat while the engine is idle.</summary>
    public uint LightColor { get; init; } = 0x08668C;
    public uint ThroatColor { get; init; } = 0x000810;

    /// <summary>The light the throat gives off under thrust, its brightness at full throttle, and how far up the bell it reaches (a share of the bell's length).</summary>
    public uint GlowColor { get; init; } = 0x1ABFFF;
    public float Glow { get; init; } = 2.2f;
    public float GlowReach { get; init; } = 0.4f;

    /// <summary>Nozzles smaller on screen than this, in pixels of exit radius, show no glow of their own.</summary>
    public float GlowPixels { get; init; } = 5f;

    /// <summary>The light of a throat under thrust of <paramref name="strength"/> (0 to 1), as the colour of an unlit part.</summary>
    public System.Numerics.Vector3 GlowLight(float strength)
    {
        var level = Glow * Math.Clamp(strength, 0f, 1f);
        return new System.Numerics.Vector3(((GlowColor >> 16) & 0xFF) / 255f, ((GlowColor >> 8) & 0xFF) / 255f, (GlowColor & 0xFF) / 255f) * level;
    }

    internal bool Valid =>
        PlateDepth is >= 0.05f and <= 0.6f && PlateInset is >= 0.02f and <= 0.25f && Bolts is 0 or 4
        && CollarSides is >= 8 and <= 24 && CollarSides % 4 == 0
        && CollarRadius is >= 0.5f and <= 1.2f && BandLift is >= 0 and <= 0.2f
        && BodyColor <= 0xFFFFFF && PlateColor <= 0xFFFFFF && AccentColor <= 0xFFFFFF
        && LightColor <= 0xFFFFFF && ThroatColor <= 0xFFFFFF && GlowColor <= 0xFFFFFF
        && Glow is >= 0 and <= 8 && GlowReach is >= 0.2f and <= 0.9f && GlowPixels is >= 0 and <= 64;
}
