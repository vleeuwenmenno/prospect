using System.Numerics;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using Prospect.Client.Input;
using Prospect.Client.Ui;
using Prospect.Client.World;
using Prospect.Game.Client;
using Prospect.Game.Protocol;
using Prospect.Game.World;

namespace Prospect.Client.Screens;

internal sealed partial class ScreenHost
{
    private const ImGuiWindowFlags EditorPanelFlags = RootFlags | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoNav;

    /// <summary>Seconds a refused placement or resize stays explained.</summary>
    private const float EditMessageSeconds = 2.5f;

    /// <summary>Seconds a commit the server turned down stays explained in the bar.</summary>
    private const float RefusalSeconds = 8f;

    private bool _editorHandled;
    private string? _editorKey;
    private byte[]? _editorHints;

    /// <summary>True when the editor's key or Escape was used this frame; the world and the menu then leave it alone.</summary>
    public bool EditorHandled => _editorHandled;

    /// <summary>The editor key as the bindings name it, for the interface's sentences.</summary>
    private string EditorKey
    {
        get
        {
            if (_editorKey is null)
            {
                var bindings = _app.Actions.Bindings(InputAction.ToggleEditor);
                _editorKey = bindings.IsEmpty ? "the editor key (unbound)" : bindings[0].DisplayName.ToUpperInvariant();
            }

            return _editorKey;
        }
    }

    /// <summary>
    /// The editor's key opens a request to edit the flown ship, or cancels the edit at any stage; Escape cancels
    /// too. Read before flight, and only with nothing else open: the menu, the map, a data sheet, Controls or the
    /// social workspace keep their own keys, and a text field keeps its letters.
    /// </summary>
    public void HandleEditorShortcut(bool active, SessionSnapshot? snapshot, PlayerInputBridge? bridge)
    {
        _editorHandled = false;
        // Escape that closed a tip was the tip's.
        if (!active || _tipBack || _screen != Screen.Session || snapshot is not { OwnHull: >= 0 } || bridge is null)
            return;
        if (_controlsHandled || _controlsOpen || _sessionMenuOpen || _mapOpen || _shipInfoOpen || SocialSurfaceOpen || _socialHandled)
            return;
        _editorHandled = _app.World.Editor.HandleKeys(_app.Actions, _app.Devices, snapshot.Hulls[snapshot.OwnHull], bridge, _ui.WantsTextInput, _ui.PointerOverInterface);
    }

    /// <summary>The waiting request, or what became of the last edit, under the bar while the editor is not shown.</summary>
    private void DrawEditorNotice(ImDrawListPtr draw, ShipEditor editor)
    {
        var requesting = editor.Stage == EditorStage.Requesting;
        var recent = editor.Time - editor.NoticeAt < ShipEditor.NoticeSeconds && (editor.NoticeApplied || editor.Notice is not (EditorReason.None or EditorReason.Cancelled));
        if (!requesting && !recent)
            return;

        ReadOnlySpan<byte> text;
        uint tone;
        if (requesting)
        {
            text = Kit.F($"Asking the server to edit this ship...");
            tone = Theme.Info;
        }
        else if (editor.NoticeApplied)
        {
            text = Kit.F($"Ship rebuilt. You are flying the new design.");
            tone = Theme.Good;
        }
        else
        {
            // Formatted first: one Kit.F inside another would write into the same arena while it is being filled.
            var reason = ReasonText(editor.Notice, editor.NoticeWait);
            text = editor.DraftKept ? Kit.F($"{reason} Your changes are kept: press {EditorKey} to go on.") : reason;
            tone = Theme.Bad;
        }

        var width = Math.Min(_ui.DisplaySize.X - Kit.Px(Theme.Space5) * 2, Kit.Px(620));
        var min = new float2(MathF.Round((_ui.DisplaySize.X - width) * 0.5f), HudTop + Kit.Px(Theme.Space6) * 2);
        var height = Kit.Px(34);
        Hud.Plate(draw, min, min + new float2(width, height), tone);
        var left = Kit.Px(Theme.Space4);
        if (requesting)
        {
            Kit.Spinner(draw, min + new float2(Kit.Px(Theme.Space4) + Kit.Px(6), height * 0.5f), Kit.Px(6));
            left += Kit.Px(20);
        }

        Kit.ClippedText(draw, _ui.SansMedium, Theme.FontSmall, min + new float2(left, Kit.Px(8)), tone == Theme.Bad ? Theme.Text : tone, text, width - left - Kit.Px(Theme.Space4));
    }

    /// <summary>The blue every selected block is outlined in, apart from the amber of acting and the red of loss.</summary>
    private const uint SelectionBlue = 0x4D8DFF;

    private readonly EditorPalette _palette = new();
    private readonly ImInputString _partSearch = new(48);
    private EditorTuning? _editorTuning;

    /// <summary>The editor's tuning, loaded the first time the editor is drawn.</summary>
    private EditorTuning Tuning => _editorTuning ??= EditorTuning.Load();

    /// <summary>The editor over the world: the marks on the draft, the parts, the details and the bar.</summary>
    private void DrawEditor(ImDrawListPtr draw, SessionSnapshot snapshot, WorldView world)
    {
        var editor = world.Editor;
        var bridge = _app.Session?.Client.Input;
        if (editor.Draft is not { } draft || editor.Content is not { } content || bridge is null)
            return;
        editor.RefreshMerge();
        // A press on a panel ends a turn, keeping it, before its rings are drawn.
        world.EndPartRotationOnPanelPress();
        DrawEditorMarks(draw, world, editor, draft);
        var layout = EditorLayout.Of(_ui.DisplaySize, Kit.Scale);
        DrawEditorParts(layout.Parts, editor, content);
        DrawEditorDetails(layout.Details, editor, draft, content, bridge);
        DrawEditorBar(layout.Bar, editor, draft, bridge);
    }

    /// <summary>
    /// Lost blocks in red, every selected block in blue with the chosen face of the latest, the face under the
    /// pointer, and the ghost of whatever is being placed, moved or pasted: green where it fits, red with the reason.
    /// </summary>
    private void DrawEditorMarks(ImDrawListPtr draw, WorldView world, ShipEditor editor, BuildDraft draft)
    {
        var boxes = draft.Boxes;
        if (editor.MissingCount > 0)
            for (var i = 0; i < boxes.Length; i++)
                if (editor.IsMissing(i) && !editor.IsSelected(i))
                    DrawBlockEdges(draw, world, boxes[i], Kit.Color(Theme.Bad, 0.85f), Kit.Px(1.5f));

        var moving = editor.Carry == EditorCarry.Move && editor.HasCarryGhost;
        foreach (var block in editor.Selection)
        {
            if ((uint)block >= (uint)boxes.Length)
                continue;
            ref readonly var box = ref boxes[block];
            DrawBlockEdges(draw, world, box, Kit.Color(SelectionBlue, moving ? 0.45f : 1f), Kit.Px(block == editor.Selected ? 2.5f : 2f));
        }

        if ((uint)editor.Selected < (uint)boxes.Length && !moving)
        {
            ref readonly var box = ref boxes[editor.Selected];
            DrawFace(draw, world, box, editor.Face, Kit.Color(SelectionBlue, 0.25f), Kit.Color(SelectionBlue));
        }

        DrawSizingNote(world, editor);
        if (!editor.HasHover)
            return;
        if (editor.HasCarryGhost)
        {
            // The whole group where it would land, each block outlined, with the reason when it does not fit.
            var ok = editor.CarryCheck == DraftEdit.Done;
            var tone = Kit.Color(ok ? Theme.Good : Theme.Bad);
            var by = editor.CarryBy;
            if (editor.Carry == EditorCarry.Move)
            {
                foreach (var block in editor.Selection)
                    if ((uint)block < (uint)boxes.Length)
                        DrawBlockEdges(draw, world, boxes[block] with { At = Shift(boxes[block].At, by) }, tone, Kit.Px(2));
            }
            else
            {
                foreach (ref readonly var box in editor.Clipboard)
                    DrawBlockEdges(draw, world, box with { At = Shift(box.At, by) }, tone, Kit.Px(2));
            }

            if (!ok)
                PointerNote(editor.CarryCheck == DraftEdit.Unchanged ? "Already there."u8 : EditText(editor.CarryCheck));
            return;
        }

        if (editor.HasGhost)
        {
            var ok = editor.GhostCheck == DraftEdit.Done;
            var tone = ok ? Theme.Good : Theme.Bad;
            var ghost = new DesignBox(editor.GhostAt, editor.PartSize, null!, editor.PartShape);
            // While it glides into a turn the whole ghost, faces and all, is drawn turned the same way.
            world.GhostPose(out var turn, out var shift);
            DrawBlockEdges(draw, world, ghost, Kit.Color(tone), Kit.Px(2), turn, shift);
            // The ghost's face against the hull, filled, shows where it attaches; a wedge being turned shows its slant.
            DrawFace(draw, world, ghost, editor.Hover.Side ^ 1, Kit.Color(tone, 0.22f), Kit.Color(tone, 0.6f), turn, shift);
            if (editor.RotatingPart && editor.PartShape != BlockShape.Box)
                DrawFace(draw, world, ghost, BlockShapes.SlantSide(editor.PartShape), Kit.Color(tone, 0.35f), Kit.Color(tone), turn, shift);
            var sizing = world.PartSizingFace;
            if (sizing >= 0)
                DrawSizingFace(draw, world, ghost, sizing);
            if (editor.RotatingPart)
                DrawRotationRings(draw, world);
            if (!ok)
                PointerNote(EditText(editor.GhostCheck));
        }
        else if ((uint)editor.Hover.Block < (uint)boxes.Length)
        {
            ref readonly var box = ref boxes[editor.Hover.Block];
            DrawFace(draw, world, box, editor.Hover.Side, Kit.Color(Theme.Accent, 0.18f), Kit.Color(Theme.Accent, 0.8f));
        }
    }

