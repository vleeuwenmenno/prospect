namespace Prospect.Game.World;

/// <summary>The planes the ship editor mirrors an edit across; any mix may be on. Each plane is square to its axis.</summary>
[Flags]
public enum MirrorAxes : byte
{
    /// <summary>No mirroring.</summary>
    None = 0,

    /// <summary>The plane square to X: left and right swap.</summary>
    X = 1,

    /// <summary>The plane square to Y: up and down swap.</summary>
    Y = 2,

    /// <summary>The plane square to Z: front and back swap.</summary>
    Z = 4,

    /// <summary>All three planes: an edit has up to eight copies.</summary>
    All = 7,
}

/// <summary>
/// Mirroring in the ship editor's grid, shared by the draft's edits and the ghosts that preview them. The planes
/// pass through the centre of a block, given as <c>centre2</c>: twice the centre on each axis, in cells, which is
/// <c>2 * At + Size</c> and so always a whole number. Every figure is whole cells, or whole tenths of a cell for a
/// mounted part, so a mirrored copy lands exactly and compares exactly. Allocates nothing.
/// </summary>
public static class Mirroring
{
    /// <summary>The most copies of one edit, the original included: two to the power of three planes.</summary>
    public const int MaxCopies = 8;

    /// <summary>Twice the centre of a box, in cells, on each axis.</summary>
    public static GridSize Centre2(in DesignBox box) =>
        new(2 * box.At.X + box.Size.X, 2 * box.At.Y + box.Size.Y, 2 * box.At.Z + box.Size.Z);

    /// <summary>
    /// A box mirrored across the planes of <paramref name="mask"/> (a <see cref="MirrorAxes"/> value): its place
    /// reflected, a thrust part's facing flipped on a mirrored axis and its wedge slant flipped on a mirrored transverse axis.
    /// </summary>
    public static DesignBox Box(in DesignBox box, int mask, GridSize centre2)
    {
        var at = box.At;
        if ((mask & 1) != 0) at = at with { X = centre2.X - (box.At.X + box.Size.X) };
        if ((mask & 2) != 0) at = at with { Y = centre2.Y - (box.At.Y + box.Size.Y) };
        if ((mask & 4) != 0) at = at with { Z = centre2.Z - (box.At.Z + box.Size.Z) };
        // Only a thrust part's facing means anything (BoxHullSums); another block keeps its own, so a copy of it that
        // lands on its plane is still the same block.
        var facing = box.Type is { Thrust: > 0 } or { FacesOut: true } ? Facing(box.Facing, mask) : box.Facing;
        return box with { At = at, Facing = facing, Shape = Shape(box.Shape, mask) };
    }

    /// <summary>A facing mirrored across the planes of <paramref name="mask"/>: it flips when its own axis is mirrored.</summary>
    public static BlockFacing Facing(BlockFacing facing, int mask) =>
        (mask & (1 << Facings.Axis(facing))) != 0 ? Facings.Opposite(facing) : facing;

    /// <summary>
    /// A shape mirrored across the planes of <paramref name="mask"/>. A wedge's removed corner is named by signs on
    /// the two axes across its extrusion; a mirrored one of those flips its sign, and mirroring along the
    /// extrusion changes nothing.
    /// </summary>
    public static BlockShape Shape(BlockShape shape, int mask)
    {
        if (shape == BlockShape.Box || !BlockShapes.IsValid(shape))
            return shape;
        BlockShapes.Decode(shape, out var e, out var u, out var v, out var su, out var sv);
        if ((mask & (1 << u)) != 0) su = -su;
        if ((mask & (1 << v)) != 0) sv = -sv;
        return (BlockShape)(1 + e * 4 + (su < 0 ? 1 : 0) + (sv < 0 ? 2 : 0));
    }

    /// <summary>
    /// Fills <paramref name="into"/> (room for <see cref="MaxCopies"/>) with the original box and then each distinct
    /// mirrored copy, one for every mix of <paramref name="axes"/>, and returns how many. A copy that is the same box
    /// as one already listed, as a box lying on a plane is, is left out.
    /// </summary>
    public static int Copies(in DesignBox box, MirrorAxes axes, GridSize centre2, Span<DesignBox> into)
    {
        var count = 0;
        for (var mask = 0; mask < MaxCopies; mask++)
        {
            if ((mask & ~(int)axes) != 0)
                continue;
            var copy = mask == 0 ? box : Box(box, mask, centre2);
            var seen = false;
            for (var i = 0; !seen && i < count; i++)
                seen = Same(into[i], copy);
            if (!seen)
                into[count++] = copy;
        }

        return count;
    }

    /// <summary>
    /// A mounted part mirrored across the planes of <paramref name="mask"/>, given its parent box and that box
    /// mirrored the same way. The face flips on a mirrored axis and its square is reflected across the face. The
    /// result's <see cref="DesignMount.Parent"/> is left as it was: the caller knows where the mirrored parent is.
    /// </summary>
    public static DesignMount Mount(in DesignBox parent, in DesignBox mirroredParent, in DesignMount mount, int mask, GridSize centre2)
    {
        var spec = mount.Type.Mount!;
        var n = Facings.Axis(mount.Face);
        int a = (n + 1) % 3, b = (n + 2) % 3;
        return mount with
        {
            Face = Facing(mount.Face, mask),
            U = Across(parent.At, mirroredParent.At, mount.U, spec.Side, a, mask, centre2),
            V = Across(parent.At, mirroredParent.At, mount.V, spec.Side, b, mask, centre2),
        };
    }

    /// <summary>Whether two boxes are the same block: place, size, type, shape and facing.</summary>
    public static bool Same(in DesignBox a, in DesignBox b) =>
        a.At == b.At && a.Size == b.Size && a.Shape == b.Shape && a.Facing == b.Facing && ReferenceEquals(a.Type, b.Type);

    private static int Across(GridSize parentAt, GridSize mirroredAt, int offset, int side, int axis, int mask, GridSize centre2)
    {
        if ((mask & (1 << axis)) == 0)
            return offset;
        // In tenths of a cell on the grid's integer edges: the square's far edge is reflected to be its near one.
        var from = At(parentAt, axis) * FaceMounts.PerCell + offset + side;
        return At(centre2, axis) * FaceMounts.PerCell - from - At(mirroredAt, axis) * FaceMounts.PerCell;
    }

    private static int At(GridSize g, int axis) => axis switch { 0 => g.X, 1 => g.Y, _ => g.Z };
}
