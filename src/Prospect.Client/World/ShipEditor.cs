using Brutal.Concurrency.Jobs;
using Brutal.GlfwApi;
using Brutal.Numerics;
using Prospect.Client.Input;
using Prospect.Engine.Diagnostics;
using Prospect.Engine.Jobs;
using Prospect.Game.Client;
using Prospect.Game.Protocol;
using Prospect.Game.World;

namespace Prospect.Client.World;

/// <summary>Why the player could not be put in control of the ship they just built.</summary>
internal enum FollowRefusal : byte
{
    /// <summary>Nothing to say: control was asked for, or there was nothing to ask for.</summary>
    None,

    /// <summary>The ship is not in the snapshot: it was lost or never arrived.</summary>
    Missing,

    /// <summary>The ship is in another sector than the drone, so the server would refuse the switch.</summary>
    OutOfSector,
}

/// <summary>Where the player's edit stands on this client.</summary>
internal enum EditorStage : byte
{
    /// <summary>No edit. The world flies as usual.</summary>
    Closed,

    /// <summary>The player asked to edit and the server has not said yes yet. The world still flies as usual.</summary>
    Requesting,

    /// <summary>The server allows the edit: the draft is shown around the ship and can be changed.</summary>
    Editing,

    /// <summary>A checked draft was committed and is on its way to the ship. The draft is shown and frozen.</summary>
    Applying,
}

/// <summary>Whether the draft's latest version has been checked as a server would check it.</summary>
internal enum DraftCheck : byte
{
    /// <summary>A check is due or running for the current version.</summary>
    Pending,

    /// <summary>The current version passed; it can be applied.</summary>
    Ready,

    /// <summary>The current version breaks a rule; <see cref="ShipEditor.CheckProblem"/> says which.</summary>
    Invalid,

    /// <summary>The check itself failed; <see cref="ShipEditor.CheckProblem"/> says so.</summary>
    Failed,
}

/// <summary>Whether the face-drag resize can start on the selection, and if not why.</summary>
internal enum ResizeOffer : byte
{
    /// <summary>One block is selected and the ship can be edited.</summary>
    Yes,

    /// <summary>Nothing is selected.</summary>
    None,

    /// <summary>More than one block is selected: the gesture sizes one block.</summary>
    Several,

    /// <summary>Only a mounted part is selected: it has no size.</summary>
    Mount,

    /// <summary>The ship cannot be edited now, or something else holds the pointer.</summary>
    Busy,
}

/// <summary>What the pointer carries in the editor until it is let go.</summary>
internal enum EditorCarry : byte
{
    /// <summary>Nothing: clicks place or select.</summary>
    None,

    /// <summary>A part dragged out of the part list; let go over a face, it is placed there.</summary>
    Part,

    /// <summary>The selected blocks, dragged by one of them; let go over a face, they move there together.</summary>
    Move,

    /// <summary>The copied blocks, following the pointer; a click places them, Escape or a right click drops them.</summary>
    Paste,
}

/// <summary>Why Repair cannot be pressed now, or <see cref="None"/> when it can.</summary>
internal enum RepairBlock : byte
{
    /// <summary>Repair can be pressed.</summary>
    None,

    /// <summary>The ship has lost nothing and was never repaired.</summary>
    NotDamaged,

    /// <summary>The draft has changes; a repair would throw them away. <see cref="ShipEditor.DiscardChanges"/> drops them.</summary>
    Unsaved,

    /// <summary>The edit is not open, or the server is still busy with something.</summary>
    Busy,
}

/// <summary>
/// The ship editor's client side: asks the server for an edit, keeps the draft, the selection and the part being
/// placed, checks each version of the draft on a worker, commits a checked one and follows the server's answers.
/// The server decides everything that matters: an edit is shown only once it is granted, and a draft is only ever
/// sent with the token of the edit that is open now. Runs on the render thread; the worker only ever sees a
/// frozen <see cref="DraftCopy"/>, never the draft being edited.
/// </summary>
internal sealed class ShipEditor(JobSystem jobs)
{
    /// <summary>Seconds after the last edit before the draft is checked, so a burst of clicks is checked once.</summary>
    public const float CheckDelay = 0.2f;

    /// <summary>Seconds a request may wait for the server's answer before the editor stops waiting.</summary>
    public const float RequestTimeout = 10f;

    /// <summary>Seconds a notice about the edit stays on screen after the editor has closed.</summary>
    public const float NoticeSeconds = 6f;

    private readonly CheckJob _job = new();
    private JobHandle _handle;
    private bool _jobRunning;
    private int _generation;
    private float _time;
    private float _editedAt;
    private float _requestedAt;
    private uint _seenSerial;
    private uint _askedSerial;

    // The hull the player flew when the edit began, and for a new ship the hull the grant is awaited for: the edit
    // follows the granted hull, which is not the flown one when the player is in the drone.
    private uint _pilotHull;
    private uint _awaitedHull;
    private uint _commitSerial;

    // The ship a New ship just made, until its edit ends: closing that edit by the player's own hand puts them in
    // control of it. An ordinary edit, and an edit ended by the server, never does.
    private uint _createdHull;
    private uint _followHull;
    private float _followAt;

    // The hull the draft was made from, as it was then: a kept draft is only reused for exactly that hull.
    private uint _sourceHull;
    private byte _sourceDesign;
    private uint _replacedFrom, _replacedTo;

    // Edits since the draft last matched the blueprint, counted through undo and redo: zero means unchanged, even
    // when the undo history was trimmed and cannot go back that far itself.
    private int _depth;

    // The selection as a list (in the order it was made, the last is the one the details panel shows) and as a
    // set for drawing and picking. Both only grow, on a click, never in a frame.
    private int[] _selection = new int[16];
    private int _selectionCount;
    private ulong[] _selectionMarks = new ulong[1];

    // The selected mounted parts, as indices into the draft's mounts, in the order they were picked.
    private int[] _mountSelection = new int[16];
    private int _mountSelectionCount;

    // The copied blocks, where they were, and which of them the pointer holds when they are pasted; and what was
    // mounted on them, each naming its block by its place among the copied ones.
    private DesignBox[] _clip = [];
    private int _clipCount;
    private int _clipAnchor;
    private DesignMount[] _clipMounts = [];
    private int _clipMountCount;

    // The block the pointer took hold of to move the selection.
    private int _grab;

    // The ship's blocks as they were when the edit opened, and which boxes of the draft they no longer have.
    private ulong[] _alive = [];
    private ulong[] _missing = [];
    private float _ownHull;
    private uint _ownRevision;
    private int _ownDents;

    // The merge the details panel offers, worked out again only when the draft or the selection changes.
    private int _mergeDraftVersion = -1;
    private int _mergeSelectionStamp = -1;
    private int _selectionStamp;

    /// <summary>Where the edit stands.</summary>
    public EditorStage Stage { get; private set; }

    /// <summary>True while the draft is shown around the ship instead of the ship itself.</summary>
    public bool Shown => Stage is EditorStage.Editing or EditorStage.Applying;

    /// <summary>True while the draft can be looked at and its blocks selected: granted, and not on its way to the ship.</summary>
    public bool CanInspect => Stage == EditorStage.Editing && Draft is not null;

    /// <summary>
    /// True while the draft may be changed: granted, not on its way to the ship, and the ship is not damaged. A
    /// damaged ship is repaired first; until then the editor only shows, selects and repairs. Every edit of the
    /// draft asks this.
    /// </summary>
    public bool CanEdit => CanInspect && !Damaged;

    /// <summary>True while the edit is open on a damaged ship: blocks can be selected and the ship repaired, nothing changed.</summary>
    public bool RepairOnly => CanInspect && Damaged;

    /// <summary>The draft, or null. Kept after the server ends an edit the player did not cancel.</summary>
    public BuildDraft? Draft { get; private set; }

    /// <summary>The design the draft started from: its pivot places the draft, its figures are the "before".</summary>
    public HullDesign? Source { get; private set; }

    /// <summary>The content the draft was made with: its parts and limits.</summary>
    public GameContent? Content { get; private set; }

    /// <summary>The hull being edited.</summary>
    public uint Hull { get; private set; }

    /// <summary>The server's latest word on the edit.</summary>
    public EditorView Server { get; private set; }

    /// <summary>
    /// The ship the player is to be put in control of now that they closed the edit of a ship they just built, or
    /// zero. The view takes it with <see cref="FinishFollow"/> as soon as the hull is in the snapshot.
    /// </summary>
    public uint FollowHull => _followHull;

    /// <summary>Seconds a built ship may be missing from the snapshot before taking control of it is given up.</summary>
    public const float FollowTimeout = 3f;

    /// <summary>True when the hull to follow has been awaited longer than <see cref="FollowTimeout"/>.</summary>
    public bool FollowTimedOut => _followHull != 0 && _time - _followAt > FollowTimeout;

    /// <summary>Why the last attempt to take control of a built ship was refused, shown for a while after; none when there is nothing to say.</summary>
    public FollowRefusal FollowNotice { get; private set; }

    /// <summary>When <see cref="FollowNotice"/> was given, on <see cref="Time"/>'s clock.</summary>
    public float FollowNoticeAt { get; private set; } = float.NegativeInfinity;

    /// <summary>Seconds since this editor started, the clock its notices are timed by.</summary>
    public float Time => _time;

    /// <summary>Why the editor last closed or was refused, shown for a while after; none when there is nothing to say.</summary>
    public EditorReason Notice { get; private set; }

    /// <summary>With <see cref="Notice"/>: true when the ship was rebuilt rather than an edit refused.</summary>
    public bool NoticeApplied { get; private set; }

    /// <summary>With <see cref="NoticeApplied"/>: true when the applied edit was of a ship just built, which the player then flies.</summary>
    public bool NoticeBuilt { get; private set; }

    /// <summary>With <see cref="Notice"/>: the seconds the server asked to wait, and when the notice was given.</summary>
    public int NoticeWait { get; private set; }
    public float NoticeAt { get; private set; } = float.NegativeInfinity;

    /// <summary>True when the editor closed without the player cancelling and their changes are kept for the next edit.</summary>
    public bool DraftKept { get; private set; }

    /// <summary>True when a kept draft could not be reused because the ship changed, and the draft started again.</summary>
    public bool DraftRestarted { get; private set; }

    /// <summary>A commit the server turned down while the edit stays open, with its wait and when it came.</summary>
    public EditorReason Refusal { get; private set; }
    public int RefusalWait { get; private set; }
    public float RefusalAt { get; private set; } = float.NegativeInfinity;

    /// <summary>The latest edit the player tried and what became of it, and when.</summary>
    public DraftEdit LastEdit { get; private set; }
    public float LastEditAt { get; private set; } = float.NegativeInfinity;

    /// <summary>The draft's latest check.</summary>
    public DraftCheck Check { get; private set; }

    /// <summary>The version of the draft <see cref="Checked"/> belongs to; -1 for none.</summary>
    public int CheckedVersion { get; private set; } = -1;

    /// <summary>The latest version that passed: its design gives the figures, its bytes are what Apply sends.</summary>
    public DraftExport Checked { get; private set; }

    /// <summary>Why the latest check did not pass, in a sentence.</summary>
    public string CheckProblem { get; private set; } = "";

    /// <summary>The selected block the details panel shows (the latest one picked), or -1 with none.</summary>
    public int Selected => _selectionCount > 0 ? _selection[_selectionCount - 1] : -1;

    /// <summary>Every selected block, by draft index, in the order they were picked. Valid until the next change.</summary>
    public ReadOnlySpan<int> Selection => _selection.AsSpan(0, _selectionCount);

    /// <summary>The selection as a <see cref="BlockSet"/> over the draft's blocks.</summary>
    public ReadOnlySpan<ulong> SelectionMarks => _selectionMarks;

    /// <summary>Whether a block of the draft is selected.</summary>
    public bool IsSelected(int block) => (uint)block < (uint)(_selectionMarks.Length << 6) && BlockSet.Has(_selectionMarks, block);

    /// <summary>Every selected mounted part, by index into the draft's mounts, in the order they were picked. Valid until the next change.</summary>
    public ReadOnlySpan<int> MountSelection => _mountSelection.AsSpan(0, _mountSelectionCount);

    /// <summary>The selected mounted part the details panel shows (the latest one picked), or -1 with none.</summary>
    public int SelectedMount => _mountSelectionCount > 0 ? _mountSelection[_mountSelectionCount - 1] : -1;

    /// <summary>Whether a mounted part of the draft is selected.</summary>
    public bool IsMountSelected(int mount) => MountSelection.Contains(mount);

    /// <summary>What the pointer carries.</summary>
    public EditorCarry Carry { get; private set; }

    /// <summary>With <see cref="EditorCarry.Move"/> or <see cref="EditorCarry.Paste"/>: how far the blocks would move, in quarter cells, and whether that fits.</summary>
    public bool HasCarryGhost { get; private set; }
    public GridPoint CarryBy { get; private set; }
    public DraftEdit CarryCheck { get; private set; }

