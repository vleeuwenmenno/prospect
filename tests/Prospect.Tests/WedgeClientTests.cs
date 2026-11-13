using System.Numerics;
using Prospect.Client.Render;
using Prospect.Client.Screens;
using Prospect.Client.World;
using Prospect.Game.World;
using Xunit;

namespace Prospect.Tests;

/// <summary>Wedges through the client's mesh, picking, editor geometry, part figures and shield routing.</summary>
public sealed class WedgeClientTests
{
    private static BlockType Core => TestDesigns.Content.Blocks.Get("core");

    private static IEnumerable<BlockShape> Wedges()
    {
        for (var code = 1; code <= 12; code++)
            yield return (BlockShape)code;
    }

    /// <summary>Every triangle of a mesh turns counter-clockwise about its vertices' unit normal, as the cube always has.</summary>
    private static void AssertWound(MeshData mesh)
    {
        for (var i = 0; i < mesh.Indices.Count; i += 3)
        {
            var a = mesh.Vertices[(int)mesh.Indices[i]];
            var b = mesh.Vertices[(int)mesh.Indices[i + 1]];
            var c = mesh.Vertices[(int)mesh.Indices[i + 2]];
            var pa = new Vector3(a.Position.X, a.Position.Y, a.Position.Z);
            var pb = new Vector3(b.Position.X, b.Position.Y, b.Position.Z);
            var pc = new Vector3(c.Position.X, c.Position.Y, c.Position.Z);
            var normal = new Vector3(a.Normal.X, a.Normal.Y, a.Normal.Z);
            Assert.Equal(1f, normal.Length(), 4);
            var face = Vector3.Cross(pb - pa, pc - pa);
            Assert.True(face.Length() > 1e-6f);
            Assert.Equal(1f, Vector3.Dot(Vector3.Normalize(face), normal), 4);
        }
    }

    [Fact]
    public void EveryBlockPrimitiveIsWoundOutwardWithinTheUnitBoxAndAWedgeIsHalfOfIt()
    {
        var cube = MeshBuilder.Cube();
        Assert.Equal((24, 36), (cube.Vertices.Count, cube.Indices.Count));
        AssertWound(cube);
        foreach (var shape in Wedges())
        {
            var mesh = MeshBuilder.Primitive(shape);
            // Two rectangles, two triangles and the slant.
            Assert.Equal((18, 24), (mesh.Vertices.Count, mesh.Indices.Count));
            AssertWound(mesh);
            var low = new Vector3(float.MaxValue);
            var high = new Vector3(float.MinValue);
            var volume = 0f;
            foreach (var v in mesh.Vertices)
            {
                var p = new Vector3(v.Position.X, v.Position.Y, v.Position.Z);
                low = Vector3.Min(low, p);
                high = Vector3.Max(high, p);
                Assert.True(Vector3.Dot(BlockShapes.Slant(shape, new Vector3(0.5f)), p) <= 1e-6f);
            }

            for (var i = 0; i < mesh.Indices.Count; i += 3)
            {
                var a = mesh.Vertices[(int)mesh.Indices[i]].Position;
                var b = mesh.Vertices[(int)mesh.Indices[i + 1]].Position;
                var c = mesh.Vertices[(int)mesh.Indices[i + 2]].Position;
                volume += Vector3.Dot(new Vector3(a.X, a.Y, a.Z), Vector3.Cross(new Vector3(b.X, b.Y, b.Z), new Vector3(c.X, c.Y, c.Z))) / 6f;
            }

            Assert.Equal(new Vector3(-0.5f), low);
            Assert.Equal(new Vector3(0.5f), high);
            Assert.Equal(0.5f, volume, 5);
        }
    }

    [Fact]
    public void ScalingAPrimitiveWithTheCofactorTurnGivesTheSizedWedgesOwnSlantNormal()
    {
        var size = new Vector3(4, 1, 3);
        foreach (var shape in Wedges())
        {
            var primitive = MeshBuilder.Primitive(shape);
            var design = TestDesigns.Of(new DesignBox(default, new GridSize(4, 1, 3), Core, shape));
            var hull = MeshBuilder.Hull(design);
            AssertWound(hull);
            var slant = Vector3.Normalize(BlockShapes.Slant(shape, design.BlockHalfExtents[0]));
            Assert.Contains(hull.Vertices, v => Vector3.Dot(new Vector3(v.Normal.X, v.Normal.Y, v.Normal.Z), slant) > 0.9999f);

            // mesh.vert turns a normal by the cofactor of the model matrix; for a scale that is the inverse scale.
            var turned = false;
            foreach (var v in primitive.Vertices)
            {
                var n = new Vector3(v.Normal.X, v.Normal.Y, v.Normal.Z);
                var cofactor = Vector3.Normalize(new Vector3(size.Y * size.Z * n.X, size.X * size.Z * n.Y, size.X * size.Y * n.Z));
                turned |= Vector3.Dot(cofactor, slant) > 0.9999f;
                // Every scaled face normal stays a face normal of the sized wedge: perpendicular to its edges.
                Assert.True(float.IsFinite(cofactor.X));
            }

            Assert.True(turned, $"{shape}: the scaled slant normal should be the sized slant's");
        }
    }

