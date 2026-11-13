using System.Numerics;
using Prospect.Game.World;

namespace Prospect.Client.World;

/// <summary>What a pointer ray found on a draft: a block, the face it entered through, and the cell of that face.</summary>
internal readonly record struct DraftHit(int Block, int Side, GridSize Cell, float Distance);

/// <summary>
/// The ship editor's geometry, free of the renderer and the interface so it can be tested alone. Grid space is
/// the design's own: cell <c>i</c> spans <c>[i - 0.5, i + 0.5)</c> about its centre, so a box covers
/// <c>[At - 0.5, At + Size - 0.5]</c> on each axis (<see cref="BoxDesign"/>). Sides are numbered as
/// <see cref="BuildDraft"/> numbers them: <c>2 * axis + (negative ? 1 : 0)</c>.
/// </summary>
internal static class EditorGeometry
{
    /// <summary>
    /// The first box a ray from <paramref name="origin"/> along <paramref name="direction"/> enters, both in grid
    /// space, with the face it enters through and the cell of that face it enters at. A box the ray starts inside
    /// is passed through, so a camera inside the hull still picks the far walls. False when nothing is hit.
    /// </summary>
    public static bool Pick(ReadOnlySpan<DesignBox> boxes, Vector3 origin, Vector3 direction, out DraftHit hit) =>
        Pick(boxes, origin, direction, default, out hit);

    /// <summary>
    /// As <see cref="Pick(ReadOnlySpan{DesignBox}, Vector3, Vector3, out DraftHit)"/>, passing over the blocks in
    /// <paramref name="skip"/> (a <see cref="BlockSet"/>), so a dragged group is placed against what is behind it.
    /// </summary>
    public static bool Pick(ReadOnlySpan<DesignBox> boxes, Vector3 origin, Vector3 direction, ReadOnlySpan<ulong> skip, out DraftHit hit)
    {
        hit = default;
        var best = float.PositiveInfinity;
        for (var i = 0; i < boxes.Length; i++)
        {
            if (!skip.IsEmpty && (i >> 6) < skip.Length && BlockSet.Has(skip, i))
                continue;
            if (!Enter(boxes[i], origin, direction, out var t, out var side) || t >= best)
                continue;
            best = t;
            hit = new DraftHit(i, side, default, t);
        }

        if (float.IsPositiveInfinity(best))
            return false;
        hit = hit with { Cell = FaceCell(boxes[hit.Block], hit.Side, origin + direction * best) };
        return true;
    }

    /// <summary>
    /// Where a ray enters a block: the distance along the ray and the side it comes in through. False when it
    /// misses, or starts inside or past the block. A wedge is entered only through its solid half; a ray through
    /// the empty half passes on. Coming in through the slant, the side is whichever of the two box sides the slant
    /// replaces is the larger, so a part placed there snaps to whole cells against the block's box.
    /// </summary>
    public static bool Enter(in DesignBox box, Vector3 origin, Vector3 direction, out float distance, out int side)
    {
        distance = 0f;
        side = -1;
        var near = float.NegativeInfinity;
        var far = float.PositiveInfinity;
        for (var axis = 0; axis < 3; axis++)
        {
            var low = Of(box.At, axis) - 0.5f;
            var high = low + Of(box.Size, axis);
            var o = origin[axis];
            var d = direction[axis];
            if (MathF.Abs(d) < 1e-9f)
            {
                if (o < low || o > high)
                    return false;
                continue;
            }

            var a = (low - o) / d;
            var b = (high - o) / d;
            // Entering through the low face means coming in from the negative side, which is that axis's minus side.
            var enterSide = 2 * axis + (d > 0 ? 1 : 0);
            if (a > b)
                (a, b) = (b, a);
            if (a > near)
            {
                near = a;
                side = enterSide;
            }

            far = Math.Min(far, b);
            if (near > far)
                return false;
        }

        if (box.Shape != BlockShape.Box)
        {
            // The solid half: dot(slant, p - middle) <= 0, with the slant normal of the block's own half extents.
            var half = new Vector3(box.Size.X, box.Size.Y, box.Size.Z) * 0.5f;
            var slant = BlockShapes.Slant(box.Shape, half);
            var o = Vector3.Dot(slant, origin - Middle(box));
            var d = Vector3.Dot(slant, direction);
            if (MathF.Abs(d) < 1e-9f)
            {
                if (o > 0f)
                    return false;
            }
            else if (d < 0f)
            {
                var t = -o / d;
                if (t > near)
                {
                    near = t;
                    side = SlantEntry(box);
                }
            }
            else
            {
                far = Math.Min(far, -o / d);
            }

            if (near > far)
                return false;
        }

        if (near < 0f || side < 0)
            return false;
        distance = near;
        return true;
    }

