using Brutal.ImGuiApi;
using Brutal.Numerics;
using Prospect.Client.Input;
using Prospect.Client.Ui;

namespace Prospect.Client.Screens;

internal sealed partial class ScreenHost
{
    private bool _controlsOpen;
    private bool _controlsHandled;
    private readonly ImInputString _controlsSearch = new(128);
    private ControlsCatalog _controls = null!;
    private ControlsBrowser _controlsBrowser = null!;

    private void InitializeControls()
    {
        _controls = new ControlsCatalog(_app.Actions);
        _controlsBrowser = new ControlsBrowser(_controls);
        InitializeSettingsControls();
    }

    /// <summary>
    /// A control was remapped: every name the interface built from the bindings (the tips' key names, the social
    /// buttons, the editor's hints) is built again, so nothing keeps showing the old key.
    /// </summary>
    private void OnBindingsChanged()
    {
        _playersKey = KeyName(InputAction.TogglePlayers);
        _socialKey = KeyName(InputAction.ToggleSocial);
        _chatKey = KeyName(InputAction.ToggleChat);
        _editorKey = null;
        _editorHints = null;
        _tipCatalogue = _tipCatalogue.Rebind(_app.Actions);
        _tips.ReplaceCatalogue(_tipCatalogue);
    }

    /// <summary>Consumes opening and closing shortcuts before flight reads this frame's input.</summary>
    public void HandleControlsShortcut(bool active)
    {
        _controlsHandled = false;
        if (!active || _screen != Screen.Session)
            return;
        var pointerOwned = _controlsOpen || _ui.PointerOverInterface;
        if (_app.Actions.InterfaceShortcutPressed(_app.Devices, InputAction.ToggleControls, _ui.WantsTextInput, pointerOwned)
            && (_controlsOpen || !_sessionMenuOpen))
        {
            if (_controlsOpen)
                _controlsOpen = false;
            else
                OpenControls();
            _controlsHandled = true;
        }
        else if (_controlsOpen && _screenTime > 0.1f
            && _app.Actions.InterfaceShortcutPressed(_app.Devices, InputAction.Pause, _ui.WantsTextInput, pointerOwned))
        {
            _controlsOpen = false;
            _controlsHandled = true;
        }
    }

    /// <summary>True when Controls consumed this frame's shortcut.</summary>
    public bool ControlsHandled => _controlsHandled;

    /// <summary>Opens the searchable quick reference of the current controls without changing the underlying view.</summary>
    public void OpenControls()
    {
        _controlsOpen = true;
        _controlsSearch.Clear();
        _controlsBrowser.Reset();
        _mapPointer.Reset();
    }

    /// <summary>Puts text in the quick reference's search box, for scripted captures.</summary>
    public void PreviewControlsSearch(string text, string? tab)
    {
        _controlsSearch.SetValue(text);
        _controlsBrowser.SetQuery(text);
        SelectControlsTab(_controlsBrowser, tab);
    }

    private static void SelectControlsTab(ControlsBrowser browser, string? name)
    {
        if (name is null)
            return;
        for (var i = 0; i < browser.Catalog.Tabs.Length; i++)
            if (browser.Catalog.Tabs[i].Title.StartsWith(name, StringComparison.OrdinalIgnoreCase))
                browser.Select(i);
    }

    private void DrawControls()
    {
        _controls.Refresh();
        _controlsBrowser.Recount();
        DrawDim(0.8f);
        BeginRoot("##controls"u8);
        var size = new float2(Math.Min(Kit.Px(760), _ui.DisplaySize.X - Kit.Px(24)), _ui.DisplaySize.Y - Kit.Px(24));
        BeginPanel("##controlspanel"u8, (_ui.DisplaySize - size) * 0.5f, size);
        Kit.Heading("Controls"u8);
        Kit.Space(Theme.Space1);
        Kit.Paragraph(_ui.Sans, Theme.FontSmall, Theme.TextMute, "A quick reference of the controls as they are now. To change one, open Settings, Controls."u8, size.X / Kit.Scale - Theme.Space6 * 2 - 14);
        Kit.Space(Theme.Space2);
        if (Kit.Button("Close"u8, ButtonKind.Secondary, 100))
            _controlsOpen = false;
        ImGui.SameLine(0, Kit.Px(Theme.Space2));
        if (Kit.Button("Change controls"u8, ButtonKind.Secondary, 170))
        {
            _controlsOpen = false;
            OpenSettings(Screen.Session);
            _settingsControlsPage = true;
        }

        Kit.Space(Theme.Space2);
        DrawControlsSearchBox(_controlsBrowser, _controlsSearch, "##controlssearch"u8);
        Kit.Space(Theme.Space2);
        var width = ImGui.GetContentRegionAvail().X;
        DrawControlTabs(_controlsBrowser, width);
        Kit.Space(Theme.Space3);
        ImGui.BeginChild("##controlsrows"u8, float2.Zero, ImGuiChildFlags.None, ImGuiWindowFlags.NoBackground);
        var rowWidth = ImGui.GetContentRegionAvail().X - Kit.Px(Theme.Space3);
        foreach (var row in _controlsBrowser.Catalog.Tabs[_controlsBrowser.Selected].Rows)
            DrawReferenceRow(_controlsBrowser, row, rowWidth, fixedRow: false);
        ImGui.EndChild();
        EndPanel();
        EndRoot();
    }

