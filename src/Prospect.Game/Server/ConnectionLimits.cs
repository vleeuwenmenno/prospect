namespace Prospect.Game.Server;

/// <summary>
/// How many connections a server lets stand at its door, and how often one address may knock. A connection is
/// <b>pending</b> from the moment the network layer reports it until the player is let in or turned away; it
/// holds a slot of its own for that time, never one of the player slots.
///
/// The defaults let a household or a LAN party behind one address join together, and leave most of the door
/// free while one address misbehaves. A server's settings file can change them under <c>"server"</c>,
/// <c>"limits"</c>; see <c>docs/hosting.md</c>. The in-process link of a local game's host is not counted.
/// </summary>
public sealed record ConnectionLimits
{
    /// <summary>The limits a server has unless its settings say otherwise.</summary>
    public static ConnectionLimits Default { get; } = new();

    /// <summary>Connections from one address that may be pending at once. One more is turned away at once.</summary>
    public int PendingPerAddress { get; init; } = 8;

    /// <summary>Connections that may be pending at once, from all addresses together.</summary>
    public int PendingTotal { get; init; } = 32;

    /// <summary>Connections one address may open in a burst before it has to wait for <see cref="ConnectionsPerSecond"/>.</summary>
    public int ConnectionBurst { get; init; } = 20;

    /// <summary>How fast an address earns another connection once its burst is spent.</summary>
    public double ConnectionsPerSecond { get; init; } = 1;

    /// <summary>
    /// Seconds a connection has, from the moment it is made, to get through the whole handshake. Measured by the
    /// clock, not in ticks: a server that is running slow does not give anyone longer.
    /// </summary>
    public double HandshakeSeconds { get; init; } = 5;

    /// <summary>
    /// Throws <see cref="InvalidDataException"/>, saying which and why, when a limit is one a server could not
    /// run with.
    /// </summary>
    public void Validate()
    {
        if (PendingTotal is < 1 or > 1024)
            throw new InvalidDataException("limits.pendingTotal must be a whole number from 1 to 1024");
        if (PendingPerAddress < 1 || PendingPerAddress > PendingTotal)
            throw new InvalidDataException("limits.pendingPerAddress must be a whole number from 1 to limits.pendingTotal");
        if (ConnectionBurst is < 1 or > 10_000)
            throw new InvalidDataException("limits.connectionBurst must be a whole number from 1 to 10000");
        if (!(ConnectionsPerSecond >= 0.01 && ConnectionsPerSecond <= 1000))
            throw new InvalidDataException("limits.connectionsPerSecond must be a number from 0.01 to 1000");
        if (!(HandshakeSeconds >= 1 && HandshakeSeconds <= 60))
            throw new InvalidDataException("limits.handshakeSeconds must be a number from 1 to 60");
    }
}
