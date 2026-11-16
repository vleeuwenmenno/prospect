using System.Numerics;
using Prospect.Client.Render;
using Prospect.Engine.IO;
using Prospect.Game.World;
using Xunit;

namespace Prospect.Tests;

/// <summary>The container's worker-built model: its plates, stroke glyphs and bounds.</summary>
public sealed class ContainerModelTests
{
    private static readonly PropulsionLook Look = PropulsionLook.Load();

    [Fact]
    public void EveryCharacterThePlatesWriteHasInBoundsStrokes()
    {
        var text = string.Concat(Look.Rack!.Galaxy.Select(r => r.Code)) + "EMPTY" + string.Concat(Look.Cargo.Sizes.ToArray().Select(s => s.Name));
        foreach (var c in text)
        {
            Assert.True(StrokeFont.Has(c), $"no glyph for {c}");
            foreach (var line in StrokeFont.Of(c).Split('|'))
                foreach (var point in line.Split(' '))
                {
                    Assert.InRange(point[0] - '0', 0, StrokeFont.Width);
                    Assert.InRange(point[1] - '0', 0, StrokeFont.Height);
                }
        }
        // S and 5 must stay distinguishable at a glance: the size class XXS is read on a plate.
        Assert.NotEqual(StrokeFont.Of('S'), StrokeFont.Of('5'));
    }

    [Fact]
    public void ResourceCodesComeFromContentAndAreUnique()
    {
        var codes = Look.Rack!.Galaxy.Select(r => r.Code).ToArray();
        Assert.Equal(["FE", "CU", "NI", "TI", "AG", "AU", "ICE"], codes);
        Assert.Equal(codes.Length, codes.Distinct().Count());
        foreach (var code in codes) Assert.InRange(code.Length, 1, 4);
    }

    [Fact]
    public void EveryContainerVariantStaysInsideItsSlotAndBoundsItsVertices()
    {
        var resources = Look.Rack!.Galaxy.Length;
        for (var size = 0; size < 8; size++)
        {
            var shape = Look.Cargo.Sizes[size].Shape;
            var half = new Vector3(shape.X, shape.Y, shape.Z) * (Look.Cargo.UnitSize * .5f);
            for (var resource = -1; resource < resources; resource++)
            {
                var vertices = new List<MeshVertex>();
                var indices = new List<uint>();
                PropulsionModels.ContainerModel(new PartBox(Vector3.Zero, half, BlockFacing.Front, 1), size, resource, Look.Rack, vertices, indices);
                Assert.InRange(vertices.Count, 500, 12000);
                foreach (var v in vertices)
                {
                    Assert.InRange(v.Position.X, -half.X - .0005f, half.X + .0005f);
                    Assert.InRange(v.Position.Y, -half.Y - .0005f, half.Y + .0005f);
                    Assert.InRange(v.Position.Z, -half.Z - .0005f, half.Z + .0005f);
                }
            }
        }
    }

    [Fact]
    public void ANewDetailThatDoesNotFitTheClearanceOrAnUnknownCodeIsRefused()
    {
        var folder = Directory.CreateTempSubdirectory("container-look");
        try
        {
            var path = Path.Combine(folder.FullName, "propulsion.json");
            var text = File.ReadAllText(AppPaths.Content("visuals", "propulsion.json"));
            Assert.Contains("\"corner\": 0.18", text);
            File.WriteAllText(path, text.Replace("\"corner\": 0.18", "\"corner\": 0.9"));
            Assert.Throws<InvalidDataException>(() => PropulsionLook.Load(path));
            File.WriteAllText(path, text.Replace("\"stroke\": 0.16", "\"stroke\": 0"));
            Assert.Throws<InvalidDataException>(() => PropulsionLook.Load(path));
        }
        finally { folder.Delete(true); }
    }
}