    private static GridSize Shift(GridSize at, GridSize by) => new(at.X + by.X, at.Y + by.Y, at.Z + by.Z);

    /// <summary>The colours of the rotation rings: about x red, about y green, as the axes are drawn.</summary>
    private const uint RingX = 0xE0605A;
    private const uint RingY = 0x6AD07A;

    /// <summary>
    /// The two rings around the ghost of a part being turned, the one under the pointer brighter and thicker, each
    /// named by its axis, and how to use them beside the pointer.
    /// </summary>
    private void DrawRotationRings(ImDrawListPtr draw, WorldView world)
    {
        if (!world.PartRings(out var middle, out var radius))
            return;
        var hovered = world.RotationRingHovered;
        for (var axis = 0; axis < 2; axis++)
        {
            var tone = axis == 0 ? RingX : RingY;
            var color = Kit.Color(tone, hovered == axis ? 1f : 0.7f);
            var thickness = Kit.Px(hovered == axis ? 3.5f : 2f);
            for (var i = 0; i < WorldView.RingSegments; i++)
            {
                if (world.ProjectSegment(world.EditorPoint(WorldView.RingPoint(middle, radius, axis, i)), world.EditorPoint(WorldView.RingPoint(middle, radius, axis, i + 1)), out var a, out var b))
                    draw.AddLine(a, b, color, thickness);
            }

            if (world.Project(world.EditorPoint(WorldView.RingPoint(middle, radius, axis, 0)), out var label))
                Kit.Text(draw, _ui.SansMedium, Theme.FontSmall, label + new float2(Kit.Px(6), -Kit.Px(18)), tone, axis == 0 ? "TURN X"u8 : "TURN Y"u8);
        }

        PointerNote(world.RotationRingDragged ? "Drag round: a quarter turn at each half way."u8
            : hovered >= 0 ? "Drag round, wheel or click: quarter turns. Shift+click back."u8
            : "Click the ghost to place it turned. R keeps the turn, Esc puts it back."u8, Theme.Info, -Kit.Px(28));
    }

    /// <summary>
    /// While S holds the ghost to be sized: its size on all three axes, and what to do next, beside the pointer.
    /// </summary>
    private void DrawSizingNote(WorldView world, ShipEditor editor)
    {
        if (!editor.SizingPart || editor.Content is not { } content)
            return;
        var size = editor.PartSize;
        var face = world.PartSizingFace;
        var limit = content.Blocks.Limits.MaxBoxSide;
        if (face < 0)
            PointerNote(Kit.F($"SIZE X {size.X}  Y {size.Y}  Z {size.Z}: point at a face of the ghost, then drag it"), Theme.Info, -Kit.Px(28));
        else if (world.PartSizingEdgeOn)
            PointerNote(Kit.F($"SIZE X {size.X}  Y {size.Y}  Z {size.Z} (1 to {limit}): {FaceName(face)} faces you, drag up or down"), Theme.Info, -Kit.Px(28));
        else
            PointerNote(Kit.F($"SIZE X {size.X}  Y {size.Y}  Z {size.Z} (1 to {limit}): drag the {FaceName(face)} face"), Theme.Info, -Kit.Px(28));
    }

    private static ReadOnlySpan<byte> FaceName(int side) => side switch
    {
        0 => "+X"u8,
        1 => "-X"u8,
        2 => "+Y"u8,
        3 => "-Y"u8,
        4 => "+Z"u8,
        _ => "-Z"u8,
    };

    /// <summary>
    /// The face of the ghost that sizing moves, filled, and an arrow out of the middle of its box side along the axis
    /// it moves on. A wedge's slant stands for the box side it replaces, so the arrow shows the axis that is sized.
    /// </summary>
    private static void DrawSizingFace(ImDrawListPtr draw, WorldView world, in DesignBox ghost, int side)
    {
        DrawFace(draw, world, ghost, side, Kit.Color(Theme.Info, 0.35f), Kit.Color(Theme.Info), Quaternion.Identity, Vector3.Zero);
        var axis = side >> 1;
        var normal = EditorGeometry.Normal(side);
        var half = new Vector3(ghost.Size.X, ghost.Size.Y, ghost.Size.Z) * 0.5f;
        var middle = new Vector3(ghost.At.X - 0.5f, ghost.At.Y - 0.5f, ghost.At.Z - 0.5f) + half;
        var from = middle + normal * half[axis];
        var to = from + normal * 1.5f;
        if (!world.ProjectSegment(world.EditorPoint(from), world.EditorPoint(to), out var a, out var b))
            return;
        var color = Kit.Color(Theme.Info);
        draw.AddLine(a, b, color, Kit.Px(2.5f));
        draw.AddCircleFilled(b, Kit.Px(4), color);
    }

    /// <summary>A short line beside the pointer, on a dark backing: red by default, for a refusal.</summary>
    private void PointerNote(ReadOnlySpan<byte> text, uint tone = Theme.Bad, float lift = 0f)
    {
        var draw = ImGui.GetForegroundDrawList();
        var at = _app.Devices.MousePosition + new float2(Kit.Px(16), Kit.Px(12) + lift);
        var size = Kit.Measure(_ui.SansMedium, Theme.FontCaption, text);
        draw.AddRectFilled(at - new float2(Kit.Px(4), Kit.Px(2)), at + size + new float2(Kit.Px(4), Kit.Px(2)), Kit.Color(Theme.Void, 0.8f));
        Kit.Text(draw, _ui.SansMedium, Theme.FontCaption, at, tone, text);
    }

    /// <summary>
    /// The edges of a block of the draft's grid, cut where they pass behind the camera: a box's twelve, a wedge's
    /// nine (seven box edges and the two slant edges across its caps).
    /// </summary>
    private static void DrawBlockEdges(ImDrawListPtr draw, WorldView world, in DesignBox box, ImColor8 color, float thickness) =>
        DrawBlockEdges(draw, world, box, color, thickness, Quaternion.Identity, Vector3.Zero);

    /// <summary>As the plain overload, the outline turned by <paramref name="turn"/> about its middle and moved by <paramref name="shift"/>.</summary>
    private static void DrawBlockEdges(ImDrawListPtr draw, WorldView world, in DesignBox box, ImColor8 color, float thickness, Quaternion turn, Vector3 shift)
    {
        Span<Double3> corners = stackalloc Double3[8];
        GridCorners(world, box, corners, turn, shift);
        for (var i = 0; i < 8; i++)
        {
            if (!BlockShapes.HasCorner(box.Shape, i))
                continue;
            for (var bit = 1; bit < 8; bit <<= 1)
            {
                if ((i & bit) != 0 || !BlockShapes.HasCorner(box.Shape, i | bit))
                    continue;
                if (world.ProjectSegment(corners[i], corners[i | bit], out var a, out var b))
                    draw.AddLine(a, b, color, thickness);
            }
        }

        if (box.Shape == BlockShape.Box)
            return;
        // The slant's two edges across the caps: its corners at each end of the extrusion.
        Span<int> slant = stackalloc int[4];
        BlockShapes.Face(box.Shape, BlockShapes.SlantSide(box.Shape), slant);
        for (var i = 0; i < 4; i += 2)
        {
            // The slant runs low end, low end, high end, high end or the reverse: pair the corners of one end.
            var a = slant[i == 0 ? 0 : 2];
            var b = slant[i == 0 ? 1 : 3];
            if (world.ProjectSegment(corners[a], corners[b], out var p, out var q))
                draw.AddLine(p, q, color, thickness);
        }
    }

