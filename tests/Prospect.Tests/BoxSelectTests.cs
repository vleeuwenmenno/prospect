using Brutal.Numerics;
using Prospect.Client.World;
using Xunit;

namespace Prospect.Tests;

/// <summary>The editor's box select rectangle: corners, modifiers and what lies inside.</summary>
public sealed class BoxSelectTests
{
    [Fact]
    public void TheRectangleIsTheSameWhicheverCornerIsDraggedFirst()
    {
        BoxSelect.Normalize(new float2(50, 10), new float2(20, 40), out var min, out var max);
        Assert.Equal(new float2(20, 10), min);
        Assert.Equal(new float2(50, 40), max);
        BoxSelect.Normalize(new float2(20, 40), new float2(50, 10), out var min2, out var max2);
        Assert.Equal((min, max), (min2, max2));
    }

    [Theory]
    [InlineData(20, 10, true)]
    [InlineData(50, 40, true)]
    [InlineData(35, 25, true)]
    [InlineData(19.9f, 25, false)]
    [InlineData(35, 40.1f, false)]
    public void APointIsInsideWithItsEdgesIncluded(float x, float y, bool inside) =>
        Assert.Equal(inside, BoxSelect.Contains(new float2(20, 10), new float2(50, 40), new float2(x, y)));

    [Theory]
    [InlineData(false, false, BoxSelectMode.Replace)]
    [InlineData(true, false, BoxSelectMode.Add)]
    [InlineData(false, true, BoxSelectMode.Remove)]
    [InlineData(true, true, BoxSelectMode.Remove)]
    public void ShiftAddsAndAltRemoves(bool shift, bool alt, BoxSelectMode mode) => Assert.Equal(mode, BoxSelect.ModeOf(shift, alt));

    [Fact]
    public void ASlipOfTheHandIsAClickNotADrag()
    {
        Assert.False(BoxSelect.IsDrag(new float2(10, 10), new float2(12, 12), 5f));
        Assert.True(BoxSelect.IsDrag(new float2(10, 10), new float2(16, 10), 5f));
    }
}
