using System.Text;
using Prospect.Game.World;

namespace Prospect.Client.World;

/// <summary>What the flown hull's ore bins would do with a loose chunk, and when they cannot take it, why.</summary>
internal enum OreIntakeState : byte
{
    /// <summary>There is room for the whole chunk, in the bins that hold its ore or in empty ones.</summary>
    Fits = 0,

    /// <summary>The live bins have no room for it in kilograms at all.</summary>
    BinsFull = 1,

    /// <summary>
    /// There is room in kilograms, but only in bins that hold another ore, and no bin is empty: a bin takes one ore
    /// until it is emptied.
    /// </summary>
    OtherOres = 2,

    /// <summary>The hull has no ore bins, or no hopper to take a chunk in, so a chunk has nowhere to go.</summary>
    NoBins = 3,
}

/// <summary>
/// How many chunks near the flown hull the bins can take, and how many they cannot and why. Counted over the near
/// radius the DRONES chip already uses, one bucket each, so <see cref="Near"/> is <see cref="Fit"/> +
/// <see cref="Full"/> + <see cref="Other"/> + <see cref="None"/>.
/// </summary>
internal readonly record struct OreIntakeCounts(int Near, int Fit, int Full, int Other, int None)
{
    /// <summary>True when some near chunk cannot be taken, for want of room, of a bin holding its ore, or of any bin.</summary>
    public bool Blocked => Full > 0 || Other > 0 || None > 0;
}

/// <summary>
/// What the flown hull's bins can take, apart from any window: one answer for a chunk from its ore and mass, and one
/// pass over the chunks near the ship. The room it reports is the server's own rule (<see cref="OreBins.Room"/>), so a
/// client says exactly what the server will do and never a second version of it. Takes no memory.
/// </summary>
internal static class OreIntake
{
    /// <summary>How many of the ores the bins hold a reason line names before it counts the rest.</summary>
    public const int NamedOres = 2;

    /// <summary>The longest ore name a reason line writes, in characters.</summary>
    private const int MaxName = 32;

    /// <summary>
    /// What the hull's bins would do with a chunk of <paramref name="resource"/> weighing <paramref name="kg"/>:
    /// <see cref="OreIntakeState.NoBins"/> without a hopper or a bin, <see cref="OreIntakeState.Fits"/> when the shared
    /// room rule has room for all of it, else <see cref="OreIntakeState.BinsFull"/> when no bin has room for it in
    /// kilograms, else <see cref="OreIntakeState.OtherOres"/>.
    /// </summary>
    public static OreIntakeState State(ReadOnlySpan<OreBin> bins, ReadOnlySpan<DesignBin> design, ReadOnlySpan<ulong> alive, int hoppers, int resource, double kg)
    {
        // A client that has not been told of the hull's bins yet knows of none, and a lost block is no bin.
        var known = Math.Min(bins.Length, design.Length);
        if (known == 0 || hoppers == 0 || alive.IsEmpty || !(kg > 0)) return OreIntakeState.NoBins;
        bins = bins[..known];
        design = design[..known];
        if (OreBins.Room(bins, design, alive, resource) >= kg) return OreIntakeState.Fits;

        // What every live bin would take of any ore at all: an empty bin whole, a filled one what is left of it.
        var free = 0.0;
        for (var i = 0; i < known; i++)
        {
            if (!BlockSet.Has(alive, design[i].Block)) continue;
            var held = bins[i].IsEmpty ? 0 : bins[i].Kg;
            free += Math.Max(0, design[i].Capacity - held);
        }

        return free >= kg ? OreIntakeState.OtherOres : OreIntakeState.BinsFull;
    }

    /// <summary>Counts the chunks within <paramref name="nearMetres"/> of the ship by the state their markers carry.</summary>
    public static OreIntakeCounts Count(ReadOnlySpan<OreMarker> markers, float nearMetres)
    {
        var near = 0;
        var fit = 0;
        var full = 0;
        var other = 0;
        var none = 0;
        for (var i = 0; i < markers.Length; i++)
        {
            ref readonly var marker = ref markers[i];
            if (!(marker.OwnDistance <= nearMetres)) continue;
            near++;
            switch (marker.Intake)
            {
                case OreIntakeState.Fits: fit++; break;
                case OreIntakeState.BinsFull: full++; break;
                case OreIntakeState.OtherOres: other++; break;
                default: none++; break;
            }
        }

        return new OreIntakeCounts(near, fit, full, other, none);
    }

    /// <summary>
    /// The ore of the nearest chunk within <paramref name="nearMetres"/> that no bin has room for because the bins
    /// hold other ores, or -1 when there is none. It is what the "no bin for" notice names.
    /// </summary>
    public static int NearestOtherOres(ReadOnlySpan<OreMarker> markers, float nearMetres)
    {
        var best = -1;
        var nearest = float.PositiveInfinity;
        for (var i = 0; i < markers.Length; i++)
        {
            ref readonly var marker = ref markers[i];
            if (marker.Intake != OreIntakeState.OtherOres || !(marker.OwnDistance <= nearMetres) || marker.OwnDistance >= nearest) continue;
            best = marker.Resource;
            nearest = marker.OwnDistance;
        }

        return best;
    }

