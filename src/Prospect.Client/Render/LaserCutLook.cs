using System.Numerics;

namespace Prospect.Client.Render;

/// <summary>
/// The hot mark a mining laser leaves where it cuts, from <c>propulsion.json</c> (<c>laser.cut</c>): how hot the rock
/// glows and how its colour cools from white-hot through orange to a dull red, the fractures that spread from the cut,
/// and the dust thrown up where a chunk breaks off. Lengths are metres, or shares of the scrape's own radius.
/// </summary>
internal sealed class LaserCutLook
{
    /// <summary>The mark's radius, as a share of the scrape's: how much surface around the cut a mark lights up.</summary>
    public float MarkRadius { get; init; } = 1.6f;

    /// <summary>The glow's brightness at its hottest, times the colour's own value.</summary>
    public float Glow { get; init; } = 2.6f;

    /// <summary>Seconds the rock glows after the beam last cut it.</summary>
    public float HotSeconds { get; init; } = 5f;

    /// <summary>The glow's colour when it is hottest, as it cools, and when it is nearly dark, as 0xRRGGBB.</summary>
    public uint HotColor { get; init; } = 0xFFF4E2;
    public uint WarmColor { get; init; } = 0xFF7A20;
    public uint CoolColor { get; init; } = 0x8A1A05;

    /// <summary>The share of <see cref="HotSeconds"/> the glow stays white-hot for before it cools to orange.</summary>
    public float HotShare { get; init; } = 0.55f;

    /// <summary>Seconds the fractures stay, and the seconds they are held at full strength before they fade.</summary>
    public float CrackSeconds { get; init; } = 16f;
    public float CrackHoldSeconds { get; init; } = 9f;

    /// <summary>How far the fractures may reach, as a share of the mark's radius, how wide a line is in metres, and how dark it is.</summary>
    public float CrackReach { get; init; } = 1.5f;
    public float CrackWidth { get; init; } = 0.14f;
    public float CrackAlpha { get; init; } = 0.45f;

    /// <summary>Dust a broken-off chunk throws: how many motes, how fast (m/s), how long each lives (s) and its size (m).</summary>
    public int BurstParticles { get; init; } = 24;
    public float BurstSpeed { get; init; } = 3.5f;
    public float BurstSeconds { get; init; } = 1.5f;
    public float BurstSize { get; init; } = 0.3f;

    /// <summary>The slower, larger motes of debris that drift out with it, on the same terms.</summary>
    public int DebrisParticles { get; init; } = 8;
    public float DebrisSpeed { get; init; } = 1.8f;
    public float DebrisSeconds { get; init; } = 1.9f;
    public float DebrisSize { get; init; } = 0.42f;

    /// <summary>Refuses figures outside what the mark, its cracks and the dust pool can draw.</summary>
    public void Check(string path)
    {
        if (HotColor > 0xFFFFFF || WarmColor > 0xFFFFFF || CoolColor > 0xFFFFFF
            || !(MarkRadius is >= 0.2f and <= 6f) || !(Glow is >= 0 and <= 20)
            || !(HotSeconds is > 0 and <= 30) || !(HotShare is >= 0.01f and < 1f)
            || !(CrackSeconds is > 0 and <= 300) || !(CrackHoldSeconds >= 0 && CrackHoldSeconds < CrackSeconds)
            || !(CrackReach is >= 1 and <= 6) || !(CrackWidth is > 0 and <= 1) || !(CrackAlpha is >= 0 and <= 1)
            || BurstParticles is < 0 or > 512 || DebrisParticles is < 0 or > 512
            || !(BurstSpeed is >= 0 and <= 50) || !(DebrisSpeed is >= 0 and <= 50)
            || !(BurstSeconds is > 0 and <= 4) || !(DebrisSeconds is > 0 and <= 4)
            || !(BurstSize is > 0 and <= 2) || !(DebrisSize is > 0 and <= 2))
            throw new InvalidDataException($"{path}: laser cut is out of range (a mark of 0.2 to 6 scrape radii, cracks 1 to 6 of those, dust of at most 4 s)");
    }

    /// <summary>
    /// The glow of a mark the beam last cut <paramref name="age"/> seconds ago: its cooling colour times how bright it
    /// still is, in linear values as <c>mesh.frag</c> reads them. Zero once it has cooled.
    /// </summary>
    public Vector3 Heat(float age)
    {
        var heat = 1f - age / HotSeconds;
        if (!(heat > 0)) return Vector3.Zero;
        if (heat > 1) heat = 1;
        var colour = heat > HotShare
            ? Mix(WarmColor, HotColor, (heat - HotShare) / (1f - HotShare))
            : Mix(CoolColor, WarmColor, heat / HotShare);
        // It dims faster than it cools: fresh rock blazes, and the last of it is a smoulder.
        return colour * (Glow * heat * MathF.Sqrt(heat));
    }

    /// <summary>How dark the fractures are <paramref name="age"/> seconds after the cut: held, then faded out.</summary>
    public float CrackStrength(float age)
    {
        var fade = CrackSeconds - age;
        if (!(fade > 0)) return 0;
        var held = MathF.Max(CrackSeconds - CrackHoldSeconds, 1e-3f);
        return CrackAlpha * MathF.Min(fade / held, 1f);
    }

    /// <summary>A colour from content, mixed in its own (sRGB) values and then made linear, as <c>mesh.vert</c> reads paint.</summary>
    private static Vector3 Mix(uint from, uint to, float t)
    {
        if (t < 0) t = 0;
        else if (t > 1) t = 1;
        return new Vector3(Channel(from, to, 16, t), Channel(from, to, 8, t), Channel(from, to, 0, t));
    }

    private static float Channel(uint from, uint to, int shift, float t)
    {
        var a = ((from >> shift) & 0xFF) / 255f;
        var b = ((to >> shift) & 0xFF) / 255f;
        return MathF.Pow(a * (1f - t) + b * t, 2.2f);
    }
}
