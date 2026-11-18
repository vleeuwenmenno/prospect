using Brutal.Numerics;
using Prospect.Client.Ui;
using Xunit;

namespace Prospect.Tests;

/// <summary>
/// The framebuffer scale the interface is laid out and baked with. It is the window's content scale, which holds
/// still while a window is dragged or tiled, and never the framebuffer-to-window ratio of a single frame: GLFW
/// reports the two sizes a frame apart during a compositor resize, so that ratio jumps while the window settles.
/// Dear ImGui re-bakes its font atlas on such a jump, and a bake adds an atlas page the ImGui backend creates with
/// managed memory inside the frame loop. The resize benchmark measured exactly those frames.
/// </summary>
public sealed class InterfaceScaleTests
{
    [Fact]
    public void TheScaleIsTheContentScaleWhenTheSizesHaveSettled()
    {
        // A window of 1259 points whose framebuffer is 1261 pixels wide: the compositor rounded the width.
        var scale = InterfaceScale.Framebuffer(new float2(1f, 1f), new int2(1259, 1390), new int2(1261, 1390));

        Assert.Equal(new float2(1f, 1f), scale);
    }

    [Fact]
    public void AFramebufferThatIsAlreadyResizedAndAWindowThatIsNotChangesNothing()
    {
        // The frame a compositor resize shows: the framebuffer has taken its new size and the window has not, so
        // the ratio is nearly twice what the window settles at.
        var settled = new float2(1f, 1f);
        var jumped = new float2(2284f / 1259f, 1f);

        Assert.Equal(new float2(1f, 1f), InterfaceScale.Framebuffer(settled, new int2(1259, 1390), new int2(2284, 1390)));
        Assert.True(jumped.X > 1.8f);
    }

    [Fact]
    public void AFractionalContentScaleIsKeptAsItIs()
    {
        var scale = InterfaceScale.Framebuffer(new float2(1.6f, 1.6f), new int2(1000, 800), new int2(1600, 1280));

        Assert.Equal(new float2(1.6f, 1.6f), scale);
    }

    [Fact]
    public void AWindowThatReportsNoContentScaleFallsBackToItsOwnRatio()
    {
        Assert.Equal(new float2(2f, 2f), InterfaceScale.Framebuffer(default, new int2(800, 600), new int2(1600, 1200)));

        // Nothing is known about a window without a size, and a zero scale would break Dear ImGui's layout.
        Assert.Equal(new float2(1f, 1f), InterfaceScale.Framebuffer(default, default, new int2(1600, 1200)));

        // An unusable content scale is no content scale at all.
        Assert.Equal(new float2(2f, 2f), InterfaceScale.Framebuffer(new float2(float.NaN, 1f), new int2(800, 600), new int2(1600, 1200)));
    }
}
