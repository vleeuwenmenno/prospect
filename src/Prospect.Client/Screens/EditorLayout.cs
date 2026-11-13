using Brutal.Numerics;
using Prospect.Client.Ui;

namespace Prospect.Client.Screens;

/// <summary>
/// Where the ship editor's panels go: the parts on the left, the selection and the ship's figures on the right,
/// and a bar along the bottom with the status and the Undo, Redo, Cancel and Apply buttons. The middle of the view
/// stays clear for the ship. Pure arithmetic, so the layout can be checked at any size and interface scale.
/// </summary>
internal readonly record struct EditorLayout(HudRect Parts, HudRect Details, HudRect Bar)
{
    /// <summary>The parts panel's width in design pixels, before it is narrowed to fit the window.</summary>
    public const float PartsWidth = 280f;

    /// <summary>The details panel's width in design pixels, before it is narrowed to fit the window.</summary>
    public const float DetailsWidth = 300f;

    /// <summary>The bottom bar's height and its widest, in design pixels.</summary>
    public const float BarHeight = 64f;
    public const float MaxBarWidth = 980f;

    /// <summary>The share of the window's width each side panel may take at most.</summary>
    public const float PartsShare = 0.28f;
    public const float DetailsShare = 0.27f;

    /// <summary>The bar's buttons, in design pixels: Undo, Redo, Cancel and Apply, with the gaps between them.</summary>
    public static ReadOnlySpan<float> ButtonWidths => [72f, 72f, 84f, 100f];
    public const float ButtonGap = 8f;
    public const float ButtonHeight = 34f;
    public const float Pad = 12f;

    /// <summary>The layout for a window of <paramref name="display"/> interface units at an interface scale.</summary>
    public static EditorLayout Of(float2 display, float scale)
    {
        float Px(float design) => MathF.Round(design * scale);
        var margin = Math.Min(Px(HudLayout.Margin), MathF.Round(display.X * 0.02f));
        var gap = Px(HudLayout.Gap);
        var top = HudLayout.Top(scale) + gap;
        var barWidth = Math.Min(display.X - margin * 2, Px(MaxBarWidth));
        var barLeft = MathF.Round((display.X - barWidth) * 0.5f);
        var bar = new HudRect(barLeft, display.Y - margin - Px(BarHeight), barLeft + barWidth, display.Y - margin);
        var bottom = bar.Top - gap;
        var parts = Math.Min(Px(PartsWidth), MathF.Round(display.X * PartsShare));
        var details = Math.Min(Px(DetailsWidth), MathF.Round(display.X * DetailsShare));
        return new EditorLayout(
            new HudRect(margin, top, margin + parts, bottom),
            new HudRect(display.X - margin - details, top, display.X - margin, bottom),
            bar);
    }

    /// <summary>
    /// How many of the part list's tabs go in a row <paramref name="width"/> wide: four, or fewer when the widest
    /// name would not fit its tab with two pixels either side.
    /// </summary>
    public static int TabsPerRow(float width, float widest, float gap, float scale)
    {
        for (var count = 4; count > 1; count--)
            if (TabWidth(width, count, gap) - MathF.Round(4 * scale) >= widest)
                return count;
        return 1;
    }

    /// <summary>The width of one tab when <paramref name="count"/> share a row.</summary>
    public static float TabWidth(float width, int count, float gap) => MathF.Floor((width - gap * (count - 1)) / count);

    /// <summary>The width the bar's buttons take, gaps and the bar's padding included, at an interface scale.</summary>
    public static float ButtonsWidth(float scale)
    {
        var width = 0f;
        foreach (var button in ButtonWidths)
            width += MathF.Round(button * scale) + MathF.Round(ButtonGap * scale);
        return width + MathF.Round(Pad * scale);
    }
}
