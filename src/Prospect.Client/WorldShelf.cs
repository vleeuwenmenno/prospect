using Prospect.Engine.Diagnostics;
using Prospect.Game.Persistence;

namespace Prospect.Client;

/// <summary>
/// The saved worlds as the menus show them: the library read on a worker, one read at a time, and the newest
/// result kept for the frame to draw. A refresh asked for while a read runs is made once that read is done.
/// Used from the main thread; <see cref="Update"/> once a frame.
/// </summary>
internal sealed class WorldShelf
{
    private readonly SaveCompatibility _expected;
    private Task<WorldList>? _scan;
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

        _scan = Task.Run(() => library.Scan(_expected));
    }

    /// <summary>Takes a finished read. Called once a frame.</summary>
    public void Update()
    {
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
    public void Wait() => _scan?.Wait(TimeSpan.FromSeconds(10));
}
