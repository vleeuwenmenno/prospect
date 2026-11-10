using Prospect.Engine.Diagnostics;
using Prospect.Engine.Net;
using Prospect.Engine.Time;
using Prospect.Game.Protocol;
using Prospect.Game.World;

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

    /// <summary>Inputs a client may have waiting; more than this means it is running ahead and the oldest are dropped.</summary>
    private const int InputQueueSize = 16;

    /// <summary>
    /// Inputs a client may have simulated in one server tick. One per tick on average, with room to absorb a late
    /// packet followed by a burst; a client sending faster than real time gains nothing.
    /// </summary>
    private const int MaxInputCredit = 4;

    /// <summary>After this many ticks without input the server flies the hull itself until input resumes.</summary>
    private const int InputStarvedTicks = 15;

    /// <summary>Other players' hulls are sent every this many ticks; a client's own hull goes every tick.</summary>
    private const int RemoteHullInterval = 3;

    private enum ConnectionState : byte
    {
        Free,
        AwaitingHello,
        Active,
    }

    private sealed class Connection
    {
        public ConnectionState State;
        public ITransport Transport = null!;
        public int Peer;
        public uint PlayerId;
        public string Name = "";
        public long ConnectedTick;

        public HullHandle Hull;
        public readonly InputRecord[] Inputs = new InputRecord[InputQueueSize];
        public int InputHead;
        public int InputCount;
        public uint LastQueuedSequence;
        public uint LastSimulatedSequence;
        public int InputCredit;
        public int StarvedTicks;
        public FlightInput LastInput;

        /// <summary>The newest order definition the client has sent, and the counter value it belongs to.</summary>
        public ushort OrderId;
        public HullOrder Order;

        public void Reset()
        {
            State = ConnectionState.Free;
            Transport = null!;
            Name = "";
            Hull = default;
            InputHead = InputCount = 0;
            LastQueuedSequence = LastSimulatedSequence = 0;
            InputCredit = StarvedTicks = 0;
            LastInput = new FlightInput { Dampeners = true };
            OrderId = 0;
            Order = default;
        }
    }

    private readonly ServerConfig _config;
    private readonly ITransport[] _transports;
    private readonly Connection[] _connections;
    private readonly TickLoop _loop;
    private readonly byte[] _sendBuffer = new byte[64 * 1024];
    private readonly HullStore _hulls = new();
    private readonly GameContent _content;
    private readonly SectorCache _rocks;

    private long _tick;
    private uint _nextPlayerId = 1;
    private int _playerCount;

    public ServerHost(ServerConfig config, GameContent content, params ITransport[] transports)
    {
        _config = config;
        _transports = transports;
        _content = content;
        Galaxy = content.Galaxy.WithSeed(config.GalaxySeed);
        _rocks = new SectorCache(Galaxy);

        // Room for every player plus connections that are still shaking hands or about to be refused as full.
        _connections = new Connection[config.MaxPlayers + 8];
        for (var i = 0; i < _connections.Length; i++)
        {
            _connections[i] = new Connection();
            _connections[i].Reset();
        }

        _loop = new TickLoop("Server", TickLoop.SimulationHz, this);
    }

    public Galaxy Galaxy { get; }

    public ServerStatus Status => new(Volatile.Read(ref _tick), Volatile.Read(ref _playerCount), _loop.Stats);

    /// <summary>The exception that stopped the server, if a tick threw.</summary>
    public Exception? Fault => _loop.Fault;

    public bool IsRunning => _loop.IsRunning;

    public void Start() => _loop.Start();

    /// <summary>Stops the tick thread, closes every transport and frees the world.</summary>
    public void Dispose()
    {
        _loop.Stop();
        foreach (var transport in _transports)
            transport.Dispose();
        _hulls.Dispose();
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

        foreach (var connection in _connections)
        {
            if (connection.State == ConnectionState.AwaitingHello && tick - connection.ConnectedTick > HandshakeTimeoutTicks)
                Refuse(connection, RejectReason.BadHandshake);
            else if (connection.State == ConnectionState.Active)
                SimulatePlayer(connection);
        }

        // Hulls nobody is flying carry out their standing orders, or hold position.
        var hold = new FlightInput { Dampeners = true };
        for (var i = 0; i < _hulls.Slots; i++)
        {
            if (!_hulls.IsAliveAt(i))
                continue;
            ref var info = ref _hulls.Info(i);
            if (!info.Piloted)
                HullSim.Step(ref _hulls.State(i), ref info.Order, hold, _content.Designs[info.Design].Flight, _rocks);
        }

        foreach (var connection in _connections)
        {
            if (connection.State == ConnectionState.Active)
                SendSnapshot(connection, tick);
        }

        Volatile.Write(ref _tick, tick + 1);
    }

    void ITickHandler.OnStop()
    {
        foreach (var connection in _connections)
        {
            if (connection.State != ConnectionState.Free)
                connection.Transport.Disconnect(connection.Peer);
            connection.Reset();
        }

        Volatile.Write(ref _playerCount, 0);
        Logs.Sim.Info($"Server '{_config.Name}' stopped at tick {_tick}");
    }

    void ITransportHandler.OnConnected(ITransport transport, int peer)
    {
        foreach (var connection in _connections)
        {
            if (connection.State != ConnectionState.Free)
                continue;

            connection.State = ConnectionState.AwaitingHello;
            connection.Transport = transport;
            connection.Peer = peer;
            connection.ConnectedTick = _tick;
            return;
        }

        transport.Disconnect(peer);
    }

    void ITransportHandler.OnDisconnected(ITransport transport, int peer, DisconnectReason reason)
    {
        var connection = Find(transport, peer);
        if (connection is null)
            return;

        if (connection.State == ConnectionState.Active)
        {
            for (var i = 0; i < _hulls.Slots; i++)
            {
                if (_hulls.IsAliveAt(i) && _hulls.Info(i).Owner == connection.PlayerId)
                    _hulls.Destroy(_hulls.HandleAt(i));
            }

            Volatile.Write(ref _playerCount, _playerCount - 1);
            Logs.Sim.Info($"{connection.Name} (player {connection.PlayerId}) left: {reason}");
        }

        connection.Reset();
    }

    void ITransportHandler.OnMessage(ITransport transport, int peer, NetChannel channel, ReadOnlySpan<byte> payload)
    {
        var connection = Find(transport, peer);
        if (connection is null || payload.IsEmpty)
            return;

        var reader = new NetReader(payload[1..]);
        var id = (MessageId)payload[0];

        if (connection.State == ConnectionState.AwaitingHello)
        {
            if (id == MessageId.Hello && channel == NetChannel.Reliable && HelloMessage.TryRead(ref reader, out var hello))
                Admit(connection, hello);
            else
                Refuse(connection, RejectReason.BadHandshake);
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
            case MessageId.Input:
                ReceiveInput(connection, ref reader);
                break;
            case MessageId.FleetOrders when channel == NetChannel.Reliable:
                ReceiveFleetOrders(connection, ref reader);
                break;
        }
    }

    private void ReceiveInput(Connection connection, ref NetReader reader)
    {
        Span<InputRecord> records = stackalloc InputRecord[InputMessage.MaxRecords];
        if (!InputMessage.TryRead(ref reader, records, out var count, out var orderId, out var order))
            return;

        connection.OrderId = orderId;
        connection.Order = order;

        // Packets repeat recent inputs; queue only the ones not seen before, oldest first.
        for (var i = 0; i < count; i++)
        {
            ref var record = ref records[i];
            if (record.Sequence <= connection.LastQueuedSequence)
                continue;

            if (connection.InputCount == InputQueueSize)
            {
                connection.InputHead = (connection.InputHead + 1) % InputQueueSize;
                connection.InputCount--;
            }

            connection.Inputs[(connection.InputHead + connection.InputCount) % InputQueueSize] = record;
            connection.InputCount++;
            connection.LastQueuedSequence = record.Sequence;
        }
    }

    private void ReceiveFleetOrders(Connection connection, ref NetReader reader)
    {
        Span<FleetOrder> orders = stackalloc FleetOrder[FleetOrdersMessage.MaxOrders];
        if (!FleetOrdersMessage.TryRead(ref reader, orders, out var count))
            return;

        // A client may only order hulls it owns, and never the one it is flying: that hull's orders travel with
        // its inputs so prediction and authority stay in step.
        for (var i = 0; i < count; i++)
        {
            var handle = new HullHandle((int)(orders[i].Hull & 0xFFFF), (int)(orders[i].Hull >> 16));
            if (!_hulls.IsAlive(handle))
                continue;
            ref var info = ref _hulls.Info(handle.Index);
            if (info.Owner == connection.PlayerId && !info.Piloted)
                info.Order = orders[i].Order;
        }
    }

    /// <summary>
    /// Advances a player's hull by the inputs that have arrived. The hull steps once per client input, so the
    /// server reproduces the client's prediction exactly; a credit limit keeps that from outrunning real time.
    /// </summary>
    private void SimulatePlayer(Connection connection)
    {
        if (!_hulls.IsAlive(connection.Hull))
            return;

        var index = connection.Hull.Index;
        ref var state = ref _hulls.State(index);
        ref var info = ref _hulls.Info(index);
        var flight = _content.Designs[info.Design].Flight;
        connection.InputCredit = Math.Min(connection.InputCredit + 1, MaxInputCredit);

        var simulated = false;
        while (connection.InputCount > 0 && connection.InputCredit > 0)
        {
            var record = connection.Inputs[connection.InputHead];
            connection.InputHead = (connection.InputHead + 1) % InputQueueSize;
            connection.InputCount--;
            connection.InputCredit--;

            if (record.OrderId != info.OrderId && record.OrderId == connection.OrderId)
            {
                info.Order = connection.Order;
                info.OrderId = record.OrderId;
            }

            HullSim.Step(ref state, ref info.Order, record.Input, flight, _rocks);
            connection.LastSimulatedSequence = record.Sequence;
            connection.LastInput = record.Input;
            simulated = true;
        }

        if (simulated)
        {
            connection.StarvedTicks = 0;
            return;
        }

        // The client has gone quiet. Keep the hull safe: carry on with its order, or brake to a stop.
        if (++connection.StarvedTicks > InputStarvedTicks)
            HullSim.Step(ref state, ref info.Order, new FlightInput { Dampeners = true }, flight, _rocks);
    }

    private void SendSnapshot(Connection connection, long tick)
    {
        var includeRemote = (tick + connection.PlayerId) % RemoteHullInterval == 0;
        var writer = new NetWriter(_sendBuffer);
        var header = new SnapshotHeader
        {
            ServerTick = tick,
            AckedInput = connection.LastSimulatedSequence,
            OwnHull = connection.Hull.Packed,
        };

        // The count is not known until the hulls are written; reserve the header and fill it in afterwards.
        header.Write(ref writer);
        var count = 0;
        for (var i = 0; i < _hulls.Slots && count < byte.MaxValue; i++)
        {
            if (!_hulls.IsAliveAt(i) || (!includeRemote && i != connection.Hull.Index))
                continue;
            if (writer.Length + HullRecord.MaxWireSize > _sendBuffer.Length)
                break;

            ref var info = ref _hulls.Info(i);
            new HullRecord
            {
                Id = _hulls.HandleAt(i).Packed,
                Owner = info.Owner,
                Design = info.Design,
                State = _hulls.State(i),
                Order = info.Order,
            }.Write(ref writer);
            count++;
        }

        var length = writer.Length;
        header.HullCount = (byte)count;
        var rewrite = new NetWriter(_sendBuffer);
        header.Write(ref rewrite);
        connection.Transport.Send(connection.Peer, NetChannel.Unreliable, _sendBuffer.AsSpan(0, length));
    }

    private void Admit(Connection connection, in HelloMessage hello)
    {
        if (hello.ProtocolVersion != ProtocolInfo.Version)
        {
            Refuse(connection, RejectReason.VersionMismatch);
            return;
        }

        if (_playerCount >= _config.MaxPlayers)
        {
            Refuse(connection, RejectReason.ServerFull);
            return;
        }

        connection.State = ConnectionState.Active;
        connection.PlayerId = _nextPlayerId++;
        connection.Name = hello.PlayerName;
        Volatile.Write(ref _playerCount, _playerCount + 1);

        // Each player's group arrives in its own lane beside the spawn point, the pod in front and the rest of
        // the starting fleet fanned out behind it, so nothing starts inside anything else.
        var lane = (int)(connection.PlayerId - 1);
        var origin = Galaxy.SpawnPoint + new Double3((lane + 1) / 2 * 160.0 * (lane % 2 == 0 ? 1 : -1), 0, 0);
        var pod = (byte)_content.PodDesign;
        connection.Hull = _hulls.Create(connection.PlayerId, pod, HullState.Spawn(origin, _content.Designs[pod].Flight));
        _hulls.Info(connection.Hull.Index).Piloted = true;
        for (var i = 0; i < _content.StartingFleet.Length; i++)
        {
            var design = (byte)_content.StartingFleet[i];
            var side = (i / 2 + 1) * 45.0 * (i % 2 == 0 ? 1 : -1);
            var position = origin + new Double3(side, 0, 40.0 + 20.0 * (i / 2));
            _hulls.Create(connection.PlayerId, design, HullState.Spawn(position, _content.Designs[design].Flight));
        }

        var welcome = new WelcomeMessage
        {
            PlayerId = connection.PlayerId,
            TickRate = TickLoop.SimulationHz,
            ServerTick = _tick,
            GalaxySeed = _config.GalaxySeed,
            ServerName = _config.Name,
            HullId = connection.Hull.Packed,
        };
        var writer = new NetWriter(_sendBuffer);
        welcome.Write(ref writer);
        connection.Transport.Send(connection.Peer, NetChannel.Reliable, writer.Written);

        Logs.Sim.Info($"{connection.Name} joined as player {connection.PlayerId} via {connection.Transport.Description}");
    }

    private void Refuse(Connection connection, RejectReason reason)
    {
        var writer = new NetWriter(_sendBuffer);
        writer.WriteByte((byte)MessageId.Reject);
        writer.WriteByte((byte)reason);
        connection.Transport.Send(connection.Peer, NetChannel.Reliable, writer.Written);
        connection.Transport.Disconnect(connection.Peer);
        Logs.Sim.Info($"Refused a connection on {connection.Transport.Description}: {reason}");
        connection.Reset();
    }

    private Connection? Find(ITransport transport, int peer)
    {
        foreach (var connection in _connections)
        {
            if (connection.State != ConnectionState.Free && connection.Peer == peer && ReferenceEquals(connection.Transport, transport))
                return connection;
        }

        return null;
    }
}
