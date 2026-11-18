using System.Numerics;
using Prospect.Game.World;
using Xunit;

namespace Prospect.Tests;

public sealed class FreeContainerEjectionTests
{
    [Fact]
    public void SplitNeverEjectsAndDamageEjectsExactlyOnceWithoutAllocation()
    {
        var content = WreckageTests.Content(cargo: true);
        var probe = new AllocationProbe();
        while (probe.Run())
        {
            using var rig = new SaveRig(SaveRig.NewWorld(content), content);
            rig.Join();
            var source = rig.Hulls.Info(0).BlockState;
            Assert.True(rig.Hulls.BlockStates.TryAddCargo(source, content, new(6, 0, 0, 100, 50, default), new byte[8]));
            var containers = rig.Get<FreeContainers>("_containers");
            rig.Hulls.BlockStates.Lose(source, 2);
            WreckageTests.Tick(rig.Server)();
            Assert.Empty(containers.Items.ToArray());
            var wreck = WreckageTests.Wreck(rig.Hulls);
            var target = rig.Hulls.Info(wreck).BlockState;
            Assert.Single(rig.Hulls.BlockStates.Cargo(target).ToArray());
            probe.Start();
            rig.Hulls.BlockStates.Hit(target, 6, float.MaxValue);
            probe.Stop();
            Assert.Single(containers.Items.ToArray());
            Assert.Equal(100, containers.Items[0].Amount);
            Assert.Equal(50, containers.Items[0].Hull);
            Assert.Empty(rig.Hulls.BlockStates.Cargo(target).ToArray());
            var id = containers.Items[0].Id;
            rig.Hulls.BlockStates.Hit(target, 6, float.MaxValue);
            Assert.Single(containers.Items.ToArray());
            Assert.Equal(id, containers.Items[0].Id);
        }
        Assert.Equal(0, probe.Bytes);
    }

    [Fact]
    public void ReleaseKeepsPayloadAndUsesDeterministicKickAndPointVelocity()
    {
        var content = WreckageTests.Content(cargo: true);
        var design = content.Pod;
        var held = new HoldContainer(6, 0, 0, 100, 31, default);
        var hull = HullState.Spawn(new(1e12, 0, 0), design.Flight);
        hull.Velocity = new(2, 3, 4); hull.AngularVelocity = new(0, 1, 0);
        var first = CargoEjection.Make(content.Cargo, design, held, hull, Vector3.Zero, 42, 17, true);
        var again = CargoEjection.Make(content.Cargo, design, held, hull, Vector3.Zero, 42, 17, true);
        Assert.Equal(first, again);
        Assert.Equal(642, first.PickupReadyTick);
        Assert.Equal(0u, first.ClaimOwner);
        var other = CargoEjection.Make(content.Cargo, design, held, hull, Vector3.Zero, 42, 18, true);
        Assert.NotEqual(first.Velocity, other.Velocity);
        Assert.Equal(100, first.Amount); Assert.Equal(31, first.Hull); Assert.Equal(42, first.Born);
        var probe = new AllocationProbe();
        while (probe.Measuring()) CargoEjection.Make(content.Cargo, design, held, hull, Vector3.Zero, 42, 17, true);
        Assert.Equal(0, probe.Bytes);
    }

    [Fact]
    public void AJettisonedContainerLeavesThroughTheOpenFaceInLineWithItsSlot()
    {
        var content = GameContent.Load();
        var design = content.Pod;
        var rack = Array.FindIndex(design.Types, t => t.Cargo > 0);
        Assert.Equal(BlockFacing.Up, design.Boxes[rack].Facing);
        var held = new HoldContainer(rack, 3, 0, 100, 400, new GridSize(2, 0, 2));
        var hull = HullState.Spawn(new(0, 0, 0), design.Flight);
        var free = CargoEjection.Make(content.Cargo, design, held, hull, Vector3.Zero, 42, 0, false);
        var unit = content.Cargo.UnitSize;
        var corner = design.BlockOffsets[rack] - design.BlockHalfExtents[rack];
        var top = design.BlockOffsets[rack].Y + design.BlockHalfExtents[rack].Y;
        // Above the opening, its bottom a tenth of a unit clear of it, straight over its slot; pushed out along +Y.
        var at = new Vector3((float)free.Position.X, (float)free.Position.Y, (float)free.Position.Z);
        Assert.Equal(corner.X + 3 * unit, at.X, 4);
        Assert.Equal(corner.Z + 3 * unit, at.Z, 4);
        Assert.Equal(top + unit + .1f * unit, at.Y, 4);
        Assert.Equal(content.Cargo.JettisonSpeed, free.Velocity.Y, 4);
        Assert.Equal(0, free.Velocity.X, 4);
        Assert.Equal(0, free.Velocity.Z, 4);
        Assert.True(CargoEjection.MayLeave(content.Cargo, design, design.FullSet, held));
    }

    [Fact]
    public void NoContainerLeavesIntoABlockOrThroughAClosedFace()
    {
        var content = GameContent.Load();
        var cargo = content.Blocks.Get("cargo");
        var core = new DesignBox(new(0, 0, 0), new(1, 1, 1), content.Blocks.Get("core"));
        var rack = new DesignBox(new(1, 0, 0), new(2, 1, 1), cargo, BlockShape.Box, BlockFacing.Up);
        // A plate over the rack's +X half: a container under it cannot leave, one under the open half can.
        var plate = new DesignBox(new(2, 1, 0), new(1, 1, 1), content.Blocks.Get("hull"));
        var design = TestDesigns.Of(core, rack, plate);
        var under = new HoldContainer(1, 3, -1, 0, 400, new GridSize(2, 0, 0));
        var open = new HoldContainer(1, 3, -1, 0, 400, new GridSize(0, 0, 0));
        Assert.False(CargoEjection.MayLeave(content.Cargo, design, design.FullSet, under));
        Assert.True(CargoEjection.MayLeave(content.Cargo, design, design.FullSet, open));
        // With the plate lost, both may.
        ulong[] alive = [0b011];
        Assert.True(CargoEjection.MayLeave(content.Cargo, design, alive, under));
        // A part mounted on the open face closes it; the rack's other faces never open.
        var mounted = TestDesigns.Mounted([core, rack], [new(1, BlockFacing.Up, 2, 2, content.Blocks.Get("tractorBeam"))]);
        Assert.Equal(-1, CargoEjection.OpenSide(mounted, 1));
        Assert.Equal((int)BlockFacing.Up, CargoEjection.OpenSide(TestDesigns.Of(core, rack), 1));
    }
}
