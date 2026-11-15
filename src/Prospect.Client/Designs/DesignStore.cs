using System.Numerics;
using System.Text;
using Prospect.Game.Protocol;
using Prospect.Game.World;

namespace Prospect.Client.Designs;

/// <summary>What a saved design file turned out to be when the folder was listed.</summary>
internal enum DesignFileState : byte
{
    /// <summary>The file is a design this build can read.</summary>
    Readable,

    /// <summary>Too big, a link, not JSON, not a design, or a design this build's content refuses.</summary>
    Unreadable,
}

/// <summary>One file of the designs folder as the Load list shows it. The name is UTF-8, ready to draw.</summary>
internal sealed record DesignEntry(string File, byte[] Name, int Blocks, float BlockSize, DesignFileState State);

/// <summary>How a save ended.</summary>
internal enum DesignSaveStatus : byte
{
    /// <summary>The file is written.</summary>
    Saved,

    /// <summary>A design of that name is already there and overwriting was not asked for. Nothing was written.</summary>
    Exists,

    /// <summary>The name is empty or leaves nothing a file can be called.</summary>
    BadName,

    /// <summary>The draft cannot be written as a design (for example it has no core).</summary>
    Invalid,

    /// <summary>The written design would be larger than <see cref="DesignStore.MaxFileBytes"/>.</summary>
    TooBig,

    /// <summary>The disk refused: the folder cannot be made or written, or is full.</summary>
    Failed,
}

/// <summary>The result of <see cref="DesignStore.Save"/>: how it ended and, when it did not save, a sentence on why.</summary>
internal readonly record struct DesignSaveResult(DesignSaveStatus Status, string Message);

/// <summary>The result of <see cref="DesignStore.Read"/>: the design, or a sentence on why there is none.</summary>
internal readonly record struct DesignReadResult(BoxDesign? Design, string Message);

/// <summary>
/// The ship designs a player saved on this computer: a folder of design files in the same format as the shipped
/// ones in <c>content/ships</c> (version 5, blocks and mounts), read and written with <see cref="BoxDesign"/>. The
/// folder is the player's and its files are not trusted: a file is read only up to <see cref="MaxFileBytes"/>,
/// never through a link, and whatever is wrong with one becomes <see cref="DesignFileState.Unreadable"/>, never an
/// exception. A name becomes a file name only through <see cref="FileStem"/>; a write goes to a temporary file in the
/// same folder and is renamed over the target. Nothing here is for the frame or the tick: call it on a worker.
/// </summary>
internal sealed class DesignStore
{
    /// <summary>The most bytes of a design file that are read or written: far more than any design the server accepts takes.</summary>
    public const int MaxFileBytes = 8 * 1024 * 1024;

    /// <summary>The most files listed; the rest of a crowded folder is left out.</summary>
    public const int MaxListed = 200;

    /// <summary>The most characters of a name that make up its file name.</summary>
    public const int MaxStemChars = 64;

    /// <summary>The most characters of a stored name that are shown.</summary>
    public const int MaxShownChars = 48;

    private const string Extension = ".json";
    private const string TempMark = ".tmp-";
    private readonly string _root;
    private readonly GameContent _content;

    /// <summary>A store in <paramref name="root"/> (made on the first save) that reads designs against <paramref name="content"/>.</summary>
    public DesignStore(string root, GameContent content)
    {
        _root = Path.GetFullPath(root);
        _content = content;
    }

    /// <summary>The folder.</summary>
    public string Root => _root;

    /// <summary>
    /// The file name (without extension) a design name is saved under, or empty when nothing is left of it: letters,
    /// digits, space, <c>-</c>, <c>_</c> and <c>.</c> are kept and everything else (path separators, quotes, control
    /// characters) becomes <c>_</c>; runs of dots become one underscore so no <c>..</c> survives, leading and trailing dots and
    /// spaces go, the result is cut to <see cref="MaxStemChars"/>, and a name Windows reserves (<c>CON</c>, <c>NUL</c>,
    /// <c>COM1</c>...) gets an underscore in front.
    /// </summary>
    public static string FileStem(string name)
    {
        var text = new StringBuilder(Math.Min(name.Length, MaxStemChars + 8));
        var dots = 0;
        foreach (var rune in name.Trim().EnumerateRunes())
        {
            if (text.Length >= MaxStemChars)
                break;
            var c = rune.Value;
            if (c == '.')
            {
                dots++;
                if (dots == 1)
                    text.Append('.');
                else if (dots == 2)
                    text[^1] = '_';
                continue;
            }

            dots = 0;
            if (Rune.IsLetterOrDigit(rune))
                text.Append(rune.ToString());
            else
                text.Append(c is ' ' or '-' or '_' ? (char)c : '_');
        }

        var stem = text.ToString().Trim(' ', '.');
        if (stem.Length == 0 || stem.Trim('_').Length == 0)
            return "";
        var head = stem.Split('.')[0].Trim().ToUpperInvariant();
        if (head is "CON" or "PRN" or "AUX" or "NUL" || head.Length == 4 && head.StartsWith("COM", StringComparison.Ordinal) && char.IsDigit(head[3])
            || head.Length == 4 && head.StartsWith("LPT", StringComparison.Ordinal) && char.IsDigit(head[3]))
            stem = "_" + stem;
        return stem;
    }

    /// <summary>The path a design with this file stem lives at, always inside the folder.</summary>
    public string PathOf(string stem) => Path.Combine(_root, stem + Extension);