    /// <summary>
    /// The eight corners of a block's box in the world, by corner number (1 +x, 2 +y, 4 +z), turned by
    /// <paramref name="turn"/> about the box's middle and moved by <paramref name="shift"/> in the grid.
    /// </summary>
    private static void GridCorners(WorldView world, in DesignBox box, Span<Double3> corners, Quaternion turn, Vector3 shift)
    {
        var half = new Vector3(box.Size.X, box.Size.Y, box.Size.Z) * 0.5f;
        var middle = new Vector3(box.At.X - 0.5f, box.At.Y - 0.5f, box.At.Z - 0.5f) + half + shift;
        for (var i = 0; i < 8; i++)
            corners[i] = world.EditorPoint(middle + Vector3.Transform(BlockShapes.Corner(i, half), turn));
    }

    /// <summary>
    /// One face of a block of the draft's grid, filled when all of it is in front of the camera: a wedge's own
    /// triangle, rectangle or slant. A side a wedge has no face on stands for its slant, which a ray through the slant
    /// enters by.
    /// </summary>
    private static void DrawFace(ImDrawListPtr draw, WorldView world, in DesignBox box, int side, ImColor8 fill, ImColor8 line) =>
        DrawFace(draw, world, box, side, fill, line, Quaternion.Identity, Vector3.Zero);

    /// <summary>As the plain overload, the face turned by <paramref name="turn"/> about the block's middle and moved by <paramref name="shift"/>.</summary>
    private static void DrawFace(ImDrawListPtr draw, WorldView world, in DesignBox box, int side, ImColor8 fill, ImColor8 line, Quaternion turn, Vector3 shift)
    {
        Span<int> ring = stackalloc int[4];
        var count = BlockShapes.Face(box.Shape, side, ring);
        if (count == 0)
            count = BlockShapes.Face(box.Shape, BlockShapes.SlantSide(box.Shape), ring);
        Span<Double3> all = stackalloc Double3[8];
        GridCorners(world, box, all, turn, shift);
        Span<float2> points = stackalloc float2[4];
        var whole = true;
        for (var i = 0; i < count; i++)
            whole &= world.Project(all[ring[i]], out points[i]);
        if (whole)
            draw.AddConvexPolyFilled(points[..count], fill);
        for (var i = 0; i < count; i++)
        {
            if (world.ProjectSegment(all[ring[i]], all[ring[(i + 1) % count]], out var a, out var b))
                draw.AddLine(a, b, line, Kit.Px(1.5f));
        }
    }

    /// <summary>Opens one of the editor's panels at a rectangle and starts a padded region inside it.</summary>
    private static void BeginEditorPanel(ImString window, ImString body, in HudRect rect, bool frame = true)
    {
        var min = new float2(rect.Left, rect.Top);
        var size = new float2(rect.Width, rect.Height);
        ImGui.SetNextWindowPos(min);
        ImGui.SetNextWindowSize(size);
        ImGui.Begin(window, EditorPanelFlags);
        if (frame)
            Kit.PanelFrame(ImGui.GetWindowDrawList(), min, min + size);
        var pad = Kit.Px(EditorLayout.Pad);
        ImGui.SetCursorScreenPos(min + new float2(pad, pad));
        ImGui.BeginChild(body, size - new float2(pad * 2, pad * 2), ImGuiChildFlags.None, ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoScrollbar);
    }

    private static void EndEditorPanel()
    {
        ImGui.EndChild();
        ImGui.End();
    }

    /// <summary>
    /// The part list: a search at the top, tabs by what parts are for (compact drone parts on their own tab), and
    /// a card per part with its figures at the size parts are placed at. A card is clicked to place that part with
    /// clicks, or dragged onto the ship. The size controls stay at the foot.
    /// </summary>
    private void DrawEditorParts(in HudRect rect, ShipEditor editor, GameContent content)
    {
        var tuning = Tuning;
        BeginEditorPanel("##editorparts"u8, "##editorpartsbody"u8, rect);
        var width = ImGui.GetContentRegionAvail().X;

        // The search: typing here never reaches the world, which takes no keys while a field has the keyboard.
        ImGui.SetNextItemWidth(width);
        ImGui.PushFont(_ui.Sans, Theme.FontSmall);
        if (ImGui.InputTextWithHint("##partsearch"u8, "Search parts"u8, _partSearch.Buffer, ImGuiInputTextFlags.NoUndoRedo))
        {
            _partSearch.EvaluateLength();
            // Only when the text changes; a frame with nothing typed allocates nothing.
            _palette.Query = _partSearch.ToString().Trim();
        }

        ImGui.PopFont();
        if (ImGui.IsItemActive())
            ImGui.GetWindowDrawList().AddRect(ImGui.GetItemRectMin(), ImGui.GetItemRectMax(), Kit.Color(Theme.Accent));
        Kit.Space(Theme.Space2);

        // The tabs, four to a row or fewer in a narrow panel; a search covers every tab, so none is lit while there is one.
        var categories = tuning.Categories;
        var gap = Kit.Px(Theme.Space1);
        var widest = 0f;
        foreach (var category in categories)
            widest = Math.Max(widest, Kit.Measure(_ui.SansMedium, Theme.FontSmall, category.Label).X);
        var perRow = EditorLayout.TabsPerRow(width, widest, gap, Kit.Scale);
        var chip = EditorLayout.TabWidth(width, perRow, gap);
        for (var i = 0; i < categories.Length; i++)
        {
            if (i % perRow != 0)
                ImGui.SameLine(0, gap);
            else if (i > 0)
                Kit.Space(Theme.Space1);
            ImGui.PushID(i);
            if (EditorPalette.HandBack(EditorButton("##tab"u8, categories[i].Label, new float2(chip, Kit.Px(24)), true, false, _palette.Query.Length == 0 && _palette.Category == i)))
            {
                _palette.Category = i;
                _partSearch.Clear();
                _palette.Query = "";
            }

            ImGui.PopID();
        }

        Kit.Space(Theme.Space2);
        if (EditorPalette.HandBack(EditorButton("##selecttool"u8, "Select blocks"u8, new float2(width, Kit.Px(26)), editor.CanEdit, false, !editor.Placing)))
            editor.StopPlacing();
        // How a part is sized and turned on the ship: two short lines, wrapped in a narrow panel.
        Kit.Space(Theme.Space1);
        Paragraph(Theme.TextMute, SizeHint, width);
        Paragraph(Theme.TextMute, TurnHint, width);
        Kit.Space(Theme.Space2);

        // The cards scroll between the tabs and the size controls, while there is room for a couple; in a short
        // window the whole panel scrolls instead.
        // The caption, the size and shape lines and the shape buttons below the cards.
        var footer = 3 * Kit.Px(16) + Kit.Px(28) + 2 * Kit.Px(Theme.Space2) + 2 * Kit.Px(Theme.Space1);
        var listHeight = ImGui.GetContentRegionAvail().Y - footer;
        var cardHeight = Kit.Px(70);
        var split = listHeight >= cardHeight;
        if (split)
            ImGui.BeginChild("##editorpartlist"u8, new float2(width, listHeight), ImGuiChildFlags.None, ImGuiWindowFlags.NoBackground);
        var types = content.Blocks.Types;
        var figures = _palette.Figures(content, editor.PartSize, editor.PartShape);
        var hull = editor.Source?.Color ?? Vector3.One;
        var shown = 0;
        for (var i = 0; i < types.Count; i++)
        {
            var type = types[i];
            if (!_palette.Shows(tuning, type))
                continue;
            shown++;
            DrawPartCard(i, type, figures[i], tuning.Compact(type), cardHeight, hull, editor);
            Kit.Space(Theme.Space1);
        }

        if (shown == 0)
            Paragraph(Theme.TextMute, _palette.Query.Length > 0 ? "No part matches the search."u8 : "No part on this tab."u8, ImGui.GetContentRegionAvail().X);
        if (split)
            ImGui.EndChild();
        // The shape parts are placed in; their size and turn are set on the ship, with the keys the hints name.
        Kit.Space(Theme.Space2);
        Kit.Caption("PART SHAPE"u8);
        Kit.Space(Theme.Space1);
        var size = editor.PartSize;
        ClippedLine(_ui.Mono, Theme.FontCaption, Theme.TextDim, Kit.F($"{size.X} x {size.Y} x {size.Z} CELLS"), width);
        ClippedLine(_ui.Mono, Theme.FontCaption, Theme.TextDim, ShapeText(editor.PartShape), width);
        Kit.Space(Theme.Space1);
        var pair = MathF.Floor((width - gap) * 0.5f);
        if (EditorPalette.HandBack(EditorButton("##shapebox"u8, "Box"u8, new float2(pair, Kit.Px(26)), editor.CanEdit, false, editor.PartShape == BlockShape.Box)))
            editor.SetPartShape(BlockShape.Box);
        ImGui.SameLine(0, gap);
        if (EditorPalette.HandBack(EditorButton("##shapewedge"u8, "Wedge"u8, new float2(pair, Kit.Px(26)), editor.CanEdit, false, editor.PartShape != BlockShape.Box)))
            editor.SetPartShape(editor.PartShape == BlockShape.Box ? BlockShape.WedgeXPlusPlus : editor.PartShape);
        EndEditorPanel();
    }

