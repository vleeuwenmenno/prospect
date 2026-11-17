using System.Numerics;
using System.Reflection;
using Prospect.Engine.Net;
using Prospect.Game.Persistence;
using Prospect.Game.Protocol;
using Prospect.Game.Server;
using Prospect.Game.World;
using Xunit;

namespace Prospect.Tests;

/// <summary>
/// Ore collection (R4): ore bins and their one rule, the hopper's intake, tractor beams and drones taking loose ore
/// chunks, the wire, the save and what an edit keeps.
/// </summary>
public sealed class OreCollectionTests
{
    private static readonly GameContent Content = GameContent.Load();
    private static readonly GameContent Shipped = FleetContent.Value;
    private static readonly GridSize One = new(1, 1, 1);

    private static DesignBox Box(string type, int x, int y, int z, BlockFacing facing = BlockFacing.Back, GridSize? size = null) =>
        new(new GridSize(x, y, z), size ?? One, Content.Blocks.Get(type), BlockShape.Box, facing);

    /// <summary>
    /// A core with a tractor beam on its top face, a generator and a battery under it, a hopper two cells to the right
    /// whose intake faces <paramref name="intake"/> (up, where the beam sees it) and two one-cell bins under the battery.
    /// </summary>
    private static HullDesign Miner(BlockFacing intake = BlockFacing.Up, bool hopper = true, bool bins = true)
    {
        var boxes = new List<DesignBox> { Box("core", 0, 0, 0), Box("generator", 0, -1, 0), Box("battery", 0, -2, 0), Box("hull", 1, 0, 0) };
        if (hopper) boxes.Add(Box("oreHopper", 2, 0, 0, intake));
        if (bins) { boxes.Add(Box("oreBin", 0, -3, 0)); boxes.Add(Box("oreBin", 0, -4, 0)); }
        return TestDesigns.Mounted([.. boxes], [new(0, BlockFacing.Up, 2, 2, Content.Blocks.Get("tractorBeam"))]);
    }

    /// <summary>A chunk seven metres above the design's first cell, at rest.</summary>
    private static OreChunk Chunk(HullDesign design, Double3 origin, int resource = 0, float mass = 300, float grade = 0.5f) => new()
    {
        Position = origin + new Vector3(.5f, 7, .5f) - (design.CenterOfMass + new Vector3(.5f)) * design.BlockSize,
        Orientation = Quaternion.Identity,
        Resource = resource,
        Grade = grade,
        Mass = mass,
    };

    private static double Capacity(HullDesign design, int bin) => design.Bins[bin].Capacity;

    [Fact]
    public void TheShippedBlocksHaveABinSizedByItsVolumeAndAHopperWithOneIntake()
    {
        var bin = Content.Blocks.Get("oreBin");
        var hopper = Content.Blocks.Get("oreHopper");
        Assert.True(bin.BoxOnly && bin.OreCapacity > 0 && !bin.Hopper && !bin.FacesOut);
        Assert.True(hopper.BoxOnly && hopper.Hopper && hopper.OreCapacity == 0 && hopper.FacesOut);
        Assert.True(Content.Blocks.Get("droneBay").FacesOut);

        // Capacity is the content's figure times the box's volume, whatever its shape.
        var design = TestDesigns.Mounted([Box("core", 0, 0, 0), Box("oreBin", 0, -1, 0), Box("oreBin", 1, 0, 0, size: new GridSize(1, 2, 3)), Box("oreHopper", 0, 1, 0, BlockFacing.Up)], []);
        Assert.Equal(2, design.Bins.Length);
        Assert.Equal((1, (double)bin.OreCapacity), (design.Bins[0].Block, design.Bins[0].Capacity));
        Assert.Equal((2, bin.OreCapacity * 6.0), (design.Bins[1].Block, design.Bins[1].Capacity));
        Assert.Equal([3], design.Hoppers);
        Assert.Equal(0, design.BinOf(1));
        Assert.Equal(-1, design.BinOf(0));

        // A design holds only so many of each.
        var many = new List<DesignBox> { Box("core", 0, 0, 0) };
        for (var i = 0; i <= OreBins.MaxBins; i++) many.Add(Box("oreBin", 1 + i, 0, 0));
        Assert.Throws<InvalidDataException>(() => TestDesigns.Mounted([.. many], []));
        var hoppers = new List<DesignBox> { Box("core", 0, 0, 0) };
        for (var i = 0; i <= OreBins.MaxHoppers; i++) hoppers.Add(Box("oreHopper", 1 + i, 0, 0, BlockFacing.Up));
        Assert.Throws<InvalidDataException>(() => TestDesigns.Mounted([.. hoppers], []));
    }

    [Theory]
    [InlineData("\"oreKgPerCubicMetre\": 2400", "\"oreKgPerCubicMetre\": -1")]
    [InlineData("\"name\": \"Ore bin\", \"boxOnly\": true", "\"name\": \"Ore bin\", \"boxOnly\": false")]
    [InlineData("\"oreKgPerCubicMetre\": 2400", "\"oreKgPerCubicMetre\": 2400, \"hopper\": true")]
    [InlineData("\"oreKgPerCubicMetre\": 2400", "\"oreKgPerCubicMetre\": 2400, \"cargo\": 8")]
    [InlineData("\"name\": \"Ore hopper\", \"boxOnly\": true", "\"name\": \"Ore hopper\", \"boxOnly\": false")]
    [InlineData("\"maxBins\": 64", "\"maxBins\": 65")]
    [InlineData("\"maxHoppers\": 16", "\"maxHoppers\": 17")]
    public void ContentThatMakesABinOrAHopperSomethingElseIsRefused(string old, string replacement)
    {
        using var copy = new ContentCopy();
        Assert.Throws<InvalidDataException>(() => copy.Replace("blocks/blocks.json", old, replacement).Load());
    }

