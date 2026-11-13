using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Prospect.Game.World;

/// <summary>What became of one edit to a <see cref="BuildDraft"/>. Anything but <see cref="Done"/> left the draft as it was.</summary>
public enum DraftEdit : byte
{
    /// <summary>The draft changed and the edit can be undone.</summary>
    Done,
    /// <summary>The edit would change nothing; nothing was recorded.</summary>
    Unchanged,
    /// <summary>No block has that index.</summary>
    NoSuchBlock,
    /// <summary>A side is below one cell or above the content's largest box side.</summary>
    BadSize,
    /// <summary>The box, or the design with it, would reach past the content's coordinate or extent limits.</summary>
    OutsideLimits,
    /// <summary>The box would share volume with another block. Touching faces are allowed.</summary>
    Overlaps,
    /// <summary>One more block would exceed the content's block count or the geometry admission count.</summary>
    TooManyBlocks,
    /// <summary>The selection names a block twice.</summary>
    BadSelection,
    /// <summary>The blocks to merge are not all of one type.</summary>
    MixedTypes,
    /// <summary>The blocks to merge do not fill one box exactly: there is a gap, or they are not in a block.</summary>
    NotABox,
    /// <summary>The blocks would give the ship FTL drives of two kinds; a ship takes drives of one kind only.</summary>
    MixedDrives,
}

/// <summary>
/// The result of <see cref="BuildDraft.Export"/>: an accepted design and its canonical bytes, or why there is none.
/// </summary>
public readonly record struct DraftExport(HullDesign? Design, byte[]? Bytes, string? Problem)
{
    /// <summary>True when the draft passed the same checks a server applies to a received design.</summary>
    public bool Ok => Design is not null;

    /// <summary>
    /// What the draft's shield surface can need against the room there is, also for a draft refused for needing
    /// too much; null when the draft was refused before it could be measured (its boxes, or too many of them).
    /// </summary>
    public SurfaceUsage? Surface { get; init; }
}

/// <summary>
/// An editable copy of a hull's boxes, for the ship editor. Edits are checked against the content limits
/// and other boxes and are undoable; reads cost nothing. Nothing here is live: <see cref="Export"/> turns the draft
/// into a new immutable design through the same validation <see cref="DesignBytes.Decode"/> gives a received one.
/// </summary>
/// <remarks>
/// Block indices are compact: deleting a block moves later ones down by one, and the exported design numbers its
/// blocks in draft order. An intermediate draft may have no core while one is swapped; only <see cref="Export"/>
/// refuses that. Sides are numbered as <see cref="HullDesign.ExteriorFaces"/> numbers faces:
/// <c>2 * axis + (negative ? 1 : 0)</c>, so +x, -x, +y, -y, +z, -z.
/// <para>
/// An edit of several blocks (<see cref="Paste"/>, <see cref="Delete(ReadOnlySpan{int})"/>, <see cref="Move"/>,
/// <see cref="Merge"/>) is checked whole, changes everything or nothing, and is one step to undo and redo. A
/// selection is a list of block indices in any order; each must name a block, and none twice. The boxes of a
/// draft never share volume, which every edit keeps and the group edits rely on.
/// </para>
/// </remarks>
public sealed class BuildDraft
{
    private readonly List<DesignBox> _boxes;
    private readonly GameContent _content;
    private readonly List<Step[]> _undo = [];
    private readonly List<Step[]> _redo = [];
    private readonly int _historyLimit;
    private int _cores;

    // How many drives of each FtlKind the draft has, so a placement can be checked against them without a pass.
    private readonly int[] _drives = new int[4];

    // The selection of the group edit being checked, as a set. Kept so that a preview every frame takes no memory.
    private ulong[] _marks = [];

    // One recorded change; an edit is the changes it made, in the order it made them. An add has no Before and
    // a delete has no After (the box's Type is null).
    private readonly record struct Step(int Index, DesignBox Before, DesignBox After);

