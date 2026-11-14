using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Prospect.Client;
using Prospect.Game.Persistence;
using Prospect.Game.World;
using Xunit;

namespace Prospect.Tests;

/// <summary>
/// Deleting saved worlds from the library, on a real disk: what goes, what is refused and left exactly as it was,
/// and what a delete that stopped half way leaves behind. A crash is simulated as the deletion's own hook
/// throwing; nothing here cuts the power, and nothing is claimed about macOS or Windows.
/// </summary>
public sealed class WorldDeletionTests : IDisposable
{
    private static readonly GameContent Content = GameContent.Load();
    private static readonly SaveCompatibility Compatible = SaveCompatibility.Of(Content);
    private readonly string _base = Path.Combine(AppContext.BaseDirectory, "save-tests", Guid.NewGuid().ToString("N"));
    private readonly string _root;
    private readonly WorldLibrary _library;

    public WorldDeletionTests()
    {
        _root = Path.Combine(_base, "worlds");
        Directory.CreateDirectory(_root);
        _library = new WorldLibrary(_root);
    }

    public void Dispose()
    {
        WorldDeletion.Trace = null;
        try
        {
            Directory.Delete(_base, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A test's leftovers are not worth failing it for.
        }
    }

    private string FolderOf(string id) => Path.Combine(_root, id);

    private static string Db(string folder) => Path.Combine(folder, SqliteWorldStore.DatabaseFile);

    /// <summary>A world with its key and one recovery copy, closed again. Returns its folder id.</summary>
    private string Closed(string name)
    {
        var store = Open(name, out var id);
        store.Dispose();
        return id;
    }

    /// <summary>A world with its key and one recovery copy, left running (leased) until the store is disposed.</summary>
    private IWorldStore Open(string name, out string id)
    {
        var initial = WorldSnapshot.NewWorld(Content, Guid.NewGuid(), name, DateTimeOffset.UtcNow, 42, Content.SettingCatalog.Defaults, default);
        var created = _library.Create(initial, out id, TestContext.Current.CancellationToken);
        Assert.Equal(CreateOutcome.Created, created.Outcome);
        Assert.Equal(BackupOutcome.Completed, created.Store!.Backup(BackupReason.Initial).Outcome);
        return created.Store;
    }

    private static void Tamper(string folder, string sql)
    {
        using var connection = new SqliteConnection($"Data Source={Db(folder)};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static DeleteResult Only(DeleteReport report) => Assert.Single(report.Results);

    [Fact]
    public void ADeleteTakesTheWorldItsKeyAndItsRecoveryCopiesAndNothingElse()
    {
        var alpha = Closed("Alpha");
        var beta = Closed("Beta");
        Assert.True(File.Exists(Path.Combine(FolderOf(alpha), WorldIdentity.KeyFile)));
        Assert.NotEmpty(Directory.GetFiles(Path.Combine(FolderOf(alpha), SqliteWorldStore.BackupFolder)));
        var betaDatabase = SHA256.HashData(File.ReadAllBytes(Db(FolderOf(beta))));

        var result = Only(_library.Delete([alpha]));

        Assert.Equal(DeleteOutcome.Deleted, result.Outcome);
        Assert.Equal("", result.Message);
        Assert.False(Directory.Exists(FolderOf(alpha)));
        var listed = _library.Scan(Compatible).Worlds;
        Assert.Equal(beta, Assert.Single(listed).FolderId);
        Assert.Equal(betaDatabase, SHA256.HashData(File.ReadAllBytes(Db(FolderOf(beta)))));
        Assert.True(File.Exists(Path.Combine(FolderOf(beta), WorldIdentity.KeyFile)));
    }

    [Fact]
    public void AWorldThatIsOpenIsRefusedAndKeepsEveryFile()
    {
        using var running = Open("Running", out var id);
        var before = Directory.GetFiles(_base, "*", SearchOption.AllDirectories).Order().ToArray();

        var refused = Only(_library.Delete([id]));

        Assert.Equal(DeleteOutcome.Busy, refused.Outcome);
        Assert.Contains("open", refused.Message);
        Assert.Equal(before, Directory.GetFiles(_base, "*", SearchOption.AllDirectories).Order().ToArray());

        // Refused as incompatible-only too: it is not read at all.
        Assert.Equal(DeleteOutcome.Busy, Only(_library.Delete([id], Compatible with { GeneratorVersion = Compatible.GeneratorVersion + 1 })).Outcome);

        running.Dispose();
        Assert.Equal(DeleteOutcome.Deleted, Only(_library.Delete([id])).Outcome);
    }

    [Fact]
    public void OnlyWorldsThisBuildCannotOpenGoWhenTheDeleteIsForThose()
    {
        var current = Closed("Current");
        var other = Closed("Other generator");
        var layout = Closed("Newer layout");
        var damaged = Closed("Damaged");
        Tamper(FolderOf(other), "UPDATE world SET generator_version = generator_version + 1");
        Tamper(FolderOf(layout), "PRAGMA user_version = 999");
        File.WriteAllBytes(Db(FolderOf(damaged)), RandomNumberGenerator.GetBytes(8192));
        var listed = _library.Scan(Compatible).Worlds.ToDictionary(w => w.FolderId, w => w.Availability);
        Assert.Equal(WorldAvailability.Ready, listed[current]);
        Assert.Equal(WorldAvailability.Incompatible, listed[other]);
        Assert.Equal(WorldAvailability.Incompatible, listed[layout]);
        Assert.Equal(WorldAvailability.Damaged, listed[damaged]);

        var report = _library.Delete([current, other, layout, damaged], Compatible);

        Assert.Equal(
            [DeleteOutcome.Kept, DeleteOutcome.Deleted, DeleteOutcome.Deleted, DeleteOutcome.Kept],
            report.Results.Select(r => r.Outcome).ToArray()
        );
        Assert.Equal(2, report.Deleted);
        Assert.True(File.Exists(Db(FolderOf(current))));
        Assert.True(File.Exists(Db(FolderOf(damaged))));
        Assert.False(Directory.Exists(FolderOf(other)));
        Assert.False(Directory.Exists(FolderOf(layout)));

        // A damaged world is still deleted when it is asked for by name.
        Assert.Equal(DeleteOutcome.Deleted, Only(_library.Delete([damaged])).Outcome);
        Assert.False(Directory.Exists(FolderOf(damaged)));
    }

    [Fact]
    public void ANameThatIsNotOneWorldFolderIsRefusedWithoutTouchingAnything()
    {
        var outside = Path.Combine(_base, "outside");
        Directory.CreateDirectory(outside);
        var marker = Path.Combine(outside, "keep.txt");
        File.WriteAllText(marker, "keep");
        var alpha = Closed("Alpha");

        foreach (var bad in new[] { "..", ".", "../outside", "a/b", "", "outside\\..\\..\\x" })
            Assert.Equal(DeleteOutcome.Unsafe, Only(_library.Delete([bad])).Outcome);
        Assert.Equal(DeleteOutcome.Missing, Only(_library.Delete(["not-there"])).Outcome);

        // A folder in the library that is not a world is not one to delete.
        Directory.CreateDirectory(FolderOf("holiday photos"));
        File.WriteAllText(Path.Combine(FolderOf("holiday photos"), "a.jpg"), "photo");
        Assert.Equal(DeleteOutcome.Missing, Only(_library.Delete(["holiday photos"])).Outcome);
        Assert.True(File.Exists(Path.Combine(FolderOf("holiday photos"), "a.jpg")));
        Assert.Single(Directory.GetFiles(FolderOf("holiday photos")));
        Assert.True(File.Exists(marker));
        Assert.True(File.Exists(Db(FolderOf(alpha))));
        Assert.Throws<ArgumentException>(() => _library.Delete(Enumerable.Range(0, WorldLibrary.MaxWorlds + 1).Select(i => i.ToString()).ToArray()));
    }

    [Fact]
    public void ALinkIsNeverFollowedOutOfTheLibrary()
    {
        if (OperatingSystem.IsWindows())
            return;

        // A world folder that is a link to a real world elsewhere.
        var elsewhere = Path.Combine(_base, "elsewhere");
        var created = SqliteWorldStore.Create(elsewhere, WorldSnapshot.NewWorld(Content, Guid.NewGuid(), "Elsewhere", DateTimeOffset.UtcNow, 42, Content.SettingCatalog.Defaults, default));
        Assert.Equal(CreateOutcome.Created, created.Outcome);
        created.Store!.Dispose();
        var linked = FolderOf("linked");
        Directory.CreateSymbolicLink(linked, elsewhere);
        var before = Directory.GetFiles(elsewhere, "*", SearchOption.AllDirectories).Order().ToArray();

        Assert.Equal(DeleteOutcome.Unsafe, Only(_library.Delete(["linked"])).Outcome);
        Assert.Equal(before, Directory.GetFiles(elsewhere, "*", SearchOption.AllDirectories).Order().ToArray());
        Assert.True(File.Exists(Db(elsewhere)));

        // A world whose key, or whose recovery folder, is a link to something outside: refused whole, link and target kept.
        var alpha = Closed("Alpha");
        var secret = Path.Combine(_base, "secret.txt");
        File.WriteAllText(secret, "secret");
        var key = Path.Combine(FolderOf(alpha), WorldIdentity.KeyFile);
        File.Delete(key);
        File.CreateSymbolicLink(key, secret);
        Assert.Equal(DeleteOutcome.Unsafe, Only(_library.Delete([alpha])).Outcome);
        Assert.True(File.Exists(secret));
        Assert.True(File.Exists(Db(FolderOf(alpha))));
        File.Delete(key);
        File.WriteAllText(key, "restored");

        var beta = Closed("Beta");
        var backups = Path.Combine(FolderOf(beta), SqliteWorldStore.BackupFolder);
        var stolen = Path.Combine(_base, "stolen");
        Directory.Move(backups, stolen);
        Directory.CreateSymbolicLink(backups, stolen);
        var copies = Directory.GetFiles(stolen).Length;
        Assert.True(copies > 0);
        Assert.Equal(DeleteOutcome.Unsafe, Only(_library.Delete([beta])).Outcome);
        Assert.Equal(copies, Directory.GetFiles(stolen).Length);
        Assert.True(File.Exists(Db(FolderOf(beta))));
    }

    [Fact]
    public void AFailureBeforeTheWorldIsTakenOutLeavesTheWorldWhole()
    {
        var alpha = Closed("Alpha");
        WorldDeletion.Trace = (step, _) =>
        {
            if (step == "flip")
                throw new IOException("disk says no");
        };

        var failed = Only(_library.Delete([alpha]));

        WorldDeletion.Trace = null;
        Assert.Equal(DeleteOutcome.Failed, failed.Outcome);
        Assert.DoesNotContain("disk says no", failed.Message);
        Assert.Equal(WorldAvailability.Ready, Assert.Single(_library.Scan(Compatible).Worlds).Availability);
        var reopened = _library.Open(alpha, Compatible, TestContext.Current.CancellationToken);
        Assert.Equal(OpenOutcome.Opened, reopened.Outcome);
        reopened.Store!.Dispose();
    }

    [Fact]
    public void AFileThatWillNotGoLeavesNoWorldAndIsFinishedLater()
    {
        var alpha = Closed("Alpha");
        var beta = Closed("Beta");
        WorldDeletion.Trace = (step, path) =>
        {
            if (step == "delete" && Path.GetFileName(path) == WorldIdentity.KeyFile)
                throw new IOException("disk says no");
        };

        var report = _library.Delete([alpha, beta]);

        // Reported per world: alpha is out of the library with a file left; beta failed the same way.
        WorldDeletion.Trace = null;
        Assert.All(report.Results, r => Assert.Equal(DeleteOutcome.Partial, r.Outcome));
        Assert.All(report.Results, r => Assert.DoesNotContain("disk says no", r.Message));
        Assert.Equal(0, report.Deleted);
        Assert.False(report.Complete);

        // What is left is not a world: not listed, not opened, never offered as a copy.
        Assert.Empty(_library.Scan(Compatible).Worlds);
        Assert.Equal(OpenOutcome.Missing, _library.Open(alpha, Compatible, TestContext.Current.CancellationToken).Outcome);
        Assert.False(File.Exists(Db(FolderOf(alpha))));
        Assert.True(File.Exists(Path.Combine(FolderOf(alpha), WorldDeletion.Tombstone)));

        Assert.Equal(2, _library.RecoverDeletions());
        Assert.False(Directory.Exists(FolderOf(alpha)));
        Assert.False(Directory.Exists(FolderOf(beta)));
        Assert.Equal(0, _library.RecoverDeletions());
    }

    [Fact]
    public void ACrashAfterTheWorldIsTakenOutLeavesNoWorldAndTheLeaseFree()
    {
        var alpha = Closed("Alpha");
        WorldDeletion.Trace = (step, path) =>
        {
            if (step == "delete" && Path.GetFileName(path) == WorldIdentity.KeyFile)
                throw new InvalidOperationException("the process died here");
        };

        Assert.Throws<InvalidOperationException>(() => _library.Delete([alpha]));

        WorldDeletion.Trace = null;
        Assert.Empty(_library.Scan(Compatible).Worlds);
        Assert.True(File.Exists(Path.Combine(FolderOf(alpha), WorldIdentity.KeyFile)));
        Assert.Equal(1, _library.RecoverDeletions());
        Assert.False(Directory.Exists(FolderOf(alpha)));
    }

    [Fact]
    public void RecoveryNeverTouchesAFolderThatStillHoldsAWorld()
    {
        var alpha = Closed("Alpha");
        File.WriteAllText(Path.Combine(FolderOf(alpha), WorldDeletion.Tombstone), "left over");

        Assert.Equal(0, _library.RecoverDeletions());

        Assert.True(File.Exists(Db(FolderOf(alpha))));
        Assert.True(File.Exists(Path.Combine(FolderOf(alpha), WorldIdentity.KeyFile)));
        Assert.Equal(WorldAvailability.Ready, Assert.Single(_library.Scan(Compatible).Worlds).Availability);
    }

    [Fact]
    public void TheWorldPlayedLastIsForgottenWhenItGoes()
    {
        var alpha = Closed("Alpha");
        var beta = Closed("Beta");
        _library.RememberLastWorld(alpha);

        Assert.Equal(DeleteOutcome.Deleted, Only(_library.Delete([beta])).Outcome);
        Assert.Equal(alpha, _library.LastWorld());
        Assert.Equal(DeleteOutcome.Deleted, Only(_library.Delete([alpha])).Outcome);
        Assert.Null(_library.LastWorld());
    }

    [Fact]
    public void FilesThePlayerPutInAWorldFolderAreLeftAndSaid()
    {
        var alpha = Closed("Alpha");
        var note = Path.Combine(FolderOf(alpha), "notes.txt");
        File.WriteAllText(note, "my notes");

        var result = Only(_library.Delete([alpha]));

        Assert.Equal(DeleteOutcome.Deleted, result.Outcome);
        Assert.Contains("left", result.Message);
        Assert.True(File.Exists(note));
        Assert.Empty(_library.Scan(Compatible).Worlds);
        Assert.False(File.Exists(Db(FolderOf(alpha))));
    }

    [Fact]
    public void AWorldIsOnlyAskedForOnceAndTheListIsInTheOrderAsked()
    {
        var alpha = Closed("Alpha");
        var beta = Closed("Beta");

        var report = _library.Delete([beta, alpha, beta]);

        Assert.Equal([beta, alpha], report.Results.Select(r => r.FolderId).ToArray());
        Assert.True(report.Complete);
    }

    [Fact]
    public void TheShelfDeletesOnAWorkerReportsOnceAndReadsTheListAgain()
    {
        var current = Closed("Current");
        var other = Closed("Other");
        Tamper(FolderOf(other), "UPDATE world SET generator_version = generator_version + 1");
        var shelf = new WorldShelf(_library, Compatible);
        shelf.Refresh();
        Pump(shelf, () => shelf.Loaded);
        Assert.Equal(2, shelf.List.Worlds.Count);

        // Named, but this build can open "current": the delete for incompatible worlds keeps it.
        Assert.True(shelf.Delete([current, other], onlyIfIncompatible: true));
        Assert.True(shelf.Deleting);
        Assert.False(shelf.Delete([current], onlyIfIncompatible: false));
        Pump(shelf, () => shelf.Report is not null && !shelf.Deleting);
        var report = shelf.TakeReport()!;
        Assert.Null(shelf.TakeReport());
        Assert.Equal([DeleteOutcome.Kept, DeleteOutcome.Deleted], report.Results.Select(r => r.Outcome).ToArray());
        Pump(shelf, () => shelf.List.Worlds.Count == 1);
        Assert.Equal(current, shelf.List.Worlds[0].FolderId);
        Assert.False(Directory.Exists(FolderOf(other)));
    }

    private static void Pump(WorldShelf shelf, Func<bool> done)
    {
        var until = DateTime.UtcNow.AddSeconds(30);
        while (!done())
        {
            Assert.True(DateTime.UtcNow < until, "The shelf did not settle.");
            shelf.Update();
            Thread.Sleep(5);
        }
    }
}
