using Brutal.ImGuiApi;
using Brutal.Numerics;
using Prospect.Client.Ui;
using Prospect.Client.World;
using Prospect.Game.Client;
using Prospect.Game.World;

namespace Prospect.Client.Screens;

internal sealed partial class ScreenHost
{
    /// <summary>
    /// The tooltip of the container under a shown pointer: what it holds, how full it is, its size, its hit points
    /// and, for a loose one, who may take it and how long until a tractor may. Nothing while no container is hovered.
    /// </summary>
    private void DrawContainerHover(SessionSnapshot snapshot, WorldView world)
    {
        var info = world.HoverContainer;
        if (!info.Any || snapshot.Galaxy is not { } galaxy) return;
        var cargo = snapshot.Content.Cargo;
        var empty = (uint)info.Resource >= (uint)galaxy.Resources.Length;
        var name = empty ? "Empty container" : galaxy.Resources[info.Resource].Name;
        var line = Kit.Px(16);
        var pad = Kit.Px(Theme.Space3);
        // A loose container the flown hull's drones could fetch names the key that sends them.
        var fetch = info.Kind == HoverKind.Free && DroneUi.Present(snapshot.Bays, world.Own.Id);
        var rows = info.Kind == HoverKind.Free ? fetch ? 6 : 5 : 4;
        var size = new float2(Kit.Px(210), pad * 2 + Kit.Px(22) + line * rows);
        var at = world.HoverContainerAt + new float2(Kit.Px(18), Kit.Px(18));
        at = new float2(MathF.Min(at.X, _ui.DisplaySize.X - size.X - pad), MathF.Min(at.Y, _ui.DisplaySize.Y - size.Y - pad));
        var draw = ImGui.GetForegroundDrawList();
        Kit.PanelFrame(draw, at, at + size);
        var text = at + new float2(pad, pad);
        Kit.Text(draw, _ui.SansMedium, Theme.FontBody, text, Theme.Text, name);
        text.Y += Kit.Px(22);
        Kit.Text(draw, _ui.Mono, Theme.FontCaption, text, Theme.TextDim, Kit.F($"{cargo.Sizes[info.Size].Name}  {info.Units} U"));
        text.Y += line;
        Kit.Text(draw, _ui.Mono, Theme.FontCaption, text, Theme.TextDim, empty ? Kit.F($"0 KG") : Kit.F($"{info.Amount:0} KG  {info.Fill * 100f:0} % FULL"));
        text.Y += line;
        Kit.Text(draw, _ui.Mono, Theme.FontCaption, text, Theme.TextDim, Kit.F($"HP {info.Hull:0} / {info.MaxHull:0}"));
        text.Y += line;
        if (info.Kind == HoverKind.Stowed)
        {
            Kit.Text(draw, _ui.Mono, Theme.FontCaption, text, Theme.Info, "IN YOUR RACK"u8);
            return;
        }
        var claim = info.Claim switch
        {
            ClaimState.Yours => "CLAIMED BY YOU"u8,
            ClaimState.Others => "CLAIMED BY SOMEONE ELSE"u8,
            _ => "UNCLAIMED"u8,
        };
        Kit.Text(draw, _ui.Mono, Theme.FontCaption, text, info.Claim == ClaimState.Others ? Theme.Bad : Theme.Info, claim);
        text.Y += line;
        Kit.Text(draw, _ui.Mono, Theme.FontCaption, text, Theme.TextDim, info.PickupSeconds > 0 ? Kit.F($"PICKUP IN {info.PickupSeconds:0.0} S") : "READY FOR PICKUP"u8);
        if (!fetch) return;
        text.Y += line;
        var keyWidth = Kit.Measure(_ui.Mono, Theme.FontCaption, _droneFetchKeyName).X;
        Kit.Text(draw, _ui.Mono, Theme.FontCaption, text, Theme.Accent, _droneFetchKeyName);
        Kit.Text(draw, _ui.Mono, Theme.FontCaption, text + new float2(keyWidth + Kit.Px(Theme.Space2), 0), Theme.Text, "FETCH WITH DRONES"u8);
    }
}
