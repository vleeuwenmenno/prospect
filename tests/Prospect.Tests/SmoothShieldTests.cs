using System.Numerics;
using System.Runtime.InteropServices;
using Prospect.Client.Render;
using Prospect.Client.World;
using Prospect.Engine.Jobs;
using Prospect.Game.Client;
using Prospect.Game.World;
using Xunit;

namespace Prospect.Tests;

/// <summary>
/// The smooth shield shell (<see cref="ShieldSurface"/>), checked against the blocks themselves: closed, wound out,
/// every block and every triangle's interior clear of the blocks, the same whatever the blocks' split or order, small
/// gaps closed and wide ones open, and the drawn shield always a whole shell: its own, an earlier one of more blocks,
/// or the intact one its design was admitted with.
/// </summary>
public sealed class SmoothShieldTests
{
    private static readonly GameContent Content = TestDesigns.Content;
    private static BlockType Core => Content.Blocks.Get("core");
    private static BlockType Plate => Content.Blocks.Get("hull");
    private readonly ShellVertex[] _vertexOut = new ShellVertex[GeometryAdmission.MaxVertices];
    private readonly ushort[] _indexOut = new ushort[GeometryAdmission.MaxIndices];

    /// <summary>A shell from the shared builder as a list of triangles, three vertices each, or null when there is none.</summary>
    internal static ShellVertex[]? Triangles(HullDesign design, ReadOnlySpan<ulong> alive, ShellShape shape) =>
        ShieldSurface.WithShared((design, alive.ToArray(), shape), static (b, s) =>
        {
            var vertices = new ShellVertex[GeometryAdmission.MaxVertices];
            var indices = new ushort[GeometryAdmission.MaxIndices];
            var count = b.Build(s.design, s.Item2, s.shape, vertices, indices, out var indexCount);
            return count < 0 ? null : Expand(vertices.AsSpan(0, count), indices.AsSpan(0, indexCount));
        });

    /// <summary>The vertices and indices of a shell from the shared builder: -1 vertices when there is none.</summary>
    internal static int Count(HullDesign design, ReadOnlySpan<ulong> alive, ShellShape shape, out int indexCount)
    {
        var (count, indices) = ShieldSurface.WithShared((design, alive.ToArray(), shape), static (b, s) =>
            (b.Build(s.design, s.Item2, s.shape, new ShellVertex[GeometryAdmission.MaxVertices], new ushort[GeometryAdmission.MaxIndices], out var n), n));
        indexCount = indices;
        return count;
    }

    /// <summary>An indexed shell as its triangles, three vertices each, for checks that walk triangles.</summary>
    internal static ShellVertex[] Expand(ReadOnlySpan<ShellVertex> vertices, ReadOnlySpan<ushort> indices)
    {
        Assert.Equal(0, indices.Length % 3);
        var triangles = new ShellVertex[indices.Length];
        for (var i = 0; i < indices.Length; i++)
        {
            Assert.InRange(indices[i], 0, vertices.Length - 1);
            triangles[i] = vertices[indices[i]];
        }

        return triangles;
    }

    /// <summary>The intact shell a design was admitted with, as triangles.</summary>
    internal static ShellVertex[] Intact(HullDesign design) => Expand(design.IntactShell.Span, design.IntactIndices.Span);

    private static ShellVertex[] Shell(HullDesign design, ReadOnlySpan<ulong> alive, ShellShape? shape = null)
    {
        var shell = Triangles(design, alive, shape ?? ShellShape.Default);
        Assert.True(shell is not null, $"no shell: {ShieldSurface.WithShared(b => b.Work.Overflow)}");
        return shell!;
    }

    /// <summary>How far a point is from the block, in metres: 0 inside it.</summary>
    private static float Off(HullDesign design, int b, Vector3 p)
    {
        var local = p - design.BlockOffsets[b];
        var half = design.BlockHalfExtents[b];
        if (design.Shapes[b] == BlockShape.Box)
            return Vector3.Max(Vector3.Abs(local) - half, Vector3.Zero).Length();
        return (local - BlockShapes.Closest(design.Shapes[b], half, local)).Length();
    }

    private static float Off(HullDesign design, ReadOnlySpan<ulong> alive, Vector3 p)
    {
        var best = float.MaxValue;
        for (var b = 0; b < design.BlockCount; b++)
            if (BlockSet.Has(alive, b))
                best = Math.Min(best, Off(design, b, p));
        return best;
    }

    /// <summary>The winding number of a closed mesh about a point: 1 inside, 0 outside.</summary>
    private static double Winding(ReadOnlySpan<ShellVertex> v, Vector3 p)
    {
        double total = 0;
        for (var i = 0; i < v.Length; i += 3)
        {
            var a = v[i].Position - p;
            var b = v[i + 1].Position - p;
            var c = v[i + 2].Position - p;
            double la = a.Length(), lb = b.Length(), lc = c.Length();
            var num = Vector3.Dot(a, Vector3.Cross(b, c));
            var den = la * lb * lc + Vector3.Dot(a, b) * lc + Vector3.Dot(a, c) * lb + Vector3.Dot(b, c) * la;
            total += 2 * Math.Atan2(num, den);
        }

        return total / (4 * Math.PI);
    }

    /// <summary>Every directed edge is matched by its reverse exactly: closed, consistently wound, no T-junction.</summary>
    private static void Closed(ReadOnlySpan<ShellVertex> v)
    {
        var edges = new Dictionary<(Vector3, Vector3), int>();
        for (var i = 0; i < v.Length; i += 3)
            for (var e = 0; e < 3; e++)
            {
                var key = (v[i + e].Position, v[i + (e + 1) % 3].Position);
                edges[key] = edges.GetValueOrDefault(key) + 1;
            }

        foreach (var ((a, b), n) in edges)
            Assert.True(edges.GetValueOrDefault((b, a)) == n, $"the edge {a} to {b} is not matched");
    }

