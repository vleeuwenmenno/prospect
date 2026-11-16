using Brutal.ImGuiApi;
using Brutal.Numerics;
using Prospect.Client.Ui;
using Prospect.Client.World;
using Prospect.Game.World;

namespace Prospect.Client.Screens;

internal sealed partial class ScreenHost
{
    /// <summary>What a row of the editor's context menu does.</summary>
    private enum EditorMenuRow : byte
    {
        Resize,
        Copy,
        Delete,
        SameType,
        Merge,
        Fuel,
        Split,
    }

    // Unscaled pixels.
    private const float MenuWidth = 250f;
    private const float MenuRowHeight = 28f;
    private const float MenuHeaderHeight = 26f;
    private const float MenuPad = 4f;
    private const float SubWidth = 230f;
    private const ImGuiWindowFlags MenuFlags = (RootFlags & ~ImGuiWindowFlags.NoBringToFrontOnFocus) | ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoFocusOnAppearing;

    private readonly EditorMenuRow[] _menuRows = new EditorMenuRow[8];

    /// <summary>
    /// The editor's context menu, opened by a right click on a placed block or a mounted part: what it applies to,
    /// then only the rows that apply, a disabled one saying why on its right. Resize, Fuel and Split act on the whole
    /// selection, or on the block clicked when it was not selected; Fuel and Split open a flyout. A click on a row
    /// acts and closes it; Escape, or a press anywhere else, closes it (<see cref="ShipEditor.HandleKeys"/> and the
    /// world do that). Nothing here is mirrored, as copy, paste and move are not.
    /// </summary>
    private void DrawEditorMenu(ShipEditor editor, BuildDraft draft, GameContent content)
    {
        var menu = editor.Menu;
        if (!menu.IsOpen)
            return;
        var boxes = draft.Boxes;
        if (menu.Mount >= 0 ? menu.Mount >= draft.Mounts.Length : (uint)menu.Block >= (uint)boxes.Length)
        {
            menu.Close();
            return;
        }

        var count = 0;
        if (menu.Mount < 0)
        {
            _menuRows[count++] = EditorMenuRow.Resize;
            _menuRows[count++] = EditorMenuRow.Copy;
            if (editor.Selection.Length > 1)
                _menuRows[count++] = EditorMenuRow.Merge;
            if (editor.SelectionIsTanks)
            {
                _menuRows[count++] = EditorMenuRow.Fuel;
                _menuRows[count++] = EditorMenuRow.Split;
            }
        }

        _menuRows[count++] = EditorMenuRow.SameType;
        _menuRows[count++] = EditorMenuRow.Delete;

        var scale = Kit.Scale;
        var rowHeight = MathF.Round(MenuRowHeight * scale);
        var pad = MathF.Round(MenuPad * scale);
        var header = MathF.Round(MenuHeaderHeight * scale);
        var size = new float2(MathF.Round(MenuWidth * scale), header + rowHeight * count + pad * 2);
        var placed = MapCourseMenu.Place(menu.At, size, _ui.DisplaySize, scale);
        var since = editor.Time - menu.OpenedAt;
        var appear = MapCourseMenu.Appear(since);
        var min = MapCourseMenu.Shown(placed, since, scale);

        ImGui.SetNextWindowPos(min);
        ImGui.SetNextWindowSize(size);
        ImGui.Begin("##editormenu"u8, MenuFlags);
        if (ImGui.IsWindowAppearing())
            ImGui.Internal.BringWindowToDisplayFront(ImGui.Internal.GetCurrentWindow());
        var previousFade = Kit.Fade;
        Kit.Fade = appear;
        var draw = ImGui.GetWindowDrawList();
        Kit.PanelFrame(draw, min, min + size);
        var blocks = editor.Selection.Length;
        var parts = editor.MountSelection.Length;
        var text = min + new float2(Kit.Px(Theme.Space3), Kit.Px(7));
        if (menu.Mount >= 0)
            Kit.ClippedText(draw, _ui.SansMedium, Theme.FontCaption, text, SelectionBlue, parts > 1 ? Kit.F($"{parts} MOUNTED PARTS") : Kit.F($"{draft.Mounts[menu.Mount].Type.Name}"), size.X - Kit.Px(Theme.Space3) * 2);
        else
            Kit.ClippedText(draw, _ui.SansMedium, Theme.FontCaption, text, SelectionBlue, blocks > 1 ? Kit.F($"{blocks} BLOCKS") : Kit.F($"{boxes[menu.Block].Type.Name}"), size.X - Kit.Px(Theme.Space3) * 2);
        draw.AddLine(new float2(min.X + Kit.Px(6), min.Y + header - 0.5f), new float2(min.X + size.X - Kit.Px(6), min.Y + header - 0.5f), Kit.Color(Theme.Line));

        var mouse = _app.Devices.MousePosition;
        var inner = new float2(size.X - Kit.Px(12), rowHeight);
        EditorMenuRow? chosen = null;
        EditorMenuRow? hovered = null;
        var rowTop = min.Y + header + pad;
        float subTop = rowTop;
        for (var i = 0; i < count; i++)
        {
            var row = _menuRows[i];
            var rowMin = new float2(min.X + Kit.Px(6), rowTop + i * rowHeight);
            if (Inside(rowMin, inner, mouse))
                hovered = row;
            if (row == menu.Sub switch { EditorMenuSub.Fuel => EditorMenuRow.Fuel, EditorMenuSub.Split => EditorMenuRow.Split, _ => (EditorMenuRow)255 })
                subTop = rowMin.Y;
            if (DrawEditorMenuRow(draw, editor, row, rowMin, inner))
                chosen = row;
        }

        ImGui.End();
        Kit.Fade = previousFade;

        // The flyout follows the pointer over the rows that have one and stays while it is on the flyout.
        if (hovered is { } over)
        {
            var sub = over switch { EditorMenuRow.Fuel => EditorMenuSub.Fuel, EditorMenuRow.Split => EditorMenuSub.Split, _ => EditorMenuSub.None };
            if (sub != menu.Sub)
            {
                menu.Sub = sub;
                if (sub != EditorMenuSub.None)
                    subTop = rowTop + Array.IndexOf(_menuRows, over, 0, count) * rowHeight;
            }
        }

        var subChosen = menu.Sub != EditorMenuSub.None && DrawEditorMenuSub(editor, draft, menu, min, size, subTop - pad, appear);
        if (subChosen)
            return;
        if (chosen is not { } done)
            return;
        switch (done)
        {
            case EditorMenuRow.Resize:
                editor.RequestResize();
                menu.Close();
                break;
            case EditorMenuRow.Copy:
                editor.CopySelection();
                menu.Close();
                break;
            case EditorMenuRow.Delete:
                editor.DeleteSelected();
                menu.Close();
                break;
            case EditorMenuRow.SameType:
                if (menu.Mount >= 0)
                    editor.SelectSameMountType(menu.Mount);
                else
                    editor.SelectSameType(editor.Selected);
                menu.Close();
                break;
            case EditorMenuRow.Merge:
                editor.MergeSelected();
                menu.Close();
                break;
            case EditorMenuRow.Fuel:
                menu.Sub = EditorMenuSub.Fuel;
                break;
            default:
                menu.Sub = EditorMenuSub.Split;
                break;
        }
    }

