using Brutal.ImGuiApi;
using Prospect.Client.Input;
using Prospect.Client.Ui;

namespace Prospect.Client.Screens;

/// <summary>
/// The galaxy map's movable panels in the session: where they are kept, saving a move once it is let go, and the
/// reset the key and the Settings button share. The rules are <see cref="MapPanels"/>'s.
/// </summary>
internal sealed partial class ScreenHost
{
    private MapPanels _mapPanels = null!;

    // The Settings button's answer, shown until the screen is left.
    private bool _panelsResetDone;

    /// <summary>Puts the map's panels back at their default places and saves that, as the key and the button do.</summary>
    public void ResetMapPanels()
    {
        _mapPanels.Reset();
        SaveMapPanels();
    }

    /// <summary>Writes the settings once a dragged panel is let go, so a place survives a crash.</summary>
    private void SaveMapPanels()
    {
        if (_mapPanels.TakeChanged() && !_app.Devices.Muted)
            _app.Settings.Save();
    }

    /// <summary>The Settings screen's panel part: what it does, the Reset panel positions button, and its answer.</summary>
    private void DrawPanelSettings(float rowWidth)
    {
        var words = _tipCatalogue.Settings;
        Kit.Space(Theme.Space4);
        ImGui.Indent(Kit.Px(Theme.Space3));
        Kit.Paragraph(_ui.Sans, Theme.FontSmall, Theme.TextMute, words.PanelsDescription, rowWidth - 24);
        ImGui.Unindent(Kit.Px(Theme.Space3));
        Kit.Space(Theme.Space2);
        if (Kit.Button(words.PanelsReset, ButtonKind.Secondary, 240))
        {
            ResetMapPanels();
            _panelsResetDone = true;
        }

        if (_panelsResetDone)
        {
            ImGui.SameLine(0, Kit.Px(Theme.Space3));
            var height = Kit.Measure(_ui.Sans, Theme.FontSmall, words.PanelsDone).Y;
            ImGui.SetCursorPosY(ImGui.GetCursorPosY() + (Kit.Px(Theme.ControlHeight) - height) * 0.5f);
            Kit.Label(_ui.Sans, Theme.FontSmall, Theme.Good, words.PanelsDone);
        }
    }
}
