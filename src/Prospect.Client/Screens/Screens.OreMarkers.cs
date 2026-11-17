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
    private readonly OreEdgeArrows _oreArrows = new();

    /// <summary>
    /// Marks the loose ore chunks in the pilot and command views: a diamond framing each chunk in view, in its ore's
    /// colour, fading with distance; chunks off screen gather into small arrows at the window's edge, each with how many
    /// lie that way. The set is the world's own, placed at the pose the mesh is drawn at. Takes no memory.
    /// </summary>
    private void DrawOreMarkers(ImDrawListPtr draw, SessionSnapshot snapshot, WorldView world)
    {
        var markers = world.OreMarkers;
        if (markers.IsEmpty || snapshot.Galaxy is not { } galaxy) return;
        var look = OreChunkLook.Current;
        var scale = Kit.Px(1f);
        var line = look.LineWidth * scale;
        var hovered = world.HoverOreChunk.Id;
        _oreArrows.Clear();
        for (var i = 0; i < markers.Length; i++)
        {
            ref readonly var marker = ref markers[i];
            var alpha = OreChunkMarks.Alpha(marker.Distance, look);
            if (alpha <= 0f) continue;
            if (!marker.OnScreen)
            {
                if (look.EdgeArrows) _oreArrows.Add(marker);
                continue;
            }

            var rgb = OreColor(galaxy, marker.Resource);
            var half = OreChunkMarks.DiamondHalf(marker, look, scale);
            var chosen = marker.Id == hovered;
            if (chosen) half *= 1.25f;
            DrawDiamond(draw, marker.Screen, half, rgb, alpha, look.FillAlpha, line * (chosen ? 1.6f : 1f));
        }

        if (!look.EdgeArrows) return;
        for (var sector = 0; sector < OreEdgeArrows.Sectors; sector++)
        {
            var count = _oreArrows.CountOf(sector);
            if (count == 0) continue;
            var alpha = OreChunkMarks.Alpha(_oreArrows.NearestOf(sector), look);
            var direction = _oreArrows.DirectionOf(sector);
            var at = _oreArrows.Place(sector, _ui.DisplaySize, look.EdgeMarginPixels * scale);
            var rgb = OreColor(galaxy, _oreArrows.ResourceOf(sector));
            var size = look.ArrowPixels * scale;
            var side = new float2(-direction.Y, direction.X);
            var tip = at + direction * size;
            var back = at - direction * (size * 0.6f);
            draw.AddTriangleFilled(tip + direction * 2f, back - direction * 1.5f + side * (size * 0.75f), back - direction * 1.5f - side * (size * 0.75f), Kit.Color(0x05070A, alpha * 0.7f));
            draw.AddTriangleFilled(tip, back + side * (size * 0.6f), back - side * (size * 0.6f), Kit.Color(rgb, alpha));
            // The count sits just inside the arrow, toward the middle of the window.
            var label = Kit.F($"{count}");
            var width = Kit.Measure(_ui.Mono, Theme.FontCaption, label).X;
            Kit.Text(draw, _ui.Mono, Theme.FontCaption, at - direction * (size * 1.6f) - new float2(width * 0.5f, Kit.Px(7)), rgb, label, alpha);
        }
    }

    /// <summary>A diamond outline with a faint fill and a dark under-stroke, so it reads on bright rock and dark space alike.</summary>
    private static void DrawDiamond(ImDrawListPtr draw, float2 centre, float half, uint rgb, float alpha, float fillAlpha, float line)
    {
        var top = centre + new float2(0, -half);
        var right = centre + new float2(half, 0);
        var bottom = centre + new float2(0, half);
        var left = centre + new float2(-half, 0);
        draw.AddQuadFilled(top, right, bottom, left, Kit.Color(rgb, alpha * fillAlpha));
        draw.AddQuad(top, right, bottom, left, Kit.Color(0x05070A, alpha * 0.75f), line + Kit.Px(1.5f));
        draw.AddQuad(top, right, bottom, left, Kit.Color(rgb, alpha), line);
    }

    private static uint OreColor(Galaxy galaxy, int resource) =>
        (uint)resource < (uint)galaxy.Resources.Length ? StarColor(galaxy.Resources[resource].Color) : Theme.TextDim;

    /// <summary>
    /// The tooltip of the chunk under a shown pointer: its ore, the grade, the whole mass and the ore in it, and how far it
    /// is from the ship. Nothing while no chunk is hovered.
    /// </summary>
    private void DrawOreChunkHover(SessionSnapshot snapshot, WorldView world)
    {
        var info = world.HoverOreChunk;
        if (!info.Any || snapshot.Galaxy is not { } galaxy) return;
        var known = (uint)info.Resource < (uint)galaxy.Resources.Length;
        var line = Kit.Px(16);
        var pad = Kit.Px(Theme.Space3);
        var size = new float2(Kit.Px(190), pad * 2 + Kit.Px(22) + line * 3);
        var at = world.HoverOreChunkAt + new float2(Kit.Px(18), Kit.Px(18));
        at = new float2(MathF.Min(at.X, _ui.DisplaySize.X - size.X - pad), MathF.Min(at.Y, _ui.DisplaySize.Y - size.Y - pad));
        var draw = ImGui.GetForegroundDrawList();
        Kit.PanelFrame(draw, at, at + size);
        var text = at + new float2(pad, pad);
        Kit.Text(draw, _ui.SansMedium, Theme.FontBody, text, known ? OreColor(galaxy, info.Resource) : Theme.Text, known ? galaxy.Resources[info.Resource].Name : "Unknown ore");
        text.Y += Kit.Px(22);
        Kit.Text(draw, _ui.Mono, Theme.FontCaption, text, Theme.TextDim, Kit.F($"GRADE {info.Grade * 100f:0} %"));
        text.Y += line;
        Kit.Text(draw, _ui.Mono, Theme.FontCaption, text, Theme.TextDim, Kit.F($"{info.Mass:0} KG  {info.OreKg:0} KG ORE"));
        text.Y += line;
        var distance = info.Distance;
        Kit.Text(draw, _ui.Mono, Theme.FontCaption, text, Theme.TextDim, distance >= 1000f ? Kit.F($"{distance / 1000f:0.0} KM AWAY") : Kit.F($"{distance:0} M AWAY"));
    }
}
