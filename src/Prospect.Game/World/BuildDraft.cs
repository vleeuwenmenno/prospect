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
    /// <summary>A side is below one cell, above the content's largest box side or above the largest side of the block's type.</summary>
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
    /// <summary>A mounted part does not fit on that face: the face is no whole side of the block, or the part would hang over its edge.</summary>
    OffFace,
    /// <summary>One more mounted part would exceed the content's count.</summary>
    TooManyMounts,
    /// <summary>The part mounts on a face and cannot be placed as a block, or is a block and cannot be mounted.</summary>
    WrongKind,
    /// <summary>One more tank would exceed the content's count: a hull's fuel is kept tank by tank.</summary>
    TooManyTanks,
    /// <summary>The part is never slanted (<see cref="BlockType.BoxOnly"/>): it is placed, pasted and turned as a box only.</summary>
    BoxOnly,
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
/// <para>
/// Parts mounted on block faces (<see cref="Mounts"/>) are kept apart from the boxes and name their block by its
/// index, which every edit keeps right. They belong to their block: they move with it and go with it. The
/// mounts of a draft always have room, and an edit keeps that by taking away, in the same step, every mount it
/// leaves without: one whose face is now covered, whose block was turned, or that no longer fits a resized face.
/// Undo brings them back with the rest of the step.
/// </para>
/// </remarks>
public sealed class BuildDraft
{
    private readonly List<DesignBox> _boxes;
    private readonly List<DesignMount> _mounts;
    private readonly List<Step> _pending = [];
    private readonly GameContent _content;
    private readonly List<Step[]> _undo = [];
    private readonly List<Step[]> _redo = [];
    private readonly int _historyLimit;
    private int _cores;

    // How many drives of each FtlKind the draft has, so a placement can be checked against them without a pass.
    private readonly int[] _drives = new int[4];

    // The selection of the group edit being checked, as a set. Kept so that a preview every frame takes no memory.
    private ulong[] _marks = [];

    // One recorded change, to a box or to a mount; an edit is the changes it made, in the order it made them. An
    // add has no Before and a delete has no After (its Type is null).
    private readonly record struct Step(int Index, DesignBox Before, DesignBox After, DesignMount MountBefore, DesignMount MountAfter, bool Mount)
    {
        public static Step Block(int index, DesignBox before, DesignBox after) => new(index, before, after, default, default, false);
        public static Step Part(int index, DesignMount before, DesignMount after) => new(index, default, default, before, after, true);
    }

