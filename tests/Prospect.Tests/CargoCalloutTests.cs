using System.Numerics;
using Prospect.Client.Screens;
using Prospect.Game.World;
using Xunit;

namespace Prospect.Tests;

/// <summary>The Cargo window's container callout: selection to callout, follow, clear, and the box it outlines, without fonts.</summary>
public sealed class CargoCalloutTests
{
    private static readonly GameContent Content = GameContent.Load(Path.Combine(AppContext.BaseDirectory, "content"));

    [Fact]
    public void ASelectionInAShownWindowOpensTheCalloutAndNothingElseDoes()
    {
        var callout = new CargoCallout();
        callout.Sync(true, 10, 5, 3, -1);
        Assert.False(callout.IsOpen);
        callout.Sync(true, 10, 5, 3, 3);
        Assert.False(callout.IsOpen);
        callout.Sync(true, 10, 5, 3, 1);
        Assert.True(callout.IsOpen);
        Assert.Equal(1, callout.Index);
        callout.Sync(true, 10, 5, 3, 2);
        Assert.Equal(2, callout.Index);
    }

    [Fact]
    public void TheCalloutClearsWithTheWindowTheHullTheHoldAndEscape()
    {
        var callout = new CargoCallout();
        callout.Sync(true, 10, 5, 3, 1);
        callout.Sync(false, 10, 5, 3, 1);
        Assert.False(callout.IsOpen);

        callout.Sync(true, 10, 5, 3, 1);
        callout.Sync(true, 11, 5, 3, 1);
        Assert.False(callout.IsOpen);

        callout.Sync(true, 10, 5, 3, 1);
        callout.Sync(true, 10, 6, 3, 1);
        Assert.False(callout.IsOpen);

        callout.Sync(true, 10, 6, 3, 1);
        Assert.True(callout.IsOpen);
        callout.Close();
        Assert.False(callout.IsOpen);
        Assert.Equal(ScreenHost.BackStep.CargoCallout, ScreenHost.NextBackStep(false, false, false, false, true, false, Prospect.Client.World.ViewMode.Pilot));
        Assert.Equal(ScreenHost.BackStep.OpenMenu, ScreenHost.NextBackStep(false, false, false, false, callout.IsOpen, false, Prospect.Client.World.ViewMode.Pilot));
    }

    [Fact]
    public void TheBoxIsTheRendererSlotInsideItsRackAndTracksTheHoldIndex()
    {
        var catalog = Content.Cargo;
        HullDesign? design = null;
        var rack = -1;
        foreach (var candidate in Content.Designs)
        {
            for (var i = 0; i < candidate.Types.Length && rack < 0; i++)
                if (candidate.Types[i].Cargo > 0 && candidate.Shapes[i] == BlockShape.Box)
                    rack = i;
            if (rack >= 0)
            {
                design = candidate;
                break;
            }
        }

        Assert.NotNull(design);
        var hold = new[]
        {
            new HoldContainer(rack, 0, -1, 0, 10f, default),
            new HoldContainer(rack, 1, 0, 50, 10f, new GridSize(1, 0, 0)),
        };
        Assert.False(CargoCallout.TryBox(design, catalog, hold, -1, out _, out _));
        Assert.False(CargoCallout.TryBox(design, catalog, hold, 2, out _, out _));
        Assert.False(CargoCallout.TryBox(design, catalog, [new HoldContainer(9999, 0, -1, 0, 1f, default)], 0, out _, out _));

        Assert.True(CargoCallout.TryBox(design, catalog, hold, 1, out var centre, out var half));
        var shape = catalog.Sizes[1].Shape;
        var corner = design.BlockOffsets[rack] - design.BlockHalfExtents[rack];
        var expected = corner + new Vector3(1 + shape.X * .5f, shape.Y * .5f, shape.Z * .5f) * catalog.UnitSize;
        Assert.Equal(expected, centre);
        Assert.Equal(new Vector3(shape.X, shape.Y, shape.Z) * (catalog.UnitSize * .5f), half);

        // Both container corners stay inside the rack block when its packed cells do.
        Assert.True(CargoCallout.TryBox(design, catalog, hold, 0, out var first, out var firstHalf));
        Assert.NotEqual(first, centre);
        var low = design.BlockOffsets[rack] - design.BlockHalfExtents[rack] - new Vector3(1e-3f);
        Assert.True(first.X - firstHalf.X >= low.X && first.Y - firstHalf.Y >= low.Y && first.Z - firstHalf.Z >= low.Z);
    }
}
