using Prospect.Game.World;
using Xunit;

namespace Prospect.Tests;

public sealed class EditFuelTests
{
    private static readonly GameContent Content = GameContent.Load();
    private static DesignBox Tank(int x, int size, string kind = "nitrogenTank") =>
        new(new GridSize(x, 0, 0), new GridSize(size, 1, 1), Content.Blocks.Get(kind));
    private static HullDesign Design(params DesignBox[] tanks) => HullDesign.Of(
        new BoxDesign("fuel", 1, [Tank(-1, 1, "core"), .. tanks], Content.Blocks.Limits, "fuel"), Content.Rules);

    [Fact]
    public void GrowShrinkSplitMergeAndDiscardConserveFuel()
    {
        var small = Design(Tank(0, 1));
        var large = Design(Tank(0, 2));
        var pieces = Design(Tank(0, 1), Tank(1, 1));
        double capacity = small.Tanks[0].Capacity;
        var levels = new double[2];
        var dump = EditFuel.Transfer(small, [capacity], large, levels);
        Assert.Equal(capacity, levels[0]);
        Assert.Equal(0, dump.Nitrogen);
        dump = EditFuel.Transfer(large, [capacity * 2], small, levels);
        Assert.Equal(capacity, levels[0]);
        Assert.Equal(capacity, dump.Nitrogen);
        dump = EditFuel.Transfer(large, [capacity], pieces, levels);
        Assert.Equal([capacity / 2, capacity / 2], levels);
        Assert.Equal(0, dump.Nitrogen);
        dump = EditFuel.Transfer(pieces, [capacity, capacity / 2], large, levels);
        Assert.Equal(capacity * 1.5, levels[0]);
        Assert.Equal(0, dump.Nitrogen);
        foreach (var target in new[] { Design(Tank(0, 1, "bipropellantTank")), Design(Tank(0, 1, "hull")) })
        {
            dump = EditFuel.Transfer(small, [capacity], target, levels);
            Assert.Equal(capacity, dump.Nitrogen);
            Assert.Equal([0.0, 0.0], levels);
        }
    }

    [Fact]
    public void RefitPumpsMovedDeletedAndSwitchedTanksBeforeDumping()
    {
        var from = Design(Tank(0, 1), Tank(1, 1));
        var to = Design(Tank(0, 1), Tank(3, 1));
        double capacity = from.Tanks[0].Capacity;
        var levels = new double[2];
        var plan = EditFuel.Plan(from, [capacity / 2, capacity], to, levels);
        Assert.Equal([capacity, capacity / 2], levels);
        Assert.Equal(capacity, plan.Moved.Nitrogen);
        Assert.Equal(0, plan.Dumped.Nitrogen);
        // Fuel switched away from nitrogen is pumped into the untouched nitrogen tank.
        to = Design(Tank(0, 1), Tank(1, 1, "bipropellantTank"));
        plan = EditFuel.Plan(from, [capacity / 4, capacity / 2], to, levels);
        Assert.Equal([capacity * 0.75, 0], levels);
        Assert.Equal(capacity / 2, plan.Moved.Nitrogen);
        Assert.Equal(0, plan.Dumped.Nitrogen);
        // Delete has the same result until total capacity runs short.
        to = Design(Tank(0, 1));
        plan = EditFuel.Plan(from, [capacity / 4, capacity / 2], to, levels);
        Assert.Equal(capacity * 0.75, levels[0]);
        Assert.Equal(0, plan.Dumped.Nitrogen);
        plan = EditFuel.Plan(from, [capacity, capacity], to, levels);
        Assert.Equal(capacity, levels[0]);
        Assert.Equal(capacity, plan.Dumped.Nitrogen);
    }

    [Fact]
    public void FuelsNeverMixAndTheRuleAllocatesNothing()
    {
        var from = Design(Tank(0, 1), Tank(1, 1, "bipropellantTank"));
        var to = Design(Tank(0, 1, "bipropellantTank"), Tank(1, 1));
        double[] held = [100, 200];
        var levels = new double[2];
        var probe = new AllocationProbe();
        for (var i = 0; i < 100; i++)
            while (probe.Measuring()) EditFuel.Transfer(from, held, to, levels);
        Assert.Equal(0, probe.Bytes);
        var dump = EditFuel.Transfer(from, held, to, levels);
        Assert.Equal(0, dump.Nitrogen);
        Assert.Equal(0, dump.Bipropellant);
        Assert.Equal([200.0, 100.0], levels);
    }
}
