using System.Diagnostics;
using Prospect.Engine.Diagnostics;
using Prospect.Engine.Net;
using Prospect.Engine.Threading;
using Prospect.Engine.Time;
using Prospect.Game.Protocol;
using Prospect.Game.World;

namespace Prospect.Game.Client;

public enum SessionState : byte
{
    /// <summary>Waiting for the transport to reach the server.</summary>
    Connecting,

    /// <summary>Connected; waiting for the server to accept the handshake.</summary>
    Handshaking,

    /// <summary>In the game.</summary>
    Active,

    /// <summary>The session has ended; <see cref="SessionSnapshot.EndReason"/> says why.</summary>
    Ended,
}

public enum SessionEndReason : byte
{
    None,
    LeftByPlayer,
    ConnectFailed,
    ConnectionLost,
    ServerClosed,
    ServerFull,
    VersionMismatch,
    Refused,
}

/// <summary>
/// What the render thread knows about the session: a copy published by the client simulation each tick. The render
/// thread reads these and never touches simulation state.
/// </summary>
public sealed class SessionSnapshot(GameContent content)
{
    public SessionState State;
    public SessionEndReason EndReason;
    public uint PlayerId;
    public string ServerName = "";
    public ulong GalaxySeed;
    public int PlayerCount;

    /// <summary>Client simulation tick that produced this snapshot.</summary>
    public long ClientTick;

    /// <summary>The server tick the client believes is current, extrapolated from the last clock probe.</summary>
    public long ServerTick;

    /// <summary>Smoothed network round-trip time in milliseconds, or -1 before the first probe returns.</summary>
    public float RoundTripMs = -1;

    /// <summary>Stopwatch timestamp at which this snapshot was published, the base for render interpolation.</summary>
    public long PublishedAt;

    /// <summary>The server's galaxy, once known. Sector contents are generated from it on demand.</summary>
    public Galaxy? Galaxy;

    /// <summary>
    /// The game's content as the server's world plays by it, once the server has said what its settings are;
    /// until then, as the files have it. Hull figures shown to the player come from here, so a data sheet reads
    /// what the simulation uses. It never changes after loading and is safe to read from any thread.
    /// </summary>
    public GameContent Content = content;

    /// <summary>The hulls to draw this tick; only the first <see cref="HullCount"/> entries are meaningful.</summary>
    public readonly HullView[] Hulls = new HullView[ClientWorld.MaxViews];
    public int HullCount;

    /// <summary>Index of the player's own hull in <see cref="Hulls"/>, or -1 before it exists.</summary>
    public int OwnHull = -1;
    public bool Dampeners;
    public FlightParams Flight;

    /// <summary>The largest recent gap between the client's prediction and the server's answer, in metres.</summary>
    public float PredictionError;

    /// <summary>The server tick as a fraction, for evaluating motion that is a function of time.</summary>
    public double ServerTickPrecise;

    /// <summary>Asteroids that have been pushed from their generated positions; the first <see cref="RockCount"/> are meaningful.</summary>
    public readonly RockDelta[] Rocks = new RockDelta[512];
    public int RockCount;

    public TickStats Loop;
}

/// <summary>
/// The client side of a game session. Runs on its own fixed-rate thread, talks to the server through a transport
/// (in-process for a local game, UDP for a remote one) and publishes snapshots for the render thread.
/// </summary>
public sealed class ClientSession : ITickHandler, ITransportHandler, IDisposable
{
    private const int PingIntervalTicks = TickLoop.SimulationHz / 2;
    private const int ConnectTimeoutTicks = TickLoop.SimulationHz * 10;

    private readonly ITransport _transport;

    // The content as loaded, and as the server's settings make it once the server has sent them.
    private readonly GameContent _loaded;
    private GameContent _content;
    private readonly ClientWorld _world;
    private Galaxy? _galaxy;
    private readonly string _playerName;
    private readonly TickLoop _loop;
    private readonly SnapshotExchange<SessionSnapshot> _snapshots;
    private readonly byte[] _sendBuffer = new byte[1024];

