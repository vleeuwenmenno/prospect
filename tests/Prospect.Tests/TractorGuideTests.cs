using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using Prospect.Game.World;
using Xunit;

namespace Prospect.Tests;

/// <summary>
/// The guided pull: a staging point outside the rack's face, a slow glide in along the face normal, stowing at the slot,
/// the hull's own blocks not holding a guided container, and the time-out that lets a stuck one go.
/// </summary>
public sealed class TractorGuideTests
{
    private static readonly GameContent Content = GameContent.Load();
    private static DesignBox Box(string type, int x, int y, int z, BlockFacing facing = BlockFacing.Back) =>
        new(new(x, y, z), new(1, 1, 1), Content.Blocks.Get(type), BlockShape.Box, facing);

    /// <summary>
    /// The turret sits on the core's top face, 0.7 m along +X, hard by a two-block ridge on +X. A rack lies on the
    /// other side of the core. A one-metre container above the ridge reaches the beam, but the emitter's exit point
    /// (half a diagonal above the turret) puts its box inside the ridge: the old pull aimed there and jammed.
    /// </summary>
    private static HullDesign Ridge() => TestDesigns.Mounted(
        [Box("core", 0, 0, 0), Box("cargo", -1, 0, 0, BlockFacing.Up), Box("generator", 0, -1, 0), Box("battery", 0, -2, 0),
            Box("hull", 1, 0, 0), Box("hull", 1, 1, 0)],
        [new(0, BlockFacing.Up, 2, 4, Content.Blocks.Get("tractorBeam"))]);

    private static Vector3 Pivot(HullDesign design) => (design.CenterOfMass + new Vector3(.5f)) * design.BlockSize;

    private static FreeContainer Body(HullDesign design, Double3 origin, Vector3 local, int size = 3) => new()
    {
        Position = origin + local - Pivot(design),
        Orientation = Quaternion.Identity,
        Size = size,
        Resource = -1,
        Hull = Content.Cargo.Sizes[size].Hull
    };

    private static readonly Vector3 AboveTheRidge = new(1.8f, 5, .5f);

    [Fact]
    public void TheStagingPointLiesOutsideTheRacksFaceAndTheFinalLegGoesStraightInAlongItsNormal()
    {
        var design = Ridge();
        Assert.True(TractorPath.TryGuide(Content.Cargo, design, default, 0, 1, 3, default, new byte[8], out var guide));
        var half = FreeContainers.Half(Content.Cargo, 3);
        // The rack is the cell at -1 on X: its slot is the whole cell, and its open face is the top (nearest to the emitter).
        Assert.Equal(new Vector3(-.5f, .5f, .5f), guide.Slot);
        Assert.Equal(guide.Slot.X, guide.Staging.X);
        Assert.Equal(guide.Slot.Z, guide.Staging.Z);
        Assert.Equal(1 + half.Y + Content.Cargo.Tractor.StagingMarginMetres, guide.Staging.Y, 4);
        Assert.True(guide.Exit.Y > TractorReach.Emitter(design.Boxes, design.Mounts[0], 1).Y);
        Assert.Equal(half, guide.Half);
    }

    [Fact]
    public void ThePathGoesToTheSlotThePackingGivesNotToTheEmitter()
    {
        var design = Ridge();
        var held = new HoldContainer[] { new(1, 3, -1, 0, 400, new GridSize(0, 0, 0)) };
        // The rack holds a metre cube already: no room for a second.
        Assert.False(TractorPath.TryGuide(Content.Cargo, design, default, 0, 1, 3, held, new byte[8], out _));
        // A small one takes the first free cell after the large one, as TryAddCargo will place it.
        var small = new HoldContainer[] { new(1, 2, -1, 0, 200, new GridSize(0, 0, 0)) };
        Assert.True(TractorPath.TryGuide(Content.Cargo, design, default, 0, 1, 2, small, new byte[8], out var guide));
        Assert.Equal(new Vector3(-.5f, .5f, .75f), guide.Slot);
        Assert.Equal(guide.Slot.Z, guide.Staging.Z);
    }

