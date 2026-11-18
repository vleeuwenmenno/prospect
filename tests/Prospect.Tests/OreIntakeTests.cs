using System.Text;
using Prospect.Client.World;
using Prospect.Game.World;
using Xunit;

namespace Prospect.Tests;

/// <summary>
/// What the flown hull's ore bins can take of the chunks around it: the one answer a chunk gets from the shared room
/// rule, the counts the DRONES chip shows, the words the tooltip and notices use, and that none of it takes memory.
/// </summary>
public sealed class OreIntakeTests
{
    private static readonly GameContent Content = GameContent.Load();
    private static readonly GridSize One = new(1, 1, 1);

    private static DesignBox Box(string type, int x, int y, int z, BlockFacing facing = BlockFacing.Back) =>
        new(new GridSize(x, y, z), One, Content.Blocks.Get(type), BlockShape.Box, facing);

    /// <summary>A core with a generator, a hopper whose intake faces up and two one-cell bins: what a chunk needs.</summary>
    private static HullDesign Miner(bool hopper = true, bool bins = true)
    {
        var boxes = new List<DesignBox> { Box("core", 0, 0, 0), Box("generator", 0, -1, 0) };
        if (hopper) boxes.Add(Box("oreHopper", 2, 0, 0, BlockFacing.Up));
        if (bins)
        {
            boxes.Add(Box("oreBin", 0, -3, 0));
            boxes.Add(Box("oreBin", 0, -4, 0));
        }

        return TestDesigns.Of([.. boxes]);
    }

    private static double Capacity(HullDesign design, int bin) => design.Bins[bin].Capacity;

    private static string Text(Span<byte> utf8, int length) => Encoding.UTF8.GetString(utf8[..length]);

    [Fact]
    public void AnEmptyBinTakesAnyOreAndTwoFullBinsOfOneOreTakeNoneOfAnother()
    {
        var design = Miner();
        var alive = design.FullSet;
        var hoppers = design.Hoppers.Length;
        Span<OreBin> bins = [OreBin.Empty, OreBin.Empty];
        var capacity = Capacity(design, 0);

        // Empty bins take any ore, whole, while there is room for the whole chunk.
        Assert.Equal(OreIntakeState.Fits, OreIntake.State(bins, design.Bins, alive, hoppers, 3, capacity));
        Assert.Equal(OreIntakeState.BinsFull, OreIntake.State(bins, design.Bins, alive, hoppers, 3, capacity * 2 + 1));

        // Both bins full of one ore: another ore has no room at all, though the kilograms are all taken.
        OreBins.Add(bins, design.Bins, alive, 3, capacity * 2, 0.5f);
        Assert.Equal(OreIntakeState.BinsFull, OreIntake.State(bins, design.Bins, alive, hoppers, 7, 1));
        // Nothing at all is not a chunk: a caller that knows no mass is told nothing can be taken.
        Assert.Equal(OreIntakeState.NoBins, OreIntake.State(bins, design.Bins, alive, hoppers, 3, 0));
    }

    [Fact]
    public void RoomInKilogramsInBinsOfAnotherOreIsNoBinRatherThanFull()
    {
        var design = Miner();
        var alive = design.FullSet;
        var hoppers = design.Hoppers.Length;
        var capacity = Capacity(design, 0);

        // Half of each bin taken by another ore: 2 x capacity / 2 is free, but none of it for ore 3.
        Span<OreBin> bins = [new OreBin { Kg = capacity * 0.5, Resource = 5, Grade = 0.5f }, new OreBin { Kg = capacity * 0.5, Resource = 5, Grade = 0.5f }];
        Assert.Equal(OreIntakeState.OtherOres, OreIntake.State(bins, design.Bins, alive, hoppers, 3, capacity / 2));
        // A chunk too big for what is free is still full: there is no room for it in kilograms.
        Assert.Equal(OreIntakeState.BinsFull, OreIntake.State(bins, design.Bins, alive, hoppers, 3, capacity + 1));
        // Its own ore still goes in, into what is left of the bin that holds it.
        Assert.Equal(OreIntakeState.Fits, OreIntake.State(bins, design.Bins, alive, hoppers, 5, capacity / 2));

        // One bin emptied again takes the other ore, whatever the other one holds.
        bins[0] = OreBin.Empty;
        Assert.Equal(OreIntakeState.Fits, OreIntake.State(bins, design.Bins, alive, hoppers, 3, Capacity(design, 1)));
    }

