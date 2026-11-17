using System.Numerics;
using Prospect.Client.Render;
using Prospect.Engine.IO;
using Prospect.Game.World;
using Xunit;

namespace Prospect.Tests;

/// <summary>
/// The drone bay's worker-built model: a body on five faces and, on the one hatch face (the block's facing), a rim,
/// two door leaves with a seam and a light. A pure function of the box, its facing and the look.
/// </summary>
public sealed class BayModelTests
{
    private static readonly GameContent Content = GameContent.Load();
    private static readonly PropulsionLook Look = PropulsionLook.Load();
    private static readonly GridSize One = new(1, 1, 1);

    public static TheoryData<BlockFacing> AllFacings => new(Enum.GetValues<BlockFacing>());

    private static Vector3 P(MeshVertex v) => new(v.Position.X, v.Position.Y, v.Position.Z);

    private static float Length(MeshVertex v) => new Vector3(v.Normal.X, v.Normal.Y, v.Normal.Z).Length();

    private static (List<MeshVertex> Vertices, List<uint> Indices) Part(BlockFacing facing, Vector3 half)
    {
        var vertices = new List<MeshVertex>();
        var indices = new List<uint>();
        PropulsionModels.BuildPart(Content.Blocks.Get("droneBay"), new PartBox(Vector3.Zero, half, facing, 1f), Look, vertices, indices);
        return (vertices, indices);
    }

    // How far a vertex lies toward the hatch from the box's centre.
    private static float Toward(MeshVertex v, BlockFacing facing) => Vector3.Dot(P(v), Facings.Vector(facing));

    [Fact]
    public void ABayHasAModelOnlyAsABox()
    {
        var bay = Content.Blocks.Get("droneBay");
        Assert.NotNull(Look.Bay);
        Assert.True(PropulsionModels.HasModel(bay, BlockShape.Box, Look));
        Assert.False(PropulsionModels.HasModel(bay, BlockShape.WedgeXPlusPlus, Look));
        Assert.False(PropulsionModels.IsBay(Content.Blocks.Get("core"), BlockShape.Box, Look));
    }

    [Theory]
    [MemberData(nameof(AllFacings))]
    public void TheModelBuildsForEveryFacingAndStaysInsideTheBox(BlockFacing facing)
    {
        var half = new Vector3(0.8f, 0.8f, 0.8f);
        var (vertices, indices) = Part(facing, half);
        Assert.InRange(vertices.Count, 200, 2000);
        Assert.Equal(0, indices.Count % 3);
        foreach (var index in indices)
            Assert.True(index < vertices.Count);
        foreach (var v in vertices)
        {
            Assert.InRange(v.Position.X, -half.X - .0005f, half.X + .0005f);
            Assert.InRange(v.Position.Y, -half.Y - .0005f, half.Y + .0005f);
            Assert.InRange(v.Position.Z, -half.Z - .0005f, half.Z + .0005f);
        }
    }

    [Theory]
    [MemberData(nameof(AllFacings))]
    public void OnlyTheHatchFaceHasARimDoorsAndALight(BlockFacing facing)
    {
        var half = new Vector3(0.8f, 0.8f, 0.8f);
        var (vertices, _) = Part(facing, half);
        var depth = half.X;
        var slab = 0.1f * 0.8f;
        var hatch = vertices.Count(v => Toward(v, facing) >= depth - slab);
        var back = vertices.Count(v => Toward(v, facing) <= -depth + slab);
        Assert.True(hatch > back + 80, $"{hatch} vertices at the hatch against {back} at the back");

        // The light is the only thing that gives off light, and it sits on the hatch side, on the cell's face at most.
        var lit = vertices.Where(v => Length(v) >= 4.5f).ToArray();
        Assert.NotEmpty(lit);
        Assert.All(lit, v => Assert.True(Toward(v, facing) >= depth - slab));

        // The doors are the only orange paint: two leaves, each a closed brick, lying under the rim on the hatch side.
        var doors = vertices.Where(v => PropulsionModels.PaintOf(Length(v), out var rgb) && rgb.X > 0.8f && rgb.Y is > 0.4f and < 0.7f).ToArray();
        Assert.Equal(52, doors.Length);
        Assert.All(doors, v => Assert.InRange(Toward(v, facing), depth - 0.14f * 0.8f - .0005f, depth - 0.02f * 0.8f));

        // The plating is behind the hatch's recess, never at the hatch.
        var plating = vertices.Where(v => PropulsionModels.PaintOf(Length(v), out var rgb) && rgb.X < 0.7f && rgb.Y > 0.4f).ToArray();
        Assert.NotEmpty(plating);
        Assert.All(plating, v => Assert.True(Toward(v, facing) <= depth - 0.14f * 0.8f + .0005f));
    }

