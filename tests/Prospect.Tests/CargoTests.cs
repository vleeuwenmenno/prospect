using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using Prospect.Client.Render;
using Prospect.Engine.Jobs;
using Prospect.Game.World;
using Xunit;

namespace Prospect.Tests;

public sealed class CargoTests
{
    private static readonly GameContent Content = GameContent.Load();
    private static readonly CargoCatalog Cargo = Content.Cargo;

    [Fact]
    public void ExactFitsCapacityAndFixedOrientation()
    {
        Assert.Equal(8, CargoPacking.Capacity(Cargo, new(1, 1, 1), 1));
        Assert.Equal(64, CargoPacking.Capacity(Cargo, new(2, 2, 2), 1));
        Assert.Equal(0, CargoPacking.Capacity(Cargo, new(1, 1, 1), .25f));
        Assert.Equal(1, CargoPacking.Capacity(Cargo, new(2, 2, 2), .25f));
        Assert.Equal(6, CargoPacking.Largest(Cargo, new(2, 2, 2), 1));
        Assert.Equal(7, CargoPacking.Largest(Cargo, new(4, 2, 2), 1));
        Assert.Equal(6, CargoPacking.Largest(Cargo, new(2, 4, 2), 1));
        Assert.Equal(-1, CargoPacking.Largest(Cargo, new(1, 1, 1), .25f));
        Span<byte> occupied = stackalloc byte[64];
        Span<CargoPlacement> slots = stackalloc CargoPlacement[8];
        Assert.True(CargoPacking.TryPack(Cargo, new(2, 2, 2), 1, [6], occupied, slots));
        Assert.Equal(new CargoPlacement(6, default), slots[0]);
        Assert.False(CargoPacking.TryPack(Cargo, new(2, 2, 2), 1, [6, 0], occupied, slots));
        Assert.False(CargoPacking.TryPack(Cargo, new(2, 2, 2), 1, [7], occupied, slots));
        Assert.False(CargoPacking.TryPack(Cargo, new(1, 1, 1), 1, [-1], occupied, slots));
        Assert.True(CargoPacking.TryPack(Cargo, new(2, 2, 2), 1, [3, 3, 3, 3, 3, 3, 3, 3], occupied, slots));
        Assert.Equal(8, Content.Blocks.Get("cargo").Cargo);
        Assert.Equal(64, BoxDesign.Scale(Content.Blocks.Get("cargo"), "cargo", 8, 8));
    }

    [Fact]
    public void CanonicalLargestFirstNonOverlappingAndAllocationFree()
    {
        var occupied = new byte[64];
        var a = new CargoPlacement[5];
        var b = new CargoPlacement[5];
        int[] forward = [0, 1, 2, 3, 4], reverse = [4, 3, 2, 1, 0];
        Assert.True(CargoPacking.TryPack(Cargo, new(2, 2, 2), 1, forward, occupied, a));
        Assert.Equal(31, occupied.Sum(x => x));
        Assert.True(CargoPacking.TryPack(Cargo, new(2, 2, 2), 1, reverse, occupied, b));
        Assert.Equal(a, b);
        Assert.Equal(4, a[0].Size);
        Assert.Equal(default, a[0].At);
        var allocation = new AllocationProbe();
        while (allocation.Measuring())
            CargoPacking.TryPack(Cargo, new(2, 2, 2), 1, forward, occupied, a);
        Assert.Equal(0, allocation.Bytes);
    }

    [Theory]
    [InlineData("units", "3")]
    [InlineData("shape", "[1,1,1]")]
    [InlineData("hull", "-1")]
    [InlineData("name", "\"XXS\"")]
    public void InvalidSizesAreRefused(string field, string value)
    {
        using var copy = new ContentCopy();
        var json = JsonNode.Parse(copy.Read("blocks/cargo.json"))!;
        json["sizes"]![1]![field] = JsonNode.Parse(value);
        Assert.Throws<InvalidDataException>(() => CargoCatalog.Parse(Encoding.UTF8.GetBytes(json.ToJsonString())));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(2)]
    public void InvalidUnitEdgesAreRefused(float unit)
    {
        using var copy = new ContentCopy();
        var json = JsonNode.Parse(copy.Read("blocks/cargo.json"))!;
        json["unitSize"] = unit;
        Assert.Throws<InvalidDataException>(() => CargoCatalog.Parse(Encoding.UTF8.GetBytes(json.ToJsonString())));
    }

