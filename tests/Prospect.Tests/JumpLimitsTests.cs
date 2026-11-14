using Prospect.Client;
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

    [Fact]
    public void CombinedProtocolIdsAreUniqueAndChartEnvelopeFitsExactly()
    {
        var ids = Enum.GetValues<MessageId>();
        Assert.Equal(ids.Length, ids.Distinct().Count());
        Assert.Equal(40u, ProtocolInfo.Version);
        Span<byte> bytes = stackalloc byte[ExplorationMessage.MaxWireSize];
        Span<SectorChart> charts = stackalloc SectorChart[ExplorationMessage.MaxCharts];
        for (var i = 0; i < charts.Length; i++) charts[i] = new(1, new(i, 0, 0), 0);
        var writer = new NetWriter(bytes);
        ExplorationMessage.Write(ref writer, default, charts);
        Assert.Equal(ExplorationMessage.MaxWireSize, writer.Written.Length);
        var reader = new NetReader(writer.Written[1..]);
        Assert.True(ExplorationMessage.Read(ref reader, charts, out _, out var count));
        Assert.Equal(ExplorationMessage.MaxCharts, count);
        Assert.Equal(8333, ExplorationMessage.VisibilityMaxWireSize);
    }

    [Fact]
    public void AHullsDataSheetCarriesItsDriveRangeSoAnotherShipsJumpCanBeCheckedFromIt()
    {
        foreach (var design in Content.Designs)
        {
            var flight = design.Flight;
            var stats = design.Stats;
            Assert.Equal(flight.FtlMaxDistance, stats.FtlMaxDistance);
            Assert.Equal(flight.Ftl, stats.Ftl);

            // The server's own check, and the one a client makes from the data sheet, agree for every design.
            var target = new Double3(0, 0, -120000);
            var server = JumpRules.Evaluate(default, target, flight, Content.Exploration, false);
            var client = JumpRules.Evaluate(default, target, JumpRules.DriveOf(Content.Pod.Flight, stats), Content.Exploration, false);
            Assert.Equal(server, client);
        }
    }

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(127, -1, -127)]
    [InlineData(-66, 1, 37)]
    public void ScriptedDefaultJumpStaysInRangeFromAnyStartSector(int x, int y, int z)
    {
        var options = new AppOptions();
        var position = Content.Galaxy.CenterOf(new(x, y, z));
        var target = options.JumpTarget(Content.Galaxy, position);
        Assert.Equal(Content.Galaxy.CenterOf(new(x + 6, y, z - 4)), target);
        foreach (var offset in new[] { -19999, 0, 19999 })
            Assert.True(JumpRules.Evaluate(position + new Double3(offset, offset, offset), target,
                Content.Pod.Flight, Content.Exploration, false).CanIssueOrder);
        options.JumpTo = new(x, y, z - 1);
        Assert.Equal(Content.Galaxy.CenterOf(options.JumpTo.Value), options.JumpTarget(Content.Galaxy, position));
    }

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
