using System.Globalization;
using System.Text;
using Prospect.Engine.Diagnostics;
using Prospect.Engine.IO;
using Prospect.Game.Protocol;
using Prospect.Game.World;

namespace Prospect.Game.Persistence;

/// <summary>Whether a world in a list can be opened now, and if not, the kind of reason.</summary>
public enum WorldAvailability : byte
{
    /// <summary>It can be opened.</summary>
    Ready,

    /// <summary>Another running game has it open.</summary>
    Busy,

    /// <summary>It was saved by another build of the game or with other game files.</summary>
    Incompatible,

    /// <summary>Its files are not a world this game wrote, or its save does not read back.</summary>
    Damaged,

    /// <summary>Its files could not be read at all: permissions, a failing disk.</summary>
    Unreadable,
}

/// <summary>
/// One world folder as a list of worlds shows it. Every text is made when the list is read, off the frame loop,
/// so a screen draws it as it is. Nothing here is a path: a world is named by its folder id inside the library.
/// </summary>
/// <param name="FolderId">The folder's name inside the library; see <see cref="WorldLibrary.IsFolderId"/>.</param>
/// <param name="Name">The world's own name, or a stand-in when it could not be read.</param>
/// <param name="Availability">Whether it can be opened now.</param>
/// <param name="Reason">Why it cannot be opened, or a note about one that can; empty when there is nothing to say.</param>
/// <param name="SavedText">When it was last saved, in local time, or empty when that is not known.</param>
/// <param name="SavedUnixMs">The same moment for sorting; 0 when not known.</param>
/// <param name="CleanClose">False when the world's last run did not end with a final save.</param>
public sealed record WorldEntry(string FolderId, string Name, WorldAvailability Availability, string Reason, string SavedText, long SavedUnixMs, bool CleanClose)
{
    /// <summary>True when the world can be opened now.</summary>
    public bool Ready => Availability == WorldAvailability.Ready;
}

/// <summary>What a look at the library found: its worlds, newest save first, and the world played last.</summary>
/// <param name="Worlds">Every world folder found, at most <see cref="WorldLibrary.MaxWorlds"/>.</param>
/// <param name="LastWorld">The folder id of the world played last, or null when none is remembered.</param>
/// <param name="Error">Why the library itself could not be read; null when it could.</param>
public sealed record WorldList(IReadOnlyList<WorldEntry> Worlds, string? LastWorld, string? Error)
{
    /// <summary>A list with nothing in it.</summary>
    public static WorldList Empty { get; } = new([], null, null);

    /// <summary>The entry of a folder id, or null when the list has none.</summary>
    public WorldEntry? Find(string? folderId)
    {
        if (folderId is null)
            return null;
        foreach (var world in Worlds)
        {
            if (world.FolderId == folderId)
                return world;
        }

        return null;
    }
}

/// <summary>
/// The folder that holds a player's saved worlds: one folder per world, each one a <see cref="SqliteWorldStore"/>
/// world, and a small file naming the world played last. Every member reads or writes the disk: call them off the
/// frame loop and off the tick.
/// <para>
/// A world's folder is named by an id the library makes (the world's identity, as 32 hexadecimal digits), never
/// by the world's name, so a name can be anything a world name may be and is never a path. A world folder copied
/// in under another name is listed too, as long as its name is one plain folder name.
/// </para>
/// </summary>
public sealed class WorldLibrary
{
    /// <summary>The file in the library that names the world played last.</summary>
    public const string LastWorldFile = "last-world";

    /// <summary>The most world folders a list reads; more are left out of it, never touched.</summary>
    public const int MaxWorlds = 256;

    /// <summary>The longest folder id the library accepts.</summary>
    public const int MaxFolderIdLength = 128;

    // A summary takes a world's lease for the moment it looks, so a list being read in this process would make an
    // open of the same world fail as busy. One library call at a time keeps them apart.
    private readonly Lock _gate = new();

