namespace Prospect.Client.Screens;

/// <summary>
/// Decides when the 3D world need not be drawn because the interface hides all of it. Only the galaxy map does
/// that: it paints an opaque backdrop over the whole window once its fade-in has finished. Menus, the controls
/// sheet and the world settings only dim the world, so it stays live under them.
/// </summary>
internal static class WorldCover
{
    /// <summary>True when the map's own backdrop is fully opaque: its fade-in (<see cref="Kit.Fade"/>) is complete.</summary>
    public static bool MapHides(float fade) => fade >= 1f;

    /// <summary>
    /// Whether this frame records the world's scene passes (scene, gas, shields, tone mapping). The simulation, the
    /// prediction and the streaming of sectors run either way; only the drawing is skipped.
    /// </summary>
    public static bool Draws(bool inSpace, bool covered) => inSpace && !covered;
}
