using Brutal.ImGuiApi;
using Brutal.Numerics;
using Prospect.Client.Ui;
using Prospect.Client.World;
using Prospect.Game.Client;
using Prospect.Game.World;

namespace Prospect.Client.Screens;

// Action groups in the flight and command views: the strip above the console, its tooltip and the popup that changes
// a laser's group without a rebuild. What is shown is the flown hull's design, the server's per-hull overrides and the
// beams it reports; the number keys and the fire button are WorldView's (WorldView.Lasers.cs).
internal sealed partial class ScreenHost
{
    private readonly ActionGroupStrip _actionGroups = new();
    private readonly ActionGroupKeys _actionGroupKeys = new();
    private bool _actionGroupsOpen;
    private bool _tipSawActionGroup;
    private bool _tipSawOverheat;

    /// <summary>Whether the popup that changes a laser's group is open; Escape closes it before it backs out of anything else.</summary>
    internal bool ActionGroupsOpen => _actionGroupsOpen;

    /// <summary>Opens the popup for a capture (<c>--open action-groups-popup</c>); it closes at once on a ship with no group.</summary>
    public void OpenActionGroupsPopup() => _actionGroupsOpen = true;

    /// <summary>
    /// Reads the flown hull's groups once a frame, before anything is drawn: which have a part, which are on, firing
    /// and cutting. The popup closes itself when there is nothing to change (no group, the map, the editor).
    /// </summary>
    private void UpdateActionGroups(SessionSnapshot snapshot, WorldView world)
    {
        _actionGroupKeys.Update(_app.Actions);
        var own = world.Own;
        if (own.Id != 0 && snapshot.Content is { } content && own.Design < content.Designs.Length)
            _actionGroups.Read(snapshot, own.Id, content.Designs[own.Design], world.ActionGroupsOn, world.LasersFiring);
        else
        {
            _actionGroups.Begin();
            _actionGroups.End(0, false);
        }

        if (_actionGroups.Count == 0 || _mapOpen || world.Editor.Shown)
            _actionGroupsOpen = false;
    }

    /// <summary>
    /// The strip's rectangle: centred over the console, above the module chips (or, without any, above the slot the
    /// status chip and the auto-align tag use), so it never shares a pixel with the console, the fuel row, the chips or
    /// those two. Pure, so a test can check it at any size and scale; the tips and the party list keep clear of it too.
    /// </summary>
    internal static HudRect ActionGroupsRect(float2 display, float scale, int groups, int chips, ModuleLook.StripLook strip, ActionGroupLook look, out float cellWidth)
    {
        var console = FlightConsole.Layout(display, scale);
        var below = console.Top - MathF.Round((Theme.Space2 + HudLayout.ChipHeight) * scale);
        if (chips > 0)
            below = Math.Min(below, StripRect(display, scale, chips, strip).Top);
        return ActionGroupStrip.Place(display, scale, groups, below, look, out cellWidth);
    }

    private HudRect ActionGroupsRect(out float cellWidth) =>
        ActionGroupsRect(_ui.DisplaySize, Kit.Scale, _actionGroups.Count, StripChips(), ModuleStyle.Strip, ActionGroupLook.Current, out cellWidth);

    /// <summary>
    /// Draws the strip, its tooltip and, when a cell was clicked, the popup. Nothing is drawn for a hull with no group.
    /// A click only reaches it where the pointer is shown (Shift in the pilot view, interface mode, the command view).
    /// </summary>
    private void DrawActionGroups(SessionSnapshot snapshot, WorldView world)
    {
        if (_actionGroups.Count == 0)
            return;
        var look = ActionGroupLook.Current;
        var rect = ActionGroupsRect(out var cellWidth);
        var result = ActionGroupPanel.DrawStrip(_actionGroups, _actionGroupKeys, rect, cellWidth, look);
        var own = world.Own;
        if (snapshot.Content is not { } content || own.Design >= content.Designs.Length)
            return;
        var design = content.Designs[own.Design];
        if (result.Pressed != 0)
            _actionGroupsOpen = !_actionGroupsOpen;

        // A scripted run's pointer is muted: it asks for one group's tooltip instead of resting on its cell.
        var hovered = _app.ScriptedHeatTip is > 0 and var asked ? asked : result.Hovered;
        if (hovered != 0 && !_actionGroupsOpen && _actionGroups.TryGet(hovered, out var cell))
        {
            var at = ActionGroupStrip.CellRect(rect, IndexOfGroup(hovered), cellWidth, Kit.Px(look.Gap));
            ActionGroupPanel.DrawTip(ImGui.GetForegroundDrawList(), snapshot, own.Id, design, cell, _actionGroupKeys.Name(cell.Group), at, _ui.DisplaySize, look,
                (own.Environment & HullEnvironment.InColdCloud) != 0, snapshot.Flight.ColdCooling);
        }

        if (_actionGroupsOpen && Bridge is { } bridge)
        {
            var popup = ActionGroupPanel.PopupRect(rect, _ui.DisplaySize, Kit.Scale, ActionGroupPanel.Rows(design), look);
            if (ActionGroupPanel.DrawPopup(snapshot, own.Id, design, bridge, _actionGroupKeys, popup, look))
                _actionGroupsOpen = false;
        }
    }

    private int IndexOfGroup(int group)
    {
        var cells = _actionGroups.Cells;
        for (var i = 0; i < cells.Length; i++)
            if (cells[i].Group == group)
                return i;
        return 0;
    }
}
