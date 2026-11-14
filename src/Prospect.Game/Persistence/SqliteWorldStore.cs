using System.Globalization;
using Microsoft.Data.Sqlite;
using Prospect.Engine.Diagnostics;
using Prospect.Engine.Net;
using Prospect.Game.World;

namespace Prospect.Game.Persistence;

/// <summary>What a world folder holds, read without running the world: enough to list it and to say why it cannot be opened.</summary>
/// <param name="Outcome">
/// <see cref="OpenOutcome.Opened"/> when the summary could be read; otherwise why not. A world that is running
/// is <see cref="OpenOutcome.Busy"/> and has no summary: it is there, and it is in use.
/// </param>
/// <param name="WorldId">The world's own identity.</param>
/// <param name="Name">The world's name.</param>
/// <param name="CreatedUnixMs">When the world was created.</param>
/// <param name="SavedUnixMs">When its newest save was committed.</param>
/// <param name="Generation">The generation of that save.</param>
/// <param name="NextWorldTick">The world interval that save begins at.</param>
/// <param name="WasCleanClose">False for a world whose last run did not end with a final save: it crashed, was killed or was only disposed.</param>
/// <param name="Compatibility">What the save was made with; compare with <see cref="SaveCompatibility.Of"/> to know whether this build loads it.</param>
/// <param name="Error">What went wrong, for a person to read; null when nothing did.</param>
public sealed record WorldSummary(
    OpenOutcome Outcome,
    Guid WorldId,
    string Name,
    long CreatedUnixMs,
    long SavedUnixMs,
    long Generation,
    long NextWorldTick,
    bool WasCleanClose,
    SaveCompatibility Compatibility,
    string? Error = null
);

/// <summary>
/// A world on disk: one SQLite database per world folder, holding one row with the world's metadata and its
/// newest snapshot as one blob in the save's own layout (<see cref="WorldSnapshotCodec"/>). A commit replaces
/// both in one transaction. Nothing here depends on the network protocol.
/// <para>
/// The folder: <c>world.lock</c> is the lease and is never removed or replaced; <c>world.db</c> is the world,
/// with SQLite's own <c>-wal</c> beside it while it runs; <c>backups/</c> holds the recovery copies, each a whole
/// database of one generation. A world is moved by copying the folder after a clean close.
/// </para>
/// <para>
/// The lease comes before anything else is opened. It is an exclusive lock on <c>world.lock</c>, held for as
/// long as the store lives, plus a table of the folders this process has leased. The lock is tried a second
/// time at once, and where that is granted the world is refused: locks that hold nobody off on this machine
/// (locking switched off, a file system without them) are found out. What that cannot show is whether a lock
/// is kept between machines. Worlds are for local file systems; a world on a network share is not supported,
/// and nothing here detects every one. Two paths to one folder (a symbolic link) meet at the same lock file.
/// </para>
/// <para>
/// What it promises about durability is SQLite's own, with a write-ahead log and <c>synchronous=FULL</c>: a
/// commit that is acknowledged was synced, and a process that dies leaves the last acknowledged commit. What a
/// drive that loses its cache on power loss does is not tested here and not promised.
/// </para>
/// </summary>
public sealed class SqliteWorldStore : IWorldStore
{
    /// <summary>The world's database in its folder.</summary>
    public const string DatabaseFile = "world.db";

    /// <summary>The lease file in a world folder. It is created once and left there for good.</summary>
    public const string LockFile = "world.lock";

    /// <summary>The folder of recovery copies inside a world folder.</summary>
    public const string BackupFolder = "backups";

    /// <summary>"PRSP" in the database header: tells a world from any other SQLite file.</summary>
    public const int ApplicationId = 0x50525350;

    /// <summary>The version of the table layout below. A database with another is refused; there are no migrations.</summary>
    public const int SchemaVersion = 2;

    /// <summary>How many completed recovery copies are kept. The oldest goes only after a newer one is published.</summary>
    public const int KeptBackups = 3;

    /// <summary>
    /// The native SQLite build this store was reviewed with, as <c>sqlite_version()</c> and
    /// <c>sqlite_source_id()</c> report it. Any other build is refused before a world is touched: change both
    /// together with the package, on purpose, after reading what changed.
    /// </summary>
    public const string AuditedSqliteVersion = "3.53.4";

    /// <inheritdoc cref="AuditedSqliteVersion"/>
    public const string AuditedSqliteSourceId = "2026-07-24 19:02:57 bf7c7f30031888f4e796e429ab3978879485813aaca6f641c7b33e4e09459bcc";

    /// <summary>The one table, exactly as SQLite keeps its text. Compared whole when a world is opened.</summary>
    internal const string Schema =
        "CREATE TABLE world (id INTEGER PRIMARY KEY CHECK (id = 1), world_id BLOB NOT NULL, name TEXT NOT NULL, created_ms INTEGER NOT NULL, "
        + "generation INTEGER NOT NULL, captured_tick INTEGER NOT NULL, saved_ms INTEGER NOT NULL, clean INTEGER NOT NULL, save_version INTEGER NOT NULL, "
        + "generator_version INTEGER NOT NULL, design_encoding INTEGER NOT NULL, content_identity BLOB NOT NULL, snapshot BLOB NOT NULL, public_key BLOB NOT NULL) STRICT";

    private const string Staging = "world.db.creating";
    private const string BackupStaging = "staging.db";
    private const string BackupPrefix = "world-";
    private const string BackupSuffix = ".db";

    /// <summary>Seconds a statement waits for a lock before it fails. Nothing else should hold one under the lease.</summary>
    private const int BusySeconds = 5;

    private static readonly Lazy<string?> RuntimeProblem = new(CheckRuntime);

    private readonly string _folder;
    private readonly SaveLimits _limits;
    private readonly Lease _lease;
    private readonly Guid _worldId;
    private SqliteConnection _connection;
    private byte[] _encoded = [];

    // What this store has found out about the files in the backup folder that are named as its copies: whether
    // each really is one. Judged once, and again only when a file's size or time has changed.
    private long _generation;
    private bool _unknown;
    private bool _disposed;

    private SqliteWorldStore(string folder, SaveLimits limits, Lease lease, SqliteConnection connection, Guid worldId, long generation, WorldIdentity identity)
    {
        Identity = identity;
        _folder = folder;
        _limits = limits;
        _lease = lease;
        _connection = connection;
        _worldId = worldId;
        _generation = generation;
    }

    /// <summary>Public identity and key condition observed under the lease at creation or open.</summary>
    public WorldIdentity Identity { get; }

