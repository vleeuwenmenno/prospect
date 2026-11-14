using System.Diagnostics;
using Prospect.Engine.Diagnostics;
using Prospect.Engine.Net;
using Prospect.Engine.Threading;
using Prospect.Engine.Time;
using Prospect.Game.Protocol;
using Prospect.Game.Server;
using Prospect.Game.World;

namespace Prospect.Game.Client;

public enum SessionState : byte
{
    /// <summary>Waiting for the transport to reach the server.</summary>
    Connecting,

    /// <summary>Connected; waiting for the server to accept the handshake.</summary>
    Handshaking,

    /// <summary>Accepted; downloading the immutable server catalogue.</summary>
    Downloading,

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

    /// <summary>The server has a password and none was given. Join again with one.</summary>
    PasswordRequired,

    /// <summary>The password given is not the server's.</summary>
    WrongPassword,

    /// <summary>
    /// Too many wrong passwords came from this address, or too many connections in a short time.
    /// <see cref="SessionSnapshot.RetryAfterSeconds"/> says how long the server wants this address to wait.
    /// </summary>
    TooManyAttempts,

    /// <summary>
    /// The game's content files are not the server's: the same version, with other blocks, rules, designs or
    /// galaxy rules. Nothing a retry changes; the files have to match.
    /// </summary>
    ContentMismatch,
}

/// <summary>
/// What the render thread knows about the session: a copy published by the client simulation each tick. The render
/// thread reads these and never touches simulation state.
/// </summary>
public sealed class SessionSnapshot(GameContent content)
{
    /// <summary>Roster, recent chat, party status, invitation and refusal for the interface.</summary>
    public readonly SocialState Social = new();

    /// <summary>
    /// Where the player's edit of their hull stands, as the server decides it: whether the draft may be committed,
    /// what was refused and why, how long to wait, and after a rebuild the old and the new hull.
    /// </summary>
    public EditorView Editor;
    public NewShipView NewShip;

    /// <summary>True while the player flies their escape drone: the one hull that may ask for a new ship.</summary>
    public bool OwnIsDrone;
    /// <summary>Personal retained module surveys and active operation feedback.</summary>
    public readonly ModuleKnowledgeView Modules = new();
    public readonly ExplorationView Exploration = new(content.Exploration.ChartsPerOwner);

    public SessionState State;
    public SessionEndReason EndReason;
    public uint PlayerId;
    public string ServerName = "";
    public ulong GalaxySeed;
    public int PlayerCount;

    /// <summary>Player slots on the server, once it has said so.</summary>
    public int MaxPlayers;

    /// <summary>Whether the game is public, unlisted or private, as the server says.</summary>
    public ServerVisibility Visibility;

    /// <summary>True when joining the server over the network takes a password.</summary>
    public bool PasswordProtected;

    /// <summary>With <see cref="SessionEndReason.TooManyAttempts"/>: seconds to wait before trying a password again.</summary>
    public int RetryAfterSeconds;

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
    /// what the simulation uses. A content never changes and is safe to read from any thread. When a design is
    /// added during play this becomes another content with that design at the end: every earlier design keeps
    /// its number and is the same object, and every hull in <see cref="Hulls"/> is of a design in this content.
    /// </summary>
    public GameContent Content = content;

    /// <summary>The hulls to draw this tick; only the first <see cref="HullCount"/> entries are meaningful.</summary>
    public readonly HullView[] Hulls = new HullView[ClientWorld.MaxViews];
    public int HullCount;

    /// <summary>
    /// The memory behind every <see cref="HullView.Blocks"/> of this snapshot: each viewed hull's block set,
    /// copied here when the snapshot is filled, so the render thread reads blocks without reaching into the
    /// session's stores. Read it through the views. It starts with a word per view and is replaced by a larger
    /// one only when the hulls in view need more.
    /// </summary>
    internal ulong[] BlockWords = new ulong[ClientWorld.MaxViews];

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
    // Reserve the full fleet message, one five-byte Control, one editor request and one design acknowledgement
    // before allowing any social traffic.
    internal const int EditorBytesPerTick = EditorRequestMessage.MaxWireSize + DesignReadyMessage.WireSize;
    internal const int SocialBytesPerTick = ServerHost.PlayerBytesPerTick - FleetOrdersMessage.MaxWireSize - 5 - EditorBytesPerTick - ExplorationMessage.ScanWireSize - ModuleMessage.RequestWireSize;
    internal static readonly int SocialMessagesPerTick = Math.Min(ServerHost.PlayerMessagesPerTick - 6, SocialBytesPerTick / SocialMessage.MaxWireSize);
    private long _receivedSnapshotTick = -1;
    private long _clientTick;
    private long _sentReliableTick = -1;

