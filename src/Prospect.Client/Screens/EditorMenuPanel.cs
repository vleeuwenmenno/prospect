using Brutal.ImGuiApi;
using Brutal.Numerics;
using Prospect.Client.Ui;
using Prospect.Client.World;
using Prospect.Game.World;

namespace Prospect.Client.Screens;

/// <summary>
/// The ship editor's context menu window and its flyout: layout, hover, the rows' state and what a press does. A
/// class of its own, with the pointer handed in, so a headless test runs the very windows the game draws.
/// </summary>
internal sealed class EditorMenuPanel(UiContext ui)
{
    /// <summary>What a row of the editor's context menu does.</summary>
    internal enum EditorMenuRow : byte
    {
        Resize,
        Copy,
        Delete,
        SameType,
        Merge,
        Fuel,
        Split,
        Group,
        Size,
    }

    // Unscaled pixels.
    private const float MenuWidth = 250f;
    private const float MenuRowHeight = 28f;
    private const float MenuHeaderHeight = 26f;
    private const float MenuPad = 4f;
    private const float SubWidth = 230f;
    private const ImGuiWindowFlags MenuFlags = (ScreenHost.RootFlags & ~ImGuiWindowFlags.NoBringToFrontOnFocus) | ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoFocusOnAppearing;

    private readonly EditorMenuRow[] _menuRows = new EditorMenuRow[8];
    /// <summary>Seconds the pointer must stay on a row before it takes the flyout over from the one that is out.</summary>
    internal const float FlyoutSwitchSeconds = 0.2f;

    private EditorMenuRow? _waiting;
    private float _waitingSince;
    private int _rowCount;
    private float2 _mainMin;
    private float2 _rowSize;
    private bool _subShown;
    private float2 _subMin;
    private int _subRows;

    /// <summary>
    /// Where the row <paramref name="row"/> of the menu was drawn last frame, its middle in screen pixels, for a test
    /// that drives the pointer. False when the menu has no such row.
    /// </summary>
    internal bool TryRowCentre(EditorMenuRow row, out float2 centre)
    {
        for (var i = 0; i < _rowCount; i++)
        {
            if (_menuRows[i] != row)
                continue;
            var rowHeight = _rowSize.Y;
            centre = new float2(_mainMin.X + _rowSize.X * 0.5f + Kit.Px(6), _mainMin.Y + MathF.Round(MenuHeaderHeight * Kit.Scale) + MathF.Round(MenuPad * Kit.Scale) + (i + 0.5f) * rowHeight);
            return true;
        }

        centre = default;
        return false;
    }

    /// <summary>Where the flyout's <paramref name="shown"/>th drawn row was last frame, its middle; false when no flyout was drawn.</summary>
    internal bool TrySubRowCentre(int shown, out float2 centre)
    {
        centre = default;
        if (!_subShown || shown >= _subRows)
            return false;
        centre = new float2(_subMin.X + Kit.Px(6) + (MathF.Round(SubWidth * Kit.Scale) - Kit.Px(12)) * 0.5f, _subMin.Y + MathF.Round(MenuPad * Kit.Scale) + (shown + 0.5f) * _rowSize.Y);
        return true;
    }

