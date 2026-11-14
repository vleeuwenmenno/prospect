using System.Diagnostics;
using System.Numerics;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Prospect.Engine.Time;
using Prospect.Game.Persistence;
using Prospect.Game.Server;
using Prospect.Game.World;
using Xunit;

namespace Prospect.Tests;

/// <summary>
/// The world store on a real disk, through the real SQLite: what it keeps, whom it keeps out, what it refuses
/// and leaves alone, and what it says when writing goes wrong. What is simulated is named where it is: a crash
/// is the files as a killed process leaves them, with everything the system had been handed; nothing here cuts
/// the power, and nothing is claimed about that.
/// </summary>
public sealed class SqliteWorldStoreTests : IDisposable
{
    private static readonly GameContent Shipped = GameContent.Load();
    private static readonly SaveCompatibility Compatible = SaveCompatibility.Of(Shipped);

    // Beside the test binary: a real file system of the machine, not a memory disk.
    private readonly string _root = Path.Combine(AppContext.BaseDirectory, "save-tests", Guid.NewGuid().ToString("N"));

    public SqliteWorldStoreTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // A test's leftovers are not worth failing it for.
        }
    }

    private string Folder(string name = "world") => Path.Combine(_root, name);

    private static string Db(string folder) => Path.Combine(folder, SqliteWorldStore.DatabaseFile);

    /// <summary>The populated world of the lifecycle tests: owners, damaged custom hulls, jumps, pushed asteroids.</summary>
    private static WorldSnapshot Populated()
    {
        Assert.True(WorldSnapshotCodec.TryDecode(SaveLifecycleTests.Populated.Value, SaveLimits.Default, out var snapshot, out var error), error);
        return snapshot!;
    }

    private static byte[] Hash(string file) => File.Exists(file) ? SHA256.HashData(File.ReadAllBytes(file)) : [];

    private SqliteWorldStore Created(string folder, WorldSnapshot? initial = null)
    {
        var created = SqliteWorldStore.Create(folder, initial ?? Populated());
        Assert.True(created.Outcome == CreateOutcome.Created, created.Error);
        Assert.Equal(1, created.Generation);
        return (SqliteWorldStore)created.Store!;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LaunchValidationUsesTheLeasedSnapshotBeforeAnyWrite(bool withWal)
    {
        var original = Folder("original");
        var candidate = Folder("candidate");
        var state = Populated();
        state.Seed = 17;
        using (var store = Created(original, state))
        {
            // The validator must see the newest committed snapshot, including one held only in the WAL.
            state.Seed = 29;
            state.NextWorldTick += 60;
            Assert.Equal(CommitOutcome.Committed, store.Commit(state, 1, final: !withWal).Outcome);
            Directory.CreateDirectory(candidate);
            File.Copy(Db(original), Db(candidate));
            if (withWal)
            {
                Assert.True(new FileInfo(Db(original) + "-wal").Length > 0);
                File.Copy(Db(original) + "-wal", Db(candidate) + "-wal");
            }
        }

        // A read-only SQLite connection may create an empty WAL beside a checkpointed database.
        static byte[] WalBytes(string folder) => File.Exists(Db(folder) + "-wal") ? File.ReadAllBytes(Db(folder) + "-wal") : [];
        var database = Hash(Db(candidate));
        var wal = WalBytes(candidate);
        var calls = 0;
        var refused = SqliteWorldStore.Open(candidate, Compatible, snapshot =>
        {
            calls++;
            Assert.Equal(29ul, snapshot.Seed);
            Assert.Equal(state.NextWorldTick, snapshot.NextWorldTick);
            Assert.Equal(OpenOutcome.Busy, SqliteWorldStore.Open(candidate, Compatible).Outcome);
            return "Launch seed conflicts.";
        });
        Assert.Equal(1, calls);
        Assert.Equal(OpenOutcome.Incompatible, refused.Outcome);
        Assert.Equal("Launch seed conflicts.", refused.Error);
        Assert.Null(refused.Store);
        Assert.Equal(database, Hash(Db(candidate)));
        Assert.Equal(wal, WalBytes(candidate));
        var summary = SqliteWorldStore.Summarize(candidate);
        Assert.Equal(OpenOutcome.Opened, summary.Outcome);
        Assert.Equal(!withWal, summary.WasCleanClose);

        // Expected failures retain their classification; unexpected exceptions escape after releasing the lease.
        var failed = SqliteWorldStore.Open(candidate, Compatible, _ => throw new IOException("validator I/O"));
        Assert.Equal(OpenOutcome.Failed, failed.Outcome);
        Assert.Equal("validator I/O", failed.Error);
        var unexpected = new InvalidOperationException("validator bug");
        Assert.Same(unexpected, Assert.Throws<InvalidOperationException>(() =>
            SqliteWorldStore.Open(candidate, Compatible, _ => throw unexpected)));
        Assert.Equal(database, Hash(Db(candidate)));
        Assert.Equal(wal, WalBytes(candidate));
        Assert.Equal(!withWal, SqliteWorldStore.Summarize(candidate).WasCleanClose);

        WorldSnapshot? validated = null;
        var opened = SqliteWorldStore.Open(candidate, Compatible, snapshot =>
        {
            validated = snapshot;
            return snapshot.Seed == 29 ? null : "Wrong seed.";
        });
        Assert.Equal(OpenOutcome.Opened, opened.Outcome);
        using (opened.Store!)
        {
            Assert.Same(validated, opened.Snapshot);
            Assert.Equal(!withWal, opened.WasCleanClose);
            var bypassCalled = false;
            var busy = SqliteWorldStore.Open(candidate, Compatible, _ =>
            {
                bypassCalled = true;
                return null;
            });
            Assert.Equal(OpenOutcome.Busy, busy.Outcome);
            Assert.False(bypassCalled);
            Assert.Null(busy.Store);
        }
    }

    private static (SqliteWorldStore Store, OpenResult Result) Opened(string folder)
    {
        var opened = SqliteWorldStore.Open(folder, Compatible);
        Assert.True(opened.Outcome == OpenOutcome.Opened, opened.Error);
        return ((SqliteWorldStore)opened.Store!, opened);
    }

    /// <summary>Changes a stored world behind the store's back, as a damaged or foreign file would be.</summary>
    private static void Tamper(string folder, string sql)
    {
        using var connection = new SqliteConnection($"Data Source={Db(folder)};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    [Fact]
    public void TheSqliteThatLoadsIsTheBuildThisStoreWasReviewedWith()
    {
        // The native library loads here, on this machine's platform, and names itself as the audited build.
        var (version, source) = SqliteWorldStore.LoadedSqlite();
        Console.WriteLine($"SQLite {version} {source}");
        Assert.Equal(SqliteWorldStore.AuditedSqliteVersion, version);
        Assert.Equal(SqliteWorldStore.AuditedSqliteSourceId, source);
    }

    [Fact]
    public void AWorldIsCreatedSavedClosedAndComesBackAsItWasSaved()
    {
        var folder = Folder();
        var initial = Populated();
        using (var store = Created(folder, initial))
        {
            Assert.True(File.Exists(Db(folder)));
            Assert.True(File.Exists(Path.Combine(folder, SqliteWorldStore.LockFile)));
            Assert.False(File.Exists(Db(folder) + ".creating"));

            // This server's alone: the log's index is in memory, and no shared-memory file is made for it.
            Assert.Equal(2, store.Commit(initial, 1, final: false).Generation);
            Assert.True(new FileInfo(Db(folder) + "-wal").Length > 0);
            Assert.False(File.Exists(Db(folder) + "-shm"));
        }

        // Disposed without a final save: the world is whole, and it was not closed cleanly.
        var (reopened, first) = Opened(folder);
        Assert.Equal((2L, false), (first.Generation, first.WasCleanClose));
        Assert.Equal(WorldSnapshotCodec.Encode(initial), WorldSnapshotCodec.Encode(first.Snapshot!));

        // A changed state, saved as the close: generation 2, checkpointed, clean.
        var changed = first.Snapshot!;
        changed.NextWorldTick += 600;
        changed.Hulls[0].State.Position = new Double3(1, 2, 3);
        changed.Owners[0].Controlled = changed.Hulls[2].Entity;
        var commit = reopened.Commit(changed, 2, final: true);
        Assert.Equal((CommitOutcome.Committed, 3L, changed.NextWorldTick, CheckpointOutcome.Completed), (commit.Outcome, commit.Generation, commit.CapturedTick, commit.Checkpoint));
        Assert.Null(commit.Error);
        Assert.Equal(0, new FileInfo(Db(folder) + "-wal").Length);
        var expected = WorldSnapshotCodec.Encode(changed);
        reopened.Dispose();
        Assert.False(File.Exists(Db(folder) + "-wal"));

        var (again, second) = Opened(folder);
        Assert.Equal((3L, true), (second.Generation, second.WasCleanClose));
        Assert.Equal(expected, WorldSnapshotCodec.Encode(second.Snapshot!));

        // Opening it made it a running world again, durably: a run that just ends was not a clean close.
        again.Dispose();
        var (last, third) = Opened(folder);
        Assert.Equal((3L, false), (third.Generation, third.WasCleanClose));
        last.Dispose();
    }

    [Fact]
    public void ACommitIsHeldToTheGenerationItExpectsAndToItsOwnWorld()
    {
        var folder = Folder();
        using var store = Created(folder);
        var state = Populated();
        Assert.Equal(2, store.Commit(state, 1, final: false).Generation);

        // The same expectation again is a writer that is behind: nothing changes, and it is told where the world is.
        state.NextWorldTick += 5;
        var stale = store.Commit(state, 1, final: true);
        Assert.Equal((CommitOutcome.Conflict, 2L), (stale.Outcome, stale.Generation));
        Assert.Equal(2, store.Generation);
        Assert.Equal(CommitOutcome.Committed, store.Commit(state, 2, final: false).Outcome);

        // Another world's state is not this world's next state, whatever generation it names.
        var other = Populated();
        other.WorldId = Guid.NewGuid();
        Assert.Equal(CommitOutcome.Rejected, store.Commit(other, 3, final: false).Outcome);
        Assert.Equal(3, store.Generation);
        store.Dispose();

        var (reopened, result) = Opened(folder);
        Assert.Equal(3, result.Generation);
        Assert.False(result.WasCleanClose);
        Assert.Equal(state.NextWorldTick, result.Snapshot!.NextWorldTick);
        reopened.Dispose();
    }

    [Fact]
    public void AWorldThatIsOpenIsRefusedToEveryoneElseInThisProcess()
    {
        var folder = Folder();
        var store = Created(folder);
        Assert.Equal(OpenOutcome.Busy, SqliteWorldStore.Open(folder, Compatible).Outcome);
        Assert.Equal(OpenOutcome.Busy, SqliteWorldStore.Open(folder + Path.DirectorySeparatorChar, Compatible).Outcome);
        Assert.Equal(OpenOutcome.Busy, SqliteWorldStore.Summarize(folder).Outcome);
        Assert.Equal(CreateOutcome.Exists, SqliteWorldStore.Create(folder, Populated()).Outcome);

        // The refusals took nothing from the one who holds it.
        Assert.Equal(CommitOutcome.Committed, store.Commit(Populated(), 1, final: true).Outcome);
        store.Dispose();

        // The lease file stays where it is, the same file, for whoever comes next.
        Assert.True(File.Exists(Path.Combine(folder, SqliteWorldStore.LockFile)));
        var (next, result) = Opened(folder);
        Assert.True(result.WasCleanClose);
        next.Dispose();
        next.Dispose();
    }

    /// <summary>
    /// Run by the test below in a process of its own, with the folder to try in the environment. On its own,
    /// with nothing in the environment, it does nothing.
    /// </summary>
    [Fact]
    public void ChildProcessProbe()
    {
        var folder = Environment.GetEnvironmentVariable("PROSPECT_SAVE_PROBE");
        var answer = Environment.GetEnvironmentVariable("PROSPECT_SAVE_PROBE_ANSWER");
        if (folder is null || answer is null)
            return;
        var opened = SqliteWorldStore.Open(folder, Compatible);
        File.WriteAllText(answer, $"{opened.Outcome}|{opened.Generation}");
        opened.Store?.Dispose();
    }

    private string Probe(string folder, bool lockingOff = false)
    {
        var answer = Path.Combine(_root, $"answer-{Guid.NewGuid():N}.txt");
        var start = new ProcessStartInfo(Environment.ProcessPath!)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(typeof(SqliteWorldStoreTests).Assembly.Location);
        start.ArgumentList.Add("-method");
        start.ArgumentList.Add($"{typeof(SqliteWorldStoreTests).FullName}.{nameof(ChildProcessProbe)}");
        start.Environment["PROSPECT_SAVE_PROBE"] = folder;
        start.Environment["PROSPECT_SAVE_PROBE_ANSWER"] = answer;
        if (lockingOff)
            start.Environment["DOTNET_SYSTEM_IO_DISABLEFILELOCKING"] = "1";
        using var child = Process.Start(start)!;
        child.StandardOutput.ReadToEnd();
        Assert.True(child.WaitForExit(60000), "the probing process did not finish");
        Assert.True(File.Exists(answer), "the probing process gave no answer");
        return File.ReadAllText(answer);
    }

    [Fact]
    public void AWorldThatIsOpenIsRefusedToAnotherProcessAndAProcessThatCannotLockIsRefusedAWorld()
    {
        var folder = Folder();
        var store = Created(folder);
        Assert.Equal(2, store.Commit(Populated(), 1, final: false).Generation);

        // Another process, the same store, the same folder, while this one holds it.
        Assert.Equal("Busy|0", Probe(folder));

        // A process told not to lock files finds out that its lock holds nobody off, and takes no world: not
        // this one, which is in use, and not one that is free either. The switch is the Unix runtime's; on
        // Windows a file opened for one process alone is that process's whatever a process is told.
        if (!OperatingSystem.IsWindows())
            Assert.StartsWith("Failed|", Probe(folder, lockingOff: true));
        store.Dispose();
        if (!OperatingSystem.IsWindows())
            Assert.StartsWith("Failed|", Probe(folder, lockingOff: true));

        // Released, the other process gets it, at the generation that was committed here.
        Assert.Equal("Opened|2", Probe(folder));

        // And it gave it back: a process that ends, or is killed, holds no lease.
        var (again, result) = Opened(folder);
        Assert.Equal(2, result.Generation);
        again.Dispose();
    }

    [Fact]
    public void AWorldOfOtherContentIsRefusedAndItsDatabaseAndLogAreLeftByteForByte()
    {
        // A world as a killed server leaves it: commits in the log that were never folded into the database.
        var folder = Folder("running");
        var crashed = Folder("crashed");
        var state = Populated();
        using (var store = Created(folder, state))
        {
            for (var generation = 1; generation <= 3; generation++)
            {
                state.NextWorldTick += 60;
                Assert.Equal(CommitOutcome.Committed, store.Commit(state, generation, final: false).Outcome);
            }

            Directory.CreateDirectory(crashed);
            File.Copy(Db(folder), Db(crashed));
            File.Copy(Db(folder) + "-wal", Db(crashed) + "-wal");
        }

        Assert.True(new FileInfo(Db(crashed) + "-wal").Length > 0);
        var database = Hash(Db(crashed));
        var log = Hash(Db(crashed) + "-wal");

        foreach (var other in new[]
        {
            Compatible with { GeneratorVersion = Compatible.GeneratorVersion + 1 },
            Compatible with { SaveVersion = Compatible.SaveVersion + 1 },
            Compatible with { DesignEncoding = Compatible.DesignEncoding + 1 },
            Compatible with { Content = default },
        })
        {
            var refused = SqliteWorldStore.Open(crashed, other);
            Assert.Equal(OpenOutcome.Incompatible, refused.Outcome);
            Assert.Null(refused.Store);
            Assert.Null(refused.Snapshot);
            Assert.Equal(database, Hash(Db(crashed)));
            Assert.Equal(log, Hash(Db(crashed) + "-wal"));
        }

        // A list of worlds can still say what it is, without running it or changing it.
        var summary = SqliteWorldStore.Summarize(crashed);
        Assert.Equal((OpenOutcome.Opened, state.WorldId, state.Name, 4L, state.NextWorldTick, false), (summary.Outcome, summary.WorldId, summary.Name, summary.Generation, summary.NextWorldTick, summary.WasCleanClose));
        Assert.Equal(Compatible, summary.Compatibility);
        Assert.Equal(database, Hash(Db(crashed)));
        Assert.Equal(log, Hash(Db(crashed) + "-wal"));

        // With the right content the killed world opens at its last acknowledged commit, and says how it ended.
        var (recovered, result) = Opened(crashed);
        Assert.Equal((4L, false), (result.Generation, result.WasCleanClose));
        Assert.Equal(WorldSnapshotCodec.Encode(state), WorldSnapshotCodec.Encode(result.Snapshot!));
        recovered.Dispose();
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("truncated")]
    [InlineData("not a world")]
    [InlineData("newer layout")]
    [InlineData("extra table")]
    [InlineData("other columns")]
    [InlineData("save that does not decode")]
    [InlineData("row of another state")]
    [InlineData("row of another world")]
    [InlineData("two rows")]
    [InlineData("larger than a world")]
    [InlineData("damaged page")]
    public void AWorldThatIsNotWhatThisStoreWroteIsRefusedBeforeAnythingIsPublishedAndLeftAsItIs(string what)
    {
        var folder = Folder();
        Created(folder).Dispose();
        var limits = SaveLimits.Default;
        var expected = OpenOutcome.Damaged;
        switch (what)
        {
            case "garbage":
                File.WriteAllBytes(Db(folder), RandomNumberGenerator.GetBytes(8192));
                break;
            case "truncated":
                File.WriteAllBytes(Db(folder), File.ReadAllBytes(Db(folder)).AsSpan(0, 5000).ToArray());
                break;
            case "not a world":
                Tamper(folder, "PRAGMA application_id = 7");
                break;
            case "newer layout":
                Tamper(folder, "PRAGMA user_version = 999");
                expected = OpenOutcome.Incompatible;
                break;
            case "extra table":
                Tamper(folder, "CREATE TABLE claims (id INTEGER PRIMARY KEY)");
                break;
            case "other columns":
                Tamper(folder, "ALTER TABLE world ADD COLUMN token TEXT");
                break;
            case "save that does not decode":
                Tamper(folder, "UPDATE world SET snapshot = substr(snapshot, 1, length(snapshot) - 9)");
                break;
            case "row of another state":
                Tamper(folder, "UPDATE world SET captured_tick = captured_tick + 1");
                break;
            case "row of another world":
                Tamper(folder, "UPDATE world SET world_id = randomblob(16)");
                break;
            case "two rows":
                Tamper(folder, "PRAGMA ignore_check_constraints = ON; INSERT INTO world SELECT 2, world_id, name, created_ms, generation, captured_tick, saved_ms, clean, save_version, generator_version, design_encoding, content_identity, snapshot, public_key FROM world");
                break;
            case "larger than a world":
                limits = new SaveLimits { MaxHulls = 2, MaxBlocks = 64, MaxRocks = 1, MaxOwners = 1 };
                break;
            case "damaged page":
                var bytes = File.ReadAllBytes(Db(folder));
                Array.Fill(bytes, (byte)0xA5, 4096 + 16, 2000);
                File.WriteAllBytes(Db(folder), bytes);
                break;
        }

        var before = Hash(Db(folder));
        var refused = SqliteWorldStore.Open(folder, Compatible, limits);
        Assert.Equal(expected, refused.Outcome);
        Assert.Null(refused.Store);
        Assert.Null(refused.Snapshot);
        Assert.False(string.IsNullOrEmpty(refused.Error));

        // Nothing was opened for writing, and the lease went back: the file is as it was, and can be looked at again.
        Assert.Equal(before, Hash(Db(folder)));
        Assert.NotEqual(OpenOutcome.Busy, SqliteWorldStore.Summarize(folder).Outcome);

        // A world is never made over one that is there, whatever state that one is in.
        Assert.Equal(CreateOutcome.Exists, SqliteWorldStore.Create(folder, Populated()).Outcome);
        Assert.Equal(before, Hash(Db(folder)));
    }

    [Fact]
    public void CreatingNeverTouchesWhatIsThereAndFinishesWhatAnInterruptedCreateLeft()
    {
        // Nothing there.
        Assert.Equal(OpenOutcome.Missing, SqliteWorldStore.Open(Folder("none"), Compatible).Outcome);
        Assert.Equal(OpenOutcome.Missing, SqliteWorldStore.Summarize(Folder("none")).Outcome);
        Assert.False(Directory.Exists(Folder("none")));

        // A log without its database is still somebody's world: not ours to build over.
        var stray = Folder("stray");
        Directory.CreateDirectory(stray);
        File.WriteAllBytes(Db(stray) + "-wal", [1, 2, 3]);
        Assert.Equal(CreateOutcome.Exists, SqliteWorldStore.Create(stray, Populated()).Outcome);
        Assert.Equal([1, 2, 3], File.ReadAllBytes(Db(stray) + "-wal"));
        Assert.False(File.Exists(Db(stray)));

        // Unowned staging is preserved; a name alone is not evidence that it is safe to delete.
        var folder = Folder();
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Db(folder) + ".creating", RandomNumberGenerator.GetBytes(3000));
        Assert.Equal(OpenOutcome.Missing, SqliteWorldStore.Open(folder, Compatible).Outcome);
        var stagingBytes = File.ReadAllBytes(Db(folder) + ".creating");
        Assert.Equal(CreateOutcome.Failed, SqliteWorldStore.Create(folder, Populated()).Outcome);
        Assert.Equal(stagingBytes, File.ReadAllBytes(Db(folder) + ".creating"));

        // A world too large to save is not created at all.
        var small = SqliteWorldStore.Create(Folder("small"), Populated(), new SaveLimits { MaxHulls = 2, MaxBlocks = 64, MaxRocks = 1, MaxOwners = 1 });
        Assert.Equal(CreateOutcome.Failed, small.Outcome);
        Assert.False(File.Exists(Db(Folder("small"))));
        Assert.Equal(OpenOutcome.Missing, SqliteWorldStore.Summarize(Folder("small")).Outcome);
    }

    [Fact]
    public void ASaveThatCannotBeWrittenLeavesTheLastOneAndSaysSo()
    {
        var folder = Folder();
        var state = Populated();
        var store = Created(folder, state);
        Assert.Equal(2, store.Commit(state, 1, final: false).Generation);

        // The database may not grow: a state that needs more room than it has is refused by SQLite itself,
        // inside the transaction, and the transaction is taken back by name.
        var pages = (long)new SqliteCommand("PRAGMA page_count", store.Connection).ExecuteScalar()!;
        new SqliteCommand($"PRAGMA max_page_count = {pages}", store.Connection).ExecuteNonQuery();
        var grown = SaveLifecycleTests.Repeated(state, 40);
        Array.Copy(state.Owners, grown.Owners, state.OwnerCount);
        var full = store.Commit(grown, 2, final: false);
        Assert.Equal((CommitOutcome.Failed, 2L), (full.Outcome, full.Generation));
        Assert.Contains("full", full.Error);
        Assert.Equal(2, store.Generation);

        // Room again, and the same state goes in under the same expectation.
        new SqliteCommand("PRAGMA max_page_count = 1073741823", store.Connection).ExecuteNonQuery();
        Assert.Equal((CommitOutcome.Committed, 3L), (store.Commit(grown, 2, final: false).Outcome, store.Generation));

        // A failure just before the commit: rolled back, unchanged, and said to be unchanged.
        store.Fault = step => { if (step == "commit") throw new IOException("the disk went away"); };
        var failed = store.Commit(state, 3, final: true);
        Assert.Equal((CommitOutcome.Failed, "the disk went away", CheckpointOutcome.NotAttempted), (failed.Outcome, failed.Error, failed.Checkpoint));
        Assert.Equal(3, store.Generation);

        // A failure just after it: the save is durable, and nothing that follows may say otherwise.
        store.Fault = step => { if (step == "committed") throw new IOException("the cache could not be written"); };
        var kept = store.Commit(state, 3, final: false);
        Assert.Equal((CommitOutcome.Committed, 4L), (kept.Outcome, kept.Generation));
        store.Fault = null;
        store.Dispose();

        var (reopened, result) = Opened(folder);
        Assert.Equal((4L, false), (result.Generation, result.WasCleanClose));
        Assert.Equal(WorldSnapshotCodec.Encode(state), WorldSnapshotCodec.Encode(result.Snapshot!));
        reopened.Dispose();
    }

    [Fact]
    public void ACommitWhoseOutcomeIsNotCertainIsLookedUpAndOnlyACertainAnswerIsGiven()
    {
        var folder = Folder();
        var state = Populated();
        var store = Created(folder, state);
        state.NextWorldTick += 60;

        // The connection dies under the commit: the rollback cannot be confirmed, so the store asks the
        // database, on a new connection under the lease it still holds, and finds the old generation.
        store.Fault = step => { if (step == "commit") store.Connection.Dispose(); };
        var lost = store.Commit(state, 1, final: false);
        Assert.Equal((CommitOutcome.Failed, 1L), (lost.Outcome, lost.Generation));
        store.Fault = null;

        // Certain that nothing changed, it goes on: the same state, the same expectation.
        Assert.Equal((CommitOutcome.Committed, 2L), (store.Commit(state, 1, final: false).Outcome, store.Generation));

        // The same again, and this time the database cannot be asked either. No answer is made up.
        store.Fault = step =>
        {
            if (step == "commit") store.Connection.Dispose();
            if (step == "reopen") throw new IOException("the disk went away");
        };
        var unknown = store.Commit(state, 2, final: true);
        Assert.Equal(CommitOutcome.Unknown, unknown.Outcome);
        Assert.Equal("the disk went away", unknown.Error);
        store.Fault = null;

        // From then on it writes nothing: no save, no final save, no recovery copy of a state it cannot name.
        Assert.Equal(CommitOutcome.Unknown, store.Commit(state, 2, final: true).Outcome);
        Assert.Equal(BackupOutcome.Failed, store.Backup(BackupReason.Final).Outcome);
        store.Dispose();

        // The next open says which it was: here the old state, not marked as closed cleanly.
        var (reopened, result) = Opened(folder);
        Assert.Equal((2L, false), (result.Generation, result.WasCleanClose));
        reopened.Dispose();
    }

    [Fact]
    public void ACheckpointThatFailsIsReportedBesideACommitThatStands()
    {
        var folder = Folder();
        var state = Populated();
        var store = Created(folder, state);
        store.Fault = step => { if (step == "checkpoint") throw new IOException("the log could not be folded in"); };
        var commit = store.Commit(state, 1, final: true);
        Assert.Equal((CommitOutcome.Committed, 2L, CheckpointOutcome.Failed, "the log could not be folded in"), (commit.Outcome, commit.Generation, commit.Checkpoint, commit.Error));

        // The log still holds the save the checkpoint did not move.
        Assert.True(new FileInfo(Db(folder) + "-wal").Length > 0);
        store.Dispose();

        var (reopened, result) = Opened(folder);
        Assert.Equal((2L, true), (result.Generation, result.WasCleanClose));
        reopened.Dispose();
    }

    [Fact]
    public void AWorldThatCannotBeWrittenToIsNotOpenedAndNotChanged()
    {
        if (!OperatingSystem.IsLinux() || Environment.UserName == "root")
            return;
        var folder = Folder();
        Created(folder).Dispose();
        File.SetUnixFileMode(Db(folder), UnixFileMode.UserRead);
        try
        {
            var before = Hash(Db(folder));

            // It reads as a whole world, and the one statement that must be written cannot be.
            var refused = SqliteWorldStore.Open(folder, Compatible);
            Assert.Equal(OpenOutcome.Failed, refused.Outcome);
            Assert.Null(refused.Store);
            Assert.Equal(before, Hash(Db(folder)));
            Assert.Equal(OpenOutcome.Opened, SqliteWorldStore.Summarize(folder).Outcome);
        }
        finally
        {
            File.SetUnixFileMode(Db(folder), UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        Opened(folder).Store.Dispose();
    }

    [Fact]
    public void RecoveryCopiesStandAloneAreBoundedAndAreNeverLostToAFailedCopy()
    {
        var folder = Folder();
        var backups = Path.Combine(folder, SqliteWorldStore.BackupFolder);
        var state = Populated();
        var store = Created(folder, state);
        var saved = new Dictionary<long, byte[]>();
        long generation = 1;
        void Save()
        {
            state.NextWorldTick += 60;
            state.Hulls[1].State.Energy = generation;
            Assert.Equal(CommitOutcome.Committed, store.Commit(state, generation, final: false).Outcome);
            saved[++generation] = WorldSnapshotCodec.Encode(state);
        }

        string[] Copies() => [.. Directory.GetFiles(backups, "world-*.db").Select(Path.GetFileName).Order()!];

        Save();
        Assert.Equal((BackupOutcome.Completed, 2L), (store.Backup(BackupReason.Initial).Outcome, store.Backup(BackupReason.Initial).Generation));
        Assert.Equal(BackupOutcome.Skipped, store.Backup(BackupReason.Interval).Outcome);
        Assert.Equal(["world-0000000000000000002.db"], Copies());

        // Somebody else's files in the folder are not copies of this store's making, and are not counted or removed.
        File.WriteAllText(Path.Combine(backups, "notes.txt"), "mine");
        File.WriteAllText(Path.Combine(backups, "world-before-the-move.db"), "mine too");

        // A copy that fails takes nothing with it: no new name, no old copy gone, and the commit it followed stands.
        Save();
        store.Fault = step => { if (step == "backup-copied") throw new IOException("no room"); };
        var failed = store.Backup(BackupReason.Interval);
        Assert.Equal((BackupOutcome.Failed, 2L, "no room"), (failed.Outcome, failed.Generation, failed.Error));
        store.Fault = null;
        Assert.Equal(["world-0000000000000000002.db", "world-before-the-move.db"], Copies());
        Assert.Equal(3, store.Generation);

        // Five generations copied: the newest three are kept, and an old one goes only after its successor is there.
        Assert.Equal(BackupOutcome.Completed, store.Backup(BackupReason.Interval).Outcome);
        for (var i = 0; i < 3; i++)
        {
            Save();
            var seen = false;
            store.Fault = step => { if (step == "backup-published") seen = Copies().Length == Math.Min(i + 4, 5); };
            Assert.Equal(BackupOutcome.Completed, store.Backup(BackupReason.Interval).Outcome);
            Assert.True(seen, "the new copy was not there before the old ones were looked at");
        }

        store.Fault = null;
        Assert.Equal(["world-0000000000000000004.db", "world-0000000000000000005.db", "world-0000000000000000006.db", "world-before-the-move.db"], Copies());
        Assert.Equal("mine", File.ReadAllText(Path.Combine(backups, "notes.txt")));
        Assert.False(File.Exists(Path.Combine(backups, "staging.db")));
        Assert.Empty(Directory.GetFiles(backups, "*-wal"));
        Assert.Empty(Directory.GetFiles(backups, "*-shm"));
        foreach (var copy in Directory.GetFiles(backups, "world-0*.db"))
            Assert.True(new FileInfo(copy).Length <= SqliteWorldStore.MaxDatabaseBytes(SaveLimits.Default));

        // The final save and its copy, then the world is gone from under them: each copy is a world by itself.
        Assert.Equal(CommitOutcome.Committed, store.Commit(state, generation, final: true).Outcome);
        saved[++generation] = WorldSnapshotCodec.Encode(state);
        Assert.Equal((BackupOutcome.Completed, 7L), (store.Backup(BackupReason.Final).Outcome, store.Backup(BackupReason.Final).Generation));
        store.Dispose();
        foreach (var copy in Directory.GetFiles(backups, "world-0*.db"))
        {
            var from = SqliteWorldStore.BackupGeneration(copy);
            var restored = Folder($"restored-{from}");
            Directory.CreateDirectory(restored);
            File.Copy(copy, Db(restored));
            var (world, result) = Opened(restored);
            Assert.Equal(from, result.Generation);
            Assert.Equal(state.WorldId, result.Snapshot!.WorldId);
            Assert.Equal(saved[from], WorldSnapshotCodec.Encode(result.Snapshot));

            // Only the copy of the final save is of a world that was closed cleanly.
            Assert.Equal(from == 7, result.WasCleanClose);
            world.Dispose();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ARollbackThatDidNotGoThroughIsNeverReadAsACommit(bool final)
    {
        var folder = Folder();
        var initial = Populated();
        var store = Created(folder, initial);
        var state = Populated();
        state.NextWorldTick += 60;

        // SQLite itself refuses the rollback: the transaction stays open on the connection, with the new
        // generation, the new bytes and the clean mark all readable there, and none of it committed.
        SQLitePCL.strdelegate_authorizer deny = (_, action, what, _, _, _) =>
            action == SQLitePCL.raw.SQLITE_TRANSACTION && what == "ROLLBACK" ? SQLitePCL.raw.SQLITE_DENY : SQLitePCL.raw.SQLITE_OK;
        store.Fault = step =>
        {
            if (step != "commit")
                return;
            SQLitePCL.raw.sqlite3_set_authorizer(store.Connection.Handle, deny, new object());
            throw new IOException("the disk went away");
        };

        var lost = store.Commit(state, 1, final);
        GC.KeepAlive(deny);
        store.Fault = null;

        // Not acknowledged: what that connection showed was its own unfinished business.
        Assert.Equal((CommitOutcome.Failed, 1L, CheckpointOutcome.NotAttempted), (lost.Outcome, lost.Generation, lost.Checkpoint));
        Assert.Equal(1, store.Generation);

        // And rightly: the store goes on from the old generation, and a new process finds what was acknowledged.
        Assert.Equal((CommitOutcome.Committed, 2L), (store.Commit(initial, 1, final: false).Outcome, store.Generation));
        store.Dispose();
        var (reopened, result) = Opened(folder);
        Assert.Equal((2L, false), (result.Generation, result.WasCleanClose));
        Assert.Equal(WorldSnapshotCodec.Encode(initial), WorldSnapshotCodec.Encode(result.Snapshot!));
        reopened.Dispose();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ACommitThatWentThroughAndThenThrewIsMadeSureOfBeforeItIsAcknowledged(bool final)
    {
        var folder = Folder();
        var state = Populated();
        var store = Created(folder, state);
        state.NextWorldTick += 60;
        store.Fault = step => { if (step == "commit-returned") throw new IOException("the answer was lost"); };
        var kept = store.Commit(state, 1, final);
        store.Fault = null;

        // It is there, on a connection that never saw the transaction, and it was folded into the database
        // with full syncing before the store said so.
        Assert.Equal((CommitOutcome.Committed, 2L), (kept.Outcome, kept.Generation));
        Assert.Equal(final ? CheckpointOutcome.Completed : CheckpointOutcome.NotAttempted, kept.Checkpoint);
        Assert.Equal(2, store.Generation);
        Assert.Equal(0, new FileInfo(Db(folder) + "-wal").Length);
        Assert.Equal(CommitOutcome.Committed, store.Commit(state, 2, final).Outcome);
        store.Dispose();

        var (reopened, result) = Opened(folder);
        Assert.Equal((3L, final), (result.Generation, result.WasCleanClose));
        Assert.Equal(WorldSnapshotCodec.Encode(state), WorldSnapshotCodec.Encode(result.Snapshot!));
        reopened.Dispose();
    }

    [Fact]
    public void ACommitThatIsInTheLogButCannotBeMadeSureOfIsNotAcknowledged()
    {
        var folder = Folder();
        var state = Populated();
        var store = Created(folder, state);
        state.NextWorldTick += 60;
        store.Fault = step =>
        {
            if (step == "commit-returned") throw new IOException("the answer was lost");
            if (step == "checkpoint") throw new IOException("the log could not be synced");
        };
        var unknown = store.Commit(state, 1, final: true);
        store.Fault = null;

        // Readable is not durable. The store does not know, says so, and writes nothing more.
        Assert.Equal((CommitOutcome.Unknown, "the log could not be synced"), (unknown.Outcome, unknown.Error));
        Assert.Equal(1, store.Generation);
        Assert.Equal(CommitOutcome.Unknown, store.Commit(state, 1, final: true).Outcome);
        Assert.Equal(CommitOutcome.Unknown, store.Commit(state, 2, final: true).Outcome);
        Assert.Equal(BackupOutcome.Failed, store.Backup(BackupReason.Final).Outcome);
        store.Dispose();
    }

    [Fact]
    public void TheLogIsNeverLetGrowPastWhatAWorldIsOpenedWith()
    {
        var limits = new SaveLimits { MaxOwners = 4, MaxHulls = 256, MaxBlocks = 65536, MaxRocks = 1024 };
        var folder = Folder();
        var state = Sized(limits);
        var created = SqliteWorldStore.Create(folder, state, limits);
        Assert.True(created.Outcome == CreateOutcome.Created, created.Error);
        var store = (SqliteWorldStore)created.Store!;

        // No checkpoint succeeds, automatic or asked for: every commit stays in the log, and the log only grows.
        new SqliteCommand("PRAGMA wal_autocheckpoint = 0", store.Connection).ExecuteNonQuery();
        store.Fault = step => { if (step == "checkpoint") throw new IOException("the log could not be folded in"); };
        long generation = 1;
        CommitResult result;
        while (true)
        {
            state.NextWorldTick += 60;
            result = store.Commit(state, generation, final: false);
            if (result.Outcome != CommitOutcome.Committed)
                break;
            generation++;
            Assert.True(generation < 2000, "the log never ran out of room");
        }

        // Refused before it was begun, with nothing changed, at the point where one more save might not fit.
        Assert.Equal((CommitOutcome.Failed, generation), (result.Outcome, result.Generation));
        Assert.Contains("log", result.Error);
        Assert.Equal(generation, store.Generation);
        var log = new FileInfo(Db(folder) + "-wal").Length;
        Assert.InRange(log, SqliteWorldStore.MaxLogBytes(limits) - SqliteWorldStore.MaxDatabaseBytes(limits) + 1, SqliteWorldStore.MaxLogBytes(limits));
        Assert.Equal(CommitOutcome.Failed, store.Commit(state, generation, final: true).Outcome);
        Assert.Equal(log, new FileInfo(Db(folder) + "-wal").Length);

        // Killed there: every commit that was acknowledged is in files this build opens.
        var crashed = Folder("crashed");
        Directory.CreateDirectory(crashed);
        File.Copy(Db(folder), Db(crashed));
        File.Copy(Db(folder) + "-wal", Db(crashed) + "-wal");
        var opened = SqliteWorldStore.Open(crashed, Compatible, limits);
        Assert.True(opened.Outcome == OpenOutcome.Opened, opened.Error);
        Assert.Equal((generation, false, state.NextWorldTick - 60), (opened.Generation, opened.WasCleanClose, opened.Snapshot!.NextWorldTick));
        opened.Store!.Dispose();

        // Where it still runs: once the log can be folded in, the same save goes through.
        store.Fault = null;
        Assert.Equal((CommitOutcome.Committed, generation + 1), (store.Commit(state, generation, final: false).Outcome, store.Generation));
        Assert.True(new FileInfo(Db(folder) + "-wal").Length < log);
        store.Dispose();
    }

    [Fact]
    public void ANameDoesNotMakeAFileARecoveryCopyAndSuchFilesAreNeverCountedRemovedOrWrittenOver()
    {
        var folder = Folder();
        var backups = Path.Combine(folder, SqliteWorldStore.BackupFolder);
        var state = Populated();
        var store = Created(folder, state);
        long generation = 1;
        void Save()
        {
            state.NextWorldTick += 60;
            Assert.Equal(CommitOutcome.Committed, store.Commit(state, generation++, final: false).Outcome);
        }

        static string Name(long of) => $"world-{of:D19}.db";
        string[] Names() => [.. Directory.GetFiles(backups, "world-*.db").Select(Path.GetFileName).Order()!];

        Save();
        Assert.Equal(BackupOutcome.Completed, store.Backup(BackupReason.Initial).Outcome);

        // Corruption can preserve length and timestamp; neither is a substitute for revalidating a copy.
        var existing = Path.Combine(backups, Name(2));
        var original = File.ReadAllBytes(existing);
        var written = File.GetLastWriteTimeUtc(existing);
        var damaged = (byte[])original.Clone();
        damaged[0] = 0;
        File.WriteAllBytes(existing, damaged);
        File.SetLastWriteTimeUtc(existing, written);
        Assert.Equal(BackupOutcome.Failed, store.Backup(BackupReason.Interval).Outcome);
        Assert.Equal(damaged, File.ReadAllBytes(existing));
        File.WriteAllBytes(existing, original);
        File.SetLastWriteTimeUtc(existing, written);

        // An empty file where the copy of the newest generation belongs. It is not a copy, so there is no copy
        // to skip for; it is not this store's, so it is not written over. The store says so and leaves it.
        Save();
        File.WriteAllBytes(Path.Combine(backups, Name(3)), []);
        var collision = store.Backup(BackupReason.Interval);
        Assert.Equal((BackupOutcome.Failed, 2L), (collision.Outcome, collision.Generation));
        Assert.Contains("left as it is", collision.Error);
        Assert.Equal(0, new FileInfo(Path.Combine(backups, Name(3))).Length);
        File.Delete(Path.Combine(backups, Name(3)));
        Assert.Equal((BackupOutcome.Completed, 3L), (store.Backup(BackupReason.Interval).Outcome, store.Generation));

        // Another world's database under a copy's name, and two damaged files under the names of generations
        // newer than any real copy.
        var other = Populated();
        other.WorldId = Guid.NewGuid();
        Created(Folder("other"), other).Dispose();
        File.Copy(Db(Folder("other")), Path.Combine(backups, Name(1)));
        File.WriteAllBytes(Path.Combine(backups, Name(4)), RandomNumberGenerator.GetBytes(20000));
        File.WriteAllBytes(Path.Combine(backups, Name(5)), File.ReadAllBytes(Path.Combine(backups, Name(2))).AsSpan(0, 6000).ToArray());
        byte[][] Foreign() => [Hash(Path.Combine(backups, Name(1))), Hash(Path.Combine(backups, Name(4))), Hash(Path.Combine(backups, Name(5)))];
        var foreign = Foreign();
        var beside = Directory.GetFiles(backups).Order().ToArray();

        // Three more real copies: 6, 7, 8. By name there would be eight "copies" and the real 2 and 3 the
        // oldest. Counted by what they are there are five, and only the two oldest real ones go.
        Save();
        Save();
        Save();
        var newest = store.Backup(BackupReason.Interval);
        Assert.Equal((BackupOutcome.Completed, 6L), (newest.Outcome, newest.Generation));
        Assert.Equal([Name(1), Name(2), Name(3), Name(4), Name(5), Name(6)], Names());
        Save();
        Assert.Equal(BackupOutcome.Completed, store.Backup(BackupReason.Interval).Outcome);
        Assert.Equal([Name(1), Name(3), Name(4), Name(5), Name(6), Name(7)], Names());
        Save();
        Assert.Equal(BackupOutcome.Completed, store.Backup(BackupReason.Interval).Outcome);
        Assert.Equal([Name(1), Name(4), Name(5), Name(6), Name(7), Name(8)], Names());

        // Byte for byte, each of the three; and nothing was put beside them either: no log, no shared-memory
        // file, no journal that a database library might leave next to a file it only meant to read.
        var after = Foreign();
        for (var i = 0; i < foreign.Length; i++)
            Assert.Equal(foreign[i], after[i]);
        Assert.Equal([.. beside.Where(file => !file.EndsWith(Name(2)) && !file.EndsWith(Name(3))), .. new[] { Name(6), Name(7), Name(8) }.Select(name => Path.Combine(backups, name))], Directory.GetFiles(backups).Order().ToArray());
        store.Dispose();

        // The three that are kept are three worlds, each of its own generation.
        foreach (var kept in new long[] { 6, 7, 8 })
        {
            var restored = Folder($"restored-{kept}");
            Directory.CreateDirectory(restored);
            File.Copy(Path.Combine(backups, Name(kept)), Db(restored));
            var (world, result) = Opened(restored);
            Assert.Equal((kept, state.WorldId), (result.Generation, result.Snapshot!.WorldId));
            world.Dispose();
        }
    }

    [Fact]
    public void ACopyWhoseNameCouldNotBeMadeDurableIsMadeDurableBeforeItIsReported()
    {
        var folder = Folder();
        var backups = Path.Combine(folder, SqliteWorldStore.BackupFolder);
        var state = Populated();
        var store = Created(folder, state);
        Assert.Equal(CommitOutcome.Committed, store.Commit(state, 1, final: false).Outcome);
        var target = Path.Combine(backups, "world-0000000000000000002.db");
        var fail = true;
        var steps = new List<(string Step, string Path)>();
        try
        {
            // The rename goes through; the sync of the folder that holds the new name does not.
            DurableFiles.Trace = (step, path) =>
            {
                steps.Add((step, path));
                if (fail && step == "sync-directory" && path == backups && File.Exists(target))
                    throw new IOException("the folder could not be synced");
            };
            var failed = store.Backup(BackupReason.Initial);
            Assert.Equal((BackupOutcome.Failed, "the folder could not be synced"), (failed.Outcome, failed.Error));
            Assert.True(File.Exists(target));

            // The copy is there and whole, but nobody has yet been told so: asked again, the store syncs the
            // file and the folder first, and only then says there is nothing more to do.
            fail = false;
            steps.Clear();
            var settled = store.Backup(BackupReason.Initial);
            Assert.Equal((BackupOutcome.Skipped, 2L), (settled.Outcome, settled.Generation));
            Assert.Contains(("sync-file", target), steps);
            Assert.True(steps.IndexOf(("sync-directory", backups)) > steps.IndexOf(("sync-file", target)));
        }
        finally
        {
            DurableFiles.Trace = null;
        }

        store.Dispose();
    }

    [Fact]
    public void CopiesThatCouldNotBeTidiedUpAfterAndFilesThatAreNotCopiesCannotPassTheFoldersBound()
    {
        var limits = new SaveLimits { MaxOwners = 4, MaxHulls = 256, MaxBlocks = 65536, MaxRocks = 1024 };
        var folder = Folder();
        var backups = Path.Combine(folder, SqliteWorldStore.BackupFolder);
        var state = Sized(limits);
        var created = SqliteWorldStore.Create(folder, state, limits);
        Assert.True(created.Outcome == CreateOutcome.Created, created.Error);
        var store = (SqliteWorldStore)created.Store!;
        long Held() => Directory.GetFiles(backups).Sum(file => new FileInfo(file).Length);

        // Every copy is published and then its call fails, before the older copies are removed: eight times.
        store.Fault = step => { if (step == "backup-published") throw new IOException("interrupted after publishing"); };
        for (long generation = 1; generation <= 8; generation++)
        {
            state.NextWorldTick += 60;
            Assert.Equal(CommitOutcome.Committed, store.Commit(state, generation, final: false).Outcome);
            var failed = store.Backup(BackupReason.Interval);

            // The newest state has its copy all the same, and what piled up before was removed first.
            Assert.Equal((BackupOutcome.Failed, generation + 1), (failed.Outcome, failed.Generation));
            Assert.InRange(Directory.GetFiles(backups, "world-0*.db").Length, 1, SqliteWorldStore.KeptBackups + 1);
            Assert.True(Held() <= SqliteWorldStore.MaxBackupBytes(limits), $"{Held()} bytes of copies");
        }

        store.Fault = null;
        Assert.Equal(BackupOutcome.Skipped, store.Backup(BackupReason.Interval).Outcome);
        Assert.Equal(SqliteWorldStore.KeptBackups, Directory.GetFiles(backups, "world-0*.db").Length);

        // A file under a copy's name that fills the folder's allowance. It is not removed to make room, and no
        // copy is begun that would pass the allowance: not a byte of staging is written.
        var filler = Path.Combine(backups, "world-0000000000000000001.db");
        using (var stream = new FileStream(filler, FileMode.CreateNew))
            stream.SetLength(SqliteWorldStore.MaxBackupBytes(limits));
        state.NextWorldTick += 60;
        Assert.Equal(CommitOutcome.Committed, store.Commit(state, 9, final: false).Outcome);
        var names = Directory.GetFiles(backups).Order().ToArray();
        var refused = store.Backup(BackupReason.Interval);
        Assert.Equal((BackupOutcome.Failed, 9L), (refused.Outcome, refused.Generation));
        Assert.Contains("Nothing was removed", refused.Error);
        Assert.Equal(names, Directory.GetFiles(backups).Order().ToArray());
        Assert.Equal(SqliteWorldStore.MaxBackupBytes(limits), new FileInfo(filler).Length);

        // Taken away by whoever put it there, copies are made again.
        File.Delete(filler);
        Assert.Equal((BackupOutcome.Completed, 10L), (store.Backup(BackupReason.Interval).Outcome, store.Backup(BackupReason.Interval).Generation));
        store.Dispose();
    }

    [Fact]
    public void FoldersAreMadeDurableBeforeAWorldOrACopyInThemIsAcknowledged()
    {
        var saves = Path.Combine(_root, "saves");
        var parent = Path.Combine(saves, "mine");
        var folder = Path.Combine(parent, "world");
        var steps = new List<(string Step, string Path)>();
        int At(string step, string path) => steps.IndexOf((step, path));
        try
        {
            DurableFiles.Trace = (step, path) => steps.Add((step, path));

            // Three folders deep, none of them there: each is made, then the folder it was made in is synced,
            // before the next; and all of it before the database gets its name.
            Created(folder).Dispose();
            Assert.True(At("create-directory", saves) >= 0);
            Assert.True(At("create-directory", saves) < At("sync-directory", _root));
            Assert.True(At("sync-directory", _root) < At("create-directory", parent));
            Assert.True(At("create-directory", parent) < At("sync-directory", saves));
            Assert.True(At("sync-directory", saves) < At("create-directory", folder));
            Assert.True(At("create-directory", folder) < At("sync-directory", parent));
            Assert.True(At("sync-directory", parent) < At("sync-file", Db(folder) + ".creating"));
            Assert.True(At("sync-file", Db(folder) + ".creating") < At("sync-directory", folder));

            // A second world beside it: only its own folder is new, and only that is made. The folders above,
            // which this user could have made in an earlier try, get their names synced again all the same.
            steps.Clear();
            var beside = Path.Combine(parent, "beside");
            Created(beside).Dispose();
            Assert.Equal(-1, At("create-directory", saves));
            Assert.Equal(-1, At("create-directory", parent));
            Assert.True(At("sync-directory", _root) >= 0);
            Assert.True(At("sync-directory", _root) < At("sync-directory", saves));
            Assert.True(At("sync-directory", saves) < At("create-directory", beside));
            Assert.True(At("create-directory", beside) < At("sync-directory", parent));
        }
        finally
        {
            DurableFiles.Trace = null;
        }
    }

    [Fact]
    public void AFolderWhoseNameCouldNotBeMadeDurableGetsItMadeDurableWhenTheSameThingIsTriedAgain()
    {
        var deep = Path.Combine(_root, "deep");
        var deeper = Path.Combine(deep, "er");
        var folder = Path.Combine(deeper, "world");
        var backups = Path.Combine(folder, SqliteWorldStore.BackupFolder);
        var steps = new List<(string Step, string Path)>();
        string? failAt = null;
        int At(string step, string path) => steps.IndexOf((step, path));
        try
        {
            DurableFiles.Trace = (step, path) =>
            {
                steps.Add((step, path));
                if (step == "sync-directory" && path == failAt)
                    throw new IOException("the folder could not be synced");
            };

            // Two of three folders are made, and the name of the second cannot be synced. No world is created.
            failAt = deep;
            var failed = SqliteWorldStore.Create(folder, Populated());
            Assert.Equal((CreateOutcome.Failed, "the folder could not be synced"), (failed.Outcome, failed.Error));
            Assert.True(Directory.Exists(deeper));
            Assert.False(Directory.Exists(folder));

            // Tried again with everything left as it was: the two folders are there, so they are not made, and
            // their names are synced now, in order, before the world's own folder is made under them.
            failAt = null;
            steps.Clear();
            var store = Created(folder);
            Assert.Equal(-1, At("create-directory", deep));
            Assert.Equal(-1, At("create-directory", deeper));
            Assert.True(At("sync-directory", _root) >= 0);
            Assert.True(At("sync-directory", _root) < At("sync-directory", deep));
            Assert.True(At("sync-directory", deep) < At("create-directory", folder));
            Assert.True(At("create-directory", folder) < At("sync-directory", deeper));
            Assert.True(At("sync-directory", deeper) < At("sync-directory", folder));

            // The same with the world's own folder left behind by a create that failed at its name.
            var second = Path.Combine(deeper, "second");
            failAt = deeper;
            Assert.Equal(CreateOutcome.Failed, SqliteWorldStore.Create(second, Populated()).Outcome);
            Assert.True(Directory.Exists(second));
            Assert.False(File.Exists(Db(second)));
            failAt = null;
            steps.Clear();
            Created(second).Dispose();
            Assert.Equal(-1, At("create-directory", second));
            Assert.True(At("sync-directory", deeper) >= 0);
            Assert.True(At("sync-directory", deeper) < At("sync-file", Db(second) + ".creating"));

            // And the copies' folder: made, its name not synced, no copy. Asked again without touching it, the
            // name is synced first, and only then is a copy put into the folder and published.
            Assert.Equal(CommitOutcome.Committed, store.Commit(Populated(), 1, final: false).Outcome);
            failAt = folder;
            steps.Clear();
            var refused = store.Backup(BackupReason.Initial);
            Assert.Equal((BackupOutcome.Failed, "the folder could not be synced"), (refused.Outcome, refused.Error));
            Assert.True(Directory.Exists(backups));
            Assert.Empty(Directory.GetFiles(backups));

            // Still failing: still no copy, however often it is asked.
            Assert.Equal(BackupOutcome.Failed, store.Backup(BackupReason.Initial).Outcome);
            Assert.Empty(Directory.GetFiles(backups));

            failAt = null;
            steps.Clear();
            Assert.Equal(BackupOutcome.Completed, store.Backup(BackupReason.Initial).Outcome);
            Assert.Equal(-1, At("create-directory", backups));
            Assert.True(At("sync-directory", folder) >= 0);
            Assert.True(At("sync-directory", folder) < At("sync-file", Path.Combine(backups, "staging.db")));
            Assert.True(At("sync-file", Path.Combine(backups, "staging.db")) < At("sync-directory", backups));
            store.Dispose();
        }
        finally
        {
            DurableFiles.Trace = null;
        }
    }

    [Fact]
    public void ACopyIsGivenRoomForTheWholeDatabaseAndNotForWhatTheMainFileHappensToHold()
    {
        var limits = new SaveLimits { MaxOwners = 4, MaxHulls = 256, MaxBlocks = 65536, MaxRocks = 1024 };
        var folder = Folder();
        var backups = Path.Combine(folder, SqliteWorldStore.BackupFolder);
        var created = SqliteWorldStore.Create(folder, Populated(), limits);
        Assert.True(created.Outcome == CreateOutcome.Created, created.Error);
        var store = (SqliteWorldStore)created.Store!;

        // A small world, then a large save that stays in the log: the main file is as small as it was, and the
        // database a copy would be made of is many times that.
        new SqliteCommand("PRAGMA wal_autocheckpoint = 0", store.Connection).ExecuteNonQuery();
        Assert.Equal(CommitOutcome.Committed, store.Commit(Sized(limits), 1, final: false).Outcome);
        var mainFile = new FileInfo(Db(folder)).Length;
        var database = (long)new SqliteCommand("PRAGMA page_count", store.Connection).ExecuteScalar()! * (long)new SqliteCommand("PRAGMA page_size", store.Connection).ExecuteScalar()!;
        Assert.True(database > 4 * mainFile, $"the database is {database} bytes and its main file {mainFile}");

        // A file under a copy's name that leaves exactly the main file's size of the folder's allowance, and a
        // failure waiting just after the copy is made, where its size used to be looked at for the first time.
        Directory.CreateDirectory(backups);
        var filler = Path.Combine(backups, "world-0000000000000000001.db");
        using (var stream = new FileStream(filler, FileMode.CreateNew))
            stream.SetLength(SqliteWorldStore.MaxBackupBytes(limits) - mainFile);
        var copied = false;
        store.Fault = step =>
        {
            if (step != "backup-copied")
                return;
            copied = true;
            throw new IOException("interrupted after copying");
        };

        // Refused before a byte of it is written: nothing is copied, nothing is left behind, nothing removed.
        var refused = store.Backup(BackupReason.Initial);
        Assert.Equal(BackupOutcome.Failed, refused.Outcome);
        Assert.Contains("Nothing was removed", refused.Error);
        Assert.False(copied);
        Assert.Equal([filler], Directory.GetFiles(backups));
        Assert.True(Directory.GetFiles(backups).Sum(file => new FileInfo(file).Length) <= SqliteWorldStore.MaxBackupBytes(limits));

        // Room for a whole database is room enough, however little of it is in the main file.
        using (var stream = new FileStream(filler, FileMode.Open))
            stream.SetLength(SqliteWorldStore.MaxBackupBytes(limits) - SqliteWorldStore.MaxDatabaseBytes(limits));
        var interrupted = store.Backup(BackupReason.Initial);
        Assert.Equal((BackupOutcome.Failed, "interrupted after copying"), (interrupted.Outcome, interrupted.Error));
        Assert.True(copied);

        // What that left under the working name is within what was set aside for it, and goes with the next try.
        Assert.True(Directory.GetFiles(backups).Sum(file => new FileInfo(file).Length) <= SqliteWorldStore.MaxBackupBytes(limits));
        store.Fault = null;
        var made = store.Backup(BackupReason.Initial);
        Assert.Equal((BackupOutcome.Completed, 2L), (made.Outcome, made.Generation));
        Assert.False(File.Exists(Path.Combine(backups, "staging.db")));
        Assert.Equal(database, new FileInfo(Path.Combine(backups, "world-0000000000000000002.db")).Length);
        Assert.True(Directory.GetFiles(backups).Sum(file => new FileInfo(file).Length) <= SqliteWorldStore.MaxBackupBytes(limits));
        store.Dispose();
    }

    [Fact]
    public void OlderCopiesAreKeptUntilTheNewerOnesThatReplaceThemAreKnownToBeDurable()
    {
        var folder = Folder();
        var backups = Path.Combine(folder, SqliteWorldStore.BackupFolder);
        var state = Populated();
        var store = Created(folder, state);
        long generation = 1;
        static string Name(long of) => $"world-{of:D19}.db";
        string[] Names() => [.. Directory.GetFiles(backups, "world-0*.db").Select(Path.GetFileName).Order()!];
        void Save()
        {
            state.NextWorldTick += 60;
            Assert.Equal(CommitOutcome.Committed, store.Commit(state, generation++, final: false).Outcome);
        }

        // Three copies, each published for good: generations 2, 3 and 4.
        for (var i = 0; i < 3; i++)
        {
            Save();
            Assert.Equal(BackupOutcome.Completed, store.Backup(BackupReason.Interval).Outcome);
        }

        Assert.Equal([Name(2), Name(3), Name(4)], Names());
        var oldest = Hash(Path.Combine(backups, Name(2)));
        var fifth = Path.Combine(backups, Name(5));
        var fail = true;
        var syncs = new List<(string Step, string Path, bool OldestThere)>();
        try
        {
            // The fourth copy gets its name, and the sync of the folder that holds the name fails.
            DurableFiles.Trace = (step, path) =>
            {
                syncs.Add((step, path, File.Exists(Path.Combine(backups, Name(2)))));
                if (fail && step == "sync-directory" && path == backups && File.Exists(fifth))
                    throw new IOException("the folder could not be synced");
            };
            Save();
            var failed = store.Backup(BackupReason.Interval);
            Assert.Equal((BackupOutcome.Failed, "the folder could not be synced"), (failed.Outcome, failed.Error));
            Assert.Equal([Name(2), Name(3), Name(4), Name(5)], Names());

            // Asked again, and again, while the folder still cannot be synced. There are four files and three
            // are kept, but the newest of them is not known to be there for good: nothing older is given up.
            for (var i = 0; i < 3; i++)
            {
                var again = store.Backup(BackupReason.Interval);
                Assert.Equal((BackupOutcome.Failed, "the folder could not be synced"), (again.Outcome, again.Error));
                Assert.Equal([Name(2), Name(3), Name(4), Name(5)], Names());
                Assert.Equal(oldest, Hash(Path.Combine(backups, Name(2))));
            }

            // The folder syncs again. The three that stay are synced, then the folder, and only then does the
            // oldest go: at the first sync of the folder it was still there.
            fail = false;
            syncs.Clear();
            var settled = store.Backup(BackupReason.Interval);
            Assert.Equal((BackupOutcome.Skipped, 5L), (settled.Outcome, settled.Generation));
            Assert.Equal([Name(3), Name(4), Name(5)], Names());
            var barrier = syncs.FindIndex(sync => sync.Step == "sync-directory" && sync.Path == backups);
            Assert.True(barrier >= 0 && syncs[barrier].OldestThere, "the oldest copy went before the folder was synced");
            foreach (var kept in new[] { Name(3), Name(4), Name(5) })
            {
                var synced = syncs.FindIndex(sync => sync.Step == "sync-file" && sync.Path == Path.Combine(backups, kept));
                Assert.True(synced >= 0 && synced < barrier, $"{kept} was not synced before the folder was");
            }
        }
        finally
        {
            DurableFiles.Trace = null;
        }

        store.Dispose();
    }

    [Fact]
    public void AnotherPathToTheSameFolderIsTheSameWorld()
    {
        // Symbolic links need no privilege on the systems this is run on; where they do, there is nothing to try.
        if (OperatingSystem.IsWindows())
            return;
        var folder = Folder();
        var alias = Folder("alias");
        var store = Created(folder);
        Directory.CreateSymbolicLink(alias, folder);

        // The table of this process's folders knows the two paths as two; the lock file is one file.
        Assert.Equal(OpenOutcome.Busy, SqliteWorldStore.Open(alias, Compatible).Outcome);
        Assert.Equal(OpenOutcome.Busy, SqliteWorldStore.Summarize(alias).Outcome);
        Assert.Equal(CreateOutcome.Exists, SqliteWorldStore.Create(alias, Populated()).Outcome);
        Assert.Equal("Busy|0", Probe(alias));
        store.Dispose();

        var through = SqliteWorldStore.Open(alias, Compatible);
        Assert.True(through.Outcome == OpenOutcome.Opened, through.Error);
        Assert.Equal(OpenOutcome.Busy, SqliteWorldStore.Open(folder, Compatible).Outcome);
        through.Store!.Dispose();
    }

    [Fact]
    public void AServerSavesClosesAndResumesAWorldOnDisk()
    {
        var folder = Folder();
        var initial = WorldSnapshot.NewWorld(Shipped, Guid.NewGuid(), "On Disk", DateTimeOffset.UtcNow, 42, Shipped.SettingCatalog.Defaults, default);
        var created = SqliteWorldStore.Create(folder, initial);
        Assert.Equal(CreateOutcome.Created, created.Outcome);
        ulong pod;
        long tick;

        static void Tick(ServerHost server, ManualTime clock, ref long callback, int count)
        {
            for (var i = 0; i < count; i++)
            {
                clock.Advance(ManualTime.Tick);
                ((ITickHandler)server).Tick(callback++);
            }
        }

        static HullStore Hulls(ServerHost server) =>
            (HullStore)typeof(ServerHost).GetField("_hulls", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(server)!;

        // The first run: straight from the create, played, saved by the clock and at the close.
        {
            var clock = new ManualTime();
            var link = new OwnerLink(Shipped);
            using var server = new ServerHost(new ServerConfig { Clock = clock, World = new WorldPersistence(created.Store!, initial, created.Generation) }, Shipped, link);

            // A list of worlds sees a world that is running as in use, and does not read it.
            Assert.Equal(OpenOutcome.Busy, SqliteWorldStore.Summarize(folder).Outcome);
            long callback = 0;
            link.Join();
            Tick(server, clock, ref callback, 30);
            Assert.Equal(3, Hulls(server).Count);
            pod = Hulls(server).Info(0).Entity;

            clock.AdvanceSeconds(31);
            Tick(server, clock, ref callback, 1);
            TestNet.WaitUntil(() => server.Save.State == SaveState.Idle && !server.Save.Dirty, "the autosave to reach the disk");
            Assert.Equal(BackupOutcome.Completed, server.Save.Backup);
            Tick(server, clock, ref callback, 20);
            tick = server.Status.Tick;

            server.BeginClose();
            for (var i = 0; i < 20000 && !server.Close.Completed; i++)
            {
                Tick(server, clock, ref callback, 1);
                Thread.Sleep(1);
            }

            var close = server.Close;
            Assert.Equal((FinalCommitOutcome.Committed, CheckpointOutcome.Completed, BackupOutcome.Completed), (close.FinalCommit, close.Checkpoint, close.Backup));
            Assert.Null(close.Error);
        }

        // Closed: the folder is a database, a lease file and copies; the list of worlds can read it.
        Assert.False(File.Exists(Db(folder) + "-wal"));
        var summary = SqliteWorldStore.Summarize(folder);
        Assert.Equal((OpenOutcome.Opened, "On Disk", tick, true), (summary.Outcome, summary.Name, summary.NextWorldTick, summary.WasCleanClose));
        Assert.Equal(2, Directory.GetFiles(Path.Combine(folder, SqliteWorldStore.BackupFolder), "world-0*.db").Length);

        // The second run: the same world, the same fleet, the same owner in the same pod, and no second fleet.
        {
            var (store, opened) = Opened(folder);
            Assert.True(opened.WasCleanClose);
            var clock = new ManualTime();
            var link = new OwnerLink(Shipped);
            using var server = new ServerHost(new ServerConfig { Clock = clock, World = new WorldPersistence(store, opened.Snapshot!, opened.Generation) }, Shipped, link);
            Assert.Equal(tick, server.Status.Tick);
            Assert.Equal(42ul, server.Galaxy.Seed);
            long callback = 0;
            link.Join();
            Tick(server, clock, ref callback, 5);
            Assert.Equal(3, Hulls(server).Count);
            Assert.Equal(pod, Hulls(server).Info((int)(link.Welcome.HullId & 0xFFFF)).Entity);
            Assert.Equal(tick, link.Welcome.ServerTick);
        }

        // That run was only disposed: the world is whole, at its last save, and says it was not closed cleanly.
        var after = SqliteWorldStore.Summarize(folder);
        Assert.Equal((OpenOutcome.Opened, tick, false), (after.Outcome, after.NextWorldTick, after.WasCleanClose));
    }

    /// <summary>A snapshot the codec accepts with every count at its limit: the largest save there is under those limits.</summary>
    private static WorldSnapshot Sized(SaveLimits limits)
    {
        var small = Populated();
        var snapshot = new WorldSnapshot(limits, checked((int)limits.MaxCharts))
        {
            Compatibility = small.Compatibility,
            WorldId = small.WorldId,
            Name = small.Name,
            CreatedUnixMs = small.CreatedUnixMs,
            Seed = small.Seed,
            SpawnSector = small.SpawnSector,
            SettingIds = small.SettingIds,
            SettingCount = small.SettingCount,
            NextWorldTick = small.NextWorldTick,
            NextOwnerId = 1000,
            NextEntityId = 10_000_000,
            Designs = small.Designs,
            DesignHashes = small.DesignHashes,
            DesignCount = small.DesignCount,
            PodDesign = small.PodDesign,
            DroneDesign = small.DroneDesign,
            StartingFleet = small.StartingFleet,
        };
        small.SettingValues.CopyTo(snapshot.SettingValues, 0);
        for (var i = 0; i < limits.MaxOwners; i++)
            snapshot.Owners[snapshot.OwnerCount++] = new SavedOwner
            {
                Id = (uint)i + 1,
                Kind = i == 0 ? OwnerKind.Local : OwnerKind.Account,
                Account = new SavedAccount("https://accounts.example/", new Guid(i + 1, 0, 0, new byte[8])),
            };
        for (var i = 0; i < snapshot.Charts.Length; i++)
            snapshot.Charts[snapshot.ChartCount++] = new SectorChart((uint)(i % limits.MaxOwners + 1), new SectorCoord(i / limits.MaxOwners, 0, 0), 0);
        for (var i = 0; i < snapshot.ModuleObservations.Length; i++)
            snapshot.ModuleObservations[snapshot.ModuleObservationCount++] = new ModuleObservation
            {
                Owner = (uint)(i % limits.MaxOwners + 1),
                Family = ModuleFamily.AsteroidLocator,
                Sector = new SectorCoord(i / limits.MaxOwners, 0, 0),
                Index = 0,
                Resource = -1,
                RecordedTick = 0
            };
        var perHull = limits.MaxBlocks / limits.MaxHulls;
        var template = small.Hulls[3];
        for (var i = 0; i < limits.MaxHulls; i++)
        {
            var hull = template;
            hull.Entity = (ulong)i + 1;
            hull.Blocks = perHull;
            hull.LossCount = perHull;
            hull.BlockStart = snapshot.BlockCount;
            hull.LossStart = snapshot.LossCount;
            hull.Visual.Count = VisualDamageState.Capacity;
            snapshot.BlockCount += perHull;
            snapshot.LossCount += perHull;
            snapshot.Hulls[snapshot.HullCount++] = hull;
        }

        for (var i = 0; i < limits.MaxRocks; i++)
            snapshot.Rocks[snapshot.RockCount++] = new RockDelta { Rock = new RockRef(new SectorCoord(i % 256, 0, 0), i), Velocity = Vector3.UnitX, StartTick = 1 };
        return snapshot;
    }

    [Fact]
    public void TheLargestSaveIsWrittenCopiedAndReadWithinItsBoundsAndWithoutASecondCopyInMemory()
    {
        var folder = Folder();
        var limits = SaveLimits.Default;
        var snapshot = Sized(limits);
        var size = WorldSnapshotCodec.EncodedSize(snapshot);
        Assert.InRange(size, limits.MaxEncodedBytes * 9 / 10, limits.MaxEncodedBytes);

        var watch = Stopwatch.StartNew();
        var store = Created(folder, snapshot);
        var create = watch.Elapsed;

        // Warm: the store has its buffer. From here a commit takes no memory the size of a save.
        Assert.Equal(CommitOutcome.Committed, store.Commit(snapshot, 1, final: false).Outcome);
        var commits = new double[5];
        var allocated = 0L;
        for (var i = 0; i < commits.Length; i++)
        {
            snapshot.NextWorldTick += 60;
            var before = GC.GetAllocatedBytesForCurrentThread();
            watch.Restart();
            Assert.Equal(CommitOutcome.Committed, store.Commit(snapshot, i + 2, final: false).Outcome);
            commits[i] = watch.Elapsed.TotalMilliseconds;
            allocated = Math.Max(allocated, GC.GetAllocatedBytesForCurrentThread() - before);
        }

        Assert.True(allocated < 256 * 1024, $"a commit allocated {allocated} bytes");
        var log = new FileInfo(Db(folder) + "-wal").Length;

        watch.Restart();
        var backup = store.Backup(BackupReason.Initial);
        var copy = watch.Elapsed;
        Assert.True(backup.Outcome == BackupOutcome.Completed, backup.Error);

        watch.Restart();
        var final = store.Commit(snapshot, 7, final: true);
        var close = watch.Elapsed;
        Assert.Equal((CommitOutcome.Committed, CheckpointOutcome.Completed), (final.Outcome, final.Checkpoint));
        store.Dispose();

        var database = new FileInfo(Db(folder)).Length;
        var copied = Directory.GetFiles(Path.Combine(folder, SqliteWorldStore.BackupFolder), "world-0*.db").Sum(file => new FileInfo(file).Length);
        Assert.True(database <= SqliteWorldStore.MaxDatabaseBytes(limits), $"the database is {database} bytes");
        Assert.True(copied <= SqliteWorldStore.MaxDatabaseBytes(limits));
        Assert.True(log <= 4 * SqliteWorldStore.MaxDatabaseBytes(limits), $"the log reached {log} bytes");

        watch.Restart();
        var (reopened, opened) = Opened(folder);
        var open = watch.Elapsed;
        Assert.Equal(8, opened.Generation);
        Assert.Equal(size, WorldSnapshotCodec.EncodedSize(opened.Snapshot!));
        reopened.Dispose();

        Array.Sort(commits);
        Console.WriteLine(
            $"Largest save on disk ({snapshot.HullCount} hulls, {snapshot.BlockCount} blocks, {snapshot.RockCount} asteroids, {size} bytes encoded): "
                + $"create {create.TotalMilliseconds:F1} ms, commit median {commits[2]:F1} ms and worst {commits[^1]:F1} ms with at most {allocated} managed bytes each, "
                + $"recovery copy {copy.TotalMilliseconds:F1} ms, final commit with checkpoint {close.TotalMilliseconds:F1} ms, open and decode {open.TotalMilliseconds:F1} ms. "
                + $"Database {database} bytes, log up to {log} bytes, one copy {copied} bytes; bounds {SqliteWorldStore.MaxDatabaseBytes(limits)} per file, {SqliteWorldStore.MaxBackupBytes(limits)} for all copies."
        );
    }
}