    /// <summary>The copied blocks, as they were in the draft when copied.</summary>
    public ReadOnlySpan<DesignBox> Clipboard => _clip.AsSpan(0, _clipCount);

    /// <summary>What was mounted on the copied blocks; each names its block by its index in <see cref="Clipboard"/>.</summary>
    public ReadOnlySpan<DesignMount> ClipboardMounts => _clipMounts.AsSpan(0, _clipMountCount);

    /// <summary>True when the draft differs from the blueprint it started from.</summary>
    public bool Dirty => _depth != 0;

    private bool _confirmDiscard;

    /// <summary>
    /// True when leaving the edit now would throw away changes the player made: the draft is open and differs from the
    /// blueprint it started from. The one rule every way out asks (the editor key, Escape, Cancel, the menu's Leave and
    /// Quit); an edit the server ended is not asked about, as the draft is kept for it already.
    /// </summary>
    public bool WouldLoseChanges => CanInspect && Dirty;

    /// <summary>
    /// True while the "Discard changes?" question is up: Discard throws the draft away, Keep editing (also Enter and
    /// Escape) goes back to it. Never true without changes to lose, so an edit that ends under it takes it away.
    /// </summary>
    public bool ConfirmDiscard => _confirmDiscard && WouldLoseChanges;

    /// <summary>The draft's boxes the ship has lost since it was built, as a <see cref="BlockSet"/>; empty when none.</summary>
    public ReadOnlySpan<ulong> Missing => _missing;

    /// <summary>How many of the draft's boxes the ship has lost.</summary>
    public int MissingCount { get; private set; }

    /// <summary>Whether a draft box is one the ship has lost.</summary>
    public bool IsMissing(int block) => (uint)block < (uint)(_missing.Length << 6) && BlockSet.Has(_missing, block);

    /// <summary>The hull points the ship has now, as of the latest snapshot.</summary>
    public float OwnHullPoints => _ownHull;

    /// <summary>The latest check's whole result, whether it passed or not: its surface usage is kept either way.</summary>
    public DraftExport Latest { get; private set; }

    /// <summary>Set when the player asks to see a block; the view takes it once and turns the camera to it.</summary>
    public bool FocusRequested { get; private set; }

    /// <summary>What merging the selection would answer, and the block it would make.</summary>
    public DraftEdit MergeCheck { get; private set; } = DraftEdit.Unchanged;
    public DesignBox Merged { get; private set; }

    /// <summary>The side of the selected block that resizing moves.</summary>
    public int Face { get; private set; } = 2;

    /// <summary>The part to place, as an index into the content's block types, in file order.</summary>
    public int Part { get; private set; }

    /// <summary>True while a click places <see cref="Part"/>; false while a click selects.</summary>
    public bool Placing { get; private set; } = true;

    /// <summary>The whole-cell size the next part is placed at.</summary>
    public GridSize PartSize { get; private set; } = new(1, 1, 1);

    /// <summary>
    /// The shape the next part is placed in: a box, or a wedge filling half of <see cref="PartSize"/>. Always a box
    /// for a part that is never slanted (<see cref="PartBoxOnly"/>); the wedge chosen before comes back for the next
    /// part that may be one.
    /// </summary>
    public BlockShape PartShape => PartBoxOnly ? BlockShape.Box : _partShape;

    // The shape chosen for parts that may be slanted; a box-only part neither shows nor turns it.
    private BlockShape _partShape = BlockShape.Box;

    /// <summary>True when the part to place is never slanted (<see cref="BlockType.BoxOnly"/>): its wedge button is off.</summary>
    public bool PartBoxOnly => Content is { } content && (uint)Part < (uint)content.Blocks.Types.Count && !BoxDesign.ShapeAllowed(content.Blocks.Types[Part], BlockShape.WedgeXPlusPlus);

    /// <summary>
    /// The way the next part faces. It turns with the part (<see cref="RotatePart"/>): a thrust part pushes the ship
    /// the opposite way, so this is where its nozzle points.
    /// </summary>
    public BlockFacing PartFacing { get; private set; } = BlockFacing.Back;

    /// <summary>
    /// The way the ghost faces where it is held: <see cref="PartFacing"/>, except for a drone bay, whose hatch is
    /// chosen from the faces it touches there (<see cref="Render.RackDeck.Hatch"/>) and placed with it.
    /// </summary>
    public BlockFacing GhostFacing =>
        Content is { } content && (uint)Part < (uint)content.Blocks.Types.Count && content.Blocks.Types[Part].FacesOut && Draft is { } draft
            ? Render.RackDeck.Hatch(Render.RackDeck.Touched(draft.Boxes, -1, GhostAt, PartSize))
            : PartFacing;

    /// <summary>The most cells the chosen part may measure along a side: its type's limit, or the content's.</summary>
    public int PartMaxSide =>
        Content is { } content ? BoxDesign.MaxSide((uint)Part < (uint)content.Blocks.Types.Count ? content.Blocks.Types[Part] : null, content.Blocks.Limits) : 1;

    /// <summary>The block face under the pointer this frame, if any.</summary>
    public bool HasHover { get; private set; }
    public DraftHit Hover { get; private set; }

    /// <summary>While placing with a face under the pointer: where the part would go and whether it fits there.</summary>
    public bool HasGhost { get; private set; }
    public GridPoint GhostAt { get; private set; }
    public DraftEdit GhostCheck { get; private set; }

    /// <summary>
    /// True when the part to place mounts on a face instead of filling cells (<see cref="BlockType.Mount"/>): it is
    /// never turned, points out of the face it is put on, and is sized only when its type is made in several sizes
    /// (<see cref="PartSizes"/>).
    /// </summary>
    public bool PartIsMount => Content is { } content && (uint)Part < (uint)content.Blocks.Types.Count && content.Blocks.Types[Part].Mount is not null;

    private static readonly int[] NoSizes = [];

    /// <summary>
    /// The sizes the part to place is made in, when it mounts and has more than one (<see cref="MountSpec.Sizes"/>: a
    /// cold gas array of one, two by two or three by three nozzles); empty for every other part.
    /// </summary>
    public IReadOnlyList<int> PartSizes =>
        Content is { } content && (uint)Part < (uint)content.Blocks.Types.Count && content.Blocks.Types[Part].Mount is { Sizable: true } spec ? spec.Sizes : NoSizes;

    /// <summary>
    /// The size the next mounted part is placed in: the one chosen for the part's type (<see cref="SetMountSize"/>,
    /// <see cref="CycleMountSize"/>, or copied from a placed one), else the type's default. 1 for a part with one size.
    /// </summary>
    public int MountSize
    {
        get
        {
            if (Content is not { } content || (uint)Part >= (uint)content.Blocks.Types.Count)
                return 1;
            return MountSizeOf(content.Blocks.Types[Part]);
        }
    }

    /// <summary>
    /// The size a part of <paramref name="type"/> would be mounted in if it were chosen now: the size last chosen for
    /// that type, else its default; 1 for a type that is no mount. What a part card's figures are shown for.
    /// </summary>
    public int MountSizeOf(BlockType type) =>
        type.Mount is not { } spec ? 1 : _mountSizeType == type && spec.Allows(_mountSize) ? _mountSize : spec.DefaultSize;

    // The size last chosen, and the type it was chosen for: another type starts at its own default.
    private int _mountSize;
    private BlockType? _mountSizeType;

    /// <summary>Chooses the size the part to place is mounted in. False when the part is not made in that size.</summary>
    public bool SetMountSize(int size)
    {
        if (Content is not { } content || (uint)Part >= (uint)content.Blocks.Types.Count || content.Blocks.Types[Part].Mount is not { Sizable: true } spec || !spec.Allows(size))
            return false;
        _mountSize = size;
        _mountSizeType = content.Blocks.Types[Part];
        if (Placing)
            UpdateGhost();
        return true;
    }

    /// <summary>
    /// Steps the size of the part to place to the next one its type is made in, back to the smallest after the
    /// largest: what the sizing key does for a part that mounts. False for a part with one size.
    /// </summary>
    public bool CycleMountSize()
    {
        var sizes = PartSizes;
        if (sizes.Count == 0 || !CanEdit || !Placing)
            return false;
        var now = MountSize;
        var next = sizes[0];
        for (var i = 0; i < sizes.Count - 1; i++)
            if (sizes[i] == now)
                next = sizes[i + 1];
        return SetMountSize(next);
    }

    /// <summary>
    /// While placing a part that mounts, with a face under the pointer: the mount it would make, snapped to the
    /// face's grid under the pointer, and whether it has room there (<see cref="GhostCheck"/>).
    /// </summary>
    public bool HasMountGhost { get; private set; }
    public DesignMount GhostMount { get; private set; }

    /// <summary>
    /// The planes placing, deleting and mounting are mirrored across, through the centre of the draft's core: a client
    /// preference, nothing of it is sent. Set it with <see cref="SetMirror"/>.
    /// </summary>
    public MirrorAxes Mirror { get; private set; }

    /// <summary>Whether the mirror planes are in effect: some are on and the draft has a core to mirror about.</summary>
    public bool MirrorActive => Mirror != MirrorAxes.None && Draft is { } draft && draft.TryMirrorFrame(out _, out _, out _);

    /// <summary>
    /// While placing with mirror on and a ghost shown: the ghost and its mirrored copies, the original first, and
    /// whether each fits as it would be placed. Fixed room for <see cref="Mirroring.MaxCopies"/>;
    /// empty with mirror off, so the plain ghost is all there is.
    /// </summary>
    public ReadOnlySpan<DesignBox> GhostCopies => _copyBoxes.AsSpan(0, _copyBoxCount);
    public ReadOnlySpan<DraftEdit> GhostCopyChecks => _copyBoxChecks.AsSpan(0, _copyBoxCount);

    /// <summary>As <see cref="GhostCopies"/> for a part that mounts; a copy with no block to go on has parent -1 and is not drawn.</summary>
    public ReadOnlySpan<DesignMount> GhostMountCopies => _copyMounts.AsSpan(0, _copyMountCount);
    public ReadOnlySpan<DraftEdit> GhostMountCopyChecks => _copyMountChecks.AsSpan(0, _copyMountCount);

    /// <summary>How many mirrored copies the latest placement or deletion left out, and when; zero stamps are news only when the time changes.</summary>
    public int MirrorSkipped { get; private set; }
    public float MirrorSkippedAt { get; private set; } = float.NegativeInfinity;

    /// <summary>Debug: the server's last answer to a tank fill (<see cref="EditorReason.FillDone"/>, <see cref="EditorReason.FillOff"/> or <see cref="EditorReason.FillRefused"/>); none before any.</summary>
    public EditorReason FillResult { get; private set; }

    /// <summary>When <see cref="FillResult"/> was given, on <see cref="Time"/>.</summary>
    public float FillResultAt { get; private set; } = float.NegativeInfinity;

    private static bool IsFillReason(EditorReason reason) => reason is EditorReason.FillDone or EditorReason.FillOff or EditorReason.FillRefused;

    /// <summary>
    /// Debug: what the Fill tank button can do now, from the selection and the server's say. The button is offered
    /// only where the server allows it (<see cref="EditorView.DebugFill"/>), while the edit is open, with a tank
    /// selected. To be removed with the button.
    /// </summary>
    public FillOffer FillState
    {
        get
        {
            Span<int> blocks = stackalloc int[EditorRequestMessage.MaxFill];
            return FillTargets(blocks, out _);
        }
    }

    /// <summary>
    /// Debug: works out the fill the selection asks for. Each selected tank block of the draft is matched to the
    /// tank of the edited hull's own design by type, shape and cells; a tank that exists only in the draft has no match and is <see cref="FillOffer.ApplyFirst"/>.
    /// The matched block indices of the hull's design go into <paramref name="blocks"/>, which holds at most
    /// <see cref="EditorRequestMessage.MaxFill"/>.
    /// </summary>
    public FillOffer FillTargets(Span<int> blocks, out int count)
    {
        count = 0;
        if (!CanEdit || !Server.DebugFill || Draft is not { } draft || Source is not { } source)
            return FillOffer.None;
        var offer = EditorFill.Plan(draft, source, Selection, blocks, out count);
        return offer == FillOffer.Ready && Server.Pending ? FillOffer.Waiting : offer;
    }

    /// <summary>Debug: asks the server to fill the selected tanks. False, and nothing is sent, unless the offer is <see cref="FillOffer.Ready"/>.</summary>
    public bool RequestFill(PlayerInputBridge bridge)
    {
        Span<int> blocks = stackalloc int[EditorRequestMessage.MaxFill];
        if (FillTargets(blocks, out var count) != FillOffer.Ready)
            return false;
        bridge.FillTanks(Token, blocks[..count]);
        return true;
    }