    private static bool Inside(float2 min, float2 size, float2 point) =>
        point.X >= min.X && point.X < min.X + size.X && point.Y >= min.Y && point.Y < min.Y + size.Y;

    /// <summary>One row of the menu: its label, a key or a reason on its right, and an arrow for a flyout.</summary>
    private bool DrawEditorMenuRow(ImDrawListPtr draw, ShipEditor editor, EditorMenuRow row, float2 rowMin, float2 rowSize)
    {
        ReadOnlySpan<byte> label;
        ReadOnlySpan<byte> note = default;
        var enabled = true;
        var arrow = false;
        var blocks = editor.Selection.Length;
        switch (row)
        {
            case EditorMenuRow.Resize:
                label = "Resize"u8;
                var offer = editor.ResizeState;
                enabled = offer == ResizeOffer.Yes;
                note = offer switch
                {
                    ResizeOffer.Yes => "S"u8,
                    ResizeOffer.Several => "ONE BLOCK AT A TIME"u8,
                    ResizeOffer.Busy => editor.CanEdit ? "NOT NOW"u8 : "REPAIR FIRST"u8,
                    _ => "SELECT A BLOCK"u8,
                };
                break;
            case EditorMenuRow.Copy:
                label = "Copy"u8;
                enabled = editor.CanEdit;
                note = enabled ? "CTRL C"u8 : "REPAIR FIRST"u8;
                break;
            case EditorMenuRow.Delete:
                label = editor.Menu.Mount >= 0 ? "Delete part"u8 : blocks > 1 ? Kit.F($"Delete {blocks} blocks") : "Delete block"u8;
                enabled = editor.CanDelete;
                note = enabled ? "DEL"u8 : editor.CanEdit ? "LEAVES NO BLOCK"u8 : "REPAIR FIRST"u8;
                break;
            case EditorMenuRow.SameType:
                label = "Select same type"u8;
                break;
            case EditorMenuRow.Merge:
                label = Kit.F($"Merge {blocks} blocks");
                enabled = editor.CanEdit && editor.MergeCheck == DraftEdit.Done;
                note = enabled ? default : editor.CanEdit ? MergeReason(editor.MergeCheck) : "REPAIR FIRST"u8;
                break;
            case EditorMenuRow.Fuel:
                label = "Fuel"u8;
                enabled = editor.CanEdit;
                note = enabled ? ConsoleFuel.Name(editor.SelectionFuel == FuelKind.None ? FuelKind.Bipropellant : editor.SelectionFuel) : "REPAIR FIRST"u8;
                if (enabled && editor.SelectionFuel == FuelKind.None)
                    note = "MIXED"u8;
                arrow = enabled;
                break;
            default:
                label = "Split"u8;
                var axes = 0;
                for (var axis = 0; axis < 3; axis++)
                    if (editor.SplitPieces(axis) > 0)
                        axes++;
                enabled = editor.CanEdit && axes > 0;
                note = enabled ? default : editor.CanEdit ? "ALL ONE CELL"u8 : "REPAIR FIRST"u8;
                arrow = enabled;
                break;
        }

        ImGui.PushID((int)row);
        ImGui.SetCursorScreenPos(rowMin);
        var pressed = ImGui.InvisibleButton("##row"u8, rowSize);
        var id = ImGui.GetItemID();
        ImGui.PopID();
        var hot = Kit.Animate(id, enabled && ImGui.IsItemHovered() ? 1f : 0f);
        EditorMenuRowLook(draw, rowMin, rowSize, label, note, enabled, hot, arrow, false);
        return pressed && enabled;
    }