    /// <summary>The hint over the part list for sizing the part on the ship before it is placed.</summary>
    internal static ReadOnlySpan<byte> SizeHint => "S resize: hold, drag a ghost face"u8;

    /// <summary>
    /// The hint for turning it. A turn needs no confirming; the note beside the pointer while the rings are up says
    /// how to place and how to put it back.
    /// </summary>
    internal static ReadOnlySpan<byte> TurnHint => "R rotate: drag ring"u8;

    /// <summary>One part's card: its name, what it is for, and its figures at the placing size.</summary>
    private void DrawPartCard(int part, BlockType type, in PartFigures figures, bool compact, float height, Vector3 hull, ShipEditor editor)
    {
        ImGui.PushID(part);
        var draw = ImGui.GetWindowDrawList();
        var at = ImGui.GetCursorScreenPos();
        var cardWidth = ImGui.GetContentRegionAvail().X;
        if (!editor.CanEdit)
            ImGui.BeginDisabled();
        var hot = EditorPalette.Card(part, new float2(cardWidth, height), editor) && editor.CanEdit;
        if (!editor.CanEdit)
            ImGui.EndDisabled();
        var chosen = editor.Placing && editor.Part == part;
        var carried = editor.Carry == EditorCarry.Part && editor.Part == part;
        var max = at + new float2(cardWidth, height);
        Kit.ChamferFill(draw, at, max, Kit.Color(chosen || hot ? Theme.PanelRaised : Theme.Panel, chosen ? 0.95f : 0.8f), Kit.Px(5));
        Kit.ChamferOutline(draw, at, max, Kit.Color(carried || chosen ? Theme.Accent : hot ? Theme.LineStrong : Theme.Line), Kit.Px(5));
        var role = EditorGeometry.Role(type);
        var swatch = at + new float2(Kit.Px(8), Kit.Px(9));
        draw.AddRectFilled(swatch, swatch + new float2(Kit.Px(10), Kit.Px(10)), Kit.Color(Packed(EditorGeometry.BlockColor(hull, type))));
        var left = Kit.Px(26);
        var room = cardWidth - left - Kit.Px(6);
        var tag = 0f;
        if (compact)
        {
            // Compact parts carry their own tag, on the line under the name, so a drone part is never mistaken for
            // its full-size namesake and the name keeps the card's width.
            var size = Kit.Measure(_ui.SansMedium, Theme.FontCaption, "DRONE"u8);
            tag = size.X + Kit.Px(8);
            var tagAt = new float2(max.X - Kit.Px(6) - size.X, at.Y + Kit.Px(22));
            draw.AddRectFilled(tagAt - new float2(Kit.Px(3), Kit.Px(1)), tagAt + size + new float2(Kit.Px(3), Kit.Px(1)), Kit.Color(Theme.Info, 0.18f));
            Kit.Text(draw, _ui.SansMedium, Theme.FontCaption, tagAt, Theme.Info, "DRONE"u8);
        }

        Kit.ClippedText(draw, _ui.SansMedium, Theme.FontSmall, at + new float2(left, Kit.Px(4)), chosen ? Theme.Text : Theme.TextDim, Kit.F($"{type.Name}"), room);
        Kit.ClippedText(draw, _ui.Sans, Theme.FontCaption, at + new float2(left, Kit.Px(22)), Theme.TextMute, RoleText(role), room - tag);
        Kit.ClippedText(draw, _ui.Mono, Theme.FontCaption, at + new float2(left, Kit.Px(38)), Theme.TextDim, Kit.F($"{figures.Mass:0.##} T   {figures.Hull:0} HP"), room);
        var main = MainFigures(role, figures);
        if (!main.IsEmpty)
            Kit.ClippedText(draw, _ui.Mono, Theme.FontCaption, at + new float2(left, Kit.Px(53)), Theme.Text, main, room);
        ImGui.PopID();
    }

    /// <summary>The figures a part of a role is chosen for, on one line: what it gives, then what it draws.</summary>
    private static ReadOnlySpan<byte> MainFigures(PartRole role, in PartFigures f) => role switch
    {
        PartRole.Engine or PartRole.Thruster => Kit.F($"{f.Thrust:0.#} KN   DRAWS {f.PowerDraw:0.##} MW"),
        PartRole.Gyro => Kit.F($"{f.Torque:0.#} KNM   DRAWS {f.PowerDraw:0.##} MW"),
        PartRole.Power => Kit.F($"MAKES {f.PowerOutput:0.##} MW"),
        PartRole.Storage => Kit.F($"STORES {f.Energy:0.#} MJ"),
        PartRole.Shield => Kit.F($"{f.Shield:0} SHIELD   {f.Recharge:0.#}/S   DRAWS {f.PowerDraw:0.##} MW"),
        PartRole.Cargo => Kit.F($"{f.Cargo:0.#} M3 CARGO"),
        PartRole.Crew => Kit.F($"{f.Crew:0} CREW"),
        PartRole.Drive => Kit.F($"CHARGES AT {f.FtlPower:0.#} MW"),
        PartRole.Core => Kit.F($"DRAWS {f.PowerDraw:0.##} MW"),
        _ => default,
    };

    /// <summary>
    /// The selection with what can be done to it, the ship against its blueprint with Repair, the checked figures
    /// with their warnings, the shield shape's complexity, and whether the draft can be applied.
    /// </summary>
    private void DrawEditorDetails(in HudRect rect, ShipEditor editor, BuildDraft draft, GameContent content, PlayerInputBridge bridge)
    {
        BeginEditorPanel("##editordetails"u8, "##editordetailsbody"u8, rect);
        var width = ImGui.GetContentRegionAvail().X;
        ImGui.BeginChild("##editordetailsscroll"u8, new float2(width, ImGui.GetContentRegionAvail().Y), ImGuiChildFlags.None, ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoScrollbar);
        width = ImGui.GetContentRegionAvail().X;
        DrawEditorSelection(editor, draft, content, width);

        if (editor.Time - editor.LastEditAt < EditMessageSeconds && editor.LastEdit is not (DraftEdit.Done or DraftEdit.Unchanged))
        {
            Kit.Space(Theme.Space1);
            Paragraph(Theme.Bad, EditText(editor.LastEdit), width);
        }

        Kit.Space(Theme.Space4);
        Kit.Caption("SHIP"u8, Theme.Accent);
        Kit.Space(Theme.Space1);
        var boxes = draft.Boxes;
        var limits = content.Blocks.Limits;
        var blockLimit = (int)Math.Min(Math.Min(limits.MaxBlocks, limits.SurfaceVertices / 36), Math.Floor(Math.Sqrt(limits.GeometryPairTests / 6.0)));
        StatRow("BLOCKS"u8, Kit.F($"{boxes.Length} / {blockLimit}"), Theme.Text, width);
        if (editor.Source is { } source)
        {
            var built = source.Flight.HullPoints;
            var now = FlightConsole.Shown(editor.OwnHullPoints, built);
            StatRow("HULL NOW / BUILT"u8, Kit.F($"{now:0} / {built:0}"), now < built ? Theme.Bad : Theme.Text, width);
        }

        if (editor.MissingCount > 0)
            Paragraph(Theme.Bad, Kit.F($"{editor.MissingCount} lost blocks, outlined in red. They are in this draft as built."), width);
        DrawRepair(editor, bridge, width);
        DrawEditorFigures(editor, draft, width);
        DrawSurface(editor, draft, width);

        Kit.Space(Theme.Space4);
        Kit.Caption("CHECK"u8, Theme.Accent);
        Kit.Space(Theme.Space1);
        if (editor.DraftRestarted && !editor.Dirty)
            Paragraph(Theme.Info, "The ship changed since your last draft, so this draft starts again from its blueprint."u8, width);
        switch (editor.Check)
        {
            case DraftCheck.Pending:
                Paragraph(Theme.TextDim, "Checking the draft..."u8, width);
                break;
            case DraftCheck.Ready when !editor.Dirty:
                Paragraph(Theme.TextDim, "No changes yet."u8, width);
                break;
            case DraftCheck.Ready:
                Paragraph(Theme.Good, "Passes every check the server makes. Ready to apply; warnings above do not stop it."u8, width);
                break;
            default:
                Paragraph(Theme.Bad, Kit.F($"{editor.CheckProblem}"), width);
                break;
        }

        ImGui.EndChild();
        EndEditorPanel();
    }