    /// <summary>
    /// Wound out, with finite unit normals on the outward side of each triangle; every block corner inside; and points
    /// on an eight-step grid across every triangle outside every surviving block.
    /// </summary>
    internal static void Holds(HullDesign design, ReadOnlySpan<ulong> alive, ReadOnlySpan<ShellVertex> v)
    {
        Assert.Equal(0, v.Length % 3);
        Closed(v);
        for (var i = 0; i < v.Length; i += 3)
        {
            var a = v[i].Position;
            var b = v[i + 1].Position;
            var c = v[i + 2].Position;
            var face = Vector3.Cross(b - a, c - a);
            // Each triangle faces the way its corners' normals do, together: the same test the square and cell shells
            // had. One corner's normal alone may lean past a sharp crease, as an averaged normal does.
            for (var k = 0; k < 3; k++)
                Assert.Equal(1f, v[i + k].Normal.Length(), 3);
            Assert.True(Vector3.Dot(v[i].Normal + v[i + 1].Normal + v[i + 2].Normal, face) > 0f, $"a triangle at {a} faces in");

            for (var s = 0; s <= 8; s++)
                for (var t = 0; t <= 8 - s; t++)
                {
                    var point = a + (b - a) * (s / 8f) + (c - a) * (t / 8f);
                    Assert.True(Off(design, alive, point) > 1e-4f * design.BlockSize, $"a triangle at {a} reaches a block at {point}");
                }
        }

        for (var b = 0; b < design.BlockCount; b++)
        {
            if (!BlockSet.Has(alive, b)) continue;
            for (var corner = 0; corner < 8; corner++)
                if (BlockShapes.HasCorner(design.Shapes[b], corner))
                    Assert.Equal(1.0, Winding(v, design.BlockOffsets[b] + BlockShapes.Corner(corner, design.BlockHalfExtents[b])), 3);
        }
    }

    [Fact]
    public void TheDenseFixtureFitsWholeAndEachDamageSetIsWholeOrRefusedForTheIntactOne()
    {
        var dense = TestDesigns.Of(ClientBoxGeometryTests.Packed(new GridSize(6, 4, 7), 88));
        var usage = GeometryAdmission.Measure(dense, Content.Blocks.Limits.SurfaceVertices, Content.Blocks.Limits.Shield);
        Assert.True(usage.Fits, $"{usage.Vertices} of {usage.Limit}, {usage.Overflow}");
        Assert.InRange(usage.Vertices, 48, 12000);
        Assert.InRange(usage.Indices, 3 * usage.Vertices / 2, GeometryAdmission.MaxIndices);
        Assert.Equal((int)usage.Vertices, dense.IntactShell.Length);
        Assert.Equal((int)usage.Indices, dense.IntactIndices.Length);
        Holds(dense, dense.FullSet, Intact(dense));

        var alive = dense.FullSet.ToArray();
        var random = new Random(11);
        for (var round = 0; round < 6; round++)
        {
            dense.FullSet.CopyTo(alive);
            for (var b = 1; b < dense.BlockCount; b++)
                if (random.NextDouble() > 0.3 + 0.1 * round) BlockSet.Remove(alive, b);
            // A damage set either gets its own whole shell, or none, when the hull keeps the intact one.
            if (Triangles(dense, alive, ShellShape.Default) is { } shell)
                Holds(dense, alive, shell);
        }
    }

    [Fact]
    public void ABoxSplitIntoCubesInAnyOrderGivesTheSameShellAndNormals()
    {
        var merged = TestDesigns.Of(new DesignBox(default, new GridSize(4, 2, 3), Core));
        var cubes = new List<DesignBox>();
        for (var x = 0; x < 4; x++) for (var y = 0; y < 2; y++) for (var z = 0; z < 3; z++) cubes.Add(new DesignBox(new(x, y, z), new(1, 1, 1), x + y + z == 0 ? Core : Plate));
        var unit = TestDesigns.Of([.. cubes]);
        cubes.Reverse();
        (cubes[0], cubes[^1]) = (cubes[^1], cubes[0]);
        var reversed = TestDesigns.Of([.. cubes]);
        var expected = Shape(merged, Shell(merged, merged.FullSet), merged);
        Holds(merged, merged.FullSet, Shell(merged, merged.FullSet));
        foreach (var design in new[] { unit, reversed })
            Same(expected, Shape(design, Shell(design, design.FullSet), merged));
    }

    /// <summary>A shell as triangles in grid space, rounded, each with its corners' normals: comparable across pivots.</summary>
    private static Dictionary<string, Vector3[]> Shape(HullDesign design, ReadOnlySpan<ShellVertex> v, HullDesign frame)
    {
        var shift = new Vector3((float)(design.Pivot.X - frame.Pivot.X), (float)(design.Pivot.Y - frame.Pivot.Y), (float)(design.Pivot.Z - frame.Pivot.Z)) * design.BlockSize;
        string Key(Vector3 p) => $"{MathF.Round((p + shift).X, 3)},{MathF.Round((p + shift).Y, 3)},{MathF.Round((p + shift).Z, 3)}";
        var triangles = new Dictionary<string, Vector3[]>();
        for (var i = 0; i < v.Length; i += 3)
        {
            var corners = new[] { (Key(v[i].Position), v[i].Normal), (Key(v[i + 1].Position), v[i + 1].Normal), (Key(v[i + 2].Position), v[i + 2].Normal) };
            Array.Sort(corners, (x, y) => string.CompareOrdinal(x.Item1, y.Item1));
            triangles[string.Join("|", corners.Select(c => c.Item1))] = corners.Select(c => c.Item2).ToArray();
        }

        return triangles;
    }

