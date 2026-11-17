namespace Prospect.Game.World;

/// <summary>
/// What one ore bin of a hull holds: raw ore of one resource, in kilograms of everything that came off the rock (ore
/// and the rock it sits in), and the share of that mass that is the ore, weighted by mass over everything put in. An
/// empty bin holds nothing and takes any ore; its resource is -1.
/// </summary>
public struct OreBin
{
    /// <summary>Kilograms held, from nothing to the bin's capacity.</summary>
    public double Kg;

    /// <summary>The galaxy's index of the ore, or -1 while the bin is empty.</summary>
    public int Resource;

    /// <summary>The share of <see cref="Kg"/> that is the ore, above 0 and at most 1; zero while the bin is empty.</summary>
    public float Grade;

    /// <summary>The bin that holds nothing.</summary>
    public static OreBin Empty => new() { Resource = -1 };

    /// <summary>True while the bin holds nothing, whatever else its fields say.</summary>
    public readonly bool IsEmpty => !(Kg > 0);

    /// <summary>Kilograms of the ore itself.</summary>
    public readonly double OreKg => IsEmpty ? 0 : Kg * Grade;
}

/// <summary>One ore bin of a design: the block it is and the kilograms it holds when full, from its volume.</summary>
public readonly record struct DesignBin(int Block, double Capacity);

/// <summary>
/// The shared rules of ore bins: how much room a hull has for an ore, which bins a delivery goes into, what a bin may
/// hold on the wire and in a save, and what an edit keeps. Plain arithmetic in bin order, so the server and a client
/// that is told the bins agree. Takes no memory.
/// </summary>
public static class OreBins
{
    /// <summary>The most bins a design may have: the pilot is told of every one, and scratch for them lives on the stack.</summary>
    public const int MaxBins = 64;

    /// <summary>The most hoppers a design may have.</summary>
    public const int MaxHoppers = 16;

    /// <summary>Kilograms under which what is left of a delivery counts as nothing: a chunk never lingers for a gram.</summary>
    public const double Crumb = 1e-3;

    /// <summary>
    /// Kilograms of <paramref name="resource"/> the hull's bins still take: what is left in the live bins that hold
    /// it, and every live empty bin whole.
    /// </summary>
    public static double Room(ReadOnlySpan<OreBin> bins, ReadOnlySpan<DesignBin> design, ReadOnlySpan<ulong> alive, int resource)
    {
        var room = 0.0;
        for (var i = 0; i < bins.Length; i++)
        {
            if (!BlockSet.Has(alive, design[i].Block)) continue;
            if (bins[i].IsEmpty) room += design[i].Capacity;
            else if (bins[i].Resource == resource) room += Math.Max(0, design[i].Capacity - bins[i].Kg);
        }

        return room;
    }

    /// <summary>
    /// Puts <paramref name="kg"/> of <paramref name="resource"/> at <paramref name="grade"/> into the bins: first the
    /// live bins that hold that ore, in design order, then the live empty ones; what one bin cannot take spills into
    /// the next. A bin's grade stays the mass-weighted share of everything in it. Returns the kilograms taken.
    /// </summary>
    public static double Add(Span<OreBin> bins, ReadOnlySpan<DesignBin> design, ReadOnlySpan<ulong> alive, int resource, double kg, float grade)
    {
        if (resource < 0 || !(kg > 0) || !(grade > 0) || grade > 1) return 0;
        var left = kg;
        for (var pass = 0; pass < 2 && left > 0; pass++)
        {
            for (var i = 0; i < bins.Length && left > 0; i++)
            {
                ref var bin = ref bins[i];
                if (!BlockSet.Has(alive, design[i].Block)) continue;
                if (pass == 0 ? bin.IsEmpty || bin.Resource != resource : !bin.IsEmpty) continue;
                var take = Math.Min(left, design[i].Capacity - (bin.IsEmpty ? 0 : bin.Kg));
                if (!(take > 0)) continue;
                var held = bin.IsEmpty ? 0 : bin.Kg;
                var ore = held * bin.Grade + take * grade;
                bin.Kg = held + take;
                bin.Resource = resource;
                bin.Grade = (float)Math.Clamp(ore / bin.Kg, 1e-6, 1);
                left -= take;
            }
        }

        return kg - left;
    }