    [Fact]
    public void ABinHoldsOneOreAndADeliveryFillsThatOresBinsFirstThenEmptyOnesAndSpills()
    {
        var design = Miner();
        var alive = design.FullSet;
        Span<OreBin> bins = [OreBin.Empty, OreBin.Empty];
        var capacity = Capacity(design, 0);
        Assert.Equal(2 * capacity, OreBins.Room(bins, design.Bins, alive, 0));

        // An empty bin takes any ore; the grade is the delivery's.
        Assert.Equal(1000, OreBins.Add(bins, design.Bins, alive, 3, 1000, 0.2f));
        Assert.Equal((1000.0, 3, 0.2f), (bins[0].Kg, bins[0].Resource, bins[0].Grade));
        Assert.True(bins[1].IsEmpty);

        // Another ore never joins it: it takes the next empty bin.
        Assert.Equal(500, OreBins.Add(bins, design.Bins, alive, 5, 500, 1f));
        Assert.Equal((500.0, 5), (bins[1].Kg, bins[1].Resource));
        Assert.Equal(capacity - 1000, OreBins.Room(bins, design.Bins, alive, 3));
        Assert.Equal(0, OreBins.Room(bins, design.Bins, alive, 7));

        // The same ore joins its bin, and the grade is weighted by mass: 1000 kg at 0.2 and 1000 kg at 0.6 is 0.4.
        Assert.Equal(1000, OreBins.Add(bins, design.Bins, alive, 3, 1000, 0.6f));
        Assert.Equal(2000, bins[0].Kg);
        Assert.Equal(0.4f, bins[0].Grade, 5);
        Assert.Equal(800, bins[0].OreKg, 3);

        // What a bin cannot take is not taken: only the room that is left.
        Assert.Equal(capacity - 2000, OreBins.Add(bins, design.Bins, alive, 3, 5000, 0.4f), 6);
        Assert.Equal(capacity, bins[0].Kg);
        Assert.Equal(0, OreBins.Add(bins, design.Bins, alive, 3, 10, 0.4f));

        // A delivery larger than one bin spills into the next empty one.
        Span<OreBin> fresh = [OreBin.Empty, OreBin.Empty];
        Assert.Equal(capacity + 600, OreBins.Add(fresh, design.Bins, alive, 2, capacity + 600, 0.3f));
        Assert.Equal((capacity, 600.0), (fresh[0].Kg, fresh[1].Kg));
        Assert.Equal((2, 2), (fresh[0].Resource, fresh[1].Resource));

        // Nothing that is no ore, no mass or no grade goes in.
        Span<OreBin> none = [OreBin.Empty, OreBin.Empty];
        Assert.Equal(0, OreBins.Add(none, design.Bins, alive, -1, 10, 0.5f));
        Assert.Equal(0, OreBins.Add(none, design.Bins, alive, 0, double.NaN, 0.5f));
        Assert.Equal(0, OreBins.Add(none, design.Bins, alive, 0, 10, 0));
        Assert.Equal(0, OreBins.Add(none, design.Bins, alive, 0, 10, 1.5f));
        Assert.True(none[0].IsEmpty && none[1].IsEmpty);
    }

    [Fact]
    public void AHullsBinsStartEmptyCountInItsMassOnBothSidesAndALostBinLosesItsOre()
    {
        var design = Miner();
        using var server = new HullStore();
        using var prediction = new HullStore();
        var a = server.Create(1, 0, design, default).Index;
        var b = prediction.Create(1, 0, design, default).Index;
        var state = server.Info(a).BlockState;
        var bins = server.BlockStates.Bins(state);
        Assert.Equal(2, bins.Length);
        Assert.True(bins[0].IsEmpty && bins[1].IsEmpty && bins[0].Resource == -1);
        Assert.Equal(0u, server.BlockStates.OreRevision(state));

        Assert.Equal(1500, server.BlockStates.AddOre(state, 2, 1500, 0.25f));
        Assert.Equal(1u, server.BlockStates.OreRevision(state));
        Assert.Equal(1500, server.BlockStates.OreKilograms(state));
        Assert.Equal(Capacity(design, 0) - 1500 + Capacity(design, 1), server.BlockStates.OreRoom(state, 2));

        // A copy of the state is the same hull: the predicting client flies exactly what the server does.
        prediction.BlockStates.CopyFrom(prediction.Info(b).BlockState, server.BlockStates, state);
        prediction.Info(b).PredictDamage = true;
        var authoritative = new HullField(server, [design]);
        var predicted = new HullField(prediction, [design], authoritative: false);
        authoritative.RestoreFigures(a);
        predicted.RestoreFigures(b);
        Assert.Equal(design.Flight.Mass + 1.5f, server.Flight(a).Mass, 3);
        Assert.Equal(server.Flight(a), prediction.Flight(b));
        // The ore sits in its bin, under the hull: the centre of mass moves down.
        Assert.True(server.Flight(a).CenterOfMass.Y < design.Flight.CenterOfMass.Y);
        for (var tick = 0; tick < 30; tick++)
        {
            authoritative.Step(a, default);
            predicted.Step(b, default);
            Assert.Equal(server.State(a), prediction.State(b));
        }

        // The bin goes, and what it held with it; the other bin and the sums carry on.
        server.BlockStates.Lose(state, design.Bins[0].Block);
        Assert.Equal(2u, server.BlockStates.OreRevision(state));
        Assert.True(server.BlockStates.Bins(state)[0].IsEmpty);
        Assert.Equal(0, server.BlockStates.OreKilograms(state));
        Assert.Equal(Capacity(design, 1), server.BlockStates.OreRoom(state, 2));
        authoritative.Refit(a);
        Assert.Equal(design.Flight.Mass - Content.Blocks.Get("oreBin").Mass, server.Flight(a).Mass, 3);

        // A lost bin takes nothing more: a delivery goes to the bin that is left.
        Assert.Equal(100, server.BlockStates.AddOre(state, 4, 100, 1f));
        Assert.True(server.BlockStates.Bins(state)[0].IsEmpty);
        Assert.Equal(4, server.BlockStates.Bins(state)[1].Resource);
    }

    [Fact]
    public void WhatBinsMayHoldIsCheckedBeforeItIsInstalled()
    {
        var design = Miner();
        using var hulls = new HullStore();
        var state = hulls.Info(hulls.Create(1, 0, design, default).Index).BlockState;
        var resources = Content.Galaxy.Resources.Length;
        var capacity = Capacity(design, 0);
        OreBin Bin(double kg, int resource, float grade) => new() { Kg = kg, Resource = resource, Grade = grade };

        Assert.True(hulls.BlockStates.TrySetOre(state, [Bin(capacity, 1, 1f), OreBin.Empty], resources));
        Assert.Equal(capacity, hulls.BlockStates.Bins(state)[0].Kg);
        var revision = hulls.BlockStates.OreRevision(state);
        OreBin[][] refused =
        [
            [Bin(10, 1, 1f)],                                        // not one record a bin
            [Bin(capacity + 1, 1, 1f), OreBin.Empty],                // more than the bin holds
            [Bin(-1, 1, 1f), OreBin.Empty],
            [Bin(double.NaN, 1, 1f), OreBin.Empty],
            [Bin(10, resources, 1f), OreBin.Empty],                  // an ore the galaxy has not
            [Bin(10, -1, 1f), OreBin.Empty],
            [Bin(10, 1, 0f), OreBin.Empty],                          // no grade
            [Bin(10, 1, 1.5f), OreBin.Empty],
            [Bin(10, 1, float.NaN), OreBin.Empty],
            [Bin(0, 1, 0f), OreBin.Empty],                           // an empty bin names no ore
            [Bin(0, -1, 0.5f), OreBin.Empty],
        ];
        foreach (var bins in refused)
            Assert.False(hulls.BlockStates.TrySetOre(state, bins, resources));
        Assert.Equal(revision, hulls.BlockStates.OreRevision(state));
        Assert.Equal(capacity, hulls.BlockStates.Bins(state)[0].Kg);

        // Nothing in a bin whose block is gone.
        hulls.BlockStates.Lose(state, design.Bins[1].Block);
        Assert.False(hulls.BlockStates.TrySetOre(state, [OreBin.Empty, Bin(10, 1, 1f)], resources));
        Assert.True(hulls.BlockStates.TrySetOre(state, [Bin(10, 1, 1f), OreBin.Empty], resources));
    }

