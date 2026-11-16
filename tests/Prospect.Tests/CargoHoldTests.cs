using Prospect.Game.World;
using Xunit;

namespace Prospect.Tests;

public sealed class CargoHoldTests
{
    private static readonly GameContent Content = GameContent.Load();
    private static HullDesign Design() => HullDesign.Of(new BoxDesign("Hold", 1,
        [new(new(0, 0, 0), new(1, 1, 1), Content.Blocks.Get("core")),
         new(new(1, 0, 0), new(2, 2, 2), Content.Blocks.Get("cargo")),
         new(new(-1, 0, 0), new(1, 1, 1), Content.Blocks.Get("cargo"))], Content.Blocks.Limits, "test"), Content.Rules);

    [Fact]
    public void TransferReordersRacksRepacksAndRefusesMissingOrSmallRacksWithoutAllocation()
    {
        var from = Design();
        var boxes = from.Boxes.ToArray();
        (boxes[1], boxes[2]) = (boxes[2], boxes[1]);
        var to = HullDesign.Of(new BoxDesign("Hold", 1, boxes, Content.Blocks.Limits, "test"), Content.Rules);
        HoldContainer[] cargo = [new(1, 4, 0, 100, 500, default), new(1, 0, -1, 0, 50, default)];
        var output = new HoldContainer[255];
        var occupied = new byte[64];
        var probe = new AllocationProbe();
        while (probe.Measuring()) Assert.True(CargoTransfer.TryPlan(Content, from, to, cargo, output, occupied));
        Assert.Equal(0, probe.Bytes);
        Assert.Equal(2, output[0].Rack);
        Assert.Equal(100, output[0].Amount);
        boxes[2] = boxes[2] with { Size = new GridSize(1, 1, 1) };
        to = HullDesign.Of(new BoxDesign("Hold", 1, boxes, Content.Blocks.Limits, "test"), Content.Rules);
        Assert.False(CargoTransfer.TryPlan(Content, from, to, cargo, output, occupied));
        boxes[2] = boxes[2] with { At = new GridSize(4, 0, 0), Size = new GridSize(2, 2, 2) };
        to = HullDesign.Of(new BoxDesign("Hold", 1, boxes, Content.Blocks.Limits, "test"), Content.Rules);
        Assert.False(CargoTransfer.TryPlan(Content, from, to, cargo, output, occupied));
    }

    [Fact]
    public void RepackingPreservesPayloadAndRefusalIsAtomic()
    {
        using var store = new BlockStateStore();
        var state = store.Create(Design(), true);
        var occupied = new byte[64];
        var small = new HoldContainer(1, 0, 0, 100, 30, default);
        var large = new HoldContainer(1, 4, 1, 200, 500, default);
        Assert.True(store.TryAddCargo(state, Content, small, occupied));
        Assert.True(store.TryAddCargo(state, Content, large, occupied));
        var before = store.Cargo(state).ToArray();
        Assert.Equal(300, store.CargoKilograms(state));
        Assert.Equal(large, before[1]);
        Assert.NotEqual(default, before[0].At);
        Assert.Equal(small with { At = before[0].At }, before[0]);
        Assert.False(store.TryAddCargo(state, Content, new(1, 6, -1, 0, 1600, default), occupied));
        Assert.Equal(before, store.Cargo(state).ToArray());
        Assert.False(store.TryAddCargo(state, Content, small, new byte[1]));
        Assert.Equal(before, store.Cargo(state).ToArray());
    }

    [Fact]
    public void InvalidPayloadAndDeadOrNonRackBlocksAreRefused()
    {
        using var store = new BlockStateStore();
        var state = store.Create(Design(), true);
        var occupied = new byte[64];
        var valid = new HoldContainer(1, 0, 0, 100, 50, default);
        HoldContainer[] invalid = [valid with { Rack = 0 }, valid with { Rack = -1 }, valid with { Rack = 3 },
            valid with { Size = 8 }, valid with { Resource = -2 }, valid with { Resource = Content.Galaxy.Resources.Length },
            valid with { Resource = -1 }, valid with { Amount = double.NaN }, valid with { Amount = double.PositiveInfinity },
            valid with { Amount = -1 }, valid with { Amount = 100000 }, valid with { Hull = float.NaN }, valid with { Hull = 51 },
            valid with { Hull = 0 }];
        foreach (var container in invalid) Assert.False(store.TryAddCargo(state, Content, container, occupied));
        Assert.Empty(store.Cargo(state).ToArray());
        Assert.True(store.TryAddCargo(state, Content, valid with { Resource = -1, Amount = 0 }, occupied));
        store.Lose(state, 1);
        Assert.Empty(store.Cargo(state).ToArray());
        Assert.False(store.TryAddCargo(state, Content, valid, occupied));
    }

    [Fact]
    public void CopiesOwnTheirCargoAndLosingRacksKeepsOnlyTheirFragment()
    {
        using var store = new BlockStateStore();
        var design = Design();
        var source = store.Create(design, true);
        var fragment = store.Create(design, true);
        var occupied = new byte[64];
        Assert.True(store.TryAddCargo(source, Content, new(1, 0, 0, 100, 50, default), occupied));
        Assert.True(store.TryAddCargo(source, Content, new(2, 0, 1, 200, 50, default), occupied));
        store.CopyFrom(fragment, store, source);
        store.Lose(fragment, 1);
        store.Lose(source, 2);
        Assert.Equal(100, store.CargoKilograms(source));
        Assert.Equal(200, store.CargoKilograms(fragment));
        store.Release(source);
        var recycled = store.Create(design, true);
        Assert.Empty(store.Cargo(recycled).ToArray());
        Assert.Equal(200, store.CargoKilograms(fragment));
    }

    [Fact]
    public void AddCopyAndRackLossTakeNoManagedMemory()
    {
        using var store = new BlockStateStore();
        var design = Design();
        var empty = store.Create(design, true);
        var target = store.Create(design, true);
        var occupied = new byte[64];
        var probe = new AllocationProbe();
        while (probe.Measuring())
        {
            store.CopyFrom(target, store, empty);
            Assert.True(store.TryAddCargo(target, Content, new(1, 0, 0, 100, 50, default), occupied));
            store.Lose(target, 1);
        }
        Assert.Equal(0, probe.Bytes);
    }
}