    /// <summary>Selected cargo rack in the live design; -2 means Apply first, -1 means no eligible selection.</summary>
    private bool _local;
    public int CargoRack => _local && Server.DebugFill ? SelectedCargoRack : -1;
    public int SelectedCargoRack
    {
        get
        {
            if (!CanEdit || Draft is null || Source is null || Selection.Length != 1) return -1;
            var box = Draft.Boxes[Selection[0]];
            if (box.Type.Cargo <= 0) return -1;
            for (var i = 0; i < Source.Boxes.Length; i++) if (Source.Boxes[i] == box) return i;
            return -2;
        }
    }
    public bool RequestContainer(PlayerInputBridge bridge, int size, int resource)
    {
        var rack = CargoRack;
        if (rack < 0 || Server.Pending) return false;
        bridge.AddContainer(Token, rack, size, resource);
        return true;
    }

    private readonly DesignBox[] _copyBoxes = new DesignBox[Mirroring.MaxCopies];
    private readonly DraftEdit[] _copyBoxChecks = new DraftEdit[Mirroring.MaxCopies];
    private int _copyBoxCount;
    private readonly DesignMount[] _copyMounts = new DesignMount[Mirroring.MaxCopies];
    private readonly DraftEdit[] _copyMountChecks = new DraftEdit[Mirroring.MaxCopies];
    private int _copyMountCount;

    /// <summary>
    /// The step a placed, pasted or moved block's place snaps to across the face it is laid against, in quarter
    /// cells: a whole cell (4), a half (2) or a quarter (1), one of <see cref="BuildDraft.SnapSteps"/>. A client
    /// preference like <see cref="Mirror"/>: nothing of it is sent, and a block's size stays whole cells. Set it with
    /// <see cref="SetSnapStep"/>.
    /// </summary>
    public int SnapStep { get; private set; } = GridPoint.PerCell;

    /// <summary>Chooses the snap step; anything but a step there is leaves it as it was. The ghost follows on the next pick.</summary>
    public void SetSnapStep(int step)
    {
        if (!BuildDraft.IsSnapStep(step) || step == SnapStep)
            return;
        SnapStep = step;
        // A held ghost keeps its place; one that follows the pointer is laid again when the pointer is next picked,
        // which gives the hover its place at the new step.
        UpdateGhost();
    }

    /// <summary>Steps the snap step on: a cell, a half, a quarter, and round to a cell again.</summary>
    public void CycleSnapStep()
    {
        var steps = BuildDraft.SnapSteps;
        var at = steps.IndexOf(SnapStep);
        SetSnapStep(steps[(at + 1) % steps.Length]);
    }

    /// <summary>Turns the mirror planes on or off, any mix, and shows the ghost's copies at once.</summary>
    public void SetMirror(MirrorAxes axes)
    {
        axes &= MirrorAxes.All;
        if (axes == Mirror)
            return;
        Mirror = axes;
        UpdateGhost();
    }

    private readonly double[] _fuelLevels = new double[TankFuel.MaxTanks];
    private int _fuelVersion;
    private int _checkedFuelVersion = -1;
    /// <summary>Whether the snapshot contains the edited hull's tank levels.</summary>
    public bool FuelKnown { get; private set; }
    /// <summary>Fuel discarded by the checked draft, in kilograms per fuel.</summary>
    public FuelTanks FuelDump => FuelPlan.Dumped;
    /// <summary>The current checked refit's fuel balance.</summary>
    public EditFuelPlan FuelPlan { get; private set; }
    /// <summary>Fuel balance if selected tanks are removed or switched to another fuel.</summary>
    public EditFuelPlan MenuFuelPlan { get; private set; }
    private int _checkedSelectionStamp = -1;
    /// <summary>Whether the menu's prediction belongs to this selection and draft.</summary>
    public bool MenuFuelReady => FuelKnown && _checkedSelectionStamp == _selectionStamp && CheckedVersion == Draft?.Version && _checkedFuelVersion == _fuelVersion;
    /// <summary>The next Apply confirms the displayed fuel loss.</summary>
    public bool ConfirmDump { get; private set; }
    /// <summary>Whether the checked edit discards a measurable quantity of fuel.</summary>
    public bool DumpsFuel => FuelKnown && (FuelDump.Nitrogen > 0f || FuelDump.Hydrazine > 0f || FuelDump.Bipropellant > 0f);

    /// <summary>True when the draft can be committed now.</summary>
    private readonly HoldContainer[] _cargo = new HoldContainer[BlockStateStore.MaxContainers];
    private int _cargoCount;
    public ReadOnlySpan<HoldContainer> LiveCargo => _cargo.AsSpan(0, _cargoCount);
    public int CargoUsedUnits
    {
        get { var total = 0; for (var i = 0; i < _cargoCount; i++) total += Content!.Cargo.Sizes[_cargo[i].Size].Units; return total; }
    }
    public double CargoKilograms
    {
        get { var total = 0.0; for (var i = 0; i < _cargoCount; i++) total += _cargo[i].Amount; return total; }
    }
    private readonly HoldContainer[] _draftCargo = new HoldContainer[BlockStateStore.MaxContainers];
    private int _draftCargoCount;
    public ReadOnlySpan<HoldContainer> DraftCargo => CheckedVersion == Draft?.Version ? _draftCargo.AsSpan(0, _draftCargoCount) : default;
    public bool CargoFits { get; private set; } = true;

    public bool CanApply =>
        CanEdit && Dirty && CargoFits && Server.Phase == EditorPhase.Open && !Server.Pending
        && _checkedFuelVersion == _fuelVersion && Server.Token != 0 && Check == DraftCheck.Ready && CheckedVersion == Draft!.Version;

    /// <summary>True when the ship has lost or worn blocks, or was repaired before, so its blueprint can rebuild it.</summary>
    public bool Damaged => Source is { } source && source.NeedsRepair(_ownRevision, _ownHull, _ownDents);

    /// <summary>Why the ship cannot be repaired from the blueprint now, or <see cref="RepairBlock.None"/>.</summary>
    public RepairBlock RepairState =>
        Stage != EditorStage.Editing || Draft is null || Source is null || Server.Phase != EditorPhase.Open || Server.Pending || Server.Token == 0 || Server.Hull != Hull
            ? RepairBlock.Busy
            : !Damaged ? RepairBlock.NotDamaged
            : Dirty ? RepairBlock.Unsaved
            : RepairBlock.None;

    /// <summary>
    /// Asks the server to rebuild the ship as its blueprint, whole, with nothing uploaded. Only from an unchanged
    /// draft, so no change is ever thrown away. The answer arrives as an apply's does.
    /// </summary>
    public bool Repair(PlayerInputBridge bridge)
    {
        if (RepairState != RepairBlock.None)
            return false;
        bridge.RepairEdit(Token);
        Stage = EditorStage.Applying;
        _commitSerial = Server.Serial;
        Refusal = EditorReason.None;
        CancelCarry();
        ClearHover();
        return true;
    }

    /// <summary>
    /// Throws the draft's changes away on the player's word: the draft is the blueprint again, as when the edit
    /// opened, and its history is gone. For a draft kept from before the ship was damaged, which holds Repair back
    /// and can no longer be applied. False, and nothing changed, when there is nothing to throw away.
    /// </summary>
    public bool DiscardChanges()
    {
        ConfirmDump = false;
        if (!CanInspect || !Dirty || Source is null || Content is null)
            return false;
        CancelCarry();
        CancelPartGestures();
        Menu.Close();
        GhostPinned = false;
        Draft = BuildDraft.FromBlueprint(Source, Content);
        _depth = 0;
        DraftKept = DraftRestarted = false;
        ClearSelection();
        CheckedVersion = -1;
        Checked = default;
        Latest = default;
        // A check of the old draft that is still running is not this draft's.
        _generation++;
        _editedAt = _time - CheckDelay;
        Check = DraftCheck.Pending;
        MarkMissing();
        UpdateGhost();
        return true;
    }

    /// <summary>Asks the server to edit the hull the player flies. The editor opens only when it says yes.</summary>
    public void Request(in HullView own, PlayerInputBridge bridge)
    {
        if (Stage != EditorStage.Closed || own.Id == 0)
            return;
        bridge.RequestEdit(own.Id);
        Stage = EditorStage.Requesting;
        _pilotHull = own.Id;
        _awaitedHull = 0;
        _createdHull = 0;
        _askedSerial = Server.Serial;
        _requestedAt = _time;
        Notice = EditorReason.None;
        NoticeApplied = false;
    }

    /// <summary>
    /// Waits for the server's grant to edit a ship it has just created for the player, who stays in the drone
    /// <paramref name="pilot"/>. The grant comes with the creation answer; the draft opens only once the new hull is
    /// in the snapshot, as <see cref="Track"/> finds it.
    /// </summary>
    public void AwaitCreated(uint pilot, uint hull)
    {
        if (Stage != EditorStage.Closed || hull == 0)
            return;
        Stage = EditorStage.Requesting;
        _pilotHull = pilot;
        _awaitedHull = hull;
        _createdHull = hull;
        _requestedAt = _time;
        Notice = EditorReason.None;
        NoticeApplied = false;
    }

    /// <summary>
    /// The editor's keys, read in the shortcut phase before flight: the editor key asks to edit the flown ship or
    /// cancels the edit at any stage, and Escape cancels an edit that is asked for or open. A focused text field
    /// keeps its letters and its Escape. True when a key was used, so nothing else acts on it this frame. The
    /// caller only asks with nothing else open.
    /// </summary>
    public bool HandleKeys(ActionMap actions, InputDevices devices, in HullView own, PlayerInputBridge bridge, bool typing, bool pointerOwned)
    {
        if (ConfirmDiscard)
        {
            // The question has the keyboard, text field or not: Enter, Escape and the editor key all keep the draft.
            if (devices.KeyPressed(GlfwKey.Enter) || devices.KeyPressed(GlfwKey.KpEnter)
                || actions.InterfaceShortcutPressed(devices, InputAction.Pause, false, pointerOwned)
                || actions.InterfaceShortcutPressed(devices, InputAction.ToggleEditor, false, pointerOwned))
                KeepEditing();
            return true;
        }

        if (actions.InterfaceShortcutPressed(devices, InputAction.ToggleEditor, typing, pointerOwned))
        {
            if (Stage == EditorStage.Closed)
                Request(own, bridge);
            else
                Leave(bridge);
            return true;
        }

        if (Stage != EditorStage.Closed && !typing && actions.InterfaceShortcutPressed(devices, InputAction.Pause, typing, pointerOwned))
        {
            // Escape first closes the context menu, then puts back a part or a block being sized or a part turned, then
            // drops what the pointer carries; only with
            // none of those does it end the edit.
            if (ConfirmDump)
                ConfirmDump = false;
            else if (Menu.IsOpen)
                Menu.Close();
            else if (SizingPart)
                CancelPartSizing();
            else if (ResizingBlock)
                CancelBlockResize();
            else if (RotatingPart)
                CancelPartRotation();
            else if (Carry != EditorCarry.None)
                CancelCarry();
            else
                Leave(bridge);
            return true;
        }

        return false;
    }

    /// <summary>
    /// The player's way out of the edit (the editor key, Escape, the Cancel button): ends it at once when nothing
    /// would be lost, otherwise puts up the "Discard changes?" question and leaves everything as it was. True when
    /// the edit ended.
    /// </summary>
    public bool Leave(PlayerInputBridge bridge)
    {
        if (AskDiscard())
            return false;
        Cancel(bridge);
        return true;
    }

    /// <summary>Puts up the "Discard changes?" question when there are changes to lose. True when it is up.</summary>
    public bool AskDiscard()
    {
        if (!WouldLoseChanges)
            return false;
        Menu.Close();
        _confirmDiscard = true;
        return true;
    }

    /// <summary>Answers the question with Keep editing: the draft and everything about it stay as they were.</summary>
    public void KeepEditing() => _confirmDiscard = false;

    /// <summary>The player cancels: whatever stage the edit is in ends, and the draft is thrown away.</summary>
    public void Cancel(PlayerInputBridge bridge)
    {
        ConfirmDump = false;
        _confirmDiscard = false;
        if (Stage == EditorStage.Closed)
            return;
        CancelPartGestures();
        Menu.Close();
        GhostPinned = false;
        bridge.CancelEdit();
        // Cancelling the edit of a ship just built leaves it as it was made, and the player goes to fly it.
        StartFollow(_createdHull);
        Stage = EditorStage.Closed;
        _partPressUsed = false;
        Draft = null;
        DraftKept = false;
        Carry = EditorCarry.None;
        ClearSelection();
        Notice = EditorReason.None;
        _generation++;
    }

    /// <summary>Forgets everything, for a new session; a check still running is discarded when it finishes.</summary>
    public void Reset()
    {
        ConfirmDump = false;
        _confirmDiscard = false;
        CancelPartGestures();
        Menu.Close();
        GhostPinned = false;
        Stage = EditorStage.Closed;
        _partPressUsed = false;
        Draft = null;
        Source = null;
        Content = null;
        Carry = EditorCarry.None;
        ClearSelection();
        _clipCount = 0;
        _clipMountCount = 0;
        Server = default;
        _seenSerial = _askedSerial = _commitSerial = 0;
        _pilotHull = _awaitedHull = _createdHull = _followHull = 0;
        FollowNotice = FollowRefusal.None;
        FollowNoticeAt = float.NegativeInfinity;
        Notice = Refusal = EditorReason.None;
        DraftKept = DraftRestarted = false;
        _replacedFrom = _replacedTo = 0;
        _generation++;
    }

