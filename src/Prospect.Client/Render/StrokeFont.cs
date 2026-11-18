namespace Prospect.Client.Render;

/// <summary>
/// A small stroke font for text built into meshes on workers (container plates): capitals, digits and a dash, each a
/// few polylines on a grid two wide and four high, y up. No font file or texture; the strokes become thin bars
/// (<see cref="PropulsionModels"/>). Like <c>EditorIcons</c>, only lines, but placed in 3D.
/// </summary>
internal static class StrokeFont
{
    /// <summary>The grid a glyph is drawn on: width 2, height 4.</summary>
    public const int Width = 2, Height = 4;

    // Each polyline is points written as two digits "xy"; polylines are separated by '|'.
    private static readonly Dictionary<char, string> Glyphs = new()
    {
        ['A'] = "00 03 14 23 20|02 22",
        ['B'] = "00 04 14 23 12 02|12 21 10 00",
        ['C'] = "24 04 00 20",
        ['D'] = "00 04 14 23 21 10 00",
        ['E'] = "24 04 00 20|02 12",
        ['F'] = "24 04 00|02 12",
        ['G'] = "24 04 00 20 22 12",
        ['H'] = "00 04|20 24|02 22",
        ['I'] = "04 24|14 10|00 20",
        ['J'] = "24 21 10 01",
        ['K'] = "00 04|24 02 20",
        ['L'] = "04 00 20",
        ['M'] = "00 04 12 24 20",
        ['N'] = "00 04 20 24",
        ['O'] = "00 04 24 20 00",
        ['P'] = "00 04 24 22 02",
        ['Q'] = "00 04 24 20 00|11 20",
        ['R'] = "00 04 24 22 02|12 20",
        ['S'] = "24 14 03 12 21 10 00",
        ['T'] = "04 24|14 10",
        ['U'] = "04 00 20 24",
        ['V'] = "04 10 24",
        ['W'] = "04 00 12 20 24",
        ['X'] = "00 24|04 20",
        ['Y'] = "04 12 24|12 10",
        ['Z'] = "04 24 00 20",
        ['0'] = "00 04 24 20 00",
        ['1'] = "03 14 10|00 20",
        ['2'] = "04 24 22 02 00 20",
        ['3'] = "04 24 20 00|02 22",
        ['4'] = "04 02 22|24 20",
        ['5'] = "24 04 02 22 20 00",
        ['6'] = "24 04 00 20 22 02",
        ['7'] = "04 24 10",
        ['8'] = "00 04 24 20 00|02 22",
        ['9'] = "20 24 04 02 22",
        ['-'] = "02 22",
        // A small u, for container sizes in cargo units ("16u"): a cup in the lower half.
        ['u'] = "02 00 20 22",
    };

    /// <summary>True when the font has a glyph for <paramref name="c"/>.</summary>
    public static bool Has(char c) => Glyphs.ContainsKey(c);

    /// <summary>The glyph's polylines, or none for a character the font lacks (it is left blank).</summary>
    public static string Of(char c) => Glyphs.TryGetValue(c, out var strokes) ? strokes : "";
}