    [Fact]
    public void AnEditKeepsOreWhereItsBinsSurviveByOverlapFirstAndDumpsWhatHasNoRoom()
    {
        var from = TestDesigns.Mounted([Box("core", 0, 0, 0), Box("oreBin", 1, 0, 0), Box("oreBin", 2, 0, 0), Box("oreBin", 3, 0, 0)], []);
        var capacity = Capacity(from, 0);
        OreBin[] held = [new() { Kg = 1000, Resource = 1, Grade = 0.5f }, new() { Kg = capacity, Resource = 2, Grade = 0.25f }, new() { Kg = 400, Resource = 1, Grade = 1f }];

        // The same bins: everything stays where it was.
        Span<OreBin> same = stackalloc OreBin[3];
        Assert.Equal(0, OreBins.Transfer(from, held, from, same));
        Assert.Equal(held, same.ToArray());

        // The first bin is gone and a new one stands elsewhere: the second keeps its ore by overlap, the third its own,
        // and the first's ore joins the bin that holds that ore before it takes the empty one.
        var moved = TestDesigns.Mounted([Box("core", 0, 0, 0), Box("oreBin", 2, 0, 0), Box("oreBin", 3, 0, 0), Box("oreBin", 0, 1, 0)], []);
        Span<OreBin> after = stackalloc OreBin[3];
        Assert.Equal(0, OreBins.Transfer(from, held, moved, after));
        Assert.Equal((capacity, 2), (after[0].Kg, after[0].Resource));
        Assert.Equal((1400.0, 1), (after[1].Kg, after[1].Resource));
        Assert.Equal((1000 * 0.5f + 400) / 1400, after[1].Grade, 5);
        Assert.True(after[2].IsEmpty);

        // One bin left: it keeps what it held by overlap, and the ore with no room is dumped.
        var one = TestDesigns.Mounted([Box("core", 0, 0, 0), Box("oreBin", 2, 0, 0)], []);
        Span<OreBin> last = stackalloc OreBin[1];
        Assert.Equal(1400, OreBins.Transfer(from, held, one, last));
        Assert.Equal((capacity, 2, 0.25f), (last[0].Kg, last[0].Resource, last[0].Grade));

        // A bin grown over two old ones of the same ore takes both; no bin at all dumps everything.
        var grown = TestDesigns.Mounted([Box("core", 0, 0, 0), Box("oreBin", 1, 0, 0, size: new GridSize(3, 1, 1))], []);
        OreBin[] iron = [new() { Kg = 1000, Resource = 1, Grade = 0.5f }, OreBin.Empty, new() { Kg = 400, Resource = 1, Grade = 1f }];
        Span<OreBin> big = stackalloc OreBin[1];
        Assert.Equal(0, OreBins.Transfer(from, iron, grown, big));
        Assert.Equal((1400.0, 1), (big[0].Kg, big[0].Resource));
        var bare = TestDesigns.Mounted([Box("core", 0, 0, 0)], []);
        Assert.Equal(1000 + capacity + 400, OreBins.Transfer(from, held, bare, Span<OreBin>.Empty));
    }

    [Fact]
    public void ABeamSeesAnIntakeByTheRackRuleAndOnlyThroughItsOneFace()
    {
        var up = Miner();
        Assert.True(TractorReach.Intake(up.Boxes, up.Mounts, up.FullSet, up.BlockSize, 0, up.Hoppers[0], out var point));
        // The aim is on the intake face, the hopper's top.
        Assert.Equal(1f, point.Y, 4);
        Assert.Equal(up.Hoppers[0], TractorReach.ChooseHopper(up.Boxes, up.Mounts, up.FullSet, up.BlockSize, 0, up.Hoppers));

        // An intake that looks away from the beam, or down where the hull hides it, is not in view.
        foreach (var facing in new[] { BlockFacing.Down, BlockFacing.Right, BlockFacing.Front, BlockFacing.Back })
        {
            var design = Miner(facing);
            Assert.False(TractorReach.Intake(design.Boxes, design.Mounts, design.FullSet, design.BlockSize, 0, design.Hoppers[0], out _));
            Assert.Equal(-1, TractorReach.ChooseHopper(design.Boxes, design.Mounts, design.FullSet, design.BlockSize, 0, design.Hoppers));
        }

        // A block that is no hopper, a lost hopper and a lost mount's block have no intake.
        Assert.False(TractorReach.Intake(up.Boxes, up.Mounts, up.FullSet, up.BlockSize, 0, 1, out _));
        var alive = up.FullSet.ToArray();
        BlockSet.Remove(alive, up.Hoppers[0]);
        Assert.False(TractorReach.Intake(up.Boxes, up.Mounts, alive, up.BlockSize, 0, up.Hoppers[0], out _));

        // The path: outside the intake along its normal, then in until the chunk's centre is its half edge inside.
        Assert.True(TractorPath.TryGuideIntake(up, up.FullSet, 0, up.Hoppers[0], 0.2f, 0.15f, out var guide));
        Assert.Equal(new Vector3(2.5f, 1.35f, 0.5f), guide.Staging);
        Assert.Equal(new Vector3(2.5f, 0.8f, 0.5f), guide.Slot);
        Assert.True(TractorPath.TryGuideIntake(up, up.FullSet, 0, up.Hoppers[0], 3f, 0.15f, out guide));
        Assert.Equal(0.5f, guide.Slot.Y, 4);
    }

    [Fact]
    public void ABeamLeadsAChunkIntoTheHopperAndItsOreGoesIntoABinWithoutTakingMemory()
    {
        var design = Miner(); using var hulls = new HullStore();
        var origin = Content.Galaxy.CenterOf(new(0, 100, 0));
        var h = hulls.Create(1, 0, design, origin);
        var field = new HullField(hulls, [design]);
        var bodies = new FreeContainers(); var chunks = new OreChunks();
        Assert.True(chunks.Add(Content, Chunk(design, origin, resource: 2, mass: 300, grade: 0.5f), out _));
        var tractor = new TractorSimulation(); var scratch = new byte[8];
        var contact = new OreChunkSimulation(Content.Galaxy); var rocks = new RockField(Content.Galaxy);
        var probe = new AllocationProbe(); var held = false; var glided = false; var billed = false;
        for (var tick = 0; tick < 1800 && !chunks.Items.IsEmpty; tick++)
        {
            while (probe.Measuring())
            {
                field.BeginRound(); tractor.Plan(bodies, chunks, Content, field, scratch, tick);
                field.Step(h.Index, default);
                tractor.Pull(bodies, chunks, Content, field, scratch, tick);
                contact.Step(chunks, Content, field, rocks, tick);
            }
            billed |= hulls.Info(h.Index).Power.Tractor > 0;
            if (chunks.Items.IsEmpty) break;
            held |= chunks.Items[0].TractorHull == h.Packed && chunks.Items[0].TractorHopper == design.Hoppers[0];
            glided |= chunks.Items[0].TractorStage == 1;
        }

        Assert.True(held); Assert.True(glided); Assert.True(billed);
        Assert.True(chunks.Items.IsEmpty, "the hopper took the chunk");
        var bin = hulls.BlockStates.Bins(hulls.Info(h.Index).BlockState)[0];
        Assert.Equal((300.0, 2, 0.5f), (bin.Kg, bin.Resource, bin.Grade));
        Assert.Equal(design.Flight.Mass + 0.3f, hulls.Flight(h.Index).Mass, 3);
        Assert.Equal(0, probe.Bytes);
    }