    /// <summary>
    /// The most bytes one database file of a world may be: the largest save twice over and a little, since a
    /// file keeps the pages of a blob it has just replaced. A live database, a recovery copy and the copy
    /// being made are each held to it.
    /// </summary>
    public static long MaxDatabaseBytes(SaveLimits limits) => 2 * limits.MaxEncodedBytes + (1 << 20);

    /// <summary>
    /// The most bytes the files named as a world's recovery copies take together, the one being made included.
    /// It is checked before a copy is begun: what is there is added up, and a copy that would pass it is not made.
    /// </summary>
    public static long MaxBackupBytes(SaveLimits limits) => (KeptBackups + 1) * MaxDatabaseBytes(limits);

    /// <summary>
    /// The most bytes a world's write-ahead log may be for the world to be opened. The store never lets a log it
    /// writes pass it: a write is begun only with room in the log for the largest save. So a world this store
    /// acknowledged is always one it opens again.
    /// </summary>
    public static long MaxLogBytes(SaveLimits limits) => 4 * MaxDatabaseBytes(limits);

    /// <summary>A hook for tests: called with the name of a step just before it, and may throw to make it fail.</summary>
    internal Action<string>? Fault { get; set; }

    /// <summary>The generation of the newest state this store knows to be committed.</summary>
    internal long Generation => _generation;

    /// <summary>The store's connection, for a test that has to break it or shrink the database it may grow to.</summary>
    internal SqliteConnection Connection => _connection;

    /// <summary>
    /// Creates a world in a folder that has none and returns it leased and running, with
    /// <paramref name="initial"/> as its first committed state under generation 1. A folder that already holds a
    /// world, or anything that could be one, is left exactly as it is. The database is built under another name
    /// and given its own only when it is whole, so a create that is interrupted leaves no world behind.
    /// </summary>
    public static CreateResult Create(string folder, WorldSnapshot initial) => Create(folder, initial, SaveLimits.Default);

