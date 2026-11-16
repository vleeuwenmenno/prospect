using System.Numerics;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using Prospect.Client.Ui;
using Prospect.Client.World;
using Prospect.Game.Client;
using Prospect.Game.World;

namespace Prospect.Client.Screens;

internal sealed partial class ScreenHost
{
    private readonly CargoCallout _cargoCallout = new();
    private readonly CalloutLabel[] _cargoCalloutLabel = new CalloutLabel[1];
    private readonly float2[] _cargoCalloutSize = new float2[1];

    /// <summary>
    /// Ties the callout to the Cargo window's selection. With the window not shown the selection is let go, so a
    /// window opened later starts with nothing called out. Called every frame the world is shown.
    /// </summary>
    private void UpdateCargoCallout(SessionSnapshot snapshot, WorldView world)
    {
        var shown = PaneShown(SocialPane.Cargo);
        if (!shown)
            _cargoView.ClearSelection();
        var own = world.Own;
        _cargoCallout.Sync(shown, own.Id, snapshot.OwnCargoTick, own.Cargo.Length, _cargoView.SelectedIndex);
    }

    /// <summary>
    /// The selected container on the flown hull, in the pilot and command views alike: its packed box outlined in its
    /// resource's colour, and a label with the resource, its mass and its size class on a leader to the box's
    /// centre. Lines only, from the same slot the renderer draws the container in (<see cref="CargoSlots"/>), cut
    /// off above the pilot console like the fuel callout.
    /// </summary>
    private void DrawCargoCallout(ImDrawListPtr draw, SessionSnapshot snapshot, WorldView world)
    {
        if (!_cargoCallout.IsOpen)
            return;
        var own = world.Own;
        var designs = snapshot.Content.Designs;
        var hold = own.Cargo.Span;
        var index = _cargoCallout.Index;
        if (own.Id == 0 || own.Design >= designs.Length
            || !CargoCallout.TryBox(designs[own.Design], snapshot.Content.Cargo, hold, index, out var centre, out var half))
            return;

        var container = hold[index];
        var resources = snapshot.Galaxy?.Resources ?? [];
        var look = PowerReadout.Current;
        var accent = (uint)container.Resource < (uint)resources.Length ? StarColor(resources[container.Resource].Color) : Theme.TextMute;
        var position = world.DrawnPosition(own);
        var orientation = world.OwnOrientation;

        var bounds = BeginCalloutClip(draw, world, look, out _);
        DrawBlockOutline(draw, world, position, orientation, centre, half, Kit.Color(accent, look.CargoCalloutOutlineAlpha), Kit.Px(look.CargoCalloutOutlineThickness));

        ref var label = ref _cargoCalloutLabel[0];
        label = default;
        label.Visible = world.Project(position + Vector3.Transform(centre, orientation), out label.Anchor);
        var pad = Kit.Px(look.CalloutLabelPad);
        var height = Kit.Measure(_ui.Mono, Theme.FontCaption, "0"u8).Y + pad;
        _cargoCalloutSize[0] = new float2(CargoCalloutWidth(snapshot, container, resources) + pad * 2, height);
        var centreOnScreen = world.Project(position, out var hullCentre) ? hullCentre : _ui.DisplaySize * 0.5f;
        FuelCalloutLayout.Place(_cargoCalloutLabel, 1, _cargoCalloutSize, centreOnScreen, bounds, Kit.Px(look.CalloutLabelOffset), Kit.Px(look.CalloutLabelGap));

        if (label.Visible)
        {
            DrawCalloutLabelFrame(draw, label, accent, look, pad);
            DrawCargoCalloutText(draw, snapshot, container, resources, new float2(label.Rect.Left + pad, label.Rect.Top + pad * 0.5f), accent);
        }

        draw.PopClipRect();
    }

    /// <summary>
    /// A label's three runs: the resource (or EMPTY) in its colour, the mass held, and the size class, as
    /// <c>IRON 340 KG · MEDIUM</c>.
    /// </summary>
    private static ReadOnlySpan<byte> CargoCalloutRun(SessionSnapshot snapshot, in HoldContainer container, ReadOnlySpan<Resource> resources, int run)
    {
        var sizes = snapshot.Content.Cargo.Sizes;
        var hasResource = (uint)container.Resource < (uint)resources.Length;
        return run switch
        {
            0 => hasResource ? Kit.F($"{resources[container.Resource].Name}") : "EMPTY"u8,
            1 => hasResource ? Kit.F($"{container.Amount:0} KG") : default,
            _ => (uint)container.Size < (uint)sizes.Length ? Kit.F($"· {sizes[container.Size].Name}") : default,
        };
    }

    private float CargoCalloutWidth(SessionSnapshot snapshot, in HoldContainer container, ReadOnlySpan<Resource> resources)
    {
        var space = Kit.Px(4);
        var width = 0f;
        for (var run = 0; run < 3; run++)
        {
            var text = CargoCalloutRun(snapshot, container, resources, run);
            if (text.IsEmpty)
                continue;
            width += (width > 0f ? space : 0f) + Kit.Measure(_ui.Mono, Theme.FontCaption, text).X;
        }

        return MathF.Ceiling(width);
    }

    private void DrawCargoCalloutText(ImDrawListPtr draw, SessionSnapshot snapshot, in HoldContainer container, ReadOnlySpan<Resource> resources, float2 at, uint accent)
    {
        var space = Kit.Px(4);
        var x = at.X;
        for (var run = 0; run < 3; run++)
        {
            var text = CargoCalloutRun(snapshot, container, resources, run);
            if (text.IsEmpty)
                continue;
            var color = run == 0 ? accent : run == 1 ? Theme.Text : Theme.TextDim;
            x += Kit.Text(draw, _ui.Mono, Theme.FontCaption, new float2(x, at.Y), color, text).X + space;
        }
    }
}
