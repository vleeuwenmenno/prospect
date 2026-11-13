namespace Prospect.Game.Persistence;

/// <summary>
/// One world's durable storage, open and leased to one server. The rules a store keeps, whatever it is made of:
/// <list type="bullet">
/// <item>
/// It is made by two static functions of its own, which this interface cannot carry:
/// <c>CreateResult Create(string folder, WorldSnapshot initial)</c> and
/// <c>OpenResult Open(string folder, SaveCompatibility expected)</c>. Both take the world's lease before they
/// look at anything, and return a store only with the lease held; a second open of the same world is refused.
/// </item>
/// <item>
/// A store that is returned has already made it durable that the world is running: its clean-close mark is
/// off. Only a <see cref="Commit"/> with <c>final</c> true that succeeds sets it again. A world that crashed, was
/// killed or was only disposed therefore never reads as closed cleanly, however clean its last save was.
/// </item>
/// <item>
/// One thread uses it: the server's save worker, from the first <see cref="Commit"/> to <see cref="Dispose"/>.
/// Nothing here is called on a tick or a frame.
/// </item>
/// <item>
/// It keeps no reference to a snapshot it is given once the call returns: the buffer goes back to the tick.
/// </item>
/// <item>
/// <see cref="Dispose"/> releases the files and the lease. It says nothing about how the world was left.
/// </item>
/// </list>
/// The server takes ownership of the store with the <see cref="WorldPersistence"/> it is given in, also when its
/// construction fails: the caller never disposes a store it has handed over.
/// </summary>
public interface IWorldStore : IDisposable
{
    /// <summary>
    /// Replaces the world's saved state with <paramref name="snapshot"/> and its metadata in one transaction,
    /// if and only if the stored generation is still <paramref name="expectedGeneration"/>, and gives the new
    /// state the next generation. On any failure the stored state and generation are what they were. With
    /// <paramref name="final"/> the world is also marked as closed cleanly in that same transaction, and the
    /// store then makes itself portable (for a database with a write-ahead log: checkpoints it), which is
    /// reported apart in <see cref="CommitResult.Checkpoint"/>: a save that is committed stays committed when
    /// that step fails. <see cref="CommitOutcome.Failed"/> is a promise that nothing changed, so a store
    /// returns it only when it knows; an exception promises nothing, and the worker reads one as
    /// <see cref="CommitOutcome.Unknown"/> and stops saving.
    /// </summary>
    CommitResult Commit(WorldSnapshot snapshot, long expectedGeneration, bool final);

    /// <summary>
    /// Makes a recovery copy of the committed state, apart from the live files, and keeps a bounded number of
    /// them: a new copy is published whole or not at all, and the previous one is never removed first. The
    /// store may skip a copy that would be of a generation it already has, or that it has no room for.
    /// </summary>
    BackupResult Backup(BackupReason reason);
}

/// <summary>How a commit ended.</summary>
public enum CommitOutcome : byte
{
    /// <summary>The state is durable under the generation the result names.</summary>
    Committed,

    /// <summary>Nothing changed; trying again with a newer state may work (a full disk, a failed write).</summary>
    Failed,

    /// <summary>
    /// The stored generation is not the expected one: something else wrote to this world. Nothing changed and
    /// nothing will be written by this server again.
    /// </summary>
    Conflict,

    /// <summary>The snapshot cannot be stored as it is (over the store's own limits). Nothing changed.</summary>
    Rejected,

    /// <summary>
    /// The store cannot tell whether the state was committed: the commit failed in a way that left its
    /// connection unusable, and reading the stored state back did not settle it. It may be the old state or the
    /// new one, never a mix. Nothing is written by this server again, a final save included: writing on top of a
    /// state nobody can name could lose the one that is there.
    /// </summary>
    Unknown,
}

/// <summary>Whether a final commit left the store's files whole and portable.</summary>
public enum CheckpointOutcome : byte
{
    /// <summary>The commit was not final, or it did not succeed.</summary>
    NotAttempted,
    Completed,

    /// <summary>The save is committed, but the folder may need the store's own recovery before it is copied.</summary>
    Failed,
}

