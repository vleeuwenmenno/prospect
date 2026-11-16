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
