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

    /// <summary>
    /// The world's settings, fixed for as long as the server runs; null for the defaults in the content. Build
    /// them with the content's <see cref="GameContent.SettingCatalog"/>.
    /// </summary>
    public WorldSettings? Settings { get; init; }

    /// <summary>The sector new players arrive in the middle of. The home sector unless a server is set up otherwise.</summary>
    public SectorCoord SpawnSector { get; init; }
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
    private readonly HullField _field;
    private readonly GameContent _content;
    private readonly RockField _rocks;
    private readonly RockImpulses _impulses = new();

    private long _tick;
    private uint _nextPlayerId = 1;
    private int _playerCount;

    public ServerHost(ServerConfig config, GameContent content, params ITransport[] transports)
    {
        _config = config;
        _transports = transports;

        // From here on the server only knows the content as its own world's settings make it.
        _content = content.WithSettings(config.Settings ?? content.SettingCatalog.Defaults);
        Galaxy = _content.Galaxy.WithSeed(config.GalaxySeed);
        _rocks = new RockField(Galaxy);
        _field = new HullField(_hulls, _content.Designs);

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

    /// <summary>The content as this server's world plays by it: its settings and the figures that follow from them.</summary>
    public GameContent Content => _content;

    /// <summary>Where new players appear: the middle of the spawn sector, which every sector's asteroids keep clear.</summary>
    private Double3 SpawnPoint => Galaxy.CenterOf(_config.SpawnSector);

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

        // Whoever reads the log should be able to tell how this world differs from a plain one.
        var catalog = _content.SettingCatalog;
        var settings = _content.Settings;
        var changed = 0;
        foreach (var setting in catalog.Definitions)
        {
            var value = settings.Get(setting);
            if (value == setting.Default)
                continue;
            changed++;
            Logs.Sim.Info($"World setting {setting.Name} ({setting.Id}): {setting.Format(value)}, default {setting.Format(setting.Default)}");
        }

        if (changed == 0)
            Logs.Sim.Info("World settings: all at their defaults");
    }

    void ITickHandler.Tick(long tick)
    {
        foreach (var transport in _transports)
            transport.Poll(this);

        // From here every hull steps for this tick: the piloted ones first, by the inputs that arrived, then the rest.
        _field.BeginRound();

        // A hull whose pilot's input has not arrived stays where it is this tick, though its velocity says
        // otherwise. The others must know before they step, or they would meet it a tick's flight ahead.
        foreach (var connection in _connections)
        {
            if (connection.State == ConnectionState.Active && connection.InputCount == 0 && connection.StarvedTicks < InputStarvedTicks && _hulls.IsAlive(connection.Hull))
                _field.Hold(connection.Hull.Index);
        }

        foreach (var connection in _connections)
        {
            if (connection.State == ConnectionState.AwaitingHello && tick - connection.ConnectedTick > HandshakeTimeoutTicks)
                Refuse(connection, RejectReason.BadHandshake);
            else if (connection.State == ConnectionState.Active)
                SimulatePlayer(connection);
        }

        // Hulls nobody is flying carry out their standing orders, or hold position. A step can destroy other
        // hulls and put their pilots in new ones, so nothing read here is kept across it.
        var hold = new FlightInput { Dampeners = true };
        for (var i = 0; i < _hulls.Slots; i++)
        {
            if (_hulls.IsAliveAt(i) && !_hulls.Info(i).Piloted && StepHull(i, hold))
                _hulls.Destroy(_hulls.HandleAt(i));
        }

        // Asteroids that were hit this tick take their new motion, and every client is told.
        if (_impulses.Count > 0)
        {
            var hit = _impulses.Items.AsSpan(0, _impulses.Count);
            foreach (ref readonly var delta in hit)
                _rocks.Set(delta);
            foreach (var connection in _connections)
            {
                if (connection.State == ConnectionState.Active)
                    SendRockDeltas(connection, hit);
            }

            _impulses.Count = 0;
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
            case MessageId.Control when channel == NetChannel.Reliable:
                TakeControl(connection, reader.ReadUInt32());
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

    /// <summary>
    /// Moves a player into another hull they own, or, for hull id zero, out of their hull into a new drone beside
    /// it. The hull left behind is flown by the server from then on; a drone left behind is removed.
    /// </summary>
    private void TakeControl(Connection connection, uint hullId)
    {
        if (!_hulls.IsAlive(connection.Hull))
            return;

        var drone = (byte)_content.DroneDesign;
        var current = connection.Hull;
        ref var currentInfo = ref _hulls.Info(current.Index);
        HullHandle next;

        if (hullId == 0)
        {
            // Already in a drone: there is nothing smaller to step out into.
            if (currentInfo.Design == drone)
                return;

            var from = _hulls.State(current.Index);
            var clearance = _content.Designs[currentInfo.Design].Flight.Radius + _content.Designs[drone].Flight.Radius + 4f;
            var above = from.Position + System.Numerics.Vector3.Transform(System.Numerics.Vector3.UnitY, from.Orientation) * clearance;
            next = _hulls.Create(connection.PlayerId, drone, _content.Designs[drone], above);
            ref var launched = ref _hulls.State(next.Index);
            launched.Orientation = from.Orientation;
            launched.Velocity = from.Velocity;

            // Creating a hull may have moved the storage; look the old one up again.
            currentInfo = ref _hulls.Info(current.Index);
        }
        else
        {
            next = new HullHandle((int)(hullId & 0xFFFF), (int)(hullId >> 16));
            if (next == current || !_hulls.IsAlive(next) || _hulls.Info(next.Index).Owner != connection.PlayerId)
                return;
        }

        if (currentInfo.Design == drone)
            _hulls.Destroy(current);
        else
            currentInfo.Piloted = false;

        // The pilot's hands replace whatever the hull was ordered to do.
        ref var info = ref _hulls.Info(next.Index);
        info.Piloted = true;
        info.Order = default;
        info.OrderId = connection.OrderId;
        connection.Hull = next;
        connection.InputHead = connection.InputCount = 0;
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
        connection.InputCredit = Math.Min(connection.InputCredit + 1, MaxInputCredit);

        var simulated = false;
        while (connection.InputCount > 0 && connection.InputCredit > 0)
        {
            var record = connection.Inputs[connection.InputHead];
            connection.InputHead = (connection.InputHead + 1) % InputQueueSize;
            connection.InputCount--;
            connection.InputCredit--;

            // Looked up for each input: the step before may have created a hull and moved the storage.
            ref var info = ref _hulls.Info(index);
            if (record.OrderId != info.OrderId && record.OrderId == connection.OrderId)
            {
                info.Order = connection.Order;
                info.OrderId = record.OrderId;
            }

            var destroyed = StepHull(index, record.Input);
            connection.LastSimulatedSequence = record.Sequence;
            connection.LastInput = record.Input;
            simulated = true;
            if (destroyed)
            {
                LoseHull(connection);
                return;
            }
        }

        if (simulated)
        {
            connection.StarvedTicks = 0;
            return;
        }

        // The client has gone quiet. Keep the hull safe: carry on with its order, or brake to a stop.
        if (++connection.StarvedTicks > InputStarvedTicks && StepHull(index, new FlightInput { Dampeners = true }))
            LoseHull(connection);
    }

    /// <summary>
    /// Advances one hull a tick, colliding it block by block with asteroids and with the other hulls. The hulls
    /// it destroys by running into them are removed here, their pilots put in drones. Returns true if the hull
    /// itself was destroyed, which is left to the caller. Creating a drone can move the hull storage: hold no
    /// reference into it across this call.
    /// </summary>
    private bool StepHull(int index, in FlightInput input)
    {
        var destroyed = _field.Step(index, input, _rocks, _tick, _impulses);
        var lost = _field.Lost;
        for (var i = 0; i < lost.Length; i++)
        {
            var handle = _hulls.HandleAt(lost[i]);
            var pilot = PilotOf(handle);
            if (pilot is not null)
                LoseHull(pilot);
            else
                _hulls.Destroy(handle);
        }

        return destroyed;
    }

    /// <summary>The connection flying a hull, or null if the server flies it.</summary>
    private Connection? PilotOf(HullHandle handle)
    {
        foreach (var connection in _connections)
        {
            if (connection.State == ConnectionState.Active && connection.Hull == handle)
                return connection;
        }

        return null;
    }

    /// <summary>
    /// The hull a player was flying has been destroyed. They get out in a drone where it died; if it was a drone
    /// that died, a new one is sent from the spawn point.
    /// </summary>
    private void LoseHull(Connection connection)
    {
        var lost = connection.Hull;
        var wasDrone = _hulls.Info(lost.Index).Design == _content.DroneDesign;
        var state = _hulls.State(lost.Index);
        _hulls.Destroy(lost);

        var drone = (byte)_content.DroneDesign;
        connection.Hull = _hulls.Create(connection.PlayerId, drone, _content.Designs[drone], wasDrone ? SpawnPoint : state.Position);
        ref var info = ref _hulls.Info(connection.Hull.Index);
        info.Piloted = true;
        info.OrderId = connection.OrderId;
        if (!wasDrone)
        {
            ref var escaped = ref _hulls.State(connection.Hull.Index);
            escaped.Orientation = state.Orientation;
            escaped.Velocity = state.Velocity * 0.5f;
        }

        connection.InputHead = connection.InputCount = 0;
        Logs.Sim.Info($"{connection.Name} lost a hull and is in a drone");
    }

    private void SendRockDeltas(Connection connection, ReadOnlySpan<RockDelta> deltas)
    {
        while (!deltas.IsEmpty)
        {
            var chunk = deltas[..Math.Min(deltas.Length, RockDeltasMessage.MaxDeltas)];
            var writer = new NetWriter(_sendBuffer);
            RockDeltasMessage.Write(ref writer, chunk);
            connection.Transport.Send(connection.Peer, NetChannel.Reliable, writer.Written);
            deltas = deltas[chunk.Length..];
        }
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

        // While the player's hull is knocking against others, those go out every tick as well: the client
        // predicts the contact, and can only do it against hulls that are where the server has them.
        var recent = tick - RemoteHullInterval;
        var inContact = _hulls.IsAlive(connection.Hull) && _hulls.Info(connection.Hull.Index).ContactTick > recent;

        // The count is not known until the hulls are written; reserve the header and fill it in afterwards.
        header.Write(ref writer);
        var count = 0;
        for (var i = 0; i < _hulls.Slots && count < byte.MaxValue; i++)
        {
            if (!_hulls.IsAliveAt(i))
                continue;
            if (!includeRemote && i != connection.Hull.Index && !(inContact && _hulls.Info(i).ContactTick > recent))
                continue;
            if (writer.Length + HullRecord.MaxWireSize > _sendBuffer.Length)
                break;

            ref var info = ref _hulls.Info(i);
            new HullRecord
            {
                Id = _hulls.HandleAt(i).Packed,
                Owner = info.Owner,
                Design = info.Design,
                Blocks = info.Blocks,
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
        var origin = SpawnPoint + new Double3((lane + 1) / 2 * 160.0 * (lane % 2 == 0 ? 1 : -1), 0, 0);
        var pod = (byte)_content.PodDesign;
        connection.Hull = _hulls.Create(connection.PlayerId, pod, _content.Designs[pod], origin);
        _hulls.Info(connection.Hull.Index).Piloted = true;
        for (var i = 0; i < _content.StartingFleet.Length; i++)
        {
            var design = (byte)_content.StartingFleet[i];
            var side = (i / 2 + 1) * 45.0 * (i % 2 == 0 ? 1 : -1);
            var position = origin + new Double3(side, 0, 40.0 + 20.0 * (i / 2));
            _hulls.Create(connection.PlayerId, design, _content.Designs[design], position);
        }

        var welcome = new WelcomeMessage
        {
            PlayerId = connection.PlayerId,
            TickRate = TickLoop.SimulationHz,
            ServerTick = _tick,
            GalaxySeed = _config.GalaxySeed,
            ServerName = _config.Name,
            HullId = connection.Hull.Packed,
            Settings = _content.Settings,
        };
        var writer = new NetWriter(_sendBuffer);
        welcome.Write(ref writer, _content.SettingCatalog);
        connection.Transport.Send(connection.Peer, NetChannel.Reliable, writer.Written);

        // The newcomer generates the galaxy from the seed like everyone else; what it cannot know is which
        // asteroids have been knocked about since.
        SendRockDeltas(connection, _rocks.Deltas);

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