    /// <summary>The search box shared by both Controls screens; the browser follows what is typed.</summary>
    private void DrawControlsSearchBox(ControlsBrowser browser, ImInputString buffer, ImString id)
    {
        Kit.Caption("SEARCH ACTION, KEY, BUTTON OR CATEGORY"u8);
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
        if (ImGui.InputText(id, buffer.Buffer, ImGuiInputTextFlags.NoUndoRedo))
        {
            buffer.EvaluateLength();
            browser.SetQuery(buffer.ToString());
        }

        if (browser.Searching)
        {
            var total = browser.TotalMatches;
            Kit.Caption(total == 0 ? "NO MATCHING CONTROLS"u8 : Kit.F($"{total} {(total == 1 ? "MATCH" : "MATCHES")} IN {CountTabsWithMatches(browser)} {(CountTabsWithMatches(browser) == 1 ? "TAB" : "TABS")}"), total == 0 ? Theme.Bad : Theme.Accent);
        }
        else
            Kit.Caption(" "u8);
    }

    private static int CountTabsWithMatches(ControlsBrowser browser)
    {
        var tabs = 0;
        foreach (var count in browser.Counts)
            if (count > 0)
                tabs++;
        return tabs;
    }

    /// <summary>
    /// The tab strip, wrapping onto more lines when it must. While a search is on, a tab with no match is greyed
    /// (and can still be opened), and one with matches shows how many. Returns true when the player opened a tab.
    /// </summary>
    private bool DrawControlTabs(ControlsBrowser browser, float width)
    {
        var draw = ImGui.GetWindowDrawList();
        var origin = ImGui.GetCursorScreenPos();
        var height = Kit.Px(32);
        var gap = Kit.Px(Theme.Space1);
        var pad = Kit.Px(Theme.Space3);
        var x = 0f;
        var y = 0f;
        var changed = false;
        var tabs = browser.Catalog.Tabs;
        for (var i = 0; i < tabs.Length; i++)
        {
            var title = tabs[i].Title;
            var count = browser.Counts[i];
            var active = browser.TabActive(i);
            var badge = browser.Searching && count > 0;
            var titleSize = Kit.Measure(_ui.SansMedium, Theme.FontSmall, Kit.F($"{title}"));
            var badgeSize = badge ? Kit.Measure(_ui.SansMedium, Theme.FontCaption, Kit.F($"{count}")) : default;
            var badgeWidth = badge ? badgeSize.X + Kit.Px(Theme.Space2) : 0f;
            var tabWidth = titleSize.X + pad * 2 + (badge ? badgeWidth + Kit.Px(Theme.Space1) : 0f);
            if (x > 0 && x + tabWidth > width)
            {
                x = 0;
                y += height + gap;
            }

            var min = origin + new float2(x, y);
            ImGui.SetCursorScreenPos(min);
            ImGui.PushID(i);
            var pressed = ImGui.InvisibleButton("##tab"u8, new float2(tabWidth, height));
            ImGui.PopID();
            var hot = Kit.IsHot();
            var selected = i == browser.Selected;
            if (pressed)
            {
                browser.Select(i);
                changed = true;
            }

            var max = min + new float2(tabWidth, height);
            if (selected)
                draw.AddRectFilled(min, max, Kit.Color(Theme.PanelRaised, 0.9f));
            else if (hot)
                draw.AddRectFilled(min, max, Kit.Color(Theme.PanelRaised, 0.5f));
            draw.AddRectFilled(new float2(min.X, max.Y - Kit.Px(2)), max, Kit.Color(selected ? Theme.Accent : Theme.Line, active || selected ? 1f : 0.5f));
            // Greyed means nothing here matches the search; it stays readable, and clickable.
            var color = selected ? Theme.Text : active ? (hot ? Theme.Text : Theme.TextDim) : Theme.TextMute;
            Kit.Text(draw, _ui.SansMedium, Theme.FontSmall, min + new float2(pad, (height - titleSize.Y) * 0.5f), color, Kit.F($"{title}"), active ? 1f : 0.55f);
            if (badge)
            {
                var pill = min + new float2(pad + titleSize.X + Kit.Px(Theme.Space2), (height - badgeSize.Y) * 0.5f - Kit.Px(2));
                var pillSize = badgeSize + new float2(Kit.Px(Theme.Space2), Kit.Px(4));
                draw.AddRectFilled(pill, pill + pillSize, Kit.Color(Theme.Accent), Kit.Px(3));
                Kit.Text(draw, _ui.SansMedium, Theme.FontCaption, pill + new float2(Kit.Px(Theme.Space1), Kit.Px(2)), Theme.AccentInk, Kit.F($"{count}"));
            }

            x += tabWidth + gap;
        }

        ImGui.SetCursorScreenPos(origin + new float2(0, y + height));
        ImGui.Dummy(new float2(width, 0));
        return changed;
    }

