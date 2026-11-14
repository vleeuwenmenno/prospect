using Prospect.Engine.Diagnostics;

namespace Prospect.Game.Persistence;

/// <summary>How deleting one saved world ended.</summary>
public enum DeleteOutcome : byte
{
    /// <summary>The world and its files are gone.</summary>
    Deleted,

    /// <summary>The world is open in a running game, this one or another. Nothing was touched.</summary>
    Busy,

    /// <summary>There is no such world any more. Nothing was touched.</summary>
    Missing,

    /// <summary>It was asked for only if this build cannot open it, and this build can. Nothing was touched.</summary>
    Kept,

    /// <summary>The folder, or something in it, is a link, or the name is not a world's. Nothing was touched.</summary>
    Unsafe,

    /// <summary>A step before the world was taken out of the library failed. The world is as it was.</summary>
    Failed,

    /// <summary>The world is out of the library, but some of its files could not be removed yet. See <see cref="WorldLibrary.RecoverDeletions"/>.</summary>
    Partial,
}

/// <summary>What happened to one world a delete was asked for.</summary>
/// <param name="FolderId">The world's folder id inside the library.</param>
/// <param name="Outcome">How it ended.</param>
/// <param name="Message">A sentence for the player: it names no folder or file. Empty when the world was deleted without a remark.</param>
public sealed record DeleteResult(string FolderId, DeleteOutcome Outcome, string Message);

/// <summary>What a delete of one or more worlds did, one result for each world asked for, in the order asked.</summary>
/// <param name="Results">One result per world.</param>
public sealed record DeleteReport(IReadOnlyList<DeleteResult> Results)
{
    /// <summary>How many worlds are gone.</summary>
    public int Deleted
    {
        get
        {
            var count = 0;
            foreach (var result in Results)
            {
                if (result.Outcome == DeleteOutcome.Deleted)
                    count++;
            }

            return count;
        }
    }

    /// <summary>True when every world asked for is gone and nothing was left behind.</summary>
    public bool Complete => Results.Count > 0 && Deleted == Results.Count && !HasNotes;

    /// <summary>True when some deleted world has a remark.</summary>
    private bool HasNotes
    {
        get
        {
            foreach (var result in Results)
            {
                if (result.Message.Length > 0)
                    return true;
            }

            return false;
        }
    }
}

/// <summary>
/// Deleting a world folder of the library, as <see cref="WorldLibrary.Delete"/> asks for it. Nothing in here
/// leaves the library: the folder is named by a checked folder id under the library's root, a link is never
/// followed, and only the files this game makes in a world folder are removed.
/// <para>
/// The order is what makes a crash safe. (1) The world's lease is taken, so a world that runs, here or in
/// another process, is refused. (2) <c>world.db</c> is renamed to <see cref="Tombstone"/> and the folder synced:
/// from that moment the folder is no world, to a list, an open or a copy, and a crash leaves nothing a library
/// treats as valid. (3) The rest goes: log, key, recovery copies, then the tombstone itself, last. (4) The lease
/// is let go, and the lock file and the folder follow. A crash after (2) leaves a folder with a tombstone and no
/// <c>world.db</c>; <see cref="WorldLibrary.RecoverDeletions"/> takes it from step (3) again.
/// </para>
/// <para>
/// Only <see cref="Run"/> touches the disk; call it off the frame loop and off the tick. Verified on Linux; the
/// rename and delete semantics on macOS and Windows are not tested here.
/// </para>
/// </summary>
internal static class WorldDeletion
{
    /// <summary>
    /// What <c>world.db</c> is renamed to while its world is deleted. A folder that holds it and no
    /// <c>world.db</c> is a world on its way out, never one to open.
    /// </summary>
    public const string Tombstone = "world.db.deleting";

    /// <summary>
    /// A hook for tests, on the thread that sets it: called with the step ("flip", "delete") and its path just
    /// before the step, and may throw an <see cref="IOException"/> to make that step fail.
    /// </summary>
    [ThreadStatic]
    internal static Action<string, string>? Trace;