    [Fact]
    public void AContainerComesFirstAndAChunkIsTakenOnlyWithAHopperInViewAndRoomForAllOfIt()
    {
        // The rack design of the tractor tests with a hopper and bins added: one beam, a container and a chunk in reach.
        var design = TestDesigns.Mounted(
            [Box("core", 0, 0, 0), Box("cargo", 2, 1, 0), Box("generator", 0, -1, 0), Box("battery", 0, -2, 0), Box("oreHopper", -2, 0, 0, BlockFacing.Up), Box("oreBin", 0, -3, 0), Box("hull", -1, 0, 0)],
            [new(0, BlockFacing.Up, 2, 2, Content.Blocks.Get("tractorBeam"))]);
        using (var hulls = new HullStore())
        {
            var h = hulls.Create(1, 0, design, Double3.Zero);
            var field = new HullField(hulls, [design]);
            var bodies = new FreeContainers(); var chunks = new OreChunks();
            Assert.True(chunks.Add(Content, Chunk(design, Double3.Zero), out _));
            Assert.True(bodies.Add(Content, new FreeContainer { Position = Chunk(design, Double3.Zero).Position + new Vector3(0, 3, 0), Orientation = Quaternion.Identity, Size = 0, Resource = -1, Hull = 50 }, out _));
            var tractor = new TractorSimulation(); var scratch = new byte[8];
            tractor.Plan(bodies, chunks, Content, field, scratch);
            Assert.Equal(h.Packed, bodies.Items[0].TractorHull);
            Assert.Equal(0u, chunks.Items[0].TractorHull);
            // What the hull asks for is the container's pull alone.
            Assert.Equal(design.Mounts[0].Type.Tractor!.Power(TractorSimulation.Mass(Content.Cargo, bodies.Items[0])), hulls.Info(h.Index).TractorDemand);

            // With the container gone the beam is free for the chunk, and asks for the chunk's pull.
            bodies.Remove(0);
            tractor.Plan(bodies, chunks, Content, field, scratch);
            Assert.Equal(h.Packed, chunks.Items[0].TractorHull);
            Assert.Equal(design.Mounts[0].Type.Tractor!.Power(chunks.Items[0].Mass), hulls.Info(h.Index).TractorDemand);

            // A chunk let go by a stall waits its retry out.
            ref var item = ref chunks.At(0);
            OreChunks.ClearTractor(ref item);
            item.RetryTick = 100;
            tractor.Plan(bodies, chunks, Content, field, scratch, 99);
            Assert.Equal(0u, chunks.Items[0].TractorHull);
            tractor.Plan(bodies, chunks, Content, field, scratch, 100);
            Assert.Equal(h.Packed, chunks.Items[0].TractorHull);
        }

        // No hopper, an intake the beam cannot see, no bin, a chunk too heavy for the beam: never taken.
        foreach (var other in new[] { Miner(hopper: false), Miner(BlockFacing.Down), Miner(bins: false) })
            Assert.Equal(0u, Planned(other, Chunk(other, Double3.Zero)).TractorHull);
        var miner = Miner();
        Assert.NotEqual(0u, Planned(miner, Chunk(miner, Double3.Zero)).TractorHull);
        Assert.Equal(0u, Planned(miner, Chunk(miner, Double3.Zero, mass: miner.Mounts[0].Type.Tractor!.MaxMass + 1)).TractorHull);

        // Room for the whole chunk, or it is left: a full ship stops pulling.
        var capacity = Capacity(miner, 0);
        Assert.NotEqual(0u, Planned(miner, Chunk(miner, Double3.Zero, resource: 0, mass: 300), (0, capacity), (0, capacity - 300)).TractorHull);
        Assert.Equal(0u, Planned(miner, Chunk(miner, Double3.Zero, resource: 0, mass: 300), (0, capacity), (0, capacity - 299)).TractorHull);
        Assert.Equal(0u, Planned(miner, Chunk(miner, Double3.Zero, resource: 0, mass: 300), (1, 10), (2, 10)).TractorHull);
        Assert.NotEqual(0u, Planned(miner, Chunk(miner, Double3.Zero, resource: 1, mass: 300), (1, 10), (2, 10)).TractorHull);
        // A chunk larger than one bin fits across two.
        Assert.NotEqual(0u, Planned(miner, Chunk(miner, Double3.Zero, mass: (float)capacity + 500)).TractorHull);
        Assert.Equal(0u, Planned(miner, Chunk(miner, Double3.Zero, mass: (float)capacity * 2 + 500)).TractorHull);
    }

    /// <summary>One plan for a hull of <paramref name="design"/> whose bins hold <paramref name="held"/> (ore, kilograms), with one chunk loose.</summary>
    private static OreChunk Planned(HullDesign design, OreChunk chunk, params (int Resource, double Kg)[] held)
    {
        using var hulls = new HullStore();
        var h = hulls.Create(1, 0, design, Double3.Zero);
        var field = new HullField(hulls, [design]);
        if (held.Length > 0)
        {
            var bins = new OreBin[held.Length];
            for (var i = 0; i < held.Length; i++) bins[i] = new OreBin { Kg = held[i].Kg, Resource = held[i].Resource, Grade = 0.5f };
            Assert.True(hulls.BlockStates.TrySetOre(hulls.Info(h.Index).BlockState, bins, Content.Galaxy.Resources.Length));
        }
        var chunks = new OreChunks();
        Assert.True(chunks.Add(Content, chunk, out _));
        new TractorSimulation().Plan(new FreeContainers(), chunks, Content, field, new byte[8]);
        return chunks.Items[0];
    }