    private static void Same(Dictionary<string, Vector3[]> expected, Dictionary<string, Vector3[]> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        foreach (var (key, normals) in expected)
        {
            Assert.True(actual.TryGetValue(key, out var other), $"no triangle {key}");
            for (var i = 0; i < 3; i++)
                Assert.True(Vector3.Dot(normals[i], other[i]) > 0.9999f, $"normals at {key} differ: {normals[i]} and {other[i]}");
        }
    }

    [Fact]
    public void AOneCellGapClosesIntoAMirroredNeckAndAFourCellOpeningStaysOpen()
    {
        foreach (var (gap, closed) in new[] { (1, true), (4, false) })
        {
            var left = new DesignBox(default, new GridSize(3, 3, 3), Core);
            var right = new DesignBox(new GridSize(3 + gap, 0, 0), new GridSize(3, 3, 3), Plate);
            var design = TestDesigns.Of(left, right);
            var shell = Shell(design, design.FullSet);
            Holds(design, design.FullSet, shell);
            // The middle of the gap, in the hull's frame.
            var middle = (design.BlockOffsets[0] + design.BlockOffsets[1]) * 0.5f;
            Assert.Equal(closed ? 1.0 : 0.0, Winding(shell, middle), 3);

            // Both orders give the same shell, normals and all.
            var swapped = TestDesigns.Of(right with { Type = Core }, left with { Type = Plate });
            Same(Shape(design, shell, design), Shape(swapped, Shell(swapped, swapped.FullSet), design));
            if (!closed)
                continue;

            // Over the neck's middle the shell faces up or down the gap's plane, not across it to either block: the
            // normals are the mesh's own, so no block's side wins a tie.
            var top = design.BlockOffsets[0].Y + design.BlockHalfExtents[0].Y;
            var checkedAny = false;
            foreach (var vertex in shell)
            {
                var p = vertex.Position - middle;
                if (MathF.Abs(p.X) > 0.2f * design.BlockSize || vertex.Position.Y < top) continue;
                checkedAny = true;
                Assert.True(MathF.Abs(vertex.Normal.X) < 0.5f, $"normal {vertex.Normal} at {p} leans to one block");
            }

            Assert.True(checkedAny);
        }
    }

    [Theory]
    [InlineData(1f, 0.3f)]
    [InlineData(0.5f, 0.3f)]
    [InlineData(0.1f, 0f)]
    [InlineData(1f, 0.5f)]
    public void EveryTriangleStaysOffTheBlocksForBoxesTurnedWedgesAndDamage(float roundness, float blend)
    {
        var shape = new ShellShape(roundness, blend);
        var design = TestDesigns.Of(
            new DesignBox(default, new GridSize(3, 2, 2), Core),
            new DesignBox(new GridSize(3, 0, 0), new GridSize(2, 2, 3), Plate, BlockShape.WedgeXPlusMinus),
            new DesignBox(new GridSize(0, 2, 0), new GridSize(2, 3, 1), Plate, BlockShapes.Rotate(BlockShape.WedgeZMinusPlus, 1)),
            new DesignBox(new GridSize(-2, 0, 1), new GridSize(1, 1, 1), Plate));
        Holds(design, design.FullSet, Shell(design, design.FullSet, shape));
        var damaged = design.FullSet.ToArray();
        BlockSet.Remove(damaged, 1);
        Holds(design, damaged, Shell(design, damaged, shape));
    }

    [Fact]
    public void AWedgesShellStandsTheMarginOffItsSlopeFacingAlongIt()
    {
        Assert.Equal(ShellShape.Default, Content.Blocks.Limits.Shield);
        var design = TestDesigns.Of(new DesignBox(default, new GridSize(6, 6, 2), Core, BlockShape.WedgeZPlusPlus));
        var shell = Shell(design, design.FullSet);
        Holds(design, design.FullSet, shell);
        var slant = Vector3.Normalize(BlockShapes.Slant(BlockShape.WedgeZPlusPlus, design.BlockHalfExtents[0]));
        var middle = design.BlockOffsets[0];
        var hits = 0;
        for (var i = 0; i < shell.Length; i += 3)
        {
            // Where the ray out of the middle of the slope crosses the shell.
            var a = shell[i].Position;
            var e1 = shell[i + 1].Position - a;
            var e2 = shell[i + 2].Position - a;
            var p = Vector3.Cross(slant, e2);
            var det = Vector3.Dot(e1, p);
            if (MathF.Abs(det) < 1e-9f) continue;
            var t0 = middle - a;
            var u = Vector3.Dot(t0, p) / det;
            var q = Vector3.Cross(t0, e1);
            var w = Vector3.Dot(slant, q) / det;
            if (u < 0 || w < 0 || u + w > 1) continue;
            var t = Vector3.Dot(e2, q) / det;
            if (t <= 0) continue;
            hits++;
            Assert.Equal(GeometryAdmission.Margin * design.BlockSize, t, 2);
            Assert.True(Vector3.Dot(shell[i].Normal, slant) > 0.99f, $"normal {shell[i].Normal} off the slope's {slant}");
        }

        Assert.True(hits > 0);
    }

    [Theory]
    [InlineData(64, 64, 64)]
    [InlineData(64, 3, 2)]
    [InlineData(40, 1, 40)]
    public void LargeBoxesFitTheBuilderAndTheirFlatMiddlesAreNeverSampled(int x, int y, int z)
    {
        var design = TestDesigns.Of(new DesignBox(default, new GridSize(x, y, z), Core));
        var builder = new ShieldSurface();
        var count = builder.Build(design, design.FullSet, ShellShape.Default, _vertexOut, _indexOut, out var indexCount);
        var work = builder.Work;
        Assert.True(count > 0, $"{work.Overflow}: {work.Cells} cells");
        Holds(design, design.FullSet, Expand(_vertexOut.AsSpan(0, count), _indexOut.AsSpan(0, indexCount)));
        // Cells grow with the edges, not with the faces' area: a face's middle is one fan.
        long edges = 4L * (x + y + z) * Resolution;
        Assert.True(work.Cells < 260 * edges, $"{work.Cells} cells for {edges} cells of edge");
        var with = TestDesigns.Of(new DesignBox(default, new GridSize(x, y, z), Core), new DesignBox(new GridSize(x + 2, 0, 0), new GridSize(1, 1, 1), Plate));
        Holds(with, with.FullSet, Shell(with, with.FullSet));
    }