    /// <summary>
    /// The hull an applied edit replaced and the one that replaced it, once, so the view can carry its target
    /// and selection over.
    /// </summary>
    public bool TakeReplacement(out uint from, out uint to)
    {
        from = _replacedFrom;
        to = _replacedTo;
        _replacedFrom = _replacedTo = 0;
        return from != 0 && to != 0;
    }

    /// <summary>
    /// Follows the server's answers, finishes and starts checks. Call once a frame while in space, before the
    /// view reads input.
    /// </summary>
    public void Track(SessionSnapshot snapshot, PlayerInputBridge bridge, float dt)
    {
        _time += dt;
        _local = snapshot.InProcess;
        var view = snapshot.Editor;
        Server = view;
        var fresh = view.Serial != _seenSerial;
        _seenSerial = view.Serial;
        ref var own = ref snapshot.Hulls[snapshot.OwnHull];

        switch (Stage)
        {
            case EditorStage.Requesting when _awaitedHull != 0:
                // A new ship: the grant arrived with the creation answer, and the hull may follow it a moment
                // later. The draft opens only once the hull is here; the player stays in the drone throughout.
                if (own.Id != _pilotHull)
                {
                    bridge.CancelEdit();
                    Close(EditorReason.HullChanged, 0, false);
                }
                else if (view.Phase == EditorPhase.Open && view.Hull == _awaitedHull && view.Token != 0)
                {
                    var at = FindHull(snapshot, _awaitedHull);
                    if (at >= 0)
                        Enter(snapshot, snapshot.Hulls[at], view);
                }
                else if (view.Phase is EditorPhase.Closed or EditorPhase.Applied && view.Reason != EditorReason.Cancelled)
                    Close(view.Reason, view.WaitSeconds, false);

                if (Stage == EditorStage.Requesting && _time - _requestedAt > RequestTimeout)
                {
                    bridge.CancelEdit();
                    Close(EditorReason.Unavailable, 0, false);
                }

                break;

            case EditorStage.Requesting:
                if (view.Serial != _askedSerial && !view.Pending)
                {
                    _askedSerial = view.Serial;
                    if (view.Phase == EditorPhase.Open && view.Hull == own.Id && view.Token != 0)
                        Enter(snapshot, own, view);
                    else if (view.Phase == EditorPhase.Open)
                    {
                        // Granted for a hull the player no longer flies: give it back.
                        bridge.CancelEdit();
                        Close(EditorReason.HullChanged, 0, false);
                    }
                    else if (view.Phase == EditorPhase.Closed && view.Reason != EditorReason.Cancelled)
                        Close(view.Reason, view.WaitSeconds, true);
                    // A cancel's answer from an earlier edit, or a stage of one, is not the answer to this request.
                }

                if (Stage == EditorStage.Requesting && _time - _requestedAt > RequestTimeout)
                {
                    bridge.CancelEdit();
                    Close(EditorReason.Unavailable, 0, true);
                }

                break;

            case EditorStage.Editing:
                // The edited hull is the granted one: the flown ship for an own edit, a ship made from the drone
                // otherwise. Its figures are read from its own record, never the flown hull's.
                var edited = FindHull(snapshot, Hull);
                if (edited >= 0)
                {
                    ref var hull = ref snapshot.Hulls[edited];
                    _ownHull = hull.Hull;
                    _ownRevision = hull.Revision;
                    _ownDents = hull.Visual.Count;
                }

                if (own.Id != _pilotHull || edited < 0)
                {
                    bridge.CancelEdit();
                    Close(EditorReason.HullChanged, 0, false);
                }
                else if (view.Phase is EditorPhase.Closed or EditorPhase.Applied)
                    Close(view.Reason, view.WaitSeconds, true);
                else if (view.Phase == EditorPhase.Open)
                {
                    if (view.Token != 0)
                        Token = view.Token;
                    if (fresh && IsFillReason(view.Reason))
                    {
                        FillResult = view.Reason;
                        FillResultAt = _time;
                    }
                    else if (fresh && view.Reason != EditorReason.None)
                        Refuse(view.Reason, view.WaitSeconds);
                }

                break;

            case EditorStage.Applying:
                if (view.Phase == EditorPhase.Applied)
                {
                    _replacedFrom = view.ReplacedHull;
                    _replacedTo = view.Hull;
                    // The ship just built was replaced by the one the player designed: that is the one to fly.
                    StartFollow(view.Hull);
                    Stage = EditorStage.Closed;
                    _partPressUsed = false;
                    Draft = null;
                    DraftKept = false;
                    ClearSelection();
                    Notice = EditorReason.None;
                    NoticeApplied = true;
                    NoticeBuilt = Hull != _pilotHull;
                    NoticeAt = _time;
                    _generation++;
                }
                else if (view.Phase == EditorPhase.Closed)
                    Close(view.Reason, view.WaitSeconds, true);
                else if (view.Phase == EditorPhase.Open && view.Serial != _commitSerial && !view.Pending)
                {
                    // Turned down, by the server or by the session before it was sent; the edit stays open.
                    Stage = EditorStage.Editing;
                    Token = view.Token;
                    if (view.Reason != EditorReason.None && !IsFillReason(view.Reason))
                        Refuse(view.Reason, view.WaitSeconds);
                }

                break;
        }

        // A part is sized or turned only while the ship may be edited.
        if ((SizingPart || RotatingPart) && !CanEdit)
            CancelPartGestures();
        var known = Source is not null && Hull == own.Id && own.Tanks.Length == Source.Tanks.Length;
        var fuelChanged = known != FuelKnown;
        FuelKnown = known;
        if (known)
            for (var i = 0; i < own.Tanks.Length; i++)
            {
                var level = (double)own.Tanks.Span[i];
                fuelChanged |= _fuelLevels[i] != level;
                _fuelLevels[i] = level;
            }
        var cargo = Hull == own.Id ? own.Cargo.Span : ReadOnlySpan<HoldContainer>.Empty;
        var cargoChanged = _cargoCount != cargo.Length;
        for (var i = 0; i < cargo.Length && !cargoChanged; i++)
        {
            var a = _cargo[i]; var b = cargo[i];
            cargoChanged = a.Rack != b.Rack || a.Size != b.Size || a.Resource != b.Resource || a.Amount != b.Amount
                || a.Hull != b.Hull || a.At.X != b.At.X || a.At.Y != b.At.Y || a.At.Z != b.At.Z;
        }
        if (cargoChanged)
        {
            cargo.CopyTo(_cargo);
            _cargoCount = cargo.Length;
            fuelChanged = true;
        }
        if (fuelChanged)
        {
            _fuelVersion++;
            ConfirmDump = false;
        }
        PumpCheck();
    }

    /// <summary>The index of a hull in the snapshot, or -1 while the snapshot does not have it.</summary>
    private static int FindHull(SessionSnapshot snapshot, uint id)
    {
        for (var i = 0; i < snapshot.HullCount; i++)
            if (snapshot.Hulls[i].Id == id)
                return i;
        return -1;
    }

    /// <summary>The token of the open edit, which a commit carries.</summary>
    public uint Token { get; private set; }

    /// <summary>Commits the checked draft. Allocates a copy of its bytes in the bridge; only on the player's Apply.</summary>
    public bool Apply(PlayerInputBridge bridge)
    {
        if (!CanApply)
            return false;
        if (DumpsFuel && !ConfirmDump)
        {
            ConfirmDump = true;
            return false;
        }
        if (!bridge.CommitEdit(Token, Checked.Bytes))
        {
            Refuse(EditorReason.TooLarge, 0);
            return false;
        }

        Stage = EditorStage.Applying;
        _commitSerial = Server.Serial;
        Refusal = EditorReason.None;
        CancelCarry();
        ClearHover();
        return true;
    }

    /// <summary>The block face under the pointer, or none; places the ghost of the part being placed.</summary>
    public void SetHover(in DraftHit hit)
    {
        // A part being sized keeps the face it was on; the gesture holds the ghost still.
        if (SizingPart)
            return;
        GhostPinned = false;
        HasHover = true;
        Hover = hit;
        UpdateGhost();
    }

    /// <summary>
    /// Nothing of the draft is under the pointer. A part being sized or turned loses its face, so the gesture is put
    /// back, and a ghost held in place lets go.
    /// </summary>
    public void ClearHover()
    {
        CancelPartGestures();
        HasHover = false;
        HasGhost = false;
        HasMountGhost = false;
        HasCarryGhost = false;
        GhostPinned = false;
    }

    /// <summary>
    /// A left click on the world. With copied blocks following the pointer it pastes them; while placing it places
    /// the part where its ghost is; otherwise it selects the block under the pointer as <see cref="Pick"/> does.
    /// </summary>
    public void Click(bool add = false)
    {
        // The rings hold the left button while a part is turned; a press on its ghost ends the turn before placing.
        if (!CanInspect || RotatingPart)
            return;
        if (CanEdit && Carry == EditorCarry.Paste)
        {
            Drop();
            return;
        }

        // A damaged ship is only looked at: a click selects, whatever part was chosen.
        if (!Placing || !CanEdit)
        {
            Pick(add);
            return;
        }

        if (HasMountGhost)
        {
            PlaceMount();
            return;
        }

        if (!HasGhost)
            return;
        PlaceBlock();
    }

    /// <summary>Places the ghost's mounted part and, with mirror on, its copies, as one step.</summary>
    private void PlaceMount()
    {
        var result = Draft!.AddMountMirrored(GhostMount.Parent, GhostMount.Face, GhostMount.U, GhostMount.V, GhostMount.Type, Mirror, out var skipped, GhostMount.Size);
        NoteSkipped(result, skipped);
        Record(result);
    }

    /// <summary>Places the ghost block and, with mirror on, its copies, as one step.</summary>
    private void PlaceBlock()
    {
        var box = new DesignBox(GhostAt, PartSize, Content!.Blocks.Types[Part], PartShape, GhostFacing);
        var result = Draft!.AddMirrored(box, Mirror, out var skipped);
        NoteSkipped(result, skipped);
        Record(result);
    }

    // Copies left out of an edit that was made are news: the notice says how many.
    private void NoteSkipped(DraftEdit result, int skipped)
    {
        if (result != DraftEdit.Done || skipped <= 0)
            return;
        MirrorSkipped = skipped;
        MirrorSkippedAt = _time;
    }

    /// <summary>
    /// Selects the block under the pointer, whether or not a part is being placed: alone, or with
    /// <paramref name="add"/> added to the selection, or taken out of it when it is already in. A pick of empty
    /// space clears the selection, except when adding, which then changes nothing.
    /// </summary>
    public void Pick(bool add)
    {
        if (!CanInspect)
            return;
        if (!HasHover || (uint)Hover.Block >= (uint)Draft!.Boxes.Length)
        {
            if (!add)
                ClearSelection();
            return;
        }

        // A part mounted on the block is nearer than the block: it is what the pointer is on.
        if ((uint)Hover.Mount < (uint)Draft.Mounts.Length)
        {
            SelectMount(Hover.Mount, add);
            return;
        }

        Face = Hover.Side;
        Select(Hover.Block, add);
    }

    /// <summary>Selects a mounted part alone, or with <paramref name="add"/> adds it to the selection or takes it out.</summary>
    public void SelectMount(int mount, bool add)
    {
        if (!CanInspect || (uint)mount >= (uint)Draft!.Mounts.Length)
            return;
        if (!add)
            ClearSelection();
        var at = MountSelection.IndexOf(mount);
        if (at >= 0)
        {
            Array.Copy(_mountSelection, at + 1, _mountSelection, at, _mountSelectionCount - at - 1);
            _mountSelectionCount--;
        }
        else
        {
            if (_mountSelectionCount == _mountSelection.Length)
                Array.Resize(ref _mountSelection, _mountSelection.Length * 2);
            _mountSelection[_mountSelectionCount++] = mount;
        }

        _selectionStamp++;
    }

    /// <summary>Selects a block alone, or with <paramref name="add"/> adds it to the selection or takes it out.</summary>
    public void Select(int block, bool add)
    {
        if (!CanInspect || (uint)block >= (uint)Draft!.Boxes.Length)
            return;
        if (!add)
            SelectOnly(block);
        else if (IsSelected(block))
            Deselect(block);
        else
            AddToSelection(block);
    }

    /// <summary>Selects one block alone and asks the view to show it, for a block the player cannot find.</summary>
    public void Locate(int block)
    {
        if (!CanInspect || (uint)block >= (uint)Draft!.Boxes.Length)
            return;
        SelectOnly(block);
        FocusRequested = true;
    }

    /// <summary>Takes a request to show the selection, once.</summary>
    public bool TakeFocusRequest()
    {
        var requested = FocusRequested;
        FocusRequested = false;
        return requested;
    }

    /// <summary>Forgets the selection.</summary>
    public void ClearSelection()
    {
        for (var i = 0; i < _selectionCount; i++)
            if ((uint)_selection[i] < (uint)(_selectionMarks.Length << 6))
                BlockSet.Remove(_selectionMarks, _selection[i]);
        _selectionCount = 0;
        _mountSelectionCount = 0;
        _selectionStamp++;
        if (Carry == EditorCarry.Move)
            CancelCarry();
    }