    /// <summary>
    /// The box side a ray through a wedge's slant counts as: (u, +su) or (v, +sv), the one with the larger box face,
    /// u when they are equal.
    /// </summary>
    public static int SlantEntry(in DesignBox box)
    {
        BlockShapes.Decode(box.Shape, out _, out var u, out var v, out var su, out var sv);
        return Of(box.Size, v) >= Of(box.Size, u) ? 2 * u + (su < 0 ? 1 : 0) : 2 * v + (sv < 0 ? 1 : 0);
    }

    /// <summary>
    /// The cell of <paramref name="box"/> under a point on its face <paramref name="side"/>: on the face's own
    /// axis the outermost layer of cells, across it the cell the point is in, rounded so that odd, even and
    /// negative positions all land in the cell whose span holds them.
    /// </summary>
    public static GridSize FaceCell(in DesignBox box, int side, Vector3 point)
    {
        Span<int> cell = stackalloc int[3];
        var normal = side >> 1;
        for (var axis = 0; axis < 3; axis++)
        {
            int low = Of(box.At, axis), high = low + Of(box.Size, axis) - 1;
            cell[axis] = axis == normal
                ? (side & 1) == 0 ? high : low
                : Math.Clamp((int)MathF.Floor(point[axis] + 0.5f), low, high);
        }

        return new GridSize(cell[0], cell[1], cell[2]);
    }

    /// <summary>
    /// The grid-space box around the listed blocks, or around every block when <paramref name="blocks"/> is empty:
    /// its low and high corners. False when there is nothing to enclose.
    /// </summary>
    public static bool Bounds(ReadOnlySpan<DesignBox> boxes, ReadOnlySpan<int> blocks, out Vector3 low, out Vector3 high)
    {
        low = new Vector3(float.PositiveInfinity);
        high = new Vector3(float.NegativeInfinity);
        var count = blocks.IsEmpty ? boxes.Length : blocks.Length;
        for (var i = 0; i < count; i++)
        {
            var index = blocks.IsEmpty ? i : blocks[i];
            if ((uint)index >= (uint)boxes.Length)
                continue;
            ref readonly var box = ref boxes[index];
            var a = new Vector3(box.At.X - 0.5f, box.At.Y - 0.5f, box.At.Z - 0.5f);
            low = Vector3.Min(low, a);
            high = Vector3.Max(high, a + new Vector3(box.Size.X, box.Size.Y, box.Size.Z));
        }

        return low.X <= high.X;
    }

    /// <summary>
    /// How far from <paramref name="pivot"/>, in metres, the farthest corner of the boxes reaches: the radius the
    /// editor's camera keeps clear of, which grows with the draft rather than staying the ship's.
    /// </summary>
    public static float Reach(ReadOnlySpan<DesignBox> boxes, Double3 pivot, float blockSize)
    {
        if (!Bounds(boxes, default, out var low, out var high))
            return 0f;
        var reach = 0f;
        for (var i = 0; i < 8; i++)
        {
            var corner = new Vector3((i & 1) == 0 ? low.X : high.X, (i & 2) == 0 ? low.Y : high.Y, (i & 4) == 0 ? low.Z : high.Z);
            reach = Math.Max(reach, ToHull(corner, pivot, blockSize).Length());
        }

        return reach;
    }

