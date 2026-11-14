using Prospect.Engine.Diagnostics;
using Prospect.Game.Persistence;

namespace Prospect.Client;

/// <summary>
/// The saved worlds as the menus show them: the library read on a worker, one read at a time, and the newest
/// result kept for the frame to draw. A refresh asked for while a read runs is made once that read is done.
/// Deleting worlds runs here too: one delete at a time on a worker, its report kept for the screen, and the list
/// read again when it is done. Used from the main thread; <see cref="Update"/> once a frame.
/// </summary>
internal sealed class WorldShelf
{
    private readonly SaveCompatibility _expected;
    private Task<WorldList>? _scan;
    private Task<DeleteReport>? _delete;
    private string[] _deleting = [];
    private bool _again;

    /// <summary>A shelf over <paramref name="library"/>, or an empty one that never reads when it is null.</summary>
    public WorldShelf(WorldLibrary? library, SaveCompatibility expected)
    {
        Library = library;
        _expected = expected;
    }

    /// <summary>The library, or null when saved worlds are off for this run.</summary>
    public WorldLibrary? Library { get; }

    /// <summary>The newest list read; empty before the first read finishes.</summary>
    public WorldList List { get; private set; } = WorldList.Empty;

    /// <summary>True until the first read has finished.</summary>
    public bool Loading => Library is not null && !Loaded;

    /// <summary>True once a read has finished.</summary>
    public bool Loaded { get; private set; }

    /// <summary>The world played last, from the newest list, or null.</summary>
    public WorldEntry? LastWorld => List.Find(List.LastWorld);

    /// <summary>True while a delete runs on its worker: nothing else on the Load World screen is offered meanwhile.</summary>
    public bool Deleting => _delete is not null;

    /// <summary>The report of the delete that finished last, until <see cref="TakeReport"/> takes it; null when there is none.</summary>
    public DeleteReport? Report { get; private set; }

    /// <summary>Takes the report of a finished delete, once, so a screen shows it once.</summary>
    public DeleteReport? TakeReport()
    {
        var report = Report;
        Report = null;
        return report;
    }

    /// <summary>
    /// Deletes the worlds of these folder ids on a worker, or does nothing and returns false when saved worlds
    /// are off or a delete is already running. With <paramref name="onlyIfIncompatible"/> a world this build can
    /// open is kept even if it is named: what a list showed is judged again under the world's lease.
    /// </summary>
    public bool Delete(string[] folderIds, bool onlyIfIncompatible)
    {
        if (Library is not { } library || _delete is not null || folderIds.Length == 0)
            return false;
        Report = null;
        _deleting = folderIds;
        var only = onlyIfIncompatible ? _expected : (SaveCompatibility?)null;
        _delete = Task.Run(() => library.Delete(folderIds, only));
        return true;
    }

    /// <summary>Starts reading the library again, or asks for it after the read that runs.</summary>
    public void Refresh()
    {
        if (Library is not { } library)
            return;
        if (_scan is not null)
        {
            _again = true;
            return;
        }

        _scan = Task.Run(() =>
        {
            // What an interrupted delete left is finished before the list is read.
            library.RecoverDeletions();
            return library.Scan(_expected);
        });
    }

    /// <summary>Takes a finished read. Called once a frame.</summary>
    public void Update()
    {
        if (_delete is { IsCompleted: true } delete)
        {
            _delete = null;
            if (delete.IsCompletedSuccessfully)
            {
                Report = delete.Result;
            }
            else
            {
                Logs.App.Error($"Deleting saved worlds failed: {delete.Exception}");
                var results = new DeleteResult[_deleting.Length];
                for (var i = 0; i < results.Length; i++)
                    results[i] = new DeleteResult(_deleting[i], DeleteOutcome.Failed, "It could not be deleted.");
                Report = new DeleteReport(results);
            }

            _deleting = [];
            Refresh();
        }

        if (_scan is not { IsCompleted: true } scan)
            return;
        _scan = null;
        if (scan.IsCompletedSuccessfully)
        {
            List = scan.Result;
        }
        else
        {
            Logs.App.Error($"Reading the saved worlds failed: {scan.Exception}");
            List = new WorldList([], null, "The folder of saved worlds could not be read.");
        }

        Loaded = true;
        if (_again)
        {
            _again = false;
            Refresh();
        }
    }

    /// <summary>Waits a bounded time for a read that runs, so nothing reads the disk after the game has gone.</summary>
    public void Wait()
    {
        try
        {
            _delete?.Wait(TimeSpan.FromSeconds(10));
        }
        catch (AggregateException)
        {
            // Logged when the frame takes it; quitting does not wait on that.
        }

        _scan?.Wait(TimeSpan.FromSeconds(10));
    }
}
