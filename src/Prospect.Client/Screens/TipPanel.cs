using Brutal.ImGuiApi;
using Brutal.Numerics;
using Prospect.Client.Ui;

namespace Prospect.Client.Screens;

/// <summary>
/// The panel one tip is shown in: its title, a counter when several wait, the text, the "Don't show this again"
/// tick, and Next and Close. It is one Dear ImGui window without keyboard navigation, so W, Space and the rest still
/// fly; while the pointer is hidden for flight it takes no input at all and a line says how to reach the buttons.
/// The size is measured and the panel drawn from the same figures, so a tip's place is known before it is drawn
/// and anything under it can give way. Nothing here allocates.
/// </summary>
internal static class TipPanel
{
    private const float Pad = Theme.Space3;
    private const float ButtonHeight = 28f;
    private const float CheckBox = 14f;
    private const float CheckRow = 22f;

    private const ImGuiWindowFlags Flags = (ScreenHost.RootFlags & ~ImGuiWindowFlags.NoBringToFrontOnFocus)
        | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoNav;

    /// <summary>The panel's size in pixels for <paramref name="tip"/>, at the interface scale the kit holds.</summary>
    public static float2 Measure(TipCatalogue catalogue, Tip tip, float displayWidth)
    {
        var ui = Kit.Ui;
        var width = Width(catalogue, displayWidth);
        var title = Kit.Measure(ui.SansMedium, Theme.FontBody, tip.TitleBytes).Y;
        var text = TextHeight(ui.Sans, tip.TextBytes, width - Kit.Px(Pad) * 2);
        var height = Kit.Px(Pad) + title + Kit.Px(Theme.Space2) + text + Kit.Px(Theme.Space3) + Kit.Px(CheckRow)
            + Kit.Px(Theme.Space2) + Kit.Px(ButtonHeight) + Kit.Px(Pad);
        return new float2(width, height);
    }

    /// <summary>Where the tick's row and the two buttons are in a panel at <paramref name="rect"/>; Next only when several tips wait.</summary>
    public readonly record struct Rows(HudRect Check, HudRect Close, HudRect Next);

    /// <summary>
    /// The interactive rows, measured up from the panel's bottom edge, so the panel and a test place them from the
    /// same figures.
    /// </summary>
    public static Rows RowsOf(TipCatalogue catalogue, bool several, in HudRect rect)
    {
        var ui = Kit.Ui;
        var pad = Kit.Px(Pad);
        var buttons = rect.Bottom - pad - Kit.Px(ButtonHeight);
        var check = buttons - Kit.Px(Theme.Space2) - Kit.Px(CheckRow);
        var right = rect.Right - pad;
        var closeWidth = ButtonWidth(ui, catalogue.Close);
        var close = new HudRect(right - closeWidth, buttons, right, buttons + Kit.Px(ButtonHeight));
        var next = default(HudRect);
        if (several)
        {
            var nextRight = close.Left - Kit.Px(Theme.Space2);
            next = new HudRect(nextRight - ButtonWidth(ui, catalogue.Next), buttons, nextRight, close.Bottom);
        }

        return new Rows(new HudRect(rect.Left + pad, check, rect.Right - pad, check + Kit.Px(CheckRow)), close, next);
    }

    private static float ButtonWidth(UiContext ui, ReadOnlySpan<byte> label) =>
        Kit.Measure(ui.SansMedium, Theme.FontSmall, label).X + Kit.Px(Theme.Space4) * 2;

    private static float Width(TipCatalogue catalogue, float displayWidth) =>
        Math.Min(Kit.Px(catalogue.PanelWidth), Math.Max(Kit.Px(200), displayWidth - Kit.Px(FloatingWindow.Margin) * 2));

    private static float TextHeight(ImFontPtr font, ReadOnlySpan<byte> text, float wrap)
    {
        ImGui.PushFont(font, Theme.FontSmall);
        var measured = ImGui.CalcTextSize(text, false, wrap).Y;
        ImGui.PopFont();
        return measured;
    }

