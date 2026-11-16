using Prospect.Game.World;
using Xunit;

namespace Prospect.Tests;

public sealed class HullConnectivityTests
{
    private static DesignBox Box(int x, int y = 0, int z = 0, string kind = "hull", int size = 1) =>
        new(new GridSize(x, y, z), new GridSize(size, 1, 1), TestDesigns.Content.Blocks.Get(kind));

    [Fact]
    public void OnlyPositiveAreaSharedFacesConnect()
    {
        var origin = Box(0);
        Assert.True(HullConnectivity.SharesFace(origin, Box(1)));
        Assert.True(HullConnectivity.SharesFace(origin, Box(0, 1)));
        Assert.True(HullConnectivity.SharesFace(origin, Box(0, 0, 1)));
        Assert.False(HullConnectivity.SharesFace(origin, Box(1, 1)));
        Assert.False(HullConnectivity.SharesFace(origin, Box(1, 1, 1)));
        Assert.False(HullConnectivity.SharesFace(origin, Box(2)));
        Assert.True(HullConnectivity.SharesFace(Box(-2, size: 3), Box(0, 1)));
        Assert.False(HullConnectivity.SharesFace(Box(-2, size: 2), Box(0, 1)));
    }

    [Fact]
    public void WedgesUseBoundingFacesRegardlessOfSlant()
    {
        foreach (var shape in Enum.GetValues<BlockShape>())
        {
            var wedge = Box(0) with { Shape = shape };
            Assert.True(HullConnectivity.SharesFace(wedge, Box(1)));
            Assert.True(HullConnectivity.SharesFace(wedge, Box(-1)));
            Assert.False(HullConnectivity.SharesFace(wedge, Box(1, 1)));
        }
    }

    [Fact]
    public void CuttingLongLineKeepsCorePieceEvenWhenLighter()
    {
        var boxes = new DesignBox[101];
        for (var i = 0; i < boxes.Length; i++) boxes[i] = Box(i, kind: i == 100 ? "core" : "hull");
        var design = TestDesigns.Of(boxes);
        using var states = new BlockStateStore();
        var state = states.Create(design, true);
        states.Lose(state, 75);
        var pieces = new HullConnectivity(design.BlockCount);
        pieces.Find(design, states.Alive(state));
        Assert.Equal(2, pieces.Count);
        Assert.Equal(1, pieces.Kept);
        Assert.Equal(-1, pieces.Pieces[75]);
        Assert.Equal(0, pieces.Pieces[74]);
        Assert.Equal(1, pieces.Pieces[76]);
    }

    [Fact]
    public void WithoutLiveCoreHeaviestPieceStaysAndTiesUseLowestBlock()
    {
        var design = TestDesigns.Of(Box(0, kind: "core"), Box(1), Box(2), Box(3), Box(4));
        using var states = new BlockStateStore();
        var state = states.Create(design, true);
        states.Lose(state, 0);
        states.Lose(state, 2);
        var pieces = new HullConnectivity(5);
        pieces.Find(design, states.Alive(state));
        Assert.Equal(2, pieces.Count);
        Assert.Equal(1, pieces.Kept);
        states.Lose(state, 4);
        pieces.Find(design, states.Alive(state));
        Assert.Equal(0, pieces.Kept);
        states.Lose(state, 1);
        states.Lose(state, 3);
        pieces.Find(design, states.Alive(state));
        Assert.Equal(0, pieces.Count);
        Assert.Equal(-1, pieces.Kept);
    }

    [Fact]
    public void LowestLiveCoreWinsAcrossDisconnectedCorePieces()
    {
        var design = TestDesigns.Of(Box(0, kind: "core"), Box(1), Box(2, kind: "core"));
        using var states = new BlockStateStore();
        var state = states.Create(design, true);
        states.Lose(state, 1);
        var pieces = new HullConnectivity(3);
        pieces.Find(design, states.Alive(state));
        Assert.Equal(0, pieces.Kept);
        states.Lose(state, 0);
        pieces.Find(design, states.Alive(state));
        Assert.Equal(0, pieces.Kept);
        Assert.Equal(0, pieces.Pieces[2]);
    }

    [Fact]
    public void RepeatedConnectivityChecksTakeNoManagedMemory()
    {
        var design = TestDesigns.Cubes(200);
        using var states = new BlockStateStore();
        var state = states.Create(design, true);
        for (var i = 25; i < 200; i += 50) states.Lose(state, i);
        var pieces = new HullConnectivity(200);
        pieces.Find(design, states.Alive(state));
        var probe = new AllocationProbe();
        for (var i = 0; i < 100; i++)
            while (probe.Measuring()) pieces.Find(design, states.Alive(state));
        Assert.Equal(0, probe.Bytes);
        Assert.Equal(2, pieces.Count);
    }
}