    [Fact]
    public void ABeamHoldsOneChunkAtATimeAndLetsGoOfOneTheBinsNoLongerHaveRoomFor()
    {
        var design = Miner(); using var hulls = new HullStore();
        var h = hulls.Create(1, 0, design, Double3.Zero);
        var field = new HullField(hulls, [design]);
        var bodies = new FreeContainers(); var chunks = new OreChunks();
        Assert.True(chunks.Add(Content, Chunk(design, Double3.Zero, mass: 800), out var first));
        var beside = Chunk(design, Double3.Zero, mass: 800);
        beside.Position += new Vector3(1.5f, 0, 0);
        Assert.True(chunks.Add(Content, beside, out _));
        var tractor = new TractorSimulation(); var scratch = new byte[8];
        var contact = new OreChunkSimulation(Content.Galaxy); var rocks = new RockField(Content.Galaxy);
        var state = hulls.Info(h.Index).BlockState;
        var together = false; var filled = false;
        for (var tick = 0; tick < 1200; tick++)
        {
            field.BeginRound(); tractor.Plan(bodies, chunks, Content, field, scratch, tick);
            together |= chunks.Items[0].TractorHull != 0 && chunks.Items[1].TractorHull != 0;
            // While the first is on its way the bins fill behind its back: less room is left than the chunk needs.
            if (!filled && chunks.Items[0].TractorHull != 0 && tick > 30)
            {
                Assert.Equal(Capacity(design, 0) * 2 - 500, hulls.BlockStates.AddOre(state, 0, Capacity(design, 0) * 2 - 500, 0.5f), 6);
                filled = true;
            }
            field.Step(h.Index, default);
            tractor.Pull(bodies, chunks, Content, field, scratch, tick);
            contact.Step(chunks, Content, field, rocks, tick);
        }

        // One beam, one chunk: the second was never held with the first.
        Assert.True(filled);
        Assert.False(together);
        // A chunk is taken whole or not at all: the beam let go, both chunks are loose and whole, and the bins hold
        // only what was put in them. A full ship stops pulling.
        Assert.Equal(2, chunks.Items.Length);
        Assert.Equal((800f, 0u), (chunks.Items[chunks.Find(first)].Mass, chunks.Items[chunks.Find(first)].TractorHull));
        Assert.Equal(0u, chunks.Items[1].TractorHull);
        Assert.Equal(Capacity(design, 0) * 2 - 500, hulls.BlockStates.OreKilograms(state), 6);
        Assert.Equal(0, hulls.Info(h.Index).TractorDemand);
    }

    [Fact]
    public void TheBinsMessageRoundTripsAndRefusesWhatNoBinCouldHold()
    {
        Assert.Equal(51, (int)MessageId.OreBins);
        Assert.Equal(13, OreBinsMessage.RecordBytes);
        Assert.Equal(OreBinsMessage.HeaderBytes + OreBins.MaxBins * OreBinsMessage.RecordBytes, OreBinsMessage.MaxWireSize);
        OreBin[] bins = [new() { Kg = 1234.5, Resource = 3, Grade = 0.25f }, OreBin.Empty, new() { Kg = 1, Resource = 0, Grade = 1f }];
        var buffer = new byte[OreBinsMessage.MaxWireSize];
        var writer = new NetWriter(buffer);
        OreBinsMessage.Write(ref writer, 7 | 1 << 16, 2, 99, bins);
        Assert.Equal(OreBinsMessage.HeaderBytes + 3 * OreBinsMessage.RecordBytes, writer.Written.Length);
        Assert.Equal((byte)MessageId.OreBins, writer.Written[0]);
        var read = new OreBin[OreBins.MaxBins];
        var reader = new NetReader(writer.Written[1..]);
        Assert.True(OreBinsMessage.TryRead(ref reader, read, out var hull, out var design, out var tick, out var count));
        Assert.Equal((7u | 1 << 16, (byte)2, 99L, 3), (hull, design, tick, count));
        Assert.Equal(bins, read[..3]);

        // Cut short, with a byte too many, for no hull, and with records no bin holds.
        var good = writer.Written.ToArray();
        bool Reads(byte[] bytes) { var r = new NetReader(bytes.AsSpan(1)); return OreBinsMessage.TryRead(ref r, new OreBin[OreBins.MaxBins], out _, out _, out _, out _); }
        Assert.True(Reads(good));
        Assert.False(Reads(good[..^1]));
        Assert.False(Reads([.. good, 0]));
        var noHull = (byte[])good.Clone(); noHull.AsSpan(1, 4).Clear();
        Assert.False(Reads(noHull));
        var first = OreBinsMessage.HeaderBytes;
        var emptyNamed = (byte[])good.Clone(); emptyNamed[first + OreBinsMessage.RecordBytes] = 2;
        Assert.False(Reads(emptyNamed));
        var noOre = (byte[])good.Clone(); noOre[first] = byte.MaxValue;
        Assert.False(Reads(noOre));
        var badKg = (byte[])good.Clone(); BitConverter.GetBytes(double.NaN).CopyTo(badKg, first + 1);
        Assert.False(Reads(badKg));
        var badGrade = (byte[])good.Clone(); BitConverter.GetBytes(1.5f).CopyTo(badGrade, first + 9);
        Assert.False(Reads(badGrade));
        Assert.False(Reads(Many(OreBins.MaxBins + 1)));

        static byte[] Many(int count)
        {
            var bytes = new byte[OreBinsMessage.HeaderBytes + count * OreBinsMessage.RecordBytes];
            var w = new NetWriter(bytes);
            var bins = new OreBin[count];
            Array.Fill(bins, OreBin.Empty);
            OreBinsMessage.Write(ref w, 9, 0, 1, bins);
            return bytes;
        }
    }

    [Fact]
    public void AChunksRecordCarriesTheBeamThatHoldsItAndTheBayViewAndTheSettingCarryOrePickup()
    {
        var chunk = new OreChunk
        {
            Id = 5,
            Position = new Double3(1, 2, 3),
            Orientation = Quaternion.Identity,
            Resource = 1,
            Grade = 0.5f,
            Mass = 300,
            TractorHull = 7 | 1 << 16,
            TractorMount = 4,
            TractorStage = 1,
            TractorPowered = true,
            TractorHopper = 9,
            TractorBest = 3,
            TractorIdle = 2,
            RetryTick = 50,
            FetchHull = 11,
            FetchMount = 2,
        };
        var buffer = new byte[OreChunkMessage.MaxWireSize];
        var writer = new NetWriter(buffer);
        OreChunkMessage.Write(ref writer, 10, new[] { chunk });
        var read = new OreChunk[1];
        var reader = new NetReader(writer.Written[1..]);
        Assert.True(OreChunkMessage.TryRead(ref reader, Content, read, out _, out var count));
        Assert.Equal(1, count);
        // The claim travels; what only the server needs does not.
        Assert.Equal((chunk.TractorHull, 4, (byte)1, true), (read[0].TractorHull, read[0].TractorMount, read[0].TractorStage, read[0].TractorPowered));
        Assert.Equal((0, 0f, 0, 0L, 0u, 0), (read[0].TractorHopper, read[0].TractorBest, read[0].TractorIdle, read[0].RetryTick, read[0].FetchHull, read[0].FetchMount));

        // A claim no server sends: a mount or a stage without a hull, a hull with no generation, a flag past one.
        var bytes = writer.Written.ToArray();
        var claim = OreChunkMessage.HeaderBytes + OreChunkMessage.RecordBytes - 8;
        foreach (var (at, value) in new[] { (claim + 7, (byte)2), (claim + 6, (byte)2) })
        {
            var broken = (byte[])bytes.Clone();
            broken[at] = value;
            reader = new NetReader(broken.AsSpan(1));
            Assert.False(OreChunkMessage.TryRead(ref reader, Content, read, out _, out _));
        }
        var noHull = (byte[])bytes.Clone(); noHull.AsSpan(claim, 4).Clear();
        reader = new NetReader(noHull.AsSpan(1));
        Assert.False(OreChunkMessage.TryRead(ref reader, Content, read, out _, out _));
        var noGeneration = (byte[])bytes.Clone(); noGeneration.AsSpan(claim + 2, 2).Clear();
        reader = new NetReader(noGeneration.AsSpan(1));
        Assert.False(OreChunkMessage.TryRead(ref reader, Content, read, out _, out _));

        // Both settings travel in one message and come back in the bay view.
        writer = new NetWriter(buffer);
        new DroneSettingsMessage(9, false, true).Write(ref writer);
        reader = new NetReader(writer.Written[1..]);
        Assert.True(DroneSettingsMessage.TryRead(ref reader, out var settings));
        Assert.Equal(new DroneSettingsMessage(9, false, true), settings);
        var view = new BayView { Hull = 9, Tick = 5, PickUpOres = true };
        writer = new NetWriter(buffer);
        BayMessage.Write(ref writer, view);
        reader = new NetReader(writer.Written[1..]);
        Assert.True(BayMessage.TryRead(ref reader, out var copy));
        Assert.True(copy.PickUpOres);
        Assert.False(copy.AutoPickup);
        var flag = writer.Written.ToArray();
        flag[1 + 4 + 8 + 4 + 4] = 2;
        reader = new NetReader(flag.AsSpan(1));
        Assert.False(BayMessage.TryRead(ref reader, out _));
        Assert.Equal(76u, ProtocolInfo.Version);
    }