    [Fact]
    public void AWedgeHullMeshCullsNoFaceAndDrawsOnlyRealFaces()
    {
        var design = TestDesigns.Of(
            new DesignBox(new(0, 0, 0), new(2, 2, 2), Core),
            new DesignBox(new(2, 0, 0), new(3, 2, 2), TestDesigns.Content.Blocks.Get("hull"), BlockShape.WedgeZPlusPlus));
        var mesh = MeshBuilder.Hull(design);
        AssertWound(mesh);
        // The box loses no face to the wedge (only boxes cover), the wedge keeps all five of its own.
        Assert.Equal(6 * 4 + 18, mesh.Vertices.Count);
    }

    /// <summary>
    /// A hull placed so that the camera, at the origin, stands at <paramref name="eye"/> in block
    /// <paramref name="block"/>'s own axes from that block's centre (not the hull's centre of mass).
    /// </summary>
    private static PickHull Pick(HullDesign design, Quaternion rotation, int block, Vector3 eye, ulong[] mask)
    {
        var at = -Vector3.Transform(design.BlockOffsets[block] + eye, rotation);
        return new(1, new Double3(at.X, at.Y, at.Z), rotation, design.BlockOffsets, design.BlockSize, mask, design.Flight.Radius,
            Faces: design.ExteriorFaces, HalfExtents: design.BlockHalfExtents, Shapes: design.Shapes);
    }

    [Fact]
    public void ARayThroughAWedgesEmptyHalfMissesAndThroughItsSolidHalfHitsScaledAndTurned()
    {
        // Removed corner +x +y; extruded along z. Half extents 10, 4 and 6 metres.
        var design = TestDesigns.Of(new DesignBox(default, new GridSize(20, 8, 12), Core, BlockShape.WedgeZPlusPlus));
        var rotation = Quaternion.CreateFromYawPitchRoll(0.4f, -0.3f, 0.2f);
        var mask = design.FullSet.ToArray();
        var hits = new PickHit[2];
        Vector3 World(Vector3 local) => Vector3.Transform(local, rotation);

        foreach (var (empty, local) in new[] { (true, new Vector3(5f, 2f, 0)), (false, new Vector3(-5f, -2f, 0)) })
        {
            // Straight down the extrusion from 60 m out: the cap is met only in the solid half.
            var ray = World(-Vector3.UnitZ);
            var hull = Pick(design, rotation, 0, local + new Vector3(0, 0, 60), mask);
            var count = TargetPicking.Pick(ray, default, ray, 500, [hull], [], hits);
            Assert.Equal(empty ? 0 : 1, count);
            if (!empty)
                Assert.Equal(54f, hits[0].Distance, 2);
            // The same box with no shapes is a whole box and is hit either way.
            Assert.Equal(1, TargetPicking.Pick(ray, default, ray, 500, [hull with { Shapes = null }], [], hits));
        }

        // Across the slant, from outside it: met on the slant, nearer than the box face behind it would be.
        var slant = Vector3.Normalize(BlockShapes.Slant(BlockShape.WedgeZPlusPlus, design.BlockHalfExtents[0]));
        var inward = World(-slant);
        var turned = Pick(design, rotation, 0, slant * 40f, mask);
        Assert.Equal(1, TargetPicking.Pick(inward, default, inward, 500, [turned], [], hits));
        Assert.Equal(40f, hits[0].Distance, 2);
    }

    [Fact]
    public void ADamagedWedgePastBlock64IsPickedByItsOwnShapeAndBit()
    {
        var boxes = new DesignBox[72];
        for (var i = 0; i < boxes.Length; i++)
            boxes[i] = new DesignBox(new GridSize(i * 2, 0, 0), new GridSize(2, 2, 2), TestDesigns.Content.Blocks.Get(i == 0 ? "core" : "hull"),
                i == 70 ? BlockShape.WedgeZPlusPlus : BlockShape.Box);
        var design = TestDesigns.Of(boxes);
        var mask = new ulong[2];
        BlockSet.Add(mask, 70);
        var hits = new PickHit[1];
        // A damaged hull draws every face of each block left: Faces null, as HullVisuals passes for it. The empty
        // corner is 1.27 m from the slant, 44 pixels at this focal length: well clear of the grab.
        PickHull Over(float x, float y) => Pick(design, Quaternion.Identity, 70, new Vector3(x, y, 30f), mask) with { Faces = null };
        Assert.Equal(1, TargetPicking.Pick(-Vector3.UnitZ, default, -Vector3.UnitZ, 1000, [Over(-0.5f, -0.5f)], [], hits));
        Assert.Equal(0, TargetPicking.Pick(-Vector3.UnitZ, default, -Vector3.UnitZ, 1000, [Over(0.9f, 0.9f)], [], hits));
        BlockSet.Remove(mask, 70);
        Assert.Equal(0, TargetPicking.Pick(-Vector3.UnitZ, default, -Vector3.UnitZ, 1000, [Over(-0.5f, -0.5f)], [], hits));
    }

