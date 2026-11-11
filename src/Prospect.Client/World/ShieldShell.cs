using Brutal.Numerics;
using Prospect.Client.Render;
using Prospect.Game.World;

namespace Prospect.Client.World;

/// <summary>
/// The surface a shield is drawn on: the outside of the blocks a hull still has, a margin off them all round. It is
/// exactly the boundary of the blocks' cubes each grown by the margin: convex edges and corners stand out square,
/// concave ones close up, and nothing is covered twice or left open, whatever the design, rotation or damage.
/// It is found on the grid the grown cubes' own sides make, and each plane's faces are merged into rectangles.
/// </summary>
internal static class ShieldShell
{
    /// <summary>How far the shell stands off the blocks, as a share of the block size.</summary>
    public const float Margin = 0.3f;

    /// <summary>
    /// The most vertices one shell has. A live design's shell is far smaller; a pathological one (blocks touching
    /// only at corners, all down a diagonal) that would need more is cut off there.
    /// </summary>
    public const int MaxVertices = HullDesign.MaxBlocks * 6 * 18;

    /// <summary>
    /// Writes the shell of the blocks of <paramref name="design"/> that are set in <paramref name="alive"/>, as
    /// triangles wound counter-clockwise seen from outside, in the hull's own frame and metres, into
    /// <paramref name="output"/>. Returns the number of vertices. Allocates nothing.
    /// </summary>
    public static int Build(HullDesign design, ulong alive, Span<MeshVertex> output)
    {
        alive &= design.FullMask;
        if (alive == 0)
            return 0;

        // Each grown cube's sides, per axis, in grid units, and which cubes cover each span between them.
        const int MaxCoords = HullDesign.MaxBlocks * 2;
        Span<float> xs = stackalloc float[MaxCoords];
        Span<float> ys = stackalloc float[MaxCoords];
        Span<float> zs = stackalloc float[MaxCoords];
        Span<ulong> xMask = stackalloc ulong[MaxCoords];
        Span<ulong> yMask = stackalloc ulong[MaxCoords];
        Span<ulong> zMask = stackalloc ulong[MaxCoords];
        var cells = design.Cells;
        var nx = Sides(cells, alive, 0, xs, xMask);
        var ny = Sides(cells, alive, 1, ys, yMask);
        var nz = Sides(cells, alive, 2, zs, zMask);

        Span<bool> faces = stackalloc bool[MaxCoords * MaxCoords];
        var count = 0;
        for (var axis = 0; axis < 3; axis++)
        {
            // The two directions across a face, in the order that winds it counter-clockwise from outside.
            var u = (axis + 1) % 3;
            var v = (axis + 2) % 3;
            var along = Pick(axis, xs[..nx], ys[..ny], zs[..nz]);
            var across = Pick(u, xs[..nx], ys[..ny], zs[..nz]);
            var up = Pick(v, xs[..nx], ys[..ny], zs[..nz]);
            var alongMask = PickMask(axis, xMask, yMask, zMask);
            var acrossMask = PickMask(u, xMask, yMask, zMask);
            var upMask = PickMask(v, xMask, yMask, zMask);
            var spansU = across.Length - 1;
            var spansV = up.Length - 1;

            for (var plane = 0; plane < along.Length; plane++)
            {
                var below = plane > 0 ? alongMask[plane - 1] : 0UL;
                var above = plane < along.Length - 1 ? alongMask[plane] : 0UL;
                for (var sign = 1; sign >= -1; sign -= 2)
                {
                    // A face of the shell is where one side of the plane is inside a grown cube and the other is not.
                    var any = false;
                    for (var j = 0; j < spansV; j++)
                    {
                        for (var i = 0; i < spansU; i++)
                        {
                            var column = acrossMask[i] & upMask[j];
                            var inside = (below & column) != 0;
                            var outside = (above & column) == 0;
                            if (sign < 0)
                            {
                                inside = (above & column) != 0;
                                outside = (below & column) == 0;
                            }

                            faces[j * spansU + i] = inside && outside;
                            any |= inside && outside;
                        }
                    }

                    if (any && !Merge(design, faces, spansU, spansV, axis, sign, along[plane], across, up, output, ref count))
                        return count;
                }
            }
        }

        return count;
    }

    /// <summary>
    /// Covers the marked faces of one plane with as few rectangles as it greedily can and writes them. Returns false
    /// once the output is full.
    /// </summary>
    private static bool Merge(HullDesign design, Span<bool> faces, int spansU, int spansV, int axis, int sign, float at, ReadOnlySpan<float> across, ReadOnlySpan<float> up, Span<MeshVertex> output, ref int count)
    {
        for (var j = 0; j < spansV; j++)
        {
            for (var i = 0; i < spansU; i++)
            {
                if (!faces[j * spansU + i])
                    continue;

                var width = 1;
                while (i + width < spansU && faces[j * spansU + i + width])
                    width++;
                var height = 1;
                while (j + height < spansV && Row(faces, spansU, j + height, i, width))
                    height++;
                for (var y = 0; y < height; y++)
                    faces.Slice((j + y) * spansU + i, width).Clear();

                if (count + 6 > output.Length)
                    return false;
                Rect(design, output, ref count, axis, sign, at, across[i], across[i + width], up[j], up[j + height]);
            }
        }

        return true;
    }

