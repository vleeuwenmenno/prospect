using Brutal.ImGuiApi;
using Brutal.Numerics;
using Prospect.Client.Ui;

namespace Prospect.Client.Screens;

/// <summary>The icons a console bar can carry.</summary>
internal enum ConsoleIcon
{
    Shield,
    Hull,
    Energy,
    Heat,
}

/// <summary>Where a bar's segments go, and where its label ends.</summary>
internal readonly record struct ConsoleTrack(float Left, float Right, float Top, float Bottom, float LabelEnd);

/// <summary>
/// The arithmetic of a segmented, slanted bar, apart from drawing so it can be tested without fonts: how far the
/// slant leans, how wide each cell is, and how much of a cell a fraction fills. The pilot console and the target
/// panel draw their bars from the same numbers.
/// </summary>
internal static class BarGeometry
{
    /// <summary>How far a bar's top edge leans right of its bottom edge, for a bar <paramref name="height"/> tall.</summary>
    public static float Skew(float height, float slantRatio) => height * slantRatio;

    /// <summary>
    /// The width of one cell: what is left of the track after the lean and the gaps, shared by <paramref name="count"/>
    /// cells. Zero or less when the track is too narrow to draw any.
    /// </summary>
    public static float CellWidth(float left, float right, float skew, float gap, int count) =>
        count > 0 ? (right - skew - left - gap * (count - 1)) / count : 0f;

    /// <summary>How much of cell <paramref name="index"/> a bar filled to <paramref name="fraction"/> lights, 0 to 1.</summary>
    public static float Fill(float fraction, int index, int count) => Math.Clamp(fraction * count - index, 0f, 1f);

    /// <summary>How many whole cells a bar filled to <paramref name="fraction"/> lights; a part of a cell does not count.</summary>
    public static int Lit(float fraction, int count) => (int)MathF.Floor(Math.Clamp(float.IsFinite(fraction) ? fraction : 0f, 0f, 1f) * count + 1e-4f);
}

/// <summary>
/// The shared drawing of the heads-up bars: the pilot console's rows and the target panel's compact ones are the
/// same slanted cells, colours and icons at different sizes. Nothing here allocates.
/// </summary>
internal static class ConsoleBars
{
    /// <summary>The console's own slant: the lean of a row's top edge over its height.</summary>
    public const float ConsoleSlantRatio = FlightConsole.Slant / FlightConsole.Row;

    /// <summary>
    /// The bar: <paramref name="count"/> slanted segments. Lit up to <paramref name="fraction"/>, the
    /// leading segment partly; from there to <paramref name="trail"/> the charge just lost, fading as it catches up.
    /// Heat colours each segment by where it sits on the scale, so a hotter drive reaches into amber and red.
    /// </summary>
    public static void Segments(ImDrawListPtr draw, in ConsoleTrack track, float fraction, float trail, uint rgb, bool heat, int count = FlightConsole.Segments, float gap = 3f, float slantRatio = ConsoleSlantRatio)
    {
        var gapPx = Kit.Px(gap);
        var height = track.Bottom - track.Top;
        var skew = BarGeometry.Skew(height, slantRatio);
        var width = BarGeometry.CellWidth(track.Left, track.Right, skew, gapPx, count);
        if (width <= 0f)
            return;

        var off = Kit.Color(Theme.Line, 0.85f);
        var lost = Kit.Color(Kit.Mix(rgb, Theme.Text, 0.45f), 0.5f);
        for (var i = 0; i < count; i++)
        {
            var x = track.Left + i * (width + gapPx);
            var lit = BarGeometry.Fill(fraction, i, count);
            var trailed = BarGeometry.Fill(trail, i, count);
            // The hues stay, a shade towards the panel so the bars read without shouting.
            var color = Kit.Color(Kit.Mix(heat ? FlightConsole.HeatColor((i + 0.5f) / count) : rgb, Theme.Panel, FlightConsole.Mute));
            if (lit > 0f)
                Part(draw, x, x + width * lit, track.Top, track.Bottom, skew, color);
            if (trailed > lit)
                Part(draw, x + width * lit, x + width * trailed, track.Top, track.Bottom, skew, lost);
            if (lit < 1f && trailed < 1f)
                Part(draw, x + width * Math.Max(lit, trailed), x + width, track.Top, track.Bottom, skew, off);
        }
    }

