using System.Numerics;
using Prospect.Client.Render;
using Prospect.Engine.IO;
using Prospect.Game.World;
using Xunit;

namespace Prospect.Tests;

/// <summary>The cargo rack's loading deck: which face carries it, its data limits and its geometry.</summary>
public sealed class RackDeckTests
{
    private static readonly GameContent Content = GameContent.Load();
    private static readonly BlockType Cargo = Content.Blocks.Get("cargo");

    [Fact]
    public void TheDeckIsOnTheOneTouchedFaceElseDownwards()
    {
        Assert.Equal(BlockFacing.Down, RackDeck.Choose(0));
        Assert.Equal(BlockFacing.Right, RackDeck.Choose(1 << 0));
        Assert.Equal(BlockFacing.Front, RackDeck.Choose(1 << 5));
        Assert.Equal(BlockFacing.Down, RackDeck.Choose(1 << 3));
        Assert.Equal(BlockFacing.Down, RackDeck.Choose((1 << 0) | (1 << 3)));
        Assert.Equal(BlockFacing.Left, RackDeck.Choose((1 << 1) | (1 << 4)));
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
        Assert.Equal((1 << 3) | (1 << 0), RackDeck.Touched(boxes, 0, rack.At, rack.Size));
        Assert.Equal(BlockFacing.Down, RackDeck.Choose(RackDeck.Touched(boxes, 0, rack.At, rack.Size)));
    }

    [Fact]
    public void ADeckThickerThanTheContainerClearanceIsRefused()
    {
        var folder = Directory.CreateTempSubdirectory("rackdeck");
        var path = Path.Combine(folder.FullName, "propulsion.json");
        var text = File.ReadAllText(AppPaths.Content("visuals", "propulsion.json"));
        Assert.Contains("\"deck\": 0.06", text);
        File.WriteAllText(path, text.Replace("\"deck\": 0.06", "\"deck\": 0.2"));
        Assert.Throws<InvalidDataException>(() => PropulsionLook.Load(path));
        File.WriteAllText(path, text.Replace("\"lightEvery\": 1", "\"lightEvery\": 0"));
        Assert.Throws<InvalidDataException>(() => PropulsionLook.Load(path));
        folder.Delete(true);
    }

    [Theory]
    [InlineData(1, 1, 1)]
    [InlineData(1, 1, 4)]
    [InlineData(3, 1, 2)]
    [InlineData(2, 2, 2)]
    public void EveryDeckFaceStaysInsideTheBlockAndBoundsItsDetail(int x, int y, int z)
    {
        var look = PropulsionLook.Load();
        var half = new Vector3(x, y, z) * .5f;
        for (var face = 0; face < 6; face++)
        {
            var vertices = new List<MeshVertex>();
            var indices = new List<uint>();
            PropulsionModels.BuildRack(new PartBox(Vector3.Zero, half, (BlockFacing)face, 1), look.Rack!, look.Cargo, vertices, indices);
            Assert.InRange(vertices.Count, 100, 6000);
            foreach (var v in vertices)
            {
                Assert.InRange(v.Position.X, -half.X - .001f, half.X + .001f);
                Assert.InRange(v.Position.Y, -half.Y - .001f, half.Y + .001f);
                Assert.InRange(v.Position.Z, -half.Z - .001f, half.Z + .001f);
            }
        }
    }

    [Fact]
    public void ClampJawsStayInsideTheirSlotOnEveryFace()
    {
        var look = PropulsionLook.Load();
        for (var size = 0; size < 8; size++)
        {
            var shape = look.Cargo.Sizes[size].Shape;
            var half = new Vector3(shape.X, shape.Y, shape.Z) * (look.Cargo.UnitSize * .5f);
            for (var face = 0; face < 6; face++)
            {
                var vertices = new List<MeshVertex>();
                var indices = new List<uint>();
                PropulsionModels.ClampModel(shape, (BlockFacing)face, look.Rack!, look.Cargo, vertices, indices);
                Assert.NotEmpty(indices);
                foreach (var v in vertices)
                {
                    Assert.InRange(v.Position.X, -half.X - .001f, half.X + .001f);
                    Assert.InRange(v.Position.Y, -half.Y - .001f, half.Y + .001f);
                    Assert.InRange(v.Position.Z, -half.Z - .001f, half.Z + .001f);
                }
            }
        }
    }
}
