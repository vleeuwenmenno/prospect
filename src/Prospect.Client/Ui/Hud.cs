using Brutal.ImGuiApi;
using Brutal.Numerics;

namespace Prospect.Client.Ui;

/// <summary>
/// The shapes the heads-up display is drawn with, in the look of the map's unit list: muted dark blue-grey panels,
/// a thin quiet border, and one small tick in the colour of what the piece is saying. Nothing glows; a piece that
/// wants the eye now has its border drawn in its colour, and its words say why.
/// </summary>
internal static class Hud
{
    /// <summary>How opaque the panel fill under every heads-up piece is.</summary>
    public const float FillAlpha = 0.9f;

    /// <summary>The tick along a panel's top edge from its left corner, in unscaled pixels.</summary>
    public const float TickWidth = 28f;
    public const float TickHeight = 2f;

    /// <summary>A border: quiet, or in <paramref name="tone"/> for a piece that wants the eye now.</summary>
    public static uint Border(uint tone, bool lit) => Kit.Mix(Theme.Line, tone, lit ? 0.7f : 0.25f);

    /// <summary>
    /// A panel in the kit's chamfered shape with a small tick in <paramref name="tone"/> at its top left. A
    /// <paramref name="lit"/> panel draws its border in that colour too.
    /// </summary>
    public static void Plate(ImDrawListPtr draw, float2 min, float2 max, uint tone, bool lit = false)
    {
        var chamfer = Kit.Px(Theme.Chamfer);
        Kit.ChamferFill(draw, min, max, Kit.Color(Theme.Panel, FillAlpha), chamfer);
        Kit.ChamferOutline(draw, min, max, Kit.Color(Border(tone, lit)), chamfer);
        draw.AddRectFilled(min, min + new float2(Kit.Px(TickWidth), Kit.Px(TickHeight)), Kit.Color(tone));
    }

    /// <summary>
    /// A band whose two ends come to a point <paramref name="point"/> deep: the jump strip and, at its own
    /// proportions, the speed readout's hexagon. Its border takes <paramref name="tone"/> quietly, or plainly when
    /// <paramref name="lit"/>.
    /// </summary>
    public static void Pointed(ImDrawListPtr draw, float2 min, float2 max, float point, uint tone, float toneAlpha, bool lit)
    {
        Span<float2> points = stackalloc float2[6];
        var middle = (min.Y + max.Y) * 0.5f;
        points[0] = new float2(min.X, middle);
        points[1] = new float2(min.X + point, min.Y);
        points[2] = new float2(max.X - point, min.Y);
        points[3] = new float2(max.X, middle);
        points[4] = new float2(max.X - point, max.Y);
        points[5] = new float2(min.X + point, max.Y);
        draw.AddConvexPolyFilled(points, Kit.Color(Theme.Panel, FillAlpha));
        draw.AddPolyline(points, Kit.Color(Border(tone, lit), toneAlpha), ImDrawFlags.Closed, 1f);
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
        draw.AddRectFilled(min, max, Kit.Color(Theme.PanelRaised));
        draw.AddRect(min, max, Kit.Color(Kit.Mix(Theme.Line, rgb, 0.5f)));
        Kit.Text(draw, font, size, new float2(min.X + Kit.Px(Theme.Space2), min.Y + Kit.Px(2)), rgb, key);
        return width;
    }
}
