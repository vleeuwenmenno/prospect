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

    /// <summary>The share of the window's width each side panel may take at most; the parts panel's includes the category rail.</summary>
    public const float PartsShare = 0.345f;
    public const float DetailsShare = 0.27f;

    /// <summary>The bar's buttons, in design pixels: Undo, Redo, Cancel and Apply, with the gaps between them.</summary>
    public static ReadOnlySpan<float> ButtonWidths => [72f, 72f, 84f, 100f];

    /// <summary>The two buttons before them, in design pixels: Save... and Load..., which open the saved designs dialogs.</summary>
    public static ReadOnlySpan<float> DesignButtonWidths => [78f, 78f];
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
        // The rail sits beside the part list, so it is added to the panel's width and its share of the window.
        var parts = Math.Min(Px(PartsWidth + RailWidth + RailGap), MathF.Round(display.X * PartsShare));
        var details = Math.Min(Px(DetailsWidth), MathF.Round(display.X * DetailsShare));
        return new EditorLayout(
            new HudRect(margin, top, margin + parts, bottom),
            new HudRect(display.X - margin - details, top, display.X - margin, bottom),
            bar);
    }

    /// <summary>The category rail's width and the gap between it and the part list, in design pixels.</summary>
    public const float RailWidth = 34f;
    public const float RailGap = 8f;

    /// <summary>The tallest an icon button on the rail is, and the gap between buttons, in design pixels.</summary>
    public const float RailButton = 34f;
    public const float RailButtonGap = 4f;

    /// <summary>The shortest a rail button gets in a short window, in design pixels.</summary>
    public const float RailMinButton = 12f;

    /// <summary>
    /// The height of each rail button for <paramref name="count"/> buttons in <paramref name="height"/> pixels: the
    /// full size, or smaller when a short window would otherwise cut the last one off. Never under <see cref="RailMinButton"/>.
    /// </summary>
    public static float RailButtonHeight(float height, int count, float scale)
    {
        var gaps = MathF.Round(RailButtonGap * scale) * Math.Max(count - 1, 0);
        var room = count > 0 ? (height - gaps) / count : height;
        return Math.Clamp(MathF.Floor(room), MathF.Round(RailMinButton * scale), MathF.Round(RailButton * scale));
    }

    /// <summary>The width the bar's buttons take, gaps and the bar's padding included, at an interface scale.</summary>
    public static float ButtonsWidth(float scale)
    {
        var width = 0f;
        foreach (var button in ButtonWidths)
            width += MathF.Round(button * scale) + MathF.Round(ButtonGap * scale);
        return width + MathF.Round(Pad * scale);
    }

    /// <summary>The design buttons' width with their gaps, at an interface scale.</summary>
    public static float DesignButtonsWidth(float scale)
    {
        var width = 0f;
        foreach (var button in DesignButtonWidths)
            width += MathF.Round(button * scale) + MathF.Round(ButtonGap * scale);
        return width;
    }

    /// <summary>
    /// Whether the bar, <paramref name="barWidth"/> wide, has room for Save... and Load... as well: it keeps 200 design
    /// pixels for the status line. A window too narrow for that shows the four editing buttons only.
    /// </summary>
    public static bool DesignButtonsFit(float barWidth, float scale) =>
        ButtonsWidth(scale) + DesignButtonsWidth(scale) + MathF.Round(200f * scale) <= barWidth;
}
