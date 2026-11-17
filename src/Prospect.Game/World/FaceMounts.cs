namespace Prospect.Game.World;

/// <summary>
/// How a block type sits on the face of another block instead of filling a grid cell, from <c>content/blocks</c>.
/// A type with one is a mount and is never a block; a type without one is a block and is never a mount.
/// </summary>
/// <param name="Side">The side of the square the part covers on the face, in tenths of a cell.</param>
/// <param name="Depth">How far it stands out from the face, in tenths of a cell.</param>
/// <param name="LegacyBlock">
/// The block type a design file older than version 5 gets where it has this part as a block: the cell stays, as
/// that type, and carries the part on the face it pointed from.
/// </param>
/// <param name="LastBlockVersion">
/// The newest design file version in which this part was still a block (<c>"blockThrough"</c> in
/// <c>blocks.json</c>): 4 for the thrusters, which became mounts in version 5, and 5 for the module scanners, which
/// became mounts in version 6. A file of that version or older names the part as a block and is converted on load.
/// </param>
public sealed record MountSpec(int Side, int Depth, string LegacyBlock, int LastBlockVersion = 4);

/// <summary>
/// One part mounted on a face of a block. <paramref name="Parent"/> is the block's index in the design,
/// <paramref name="Face"/> the side of it the part sits on and points out of, and <paramref name="U"/> and
/// <paramref name="V"/> the low corner of its square on that face, in tenths of a cell from the face's low corner
/// along the axes <c>(axis + 1) % 3</c> and <c>(axis + 2) % 3</c>. A mount has no state of its own: a hull has it
/// exactly while it has the parent block. <paramref name="Group"/> is the part's action group
/// (<see cref="ActionGroups"/>): 1 to 10, or 0 for none; only a groupable part (a mining laser) has one.
/// </summary>
public readonly record struct DesignMount(int Parent, BlockFacing Face, int U, int V, BlockType Type, byte Group = 0);

/// <summary>
/// The geometry and the rules of face mounts, shared by design files, received designs and the editor's drafts.
/// Everything is whole tenths of a cell on the blocks' integer edges, so fitting and overlap are exact.
/// </summary>
public static class FaceMounts
{
    /// <summary>Positions on a face are in this many steps a cell.</summary>
    public const int PerCell = 10;

    /// <summary>The largest side of a mount's square: one cell.</summary>
    public const int MaxSide = PerCell;

    /// <summary>
    /// The furthest a mount may stand out, in tenths of a cell. A shield shell stands 0.3 block off the blocks
    /// (<see cref="ShieldSurface"/>), so a mount this deep is always inside it.
    /// </summary>
    public const int MaxDepth = 2;

    /// <summary>
    /// The box a mount fills, on the integer block edges times <see cref="PerCell"/>: a block covers
    /// <c>[At, At + Size)</c> times ten there. Each span needs three slots.
    /// </summary>
    public static void Box(in DesignBox parent, in DesignMount mount, Span<int> low, Span<int> high)
    {
        var spec = mount.Type.Mount!;
        var n = Facings.Axis(mount.Face);
        var a = (n + 1) % 3;
        var b = (n + 2) % 3;
        if (Facings.Sign(mount.Face) > 0)
        {
            low[n] = (Of(parent.At, n) + Of(parent.Size, n)) * PerCell;
            high[n] = low[n] + spec.Depth;
        }
        else
        {
            high[n] = Of(parent.At, n) * PerCell;
            low[n] = high[n] - spec.Depth;
        }

        low[a] = Of(parent.At, a) * PerCell + mount.U;
        high[a] = low[a] + spec.Side;
        low[b] = Of(parent.At, b) * PerCell + mount.V;
        high[b] = low[b] + spec.Side;
    }

    /// <summary>
    /// Whether the mount's square lies on a whole face of its parent: a box's six sides, or the two full sides of a
    /// wedge. Its slant, its triangular caps and its missing side carry nothing.
    /// </summary>
    public static bool OnFace(in DesignBox parent, in DesignMount mount)
    {
        if (mount.Type?.Mount is not { } spec || !Facings.IsValid(mount.Face) || !BlockShapes.IsFullFace(parent.Shape, (int)mount.Face))
            return false;
        var n = Facings.Axis(mount.Face);
        return mount.U >= 0 && mount.V >= 0
            && mount.U + spec.Side <= Of(parent.Size, (n + 1) % 3) * PerCell
            && mount.V + spec.Side <= Of(parent.Size, (n + 2) % 3) * PerCell;
    }

