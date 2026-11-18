using System.Numerics;
using Prospect.Client.Render;
using Prospect.Client.World;
using Prospect.Game.World;
using Xunit;

namespace Prospect.Tests;

/// <summary>How containers are seen going into, moving in and leaving the own racks (<see cref="CargoMotion"/>).</summary>
public sealed class CargoMotionTests
{
    private static readonly GameContent Content = GameContent.Load();
    private static readonly CargoMotionLook Look = PropulsionLook.Load().Rack!.Motion!;
    private static readonly HullDesign Design = Content.Pod;
    private static readonly int Rack = Array.FindIndex(Design.Types, t => t.Cargo > 0);
    private const uint Own = 7;

    private static HoldContainer Medium(int x, int z, double kg = 100) => new(Rack, 3, 0, kg, 400, new GridSize(x, 0, z));

    private static FreeContainer Body(uint id, double kg, byte stage, uint beam = Own) =>
        new() { Id = id, Size = 3, Resource = 0, Amount = kg, Hull = 400, TractorHull = beam, TractorStage = stage, Orientation = Quaternion.Identity };

    private static void Frame(CargoMotion motion, double now, long tick, HoldContainer[] hold, params (FreeContainer Item, Vector3 At)[] free)
    {
        motion.BeginFree(now, Own);
        foreach (var (item, at) in free) motion.SeeFree(item, at, Quaternion.Identity);
        motion.EndFree();
        motion.Hold(Own, tick, Design, Content.Cargo, hold);
    }

    private static CargoMotion Started(HoldContainer[] hold)
    {
        var motion = new CargoMotion(Look);
        Frame(motion, 0, 0, []);
        Frame(motion, .1, 5, hold);
        return motion;
    }

    [Fact]
    public void TheFirstHoldIsShownAsItIsAndAnotherHullsToo()
    {
        var motion = Started([Medium(0, 0), Medium(2, 0, 200)]);
        Assert.Equal(CargoStage.Stored, motion.Stage(0));
        Assert.Equal(CargoStage.Stored, motion.Stage(1));
        motion.Pose(0, Design, Content.Cargo, out var offset, out _, out _);
        Assert.Equal(CargoSlots.Centre(Design, Content.Cargo, Medium(0, 0)), offset);
        // Taking control of another hull: what it holds is never animated either, nor its first hold.
        motion.BeginFree(.2, 9); motion.EndFree();
        motion.Hold(9, 5, Design, Content.Cargo, []);
        motion.BeginFree(.3, 9); motion.EndFree();
        motion.Hold(9, 8, Design, Content.Cargo, [Medium(0, 0)]);
        Assert.Equal(CargoStage.Stored, motion.Stage(0));
        Assert.Equal(0, motion.LeavingCount);
    }

    [Fact]
    public void AContainerTheBeamLedInTakesOverFromWhereItWasDrawnSlidesToItsSlotAndLocks()
    {
        var motion = Started([Medium(0, 0)]);
        var glide = new Vector3(-1.5f, 1.2f, .5f);
        Frame(motion, .2, 5, [Medium(0, 0)], (Body(11, 300, 1), glide));
        Assert.Equal(CargoStage.Transferring, CargoMotion.StageOf(Body(11, 300, 1), Own));
        Assert.Equal(CargoStage.Approaching, CargoMotion.StageOf(Body(11, 300, 0), Own));
        Assert.Equal(CargoStage.Loose, CargoMotion.StageOf(Body(11, 300, 0, 3), Own));
        // The server stows it: the hold has it, and the body is still in the set this frame.
        HoldContainer[] after = [Medium(0, 0), Medium(2, 0, 300)];
        Frame(motion, .3, 6, after, (Body(11, 300, 1), glide));
        Assert.True(motion.Hides(11));
        Assert.Equal(CargoStage.Stored, motion.Stage(0));
        motion.Pose(1, Design, Content.Cargo, out var start, out _, out var moving);
        Assert.Equal(glide, start);
        Assert.NotEqual(CargoStage.Stored, motion.Stage(1));
        // Half way it is between; at the end it is in its slot with the locks flashing; then it rests.
        Frame(motion, 1, 6, after);
        var stage = motion.Pose(1, Design, Content.Cargo, out var between, out _, out _);
        Assert.True(stage is CargoStage.Positioning or CargoStage.Transferring);
        var slot = CargoSlots.Centre(Design, Content.Cargo, after[1]);
        Assert.True(Vector3.Distance(between, slot) < Vector3.Distance(glide, slot));
        Frame(motion, .3 + Look.MaxSeconds + .01, 6, after);
        Assert.Equal(CargoStage.Locked, motion.Pose(1, Design, Content.Cargo, out var locked, out _, out var flash));
        Assert.Equal(slot, locked);
        Assert.NotEqual(moving, flash);
        Frame(motion, .3 + Look.MaxSeconds + Look.LockSeconds + .02, 6, after);
        Assert.Equal(CargoStage.Stored, motion.Pose(1, Design, Content.Cargo, out _, out _, out var rest));
        Assert.Equal(CargoMotionLook.Light(Look.StoredLight, Look.StoredLevel), rest);
        Assert.False(motion.Hides(11));
    }