    /// <summary>
    /// Writes the one reason line a blocked chunk's tooltip adds, in UTF-8, and returns its length: "BINS FULL",
    /// "NO ORE BINS", or "NO BIN FOR &lt;ORE&gt;". None for a chunk that fits.
    /// </summary>
    public static int WriteReason(Span<byte> utf8, OreIntakeState state, Galaxy galaxy, int resource)
    {
        return state switch
        {
            OreIntakeState.BinsFull => Literal(utf8, "BINS FULL"u8),
            OreIntakeState.NoBins => Literal(utf8, "NO ORE BINS"u8),
            OreIntakeState.OtherOres => Name(utf8, "NO BIN FOR "u8, galaxy, resource),
            _ => 0,
        };
    }

    /// <summary>
    /// Writes the ores keeping a chunk out, in UTF-8, and returns the length: "BINS HOLD &lt;ORE&gt;, &lt;ORE&gt;",
    /// with at most <see cref="NamedOres"/> of them named and " +N" for the rest. Empty when the bins are not what keeps
    /// it out, or when every bin that would take it is empty.
    /// </summary>
    public static int WriteHeld(Span<byte> utf8, Galaxy galaxy, int resource, ReadOnlySpan<OreBin> bins, ReadOnlySpan<DesignBin> design, ReadOnlySpan<ulong> alive)
    {
        if (alive.IsEmpty) return 0;
        var known = Math.Min(bins.Length, design.Length);
        var at = Literal(utf8, "BINS HOLD "u8);
        if (at == 0) return 0;
        var named = 0;
        var rest = 0;
        for (var i = 0; i < known; i++)
        {
            if (!Holds(design, alive, bins, i) || bins[i].Resource == resource) continue;
            // One name an ore, in the order the bins hold them: the first bin of an ore names it.
            var seen = false;
            for (var j = 0; j < i && !seen; j++)
                seen = Holds(design, alive, bins, j) && bins[j].Resource == bins[i].Resource;
            if (seen) continue;
            if (named == NamedOres)
            {
                rest++;
                continue;
            }

            if (named > 0) at += Literal(utf8[at..], ", "u8);
            at += Name(utf8[at..], default, galaxy, bins[i].Resource);
            named++;
        }

        if (named == 0) return 0;
        return rest > 0 ? at + Count(utf8[at..], rest) : at;
    }

    // True when the bin is live and still has room, which is what makes it one that keeps a chunk of another ore out.
    private static bool Holds(ReadOnlySpan<DesignBin> design, ReadOnlySpan<ulong> alive, ReadOnlySpan<OreBin> bins, int bin) =>
        BlockSet.Has(alive, design[bin].Block) && !bins[bin].IsEmpty && design[bin].Capacity - bins[bin].Kg > 0;

    private static int Literal(Span<byte> utf8, ReadOnlySpan<byte> text)
    {
        if (text.Length > utf8.Length) return 0;
        text.CopyTo(utf8);
        return text.Length;
    }

    private static int Name(Span<byte> utf8, ReadOnlySpan<byte> prefix, Galaxy galaxy, int resource)
    {
        var at = prefix.Length;
        if (at > utf8.Length) return 0;
        prefix.CopyTo(utf8);
        if ((uint)resource >= (uint)galaxy.Resources.Length) return at;
        // The tooltip and the chip are set in upper-case captions, so a name in them is written as one; the notices say
        // it as the content has it.
        var name = galaxy.Resources[resource].Name.AsSpan();
        if (name.Length > MaxName) name = name[..MaxName];
        Span<char> upper = stackalloc char[MaxName];
        name.ToUpperInvariant(upper);
        return at + Text(utf8[at..], upper[..name.Length]);
    }

    private static int Count(Span<byte> utf8, int count)
    {
        var at = Literal(utf8, " +"u8);
        Span<char> digits = stackalloc char[11];
        if (!count.TryFormat(digits, out var written, provider: System.Globalization.CultureInfo.InvariantCulture)) return at;
        return at + Text(utf8[at..], digits[..written]);
    }

    /// <summary>Writes as much of <paramref name="text"/> as fits, whole characters only, and returns the bytes written.</summary>
    private static int Text(Span<byte> utf8, ReadOnlySpan<char> text)
    {
        var length = text.Length;
        while (length > 0 && Encoding.UTF8.GetByteCount(text[..length]) > utf8.Length) length--;
        return length == 0 ? 0 : Encoding.UTF8.GetBytes(text[..length], utf8);
    }
}
