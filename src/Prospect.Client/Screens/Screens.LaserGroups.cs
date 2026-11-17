using Brutal.ImGuiApi;
using Brutal.Numerics;
using Prospect.Client.Ui;
using Prospect.Client.World;
using Prospect.Game.World;

namespace Prospect.Client.Screens;

internal sealed partial class ScreenHost
{
    /// <summary>
    /// The action group selector in the details panel, for a selection of mounted parts that may be in a group (a
    /// mining laser): a row of chips 1 to 9 and 0 (group 10) and None below, the group every selected part is in
    /// highlighted (none when they differ). A press goes through <see cref="ShipEditor.SetSelectedGroup"/>, the same
    /// call the context menu's Group row makes, so the two cannot disagree. Nothing is drawn for a part that has no
    /// group. Takes no memory per frame.
    /// </summary>
    private void DrawGroupSelector(ShipEditor editor, float width)
    {
        if (!editor.SelectionIsGroupable)
            return;
        var current = editor.SelectionGroup;
        var count = editor.MountSelection.Length;
        Kit.Space(Theme.Space2);
        Kit.Caption("ACTION GROUP"u8, Theme.Accent);
        Kit.Space(Theme.Space1);
        var gap = Kit.Px(Theme.Space1);
        var chip = MathF.Floor((width - gap * (ActionGroups.Count - 1)) / ActionGroups.Count);
        for (var i = 0; i < ActionGroups.Count; i++)
        {
            // The key order: 1 to 9, then 0, which is group 10.
            var group = i + 1;
            if (i > 0)
                ImGui.SameLine(0, gap);
            ImGui.PushID(group);
            if (EditorButton("##group"u8, Kit.F($"{ActionGroups.Digit(group)}"), new float2(chip, Kit.Px(26)), editor.CanEdit, false, current == group))
                editor.SetSelectedGroup((byte)group);
            ImGui.PopID();
        }

        Kit.Space(Theme.Space1);
        if (EditorButton("##groupnone"u8, "None"u8, new float2(width, Kit.Px(24)), editor.CanEdit, false, current == ActionGroups.None))
            editor.SetSelectedGroup(ActionGroups.None);
        Kit.Space(Theme.Space1);
        if (current > 0)
            Paragraph(Theme.TextDim, count > 1 ? Kit.F($"All {count} in group {ActionGroups.Digit(current)}. The number key {ActionGroups.Digit(current)} turns it on in flight.") : Kit.F($"Group {ActionGroups.Digit(current)}. The number key {ActionGroups.Digit(current)} turns it on in flight."), width);
        else if (current == ActionGroups.None)
            Paragraph(Theme.TextDim, "No group: this part never fires."u8, width);
        else
            Paragraph(Theme.TextDim, "The selected parts are in different groups."u8, width);
    }
}
