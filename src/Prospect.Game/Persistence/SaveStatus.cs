namespace Prospect.Game.Persistence;

/// <summary>What the save system of a server is doing.</summary>
public enum SaveState : byte
{
    /// <summary>The world is not saved at all: a game without a world store.</summary>
    Off,

    /// <summary>Nothing is being captured or written.</summary>
    Idle,

    /// <summary>The tick is copying the world into the capture buffer.</summary>
    Capturing,

    /// <summary>The save worker holds the buffer: it is committing it, or making a recovery copy.</summary>
    Writing,

    /// <summary>
    /// Saving has stopped for good in this run: the store refused the state or was written by something else.
    /// The world plays on unsaved; the last completed save is the recovery point.
    /// </summary>
    Halted,
}

/// <summary>
/// Where a server's saves stand, readable from any thread without taking memory. The fields are read one after
/// another and not as one moment: a save completing in between shows as a state one step apart, never as a
/// value that was not true.
/// </summary>
/// <param name="State">What the save system is doing now.</param>
/// <param name="Dirty">True while the world holds changes no completed save has.</param>
/// <param name="Generation">The generation of the last completed save, or the one the world was opened at.</param>
/// <param name="SavedTick">The world interval the last completed save begins at; -1 before this run's first.</param>
/// <param name="SavedAtUnixMs">When that save was durable, in milliseconds since the Unix epoch; 0 before the first.</param>
/// <param name="FailedAttempts">Commits that failed since the last one that succeeded.</param>
/// <param name="Error">Why the newest commit failed; null when it did not.</param>
/// <param name="Backup">How the newest recovery copy ended.</param>
/// <param name="BackupGeneration">The generation the newest completed recovery copy holds; 0 when this run made none.</param>
/// <param name="BackupError">Why the newest recovery copy failed; null when it did not.</param>
/// <param name="RefusedGrowth">
/// How often the world refused to grow past what a save holds: an asteroid left where it was, a drone not
/// launched, an owner not let in. Not zero means the world has reached <see cref="SaveLimits"/>.
/// </param>
public readonly record struct SaveStatus(
    SaveState State,
    bool Dirty,
    long Generation,
    long SavedTick,
    long SavedAtUnixMs,
    int FailedAttempts,
    string? Error,
    BackupOutcome Backup,
    long BackupGeneration,
    string? BackupError,
    long RefusedGrowth
)
{
    /// <summary>The status of a game that is not saved.</summary>
    public static SaveStatus Off => new(SaveState.Off, false, 0, -1, 0, 0, null, BackupOutcome.NotAttempted, 0, null, 0);
}

/// <summary>How far closing a server has come.</summary>
public enum ClosePhase : byte
{
    /// <summary>Nobody asked to close it.</summary>
    Open,

    /// <summary>The world is frozen and waits for the save buffer, which an earlier save still holds.</summary>
    Freezing,

    /// <summary>The frozen world is captured and its final save is being committed.</summary>
    Saving,

    /// <summary>Closing is over, well or badly; the server can be disposed. See <see cref="CloseStatus.FinalCommit"/>.</summary>
    Completed,
}

/// <summary>What became of the final save.</summary>
public enum FinalCommitOutcome : byte
{
    /// <summary>Closing is not over.</summary>
    Pending,

    /// <summary>The game has no world store: there was nothing to save.</summary>
    NotPersisted,

    /// <summary>The final state is durable and the world is marked as closed cleanly.</summary>
    Committed,

    /// <summary>
    /// The final state could not be committed in the attempts allowed. The world is as its last completed save
    /// left it, and it is not marked as closed cleanly.
    /// </summary>
    Failed,

    /// <summary>
    /// The store could not tell whether the final state was committed. The world is either as its last
    /// completed save left it or as it was closed, and is not known to be marked as closed cleanly; the next
    /// open says which.
    /// </summary>
    Unknown,

    /// <summary>
    /// A tick threw. The world was caught halfway through an interval, so it was not captured; a capture that
    /// was complete before the fault may still have been committed as an ordinary save.
    /// </summary>
    SkippedAfterFault,

    /// <summary>The server was stopped or disposed without being asked to close: nothing final was saved.</summary>
    NotRequested,

    /// <summary>
    /// The server was disposed before the close it was asked for had finished, or the save worker did not stop
    /// in the time it is given. Whether the final save went through is not known here.
    /// </summary>
    Abandoned,
}

/// <summary>
/// Where closing a server stands, readable from any thread without taking memory. A close is over when
/// <see cref="Completed"/> is true; it went well only when <see cref="Clean"/> is. The three outcomes are apart
/// on purpose: a save can be committed while the store could not be made portable, or while no recovery copy
/// could be made, and neither undoes the commit.
/// </summary>
/// <param name="Phase">How far closing has come.</param>
/// <param name="FinalCommit">What became of the final save.</param>
/// <param name="Checkpoint">Whether the store was left whole and portable after the final commit.</param>
/// <param name="Backup">How the recovery copy of the final state ended.</param>
/// <param name="Error">What went wrong, for a person to read; null when nothing did.</param>
public readonly record struct CloseStatus(ClosePhase Phase, FinalCommitOutcome FinalCommit, CheckpointOutcome Checkpoint, BackupOutcome Backup, string? Error)
{
    /// <summary>Why the final checkpoint failed; a successful commit remains successful.</summary>
    public string? CheckpointError { get; init; }

    /// <summary>Why the final recovery copy failed, independently of any checkpoint failure.</summary>
    public string? BackupError { get; init; }

    /// <summary>True once closing is over and the server can be disposed.</summary>
    public bool Completed => Phase == ClosePhase.Completed;

    /// <summary>True when the final state is durable and the world is marked as closed cleanly.</summary>
    public bool Clean => FinalCommit == FinalCommitOutcome.Committed;

    /// <summary>True when the save is safe but the folder should not be moved, or has no fresh recovery copy.</summary>
    public bool Degraded => Clean && (Checkpoint == CheckpointOutcome.Failed || Backup == BackupOutcome.Failed);

    /// <summary>The status of a server nobody asked to close.</summary>
    public static CloseStatus Running => new(ClosePhase.Open, FinalCommitOutcome.Pending, CheckpointOutcome.NotAttempted, BackupOutcome.NotAttempted, null);
}