    private BuildDraft(HullDesign design, List<DesignBox> boxes, List<DesignMount> mounts, GameContent content, int historyLimit)
    {
        Name = design.Name; Class = design.Class; BlockSize = design.BlockSize; Color = design.Color;
        _boxes = boxes; _mounts = mounts; _content = content; _historyLimit = historyLimit;
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
        var mounts = new List<DesignMount>(design.Mounts.Length);
        for (var i = 0; i < design.BlockCount; i++)
        {
            if (!BlockSet.Has(alive, i)) continue;

            // A block's mounts come with it, under the index the block has in the draft.
            design.MountsOf(i, out var start, out var end);
            for (var m = start; m < end; m++) mounts.Add(design.Mounts[m] with { Parent = boxes.Count });
            boxes.Add(design.Boxes[i]);
        }

        return new BuildDraft(design, boxes, mounts, content, historyLimit);
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
    /// <summary>
    /// The parts mounted on block faces, in the order they were made; <see cref="DesignMount.Parent"/> is an index
    /// into <see cref="Boxes"/>. Valid until the next edit; read it again after one.
    /// </summary>
    public ReadOnlySpan<DesignMount> Mounts => CollectionsMarshal.AsSpan(_mounts);
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

    /// <summary>Adds a box at the end of the draft, facing <paramref name="facing"/>.</summary>
    public DraftEdit Add(GridSize at, GridSize size, BlockType type, BlockShape shape = BlockShape.Box, BlockFacing facing = BlockFacing.Back)
    {
        ArgumentNullException.ThrowIfNull(type);
        var box = new DesignBox(at, size, type, shape, facing);
        if (!BlockShapes.IsValid(shape)) throw new ArgumentOutOfRangeException(nameof(shape));
        if (!Facings.IsValid(facing)) throw new ArgumentOutOfRangeException(nameof(facing));
        var check = CanAdd(at, size, type, shape);
        if (check != DraftEdit.Done) return check;
        Do(Step.Block(_boxes.Count, default, box));
        Finish();
        return DraftEdit.Done;
    }

    /// <summary>
    /// Mounts a part on side <paramref name="face"/> of block <paramref name="parent"/>, its square's low corner
    /// <paramref name="u"/> and <paramref name="v"/> tenths of a cell from the face's (<see cref="DesignMount"/>).
    /// It points out of the face. The new mount is the last of <see cref="Mounts"/>.
    /// </summary>
    public DraftEdit AddMount(int parent, BlockFacing face, int u, int v, BlockType type)
    {
        ArgumentNullException.ThrowIfNull(type);
        var check = CanAddMount(parent, face, u, v, type);
        if (check != DraftEdit.Done) return check;
        Do(Step.Part(_mounts.Count, default, new DesignMount(parent, face, u, v, type)));
        Finish(settle: false);
        return DraftEdit.Done;
    }

    /// <summary>
    /// What <see cref="AddMount"/> would answer, without changing the draft, its version or its history: whether
    /// the part has room there. One pass over the boxes and the mounts; allocates nothing.
    /// </summary>
    public DraftEdit CanAddMount(int parent, BlockFacing face, int u, int v, BlockType type)
    {
        if (type?.Mount is null) return DraftEdit.WrongKind;
        if (_mounts.Count >= _content.Blocks.Limits.MaxMounts) return DraftEdit.TooManyMounts;
        return FaceMounts.Check(Boxes, Mounts, new DesignMount(parent, face, u, v, type));
    }

    /// <summary>
    /// Where a part's square goes on side <paramref name="face"/> of block <paramref name="parent"/> so that its
    /// middle is as near as it can be to <paramref name="point"/> (grid space, as the editor's rays are): on the
    /// face's grid of tenths, and kept on the face. Only a position: <see cref="AddMount"/> still checks it.
    /// </summary>
    public void SnapMount(int parent, BlockFacing face, BlockType type, Vector3 point, out int u, out int v)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)parent, (uint)_boxes.Count, nameof(parent));
        var spec = type.Mount ?? throw new ArgumentException("The part is no mount.", nameof(type));
        var box = _boxes[parent];
        var n = Facings.Axis(face);
        var a = (n + 1) % 3;
        var b = (n + 2) % 3;
        u = Place(a);
        v = Place(b);

        int Place(int axis)
        {
            // The face's low edge is half a cell below its first cell's centre; the square's low corner then sits
            // half its side below the point.
            var from = (point[axis] + 0.5f - Of(box.At, axis)) * FaceMounts.PerCell - spec.Side * 0.5f;
            return Math.Clamp((int)MathF.Floor(from + 0.5f), 0, Math.Max(Of(box.Size, axis) * FaceMounts.PerCell - spec.Side, 0));
        }
    }

    /// <summary>
    /// What <see cref="Add"/> would answer for a box here, without changing the draft, its version or its history:
    /// for showing whether a placement fits before it is made. With a <paramref name="type"/>, also whether its
    /// drive is of the draft's kind and the size is one the type allows. One pass over the boxes; allocates nothing.
    /// </summary>
    public DraftEdit CanAdd(GridSize at, GridSize size, BlockType? type = null, BlockShape shape = BlockShape.Box)
    {
        // The same count bound GeometryAdmission.Count applies, checked here so the editor stops at it; Export runs the real one.
        if (!CountFits(_boxes.Count + 1L))
            return DraftEdit.TooManyBlocks;
        if (type?.Mount is not null)
            return DraftEdit.WrongKind;
        if (!BoxDesign.ShapeAllowed(type, shape))
            return DraftEdit.BoxOnly;
        if (type is not null && type.Tank != FuelKind.None && Tanks() >= _content.Blocks.Limits.MaxTanks)
            return DraftEdit.TooManyTanks;
        var check = Check(new DesignBox(at, size, type!), -1);
        if (check != DraftEdit.Done || type is null)
            return check;
        var box = new DesignBox(at, size, type);
        return MixesDrives(new ReadOnlySpan<DesignBox>(in box)) ? DraftEdit.MixedDrives : DraftEdit.Done;
    }

    /// <summary>
    /// Adds copies of <paramref name="boxes"/>, each moved by <paramref name="by"/> cells, at the end of the
    /// draft in their order: the pasted blocks are the last <c>boxes.Length</c>. All of them or none. With
    /// <paramref name="mounts"/>, whose <see cref="DesignMount.Parent"/> is an index into <paramref name="boxes"/>,
    /// the copies carry those parts; one with no room where it lands, or past the content's count, is left behind.
    /// </summary>
    public DraftEdit Paste(ReadOnlySpan<DesignBox> boxes, GridSize by, ReadOnlySpan<DesignMount> mounts = default)
    {
        foreach (ref readonly var box in boxes) ArgumentNullException.ThrowIfNull(box.Type, nameof(boxes));
        var check = CanPaste(boxes, by);
        if (check != DraftEdit.Done) return check;
        var first = _boxes.Count;
        for (var i = 0; i < boxes.Length; i++)
            Do(Step.Block(first + i, default, boxes[i] with { At = Shifted(boxes[i].At, by) }));
        foreach (ref readonly var mount in mounts)
        {
            if ((uint)mount.Parent >= (uint)boxes.Length) throw new ArgumentOutOfRangeException(nameof(mounts));
            var placed = mount with { Parent = first + mount.Parent };
            if (_mounts.Count < _content.Blocks.Limits.MaxMounts && FaceMounts.Check(Boxes, Mounts, placed) == DraftEdit.Done)
                Do(Step.Part(_mounts.Count, default, placed));
        }

        Finish();
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
        var tanks = 0;
        foreach (ref readonly var box in boxes)
        {
            if (box.Type?.Mount is not null) return DraftEdit.WrongKind;
            if (box.Type is not null && box.Type.Tank != FuelKind.None) tanks++;
        }

        if (tanks > 0 && Tanks() + tanks > _content.Blocks.Limits.MaxTanks) return DraftEdit.TooManyTanks;
        var check = CheckGroup(boxes, default, by, default);
        return check == DraftEdit.Done && MixesDrives(boxes) ? DraftEdit.MixedDrives : check;
    }

    /// <summary>
    /// Replaces every block and mounted part of the draft by <paramref name="boxes"/> and <paramref name="mounts"/>
    /// (whose <see cref="DesignMount.Parent"/> is an index into <paramref name="boxes"/>), as one step to undo and
    /// redo. The name, class, block size and colour stay the draft's: the ship keeps what it is. All of it or none:
    /// the new blocks are checked against the content's limits and each other, and every part against its face.
    /// </summary>
    public DraftEdit Replace(ReadOnlySpan<DesignBox> boxes, ReadOnlySpan<DesignMount> mounts)
    {
        if (boxes.IsEmpty) return DraftEdit.Unchanged;
        foreach (ref readonly var box in boxes)
        {
            ArgumentNullException.ThrowIfNull(box.Type, nameof(boxes));
            if (box.Type.Mount is not null) return DraftEdit.WrongKind;
        }

        if (!CountFits(boxes.Length)) return DraftEdit.TooManyBlocks;
        if (mounts.Length > _content.Blocks.Limits.MaxMounts) return DraftEdit.TooManyMounts;
        if (BoxDesign.MixedDrive(boxes, out _) >= 0) return DraftEdit.MixedDrives;

        // Everything now in the draft goes, so nothing of it is compared against.
        var words = BlockSet.WordCount(_boxes.Count);
        if (_marks.Length < words) _marks = new ulong[words];
        Array.Fill(_marks, ulong.MaxValue);
        var check = CheckGroup(boxes, default, default, _marks);
        if (check != DraftEdit.Done) return check;
        for (var i = 0; i < mounts.Length; i++)
        {
            if ((uint)mounts[i].Parent >= (uint)boxes.Length || mounts[i].Type is null) return DraftEdit.NoSuchBlock;
            var fit = FaceMounts.Check(boxes, mounts[..i], mounts[i]);
            if (fit != DraftEdit.Done) return fit;
        }

        for (var i = _mounts.Count - 1; i >= 0; i--) Do(Step.Part(i, _mounts[i], default));
        for (var i = _boxes.Count - 1; i >= 0; i--) Do(Step.Block(i, _boxes[i], default));
        for (var i = 0; i < boxes.Length; i++) Do(Step.Block(i, default, boxes[i]));
        for (var i = 0; i < mounts.Length; i++) Do(Step.Part(i, default, mounts[i]));
        Finish(settle: false);
        return DraftEdit.Done;
    }

    /// <summary>
    /// Runs on a design read from a file the checks <see cref="Export"/> runs on a draft, which are the ones a server
    /// runs on a received design, with the draft's own name, class, block size and colour taken from the design.
    /// Costs about as much as receiving a design: call it on a worker.
    /// </summary>
    public static DraftExport Check(BoxDesign design, GameContent content) =>
        Export(design.Name, design.Class, design.BlockSize, design.Color, design.Blocks.ToArray(), design.Mounts.ToArray(), content);

    /// <summary>Removes a block and what is mounted on it; later blocks move down one index.</summary>
    public DraftEdit Delete(int block) => Delete(new ReadOnlySpan<int>(in block), default);

    /// <summary>Removes the blocks of a selection and what is mounted on them; the blocks left move down and keep their order.</summary>
    public DraftEdit Delete(ReadOnlySpan<int> blocks) => Delete(blocks, default);

    /// <summary>Removes one mounted part; later mounts move down one index.</summary>
    public DraftEdit DeleteMount(int mount) => Delete(default, new ReadOnlySpan<int>(in mount));

    /// <summary>
    /// Removes the blocks and the mounted parts of a selection together, as one step: each listed block with
    /// everything mounted on it, and each listed mount (an index into <see cref="Mounts"/>, none twice). What is
    /// left moves down and keeps its order.
    /// </summary>
    public DraftEdit Delete(ReadOnlySpan<int> blocks, ReadOnlySpan<int> mounts)
    {
        var check = Mark(blocks, out var marks);
        if (check != DraftEdit.Done) return check;
        for (var i = 0; i < mounts.Length; i++)
        {
            if ((uint)mounts[i] >= (uint)_mounts.Count) return DraftEdit.NoSuchBlock;
            if (mounts[..i].Contains(mounts[i])) return DraftEdit.BadSelection;
        }

        if (blocks.IsEmpty && mounts.IsEmpty) return DraftEdit.Unchanged;

        // From the last down, so that no removal moves one that is still to go; mounts before the blocks they sit on.
        for (var i = _mounts.Count - 1; i >= 0; i--)
            if (mounts.Contains(i) || BlockSet.Has(marks, _mounts[i].Parent)) Do(Step.Part(i, _mounts[i], default));
        for (var i = _boxes.Count - 1; i >= 0; i--)
            if (BlockSet.Has(marks, i)) Do(Step.Block(i, _boxes[i], default));
        Finish(settle: false);
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
        foreach (var block in blocks)
        {
            var box = _boxes[block];
            Do(Step.Block(block, box, box with { At = Shifted(box.At, by) }));
        }

        Finish();
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
        var lowest = 0;
        while (!BlockSet.Has(marks, lowest)) lowest++;

        // What was mounted on the blocks stays where it is, now on the one block: every such mount was on the
        // outside of the box they fill, which is a face of the merged block.
        Span<int> low = stackalloc int[3];
        Span<int> high = stackalloc int[3];
        for (var i = 0; i < _mounts.Count; i++)
        {
            var mount = _mounts[i];
            if (!BlockSet.Has(marks, mount.Parent)) continue;
            FaceMounts.Box(_boxes[mount.Parent], mount, low, high);
            var n = Facings.Axis(mount.Face);
            int a = (n + 1) % 3, b = (n + 2) % 3;
            Do(Step.Part(i, mount, mount with { Parent = lowest, U = low[a] - Of(merged.At, a) * FaceMounts.PerCell, V = low[b] - Of(merged.At, b) * FaceMounts.PerCell }));
        }

        for (var i = _boxes.Count - 1; i > lowest; i--)
            if (BlockSet.Has(marks, i)) Do(Step.Block(i, _boxes[i], default));
        Do(Step.Block(lowest, _boxes[lowest], merged));
        Finish();
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
        var limit = BoxDesign.MaxSide(first.Type, _content.Blocks.Limits);
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

        // A minus side that moves takes the block's low corner with it; what is mounted on the faces along that
        // axis is counted from that corner, so it is counted again to stay where it is. A mount the face no
        // longer reaches goes with the rest that has no room.
        var axis = side >> 1;
        var shift = (Of(_boxes[block].At, axis) - Of(changed.At, axis)) * FaceMounts.PerCell;
        for (var i = 0; shift != 0 && i < _mounts.Count; i++)
        {
            var mount = _mounts[i];
            var n = Facings.Axis(mount.Face);
            if (mount.Parent != block || n == axis) continue;
            Do(Step.Part(i, mount, (n + 1) % 3 == axis ? mount with { U = mount.U + shift } : mount with { V = mount.V + shift }));
        }

        Do(Step.Block(block, _boxes[block], changed));
        Finish();
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

    /// <summary>
    /// Rotates one block by a positive quarter turn, keeping its low corner fixed; one undo step. Its size, shape
    /// and facing turn together, so a thrust part turned this way pushes another way. What was mounted on it is
    /// taken off: its faces are other faces now.
    /// </summary>
    public DraftEdit Rotate(int block, int axis)
    {
        var check = CanRotate(block, axis, out var changed);
        if (check != DraftEdit.Done) return check;
        for (var i = _mounts.Count - 1; i >= 0; i--)
            if (_mounts[i].Parent == block) Do(Step.Part(i, _mounts[i], default));
        Do(Step.Block(block, _boxes[block], changed));
        Finish();
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
        changed = box with { Size = size, Shape = BlockShapes.Rotate(box.Shape, axis), Facing = Facings.Rotate(box.Facing, axis) };
        if (changed == box) return DraftEdit.Unchanged;
        return Check(changed, block);
    }

    // Room for one edit's copies, so a mirrored edit takes no memory. An edit runs on one thread, one at a time.
    private readonly DesignBox[] _copyBoxes = new DesignBox[Mirroring.MaxCopies];
    private readonly DesignMount[] _copyMounts = new DesignMount[Mirroring.MaxCopies];

    // The mirror planes' place and the draft's extent, worked out again only when the draft has changed.
    private int _frameVersion = -1;
    private bool _hasFrame;
    private GridSize _centre2;
    private GridSize _frameLow;
    private GridSize _frameHigh;

    /// <summary>
    /// Where the mirror planes are: through the centre of the draft's first core block. <paramref name="centre2"/> is
    /// twice that centre in cells and <paramref name="low"/> and <paramref name="high"/> the draft's extent on the
    /// grid's integer edges. False when the draft has no core, which leaves nothing to mirror about. One pass over
    /// the boxes after a change, nothing between; allocates nothing.
    /// </summary>
    public bool TryMirrorFrame(out GridSize centre2, out GridSize low, out GridSize high)
    {
        if (_frameVersion != Version)
        {
            _frameVersion = Version;
            _hasFrame = false;
            int lx = int.MaxValue, ly = int.MaxValue, lz = int.MaxValue, hx = int.MinValue, hy = int.MinValue, hz = int.MinValue;
            foreach (ref readonly var box in Boxes)
            {
                if (!_hasFrame && box.Type.Core)
                {
                    _hasFrame = true;
                    _centre2 = Mirroring.Centre2(box);
                }

                lx = Math.Min(lx, box.At.X); ly = Math.Min(ly, box.At.Y); lz = Math.Min(lz, box.At.Z);
                hx = Math.Max(hx, box.At.X + box.Size.X); hy = Math.Max(hy, box.At.Y + box.Size.Y); hz = Math.Max(hz, box.At.Z + box.Size.Z);
            }

            _frameLow = new GridSize(lx, ly, lz);
            _frameHigh = new GridSize(hx, hy, hz);
        }

        centre2 = _centre2; low = _frameLow; high = _frameHigh;
        return _hasFrame;
    }

    /// <summary>
    /// Fills <paramref name="into"/> (room for <see cref="Mirroring.MaxCopies"/>) with <paramref name="box"/> and its
    /// mirrored copies across <paramref name="axes"/>, the original first, and returns how many. Without a core to
    /// mirror about, or with no axes, only the original. Allocates nothing.
    /// </summary>
    public int MirrorBoxes(in DesignBox box, MirrorAxes axes, Span<DesignBox> into)
    {
        if (axes == MirrorAxes.None || !TryMirrorFrame(out var centre2, out _, out _))
        {
            into[0] = box;
            return 1;
        }

        return Mirroring.Copies(box, axes, centre2, into);
    }

    /// <summary>
    /// What <see cref="Add"/> would answer for each of these boxes placed one after the other, as
    /// <see cref="AddMirrored"/> places them: a box is checked against the draft and the boxes before it that fit.
    /// Nothing changes: the draft, its version and its history stay as they were. Allocates nothing once the draft's
    /// list has room.
    /// </summary>
    public void CheckBoxes(ReadOnlySpan<DesignBox> boxes, Span<DraftEdit> results)
    {
        var provisional = 0;
        try
        {
            for (var i = 0; i < boxes.Length; i++)
            {
                results[i] = CanAdd(boxes[i].At, boxes[i].Size, boxes[i].Type, boxes[i].Shape);
                if (results[i] != DraftEdit.Done)
                    continue;

                // Later boxes are checked against this one as if it were placed; taken out again below.
                _boxes.Add(boxes[i]);
                provisional++;
            }
        }
        finally
        {
            _boxes.RemoveRange(_boxes.Count - provisional, provisional);
        }
    }

    /// <summary>
    /// Adds <paramref name="box"/> and its mirrored copies across <paramref name="axes"/> as one step to undo. When
    /// the box itself does not fit, the answer is why and nothing changes. A copy that does not fit is left out:
    /// <paramref name="skipped"/> counts those. A copy that is the box itself, as one lying on a plane is, is not
    /// a second block and is not counted.
    /// </summary>
    public DraftEdit AddMirrored(in DesignBox box, MirrorAxes axes, out int skipped)
    {
        skipped = 0;
        ArgumentNullException.ThrowIfNull(box.Type);
        if (!BlockShapes.IsValid(box.Shape)) throw new ArgumentOutOfRangeException(nameof(box));
        if (!Facings.IsValid(box.Facing)) throw new ArgumentOutOfRangeException(nameof(box));
        Span<DesignBox> copies = _copyBoxes;
        Span<DraftEdit> checks = stackalloc DraftEdit[Mirroring.MaxCopies];
        var count = MirrorBoxes(box, axes, copies);
        CheckBoxes(copies[..count], checks);
        if (checks[0] != DraftEdit.Done)
            return checks[0];
        for (var i = 0; i < count; i++)
        {
            if (checks[i] == DraftEdit.Done)
                Do(Step.Block(_boxes.Count, default, copies[i]));
            else
                skipped++;
        }

        Finish();
        return DraftEdit.Done;
    }

    /// <summary>
    /// Fills <paramref name="into"/> (room for <see cref="Mirroring.MaxCopies"/>) with <paramref name="mount"/> and
    /// its mirrored copies across <paramref name="axes"/>, the original first, and returns how many. A copy whose
    /// mirrored parent block is not in the draft has <see cref="DesignMount.Parent"/> -1: there is nothing to put
    /// it on. Without a core to mirror about, or with no axes, only the original. Allocates nothing.
    /// </summary>
    public int MirrorMounts(in DesignMount mount, MirrorAxes axes, Span<DesignMount> into)
    {
        into[0] = mount;
        if (axes == MirrorAxes.None || !TryMirrorFrame(out var centre2, out _, out _) || (uint)mount.Parent >= (uint)_boxes.Count)
            return 1;
        var parent = _boxes[mount.Parent];
        var count = 1;
        for (var mask = 1; mask < Mirroring.MaxCopies; mask++)
        {
            if ((mask & ~(int)axes) != 0)
                continue;
            var mirrored = Mirroring.Box(parent, mask, centre2);
            var copy = Mirroring.Mount(parent, mirrored, mount, mask, centre2) with { Parent = IndexOfBlock(mirrored) };
            var seen = false;
            for (var i = 0; !seen && i < count; i++)
                seen = into[i] == copy;
            if (!seen)
                into[count++] = copy;
        }

        return count;
    }

    /// <summary>
    /// What <see cref="AddMount"/> would answer for each of these mounts placed one after the other, as
    /// <see cref="AddMountMirrored"/> places them. A mount with no parent (-1) cannot be placed. Nothing changes.
    /// </summary>
    public void CheckMounts(ReadOnlySpan<DesignMount> mounts, Span<DraftEdit> results)
    {
        var provisional = 0;
        try
        {
            for (var i = 0; i < mounts.Length; i++)
            {
                results[i] = mounts[i].Parent < 0 ? DraftEdit.NoSuchBlock : CanAddMount(mounts[i].Parent, mounts[i].Face, mounts[i].U, mounts[i].V, mounts[i].Type);
                if (results[i] != DraftEdit.Done)
                    continue;
                _mounts.Add(mounts[i]);
                provisional++;
            }
        }
        finally
        {
            _mounts.RemoveRange(_mounts.Count - provisional, provisional);
        }
    }

    /// <summary>
    /// As <see cref="AddMount"/>, with the mirrored copies across <paramref name="axes"/> as one step to undo: each
    /// goes on the mirrored block, on the mirrored face, at the mirrored place on it. A copy with no block to go on
    /// or no room is left out and counted in <paramref name="skipped"/>; a copy that is the mount itself is not.
    /// </summary>
    public DraftEdit AddMountMirrored(int parent, BlockFacing face, int u, int v, BlockType type, MirrorAxes axes, out int skipped)
    {
        skipped = 0;
        ArgumentNullException.ThrowIfNull(type);
        Span<DesignMount> copies = _copyMounts;
        Span<DraftEdit> checks = stackalloc DraftEdit[Mirroring.MaxCopies];
        var count = MirrorMounts(new DesignMount(parent, face, u, v, type), axes, copies);
        CheckMounts(copies[..count], checks);
        if (checks[0] != DraftEdit.Done)
            return checks[0];
        for (var i = 0; i < count; i++)
        {
            if (checks[i] == DraftEdit.Done)
                Do(Step.Part(_mounts.Count, default, copies[i]));
            else
                skipped++;
        }

        Finish(settle: false);
        return DraftEdit.Done;
    }

    /// <summary>
    /// As <see cref="Delete(ReadOnlySpan{int}, ReadOnlySpan{int})"/>, and also the mirrored copies across
    /// <paramref name="axes"/> of each listed block and mounted part that the draft has: the same box of the same
    /// type, or the same mount on the mirrored block. One step to undo. A copy the draft does not have is simply not
    /// there to remove. Refused as <see cref="DraftEdit.Unchanged"/> when it would take every block.
    /// </summary>
    public DraftEdit DeleteMirrored(ReadOnlySpan<int> blocks, ReadOnlySpan<int> mounts, MirrorAxes axes)
    {
        if (axes == MirrorAxes.None || !TryMirrorFrame(out var centre2, out _, out _))
            return Delete(blocks, mounts);
        var check = Mark(blocks, out _);
        if (check != DraftEdit.Done) return check;
        var allBlocks = new List<int>(blocks.Length * 2);
        var allMounts = new List<int>(mounts.Length * 2);
        foreach (var block in blocks)
        {
            var box = _boxes[block];
            for (var mask = 0; mask < Mirroring.MaxCopies; mask++)
            {
                if ((mask & ~(int)axes) != 0) continue;
                var at = mask == 0 ? block : IndexOfBlock(Mirroring.Box(box, mask, centre2));
                if (at >= 0 && !allBlocks.Contains(at)) allBlocks.Add(at);
            }
        }

        foreach (var index in mounts)
        {
            if ((uint)index >= (uint)_mounts.Count) return DraftEdit.NoSuchBlock;
            var mount = _mounts[index];
            var parent = _boxes[mount.Parent];
            for (var mask = 0; mask < Mirroring.MaxCopies; mask++)
            {
                if ((mask & ~(int)axes) != 0) continue;
                var at = index;
                if (mask != 0)
                {
                    var mirrored = Mirroring.Box(parent, mask, centre2);
                    var copy = Mirroring.Mount(parent, mirrored, mount, mask, centre2) with { Parent = IndexOfBlock(mirrored) };
                    at = copy.Parent < 0 ? -1 : _mounts.IndexOf(copy);
                }

                if (at >= 0 && !allMounts.Contains(at)) allMounts.Add(at);
            }
        }

        if (allBlocks.Count >= _boxes.Count) return DraftEdit.Unchanged;
        return Delete(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(allBlocks), System.Runtime.InteropServices.CollectionsMarshal.AsSpan(allMounts));
    }

    // The index of the block that is exactly this box, or -1: the mirrored counterpart of a block, when there is one.
    private int IndexOfBlock(in DesignBox box)
    {
        for (var i = 0; i < _boxes.Count; i++)
            if (Mirroring.Same(_boxes[i], box)) return i;
        return -1;
    }

    /// <summary>Takes back the latest edit. False when there is none.</summary>
    public bool Undo()
    {
        if (_undo.Count == 0) return false;
        var steps = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        for (var i = steps.Length - 1; i >= 0; i--) Apply(steps[i], false);
        _redo.Add(steps);
        return true;
    }

    /// <summary>Makes the latest undone edit again. False when there is none.</summary>
    public bool Redo()
    {
        if (_redo.Count == 0) return false;
        var steps = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        foreach (var step in steps) Apply(step, true);
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
    public DraftExport Export() => Export(Name, Class, BlockSize, Color, _boxes.ToArray(), _mounts.ToArray(), _content);

    /// <summary>
    /// A frozen copy of the draft as it is now, numbered with its <see cref="Version"/>. Another thread may export
    /// the copy while this draft goes on being edited; the copy never changes. Allocates the box array once.
    /// </summary>
    public DraftCopy Copy() => new(Name, Class, BlockSize, Color, _boxes.ToArray(), _mounts.ToArray(), _content, Version);

    internal static DraftExport Export(string name, HullClass hullClass, float blockSize, Vector3 color, DesignBox[] boxes, DesignMount[] mounts, GameContent content)
    {
        SurfaceUsage? surface = null;
        try
        {
            var limits = content.Blocks.Limits;
            GeometryAdmission.Count(boxes.Length, limits);
            BoxDesign.RequireOneDriveKind(boxes, "draft");
            var built = HullDesign.Of(new BoxDesign(name, blockSize, boxes, limits, "draft", hullClass, color, mounts), content.Rules);

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

    /// <summary>How many of the draft's blocks are tanks. One pass over the boxes; allocates nothing.</summary>
    private int Tanks()
    {
        var tanks = 0;
        foreach (var box in _boxes)
            if (box.Type.Tank != FuelKind.None) tanks++;
        return tanks;
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
            if (!BoxDesign.ShapeAllowed(box.Type, box.Shape)) return DraftEdit.BoxOnly;
            for (var axis = 0; axis < 3; axis++)
            {
                // In 64 bits: a move far enough to wrap around would otherwise look like one that fits.
                long at = (long)Of(box.At, axis) + Of(by, axis), size = Of(box.Size, axis);
                if (size < 1 || size > BoxDesign.MaxSide(box.Type, limits)) return DraftEdit.BadSize;
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
        if (!BoxDesign.ShapeAllowed(box.Type, box.Shape)) return DraftEdit.BoxOnly;
        for (var axis = 0; axis < 3; axis++)
        {
            int at = Of(box.At, axis), size = Of(box.Size, axis);
            if (size < 1 || size > BoxDesign.MaxSide(box.Type, limits)) return DraftEdit.BadSize;
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

    // Makes one change of the edit under way and notes it.
    private void Do(Step step)
    {
        Apply(step, true);
        _pending.Add(step);
    }

    // Ends an edit: remembers its changes as one step of history, however many they are. With `settle`, mounts the
    // edit left without room are taken away first, as part of the same step.
    private void Finish(bool settle = true)
    {
        if (settle) Settle();
        _redo.Clear();
        if (_undo.Count == _historyLimit) _undo.RemoveAt(0);
        _undo.Add([.. _pending]);
        _pending.Clear();
    }

    // Takes away every mount that has no room as the boxes now stand, in order: an earlier mount keeps its place
    // against a later one. Edits that only add or remove mounts check for themselves and skip this.
    private void Settle()
    {
        for (var i = 0; i < _mounts.Count;)
        {
            var mounts = Mounts;
            if (FaceMounts.Check(Boxes, mounts[..i], mounts[i]) == DraftEdit.Done) i++;
            else Do(Step.Part(i, _mounts[i], default));
        }
    }

    // Makes a change (`forward`) or takes it back. A box or mount without a type means none, so this inserts or
    // removes. Mounts name their block by index: they follow when a box is put in or taken out below them. A box
    // is only ever taken out with nothing mounted on it, and comes back before what was.
    private void Apply(in Step step, bool forward)
    {
        var index = step.Index;
        if (step.Mount)
        {
            var (before, after) = forward ? (step.MountBefore, step.MountAfter) : (step.MountAfter, step.MountBefore);
            if (before.Type is null) _mounts.Insert(index, after);
            else if (after.Type is null) _mounts.RemoveAt(index);
            else _mounts[index] = after;
            Version++;
            return;
        }

        var (from, to) = forward ? (step.Before, step.After) : (step.After, step.Before);
        if (from.Type is null || to.Type is null)
        {
            var mounts = CollectionsMarshal.AsSpan(_mounts);
            var by = from.Type is null ? 1 : -1;
            foreach (ref var mount in mounts)
                if (mount.Parent > index || (by > 0 && mount.Parent == index)) mount = mount with { Parent = mount.Parent + by };
        }

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
    private readonly DesignMount[] _mounts;
    private readonly GameContent _content;

    internal DraftCopy(string name, HullClass hullClass, float blockSize, Vector3 color, DesignBox[] boxes, DesignMount[] mounts, GameContent content, int version)
    {
        _name = name; _class = hullClass; _blockSize = blockSize; _color = color; _boxes = boxes; _mounts = mounts; _content = content; Version = version;
    }

    /// <summary>The draft's <see cref="BuildDraft.Version"/> when the copy was made.</summary>
    public int Version { get; }

    /// <summary>How many boxes the copy holds.</summary>
    public int BlockCount => _boxes.Length;

    /// <summary>As <see cref="BuildDraft.Export"/>, for the draft as it was when copied. Safe on any thread.</summary>
    public DraftExport Export() => BuildDraft.Export(_name, _class, _blockSize, _color, _boxes, _mounts, _content);
}