    private BuildDraft(HullDesign design, List<DesignBox> boxes, GameContent content, int historyLimit)
    {
        Name = design.Name; Class = design.Class; BlockSize = design.BlockSize; Color = design.Color;
        _boxes = boxes; _content = content; _historyLimit = historyLimit;
        foreach (var box in boxes)
        {
            if (box.Type.Core) _cores++;
            _drives[(int)box.Type.Ftl]++;
        }
    }

    /// <summary>
    /// A draft of the blocks of <paramref name="design"/> that are in <paramref name="alive"/>, in design order,
    /// with its name, class, block size and colour. Keeps at most <paramref name="historyLimit"/> undo steps.
    /// </summary>
    public static BuildDraft From(HullDesign design, ReadOnlySpan<ulong> alive, GameContent content, int historyLimit = 256)
    {
        if (alive.Length < BlockSet.WordCount(design.BlockCount)) throw new ArgumentException("The set is shorter than the design.", nameof(alive));
        ArgumentOutOfRangeException.ThrowIfLessThan(historyLimit, 1);
        var boxes = new List<DesignBox>(design.BlockCount);
        for (var i = 0; i < design.BlockCount; i++)
            if (BlockSet.Has(alive, i)) boxes.Add(design.Boxes[i]);
        return new BuildDraft(design, boxes, content, historyLimit);
    }

    /// <summary>
    /// A draft of every block of <paramref name="design"/> as it was built, whatever the hull has lost since:
    /// the blueprint. Block indices are the design's own, so a hull's set of surviving blocks says which of the
    /// draft's boxes are missing from the ship.
    /// </summary>
    public static BuildDraft FromBlueprint(HullDesign design, GameContent content, int historyLimit = 256) =>
        From(design, design.FullSet, content, historyLimit);

    /// <summary>Design display name, kept from the source design.</summary>
    public string Name { get; }
    /// <summary>Design category, kept from the source design.</summary>
    public HullClass Class { get; }
    /// <summary>Metres per grid cell, kept from the source design.</summary>
    public float BlockSize { get; }
    /// <summary>Design colour, kept from the source design.</summary>
    public Vector3 Color { get; }

    /// <summary>The boxes in draft order. Valid until the next edit; read it again after one.</summary>
    public ReadOnlySpan<DesignBox> Boxes => CollectionsMarshal.AsSpan(_boxes);
    /// <summary>Rises on every change, undo and redo included, so a view rebuilds only when it differs.</summary>
    public int Version { get; private set; }
    /// <summary>Whether a core is left. Without one the draft cannot be exported.</summary>
    public bool HasCore => _cores > 0;
    /// <summary>Whether there is an edit to undo.</summary>
    public bool CanUndo => _undo.Count > 0;
    /// <summary>Whether there is an undone edit to redo.</summary>
    public bool CanRedo => _redo.Count > 0;

