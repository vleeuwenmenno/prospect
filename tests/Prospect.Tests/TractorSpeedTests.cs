using System.Numerics;
using Prospect.Engine.Time;
using Prospect.Game.World;
using Xunit;

namespace Prospect.Tests;

/// <summary>
/// Pickup at speed: a hull coasting at 240 m/s that jettisoned a container (so it rides along at the hull's velocity)
/// must take it back, and one that closes on a container ahead must grab and stow it. Ticks run in the server's order:
/// plan, hull step, pull, container step.
/// </summary>
public sealed class TractorSpeedTests
{
    private static readonly GameContent Content = GameContent.Load();
    private static DesignBox Box(string type, int x, int y, int z) => new(new(x, y, z), new(1, 1, 1), Content.Blocks.Get(type));

    /// <summary>The ridge hull of <see cref="TractorGuideTests"/>: a turret on the core's top face, a rack beside the core.</summary>
    private static HullDesign Ridge() => TestDesigns.Mounted(
        [Box("core", 0, 0, 0), Box("cargo", -1, 0, 0), Box("generator", 0, -1, 0), Box("battery", 0, -2, 0),
            Box("hull", 1, 0, 0), Box("hull", 1, 1, 0)],
        [new(0, BlockFacing.Up, 2, 4, Content.Blocks.Get("tractorBeam"))]);

    private static Vector3 Pivot(HullDesign design) => (design.CenterOfMass + new Vector3(.5f)) * design.BlockSize;

    private static readonly long Delay = (long)(10 * TickLoop.SimulationHz);

    /// <param name="Miss">Distance from the slot, in the hull's frame, where the body was the tick before it was stowed.</param>
    private readonly record struct Outcome(long Acquired, long Stowed, int Releases, float Miss, string Last);

    private static Outcome Run(Vector3 hullVelocity, Vector3 local, Vector3 bodyVelocity, long ready, long maxTicks)
    {
        var design = Ridge(); using var hulls = new HullStore();
        var origin = Content.Galaxy.CenterOf(new(0, 100, 0));
        var h = hulls.Create(1, 0, design, origin);
        hulls.State(h.Index).Velocity = hullVelocity;
        var field = new HullField(hulls, [design]); var bodies = new FreeContainers();
        const int size = 3;
        Assert.True(bodies.Add(Content, new FreeContainer
        {
            Position = origin + local - Pivot(design),
            Velocity = bodyVelocity,
            Orientation = Quaternion.Identity,
            Size = size,
            Resource = -1,
            Hull = Content.Cargo.Sizes[size].Hull,
            PickupReadyTick = ready,
        }, out _));
        var tractor = new TractorSimulation(); var scratch = new byte[8];
        var contact = new ContainerSimulation(Content.Galaxy); var rocks = new RockField(Content.Galaxy);
        Assert.True(TractorPath.TryGuide(Content.Cargo, design, default, 0, 1, size, default, new byte[8], out var guide));
        long acquired = -1; var releases = 0; var held = false; var miss = float.PositiveInfinity;
        for (long tick = 0; tick < maxTicks; tick++)
        {
            field.BeginRound(); tractor.Plan(bodies, Content, field, scratch, tick);
            field.Step(h.Index, default);
            tractor.Pull(bodies, Content, field, scratch, tick);
            contact.Step(bodies, Content, field, rocks, tick);
            if (bodies.Items.IsEmpty)
            {
                Assert.Single(hulls.BlockStates.Cargo(hulls.Info(h.Index).BlockState).ToArray());
                return new(acquired, tick, releases, miss, "stowed");
            }
            var now = bodies.Items[0].TractorHull != 0;
            if (now && acquired < 0) acquired = tick;
            if (held && !now) releases++;
            held = now;
            var body = bodies.Items[0]; var pose = hulls.State(h.Index);
            miss = Vector3.Distance(guide.Slot, HullRotation.Rotate(body.Position.RelativeTo(pose.Position), Quaternion.Conjugate(pose.Orientation)) + Pivot(design));
        }
        var item = bodies.Items[0]; var state = hulls.State(h.Index);
        var at = HullRotation.Rotate(item.Position.RelativeTo(state.Position), Quaternion.Conjugate(state.Orientation)) + Pivot(design);
        return new(acquired, -1, releases, miss, $"stage={item.TractorStage} idle={item.TractorIdle} held={item.TractorHull != 0} local={at} relative={item.Velocity - state.Velocity}");
    }

    public static TheoryData<float, float, float> Velocities() => new()
    {
        { 0, 0, 0 }, { 0, 0, -240 }, { 0, 0, 240 }, { 240, 0, 0 }, { -240, 0, 0 }, { 0, 240, 0 }, { 0, -240, 0 }, { 139, 139, -139 },
    };

    [Theory]
    [MemberData(nameof(Velocities))]
    public void AContainerJettisonedAtSpeedIsTakenBackOnceThePickupDelayEnds(float x, float y, float z)
    {
        var velocity = new Vector3(x, y, z);
        var outcome = Run(velocity, new Vector3(1.8f, 5, .5f), velocity, Delay, Delay + 30 * TickLoop.SimulationHz);
        Assert.True(outcome.Stowed >= 0, outcome.Last);
        Assert.Equal(Delay, outcome.Acquired);
        Assert.Equal(0, outcome.Releases);
        // Stowed where the slot is, not metres away: the pull measures the body and the hull at the same instant.
        Assert.True(outcome.Miss < .1f, $"stowed {outcome.Miss} m from its slot");
        Assert.True(outcome.Stowed - outcome.Acquired < 10 * TickLoop.SimulationHz, $"took {(outcome.Stowed - outcome.Acquired) / 60f} s");
    }

    [Theory]
    [InlineData(0, 1, 0)]
    [InlineData(0, 0, -1)]
    [InlineData(0, 0, 1)]
    public void AHullClosingAtTenMetresASecondOnAContainerAheadGrabsAndStowsIt(float x, float y, float z)
    {
        var ahead = new Vector3(x, y, z);
        // The container coasts at 240 m/s, 100 m ahead along the direction of travel (and a few metres above the
        // turret, so the beam has it in view); the hull follows at 250 m/s. Its pickup delay is long over.
        var outcome = Run(ahead * 250, new Vector3(.5f, 5, .5f) + ahead * 100, ahead * 240, 0, 30 * TickLoop.SimulationHz);
        Assert.True(outcome.Stowed >= 0, outcome.Last);
        // In range (40 m and a cell) after about 5.9 s of closing.
        Assert.InRange(outcome.Acquired, 5 * TickLoop.SimulationHz, 7 * TickLoop.SimulationHz);
        Assert.Equal(0, outcome.Releases);
        Assert.True(outcome.Miss < .1f, $"stowed {outcome.Miss} m from its slot");
    }
}
