using Prospect.Client.Screens;
using Prospect.Game.World;
using Xunit;

namespace Prospect.Tests;

/// <summary>The rows the target panel and data sheet read for a scanned asteroid, apart from drawing.</summary>
public sealed class RockCompositionTests
{
    private static ModuleObservation Record(int resource, float grade, int mix1 = -1, float share1 = 0, int mix2 = -1, float share2 = 0) =>
        new() { Resource = resource, Grade = grade, Mix1 = mix1, Mix1Share = share1, Mix2 = mix2, Mix2Share = share2 };

    [Fact]
    public void ResolvedSharesLeaveRockAsAFigure()
    {
        var composition = RockComposition.From(Record(0, 0.40f, 1, 0.10f, 2, 0.05f), 8);
        Assert.Equal(3, composition.Count);
        Assert.False(composition.RockIsCeiling);
        Assert.Equal(0.45f, composition.Remainder, 4);
        Assert.Equal(4, composition.Rows);
    }

    [Fact]
    public void ATraceMakesRockACeilingAndHasNoFigure()
    {
        var composition = RockComposition.From(Record(0, 0.40f, 1, 0.10f, 2, 0f), 8);
        Assert.True(composition.IsTrace(2));
        Assert.False(composition.IsTrace(1));
        Assert.True(composition.RockIsCeiling);
        Assert.Equal(0.50f, composition.Remainder, 4);
        Assert.True(RockComposition.From(Record(0, 0f), 8).IsTrace(0));
    }

    [Fact]
    public void BareRockAndDamagedRecordsHaveNoOreParts()
    {
        Assert.Equal(0, RockComposition.From(Record(-1, 0f), 8).Count);
        var damaged = RockComposition.From(Record(99, 0.4f), 8);
        Assert.Equal(0, damaged.Count);
        Assert.Equal(1f, damaged.Remainder);
        Assert.Equal(2, RockComposition.From(Record(0, 0.4f, 1, 0.1f, 99, 0.1f), 8).Count);
    }
}
