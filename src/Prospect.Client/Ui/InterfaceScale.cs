using Brutal.Numerics;

namespace Prospect.Client.Ui;

/// <summary>
/// The framebuffer scale the interface is laid out and baked with: pixels per interface point.
/// </summary>
/// <remarks>
/// The window's content scale is that ratio: the sizes settle at <c>framebuffer = round(window * contentScale)</c>,
/// and the content scale does not change while a window is dragged or tiled. This frame's
/// <c>framebuffer / window</c> ratio is not, because GLFW reports the window size in points and the framebuffer in
/// pixels one frame apart while a compositor resizes the window: for a frame or two the framebuffer has the new size
/// and the window the old one, and the ratio jumps to twice what it settles at. Dear ImGui re-bakes its whole font
/// atlas to a new size whenever that scale changes, and a bake that needs more room adds an atlas page, which the
/// ImGui renderer backend creates with managed memory inside the frame loop (the page it replaces is destroyed three
/// frames later, also inside the frame loop). The benchmark's allocation target caught exactly those two frames.
/// </remarks>
internal static class InterfaceScale
{
    /// <summary>The scale to give Dear ImGui, from the window's content scale where the platform reports one.</summary>
    public static float2 Framebuffer(float2 contentScale, int2 windowSize, int2 framebuffer)
    {
        if (contentScale.X > 0f && contentScale.Y > 0f && float.IsFinite(contentScale.X) && float.IsFinite(contentScale.Y))
            return contentScale;
        if (windowSize.X <= 0 || windowSize.Y <= 0 || framebuffer.X <= 0 || framebuffer.Y <= 0)
            return new float2(1f, 1f);
        return new float2((float)framebuffer.X / windowSize.X, (float)framebuffer.Y / windowSize.Y);
    }
}