    [Fact]
    public void ABodyAboveTheRidgeIsGuidedInAndStowedWhereThePullAimedAtTheEmitterAndJammed()
    {
        var design = Ridge(); using var hulls = new HullStore();
        var origin = Content.Galaxy.CenterOf(new(0, 100, 0));
        var h = hulls.Create(1, 0, design, origin);
        var field = new HullField(hulls, [design]); var bodies = new FreeContainers();
        Assert.True(bodies.Add(Content, Body(design, origin, AboveTheRidge), out _));
        var tractor = new TractorSimulation(); var scratch = new byte[8];
        var contact = new ContainerSimulation(Content.Galaxy); var rocks = new RockField(Content.Galaxy);
        var fastest = 0f; var glided = false;
        for (var tick = 0; tick < 1800 && !bodies.Items.IsEmpty; tick++)
        {
            field.BeginRound(); tractor.Plan(bodies, Content, field, scratch, tick);
            field.Step(h.Index, default);
            tractor.Pull(bodies, Content, field, scratch, tick);
            contact.Step(bodies, Content, field, rocks, tick);
            if (bodies.Items.IsEmpty) break;
            var item = bodies.Items[0];
            if (item.TractorStage == 1) { glided = true; fastest = Math.Max(fastest, item.Velocity.Length()); }
        }
        Assert.True(bodies.Items.IsEmpty, bodies.Items.IsEmpty ? "stowed" : $"stage={bodies.Items[0].TractorStage} idle={bodies.Items[0].TractorIdle} local={bodies.Items[0].Position.RelativeTo(origin) + Pivot(design)}");
        Assert.True(glided);
        Assert.True(fastest <= Content.Cargo.Tractor.FinalSpeed + .01f, $"final glide reached {fastest} m/s");
        var stowed = hulls.BlockStates.Cargo(hulls.Info(h.Index).BlockState).ToArray();
        Assert.Single(stowed);
        Assert.Equal(1, stowed[0].Rack);
        Assert.Equal(3, stowed[0].Size);
    }

    [Fact]
    public void ABodyStartingBesideTheRidgeStillStows()
    {
        var design = Ridge(); using var hulls = new HullStore();
        var origin = Content.Galaxy.CenterOf(new(0, 100, 0));
        var h = hulls.Create(1, 0, design, origin);
        var field = new HullField(hulls, [design]); var bodies = new FreeContainers();
        // Straight over the core, level with the ridge's top: the straight line to the staging point is clear.
        Assert.True(bodies.Add(Content, Body(design, origin, new Vector3(.5f, 4, .5f), 0), out _));
        var tractor = new TractorSimulation(); var scratch = new byte[8];
        var contact = new ContainerSimulation(Content.Galaxy); var rocks = new RockField(Content.Galaxy);
        for (var tick = 0; tick < 1800 && !bodies.Items.IsEmpty; tick++)
        {
            field.BeginRound(); tractor.Plan(bodies, Content, field, scratch, tick);
            field.Step(h.Index, default);
            tractor.Pull(bodies, Content, field, scratch, tick);
            contact.Step(bodies, Content, field, rocks, tick);
        }
        Assert.True(bodies.Items.IsEmpty);
        Assert.Single(hulls.BlockStates.Cargo(hulls.Info(h.Index).BlockState).ToArray());
    }

    [Fact]
    public void AGuidedBodyPassesItsOwnHullButStillHitsAnotherHull()
    {
        var design = Ridge(); using var hulls = new HullStore();
        var origin = Content.Galaxy.CenterOf(new(0, 100, 0));
        var own = hulls.Create(1, 0, design, origin);
        var other = hulls.Create(2, 0, design, origin + new Vector3(40, 0, 0));
        var field = new HullField(hulls, [design]);
        var rocks = new RockField(Content.Galaxy); var contact = new ContainerSimulation(Content.Galaxy);
        // Half-way into the core of whichever hull it overlaps.
        var inside = new Vector3(.9f, .5f, .5f);
        foreach (var (hull, claimed, powered, moves) in new[]
            { (own, own, true, false), (own, own, false, true), (other, own, true, true) })
        {
            var bodies = new FreeContainers();
            var body = Body(design, hulls.State(hull.Index).Position, inside);
            body.TractorHull = claimed.Packed; body.TractorPowered = powered; body.Velocity = new Vector3(-1, 0, 0);
            Assert.True(bodies.Add(Content, body, out _));
            var start = bodies.Items[0].Position;
            contact.Step(bodies, Content, field, rocks, 0);
            var pushed = bodies.Items[0].Position.RelativeTo(start + body.Velocity * HullSim.Dt).Length() > .01f;
            Assert.Equal(moves, pushed);
        }
    }

