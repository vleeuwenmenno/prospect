using Prospect.Engine.Diagnostics;
using Prospect.Engine.Net;
using Prospect.Engine.Time;
using Prospect.Game.Protocol;

namespace Prospect.Game.Server;

public sealed class ServerConfig
{
    public string Name { get; init; } = "PROSPECT server";
    public int MaxPlayers { get; init; } = 16;
    public ulong GalaxySeed { get; init; } = 0x50524F5350454354;
}

/// <summary>A point-in-time view of the server, readable from any thread.</summary>
public readonly record struct ServerStatus(long Tick, int PlayerCount, TickStats Loop);

/// <summary>
/// The authoritative simulation. The dedicated server binary and a singleplayer game both run this class on its
/// own thread; the only difference is which transports it is given. Everything in here executes on the tick thread.
/// </summary>
public sealed class ServerHost : ITickHandler, ITransportHandler, IDisposable
{
    /// <summary>A connection that has not said hello within this many ticks is dropped.</summary>
    private const int HandshakeTimeoutTicks = TickLoop.SimulationHz * 5;

    private enum ConnectionState : byte
    {
        Free,
        AwaitingHello,
        Active,
    }

    private struct Connection
    {
        public ConnectionState State;
        public ITransport Transport;
        public int Peer;
        public uint PlayerId;
        public string Name;
        public long ConnectedTick;
    }

    private readonly ServerConfig _config;
    private readonly ITransport[] _transports;
    private readonly Connection[] _connections;
    private readonly TickLoop _loop;
    private readonly byte[] _sendBuffer = new byte[1024];

    private long _tick;
    private uint _nextPlayerId = 1;
    private int _playerCount;

    public ServerHost(ServerConfig config, params ITransport[] transports)
    {
        _config = config;
        _transports = transports;
        // Room for every player plus connections that are still shaking hands or about to be refused as full.
        _connections = new Connection[config.MaxPlayers + 8];
        _loop = new TickLoop("Server", TickLoop.SimulationHz, this);
    }

    public ServerStatus Status => new(Volatile.Read(ref _tick), Volatile.Read(ref _playerCount), _loop.Stats);

    /// <summary>The exception that stopped the server, if a tick threw.</summary>
    public Exception? Fault => _loop.Fault;

    public bool IsRunning => _loop.IsRunning;

    public void Start() => _loop.Start();

    /// <summary>Stops the tick thread and closes every transport.</summary>
    public void Dispose()
    {
        _loop.Stop();
        foreach (var transport in _transports)
            transport.Dispose();
    }

    void ITickHandler.OnStart()
    {
        foreach (var transport in _transports)
            Logs.Sim.Info($"Server '{_config.Name}' up on {transport.Description}, {_config.MaxPlayers} player slots");
    }

    void ITickHandler.Tick(long tick)
    {
        foreach (var transport in _transports)
            transport.Poll(this);

        for (var i = 0; i < _connections.Length; i++)
        {
            ref var connection = ref _connections[i];
            if (connection.State == ConnectionState.AwaitingHello && tick - connection.ConnectedTick > HandshakeTimeoutTicks)
                Refuse(ref connection, RejectReason.BadHandshake);
        }

        Volatile.Write(ref _tick, tick + 1);
    }

    void ITickHandler.OnStop()
    {
        for (var i = 0; i < _connections.Length; i++)
        {
            ref var connection = ref _connections[i];
            if (connection.State != ConnectionState.Free)
                connection.Transport.Disconnect(connection.Peer);
            connection = default;
        }

        Volatile.Write(ref _playerCount, 0);
        Logs.Sim.Info($"Server '{_config.Name}' stopped at tick {_tick}");
    }

