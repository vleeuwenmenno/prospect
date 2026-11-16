using System.Numerics;
using Prospect.Client.World;
using Prospect.Game.World;
using Xunit;

namespace Prospect.Tests;

public sealed class FreeContainerVisualTests
{
    [Fact]
    public void PoseInterpolatesDoublePositionsBoundsExtrapolationAndWithdraws()
    {
        var view = new FreeContainerVisuals();
        var items = new[] { new FreeContainer { Id = 1, Position = new(1e12, 0, 0), Orientation = Quaternion.Identity, Velocity = new(60, 0, 0), Hull = 50 } };
        Assert.True(view.Update(items, 6));
        Assert.False(view.Contact(items[0], out _));
        items[0].Position += new Vector3(6, 0, 0);
        Assert.True(view.Update(items, 12));
        Assert.False(view.Update(items, 12));
        Assert.Equal(1e12 + 3, view.Pose(items[0], 15).Position.X);
        Assert.Equal(1e12 + 18, view.Pose(items[0], 1000).Position.X);
        Assert.False(view.Contact(items[0], out _));
        items[0].Velocity = new(-30, 0, 0);
        view.Update(items, 18);
        Assert.True(view.Contact(items[0], out var normal));
        Assert.Equal(-Vector3.UnitX, normal);
        Assert.True(view.Update([], 24)); Assert.True(view.Items.IsEmpty);
    }

    [Fact]
    public void PickingUsesRotatedContainerBoxAndViewWorkTakesNoMemory()
    {
        var content = GameContent.Load();
        var item = new FreeContainer { Id = 1, Size = 0, Position = new(1e12, 0, -10), Orientation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, .5f) };
        var camera = new Double3(1e12, 0, 0);
        Assert.True(float.IsFinite(TargetPicking.Container(-Vector3.UnitZ, camera, item, content.Cargo)));
        Assert.True(float.IsPositiveInfinity(TargetPicking.Container(Vector3.UnitX, camera, item, content.Cargo)));
        var view = new FreeContainerVisuals(); var items = new[] { item }; long tick = 0;
        var probe = new AllocationProbe();
        while (probe.Measuring())
        {
            view.Update(items, ++tick);
            view.Pose(item, tick);
            view.Contact(item, out _);
            TargetPicking.Container(-Vector3.UnitZ, camera, item, content.Cargo);
        }
        Assert.Equal(0, probe.Bytes);
    }
}
