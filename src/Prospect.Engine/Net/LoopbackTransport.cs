using Prospect.Engine.Threading;

namespace Prospect.Engine.Net;

/// <summary>
/// An in-process connection between a local server and its client: two message rings, no sockets. Singleplayer is
/// a server and a client joined by one of these, running exactly the code a network game runs.
/// </summary>
public sealed class LoopbackLink : IDisposable
{
    private const int RingBytes = 1 << 22;

    private readonly MessageRing _toServer = new(RingBytes);
    private readonly MessageRing _toClient = new(RingBytes);

    public LoopbackLink()
    {
        Server = new Endpoint(_toServer, _toClient, "loopback (server end)", isClient: false);
        Client = new Endpoint(_toClient, _toServer, "loopback (client end)", isClient: true);
    }

    /// <summary>The end polled by the server thread.</summary>
    public ITransport Server { get; }

    /// <summary>The end polled by the client simulation thread.</summary>
    public ITransport Client { get; }

    /// <summary>Frees the rings. Call only after both threads have stopped using their ends.</summary>
    public void Dispose()
    {
        _toServer.Dispose();
        _toClient.Dispose();
    }

    private sealed class Endpoint : ITransport
    {
        private const byte TagConnect = 0xFE;
        private const byte TagDisconnect = 0xFF;

        private readonly MessageRing _incoming;
        private readonly MessageRing _outgoing;
        private readonly byte[] _scratch;
        private bool _connected;
        private bool _closed;

        public Endpoint(MessageRing incoming, MessageRing outgoing, string description, bool isClient)
        {
            _incoming = incoming;
            _outgoing = outgoing;
            _scratch = new byte[incoming.MaxMessageSize];
            Description = description;
            if (isClient)
                _outgoing.TryWrite(TagConnect, default);
        }

        public string Description { get; }

        public int MaxPayloadSize => _outgoing.MaxMessageSize;

        public bool IsInProcess => true;

        public bool Poll(ITransportHandler handler, ReceiveBudget budget)
        {
            var packets = 0;
            var bytes = 0L;
            while (!_closed)
            {
                if (packets >= budget.Packets || bytes >= budget.Bytes)
                    return false;
                if (!_incoming.TryRead(_scratch, out var tag, out var length))
                    break;

                packets++;
                bytes += length;
                switch (tag)
                {
                    case TagConnect:
                        if (_connected)
                            break;
                        _connected = true;
                        _outgoing.TryWrite(TagConnect, default);
                        handler.OnConnected(this, 0);
                        break;
                    case TagDisconnect:
                        _closed = true;
                        if (_connected)
                            handler.OnDisconnected(this, 0, DisconnectReason.Requested);
                        _connected = false;
                        break;
                    default:
                        if (_connected)
                            handler.OnMessage(this, 0, (NetChannel)tag, _scratch.AsSpan(0, length));
                        break;
                }
            }

            return true;
        }

        public bool Send(int peer, NetChannel channel, ReadOnlySpan<byte> payload) =>
            _connected && !_closed && _outgoing.TryWrite((byte)channel, payload);

        public void Disconnect(int peer)
        {
            if (_closed)
                return;
            _closed = true;
            _connected = false;
            _outgoing.TryWrite(TagDisconnect, default);
        }

        public int GetRoundTripMs(int peer) => 0;

        public ulong GetPeerOrigin(int peer) => 0;

        public void Dispose() => Disconnect(0);
    }
}