    private SessionState _state = SessionState.Connecting;
    private SessionEndReason _endReason;
    private uint _playerId;
    private string _serverName = "";
    private ulong _galaxySeed;
    private int _playerCount;
    private float _roundTripMs = -1;

    // Clock sync: the server tick reported by the last pong and the local time at which it was (estimated) current.
    private long _syncServerTick;
    private long _syncTimestamp;

    private volatile bool _leaveRequested;

    public ClientSession(ITransport transport, GameContent content, string playerName)
    {
        _transport = transport;
        _loaded = content;
        _content = content;
        _snapshots = new SnapshotExchange<SessionSnapshot>(() => new SessionSnapshot(content));
        _world = new ClientWorld(content);
        _playerName = PlayerNames.Sanitize(playerName);
        _loop = new TickLoop("ClientSim", TickLoop.SimulationHz, this);
    }

    /// <summary>The exception that stopped the session thread, if a tick threw.</summary>
    public Exception? Fault => _loop.Fault;

    public void Start() => _loop.Start();

    /// <summary>The latest published snapshot. Call from the render thread only.</summary>
    public SessionSnapshot AcquireSnapshot() => _snapshots.Acquire(out _);

    /// <summary>Where the input thread delivers the player's controls and orders.</summary>
    public PlayerInputBridge Input { get; } = new();

    /// <summary>Asks the session to leave the server. Safe from any thread; takes effect on the next tick.</summary>
    public void RequestLeave() => _leaveRequested = true;

    /// <summary>Stops the session thread and closes the transport.</summary>
    public void Dispose()
    {
        _loop.Stop();
        _transport.Dispose();
        _world.Dispose();
    }

    void ITickHandler.OnStart() => Logs.Net.Info($"Session starting on {_transport.Description}");

    void ITickHandler.Tick(long tick)
    {
        if (_state != SessionState.Ended)
        {
            _transport.Poll(this);

            if (_leaveRequested)
                End(SessionEndReason.LeftByPlayer);
            else if (_state != SessionState.Active && tick > ConnectTimeoutTicks)
                End(SessionEndReason.ConnectFailed);
            else if (_state == SessionState.Active && tick % PingIntervalTicks == 0)
                SendPing();

            if (_state == SessionState.Active)
                _world.Tick(Input, _transport, EstimateServerTick(Stopwatch.GetTimestamp()));
        }

        var snapshot = _snapshots.WriteBuffer;
        var now = Stopwatch.GetTimestamp();
        snapshot.State = _state;
        snapshot.EndReason = _endReason;
        snapshot.PlayerId = _playerId;
        snapshot.ServerName = _serverName;
        snapshot.GalaxySeed = _galaxySeed;
        snapshot.PlayerCount = _playerCount;
        snapshot.ClientTick = tick;
        var serverTick = _state == SessionState.Active ? EstimateServerTick(now) : 0;
        snapshot.ServerTick = (long)serverTick;
        snapshot.ServerTickPrecise = serverTick;
        snapshot.RockCount = _state == SessionState.Active ? _world.CopyRockDeltas(snapshot.Rocks) : 0;
        snapshot.Galaxy = _galaxy;
        snapshot.Content = _content;
        snapshot.HullCount = _state == SessionState.Active ? _world.BuildViews(snapshot.Hulls, serverTick, tick, out snapshot.OwnHull) : 0;
        if (snapshot.HullCount == 0)
            snapshot.OwnHull = -1;
        snapshot.Dampeners = _world.Dampeners;
        snapshot.PredictionError = _world.PredictionError;
        snapshot.Flight = _world.Flight;
        snapshot.RoundTripMs = _roundTripMs;
        snapshot.PublishedAt = now;
        snapshot.Loop = _loop.Stats;
        _snapshots.Publish();
    }

    private double EstimateServerTick(long now) =>
        _syncServerTick + (now - _syncTimestamp) * (double)TickLoop.SimulationHz / Stopwatch.Frequency;

    void ITickHandler.OnStop()
    {
        if (_state != SessionState.Ended)
            _transport.Disconnect(0);
    }