    void ITransportHandler.OnConnected(ITransport transport, int peer)
    {
        for (var i = 0; i < _connections.Length; i++)
        {
            ref var connection = ref _connections[i];
            if (connection.State != ConnectionState.Free)
                continue;

            connection = new Connection
            {
                State = ConnectionState.AwaitingHello,
                Transport = transport,
                Peer = peer,
                Name = "",
                ConnectedTick = _tick,
            };
            return;
        }

        transport.Disconnect(peer);
    }

    void ITransportHandler.OnDisconnected(ITransport transport, int peer, DisconnectReason reason)
    {
        var index = Find(transport, peer);
        if (index < 0)
            return;

        ref var connection = ref _connections[index];
        if (connection.State == ConnectionState.Active)
        {
            Volatile.Write(ref _playerCount, _playerCount - 1);
            Logs.Sim.Info($"{connection.Name} (player {connection.PlayerId}) left: {reason}");
        }

        connection = default;
    }

    void ITransportHandler.OnMessage(ITransport transport, int peer, NetChannel channel, ReadOnlySpan<byte> payload)
    {
        var index = Find(transport, peer);
        if (index < 0 || payload.IsEmpty)
            return;

        ref var connection = ref _connections[index];
        var reader = new NetReader(payload[1..]);
        var id = (MessageId)payload[0];

        if (connection.State == ConnectionState.AwaitingHello)
        {
            if (id == MessageId.Hello && channel == NetChannel.Reliable && HelloMessage.TryRead(ref reader, out var hello))
                Admit(ref connection, hello);
            else
                Refuse(ref connection, RejectReason.BadHandshake);
            return;
        }

        switch (id)
        {
            case MessageId.Ping when PingMessage.TryRead(ref reader, out var ping):
                {
                    var pong = new PongMessage
                    {
                        ClientTimestamp = ping.ClientTimestamp,
                        ServerTick = _tick,
                        PlayerCount = (ushort)_playerCount,
                    };
                    var writer = new NetWriter(_sendBuffer);
                    pong.Write(ref writer);
                    transport.Send(peer, NetChannel.Unreliable, writer.Written);
                    break;
                }
        }
    }

    private void Admit(ref Connection connection, in HelloMessage hello)
    {
        if (hello.ProtocolVersion != ProtocolInfo.Version)
        {
            Refuse(ref connection, RejectReason.VersionMismatch);
            return;
        }

        if (_playerCount >= _config.MaxPlayers)
        {
            Refuse(ref connection, RejectReason.ServerFull);
            return;
        }

        connection.State = ConnectionState.Active;
        connection.PlayerId = _nextPlayerId++;
        connection.Name = hello.PlayerName;
        Volatile.Write(ref _playerCount, _playerCount + 1);

        var welcome = new WelcomeMessage
        {
            PlayerId = connection.PlayerId,
            TickRate = TickLoop.SimulationHz,
            ServerTick = _tick,
            GalaxySeed = _config.GalaxySeed,
            ServerName = _config.Name,
        };
        var writer = new NetWriter(_sendBuffer);
        welcome.Write(ref writer);
        connection.Transport.Send(connection.Peer, NetChannel.Reliable, writer.Written);

        Logs.Sim.Info($"{connection.Name} joined as player {connection.PlayerId} via {connection.Transport.Description}");
    }

    private void Refuse(ref Connection connection, RejectReason reason)
    {
        var writer = new NetWriter(_sendBuffer);
        writer.WriteByte((byte)MessageId.Reject);
        writer.WriteByte((byte)reason);
        connection.Transport.Send(connection.Peer, NetChannel.Reliable, writer.Written);
        connection.Transport.Disconnect(connection.Peer);
        Logs.Sim.Info($"Refused a connection on {connection.Transport.Description}: {reason}");
        connection = default;
    }

    private int Find(ITransport transport, int peer)
    {
        for (var i = 0; i < _connections.Length; i++)
        {
            ref var connection = ref _connections[i];
            if (connection.State != ConnectionState.Free && connection.Peer == peer && ReferenceEquals(connection.Transport, transport))
                return i;
        }

        return -1;
    }
}