    [Fact]
    public void RetuningTheUnitAndCapacityTogetherKeepsTheGridAndFiguresConsistent()
    {
        using var copy = new ContentCopy();
        copy.Replace("blocks/cargo.json", "\"unitSize\": 0.5", "\"unitSize\": 0.25");
        copy.Replace("blocks/blocks.json", "\"cargo\": 8", "\"cargo\": 64");
        var tuned = copy.Load();
        Assert.NotEqual(Content.Identity, tuned.Identity);
        Assert.Equal(tuned.Blocks.Get("cargo").Cargo, CargoPacking.Capacity(tuned.Cargo, new(1, 1, 1), 1));
    }

    [Fact]
    public void RackAndContainerModelsAreBoundedAndWorkerLookupsTakeNoMemory()
    {
        var look = PropulsionLook.Load();
        var type = Content.Blocks.Get("cargo");
        var tuning = Prospect.Client.World.EditorTuning.Load(Path.Combine(AppContext.BaseDirectory, "content", "visuals", "editor.json"));
        var palette = new Prospect.Client.Screens.EditorPalette { Category = Array.FindIndex(tuning.Categories, c => c.Name == "Hull") };
        Assert.True(palette.Shows(tuning, type));
        Assert.True(type.BoxOnly);
        Assert.True(type.FitsClass(HullClass.Ship));
        Assert.False(type.FitsClass(HullClass.Fighter));
        Assert.True(PropulsionModels.HasModel(type, BlockShape.Box, look));
        var vertices = new List<MeshVertex>();
        var indices = new List<uint>();
        var box = new PartBox(Vector3.Zero, Vector3.One, BlockFacing.Back, 1);
        PropulsionModels.BuildPart(type, box, look, vertices, indices);
        Assert.InRange(vertices.Count, 100, 2000);
        var rackVertices = vertices.Count;
        PropulsionModels.RackPreview(box, look.Rack!, Cargo, vertices, indices);
        Assert.InRange(vertices.Count, rackVertices + 1, 10000);
        foreach (var v in vertices)
        {
            Assert.InRange(v.Position.X, -1.001f, 1.001f);
            Assert.InRange(v.Position.Y, -1.001f, 1.001f);
            Assert.InRange(v.Position.Z, -1.001f, 1.001f);
        }
        using var jobs = new JobSystem(1, "Cargo");
        var models = new PartModels(jobs, look);
        models.Begin();
        models.TryGet(type, BlockShape.Box, Vector3.One, BlockFacing.Back, 1, out _);
        models.Flush();
        jobs.CompleteAll();
        models.Begin();
        Assert.True(models.TryGet(type, BlockShape.Box, Vector3.One, BlockFacing.Back, 1, out _));
        for (var size = 0; size < 8; size++)
        {
            Assert.True(models.TryGetContainer(size, out var container));
            Assert.False(container.IsEmpty);
            var shape = Cargo.Sizes[size].Shape;
            foreach (var vertex in container)
            {
                Assert.InRange(Math.Abs(vertex.Position.X), 0, shape.X * Cargo.UnitSize * .5f);
                Assert.InRange(Math.Abs(vertex.Position.Y), 0, shape.Y * Cargo.UnitSize * .5f);
                Assert.InRange(Math.Abs(vertex.Position.Z), 0, shape.Z * Cargo.UnitSize * .5f);
            }
        }
        var allocation = new AllocationProbe();
        while (allocation.Measuring())
        {
            models.Begin();
            Assert.True(models.TryGetContainer(3, out _));
            models.TryGet(type, BlockShape.Box, Vector3.One, BlockFacing.Back, 1, out _);
            models.Flush();
        }
        Assert.Equal(0, allocation.Bytes);
    }
}
