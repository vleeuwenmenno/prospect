using Brutal.ImGuiApi;
using Brutal.Numerics;
using Prospect.Client.Ui;
using Prospect.Game.Client;
using Prospect.Game.Protocol;
using Prospect.Game.World;

namespace Prospect.Client.Screens;

internal sealed partial class ScreenHost
{
    private StatusTuning? _statusTuning;

    /// <summary>How the status icons look, loaded once from <c>content/visuals/status.json</c>.</summary>
    private StatusTuning StatusLook => _statusTuning ??= StatusTuning.Load();

    /// <summary>
    /// A hull's status icons side by side from <paramref name="left"/>, centred on <paramref name="middle"/> in
    /// height: a bolt for a lightning cloud (pulsing with a ring while a burst disrupts sensors) and a snowflake for a
    /// cold cloud. Returns the width drawn. The flags are the snapshot's, so nothing here follows the drawn pose.
    /// </summary>
    private float DrawStatusIcons(ImDrawListPtr draw, HullEnvironment environment, float left, float middle)
    {
        var look = StatusLook;
        var size = Kit.Px(look.IconSize);
        var gap = Kit.Px(look.IconGap);
        for (var slot = 0; slot < HullStatus.Count(environment); slot++)
            DrawStatusIcon(draw, environment, slot, new float2(left + slot * (size + gap) + size * 0.5f, middle), size, look);
        return HullStatus.IconsWidth(environment, size, gap);
    }

    private void DrawStatusIcon(ImDrawListPtr draw, HullEnvironment environment, int slot, float2 center, float size, StatusTuning look)
    {
        var icon = HullStatus.Icon(environment, slot);
        var rgb = HullStatus.Rgb(look, icon);
        var alpha = HullStatus.Alpha(look, environment, slot, _ui.Time);
        var half = size * 0.5f;
        var color = Kit.Color(rgb, alpha);
        var width = Kit.Px(1.8f);
        if (icon == StatusIcon.Lightning)
        {
            if (HullStatus.Bursting(environment, slot))
                draw.AddCircle(center, half * 1.45f, Kit.Color(rgb, look.BurstRingAlpha * (0.4f + 0.6f * HullStatus.Pulse(look, _ui.Time))), 20, Kit.Px(1f));
            Span<float2> bolt =
            [
                center + new float2(half * 0.35f, -half),
                center + new float2(-half * 0.45f, half * 0.1f),
                center + new float2(half * 0.2f, half * 0.1f),
                center + new float2(-half * 0.35f, half),
            ];
            draw.AddPolyline(bolt, color, ImDrawFlags.None, width);
            return;
        }

        // Three crossing arms with a short barb a little way in from each end.
        var reach = half * 0.95f;
        for (var arm = 0; arm < 3; arm++)
        {
            var angle = arm * MathF.PI / 3f + MathF.PI / 2f;
            var along = new float2(MathF.Cos(angle), MathF.Sin(angle));
            draw.AddLine(center - along * reach, center + along * reach, color, Kit.Px(1.2f));
            for (var end = -1; end <= 1; end += 2)
            {
                var tip = center + along * (reach * 0.55f * end);
                for (var side = -1; side <= 1; side += 2)
                {
                    var branch = angle + MathF.PI / 3f * side * end + (end < 0 ? MathF.PI : 0f);
                    draw.AddLine(tip, tip + new float2(MathF.Cos(branch), MathF.Sin(branch)) * (reach * 0.3f), color, Kit.Px(1f));
                }
            }
        }
    }

    /// <summary>The width of <see cref="DrawStatusLine"/> for these flags with every label, in scaled pixels.</summary>
    private float StatusLineWidth(HullEnvironment environment)
    {
        var look = StatusLook;
        var size = Kit.Px(look.IconSize);
        var gap = Kit.Px(look.IconGap);
        var width = 0f;
        for (var slot = 0; slot < HullStatus.Count(environment); slot++)
            width += size + gap + Kit.Measure(_ui.Mono, Theme.FontCaption, HullStatus.Label(environment, slot)).X + (slot > 0 ? gap * 2 : 0f);
        return width;
    }

    /// <summary>
    /// The status icons each followed by its words (SENSORS DISRUPTED, LIGHTNING CLOUD, COLD CLOUD), from
    /// <paramref name="left"/> and no further than <paramref name="maxRight"/>. A burst's words take the icon's
    /// colour; the rest are muted. Returns the width used.
    /// </summary>
    private float DrawStatusLine(ImDrawListPtr draw, HullEnvironment environment, float left, float middle, float maxRight)
    {
        var look = StatusLook;
        var size = Kit.Px(look.IconSize);
        var gap = Kit.Px(look.IconGap);
        var x = left;
        for (var slot = 0; slot < HullStatus.Count(environment); slot++)
        {
            if (x + size > maxRight)
                break;
            DrawStatusIcon(draw, environment, slot, new float2(x + size * 0.5f, middle), size, look);
            x += size + gap;
            var tone = HullStatus.Bursting(environment, slot) ? HullStatus.Rgb(look, HullStatus.Icon(environment, slot)) : Theme.TextDim;
            x += Kit.ClippedText(draw, _ui.Mono, Theme.FontCaption, new float2(x, middle - Kit.Px(7)), tone, HullStatus.Label(environment, slot), Math.Max(0f, maxRight - x)) + gap * 2;
        }

        return Math.Max(0f, x - left - gap * 2);
    }

