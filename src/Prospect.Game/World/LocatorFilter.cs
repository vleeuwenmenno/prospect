namespace Prospect.Game.World;

/// <summary>
/// What an advanced locator's pulse looks for: a diameter range in whole metres and the main ores to report. The
/// filter is the player's own choice, kept by the client and sent with each pulse; the server keeps none of it.
/// <para>
/// A diameter of zero is no bound at all, so the default filter lets every diameter through. An ore of zero is no
/// ore narrowing either: every main ore is reported and so is bare rock. With ore bits set, only the rocks whose
/// main ore is one of them are reported and bare rock is left out, which is what a player looking for an ore wants.
/// </para>
/// </summary>
/// <param name="MinMetres">The smallest diameter reported, in whole metres; zero for no lower bound.</param>
/// <param name="MaxMetres">The largest diameter reported, in whole metres; zero for no upper bound.</param>
/// <param name="Ores">One bit a resource of the galaxy, by its index in <see cref="Galaxy.Resources"/>; zero for any ore.</param>
public readonly record struct LocatorFilter(ushort MinMetres = 0, ushort MaxMetres = 0, uint Ores = 0)
{
    /// <summary>The most resources a filter can name, one bit each: an observation's resource index is under 32.</summary>
    public const int MaxResources = 32;

    /// <summary>The widest diameter a filter can carry: the wire's two bytes, in metres.</summary>
    public const ushort MaxDiameterMetres = ushort.MaxValue;

    /// <summary>Whether the filter narrows nothing: every rock, of every diameter, is reported.</summary>
    public bool Empty => MinMetres == 0 && MaxMetres == 0 && Ores == 0;

    /// <summary>How many main ores the filter names; zero means any ore.</summary>
    public int OreCount
    {
        get
        {
            var bits = Ores;
            var count = 0;
            while (bits != 0) { bits &= bits - 1; count++; }
            return count;
        }
    }

    /// <summary>The bit of one resource in <see cref="Ores"/>; zero for an index no filter can name.</summary>
    public static uint BitOf(int resource) => (uint)resource < MaxResources ? 1u << resource : 0u;

    /// <summary>Every ore bit this galaxy has; bits above it are unknown and refused.</summary>
    public static uint AllOres(Galaxy galaxy)
    {
        var mask = 0u;
        for (var i = 0; i < galaxy.Resources.Length && i < MaxResources; i++) mask |= 1u << i;
        return mask;
    }

    /// <summary>
    /// Whether <paramref name="radiusMetres"/> is within the diameter range. The diameter is twice the radius, as
    /// every readout words a rock: the figure the marks show.
    /// </summary>
    public bool WithinDiameter(double radiusMetres)
    {
        var diameter = radiusMetres * 2;
        return diameter >= MinMetres && (MaxMetres == 0 || diameter <= MaxMetres);
    }

    /// <summary>
    /// Whether a rock of this main ore (<paramref name="resource"/>, -1 for bare rock) and radius is one the pulse
    /// reports. The same rule server-side, in the pulse's candidate loop, and client-side for a preview.
    /// </summary>
    public bool Allows(int resource, double radiusMetres)
    {
        if (!WithinDiameter(radiusMetres)) return false;
        if (Ores == 0) return true;
        return (uint)resource < MaxResources && (Ores & (1u << resource)) != 0;
    }

    /// <summary>
    /// Whether a filter may be sent at all: a range that is the right way round, and only ores this galaxy has. A
    /// client that sends anything else is refused after admission, as any other bad request is.
    /// </summary>
    public bool Valid(Galaxy galaxy) =>
        MinMetres <= (MaxMetres == 0 ? MaxDiameterMetres : MaxMetres) && (Ores & ~AllOres(galaxy)) == 0;

    /// <summary>The filter with one resource's bit added or removed.</summary>
    public LocatorFilter WithOre(int resource, bool wanted)
    {
        var bit = BitOf(resource);
        return bit == 0 ? this : this with { Ores = wanted ? Ores | bit : Ores & ~bit };
    }

    /// <summary>Whether the filter names this resource's main ore.</summary>
    public bool HasOre(int resource) => (Ores & BitOf(resource)) != 0;

    /// <summary>
    /// The filter with one bound stepped, kept in order and inside what the controls reach.
    /// </summary>
    /// <param name="min">True to step the lower bound, false the upper one.</param>
    /// <param name="steps">How many steps to add; negative to lower the bound.</param>
    /// <param name="stepMetres">The size of one step, from the client's content.</param>
    /// <param name="capMetres">The widest diameter the controls reach, from the client's content.</param>
    public LocatorFilter Stepped(bool min, int steps, int stepMetres, int capMetres)
    {
        var cap = Math.Clamp(capMetres, 1, MaxDiameterMetres);
        var step = Math.Max(1, stepMetres);
        // A bound of zero is no bound: the lower one stops at the controls' widest, and the upper one steps down from
        // it. Stepping the lower bound leaves the upper one as it was, "ANY" included.
        var upper = MaxMetres == 0 ? cap : Math.Min(MaxMetres, cap);
        var lower = Math.Min(MinMetres, upper);
        return min
            ? this with { MinMetres = (ushort)Math.Clamp(lower + steps * step, 0, upper) }
            : this with { MaxMetres = (ushort)Math.Clamp(upper + steps * step, lower, cap) };
    }
}