    /// <summary>A box's centre in grid space.</summary>
    public static Vector3 Middle(in DesignBox box) =>
        new(box.At.X + (box.Size.X - 1) * 0.5f, box.At.Y + (box.Size.Y - 1) * 0.5f, box.At.Z + (box.Size.Z - 1) * 0.5f);

    /// <summary>A grid-space point in the hull's own frame, in metres from <paramref name="pivot"/>.</summary>
    public static Vector3 ToHull(Vector3 grid, Double3 pivot, float blockSize) =>
        new((float)((grid.X - pivot.X) * blockSize), (float)((grid.Y - pivot.Y) * blockSize), (float)((grid.Z - pivot.Z) * blockSize));

    /// <summary>A point in the hull's own frame, in metres from <paramref name="pivot"/>, in grid space.</summary>
    public static Vector3 ToGrid(Vector3 hull, Double3 pivot, float blockSize) =>
        new((float)(hull.X / blockSize + pivot.X), (float)(hull.Y / blockSize + pivot.Y), (float)(hull.Z / blockSize + pivot.Z));

    /// <summary>The unit vector out of a side.</summary>
    public static Vector3 Normal(int side)
    {
        var sign = (side & 1) == 0 ? 1f : -1f;
        return (side >> 1) switch { 0 => new Vector3(sign, 0, 0), 1 => new Vector3(0, sign, 0), _ => new Vector3(0, 0, sign) };
    }

    /// <summary>A grid size's length along an axis.</summary>
    public static int Of(GridSize g, int axis) => axis switch { 0 => g.X, 1 => g.Y, _ => g.Z };

    /// <summary>A grid size with one axis changed.</summary>
    public static GridSize With(GridSize g, int axis, int value) => axis switch { 0 => g with { X = value }, 1 => g with { Y = value }, _ => g with { Z = value } };

    /// <summary>What a block is for, by what it does rather than by its id, for the part list.</summary>
    public static PartRole Role(BlockType type) =>
        type.Core ? PartRole.Core
        : type.Ftl != FtlKind.None ? PartRole.Drive
        : type.Thrust > 0 ? type.ForwardOnly ? PartRole.Engine : PartRole.Thruster
        : type.Torque > 0 ? PartRole.Gyro
        : type.PowerOutput > 0 ? PartRole.Power
        : type.EnergyStorage > 0 ? PartRole.Storage
        : type.Shield > 0 ? PartRole.Shield
        : type.Cargo > 0 ? PartRole.Cargo
        : type.Crew > 0 ? PartRole.Crew
        : PartRole.Structure;

    /// <summary>The faint tint a part of a role adds to the hull colour in the editor, so parts can be told apart.</summary>
    public static Vector3 Tint(PartRole role) => role switch
    {
        PartRole.Core => new Vector3(1.00f, 0.69f, 0.13f),
        PartRole.Drive => new Vector3(0.62f, 0.45f, 1.00f),
        PartRole.Engine => new Vector3(1.00f, 0.45f, 0.20f),
        PartRole.Thruster => new Vector3(0.95f, 0.60f, 0.35f),
        PartRole.Gyro => new Vector3(0.80f, 0.45f, 0.95f),
        PartRole.Power => new Vector3(0.95f, 0.90f, 0.30f),
        PartRole.Storage => new Vector3(0.35f, 0.85f, 0.45f),
        PartRole.Shield => new Vector3(0.30f, 0.75f, 1.00f),
        PartRole.Cargo => new Vector3(0.70f, 0.55f, 0.40f),
        PartRole.Crew => new Vector3(0.90f, 0.90f, 0.95f),
        _ => Vector3.Zero,
    };

    /// <summary>The colour a draft block is drawn in: the hull's colour with a faint tint for its role.</summary>
    public static Vector3 BlockColor(Vector3 hull, BlockType type)
    {
        var role = Role(type);
        return role == PartRole.Structure ? hull : Vector3.Lerp(hull, Tint(role), 0.3f);
    }
}

/// <summary>What a part does, as the editor's part list names it.</summary>
internal enum PartRole : byte
{
    Structure,
    Core,
    Drive,
    Engine,
    Thruster,
    Gyro,
    Power,
    Storage,
    Shield,
    Cargo,
    Crew,
}
