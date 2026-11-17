using Brutal.ImGuiApi;
using Brutal.Numerics;
using Prospect.Client.Ui;
using Prospect.Client.World;
using Prospect.Game.World;

namespace Prospect.Client.Screens;

internal sealed partial class ScreenHost
{
    /// <summary>
    /// While a part made in several sizes is the part to place (a cold gas array): one button a size, so many nozzles
    /// a side, the chosen one highlighted. A press goes through <see cref="ShipEditor.SetMountSize"/>, which the
    /// sizing key steps too, so the ghost on the ship is the size shown here. Nothing is drawn for any other part.
    /// Takes no memory per frame.
    /// </summary>
    private void DrawMountSizeChoice(ShipEditor editor, float width)
    {
        var sizes = editor.PartSizes;
        if (!editor.Placing || !editor.CanEdit || sizes.Count == 0)
            return;
        Kit.Space(Theme.Space1);
        Kit.Caption("ARRAY SIZE"u8, Theme.Accent);
        Kit.Space(Theme.Space1);
        var gap = Kit.Px(Theme.Space1);
        var chip = MathF.Floor((width - gap * (sizes.Count - 1)) / sizes.Count);
        var now = editor.MountSize;
        for (var i = 0; i < sizes.Count; i++)
        {
            if (i > 0)
                ImGui.SameLine(0, gap);
            ImGui.PushID(i);
            if (EditorPalette.HandBack(EditorButton("##mountsize"u8, Kit.F($"{sizes[i]} x {sizes[i]}"), new float2(chip, Kit.Px(26)), true, false, now == sizes[i])))
                editor.SetMountSize(sizes[i]);
            ImGui.PopID();
        }

        Kit.Space(Theme.Space1);
        Paragraph(Theme.TextDim, Kit.F($"{now * now} {(now == 1 ? "nozzle" : "nozzles")}. S steps the size."), width);
    }

    /// <summary>
    /// The size selector in the details panel, for a selection of mounted parts of one type made in several sizes: one
    /// button a size, the size every selected part has highlighted (none when they differ), a size with no room on
    /// its face off. A press goes through <see cref="ShipEditor.SetSelectedSize"/>, the same call the context menu's
    /// Size row makes, so the two cannot disagree. Nothing is drawn for any other part. Takes no memory per frame.
    /// </summary>
    private void DrawSizeSelector(ShipEditor editor, float width)
    {
        var sizes = editor.SelectionSizes;
        if (sizes.Count == 0)
            return;
        var current = editor.SelectionSize;
        Kit.Space(Theme.Space2);
        Kit.Caption("ARRAY SIZE"u8, Theme.Accent);
        Kit.Space(Theme.Space1);
        var gap = Kit.Px(Theme.Space1);
        var chip = MathF.Floor((width - gap * (sizes.Count - 1)) / sizes.Count);
        var blocked = false;
        for (var i = 0; i < sizes.Count; i++)
        {
            if (i > 0)
                ImGui.SameLine(0, gap);
            var can = editor.CanSetSize(sizes[i]);
            blocked |= can is DraftEdit.OffFace or DraftEdit.Overlaps;
            ImGui.PushID(i);
            if (EditorButton("##selectedsize"u8, Kit.F($"{sizes[i]} x {sizes[i]}"), new float2(chip, Kit.Px(26)), can == DraftEdit.Done, false, current == sizes[i]))
                editor.SetSelectedSize(sizes[i]);
            ImGui.PopID();
        }

        Kit.Space(Theme.Space1);
        if (current < 0)
            Paragraph(Theme.TextDim, "The selected parts are of different sizes."u8, width);
        else if (blocked)
            Paragraph(Theme.TextDim, "A size that is off has no room on this face."u8, width);
    }
}