    /// <summary>A library rooted at a folder, which need not exist yet: it is made when the first world is.</summary>
    public WorldLibrary(string root)
    {
        if (string.IsNullOrWhiteSpace(root))
            throw new ArgumentException("A world library needs a folder.", nameof(root));
        Root = Path.GetFullPath(root);
    }

    /// <summary>The library's folder, absolute.</summary>
    public string Root { get; }

    /// <summary>
    /// True for a name that is one plain folder inside the library: not empty, not <c>.</c> or <c>..</c>, no
    /// separator, nothing a file name may not hold, no space at either end, and at most
    /// <see cref="MaxFolderIdLength"/> characters. Only such a name is ever joined to <see cref="Root"/>.
    /// </summary>
    public static bool IsFolderId(string? id)
    {
        if (string.IsNullOrEmpty(id) || id.Length > MaxFolderIdLength || id is "." or ".." || id.Trim() != id)
            return false;
        if (id.AsSpan().IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || id.Contains('/') || id.Contains('\\') || id.Contains(':'))
            return false;
        foreach (var c in id)
        {
            if (char.IsControl(c))
                return false;
        }

        return true;
    }

    /// <summary>The folder id a new world is given: its identity as 32 lower-case hexadecimal digits.</summary>
    public static string NewFolderId(Guid worldId) => worldId.ToString("N", CultureInfo.InvariantCulture);

    /// <summary>The folder of a world. Throws <see cref="ArgumentException"/> for an id that is not a folder id.</summary>
    public string FolderOf(string folderId)
    {
        if (!IsFolderId(folderId))
            throw new ArgumentException("That is not a world folder id.", nameof(folderId));
        return Path.Combine(Root, folderId);
    }

    /// <summary>
    /// Reads the library: every folder with a world database in it, summarised as
    /// <see cref="SqliteWorldStore.Summarize"/> sees it and judged against <paramref name="expected"/>, newest
    /// save first, plus the world played last. A world that is running, damaged or saved by another build stays
    /// in the list with the reason. Nothing is written. Bounded: at most <see cref="MaxWorlds"/> folders are read.
    /// </summary>
    public WorldList Scan(SaveCompatibility expected)
    {
        lock (_gate)
            return ScanLocked(expected);
    }