    // From here on: a real server, its host on an in-process link.

    /// <summary>
    /// The shipped content with a pod that has a tractor beam on its core's top face, a hopper in its view with the
    /// intake up, two bins under the hull and a drone bay whose hatch faces left.
    /// </summary>
    private static GameContent Mining(bool bay = true)
    {
        DesignBox Part(string type, int x, int y, int z, BlockFacing facing = BlockFacing.Front) =>
            new(new GridSize(x, y, z), One, Shipped.Blocks.Get(type), BlockShape.Box, facing);
        var boxes = new List<DesignBox>
        {
            Part("core", 0, 0, 0), Part("generator", 0, -1, 0), Part("battery", 0, -2, 0), Part("gyro", -1, 0, 0),
            Part("ionDrive", 0, 0, 1), Part("sensor", 0, 0, -1), Part("hull", 1, 0, 0), Part("oreHopper", 2, 0, 0, BlockFacing.Up),
            Part("oreBin", 0, -3, 0), Part("oreBin", 0, -4, 0),
        };
        if (bay) boxes.Add(Part("droneBay", -1, 0, -1, BlockFacing.Left));
        var pod = Shipped.Pod;
        var design = HullDesign.Of(new BoxDesign(pod.Name, 1, [.. boxes], Shipped.Blocks.Limits, "test", pod.Class, pod.Color,
            [new DesignMount(0, BlockFacing.Up, 2, 2, Shipped.Blocks.Get("tractorBeam"))]), Shipped.Rules);
        var designs = Shipped.Designs.ToArray();
        designs[Shipped.PodDesign] = design;
        return Shipped.WithCatalogue(designs, Shipped.PodDesign, Shipped.DroneDesign, Shipped.StartingFleet, Shipped.WorkDroneDesign);
    }

    private static SaveRig Rig(GameContent content)
    {
        var rig = new SaveRig(SaveRig.NewWorld(content), content);
        rig.Join();
        rig.Tick();
        return rig;
    }

    private static HullHandle Pod(SaveRig rig) { var own = rig.Link.LastSnapshot.OwnHull; return new((int)(own & 0xFFFF), (int)(own >> 16)); }

    private static OreChunks Chunks(SaveRig rig) => rig.Get<OreChunks>("_chunks");

    private static ReadOnlySpan<OreBin> Bins(SaveRig rig) => rig.Hulls.BlockStates.Bins(rig.Hulls.Info(Pod(rig).Index).BlockState);

    /// <summary>A chunk at <paramref name="offset"/> from the pod, at rest with it.</summary>
    private static uint Place(SaveRig rig, GameContent content, Vector3 offset, int resource = 1, float mass = 400, float grade = 0.25f)
    {
        var pod = rig.Hulls.State(Pod(rig).Index);
        Assert.True(Chunks(rig).Add(content, new OreChunk
        {
            Position = pod.Position + offset,
            Velocity = pod.Velocity,
            Orientation = Quaternion.Identity,
            Resource = resource,
            Grade = grade,
            Mass = mass,
            Born = rig.WorldTick,
        }, out var id));
        return id;
    }

    private static void TickUntil(SaveRig rig, Func<bool> done, float seconds, string what)
    {
        for (var i = 0; i < seconds * 60 && !done(); i++)
            rig.Tick();
        Assert.True(done(), what);
    }

    private static List<int> Drones(SaveRig rig)
    {
        var found = new List<int>();
        for (var i = 0; i < rig.Hulls.Slots; i++)
            if (rig.Hulls.IsAliveAt(i) && rig.Hulls.Info(i).WorkDrone) found.Add(i);
        return found;
    }

    /// <summary>
    /// True once every drone out is on a <see cref="OrderTask.Hold"/> order and near the place it holds: a fetch that is
    /// over parks its drones at their hold points instead of docking them.
    /// </summary>
    private static bool Parked(SaveRig rig)
    {
        var drones = Drones(rig);
        if (drones.Count == 0) return false;
        var orders = rig.Get<TaskOrder[]>("_orders");
        var count = rig.Get<int>("_orderCount");
        var parked = 0;
        for (var i = 0; i < count; i++)
        {
            if (orders[i].Task != OrderTask.Hold || !drones.Contains(orders[i].Actor.Index)) continue;
            var state = rig.Hulls.State(orders[i].Base.Index);
            var place = state.Position + HullRotation.Rotate(orders[i].Point, state.Orientation);
            if (rig.Hulls.State(orders[i].Actor.Index).Position.RelativeTo(place).LengthSquared() > 4f) return false;
            parked++;
        }
        return parked == drones.Count;
    }

    private static void Settings(SaveRig rig, bool cargo, bool ores) =>
        rig.Link.DroneSettings(new DroneSettingsMessage(rig.Link.LastSnapshot.OwnHull, cargo, ores));

    [Fact]
    public void TheServerStowsAChunkInReachAndTellsThePilotWhatTheBinsHold()
    {
        var content = Mining();
        using var rig = Rig(content);
        rig.Tick(2);
        // The bins are told once, empty, with the hull.
        Assert.Equal(1, rig.Link.OreMessages);
        Assert.Equal(2, rig.Link.OreCount);
        Assert.True(rig.Link.Ore[0].IsEmpty && rig.Link.Ore[1].IsEmpty);

        Place(rig, content, new Vector3(0, 9, 0), resource: 1, mass: 400, grade: 0.25f);
        TickUntil(rig, () => Chunks(rig).Items.Length == 0, 60, "the chunk went into the hopper");
        Assert.Equal((400.0, 1, 0.25f), (Bins(rig)[0].Kg, Bins(rig)[0].Resource, Bins(rig)[0].Grade));
        rig.Tick();
        Assert.Equal(2, rig.Link.OreMessages);
        Assert.Equal((400.0, 1, 0.25f), (rig.Link.Ore[0].Kg, rig.Link.Ore[0].Resource, rig.Link.Ore[0].Grade));
        Assert.Equal(rig.Link.LastSnapshot.OwnHull, rig.Link.OreHull);

        // Nothing more is sent while nothing changes; a second chunk of the same ore joins the bin, weighted by mass.
        rig.Tick(30);
        Assert.Equal(2, rig.Link.OreMessages);
        Place(rig, content, new Vector3(0, 9, 0), resource: 1, mass: 400, grade: 0.75f);
        TickUntil(rig, () => Chunks(rig).Items.Length == 0, 60, "the second chunk went in");
        rig.Tick();
        Assert.Equal(3, rig.Link.OreMessages);
        Assert.Equal(800, rig.Link.Ore[0].Kg);
        Assert.Equal(0.5f, rig.Link.Ore[0].Grade, 5);
        Assert.Null(rig.Server.Fault);
    }

