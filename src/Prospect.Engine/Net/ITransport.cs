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

    /// <summary>Delivers pending connection events and messages to the handler.</summary>
    void Poll(ITransportHandler handler);

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
