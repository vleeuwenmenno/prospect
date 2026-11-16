namespace Prospect.Game.World;

/// <summary>Per-fuel quantities before and after a refit, including pumping and loss.</summary>
public readonly record struct EditFuelPlan(FuelTanks Before, FuelTanks After, FuelTanks Moved, FuelTanks Dumped);

/// <summary>Fuel retained by an edit in the unchanged design grid. Never allocates.</summary>
public static class EditFuel
{
    /// <summary>
    /// Pools fuel within connected groups of overlapping old and new tanks of the same fuel.
    /// Shares follow overlap volume; capped shares are offered again to the group's remaining tanks.
    /// Tank order fixes all double arithmetic. Returns dumped kilograms per fuel after pumping and replaces output levels.
    /// </summary>
    public static FuelTanks Transfer(HullDesign from, ReadOnlySpan<double> held, HullDesign to, Span<double> levels)
        => Plan(from, held, to, levels).Dumped;

    /// <summary>
    /// Preserves overlapping fuel, then pumps surplus into same-fuel capacity in block order.
    /// Excluded blocks preview removing selected tanks without constructing another design.
    /// </summary>
    public static EditFuelPlan Plan(HullDesign from, ReadOnlySpan<double> held, HullDesign to, Span<double> levels,
        ReadOnlySpan<int> excludedBlocks = default)
    {
        levels.Clear();
        Span<double> dumped = stackalloc double[Fuels.Count + 1];
        dumped.Clear();
        Span<int> groups = stackalloc int[TankFuel.MaxTanks * 2];
        Span<double> weights = stackalloc double[TankFuel.MaxTanks];
        weights.Clear();
        var count = from.Tanks.Length + to.Tanks.Length;
        for (var i = 0; i < count; i++) groups[i] = i;
        for (var i = 0; i < from.Tanks.Length; i++)
            for (var j = 0; j < to.Tanks.Length; j++)
            {
                if (from.Tanks[i].Kind != to.Tanks[j].Kind || excludedBlocks.Contains(to.Tanks[j].Block)) continue;
                var volume = Overlap(from.Boxes[from.Tanks[i].Block], to.Boxes[to.Tanks[j].Block]);
                if (volume == 0) continue;
                weights[j] += volume;
                var a = Root(groups, i);
                var b = Root(groups, from.Tanks.Length + j);
                groups[Math.Max(a, b)] = Math.Min(a, b);
            }
        for (var i = 0; i < count; i++) groups[i] = Root(groups, i);
        for (var group = 0; group < from.Tanks.Length; group++)
        {
            if (groups[group] != group) continue;
            double remaining = 0;
            for (var i = 0; i < from.Tanks.Length; i++)
                if (groups[i] == group) remaining += Math.Clamp(held[i], 0, from.Tanks[i].Capacity);
            for (var pass = 0; pass <= to.Tanks.Length && remaining > 0; pass++)
            {
                double total = 0;
                var last = -1;
                for (var j = 0; j < to.Tanks.Length; j++)
                    if (groups[from.Tanks.Length + j] == group && levels[j] < to.Tanks[j].Capacity)
                    {
                        total += weights[j];
                        last = j;
                    }
                if (last < 0) break;
                var offered = remaining;
                var capped = false;
                for (var j = 0; j <= last; j++)
                {
                    var room = to.Tanks[j].Capacity - levels[j];
                    if (groups[from.Tanks.Length + j] != group || room <= 0) continue;
                    var share = j == last && !capped ? remaining : offered * (weights[j] / total);
                    var take = Math.Min(remaining, Math.Min(room, share));
                    capped |= share > room;
                    levels[j] += take;
                    remaining -= take;
                }
                if (!capped) break;
            }
            dumped[(int)from.Tanks[group].Kind] += remaining;
        }
        Span<double> moved = stackalloc double[Fuels.Count + 1];
        moved.Clear();
        for (var j = 0; j < to.Tanks.Length; j++)
        {
            if (excludedBlocks.Contains(to.Tanks[j].Block)) continue;
            var fuel = (int)to.Tanks[j].Kind;
            var take = Math.Min(dumped[fuel], to.Tanks[j].Capacity - levels[j]);
            levels[j] += take;
            dumped[fuel] -= take;
            moved[fuel] += take;
        }
        return new EditFuelPlan(TankFuel.Total(held, from.Tanks), TankFuel.Total(levels, to.Tanks),
            TankFuel.Fuel(moved), TankFuel.Fuel(dumped));
    }

    private static int Root(ReadOnlySpan<int> groups, int i)
    {
        while (groups[i] != i) i = groups[i];
        return i;
    }

    private static double Overlap(in DesignBox a, in DesignBox b) =>
        (double)Math.Max(0, Math.Min(a.At.X + a.Size.X, b.At.X + b.Size.X) - Math.Max(a.At.X, b.At.X)) *
        Math.Max(0, Math.Min(a.At.Y + a.Size.Y, b.At.Y + b.Size.Y) - Math.Max(a.At.Y, b.At.Y)) *
        Math.Max(0, Math.Min(a.At.Z + a.Size.Z, b.At.Z + b.Size.Z) - Math.Max(a.At.Z, b.At.Z));
}