    /// <summary>
    /// Draws the tooltip of the icon the pointer is on, if any, and returns true when it did. The icons start at
    /// <paramref name="left"/> and are centred on <paramref name="middle"/>. Nothing is drawn while another surface
    /// owns the pointer, and the tip takes no input.
    /// </summary>
    private bool DrawStatusTip(HullEnvironment environment, float left, float middle, bool pointable)
    {
        if (!pointable || _ui.PointerOverInterface || HullStatus.Count(environment) == 0)
            return false;
        var look = StatusLook;
        var size = Kit.Px(look.IconSize);
        var gap = Kit.Px(look.IconGap);
        var slot = HullStatus.Hit(environment, left, middle, size, gap, ImGui.GetIO().MousePos);
        if (slot < 0)
            return false;

        var draw = ImGui.GetForegroundDrawList();
        var title = HullStatus.Label(environment, slot);
        var first = HullStatus.Meaning(environment, slot, 0);
        var second = HullStatus.Meaning(environment, slot, 1);
        var pad = Kit.Px(Theme.Space2);
        var titleSize = Kit.Measure(_ui.Mono, Theme.FontCaption, title);
        var firstSize = Kit.Measure(_ui.Sans, Theme.FontCaption, first);
        var secondSize = Kit.Measure(_ui.Sans, Theme.FontCaption, second);
        var width = Math.Max(titleSize.X, Math.Max(firstSize.X, secondSize.X)) + pad * 2;
        var height = titleSize.Y + firstSize.Y + secondSize.Y + pad * 2 + Kit.Px(4);
        var display = _ui.DisplaySize;
        var edge = Kit.Px(Theme.Space2);
        var x = Math.Clamp(left + slot * (size + gap) + size * 0.5f - width * 0.5f, edge, Math.Max(edge, display.X - width - edge));
        var y = middle + size * 0.5f + edge;
        if (y + height > display.Y - edge)
            y = Math.Max(edge, middle - size * 0.5f - edge - height);
        var min = new float2(x, y);
        var icon = HullStatus.Icon(environment, slot);
        Hud.Plate(draw, min, min + new float2(width, height), HullStatus.Rgb(look, icon), HullStatus.Bursting(environment, slot));
        Kit.Text(draw, _ui.Mono, Theme.FontCaption, min + new float2(pad, pad), HullStatus.Rgb(look, icon), title);
        Kit.Text(draw, _ui.Sans, Theme.FontCaption, min + new float2(pad, pad + titleSize.Y + Kit.Px(2)), Theme.TextDim, first);
        Kit.Text(draw, _ui.Sans, Theme.FontCaption, min + new float2(pad, pad + titleSize.Y + firstSize.Y + Kit.Px(2)), Theme.TextDim, second);
        return true;
    }

    /// <summary>
    /// The pilot's own status chip: a small plate above the left of the flight console naming what the ship is in
    /// (lightning cloud, sensors disrupted, cold cloud), pulsing with the icon while a burst is on. Shown only for
    /// a ship with a status. Beside the jump strip it keeps to the room left of it and drops its words for icons.
    /// </summary>
    private void DrawOwnStatusChip(ImDrawListPtr draw, in HullView own, float alpha, bool pointable)
    {
        var environment = own.Environment;
        if (HullStatus.Count(environment) == 0)
            return;
        var previous = Kit.Fade;
        Kit.Fade = alpha;
        var look = StatusLook;
        var layout = FlightConsole.Layout(_ui.DisplaySize, Kit.Scale);
        var pad = Kit.Px(Theme.Space2);
        var height = Kit.Px(HudLayout.ChipHeight);
        var left = layout.LeftX;
        var top = layout.Top - Kit.Px(Theme.Space2) - height;
        var room = _ui.DisplaySize.X - left;
        var wording = StatusLineWidth(environment) + pad * 2;
        var wordsFit = wording <= room;
        var inner = wordsFit ? wording : HullStatus.IconsWidth(environment, Kit.Px(look.IconSize), Kit.Px(look.IconGap)) + pad * 2;
        var min = new float2(left, top);
        var middle = top + height * 0.5f;
        var tone = HullStatus.Rgb(look, HullStatus.Icon(environment, 0));
        Hud.Plate(draw, min, min + new float2(inner, height), tone, HullStatus.Bursting(environment, 0));
        if (wordsFit)
            DrawStatusLine(draw, environment, left + pad, middle, left + inner - pad);
        else
            DrawStatusIcons(draw, environment, left + pad, middle);
        Kit.Fade = previous;
        if (!wordsFit)
            DrawStatusTip(environment, left + pad, middle, pointable);
    }
}