    /// <summary>
    /// Deletes one world. With <paramref name="onlyIfIncompatibleWith"/> it is deleted only if this compatibility
    /// cannot open it, judged under the lease, so a world that changed since a list was read is not lost. A
    /// folder that is already a tombstone is finished. Never throws for a world that cannot be deleted: the
    /// result says why.
    /// </summary>
    public static DeleteResult Run(string root, string folderId, SaveCompatibility? onlyIfIncompatibleWith)
    {
        if (!WorldLibrary.IsFolderId(folderId))
            return new(folderId, DeleteOutcome.Unsafe, "That is not a saved world.");
        var folder = Path.GetFullPath(Path.Combine(root, folderId));

        // Only a folder directly inside the library, whatever the id tried.
        if (!string.Equals(Path.GetDirectoryName(folder), Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.Ordinal))
            return new(folderId, DeleteOutcome.Unsafe, "That is not a saved world.");

        IDisposable? lease = null;
        try
        {
            if (!Directory.Exists(folder))
                return new(folderId, DeleteOutcome.Missing, "It is no longer there.");
            if (IsLink(folder) || HasLinks(folder))
                return new(folderId, DeleteOutcome.Unsafe, "Its folder is a link or holds one, so nothing was touched. Delete it by hand if you want it gone.");

            var database = Path.Combine(folder, SqliteWorldStore.DatabaseFile);
            var tombstone = Path.Combine(folder, Tombstone);
            if (!File.Exists(database) && !File.Exists(tombstone))
                return new(folderId, DeleteOutcome.Missing, "It is no longer there.");

            lease = SqliteWorldStore.TakeLease(folder, out var busy, out var leaseError);
            if (lease is null)
            {
                Logs.App.Warning($"World {folderId} was not deleted: {leaseError}");
                return busy
                    ? new(folderId, DeleteOutcome.Busy, "It is open in a running game. Close it there first.")
                    : new(folderId, DeleteOutcome.Failed, "Its files could not be locked. Nothing was deleted.");
            }

            // Judged again under the lease: what a list showed may be older than the lock.
            var live = File.Exists(database);
            if (!live && !File.Exists(tombstone))
                return new(folderId, DeleteOutcome.Missing, "It is no longer there.");
            if (live && onlyIfIncompatibleWith is { } expected)
            {
                var summary = SqliteWorldStore.SummarizeLeased(database);
                var incompatible = summary.Outcome == OpenOutcome.Incompatible || (summary.Outcome == OpenOutcome.Opened && summary.Compatibility != expected);
                if (!incompatible)
                    return new(folderId, DeleteOutcome.Kept, "This version of the game can open it, so it was kept.");
            }

            if (live)
            {
                var flip = Flip(folder, database, tombstone);
                if (flip is not null)
                    return new(folderId, DeleteOutcome.Failed, flip);
            }

            return Finish(root, folderId, folder, tombstone, ref lease);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Logs.App.Warning($"World {folderId} was not deleted: {e.Message}");
            return new(folderId, DeleteOutcome.Failed, "Its files could not be read. Nothing was deleted.");
        }
        finally
        {
            lease?.Dispose();
        }
    }

