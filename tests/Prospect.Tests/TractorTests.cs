using System.Numerics;
using System.Text.Json.Nodes;
using Prospect.Client.Render;
using Prospect.Engine.IO;
using Prospect.Game.World;
using Xunit;

namespace Prospect.Tests;

public sealed class TractorTests
{
    private static readonly GameContent Content = GameContent.Load();

    [Fact]
    public void TractorLoadsAsAnActiveOnlyMountWithAnEmitterModel()
    {
        var type = Content.Blocks.Get("tractorBeam");
        Assert.NotNull(type.Mount);
        Assert.NotNull(type.Tractor);
        Assert.Equal(0, type.Thrust);
        Assert.Equal(0, type.PowerDraw);
        Assert.True(type.Tractor.Power(2000) > type.Tractor.Power(1000));
        var design = TestDesigns.Mounted([new(new(0, 0, 0), new(1, 1, 1), Content.Blocks.Get("core"))],
            [new(0, BlockFacing.Up, 2, 2, type)]);
        var vertices = new List<MeshVertex>();
        var indices = new List<uint>();
        var nozzles = new List<ThrustNozzle>();
        Assert.True(PropulsionModels.AddMount(design, 0, PropulsionLook.Load(), vertices, indices, nozzles));
        Assert.NotEmpty(indices);
        Assert.Empty(nozzles);
        foreach (var vertex in vertices)
        {
            var p = new Vector3(vertex.Position.X, vertex.Position.Y, vertex.Position.Z) - design.MountOffsets[0];
            Assert.True(Vector3.Abs(p).X <= design.MountHalfExtents[0].X + .0001f);
            Assert.True(Vector3.Abs(p).Y <= design.MountHalfExtents[0].Y + .0001f);
            Assert.True(Vector3.Abs(p).Z <= design.MountHalfExtents[0].Z + .0001f);
        }
    }

    [Theory]
    [InlineData("range", 0)]
    [InlineData("range", 1001)]
    [InlineData("acceleration", -1)]
    [InlineData("maxMass", 0)]
    [InlineData("basePower", 0)]
    [InlineData("powerPerTonne", -1)]
    public void InvalidTractorTuningRefusesContent(string field, float value)
    {
        var json = JsonNode.Parse(File.ReadAllText(AppPaths.Content("blocks", "blocks.json")))!;
        json["blocks"]!["tractorBeam"]!["tractor"]![field] = value;
        Assert.Throws<InvalidDataException>(() => BlockCatalog.Parse(System.Text.Encoding.UTF8.GetBytes(json.ToJsonString()), "test"));
    }
}