    [Fact]
    public void PickUpOresSendsDronesForAChunkBeyondTheBeamAndTheBeamTakesItIntoTheHopper()
    {
        var content = Mining();
        using var rig = Rig(content);
        var id = Place(rig, content, new Vector3(-150, 20, 40), mass: 600);

        // Off by default: nothing goes out for a loose chunk, and "Pick up cargo" alone sends nobody for one.
        rig.Tick(90);
        Assert.False(rig.Link.Bays.PickUpOres);
        Settings(rig, cargo: true, ores: false);
        rig.Tick(90);
        Assert.True(rig.Link.Bays.AutoPickup);
        Assert.False(rig.Link.Bays.PickUpOres);
        Assert.Equal(0u, Chunks(rig).Items[0].FetchHull);
        Assert.Empty(Drones(rig));

        Settings(rig, cargo: false, ores: true);
        rig.Tick();
        Assert.True(rig.Link.Bays.PickUpOres);
        Assert.False(rig.Link.Bays.AutoPickup);
        TickUntil(rig, () => Chunks(rig).Items[0].FetchHull != 0, 3, "ore pickup started a fetch");
        Assert.Equal(DroneReply.Fetching, rig.Link.Bays.Reply);
        Assert.Equal(1, rig.Link.Bays.Fetching);
        TickUntil(rig, () => Drones(rig).Count == 1, 3, "one drone launched");

        // The drones have it: no beam takes it on the way in.
        TickUntil(rig, () => Chunks(rig).Items[0].Position.RelativeTo(rig.Hulls.State(Pod(rig).Index).Position).Length() < 30, 120, "the chunk brought close");
        Assert.Equal(0u, Chunks(rig).Items[0].TractorHull);
        TickUntil(rig, () => Chunks(rig).Find(id) < 0, 90, "the beam took the chunk into the hopper");
        Assert.Equal(600, Bins(rig)[0].Kg);
        Assert.Equal(DroneReply.Delivered, rig.Link.Bays.Reply);
        // The chunk is in: the drone goes back to holding station, out of its bay, ready for the next one.
        TickUntil(rig, () => Parked(rig), 60, "the drone parked at its hold point");
        Assert.Single(Drones(rig));
        Assert.Equal(1, rig.Link.Bays.Bays[0].Out);
        Assert.Equal(3, rig.Link.Bays.Bays[0].Stocked);

        // Turned off, the next chunk is left where it lies; the parked drone stays out.
        Settings(rig, cargo: false, ores: false);
        rig.Tick();
        Assert.False(rig.Link.Bays.PickUpOres);
        Place(rig, content, new Vector3(-100, 10, 0));
        rig.Tick(60 * 8);
        Assert.Equal(0u, Chunks(rig).Items[0].FetchHull);
        Assert.Single(Drones(rig));
        Assert.True(Parked(rig));
        Assert.Null(rig.Server.Fault);
    }

    [Fact]
    public void OrePickupSendsTheParkedDronesForTheNextChunkWithoutLaunchingMore()
    {
        var content = Mining();
        using var rig = Rig(content);
        var first = Place(rig, content, new Vector3(-150, 20, 40), mass: 600);
        var second = Place(rig, content, new Vector3(-160, -20, 30), mass: 500);
        Settings(rig, cargo: false, ores: true);
        rig.Tick();
        TickUntil(rig, () => Chunks(rig).Items[0].FetchHull != 0, 3, "ore pickup started a fetch");
        TickUntil(rig, () => Chunks(rig).Find(first) < 0, 120, "the first chunk went into the hopper");
        Assert.Equal(600, Bins(rig)[0].Kg);
        Assert.Single(Drones(rig));
        Assert.Equal(1, rig.Link.Bays.Bays[0].Out);
        Assert.Equal(3, rig.Link.Bays.Bays[0].Stocked);

        // The second fetch takes the drone that is already out: nothing leaves a bay for it.
        TickUntil(rig, () => Chunks(rig).Find(second) >= 0 && Chunks(rig).Items[Chunks(rig).Find(second)].FetchHull != 0, 10, "ore pickup started the second fetch");
        Assert.Single(Drones(rig));
        Assert.Equal(1, rig.Link.Bays.Bays[0].Out);
        Assert.Equal(3, rig.Link.Bays.Bays[0].Stocked);
        Assert.Equal(1, rig.Link.Bays.Fetching);
        TickUntil(rig, () => Chunks(rig).Find(second) < 0, 150, "the second chunk went into the hopper");
        Assert.Equal(1100, Bins(rig)[0].Kg);
        Assert.Single(Drones(rig));
        Assert.Equal(3, rig.Link.Bays.Bays[0].Stocked);

        // With nothing left to fetch, it goes back to holding station at its hold point; a recall would dock it.
        TickUntil(rig, () => Parked(rig), 60, "the drone parked at its hold point");
        Assert.Null(rig.Server.Fault);
    }

    [Fact]
    public void OrePickupLeavesAloneWhatTheBeamReachesWhatHasNoRoomAndWhatSomeoneHas()
    {
        var content = Mining();
        using var rig = Rig(content);
        var state = rig.Hulls.Info(Pod(rig).Index).BlockState;
        var capacity = content.Designs[content.PodDesign].Bins[0].Capacity;

        // The bins are full of another ore: no room, so nobody is sent and nothing is said.
        Assert.Equal(capacity * 2, rig.Hulls.BlockStates.AddOre(state, 2, capacity * 2, 0.5f));
        Place(rig, content, new Vector3(-150, 20, 40), resource: 1);
        Settings(rig, cargo: false, ores: true);
        rig.Tick(60 * 4);
        Assert.Equal(0u, Chunks(rig).Items[0].FetchHull);
        Assert.Empty(Drones(rig));
        Assert.NotEqual(DroneReply.NoRoom, rig.Link.Bays.Reply);

        // A hull with no hopper sends nobody either: drones bring chunks to a beam, and the beam needs an intake.
        using var bare = Rig(DroneFetchTests.Content());
        Place(bare, DroneFetchTests.Content(), new Vector3(-150, 20, 40));
        Settings(bare, cargo: false, ores: true);
        bare.Tick(60 * 4);
        Assert.True(bare.Link.Bays.PickUpOres);
        Assert.Equal(0u, Chunks(bare).Items[0].FetchHull);
        Assert.Empty(Drones(bare));
    }

