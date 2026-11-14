using System.Security.Cryptography;
using Prospect.Game.World;
using Xunit;

namespace Prospect.Tests;

public sealed class ModuleEquipmentTests
{
    [Fact]
    public void ShippedBlocksSurviveCanonicalDecodeAndAppearInPaletteCatalogue()
    {
        var content = GameContent.Load();
        foreach (var id in new[] { "gasScanner", "asteroidScanner", "asteroidLocator" })
        {
            var type = content.Blocks.Get(id);
            Assert.Contains(type, content.Blocks.Types);
            var design = HullDesign.Of(new BoxDesign("module", 1,
                [new(default, new(1, 1, 1), type), new(new(2, 0, 0), new(1, 1, 1), content.Blocks.Get("core"))], content.Blocks.Limits, "test"), content.Rules);
            var bytes = DesignBytes.Encode(design);
            var decoded = DesignBytes.Decode(bytes, SHA256.HashData(bytes), content);
            var equipment = decoded.Stats.Module(type.Module.Family);
            Assert.Equal(1, equipment.Block);
            Assert.Equal(type.Module.Range, equipment.Range);
            Assert.Equal(ModuleQuality.Common, equipment.Quality);
            Assert.Equal(1, equipment.Mark);
            Assert.Equal(type.Mass + 3, decoded.Stats.Mass);
            Assert.Equal(type.PowerDraw + 0.2f, decoded.Flight.IdlePower);
        }
    }

    [Fact]
    public void ResizingAndDuplicatesDoNotStackAndLossSelectsRemainingBlockWithoutAllocation()
    {
        var content = GameContent.Load();
        var type = content.Blocks.Get("gasScanner");
        var boxes = new DesignBox[66];
        for (var i = 0; i < boxes.Length; i++) boxes[i] = new(new(i * 3, 0, 0), new(2, 1, 1), type);
        boxes[^1] = new(new(200, 0, 0), new(1, 1, 1), content.Blocks.Get("core"));
        var sums = new BoxHullSums(new BoxDesign("modules", 1, boxes, content.Blocks.Limits, "test"));
        var live = new ulong[BlockSet.WordCount(boxes.Length)];
        BlockSet.Fill(live, boxes.Length);
        var running = sums.Intact.ToArray();
        sums.Evaluate(running, content.Rules, out var flight, out var stats);
        Assert.Equal(1, stats.GasScanner.Block);
        Assert.Equal(1500, stats.GasScanner.Range);
        Assert.Equal(65 * 0.1f + 0.2f, flight.IdlePower, 4);
        Assert.Equal(263, stats.Mass);
        var probe = new AllocationProbe();
        while (probe.Run())
        {
            BlockSet.Fill(live, boxes.Length);
            sums.Build(running, live);
            probe.Start();
            for (var i = 0; i < boxes.Length; i++)
            {
                BlockSet.Remove(live, i);
                sums.Lose(running, live, i);
                sums.Evaluate(running, content.Rules, out flight, out stats);
            }
            probe.Stop();
        }
        Assert.Equal(0, probe.Bytes);
        Assert.Equal(default, stats.GasScanner);
        Assert.Equal(0, flight.IdlePower);
        BlockSet.Add(live, 64);
        sums.Build(running, live);
        sums.Evaluate(running, content.Rules, out _, out stats);
        Assert.Equal(65, stats.GasScanner.Block);
    }
}
