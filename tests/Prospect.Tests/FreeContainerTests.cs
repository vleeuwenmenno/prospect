using System.Numerics;
using Prospect.Game.World;
using Xunit;

namespace Prospect.Tests;

public sealed class FreeContainerTests
{
    private static readonly GameContent Content = GameContent.Load();
    private static FreeContainer Container(long born = 0) => new()
    {
        Position = Double3.Zero,
        Orientation = Quaternion.Identity,
        Size = 0,
        Resource = -1,
        Hull = 50,
        Born = born
    };

    [Fact]
    public void GlobalCapAndCrossingSectorCapKeepNewestIdentities()
    {
        var store = new FreeContainers();
        for (var i = 0; i <= FreeContainers.Capacity; i++)
        {
            var item = Container(i); item.Position = Content.Galaxy.CenterOf(new(i, 100, 0));
            Assert.True(store.Add(Content, item, out _));
        }
        Assert.Equal(FreeContainers.Capacity, store.Items.Length);
        Assert.Equal(-1, store.Find(1));
        var id = store.Items[^1].Id;
        for (var i = 0; i < store.Items.Length; i++) store.At(i).Position = Double3.Zero;
        store.EnforceCaps(Content);
        Assert.Equal(Content.Cargo.SpacePerSector, store.Items.Length);
        Assert.True(store.Find(id) >= 0);
    }

    [Fact]
    public void FirstContactTickWithAsteroidsDisabledUsesOnlyReservedMemory()
    {
        var content = Content.WithSettings(Content.SettingCatalog.Build(null, ["asteroidAmount=0"]));
        // World generation is already warm in a running world; the container's own scratch is still fresh.
        var warm = new SectorContent();
        content.Galaxy.Reserve(warm);
        content.Galaxy.Generate(default, warm);
        var probe = new AllocationProbe();
        while (probe.Run())
        {
            using var hulls = new HullStore();
            var field = new HullField(hulls, content.Designs);
            var rocks = new RockField(content.Galaxy);
            var simulation = new ContainerSimulation(content.Galaxy);
            var store = new FreeContainers();
            Assert.True(store.Add(content, Container(), out _));
            probe.Start();
            simulation.Step(store, content, field, rocks, 1);
            probe.Stop();
        }
        Assert.Equal(0, probe.Bytes);
    }

    [Fact]
    public void ProductionContactPassTransfersMomentumAndRemovesDestroyedContainer()
    {
        using var hulls = new HullStore();
        var design = Content.Pod;
        var designIndex = Array.IndexOf(Content.Designs, design);
        var origin = Content.Galaxy.CenterOf(new(0, 100, 0));
        var hull = hulls.Create(1, (byte)designIndex, design, origin);
        hulls.State(hull.Index).Shield = 0;
        var field = new HullField(hulls, Content.Designs);
        var rocks = new RockField(Content.Galaxy);
        var simulation = new ContainerSimulation(Content.Galaxy);
        var block = 0;
        for (var i = 1; i < design.BlockCount; i++)
            if (design.BlockOffsets[i].X + design.BlockHalfExtents[i].X > design.BlockOffsets[block].X + design.BlockHalfExtents[block].X) block = i;
        var item = Container(); item.Hull = .001f; item.Velocity = new(-1000, 0, 0);
        item.Position = origin + design.BlockOffsets[block] + new Vector3(design.BlockHalfExtents[block].X
            + FreeContainers.Half(Content.Cargo, 0).X - .05f + 1000 * HullSim.Dt, 0, 0);
        var store = new FreeContainers(); Assert.True(store.Add(Content, item, out _));
        simulation.Step(store, Content, field, rocks, 1);
        Assert.True(store.Items.IsEmpty);
        Assert.True(hulls.State(hull.Index).Velocity.X < 0);
    }

    [Fact]
    public void SectorCapEvictsOldestWithoutReusingIdentity()
    {
        var store = new FreeContainers();
        Assert.True(store.Add(Content, Container(), out var first));
        for (var i = 1; i <= Content.Cargo.SpacePerSector; i++)
            Assert.True(store.Add(Content, Container(i), out _));
        Assert.Equal(Content.Cargo.SpacePerSector, store.Items.Length);
        Assert.Equal(-1, store.Find(first));
        Assert.Equal(2u, store.Items[0].Id);
        var invalid = Container(); invalid.Amount = double.NaN;
        Assert.False(store.Add(Content, invalid, out _));
        Assert.Equal(2u, store.Items[0].Id);
    }

    [Fact]
    public void DriftAndDestructionPreserveOtherIdentitiesWithoutAllocation()
    {
        var store = new FreeContainers();
        var c = Container(); c.Velocity = new(60, 0, 0); c.AngularVelocity = new(0, 1, 0);
        Assert.True(store.Add(Content, c, out var first));
        Assert.True(store.Add(Content, Container(), out var second));
        store.Drift();
        Assert.Equal(60 * HullSim.Dt, store.Items[0].Position.X, 5);
        Assert.NotEqual(Quaternion.Identity, store.Items[0].Orientation);
        var probe = new AllocationProbe();
        while (probe.Measuring()) store.Drift();
        Assert.Equal(0, probe.Bytes);
        Assert.True(store.Damage(first, 50));
        Assert.False(store.Damage(first, 50));
        Assert.Equal(second, store.Items[0].Id);
        Assert.False(store.Damage(second, float.NaN));
        Assert.Equal(50, store.Items[0].Hull);
    }

    [Fact]
    public void SharedBoxContactBouncesDamagesAndDestroysShell()
    {
        var c = Container(); c.Position = new(.6, 0, 0); c.Velocity = new(-20, 0, 0);
        Assert.True(FreeContainers.BoxContact(ref c, Content.Cargo, Double3.Zero, Quaternion.Identity,
            new(.5f), BlockShape.Box, Vector3.Zero, 1, 1, out var normal));
        Assert.True(normal.X > 0);
        Assert.True(c.Velocity.X > 0);
        Assert.True(c.Hull < 50);
        c.Position = new(.6, 0, 0); c.Velocity = new(-1000, 0, 0);
        Assert.True(FreeContainers.BoxContact(ref c, Content.Cargo, Double3.Zero, Quaternion.Identity,
            new(.5f), BlockShape.Box, Vector3.Zero, 1, 1, out _));
        Assert.Equal(0, c.Hull);
    }

    [Fact]
    public void SharedRockContactDamagesWithoutAllocating()
    {
        var rock = new Asteroid { Radius = 10, Rotation = Quaternion.Identity, Shape = 0 };
        var c = Container(); c.Velocity = new(0, -20, 0);
        Assert.True(FreeContainers.RockContact(ref c, Content.Cargo, rock, Double3.Zero, Vector3.Zero, 1, 1));
        Assert.True(c.Hull < 50);
        var probe = new AllocationProbe();
        while (probe.Measuring())
        {
            c = Container(); c.Velocity = new(0, -20, 0);
            FreeContainers.RockContact(ref c, Content.Cargo, rock, Double3.Zero, Vector3.Zero, 1, 1);
        }
        Assert.Equal(0, probe.Bytes);
    }

    [Fact]
    public void InvalidPoseNeverEvictsCargo()
    {
        var store = new FreeContainers();
        var c = Container(); c.Orientation = default;
        Assert.False(store.Add(Content, c, out _));
        c = Container(); c.Position = new(double.PositiveInfinity, 0, 0);
        Assert.False(store.Add(Content, c, out _));
        c = Container(); c.Velocity = new(float.NaN, 0, 0);
        Assert.False(store.Add(Content, c, out _));
        Assert.Empty(store.Items.ToArray());
    }
}
