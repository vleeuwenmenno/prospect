using System.Numerics;
using Prospect.Client.Render;
using Prospect.Engine.IO;
using Prospect.Game.World;
using Xunit;

namespace Prospect.Tests;

/// <summary>A cargo rack's one open face: which face it is, how old files take it, its data limits and its geometry.</summary>
public sealed class RackModelTests
{
    private static readonly GameContent Content = GameContent.Load();
    private static readonly BlockType Cargo = Content.Blocks.Get("cargo");

    [Fact]
    public void TheOpeningLooksAwayFromWhatTheRackIsSetAgainst()
    {
        Assert.Equal(BlockFacing.Up, OpenFaces.Choose(0));
        Assert.Equal(BlockFacing.Left, OpenFaces.Choose(1 << 0));
        Assert.Equal(BlockFacing.Back, OpenFaces.Choose(1 << 5));
        Assert.Equal(BlockFacing.Up, OpenFaces.Choose(1 << 3));
        Assert.Equal(BlockFacing.Up, OpenFaces.Choose((1 << 0) | (1 << 3)));
        Assert.Equal(BlockFacing.Right, OpenFaces.Choose((1 << 1) | (1 << 4)));
        // Racks, bays and hoppers all take their open face by this one rule.
        Assert.True(Cargo.FacesOut);
        Assert.True(Content.Blocks.Get("droneBay").FacesOut);
        Assert.True(Content.Blocks.Get("oreHopper").FacesOut);
        Assert.False(Content.Blocks.Get("hull").FacesOut);
    }

    [Fact]
    public void TouchedFacesFollowTheDesignsOwnTest()
    {
        var rack = new DesignBox(new(1, 1, 0), new(3, 1, 2), Cargo);
        var boxes = new[]
        {
            rack,
            new DesignBox(new(2, 0, 0), new(1, 1, 1), Content.Blocks.Get("hull")),
            new DesignBox(new(4, 1, 1), new(1, 1, 1), Content.Blocks.Get("hull")),
            new DesignBox(new(9, 9, 9), new(1, 1, 1), Content.Blocks.Get("hull")),
        };
        // Below it over part of its area (-Y, face 3) and at its +X end (face 0).
        Assert.Equal((1 << 3) | (1 << 0), OpenFaces.Touched(boxes, 0, rack.At, rack.Size));
        Assert.Equal(BlockFacing.Up, OpenFaces.Choose(OpenFaces.Touched(boxes, 0, rack.At, rack.Size)));
    }

    [Fact]
    public void AnOldDesignFilesRacksOpenByTheSameRuleAndANewFileKeepsItsFace()
    {
        static string File(int version, string rackFacing) =>
            "{\"version\": " + version + ", \"name\": \"Old\", \"class\": \"ship\", \"blockSize\": 1, \"color\": [0.5, 0.5, 0.5], \"blocks\": ["
            + "{\"at\": [0, 0, 0], \"type\": \"core\"},"
            + "{\"at\": [1, 0, 0], \"type\": \"cargo\"" + rackFacing + "},"
            + "{\"at\": [0, 2, 0], \"type\": \"cargo\", \"size\": [1, 1, 2]}]}";
        // Version 9: a rack set against the core's +X side opens away from it; one standing alone opens upwards. A
        // facing such a file wrote for a rack never meant anything, so it is not kept.
        foreach (var named in new[] { "", ", \"facing\": \"back\"" })
        {
            var old = BoxDesign.Read(System.Text.Encoding.UTF8.GetBytes(File(9, named)), Content.Blocks, "old");
            Assert.Equal(BlockFacing.Right, old.Boxes[1].Facing);
            Assert.Equal(BlockFacing.Up, old.Boxes[2].Facing);
        }
        // A rack whose rule face is covered by a mount takes the first face that is free.
        var mounted = File(9, "")[..^2] + "], \"mounts\": [{\"on\": 1, \"face\": \"right\", \"at\": [2, 2], \"type\": \"coldGasThruster\"}]}";
        var moved = BoxDesign.Read(System.Text.Encoding.UTF8.GetBytes(mounted), Content.Blocks, "mounted");
        Assert.Equal(BlockFacing.Up, moved.Boxes[1].Facing);
        var current = BoxDesign.Read(System.Text.Encoding.UTF8.GetBytes(File(10, ", \"facing\": \"front\"")), Content.Blocks, "new");
        Assert.Equal(BlockFacing.Front, current.Boxes[1].Facing);
        Assert.Equal(10, BoxDesign.FormatVersion);
    }

