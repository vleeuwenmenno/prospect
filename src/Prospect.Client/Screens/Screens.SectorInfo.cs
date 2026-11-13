using Prospect.Client.Ui;
using Prospect.Client.World;

namespace Prospect.Client.Screens;

internal sealed partial class ScreenHost
{
    private readonly SectorInfoToggle _sectorInfo = new();

    /// <summary>
    /// Toggles the sector panel from its key before flight reads this frame's input, in the pilot view, the command
    /// view and on the map. Not while a menu, Controls or the social workspace is open, nor while a text field has
    /// the keyboard. The key is left to the world as well: the panel takes no input of its own.
    /// </summary>
    public void HandleSectorInfoShortcut(bool active)
    {
        var allowed = active && _screen == Screen.Session && !_controlsHandled && !_controlsOpen && !_sessionMenuOpen
            && !SocialSurfaceOpen && !_socialHandled;
        _sectorInfo.HandleShortcut(_app.Actions, _app.Devices, allowed, _ui.WantsTextInput, _ui.PointerOverInterface);
    }

    private float SectorColumnBottom(WorldView world, bool compact) =>
        SectorInfoToggle.Bottom(Kit.Scale, _sectorInfo.Open, SectorPanelHeight(world, compact));
}
