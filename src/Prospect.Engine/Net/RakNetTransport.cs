using System.Diagnostics;
using Brutal.RakNetApi;

namespace Prospect.Engine.Net;

/// <summary>A transport over UDP using BRUTAL's RakNet binding. Either listens for peers or connects to one.</summary>
public sealed unsafe class RakNetTransport : ITransport
{
    private const int HeaderSize = 2;
    private const int MaxPayload = 1 << 20;

    private struct Peer
    {
        public bool Active;
        public RakNetGUID Guid;
        public SystemAddress Address;
    }

    private readonly RakPeerInstance _rak;
    private readonly Peer[] _peers;
    private readonly bool _isServer;
    private byte[] _scratch = new byte[2048];
    private bool _disposed;

    // A client's server, kept to ask again after being told to wait; and when to ask, zero for not at all.
    private readonly byte[]? _hostUtf8;
    private readonly ushort _hostPort;
    private ConnectRetry _retry;

    private RakNetTransport(RakPeerInstance rak, int maxPeers, bool isServer, string description, byte[]? hostUtf8 = null, ushort hostPort = 0)
    {
        _rak = rak;
        _peers = new Peer[maxPeers];
        _isServer = isServer;
        _hostUtf8 = hostUtf8;
        _hostPort = hostPort;
        Description = description;
    }

    public string Description { get; }

    public int MaxPayloadSize => MaxPayload;

    public bool IsInProcess => false;

    /// <summary>
    /// False: BRUTAL's RakNet binding only carries IPv4 addresses, so this transport neither listens on nor
    /// connects to IPv6.
    /// </summary>
    public static bool SupportsIpv6 => false;

    /// <summary>
    /// Starts listening for clients on a UDP port. <paramref name="maxPeers"/> is how many connections the
    /// caller will hold open at once.
    ///
    /// <paramref name="halfOpenSlots"/> is room on top of that for connections RakNet itself is holding: it
    /// gives a slot to anyone who sends the opening datagram of its handshake, before this class or its caller
    /// hears of it, and keeps the slot for up to ten seconds if nothing follows; a connection being closed
    /// keeps its slot until the other end has acknowledged. With every slot taken RakNet turns newcomers away.
    /// It starts at most one connection per address every 100 ms (the loopback address excepted), so one address
    /// can hold about a hundred such slots and no more; an attacker who sends from many addresses, or forges
    /// them, is not held back by that.
    /// </summary>
    public static RakNetTransport Listen(ushort port, int maxPeers, int halfOpenSlots = 0)
    {
        var slots = Math.Min(maxPeers + Math.Max(halfOpenSlots, 0), ushort.MaxValue);
        var rak = RakNetLibrary.CreateInstance();
        var socket = new SocketDescriptor(port);
        var result = rak.Startup((uint)slots, new Span<SocketDescriptor>(ref socket));
        if (result != StartupResult.RaknetStarted)
        {
            rak.Dispose();
            throw new IOException($"Cannot listen on UDP port {port}: {result}");
        }

        rak.SetMaximumIncomingConnections((ushort)slots);
        rak.SetLimitIPConnectionFrequency(true);
        rak.SetOccasionalPing(true);
        return new RakNetTransport(rak, maxPeers, true, $"udp listen :{port}");
    }

    /// <summary>
    /// Starts connecting to a server. The outcome arrives through <see cref="Poll"/> as a connected or disconnected
    /// event for peer 0.
    /// </summary>
    public static RakNetTransport Connect(string host, ushort port)
    {
        var rak = RakNetLibrary.CreateInstance();
        var socket = new SocketDescriptor(0);
        var result = rak.Startup(1, new Span<SocketDescriptor>(ref socket));
        if (result != StartupResult.RaknetStarted)
        {
            rak.Dispose();
            throw new IOException($"Cannot open a UDP socket: {result}");
        }

        rak.SetOccasionalPing(true);

        var hostUtf8 = System.Text.Encoding.UTF8.GetBytes(host + '\0');
        var attempt = rak.Connect(hostUtf8, port, default, null);
        if (attempt != ConnectionAttemptResult.ConnectionAttemptStarted)
        {
            rak.Shutdown(0);
            rak.Dispose();
            throw new IOException($"Cannot connect to {host}:{port}: {attempt}");
        }

        return new RakNetTransport(rak, 1, false, $"udp {host}:{port}", hostUtf8, port);
    }

    /// <summary>
    /// Adds artificial packet loss and latency to everything this transport sends, to test play under bad network
    /// conditions. Loss is a fraction from 0 to 1; latency is added per direction.
    /// </summary>
    public void SimulateConditions(float packetLoss, int extraLatencyMs, int latencyVarianceMs) =>
        _rak.ApplyNetworkSimulator(packetLoss, (ushort)extraLatencyMs, (ushort)latencyVarianceMs);

    public bool Poll(ITransportHandler handler, ReceiveBudget budget)
    {
        if (_retry.TakeDue(Stopwatch.GetTimestamp()))
        {
            if (_rak.Connect(_hostUtf8, _hostPort, default, null) != ConnectionAttemptResult.ConnectionAttemptStarted)
                handler.OnDisconnected(this, 0, DisconnectReason.ConnectFailed);
        }

        // Everything taken off RakNet's queue counts, also what turns out to be for nobody: the limit is on the
        // work one poll can be made to do, and a packet that is thrown away was still looked at.
        var packets = 0;
        var bytes = 0L;
        while (true)
        {
            if (packets >= budget.Packets || bytes >= budget.Bytes)
                return false;

            var packet = _rak.Receive();
            if (packet == null)
                return true;

            packets++;
            bytes += packet->Length;
            try
            {
                if (packet->Length > 0)
                    Handle(handler, packet);
            }
            finally
            {
                _rak.DeallocatePacket(packet);
            }
        }
    }

