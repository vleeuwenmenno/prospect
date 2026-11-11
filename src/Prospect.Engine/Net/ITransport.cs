namespace Prospect.Engine.Net;

/// <summary>How a message is delivered.</summary>
public enum NetChannel : byte
{
    /// <summary>May be lost or arrive out of order. For state that is superseded every tick: snapshots, input.</summary>
    Unreliable = 0,

    /// <summary>Arrives exactly once and in order. For events that must not be lost.</summary>
    Reliable = 1,
}

public enum DisconnectReason : byte
{
    /// <summary>The other side closed the connection deliberately.</summary>
    Requested,

    /// <summary>The connection timed out.</summary>
    ConnectionLost,

    /// <summary>The connection attempt got no usable answer.</summary>
    ConnectFailed,

    /// <summary>The server has no free player slots.</summary>
    ServerFull,
}

/// <summary>
/// How much one <see cref="ITransport.Poll"/> may take in: a number of packets and a number of bytes. Whatever is
/// still waiting when either runs out stays queued, in order, for the next poll. Nothing is dropped by it.
/// </summary>
/// <param name="Packets">Packets and connection events taken off the queue, whether or not they turn out to be for anyone.</param>
/// <param name="Bytes">Bytes of those packets.</param>
public readonly record struct ReceiveBudget(int Packets, int Bytes)
{
    /// <summary>No limit: the poll returns only when nothing is waiting.</summary>
    public static ReceiveBudget Unlimited => new(int.MaxValue, int.MaxValue);
}

/// <summary>Receives what a transport produced during <see cref="ITransport.Poll"/>.</summary>
public interface ITransportHandler
{
    void OnConnected(ITransport transport, int peer);
    void OnDisconnected(ITransport transport, int peer, DisconnectReason reason);
    void OnMessage(ITransport transport, int peer, NetChannel channel, ReadOnlySpan<byte> payload);
}

/// <summary>
/// A message pipe to one or more peers. The simulation talks only to this interface, so a local game (in-process
/// queues) and a network game (RakNet) run the same code. A transport is used from a single thread.
/// </summary>
public interface ITransport : IDisposable
{
    /// <summary>Human-readable endpoint, for logs.</summary>
    string Description { get; }

    /// <summary>The largest payload <see cref="Send"/> accepts.</summary>
    int MaxPayloadSize { get; }

    /// <summary>
    /// True when the other end is in this process: the link between a local game's server and its own client.
    /// Nothing from outside the process can arrive on such a transport.
    /// </summary>
    bool IsInProcess { get; }

    /// <summary>
    /// Delivers pending connection events and messages to the handler, oldest first, until nothing is waiting or
    /// <paramref name="budget"/> is used up. Returns false when the budget ran out before the queue was seen
    /// empty: the rest is delivered by later polls.
    /// </summary>
    bool Poll(ITransportHandler handler, ReceiveBudget budget);

    /// <summary>Queues a message. Returns false if the peer is gone or the transport cannot take it.</summary>
    bool Send(int peer, NetChannel channel, ReadOnlySpan<byte> payload);

    /// <summary>Closes the connection to a peer. No further events are raised for it.</summary>
    void Disconnect(int peer);

    /// <summary>Smoothed round-trip time to the peer in milliseconds, or -1 if unknown.</summary>
    int GetRoundTripMs(int peer);

    /// <summary>
    /// A number that is the same for every connection from one network address and differs between addresses, so
    /// limits can be counted per address. Zero when the peer is in this process or its address is not known.
    /// </summary>
    ulong GetPeerOrigin(int peer);
}

/// <summary>Shorthands for <see cref="ITransport"/>.</summary>
public static class TransportExtensions
{
    /// <summary>Delivers everything that is waiting. For the ends that trust what they are connected to.</summary>
    public static void Poll(this ITransport transport, ITransportHandler handler) => transport.Poll(handler, ReceiveBudget.Unlimited);
}