    [Fact]
    public void OreAndOrePickupAreSavedWithTheHullAndASaveThatHoldsWhatNoBinCouldIsRefused()
    {
        var content = Mining();
        var folder = SaveRig.NewWorld(content);
        using (var rig = new SaveRig(folder, content))
        {
            rig.Join();
            rig.Tick();
            Settings(rig, cargo: false, ores: true);
            Place(rig, content, new Vector3(0, 9, 0), resource: 3, mass: 700, grade: 0.4f);
            TickUntil(rig, () => Chunks(rig).Items.Length == 0, 60, "the chunk went into the hopper");
            // A chunk still held by the beam when the world closes: saved loose, the claim is not.
            Place(rig, content, new Vector3(0, 30, 0), resource: 3, mass: 500);
            TickUntil(rig, () => Chunks(rig).Items[0].TractorHull != 0, 10, "the beam holds the second chunk");
            Assert.True(rig.Link.Bays.PickUpOres);
            Assert.True(rig.Close().Clean);
        }

        var saved = folder.Decode();
        Assert.Equal(23, WorldSnapshotCodec.Version);
        Assert.Equal(2, saved.BinCount);
        var pod = -1;
        for (var i = 0; i < saved.HullCount; i++)
            if (saved.Hulls[i].Bins > 0) pod = i;
        Assert.True(pod >= 0);
        Assert.True(saved.Hulls[pod].PickUpOres);
        Assert.False(saved.Hulls[pod].AutoPickup);
        Assert.Equal((700.0, 3, 0.4f), (saved.OreBins[saved.Hulls[pod].BinStart].Kg, saved.OreBins[saved.Hulls[pod].BinStart].Resource, saved.OreBins[saved.Hulls[pod].BinStart].Grade));
        Assert.True(saved.OreBins[saved.Hulls[pod].BinStart + 1].IsEmpty);
        Assert.Equal(1, saved.OreChunkCount);

        // The same bytes again from what was decoded.
        Assert.Equal(WorldSnapshotCodec.Encode(saved), WorldSnapshotCodec.Encode(folder.Decode()));

        using (var again = new SaveRig(folder, content))
        {
            again.Join();
            again.Tick(2);
            Assert.True(again.Link.Bays.PickUpOres);
            Assert.Equal((700.0, 3, 0.4f), (again.Link.Ore[0].Kg, again.Link.Ore[0].Resource, again.Link.Ore[0].Grade));
            Assert.Equal(700, Bins(again)[0].Kg);
            // The hull weighs its ore from the first interval on.
            Assert.Equal(content.Designs[content.PodDesign].Flight.Mass + 0.7f, again.Hulls.Flight(Pod(again).Index).Mass, 3);
            // The loose chunk starts over: no claim came with it, and the beam takes it again by itself.
            TickUntil(again, () => Chunks(again).Items.Length == 0, 60, "the saved chunk went into the hopper");
            Assert.Equal(1200, Bins(again)[0].Kg);
        }

        // What no bin of the design holds, an ore the galaxy has not, ore in a hull's bins that are not its design's,
        // and ore pickup on a hull with no bay: each save is refused whole.
        var capacity = content.Designs[content.PodDesign].Bins[0].Capacity;
        Func<WorldSnapshot, int, WorldSnapshot>[] broken =
        [
            (world, hull) => { world.OreBins[world.Hulls[hull].BinStart].Kg = capacity + 1; return world; },
            (world, hull) => { world.OreBins[world.Hulls[hull].BinStart].Resource = content.Galaxy.Resources.Length; return world; },
            (world, hull) => { world.Hulls[hull].Bins = 1; world.BinCount = 1; return world; },
        ];
        foreach (var change in broken)
            Assert.Throws<InvalidDataException>(() => new SaveRig(folder, content, edit: world => change(world, pod)).Dispose());
        var noBay = Mining(bay: false);
        var plain = SaveRig.NewWorld(noBay);
        using (var rig = new SaveRig(plain, noBay)) { rig.Join(); rig.Tick(2); Assert.True(rig.Close().Clean); }
        Assert.Throws<InvalidDataException>(() => new SaveRig(plain, noBay, edit: world =>
        {
            for (var i = 0; i < world.HullCount; i++) world.Hulls[i].PickUpOres = true;
            return world;
        }).Dispose());
    }

    [Fact]
    public void AnEditedHullKeepsItsOreAndOrePickup()
    {
        var content = Mining();
        using var rig = Rig(content);
        Settings(rig, cargo: false, ores: true);
        rig.Tick(2);
        var old = Pod(rig);
        var info = rig.Hulls.Info(old.Index);
        Assert.Equal(900, rig.Hulls.BlockStates.AddOre(info.BlockState, 2, 900, 0.3f));
        const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        // What ReplaceHull does once the new hull exists: the ore is carried before its figures are read, the bays after.
        var next = (HullHandle)typeof(ServerHost).GetMethod("CreateHull", Private)!.Invoke(rig.Server, [info.Owner, info.Design, rig.Hulls.State(old.Index).Position, false, info.Entity])!;
        var design = content.Designs[info.Design];
        typeof(ServerHost).GetMethod("CarryOre", Private)!.Invoke(rig.Server, [info.BlockState, design, rig.Hulls.Info(next.Index).BlockState, design]);
        typeof(ServerHost).GetMethod("CarryBays", Private)!.Invoke(rig.Server, [old, next]);
        rig.Hulls.Destroy(old);
        var bin = rig.Hulls.BlockStates.Bins(rig.Hulls.Info(next.Index).BlockState)[0];
        Assert.Equal((900.0, 2, 0.3f), (bin.Kg, bin.Resource, bin.Grade));
        var pages = rig.Get<Array>("_bayPages");
        var page = pages.GetValue(rig.Hulls.Info(next.Index).BayPage - 1)!;
        Assert.True((bool)page.GetType().GetField("PickUpOres")!.GetValue(page)!);
    }

    [Fact]
    public void OrePickupAndTheChunkCarryTakeNoMemory()
    {
        var content = Mining();
        using var rig = Rig(content);
        // Chunks the look skips every tick (beyond range) and one the drones carry.
        for (var i = 0; i < 40; i++) Place(rig, content, new Vector3(content.Drones.FetchRangeMetres + 50 + i, 0, 0));
        Place(rig, content, new Vector3(-120, 20, 40), mass: 600);
        Settings(rig, cargo: false, ores: true);
        TickUntil(rig, () => Drones(rig).Count == 1, 6, "a drone launched for the chunk");
        const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        var tickDrones = typeof(ServerHost).GetMethod("TickDrones", Private)!.CreateDelegate<Action>(rig.Server);
        var tickCarries = typeof(ServerHost).GetMethod("TickOreCarries", Private)!.CreateDelegate<Action>(rig.Server);
        var probe = new AllocationProbe();
        for (var i = 0; i < 120; i++)
        {
            while (probe.Measuring()) { tickDrones(); tickCarries(); }
            rig.Tick();
        }
        Assert.Equal(0, probe.Bytes);
    }
}