    [Fact]
    public void AContainerWithNoBeamBehindItComesInThroughTheOpening()
    {
        var motion = Started([]);
        Frame(motion, .5, 6, [Medium(0, 2)]);
        Assert.Equal(CargoStage.Transferring, motion.Pose(0, Design, Content.Cargo, out var start, out _, out _));
        var top = Design.BlockOffsets[Rack].Y + Design.BlockHalfExtents[Rack].Y;
        Assert.True(start.Y > top, "it starts outside the open face");
        // Through the opening, then inside to the slot, then the locks: in that order, each seen.
        var seen = new List<CargoStage>();
        for (var t = .5; t < .5 + Look.MaxSeconds + Look.LockSeconds + .1; t += .02)
        {
            Frame(motion, t, 6, [Medium(0, 2)]);
            var stage = motion.Stage(0);
            if (seen.Count == 0 || seen[^1] != stage) seen.Add(stage);
        }
        Assert.Equal([CargoStage.Transferring, CargoStage.Positioning, CargoStage.Locked, CargoStage.Stored], seen);
    }

    [Fact]
    public void ARepackSlidesEachContainerFromItsOldSlotToItsNew()
    {
        var motion = Started([Medium(0, 0), Medium(2, 0, 200)]);
        // Same containers, other places (as a repack after a larger one came in would leave them).
        HoldContainer[] repacked = [Medium(0, 2), Medium(2, 2, 200)];
        Frame(motion, 1, 6, repacked);
        Assert.Equal(CargoStage.Positioning, motion.Pose(0, Design, Content.Cargo, out var from, out _, out _));
        Assert.Equal(CargoSlots.Centre(Design, Content.Cargo, Medium(0, 0)), from);
        motion.Pose(1, Design, Content.Cargo, out var other, out _, out _);
        Assert.Equal(CargoSlots.Centre(Design, Content.Cargo, Medium(2, 0, 200)), other);
        Assert.Equal(0, motion.LeavingCount);
    }

    [Fact]
    public void AReleasedContainerSlidesOutToTheLooseBodyItBecameWithoutTwoOfThemShowing()
    {
        var motion = Started([Medium(0, 0), Medium(2, 0, 200)]);
        var exit = new Vector3(-2, 1.6f, .5f);
        // The body is in the set before the hold drops the record: it is held back so it is not seen twice.
        Frame(motion, 1, 5, [Medium(0, 0), Medium(2, 0, 200)], (Body(21, 200, 0, 0), exit));
        Assert.True(motion.Hides(21));
        Frame(motion, 1.05, 6, [Medium(0, 0)], (Body(21, 200, 0, 0), exit));
        Assert.Equal(1, motion.LeavingCount);
        Assert.Equal(CargoStage.Unlocking, motion.Leave(0, out var record, out var from, out _, out var free, out var slide, out _));
        Assert.Equal(200, record.Amount);
        Assert.Equal(CargoSlots.Centre(Design, Content.Cargo, Medium(2, 0, 200)), from);
        Assert.Equal(21u, free);
        Assert.Equal(0, slide);
        Assert.True(motion.Hides(21));
        Frame(motion, 1.05 + Look.UnlockSeconds + Look.LeaveSeconds * .5, 6, [Medium(0, 0)], (Body(21, 200, 0, 0), exit));
        Assert.Equal(CargoStage.Transferring, motion.Leave(0, out _, out _, out _, out _, out slide, out _));
        Assert.InRange(slide, .01f, .99f);
        Frame(motion, 1.05 + Look.UnlockSeconds + Look.LeaveSeconds + .01, 6, [Medium(0, 0)], (Body(21, 200, 0, 0), exit));
        Assert.Equal(0, motion.LeavingCount);
        Assert.False(motion.Hides(21));
    }

    [Fact]
    public void AReleaseWhoseBodyComesLaterWaitsForItAndOneThatNeverComesIsDropped()
    {
        var motion = Started([Medium(0, 0)]);
        Frame(motion, 1, 6, []);
        Assert.Equal(1, motion.LeavingCount);
        Assert.Equal(0u, motion.Leave(0, out _, out _, out _, out var free, out _, out _) == CargoStage.Unlocking ? free : 1u);
        Frame(motion, 1.1, 6, [], (Body(31, 100, 0, 0), new Vector3(0, 2, 0)));
        motion.Leave(0, out _, out _, out _, out free, out _, out _);
        Assert.Equal(31u, free);

        var lonely = Started([Medium(0, 0)]);
        Frame(lonely, 1, 6, []);
        Frame(lonely, 1 + Look.MatchSeconds + .01, 6, []);
        Assert.Equal(0, lonely.LeavingCount);
    }

    [Fact]
    public void FollowingTheHoldAndTheBodiesTakesNoMemory()
    {
        var motion = Started([Medium(0, 0), Medium(2, 0, 200)]);
        HoldContainer[] a = [Medium(0, 0), Medium(2, 0, 200)];
        HoldContainer[] b = [Medium(0, 2), Medium(2, 2, 200), Medium(0, 0, 300)];
        (FreeContainer, Vector3)[] free = [(Body(41, 300, 1), new Vector3(0, 2, 0)), (Body(42, 10, 0, 0), Vector3.Zero)];
        var probe = new AllocationProbe();
        var now = 1.0;
        var tick = 6L;
        for (var i = 0; i < 40; i++)
            while (probe.Measuring())
            {
                now += .05;
                Frame(motion, now, ++tick, (tick & 1) == 0 ? a : b, free);
                for (var k = 0; k < motion.Records.Length; k++) motion.Pose(k, Design, Content.Cargo, out _, out _, out _);
                motion.Hides(41);
            }
        Assert.Equal(0, probe.Bytes);
    }
}
