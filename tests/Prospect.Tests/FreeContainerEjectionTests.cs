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
}
