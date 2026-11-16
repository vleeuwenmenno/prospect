using System.Numerics;
using Prospect.Game.World;
using Xunit;

namespace Prospect.Tests;

public sealed class TractorSimulationTests
{
    private static readonly GameContent Content = GameContent.Load();
    private static DesignBox Box(string type, int x, int y, int z) => new(new(x, y, z), new(1, 1, 1), Content.Blocks.Get(type));
    private static HullDesign Design() => TestDesigns.Mounted(
        [Box("core", 0, 0, 0), Box("cargo", 2, 1, 0), Box("generator", 0, -1, 0), Box("battery", 0, -2, 0)],
        [new(0, BlockFacing.Up, 2, 2, Content.Blocks.Get("tractorBeam"))]);
    private static FreeContainer Item(HullDesign design, Double3 origin) => new()
    {
        Position = origin + new Vector3(.5f, 7, .5f) - (design.CenterOfMass + new Vector3(.5f)) * design.BlockSize,
        Orientation = Quaternion.Identity,
        Size = 0,
        Resource = -1,
        Hull = 50
    };

    [Theory]
    [InlineData(0u)]
    [InlineData(2u)]
    public void DelayBlocksEveryOwnerThenEligibleOwnerPulls(uint owner)
    {
        var design = Design(); using var hulls = new HullStore();
        var first = hulls.Create(1, 0, design, Double3.Zero);
        var second = hulls.Create(2, 0, design, Double3.Zero);
        var field = new HullField(hulls, [design]);
        var bodies = new FreeContainers(); var item = Item(design, Double3.Zero);
        item.ClaimOwner = owner; item.PickupReadyTick = 600;
        Assert.True(bodies.Add(Content, item, out _));
        var tractor = new TractorSimulation(); var scratch = new byte[8];
        for (var tick = 0; tick < 600; tick++)
        {
            tractor.Plan(bodies, Content, field, scratch, tick);
            Assert.Equal(0u, bodies.Items[0].TractorHull);
            Assert.Equal(0, hulls.Info(first.Index).TractorDemand);
            Assert.Equal(0, hulls.Info(second.Index).TractorDemand);
        }
        tractor.Plan(bodies, Content, field, scratch, 600);
        var receiver = owner == 0 ? first : second;
        Assert.Equal(receiver.Packed, bodies.Items[0].TractorHull);
        hulls.Info(receiver.Index).Power.Tractor = hulls.Info(receiver.Index).TractorDemand;
        tractor.Pull(bodies, Content, field, scratch);
        Assert.True(bodies.Items[0].TractorPowered);
        Assert.NotEqual(Vector3.Zero, bodies.Items[0].Velocity);
    }

    [Fact]
    public void TwoMountsReserveDifferentBodiesInParallel()
    {
        var basis = Design();
        var design = TestDesigns.Mounted(basis.Boxes.ToArray(),
            [basis.Mounts[0], new(0, BlockFacing.Right, 2, 2, Content.Blocks.Get("tractorBeam"))]);
        using var hulls = new HullStore();
        var h = hulls.Create(1, 0, design, Double3.Zero);
        var field = new HullField(hulls, [design]);
        var bodies = new FreeContainers();
        Assert.True(bodies.Add(Content, Item(design, Double3.Zero), out _));
        var other = Item(design, Double3.Zero);
        other.Position = Double3.Zero + new Vector3(5, .5f, .5f) - (design.CenterOfMass + new Vector3(.5f));
        Assert.True(bodies.Add(Content, other, out _));
        var tractor = new TractorSimulation();
        tractor.Plan(bodies, Content, field, new byte[8]);
        Assert.Equal(h.Packed, bodies.Items[0].TractorHull);
        Assert.Equal(h.Packed, bodies.Items[1].TractorHull);
        Assert.NotEqual(bodies.Items[0].TractorMount, bodies.Items[1].TractorMount);
        Assert.True(hulls.Info(h.Index).TractorDemand > 4);
    }

    [Fact]
    public void AFullAcquisitionBudgetResumesAtTheNextMountInsteadOfStarvingIt()
    {
        var basis = Design();
        var blocked = TestDesigns.Mounted([.. basis.Boxes, Box("hull", 1, 1, 0)], basis.Mounts.ToArray());
        using var hulls = new HullStore();
        for (var i = 0; i < 4; i++) hulls.Create((uint)(i + 1), 0, blocked, Double3.Zero);
        var receiver = hulls.Create(5, 1, basis, new(0, 0, 4));
        var field = new HullField(hulls, [blocked, basis]);
        var bodies = new FreeContainers();
        for (var i = 0; i < 64; i++) Assert.True(bodies.Add(Content, Item(basis, Double3.Zero), out _));
        var tractor = new TractorSimulation(); var scratch = new byte[8];
        tractor.Plan(bodies, Content, field, scratch);
        Assert.Equal(0u, bodies.Items[0].TractorHull);
        tractor.Plan(bodies, Content, field, scratch);
        Assert.Equal(receiver.Packed, bodies.Items[0].TractorHull);
    }