    [Fact]
    public void TheDoorsSplitInTwoWithASeamBetweenThem()
    {
        var half = new Vector3(0.8f, 0.8f, 0.8f);
        var (vertices, _) = Part(BlockFacing.Up, half);
        var doors = vertices.Where(v => PropulsionModels.PaintOf(Length(v), out var rgb) && rgb.X > 0.8f && rgb.Y is > 0.4f and < 0.7f).ToArray();
        // Facing +Y: the first cross axis is Z, which ties with X and so splits the leaves.
        var seam = Look.Bay!.Seam * 0.8f;
        var negative = doors.Where(v => v.Position.Z < 0).Select(v => v.Position.Z).Max();
        var positive = doors.Where(v => v.Position.Z > 0).Select(v => v.Position.Z).Min();
        Assert.Equal(seam, positive - negative, 3);
        Assert.Equal(26, doors.Count(v => v.Position.Z < 0));
    }

    [Theory]
    [MemberData(nameof(AllFacings))]
    public void AHullMeshTakesTheBlocksOwnFacingAsTheHatch(BlockFacing facing)
    {
        var design = TestDesigns.Of([new DesignBox(new GridSize(0, 0, 0), One, Content.Blocks.Get("core")),
            new DesignBox(new GridSize(1, 0, 0), One, Content.Blocks.Get("droneBay"), BlockShape.Box, facing)]);
        var vertices = new List<MeshVertex>();
        var indices = new List<uint>();
        Assert.True(PropulsionModels.Add(design, 1, Look, vertices, indices, null));
        var centre = design.BlockOffsets[1];
        var half = design.BlockHalfExtents[1];
        foreach (var v in vertices)
        {
            var p = P(v) - centre;
            Assert.InRange(p.X, -half.X - .0005f, half.X + .0005f);
            Assert.InRange(p.Y, -half.Y - .0005f, half.Y + .0005f);
            Assert.InRange(p.Z, -half.Z - .0005f, half.Z + .0005f);
        }

        // The same model the editor's single part gets.
        var (alone, _) = Part(facing, half);
        Assert.Equal(alone.Count, vertices.Count);
        Assert.Contains(vertices, v => Length(v) >= 4.5f && Vector3.Dot(P(v) - centre, Facings.Vector(facing)) > 0);
    }

    [Fact]
    public void AFigureOutOfRangeIsRefused()
    {
        var folder = Directory.CreateTempSubdirectory("bay-look");
        try
        {
            var path = Path.Combine(folder.FullName, "propulsion.json");
            var text = File.ReadAllText(AppPaths.Content("visuals", "propulsion.json"));
            foreach (var (was, now) in new[]
            {
                ("\"lightLift\": 0.02", "\"lightLift\": 0.2"),
                ("\"rimWidth\": 0.16", "\"rimWidth\": 0.6"),
                ("\"inset\": 0.05", "\"inset\": 0"),
                ("\"seam\": 0.05", "\"seam\": 0.9"),
                ("\"door\": [0.88, 0.58, 0.12]", "\"door\": [1.5, 0.58, 0.12]"),
                ("\"light\": [0.3, 1.0, 0.45]", "\"light\": [0.3, 1.0]"),
            })
            {
                Assert.Contains(was, text);
                File.WriteAllText(path, text.Replace(was, now));
                Assert.Throws<InvalidDataException>(() => PropulsionLook.Load(path));
            }

            File.WriteAllText(path, text);
            Assert.NotNull(PropulsionLook.Load(path).Bay);
        }
        finally { folder.Delete(true); }
    }
}