    private void SelectOnly(int block)
    {
        ClearSelection();
        AddToSelection(block);
    }

    private void AddToSelection(int block)
    {
        if (IsSelected(block))
            return;
        if (_selectionCount == _selection.Length)
            Array.Resize(ref _selection, _selection.Length * 2);
        var words = BlockSet.WordCount(Math.Max(block + 1, Draft?.Boxes.Length ?? 0));
        if (_selectionMarks.Length < words)
            Array.Resize(ref _selectionMarks, Math.Max(words, _selectionMarks.Length * 2));
        _selection[_selectionCount++] = block;
        BlockSet.Add(_selectionMarks, block);
        _selectionStamp++;
    }

    private void Deselect(int block)
    {
        var at = Selection.IndexOf(block);
        if (at < 0)
            return;
        Array.Copy(_selection, at + 1, _selection, at, _selectionCount - at - 1);
        _selectionCount--;
        BlockSet.Remove(_selectionMarks, block);
        _selectionStamp++;
    }

    /// <summary>
    /// Starts a bulk change of the block selection: sizes its buffers for the whole draft once, so the changes that
    /// follow take no memory however many blocks they touch, and with <see cref="BoxSelectMode.Replace"/> forgets the
    /// selection. Mounted parts are cleared with it, never box selected. False when blocks cannot be selected now.
    /// </summary>
    public bool BeginBulkSelect(BoxSelectMode mode)
    {
        if (!CanInspect)
            return false;
        var count = Draft!.Boxes.Length;
        if (_selection.Length < count)
            Array.Resize(ref _selection, Math.Max(count, _selection.Length * 2));
        var words = BlockSet.WordCount(count);
        if (_selectionMarks.Length < words)
            Array.Resize(ref _selectionMarks, Math.Max(words, _selectionMarks.Length * 2));
        if (mode == BoxSelectMode.Replace)
            ClearSelection();
        return true;
    }

    /// <summary>Puts one block in or takes it out of the selection for a bulk change begun with <see cref="BeginBulkSelect"/>.</summary>
    public void BulkSelect(int block, BoxSelectMode mode)
    {
        if ((uint)block >= (uint)Draft!.Boxes.Length)
            return;
        if (mode == BoxSelectMode.Remove)
            Deselect(block);
        else
            AddToSelection(block);
    }

    /// <summary>Selects every block of the draft. False when blocks cannot be selected now.</summary>
    public bool SelectAll()
    {
        if (!BeginBulkSelect(BoxSelectMode.Replace))
            return false;
        for (var i = 0; i < Draft!.Boxes.Length; i++)
            AddToSelection(i);
        return true;
    }

    /// <summary>
    /// Selects every block of the same type as <paramref name="block"/>, whatever its size, shape or facing: alone, or
    /// with <paramref name="add"/> kept with what is selected already. False when there is no such block.
    /// </summary>
    public bool SelectSameType(int block, bool add = false)
    {
        if (!CanInspect || (uint)block >= (uint)Draft!.Boxes.Length)
            return false;
        var boxes = Draft.Boxes;
        var type = boxes[block].Type;
        BeginBulkSelect(add ? BoxSelectMode.Add : BoxSelectMode.Replace);
        for (var i = 0; i < boxes.Length; i++)
            if (boxes[i].Type == type)
                AddToSelection(i);
        // The one asked about is the one the details panel shows.
        if (_selection[_selectionCount - 1] != block)
            Deselect(block);
        AddToSelection(block);
        return true;
    }

    /// <summary>
    /// The pointer took hold of a selected block and has moved: the selection follows it until it is let go. False,
    /// and nothing carried, when that block is not selected.
    /// </summary>
    public bool BeginMove(int block)
    {
        if (!CanEdit || Carry != EditorCarry.None || !IsSelected(block))
            return false;
        CancelPartGestures();
        _grab = block;
        Carry = EditorCarry.Move;
        UpdateGhost();
        return true;
    }

    /// <summary>
    /// A part card of the part list is being dragged: the part is chosen and its ghost follows the pointer over
    /// the ship until the button is let go. Only the part list calls this; nothing else hands the world a press.
    /// </summary>
    public void BeginPartDrag(int part)
    {
        if (!CanEdit || Carry != EditorCarry.None || _partPressUsed || Content is null || (uint)part >= (uint)Content.Blocks.Types.Count)
            return;
        ChoosePart(part);
        Carry = EditorCarry.Part;
        _partPressUsed = true;
    }

    /// <summary>
    /// The press on a part card ended. One press carries a part once: a drag dropped with Escape is not picked up
    /// again while the button is still held.
    /// </summary>
    public void EndPartPress() => _partPressUsed = false;

    private bool _partPressUsed;

    /// <summary>
    /// Copies the selected blocks, in draft order, with what is mounted on them; the latest picked is the one the
    /// pointer holds on paste. A mounted part selected with no block is not copied to be pasted: it sits on a face,
    /// not in a place of its own, so copying it picks it as the part to place.
    /// </summary>
    public bool CopySelection()
    {
        if (!CanEdit || (_selectionCount == 0 && _mountSelectionCount == 0))
            return false;
        var mounts = Draft!.Mounts;
        if (_selectionCount == 0)
        {
            if ((uint)SelectedMount >= (uint)mounts.Length || Content is null)
                return false;
            var types = Content.Blocks.Types;
            for (var i = 0; i < types.Count; i++)
            {
                if (types[i] != mounts[SelectedMount].Type)
                    continue;
                // The next one is placed in the size of the one copied.
                _mountSize = mounts[SelectedMount].Size;
                _mountSizeType = types[i];
                ChoosePart(i);
                return true;
            }

            return false;
        }

        if (_clip.Length < _selectionCount)
            _clip = new DesignBox[Math.Max(_selectionCount, _clip.Length * 2)];
        var boxes = Draft.Boxes;
        _clipCount = 0;
        _clipMountCount = 0;
        for (var i = 0; i < boxes.Length; i++)
        {
            if (!IsSelected(i))
                continue;
            if (i == Selected)
                _clipAnchor = _clipCount;
            foreach (ref readonly var mount in mounts)
            {
                if (mount.Parent != i)
                    continue;
                if (_clipMountCount == _clipMounts.Length)
                    Array.Resize(ref _clipMounts, Math.Max(16, _clipMounts.Length * 2));
                _clipMounts[_clipMountCount++] = mount with { Parent = _clipCount };
            }

            _clip[_clipCount++] = boxes[i];
        }

        return true;
    }

    /// <summary>Starts pasting: the copied blocks follow the pointer, snapped to the face under it, until a click.</summary>
    public bool BeginPaste()
    {
        if (!CanEdit || _clipCount == 0 || Carry is EditorCarry.Part or EditorCarry.Move)
            return false;
        CancelPartGestures();
        Carry = EditorCarry.Paste;
        UpdateGhost();
        return true;
    }

    /// <summary>
    /// Lets go of what the pointer carries over the world: the part, the moved blocks or the pasted ones go where
    /// their ghost is, if it fits there. Anywhere else, or where it does not fit, nothing changes.
    /// </summary>
    public void Drop()
    {
        var carry = Carry;
        if (carry == EditorCarry.None || !CanEdit)
        {
            CancelCarry();
            return;
        }

        if (carry == EditorCarry.Part)
        {
            Carry = EditorCarry.None;
            if (HasMountGhost && GhostCheck == DraftEdit.Done)
                PlaceMount();
            else if (HasGhost && GhostCheck == DraftEdit.Done)
                PlaceBlock();

            return;
        }

        var fits = HasCarryGhost && CarryCheck == DraftEdit.Done;
        var by = CarryBy;
        Carry = EditorCarry.None;
        HasCarryGhost = false;
        if (!fits)
            return;
        if (carry == EditorCarry.Move)
        {
            // Moved blocks keep their indices, so the selection still names them.
            Record(Draft!.Move(Selection, by));
            return;
        }

        var result = Draft!.Paste(Clipboard, by, ClipboardMounts);
        Record(result);
    }

    /// <summary>Drops what the pointer carries without changing anything.</summary>
    public void CancelCarry()
    {
        Carry = EditorCarry.None;
        HasCarryGhost = false;
        UpdateGhost();
    }

    /// <summary>Chooses the part to place and starts placing it. A part being turned is put back first.</summary>
    public void ChoosePart(int part)
    {
        if (Content is null || (uint)part >= (uint)Content.Blocks.Types.Count)
            return;
        CancelPartGestures();
        GhostPinned = false;
        Part = part;
        Placing = true;

        // A part that may not be as large as the last one was sized comes down to what its type allows.
        var limit = PartMaxSide;
        PartSize = new GridSize(Math.Min(PartSize.X, limit), Math.Min(PartSize.Y, limit), Math.Min(PartSize.Z, limit));
        UpdateGhost();
    }

    /// <summary>Stops placing: a click selects instead.</summary>
    public void StopPlacing()
    {
        CancelPartGestures();
        Placing = false;
        HasGhost = false;
        HasMountGhost = false;
        GhostPinned = false;
    }

    /// <summary>
    /// Sets one axis of the size parts are placed at, within the largest side the chosen part may have; the ghost
    /// snaps to its face again.
    /// </summary>
    public void SetPartSize(int axis, int length)
    {
        var limit = PartMaxSide;
        PartSize = EditorGeometry.With(PartSize, axis, Math.Clamp(length, 1, limit));
        GhostPinned = false;
        UpdateGhost();
    }

    /// <summary>
    /// True while a face of the ghost of the part to place may be dragged to size it: the ghost holds still where it
    /// was when the gesture began. Nothing on the draft changes: <see cref="EndPartSizing"/> keeps the size and the
    /// place for the next placement, which takes a click of its own, and <see cref="CancelPartSizing"/> puts back
    /// the size and place from before.
    /// </summary>
    public bool SizingPart { get; private set; }

    /// <summary>
    /// True while the ghost is held where sizing or a turn left it rather than snapped to the face under the pointer:
    /// from the start of sizing until the pointer finds the ship again (<see cref="SetHover"/>), the hover is lost or
    /// the draft changes. A click places the held ghost.
    /// </summary>
    public bool GhostPinned { get; private set; }

    private GridSize _sizeFromSize;
    private GridPoint _sizeFromAt;
    private GridPoint _pinAt;
    private GridSize _pinSize;

    /// <summary>
    /// Starts sizing the part to place, while its ghost is on a face and the ship may be edited, and holds the ghost
    /// where it is. False when it cannot start.
    /// </summary>
    public bool BeginPartSizing()
    {
        if (SizingPart || RotatingPart || !CanEdit || !Placing || Carry != EditorCarry.None || !HasGhost || PartIsMount)
            return false;
        _sizeFromSize = PartSize;
        _sizeFromAt = GhostAt;
        Pin(GhostAt, PartSize);
        SizingPart = true;
        return true;
    }

    /// <summary>
    /// Moves one face of the ghost being sized to make it <paramref name="length"/> whole cells along that face's
    /// axis, from one to the largest side the chosen part may have. The opposite face stays where it is in the grid: moving a
    /// minus face moves the ghost's low corner with it. A wedge stays the same wedge, its slope stretched.
    /// </summary>
    public void SizePartFace(int side, int length)
    {
        if (!SizingPart || (uint)side > 5u)
            return;
        var axis = side >> 1;
        var limit = PartMaxSide;
        length = Math.Clamp(length, 1, limit);
        var old = EditorGeometry.Of(PartSize, axis);
        if (length == old)
            return;
        var at = GhostAt;
        if ((side & 1) != 0)
            at = at.With(axis, at.Of(axis) + (old - length) * GridPoint.PerCell);
        PartSize = EditorGeometry.With(PartSize, axis, length);
        Pin(at, PartSize);
        UpdateGhost();
    }

    /// <summary>Ends sizing: the size and the place stay, held until the pointer finds the ship again.</summary>
    public void EndPartSizing() => SizingPart = false;

    /// <summary>Puts back the part's size and the ghost's place from before sizing. Nothing when not sizing.</summary>
    public void CancelPartSizing()
    {
        if (!SizingPart)
            return;
        SizingPart = false;
        PartSize = _sizeFromSize;
        if (GhostPinned)
            Pin(_sizeFromAt, _sizeFromSize);
        UpdateGhost();
    }

    private void Pin(GridPoint at, GridSize size)
    {
        GhostPinned = true;
        _pinAt = at;
        _pinSize = size;
    }

    /// <summary>Puts back a part being sized or turned, whichever it is.</summary>
    private void CancelPartGestures()
    {
        CancelPartSizing();
        CancelPartRotation();
        // A block being resized keeps what it has: whatever ended the gesture is not the player's Escape.
        EndBlockResize();
    }