    /// <summary>
    /// Where a box of <paramref name="size"/> sits flush against side <paramref name="side"/> of block
    /// <paramref name="target"/>. Across the face it is centred on the target, rounding towards negative, or on the
    /// cell <paramref name="near"/> names when given, and always kept touching the face by at least one cell.
    /// The result is only a position: <see cref="Add"/> still checks it.
    /// </summary>
    public GridSize Snap(int target, int side, GridSize size, GridSize? near = null)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)target, (uint)_boxes.Count, nameof(target));
        ArgumentOutOfRangeException.ThrowIfGreaterThan((uint)side, 5u, nameof(side));
        if (size.X < 1 || size.Y < 1 || size.Z < 1) throw new ArgumentOutOfRangeException(nameof(size));
        var t = _boxes[target];
        var normal = side >> 1;
        Span<int> at = stackalloc int[3];
        for (var axis = 0; axis < 3; axis++)
        {
            int low = Of(t.At, axis), length = Of(t.Size, axis), own = Of(size, axis);
            if (axis == normal) { at[axis] = (side & 1) == 0 ? low + length : low - own; continue; }
            // An arithmetic shift halves towards negative infinity, so odd differences round the same way at any sign.
            var wanted = near is { } n ? Of(n, axis) - ((own - 1) >> 1) : low + ((length - own) >> 1);
            at[axis] = Math.Clamp(wanted, low - own + 1, low + length - 1);
        }
        return new GridSize(at[0], at[1], at[2]);
    }

    /// <summary>Adds a box at the end of the draft.</summary>
    public DraftEdit Add(GridSize at, GridSize size, BlockType type, BlockShape shape = BlockShape.Box)
    {
        ArgumentNullException.ThrowIfNull(type);
        var box = new DesignBox(at, size, type, shape);
        if (!BlockShapes.IsValid(shape)) throw new ArgumentOutOfRangeException(nameof(shape));
        var check = CanAdd(at, size, type);
        if (check != DraftEdit.Done) return check;
        Record([new Step(_boxes.Count, default, box)]);
        return DraftEdit.Done;
    }

    /// <summary>
    /// What <see cref="Add"/> would answer for a box here, without changing the draft, its version or its history:
    /// for showing whether a placement fits before it is made. With a <paramref name="type"/>, also whether its
    /// drive is of the draft's kind. One pass over the boxes; allocates nothing.
    /// </summary>
    public DraftEdit CanAdd(GridSize at, GridSize size, BlockType? type = null)
    {
        // The same count bound GeometryAdmission.Count applies, checked here so the editor stops at it; Export runs the real one.
        if (!CountFits(_boxes.Count + 1L))
            return DraftEdit.TooManyBlocks;
        var check = Check(new DesignBox(at, size, null!), -1);
        if (check != DraftEdit.Done || type is null)
            return check;
        var box = new DesignBox(at, size, type);
        return MixesDrives(new ReadOnlySpan<DesignBox>(in box)) ? DraftEdit.MixedDrives : DraftEdit.Done;
    }

    /// <summary>
    /// Adds copies of <paramref name="boxes"/>, each moved by <paramref name="by"/> cells, at the end of the
    /// draft in their order: the pasted blocks are the last <c>boxes.Length</c>. All of them or none.
    /// </summary>
    public DraftEdit Paste(ReadOnlySpan<DesignBox> boxes, GridSize by)
    {
        foreach (ref readonly var box in boxes) ArgumentNullException.ThrowIfNull(box.Type, nameof(boxes));
        var check = CanPaste(boxes, by);
        if (check != DraftEdit.Done) return check;
        var steps = new Step[boxes.Length];
        for (var i = 0; i < steps.Length; i++)
            steps[i] = new Step(_boxes.Count + i, default, boxes[i] with { At = Shifted(boxes[i].At, by) });
        Record(steps);
        return DraftEdit.Done;
    }

    /// <summary>
    /// What <see cref="Paste"/> would answer, without changing the draft, its version or its history. The boxes
    /// are checked against the draft and against each other; of their types only the kind of drive is looked at.
    /// Allocates nothing.
    /// </summary>
    public DraftEdit CanPaste(ReadOnlySpan<DesignBox> boxes, GridSize by)
    {
        if (boxes.IsEmpty) return DraftEdit.Unchanged;
        if (!CountFits((long)_boxes.Count + boxes.Length)) return DraftEdit.TooManyBlocks;
        var check = CheckGroup(boxes, default, by, default);
        return check == DraftEdit.Done && MixesDrives(boxes) ? DraftEdit.MixedDrives : check;
    }

    /// <summary>Removes a block; later blocks move down one index.</summary>
    public DraftEdit Delete(int block)
    {
        if ((uint)block >= (uint)_boxes.Count) return DraftEdit.NoSuchBlock;
        Record([new Step(block, _boxes[block], default)]);
        return DraftEdit.Done;
    }

    /// <summary>Removes the blocks of a selection; the blocks left move down and keep their order.</summary>
    public DraftEdit Delete(ReadOnlySpan<int> blocks)
    {
        var check = Mark(blocks, out var marks);
        if (check != DraftEdit.Done) return check;
        if (blocks.IsEmpty) return DraftEdit.Unchanged;

        // From the last block down, so that no removal moves a block that is still to go.
        var steps = new Step[blocks.Length];
        var next = 0;
        for (var i = _boxes.Count - 1; i >= 0; i--)
            if (BlockSet.Has(marks, i)) steps[next++] = new Step(i, _boxes[i], default);
        Record(steps);
        return DraftEdit.Done;
    }

    /// <summary>
    /// Moves the blocks of a selection by <paramref name="by"/> cells, together. They keep their indices, so the
    /// selection still names them afterwards. They may land where they or each other stood, on no other block.
    /// </summary>
    public DraftEdit Move(ReadOnlySpan<int> blocks, GridSize by)
    {
        var check = CanMove(blocks, by);
        if (check != DraftEdit.Done) return check;
        var steps = new Step[blocks.Length];
        for (var i = 0; i < steps.Length; i++)
        {
            var box = _boxes[blocks[i]];
            steps[i] = new Step(blocks[i], box, box with { At = Shifted(box.At, by) });
        }

        Record(steps);
        return DraftEdit.Done;
    }

    /// <summary>
    /// What <see cref="Move"/> would answer, without changing the draft, its version or its history: for showing
    /// whether a dragged group fits where it is held. Allocates nothing once the draft has checked a selection
    /// of its size.
    /// </summary>
    public DraftEdit CanMove(ReadOnlySpan<int> blocks, GridSize by)
    {
        var check = Mark(blocks, out var marks);
        if (check != DraftEdit.Done) return check;
        if (blocks.IsEmpty || by == default) return DraftEdit.Unchanged;
        return CheckGroup(Boxes, blocks, by, marks);
    }

    /// <summary>
    /// Makes one box of the blocks of a selection: all of one type, and together filling the box around them
    /// with no gap. The merged block takes the place of the selection's lowest index and the others are
    /// removed. A type's figures may scale with a block's size, so the ship's figures can change.
    /// </summary>
    public DraftEdit Merge(ReadOnlySpan<int> blocks)
    {
        var check = CanMerge(blocks, out var merged);
        if (check != DraftEdit.Done) return check;

        // CanMerge left the selection marked. The others go from the last down; the lowest then still has its index.
        ReadOnlySpan<ulong> marks = _marks;
        var steps = new Step[blocks.Length];
        var next = 0;
        var lowest = 0;
        while (!BlockSet.Has(marks, lowest)) lowest++;
        for (var i = _boxes.Count - 1; i > lowest; i--)
            if (BlockSet.Has(marks, i)) steps[next++] = new Step(i, _boxes[i], default);
        steps[next] = new Step(lowest, _boxes[lowest], merged);
        Record(steps);
        return DraftEdit.Done;
    }

    /// <summary>
    /// What <see cref="Merge"/> would answer and the box it would make, without changing the draft, its version
    /// or its history. Looks at the selected blocks only, and at no cell: boxes of a draft share no volume, so
    /// they fill the box around them exactly when their volumes add up to its volume.
    /// </summary>
    public DraftEdit CanMerge(ReadOnlySpan<int> blocks, out DesignBox merged)
    {
        merged = default;
        var check = Mark(blocks, out _);
        if (check != DraftEdit.Done) return check;
        if (blocks.Length < 2) return DraftEdit.Unchanged;

        var first = _boxes[blocks[0]];
        Span<int> low = [first.At.X, first.At.Y, first.At.Z];
        Span<int> high = [first.At.X + first.Size.X, first.At.Y + first.Size.Y, first.At.Z + first.Size.Z];
        long volume = 0;
        foreach (var block in blocks)
        {
            var box = _boxes[block];
            if (box.Shape != BlockShape.Box) return DraftEdit.NotABox;
            if (box.Type != first.Type) return DraftEdit.MixedTypes;
            volume += (long)box.Size.X * box.Size.Y * box.Size.Z;
            for (var axis = 0; axis < 3; axis++)
            {
                low[axis] = Math.Min(low[axis], Of(box.At, axis));
                high[axis] = Math.Max(high[axis], Of(box.At, axis) + Of(box.Size, axis));
            }
        }

        var size = new GridSize(high[0] - low[0], high[1] - low[1], high[2] - low[2]);
        if (volume != (long)size.X * size.Y * size.Z) return DraftEdit.NotABox;
        var limit = _content.Blocks.Limits.MaxBoxSide;
        if (size.X > limit || size.Y > limit || size.Z > limit) return DraftEdit.BadSize;
        merged = first with { At = new GridSize(low[0], low[1], low[2]), Size = size };
        return DraftEdit.Done;
    }

    /// <summary>
    /// Sets a block's whole-cell length along the axis of <paramref name="side"/>, moving that side and keeping the
    /// opposite one where it is.
    /// </summary>
    public DraftEdit Resize(int block, int side, int length)
    {
        var check = CanResize(block, side, length, out var changed);
        if (check != DraftEdit.Done) return check;
        Record([new Step(block, _boxes[block], changed)]);
        return DraftEdit.Done;
    }

    /// <summary>What <see cref="Resize"/> would answer, without changing the draft, its version or its history.</summary>
    public DraftEdit CanResize(int block, int side, int length) => CanResize(block, side, length, out _);

    private DraftEdit CanResize(int block, int side, int length, out DesignBox changed)
    {
        changed = default;
        if ((uint)block >= (uint)_boxes.Count) return DraftEdit.NoSuchBlock;
        ArgumentOutOfRangeException.ThrowIfGreaterThan((uint)side, 5u, nameof(side));
        var box = _boxes[block];
        var axis = side >> 1;
        var at = (side & 1) == 0 ? box.At : With(box.At, axis, Of(box.At, axis) + Of(box.Size, axis) - length);
        changed = box with { At = at, Size = With(box.Size, axis, length) };
        if (changed == box) return DraftEdit.Unchanged;
        return Check(changed, block);
    }

    /// <summary>Rotates one block by a positive quarter turn, keeping its low corner fixed; one undo step.</summary>
    public DraftEdit Rotate(int block, int axis)
    {
        var check = CanRotate(block, axis, out var changed);
        if (check == DraftEdit.Done) Record([new Step(block, _boxes[block], changed)]);
        return check;
    }

    /// <summary>What <see cref="Rotate"/> would answer, without changing anything.</summary>
    public DraftEdit CanRotate(int block, int axis) => CanRotate(block, axis, out _);

    private DraftEdit CanRotate(int block, int axis, out DesignBox changed)
    {
        changed = default;
        if ((uint)block >= (uint)_boxes.Count) return DraftEdit.NoSuchBlock;
        ArgumentOutOfRangeException.ThrowIfGreaterThan((uint)axis, 2u);
        var box = _boxes[block]; var u = (axis + 1) % 3; var v = (axis + 2) % 3;
        var size = With(With(box.Size, u, Of(box.Size, v)), v, Of(box.Size, u));
        changed = box with { Size = size, Shape = BlockShapes.Rotate(box.Shape, axis) };
        if (changed == box) return DraftEdit.Unchanged;
        return Check(changed, block);
    }

    /// <summary>Takes back the latest edit. False when there is none.</summary>
    public bool Undo()
    {
        if (_undo.Count == 0) return false;
        var steps = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        for (var i = steps.Length - 1; i >= 0; i--) Apply(steps[i].Index, steps[i].After, steps[i].Before);
        _redo.Add(steps);
        return true;
    }

    /// <summary>Makes the latest undone edit again. False when there is none.</summary>
    public bool Redo()
    {
        if (_redo.Count == 0) return false;
        var steps = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        foreach (var step in steps) Apply(step.Index, step.Before, step.After);
        _undo.Add(steps);
        return true;
    }

    /// <summary>
    /// Builds the draft into a new design and runs every check a server runs on a received design: content limits,
    /// overlap, a core, geometry admission, the transfer size and finite figures. A design need not be able to
    /// fly, turn or power itself: what it can do is in the exported design's <see cref="HullDesign.Flight"/>,
    /// for the editor to warn about. Allocates and costs about as much as receiving the design, so call it on
    /// Apply or on a worker, not every frame.
    /// </summary>
    public DraftExport Export() => Export(Name, Class, BlockSize, Color, _boxes.ToArray(), _content);

    /// <summary>
    /// A frozen copy of the draft as it is now, numbered with its <see cref="Version"/>. Another thread may export
    /// the copy while this draft goes on being edited; the copy never changes. Allocates the box array once.
    /// </summary>
    public DraftCopy Copy() => new(Name, Class, BlockSize, Color, _boxes.ToArray(), _content, Version);

    internal static DraftExport Export(string name, HullClass hullClass, float blockSize, Vector3 color, DesignBox[] boxes, GameContent content)
    {
        SurfaceUsage? surface = null;
        try
        {
            var limits = content.Blocks.Limits;
            GeometryAdmission.Count(boxes.Length, limits);
            BoxDesign.RequireOneDriveKind(boxes, "draft");
            var built = HullDesign.Of(new BoxDesign(name, blockSize, boxes, limits, "draft", hullClass, color), content.Rules);

            // Measured here as well as in the decoding, so that a draft refused for its surface still says how far over it is.
            surface = GeometryAdmission.Measure(built, limits.SurfaceVertices, limits.Shield, limits.SurfaceIndices);
            var bytes = DesignBytes.Encode(built);
            return new DraftExport(DesignBytes.Decode(bytes, SHA256.HashData(bytes), content), bytes, null) { Surface = surface };
        }
        catch (InvalidDataException e) { return new DraftExport(null, null, e.Message) { Surface = surface }; }
    }

    // Whether adding these boxes would give the draft drives of two kinds, as BoxDesign.RequireOneDriveKind refuses.
    private bool MixesDrives(ReadOnlySpan<DesignBox> adding)
    {
        var kind = FtlKind.None;
        for (var k = 1; k < _drives.Length && kind == FtlKind.None; k++)
            if (_drives[k] > 0) kind = (FtlKind)k;
        foreach (ref readonly var box in adding)
        {
            var next = box.Type.Ftl;
            if (next == FtlKind.None) continue;
            if (kind == FtlKind.None) kind = next;
            else if (next != kind) return true;
        }

        return false;
    }

    // The same count bound GeometryAdmission.Count applies, checked here so the editor stops at it; Export runs the real one.
    private bool CountFits(long count)
    {
        var limits = _content.Blocks.Limits;
        return count <= limits.MaxBlocks && count * 36 <= limits.SurfaceVertices && 6 * count * count <= limits.GeometryPairTests;
    }

    /// <summary>
    /// Marks a selection in <see cref="_marks"/>, which is cleared first. <see cref="DraftEdit.NoSuchBlock"/> for
    /// an index the draft has no block for and <see cref="DraftEdit.BadSelection"/> for one named twice.
    /// </summary>
    private DraftEdit Mark(ReadOnlySpan<int> blocks, out ReadOnlySpan<ulong> marks)
    {
        var words = BlockSet.WordCount(_boxes.Count);
        if (_marks.Length < words) _marks = new ulong[words];
        Array.Clear(_marks);
        marks = _marks;
        foreach (var block in blocks)
        {
            if ((uint)block >= (uint)_boxes.Count) return DraftEdit.NoSuchBlock;
            if (BlockSet.Has(_marks, block)) return DraftEdit.BadSelection;
            BlockSet.Add(_marks, block);
        }

        return DraftEdit.Done;
    }

    /// <summary>
    /// Checks a group of boxes placed together: the boxes of <paramref name="source"/> that <paramref name="pick"/>
    /// names, or all of them when it names none, each moved by <paramref name="by"/>. They are checked as
    /// <see cref="Check"/> checks one box, against every box of the draft that is not in <paramref name="moving"/>
    /// and, for boxes that come from outside, against each other; a group that is moved keeps its shape and
    /// cannot run into itself. Boxes far from the group are passed over by the group's own bounds.
    /// </summary>
    private DraftEdit CheckGroup(ReadOnlySpan<DesignBox> source, ReadOnlySpan<int> pick, GridSize by, ReadOnlySpan<ulong> moving)
    {
        var limits = _content.Blocks.Limits;
        var count = pick.IsEmpty ? source.Length : pick.Length;
        Span<int> low = [int.MaxValue, int.MaxValue, int.MaxValue];
        Span<int> high = [int.MinValue, int.MinValue, int.MinValue];
        for (var i = 0; i < count; i++)
        {
            ref readonly var box = ref source[pick.IsEmpty ? i : pick[i]];
            if (!BlockShapes.IsValid(box.Shape)) throw new ArgumentOutOfRangeException(nameof(source));
            for (var axis = 0; axis < 3; axis++)
            {
                // In 64 bits: a move far enough to wrap around would otherwise look like one that fits.
                long at = (long)Of(box.At, axis) + Of(by, axis), size = Of(box.Size, axis);
                if (size < 1 || size > limits.MaxBoxSide) return DraftEdit.BadSize;
                if (at < -limits.MaxDesignSide || at + size > limits.MaxDesignSide) return DraftEdit.OutsideLimits;
                low[axis] = Math.Min(low[axis], (int)at); high[axis] = Math.Max(high[axis], (int)(at + size));
            }
        }

        Span<int> unionLow = [low[0], low[1], low[2]];
        Span<int> unionHigh = [high[0], high[1], high[2]];
        var overlaps = false;
        for (var j = 0; j < _boxes.Count; j++)
        {
            if (!moving.IsEmpty && BlockSet.Has(moving, j)) continue;
            var other = _boxes[j];
            var near = true;
            for (var axis = 0; axis < 3; axis++)
            {
                int a = Of(other.At, axis), b = a + Of(other.Size, axis);
                near &= a < high[axis] && b > low[axis];
                unionLow[axis] = Math.Min(unionLow[axis], a); unionHigh[axis] = Math.Max(unionHigh[axis], b);
            }

            for (var i = 0; near && !overlaps && i < count; i++)
                overlaps = Shares(source[pick.IsEmpty ? i : pick[i]], by, other, default);
        }

        if (unionHigh[0] - unionLow[0] > limits.MaxDesignSide || unionHigh[1] - unionLow[1] > limits.MaxDesignSide || unionHigh[2] - unionLow[2] > limits.MaxDesignSide)
            return DraftEdit.OutsideLimits;
        for (var i = 0; pick.IsEmpty && !overlaps && i < count; i++)
            for (var k = i + 1; !overlaps && k < count; k++)
                overlaps = Shares(source[i], by, source[k], by);
        return overlaps ? DraftEdit.Overlaps : DraftEdit.Done;
    }

    // Whether two boxes, each moved by its own shift, share volume. Touching faces do not.
    private static bool Shares(in DesignBox a, GridSize shiftA, in DesignBox b, GridSize shiftB)
    {
        for (var axis = 0; axis < 3; axis++)
        {
            int la = Of(a.At, axis) + Of(shiftA, axis), lb = Of(b.At, axis) + Of(shiftB, axis);
            if (la >= lb + Of(b.Size, axis) || lb >= la + Of(a.Size, axis)) return false;
        }

        return true;
    }

    private static GridSize Shifted(GridSize at, GridSize by) => new(at.X + by.X, at.Y + by.Y, at.Z + by.Z);

    private DraftEdit Check(DesignBox box, int ignore)
    {
        var limits = _content.Blocks.Limits;
        // Cell i spans [i - 0.5, i + 0.5) about its centre (BoxDesign.Low/High/Mid); every box carries the same half
        // cell, so overlap, touching and extent compare exactly on the integer edges [At, At + Size).
        Span<int> low = stackalloc int[3];
        Span<int> high = stackalloc int[3];
        Span<int> unionLow = stackalloc int[3];
        Span<int> unionHigh = stackalloc int[3];
        for (var axis = 0; axis < 3; axis++)
        {
            int at = Of(box.At, axis), size = Of(box.Size, axis);
            if (size < 1 || size > limits.MaxBoxSide) return DraftEdit.BadSize;
            if (at < -limits.MaxDesignSide || (long)at + size > limits.MaxDesignSide) return DraftEdit.OutsideLimits;
            low[axis] = unionLow[axis] = at; high[axis] = unionHigh[axis] = at + size;
        }
        var overlaps = false;
        for (var i = 0; i < _boxes.Count; i++)
        {
            if (i == ignore) continue;
            var other = _boxes[i];
            var shared = 0;
            for (var axis = 0; axis < 3; axis++)
            {
                int a = Of(other.At, axis), b = a + Of(other.Size, axis);
                if (a < high[axis] && b > low[axis]) shared++;
                unionLow[axis] = Math.Min(unionLow[axis], a); unionHigh[axis] = Math.Max(unionHigh[axis], b);
            }
            overlaps |= shared == 3;
        }
        low = unionLow; high = unionHigh;
        if (high[0] - low[0] > limits.MaxDesignSide || high[1] - low[1] > limits.MaxDesignSide || high[2] - low[2] > limits.MaxDesignSide)
            return DraftEdit.OutsideLimits;
        return overlaps ? DraftEdit.Overlaps : DraftEdit.Done;
    }

    // Makes an edit and remembers it as one step of history, however many changes it is.
    private void Record(Step[] steps)
    {
        foreach (var step in steps) Apply(step.Index, step.Before, step.After);
        _redo.Clear();
        if (_undo.Count == _historyLimit) _undo.RemoveAt(0);
        _undo.Add(steps);
    }

    // Replaces `from` at `index` with `to`; a box without a type means no box, so this inserts or removes.
    private void Apply(int index, DesignBox from, DesignBox to)
    {
        if (from.Type is null) _boxes.Insert(index, to);
        else if (to.Type is null) _boxes.RemoveAt(index);
        else _boxes[index] = to;
        if (from.Type?.Core == true) _cores--;
        if (to.Type?.Core == true) _cores++;
        if (from.Type is { } removed) _drives[(int)removed.Ftl]--;
        if (to.Type is { } added) _drives[(int)added.Ftl]++;
        Version++;
    }

    private static int Of(GridSize g, int axis) => axis switch { 0 => g.X, 1 => g.Y, _ => g.Z };
    private static GridSize With(GridSize g, int axis, int value) => axis switch { 0 => g with { X = value }, 1 => g with { Y = value }, _ => g with { Z = value } };
}

/// <summary>
/// A <see cref="BuildDraft"/> frozen at one <see cref="Version"/>, for checking on a worker while the draft is
/// edited on. Nothing in it changes after it is made, so any thread may read it.
/// </summary>
public sealed class DraftCopy
{
    private readonly string _name;
    private readonly HullClass _class;
    private readonly float _blockSize;
    private readonly Vector3 _color;
    private readonly DesignBox[] _boxes;
    private readonly GameContent _content;

    internal DraftCopy(string name, HullClass hullClass, float blockSize, Vector3 color, DesignBox[] boxes, GameContent content, int version)
    {
        _name = name; _class = hullClass; _blockSize = blockSize; _color = color; _boxes = boxes; _content = content; Version = version;
    }

    /// <summary>The draft's <see cref="BuildDraft.Version"/> when the copy was made.</summary>
    public int Version { get; }

    /// <summary>How many boxes the copy holds.</summary>
    public int BlockCount => _boxes.Length;

    /// <summary>As <see cref="BuildDraft.Export"/>, for the draft as it was when copied. Safe on any thread.</summary>
    public DraftExport Export() => BuildDraft.Export(_name, _class, _blockSize, _color, _boxes, _content);
}
