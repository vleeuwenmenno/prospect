using Brutal.Numerics;
using Prospect.Client.Ui;

namespace Prospect.Client.Screens;

/// <summary>
/// Where a tip panel goes, as pure arithmetic so it can be checked at any size and interface scale. Against the
/// screen a tip sits inside its edge or corner; against a panel it sits outside it, on the named side, centred
/// along it; against a point (the pointer, a target) it sits beside the point, flipped to the other side when it
/// would run off. Whatever the anchor says, the result stays on screen, under the navigation bar and inside the
/// margins, as a floating window does.
/// </summary>
internal static class TipPlacement
{
    /// <summary>The gap between a tip and the panel it is placed against, and from a point, in unscaled pixels.</summary>
    public const float Gap = 10f;
    public const float PointGap = 18f;

    /// <summary>
    /// The top left corner of a tip of <paramref name="size"/> pixels. <paramref name="area"/> is the region as
    /// the caller found it this frame; <paramref name="known"/> is false when it is not there (an editor panel with
    /// no editor open, a target that is gone), and the tip then takes the top right of the screen.
    /// </summary>
    public static float2 Place(in TipAnchor anchor, bool known, in HudRect area, float2 size, float2 display, float scale, float2 shift = default) =>
        PanelShift.Place(Home(anchor, known, area, size, display, scale), size, display, scale, new float2(anchor.OffsetX + shift.X, anchor.OffsetY + shift.Y));

    /// <summary>Where the anchor puts the tip before its own offset and the player's shift: not yet clamped to the screen.</summary>
    private static float2 Home(in TipAnchor anchor, bool known, in HudRect area, float2 size, float2 display, float scale)
    {
        float Px(float design) => MathF.Round(design * scale);
        var margin = Px(FloatingWindow.Margin);
        var top = HudLayout.TopBarBottom(scale) + margin;
        var screen = new HudRect(margin, top, Math.Max(margin, display.X - margin), Math.Max(top, display.Y - margin));

        var region = anchor.Region;
        var side = anchor.Side;
        if (region is not TipRegion.Screen && !known)
        {
            region = TipRegion.Screen;
            side = TipSide.TopRight;
        }

        float2 at;
        if (region == TipRegion.Screen)
        {
            at = Inside(screen, side, size);
        }
        else
        {
            var gap = Px(region is TipRegion.Cursor or TipRegion.Target ? PointGap : Gap);
            at = Outside(area, side, size, gap);
            // Beside a panel or point, a tip that would leave the screen takes the opposite side of it instead of
            // covering what it points at.
            if (at.X < screen.Left || at.X + size.X > screen.Right)
                at = Outside(area, Mirror(side, true), size, gap);
            if (at.Y < screen.Top || at.Y + size.Y > screen.Bottom)
                at = Outside(area, Mirror(side, false), size, gap);
        }

        return at;
    }

    private static readonly int SideCount = Enum.GetValues<TipSide>().Length;
    private static readonly string[] Keys = BuildKeys();

    private static string[] BuildKeys()
    {
        var keys = new string[Enum.GetValues<TipRegion>().Length * SideCount];
        for (var i = 0; i < keys.Length; i++)
            keys[i] = $"{(TipRegion)(i / SideCount)}.{(TipSide)(i % SideCount)}";
        return keys;
    }

    /// <summary>
    /// The name a dragged position is saved under: the anchor's region and side, such as "Screen.TopRight". Tips
    /// that share an anchor share where the player put them. Built once; a lookup never allocates.
    /// </summary>
    public static string KeyOf(in TipAnchor anchor) => Keys[(int)anchor.Region * SideCount + (int)anchor.Side];

    /// <summary>
    /// The shift, in unscaled pixels, that carries a tip now at <paramref name="shift"/> by
    /// <paramref name="delta"/> screen pixels, by the rule every draggable panel shares (<see cref="PanelShift"/>).
    /// </summary>
    public static float2 Dragged(in TipAnchor anchor, bool known, in HudRect area, float2 size, float2 display, float scale, float2 shift, float2 delta) =>
        PanelShift.Dragged(Home(anchor, known, area, size, display, scale), size, display, scale, shift, delta, new float2(anchor.OffsetX, anchor.OffsetY));

    private static float2 Inside(in HudRect area, TipSide side, float2 size)
    {
        var x = side switch
        {
            TipSide.TopLeft or TipSide.Left or TipSide.BottomLeft => area.Left,
            TipSide.TopRight or TipSide.Right or TipSide.BottomRight => area.Right - size.X,
            _ => MathF.Round((area.Left + area.Right - size.X) * 0.5f),
        };
        var y = side switch
        {
            TipSide.TopLeft or TipSide.Top or TipSide.TopRight => area.Top,
            TipSide.BottomLeft or TipSide.Bottom or TipSide.BottomRight => area.Bottom - size.Y,
            _ => MathF.Round((area.Top + area.Bottom - size.Y) * 0.5f),
        };
        return new float2(x, y);
    }

    private static float2 Outside(in HudRect area, TipSide side, float2 size, float gap)
    {
        var middleX = MathF.Round((area.Left + area.Right - size.X) * 0.5f);
        var middleY = MathF.Round((area.Top + area.Bottom - size.Y) * 0.5f);
        var x = side switch
        {
            TipSide.TopLeft or TipSide.Left or TipSide.BottomLeft => area.Left - gap - size.X,
            TipSide.TopRight or TipSide.Right or TipSide.BottomRight => area.Right + gap,
            _ => middleX,
        };
        var y = side switch
        {
            TipSide.TopLeft or TipSide.Top or TipSide.TopRight => area.Top - gap - size.Y,
            TipSide.BottomLeft or TipSide.Bottom or TipSide.BottomRight => area.Bottom + gap,
            _ => middleY,
        };
        return new float2(x, y);
    }

    /// <summary>The other side along one axis: left and right swap, or top and bottom do.</summary>
    private static TipSide Mirror(TipSide side, bool horizontal) => horizontal
        ? side switch
        {
            TipSide.TopLeft => TipSide.TopRight,
            TipSide.TopRight => TipSide.TopLeft,
            TipSide.Left => TipSide.Right,
            TipSide.Right => TipSide.Left,
            TipSide.BottomLeft => TipSide.BottomRight,
            TipSide.BottomRight => TipSide.BottomLeft,
            _ => side,
        }
        : side switch
        {
            TipSide.TopLeft => TipSide.BottomLeft,
            TipSide.BottomLeft => TipSide.TopLeft,
            TipSide.Top => TipSide.Bottom,
            TipSide.Bottom => TipSide.Top,
            TipSide.TopRight => TipSide.BottomRight,
            TipSide.BottomRight => TipSide.TopRight,
            _ => side,
        };
}
