using Prospect.Engine.Net;
using Prospect.Engine.Time;
using Prospect.Game.Protocol;
using Prospect.Game.Server;
using Prospect.Game.World;

namespace Prospect.Tests;

/// <summary>A clock a test moves by hand, in units of its own: <see cref="Frequency"/> of them are one second.</summary>
internal sealed class ManualTime : TimeProvider
{
    /// <summary>Chosen so that one simulation tick is a whole number of units.</summary>
    public const long Frequency = 60_000;

    public const long Tick = Frequency / TickLoop.SimulationHz;

    private long _now = Frequency;

    public override long TimestampFrequency => Frequency;

    public override long GetTimestamp() => _now;

    public void Advance(long units) => _now += units;

    public void AdvanceSeconds(double seconds) => _now += (long)(seconds * Frequency);
}

/// <summary>
/// A network transport a test plays the other side of: connections from any address it likes, exactly the bytes
/// it wants, in the order it wants, and a record of what the server sent back and whom it closed. It is used from
/// one thread, with the server ticked by hand, so a test is the same every run and can count what a tick allocates:
/// nothing here allocates after construction.
/// </summary>
internal sealed class ScriptedTransport : ITransport
{
    /// <summary>What the transport remembers of one connection.</summary>
    public struct PeerRecord
    {
        public ulong Origin;

        /// <summary>False once either side has closed the connection.</summary>
        public bool Open;

        /// <summary>True when it was the server that closed it.</summary>
        public bool ClosedByServer;
        public int Welcomes;
        public int Challenges;
        public int Pongs;
        public int Snapshots;

        /// <summary>The last input the server's latest snapshot says it has simulated.</summary>
        public uint AckedInput;
        public int Rejects;
        public RejectReason Reject;
        public int RetryAfterSeconds;
    }

    private const byte KindConnect = 0;
    private const byte KindMessage = 1;
    private const byte KindHangUp = 2;

    private struct Event
    {
        public byte Kind;
        public int Peer;
        public NetChannel Channel;
        public byte[] Payload;
        public int Length;
    }

    private readonly Queue<Event> _events = new(1 << 17);
    private readonly PeerRecord[] _peers;
    private const int ChallengeBytes = Passwords.SaltSize + Passwords.ChallengeSize;
    private readonly byte[] _challenges;
    private int _count;

    public ScriptedTransport(int capacity = 1 << 16)
    {
        _peers = new PeerRecord[capacity];
        _challenges = new byte[capacity * ChallengeBytes];
    }

    internal delegate void SnapshotSink(int peer, ReadOnlySpan<byte> payload);

    /// <summary>Optional observer of the exact snapshot bytes sent by the server.</summary>
    internal SnapshotSink? ObserveSnapshot { get; set; }

    public string Description => "scripted";

    public int MaxPayloadSize => 1 << 20;

    public bool IsInProcess => false;

    /// <summary>Connections opened so far.</summary>
    public int Count => _count;

    /// <summary>Events and packets handed to the server so far, and those thrown away because their sender was closed.</summary>
    public long Delivered { get; private set; }

    public ref PeerRecord this[int peer] => ref _peers[peer];

    /// <summary>The salt and challenge the server last sent a peer.</summary>
    public (byte[] Salt, byte[] Challenge) ChallengeOf(int peer)
    {
        var bytes = _challenges.AsSpan(peer * ChallengeBytes, ChallengeBytes);
        return (bytes[..Passwords.SaltSize].ToArray(), bytes[Passwords.SaltSize..].ToArray());
    }

    /// <summary>A new connection from an address; the server hears of it in its next poll.</summary>
    public int Connect(ulong origin)
    {
        var peer = _count++;
        _peers[peer] = new PeerRecord { Origin = origin, Open = true };
        _events.Enqueue(new Event { Kind = KindConnect, Peer = peer });
        return peer;
    }

    /// <summary>Sends the server the first <paramref name="length"/> bytes of an array, which is not copied.</summary>
    public void Deliver(int peer, byte[] payload, int length, NetChannel channel = NetChannel.Reliable) =>
        _events.Enqueue(
            new Event
            {
                Kind = KindMessage,
                Peer = peer,
                Channel = channel,
                Payload = payload,
                Length = length,
            }
        );

    public void Deliver(int peer, byte[] payload, NetChannel channel = NetChannel.Reliable) => Deliver(peer, payload, payload.Length, channel);

    /// <summary>The other end closes the connection.</summary>
    public void HangUp(int peer) => _events.Enqueue(new Event { Kind = KindHangUp, Peer = peer });

    /// <summary>How many of the connections an address opened are still open.</summary>
    public int OpenFrom(ulong origin)
    {
        var open = 0;
        for (var i = 0; i < _count; i++)
        {
            if (_peers[i].Open && _peers[i].Origin == origin)
                open++;
        }

        return open;
    }

    public bool Poll(ITransportHandler handler, ReceiveBudget budget)
    {
        var packets = 0;
        var bytes = 0L;
        while (_events.Count > 0)
        {
            if (packets >= budget.Packets || bytes >= budget.Bytes)
                return false;

            var e = _events.Dequeue();
            packets++;
            bytes += e.Length;
            Delivered++;
            ref var peer = ref _peers[e.Peer];
            switch (e.Kind)
            {
                case KindConnect:
                    handler.OnConnected(this, e.Peer);
                    break;
                case KindHangUp when peer.Open:
                    peer.Open = false;
                    handler.OnDisconnected(this, e.Peer, DisconnectReason.Requested);
                    break;

                // As a real transport does: what a closed connection still had on its way is thrown away.
                case KindMessage when peer.Open:
                    handler.OnMessage(this, e.Peer, e.Channel, e.Payload.AsSpan(0, e.Length));
                    break;
            }
        }

        return true;
    }

