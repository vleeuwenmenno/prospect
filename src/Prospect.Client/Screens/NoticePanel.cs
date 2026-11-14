using Brutal.ImGuiApi;
using Brutal.Numerics;
using Prospect.Client.Ui;

namespace Prospect.Client.Screens;

/// <summary>
/// The notices as they are drawn: a stack of small muted plates at the top right, newest on top. Heights are
/// measured and the stack drawn from the same figures, so its rectangle is known before it is drawn and the map can
/// keep its own gestures off it. One Dear ImGui window without keyboard navigation holds the stack, so the keys still
/// fly; while the pointer is hidden for flight it takes no input at all. Nothing here allocates.
/// </summary>
internal static class NoticePanel
{
    private const float Pad = Theme.Space3;
    private const float CloseZone = 28f;
    private const float BarWidth = 3f;
    private const float MinHeight = 36f;
    private const float TimerHeight = 2f;

    private const ImGuiWindowFlags Flags = (ScreenHost.RootFlags & ~ImGuiWindowFlags.NoBringToFrontOnFocus)
        | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoNav;

    /// <summary>What a frame's drawing found: the row whose X was pressed and the row whose body was pressed, or -1.</summary>
    public readonly record struct Result(int ClosedRow, int ActionRow);

    /// <summary>The width of the stack in pixels at the interface scale the kit holds, never wider than the room left of <paramref name="right"/> allows.</summary>
    public static float Width(NoticeCatalogue catalogue, float right) =>
        Math.Min(Kit.Px(catalogue.Width), Math.Max(Kit.Px(160), right - Kit.Px(Theme.Space5)));

    /// <summary>
    /// Fills <paramref name="heights"/> with each shown row's height and returns the stack's rectangle: its right edge
    /// at <paramref name="right"/> and its top at <paramref name="top"/>. Rows that would run below
    /// <paramref name="bottom"/> are not shown (<see cref="NoticeBoard.Limit"/>), but one always is. Empty when nothing
    /// is shown.
    /// </summary>
    public static HudRect Layout(NoticeBoard board, float right, float top, float bottom, Span<float> heights)
    {
        var width = Width(board.Catalogue, right);
        var wrap = TextWrap(width);
        var gap = Kit.Px(Theme.Space2);
        var total = 0f;
        for (var limit = NoticeBoard.MaxVisible; limit >= 1; limit--)
        {
            board.Limit = limit;
            var rows = board.Visible;
            if (rows == 0)
                return default;
            total = 0f;
            for (var row = 0; row < rows; row++)
            {
                var text = TextHeight(Kit.Ui.Sans, board.NoticeAt(row).TextBytes, wrap);
                heights[row] = Math.Max(Kit.Px(MinHeight), text + Kit.Px(Pad) * 2);
                total += heights[row] + (row > 0 ? gap : 0f);
            }

            if (top + total <= bottom)
                break;
        }

        return new HudRect(right - width, top, right, top + total);
    }

    private static float TextWrap(float width) => width - Kit.Px(Pad) * 2 - Kit.Px(BarWidth) - Kit.Px(CloseZone) + Kit.Px(Pad);

    private static float TextHeight(ImFontPtr font, ReadOnlySpan<byte> text, float wrap)
    {
        ImGui.PushFont(font, Theme.FontSmall);
        var measured = ImGui.CalcTextSize(text, false, wrap).Y;
        ImGui.PopFont();
        return measured;
    }

