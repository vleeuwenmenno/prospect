using System.Runtime.InteropServices;

namespace Prospect.Game.Persistence;

/// <summary>
/// Making a finished file durable under its final name: the file's contents to the disk, the rename, and then
/// the directory that holds the name. A rename alone is one step for whoever looks at the directory, but the
/// directory entry itself is only as durable as the directory's last sync.
/// <para>
/// What is verified by this project's tests is Linux: <c>fsync</c> on the file and on the directory. macOS takes
/// the same calls, which there reach the drive's cache and not necessarily the platter, and Windows renames with
/// write-through; neither has been run by this project, and <see cref="Verified"/> says so. None of it is a
/// promise about a drive that lies about its cache.
/// </para>
/// </summary>
internal static partial class DurableFiles
{
    /// <summary>True on the one platform where the file and directory sync below has been exercised by the tests.</summary>
    public static bool Verified => OperatingSystem.IsLinux();

    /// <summary>
    /// A hook for tests, on the thread that sets it: called with the step ("sync-file", "sync-directory",
    /// "create-directory") and its path just before the step, and may throw to make the step fail.
    /// </summary>
    [ThreadStatic]
    internal static Action<string, string>? Trace;

    /// <summary>
    /// Creates a directory and every missing directory above it, each durably: a directory that is made is
    /// followed by a sync of the directory it was made in, so its own name is on disk before anything is put
    /// into it.
    /// <para>
    /// A directory that is already there may have been made by an earlier call whose sync failed, so its name
    /// is synced again too, all the way up: a barrier that was missed once is not forgotten. One exception keeps
    /// this from reaching into the system's own directories, which a user often may not even open: where the
    /// system says this user may not write, an existing name is not synced again. That rests on an assumption,
    /// and it is the supported one: permissions on the way to a world do not change between a try that failed
    /// and the next. Nothing here proves who made a directory. Where the permissions were changed in between,
    /// a name made by the failed try is not synced by the next, and is as durable as the file system made it.
    /// </para>
    /// <para>
    /// Throws <see cref="IOException"/> when a sync fails; the directories made so far stay, and the next call
    /// goes over the same ground. On Windows nothing here syncs: a directory cannot be synced there, and the
    /// durability of a new directory's name is the file system's journal's, which this project has not tested.
    /// </para>
    /// </summary>
    public static void CreateDirectory(string directory)
    {
        var ancestors = new Stack<string>();
        for (var at = Path.GetFullPath(directory); at is not null; at = Path.GetDirectoryName(at))
            ancestors.Push(at);
        while (ancestors.Count > 0)
        {
            var next = ancestors.Pop();
            var made = !Directory.Exists(next);
            if (made)
            {
                Trace?.Invoke("create-directory", next);
                if (OperatingSystem.IsWindows()) Directory.CreateDirectory(next);
                else Directory.CreateDirectory(next, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            if (Path.GetDirectoryName(next) is { } parent && (made || MayHoldOurNames(parent)))
                SyncDirectory(parent);
        }
    }

    /// <summary>
    /// Whether an existing name in a directory is synced again: yes, unless the system says outright that this
    /// user may not write there (no permission, or a file system mounted read-only). Any other answer, a path
    /// that cannot be resolved or an error reading it, is not taken for "no": it throws, and nothing is
    /// acknowledged on top of a question that was not answered.
    /// </summary>
    private static bool MayHoldOurNames(string directory)
    {
        if (OperatingSystem.IsWindows() || Access(directory, WriteAccess) == 0)
            return true;
        var error = Marshal.GetLastPInvokeError();
        return error is PermissionDenied or ReadOnlyFileSystem
            ? false
            : throw new IOException($"Could not tell whether {directory} can be written to: error {error}.");
    }

    /// <summary>Flushes a closed file's contents to the disk.</summary>
    public static void SyncFile(string path)
    {
        Trace?.Invoke("sync-file", path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        stream.Flush(flushToDisk: true);
    }

    /// <summary>
    /// Gives a finished file its final name, which must be free, and makes the name durable. The file must
    /// already be synced. Throws <see cref="IOException"/> when the name is taken or the sync fails; the file is
    /// then under one of the two names, whole.
    /// </summary>
    public static void Publish(string from, string to)
    {
        if (OperatingSystem.IsWindows())
        {
            // No directory handle to sync here: the rename itself is asked to be on disk before it returns.
            if (!MoveFileExW(from, to, MoveFileWriteThrough))
                throw new IOException($"Could not publish {Path.GetFileName(to)}: error {Marshal.GetLastPInvokeError()}.");
            return;
        }

        File.Move(from, to, overwrite: false);
        SyncDirectory(Path.GetDirectoryName(Path.GetFullPath(to))!);
    }

    /// <summary>Syncs a directory, so that names created, renamed or removed in it survive. A no-op on Windows.</summary>
    public static void SyncDirectory(string directory)
    {
        Trace?.Invoke("sync-directory", directory);
        if (OperatingSystem.IsWindows())
            return;
        var descriptor = Open(directory, 0);
        if (descriptor < 0)
            throw new IOException($"Could not open {directory} to sync it: error {Marshal.GetLastPInvokeError()}.");
        try
        {
            if (Fsync(descriptor) != 0)
                throw new IOException($"Could not sync {directory}: error {Marshal.GetLastPInvokeError()}.");
        }
        finally
        {
            _ = Close(descriptor);
        }
    }

    private const uint MoveFileWriteThrough = 0x8;

    /// <summary>W_OK, EACCES and EROFS: the same numbers on Linux and macOS.</summary>
    private const int WriteAccess = 2;
    private const int PermissionDenied = 13;
    private const int ReadOnlyFileSystem = 30;

    [LibraryImport("libc", EntryPoint = "access", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Access(string path, int mode);

    [LibraryImport("libc", EntryPoint = "open", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Open(string path, int flags);

    [LibraryImport("libc", EntryPoint = "fsync", SetLastError = true)]
    private static partial int Fsync(int descriptor);

    [LibraryImport("libc", EntryPoint = "close", SetLastError = true)]
    private static partial int Close(int descriptor);

    [LibraryImport("kernel32", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool MoveFileExW(string from, string to, uint flags);
}
