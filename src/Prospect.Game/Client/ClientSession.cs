using System.Diagnostics;
using Prospect.Engine.Diagnostics;
using Prospect.Engine.Net;
using Prospect.Engine.Threading;
using Prospect.Engine.Time;
using Prospect.Game.Protocol;

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
public sealed class SessionSnapshot
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
    private readonly string _playerName;
    private readonly TickLoop _loop;
    private readonly SnapshotExchange<SessionSnapshot> _snapshots = new(() => new SessionSnapshot());
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

    public ClientSession(ITransport transport, string playerName)
    {
        _transport = transport;
        _playerName = PlayerNames.Sanitize(playerName);
        _loop = new TickLoop("ClientSim", TickLoop.SimulationHz, this);
    }

    /// <summary>The exception that stopped the session thread, if a tick threw.</summary>
    public Exception? Fault => _loop.Fault;

    public void Start() => _loop.Start();

    /// <summary>The latest published snapshot. Call from the render thread only.</summary>
    public SessionSnapshot AcquireSnapshot() => _snapshots.Acquire(out _);

    /// <summary>Asks the session to leave the server. Safe from any thread; takes effect on the next tick.</summary>
    public void RequestLeave() => _leaveRequested = true;

    /// <summary>Stops the session thread and closes the transport.</summary>
    public void Dispose()
    {
        _loop.Stop();
        _transport.Dispose();
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
        snapshot.ServerTick = _state == SessionState.Active
            ? _syncServerTick + (now - _syncTimestamp) * TickLoop.SimulationHz / Stopwatch.Frequency
            : 0;
        snapshot.RoundTripMs = _roundTripMs;
        snapshot.PublishedAt = now;
        snapshot.Loop = _loop.Stats;
        _snapshots.Publish();
    }

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
                when _state == SessionState.Handshaking && WelcomeMessage.TryRead(ref reader, out var welcome):
                _state = SessionState.Active;
                _playerId = welcome.PlayerId;
                _serverName = welcome.ServerName;
                _galaxySeed = welcome.GalaxySeed;
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
