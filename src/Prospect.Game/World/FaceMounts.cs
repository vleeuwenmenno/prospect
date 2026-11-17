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
public sealed record MountSpec(int Side, int Depth, string LegacyBlock, int LastBlockVersion = 4)
{
    /// <summary>
    /// The sizes the part may be placed in (<c>"sizes"</c> in <c>blocks.json</c>), rising: a part of size n is a
    /// square of n by n units (a cold gas array's nozzles), <see cref="Side"/> times n on a side, and every figure of
    /// its type counts n times n. A part that names none has the one size 1: the type's figures are the part's.
    /// </summary>
    public IReadOnlyList<int> Sizes { get; init; } = [1];

    /// <summary>
    /// The size a part has where none is named (<c>"defaultSize"</c>): in a design file that lists no size, in an old
    /// file converted on load, and the size the editor first offers.
    /// </summary>
    public int DefaultSize { get; init; } = 1;

    /// <summary>The largest size a part of any type may have: a size is one byte, and a square of 15 units is ample.</summary>
    public const int MaxSize = 15;

    /// <summary>True for a part placed in more than one size.</summary>
    public bool Sizable => Sizes.Count > 1;

    /// <summary>Whether a part of this type may have <paramref name="size"/>. Allocates nothing.</summary>
    public bool Allows(int size)
    {
        for (var i = 0; i < Sizes.Count; i++)
            if (Sizes[i] == size) return true;
        return false;
    }

    /// <summary>The side of the square a part of <paramref name="size"/> covers on its face, in tenths of a cell.</summary>
    public int SideOf(int size) => Side * size;

    /// <summary>The largest side a part of this type covers, in tenths of a cell.</summary>
    public int LargestSide => SideOf(Sizes[Sizes.Count - 1]);
}

/// <summary>
/// One part mounted on a face of a block. <paramref name="Parent"/> is the block's index in the design,
/// <paramref name="Face"/> the side of it the part sits on and points out of, and <paramref name="U"/> and
/// <paramref name="V"/> the low corner of its square on that face, in tenths of a cell from the face's low corner
/// along the axes <c>(axis + 1) % 3</c> and <c>(axis + 2) % 3</c>. A mount has no state of its own: a hull has it
/// exactly while it has the parent block. <paramref name="Group"/> is the part's action group
/// (<see cref="ActionGroups"/>): 1 to 10, or 0 for none; only a groupable part (a mining laser) has one.
/// <paramref name="Size"/> is how many units the part is on a side (<see cref="MountSpec.Sizes"/>): 1 for every part
/// but one placed larger, such as a cold gas array of 2 by 2 or 3 by 3 nozzles. Its square is the type's side times
/// the size, and its thrust, burn, power and mass are the type's times the size squared (<see cref="FaceMounts.Units"/>).
/// </summary>
public readonly record struct DesignMount(int Parent, BlockFacing Face, int U, int V, BlockType Type, byte Group = 0, byte Size = 1);

/// <summary>
/// The geometry and the rules of face mounts, shared by design files, received designs and the editor's drafts.
/// A mount's place on its face is whole tenths of a cell from the face's low corner, and a block's place is whole
/// quarters of a cell (<see cref="GridPoint"/>), so the boxes compared here are in twentieths (<see cref="Fine"/>),
/// the smallest step both are whole in: fitting and overlap are exact wherever the block sits.
/// </summary>
public static class FaceMounts
{
    /// <summary>Positions on a face are in this many steps a cell.</summary>
    public const int PerCell = 10;

    /// <summary>
    /// Steps a cell of the boxes <see cref="Box"/> gives and <see cref="Covers"/> takes: twentieths, in which a
    /// block's quarter cells (five each) and a mount's tenths (two each) are both whole.
    /// </summary>
    public const int Fine = 20;

    private const int FinePerQuarter = Fine / GridPoint.PerCell;
    private const int FinePerTenth = Fine / PerCell;

    /// <summary>The largest side of a mount's square: one cell.</summary>
    public const int MaxSide = PerCell;

    /// <summary>
    /// The furthest a mount may stand out, in tenths of a cell. A shield shell stands 0.3 block off the blocks
    /// (<see cref="ShieldSurface"/>), so a mount this deep is always inside it.
    /// </summary>
    public const int MaxDepth = 2;

    /// <summary>The side of the square a mount covers on its face, in tenths of a cell: its type's side times its size.</summary>
    public static int Side(in DesignMount mount) => mount.Type.Mount!.SideOf(mount.Size);

    /// <summary>
    /// How many times a mount counts its type's figures: its size squared, the nozzles of a cold gas array. One for
    /// every part of size 1.
    /// </summary>
    public static int Units(in DesignMount mount) => mount.Size * mount.Size;

    /// <summary>The size a part of <paramref name="type"/> has where none is named; 1 for a type that is no mount.</summary>
    public static byte DefaultSize(BlockType? type) => (byte)(type?.Mount?.DefaultSize ?? 1);