    /// <summary>
    /// Whether <paramref name="bins"/> is what a hull of <paramref name="design"/> with the blocks in
    /// <paramref name="alive"/> could hold: one record a bin, each empty (nothing, resource -1, grade 0) or an ore of
    /// the <paramref name="resources"/> the galaxy has, more than nothing and at most the bin's capacity, at a grade
    /// above 0 and at most 1, and nothing in a bin whose block is gone. The one rule for the wire and a save.
    /// </summary>
    public static bool Valid(ReadOnlySpan<OreBin> bins, HullDesign design, ReadOnlySpan<ulong> alive, int resources)
    {
        var shape = design.Bins;
        if (bins.Length != shape.Length) return false;
        for (var i = 0; i < bins.Length; i++)
        {
            ref readonly var bin = ref bins[i];
            if (bin.Kg == 0)
            {
                if (bin.Resource != -1 || bin.Grade != 0) return false;
                continue;
            }

            // Written so that a value that is no number fails it. A capacity is worked out in double precision on both
            // sides from the same content, so the bound is exact.
            if (!(bin.Kg > 0 && bin.Kg <= shape[i].Capacity) || (uint)bin.Resource >= (uint)resources
                || !(bin.Grade > 0 && bin.Grade <= 1) || !BlockSet.Has(alive, shape[i].Block)) return false;
        }

        return true;
    }

    /// <summary>
    /// What a hull replaced by an edit or a repair keeps of its ore. Each old bin's ore goes first into the new bins
    /// its box overlaps (in the shared grid, by overlap volume, largest share first come), where a new bin takes one
    /// ore only; what does not fit there is put into the other new bins by <see cref="Add"/>. Fills
    /// <paramref name="after"/> and returns the kilograms that found no room and are dumped.
    /// </summary>
    public static double Transfer(HullDesign from, ReadOnlySpan<OreBin> before, HullDesign to, Span<OreBin> after)
    {
        after.Fill(OreBin.Empty);
        Span<double> spill = stackalloc double[MaxBins];
        var old = from.Bins;
        var next = to.Bins;
        for (var i = 0; i < old.Length && i < before.Length; i++)
        {
            spill[i] = 0;
            ref readonly var held = ref before[i];
            if (held.IsEmpty || held.Resource < 0) continue;
            var total = 0.0;
            for (var j = 0; j < next.Length; j++) total += Overlap(from.Boxes[old[i].Block], to.Boxes[next[j].Block]);
            var left = held.Kg;
            for (var j = 0; j < next.Length && total > 0; j++)
            {
                var overlap = Overlap(from.Boxes[old[i].Block], to.Boxes[next[j].Block]);
                if (overlap == 0) continue;
                ref var bin = ref after[j];
                if (!bin.IsEmpty && bin.Resource != held.Resource) continue;
                var kept = bin.IsEmpty ? 0 : bin.Kg;
                var take = Math.Min(Math.Min(left, held.Kg * (overlap / total)), next[j].Capacity - kept);
                if (!(take > 0)) continue;
                var ore = kept * bin.Grade + take * held.Grade;
                bin.Kg = kept + take;
                bin.Resource = held.Resource;
                bin.Grade = (float)Math.Clamp(ore / bin.Kg, 1e-6, 1);
                left -= take;
            }

            spill[i] = Math.Max(0, left);
        }

        var dumped = 0.0;
        for (var i = 0; i < old.Length && i < before.Length; i++)
        {
            if (!(spill[i] > 0)) continue;
            dumped += spill[i] - Add(after, next, to.FullSet, before[i].Resource, spill[i], before[i].Grade);
        }

        return dumped;
    }

    private static double Overlap(in DesignBox a, in DesignBox b) =>
        (double)Math.Max(0, Math.Min(a.At.X + a.Size.X, b.At.X + b.Size.X) - Math.Max(a.At.X, b.At.X)) *
        Math.Max(0, Math.Min(a.At.Y + a.Size.Y, b.At.Y + b.Size.Y) - Math.Max(a.At.Y, b.At.Y)) *
        Math.Max(0, Math.Min(a.At.Z + a.Size.Z, b.At.Z + b.Size.Z) - Math.Max(a.At.Z, b.At.Z));
}