    /// <summary>Chooses the shape parts are placed in. A part being turned is put back first.</summary>
    public void SetPartShape(BlockShape shape)
    {
        if (!BlockShapes.IsValid(shape))
            return;
        // A part that is never slanted keeps its box: the wedge button is off for it, and nothing else asks.
        if (shape != BlockShape.Box && PartBoxOnly)
            return;
        CancelPartGestures();
        GhostPinned = false;
        _partShape = shape;
        UpdateGhost();
    }

    /// <summary>
    /// Turns the part to place a quarter turn about a grid axis, as <see cref="BuildDraft.Rotate"/> turns a placed
    /// block: its shape and its facing turn and its size swaps the other two axes.
    /// </summary>
    public void RotatePart(int axis)
    {
        if ((uint)axis > 2u)
            return;
        var u = (axis + 1) % 3;
        var v = (axis + 2) % 3;
        PartSize = EditorGeometry.With(EditorGeometry.With(PartSize, u, EditorGeometry.Of(PartSize, v)), v, EditorGeometry.Of(PartSize, u));
        // A box turns into a box, so a box-only part can never be turned into a slant.
        if (!PartBoxOnly)
            _partShape = BlockShapes.Rotate(_partShape, axis);
        PartFacing = Facings.Rotate(PartFacing, axis);
        UpdateGhost();
    }

    /// <summary>
    /// True while the part to place is being turned with the rotation rings around its ghost. Nothing on the draft
    /// changes: each turn counts at once, <see cref="EndPartRotation"/> keeps it with nothing to confirm, and
    /// <see cref="CancelPartRotation"/> puts back the shape, size and facing from before.
    /// </summary>
    public bool RotatingPart { get; private set; }

    private GridSize _rotateSize;
    private BlockShape _rotateShape;
    private BlockFacing _rotateFacing;
    private GridPoint _rotateAt;

    /// <summary>
    /// Starts turning the part to place, while its ghost is on a face and the ship may be edited. The ghost keeps
    /// to that face while it turns. False when it cannot start.
    /// </summary>
    public bool BeginPartRotation()
    {
        if (RotatingPart || SizingPart || !CanEdit || !Placing || Carry != EditorCarry.None || !HasGhost || PartIsMount)
            return false;
        _rotateSize = PartSize;
        _rotateShape = _partShape;
        _rotateFacing = PartFacing;
        _rotateAt = GhostAt;
        RotatingPart = true;
        return true;
    }

    /// <summary>
    /// Turns the part being turned by whole quarter turns about a grid axis, negative the other way, as
    /// <see cref="RotatePart"/> turns it: its shape and its facing turn and its size swaps the other two axes.
    /// </summary>
    public void TurnPart(int axis, int turns)
    {
        if (!RotatingPart || (uint)axis > 2u)
            return;
        for (var i = (turns % 4 + 4) % 4; i > 0; i--)
            RotatePart(axis);
    }

    /// <summary>How many turns have been put back, so the view can tell a turn put back from one kept.</summary>
    public int RotationCancels { get; private set; }

    /// <summary>
    /// Ends the turn and keeps it for placing; nothing is placed. With <paramref name="hold"/> the ghost stays where
    /// it was turned until the pointer leaves it for the ship, as after sizing, so a click on it places it there;
    /// without, it follows the pointer again at once. Nothing when no part is being turned.
    /// </summary>
    public void EndPartRotation(bool hold)
    {
        if (!RotatingPart)
            return;
        RotatingPart = false;
        if (hold && HasGhost)
            Pin(GhostAt, PartSize);
        else
            GhostPinned = false;
    }

    /// <summary>Puts back the part's shape, size and facing from before it was turned. Nothing when no part is being turned.</summary>
    public void CancelPartRotation()
    {
        if (!RotatingPart)
            return;
        RotatingPart = false;
        RotationCancels++;
        PartSize = _rotateSize;
        _partShape = _rotateShape;
        PartFacing = _rotateFacing;
        // A held ghost goes back to where it was held, not to where the turn re-laid it.
        if (GhostPinned)
            Pin(_rotateAt, _rotateSize);
        UpdateGhost();
    }

    /// <summary>
    /// What turning the selected block a quarter turn about a grid axis would give, without doing it: refused when
    /// the turned block would overlap another or leave the limits.
    /// </summary>
    public DraftEdit CanRotateSelected(int axis) =>
        CanEdit && (uint)Selected < (uint)Draft!.Boxes.Length && (uint)axis <= 2u ? Draft.CanRotate(Selected, axis) : DraftEdit.NoSuchBlock;

    /// <summary>Turns the selected block a quarter turn about a grid axis, keeping its low corner; one step to undo.</summary>
    public void RotateSelected(int axis)
    {
        if (!CanEdit || (uint)Selected >= (uint)Draft!.Boxes.Length || (uint)axis > 2u)
            return;
        Record(Draft.Rotate(Selected, axis));
    }

    /// <summary>Chooses which side of the selected block resizing moves.</summary>
    public void SetFace(int side) => Face = Math.Clamp(side, 0, 5);

    /// <summary>The context menu a right click on a block or a mounted part opens.</summary>
    public EditorMenu Menu { get; } = new();

    /// <summary>
    /// Opens the context menu at <paramref name="at"/> for the block or mounted part the right click was on. When it
    /// is in the selection the menu acts on the whole selection; otherwise it becomes the selection first. False
    /// when nothing can be inspected.
    /// </summary>
    public bool OpenMenu(float2 at, int block, int mount)
    {
        if (!CanInspect || Carry != EditorCarry.None)
            return false;
        var boxes = Draft!.Boxes;
        if ((uint)mount < (uint)Draft.Mounts.Length)
        {
            if (!IsMountSelected(mount))
                SelectMount(mount, false);
            block = -1;
        }
        else if ((uint)block < (uint)boxes.Length)
        {
            if (!IsSelected(block))
                Select(block, false);
            mount = -1;
        }
        else
            return false;
        RefreshMerge();
        Menu.Open(at, block, mount, _time);
        return true;
    }

    /// <summary>
    /// True when at least one mounted part is selected and every selected one may be in an action group
    /// (<see cref="ActionGroups.Groupable"/>): what the group selector and the menu's Group row are offered on.
    /// </summary>
    public bool SelectionIsGroupable
    {
        get
        {
            if (Draft is not { } draft || _mountSelectionCount == 0)
                return false;
            var mounts = draft.Mounts;
            foreach (var mount in MountSelection)
                if ((uint)mount >= (uint)mounts.Length || !ActionGroups.Groupable(mounts[mount].Type))
                    return false;
            return true;
        }
    }

    /// <summary>
    /// The action group every selected mounted part is in (0 for none), or -1 when they differ or the selection is
    /// not groupable.
    /// </summary>
    public int SelectionGroup
    {
        get
        {
            if (!SelectionIsGroupable)
                return -1;
            var mounts = Draft!.Mounts;
            var group = mounts[MountSelection[0]].Group;
            foreach (var mount in MountSelection)
                if (mounts[mount].Group != group)
                    return -1;
            return group;
        }
    }

    /// <summary>What putting the selected mounted parts in <paramref name="group"/> would answer, without doing it.</summary>
    public DraftEdit CanSetGroup(byte group) =>
        !CanEdit || !SelectionIsGroupable ? DraftEdit.NoGroup : SelectionGroup == group ? DraftEdit.Unchanged : DraftEdit.Done;

    /// <summary>
    /// Puts every selected mounted part in action group <paramref name="group"/> (1 to 10, 0 for none), as one step to
    /// undo. The group is a part of the design, so it travels with Apply like any edit. The selection stays, since no
    /// index moves; the change is never mirrored, as a fuel or a split is not.
    /// </summary>
    public void SetSelectedGroup(byte group)
    {
        if (!CanEdit || !SelectionIsGroupable)
            return;
        CancelCarry();
        Record(Draft!.SetMountGroups(MountSelection, group), keepMountSelection: true);
    }

    /// <summary>
    /// The sizes every selected mounted part is made in, when at least one is selected, all are of one type and that
    /// type has more than one size: what the size selector and the menu's Size row are offered on. Empty otherwise.
    /// </summary>
    public IReadOnlyList<int> SelectionSizes
    {
        get
        {
            if (Draft is not { } draft || _mountSelectionCount == 0)
                return NoSizes;
            var mounts = draft.Mounts;
            if ((uint)MountSelection[0] >= (uint)mounts.Length || mounts[MountSelection[0]].Type.Mount is not { Sizable: true } spec)
                return NoSizes;
            var type = mounts[MountSelection[0]].Type;
            foreach (var mount in MountSelection)
                if ((uint)mount >= (uint)mounts.Length || mounts[mount].Type != type)
                    return NoSizes;
            return spec.Sizes;
        }
    }

    /// <summary>The size every selected mounted part has, or -1 when they differ or the selection has no sizes.</summary>
    public int SelectionSize
    {
        get
        {
            if (SelectionSizes.Count == 0)
                return -1;
            var mounts = Draft!.Mounts;
            int size = mounts[MountSelection[0]].Size;
            foreach (var mount in MountSelection)
                if (mounts[mount].Size != size)
                    return -1;
            return size;
        }
    }

    /// <summary>
    /// What making the selected mounted parts <paramref name="size"/> would answer, without doing it: the draft's own
    /// check (<see cref="BuildDraft.CanSetMountSizes"/>), so a part with no room at that size is said before the click.
    /// </summary>
    public DraftEdit CanSetSize(int size) =>
        !CanEdit || SelectionSizes.Count == 0 ? DraftEdit.NoSize : Draft!.CanSetMountSizes(MountSelection, size);

    /// <summary>
    /// Makes every selected mounted part <paramref name="size"/> (<see cref="BuildDraft.SetMountSizes"/>), as one step
    /// to undo. The selection stays, since no index moves; the change is never mirrored, as a group change is not.
    /// </summary>
    public void SetSelectedSize(int size)
    {
        if (!CanEdit || SelectionSizes.Count == 0)
            return;
        CancelCarry();
        Record(Draft!.SetMountSizes(MountSelection, size), keepMountSelection: true);
    }

    /// <summary>Selects every mounted part of the type of mounted part <paramref name="mount"/>, replacing the selection.</summary>
    public bool SelectSameMountType(int mount)
    {
        if (!CanInspect || (uint)mount >= (uint)Draft!.Mounts.Length)
            return false;
        var mounts = Draft.Mounts;
        var type = mounts[mount].Type;
        ClearSelection();
        for (var i = 0; i < mounts.Length; i++)
            if (mounts[i].Type == type && i != mount)
                SelectMount(i, true);
        SelectMount(mount, true);
        return true;
    }

    /// <summary>Writes the fuels the craft has a tank type for into <paramref name="into"/> and returns how many.</summary>
    public int FuelChoices(Span<FuelKind> into) => Draft?.TankFuels(into) ?? 0;

    /// <summary>True when at least one block is selected and every selected block is a tank.</summary>
    public bool SelectionIsTanks
    {
        get
        {
            if (Draft is not { } draft || _selectionCount == 0)
                return false;
            var boxes = draft.Boxes;
            foreach (var block in Selection)
                if ((uint)block >= (uint)boxes.Length || boxes[block].Type.Tank == FuelKind.None)
                    return false;
            return true;
        }
    }

    /// <summary>The fuel every selected tank holds, or none when they differ or the selection is no tanks.</summary>
    public FuelKind SelectionFuel
    {
        get
        {
            if (!SelectionIsTanks)
                return FuelKind.None;
            var boxes = Draft!.Boxes;
            var fuel = boxes[Selection[0]].Type.Tank;
            foreach (var block in Selection)
                if (boxes[block].Type.Tank != fuel)
                    return FuelKind.None;
            return fuel;
        }
    }

    /// <summary>What making the selected tanks hold <paramref name="fuel"/> would answer, without doing it.</summary>
    public DraftEdit CanChangeFuel(FuelKind fuel) =>
        CanEdit && _selectionCount > 0 ? Draft!.CanSwapTank(Selection, fuel) : DraftEdit.NoSuchBlock;

    /// <summary>
    /// Makes every selected tank hold <paramref name="fuel"/>, as one step to undo: the block takes the tank type of
    /// that fuel for this craft and keeps its place, size and facing. Never mirrored. A changed tank starts empty
    /// when the edit is applied, as any tank the edit replaces does (<c>CarryFuel</c>: same type, shape and cells).
    /// </summary>
    public void ChangeFuel(FuelKind fuel)
    {
        if (!CanEdit || _selectionCount == 0)
            return;
        CancelCarry();
        Record(Draft!.SwapTank(Selection, fuel));
    }

    /// <summary>
    /// How many pieces cutting the selected tanks along <paramref name="axis"/> makes: the cells of each tank that is
    /// over one cell long on it, added up. Zero when none is.
    /// </summary>
    public int SplitPieces(int axis)
    {
        if (Draft is not { } draft)
            return 0;
        var boxes = draft.Boxes;
        var pieces = 0;
        foreach (var block in Selection)
        {
            if ((uint)block >= (uint)boxes.Length)
                continue;
            var length = EditorGeometry.Of(boxes[block].Size, axis);
            if (length > 1)
                pieces += length;
        }

        return pieces;
    }