    [Fact]
    public void ABodyTheBeamCannotSeeIsReleasedAfterTheStallTimeAndWaitsBeforeAnyTractorTriesAgain()
    {
        var design = Ridge(); using var hulls = new HullStore();
        var origin = Content.Galaxy.CenterOf(new(0, 100, 0));
        var h = hulls.Create(1, 0, design, origin);
        var field = new HullField(hulls, [design]); var bodies = new FreeContainers();
        Assert.True(bodies.Add(Content, Body(design, origin, AboveTheRidge), out _));
        var tractor = new TractorSimulation(); var scratch = new byte[8];
        tractor.Plan(bodies, Content, field, scratch, 0);
        Assert.Equal(h.Packed, bodies.Items[0].TractorHull);
        // Knocked behind the hull: the core and the generator stand between it and the emitter.
        bodies.At(0).Position = origin + new Vector3(.5f, -4, .5f) - Pivot(design);
        var ticks = Content.Cargo.Tractor.StallTicks;
        for (var tick = 1; tick < ticks; tick++)
        {
            tractor.Plan(bodies, Content, field, scratch, tick);
            tractor.Pull(bodies, Content, field, scratch, tick);
            Assert.Equal(h.Packed, bodies.Items[0].TractorHull);
        }
        tractor.Pull(bodies, Content, field, scratch, ticks);
        Assert.Equal(0u, bodies.Items[0].TractorHull);
        var ready = ticks + (long)Math.Ceiling(Content.Cargo.Tractor.RetrySeconds * Prospect.Engine.Time.TickLoop.SimulationHz);
        Assert.Equal(ready, bodies.Items[0].PickupReadyTick);
        bodies.At(0).Position = origin + AboveTheRidge - Pivot(design);
        tractor.Plan(bodies, Content, field, scratch, ready - 1);
        Assert.Equal(0u, bodies.Items[0].TractorHull);
        tractor.Plan(bodies, Content, field, scratch, ready);
        Assert.Equal(h.Packed, bodies.Items[0].TractorHull);
    }

