using System.Numerics;
using Brutal.Numerics;
using Prospect.Client.Render;
using Prospect.Client.World;
using Prospect.Game.World;
using Xunit;

namespace Prospect.Tests;

/// <summary>The container tooltip's figures and picking, font-free; the frame-order tests drive it through a real pointer.</summary>
public sealed class ContainerHoverTests
{
    private static readonly GameContent Content = GameContent.Load();
    private static readonly CargoCatalog Cargo = Content.Cargo;
    private static readonly Resource[] Resources = Content.Galaxy.Resources;

    [Fact]
    public void FillIsKilogramsAgainstWhatTheSizeHoldsOfThatResource()
    {
        var unit = Cargo.UnitSize;
        var full = Cargo.Sizes[2].Units * unit * unit * unit * Cargo.Density(Resources[0].Id);
        Assert.Equal(1f, ContainerHover.Fill(Cargo, Resources, 2, 0, full));
        Assert.Equal(.5f, ContainerHover.Fill(Cargo, Resources, 2, 0, full / 2), 3);
        Assert.Equal(1f, ContainerHover.Fill(Cargo, Resources, 2, 0, full * 3));
        Assert.Equal(0f, ContainerHover.Fill(Cargo, Resources, 2, -1, 10));
        Assert.Equal(0f, ContainerHover.Fill(Cargo, Resources, 99, 0, 10));
        Assert.Equal(0f, ContainerHover.Fill(Cargo, Resources, 2, 0, double.NaN));
    }

    [Fact]
    public void AStowedContainerReportsItsOwnRecord()
    {
        var info = ContainerHover.Stowed(Cargo, Resources, new HoldContainer(3, 1, 1, 500, 90, default));
        Assert.Equal(HoverKind.Stowed, info.Kind);
        Assert.Equal(1, info.Resource);
        Assert.Equal(500, info.Amount);
        Assert.Equal(90, info.Hull);
        Assert.Equal(Cargo.Sizes[1].Hull, info.MaxHull);
        Assert.Equal(Cargo.Sizes[1].Units, info.Units);
        Assert.Equal(ClaimState.None, info.Claim);
        Assert.False(ContainerHover.Stowed(Cargo, Resources, new HoldContainer(3, 99, 1, 1, 1, default)).Any);
    }

    [Fact]
    public void ALooseContainerSaysWhoClaimsItAndHowLongUntilPickup()
    {
        var delayTicks = (long)(Cargo.PickupDelaySeconds * Prospect.Engine.Time.TickLoop.SimulationHz);
        var item = new FreeContainer { Size = 0, Resource = -1, Hull = 40, PickupReadyTick = 1000 + delayTicks, ClaimOwner = 7 };
        var mine = ContainerHover.Free(Cargo, Resources, item, 7, 1000);
        Assert.Equal(HoverKind.Free, mine.Kind);
        Assert.Equal(ClaimState.Yours, mine.Claim);
        Assert.Equal(Cargo.PickupDelaySeconds, mine.PickupSeconds, 3);
        Assert.Equal(ClaimState.Others, ContainerHover.Free(Cargo, Resources, item, 8, 1000).Claim);
        item.ClaimOwner = 0;
        Assert.Equal(ClaimState.Unclaimed, ContainerHover.Free(Cargo, Resources, item, 8, 1000).Claim);
        // Once the delay has passed there is nothing left to wait for, and a container released earlier waits for nothing.
        Assert.Equal(0f, ContainerHover.Free(Cargo, Resources, item, 8, 1000 + delayTicks).PickupSeconds);
        Assert.Equal(0f, ContainerHover.Free(Cargo, Resources, item, 8, 1000 + delayTicks + 500).PickupSeconds);
    }

    [Fact]
    public void ARayThroughAStowedSlotHitsItAndOneBesideItMisses()
    {
        var design = Content.Pod;
        var rack = Array.FindIndex(design.Types, type => type.Cargo > 0);
        Assert.True(rack >= 0);
        var held = new HoldContainer(rack, 1, 0, 100, 100, default);
        var origin = new Double3(1000, 2000, 3000);
        var orientation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, .7f);
        var pose = ContainerHover.StowedPose(design, origin, orientation, 1, held, Cargo);
        var camera = origin + new Vector3(0, 0, 50);
        var toward = Vector3.Normalize(pose.Position.RelativeTo(camera));
        var hit = TargetPicking.Container(toward, camera, pose, Cargo);
        Assert.True(float.IsFinite(hit));
        Assert.InRange(hit, 40f, 60f);
        var aside = Vector3.Normalize(toward + new Vector3(.2f, 0, 0));
        Assert.True(float.IsPositiveInfinity(TargetPicking.Container(aside, camera, pose, Cargo)));
        // The pose follows the hull: moving it moves the slot by the same amount.
        var moved = ContainerHover.StowedPose(design, origin + new Vector3(5, 0, 0), orientation, 1, held, Cargo);
        Assert.Equal(5f, (float)(moved.Position.X - pose.Position.X), 3);
    }

    [Fact]
    public void TheHoverPathTakesNoMemory()
    {
        var item = new FreeContainer { Size = 3, Resource = 2, Amount = 100, Hull = 50, PickupReadyTick = 600, ClaimOwner = 4 };
        var held = new HoldContainer(0, 3, 2, 100, 50, default);
        var probe = new AllocationProbe();
        while (probe.Run())
        {
            probe.Start();
            for (var i = 0; i < 1000; i++)
            {
                _ = ContainerHover.Free(Cargo, Resources, item, 4, i);
                _ = ContainerHover.Stowed(Cargo, Resources, held);
            }
            probe.Stop();
        }
        Assert.Equal(0, probe.Bytes);
    }
}