    /// <summary>
    /// Draws the tip the board shows in <paramref name="rect"/> and applies a press on Next, Close or the tick to
    /// the board. <paramref name="interactive"/> is false while the pointer is hidden: the window then takes no
    /// input and shows the hint in place of the buttons. <paramref name="claim"/> is called after the window
    /// begins, for a surface under it that must hand the pointer over (the map).
    /// </summary>
    public static void Draw(TipBoard board, TipCatalogue catalogue, in HudRect rect, bool interactive, Action? claim = null)
    {
        var ui = Kit.Ui;
        var tip = board.Current;
        var min = new float2(rect.Left, rect.Top);
        var size = new float2(rect.Width, rect.Height);
        var max = min + size;

        ImGui.SetNextWindowPos(min);
        ImGui.SetNextWindowSize(size);
        ImGui.Begin("##tip"u8, interactive ? Flags : Flags | ImGuiWindowFlags.NoInputs);
        claim?.Invoke();
        // Whatever opened since, the tip stays on top of it: the map, a pinned window. A window already in front
        // is left where it is.
        ImGui.Internal.BringWindowToDisplayFront(ImGui.Internal.GetCurrentWindow());
        var draw = ImGui.GetWindowDrawList();
        var pad = Kit.Px(Pad);
        var inner = size.X - pad * 2;
        Hud.Plate(draw, min, max, Theme.Accent, true);

        // Title, and the counter at the right when more than one tip waits.
        var y = min.Y + pad;
        var title = Kit.Text(draw, ui.SansMedium, Theme.FontBody, new float2(min.X + pad, y), Theme.Text, tip.TitleBytes);
        if (board.Count > 1)
        {
            var counter = Kit.F($"{board.Index + 1}/{board.Count}");
            var counterSize = Kit.Measure(ui.Mono, Theme.FontCaption, counter);
            Kit.Text(draw, ui.Mono, Theme.FontCaption, new float2(max.X - pad - counterSize.X, y + (title.Y - counterSize.Y) * 0.5f), Theme.Accent, counter);
        }

        y += title.Y + Kit.Px(Theme.Space2);

        // The text, wrapped to the width the measure used.
        ImGui.PushFont(ui.Sans, Theme.FontSmall);
        var textHeight = ImGui.CalcTextSize(tip.TextBytes, false, inner).Y;
        draw.AddText(ui.Sans, ImGui.GetFontSize(), new float2(MathF.Round(min.X + pad), MathF.Round(y)), Kit.Color(Theme.TextDim), tip.TextBytes, inner);
        ImGui.PopFont();

        // The tick.
        var rows = RowsOf(catalogue, board.Count > 1, rect);
        var dim = interactive ? 1f : 0.5f;
        var rowMin = new float2(rows.Check.Left, rows.Check.Top);
        var rowSize = new float2(rows.Check.Width, rows.Check.Height);
        var toggled = false;
        if (interactive)
        {
            ImGui.SetCursorScreenPos(rowMin);
            toggled = ImGui.InvisibleButton("##tipcheck"u8, rowSize);
        }

        var checkHot = interactive && ImGui.IsItemHovered();
        var box = Kit.Px(CheckBox);
        var boxMin = new float2(rowMin.X, rowMin.Y + MathF.Round((rowSize.Y - box) * 0.5f));
        draw.AddRectFilled(boxMin, boxMin + new float2(box, box), Kit.Color(Theme.Void, 0.8f));
        draw.AddRect(boxMin, boxMin + new float2(box, box), Kit.Color(board.DontShowAgain || checkHot ? Theme.Accent : Theme.LineStrong, dim));
        if (board.DontShowAgain)
        {
            var inset = Kit.Px(3);
            draw.AddRectFilled(boxMin + new float2(inset, inset), boxMin + new float2(box - inset, box - inset), Kit.Color(Theme.Accent, dim));
        }

        var label = Kit.Measure(ui.Sans, Theme.FontSmall, catalogue.DontShowAgain);
        Kit.Text(draw, ui.Sans, Theme.FontSmall, new float2(boxMin.X + box + Kit.Px(Theme.Space2), rowMin.Y + (rowSize.Y - label.Y) * 0.5f),
            checkHot ? Theme.Text : Theme.TextDim, catalogue.DontShowAgain, dim);

        // Next and Close at the right; with the pointer hidden, the line that says how to use them.
        var pressedNext = false;
        var pressedClose = false;
        if (interactive)
        {
            pressedClose = Button("##tipclose"u8, catalogue.Close, rows.Close, false);
            if (board.Count > 1)
                pressedNext = Button("##tipnext"u8, catalogue.Next, rows.Next, true);
        }
        else
        {
            var hint = Kit.Measure(ui.Sans, Theme.FontCaption, catalogue.CapturedHint);
            Kit.ClippedText(draw, ui.Sans, Theme.FontCaption, new float2(min.X + pad, rows.Close.Top + (rows.Close.Height - hint.Y) * 0.5f), Theme.TextMute, catalogue.CapturedHint, inner);
        }

        ImGui.End();

        // Applied last, so the panel above was drawn from the tip that was shown when it was measured.
        if (toggled)
            board.DontShowAgain = !board.DontShowAgain;
        if (pressedNext)
            board.Next();
        else if (pressedClose)
            board.Close();
    }

    /// <summary>One small button in <paramref name="area"/>.</summary>
    private static bool Button(ImString id, ReadOnlySpan<byte> label, in HudRect area, bool primary)
    {
        var ui = Kit.Ui;
        var text = Kit.Measure(ui.SansMedium, Theme.FontSmall, label);
        var min = new float2(area.Left, area.Top);
        var size = new float2(area.Width, area.Height);
        ImGui.SetCursorScreenPos(min);
        var pressed = ImGui.InvisibleButton(id, size);
        var held = ImGui.IsItemActive();
        var hot = Kit.Animate(ImGui.GetItemID(), Kit.IsHot() ? 1f : 0f);
        var draw = ImGui.GetWindowDrawList();
        var max = min + size;
        var chamfer = Kit.Px(5);
        uint ink;
        if (primary)
        {
            Kit.ChamferFill(draw, min, max, Kit.Color(held ? Theme.Accent : Kit.Mix(Theme.Accent, Theme.AccentBright, hot)), chamfer);
            ink = Theme.AccentInk;
        }
        else
        {
            Kit.ChamferFill(draw, min, max, Kit.Color(Kit.Mix(Theme.Panel, Theme.PanelRaised, hot)), chamfer);
            Kit.ChamferOutline(draw, min, max, Kit.Color(Kit.Mix(Theme.Line, Theme.Accent, hot)), chamfer);
            ink = Kit.Mix(Theme.TextDim, Theme.Text, hot);
        }

        Kit.Text(draw, ui.SansMedium, Theme.FontSmall, min + (size - text) * 0.5f, ink, label);
        return pressed;
    }
}