    public bool Send(int peer, NetChannel channel, ReadOnlySpan<byte> payload)
    {
        if ((uint)peer >= (uint)_peers.Length || !_peers[peer].Active || payload.Length > MaxPayload)
            return false;

        var size = HeaderSize + payload.Length;
        if (_scratch.Length < size)
            _scratch = new byte[Math.Max(size, _scratch.Length * 2)];

        _scratch[0] = (byte)DefaultMessageIDTypes.UserPacketEnum;
        _scratch[1] = (byte)channel;
        payload.CopyTo(_scratch.AsSpan(HeaderSize));

        var reliable = channel == NetChannel.Reliable;
        var receipt = _rak.Send(
            _scratch.AsSpan(0, size),
            reliable ? PacketPriority.HighPriority : PacketPriority.ImmediatePriority,
            reliable ? PacketReliability.ReliableOrdered : PacketReliability.Unreliable,
            0,
            new AddressOrGUID(_peers[peer].Guid),
            false
        );
        return receipt != 0;
    }

    public void Disconnect(int peer)
    {
        if ((uint)peer >= (uint)_peers.Length || !_peers[peer].Active)
            return;
        _rak.CloseConnection(new AddressOrGUID(_peers[peer].Guid), true);
        _peers[peer].Active = false;
    }

    public int GetRoundTripMs(int peer)
    {
        if ((uint)peer >= (uint)_peers.Length || !_peers[peer].Active)
            return -1;
        return _rak.GetAveragePing(new AddressOrGUID(_peers[peer].Guid));
    }

    public ulong GetPeerOrigin(int peer)
    {
        if ((uint)peer >= (uint)_peers.Length || !_peers[peer].Active)
            return 0;

        // The binding only knows IPv4: the four address bytes, under a marker bit so no address reads as zero.
        ref readonly var bytes = ref _peers[peer].Address.Address.Addr4.SinAddr.SUn.SUnB;
        return 1UL << 32 | (ulong)bytes.SB1 << 24 | (ulong)bytes.SB2 << 16 | (ulong)bytes.SB3 << 8 | bytes.SB4;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        // Give queued disconnect notifications a moment to leave so peers see a clean close, not a timeout.
        _rak.Shutdown(200);
        _rak.Dispose();
    }

    private void Handle(ITransportHandler handler, Packet* packet)
    {
        var id = (DefaultMessageIDTypes)packet->Data[0];
        switch (id)
        {
            case DefaultMessageIDTypes.NewIncomingConnection when _isServer:
            case DefaultMessageIDTypes.ConnectionRequestAccepted when !_isServer:
                {
                    _retry.Cancel();
                    var peer = Allocate(packet->Guid, packet->SystemAddress);
                    if (peer < 0)
                        _rak.CloseConnection(new AddressOrGUID(packet->Guid), true);
                    else
                        handler.OnConnected(this, peer);
                    break;
                }
            case DefaultMessageIDTypes.DisconnectionNotification:
                Drop(handler, packet->Guid, DisconnectReason.Requested);
                break;
            case DefaultMessageIDTypes.ConnectionLost:
                Drop(handler, packet->Guid, DisconnectReason.ConnectionLost);
                break;
            case DefaultMessageIDTypes.NoFreeIncomingConnections when !_isServer:
                _retry.Cancel();
                handler.OnDisconnected(this, 0, DisconnectReason.ServerFull);
                break;
            case DefaultMessageIDTypes.IpRecentlyConnected when !_isServer:
                // Someone at this address connected within the last 100 ms. Ask again shortly, a little later
                // each time and never at the same moment as a neighbour who was told the same.
                if (!_retry.Schedule(Stopwatch.GetTimestamp(), Stopwatch.Frequency, Random.Shared.Next(120)))
                    handler.OnDisconnected(this, 0, DisconnectReason.ConnectFailed);

                break;
            case DefaultMessageIDTypes.ConnectionAttemptFailed when !_isServer:
            case DefaultMessageIDTypes.ConnectionBanned when !_isServer:
            case DefaultMessageIDTypes.InvalidPassword when !_isServer:
            case DefaultMessageIDTypes.IncompatibleProtocolVersion when !_isServer:
                _retry.Cancel();
                handler.OnDisconnected(this, 0, DisconnectReason.ConnectFailed);
                break;
            case DefaultMessageIDTypes.UserPacketEnum when packet->Length >= HeaderSize:
                {
                    var peer = Find(packet->Guid);
                    var channel = packet->Data[1];
                    if (peer >= 0 && channel <= (byte)NetChannel.Reliable)
                        handler.OnMessage(
                            this,
                            peer,
                            (NetChannel)channel,
                            new ReadOnlySpan<byte>(packet->Data + HeaderSize, (int)packet->Length - HeaderSize)
                        );
                    break;
                }
        }
    }

    private void Drop(ITransportHandler handler, RakNetGUID guid, DisconnectReason reason)
    {
        var peer = Find(guid);
        if (peer < 0)
            return;
        _peers[peer].Active = false;
        handler.OnDisconnected(this, peer, reason);
    }

    private int Allocate(RakNetGUID guid, SystemAddress address)
    {
        for (var i = 0; i < _peers.Length; i++)
        {
            if (_peers[i].Active)
                continue;
            _peers[i] = new Peer
            {
                Active = true,
                Guid = guid,
                Address = address,
            };
            return i;
        }

        return -1;
    }

    private int Find(RakNetGUID guid)
    {
        for (var i = 0; i < _peers.Length; i++)
        {
            if (_peers[i].Active && _peers[i].Guid == guid)
                return i;
        }

        return -1;
    }
}
