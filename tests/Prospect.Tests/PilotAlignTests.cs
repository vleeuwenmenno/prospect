using System.Numerics;
using Prospect.Client.World;
using Xunit;

namespace Prospect.Tests;

public sealed class PilotAlignTests
{
    private static readonly float Stop = PilotTuning.Load().AlignStopCosine;
    private static readonly Vector3 Nose = -Vector3.UnitZ;
    private static readonly Vector3 Side = Vector3.Normalize(new Vector3(1f, 0f, -1f));

    [Fact]
    public void HeldAlignsAndReleaseKeepsAligningUntilWithinTheStopAngle()
    {
        var align = new PilotAlign(Stop);
        Assert.False(align.Update(false, false, Nose, Side));
        Assert.True(align.Update(true, false, Nose, Side));
        Assert.True(align.Update(false, false, Nose, Side));
        Assert.True(align.Latched);

        var almost = Vector3.Normalize(Nose + Vector3.UnitX * 0.001f);
        Assert.False(align.Update(false, false, almost, Nose));
        Assert.False(align.Latched);
    }

    [Fact]
    public void BlockedNeverAlignsAndForgetsAClick()
    {
        var align = new PilotAlign(Stop);
        Assert.False(align.Update(true, true, Nose, Side));
        Assert.False(align.Update(false, false, Nose, Side));

        Assert.True(align.Update(true, false, Nose, Side));
        Assert.True(align.Update(false, false, Nose, Side));
        Assert.False(align.Update(false, true, Nose, Side));
        Assert.False(align.Update(false, false, Nose, Side));
    }

    [Fact]
    public void ANewPressReplacesAClickAndStopsOnlyOnRelease()
    {
        var align = new PilotAlign(Stop);
        align.Update(true, false, Nose, Side);
        align.Update(false, false, Nose, Side);
        Assert.True(align.Update(true, false, Side, Side));
        Assert.False(align.Latched);
        Assert.False(align.Update(false, false, Side, Side));
    }
}
