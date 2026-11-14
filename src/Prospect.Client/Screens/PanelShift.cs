using Brutal.Numerics;

namespace Prospect.Client.Screens;

/// <summary>
/// Where a panel the player can drag goes: its home (the place the game gives it) plus the player's saved shift in
/// unscaled pixels, kept wholly on screen, under the navigation bar and inside the margins. Pure arithmetic, so it
/// holds at any window size and interface scale, and it is what the tip panels and the galaxy map's panels share.
/// The shift is stored unscaled and the clamp is worked out every frame, so a smaller window or a larger interface
/// scale never leaves a title bar out of reach, and the original place comes back when the room does.
/// </summary>
internal static class PanelShift
{
    /// <summary>
    /// The top left corner of a panel of <paramref name="size"/> pixels whose home is <paramref name="home"/> (in
    /// screen pixels) and which the player has moved by <paramref name="shift"/> unscaled pixels.
    /// </summary>
    public static float2 Place(float2 home, float2 size, float2 display, float scale, float2 shift) =>
        FloatingWindow.Clamp(home + new float2(MathF.Round(shift.X * scale), MathF.Round(shift.Y * scale)), size, display, scale);

    /// <summary>
    /// The shift that carries a panel now at <paramref name="shift"/> by <paramref name="delta"/> screen pixels.
    /// The move starts from where the panel is drawn (after the clamp), so a panel pressed against an edge follows
    /// the pointer at once instead of first working off a home place that lies past the edge. What is saved is
    /// the shift from home that gives the clamped result, never a position past an edge. <paramref name="fixedOffset"/>
    /// is a part of the place that is not the player's (a tip anchor's own offset).
    /// </summary>
    public static float2 Dragged(float2 home, float2 size, float2 display, float scale, float2 shift, float2 delta, float2 fixedOffset = default)
    {
        var now = Place(home, size, display, scale, fixedOffset + shift);
        var at = FloatingWindow.Clamp(now + delta, size, display, scale);
        return (at - home) / scale - fixedOffset;
    }
}
