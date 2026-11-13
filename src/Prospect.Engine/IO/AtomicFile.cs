namespace Prospect.Engine.IO;

/// <summary>
/// Writes files so that a crash or power loss leaves either the complete old contents or the complete new contents,
/// never a truncated mix. The data goes to a temporary file in the same directory, is flushed to the disk, and is
/// then renamed over the target, which the file system performs as a single step.
/// </summary>
public static class AtomicFile
{
    public static void Write(string path, ReadOnlySpan<byte> contents)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        AppPaths.CreatePrivateDirectory(directory);

        var temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Environment.ProcessId}.tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(contents);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, path, overwrite: true);
        }
        catch
        {
            File.Delete(temporary);
            throw;
        }
    }
}
