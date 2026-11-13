using System.Diagnostics;
using System.Security.Cryptography;
using Prospect.Engine.Diagnostics;
using Prospect.Engine.Net;
using Prospect.Engine.Time;
using Prospect.Game.Lobby;
using Prospect.Game.Protocol;
using Prospect.Game.World;

namespace Prospect.Game.Server;

public sealed class ServerConfig
{
    /// <summary>
    /// The name the host chose, shown to players and on the list. It must pass
    /// <see cref="ServerNames.TryNormalize"/>: 1 to 48 characters once cleaned, as the listing server demands.
    /// </summary>
    public string Name { get; init; } = ServerNames.Default;

    /// <summary>Player slots, the host of a local game included.</summary>
    public int MaxPlayers { get; init; } = 16;

    /// <summary>
    /// Who can find and join the game. Private opens no network listener at all; unlisted and public listen on
    /// UDP <see cref="Port"/>; only public announces itself to <see cref="Listing"/>.
    /// </summary>
    public ServerVisibility Visibility { get; init; } = ServerVisibility.Private;

    /// <summary>
    /// The password a player joining over the network must know, or null for none. It never travels: see
    /// <see cref="Passwords"/>. The host's own client, on the in-process link, is not asked for it.
    /// </summary>
    public string? Password { get; init; }

    /// <summary>How many connections may be waiting to be let in, and how often one address may open one.</summary>
    public ConnectionLimits Limits { get; init; } = ConnectionLimits.Default;

    /// <summary>The UDP port an unlisted or public game listens on.</summary>
    public ushort Port { get; init; } = ProtocolInfo.DefaultPort;

    /// <summary>The listing server a public game announces itself to, or null to announce nowhere.</summary>
    public ListingConfig? Listing { get; init; }
    public ulong GalaxySeed { get; init; } = 0x50524F5350454354;

    /// <summary>
    /// The world's settings, fixed for as long as the server runs; null for the defaults in the content. Build
    /// them with the content's <see cref="GameContent.SettingCatalog"/>.
    /// </summary>
    public WorldSettings? Settings { get; init; }

    /// <summary>The sector new players arrive in the middle of. The home sector unless a server is set up otherwise.</summary>
    public SectorCoord SpawnSector { get; init; }

    /// <summary>The clock handshake deadlines and lock-outs are measured by. A test moves its own.</summary>
    internal TimeProvider Clock { get; init; } = TimeProvider.System;
}

/// <summary>A point-in-time view of the server, readable from any thread.</summary>
public readonly record struct ServerStatus(long Tick, int PlayerCount, TickStats Loop);

/// <summary>
/// The authoritative simulation. The dedicated server binary and a singleplayer game both run this class on its
/// own thread; the only difference is which transports it is given. Everything in here executes on the tick thread.
/// </summary>
public sealed partial class ServerHost : ITickHandler, ITransportHandler, IDisposable
{
    /// <summary>
    /// Messages one player may have handled in one tick, and their bytes. A client sends an input and now and
    /// then a probe or an order: two or three small messages a tick, a handful after a hitch. What a player
    /// sends beyond the unreliable allowance is dropped unread. Reliable traffic has a separate allowance
    /// of the same size; exceeding it disconnects rather than silently dropping commands.
    /// </summary>
    internal const int PlayerMessagesPerTick = 32;

    internal const int PlayerBytesPerTick = 8 * 1024;

    /// <summary>
    /// What one poll of a network transport may take in besides every player's own allowance: connection
    /// events, handshakes, and whatever arrives for nobody. See <see cref="ITransport.Poll"/>.
    /// </summary>
    internal const int SparePacketsPerTick = 1024;

    internal const int SpareBytesPerTick = 256 * 1024;

    /// <summary>
    /// A player who is over the allowance gains this many strikes that tick and loses one in every tick they
    /// are not; at <see cref="FloodStrikeLimit"/> they are removed. A burst after a hitch is forgiven, half a
    /// second of flooding is not.
    /// </summary>
    private const int FloodStrikesPerTick = 4;

    private const int FloodStrikeLimit = 120;

    /// <summary>
    /// Room in the network layer's own table on top of the connections this server holds, for connections
    /// that layer is still opening or closing. See <see cref="RakNetTransport.Listen"/>.
    /// </summary>
    private const int HalfOpenSlots = 512;

    /// <summary>Refused connections are reported in one line at most this often, however many there are.</summary>
    private const int RefusalLogSeconds = 5;

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