    /// <summary>
    /// Writes the draft's blocks and parts as a design called <paramref name="name"/>. Without
    /// <paramref name="overwrite"/> an existing file is left alone and the answer is <see cref="DesignSaveStatus.Exists"/>.
    /// The file is written whole to a temporary one and renamed, so a crash or a full disk never leaves half a design
    /// under the name.
    /// </summary>
    public DesignSaveResult Save(string name, float blockSize, HullClass hullClass, Vector3 color, DesignBox[] boxes, DesignMount[] mounts, bool overwrite)
    {
        name = name.Trim();
        var stem = FileStem(name);
        if (NameProblems(name) || stem.Length == 0)
            return new DesignSaveResult(DesignSaveStatus.BadName, "That name cannot be used for a file. Use letters, digits or spaces.");
        var path = PathOf(stem);
        if (!Path.GetFullPath(path).StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            return new DesignSaveResult(DesignSaveStatus.BadName, "That name cannot be used for a file.");

        byte[] bytes;
        try
        {
            using var stream = new MemoryStream();
            new BoxDesign(name, blockSize, boxes, _content.Blocks.Limits, "draft", hullClass, color, mounts).Write(stream);
            if (stream.Length > MaxFileBytes)
                return new DesignSaveResult(DesignSaveStatus.TooBig, "The design is too big to save.");
            bytes = stream.ToArray();
        }
        catch (Exception e) when (e is InvalidDataException or ArgumentException or InvalidOperationException)
        {
            return new DesignSaveResult(DesignSaveStatus.Invalid, "The draft cannot be saved: " + Trimmed(e.Message));
        }

        var temp = path + TempMark + Guid.NewGuid().ToString("N");
        try
        {
            Directory.CreateDirectory(_root);
            if (!overwrite && File.Exists(path))
                return new DesignSaveResult(DesignSaveStatus.Exists, "");
            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                file.Write(bytes);
                file.Flush(true);
            }

            File.Move(temp, path, overwrite: true);
            return new DesignSaveResult(DesignSaveStatus.Saved, "");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or System.Security.SecurityException)
        {
            try { File.Delete(temp); } catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
            return new DesignSaveResult(DesignSaveStatus.Failed, "The designs folder could not be written. Check that it has room.");
        }
    }

    /// <summary>
    /// Lists the folder, sorted by name, at most <see cref="MaxListed"/> files, each opened to read its name, block
    /// count and block size. A missing folder is an empty list.
    /// </summary>
    public DesignEntry[] List()
    {
        if (!Directory.Exists(_root))
            return [];
        var files = new List<string>();
        try
        {
            foreach (var path in Directory.EnumerateFiles(_root, "*" + Extension, new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = false }))
                if (path.EndsWith(Extension, StringComparison.OrdinalIgnoreCase))
                    files.Add(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        files.Sort(StringComparer.OrdinalIgnoreCase);
        if (files.Count > MaxListed)
            files.RemoveRange(MaxListed, files.Count - MaxListed);
        var entries = new DesignEntry[files.Count];
        for (var i = 0; i < entries.Length; i++)
        {
            var file = Path.GetFileName(files[i]);
            var design = ReadFile(files[i], out _);
            entries[i] = design is null
                ? new DesignEntry(file, Encoding.UTF8.GetBytes(Shown(Path.GetFileNameWithoutExtension(file))), 0, 0f, DesignFileState.Unreadable)
                : new DesignEntry(file, Encoding.UTF8.GetBytes(Shown(design.Name)), design.Blocks.Length, design.BlockSize, DesignFileState.Readable);
        }

        return entries;
    }

    /// <summary>
    /// Reads one listed file (a name from <see cref="DesignEntry.File"/>, never a path) and runs on it the checks a
    /// server runs on a received design, so what comes back can be put in a draft without a second look.
    /// </summary>
    public DesignReadResult Read(string file)
    {
        if (file.Length == 0 || file != Path.GetFileName(file) || !file.EndsWith(Extension, StringComparison.OrdinalIgnoreCase))
            return new DesignReadResult(null, "That is not a saved design.");
        var design = ReadFile(Path.Combine(_root, file), out var problem);
        if (design is null)
            return new DesignReadResult(null, problem);
        var check = BuildDraft.Check(design, _content);
        return check.Ok ? new DesignReadResult(design, "") : new DesignReadResult(null, "This design cannot be used: " + Trimmed(check.Problem ?? "refused"));
    }

    /// <summary>Whether a design of this name has a file already; for tests and the capture hook, since the save asks for itself.</summary>
    public bool Exists(string name) => FileStem(name) is { Length: > 0 } stem && File.Exists(PathOf(stem));

    // The file as a design, or null with the reason. Never throws for anything a file can contain.
    private BoxDesign? ReadFile(string path, out string problem)
    {
        problem = "";
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.LinkTarget is not null || (info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                problem = "That file is not a design.";
                return null;
            }

            if (info.Length > MaxFileBytes)
            {
                problem = "That file is too big to be a design.";
                return null;
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var bytes = new byte[(int)Math.Min(stream.Length, MaxFileBytes + 1L)];
            stream.ReadExactly(bytes);
            return BoxDesign.Read(bytes, _content.Blocks, Path.GetFileName(path));
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            problem = "This file is not a design this version can read.";
            return null;
        }
    }

    // A name longer than a ship's, or with a control character, is not saved: the file would not load as a ship name.
    private static bool NameProblems(string name)
    {
        ShipName probe = default;
        return !probe.Set(name);
    }

    // What a list or a notice shows of a stored name: control characters out, cut to a readable length.
    private static string Shown(string name)
    {
        var text = new StringBuilder();
        var count = 0;
        foreach (var rune in name.EnumerateRunes())
        {
            if (count++ >= MaxShownChars)
                break;
            text.Append(Rune.IsControl(rune) ? "?" : rune.ToString());
        }

        return text.Length == 0 ? "?" : text.ToString();
    }

    private static string Trimmed(string message) => message.Length <= 120 ? message : message[..120];
}