    private const int PingIntervalTicks = TickLoop.SimulationHz / 2;
    private const int ConnectTimeoutTicks = TickLoop.SimulationHz * 10;

    private readonly SocialState _social = new();
    private readonly ITransport _transport;

    // The content as loaded, and as the server's settings make it once the server has sent them.
    private readonly DesignCatalogue _cache;
    private DesignDownload? _download;
    private long _downloadDeadline;

    // Designs the server adds during play, and which of them the server has been told this client can show:
    // every design below _readySent, and failure for the ones below _failSent that it cannot.
    private DesignAppend? _append;
    private int _readySent, _failSent;
    private readonly EditorClient _editor = new();
    private readonly GameContent _loaded;
    private GameContent _content;
    private readonly ClientWorld _world;
    private Galaxy? _galaxy;
    private readonly string _playerName;
    // Held only until it has been used to answer the server's challenge, or the session ends without one.
    private string? _password;
    private bool _challenged;
    private readonly TickLoop _loop;
    private readonly SnapshotExchange<SessionSnapshot> _snapshots;
    private readonly byte[] _sendBuffer = new byte[1024];

    private SessionState _state = SessionState.Connecting;
    private SessionEndReason _endReason;
    private uint _playerId;
    private string _serverName = "";
    private ulong _galaxySeed;
    private int _playerCount;
    private int _maxPlayers;
    private ServerVisibility _visibility;
    private bool _passwordProtected;
    private int _retryAfterSeconds;
    private float _roundTripMs = -1;

    // Clock sync: the server tick reported by the last pong and the local time at which it was (estimated) current.
    private long _syncServerTick;
    private long _syncTimestamp;
    private readonly TimeProvider _clock;

    private volatile bool _leaveRequested;

    /// <param name="transport">The link to the server.</param>
    /// <param name="content">The game's content as the files have it.</param>
    /// <param name="playerName">The player's callsign.</param>
    /// <param name="password">
    /// The server's password, if the player gave one. It is only ever used to answer the server's challenge
    /// (<see cref="Passwords"/>) and never sent. One that no server could have counts as none. The session
    /// lets go of it as soon as it has answered, been let in or ended. That is all a managed string allows:
    /// it cannot be overwritten, so the text stays in memory until the collector frees it, and whoever handed
    /// it over may still hold it.
    /// </param>
    public ClientSession(ITransport transport, GameContent content, string playerName, string? password = null, TimeProvider? clock = null)
    {
        _clock = clock ?? TimeProvider.System;
        _password = password is not null && Passwords.IsUsable(password) ? password : null;
        _transport = transport;
        _loaded = content;
        _cache = new DesignCatalogue(content);
        _content = content;
        _snapshots = new SnapshotExchange<SessionSnapshot>(() => new SessionSnapshot(content));
        _world = new ClientWorld(content);
        _playerName = PlayerNames.Sanitize(playerName);
        Input.MaxCommitBytes = content.BlockTransfers.MaxBytes;
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
        _download?.Dispose();
        _download = null;
        _append?.Dispose();
        _append = null;
        _editor.Clear();
        _password = null;
        try { _transport.Dispose(); }
        finally { _world.Dispose(); }
    }

    void ITickHandler.OnStart() => Logs.Net.Info($"Session starting on {_transport.Description}");