    /// <summary>Whether a box in tenths shares volume with a block. Touching does not. A wedge counts as its whole box.</summary>
    public static bool Covers(in DesignBox block, ReadOnlySpan<int> low, ReadOnlySpan<int> high)
    {
        for (var axis = 0; axis < 3; axis++)
        {
            var from = Of(block.At, axis) * PerCell;
            if (low[axis] >= from + Of(block.Size, axis) * PerCell || high[axis] <= from)
                return false;
        }

        return true;
    }

    /// <summary>
    /// Whether one mount has room: its parent is there, its square lies on a whole face of it, no block covers the
    /// box it fills and no other mount shares volume with it. <paramref name="ignore"/> names a mount to pass over
    /// (the one being asked about, when it is in the list). Allocates nothing.
    /// </summary>
    public static DraftEdit Check(ReadOnlySpan<DesignBox> blocks, ReadOnlySpan<DesignMount> mounts, in DesignMount mount, int ignore = -1)
    {
        if ((uint)mount.Parent >= (uint)blocks.Length)
            return DraftEdit.NoSuchBlock;
        if (!OnFace(blocks[mount.Parent], mount))
            return DraftEdit.OffFace;
        Span<int> low = stackalloc int[3];
        Span<int> high = stackalloc int[3];
        Span<int> otherLow = stackalloc int[3];
        Span<int> otherHigh = stackalloc int[3];
        Box(blocks[mount.Parent], mount, low, high);
        foreach (ref readonly var block in blocks)
            if (Covers(block, low, high))
                return DraftEdit.Overlaps;
        for (var i = 0; i < mounts.Length; i++)
        {
            ref readonly var other = ref mounts[i];
            if (i == ignore || (uint)other.Parent >= (uint)blocks.Length || other.Type?.Mount is null)
                continue;
            Box(blocks[other.Parent], other, otherLow, otherHigh);
            if (low[0] < otherHigh[0] && otherLow[0] < high[0] && low[1] < otherHigh[1] && otherLow[1] < high[1] && low[2] < otherHigh[2] && otherLow[2] < high[2])
                return DraftEdit.Overlaps;
        }

        return DraftEdit.Done;
    }

    /// <summary>
    /// The order a design keeps its mounts in: by parent, then face, then V, then U. A design's bytes list them so,
    /// which gives one set of mounts one encoding and keeps a block's mounts together.
    /// </summary>
    public static int Compare(DesignMount a, DesignMount b)
    {
        var c = a.Parent.CompareTo(b.Parent);
        if (c == 0) c = ((byte)a.Face).CompareTo((byte)b.Face);
        if (c == 0) c = a.V.CompareTo(b.V);
        if (c == 0) c = a.U.CompareTo(b.U);
        return c != 0 ? c : string.CompareOrdinal(a.Type.Id, b.Type.Id);
    }

    /// <summary>
    /// Holds a design's mounts to the rules: a mount type each, within the content's count, each on a whole open
    /// face of a block that is there, none sharing volume with a block or another mount. Refuses with the first
    /// that breaks one.
    /// </summary>
    public static void Validate(ReadOnlySpan<DesignBox> blocks, ReadOnlySpan<DesignMount> mounts, DesignLimits limits, string source)
    {
        if (mounts.Length > limits.MaxMounts)
            throw new InvalidDataException($"{source}: mounts: too many mounted parts (limit {limits.MaxMounts})");
        for (var i = 0; i < mounts.Length; i++)
        {
            ref readonly var mount = ref mounts[i];
            if (mount.Type?.Mount is null)
                throw new InvalidDataException($"{source}: mount {i}: a {mount.Type?.Name ?? "part of no type"} is a block and cannot be mounted on a face");
            if (!ActionGroups.Allowed(mount.Type, mount.Group))
                throw new InvalidDataException($"{source}: mount {i}: a {mount.Type.Name} cannot be in action group {mount.Group}");
            switch (Check(blocks, mounts[..i], mount))
            {
                case DraftEdit.Done:
                    break;
                case DraftEdit.NoSuchBlock:
                    throw new InvalidDataException($"{source}: mount {i}: there is no block {mount.Parent} to mount it on");
                case DraftEdit.OffFace:
                    throw new InvalidDataException($"{source}: mount {i}: it does not fit on that face of block {mount.Parent}");
                default:
                    throw new InvalidDataException($"{source}: mount {i}: a block or another mounted part is in its way");
            }
        }
    }

    internal static int Of(GridSize g, int axis) => axis switch { 0 => g.X, 1 => g.Y, _ => g.Z };
}