    internal static CreateResult Create(string folder, WorldSnapshot initial, SaveLimits limits)
    {
        if (RuntimeProblem.Value is { } problem)
            return new CreateResult(CreateOutcome.Failed, null, 0, problem);

        Lease? lease = null;
        SqliteConnection? connection = null;
        bool ownKey = false;
        bool keyPublished = false;
        bool ownDatabase = false;
        string? keyStaging = null;
        try
        {
            folder = Path.GetFullPath(folder);
            var database = Path.Combine(folder, DatabaseFile);
            if (HasWorldFiles(database))
                return new CreateResult(CreateOutcome.Exists, null, 0, "The folder already holds a world.");

            // The folder and every folder made on the way to it are in their parents for good before the
            // world is: a world whose folder a crash can take away was never created.
            DurableFiles.CreateDirectory(folder);
            lease = Lease.TryTake(folder, out _, out var leaseError);
            if (lease is null)
                return new CreateResult(CreateOutcome.Failed, null, 0, leaseError);

            // Asked again under the lease: whoever held it a moment ago may have created the world.
            if (HasWorldFiles(database))
            {
                lease.Dispose();
                return new CreateResult(CreateOutcome.Exists, null, 0, "The folder already holds a world.");
            }

            var size = WorldSnapshotCodec.EncodedSize(initial);
            if (size > limits.MaxEncodedBytes)
            {
                lease.Dispose();
                return new CreateResult(CreateOutcome.Failed, null, 0, "The world is larger than a save may be.");
            }

            var bytes = WorldSnapshotCodec.Encode(initial);

            // Read back as an open would read it: a world is never stored that could not be opened again.
            if (!WorldSnapshotCodec.TryDecode(bytes, limits, out _, out var unreadable))
            {
                lease.Dispose();
                return new CreateResult(CreateOutcome.Failed, null, 0, $"The world does not fit a save: {unreadable}.");
            }

            var staging = Path.Combine(folder, Staging);
            if (HasWorldFiles(staging) || Path.Exists(staging + "-shm"))
                throw new IOException("Unowned creation staging prevents creation; nothing was replaced.");
            keyStaging = Path.Combine(folder, "world.key.creating");
            if (Path.Exists(Path.Combine(folder, WorldIdentity.KeyFile)) || Path.Exists(keyStaging))
                throw new IOException("An existing identity file prevents creation; nothing was replaced.");
            // Only files created by this invocation may be removed after failure.
            var publicKey = WorldIdentity.CreateKey(folder, keyStaging, () => ownKey = true);

            using (var reserved = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                ownDatabase = true;
            using (var building = OpenConnection(staging, SqliteOpenMode.ReadWriteCreate))
            {
                Execute(building, $"PRAGMA application_id = {ApplicationId}");
                Execute(building, $"PRAGMA user_version = {SchemaVersion}");
                Execute(building, "PRAGMA synchronous = FULL");
                using var transaction = building.BeginTransaction(deferred: false);
                Execute(building, Schema, transaction);
                using (var insert = building.CreateCommand())
                {
                    insert.Transaction = transaction;
                    insert.CommandText =
                        "INSERT INTO world (id, world_id, name, created_ms, generation, captured_tick, saved_ms, clean, save_version, generator_version, design_encoding, content_identity, snapshot, public_key) "
                        + "VALUES (1, $world, $name, $created, 1, $tick, $saved, 0, $save, $generator, $design, $content, $snapshot, $publicKey)";
                    insert.Parameters.AddWithValue("$world", initial.WorldId.ToByteArray());
                    insert.Parameters.AddWithValue("$name", initial.Name);
                    insert.Parameters.AddWithValue("$created", initial.CreatedUnixMs);
                    insert.Parameters.AddWithValue("$tick", initial.NextWorldTick);
                    insert.Parameters.AddWithValue("$saved", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                    insert.Parameters.AddWithValue("$save", initial.Compatibility.SaveVersion);
                    insert.Parameters.AddWithValue("$generator", initial.Compatibility.GeneratorVersion);
                    insert.Parameters.AddWithValue("$design", initial.Compatibility.DesignEncoding);
                    insert.Parameters.AddWithValue("$content", IdentityBytes(initial.Compatibility.Content));
                    insert.Parameters.AddWithValue("$snapshot", bytes);
                    insert.Parameters.AddWithValue("$publicKey", publicKey);
                    insert.ExecuteNonQuery();
                }

                transaction.Commit();
            }

            DurableFiles.SyncFile(staging);
            if (ownKey)
            {
                DurableFiles.Publish(keyStaging, Path.Combine(folder, WorldIdentity.KeyFile));
                keyPublished = true;
            }
            var identity = WorldIdentity.Read(folder, initial.WorldId, publicKey);
            DurableFiles.Publish(staging, database);

            connection = OpenWriter(database, limits);
            return new CreateResult(CreateOutcome.Created, new SqliteWorldStore(folder, limits, lease, connection, initial.WorldId, 1, identity), 1);
        }
        catch (Exception e)
        {
            try
            {
                connection?.Dispose();
                if ((ownKey || ownDatabase) && !HasWorldFiles(Path.Combine(folder, DatabaseFile)))
                {
                    if (ownDatabase) DeleteDatabase(Path.Combine(folder, Staging));
                    if (ownKey) File.Delete(keyStaging!);
                    if (keyPublished) File.Delete(Path.Combine(folder, WorldIdentity.KeyFile));
                }
            }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
            finally { lease?.Dispose(); }
            return new CreateResult(CreateOutcome.Failed, null, 0, e is IOException ? e.Message : "World creation failed; no existing identity was replaced.");
        }
    }

    /// <summary>
    /// Opens the world in a folder and returns it leased and running. In this order: the lease; a read-only look
    /// at the database, which checks that it is a world of this layout, that it is whole, that it was saved with
    /// <paramref name="expected"/>, and decodes its snapshot within the limits; then, only when all of that
    /// held, the one writing connection, which makes it durable that the world is running before this returns.
    /// A world that is refused keeps its database and its write-ahead log byte for byte: nothing was opened for
    /// writing. (SQLite may still create shared-memory or empty WAL files while inspecting the database.)
    /// <para>
    /// The snapshot that comes back has been decoded, not believed: the server that restores it judges it
    /// against its content and refuses it whole if it does not hold together.
    /// </para>
    /// </summary>
    public static OpenResult Open(string folder, SaveCompatibility expected) => Open(folder, expected, SaveLimits.Default);

    /// <summary>
    /// Checks launch constraints under the world's lease before opening it for writing. The validator must
    /// not mutate the snapshot. A nonnull reason refuses the open as <see cref="OpenOutcome.Incompatible"/>.
    /// Expected storage exceptions return Failed; other exceptions propagate after releasing the lease.
    /// </summary>
    public static OpenResult Open(string folder, SaveCompatibility expected, Func<WorldSnapshot, string?> validate) =>
        Open(folder, expected, SaveLimits.Default, validate);

    internal static OpenResult Open(string folder, SaveCompatibility expected, SaveLimits limits, Func<WorldSnapshot, string?>? validate = null)
    {
        if (RuntimeProblem.Value is { } problem)
            return Refused(OpenOutcome.Failed, problem);

        Lease? lease = null;
        SqliteConnection? connection = null;
        try
        {
            folder = Path.GetFullPath(folder);
            var database = Path.Combine(folder, DatabaseFile);
            if (!File.Exists(database))
                return Refused(OpenOutcome.Missing, "There is no world in the folder.");
            lease = Lease.TryTake(folder, out var busy, out var leaseError);
            if (lease is null)
                return Refused(busy ? OpenOutcome.Busy : OpenOutcome.Failed, leaseError);

            var look = Inspect(database, limits, expected, materialize: true);
            if (look.Outcome != OpenOutcome.Opened)
            {
                lease.Dispose();
                return Refused(look.Outcome, look.Error);
            }

            if (validate?.Invoke(look.Snapshot!) is { } refusal)
            {
                lease.Dispose();
                return Refused(OpenOutcome.Incompatible, refusal);
            }

            var identity = WorldIdentity.Read(folder, look.WorldId, look.PublicKey!);

            // Only now is anything opened for writing. Under the lease nothing else can have written since the
            // look, and the generation is asked again in the same statement that marks the world as running.
            connection = OpenWriter(database, limits);
            if (!HasLogRoom(connection, database, limits, null, out var logError))
                throw new IOException(logError);
            using (var mark = connection.CreateCommand())
            {
                mark.CommandText = "UPDATE world SET clean = 0 WHERE id = 1 AND generation = $generation";
                mark.Parameters.AddWithValue("$generation", look.Generation);
                if (mark.ExecuteNonQuery() != 1)
                    throw new InvalidDataException("The world changed while it was being opened.");
            }

            return new OpenResult(OpenOutcome.Opened, new SqliteWorldStore(folder, limits, lease, connection, look.WorldId, look.Generation, identity), look.Snapshot, look.Generation, look.Clean);
        }
        catch (Exception e) when (e is SqliteException or IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or NotSupportedException)
        {
            try { connection?.Dispose(); }
            finally { lease?.Dispose(); }
            return Refused(OpenOutcome.Failed, e.Message);
        }
        catch
        {
            try { connection?.Dispose(); }
            finally { lease?.Dispose(); }
            throw;
        }
    }

    /// <summary>
    /// Reads what a world folder holds without running the world or decoding its snapshot: for a list of
    /// worlds. It takes the lease for the moment it looks, so a world that is running is reported as
    /// <see cref="OpenOutcome.Busy"/> and not read at all; nothing is opened for writing. Run it off the frame
    /// loop: it checks the database's integrity.
    /// </summary>
    public static WorldSummary Summarize(string folder)
    {
        static WorldSummary Without(OpenOutcome outcome, string? error) => new(outcome, Guid.Empty, "", 0, 0, 0, 0, false, default, error);

        if (RuntimeProblem.Value is { } problem)
            return Without(OpenOutcome.Failed, problem);
        try
        {
            folder = Path.GetFullPath(folder);
            var database = Path.Combine(folder, DatabaseFile);
            if (!File.Exists(database))
                return Without(OpenOutcome.Missing, "There is no world in the folder.");
            using var lease = Lease.TryTake(folder, out var busy, out var leaseError);
            if (lease is null)
                return Without(busy ? OpenOutcome.Busy : OpenOutcome.Failed, leaseError);
            return SummarizeLeased(database);
        }
        catch (Exception e) when (e is SqliteException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return Without(OpenOutcome.Failed, e.Message);
        }
    }

    /// <summary>The look <see cref="Summarize"/> takes, for a caller that already holds the world's lease (<see cref="TakeLease"/>).</summary>
    internal static WorldSummary SummarizeLeased(string database)
    {
        static WorldSummary Without(OpenOutcome outcome, string? error) => new(outcome, Guid.Empty, "", 0, 0, 0, 0, false, default, error);

        if (RuntimeProblem.Value is { } problem)
            return Without(OpenOutcome.Failed, problem);
        try
        {
            var look = Inspect(database, SaveLimits.Default, null, materialize: false);
            return look.Outcome is OpenOutcome.Opened or OpenOutcome.Incompatible
                ? new WorldSummary(look.Outcome, look.WorldId, look.Name, look.CreatedMs, look.SavedMs, look.Generation, look.CapturedTick, look.Clean, look.Compatibility, look.Error)
                : Without(look.Outcome, look.Error);
        }
        catch (Exception e) when (e is SqliteException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return Without(OpenOutcome.Failed, e.Message);
        }
    }

    /// <summary>
    /// Takes a world folder's lease the way an open does, for <see cref="WorldDeletion"/>: null with
    /// <paramref name="busy"/> when someone holds it. The caller disposes it, and nothing is opened.
    /// </summary>
    internal static IDisposable? TakeLease(string folder, out bool busy, out string error) => Lease.TryTake(Path.GetFullPath(folder), out busy, out error);

    /// <inheritdoc/>
    public CommitResult Commit(WorldSnapshot snapshot, long expectedGeneration, bool final)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_unknown)
            return new CommitResult(CommitOutcome.Unknown, _generation, snapshot.NextWorldTick, DateTimeOffset.UtcNow, Error: "An earlier save ended without the store knowing whether it was committed; nothing more is written.");

        int length;
        try
        {
            var size = WorldSnapshotCodec.EncodedSize(snapshot);
            if (size > _limits.MaxEncodedBytes || snapshot.WorldId != _worldId || snapshot.OwnerCount > _limits.MaxOwners || snapshot.HullCount > _limits.MaxHulls
                || snapshot.BlockCount > _limits.MaxBlocks || snapshot.RockCount > _limits.MaxRocks)
                return new CommitResult(CommitOutcome.Rejected, _generation, snapshot.NextWorldTick, DateTimeOffset.UtcNow, Error: "The snapshot holds more than a save may, or is of another world.");

            // One buffer, kept and grown as the world grows: a save takes no new memory once it has been this size.
            if (_encoded.Length < size)
                _encoded = new byte[Math.Min(_limits.MaxEncodedBytes, Math.Max(size, _encoded.Length * 2L))];
            length = WorldSnapshotCodec.Encode(snapshot, _encoded);
        }
        catch (InvalidDataException e)
        {
            return new CommitResult(CommitOutcome.Rejected, _generation, snapshot.NextWorldTick, DateTimeOffset.UtcNow, Error: e.Message);
        }

        // Before anything is begun: a save is only written with room in the log for all of it. Refused here,
        // nothing has changed, and what was acknowledged before stays acknowledged and stays openable.
        if (!HasLogRoom(_connection, Path.Combine(_folder, DatabaseFile), _limits, Fault, out var logError))
            return new CommitResult(CommitOutcome.Failed, _generation, snapshot.NextWorldTick, DateTimeOffset.UtcNow, Error: logError);

        var next = expectedGeneration + 1;
        var saved = DateTimeOffset.UtcNow;
        SqliteTransaction? transaction = null;
        var committing = false;
        try
        {
            transaction = _connection.BeginTransaction(deferred: false);
            int rows;
            using (var update = _connection.CreateCommand())
            {
                update.Transaction = transaction;
                update.CommandText =
                    "UPDATE world SET snapshot = zeroblob($length), generation = $next, captured_tick = $tick, saved_ms = $saved, clean = $clean, name = $name "
                    + "WHERE id = 1 AND generation = $expected AND world_id = $world";
                update.Parameters.AddWithValue("$length", length);
                update.Parameters.AddWithValue("$next", next);
                update.Parameters.AddWithValue("$tick", snapshot.NextWorldTick);
                update.Parameters.AddWithValue("$saved", saved.ToUnixTimeMilliseconds());
                update.Parameters.AddWithValue("$clean", final ? 1 : 0);
                update.Parameters.AddWithValue("$name", snapshot.Name);
                update.Parameters.AddWithValue("$expected", expectedGeneration);
                update.Parameters.AddWithValue("$world", _worldId.ToByteArray());
                rows = update.ExecuteNonQuery();
            }

            if (rows != 1)
            {
                transaction.Rollback();
                var stored = ReadState(_connection);
                return new CommitResult(CommitOutcome.Conflict, stored.Generation, snapshot.NextWorldTick, DateTimeOffset.UtcNow, Error: $"The world is at generation {stored.Generation}, not {expectedGeneration}: something else wrote to it.");
            }

            // The blob is written through the row's own pages, from the kept buffer: no second copy of the save.
            using (var blob = new SqliteBlob(_connection, "world", "snapshot", 1))
                blob.Write(_encoded, 0, length);

            Fault?.Invoke("commit");
            committing = true;
            transaction.Commit();
            Fault?.Invoke("commit-returned");
        }
        catch (Exception e) when (e is SqliteException or IOException or InvalidOperationException)
        {
            // Rolled back by name, never by assumption. A statement that failed before the commit and a rollback
            // that went through leave the world as it was, and that can be said. Anything else is looked up,
            // and never on this connection: one whose rollback did not go through still shows its own
            // unfinished transaction as if it were the world.
            var rolledBack = TryRollback(transaction);
            if (!committing && rolledBack)
                return new CommitResult(CommitOutcome.Failed, _generation, snapshot.NextWorldTick, DateTimeOffset.UtcNow, Error: e.Message);
            var resolved = Resolve(expectedGeneration, next, length, final, e.Message, out var cause);
            if (resolved != CommitOutcome.Committed)
                return new CommitResult(resolved, _generation, snapshot.NextWorldTick, DateTimeOffset.UtcNow, Error: cause);
        }
        finally
        {
            transaction?.Dispose();
        }

        // Durable. Nothing from here on can make it not so, and nothing from here on is reported as if it had.
        _generation = next;
        var checkpoint = CheckpointOutcome.NotAttempted;
        string? error = null;
        try
        {
            Fault?.Invoke("committed");
            if (final)
                checkpoint = Checkpoint(out error);
        }
        catch (Exception e)
        {
            if (final)
                checkpoint = CheckpointOutcome.Failed;
            error = e.Message;
        }

        return new CommitResult(CommitOutcome.Committed, next, snapshot.NextWorldTick, saved, checkpoint, error);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// A file in the backup folder is a recovery copy of this world when it says so itself: it opens from its own
    /// file, is whole, decodes, and is of this world and of the generation its name gives. A name alone is
    /// nothing. Files that only have the name, damaged ones and another world's, are never counted as copies,
    /// never removed and never written over; they do count against the bytes the folder may hold, so a folder
    /// full of them stops new copies until somebody takes them away.
    /// </remarks>
    public BackupResult Backup(BackupReason reason)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var newest = 0L;
        try
        {
            var folder = Path.Combine(_folder, BackupFolder);
            DurableFiles.CreateDirectory(folder);
            if (_unknown)
                return new BackupResult(BackupOutcome.Failed, newest, "The store does not know its own state; no recovery copy is made of it.");

            // What an earlier copy left under the working name is this store's own scrap.
            var staging = Path.Combine(folder, BackupStaging);
            DeleteDatabase(staging);

            // What is there, judged file by file; and first of all, copies an earlier call published and could
            // not tidy up after are tidied up now, so that failures cannot pile copies up.
            var copies = Copies(folder, out var otherBytes);
            Prune(folder, copies);
            newest = copies.Count > 0 ? copies[0].Generation : 0;

            var target = Path.Combine(folder, BackupName(_generation));
            if (newest == _generation)
            {
                // The copy is there and is what it should be. Whether its name was made durable is not known
                // (the call that published it may have failed just after), so it is made durable now.
                DurableFiles.SyncFile(target);
                DurableFiles.SyncDirectory(folder);
                return new BackupResult(BackupOutcome.Skipped, newest);
            }

            if (File.Exists(target))
                return new BackupResult(BackupOutcome.Failed, newest, $"A file that is not a recovery copy of this world stands where the copy of generation {_generation} belongs; it was left as it is.");

            // Reserve the full supported copy size: committed pages in the WAL may not be in the main file yet.
            var live = MaxDatabaseBytes(_limits);
            var held = otherBytes;
            foreach (var copy in copies)
                held += copy.Bytes;
            if (held + live > MaxBackupBytes(_limits))
                return new BackupResult(BackupOutcome.Failed, newest, $"The recovery copies' folder holds {held} bytes; another copy would pass the {MaxBackupBytes(_limits)} it is held to. Nothing was removed.");

            // Room on the disk for the copy and for the world's own next save, or no copy: a recovery copy
            // must never be what fills the disk the world is saved to.
            var free = new DriveInfo(OperatingSystem.IsWindows() ? Path.GetPathRoot(folder)! : folder).AvailableFreeSpace;
            if (free < 3 * live + (1 << 20))
                return new BackupResult(BackupOutcome.Failed, newest, "There is not enough free space for a recovery copy.");

            using (var destination = OpenConnection(staging, SqliteOpenMode.ReadWriteCreate))
            {
                _connection.BackupDatabase(destination);

                // One file that stands alone: no log beside it to be lost or left behind.
                Execute(destination, "PRAGMA journal_mode = DELETE");
            }

            Fault?.Invoke("backup-copied");
            var bytes = new FileInfo(staging).Length;
            if (bytes > MaxDatabaseBytes(_limits) || held + bytes > MaxBackupBytes(_limits))
            {
                DeleteDatabase(staging);
                return new BackupResult(BackupOutcome.Failed, newest, "The recovery copy came out larger than one may be.");
            }

            DurableFiles.SyncFile(staging);

            // Opened again as any world is, from its own file alone, and held to what it must be a copy of.
            var look = Inspect(staging, _limits, null, materialize: true);
            if (look.Outcome != OpenOutcome.Opened || look.WorldId != _worldId || Convert.ToBase64String(look.PublicKey!) != Identity.PublicKey || look.Generation != _generation)
            {
                DeleteDatabase(staging);
                return new BackupResult(BackupOutcome.Failed, newest, $"The recovery copy is not a whole copy of generation {_generation}: {look.Error ?? "it is of another state"}.");
            }

            // The shared-memory file a read leaves beside a database is no part of the copy.
            File.Delete(staging + "-shm");
            DurableFiles.Publish(staging, target);
            var published = new FileInfo(target);
            copies.Insert(0, (_generation, target, published.Length));
            newest = _generation;
            Fault?.Invoke("backup-published");

            // Only now, with the new copy under its name for good, do the oldest go.
            Prune(folder, copies);
            return new BackupResult(BackupOutcome.Completed, _generation);
        }
        catch (Exception e) when (e is SqliteException or IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            return new BackupResult(BackupOutcome.Failed, newest, e.Message);
        }
    }