    /// <summary>Whether a part of <paramref name="type"/> may have <paramref name="size"/>: the one rule of a mount's size.</summary>
    public static bool SizeAllowed(BlockType? type, int size) => type?.Mount is { } spec && spec.Allows(size);

    /// <summary>
    /// The box a mount fills, on the grid's edges in twentieths of a cell (<see cref="Fine"/>): a block covers
    /// <c>[Low4, High4)</c> times five there. Each span needs three slots.
    /// </summary>
    public static void Box(in DesignBox parent, in DesignMount mount, Span<int> low, Span<int> high)
    {
        var spec = mount.Type.Mount!;
        var side = spec.SideOf(mount.Size) * FinePerTenth;
        var n = Facings.Axis(mount.Face);
        var a = (n + 1) % 3;
        var b = (n + 2) % 3;
        if (Facings.Sign(mount.Face) > 0)
        {
            low[n] = parent.High4(n) * FinePerQuarter;
            high[n] = low[n] + spec.Depth * FinePerTenth;
        }
        else
        {
            high[n] = parent.Low4(n) * FinePerQuarter;
            low[n] = high[n] - spec.Depth * FinePerTenth;
        }

        low[a] = parent.Low4(a) * FinePerQuarter + mount.U * FinePerTenth;
        high[a] = low[a] + side;
        low[b] = parent.Low4(b) * FinePerQuarter + mount.V * FinePerTenth;
        high[b] = low[b] + side;
    }

    /// <summary>
    /// A mount's place on a face (<see cref="DesignMount.U"/> or <see cref="DesignMount.V"/>, tenths of a cell from
    /// the face's low corner) for a square whose low edge along <paramref name="axis"/> is <paramref name="fine"/>, a
    /// coordinate of <see cref="Box"/>. The edge must be whole tenths from the block's corner, as a mount's is.
    /// </summary>
    public static int Place(in DesignBox parent, int axis, int fine) => (fine - parent.Low4(axis) * FinePerQuarter) / FinePerTenth;

    /// <summary>
    /// Whether the mount's square lies on a whole face of its parent: a box's six sides, or the two full sides of a
    /// wedge. Its slant, its triangular caps and its missing side carry nothing.
    /// </summary>
    public static bool OnFace(in DesignBox parent, in DesignMount mount)
    {
        if (mount.Type?.Mount is not { } spec || !spec.Allows(mount.Size) || !Facings.IsValid(mount.Face) || !BlockShapes.IsFullFace(parent.Shape, (int)mount.Face))
            return false;
        var side = spec.SideOf(mount.Size);
        var n = Facings.Axis(mount.Face);
        return mount.U >= 0 && mount.V >= 0
            && mount.U + side <= Of(parent.Size, (n + 1) % 3) * PerCell
            && mount.V + side <= Of(parent.Size, (n + 2) % 3) * PerCell;
    }

    /// <summary>Whether a box in twentieths (<see cref="Fine"/>) shares volume with a block. Touching does not. A wedge counts as its whole box.</summary>
    public static bool Covers(in DesignBox block, ReadOnlySpan<int> low, ReadOnlySpan<int> high)
    {
        for (var axis = 0; axis < 3; axis++)
        {
            if (low[axis] >= block.High4(axis) * FinePerQuarter || high[axis] <= block.Low4(axis) * FinePerQuarter)
                return false;
        }

        return true;
    }

    /// <summary>
    /// Whether one mount has room: its parent is there, its size is one its type may have
    /// (<see cref="DraftEdit.NoSize"/> otherwise), its square lies on a whole face of it, no block covers the
    /// box it fills and no other mount shares volume with it. <paramref name="ignore"/> names a mount to pass over
    /// (the one being asked about, when it is in the list). Allocates nothing.
    /// </summary>
    public static DraftEdit Check(ReadOnlySpan<DesignBox> blocks, ReadOnlySpan<DesignMount> mounts, in DesignMount mount, int ignore = -1)
    {
        if ((uint)mount.Parent >= (uint)blocks.Length)
            return DraftEdit.NoSuchBlock;
        if (mount.Type?.Mount is { } own && !own.Allows(mount.Size))
            return DraftEdit.NoSize;
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
        if (c == 0) c = string.CompareOrdinal(a.Type.Id, b.Type.Id);
        return c != 0 ? c : a.Size.CompareTo(b.Size);
    }

    /// <summary>
    /// Holds a design's mounts to the rules: a mount type each, of a size the type may have, within the content's
    /// count, each on a whole open
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
                case DraftEdit.NoSize:
                    throw new InvalidDataException($"{source}: mount {i}: a {mount.Type.Name} is not made in size {mount.Size}");
                case DraftEdit.OffFace:
                    throw new InvalidDataException($"{source}: mount {i}: it does not fit on that face of block {mount.Parent}");
                default:
                    throw new InvalidDataException($"{source}: mount {i}: a block or another mounted part is in its way");
            }
        }
    }

    internal static int Of(GridSize g, int axis) => axis switch { 0 => g.X, 1 => g.Y, _ => g.Z };
}