    [Fact]
    public void AWallThickerThanTheRacksShareOfTheClearanceIsRefused()
    {
        var folder = Directory.CreateTempSubdirectory("rackmodel");
        try
        {
            var path = Path.Combine(folder.FullName, "propulsion.json");
            var text = System.IO.File.ReadAllText(AppPaths.Content("visuals", "propulsion.json"));
            Assert.Contains("\"wall\": 0.024", text);
            System.IO.File.WriteAllText(path, text.Replace("\"wall\": 0.024", "\"wall\": 0.05"));
            Assert.Throws<InvalidDataException>(() => PropulsionLook.Load(path));
            System.IO.File.WriteAllText(path, text.Replace("\"railHeight\": 0.014", "\"railHeight\": 0.03"));
            Assert.Throws<InvalidDataException>(() => PropulsionLook.Load(path));
            System.IO.File.WriteAllText(path, text.Replace("\"panelEvery\": 2", "\"panelEvery\": 0"));
            Assert.Throws<InvalidDataException>(() => PropulsionLook.Load(path));
            System.IO.File.WriteAllText(path, text.Replace("\"lockSeconds\": 0.7", "\"lockSeconds\": 0"));
            Assert.Throws<InvalidDataException>(() => PropulsionLook.Load(path));
        }
        finally { folder.Delete(true); }
    }

    [Theory]
    [InlineData(1, 1, 1)]
    [InlineData(1, 1, 4)]
    [InlineData(3, 1, 2)]
    [InlineData(2, 2, 2)]
    public void EveryRackStaysInsideItsBlockAndWithinItsShareOfTheClearance(int x, int y, int z)
    {
        var look = PropulsionLook.Load();
        var half = new Vector3(x, y, z) * .5f;
        var budget = look.Rack!.RackBudget * look.Cargo.UnitSize;
        for (var face = 0; face < 6; face++)
        {
            foreach (var closed in new[] { 0, 1 << face })
            {
                var vertices = new List<MeshVertex>();
                var indices = new List<uint>();
                PropulsionModels.BuildRack(new PartBox(Vector3.Zero, half, (BlockFacing)face, 1), look.Rack, look.Cargo, vertices, indices, closed);
                Assert.InRange(vertices.Count, 100, 20000);
                foreach (var v in vertices)
                {
                    var p = new Vector3(v.Position.X, v.Position.Y, v.Position.Z);
                    Assert.InRange(p.X, -half.X - .0001f, half.X + .0001f);
                    Assert.InRange(p.Y, -half.Y - .0001f, half.Y + .0001f);
                    Assert.InRange(p.Z, -half.Z - .0001f, half.Z + .0001f);
                    // Every part is a wall, a rail or a light within the rack's share of a container's clearance.
                    var inner = MathF.Min(half.X - MathF.Abs(p.X), MathF.Min(half.Y - MathF.Abs(p.Y), half.Z - MathF.Abs(p.Z)));
                    Assert.True(inner <= budget + .0001f, $"a rack part reaches {inner} m into the slots");
                }
            }
        }
    }

    [Fact]
    public void ClampJawsAndTheirLightsStayInTheContainersShareOfItsClearance()
    {
        var look = PropulsionLook.Load();
        for (var size = 0; size < 8; size++)
        {
            var shape = look.Cargo.Sizes[size].Shape;
            var half = new Vector3(shape.X, shape.Y, shape.Z) * (look.Cargo.UnitSize * .5f);
            var inset = MathF.Min(half.X, MathF.Min(half.Y, half.Z)) * look.Rack!.Gap;
            var reach = half - new Vector3(inset * (1 - RackLook.DetailShare));
            for (var face = 0; face < 6; face++)
            {
                var vertices = new List<MeshVertex>();
                var indices = new List<uint>();
                var lights = new List<MeshVertex>();
                var lightIndices = new List<uint>();
                PropulsionModels.ClampModel(shape, (BlockFacing)face, look.Rack, look.Cargo, vertices, indices, lights, lightIndices);
                Assert.NotEmpty(indices);
                Assert.NotEmpty(lightIndices);
                foreach (var v in vertices.Concat(lights))
                {
                    Assert.InRange(v.Position.X, -reach.X - .0001f, reach.X + .0001f);
                    Assert.InRange(v.Position.Y, -reach.Y - .0001f, reach.Y + .0001f);
                    Assert.InRange(v.Position.Z, -reach.Z - .0001f, reach.Z + .0001f);
                }
            }
        }
    }