    private static bool Row(Span<bool> faces, int spansU, int row, int from, int width)
    {
        for (var i = from; i < from + width; i++)
        {
            if (!faces[row * spansU + i])
                return false;
        }

        return true;
    }

    /// <summary>The sides of the grown cubes along one axis, sorted and merged, and which cubes cover each span.</summary>
    private static int Sides((int X, int Y, int Z)[] cells, ulong alive, int axis, Span<float> sides, Span<ulong> covered)
    {
        var reach = 0.5f + Margin;
        var count = 0;
        for (var b = 0; b < cells.Length; b++)
        {
            if ((alive & 1UL << b) == 0)
                continue;
            var c = axis == 0 ? cells[b].X : axis == 1 ? cells[b].Y : cells[b].Z;
            count = Insert(sides, count, c - reach);
            count = Insert(sides, count, c + reach);
        }

        for (var s = 0; s < count - 1; s++)
        {
            var middle = (sides[s] + sides[s + 1]) * 0.5f;
            var mask = 0UL;
            for (var b = 0; b < cells.Length; b++)
            {
                var c = axis == 0 ? cells[b].X : axis == 1 ? cells[b].Y : cells[b].Z;
                if ((alive & 1UL << b) != 0 && MathF.Abs(middle - c) < reach)
                    mask |= 1UL << b;
            }

            covered[s] = mask;
        }

        return count;
    }

    /// <summary>Inserts a value into a sorted run unless it is already there.</summary>
    private static int Insert(Span<float> sorted, int count, float value)
    {
        var at = 0;
        while (at < count && sorted[at] < value - 1e-4f)
            at++;
        if (at < count && MathF.Abs(sorted[at] - value) <= 1e-4f)
            return count;
        for (var i = count; i > at; i--)
            sorted[i] = sorted[i - 1];
        sorted[at] = value;
        return count + 1;
    }

    private static ReadOnlySpan<float> Pick(int axis, ReadOnlySpan<float> x, ReadOnlySpan<float> y, ReadOnlySpan<float> z) =>
        axis == 0 ? x : axis == 1 ? y : z;

    private static ReadOnlySpan<ulong> PickMask(int axis, ReadOnlySpan<ulong> x, ReadOnlySpan<ulong> y, ReadOnlySpan<ulong> z) =>
        axis == 0 ? x : axis == 1 ? y : z;

    /// <summary>One rectangle of the plane <paramref name="at"/> along <paramref name="axis"/>, in grid units.</summary>
    private static void Rect(HullDesign design, Span<MeshVertex> output, ref int count, int axis, int sign, float at, float u0, float u1, float v0, float v1)
    {
        var u = (axis + 1) % 3;
        var v = (axis + 2) % 3;
        var normal = Axis(axis) * sign;
        var a = Point(design, axis, at, u, u0, v, v0);
        var b = Point(design, axis, at, u, u1, v, v0);
        var c = Point(design, axis, at, u, u1, v, v1);
        var d = Point(design, axis, at, u, u0, v, v1);

        // Facing the other way, the same corners wind the other way round.
        if (sign < 0)
            (b, d) = (d, b);
        Vertex(output, ref count, a, normal);
        Vertex(output, ref count, b, normal);
        Vertex(output, ref count, c, normal);
        Vertex(output, ref count, a, normal);
        Vertex(output, ref count, c, normal);
        Vertex(output, ref count, d, normal);
    }

    /// <summary>A grid point in the hull's frame and metres: about the centre of mass, scaled by the block size.</summary>
    private static System.Numerics.Vector3 Point(HullDesign design, int axis, float at, int u, float atU, int v, float atV)
    {
        var p = System.Numerics.Vector3.Zero;
        p += Axis(axis) * at + Axis(u) * atU + Axis(v) * atV;
        return (p - design.CenterOfMass) * design.BlockSize;
    }

    private static System.Numerics.Vector3 Axis(int axis) =>
        axis switch
        {
            0 => System.Numerics.Vector3.UnitX,
            1 => System.Numerics.Vector3.UnitY,
            _ => System.Numerics.Vector3.UnitZ,
        };

    private static void Vertex(Span<MeshVertex> output, ref int count, System.Numerics.Vector3 position, System.Numerics.Vector3 normal) =>
        output[count++] = new MeshVertex { Position = new float3(position.X, position.Y, position.Z), Normal = new float3(normal.X, normal.Y, normal.Z) };
}
