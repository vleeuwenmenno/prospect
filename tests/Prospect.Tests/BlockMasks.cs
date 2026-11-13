using Prospect.Game.Client;
using Prospect.Game.World;
using Xunit;

namespace Prospect.Tests;

/// <summary>
/// A block set as the single 64-bit mask it is for a design the loader admits. The shipped designs all fit one
/// word, and tests of them read better against one number; each helper checks that the set really is one word,
/// so none of them can quietly drop blocks of a larger design.
/// </summary>
internal static class BlockMasks
{
    /// <summary>A one-word block set as its mask.</summary>
    public static ulong Word(ReadOnlySpan<ulong> set)
    {
        Assert.Equal(1, set.Length);
        return set[0];
    }

    /// <summary>A mask as a one-word block set in memory of its own, for a view or a pick.</summary>
    public static ReadOnlyMemory<ulong> Set(ulong mask) => new[] { mask };

    /// <summary>Every block of the design.</summary>
    public static ulong FullMask(this HullDesign design) => Word(design.FullSet);

    /// <summary>The blocks whose loss destroys the hull.</summary>
    public static ulong CoreMask(this HullDesign design) => Word(design.CoreSet);

    /// <summary>The blocks a stored hull still has.</summary>
    public static ulong Mask(this HullStore store, int index) => Word(store.Blocks(index));

    /// <summary>The blocks a published view shows.</summary>
    public static ulong Mask(this in HullView view) => Word(view.Blocks.Span);

    /// <summary>
    /// Takes blocks from a stored hull as damage would, counting each loss, so the field sees them at its next refit.
    /// </summary>
    public static void Lose(this HullStore store, int index, ulong blocks)
    {
        for (var b = 0; b < 64; b++)
        {
            if ((blocks & 1UL << b) != 0)
                store.BlockStates.Lose(store.Info(index).BlockState, b);
        }
    }

    /// <summary>
    /// The figures of a hull of the design with the blocks of <paramref name="alive"/>, from sums built afresh
    /// for that set: what a hull's running sums must agree with, to the last bit, however it came to have them.
    /// </summary>
    public static void Evaluate(this HullDesign design, ReadOnlySpan<ulong> alive, out FlightParams flight, out HullStats stats)
    {
        var sums = new HullSum[design.Sums.NodeCount];
        design.Sums.Build(sums, alive);
        design.Figures(sums, out flight, out stats);
    }

    /// <summary>The data sheet of a hull of the design with these blocks, as a session publishes it with the hull's view.</summary>
    public static HullStats StatsOf(this HullDesign design, ReadOnlySpan<ulong> alive)
    {
        design.Evaluate(alive, out _, out var stats);
        return stats;
    }

    /// <summary>
    /// A body that takes damage: over a block state of its own, with every block's hit points, in a store the
    /// test keeps. What it has left is read back through <paramref name="states"/> and the body's state.
    /// </summary>
    public static HullBody Body(this BlockStateStore states, HullDesign design)
    {
        var state = states.Create(design, hitPoints: true);
        return new HullBody { Design = design, Alive = states.Alive(state), States = states, State = state, Scratch = design.Scratch() };
    }

    /// <summary>What a hull of the design brings to a step that can run into something.</summary>
    public static ContactScratch Scratch(this HullDesign design) => new(design.BlockCount);
}