    private static void Part(ImDrawListPtr draw, float from, float to, float top, float bottom, float skew, ImColor8 color) =>
        draw.AddQuadFilled(new float2(from + skew, top), new float2(to + skew, top), new float2(to, bottom), new float2(from, bottom), color);

    /// <summary>A slanted tick across the bar at a fraction of it: the boost floor, the restart point, where boost unlocks.</summary>
    public static void Marker(ImDrawListPtr draw, in ConsoleTrack track, float fraction, float alpha, float slantRatio = ConsoleSlantRatio)
    {
        var height = track.Bottom - track.Top;
        var skew = BarGeometry.Skew(height, slantRatio);
        var reach = Kit.Px(3);
        var x = track.Left + (track.Right - skew - track.Left) * Math.Clamp(fraction, 0f, 1f);
        var rise = reach * slantRatio;
        draw.AddLine(
            new float2(x - rise, track.Bottom + reach),
            new float2(x + skew + rise, track.Top - reach),
            Kit.Color(Theme.Text, alpha),
            Kit.Px(1.5f)
        );
    }

    /// <summary>A row's icon in a square of <paramref name="size"/> design pixels (14 for the console) at <paramref name="at"/>.</summary>
    public static void Glyph(ImDrawListPtr draw, ConsoleIcon icon, float2 at, uint rgb, float size = 14f)
    {
        var u = Kit.Scale * size / 14f;
        var color = Kit.Color(rgb, 0.95f);
        var thickness = Math.Max(1f, Kit.Px(1.5f) * size / 14f);
        Span<float2> points = stackalloc float2[7];
        switch (icon)
        {
            case ConsoleIcon.Shield:
                points[0] = at + new float2(7, 0.5f) * u;
                points[1] = at + new float2(13, 2.5f) * u;
                points[2] = at + new float2(12.5f, 8) * u;
                points[3] = at + new float2(7, 13.5f) * u;
                points[4] = at + new float2(1.5f, 8) * u;
                points[5] = at + new float2(1, 2.5f) * u;
                draw.AddPolyline(points[..6], color, ImDrawFlags.Closed, thickness);
                break;
            case ConsoleIcon.Hull:
                points[0] = at + new float2(7, 0.5f) * u;
                points[1] = at + new float2(13, 4) * u;
                points[2] = at + new float2(13, 10) * u;
                points[3] = at + new float2(7, 13.5f) * u;
                points[4] = at + new float2(1, 10) * u;
                points[5] = at + new float2(1, 4) * u;
                draw.AddPolyline(points[..6], color, ImDrawFlags.Closed, thickness);
                draw.AddCircleFilled(at + new float2(7, 7) * u, 1.8f * u, color, 8);
                break;
            case ConsoleIcon.Energy:
                points[0] = at + new float2(8.5f, 0.5f) * u;
                points[1] = at + new float2(2.5f, 8) * u;
                points[2] = at + new float2(6.5f, 8) * u;
                points[3] = at + new float2(5, 13.5f) * u;
                points[4] = at + new float2(11.5f, 5.5f) * u;
                points[5] = at + new float2(7.5f, 5.5f) * u;
                points[6] = at + new float2(9.5f, 0.5f) * u;
                draw.AddPolyline(points, color, ImDrawFlags.Closed, thickness);
                break;
            default:
                for (var i = 0; i < 3; i++)
                {
                    var x = 2.5f + i * 4f;
                    points[0] = at + new float2(x, 13.5f) * u;
                    points[1] = at + new float2(x + 1.5f, 9.5f) * u;
                    points[2] = at + new float2(x, 5) * u;
                    points[3] = at + new float2(x + 1.5f, 0.5f) * u;
                    draw.AddPolyline(points[..4], color, ImDrawFlags.None, thickness);
                }

                break;
        }
    }
}