    void ITickHandler.Tick(long tick)
    {
        _clientTick = tick;
        if (_state != SessionState.Ended)
        {
            _transport.Poll(this);

            if (_state == SessionState.Downloading)
            {
                _download!.Tick(_transport, _sendBuffer, tick);
                if (_download.Failed || tick > _downloadDeadline) End(SessionEndReason.ContentMismatch);
                else if (_download.Result is { } ready)
                {
                    if (!ReferenceEquals(_content, ready)) { _content = ready; _world.SetContent(ready); }
                    _sendBuffer[0] = (byte)MessageId.CatalogueReady;
                    if (_transport.Send(0, NetChannel.Reliable, _sendBuffer.AsSpan(0, 1)))
                    {
                        _state = SessionState.Active;
                        _append = new DesignAppend(_content, _cache, _content.Designs.Length, _download.TotalBytes);
                        _readySent = _failSent = _content.Designs.Length;
                        _download.Dispose(); _download = null;
                        SendPing();
                    }
                }
            }
            if (_leaveRequested)
                End(SessionEndReason.LeftByPlayer);
            else if (_state is SessionState.Connecting or SessionState.Handshaking && tick > ConnectTimeoutTicks)
                End(SessionEndReason.ConnectFailed);
            else if (_state == SessionState.Active && tick % PingIntervalTicks == 0)
                SendPing();

            if (_state == SessionState.Active)
            {
                // Repeated catch-up steps without a new server snapshot share one reliable allowance.
                var sendCommands = _receivedSnapshotTick > _sentReliableTick;
                if (sendCommands) _sentReliableTick = _receivedSnapshotTick;
                for (var sent = 0; sendCommands && sent < SocialMessagesPerTick && Input.ConsumeSocial(out var message); sent++)
                {
                    var writer = new NetWriter(_sendBuffer);
                    message.Write(ref writer);
                    if (!_transport.Send(0, NetChannel.Reliable, writer.Written))
                    {
                        End(SessionEndReason.ConnectionLost);
                        break;
                    }
                }
                if (_state == SessionState.Active)
                {
                    // A design the server added is put at the end of the catalogue between ticks, once it has
                    // been checked. Nothing that is known changes: the world goes on with what it had.
                    if (_append!.Tick(_content.Designs.Length, _editor, _transport, _sendBuffer, tick) is { } added)
                    {
                        _content = _content.WithDesign(added);
                        _world.AppendContent(_content);
                    }

                    _editor.Tick(Input, _transport, sendCommands, _content.BlockTransfers.MaxBytes);
                    if (sendCommands)
                        ReportDesigns();
                    _world.Tick(Input, _transport, EstimateServerTick(_clock.GetTimestamp()), sendCommands, tick, _roundTripMs);
                }
            }
        }

        var snapshot = _snapshots.WriteBuffer;
        var now = _clock.GetTimestamp();
        _social.CopyTo(snapshot.Social);
        snapshot.Editor = _editor.View;
        snapshot.NewShip = _editor.NewShip;
        snapshot.State = _state;
        snapshot.EndReason = _endReason;
        snapshot.PlayerId = _playerId;
        snapshot.ServerName = _serverName;
        snapshot.GalaxySeed = _galaxySeed;
        snapshot.PlayerCount = _playerCount;
        snapshot.MaxPlayers = _maxPlayers;
        snapshot.Visibility = _visibility;
        snapshot.PasswordProtected = _passwordProtected;
        snapshot.RetryAfterSeconds = _retryAfterSeconds;
        snapshot.ClientTick = tick;
        var serverTick = _state == SessionState.Active ? EstimateServerTick(now) : 0;
        snapshot.ServerTick = (long)serverTick;
        _world.CopyModules(snapshot.Modules);
        _world.CopyExploration(snapshot.Exploration, (long)serverTick);
        snapshot.ServerTickPrecise = serverTick;
        snapshot.RockCount = _state == SessionState.Active ? _world.CopyRockDeltas(snapshot.Rocks) : 0;
        snapshot.Galaxy = _galaxy;
        snapshot.Content = _content;
        snapshot.HullCount = _state == SessionState.Active ? _world.BuildViews(snapshot.Hulls, ref snapshot.BlockWords, serverTick, tick, out snapshot.OwnHull) : 0;
        if (snapshot.HullCount == 0)
            snapshot.OwnHull = -1;
        snapshot.OwnIsDrone = _state == SessionState.Active && snapshot.HullCount > 0 && _world.OwnIsDrone;
        snapshot.Dampeners = _world.Dampeners;
        snapshot.PredictionError = _world.PredictionError;
        snapshot.Flight = _world.Flight;
        snapshot.RoundTripMs = _roundTripMs;
        snapshot.PublishedAt = Stopwatch.GetTimestamp();
        snapshot.Loop = _loop.Stats;
        _snapshots.Publish();
    }

    private double EstimateServerTick(long now) =>
        _syncServerTick + (now - _syncTimestamp) * (double)TickLoop.SimulationHz / _clock.TimestampFrequency;

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
        var hello = new HelloMessage { ProtocolVersion = ProtocolInfo.Version, PlayerName = _playerName, Content = _loaded.Identity };
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
                _state = SessionState.Downloading;
                _password = null;
                _playerId = welcome.PlayerId;
                _serverName = welcome.ServerName;
                _galaxySeed = welcome.GalaxySeed;
                _visibility = welcome.Visibility;
                _passwordProtected = welcome.PasswordProtected;
                _maxPlayers = welcome.MaxPlayers;
                _social.Roster = new SocialMessage[_maxPlayers + 32];

