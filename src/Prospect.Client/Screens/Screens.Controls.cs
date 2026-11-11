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
    private string _controlsQuery = "";
    private ControlsCatalog _controls = null!;

    private void InitializeControls() => _controls = new ControlsCatalog(_app.Actions);

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

    /// <summary>Opens searchable controls without changing the underlying view.</summary>
    public void OpenControls()
    {
        _controlsOpen = true;
        _controlsSearch.Clear();
        _controlsQuery = "";
        _mapBoxing = false;
    }

    private void DrawControls()
    {
        DrawDim(0.8f);
        BeginRoot("##controls"u8);
        var size = new float2(Math.Min(Kit.Px(760), _ui.DisplaySize.X - Kit.Px(24)), _ui.DisplaySize.Y - Kit.Px(24));
        BeginPanel("##controlspanel"u8, (_ui.DisplaySize - size) * 0.5f, size);
        Kit.Heading("Controls"u8);
        Kit.Space(Theme.Space2);
        if (Kit.Button("Close"u8, ButtonKind.Secondary, 100))
            _controlsOpen = false;
        Kit.Space(Theme.Space2);
        Kit.Caption("SEARCH ACTION, KEY, BUTTON OR CATEGORY"u8);
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
        if (ImGui.InputText("##controlssearch"u8, _controlsSearch.Buffer, ImGuiInputTextFlags.NoUndoRedo))
        {
            _controlsSearch.EvaluateLength();
            _controlsQuery = _controlsSearch.ToString().Trim();
        }
        Kit.Space(Theme.Space3);
        ImGui.BeginChild("##controlsrows"u8, float2.Zero, ImGuiChildFlags.None, ImGuiWindowFlags.NoBackground);
        var count = 0;
        foreach (var row in _controls.Rows)
        {
            if (!row.Matches(_controlsQuery))
                continue;
            count++;
            Kit.Caption(row.Category, Theme.Accent);
            ImGui.PushFont(_ui.Sans, Theme.FontBody);
            ImGui.PushTextWrapPos(0);
            // BRUTAL Text passes no end pointer, unlike the draw-list text used by Kit.
            ImGui.Text(Kit.F($"{row.Action}\0"));
            ImGui.Text(Kit.F($"{row.Binding}\0"));
            ImGui.PopTextWrapPos();
            ImGui.PopFont();
            Kit.Space(Theme.Space3);
        }
        if (count == 0)
            Kit.Body("No matching controls."u8);
        ImGui.EndChild();
        EndPanel();
        EndRoot();
    }
}