    void ITransportHandler.OnConnected(ITransport transport, int peer)
    {
        if (_state != SessionState.Connecting)
            return;

        _state = SessionState.Handshaking;
        var hello = new HelloMessage { ProtocolVersion = ProtocolInfo.Version, PlayerName = _playerName };
        var writer = new NetWriter(_sendBuffer);
        hello.Write(ref writer);
        transport.Send(peer, NetChannel.Reliable, writer.Written);
    }

    void ITransportHandler.OnDisconnected(ITransport transport, int peer, DisconnectReason reason)
    {
        if (_state == SessionState.Ended)
            return;

        End(
            reason switch
            {
                DisconnectReason.ServerFull => SessionEndReason.ServerFull,
                DisconnectReason.ConnectFailed => SessionEndReason.ConnectFailed,
                DisconnectReason.ConnectionLost => SessionEndReason.ConnectionLost,
                _ when _state == SessionState.Active => SessionEndReason.ServerClosed,
                _ => SessionEndReason.Refused,
            }
        );
    }

    void ITransportHandler.OnMessage(ITransport transport, int peer, NetChannel channel, ReadOnlySpan<byte> payload)
    {
        if (payload.IsEmpty || _state == SessionState.Ended)
            return;

        var reader = new NetReader(payload[1..]);
        switch ((MessageId)payload[0])
        {
            case MessageId.Welcome
                when _state == SessionState.Handshaking && WelcomeMessage.TryRead(ref reader, _loaded.SettingCatalog, out var welcome):
                _state = SessionState.Active;
                _playerId = welcome.PlayerId;
                _serverName = welcome.ServerName;
                _galaxySeed = welcome.GalaxySeed;

                // The server's world, not this machine's files, says how hard things hit: predict by its settings.
                _content = _loaded.WithSettings(welcome.Settings);
                _world.SetContent(_content);
                _galaxy = _content.Galaxy.WithSeed(welcome.GalaxySeed);
                _world.SetGalaxy(_galaxy);
                _world.SetPlayer(welcome.PlayerId);
                _world.SetOwnHull(welcome.HullId);
                _playerCount = 1;
                _syncServerTick = welcome.ServerTick;
                _syncTimestamp = Stopwatch.GetTimestamp();
                Logs.Net.Info($"Joined '{_serverName}' as player {_playerId}");
                SendPing();
                break;

            case MessageId.Reject when _state == SessionState.Handshaking:
                End(
                    (RejectReason)reader.ReadByte() switch
                    {
                        RejectReason.VersionMismatch => SessionEndReason.VersionMismatch,
                        RejectReason.ServerFull => SessionEndReason.ServerFull,
                        _ => SessionEndReason.Refused,
                    }
                );
                break;

            case MessageId.RockDeltas when _state == SessionState.Active && channel == NetChannel.Reliable:
                _world.OnRockDeltas(ref reader);
                break;

            case MessageId.Snapshot when _state == SessionState.Active:
                _world.OnSnapshot(ref reader, _loop.Stats.Tick);
                break;

            case MessageId.Pong when _state == SessionState.Active && PongMessage.TryRead(ref reader, out var pong):
                {
                    var now = Stopwatch.GetTimestamp();
                    var roundTrip = now - pong.ClientTimestamp;
                    if (roundTrip < 0 || roundTrip > Stopwatch.Frequency * 10)
                        break;

                    var ms = (float)(roundTrip * 1000.0 / Stopwatch.Frequency);
                    _roundTripMs = _roundTripMs < 0 ? ms : _roundTripMs + (ms - _roundTripMs) * 0.2f;
                    _playerCount = pong.PlayerCount;

                    // The server stamped its tick half a round trip ago.
                    _syncServerTick = pong.ServerTick;
                    _syncTimestamp = now - roundTrip / 2;
                    break;
                }
        }
    }

    private void SendPing()
    {
        var ping = new PingMessage { ClientTimestamp = Stopwatch.GetTimestamp() };
        var writer = new NetWriter(_sendBuffer);
        ping.Write(ref writer);
        _transport.Send(0, NetChannel.Unreliable, writer.Written);
    }

    private void End(SessionEndReason reason)
    {
        _transport.Disconnect(0);
        _state = SessionState.Ended;
        _endReason = reason;
        Logs.Net.Info($"Session ended: {reason}");
    }
}