    /// <summary>What cutting the selected tanks along <paramref name="axis"/> would answer, without doing it.</summary>
    public DraftEdit CanSplit(int axis) =>
        CanEdit && _selectionCount > 0 ? Draft!.CanSplit(Selection, axis) : DraftEdit.NoSuchBlock;

    /// <summary>
    /// Cuts every selected tank over one cell long on <paramref name="axis"/> into pieces one cell thick, as one step
    /// to undo, and selects all the pieces. Never mirrored. The pieces start empty when the edit is applied, as a
    /// changed tank does.
    /// </summary>
    public void SplitSelected(int axis)
    {
        if (!CanEdit || _selectionCount == 0)
            return;
        CancelCarry();
        var before = Draft!.Boxes.Length;
        var result = Draft.Split(Selection, axis);
        Record(result);
        if (result != DraftEdit.Done || !BeginBulkSelect(BoxSelectMode.Add))
            return;
        for (var i = before; i < Draft.Boxes.Length; i++)
            BulkSelect(i, BoxSelectMode.Add);
    }

    /// <summary>Why the face-drag resize cannot start on the selection now.</summary>
    public ResizeOffer ResizeState =>
        !CanEdit || Carry != EditorCarry.None || RotatingPart || SizingPart ? ResizeOffer.Busy
        : _mountSelectionCount > 0 && _selectionCount == 0 ? ResizeOffer.Mount
        : _selectionCount == 0 ? ResizeOffer.None
        : _selectionCount > 1 ? ResizeOffer.Several
        : ResizeOffer.Yes;

    /// <summary>True while a face of the selected block is dragged to resize it (S held, or started from the menu).</summary>
    public bool ResizingBlock { get; private set; }

    /// <summary>True when the resize began from the menu, so it does not end when S is not held.</summary>
    public bool ResizeByMenu { get; private set; }

    /// <summary>The block being resized.</summary>
    public int ResizingIndex { get; private set; } = -1;

    // Whether the gesture has made its one step yet: later changes of the size join it.
    private bool _resizeStarted;

    /// <summary>The menu asked for the resize gesture; the view starts it on its next update.</summary>
    public bool ResizeRequested { get; private set; }

    /// <summary>Asks the view to start the face-drag resize on the selected block, as S does.</summary>
    public void RequestResize()
    {
        if (ResizeState == ResizeOffer.Yes)
            ResizeRequested = true;
    }

    /// <summary>Takes a request to start resizing, once.</summary>
    public bool TakeResizeRequest()
    {
        var requested = ResizeRequested;
        ResizeRequested = false;
        return requested;
    }

    /// <summary>
    /// Starts dragging a face of the one selected block to resize it, as S does for the ghost of a part. Nothing on
    /// the draft changes until a face is dragged; every change from then on is the same one step to undo. False when
    /// it cannot start (see <see cref="ResizeState"/>).
    /// </summary>
    public bool BeginBlockResize(bool byMenu)
    {
        if (ResizingBlock || ResizeState != ResizeOffer.Yes || (uint)Selected >= (uint)Draft!.Boxes.Length)
            return false;
        ResizingBlock = true;
        ResizeByMenu = byMenu;
        ResizingIndex = Selected;
        _resizeStarted = false;
        Menu.Close();
        return true;
    }

    /// <summary>The block being resized, as it stands now.</summary>
    public DesignBox ResizingBox => Draft!.Boxes[ResizingIndex];

    /// <summary>The most cells the block being resized may measure along a side: its type's limit.</summary>
    public int ResizingMaxSide => Content is { } content && Draft is { } draft && (uint)ResizingIndex < (uint)draft.Boxes.Length
        ? BoxDesign.MaxSide(draft.Boxes[ResizingIndex].Type, content.Blocks.Limits)
        : 1;

    /// <summary>
    /// Moves one face of the block being resized to make it <paramref name="length"/> whole cells along that face's
    /// axis, from one to its type's largest side, the opposite face staying where it is. Where the draft's own checks
    /// refuse that length it stops at the nearest length that fits and never overlaps. Returns the length the block
    /// has now. Every change joins the gesture's one undo step.
    /// </summary>
    public int SizeBlockFace(int side, int length)
    {
        if (!ResizingBlock || !CanEdit || (uint)ResizingIndex >= (uint)Draft!.Boxes.Length || (uint)side > 5u)
            return 0;
        var axis = side >> 1;
        var current = EditorGeometry.Of(Draft.Boxes[ResizingIndex].Size, axis);
        length = Math.Clamp(length, 1, ResizingMaxSide);
        var step = length > current ? -1 : 1;
        while (length != current && Draft.CanResize(ResizingIndex, side, length) != DraftEdit.Done)
            length += step;
        if (length == current || Draft.Resize(ResizingIndex, side, length) != DraftEdit.Done)
            return current;
        if (_resizeStarted)
            Draft.JoinLatest();
        else
        {
            _resizeStarted = true;
            _depth++;
        }

        LastEdit = DraftEdit.Done;
        LastEditAt = _time;
        Changed(gesture: true);
        return length;
    }

    /// <summary>Ends the resize, keeping the size the block has.</summary>
    public void EndBlockResize()
    {
        ResizingBlock = false;
        ResizeByMenu = false;
        ResizingIndex = -1;
        _resizeStarted = false;
    }

    /// <summary>Ends the resize and puts the block back as it was before the gesture, with nothing left to redo.</summary>
    public void CancelBlockResize()
    {
        if (!ResizingBlock)
            return;
        if (_resizeStarted && Draft is { } draft && draft.Retract())
        {
            _depth--;
            EndBlockResize();
            Changed(gesture: true);
            return;
        }

        EndBlockResize();
    }

    /// <summary>Works out again, when the draft or the selection changed, whether the selection can be merged.</summary>
    public void RefreshMerge()
    {
        if (Draft is null || (_mergeDraftVersion == Draft.Version && _mergeSelectionStamp == _selectionStamp))
            return;
        _mergeDraftVersion = Draft.Version;
        _mergeSelectionStamp = _selectionStamp;
        MergeCheck = Draft.CanMerge(Selection, out var merged);
        Merged = merged;
        if (MergeCheck != DraftEdit.Done || Content is null)
            return;
        // What the blocks add now against what the one block will add: a size rule that is not linear changes it.
        var boxes = Draft.Boxes;
        var before = default(PartFigures);
        foreach (var block in Selection)
        {
            var box = boxes[block];
            before += PartFigures.Of(box.Type, (long)box.Size.X * box.Size.Y * box.Size.Z, Content.Rules, box.Shape);
        }

        MergeBefore = before;
        MergeAfter = PartFigures.Of(merged.Type, (long)merged.Size.X * merged.Size.Y * merged.Size.Z, Content.Rules);
    }

    /// <summary>With <see cref="MergeCheck"/> done: the figures of the selected blocks together, and of the block a merge makes.</summary>
    public PartFigures MergeBefore { get; private set; }
    public PartFigures MergeAfter { get; private set; }

    /// <summary>Makes one block of the selection, as one step to undo; the merged block is then selected.</summary>
    public void MergeSelected()
    {
        if (!CanEdit || _selectionCount < 2)
            return;
        var lowest = int.MaxValue;
        foreach (var block in Selection)
            lowest = Math.Min(lowest, block);
        var result = Draft!.Merge(Selection);
        Record(result);
        if (result == DraftEdit.Done)
            SelectOnly(lowest);
    }

    /// <summary>The selected block's length along the axis of <see cref="Face"/>, or 0 with none selected.</summary>
    public int SelectedLength => Draft is { } draft && (uint)Selected < (uint)draft.Boxes.Length
        ? EditorGeometry.Of(draft.Boxes[Selected].Size, Face >> 1)
        : 0;

    /// <summary>What resizing the selected block to a length would give, without doing it.</summary>
    public DraftEdit CanResizeSelected(int length) =>
        CanEdit && (uint)Selected < (uint)Draft!.Boxes.Length ? Draft.CanResize(Selected, Face, length) : DraftEdit.NoSuchBlock;

    /// <summary>Resizes the selected block along its chosen face, keeping the opposite face where it is.</summary>
    public void ResizeSelected(int length)
    {
        if (!CanEdit || (uint)Selected >= (uint)Draft!.Boxes.Length)
            return;
        Record(Draft.Resize(Selected, Face, length));
    }

    /// <summary>True when the selection may be deleted: there is one, of blocks or mounted parts, and it leaves at least one block.</summary>
    public bool CanDelete => CanEdit && (_selectionCount > 0 || _mountSelectionCount > 0) && _selectionCount < Draft!.Boxes.Length;

    /// <summary>
    /// Deletes every selected block, with what is mounted on it, and every selected mounted part, as one step to undo.
    /// </summary>
    public void DeleteSelected()
    {
        if (!CanDelete)
            return;
        CancelCarry();
        var result = Draft!.DeleteMirrored(Selection, MountSelection, Mirror);
        if (result == DraftEdit.Done)
            ClearSelection();
        Record(result);
    }

    /// <summary>
    /// Replaces the whole draft by the blocks and parts of a loaded design, as one step to undo (and redo). The ship
    /// is not touched until Apply; the selection is cleared, since every block is another one now.
    /// </summary>
    public DraftEdit ReplaceDraft(BoxDesign design)
    {
        if (!CanEdit)
            return DraftEdit.Unchanged;
        CancelCarry();
        var result = Draft!.Replace(design.Blocks, design.Mounts);
        if (result == DraftEdit.Done)
            ClearSelection();
        Record(result);
        return result;
    }

    /// <summary>Takes back the latest edit. The selection is cleared: the blocks it named may be other blocks now.</summary>
    public void Undo()
    {
        if (!CanEdit)
            return;
        CancelCarry();
        if (!Draft!.Undo())
            return;
        _depth--;
        ClearSelection();
        Changed();
    }

    /// <summary>Makes the latest undone edit again; the selection is cleared, as for an undo.</summary>
    public void Redo()
    {
        if (!CanEdit)
            return;
        CancelCarry();
        if (!Draft!.Redo())
            return;
        _depth++;
        ClearSelection();
        Changed();
    }

    private void Record(DraftEdit result, bool keepMountSelection = false)
    {
        LastEdit = result;
        LastEditAt = _time;
        if (result != DraftEdit.Done)
            return;
        _depth++;
        Changed(keepMountSelection: keepMountSelection);
    }

    // `keepMountSelection` is for an edit that moves no mount's index (a group change), so the selection still names
    // the same parts and the details panel keeps showing them.
    private void Changed(bool gesture = false, bool keepMountSelection = false)
    {
        ConfirmDump = false;
        // The ghost's face may have moved or gone: a part being sized or turned is put back, and a held ghost lets go.
        // The face drag of a block is made of edits and goes on through its own.
        if (!gesture)
        {
            CancelPartGestures();
            Menu.Close();
        }

        GhostPinned = false;
        _editedAt = _time;
        Check = DraftCheck.Pending;
        // An edit may take mounts away with it, which moves the others down: mounted parts are picked again after one.
        if (_mountSelectionCount > 0 && !keepMountSelection)
        {
            _mountSelectionCount = 0;
            _selectionStamp++;
        }

        // Every edit that shifts indices sets the selection itself; this only guards against one left past the end.
        var count = Draft!.Boxes.Length;
        for (var i = 0; i < _selectionCount; i++)
        {
            if ((uint)_selection[i] < (uint)count)
                continue;
            ClearSelection();
            break;
        }

        var words = BlockSet.WordCount(count);
        if (_selectionMarks.Length < words)
            Array.Resize(ref _selectionMarks, Math.Max(words, _selectionMarks.Length * 2));
        MarkMissing();
        UpdateGhost();
    }