    [Fact]
    public void AHullWithoutAHopperOrWithoutBinsTakesNothingAndALostBinIsNoBin()
    {
        var noHopper = Miner(hopper: false);
        var noBins = Miner(bins: false);
        Span<OreBin> bins = [OreBin.Empty, OreBin.Empty];
        Assert.Equal(OreIntakeState.NoBins, OreIntake.State(bins, noHopper.Bins, noHopper.FullSet, noHopper.Hoppers.Length, 0, 10));
        Assert.Equal(OreIntakeState.NoBins, OreIntake.State(bins, noBins.Bins, noBins.FullSet, noBins.Hoppers.Length, 0, 10));
        // A client that has not been told of the bins yet knows of none, and neither does one asked of no chunk.
        Assert.Equal(OreIntakeState.NoBins, OreIntake.State(ReadOnlySpan<OreBin>.Empty, noHopper.Bins, noHopper.FullSet, 1, 0, 10));
        Assert.Equal(OreIntakeState.NoBins, OreIntake.State(bins, noHopper.Bins, noHopper.FullSet, 1, 0, 0));

        // A bin whose block is gone holds nothing and takes nothing: the shared rule counts only live bins.
        var design = Miner();
        Span<ulong> alive = design.FullSet.ToArray();
        BlockSet.Remove(alive, design.Bins[0].Block);
        BlockSet.Remove(alive, design.Bins[1].Block);
        Assert.Equal(OreIntakeState.BinsFull, OreIntake.State(bins, design.Bins, alive, design.Hoppers.Length, 0, 1));
    }

    private static OreMarker Chunk(int resource, float mass, float own, OreIntakeState intake) =>
        new() { Id = (uint)(resource + 1), Resource = resource, Mass = mass, OwnDistance = own, Intake = intake };

    [Fact]
    public void TheCountsNearTheShipFollowWhatTheBinsWouldDoAndTheNearestOtherOreIsNamed()
    {
        OreMarker[] markers =
        [
            Chunk(1, 100, 10, OreIntakeState.Fits),
            Chunk(2, 100, 20, OreIntakeState.OtherOres),
            Chunk(3, 100, 30, OreIntakeState.BinsFull),
            Chunk(4, 100, 40, OreIntakeState.OtherOres),
            Chunk(5, 100, 900, OreIntakeState.BinsFull),
        ];
        var counts = OreIntake.Count(markers, 500);
        Assert.Equal((4, 1, 1, 2, 0), (counts.Near, counts.Fit, counts.Full, counts.Other, counts.None));
        Assert.True(counts.Blocked);
        Assert.Equal(2, OreIntake.NearestOtherOres(markers, 500));
        // Nothing near, nothing blocked and no ore named.
        Assert.Equal(default, OreIntake.Count(markers, 1));
        Assert.False(OreIntake.Count(markers, 1).Blocked);
        Assert.Equal(-1, OreIntake.NearestOtherOres(markers, 1));
        Assert.Equal(-1, OreIntake.NearestOtherOres([Chunk(1, 1, 1, OreIntakeState.Fits)], 500));
        // A hull with no bins has its chunks counted apart, since no ore of theirs is the reason.
        var none = OreIntake.Count([Chunk(1, 1, 1, OreIntakeState.NoBins)], 500);
        Assert.Equal((1, 0, 0, 0, 1), (none.Near, none.Fit, none.Full, none.Other, none.None));
        Assert.True(none.Blocked);
    }

