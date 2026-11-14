using Brutal.ImGuiApi;
using Brutal.Numerics;
using Prospect.Client.Ui;
using Prospect.Client.World;
using Prospect.Game.Client;
using Prospect.Game.Protocol;

namespace Prospect.Client.Screens;

/// <summary>
/// The New ship dialog's wiring: the editor key in the escape drone asks for a name and a new one-block ship; the
/// key with Ctrl held edits the drone itself. The rules and keys are <see cref="NewShipDialog"/>'s and the content
/// <see cref="NewShipPanel"/>'s; this is the window around it and what it takes from the screens.
/// </summary>
internal sealed partial class ScreenHost
{
    private readonly NewShipDialog _newShip = new();
    private readonly ImInputString _newShipName = new(NewShipDialog.NameBytes);

    /// <summary>
    /// The capture hooks, once the player flies the drone: <c>new-ship</c> opens the dialog empty,
    /// <c>new-ship-refused</c> with a refusal shown (put there by the hook, not sent by a server), and
    /// <c>new-ship-editor</c> builds a ship through the real dialog and server, so the editor opens on it.
    /// The drone-edit tip fires as it does on the key, and shows with <c>--tips-sample on</c>.
    /// </summary>
    internal void PreviewNewShip(string view, SessionSnapshot snapshot, PlayerInputBridge bridge)
    {
        var refused = view == "new-ship-refused";
        _newShipName.SetValue(refused ? "Prospector" : view == "new-ship-editor" ? "Scout" : "");
        _newShip.BeginRefused(refused ? EditorReason.DuplicateName : EditorReason.None);
        FireTip(TipTriggers.DroneEditHint);
        if (view == "new-ship-editor")
            _newShip.Submit("Scout", snapshot, bridge);
    }

    /// <summary>Follows the server's answer to the request, once a frame while in the game.</summary>
    private void UpdateNewShip(SessionSnapshot snapshot)
    {
        if (_newShip.Open && snapshot.OwnHull >= 0)
            _newShip.Update(snapshot, _app.World.Editor, snapshot.Hulls[snapshot.OwnHull].Id);
    }

    /// <summary>The dialog over the world, dimmed behind, in a window of its own that takes the pointer and the keyboard.</summary>
    private void DrawNewShip(SessionSnapshot snapshot)
    {
        var bridge = _app.Session?.Client.Input;
        if (!_newShip.Open || bridge is null)
            return;
        var panel = NewShipDialog.Panel(_ui.DisplaySize, Kit.Scale);
        DrawDim(0.55f);
        BeginRoot("##newship"u8);
        BeginPanel("##newshippanel"u8, new float2(panel.Left, panel.Top), new float2(panel.Width, panel.Height));
        NewShipPanel.DrawBody(_newShip, _newShipName, snapshot, bridge, _ui);
        EndPanel();
        EndRoot();
    }
}