    private void UpdateGhost()
    {
        HasGhost = false;
        HasMountGhost = false;
        HasCarryGhost = false;
        _copyBoxCount = 0;
        _copyMountCount = 0;
        if (!HasHover || !CanEdit || (uint)Hover.Block >= (uint)Draft!.Boxes.Length)
            return;
        var boxes = Draft.Boxes;
        switch (Carry)
        {
            case EditorCarry.Move when (uint)_grab < (uint)boxes.Length:
                {
                    // The held block is laid against the face under the pointer; the rest of the group keeps its place
                    // around it.
                    var held = boxes[_grab];
                    var at = Draft.Snap(Hover.Block, Hover.Side, held.Size, Hover.Cell, SnapStep);
                    CarryBy = at - held.At;
                    CarryCheck = Draft.CanMove(Selection, CarryBy);
                    HasCarryGhost = true;
                    return;
                }
            case EditorCarry.Paste when _clipCount > 0:
                {
                    var held = _clip[_clipAnchor];
                    var at = Draft.Snap(Hover.Block, Hover.Side, held.Size, Hover.Cell, SnapStep);
                    CarryBy = at - held.At;
                    CarryCheck = Draft.CanPaste(Clipboard, CarryBy);
                    HasCarryGhost = true;
                    return;
                }
            case EditorCarry.Move or EditorCarry.Paste:
                return;
        }

        if (!Placing)
            return;
        if (PartIsMount)
        {
            // A part that mounts goes on the face under the pointer, where the pointer is on it, and points out of it.
            var type = Content!.Blocks.Types[Part];
            var face = (BlockFacing)Hover.Side;
            var size = MountSize;
            Draft.SnapMount(Hover.Block, face, type, Hover.Point, out var u, out var v, size);
            GhostMount = new DesignMount(Hover.Block, face, u, v, type, Size: (byte)size);
            GhostCheck = Draft.CanAddMount(Hover.Block, face, u, v, type, size);
            HasMountGhost = true;
            if (Mirror != MirrorAxes.None)
            {
                _copyMountCount = Draft.MirrorMounts(GhostMount, Mirror, _copyMounts);
                Draft.CheckMounts(_copyMounts.AsSpan(0, _copyMountCount), _copyMountChecks);
                if (_copyMountCount == 1)
                    _copyMountCount = 0;
            }

            return;
        }

        if (GhostPinned)
        {
            // A held ghost stays where it is held. Turned, its new size is laid against its face again, centred
            // where its middle was, so it stays on the face without jumping to the pointer.
            if (PartSize != _pinSize)
            {
                var middle = _pinAt + new GridSize((_pinSize.X - 1) >> 1, (_pinSize.Y - 1) >> 1, (_pinSize.Z - 1) >> 1);
                Pin(Draft.Snap(Hover.Block, Hover.Side, PartSize, middle, SnapStep), PartSize);
            }

            GhostAt = _pinAt;
        }
        else
            GhostAt = Draft.Snap(Hover.Block, Hover.Side, PartSize, Hover.Cell, SnapStep);
        GhostCheck = Draft.CanAdd(GhostAt, PartSize, Content!.Blocks.Types[Part], PartShape);
        HasGhost = true;
        if (Mirror != MirrorAxes.None)
        {
            var box = new DesignBox(GhostAt, PartSize, Content.Blocks.Types[Part], PartShape, GhostFacing);
            _copyBoxCount = Draft.MirrorBoxes(box, Mirror, _copyBoxes);
            Draft.CheckBoxes(_copyBoxes.AsSpan(0, _copyBoxCount), _copyBoxChecks);
            if (_copyBoxCount == 1)
                _copyBoxCount = 0;
        }
    }

    /// <summary>
    /// Marks the draft's boxes the ship has lost: a box of the blueprint the hull no longer has, wherever it now
    /// stands in the draft. Runs on an edit, never in a frame.
    /// </summary>
    private void MarkMissing()
    {
        MissingCount = 0;
        var boxes = Draft!.Boxes;
        var words = BlockSet.WordCount(boxes.Length);
        if (_missing.Length < words)
            _missing = new ulong[Math.Max(words, _missing.Length * 2)];
        Array.Clear(_missing);
        if (Source is not { } source || _alive.Length == 0)
            return;
        var blueprint = source.Boxes;
        for (var j = 0; j < source.BlockCount; j++)
        {
            if (BlockSet.Has(_alive, j))
                continue;
            // Boxes of a draft never overlap, so at most one box equals the lost one; it is usually still at its index.
            var lost = blueprint[j];
            var at = j < boxes.Length && boxes[j] == lost ? j : boxes.IndexOf(lost);
            if (at < 0)
                continue;
            BlockSet.Add(_missing, at);
            MissingCount++;
        }
    }

    private void Enter(SessionSnapshot snapshot, in HullView own, in EditorView view)
    {
        // Every edit, however it came to open, starts in the select tool: no part is held and no ghost shows.
        StopPlacing();
        Menu.Close();
        _confirmDiscard = false;
        Token = view.Token;
        Hull = own.Id;
        Content = snapshot.Content;
        DraftRestarted = false;
        // Damage changes the live blocks, not the blueprint that the player's draft edits.
        var reuse = Draft is not null && _sourceHull == own.Id && _sourceDesign == own.Design;
        if (!reuse)
        {
            DraftRestarted = Draft is not null && DraftKept;
            Source = snapshot.Content.Designs[own.Design];
            // The whole blueprint, lost blocks included: what is missing is marked, and Repair restores it.
            Draft = BuildDraft.FromBlueprint(Source, snapshot.Content);
            _depth = 0;
            _sourceHull = own.Id;
            _sourceDesign = own.Design;
            ClearSelection();
            CheckedVersion = -1;
            Checked = default;
            Latest = default;
            if (Part >= snapshot.Content.Blocks.Types.Count)
                Part = 0;
        }

        _alive = own.Blocks.Span.ToArray();
        DraftKept = false;
        Stage = EditorStage.Editing;
        _ownHull = own.Hull;
        _ownRevision = own.Revision;
        _ownDents = own.Visual.Count;
        Notice = Refusal = EditorReason.None;
        NoticeApplied = false;
        Carry = EditorCarry.None;
        FocusRequested = false;
        MarkMissing();
        ClearHover();
        // The first check runs at once; it gives the starting figures.
        _editedAt = _time - CheckDelay;
        if (CheckedVersion != Draft!.Version)
            Check = DraftCheck.Pending;
    }

    private void Close(EditorReason reason, int wait, bool keep)
    {
        ConfirmDump = false;
        _confirmDiscard = false;
        // The server or a change of ship ended the edit, not the player: they stay where they are.
        _createdHull = 0;
        CancelPartGestures();
        Menu.Close();
        Stage = EditorStage.Closed;
        _partPressUsed = false;
        Notice = reason;
        NoticeWait = wait;
        NoticeAt = _time;
        NoticeApplied = false;
        // A new ship cannot be reopened from the drone, so its unsaved draft is never kept for "press the key to go on".
        keep &= Hull == _pilotHull;
        DraftKept = keep && Draft is not null && Dirty;
        if (!keep)
            Draft = null;
        Carry = EditorCarry.None;
        ClearHover();
        _generation++;
    }

    /// <summary>Starts waiting to put the player in control of <paramref name="hull"/> when it is one just built; ends the built ship's edit.</summary>
    private void StartFollow(uint hull)
    {
        if (_createdHull == 0 || hull == 0)
            return;
        _createdHull = 0;
        _followHull = hull;
        _followAt = _time;
        FollowNotice = FollowRefusal.None;
    }

    /// <summary>
    /// Ends the wait for the built ship's control: the view asked for it, or could not, and says why. Zero reason
    /// means the request went out.
    /// </summary>
    public void FinishFollow(FollowRefusal reason)
    {
        _followHull = 0;
        FollowNotice = reason;
        FollowNoticeAt = _time;
    }

    private void Refuse(EditorReason reason, int wait)
    {
        Refusal = reason;
        RefusalWait = wait;
        RefusalAt = _time;
    }

    /// <summary>Takes a finished check if it is still for this draft, and starts the next one when it is due.</summary>
    private void PumpCheck()
    {
        if (_jobRunning && jobs.IsComplete(_handle))
        {
            _jobRunning = false;
            if (_job.Generation == _generation && Draft is not null && _job.Version == Draft.Version && _job.FuelVersion == _fuelVersion)
            {
                CheckedVersion = _job.Version;
                _checkedFuelVersion = _job.FuelVersion;
                FuelPlan = _job.Plan;
                CargoFits = _job.CargoFits;
                _draftCargoCount = CargoFits ? _job.CargoCount : 0;
                _job.PlannedCargo.AsSpan(0, _draftCargoCount).CopyTo(_draftCargo);
                if (_job.SelectionStamp == _selectionStamp)
                {
                    MenuFuelPlan = _job.MenuPlan;
                    _checkedSelectionStamp = _job.SelectionStamp;
                }
                Latest = _job.Result;
                if (_job.Failure is { } failure)
                {
                    Check = DraftCheck.Failed;
                    CheckProblem = failure;
                }
                else if (_job.Result.Ok)
                {
                    Check = DraftCheck.Ready;
                    Checked = _job.Result;
                    CheckProblem = "";
                }
                else
                {
                    Check = DraftCheck.Invalid;
                    CheckProblem = Plain(_job.Result.Problem);
                }
            }
        }

        // Only one check at a time, on a frozen copy; a newer edit waits for the running one and then starts again.
        if (Stage == EditorStage.Editing && Draft is not null && !_jobRunning && (CheckedVersion != Draft.Version || _checkedFuelVersion != _fuelVersion || _checkedSelectionStamp != _selectionStamp) && _time - _editedAt >= CheckDelay)
        {
            Check = DraftCheck.Pending;
            // Automatic cargo/fuel updates reuse the frozen geometry and its export. Only a player edit
            // needs another copy; otherwise a tractor stow would allocate on the render thread.
            _job.Rebuild = _job.Copy is null || _job.Version != Draft.Version || _job.Generation != _generation || _job.Failure is not null;
            if (_job.Rebuild) _job.Copy = Draft.Copy();
            _job.Source = FuelKnown ? Source : null;
            _job.Content = Content;
            _job.CargoCount = _cargoCount;
            _cargo.AsSpan(0, _cargoCount).CopyTo(_job.Cargo);
            _fuelLevels.CopyTo(_job.Levels, 0);
            _job.FuelVersion = _fuelVersion;
            if (_job.SelectionStamp != _selectionStamp) _job.Selection = Selection.ToArray();
            _job.SelectionStamp = _selectionStamp;
            _job.Version = Draft.Version;
            _job.Generation = _generation;
            _handle = jobs.Schedule(_job, 2);
            _jobRunning = true;
        }
    }

    /// <summary>A check's problem as a sentence for the player: the design's internal name is left off the front.</summary>
    private static string Plain(string? problem)
    {
        if (string.IsNullOrEmpty(problem))
            return "The design is not valid.";
        var text = problem.StartsWith("draft: ", StringComparison.Ordinal) ? problem[7..] : problem;
        if (text.StartsWith("blocks: ", StringComparison.Ordinal))
            text = text[8..];
        return text.Length > 0 ? char.ToUpperInvariant(text[0]) + text[1..] + (text.EndsWith('.') ? "" : ".") : "The design is not valid.";
    }

    /// <summary>Checks one frozen draft as the server would. Reused: one runs at a time.</summary>
    private sealed class CheckJob : IJob
    {
        public HullDesign? Source;
        public GameContent? Content;
        public readonly HoldContainer[] Cargo = new HoldContainer[BlockStateStore.MaxContainers];
        public int CargoCount;
        public bool CargoFits;
        public readonly HoldContainer[] PlannedCargo = new HoldContainer[BlockStateStore.MaxContainers];
        public readonly double[] Levels = new double[TankFuel.MaxTanks];
        public int FuelVersion;
        public EditFuelPlan Plan;
        public EditFuelPlan MenuPlan;
        public int[] Selection = [];
        public int SelectionStamp;
        public DraftCopy? Copy;
        public bool Rebuild;
        private byte[] _occupied = [];
        public int Version;
        public int Generation;
        public DraftExport Result;
        public string? Failure;

        private static FuelTanks Difference(FuelTanks a, FuelTanks b) => new()
        {
            Nitrogen = Math.Max(0, a.Nitrogen - b.Nitrogen),
            Hydrazine = Math.Max(0, a.Hydrazine - b.Hydrazine),
            Bipropellant = Math.Max(0, a.Bipropellant - b.Bipropellant),
        };

        public void Run()
        {
            Failure = null;
            if (Rebuild) Result = default;
            Plan = MenuPlan = default;
            CargoFits = true;
            try
            {
                if (Rebuild)
                {
                    Result = Copy!.Export();
                    if (Result.Ok)
                    {
                        var capacity = 0;
                        foreach (var box in Result.Design!.Boxes)
                            if (box.Type.Cargo > 0) capacity = Math.Max(capacity, CargoPacking.Capacity(Content!.Cargo, box.Size, Result.Design.BlockSize));
                        if (_occupied.Length < capacity) _occupied = new byte[capacity];
                    }
                }
                if (Result.Ok && Source is not null)
                {
                    if (CargoCount > 0)
                    {
                        CargoFits = CargoTransfer.TryPlan(Content!, Source, Result.Design!, Cargo.AsSpan(0, CargoCount), PlannedCargo, _occupied);
                    }
                    Span<double> levels = stackalloc double[TankFuel.MaxTanks];
                    Plan = EditFuel.Plan(Source, Levels, Result.Design!, levels);
                    var selectedFuel = 0.0;
                    for (var tank = 0; tank < Result.Design!.Tanks.Length; tank++)
                        if (Selection.AsSpan().Contains(Result.Design.Tanks[tank].Block)) selectedFuel += levels[tank];
                    if (selectedFuel > 0)
                    {
                        var removed = EditFuel.Plan(Source, Levels, Result.Design, levels, Selection);
                        MenuPlan = removed with { Moved = Difference(removed.Moved, Plan.Moved), Dumped = Difference(removed.Dumped, Plan.Dumped) };
                    }
                }
            }
            catch (Exception exception)
            {
                // Anything other than a rule the design breaks is a fault in the check; say so rather than guess.
                Logs.App.Error($"Could not check the ship design: {exception}");
                Failure = "The design could not be checked. Try another change, or cancel the edit.";
            }
        }
    }
}