    /// <summary>Takes the world out of the library: rename, then sync. Returns a message when that failed, with the world as it was.</summary>
    private static string? Flip(string folder, string database, string tombstone)
    {
        try
        {
            Trace?.Invoke("flip", database);
            File.Move(database, tombstone, overwrite: false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Logs.App.Warning($"World {Path.GetFileName(folder)} was not deleted: the database could not be set aside: {e.Message}");
            return "It could not be taken out of the library. Nothing was deleted.";
        }

        try
        {
            DurableFiles.SyncDirectory(folder);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Not known to be durable: put the world back rather than delete what a power loss could give back.
            Logs.App.Warning($"World {Path.GetFileName(folder)} was not deleted: the rename could not be synced: {e.Message}");
            try
            {
                File.Move(tombstone, database, overwrite: false);
            }
            catch (Exception back) when (back is IOException or UnauthorizedAccessException)
            {
                Logs.App.Error($"World {Path.GetFileName(folder)}: the database could not be put back: {back.Message}");
            }

            return "It could not be taken out of the library durably. Nothing was deleted.";
        }

        return null;
    }

    /// <summary>The steps after the flip. Also the whole of a recovery.</summary>
    private static DeleteResult Finish(string root, string folderId, string folder, string tombstone, ref IDisposable? lease)
    {
        var failed = 0;
        void Remove(string path)
        {
            try
            {
                Trace?.Invoke("delete", path);
                File.Delete(path);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                failed++;
                Logs.App.Warning($"World {folderId}: could not remove {Path.GetFileName(path)}: {e.Message}");
            }
        }

        foreach (var name in new[]
        {
            SqliteWorldStore.DatabaseFile + "-wal",
            SqliteWorldStore.DatabaseFile + "-shm",
            SqliteWorldStore.DatabaseFile + "-journal",
            WorldIdentity.KeyFile,
            WorldIdentity.KeyFile + ".creating",
            SqliteWorldStore.DatabaseFile + ".creating",
            SqliteWorldStore.DatabaseFile + ".creating-wal",
            SqliteWorldStore.DatabaseFile + ".creating-shm",
            SqliteWorldStore.DatabaseFile + ".creating-journal",
        })
            Remove(Path.Combine(folder, name));

        var left = false;
        var backups = Path.Combine(folder, SqliteWorldStore.BackupFolder);
        if (Directory.Exists(backups))
        {
            foreach (var file in Directory.EnumerateFiles(backups))
            {
                if (IsBackupFile(Path.GetFileName(file)))
                    Remove(file);
            }

            if (failed == 0)
            {
                try
                {
                    Directory.Delete(backups, recursive: false);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // Something the game did not make is in there: it is left alone.
                    left = true;
                }
            }
        }

        // The tombstone goes last: while it is there a recovery knows the folder is not done.
        if (failed == 0)
            Remove(tombstone);
        if (failed > 0)
            return new(folderId, DeleteOutcome.Partial, "It is gone from the list, but some of its files could not be removed. The game tries again when it next reads the list.");

        try
        {
            DurableFiles.SyncDirectory(folder);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Logs.App.Warning($"World {folderId}: the removal could not be synced: {e.Message}");
        }

        // The lock file is only removed with nobody holding it, and the folder only when nothing else is in it.
        lease?.Dispose();
        lease = null;
        try
        {
            File.Delete(Path.Combine(folder, SqliteWorldStore.LockFile));
            Directory.Delete(folder, recursive: false);
            try
            {
                DurableFiles.SyncDirectory(root);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Logs.App.Warning($"World {folderId}: the library folder could not be synced: {e.Message}");
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            left = true;
        }

        Logs.App.Info($"World {folderId} was deleted");
        return new(folderId, DeleteOutcome.Deleted, left ? "Files you put in its folder were left there." : "");
    }

    /// <summary>Whether a file name in a world's recovery folder is one this game makes.</summary>
    private static bool IsBackupFile(string name)
    {
        if (name.StartsWith("staging.db", StringComparison.Ordinal))
            return name is "staging.db" or "staging.db-wal" or "staging.db-shm" or "staging.db-journal";
        return name.StartsWith("world-", StringComparison.Ordinal)
            && (name.EndsWith(".db", StringComparison.Ordinal) || name.EndsWith(".db-wal", StringComparison.Ordinal) || name.EndsWith(".db-shm", StringComparison.Ordinal) || name.EndsWith(".db-journal", StringComparison.Ordinal));
    }

    private static bool IsLink(string path)
    {
        var info = new FileInfo(path);
        return info.LinkTarget is not null || ((info.Exists || Directory.Exists(path)) && info.Attributes.HasFlag(FileAttributes.ReparsePoint));
    }

    /// <summary>Whether anything directly in the folder, or in its recovery folder, is a link. A link is never followed, so a world holding one is refused whole.</summary>
    private static bool HasLinks(string folder)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(folder))
        {
            if (IsLink(entry))
                return true;
        }

        var backups = Path.Combine(folder, SqliteWorldStore.BackupFolder);
        if (Directory.Exists(backups))
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(backups))
            {
                if (IsLink(entry))
                    return true;
            }
        }

        return false;
    }
}