    /// <summary>
    /// The editor's context menu, opened by a right click on a placed block or a mounted part: what it applies to,
    /// then only the rows that apply, a disabled one saying why on its right. Resize, Fuel and Split act on the whole
    /// selection, or on the block clicked when it was not selected; Fuel and Split open a flyout. A click on a row
    /// acts and closes it; Escape, or a press anywhere else, closes it (<see cref="ShipEditor.HandleKeys"/> and the
    /// world do that). Nothing here is mirrored, as copy, paste and move are not.
    /// </summary>
    public void Draw(ShipEditor editor, BuildDraft draft, float2 mouse)
    {
        var menu = editor.Menu;
        if (!menu.IsOpen)
        {
            _waiting = null;
            return;
        }

        // Set again by whichever row holds a press as it is drawn below.
        menu.Pressing = false;
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

        else
        {
            if (editor.SelectionIsGroupable)
                _menuRows[count++] = EditorMenuRow.Group;
            if (editor.SelectionSizes.Count > 0)
                _menuRows[count++] = EditorMenuRow.Size;
        }

        _menuRows[count++] = EditorMenuRow.SameType;
        _menuRows[count++] = EditorMenuRow.Delete;

        var scale = Kit.Scale;
        var rowHeight = MathF.Round(MenuRowHeight * scale);
        var pad = MathF.Round(MenuPad * scale);
        var header = MathF.Round(MenuHeaderHeight * scale);
        var size = new float2(MathF.Round(MenuWidth * scale), header + rowHeight * count + pad * 2);
        var placed = MapCourseMenu.Place(menu.At, size, ui.DisplaySize, scale);
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
            Kit.ClippedText(draw, ui.SansMedium, Theme.FontCaption, text, ScreenHost.SelectionBlue, parts > 1 ? Kit.F($"{parts} MOUNTED PARTS") : Kit.F($"{draft.Mounts[menu.Mount].Type.Name}"), size.X - Kit.Px(Theme.Space3) * 2);
        else
            Kit.ClippedText(draw, ui.SansMedium, Theme.FontCaption, text, ScreenHost.SelectionBlue, blocks > 1 ? Kit.F($"{blocks} BLOCKS") : Kit.F($"{boxes[menu.Block].Type.Name}"), size.X - Kit.Px(Theme.Space3) * 2);
        draw.AddLine(new float2(min.X + Kit.Px(6), min.Y + header - 0.5f), new float2(min.X + size.X - Kit.Px(6), min.Y + header - 0.5f), Kit.Color(Theme.Line));

        var inner = new float2(size.X - Kit.Px(12), rowHeight);
        _rowCount = count;
        _mainMin = min;
        _rowSize = inner;
        _subShown = false;
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
            if (row == menu.Sub switch { EditorMenuSub.Fuel => EditorMenuRow.Fuel, EditorMenuSub.Split => EditorMenuRow.Split, EditorMenuSub.Group => EditorMenuRow.Group, EditorMenuSub.Size => EditorMenuRow.Size, _ => (EditorMenuRow)255 })
                subTop = rowMin.Y;
            if (DrawRow(draw, editor, row, rowMin, inner))
                chosen = row;
        }

        ImGui.End();
        Kit.Fade = previousFade;

        // The flyout opens at once on a row that has one. Once it is out, another row takes it over (or takes it away)
        // only after the pointer has stayed on that row a moment: a hand going from Fuel to a fuel in the flyout
        // crosses the rows below Fuel on the way and must not lose the flyout to them.
        if (hovered is { } over)
        {
            var sub = over switch { EditorMenuRow.Fuel => EditorMenuSub.Fuel, EditorMenuRow.Split => EditorMenuSub.Split, EditorMenuRow.Group => EditorMenuSub.Group, EditorMenuRow.Size => EditorMenuSub.Size, _ => EditorMenuSub.None };
            if (sub == menu.Sub)
                _waiting = null;
            else
            {
                if (_waiting != over)
                {
                    _waiting = over;
                    _waitingSince = editor.Time;
                }

                if (menu.Sub == EditorMenuSub.None || editor.Time - _waitingSince >= FlyoutSwitchSeconds)
                {
                    menu.Sub = sub;
                    _waiting = null;
                    if (sub != EditorMenuSub.None)
                        subTop = rowTop + Array.IndexOf(_menuRows, over, 0, count) * rowHeight;
                }
            }
        }
        else
            _waiting = null;

