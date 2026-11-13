using Prospect.Engine.Diagnostics;
using Prospect.Game.Client;
using Prospect.Game.Lobby;
using Prospect.Game.Persistence;
using Prospect.Game.Protocol;
using Prospect.Game.Server;
using Prospect.Game.World;

namespace Prospect.Client;

/// <summary>What goes wrong with a world's final save on purpose (<c>--save-fault</c>), for captures only.</summary>
internal enum StoreFault : byte
{
    /// <summary>Nothing.</summary>
    None,

    /// <summary>The final save takes eight seconds, so the saving screen can be captured.</summary>
    Slow,

    /// <summary>The final save fails, and is not written.</summary>
    Fail,

    /// <summary>The final save ends unknown, and is not written.</summary>
    Unknown,

    /// <summary>The final save is written, then reported as not tidied up.</summary>
    Degraded,
}

/// <summary>
/// Opening a saved world as a private game: the world taken from the library, its server started in this
/// process and the client joined to it. Every member reads or writes the disk and restores a whole world: run
/// them on a worker, as <see cref="SessionLifecycle.Enter"/> does. None throws for a world that cannot be used;
/// each says why in a sentence for the player, and logs what the store said. Each gives up when its token is
/// cancelled before the disk is touched or before the server is started, leaving nothing leased behind.
/// </summary>
internal static class LocalWorlds
{
    /// <summary>
    /// Creates a world with a name, settings and spawn sector, under the default seed, and starts it. The world
    /// keeps all three for good: loading it never takes them from anywhere else.
    /// </summary>
    public static SessionStart Create(WorldLibrary library, GameContent content, string name, in WorldSettings settings, SectorCoord spawn, int autosaveSeconds, string player, CancellationToken cancel = default, StoreFault fault = StoreFault.None, ListingConfig? accountAuthority = null)
    {
        WorldSnapshot initial;
        try
        {
            initial = WorldSnapshot.NewWorld(content, Guid.NewGuid(), name, DateTimeOffset.UtcNow, new ServerConfig().GalaxySeed, settings, spawn);
        }
        catch (InvalidDataException e)
        {
            return SessionStart.Failed(e.Message);
        }

        if (cancel.IsCancellationRequested)
            return SessionStart.Cancelled;
        var created = library.Create(initial, out var folder, cancel);
        if (created.Outcome != CreateOutcome.Created)
        {
            Logs.App.Error($"Creating world '{name}' failed: {created.Outcome}: {created.Error}");
            return SessionStart.Failed("The world could not be created. Check that the folder of saved worlds can be written and has room.");
        }

        return Start(library, content, folder, Faulty(created.Store!, fault), initial, created.Generation, autosaveSeconds, player, cancel, accountAuthority);
    }

    /// <summary>Opens the world played last. One that is no longer there is reported, never made again.</summary>
    public static SessionStart Continue(WorldLibrary library, GameContent content, int autosaveSeconds, string player, CancellationToken cancel = default, StoreFault fault = StoreFault.None, ListingConfig? accountAuthority = null)
    {
        if (cancel.IsCancellationRequested)
            return SessionStart.Cancelled;
        return library.LastWorld() is { } folder ? Open(library, content, folder, autosaveSeconds, player, cancel, fault, accountAuthority) : SessionStart.Failed("No world has been played yet.");
    }

    /// <summary>Opens a world of the library by its folder id and starts it.</summary>
    public static SessionStart Open(WorldLibrary library, GameContent content, string folder, int autosaveSeconds, string player, CancellationToken cancel = default, StoreFault fault = StoreFault.None, ListingConfig? accountAuthority = null)
    {
        if (!WorldLibrary.IsFolderId(folder))
            return SessionStart.Failed(WorldLibrary.Explain(OpenOutcome.Missing));
        if (cancel.IsCancellationRequested)
            return SessionStart.Cancelled;
        var opened = library.Open(folder, SaveCompatibility.Of(content), cancel);
        if (opened.Outcome != OpenOutcome.Opened)
        {
            Logs.App.Warning($"World {folder} was not opened: {opened.Outcome}: {opened.Error}");
            return SessionStart.Failed(WorldLibrary.Explain(opened.Outcome));
        }

        if (!opened.WasCleanClose)
            Logs.App.Warning($"World {folder} was not closed cleanly last time; it goes on from its last save");
        return Start(library, content, folder, Faulty(opened.Store!, fault), opened.Snapshot!, opened.Generation, autosaveSeconds, player, cancel, accountAuthority);
    }

