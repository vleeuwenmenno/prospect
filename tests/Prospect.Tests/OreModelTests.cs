using System.Numerics;
using Prospect.Client.Render;
using Prospect.Client.Screens;
using Prospect.Client.World;
using Prospect.Game.World;
using Xunit;

namespace Prospect.Tests;

/// <summary>
/// The worker-built models of the ore hopper (a body on five faces and, on its one intake face, a rim round a pit
/// with grate bars and a light) and of the ore bin (a banded body), and how the editor and the Ship window speak of
/// the two parts.
/// </summary>
public sealed class OreModelTests
{
    private static readonly GameContent Content = GameContent.Load();
    private static readonly PropulsionLook Look = PropulsionLook.Load();

    public static TheoryData<BlockFacing> AllFacings => new(Enum.GetValues<BlockFacing>());

    private static Vector3 P(MeshVertex v) => new(v.Position.X, v.Position.Y, v.Position.Z);

    private static List<MeshVertex> Part(string type, BlockFacing facing, Vector3 half)
    {
        var vertices = new List<MeshVertex>();
        var indices = new List<uint>();
        PropulsionModels.BuildPart(Content.Blocks.Get(type), new PartBox(Vector3.Zero, half, facing, 1f), Look, vertices, indices);
        Assert.Equal(0, indices.Count % 3);
        foreach (var index in indices)
            Assert.True(index < vertices.Count);
        return vertices;
    }

    [Fact]
    public void AHopperAndABinHaveAModelOnlyAsABox()
    {
        Assert.NotNull(Look.Hopper);
        Assert.NotNull(Look.Bin);
        foreach (var id in new[] { "oreHopper", "oreBin" })
        {
            var type = Content.Blocks.Get(id);
            Assert.True(PropulsionModels.HasModel(type, BlockShape.Box, Look));
            Assert.False(PropulsionModels.HasModel(type, BlockShape.WedgeXPlusPlus, Look));
        }

        Assert.False(PropulsionModels.IsHopper(Content.Blocks.Get("oreBin"), BlockShape.Box, Look));
        Assert.False(PropulsionModels.IsBin(Content.Blocks.Get("oreHopper"), BlockShape.Box, Look));
        Assert.False(PropulsionModels.IsHopper(Content.Blocks.Get("droneBay"), BlockShape.Box, Look));
    }

    [Theory]
    [MemberData(nameof(AllFacings))]
    public void TheModelsBuildForEveryFacingAndStayInsideTheBox(BlockFacing facing)
    {
        foreach (var (id, half) in new[] { ("oreHopper", new Vector3(0.5f)), ("oreHopper", new Vector3(1f, 0.5f, 0.5f)), ("oreBin", new Vector3(0.5f)), ("oreBin", new Vector3(0.5f, 0.5f, 1f)) })
        {
            var vertices = Part(id, facing, half);
            Assert.InRange(vertices.Count, 100, 3000);
            foreach (var v in vertices)
            {
                Assert.InRange(v.Position.X, -half.X - .0005f, half.X + .0005f);
                Assert.InRange(v.Position.Y, -half.Y - .0005f, half.Y + .0005f);
                Assert.InRange(v.Position.Z, -half.Z - .0005f, half.Z + .0005f);
            }
        }
    }

    [Theory]
    [MemberData(nameof(AllFacings))]
    public void OnlyTheIntakeFaceOfAHopperHasARimBarsAndALight(BlockFacing facing)
    {
        var half = new Vector3(0.5f);
        var vertices = Part("oreHopper", facing, half);
        float Toward(MeshVertex v) => Vector3.Dot(P(v), Facings.Vector(facing));
        var intake = vertices.Count(v => Toward(v) >= half.X - 0.05f);
        var back = vertices.Count(v => Toward(v) <= -half.X + 0.05f);
        Assert.True(intake > back + 40, $"{intake} vertices at the intake against {back} at the back");

        // The light is the only thing that gives off light, and it sits on the intake side, on the cell's face at most.
        var lit = vertices.Where(v => new Vector3(v.Normal.X, v.Normal.Y, v.Normal.Z).Length() >= 4.5f).ToArray();
        Assert.NotEmpty(lit);
        Assert.All(lit, v => Assert.True(Toward(v) >= half.X - 0.05f));

        // The pit is a pit: its floor lies well under the rim.
        var floor = vertices.Where(v => MathF.Abs(Vector3.Dot(P(v), Facings.Vector(facing))) < half.X && Vector3.Dot(new Vector3(v.Normal.X, v.Normal.Y, v.Normal.Z), Facings.Vector(facing)) > 0.5f).Min(Toward);
        Assert.True(floor < half.X - 0.2f, $"the pit's floor is {half.X - floor:0.00} m under the face");
    }

    [Fact]
    public void TheEditorFilesBothUnderStorageAndSaysWhatEachIsFor()
    {
        var bin = Content.Blocks.Get("oreBin");
        var hopper = Content.Blocks.Get("oreHopper");
        Assert.Equal(PartRole.Bin, EditorGeometry.Role(bin));
        Assert.Equal(PartRole.Hopper, EditorGeometry.Role(hopper));
        var tuning = EditorTuning.Load();
        var storage = Array.FindIndex(tuning.Categories, c => c.Name == "Storage");
        Assert.Contains(PartRole.Bin, tuning.Categories[storage].Roles!);
        Assert.Contains(PartRole.Hopper, tuning.Categories[storage].Roles!);
        Assert.Contains("ore", EditorPalette.RoleWord(PartRole.Bin));
        Assert.Contains("ore", EditorPalette.RoleWord(PartRole.Hopper));

        // A bin's card figure is what it holds: the content's figure for each cubic metre.
        Assert.Equal(bin.OreCapacity * 6, PartFigures.Of(bin, 6, Content.Rules).Ore);
        Assert.Equal(0, PartFigures.Of(hopper, 1, Content.Rules).Ore);
        Assert.Equal(bin.OreCapacity * 2, PartFigures.Of(bin, 2, Content.Rules).Main(PartRole.Bin));
    }

    [Fact]
    public void TheShipWindowHasAnOreSectionForAHullWithBinsAndAddsThemUp()
    {
        var pod = Content.Pod;
        Assert.True(ShipSheet.Shows(ShipSheet.Section.Ore, pod.Stats, pod, isOwn: true));
        Assert.True(ShipSheet.Shows(ShipSheet.Section.Ore, pod.Stats, pod, isOwn: false));
        Assert.False(ShipSheet.Shows(ShipSheet.Section.Ore, Content.Designs[Content.DroneDesign].Stats, Content.Designs[Content.DroneDesign], isOwn: true));
        Assert.Equal(9, ShipSheet.Sections);
        Assert.Equal(9, Enum.GetValues<ShipSheet.Section>().Length);
        var capacity = 0.0;
        foreach (var bin in pod.Bins) capacity += bin.Capacity;
        Assert.Equal(capacity, ShipSheet.OreCapacity(pod));
        Assert.Equal(1500, ShipSheet.OreHeld([new OreBin { Kg = 1000, Resource = 1, Grade = 0.5f }, OreBin.Empty, new OreBin { Kg = 500, Resource = 2, Grade = 1f }]));
        Assert.Equal(0, ShipSheet.OreHeld([]));
    }
}
