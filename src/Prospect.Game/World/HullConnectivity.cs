using System.Numerics;

namespace Prospect.Game.World;

/// <summary>
/// Finds face-connected live pieces without changing their block state. Working memory is reserved before
/// ticking and reused across hulls. Wedges connect by their bounding box faces, including their empty side;
/// edges, corners and slanted surfaces do not connect. Mounts belong to their parent block.
/// </summary>
public sealed class HullConnectivity
{
    private readonly int[] _pieces;
    private readonly int[] _queue;
    private readonly double[] _mass;
    private int _blocks;

    /// <summary>Reserves room for the largest design that will be examined.</summary>
    public HullConnectivity(int maxBlocks)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxBlocks);
        _pieces = new int[maxBlocks];
        _queue = new int[maxBlocks];
        _mass = new double[maxBlocks];
    }

    /// <summary>Piece number for each block, or -1 for a lost block. Valid until the next Find.</summary>
    public ReadOnlySpan<int> Pieces => _pieces.AsSpan(0, _blocks);

    /// <summary>Number of live pieces in the last Find.</summary>
    public int Count { get; private set; }

    /// <summary>
    /// Piece containing the lowest-index live core, otherwise the heaviest piece by evaluated dry mass,
    /// mounts included. Equal masses keep the piece with the lowest block index. Empty hulls return -1.
    /// </summary>
    public int Kept { get; private set; } = -1;

    /// <summary>Labels all live pieces in lowest-block-index order, using the design's existing spatial tree.</summary>
    public void Find(HullDesign design, ReadOnlySpan<ulong> alive)
    {
        if (design.BlockCount > _pieces.Length || alive.Length != BlockSet.WordCount(design.BlockCount))
            throw new ArgumentException("Connectivity needs reserved room and a complete block set.");
        _blocks = design.BlockCount;
        _pieces.AsSpan(0, _blocks).Fill(-1);
        _mass.AsSpan(0, _blocks).Clear();
        Count = 0;
        Kept = -1;
        var boxes = design.Boxes;
        for (var root = 0; root < _blocks; root++)
        {
            if (!BlockSet.Has(alive, root) || _pieces[root] >= 0) continue;
            var piece = Count++;
            _pieces[root] = piece;
            _queue[0] = root;
            var end = 1;
            for (var at = 0; at < end; at++)
            {
                var block = _queue[at];
                var center = design.BlockOffsets[block];
                var half = design.BlockHalfExtents[block];
                // Broaden only the tree query for float rounding. The final face test uses exact grid integers.
                var low = center - half;
                var high = center + half;
                var pad = new Vector3(design.BlockSize * 0.001f);
                low -= pad;
                high += pad;
                var cursor = 0;
                var visits = int.MaxValue;
                while (design.Collision.Next(ref cursor, low, high, alive, ref visits, out var other))
                {
                    if (_pieces[other] >= 0 || !SharesFace(boxes[block], boxes[other])) continue;
                    _pieces[other] = piece;
                    _queue[end++] = other;
                }
            }
        }

        // File order makes mass summation independent of the tree's traversal order.
        for (var block = 0; block < _blocks; block++)
        {
            var piece = _pieces[block];
            if (piece < 0) continue;
            _mass[piece] += design.Sums.BlockMass(block);
            if (Kept < 0 && BlockSet.Has(design.CoreSet, block)) Kept = piece;
        }
        if (Kept >= 0) return;
        for (var piece = 0; piece < Count; piece++)
            if (Kept < 0 || _mass[piece] > _mass[Kept]) Kept = piece;
    }

    /// <summary>
    /// True only for coincident bounding box faces with strictly positive overlap in both other axes. Edges are
    /// compared in quarter cells, the unit a block's place is whole in: a block set half a cell over still shares a
    /// face with the block it sits against, and two faces a quarter cell apart share none.
    /// </summary>
    public static bool SharesFace(in DesignBox a, in DesignBox b)
    {
        var x = Overlap(a, b, 0);
        var y = Overlap(a, b, 1);
        var z = Overlap(a, b, 2);
        return (Touches(a, b, 0) && y && z)
            || (Touches(a, b, 1) && x && z)
            || (Touches(a, b, 2) && x && y);
    }

    private static bool Overlap(in DesignBox a, in DesignBox b, int axis) =>
        Math.Max(a.Low4(axis), b.Low4(axis)) < Math.Min(a.High4(axis), b.High4(axis));

    private static bool Touches(in DesignBox a, in DesignBox b, int axis) =>
        a.High4(axis) == b.Low4(axis) || b.High4(axis) == a.Low4(axis);
}