    private const int Resolution = ShieldSurface.Resolution;

    [Fact]
    public void ABuilderStopsExactlyAtItsDistanceBudgetReturnsNoShellAndIsUsableAgain()
    {
        var design = TestDesigns.Of(new DesignBox(default, new GridSize(2, 1, 1), Core), new DesignBox(new GridSize(3, 0, 0), new GridSize(1, 1, 1), Plate));
        var small = TestDesigns.Of(new DesignBox(default, new GridSize(1, 1, 1), Core));
        var free = new ShieldSurface(1 << 14);
        var whole = free.Build(design, design.FullSet, ShellShape.Default, _vertexOut, _indexOut, out _);
        var needed = free.Work.Distances;
        Assert.True(whole > 0);
        var cheap = free.Build(small, small.FullSet, ShellShape.Default, _vertexOut, _indexOut, out _);
        Assert.True(free.Work.Distances < needed);

        var exact = new ShieldSurface(1 << 14, needed);
        Assert.Equal(whole, exact.Build(design, design.FullSet, ShellShape.Default, _vertexOut, _indexOut, out _));
        Assert.Equal(needed, exact.Work.Distances);
        var tight = new ShieldSurface(1 << 14, needed - 1);
        Assert.Equal(-1, tight.Build(design, design.FullSet, ShellShape.Default, _vertexOut, _indexOut, out _));
        Assert.Equal("distances", tight.Work.Overflow);
        Assert.True(tight.Work.Distances <= needed - 1);
        Assert.Equal(cheap, tight.Build(small, small.FullSet, ShellShape.Default, _vertexOut, _indexOut, out _));
        Assert.Null(tight.Work.Overflow);
    }

    [Fact]
    public void TheVertexAndIndexRoomsAreEachCheckedAtTheirExactBoundary()
    {
        var design = TestDesigns.Of(new DesignBox(default, new GridSize(2, 1, 1), Core), new DesignBox(new GridSize(3, 0, 0), new GridSize(1, 2, 1), Plate));
        var builder = new ShieldSurface(1 << 15);
        var vertices = builder.Build(design, design.FullSet, ShellShape.Default, _vertexOut, _indexOut, out var indices);
        Assert.True(vertices > 0 && indices > 0);

        // The builder's own spans: exactly enough is enough; one short of either stops it, naming which.
        Assert.Equal(vertices, builder.Build(design, design.FullSet, ShellShape.Default, _vertexOut.AsSpan(0, vertices), _indexOut.AsSpan(0, indices), out var exact));
        Assert.Equal(indices, exact);
        Assert.Equal(-1, builder.Build(design, design.FullSet, ShellShape.Default, _vertexOut.AsSpan(0, vertices - 1), _indexOut, out _));
        Assert.Equal("vertices", builder.Work.Overflow);
        Assert.Equal(-1, builder.Build(design, design.FullSet, ShellShape.Default, _vertexOut, _indexOut.AsSpan(0, indices - 1), out _));
        Assert.Equal("indices", builder.Work.Overflow);

        // Admission's limits: each checked, each named, and a design refused keeps no shell.
        Assert.True(GeometryAdmission.Measure(design, vertices, ShellShape.Default, indices).Fits);
        var kept = design.IntactShell;
        var fewer = TestDesigns.Of(design.Boxes.ToArray());
        Assert.False(GeometryAdmission.Measure(fewer, vertices - 1, ShellShape.Default, indices).Fits);
        Assert.False(GeometryAdmission.Measure(fewer, vertices, ShellShape.Default, indices - 1).Fits);
        Assert.True(fewer.IntactShell.IsEmpty && fewer.IntactIndices.IsEmpty);
        Assert.Contains($"{vertices} of {vertices - 1} vertices", Assert.Throws<InvalidDataException>(() => GeometryAdmission.Validate(fewer, vertices - 1, ShellShape.Default, indices)).Message);
        Assert.Contains($"{indices} of {indices - 1} indices", Assert.Throws<InvalidDataException>(() => GeometryAdmission.Validate(fewer, vertices, ShellShape.Default, indices - 1)).Message);
        Assert.Equal(kept.Length, design.IntactShell.Length);
        Assert.Equal(indices, design.IntactIndices.Length);
    }

