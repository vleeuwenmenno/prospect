using Brutal.Concurrency.Jobs;
using Prospect.Client.Input;
using Prospect.Engine.Diagnostics;
using Prospect.Engine.Jobs;
using Prospect.Game.Client;
using Prospect.Game.Protocol;
using Prospect.Game.World;

namespace Prospect.Client.World;

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

    // The copied blocks, where they were, and which of them the pointer holds when they are pasted.
    private DesignBox[] _clip = [];
    private int _clipCount;
    private int _clipAnchor;

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

    /// <summary>Seconds since this editor started, the clock its notices are timed by.</summary>
    public float Time => _time;

    /// <summary>Why the editor last closed or was refused, shown for a while after; none when there is nothing to say.</summary>
    public EditorReason Notice { get; private set; }

    /// <summary>With <see cref="Notice"/>: true when the ship was rebuilt rather than an edit refused.</summary>
    public bool NoticeApplied { get; private set; }

    /// <summary>With <see cref="NoticeApplied"/>: true when the applied edit built a new ship beside the drone, which keeps flying.</summary>
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

    /// <summary>What the pointer carries.</summary>
    public EditorCarry Carry { get; private set; }

    /// <summary>With <see cref="EditorCarry.Move"/> or <see cref="EditorCarry.Paste"/>: the cells the blocks would move by, and whether that fits.</summary>
    public bool HasCarryGhost { get; private set; }
    public GridSize CarryBy { get; private set; }
    public DraftEdit CarryCheck { get; private set; }

    /// <summary>The copied blocks, as they were in the draft when copied.</summary>
    public ReadOnlySpan<DesignBox> Clipboard => _clip.AsSpan(0, _clipCount);

    /// <summary>True when the draft differs from the blueprint it started from.</summary>
    public bool Dirty => _depth != 0;

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

    /// <summary>The shape the next part is placed in: a box, or a wedge filling half of <see cref="PartSize"/>.</summary>
    public BlockShape PartShape { get; private set; } = BlockShape.Box;

    /// <summary>The block face under the pointer this frame, if any.</summary>
    public bool HasHover { get; private set; }
    public DraftHit Hover { get; private set; }

    /// <summary>While placing with a face under the pointer: where the part would go and whether it fits there.</summary>
    public bool HasGhost { get; private set; }
    public GridSize GhostAt { get; private set; }
    public DraftEdit GhostCheck { get; private set; }

    /// <summary>True when the draft can be committed now.</summary>
    public bool CanApply =>
        CanEdit && Dirty && Server.Phase == EditorPhase.Open && !Server.Pending
        && Server.Token != 0 && Check == DraftCheck.Ready && CheckedVersion == Draft!.Version;

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
        if (!CanInspect || !Dirty || Source is null || Content is null)
            return false;
        CancelCarry();
        CancelPartGestures();
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
        if (actions.InterfaceShortcutPressed(devices, InputAction.ToggleEditor, typing, pointerOwned))
        {
            if (Stage == EditorStage.Closed)
                Request(own, bridge);
            else
                Cancel(bridge);
            return true;
        }

        if (Stage != EditorStage.Closed && !typing && actions.InterfaceShortcutPressed(devices, InputAction.Pause, typing, pointerOwned))
        {
            // Escape first puts back a part being sized or turned, then drops what the pointer carries; only with
            // none of those does it end the edit.
            if (SizingPart)
                CancelPartSizing();
            else if (RotatingPart)
                CancelPartRotation();
            else if (Carry != EditorCarry.None)
                CancelCarry();
            else
                Cancel(bridge);
            return true;
        }

        return false;
    }

    /// <summary>The player cancels: whatever stage the edit is in ends, and the draft is thrown away.</summary>
    public void Cancel(PlayerInputBridge bridge)
    {
        if (Stage == EditorStage.Closed)
            return;
        CancelPartGestures();
        GhostPinned = false;
        bridge.CancelEdit();
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
        CancelPartGestures();
        GhostPinned = false;
        Stage = EditorStage.Closed;
        _partPressUsed = false;
        Draft = null;
        Source = null;
        Content = null;
        Carry = EditorCarry.None;
        ClearSelection();
        _clipCount = 0;
        Server = default;
        _seenSerial = _askedSerial = _commitSerial = 0;
        _pilotHull = _awaitedHull = 0;
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
                    if (fresh && view.Reason != EditorReason.None)
                        Refuse(view.Reason, view.WaitSeconds);
                }

                break;

            case EditorStage.Applying:
                if (view.Phase == EditorPhase.Applied)
                {
                    _replacedFrom = view.ReplacedHull;
                    _replacedTo = view.Hull;
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
                    if (view.Reason != EditorReason.None)
                        Refuse(view.Reason, view.WaitSeconds);
                }

                break;
        }

        // A part is sized or turned only while the ship may be edited.
        if ((SizingPart || RotatingPart) && !CanEdit)
            CancelPartGestures();
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

        if (!HasGhost)
            return;
        var result = Draft!.Add(GhostAt, PartSize, Content!.Blocks.Types[Part], PartShape);
        Record(result);
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

        Face = Hover.Side;
        Select(Hover.Block, add);
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

    /// <summary>Copies the selected blocks, in draft order; the latest picked is the one the pointer holds on paste.</summary>
    public bool CopySelection()
    {
        if (!CanEdit || _selectionCount == 0)
            return false;
        if (_clip.Length < _selectionCount)
            _clip = new DesignBox[Math.Max(_selectionCount, _clip.Length * 2)];
        var boxes = Draft!.Boxes;
        _clipCount = 0;
        for (var i = 0; i < boxes.Length; i++)
        {
            if (!IsSelected(i))
                continue;
            if (i == Selected)
                _clipAnchor = _clipCount;
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
            if (HasGhost && GhostCheck == DraftEdit.Done)
            {
                var placed = Draft!.Add(GhostAt, PartSize, Content!.Blocks.Types[Part], PartShape);
                Record(placed);
            }

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

        var result = Draft!.Paste(Clipboard, by);
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
        UpdateGhost();
    }

    /// <summary>Stops placing: a click selects instead.</summary>
    public void StopPlacing()
    {
        CancelPartGestures();
        Placing = false;
        HasGhost = false;
        GhostPinned = false;
    }

    /// <summary>
    /// Sets one axis of the size parts are placed at, within the content's largest box side; the ghost snaps to its
    /// face again.
    /// </summary>
    public void SetPartSize(int axis, int length)
    {
        var limit = Content?.Blocks.Limits.MaxBoxSide ?? 1;
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
    private GridSize _sizeFromAt;
    private GridSize _pinAt;
    private GridSize _pinSize;

    /// <summary>
    /// Starts sizing the part to place, while its ghost is on a face and the ship may be edited, and holds the ghost
    /// where it is. False when it cannot start.
    /// </summary>
    public bool BeginPartSizing()
    {
        if (SizingPart || RotatingPart || !CanEdit || !Placing || Carry != EditorCarry.None || !HasGhost)
            return false;
        _sizeFromSize = PartSize;
        _sizeFromAt = GhostAt;
        Pin(GhostAt, PartSize);
        SizingPart = true;
        return true;
    }

    /// <summary>
    /// Moves one face of the ghost being sized to make it <paramref name="length"/> whole cells along that face's
    /// axis, from one to the content's largest box side. The opposite face stays where it is in the grid: moving a
    /// minus face moves the ghost's low corner with it. A wedge stays the same wedge, its slope stretched.
    /// </summary>
    public void SizePartFace(int side, int length)
    {
        if (!SizingPart || (uint)side > 5u)
            return;
        var axis = side >> 1;
        var limit = Content?.Blocks.Limits.MaxBoxSide ?? 1;
        length = Math.Clamp(length, 1, limit);
        var old = EditorGeometry.Of(PartSize, axis);
        if (length == old)
            return;
        var at = GhostAt;
        if ((side & 1) != 0)
            at = EditorGeometry.With(at, axis, EditorGeometry.Of(at, axis) + old - length);
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

    private void Pin(GridSize at, GridSize size)
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
    }

    /// <summary>Chooses the shape parts are placed in. A part being turned is put back first.</summary>
    public void SetPartShape(BlockShape shape)
    {
        if (!BlockShapes.IsValid(shape))
            return;
        CancelPartGestures();
        GhostPinned = false;
        PartShape = shape;
        UpdateGhost();
    }

    /// <summary>
    /// Turns the part to place a quarter turn about a grid axis, as <see cref="BuildDraft.Rotate"/> turns a placed
    /// block: its shape turns and its size swaps the other two axes.
    /// </summary>
    public void RotatePart(int axis)
    {
        if ((uint)axis > 2u)
            return;
        var u = (axis + 1) % 3;
        var v = (axis + 2) % 3;
        PartSize = EditorGeometry.With(EditorGeometry.With(PartSize, u, EditorGeometry.Of(PartSize, v)), v, EditorGeometry.Of(PartSize, u));
        PartShape = BlockShapes.Rotate(PartShape, axis);
        UpdateGhost();
    }

    /// <summary>
    /// True while the part to place is being turned with the rotation rings around its ghost. Nothing on the draft
    /// changes: each turn counts at once, <see cref="EndPartRotation"/> keeps it with nothing to confirm, and
    /// <see cref="CancelPartRotation"/> puts back the shape and size from before.
    /// </summary>
    public bool RotatingPart { get; private set; }

    private GridSize _rotateSize;
    private BlockShape _rotateShape;
    private GridSize _rotateAt;

    /// <summary>
    /// Starts turning the part to place, while its ghost is on a face and the ship may be edited. The ghost keeps
    /// to that face while it turns. False when it cannot start.
    /// </summary>
    public bool BeginPartRotation()
    {
        if (RotatingPart || SizingPart || !CanEdit || !Placing || Carry != EditorCarry.None || !HasGhost)
            return false;
        _rotateSize = PartSize;
        _rotateShape = PartShape;
        _rotateAt = GhostAt;
        RotatingPart = true;
        return true;
    }

    /// <summary>
    /// Turns the part being turned by whole quarter turns about a grid axis, negative the other way, as
    /// <see cref="RotatePart"/> turns it: its shape turns and its size swaps the other two axes.
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

    /// <summary>Puts back the part's shape and size from before it was turned. Nothing when no part is being turned.</summary>
    public void CancelPartRotation()
    {
        if (!RotatingPart)
            return;
        RotatingPart = false;
        RotationCancels++;
        PartSize = _rotateSize;
        PartShape = _rotateShape;
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

    /// <summary>True when the selection may be deleted: there is one, and it leaves at least one block.</summary>
    public bool CanDelete => CanEdit && _selectionCount > 0 && _selectionCount < Draft!.Boxes.Length;

    /// <summary>Deletes every selected block, as one step to undo.</summary>
    public void DeleteSelected()
    {
        if (!CanDelete)
            return;
        CancelCarry();
        var result = Draft!.Delete(Selection);
        if (result == DraftEdit.Done)
            ClearSelection();
        Record(result);
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

    private void Record(DraftEdit result)
    {
        LastEdit = result;
        LastEditAt = _time;
        if (result != DraftEdit.Done)
            return;
        _depth++;
        Changed();
    }

    private void Changed()
    {
        // The ghost's face may have moved or gone: a part being sized or turned is put back, and a held ghost lets go.
        CancelPartGestures();
        GhostPinned = false;
        _editedAt = _time;
        Check = DraftCheck.Pending;
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
        HasCarryGhost = false;
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
                    var at = Draft.Snap(Hover.Block, Hover.Side, held.Size, Hover.Cell);
                    CarryBy = new GridSize(at.X - held.At.X, at.Y - held.At.Y, at.Z - held.At.Z);
                    CarryCheck = Draft.CanMove(Selection, CarryBy);
                    HasCarryGhost = true;
                    return;
                }
            case EditorCarry.Paste when _clipCount > 0:
                {
                    var held = _clip[_clipAnchor];
                    var at = Draft.Snap(Hover.Block, Hover.Side, held.Size, Hover.Cell);
                    CarryBy = new GridSize(at.X - held.At.X, at.Y - held.At.Y, at.Z - held.At.Z);
                    CarryCheck = Draft.CanPaste(Clipboard, CarryBy);
                    HasCarryGhost = true;
                    return;
                }
            case EditorCarry.Move or EditorCarry.Paste:
                return;
        }

        if (!Placing)
            return;
        if (GhostPinned)
        {
            // A held ghost stays where it is held. Turned, its new size is laid against its face again, centred
            // where its middle was, so it stays on the face without jumping to the pointer.
            if (PartSize != _pinSize)
            {
                var middle = new GridSize(_pinAt.X + ((_pinSize.X - 1) >> 1), _pinAt.Y + ((_pinSize.Y - 1) >> 1), _pinAt.Z + ((_pinSize.Z - 1) >> 1));
                Pin(Draft.Snap(Hover.Block, Hover.Side, PartSize, middle), PartSize);
            }

            GhostAt = _pinAt;
        }
        else
            GhostAt = Draft.Snap(Hover.Block, Hover.Side, PartSize, Hover.Cell);
        GhostCheck = Draft.CanAdd(GhostAt, PartSize, Content!.Blocks.Types[Part]);
        HasGhost = true;
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
        CancelPartGestures();
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
        CancelPartGestures();
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
            if (_job.Generation == _generation && Draft is not null && _job.Version == Draft.Version)
            {
                CheckedVersion = _job.Version;
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

            _job.Copy = null;
        }

        // Only one check at a time, on a frozen copy; a newer edit waits for the running one and then starts again.
        if (Stage == EditorStage.Editing && Draft is not null && !_jobRunning && CheckedVersion != Draft.Version && _time - _editedAt >= CheckDelay)
        {
            Check = DraftCheck.Pending;
            _job.Copy = Draft.Copy();
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
        public DraftCopy? Copy;
        public int Version;
        public int Generation;
        public DraftExport Result;
        public string? Failure;

        public void Run()
        {
            Failure = null;
            Result = default;
            try
            {
                Result = Copy!.Export();
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