    /// <summary>The selection: one block with its resize controls, or a group with Merge; Copy, Paste and Delete.</summary>
    private void DrawEditorSelection(ShipEditor editor, BuildDraft draft, GameContent content, float width)
    {
        var boxes = draft.Boxes;
        var count = editor.Selection.Length;
        Kit.Caption(count > 1 ? Kit.F($"SELECTED: {count} BLOCKS") : "SELECTED"u8, SelectionBlue);
        Kit.Space(Theme.Space1);
        if ((uint)editor.Selected >= (uint)boxes.Length)
        {
            var part = PartName(editor, content);
            Paragraph(Theme.TextMute, editor.RepairOnly
                ? "Repair the ship before you edit it. Click or middle click a block to select it; Shift adds."u8
                : editor.Placing
                ? Kit.F($"Click a face to place {part}, or drag a card onto the ship. Middle click a block to select it; Shift adds.")
                : "Click or middle click a block to select it; Shift adds or removes one."u8, width);
            if (editor.Clipboard.Length > 0)
            {
                Kit.Space(Theme.Space1);
                if (EditorButton("##paste"u8, Kit.F($"Paste {editor.Clipboard.Length}"), new float2(width, Kit.Px(26)), editor.CanEdit && editor.Carry == EditorCarry.None))
                    editor.BeginPaste();
            }

            return;
        }

        ref readonly var box = ref boxes[editor.Selected];
        ClippedLine(_ui.SansMedium, Theme.FontSmall, Theme.Text, Kit.F($"{box.Type.Name}"), width);
        ClippedLine(_ui.Mono, Theme.FontCaption, Theme.TextDim, Kit.F($"{box.Size.X} x {box.Size.Y} x {box.Size.Z} CELLS AT {box.At.X} {box.At.Y} {box.At.Z}"), width);
        ClippedLine(_ui.Mono, Theme.FontCaption, Theme.TextDim, ShapeText(box.Shape), width);
        if (editor.IsMissing(editor.Selected))
            Paragraph(Theme.Bad, "The ship has lost this block. Repair rebuilds it."u8, width);
        Kit.Space(Theme.Space2);
        if (count == 1)
        {
            Kit.Caption("RESIZE FROM FACE"u8);
            Kit.Space(Theme.Space1);
            // Two rows of three, plus sides over minus sides, so the labels fit a narrow panel too.
            var gap = Kit.Px(Theme.Space1);
            var chip = MathF.Floor((width - gap * 2) / 3f);
            for (var i = 0; i < 6; i++)
            {
                var side = (i % 3) * 2 + i / 3;
                if (i % 3 != 0)
                    ImGui.SameLine(0, gap);
                else if (i == 3)
                    Kit.Space(Theme.Space1);
                ImGui.PushID(side);
                if (EditorButton("##face"u8, SideName(side), new float2(chip, Kit.Px(26)), editor.CanEdit, false, editor.Face == side))
                    editor.SetFace(side);
                ImGui.PopID();
            }

            Kit.Space(Theme.Space1);
            var length = editor.SelectedLength;
            var step = Stepper(3, Kit.F($"LENGTH ALONG {AxisName(editor.Face >> 1)}"), length,
                editor.CanResizeSelected(length - 1) == DraftEdit.Done, editor.CanResizeSelected(length + 1) == DraftEdit.Done, width);
            if (step != 0)
                editor.ResizeSelected(length + step);
            Kit.Space(Theme.Space1);

            // A quarter turn about each axis, keeping the low corner; refused where the turned block would not fit.
            for (var axis = 0; axis < 3; axis++)
            {
                if (axis > 0)
                    ImGui.SameLine(0, gap);
                ImGui.PushID(axis);
                if (EditorButton("##turnblock"u8, Kit.F($"Turn {AxisName(axis)}"), new float2(chip, Kit.Px(26)), editor.CanRotateSelected(axis) == DraftEdit.Done))
                    editor.RotateSelected(axis);
                ImGui.PopID();
            }

            Kit.Space(Theme.Space2);
        }
        else
        {
            if (EditorButton("##merge"u8, Kit.F($"Merge {count} blocks"), new float2(width, Kit.Px(28)), editor.CanEdit && editor.MergeCheck == DraftEdit.Done))
                editor.MergeSelected();
            Kit.Space(Theme.Space1);
            if (editor.MergeCheck == DraftEdit.Done)
            {
                var merged = editor.Merged;
                var role = EditorGeometry.Role(merged.Type);
                Paragraph(Theme.TextDim, Kit.F($"Makes one {merged.Size.X} x {merged.Size.Y} x {merged.Size.Z} {merged.Type.Name}. Larger blocks follow their own size rules, so figures can change:"), width);
                StatRow(MainLabel(role), Kit.F($"{editor.MergeBefore.Main(role):0.##} -> {editor.MergeAfter.Main(role):0.##}"), Theme.Text, width);
                StatRow("HULL"u8, Kit.F($"{editor.MergeBefore.Hull:0} -> {editor.MergeAfter.Hull:0}"), Theme.Text, width);
                if (editor.MergeAfter.PowerDraw != editor.MergeBefore.PowerDraw)
                    StatRow("DRAWS"u8, Kit.F($"{editor.MergeBefore.PowerDraw:0.##} -> {editor.MergeAfter.PowerDraw:0.##} MW"), Theme.Text, width);
            }
            else
            {
                Paragraph(Theme.TextMute, MergeText(editor.MergeCheck), width);
            }

            Kit.Space(Theme.Space2);
        }

        // Copy and Paste, then Delete, as Ctrl+C, Ctrl+V and Delete do.
        var half = MathF.Floor((width - Kit.Px(Theme.Space1)) * 0.5f);
        if (EditorButton("##copy"u8, "Copy"u8, new float2(half, Kit.Px(26)), editor.CanEdit))
            editor.CopySelection();
        ImGui.SameLine(0, Kit.Px(Theme.Space1));
        if (EditorButton("##paste"u8, editor.Clipboard.Length > 0 ? Kit.F($"Paste {editor.Clipboard.Length}") : "Paste"u8, new float2(half, Kit.Px(26)),
                editor.CanEdit && editor.Clipboard.Length > 0 && editor.Carry == EditorCarry.None))
            editor.BeginPaste();
        Kit.Space(Theme.Space1);
        if (EditorButton("##delete"u8, count > 1 ? Kit.F($"Delete {count} blocks") : "Delete block"u8, new float2(width, Kit.Px(28)), editor.CanDelete))
            editor.DeleteSelected();
    }

    /// <summary>Repair, for a damaged ship: rebuilds it as built, and says why it cannot when it cannot.</summary>
    private void DrawRepair(ShipEditor editor, PlayerInputBridge bridge, float width)
    {
        if (!editor.Damaged)
            return;
        var state = editor.RepairState;
        Kit.Space(Theme.Space2);
        Paragraph(Theme.Bad, "Repair first: a damaged ship cannot be edited. You can still look around and select blocks."u8, width);
        Kit.Space(Theme.Space1);
        if (EditorButton("##repair"u8, "Repair ship"u8, new float2(width, Kit.Px(28)), state == RepairBlock.None, state == RepairBlock.None))
            editor.Repair(bridge);
        Kit.Space(Theme.Space1);
        if (state == RepairBlock.Unsaved)
        {
            // Changes kept from before the damage can no longer be applied; only the player throws them away.
            Paragraph(Theme.Bad, "This draft has changes from before the damage. Repair rebuilds the ship as built, so discard them first."u8, width);
            Kit.Space(Theme.Space1);
            if (EditorButton("##discard"u8, "Discard changes"u8, new float2(width, Kit.Px(26)), true))
                editor.DiscardChanges();
            return;
        }

        Paragraph(state == RepairBlock.None ? Theme.TextDim : Theme.Bad, state switch
        {
            RepairBlock.None => "Rebuilds every lost or worn block as the ship was built. Shield and battery charge stay as they are."u8,
            _ => "Waiting for the server."u8,
        }, width);
    }

