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
    private readonly FuelCallout _fuelCallout = new();
    private bool _fuelCalloutScripted;

    // The callout's fixed buffers: every tank a design may have, and the labels.
    private readonly CalloutTank[] _calloutTanks = new CalloutTank[TankFuel.MaxTanks];
    private readonly int[] _calloutGroups = new int[TankFuel.MaxTanks];
    private readonly CalloutLabel[] _calloutLabels = new CalloutLabel[FuelCalloutLayout.MaxLabels];
    private readonly float2[] _calloutSizes = new float2[FuelCalloutLayout.MaxLabels];

    /// <summary>
    /// Keeps the callout to the flown hull, and opens the one a scripted run asked for once the hull has a tank of
    /// that fuel. Called every frame the world is shown, before the console reads a click.
    /// </summary>
    private void UpdateFuelCallout(WorldView world)
    {
        var own = world.Own;
        _fuelCallout.Follow(own.Id, own.Fuel);
        var scripted = _app.ScriptedFuelCallout;
        if (!_fuelCalloutScripted && scripted != FuelKind.None && own.Id != 0 && ConsoleFuel.Capacity(own.Fuel, scripted) > 0f)
        {
            _fuelCalloutScripted = true;
            _fuelCallout.Show(own.Id, scripted);
        }
    }

    /// <summary>
    /// The open fuel's tanks on the flown hull, in the pilot and command views alike: each tank block outlined in the
    /// fuel's colour (a lost one in red), and a label for each tank, or for a group of tanks near each other once
    /// there are more than the content's labels, with a thin leader to its tanks. Labels stand in two columns beside
    /// the hull, clear of each other and of the pilot console. Lines only, drawn from the design's boxes: no mesh is
    /// built.
    /// </summary>
    private void DrawFuelCallout(ImDrawListPtr draw, SessionSnapshot snapshot, WorldView world)
    {
        var kind = _fuelCallout.Open;
        if (kind == FuelKind.None)
            return;
        var own = world.Own;
        var designs = snapshot.Content.Designs;
        if (own.Id == 0 || own.Design >= designs.Length)
            return;
        var design = designs[own.Design];
        var look = PowerReadout.Current;
        var alive = own.Blocks.Span;
        var levels = own.Tanks.Span;
        var position = world.DrawnPosition(own);
        var orientation = world.OwnOrientation;
        var accent = look.FuelRgb(kind);
        var outline = Kit.Px(look.CalloutOutlineThickness);

        // In the pilot view the console covers the bottom of the view, and the tanks behind it stay behind it: the
        // whole callout is cut off at the console's top edge, and its labels keep a gap above that.
        var display = _ui.DisplaySize;
        var pilot = world.CommandBlend < 0.5f;
        var consoleTop = FlightConsole.Layout(display, Kit.Scale).Top;
        draw.PushClipRect(float2.Zero, new float2(display.X, pilot ? consoleTop : display.Y), true);

        // Every tank of the fuel: its outline now, its place and figures for the labels.
        var count = 0;
        var tanks = design.Tanks;
        for (var t = 0; t < tanks.Length && count < _calloutTanks.Length; t++)
        {
            if (tanks[t].Kind != kind)
                continue;
            var block = tanks[t].Block;
            var lost = (block >> 6) >= alive.Length || !BlockSet.Has(alive, block);
            var offset = design.BlockOffsets[block];
            var half = design.BlockHalfExtents[block];
            var centre = position + Vector3.Transform(offset, orientation);
            DrawBlockOutline(draw, world, position, orientation, offset, half, Kit.Color(lost ? Theme.Bad : accent, lost ? look.CalloutLostAlpha : look.CalloutOutlineAlpha), outline);
            ref var tank = ref _calloutTanks[count++];
            tank.Local = offset;
            tank.OnScreen = world.Project(centre, out tank.Screen);
            tank.Kilograms = lost || t >= levels.Length ? 0f : FlightConsole.Shown(levels[t], tanks[t].Capacity);
            tank.Capacity = tanks[t].Capacity;
            tank.Lost = lost;
        }

        var labels = FuelCalloutLayout.Group(_calloutTanks.AsSpan(0, count), look.CalloutMaxLabels, _calloutGroups, _calloutLabels);
        var pad = Kit.Px(look.CalloutLabelPad);
        var height = Kit.Measure(_ui.Mono, Theme.FontCaption, "0"u8).Y + pad;
        for (var i = 0; i < labels; i++)
            _calloutSizes[i] = new float2(CalloutLabelWidth(kind, _calloutLabels[i]) + pad * 2, height);

        // The columns keep to the screen and, in the pilot view, above the console and its fuel row.
        var margin = Kit.Px(look.CalloutMargin);
        var bottom = pilot ? consoleTop - Kit.Px(look.CalloutConsoleGap) : display.Y - margin;
        var bounds = new HudRect(margin, margin, display.X - margin, Math.Max(margin, bottom));
        var centreOnScreen = world.Project(position, out var hullCentre) ? hullCentre : display * 0.5f;
        FuelCalloutLayout.Place(_calloutLabels, labels, _calloutSizes, centreOnScreen, bounds, Kit.Px(look.CalloutLabelOffset), Kit.Px(look.CalloutLabelGap));

        var leader = Kit.Px(look.CalloutLeaderThickness);
        var dot = Kit.Px(look.CalloutAnchorDot);
        for (var i = 0; i < labels; i++)
        {
            ref readonly var label = ref _calloutLabels[i];
            if (!label.Visible)
                continue;
            var tone = label.Lost == label.Tanks ? Theme.Bad : accent;
            var rect = label.Rect;
            var edge = new float2(label.Left ? rect.Right : rect.Left, (rect.Top + rect.Bottom) * 0.5f);
            draw.AddLine(edge, label.Anchor, Kit.Color(tone, look.CalloutLeaderAlpha), leader);
            if (dot > 0f)
                draw.AddCircleFilled(label.Anchor, dot, Kit.Color(tone, look.CalloutLeaderAlpha), 8);
            var min = new float2(rect.Left, rect.Top);
            var max = new float2(rect.Right, rect.Bottom);
            draw.AddRectFilled(min, max, Kit.Color(Theme.Panel, Hud.FillAlpha));
            draw.AddRect(min, max, Kit.Color(tone, 0.8f), 0f, ImDrawFlags.None, 1f);
            DrawCalloutLabelText(draw, kind, label, new float2(rect.Left + pad, rect.Top + pad * 0.5f), accent);
        }

        draw.PopClipRect();
    }

    /// <summary>The twelve edges of a block's box, projected and cut at the near plane.</summary>
    private static void DrawBlockOutline(ImDrawListPtr draw, WorldView world, Double3 position, Quaternion orientation, Vector3 offset, Vector3 half, ImColor8 color, float thickness)
    {
        Span<Double3> corners = stackalloc Double3[8];
        for (var c = 0; c < 8; c++)
        {
            var local = offset + new Vector3((c & 1) == 0 ? -half.X : half.X, (c & 2) == 0 ? -half.Y : half.Y, (c & 4) == 0 ? -half.Z : half.Z);
            corners[c] = position + Vector3.Transform(local, orientation);
        }

        for (var c = 0; c < 8; c++)
        {
            for (var bit = 1; bit < 8; bit <<= 1)
            {
                if ((c & bit) != 0)
                    continue;
                if (world.ProjectSegment(corners[c], corners[c | bit], out var a, out var b))
                    draw.AddLine(a, b, color, thickness);
            }
        }
    }

    /// <summary>
    /// A label's three runs of text: what it is (the fuel, or how many tanks) in the fuel's colour, what they hold,
    /// and what is lost in red. A tank says <c>BIPROP 340 / 1000 KG</c> or <c>BIPROP LOST</c>; a group says
    /// <c>4 TANKS · 1200 KG</c>, then <c>· 1 LOST</c> when one of them is.
    /// </summary>
    private static ReadOnlySpan<byte> CalloutRun(FuelKind kind, in CalloutLabel label, int run)
    {
        var single = label.Tanks == 1;
        return run switch
        {
            0 => single ? ConsoleFuel.Name(kind) : Kit.F($"{label.Tanks} TANKS"),
            1 => single ? label.Lost > 0 ? default : ConsoleFuel.Stored(label.Kilograms, label.Capacity)
                : label.Kilograms >= 10000f ? Kit.F($"· {label.Kilograms / 1000f:0.##} T") : Kit.F($"· {label.Kilograms:0} KG"),
            _ => label.Lost == 0 ? default : single ? "LOST"u8 : Kit.F($"· {label.Lost} LOST"),
        };
    }

    private float CalloutLabelWidth(FuelKind kind, in CalloutLabel label)
    {
        var space = Kit.Px(4);
        var width = 0f;
        for (var run = 0; run < 3; run++)
        {
            var text = CalloutRun(kind, label, run);
            if (text.IsEmpty)
                continue;
            width += (width > 0f ? space : 0f) + Kit.Measure(_ui.Mono, Theme.FontCaption, text).X;
        }

        return MathF.Ceiling(width);
    }

    private void DrawCalloutLabelText(ImDrawListPtr draw, FuelKind kind, in CalloutLabel label, float2 at, uint accent)
    {
        var space = Kit.Px(4);
        var x = at.X;
        for (var run = 0; run < 3; run++)
        {
            var text = CalloutRun(kind, label, run);
            if (text.IsEmpty)
                continue;
            var color = run == 0 ? accent : run == 1 ? Theme.Text : Theme.Bad;
            x += Kit.Text(draw, _ui.Mono, Theme.FontCaption, new float2(x, at.Y), color, text).X + space;
        }
    }
}