/// <summary>What a commit did.</summary>
/// <param name="Outcome">How it ended. The other fields describe the committed state only when it was committed.</param>
/// <param name="Generation">The generation the state is stored under.</param>
/// <param name="CapturedTick">The snapshot's <see cref="WorldSnapshot.NextWorldTick"/>.</param>
/// <param name="CompletedAt">When the commit was durable.</param>
/// <param name="Checkpoint">How making the store portable went, for a final commit.</param>
/// <param name="Error">What went wrong, for a person to read; null when nothing did.</param>
public readonly record struct CommitResult(
    CommitOutcome Outcome,
    long Generation,
    long CapturedTick,
    DateTimeOffset CompletedAt,
    CheckpointOutcome Checkpoint = CheckpointOutcome.NotAttempted,
    string? Error = null
);

/// <summary>Why a recovery copy is asked for.</summary>
public enum BackupReason : byte
{
    /// <summary>The first save of this run of the server.</summary>
    Initial,

    /// <summary>The time between copies has passed and a newer generation is committed.</summary>
    Interval,

    /// <summary>The world is closing and its final save is committed.</summary>
    Final,
}

/// <summary>How a recovery copy ended.</summary>
public enum BackupOutcome : byte
{
    /// <summary>None was asked for yet.</summary>
    NotAttempted,
    Completed,

    /// <summary>The store chose not to make one: it has this generation already, or no room to publish another safely.</summary>
    Skipped,

    /// <summary>The copy could not be made or published. The committed save is not affected.</summary>
    Failed,
}

/// <summary>What a recovery copy did.</summary>
/// <param name="Outcome">How it ended.</param>
/// <param name="Generation">The generation the newest completed copy holds.</param>
/// <param name="Error">What went wrong, for a person to read; null when nothing did.</param>
public readonly record struct BackupResult(BackupOutcome Outcome, long Generation, string? Error = null);

/// <summary>How opening a world ended.</summary>
public enum OpenOutcome : byte
{
    Opened,

    /// <summary>There is no world in the folder.</summary>
    Missing,

    /// <summary>The world is leased to another server. Nothing was read.</summary>
    Busy,

    /// <summary>The world was saved with other content or by another build. It was not changed.</summary>
    Incompatible,

    /// <summary>The world's files are not what this store wrote, or its save does not decode. They were not changed.</summary>
    Damaged,

    /// <summary>The files could not be read or the lease could not be taken (permissions, a failing disk).</summary>
    Failed,
}

/// <summary>What a store's <c>Open</c> returns.</summary>
/// <param name="Outcome">How it ended. Only <see cref="OpenOutcome.Opened"/> carries a store and a snapshot.</param>
/// <param name="Store">The leased store, already marked as running.</param>
/// <param name="Snapshot">The committed state, decoded within <see cref="SaveLimits"/>; a server judges it against its content.</param>
/// <param name="Generation">The generation that state is stored under: the first commit's expected generation.</param>
/// <param name="WasCleanClose">Whether the world was closed cleanly before this open marked it as running.</param>
/// <param name="Error">What went wrong, for a person to read; null when nothing did.</param>
public sealed record OpenResult(OpenOutcome Outcome, IWorldStore? Store, WorldSnapshot? Snapshot, long Generation, bool WasCleanClose, string? Error = null);

/// <summary>How creating a world ended.</summary>
public enum CreateOutcome : byte
{
    Created,

    /// <summary>The folder already holds a world. It was not changed.</summary>
    Exists,
    Failed,
}

/// <summary>What a store's <c>Create</c> returns.</summary>
/// <param name="Outcome">How it ended. Only <see cref="CreateOutcome.Created"/> carries a store.</param>
/// <param name="Store">The leased store, holding the initial snapshot as its first committed state.</param>
/// <param name="Generation">The generation the initial state is stored under.</param>
/// <param name="Error">What went wrong, for a person to read; null when nothing did.</param>
public sealed record CreateResult(CreateOutcome Outcome, IWorldStore? Store, long Generation, string? Error = null);
