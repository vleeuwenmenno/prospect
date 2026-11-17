using System.Numerics;
using Prospect.Client.World;
using Prospect.Game.Client;
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
        // Between the two sets: interpolated. Past the newest: flown on at its velocity (60 m/s, a metre a tick),
        // as far as the own hull's prediction lead asks, and no further than MaxAheadTicks.
        Assert.Equal(1e12 + 3, view.Pose(items[0], 9).Position.X);
        Assert.Equal(1e12, view.Pose(items[0], 0).Position.X);
        Assert.Equal(1e12 + 15, view.Pose(items[0], 21).Position.X, 6);
        Assert.Equal(1e12 + 6 + FreeContainerVisuals.MaxAheadTicks, view.Pose(items[0], 1000).Position.X, 6);
        Assert.False(view.Contact(items[0], out _));
        items[0].Velocity = new(-30, 0, 0);
        view.Update(items, 18);
        Assert.True(view.Contact(items[0], out var normal));
        Assert.Equal(-Vector3.UnitX, normal);
        Assert.True(view.Update([], 24)); Assert.True(view.Items.IsEmpty);
    }

    [Fact]
    public void AContainerRidingAlongAtSpeedIsShownWhereItIsBesideTheOwnHull()
    {
        // Hull and container coast at 240 m/s, five metres apart. Sets come every six ticks (the newest at 12); the
        // own hull is predicted to tick 17. Shown one interval behind the server, the container trailed by metres.
        var velocity = new Vector3(0, 0, -240); var offset = new Vector3(3, 5, 0); var start = new Double3(1e9, 0, 0);
        Double3 At(long tick) => start + velocity * (tick / 60f);
        var view = new FreeContainerVisuals();
        var item = new FreeContainer { Id = 1, Velocity = velocity, Orientation = Quaternion.Identity, Hull = 50 };
        foreach (var t in new long[] { 6, 12 })
        {
            item.Position = At(t) + offset;
            view.Update([item], t);
        }
        var hulls = new[] { new HullView { Id = 7, IsOwn = true, ShownTick = 17, Position = At(17), PreviousPosition = At(16) },
            new HullView { Id = 9, ShownTick = 8 } };
        foreach (var alpha in new[] { 0f, .5f, 1f })
        {
            var drawn = Double3.Lerp(hulls[0].PreviousPosition, hulls[0].Position, alpha);
            var pose = view.Pose(item, FreeContainerVisuals.ShownTick(item, hulls, 0, 14, alpha));
            Assert.True((pose.Position.RelativeTo(drawn) - offset).Length() < .01f, $"{pose.Position.RelativeTo(drawn)} at alpha {alpha}");
        }
        // A body another hull's beam holds goes with that hull, which is drawn in the past; with no hull, the server's tick.
        Assert.Equal(7.5, FreeContainerVisuals.ShownTick(item with { TractorHull = 9 }, hulls, 0, 14, .5f));
        Assert.Equal(14.5, FreeContainerVisuals.ShownTick(item, [], -1, 14, .5f));
    }

    [Fact]
    public void PoweredPullAndItsHandOffDoNotInventContactSparks()
    {
        var view = new FreeContainerVisuals();
        var items = new[] { new FreeContainer { Id = 1, Orientation = Quaternion.Identity } };
        view.Update(items, 6);
        items[0].Velocity = Vector3.UnitY;
        items[0].TractorPowered = true;
        view.Update(items, 12);
        Assert.False(view.Contact(items[0], out _));
        items[0].TractorPowered = false;
        items[0].Velocity = Vector3.UnitX;
        view.Update(items, 18);
        Assert.False(view.Contact(items[0], out _));
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