    private WorldList ScanLocked(SaveCompatibility expected)
    {
        var last = LastWorld();
        if (!Directory.Exists(Root))
            return new WorldList([], last, null);

        var worlds = new List<WorldEntry>();
        try
        {
            foreach (var folder in Directory.EnumerateDirectories(Root))
            {
                var id = Path.GetFileName(folder);
                if (!IsFolderId(id) || !File.Exists(Path.Combine(folder, SqliteWorldStore.DatabaseFile)))
                    continue;
                if (worlds.Count == MaxWorlds)
                {
                    Logs.App.Warning($"More than {MaxWorlds} worlds in the library; the rest are not listed");
                    break;
                }

                worlds.Add(Describe(id, SqliteWorldStore.Summarize(folder), expected));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Logs.App.Warning($"Could not read the world library: {e.Message}");
            return new WorldList(worlds, last, "The folder of saved worlds could not be read.");
        }

        worlds.Sort(static (a, b) => b.SavedUnixMs != a.SavedUnixMs ? b.SavedUnixMs.CompareTo(a.SavedUnixMs) : string.CompareOrdinal(a.FolderId, b.FolderId));
        return new WorldList(worlds, last, null);
    }

    /// <summary>The folder id of the world played last, or null when none is remembered or the file is not one.</summary>
    public string? LastWorld()
    {
        try
        {
            var path = Path.Combine(Root, LastWorldFile);
            if (!File.Exists(path) || new FileInfo(path).Length > MaxFolderIdLength * 4)
                return null;
            var id = File.ReadAllText(path, Encoding.UTF8).Trim();
            return IsFolderId(id) ? id : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Remembers a world as the one played last. A failure is logged: Continue then offers the one before.</summary>
    public void RememberLastWorld(string folderId)
    {
        if (!IsFolderId(folderId))
            throw new ArgumentException("That is not a world folder id.", nameof(folderId));
        try
        {
            AtomicFile.Write(Path.Combine(Root, LastWorldFile), Encoding.UTF8.GetBytes(folderId));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Logs.App.Warning($"Could not remember the last world: {e.Message}");
        }
    }

    /// <summary>
    /// Creates a world in a new folder of its own, named by the snapshot's identity, and returns it leased and
    /// running, as <see cref="SqliteWorldStore.Create(string, WorldSnapshot)"/> does. A folder that already holds a
    /// world is never built over. Throws <see cref="OperationCanceledException"/>, having touched nothing, when
    /// <paramref name="cancel"/> is cancelled before the library is free.
    /// </summary>
    public CreateResult Create(WorldSnapshot initial, out string folderId, CancellationToken cancel = default)
    {
        folderId = NewFolderId(initial.WorldId);
        var folder = FolderOf(folderId);
        EnterGate(cancel);
        try
        {
            return SqliteWorldStore.Create(folder, initial);
        }
        finally
        {
            _gate.Exit();
        }
    }

    /// <summary>
    /// Opens a world of the library, as <see cref="SqliteWorldStore.Open(string, SaveCompatibility)"/> does.
    /// Throws <see cref="OperationCanceledException"/>, having touched nothing, when <paramref name="cancel"/> is
    /// cancelled before the library is free.
    /// </summary>
    public OpenResult Open(string folderId, SaveCompatibility expected, CancellationToken cancel = default)
    {
        var folder = FolderOf(folderId);
        EnterGate(cancel);
        try
        {
            return SqliteWorldStore.Open(folder, expected);
        }
        finally
        {
            _gate.Exit();
        }
    }

    /// <summary>
    /// Deletes worlds of the library: each one's save, its identity key and its recovery copies, for good. A world
    /// that is open anywhere (this game's own session, one still being released, another process) is refused, a
    /// folder that is a link or holds one is refused, and nothing outside the library's folders is touched. With
    /// <paramref name="onlyIfIncompatibleWith"/> a world is deleted only if that compatibility cannot open it,
    /// judged under the world's own lease. Every world gets a result; a failure for one does not stop the others.
    /// A world's folder is first made not a world (see <see cref="WorldDeletion"/>), so a crash half way never
    /// leaves one the library would list. Run it on a worker.
    /// </summary>
    /// <exception cref="ArgumentException">More than <see cref="MaxWorlds"/> worlds were asked for.</exception>
    public DeleteReport Delete(IReadOnlyList<string> folderIds, SaveCompatibility? onlyIfIncompatibleWith = null)
    {
        ArgumentNullException.ThrowIfNull(folderIds);
        if (folderIds.Count > MaxWorlds)
            throw new ArgumentException($"At most {MaxWorlds} worlds are deleted at once.", nameof(folderIds));
        var results = new List<DeleteResult>(folderIds.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        lock (_gate)
        {
            var last = LastWorld();
            foreach (var id in folderIds)
            {
                if (!seen.Add(id))
                    continue;
                var result = WorldDeletion.Run(Root, id, onlyIfIncompatibleWith);
                results.Add(result);

                // The world played last is gone: Continue says so rather than trying a folder that is not there.
                if (result.Outcome is DeleteOutcome.Deleted or DeleteOutcome.Partial && id == last)
                    ForgetLastWorld();
            }
        }

        return new DeleteReport(results);
    }

    /// <summary>
    /// Finishes the deletions a crash or a failure interrupted: a folder that holds the tombstone of
    /// <see cref="WorldDeletion"/> and no database. Never touches a folder that still has a world in it.
    /// Returns how many it finished. Run it on a worker, before reading the list.
    /// </summary>
    public int RecoverDeletions()
    {
        lock (_gate)
        {
            if (!Directory.Exists(Root))
                return 0;
            var finished = 0;
            try
            {
                foreach (var folder in Directory.EnumerateDirectories(Root))
                {
                    var id = Path.GetFileName(folder);
                    if (!IsFolderId(id) || File.Exists(Path.Combine(folder, SqliteWorldStore.DatabaseFile)) || !File.Exists(Path.Combine(folder, WorldDeletion.Tombstone)))
                        continue;
                    var result = WorldDeletion.Run(Root, id, null);
                    if (result.Outcome == DeleteOutcome.Deleted)
                        finished++;
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Logs.App.Warning($"Could not look for interrupted deletions: {e.Message}");
            }

            return finished;
        }
    }

    private void ForgetLastWorld()
    {
        try
        {
            File.Delete(Path.Combine(Root, LastWorldFile));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Logs.App.Warning($"Could not forget the last world: {e.Message}");
        }
    }

    /// <summary>Takes the library's gate, giving up when <paramref name="cancel"/> is cancelled: a list being read never traps an open that is no longer wanted.</summary>
    private void EnterGate(CancellationToken cancel)
    {
        while (!_gate.TryEnter(50))
            cancel.ThrowIfCancellationRequested();
        if (!cancel.IsCancellationRequested)
            return;
        _gate.Exit();
        cancel.ThrowIfCancellationRequested();
    }

    /// <summary>
    /// A sentence for the player about why a world did not open. The store's own message is not shown: it can
    /// name files and folders. It goes to the log instead.
    /// </summary>
    public static string Explain(OpenOutcome outcome) =>
        outcome switch
        {
            OpenOutcome.Missing => "That world is no longer there.",
            OpenOutcome.Busy => "That world is open in another running game. Close it there first.",
            OpenOutcome.Incompatible => "That world was saved by another version of the game or with other game files. It was left as it is.",
            OpenOutcome.Damaged => "That world's save is damaged and cannot be opened. It was left as it is.",
            _ => "That world could not be read. Check that its folder can be read and written.",
        };

    private static WorldEntry Describe(string id, WorldSummary summary, SaveCompatibility expected)
    {
        if (summary.SavedUnixMs < DateTimeOffset.MinValue.ToUnixTimeMilliseconds()
            || summary.SavedUnixMs > DateTimeOffset.MaxValue.ToUnixTimeMilliseconds())
            summary = summary with { Outcome = OpenOutcome.Damaged, SavedUnixMs = 0, Error = "The saved timestamp is outside the supported range." };
        var saved = summary.SavedUnixMs > 0
            ? DateTimeOffset.FromUnixTimeMilliseconds(summary.SavedUnixMs).ToLocalTime().ToString("yyyy-MM-dd  HH:mm", CultureInfo.InvariantCulture)
            : "";
        var name = summary.Name.Length > 0 && ServerNames.TryNormalize(summary.Name, out var clean) ? clean : "Unnamed world";
        if (summary.Outcome != OpenOutcome.Opened && summary.Error is { } error)
            Logs.App.Info($"World {id}: {summary.Outcome}: {error}");

        return summary.Outcome switch
        {
            OpenOutcome.Opened when summary.Compatibility != expected =>
                new(id, name, WorldAvailability.Incompatible, "Saved by another version of the game or with other game files.", saved, summary.SavedUnixMs, summary.WasCleanClose),
            OpenOutcome.Opened =>
                new(id, name, WorldAvailability.Ready, summary.WasCleanClose ? "" : "Its last session did not close cleanly. It goes on from its last save.", saved, summary.SavedUnixMs, summary.WasCleanClose),
            OpenOutcome.Incompatible =>
                new(id, name, WorldAvailability.Incompatible, "Saved by another version of the game or with other game files.", saved, summary.SavedUnixMs, summary.WasCleanClose),
            OpenOutcome.Busy => new(id, "World in use", WorldAvailability.Busy, "Open in another running game.", "", 0, true),
            OpenOutcome.Damaged => new(id, "Damaged world", WorldAvailability.Damaged, "The save is damaged and cannot be opened. It is kept as it is.", "", 0, false),
            _ => new(id, "Unreadable world", WorldAvailability.Unreadable, "Its files could not be read.", "", 0, false),
        };
    }
}
