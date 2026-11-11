using Brutal.ImGuiApi;
using Brutal.Numerics;

namespace Prospect.Client.Ui;

/// <summary>
/// The shapes the heads-up display is drawn with, so every piece of it speaks one angular language: dark glass,
/// a hairline, and one lit edge in the colour of what the piece is saying. Glow is a single soft stroke under that
/// edge and nowhere else, so the edges that matter stand out and the rest stay quiet.
/// </summary>
internal static class Hud
{
    /// <summary>The glass under every heads-up piece.</summary>
    public const float GlassAlpha = 0.72f;

    /// <summary>
    /// A panel in the kit's chamfered shape, with its left edge lit in <paramref name="tone"/>. A
    /// <paramref name="lit"/> panel adds the soft stroke under that edge, for one that wants the eye now.
    /// </summary>
    public static void Plate(ImDrawListPtr draw, float2 min, float2 max, uint tone, bool lit = false)
    {
        var chamfer = Kit.Px(Theme.Chamfer);
        Kit.ChamferFill(draw, min, max, Kit.Color(Theme.Void, GlassAlpha), chamfer);
        Kit.ChamferOutline(draw, min, max, Kit.Color(Theme.LineStrong, 0.55f), chamfer);
        var top = new float2(min.X + Kit.Px(1), min.Y);
        var bottom = new float2(min.X + Kit.Px(1), max.Y - chamfer);
        if (lit)
            draw.AddLine(top, bottom, Kit.Color(tone, 0.16f), Kit.Px(5));
        draw.AddLine(top, bottom, Kit.Color(tone, 0.95f), Kit.Px(2));
    }

    /// <summary>
    /// A band whose two ends come to a point <paramref name="point"/> deep: the jump strip, the NAV card and, at
    /// its own proportions, the speed readout's hexagon. Its outline is lit in <paramref name="tone"/>.
    /// </summary>
    public static void Pointed(ImDrawListPtr draw, float2 min, float2 max, float point, uint tone, float toneAlpha, bool glow)
    {
        Span<float2> points = stackalloc float2[6];
        var middle = (min.Y + max.Y) * 0.5f;
        points[0] = new float2(min.X, middle);
        points[1] = new float2(min.X + point, min.Y);
        points[2] = new float2(max.X - point, min.Y);
        points[3] = new float2(max.X, middle);
        points[4] = new float2(max.X - point, max.Y);
        points[5] = new float2(min.X + point, max.Y);
        draw.AddConvexPolyFilled(points, Kit.Color(Theme.Void, GlassAlpha));
        if (glow)
            draw.AddPolyline(points, Kit.Color(tone, 0.14f), ImDrawFlags.Closed, Kit.Px(4));
        draw.AddPolyline(points, Kit.Color(tone, toneAlpha), ImDrawFlags.Closed, 1f);
    }

    /// <summary>The width <see cref="KeyChip"/> takes for a key's name.</summary>
    public static float KeyChipWidth(ImFontPtr font, float size, ReadOnlySpan<byte> key) =>
        key.IsEmpty ? 0f : Kit.Measure(font, size, key).X + Kit.Px(Theme.Space2) * 2;

    /// <summary>
    /// A key's name in a small outlined box, as the loaded bindings name it, from <paramref name="left"/> and
    /// centred on <paramref name="middle"/> in height. Returns its width; nothing is drawn for an unbound action.
    /// </summary>
    public static float KeyChip(ImDrawListPtr draw, ImFontPtr font, float size, float left, float middle, uint rgb, ReadOnlySpan<byte> key)
    {
        if (key.IsEmpty)
            return 0f;
        var text = Kit.Measure(font, size, key);
        var width = text.X + Kit.Px(Theme.Space2) * 2;
        var height = MathF.Round(text.Y + Kit.Px(4));
        var min = new float2(left, MathF.Round(middle - height * 0.5f));
        var max = min + new float2(width, height);
        draw.AddRectFilled(min, max, Kit.Color(rgb, 0.12f));
        draw.AddRect(min, max, Kit.Color(rgb, 0.8f));
        Kit.Text(draw, font, size, new float2(min.X + Kit.Px(Theme.Space2), min.Y + Kit.Px(2)), rgb, key);
        return width;
    }
}
