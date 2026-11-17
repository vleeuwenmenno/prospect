using Brutal.ImGuiApi;
using Brutal.Numerics;
using Prospect.Client.Ui;
using Prospect.Game.Client;
using Prospect.Game.World;

namespace Prospect.Client.Screens;

internal sealed partial class ScreenHost
{
    private void DrawContainerReadout(ImDrawListPtr draw, SessionSnapshot snapshot, in FreeContainer container)
    {
        var size = new float2(Kit.Px(HudLayout.TargetReadoutWidth), Kit.Px(110));
        var min = new float2((_ui.DisplaySize.X - size.X) * .5f, HudTop);
        Kit.PanelFrame(draw, min, min + size);
        var text = min + new float2(Kit.Px(Theme.Space4), Kit.Px(Theme.Space3));
        var cargo = snapshot.Content.Cargo;
        var resource = container.Resource < 0 ? "Empty" : snapshot.Galaxy!.Resources[container.Resource].Name;
        Kit.Text(draw, _ui.SansMedium, Theme.FontCaption, text, Theme.Info, "TARGET  -  CONTAINER"u8);
        Kit.Text(draw, _ui.SansMedium, Theme.FontBody, text + new float2(0, Kit.Px(20)), Theme.Text,
            Kit.F($"{cargo.Sizes[container.Size].Name}  {resource}"));
        Kit.Text(draw, _ui.Mono, Theme.FontCaption, text + new float2(0, Kit.Px(46)), Theme.TextDim,
            Kit.F($"{container.Amount:0.##} KG    HP {container.Hull:0}/{cargo.Sizes[container.Size].Hull:0}"));
        if (DroneUi.Present(snapshot.Bays, _app.World.Own.Id))
            DrawFetchButton(draw, snapshot, container.Id, new float2(min.X + Kit.Px(Theme.Space4), min.Y + size.Y - Kit.Px(34)),
                new float2(size.X - Kit.Px(Theme.Space4 * 2), Kit.Px(26)));
    }

    /// <summary>
    /// The target panel's button that sends the flown hull's drones for the targeted container, as the fetch key does.
    /// It takes a click only where the pointer is shown; the server answers on the drone chip.
    /// </summary>
    private void DrawFetchButton(ImDrawListPtr draw, SessionSnapshot snapshot, uint container, float2 min, float2 size)
    {
        ImGui.SetNextWindowPos(min);
        ImGui.SetNextWindowSize(size);
        ImGui.Begin("##fetchbutton"u8, RootFlags | ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
        ImGui.SetCursorScreenPos(min);
        var pressed = ImGui.InvisibleButton("##fetch"u8, size);
        var hot = ImGui.IsItemHovered();
        ImGui.End();
        var busy = snapshot.Bays.Fetching > 0;
        Hud.Plate(draw, min, min + size, Theme.Accent, hot);
        var pad = Kit.Px(Theme.Space2);
        var y = min.Y + (size.Y - Kit.Px(14)) * .5f;
        var keyWidth = Kit.Measure(_ui.Mono, Theme.FontCaption, _droneFetchKeyName).X;
        Kit.Text(draw, _ui.Mono, Theme.FontCaption, new float2(min.X + pad, y), Theme.Accent, _droneFetchKeyName);
        Kit.ClippedText(draw, _ui.SansMedium, Theme.FontCaption, new float2(min.X + pad * 2 + keyWidth, y), hot ? Theme.Text : Theme.TextDim,
            busy ? "FETCH WITH DRONES  (DRONES OUT FETCHING)"u8 : "FETCH WITH DRONES"u8, Math.Max(0f, size.X - pad * 3 - keyWidth));
        if (pressed && Bridge is { } bridge) bridge.FetchContainer(_app.World.Own.Id, container);
    }
}