    [Fact]
    public void PowerIsBilledOnceAndInsufficientPowerSuppliesNoTractor()
    {
        var flight = Content.Pod.Flight; flight.PowerGenerated = 0; flight.IdlePower = 0; flight.ShieldRecharge = 0;
        flight.TractorPower = 3;
        var state = HullState.Spawn(Double3.Zero, flight); state.Energy = 10;
        FlightModel.Step(ref state, default, flight, HullSim.Dt, out var flow);
        Assert.Equal(3, flow.Tractor);
        Assert.Equal(10 - 3 * HullSim.Dt, state.Energy, 5);
        Assert.Equal(-3, flow.Net);
        state.Energy = .001f;
        FlightModel.Step(ref state, default, flight, HullSim.Dt, out flow);
        Assert.Equal(0, flow.Tractor);
        Assert.Equal(.001f, state.Energy);
    }

    [Fact]
    public void PullMovesAFreeBodyThroughTheMountThenStowsAndTakesNoTickMemory()
    {
        var design = Design(); using var hulls = new HullStore();
        var origin = Content.Galaxy.CenterOf(new(0, 100, 0));
        var h = hulls.Create(1, 0, design, origin);
        var field = new HullField(hulls, [design]);
        var bodies = new FreeContainers(); var delayed = Item(design, origin);
        delayed.ClaimOwner = 1; delayed.PickupReadyTick = 600;
        Assert.True(bodies.Add(Content, delayed, out _));
        var tractor = new TractorSimulation(); var scratch = new byte[8];
        var contact = new ContainerSimulation(Content.Galaxy); var rocks = new RockField(Content.Galaxy);
        var probe = new AllocationProbe(); var moved = false; var slid = false; var billed = false;
        var start = bodies.Items[0].Position;
        for (var tick = 0; tick < 1800 && !bodies.Items.IsEmpty; tick++)
        {
            field.BeginRound(); tractor.Plan(bodies, Content, field, scratch, tick);
            field.Step(h.Index, default);
            tractor.Pull(bodies, Content, field, scratch);
            contact.Step(bodies, Content, field, rocks, tick);
            billed |= hulls.Info(h.Index).Power.Tractor > 0;
            if (!bodies.Items.IsEmpty)
            {
                moved |= bodies.Items[0].Position != start;
                slid |= bodies.Items[0].TractorStage == 1;
            }
        }
        Assert.True(moved); Assert.True(slid); Assert.True(billed);
        Assert.True(bodies.Items.IsEmpty, bodies.Items.IsEmpty ? "stowed" : $"stage={bodies.Items[0].TractorStage} claim={bodies.Items[0].TractorHull} local={bodies.Items[0].Position.RelativeTo(hulls.State(h.Index).Position) + design.CenterOfMass} velocity={bodies.Items[0].Velocity} power={hulls.Info(h.Index).Power.Tractor}");
        Assert.Single(hulls.BlockStates.Cargo(hulls.Info(h.Index).BlockState).ToArray());
        Assert.True(bodies.Add(Content, Item(design, origin), out _));
        tractor.Plan(bodies, Content, field, scratch);
        for (var i = 0; i < 100; i++)
            while (probe.Measuring()) { tractor.Plan(bodies, Content, field, scratch); tractor.Pull(bodies, Content, field, scratch); }
        Assert.Equal(0, probe.Bytes);
    }

    [Fact]
    public void ExistingClaimWinsAgainstAnotherHullAndNoPaidPowerMeansNoPull()
    {
        var design = Design(); using var hulls = new HullStore();
        var first = hulls.Create(1, 0, design, Double3.Zero);
        var field = new HullField(hulls, [design]); var bodies = new FreeContainers();
        Assert.True(bodies.Add(Content, Item(design, Double3.Zero), out _));
        var tractor = new TractorSimulation(); var scratch = new byte[8];
        tractor.Plan(bodies, Content, field, scratch);
        Assert.Equal(first.Packed, bodies.Items[0].TractorHull);
        hulls.Create(2, 0, design, new(0, 0, 4));
        var before = bodies.Items[0];
        tractor.Plan(bodies, Content, field, scratch);
        tractor.Pull(bodies, Content, field, scratch);
        Assert.Equal(first.Packed, bodies.Items[0].TractorHull);
        Assert.Equal(before.Position, bodies.Items[0].Position);
        Assert.Equal(before.Velocity, bodies.Items[0].Velocity);
        Assert.False(bodies.Items[0].TractorPowered);
        hulls.Destroy(first);
        tractor.Plan(bodies, Content, field, scratch);
        Assert.NotEqual(0u, bodies.Items[0].TractorHull);
        Assert.NotEqual(first.Packed, bodies.Items[0].TractorHull);
    }
}
