using Brutal.Numerics;
using Prospect.Client.Screens;
using Xunit;

namespace Prospect.Tests;

/// <summary>Where the map's unit list goes on small and scaled windows.</summary>
public sealed class MapUnitLayoutTests
{
    [Theory]
    [InlineData(1280f, 720f, 1f, 3, false, false)]
    [InlineData(1280f, 720f, 1f, 64, false, false)]
    [InlineData(1655f, 688f, 2f, 3, true, true)]
    [InlineData(1280f, 720f, 2f, 3, true, true)]
    [InlineData(1655f, 688f, 2f, 64, true, true)]
    public void TheListFitsUnderTheHeadingAndAlwaysShowsItsRows(float width, float height, float scale, int rows, bool shortHeading, bool compactRows)
    {
        var display = new float2(width, height);
        var sectorBottom = MathF.Round(24f * scale) + MathF.Round(116f * scale);
        var hoverCard = MapUnitList.HoverCardLeft(width, scale, 340f, 96f);
        var layout = MapUnitList.Layout(display, scale, rows, sectorBottom, hoverCard);

        Assert.Equal(shortHeading, layout.ShortHeading);
        Assert.Equal(compactRows, layout.CompactRows);

        // Both panels fit every required viewport without covering each other.
        Assert.True(layout.Min.X >= 0 && layout.Min.X + layout.Size.X <= display.X);
        Assert.True(layout.Min.X + layout.Size.X + MathF.Round(8f * scale) <= hoverCard);
        Assert.True(hoverCard + MathF.Round(340f * scale) <= display.X);
        Assert.True(layout.Min.Y + layout.Size.Y <= display.Y);
        var above = layout.ShortHeading ? sectorBottom + MathF.Round(36f * scale) : MathF.Round(256f * scale);
        Assert.True(layout.Min.Y >= above);

        // Three rows always show whole; more scroll.
        Assert.True((int)(layout.RowsSize.Y / layout.Row) >= Math.Min(rows, 3));
    }
}
