using System.Numerics;
using Brutal.ImGuiApi;
using Prospect.Client.Ui;
using Prospect.Client.World;
using Prospect.Game.Client;
using Prospect.Game.World;

namespace Prospect.Client.Screens;

internal sealed partial class ScreenHost
{
    private static void DrawTractorBeams(ImDrawListPtr draw, SessionSnapshot snapshot, WorldView world)
    {
        var look = world.TractorLook;
        for (var i = 0; i < snapshot.FreeContainerCount; i++)
        {
            if (!world.TractorBeam(snapshot.FreeContainers[i], out var start, out var end)
                || !world.ProjectSegment(start, end, out var a, out var b)) continue;
            draw.AddLine(a, b, Kit.Color(look.BeamColor, look.Opacity * .12f), Kit.Px(look.GlowWidth));
            draw.AddLine(a, b, Kit.Color(look.BeamColor, look.Opacity * .3f), Kit.Px((look.CoreWidth + look.GlowWidth) * .5f));
            draw.AddLine(a, b, Kit.Color(look.BeamColor, look.Opacity), Kit.Px(look.CoreWidth));
        }
    }

    private static void DrawTractorReach(ImDrawListPtr draw, WorldView world, BuildDraft draft)
    {
        var preview = world.TractorPreview; var look = world.TractorLook;
        var boxes = draft.Boxes; var mounts = draft.Mounts;
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
                if (world.ProjectSegment(world.EditorPoint(start), world.EditorPoint(end), out var a, out var b))
                    draw.AddLine(a, b, Kit.Color(look.BeamColor, look.Opacity * .7f), Kit.Px(look.CoreWidth));
            }
        }
        if (preview.Complete)
            for (var r = 0; r < boxes.Length; r++)
                if (boxes[r].Type.Cargo > 0 && !preview.Reached(r))
                    DrawBlockEdges(draw, world, boxes[r], Kit.Color(look.WarningColor, .8f), Kit.Px(look.CoreWidth));
    }

    private void DrawTractorDetails(ShipEditor editor, BuildDraft draft, float width)
    {
        var preview = _app.World.TractorPreview;
        if (preview.Racks > 0)
            Paragraph(preview.Complete && preview.Unreached > 0 ? Theme.Accent : Theme.TextDim,
                preview.Complete ? Kit.F($"TRACTOR REACH: {preview.Racks - preview.Unreached} / {preview.Racks} RACKS") : "CHECKING TRACTOR REACH"u8, width);
        if ((uint)editor.SelectedMount < (uint)draft.Mounts.Length && draft.Mounts[editor.SelectedMount].Type.Tractor is { } spec)
            Paragraph(Theme.TextDim, Kit.F($"Automatic pickup · {spec.Range:0} m · {spec.MaxMass / 1000:0.#} t max · {spec.BasePower:0.#} MW + {spec.PowerPerTonne:0.##} MW/t"), width);
    }
}