    /// <summary>The look shared by the menu's rows and its flyouts' rows.</summary>
    private void EditorMenuRowLook(ImDrawListPtr draw, float2 rowMin, float2 rowSize, ReadOnlySpan<byte> label, ReadOnlySpan<byte> note, bool enabled, float hot, bool arrow, bool check)
    {
        if (hot > 0.01f)
        {
            draw.AddRectFilled(rowMin, rowMin + new float2(rowSize.X * (0.35f + 0.65f * hot), rowSize.Y), Kit.Color(Theme.PanelRaised, 0.9f * hot));
            draw.AddRectFilled(rowMin, rowMin + new float2(Kit.Px(3), rowSize.Y), Kit.Color(Theme.Accent, hot));
        }

        var left = rowMin.X + Kit.Px(Theme.Space3) + Kit.Px(6) * hot;
        var middle = rowMin.Y + rowSize.Y * 0.5f;
        if (check)
        {
            var c = new float2(rowMin.X + Kit.Px(Theme.Space3) + Kit.Px(3), middle);
            var color = Kit.Color(Theme.Accent);
            draw.AddLine(c + new float2(-Kit.Px(4), 0), c + new float2(-Kit.Px(1), Kit.Px(3)), color, Kit.Px(2));
            draw.AddLine(c + new float2(-Kit.Px(1), Kit.Px(3)), c + new float2(Kit.Px(5), -Kit.Px(4)), color, Kit.Px(2));
            left += Kit.Px(16);
        }

        var tone = enabled ? Kit.Mix(Theme.TextDim, Theme.Text, 0.55f + 0.45f * hot) : Theme.TextMute;
        var labelWidth = Kit.Measure(_ui.SansMedium, Theme.FontBody, label).X;
        Kit.Text(draw, _ui.SansMedium, Theme.FontBody, new float2(left, middle - Kit.Px(Theme.FontBody) * 0.55f), tone, label, enabled ? 1f : 0.7f);
        var right = rowMin.X + rowSize.X - Kit.Px(Theme.Space3);
        if (arrow)
        {
            var a = new float2(right - Kit.Px(3), middle);
            draw.AddTriangleFilled(a + new float2(-Kit.Px(4), -Kit.Px(5)), a + new float2(-Kit.Px(4), Kit.Px(5)), a + new float2(Kit.Px(3), 0), Kit.Color(enabled ? Kit.Mix(Theme.TextDim, Theme.Accent, hot) : Theme.TextMute));
            right -= Kit.Px(16);
        }

        if (note.IsEmpty)
            return;
        var room = Math.Max(0f, right - left - labelWidth - Kit.Px(Theme.Space2));
        var noteWidth = Math.Min(Kit.Measure(_ui.Mono, Theme.FontCaption, note).X, room);
        Kit.ClippedText(draw, _ui.Mono, Theme.FontCaption, new float2(right - noteWidth, middle - Kit.Px(Theme.FontCaption) * 0.55f),
            enabled ? Kit.Mix(Theme.TextMute, Theme.Accent, hot) : Theme.Bad, note, room);
    }