                // The server's world, not this machine's files, says how hard things hit: predict by its settings.
                _content = _loaded.WithSettings(welcome.Settings);
                _download = new DesignDownload(_content, _cache);
                _downloadDeadline = _clientTick + _content.BlockTransfers.AdmissionTicks;
                _galaxy = _content.Galaxy.WithSeed(welcome.GalaxySeed);
                _world.SetGalaxy(_galaxy);
                _world.SetPlayer(welcome.PlayerId);
                _world.SetOwnHull(welcome.HullId);
                _playerCount = 1;
                _syncServerTick = welcome.ServerTick;
                _syncTimestamp = _clock.GetTimestamp();
                Logs.Net.Info($"Joined '{_serverName}' as player {_playerId}");
                SendPing();
                break;

            case MessageId.DesignData when _state == SessionState.Downloading && channel == NetChannel.Bulk:
                _download!.Receive(ref reader);
                break;

            case MessageId.DesignData when _state == SessionState.Active && channel == NetChannel.Bulk:
                _append!.OnData(ref reader, _content.Designs.Length, _clientTick);
                break;

            case MessageId.DesignNotice when _state == SessionState.Active && channel == NetChannel.Reliable:
                _append!.OnNotice(ref reader);
                break;

            case MessageId.EditorState when _state == SessionState.Active && channel == NetChannel.Reliable:
                _editor.OnState(ref reader);
                break;

            case MessageId.Challenge when _state == SessionState.Handshaking && channel == NetChannel.Reliable:
                AnswerChallenge(ref reader);
                break;

            case MessageId.Reject when _state == SessionState.Handshaking:
                {
                    RejectMessage.TryRead(ref reader, out var reject);
                    _retryAfterSeconds = reject.Reason == RejectReason.TooManyAttempts ? reject.RetryAfterSeconds : 0;

                    // The server's log has its own: comparing the two is how a host and a player find which side changed.
                    if (reject.Reason == RejectReason.ContentMismatch)
                        Logs.Net.Info($"The server plays other content than this game's {_loaded.Identity}");
                    End(
                        reject.Reason switch
                        {
                            RejectReason.VersionMismatch => SessionEndReason.VersionMismatch,
                            RejectReason.ServerFull => SessionEndReason.ServerFull,
                            RejectReason.WrongPassword => SessionEndReason.WrongPassword,
                            RejectReason.TooManyAttempts => SessionEndReason.TooManyAttempts,
                            RejectReason.ContentMismatch => SessionEndReason.ContentMismatch,
                            _ => SessionEndReason.Refused,
                        }
                    );
                    break;
                }

            case MessageId.Social when _state == SessionState.Active && channel == NetChannel.Reliable:
                if (SocialMessage.TryRead(ref reader, out var social)
                    && (social.Kind != SocialKind.Member || (social.Value < _content.Designs.Length && social.Shield is >= 0 and <= 1 && social.Hull is >= 0 and <= 1)))
                    _social.Apply(social);
                break;

            case MessageId.Navigation when _state == SessionState.Active && channel == NetChannel.Reliable:
                _world.OnNavigation(ref reader);
                break;
            case MessageId.ModuleSurvey when _state == SessionState.Active && channel == NetChannel.Reliable:
                _world.OnModuleSurvey(ref reader);
                break;
            case MessageId.Exploration when _state == SessionState.Active && channel == NetChannel.Reliable:
                _world.OnExploration(ref reader);
                break;
            case MessageId.Visibility when _state == SessionState.Active && channel == NetChannel.Reliable:
                _world.OnVisibility(ref reader);
                break;
            case MessageId.RockDeltas when _state == SessionState.Active && channel == NetChannel.Reliable:
                _world.OnRockDeltas(ref reader);
                break;

            case MessageId.VisualState when _state == SessionState.Active && channel == NetChannel.Bulk:
                _world.OnBlockState(ref reader, _clientTick, true);
                break;
            case MessageId.BlockState when _state == SessionState.Active && channel == NetChannel.Bulk:
                _world.OnBlockState(ref reader, _clientTick);
                break;
            case MessageId.Snapshot when _state == SessionState.Active:
                var headerReader = reader;
                if (SnapshotHeader.TryRead(ref headerReader, out var header))
                    _receivedSnapshotTick = Math.Max(_receivedSnapshotTick, header.ServerTick);
                _world.OnSnapshot(ref reader, _clientTick);
                break;