    [Fact]
    public void ABodyTheBeamLosesBehindTheHullIsPulledBackIntoViewInsteadOfDriftingUntilTheStall()
    {
        // A load the beam has lost used to be neither steered nor let go: it kept whatever speed it had until the stall
        // dropped it, 4 s later (a turned hull, or a body passing behind the hull's own blocks). The trace had it sitting
        // behind the core at 2.7 m/s with a frozen velocity for 240 ticks, then released and gone. The beam now pulls it
        // back to its exit point, the one place it always sees, and the ordinary guide resumes from there.
        var design = Ridge(); using var hulls = new HullStore();
        var origin = Content.Galaxy.CenterOf(new(0, 100, 0));
        var h = hulls.Create(1, 0, design, origin);
        var field = new HullField(hulls, [design]); var bodies = new FreeContainers();
        Assert.True(bodies.Add(Content, Body(design, origin, AboveTheRidge), out _));
        var tractor = new TractorSimulation(); var scratch = new byte[8];
        var contact = new ContainerSimulation(Content.Galaxy); var rocks = new RockField(Content.Galaxy);
        var pivot = Pivot(design); var mount = design.Mounts[0];
        var lostSight = false; var pulledWhileLost = false; var released = false; var stowed = -1;
        var wasHeld = false;
        for (var tick = 0; tick < 1800 && stowed < 0; tick++)
        {
            field.BeginRound();
            tractor.Plan(bodies, Content, field, scratch, tick);
            if (tick == 40)
            {
                // The hull comes about: the core, the generator and the battery now stand between the beam and the body.
                hulls.State(h.Index).Orientation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI);
            }
            field.Step(h.Index, default);
            tractor.Pull(bodies, Content, field, scratch, tick);
            contact.Step(bodies, Content, field, rocks, tick);
            if (bodies.Items.IsEmpty) { stowed = tick; break; }
            var item = bodies.Items[0];
            released |= wasHeld && item.TractorHull == 0;
            wasHeld = item.TractorHull == h.Packed;
            if (!wasHeld) continue;
            var state = hulls.State(h.Index);
            var local = HullRotation.Rotate(item.Position.RelativeTo(state.Position), Quaternion.Conjugate(state.Orientation)) + pivot;
            var sight = TractorReach.Container(design.Boxes, hulls.Blocks(h.Index), design.BlockSize, mount, local);
            if (!sight)
            {
                lostSight = true;
                // The beam keeps pulling -- and so keeps its hold on the body -- even though the emitter cannot see it.
                if (tick > 40) pulledWhileLost |= item.TractorPowered;
            }
        }
        Assert.True(lostSight, "the turn put the hull's own blocks in the beam's way");
        Assert.False(released, "the beam keeps its load while it brings it back, instead of dropping it at the stall");
        Assert.True(pulledWhileLost, "a body out of sight is steered back, not left drifting");
        Assert.InRange(stowed, 0, 900);
        Assert.Single(hulls.BlockStates.Cargo(hulls.Info(h.Index).BlockState).ToArray());
    }

    [Fact]
    public void ABodyPushedBackAndForthOnOneLegStillReachesTheStall()
    {
        // A body pressed against something the beam cannot pull it through (a rock: contacts with rocks still count
        // for a held body) is pushed back and pulled in again, tick after tick, without ever coming closer. Its path
        // left then grows and shrinks on the same leg. Only a change of leg may raise the least path left, so the
        // beam must still let it go at the stall; raising it whenever the path grew counted every pull in as a gain,
        // and the body held the beam for ever.
        var design = Ridge(); using var hulls = new HullStore();
        var origin = Content.Galaxy.CenterOf(new(0, 100, 0));
        var h = hulls.Create(1, 0, design, origin);
        var field = new HullField(hulls, [design]); var bodies = new FreeContainers();
        Assert.True(bodies.Add(Content, Body(design, origin, AboveTheRidge), out _));
        var tractor = new TractorSimulation(); var scratch = new byte[8];
        field.BeginRound();
        tractor.Plan(bodies, Content, field, scratch, 0);
        Assert.Equal(h.Packed, bodies.Items[0].TractorHull);
        var near = origin + AboveTheRidge - Pivot(design);
        var far = near + new Vector3(0, .2f, 0);
        var released = -1;
        var ticks = Content.Cargo.Tractor.StallTicks;
        for (var tick = 1; tick < 3 * ticks && released < 0; tick++)
        {
            field.BeginRound();
            tractor.Plan(bodies, Content, field, scratch, tick);
            field.Step(h.Index, default);
            // Pushed out 20 cm, then pulled back to where it was: the same two places, over and over.
            bodies.At(0).Position = (tick & 1) == 0 ? near : far;
            bodies.At(0).Velocity = Vector3.Zero;
            tractor.Pull(bodies, Content, field, scratch, tick);
            if (bodies.Items[0].TractorHull == 0) released = tick;
        }
        Assert.InRange(released, ticks / 2, 2 * ticks);
    }

    [Fact]
    public void ASlowGlideAlwaysShowsProgress()
    {
        // The least shrink that counts as progress must never exceed what the approach rule closes in a tick, or a
        // body creeping in at the final glide's 0.6 m/s would need two ticks to show any gain at all; the count of ticks
        // without one must never reach the stall. A fast approach still needs the whole progressMetres to count.
        var tuning = Content.Cargo.Tractor;
        foreach (var speed in new[] { tuning.FinalSpeed, 1.2f, 3, 10 })
            Assert.True(Approach.NeededProgress(speed, tuning.ProgressMetres) <= speed * HullSim.Dt,
                $"{speed} m/s closes more than the shrink it must show");
        Assert.Equal(tuning.ProgressMetres, Approach.NeededProgress(20, tuning.ProgressMetres));
        Assert.Equal(0f, Approach.NeededProgress(0, tuning.ProgressMetres));
    }

    [Fact]
    public void TheApproachSpeedKeepsBrakingRoomInHand()
    {
        // The speed used to be exactly the speed that stops on the goal, so a single tick in which the pull was skipped
        // (no power, no sight) left the body too fast to stop: it passed the staging point, then the hull, and was lost.
        // The rule now closes at a share of that speed and has room to spare.
        var share = Content.Cargo.Tractor.ApproachBrakeShare;
        Assert.InRange(share, 0.05f, 0.95f);
        const float acceleration = 4;
        var ticks = 6 * HullSim.Dt;
        foreach (var distance in new[] { 2f, 5f, 20f, 60f })
        {
            var speed = Approach.Speed(acceleration, distance, share);
            Assert.InRange(speed, 0, MathF.Sqrt(2 * acceleration * distance));
            // Still stoppable with the full acceleration in the room left, after six ticks without the pull.
            Assert.True(Approach.StopsIn(acceleration, speed, distance - speed * ticks, 1f),
                $"{distance} m: the speed it closes at survives six ticks without the pull");
            var exact = MathF.Sqrt(2 * acceleration * distance);
            Assert.False(Approach.StopsIn(acceleration, exact, distance - exact * HullSim.Dt, 1f),
                $"{distance} m: the exact stopping speed cannot survive even one");
        }
    }

    [Theory]
    [InlineData("stagingMarginMetres", 0)]
    [InlineData("toleranceMetres", 0)]
    [InlineData("finalSpeed", 0)]
    [InlineData("settleSpeed", 5)]
    [InlineData("alignRate", 0)]
    [InlineData("progressMetres", -1)]
    [InlineData("stallSeconds", 0)]
    [InlineData("retrySeconds", -1)]
    [InlineData("approachBrakeShare", 0)]
    [InlineData("approachBrakeShare", 1.5)]
    public void InvalidTractorGuidanceIsRefused(string name, float value)
    {
        using var copy = new ContentCopy();
        var json = JsonNode.Parse(copy.Read("blocks/cargo.json"))!;
        json["tractor"]![name] = value;
        Assert.Throws<InvalidDataException>(() => CargoCatalog.Parse(Encoding.UTF8.GetBytes(json.ToJsonString())));
    }

    [Fact]
    public void MissingTractorGuidanceIsRefused()
    {
        using var copy = new ContentCopy();
        var json = JsonNode.Parse(copy.Read("blocks/cargo.json"))!;
        json.AsObject().Remove("tractor");
        Assert.ThrowsAny<Exception>(() => CargoCatalog.Parse(Encoding.UTF8.GetBytes(json.ToJsonString())));
    }
}
