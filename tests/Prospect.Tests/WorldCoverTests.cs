using Prospect.Client.Screens;
using Prospect.Client.Ui;
using Xunit;

namespace Prospect.Tests;

/// <summary>When a frame in space skips the 3D world because the interface hides all of it.</summary>
public sealed class WorldCoverTests
{
    [Fact]
    public void TheMapHidesTheWorldOnlyOnceItsBackdropIsOpaque()
    {
        // The map's fade is Kit.EaseOut(time / Theme.ScreenEnter): the world is drawn under it for the fade-in
        // and not a frame longer than the backdrop is translucent.
        Assert.False(WorldCover.MapHides(Kit.EaseOut(0f)));
        Assert.False(WorldCover.MapHides(Kit.EaseOut(Theme.ScreenEnter * 0.99f / Theme.ScreenEnter)));
        Assert.True(WorldCover.MapHides(Kit.EaseOut(Theme.ScreenEnter / Theme.ScreenEnter)));
        Assert.True(WorldCover.MapHides(Kit.EaseOut(5f)));
    }

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    public void TheSceneIsRecordedOnlyInSpaceAndWhenNothingHidesIt(bool inSpace, bool covered, bool draws) =>
        Assert.Equal(draws, WorldCover.Draws(inSpace, covered));
}
