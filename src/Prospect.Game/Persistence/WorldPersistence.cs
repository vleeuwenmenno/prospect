namespace Prospect.Game.Persistence;

/// <summary>
/// A saved world as a server is given it: the leased store, the state to begin from and the generation that
/// state is stored under, as a store's <c>Open</c> or <c>Create</c> returned them. The server owns the store
/// from the moment it is constructed with this, also when its construction fails.
/// </summary>
public sealed class WorldPersistence
{
    /// <summary>A world to run. For a new world <paramref name="world"/> is the snapshot the store was created with.</summary>
    public WorldPersistence(IWorldStore store, WorldSnapshot world, long generation)
    {
        Store = store;
        World = world;
        Generation = generation;
    }

    /// <summary>The store, which only the server's save worker uses from here on.</summary>
    public IWorldStore Store { get; }

    /// <summary>The state the server restores. It is checked against the server's content before anything runs.</summary>
    public WorldSnapshot World { get; }

    /// <summary>The generation <see cref="World"/> is stored under.</summary>
    public long Generation { get; }

    /// <summary>
    /// Seconds between automatic saves while the world has changed, from 1 to 3600. A save that is still being
    /// written when the next is due is not queued behind: the next one is made when the buffer is free again.
    /// </summary>
    public int AutosaveSeconds { get; init; } = 30;

    /// <summary>The bounds of the world and of the capture buffer; a test builds a smaller one.</summary>
    internal SaveLimits Limits { get; init; } = SaveLimits.Default;

    /// <summary>Seconds before a save that failed is tried again, with the state the world has by then.</summary>
    internal double RetrySeconds { get; init; } = 5;

    /// <summary>How often the final save is tried, and the pause between two tries. The frozen state does not change, so the same capture is committed.</summary>
    internal int FinalAttempts { get; init; } = 3;
    internal int FinalRetryMilliseconds { get; init; } = 250;

    /// <summary>Seconds between recovery copies.</summary>
    internal double BackupSeconds { get; init; } = 600;

    /// <summary>How long disposing the server waits for the save worker before it leaves it behind.</summary>
    internal int StopTimeoutMilliseconds { get; init; } = 15000;
}
