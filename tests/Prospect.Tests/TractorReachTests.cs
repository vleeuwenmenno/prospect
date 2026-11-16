using System.Numerics;
using Prospect.Game.World;
using Xunit;

namespace Prospect.Tests;

public sealed class TractorReachTests
{
    private static readonly GameContent Content = GameContent.Load();
    private static DesignBox Box(string type, int x, int y, int z) => new(new(x, y, z), new(1, 1, 1), Content.Blocks.Get(type));
    private static readonly DesignBox[] Boxes = [Box("core", 0, 0, 0), Box("cargo", 2, 1, 0)];
    private static readonly DesignMount[] Mounts = [new(0, BlockFacing.Up, 2, 2, Content.Blocks.Get("tractorBeam"))];

    [Fact]
    public void NearestRackFaceAndContainerNeedClearLiveSegmentsAndRange()
    {
        Assert.True(TractorReach.Rack(Boxes, Mounts, [], 1, 0, 1, out var point, out var side));
        Assert.Equal(new Vector3(2, 1.5f, .5f), point);
        Assert.Equal(BlockFacing.Left, side);
        Assert.True(TractorReach.Container(Boxes, [], 1, Mounts[0], new(.5f, 5, .5f)));
        Assert.False(TractorReach.Container(Boxes, [], 1, Mounts[0], new(.5f, 42, .5f)));
        Assert.False(TractorReach.Container(Boxes, [], 1, Mounts[0], new(.5f, -5, .5f)));
        DesignBox[] blockedRack = [.. Boxes, Box("hull", 1, 1, 0)];
        Assert.False(TractorReach.Rack(blockedRack, Mounts, [], 1, 0, 1, out _, out _));
        ulong[] alive = [3];
        Assert.True(TractorReach.Rack(blockedRack, Mounts, alive, 1, 0, 1, out _, out _));
        DesignBox[] blockedContainer = [.. Boxes, Box("hull", 0, 3, 0)];
        Assert.False(TractorReach.Container(blockedContainer, [], 1, Mounts[0], new(.5f, 5, .5f)));
        Assert.True(TractorReach.Container(blockedContainer, alive, 1, Mounts[0], new(.5f, 5, .5f)));
        alive[0] = 2;
        Assert.False(TractorReach.Rack(Boxes, Mounts, alive, 1, 0, 1, out _, out _));
    }

    [Fact]
    public void FitUsesPackingAndReservationsWithoutChangingTheHold()
    {
        var occupied = new byte[8];
        Assert.Equal(1, TractorReach.ChooseRack(Content.Cargo, Boxes, Mounts, [], 1, 0, [], [], 3, occupied));
        HoldContainer[] full = [new(1, 3, -1, 0, 1, default)];
        Assert.Equal(-1, TractorReach.ChooseRack(Content.Cargo, Boxes, Mounts, [], 1, 0, full, [], 0, occupied));
        Assert.Equal(-1, TractorReach.ChooseRack(Content.Cargo, Boxes, Mounts, [], 1, 0, [], full, 0, occupied));
        Assert.Equal(-1, TractorReach.ChooseRack(Content.Cargo, Boxes, Mounts, [], 1, 0, [], [], 4, occupied));
        var probe = new AllocationProbe();
        for (var i = 0; i < 100; i++)
            while (probe.Measuring()) TractorReach.ChooseRack(Content.Cargo, Boxes, Mounts, [], 1, 0, [], [], 3, occupied);
        Assert.Equal(0, probe.Bytes);
    }

    [Fact]
    public void MountedRackFaceIsNotOpen()
    {
        DesignMount[] mounts = [.. Mounts, new(1, BlockFacing.Left, 2, 2, Content.Blocks.Get("tractorBeam"))];
        Assert.False(TractorReach.Rack(Boxes, mounts, [], 1, 0, 1, out _, out _));
    }
}