    /// <summary>
    /// Starts a leased store's world in a server of this process and joins it, with nothing taken from the launch:
    /// the server runs by the world's own seed, settings and spawn sector. The server owns the store from the
    /// moment it is constructed with it, also when that fails. The session's <see cref="SessionStart.Released"/>
    /// completes when the store has been disposed, whoever disposes it.
    /// </summary>
    /// <param name="store">The leased store.</param>
    /// <param name="persist">Makes the world's persistence around the store it is given (the tracking one).</param>
    /// <param name="content">The game's content.</param>
    /// <param name="player">The player's name.</param>
    internal static SessionStart Start(IWorldStore store, Func<IWorldStore, WorldPersistence> persist, GameContent content, string player, ListingConfig? accountAuthority = null)
    {
        var tracked = new ReleaseTrackingStore(store);
        ServerConfig config;
        try
        {
            var persistence = persist(tracked);
            config = new ServerConfig
            {
                Name = persistence.World.Name,
                MaxPlayers = 1,
                Visibility = ServerVisibility.Private,
                World = persistence,
                AccountAuthority = accountAuthority,
            };
        }
        catch
        {
            // Not handed over yet: the store is still this function's to release.
            tracked.Dispose();
            throw;
        }

        // The server owns the store from here, also when this throws.
        var session = GameSession.StartLocal(config, content, player);
        return new SessionStart(session, "") { Released = tracked.Released };
    }

    private static SessionStart Start(WorldLibrary library, GameContent content, string folder, IWorldStore store, WorldSnapshot world, long generation, int autosaveSeconds, string player, CancellationToken cancel, ListingConfig? accountAuthority)
    {
        // The last moment to give up cheaply: nothing runs yet, and the lease goes with the store.
        if (cancel.IsCancellationRequested)
        {
            store.Dispose();
            return SessionStart.Cancelled;
        }

        SessionStart started;
        try
        {
            started = Start(store, tracked => new WorldPersistence(tracked, world, generation) { AutosaveSeconds = Math.Clamp(autosaveSeconds, 1, 3600) }, content, player, accountAuthority);
        }
        catch (InvalidDataException e)
        {
            Logs.App.Warning($"World {folder} could not be started: {e.Message}");
            return SessionStart.Failed($"This world could not be started. {e.Message}");
        }

        library.RememberLastWorld(folder);
        return started with { Folder = folder };
    }

    private static IWorldStore Faulty(IWorldStore store, StoreFault fault) => fault == StoreFault.None ? store : new FaultyStore(store, fault);

    /// <summary>
    /// A store whose final save goes wrong on purpose, for captures of what the player is then told. Ordinary
    /// saves go through. A failed or unknown final save is not written at all, which keeps both promises: the
    /// stored world is the last completed save. A degraded one is written and then said not to be tidied up.
    /// </summary>
    private sealed class FaultyStore(IWorldStore inner, StoreFault fault) : IWorldStore
    {
        /// <inheritdoc/>
        public CommitResult Commit(WorldSnapshot snapshot, long expectedGeneration, bool final)
        {
            if (!final)
                return inner.Commit(snapshot, expectedGeneration, final);
            switch (fault)
            {
                case StoreFault.Slow:
                    Thread.Sleep(8000);
                    return inner.Commit(snapshot, expectedGeneration, final);
                case StoreFault.Fail:
                    return new CommitResult(CommitOutcome.Failed, expectedGeneration, snapshot.NextWorldTick, DateTimeOffset.UtcNow, Error: "A save fault was asked for.");
                case StoreFault.Unknown:
                    return new CommitResult(CommitOutcome.Unknown, expectedGeneration, snapshot.NextWorldTick, DateTimeOffset.UtcNow, Error: "A save fault was asked for.");
                default:
                    var result = inner.Commit(snapshot, expectedGeneration, final);
                    return result.Outcome == CommitOutcome.Committed ? result with { Checkpoint = CheckpointOutcome.Failed, Error = "A save fault was asked for." } : result;
            }
        }

        /// <inheritdoc/>
        public BackupResult Backup(BackupReason reason) => inner.Backup(reason);

        /// <inheritdoc/>
        public void Dispose() => inner.Dispose();
    }

    /// <summary>
    /// A store that says when it has been disposed, so the lifecycle knows when a world's lease is free again,
    /// also when a save worker that did not stop in time disposes it long after its server.
    /// </summary>
    private sealed class ReleaseTrackingStore(IWorldStore inner) : IWorldStore
    {
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Completes once the store has been disposed.</summary>
        public Task Released => _released.Task;

        /// <inheritdoc/>
        public CommitResult Commit(WorldSnapshot snapshot, long expectedGeneration, bool final) => inner.Commit(snapshot, expectedGeneration, final);

        /// <inheritdoc/>
        public BackupResult Backup(BackupReason reason) => inner.Backup(reason);

        /// <inheritdoc/>
        public void Dispose()
        {
            // A dispose that throws has not shown the lease to be free: the task says so instead of completing.
            try
            {
                inner.Dispose();
            }
            catch (Exception e)
            {
                _released.TrySetException(e);
                throw;
            }

            _released.TrySetResult();
        }
    }
}
