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
    private void DrawTractorReach(ImDrawListPtr draw, SessionSnapshot snapshot, WorldView world, ShipEditor editor, BuildDraft draft, in EditorLayout layout)
    {
        var preview = world.TractorPreview;
        var look = world.TractorLook;
        var boxes = draft.Boxes;
        var mounts = draft.Mounts;
        var reachColor = Kit.Color(look.BeamColor, MathF.Min(1f, look.Opacity * 1.1f));
        var thickness = Kit.Px(MathF.Max(look.CoreWidth * 1.35f, 2f));
        var arrowLen = Kit.Px(look.ReachArrowSize);
        var arrowHalf = Kit.Px(look.ReachArrowSize * 0.55f);

        for (var m = 0; m < mounts.Length; m++)
        {
            if (mounts[m].Type.Tractor is null) continue;
            var start = TractorReach.Emitter(boxes, mounts[m], draft.BlockSize) / draft.BlockSize - new Vector3(.5f);
            for (var r = 0; r < boxes.Length; r++)
            {
                if (!preview.Linked(m, r)) continue;
                var box = boxes[r];
                var end = EditorGeometry.Middle(box) + Facings.Vector(preview.Face(m, r))
                    * new Vector3(box.Size.X, box.Size.Y, box.Size.Z) * .5f;
                if (!world.ProjectSegment(world.EditorPoint(start), world.EditorPoint(end), out var a, out var b))
                    continue;

                var delta = b - a;
                var len = delta.Length();
                if (len < 1f) continue;

                var dir = delta / len;
                var perp = new float2(-dir.Y, dir.X);
                var lineLen = MathF.Max(0f, len - arrowLen * 0.8f);
                var dash = Kit.Px(look.ReachDashLength);
                var gap = Kit.Px(look.ReachGapLength);
                var step = dash + gap;

                const int maxDashes = 48;
                if (lineLen > 1f && step > 1f)
                {
                    var count = (int)MathF.Ceiling(lineLen / step);
                    if (count > maxDashes)
                    {
                        var scale = (float)count / maxDashes;
                        dash *= scale;
                        gap *= scale;
                        step = dash + gap;
                    }
                    for (var d = 0f; d < lineLen; d += step)
                        draw.AddLine(a + dir * d, a + dir * MathF.Min(d + dash, lineLen), reachColor, thickness);
                }

                draw.AddCircleFilled(a, Kit.Px(2.5f), reachColor, 10);
                var baseMid = b - dir * arrowLen;
                draw.AddTriangleFilled(b, baseMid + perp * arrowHalf, baseMid - perp * arrowHalf, reachColor);
            }
        }

        if (!preview.Complete)
            return;

        var pad = Kit.Px(Theme.Space2);
        var safeMin = new float2(layout.Parts.Right + pad, layout.Parts.Top + pad);
        var safeMax = new float2(layout.Details.Left - pad, layout.Bar.Top - pad);
        var hasJettison = snapshot.OwnHull >= 0 && editor.SelectedCargoRack >= 0 && editor.Hull == snapshot.Hulls[snapshot.OwnHull].Id;
        HudRect jettisonRect = default;
        if (hasJettison)
        {
            var jWidth = Kit.Px(310);
            var jPos = new float2((_ui.DisplaySize.X - jWidth) * 0.5f, _ui.DisplaySize.Y - Kit.Px(300));
            jettisonRect = new HudRect(jPos.X, jPos.Y, jPos.X + jWidth, jPos.Y + Kit.Px(210));
        }

        var labelText = "NO TRACTOR IN VIEW"u8;
        var textSize = Kit.Measure(_ui.SansMedium, Theme.FontCaption, labelText);
        var padX = Kit.Px(6);
        var padY = Kit.Px(3);
        var barW = Kit.Px(2.5f);
        var pillSize = new float2(textSize.X + padX * 2 + barW, textSize.Y + padY * 2);
        var canPlaceLabels = safeMax.X - safeMin.X >= pillSize.X && safeMax.Y - safeMin.Y >= pillSize.Y;

        const int maxLabels = 8;
        Span<HudRect> placed = stackalloc HudRect[maxLabels];
        var placedCount = 0;

        for (var r = 0; r < boxes.Length; r++)
        {
            if (boxes[r].Type.Cargo <= 0 || preview.Reached(r))
                continue;

            DrawBlockEdges(draw, world, boxes[r], Kit.Color(look.WarningColor, 0.9f), thickness);

            if (!canPlaceLabels || placedCount >= maxLabels)
                continue;
            var middle = EditorGeometry.Middle(boxes[r]);
            if (!world.Project(world.EditorPoint(middle), out var screen))
                continue;

            var offsetX = Kit.Px(14);
            var x = screen.X + offsetX;
            if (x + pillSize.X > safeMax.X)
                x = screen.X - offsetX - pillSize.X;
            x = Math.Clamp(x, safeMin.X, safeMax.X - pillSize.X);
            var y = Math.Clamp(screen.Y - pillSize.Y * 0.5f, safeMin.Y, safeMax.Y - pillSize.Y);

            var rect = new HudRect(x, y, x + pillSize.X, y + pillSize.Y);
            if (hasJettison && RectsOverlap(rect, jettisonRect))
            {
                var aboveY = MathF.Max(safeMin.Y, jettisonRect.Top - pad - pillSize.Y);
                rect = new HudRect(x, aboveY, x + pillSize.X, aboveY + pillSize.Y);
            }
            for (var p = 0; p < placedCount; p++)
            {
                if (!RectsOverlap(rect, placed[p])) continue;
                var nextY = MathF.Min(safeMax.Y - pillSize.Y, placed[p].Bottom + Kit.Px(4));
                rect = new HudRect(rect.Left, nextY, rect.Right, nextY + pillSize.Y);
            }

            placed[placedCount++] = rect;
            var pillMin = new float2(rect.Left, rect.Top);
            var pillMax = new float2(rect.Right, rect.Bottom);
            draw.AddRectFilled(pillMin, pillMax, Kit.Color(Theme.Void, 0.88f), Kit.Px(2));
            draw.AddRect(pillMin, pillMax, Kit.Color(look.WarningColor, 0.7f), Kit.Px(2));
            draw.AddRectFilled(pillMin, new float2(pillMin.X + barW, pillMax.Y), Kit.Color(look.WarningColor, 0.95f));
            Kit.Text(draw, _ui.SansMedium, Theme.FontCaption, pillMin + new float2(barW + padX, padY), look.WarningColor, labelText);
        }
    }

    private static bool RectsOverlap(in HudRect a, in HudRect b) =>
        a.Left < b.Right && a.Right > b.Left && a.Top < b.Bottom && a.Bottom > b.Top;

    private void DrawTractorDetails(ShipEditor editor, BuildDraft draft, float width)
    {
        var preview = _app.World.TractorPreview;
        var hasSpec = (uint)editor.SelectedMount < (uint)draft.Mounts.Length && draft.Mounts[editor.SelectedMount].Type.Tractor is not null;
        if (preview.Racks <= 0 && !hasSpec)
            return;

        Kit.Space(Theme.Space4);
        Kit.Caption("TRACTORS"u8, Theme.Accent);
        Kit.Space(Theme.Space1);
        if (preview.Racks > 0)
        {
            var tone = preview.Complete && preview.Unreached > 0 ? Theme.Accent : Theme.Text;
            var value = preview.Complete
                ? Kit.F($"{preview.Racks - preview.Unreached} / {preview.Racks} RACKS")
                : "CHECKING"u8;
            StatRow("REACH"u8, value, tone, width);
        }
        if (hasSpec && draft.Mounts[editor.SelectedMount].Type.Tractor is { } spec)
            Paragraph(Theme.TextDim, Kit.F($"Automatic pickup · {spec.Range:0} m · {spec.MaxMass / 1000:0.#} t max · {spec.BasePower:0.#} MW + {spec.PowerPerTonne:0.##} MW/t"), width);
    }
}