        /// <summary>The hello was good and the server has a password: a challenge is out and the proof is awaited.</summary>
        AwaitingProof,
        Active,
    }

    private sealed class Connection
    {
        public ConnectionState State;
        public ITransport Transport = null!;
        public int Peer;
        public uint PlayerId;
        public uint Party, Inviter;
        public ulong InvitationId;
        public long InviteUntil, SocialAt;
        public double SocialCredit;
        public bool SocialFailed;
        public BlockSender? Blocks;
        public bool CatalogueReady;
        public long CatalogueDeadline;

        /// <summary>
        /// How many designs the manifest this client was sent lists; zero until it has asked for one. Designs from
        /// that number on were added later and are announced one by one.
        /// </summary>
        public int ManifestDesigns;

        /// <summary>One past the last design announced to the client.</summary>
        public int NoticedDesigns;

        /// <summary>
        /// The designs the client can show, and the ones it said it cannot, by design number. The client is sent
        /// no hull of a design it is not ready for.
        /// </summary>
        public readonly ulong[] ReadyDesigns = new ulong[4];
        public readonly ulong[] FailedDesigns = new ulong[4];

        /// <summary>Where the player's edit stands; see <see cref="ServerHost.TickEditor"/>.</summary>
        public EditorPhase EditPhase;

        /// <summary>The edit's name, the hull it is of, and the tick it was allowed in: damage from then on ends it.</summary>
        public uint EditToken;
        public HullHandle EditHull;
        public long EditStartTick;

        /// <summary>When the upload, the owner's client or the other clients run out of time, by stage.</summary>
        public long EditDeadline;

        /// <summary>The earliest tick the player may commit another design.</summary>
        public long EditNextCommit;

        /// <summary>The buffer the committed design arrives in, while it arrives and is checked.</summary>
        public EditUpload? EditUpload;

        /// <summary>The design the hull will be replaced with, and whether its owner's client is ready for it.</summary>
        public byte EditDesign;
        public bool EditOwnerReady;

        /// <summary>The newest answer about the edit, and whether the transport has yet to take it.</summary>
        public EditorStateMessage EditReply;
        public bool EditReplyDirty;
        public string Name = "";

        /// <summary>The name a hello gave, cleaned, kept here without allocating until the player is let in.</summary>
        public readonly char[] NameChars = new char[ProtocolInfo.MaxNameChars];
        public int NameLength;

        /// <summary>The moment, by the server's clock, at which a connection still shaking hands is dropped.</summary>
        public long Deadline;

        /// <summary>Messages and bytes this player has sent in the current tick, and whether that was too many.</summary>
        public int TickMessages;
        public int TickBytes;
        public int ReliableMessages;
        public int ReliableBytes;
        public bool OverAllowance;
        public int FloodStrikes;

        /// <summary>The random bytes this connection was challenged with; meaningful only while a proof is awaited.</summary>
        public readonly byte[] Challenge = new byte[Passwords.ChallengeSize];

        /// <summary>The address the connection comes from, as connections and wrong passwords are counted by.</summary>
        public ulong Origin;

        public bool Pending => State is ConnectionState.AwaitingHello or ConnectionState.AwaitingProof;

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
            Party = Inviter = 0;
            InvitationId = 0;
            InviteUntil = SocialAt = 0;
            SocialCredit = 0;
            SocialFailed = false;
            Blocks = null;
            CatalogueReady = false;
            ManifestDesigns = NoticedDesigns = 0;
            Array.Clear(ReadyDesigns);
            Array.Clear(FailedDesigns);
            EditPhase = EditorPhase.Closed;
            EditToken = 0;
            EditHull = default;
            EditStartTick = EditDeadline = EditNextCommit = 0;
            EditUpload = null;
            EditDesign = 0;
            EditOwnerReady = false;
            EditReply = default;
            EditReplyDirty = false;
            State = ConnectionState.Free;
            Transport = null!;
            Name = "";
            NameLength = 0;
            Deadline = 0;
            TickMessages = TickBytes = 0;
            ReliableMessages = ReliableBytes = 0;
            OverAllowance = false;
            FloodStrikes = 0;
            Origin = 0;
            Array.Clear(Challenge);
            Hull = default;
            InputHead = InputCount = 0;
            LastQueuedSequence = LastSimulatedSequence = 0;
            InputCredit = StarvedTicks = 0;
            LastInput = new FlightInput { Dampeners = true };
            OrderId = 0;
            Order = default;
        }
    }

    /// <summary>Why a connection was turned away, for the log; the first few are what the client is told.</summary>
    private enum Refusal : byte
    {
        VersionMismatch,
        ServerFull,
        BadHandshake,
        WrongPassword,
        LockedOut,
        ContentMismatch,
        TimedOut,
        TooFast,
        AddressBusy,
        DoorFull,
        Count,
    }

    // What the server needs of its configuration. The password is not among it: only the key derived from it is kept.
    private readonly int _maxPlayers;
    private readonly ServerVisibility _visibility;
    private readonly ulong _galaxySeed;
    private readonly SectorCoord _spawnSector;
    private readonly string? _listingHost;
    private readonly ConnectionLimits _limits;
    private readonly TimeProvider _clock;
    private readonly long _handshakeTime;
    private readonly ITransport[] _transports;
    private readonly Connection[] _connections;
    private readonly TickLoop _loop;
    private readonly DesignCatalogue _catalogue;
    private readonly byte[] _sendBuffer = new byte[64 * 1024];
    private readonly HullStore _hulls = new();
    private readonly HullField _field;

    // Replaced, never changed, when a design is added: the same content with one more design at the end.
    private GameContent _content;
    private readonly RockField _rocks;
    private readonly RockImpulses _impulses = new();
    private readonly string _name;

    // The password as the handshake uses it: the key derived from it under this run's salt. Null without one.
    // The key sits in an array the collector never moves, so clearing it on disposal clears the only copy.
    private readonly byte[] _salt = new byte[Passwords.SaltSize];
    private readonly byte[]? _passwordKey;
    private readonly AttemptLimiter _attempts;
    private readonly ConnectionThrottle _throttle;
    private readonly ListingAnnouncer? _announcer;

    // Refusals since the last line about them in the log, by reason, and when the next line may be written.
    private readonly int[] _refused = new int[(int)Refusal.Count];
    private int _refusedCount;
    private int _budgetTicks;
    private long _nextRefusalLog;

    private long _tick;
    private uint _nextPlayerId = 1;
    private int _playerCount;

    /// <summary>
    /// Sets up a server. <paramref name="transports"/> are the links the caller brings: the in-process one of a
    /// local game, or one a test made. An unlisted or public server opens its own UDP listener on top of them.
    /// Throws <see cref="InvalidDataException"/> for a name or password that cannot be used, and
    /// <see cref="IOException"/> when the UDP port cannot be opened.
    /// </summary>
    public ServerHost(ServerConfig config, GameContent content, params ITransport[] transports)
    {
        ITransport? listener = null;
        try
        {
            _maxPlayers = config.MaxPlayers;
            _visibility = config.Visibility;
            _galaxySeed = config.GalaxySeed;
            _spawnSector = config.SpawnSector;
            _listingHost = config.Listing?.Url.Host;
            _limits = config.Limits;
            _clock = config.Clock;
            _handshakeTime = (long)(_limits.HandshakeSeconds * _clock.TimestampFrequency);
            _attempts = new AttemptLimiter(_clock.TimestampFrequency);
            _limits.Validate();
            if (!ServerNames.TryNormalize(config.Name, out _name))
                throw new InvalidDataException($"A server's name must be 1 to {ServerNames.MaxLength} characters.");
            if (config.Password is not null && !Passwords.IsUsable(config.Password))
                throw new InvalidDataException($"A server's password must be 1 to {Passwords.MaxChars} characters, without control characters.");
            if (config.MaxPlayers is < 1 or > ushort.MaxValue)
                throw new InvalidDataException($"A server has 1 to {ushort.MaxValue} player slots.");

            if (config.Password is not null)
            {
                RandomNumberGenerator.Fill(_salt);
                _passwordKey = GC.AllocateArray<byte>(Passwords.KeySize, pinned: true);
                Passwords.DeriveKey(config.Password, _salt, _passwordKey);
            }

            // A private game is the host alone: no socket is opened, so there is nothing to connect to. The
            // listener has room for every player and every pending connection, and a few that are refused in
            // the moment they arrive.
            if (config.Visibility != ServerVisibility.Private)
            {
                listener = RakNetTransport.Listen(config.Port, config.MaxPlayers + _limits.PendingTotal + 8, HalfOpenSlots);
                transports = [.. transports, listener];
            }

            _transports = transports;
            _throttle = new ConnectionThrottle(_limits.ConnectionBurst, _limits.ConnectionsPerSecond, _clock.TimestampFrequency);
            if (config.Visibility == ServerVisibility.Public && config.Listing is { } listing)
            {
                _announcer = new ListingAnnouncer(
                    listing,
                    _name,
                    config.Port,
                    config.MaxPlayers,
                    _passwordKey is not null,
                    () => Volatile.Read(ref _playerCount)
                );
            }

            // From here on the server only knows the content as its own world's settings make it.
            _content = content.WithSettings(config.Settings ?? content.SettingCatalog.Defaults);
            _catalogue = new DesignCatalogue(_content);
            _startupDesigns = _catalogue.Count;
            _uploads = new EditUpload[_content.Editor.Uploads];
            for (var i = 0; i < _uploads.Length; i++)
                _uploads[i] = new EditUpload(_content.BlockTransfers.MaxBytes);
            Galaxy = _content.Galaxy.WithSeed(config.GalaxySeed);
            _rocks = new RockField(Galaxy);

            // Room for the largest design the editor can add, taken now: a new design costs the tick no memory.
            _field = new HullField(_hulls, _content.Designs, reserveBlocks: Math.Min(_content.Blocks.Limits.MaxBlocks, GeometryAdmission.MaxBlocks));

            // A slot for every player, and apart from those one for every connection that may be pending: however
            // many stand at the door, there is a slot for whoever is let in. The in-process links are not counted
            // among the pending, so each has a slot of its own.
            var inProcess = 0;
            foreach (var transport in transports)
                inProcess += transport.IsInProcess ? 1 : 0;
            _connections = new Connection[config.MaxPlayers + _limits.PendingTotal + inProcess];
            for (var i = 0; i < _connections.Length; i++)
            {
                _connections[i] = new Connection();
                _connections[i].Reset();
            }

            _loop = new TickLoop("Server", TickLoop.SimulationHz, this);
        }
        catch
        {
            if (_passwordKey is not null)
                CryptographicOperations.ZeroMemory(_passwordKey);
            try { listener?.Dispose(); }
            finally
            {
                try { _announcer?.Dispose(); }
                finally { _hulls.Dispose(); }
            }
            throw;
        }
    }

    public Galaxy Galaxy { get; }

    /// <summary>The content as this server's world plays by it: its settings and the figures that follow from them.</summary>
    public GameContent Content => _content;

    /// <summary>Where new players appear: the middle of the spawn sector, which every sector's asteroids keep clear.</summary>
    private Double3 SpawnPoint => Galaxy.CenterOf(_spawnSector);

    public ServerStatus Status => new(Volatile.Read(ref _tick), Volatile.Read(ref _playerCount), _loop.Stats);

    /// <summary>The exception that stopped the server, if a tick threw.</summary>
    public Exception? Fault => _loop.Fault;

    public bool IsRunning => _loop.IsRunning;

    /// <summary>The server's name as players see it.</summary>
    public string Name => _name;

    /// <summary>Who can find and join this server.</summary>
    public ServerVisibility Visibility => _visibility;

    /// <summary>Player slots, the host of a local game included.</summary>
    public int MaxPlayers => _maxPlayers;

    /// <summary>Where a public server stands with the listing server; off for any other. Safe from any thread.</summary>
    public AnnounceState Announce => _announcer?.State ?? AnnounceState.Off;

    public void Start()
    {
        _loop.Start();
        _announcer?.Start();
    }

    /// <summary>
    /// Stops the tick thread, closes every transport and frees the world. A public server also takes itself
    /// off the list, which may take a few seconds when the listing server is slow.
    ///
    /// The password's key is cleared here. That is what this class can promise: it never kept the password, and
    /// its one copy of the key is gone when it is disposed. It cannot promise that the password is nowhere in
    /// the process. The text it was derived from is a managed string owned by whoever configured the server:
    /// it cannot be overwritten, it lives until the collector frees it, and the command line, the environment
    /// or the settings file it came from still holds it.
    /// </summary>
    public void Dispose()
    {
        _loop.Stop();
        StopEditor();
        // Clear secrets before any external cleanup can throw.
        if (_passwordKey is not null)
            CryptographicOperations.ZeroMemory(_passwordKey);
        foreach (var connection in _connections)
            CryptographicOperations.ZeroMemory(connection.Challenge);
        Exception? failure = null;
        foreach (var transport in _transports)
        {
            try { transport.Dispose(); }
            catch (Exception error) { failure ??= error; }
        }
        try { _announcer?.Dispose(); }
        finally { _hulls.Dispose(); }
        if (failure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    void ITickHandler.OnStart()
    {
        foreach (var transport in _transports)
            Logs.Sim.Info($"Server '{_name}' up on {transport.Description}, {_maxPlayers} player slots");
        Logs.Sim.Info(
            _visibility switch
            {
                ServerVisibility.Private => "Private: nobody can join",
                ServerVisibility.Unlisted => $"Unlisted: joined by address, {(_passwordKey is null ? "no password" : "with a password")}",
                _ when _announcer is null => $"Public, but there is no listing server to announce to; {(_passwordKey is null ? "no password" : "with a password")}",
                _ => $"Public: announcing to {_listingHost}, {(_passwordKey is null ? "no password" : "with a password")}",
            }
        );

        // A player turned away for other content can compare this with what their own log says.
        Logs.Sim.Info($"Content {_content.Identity}: a client joins only with the same blocks, rules, designs and galaxy rules");

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
        // First what must happen whatever arrives: handshakes that have run out of time end, by the clock, and
        // players who keep sending far more than a client does are removed. Network input is read after this
        // and within a budget, so no amount of it can put either off or keep the simulation from its turn.
        var now = _clock.GetTimestamp();
        foreach (var connection in _connections)
        {
            if (connection.Pending)
            {
                if (now >= connection.Deadline)
                    Refuse(connection, Refusal.TimedOut);
            }
            else if (connection.State == ConnectionState.Active)
            {
                if (connection.SocialFailed || (!connection.CatalogueReady && tick > connection.CatalogueDeadline))
                {
                    connection.Transport.Disconnect(connection.Peer);
                    RemovePlayer(connection);
                    connection.Reset();
                    continue;
                }
                connection.FloodStrikes = connection.OverAllowance ? connection.FloodStrikes + FloodStrikesPerTick : Math.Max(connection.FloodStrikes - 1, 0);
                connection.TickMessages = connection.TickBytes = 0;
                connection.ReliableMessages = connection.ReliableBytes = 0;
                connection.OverAllowance = false;
                if (connection.FloodStrikes >= FloodStrikeLimit)
                    RemoveFlooder(connection);
            }
        }

        // The host's own client, in this process, is read to the end. A network transport is read until its
        // budget is used up: room for every player's allowance and a fixed amount besides. What is still
        // waiting then stays in the transport's queue, in order, and is read in the next tick.
        var budget = new ReceiveBudget(SparePacketsPerTick + _maxPlayers * PlayerMessagesPerTick, SpareBytesPerTick + _maxPlayers * PlayerBytesPerTick);
        var carriedOver = false;
        foreach (var transport in _transports)
            carriedOver |= !transport.Poll(this, transport.IsInProcess ? ReceiveBudget.Unlimited : budget);
        if (carriedOver)
            _budgetTicks++;
        ReportRefusals(now);

        // Edits move on before the hulls step, so a hull that is replaced steps as the new one this tick.
        TickEditor(tick);

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
            if (connection.State == ConnectionState.Active)
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

        TickSocial(tick);
        Volatile.Write(ref _tick, tick + 1);
    }

    void ITickHandler.OnStop()
    {
        foreach (var connection in _connections)
        {
            if (connection.State != ConnectionState.Free)
                connection.Transport.Disconnect(connection.Peer);
            CloseEdit(connection);
            connection.Reset();
        }

        Volatile.Write(ref _playerCount, 0);
        Logs.Sim.Info($"Server '{_name}' stopped at tick {_tick}");
    }

    void ITransportHandler.OnConnected(ITransport transport, int peer)
    {
        var now = _clock.GetTimestamp();
        var origin = transport.IsInProcess ? 0 : transport.GetPeerOrigin(peer);

        // One pass: a free slot, and how many connections stand at the door, from this address and in all.
        Connection? free = null;
        var pending = 0;
        var pendingHere = 0;
        foreach (var connection in _connections)
        {
            if (connection.State == ConnectionState.Free)
                free ??= connection;
            else if (connection.Pending && !connection.Transport.IsInProcess)
            {
                pending++;
                if (connection.Origin == origin)
                    pendingHere++;
            }
        }

        // The limits come before a slot is taken and before anything the connection sends is read. The link to
        // the host's own client is not held to them: nothing from outside the process can arrive on it.
        if (!transport.IsInProcess)
        {
            if (!_throttle.TryTake(origin, now, out var wait))
            {
                TurnAway(transport, peer, Refusal.TooFast, wait);
                return;
            }

            if (pendingHere >= _limits.PendingPerAddress)
            {
                TurnAway(transport, peer, Refusal.AddressBusy, 1);
                return;
            }

            if (pending >= _limits.PendingTotal)
            {
                TurnAway(transport, peer, Refusal.DoorFull, 1);
                return;
            }
        }

        if (free is null)
        {
            transport.Disconnect(peer);
            return;
        }

        free.State = ConnectionState.AwaitingHello;
        free.Transport = transport;
        free.Peer = peer;
        free.Origin = origin;
        free.Deadline = now + _handshakeTime;
    }

    void ITransportHandler.OnDisconnected(ITransport transport, int peer, DisconnectReason reason)
    {
        var connection = Find(transport, peer);
        if (connection is null)
            return;

        if (connection.State == ConnectionState.Active)
        {
            RemovePlayer(connection);
            Logs.Sim.Info($"{connection.Name} (player {connection.PlayerId}) left: {reason}");
        }

        connection.Reset();
    }

    void ITransportHandler.OnMessage(ITransport transport, int peer, NetChannel channel, ReadOnlySpan<byte> payload)
    {
        var connection = Find(transport, peer);
        if (connection is null)
            return;

        // A connection that is shaking hands may send exactly what the handshake asks for next, once. Anything
        // else, an empty message included, ends it: it has no slot to idle in and nothing more of it is read.
        if (connection.State == ConnectionState.AwaitingHello)
        {
            if (!payload.IsEmpty && payload[0] == (byte)MessageId.Hello && channel == NetChannel.Reliable)
            {
                var hello = new NetReader(payload[1..]);
                Greet(connection, ref hello);
            }
            else
            {
                Refuse(connection, Refusal.BadHandshake);
            }

            return;
        }

        if (connection.State == ConnectionState.AwaitingProof)
        {
            Span<byte> proof = stackalloc byte[Passwords.ProofSize];
            var answer = new NetReader(payload.IsEmpty ? payload : payload[1..]);
            if (!payload.IsEmpty && payload[0] == (byte)MessageId.Proof && channel == NetChannel.Reliable && ProofMessage.TryRead(ref answer, proof))
                CheckProof(connection, proof);
            else
                Refuse(connection, Refusal.BadHandshake);
            return;
        }

        // Reliable commands have their own allowance so a hitch in input cannot consume it. They execute in
        // arrival order; an overflow closes the connection rather than silently losing a one-shot command.
        if (!transport.IsInProcess)
        {
            if (channel == NetChannel.Reliable)
            {
                connection.ReliableMessages++;
                connection.ReliableBytes += payload.Length;
                if (connection.ReliableMessages > PlayerMessagesPerTick || connection.ReliableBytes > PlayerBytesPerTick)
                {
                    RemoveFlooder(connection);
                    return;
                }
            }
            else
            {
                connection.TickMessages++;
                connection.TickBytes += payload.Length;
                if (connection.TickMessages > PlayerMessagesPerTick || connection.TickBytes > PlayerBytesPerTick)
                {
                    connection.OverAllowance = true;
                    return;
                }
            }
        }

        if (payload.IsEmpty)
            return;

        var reader = new NetReader(payload[1..]);
        if (!connection.CatalogueReady)
        {
            if (payload[0] == (byte)MessageId.DesignRequest && channel == NetChannel.Unreliable && payload.Length == 2)
            {
                var id = payload[1];
                if (id == 255)
                {
                    // The manifest and the designs it lists never change once made, so what this client is
                    // sent stays whole whatever is added meanwhile. What is added later it is told afterwards.
                    if (connection.Blocks!.QueueDesign(id, _catalogue.Manifest, _tick))
                        connection.ManifestDesigns = _catalogue.Count;
                }
                else if (id < _catalogue.Bytes.Length) connection.Blocks!.QueueDesign(id, _catalogue.Bytes[id], _tick);
            }
            else if (payload[0] == (byte)MessageId.CatalogueReady && channel == NetChannel.Reliable && payload.Length == 1)
            {
                // A client that never asked for the manifest can know no more than the designs the server started with.
                if (connection.ManifestDesigns == 0)
                    connection.ManifestDesigns = _startupDesigns;
                BlockSet.Fill(connection.ReadyDesigns, connection.ManifestDesigns);
                connection.NoticedDesigns = connection.ManifestDesigns;
                connection.CatalogueReady = true;
                SendRockDeltas(connection, _rocks.Deltas);
                JoinSocial(connection);
            }
            return;
        }
        switch ((MessageId)payload[0])
        {
            case MessageId.BlockAck when channel == NetChannel.Unreliable:
                connection.Blocks!.Receive(ref reader, _hulls, _tick);
                break;
            case MessageId.Social when channel == NetChannel.Reliable:
                ReceiveSocial(connection, ref reader);
                break;
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
                var hullId = reader.ReadUInt32();
                if (reader.IsValid)
                    TakeControl(connection, hullId);
                break;
            case MessageId.FleetOrders when channel == NetChannel.Reliable:
                ReceiveFleetOrders(connection, ref reader);
                break;
            case MessageId.Editor when channel == NetChannel.Reliable:
                ReceiveEditor(connection, ref reader);
                break;
            case MessageId.EditorData when channel == NetChannel.Bulk:
                ReceiveEditorData(connection, ref reader);
                break;
            case MessageId.DesignReady when channel == NetChannel.Reliable:
                ReceiveDesignReady(connection, ref reader);
                break;
            case MessageId.DesignRequest when channel == NetChannel.Unreliable && payload.Length == 2:
                {
                    // Only a design added since this client's manifest, announced to it and not yet shown by it.
                    var id = payload[1];
                    if (id >= connection.ManifestDesigns && id < connection.NoticedDesigns && !BlockSet.Has(connection.ReadyDesigns, id))
                        connection.Blocks!.QueueDesign(id, _catalogue.Bytes[id], _tick, repeat: true);
                    break;
                }
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
            if (currentInfo.EscapeDrone)
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

            // Stepping out does not leave the damage behind: the drone has been as quiet as the hull it left.
            _hulls.Info(next.Index).DamageTick = currentInfo.DamageTick;
            _hulls.Info(next.Index).EscapeDrone = true;
        }
        else
        {
            next = new HullHandle((int)(hullId & 0xFFFF), (int)(hullId >> 16));
            if (next == current || !_hulls.IsAlive(next) || _hulls.Info(next.Index).Owner != connection.PlayerId)
                return;

            // Check before mutation so an out-of-sector refusal also preserves an escape drone.
            if (!Galaxy.InSameSector(_hulls.State(current.Index).Position, _hulls.State(next.Index).Position))
                return;
        }

        if (currentInfo.EscapeDrone)
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
                HullSim.SetOrder(ref _hulls.State(handle.Index), ref info.Order, orders[i].Order);
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
                HullSim.SetOrder(ref _hulls.State(index), ref info.Order, connection.Order);
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
        var wasDrone = _hulls.Info(lost.Index).EscapeDrone;
        var state = _hulls.State(lost.Index);
        _hulls.Destroy(lost);

        var drone = (byte)_content.DroneDesign;
        connection.Hull = _hulls.Create(connection.PlayerId, drone, _content.Designs[drone], wasDrone ? SpawnPoint : state.Position);
        ref var info = ref _hulls.Info(connection.Hull.Index);
        info.EscapeDrone = true;
        info.Piloted = true;
        info.OrderId = connection.OrderId;

        // The drone comes out of a hull that was just destroyed: its time without damage starts now.
        info.DamageTick = _tick;
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
        connection.Blocks!.Send(connection.Transport, connection.Peer, _sendBuffer, tick);
        if (!connection.CatalogueReady) return;
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

            // A hull of a design this client cannot show yet is left out until the client says it can.
            ref var info = ref _hulls.Info(i);
            if (!BlockSet.Has(connection.ReadyDesigns, info.Design))
                continue;
            var record = new HullRecord
            {
                Id = _hulls.HandleAt(i).Packed,
                Owner = info.Owner,
                Design = info.Design,
                Revision = _hulls.Revision(i),
                State = _hulls.State(i),
                Order = info.Order,
            };
            record.LossStart = Math.Min(record.Revision, connection.Blocks.Acknowledged(record.Id));
            var losses = _hulls.BlockStates.Losses(info.BlockState);
            record.LossCount = (byte)Math.Min(HullRecord.MaxLosses, record.Revision - record.LossStart);
            for (var loss = 0; loss < record.LossCount; loss++) record.Losses[loss] = losses[(int)record.LossStart + loss];
            record.Write(ref writer);
            count++;
        }

        var length = writer.Length;
        header.HullCount = (byte)count;
        var rewrite = new NetWriter(_sendBuffer);
        header.Write(ref rewrite);
        connection.Transport.Send(connection.Peer, NetChannel.Unreliable, _sendBuffer.AsSpan(0, length));
    }

    /// <summary>
    /// Answers a hello. Without a password, or on the in-process link of the host's own client, the player is
    /// let in. Otherwise the answer is a challenge, unless the address has sent too many wrong passwords.
    ///
    /// The checks run in a fixed order: the version, a free player slot, the lock-out, the name, the content.
    /// Everything that can turn the connection away without the name is checked before the name is read, and the
    /// name is kept in the connection's own buffer: a hello that is refused costs the server no memory, however
    /// many arrive. Content is compared last, and before any challenge: a client whose content differs could not
    /// play here with the right password either, so it is told so without being asked for one. The host's own
    /// client goes through the same comparison, which it passes because it was given the same content.
    /// </summary>
    private void Greet(Connection connection, ref NetReader hello)
    {
        if (!HelloMessage.TryReadVersion(ref hello, out var version))
        {
            Refuse(connection, Refusal.BadHandshake);
            return;
        }

        if (version != ProtocolInfo.Version)
        {
            Refuse(connection, Refusal.VersionMismatch);
            return;
        }

        if (_playerCount >= _maxPlayers)
        {
            Refuse(connection, Refusal.ServerFull);
            return;
        }

        var challenged = _passwordKey is not null && !connection.Transport.IsInProcess;
        if (challenged)
        {
            var wait = _attempts.SecondsBlocked(connection.Origin, _clock.GetTimestamp());
            if (wait > 0)
            {
                Refuse(connection, Refusal.LockedOut, wait);
                return;
            }
        }

        if (!HelloMessage.TryReadName(ref hello, connection.NameChars, out connection.NameLength))
        {
            Refuse(connection, Refusal.BadHandshake);
            return;
        }

        if (!HelloMessage.TryReadContent(ref hello, out var content))
        {
            Refuse(connection, Refusal.BadHandshake);
            return;
        }

        if (content != _content.Identity)
        {
            Refuse(connection, Refusal.ContentMismatch);
            return;
        }

        if (!challenged)
        {
            Admit(connection);
            return;
        }

        // Fresh random bytes for this connection alone: a proof overheard on another one answers a different challenge.
        RandomNumberGenerator.Fill(connection.Challenge);
        connection.State = ConnectionState.AwaitingProof;
        var writer = new NetWriter(_sendBuffer);
        ChallengeMessage.Write(ref writer, _salt, connection.Challenge);
        connection.Transport.Send(connection.Peer, NetChannel.Reliable, writer.Written);
    }

    /// <summary>
    /// Judges the proof a challenged connection sent. A connection gets one try: right lets the player in, wrong
    /// is counted against the address and the connection is closed.
    /// </summary>
    private void CheckProof(Connection connection, ReadOnlySpan<byte> proof)
    {
        // Asked again here: several connections from one address may have been challenged before the first failed.
        var now = _clock.GetTimestamp();
        var wait = _attempts.SecondsBlocked(connection.Origin, now);
        if (wait > 0)
        {
            Refuse(connection, Refusal.LockedOut, wait);
            return;
        }

        if (!Passwords.Verify(_passwordKey, _salt, connection.Challenge, proof))
        {
            _attempts.RecordFailure(connection.Origin, now);
            Refuse(connection, Refusal.WrongPassword);
            return;
        }

        // The slot may have gone to someone else while this player was proving themselves.
        if (_playerCount >= _maxPlayers)
        {
            Refuse(connection, Refusal.ServerFull);
            return;
        }

        Admit(connection);
    }

    /// <summary>Lets a player in: gives them their fleet and tells them about the world.</summary>
    private void Admit(Connection connection)
    {
        Array.Clear(connection.Challenge);
        connection.State = ConnectionState.Active;
        connection.Blocks = new BlockSender(_content.BlockTransfers);
        connection.CatalogueDeadline = _tick + _content.BlockTransfers.AdmissionTicks;

        connection.Name = new string(connection.NameChars, 0, connection.NameLength);
        connection.PlayerId = _nextPlayerId++;
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
            GalaxySeed = _galaxySeed,
            ServerName = _name,
            HullId = connection.Hull.Packed,
            Settings = _content.Settings,
            Visibility = _visibility,
            PasswordProtected = _passwordKey is not null,
            MaxPlayers = (ushort)_maxPlayers,
        };
        var writer = new NetWriter(_sendBuffer);
        welcome.Write(ref writer, _content.SettingCatalog);
        connection.Transport.Send(connection.Peer, NetChannel.Reliable, writer.Written);

        // The newcomer generates the galaxy from the seed like everyone else; what it cannot know is which
        // asteroids have been knocked about since.


        Logs.Sim.Info($"{connection.Name} joined as player {connection.PlayerId} via {connection.Transport.Description}");
    }

    /// <summary>Ends a connection that is shaking hands, telling the other end why, and frees its slot.</summary>
    private void Refuse(Connection connection, Refusal why, int retryAfterSeconds = 0)
    {
        TurnAway(connection.Transport, connection.Peer, why, retryAfterSeconds);
        connection.Reset();
    }

    /// <summary>
    /// Tells a peer it is not let in and closes its connection. Nothing here allocates and nothing is written to
    /// the log: refusals are counted and reported together by <see cref="ReportRefusals"/>, so a flood of
    /// connections costs the tick no memory and the log one line every few seconds.
    /// </summary>
    private void TurnAway(ITransport transport, int peer, Refusal why, int retryAfterSeconds)
    {
        var reason = why switch
        {
            Refusal.VersionMismatch => RejectReason.VersionMismatch,
            Refusal.ServerFull => RejectReason.ServerFull,
            Refusal.WrongPassword => RejectReason.WrongPassword,
            Refusal.ContentMismatch => RejectReason.ContentMismatch,
            Refusal.LockedOut or Refusal.TooFast or Refusal.AddressBusy or Refusal.DoorFull => RejectReason.TooManyAttempts,
            _ => RejectReason.BadHandshake,
        };
        var writer = new NetWriter(_sendBuffer);
        new RejectMessage { Reason = reason, RetryAfterSeconds = (ushort)Math.Clamp(retryAfterSeconds, 0, ushort.MaxValue) }.Write(ref writer);
        transport.Send(peer, NetChannel.Reliable, writer.Written);
        transport.Disconnect(peer);

        _refused[(int)why]++;
        _refusedCount++;
    }

    /// <summary>
    /// Writes what was refused since the last time, in one line, at most once every
    /// <see cref="RefusalLogSeconds"/> seconds. The first refusal after a quiet spell is reported in the tick it
    /// happens in; during a flood the lines are that far apart and say how many there were.
    /// </summary>
    private void ReportRefusals(long now)
    {
        if ((_refusedCount == 0 && _budgetTicks == 0) || now < _nextRefusalLog)
            return;

        _nextRefusalLog = now + RefusalLogSeconds * _clock.TimestampFrequency;
        if (_refusedCount > 0)
        {
            Logs.Sim.Info(
                $"Refused {_refusedCount} connection(s): "
                    + $"{_refused[(int)Refusal.TooFast]} too many from one address, "
                    + $"{_refused[(int)Refusal.AddressBusy]} over an address's share of the door, "
                    + $"{_refused[(int)Refusal.DoorFull]} with the door full, "
                    + $"{_refused[(int)Refusal.TimedOut]} handshake timed out, "
                    + $"{_refused[(int)Refusal.BadHandshake]} bad handshake, "
                    + $"{_refused[(int)Refusal.WrongPassword]} wrong password, "
                    + $"{_refused[(int)Refusal.LockedOut]} locked out, "
                    + $"{_refused[(int)Refusal.VersionMismatch]} other version, "
                    + $"{_refused[(int)Refusal.ContentMismatch]} other content, "
                    + $"{_refused[(int)Refusal.ServerFull]} server full"
            );
        }

        if (_budgetTicks > 0)
            Logs.Sim.Warning($"Network input filled its budget in {_budgetTicks} tick(s); the rest was read a tick later");

        Array.Clear(_refused);
        _refusedCount = 0;
        _budgetTicks = 0;
    }

    /// <summary>Removes a player who keeps sending far more than a client does.</summary>
    private void RemoveFlooder(Connection connection)
    {
        Logs.Sim.Warning($"{connection.Name} (player {connection.PlayerId}) removed: sending far more than a client does");
        connection.Transport.Disconnect(connection.Peer);
        RemovePlayer(connection);
        connection.Reset();
    }

    /// <summary>Takes a player's hulls out of the world and frees their player slot.</summary>
    private void RemovePlayer(Connection connection)
    {
        CloseEdit(connection);
        LeaveSocial(connection);
        for (var i = 0; i < _hulls.Slots; i++)
        {
            if (_hulls.IsAliveAt(i) && _hulls.Info(i).Owner == connection.PlayerId)
                _hulls.Destroy(_hulls.HandleAt(i));
        }

        Volatile.Write(ref _playerCount, _playerCount - 1);
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