        var subChosen = menu.Sub != EditorMenuSub.None && DrawSub(editor, menu, min, size, subTop - pad, appear);
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
            case EditorMenuRow.Group:
                menu.Sub = EditorMenuSub.Group;
                break;
            case EditorMenuRow.Size:
                menu.Sub = EditorMenuSub.Size;
                break;
            default:
                menu.Sub = EditorMenuSub.Split;
                break;
        }
    }

    private static bool Inside(float2 min, float2 size, float2 point) =>
        point.X >= min.X && point.X < min.X + size.X && point.Y >= min.Y && point.Y < min.Y + size.Y;

    /// <summary>One row of the menu: its label, a key or a reason on its right, and an arrow for a flyout.</summary>
    private bool DrawRow(ImDrawListPtr draw, ShipEditor editor, EditorMenuRow row, float2 rowMin, float2 rowSize)
    {
        ReadOnlySpan<byte> label;
        ReadOnlySpan<byte> note = default;
        var noteTone = 0u;
        var enabled = true;
        var arrow = false;
        var blocks = editor.Selection.Length;
        var plan = editor.MenuFuelPlan;
        var dumped = plan.Dumped.Nitrogen + plan.Dumped.Hydrazine + plan.Dumped.Bipropellant;
        var moved = plan.Moved.Nitrogen + plan.Moved.Hydrazine + plan.Moved.Bipropellant;

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
                if (enabled && editor.MenuFuelReady && editor.Menu.Mount < 0 && (dumped > 0f || moved > 0f))
                {
                    note = dumped > 0f ? Kit.F($"DUMPS {dumped:0.##} KG") : Kit.F($"MOVES {moved:0.##} KG");
                    noteTone = dumped > 0f ? 0xFF8A3Du : Theme.Info;
                }
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
                if (enabled && editor.MenuFuelReady && (dumped > 0f || moved > 0f))
                {
                    note = dumped > 0f ? Kit.F($"DUMPS {dumped:0.##} KG") : Kit.F($"MOVES {moved:0.##} KG");
                    noteTone = dumped > 0f ? 0xFF8A3Du : Theme.Info;
                }
                arrow = enabled;
                break;
            case EditorMenuRow.Group:
                label = "Group"u8;
                enabled = editor.CanEdit;
                var group = editor.SelectionGroup;
                note = !enabled ? "REPAIR FIRST"u8 : group < 0 ? "MIXED"u8 : group == ActionGroups.None ? "NONE"u8 : Kit.F($"{ActionGroups.Digit(group)}");
                arrow = enabled;
                break;
            case EditorMenuRow.Size:
                label = "Size"u8;
                enabled = editor.CanEdit;
                var units = editor.SelectionSize;
                note = !enabled ? "REPAIR FIRST"u8 : units < 0 ? "MIXED"u8 : Kit.F($"{units} X {units}");
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
        if (ImGui.IsItemActive())
            editor.Menu.Pressing = true;
        var id = ImGui.GetItemID();
        ImGui.PopID();
        var hot = Kit.Animate(id, enabled && ImGui.IsItemHovered() ? 1f : 0f);
        RowLook(draw, rowMin, rowSize, label, note, enabled, hot, arrow, false, noteTone);
        return pressed && enabled;
    }

    /// <summary>The look shared by the menu's rows and its flyouts' rows.</summary>
    private void RowLook(ImDrawListPtr draw, float2 rowMin, float2 rowSize, ReadOnlySpan<byte> label, ReadOnlySpan<byte> note, bool enabled, float hot, bool arrow, bool check, uint noteTone = 0)
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
        var labelWidth = Kit.Measure(ui.SansMedium, Theme.FontBody, label).X;
        Kit.Text(draw, ui.SansMedium, Theme.FontBody, new float2(left, middle - Kit.Px(Theme.FontBody) * 0.55f), tone, label, enabled ? 1f : 0.7f);
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
        var noteWidth = Math.Min(Kit.Measure(ui.Mono, Theme.FontCaption, note).X, room);
        Kit.ClippedText(draw, ui.Mono, Theme.FontCaption, new float2(right - noteWidth, middle - Kit.Px(Theme.FontCaption) * 0.55f),
            noteTone != 0 && enabled ? noteTone : enabled ? Kit.Mix(Theme.TextMute, Theme.Accent, hot) : Theme.Bad, note, room);
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
    private bool DrawSub(ShipEditor editor, EditorMenu menu, float2 mainMin, float2 mainSize, float top, float appear)
    {
        var scale = Kit.Scale;
        var rowHeight = MathF.Round(MenuRowHeight * scale);
        var pad = MathF.Round(MenuPad * scale);
        Span<FuelKind> fuels = stackalloc FuelKind[4];
        var sizes = editor.SelectionSizes;
        var count = menu.Sub switch { EditorMenuSub.Fuel => editor.FuelChoices(fuels), EditorMenuSub.Group => ActionGroups.Count + 1, EditorMenuSub.Size => sizes.Count, _ => 3 };
        if (count == 0)
            return false;
        var size = new float2(MathF.Round(SubWidth * scale), rowHeight * count + pad * 2);
        var display = ui.DisplaySize;
        var margin = MathF.Round(Theme.Space2 * scale);
        var x = mainMin.X + mainSize.X - Kit.Px(2);
        if (x + size.X > display.X - margin)
            x = mainMin.X - size.X + Kit.Px(2);
        var min = new float2(x, Math.Clamp(top, margin, Math.Max(margin, display.Y - size.Y - margin)));
        _subShown = true;
        _subMin = min;
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
            else if (menu.Sub == EditorMenuSub.Group)
            {
                // Groups 1 to 9, then 0 (group 10) as the keys run, then none.
                var group = i < ActionGroups.Count ? i + 1 : ActionGroups.None;
                label = group == ActionGroups.None ? "None"u8 : Kit.F($"Group {ActionGroups.Digit(group)}");
                check = editor.SelectionGroup == group;
                var can = editor.CanSetGroup((byte)group);
                enabled = can == DraftEdit.Done;
                note = check ? "NOW"u8 : can switch { DraftEdit.Done => default, _ => "NOT NOW"u8 };
            }
            else if (menu.Sub == EditorMenuSub.Size)
            {
                // One row a size the part is made in: so many nozzles a side.
                var units = sizes[i];
                label = Kit.F($"{units} x {units}");
                check = editor.SelectionSize == units;
                var can = editor.CanSetSize(units);
                enabled = can == DraftEdit.Done;
                note = check ? "NOW"u8 : can switch { DraftEdit.Done => default, DraftEdit.Unchanged => "NOW"u8, DraftEdit.OffFace => "FACE TOO SMALL"u8, DraftEdit.Overlaps => "NO ROOM"u8, _ => "NOT NOW"u8 };
            }
            else
            {
                var pieces = editor.SplitPieces(axis);
                if (pieces == 0)
                    continue;
                label = Kit.F($"Along {ScreenHost.AxisName(axis)} ({pieces} pieces)");
                var can = editor.CanSplit(axis);
                enabled = can == DraftEdit.Done;
                note = can switch { DraftEdit.Done => default, DraftEdit.TooManyBlocks => "BLOCK LIMIT"u8, DraftEdit.TooManyTanks => "TANK LIMIT"u8, _ => "NOT NOW"u8 };
            }

            shown++;
            ImGui.PushID(i);
            ImGui.SetCursorScreenPos(rowMin);
            var pressed = ImGui.InvisibleButton("##sub"u8, inner);
            if (ImGui.IsItemActive())
                menu.Pressing = true;
            var id = ImGui.GetItemID();
            ImGui.PopID();
            var hot = Kit.Animate(id, enabled && ImGui.IsItemHovered() ? 1f : 0f);
            RowLook(draw, rowMin, inner, label, note, enabled, hot, false, check);
            if (!pressed || !enabled)
                continue;
            if (menu.Sub == EditorMenuSub.Fuel)
                editor.ChangeFuel(fuels[i]);
            else if (menu.Sub == EditorMenuSub.Group)
                editor.SetSelectedGroup((byte)(i < ActionGroups.Count ? i + 1 : ActionGroups.None));
            else if (menu.Sub == EditorMenuSub.Size)
                editor.SetSelectedSize(sizes[i]);
            else
                editor.SplitSelected(axis);
            menu.Close();
            acted = true;
            break;
        }

        _subRows = shown;
        ImGui.End();
        Kit.Fade = previousFade;
        return acted;
    }
}