            case MessageId.Pong when _state == SessionState.Active && PongMessage.TryRead(ref reader, out var pong):
                {
                    var now = _clock.GetTimestamp();
                    var roundTrip = now - pong.ClientTimestamp;
                    if (roundTrip < 0 || roundTrip > _clock.TimestampFrequency * 10 || pong.ServerTick < 0 || pong.ServerTick > long.MaxValue - 1024)
                        break;

                    var ms = (float)(roundTrip * 1000.0 / _clock.TimestampFrequency);
                    _roundTripMs = _roundTripMs < 0 ? ms : _roundTripMs + (ms - _roundTripMs) * 0.2f;
                    _playerCount = pong.PlayerCount;

                    // The server stamped its tick half a round trip ago.
                    _syncServerTick = pong.ServerTick;
                    _syncTimestamp = now - roundTrip / 2;
                    break;
                }
        }
    }

    /// <summary>
    /// The server has a password and sent its salt and a challenge. With a password in hand the session proves
    /// it knows it; without one it ends, saying a password is needed, so the player can be asked and join again.
    /// </summary>
    private void AnswerChallenge(ref NetReader reader)
    {
        Span<byte> salt = stackalloc byte[Passwords.SaltSize];
        Span<byte> challenge = stackalloc byte[Passwords.ChallengeSize];
        if (!ChallengeMessage.TryRead(ref reader, salt, challenge))
        {
            End(SessionEndReason.Refused);
            return;
        }

        // One answer per session. Deriving the key is slow on purpose, and a server that keeps asking must not
        // be able to keep this thread busy with it, or collect proofs for challenges of its choosing.
        if (_challenged)
        {
            End(SessionEndReason.Refused);
            return;
        }

        if (_password is null)
        {
            End(SessionEndReason.PasswordRequired);
            return;
        }

        // The password has done its work once the key is derived from it: the session lets go of it here.
        _challenged = true;
        Span<byte> key = stackalloc byte[Passwords.KeySize];
        Span<byte> proof = stackalloc byte[Passwords.ProofSize];
        Passwords.DeriveKey(_password, salt, key);
        _password = null;
        Passwords.ComputeProof(key, salt, challenge, proof);
        System.Security.Cryptography.CryptographicOperations.ZeroMemory(key);

        var writer = new NetWriter(_sendBuffer);
        ProofMessage.Write(ref writer, proof);
        _transport.Send(0, NetChannel.Reliable, writer.Written);
    }

    /// <summary>
    /// Tells the server, one design a tick, which of the designs it added this client can show: a design that has
    /// been checked and decoded here and whose mesh the renderer has installed, as it reports through
    /// <see cref="PlayerInputBridge.ReportInstalledDesigns"/>. Until then the server sends no hull of it. A design
    /// this client could not take, or could not build a mesh for, is reported as that, so the server does not
    /// keep an edit waiting for it.
    /// </summary>
    private void ReportDesigns()
    {
        var append = _append!;
        if (_readySent < append.Announced && _readySent < _content.Designs.Length && _readySent < Input.InstalledDesigns)
        {
            if (SendDesignReady(_readySent, true))
                _readySent++;
            return;
        }

        var id = Math.Max(_readySent, _failSent);
        if (id >= append.Announced)
            return;
        var undecoded = append.Failed && id >= _content.Designs.Length;
        var unbuilt = id >= Input.InstalledDesigns && id < Input.FailedDesigns;
        if ((undecoded || unbuilt) && SendDesignReady(id, false))
            _failSent = id + 1;
    }

    private bool SendDesignReady(int design, bool ready)
    {
        var writer = new NetWriter(_sendBuffer);
        DesignReadyMessage.Write(ref writer, (byte)design, ready);
        return _transport.Send(0, NetChannel.Reliable, writer.Written);
    }

    private void SendPing()
    {
        var ping = new PingMessage { ClientTimestamp = _clock.GetTimestamp() };
        var writer = new NetWriter(_sendBuffer);
        ping.Write(ref writer);
        _transport.Send(0, NetChannel.Unreliable, writer.Written);
    }

    private void End(SessionEndReason reason)
    {
        _download?.Dispose(); _download = null;
        _append?.Dispose(); _append = null;
        _editor.Clear();
        _transport.Disconnect(0);
        _state = SessionState.Ended;
        _social.Disconnect();
        _endReason = reason;
        _password = null;
        Logs.Net.Info($"Session ended: {reason}");
    }
}