    /// <summary>
    /// The figures of the checked draft against the ship's, a row in red where the design flies or powers poorly,
    /// and why. Only the current draft's figures are shown as its own; older ones are labelled as such.
    /// </summary>
    private void DrawEditorFigures(ShipEditor editor, BuildDraft draft, float width)
    {
        Kit.Space(Theme.Space4);
        Kit.Caption("FIGURES"u8, Theme.Accent);
        Kit.Space(Theme.Space1);
        var before = editor.Source?.Flight;
        if (editor.Checked.Design is not { } checkedDesign || before is not { } was)
        {
            Paragraph(Theme.TextMute, "Figures appear once the draft has been checked."u8, width);
            return;
        }

        var current = editor.CheckedVersion == draft.Version && editor.Check == DraftCheck.Ready;
        if (!current)
            Paragraph(Theme.TextMute, editor.Check == DraftCheck.Pending
                ? "Checking the draft. These are the figures of the last version that passed, not this one."u8
                : "This draft does not pass. These are the figures of the last version that did."u8, width);
        var now = checkedDesign.Flight;
        Span<DesignWarning> warnings = stackalloc DesignWarning[DesignWarnings.Count];
        var count = current ? DesignWarnings.Of(now, Tuning, warnings) : 0;
        warnings = warnings[..count];
        uint Tone(bool warned) => !current ? Theme.TextMute : warned ? Theme.Bad : Theme.Text;
        var power = Has(warnings, DesignWarning.NoPower) || Has(warnings, DesignWarning.IdleOverload) || Has(warnings, DesignWarning.ThrustOverload) || Has(warnings, DesignWarning.ShieldOverload);
        StatRow("MASS"u8, Kit.F($"{now.Mass:0.#} T (WAS {was.Mass:0.#})"), Tone(false), width);
        StatRow("TOP SPEED"u8, Kit.F($"{now.MaxSpeed:0} M/S (WAS {was.MaxSpeed:0})"), Tone(Has(warnings, DesignWarning.SlowTop)), width);
        StatRow("FORWARD"u8, Kit.F($"{now.Acceleration.Z:0.0} M/S2 (WAS {was.Acceleration.Z:0.0})"), Tone(Has(warnings, DesignWarning.NoForward) || Has(warnings, DesignWarning.SlowForward)), width);
        StatRow("TURN"u8, Kit.F($"{SlowestTurn(now):0} DEG/S (WAS {SlowestTurn(was):0})"), Tone(Has(warnings, DesignWarning.NoTurn) || Has(warnings, DesignWarning.SlowTurn)), width);
        StatRow("POWER"u8, Kit.F($"{now.PowerGenerated:0.#} MW, IDLE {now.IdlePower:0.#}"), Tone(power), width);
        StatRow("BATTERY"u8, Kit.F($"{now.EnergyStorage:0} MJ (WAS {was.EnergyStorage:0})"), Tone(Has(warnings, DesignWarning.NoBattery)), width);
        StatRow("HULL"u8, Kit.F($"{now.HullPoints:0} (WAS {was.HullPoints:0})"), Tone(false), width);
        StatRow("SHIELD"u8, Kit.F($"{now.ShieldCapacity:0} (WAS {was.ShieldCapacity:0})"), Tone(Has(warnings, DesignWarning.NoBattery) || Has(warnings, DesignWarning.ShieldOverload)), width);
        foreach (var warning in warnings)
        {
            Kit.Space(Theme.Space1);
            Paragraph(Theme.Bad, WarningText(warning, now), width);
        }

        if (count > 0)
            Paragraph(Theme.TextMute, "Warnings do not stop Apply."u8, width);
    }

    private static bool Has(ReadOnlySpan<DesignWarning> warnings, DesignWarning warning) => warnings.Contains(warning);

    private static float SlowestTurn(in FlightParams flight) => MathF.Min(flight.TurnRate.X, MathF.Min(flight.TurnRate.Y, flight.TurnRate.Z)) * 57.29578f;

    /// <summary>What a warning means for the ship, with its figures.</summary>
    private ReadOnlySpan<byte> WarningText(DesignWarning warning, in FlightParams f) => warning switch
    {
        DesignWarning.NoPower => "No generator: nothing makes power, so the battery only runs down."u8,
        DesignWarning.IdleOverload => Kit.F($"Generators make {f.PowerGenerated:0.##} MW, less than the {f.IdlePower:0.##} MW always drawn: the battery runs down even at rest."),
        DesignWarning.ThrustOverload => Kit.F($"Full normal thrust needs {f.ThrustPower:0.##} MW; generators supply {Math.Max(0f, f.PowerGenerated - f.IdlePower):0.##} MW after idle. The battery covers the shortfall. Empty battery: {100f * Math.Clamp((f.PowerGenerated - f.IdlePower) / Math.Max(f.ThrustPower, float.Epsilon), 0f, 1f):0}% normal thrust at full throttle. Boost draws {f.BoostPowerMultiplier:0.#} times as much."),
        DesignWarning.ShieldOverload => Kit.F($"A recharging shield draws {f.ShieldPower:0.##} MW ({f.ShieldRestartPower:0.##} MW to restart), more than the {Math.Max(0f, f.PowerGenerated - f.IdlePower):0.##} MW left after idle: it recharges off the battery."),
        DesignWarning.NoBattery => "No battery: a shield only recharges from stored energy, so this shield never recharges once hit. Boost needs a battery too."u8,
        DesignWarning.NoForward => "No engine or thruster pushes forward: the ship cannot move ahead."u8,
        DesignWarning.SlowForward => Kit.F($"Forward acceleration is only {f.Acceleration.Z:0.0} m/s2."),
        DesignWarning.SlowTop => Kit.F($"Top speed is only {f.MaxSpeed:0} m/s."),
        DesignWarning.NoTurn => "No gyro or thruster turns the ship on every axis: it cannot turn freely."u8,
        _ => Kit.F($"The ship turns at only {SlowestTurn(f):0.#} deg/s at its slowest."),
    };

    /// <summary>
    /// How complex the shield's shape is against what the server allows: measured on the current draft only, also
    /// when it is refused for it, with the block that adds the most, which the player can be shown.
    /// </summary>
    private void DrawSurface(ShipEditor editor, BuildDraft draft, float width)
    {
        Kit.Space(Theme.Space4);
        Kit.Caption("SHIELD SHAPE"u8, Theme.Accent);
        Kit.Space(Theme.Space1);
        if (editor.CheckedVersion != draft.Version || editor.Check == DraftCheck.Pending)
        {
            Paragraph(Theme.TextMute, "Measured once the draft has been checked."u8, width);
            return;
        }

        if (editor.Latest.Surface is not { } surface)
        {
            Paragraph(Theme.TextMute, "Not measured: the draft has too many blocks to measure."u8, width);
            return;
        }

        // Both of the shield's rooms, points and triangle corners; the bar shows the fuller one.
        var points = surface.Limit > 0 ? surface.Vertices / (float)surface.Limit : 1f;
        var corners = surface.IndexLimit > 0 ? surface.Indices / (float)surface.IndexLimit : 1f;
        var share = Math.Clamp(Math.Max(points, corners), 0f, 1f);
        var tone = !surface.Fits ? Theme.Bad : share > 0.8f ? Theme.Accent : Theme.Good;
        StatRow("POINTS"u8, surface.Vertices > surface.Limit ? Kit.F($"OVER {surface.Limit}") : Kit.F($"{surface.Vertices} / {surface.Limit}"), points > 1f ? Theme.Bad : tone, width);
        StatRow("CORNERS"u8, surface.Indices > surface.IndexLimit ? Kit.F($"OVER {surface.IndexLimit}") : Kit.F($"{surface.Indices} / {surface.IndexLimit}"), corners > 1f ? Theme.Bad : tone, width);
        var draw = ImGui.GetWindowDrawList();
        var at = ImGui.GetCursorScreenPos();
        draw.AddRectFilled(at, at + new float2(width, Kit.Px(4)), Kit.Color(Theme.Void));
        draw.AddRectFilled(at, at + new float2(width * share, Kit.Px(4)), Kit.Color(tone));
        ImGui.Dummy(new float2(width, Kit.Px(8)));
        Paragraph(surface.Fits ? Theme.TextMute : Theme.Bad, surface.Fits
            ? "How detailed the shield around the ship is. Long flat faces and straight edges cost little; separate parts, slanted steps and many corners cost most. Gaps of about a block are bridged."u8
            : surface.Overflow is "vertices" or "indices" or null
                ? "The shield around this shape would be too detailed, so the ship cannot be built. Join separate parts, or replace stepped slopes with wedges."u8
                : "The shield around this shape would take too long to work out, so the ship cannot be built. Join separate parts or simplify the outline."u8, width);
        if (surface.WorstBlock < 0 || surface.WorstBlock >= draft.Boxes.Length)
            return;
        var worst = draft.Boxes[surface.WorstBlock];
        Paragraph(Theme.TextDim, Kit.F($"Most detailed: a {worst.Type.Name} adding {surface.WorstVertices}."), width);
        Kit.Space(Theme.Space1);
        if (EditorButton("##locate"u8, "Show that block"u8, new float2(width, Kit.Px(26)), editor.CanEdit))
            editor.Locate(surface.WorstBlock);
    }

