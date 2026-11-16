using Brutal.ImGuiApi;
using Brutal.Numerics;
using Prospect.Client.Ui;
using Prospect.Client.World;
using Prospect.Game.Client;
using Prospect.Game.World;

namespace Prospect.Client.Screens;

internal sealed partial class ScreenHost
{
    private int _jettisonSelected;
    private long _jettisonTick = -1;
    private void DrawCargoJettison(SessionSnapshot snapshot, ShipEditor editor, PlayerInputBridge bridge)
    {
        var rack = editor.SelectedCargoRack;
        if (snapshot.OwnHull < 0 || rack < 0 || editor.Hull != snapshot.Hulls[snapshot.OwnHull].Id) return;
        if (_jettisonTick != snapshot.OwnCargoTick) { _jettisonTick = snapshot.OwnCargoTick; _jettisonSelected = -1; }
        var width = Kit.Px(310);
        ImGui.SetNextWindowPos(new float2((_ui.DisplaySize.X - width) * .5f, _ui.DisplaySize.Y - Kit.Px(300)));
        ImGui.SetNextWindowSize(new float2(width, Kit.Px(210)));
        ImGui.Begin("##cargoJettison"u8, EditorPanelFlags & ~ImGuiWindowFlags.NoScrollbar);
        var min = ImGui.GetWindowPos();
        Kit.PanelFrame(ImGui.GetWindowDrawList(), min, min + ImGui.GetWindowSize());
        ImGui.SetCursorScreenPos(min + new float2(Kit.Px(10), Kit.Px(10)));
        Kit.Caption("CARGO IN SELECTED RACK"u8);
        ImGui.BeginChild("##jettisonSlots"u8, new float2(width - Kit.Px(20), Kit.Px(125)), ImGuiChildFlags.None, ImGuiWindowFlags.NoBackground);
        for (var i = 0; i < snapshot.Hulls[snapshot.OwnHull].Cargo.Length; i++)
        {
            var held = snapshot.OwnCargo[i];
            if (held.Rack != rack) continue;
            ImGui.PushID(i);
            var resource = held.Resource < 0 ? "Empty" : snapshot.Galaxy!.Resources[held.Resource].Name;
            var row = ImGui.GetCursorScreenPos();
            var rowSize = new float2(ImGui.GetContentRegionAvail().X, Kit.Px(26));
            if (ImGui.InvisibleButton("##container"u8, rowSize)) _jettisonSelected = i;
            if (_jettisonSelected == i) ImGui.GetWindowDrawList().AddRectFilled(row, row + rowSize, Kit.Color(Theme.PanelRaised));
            Kit.Text(ImGui.GetWindowDrawList(), _ui.Sans, Theme.FontSmall, row + new float2(0, Kit.Px(4)),
                _jettisonSelected == i ? Theme.Accent : Theme.Text,
                Kit.F($"{snapshot.Content.Cargo.Sizes[held.Size].Name}  {resource}  {held.Amount:0} kg"));
            ImGui.PopID();
        }
        ImGui.EndChild();
        var ready = _jettisonSelected >= 0 && _jettisonSelected < snapshot.Hulls[snapshot.OwnHull].Cargo.Length
            && snapshot.OwnCargo[_jettisonSelected].Rack == rack && CargoEjection.OpenSide(editor.Source!, rack) >= 0;
        ImGui.BeginDisabled(!ready);
        if (ImGui.Button("JETTISON SELECTED"u8))
        {
            bridge.JettisonContainer(editor.Token, editor.Hull, rack, _jettisonSelected, snapshot.OwnCargoTick);
            _jettisonSelected = -1;
        }
        ImGui.EndDisabled();
        ImGui.End();
    }
}