    private static ReadOnlySpan<byte> MergeReason(DraftEdit edit) => edit switch
    {
        DraftEdit.MixedTypes => "MIXED TYPES"u8,
        DraftEdit.NotABox => "NOT A BOX"u8,
        DraftEdit.BadSize => "TOO LONG"u8,
        _ => "SELECT AGAIN"u8,
    };

    /// <summary>
    /// The flyout beside the menu: the fuels to change the selected tanks to, the current one checked, or the axes to
    /// cut them along with how many pieces that makes. True when a row was chosen and acted on.
    /// </summary>
    private bool DrawEditorMenuSub(ShipEditor editor, BuildDraft draft, EditorMenu menu, float2 mainMin, float2 mainSize, float top, float appear)
    {
        var scale = Kit.Scale;
        var rowHeight = MathF.Round(MenuRowHeight * scale);
        var pad = MathF.Round(MenuPad * scale);
        Span<FuelKind> fuels = stackalloc FuelKind[4];
        var count = menu.Sub == EditorMenuSub.Fuel ? editor.FuelChoices(fuels) : 3;
        if (count == 0)
            return false;
        var size = new float2(MathF.Round(SubWidth * scale), rowHeight * count + pad * 2);
        var display = _ui.DisplaySize;
        var margin = MathF.Round(Theme.Space2 * scale);
        var x = mainMin.X + mainSize.X - Kit.Px(2);
        if (x + size.X > display.X - margin)
            x = mainMin.X - size.X + Kit.Px(2);
        var min = new float2(x, Math.Clamp(top, margin, Math.Max(margin, display.Y - size.Y - margin)));
        ImGui.SetNextWindowPos(min);
        ImGui.SetNextWindowSize(size);
        ImGui.Begin("##editormenusub"u8, MenuFlags);
        if (ImGui.IsWindowAppearing())
            ImGui.Internal.BringWindowToDisplayFront(ImGui.Internal.GetCurrentWindow());
        var previousFade = Kit.Fade;
        Kit.Fade = appear;
        var draw = ImGui.GetWindowDrawList();
        Kit.PanelFrame(draw, min, min + size);
        var inner = new float2(size.X - Kit.Px(12), rowHeight);
        var acted = false;
        var shown = 0;
        for (var i = 0; i < count; i++)
        {
            var rowMin = new float2(min.X + Kit.Px(6), min.Y + pad + shown * rowHeight);
            ReadOnlySpan<byte> label;
            ReadOnlySpan<byte> note = default;
            var enabled = true;
            var check = false;
            var axis = i;
            if (menu.Sub == EditorMenuSub.Fuel)
            {
                var fuel = fuels[i];
                label = ConsoleFuel.FullName(fuel);
                check = editor.SelectionFuel == fuel;
                var can = editor.CanChangeFuel(fuel);
                enabled = can == DraftEdit.Done;
                note = check ? "NOW"u8 : can switch { DraftEdit.Done => default, DraftEdit.BadSize => "TOO LONG"u8, DraftEdit.Unchanged => "NOW"u8, _ => "NOT NOW"u8 };
            }
            else
            {
                var pieces = editor.SplitPieces(axis);
                if (pieces == 0)
                    continue;
                label = Kit.F($"Along {AxisName(axis)} ({pieces} pieces)");
                var can = editor.CanSplit(axis);
                enabled = can == DraftEdit.Done;
                note = can switch { DraftEdit.Done => default, DraftEdit.TooManyBlocks => "BLOCK LIMIT"u8, DraftEdit.TooManyTanks => "TANK LIMIT"u8, _ => "NOT NOW"u8 };
            }

            shown++;
            ImGui.PushID(i);
            ImGui.SetCursorScreenPos(rowMin);
            var pressed = ImGui.InvisibleButton("##sub"u8, inner);
            var id = ImGui.GetItemID();
            ImGui.PopID();
            var hot = Kit.Animate(id, enabled && ImGui.IsItemHovered() ? 1f : 0f);
            EditorMenuRowLook(draw, rowMin, inner, label, note, enabled, hot, false, check);
            if (!pressed || !enabled)
                continue;
            if (menu.Sub == EditorMenuSub.Fuel)
                editor.ChangeFuel(fuels[i]);
            else
                editor.SplitSelected(axis);
            menu.Close();
            acted = true;
            break;
        }

        ImGui.End();
        Kit.Fade = previousFade;
        return acted;
    }
}