    [Fact]
    public void TheReasonLineSaysWhichOfTheThreeItIsAndTheHeldLineNamesTheOresInTheWay()
    {
        var galaxy = Content.Galaxy;
        Span<byte> text = stackalloc byte[96];
        Assert.Equal(0, OreIntake.WriteReason(text, OreIntakeState.Fits, galaxy, 0));
        Assert.Equal("BINS FULL", Text(text, OreIntake.WriteReason(text, OreIntakeState.BinsFull, galaxy, 0)));
        Assert.Equal("NO ORE BINS", Text(text, OreIntake.WriteReason(text, OreIntakeState.NoBins, galaxy, 0)));
        Assert.Equal("NO BIN FOR GOLD", Text(text, OreIntake.WriteReason(text, OreIntakeState.OtherOres, galaxy, 5)));
        // An ore this galaxy lacks says so without a name rather than writing a wrong one.
        Assert.Equal("NO BIN FOR ", Text(text, OreIntake.WriteReason(text, OreIntakeState.OtherOres, galaxy, 99)));

        var design = Miner();
        var alive = design.FullSet;
        var capacity = Capacity(design, 0);
        Span<OreBin> bins = [OreBin.Empty, OreBin.Empty];
        // Empty bins keep nothing out, and a bin full of the chunk's own ore is not what keeps it out.
        Assert.Equal(0, OreIntake.WriteHeld(text, galaxy, 3, [OreBin.Empty, OreBin.Empty], design.Bins, alive));
        OreBins.Add(bins, design.Bins, alive, 3, capacity, 0.5f);
        Assert.Equal(0, OreIntake.WriteHeld(text, galaxy, 3, bins, design.Bins, alive));

        // The other ore, once, however many bins hold it.
        bins[1] = OreBin.Empty;
        OreBins.Add(bins, design.Bins, alive, 5, 10, 0.5f);
        Assert.Equal("BINS HOLD GOLD", Text(text, OreIntake.WriteHeld(text, galaxy, 3, bins, design.Bins, alive)));
    }

    [Fact]
    public void TheHeldLineStopsAtTwoNamesAndCountsTheRest()
    {
        // Three bins, each half full of another ore, so three ores keep a fourth out.
        var boxes = new List<DesignBox> { Box("core", 0, 0, 0), Box("generator", 0, -1, 0), Box("oreHopper", 2, 0, 0, BlockFacing.Up) };
        for (var i = 0; i < 3; i++) boxes.Add(Box("oreBin", 0, -3 - i, 0));
        var design = TestDesigns.Of([.. boxes]);
        var alive = design.FullSet;
        Span<OreBin> bins = [OreBin.Empty, OreBin.Empty, OreBin.Empty];
        for (var i = 0; i < 3; i++)
            OreBins.Add(bins, design.Bins, alive, i, design.Bins[i].Capacity * 0.5, 0.5f);
        Span<byte> text = stackalloc byte[96];
        Assert.Equal("BINS HOLD IRON, COPPER +1", Text(text, OreIntake.WriteHeld(text, Content.Galaxy, 3, bins, design.Bins, alive)));
        // A short buffer cuts the line rather than writing past it.
        Span<byte> small = stackalloc byte[12];
        Assert.Equal("BINS HOLD IR", Text(small, OreIntake.WriteHeld(small, Content.Galaxy, 3, bins, design.Bins, alive)));
    }

    [Fact]
    public void TheStateAndTheWordsTakeNoMemory()
    {
        var design = Miner();
        var alive = design.FullSet;
        var hoppers = design.Hoppers.Length;
        Span<OreBin> bins = [OreBin.Empty, OreBin.Empty];
        OreBins.Add(bins, design.Bins, alive, 5, Capacity(design, 0) * 0.5, 0.5f);
        Span<OreMarker> markers = [
            Chunk(1, 100, 10, OreIntakeState.Fits),
            Chunk(2, 100, 20, OreIntakeState.OtherOres),
            Chunk(3, 100, 30, OreIntakeState.BinsFull),
        ];
        var galaxy = Content.Galaxy;
        Span<byte> text = stackalloc byte[96];

        var allocation = new AllocationProbe();
        while (allocation.Measuring())
        {
            for (var i = 0; i < 200; i++)
            {
                var state = OreIntake.State(bins, design.Bins, alive, hoppers, i % 7, 10 + i);
                _ = OreIntake.Count(markers, 500);
                _ = OreIntake.NearestOtherOres(markers, 500);
                _ = OreIntake.WriteReason(text, state, galaxy, i % 7);
                _ = OreIntake.WriteHeld(text, galaxy, i % 7, bins, design.Bins, alive);
            }
        }

        Assert.Equal(0, allocation.Bytes);
    }
}