    /// <summary>
    /// No two faces of a rack lie in one plane facing the same way unless they are painted alike. Two surfaces in one
    /// plane fight for the pixel, and an empty rack's walls flickered in stripes because a wall panel and the backing
    /// behind it, painted differently, lay in one plane. Faces painted alike come out the same colour whichever wins,
    /// so the corner where two walls' rim bands or lit rims meet cannot show.
    /// </summary>
    [Fact]
    public void NoTwoFacesOfARackShareAPlaneFacingTheSameWayUnlessTheyArePaintedAlike()
    {
        var look = PropulsionLook.Load();
        foreach (var half in new[] { new Vector3(.5f), new Vector3(.5f, .5f, 2), new Vector3(1.5f, .5f, 1), new Vector3(1, 1, 1) })
            for (var face = 0; face < 6; face++)
                foreach (var closed in new[] { 0, 0b111111 & ~(1 << face) })
                {
                    var vertices = new List<MeshVertex>();
                    var indices = new List<uint>();
                    PropulsionModels.BuildRack(new PartBox(Vector3.Zero, half, (BlockFacing)face, 1), look.Rack!, look.Cargo, vertices, indices, closed);
                    var fight = ModelFaces.Fight(vertices, indices);
                    Assert.True(fight is null, $"a rack {half} open {face}, closed {closed}: {fight}");
                }
    }

    /// <summary>
    /// A rack block is drawn as its model, never as its own box, and the model keeps inside the block: no triangle of
    /// the hull mesh lies on one of the block's faces, where a plain box face of a neighbour would meet it in one
    /// plane.
    /// </summary>
    [Fact]
    public void ARackBlockIsDrawnAsItsModelAndNeverAsItsOwnBox()
    {
        var design = HullDesign.Of(new BoxDesign("rack", 1,
            [new DesignBox(new(0, 0, 0), new(1, 1, 1), Content.Blocks.Get("core")), new DesignBox(new(5, 0, 0), new(2, 1, 1), Cargo)],
            Content.Blocks.Limits, "rack"), Content.Rules);
        var rack = Array.FindIndex(Enumerable.Range(0, design.BlockCount).ToArray(), b => design.Types[b].Cargo > 0);
        Assert.True(rack >= 0, "the fixture has no rack");
        var centre = design.BlockOffsets[rack];
        var half = design.BlockHalfExtents[rack];
        var mesh = MeshBuilder.Hull(design, PropulsionLook.Load());
        var drawn = 0;
        for (var i = 0; i + 2 < mesh.Indices.Count; i += 3)
        {
            var corners = new[] { ModelFaces.Point(mesh.Vertices[(int)mesh.Indices[i]]), ModelFaces.Point(mesh.Vertices[(int)mesh.Indices[i + 1]]), ModelFaces.Point(mesh.Vertices[(int)mesh.Indices[i + 2]]) };
            // Only the faces inside the rack's own box: the design carries a core block 5 cells away.
            if (corners.Any(p => MathF.Abs(p.X - centre.X) > half.X || MathF.Abs(p.Y - centre.Y) > half.Y || MathF.Abs(p.Z - centre.Z) > half.Z)) continue;
            drawn++;
            for (var axis = 0; axis < 3; axis++)
                foreach (var side in new[] { -1f, 1f })
                {
                    var plane = centre[axis] + side * half[axis];
                    if (corners.All(p => MathF.Abs(p[axis] - plane) <= 1e-4f))
                        Assert.Fail($"a face of the rack model lies in the block's own face {(BlockFacing)(2 * axis + (side > 0 ? 0 : 1))} at {plane}: {corners[0]} {corners[1]} {corners[2]}");
                }
        }
        Assert.True(drawn > 100, $"the rack's model is not in the hull mesh ({drawn} triangles near the block)");
    }
}
