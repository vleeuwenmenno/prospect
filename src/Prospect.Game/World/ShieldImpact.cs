namespace Prospect.Game.World;

/// <summary>Quantizes contact energy without changing the hit record or physical damage.</summary>
public static class ShieldImpact
{
    /// <summary>A contact always has strength at least one; zero remains reserved for no event.</summary>
    public static byte Strength(float energy, float absorbed, float capacity, float fullFraction)
    {
        if (capacity <= 0 || fullFraction <= 0) return 1;
        var fraction = Math.Max(energy, absorbed) / capacity / fullFraction;
        return (byte)Math.Clamp((int)MathF.Ceiling(MathF.Sqrt(Math.Clamp(fraction, 0f, 1f)) * 255f), 1, 255);
    }
}
