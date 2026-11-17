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
    /// too. In the escape drone the plain key opens the New ship dialog and Ctrl with it edits the drone
    /// (<see cref="NewShipDialog.HandleKeys"/>). Read before flight, and only with nothing else open: the menu, the
    /// map, a data sheet, Controls or the social workspace keep their own keys, and a text field keeps its letters.
    /// </summary>
    public void HandleEditorShortcut(bool active, SessionSnapshot? snapshot, PlayerInputBridge? bridge)
    {
        _editorHandled = false;
        // Escape that closed a tip was the tip's.
        if (!active || _tipBack || _screen != Screen.Session || snapshot is not { OwnHull: >= 0 } || bridge is null)
            return;
        if (_controlsHandled || _controlsOpen || _sessionMenuOpen || _mapOpen || SocialSurfaceOpen || _socialHandled)
            return;
        // A saved designs dialog over the editor has the keyboard: Enter and Escape are its, and no editor key is read.
        if (_designs.Open)
        {
            _editorHandled = _designs.HandleKeys(_app.Actions, _app.Devices, _app.Designs, _app.World.Editor, _notices, _designName, _ui.WantsTextInput, _ui.PointerOverInterface);
            return;
        }

        _editorHandled = _newShip.HandleKeys(_app.Actions, _app.Devices, snapshot, _app.World.Editor, bridge, _tips, _newShipName, _ui.WantsTextInput, _ui.PointerOverInterface);
    }

    private readonly EditorNoticeTracker _editorNotices = new();

    /// <summary>The sentence for a refused or ended edit, with what is kept; the tracker asks for it only when it posts.</summary>
    private ReadOnlySpan<byte> RefusalSentence(EditorReason reason, int wait, bool kept)
    {
        // Formatted first: one Kit.F inside another would write into the same arena while it is being filled.
        var text = ReasonText(reason, wait);
        return kept ? Kit.F($"{text} Your changes are kept: press {EditorKey} to go on.") : text;
    }

    /// <summary>The blue every selected block is outlined in, apart from the amber of acting and the red of loss.</summary>
    internal const uint SelectionBlue = 0x4D8DFF;

    private readonly EditorPalette _palette = new();
    private readonly EditorMenuPanel _editorMenu;
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
        var layout = EditorLayout.Of(_ui.DisplaySize, Kit.Scale);
        DrawTractorReach(draw, snapshot, world, editor, draft, layout);
        DrawEditorMarks(draw, world, editor, draft);
        DrawEditorParts(layout.Parts, editor, content);
        DrawEditorDetails(layout.Details, editor, draft, content, bridge);
        DrawEditorBar(layout.Bar, editor, draft, bridge);
        // Last, so the menu is in front of the panels.
        _editorMenu.Draw(editor, draft, _app.Devices.MousePosition);
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
            if (box.Type.Thrust > 0)
                DrawFacing(draw, world, box, Quaternion.Identity, Vector3.Zero);
        }

        // Selected mounted parts, outlined like selected blocks, each with where it points.
        var mounts = draft.Mounts;
        foreach (var mount in editor.MountSelection)
        {
            if ((uint)mount >= (uint)mounts.Length || (uint)mounts[mount].Parent >= (uint)boxes.Length)
                continue;
            DrawMount(draw, world, boxes[mounts[mount].Parent], mounts[mount], Kit.Color(SelectionBlue), Kit.Px(mount == editor.SelectedMount ? 2.5f : 2f), mount == editor.SelectedMount);
        }

        // The box being dragged out, in the world layer like the command view's: blue to select or add, red to remove.
        if (world.EditorBoxSelecting)
        {
            BoxSelect.Normalize(world.EditorBoxStart, world.EditorBoxEnd, out var boxMin, out var boxMax);
            var tone = world.EditorBoxMode == BoxSelectMode.Remove ? Theme.Bad : SelectionBlue;
            draw.AddRectFilled(boxMin, boxMax, Kit.Color(tone, 0.12f));
            draw.AddRect(boxMin, boxMax, Kit.Color(tone, 0.95f));
        }

        DrawSizingNote(world, editor);
        DrawMirrorPlanes(draw, world, editor, draft);
        if (editor.ResizingBlock && (uint)editor.ResizingIndex < (uint)boxes.Length && world.PartSizingFace >= 0)
            DrawSizingFace(draw, world, boxes[editor.ResizingIndex], world.PartSizingFace);
        if (!editor.HasHover || editor.ResizingBlock)
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

        if (editor.HasMountGhost && (uint)editor.GhostMount.Parent < (uint)boxes.Length)
        {
            // A part that mounts shows only itself, on the face under the pointer, and where it points.
            var ok = editor.GhostCheck == DraftEdit.Done;
            DrawMount(draw, world, boxes[editor.GhostMount.Parent], editor.GhostMount, Kit.Color(ok ? Theme.Good : Theme.Bad), Kit.Px(2), true);
            var mirrored = editor.GhostMountCopies;
            var mirroredChecks = editor.GhostMountCopyChecks;
            var left = 0;
            for (var i = 1; i < mirrored.Length; i++)
            {
                if (mirroredChecks[i] != DraftEdit.Done)
                    left++;
                if ((uint)mirrored[i].Parent < (uint)boxes.Length)
                    DrawMount(draw, world, boxes[mirrored[i].Parent], mirrored[i], Kit.Color(mirroredChecks[i] == DraftEdit.Done ? Theme.Good : Theme.Bad), Kit.Px(2), true);
            }

            if (!ok)
                PointerNote(EditText(editor.GhostCheck));
            else if (left > 0)
                PointerNote(Kit.F($"{left} mirrored not placed"), Theme.TextDim);
        }
        else if (editor.HasGhost)
        {
            var ok = editor.GhostCheck == DraftEdit.Done;
            var tone = ok ? Theme.Good : Theme.Bad;
            var ghost = new DesignBox(editor.GhostAt, editor.PartSize, null!, editor.PartShape, editor.GhostFacing);
            // While it glides into a turn the whole ghost, faces and all, is drawn turned the same way.
            world.GhostPose(out var turn, out var shift);
            DrawBlockEdges(draw, world, ghost, Kit.Color(tone), Kit.Px(2), turn, shift);
            // A thrust part shows where its nozzle points, so a turn that only changes that can be seen.
            var thrusts = editor.Content is { } parts && (uint)editor.Part < (uint)parts.Blocks.Types.Count && parts.Blocks.Types[editor.Part].Thrust > 0;
            if (thrusts)
                DrawFacing(draw, world, ghost, turn, shift);
            // The mirrored copies, each outlined by whether it would be placed.
            var mirroredBoxes = editor.GhostCopies;
            var mirroredBoxChecks = editor.GhostCopyChecks;
            var skippedCopies = 0;
            for (var i = 1; i < mirroredBoxes.Length; i++)
            {
                var copyOk = mirroredBoxChecks[i] == DraftEdit.Done;
                if (!copyOk)
                    skippedCopies++;
                DrawBlockEdges(draw, world, mirroredBoxes[i], Kit.Color(copyOk ? Theme.Good : Theme.Bad), Kit.Px(2));
                if (thrusts)
                    DrawFacing(draw, world, mirroredBoxes[i], Quaternion.Identity, Vector3.Zero);
            }
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
            else if (skippedCopies > 0)
                PointerNote(Kit.F($"{skippedCopies} mirrored not placed"), Theme.TextDim);
        }
        else if ((uint)editor.Hover.Mount < (uint)mounts.Length && (uint)mounts[editor.Hover.Mount].Parent < (uint)boxes.Length)
        {
            DrawMount(draw, world, boxes[mounts[editor.Hover.Mount].Parent], mounts[editor.Hover.Mount], Kit.Color(Theme.Accent, 0.8f), Kit.Px(1.5f), false);
        }
        else if ((uint)editor.Hover.Block < (uint)boxes.Length)
        {
            ref readonly var box = ref boxes[editor.Hover.Block];
            DrawFace(draw, world, box, editor.Hover.Side, Kit.Color(Theme.Accent, 0.18f), Kit.Color(Theme.Accent, 0.8f));
        }
    }

    /// <summary>The colours of the mirror planes: X red and Y green as the axes are drawn, Z blue.</summary>
    private const uint PlaneZ = 0x5AA0E0;
    private static readonly int[] MirrorPlaneRing = [0, 1, 3, 2];

    /// <summary>
    /// The active mirror planes, faintly, through the centre of the core: a rectangle each, square to its axis,
    /// reaching a cell past the draft on every side so it can be seen from outside the ship.
    /// </summary>
    private static void DrawMirrorPlanes(ImDrawListPtr draw, WorldView world, ShipEditor editor, BuildDraft draft)
    {
        if (editor.Mirror == MirrorAxes.None || !draft.TryMirrorFrame(out var centre2, out var low, out var high))
            return;
        Span<float2> points = stackalloc float2[4];
        Span<Double3> corners = stackalloc Double3[4];
        for (var axis = 0; axis < 3; axis++)
        {
            if (((int)editor.Mirror & (1 << axis)) == 0)
                continue;
            var tone = axis == 0 ? RingX : axis == 1 ? RingY : PlaneZ;
            int a = (axis + 1) % 3, b = (axis + 2) % 3;
            // Symmetric about the core on the two axes the plane spans, so the core is its middle.
            var reachA = MirrorReach(centre2, low, high, a);
            var reachB = MirrorReach(centre2, low, high, b);
            var middle = new Vector3(centre2.X * 0.5f - 0.5f, centre2.Y * 0.5f - 0.5f, centre2.Z * 0.5f - 0.5f);
            for (var i = 0; i < 4; i++)
            {
                var corner = middle;
                corner[a] += (i & 1) == 0 ? -reachA : reachA;
                corner[b] += (i & 2) == 0 ? -reachB : reachB;
                corners[i] = world.EditorPoint(corner);
            }

            // The ring order of the four corners: 0, 1, 3, 2.
            ReadOnlySpan<int> ring = MirrorPlaneRing;
            var whole = true;
            for (var i = 0; i < 4; i++)
                whole &= world.Project(corners[ring[i]], out points[i]);
            if (whole)
                draw.AddConvexPolyFilled(points, Kit.Color(tone, 0.07f));
            for (var i = 0; i < 4; i++)
            {
                if (world.ProjectSegment(corners[ring[i]], corners[ring[(i + 1) % 4]], out var from, out var to))
                    draw.AddLine(from, to, Kit.Color(tone, 0.4f), Kit.Px(1.25f));
            }
        }
    }

    // Half the plane's length along one axis: from the core's middle to the draft's far side, a cell more.
    private static float MirrorReach(GridSize centre2, GridSize low, GridSize high, int axis)
    {
        var middle = EditorGeometry.Of(centre2, axis) * 0.5f;
        return Math.Max(middle - EditorGeometry.Of(low, axis), EditorGeometry.Of(high, axis) - middle) + 1f;
    }

    /// <summary>
    /// A mounted part as the edges of the small box it fills on its face; with <paramref name="exhaust"/>, a line out
    /// of its middle the way its nozzle points, which is out of the face. The part pushes the ship the other way.
    /// </summary>
    private static void DrawMount(ImDrawListPtr draw, WorldView world, in DesignBox parent, in DesignMount mount, ImColor8 color, float thickness, bool exhaust)
    {
        EditorGeometry.MountBounds(parent, mount, out var low, out var high);
        Span<Double3> corners = stackalloc Double3[8];
        for (var i = 0; i < 8; i++)
            corners[i] = world.EditorPoint(new Vector3((i & 1) == 0 ? low.X : high.X, (i & 2) == 0 ? low.Y : high.Y, (i & 4) == 0 ? low.Z : high.Z));
        for (var i = 0; i < 8; i++)
        {
            for (var bit = 1; bit < 8; bit <<= 1)
            {
                if ((i & bit) == 0 && world.ProjectSegment(corners[i], corners[i | bit], out var a, out var b))
                    draw.AddLine(a, b, color, thickness);
            }
        }

        if (!exhaust)
            return;
        var normal = EditorGeometry.Normal((int)mount.Face);
        var middle = (low + high) * 0.5f;
        var from = middle + normal * ((high - low)[(int)mount.Face >> 1] * 0.5f);
        if (!world.ProjectSegment(world.EditorPoint(from), world.EditorPoint(from + normal * 0.8f), out var p, out var q))
            return;
        var orange = Kit.Color(ExhaustOrange);
        draw.AddLine(p, q, orange, Kit.Px(3f));
        draw.AddCircle(q, Kit.Px(5), orange, 0, Kit.Px(2f));
    }

    private static GridSize Shift(GridSize at, GridSize by) => new(at.X + by.X, at.Y + by.Y, at.Z + by.Z);

    /// <summary>The colours of the rotation rings: about x red, about y green, about z blue, as the axes are drawn.</summary>
    private const uint RingX = 0xE0605A;
    private const uint RingY = 0x6AD07A;
    private const uint RingZ = 0x5A9CF0;

    /// <summary>
    /// The three rings around the ghost of a part being turned, the one under the pointer brighter and thicker, each
    /// named by its axis, and how to use them beside the pointer.
    /// </summary>
    private void DrawRotationRings(ImDrawListPtr draw, WorldView world)
    {
        if (!world.PartRings(out var middle, out var radius))
            return;
        var hovered = world.RotationRingHovered;
        for (var axis = 0; axis < 3; axis++)
        {
            var tone = axis == 0 ? RingX : axis == 1 ? RingY : RingZ;
            var color = Kit.Color(tone, hovered == axis ? 1f : 0.7f);
            var thickness = Kit.Px(hovered == axis ? 3.5f : 2f);
            for (var i = 0; i < WorldView.RingSegments; i++)
            {
                if (world.ProjectSegment(world.EditorPoint(WorldView.RingPoint(middle, radius, axis, i)), world.EditorPoint(WorldView.RingPoint(middle, radius, axis, i + 1)), out var a, out var b))
                    draw.AddLine(a, b, color, thickness);
            }

            // The y and z rings meet at their first point, so the z label sits half way round, on the other side.
            if (world.Project(world.EditorPoint(WorldView.RingPoint(middle, radius, axis, axis == 2 ? WorldView.RingSegments / 2 : 0)), out var label))
                Kit.Text(draw, _ui.SansMedium, Theme.FontSmall, label + new float2(Kit.Px(6), -Kit.Px(18)), tone, axis == 0 ? "TURN X"u8 : axis == 1 ? "TURN Y"u8 : "TURN Z"u8);
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
        if (editor.ResizingBlock && editor.Draft is { } draft && (uint)editor.ResizingIndex < (uint)draft.Boxes.Length)
        {
            var box = draft.Boxes[editor.ResizingIndex];
            var current = box.Size;
            var max = editor.ResizingMaxSide;
            var side = world.PartSizingFace;
            if (side < 0)
                PointerNote(Kit.F($"SIZE X {current.X}  Y {current.Y}  Z {current.Z}: point at a face of the block, then drag it. Esc puts it back"), Theme.Info, -Kit.Px(28));
            else if (world.PartSizingEdgeOn)
                PointerNote(Kit.F($"SIZE X {current.X}  Y {current.Y}  Z {current.Z} (1 to {max}): {FaceName(side)} faces you, drag up or down"), Theme.Info, -Kit.Px(28));
            else
                PointerNote(Kit.F($"SIZE X {current.X}  Y {current.Y}  Z {current.Z} (1 to {max}): drag the {FaceName(side)} face"), Theme.Info, -Kit.Px(28));
            return;
        }

        if (!editor.SizingPart || editor.Content is not { } content)
            return;
        var size = editor.PartSize;
        var face = world.PartSizingFace;
        var limit = editor.PartMaxSide;
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

    /// <summary>The colour of a thrust part's exhaust mark: where its nozzle points, the opposite of the way it pushes.</summary>
    private const uint ExhaustOrange = 0xFF8A3D;

    /// <summary>
    /// Where a thrust part's nozzle points: the side it faces, filled, and a line out of its middle that widens to
    /// a bar, as an exhaust would leave it. The part pushes the ship the other way.
    /// </summary>
    private static void DrawFacing(ImDrawListPtr draw, WorldView world, in DesignBox box, Quaternion turn, Vector3 shift)
    {
        var side = (int)box.Facing;
        DrawFace(draw, world, box, side, Kit.Color(ExhaustOrange, 0.3f), Kit.Color(ExhaustOrange), turn, shift);
        var axis = side >> 1;
        var normal = EditorGeometry.Normal(side);
        var half = new Vector3(box.Size.X, box.Size.Y, box.Size.Z) * 0.5f;
        var middle = new Vector3(box.At.X - 0.5f, box.At.Y - 0.5f, box.At.Z - 0.5f) + half + shift;
        var from = middle + Vector3.Transform(normal * half[axis], turn);
        var to = middle + Vector3.Transform(normal * (half[axis] + 1.2f), turn);
        if (!world.ProjectSegment(world.EditorPoint(from), world.EditorPoint(to), out var a, out var b))
            return;
        var color = Kit.Color(ExhaustOrange);
        draw.AddLine(a, b, color, Kit.Px(3f));
        draw.AddCircleFilled(a, Kit.Px(4), color);
        draw.AddCircle(b, Kit.Px(6), color, 0, Kit.Px(2f));
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
    /// The part list: a rail of category icons down its left side (a tooltip names each), a search at the top, and
    /// a card per part with its figures at the size parts are placed at; the fuel tanks share one card, whose fuel
    /// is chosen in the details panel. A card is clicked to place that part with clicks, or dragged onto the ship.
    /// The size controls stay at the foot.
    /// </summary>
    private void DrawEditorParts(in HudRect rect, ShipEditor editor, GameContent content)
    {
        var tuning = Tuning;
        _palette.Craft = editor.Draft?.Class ?? editor.Source?.Class ?? HullClass.Ship;
        _palette.KeepTabShown(tuning, content.Blocks.Types);
        BeginEditorPanel("##editorparts"u8, "##editorpartsbody"u8, rect);

        // The rail takes the panel's left side; everything else lives in a column to its right.
        var origin = ImGui.GetCursorScreenPos();
        var whole = ImGui.GetContentRegionAvail();
        DrawCategoryRail(tuning, content.Blocks.Types, origin, whole.Y);
        var railSpace = Kit.Px(EditorLayout.RailWidth + EditorLayout.RailGap);
        ImGui.SetCursorScreenPos(origin + new float2(railSpace, 0));
        ImGui.BeginChild("##editorpartsmain"u8, new float2(whole.X - railSpace, whole.Y), ImGuiChildFlags.None, ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoScrollbar);
        var width = ImGui.GetContentRegionAvail().X;

        // Placing a tank by any route remembers its fuel for the one Tank card.
        if (editor.Placing && (uint)editor.Part < (uint)content.Blocks.Types.Count && EditorPalette.IsTank(content.Blocks.Types[editor.Part]))
            _palette.TankFuel = content.Blocks.Types[editor.Part].Tank;

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

        var gap = Kit.Px(Theme.Space1);
        var heading = _palette.Query.Length > 0 ? "SEARCH RESULTS"u8 : (uint)_palette.Category < (uint)tuning.Categories.Length ? tuning.Categories[_palette.Category].Label : "PARTS"u8;
        Kit.Caption(heading);
        Kit.Space(Theme.Space1);
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
        var footer = 3 * Kit.Px(16) + Kit.Px(28) + 2 * Kit.Px(Theme.Space2) + 2 * Kit.Px(Theme.Space1) + MirrorRowHeight;
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
            if (!_palette.ShowsCard(tuning, types, i))
                continue;
            shown++;
            DrawPartCard(i, type, figures[i], tuning.Compact(type), type.Module.Family != ModuleFamily.None || type.Tractor is not null || type.Laser is not null || type.Bay is not null ? Kit.Px(ModuleCardHeight) : cardHeight, hull, editor);
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
        if (editor.PartIsMount)
        {
            // A part that mounts has one size and no shape, and points out of the face it is put on.
            var spec = content.Blocks.Types[editor.Part].Mount!;
            ClippedLine(_ui.Mono, Theme.FontCaption, Theme.TextDim, Kit.F($"MOUNTS ON A FACE: {spec.Side / (float)FaceMounts.PerCell:0.#} x {spec.Side / (float)FaceMounts.PerCell:0.#} CELL"), width);
            ClippedLine(_ui.Mono, Theme.FontCaption, Theme.TextDim, "NO SIZE, NO TURN"u8, width);
            ClippedLine(_ui.Mono, Theme.FontCaption, ExhaustOrange, "POINTS OUT OF ITS FACE"u8, width);
        }
        else
        {
            ClippedLine(_ui.Mono, Theme.FontCaption, Theme.TextDim, Kit.F($"{size.X} x {size.Y} x {size.Z} CELLS"), width);
            ClippedLine(_ui.Mono, Theme.FontCaption, Theme.TextDim, ShapeText(editor.PartShape), width);
            if (editor.PartBoxOnly)
                ClippedLine(_ui.Mono, Theme.FontCaption, Theme.TextMute, "NEVER SLANTED: NO WEDGE"u8, width);
            if ((uint)editor.Part < (uint)content.Blocks.Types.Count && content.Blocks.Types[editor.Part].Thrust > 0)
                ClippedLine(_ui.Mono, Theme.FontCaption, ExhaustOrange, FacingText(editor.PartFacing), width);
        }

        Kit.Space(Theme.Space1);
        var pair = MathF.Floor((width - gap) * 0.5f);
        if (EditorPalette.HandBack(EditorButton("##shapebox"u8, "Box"u8, new float2(pair, Kit.Px(26)), editor.CanEdit, false, editor.PartShape == BlockShape.Box)))
            editor.SetPartShape(BlockShape.Box);
        ImGui.SameLine(0, gap);
        if (EditorPalette.HandBack(EditorButton("##shapewedge"u8, "Wedge"u8, new float2(pair, Kit.Px(26)), editor.CanEdit && !editor.PartBoxOnly, false, editor.PartShape != BlockShape.Box)))
            editor.SetPartShape(editor.PartShape == BlockShape.Box ? BlockShape.WedgeXPlusPlus : editor.PartShape);
        DrawMirrorRow(editor, width, gap);
        ImGui.EndChild();
        EndEditorPanel();
    }

    /// <summary>The category the tooltip is held open for by a scripted capture (<c>--open edit-palette-tip</c>); -1 for none.</summary>
    private int _railTipScripted = -1;

    /// <summary>Holds the tooltip of a category's rail button open without a pointer, and chooses that category. For captures.</summary>
    public void ShowEditorRailTip(string name)
    {
        var categories = Tuning.Categories;
        for (var i = 0; i < categories.Length; i++)
            if (categories[i].Name == name)
                _railTipScripted = i;
    }

    /// <summary>
    /// The category rail: one icon button per category that has a part for this craft, down the panel's left side,
    /// the chosen one filled with an accent bar (none while a search covers every category). Hovering a button shows
    /// the category's name and its tip beside it. Choosing one clears the search.
    /// </summary>
    private void DrawCategoryRail(EditorTuning tuning, IReadOnlyList<BlockType> types, float2 origin, float height)
    {
        var draw = ImGui.GetWindowDrawList();
        var categories = tuning.Categories;
        var count = 0;
        for (var i = 0; i < categories.Length; i++)
            if (_palette.TabHasParts(tuning, types, i))
                count++;
        var size = new float2(Kit.Px(EditorLayout.RailWidth), EditorLayout.RailButtonHeight(height, count, Kit.Scale));
        var gap = Kit.Px(EditorLayout.RailButtonGap);
        var stroke = MathF.Max(1f, Kit.Px(1.25f));
        var radius = MathF.Floor(Math.Min(size.X, size.Y) * 0.3f);
        var y = 0f;
        var tipped = -1;
        var tipMin = default(float2);
        for (var i = 0; i < categories.Length; i++)
        {
            // A category with no part for this craft is not shown.
            if (!_palette.TabHasParts(tuning, types, i))
                continue;
            var min = origin + new float2(0, y);
            var max = min + size;
            ImGui.SetCursorScreenPos(min);
            ImGui.PushID(i);
            var pressed = ImGui.InvisibleButton("##category"u8, size);
            var hot = ImGui.IsItemHovered();
            ImGui.PopID();
            var chosen = _palette.Query.Length == 0 && _palette.Category == i;
            Kit.ChamferFill(draw, min, max, Kit.Color(chosen || hot ? Theme.PanelRaised : Theme.Panel, chosen ? 0.95f : 0.8f), Kit.Px(5));
            Kit.ChamferOutline(draw, min, max, Kit.Color(chosen ? Theme.Accent : hot ? Theme.LineStrong : Theme.Line), Kit.Px(5));
            if (chosen)
                draw.AddRectFilled(min + new float2(0, Kit.Px(4)), min + new float2(Kit.Px(2), size.Y - Kit.Px(4)), Kit.Color(Theme.Accent));
            var tone = chosen ? Theme.Accent : hot ? Theme.AccentBright : Theme.TextDim;
            EditorIcons.Draw(draw, categories[i].Glyph, new float2(MathF.Floor((min.X + max.X) * 0.5f) + 0.5f, MathF.Floor((min.Y + max.Y) * 0.5f) + 0.5f), radius, Kit.Color(tone), stroke);
            if (hot || i == _railTipScripted)
            {
                tipped = i;
                tipMin = new float2(max.X + Kit.Px(6), min.Y);
            }

            if (EditorPalette.HandBack(pressed))
            {
                _palette.Category = i;
                _partSearch.Clear();
                _palette.Query = "";
            }

            y += size.Y + gap;
        }

        if (tipped < 0)
            return;
        var category = categories[tipped];
        var fore = ImGui.GetForegroundDrawList();
        var title = Kit.Measure(_ui.SansMedium, Theme.FontSmall, category.NameText);
        var line = Kit.Measure(_ui.Sans, Theme.FontCaption, category.TipText);
        var pad = Kit.Px(Theme.Space2);
        var box = new float2(MathF.Ceiling(Math.Max(title.X, line.X)) + pad * 2, pad * 2 + Kit.Px(20) + Kit.Px(16));
        tipMin.X = Math.Min(tipMin.X, Math.Max(0f, _ui.DisplaySize.X - box.X - Kit.Px(6)));
        tipMin.Y = Math.Min(tipMin.Y, Math.Max(0f, _ui.DisplaySize.Y - box.Y - Kit.Px(6)));
        Kit.PanelFrame(fore, tipMin, tipMin + box);
        Kit.Text(fore, _ui.SansMedium, Theme.FontSmall, tipMin + new float2(pad, pad), Theme.Text, category.NameText);
        Kit.Text(fore, _ui.Sans, Theme.FontCaption, tipMin + new float2(pad, pad + Kit.Px(20)), Theme.TextDim, category.TipText);
    }

    /// <summary>The height <see cref="DrawMirrorRow"/> takes: its caption and the checkboxes, with the space above them.</summary>
    private static float MirrorRowHeight => Kit.Px(16) + Kit.Px(24) + Kit.Px(Theme.Space2) + Kit.Px(Theme.Space1);

    /// <summary>
    /// Three checkboxes, X, Y and Z, for the planes through the core that placing, deleting and mounting are mirrored
    /// across. A change is a client preference: it is saved with the settings and sent to no one.
    /// </summary>
    private void DrawMirrorRow(ShipEditor editor, float width, float gap)
    {
        Kit.Space(Theme.Space2);
        Kit.Caption("MIRROR"u8);
        Kit.Space(Theme.Space1);
        var cell = MathF.Floor((width - 2 * gap) / 3f);
        var axes = editor.Mirror;
        var next = axes;
        for (var axis = 0; axis < 3; axis++)
        {
            if (axis > 0)
                ImGui.SameLine(0, gap);
            var flag = (MirrorAxes)(1 << axis);
            ImGui.PushID(axis);
            var label = axis == 0 ? "X"u8 : axis == 1 ? "Y"u8 : "Z"u8;
            if (EditorPalette.HandBack(EditorCheckbox("##mirror"u8, label, new float2(cell, Kit.Px(24)), editor.CanEdit, (axes & flag) != 0)))
                next ^= flag;
            ImGui.PopID();
        }

        if (next == axes)
            return;
        editor.SetMirror(next);
        _app.Settings.EditorMirror = (int)next;
        if (!_app.Devices.Muted)
            _app.Settings.Save();
    }

    /// <summary>A checkbox cell: a square that is filled and ticked when on, and its label. True when pressed.</summary>
    private bool EditorCheckbox(ImString id, ReadOnlySpan<byte> label, float2 size, bool enabled, bool on)
    {
        var draw = ImGui.GetWindowDrawList();
        var position = ImGui.GetCursorScreenPos();
        if (!enabled)
            ImGui.BeginDisabled();
        var pressed = ImGui.InvisibleButton(id, size);
        var hot = enabled && ImGui.IsItemHovered();
        if (!enabled)
            ImGui.EndDisabled();
        var chamfer = Kit.Px(5);
        Kit.ChamferFill(draw, position, position + size, Kit.Color(hot ? Theme.PanelRaised : Theme.Panel), chamfer);
        Kit.ChamferOutline(draw, position, position + size, Kit.Color(on ? Theme.Accent : hot ? Theme.LineStrong : Theme.Line), chamfer);
        var box = Kit.Px(12);
        var min = position + new float2(Kit.Px(8), (size.Y - box) * 0.5f);
        var max = min + new float2(box, box);
        var tone = !enabled ? Theme.TextMute : on ? Theme.Accent : Theme.TextDim;
        if (on)
        {
            draw.AddRectFilled(min, max, Kit.Color(tone));
            // The tick, in the ink the primary buttons use over the accent.
            var thick = Kit.Px(1.75f);
            var a = min + new float2(box * 0.2f, box * 0.55f);
            var b = min + new float2(box * 0.42f, box * 0.78f);
            var c = min + new float2(box * 0.8f, box * 0.25f);
            draw.AddLine(a, b, Kit.Color(Theme.AccentInk), thick);
            draw.AddLine(b, c, Kit.Color(Theme.AccentInk), thick);
        }
        else
            draw.AddRect(min, max, Kit.Color(tone), 0, 0, Kit.Px(1.5f));
        var measured = Kit.Measure(_ui.SansMedium, Theme.FontSmall, label);
        Kit.Text(draw, _ui.SansMedium, Theme.FontSmall, new float2(max.X + Kit.Px(8), position.Y + (size.Y - measured.Y) * 0.5f), on || hot ? Theme.Text : Theme.TextDim, label);
        return pressed && enabled;
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
        // One card stands for every tank type, so it is lit while any of them is the part to place.
        var tankCard = EditorPalette.IsTank(type);
        var placedType = editor.Content is { } placed && (uint)editor.Part < (uint)placed.Blocks.Types.Count ? placed.Blocks.Types[editor.Part] : null;
        var isPart = editor.Part == part || (tankCard && placedType is not null && EditorPalette.IsTank(placedType));
        var chosen = editor.Placing && isPart;
        var carried = editor.Carry == EditorCarry.Part && isPart;
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

        Kit.ClippedText(draw, _ui.SansMedium, Theme.FontSmall, at + new float2(left, Kit.Px(4)), chosen ? Theme.Text : Theme.TextDim, tankCard ? "Tank"u8 : Kit.F($"{type.Name}"), room);
        if (role == PartRole.Module)
        {
            DrawModuleCard(draw, at, type.Module, figures, left, room, max.X);
            ImGui.PopID();
            return;
        }

        if (type.Tractor is { } tractor)
        {
            DrawTractorCard(draw, at, tractor, figures, left, room);
            ImGui.PopID();
            return;
        }

        if (type.Laser is { } laser)
        {
            DrawLaserCard(draw, at, laser, figures, left, room);
            ImGui.PopID();
            return;
        }

        if (type.Bay is { } bay)
        {
            DrawBayCard(draw, at, bay, figures, left, room);
            ImGui.PopID();
            return;
        }

        // The tank card names the fuel it would place; the fuel is chosen in the details panel.
        Kit.ClippedText(draw, _ui.Sans, Theme.FontCaption, at + new float2(left, Kit.Px(22)), Theme.TextMute, tankCard ? Kit.F($"{type.Name}") : RoleText(role), room - tag);
        Kit.ClippedText(draw, _ui.Mono, Theme.FontCaption, at + new float2(left, Kit.Px(38)), Theme.TextDim, Kit.F($"{figures.Mass:0.##} T   {figures.Hull:0} HP"), room);
        var main = MainFigures(role, figures);
        if (!main.IsEmpty)
            Kit.ClippedText(draw, _ui.Mono, Theme.FontCaption, at + new float2(left, Kit.Px(53)), Theme.Text, main, room);
        ImGui.PopID();
    }

    /// <summary>Height of a module's part card, in unscaled pixels: two more lines than another part's, for the figures.</summary>
    internal const float ModuleCardHeight = 100f;

    /// <summary>
    /// A module's card below its name: the family, its quality as a badge in the tier's colour with its mark, the
    /// mass and that it mounts on a face (it has no hit points of its own), and the figures it is chosen for (reach, scan time, power draw, cooldown). Every figure is the
    /// block's own from the content, at its quality.
    /// </summary>
    private void DrawModuleCard(ImDrawListPtr draw, float2 at, in ModuleSpec spec, in PartFigures figures, float left, float room, float right)
    {
        var look = ModuleLook.Current;
        var tier = look.Tier(spec.Quality);
        Kit.ClippedText(draw, _ui.Sans, Theme.FontCaption, at + new float2(left, Kit.Px(21)), Theme.TextMute, look.Family(spec.Family).Label, room);
        var badge = Kit.Measure(_ui.SansMedium, Theme.FontCaption, tier.Label);
        var badgeMin = at + new float2(left, Kit.Px(36));
        var badgeMax = badgeMin + badge + new float2(Kit.Px(8), Kit.Px(2));
        draw.AddRectFilled(badgeMin, badgeMax, Kit.Color(tier.Rgb, 0.18f));
        draw.AddRect(badgeMin, badgeMax, Kit.Color(tier.Rgb, 0.9f));
        Kit.Text(draw, _ui.SansMedium, Theme.FontCaption, badgeMin + new float2(Kit.Px(4), Kit.Px(1)), tier.Rgb, tier.Label);
        Kit.ClippedText(draw, _ui.Mono, Theme.FontCaption, new float2(badgeMax.X + Kit.Px(8), badgeMin.Y + Kit.Px(1)), Theme.Text, Kit.F($"MK {spec.Mark}"), Math.Max(0f, right - badgeMax.X - Kit.Px(14)));
        Kit.ClippedText(draw, _ui.Mono, Theme.FontCaption, at + new float2(left, Kit.Px(55)), Theme.TextDim, Kit.F($"{figures.Mass:0.##} T   FACE MOUNT"), room);
        var reach = spec.Range >= 1000 ? Kit.F($"RANGE {spec.Range / 1000:0.##} KM   SCAN {spec.ScanSeconds:0.#} S") : Kit.F($"RANGE {spec.Range:0} M   SCAN {spec.ScanSeconds:0.#} S");
        Kit.ClippedText(draw, _ui.Mono, Theme.FontCaption, at + new float2(left, Kit.Px(70)), Theme.Text, reach, room);
        Kit.ClippedText(draw, _ui.Mono, Theme.FontCaption, at + new float2(left, Kit.Px(85)), Theme.Text, Kit.F($"DRAWS {spec.PowerDraw:0.##} MW   COOLDOWN {spec.CooldownSeconds:0.#} S"), room);
    }

    /// <summary>
    /// A tractor's card below its name, laid out as a module's: what it is, its quality as a badge in the tier's colour,
    /// the mass, then its reach and pull and what it draws. Every figure is the block's own at its quality.
    /// </summary>
    private void DrawTractorCard(ImDrawListPtr draw, float2 at, TractorSpec spec, in PartFigures figures, float left, float room)
    {
        var tier = ModuleLook.Current.Tier(spec.Quality);
        Kit.ClippedText(draw, _ui.Sans, Theme.FontCaption, at + new float2(left, Kit.Px(21)), Theme.TextMute, "TRACTOR BEAM"u8, room);
        var badge = Kit.Measure(_ui.SansMedium, Theme.FontCaption, tier.Label);
        var badgeMin = at + new float2(left, Kit.Px(36));
        var badgeMax = badgeMin + badge + new float2(Kit.Px(8), Kit.Px(2));
        draw.AddRectFilled(badgeMin, badgeMax, Kit.Color(tier.Rgb, 0.18f));
        draw.AddRect(badgeMin, badgeMax, Kit.Color(tier.Rgb, 0.9f));
        Kit.Text(draw, _ui.SansMedium, Theme.FontCaption, badgeMin + new float2(Kit.Px(4), Kit.Px(1)), tier.Rgb, tier.Label);
        Kit.ClippedText(draw, _ui.Mono, Theme.FontCaption, at + new float2(left, Kit.Px(55)), Theme.TextDim, Kit.F($"{figures.Mass:0.##} T   FACE MOUNT"), room);
        Kit.ClippedText(draw, _ui.Mono, Theme.FontCaption, at + new float2(left, Kit.Px(70)), Theme.Text, Kit.F($"RANGE {spec.Range:0} M   PULL {spec.Acceleration:0.#} M/S2"), room);
        Kit.ClippedText(draw, _ui.Mono, Theme.FontCaption, at + new float2(left, Kit.Px(85)), Theme.Text, Kit.F($"LIFTS {spec.MaxMass / 1000:0.#} T   DRAWS {spec.BasePower:0.##} MW + {spec.PowerPerTonne:0.##}/T"), room);
    }

    /// <summary>
    /// A mining laser's card below its name, laid out as a tractor's: what it is, its quality as a badge in the tier's
    /// colour, the mass, then how far it reaches and how wide and fast it cuts, and what it draws while firing and how
    /// far off its face it may aim. Every figure is the block's own at its quality.
    /// </summary>
    private void DrawLaserCard(ImDrawListPtr draw, float2 at, LaserSpec spec, in PartFigures figures, float left, float room)
    {
        var tier = ModuleLook.Current.Tier(spec.Quality);
        Kit.ClippedText(draw, _ui.Sans, Theme.FontCaption, at + new float2(left, Kit.Px(21)), Theme.TextMute, "MINING LASER: CUTS ROCK"u8, room);
        var badge = Kit.Measure(_ui.SansMedium, Theme.FontCaption, tier.Label);
        var badgeMin = at + new float2(left, Kit.Px(36));
        var badgeMax = badgeMin + badge + new float2(Kit.Px(8), Kit.Px(2));
        draw.AddRectFilled(badgeMin, badgeMax, Kit.Color(tier.Rgb, 0.18f));
        draw.AddRect(badgeMin, badgeMax, Kit.Color(tier.Rgb, 0.9f));
        Kit.Text(draw, _ui.SansMedium, Theme.FontCaption, badgeMin + new float2(Kit.Px(4), Kit.Px(1)), tier.Rgb, tier.Label);
        Kit.ClippedText(draw, _ui.Mono, Theme.FontCaption, at + new float2(left, Kit.Px(55)), Theme.TextDim, Kit.F($"{figures.Mass:0.##} T   FACE MOUNT"), room);
        Kit.ClippedText(draw, _ui.Mono, Theme.FontCaption, at + new float2(left, Kit.Px(70)), Theme.Text, Kit.F($"RANGE {spec.Range:0} M   CUT {spec.CutRadius:0.##} M   {spec.CutRate:0.##} M/S"), room);
        Kit.ClippedText(draw, _ui.Mono, Theme.FontCaption, at + new float2(left, Kit.Px(85)), Theme.Text, Kit.F($"DRAWS {spec.Power:0.##} MW   AIMS {spec.GimbalDegrees:0} DEG"), room);
    }

    /// <summary>
    /// A drone bay's card below its name, laid out as a tractor's: what it is, its quality as a badge in the tier's
    /// colour, the mass and hit points, then how many drones it keeps, launches at once and how long one takes to rebuild.
    /// </summary>
    private void DrawBayCard(ImDrawListPtr draw, float2 at, BaySpec spec, in PartFigures figures, float left, float room)
    {
        var tier = ModuleLook.Current.Tier(spec.Quality);
        Kit.ClippedText(draw, _ui.Sans, Theme.FontCaption, at + new float2(left, Kit.Px(21)), Theme.TextMute, "DRONE BAY: ONE HATCH"u8, room);
        var badge = Kit.Measure(_ui.SansMedium, Theme.FontCaption, tier.Label);
        var badgeMin = at + new float2(left, Kit.Px(36));
        var badgeMax = badgeMin + badge + new float2(Kit.Px(8), Kit.Px(2));
        draw.AddRectFilled(badgeMin, badgeMax, Kit.Color(tier.Rgb, 0.18f));
        draw.AddRect(badgeMin, badgeMax, Kit.Color(tier.Rgb, 0.9f));
        Kit.Text(draw, _ui.SansMedium, Theme.FontCaption, badgeMin + new float2(Kit.Px(4), Kit.Px(1)), tier.Rgb, tier.Label);
        Kit.ClippedText(draw, _ui.Mono, Theme.FontCaption, at + new float2(left, Kit.Px(55)), Theme.TextDim, Kit.F($"{figures.Mass:0.##} T   {figures.Hull:0} HP"), room);
        Kit.ClippedText(draw, _ui.Mono, Theme.FontCaption, at + new float2(left, Kit.Px(70)), Theme.Text, Kit.F($"KEEPS {spec.Capacity} DRONES   {spec.LaunchRate} AT ONCE"), room);
        Kit.ClippedText(draw, _ui.Mono, Theme.FontCaption, at + new float2(left, Kit.Px(85)), Theme.Text, Kit.F($"REBUILDS ONE IN {spec.RebuildSeconds:0} S"), room);
    }

    /// <summary>The figures a part of a role is chosen for, on one line: what it gives, then what it draws.</summary>
    private static ReadOnlySpan<byte> MainFigures(PartRole role, in PartFigures f) => role switch
    {
        PartRole.Engine or PartRole.Thruster => Kit.F($"{f.Thrust:0.#} KN   {f.FuelBurn:0.###} KG/S   {f.PowerDraw:0.##} MW"),
        PartRole.Tank => Kit.F($"HOLDS {f.FuelCapacity:0.#} KG"),
        PartRole.Gyro => Kit.F($"{f.Torque:0.#} KNM   DRAWS {f.PowerDraw:0.##} MW"),
        PartRole.Power => Kit.F($"MAKES {f.PowerOutput:0.##} MW"),
        PartRole.Storage => Kit.F($"STORES {f.Energy:0.#} MJ"),
        PartRole.Shield => Kit.F($"{f.Shield:0} SHIELD   {f.Recharge:0.#}/S   DRAWS {f.PowerDraw:0.##} MW"),
        PartRole.Cargo => Kit.F($"{f.Cargo:0.#} u CARGO"),
        PartRole.Tractor => "AUTOMATIC CARGO PICKUP"u8,
        PartRole.Laser => "CUTS ROCK"u8,
        PartRole.Crew => Kit.F($"{f.Crew:0} CREW"),
        PartRole.Drive => Kit.F($"CHARGES AT {f.FtlPower:0.#} MW"),
        PartRole.Core => Kit.F($"DRAWS {f.PowerDraw:0.##} MW"),
        _ => default,
    };

    /// <summary>Kilograms of fuel the checked draft would dump, over every fuel.</summary>
    private static float TotalDumped(ShipEditor editor) => editor.FuelDump.Nitrogen + editor.FuelDump.Hydrazine + editor.FuelDump.Bipropellant;

    /// <summary>
    /// What an apply does to the fuel, a row a fuel like the FIGURES rows: the fuel's name on the left, its level
    /// before and after on the right with a drawn arrow, then a note under it only when fuel moves between tanks or is dumped.
    /// </summary>
    private void DrawFuelOnApply(ShipEditor editor, float width)
    {
        if (editor.Check != DraftCheck.Ready || !editor.Dirty || editor.Source is not { } source || editor.Checked.Design is not { } design)
            return;
        var any = false;
        for (var fuel = 1; fuel <= Fuels.Count && !any; fuel++)
            any = source.Flight.FuelCapacity[(FuelKind)fuel] > 0 || design.Flight.FuelCapacity[(FuelKind)fuel] > 0;
        if (!any)
            return;
        Kit.Space(Theme.Space4);
        Kit.Caption("FUEL ON APPLY"u8, Theme.Accent);
        Kit.Space(Theme.Space1);
        var known = editor.FuelKnown;
        for (var fuel = 1; fuel <= Fuels.Count; fuel++)
        {
            var kind = (FuelKind)fuel;
            var capBefore = source.Flight.FuelCapacity[kind];
            var capAfter = design.Flight.FuelCapacity[kind];
            if (capBefore <= 0 && capAfter <= 0)
                continue;
            var name = ConsoleFuel.FullName(kind);
            if (!known)
            {
                FuelChangeRow(name, capBefore, capAfter, "KG CAPACITY"u8, Theme.TextDim, width);
                continue;
            }

            var before = editor.FuelPlan.Before[kind];
            var after = editor.FuelPlan.After[kind];
            var moved = editor.FuelPlan.Moved[kind];
            var dumped = editor.FuelDump[kind];
            FuelChangeRow(name, before, after, "KG"u8, dumped > 0f ? ExhaustOrange : Theme.Text, width);
            ImGui.Indent(Kit.Px(Theme.Space2));
            if (moved > 0f)
                StatRow("MOVED"u8, Kit.F($"{moved:0.##} KG"), Theme.Info, width - Kit.Px(Theme.Space2));
            if (dumped > 0f)
                StatRow("DUMPED"u8, Kit.F($"{dumped:0.##} KG"), ExhaustOrange, width - Kit.Px(Theme.Space2));
            ImGui.Unindent(Kit.Px(Theme.Space2));
        }

        if (!known)
            Paragraph(ExhaustOrange, "This ship's tank levels are not known here, so a tank that changes may lose its fuel."u8, width);
    }

    /// <summary>One fuel row: name on the left, "before > after UNIT" on the right, the arrow drawn so no font needs the glyph.</summary>
    private void FuelChangeRow(ReadOnlySpan<byte> name, float before, float after, ReadOnlySpan<byte> unit, uint tone, float width)
    {
        var draw = ImGui.GetWindowDrawList();
        var at = ImGui.GetCursorScreenPos();
        var font = _ui.Mono;
        var size = Theme.FontCaption;
        var tail = Kit.Measure(font, size, Kit.F($"{after:0.##} {unit}")).X;
        var head = Kit.Measure(font, size, Kit.F($"{before:0.##}")).X;
        var gap = Kit.Px(Theme.Space1);
        var arrow = Kit.Px(9);
        var tailAt = at.X + width - tail;
        Kit.Text(draw, font, size, new float2(tailAt, at.Y), tone, Kit.F($"{after:0.##} {unit}"));
        var tip = new float2(tailAt - gap, at.Y + Kit.Px(size) * 0.55f);
        var color = Kit.Color(Theme.TextDim);
        draw.AddLine(tip - new float2(arrow, 0), tip, color, Kit.Px(1));
        draw.AddLine(tip - new float2(Kit.Px(4), Kit.Px(3)), tip, color, Kit.Px(1));
        draw.AddLine(tip - new float2(Kit.Px(4), -Kit.Px(3)), tip, color, Kit.Px(1));
        var headAt = tip.X - arrow - gap - head;
        Kit.Text(draw, font, size, new float2(headAt, at.Y), Theme.TextDim, Kit.F($"{before:0.##}"));
        Kit.ClippedText(draw, _ui.SansMedium, Theme.FontCaption, at, Theme.TextMute, name, Math.Max(0f, headAt - at.X - Kit.Px(Theme.Space2)));
        ImGui.Dummy(new float2(width, Kit.Px(18)));
    }

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
        DrawFillTank(editor, bridge, width);
        DrawCargoSection(editor, draft, content, bridge, width);
        DrawTractorDetails(editor, draft, width);

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

        DrawFuelOnApply(editor, width);
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
                if (editor.DumpsFuel)
                    Paragraph(ExhaustOrange, Kit.F($"Applying dumps {TotalDumped(editor):0.##} kg of fuel."), width);
                else
                    Paragraph(Theme.Good, "Passes every check. Ready to apply."u8, width);
                break;
            default:
                Paragraph(Theme.Bad, Kit.F($"{editor.CheckProblem}"), width);
                break;
        }

        ImGui.EndChild();
        EndEditorPanel();
    }

    /// <summary>The selection: one block with its resize controls, or a group with Merge; Copy, Paste and Delete.</summary>
    /// <summary>
    /// While a tank is the part to place: one button per fuel the craft has a tank for. Choosing one makes that
    /// fuel's tank type the part to place, and the palette keeps the choice for the next tank.
    /// </summary>
    private void DrawTankFuelChoice(ShipEditor editor, GameContent content, float width)
    {
        var types = content.Blocks.Types;
        if (!editor.Placing || !editor.CanEdit || (uint)editor.Part >= (uint)types.Count || !EditorPalette.IsTank(types[editor.Part]))
            return;
        Span<FuelKind> fuels = stackalloc FuelKind[4];
        var count = _palette.TankFuels(types, fuels);
        if (count == 0)
            return;
        Kit.Space(Theme.Space1);
        Kit.Caption("TANK FUEL"u8);
        Kit.Space(Theme.Space1);
        var gap = Kit.Px(Theme.Space1);
        var chip = MathF.Floor((width - gap * (count - 1)) / count);
        for (var i = 0; i < count; i++)
        {
            if (i > 0)
                ImGui.SameLine(0, gap);
            ImGui.PushID(i);
            if (EditorPalette.HandBack(EditorButton("##tankfuel"u8, ConsoleFuel.Name(fuels[i]), new float2(chip, Kit.Px(26)), true, false, types[editor.Part].Tank == fuels[i])))
            {
                _palette.TankFuel = fuels[i];
                editor.ChoosePart(_palette.TankTypeOf(types, fuels[i]));
            }

            ImGui.PopID();
        }
    }

    private void DrawEditorSelection(ShipEditor editor, BuildDraft draft, GameContent content, float width)
    {
        var boxes = draft.Boxes;
        var count = editor.Selection.Length;
        var mounts = draft.Mounts;
        var mountCount = editor.MountSelection.Length;
        Kit.Caption(count > 1 ? Kit.F($"SELECTED: {count} BLOCKS") : count == 0 && mountCount > 1 ? Kit.F($"SELECTED: {mountCount} MOUNTED PARTS") : "SELECTED"u8, SelectionBlue);
        Kit.Space(Theme.Space1);
        if (count == 0 && (uint)editor.SelectedMount < (uint)mounts.Length && (uint)mounts[editor.SelectedMount].Parent < (uint)boxes.Length)
        {
            // A mounted part: what it is, what it sits on and which way it pushes. It is deleted or picked to place
            // another; it has no size and no turn of its own.
            var mount = mounts[editor.SelectedMount];
            ClippedLine(_ui.SansMedium, Theme.FontSmall, Theme.Text, Kit.F($"{mount.Type.Name}"), width);
            ClippedLine(_ui.Mono, Theme.FontCaption, Theme.TextDim, Kit.F($"MOUNTED ON {boxes[mount.Parent].Type.Name}, {SideName((int)mount.Face)} FACE"), width);
            if (mount.Type.Laser is { } laser)
            {
                ClippedLine(_ui.Mono, Theme.FontCaption, Theme.Text, Kit.F($"RANGE {laser.Range:0} M   CUT {laser.CutRadius:0.##} M"), width);
                ClippedLine(_ui.Mono, Theme.FontCaption, Theme.TextDim, Kit.F($"{laser.CutRate:0.##} M/S   {laser.Power:0.##} MW   {mount.Type.Mass:0.###} T"), width);
            }
            else
            {
                ClippedLine(_ui.Mono, Theme.FontCaption, ExhaustOrange, FacingText(mount.Face), width);
                ClippedLine(_ui.Mono, Theme.FontCaption, Theme.TextDim, Kit.F($"{mount.Type.Thrust:0.#} KN   {mount.Type.FuelBurn:0.####} KG/S   {mount.Type.Mass:0.###} T"), width);
            }
            if (editor.IsMissing(mount.Parent))
                Paragraph(Theme.Bad, "The ship has lost the block this sits on. Repair rebuilds both."u8, width);
            DrawGroupSelector(editor, width);
            Kit.Space(Theme.Space2);
            if (EditorButton("##placemore"u8, "Place more of these"u8, new float2(width, Kit.Px(26)), editor.CanEdit))
                editor.CopySelection();
            Kit.Space(Theme.Space1);
            if (EditorButton("##deletemount"u8, mountCount > 1 ? Kit.F($"Delete {mountCount} parts") : "Delete part"u8, new float2(width, Kit.Px(28)), editor.CanDelete))
                editor.DeleteSelected();
            return;
        }

        if ((uint)editor.Selected >= (uint)boxes.Length)
        {
            var part = PartName(editor, content);
            Paragraph(Theme.TextMute, editor.RepairOnly
                ? "Repair the ship before you edit it. Click or middle click a block to select it; Shift adds."u8
                : editor.Placing
                ? Kit.F($"Click a face to place {part}, or drag a card onto the ship. Middle click a block to select it; Shift adds.")
                : "Click or middle click a block to select it; Shift adds or removes one."u8, width);
            DrawTankFuelChoice(editor, content, width);
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

    private int _cargoSize = 3, _cargoResource = -1;
    private void DrawCargoSection(ShipEditor editor, BuildDraft draft, GameContent content, PlayerInputBridge bridge, float width)
    {
        if (editor.Selection.Length != 1 || editor.Source is not { } source) return;
        if (draft.Boxes[editor.Selection[0]].Type.Cargo <= 0) return;

        Kit.Space(Theme.Space4);
        Kit.Caption("CARGO"u8, Theme.Accent);
        Kit.Space(Theme.Space1);
        StatRow("USED"u8, Kit.F($"{editor.CargoUsedUnits} / {source.Stats.CargoHold:0} U"), Theme.Text, width);
        StatRow("MASS"u8, Kit.F($"{editor.CargoKilograms / 1000.0:0.0} T"), Theme.Text, width);

        var cargo = editor.LiveCargo;
        if (cargo.Length > 0)
        {
            const int maxShown = 8;
            var shown = Math.Min(cargo.Length, maxShown);
            ImGui.Indent(Kit.Px(Theme.Space2));
            var subWidth = width - Kit.Px(Theme.Space2);
            for (var i = 0; i < shown; i++)
            {
                ref readonly var c = ref cargo[i];
                var sizeName = (uint)c.Size < (uint)content.Cargo.Sizes.Length ? content.Cargo.Sizes[c.Size].Name : "?";
                var resName = (uint)c.Resource < (uint)content.Galaxy.Resources.Length ? content.Galaxy.Resources[c.Resource].Name : "Empty";
                var label = Kit.F($"{sizeName} {resName}");
                var value = Kit.F($"{c.Amount / 1000.0:0.0} T");
                StatRow(label, value, Theme.TextDim, subWidth);
            }
            if (cargo.Length > shown)
                Paragraph(Theme.TextMute, Kit.F($"+{cargo.Length - shown} more"), subWidth);
            ImGui.Unindent(Kit.Px(Theme.Space2));
        }

        var rack = editor.CargoRack;
        if (rack == -1) return;

        Kit.Space(Theme.Space2);
        Kit.Caption("DEBUG"u8, ExhaustOrange);
        Kit.Space(Theme.Space1);

        var gap = Kit.Px(Theme.Space1);
        var sizes = content.Cargo.Sizes;
        _cargoSize = Math.Clamp(_cargoSize, 0, sizes.Length - 1);
        const int sizeCols = 4;
        var sizeChip = MathF.Floor((width - gap * (sizeCols - 1)) / sizeCols);
        for (var i = 0; i < sizes.Length; i++)
        {
            if (i % sizeCols > 0) ImGui.SameLine(0, gap);
            else if (i > 0) Kit.Space(Theme.Space1);
            ImGui.PushID(i);
            if (EditorButton("##cargosize"u8, Kit.F($"{sizes[i].Name}"), new float2(sizeChip, Kit.Px(24)), true, false, _cargoSize == i))
                _cargoSize = i;
            ImGui.PopID();
        }

        Kit.Space(Theme.Space1);
        var resources = content.Galaxy.Resources;
        var resTotal = resources.Length + 1;
        const int resCols = 3;
        var resChip = MathF.Floor((width - gap * (resCols - 1)) / resCols);
        for (var i = 0; i < resTotal; i++)
        {
            var resIndex = i - 1;
            if (i % resCols > 0) ImGui.SameLine(0, gap);
            else if (i > 0) Kit.Space(Theme.Space1);
            ImGui.PushID(100 + i);
            var name = resIndex < 0 ? "Empty"u8 : Kit.F($"{resources[resIndex].Name}");
            if (EditorButton("##cargores"u8, name, new float2(resChip, Kit.Px(24)), true, false, _cargoResource == resIndex))
                _cargoResource = resIndex;
            ImGui.PopID();
        }

        Kit.Space(Theme.Space1);
        if (EditorButton("##addcontainer"u8, "ADD CONTAINER (DEBUG)"u8, new float2(width, Kit.Px(28)), rack >= 0 && !editor.Server.Pending))
            editor.RequestContainer(bridge, _cargoSize, _cargoResource);
        if (rack == -2) Paragraph(Theme.TextMute, "Apply first: that rack is only in your draft."u8, width);
    }

    /// <summary>
    /// Debug: the Fill tank button, under the selection's own buttons. It asks the server to fill the selected tank
    /// blocks of the edited hull to capacity, and shows only where the server allows it and a tank is selected. A
    /// tank that is only in the unapplied draft says to apply first. To be removed with the debug fill.
    /// </summary>
    private void DrawFillTank(ShipEditor editor, PlayerInputBridge bridge, float width)
    {
        Span<int> blocks = stackalloc int[EditorRequestMessage.MaxFill];
        var offer = editor.FillTargets(blocks, out var count);
        if (offer == FillOffer.None)
            return;
        Kit.Space(Theme.Space2);
        var label = count > 1 ? Kit.F($"FILL {count} TANKS (DEBUG)") : "FILL TANK (DEBUG)"u8;
        if (EditorButton("##filltank"u8, label, new float2(width, Kit.Px(28)), offer == FillOffer.Ready))
            editor.RequestFill(bridge);
        if (offer == FillOffer.ApplyFirst)
            Paragraph(Theme.TextMute, "Apply first: that tank is only in your draft."u8, width);
        else if (offer == FillOffer.TooMany)
            Paragraph(Theme.TextMute, Kit.F($"Select at most {EditorRequestMessage.MaxFill} tanks."), width);
    }

    /// <summary>
    /// Repair, for a damaged ship: rebuilds it as built, and says why it cannot when it cannot. A repair restores
    /// blocks, never fuel.
    /// </summary>
    private void DrawRepair(ShipEditor editor, PlayerInputBridge bridge, float width)
    {
        var state = editor.RepairState;
        if (!editor.Damaged)
            return;

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
            RepairBlock.None => "Rebuilds every lost or worn block as the ship was built. Fuel, shield and battery charge stay as they are."u8,
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
        var missing = current ? DesignWarnings.MissingDirections(now) : 0;
        StatRow("BRAKING"u8, Kit.F($"{now.ReverseAcceleration:0.0} M/S2 (WAS {was.ReverseAcceleration:0.0})"), Tone((missing & 1 << (int)BlockFacing.Back) != 0), width);
        StatRow("SIDEWAYS"u8, Kit.F($"{now.Acceleration.X:0.0} M/S2 (WAS {was.Acceleration.X:0.0})"), Tone((missing & (1 << (int)BlockFacing.Right | 1 << (int)BlockFacing.Left)) != 0), width);
        StatRow("UP AND DOWN"u8, Kit.F($"{now.Acceleration.Y:0.0} M/S2 (WAS {was.Acceleration.Y:0.0})"), Tone((missing & (1 << (int)BlockFacing.Up | 1 << (int)BlockFacing.Down)) != 0), width);
        var noTank = Has(warnings, DesignWarning.NoTank);
        StatRow("NITROGEN"u8, Kit.F($"{now.FuelCapacity.Nitrogen:0} KG (WAS {was.FuelCapacity.Nitrogen:0})"), Tone(noTank && now.Burns(FuelKind.Nitrogen) && !(now.FuelCapacity.Nitrogen > 0f)), width);
        StatRow("HYDRAZINE"u8, Kit.F($"{now.FuelCapacity.Hydrazine:0} KG (WAS {was.FuelCapacity.Hydrazine:0})"), Tone(noTank && now.Burns(FuelKind.Hydrazine) && !(now.FuelCapacity.Hydrazine > 0f)), width);
        StatRow("BIPROPELLANT"u8, Kit.F($"{now.FuelCapacity.Bipropellant:0} KG (WAS {was.FuelCapacity.Bipropellant:0})"), Tone(noTank && now.Burns(FuelKind.Bipropellant) && !(now.FuelCapacity.Bipropellant > 0f)), width);
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

    /// <summary>
    /// Which ways the ship cannot be pushed, for the set <see cref="DesignWarnings.MissingDirections"/> gives: a part
    /// pushes away from the way it faces, so the cure is a part facing the other way.
    /// </summary>
    internal static ReadOnlySpan<byte> MissingText(int missing)
    {
        var back = (missing & 1 << (int)BlockFacing.Back) != 0;
        var side = (missing & (1 << (int)BlockFacing.Right | 1 << (int)BlockFacing.Left)) != 0;
        var vertical = (missing & (1 << (int)BlockFacing.Up | 1 << (int)BlockFacing.Down)) != 0;
        return (back, side, vertical) switch
        {
            (true, false, false) => "No thruster faces front, so nothing brakes: the ship cannot slow down or reverse."u8,
            (false, true, false) => "No thruster faces left or none faces right: the ship cannot move sideways both ways."u8,
            (false, false, true) => "No thruster faces up or none faces down: the ship cannot move up and down both ways."u8,
            (true, true, false) => "Nothing brakes and the ship cannot move sideways both ways: it needs thrusters facing front, left and right."u8,
            (true, false, true) => "Nothing brakes and the ship cannot move up and down both ways: it needs thrusters facing front, up and down."u8,
            (false, true, true) => "The ship cannot move sideways or up and down both ways: it needs thrusters facing left, right, up and down."u8,
            _ => "Nothing brakes and the ship cannot move sideways or up and down: it needs thrusters facing every way but back."u8,
        };
    }

    /// <summary>Where a thrust part to place points its nozzle, and so which way it pushes.</summary>
    internal static ReadOnlySpan<byte> FacingText(BlockFacing facing) => facing switch
    {
        BlockFacing.Right => "FACES RIGHT: PUSHES LEFT"u8,
        BlockFacing.Left => "FACES LEFT: PUSHES RIGHT"u8,
        BlockFacing.Up => "FACES UP: PUSHES DOWN"u8,
        BlockFacing.Down => "FACES DOWN: PUSHES UP"u8,
        BlockFacing.Back => "FACES BACK: PUSHES FORWARD"u8,
        _ => "FACES FRONT: BRAKES"u8,
    };

    private static float SlowestTurn(in FlightParams flight) => MathF.Min(flight.TurnRate.X, MathF.Min(flight.TurnRate.Y, flight.TurnRate.Z)) * 57.29578f;

    /// <summary>What a warning means for the ship, with its figures.</summary>
    private ReadOnlySpan<byte> WarningText(DesignWarning warning, in FlightParams f) => warning switch
    {
        DesignWarning.NoPower => "No generator: nothing makes power, so the battery only runs down."u8,
        DesignWarning.IdleOverload => Kit.F($"Generators make {f.PowerGenerated:0.##} MW, less than the {f.IdlePower:0.##} MW always drawn: the battery runs down even at rest."),
        DesignWarning.ThrustOverload => Kit.F($"Full normal thrust needs {f.ThrustPower:0.##} MW; generators supply {Math.Max(0f, f.PowerGenerated - f.IdlePower):0.##} MW after idle. The battery covers the shortfall. Empty battery: {100f * Math.Clamp((f.PowerGenerated - f.IdlePower) / Math.Max(f.ThrustPower, float.Epsilon), 0f, 1f):0}% normal thrust at full throttle. Boost draws {f.BoostPowerMultiplier:0.#} times as much."),
        DesignWarning.ShieldOverload => Kit.F($"A recharging shield draws {f.ShieldPower:0.##} MW ({f.ShieldRestartPower:0.##} MW to restart), more than the {Math.Max(0f, f.PowerGenerated - f.IdlePower):0.##} MW left after idle: it recharges off the battery."),
        DesignWarning.NoBattery => "No battery: a shield only recharges from stored energy, so this shield never recharges once hit. Boost needs a battery too."u8,
        DesignWarning.NoForward => "No engine or thruster faces back, so nothing pushes forward: the ship cannot move ahead."u8,
        DesignWarning.NoDirection => MissingText(DesignWarnings.MissingDirections(f)),
        DesignWarning.NoTank => HullDesign.MissingTank(f) switch
        {
            FuelKind.Nitrogen => "Cold gas thrusters burn nitrogen and the ship has no nitrogen tank: they never fire."u8,
            FuelKind.Hydrazine => "Hot thrusters burn hydrazine and the ship has no hydrazine tank: they never fire."u8,
            _ => "Engines burn bipropellant and the ship has no bipropellant tank: they never fire."u8,
        },
        DesignWarning.SlowForward => Kit.F($"Forward acceleration is only {f.Acceleration.Z:0.0} m/s2."),
        DesignWarning.SlowTop => Kit.F($"Top speed is only {f.MaxSpeed:0} m/s."),
        DesignWarning.NoTurn => "No gyro turns the ship on every axis: it cannot turn freely."u8,
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
        var withDesigns = EditorLayout.DesignButtonsFit(rect.Width, Kit.Scale);
        var buttons = EditorLayout.ButtonsWidth(Kit.Scale) + (withDesigns ? EditorLayout.DesignButtonsWidth(Kit.Scale) : 0f) - Kit.Px(EditorLayout.Pad);
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
        if (withDesigns)
        {
            var designs = EditorLayout.DesignButtonWidths;
            var diskReady = editor.CanEdit && _app.Designs.Available;
            if (EditorButton("##savedesign"u8, "Save..."u8, new float2(Kit.Px(designs[0]), height), diskReady))
                OpenSaveDesign(editor);
            ImGui.SameLine(0, gap);
            if (EditorButton("##loaddesign"u8, "Load..."u8, new float2(Kit.Px(designs[1]), height), diskReady))
                _designs.BeginLoad(_app.Designs, editor);
            ImGui.SameLine(0, gap);
        }

        if (EditorButton("##undo"u8, "Undo"u8, new float2(Kit.Px(widths[0]), height), editor.CanEdit && draft.CanUndo))
            editor.Undo();
        ImGui.SameLine(0, gap);
        if (EditorButton("##redo"u8, "Redo"u8, new float2(Kit.Px(widths[1]), height), editor.CanEdit && draft.CanRedo))
            editor.Redo();
        ImGui.SameLine(0, gap);
        if (EditorButton("##cancel"u8, "Cancel"u8, new float2(Kit.Px(widths[2]), height), true))
            editor.Cancel(bridge);
        ImGui.SameLine(0, gap);
        if (EditorButton("##apply"u8, editor.Stage == EditorStage.Applying ? "Applying"u8 : editor.ConfirmDump ? "Dump & apply?"u8 : "Apply"u8, new float2(Kit.Px(widths[3]), height), editor.CanApply, true))
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
            case DraftCheck.Ready when !editor.CargoFits:
                tone = Theme.Bad;
                return "Cargo would not fit. Keep occupied racks in place with enough room."u8;
            case DraftCheck.Ready when editor.ConfirmDump:
                tone = ExhaustOrange;
                return Kit.F($"Click again to dump {TotalDumped(editor):0.##} kg and apply.");
            case DraftCheck.Ready when editor.DumpsFuel:
                tone = ExhaustOrange;
                return Kit.F($"Applying dumps {TotalDumped(editor):0.##} kg of fuel.");
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

    internal static ReadOnlySpan<byte> AxisName(int axis) => axis switch { 0 => "X"u8, 1 => "Y"u8, _ => "Z"u8 };

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
        PartRole.Engine => "Main engine: pushes away from where it faces"u8,
        PartRole.Thruster => "Thruster: pushes away from where it faces"u8,
        PartRole.Tank => "Fuel tank"u8,
        PartRole.Gyro => "Gyro: turns the ship"u8,
        PartRole.Power => "Generator: makes power"u8,
        PartRole.Storage => "Battery: stores energy"u8,
        PartRole.Shield => "Shield emitter"u8,
        PartRole.Cargo => "Cargo space"u8,
        PartRole.Tractor => "Tractor beam: pulls containers into reachable racks"u8,
        PartRole.Laser => "Mining laser: cuts rock while its action group fires"u8,
        PartRole.Bay => "Drone bay: drones leave and dock through its hatch"u8,
        PartRole.Crew => "Crew quarters"u8,
        _ => "Structure and armour"u8,
    };

    /// <summary>The name of the figure a role's merge preview compares.</summary>
    private static ReadOnlySpan<byte> MainLabel(PartRole role) => role switch
    {
        PartRole.Engine or PartRole.Thruster => "THRUST KN"u8,
        PartRole.Tank => "HOLDS KG"u8,
        PartRole.Gyro => "TORQUE KNM"u8,
        PartRole.Power => "MAKES MW"u8,
        PartRole.Storage => "STORES MJ"u8,
        PartRole.Shield => "SHIELD"u8,
        PartRole.Cargo => "CARGO u"u8,
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
        DraftEdit.OffFace => "It does not fit there: a thruster mounts on a whole flat face."u8,
        DraftEdit.TooManyMounts => "The ship has as many mounted parts as a design may have."u8,
        DraftEdit.WrongKind => "That part mounts on a face; it is not a block."u8,
        DraftEdit.TooManyTanks => "The ship has as many tanks as a design may have. Make a tank larger instead."u8,
        DraftEdit.BoxOnly => "That part is never slanted: tanks, sensors and scanners are boxes only."u8,
        DraftEdit.WrongCraft => "That part is for the other kind of craft: drone parts fit only the drone."u8,
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
        EditorReason.CargoFull => "Cargo would not fit. Keep occupied racks in place with enough room."u8,
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