    [Fact]
    public void SixtyFourSeparateCubesAreAdmittedWholeAndEightHundredAreRefusedForTheirVertices()
    {
        DesignBox[] Spread(int count)
        {
            var boxes = new DesignBox[count];
            for (var i = 0; i < count; i++)
                boxes[i] = new DesignBox(new GridSize(i * 4, 0, 0), new GridSize(1, 1, 1), i == 0 ? Core : Plate);
            return boxes;
        }

        var spread = TestDesigns.Of(Spread(64));
        GeometryAdmission.Validate(spread, Content.Blocks.Limits);
        Assert.InRange(spread.IntactShell.Length, 1, GeometryAdmission.MaxVertices);
        Assert.InRange(spread.IntactIndices.Length, 1, GeometryAdmission.MaxIndices);
        Holds(spread, spread.FullSet, Intact(spread));

        // 800 separate cubes, each an exact rounded box of 80 vertices: 64,000, past the 32,768 there is room for.
        var many = TestDesigns.Of(Spread(800));
        Assert.Contains("more than 32768 vertices", Assert.Throws<InvalidDataException>(() => GeometryAdmission.Validate(many, Content.Blocks.Limits)).Message);
        Assert.True(many.IntactShell.IsEmpty);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(0, -1)]
    [InlineData(1, 1)]
    [InlineData(1, -1)]
    [InlineData(2, 1)]
    [InlineData(2, -1)]
    public void ABlockOverTheMiddleOfAFlatFaceKeepsItsOwnShield(int axis, int sign)
    {
        // A 10 x 10 plate one block thick across the axis, and a wedge two cells off the middle of one face: inside the
        // plate's flat stretch, but outside the reach its certificate once checked. Its shield must be whole. A cube
        // on the plate's edge, and the wedge, keep both parts off the rounded-box path, so the sampling meets them.
        Span<int> size = [10, 10, 10];
        size[axis] = 1;
        Span<int> at = [4, 4, 4];
        at[axis] = sign > 0 ? 3 : -3;
        Span<int> side = [0, 0, 0];
        side[(axis + 1) % 3] = -1;
        var design = TestDesigns.Of(new DesignBox(default, new GridSize(size[0], size[1], size[2]), Core),
            new DesignBox(new GridSize(at[0], at[1], at[2]), new GridSize(1, 1, 1), Plate, BlockShape.WedgeZPlusPlus),
            new DesignBox(new GridSize(side[0], side[1], side[2]), new GridSize(1, 1, 1), Plate));
        var shell = Shell(design, design.FullSet);
        Holds(design, design.FullSet, shell);
        // The cube's own shell stands round it: out of its middle along every axis, the shell is met within reach.
        var cube = design.BlockOffsets[1];
        Assert.Equal(1.0, Winding(shell, cube), 3);
        Assert.Equal(0.0, Winding(shell, cube + new Vector3(0, 0, 0) + Axis(axis) * sign * 1.5f), 3);
        // The plate still takes its flat middle whole on the other face and next to the cube.
        var plate = TestDesigns.Of(new DesignBox(default, new GridSize(size[0], size[1], size[2]), Core),
            new DesignBox(new GridSize(side[0], side[1], side[2]), new GridSize(1, 1, 1), Plate));
        Holds(plate, plate.FullSet, Shell(plate, plate.FullSet));
    }

    private static Vector3 Axis(int axis) => axis == 0 ? Vector3.UnitX : axis == 1 ? Vector3.UnitY : Vector3.UnitZ;

    [Theory]
    [InlineData(0.1f, 0f)]
    [InlineData(0.5f, 0.3f)]
    [InlineData(1f, 0.3f)]
    public void AFlatMiddleStandsOnTheSameSampledPlaneAsTheSameUnionOfCubes(float roundness, float blend)
    {
        // Merged, the plate's middle is one fan; as cubes it is swept strips. Both must be the same flat plane, at the
        // sampled crossing its edges have, facing straight out.
        var shape = new ShellShape(roundness, blend);
        // A cube on one side keeps the part off the rounded-box path, so its flat middle is sampled.
        var lump = new DesignBox(new GridSize(-1, 0, 0), new GridSize(1, 1, 1), Plate);
        var merged = TestDesigns.Of(new DesignBox(default, new GridSize(6, 6, 1), Core), lump);
        var cubes = new List<DesignBox> { lump };
        for (var x = 0; x < 6; x++) for (var y = 0; y < 6; y++) cubes.Add(new DesignBox(new GridSize(x, y, 0), new GridSize(1, 1, 1), x + y == 0 ? Core : Plate));
        var split = TestDesigns.Of([.. cubes]);
        var whole = Shell(merged, merged.FullSet, shape);
        var parts = Shell(split, split.FullSet, shape);
        Holds(merged, merged.FullSet, whole);
        Holds(split, split.FullSet, parts);
        // Both in grid space: hulls of different centres of mass.
        Vector3 Grid(HullDesign d, Vector3 p) => p / d.BlockSize + new Vector3((float)d.Pivot.X, (float)d.Pivot.Y, (float)d.Pivot.Z);
        for (var x = 1.2f; x <= 3.8f; x += 0.65f)
            for (var y = 1.2f; y <= 3.8f; y += 0.65f)
            {
                var (top, normal) = Down(merged, whole, x, y);
                var (other, otherNormal) = Down(split, parts, x, y);
                Assert.Equal(top, other, 4);
                // Away from the rounded edges, where vertex normals blend into theirs, the plane faces straight out.
                if (x is < 1.8f or > 3.2f || y is < 1.8f or > 3.2f) continue;
                Assert.True(Vector3.Dot(normal, Vector3.UnitZ) > 0.9999f, $"merged normal {normal} at {x}, {y}");
                Assert.True(Vector3.Dot(otherNormal, Vector3.UnitZ) > 0.9999f, $"split normal {otherNormal} at {x}, {y}");
            }

        (float Height, Vector3 Normal) Down(HullDesign d, ShellVertex[] mesh, float x, float y)
        {
            // Straight down from above, in the design's grid: the first triangle met, its height and its corners' normal there.
            for (var i = 0; i < mesh.Length; i += 3)
            {
                var a = Grid(d, mesh[i].Position);
                var b = Grid(d, mesh[i + 1].Position);
                var c = Grid(d, mesh[i + 2].Position);
                var area = (b.X - a.X) * (c.Y - a.Y) - (c.X - a.X) * (b.Y - a.Y);
                if (MathF.Abs(area) < 1e-9f) continue;
                var l1 = ((b.X - x) * (c.Y - y) - (c.X - x) * (b.Y - y)) / area;
                var l2 = ((c.X - x) * (a.Y - y) - (a.X - x) * (c.Y - y)) / area;
                var l3 = 1 - l1 - l2;
                if (l1 < -1e-6f || l2 < -1e-6f || l3 < -1e-6f) continue;
                var z = l1 * a.Z + l2 * b.Z + l3 * c.Z;
                if (z < 0.5f) continue;
                return (z, Vector3.Normalize(l1 * mesh[i].Normal + l2 * mesh[i + 1].Normal + l3 * mesh[i + 2].Normal));
            }

            throw new InvalidOperationException($"nothing over {x}, {y}");
        }
    }