    /// <summary>
    /// The recovery copies of this world in a folder, newest first, each judged by what it holds; and, apart,
    /// the bytes of the files that are named as copies and are not.
    /// </summary>
    private List<(long Generation, string File, long Bytes)> Copies(string folder, out long otherBytes)
    {
        otherBytes = 0;
        var copies = new List<(long Generation, string File, long Bytes)>();
        foreach (var file in Directory.EnumerateFiles(folder, BackupPrefix + "*" + BackupSuffix))
        {
            var generation = BackupGeneration(file);
            if (generation < 1)
                continue;
            var info = new FileInfo(file);
            if (IsCopy(file, generation))
                copies.Add((generation, file, info.Length));
            else
                otherBytes += info.Length;
        }

        copies.Sort((a, b) => b.Generation.CompareTo(a.Generation));
        return copies;
    }

    /// <summary>
    /// Whether a file named as the copy of a generation is one: a whole world database that decodes, of this
    /// world, of that generation. Anything that cannot be read as that, for whatever reason, is not.
    /// </summary>
    private bool IsCopy(string file, long generation)
    {
        bool copy;
        try
        {
            // A file that is not this store's is not handed to SQLite at all: opening somebody's database,
            // even only to read it, can leave a shared-memory file beside it.
            if (!HasCopyHeader(file))
                return false;
            var look = Inspect(file, _limits, null, materialize: true);
            copy = look.Outcome == OpenOutcome.Opened && look.WorldId == _worldId && Convert.ToBase64String(look.PublicKey!) == Identity.PublicKey && look.Generation == generation;
        }
        catch (Exception e) when (e is SqliteException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            copy = false;
        }

        return copy;
    }

