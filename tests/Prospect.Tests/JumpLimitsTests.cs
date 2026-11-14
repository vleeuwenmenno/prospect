using Prospect.Engine.Net;
using Prospect.Game.Protocol;
using Prospect.Game.World;
using Xunit;

namespace Prospect.Tests;

public sealed class JumpLimitsTests
{
    private static readonly GameContent Content = GameContent.Load();
    private static JumpEligibility Evaluate(double distance, bool charted = false) =>
        JumpRules.Evaluate(default, new Double3(0, distance, 0), Content.Pod.Flight, Content.Exploration, charted);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NavigationWireIsExactAndRejectsTruncation(bool response)
    {
        Span<byte> bytes = stackalloc byte[NavigationMessage.MaxWireSize];
        var writer = new NetWriter(bytes);
        var view = new NavigationView { Hull = 7, ResultHull = 8, HasPlot = true, Target = new(1, 2, 3), Refusal = JumpRefusal.TooFar };
        NavigationMessage.Write(ref writer, response ? MessageId.Navigation : MessageId.PlotJump, view);
        Assert.Equal(response ? NavigationMessage.MaxWireSize : NavigationMessage.RequestWireSize, writer.Written.Length);
        var reader = new NetReader(writer.Written[1..]);
        Assert.True(NavigationMessage.Read(ref reader, response, out var read));
        Assert.Equal(view.Target, read.Target);
        Assert.Equal(response ? JumpRefusal.TooFar : JumpRefusal.None, read.Refusal);
        for (var i = 1; i < writer.Written.Length; i++)
        {
            reader = new NetReader(writer.Written[1..i]);
            Assert.False(NavigationMessage.Read(ref reader, response, out _));
        }
        bytes[5] = 2;
        reader = new NetReader(writer.Written[1..]);
        Assert.False(NavigationMessage.Read(ref reader, response, out _));
    }

    [Fact]
    public void LimitsAreInclusiveAndSpherical()
    {
        Assert.Equal(JumpRefusal.TooClose, Evaluate(4999).Reason);
        Assert.True(Evaluate(5000).CanIssueOrder);
        Assert.True(Evaluate(400000).CanIssueOrder);
        Assert.Equal(JumpRefusal.TooFar, Evaluate(400001).Reason);
        Assert.Equal(JumpRefusal.InvalidTarget, Evaluate(double.NaN).Reason);
        Assert.Equal(Evaluate(100000), JumpRules.Evaluate(default, new Double3(60000, 0, 80000), Content.Pod.Flight, Content.Exploration, false));
    }

    [Fact]
    public void RiskIsMonotoneSafeNearAndRemovedByChart()
    {
        Assert.Equal(0, Evaluate(40000).Risk);
        Assert.Equal(0.5f, Evaluate(220000).Risk);
        Assert.Equal(1, Evaluate(400000).Risk);
        Assert.Equal(0, Evaluate(400000, true).Risk);
        var trip = Evaluate(220000);
        var hits = 0;
        for (var tick = 0; tick < 1000; tick++)
        {
            var damage = JumpRules.DamageFraction(trip, 123, 1, tick);
            Assert.Equal(damage, JumpRules.DamageFraction(trip, 123, 1, tick));
            if (damage > 0) hits++;
        }
        Assert.InRange(hits, 400, 600);
    }

    [Fact]
    public void DamagePreservesBlocksAndFloorWithoutAllocations()
    {
        using var store = new BlockStateStore();
        var design = Content.Pod;
        var handle = store.Create(design, true);
        var probe = new AllocationProbe();
        for (var i = 0; i < 100; i++)
        {
            while (probe.Measuring()) JumpRules.ApplyDamage(store, handle, design, 0.9f);
        }
        Assert.Equal(0, probe.Bytes);
        Assert.InRange(store.HitTotal(handle), design.IntactHitTotal * 0.1, design.IntactHitTotal * 0.10001);
        Assert.Equal(0u, store.Revision(handle));
        for (var i = 0; i < design.BlockCount; i++) store.SetHitPoints(handle, i, design.IntactHitPoints[i] * 0.05f);
        var total = store.HitTotal(handle);
        JumpRules.ApplyDamage(store, handle, design, 1);
        Assert.Equal(total, store.HitTotal(handle));
    }
}