    [Fact]
    public void TreeVisitsAreChargedBeforeEveryQueryWithAnExactBudgetAndReuse()
    {
        // 300 plates stacked face to face: their flat faces are checked first, and with the visits a one-cell builder
        // allows that runs out of visits, not of cells.
        var stack = new DesignBox[300];
        for (var i = 0; i < stack.Length; i++)
            stack[i] = new DesignBox(new GridSize(0, 0, i), new GridSize(6, 6, 1), i == 0 ? Core : Plate);
        var tall = TestDesigns.Of(stack);
        var starved = new ShieldSurface(1);
        Assert.Equal(-1, starved.Build(tall, tall.FullSet, ShellShape.Default, _vertexOut, _indexOut, out _));
        Assert.Equal("visits", starved.Work.Overflow);
        Assert.True(starved.Work.Visits <= 4096);

        var design = TestDesigns.Of(new DesignBox(default, new GridSize(8, 8, 2), Core), new DesignBox(new GridSize(3, 3, 2), new GridSize(2, 2, 1), Plate));
        var small = TestDesigns.Of(new DesignBox(default, new GridSize(1, 1, 1), Core));
        var free = new ShieldSurface(1 << 15);
        var whole = free.Build(design, design.FullSet, ShellShape.Default, _vertexOut, _indexOut, out _);
        var needed = free.Work.Visits;
        Assert.True(whole > 0 && needed > 0);
        var exact = new ShieldSurface(1 << 15, 0, GeometryAdmission.MaxVertices, needed);
        Assert.Equal(whole, exact.Build(design, design.FullSet, ShellShape.Default, _vertexOut, _indexOut, out _));
        Assert.Equal(needed, exact.Work.Visits);
        var tight = new ShieldSurface(1 << 15, 0, GeometryAdmission.MaxVertices, needed - 1);
        Assert.Equal(-1, tight.Build(design, design.FullSet, ShellShape.Default, _vertexOut, _indexOut, out _));
        Assert.Equal("visits", tight.Work.Overflow);
        Assert.True(tight.Work.Visits <= needed - 1);
        Assert.True(tight.Build(small, small.FullSet, ShellShape.Default, _vertexOut, _indexOut, out _) > 0);
        Assert.Null(tight.Work.Overflow);
    }

    [Fact]
    public void TheScratchReportedIsEveryArrayTheBuilderHolds()
    {
        var builder = new ShieldSurface(1 << 12);
        long sum = 0;
        foreach (var field in typeof(ShieldSurface).GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic))
        {
            if (field.GetValue(builder) is not Array array) continue;
            var size = (int)typeof(System.Runtime.CompilerServices.Unsafe).GetMethod(nameof(System.Runtime.CompilerServices.Unsafe.SizeOf))!
                .MakeGenericMethod(array.GetType().GetElementType()!).Invoke(null, null)!;
            sum += (long)array.Length * size;
        }