    /// <summary>The status of the edit, the editor's keys, and Undo, Redo, Cancel and Apply.</summary>
    private void DrawEditorBar(in HudRect rect, ShipEditor editor, BuildDraft draft, PlayerInputBridge bridge)
    {
        BeginEditorPanel("##editorbar"u8, "##editorbarbody"u8, rect);
        var draw = ImGui.GetWindowDrawList();
        var origin = ImGui.GetCursorScreenPos();
        var inner = ImGui.GetContentRegionAvail();
        var buttons = EditorLayout.ButtonsWidth(Kit.Scale) - Kit.Px(EditorLayout.Pad);
        var statusWidth = Math.Max(0f, inner.X - buttons - Kit.Px(Theme.Space3));

        // What the edit is doing, then the keys.
        var status = EditorStatus(editor, out var tone, out var busy);
        var left = 0f;
        if (busy)
        {
            Kit.Spinner(draw, origin + new float2(Kit.Px(7), Kit.Px(10)), Kit.Px(6));
            left = Kit.Px(20);
        }

        Kit.ClippedText(draw, _ui.SansMedium, Theme.FontSmall, origin + new float2(left, Kit.Px(2)), tone, status, statusWidth - left);
        if (editor.Stage == EditorStage.Applying && editor.Server is { Phase: EditorPhase.Receiving, TotalBytes: > 0 } sending)
        {
            var bar = origin + new float2(left, Kit.Px(20));
            var length = Math.Max(0f, statusWidth - left);
            draw.AddRectFilled(bar, bar + new float2(length, Kit.Px(2)), Kit.Color(Theme.LineStrong));
            draw.AddRectFilled(bar, bar + new float2(length * Math.Clamp(sending.SentBytes / (float)sending.TotalBytes, 0f, 1f), Kit.Px(2)), Kit.Color(Theme.Accent));
        }

        if (_editorHints is null)
        {
            var follow = _app.Actions.Bindings(InputAction.Follow);
            var focus = follow.IsEmpty ? "FOLLOW KEY" : follow[0].DisplayName.ToUpperInvariant();
            _editorHints = System.Text.Encoding.UTF8.GetBytes(
                $"LEFT CLICK PLACE   HOLD A/D W/S (+SHIFT) + MOUSE SIZE PART   R TURN PART   MIDDLE CLICK SELECT (SHIFT ADDS)   DRAG SELECTED TO MOVE   {focus} FOCUS   RIGHT DRAG ORBIT   MIDDLE DRAG PAN   WHEEL ZOOM   CTRL+C / CTRL+V COPY / PASTE   CTRL+Z UNDO   CTRL+Y REDO   DELETE REMOVE   {EditorKey} OR ESC CANCEL");
        }

        Kit.ClippedText(draw, _ui.Sans, Theme.FontCaption, origin + new float2(0, Kit.Px(24)), Theme.TextMute, (ReadOnlySpan<byte>)_editorHints, statusWidth);

        var height = Kit.Px(EditorLayout.ButtonHeight);
        var x = origin.X + inner.X - buttons;
        var y = origin.Y + MathF.Round((inner.Y - height) * 0.5f);
        var widths = EditorLayout.ButtonWidths;
        var gap = Kit.Px(EditorLayout.ButtonGap);
        ImGui.SetCursorScreenPos(new float2(x, y));
        if (EditorButton("##undo"u8, "Undo"u8, new float2(Kit.Px(widths[0]), height), editor.CanEdit && draft.CanUndo))
            editor.Undo();
        ImGui.SameLine(0, gap);
        if (EditorButton("##redo"u8, "Redo"u8, new float2(Kit.Px(widths[1]), height), editor.CanEdit && draft.CanRedo))
            editor.Redo();
        ImGui.SameLine(0, gap);
        if (EditorButton("##cancel"u8, "Cancel"u8, new float2(Kit.Px(widths[2]), height), true))
            editor.Cancel(bridge);
        ImGui.SameLine(0, gap);
        if (EditorButton("##apply"u8, editor.Stage == EditorStage.Applying ? "Applying"u8 : "Apply"u8, new float2(Kit.Px(widths[3]), height), editor.CanApply, true))
            editor.Apply(bridge);
        EndEditorPanel();
    }

    /// <summary>One line on where the edit stands, its colour, and whether it is waiting on something.</summary>
    private ReadOnlySpan<byte> EditorStatus(ShipEditor editor, out uint tone, out bool busy)
    {
        tone = Theme.Info;
        busy = true;
        if (editor.Stage == EditorStage.Applying)
        {
            var server = editor.Server;
            return server.Phase switch
            {
                EditorPhase.Receiving when server.TotalBytes > 0 =>
                    Kit.F($"Sending the design: {server.SentBytes / 1024f:0.0} of {server.TotalBytes / 1024f:0.0} KB"),
                EditorPhase.Checking => "The server is checking the design..."u8,
                EditorPhase.Publishing => "Preparing the new ship for every player..."u8,
                _ => "Waiting for the server..."u8,
            };
        }

        busy = false;
        switch (editor.Carry)
        {
            case EditorCarry.Part:
                return "Let go over a face to place the part; over a panel, or with Esc, nothing changes."u8;
            case EditorCarry.Move:
                return "Let go over a face to move the selection there; Esc or a right click leaves it where it was."u8;
            case EditorCarry.Paste:
                return "Click a face to paste the copied blocks; Esc or a right click stops pasting."u8;
        }

        tone = Theme.Bad;
        if (editor.RepairOnly)
            return "Repair first: the ship is damaged. Repair it, then edit it."u8;
        if (editor.Refusal != EditorReason.None && editor.Time - editor.RefusalAt < RefusalSeconds)
        {
            var reason = ReasonText(editor.Refusal, editor.RefusalWait - (int)(editor.Time - editor.RefusalAt));
            return Kit.F($"Not applied: {reason}");
        }

        switch (editor.Check)
        {
            case DraftCheck.Pending:
                tone = Theme.TextDim;
                busy = true;
                return "Checking the draft..."u8;
            case DraftCheck.Ready when !editor.Dirty:
                tone = Theme.TextDim;
                return "Editing. Place, move, merge or delete blocks, then apply."u8;
            case DraftCheck.Ready:
                tone = Theme.Good;
                return "Ready to apply."u8;
            default:
                return Kit.F($"Not ready: {editor.CheckProblem}");
        }
    }

    /// <summary>A compact chamfered button; returns true when pressed while enabled.</summary>
    private bool EditorButton(ImString id, ReadOnlySpan<byte> label, float2 size, bool enabled, bool primary = false, bool chosen = false)
    {
        var draw = ImGui.GetWindowDrawList();
        var position = ImGui.GetCursorScreenPos();
        if (!enabled)
            ImGui.BeginDisabled();
        var pressed = ImGui.InvisibleButton(id, size);
        var hot = enabled && ImGui.IsItemHovered();
        var held = enabled && ImGui.IsItemActive();
        if (!enabled)
            ImGui.EndDisabled();

        var max = position + size;
        var chamfer = Kit.Px(5);
        uint text;
        if (!enabled)
        {
            Kit.ChamferFill(draw, position, max, Kit.Color(Theme.Panel), chamfer);
            Kit.ChamferOutline(draw, position, max, Kit.Color(Theme.Line), chamfer);
            text = Theme.TextMute;
        }
        else if (primary)
        {
            Kit.ChamferFill(draw, position, max, Kit.Color(held ? Theme.Accent : hot ? Theme.AccentBright : Theme.Accent), chamfer);
            text = Theme.AccentInk;
        }
        else
        {
            Kit.ChamferFill(draw, position, max, Kit.Color(hot || chosen ? Theme.PanelRaised : Theme.Panel), chamfer);
            Kit.ChamferOutline(draw, position, max, Kit.Color(chosen ? Theme.Accent : hot ? Theme.LineStrong : Theme.Line), chamfer);
            text = chosen || hot ? Theme.Text : Theme.TextDim;
        }

        var measured = Kit.Measure(_ui.SansMedium, Theme.FontSmall, label);
        if (measured.X <= size.X - Kit.Px(4))
            Kit.Text(draw, _ui.SansMedium, Theme.FontSmall, position + (size - measured) * 0.5f, text, label);
        else
            Kit.ClippedText(draw, _ui.SansMedium, Theme.FontSmall, position + new float2(Kit.Px(2), (size.Y - measured.Y) * 0.5f), text, label, size.X - Kit.Px(4));
        return pressed && enabled;
    }

    /// <summary>A labelled whole number with buttons to lower and raise it; returns -1, 0 or +1.</summary>
    private int Stepper(int id, ReadOnlySpan<byte> label, int value, bool down, bool up, float width)
    {
        ImGui.PushID(id + 100);
        var start = ImGui.GetCursorScreenPos();
        var height = Kit.Px(28);
        var button = Kit.Px(28);
        var valueWidth = Kit.Px(40);
        var draw = ImGui.GetWindowDrawList();
        var labelWidth = width - button * 2 - valueWidth - Kit.Px(Theme.Space1) * 2;
        var labelSize = Kit.Measure(_ui.SansMedium, Theme.FontCaption, label);
        Kit.ClippedText(draw, _ui.SansMedium, Theme.FontCaption, start + new float2(0, (height - labelSize.Y) * 0.5f), Theme.TextDim, label, labelWidth);
        var step = 0;
        ImGui.SetCursorScreenPos(start + new float2(width - button * 2 - valueWidth, 0));
        if (EditorButton("##down"u8, "-"u8, new float2(button, height), down))
            step = -1;
        ImGui.SameLine(0, 0);
        var valueAt = ImGui.GetCursorScreenPos();
        var text = Kit.F($"{value}");
        var size = Kit.Measure(_ui.Mono, Theme.FontSmall, text);
        Kit.Text(draw, _ui.Mono, Theme.FontSmall, valueAt + new float2((valueWidth - size.X) * 0.5f, (height - size.Y) * 0.5f), Theme.Text, text);
        ImGui.Dummy(new float2(valueWidth, height));
        ImGui.SameLine(0, 0);
        if (EditorButton("##up"u8, "+"u8, new float2(button, height), up))
            step = 1;
        ImGui.PopID();
        return step;
    }