    public bool Send(int peer, NetChannel channel, ReadOnlySpan<byte> payload)
    {
        ref var record = ref _peers[peer];
        if (!record.Open || payload.IsEmpty)
            return false;

        var reader = new NetReader(payload[1..]);
        switch ((MessageId)payload[0])
        {
            case MessageId.Welcome:
                record.Welcomes++;
                break;
            case MessageId.Pong:
                record.Pongs++;
                break;
            case MessageId.Challenge:
                record.Challenges++;
                payload.Slice(1, ChallengeBytes).CopyTo(_challenges.AsSpan(peer * ChallengeBytes));
                break;
            case MessageId.Snapshot when SnapshotHeader.TryRead(ref reader, out var header):
                record.Snapshots++;
                record.AckedInput = header.AckedInput;
                ObserveSnapshot?.Invoke(peer, payload);
                break;
            case MessageId.Reject when RejectMessage.TryRead(ref reader, out var reject):
                record.Rejects++;
                record.Reject = reject.Reason;
                record.RetryAfterSeconds = reject.RetryAfterSeconds;
                break;
        }

        return true;
    }

    public void Disconnect(int peer)
    {
        ref var record = ref _peers[peer];
        if (!record.Open)
            return;
        record.Open = false;
        record.ClosedByServer = true;
    }

    public int GetRoundTripMs(int peer) => 0;

    public ulong GetPeerOrigin(int peer) => _peers[peer].Open ? _peers[peer].Origin : 0;

    public void Dispose() { }
}

/// <summary>
/// A server that is not started: the test calls its tick itself, with a clock it moves and a network it plays.
/// One tick of the rig is one sixtieth of a second on that clock unless the test says otherwise.
/// </summary>
internal sealed class ScriptedServer : IDisposable
{
    private readonly byte[] _inputBuffer = new byte[256];
    private readonly byte[] _pingBuffer = new byte[16];
    private long _tick;

    public ScriptedServer(GameContent content, int maxPlayers = 8, string? password = null, ConnectionLimits? limits = null)
    {
        Server = new ServerHost(
            new ServerConfig
            {
                Name = "Scripted",
                MaxPlayers = maxPlayers,
                Password = password,
                Limits = limits ?? ConnectionLimits.Default,
                Clock = Clock,
            },
            content,
            Net
        );
    }

    public ManualTime Clock { get; } = new();

    public ScriptedTransport Net { get; } = new();

    public ServerHost Server { get; }

    public void Dispose() => Server.Dispose();

    /// <summary>Runs ticks, the clock moving one tick's time before each.</summary>
    public void Tick(int count = 1)
    {
        for (var i = 0; i < count; i++)
        {
            Clock.Advance(ManualTime.Tick);
            ((ITickHandler)Server).Tick(_tick++);
        }
    }

    /// <summary>
    /// A hello as a client of this build sends it: with the server's own content, unless the test says the
    /// client has <paramref name="content"/>.
    /// </summary>
    public void Hello(int peer, string name = "Scripted", uint version = ProtocolInfo.Version, GameContent? content = null)
    {
        var bytes = new byte[HelloMessage.MaxWireSize];
        var writer = new NetWriter(bytes);
        new HelloMessage { ProtocolVersion = version, PlayerName = name, Content = (content ?? Server.Content).Identity }.Write(ref writer);
        Net.Deliver(peer, bytes, writer.Length);
    }

    /// <summary>The proof for the challenge the server sent a peer, made from a password.</summary>
    public void Proof(int peer, string password)
    {
        var (salt, challenge) = Net.ChallengeOf(peer);
        var key = new byte[Passwords.KeySize];
        var proof = new byte[Passwords.ProofSize];
        Passwords.DeriveKey(password, salt, key);
        Passwords.ComputeProof(key, salt, challenge, proof);
        var bytes = new byte[ProofMessage.WireSize];
        var writer = new NetWriter(bytes);
        ProofMessage.Write(ref writer, proof);
        Net.Deliver(peer, bytes);
    }

    /// <summary>Connects from an address and goes through the whole handshake. Returns the peer.</summary>
    public int Join(ulong origin, string? password = null, string name = "Scripted", GameContent? content = null)
    {
        var peer = Net.Connect(origin);
        Hello(peer, name, content: content);
        // This fixture already owns Server.Content, so its canonical catalogue is a complete cache hit.
        if (password is null) Net.Deliver(peer, [(byte)MessageId.CatalogueReady]);
        Tick();
        if (password is not null && Net[peer].Challenges == 1)
        {
            Proof(peer, password);
            Net.Deliver(peer, [(byte)MessageId.CatalogueReady]);
            Tick();
        }

        return peer;
    }

    /// <summary>One input from a player, with nobody at the controls. Allocates nothing.</summary>
    public void Input(int peer, uint sequence)
    {
        Span<InputRecord> records = [new InputRecord { Sequence = sequence, Input = new FlightInput { Dampeners = true } }];
        var writer = new NetWriter(_inputBuffer);
        InputMessage.Write(ref writer, records, 0, default);
        Net.Deliver(peer, _inputBuffer, writer.Length, NetChannel.Unreliable);
    }

    /// <summary>One clock probe from a player, which the server answers with a pong. Allocates nothing.</summary>
    public void Ping(int peer)
    {
        var writer = new NetWriter(_pingBuffer);
        new PingMessage { ClientTimestamp = 1 }.Write(ref writer);
        Net.Deliver(peer, _pingBuffer, writer.Length, NetChannel.Unreliable);
    }
}