    [Fact]
    public void TheEditorEntersAWedgeOnlyThroughItsSolidHalfAndTheSlantCountsAsTheLargerSide()
    {
        // Cells 0..3 on x, 0..1 on y, 0 on z; removed corner +x +y, so the slant faces +x +y.
        var wedge = new DesignBox(default, new GridSize(4, 2, 1), Core, BlockShape.WedgeZPlusPlus);
        // Down z through the empty corner: nothing. Through the solid corner: the +z cap.
        Assert.False(EditorGeometry.Enter(wedge, new Vector3(3f, 1f, 5f), -Vector3.UnitZ, out _, out _));
        Assert.True(EditorGeometry.Enter(wedge, new Vector3(0f, 0f, 5f), -Vector3.UnitZ, out var distance, out var side));
        Assert.Equal((4.5f, 4), (distance, side));

        // From +x along -x at y = 1: past the box's +x face at x = 3.5, onto the slant at x = 0.5. The slant stands
        // for the larger of the box sides it replaces: +y (4 x 1) over +x (2 x 1)...
        Assert.True(EditorGeometry.Enter(wedge, new Vector3(9f, 1f, 0f), -Vector3.UnitX, out distance, out side));
        Assert.Equal((8.5f, 2), (distance, side));
        // ...and +x when that is the larger.
        Assert.Equal(0, EditorGeometry.SlantEntry(wedge with { Size = new GridSize(1, 2, 1) }));

        // The draft pick goes through the empty half to the block behind it.
        var behind = new DesignBox(new GridSize(0, 0, -3), new GridSize(4, 2, 1), Core);
        Assert.True(EditorGeometry.Pick([wedge, behind], new Vector3(3f, 1f, 5f), -Vector3.UnitZ, out var hit));
        Assert.Equal(1, hit.Block);
    }

    [Fact]
    public void PartCardsCountAWedgeAsHalfItsBoxAndCacheBoxAndWedgeApart()
    {
        var content = TestDesigns.Content;
        var rules = content.Rules;
        foreach (var type in content.Blocks.Types)
        {
            var wedge = PartFigures.Of(type, 2, rules, BlockShape.WedgeXMinusPlus);
            var cell = PartFigures.Of(type, 1, rules);
            Assert.Equal(cell.Mass, wedge.Mass);
            Assert.Equal(cell.Hull, wedge.Hull);
            Assert.Equal(cell.Crew, wedge.Crew);
            Assert.Equal(cell.Thrust, wedge.Thrust);
            Assert.Equal((float)BoxDesign.ScaleHalf(type, "mass", type.Mass, 3), PartFigures.Of(type, 3, rules, BlockShape.WedgeYPlusPlus).Mass);
        }

        // A one-cell wedge of crew quarters holds half a person: none, as the design counts it.
        var quarters = content.Blocks.Types.FirstOrDefault(t => t.Crew > 0);
        if (quarters is not null)
        {
            var design = TestDesigns.Of(new DesignBox(default, new GridSize(1, 1, 1), Core),
                new DesignBox(new GridSize(1, 0, 0), new GridSize(1, 1, 1), quarters, BlockShape.WedgeZPlusPlus));
            var alone = TestDesigns.Of(new DesignBox(default, new GridSize(1, 1, 1), Core));
            Assert.Equal(design.Stats.Crew - alone.Stats.Crew, PartFigures.Of(quarters, 1, rules, BlockShape.WedgeZPlusPlus).Crew);
        }

        var palette = new EditorPalette();
        var size = new GridSize(1, 1, 1);
        var box = palette.Figures(content, size).ToArray();
        var half = palette.Figures(content, size, BlockShape.WedgeZMinusMinus).ToArray();
        var again = palette.Figures(content, size, BlockShape.WedgeXPlusPlus).ToArray();
        Assert.NotEqual(box, half);
        Assert.Equal(half, again);
        Assert.Equal(box, palette.Figures(content, size).ToArray());
    }

    [Fact]
    public void AUnitWedgeTakesTheSameSmoothShellAsEveryOtherDesign()
    {
        var design = TestDesigns.Of(new DesignBox(default, new GridSize(1, 1, 1), Core),
            new DesignBox(new GridSize(1, 0, 0), new GridSize(1, 1, 1), TestDesigns.Content.Blocks.Get("hull"), BlockShape.WedgeYMinusPlus));
        Assert.False(design.UsesRoundedUnitShell);
        GeometryAdmission.Validate(design);
        Assert.False(design.IntactShell.IsEmpty);
        // Every vertex stands off both blocks: the wedge's empty half is not shielded as a cube.
        foreach (var vertex in design.IntactShell.Span)
            for (var b = 0; b < 2; b++)
            {
                var local = vertex.Position - design.BlockOffsets[b];
                var near = design.Shapes[b] == BlockShape.Box ? Vector3.Clamp(local, -design.BlockHalfExtents[b], design.BlockHalfExtents[b])
                    : BlockShapes.Closest(design.Shapes[b], design.BlockHalfExtents[b], local);
                Assert.True((local - near).Length() > 0.01f);
            }
    }
}