    /// <summary>A label and its value on one line, the value at the right edge.</summary>
    private void StatRow(ReadOnlySpan<byte> label, ReadOnlySpan<byte> value, uint tone, float width)
    {
        var draw = ImGui.GetWindowDrawList();
        var at = ImGui.GetCursorScreenPos();
        var height = Kit.Px(18);
        var labelWidth = Kit.Measure(_ui.SansMedium, Theme.FontCaption, label).X;
        Kit.Text(draw, _ui.SansMedium, Theme.FontCaption, at, Theme.TextMute, label);
        var room = width - labelWidth - Kit.Px(Theme.Space2);
        var size = Kit.Measure(_ui.Mono, Theme.FontCaption, value);
        if (size.X <= room)
            Kit.Text(draw, _ui.Mono, Theme.FontCaption, at + new float2(width - size.X, 0), tone, value);
        else
            Kit.ClippedText(draw, _ui.Mono, Theme.FontCaption, at + new float2(labelWidth + Kit.Px(Theme.Space2), 0), tone, value, room);
        ImGui.Dummy(new float2(width, height));
    }

    private void ClippedLine(ImFontPtr font, float size, uint tone, ReadOnlySpan<byte> text, float width)
    {
        var at = ImGui.GetCursorScreenPos();
        Kit.ClippedText(ImGui.GetWindowDrawList(), font, size, at, tone, text, width);
        ImGui.Dummy(new float2(width, Kit.Measure(font, size, "Ag"u8).Y + Kit.Px(2)));
    }

    /// <summary>Wrapped body text of the current window's width, in a colour.</summary>
    private void Paragraph(uint tone, ReadOnlySpan<byte> text, float width) =>
        Kit.Paragraph(_ui.Sans, Theme.FontSmall, tone, text, width / Kit.Scale);

    private static ReadOnlySpan<byte> PartName(ShipEditor editor, GameContent content) =>
        (uint)editor.Part < (uint)content.Blocks.Types.Count ? Kit.F($"{content.Blocks.Types[editor.Part].Name}") : "the part"u8;

    private static uint Packed(Vector3 color) =>
        ((uint)Math.Clamp(color.X * 255f, 0f, 255f) << 16) | ((uint)Math.Clamp(color.Y * 255f, 0f, 255f) << 8) | (uint)Math.Clamp(color.Z * 255f, 0f, 255f);

    private static ReadOnlySpan<byte> AxisName(int axis) => axis switch { 0 => "X"u8, 1 => "Y"u8, _ => "Z"u8 };

    /// <summary>A block shape for the player: a box, or a wedge and the way its slope faces.</summary>
    internal static ReadOnlySpan<byte> ShapeText(BlockShape shape)
    {
        if (shape == BlockShape.Box)
            return "BOX"u8;
        BlockShapes.Decode(shape, out _, out var u, out var v, out var su, out var sv);
        return Kit.F($"WEDGE, SLOPE FACES {(su > 0 ? "+"u8 : "-"u8)}{AxisName(u)} {(sv > 0 ? "+"u8 : "-"u8)}{AxisName(v)}");
    }

    private static ReadOnlySpan<byte> SideName(int side) => side switch
    {
        0 => "+X"u8,
        1 => "-X"u8,
        2 => "+Y"u8,
        3 => "-Y"u8,
        4 => "+Z"u8,
        _ => "-Z"u8,
    };

    /// <summary>What a part is for, as the part list says it.</summary>
    internal static ReadOnlySpan<byte> RoleText(PartRole role) => role switch
    {
        PartRole.Core => "Core: the ship cannot exist without one"u8,
        PartRole.Drive => "Jump drive"u8,
        PartRole.Engine => "Main engine: pushes forward only"u8,
        PartRole.Thruster => "Thruster: pushes every way"u8,
        PartRole.Gyro => "Gyro: turns the ship"u8,
        PartRole.Power => "Generator: makes power"u8,
        PartRole.Storage => "Battery: stores energy"u8,
        PartRole.Shield => "Shield emitter"u8,
        PartRole.Cargo => "Cargo space"u8,
        PartRole.Crew => "Crew quarters"u8,
        _ => "Structure and armour"u8,
    };

    /// <summary>The name of the figure a role's merge preview compares.</summary>
    private static ReadOnlySpan<byte> MainLabel(PartRole role) => role switch
    {
        PartRole.Engine or PartRole.Thruster => "THRUST KN"u8,
        PartRole.Gyro => "TORQUE KNM"u8,
        PartRole.Power => "MAKES MW"u8,
        PartRole.Storage => "STORES MJ"u8,
        PartRole.Shield => "SHIELD"u8,
        PartRole.Cargo => "CARGO M3"u8,
        PartRole.Crew => "CREW"u8,
        PartRole.Drive => "CHARGE MW"u8,
        _ => "MASS"u8,
    };

    /// <summary>Why a selection cannot be merged, in a sentence.</summary>
    internal static ReadOnlySpan<byte> MergeText(DraftEdit edit) => edit switch
    {
        DraftEdit.MixedTypes => "Only blocks of one type merge."u8,
        DraftEdit.NotABox => "Merging needs blocks that fill a box exactly: no gaps, no corners missing."u8,
        DraftEdit.BadSize => "The merged block would be longer than a block may be."u8,
        DraftEdit.BadSelection or DraftEdit.NoSuchBlock => "The selection changed; select the blocks again."u8,
        _ => "Select two or more blocks to merge them."u8,
    };

    /// <summary>Why a placement, move, paste or resize was refused, in a sentence.</summary>
    internal static ReadOnlySpan<byte> EditText(DraftEdit edit) => edit switch
    {
        DraftEdit.Overlaps => "Overlaps another block."u8,
        DraftEdit.BadSelection => "The selection names a block twice."u8,
        DraftEdit.MixedTypes => "Only blocks of one type merge."u8,
        DraftEdit.NotABox => "Those blocks do not fill a box exactly."u8,
        DraftEdit.BadSize => "That size is outside what a block may be."u8,
        DraftEdit.OutsideLimits => "That would make the ship larger than a design may be."u8,
        DraftEdit.TooManyBlocks => "The ship has as many blocks as a design may have."u8,
        DraftEdit.NoSuchBlock => "That block is gone."u8,
        DraftEdit.MixedDrives => "The ship has another kind of FTL drive; a ship takes one kind."u8,
        _ => "Nothing changed."u8,
    };

    /// <summary>Why the server refused or ended an edit, in a sentence; with the wait it asked for where there is one.</summary>
    internal ReadOnlySpan<byte> ReasonText(EditorReason reason, int wait) => reason switch
    {
        EditorReason.NotPiloting => "You can only edit the ship you are flying."u8,
        EditorReason.Jumping => "A ship cannot be edited during a jump or with a jump ordered."u8,
        EditorReason.Quiet => wait > 0
            ? Kit.F($"The ship took damage too recently. Try again in {wait} s.")
            : "The ship took damage too recently. Try again shortly."u8,
        EditorReason.Cancelled => "The edit was cancelled."u8,
        EditorReason.Damaged => "The ship took damage, so the edit was closed."u8,
        EditorReason.HullChanged => "You left the ship, or it was lost, so the edit was closed."u8,
        EditorReason.Busy => "The server is busy checking other designs. Apply again in a moment."u8,
        EditorReason.TooSoon => wait > 0
            ? Kit.F($"Too soon after the last apply. Try again in {wait} s.")
            : "Too soon after the last apply. Try again shortly."u8,
        EditorReason.TooLarge => "The design is too large to send."u8,
        EditorReason.UploadFailed => "The design did not arrive whole. Apply again."u8,
        EditorReason.UploadTimedOut => "Sending the design took too long. Apply again."u8,
        EditorReason.Invalid => "The server does not accept this design."u8,
        EditorReason.WrongKind => "The design is not the same kind of ship."u8,
        EditorReason.NoChange => "The draft is the ship as it already is."u8,
        EditorReason.CatalogueFull => "This server has no room for more designs until it restarts."u8,
        EditorReason.ClientNotReady => "Your game was not ready to show the new ship in time."u8,
        EditorReason.ClientFailed => "Your game could not prepare the new ship."u8,
        EditorReason.HullLimit => "The world has no room for another ship."u8,
        EditorReason.RepairFirst => "The ship is damaged. Repair it before you apply an edit."u8,
        EditorReason.Unavailable => "The server did not answer. Try again."u8,
        _ => "The edit ended."u8,
    };
}