    /// <summary>
    /// One row of a Controls screen: the action, then what it is bound to, wrapped to the width. A row the search
    /// found is lit and the others dimmed. On the Settings page a row that cannot be changed is drawn this way too,
    /// with <paramref name="fixedRow"/> set, which adds a "Fixed" tag.
    /// </summary>
    private void DrawReferenceRow(ControlsBrowser browser, in ControlsCatalog.Row row, float width, bool fixedRow)
    {
        var draw = ImGui.GetWindowDrawList();
        var position = ImGui.GetCursorScreenPos();
        var pad = Kit.Px(Theme.Space3);
        var inner = Math.Max(width - pad * 2, 1f);
        var hit = browser.Highlighted(row);
        var dim = browser.Searching && !hit;
        var labelHeight = WrappedHeight(_ui.Sans, Theme.FontBody, Kit.F($"{row.Action}"), inner);
        var bindingHeight = WrappedHeight(_ui.Mono, Theme.FontSmall, Kit.F($"{row.Binding}"), inner);
        var height = pad + labelHeight + Kit.Px(Theme.Space1) + bindingHeight + pad;
        var max = position + new float2(width, height);
        if (hit)
        {
            draw.AddRectFilled(position, max, Kit.Color(Theme.PanelRaised, 0.9f));
            draw.AddRectFilled(position, new float2(position.X + Kit.Px(3), max.Y), Kit.Color(Theme.Accent));
        }

        WrappedText(draw, _ui.Sans, Theme.FontBody, position + new float2(pad, pad), dim ? Theme.TextMute : Theme.Text, Kit.F($"{row.Action}"), inner);
        WrappedText(draw, _ui.Mono, Theme.FontSmall, position + new float2(pad, pad + labelHeight + Kit.Px(Theme.Space1)), hit ? Theme.Accent : dim ? Theme.TextMute : Theme.TextDim, Kit.F($"{row.Binding}"), inner);
        if (fixedRow)
        {
            var tag = Kit.Measure(_ui.SansMedium, Theme.FontCaption, "FIXED"u8);
            Kit.Text(draw, _ui.SansMedium, Theme.FontCaption, new float2(max.X - pad - tag.X, position.Y + pad), Theme.TextMute, "FIXED"u8);
        }

        draw.AddLine(new float2(position.X, max.Y - 0.5f), new float2(max.X, max.Y - 0.5f), Kit.Color(Theme.Line, 0.6f));
        ImGui.Dummy(new float2(width, height));
    }

    private static float WrappedHeight(ImFontPtr font, float size, ImString text, float wrap)
    {
        ImGui.PushFont(font, size);
        var height = ImGui.CalcTextSize(text, false, wrap).Y;
        ImGui.PopFont();
        return height;
    }

    private static void WrappedText(ImDrawListPtr draw, ImFontPtr font, float size, float2 position, uint rgb, ImString text, float wrap)
    {
        ImGui.PushFont(font, size);
        draw.AddText(font, ImGui.GetFontSize(), new float2(MathF.Round(position.X), MathF.Round(position.Y)), Kit.Color(rgb), text, wrap);
        ImGui.PopFont();
    }
}
