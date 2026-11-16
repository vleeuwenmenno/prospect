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
    }
}