    /// <summary>
    /// Whether a file's first hundred bytes are those of a recovery copy as this store publishes one, read as
    /// plain bytes: a SQLite database, of a world, of this layout, and kept with a rollback journal. The last is
    /// what every published copy is (it is converted before it gets its name) and what makes it safe to open:
    /// a database kept with a write-ahead log makes SQLite create files beside it for any reader. A file that
    /// fails this is not a copy, and nothing but these bytes of it is ever read.
    /// </summary>
    private static bool HasCopyHeader(string file)
    {
        Span<byte> header = stackalloc byte[100];
        using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            if (stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false) < header.Length)
                return false;
        }

        return header[..16].SequenceEqual("SQLite format 3\0"u8)
            && header[18] == 1
            && header[19] == 1
            && System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(header[60..]) == SchemaVersion
            && System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(header[68..]) == ApplicationId;
    }

    /// <summary>
    /// Removes the oldest of this world's recovery copies beyond <see cref="KeptBackups"/>, from the folder and
    /// from the list. Only files that were judged to be copies are in the list; nothing else is ever removed.
    /// </summary>
    private void Prune(string folder, List<(long Generation, string File, long Bytes)> copies)
    {
        if (copies.Count <= KeptBackups)
            return;
        // A visible replacement may be left by a failed publication sync. Make retained copies durable first.
        for (var i = 0; i < KeptBackups; i++)
            DurableFiles.SyncFile(copies[i].File);
        DurableFiles.SyncDirectory(folder);
        for (var i = copies.Count - 1; i >= KeptBackups; i--)
        {
            File.Delete(copies[i].File);
            copies.RemoveAt(i);
        }

        DurableFiles.SyncDirectory(folder);
    }

    /// <summary>
    /// Closes the database and gives the lease back. SQLite folds the write-ahead log into the database as the
    /// last connection closes. This says nothing about how the world was left: only a final commit marks it as
    /// closed cleanly.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        try
        {
            _connection.Dispose();
        }
        finally
        {
            _lease.Dispose();
        }
    }

    /// <summary>
    /// After a commit that threw and could not be taken back by name: finds out what is stored, under the lease
    /// this store still holds. The connection the transaction lived on is closed first, which ends whatever it
    /// had open, and a new one is asked: what that one reads is committed, not pending.
    /// <para>
    /// The old generation there means nothing changed. The new generation, with the bytes and the clean mark
    /// that were being written, means the commit is in the log, and no more than that: the error may have been
    /// the sync. So the log is folded into the database with full syncing before the save is acknowledged. If
    /// any of this cannot be done, or what is stored is neither, no answer is given: the outcome is unknown and
    /// the store writes nothing more.
    /// </para>
    /// </summary>
    private CommitOutcome Resolve(long expected, long next, int length, bool final, string first, out string cause)
    {
        cause = first;
        try
        {
            Reopen();
            var stored = ReadState(_connection);
            if (stored.Generation == expected)
                return CommitOutcome.Failed;
            if (stored.Generation == next && stored.Length == length && stored.Clean == final && StoredBytesAre(_encoded.AsSpan(0, length)))
            {
                if (Checkpoint(_connection, Fault, out var unsynced) == CheckpointOutcome.Completed)
                    return CommitOutcome.Committed;
                cause = unsynced ?? cause;
            }
        }
        catch (Exception e) when (e is SqliteException or IOException or InvalidOperationException or InvalidDataException)
        {
            cause = e.Message;
        }

        _unknown = true;
        Logs.Sim.Error($"The world store cannot tell whether a save was committed ({cause}); it writes nothing more.");
        return CommitOutcome.Unknown;
    }

    private void Reopen()
    {
        try
        {
            _connection.Dispose();
        }
        catch (SqliteException)
        {
            // A connection that cannot even close is why this one is being replaced.
        }

        Fault?.Invoke("reopen");
        _connection = OpenWriter(Path.Combine(_folder, DatabaseFile), _limits);
    }

    private bool StoredBytesAre(ReadOnlySpan<byte> expected)
    {
        using var blob = new SqliteBlob(_connection, "world", "snapshot", 1, readOnly: true);
        Span<byte> chunk = stackalloc byte[4096];
        var at = 0;
        while (at < expected.Length)
        {
            var read = blob.Read(chunk[..Math.Min(chunk.Length, expected.Length - at)]);
            if (read <= 0 || !chunk[..read].SequenceEqual(expected.Slice(at, read)))
                return false;
            at += read;
        }

        return true;
    }

    private CheckpointOutcome Checkpoint(out string? error) => Checkpoint(_connection, Fault, out error);

    /// <summary>
    /// Folds the whole log into the database and empties it. SQLite reports a checkpoint that could not finish
    /// in its result row, not as an error: the row is read, and anything but "not blocked, every frame moved" is
    /// a failure.
    /// </summary>
    private static CheckpointOutcome Checkpoint(SqliteConnection connection, Action<string>? fault, out string? error)
    {
        fault?.Invoke("checkpoint");
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            error = "The checkpoint gave no result.";
            return CheckpointOutcome.Failed;
        }

        var blocked = reader.GetInt64(0);
        var logged = reader.GetInt64(1);
        var moved = reader.GetInt64(2);
        if (blocked == 0 && logged >= 0 && logged == moved)
        {
            error = null;
            return CheckpointOutcome.Completed;
        }

        error = $"The checkpoint moved {moved} of {logged} log frames{(blocked != 0 ? " and was blocked" : "")}.";
        return CheckpointOutcome.Failed;
    }

    /// <summary>
    /// Whether the log has room for one more write, the largest there is, without passing what a world may be
    /// opened with (<see cref="MaxLogBytes"/>). SQLite's own limit on the log only cuts it back after a
    /// checkpoint; it does not stop it growing while checkpoints fail. So when the room is gone the log is
    /// folded into the database here, and if that cannot be done the write is not begun at all.
    /// </summary>
    private static bool HasLogRoom(SqliteConnection connection, string database, SaveLimits limits, Action<string>? fault, out string error)
    {
        error = "";
        var log = new FileInfo(database + "-wal");
        if (!log.Exists || log.Length + MaxDatabaseBytes(limits) <= MaxLogBytes(limits))
            return true;
        string? why;
        try
        {
            if (Checkpoint(connection, fault, out why) == CheckpointOutcome.Completed)
                return true;
        }
        catch (Exception e) when (e is SqliteException or IOException)
        {
            why = e.Message;
        }

        error = $"The world's log is full and could not be folded into its database ({why}); nothing more is written until it can.";
        return false;
    }

    private static bool TryRollback(SqliteTransaction? transaction)
    {
        if (transaction is null)
            return true;
        try
        {
            transaction.Rollback();
            return true;
        }
        catch (Exception e) when (e is SqliteException or InvalidOperationException)
        {
            return false;
        }
    }

    private static (long Generation, long Length, bool Clean) ReadState(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT generation, length(snapshot), clean FROM world WHERE id = 1";
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            throw new InvalidDataException("The world's row is gone.");
        return (reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2) == 1);
    }

    /// <summary>What a read-only look at a database found.</summary>
    private readonly record struct Look(
        OpenOutcome Outcome,
        string? Error,
        Guid WorldId,
        string Name,
        long CreatedMs,
        long SavedMs,
        long Generation,
        long CapturedTick,
        bool Clean,
        SaveCompatibility Compatibility,
        WorldSnapshot? Snapshot,
        byte[]? PublicKey = null
    );

    /// <summary>
    /// Looks at a database through a connection that can only read, and closes it again. Every check comes
    /// before the thing it guards: the file sizes before the file is opened, the header before the schema, the
    /// schema before a row is read, the row's own figures before the blob's length, the length before the blob
    /// is brought into memory, and the codec before a snapshot exists. With <paramref name="expected"/> a world
    /// saved with anything else is refused before its blob is read.
    /// </summary>
    private static Look Inspect(string database, SaveLimits limits, SaveCompatibility? expected, bool materialize)
    {
        static Look No(OpenOutcome outcome, string error) => new(outcome, error, Guid.Empty, "", 0, 0, 0, 0, false, default, null);

        var size = new FileInfo(database).Length;
        var log = new FileInfo(database + "-wal");
        if (size > MaxDatabaseBytes(limits) || (log.Exists && log.Length > MaxLogBytes(limits)))
            return No(OpenOutcome.Damaged, "The world's files are larger than any world this build keeps.");

        using var connection = OpenConnection(database, SqliteOpenMode.ReadOnly);
        try
        {
            if (Number(connection, "PRAGMA application_id") != ApplicationId)
                return No(OpenOutcome.Damaged, "The file is not a world.");
            var version = Number(connection, "PRAGMA user_version");
            if (version != SchemaVersion)
                return No(OpenOutcome.Incompatible, $"The world is stored in layout {version}; this build reads {SchemaVersion}.");

            using (var schema = connection.CreateCommand())
            {
                schema.CommandText = "SELECT type, name, tbl_name, sql FROM sqlite_schema ORDER BY name";
                using var reader = schema.ExecuteReader();
                if (!reader.Read() || reader.GetString(0) != "table" || reader.GetString(1) != "world" || reader.GetString(2) != "world"
                    || reader.IsDBNull(3) || reader.GetString(3) != Schema || reader.Read())
                    return No(OpenOutcome.Damaged, "The world's database is not laid out as this build lays one out.");
            }

            using (var check = connection.CreateCommand())
            {
                check.CommandText = "PRAGMA quick_check(1)";
                if (check.ExecuteScalar() as string != "ok")
                    return No(OpenOutcome.Damaged, "The world's database is damaged.");
            }

            if (Number(connection, "SELECT count(*) FROM world") != 1)
                return No(OpenOutcome.Damaged, "The world's database does not hold exactly one world.");

            Guid worldId;
            string name;
            long created, saved, generation, tick, clean, length;
            SaveCompatibility compatibility;
            using (var row = connection.CreateCommand())
            {
                row.CommandText =
                    "SELECT id, length(world_id), world_id, length(name), name, created_ms, saved_ms, generation, captured_tick, clean, save_version, generator_version, design_encoding, "
                    + "length(content_identity), content_identity, length(snapshot) FROM world";
                using var reader = row.ExecuteReader();
                reader.Read();
                if (reader.GetInt64(0) != 1 || reader.GetInt64(1) != 16 || reader.GetInt64(3) is < 1 or > SaveLimits.MaxNameBytes || reader.GetInt64(13) != ContentIdentity.WireSize)
                    return No(OpenOutcome.Damaged, "The world's row does not hold together.");
                var bytes = new byte[16];
                reader.GetBytes(2, 0, bytes, 0, 16);
                worldId = new Guid(bytes);
                name = reader.GetString(4);
                created = reader.GetInt64(5);
                saved = reader.GetInt64(6);
                generation = reader.GetInt64(7);
                tick = reader.GetInt64(8);
                clean = reader.GetInt64(9);
                reader.GetBytes(14, 0, bytes, 0, ContentIdentity.WireSize);
                var identity = new NetReader(bytes);
                ContentIdentity.TryRead(ref identity, out var content);
                compatibility = new SaveCompatibility(Checked(reader.GetInt64(10)), Checked(reader.GetInt64(11)), Checked(reader.GetInt64(12)), content);
                length = reader.GetInt64(15);
            }

            if (worldId == Guid.Empty || generation < 1 || tick < 0 || clean is not (0 or 1))
                return No(OpenOutcome.Damaged, "The world's row does not hold together.");
            byte[] publicKey;
            using (var keyRow = connection.CreateCommand())
            {
                keyRow.CommandText = "SELECT length(public_key), public_key FROM world WHERE id = 1";
                using var keyReader = keyRow.ExecuteReader();
                if (!keyReader.Read() || keyReader.GetInt64(0) != 91)
                    return No(OpenOutcome.Damaged, "The world identity is malformed.");
                publicKey = new byte[91];
                keyReader.GetBytes(1, 0, publicKey, 0, 91);
                if (!WorldIdentity.ValidPublicKey(publicKey))
                    return No(OpenOutcome.Damaged, "The world identity is malformed.");
            }
            var look = new Look(OpenOutcome.Opened, null, worldId, name, created, saved, generation, tick, clean == 1, compatibility, null, publicKey);
            if (expected is { } wanted && compatibility != wanted || !materialize && compatibility.SaveVersion != WorldSnapshotCodec.Version)
                return look with { Outcome = OpenOutcome.Incompatible, Error = "The world was saved with other content or by another build of the game." };
            if (length < 1 || length > limits.MaxEncodedBytes)
                return No(OpenOutcome.Damaged, "The world's save is larger than any world this build keeps.");
            if (!materialize)
                return look;

            // The length is known and within bounds: only now is the blob brought into memory, once, at its size.
            var blob = new byte[length];
            using (var stream = new SqliteBlob(connection, "world", "snapshot", 1, readOnly: true))
                stream.ReadExactly(blob);
            if (!WorldSnapshotCodec.TryDecode(blob, limits, out var snapshot, out var error))
                return No(OpenOutcome.Damaged, $"The world's save does not decode: {error}.");

            // The row and the save it carries must be of one world and one state.
            if (snapshot.WorldId != worldId || snapshot.Name != name || snapshot.CreatedUnixMs != created || snapshot.NextWorldTick != tick || snapshot.Compatibility != compatibility)
                return No(OpenOutcome.Damaged, "The world's row and its save are not of one state.");
            return look with { Snapshot = snapshot };
        }
        catch (SqliteException e) when (e.SqliteErrorCode is 11 or 26)
        {
            // SQLITE_CORRUPT and SQLITE_NOTADB: what the file is, not what went wrong reading it.
            return No(OpenOutcome.Damaged, $"The world's database is damaged: {e.Message}");
        }
    }

    private static int Checked(long value) => value is >= int.MinValue and <= int.MaxValue ? (int)value : int.MinValue;

    /// <summary>
    /// The one connection that writes a world. Exclusive locking is asked for before the first statement that
    /// touches the file, so the database is this connection's alone and its log's index is kept in memory, not
    /// in a shared file. Then the write-ahead log, full syncing, and a cache and a log limit that fit a save.
    /// </summary>
    private static SqliteConnection OpenWriter(string database, SaveLimits limits)
    {
        var connection = OpenConnection(database, SqliteOpenMode.ReadWrite);
        try
        {
            if (Text(connection, "PRAGMA locking_mode = EXCLUSIVE") != "exclusive" || Text(connection, "PRAGMA journal_mode = WAL") != "wal")
                throw new IOException("The world's database could not be opened for this server alone, with a write-ahead log.");
            Execute(connection, "PRAGMA synchronous = FULL");

            // A whole save in memory until its commit, so its pages are written to the log once; and a log
            // that is cut back to a save or two after a checkpoint instead of keeping its high-water size.
            Execute(connection, $"PRAGMA cache_size = -{(limits.MaxEncodedBytes + (4 << 20)) / 1024}");
            Execute(connection, $"PRAGMA journal_size_limit = {MaxDatabaseBytes(limits)}");
            if (Number(connection, "PRAGMA synchronous") != 2)
                throw new IOException("The world's database does not sync every commit.");
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private static SqliteConnection OpenConnection(string path, SqliteOpenMode mode)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = mode,
            Pooling = false,
            DefaultTimeout = BusySeconds,
        }.ToString());
        try
        {
            connection.Open();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private static void Execute(SqliteConnection connection, string sql, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static long Number(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar() is long value ? value : throw new InvalidDataException($"{sql} gave no number.");
    }

    private static string? Text(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar() as string;
    }

    private static byte[] IdentityBytes(ContentIdentity identity)
    {
        var bytes = new byte[ContentIdentity.WireSize];
        var writer = new NetWriter(bytes);
        identity.Write(ref writer);
        return bytes;
    }

    /// <summary>True if the folder has a database or anything SQLite would read as part of one.</summary>
    private static bool HasWorldFiles(string database) => File.Exists(database) || File.Exists(database + "-wal") || File.Exists(database + "-journal");

    /// <summary>Removes a database this store made under a staging name, with whatever SQLite left beside it.</summary>
    private static void DeleteDatabase(string path)
    {
        File.Delete(path);
        File.Delete(path + "-wal");
        File.Delete(path + "-shm");
        File.Delete(path + "-journal");
    }

    private static string BackupName(long generation) => BackupPrefix + generation.ToString("D19", CultureInfo.InvariantCulture) + BackupSuffix;

    /// <summary>The generation in a recovery copy's name, or -1 for a file that is not one of this store's copies.</summary>
    internal static long BackupGeneration(string file)
    {
        var name = Path.GetFileName(file);
        if (name.Length != BackupPrefix.Length + 19 + BackupSuffix.Length || !name.StartsWith(BackupPrefix, StringComparison.Ordinal) || !name.EndsWith(BackupSuffix, StringComparison.Ordinal))
            return -1;
        return long.TryParse(name.AsSpan(BackupPrefix.Length, 19), NumberStyles.None, CultureInfo.InvariantCulture, out var generation) ? generation : -1;
    }

    private static OpenResult Refused(OpenOutcome outcome, string? error) => new(outcome, null, null, 0, false, error);

    /// <summary>Why the SQLite that was loaded is not the one this store was reviewed with, or null when it is.</summary>
    private static string? CheckRuntime()
    {
        try
        {
            using var connection = new SqliteConnection("Data Source=:memory:;Pooling=False");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT sqlite_version(), sqlite_source_id()";
            using var reader = command.ExecuteReader();
            reader.Read();
            var version = reader.GetString(0);
            var source = reader.GetString(1);
            return version == AuditedSqliteVersion && source == AuditedSqliteSourceId
                ? null
                : $"World saves need SQLite {AuditedSqliteVersion} as it was reviewed; this is {version} ({source}).";
        }
        catch (Exception e) when (e is SqliteException or DllNotFoundException or TypeInitializationException or InvalidOperationException)
        {
            return $"SQLite could not be loaded: {e.Message}";
        }
    }

    /// <summary>The SQLite that is loaded, as it names itself: for a log line and for the test that pins it.</summary>
    internal static (string Version, string SourceId) LoadedSqlite()
    {
        using var connection = new SqliteConnection("Data Source=:memory:;Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT sqlite_version(), sqlite_source_id()";
        using var reader = command.ExecuteReader();
        reader.Read();
        return (reader.GetString(0), reader.GetString(1));
    }

    /// <summary>
    /// A world's lease: an exclusive lock on its lock file for as long as this lives, and the folder's place in
    /// the table of folders this process holds. The file is opened or created and never deleted, so there is
    /// always one file, and whoever locks it locks the same one.
    /// </summary>
    private sealed class Lease : IDisposable
    {
        private static readonly HashSet<string> Held = new(StringComparer.Ordinal);
        private readonly string _folder;
        private FileStream? _file;

        private Lease(string folder, FileStream file)
        {
            _folder = folder;
            _file = file;
        }

        /// <summary>
        /// Takes the lease, or says why not: <paramref name="busy"/> when someone holds it, an
        /// <paramref name="error"/> otherwise. The lock is then tried a second time, which must be refused:
        /// where it is granted, a lock holds nobody off on this machine (locking switched off, a file system
        /// without locks), and a second server could run the same world. That it is refused here says nothing
        /// about another machine on a shared file system.
        /// </summary>
        public static Lease? TryTake(string folder, out bool busy, out string error)
        {
            busy = false;
            lock (Held)
            {
                if (!Held.Add(folder))
                {
                    busy = true;
                    error = "The world is in use.";
                    return null;
                }
            }

            FileStream? file = null;
            try
            {
                var path = Path.Combine(folder, LockFile);
                file = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                try
                {
                    using var second = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                    error = "This file system does not keep exclusive locks, so a world cannot be run from it safely.";
                }
                catch (IOException e) when (e is not (FileNotFoundException or DirectoryNotFoundException or PathTooLongException))
                {
                    error = "";
                    return new Lease(folder, file);
                }
            }
            catch (IOException e) when (e is not (FileNotFoundException or DirectoryNotFoundException or PathTooLongException or DriveNotFoundException))
            {
                busy = true;
                error = "The world is in use by another server.";
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                error = e.Message;
            }

            file?.Dispose();
            lock (Held)
                Held.Remove(folder);
            return null;
        }

        public void Dispose()
        {
            if (_file is null)
                return;
            _file.Dispose();
            _file = null;
            lock (Held)
                Held.Remove(_folder);
        }
    }
}