    /// <summary>
    /// Draws the stack in <paramref name="rect"/>, with <paramref name="heights"/> from <see cref="Layout"/>, and tells
    /// the board which notice the pointer is over. <paramref name="interactive"/> is false while the pointer is hidden:
    /// the window then takes no input. <paramref name="claim"/> is called after the window begins, for a surface under
    /// it that must hand the pointer over (the map). Presses are reported, not applied, so the caller decides what an
    /// action does.
    /// </summary>
    public static Result Draw(NoticeBoard board, in HudRect rect, ReadOnlySpan<float> heights, bool interactive, Action? claim = null)
    {
        var ui = Kit.Ui;
        var min = new float2(rect.Left, rect.Top);
        var size = new float2(rect.Width, rect.Height);
        ImGui.SetNextWindowPos(min);
        ImGui.SetNextWindowSize(size);
        ImGui.Begin("##notices"u8, interactive ? Flags : Flags | ImGuiWindowFlags.NoInputs);
        claim?.Invoke();
        // Whatever opened since, the notices stay on top of it: the map, a pinned window.
        ImGui.Internal.BringWindowToDisplayFront(ImGui.Internal.GetCurrentWindow());
        var draw = ImGui.GetWindowDrawList();
        var previousFade = Kit.Fade;
        var pad = Kit.Px(Pad);
        var gap = Kit.Px(Theme.Space2);
        var closeZone = Kit.Px(CloseZone);
        var closed = -1;
        var acted = -1;
        var y = rect.Top;
        for (var row = 0; row < board.Visible; row++)
        {
            var notice = board.NoticeAt(row);
            var rowMin = new float2(rect.Left, y);
            var rowSize = new float2(rect.Width, heights[row]);
            var rowMax = rowMin + rowSize;
            y += heights[row] + gap;

            var hovered = false;
            var pressed = false;
            var close = false;
            if (interactive)
            {
                ImGui.PushID(row);
                ImGui.SetCursorScreenPos(rowMin);
                pressed = ImGui.InvisibleButton("##body"u8, new float2(rowSize.X - closeZone, rowSize.Y));
                hovered = ImGui.IsItemHovered();
                if (hovered && notice.Action != NoticeAction.None)
                    ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                ImGui.SetCursorScreenPos(new float2(rowMax.X - closeZone, rowMin.Y));
                close = ImGui.InvisibleButton("##close"u8, new float2(closeZone, rowSize.Y));
                hovered |= ImGui.IsItemHovered();
                ImGui.PopID();
            }

            Kit.Fade = previousFade * board.AlphaAt(row);
            var chamfer = Kit.Px(Theme.Chamfer);
            Kit.ChamferFill(draw, rowMin, rowMax, Kit.Color(Theme.Panel, 0.9f), chamfer);
            Kit.ChamferOutline(draw, rowMin, rowMax, Kit.Color(Hud.Border(notice.Accent, false)), chamfer);
            draw.AddRectFilled(rowMin + new float2(0, chamfer), new float2(rowMin.X + Kit.Px(BarWidth), rowMax.Y - chamfer), Kit.Color(notice.Accent, 0.8f));

            var textMin = new float2(MathF.Round(rowMin.X + Kit.Px(BarWidth) + pad), MathF.Round(rowMin.Y + pad));
            ImGui.PushFont(ui.Sans, Theme.FontSmall);
            draw.AddText(ui.Sans, ImGui.GetFontSize(), textMin, Kit.Color(hovered ? Theme.Text : Theme.TextDim), notice.TextBytes, TextWrap(rect.Width));
            ImGui.PopFont();

            // The X, in the zone at the right: two strokes, brighter while the pointer is over the notice.
            var centre = new float2(rowMax.X - closeZone * 0.5f, rowMin.Y + pad + Kit.Px(7));
            var arm = Kit.Px(4);
            var ink = Kit.Color(hovered ? Theme.Text : Theme.TextMute);
            draw.AddLine(centre - new float2(arm, arm), centre + new float2(arm, arm), ink, Kit.Px(1.2f));
            draw.AddLine(centre - new float2(arm, -arm), centre + new float2(arm, -arm), ink, Kit.Px(1.2f));

            // What is left of the notice's time: a hair along the bottom, so a held notice shows it is held.
            var left = Math.Clamp(1f - board.AgeAt(row) / notice.Seconds, 0f, 1f);
            var timerMin = new float2(rowMin.X + chamfer, rowMax.Y - Kit.Px(TimerHeight));
            draw.AddRectFilled(timerMin, new float2(timerMin.X + (rowSize.X - chamfer * 2) * left, rowMax.Y), Kit.Color(notice.Accent, hovered ? 0.6f : 0.3f));

            if (hovered)
                board.Hover(row);
            if (close)
                closed = row;
            else if (pressed && notice.Action != NoticeAction.None)
                acted = row;
        }

        Kit.Fade = previousFade;
        ImGui.End();
        return new Result(closed, acted);
    }
}