        Assert.Equal(sum, builder.ScratchBytes);
    }

    [Theory]
    [InlineData(3, 2, 4, 0, 0, 0)]
    [InlineData(1, 5, 3, 7, -4, 2)]
    [InlineData(1, 1, 1, -3, 9, 5)]
    public void ASeparateBoxIsTheSameExactRoundedBoxWhetherOneBlockOrBuiltOfCubesInAnyOrder(int x, int y, int z, int ax, int ay, int az)
    {
        var at = new GridSize(ax, ay, az);
        var merged = TestDesigns.Of(new DesignBox(at, new GridSize(x, y, z), Core));
        var cubes = new List<DesignBox>();
        for (var i = 0; i < x; i++) for (var j = 0; j < y; j++) for (var k = 0; k < z; k++)
                    cubes.Add(new DesignBox(new GridSize(ax + i, ay + j, az + k), new GridSize(1, 1, 1), i + j + k == 0 ? Core : Plate));
        var unit = TestDesigns.Of([.. cubes]);
        cubes.Reverse();
        (cubes[0], cubes[^1]) = (cubes[^1], cubes[0]);
        var reversed = TestDesigns.Of([.. cubes]);
        var expected = Shape(merged, Shell(merged, merged.FullSet), merged);
        foreach (var design in new[] { merged, unit, reversed })
        {
            var builder = new ShieldSurface(1 << 12);
            var count = builder.Build(design, design.FullSet, ShellShape.Default, _vertexOut, _indexOut, out var indices);
            Assert.Equal((80, 468, 1, 0), (count, indices, builder.Work.RoundedBoxes, builder.Work.Cells));
            var shell = Expand(_vertexOut.AsSpan(0, count), _indexOut.AsSpan(0, indices));
            Holds(design, design.FullSet, shell);
            Same(expected, Shape(design, shell, merged));
            // Every vertex is on the exact target, the margin off the box, its normal straight out from the box there.
            var low = new Vector3(float.MaxValue);
            var high = new Vector3(float.MinValue);
            for (var b = 0; b < design.BlockCount; b++)
            {
                low = Vector3.Min(low, design.BlockOffsets[b] - design.BlockHalfExtents[b]);
                high = Vector3.Max(high, design.BlockOffsets[b] + design.BlockHalfExtents[b]);
            }

            foreach (var vertex in _vertexOut.AsSpan(0, count))
            {
                var near = Vector3.Clamp(vertex.Position, low, high);
                Assert.Equal(GeometryAdmission.Margin * design.BlockSize, Vector3.Distance(vertex.Position, near), 4);
                Assert.True(Vector3.Dot(Vector3.Normalize(vertex.Position - near), vertex.Normal) > 0.9999f);
            }

            // Across every triangle the shell stays within the stated chord error of the target: at most 0.0751 of the
            // radius in from it, and never out past it.
            var margin = GeometryAdmission.Margin * design.BlockSize;
            for (var i = 0; i < shell.Length; i += 3)
                for (var sStep = 0; sStep <= 6; sStep++)
                    for (var tStep = 0; tStep <= 6 - sStep; tStep++)
                    {
                        var p = shell[i].Position + (shell[i + 1].Position - shell[i].Position) * (sStep / 6f) + (shell[i + 2].Position - shell[i].Position) * (tStep / 6f);
                        Assert.InRange(Vector3.Distance(p, Vector3.Clamp(p, low, high)), margin * (1 - 0.0752f), margin + 1e-4f);
                    }
        }
    }

    [Theory]
    [InlineData(0.1f, 0f)]
    [InlineData(0.5f, 0.3f)]
    public void ARoundedBoxOfLowRoundnessStillEnclosesItsBox(float roundness, float blend)
    {
        var design = TestDesigns.Of(new DesignBox(default, new GridSize(4, 1, 2), Core));
        var shell = Shell(design, design.FullSet, new ShellShape(roundness, blend));
        Holds(design, design.FullSet, shell);
    }

    [Fact]
    public void BoxesACellApartAreSampledAndCloseButFourCellsApartAreTwoRoundedBoxes()
    {
        foreach (var (gap, rounded) in new[] { (1, 0), (2, 2), (4, 2) })
        {
            var design = TestDesigns.Of(new DesignBox(default, new GridSize(3, 3, 3), Core),
                new DesignBox(new GridSize(3 + gap, 0, 0), new GridSize(3, 3, 3), Plate));
            var builder = new ShieldSurface(1 << 16);
            var count = builder.Build(design, design.FullSet, ShellShape.Default, _vertexOut, _indexOut, out var indices);
            Assert.Equal(rounded, builder.Work.RoundedBoxes);
            var shell = Expand(_vertexOut.AsSpan(0, count), _indexOut.AsSpan(0, indices));
            Holds(design, design.FullSet, shell);
            var middle = (design.BlockOffsets[0] + design.BlockOffsets[1]) * 0.5f;
            Assert.Equal(gap == 1 ? 1.0 : 0.0, Winding(shell, middle), 3);
        }
    }

    [Fact]
    public void AWarmBuildAllocatesNothing()
    {
        var design = TestDesigns.Of(ClientBoxGeometryTests.Packed(new GridSize(4, 3, 4), 5));
        var builder = new ShieldSurface(1 << 15);
        var alive = design.FullSet.ToArray();
        BlockSet.Remove(alive, 3);
        builder.Build(design, alive, ShellShape.Default, _vertexOut, _indexOut, out _);
        builder.Build(design, design.FullSet, ShellShape.Default, _vertexOut, _indexOut, out _);
        var allocation = new AllocationProbe();
        while (allocation.Measuring()) builder.Build(design, alive, ShellShape.Default, _vertexOut, _indexOut, out _);
        while (allocation.Measuring()) builder.Build(design, design.FullSet, ShellShape.Default, _vertexOut, _indexOut, out _);
        Assert.Equal(0, allocation.Bytes);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Drawn shields.
    // ---------------------------------------------------------------------------------------------------------------

    private static readonly HullDesign Pod = Content.Pod;
    private static readonly int Emitter = Array.FindIndex(Pod.Types, t => t.Shield > 0);
    private readonly ShieldDraw[] _draws = new ShieldDraw[ShieldVisuals.MaxShields];
    private readonly ShieldInstance[] _instances = new ShieldInstance[ShieldVisuals.MaxShields];
    private readonly MeshVertex[] _vertices = new MeshVertex[ShieldVisuals.MaxVertices];
    private readonly ushort[] _indices = new ushort[ShieldVisuals.MaxIndices];
    private long _publication;

    private static HullView Hull(uint id, ulong blocks, uint revision = 0) => new()
    {
        Id = id,
        Design = (byte)Content.PodDesign,
        Blocks = BlockMasks.Set(blocks),
        Revision = revision,
        Stats = Pod.StatsOf([blocks]),
        Orientation = Quaternion.Identity,
        Shield = 1500f,
    };

    /// <summary>A frame of a new publication; the vertices the hull's shield is drawn with, empty if none.</summary>
    private ReadOnlySpan<ShellVertex> Draw(ShieldVisuals shields, HullView hull)
    {
        _publication++;
        shields.Update([hull], Content.Designs, _publication, 1f / 60f);
        shields.Begin(_draws);
        shields.Add(hull.Id, Vector3.Zero, Quaternion.Identity, 1f, 1f, _instances, _vertices, _indices);
        return shields.Draws.Length == 0 ? default
            : MemoryMarshal.Cast<MeshVertex, ShellVertex>(_vertices.AsSpan(shields.Draws[0].FirstVertex, shields.Draws[0].VertexCount));
    }

    /// <summary>Ten frames of new publications of the same hulls, allocating nothing of their own.</summary>
    private void Frames(ShieldVisuals shields, HullView[] hulls)
    {
        for (var i = 0; i < 10; i++)
        {
            _publication++;
            shields.Update(hulls, Content.Designs, _publication, 1f / 60f);
            shields.Begin(_draws);
            shields.Add(hulls[0].Id, Vector3.Zero, Quaternion.Identity, 1f, 1f, _instances, _vertices, _indices);
        }
    }

    private static void SameVertices(ReadOnlySpan<ShellVertex> expected, ReadOnlySpan<ShellVertex> actual)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (var i = 0; i < expected.Length; i++)
            Assert.Equal(expected[i].Position, actual[i].Position);
    }

    /// <summary>The indices the last drawn shell was drawn with.</summary>
    private ReadOnlySpan<ushort> DrawnIndices(ShieldVisuals shields) =>
        shields.Draws.Length == 0 ? default : _indices.AsSpan(shields.Draws[0].FirstIndex, shields.Draws[0].IndexCount);

    [Fact]
    public void EachShellIsDrawnFromItsOwnRangesAndOneThatDoesNotFitIsLeftOutWhole()
    {
        var shields = new ShieldVisuals(ShieldLook.Load());
        var full = Pod.FullMask();
        var damaged = full & ~(1UL << (Emitter == 1 ? 2 : 1));
        HullView[] hulls = [Hull(1, full), Hull(2, damaged, 1)];
        _publication++;
        shields.Update(hulls, Content.Designs, _publication, 1f / 60f);
        shields.Begin(_draws);
        shields.Add(1, Vector3.Zero, Quaternion.Identity, 1f, 1f, _instances, _vertices, _indices);
        shields.Add(2, Vector3.Zero, Quaternion.Identity, 1f, 1f, _instances, _vertices, _indices);
        Assert.Equal(2, shields.Draws.Length);
        var first = shields.Draws[0];
        var second = shields.Draws[1];
        Assert.Equal((0, 0), (first.FirstVertex, first.FirstIndex));
        Assert.Equal((first.VertexCount, first.IndexCount), (second.FirstVertex, second.FirstIndex));
        // Indices count from their own shell's first vertex, as the draw's vertex offset adds it.
        foreach (var draw in new[] { first, second })
            foreach (var index in _indices.AsSpan(draw.FirstIndex, draw.IndexCount))
                Assert.InRange(index, 0, draw.VertexCount - 1);
        Assert.Equal(Count(Pod, [damaged], Pod.ShellShape, out var damagedIndices), second.VertexCount);
        Assert.Equal(damagedIndices, second.IndexCount);

        // Room for the first shell's indices and one fewer than the second's: the second is left out, not cut.
        shields.Begin(_draws);
        var room = _indices.AsSpan(0, first.IndexCount + second.IndexCount - 1);
        shields.Add(1, Vector3.Zero, Quaternion.Identity, 1f, 1f, _instances, _vertices, room);
        shields.Add(2, Vector3.Zero, Quaternion.Identity, 1f, 1f, _instances, _vertices, room);
        Assert.Equal(1, shields.Draws.Length);
    }

    [Fact]
    public void ADamagedShellThatDoesNotFitIsTheAdmittedIntactOneNeverEmptyAndAllocatesNothing()
    {
        var shields = new ShieldVisuals(ShieldLook.Load(), null, damagedRoom: 30);
        var full = Pod.FullMask();
        SameVertices(Pod.IntactShell.Span, Draw(shields, Hull(1, full)));
        var damaged = full & ~(1UL << (Emitter == 1 ? 2 : 1));
        SameVertices(Pod.IntactShell.Span, Draw(shields, Hull(1, damaged, 1)));
        // Once refused, the damage set keeps the intact shell, indices too, and is not built again.
        SameVertices(Pod.IntactShell.Span, Draw(shields, Hull(1, damaged, 1)));
        Assert.True(Pod.IntactIndices.Span.SequenceEqual(DrawnIndices(shields)));
        HullView[] view = [Hull(1, damaged, 1)];
        Frames(shields, view);
        var allocation = new AllocationProbe();
        while (allocation.Measuring()) Frames(shields, view);
        Assert.Equal(0, allocation.Bytes);
    }

    [Fact]
    public void ShellsBuiltOnTheWorkerFollowTheNewestLossAndDropWorkFromAnEndedSession()
    {
        using var jobs = new JobSystem(1, "Shells");
        var shields = new ShieldVisuals(ShieldLook.Load(), jobs);
        var full = Pod.FullMask();
        var losses = new List<int>();
        for (var b = 0; b < Pod.BlockCount && losses.Count < 2; b++)
            if (b != Emitter && !Pod.Types[b].Core) losses.Add(b);
        var first = full & ~(1UL << losses[0]);
        var both = first & ~(1UL << losses[1]);
        int Own(ulong mask) => Count(Pod, [mask], Pod.ShellShape, out _);

        SameVertices(Pod.IntactShell.Span, Draw(shields, Hull(7, full)));
        // A loss: the intact shell stays drawn while the worker builds; a second loss comes before it is done.
        SameVertices(Pod.IntactShell.Span, Draw(shields, Hull(7, first, 1)));
        SameVertices(Pod.IntactShell.Span, Draw(shields, Hull(7, both, 2)));
        jobs.CompleteAll();
        // The first loss's shell encloses what is left, and is drawn while the newest is built.
        Assert.Equal(Own(first), Draw(shields, Hull(7, both, 2)).Length);
        jobs.CompleteAll();
        Assert.Equal(Own(both), Draw(shields, Hull(7, both, 2)).Length);

        // A build still running when the session ends is thrown away: the next hull of that number starts intact.
        Draw(shields, Hull(8, full));
        Draw(shields, Hull(8, first, 1));
        shields.Clear();
        jobs.CompleteAll();
        SameVertices(Pod.IntactShell.Span, Draw(shields, Hull(8, full)));

        // The shield's charge and capacity are the hull's own, now: losing the emitter takes the shield down at once,
        // whatever shell is still drawn.
        Draw(shields, Hull(9, full));
        var bare = full & ~(1UL << Emitter);
        _publication++;
        shields.Update([Hull(9, bare, 1) with { Stats = Pod.StatsOf([bare]) }], Content.Designs, _publication, 1f / 60f);
        shields.Begin(_draws);
        shields.Add(9, Vector3.Zero, Quaternion.Identity, 1f, 1f, _instances, _vertices, _indices);
        Assert.True(shields.Draws.Length == 0 || _instances[0].Params.Z > 0f, "a shield without its emitter is not shown online");
        jobs.CompleteAll();
    }
}
